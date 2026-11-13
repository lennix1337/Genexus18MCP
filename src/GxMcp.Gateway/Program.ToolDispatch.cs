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
        /// Persist the post-timeout recovery evidence for a mutating tool and
        /// annotate the timeout envelope. Manifest imports deliberately use the
        /// reserved KB-level target: a successful read of one arbitrary object
        /// or Source part is not evidence for the whole manifest.
        /// </summary>
        internal static bool RegisterTimeoutRecoveryFence(
            MutationRecoveryRegistry recovery,
            string? kbAlias,
            string? toolName,
            JObject? toolArgs,
            string? operationId,
            JObject timeoutPayload)
        {
            if (recovery == null || string.IsNullOrWhiteSpace(kbAlias)
                || !IsMutatingTool(toolName ?? string.Empty, toolArgs)
                // A preview that cannot persist leaves nothing to reconcile; fencing the
                // object would block the real write that follows the preview.
                || OperationClassifier.IsEffectFreeDryRunPreview(toolName, toolArgs))
                return false;

            var targets = EnumerateMutationRecoveryTargets(toolName ?? string.Empty, toolArgs).ToList();
            if (targets.Count == 0) return false;

            string recoveryOperationId = string.IsNullOrWhiteSpace(operationId)
                ? "timeout-" + Guid.NewGuid().ToString("N")
                : operationId;
            foreach (var recoveryTarget in targets)
            {
                if (MutationRecoveryRegistry.IsKbLevelRecoveryTarget(
                    recoveryTarget.Target, recoveryTarget.Part))
                {
                    recovery.RequireRead(
                        kbAlias, recoveryTarget.Target, recoveryTarget.Part, recoveryOperationId);
                }
                else
                {
                    recovery.RequireRead(
                        kbAlias, recoveryTarget.Target, recoveryTarget.Part, recoveryOperationId,
                        toolArgs?["guid"]?.ToString(), toolArgs?["entityKey"]?.ToString(),
                        toolArgs?["type"]?.ToString(), toolArgs?["path"]?.ToString(),
                        toolArgs?["baseVersion"]?.ToString() ?? toolArgs?["expectedVersion"]?.ToString(),
                        string.Equals(toolName, "genexus_properties", StringComparison.OrdinalIgnoreCase)
                            ? (toolArgs?["properties"] as JObject)?.Properties().Select(p => p.Name)
                                ?? new[] { toolArgs?["propertyName"]?.ToString() ?? string.Empty }
                            : null);
                }
            }

            timeoutPayload["reReadRequired"] = true;
            MarkMutationOutcomeUnknown(timeoutPayload);
            if (timeoutPayload["error"] is JObject timeoutError)
            {
                timeoutError["retryable"] = false;
                timeoutError["reconciliationRequired"] = true;
                if (string.Equals(toolName, "genexus_properties", StringComparison.OrdinalIgnoreCase))
                    timeoutError["hint"] = "Do not repeat the write. After the Worker finishes, call genexus_properties action=get with reconcileTimedOutWrite=true and all affected propertyNames.";
            }
            if (targets.Any(target => MutationRecoveryRegistry.IsKbLevelRecoveryTarget(
                target.Target, target.Part)))
                timeoutPayload["recoveryScope"] = "kb";
            return true;
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
            if (transportCancellation.IsCancellationRequested)
                throw new PreDispatchCancelledException(transportCancellation);
            string tName = tcParams["name"]?.ToString() ?? "";
            var tArgs = tcParams["arguments"] as JObject;
            string kbScope = _currentKb.Value?.NormalizedAlias ?? "";
            JObject? modulePreviewError = ValidateModulePreview(tName, tArgs);
            if (modulePreviewError != null)
                return BuildToolResultContent(modulePreviewError, isError: true, toolName: tName, toolArgs: tArgs);

            // 1. CACHE INVALIDATION: If it's a write operation or a re-index, clear the cache.
            // PERF: compute once so the semantic-cache read below can skip the
            // (payload-serializing) key construction for mutating tools — the cache
            // was just cleared, so a lookup would be a guaranteed miss anyway.
            // genexus_edit used to serialize its full source payload into a key
            // for that doomed lookup on every call.
            bool isMutating = IsMutatingTool(tName, tArgs);
            // READ-ONLY MODE: fails closed, so it is checked against the classifier's proof of a
            // read rather than against isMutating (an unclassified tool is not a proven read).
            JObject? policyRefusal = TryRefuseByPolicy(tName, tArgs, isMutating);
            if (policyRefusal != null) return policyRefusal;
            // WRITE GATE: unconditional on purpose. A write refused because a peer
            // Gateway held the journal mid-commit would be a false block, so this
            // must not consult a change signal. The genexus_read branch below uses
            // RefreshIfChanged() instead; the asymmetry is deliberate, do not unify.
            if (isMutating && !IsMutationPreview(tArgs))
                _mutationRecovery.Refresh();
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
                JObject? fenceBlocked = TryRefuseWhenWriteFenced(tName, tArgs, kbScope);
                if (fenceBlocked != null) return fenceBlocked;
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
            string? lcAction = tArgs?["action"]?.ToString()?.ToLowerInvariant();
            // Live diagnostics and progress reads must always reflect current state.
            bool isLiveTool = IsLiveToolForCache(tName, lcAction);
            var recoveryReads = new List<(string Target, string Part, RecoveryRequirement? Observed)>();
            // Issue #357: a conditional read may omit its body. An unresolved write
            // fence may not be reconciled by an absent body, so when one covers the
            // part being read the token is honoured-but-overridden and a full
            // authoritative read is forced instead.
            bool requireAuthoritativeRead = false;
            bool propertiesRecoveryRead = string.Equals(tName, "genexus_properties", StringComparison.OrdinalIgnoreCase)
                && string.Equals(lcAction, "get", StringComparison.OrdinalIgnoreCase)
                && tArgs?["reconcileTimedOutWrite"]?.ToObject<bool?>() == true;
            if (string.Equals(tName, "genexus_read", StringComparison.OrdinalIgnoreCase) || propertiesRecoveryRead)
                _mutationRecovery.RefreshIfChanged();
            if (string.Equals(tName, "genexus_read", StringComparison.OrdinalIgnoreCase))
            {
                // READ PATH: conditional. The journal is shared with other Gateways,
                // so a fence written elsewhere must still be observed - the signal is
                // checked on every call - but a read is a cache-freshness check and
                // must not pay an exclusive cross-process lock, a full re-parse and a
                // directory glob on every call. The mutating branch above keeps the
                // unconditional Refresh(); the two are deliberately different.
                if (_mutationRecovery.Count > 0)
                    recoveryReads.AddRange(FindCoveredRecoveryReads(_mutationRecovery, CurrentKbScopes(), tArgs));
                requireAuthoritativeRead = recoveryReads.Count > 0;
                // A cached response cannot reconcile an uncertain persisted write.
                isLiveTool |= !_mutationRecovery.IsHealthy || _mutationRecovery.Count > 0;
            }
            else if (propertiesRecoveryRead)
            {
                recoveryReads.AddRange(_mutationRecovery.FindForRead(kbScope, tArgs, "Properties")
                    .Select(observed => (observed.Target, observed.Part, (RecoveryRequirement?)observed)));
                isLiveTool = true;
            }

            // Scope the semantic cache by the resolved KB: the same tool+args
            // against two different open KBs must not share envelopes (the
            // worker is single-KB, so an identical read against KB-B could
            // otherwise replay KB-A's cached result).
            // PERF: only build the key (which serializes the whole args tree) when
            // a lookup can possibly hit. Mutating tools just cleared the cache
            // (guaranteed miss) and live tools never read from it, so for those
            // the key — and the expensive payload ToString for genexus_edit/write
            // — is pure waste.
            // Refresh also discards previously cached aliases for this KB. No external
            // IDE notification is required; unrelated KB scopes remain warm.
            InvalidateRefreshReadScope(_semanticCache, kbScope, tName, tArgs);
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
            JObject? cacheHit = TryServeSemanticCacheHit(cKey, tName, tArgs);
            if (cacheHit != null) return cacheHit;

            // Rebuild full request so ConvertToolCall works
            var fullReq = new JObject
            {
                ["method"] = "tools/call",
                ["params"] = tcParams
            };

            // Issue #356: a `kbs=[...]` federation is answered before the single-KB
            // index gate below, which would otherwise judge the whole call by the
            // session KB's index state — a question the federation answers per KB.
            // Returns null for an ordinary single-KB query.
            // Deliberately ahead of the semantic cache as well: a cached
            // federation would need per-KB revision bookkeeping to invalidate
            // correctly, and a stale multi-KB answer is more misleading than an
            // uncached one. Each leg is still recorded in ToolLatencyStats.
            JObject? multiKbResult = await TryDispatchMultiKbDiscoveryAsync(tName, tArgs, transportCancellation);
            if (multiKbResult != null) return multiKbResult;

            // gx-engine real routing (docs/engine_backend.md): with Engine.Backend=gxengine,
            // the parity capabilities that declare product consumers serve their operations
            // here — before the SDK index gate, which must not short-circuit a read, query or
            // lifecycle build the engine can answer without the worker index. A call outside
            // the admitted space (or a failed engine invocation) returns null and continues on
            // the SDK path, or refuses under Engine.Fallback=error. Returns null when the
            // backend is sdk or the tool is not one of the routed consumers.
            JObject? engineRoutedResult = await TryDispatchEngineRoutedOperationAsync(
                tName, lcAction, tArgs, _currentKb.Value?.Path, _currentKb.Value?.Alias, transportCancellation);
            if (engineRoutedResult != null) return engineRoutedResult;

            // Index gate: gateway-side fast-fail for SDK-bound tools while the Worker index is not usable.
            JObject? indexGateResult = await TryRefuseWhenIndexNotReadyAsync(tName, tArgs, transportCancellation);
            if (indexGateResult != null) return indexGateResult;

            // Gateway-served tools (factored method below; null = fall through).
            // genexus_wire action=pack_task on the open native KB is the one Gateway-owned call
            // the Worker answers (SDK-backed index, no text tree): fall through to the worker command below.
            JObject? gatewayResult = GxwireNativePackTask.Applies(tName, tArgs, _currentKb.Value?.Path)
                ? null
                : await TryDispatchGatewayToolAsync(tName, tArgs, sessionId, sessionContextEnabled);
            if (gatewayResult != null) return gatewayResult;

            // genexus_impact whatIf=true: Gateway-composed read (null for every other call).
            JObject? whatIfResult = await TryDispatchImpactWhatIfAsync(
                tName, tArgs, request, sessionId, sessionContextEnabled, idToken, transportCancellation);
            if (whatIfResult != null) return whatIfResult;

            // Async lifecycle build (factored TryDispatchAsyncLifecycleBuildAsync below).
            // build / rebuild actions go through path selection (see factored method below):
            // null = not an async-eligible build, or sync fast-path → fall through.
            JObject? asyncBuildResult = await TryDispatchAsyncLifecycleBuildAsync(tName, lcAction, tArgs, request, sessionId, idToken, transportCancellation);
            if (asyncBuildResult != null) return asyncBuildResult;

            object? rawWorkerCmd = BuildFileTransferWorkerCommand(tName, tArgs);

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
                    BuildNoWorkerCommandError(tName, tArgs),
                    isError: true,
                    toolName: tName,
                    toolArgs: tArgs);
            }

            workerCmd["client"] = "mcp";
            if (requireAuthoritativeRead)
            {
                // Set on the routed command, not on tArgs: the caller's arguments
                // must keep describing what they asked for, and no pre-routing
                // decision (cache key, gateway-served tools, timeouts) may observe
                // this internal fence. The names are the ones the Worker reads (it
                // receives the whole routed command as its arguments): the previous
                // `_requireAuthoritativeRead` spelling was never read, so a
                // conditional token could still turn a fenced read into "not modified".
                ApplyRecoveryReadFlags(workerCmd);
            }
            AttachClientRequestIdentity(workerCmd, tArgs);
            int timeoutMs = GetToolTimeoutMs(tName, tArgs);
            // Still needed by the timeout handler below (recovery hints for
            // gxserver writes); the async-edit intercept recomputes its own copy.
            bool isAsyncGxServer = (tArgs?["async"]?.ToObject<bool?>() ?? false)
                                   && IsAsyncGxServerAction(tName, tArgs);

            // Async edit/variable/gxserver (factored method below; null = run synchronously).
            JObject? asyncEditResult = TryDispatchAsyncEdit(workerCmd, tName, tArgs, sessionId, request);
            if (asyncEditResult != null) return asyncEditResult;

            // Issue #325: async=true on a long version-store read hands back an operationId
            // before the SDK call starts, so the result is still retrievable when the
            // caller's synchronous window expires (null = run synchronously).
            JObject? asyncReadResult = TryDispatchAsyncRead(workerCmd, tName, tArgs, sessionId, request);
            if (asyncReadResult != null) return asyncReadResult;

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
            var responseCtx = new ToolCallResponseContext
            {
                ToolName = tName,
                ToolArgs = tArgs,
                LcAction = lcAction,
                KbScope = kbScope,
                RecoveryReads = recoveryReads,
                PropertiesRecoveryRead = propertiesRecoveryRead,
                CacheKey = cKey,
                IsLiveTool = isLiveTool,
                CacheEpochAtDispatch = cacheEpochAtDispatch,
                CacheRevisionAtDispatch = cacheRevisionAtDispatch,
                KbPath = _currentKb.Value?.Path ?? _activeConfig?.Environment?.KBPath,
                KbAlias = _currentKb.Value?.Alias,
                SessionId = sessionId
            };
            innerResult = await SendWorkerCommandAsync(
                workerCmd,
                timeoutMs,
                $"Timeout waiting for tool: {tName}",
                resultObj => ShapeWorkerToolResponse(resultObj, responseCtx),
                (operationId, correlationId) =>
                    BuildWorkerTimeoutToolResult(operationId, correlationId, responseCtx, isAsyncGxServer),
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

            // A non-refresh read can start after the pre-dispatch invalidation and
            // populate a new-generation alias from the Worker before this SDK reload
            // completes. Advance the generation again so in-flight cache fills cannot
            // survive the authoritative refresh.
            InvalidateRefreshReadScope(_semanticCache, kbScope, tName, tArgs);

            // The same race exists for every write (#653): the pre-dispatch invalidation
            // above opens a new generation, a read that starts inside it (a batched
            // edit+read, or a read while a slow edit is still saving) is cached with the
            // pre-edit source, and the generation fence cannot tell that entry from a
            // fresh one. Close the generation again now that the write has settled
            // (applied, refused or timed out).
            InvalidateSettledMutationScope(_semanticCache, kbScope, tArgs, isMutating);

            // apply_pattern { validate: true }: post-apply validation build (no-op for other tools).
            await ValidateAppliedPatternAsync(innerResult, tName, tArgs);

            // genexus_edit { validationMode: "native" }: opt-in, budgeted, fail-soft engine diagnostics
            // of the saved object. Returns at once when the call did not ask for it.
            await AttachNativeDiagnosticsAsync(innerResult, tName, tArgs, transportCancellation);

            return innerResult ??BuildToolResultContent(
                new JObject { ["error"] = "Tool did not produce a result." },
                isError: true,
                toolName: tName,
                toolArgs: tArgs);
        }

        /// <summary>
        /// Environment policy gates: read-only mode and the write-KB allowlist. Returns the
        /// refusal result, or null when the call may continue. Extracted from
        /// DispatchToolCallCoreAsync (phase: policy).
        /// </summary>
        private static JObject? TryRefuseByPolicy(string tName, JObject? tArgs, bool isMutating)
        {
            if (ReadOnlyMode.FromEnvironment().Refuses(tName, tArgs))
                return BuildToolResultContent(ReadOnlyMode.BuildRefusal(tName), isError: true, toolName: tName, toolArgs: tArgs);
            if (isMutating && !IsMutationPreview(tArgs))
            {
                var writeAllowlist = WriteKbAllowlist.FromEnvironment();
                string? writeKbPath = _currentKb.Value?.Path ?? _activeConfig?.Environment?.KBPath;
                if (!writeAllowlist.Allows(writeKbPath))
                    return BuildToolResultContent(
                        WriteKbAllowlist.BuildRefusal(tName, writeKbPath ?? "(no KB resolved)", writeAllowlist.Raw),
                        isError: true,
                        toolName: tName,
                        toolArgs: tArgs);
            }
            return null;
        }

        /// <summary>
        /// Refuses a write that a still-unreconciled recovery fence covers; null when no
        /// fence applies. Extracted from DispatchToolCallCoreAsync (phase: write fence).
        /// </summary>
        private static JObject? TryRefuseWhenWriteFenced(string tName, JObject? tArgs, string kbScope)
        {
            RecoveryRequirement? recoveryRequirement = null;
            // Every scope the KB's fences may carry: a fence recorded under the folder
            // alias by a call that addressed the KB by path still blocks the write.
            IReadOnlyList<string> fenceScopes = _mutationRecovery.Count > 0
                ? CurrentKbScopes()
                : Array.Empty<string>();
            foreach (var recoveryTarget in EnumerateMutationRecoveryTargets(tName, tArgs))
            {
                foreach (var fenceScope in fenceScopes.Count > 0 ? (IEnumerable<string>)fenceScopes : new[] { kbScope })
                {
                    if (_mutationRecovery.TryGet(fenceScope, recoveryTarget.Target, recoveryTarget.Part, out var found))
                    {
                        recoveryRequirement = found;
                        break;
                    }
                }
                if (recoveryRequirement != null) break;
            }
            if (recoveryRequirement != null)
            {
                return BuildToolResultContent(
                    MutationRecoveryRegistry.BuildBlockedEnvelope(recoveryRequirement),
                    isError: true,
                    toolName: tName,
                    toolArgs: tArgs);
            }
            return null;
        }

        /// <summary>
        /// Gateway semantic-cache lookup: the cached tool result stamped as a hit, or null
        /// on a miss (or a null key). Extracted from DispatchToolCallCoreAsync (phase: cache lookup).
        /// </summary>
        private static JObject? TryServeSemanticCacheHit(string? cKey, string tName, JObject? tArgs)
        {
            if (cKey != null && _semanticCache.TryGet(cKey, out var cachedResponse, out long cacheAgeMs))
            {
                if (_verboseRequestLogs) Log($"[Cache] HIT for {tName}");
                var cached = cachedResponse["result"] as JObject;
                if (cached != null)
                {
                    var hit = (JObject)cached.DeepClone();
                    var hitMeta = hit["_meta"] as JObject ?? new JObject();
                    hit["_meta"] = hitMeta;
                    hitMeta["cacheOutcome"] = "hit";
                    hitMeta["cacheSource"] = "gateway-semantic-cache";
                    hitMeta["cacheAgeMs"] = cacheAgeMs;
                    AttachGatewaySemanticCacheAge(hit, cacheAgeMs);
                    AttachExactReadIndexMetadata(hit, tName, tArgs);
                    _operationTracker.RecordCacheHit(tName);
                    return hit;
                }
            }
            return null;
        }

        /// <summary>
        /// Worker command for the two file-transfer tools the router does not convert
        /// (export/import object text); null for every other tool.
        /// Extracted from DispatchToolCallCoreAsync (phase: worker command).
        /// </summary>
        private static object? BuildFileTransferWorkerCommand(string tName, JObject? tArgs)
        {
            if (string.Equals(tName, "genexus_export_object", StringComparison.OrdinalIgnoreCase))
            {
                return new
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
            if (string.Equals(tName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    module = "Object",
                    action = "ImportText",
                    target = tArgs?["name"]?.ToString(),
                    path = tArgs?["inputPath"]?.ToString(),
                    part = tArgs?["part"]?.ToString() ?? "Source",
                    type = tArgs?["type"]?.ToString()
                };
            }
            return null;
        }

        /// <summary>
        /// Error for a tool call that neither the Gateway nor any router turned into a Worker
        /// command (issue #662). Names the tool and action so the caller can tell a mistyped
        /// action from a Gateway routing gap, instead of a generic conversion failure.
        /// </summary>
        internal static JObject BuildNoWorkerCommandError(string toolName, JObject? toolArgs)
        {
            string? action = toolArgs?["action"]?.ToString();
            string target = string.IsNullOrWhiteSpace(action)
                ? $"Tool '{toolName}'"
                : $"Tool '{toolName}' action '{action}'";
            string hint = string.Equals(toolName, OperationClassifier.KbTool, StringComparison.OrdinalIgnoreCase)
                ? "genexus_kb actions are list, open, close, select, set_session_default, set_persistent_default, set_default, create, set_startup, get_startup, list_environments, get_environment and set_environment. " +
                  "action=open needs no configured KB: pass path=<KB root> (alias optional)."
                : "Check the action name against the tool schema (tools/list).";
            return new JObject
            {
                ["error"] = $"{target} produced no Worker command: the Gateway has no route for it.",
                ["code"] = "TOOL_ROUTE_NOT_FOUND",
                ["tool"] = toolName,
                ["action"] = action,
                ["hint"] = hint
            };
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

            // Doctor is answered by the Gateway from its own state (pool PID, index
            // mirror, operation tracker), so it still answers when startup failed.
            // Its one Worker read, the WorkWithPlus template block, is asked only of
            // a Worker that is already running; see ProbeDoctorWwpTemplatesAsync.
            if (string.Equals(tName, "genexus_doctor", StringComparison.OrdinalIgnoreCase))
            {
                string? doctorSessionId = sessionContextEnabled ? sessionId : null;
                JObject wwpTemplates = await ProbeDoctorWwpTemplatesAsync(doctorSessionId);
                JObject doctor = BuildGatewayDoctorEnvelope(
                    doctorSessionId, wwpTemplates, await BuildSharedHostBlockAsync());
                return BuildToolResultContent(doctor, false, tName, tArgs);
            }

            if (string.Equals(tName, "genexus_wire", StringComparison.OrdinalIgnoreCase))
            {
                JObject wireResult = await ExecuteGxwireToolAsync(tArgs, sessionId, sessionContextEnabled);
                bool isErr = wireResult["error"] != null || string.Equals(wireResult["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase);
                try
                {
                    return BuildToolResultContent(wireResult, isErr, tName, tArgs);
                }
                catch (Exception shapeEx)
                {
                    // The envelope itself is the last thing that can throw on this
                    // path; if it does, the client still gets a coded answer and
                    // not the transport's bare `Internal error` (#593).
                    Log($"[genexus_wire] envelope shaping failed: {shapeEx.GetType().Name}: {shapeEx.Message}");
                    var fallback = new JObject
                    {
                        ["status"] = "error",
                        ["error"] = GxwireErrorCodes.UnexpectedException,
                        ["code"] = GxwireErrorCodes.UnexpectedException,
                        ["message"] = "genexus_wire produced a result the gateway could not shape into a response: "
                            + GxwireTool.Head(shapeEx.Message, GxwireTool.GxwireExceptionMessageChars),
                        ["originalCode"] = wireResult["code"]?.ToString() ?? wireResult["error"]?.ToString(),
                        ["diagnosticContext"] = new JObject
                        {
                            ["tool"] = "genexus_wire",
                            ["stage"] = "envelope",
                            ["exceptionType"] = shapeEx.GetType().Name
                        }
                    };
                    return BuildToolResultContent(fallback, true, tName, tArgs);
                }
            }

            if (string.Equals(tName, "genexus_recipe", StringComparison.OrdinalIgnoreCase))
            {
                string? action = tArgs?["action"]?.ToString()?.ToLowerInvariant();
                JObject payload;
                bool isErr;

                if (string.Equals(action, "suggest_macro", StringComparison.OrdinalIgnoreCase))
                {
                    int windowMinutes = tArgs?["windowMinutes"]?.ToObject<int?>() ?? 30;
                    int minReps = tArgs?["minRepetitions"]?.ToObject<int?>() ?? 3;
                    var svc = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                    payload = svc.Suggest(windowMinutes, minReps);
                    isErr = string.Equals(payload["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(action, "crystallize", StringComparison.OrdinalIgnoreCase))
                {
                    string? macroName = tArgs?["macroName"]?.ToString();
                    string? description =tArgs?["description"]?.ToString();
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
                                if (string.Equals(c["proposedName"]?.ToString(), macroName, StringComparison.OrdinalIgnoreCase))
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
                    isErr = string.Equals(payload["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Default: legacy behavior. action=list/describe via RecipeCatalog.Get.
                    // If action is provided and is list/describe, route via Dispatch.
                    string? recipeName = tArgs?["name"]?.ToString();
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
                    isErr = payload["error"] != null || string.Equals(payload["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                }

                return BuildToolResultContent(payload, isErr, tName, tArgs);
            }

            return null;
        }

        // genexus_wire execution lives in Program.Gxwire.cs (#593): every failure
        // path returns a coded envelope with diagnosticContext, and the process
        // launch is a replaceable seam so the paths are testable without gxwire.exe.

        /// <summary>Reads bounded Worker index status without hiding error envelopes.</summary>
        private static async Task<JObject?> ReadLifecycleIndexStatusAsync(
            string jobId, string toolName, JObject? toolArgs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JObject? envelope = await SendWorkerCommandAsync(
                new JObject
                {
                    ["module"] = "KB", ["action"] = "GetIndexStatus",
                    ["wait"] = 30, ["freshness"] = "current"
                },
                35000, $"Timeout waiting for index completion (job={jobId})",
                env => env,
                (_, correlationId) => new JObject { ["error"] = "Index status timeout", ["correlationId"] = correlationId },
                toolName: toolName, toolArgs: toolArgs, trackOperation: false);
            return envelope?["error"] != null ? envelope : (envelope?["result"] as JObject) ?? envelope;
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
            // build / rebuild actions, and specify with wait_until_done=true, go through path selection:
            //   - estimated_seconds &lt; BuildSyncThresholdSeconds  → sync fast-path (null)
            //   - estimated_seconds &gt;= BuildSyncThresholdSeconds  → async Task.Run, return job_id immediately
            if (!ShouldDispatchLifecycleBuildAsync(tName, lcAction, tArgs))
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
            bool waitUntilDone = tArgs?["wait_until_done"]?.ToObject<bool?>() ?? false;
            bool registryOnlyAction = string.Equals(lcAction, "validate-kb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lcAction, "reorg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lcAction, "index", StringComparison.OrdinalIgnoreCase);

            // A caller asking for wait_until_done must stay on the registry path even
            // when it supplies a small estimate. The same is true for the other long
            // actions: their worker operation has no task id to poll synchronously.
            bool useSync = !waitUntilDone && !registryOnlyAction
                && callerEstimate.HasValue && BuildPathSelector.UseSync(callerEstimate.Value, threshold);
            if (useSync)
            {
                // UseSync == true → fall through to the normal synchronous dispatch below
                Log($"[AsyncBuild] Short build (estimated={estimatedSeconds}s < threshold={threshold}s): using sync fast-path");
                return null;
            }

            // --- ASYNC PATH (Task 4.3) ---
            // Admission happens before Task.Run/SendWorkerCommandAsync. A queued
            // caller therefore receives an operation handle immediately instead of
            // waiting behind the Worker STA and only then seeing a late rejection.
            var ownership = GetCurrentOwnership(sessionId);
            // KbId is the stable physical-worker identity; aliases can differ
            // between independent Gateway attachments to the same shared Worker.
            string workerScope = _currentKb.Value?.KbId ?? ownership.KbId;
            bool serialize = !string.Equals(
                Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS"),
                "1", StringComparison.OrdinalIgnoreCase);
            // A finished build whose poller never completed its job would keep every later
            // build queued; ask the Worker about the scope's running jobs first (#418).
            if (serialize)
            {
                foreach (var stale in JobRegistry.GetReconciliationCandidates(workerScope, ownership))
                    await ReconcileJobWithWorkerAsync(stale, tName, tArgs);
            }
            var admission = JobRegistry.AdmitLifecycle(
                sessionId,
                $"lifecycle/{lcAction}",
                estimatedSeconds,
                ownership,
                workerScope,
                BuildLifecycleRequestKey(lcAction, tArgs),
                serialize);
            var job = admission.Job;
            if (admission.IsNew)
            {
                job.WorkerAlias = _currentKb.Value?.NormalizedAlias;
                job.QueueAction = lcAction;
                job.QueueTarget = tArgs?["target"]?.ToString();
            }
            Log($"[AsyncBuild] Dispatching job={job.Id} action={lcAction} target={tArgs?["target"]?.ToString() ?? "(all)"} estimated={estimatedSeconds}s status={job.Status} coalesced={admission.Coalesced}");

            // Only the canonical operation owns a worker command.  A coalesced caller
            // joins the existing poller and therefore cannot start a second build.
            if (admission.IsNew)
            {
                _ = Task.Run(async () =>
            {
                try
                {
                    if (!await JobRegistry.WaitForLifecycleAdmissionAsync(job.Id, CancellationToken.None))
                    {
                        JobRegistry.Complete(job.Id, false, "Lifecycle admission cancelled before worker dispatch.");
                        return;
                    }

                    // Step 1: Kick off the build on the worker — returns {status:"Accepted", taskId:...}
                    // v2.3.8 (Task 5.2) — forward callee-expansion knobs through the async path.
                    // Keep dryRun out of this branch entirely; the condition above routes
                    // previews through the normal worker command, where BuildDryRun runs.
                    var buildCmd = BuildAsyncLifecycleCommand(lcAction, tArgs, job.Id);

                    int admissionTimeoutMs = string.Equals(lcAction, "validate-kb", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(lcAction, "index", StringComparison.OrdinalIgnoreCase)
                        ? 600000
                        : 60000;
                    JObject? ackEnvelope = await SendWorkerCommandAsync(
                        buildCmd,
                        admissionTimeoutMs,
                        $"Timeout starting async lifecycle action (job={job.Id})",
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
                    if (!string.IsNullOrEmpty(taskId)) JobRegistry.SetWorkerTaskId(job.Id, taskId);
                    if (string.IsNullOrEmpty(taskId))
                    {
                        // BulkIndex acknowledges a background operation, not its completion.
                        // Keep the lifecycle job live until that exact index is idle/current.
                        if (string.Equals(lcAction, "index", StringComparison.OrdinalIgnoreCase))
                        {
                            await LifecycleIndexCompletion.CompleteJobAsync(
                                JobRegistry, job, ack,
                                ct => ReadLifecycleIndexStatusAsync(job.Id, tName, tArgs, ct),
                                TimeSpan.FromSeconds(ResolveAsyncBuildHardCapSeconds(lcAction)));
                            return;
                        }
                        // Other taskless actions complete inside the Worker command.
                        bool syncSuccess = IsSuccessfulBackgroundToolCompletion(ack)
                            && !string.Equals(ack["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase)
                            && ack["error"] == null;
                        JobRegistry.Complete(job.Id, syncSuccess,
                                    syncSuccess ? "Lifecycle action completed" : $"Lifecycle action error: {ack["error"]?.ToString() ?? "unknown"}",
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
                    bool terminalWorkerState =
                        string.Equals(finalState, "Succeeded", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(finalState, "Failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(finalState, "Error", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(finalState, "Cancelled", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(finalState, "ReorgRequired", StringComparison.OrdinalIgnoreCase);
                    if (terminalWorkerState)
                    {
                        // Worker Status is a delta surface. Hydrate the canonical
                        // Gateway operation with Worker Result before storing it so
                        // action=result retains the complete warning list.
                        JObject? fullResult = await ReadWorkerLifecycleResultAsync(
                            taskId, tArgs, CancellationToken.None);
                        if (fullResult != null)
                        {
                            finalStatus = fullResult;
                            finalState = fullResult["status"]?.ToString()
                                ?? fullResult["Status"]?.ToString()
                                ?? finalState;
                        }
                    }
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
            }

            // Friction 2026-05-22: wait_until_done=true blocks in a single turn
            // up to MaxLongPollSeconds instead of forcing the caller to poll. Falls
            // back to job_id+running if the build outruns the cap.
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
                        cancellationToken: longPollCancellationToken,
                        until: "terminal");
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
                string? terminalStatus = pollResult["status"]?.ToString();
                bool stillRunning = string.Equals(terminalStatus, "running", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(terminalStatus, "queued", StringComparison.OrdinalIgnoreCase);
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

            // Return immediately with the canonical operation identity and queue
            // metadata.  A coalesced caller gets the same operationId and observes the
            // existing queue position instead of creating a second execution.
            var asyncResponse = BuildAsyncLifecycleAcceptedPayload(job, lcAction);
            asyncResponse["coalesced"] = admission.Coalesced;
            var queueMetadata = JobRegistry.GetLifecycleQueueMetadata(job.Id);
            foreach (var property in queueMetadata.Properties())
            {
                if (property.Name == "status" && admission.IsNew) continue;
                asyncResponse[property.Name] = property.Value?.DeepClone();
            }
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
            var recoveryTargets = EnumerateMutationRecoveryTargets(tName, tArgs).ToList();
            string jobLabel = isAsyncGxServer ? $"gxserver/{tArgs?["action"]?.ToString()}" : $"edit/{tName}";
            var editJob = JobRegistry.Start(sessionId, jobLabel, estEdit, GetCurrentOwnership(sessionId));
            editJob.WorkerAlias = _currentKb.Value?.NormalizedAlias;
            editJob.Target = GetAsyncMutationTarget(tName, tArgs);
            string ioAction = tArgs?["action"]?.ToString()?.ToLowerInvariant() ?? string.Empty;
            if (string.Equals(tName, "genexus_io", StringComparison.OrdinalIgnoreCase))
                editJob.Part = ioAction == "export_kb_to_text" ? "ObjectTextExport" : "ObjectText";
            else
                editJob.Part = recoveryTargets.FirstOrDefault(target =>
                    string.Equals(target.Target, editJob.Target, StringComparison.OrdinalIgnoreCase)).Part
                    ?? tArgs?["part"]?.ToString()
                    ?? recoveryTargets.FirstOrDefault().Part
                    ?? "Source";
            editJob.ObjectType = tArgs?["type"]?.ToString();
            editJob.TargetGuid = tArgs?["guid"]?.ToString();
            editJob.TargetEntityKey = tArgs?["entityKey"]?.ToString();
            editJob.TargetPath = tArgs?["path"]?.ToString();
            editJob.ExpectedVersion = tArgs?["baseVersion"]?.ToString() ?? tArgs?["expectedVersion"]?.ToString();
            Log($"[AsyncEdit] Dispatching job={editJob.Id} tool={tName} estimated={estEdit}s");
            // v2.6.2 (Item B): inject cancelToken=jobId so the worker's
            // blanket-register at dispatch entry makes lifecycle cancel resolvable.
            if (workerCmd["params"] is JObject capturedParams)
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
                            var stalledEnvelope = BuildStalledAsyncMutationEnvelope(
                                editJob.Id, capturedName, estEdit, boundSeconds, workerRecycled);
                            if (recoveryTargets.Any(target => MutationRecoveryRegistry.IsKbLevelRecoveryTarget(
                                target.Target, target.Part)))
                            {
                                stalledEnvelope["recoveryScope"] = "kb";
                                stalledEnvelope["hint"] = "Reconcile the manifest/KB state before retrying; no single object/part read can clear this fence.";
                            }
                            JobRegistry.Stall(
                                editJob.Id,
                                capturedName + " did not return within the " + boundText
                                    + " time bound; SDK call likely blocked (IDE modal dialog or retrying validation) — see result for recovery steps.",
                                stalledEnvelope);
                            foreach (var recoveryTarget in recoveryTargets)
                            {
                                if (MutationRecoveryRegistry.IsKbLevelRecoveryTarget(
                                    recoveryTarget.Target, recoveryTarget.Part))
                                {
                                    _mutationRecovery.RequireRead(
                                        editJob.WorkerAlias, recoveryTarget.Target, recoveryTarget.Part, editJob.Id);
                                }
                                else
                                {
                                    _mutationRecovery.RequireRead(
                                        editJob.WorkerAlias, recoveryTarget.Target, recoveryTarget.Part, editJob.Id,
                                        tArgs?["guid"]?.ToString(), tArgs?["entityKey"]?.ToString(),
                                        tArgs?["type"]?.ToString(), tArgs?["path"]?.ToString(),
                                        tArgs?["baseVersion"]?.ToString() ?? tArgs?["expectedVersion"]?.ToString());
                                }
                            }
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

        /// <summary>
        /// Async read intercept (issue #325). Returns null when the call is not an
        /// async-eligible version-store read, so the caller falls through to the normal
        /// synchronous worker dispatch. When it does apply, the operation is registered
        /// and its identifier returned immediately; the SDK read itself runs detached and
        /// its envelope is stored on the job for genexus_lifecycle action=result.
        /// </summary>
        private static JObject? TryDispatchAsyncRead(
            JObject workerCmd,
            string tName,
            JObject? tArgs,
            string sessionId,
            JObject request)
        {
            if (!IsAsyncLongReadAction(tName, tArgs)) return null;

            string action = tArgs!["action"]?.ToString()?.Trim().ToLowerInvariant() ?? "read";
            // A historical read is only worth the async dance when the caller is willing to
            // poll; the estimate is a cadence hint, so default to the order of magnitude
            // these reads actually take rather than the mutation default.
            int estimate = tArgs["estimated_seconds"]?.ToObject<int?>() ?? 60;
            var readJob = JobRegistry.Start(sessionId, $"versioning/{action}", estimate, GetCurrentOwnership(sessionId));
            readJob.WorkerAlias = _currentKb.Value?.NormalizedAlias;
            readJob.Target = tArgs["name"]?.ToString();
            readJob.Part = tArgs["part"]?.ToString() ?? tArgs["partName"]?.ToString();
            // Bind the job id as the worker's cancel token so genexus_lifecycle action=cancel
            // can stop a read that is still holding the STA. The worker reads it from the top
            // level of the command envelope (BuildWorkerRpcRequest nests the whole command
            // under params; CommandDispatcher registers args.cancelToken for every command).
            workerCmd["cancelToken"] = readJob.Id;
            Log($"[AsyncRead] Dispatching job={readJob.Id} action={action} estimated={estimate}s");

            var capturedCmd = workerCmd;
            var capturedName = tName;
            _ = Task.Run(async () =>
            {
                try
                {
                    // timeoutMs=0: a slow historical read must not be cut off by the
                    // Gateway wait budget, because the whole point of this path is that the
                    // result outlives the caller's window. Reads are non-preemptible on the
                    // worker's STA exactly like a write, so the recovery handle is cancel,
                    // not a shorter wait.
                    var inner = await SendWorkerCommandAsync(
                        capturedCmd, 0,
                        $"Timeout waiting for async read: {capturedName}",
                        r => r,
                        (_, __) => new JObject { ["status"] = "Running" },
                        toolName: capturedName,
                        toolArgs: tArgs,
                        operationIdentity: readJob.Id).ConfigureAwait(false);
                    bool ok = IsSuccessfulBackgroundToolCompletion(inner);
                    JobRegistry.Complete(
                        readJob.Id,
                        ok,
                        (ok ? "Read succeeded: " : "Read failed: ") + action,
                        inner);
                }
                catch (Exception ex)
                {
                    JobRegistry.Complete(readJob.Id, false, $"Read exception: {ex.Message}");
                    Log($"[AsyncRead] Exception in job={readJob.Id}: {ex.Message}");
                }
            });

            var asyncReadResponse = BuildAsyncReadAcceptedPayload(readJob, action);
            if (McpTasksProtocol.SupportsTasks(request))
            {
                return McpTasksProtocol.BuildCreateTaskResult(
                    readJob,
                    asyncReadResponse["hint"]?.ToString() ?? "Read accepted; poll tasks/get for completion.");
            }
            return BuildToolResultContent(asyncReadResponse, false, tName, tArgs);
        }
    }
}
