using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Bulk write + rollback replay extracted from WriteService.cs (plan 007).
    // Pure move, no logic changes — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        /// <summary>
        /// Item 15 (mcp-improvements-2026-05-22) — descriptor passed to the
        /// rollback replayer. Pure data so the replay logic is unit-testable
        /// without spinning up a KB.
        /// </summary>
        internal class BulkRollbackItem
        {
            public string Name;
            public string Part;
            public string Type;
            public string SnapshotPath;
        }

        /// <summary>
        /// Item 15 (mcp-improvements-2026-05-22) — replay each successful write
        /// in REVERSE order using the pre-snapshot bytes. Pure helper so unit
        /// tests can drive it through fakes; the production caller wires
        /// EditSnapshotStore.ReadSnapshot and WriteObject as the delegates.
        /// </summary>
        internal static JArray BulkRollbackReplay(
            List<BulkRollbackItem> plan,
            System.Func<string, string> snapshotReader,
            System.Func<string, string, string, string, string> writer)
        {
            var rollbackResults = new JArray();
            if (plan == null) return rollbackResults;
            for (int i = plan.Count - 1; i >= 0; i--)
            {
                var item = plan[i];
                if (VariablesTextReceipt.Applies(item.Part))
                {
                    rollbackResults.Add(new JObject { ["target"] = item.Name, ["itemStatus"] = "Error",
                        ["message"] = "Textual Variables snapshots cannot certify metadata or an atomic conditional restore; no recovery write was attempted." });
                    continue;
                }
                string priorContent = snapshotReader?.Invoke(item.SnapshotPath);
                if (priorContent == null)
                {
                    rollbackResults.Add(new JObject
                    {
                        ["target"] = item.Name,
                        ["itemStatus"] = "Error",
                        ["message"] = "Snapshot bytes unreadable; rollback skipped."
                    });
                    continue;
                }
                string raw;
                try
                {
                    raw = writer(item.Name, item.Part, priorContent, item.Type);
                }
                catch (Exception rex)
                {
                    rollbackResults.Add(new JObject
                    {
                        ["target"] = item.Name,
                        ["itemStatus"] = "Error",
                        ["message"] = "Rollback write threw: " + rex.Message
                    });
                    continue;
                }
                var rparsed = GxMcp.Worker.Helpers.JsonUtil.SafeParse(raw);
                var rstatus = (rparsed as JObject)?["status"]?.ToString();
                rollbackResults.Add(new JObject
                {
                    ["target"] = item.Name,
                    ["itemStatus"] = string.Equals(rstatus, "Error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Restored",
                    ["detail"] = rparsed
                });
            }
            return rollbackResults;
        }

        // Item shape: { name, part?, content, type?, dryRun? }. stopOnError halts at first failure.
        public string BulkWrite(JObject args)
        {
            var items = args?["targets"] as JArray;
            if (items == null || items.Count == 0)
                return McpResponse.Err(
                    code: "MissingParameter",
                    message: "targets[] required",
                    hint: "Supply an array of {name, part?, content} items under 'targets'.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_edit",
                        args: new JObject { ["name"] = "<target>", ["part"] = "Source", ["content"] = "<code>" },
                        why: "Use genexus_edit for single-object writes; genexus_bulk_edit for multi-object batches.")));

            foreach (var item in items.OfType<JObject>())
            {
                string part = item["part"]?.ToString() ?? "Source";
                if (!VariablesTextReceipt.Applies(part)) continue;
                string preview = WriteObject(item["name"]?.ToString(), part, item["content"]?.ToString(),
                    item["type"]?.ToString(), dryRun: true);
                if (JObject.Parse(preview)["status"]?.ToString() == "error") return preview;
            }

            bool stopOnError = args?["stopOnError"]?.ToObject<bool?>() ?? true;
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
            // Item 15 (mcp-improvements-2026-05-22): atomic multi-object batch.
            // When transactional=true, pre-snapshot every target's prior content
            // and roll back all successful writes on the first Error.
            bool transactional = args?["transactional"]?.ToObject<bool?>() ?? false;
            string concurrencyPolicy = args?["concurrencyPolicy"]?.ToString() ?? "warn";

            string kbPath = null;
            string kbName = null;
            try
            {
                var kb = _objectService?.GetKbService()?.GetKB();
                kbPath = _objectService?.GetKbService()?.GetKbPath();
                kbName = kb?.Name;
            }
            catch { }

            // Issue #128: Pre-flight fail_if_open check for all targets before mutating state
            if (string.Equals(concurrencyPolicy, "fail_if_open", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var it in items)
                {
                    string targetName = it?["name"]?.ToString();
                    if (string.IsNullOrEmpty(targetName)) continue;
                    IdeConcurrencyStatus probe = null;
                    try { probe = IdeConcurrencyDetector.Check(kbPath, kbName, targetName); } catch { }
                    if (probe != null && probe.IsTargetObjectOpen)
                    {
                        return McpResponse.Err(
                            code: "IdeObjectOpen",
                            message: probe.WarningMessage,
                            hint: "Close the object tab in GeneXus IDE without saving, reload it, or set concurrencyPolicy='warn' to proceed anyway.",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_edit",
                                args: new JObject { ["concurrencyPolicy"] = "warn" },
                                why: "Force bulk write by setting concurrencyPolicy='warn'.")),
                            target: targetName,
                            extra: probe.ToWarningObject(targetName, kbName));
                    }
                }
            }

            var results = new JArray();
            int success = 0, failure = 0, skipped = 0;

            // Track per-item rollback context (only populated when transactional=true).
            // We capture pre-write snapshots BEFORE each write so a mid-batch failure
            // can replay prior bytes in reverse order.
            var rollbackPlan = new List<(string Name, string Part, string Type, GxMcp.Worker.Helpers.EditSnapshotStore.SnapshotInfo Snapshot)>();
            string failedAt = null;

            foreach (var it in items)
            {
                if (failure > 0 && stopOnError && !transactional)
                {
                    results.Add(JObject.Parse(McpResponse.Ok(
                        target: it?["name"]?.ToString(),
                        code: "WriteSkipped")));
                    skipped++;
                    continue;
                }
                var name = it?["name"]?.ToString();
                var part = it?["part"]?.ToString() ?? "";
                var content = it?["content"]?.ToString();
                var itemType = it?["type"]?.ToString();
                var itemDryRun = it?["dryRun"]?.ToObject<bool?>() ?? dryRun;
                if (string.IsNullOrEmpty(name) || content == null)
                {
                    results.Add(JObject.Parse(McpResponse.Err(
                        code: "MissingParameter",
                        message: "missing name or content",
                        hint: "Each item in targets[] must have 'name' and 'content'.",
                        target: name)));
                    failure++;
                    if (transactional) { failedAt = name; break; }
                    continue;
                }

                // Capture pre-write snapshot for transactional rollback. Dry-run
                // items don't modify state, so they don't need rollback bytes.
                GxMcp.Worker.Helpers.EditSnapshotStore.SnapshotInfo preSnap = null;
                if (transactional && !itemDryRun)
                {
                    preSnap = TryCapturePreWriteSnapshot(name, part, itemType);
                }

                string raw = WriteObject(name, part, content, itemType, true, false, true, itemDryRun);

                // Issue #128: attach IDE concurrency warning and nudge IDE if applicable
                IdeConcurrencyStatus itemProbe = null;
                try { itemProbe = IdeConcurrencyDetector.Check(kbPath, kbName, name); } catch { }
                if (itemProbe != null && itemProbe.HasWarning)
                {
                    raw = AttachWarning(raw, itemProbe.ToWarningObject(name, kbName));
                }
                if (!itemDryRun && itemProbe != null)
                {
                    itemProbe.TickleIde();
                }

                var parsed = GxMcp.Worker.Helpers.JsonUtil.SafeParse(raw);
                results.Add(parsed);

                var status = (parsed as JObject)?["status"]?.ToString();
                bool isError = string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
                if (isError)
                {
                    failure++;
                    if (transactional)
                    {
                        failedAt = name;
                        break;
                    }
                }
                else
                {
                    success++;
                    if (transactional && preSnap != null)
                    {
                        rollbackPlan.Add((name, string.IsNullOrEmpty(part) ? "Source" : part, itemType, preSnap));
                    }
                }
            }

            // Transactional rollback path: replay each successful write in reverse
            // using the pre-snapshot bytes. Each rollback is itself a WriteObject
            // call, so it gets validated and persisted via the same SDK path that
            // the original edit used — guaranteeing the same write semantics.
            if (transactional && failure > 0)
            {
                var planForHelper = new List<BulkRollbackItem>();
                foreach (var p in rollbackPlan)
                {
                    planForHelper.Add(new BulkRollbackItem { Name = p.Name, Part = p.Part, Type = p.Type, SnapshotPath = p.Snapshot?.Path });
                }
                var rollbackResults = BulkRollbackReplay(
                    planForHelper,
                    GxMcp.Worker.Helpers.EditSnapshotStore.ReadSnapshot,
                    (name, part, content, type) => WriteObject(name, part, content, type, true, false, true, false));

                // Surface rollback-itself-failures at the top level so callers don't have to
                // walk rollbackResults to learn the KB is in a half-restored state.
                int rollbackErrors = 0;
                foreach (var r in rollbackResults)
                {
                    var s = r["status"]?.ToString();
                    if (string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s, "error", StringComparison.OrdinalIgnoreCase)) rollbackErrors++;
                }
                bool rollbackSucceeded = rollbackErrors == 0;
                string rollbackCode = rollbackSucceeded ? "BulkWriteRolledBack"
                    : (rollbackErrors == rollbackResults.Count ? "BulkRollbackFailed" : "BulkRollbackPartial");

                // Return as error when rollback succeeded (batch failed), partial when rollback itself partially failed.
                string rollbackHint = rollbackSucceeded
                    ? "Transactional batch failed at '" + failedAt + "'; all prior writes were rolled back successfully."
                    : "KB may be in a half-restored state. Inspect rollbackResults items and consider genexus_undo on affected targets.";

                var rollbackResultObj = new JObject
                {
                    ["rollbackSucceeded"] = rollbackSucceeded,
                    ["failedAt"] = failedAt,
                    ["successfulBeforeFailure"] = new JArray(rollbackPlan.Select(r => (JToken)r.Name).ToArray()),
                    ["counts"] = new JObject {
                        ["attempted"] = results.Count,
                        ["rolledBack"] = rollbackPlan.Count - rollbackErrors,
                        ["rollbackErrored"] = rollbackErrors,
                        ["failed"] = failure
                    },
                    ["results"] = results,
                    ["rollbackResults"] = rollbackResults
                };
                GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(rollbackResultObj, SummarizeBulkSdkPath(results));

                if (rollbackSucceeded)
                {
                    // All rolled back — emit as error.
                    return McpResponse.Err(
                        code: rollbackCode,
                        message: "Transactional bulk write failed at '" + failedAt + "'; all prior writes rolled back.",
                        hint: rollbackHint,
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = failedAt },
                            why: "Inspect the failing object to diagnose why the write was rejected.")),
                        target: failedAt,
                        extra: rollbackResultObj);
                }
                else
                {
                    // Rollback itself partially failed — partial.
                    string partialStr = McpResponse.Partial(
                        target: failedAt,
                        code: rollbackCode,
                        result: rollbackResultObj,
                        warnings: new JArray(new JObject { ["code"] = "RollbackPartialFailure", ["message"] = rollbackHint }));
                    return partialStr;
                }
            }

            string bulkCode = failure == 0 ? "BulkWriteCompleted" : "BulkWritePartial";
            var bulkResult = new JObject
            {
                ["counts"] = new JObject { ["success"] = success, ["failure"] = failure, ["skipped"] = skipped },
                ["results"] = results,
            };
            // Bulk inherits whatever each item's sdkPath was: when all match, tag the bulk
            // with that value; when they differ, tag "hybrid" so observability captures the mix.
            string bulkSdkPath = SummarizeBulkSdkPath(results);
            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(bulkResult, bulkSdkPath);

            if (failure == 0)
                return McpResponse.Ok(code: bulkCode, result: bulkResult);
            else
                return McpResponse.Partial(target: null, code: bulkCode, result: bulkResult,
                    warnings: new JArray(new JObject
                    {
                        ["code"] = "PartialWriteFailure",
                        ["message"] = $"{failure} of {results.Count} write(s) failed. Inspect results[] for details."
                    }));
        }

        private static string SummarizeBulkSdkPath(JArray results)
        {
            string seen = null;
            foreach (var r in results)
            {
                if (!(r is JObject jo)) continue;
                string p = jo["_meta"]?["sdkPath"]?.ToString();
                if (string.IsNullOrEmpty(p)) continue;
                if (seen == null) seen = p;
                else if (!string.Equals(seen, p, StringComparison.Ordinal)) return GxMcp.Worker.Helpers.WriteResultMeta.Hybrid;
            }
            return seen ?? GxMcp.Worker.Helpers.WriteResultMeta.TypedSdk;
        }

    }
}
