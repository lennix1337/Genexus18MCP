using System;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_merge — WRITE surface over the GeneXus SDK's
    /// <see cref="IMergeService"/> (Artech.Architecture.Common.Services;
    /// resolved dynamically via <c>SdkServices.TryGetService</c> — same idiom
    /// as <see cref="CompareService"/> / <see cref="GxServerSyncService"/>, no
    /// new assembly reference needed).
    ///
    /// Feasibility (verified via reflection against the installed SDK, not
    /// just the sdk-probe docs, which under-list the interface): IMergeService
    /// exposes a base-less 2-way overload —
    /// <c>MergeObjects(KBObject left, KBObject right, KBModel targetModel, bool ignoreConflicts)</c>
    /// — alongside the 3-way <c>MergeObjects(KBObject baseObj, left, right, targetModel)</c>.
    /// This removes the "needs an ancestor" wall that killed other SDK-write
    /// spikes: mode=objects works headless with just two objects when
    /// <c>objectBase</c> is omitted.
    ///
    /// mode=models (IMergeService.MergeModels) is intentionally NOT implemented:
    /// it needs three distinct KBModel instances (ref/target/source), and this
    /// worker process holds exactly one open KB/model. Cross-KB model merge
    /// would need multi-KB orchestration beyond this spike — returns a clean
    /// MergeModelsUnsupported error instead of faking it.
    ///
    /// dryRun defaults to true: reports what differs (left vs right, and vs
    /// base when supplied) via IComparerService WITHOUT calling MergeObjects —
    /// no write happens. dryRun=false performs the real merge and persists the
    /// result via KBObject.EnsureSave(). Every SDK call is guarded; this never
    /// crashes the worker.
    /// </summary>
    public class MergeToolService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public MergeToolService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            string mode = args?["mode"]?.ToString();
            if (string.IsNullOrWhiteSpace(mode)) mode = "objects";
            mode = mode.Trim().ToLowerInvariant();

            if (mode == "models")
            {
                return McpResponse.Err(
                    code: "MergeModelsUnsupported",
                    message: "mode=models (IMergeService.MergeModels) requires three distinct KBModel instances (ref/target/source); this worker holds exactly one open KB/model.",
                    hint: "Use mode=objects for a 2-way (no objectBase) or 3-way (with objectBase) object merge within the open KB.");
            }

            if (mode != "objects")
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "Unknown mode '" + mode + "'. Expected 'objects' (mode=models is not supported in this build).",
                    hint: "Pass mode=objects.");
            }

            string leftName = args?["objectLeft"]?.ToString();
            string rightName = args?["objectRight"]?.ToString();
            string baseName = args?["objectBase"]?.ToString();
            string type = args?["type"]?.ToString();
            bool ignoreConflicts = args?["ignoreConflicts"]?.ToObject<bool?>() ?? false;
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? true;

            if (string.IsNullOrWhiteSpace(leftName) || string.IsNullOrWhiteSpace(rightName))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "objectLeft and objectRight are required.",
                    hint: "Pass both objectLeft and objectRight as existing object names. objectBase is optional (enables a 3-way merge).");
            }

            // issue #32 item 2 (generalized): resilient resolve — self-heals a lazy/late
            // SDK registration after a worker respawn before hard-failing.
            IMergeService mergeSvc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IMergeService>();

            if (mergeSvc == null)
            {
                return McpResponse.Err(
                    code: "MergeServiceUnavailable",
                    message: "The GeneXus SDK's IMergeService is not registered in this worker session (self-heal retries were exhausted).",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            KBObject left;
            KBObject right;
            KBObject baseObj = null;
            try
            {
                left = _objects?.FindObject(leftName, type);
                right = _objects?.FindObject(rightName, type);
                if (!string.IsNullOrWhiteSpace(baseName)) baseObj = _objects?.FindObject(baseName, type);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "MergeFailed", message: ex.Message, hint: "Check the worker log for details.");
            }

            if (left == null)
            {
                return McpResponse.Err(code: "ObjectNotFound", message: "Object '" + leftName + "' not found.", hint: "Confirm objectLeft exists (and matches type, if provided).", nextSteps: new JArray(McpResponse.NextStep("genexus_query", new JObject { ["name"] = leftName }, "Search for objectLeft by name to confirm it exists.")));
            }
            if (right == null)
            {
                return McpResponse.Err(code: "ObjectNotFound", message: "Object '" + rightName + "' not found.", hint: "Confirm objectRight exists (and matches type, if provided).", nextSteps: new JArray(McpResponse.NextStep("genexus_query", new JObject { ["name"] = rightName }, "Search for objectRight by name to confirm it exists.")));
            }
            if (!string.IsNullOrWhiteSpace(baseName) && baseObj == null)
            {
                return McpResponse.Err(code: "ObjectNotFound", message: "Object '" + baseName + "' not found.", hint: "Confirm objectBase exists (and matches type, if provided).", nextSteps: new JArray(McpResponse.NextStep("genexus_query", new JObject { ["name"] = baseName }, "Search for objectBase by name to confirm it exists.")));
            }

            bool threeWay = baseObj != null;

            if (dryRun)
            {
                return DryRunReport(left, right, baseObj, threeWay, ignoreConflicts);
            }

            KBModel targetModel = left.Model;

            try
            {
                KBObject merged = threeWay
                    ? mergeSvc.MergeObjects(baseObj, left, right, targetModel)
                    : mergeSvc.MergeObjects(left, right, targetModel, ignoreConflicts);

                if (merged == null)
                {
                    return McpResponse.Err(
                        code: "MergeFailed",
                        message: "IMergeService.MergeObjects returned null.",
                        hint: "The SDK reported no mergeable result. Try ignoreConflicts=true for a 2-way merge, or supply objectBase for a 3-way merge.");
                }

                bool saved;
                string saveError = null;
                try
                {
                    merged.EnsureSave();
                    saved = true;
                    WriteService.NotePerTargetWrite(merged.Name);
                }
                catch (Exception exSave)
                {
                    saved = false;
                    saveError = exSave.Message;
                }

                var result = new JObject
                {
                    ["merged"] = merged.Name,
                    ["mode"] = threeWay ? "objects:3way" : "objects:2way",
                    ["objectLeft"] = left.Name,
                    ["objectRight"] = right.Name,
                    ["objectBase"] = baseObj?.Name,
                    ["ignoreConflicts"] = ignoreConflicts,
                    ["saved"] = saved,
                    ["source"] = "sdk:IMergeService"
                };

                if (!saved)
                {
                    result["saveError"] = saveError;
                    return McpResponse.Partial(
                        target: merged.Name,
                        code: "MergeCompletedSaveFailed",
                        result: result,
                        warnings: new JArray { "Merge completed in memory but EnsureSave() failed: " + saveError });
                }

                return McpResponse.Ok(code: "MergeCompleted", result: result);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "MergeFailed",
                    message: ex.Message,
                    hint: "Check the worker log for details. Unresolved conflicts may need ignoreConflicts=true (2-way) or an explicit objectBase (3-way).");
            }
        }

        /// <summary>
        /// dryRun=true path: reports pairwise content equality via IComparerService
        /// (mirrors CompareService) without ever calling MergeObjects. Best-effort —
        /// any single comparison failure just leaves that field null.
        /// </summary>
        private static string DryRunReport(KBObject left, KBObject right, KBObject baseObj, bool threeWay, bool ignoreConflicts)
        {
            IComparerService cmp = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IComparerService>();

            bool? leftEqualsRight = null;
            bool? baseEqualsLeft = null;
            bool? baseEqualsRight = null;

            if (cmp != null)
            {
                try { leftEqualsRight = Compatibility.SdkCompat.AreEqualInContent(cmp, left, right); } catch { }
                if (threeWay)
                {
                    try { baseEqualsLeft = Compatibility.SdkCompat.AreEqualInContent(cmp, baseObj, left); } catch { }
                    try { baseEqualsRight = Compatibility.SdkCompat.AreEqualInContent(cmp, baseObj, right); } catch { }
                }
            }

            var result = new JObject
            {
                ["wouldMerge"] = true,
                ["mode"] = threeWay ? "objects:3way" : "objects:2way",
                ["objectLeft"] = left.Name,
                ["objectRight"] = right.Name,
                ["objectBase"] = baseObj?.Name,
                ["ignoreConflicts"] = ignoreConflicts,
                ["leftEqualsRight"] = leftEqualsRight,
                ["baseEqualsLeft"] = baseEqualsLeft,
                ["baseEqualsRight"] = baseEqualsRight,
                ["note"] = "dryRun=true — no SDK MergeObjects call was made, nothing was written. Pass dryRun=false to perform the merge.",
                ["source"] = "sdk:IComparerService"
            };
            return McpResponse.Ok(code: "MergePreview", result: result);
        }
    }
}
