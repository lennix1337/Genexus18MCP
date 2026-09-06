using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // v2.3.8 (Task 6.1) — friction report 2026-05-15 #9: a failed build's
    // BuildService.GetStatus payload (full Errors[], Warnings[], Output) blows
    // the assistant's tool-result budget. Compact mode replaces it with counts,
    // top-10 errors, and warning dedup (one entry per message + sampleLocations).
    // Triggered when the lifecycle tool args carry compact != false; default true.
    public static class LifecycleResponseShaper
    {
        public const int ErrorCap = 10;
        public const int WarningSampleCap = 3;

        public static string Compact(string rawJson, bool compact)
        {
            if (!compact || string.IsNullOrWhiteSpace(rawJson)) return rawJson;

            JObject obj;
            try { obj = JObject.Parse(rawJson); }
            catch { return rawJson; }

            // Only reshape worker BuildTaskStatus shapes (have ErrorCount or Errors array).
            // Anything else (job envelopes, KB-side payloads, etc.) passes through verbatim.
            if (obj["Errors"] == null && obj["Warnings"] == null && obj["ErrorCount"] == null)
                return rawJson;

            return CompactObject(obj).ToString(Newtonsoft.Json.Formatting.None);
        }

        // PERFORMANCE (perf-review round 3): tree-based core shared with the gateway
        // dispatch paths — callers holding an in-memory JObject skip the
        // serialize→parse round-trip the string overload used to force.
        public static JObject CompactObject(JObject obj)
        {
            var errors = obj["Errors"] as JArray ?? new JArray();
            var warnings = obj["Warnings"] as JArray ?? new JArray();
            int errCount = obj["ErrorCount"]?.Value<int?>() ?? errors.Count;
            int warnCount = obj["WarningCount"]?.Value<int?>() ?? warnings.Count;

            var dedupedWarnings = new JArray();
            var groups = warnings
                .GroupBy(w => (w is JObject jo ? jo["message"]?.ToString() : w?.ToString()) ?? "")
                .ToList();
            foreach (var g in groups)
            {
                var sample = g.Take(WarningSampleCap)
                    .Select(w => w is JObject jo ? (jo["location"]?.ToString() ?? jo.ToString(Newtonsoft.Json.Formatting.None)) : w?.ToString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
                dedupedWarnings.Add(new JObject
                {
                    ["message"] = g.Key,
                    ["count"] = g.Count(),
                    ["sampleLocations"] = new JArray(sample)
                });
            }

            var compactObj = new JObject
            {
                ["Status"] = obj["Status"] ?? obj["status"],
                ["Phase"] = obj["Phase"],
                ["TaskId"] = obj["TaskId"],
                ["ExitCode"] = obj["ExitCode"],
                ["buildMode"] = obj["buildMode"] ?? obj["BuildMode"],
                ["kbOpened"] = obj["kbOpened"] ?? obj["KbOpened"],
                ["buildAllDone"] = obj["buildAllDone"] ?? obj["BuildAllDone"],
                ["reorgRequired"] = obj["reorgRequired"] ?? obj["ReorgRequired"],
                ["msBuildExitCode"] = obj["msBuildExitCode"] ?? obj["MsBuildExitCode"],
                ["fullLogPath"] = obj["fullLogPath"] ?? obj["FullLogPath"],
                ["error"] = obj["error"] ?? obj["Error"],
                ["hint"] = obj["hint"] ?? obj["Hint"],
                ["errorCount"] = errCount,
                ["warningCount"] = warnCount,
                ["errors"] = new JArray(errors.Take(ErrorCap)),
                ["warnings"] = dedupedWarnings,
                ["summary"] = $"{errCount} errors / {warnCount} warnings",
                ["truncated"] = errCount > ErrorCap,
                ["compact"] = true
            };

            if (obj["Environment"] != null)
                compactObj["Environment"] = obj["Environment"];

            // FR (2026-05-21): when CS0246/CS2001 fired, BuildService already extracted
            // the missing object names into SuggestedRebuildTargets. Surface them as a
            // ready-to-fire retry hint so the agent doesn't have to scrape the paths
            // out of error[] by hand.
            var suggested = obj["SuggestedRebuildTargets"] as JArray;
            if (suggested != null && suggested.Count > 0)
            {
                var names = suggested.Select(t => t?.ToString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (names.Length > 0)
                {
                    compactObj["suggested_retry"] = new JObject
                    {
                        ["action"] = "build",
                        ["target"] = string.Join(",", names),
                        ["includeCallees"] = "direct",
                        ["hint"] = "CS0246/CS2001 referenced these objects — rebuild them (and direct callees) before retrying the full build."
                    };
                }
            }

            // Friction 2026-05-22: surface PhaseFailure / PartialSuccess so callers
            // get a real signal instead of "Build Failed: 0 errors, 0 warnings".
            var phaseFailure = obj["PhaseFailure"] as JObject;
            if (phaseFailure != null)
            {
                compactObj["phase_failure"] = phaseFailure;
                // No SuggestedRebuildTargets in this scenario — offer a different retry path.
                if (compactObj["suggested_retry"] == null)
                {
                    string failName = phaseFailure["Name"]?.ToString() ?? "";
                    string hint;
                    if (failName.IndexOf("WebAppConfig", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hint = "WebAppConfig step failed (often a missing template/config). The Generation/Compilation steps may already have updated the DLL — try running the object once before rebuilding, or run a full Build via the IDE to regenerate the config.";
                    }
                    else
                    {
                        hint = $"Late MSBuild step '{failName}' failed. Generation/Compilation may have succeeded — check phase_failure.Message before rerunning the whole build.";
                    }
                    compactObj["suggested_retry"] = new JObject
                    {
                        ["action"] = "build",
                        ["hint"] = hint,
                        ["recoverable"] = obj["PartialSuccess"]?.ToObject<bool?>() ?? false
                    };
                }
            }
            if (obj["PartialSuccess"]?.ToObject<bool?>() == true)
            {
                compactObj["partial_success"] = true;
                // Override Status so a caller branching on it gets a clear signal.
                compactObj["effective_status"] = "PartialSuccess";
            }

            // issue #42 — build-evidence gate. The worker attaches GenerateEvidence
            // to a Succeeded build when it verified the generated .cs. Surface it and,
            // when the gate found a gap (ok=false), mark effective_status so a caller
            // branching on Status doesn't treat a gapped build as a clean success.
            var genEvidence = obj["GenerateEvidence"] as JObject;
            if (genEvidence != null)
            {
                compactObj["generateEvidence"] = genEvidence;
                bool gateOk = genEvidence["ok"]?.ToObject<bool?>() ?? true;
                if (!gateOk && compactObj["effective_status"] == null)
                {
                    // Still isError=false (the build itself succeeded) but the agent
                    // sees a distinct, actionable signal instead of a clean Succeeded.
                    compactObj["effective_status"] = "SucceededWithGaps";
                }
                if (obj["Hint"] != null && compactObj["hint"] == null)
                    compactObj["hint"] = obj["Hint"];
            }

            // issue #42 (P5) — objects edited but not yet successfully rebuilt.
            var stale = obj["staleGenerated"] as JArray;
            if (stale != null && stale.Count > 0) compactObj["staleGenerated"] = stale;

            // Surface taskId/jobId for callers that want to fetch the raw payload later via action=result&compact=false.
            if (obj["jobId"] != null) compactObj["jobId"] = obj["jobId"];
            if (obj["ElapsedSeconds"] != null) compactObj["ElapsedSeconds"] = obj["ElapsedSeconds"];
            if (obj["_meta"] != null) compactObj["_meta"] = obj["_meta"];

            return compactObj;
        }

        /// <summary>
        /// Three-way classification of a terminal build payload (worker's
        /// BuildTaskStatus, optionally already passed through Compact). Friction
        /// 2026-05-22 item 10: a "Build succeeded: 0 warnings, 0 errors" was
        /// returning inside an <c>&lt;e&gt;error{…}&gt;</c> envelope because the
        /// async path was matching JobEntry.Status against "completed" while the
        /// registry actually stamps "succeeded". Centralize the rule so every
        /// caller (sync fast-path, async job-id long-poll, wait_until_done) ends
        /// up on the same envelope shape.
        /// </summary>
        public enum BuildOutcome
        {
            /// <summary>ExitCode=0, ErrorCount=0, WarningCount=0 → MCP isError=false.</summary>
            Success,
            /// <summary>partial_success=true (Generation+Compilation OK, late MSBuild step failed)
            /// → MCP isError=false but envelope carries partial_success/warning markers.</summary>
            PartialSuccess,
            /// <summary>Any real failure (ErrorCount&gt;0, non-zero ExitCode without partial_success,
            /// or non-terminal/unknown state) → MCP isError=true.</summary>
            Error
        }

        /// <summary>
        /// Inspect a (possibly shaped) build payload and decide whether the MCP
        /// envelope should be success, warning, or error. Accepts either the
        /// post-Compact shape (lowercase errorCount/warningCount, partial_success)
        /// or the raw BuildTaskStatus (PascalCase). status/Status terminal labels
        /// are honored: "Succeeded" → Success regardless of counts; "Failed" with
        /// partial_success=true → PartialSuccess; otherwise count/ExitCode drive
        /// the classification.
        /// </summary>
        public static BuildOutcome ClassifyBuildOutcome(JObject buildPayload)
        {
            if (buildPayload == null) return BuildOutcome.Error;

            int errCount = buildPayload["errorCount"]?.ToObject<int?>()
                           ?? buildPayload["ErrorCount"]?.ToObject<int?>()
                           ?? -1;
            int warnCount = buildPayload["warningCount"]?.ToObject<int?>()
                            ?? buildPayload["WarningCount"]?.ToObject<int?>()
                            ?? 0;
            int? exitCode = buildPayload["ExitCode"]?.ToObject<int?>()
                            ?? buildPayload["exitCode"]?.ToObject<int?>();
            string status = (buildPayload["status"] ?? buildPayload["Status"])?.ToString();
            bool partial = buildPayload["partial_success"]?.ToObject<bool?>()
                           ?? buildPayload["PartialSuccess"]?.ToObject<bool?>()
                           ?? false;
            bool isBuildAll = string.Equals(buildPayload["buildMode"]?.ToString(), "BuildAll", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(buildPayload["BuildMode"]?.ToString(), "BuildAll", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(buildPayload["Action"]?.ToString(), "BuildAll", StringComparison.OrdinalIgnoreCase);
            bool reorgRequired = buildPayload["reorgRequired"]?.ToObject<bool?>()
                              ?? buildPayload["ReorgRequired"]?.ToObject<bool?>()
                              ?? string.Equals(status, "ReorgRequired", StringComparison.OrdinalIgnoreCase);
            bool buildAllDone = buildPayload["buildAllDone"]?.ToObject<bool?>()
                             ?? buildPayload["BuildAllDone"]?.ToObject<bool?>()
                             ?? !isBuildAll;

            if (isBuildAll && (reorgRequired || !buildAllDone)) return BuildOutcome.Error;
            if (partial) return BuildOutcome.PartialSuccess;

            // Explicit terminal labels win when present.
            if (!string.IsNullOrEmpty(status))
            {
                if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
                    return BuildOutcome.Success;
                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "ReorgRequired", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "stalled", StringComparison.OrdinalIgnoreCase))
                    return BuildOutcome.Error;
            }

            // Fall back to numeric signals: 0/0/0 with no explicit status is success.
            if (errCount == 0 && warnCount == 0 && (exitCode ?? 0) == 0)
                return BuildOutcome.Success;
            if (errCount <= 0 && (exitCode ?? 0) == 0)
                return BuildOutcome.Success; // counts unknown but exit clean.

            return BuildOutcome.Error;
        }

        public static bool ShouldCompact(JObject toolArgs)
        {
            if (toolArgs == null) return true;
            var v = toolArgs["compact"];
            if (v == null) return true;
            if (v.Type == JTokenType.Boolean) return v.Value<bool>();
            if (v.Type == JTokenType.String)
            {
                var s = v.ToString();
                return !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) && !string.Equals(s, "0", StringComparison.Ordinal);
            }
            return true;
        }
    }
}
