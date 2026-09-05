using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class AnalyzeService : IAnalyzeServiceFacade
    {
        // v2.6.9 perf: inspect (GetConversionContext) cache. The SDK reads for
        // signature/variables/structure/parts/controls/events/callers are
        // serialised on the STA thread and the first call per target costs
        // ~700 ms p99. Subsequent calls within TTL skip the SDK round-trips
        // when no write has landed against the target. Invalidation watches
        // WriteService._lastWriteAtUtc — any in-process write to the target
        // post-cache flips the entry stale and forces a fresh read.
        //
        // Same shape reused for the ImpactAnalysis cache below; both survive
        // a gateway semantic-cache wipe (which clears on every mutating
        // call regardless of target) so an alternating read/write/read flow
        // against different targets keeps the read side warm.
        private sealed class InspectCacheEntry
        {
            public string Json;
            public System.DateTime FilledAtUtc;
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InspectCacheEntry> _inspectCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, InspectCacheEntry>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly System.TimeSpan InspectCacheTtl = System.TimeSpan.FromSeconds(30);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InspectCacheEntry> _impactCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, InspectCacheEntry>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly System.TimeSpan ImpactCacheTtl = System.TimeSpan.FromSeconds(30);

        // These caches only evict a stale entry when its key is re-read, so keys inspected once
        // and never revisited would accumulate (one JSON-holding entry per distinct object over
        // a long session). Sweep expired entries opportunistically on write, once the cache has
        // grown past a small floor — keeps it to roughly "entries touched within the TTL".
        private static void SweepExpired(
            System.Collections.Concurrent.ConcurrentDictionary<string, InspectCacheEntry> cache, System.TimeSpan ttl)
        {
            if (cache.Count <= 256) return;
            var cutoff = System.DateTime.UtcNow - ttl;
            foreach (var kv in cache)
                if (kv.Value == null || kv.Value.FilledAtUtc < cutoff)
                    cache.TryRemove(kv.Key, out _);
        }

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        private readonly UIService _uiService;
        private readonly NavigationService _navigationService;

        // v2.3.8 (Task 1.4): unified graph navigation. ImpactAnalysis used to
        // run an inline BFS over CalledBy here; it now delegates to
        // CallerGraphService.GetCallersTransitive so callers/callees logic
        // lives in one place.
        private readonly CallerGraphService _graph;

        public AnalyzeService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService, NavigationService navigationService = null, UIService uiService = null, CallerGraphService graph = null)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            _navigationService = navigationService;
            _uiService = uiService;
            _graph = graph ?? new CallerGraphService(indexCacheService);
        }

        // Test-friendly ctor matching the plan signature.
        public AnalyzeService(IndexCacheService index, ObjectService objSvc, CallerGraphService graph)
        {
            _indexCacheService = index;
            _objectService = objSvc;
            _graph = graph ?? new CallerGraphService(index);
        }

        public string Analyze(string target, string typeFilter = null)
        {
            try
            {
                var kb = _kbService.GetKB();
                if (target == null) return Models.McpResponse.Err(code: "TargetRequired",
                    message: "KB-wide analysis (no target) is not implemented; analyze needs a specific object.",
                    hint: "Pass target=<objectName>. To browse the KB use genexus_list_objects; for KB-level health use genexus_doctor.");

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                
                // PERFORMANCE (W-A4): de-duplicate references before issuing SDK.Get calls.
                // GetReferences() returns one edge per call-site, so a procedure that calls the
                // same target 10× would previously cost 10 Get() round-trips. The N stays the
                // same when each reference is unique, but cuts hard in real KBs where repeated
                // edges are common. This is the safe portion of the audited N+1 win — touching
                // the SDK semantics (batched fetch / reverse-resolve via index) needs its own
                // regression suite, so deliberately left out here.
                var calls = new JArray();
                var seenRefKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in obj.GetReferences())
                {
                    string refKey = null;
                    try { refKey = reference.To?.ToString(); } catch { }
                    if (!string.IsNullOrEmpty(refKey) && !seenRefKeys.Add(refKey)) continue;

                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) {
                        var cObj = new JObject();
                        cObj["name"] = targetObj.Name;
                        cObj["type"] = targetObj.TypeDescriptor.Name;
                        cObj["description"] = targetObj.Description;
                        calls.Add(cObj);
                    }
                }
                result["calls"] = calls;

                // Add Impact Radius if Index is available
                try {
                    var impact = GetImpactAnalysis(obj.Name);
                    result["impactAnalysis"] = JObject.Parse(impact);
                } catch {}

                return Models.McpResponse.Ok(target: target, code: "AnalyzeCompleted", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AnalyzeFailed",
                    message: ex.Message,
                    hint: "Ensure the target object exists and the KB index is ready.",
                    target: target);
            }
        }

        // Backwards-compatible entry point used by Analyze() and other callers
        // that don't care about the index-readiness envelope.
        public string GetImpactAnalysis(string targetName)
        {
            return ImpactAnalysis(targetName, waitForIndex: true);
        }

        // v2.3.8 (Task 1.4): index-aware impact analysis. Delegates the BFS to
        // CallerGraphService and adds a waitForIndex contract so callers can
        // opt out of blocking on a cold/reindexing index.
        public string ImpactAnalysis(string targetName, bool waitForIndex = true, int waitTimeoutMs = 30000)
            => ImpactAnalysis(targetName, waitForIndex, waitTimeoutMs, System.Threading.CancellationToken.None);

        // v2.3.8 (post-Task 7.2): same path, threaded with a CancellationToken so
        // a Control:Cancel from the gateway side-channel can stop the BFS mid-walk.
        public string ImpactAnalysis(string targetName, bool waitForIndex, int waitTimeoutMs, System.Threading.CancellationToken ct)
        {
            // v2.6.9 perf: cache repeated impact queries against the same target.
            // The BFS over CalledBy is read-only and 25-50 ms on the STA thread;
            // a 30 s TTL + WriteService.WasTargetWrittenSince invalidation makes
            // repeats sub-ms without affecting freshness after edits.
            //
            // Only consult / populate the cache when the lite index pass has
            // finished (LiteReady / Enriching / Ready). Cold / Reindexing must
            // still flow through the gate below so callers honour waitForIndex
            // semantics and the Reindexing-envelope contract.
            var earlyState = _indexCacheService?.GetState();
            bool earlyLitePassDone = earlyState != null
                && earlyState.Status != "Cold" && earlyState.Status != "Reindexing";
            if (earlyLitePassDone && !string.IsNullOrEmpty(targetName))
            {
                if (_impactCache.TryGetValue(targetName, out var cachedImpact) && cachedImpact != null)
                {
                    bool ttlOk = (System.DateTime.UtcNow - cachedImpact.FilledAtUtc) < ImpactCacheTtl;
                    bool noWrite = !WriteService.WasTargetWrittenSince(targetName, cachedImpact.FilledAtUtc);
                    if (ttlOk && noWrite)
                    {
                        return cachedImpact.Json;
                    }
                    _impactCache.TryRemove(targetName, out _);
                }
            }
            try
            {
                // 1) Index-readiness envelope.
                // v2.4.0 (SP6.T7): with the fast-index split the gate no longer needs to
                // wait for full enrichment ("Ready"). We block only while the lite pass
                // hasn't finished yet (Cold / Reindexing). Once we reach LiteReady,
                // Enriching, or Ready the object catalogue is populated and we can resolve
                // the target; we enrich it on-demand below so its call-graph is complete.
                var state = _indexCacheService?.GetState();
                bool litePassPending = state != null && (state.Status == "Cold" || state.Status == "Reindexing");
                if (litePassPending)
                {
                    if (!waitForIndex)
                    {
                        var env = new JObject { ["status"] = state.Status };
                        if (state.EtaMs.HasValue) env["etaMs"] = state.EtaMs.Value;
                        if (state.Progress.HasValue) env["progress"] = state.Progress.Value;
                        env["hint"] = "Pass waitForIndex=true to block until the lite index pass completes.";
                        return env.ToString();
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < waitTimeoutMs)
                    {
                        var s = _indexCacheService.GetState();
                        if (s == null || (s.Status != "Cold" && s.Status != "Reindexing")) break;
                        System.Threading.Thread.Sleep(250);
                        ct.ThrowIfCancellationRequested();
                    }
                    var finalState = _indexCacheService.GetState();
                    if (finalState == null || finalState.Status == "Cold" || finalState.Status == "Reindexing")
                    {
                        return new JObject
                        {
                            ["status"] = "Timeout",
                            ["waitedMs"] = sw.ElapsedMilliseconds
                        }.ToString();
                    }
                }

                var index = _indexCacheService?.GetIndex();
                if (index == null || index.Objects == null) return Models.McpResponse.Err(code: "SearchIndexMissing", message: "Index not found.", hint: "Run the KB indexing flow before requesting impact analysis.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index" }, "Builds the on-disk SearchIndex required for impact analysis.")), retryAfterMs: 10000, target: targetName);

                // 2) Name resolution — preserve the existing priority ordering
                //    (Procedure > Transaction > Table) so consumers see the
                //    same canonical name they used to.
                SearchIndex.IndexEntry targetNode = null;
                string foundKey = null;

                // v2.3.8 (post-self-review): replace the fragile EndsWith-then-give-up
                // lookup with a 4-stage chain. Closes the residual "Indexing in progress
                // for this object. Please retry in a few seconds" stale message that
                // fired when the index entry's KEY didn't match `:<targetName>` exactly
                // (typically when the entry's Type field was empty/different or the
                // name carried whitespace from SDK serialisation).
                targetNode = ResolveIndexEntry(index, targetName, out foundKey);
                if (targetNode == null)
                {
                    // Last resort: ask the SDK. When it finds the object, use its
                    // canonical name to do one more aggressive scan; if still nothing,
                    // synthesise an empty-but-valid impact envelope so the caller
                    // doesn't have to retry on a polling stub message. The agent gets
                    // a deterministic answer (riskLevel=Unknown + indexEdgesMissing
                    // hint) and can decide whether to re-index.
                    string sdkName = null;
                    try { sdkName = _objectService?.FindObject(targetName)?.Name; } catch { }
                    if (!string.IsNullOrEmpty(sdkName))
                    {
                        targetNode = ResolveIndexEntry(index, sdkName, out foundKey);
                        if (targetNode == null)
                        {
                            return Models.McpResponse.Ok(target: sdkName, code: "ImpactAnalysisCompleted", result: new JObject
                            {
                                ["totalAffected"] = 0,
                                ["blastRadiusScore"] = 0,
                                ["riskLevel"] = "Unknown",
                                ["callers"] = new JArray(),
                                ["callees"] = new JArray(),
                                ["indexEdgesMissing"] = true,
                                ["hint"] = "Object was found in the KB via the SDK but its call-graph edges are not yet in the search index. Call genexus_lifecycle(action='index', force=true) to clear the stale snapshot and run a full SDK rescan, then retry."
                            });
                        }
                    }
                    else
                    {
                        return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found in index.", hint: "The object was not found in the search index or the active Knowledge Base. Re-run after genexus_lifecycle action=index completes.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index", ["force"] = true }, "Forces a full KB rescan so newly added objects are discoverable.")), target: targetName);
                    }
                }

                targetName = targetNode.Name; // canonical name

                // 2b) On-demand enrichment (SP6.T7): if the lite pass is done but the
                // target entry hasn't been enriched yet (SourceSnippet/Calls/CalledBy not
                // populated), promote it immediately so the BFS has accurate edges.
                if (!targetNode.IsEnriched)
                {
                    var enrichQueue = _indexCacheService?.GetEnrichmentQueue();
                    if (enrichQueue != null)
                        enrichQueue.PromoteAsync(targetNode, ct).GetAwaiter().GetResult();
                }

                // 3) Delegate the BFS to the unified graph service.
                var graph = _graph ?? new CallerGraphService(_indexCacheService);
                var callersResult = graph.GetCallersTransitive(targetName, 200, ct);
                if (ct.IsCancellationRequested) return Models.McpResponse.Ok(target: targetName, code: "Cancelled", result: new JObject());
                var calleesResult = graph.GetCalleesTransitive(targetName, 200, ct);
                if (ct.IsCancellationRequested) return Models.McpResponse.Ok(target: targetName, code: "Cancelled", result: new JObject());

                // De-dupe + index-aware enrichment (score / entry points / etc.).
                var affected = new HashSet<string>(callersResult.Nodes, StringComparer.OrdinalIgnoreCase);

                int score = 0;
                var entryPoints = new List<string>();
                foreach (var name in affected)
                {
                    if (index.Objects.TryGetValue(name, out var node) ||
                        TryFindByBareName(index, name, out node))
                    {
                        if (node != null)
                        {
                            score += GetTypeWeight(node.Type);
                            if (IsEntryPoint(node)) entryPoints.Add(name);
                        }
                    }
                }

                var json = new JObject();
                json["target"] = targetName;
                // v2.8.5: surface WHICH object was resolved. inspect and impact used
                // different resolvers (inspect→Table, impact→Transaction for a colliding
                // name), so an agent saw "5 callers" from one tool and "0" from the other
                // with no way to tell they analysed different objects. resolvedType makes
                // the resolution explicit on both sides.
                json["resolvedType"] = targetNode.Type ?? "";
                json["totalAffected"] = affected.Count;
                json["blastRadiusScore"] = score;
                json["riskLevel"] = score > 100 ? "High" : (score > 20 ? "Medium" : "Low");

                var callersArr = new JArray();
                foreach (var c in callersResult.Nodes) callersArr.Add(c);
                json["callers"] = callersArr;
                json["callersTruncated"] = callersResult.Truncated;

                var calleesArr = new JArray();
                foreach (var c in calleesResult.Nodes) calleesArr.Add(c);
                json["callees"] = calleesArr;
                json["calleesTruncated"] = calleesResult.Truncated;
                json["maxDepth"] = Math.Max(callersResult.Depth, calleesResult.Depth);

                // v2.8.5 (friction 2026-06-02): zero-signal honesty.
                // The index can hold a node but carry no call-graph edges for it
                // (not yet enriched, or a stale snapshot). The old code mapped that
                // score==0 case straight to riskLevel "Low" — indistinguishable from
                // a genuinely-safe object, and the exact bug that made impact report
                // blastRadius 0 for an object inspect showed as having callers. When
                // the index yields zero edges, cross-check the live SDK reference
                // graph (same source genexus_inspect uses) before claiming "Low".
                bool zeroFromIndex = callersResult.Nodes.Count == 0 && calleesResult.Nodes.Count == 0;
                if (zeroFromIndex)
                {
                    var sdk = TrySdkReferenceCrossCheck(targetName);
                    if (sdk == null)
                    {
                        // No SDK available to confirm — must NOT assert "Low" on no signal.
                        json["riskLevel"] = "Unknown";
                        json["indexEdgesMissing"] = true;
                        json["verifiedZero"] = false;
                        json["hint"] = "The search index holds this object but no call-graph edges for it (it may not be enriched yet, or the snapshot is stale). Blast radius could NOT be confirmed — this is NOT a guarantee of zero impact. Run genexus_lifecycle(action='index', force=true) and retry, or cross-check with genexus_inspect(include=['callers']).";
                    }
                    else if (sdk.Callers.Count > 0 || sdk.Callees.Count > 0)
                    {
                        // SDK sees edges the index missed — surface them and flag the gap
                        // instead of reporting a misleading "0 affected / Low".
                        json["riskLevel"] = sdk.Callers.Count > 5 ? "Medium" : "Low";
                        json["totalAffected"] = sdk.Callers.Count;
                        json["indexEdgesMissing"] = true;
                        var sdkCallers = new JArray();
                        foreach (var c in sdk.Callers) sdkCallers.Add(c);
                        var sdkCallees = new JArray();
                        foreach (var c in sdk.Callees) sdkCallees.Add(c);
                        json["sdkCrossCheck"] = new JObject
                        {
                            ["callers"] = sdkCallers,
                            ["callees"] = sdkCallees,
                            ["note"] = "Index call-graph edges weren't populated for this object yet; these edges come from the live SDK reference graph, which is authoritative. With lazy enrichment (default) the index edges fill in on demand — re-running impact for the same target shortly will use them. (force=true does NOT eagerly enrich in lazy mode.)"
                        };
                    }
                    else
                    {
                        // SDK confirms genuinely zero incoming/outgoing references.
                        json["riskLevel"] = "None";
                        json["verifiedZero"] = true;
                    }
                }

                var topEntryPoints = new JArray();
                foreach (var ep in entryPoints.Take(50)) topEntryPoints.Add(ep);
                json["affectedEntryPoints"] = topEntryPoints;

                var topAffected = new JArray();
                foreach (var aff in affected.Take(50)) topAffected.Add(aff);
                json["topImpacted"] = topAffected;

                GxMcp.Worker.Helpers.ProgressEmitter.Emit(100, 100, "Impact analysis: complete");

                string impactJson = Models.McpResponse.Ok(target: targetName, code: "ImpactAnalysisCompleted", result: json);
                // v2.6.9 perf: populate impact cache (see _impactCache decl).
                if (!string.IsNullOrEmpty(targetName))
                {
                    SweepExpired(_impactCache, ImpactCacheTtl);
                    _impactCache[targetName] = new InspectCacheEntry
                    {
                        Json = impactJson,
                        FilledAtUtc = System.DateTime.UtcNow
                    };
                }
                return impactJson;
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "ImpactAnalysisFailed", message: "Impact Analysis failed: " + ex.Message, hint: "Check the worker log for the underlying exception. Re-run after verifying the KB index is ready.", target: targetName);
            }
        }

        // v2.3.8 (post-self-review) — 4-stage index entry resolver. Covers the
        // failure modes that were leaking into the legacy "Indexing in progress
        // for this object" stale message:
        //  1. caller passed a "Type:Name" key directly
        //  2. key endswith ":<name>" (case-insensitive)
        //  3. value scan by Name field (cheapest after #2; catches entries whose
        //     Type was empty so the EndsWith pattern didn't match)
        //  4. value scan by trimmed/lowercased Name (catches whitespace/casing
        //     drift between SDK serialisation and index ingestion)
        private static SearchIndex.IndexEntry ResolveIndexEntry(SearchIndex index, string targetName, out string foundKey)
        {
            foundKey = null;
            if (index?.Objects == null || string.IsNullOrEmpty(targetName)) return null;

            // Stage 1: direct key lookup
            if (targetName.Contains(":") && index.Objects.TryGetValue(targetName, out var direct))
            {
                foundKey = targetName;
                return direct;
            }

            // Stage 2: EndsWith on the key suffix, type-priority ordering.
            var possibleKeys = index.Objects.Keys
                .Where(k => k.EndsWith(":" + targetName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (possibleKeys.Count > 0)
            {
                foundKey = possibleKeys.FirstOrDefault(k => k.StartsWith("Procedure:", StringComparison.OrdinalIgnoreCase))
                         ?? possibleKeys.FirstOrDefault(k => k.StartsWith("Transaction:", StringComparison.OrdinalIgnoreCase))
                         ?? possibleKeys.FirstOrDefault(k => k.StartsWith("WebPanel:", StringComparison.OrdinalIgnoreCase))
                         ?? possibleKeys.FirstOrDefault(k => k.StartsWith("DataProvider:", StringComparison.OrdinalIgnoreCase))
                         ?? possibleKeys.FirstOrDefault(k => k.StartsWith("Table:", StringComparison.OrdinalIgnoreCase))
                         ?? possibleKeys.First();
                if (index.Objects.TryGetValue(foundKey, out var byKey)) return byKey;
            }

            // Stage 3: exact Name match on the values (handles entries whose
            // stored Type is empty/null so the EndsWith on the key missed).
            foreach (var kv in index.Objects)
            {
                if (kv.Value != null && string.Equals(kv.Value.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    foundKey = kv.Key;
                    return kv.Value;
                }
            }

            // Stage 4: trimmed match — last resort for whitespace/encoding drift
            // on the entry side. Runs unconditionally on miss; the caller's name
            // might be clean while the index entry's Name carries SDK whitespace.
            var trimmed = targetName.Trim();
            if (trimmed.Length > 0)
            {
                foreach (var kv in index.Objects)
                {
                    if (kv.Value != null && string.Equals(kv.Value.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        foundKey = kv.Key;
                        return kv.Value;
                    }
                }
            }

            return null;
        }

        // Helper: graph service returns bare names (e.g. "A"), but the index
        // keys things as "Type:Name". Resolve a bare name back to its entry by
        // scanning the keys once we don't have a direct hit.
        private static bool TryFindByBareName(SearchIndex index, string bareName, out SearchIndex.IndexEntry entry)
        {
            entry = null;
            if (index?.Objects == null || string.IsNullOrEmpty(bareName)) return false;
            foreach (var kv in index.Objects)
            {
                var v = kv.Value;
                if (v != null && string.Equals(v.Name, bareName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = v;
                    return true;
                }
            }
            return false;
        }

        // v2.8.5: container for SDK reference-graph cross-check results.
        private sealed class SdkRefs
        {
            public readonly List<string> Callers = new List<string>();
            public readonly List<string> Callees = new List<string>();
        }

        // v2.8.5 (friction 2026-06-02): when the index reports zero edges for an
        // object, consult the live SDK reference graph (GetReferencesTo / GetReferences)
        // — the same source genexus_inspect uses — to tell "genuinely zero" apart from
        // "index not enriched". Returns null when no SDK/ObjectService is wired (unit
        // tests, cold worker) so the caller falls back to riskLevel=Unknown instead of
        // asserting a misleading "Low". Best-effort and bounded; never throws.
        private SdkRefs TrySdkReferenceCrossCheck(string name)
        {
            if (_objectService == null || string.IsNullOrEmpty(name)) return null;
            KBObject obj;
            try { obj = _objectService.FindObject(name); }
            catch { return null; }
            if (obj == null) return null;

            dynamic kb = null;
            try { kb = _kbService?.GetKB(); } catch { kb = null; }

            var refs = new SdkRefs();
            const int cap = 50;
            try
            {
                foreach (var r in obj.GetReferencesTo())
                {
                    if (refs.Callers.Count >= cap) break;
                    try
                    {
                        var s = kb?.DesignModel.Objects.Get(r.From);
                        if (s != null && !refs.Callers.Contains(s.Name)) refs.Callers.Add(s.Name);
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                foreach (var r in obj.GetReferences())
                {
                    if (refs.Callees.Count >= cap) break;
                    try
                    {
                        var t = kb?.DesignModel.Objects.Get(r.To);
                        if (t != null && !refs.Callees.Contains(t.Name)) refs.Callees.Add(t.Name);
                    }
                    catch { }
                }
            }
            catch { }
            return refs;
        }

        private int GetTypeWeight(string type)
        {
            if (string.IsNullOrEmpty(type)) return 1;
            var t = type.ToLower();
            if (t.Contains("transaction")) return 10;
            if (t.Contains("webpanel")) return 5;
            if (t.Contains("procedure")) return 3;
            if (t.Contains("dataselector")) return 5;
            if (t.Contains("attribute")) return 8;
            return 1;
        }

        private bool IsEntryPoint(GxMcp.Worker.Models.SearchIndex.IndexEntry node)
        {
            if (node.Type == "Transaction" || node.Type == "WebPanel") return true;
            if (!string.IsNullOrEmpty(node.Description) && node.Description.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }


        public string GetAttributeMetadata(string name)
        {
            try
            {
                var kb = _kbService.GetKB();
                foreach (var obj in kb.DesignModel.Objects.GetByName(null, null, name))
                {
                    if (string.Equals(obj.TypeDescriptor.Name, "Attribute", StringComparison.OrdinalIgnoreCase))
                    {
                        dynamic attr = obj;
                        var json = new JObject();
                        json["name"] = attr.Name;
                        json["description"] = attr.Description;
                        json["type"] = attr.Type.ToString();
                        json["length"] = (int)attr.Length;
                        json["decimals"] = (int)attr.Decimals;
                        
                        try {
                            if (attr.Table != null) {
                                json["table"] = attr.Table.Name;
                            }
                        } catch {}

                        return json.ToString();
                    }
                }
                return Models.McpResponse.Err(code: "AttributeNotFound", message: "Attribute not found.", hint: "Confirm the attribute name is correct and exists in the active KB.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", new JObject { ["typeFilter"] = "Attribute" }, "Lists all attributes in the KB so you can pick the right name.")));
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "GetAttributeMetadataFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        public string GetVariables(string name, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                if (vPart == null) return Models.McpResponse.Err(code: "VariablesPartNotFound", message: "Variables part not found.", hint: "The object does not expose a Variables part. Check availableParts for what this object supports.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = name }, "Returns availableParts so you can see which parts this object exposes.")), target: name, extra: new JObject { ["objectName"] = obj.Name, ["objectType"] = obj.TypeDescriptor?.Name, ["availableParts"] = new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj)) });

                var variables = new JArray();
                int idx = 0;
                foreach (Variable var in vPart.Variables)
                {
                    idx++;
                    var item = new JObject();
                    item["name"] = var.Name;
                    item["type"] = var.Type.ToString();
                    // Layout XML uses AttID="var:N" — surface the internal id so agents don't grep the generated .cs.
                    int? internalId = VariableInjector.GetVariableInternalId(var, idx);
                    if (internalId.HasValue) item["internalId"] = internalId.Value;
                    var managedBy = GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(var.Name);
                    if (managedBy != null) item["managedBy"] = managedBy;
                    variables.Add(item);
                }

                var result = new JObject();
                result["variables"] = variables;
                result["source"] = VariableInjector.GetVariablesAsText((dynamic)vPart);
                return Models.McpResponse.Ok(target: name, code: "VariablesRead", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "GetVariablesFailed", message: ex.Message, hint: "Ensure the KB is open and the target object exposes a Variables part.", target: name);
            }
        }

        // Item 23 — Event-flow ASCII viz for WebPanel/SDPanel.
        // Parses the Events source and groups handlers under the gate that fires
        // them (Start → Refresh → user events). No control-flow inference;
        // events that exist get listed in canonical order so the agent gets
        // a one-glance map of the surface.
        public string GetEventFlow(string name, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                string source = null;
                try
                {
                    if (obj is global::Artech.Genexus.Common.Objects.WebPanel wbp)
                        source = wbp.Parts.Get<global::Artech.Genexus.Common.Parts.EventsPart>()?.Source ?? "";
                    else if (obj is global::Artech.Genexus.Common.Objects.Transaction trn)
                        source = trn.Parts.Get<global::Artech.Genexus.Common.Parts.EventsPart>()?.Source ?? "";
                }
                catch { source = ""; }

                if (string.IsNullOrEmpty(source))
                {
                    return new JObject
                    {
                        ["name"] = obj.Name,
                        ["type"] = obj.TypeDescriptor.Name,
                        ["events"] = new JArray(),
                        ["asciiTree"] = obj.Name + " (no Events source)"
                    }.ToString();
                }

                // Cheap parser: pull every `Event <Name>` declaration. Order is preserved
                // so the agent sees them as authored. Sub names are ignored (they're not
                // event entry-points).
                var rx = new System.Text.RegularExpressions.Regex(
                    @"^\s*Event\s+([A-Za-z_][\w\.]*)\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
                var seenEvent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var events = new JArray();
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(source))
                {
                    string evName = m.Groups[1].Value;
                    if (seenEvent.Add(evName))
                        events.Add(evName);
                }

                // Group: lifecycle (Start/Refresh/Load) first; everything else after.
                string[] lifecycle = { "Start", "Refresh", "Load", "ClientStart" };
                var sb = new System.Text.StringBuilder();
                sb.Append(obj.Name).Append(" (").Append(obj.TypeDescriptor.Name).Append(')').Append('\n');
                var lifecycleEvents = events.Where(e => lifecycle.Contains(e.ToString(), StringComparer.OrdinalIgnoreCase)).Select(e => e.ToString()).ToList();
                var userEvents = events.Where(e => !lifecycle.Contains(e.ToString(), StringComparer.OrdinalIgnoreCase)).Select(e => e.ToString()).ToList();
                sb.Append("├─ lifecycle (").Append(lifecycleEvents.Count).Append(")\n");
                for (int i = 0; i < lifecycleEvents.Count; i++)
                    sb.Append("│  ").Append(i == lifecycleEvents.Count - 1 ? "└─ " : "├─ ").Append(lifecycleEvents[i]).Append('\n');
                sb.Append("└─ user events (").Append(userEvents.Count).Append(")\n");
                for (int i = 0; i < userEvents.Count; i++)
                    sb.Append("   ").Append(i == userEvents.Count - 1 ? "└─ " : "├─ ").Append(userEvents[i]).Append('\n');

                return new JObject
                {
                    ["name"] = obj.Name,
                    ["type"] = obj.TypeDescriptor.Name,
                    ["events"] = events,
                    ["asciiTree"] = sb.ToString()
                }.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("GetEventFlow failed: " + ex);
                return Models.McpResponse.Err(code: "GetEventFlowFailed", message: "GetEventFlow failed: " + ex.Message, hint: "Ensure the object has an Events part and the KB is fully loaded.", target: name);
            }
        }

        private static string BuildHierarchyAsciiTree(string rootName, string rootType, JArray calls, JArray calledBy)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(rootName).Append(" (").Append(rootType).Append(')').Append('\n');
            void Render(string label, JArray arr, bool last)
            {
                string branch = last ? "└─ " : "├─ ";
                string pad = last ? "   " : "│  ";
                sb.Append(branch).Append(label).Append(" (").Append(arr.Count).Append(')').Append('\n');
                for (int i = 0; i < arr.Count; i++)
                {
                    bool isLast = i == arr.Count - 1;
                    var item = (JObject)arr[i];
                    sb.Append(pad).Append(isLast ? "└─ " : "├─ ")
                      .Append(item["name"]).Append(" (").Append(item["type"]).Append(')').Append('\n');
                }
            }
            Render("calls", calls, last: false);
            Render("calledBy", calledBy, last: true);
            return sb.ToString();
        }

        public string GetHierarchy(string name, string typeFilter = null)
        {
            try
            {
                var kb = _kbService.GetKB();
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;

                // issue #25 follow-up (P1): cap edge lists so a heavily-referenced
                // base object (Domain/base Transaction) can't return hundreds of
                // full-description entries in one payload.
                const int maxEdges = 100;
                // PERFORMANCE (W-A4): dedup outgoing references — same justification as Analyze.
                var calls = new JArray();
                var seenOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool callsTruncated = false;
                foreach (var reference in obj.GetReferences())
                {
                    string refKey = null;
                    try { refKey = reference.To?.ToString(); } catch { }
                    if (!string.IsNullOrEmpty(refKey) && !seenOut.Add(refKey)) continue;
                    if (calls.Count >= maxEdges) { callsTruncated = true; break; }

                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) calls.Add(new JObject {
                        ["name"] = targetObj.Name,
                        ["type"] = targetObj.TypeDescriptor.Name,
                        ["description"] = targetObj.Description
                    });
                }
                result["calls"] = calls;
                if (callsTruncated) result["callsTruncated"] = true;

                // PERFORMANCE (W-A4): dedup incoming references.
                var calledBy = new JArray();
                var seenIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool calledByTruncated = false;
                foreach (var reference in obj.GetReferencesTo())
                {
                    string refKey = null;
                    try { refKey = reference.From?.ToString(); } catch { }
                    if (!string.IsNullOrEmpty(refKey) && !seenIn.Add(refKey)) continue;
                    if (calledBy.Count >= maxEdges) { calledByTruncated = true; break; }

                    var sourceObj = kb.DesignModel.Objects.Get(reference.From);
                    if (sourceObj != null) calledBy.Add(new JObject {
                        ["name"] = sourceObj.Name,
                        ["type"] = sourceObj.TypeDescriptor.Name,
                        ["description"] = sourceObj.Description
                    });
                }
                result["calledBy"] = calledBy;
                if (calledByTruncated) result["calledByTruncated"] = true;

                // Friction item 25: ASCII tree view — eyeball-friendly summary the agent
                // can paste back to the user without re-formatting the flat arrays.
                result["asciiTree"] = BuildHierarchyAsciiTree(obj.Name, obj.TypeDescriptor.Name, calls, calledBy);

                return result.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "GetHierarchyFailed", message: ex.Message, hint: "Ensure the KB is open and the target object exists.");
            }
        }

        // Phase 2: genexus_inspect piggyback. Resolves the real object type off the
        // (already-built) response json when typeFilter wasn't given, so type-scoped
        // memories match even on a plain `inspect target=Foo` call. Defensive — never
        // lets memory surfacing break an inspect response.
        private string AttachRelevantMemory(string json, string objectName, string typeFilter)
        {
            try
            {
                string kbPath = _kbService?.GetKbPath();
                string objectType = typeFilter;
                if (string.IsNullOrEmpty(objectType))
                {
                    try { objectType = JObject.Parse(json)?["type"]?.ToString(); } catch { }
                }
                return MemoryService.AttachRelevantMemory(kbPath, json, objectName, objectType);
            }
            catch
            {
                return json;
            }
        }

        private string FormatInspectNotFound(string name)
        {
            var diagnostic = _objectService?.GetLastResolutionDiagnostic();
            if (diagnostic != null)
            {
                return Models.McpResponse.Err(
                    code: "IndexedObjectUnavailable",
                    message: "The search index contains the object, but the active SDK could not resolve its native identity.",
                    hint: diagnostic["hint"]?.ToString(),
                    target: name,
                    errorExtra: diagnostic);
            }
            return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());
        }

        // KB-wide source analytics over the index (zero SDK reads). Aggregates the per-object
        // CodeMetrics captured at enrichment: totals + optimization candidates (nested for-each,
        // where-heavy procedures). typeFilter defaults to Procedure+DataProvider.
        public string GetCodeMetrics(string typeFilter = null, int top = 25)
        {
            try
            {
                var index = _indexCacheService?.GetIndex();
                if (index == null || index.Objects == null)
                    return Models.McpResponse.Err(code: "SearchIndexMissing", message: "Index not found.",
                        hint: "Run genexus_lifecycle action=index, then retry.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index" }, "Builds the on-disk SearchIndex code metrics aggregate over.")),
                        retryAfterMs: 10000);
                if (top <= 0) top = 25;
                if (top > 200) top = 200;

                bool typed = !string.IsNullOrWhiteSpace(typeFilter);
                long fe = 0, nfe = 0, wh = 0, nw = 0, cm = 0, lines = 0;
                int objects = 0, withMetrics = 0, missing = 0;
                var withM = new List<Models.SearchIndex.IndexEntry>();

                foreach (var e in index.Objects.Values)
                {
                    if (e == null) continue;
                    if (typed) { if (!string.Equals(e.Type, typeFilter, StringComparison.OrdinalIgnoreCase)) continue; }
                    else if (!(string.Equals(e.Type, "Procedure", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(e.Type, "DataProvider", StringComparison.OrdinalIgnoreCase))) continue;
                    objects++;
                    if (e.Metrics == null) { missing++; continue; }
                    withMetrics++;
                    var mm = e.Metrics;
                    fe += mm.ForEach; nfe += mm.NestedForEach; wh += mm.Where;
                    nw += mm.New; cm += mm.Commit; lines += mm.Lines;
                    withM.Add(e);
                }

                Func<Models.SearchIndex.IndexEntry, JObject> row = e => new JObject
                {
                    ["name"] = e.Name, ["type"] = e.Type,
                    ["forEach"] = e.Metrics.ForEach, ["nestedForEach"] = e.Metrics.NestedForEach,
                    ["where"] = e.Metrics.Where, ["new"] = e.Metrics.New, ["commit"] = e.Metrics.Commit,
                    ["lines"] = e.Metrics.Lines
                };

                var candidates = new JArray(withM
                    .Where(e => e.Metrics.NestedForEach > 0)
                    .OrderByDescending(e => e.Metrics.NestedForEach)
                    .ThenByDescending(e => e.Metrics.ForEach)
                    .Take(top).Select(row).ToArray());

                var topForEach = new JArray(withM
                    .Where(e => e.Metrics.ForEach > 0)
                    .OrderByDescending(e => e.Metrics.ForEach)
                    .Take(top).Select(row).ToArray());

                var result = new JObject
                {
                    ["scope"] = typed ? typeFilter : "Procedure+DataProvider",
                    ["totals"] = new JObject
                    {
                        ["objects"] = objects, ["withMetrics"] = withMetrics, ["missingMetrics"] = missing,
                        ["forEach"] = fe, ["nestedForEach"] = nfe, ["where"] = wh,
                        ["new"] = nw, ["commit"] = cm, ["lines"] = lines
                    },
                    ["optimizationCandidates"] = candidates,
                    ["topByForEach"] = topForEach
                };
                if (missing > 0)
                    result["note"] = missing + " object(s) have no metrics yet (indexed before code-metrics landed, or not re-enriched). Run genexus_lifecycle action=index force=true for complete coverage.";
                result["hint"] = "optimizationCandidates = procedures with nested 'for each' (a for-each inside another) — the classic smell that often collapses to a single navigation or a data selector. Cross-check where counts; open one with genexus_read part=Source.";
                return Models.McpResponse.Ok(code: "CodeMetricsAggregated", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "CodeMetricsFailed", message: ex.Message);
            }
        }

        public string GetConversionContext(string name, JArray include = null, string typeFilter = null, string projection = "standard",
            string guid = null, string entityKey = null, string path = null)
        {
            // Wire the long-advertised (but previously unimplemented) projection knob:
            //   minimal  = name/type/description/lifecycle only (cheapest orient)
            //   standard = lean default (source heads capped at 1200, variables name+type, ≤40)
            //   verbose  = full (source heads capped at 8000, full variable detail)
            projection = string.IsNullOrWhiteSpace(projection) ? "standard" : projection.Trim().ToLowerInvariant();
            bool verbose = projection == "verbose";
            bool minimal = projection == "minimal";
            // v2.6.9 perf: inspect cache. Build a stable key from name + sorted
            // include set + typeFilter, then reuse a fresh entry if no write
            // landed against the target since the cache was filled.
            string includeKey = include == null || include.Count == 0
                ? "*"
                : string.Join(",", include.Select(i => (i?.ToString() ?? string.Empty).Trim().ToLowerInvariant())
                                          .Where(s => s.Length > 0)
                                          .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase));
            // projection must partition the cache — minimal/standard/verbose payloads differ.
            string inspectKey = (name ?? "") + "|" + includeKey + "|" + (typeFilter ?? "") + "|" + projection
                + "|" + (guid ?? "") + "|" + (entityKey ?? "") + "|" + (path ?? "");
            if (_inspectCache.TryGetValue(inspectKey, out var cached) && cached != null)
            {
                bool ttlOk = (System.DateTime.UtcNow - cached.FilledAtUtc) < InspectCacheTtl;
                bool noConcurrentWrite = !WriteService.WasTargetWrittenSince(name, cached.FilledAtUtc);
                if (ttlOk && noConcurrentWrite)
                {
                    // Phase 2: memory piggyback stays OUT of the cached json (always
                    // fresh) — attach it to a copy on every cache hit instead.
                    return AttachRelevantMemory(cached.Json, name, typeFilter);
                }
                _inspectCache.TryRemove(inspectKey, out _);
            }
            try
            {
                var obj = _objectService.FindObject(name, typeFilter, guid, entityKey, path);
                if (obj == null) return FormatInspectNotFound(name);

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["identity"] = _objectService.BuildObjectIdentity(obj);
                result["description"] = obj.Description;

                // v2.8.5: ambiguity disclosure. When a name resolves across multiple
                // object types (classic: a Transaction and its generated Table share a
                // name), inspect used to pick one silently. Surface the alternatives so
                // the agent knows it can re-query with type=... — this is what made
                // inspect(X) and analyze(impact,X) appear to contradict each other:
                // they silently resolved different objects for the same name.
                try
                {
                    var ambIndex = _indexCacheService?.GetIndex();
                    if (ambIndex?.Objects != null)
                    {
                        var others = new JArray();
                        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { obj.TypeDescriptor.Name ?? string.Empty };
                        foreach (var kv in ambIndex.Objects)
                        {
                            var e = kv.Value;
                            if (e == null || string.IsNullOrEmpty(e.Type)) continue;
                            if (string.Equals(e.Name, obj.Name, StringComparison.OrdinalIgnoreCase)
                                && seenTypes.Add(e.Type))
                            {
                                others.Add(new JObject { ["name"] = e.Name, ["type"] = e.Type });
                            }
                        }
                        if (others.Count > 0)
                        {
                            result["resolvedAs"] = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name };
                            result["alsoMatches"] = others;
                        }
                    }
                }
                catch { /* disclosure is best-effort; never break inspect */ }

                // Attribute-specific: surface physical tables that host this attribute
                if (obj is global::Artech.Genexus.Common.Objects.Attribute)
                {
                    try
                    {
                        var index = _indexCacheService.GetIndex();
                        if (index.Objects.TryGetValue($"Attribute:{obj.Name}", out var entry) && entry != null)
                        {
                            var tablesArr = new JArray();
                            if (entry.Tables != null)
                            {
                                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var t in entry.Tables)
                                {
                                    if (!string.IsNullOrWhiteSpace(t) && seen.Add(t))
                                        tablesArr.Add(t);
                                }
                            }
                            result["tables"] = tablesArr;
                        }
                        else
                        {
                            result["tables"] = new JArray();
                        }
                    }
                    catch { /* swallow — keep response valid */ }
                }

                // W6 — Theme-specific: classes + control compatibility + (optional) CSS rules.
                // Accepts `include=["classes"]` (default for theme objects) and `include=["classesFull"]`
                // for cssRule + properties dictionary. Filters via `controlTypeFilter` / `nameFilter`
                // in the include array (encoded as inline JSON objects). Reflection-based — see
                // ThemeInspector for the SDK probe.
                if (ThemeInspector.IsTheme(obj))
                {
                    try
                    {
                        string detail = (include != null && include.Any(i => string.Equals(i?.ToString(), "classesFull", StringComparison.OrdinalIgnoreCase)))
                            ? "full" : "summary";
                        var themeView = ThemeInspector.InspectTheme(obj, detail);
                        if (themeView != null)
                        {
                            foreach (var p in themeView.Properties())
                            {
                                // Skip name/type/description we already wrote.
                                if (string.Equals(p.Name, "name", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(p.Name, "description", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                result[p.Name] = p.Value;
                            }
                        }
                    }
                    catch { /* swallow — keep response valid */ }
                }

                // DataProvider-specific: returnsSDT + readsFromTables
                if (obj is Artech.Genexus.Common.Objects.DataProvider dpObj)
                {
                    try
                    {
                        string returnsSdt = TryGetDataProviderReturnsSdt(dpObj);
                        if (!string.IsNullOrEmpty(returnsSdt))
                            result["returnsSDT"] = returnsSdt;

                        var tables = ResolveDataProviderTablesRead(dpObj);
                        if (tables.Count > 0)
                        {
                            var arr = new JArray();
                            foreach (var t in tables) arr.Add(t);
                            result["readsFromTables"] = arr;
                        }
                    }
                    catch { /* swallow — keep response valid */ }
                }

                // projection=minimal: cheapest orient — name/type/description + lifecycle only.
                // Skips every SDK part read; ~30-50 tokens vs a few thousand for standard/verbose.
                if (minimal)
                {
                    try
                    {
                        var life = new JObject();
                        DateTime lu = default;
                        string lub = null;
                        try { lu = obj.LastUpdate; } catch { }
                        try { lub = obj.UserName; } catch { }
                        if (lu > DateTime.MinValue) life["lastUpdate"] = lu.ToUniversalTime().ToString("o");
                        if (!string.IsNullOrEmpty(lub)) life["lastModifiedBy"] = lub;
                        if (life.Count > 0) result["lifecycle"] = life;
                    }
                    catch { }
                    result["projection"] = "minimal";
                    result["availableParts"] = new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj));
                    result["hint"] = "Minimal projection. Pass projection=standard for variables+signature+source heads, or projection=verbose for full detail.";
                    string mjson = result.ToString(Newtonsoft.Json.Formatting.None);
                    SweepExpired(_inspectCache, InspectCacheTtl);
                    _inspectCache[inspectKey] = new InspectCacheEntry { Json = mjson, FilledAtUtc = System.DateTime.UtcNow };
                    return AttachRelevantMemory(mjson, obj.Name, obj.TypeDescriptor.Name);
                }

                bool includeAll = (include == null || include.Count == 0);
                HashSet<string> requested = includeAll ? new HashSet<string>() : new HashSet<string>(include.Select(i => i.ToString().ToLower()));

                // PERFORMANCE: Parallel execution of metadata extraction
                var tasks = new List<Task>();

                // 1. Signature (Parameters)
                if (includeAll || requested.Contains("signature"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                            lock (result) {
                                if (!string.IsNullOrEmpty(parmRule)) result["parmRule"] = parmRule;
                                var parameters = new JArray();
                                foreach (var p in parms) parameters.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                                result["parameters"] = parameters;
                            }
                        } catch {}
                    }));
                }

                // 2. Variables
                if (includeAll || requested.Contains("variables"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                            if (vPart != null) {
                                // Lean default: name+type only, capped at 40 rows — enough to orient.
                                // verbose=true adds length/decimals/internalId and the full list.
                                const int leanVarCap = 40;
                                var variables = new JArray();
                                int idxLocal = 0;
                                int emitted = 0;
                                foreach (Variable v in vPart.Variables) {
                                    idxLocal++;
                                    if (!verbose && emitted >= leanVarCap) continue;
                                    var entry = new JObject { ["name"] = v.Name, ["type"] = v.Type.ToString() };
                                    if (verbose) {
                                        entry["length"] = (int)v.Length;
                                        entry["decimals"] = (int)v.Decimals;
                                        // FR#1 + FR#13: also surface internalId here.
                                        int? id = VariableInjector.GetVariableInternalId(v, idxLocal);
                                        if (id.HasValue) entry["internalId"] = id.Value;
                                    }
                                    variables.Add(entry);
                                    emitted++;
                                }
                                lock (result) {
                                    result["variables"] = variables;
                                    if (!verbose && idxLocal > emitted) {
                                        result["variablesTruncated"] = true;
                                        result["variablesTotal"] = idxLocal;
                                    }
                                }
                            }

                            // FR#3 (friction-report 2026-05-19): scan WebFormPart layout for all
                            // AttID="var:N" / "att:N" references so the agent knows which slots are
                            // taken when authoring new <gxAttribute /> bindings. The actual N→variable
                            // mapping requires runtime resolution we cannot reproduce here, but
                            // exposing the in-use list eliminates trial-and-error guessing.
                            try
                            {
                                var wfPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("WebFormPart"));
                                if (wfPart != null)
                                {
                                    var xmlDocProp = wfPart.GetType().GetProperty("Document");
                                    var xmlDoc = xmlDocProp?.GetValue(wfPart) as System.Xml.XmlDocument;
                                    if (xmlDoc?.DocumentElement != null)
                                    {
                                        string layoutXml = xmlDoc.OuterXml;
                                        var idsInUse = new JArray();
                                        var seen = new HashSet<string>();
                                        var re = new System.Text.RegularExpressions.Regex(
                                            "AttID=\"((?:var|att):\\d+)\"",
                                            System.Text.RegularExpressions.RegexOptions.Compiled);
                                        foreach (System.Text.RegularExpressions.Match m in re.Matches(layoutXml))
                                        {
                                            string id = m.Groups[1].Value;
                                            if (seen.Add(id)) idsInUse.Add(id);
                                        }
                                        if (idsInUse.Count > 0)
                                            lock (result) result["layoutAttIdsInUse"] = idsInUse;

                                        // FR#1 + FR#2 (friction-report 2026-05-19): surface static
                                        // gotcha warnings so the agent learns at inspect time, not
                                        // after build+browser smoke. Currently catches gxButton custom
                                        // OnClickEvent in html forms and gxAttribute Radio/Combo bound
                                        // to a var that shadows a transaction attribute.
                                        try
                                        {
                                            var gotchas = LayoutGotchaScanner.Scan(layoutXml, obj);
                                            if (gotchas != null && gotchas.Count > 0)
                                            {
                                                var arr = new JArray();
                                                foreach (var g in gotchas)
                                                {
                                                    var entry = new JObject
                                                    {
                                                        ["code"] = g.Code,
                                                        ["docUrl"] = g.DocUrl,
                                                        ["severity"] = g.Severity,
                                                        ["element"] = g.Element,
                                                        ["controlId"] = g.ControlId,
                                                        ["message"] = g.Message,
                                                        ["workaround"] = g.Workaround
                                                    };
                                                    arr.Add(entry);
                                                }
                                                lock (result) result["layoutGotchas"] = arr;
                                            }
                                        }
                                        catch { /* scanner best-effort */ }
                                    }
                                }
                            }
                            catch { /* best-effort */ }
                        } catch {}
                    }));
                }

                // 3. Structure (Rules/Events)
                if (includeAll || requested.Contains("structure"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            // issue #25 follow-up (P1): cap each part source so a default
                            // inspect (no `include` filter) can't dump tens of KB of
                            // Rules+Conditions+Events unpaginated. Full source is available
                            // via genexus_read (paginated). Mirrors the maxCallers cap below.
                            int srcCap = verbose ? InspectSourceCap : InspectSourceCapLean;
                            var rules = CapInspectSource(GetPartSourceByName(obj, "Rules"), out bool rulesTrunc, srcCap);
                            lock (result) { result["rules"] = rules; if (rulesTrunc) result["rulesTruncated"] = true; }

                            if (obj is Procedure || obj is WebPanel) {
                                var conditions = CapInspectSource(GetPartSourceByName(obj, "Conditions"), out bool condTrunc, srcCap);
                                lock (result) { result["conditions"] = conditions; if (condTrunc) result["conditionsTruncated"] = true; }
                            }
                            if (obj is Transaction || obj is WebPanel) {
                                var events = CapInspectSource(GetPartSourceByName(obj, "Events"), out bool evTrunc, srcCap);
                                lock (result) { result["events"] = events; if (evTrunc) result["eventsTruncated"] = true; }
                            }
                            lock (result) result["sourceReadHint"] = verbose
                                ? "Inspect source parts are capped at 8000 chars; use genexus_read part=Rules|Conditions|Events (paginated) for the full text."
                                : "Lean inspect: source parts capped at 1200 chars. Pass verbose=true for the 8000-char heads, or genexus_read part=Rules|Conditions|Events for full paginated text.";
                        } catch {}
                    }));
                }

                // 4. Domains & Enums
                if (includeAll || requested.Contains("metadata") || requested.Contains("variables"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var domains = new JArray();
                            var processedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                            if (vPart != null) {
                                foreach (var v in vPart.Variables) {
                                    dynamic dv = v;
                                    var domain = dv.Domain ?? (dv.Attribute != null ? dv.Attribute.Domain : null);
                                    if (domain != null && !processedDomains.Contains(domain.Name)) {
                                        processedDomains.Add(domain.Name);
                                        var dObj = new JObject { ["name"] = domain.Name };
                                        var values = new JArray();
                                        foreach (var ev in ((dynamic)domain).EnumValues) values.Add(new JObject { ["name"] = ev.Name, ["value"] = ev.Value });
                                        dObj["values"] = values;
                                        domains.Add(dObj);
                                    }
                                }
                            }
                            if (domains.Count > 0) lock (result) result["domains"] = domains;
                        } catch {}
                    }));
                }

                // 5. Callers (incoming references) — surfaces top-N callers so the agent can
                // skip a follow-up analyze(mode=impact) / query usedby:* call. Opt-in via
                // include=["callers"] or default (when no include filter is provided).
                if (includeAll || requested.Contains("callers"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var kb = _kbService.GetKB();
                            var callers = new JArray();
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            const int maxCallers = 20;
                            foreach (var reference in obj.GetReferencesTo())
                            {
                                string refKey = null;
                                try { refKey = reference.From?.ToString(); } catch { }
                                if (string.IsNullOrEmpty(refKey) || !seen.Add(refKey)) continue;

                                var sourceObj = kb.DesignModel.Objects.Get(reference.From);
                                if (sourceObj == null) continue;
                                callers.Add(new JObject {
                                    ["name"] = sourceObj.Name,
                                    ["type"] = sourceObj.TypeDescriptor.Name
                                });
                                if (callers.Count >= maxCallers) break;
                            }
                            lock (result) {
                                result["callers"] = callers;
                                result["callersTruncated"] = callers.Count >= maxCallers;
                            }
                        } catch {}
                    }));
                }

                // Wait for all metadata tasks to complete (with timeout for safety)
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));

                // 5. UI Structure (Sync because it's usually fast or has its own internal logic)
                if (_uiService != null && (includeAll || requested.Contains("structure"))) {
                    try { result["uiStructure"] = _uiService.GetSimplifiedUIStructure(obj); } catch {}
                }

                // 5.0.1 SDT items — surfaces the structural items of an SDT so the agent can
                // confirm field names/types/lengths from inspect without an additional
                // genexus_read part=Structure call. Friction-report 2026-05-13 #7.
                if ((includeAll || requested.Contains("structure")) &&
                    obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var sdtItems = BuildSdtItemsJson(obj);
                        if (sdtItems != null) result["sdtStructure"] = sdtItems;
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug("[Analyze] BuildSdtItemsJson failed for " + obj.Name + ": " + ex.Message);
                    }
                }

                // 5.1 Controls + valid events repertoire (opt-in: include=["controls"] or include=["events_repertoire"])
                if (_uiService != null && (requested.Contains("controls") || requested.Contains("events_repertoire"))) {
                    try { result["controls"] = _uiService.GetControlsRepertoire(obj); } catch {}
                }

                // 5.2 Navigation (For Each / Group base tables, indexes, filters) — opt-in only (heavy)
                if (_navigationService != null && requested.Contains("navigation"))
                {
                    try
                    {
                        string navJson = _navigationService.GetNavigation(obj.Name);
                        var navObj = Newtonsoft.Json.Linq.JObject.Parse(navJson);
                        if (navObj["error"] != null)
                            result["navigation"] = new JObject { ["error"] = navObj["error"] };
                        else
                            result["navigation"] = navObj;
                    }
                    catch (Exception ex)
                    {
                        result["navigation"] = new JObject { ["error"] = ex.Message };
                    }
                }

                // 6. Metadata (Sync)
                if (includeAll || requested.Contains("metadata")) result["wwpMetadata"] = GetWWPMetadata(obj);

                // v2.6.8: lifecycle block. Mirrors list_objects projection so the
                // agent can answer "when was this last touched / by whom" from a
                // single inspect call. Defensive reads — partially-loaded objects
                // can throw on KBObject accessors.
                if (includeAll || requested.Contains("metadata"))
                {
                    try
                    {
                        var life = new JObject();
                        DateTime lu = default, ca = default;
                        string lub = null;
                        try { lu = obj.LastUpdate; } catch { }
                        try { ca = obj.VersionDate; } catch { }
                        try { lub = obj.UserName; } catch { }
                        if (lu > DateTime.MinValue) life["lastUpdate"] = lu.ToUniversalTime().ToString("o");
                        if (ca > DateTime.MinValue) life["createdAt"] = ca.ToUniversalTime().ToString("o");
                        if (!string.IsNullOrEmpty(lub)) life["lastModifiedBy"] = lub;
                        if (life.Count > 0) result["lifecycle"] = life;
                    }
                    catch (Exception ex) { Logger.Debug("[Inspect] lifecycle block failed: " + ex.Message); }
                }

                // 6.0 Transaction-specific metadata — surfaces IsBusinessComponent so the agent
                // can verify BC state without paging through the full property bag.
                if ((includeAll || requested.Contains("metadata")) && obj is global::Artech.Genexus.Common.Objects.Transaction trnMeta)
                {
                    try
                    {
                        var trnMd = new JObject { ["isBusinessComponent"] = trnMeta.IsBusinessComponent };
                        result["transactionMetadata"] = trnMd;
                    }
                    catch (Exception ex) { Logger.Debug("[Inspect] Transaction metadata extraction failed: " + ex.Message); }
                }

                // 6.1 Available parts (Sync) — discovery aid
                if (includeAll || requested.Contains("parts"))
                {
                    try
                    {
                        var partsArr = new JArray();
                        var available = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                        foreach (var p in available) partsArr.Add(p);
                        result["availableParts"] = partsArr;
                    }
                    catch { }
                }

                // 6.2 Runtime IDs (opt-in: include=["runtimeIds"]).
                // Parses the generated .cs file from GXSPC*/GEN*/web/ to map
                // design-time control IDs to their runtime HTML element IDs.
                // Requires a prior build; returns an empty array when no
                // generated file is found.
                if (requested.Contains("runtimeids"))
                {
                    try
                    {
                        string kbPath = null;
                        try { kbPath = _kbService?.GetKbPath(); } catch { }
                        var runtimeIds = GetRuntimeIds(kbPath, obj.Name);
                        result["runtimeIds"] = runtimeIds;
                        if (kbPath == null || runtimeIds.Count == 0)
                        {
                            result["runtimeIdsNote"] = "runtimeIds requires a prior build. No generated .cs file was found — run genexus_lifecycle action=build first.";
                        }
                    }
                    catch { result["runtimeIds"] = new JArray(); }
                }

                // 7. Final Summary
                result["summary"] = GenerateSummary(obj, result);

                string json = result.ToString();
                // v2.6.9 perf: populate inspect cache. Bounded by 30 s TTL +
                // WriteService.WasTargetWrittenSince invalidation; safe to skip
                // SDK reads on the next hit.
                SweepExpired(_inspectCache, InspectCacheTtl);
                _inspectCache[inspectKey] = new InspectCacheEntry
                {
                    Json = json,
                    FilledAtUtc = System.DateTime.UtcNow
                };
                // Phase 2: attach memory to a copy returned to the caller — the
                // cached entry above stays memory-free so it's reused as-is.
                return AttachRelevantMemory(json, obj.Name, obj.TypeDescriptor.Name);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "InspectFailed", message: ex.Message, hint: "Ensure the KB is open and the target object exists.");
            }
        }

        private string GenerateSummary(KBObject obj, JObject fullResult)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"{obj.TypeDescriptor.Name} {obj.Name}: {obj.Description}. ");
                
                var parms = fullResult["parameters"] as JArray;
                if (parms != null && parms.Count > 0)
                    sb.Append($"Accepts {parms.Count} parameters. ");
                
                var vars = fullResult["variables"] as JArray;
                if (vars != null && vars.Count > 0)
                    sb.Append($"Uses {vars.Count} local variables. ");

                if (fullResult["uiStructure"] != null && fullResult["uiStructure"].Type != JTokenType.Null)
                    sb.Append("Has a user interface. ");

                if (fullResult["wwpMetadata"] != null && fullResult["wwpMetadata"].Type != JTokenType.Null)
                    sb.Append("Uses WorkWithPlus patterns. ");

                return sb.ToString().Trim();
            }
            catch { return $"{obj.TypeDescriptor.Name} {obj.Name}"; }
        }

        private JObject GetWWPMetadata(KBObject obj)
        {
            var wwp = new JObject();
            try {
                dynamic dObj = obj;
                if (dObj.Properties != null) {
                    foreach (dynamic prop in dObj.Properties) {
                        string name = prop.Name;
                        if (name == "Pattern") wwp["pattern"] = prop.Value?.ToString();
                        else if (name == "MasterPage") wwp["masterPage"] = prop.Value?.ToString();
                    }
                }

                bool isWWP = false;
                try
                {
                    if (obj.Parts.Cast<KBObjectPart>().Any(p =>
                        (p.TypeDescriptor?.Name ?? string.Empty).IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        isWWP = true;
                    }
                }
                catch { }
                if (!isWWP && !string.IsNullOrEmpty(obj.Description) &&
                    (obj.Description.IndexOf("WorkWithPlus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     obj.Description.IndexOf("WWP", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    isWWP = true;
                }
                wwp["isWorkWithPlusAware"] = isWWP;
            } catch {}
            return wwp;
        }

        private string GetPartSourceByName(KBObject obj, string name)
        {
            try {
                var part = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.TypeDescriptor.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (part is ISource source) return source.Source;
            } catch {}
            return null;
        }

        // issue #25 follow-up (P1): per-part source cap for genexus_inspect so a default
        // (no `include`) inspect stays token-bounded. Head-only slice (a truncated head is
        // enough for orientation; genexus_read gives the full paginated text).
        private const int InspectSourceCap = 8000;
        // Lean default (no verbose): a short head is enough to orient; full text via genexus_read.
        private const int InspectSourceCapLean = 1200;
        private static string CapInspectSource(string src, out bool truncated, int cap = InspectSourceCap)
        {
            truncated = false;
            if (string.IsNullOrEmpty(src) || src.Length <= cap) return src;
            truncated = true;
            return src.Substring(0, cap) + "\n\n// ... [inspect source truncated — use genexus_read for the full part] ...";
        }

        /// <summary>
        /// SOTA 360-degree task context: returns the complete target object +
        /// signatures of all called procedures/panels + schemas and PKs of referenced tables +
        /// structures of referenced SDTs + top callers in a single roundtrip.
        /// </summary>
        public string Get360Context(string target, string typeFilter = null,
            string guid = null, string entityKey = null, string path = null)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter, guid, entityKey, path);
                if (obj == null) return FormatInspectNotFound(target);

                // 1. Read full object
                string fullObjJson = _objectService.ReadFullObject(target, typeFilter, guid, entityKey, path);
                JObject fullObjResult = null;
                try
                {
                    var parsed = JObject.Parse(fullObjJson);
                    fullObjResult = (parsed["result"] as JObject) ?? parsed;
                }
                catch
                {
                    fullObjResult = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor?.Name };
                }

                var result = new JObject
                {
                    ["object"] = fullObjResult
                };

                // 2. Discover called procedures / webpanels and their parm signatures
                var calledSignatures = new JArray();
                var referencedTables = new JArray();
                var referencedSDTs = new JArray();
                var callers = new JArray();

                var seenCalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenSdts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                dynamic kb = _kbService?.GetKB();

                // 2a. Outgoing references from SDK
                if (kb != null)
                {
                    try
                    {
                        foreach (var reference in obj.GetReferences())
                        {
                            try
                            {
                                var refObj = kb.DesignModel.Objects.Get(reference.To);
                                if (refObj == null) continue;

                                string rName = refObj.Name;
                                string rType = refObj.TypeDescriptor?.Name;

                                if (rType == "Procedure" || rType == "WebPanel" || rType == "DataProvider" || rType == "WebComponent")
                                {
                                    if (seenCalled.Add(rName) && !rName.Equals(obj.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        var calleeObj = _objectService.FindObject(rName, rType) ?? (KBObject)refObj;
                                        var pResult = _objectService.GetParametersInternal(calleeObj);
                                        var cObj = new JObject
                                        {
                                            ["name"] = rName,
                                            ["type"] = rType
                                        };
                                        if (!string.IsNullOrEmpty(pResult.parmRule)) cObj["parmRule"] = pResult.parmRule;
                                        calledSignatures.Add(cObj);
                                    }
                                }
                                else if (rType == "Table" || rType == "Transaction")
                                {
                                    string tblName = rName;
                                    if (seenTables.Add(tblName))
                                    {
                                        var tObj = _objectService.FindObject(tblName) as Table;
                                        if (tObj == null && refObj is Transaction trn)
                                        {
                                            tObj = _objectService.FindObject(trn.Name, "Table") as Table;
                                        }

                                        if (tObj != null)
                                        {
                                            var tblStruct = GetTableStructureCompact(tObj);
                                            if (tblStruct != null) referencedTables.Add(tblStruct);
                                        }
                                    }
                                }
                                else if (rType == "SDT")
                                {
                                    if (seenSdts.Add(rName))
                                    {
                                        var sdtStruct = GetSdtStructureCompact(refObj);
                                        if (sdtStruct != null) referencedSDTs.Add(sdtStruct);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // 2b. Also scan variables for SDT references that might not be in Direct References
                var vars = fullObjResult["variables"] as JArray;
                if (vars != null)
                {
                    foreach (var v in vars)
                    {
                        string sdtName = v["sdt"]?.ToString();
                        if (!string.IsNullOrEmpty(sdtName) && seenSdts.Add(sdtName))
                        {
                            var sdtObj = _objectService.FindObject(sdtName, "SDT");
                            if (sdtObj != null)
                            {
                                var sdtStruct = GetSdtStructureCompact(sdtObj);
                                if (sdtStruct != null) referencedSDTs.Add(sdtStruct);
                            }
                        }
                    }
                }

                // 2c. Discover incoming callers (top 10)
                if (kb != null)
                {
                    try
                    {
                        var seenCallerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var reference in obj.GetReferencesTo())
                        {
                            try
                            {
                                var sourceObj = kb.DesignModel.Objects.Get(reference.From);
                                if (sourceObj != null && seenCallerNames.Add(sourceObj.Name))
                                {
                                    callers.Add(new JObject
                                    {
                                        ["name"] = sourceObj.Name,
                                        ["type"] = sourceObj.TypeDescriptor?.Name
                                    });
                                    if (callers.Count >= 10) break;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                result["calledSignatures"] = calledSignatures;
                result["referencedTables"] = referencedTables;
                result["referencedSDTs"] = referencedSDTs;
                result["callers"] = callers;

                return Models.McpResponse.Ok(target: obj.Name, code: "360ContextRead", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "Context360Failed",
                    message: ex.Message,
                    hint: "Check that the KB is open and the object is accessible.",
                    target: target);
            }
        }

        private JObject GetTableStructureCompact(Table tbl)
        {
            try
            {
                var res = new JObject
                {
                    ["name"] = tbl.Name,
                    ["description"] = tbl.Description
                };

                var pkArr = new JArray();
                var colsArr = new JArray();

                foreach (var attr in tbl.TableStructure.Attributes)
                {
                    if (attr.IsKey) pkArr.Add(attr.Name);
                    var col = new JObject
                    {
                        ["name"] = attr.Name,
                        ["type"] = attr.Attribute?.Type.ToString()
                    };
                    if (attr.IsKey) col["isKey"] = true;
                    colsArr.Add(col);
                }

                res["primaryKey"] = pkArr;
                res["columns"] = colsArr;
                return res;
            }
            catch { return null; }
        }

        private JObject GetSdtStructureCompact(KBObject sdtObj)
        {
            try
            {
                var res = new JObject
                {
                    ["name"] = sdtObj.Name
                };
                dynamic sdt = sdtObj;
                try { res["isCollection"] = (bool)sdt.IsCollection; } catch { }
                try
                {
                    string dsl = StructureParser.SerializeToText(sdtObj);
                    if (!string.IsNullOrWhiteSpace(dsl)) res["structure"] = dsl;
                }
                catch { }
                return res;
            }
            catch { return null; }
        }

        public string GetSignature(string name, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["parmRule"] = parmRule;
                
                var parmArray = new JArray();
                foreach (var p in parms) {
                    parmArray.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                }
                result["parameters"] = parmArray;
                
                return result.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "GetSignatureFailed", message: ex.Message, hint: "Ensure the KB is open and the target object exists.", target: name);
            }
        }

        private string TryGetDataProviderReturnsSdt(Artech.Genexus.Common.Objects.DataProvider dp)
        {
            // 1) Look for output() rule
            try
            {
                string rules = ((dynamic)dp).Rules?.Source as string ?? "";
                if (!string.IsNullOrEmpty(rules))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(rules,
                        @"\boutput\s*\(\s*&?(\w+)\s*\)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string varName = m.Groups[1].Value;
                        string sdtFromVar = TryResolveSdtFromVariable(dp, varName);
                        if (!string.IsNullOrEmpty(sdtFromVar)) return sdtFromVar;
                        return varName;
                    }
                }
            }
            catch { }

            // 2) Try direct properties exposed by the SDK
            foreach (string prop in new[] { "OutputType", "ReturnType", "ReturnTypeName" })
            {
                try
                {
                    var pi = dp.GetType().GetProperty(prop);
                    if (pi == null) continue;
                    var v = pi.GetValue(dp);
                    if (v != null)
                    {
                        string s = v.ToString();
                        if (!string.IsNullOrEmpty(s) && !s.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return s;
                    }
                }
                catch { }
            }

            return null;
        }

        private string TryResolveSdtFromVariable(KBObject obj, string variableName)
        {
            if (string.IsNullOrEmpty(variableName)) return null;
            try
            {
                dynamic vPart = obj.Parts.Cast<KBObjectPart>()
                    .FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart", StringComparison.OrdinalIgnoreCase));
                if (vPart == null) return null;
                foreach (var v in vPart.Variables)
                {
                    try
                    {
                        string name = (string)((dynamic)v).Name;
                        if (!string.Equals(name, variableName, StringComparison.OrdinalIgnoreCase)) continue;

                        string sdtName = null;
                        try { sdtName = ((dynamic)v).PromptInformation?.SDTName as string; } catch { }
                        if (!string.IsNullOrEmpty(sdtName)) return sdtName;

                        string baseType = ((dynamic)v).Type?.ToString();
                        if (!string.IsNullOrEmpty(baseType) && baseType.IndexOf("SDT", StringComparison.OrdinalIgnoreCase) >= 0)
                            return baseType;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private List<string> ResolveDataProviderTablesRead(Artech.Genexus.Common.Objects.DataProvider dp)
        {
            var tables = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) Use the resolved navigation report if available
            if (_navigationService != null)
            {
                try
                {
                    string navJson = _navigationService.GetNavigation(dp.Name);
                    var nav = JObject.Parse(navJson);
                    if (nav["error"] == null && nav["levels"] is JArray levels)
                    {
                        foreach (var l in levels)
                        {
                            string t = (string)l["baseTable"];
                            if (!string.IsNullOrWhiteSpace(t) && seen.Add(t)) tables.Add(t);
                        }
                    }
                }
                catch { }
            }

            // 2) Fallback: enriched index entry's Tables list (filter to actual Tables only)
            if (tables.Count == 0)
            {
                try
                {
                    var index = _indexCacheService.GetIndex();
                    if (index.Objects.TryGetValue($"DataProvider:{dp.Name}", out var entry) && entry?.Tables != null)
                    {
                        foreach (var name in entry.Tables)
                        {
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (index.Objects.TryGetValue($"Table:{name}", out _) && seen.Add(name))
                                tables.Add(name);
                        }
                    }
                }
                catch { }
            }

            return tables;
        }

        // ----------------------------------------------------------------
        // FR#18 (Stream G, v2.6.6) — analyze mode=parent_context.
        // Walks IndexCacheService.GetIndex().Objects[target].CalledBy and
        // classifies each caller by inspecting its Source / Events /
        // Conditions parts for popup vs link invocations.
        //
        // Returns shape:
        //   { openedAs, popupCallers[], standaloneCallers[], hint }
        //
        // A sourceResolver seam lets unit tests inject synthetic source
        // without standing up a KB; default delegates to ObjectService.
        // ----------------------------------------------------------------
        public string ParentContext(string target)
        {
            return ParentContext(target, null);
        }

        internal string ParentContext(string target, Func<string, string, string> sourceResolver)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target))
                    return Models.McpResponse.Err(code: "TargetRequired", message: "target is required", hint: "Provide a non-empty target name.");

                var index = _indexCacheService?.GetIndex();
                if (index == null || index.Objects == null)
                    return Models.McpResponse.Err(code: "SearchIndexMissing", message: "Index not found.", hint: "Run the KB indexing flow before requesting parent_context analysis.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index" }, "Builds the SearchIndex required for parent_context analysis.")), retryAfterMs: 10000, target: target);

                // Resolve the target entry (4-stage same as ImpactAnalysis).
                var entry = ResolveIndexEntry(index, target, out var _);
                if (entry == null)
                {
                    return new JObject
                    {
                        ["openedAs"] = "unknown",
                        ["popupCallers"] = new JArray(),
                        ["standaloneCallers"] = new JArray(),
                        ["hint"] = HintForOpenedAs("unknown"),
                        ["note"] = "Target '" + target + "' not in index. Re-run after `genexus_lifecycle action=index` completes."
                    }.ToString();
                }
                string canonicalName = entry.Name ?? target;
                var callers = entry.CalledBy ?? new List<string>();

                // Default resolver: best-effort over ObjectService source parts.
                if (sourceResolver == null)
                {
                    sourceResolver = (callerName, partName) =>
                    {
                        try
                        {
                            if (_objectService == null) return null;
                            return _objectService.ReadObjectSource(callerName, partName);
                        }
                        catch { return null; }
                    };
                }

                // Regex set — built per target so we can use the canonical name.
                string esc = System.Text.RegularExpressions.Regex.Escape(canonicalName);
                var rePopupDot = new System.Text.RegularExpressions.Regex(
                    "\\b" + esc + "\\.PopUp\\s*\\(",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var rePopupCtx = new System.Text.RegularExpressions.Regex(
                    "\\b(?:context|gx|gxContext)\\.PopUp\\s*\\(\\s*[\"']?" + esc,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var reLink = new System.Text.RegularExpressions.Regex(
                    "\\b" + esc + "\\.Link\\s*\\(",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var reNew = new System.Text.RegularExpressions.Regex(
                    "&\\w+\\s*=\\s*new\\s+" + esc + "\\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var popupCallers = new List<string>();
                var standaloneCallers = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var caller in callers)
                {
                    if (string.IsNullOrWhiteSpace(caller) || !seen.Add(caller)) continue;

                    var sb = new System.Text.StringBuilder();
                    foreach (var partName in new[] { "Source", "Events", "Conditions" })
                    {
                        string src = null;
                        try { src = sourceResolver(caller, partName); } catch { }
                        if (!string.IsNullOrEmpty(src)) sb.Append(src).Append('\n');
                    }
                    string combined = sb.ToString();
                    if (string.IsNullOrEmpty(combined)) continue;

                    bool isPopup = rePopupDot.IsMatch(combined) || rePopupCtx.IsMatch(combined);
                    if (isPopup)
                    {
                        popupCallers.Add(caller);
                        continue;
                    }
                    bool isStandalone = reLink.IsMatch(combined) || reNew.IsMatch(combined);
                    if (isStandalone) standaloneCallers.Add(caller);
                    // unmatched → silently ignored per spec
                }

                string openedAs;
                if (popupCallers.Count > 0 && standaloneCallers.Count > 0) openedAs = "both";
                else if (popupCallers.Count > 0) openedAs = "popup";
                else if (standaloneCallers.Count > 0) openedAs = "standalone";
                else openedAs = "unknown";

                var pArr = new JArray(); foreach (var c in popupCallers) pArr.Add(c);
                var sArr = new JArray(); foreach (var c in standaloneCallers) sArr.Add(c);

                return new JObject
                {
                    ["target"] = canonicalName,
                    ["openedAs"] = openedAs,
                    ["popupCallers"] = pArr,
                    ["standaloneCallers"] = sArr,
                    ["hint"] = HintForOpenedAs(openedAs)
                }.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "ParentContextFailed", message: ex.Message, hint: "Ensure the KB is open and the search index is ready.", target: target);
            }
        }

        /// <summary>
        /// FR#18 hint table. Public so PopupTemplateService can echo the
        /// popup-branch hint in its create-popup response envelope.
        /// </summary>
        public static string HintForOpenedAs(string openedAs)
        {
            switch ((openedAs ?? "").ToLowerInvariant())
            {
                case "popup":
                    return "Opened as popup. Do NOT use Link() in Enter event handlers — it loops because the popup is already a Link() target. Use Cancel.OnClick = Hide() and ReturnTo() to return values. Forms inside the popup should commit via the popup's own confirmation button.";
                case "standalone":
                    return "Opened as standalone (Link). Standard form-submit + Link() patterns apply. ReturnTo() in this context will return to the previous page rather than a popup parent.";
                case "both":
                    return "Called from both popup and standalone sites. Use IsPopUp() at runtime to branch behavior; treat Cancel.OnClick and Enter event flows defensively.";
                default:
                    return "No callers discovered (callers may exist outside the indexed set, or the object is a launcher). Re-run after `genexus_lifecycle action=index` completes.";
            }
        }

        // ----------------------------------------------------------------
        // mode=cross_platform_impact — Wave-3 SOTA add-on.
        // Buckets target's callers by surface (Web vs SmartDevices) and runs
        // divergence detectors so the agent can predict which platform breaks
        // when a Transaction/SDT/Domain changes shape. See
        // CrossPlatformImpactAnalyzer for the heuristic catalog.
        // ----------------------------------------------------------------
        public string CrossPlatformImpact(string target)
        {
            return CrossPlatformImpact(target, null);
        }

        internal string CrossPlatformImpact(string target, Func<string, string, string> sourceResolver)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target))
                    return Models.McpResponse.Err(code: "TargetRequired", message: "target is required", hint: "Provide a non-empty target name.");

                var index = _indexCacheService?.GetIndex();
                if (index == null || index.Objects == null)
                    return Models.McpResponse.Err(code: "SearchIndexMissing", message: "Index not found.", hint: "Run the KB indexing flow before requesting cross_platform_impact analysis.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index" }, "Builds the SearchIndex required for cross_platform_impact.")), retryAfterMs: 10000, target: target);

                var entry = ResolveIndexEntry(index, target, out var _);
                if (entry == null)
                    return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found in index.", hint: "The object was not found in the search index. Re-run after genexus_lifecycle action=index completes.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index", ["force"] = true }, "Forces a full KB rescan so newly added objects are discoverable.")), target: target);

                string canonicalName = entry.Name ?? target;
                string canonicalType = entry.Type ?? "Object";

                // Pull the target's Rules part text so the surface-gated detector
                // has something to look at. Best-effort: skip silently when the
                // SDK isn't reachable (unit tests pass sourceResolver=null).
                string rulesSource = null;
                if (sourceResolver != null)
                {
                    try { rulesSource = sourceResolver(canonicalName, "Rules"); } catch { }
                }
                else if (_objectService != null)
                {
                    try { rulesSource = _objectService.ReadObjectSource(canonicalName, "Rules"); } catch { }
                    if (!string.IsNullOrEmpty(rulesSource))
                    {
                        var trimmed = rulesSource.TrimStart();
                        // Skip ReadObjectSource error envelopes — see FindCallerSites for
                        // the same defensive pattern.
                        if (trimmed.StartsWith("{") && (
                            trimmed.IndexOf("\"status\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            trimmed.IndexOf("\"Error\"", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            rulesSource = null;
                        }
                    }
                }

                // Default caller-source resolver: route to ObjectService.ReadObjectSource.
                Func<string, string, string> resolverForCallers = sourceResolver;
                if (resolverForCallers == null && _objectService != null)
                {
                    resolverForCallers = (n, p) =>
                    {
                        try { return _objectService.ReadObjectSource(n, p); }
                        catch { return null; }
                    };
                }

                var result = CrossPlatformImpactAnalyzer.Analyze(
                    canonicalName, canonicalType, index, rulesSource, resolverForCallers);

                var webArr = new JArray(); foreach (var c in result.WebCallers) webArr.Add(c);
                var sdArr = new JArray(); foreach (var c in result.SdCallers) sdArr.Add(c);
                var divArr = new JArray(); foreach (var d in result.Divergence) divArr.Add(d);
                var detectorsRun = new JArray(); foreach (var d in result.DetectorsRun) detectorsRun.Add(d);
                var detectorsPending = new JArray(); foreach (var d in result.DetectorsPending) detectorsPending.Add(d);

                string confidence;
                if (resolverForCallers == null) confidence = "low";
                else if (rulesSource == null && (result.WebCallers.Count == 0 || result.SdCallers.Count == 0)) confidence = "low";
                else if (rulesSource == null) confidence = "medium";
                else confidence = "medium";

                return McpResponse.Ok(
                    target: canonicalName,
                    code: "CrossPlatformImpactCompleted",
                    result: new JObject
                    {
                        ["target"] = new JObject { ["name"] = canonicalName, ["type"] = canonicalType },
                        ["platforms"] = new JObject
                        {
                            ["Web"] = new JObject { ["callers"] = webArr, ["count"] = result.WebCallers.Count, ["divergenceSignals"] = new JArray() },
                            ["SmartDevices"] = new JObject { ["callers"] = sdArr, ["count"] = result.SdCallers.Count, ["divergenceSignals"] = new JArray() }
                        },
                        ["crossPlatformDivergence"] = divArr,
                        ["summary"] = new JObject
                        {
                            ["webCallers"] = result.WebCallers.Count,
                            ["sdCallers"] = result.SdCallers.Count,
                            ["divergencePoints"] = result.Divergence.Count
                        },
                        ["_meta"] = new JObject
                        {
                            ["confidence"] = confidence,
                            ["detectorsRun"] = detectorsRun,
                            ["detectorsPending"] = detectorsPending,
                            ["rulesSourceAvailable"] = !string.IsNullOrEmpty(rulesSource),
                            ["note"] = "Heuristic platform classification based on caller object types + transitive caller walk. Procedures are bucketed via depth-3 caller traversal."
                        }
                    });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "CrossPlatformImpactFailed", message: ex.Message, hint: "Ensure the KB is open and the search index is ready.", target: target);
            }
        }

        public string ExplainCode(string target, string codeSnippet)
        {
            // Keep the legacy explain route as an explicit compatibility
            // envelope. It is intentionally not a fabricated explanation: old
            // clients get a typed NotImplemented result and a supported-mode hint.
            return Models.McpResponse.Err(code: "ModeNotImplemented",
                message: "analyze mode=explain is not implemented.",
                hint: "Use mode=summary, linter, navigation, data_context, or pattern_metadata instead.");
        }

        private static readonly Guid SDT_STRUCTURE_PART_GUID = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        // Walk an SDT's structural items via reflection over the SDK's SDTLevel surface and
        // produce a JSON-friendly tree the agent can inspect without a follow-up read call.
        // Returns null when the SDT structure part can't be located so callers fall back to
        // their existing surfaces; callers also surface the DSL text for human eyes.
        // ----------------------------------------------------------------
        // Runtime IDs — parses generated .cs files from GXSPC*/GEN*/web/
        // to map design-time control IDs to their HTML element IDs.
        // ----------------------------------------------------------------

        internal static JArray GetRuntimeIds(string kbPath, string objectName)
        {
            var arr = new JArray();
            var entries = GxMcp.Worker.Helpers.RuntimeIdParser.ParseFromKbDirectory(kbPath, objectName);
            foreach (var e in entries)
            {
                var item = new JObject();
                item["designId"] = e.DesignId;
                item["htmlId"]   = e.HtmlId;
                if (!string.IsNullOrEmpty(e.Kind))
                    item["kind"] = e.Kind;
                if (e.Hidden.HasValue)
                    item["hidden"] = e.Hidden.Value;
                arr.Add(item);
            }
            return arr;
        }

        private static JObject BuildSdtItemsJson(KBObject sdt)
        {
            if (sdt == null) return null;

            KBObjectPart structure = null;
            foreach (KBObjectPart p in sdt.Parts)
            {
                try
                {
                    string descName = p.TypeDescriptor?.Name ?? "";
                    string className = p.GetType().Name;
                    if (p.Type == SDT_STRUCTURE_PART_GUID ||
                        descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        structure = p;
                        break;
                    }
                }
                catch { }
            }
            if (structure == null) return null;

            dynamic ds = structure;
            dynamic root = null;
            try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }
            if (root == null) return null;

            KBModel model = null;
            try { model = sdt.Model; } catch { }

            var items = new JArray();
            try
            {
                foreach (dynamic child in root.Items)
                {
                    var node = BuildSdtItemNode(child, model);
                    if (node != null) items.Add(node);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[Analyze] SDT item walk failed: " + ex.Message);
            }

            int leafCount = 0, levelCount = 0;
            CountSdtItems(items, ref leafCount, ref levelCount);

            var result = new JObject
            {
                ["itemCount"] = leafCount,
                ["levelCount"] = levelCount
            };

            // issue #47: a top-level collection SDT (the "Collection" checkbox on the SDT root)
            // carries its collection flag on the structure ROOT level, not on the SDT KBObject —
            // sdt.IsCollection reads false there. Surface it (with the collection item name) so
            // callers see the collection root the IDE shows, instead of a flat structure.
            dynamic dsdt = sdt;
            bool rootIsCollection = false;
            try { rootIsCollection = (bool)root.IsCollection; } catch { }
            if (!rootIsCollection) { try { rootIsCollection = (bool)dsdt.IsCollection; } catch { } }
            result["isCollection"] = rootIsCollection;
            if (rootIsCollection)
            {
                string itemName = null;
                try { itemName = (string)root.CollectionItemName; } catch { }
                if (string.IsNullOrEmpty(itemName)) { try { itemName = (string)dsdt.CollectionItemName; } catch { } }
                if (!string.IsNullOrEmpty(itemName)) result["collectionItemName"] = itemName;
            }

            result["items"] = items;
            return result;
        }

        private static void CountSdtItems(JArray items, ref int leafCount, ref int levelCount)
        {
            if (items == null) return;
            foreach (var it in items)
            {
                var children = it["children"] as JArray;
                if (children != null && children.Count > 0)
                {
                    levelCount++;
                    CountSdtItems(children, ref leafCount, ref levelCount);
                }
                else
                {
                    leafCount++;
                }
            }
        }

        private static JObject BuildSdtItemNode(dynamic item, KBModel model = null)
        {
            if (item == null) return null;
            var node = new JObject();
            try { node["name"] = (string)item.Name; } catch { node["name"] = ""; }

            // Detect compound (level) vs leaf. SDK exposes IsLeafItem on most builds; some
            // older surfaces hide it, so fall back to a child probe.
            bool isLeaf;
            try { isLeaf = (bool)item.IsLeafItem; }
            catch
            {
                bool hasChild = false;
                try { foreach (var _ in item.Items) { hasChild = true; break; } } catch { }
                isLeaf = !hasChild;
            }

            try { node["isCollection"] = (bool)item.IsCollection; } catch { node["isCollection"] = false; }

            if (isLeaf)
            {
                string typeStr = null;
                try { typeStr = item.Type != null ? item.Type.ToString() : null; } catch { }
                if (!string.IsNullOrEmpty(typeStr)) node["type"] = typeStr;

                // issue #109: surface basedOnAttribute if member is based on an Attribute.
                try
                {
                    string attrName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)item);
                    if (!string.IsNullOrEmpty(attrName)) node["basedOnAttribute"] = attrName;
                }
                catch { }

                // issue #51: surface basedOnDomain if member is based on a Domain.
                try
                {
                    string domName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetDomainBasedOnName((object)item);
                    if (!string.IsNullOrEmpty(domName)) node["basedOnDomain"] = domName;
                }
                catch { }

                // issue #47: a reference-typed member (GX_SDT / user-defined type) reads back as
                // the raw enum ("GX_SDT"); surface the referenced object's name so callers see the
                // SDT/type the IDE shows instead of an opaque primitive-looking token.
                if (model != null && GxMcp.Worker.Helpers.SdtMemberResolver.IsReferenceType(typeStr))
                {
                    string refName = GxMcp.Worker.Helpers.SdtMemberResolver.ResolveReferencedTypeName((object)item, model);
                    if (!string.IsNullOrEmpty(refName)) node["referencedType"] = refName;
                }

                // Length / Decimals are exposed on most SDTItem surfaces; harvest defensively.
                try { object len = item.Length; if (len != null) node["length"] = Convert.ToInt32(len); } catch { }
                try { object dec = item.Decimals; if (dec != null) node["decimals"] = Convert.ToInt32(dec); } catch { }
                try { object sig = item.Signed; if (sig != null) node["signed"] = Convert.ToBoolean(sig); } catch { }
                node["isLevel"] = false;
            }
            else
            {
                node["isLevel"] = true;
                var children = new JArray();
                try
                {
                    foreach (dynamic c in item.Items)
                    {
                        var sub = BuildSdtItemNode(c, model);
                        if (sub != null) children.Add(sub);
                    }
                }
                catch { }
                node["children"] = children;
            }

            return node;
        }

        // ----------------------------------------------------------------
        // Item 24 — mode=callers: per-call-site detail with line + context.
        // Enumerates every caller from the index and scans their source for
        // actual call sites, returning line number + 3-line surrounding context.
        // ----------------------------------------------------------------
        public string FindCallerSites(string targetName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetName))
                    return Models.McpResponse.Err(code: "TargetRequired", message: "target is required", hint: "Provide a non-empty target name.");

                var index = _indexCacheService?.GetIndex();
                if (index == null || index.Objects == null)
                    return Models.McpResponse.Err(code: "SearchIndexMissing", message: "Index not found.", hint: "Run the KB indexing flow before requesting callers analysis.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index" }, "Builds the SearchIndex required for callers analysis.")), retryAfterMs: 10000, target: targetName);

                var entry = ResolveIndexEntry(index, targetName, out var _);
                if (entry == null)
                    return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found in index.", hint: "The object was not found in the search index. Re-run after genexus_lifecycle action=index completes.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "index", ["force"] = true }, "Forces a full KB rescan so the object becomes discoverable.")), target: targetName);

                string canonicalName = entry.Name ?? targetName;
                var callerNames = entry.CalledBy ?? new List<string>();

                var callers = new JArray();
                const int ctx = 3;

                foreach (var callerName in callerNames)
                {
                    // Read each source part that might contain calls
                    string[] partsToCheck = { "Source", "Events", "Rules" };
                    foreach (var partName in partsToCheck)
                    {
                        string src = null;
                        try
                        {
                            src = _objectService?.ReadObjectSource(callerName, partName);
                            // Skip ReadObjectSource error envelopes — must look like a JSON error
                            // object, NOT any source line that happens to contain the word "error".
                            // Two shapes occur: {"status":"Error",...} and {"error":"..."} (no status).
                            // Match both, case-insensitively, while still rejecting real source that
                            // merely mentions "error" mid-line.
                            if (!string.IsNullOrEmpty(src))
                            {
                                var trimmed = src.TrimStart();
                                if (trimmed.StartsWith("{"))
                                {
                                    bool looksLikeStatusError = trimmed.IndexOf("\"status\"", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 && trimmed.IndexOf("\"Error\"", StringComparison.OrdinalIgnoreCase) >= 0;
                                    // Front-of-payload error envelope: "error" key within the first 64 bytes
                                    // of the JSON object. Real source rarely opens with a JSON object whose
                                    // very first key is "error".
                                    int errorKey = trimmed.IndexOf("\"error\"", StringComparison.OrdinalIgnoreCase);
                                    bool looksLikeBareError = errorKey > 0 && errorKey < 64;
                                    if (looksLikeStatusError || looksLikeBareError)
                                        src = null;
                                }
                            }
                        }
                        catch { }
                        if (string.IsNullOrEmpty(src)) continue;

                        var lines = src.Split('\n');
                        foreach (var call in SourceParser.ParseCalls(src, false))
                        {
                            if (!string.Equals(call.Callee, canonicalName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Also match unqualified suffix
                                int dot = call.Callee?.LastIndexOf('.') ?? -1;
                                string unqualified = dot >= 0 ? call.Callee.Substring(dot + 1) : call.Callee;
                                if (!string.Equals(unqualified, canonicalName, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            int idx = call.LineNumber - 1;
                            string lineText = idx >= 0 && idx < lines.Length ? lines[idx] : "";
                            var ctxBefore = new JArray();
                            for (int i = Math.Max(0, idx - ctx); i < idx; i++) ctxBefore.Add(lines[i]);
                            var ctxAfter = new JArray();
                            for (int i = idx + 1; i < Math.Min(lines.Length, idx + ctx + 1); i++) ctxAfter.Add(lines[i]);

                            callers.Add(new JObject
                            {
                                ["object"] = callerName,
                                ["part"] = partName,
                                ["line"] = call.LineNumber,
                                ["lineText"] = lineText,
                                ["context"] = ctxBefore.ToString(Newtonsoft.Json.Formatting.None) + "\n" + lineText + "\n" + ctxAfter.ToString(Newtonsoft.Json.Formatting.None),
                                ["contextBefore"] = ctxBefore,
                                ["contextAfter"] = ctxAfter,
                                ["args"] = new JArray(call.Args.ToArray<object>())
                            });
                        }
                    }
                }

                // issue #25 follow-up (P0): a zero result here is dangerous — the
                // index holds the object but CalledBy is empty until the object is
                // enriched (lazy mode leaves Status="Ready" with un-enriched edges),
                // so "0 call sites" reads as "safe to delete/rename" when it may just
                // mean "not enriched yet". Never assert an authoritative zero without
                // cross-checking the live SDK reference graph (same source impact uses).
                if (callers.Count == 0)
                {
                    var sdk = TrySdkReferenceCrossCheck(canonicalName);
                    var zeroResult = new JObject
                    {
                        ["callSiteCount"] = 0,
                        ["callers"] = callers,
                        ["indexEdgesMissing"] = true
                    };
                    if (sdk == null)
                    {
                        zeroResult["verifiedZero"] = false;
                        zeroResult["hint"] = "The index holds this object but no caller edges (it may not be enriched yet). This is NOT a confirmed 'no callers' — do NOT treat it as safe to delete/rename. Cross-check with genexus_analyze(mode=impact) or genexus_inspect(include=['callers']).";
                        return McpResponse.Ok(target: canonicalName, code: "CallerSitesUnconfirmed", result: zeroResult);
                    }
                    if (sdk.Callers.Count > 0)
                    {
                        var sdkCallers = new JArray();
                        foreach (var c in sdk.Callers) sdkCallers.Add(c);
                        zeroResult["sdkCallers"] = sdkCallers;
                        zeroResult["verifiedZero"] = false;
                        zeroResult["hint"] = "The index had no caller edges yet, but the live SDK reference graph found callers (listed under sdkCallers). Line-level call sites weren't resolved because the index isn't enriched — re-run shortly, or use genexus_read on the listed callers.";
                        return McpResponse.Ok(target: canonicalName, code: "CallerSitesUnconfirmed", result: zeroResult);
                    }
                    // SDK confirms genuinely zero incoming references.
                    zeroResult["indexEdgesMissing"] = false;
                    zeroResult["verifiedZero"] = true;
                    return McpResponse.Ok(target: canonicalName, code: "CallerSitesFound", result: zeroResult);
                }

                return McpResponse.Ok(
                    target: canonicalName,
                    code: "CallerSitesFound",
                    result: new JObject
                    {
                        ["callSiteCount"] = callers.Count,
                        ["callers"] = callers
                    });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "FindCallerSitesFailed", message: ex.Message, hint: "Ensure the KB is open and the search index is ready.", target: targetName);
            }
        }

        // Wave-3 item 87: rank KB objects by composite heat
        // (editCount + refCount + callerCount). editCount comes from
        // .gx/snapshots/<guid>-<part>-*.bak filename histogram; refCount /
        // callerCount come from the search-index Calls / CalledBy edges.
        // Top 50 by descending score; optional ASCII viz when format=ascii.
        public string DependencyHeatmap(string kbPath, string format)
        {
            try
            {
                var idx = _indexCacheService?.GetIndex();
                if (idx == null || idx.Objects.IsEmpty)
                {
                    return new JObject
                    {
                        ["status"] = "Unwired",
                        ["code"] = "ItemDeferred",
                        ["hint"] = "Index empty/unavailable; run genexus_lifecycle action=index first."
                    }.ToString();
                }

                // Build edit-count histogram from snapshots. Key = guidSanitized (matches
                // KbReadmeService.TopEditedObjects behaviour for parity).
                var editsByGuid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(kbPath))
                {
                    string snapDir = System.IO.Path.Combine(kbPath, ".gx", "snapshots");
                    if (System.IO.Directory.Exists(snapDir))
                    {
                        try
                        {
                            foreach (var path in System.IO.Directory.EnumerateFiles(snapDir))
                            {
                                string fn = System.IO.Path.GetFileName(path);
                                if (!(fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                                      || fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase))) continue;
                                int dash = fn.IndexOf('-');
                                if (dash <= 0) continue;
                                string guidKey = fn.Substring(0, dash);
                                editsByGuid[guidKey] = editsByGuid.TryGetValue(guidKey, out int n) ? n + 1 : 1;
                            }
                        }
                        catch { }
                    }
                }

                var entries = new List<JObject>();
                foreach (var kvp in idx.Objects)
                {
                    var entry = kvp.Value;
                    if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                    int editCount = 0;
                    if (!string.IsNullOrEmpty(entry.Guid))
                    {
                        // Snapshot key is the sanitised GUID — sanitise = replace
                        // '/' '\' ':' '<' '>' with '_'. For our histogram we accept
                        // the raw GUID too (LightWeight check: many KBs have GUIDs
                        // that contain none of the unsafe chars).
                        string raw = entry.Guid;
                        string sanitised = SanitiseGuid(raw);
                        editsByGuid.TryGetValue(sanitised, out editCount);
                        if (editCount == 0) editsByGuid.TryGetValue(raw, out editCount);
                    }
                    int refCount = entry.Calls?.Count ?? 0;
                    int callerCount = entry.CalledBy?.Count ?? 0;
                    // Composite heat: weighted sum. Edits are the strongest signal
                    // (someone actively touches it), then incoming calls, then outgoing.
                    int score = (editCount * 5) + (callerCount * 2) + refCount;
                    if (score <= 0) continue;
                    entries.Add(new JObject
                    {
                        ["name"] = entry.Name,
                        ["type"] = entry.Type,
                        ["score"] = score,
                        ["factors"] = new JObject
                        {
                            ["editCount"] = editCount,
                            ["refCount"] = refCount,
                            ["callerCount"] = callerCount
                        }
                    });
                }
                var topRanked = entries
                    .OrderByDescending(e => e["score"]?.ToObject<int>() ?? 0)
                    .ThenBy(e => e["name"]?.ToString())
                    .Take(50)
                    .ToList();

                var result = new JObject
                {
                    ["objects"] = new JArray(topRanked.Cast<JToken>().ToArray()),
                    ["totalScanned"] = idx.Objects.Count,
                    ["note"] = "score = editCount*5 + callerCount*2 + refCount. Edit counts read from .gx/snapshots/."
                };

                if (string.Equals(format, "ascii", StringComparison.OrdinalIgnoreCase))
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("Dependency heatmap (top ").Append(topRanked.Count).Append(")\n");
                    int maxScore = topRanked.Count > 0 ? topRanked[0]["score"]!.ToObject<int>() : 1;
                    if (maxScore <= 0) maxScore = 1;
                    foreach (var e in topRanked)
                    {
                        int score = e["score"]!.ToObject<int>();
                        int barLen = (int)Math.Round((double)score / maxScore * 24.0);
                        sb.Append(' ').Append(new string('█', Math.Max(1, barLen))).Append(' ')
                          .Append(e["name"]).Append(" (").Append(score).Append(")\n");
                    }
                    result["ascii"] = sb.ToString();
                }

                return McpResponse.Ok(code: "DependencyHeatmapCompleted", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "DependencyHeatmapFailed", message: ex.Message, hint: "Ensure the search index is ready; run genexus_lifecycle action=index first.");
            }
        }

        private static string SanitiseGuid(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (ch == '/' || ch == '\\' || ch == ':' || ch == '<' || ch == '>') sb.Append('_');
                else sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
