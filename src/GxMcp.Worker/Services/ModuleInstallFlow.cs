using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    internal interface IModuleInstallBackend
    {
        void ValidateDestination();
        ModuleInstallSnapshot Read(ModuleInstallPackage package);
        bool Install(ModuleInstallPackage package);
    }

    internal sealed class ModuleInstallSnapshot
    {
        internal bool ModuleExists { get; set; }
        internal string Version { get; set; }
        internal JObject Inventory { get; set; }
        internal bool MatchesPackage { get; set; }
        internal string Conflict { get; set; }
    }

    // The caller holds the KB mutation lease throughout this flow. No retry or compensating write.
    internal static class ModuleInstallFlow
    {
        internal static JObject Run(IReadOnlyList<ModuleInstallPackage> packages, bool dryRun, IModuleInstallBackend backend)
        {
            if (packages == null || packages.Count == 0) throw new ArgumentException("A dependency plan is required.", nameof(packages));
            var baseline = new ModuleInstallSnapshot[packages.Count];
            var current = new ModuleInstallSnapshot[packages.Count];
            var attempted = new JArray();
            string stage = "destination";
            bool writeAttempted = false;
            try
            {
                backend.ValidateDestination();
                stage = "readBefore";
                for (int i = 0; i < packages.Count; i++) baseline[i] = current[i] = Read(backend, packages[i]);
                for (int i = 0; i < packages.Count; i++)
                {
                    if (!string.IsNullOrEmpty(current[i].Conflict))
                        return Receipt("ModuleInstallConflict", false, dryRun, packages, baseline, current, attempted, stage);
                    if (current[i].ModuleExists && (!current[i].MatchesPackage || current[i].Version != packages[i].Metadata.Version))
                        return Receipt("ModuleInstallConflict", false, dryRun, packages, baseline, current, attempted, stage);
                }
                if (dryRun) return Receipt("ModuleInstallDryRun", true, true, packages, baseline, current, attempted, "plan");

                for (int i = 0; i < packages.Count; i++)
                {
                    // Validate even a no-op: a stale destination must never be reported as verified.
                    stage = "recheck";
                    backend.ValidateDestination();
                    for (int j = 0; j < packages.Count; j++)
                    {
                        ModuleInstallSnapshot fresh = Read(backend, packages[j]);
                        if (!Equivalent(current[j], fresh))
                        {
                            current[j] = fresh;
                            ReadAll(backend, packages, current);
                            return Receipt("ModuleInstallConcurrentChange", false, false, packages, baseline, current, attempted, stage);
                        }
                        current[j] = fresh;
                    }
                    if (current[i].MatchesPackage) continue;
                    stage = "install";
                    writeAttempted = true;
                    attempted.Add(packages[i].Metadata.Name);
                    bool accepted = backend.Install(packages[i]);
                    stage = "readback";
                    bool known = ReadAll(backend, packages, current);
                    if (!accepted || !known || !current[i].MatchesPackage)
                        return Receipt("ModuleInstallNotVerified", false, false, packages, baseline, current, attempted, stage);
                    // Dependencies already installed must remain verified; pending packages must remain unchanged.
                    for (int j = 0; j < packages.Count; j++)
                    {
                        if (!OutsidePlanUnchanged(baseline[j], current[j]) || (j <= i ? !current[j].MatchesPackage : !Equivalent(baseline[j], current[j])))
                            return Receipt("ModuleInstallUnexpectedChange", false, false, packages, baseline, current, attempted, stage);
                    }
                }
                return Receipt("ModuleInstalled", true, false, packages, baseline, current, attempted, "complete");
            }
            catch (Exception ex)
            {
                if (writeAttempted) ReadAll(backend, packages, current);
                var result = Receipt(ex is ModuleInstallPlanException planned ? planned.Code : "ModuleInstallFailed", false, dryRun, packages, baseline, current, attempted, stage);
                result["diagnostic"] = new JObject { ["exceptionType"] = ex.GetType().Name };
                if (ex is ModuleInstallPlanException) result["diagnostic"]["message"] = ex.Message;
                result["diagnostic"]["callSites"] = new JArray((new StackTrace(ex, false).GetFrames() ?? Array.Empty<StackFrame>())
                    .Select(f => f.GetMethod()).Where(m => m?.DeclaringType != null).Take(8)
                    .Select(m => m.DeclaringType.FullName + "." + m.Name));
                if (ex is ArgumentException argument && Regex.IsMatch(argument.ParamName ?? "", "^[A-Za-z_][A-Za-z0-9_]{0,79}$"))
                    result["diagnostic"]["parameter"] = argument.ParamName;
                return result;
            }
        }

        private static ModuleInstallSnapshot Read(IModuleInstallBackend backend, ModuleInstallPackage package)
        {
            ModuleInstallSnapshot result = backend.Read(package);
            if (result?.Inventory == null) throw new InvalidOperationException("An independent inventory is required.");
            // Freeze SDK-backed/dynamic data before the next operation.
            return new ModuleInstallSnapshot { ModuleExists = result.ModuleExists, Version = result.Version,
                MatchesPackage = result.ModuleExists && result.Version == package.Metadata.Version && result.MatchesPackage && string.IsNullOrEmpty(result.Conflict),
                Conflict = result.Conflict, Inventory = (JObject)result.Inventory.DeepClone() };
        }

        private static bool ReadAll(IModuleInstallBackend backend, IReadOnlyList<ModuleInstallPackage> packages, ModuleInstallSnapshot[] destination)
        {
            bool known = true;
            for (int i = 0; i < packages.Count; i++)
            {
                try { destination[i] = Read(backend, packages[i]); }
                catch { destination[i] = null; known = false; }
            }
            return known;
        }

        private static bool Equivalent(ModuleInstallSnapshot left, ModuleInstallSnapshot right) =>
            left != null && right != null && left.ModuleExists == right.ModuleExists && left.Version == right.Version &&
            left.MatchesPackage == right.MatchesPackage && left.Conflict == right.Conflict && JToken.DeepEquals(left.Inventory, right.Inventory);

        private static bool OutsidePlanUnchanged(ModuleInstallSnapshot left, ModuleInstallSnapshot right) =>
            left != null && right != null
            && JToken.DeepEquals(left.Inventory["outsidePlanObjectCount"], right.Inventory["outsidePlanObjectCount"])
            && JToken.DeepEquals(left.Inventory["outsidePlanLatestUpdate"], right.Inventory["outsidePlanLatestUpdate"]);

        private static JObject Receipt(string code, bool success, bool dryRun, IReadOnlyList<ModuleInstallPackage> packages,
            ModuleInstallSnapshot[] before, ModuleInstallSnapshot[] after, JArray attempted, string stage)
        {
            bool outsideChanged = Enumerable.Range(0, packages.Count).Any(i => before[i] != null && after[i] != null && !OutsidePlanUnchanged(before[i], after[i]));
            bool known = after.All(s => s != null) && !outsideChanged;
            bool matches = known && after.All(s => s.MatchesPackage);
            bool changed = Enumerable.Range(0, packages.Count).Any(i => before[i] != null && after[i] != null && !JToken.DeepEquals(before[i].Inventory, after[i].Inventory));
            var plan = new JArray();
            var inventory = new JArray();
            for (int i = 0; i < packages.Count; i++)
            {
                var package = packages[i];
                plan.Add(new JObject
                {
                    ["module"] = package.Metadata.Name, ["version"] = package.Metadata.Version, ["sha256"] = package.Sha256,
                    ["action"] = before[i]?.MatchesPackage == true ? "alreadyInstalled" : "install", ["installedVersion"] = before[i]?.Version,
                    ["objects"] = new JArray(package.Objects.Select(o => new JObject { ["guid"] = o.Guid.ToString(), ["name"] = o.Name, ["typeGuid"] = o.TypeGuid.ToString() }))
                });
                inventory.Add(new JObject { ["module"] = package.Metadata.Name, ["known"] = after[i] != null,
                    ["matches"] = after[i]?.MatchesPackage == true, ["conflictCode"] = after[i]?.Conflict,
                    ["before"] = before[i]?.Inventory?.DeepClone(), ["after"] = after[i]?.Inventory?.DeepClone() });
            }
            ModuleInstallPackage target = packages[packages.Count - 1];
            return new JObject
            {
                ["status"] = success ? "ok" : "error", ["code"] = code, ["stage"] = stage,
                ["module"] = target.Metadata.Name, ["version"] = target.Metadata.Version,
                ["dryRun"] = dryRun, ["noMutation"] = attempted.Count == 0,
                ["persisted"] = dryRun ? new JValue(false) : known ? new JValue(matches || changed) : JValue.CreateNull(),
                ["persistedStateKnown"] = known, ["verifiedByReadback"] = !dryRun && matches,
                ["partialPersistenceDetected"] = changed && !matches, ["stateChangedSincePlan"] = changed, ["retryable"] = false,
                ["outsidePlanChangeDetected"] = outsideChanged,
                ["attemptedModules"] = attempted, ["plan"] = plan, ["inventory"] = inventory,
                ["dependencies"] = new JArray(packages.Take(packages.Count - 1).Select(p => new JObject { ["name"] = p.Metadata.Name, ["version"] = p.Metadata.Version })),
                ["warnings"] = new JArray(target.ResolutionWarnings),
                ["rollback"] = new JObject { ["attempted"] = false, ["verified"] = false, ["supported"] = false },
                ["recoveryActions"] = success ? new JArray() : new JArray("Inspect the reported independent inventory before any explicit recovery; do not automatically retry installation."),
                ["implicitLifecycleOperations"] = new JArray()
            };
        }
    }
}
