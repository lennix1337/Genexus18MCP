using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using GxMcp.Worker.Helpers;
using Microsoft.Build.Framework;

namespace GxMcp.Worker.Services
{
    // Outcome of the in-process build attempt. Distinguishes "the GeneXus
    // pipeline actually ran and failed" (diagnostics were produced; a full
    // external MSBuild.exe rebuild would only reproduce them, doubling
    // wall-clock — the "build never returns" the user reported) from "the
    // in-process path could not even start" (SDK types missing, null handle,
    // unsupported action, or an exception before any section executed), where
    // falling back to MSBuild.exe is the honest thing to do.
    internal enum InProcessBuildOutcome
    {
        Succeeded,
        FailedWithDiagnostics,
        CouldNotRun
    }

    // v2.6.6 Stream D — orchestrates in-process invocation of the two GeneXus
    // MSBuild tasks the IDE pipeline relies on:
    //   - Genexus.MsBuild.Tasks.SpecifyOneOnly   (when Build/RebuildAll has targets)
    //   - Genexus.MsBuild.Tasks.IdeWebBuildAndDeploy
    //
    // The worker already holds the KB open through KbService._kb; this runner
    // shares that instance via the task's public `KB` property instead of
    // spawning MSBuild.exe and re-opening the KB out-of-process. Expected
    // speedup: 5-10 min → 5-30 s for targeted builds.
    internal static class InProcessBuildRunner
    {
        // Type cache — Assembly.LoadFrom is amortised across calls.
        private static Type _typeSpecifyOneOnly;
        private static Type _typeIdeWebBuildAndDeploy;
        private static Type _typeBuildOne;
        private static Type _typeBuildAll;
        private static bool _assemblyLoadAttempted;

        // Compile-only fast-fast path (env: GXMCP_BUILD_COMPILE_ONLY=1).
        // Bypasses BuildOne's hardcoded BuildOptions.BuildProcess (Specify|Generate|Compile,
        // ~25s on a warm worker for our reference KB) and invokes
        // GenexusBLServices.Build.Build(workingSet, BuildOptions.Compile|ContinueOnError, keys, token)
        // directly via reflection. Requires the .cs to already be current for the target
        // (i.e. nothing changed since the last Specify/Generate). If the SDK errors
        // because the .cs is stale, we fall back to the regular BuildOne path.
        private static Type _typeObjectNameHelper;
        private static Type _typeDevelopmentWorkingSet;
        private static Type _typeBuildOptions;
        private static Type _typeGenexusBLServices;
        private static System.Reflection.MethodInfo _miBuildBuild; // Build(workingSet, BuildOptions, IEnumerable<EntityKey>, CancellationToken)
        private static System.Reflection.MethodInfo _miBuildWithTheseOnly; // BuildWithTheseOnly(workingSet, IEnumerable<EntityKey>, CancellationToken)
        // 2026-05-22: Build.Build(...) is a dead-end because the internal BuildProcess does
        // `options = 0x3800 | options` — Spec+Gen+Compile are forced regardless of caller flags.
        // The IDE's true "Compile only" path is IRunService.Compile(KBModel, EntityKey), which
        // the MSBuild Compile task wraps. Reflect through GenexusBLServices.Run.Compile(model, key).
        private static System.Reflection.MethodInfo _miRunCompile; // Compile(KBModel, EntityKey) -> bool
        private static readonly object _typeCacheLock = new object();

        // Public entry. Returns Succeeded when the caller should write the
        // final status from the captured diagnostics; FailedWithDiagnostics
        // when the pipeline ran but failed (caller terminalizes Failed — NO
        // external fallback); CouldNotRun when the in-process path never
        // started (caller falls back to the external MSBuild.exe spawn).
        // Never throws.
        public static InProcessBuildOutcome Run(
            BuildService.BuildTaskStatus status,
            string action,
            List<string> targets,
            Action<BuildService.BuildTaskStatus, string, bool> sink,
            object kbHandle,
            object kbLock,
            bool skipFullDeploy = false,
            string kbPath = null,
            bool specifyOnly = false,
            bool fullDeploy = false)
        {
            if (status == null) return InProcessBuildOutcome.CouldNotRun;
            if (kbHandle == null)
            {
                Logger.Warn("[BUILD-INPROCESS] kb handle is null — skipping in-process path");
                return InProcessBuildOutcome.CouldNotRun;
            }
            if (kbLock == null)
            {
                Logger.Warn("[BUILD-INPROCESS] kb lock is null — skipping in-process path");
                return InProcessBuildOutcome.CouldNotRun;
            }

            // Stream D follow-up: Build/RebuildAll are wired through the
            // in-process task pair (SpecifyOneOnly + IdeWebBuildAndDeploy), while
            // BuildAll uses the SDK's dedicated BuildAll task. Other
            // actions need distinct tasks the external-msbuild template owns
            // (Reorg → CheckAndInstallDatabase, Validate/Check → CheckKnowledgeBase,
            // Sync → full IdeWebBuildAndDeploy). Refuse here so RunBuild's
            // unchanged fallback runs them through MSBuild.exe.
            if (!string.IsNullOrEmpty(action)
                && !action.Equals("Build", StringComparison.OrdinalIgnoreCase)
                && !action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase)
                && !action.Equals("BuildAll", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("[BUILD-INPROCESS] action='" + action + "' not supported in-process — falling back to MSBuild.exe");
                return InProcessBuildOutcome.CouldNotRun;
            }

            try
            {
                if (!EnsureTypesLoaded())
                {
                    return InProcessBuildOutcome.CouldNotRun;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BUILD-INPROCESS] EnsureTypesLoaded threw: " + ex.Message);
                return InProcessBuildOutcome.CouldNotRun;
            }

            // Wrap the BuildService sink so the engine sees a (line,isError) action
            // and HandleLine still receives the status object.
            Action<string, bool> lineSink = (l, e) => { try { sink(status, l, e); } catch { } };
            var engine = new InProcessBuildEngine(lineSink);

            lock (kbLock)
            {
                try
                {
                    bool isBuildWithTargets = action != null
                        && (action.Equals("Build", StringComparison.OrdinalIgnoreCase)
                            || action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase))
                        && targets != null
                        && targets.Count > 0;
                    bool forceRebuild = action != null && action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase);

                    if (action != null && action.Equals("BuildAll", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_typeBuildAll == null)
                        {
                            Logger.Warn("[BUILD-INPROCESS] BuildAll task type is unavailable — falling back to MSBuild.exe");
                            return InProcessBuildOutcome.CouldNotRun;
                        }

                        lineSink("[GXMCP-BUILD-ALL] KB opened", false);
                        lineSink("[GXMCP-BUILD-ALL] BuildAll started", false);
                        bool buildAllOk = ExecuteBuildAll(kbHandle, engine, lineSink);
                        if (buildAllOk)
                            lineSink("[GXMCP-BUILD-ALL] BuildAll completed", false);
                        return buildAllOk
                            ? InProcessBuildOutcome.Succeeded
                            : InProcessBuildOutcome.FailedWithDiagnostics;
                    }

                    // issue #28 item 12: spec-check only. Run SpecifyOneOnly (Spec+Gen) for the
                    // target(s) and stop — no Compile, no IdeWebBuildAndDeploy. Surfaces spc*/gen*
                    // diagnostics via the engine sink (→ status errors, split by #13) without the
                    // full build's compile+deploy cost. Reuses the proven ExecuteSpecifyOneOnly path.
                    if (specifyOnly && isBuildWithTargets)
                    {
                        lineSink("[BUILD-INPROCESS] specifyOnly=true — running SpecifyOneOnly (Spec+Gen) only; Compile and deploy are SKIPPED. Use this for a fast spec check, not to produce runnable output.", false);
                        engine.ResetSectionFlags();
                        bool specOk = ExecuteSpecifyOneOnly(kbHandle, targets, engine);
                        // Success here means "spec pass ran"; spec errors (if any) were emitted to
                        // the sink and counted. Report Succeeded so the caller surfaces the spec
                        // result rather than falling back to a full MSBuild.exe spawn. If the spec
                        // pass produced nothing at all, CouldNotRun lets RunBuild's specifyOnly
                        // guard report "spec-check unavailable" (it still never runs a full build).
                        return (specOk || status.ErrorCount > 0)
                            ? InProcessBuildOutcome.Succeeded
                            : InProcessBuildOutcome.CouldNotRun;
                    }

                    // Friction 2026-05-22 fast path: use BuildOne — the same task the
                    // GeneXus IDE F5 invokes for "Build this object only". It does
                    // spec + gen + compile + targeted deploy of the changed object
                    // and skips the full IdeWebBuildAndDeploy (which copies every
                    // module + runs WebAppConfig and is the ~6.5min bottleneck).
                    //
                    // Path enabled by default for action=Build with explicit targets.
                    // RebuildAll with explicit targets stays on the legacy task pair
                    // below so SpecifyOneOnly scopes the force rebuild; targetless
                    // RebuildAll still runs against the whole KB. Opt-out with
                    // GXMCP_INPROCESS_BUILD_FASTPATH=0.
                    // A2: fullDeploy=true forces the legacy IdeWebBuildAndDeploy path
                    // (module/theme copy → web/bin + WebAppConfig) so a targeted build
                    // produces runnable output, instead of BuildOne's compile-only fast path.
                    bool useBuildOne =
                        isBuildWithTargets
                        && !forceRebuild
                        && !fullDeploy
                        && _typeBuildOne != null
                        && !string.Equals(Environment.GetEnvironmentVariable("GXMCP_INPROCESS_BUILD_FASTPATH"), "0", StringComparison.OrdinalIgnoreCase);

                    // Compile-only fast-fast path (experimental).
                    // GXMCP_BUILD_COMPILE_ONLY=1 → call GenexusBLServices.Build.Build with only
                    // BuildOptions.Compile, skipping Specify (~16s) + Generate (~9s). Requires the
                    // .cs to already be current — falls back to full BuildOne on any failure.
                    bool tryCompileOnly =
                        isBuildWithTargets && !forceRebuild
                        && string.Equals(Environment.GetEnvironmentVariable("GXMCP_BUILD_COMPILE_ONLY"), "1", StringComparison.Ordinal)
                        && _miRunCompile != null;
                    if (tryCompileOnly)
                    {
                        lineSink("[BUILD-INPROCESS] fast-fast path (experimental): Compile-only x" + targets.Count, false);
                        bool allOk = true;
                        foreach (var t in targets)
                        {
                            if (!ExecuteCompileOnly(kbHandle, t, lineSink))
                            {
                                lineSink("[BUILD-INPROCESS] compile-only failed for '" + t + "' — falling back to BuildOne", false);
                                allOk = false;
                                break;
                            }
                        }
                        if (allOk) return InProcessBuildOutcome.Succeeded;
                        // Reset engine flags before the BuildOne fallback so leftover state
                        // from this attempt doesn't contaminate the BuildOne partial-success check.
                        engine.ResetSectionFlags();
                    }

                    if (useBuildOne)
                    {
                        lineSink("[BUILD-INPROCESS] fast path: BuildOne x" + targets.Count + " — Theme/Image/Style/Module deploy steps are SKIPPED. If your change touches theme CSS, generated JS, or shared module DLLs and they don't appear in web/, set GXMCP_INPROCESS_BUILD_FASTPATH=0 to use the full deploy.", false);

                        // Bug #3: BuildOne/BuildBatch silently no-op (return true) on an object
                        // name that doesn't resolve in the DesignModel, so a build could report
                        // "succeeded, 0 objects" and leave the .dll stale (e.g. a WorkWith-pattern
                        // panel whose object name differs from the name passed). Pre-resolve the
                        // targets: fail loud when NONE resolve, warn when some are skipped.
                        int resolvedCount = CountResolvableTargets(kbHandle, targets, out var unresolvedNames);
                        if (resolvedCount == 0)
                        {
                            lineSink("[BUILD-INPROCESS] none of the " + targets.Count + " requested target(s) resolved to a KBObject: "
                                + string.Join(", ", unresolvedNames)
                                + ". Nothing was built — the .dll was NOT refreshed. Check the object name/type; pattern-generated objects (WorkWith panels, etc.) may have a different object name than the pattern instance.", true);
                            return InProcessBuildOutcome.FailedWithDiagnostics;
                        }
                        if (unresolvedNames != null && unresolvedNames.Count > 0)
                            lineSink("[BUILD-INPROCESS] warning: " + unresolvedNames.Count + " requested target(s) not found and skipped (their .dll will NOT change): "
                                + string.Join(", ", unresolvedNames), false);

                        // 2026-05-22: Multi-target batch path (Option D, "BuildBatch").
                        // BuildOne is a thin wrapper around
                        //   GenexusBLServices.Build.Build(workingSet, BuildOptions, IEnumerable<EntityKey>, token)
                        // (4-arg bool overload). That interface natively accepts N keys and
                        // does shared spec/gen/compile setup. For >1 target we resolve all
                        // EntityKeys up-front and invoke the BL once instead of looping
                        // BuildOne.Execute N times. The kbLock still serializes the call,
                        // but the inner SDK shares one Specify pass, one Generate pass, one
                        // Compile sweep, and one deploy hook — wall-clock for 2 targets goes
                        // from ~68s (2×34s) to one shared pipeline.
                        //
                        // Single target still uses ExecuteBuildOne for IDE-F5 parity (lower
                        // risk: identical to the path that's been live).
                        //
                        // 2026-05-22: BuildBatch is OPT-IN (GXMCP_INPROCESS_BUILD_BATCH=1).
                        // Live-test 16:34 found that the BL.Build N-key call on the reference
                        // KB throws Win32Exception "arquivo não encontrado" at the late
                        // WebAppConfig deploy step — same root cause as the IdeWebBuildAndDeploy
                        // failure mode we already work around with InProcessBuildEngine's
                        // section markers + partial-success branch. BuildBatch bypasses
                        // InProcessBuildEngine entirely (no engine subscription on Build.Build),
                        // so the partial-success check can't help; worse, the failure leaves
                        // the SDK pipeline in a broken state that propagates to the
                        // subsequent ExecuteCompileOnly attempt in the per-target loop.
                        // Per-target BuildOne (with engine.CompileSucceeded markers) remains
                        // the safe default. Re-enable per-call for KBs where WebAppConfig
                        // works cleanly.
                        // Issue #96: Multi-target batched BuildWithTheseOnly when includeCallees=none.
                        // When includeCallees=none and there are >1 targets, instead of running N sequential
                        // BuildOne builds, issue a single IBuildServiceBL.BuildWithTheseOnly call with
                        // all EntityKeys. This amortizes module copying and DeveloperMenu regeneration, running
                        // a single shared specification & MSBuild compilation step.
                        bool isIncludeCalleesNone = string.Equals(status.BuildPlan?.IncludeCallees, "none", StringComparison.OrdinalIgnoreCase);
                        if (targets.Count > 1 && isIncludeCalleesNone && _miBuildWithTheseOnly != null)
                        {
                            engine.ResetSectionFlags();
                            var withTheseOnlyResult = ExecuteBuildWithTheseOnly(kbHandle, targets, lineSink);
                            if (withTheseOnlyResult == BatchOutcome.Success)
                            {
                                lineSink("[BUILD-INPROCESS] batch BuildWithTheseOnly x" + targets.Count + " — OK (shared spec/gen/compile pipeline).", false);
                                foreach (var t in targets) EditDirtyTracker.MarkClean(kbPath, t);
                                return status.ErrorCount == 0
                                    ? InProcessBuildOutcome.Succeeded
                                    : InProcessBuildOutcome.FailedWithDiagnostics;
                            }
                            if (withTheseOnlyResult == BatchOutcome.NotApplicable)
                            {
                                lineSink("[BUILD-INPROCESS] batch BuildWithTheseOnly unavailable — falling through to per-target BuildOne.", false);
                            }
                            else
                            {
                                lineSink("[BUILD-INPROCESS] batch BuildWithTheseOnly failed — falling back to per-target BuildOne loop.", false);
                                engine.ResetSectionFlags();
                            }
                        }

                        bool tryBatch = targets.Count > 1
                            && _miBuildBuild != null
                            && _typeDevelopmentWorkingSet != null
                            && _typeBuildOptions != null
                            && _typeObjectNameHelper != null
                            && string.Equals(Environment.GetEnvironmentVariable("GXMCP_INPROCESS_BUILD_BATCH"), "1", StringComparison.OrdinalIgnoreCase);
                        // v2.6.9 — if EVERY target in the batch is "clean" (no MCP edit since
                        // last successful BuildOne), the .cs files are all current. Skip the
                        // shared spec+gen batch and run Run.Compile per target — same fast-fast
                        // path the per-target loop uses below, but applied to all of them.
                        bool allClean = _miRunCompile != null && targets.All(t => !EditDirtyTracker.IsDirty(kbPath, t));
                        if (tryBatch && allClean)
                        {
                            lineSink("[BUILD-INPROCESS] all " + targets.Count + " targets clean — using compile-only fast-fast path for the whole batch.", false);
                            bool allOk = true;
                            foreach (var t in targets)
                            {
                                if (!ExecuteCompileOnly(kbHandle, t, lineSink))
                                {
                                    lineSink("[BUILD-INPROCESS] compile-only failed for clean target '" + t + "' — falling through to BuildBatch / per-target.", false);
                                    allOk = false;
                                    break;
                                }
                            }
                            if (allOk)
                            {
                                foreach (var t in targets) EditDirtyTracker.MarkClean(kbPath, t);
                                return InProcessBuildOutcome.Succeeded;
                            }
                            engine.ResetSectionFlags();
                        }

                        if (tryBatch)
                        {
                            engine.ResetSectionFlags();
                            var batchResult = ExecuteBuildBatch(kbHandle, targets, lineSink);
                            if (batchResult == BatchOutcome.Success)
                            {
                                lineSink("[BUILD-INPROCESS] batch BuildBatch x" + targets.Count + " — OK (shared spec/gen/compile pipeline).", false);
                                foreach (var t in targets) EditDirtyTracker.MarkClean(kbPath, t);
                                return InProcessBuildOutcome.Succeeded;
                            }
                            if (batchResult == BatchOutcome.NotApplicable)
                            {
                                // Type/key resolution failed up front — fall through to the
                                // existing per-target loop.
                                lineSink("[BUILD-INPROCESS] batch path unavailable — falling through to per-target BuildOne.", false);
                            }
                            else
                            {
                                // BL.Build returned false. Fall through to per-target loop so
                                // partial-success per-target heuristics still apply (some targets
                                // may succeed, late-stage WebAppConfig failure is non-blocking).
                                lineSink("[BUILD-INPROCESS] batch BuildBatch returned false — falling back to per-target BuildOne loop.", false);
                                engine.ResetSectionFlags();
                            }
                        }

                        foreach (var t in targets)
                        {
                            // R1: clear sticky section flags from any previous target in
                            // this loop. Otherwise target N's CompileSucceeded /
                            // WebAppConfigStarted leak into target N+1's partial-success
                            // decision.
                            engine.ResetSectionFlags();

                            // v2.6.9 — per-target fast-fast path. If the dirty tracker has
                            // an explicit "clean" record (= built successfully since the
                            // last MCP edit), skip Specify+Generate and call Run.Compile
                            // directly. On any compile-only failure, fall back to BuildOne
                            // for this target (which regenerates the .cs).
                            if (_miRunCompile != null && !EditDirtyTracker.IsDirty(kbPath, t))
                            {
                                lineSink("[BUILD-INPROCESS] '" + t + "' is clean — compile-only fast-fast path.", false);
                                if (ExecuteCompileOnly(kbHandle, t, lineSink))
                                {
                                    EditDirtyTracker.MarkClean(kbPath, t);
                                    continue;
                                }
                                lineSink("[BUILD-INPROCESS] compile-only failed for '" + t + "' — falling back to full BuildOne.", false);
                                engine.ResetSectionFlags();
                            }

                            if (!ExecuteBuildOne(kbHandle, t, engine, buildCalled: false))
                            {
                                // Friction 2026-05-22: BuildOne.Execute may return false even
                                // when the C# compile already succeeded. The GeneXus build
                                // pipeline emits ">E1...Compilation" when the .dll is written,
                                // then proceeds to WebAppConfig (web.config update) which
                                // often fails on dev KBs without IIS plumbing — the
                                // late-stage failure is non-blocking for serving the object.
                                // If the engine trace shows compile OK + the only failed
                                // stage was post-compile, treat as PartialSuccess and skip
                                // the costly MSBuild.exe fallback. Net wall-clock drops from
                                // ~6min to ~56s for single-target builds.
                                if (engine.CompileSucceeded && engine.WebAppConfigStarted)
                                {
                                    lineSink("[BUILD-INPROCESS] BuildOne late-stage failure after compile OK — accepting as PARTIAL SUCCESS (DLL written, web.config step skipped).", false);
                                    Logger.Info("[BUILD-INPROCESS] PartialSuccess: compile OK for '" + t + "'; web.config / deploy step failed but DLL is in place.");
                                    EditDirtyTracker.MarkClean(kbPath, t); // v2.6.9 — DLL written so .cs is current
                                    continue;
                                }
                                Logger.Warn("[BUILD-INPROCESS] BuildOne returned false for '" + t + "' — pipeline ran, terminalizing (no external fallback)");
                                // Dump the engine's captured trace so we can see WHY the
                                // task failed (errorless silent failure would otherwise be invisible).
                                try
                                {
                                    string trace = engine.DrainTrace();
                                    foreach (var ln in trace.Split('\n'))
                                        Logger.Info("[BUILD-INPROCESS] TRACE: " + ln);
                                }
                                catch { }
                                // The GeneXus BuildOne pipeline ran (spec/gen/compile) and
                                // failed. A full MSBuild.exe rebuild would only reproduce the
                                // same result at 5-13× the wall-clock, so we surface the
                                // captured diagnostics and terminalize instead of falling back.
                                return InProcessBuildOutcome.FailedWithDiagnostics;
                            }
                            // v2.6.9 — BuildOne returned true; .cs and .dll are current for this target.
                            EditDirtyTracker.MarkClean(kbPath, t);
                        }
                        return InProcessBuildOutcome.Succeeded;
                    }

                    // Legacy path (targeted RebuildAll, opt-out, or empty targets):
                    // SpecifyOneOnly + IdeWebBuildAndDeploy.
                    if (isBuildWithTargets)
                    {
                        if (!ExecuteSpecifyOneOnly(kbHandle, targets, engine))
                        {
                            // Spec pass ran and returned false → spc/gen diagnostics are
                            // already on the engine sink. Terminalize with them rather
                            // than re-running the whole thing under MSBuild.exe.
                            return InProcessBuildOutcome.FailedWithDiagnostics;
                        }
                    }

                    // Friction 2026-05-22 (experimental): when the caller passes
                    // skipFullDeploy=true on a single-target Build with no callees,
                    // we stop after SpecifyOneOnly. Spec+Gen wrote the .cs sources
                    // and the next IDE Build All (or a normal lifecycle build)
                    // picks the DLL up. This skips Copying Module GeneXus / GAM /
                    // Crypto and the WebAppConfig step — turning 5-13min into ~30s.
                    // EXPERIMENTAL: validate live against the runtime before
                    // making this the default for includeCallees=none.
                    if (skipFullDeploy && isBuildWithTargets && !forceRebuild)
                    {
                        lineSink("[BUILD-INPROCESS] skipFullDeploy=true — stopping after SpecifyOneOnly. DLL output is NOT redeployed; the IDE build task is bypassed.", false);
                        return InProcessBuildOutcome.Succeeded;
                    }

                    if (!ExecuteIdeWebBuildAndDeploy(kbHandle, engine, forceRebuild))
                    {
                        // The IDE build+deploy pipeline ran end-to-end and failed
                        // (the build-all path). Its diagnostics — or, for an
                        // itemless failure, the >E0 section marker parsed by
                        // HandleLine — are already on the status. Terminalize with
                        // them; do NOT re-run the whole build under MSBuild.exe.
                        return InProcessBuildOutcome.FailedWithDiagnostics;
                    }

                    return InProcessBuildOutcome.Succeeded;
                }
                catch (Exception ex)
                {
                    // An exception before/within the pipeline setup means the
                    // in-process path is not trustworthy — let RunBuild fall back
                    // to the external MSBuild.exe spawn.
                    LogExceptionChain("Outer-Execute", ex);
                    return InProcessBuildOutcome.CouldNotRun;
                }
            }
        }

        // Compile-only fast-fast path. Bypasses BuildOne's hardcoded BuildOptions.BuildProcess
        // (which forces Specify+Generate+Compile, ~25s warm on our reference KB) and invokes
        // GenexusBLServices.Build.Build(...) directly with only the Compile flag. The .cs file
        // must already be current — if it's stale the SDK errors and we fall back to BuildOne.
        // Returns true on success, false on any reflection / execute failure.
        private static bool ExecuteCompileOnly(object kbHandle, string objectName, Action<string, bool> lineSink)
        {
            try
            {
                if (_miRunCompile == null || _typeGenexusBLServices == null || _typeObjectNameHelper == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteCompileOnly: required types not resolved");
                    return false;
                }

                // KBObject = ObjectNameHelper.Get(kb.DesignModel, objectName)
                var designModelProp = kbHandle.GetType().GetProperty("DesignModel", BindingFlags.Public | BindingFlags.Instance);
                object designModel = designModelProp?.GetValue(kbHandle);
                if (designModel == null) { Logger.Warn("[BUILD-INPROCESS] ExecuteCompileOnly: KB.DesignModel not found"); return false; }

                // The Compile MSBuild task uses kb.DesignModel.Environment.TargetModel (not DesignModel itself).
                // Mirror that — TargetModel is the language-specific model the compiler walks.
                var envProp = designModel.GetType().GetProperty("Environment", BindingFlags.Public | BindingFlags.Instance);
                object envObj = envProp?.GetValue(designModel);
                object targetModel = null;
                if (envObj != null)
                {
                    var tmProp = envObj.GetType().GetProperty("TargetModel", BindingFlags.Public | BindingFlags.Instance);
                    targetModel = tmProp?.GetValue(envObj);
                }
                if (targetModel == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteCompileOnly: KB.DesignModel.Environment.TargetModel not found — falling back to DesignModel");
                    targetModel = designModel;
                }

                // Object resolution via ResolveTargetKBObject (supports homonym disambiguation and Type:Name qualification).
                object kbObject = ResolveTargetKBObject(targetModel, objectName) ?? ResolveTargetKBObject(designModel, objectName);
                if (kbObject == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteCompileOnly: object '" + objectName + "' not found in target model or design model");
                    return false;
                }
                var keyProp = kbObject.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                object entityKey = keyProp?.GetValue(kbObject);
                if (entityKey == null) { Logger.Warn("[BUILD-INPROCESS] ExecuteCompileOnly: KBObject.Key not found"); return false; }

                // Resolve the live IRunService instance.
                var runProp = _typeGenexusBLServices.GetProperty("Run", BindingFlags.Public | BindingFlags.Static);
                object runService = runProp?.GetValue(null);
                if (runService == null) { Logger.Warn("[BUILD-INPROCESS] ExecuteCompileOnly: GenexusBLServices.Run returned null"); return false; }

                lineSink("[BUILD-INPROCESS] compile-only (Run.Compile): targetModel + [" + objectName + "]", false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                object result = _miRunCompile.Invoke(runService, new object[] { targetModel, entityKey });
                sw.Stop();
                bool ok = result is bool b && b;
                Logger.Info("[BUILD-INPROCESS] ExecuteCompileOnly('" + objectName + "') returned " + ok + " in " + sw.ElapsedMilliseconds + "ms");
                return ok;
            }
            catch (Exception ex)
            {
                LogExceptionChain("ExecuteCompileOnly(" + objectName + ")", ex);
                return false;
            }
        }

        private enum BatchOutcome { Success, Failure, NotApplicable }

        // Multi-target batch build. Resolves all object names to EntityKeys via
        // ObjectNameHelper, builds a DevelopmentWorkingSet, and invokes the
        // 4-arg overload of GenexusBLServices.Build.Build(workingSet, BuildOptions,
        // IEnumerable<EntityKey>, CancellationToken). This is the same BL method
        // BuildOne.Execute calls — but with N keys instead of 1, so the SDK runs
        // a single shared Specify + Generate + Compile pipeline.
        //
        // Options match BuildOne exactly:
        //   ContinueOnError | BuildProcess | BuildCalled
        // (no Rebuild, no DetailedNavigation — those are RebuildAll / opt-in paths
        // that don't reach this fast path today.)
        //
        // Returns Success / Failure based on the BL return value. NotApplicable
        // signals "couldn't even attempt — fall through to per-target loop"
        // (missing types, no object resolved).
        private static BatchOutcome ExecuteBuildBatch(object kbHandle, List<string> objectNames, Action<string, bool> lineSink)
        {
            try
            {
                if (_miBuildBuild == null || _typeDevelopmentWorkingSet == null
                    || _typeBuildOptions == null || _typeObjectNameHelper == null
                    || _typeGenexusBLServices == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: required types not resolved");
                    return BatchOutcome.NotApplicable;
                }

                // kb.DesignModel — same instance used by BuildOne.
                var designModelProp = kbHandle.GetType().GetProperty("DesignModel", BindingFlags.Public | BindingFlags.Instance);
                object designModel = designModelProp?.GetValue(kbHandle);
                if (designModel == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: KB.DesignModel not found");
                    return BatchOutcome.NotApplicable;
                }

                // Resolve every object name → EntityKey via ObjectNameHelper.Get(designModel, name).
                // Mirrors BuildOne.Execute line: KBObject kBObject = ObjectNameHelper.Get(base.KB.DesignModel, ObjectName).
                var getMi = _typeObjectNameHelper.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string));
                if (getMi == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: ObjectNameHelper.Get(model,string) not found");
                    return BatchOutcome.NotApplicable;
                }

                // Need EntityKey type to build the strongly-typed List<EntityKey> the BL expects.
                var keysList = new List<string>(); // names, for log
                System.Collections.IList typedList = null;
                Type entityKeyType = _miBuildBuild.GetParameters()[2].ParameterType.IsGenericType
                    ? _miBuildBuild.GetParameters()[2].ParameterType.GetGenericArguments()[0]
                    : null;
                if (entityKeyType == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: cannot infer EntityKey generic argument");
                    return BatchOutcome.NotApplicable;
                }
                var listType = typeof(List<>).MakeGenericType(entityKeyType);
                typedList = (System.Collections.IList)Activator.CreateInstance(listType);

                foreach (var name in objectNames)
                {
                    object kbObject = ResolveTargetKBObject(designModel, name);
                    if (kbObject == null)
                    {
                        // BuildOne treats an unresolved name as `return true` (no-op). To preserve
                        // that semantics in a batch (one unknown name shouldn't poison the others),
                        // we just skip it.
                        lineSink("[BUILD-INPROCESS] batch: object '" + name + "' not found in DesignModel — skipping.", false);
                        continue;
                    }
                    var keyProp = kbObject.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                    object entityKey = keyProp?.GetValue(kbObject);
                    if (entityKey == null)
                    {
                        Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: KBObject.Key null for '" + name + "'");
                        continue;
                    }
                    typedList.Add(entityKey);
                    keysList.Add(name);
                }

                if (typedList.Count == 0)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: no resolvable objects in target list");
                    return BatchOutcome.NotApplicable;
                }

                // DevelopmentWorkingSet(designModel) — same single-arg ctor BuildOne uses.
                var workingSetCtor = _typeDevelopmentWorkingSet.GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Length == 1
                        && string.Equals(c.GetParameters()[0].ParameterType.Name, "KBModel", StringComparison.Ordinal));
                if (workingSetCtor == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: DevelopmentWorkingSet(KBModel) ctor not found");
                    return BatchOutcome.NotApplicable;
                }
                object workingSet = workingSetCtor.Invoke(new object[] { designModel });

                // BuildOptions(ContinueOnError | BuildProcess | BuildCalled)
                object buildOptions = Enum.ToObject(_typeBuildOptions, 0x01 | 0x04 | 0x08);

                using (var cts = new CancellationTokenSource())
                {
                    var buildProp = _typeGenexusBLServices.GetProperty("Build", BindingFlags.Public | BindingFlags.Static);
                    object buildService = buildProp?.GetValue(null);
                    if (buildService == null)
                    {
                        Logger.Warn("[BUILD-INPROCESS] ExecuteBuildBatch: GenexusBLServices.Build returned null");
                        return BatchOutcome.NotApplicable;
                    }

                    lineSink("[BUILD-INPROCESS] batch BuildBatch: " + typedList.Count + " keys → BL.Build (shared spec/gen/compile).", false);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    object result = _miBuildBuild.Invoke(buildService, new object[] { workingSet, buildOptions, typedList, cts.Token });
                    sw.Stop();
                    bool ok = result is bool b && b;
                    Logger.Info("[BUILD-INPROCESS] ExecuteBuildBatch(" + string.Join(",", keysList) + ") returned " + ok
                                + " in " + sw.ElapsedMilliseconds + "ms");
                    return ok ? BatchOutcome.Success : BatchOutcome.Failure;
                }
            }
            catch (Exception ex)
            {
                LogExceptionChain("ExecuteBuildBatch(" + string.Join(",", objectNames) + ")", ex);
                return BatchOutcome.Failure;
            }
        }

        // Issue #96: Batch build without callees using IBuildServiceBL.BuildWithTheseOnly.
        // Runs a single shared Specify + Generate + MSBuild compilation pipeline for all
        // supplied targets, amortizing fixed overhead across the batch.
        private static BatchOutcome ExecuteBuildWithTheseOnly(object kbHandle, List<string> objectNames, Action<string, bool> lineSink)
        {
            try
            {
                if (_miBuildWithTheseOnly == null || _typeDevelopmentWorkingSet == null
                    || _typeGenexusBLServices == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly: required types not resolved");
                    return BatchOutcome.NotApplicable;
                }

                var designModelProp = kbHandle.GetType().GetProperty("DesignModel", BindingFlags.Public | BindingFlags.Instance);
                object designModel = designModelProp?.GetValue(kbHandle);
                if (designModel == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly: KB.DesignModel not found");
                    return BatchOutcome.NotApplicable;
                }

                var keysList = new List<string>();
                Type entityKeyType = _miBuildWithTheseOnly.GetParameters()[1].ParameterType.IsGenericType
                    ? _miBuildWithTheseOnly.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                    : null;
                if (entityKeyType == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly: cannot infer EntityKey generic argument");
                    return BatchOutcome.NotApplicable;
                }
                var listType = typeof(List<>).MakeGenericType(entityKeyType);
                var typedList = (System.Collections.IList)Activator.CreateInstance(listType);

                foreach (var name in objectNames)
                {
                    object kbObject = ResolveTargetKBObject(designModel, name);
                    if (kbObject == null)
                    {
                        lineSink("[BUILD-INPROCESS] batch: object '" + name + "' not found in DesignModel — skipping.", false);
                        continue;
                    }
                    var keyProp = kbObject.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                    object entityKey = keyProp?.GetValue(kbObject);
                    if (entityKey == null)
                    {
                        Logger.Warn("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly: KBObject.Key null for '" + name + "'");
                        continue;
                    }
                    typedList.Add(entityKey);
                    keysList.Add(name);
                }

                if (typedList.Count == 0)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly: no resolvable objects in target list");
                    return BatchOutcome.NotApplicable;
                }

                var workingSetCtor = _typeDevelopmentWorkingSet.GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Length == 1
                        && string.Equals(c.GetParameters()[0].ParameterType.Name, "KBModel", StringComparison.Ordinal));
                if (workingSetCtor == null)
                {
                    Logger.Warn("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly: DevelopmentWorkingSet(KBModel) ctor not found");
                    return BatchOutcome.NotApplicable;
                }
                object workingSet = workingSetCtor.Invoke(new object[] { designModel });

                using (var cts = new CancellationTokenSource())
                {
                    var buildProp = _typeGenexusBLServices.GetProperty("Build", BindingFlags.Public | BindingFlags.Static);
                    object buildService = buildProp?.GetValue(null);
                    if (buildService == null)
                    {
                        Logger.Warn("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly: GenexusBLServices.Build returned null");
                        return BatchOutcome.NotApplicable;
                    }

                    lineSink("[BUILD-INPROCESS] batch BuildWithTheseOnly: " + typedList.Count + " keys → BL.BuildWithTheseOnly (shared spec/gen/compile).", false);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _miBuildWithTheseOnly.Invoke(buildService, new object[] { workingSet, typedList, cts.Token });
                    sw.Stop();
                    Logger.Info("[BUILD-INPROCESS] ExecuteBuildWithTheseOnly(" + string.Join(",", keysList) + ") completed in " + sw.ElapsedMilliseconds + "ms");
                    return BatchOutcome.Success;
                }
            }
            catch (Exception ex)
            {
                LogExceptionChain("ExecuteBuildWithTheseOnly(" + string.Join(",", objectNames) + ")", ex);
                return BatchOutcome.Failure;
            }
        }

        // Fast per-object build (IDE F5 parity). Returns true on Execute returning
        // true; engine sink already captured any spec/gen/compile diagnostics.
        private static bool ExecuteBuildOne(object kbHandle, string objectName, IBuildEngine engine, bool buildCalled)
        {
            try
            {
                var typeBuildOne = _typeBuildOne;
                if (typeBuildOne == null)
                {
                    Logger.Error("[BUILD-INPROCESS] BuildOne type not loaded");
                    return false;
                }
                string cleanName = objectName;
                if (!string.IsNullOrEmpty(cleanName))
                {
                    int c = cleanName.IndexOf(':');
                    if (c > 0) cleanName = cleanName.Substring(c + 1).Trim();
                }

                object task = Activator.CreateInstance(typeBuildOne);
                SetProp(task, "KB", kbHandle);
                SetProp(task, "ObjectName", cleanName);
                SetProp(task, "ForceRebuild", false);
                SetProp(task, "BuildCalled", buildCalled);
                SetProp(task, "Output", "IDE");
                SetProp(task, "EventsSuspended", true);
                SetProp(task, "BuildEngine", engine);

                var execute = typeBuildOne.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
                if (execute == null)
                {
                    Logger.Error("[BUILD-INPROCESS] BuildOne.Execute method not found");
                    return false;
                }
                object result = execute.Invoke(task, null);
                bool ok = result is bool b && b;
                if (!ok) Logger.Warn("[BUILD-INPROCESS] BuildOne.Execute returned false for '" + objectName + "'");
                return ok;
            }
            catch (Exception ex)
            {
                LogExceptionChain("BuildOne(" + objectName + ")", ex);
                return false;
            }
        }

        /// <summary>
        /// Bug #3: count how many requested target names resolve to a KBObject via
        /// ResolveTargetKBObject (homonym-safe and Type:Name capable). Returns -1 when resolution
        /// can't be determined (missing type / DesignModel) so the caller does NOT gate and
        /// preserves legacy behavior. Otherwise returns the resolved count and reports
        /// unresolved names via <paramref name="unresolved"/>.
        /// </summary>
        private static int CountResolvableTargets(object kbHandle, List<string> names, out List<string> unresolved)
        {
            unresolved = new List<string>();
            try
            {
                if (names == null || names.Count == 0) return -1;
                var designModelProp = kbHandle?.GetType().GetProperty("DesignModel", BindingFlags.Public | BindingFlags.Instance);
                object designModel = designModelProp?.GetValue(kbHandle);
                if (designModel == null) return -1;

                int resolved = 0;
                foreach (var name in names)
                {
                    object kbObject = null;
                    try { kbObject = ResolveTargetKBObject(designModel, name); }
                    catch { return -1; } // resolution itself threw — don't gate on a partial answer
                    if (kbObject == null) unresolved.Add(name);
                    else resolved++;
                }
                return resolved;
            }
            catch
            {
                unresolved = new List<string>();
                return -1;
            }
        }

        /// <summary>
        /// Issue #115: Resolves a build target to a KBObject in the specified model.
        /// Supports:
        /// 1. Type:Name qualification (e.g. "Transaction:SampleEntity", "Table:SampleEntity")
        /// 2. Guid string ("{...}" or raw Guid)
        /// 3. ObjectNameHelper.Get(model, name)
        /// 4. Disambiguation of Table homonyms: when ObjectNameHelper.Get returns a Table or null,
        ///    probes primary logic objects (Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, DataSelector)
        ///    before falling back to Table.
        /// </summary>
        internal static object ResolveTargetKBObject(object model, string target)
        {
            if (model == null || string.IsNullOrWhiteSpace(target)) return null;

            string simpleName = target.Trim();
            string typeName = null;
            int colon = simpleName.IndexOf(':');
            if (colon > 0)
            {
                typeName = simpleName.Substring(0, colon).Trim();
                simpleName = simpleName.Substring(colon + 1).Trim();
            }

            // 1. Guid resolution
            if (Guid.TryParse(simpleName, out Guid guid))
            {
                try
                {
                    var objectsProp = model.GetType().GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
                    object objects = objectsProp?.GetValue(model);
                    if (objects != null)
                    {
                        var getGuidMi = objects.GetType().GetMethod("Get", new[] { typeof(Guid) });
                        if (getGuidMi != null)
                        {
                            object hit = getGuidMi.Invoke(objects, new object[] { guid });
                            if (hit != null) return hit;
                        }
                    }
                }
                catch { }
            }

            // 2. Explicit Type qualification
            if (!string.IsNullOrEmpty(typeName))
            {
                object typedHit = TryResolveTypedKBObject(model, typeName, simpleName);
                if (typedHit != null) return typedHit;
            }

            // 3. ObjectNameHelper.Get(model, simpleName)
            if (_typeObjectNameHelper != null)
            {
                try
                {
                    var getMi = _typeObjectNameHelper.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string));
                    if (getMi != null)
                    {
                        object hit = getMi.Invoke(null, new object[] { model, simpleName });
                        if (hit != null)
                        {
                            string desc = null;
                            try
                            {
                                var tdProp = hit.GetType().GetProperty("TypeDescriptor", BindingFlags.Public | BindingFlags.Instance);
                                object td = tdProp?.GetValue(hit);
                                var nameProp = td?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                                desc = nameProp?.GetValue(td) as string;
                            }
                            catch { }

                            // If not a Table, return it directly
                            if (!string.Equals(desc, "Table", StringComparison.OrdinalIgnoreCase))
                            {
                                return hit;
                            }

                            // If it is a Table, check if a Transaction shares this name (homonym)
                            object trnHit = TryResolveTypedKBObject(model, "Transaction", simpleName);
                            if (trnHit != null) return trnHit;
                            return hit;
                        }
                    }
                }
                catch { }
            }

            // 4. Probe candidates in priority order (primary logic types first)
            string[] candidateTypes = { "Transaction", "Procedure", "WebPanel", "SDPanel", "SDT", "DataProvider", "DataSelector", "Domain", "Table" };
            foreach (var cand in candidateTypes)
            {
                object candHit = TryResolveTypedKBObject(model, cand, simpleName);
                if (candHit != null) return candHit;
            }

            return null;
        }

        private static object TryResolveTypedKBObject(object model, string typeName, string name)
        {
            if (model == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var modelObj = model as Artech.Architecture.Common.Objects.KBModel;
                if (modelObj == null)
                {
                    var designProp = model.GetType().GetProperty("DesignModel", BindingFlags.Public | BindingFlags.Instance);
                    modelObj = (designProp?.GetValue(model) as Artech.Architecture.Common.Objects.KBModel);
                }

                if (modelObj != null)
                {
                    var qName = new Artech.Architecture.Common.Objects.QualifiedName(name);
                    string norm = (typeName ?? "").Trim();

                    // 1. Direct typed lookups for primary logic types
                    if (norm.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
                    {
                        try { return Artech.Genexus.Common.Objects.Transaction.Get(modelObj, qName); } catch { }
                    }
                    if (norm.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
                    {
                        try { return Artech.Genexus.Common.Objects.Procedure.Get(modelObj, qName); } catch { }
                    }
                    if (norm.Equals("WebPanel", StringComparison.OrdinalIgnoreCase))
                    {
                        try { return Artech.Genexus.Common.Objects.WebPanel.Get(modelObj, qName); } catch { }
                    }
                    if (norm.Equals("DataProvider", StringComparison.OrdinalIgnoreCase))
                    {
                        try { return Artech.Genexus.Common.Objects.DataProvider.Get(modelObj, qName); } catch { }
                    }
                    if (norm.Equals("DataSelector", StringComparison.OrdinalIgnoreCase))
                    {
                        try { return Artech.Genexus.Common.Objects.DataSelector.Get(modelObj, qName); } catch { }
                    }
                    if (norm.Equals("Domain", StringComparison.OrdinalIgnoreCase))
                    {
                        try { return Artech.Genexus.Common.Objects.Domain.Get(modelObj, qName); } catch { }
                    }

                    // 2. Lookup across all objects via modelObj.Objects.GetByName
                    try
                    {
                        var matches = modelObj.Objects.GetByName(null, null, name);
                        if (matches != null)
                        {
                            object tableFallback = null;
                            foreach (Artech.Architecture.Common.Objects.KBObject o in matches)
                            {
                                if (o == null) continue;
                                string oType = o.TypeDescriptor?.Name ?? o.GetType().Name;
                                if (!string.IsNullOrEmpty(norm))
                                {
                                    if (string.Equals(oType, norm, StringComparison.OrdinalIgnoreCase))
                                        return o;
                                }
                                else
                                {
                                    // If untyped, prefer non-Table objects over Table
                                    if (string.Equals(oType, "Table", StringComparison.OrdinalIgnoreCase) || o is Artech.Genexus.Common.Objects.Table)
                                    {
                                        if (tableFallback == null) tableFallback = o;
                                    }
                                    else
                                    {
                                        return o;
                                    }
                                }
                            }
                            if (tableFallback != null) return tableFallback;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static bool EnsureTypesLoaded()
        {
            lock (_typeCacheLock)
            {
                if (_typeSpecifyOneOnly != null && _typeIdeWebBuildAndDeploy != null) return true;
                if (_assemblyLoadAttempted && (_typeSpecifyOneOnly == null || _typeIdeWebBuildAndDeploy == null))
                {
                    // We already failed once; don't spam the log on every build.
                    return false;
                }
                _assemblyLoadAttempted = true;

                string gxDir = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR")
                               ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                if (string.IsNullOrWhiteSpace(gxDir) || !Directory.Exists(gxDir))
                {
                    Logger.Error("[BUILD-INPROCESS] GX_PROGRAM_DIR not found: " + gxDir);
                    return false;
                }

                string asmPath = Path.Combine(gxDir, "Genexus.MsBuild.Tasks.dll");
                if (!File.Exists(asmPath))
                {
                    Logger.Error("[BUILD-INPROCESS] Genexus.MsBuild.Tasks.dll not found at " + asmPath);
                    return false;
                }

                Assembly asm;
                try
                {
                    asm = Assembly.LoadFrom(asmPath);
                }
                catch (Exception ex)
                {
                    Logger.Error("[BUILD-INPROCESS] LoadFrom failed for " + asmPath + ": " + ex.Message);
                    return false;
                }

                _typeSpecifyOneOnly = asm.GetType("Genexus.MsBuild.Tasks.SpecifyOneOnly", throwOnError: false);
                _typeIdeWebBuildAndDeploy = asm.GetType("Genexus.MsBuild.Tasks.IdeWebBuildAndDeploy", throwOnError: false);
                _typeBuildOne = asm.GetType("Genexus.MsBuild.Tasks.BuildOne", throwOnError: false);
                _typeBuildAll = asm.GetType("Genexus.MsBuild.Tasks.BuildAll", throwOnError: false);

                if (_typeSpecifyOneOnly == null || _typeIdeWebBuildAndDeploy == null)
                {
                    Logger.Error("[BUILD-INPROCESS] Required task types missing in Genexus.MsBuild.Tasks.dll "
                                 + "(SpecifyOneOnly=" + (_typeSpecifyOneOnly != null) + ", "
                                 + "IdeWebBuildAndDeploy=" + (_typeIdeWebBuildAndDeploy != null) + ", "
                                 + "BuildOne=" + (_typeBuildOne != null) + ", "
                                 + "BuildAll=" + (_typeBuildAll != null) + ")");
                    return false;
                }

                Logger.Info("[BUILD-INPROCESS] Task types loaded from " + asmPath
                            + " (BuildOne fast path: " + (_typeBuildOne != null ? "available" : "missing")
                            + ", BuildAll: " + (_typeBuildAll != null ? "available" : "missing") + ")");

                // Compile-only fast-fast path: load the GeneXus BL types via the
                // referenced assemblies of Genexus.MsBuild.Tasks (already loaded above).
                // Failure here is non-fatal — we only lose the fast-fast path.
                try
                {
                    Assembly LoadByName(string name)
                    {
                        var an = asm.GetReferencedAssemblies().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        return an != null ? Assembly.Load(an) : null;
                    }
                    var asmGxCommon = LoadByName("Artech.Genexus.Common");
                    var asmArchCommon = LoadByName("Artech.Architecture.Common");
                    if (asmGxCommon != null)
                    {
                        _typeGenexusBLServices = asmGxCommon.GetType("Artech.Genexus.Common.Services.GenexusBLServices", false);
                        _typeBuildOptions = asmGxCommon.GetType("Artech.Genexus.Common.Commands.BuildOptions", false);
                    }
                    if (asmArchCommon != null)
                    {
                        // Confirmed via ilspycmd -l class: types live under .Objects and .Helpers,
                        // not .Helpers.Build as the BuildOne decompile suggested.
                        _typeDevelopmentWorkingSet = asmArchCommon.GetType("Artech.Architecture.Common.Objects.DevelopmentWorkingSet", false);
                        _typeObjectNameHelper = asmArchCommon.GetType("Artech.Architecture.Common.Helpers.ObjectNameHelper", false);
                    }
                    // Resolve IBuildServiceBL.Build(workingSet, BuildOptions, IEnumerable<EntityKey>, CancellationToken)
                    // and IBuildServiceBL.BuildWithTheseOnly(workingSet, IEnumerable<EntityKey>, CancellationToken).
                    if (_typeGenexusBLServices != null && _typeDevelopmentWorkingSet != null)
                    {
                        var buildProp = _typeGenexusBLServices.GetProperty("Build", BindingFlags.Public | BindingFlags.Static);
                        var serviceType = buildProp?.PropertyType;
                        if (serviceType != null)
                        {
                            foreach (var mi in serviceType.GetMethods())
                            {
                                if (_typeBuildOptions != null && string.Equals(mi.Name, "Build", StringComparison.Ordinal))
                                {
                                    var ps = mi.GetParameters();
                                    if (ps.Length == 4
                                        && ps[0].ParameterType == _typeDevelopmentWorkingSet
                                        && ps[1].ParameterType == _typeBuildOptions
                                        && ps[3].ParameterType == typeof(CancellationToken))
                                    {
                                        _miBuildBuild = mi;
                                    }
                                }
                                else if (string.Equals(mi.Name, "BuildWithTheseOnly", StringComparison.Ordinal))
                                {
                                    var ps = mi.GetParameters();
                                    if (ps.Length == 3
                                        && ps[0].ParameterType == _typeDevelopmentWorkingSet
                                        && ps[2].ParameterType == typeof(CancellationToken))
                                    {
                                        _miBuildWithTheseOnly = mi;
                                    }
                                }
                            }
                        }
                    }
                    // Resolve IRunService.Compile(KBModel, EntityKey) → bool.
                    if (_typeGenexusBLServices != null)
                    {
                        var runProp = _typeGenexusBLServices.GetProperty("Run", BindingFlags.Public | BindingFlags.Static);
                        var runServiceType = runProp?.PropertyType;
                        if (runServiceType != null)
                        {
                            foreach (var mi in runServiceType.GetMethods())
                            {
                                if (!string.Equals(mi.Name, "Compile", StringComparison.Ordinal)) continue;
                                var ps = mi.GetParameters();
                                if (ps.Length != 2) continue;
                                // ps[0] is KBModel, ps[1] is EntityKey — match by type name (avoids type-loading cycles).
                                if (!string.Equals(ps[0].ParameterType.Name, "KBModel", StringComparison.Ordinal)) continue;
                                if (!string.Equals(ps[1].ParameterType.Name, "EntityKey", StringComparison.Ordinal)) continue;
                                _miRunCompile = mi;
                                break;
                            }
                        }
                    }

                    Logger.Info("[BUILD-INPROCESS] Compile-only path: "
                                + (_miRunCompile != null ? "available (Run.Compile)" : (_miBuildBuild != null ? "available (Build.Build — slow, forces spec)" : "missing"))
                                + " (BL=" + (_typeGenexusBLServices != null)
                                + ", BuildOptions=" + (_typeBuildOptions != null)
                                + ", DevSet=" + (_typeDevelopmentWorkingSet != null)
                                + ", ObjNameHelper=" + (_typeObjectNameHelper != null)
                                + ", RunCompile=" + (_miRunCompile != null)
                                + ", BuildWithTheseOnly=" + (_miBuildWithTheseOnly != null) + ")");
                }
                catch (Exception coEx)
                {
                    Logger.Warn("[BUILD-INPROCESS] Compile-only resolution failed (non-fatal): " + coEx.Message);
                }

                // 2026-05-22 (Path 1) — SDK has a "resilient Specifier daemon" mode
                // (SpecifierRecycleCount=0) that keeps the daemon + caches alive between
                // BuildOne calls. Live benchmark on the reference KB showed this REGRESSED
                // warm spec time from ~16s to ~25s — without recycle the daemon accumulates
                // state and gets slower, not faster. Kept as opt-in (GXMCP_RESILIENT_SPEC=1)
                // for KBs where the trade-off might be different. The override path is
                // safe (InProcessBag in-memory only, no disk write). Default OFF.
                try
                {
                    if (string.Equals(Environment.GetEnvironmentVariable("GXMCP_RESILIENT_SPEC"), "1", StringComparison.OrdinalIgnoreCase))
                    {
                        Assembly LoadByName2(string name)
                        {
                            var an = asm.GetReferencedAssemblies().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                            return an != null ? Assembly.Load(an) : null;
                        }
                        var commonHelpers = LoadByName2("Artech.Common.Helpers") ?? AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(a => a.GetName().Name.Equals("Artech.Common.Helpers", StringComparison.OrdinalIgnoreCase));
                        if (commonHelpers == null)
                        {
                            commonHelpers = Assembly.LoadFrom(Path.Combine(gxDir, "Artech.Common.Helpers.dll"));
                        }
                        var bagType = commonHelpers?.GetType("Artech.Common.Helpers.SharedMemory.InProcessBag", false);
                        var getBagMi = bagType?.GetMethod("GetBag", BindingFlags.Public | BindingFlags.Static);
                        var bag = getBagMi?.Invoke(null, new object[] { "DAEMON" }) as System.Collections.IDictionary;
                        if (bag != null)
                        {
                            bag["SpecifierRecycleCount"] = "0"; // 0 = resilient mode (keep daemon + caches alive)
                            Logger.Info("[BUILD-INPROCESS] Resilient Specifier daemon enabled (SpecifierRecycleCount=0 in DAEMON bag)");
                        }
                        else
                        {
                            Logger.Warn("[BUILD-INPROCESS] Resilient Specifier setup: InProcessBag(DAEMON) not resolvable — leaving SDK default (-1).");
                        }
                    }
                }
                catch (Exception rsEx)
                {
                    Logger.Warn("[BUILD-INPROCESS] Resilient Specifier setup failed (non-fatal): " + rsEx.Message);
                }

                // Force-trigger Artech.MsBuild.Common.ArtechTask's static ctor in
                // isolation so we can capture the REAL cause (the live runtime hides
                // it inside two layers of TargetInvocationException + TypeInitException).
                try
                {
                    var artechAsm = asm.GetReferencedAssemblies()
                        .FirstOrDefault(n => n.Name.Equals("Artech.MsBuild.Common", StringComparison.OrdinalIgnoreCase));
                    if (artechAsm != null)
                    {
                        var loaded = Assembly.Load(artechAsm);
                        var artechTaskType = loaded.GetType("Artech.MsBuild.Common.ArtechTask", throwOnError: false);
                        if (artechTaskType != null)
                        {
                            try
                            {
                                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                                    artechTaskType.TypeHandle);
                                Logger.Info("[BUILD-INPROCESS] ArtechTask static ctor: OK");
                            }
                            catch (Exception ctorEx)
                            {
                                LogExceptionChain("ArtechTask-cctor", ctorEx);
                            }
                        }
                        else
                        {
                            Logger.Warn("[BUILD-INPROCESS] Artech.MsBuild.Common.ArtechTask type NOT FOUND in " + loaded.Location);
                        }
                    }
                    else
                    {
                        Logger.Warn("[BUILD-INPROCESS] Artech.MsBuild.Common assembly is not a referenced dependency of Genexus.MsBuild.Tasks");
                    }
                }
                catch (Exception probeEx)
                {
                    LogExceptionChain("ArtechTask-probe", probeEx);
                }

                return true;
            }
        }

        private static bool ExecuteSpecifyOneOnly(object kbHandle, List<string> targets, IBuildEngine engine)
        {
            try
            {
                object task = Activator.CreateInstance(_typeSpecifyOneOnly);
                SetProp(task, "KB", kbHandle);
                SetProp(task, "ObjectNames", string.Join(";", targets));
                SetProp(task, "BuildEngine", engine);

                var execute = _typeSpecifyOneOnly.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
                if (execute == null)
                {
                    Logger.Error("[BUILD-INPROCESS] SpecifyOneOnly.Execute method not found");
                    return false;
                }
                object result = execute.Invoke(task, null);
                bool ok = result is bool b && b;
                if (!ok) Logger.Warn("[BUILD-INPROCESS] SpecifyOneOnly.Execute returned false");
                return ok;
            }
            catch (Exception ex)
            {
                LogExceptionChain("SpecifyOneOnly", ex);
                return false;
            }
        }

        // Unwrap nested TargetInvocationException / TypeInitializationException so
        // the real root cause (usually a missing assembly / IBuildEngine surface
        // mismatch) shows in worker_debug.log instead of the generic outer wrapper.
        private static void LogExceptionChain(string what, Exception ex)
        {
            int depth = 0;
            for (Exception e = ex; e != null && depth < 8; e = e.InnerException, depth++)
            {
                string indent = new string(' ', depth * 2);
                Logger.Error("[BUILD-INPROCESS] " + indent + what + " ex[" + depth + "] "
                             + e.GetType().FullName + ": " + e.Message);
                if (e is System.Reflection.ReflectionTypeLoadException rtle && rtle.LoaderExceptions != null)
                {
                    int li = 0;
                    foreach (var le in rtle.LoaderExceptions)
                    {
                        Logger.Error("[BUILD-INPROCESS] " + indent + "  loader[" + (li++) + "] "
                                     + le?.GetType().FullName + ": " + le?.Message);
                    }
                }
                if (depth == 0 && e.StackTrace != null)
                {
                    foreach (var line in e.StackTrace.Split('\n').Take(5))
                        Logger.Error("[BUILD-INPROCESS]   at " + line.Trim());
                }
            }
        }

        private static bool ExecuteIdeWebBuildAndDeploy(object kbHandle, IBuildEngine engine, bool forceRebuild)
        {
            try
            {
                object task = Activator.CreateInstance(_typeIdeWebBuildAndDeploy);
                SetProp(task, "KB", kbHandle);
                SetProp(task, "ForceRebuild", forceRebuild);
                SetProp(task, "CompileMains", true);
                SetProp(task, "Output", "IDE");
                SetProp(task, "EventsSuspended", true);
                SetProp(task, "BuildEngine", engine);

                var execute = _typeIdeWebBuildAndDeploy.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
                if (execute == null)
                {
                    Logger.Error("[BUILD-INPROCESS] IdeWebBuildAndDeploy.Execute method not found");
                    return false;
                }
                object result = execute.Invoke(task, null);
                bool ok = result is bool b && b;
                if (!ok) Logger.Warn("[BUILD-INPROCESS] IdeWebBuildAndDeploy.Execute returned false");
                return ok;
            }
            catch (Exception ex)
            {
                LogExceptionChain("IdeWebBuildAndDeploy", ex);
                return false;
            }
        }

        private static bool ExecuteBuildAll(object kbHandle, IBuildEngine engine, Action<string, bool> lineSink)
        {
            try
            {
                object task = Activator.CreateInstance(_typeBuildAll);
                SetProp(task, "KB", kbHandle);
                SetProp(task, "ForceRebuild", false);
                SetProp(task, "CompileMains", true);
                SetProp(task, "FailIfReorg", true);
                SetProp(task, "DoNotExecuteReorg", true);
                SetProp(task, "DetailedNavigation", false);
                SetProp(task, "Output", "IDE");
                SetProp(task, "EventsSuspended", true);
                SetProp(task, "BuildEngine", engine);

                var execute = _typeBuildAll.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
                if (execute == null)
                {
                    Logger.Error("[BUILD-INPROCESS] BuildAll.Execute method not found");
                    return false;
                }
                object result = execute.Invoke(task, null);
                bool ok = result is bool b && b;
                if (!ok) Logger.Warn("[BUILD-INPROCESS] BuildAll.Execute returned false");
                return ok;
            }
            catch (Exception ex)
            {
                LogExceptionChain("BuildAll", ex);
                lineSink("[GXMCP-BUILD-ALL] BuildAll task failed: " + ex.Message, true);
                return false;
            }
        }

        private static void SetProp(object target, string name, object value)
        {
            if (target == null) return;
            var pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null || !pi.CanWrite)
            {
                // Not all properties are guaranteed to exist across GeneXus versions —
                // missing optional properties are ignored, required ones blow up
                // later in Execute() and surface through the catch.
                Logger.Debug("[BUILD-INPROCESS] property '" + name + "' not settable on " + target.GetType().Name);
                return;
            }
            pi.SetValue(target, value);
        }

        // Test seam — exposes the type-resolution result so unit tests can
        // assert SDK presence without needing a live KB.
        internal static bool TryResolveTypes(out string error)
        {
            error = null;
            if (EnsureTypesLoaded()) return true;
            error = "Genexus.MsBuild.Tasks types not loaded — see worker_debug.log";
            return false;
        }
    }
}
