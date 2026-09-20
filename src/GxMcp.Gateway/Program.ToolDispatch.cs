using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    partial class Program
    {

        /// <summary>
        /// Carries the caller's stable operation identity into the Worker RPC.
        /// The Gateway journal remains the durable fence; the Worker identity is
        /// a second in-process guard for retries that arrive after a transport
        /// timeout but before the first response reaches the client.
        /// </summary>
        internal static void AttachClientRequestIdentity(JObject workerCommand, JObject? toolArgs)
        {
            if (workerCommand == null || toolArgs == null) return;

            string? identity = toolArgs["clientRequestId"]?.ToString();
            if (string.IsNullOrWhiteSpace(identity))
                identity = toolArgs["idempotencyKey"]?.ToString();
            if (string.IsNullOrWhiteSpace(identity)) return;

            var workerParams = workerCommand["params"] as JObject;
            if (workerParams == null)
            {
                workerParams = new JObject();
                workerCommand["params"] = workerParams;
            }

            // A router/adapter may already have bound a stronger identity. Do
            // not silently replace it with a client argument.
            if (string.IsNullOrWhiteSpace(workerParams["clientRequestId"]?.ToString()))
                workerParams["clientRequestId"] = identity;
        }

        private static string? GetAsyncMutationTarget(string? toolName, JObject? args)
        {
            string? direct = args?["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            if (!string.Equals(toolName, "genexus_io", StringComparison.OrdinalIgnoreCase)) return direct;

            if (args?["targets"] is JArray targets)
            {
                foreach (JToken token in targets)
                {
                    string? value;
                    if (token.Type == JTokenType.Object)
                        value = token["name"]?.ToString() ?? token["target"]?.ToString();
                    else
                        value = token.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            return null;
        }

        private static bool RequiresAsyncMutationRecovery(JobEntry? job)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.Target)) return false;
            return !string.Equals(job.Part, "ObjectTextExport", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Inner dispatch: returns { isError, content } (tool result payload, no
        /// JSON-RPC envelope). Extracted from the former DispatchCore local function;
        /// the caller binds request/session context explicitly.
        /// </summary>
        private static async Task<JObject> DispatchToolCallCoreAsync(
            JObject tcParams,
            JObject request,
            string sessionId,
            bool sessionContextEnabled,
            JToken? idToken,
            CancellationToken transportCancellation)
        {
            string tName = tcParams["name"]?.ToString() ?? "";
            var tArgs = tcParams["arguments"] as JObject;
            string kbScope = _currentKb.Value?.NormalizedAlias ?? "";

            // 1. CACHE INVALIDATION: If it's a write operation or a re-index, clear the cache.
            // PERF: compute once so the semantic-cache read below can skip the
            // (payload-serializing) key construction for mutating tools — the cache
            // was just cleared, so a lookup would be a guaranteed miss anyway.
            // genexus_edit used to serialize its full source payload into a key
            // for that doomed lookup on every call.
            bool isMutating = IsMutatingTool(tName, tArgs);
            if (isMutating
                && !IsMutationPreview(tArgs)
                && !_mutationRecovery.IsHealthy)
            {
                return BuildToolResultContent(
                    MutationRecoveryRegistry.BuildJournalBlockedEnvelope(_mutationRecovery.JournalError),
                    isError: true,
                    toolName: tName,
                    toolArgs: tArgs);
            }
            if (isMutating && !IsMutationPreview(tArgs))
            {
                RecoveryRequirement? recoveryRequirement = null;
                foreach (var recoveryTarget in EnumerateMutationRecoveryTargets(tName, tArgs))
                {
                    if (_mutationRecovery.TryGet(kbScope, recoveryTarget.Target, recoveryTarget.Part, out var found))
                    {
                        recoveryRequirement = found;
                        break;
                    }
                }
                if (recoveryRequirement != null)
                {
                    return BuildToolResultContent(
                        MutationRecoveryRegistry.BuildBlockedEnvelope(recoveryRequirement),
                        isError: true,
                        toolName: tName,
                        toolArgs: tArgs);
                }
            }
            if (isMutating && !IsMutationPreview(tArgs))
            {
                // A confirmed or uncertain mutation invalidates the entire
                // affected KB generation. Object names in request arguments
                // do not describe the population returned by list/query reads,
                // so substring invalidation can preserve stale collections.
                // The generation fence below still lets unrelated KBs retain
                // their warm entries and blocks in-flight old reads.
                long revision = _semanticCache.InvalidateScope(kbScope, out int removed);
                if (_verboseRequestLogs)
                    Log($"[Cache] Generation invalidation for '{kbScope}' by {tName}: revision={revision}, {removed} entr(y|ies) dropped");
                // Per-KB generations fence related in-flight reads without
                // invalidating warm entries (or writes) for unrelated KBs.
                // Only an unknown/global scope needs the process-wide epoch.
                if (string.IsNullOrWhiteSpace(kbScope))
                    System.Threading.Interlocked.Increment(ref SemanticCacheEpoch);
                BroadcastResourcesListChanged($"cache_invalidated:{tName}", kbScope, revision);
                BroadcastResourceUpdated(
                    "genexus://objects",
                    $"tool:{tName}",
                    kbScope,
                    revision);
            }

            // 2. SEMANTIC CACHE: Try to get from cache for classifier-approved
            // read-only tools. CreateSemanticCacheKey repeats the safety gate
            // so side-effectful action parameters cannot be cached accidentally.
            // Skip caching for live-progress lifecycle reads (status/result/cancel) and logs —
            // these must always reflect current worker state, not a stale snapshot.
            string lcAction = tArgs?["action"]?.ToString()?.ToLowerInvariant();
            // Live diagnostics and progress reads must always reflect current state.
            bool isLiveTool = IsLiveToolForCache(tName, lcAction);

            // Scope the semantic cache by the resolved KB: the same tool+args
            // against two different open KBs must not share envelopes (the
            // worker is single-KB, so an identical read against KB-B could
            // otherwise replay KB-A's cached result).
            // PERF: only build the key (which serializes the whole args tree) when
            // a lookup can possibly hit. Mutating tools just cleared the cache
            // (guaranteed miss) and live tools never read from it, so for those
            // the key — and the expensive payload ToString for genexus_edit/write
            // — is pure waste.
            long cacheRevisionAtDispatch = _semanticCache.GetRevision(kbScope);
            string? modelScope = null;
            string? environmentScope = null;
            var currentKbForCache = _currentKb.Value;
            if (currentKbForCache?.IsEnvCacheFresh == true)
            {
                modelScope = currentKbForCache.ActiveEnvironmentVersion;
                environmentScope = currentKbForCache.ActiveEnvironment;
            }
            string? cKey = CreateSemanticCacheKey(
                kbScope, tName, tArgs, isMutating, isLiveTool,
                cacheRevisionAtDispatch, modelScope, environmentScope);
            if (cKey != null && _semanticCache.TryGet(cKey, out var cachedResponse))
            {
                if (_verboseRequestLogs) Log($"[Cache] HIT for {tName}");
                var cached = cachedResponse["result"] as JObject;
                if (cached != null)
                {
                    var hit = (JObject)cached.DeepClone();
                    var hitMeta = hit["_meta"] as JObject ?? new JObject();
                    hit["_meta"] = hitMeta;
                    hitMeta["cacheOutcome"] = "hit";
                    _operationTracker.RecordCacheHit(tName);
                    return hit;
                }
            }

            // Rebuild full request so ConvertToolCall works
            var fullReq = new JObject
            {
                ["method"] = "tools/call",
                ["params"] = tcParams
            };

            // v2.6.9 perf: gateway-side fast-fail for SDK-bound tools when
            // the worker is still doing its initial BulkIndex on the STA thread.
            // Without this the request queues behind a 30-60s SDK enumeration
            // and the agent eats the full 60s gateway-timeout before learning
            // the index isn't ready. Once _lastKnownIndexState reports a usable
            // status we forward normally; when it doesn't, the block below does a
            // synchronous refresh-and-recheck before fast-failing so a ready index
            // isn't masked by a stale mirror. Worker-side ListService has its own
            // fast-fail for callers that bypass this short-circuit.
            //
            // Allow-list of tools that are blocked behind the STA thread and
            // therefore benefit from the short-circuit. Gateway-served tools
            // (whoami, recipe, kb_diff, sandbox, etc.) bypass naturally because they
            // never hit the worker dispatcher. genexus_doctor is deliberately NOT
            // listed: it runs off the STA thread (worker "health" method) and reads
            // the on-disk snapshot, returning its own SearchIndexMissing/Empty report
            // with retry hints — far more useful than a generic IndexNotReady while
            // indexing, and it doubles as an escape hatch when the mirror is wrong.
            if (IsIndexDependentTool(tName))
            {
                IndexStateSnapshot idxSnap = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
                bool indexUsable = IsIndexUsableForReads(idxSnap);
                if (!indexUsable)
                {
                    // The gateway's index mirror is only written by TryRefreshIndexStateFromWorkerAsync
                    // (whoami), so a "not usable" snapshot may just be STALE — the worker finished
                    // indexing but no whoami has refreshed the mirror since. Rather than fast-fail
                    // on a possibly-stale cache (which left agents stuck on IndexNotReady until they
                    // manually called whoami), do ONE bounded synchronous refresh and re-check.
                    // GetIndexState runs off the worker's STA thread, so this stays fast even while a
                    // cold-start BulkIndex is in flight. Skip the refresh when the mirror was just
                    // refreshed (index genuinely still building) so tight retry loops don't each pay
                    // a round-trip.
                    bool cacheStale = idxSnap == null
                        || idxSnap.RefreshedAtUtc == DateTime.MinValue
                        || (DateTime.UtcNow - idxSnap.RefreshedAtUtc).TotalSeconds > 2;
                    if (cacheStale && await TryRefreshIndexStateFromWorkerAsync(
                        timeoutMs: 1200, kbAlias: _currentKb.Value?.NormalizedAlias))
                    {
                        idxSnap = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
                        indexUsable = IsIndexUsableForReads(idxSnap);
                    }
                }
                if (!indexUsable)
                {
                    // Cold path: right after a KB open the bootstrap settles the index mirror, and
                    // this call can land inside that short window. Joining that settle is bounded
                    // and turns the agent's first index-dependent call into a real result instead
                    // of an IndexNotReady envelope it must retry — measured against a real KB, the
                    // envelope cost a full extra turn plus the advertised retryAfterMs backoff on
                    // an index that became usable ~250ms later.
                    //
                    // The join alone is not enough: a call that reaches this gate in the same
                    // instant the bootstrap publishes its settle loses the race and fast-fails
                    // (observed intermittently with genexus_search_source as the first call). So
                    // when the mirror reports a restored snapshot still awaiting its delta, this
                    // gate settles the mirror itself — same bounded routine, same budget — making
                    // the outcome independent of that race. Any other not-ready state (a genuine
                    // first-ever build) falls straight through to the envelope below.
                    var settleInFlight = IndexMirrorSettleInFlight;
                    if (settleInFlight != null && !settleInFlight.IsCompleted)
                    {
                        // Link the caller's token with gateway shutdown so either one ends the wait.
                        // The rest of this method already honours transportCancellation, and a
                        // cancelled request must not be held here for the whole ceiling.
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                            _gatewayLifetime.Token, transportCancellation);
                        try
                        {
                            await Task.WhenAny(settleInFlight,
                                Task.Delay(IndexMirrorSettleGateWaitCeilingMs, waitCts.Token));
                        }
                        catch { }
                    }
                    else if (IsRestoredSnapshotAwaitingDeltaForTest(idxSnap?.Status, idxSnap?.Freshness))
                    {
                        await SettleIndexMirrorAfterBootstrapAsync();
                    }
                    idxSnap = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
                    indexUsable = IsIndexUsableForReads(idxSnap);
                }
                if (!indexUsable)
                {
                    // This envelope is silent otherwise, and "which field closed the gate, and how
                    // old is the mirror it came from" is the first question when the agent's cold
                    // start eats Indexing envelopes against an index that is already usable.
                    long mirrorAgeMs = idxSnap == null || idxSnap.RefreshedAtUtc == DateTime.MinValue
                        ? -1
                        : (long)(DateTime.UtcNow - idxSnap.RefreshedAtUtc).TotalMilliseconds;
                    Log($"[IndexGate] closed tool={tName} status={idxSnap?.Status ?? "<null>"} "
                        + $"freshness={idxSnap?.Freshness ?? "<null>"} "
                        + $"opState={idxSnap?.OperationState ?? "<null>"} mirrorAgeMs={mirrorAgeMs}");
                    return BuildToolResultContent(
                        BuildIndexNotReadyEnvelope(
                            idxSnap?.Status, idxSnap?.Freshness, idxSnap?.TotalObjects ?? 0,
                            idxSnap?.Progress, idxSnap?.EtaMs, idxSnap?.OperationId,
                            idxSnap?.OperationState, idxSnap?.WorkerAlive,
                            idxSnap?.Recoverable, idxSnap?.Stalled),
                        false, tName, tArgs);
                }
            }

            // Gateway-served tools (factored method below; null = fall through).
            JObject? gatewayResult = await TryDispatchGatewayToolAsync(tName, tArgs, sessionId, sessionContextEnabled);
            if (gatewayResult != null) return gatewayResult;

            // Async lifecycle build (factored TryDispatchAsyncLifecycleBuildAsync below).
            // build / rebuild actions go through path selection (see factored method below):
            // null = not an async-eligible build, or sync fast-path → fall through.
            JObject? asyncBuildResult = await TryDispatchAsyncLifecycleBuildAsync(tName, lcAction, tArgs, request, sessionId, idToken, transportCancellation);
            if (asyncBuildResult != null) return asyncBuildResult;

            object? rawWorkerCmd = null;
            if (string.Equals(tName, "genexus_export_object", StringComparison.OrdinalIgnoreCase))
            {
                rawWorkerCmd = new
                {
                    module = "Object",
                    action = "ExportText",
                    target = tArgs?["name"]?.ToString(),
                    path = tArgs?["outputPath"]?.ToString(),
                    part = tArgs?["part"]?.ToString() ?? "Source",
                    type = tArgs?["type"]?.ToString(),
                    overwrite = tArgs?["overwrite"]?.ToObject<bool?>() ?? false
                };
            }
            else if (string.Equals(tName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
            {
                rawWorkerCmd = new
                {
                    module = "Object",
                    action = "ImportText",
                    target = tArgs?["name"]?.ToString(),
                    path = tArgs?["inputPath"]?.ToString(),
                    part = tArgs?["part"]?.ToString() ?? "Source",
                    type = tArgs?["type"]?.ToString()
                };
            }

            try
            {
                rawWorkerCmd ??= McpRouter.ConvertToolCall(fullReq);
            }
            catch (UsageException ux)
            {
                // Return as error result (not JSON-RPC level — that happens in wrapper below)
                return new JObject
                {
                    ["isError"] = true,
                    ["usageException"] = true,
                    ["code"] = -32602,
                    ["message"] = ux.Message,
                    ["usageCode"] = ux.Code
                };
            }

            var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
            if (workerCmd == null)
            {
                return BuildToolResultContent(
                    new JObject { ["error"] = "Tool conversion produced no worker command." },
                    isError: true,
                    toolName: tName,
                    toolArgs: tArgs);
            }

            workerCmd["client"] = "mcp";
            AttachClientRequestIdentity(workerCmd, tArgs);
            int timeoutMs = GetToolTimeoutMs(tName, tArgs);
            // Still needed by the timeout handler below (recovery hints for
            // gxserver writes); the async-edit intercept recomputes its own copy.
            bool isAsyncGxServer = (tArgs?["async"]?.ToObject<bool?>() ?? false)
                                   && IsAsyncGxServerAction(tName, tArgs);

            // Async edit/variable/gxserver (factored method below; null = run synchronously).
            JObject? asyncEditResult = TryDispatchAsyncEdit(workerCmd, tName, tArgs, sessionId, request);
            if (asyncEditResult != null) return asyncEditResult;

            JObject? innerResult = null;
            // MCP keepalive: when the client supplied a progressToken, emit
            // notifications/progress while the worker runs so long synchronous
            // tools (apply_pattern, delete, analyze, …) don't trip the client's
            // request timeout. No-op when the client omits the token.
            var toolProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
            bool toolHasProgressToken = toolProgressToken != null && toolProgressToken.Type != JTokenType.Null;
            // C1 (race fix): capture the cache epoch before dispatch; if a concurrent
            // mutation clears the cache while this read is in flight, the store below
            // is skipped because the epoch will no longer match.
            int cacheEpochAtDispatch = System.Threading.Interlocked.CompareExchange(ref SemanticCacheEpoch, 0, 0);
            innerResult = await SendWorkerCommandAsync(
                workerCmd,
                timeoutMs,
                $"Timeout waiting for tool: {tName}",
                resultObj =>
                {
                    JToken? finalResult = null;
                    try {
                        finalResult = TruncateResponseIfNeeded(resultObj["result"] ?? resultObj["error"], tName);
                    } catch (Exception exTrunc) {
                        Log($"[Gateway] Error during truncation: {exTrunc.Message}");
                        finalResult = resultObj["result"] ?? resultObj["error"];
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

                    if (!isErr && string.Equals(tName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var recoveryTarget in EnumerateMutationRecoveryTargets(tName, tArgs))
                            _mutationRecovery.ConfirmRead(kbScope, recoveryTarget.Target, recoveryTarget.Part);
                    }

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
                    if (!isErr && PerfProfile.V1Enabled && finalResult is JObject finalJObj)
                    {
                        var guard = new ResponseSizeGuard();
                        var (guarded, _) = guard.Apply(finalJObj, tName, tArgs);
                        finalResult = guarded;
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
                },
                (operationId, correlationId) =>
                {
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
                    if (recordWrite) MarkRecordWriteOutcomeUnknown(timeoutPayload);
                    var timeoutError = timeoutPayload["error"] as JObject;
                    if (recordWrite && timeoutError != null)
                    {
                        timeoutError["code"] = "UnknownCommitState";
                        timeoutError["hint"] = "Do not repeat the mutation. Poll the original operation and read the target back before authorizing a new attempt.";
                        timeoutError["retryable"] = false;
                        timeoutError["reconciliationRequired"] = true;
                    }
                    var help = new JArray();
                    if (recordWrite)
                        help.Add("Do not repeat the write. Poll the original operation result, then query the record keys against the datastore.");
                    if (!string.IsNullOrWhiteSpace(operationId))
                    {
                        timeoutPayload["operationId"] = operationId;
                        help.Add($"Operation is still running. Query genexus_lifecycle(action='status', target='op:{operationId}') or action='result'.");
                        if (tName != null && (tName.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0
                                             || tName.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                             || tName.IndexOf("variable", StringComparison.OrdinalIgnoreCase) >= 0
                                             || isAsyncGxServer))
                        {
                            // Writes have usually persisted by the time the gateway times out; poll result, then read — don't retry the edit.
                            help.Add("For long writes the change is usually already persisted; check action='result' once, then read back instead of retrying.");
                            foreach (var recoveryTarget in EnumerateMutationRecoveryTargets(tName!, tArgs))
                                _mutationRecovery.RequireRead(kbScope, recoveryTarget.Target, recoveryTarget.Part, operationId);
                            timeoutPayload["reReadRequired"] = true;
                        }
                    }
                    else if (!recordWrite)
                    {
                        help.Add("Retry with narrower scope or lower limit.");
                    }
                    timeoutPayload["help"] = help;

                    return BuildToolResultContent(timeoutPayload, isError: true, toolName: tName, toolArgs: tArgs);
                },
                toolName: tName,
                toolArgs: tArgs,
                trackOperation: true,
                progressToken: toolProgressToken,
                heartbeat: toolHasProgressToken ? TryWriteStdout : null,
                // E9: bind this worker request to the MCP client request id so
                // notifications/cancelled can find and abort it (the _pendingRequests
                // key is a gateway GUID, invisible to the client).
                mcpRequestId: idToken?.ToString(),
                mcpRequestIdToken: idToken,
                mcpSessionId: sessionId);

            // apply_pattern { validate: true } — post-apply build of the
            // generated host so the LLM sees compile failures in a single
            // tool call. Without this the agent declared "Success" and only
            // discovered the broken binding by opening the IDE. Runs OUTSIDE
            // the SendWorkerCommandAsync onSuccess lambda (which is sync) so
            // we can await the validation build here.
            if (innerResult != null
                && !(innerResult["isError"]?.ToObject<bool?>() ?? false)
                && string.Equals(tName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase)
                && (tArgs?["validate"]?.ToObject<bool?>() ?? false))
            {
                string applyText = (innerResult["content"] as JArray)?[0]?["text"]?.ToString() ?? "";
                // Round-trip is structural: innerResult.content[0].text was serialized
                // by BuildToolResultContent inside SendWorkerCommandAsync's sync onSuccess
                // lambda (after truncation/normalization) and no parsed payload survives;
                // we must parse to read patternHost AND to fold the validation block back in.
                JObject applyPayload = null;
                try { applyPayload = JObject.Parse(applyText); } catch { }
                string hostName = applyPayload?["patternHost"]?.ToString();

                var vBlock = new JObject { ["target"] = hostName };
                if (string.IsNullOrWhiteSpace(hostName))
                {
                    vBlock["status"] = "skipped";
                    vBlock["reason"] = "No patternHost in apply response — nothing to validate.";
                }
                else
                {
                    // Worker's BuildService.Build is async internally — kicks off
                    // Task.Run and returns a Running envelope with a taskId in ms.
                    // We must (1) fire Build/Build, (2) poll Build/Status with that
                    // taskId until terminal, (3) fetch Build/Result for errors.
                    var vSw = System.Diagnostics.Stopwatch.StartNew();
                    JObject startCmd = new JObject
                    {
                        ["module"] = "Build",
                        ["action"] = "Build",
                        ["target"] = hostName,
                        ["includeCallees"] = "direct"
                    };
                    JObject? startEnv = await SendWorkerCommandAsync(
                        startCmd, 10000,
                        "validate-start timeout",
                        env => env,
                        (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                        toolName: "genexus_apply_pattern.validate.start",
                        toolArgs: null, trackOperation: false);

                    string taskId = null;
                    JObject startInner = startEnv?["result"] as JObject;
                    if (startInner == null && startEnv?["result"] is JValue sjv && sjv.Type == JTokenType.String)
                    {
                        try { startInner = JObject.Parse(sjv.ToString()); } catch { }
                    }
                    taskId = startInner?["TaskId"]?.ToString() ?? startInner?["taskId"]?.ToString();

                    JObject terminal = null;
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        // Poll up to 180s. Adaptive interval: most validation
                        // builds of a single object finish in 1-3s, so poll fast
                        // (250ms) for the first 2s and fall back to 1s — cuts the
                        // common-case wait from ~1-4s to ~0.25-1s.
                        for (int i = 0; i < 180 && vSw.ElapsedMilliseconds < 180000; i++)
                        {
                            await Task.Delay(i < 8 ? 250 : 1000);
                            var statusCmd = new JObject
                            {
                                ["module"] = "Build",
                                ["action"] = "Status",
                                ["target"] = taskId
                            };
                            JObject? statusEnv = await SendWorkerCommandAsync(
                                statusCmd, 8000, "validate-poll timeout",
                                env => env,
                                (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                                toolName: "genexus_apply_pattern.validate.poll",
                                toolArgs: null, trackOperation: false);
                            JObject sObj = statusEnv?["result"] as JObject;
                            if (sObj == null && statusEnv?["result"] is JValue pjv && pjv.Type == JTokenType.String)
                            {
                                try { sObj = JObject.Parse(pjv.ToString()); } catch { }
                            }
                            string sStatus = sObj?["Status"]?.ToString() ?? sObj?["status"]?.ToString();
                            if (!string.IsNullOrEmpty(sStatus) && !string.Equals(sStatus, "Running", StringComparison.OrdinalIgnoreCase))
                            {
                                terminal = sObj;
                                break;
                            }
                        }
                    }
                    vSw.Stop();
                    vBlock["durationMs"] = vSw.ElapsedMilliseconds;

                    if (string.IsNullOrEmpty(taskId))
                    {
                        vBlock["status"] = "error";
                        vBlock["error"] = "Worker did not return a taskId for the validation build.";
                        if (startInner != null) vBlock["startResponse"] = startInner;
                    }
                    else if (terminal == null)
                    {
                        vBlock["status"] = "timeout";
                        vBlock["error"] = "Validation build did not finish in 180s.";
                        vBlock["taskId"] = taskId;
                    }
                    else
                    {
                        int errorCount = terminal["ErrorCount"]?.ToObject<int?>() ?? terminal["errorCount"]?.ToObject<int?>() ?? (terminal["Errors"] as JArray)?.Count ?? 0;
                        int warningCount = terminal["WarningCount"]?.ToObject<int?>() ?? terminal["warningCount"]?.ToObject<int?>() ?? (terminal["Warnings"] as JArray)?.Count ?? 0;
                        string termStatus = terminal["Status"]?.ToString() ?? "";
                        bool buildOk = errorCount == 0
                            && !string.Equals(termStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                            && terminal["error"] == null;
                        vBlock["status"] = buildOk ? "ok" : "failed";
                        vBlock["errorCount"] = errorCount;
                        vBlock["warningCount"] = warningCount;
                        vBlock["taskId"] = taskId;
                        var errArr = terminal["Errors"] as JArray ?? terminal["errors"] as JArray;
                        if (errArr != null && errArr.Count > 0)
                            vBlock["errors"] = new JArray(errArr.Take(10));
                        var warnArr = terminal["Warnings"] as JArray ?? terminal["warnings"] as JArray;
                        if (warnArr != null && warnArr.Count > 0)
                            vBlock["warnings"] = new JArray(warnArr.Take(5));
                    }
                }

                // Fold the validation block back into the toolResult text
                // payload, and promote isError when validation didn't pass.
                if (applyPayload != null)
                {
                    applyPayload["validation"] = vBlock;
                    var newText = applyPayload.ToString(Formatting.None);
                    var contentArr = innerResult["content"] as JArray;
                    if (contentArr != null && contentArr.Count > 0 && contentArr[0] is JObject c0)
                    {
                        c0["text"] = newText;
                    }
                    if (!string.Equals(vBlock["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(vBlock["status"]?.ToString(), "skipped", StringComparison.OrdinalIgnoreCase))
                    {
                        innerResult["isError"] = true;
                    }
                }
            }

            return innerResult ?? BuildToolResultContent(
                new JObject { ["error"] = "Tool did not produce a result." },
                isError: true,
                toolName: tName,
                toolArgs: tArgs);
        }

        /// <summary>
        /// Gateway-served tools (no worker involvement). Returns null when tName is
        /// not gateway-owned so the caller falls through to worker dispatch.
        /// Extracted from DispatchToolCallCoreAsync (phase: gateway tools).
        /// </summary>
        private static async Task<JObject?> TryDispatchGatewayToolAsync(
            string tName,
            JObject? tArgs,
            string sessionId,
            bool sessionContextEnabled)
        {
            // Gateway-served tools (no worker involvement)
            if (string.Equals(tName, "genexus_whoami", StringComparison.OrdinalIgnoreCase))
            {
                bool whoamiVerbose = tArgs?["verbose"]?.ToObject<bool>() ?? false;
                JObject whoami = await BuildWhoamiPayloadAsync(
                    whoamiVerbose,
                    sessionContextEnabled ? sessionId : null);
                return BuildToolResultContent(whoami, false, tName, tArgs);
            }

            // Doctor is a Gateway-owned health snapshot. Calling the Worker
            // version here made the result stale after a reload because the
            // health payload was tied to the old process and its counters.
            // The Gateway already has the current pool PID, index mirror and
            // operation tracker, and can answer even when startup failed.
            if (string.Equals(tName, "genexus_doctor", StringComparison.OrdinalIgnoreCase))
            {
                JObject doctor = BuildGatewayDoctorEnvelope(
                    sessionContextEnabled ? sessionId : null);
                return BuildToolResultContent(doctor, false, tName, tArgs);
            }

            if (string.Equals(tName, "genexus_recipe", StringComparison.OrdinalIgnoreCase))
            {
                string action = tArgs?["action"]?.ToString()?.ToLowerInvariant();
                JObject payload;
                bool isErr;

                if (string.Equals(action, "suggest_macro", StringComparison.OrdinalIgnoreCase))
                {
                    int windowMinutes = tArgs?["windowMinutes"]?.ToObject<int?>() ?? 30;
                    int minReps = tArgs?["minRepetitions"]?.ToObject<int?>() ?? 3;
                    var svc = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                    payload = svc.Suggest(windowMinutes, minReps);
                    isErr = string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(action, "crystallize", StringComparison.OrdinalIgnoreCase))
                {
                    string macroName = tArgs?["macroName"]?.ToString();
                    string description = tArgs?["description"]?.ToString();
                    var steps = tArgs?["steps"] as JArray;

                    // If steps were not supplied, try to re-derive from current history
                    // using the proposedName as the discriminator.
                    if (steps == null || steps.Count == 0)
                    {
                        var svc = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                        JObject sugg = svc.Suggest(60, 2);
                        if (sugg["candidateMacros"] is JArray arr)
                        {
                            foreach (var c in arr)
                            {
                                if (string.Equals(c?["proposedName"]?.ToString(), macroName, StringComparison.OrdinalIgnoreCase))
                                {
                                    steps = c["steps"] as JArray;
                                    if (string.IsNullOrWhiteSpace(description))
                                        description = c["suggestedDescription"]?.ToString();
                                    break;
                                }
                            }
                        }
                    }

                    var svc2 = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                    payload = svc2.Crystallize(macroName, description, steps);
                    isErr = string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Default: legacy behavior. action=list/describe via RecipeCatalog.Get.
                    // If action is provided and is list/describe, route via Dispatch.
                    string recipeName = tArgs?["name"]?.ToString();
                    if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(action, "describe", StringComparison.OrdinalIgnoreCase))
                    {
                        payload = RecipeCatalog.Dispatch(action, recipeName);
                    }
                    else if (!string.IsNullOrEmpty(action))
                    {
                        payload = new JObject
                        {
                            ["status"] = "Error",
                            ["error"] = $"Unknown action '{action}'.",
                            ["hint"] = "Supported: list, describe, suggest_macro, crystallize."
                        };
                    }
                    else
                    {
                        payload = RecipeCatalog.Get(recipeName);
                    }
                    isErr = payload?["error"] != null || string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                }

                return BuildToolResultContent(payload, isErr, tName, tArgs);
            }

            return null;
        }

        /// <summary>
        /// Async lifecycle build intercept (Tasks 4.3 + 4.4). Returns null when the
        /// call is not an async-eligible lifecycle build, or when the caller estimate
        /// selects the sync fast-path — the caller then falls through to normal
        /// synchronous dispatch. Extracted from DispatchToolCallCoreAsync.
        /// </summary>
        private static async Task<JObject?> TryDispatchAsyncLifecycleBuildAsync(
            string tName,
            string? lcAction,
            JObject? tArgs,
            JObject request,
            string sessionId,
            JToken? idToken,
            CancellationToken transportCancellation)
        {
            // build / rebuild actions go through path selection:
            //   - estimated_seconds &lt; BuildSyncThresholdSeconds  → sync fast-path (null)
            //   - estimated_seconds &gt;= BuildSyncThresholdSeconds  → async Task.Run, return job_id immediately
            if (!(string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(lcAction, "build", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lcAction, "build_all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lcAction, "rebuild", StringComparison.OrdinalIgnoreCase))
                && !IsLifecycleBuildDryRun(tArgs)))
                return null;

            // Issue #27 item 2: prefer a data-driven estimate (median of recent
            // build wall-clocks for this action) over the flat 60/120 the reporter
            // saw. Routing still keys on an EXPLICIT caller estimate only, so the
            // sync/async split is unchanged for callers that don't pass one — the
            // historical value only makes the reported estimated_seconds realistic
            // (history is recorded on async builds; letting it force the sync path
            // would create an oscillation the caller never asked for).
            int? callerEstimate = tArgs?["estimated_seconds"]?.ToObject<int?>();
            int estimatedSeconds = callerEstimate
                                   ?? JobRegistry.EstimateBuildSeconds($"lifecycle/{lcAction}")
                                   ?? (string.Equals(lcAction, "rebuild", StringComparison.OrdinalIgnoreCase) ? 120 : 60);
            int threshold = _activeConfig?.Server?.BuildSyncThresholdSeconds ?? 20;

            bool useSync = callerEstimate.HasValue && BuildPathSelector.UseSync(callerEstimate.Value, threshold);
            if (useSync)
            {
                // UseSync == true → fall through to the normal synchronous dispatch below
                Log($"[AsyncBuild] Short build (estimated={estimatedSeconds}s < threshold={threshold}s): using sync fast-path");
                return null;
            }

            // --- ASYNC PATH (Task 4.3) ---
            // Register the job first, then fire-and-forget the actual build.
            // The worker call is synchronous over the JSON-RPC pipe, so we wrap
            // it in Task.Run so the gateway thread returns to the caller immediately.
            var job = JobRegistry.Start(sessionId, $"lifecycle/{lcAction}", estimatedSeconds, GetCurrentOwnership(sessionId));
            Log($"[AsyncBuild] Dispatching job={job.Id} action={lcAction} target={tArgs?["target"]?.ToString() ?? "(all)"} estimated={estimatedSeconds}s");

            _ = Task.Run(async () =>
            {
                try
                {
                    // Step 1: Kick off the build on the worker — returns {status:"Accepted", taskId:...}
                    // v2.3.8 (Task 5.2) — forward callee-expansion knobs through the async path.
                    // Keep dryRun out of this branch entirely; the condition above routes
                    // previews through the normal worker command, where BuildDryRun runs.
                    var buildCmd = BuildAsyncLifecycleCommand(lcAction, tArgs, job.Id);

                    JObject? ackEnvelope = await SendWorkerCommandAsync(
                        buildCmd,
                        60000,
                        $"Timeout starting async build (job={job.Id})",
                        env => env,
                        (_, correlationId) => new JObject { ["error"] = "Gateway timeout starting build.", ["correlationId"] = correlationId },
                        toolName: tName, toolArgs: tArgs, trackOperation: false);

                    JObject? ack = (ackEnvelope?["result"] as JObject) ?? ackEnvelope;
                    if (ack == null || ack["error"] != null)
                    {
                        JobRegistry.Complete(job.Id, false,
                            $"Build start failed: {ack?["error"]?.ToString() ?? "unknown error"}", ack);
                        return;
                    }

                    string? taskId = ack["taskId"]?.ToString();
                    // Issue #27 item 1: record the worker task id on the job so a
                    // later status/result poll can reconcile against the worker's
                    // live build-task state if this background poller wedges.
                    if (!string.IsNullOrEmpty(taskId)) job.WorkerTaskId = taskId;
                    if (string.IsNullOrEmpty(taskId))
                    {
                        // No taskId means the worker returned a synchronous result already
                        // (or an error response). Complete with what we have.
                        bool syncSuccess = !string.Equals(ack["status"]?.ToString(), "Error",
                            StringComparison.OrdinalIgnoreCase) && ack["error"] == null;
                        JobRegistry.Complete(job.Id, syncSuccess,
                                    syncSuccess ? "Build completed (sync)" : $"Build error: {ack["error"]?.ToString() ?? "unknown"}",
                            ack);
                        return;
                    }

                    Log($"[AsyncBuild] job={job.Id} taskId={taskId} — polling status until terminal");

                    // v2.3.8 (Task 7.2) — register a CTS so lifecycle action=cancel
                    // with this job_id can short-circuit the polling loop.
                    var pollCt = JobRegistry.RegisterCancellation(job.Id);

                    // Step 2: Poll worker Build/Status until status is terminal.
                    // Terminal states from BuildTaskStatus: Succeeded | Failed | Error | Cancelled | ReorgRequired.
                    JObject? finalStatus = null;
                    int failedPolls = 0;
                    int pollCount = 0;
                    int hardCapSeconds = ResolveAsyncBuildHardCapSeconds(lcAction);
                    var hardCap = DateTime.UtcNow.AddSeconds(hardCapSeconds);
                    Log($"[AsyncBuild] job={job.Id} hard cap={hardCapSeconds}s");
                    while (DateTime.UtcNow < hardCap)
                    {
                        if (pollCt.IsCancellationRequested)
                        {
                            // Best-effort: tell the worker to kill the MSBuild child if any.
                            try
                            {
                                _ = SendWorkerCommandAsync(
                                    new JObject { ["module"] = "Build", ["action"] = "Cancel", ["target"] = taskId },
                                    5000, "cancel-fanout",
                                    env => env,
                                    (_, __) => new JObject(),
                                    toolName: tName, toolArgs: tArgs, trackOperation: false);
                            }
                            catch { /* fire-and-forget */ }
                            finalStatus = new JObject { ["status"] = "Cancelled", ["taskId"] = taskId };
                            break;
                        }
                        // Adaptive poll: builds take minutes, so the 2s
                        // interval only matters at the tail — but a fast
                        // first probe (500ms) catches sync-fast builds that
                        // finish between Start and the first Status call.
                        await Task.Delay(pollCount == 0 ? 500 : 2000).ConfigureAwait(false);
                        pollCount++;

                        var statusCmd = new JObject
                        {
                            ["module"] = "Build",
                            ["action"] = "Status",
                            ["target"] = taskId
                        };
                        JObject? statusEnv = await SendWorkerCommandAsync(
                            statusCmd,
                            30000,
                            $"Timeout polling build status (job={job.Id})",
                            env => env,
                            (_, correlationId) => new JObject { ["error"] = "Status poll timeout", ["correlationId"] = correlationId },
                            toolName: tName, toolArgs: tArgs, trackOperation: false);

                        // issue #113 — a dead worker must fail the job fast instead of
                        // looping until hardCap with the caller still waiting
                        // on wait_until_done / transport. Any error envelope here means
                        // the poll didn't reach the worker (crashed/exited/pipe gone);
                        // a single miss is tolerated, consecutive misses are terminal.
                        if (statusEnv == null || statusEnv["error"] != null)
                        {
                            failedPolls++;
                            Log($"[AsyncBuild] job={job.Id} status poll failed ({failedPolls}/{BuildStatusPollPolicy.MaxConsecutiveFailures})"
                                + (statusEnv?["error"] != null ? $": {statusEnv["error"]}" : ": empty response"));
                            if (BuildStatusPollPolicy.ShouldAbort(failedPolls))
                            {
                                string abortMsg = "Worker process exited mid-build (no response to " + failedPolls
                                    + " consecutive status polls). The build did NOT complete — check the crash ledger via "
                                    + "genexus_whoami diagnostics, then re-run genexus_lifecycle action=build.";
                                finalStatus = new JObject { ["status"] = "Failed", ["taskId"] = taskId, ["error"] = abortMsg };
                                JobRegistry.Complete(job.Id, false, abortMsg, finalStatus);
                                Log($"[AsyncBuild] job={job.Id} aborted: worker exited mid-build after {failedPolls} failed status polls.");
                                return;
                            }
                            continue;
                        }
                        failedPolls = 0;

                        finalStatus = (statusEnv?["result"] as JObject) ?? statusEnv;
                        string? s = finalStatus?["status"]?.ToString() ?? finalStatus?["Status"]?.ToString();
                        if (string.Equals(s, "Succeeded", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(s, "Cancelled", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(s, "ReorgRequired", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    // Step 3: Complete the JobRegistry entry with the real final status.
                    string? finalState = finalStatus?["status"]?.ToString() ?? finalStatus?["Status"]?.ToString() ?? "Timeout";
                    var finalOutcome = finalStatus != null
                        ? LifecycleResponseShaper.ClassifyBuildOutcome(finalStatus)
                        : LifecycleResponseShaper.BuildOutcome.Error;
                    bool success = finalOutcome == LifecycleResponseShaper.BuildOutcome.Success;
                    int errs = finalStatus?["errorCount"]?.ToObject<int?>() ?? finalStatus?["ErrorCount"]?.ToObject<int?>() ?? 0;
                    int warns = finalStatus?["warningCount"]?.ToObject<int?>() ?? finalStatus?["WarningCount"]?.ToObject<int?>() ?? 0;
                    string summary = string.Equals(finalState, "ReorgRequired", StringComparison.OrdinalIgnoreCase)
                        ? "Build All stopped because the KB requires reorganization; run action=reorg explicitly and retry."
                        : success
                        ? $"Build succeeded: {warns} warnings, {errs} errors"
                        : $"Build {finalState}: {errs} errors, {warns} warnings";
                    JobRegistry.Complete(job.Id, success, summary, finalStatus);
                    Log($"[AsyncBuild] Completed job={job.Id} status={finalState} errors={errs} warnings={warns}");
                }
                catch (Exception ex)
                {
                    JobRegistry.Complete(job.Id, false, $"Build exception: {ex.Message}");
                    Log($"[AsyncBuild] Exception in job={job.Id}: {ex.Message}");
                }
            });

            // Friction 2026-05-22: wait_until_done=true blocks in a single turn
            // up to MaxLongPollSeconds instead of forcing the caller to poll. Falls
            // back to job_id+running if the build outruns the cap.
            bool waitUntilDone = tArgs?["wait_until_done"]?.ToObject<bool?>() ?? false;
            if (waitUntilDone)
            {
                int blockingCap = tArgs?["wait_seconds"]?.ToObject<int?>() ?? McpRouter.MaxLongPollSeconds;
                var clientProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                bool hasProgressToken = clientProgressToken != null && clientProgressToken.Type != JTokenType.Null;
                string pendingLongPollKey = RegisterPendingLongPoll(
                    sessionId,
                    idToken,
                    transportCancellation,
                    out var longPollCancellationToken);
                JObject pollResult;
                try
                {
                    pollResult = await McpRouter.LongPollJob(
                        JobRegistry, job.Id, blockingCap,
                        progressToken: clientProgressToken,
                        heartbeat: hasProgressToken ? TryWriteStdout : null,
                        cancellationToken: longPollCancellationToken);
                }
                finally
                {
                    UnregisterPendingLongPoll(pendingLongPollKey);
                }
                // Classify the terminal status so the MCP envelope's isError
                // matches the build outcome. LongPollJob surfaces JobEntry.Status
                // which is one of: running, succeeded, failed, cancelled.
                // running == we hit the long-poll cap without termination — not an
                // error per se, the caller can re-poll.
                //
                // Friction 2026-05-22 item 10: this used to compare against
                // "completed" (which the registry never emits — it stamps
                // "succeeded"/"failed"). Result: every successful build wrapped
                // in an error envelope. Fix routes the inner BuildTaskStatus
                // through ClassifyBuildOutcome so 0/0/exit=0 = success and
                // partial_success surfaces as a warning marker, not an error.
                string terminalStatus = pollResult["status"]?.ToString();
                bool stillRunning = string.Equals(terminalStatus, "running", StringComparison.OrdinalIgnoreCase);
                bool isErr;
                if (stillRunning) isErr = false;
                else if (pollResult["result"] is JObject buildPayloadFinal)
                {
                    var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(buildPayloadFinal);
                    isErr = outcome == LifecycleResponseShaper.BuildOutcome.Error;
                    if (outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess)
                    {
                        pollResult["partial_success"] = true;
                        if (pollResult["envelope"] == null) pollResult["envelope"] = "warning";
                    }
                }
                else
                {
                    // No structured result — trust the registry summary string.
                    bool succeeded = string.Equals(terminalStatus, "succeeded", StringComparison.OrdinalIgnoreCase);
                    isErr = !succeeded;
                }
                if (!isErr
                    && LifecycleResponseShaper.ShouldCompact(tArgs)
                    && pollResult["result"] is JObject innerResult2)
                {
                    try { pollResult["result"] = LifecycleResponseShaper.CompactObject(innerResult2); } // perf: no serialize→parse round-trip
                    catch { /* shaper passthrough on non-JSON */ }
                }
                return BuildToolResultContent(pollResult, isErr, tName, tArgs);
            }

            // Return immediately with job_id
            var asyncResponse = BuildAsyncLifecycleAcceptedPayload(job, lcAction);
            if (McpTasksProtocol.SupportsTasks(request))
            {
                return McpTasksProtocol.BuildCreateTaskResult(
                    job,
                    asyncResponse["hint"]?.ToString() ?? "Build accepted; poll tasks/get for completion.");
            }
            return BuildToolResultContent(asyncResponse, false, tName, tArgs);
        }

        /// <summary>
        /// Async edit/variable/gxserver intercept. Returns null when the call is
        /// synchronous (no async=true on an async-eligible mutation) so the caller
        /// falls through to the normal worker dispatch.
        /// Extracted from DispatchToolCallCoreAsync.
        /// </summary>
        private static JObject? TryDispatchAsyncEdit(
            JObject workerCmd,
            string tName,
            JObject? tArgs,
            string sessionId,
            JObject request)
        {
            // async=true on edit/variable tools → fire-and-forget; result piggybacks via _meta.background_jobs.
            bool isAsyncGxServer = (tArgs?["async"]?.ToObject<bool?>() ?? false)
                                   && IsAsyncGxServerAction(tName, tArgs);
            // A preview is deliberately synchronous: it must never be represented as
            // a background mutation job, acquire a second operation identity, or outlive
            // the caller. The worker's dry-run branch performs no Save.
            bool editAsync = ShouldRunMutationAsync(tName, tArgs) || isAsyncGxServer;
            if (!editAsync) return null;

            // gxserver update on a stale KB runs many minutes; give it a longer
            // default estimate so the poll cadence is sensible.
            int estEdit = tArgs?["estimated_seconds"]?.ToObject<int?>() ?? (isAsyncGxServer ? 120 : 30);
            string jobLabel = isAsyncGxServer ? $"gxserver/{tArgs?["action"]?.ToString()}" : $"edit/{tName}";
            var editJob = JobRegistry.Start(sessionId, jobLabel, estEdit, GetCurrentOwnership(sessionId));
            editJob.WorkerAlias = _currentKb.Value?.NormalizedAlias;
            editJob.Target = GetAsyncMutationTarget(tName, tArgs);
            string ioAction = tArgs?["action"]?.ToString()?.ToLowerInvariant() ?? string.Empty;
            if (string.Equals(tName, "genexus_io", StringComparison.OrdinalIgnoreCase))
                editJob.Part = ioAction == "export_kb_to_text" ? "ObjectTextExport" : "ObjectText";
            else
                editJob.Part = tArgs?["part"]?.ToString() ?? "Source";
            editJob.ObjectType = tArgs?["type"]?.ToString();
            Log($"[AsyncEdit] Dispatching job={editJob.Id} tool={tName} estimated={estEdit}s");
            // v2.6.2 (Item B): inject cancelToken=jobId so the worker's
            // blanket-register at dispatch entry makes lifecycle cancel resolvable.
            if (workerCmd?["params"] is JObject capturedParams)
                capturedParams["cancelToken"] = editJob.Id;
            var capturedCmd = workerCmd;
            var capturedName = tName;
            _ = Task.Run(async () =>
            {
                try
                {
                    // Issue #79: SendWorkerCommandAsync is called with timeoutMs=0
                    // (wait forever) because a legitimately slow SDK save must not be
                    // cut off — but that means a BLOCKED SDK call (IDE modal dialog
                    // holding the model, or the SDK retrying a failing validation
                    // internally) left the job 'running' indefinitely with no
                    // actionable signal. Race the worker wait against a generous
                    // watchdog bound; on fire, mark the job terminal 'stalled' with
                    // recovery steps instead of hanging forever. gxserver update/commit
                    // is excluded (a server apply can legitimately run arbitrarily
                    // long — an 850-object changelist exceeded the 10 min sync
                    // ceiling), so its jobs wait without a stall bound.
                    int watchdogMs = isAsyncGxServer ? int.MaxValue : AsyncEditWatchdogMs(estEdit);
                    var cancelToken = JobRegistry.RegisterCancellation(editJob.Id);
                    var watchdogDelay = Task.Delay(watchdogMs, cancelToken);
                    var workerTask = SendWorkerCommandAsync(
                        capturedCmd, 0,
                        $"Timeout waiting for async edit: {capturedName}",
                        r => r, (_, __) => new JObject { ["status"] = "Running" },
                        operationIdentity: editJob.Id);
                    var completed = await Task.WhenAny(workerTask, watchdogDelay).ConfigureAwait(false);
                    if (completed != workerTask)
                    {
                        // Cancelled (delay faulted via the CTS) or deadline hit. A
                        // cancel already flipped the job to 'cancelled'; only stall
                        // when it is genuinely still running.
                        var now = JobRegistry.Get(editJob.Id);
                        if (now != null && string.Equals(now.Status, "running", StringComparison.OrdinalIgnoreCase))
                        {
                            int boundSeconds = watchdogMs == int.MaxValue ? -1 : watchdogMs / 1000;
                            string boundText = boundSeconds > 0 ? boundSeconds + "s" : "unbounded (watchdog disabled)";
                            // Plan 069: a genuinely stalled job means the worker's STA
                            // thread is stuck inside a blocked SDK call that will never
                            // answer the in-flight command. Marking the job 'stalled'
                            // alone left the KB wedged until the 15-min health-loop
                            // detector killed the process. Recycle the worker NOW
                            // (force-kill + Wedged → eager respawn) so the KB is usable
                            // again right away instead of ~15 minutes later. _currentKb
                            // is an AsyncLocal, so it still resolves the KB this job
                            // was dispatched against from inside this Task.Run.
                            bool workerRecycled = false;
                            var stalledKb = _currentKb.Value;
                            // A microsecond race: the worker may have answered between
                            // WhenAny returning the watchdog and this branch. Never
                            // force-kill a worker that just completed its save — only
                            // recycle when the command is genuinely still in flight.
                            if (stalledKb == null)
                            {
                                Log($"[AsyncEdit] No KB resolved for job={editJob.Id}; skipping stalled-worker recycle (health loop will reap the wedged process).");
                            }
                            else if (workerTask.IsCompleted)
                            {
                                Log($"[AsyncEdit] Job={editJob.Id} worker answered just after the watchdog fired — skipping recycle.");
                            }
                            else if (_workerPool == null)
                            {
                                Log($"[AsyncEdit] No worker pool available for job={editJob.Id}; skipping stalled-worker recycle (health loop will reap the wedged process).");
                            }
                            else
                            {
                                try { workerRecycled = _workerPool.RecycleStalledWorker(stalledKb.NormalizedAlias); }
                                catch (Exception recycleEx)
                                {
                                    Log($"[AsyncEdit] Stalled-worker recycle failed for KB '{stalledKb.Alias}': {recycleEx.Message}");
                                }
                            }
                            JobRegistry.Stall(
                                editJob.Id,
                                capturedName + " did not return within the " + boundText
                                    + " time bound; SDK call likely blocked (IDE modal dialog or retrying validation) — see result for recovery steps.",
                                BuildStalledAsyncMutationEnvelope(editJob.Id, capturedName, estEdit, boundSeconds, workerRecycled));
                            if (RequiresAsyncMutationRecovery(editJob))
                            {
                                _mutationRecovery.RequireRead(
                                    editJob.WorkerAlias,
                                    editJob.Target,
                                    editJob.Part,
                                    editJob.Id);
                            }
                            foreach (var recoveryTarget in EnumerateMutationRecoveryTargets(capturedName, tArgs))
                                _mutationRecovery.RequireRead(editJob.WorkerAlias, recoveryTarget.Target, recoveryTarget.Part, editJob.Id);
                            Log($"[AsyncEdit] Watchdog fired for job={editJob.Id} tool={capturedName} after {watchdogMs}ms — marked stalled (workerRecycled={workerRecycled}).");
                        }
                        return;
                    }
                    var inner = await workerTask;
                    bool ok = IsSuccessfulBackgroundToolCompletion(inner);
                    JobRegistry.Complete(editJob.Id, ok, BuildAsyncMutationCompletionSummary(capturedName, ok), inner);
                }
                catch (Exception ex)
                {
                    string failurePrefix = string.Equals(capturedName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(capturedName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(capturedName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(capturedName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase)
                        ? "Variable update exception"
                        : "Edit exception";
                    JobRegistry.Complete(editJob.Id, false, $"{failurePrefix}: {ex.Message}");
                    Log($"[AsyncEdit] Exception in job={editJob.Id}: {ex.Message}");
                }
            });
            var asyncEditResponse = isAsyncGxServer
                ? BuildAsyncAcceptedPayload(editJob, $"GXserver {tArgs?["action"]?.ToString()} accepted;")
                : (string.Equals(tName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(tName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(tName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(tName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase)
                ? BuildAsyncVariableAcceptedPayload(editJob)
                : BuildAsyncEditAcceptedPayload(editJob));
            if (McpTasksProtocol.SupportsTasks(request))
            {
                return McpTasksProtocol.BuildCreateTaskResult(
                    editJob,
                    asyncEditResponse["hint"]?.ToString() ?? "Operation accepted; poll tasks/get for completion.");
            }
            return BuildToolResultContent(asyncEditResponse, false, tName, tArgs);
        }
    }
}
