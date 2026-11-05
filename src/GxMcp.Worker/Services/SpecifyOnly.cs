using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // lifecycle action=specify_only: run GeneXus' Specify step for ONE object, identified by
    // GUID, and nothing else. The pieces below are deliberately free of the GeneXus SDK so the
    // decisions (what is refused, what is planned, what the receipt says) are unit-testable:
    //   SpecifyOnlyPlanner         facts -> plan or refusal
    //   SpecifyOnlyExecutor        runs a plan against an ISpecifyOnlyEnvironment
    //   SpecifyOnlyJudge           the verdict on a finished run (shared by the receipt and the runner)
    //   SpecifyOnlyReceiptBuilder  record -> the JSON the caller reads
    // The SDK-bound implementation of ISpecifyOnlyEnvironment lives in
    // InProcessBuildRunner.SpecifyOnly.cs.

    internal static class SpecifyOnlyCodes
    {
        public const string InvalidGuid = "SpecifyOnlyInvalidGuid";
        public const string ObjectNotFound = "SpecifyOnlyObjectNotFound";
        public const string VersionConflict = "SpecifyOnlyVersionConflict";
        public const string EnvironmentCopyUnsafe = "SpecifyOnlyEnvironmentCopyUnsafe";
        public const string EnvironmentCopyUnavailable = "SpecifyOnlyEnvironmentCopyUnavailable";
        public const string EnvironmentCopyFailed = "SpecifyOnlyEnvironmentCopyFailed";
        public const string EnvironmentUnavailable = "SpecifyOnlyEnvironmentUnavailable";
        public const string EnvironmentNotSupported = "SpecifyOnlyEnvironmentNotSupported";
        public const string SpecifierUnavailable = "SpecifyOnlySpecifierUnavailable";
        public const string GenerationNotSuppressible = "SpecifyOnlyGenerationNotSuppressible";
        public const string InProcessDisabled = "SpecifyOnlyInProcessDisabled";
        public const string KbNotOpen = "SpecifyOnlyKbNotOpen";
        public const string ExecutionFailed = "SpecifyOnlyExecutionFailed";
        public const string SpecificationFailed = "SpecifyOnlySpecificationFailed";
        public const string ConcurrentChange = "SpecifyOnlyConcurrentChange";
        public const string GenerationObserved = "SpecifyOnlyGenerationObserved";
        public const string ObjectUnreachable = "SpecifyOnlyObjectUnreachable";
        public const string VersionUnverified = "SpecifyOnlyVersionUnverified";
        public const string SpecificationNotObserved = "SpecifyOnlySpecificationNotObserved";
        public const string TypeNotSupported = "SpecifyOnlyTypeNotSupported";
        public const string GeneratorOutputTouched = "SpecifyOnlyGeneratorOutputTouched";

        // Marker the runner puts on the error lines it emits itself, so the receipt can tell them apart
        // from the specifier's own spc####/gen#### lines. The colon cannot occur in an object name, and the
        // SDK suffixes its own lines with "[<Object>, line N]", so no SDK line carries it.
        public const string LineMarker = "[SpecifyOnly:";

        public static string Line(string code, string message)
            => "error : " + LineMarker + code + "] " + message;
    }

    internal static class SpecifyOnlySteps
    {
        public const string ResolveObject = "resolve-object";
        public const string CheckConcurrency = "check-concurrency";
        public const string CopyToEnvironment = "copy-to-environment";
        public const string Specify = "specify";
        public const string VerifyVersion = "verify-version";

        public const string Planned = "planned";
        public const string Done = "done";
        public const string Skipped = "skipped";
        public const string Failed = "failed";

        // Steps this operation never runs, reported on every receipt so a caller does not
        // have to infer them from what is missing.
        public static readonly KeyValuePair<string, string>[] NeverRun =
        {
            new KeyValuePair<string, string>("generate", "Not part of specify_only; the SDK's concurrent generation is switched off for the call."),
            new KeyValuePair<string, string>("compile", "Not part of specify_only."),
            new KeyValuePair<string, string>("reorganize", "Not part of specify_only; no database reorganization is checked, created or applied."),
            new KeyValuePair<string, string>("deploy", "Not part of specify_only."),
            new KeyValuePair<string, string>("execute", "Not part of specify_only; the object is not run."),
            new KeyValuePair<string, string>("test", "Not part of specify_only; no test is run.")
        };
    }

    internal enum SpecifyOnlyCopyState
    {
        // The Knowledge Base has no separate environment model: the specifier reads the design model.
        NotApplicable,
        Current,
        Missing,
        Stale
    }

    // Public only because BuildService.Build, which carries it to the task, is public.
    public sealed class SpecifyOnlyRequest
    {
        public Guid Guid { get; set; }
        public string ExpectedVersion { get; set; }
    }

    // What the environment reported about the target. Everything the planner decides on.
    internal sealed class SpecifyOnlyFacts
    {
        public bool ObjectFound { get; set; }
        public string Name { get; set; }
        public string TypeName { get; set; }
        // Opaque object version (WriteService.ComputeVersionToken): the object-level prefix of the token genexus_read hands out.
        public string Version { get; set; }
        public bool SeparateEnvironment { get; set; }
        public bool EnvironmentCopyExists { get; set; }
        public DateTime? DesignLastUpdate { get; set; }
        public DateTime? EnvironmentLastUpdate { get; set; }
        public bool CopyApiAvailable { get; set; }
        public bool GenerationSuppressible { get; set; }
        public bool SpecifierAvailable { get; set; }
        // The in-process build pipeline is switched off (GXMCP_INPROCESS_BUILD=0): the only way left to
        // run a build task is an external full build, which specify_only must never fall back to.
        public bool InProcessDisabled { get; set; }
        // The KB reports no environment (target) model at all.
        public bool NoTargetModel { get; set; }
    }

    internal sealed class SpecifyOnlyStep
    {
        public string Name { get; set; }
        public bool Mutates { get; set; }
        public string State { get; set; }
        public string Detail { get; set; }
    }

    internal sealed class SpecifyOnlyRefusal
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string Hint { get; set; }
    }

    internal sealed class SpecifyOnlyPlan
    {
        public SpecifyOnlyFacts Facts { get; set; }
        public SpecifyOnlyCopyState CopyState { get; set; }
        public bool TypeAllowsEnvironmentCopy { get; set; }
        public bool NeedsEnvironmentCopy { get; set; }
        public SpecifyOnlyRefusal Refusal { get; set; }
        public List<SpecifyOnlyStep> Steps { get; } = new List<SpecifyOnlyStep>();
    }

    // What the probe of the generator output folder saw. Count is -1 when there was nothing to look at.
    internal sealed class SpecifyOnlyProbe
    {
        public int Count { get; set; } = -1;
        public List<string> Names { get; set; } = new List<string>();
    }

    // Outcome of the specifier call itself, as the SDK-bound environment saw it.
    internal sealed class SpecifyOnlyRunResult
    {
        public bool SpecifierInvoked { get; set; }
        public bool SpecifierReturned { get; set; }
        public bool GenerationSuppressed { get; set; }
        // The specifier said the object is unreachable (spc0217: no Main program reaches it), which GeneXus
        // answers by not specifying it at all.
        public bool ObjectUnreachable { get; set; }
        // The call asked the specifier to (re)specify the named object even when it considers it up to date
        // (BuildOptions.Rebuild, what SpecifyOneOnly.ForceRebuild sets); false when this SDK has no such option.
        public bool RebuildApplied { get; set; }
        public List<string> SectionsObserved { get; set; } = new List<string>();
        // Files the specifier wrote in its own output folder (GXSPC<model>) during the call.
        public List<string> SpecificationFiles { get; set; } = new List<string>();
        public string Failure { get; set; }
    }

    // Everything one execution did, in the order it did it. The receipt is rendered from this.
    internal sealed class SpecifyOnlyRun
    {
        public bool DryRun { get; set; }
        public DateTime StartedUtc { get; set; }
        public SpecifyOnlyPlan Plan { get; set; }
        // The copy call was made (it may have written something even when it threw or could not be confirmed).
        public bool CopyAttempted { get; set; }
        // A re-read confirmed the copy exists and is not older than the design object.
        public bool CopyVerified { get; set; }
        public string CopyAction { get; set; }
        public SpecifyOnlyRefusal Failure { get; set; }
        public SpecifyOnlyRunResult Specify { get; set; }
        public string VersionAfter { get; set; }
        public SpecifyOnlyProbe GeneratorProbe { get; set; } = new SpecifyOnlyProbe();

        public bool CopyPerformed => CopyAttempted && CopyVerified;

        // Both the version before and the version after the run are known, so they can be compared.
        public bool VersionVerified => Plan?.Facts?.Version != null && VersionAfter != null;

        public bool VersionChanged
            => VersionVerified
               && !string.Equals(Plan.Facts.Version, VersionAfter, StringComparison.Ordinal);
    }

    // The seam between the decisions above and the GeneXus SDK.
    internal interface ISpecifyOnlyEnvironment
    {
        SpecifyOnlyFacts ReadFacts(Guid guid);
        // Copies the design object into the environment model (KnowledgeBase.CopyEntity). Throws when the SDK refuses.
        void CopyToEnvironment(Guid guid);
        // Runs the specifier for the object with the SDK's concurrent generation switched off for the call.
        SpecifyOnlyRunResult Specify(Guid guid);
        // What changed in the generator output folder since the given instant.
        SpecifyOnlyProbe ProbeGeneratorOutput(DateTime startedUtc);
    }

    // Turns a process-wide switch off for the duration of a call and gives the previous value back whatever
    // happens. The SDK's concurrent generation is such a switch: BuildDaemonClient.ConcurrentGeneration.
    internal static class SpecifyOnlyGenerationSwitch
    {
        // Two overlapping calls would each capture a "previous" value the other has already changed and give
        // it back in the wrong order, leaving the process-wide switch stuck off (the Worker FIFO normally keeps
        // them apart; GXMCP_ALLOW_CONCURRENT_BUILDS=1 removes it). One call at a time.
        private static readonly object Gate = new object();

        // Runs body only when the switch could be read, switched off and confirmed off. Returns false (and
        // does not run body) otherwise, with the reason in failure.
        public static bool RunWithSwitchOff(Func<bool> get, Action<bool> set, Action body, out string failure)
        {
            lock (Gate) return RunWithSwitchOffLocked(get, set, body, out failure);
        }

        private static bool RunWithSwitchOffLocked(Func<bool> get, Action<bool> set, Action body, out string failure)
        {
            failure = null;
            bool previous;
            try { previous = get(); }
            catch (Exception ex) { failure = "The generation switch could not be read: " + ex.Message; return false; }

            try
            {
                try
                {
                    set(false);
                    if (get())
                    {
                        failure = "Concurrent generation could not be switched off, so the specifier was not run.";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    failure = "The generation switch could not be turned off: " + ex.Message;
                    return false;
                }
                body();
                return true;
            }
            finally
            {
                try { set(previous); }
                catch (Exception ex) { Logger.Warn("[SPECIFY-ONLY] could not restore the generation switch: " + ex.Message); }
            }
        }
    }

    // The "nothing was generated" probe over the filesystem: the generator writes its output under the
    // environment's output folder (the folder itself, and its web\ folder), and a specification does not.
    // This is evidence, not proof: output that lands deeper (module folders, Java source trees) or is written
    // after the probe is not seen, and a hook that touches the folder while specifying is counted.
    internal static class SpecifyOnlyGeneratorProbe
    {
        private const int Cap = 1000;
        private const int NameCap = 10;

        // Entries at or after startedUtc under outputRoot: files directly in the folder and in its web\ folder,
        // plus folders created inside it (a first web\ appearing is the clearest sign of a first generation) and
        // the folder itself when it was created in the meantime. Count is -1 when there is no folder to look at
        // and 0 when it does not exist (nothing was created).
        public static SpecifyOnlyProbe Probe(string outputRoot, DateTime startedUtc)
        {
            var probe = new SpecifyOnlyProbe();
            if (string.IsNullOrWhiteSpace(outputRoot)) return probe;
            probe.Count = 0;
            if (!System.IO.Directory.Exists(outputRoot)) return probe;

            void Touch(string name)
            {
                probe.Count++;
                if (probe.Names.Count < NameCap) probe.Names.Add(name);
            }

            if (System.IO.Directory.GetCreationTimeUtc(outputRoot) >= startedUtc) Touch("(output folder)");
            foreach (string folder in new[] { outputRoot, System.IO.Path.Combine(outputRoot, "web") })
            {
                if (!System.IO.Directory.Exists(folder)) continue;
                string prefix = string.Equals(folder, outputRoot, StringComparison.OrdinalIgnoreCase) ? string.Empty : "web\\";
                foreach (var file in new System.IO.DirectoryInfo(folder).EnumerateFiles("*", System.IO.SearchOption.TopDirectoryOnly))
                {
                    if (file.LastWriteTimeUtc >= startedUtc) Touch(prefix + file.Name);
                    if (probe.Count >= Cap) return probe;
                }
            }
            foreach (var child in new System.IO.DirectoryInfo(outputRoot).EnumerateDirectories())
            {
                if (child.CreationTimeUtc >= startedUtc) Touch(child.Name + "\\");
                if (probe.Count >= Cap) return probe;
            }
            return probe;
        }

        public static int CountTouchedSince(string outputRoot, DateTime startedUtc)
            => Probe(outputRoot, startedUtc).Count;

        // Names of the files directly inside folder written at or after startedUtc, newest first, at most max.
        // Used to name the specification output a run left behind (the specifier's GXSPC<model> folder).
        public static List<string> ListFilesSince(string folder, DateTime startedUtc, int max)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder)) return names;
            foreach (var file in new System.IO.DirectoryInfo(folder).EnumerateFiles("*", System.IO.SearchOption.TopDirectoryOnly)
                .Where(f => f.LastWriteTimeUtc >= startedUtc)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(0, max)))
                names.Add(file.Name);
            return names;
        }
    }

    internal static class SpecifyOnlyPlanner
    {
        // Types whose environment copy cannot change what a database reorganization would do: they carry no
        // table, attribute or index definitions. Copying any other type into the environment model by itself
        // could hide a pending reorganization (the same reason lifecycle specify refuses to refresh a stale
        // copy), so those are refused instead. SDPanel is left out on purpose: specifying a Smart Devices main
        // object makes the SDK save an offline database object and the object itself. DataProvider is left out
        // too: a Data Provider can populate tables while a reorganization runs, so its copy is not neutral either.
        private static readonly HashSet<string> EnvironmentCopySafeTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "procedure", "webpanel", "api"
            };

        internal static bool AllowsEnvironmentCopy(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return false;
            return EnvironmentCopySafeTypes.Contains(NormalizeTypeName(typeName));
        }

        // Types specify_only does not run at all, whatever the state of their copy: specifying a Smart Devices
        // main object makes the SDK save an offline database object and the object itself, which contradicts
        // "the design object is never written" and has its own impact path.
        private static readonly HashSet<string> UnsupportedTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sdpanel" };

        internal static bool IsTypeSupported(string typeName)
            => string.IsNullOrWhiteSpace(typeName) || !UnsupportedTypes.Contains(NormalizeTypeName(typeName));

        private static string NormalizeTypeName(string typeName)
            => new string(typeName.Where(char.IsLetterOrDigit).ToArray());

        // True when the caller expects nothing (no expectedVersion) or expects the version the object has. The
        // token genexus_read hands out for a part is "<object version>:<content fingerprint>", and the object
        // version is its prefix, so a part read is enough to prove the object did not change since. Exactly one
        // extra segment is accepted: a longer tail is not a token anybody here hands out.
        internal static bool VersionMatches(string expected, string current)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;
            if (current == null) return false;
            string trimmed = expected.Trim();
            if (string.Equals(trimmed, current, StringComparison.Ordinal)) return true;
            string prefix = current + ":";
            return trimmed.StartsWith(prefix, StringComparison.Ordinal)
                && trimmed.Length > prefix.Length
                && trimmed.IndexOf(':', prefix.Length) < 0;
        }

        internal static SpecifyOnlyCopyState ClassifyCopyState(SpecifyOnlyFacts facts)
        {
            if (!facts.SeparateEnvironment) return SpecifyOnlyCopyState.NotApplicable;
            if (!facts.EnvironmentCopyExists) return SpecifyOnlyCopyState.Missing;
            return InProcessBuildRunner.IsEnvironmentCopyStale(facts.DesignLastUpdate, facts.EnvironmentLastUpdate)
                ? SpecifyOnlyCopyState.Stale
                : SpecifyOnlyCopyState.Current;
        }

        public static SpecifyOnlyPlan Plan(SpecifyOnlyRequest request, SpecifyOnlyFacts facts)
        {
            var plan = new SpecifyOnlyPlan { Facts = facts };

            if (!facts.ObjectFound)
            {
                plan.Steps.Add(Step(SpecifyOnlySteps.ResolveObject, false, SpecifyOnlySteps.Failed,
                    "No object with this GUID exists in the design model."));
                Refuse(plan, SpecifyOnlyCodes.ObjectNotFound,
                    "No object with GUID " + request.Guid.ToString("D") + " exists in the Knowledge Base.",
                    "Use the GUID genexus_read, genexus_query or genexus_inspect returns for the object.");
                return plan;
            }

            plan.Steps.Add(Step(SpecifyOnlySteps.ResolveObject, false, SpecifyOnlySteps.Done,
                facts.TypeName + ":" + facts.Name));
            plan.CopyState = ClassifyCopyState(facts);
            plan.TypeAllowsEnvironmentCopy = AllowsEnvironmentCopy(facts.TypeName);

            bool hasExpected = !string.IsNullOrWhiteSpace(request.ExpectedVersion);
            bool versionMatches = VersionMatches(request.ExpectedVersion, facts.Version);
            plan.Steps.Add(Step(SpecifyOnlySteps.CheckConcurrency, false,
                versionMatches ? SpecifyOnlySteps.Done : SpecifyOnlySteps.Failed,
                hasExpected
                    ? (versionMatches ? "expectedVersion matches the current version." : "expectedVersion differs from the current version.")
                    : "No expectedVersion supplied; the version is reported but not enforced."));
            if (!versionMatches)
            {
                Refuse(plan, SpecifyOnlyCodes.VersionConflict,
                    "The object changed since the version the caller read: expected '" + request.ExpectedVersion.Trim()
                    + "', current '" + facts.Version + "'. Nothing was specified.",
                    "Re-read the object (genexus_read) and retry with its current versionToken as expectedVersion, or omit expectedVersion.");
                return plan;
            }

            if (!IsTypeSupported(facts.TypeName))
            {
                Refuse(plan, SpecifyOnlyCodes.TypeNotSupported,
                    "The " + facts.TypeName + " '" + facts.Name + "' cannot be specified by specify_only: specifying a Smart Devices main object makes the SDK save an offline database object and the object itself. Nothing was specified.",
                    "Specify it from the GeneXus IDE, or run genexus_lifecycle action=build target=<object> skipFullDeploy=true.");
                return plan;
            }

            if (facts.InProcessDisabled)
            {
                Refuse(plan, SpecifyOnlyCodes.InProcessDisabled,
                    "The in-process build pipeline is switched off (GXMCP_INPROCESS_BUILD=0). Without it a build task falls back to an external full build, which specify_only must never run. Nothing was specified.",
                    "Unset GXMCP_INPROCESS_BUILD (or set it to anything but 0) and restart the Worker.");
                return plan;
            }
            if (facts.NoTargetModel)
            {
                Refuse(plan, SpecifyOnlyCodes.EnvironmentUnavailable,
                    "The Knowledge Base reports no environment (target) model, so there is no copy of the object for the specifier to read. Nothing was specified.",
                    "Run genexus_kb action=get_environment, and genexus_doctor if it stays empty.");
                return plan;
            }
            if (!facts.SpecifierAvailable)
            {
                Refuse(plan, SpecifyOnlyCodes.SpecifierUnavailable,
                    "The GeneXus specifier service is not available in this Worker.",
                    "Run genexus_doctor; the KB may not be fully open or the SDK may be missing the specifier package.");
                return plan;
            }
            if (!facts.GenerationSuppressible)
            {
                Refuse(plan, SpecifyOnlyCodes.GenerationNotSuppressible,
                    "This GeneXus SDK does not expose the switch that turns off concurrent generation during Specify, so specify_only cannot promise that nothing is generated.",
                    "Use genexus_lifecycle action=specify (which may also generate) or run the Specify command in the GeneXus IDE.");
                return plan;
            }

            if (plan.CopyState == SpecifyOnlyCopyState.Missing || plan.CopyState == SpecifyOnlyCopyState.Stale)
            {
                string kind = plan.CopyState == SpecifyOnlyCopyState.Missing ? "has no copy" : "has a stale copy";
                if (!plan.TypeAllowsEnvironmentCopy)
                {
                    Refuse(plan, SpecifyOnlyCodes.EnvironmentCopyUnsafe,
                        "The " + facts.TypeName + " '" + facts.Name + "' " + kind + " in the environment model, and copying a "
                        + facts.TypeName + " on its own could hide a pending database reorganization. Nothing was specified.",
                        "Run genexus_lifecycle action=build target=<object> skipFullDeploy=true (it checks the reorganization first), then specify_only.");
                    return plan;
                }
                if (!facts.CopyApiAvailable)
                {
                    Refuse(plan, SpecifyOnlyCodes.EnvironmentCopyUnavailable,
                        "The object " + kind + " in the environment model and this GeneXus SDK does not expose the per-object copy needed to create it. Nothing was specified.",
                        "Run genexus_lifecycle action=build target=<object> skipFullDeploy=true once, then specify_only.");
                    return plan;
                }
                plan.NeedsEnvironmentCopy = true;
                plan.Steps.Add(Step(SpecifyOnlySteps.CopyToEnvironment, true, SpecifyOnlySteps.Planned,
                    plan.CopyState == SpecifyOnlyCopyState.Missing
                        ? "Create the object's copy in the environment model (the specifier reads that copy)."
                        : "Refresh the object's stale copy in the environment model (the specifier reads that copy)."));
            }
            else
            {
                plan.Steps.Add(Step(SpecifyOnlySteps.CopyToEnvironment, true, SpecifyOnlySteps.Skipped,
                    plan.CopyState == SpecifyOnlyCopyState.NotApplicable
                        ? "No separate environment model: the specifier reads the design model."
                        : "The environment model already holds a current copy."));
            }

            plan.Steps.Add(Step(SpecifyOnlySteps.Specify, true, SpecifyOnlySteps.Planned,
                "Run the specifier for this object only, with concurrent generation switched off."));
            plan.Steps.Add(Step(SpecifyOnlySteps.VerifyVersion, false, SpecifyOnlySteps.Planned,
                "Re-read the object's version to detect a change made while it was being specified."));
            return plan;
        }

        private static void Refuse(SpecifyOnlyPlan plan, string code, string message, string hint)
        {
            plan.Refusal = new SpecifyOnlyRefusal { Code = code, Message = message, Hint = hint };
            foreach (var name in new[]
            {
                SpecifyOnlySteps.CheckConcurrency, SpecifyOnlySteps.CopyToEnvironment,
                SpecifyOnlySteps.Specify, SpecifyOnlySteps.VerifyVersion
            })
            {
                if (plan.Steps.Any(s => s.Name == name)) continue;
                plan.Steps.Add(Step(name, name == SpecifyOnlySteps.CopyToEnvironment || name == SpecifyOnlySteps.Specify,
                    SpecifyOnlySteps.Skipped, "Not reached: " + code + "."));
            }
        }

        private static SpecifyOnlyStep Step(string name, bool mutates, string state, string detail)
            => new SpecifyOnlyStep { Name = name, Mutates = mutates, State = state, Detail = detail };
    }

    internal sealed class SpecifyOnlyExecutor
    {
        private readonly ISpecifyOnlyEnvironment _environment;
        private readonly Func<DateTime> _clock;

        public SpecifyOnlyExecutor(ISpecifyOnlyEnvironment environment, Func<DateTime> clock = null)
        {
            _environment = environment;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        // Plans without touching anything: reads the facts and decides. Used by dryRun and by the
        // request-time refusal that avoids queueing a task that is bound to be refused.
        public SpecifyOnlyRun Plan(SpecifyOnlyRequest request)
        {
            var run = new SpecifyOnlyRun { DryRun = true, StartedUtc = _clock() };
            SpecifyOnlyFacts facts = SafeReadFacts(request.Guid, run);
            if (facts == null) return run;
            run.Plan = SpecifyOnlyPlanner.Plan(request, facts);
            return run;
        }

        // Runs the plan. Mutations happen only after the planner accepted the request, copy first,
        // specification second; whatever fails stops the sequence and is recorded, not thrown.
        public SpecifyOnlyRun Execute(SpecifyOnlyRequest request)
        {
            var run = new SpecifyOnlyRun { DryRun = false, StartedUtc = _clock() };
            SpecifyOnlyFacts facts = SafeReadFacts(request.Guid, run);
            if (facts == null) return run;
            run.Plan = SpecifyOnlyPlanner.Plan(request, facts);
            if (run.Plan.Refusal != null) return run;

            if (run.Plan.NeedsEnvironmentCopy && !CopyToEnvironment(request, run, facts)) return run;

            try
            {
                run.Specify = _environment.Specify(request.Guid);
            }
            catch (Exception ex)
            {
                run.Specify = new SpecifyOnlyRunResult { Failure = ex.Message };
            }

            if (run.Specify == null || run.Specify.Failure != null || !run.Specify.SpecifierInvoked)
            {
                MarkStep(run, SpecifyOnlySteps.Specify, SpecifyOnlySteps.Failed,
                    run.Specify?.Failure ?? "The specifier was not invoked.");
                MarkStep(run, SpecifyOnlySteps.VerifyVersion, SpecifyOnlySteps.Skipped, "Not reached: the specifier did not run.");
            }
            else
            {
                MarkStep(run, SpecifyOnlySteps.Specify, SpecifyOnlySteps.Done,
                    run.Specify.SpecifierReturned ? "The specifier completed." : "The specifier returned failure.");
                SpecifyOnlyFacts post = null;
                try { post = _environment.ReadFacts(request.Guid); } catch { }
                // A version that could not be read after the run (a failed re-read, an object deleted meanwhile, a
                // token that cannot be computed) is not "unchanged": the result cannot be tied to a version.
                run.VersionAfter = post != null && post.ObjectFound ? post.Version : null;
                MarkStep(run, SpecifyOnlySteps.VerifyVersion,
                    run.VersionVerified ? SpecifyOnlySteps.Done : SpecifyOnlySteps.Failed,
                    post == null ? "The object could not be re-read."
                        : !post.ObjectFound ? "The object no longer exists."
                        : !run.VersionVerified ? "The object's version could not be read, so it cannot be compared."
                        : run.VersionChanged ? "The object changed while it was being specified."
                        : "The version did not change.");
            }

            try { run.GeneratorProbe = _environment.ProbeGeneratorOutput(run.StartedUtc) ?? new SpecifyOnlyProbe(); }
            catch { run.GeneratorProbe = new SpecifyOnlyProbe(); }
            return run;
        }

        // Creates or refreshes the object's copy and checks the result before anything is specified: a copy
        // call that returns is not a copy that exists, and a stale copy that stays stale would have the old
        // source specified and reported as current.
        private bool CopyToEnvironment(SpecifyOnlyRequest request, SpecifyOnlyRun run, SpecifyOnlyFacts before)
        {
            bool created = run.Plan.CopyState == SpecifyOnlyCopyState.Missing;
            run.CopyAction = created ? "created" : "refreshed";
            run.CopyAttempted = true;
            try
            {
                _environment.CopyToEnvironment(request.Guid);
            }
            catch (Exception ex)
            {
                return FailCopy(run, ex.Message,
                    "The SDK could not " + (created ? "create" : "refresh") + " the object's copy in the environment model: " + ex.Message);
            }

            SpecifyOnlyFacts after = SafeReadFacts(request.Guid, run);
            if (after == null)
            {
                MarkStep(run, SpecifyOnlySteps.CopyToEnvironment, SpecifyOnlySteps.Failed, "The copy could not be confirmed: the object could not be re-read.");
                SkipRemaining(run, SpecifyOnlyCodes.ExecutionFailed);
                return false;
            }
            if (!after.EnvironmentCopyExists)
                return FailCopy(run, "The SDK reported success but the environment model still has no copy.",
                    "The copy call returned but the environment model still has no copy of the object.");
            if (InProcessBuildRunner.IsEnvironmentCopyStale(after.DesignLastUpdate, after.EnvironmentLastUpdate))
                return FailCopy(run, "The environment copy is still older than the design object.",
                    "The copy call returned but the environment copy is still older than the design object (design "
                    + FormatUtc(after.DesignLastUpdate) + ", environment " + FormatUtc(after.EnvironmentLastUpdate) + ").");

            run.CopyVerified = true;
            MarkStep(run, SpecifyOnlySteps.CopyToEnvironment, SpecifyOnlySteps.Done, "Environment copy " + run.CopyAction + ".");
            return true;
        }

        private static bool FailCopy(SpecifyOnlyRun run, string stepDetail, string message)
        {
            MarkStep(run, SpecifyOnlySteps.CopyToEnvironment, SpecifyOnlySteps.Failed, stepDetail);
            SkipRemaining(run, SpecifyOnlyCodes.EnvironmentCopyFailed);
            run.Failure = new SpecifyOnlyRefusal
            {
                Code = SpecifyOnlyCodes.EnvironmentCopyFailed,
                Message = message,
                Hint = "Nothing was specified. Run genexus_lifecycle action=build target=<object> skipFullDeploy=true, then specify_only."
            };
            return false;
        }

        private static string FormatUtc(DateTime? value)
            => value.HasValue ? value.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : "none";

        private SpecifyOnlyFacts SafeReadFacts(Guid guid, SpecifyOnlyRun run)
        {
            try
            {
                return _environment.ReadFacts(guid);
            }
            catch (Exception ex)
            {
                run.Failure = new SpecifyOnlyRefusal
                {
                    Code = SpecifyOnlyCodes.ExecutionFailed,
                    Message = "The object could not be read: " + ex.Message,
                    Hint = "Run genexus_doctor; the KB may not be open."
                };
                return null;
            }
        }

        private static void MarkStep(SpecifyOnlyRun run, string name, string state, string detail)
        {
            var step = run.Plan?.Steps.FirstOrDefault(s => s.Name == name);
            if (step == null) return;
            step.State = state;
            step.Detail = detail;
        }

        private static void SkipRemaining(SpecifyOnlyRun run, string code)
        {
            foreach (var step in run.Plan.Steps.Where(s => s.State == SpecifyOnlySteps.Planned).ToList())
            {
                step.State = SpecifyOnlySteps.Skipped;
                step.Detail = "Not reached: " + code + ".";
            }
        }
    }

    internal enum SpecifyOnlyVerdictKind
    {
        Specified,
        Refused,
        ExecutionFailed,
        GenerationObserved,
        Unreachable,
        SpecificationFailed,
        SpecificationNotObserved,
        VersionUnverified,
        ConcurrentChange
    }

    internal sealed class SpecifyOnlyVerdict
    {
        public SpecifyOnlyVerdictKind Kind { get; set; }
        // The code and message of the problem when the verdict is not Specified and the SDK has not already
        // said it with an itemized diagnostic of its own (OwnDiagnostic false).
        public string Code { get; set; }
        public string Message { get; set; }
        public bool OwnDiagnostic { get; set; }
        public bool Succeeded => Kind == SpecifyOnlyVerdictKind.Specified;
    }

    // One reading of a finished run, shared by the receipt and by the task that has to end Failed with the
    // reason: they cannot disagree about what happened.
    internal static class SpecifyOnlyJudge
    {
        // specifierErrors: the specifier itself reported at least one error (an itemized spc####/gen#### line).
        // taskStatus: the status of the task the run belongs to, when there is one: a task that ended Failed,
        // Cancelled or TimedOut (a watchdog, a cancel) is never reported as a pass, whatever the specifier said later.
        public static SpecifyOnlyVerdict Judge(SpecifyOnlyRun run, bool specifierErrors, string taskStatus = null)
        {
            SpecifyOnlyRefusal refusal = run.Plan?.Refusal;
            if (refusal != null)
                return Own(SpecifyOnlyVerdictKind.Refused, refusal.Code, refusal.Message);
            if (run.Failure != null)
                return Own(SpecifyOnlyVerdictKind.ExecutionFailed, run.Failure.Code, run.Failure.Message);

            SpecifyOnlyRunResult spec = run.Specify;
            if (spec == null || spec.Failure != null || !spec.SpecifierInvoked)
                return Own(SpecifyOnlyVerdictKind.ExecutionFailed, SpecifyOnlyCodes.ExecutionFailed,
                    "The specifier did not run: " + (spec?.Failure ?? "it was not invoked") + ".");

            var later = spec.SectionsObserved.Where(SpecifyOnlyReceiptBuilder.IsGenerationOrLaterSection).ToList();
            if (later.Count > 0)
                return Own(SpecifyOnlyVerdictKind.GenerationObserved, SpecifyOnlyCodes.GenerationObserved,
                    "The SDK opened a generation, compilation, reorganization or deploy section during specify_only ("
                    + string.Join(", ", later) + "); the 'nothing was generated' guarantee does not hold for this call.");

            if (spec.ObjectUnreachable)
                return Own(SpecifyOnlyVerdictKind.Unreachable, SpecifyOnlyCodes.ObjectUnreachable,
                    "GeneXus did not specify the object: no Main program reaches it (spc0217), and an unreachable object is skipped even when named explicitly. "
                    + "Give it a caller, or set it as a Main program, and run specify_only again.");

            if (specifierErrors)
                return new SpecifyOnlyVerdict { Kind = SpecifyOnlyVerdictKind.SpecificationFailed, OwnDiagnostic = false };
            if (!spec.SpecifierReturned)
                return Own(SpecifyOnlyVerdictKind.SpecificationFailed, SpecifyOnlyCodes.SpecificationFailed,
                    "The specifier returned failure without an itemized diagnostic.");

            if (IsFailedTaskStatus(taskStatus))
                return Own(SpecifyOnlyVerdictKind.ExecutionFailed, SpecifyOnlyCodes.ExecutionFailed,
                    "The task ended as " + taskStatus + " while the specifier was running, so its result is not reported as a pass. "
                    + "The SDK may have finished the specification afterwards; run specify_only again to confirm.");

            // The specifier said "done" without errors: something must also show it analyzed the object (a Specification
            // section, or specification files written), or the call may have been a no-op (an object the specifier
            // does not process, a captured output that stayed empty).
            if (!SpecifyOnlyReceiptBuilder.SpecificationObserved(spec))
                return Own(SpecifyOnlyVerdictKind.SpecificationNotObserved, SpecifyOnlyCodes.SpecificationNotObserved,
                    "The specifier returned success but nothing shows it analyzed the object: no Specification section was observed and no specification file was written. "
                    + "The object may be of a type the specifier does not process. Run genexus_lifecycle action=build target=<object> skipFullDeploy=true to specify it with the build.");

            if (!run.VersionVerified)
                return Own(SpecifyOnlyVerdictKind.VersionUnverified, SpecifyOnlyCodes.VersionUnverified,
                    "The object's version could not be read after the specification (the re-read failed, or the object no longer exists), so the result cannot be tied to a version. Run again.");

            if (run.VersionChanged)
                return Own(SpecifyOnlyVerdictKind.ConcurrentChange, SpecifyOnlyCodes.ConcurrentChange,
                    "The object's version changed while it was being specified (version " + run.Plan.Facts.Version + " -> " + run.VersionAfter
                    + "), by another writer or by the SDK itself; the result does not describe its current content. Run again.");

            return new SpecifyOnlyVerdict { Kind = SpecifyOnlyVerdictKind.Specified };
        }

        private static SpecifyOnlyVerdict Own(SpecifyOnlyVerdictKind kind, string code, string message)
            => new SpecifyOnlyVerdict { Kind = kind, Code = code, Message = message, OwnDiagnostic = true };

        internal static bool IsFailedTaskStatus(string status)
            => string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "TimedOut", StringComparison.OrdinalIgnoreCase);
    }

    internal static class SpecifyOnlyReceiptBuilder
    {
        internal const string OutcomeWouldSpecify = "WouldSpecify";
        internal const string OutcomeWouldRefuse = "WouldRefuse";

        private const string RollbackReason =
            "specify_only does not write the design object, so there is nothing of the caller's to restore. "
            + "The environment copy and the specification output are derived from the design object; the SDK exposes no "
            + "operation to remove one object's copy, and running specify_only again converges on the same state.";

        private const string ReferencesNote =
            "The specifier resolves what this object references against the environment model, where each referenced object is as of its own last build or copy.";

        // statusDiagnostics: the specifier's own diagnostics, parsed from the task's error lines.
        public static JObject Build(SpecifyOnlyRequest request, SpecifyOnlyRun run, JArray statusDiagnostics, string taskStatus = null)
        {
            SpecifyOnlyPlan plan = run.Plan;
            SpecifyOnlyFacts facts = plan?.Facts;
            SpecifyOnlyVerdict verdict = SpecifyOnlyJudge.Judge(run, HasError(statusDiagnostics), taskStatus);
            var diagnostics = new JArray();
            if (verdict.OwnDiagnostic)
                diagnostics.Add(OwnDiagnostic("error", verdict.Code, verdict.Message));
            // Files appearing in the generator output folder are an observation, not a verdict: the SDK's
            // specification hooks can write there too. Say so instead of leaving it to the evidence block alone.
            if (run.GeneratorProbe != null && run.GeneratorProbe.Count > 0)
                diagnostics.Add(OwnDiagnostic("warning", SpecifyOnlyCodes.GeneratorOutputTouched,
                    run.GeneratorProbe.Count + " entr" + (run.GeneratorProbe.Count == 1 ? "y" : "ies")
                    + " changed in the generator output folder during the call (" + string.Join(", ", run.GeneratorProbe.Names)
                    + "). specify_only does not generate, and the SDK's specification hooks can write there; see evidence.generatorOutputTouchedNames."));

            var receipt = new JObject
            {
                ["operation"] = "specify_only",
                ["dryRun"] = false,
                ["outcome"] = verdict.Kind.ToString(),
                ["specified"] = verdict.Succeeded,
                ["target"] = Target(request, facts),
                ["version"] = Version(request, facts, run.VersionAfter, run),
                ["environmentCopy"] = EnvironmentCopy(plan, run),
                ["steps"] = Steps(plan),
                ["stepsExecuted"] = new JArray((plan?.Steps ?? new List<SpecifyOnlyStep>())
                    .Where(s => s.State == SpecifyOnlySteps.Done).Select(s => (object)s.Name).ToArray()),
                ["stepsNotExecuted"] = NeverRun(run),
                ["designObjectVersionChanged"] = run.VersionAfter == null || facts?.Version == null ? JValue.CreateNull() : (JToken)run.VersionChanged,
                ["persisted"] = Persisted(plan, run),
                ["rollback"] = Rollback(run),
                ["diagnostics"] = Merge(diagnostics, statusDiagnostics),
                ["evidence"] = Evidence(run)
            };
            SpecifyOnlyRefusal refusal = plan?.Refusal ?? run.Failure;
            if (refusal != null) receipt["refusal"] = Refusal(refusal);
            return receipt;
        }

        public static string OutcomeOf(SpecifyOnlyRun run, JArray statusDiagnostics, string taskStatus = null)
            => SpecifyOnlyJudge.Judge(run, HasError(statusDiagnostics), taskStatus).Kind.ToString();

        // Something shows the specifier worked on the object: it opened a Specification section (the protocol
        // GenexusBLServices.Specifier writes to the output service) or it wrote specification files.
        internal static bool SpecificationObserved(SpecifyOnlyRunResult spec)
            => spec != null
               && (spec.SpecificationFiles.Count > 0
                   || spec.SectionsObserved.Any(s => s != null && s.IndexOf("Specification", StringComparison.OrdinalIgnoreCase) >= 0));

        public static JObject BuildDryRun(SpecifyOnlyRequest request, SpecifyOnlyRun run)
        {
            SpecifyOnlyPlan plan = run.Plan;
            SpecifyOnlyFacts facts = plan?.Facts;
            SpecifyOnlyRefusal refusal = plan?.Refusal ?? run.Failure;
            var receipt = new JObject
            {
                ["operation"] = "specify_only",
                ["dryRun"] = true,
                ["outcome"] = refusal == null ? OutcomeWouldSpecify : OutcomeWouldRefuse,
                ["specified"] = JValue.CreateNull(),
                ["target"] = Target(request, facts),
                ["version"] = Version(request, facts, null, run),
                ["environmentCopy"] = EnvironmentCopy(plan, run),
                ["steps"] = Steps(plan),
                ["stepsExecuted"] = new JArray(),
                ["stepsNotExecuted"] = NeverRun(null),
                ["wouldPersist"] = WouldPersist(plan),
                ["persisted"] = new JArray(),
                ["rollback"] = new JObject
                {
                    ["applicable"] = plan != null && refusal == null,
                    ["performed"] = false,
                    ["reason"] = RollbackReason
                },
                ["evidence"] = new JObject
                {
                    ["mutationPerformed"] = false,
                    ["generationWillBeSuppressed"] = facts != null && facts.GenerationSuppressible
                }
            };
            if (refusal != null) receipt["refusal"] = Refusal(refusal);
            return receipt;
        }

        // The evidence is a set of observations, not a proof: generation is switched off by construction, and
        // this says whether anything the call observed contradicts that.
        internal static bool NoGenerationObserved(SpecifyOnlyRun run)
        {
            SpecifyOnlyRunResult spec = run.Specify;
            if (spec == null || !spec.SpecifierInvoked) return false;
            return spec.GenerationSuppressed
                && !spec.SectionsObserved.Any(IsGenerationOrLaterSection)
                && run.GeneratorProbe != null && run.GeneratorProbe.Count == 0;
        }

        // Section names the SDK opens ("Specification", "Default (.NET Framework) Generation", "... Compilation").
        internal static bool IsGenerationOrLaterSection(string section)
        {
            if (string.IsNullOrWhiteSpace(section)) return false;
            return section.IndexOf("Generat", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Compil", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Reorg", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Impact", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Creation Analysis", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Copy Model", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static JObject Evidence(SpecifyOnlyRun run)
        {
            SpecifyOnlyRunResult spec = run.Specify;
            SpecifyOnlyProbe probe = run.GeneratorProbe ?? new SpecifyOnlyProbe();
            return new JObject
            {
                ["specifierInvoked"] = spec != null && spec.SpecifierInvoked,
                ["generationSuppressed"] = spec != null && spec.GenerationSuppressed,
                ["rebuildApplied"] = spec != null && spec.RebuildApplied,
                ["sdkSectionsObserved"] = new JArray((spec?.SectionsObserved ?? new List<string>()).Cast<object>().ToArray()),
                ["generationOrCompilationSectionObserved"] = spec != null && spec.SectionsObserved.Any(IsGenerationOrLaterSection),
                ["generatorOutputTouched"] = probe.Count < 0 ? (JToken)JValue.CreateNull() : probe.Count,
                ["generatorOutputTouchedNames"] = new JArray(probe.Names.Cast<object>().ToArray()),
                ["noGenerationObserved"] = NoGenerationObserved(run)
            };
        }

        private static JArray Merge(JArray own, JArray sdk)
        {
            var merged = new JArray();
            foreach (var d in own) merged.Add(d);
            if (sdk != null) foreach (var d in sdk) merged.Add(d);
            return merged;
        }

        private static JObject OwnDiagnostic(string severity, string code, string message)
            => new JObject { ["severity"] = severity, ["code"] = code, ["message"] = message, ["source"] = "specify_only" };

        private static bool HasError(JArray diagnostics)
            => diagnostics != null && diagnostics.OfType<JObject>().Any(d =>
                !string.Equals(d["severity"]?.ToString(), "warning", StringComparison.OrdinalIgnoreCase));

        private static JObject Target(SpecifyOnlyRequest request, SpecifyOnlyFacts facts)
        {
            var target = new JObject { ["guid"] = request.Guid.ToString("D") };
            if (facts != null && facts.ObjectFound)
            {
                target["name"] = facts.Name;
                target["type"] = facts.TypeName;
            }
            return target;
        }

        private static JObject Version(SpecifyOnlyRequest request, SpecifyOnlyFacts facts, string after, SpecifyOnlyRun run)
        {
            string current = facts?.Version;
            bool hasExpected = !string.IsNullOrWhiteSpace(request.ExpectedVersion);
            var version = new JObject
            {
                ["expected"] = hasExpected ? (JToken)request.ExpectedVersion.Trim() : JValue.CreateNull(),
                ["current"] = current == null ? JValue.CreateNull() : (JToken)current,
                ["matchesExpected"] = hasExpected && current != null
                    ? (JToken)SpecifyOnlyPlanner.VersionMatches(request.ExpectedVersion, current)
                    : JValue.CreateNull()
            };
            if (!run.DryRun)
            {
                version["after"] = after == null ? JValue.CreateNull() : (JToken)after;
                version["unchangedDuringRun"] = current != null && after != null
                    ? (JToken)string.Equals(current, after, StringComparison.Ordinal)
                    : JValue.CreateNull();
            }
            return version;
        }

        private static JObject EnvironmentCopy(SpecifyOnlyPlan plan, SpecifyOnlyRun run)
        {
            string action = run.CopyPerformed
                ? run.CopyAction
                : run.CopyAttempted
                    ? "attempted-unverified"
                    : plan != null && plan.NeedsEnvironmentCopy && run.DryRun ? "wouldCreateOrRefresh" : "none";
            return new JObject
            {
                ["state"] = plan == null || plan.Facts == null || !plan.Facts.ObjectFound ? "unknown" : StateName(plan.CopyState),
                ["typeEligibleForCopy"] = plan != null && plan.TypeAllowsEnvironmentCopy,
                ["action"] = action,
                ["referencesNote"] = ReferencesNote
            };
        }

        private static string StateName(SpecifyOnlyCopyState state)
        {
            switch (state)
            {
                case SpecifyOnlyCopyState.NotApplicable: return "notApplicable";
                case SpecifyOnlyCopyState.Current: return "current";
                case SpecifyOnlyCopyState.Missing: return "missing";
                default: return "stale";
            }
        }

        private static JArray Steps(SpecifyOnlyPlan plan)
        {
            var steps = new JArray();
            if (plan == null) return steps;
            foreach (var s in plan.Steps)
                steps.Add(new JObject { ["name"] = s.Name, ["mutates"] = s.Mutates, ["state"] = s.State, ["detail"] = s.Detail });
            return steps;
        }

        // The steps the operation never runs. When the SDK opened a section that belongs to one of them, the
        // entry says so instead of repeating the promise.
        private static JArray NeverRun(SpecifyOnlyRun run)
        {
            var observed = run?.Specify?.SectionsObserved ?? new List<string>();
            var list = new JArray();
            foreach (var pair in SpecifyOnlySteps.NeverRun)
            {
                var contradicting = observed.Where(section => SectionBelongsTo(pair.Key, section)).ToList();
                var entry = new JObject { ["step"] = pair.Key };
                if (contradicting.Count == 0)
                {
                    entry["reason"] = pair.Value;
                }
                else
                {
                    entry["reason"] = "Not part of specify_only, but the SDK opened: " + string.Join(", ", contradicting) + ".";
                    entry["contradictedBy"] = new JArray(contradicting.Cast<object>().ToArray());
                }
                list.Add(entry);
            }
            return list;
        }

        private static bool SectionBelongsTo(string step, string section)
        {
            if (string.IsNullOrWhiteSpace(section)) return false;
            switch (step)
            {
                case "generate": return section.IndexOf("Generat", StringComparison.OrdinalIgnoreCase) >= 0;
                case "compile": return section.IndexOf("Compil", StringComparison.OrdinalIgnoreCase) >= 0;
                case "reorganize":
                    return section.IndexOf("Reorg", StringComparison.OrdinalIgnoreCase) >= 0
                        || section.IndexOf("Impact", StringComparison.OrdinalIgnoreCase) >= 0
                        || section.IndexOf("Creation Analysis", StringComparison.OrdinalIgnoreCase) >= 0;
                case "deploy": return section.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0;
                default: return false;
            }
        }

        private static JArray WouldPersist(SpecifyOnlyPlan plan)
        {
            var list = new JArray();
            if (plan == null || plan.Refusal != null) return list;
            if (plan.NeedsEnvironmentCopy)
                list.Add(new JObject { ["kind"] = "environment-copy", ["action"] = plan.CopyState == SpecifyOnlyCopyState.Missing ? "create" : "refresh" });
            list.Add(new JObject { ["kind"] = "specification-output", ["scope"] = "environment model" });
            return list;
        }

        private static JArray Persisted(SpecifyOnlyPlan plan, SpecifyOnlyRun run)
        {
            var list = new JArray();
            if (run.CopyAttempted)
            {
                var copy = new JObject
                {
                    ["kind"] = "environment-copy",
                    ["action"] = run.CopyAction,
                    ["object"] = plan.Facts.TypeName + ":" + plan.Facts.Name,
                    ["verified"] = run.CopyVerified
                };
                if (!run.CopyVerified)
                    copy["note"] = "The SDK call was made but a re-read did not confirm the result; the copy may be absent, stale or partial.";
                list.Add(copy);
            }
            if (run.Specify != null && run.Specify.SpecifierInvoked)
                list.Add(new JObject
                {
                    ["kind"] = "specification-output",
                    ["scope"] = "environment model",
                    ["files"] = new JArray(run.Specify.SpecificationFiles.Cast<object>().ToArray()),
                    ["note"] = "The specification results the specifier keeps for the object; the same store a build's specification step writes. 'files' names what it wrote in its output folder during this call."
                });
            return list;
        }

        private static JObject Rollback(SpecifyOnlyRun run)
        {
            bool persistedAnything = run.CopyAttempted || (run.Specify != null && run.Specify.SpecifierInvoked);
            return new JObject
            {
                ["applicable"] = persistedAnything,
                ["performed"] = false,
                ["reason"] = persistedAnything ? RollbackReason : "Nothing was persisted."
            };
        }

        private static JObject Refusal(SpecifyOnlyRefusal refusal)
        {
            var o = new JObject { ["code"] = refusal.Code, ["message"] = refusal.Message };
            if (!string.IsNullOrEmpty(refusal.Hint)) o["hint"] = refusal.Hint;
            return o;
        }
    }
}
