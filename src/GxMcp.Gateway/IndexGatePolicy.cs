using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // Keep the index readiness gate limited to reads and analyses whose
        // result is backed by the search index. SDK mutations and builds have
        // their own source/object validation and remain usable during rebuilds.
        internal static bool IsIndexDependentToolForTest(string? toolName, JObject? args = null)
            => IsIndexDependentTool(toolName, args);

        // Single source of truth for the gate's tool set. Exposed as a read-only view (not just
        // switch arms) so the coverage guard test can assert exactly what the gate protects
        // instead of duplicating the list in the test project — see
        // NoNewGateEntryIsConsumedByTheLegacyAliasRewrite, which is what keeps an entry that the
        // alias rewrite consumes from silently joining the known list below.
        private static readonly HashSet<string> IndexDependentToolSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "genexus_list_objects",
                "genexus_query",
                "genexus_inspect",
                "genexus_read",
                "genexus_search_source",
                "genexus_analyze",
                "genexus_explain",
                "genexus_types",
                "genexus_navigation",
                "genexus_kb_explorer",
                "genexus_diff_generated",
                "genexus_what_if",
                "genexus_db_drift",
                "genexus_orient",
                "genexus_security"
            };

        internal static IReadOnlySet<string> IndexDependentTools => IndexDependentToolSet;

        private static bool IsIndexDependentTool(string? toolName, JObject? args = null)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            if (IndexDependentToolSet.Contains(toolName)) return true;

            string? action = args?["action"]?.ToString();
            if (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(action, "drift_check", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "drift_report", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "types_list", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "types_describe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "types_validate", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(toolName, "genexus_versioning", StringComparison.OrdinalIgnoreCase)
                && string.Equals(action, "diff_generated", StringComparison.OrdinalIgnoreCase);
        }

        // Issue #209 (policy A): the gate stays fail-closed, but its envelope must be
        // observable, awaitable and retryable — it names the index state, points at the one
        // call that waits for freshness=current, and always carries a retry hint (the worker's
        // ETA when it has one, mirroring the SourceSearchService fallback otherwise).
        internal const int DefaultIndexRetryAfterMs = 5000;

        internal static JObject BuildIndexNotReadyEnvelopeForTest(
            string? status, string? freshness, int totalObjects, double? progress, int? etaMs,
            string? operationId = null, string? operationState = null, bool? workerAlive = null,
            bool? recoverable = null, bool? stalled = null)
            => BuildIndexNotReadyEnvelope(status, freshness, totalObjects, progress, etaMs,
                operationId, operationState, workerAlive, recoverable, stalled);

        private static JObject BuildIndexNotReadyEnvelope(
            string? status, string? freshness, int totalObjects, double? progress, int? etaMs,
            string? operationId = null, string? operationState = null, bool? workerAlive = null,
            bool? recoverable = null, bool? stalled = null)
        {
            bool idleStale = string.Equals(freshness, "stale", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(operationState, "Idle", StringComparison.OrdinalIgnoreCase)
                    && workerAlive == false;
            bool isRecoverable = recoverable == true
                || IsStalledIndexStatus(status, operationState, workerAlive) || idleStale;
            var envelope = new JObject
            {
                ["status"] = "Indexing",
                ["code"] = "IndexNotReady",
                // Report the REAL index status (as whoami's index block does) — the freshness
                // field below is what explains why the gate is closed. The previous
                // `freshness == current ? status : "Refreshing"` reported "Refreshing" for a
                // cold start too, so the two surfaces disagreed about the same state.
                ["indexStatus"] = status ?? "Cold",
                ["freshness"] = freshness ?? "stale",
                ["totalObjects"] = totalObjects,
                ["message"] = BuildIndexingMessage(status, progress, etaMs),
                // A Cold/Unknown index is idle — nothing will reach freshness=current by itself
                // (a failed warm-start delta lands here after its retries are exhausted), so the
                // hint has to name the manual recovery in the same turn instead of sending the
                // caller into a wait that can only time out. Mirrors whoami's indexSuggestion.
                ["hint"] = "Wait instead of polling: genexus_lifecycle action=status wait=30 freshness=current, "
                    + "then re-issue this tool. genexus_whoami observes progress but does not block."
                    + (idleStale
                        ? " No index refresh is active. If the state persists, start a warm refresh with genexus_lifecycle action=index force=false."
                        : isRecoverable
                        ? " This index is not progressing on its own — if that wait times out, recover with genexus_lifecycle action=index force=true."
                        : string.Empty),
                ["retryAfterMs"] = etaMs ?? DefaultIndexRetryAfterMs,
                ["recoverable"] = isRecoverable,
                ["workerAlive"] = workerAlive ?? false,
                ["operationState"] = operationState ?? "Unknown"
            };
            if (!string.IsNullOrWhiteSpace(operationId)) envelope["operationId"] = operationId;
            if (stalled.HasValue) envelope["stalled"] = stalled.Value;
            if (progress != null) envelope["progress"] = progress.Value;
            if (etaMs != null) envelope["etaMs"] = etaMs.Value;
            return envelope;
        }

        // Cold is the index state machine's "not built / failed" value (MarkIndexFailed publishes
        // it), and Unknown is the default the gateway uses before any state is known.
        private static bool IsStalledIndexStatus(string? status, string? operationState = null, bool? workerAlive = null)
        {
            if (string.Equals(operationState, "Stalled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(operationState, "WorkerExited", StringComparison.OrdinalIgnoreCase))
                return true;

            // Cold can coexist with a healthy operation before the worker
            // publishes Reindexing. Force recovery is only valid for an
            // observed stall, an exited worker, or an idle cold index.
            if (string.Equals(operationState, "Starting", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(operationState, "Building", StringComparison.OrdinalIgnoreCase))
                return workerAlive == false;

            return string.IsNullOrWhiteSpace(status)
                || string.Equals(status, "Cold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsTransientResponseForCacheForTest(JObject? response)
            => IsTransientResponseForCache(response);

        private static bool IsTransientResponseForCache(JObject? response)
        {
            if (response == null) return false;

            var status = response["status"]?.ToString();
            var code = response["code"]?.ToString();
            return string.Equals(status, "Reindexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "IndexCold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Indexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
                // Canonical worker envelopes use status="ok" plus a transient code.
                // Inspect both fields or IndexCold can enter the semantic cache.
                || string.Equals(code, "IndexNotReady", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Reindexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "IndexCold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Indexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Running", StringComparison.OrdinalIgnoreCase);
        }
    }
}
