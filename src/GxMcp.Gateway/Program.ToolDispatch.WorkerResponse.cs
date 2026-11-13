using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>
        /// Per-call values the worker-response stages need, captured once by
        /// DispatchToolCallCoreAsync before the worker call. Plain values only: the
        /// ambient state (_currentKb, session context, statics) is not copied here.
        /// </summary>
        private sealed class ToolCallResponseContext
        {
            public required string ToolName { get; init; }
            public required JObject? ToolArgs { get; init; }
            public required string? LcAction { get; init; }
            public required string KbScope { get; init; }
            public required List<(string Target, string Part, RecoveryRequirement? Observed)> RecoveryReads { get; init; }
            public required bool PropertiesRecoveryRead { get; init; }
            public required string? CacheKey { get; init; }
            public required bool IsLiveTool { get; init; }
            public required int CacheEpochAtDispatch { get; init; }
            public required long CacheRevisionAtDispatch { get; init; }
        }

        /// <summary>
        /// Worker success path: shape, trim, guard and (when eligible) cache the worker
        /// envelope, returning the tool result. Extracted from the onSuccess callback of
        /// DispatchToolCallCoreAsync (phase: response shaping).
        /// </summary>
        private static JObject ShapeWorkerToolResponse(JObject resultObj, ToolCallResponseContext ctx)
        {
            string tName = ctx.ToolName;
            JObject? tArgs = ctx.ToolArgs;
            string? lcAction = ctx.LcAction;
            string kbScope = ctx.KbScope;
            var recoveryReads = ctx.RecoveryReads;
            bool propertiesRecoveryRead = ctx.PropertiesRecoveryRead;
            string? cKey = ctx.CacheKey;
            bool isLiveTool = ctx.IsLiveTool;
            int cacheEpochAtDispatch = ctx.CacheEpochAtDispatch;
            long cacheRevisionAtDispatch = ctx.CacheRevisionAtDispatch;

            NormalizeInterruptedModuleInstallation(resultObj, tName, tArgs);
            JToken? finalResult = null;
            var omittedMutationFields = new JArray();
            JToken? workerPayload = resultObj["result"] ?? resultObj["error"];
            // The Worker's pre-cut certificate of what the SDK holds. Taken here,
            // before any budget, trim or size guard can touch the payload, so the
            // decision to clear a fence does not depend on how much of the part
            // the caller is allowed to see.
            JObject? recoveryCertificate = recoveryReads.Count > 0
                ? ExtractRecoveryCertificate(workerPayload)
                : null;
            bool includePersistedText = IsMutatingTool(tName, tArgs)
                && (tArgs?["includePersistedText"]?.Value<bool?>() ?? false);
            JToken shapedPayload = workerPayload ?? JValue.CreateNull();
            try
            {
                shapedPayload = ShapeMutationResponse(workerPayload, tName, tArgs, omittedMutationFields);
            }
            catch (Exception exShape)
            {
                Log($"[Gateway] Mutation response shaping failed for {tName}: {exShape.Message}");
            }
            try {
                finalResult = includePersistedText
                    ? shapedPayload
                    : TruncateResponseIfNeeded(shapedPayload, tName);
            } catch (Exception exTrunc) {
                Log($"[Gateway] Error during truncation: {exTrunc.Message}");
                finalResult = shapedPayload;
            }

            if (string.Equals(tName, "genexus_edit_and_build", StringComparison.OrdinalIgnoreCase)
                && finalResult is JObject editAndBuildPayload)
            {
                NormalizeEditAndBuildPayload(editAndBuildPayload);
            }

            bool isErr = resultObj["error"] != null || string.Equals(resultObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
            // Inner-payload error detection — tools that return their
            // failure envelope as result.{error|status} (e.g. genexus_read
            // returning {"part":"Source","error":"Part 'Source' not found ..."})
            // were previously sent with isError=false because we only checked
            // the outer envelope. Mirror the inner shape so MCP clients that
            // branch on isError stay correct.
            if (!isErr && finalResult is JObject innerErrObj)
            {
                bool innerHasError = innerErrObj["error"] != null
                    || string.Equals(innerErrObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(innerErrObj["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(innerErrObj["status"]?.ToString(), "NotImplemented", StringComparison.OrdinalIgnoreCase);
                if (innerHasError) isErr = true;
            }
            if (!isErr && OperationClassifier.IsKbEnvironmentMutation(tName, lcAction))
                InvalidateDatabaseInfoCache(kbScope);

            // Friction 2026-05-22 #63: attach suggested_next_step on every error
            // envelope (verbose OR terse). McpRouter.AttachSuggestedNextStep is a
            // pure pattern-match over code/message text; routes patch NoMatch →
            // near-match inspection, visual write failures → LayoutGotchaScanner,
            // KB_AMBIGUOUS → kb=<alias>, spc0150 → extract_to_procedure recipe.
            if (isErr && finalResult is JObject preTrimErr)
            {
                if (preTrimErr["suggested_next_step"] == null)
                {
                    var hint = McpRouter.AttachSuggestedNextStep(preTrimErr);
                    if (hint != null) preTrimErr["suggested_next_step"] = hint;
                }

                if (preTrimErr["diagnosticContext"] == null)
                {
                    preTrimErr["diagnosticContext"] = BuildDiagnosticContext();
                }
            }

            // TerseErrors: trim error envelopes to {message, code, hint} by default.
            // verbose_errors=true restores the full payload. Gated by PerfProfile.V1Enabled.
            // Runs BEFORE ResponseSizeGuard so the guard sees the trimmed (smaller) payload.
            if (isErr && PerfProfile.V1Enabled && finalResult is JObject errObj)
            {
                bool verbose = tArgs?["verbose_errors"]?.ToObject<bool?>() ?? false;
                finalResult = McpRouter.TrimErrorEnvelope(errObj, verbose);
            }

            // ResponseSizeGuard: replace oversize JObject results with a truncation sentinel.
            // Gated by PerfProfile.V1Enabled so it can be disabled via MCP_PERF_PROFILE=legacy.
            if (!isErr && !includePersistedText && PerfProfile.V1Enabled && finalResult is JObject finalJObj)
            {
                var guard = new ResponseSizeGuard();
                var (guarded, _) = guard.Apply(finalJObj, tName, tArgs);
                finalResult = guarded;
            }

            // Reconcile only content that actually survives the wire guards.
            if (!isErr && string.Equals(tName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                && recoveryReads.Count > 0)
            {
                int fencesCleared = ConfirmRecoveryReadWithCertificate(
                    _mutationRecovery, kbScope, finalResult, recoveryCertificate, recoveryReads);
                if (finalResult is JObject clearedPayload)
                {
                    clearedPayload["recoveryFenceCleared"] = fencesCleared > 0;
                    // Which fences this read covered, and what the version says about
                    // the write that timed out (clearing a fence does not say it landed).
                    clearedPayload["recoveryFences"] = DescribeRecoveryFences(
                        _mutationRecovery, finalResult, recoveryCertificate, recoveryReads);
                }
            }
            if (!isErr && propertiesRecoveryRead && finalResult is JObject propertyPayload)
            {
                bool cleared = false;
                foreach (var recoveryTarget in recoveryReads)
                {
                    if (IsCompletePropertiesRecoveryRead(propertyPayload, recoveryTarget.Observed, tArgs))
                        cleared |= _mutationRecovery.ConfirmRead(kbScope, recoveryTarget.Target,
                            recoveryTarget.Part, recoveryTarget.Observed);
                }
                propertyPayload["recoveryFenceCleared"] = cleared;
                if (propertyPayload["result"] is JObject propertyResult)
                {
                    propertyResult["recoveryFenceCleared"] = cleared;
                    if (propertyResult["values"] != null)
                        propertyPayload["values"] = propertyResult["values"]!.DeepClone();
                    if (propertyResult["versionToken"] != null)
                        propertyPayload["versionToken"] = propertyResult["versionToken"]!.DeepClone();
                }
            }

            // StripNulls: remove null-valued properties from the result to reduce wire size.
            // Runs after ResponseSizeGuard so the guard measures the pre-stripped payload.
            if (PerfProfile.V1Enabled && finalResult is JObject stripObj)
            {
                McpRouter.StripNulls(stripObj);
            }

            // v2.3.8 (Task 6.1) — compact lifecycle status by default.
            // Replaces the verbose BuildTaskStatus payload (full Errors/Warnings/Output)
            // with counts + top-10 errors + warning dedup. compact=false restores legacy shape.
            if (!isErr
                && string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                && string.Equals(lcAction, "status", StringComparison.OrdinalIgnoreCase)
                && finalResult is JObject lifecycleObj
                && LifecycleResponseShaper.ShouldCompact(tArgs))
            {
                // perf: tree-based compact, no serialize→parse round-trip.
                // The old code wrapped a parse failure; CompactObject works on
                // an already-parsed JObject and cannot throw for JSON reasons.
                finalResult = LifecycleResponseShaper.CompactObject(lifecycleObj);
            }

            // The worker envelope has no further consumers after this callback.
            // Transfer the selected payload out of it so KB metadata can be
            // attached without cloning the entire response tree.
            DetachResponsePayload(resultObj, finalResult);
            var toolResult = BuildToolResultContent(finalResult, isErr, tName, tArgs, payloadOwned: true);
            BrowserScreenshotImage.Attach(toolResult, finalResult, tName, tArgs, isErr);
            AttachOmittedFieldsMetadata(toolResult, omittedMutationFields);
            AttachExactReadIndexMetadata(toolResult, tName, tArgs);
            if (cKey != null)
            {
                var cacheMeta = toolResult["_meta"] as JObject ?? new JObject();
                toolResult["_meta"] = cacheMeta;
                cacheMeta["cacheOutcome"] = "miss";
            }

            // v2.3.8 (post-self-review) — don't cache transient envelopes.
            // A "Reindexing"/"IndexCold"/"Timeout"/"Cancelled"/"BuildPlanTooLarge"
            // response is a snapshot of the worker's current state, not a stable
            // semantic answer. Caching it kept analyze impact pinned to the
            // first response (often Timeout during a reindex), so callers got
            // the same stale envelope on every retry until cache eviction.
            bool isTransient = finalResult is JObject transientCheck
                && IsTransientResponseForCache(transientCheck);

            // PERF: `cKey != null` also excludes mutating tools (which
            // cleared the cache and must not pollute it with a write
            // result), plus parameter-dependent side effects recognized by
            // OperationClassifier. The old `Contains("write")/Contains("patch")`
            // check missed tools like genexus_edit.
            // C1 (race fix): if a mutation invalidated the cache while this read
            // was in flight, the pre-mutation envelope must not be stored.
            if (!isErr && !isTransient && !isLiveTool && cKey != null
                && System.Threading.Volatile.Read(ref SemanticCacheEpoch) == cacheEpochAtDispatch
                && _semanticCache.GetRevision(kbScope) == cacheRevisionAtDispatch)
            {
                // Store full envelope in semantic cache (rebuilt on hit above)
                _semanticCache.Set(cKey, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["result"] = toolResult
                });
            }

            return toolResult;
        }

        /// <summary>
        /// Worker timeout path: builds the coded WorkerTimeout result and registers the
        /// recovery fences a timed-out write needs. Extracted from the onTimeout callback
        /// of DispatchToolCallCoreAsync (phase: timeout handling).
        /// </summary>
        private static JObject BuildWorkerTimeoutToolResult(
            string? operationId,
            string correlationId,
            ToolCallResponseContext ctx,
            bool isAsyncGxServer)
        {
            string tName = ctx.ToolName;
            JObject? tArgs = ctx.ToolArgs;
            string kbScope = ctx.KbScope;

            string message = $"GeneXus MCP Worker timed out executing tool: {tName}.";
            var timeoutPayload = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "WorkerTimeout",
                    ["message"] = message,
                    ["hint"] = "The Worker may still be finishing; poll the operation before retrying.",
                    ["retryable"] = true,
                    ["reconciliationRequired"] = false
                },
                ["correlationId"] = correlationId
            };

            bool recordWrite = IsTransactionRecordOperation(tName!, tArgs) && IsMutatingTool(tName!, tArgs);
            bool moduleInstall = IsModuleInstallation(tName, tArgs) && tArgs?["dryRun"]?.ToObject<bool?>() != true;
            if (moduleInstall) MarkModuleInstallOutcomeUnknown(timeoutPayload);
            if (recordWrite) MarkRecordWriteOutcomeUnknown(timeoutPayload);
            var timeoutError = timeoutPayload["error"] as JObject;
            if (recordWrite && timeoutError != null)
            {
                timeoutError["code"] = "UnknownCommitState";
                timeoutError["hint"] = "Do not repeat the mutation. Poll the original operation and read the target back before authorizing a new attempt.";
                timeoutError["retryable"] = false;
                timeoutError["reconciliationRequired"] = true;
            }
            bool hasTimeoutRecoveryTargets = RegisterTimeoutRecoveryFence(
                _mutationRecovery, kbScope, tName, tArgs, operationId, timeoutPayload);
            var help = new JArray();
            if (recordWrite)
                help.Add("Do not repeat the write. Poll the original operation result, then query the record keys against the datastore.");
            if (string.Equals(timeoutPayload["recoveryScope"]?.ToString(), "kb", StringComparison.OrdinalIgnoreCase))
                help.Add("The manifest may have changed multiple KB objects and parts; reconcile the KB state before retrying.");
            if (moduleInstall)
                help.Add("Do not repeat installation. Wait until the Worker is no longer busy, then independently inspect installed modules, dependencies and objects. A Source read cannot reconcile a module installation; no asynchronous result or automatic inventory reconciliation is available.");
            if (!string.IsNullOrWhiteSpace(operationId))
            {
                timeoutPayload["operationId"] = operationId;
                if (!moduleInstall)
                    help.Add($"Operation is still running. Query genexus_lifecycle(action='status', target='op:{operationId}') or action='result'.");
                if (tName != null && (tName.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0
                                     || tName.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                     || tName.IndexOf("variable", StringComparison.OrdinalIgnoreCase) >= 0
                                     || isAsyncGxServer
                                     || hasTimeoutRecoveryTargets))
                {
                    // Writes have usually persisted by the time the gateway times out; poll result, then read — don't retry the edit.
                    help.Add("For long writes the change is usually already persisted; check action='result' once, then read back instead of retrying.");
                }
            }
            else if (!recordWrite && !moduleInstall && !hasTimeoutRecoveryTargets)
            {
                help.Add("Retry with narrower scope or lower limit.");
            }
            timeoutPayload["help"] = help;

            return BuildToolResultContent(timeoutPayload, isError: true, toolName: tName, toolArgs: tArgs);
        }
    }
}
