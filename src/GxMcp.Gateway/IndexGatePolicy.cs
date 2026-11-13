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
            => IsIndexDependentTool(toolName, args, _currentKb.Value?.Path);

        internal static bool IsIndexDependentToolForTest(string? toolName, JObject? args, string? openKbPath)
            => IsIndexDependentTool(toolName, args, openKbPath);

        private static bool IsIndexDependentTool(string? toolName, JObject? args, string? openKbPath)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            if (IsIdentityBoundReadAvailableDuringIndexing(toolName, args)) return false;
            if (IndexDependentToolSet.Contains(toolName)) return true;

            // genexus_wire is Gateway-owned and file-based, except pack_task on the open native KB,
            // which the Worker answers from the search index and so waits for it like the other index reads.
            if (GxwireNativePackTask.Applies(toolName, args, openKbPath)) return true;

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

        internal static bool IsIdentityBoundReadAvailableDuringIndexingForTest(
            string? toolName, JObject? args = null)
            => IsIdentityBoundReadAvailableDuringIndexing(toolName, args);

        private static bool IsIdentityBoundReadAvailableDuringIndexing(string? toolName, JObject? args)
        {
            if (args == null || string.IsNullOrWhiteSpace(toolName)) return false;

            bool hasObjectIdentity = HasNonEmptyArgument(args, "guid")
                || HasNonEmptyArgument(args, "entityKey")
                || HasNonEmptyArgument(args, "path")
                || HasNonEmptyArgument(args, "name");

            // genexus_read accepts objectName as an alias of name (ObjectRouter), so the gate must too.
            if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                return hasObjectIdentity || HasNonEmptyArgument(args, "objectName");

            if (string.Equals(toolName, "genexus_inspect", StringComparison.OrdinalIgnoreCase))
                return hasObjectIdentity;

            if (string.Equals(toolName, "genexus_navigation", StringComparison.OrdinalIgnoreCase))
                return string.Equals(args["action"]?.ToString(), "view", StringComparison.OrdinalIgnoreCase)
                    && HasNonEmptyArgument(args, "name");

            if (string.Equals(toolName, "genexus_search_source", StringComparison.OrdinalIgnoreCase))
                return HasNonEmptyArgument(args, "objectName");

            return false;
        }

        private static bool HasNonEmptyArgument(JObject args, string name)
            => !string.IsNullOrWhiteSpace(args[name]?.ToString());

        internal static JObject? BuildExactReadIndexMetadataForTest(
            string? status, string? freshness, int? etaMs)
            => BuildExactReadIndexMetadata(new IndexStateSnapshot
            {
                Status = string.IsNullOrWhiteSpace(status) ? "Cold" : status,
                Freshness = freshness,
                EtaMs = etaMs
            });

        private static JObject? BuildExactReadIndexMetadata(IndexStateSnapshot snapshot)
        {
            if (IsIndexUsableForReads(snapshot)) return null;
            string freshness = string.IsNullOrWhiteSpace(snapshot.Freshness)
                ? InferIndexFreshness(snapshot.Status)
                : snapshot.Freshness!;
            return new JObject
            {
                ["status"] = snapshot.Status,
                ["freshness"] = freshness,
                ["etaMs"] = snapshot.EtaMs.HasValue
                    ? (JToken)snapshot.EtaMs.Value
                    : JValue.CreateNull()
            };
        }

        private static void AttachExactReadIndexMetadata(JObject toolResult, string? toolName, JObject? args)
        {
            if (!IsIdentityBoundReadAvailableDuringIndexing(toolName, args)) return;
            IndexStateSnapshot snapshot = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
            JObject? indexMetadata = BuildExactReadIndexMetadata(snapshot);
            JObject metadata = toolResult["_meta"] as JObject ?? new JObject();
            if (indexMetadata == null)
            {
                metadata.Remove("index");
            }
            else
            {
                metadata["index"] = indexMetadata;
            }
            if (metadata.HasValues) toolResult["_meta"] = metadata;
            else toolResult.Remove("_meta");
        }

        // Reads gx-engine answers from the KB storage instead of the Worker index. They are never
        // refused by the gate (native routing runs first), so the only thing the agent needs while
        // the index warms is to know that the answer is storage-backed and the index is not current.
        private static readonly string[] NativeIndexMarkedTools =
            { "genexus_query", "genexus_list_objects", "genexus_inspect" };

        internal static void AttachNativeServedIndexMetadataForTest(JObject toolResult, string? toolName)
            => AttachNativeServedIndexMetadata(toolResult, toolName);

        /// <summary>
        /// Adds the existing <c>_meta.index</c> block (as exact reads do) to a gx-engine-served index-class
        /// read while the index is not usable. Silent when the mirror was never refreshed: a placeholder
        /// Cold is "unknown", not a warming index. In-memory read, only reached for engine-served results.
        /// </summary>
        private static void AttachNativeServedIndexMetadata(JObject toolResult, string? toolName)
        {
            if (toolName == null
                || !Array.Exists(NativeIndexMarkedTools, t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase)))
                return;
            IndexStateSnapshot snapshot = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
            if (snapshot.RefreshedAtUtc == DateTime.MinValue) return;
            JObject? indexMetadata = BuildExactReadIndexMetadata(snapshot);
            if (indexMetadata == null) return;
            JObject metadata = toolResult["_meta"] as JObject ?? new JObject();
            metadata["index"] = indexMetadata;
            toolResult["_meta"] = metadata;
        }

        // Gated operations (see IndexDependentToolSet) that gx-engine can answer without the index,
        // with the admitted shape. Whether each is routed comes from the registry (EngineRouter), so
        // this list cannot claim a route the engine does not have.
        private static readonly (string Operation, string Shape)[] NativeReadsDuringIndexing =
        {
            ("genexus_query", "genexus_query query=type:<Type> fields=name,type limit<=200"),
            ("genexus_list_objects",
                "genexus_list_objects typeFilter=<Procedure|Transaction|WebPanel|Table|DataProvider> limit<=100 fields=name,type"),
        };

        internal static System.Collections.Generic.IReadOnlyList<string> NativeReadOperationsDuringIndexingForTest
            => Array.ConvertAll(NativeReadsDuringIndexing, entry => entry.Operation);

        /// <summary>
        /// The native reads an IndexNotReady refusal can point at: non-null only when the engine backend
        /// would route them (Backend=gxengine, parity capability) and the engine executable exists.
        /// </summary>
        internal static JArray? BuildNativeReadsAvailableDuringIndexing(
            EngineBackendSettings settings, Func<EngineBackendSettings, string?> locateEngine)
        {
            JArray? shapes = null;
            foreach (var (operation, shape) in NativeReadsDuringIndexing)
            {
                string? capabilityId = EngineRoutedOperations.EngineCapabilityIdForOperation(operation);
                if (capabilityId == null
                    || EngineRouter.Decide(capabilityId, settings).Target != EngineRouteTarget.Engine) continue;
                (shapes ??= new JArray()).Add(shape);
            }
            return shapes != null && locateEngine(settings) != null ? shapes : null;
        }

        // Issue #209 (policy A): the gate stays fail-closed, but its envelope must be
        // observable, awaitable and retryable — it names the index state, points at the one
        // call that waits for freshness=current, and always carries a retry hint (the worker's
        // ETA when it has one, mirroring the SourceSearchService fallback otherwise).
        internal const int DefaultIndexRetryAfterMs = 5000;

        // Longest genexus_kb action=open waits to learn whether the Worker's own open failed.
        internal const int KbOpenFailureProbeCeilingMs = 2000;

        internal static JObject? BuildKbOpenFailedEnvelopeForTest(string? kbAlias)
            => BuildKbOpenFailedEnvelope(GetLastKnownIndexState(kbAlias));

        // A KB the Worker failed to open stays Cold forever, so the IndexNotReady envelope
        // ("wait, then retry") is a lie for it: name the failure the Worker recorded instead.
        internal static JObject? BuildKbOpenFailedEnvelope(IndexStateSnapshot? snapshot)
        {
            JObject? failure = snapshot?.KbOpenFailure;
            if (failure == null) return null;
            string message = failure["message"]?.ToString() ?? string.Empty;
            var envelope = new JObject
            {
                ["status"] = "error",
                ["code"] = "KbOpenFailed",
                ["message"] = string.IsNullOrWhiteSpace(message)
                    ? "The Worker could not open the Knowledge Base."
                    : message,
                ["hint"] = "Fix the cause named in the message (path, directory link, GeneXus installation or KB version), "
                    + "then reopen with genexus_kb action=open. Waiting or reindexing cannot recover a KB that is not open.",
                ["retryable"] = false
            };
            if (failure["path"] != null) envelope["path"] = failure["path"]!.DeepClone();
            if (failure["atUtc"] != null) envelope["failedAtUtc"] = failure["atUtc"]!.DeepClone();
            string? workerCode = failure["code"]?.ToString();
            if (!string.IsNullOrWhiteSpace(workerCode) && workerCode != "KbOpenFailed")
                envelope["openFailureCode"] = workerCode;
            return envelope;
        }

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
                    + " Exact reads by name, type, GUID, EntityKey, or path remain available during indexing; index-derived extras may be partial."
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
