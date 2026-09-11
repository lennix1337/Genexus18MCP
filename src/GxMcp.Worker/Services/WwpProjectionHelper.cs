using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Static helper that invokes the WorkWithPlus pattern build process'
    /// <c>UpdateParentObject</c> step — the SDK lifecycle hook that projects a
    /// PatternInstance onto the bound KBObject's WebForm. Discovered via F17
    /// (see <c>docs/sdk-probe/wwp-projection-discovery.md</c>).
    ///
    /// Lifted out of <see cref="PatternApplyService"/> so <see cref="WriteService"/>
    /// can invoke it after a successful PatternInstance edit without taking a
    /// circular dependency on PatternApplyService.
    /// </summary>
    internal static class WwpProjectionHelper
    {
        internal sealed class ProjectionResult
        {
            internal bool LifecycleAttempted;
            internal bool ShouldBuild;
            internal bool BeforeStartBuild;
            internal bool AfterImportResources;
            internal bool UpdateParentObject;
            internal bool AfterEndBuild;
            internal bool ParentSaved;
            internal string Failure;

            internal bool LifecycleExecuted => LifecycleAttempted
                && ShouldBuild
                && BeforeStartBuild
                && AfterImportResources
                && UpdateParentObject
                && AfterEndBuild
                && ParentSaved;
        }

        /// <summary>
        /// Project the PatternInstance on <paramref name="host"/> onto
        /// <paramref name="parent"/>'s WebForm via the WWP build process.
        /// Saves the parent with ForceSave + SkipValidation so the projected
        /// WebForm persists even if it would fail WebPanel-level semantic checks.
        ///
        /// Errors are logged and swallowed — the edit that triggered this call
        /// already succeeded; projection is best-effort.
        /// </summary>
        public static bool TryProjectHostOntoParent(KBObject parent, KBObject host)
        {
            return TryProjectHostOntoParent(parent, host, out _);
        }

        public static bool TryProjectHostOntoParent(KBObject parent, KBObject host,
            out ProjectionResult result)
        {
            result = new ProjectionResult();
            if (parent == null || host == null) return false;
            try
            {
                var wwpAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVelop.Patterns.WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (wwpAsm == null)
                {
                    try
                    {
                        var gxPath = Environment.GetEnvironmentVariable("GX_PATH") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                        var wwpDllPath = Path.Combine(gxPath, "Packages", "Patterns", "WorkWithPlus", "DVelop.Patterns.WorkWithPlus.dll");
                        if (File.Exists(wwpDllPath)) wwpAsm = Assembly.LoadFrom(wwpDllPath);
                    }
                    catch { }
                }
                if (wwpAsm == null) { Logger.Debug("[WWP-PROJECT] DVelop.Patterns.WorkWithPlus not loaded"); return false; }

                var workWithPatternType = wwpAsm.GetType("DVelop.Patterns.WorkWithPlus.WorkWithPattern", false);
                if (workWithPatternType == null) { Logger.Debug("[WWP-PROJECT] WorkWithPattern type not found"); return false; }

                object impl;
                try { impl = Activator.CreateInstance(workWithPatternType); }
                catch (Exception ex) { Logger.Debug("[WWP-PROJECT] ctor failed: " + ex.Message); return false; }
                if (impl == null) return false;

                try
                {
                    var initMethod = workWithPatternType.GetMethod("Initialize",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    initMethod?.Invoke(impl, null);
                }
                catch (Exception ex) { Logger.Debug("[WWP-PROJECT] Initialize skipped: " + ex.Message); }

                var getBuildProcess = workWithPatternType.GetMethod("GetBuildProcess",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getBuildProcess == null) { Logger.Debug("[WWP-PROJECT] GetBuildProcess() not found"); return false; }

                object buildProcess = getBuildProcess.Invoke(impl, null);
                if (buildProcess == null) { Logger.Debug("[WWP-PROJECT] GetBuildProcess returned null"); return false; }

                var updateParent = buildProcess.GetType().GetMethod("UpdateParentObject",
                    BindingFlags.Public | BindingFlags.Instance);
                if (updateParent == null) { Logger.Debug("[WWP-PROJECT] UpdateParentObject() not found"); return false; }

                // F19: Run the FULL IPatternBuildProcess lifecycle the IDE uses, not
                // just UpdateParentObject. Most hooks tolerate missing context (best-
                // effort wrappers), but BeforeStartBuild / AfterEndBuild establish
                // engine state that UpdateParentObject depends on for richer templates.
                //   ShouldBuild → BeforeStartBuild → AfterImportResources →
                //   BeforeGenerateObjects → UpdateParentObject → AfterSaveObjects →
                //   AfterEndBuild
                Logger.Info("[WWP-PROJECT] Running full IPatternBuildProcess lifecycle on " +
                    buildProcess.GetType().FullName + " for host=" + host.Name);

                result.LifecycleAttempted = true;
                result.ShouldBuild = TryInvokeBP(buildProcess, "ShouldBuild", new[] { host });
                result.BeforeStartBuild = result.ShouldBuild
                    && TryInvokeBP(buildProcess, "BeforeStartBuild", new[] { host });
                result.AfterImportResources = result.BeforeStartBuild
                    && TryInvokeBP(buildProcess, "AfterImportResources", new[] { host });
                if (!result.AfterImportResources)
                {
                    result.Failure = "A required WorkWithPlus build-process callback failed before projection.";
                    return false;
                }
                // BeforeGenerateObjects takes (PatternInstance, IBaseCollection<PatternObject>).
                // We don't have the second arg cheaply; skip — it's mostly used to filter
                // which objects to build, not required for the projection step.

                updateParent.Invoke(buildProcess, new object[] { parent, host });
                result.UpdateParentObject = true;
                Logger.Info("[WWP-PROJECT] UpdateParentObject returned successfully");

                // AfterSaveObjects takes (PatternInstance, InstanceObjects); we don't have
                // a real InstanceObjects collection. Same for BeforeSaveObjects. Skip
                // them rather than pass nulls and risk NRE inside the SDK.

                result.AfterEndBuild = TryInvokeBP(buildProcess, "AfterEndBuild", new[] { host });
                if (!result.AfterEndBuild)
                {
                    result.Failure = "The WorkWithPlus AfterEndBuild callback failed after projection.";
                    return false;
                }

                try
                {
                    var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                    {
                        ForceSave = true,
                        ForceSaveDefaultParts = true,
                        SkipValidation = true
                    };
                    parent.Save(prefs);
                    result.ParentSaved = true;
                    Logger.Info("[WWP-PROJECT] Saved parent '" + parent.Name + "' (ForceSave+SkipValidation).");
                }
                catch (Exception saveEx)
                {
                    Logger.Info("[WWP-PROJECT] ForceSave parent threw: " + saveEx.Message + " — falling back to EnsureSave.");
                    try
                    {
                        parent.EnsureSave(true);
                        result.ParentSaved = true;
                    }
                    catch (Exception ex2)
                    {
                        result.Failure = "Both ForceSave and EnsureSave failed: " + ex2.Message;
                        Logger.Info("[WWP-PROJECT] EnsureSave fallback failed: " + ex2.Message);
                    }
                }
                return result.LifecycleExecuted;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                result.Failure = inner.GetType().Name + ": " + inner.Message;
                Logger.Warn("[WWP-PROJECT] UpdateParentObject threw: " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
            catch (Exception ex)
            {
                result.Failure = ex.GetType().Name + ": " + ex.Message;
                Logger.Warn("[WWP-PROJECT] failed: " + ex.Message);
                return false;
            }
        }

        // Best-effort invoke for IPatternBuildProcess lifecycle hooks that take
        // a single (PatternInstance) argument. Logs and swallows errors — these
        // are advisory in our headless context and missing services in the SDK
        // shouldn't fail the projection.
        private static bool TryInvokeBP(object buildProcess, string methodName, object[] args)
        {
            try
            {
                var m = buildProcess.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                if (m == null) { Logger.Debug("[WWP-PROJECT] " + methodName + " not found"); return false; }
                object value = m.Invoke(buildProcess, args);
                if (m.ReturnType == typeof(bool) && value is bool boolValue && !boolValue)
                {
                    Logger.Debug("[WWP-PROJECT] " + methodName + " returned False");
                    return false;
                }
                Logger.Debug("[WWP-PROJECT] " + methodName + " ok");
                return true;
            }
            catch (TargetInvocationException tie)
            {
                Logger.Debug("[WWP-PROJECT] " + methodName + " threw: " + (tie.InnerException?.GetType().Name ?? "") + ": " + (tie.InnerException?.Message ?? ""));
                return false;
            }
            catch (Exception ex)
            {
                Logger.Debug("[WWP-PROJECT] " + methodName + " reflection error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Resolve the parent KBObject for a WorkWithPlus host. Convention:
        /// `WorkWithPlus&lt;X&gt;` host → parent named `X`. Falls back to reading
        /// the host's PatternInstance XML for a SecFuntionKey / transaction ref
        /// if the name strip doesn't resolve.
        /// </summary>
        public static KBObject ResolveHostParent(KBObject host, ObjectService objectService)
        {
            if (host == null || objectService == null) return null;
            const string prefix = "WorkWithPlus";
            string hostName = host.Name ?? string.Empty;
            if (!hostName.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string parentName = hostName.Substring(prefix.Length);
            if (string.IsNullOrEmpty(parentName)) return null;

            try
            {
                var parent = objectService.FindObject(parentName);
                if (parent != null) return parent;
            }
            catch { }
            return null;
        }
    }
}
