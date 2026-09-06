using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static JToken TruncateResponseIfNeeded(JToken? result, string toolName)
        {
            if (result == null) return JValue.CreateNull();
            
            string? readPart = (result as JObject)?["part"]?.ToString();
            bool isXmlMetadataRead = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                                     (string.Equals(readPart, "Layout", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternVirtual", StringComparison.OrdinalIgnoreCase));

            // PERFORMANCE (G-T1): fast structural pre-check to avoid full tree serialization
            // on obviously small responses (whoami, status, inspect, short reads, mutations).
            if (result is JObject quickObj)
            {
                bool mayExceedBudget = false;
                foreach (var prop in quickObj.Properties())
                {
                    if (prop.Value is JValue jv && jv.Value is string s && s.Length > (isXmlMetadataRead ? 100000 : 15000))
                    {
                        mayExceedBudget = true;
                        break;
                    }
                    if (prop.Value is JArray jarr && jarr.Count > 10)
                    {
                        mayExceedBudget = true;
                        break;
                    }
                }
                if (!mayExceedBudget)
                {
                    return result;
                }
            }

            string raw = result.ToString(Formatting.None);
            // issue #25 #6: the worker already paginates genexus_read to ~200 lines /
            // 16 KB and reports it via `isTruncatedByWorker` + offset/limit/
            // suggestedNextOffset. When the worker already bounded the page, the
            // gateway must NOT char-slice `source` again — that re-cut dropped the
            // middle of an already-bounded page and orphaned the pagination fields.
            bool workerPaginatedRead =
                string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                ((result as JObject)?["isTruncatedByWorker"]?.ToObject<bool>() ?? false);
            int softBudget = isXmlMetadataRead
                ? 220000
                : string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                ? 24000
                : string.Equals(toolName, "genexus_asset", StringComparison.OrdinalIgnoreCase)
                    ? 400000
                    : 60000;
            if (raw.Length < softBudget) return result;

            Log($"[Budget] Truncating response for {toolName} ({raw.Length} chars)");

            if (result is JObject obj)
            {
                if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var metadataField in new[] { "variables", "calls", "dataSchema", "patternMetadata" })
                    {
                        if (obj[metadataField] != null)
                        {
                            obj.Remove(metadataField);
                            obj["isTruncated"] = true;
                            obj["message"] = "Gateway trimmed derived metadata from genexus_read to keep the response within the MCP context budget.";
                        }
                    }
                }

                if (obj["results"] is JArray searchResults && searchResults.Count > 10)
                {
                    int originalCount = searchResults.Count;
                    string currentRaw = obj.ToString(Formatting.None);
                    if (currentRaw.Length > 80000)
                    {
                        // Drastic pruning: keep only first 5
                        while (searchResults.Count > 5) searchResults.RemoveAt(searchResults.Count - 1);
                        obj["isTruncated"] = true;
                        obj["returnedCount"] = 5;
                        obj["originalCount"] = originalCount;
                        return obj;
                    }
                }

                bool isRead = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase);

                // issue #26 P7: for genexus_read source/content, the gateway used to
                // head+tail slice and DROP THE MIDDLE — leaving a silent hole and an
                // offset that no longer described the returned bytes. Replace that with a
                // single, predictable, LINE-ALIGNED PREFIX cut that shares the worker's
                // line-based pagination model: keep whole lines from the front, tell the
                // caller exactly which limit hit and the safe line offset to continue from.
                // No middle is ever dropped.
                if (isRead && !isXmlMetadataRead)
                {
                    // Issue #27 item 7: when the caller explicitly asked for the whole part
                    // (limit=0 → worker sets explicitFullRead), honour it with a much larger
                    // budget so "read in full" is truthful. Still a line-aligned prefix cut
                    // (never a middle drop) with a safe continuation offset if the part is
                    // genuinely enormous, so the contract stays predictable either way.
                    bool explicitFullRead = (obj["explicitFullRead"]?.ToObject<bool?>() ?? false);
                    int readFieldBudget = explicitFullRead ? 200000 : 20000;
                    foreach (var field in new[] { "source", "content", "code" })
                    {
                        // Worker already paginated this page — its offset/suggestedNextOffset
                        // are authoritative; don't second-guess with a gateway cut.
                        if (workerPaginatedRead && string.Equals(field, "source", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (obj[field]?.Type != JTokenType.String) continue;
                        string val = obj[field]!.ToString();
                        if (val.Length <= readFieldBudget) continue;

                        // Cut on a line boundary at/under the budget so we never split a line.
                        int cut = val.LastIndexOf('\n', Math.Min(readFieldBudget, val.Length) - 1);
                        if (cut <= 0) cut = Math.Min(readFieldBudget, val.Length); // no newline: hard prefix
                        string kept = val.Substring(0, cut);
                        int keptLines = kept.Length == 0 ? 0 : kept.Split('\n').Length;
                        int baseOffset = obj["offset"]?.ToObject<int?>() ?? 0;
                        int safeNextOffset = baseOffset + keptLines;

                        obj[field] = kept;
                        obj["isTruncated"] = true;
                        obj["truncatedByGateway"] = true;
                        obj["truncatedBy"] = "gateway";
                        obj["gatewaySafeNextOffset"] = safeNextOffset;
                        obj["gatewayTruncationHint"] =
                            $"Gateway trimmed '{field}' to the context budget by keeping whole lines from the front (NO middle dropped). " +
                            $"Continue cleanly with genexus_read offset={safeNextOffset} (line-based) to read the next page.";
                    }
                }

                // Non-read tools (and read metadata fields): head+tail trim is fine here —
                // these are derived blobs, not paginable source, so a middle elision just
                // fits the budget without a pagination contract to break.
                var fieldsToTruncate = (isRead && !isXmlMetadataRead)
                    ? new[] { "fileContent", "details" }
                    : new[] { "source", "content", "code", "fileContent", "details" };
                foreach (var field in fieldsToTruncate)
                {
                    var fieldValue = obj[field];
                    if (fieldValue != null && fieldValue.Type == JTokenType.String)
                    {
                        string val = fieldValue.ToString();
                        int fieldBudget = isXmlMetadataRead ? 180000 : 20000;
                        int headBudget = isXmlMetadataRead ? 140000 : 15000;
                        int tailBudget = isXmlMetadataRead ? 40000 : 5000;
                        if (val.Length > fieldBudget)
                        {
                            obj[field] = val.Substring(0, headBudget) +
                                           "\n\n[... TRUNCATED BY GATEWAY TOKEN BUDGET ...] \n\n" +
                                           val.Substring(val.Length - tailBudget);
                            obj["isTruncated"] = true;
                            obj["truncatedByGateway"] = true;
                            obj["gatewayTruncationHint"] = "Gateway trimmed this field to fit the context budget (a middle slice was dropped). This field is not paginable; re-request the specific object/part if you need the full bytes.";
                        }
                    }
                }

                string truncatedRaw = obj.ToString(Formatting.None);
                if (truncatedRaw.Length > 80000)
                {
                    // issue #25 #6: for genexus_read, preserve the head+tail-trimmed
                    // object instead of wiping it to a bare error (the old fallback
                    // discarded the tail it had just carefully kept). Only non-read
                    // shapes fall back to the structural error.
                    if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                    {
                        obj["isTruncated"] = true;
                        obj["truncatedByGateway"] = true;
                        obj["message"] = "Response exceeded the gateway budget even after trimming; re-request with a smaller `limit` or use offset/limit pagination for exact bytes.";
                        return obj;
                    }
                    // Fallback to ensuring valid JSON structure when heavily nested Strings overfill
                    return JToken.FromObject(new {
                        jsonrpc = "2.0",
                        error = "Response exceeded 80k token budget and could not be safely parsed. Try lower limits or pagination.",
                        isTruncated = true
                    });
                }
                return obj;
            }
            else if (result is JArray arr)
            {
                // Truncate arrays if they exceed limits.
                // PERF: this used to serialize the entire array inside the while condition
                // after every single removal (O(n²) on large lists). Measure once, drop an
                // estimated block of items (avg serialized bytes per item, small safety
                // margin), and re-measure only after each block — not per item.
                int totalLen = arr.ToString(Formatting.None).Length;
                while (arr.Count > 5 && totalLen > 80000)
                {
                    long avgItemLen = Math.Max(1L, totalLen / arr.Count);
                    long estimatedRemove = (long)Math.Ceiling((totalLen - 80000) / (double)avgItemLen * 1.05);
                    int block = (int)Math.Min(arr.Count - 5L, Math.Max(1L, estimatedRemove));
                    for (int i = 0; i < block; i++)
                        arr.RemoveAt(arr.Count - 1);
                    totalLen = arr.ToString(Formatting.None).Length;
                }
                if (totalLen > 80000)
                {
                    return JToken.FromObject(new { 
                        error = "Array response exceeded 80k token budget. Try lower limits or pagination.", 
                        isTruncated = true 
                    });
                }
                return arr;
            }

            return new JValue(raw.Substring(0, 75000) + "... [TRUNCATED]");
        }

        /// <summary>
        /// Extracts the object name a mutating call targets, when the whole mutation is
        /// scoped to that one object. Used for granular semantic-cache invalidation:
        /// only cached reads referencing this target are dropped instead of the entire
        /// store. Returns null (→ full Clear) for KB-wide mutations or unknown arg
        /// shapes. Conservative: prefer returning null over a wrong name.
        /// </summary>
        internal static string? ExtractMutationTarget(string toolName, JObject? args)
        {
            if (args == null) return null;

            // KB-wide / multi-object mutations must never be scoped to one target.
            if (string.Equals(toolName, "genexus_rename_across_kb", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_kb_import", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
                return null;

            // Common single-target shapes across the tool surface.
            string? name = args["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            string? target = args["target"]?.ToString();
            if (!string.IsNullOrWhiteSpace(target)) return target;

            // genexus_read-style arrays: only when exactly one target is present —
            // a write against [A,B] invalidates both, so fall back to full clear.
            if (args["targets"] is JArray arr && arr.Count == 1)
            {
                string? single = arr[0]?.ToString();
                if (!string.IsNullOrWhiteSpace(single)) return single;
            }

            return null;
        }

        /// <summary>
        /// Enumerates every object named by a mutating payload. Recovery fences
        /// must cover multi-target edits and explicit change sets as well as the
        /// legacy single-name shape; returning duplicates is intentionally
        /// avoided so one fence produces one deterministic block.
        /// </summary>
        internal static IEnumerable<string> EnumerateMutationTargets(string toolName, JObject? args)
        {
            if (args == null) yield break;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) seen.Add(value.Trim());
            }

            Add(args["name"]?.ToString());
            Add(args["target"]?.ToString());

            if (args["targets"] is JArray targets)
            {
                foreach (var token in targets)
                {
                    if (token is JObject item)
                    {
                        Add(item["name"]?.ToString());
                        Add(item["target"]?.ToString());
                    }
                    else Add(token?.ToString());
                }
            }

            if (args["changeSet"] is JObject changeSet)
            {
                var changes = changeSet["changes"] as JArray ?? changeSet["targets"] as JArray;
                foreach (var token in changes?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    Add(token["name"]?.ToString());
                    Add(token["target"]?.ToString());
                }
            }

            foreach (string target in seen) yield return target;
        }

        // Semantic-cache invalidation gate: returns true when a tool call may
        // change KB object state, so DispatchCore can clear _semanticCache before
        // (and only before) a mutation. A MISS here means the next identical read
        // replays a stale envelope — the read-after-delete staleness bug (a
        // genexus_delete_object was not recognised as mutating, so a cached
        // part=Structure read survived the delete). The verb-substring heuristic
        // covers names like edit/create/refactor; umbrella tools and
        // action-dependent tools need the explicit cases below.
        internal static void MarkRecordWriteOutcomeUnknown(JObject payload)
        {
            payload["retriable"] = false;
            payload["retrySafe"] = false;
            payload["persisted"] = JValue.CreateNull();
            payload["commitState"] = "Indeterminate";
            payload["rereadConfirmed"] = false;
        }

        internal static bool IsTransactionRecordOperation(string toolName, JObject? args)
        {
            if (!string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase)) return false;
            string? action = args?["action"]?.ToString();
            return string.Equals(action, "records_query", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "records_insert", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "records_update", StringComparison.OrdinalIgnoreCase);
        }

        // Record reads and previews are live database observations. Neither an empty
        // query nor an earlier successful mutation may bypass a fresh worker call.
        // The action classifier is also the cache safety boundary: action-dependent
        // file/browser side effects must not be cached merely because the legacy
        // invalidation list does not know about them yet.
        internal static string? CreateSemanticCacheKey(string kbScope, string toolName,
            JObject? args, bool isMutating, bool isLiveTool)
        {
            if (isMutating || isLiveTool
                || OperationClassifier.Describe(toolName, args).Kind != OperationClassifier.OperationKind.ReadOnly
                || IsTransactionRecordOperation(toolName, args))
                return null;
            return $"{kbScope}|{toolName}:{args?.ToString(Newtonsoft.Json.Formatting.None)}";
        }

        /// <summary>
        /// Builds a deterministic semantic-cache key. The legacy overload above
        /// preserves the pre-v3 shape for callers that only need the classifier;
        /// the dispatch path supplies a KB generation and optional model/environment
        /// identity so equivalent JSON with different property order shares a key.
        /// </summary>
        internal static string? CreateSemanticCacheKey(string kbScope, string toolName,
            JObject? args, bool isMutating, bool isLiveTool, long cacheRevision,
            string? modelScope, string? environmentScope)
        {
            if (isMutating || isLiveTool
                || OperationClassifier.Describe(toolName, args).Kind != OperationClassifier.OperationKind.ReadOnly
                || IsTransactionRecordOperation(toolName, args))
                return null;

            var canonicalArgs = CanonicalizeJson(args ?? new JObject());
            string normalizedKb = (kbScope ?? string.Empty).Trim().ToLowerInvariant();
            string normalizedTool = (toolName ?? string.Empty).Trim().ToLowerInvariant();
            string model = CanonicalizeScopePart(modelScope);
            string environment = CanonicalizeScopePart(environmentScope);

            return $"{normalizedKb}|{normalizedTool}:{canonicalArgs.ToString(Newtonsoft.Json.Formatting.None)}"
                + $"|rev={cacheRevision}|model={model}|env={environment}";
        }

        /// <summary>Sorts object properties recursively while preserving array order.</summary>
        internal static JToken CanonicalizeJson(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted.Add(property.Name, CanonicalizeJson(property.Value));
                return sorted;
            }

            if (token is JArray array)
            {
                var sorted = new JArray();
                foreach (var item in array)
                    sorted.Add(CanonicalizeJson(item));
                return sorted;
            }

            return token.DeepClone();
        }

        private static string CanonicalizeScopePart(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

        internal static bool IsMutatingTool(string toolName, JObject? args)
        {
            return !string.IsNullOrWhiteSpace(toolName)
                && OperationClassifier.IsMutationCandidate(toolName, args);
        }

        // Items 54/55/56: resolve a "KB ref" argument that may be either an alias
        // declared in config.Environment.KBs[] or a literal filesystem path.
        // Returns the resolved absolute path, or null if neither match.
        private static string? ResolveKbPath(string aliasOrPath)
        {
            if (string.IsNullOrWhiteSpace(aliasOrPath)) return null;
            var declared = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, aliasOrPath, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return declared.Path;
            if (System.IO.Directory.Exists(aliasOrPath)) return aliasOrPath;
            return null;
        }

        /// <summary>
        /// Add <c>_meta.autoInjected: ["type"]</c> to the content text payload of a
        /// tool result envelope so the LLM sees that gateway inferred the type.
        /// Does not overwrite any existing <c>_meta</c> structure — merges only.
        /// </summary>
        private static void InjectAutoTypeAnnotation(JObject toolInnerResult, string injectedType)
        {
            try
            {
                var contentArr = toolInnerResult["content"] as JArray;
                if (contentArr == null || contentArr.Count == 0) return;
                var firstContent = contentArr[0] as JObject;
                if (firstContent == null) return;

                string? rawText = firstContent["text"]?.ToString();
                if (rawText == null) return;

                JObject payload;
                try { payload = JObject.Parse(rawText); }
                catch { return; }  // non-JSON text blob — skip

                // Merge into existing _meta or create new
                if (payload["_meta"] is not JObject meta)
                {
                    meta = new JObject();
                    payload["_meta"] = meta;
                }
                meta["autoInjected"] = new JArray("type");
                meta["autoInjectedType"] = injectedType;

                firstContent["text"] = payload.ToString(Formatting.None);
            }
            catch
            {
                // Best-effort — never fail a tool call over annotation
            }
        }

        // PERF: primary-collection keys probed by NormalizeToolPayloadForAxi on every
        // response. Was a per-call allocated string[] before the perf-review pass.
        private static readonly string[] CollectionKeys =
        {
            "results", "objects", "items", "tools", "checks", "entries", "nodes", "controls",
            // Additional primary-collection keys used by non-search tools:
            "endpoints", "history", "snapshots", "versions", "modules",
            "pending", "ignored", "conflicts", "targets", "pipelines"
        };

        // PERF: compact-projection field sets are immutable and reused across every
        // query/list response — no per-call HashSet allocation.
        private static readonly HashSet<string> CompactFieldsQuery =
            new HashSet<string>(new[] { "name", "type", "path", "lastUpdate" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CompactFieldsListObjects =
            new HashSet<string>(new[] { "name", "type", "path", "parentPath", "lastUpdate", "guid", "entityKey", "entityTypeGuid", "entityId" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CompactFieldsSearch =
            new HashSet<string>(new[] { "name", "type", "description", "path", "lastUpdate" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> MinimalProjectionFields =
            new HashSet<string>(new[] { "name", "type", "lastUpdate" }, StringComparer.OrdinalIgnoreCase);

        internal static JObject BuildToolTextResponse(JToken? idToken, JToken payload, bool isError, string? toolName = null, JObject? toolArgs = null, bool payloadOwned = false)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idToken?.DeepClone(),
                ["result"] = BuildToolResultContent(payload, isError, toolName, toolArgs, payloadOwned)
            };
        }

        internal static JObject BuildToolResultContent(
            JToken payload,
            bool isError,
            string? toolName = null,
            JObject? toolArgs = null,
            bool payloadOwned = false)
        {
            JToken axiPayload = NormalizeToolPayloadForAxi(payload, toolName ?? "unknown", toolArgs, isError);
            string? kbAlias = _currentKb.Value?.Alias;
            if (!string.IsNullOrWhiteSpace(kbAlias))
            {
                // Worker responses are detached immediately before this method is called,
                // so the gateway can add the correlation metadata in place. Gateway-created
                // or shared payloads keep the defensive clone contract of the public helper.
                axiPayload = payloadOwned
                    ? AttachKbContextMetadataToOwnedPayload(axiPayload, kbAlias!)
                    : AddKbContextMetadata(axiPayload, kbAlias!);
            }

            var result = new JObject
            {
                ["resultType"] = "complete",
                ["isError"] = isError,
                ["content"] = new JArray { new JObject
                {
                    ["type"] = "text",
                    ["text"] = axiPayload.ToString(Formatting.None)
                } }
            };
            // Legacy stdio clients (including OpenCode's text-oriented MCP path)
            // need the resolved KB in-band to correlate a response. Modern clients
            // can use the protocol metadata without reparsing the text.
            if (!string.IsNullOrWhiteSpace(kbAlias))
            {
                result["_meta"] = new JObject { ["kbAlias"] = kbAlias };
            }
            // MCP's structuredContent lets modern clients consume the JSON result
            // without reparsing the text content. Keep the text representation for
            // legacy clients and omit structuredContent on tool errors.
            // Perf: structuredContent duplicates the whole payload (~+55% bytes per
            // response, measured). Gated by Server.EmitStructuredContent / env
            // GXMCP_NO_STRUCTURED_CONTENT so lean deployments can drop it.
            if (!isError && EmitStructuredContentEnabledCached()
                && (axiPayload.Type == JTokenType.Object || axiPayload.Type == JTokenType.Array))
            {
                result["structuredContent"] = axiPayload;
            }
            return result;
        }

        // Resolved per call (cheap: one env lookup + one static field read) so a config
        // reload or env change takes effect without restart. Env wins over config file.
        internal static bool EmitStructuredContentEnabled()
        {
            string? noEnv = Environment.GetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT");
            if (string.Equals(noEnv, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(noEnv, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            string? emitEnv = Environment.GetEnvironmentVariable("GXMCP_EMIT_STRUCTURED_CONTENT");
            if (!string.IsNullOrWhiteSpace(emitEnv))
            {
                return string.Equals(emitEnv, "1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(emitEnv, "true", StringComparison.OrdinalIgnoreCase);
            }

            return ActiveConfig?.Server?.EmitStructuredContent ?? true;
        }

        // Resolved per call like EmitStructuredContentEnabled. Env wins.
        internal static bool TerseResponsesEnabled()
        {
            string? env = Environment.GetEnvironmentVariable("GXMCP_TERSE");
            if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
                return true;

            return ActiveConfig?.Server?.TerseResponses ?? false;
        }

        // PERFORMANCE (perf-review round 3): short-TTL cache for the env-var probes the
        // response path reads on every tool call (TerseResponsesEnabled runs 2-3x per
        // request; GXMCP_LEGACY_TOOL_ALIASES once more). Environment.GetEnvironmentVariable
        // is a Win32 call each time — a 5s TTL keeps config-change responsiveness while
        // removing the per-request syscall cost. Env change still lands within one TTL;
        // tests that flip these variables use SetEnvVarForTests below to bypass the cache.
        private static readonly object _envGate = new();
        private static DateTime _envCacheAt = DateTime.MinValue;
        private static bool _terseCached;
        private static bool _structuredContentCached;
        private static bool _legacyAliasesDisabled;

        internal static TimeSpan EnvProbeTtl { get; set; } = TimeSpan.FromSeconds(5);

        private static void RefreshEnvProbeCache()
        {
            var now = DateTime.UtcNow;
            lock (_envGate)
            {
                if (now - _envCacheAt < EnvProbeTtl) return;
                _terseCached = ResolveTerseUncached();
                _structuredContentCached = ResolveStructuredContentUncached();
                _legacyAliasesDisabled = string.Equals(Environment.GetEnvironmentVariable("GXMCP_LEGACY_TOOL_ALIASES"), "0", StringComparison.Ordinal);
                _envCacheAt = now;
            }
        }

        private static bool ResolveTerseUncached()
        {
            string? env = Environment.GetEnvironmentVariable("GXMCP_TERSE");
            if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
                return true;
            return ActiveConfig?.Server?.TerseResponses ?? false;
        }

        private static bool ResolveStructuredContentUncached()
        {
            string? env = Environment.GetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT");
            if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
                return false;
            return ActiveConfig?.Server?.EmitStructuredContent ?? true;
        }

        internal static bool TerseResponsesEnabledCached()
        {
            RefreshEnvProbeCache();
            lock (_envGate) return _terseCached;
        }

        internal static bool EmitStructuredContentEnabledCached()
        {
            RefreshEnvProbeCache();
            lock (_envGate) return _structuredContentCached;
        }

        // Test hook: drop the probe cache so an env/config change is observed immediately.

        // PERF round 3: GXMCP_LEGACY_TOOL_ALIASES probe (per tool call) behind the same
        // short-TTL cache as the terse/structured-content flags. Default: aliases ON.
        internal static bool LegacyToolAliasesDisabledCached()
        {
            RefreshEnvProbeCache();
            lock (_envGate) return _legacyAliasesDisabled;
        }

        internal static void InvalidateEnvProbeCache()
        {
            lock (_envGate) _envCacheAt = DateTime.MinValue;
        }

        internal static JToken AttachKbContextMetadataToOwnedPayload(JToken payload, string kbAlias)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (string.IsNullOrWhiteSpace(kbAlias)) throw new ArgumentException("KB alias is required.", nameof(kbAlias));

            string alias = kbAlias.Trim();
            if (payload is JObject obj)
            {
                // A parent means another response tree still owns this token. Keep the
                // defensive behavior instead of mutating that tree unexpectedly.
                if (obj.Parent != null) return AddKbContextMetadata(obj, alias);
                obj["kbAlias"] = alias;
                return obj;
            }

            if (payload is JArray array)
            {
                return new JObject
                {
                    ["results"] = array.Parent == null ? array : array.DeepClone(),
                    ["kbAlias"] = alias
                };
            }

            return new JObject
            {
                ["value"] = payload.Parent == null ? payload : payload.DeepClone(),
                ["kbAlias"] = alias
            };
        }

        private static void DetachResponsePayload(JObject response, JToken? payload)
        {
            if (payload == null) return;
            if (ReferenceEquals(response["result"], payload))
            {
                response.Remove("result");
            }
            else if (ReferenceEquals(response["error"], payload))
            {
                response.Remove("error");
            }
        }

        internal static JToken AddKbContextMetadata(JToken payload, string kbAlias)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (string.IsNullOrWhiteSpace(kbAlias)) throw new ArgumentException("KB alias is required.", nameof(kbAlias));

            if (payload is JObject obj)
            {
                var clone = (JObject)obj.DeepClone();
                clone["kbAlias"] = kbAlias.Trim();
                return clone;
            }

            if (payload is JArray array)
            {
                return new JObject
                {
                    ["results"] = array.DeepClone(),
                    ["kbAlias"] = kbAlias.Trim()
                };
            }

            return new JObject
            {
                ["value"] = payload.DeepClone(),
                ["kbAlias"] = kbAlias.Trim()
            };
        }

        private static JToken NormalizeToolPayloadForAxi(JToken? payload, string toolName, JObject? toolArgs, bool isError)
        {
            HashSet<string>? requestedFields = ParseRequestedFields(toolArgs);
            // Friction 2026-05-22 #64: projection=minimal|standard|verbose lets the
            // agent opt into a smaller or larger field set without having to enumerate
            // fields[]. Resolves to a HashSet that overrides the axiCompact default —
            // explicit fields[] still wins (highest specificity).
            string? projection = toolArgs?["projection"]?.ToString();
            bool verboseRequested = !string.IsNullOrWhiteSpace(projection)
                && string.Equals(projection.Trim(), "verbose", StringComparison.OrdinalIgnoreCase);
            if (requestedFields == null && !string.IsNullOrWhiteSpace(projection))
            {
                requestedFields = ResolveProjection(toolName, projection);
            }
            // projection=verbose explicitly opts OUT of the compact filter — earlier
            // versions silently fell into GetDefaultCompactFields here because
            // ResolveProjection returns null for both 'verbose' and unknown levels.
            if (requestedFields == null && !verboseRequested && ShouldUseCompactDefaults(toolArgs))
            {
                requestedFields = GetDefaultCompactFields(toolName);
            }

            bool shouldProject = requestedFields != null && requestedFields.Count > 0 && ShouldProjectFieldsForTool(toolName);

            JObject obj;
            string? matchedKey = null;

            if (payload is JArray arrayPayload)
            {
                matchedKey = "results";
                obj = new JObject
                {
                    ["results"] = shouldProject ? ProjectArrayItems(arrayPayload, requestedFields!) : arrayPayload.DeepClone()
                };
            }
            else if (payload is JObject objPayload)
            {
                matchedKey = CollectionKeys.FirstOrDefault(k => objPayload[k] is JArray);
                if (shouldProject && matchedKey != null)
                {
                    obj = new JObject();
                    foreach (var prop in objPayload.Properties())
                    {
                        if (string.Equals(prop.Name, matchedKey, StringComparison.Ordinal))
                        {
                            obj[prop.Name] = ProjectArrayItems((JArray)prop.Value, requestedFields!);
                        }
                        else
                        {
                            obj[prop.Name] = prop.Value.DeepClone();
                        }
                    }
                }
                else
                {
                    // PERFORMANCE (perf-review): mutate the payload in place instead of
                    // DeepCloning it. The tree is exclusively owned by this response path
                    // at this point: TruncateResponseIfNeeded already mutates it upstream,
                    // OperationTracker.DeepClone()s its telemetry snapshot, the semantic
                    // cache hands out clones on hit, and IdempotencyMiddleware clones
                    // before storing. The old DeepClone copied the full tree of EVERY
                    // response — the single largest per-response allocation for
                    // genexus_read / genexus_whoami / edit results.
                    obj = objPayload;
                }
            }
            else
            {
                return payload ?? JValue.CreateNull();
            }

            // Per-response meta is intentionally lean: `schemaVersion` is emitted
            // once in the `initialize` handshake (`_meta.schemaVersion`) and the
            // client already knows which tool it called, so neither field is
            // repeated per response (~60B/response saved). Only emit `meta` when
            // a real signal (truncated/fields/totalByType/…) gets attached below.
            var meta = obj["meta"] as JObject ?? new JObject();

            if (obj["isTruncated"]?.Value<bool>() == true)
            {
                meta["truncated"] = true;
                var help = obj["help"] as JArray ?? new JArray();
                string truncateHint = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                    ? "Response truncated by gateway budget. Use limit/offset to page source content."
                    : "Response truncated by gateway budget. Narrow filters or lower limit for deterministic follow-up.";

                if (!help.Any(item => string.Equals(item?.ToString(), truncateHint, StringComparison.OrdinalIgnoreCase)))
                {
                    help.Add(truncateHint);
                }

                obj["help"] = help;
            }

            if (!isError &&
                string.Equals(obj["status"]?.ToString(), "ok", StringComparison.Ordinal) &&
                obj["noChange"] == null &&
                (string.Equals(obj["code"]?.ToString(), "NoChange", StringComparison.Ordinal)
                 || string.Equals(obj["result"]?["noChangeReason"]?.ToString(), "literal_identical", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(obj["details"]?.ToString(), "No change", StringComparison.OrdinalIgnoreCase)))
            {
                obj["noChange"] = true;
            }

            // Where the collection lives: top-level first (search tools), then inside the
            // canonical `result` object (McpResponse.Ok producers). Deterministic lookup —
            // never auto-detect "the sole array property" (would wrongly pick up per-row
            // sub-arrays like `endpoints[i].parms`).
            JObject collectionHost = obj;
            if (matchedKey == null && obj["result"] is JObject resultObj)
            {
                matchedKey = CollectionKeys.FirstOrDefault(k => resultObj[k] is JArray);
                if (matchedKey != null)
                {
                    collectionHost = resultObj;
                }
            }

            if (matchedKey != null)
            {
                var arr = (JArray)collectionHost[matchedKey]!;

                if (collectionHost == obj && shouldProject)
                {
                    meta["fields"] = new JArray(requestedFields!.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));
                }

                if (meta["totalByType"] == null)
                {
                    var totalsByType = BuildTotalsByType(arr);
                    if (totalsByType.Properties().Any())
                    {
                        meta["totalByType"] = totalsByType;
                    }
                }

                int returned = arr.Count;
                if (obj["returned"] == null) obj["returned"] = returned;
                if (obj["empty"] == null) obj["empty"] = returned == 0;
                if ((obj["empty"]?.Value<bool>() ?? false))
                {
                    EnsureEmptyStateHelp(obj, toolName);
                }

                int? total = TryReadInt(obj["total"]) ??
                             TryReadInt(obj["count"]) ??
                             TryReadInt(obj["totalCount"]);
                if (total.HasValue && obj["total"] == null)
                {
                    obj["total"] = total.Value;
                }

                int? limit = TryReadInt(toolArgs?["limit"]);
                int offset = TryReadInt(toolArgs?["offset"]) ?? 0;
                int? effectiveTotal = TryReadInt(obj["total"]);

                if (limit.HasValue && effectiveTotal.HasValue)
                {
                    bool hasMore = (offset + returned) < effectiveTotal.Value;
                    if (obj["hasMore"] == null) obj["hasMore"] = hasMore;
                    if (hasMore && obj["nextOffset"] == null)
                    {
                        obj["nextOffset"] = offset + returned;
                    }
                }
            }

            // Only emit a `meta` block when at least one signal was attached;
            // an empty `{}` is pure overhead for the 90% of responses that have
            // no truncation/projection/totals to surface.
            if (meta.Properties().Any())
            {
                obj["meta"] = meta;
            }
            else
            {
                obj.Remove("meta");
            }

            // SQL-dialect nudge for DB tools. The LLM already sees the dialect in
            // whoami.database.default.dialect, but planting it on the response of the
            // tool that actually returns SQL is the second-nudge that lets the agent
            // align dialect at point-of-use without re-reading whoami.
            try
            {
                if (IsSqlGeneratingTool(toolName, toolArgs) && obj["dialect"] == null)
                {
                    var info = GetCachedDatabaseInfo();
                    var defaultStore = info?["default"] as JObject;
                    string? dialect = defaultStore?["dialect"]?.ToString();
                    string? type = defaultStore?["type"]?.ToString();
                    if (!string.IsNullOrEmpty(dialect) && !string.Equals(dialect, "unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        obj["dialect"] = dialect;
                        if (!string.IsNullOrEmpty(type)) obj["dialectType"] = type;
                    }
                }
            }
            catch { /* best-effort UX sugar */ }

            // next_legal_actions injection — last step.
            // SOTA LLM-UX: state-changing tool responses carry an additive
            // array of the most-likely useful next tool calls so the LLM
            // doesn't have to guess across turns. Read-only tools and
            // payloads without a natural follow-up return null and the
            // field is simply omitted. Spec-clean: extra top-level field;
            // clients that don't know about it ignore it.
            try
            {
                // Terse mode: skip next_legal_actions injection entirely, and also strip
                // the worker's per-response UX sugar from the inner _meta block
                // (suggested_next / alternative_views / aggregates / enrichmentHint —
                // measured ~420 bytes on list_objects, ~480 on query). The actionable
                // fields (tokens hint stays out via InjectMetaTokens gate; match_quality
                // and empty_reason are kept — they change how the agent reads results).
                if (TerseResponsesEnabledCached())
                {
                    if (obj["_meta"] is JObject innerMeta)
                    {
                        innerMeta.Remove("suggested_next");
                        innerMeta.Remove("alternative_views");
                        innerMeta.Remove("aggregates");
                        innerMeta.Remove("enrichmentHint");
                        innerMeta.Remove("autoInjected");
                        innerMeta.Remove("autoInjectedType");
                        if (!innerMeta.Properties().Any()) obj.Remove("_meta");
                    }
                    // Static one-liner the agent reads once and never needs again
                    // (~160 bytes on every inspect).
                    obj.Remove("sourceReadHint");
                    // Debug correlation GUID — only useful when pasting logs for support.
                    obj.Remove("correlationId");
                    // Name-resolution echoes: name/type are already top-level.
                    obj.Remove("resolvedAs");
                    obj.Remove("alsoMatches");
                    // Prose that restates the structured payload (~110 bytes).
                    obj.Remove("summary");
                    // SDK-internal value, never actionable for the agent.
                    if (obj["wwpMetadata"] is JObject wwp) wwp.Remove("masterPage");
                    // Machine host\\user noise; lastUpdate (actionable) stays.
                    if (obj["lifecycle"] is JObject lc) lc.Remove("lastModifiedBy");
                    return obj;
                }

                if (obj["next_legal_actions"] == null)
                {
                    JArray? actions = NextLegalActionsBuilder.BuildFor(toolName, toolArgs, obj, isError);
                    if (actions != null && actions.Count > 0)
                    {
                        obj["next_legal_actions"] = actions;
                    }
                }
            }
            catch
            {
                // Builder is best-effort UX sugar; never let it break the
                // response envelope.
            }

            return obj;
        }

        private static void EnsureEmptyStateHelp(JObject obj, string toolName)
        {
            var help = obj["help"] as JArray ?? new JArray();
            string hint = string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase)
                ? "No matches found for the current query. Try broader terms or remove filters."
                : string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase)
                    ? "No objects found for the current scope. Verify parentPath/parent filters."
                    : "No results returned for this request.";

            if (!help.Any(item => string.Equals(item?.ToString(), hint, StringComparison.OrdinalIgnoreCase)))
            {
                help.Add(hint);
            }

            obj["help"] = help;
        }

        private static bool ShouldProjectFieldsForTool(string toolName)
        {
            return string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(toolName, "genexus_search", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildTotalsByType(JArray arr)
        {
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in arr)
            {
                if (row is JObject rowObj)
                {
                    if (rowObj.TryGetValue("type", StringComparison.OrdinalIgnoreCase, out var typeTok) &&
                        typeTok is JValue jv && jv.Value is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        totals[s] = totals.TryGetValue(s, out int count) ? count + 1 : 1;
                    }
                }
            }

            var outObj = new JObject();
            foreach (var kv in totals.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                outObj[kv.Key] = kv.Value;
            }

            return outObj;
        }

        private static JArray ProjectArrayItems(JArray arr, HashSet<string> fields)
        {
            var projected = new JArray();
            foreach (var row in arr)
            {
                if (row is not JObject rowObj)
                {
                    projected.Add(row.DeepClone());
                    continue;
                }

                var outRow = new JObject();
                foreach (var prop in rowObj.Properties())
                {
                    if (fields.Contains(prop.Name))
                    {
                        outRow[prop.Name] = prop.Value.DeepClone();
                    }
                }

                projected.Add(outRow);
            }

            return projected;
        }

        // Returns true when compact-by-default projection should be applied for tools that
        // declare a default compact field set in GetDefaultCompactFields. Default behavior
        // (no axiCompact key) is TRUE — the LLM must pass `axiCompact: false` to opt out.
        private static bool ShouldUseCompactDefaults(JObject? toolArgs)
        {
            if (toolArgs == null) return true;
            var token = toolArgs["axiCompact"];
            if (token == null) return true;
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }
            return !bool.TryParse(token.ToString(), out bool parsed) || parsed;
        }

        /// <summary>
        /// Friction 2026-05-22 #64: resolve projection=minimal|standard|verbose to
        /// the field set the gateway should apply. Returns null for unknown levels
        /// or when the tool doesn't support projection (caller falls back to
        /// axiCompact defaults).
        /// </summary>
        ///   - minimal: name + kind/type + lastUpdate (3 fields, smallest legal shape)
        ///   - standard: GetDefaultCompactFields(toolName) — same as today's default
        ///   - verbose: returns null so no projection filter is applied → full payload
        internal static HashSet<string>? ResolveProjection(string toolName, string projection)
        {
            if (string.IsNullOrWhiteSpace(projection)) return null;
            string p = projection.Trim().ToLowerInvariant();
            if (p == "verbose")
            {
                // No filter at all — caller sees every field the worker emitted.
                return null;
            }
            if (p == "minimal")
            {
                // The smallest legal projection. Matches the schema description
                // exactly: {name, type, lastUpdate}. (Prior versions also whitelisted
                // 'kind' defensively but no worker emits it today — keeping the
                // field-set tight so 'minimal' is honest about its contract.)
                // PERF: cached static set — immutable, reused across responses.
                return MinimalProjectionFields;
            }
            if (p == "standard")
            {
                // Fall through to today's default. GetDefaultCompactFields is the
                // single source of truth — keeping projection=standard in lockstep.
                return GetDefaultCompactFields(toolName);
            }
            // Unknown projection level — treat like default (caller will fall back).
            return null;
        }

        private static string BuildIndexingMessage(string? status, double? progress, int? etaMs)
        {
            string s = status ?? "Cold";
            string phase = string.Equals(s, "Reindexing", StringComparison.OrdinalIgnoreCase) ? "Rebuilding index"
                : string.Equals(s, "UltraLiteReady", StringComparison.OrdinalIgnoreCase) ? "Walking KB (ultra-lite pass)"
                : string.Equals(s, "Cold", StringComparison.OrdinalIgnoreCase) ? "Building index from cold start"
                : "Building index";

            var parts = new System.Collections.Generic.List<string> { phase };
            if (progress.HasValue && progress.Value > 0 && progress.Value < 1)
            {
                parts.Add($"{(int)Math.Round(progress.Value * 100)}% complete");
            }
            if (etaMs.HasValue && etaMs.Value > 0)
            {
                int seconds = (int)Math.Ceiling(etaMs.Value / 1000.0);
                parts.Add(seconds <= 1 ? "~1s remaining" : $"~{seconds}s remaining");
            }
            return string.Join(", ", parts) + ".";
        }

        private static bool IsSqlGeneratingTool(string toolName, JObject? toolArgs)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            if (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase))
            {
                string? action = toolArgs?["action"]?.ToString();
                return string.Equals(action, "sql_ddl", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "sql_navigation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_analyze", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_suggest", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_report", StringComparison.OrdinalIgnoreCase);
            }
            // Legacy aliases — keep emitting the nudge for callers using the old names
            // until they drop out of LegacyToolAliases.
            return string.Equals(toolName, "genexus_sql", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_db_optimize", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string>? GetDefaultCompactFields(string toolName)
        {
            // PERF: cached statics (see CompactFields* fields above) — the caller
            // only reads them (Contains/Count/OrderBy), never mutates.
            if (string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase))
            {
                // v2.6.8: lastUpdate is part of the compact projection — same
                // rationale as list_objects (small, answers "what changed").
                return CompactFieldsQuery;
            }

            if (string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase))
            {
                // v2.6.8: keep lastUpdate in the compact projection — it's the
                // signal that powers "what changed?" workflows and is cheap (~30b).
                // createdAt/lastModifiedBy stay verbose-only at the worker.
                return CompactFieldsListObjects;
            }

            if (string.Equals(toolName, "genexus_search", StringComparison.OrdinalIgnoreCase))
            {
                // genexus_search returns 50-item result pages with per-item type
                // metadata (guid/length/decimals) that a scanning agent rarely needs.
                // Same allowlist as query — name/type/description/path identify the hit;
                // lastUpdate powers recency workflows. Explicit fields[] still wins.
                return CompactFieldsSearch;
            }

            return null;
        }

        private static HashSet<string>? ParseRequestedFields(JObject? toolArgs)
        {
            if (toolArgs == null) return null;
            var token = toolArgs["fields"];
            if (token == null) return null;

            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Values<string>())
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        fields.Add(item.Trim());
                    }
                }
            }
            else
            {
                string raw = token.ToString();
                foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = piece.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        fields.Add(value);
                    }
                }
            }

            return fields.Count == 0 ? null : fields;
        }

        private static int? TryReadInt(JToken? token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            if (token.Type == JTokenType.Float) return (int)Math.Floor(token.Value<double>());
            if (token.Type == JTokenType.String &&
                int.TryParse(token.Value<string>(), out int parsed))
            {
                return parsed;
            }

            return null;
        }

    }
}
