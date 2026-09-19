using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class SearchService
    {
        private readonly IndexCacheService _indexCacheService;
        private readonly ObjectService _objectService;
        private readonly VectorService _vectorService = new VectorService();
        private static readonly BoundedStringCache _queryCache = new BoundedStringCache(512);
        private static DateTime _lastIndexTime = DateTime.MinValue;

        // PERFORMANCE (perf-review): these hot-path patterns only vary by a fixed
        // filter name, so precompile once instead of compiling a fresh Regex per
        // query — ParseQuery runs 7 ExtractFilter calls per search and each used
        // to build a new Regex (plus the @quick strip and the bare-quoted name
        // match). RegexOptions.Compiled pays a one-time JIT and then runs native.
        private static readonly Regex QuickSuffixRegex = new Regex(@"\s*@quick\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BareQuotedNameRegex = new Regex("^\"(?<v>[^\"]+)\"$", RegexOptions.Compiled);

        // PERFORMANCE (perf-review): hoisted separator arrays. TryDirectLookup's
        // IndexOfAny and ParseQuery's Split used to allocate a fresh char[] on every
        // search query (these run before the query-cache lookup, so even cached
        // queries paid them).
        private static readonly char[] DirectLookupStopChars = { ' ', ':', '*', '@', '"', '?', '/' };
        private static readonly char[] SpaceSeparator = { ' ' };

        public SearchService(IndexCacheService indexCacheService, ObjectService objectService = null)
        {
            _indexCacheService = indexCacheService;
            _objectService = objectService;
        }

        public string Search(string query, string typeFilter = null, string domainFilter = null, int limit = 50, bool exactMatch = false,
            string sort = null, DateTime since = default(DateTime), DateTime modifiedBefore = default(DateTime), string cursor = null)
        {
            // PERFORMANCE (W-M3): instrument the search hot path so regressions surface in logs.
            // Threshold 50ms keeps noise low while catching any real degradation.
            var sw = Stopwatch.StartNew();
            try
            {
                // Fast-path: a literal name lookup that doesn't need the index. Cold-start
                // indexing on huge KBs takes minutes — agents asking for a known-name object
                // shouldn't block on it. Falls through to the normal index path if the
                // direct SDK lookup misses.
                var directHit = TryDirectLookup(query, typeFilter, exactMatch);
                if (directHit != null) return directHit;

                bool indexMissing = _indexCacheService.IsIndexMissing;
                bool scanning = _indexCacheService.IsScanning;
                if (indexMissing && !scanning)
                {
                    try { _indexCacheService.KbService?.BulkIndex(); } catch { }
                    scanning = _indexCacheService.IsScanning;
                }

                var index = _indexCacheService.GetIndex();
                bool indexEmpty = index == null || index.Objects.Count == 0;

                if (indexEmpty)
                {
                    // Distinguish a genuinely-empty KB (index built — the walk completed and
                    // found no model objects, e.g. a KB whose LocalDB model is missing) from a
                    // build still in progress. The former must return a plain zero-result
                    // response; the latter keeps the "warming" partial + BulkIndex kick below
                    // (which would otherwise loop forever on an empty KB).
                    var st = _indexCacheService.GetState();
                    bool indexBuilt = st != null
                        && (string.Equals(st.Status, "Ready", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(st.Status, "LiteReady", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(st.Status, "Enriching", StringComparison.OrdinalIgnoreCase));
                    if (indexBuilt)
                    {
                        return BuildEmptyKbResponse(limit);
                    }

                    // If we genuinely have nothing yet, return progress info — but DON'T pretend
                    // it's a "zero results" search. _meta.indexStatus = "warming" tells the agent
                    // to retry once indexing progresses, while still reporting the (zero) snapshot.
                    if (scanning)
                    {
                        return BuildPartialResponse(query, new object[0], 0, scanning: true);
                    }
                    try { _indexCacheService.KbService?.BulkIndex(); } catch { }
                    return BuildPartialResponse(query, new object[0], 0, scanning: true);
                }

                if (index.LastUpdated > _lastIndexTime) { _queryCache.Clear(); _lastIndexTime = index.LastUpdated; }

                bool isQuick = !string.IsNullOrEmpty(query) && query.IndexOf("@quick", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isQuick)
                {
                    query = QuickSuffixRegex.Replace(query, "").Trim();
                }

                // v2.6.8: temporal/sort/cursor controls participate in the cache key
                // so callers paging by cursor or filtering by date don't collide with
                // the cached relevance-sorted page.
                string cacheKey = string.Concat(
                    query ?? "", "|", typeFilter ?? "", "|", domainFilter ?? "", "|",
                    limit.ToString(), "|", isQuick ? "quick" : "full", "|", exactMatch ? "exact" : "fuzzy",
                    "|s=", sort ?? "", "|sn=", since.Ticks.ToString(), "|mb=", modifiedBefore.Ticks.ToString(),
                    "|cu=", cursor ?? "");
                if (_queryCache.TryGetValue(cacheKey, out var cached)) return cached;

                var criteria = ParseQuery(query);
                if (!string.IsNullOrEmpty(typeFilter)) criteria.TypeFilter = typeFilter;
                if (!string.IsNullOrEmpty(domainFilter)) criteria.DomainFilter = domainFilter;

                if (exactMatch && criteria.Terms.Count > 0)
                {
                    List<SearchIndex.IndexEntry> exactList = null;
                    if (!string.IsNullOrEmpty(criteria.TypeFilter) && index.Objects != null)
                    {
                        exactList = new List<SearchIndex.IndexEntry>();
                        foreach (var t in criteria.Terms)
                        {
                            string key = criteria.TypeFilter + ":" + t;
                            if (index.Objects.TryGetValue(key, out var directEntry) && directEntry != null)
                            {
                                exactList.Add(directEntry);
                            }
                        }
                    }

                    if (exactList == null || exactList.Count == 0)
                    {
                        var exactCandidates = criteria.Terms
                            .SelectMany(t => index.FindByName(t))
                            .Distinct();
                        if (!string.IsNullOrEmpty(criteria.TypeFilter))
                            exactCandidates = exactCandidates.Where(e => IsTypeMatch(e.Type, criteria.TypeFilter));
                        exactList = exactCandidates.ToList();
                    }
                    var exactResultsArr = new JArray();
                    foreach (var e in exactList)
                    {
                        exactResultsArr.Add(new JObject
                        {
                            ["guid"] = e.Guid,
                            ["name"] = e.Name,
                            ["type"] = e.Type,
                            ["description"] = e.Description,
                            ["parent"] = e.Parent,
                            ["module"] = e.Module,
                            ["path"] = e.Path,
                            ["parentPath"] = e.ParentPath,
                            ["dataType"] = e.DataType,
                            ["table"] = e.RootTable
                        });
                    }
                    var exactObj = new JObject
                    {
                        ["count"] = exactList.Count,
                        ["total"] = exactList.Count,
                        ["hasMore"] = false,
                        ["results"] = exactResultsArr
                    };
                    // v2.8.0: canonical pagination block
                    exactObj["pagination"] = new JObject
                    {
                        ["offset"]     = 0,
                        ["limit"]      = exactList.Count,
                        ["returned"]   = exactList.Count,
                        ["total"]      = exactList.Count,
                        ["hasMore"]    = false,
                        ["nextOffset"] = JValue.CreateNull()
                    };
                    if (exactList.Count > 0)
                    {
                        var top = exactList[0];
                        exactObj["_meta"] = new JObject
                        {
                            ["suggested_next"] = new JObject
                            {
                                ["tool"] = "genexus_read",
                                ["args"] = new JObject { ["name"] = top.Name, ["type"] = top.Type }
                            }
                        };
                    }
                    var exactJson = exactObj.ToString(Newtonsoft.Json.Formatting.None);
                    _queryCache.TryAdd(cacheKey, exactJson);
                    return exactJson;
                }

                IEnumerable<SearchIndex.IndexEntry> sourceSet = null;
                // Plan 002: true only when sourceSet ends up as the unfiltered
                // index.Objects.Values scan (no parent/parentPath filter already narrowed it).
                bool sourceIsFullScan = false;

                if (criteria.ParentPathFilter != null && index.ChildrenByParent != null)
                {
                    if (index.ChildrenByParent.TryGetValue(criteria.ParentPathFilter, out var children))
                    {
                        sourceSet = children;
                    }
                    else
                    {
                        sourceSet = Enumerable.Empty<SearchIndex.IndexEntry>();
                    }
                }
                else if (!string.IsNullOrEmpty(criteria.ParentFilter) && index.ChildrenByParent != null)
                {
                    if (index.ChildrenByParent.TryGetValue(criteria.ParentFilter, out var children))
                    {
                        sourceSet = children;
                    }
                    else
                    {
                        sourceSet = Enumerable.Empty<SearchIndex.IndexEntry>();
                    }
                }
                else
                {
                    sourceSet = index.Objects.Values;
                    sourceIsFullScan = true;
                }

                // PERFORMANCE: When no parent/path filter narrowed sourceSet and criteria.NameFilter is specified,
                // resolve candidate entries directly through index.FindByName instead of scanning all objects.
                if (sourceIsFullScan && !string.IsNullOrEmpty(criteria.NameFilter))
                {
                    sourceSet = index.FindByName(criteria.NameFilter);
                    sourceIsFullScan = false;
                }

                // Plan 002: when no parent/parentPath filter already narrowed sourceSet,
                // intersect the derived TypeIndex/DomainIndex buckets instead of scanning
                // every object. IsTypeMatch is alias-aware ("prc" contains-matches
                // "Procedure"), so resolve the filter to the concrete Type bucket(s) that
                // actually match before touching Objects; DomainFilter is an exact
                // case-insensitive match so its bucket key applies directly. The existing
                // TypeFilter/DomainFilter .Where() below still runs afterwards (cheap safety
                // net) — this only shrinks the candidate set that reaches it. Falls back to
                // the unchanged full scan when TypeIndex/DomainIndex haven't been built yet
                // (e.g. the LoadFromEntries test seam).
                if (sourceIsFullScan && index.TypeIndex != null && index.DomainIndex != null
                    && (!string.IsNullOrEmpty(criteria.TypeFilter) || !string.IsNullOrEmpty(criteria.DomainFilter)))
                {
                    HashSet<string> candidateKeys = null;

                    if (!string.IsNullOrEmpty(criteria.TypeFilter))
                    {
                        candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var typeKey in index.TypeIndex.Keys)
                        {
                            if (!IsTypeMatch(typeKey, criteria.TypeFilter)) continue;
                            if (index.TypeIndex.TryGetValue(typeKey, out var typeKeys))
                            {
                                lock (typeKeys) { candidateKeys.UnionWith(typeKeys); }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(criteria.DomainFilter))
                    {
                        HashSet<string> domainKeys;
                        if (index.DomainIndex.TryGetValue(criteria.DomainFilter, out var domainBucket))
                        {
                            lock (domainBucket) { domainKeys = new HashSet<string>(domainBucket, StringComparer.OrdinalIgnoreCase); }
                        }
                        else
                        {
                            domainKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }
                        candidateKeys = candidateKeys == null
                            ? domainKeys
                            : new HashSet<string>(candidateKeys.Where(domainKeys.Contains), StringComparer.OrdinalIgnoreCase);
                    }

                    if (candidateKeys != null)
                    {
                        sourceSet = candidateKeys
                            .Select(k => { index.Objects.TryGetValue(k, out var e); return e; })
                            .Where(e => e != null)
                            .ToList();
                    }
                }

                // PERFORMANCE (W-M4): cap PLINQ parallelism so large KBs (50k+ objects) on
                // 16+ core machines don't spawn one task per core and pressure the GC.
                int dop = Math.Min(4, Math.Max(1, Environment.ProcessorCount));
                // PERFORMANCE (perf-review): PLINQ's partition/merge overhead dominates for
                // small candidate sets — a TypeIndex/DomainIndex-filtered query often lands
                // well under a few thousand candidates. Stay on sequential LINQ below the
                // threshold and only parallelize genuinely large scans.
                //
                // Calibrated by SearchRankParallelismBenchmark (DOP=4, faithful ranker work
                // incl. 128-dim cosine, dev hardware): PLINQ is 1.17-1.46x SLOWER for
                // 64..2048 candidates (allocating up to 2.2x more), breaking even only
                // around 4096. Threshold 2048 keeps the common filtered range sequential
                // and only engages PLINQ where it is at worst neutral.
                const int ParallelScanThreshold = 2048;
                bool parallelize = sourceSet is not ICollection<SearchIndex.IndexEntry> sourceCol
                                   || sourceCol.Count >= ParallelScanThreshold;
                IEnumerable<SearchIndex.IndexEntry> queryResults = parallelize
                    ? sourceSet.AsParallel().WithDegreeOfParallelism(dop)
                    : sourceSet;

                // name:"X" demands exact-name match. Hard filter so the ranker never
                // sees substring / vector candidates — those were poisoning results
                // when an agent passed a long unique identifier.
                if (!string.IsNullOrEmpty(criteria.NameFilter))
                    queryResults = queryResults.Where(e => string.Equals(e.Name, criteria.NameFilter, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(criteria.TypeFilter))
                    queryResults = queryResults.Where(e => IsTypeMatch(e.Type, criteria.TypeFilter));
                
                if (!string.IsNullOrEmpty(criteria.DomainFilter))
                    queryResults = queryResults.Where(e => string.Equals(e.BusinessDomain, criteria.DomainFilter, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(criteria.DescriptionFilter))
                    queryResults = queryResults.Where(IndexEntryFilterBuilder.DescriptionContains(criteria.DescriptionFilter));

                if (!string.IsNullOrEmpty(criteria.MetadataFilter))
                    queryResults = queryResults.Where(e => 
                        (e.ParmRule ?? "").IndexOf(criteria.MetadataFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (e.DataType ?? "").IndexOf(criteria.MetadataFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (e.RootTable ?? "").IndexOf(criteria.MetadataFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    );

                if (criteria.ParentPathFilter != null && (sourceSet == index.Objects.Values))
                    queryResults = queryResults.Where(e => string.Equals(e.ParentPath ?? string.Empty, criteria.ParentPathFilter, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(criteria.ParentFilter) && (sourceSet == index.Objects.Values))
                    queryResults = queryResults.Where(e => string.Equals(e.Parent, criteria.ParentFilter, StringComparison.OrdinalIgnoreCase));

                // v2.6.8: temporal bounds. Same semantics as list_objects — Since
                // inclusive, ModifiedBefore exclusive. Applied before ranking so
                // the score budget doesn't get spent on out-of-window items.
                if (since > DateTime.MinValue)
                    queryResults = queryResults.Where(IndexEntryFilterBuilder.SinceInclusive(since));
                if (modifiedBefore > DateTime.MinValue)
                    queryResults = queryResults.Where(IndexEntryFilterBuilder.ModifiedBeforeExclusive(modifiedBefore));

                if (!string.IsNullOrEmpty(criteria.UsedByFilter))
                {
                    // Build the set of objects that reference the target via the inverted CalledBy index.
                    // Multiple entries can share a name across types (e.g. Attribute:X and Domain:X), so collect all.
                    var consumerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var candidates = index.FindByName(criteria.UsedByFilter);
                    foreach (var t in candidates)
                    {
                        if (t?.CalledBy == null) continue;
                        lock (t.CalledBy)
                        {
                            foreach (var c in t.CalledBy)
                            {
                                if (!string.IsNullOrEmpty(c)) consumerNames.Add(c);
                            }
                        }
                    }

                    queryResults = queryResults.Where(e =>
                        consumerNames.Contains(e.Name) ||
                        (e.RootTable != null && string.Equals(e.RootTable, criteria.UsedByFilter, StringComparison.OrdinalIgnoreCase)) ||
                        (e.Calls != null && e.Calls.Exists(c => string.Equals(c, criteria.UsedByFilter, StringComparison.OrdinalIgnoreCase))) ||
                        (e.Tables != null && e.Tables.Exists(t => string.Equals(t, criteria.UsedByFilter, StringComparison.OrdinalIgnoreCase)))
                    );
                }

                float[] queryEmbedding = null;
                if (!isQuick && criteria.Terms.Count > 0)
                {
                    queryEmbedding = _vectorService.ComputeEmbedding(query);
                }

                var rankedAll = queryResults
                    .Select(entry =>
                    {
                        int score = 0;
                        float vectorScore = 0;

                        if (criteria.Terms.Count > 0)
                        {
                            score = CalculateSemanticScore(entry, criteria.Terms, criteria.TypeFilter);

                            // SHORT-CIRCUIT: structural noise types should never surface unless
                            // the caller explicitly asked for them via typeFilter. Index/Folder/
                            // Module had been polluting query results when there was no exact
                            // name match (see Probe#2: "Country" returned 15 Index objects).
                            bool isNoiseType = entry.Type == "Folder" || entry.Type == "Module" || entry.Type == "Index";
                            bool noiseExplicitlyRequested = !string.IsNullOrEmpty(criteria.TypeFilter)
                                && IsTypeMatch(entry.Type, criteria.TypeFilter);
                            if (score <= 0 && isNoiseType && !noiseExplicitlyRequested)
                                return new RankedResult(null, -1, 0);
                            if (isNoiseType && !noiseExplicitlyRequested)
                                return new RankedResult(null, -1, 0);

                            if (!isQuick && entry.Embedding != null && queryEmbedding != null)
                            {
                                vectorScore = _vectorService.CosineSimilarity(queryEmbedding, entry.Embedding);
                            }
                            if (!isQuick && score <= 0 && vectorScore < 0.45f)
                                return new RankedResult(null, -1, 0);
                        }
                        else
                        {
                            score = (entry.Type == "Folder" || entry.Type == "Module") ? 1000 : 1; // Default browsing order
                        }

                        int finalScore = score + (int)(vectorScore * 1000);
                        return new RankedResult(entry, finalScore, vectorScore);
                    })
                    .Where(r => r.Score > 0)
                    .ToList();

                // v2.6.8: sort selector. "lastUpdate" bypasses relevance ranking and
                // returns newest-first — the agent explicitly opted out of score
                // ordering in favor of recency.
                bool sortByLastUpdate = !string.IsNullOrEmpty(sort) &&
                    string.Equals(sort, "lastUpdate", StringComparison.OrdinalIgnoreCase);
                if (sortByLastUpdate)
                {
                    rankedAll.Sort(CompareRankedResultsByLastUpdate);
                }
                else
                {
                    rankedAll.Sort(CompareRankedResults);
                }

                int total = rankedAll.Count;

                // v2.6.8: stable cursor (only honored with sort=lastUpdate, same as list_objects).
                int startIndex = 0;
                if (sortByLastUpdate && !string.IsNullOrEmpty(cursor))
                {
                    var decoded = ListService.DecodeCursor(cursor);
                    if (decoded.HasValue)
                    {
                        var (cursorTs, cursorName, cursorGuid) = decoded.Value;
                        // v2.6.8 (review C1): predicate mirrors the sort tuple
                        // (LastUpdate desc, Name asc, Guid asc) — see ListService
                        // for the rationale.
                        int resumeAt = rankedAll.FindIndex(r =>
                        {
                            if (r.Entry.LastUpdate < cursorTs) return true;
                            if (r.Entry.LastUpdate != cursorTs) return false;
                            int byName = string.Compare(r.Entry.Name ?? string.Empty, cursorName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                            if (byName > 0) return true;
                            if (byName < 0) return false;
                            return string.Compare(r.Entry.Guid ?? string.Empty, cursorGuid ?? string.Empty, StringComparison.OrdinalIgnoreCase) > 0;
                        });
                        if (resumeAt >= 0) startIndex = resumeAt;
                        else startIndex = total;
                    }
                }

                var effectiveLimit = limit <= 0 ? total : limit;
                int endIndex = Math.Min(total, (int)Math.Min((long)total, (long)startIndex + effectiveLimit));
                int returnedCount = Math.Max(0, endIndex - startIndex);
                bool hasMore = (startIndex + returnedCount) < total;

                JObject responseObj;
                // v2.6.8: stringify lastUpdate once per row; null when unknown so the
                // gateway projector can detect "no lifecycle data" cleanly.
                string FormatLu(SearchIndex.IndexEntry e) =>
                    e.LastUpdate > DateTime.MinValue ? e.LastUpdate.ToUniversalTime().ToString("o") : null;

                var resultsArr = new JArray();
                if (isQuick)
                {
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        var e = rankedAll[i].Entry;
                        resultsArr.Add(new JObject
                        {
                            ["guid"] = e.Guid,
                            ["name"] = e.Name,
                            ["type"] = e.Type,
                            ["parent"] = e.Parent,
                            ["module"] = e.Module,
                            ["path"] = e.Path,
                            ["parentPath"] = e.ParentPath,
                            ["lastUpdate"] = FormatLu(e)
                        });
                    }
                }
                else
                {
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        var r = rankedAll[i];
                        var e = r.Entry;
                        resultsArr.Add(new JObject
                        {
                            ["guid"] = e.Guid,
                            ["name"] = e.Name,
                            ["type"] = e.Type,
                            ["description"] = e.Description,
                            ["parm"] = e.ParmRule,
                            ["snippet"] = e.SourceSnippet,
                            ["parent"] = e.Parent,
                            ["module"] = e.Module,
                            ["path"] = e.Path,
                            ["parentPath"] = e.ParentPath,
                            ["dataType"] = e.DataType,
                            ["length"] = e.Length,
                            ["decimals"] = e.Decimals,
                            ["table"] = e.RootTable,
                            ["similarity"] = r.VectorSimilarity,
                            ["lastUpdate"] = FormatLu(e)
                        });
                    }
                }

                responseObj = new JObject
                {
                    ["count"] = returnedCount,
                    ["total"] = total,
                    ["hasMore"] = hasMore,
                    ["results"] = resultsArr
                };

                // v2.6.8: nextCursor for stable temporal paging. Mirrors list_objects.
                if (sortByLastUpdate && hasMore && returnedCount > 0 && endIndex > 0)
                {
                    var last = rankedAll[endIndex - 1].Entry;
                    if (last != null)
                    {
                        var token = ListService.EncodeCursor(last.LastUpdate, last.Name, last.Guid);
                        if (!string.IsNullOrEmpty(token)) responseObj["nextCursor"] = token;
                    }
                }

                // v2.8.0: canonical pagination block
                responseObj["pagination"] = new JObject
                {
                    ["offset"]     = startIndex,
                    ["limit"]      = effectiveLimit,
                    ["returned"]   = returnedCount,
                    ["total"]      = total,
                    ["hasMore"]    = hasMore,
                    ["nextOffset"] = hasMore ? (JToken)(int)(startIndex + returnedCount) : JValue.CreateNull()
                };

                // Only surface a "suggested_next" when the top result is a confident
                // name match (exact >= 10000, prefix >= 1000). Substring/vector-only
                // hits had been driving agents to wrong objects (e.g. literal
                // "Country" suggesting "QueryViewerCountry"). Always emit a
                // match_quality so the caller can decide.
                var meta = (responseObj["_meta"] as JObject) ?? new JObject();
                string matchQuality = "none";
                if (returnedCount > 0 && startIndex < total)
                {
                    var topResult = rankedAll[startIndex];
                    int topScore = topResult.Score;
                    string topName = topResult.Entry?.Name ?? "";
                    bool topIsExact = criteria.Terms.Any(t => string.Equals(topName, t, StringComparison.OrdinalIgnoreCase));
                    bool topIsPrefix = !topIsExact && criteria.Terms.Any(t => topName.StartsWith(t, StringComparison.OrdinalIgnoreCase));
                    if (topIsExact) matchQuality = "exact";
                    else if (topIsPrefix) matchQuality = "prefix";
                    else if (topScore >= 500) matchQuality = "substring";
                    else matchQuality = "vector";

                    meta["match_quality"] = matchQuality;
                    if (matchQuality == "exact" || matchQuality == "prefix")
                    {
                        var suggestion = BuildSuggestedNext(topResult.Entry);
                        if (suggestion != null) meta["suggested_next"] = suggestion;
                    }
                }
                else
                {
                    meta["match_quality"] = matchQuality;
                }
                responseObj["_meta"] = meta;

                // issue #25 follow-up (P0): usedby (reads CalledBy/Calls) and semantic
                // ranking (reads Embedding) depend on ENRICHMENT, which lazily lags the
                // "Ready" state. IsScanning is false in that window, so without this an
                // empty usedby set / degraded semantic recall looks authoritative. Flag
                // it so a zero/short result isn't read as "nothing uses X".
                bool enrichmentSensitive = !string.IsNullOrEmpty(criteria.UsedByFilter)
                    || (!isQuick && criteria.Terms.Count > 0);
                if (enrichmentSensitive && !_indexCacheService.IsScanning && _indexCacheService.HasPendingEnrichment())
                {
                    meta["enrichmentPending"] = true;
                    meta["enrichmentHint"] = "The index is still enriching (cross-reference edges / semantic vectors are incomplete). A zero or short result here may be an artifact of pending enrichment, not the truth — re-run once whoami reports enrichment complete before concluding nothing matches.";
                    responseObj["_meta"] = meta;
                }

                if (_indexCacheService.IsScanning)
                {
                    AnnotatePartial(responseObj);
                }

                string json = responseObj.ToString(Newtonsoft.Json.Formatting.None);
                if (!_indexCacheService.IsScanning) _queryCache.TryAdd(cacheKey, json);

                return json;
            }
            catch (Exception ex) { return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
            finally
            {
                sw.Stop();
                if (sw.ElapsedMilliseconds > 50)
                {
                    Logger.Info($"[SEARCH-SLOW] {sw.ElapsedMilliseconds}ms query='{query}' type='{typeFilter}' limit={limit} exact={exactMatch}");
                }
            }
        }

        // Returns a serialized result if the query looks like a literal object name
        // and an SDK direct lookup succeeded. Returns null to fall through to the
        // normal index-based path.
        private string TryDirectLookup(string query, string typeFilter, bool exactMatch)
        {
            if (_objectService == null) return null;
            // STA guard: search runs on the thread-pool (MTA) because it's marked
            // thread-safe in CommandDispatcher.IsThreadSafe, but FindObject touches
            // the COM-flavoured SDK — same class of native AV documented for
            // SearchSource. Losing the exact-match boost off-STA is acceptable;
            // crashing the worker is not. Fall through to the in-memory index path.
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA) return null;
            if (string.IsNullOrWhiteSpace(query)) return null;

            string trimmed = query.Trim();
            // Skip if the query carries filter syntax, wildcards, or multi-term semantics.
            if (trimmed.IndexOfAny(DirectLookupStopChars) >= 0) return null;
            // Object names are typically reasonable identifiers.
            if (trimmed.Length > 80) return null;

            try
            {
                var obj = _objectService.FindObject(trimmed, typeFilter);
                if (obj == null) return null;

                string typeName = null;
                try { typeName = obj.TypeDescriptor?.Name; } catch { }
                string guid = null;
                try { guid = obj.Guid.ToString(); } catch { }

                var single = new JObject
                {
                    ["guid"] = guid,
                    ["name"] = obj.Name,
                    ["type"] = typeName
                };

                var responseObj = new JObject
                {
                    ["count"] = 1,
                    ["total"] = 1,
                    ["hasMore"] = false,
                    ["results"] = new JArray(single),
                    // v2.8.0: canonical pagination block
                    ["pagination"] = new JObject
                    {
                        ["offset"]     = 0,
                        ["limit"]      = 1,
                        ["returned"]   = 1,
                        ["total"]      = 1,
                        ["hasMore"]    = false,
                        ["nextOffset"] = JValue.CreateNull()
                    },
                    ["_meta"] = new JObject
                    {
                        ["direct_lookup"] = true,
                        // Direct lookup means the SDK returned an exact name match —
                        // surface match_quality so the envelope is consistent with the
                        // index path (callers can branch on this field uniformly).
                        ["match_quality"] = "exact",
                        ["suggested_next"] = BuildSuggestedReadFor(obj.Name, typeName)
                    }
                };

                // Annotate partial whenever the background bulk index hasn't finished —
                // even on a direct-lookup success — so the agent knows that a re-query
                // (e.g. broader search by attribute) may yield more results once the
                // index is warm. Probe#1 observed cold-state queries silently omitting
                // this flag.
                if (_indexCacheService.IsScanning || _indexCacheService.IsIndexMissing)
                    AnnotatePartial(responseObj);

                return responseObj.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                Logger.Debug("Direct lookup failed (" + trimmed + "): " + ex.Message);
                return null;
            }
        }

        // Build a plain zero-result payload for a built-and-empty index (the KB genuinely
        // has no model objects). Mirrors the canonical search response shape so callers can
        // branch uniformly; _meta.empty_reason = "kb_has_no_objects" makes it unambiguous
        // versus the "warming" partial responses (and stops the redundant BulkIndex kick).
        private string BuildEmptyKbResponse(int limit)
        {
            var resp = new JObject
            {
                ["count"] = 0,
                ["total"] = 0,
                ["hasMore"] = false,
                ["results"] = new JArray(),
                ["pagination"] = new JObject
                {
                    ["offset"]     = 0,
                    ["limit"]      = limit,
                    ["returned"]   = 0,
                    ["total"]      = 0,
                    ["hasMore"]    = false,
                    ["nextOffset"] = JValue.CreateNull()
                },
                ["_meta"] = new JObject
                {
                    ["empty_reason"] = "kb_has_no_objects",
                    ["emptyHint"] = "This KB's model reports no objects (empty KB — e.g. a missing LocalDB model). Create objects with genexus_create or open a different KB; re-running lifecycle action=index cannot populate it."
                }
            };
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        // Build a zero/partial-result payload that signals indexing is still running,
        // so agents know to retry and clients can render a progress hint.
        private string BuildPartialResponse(string query, object[] results, int total, bool scanning)
        {
            int returned = results?.Length ?? 0;
            var resp = new JObject
            {
                ["count"] = returned,
                ["total"] = total,
                ["hasMore"] = false,
                ["results"] = results == null ? new JArray() : JArray.FromObject(results),
                // v2.8.0: canonical pagination block
                ["pagination"] = new JObject
                {
                    ["offset"]     = 0,
                    ["limit"]      = returned,
                    ["returned"]   = returned,
                    ["total"]      = total,
                    ["hasMore"]    = false,
                    ["nextOffset"] = JValue.CreateNull()
                }
            };
            AnnotatePartial(resp);
            if (!scanning) resp["_meta"]["indexStatus"] = "empty";
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        private void AnnotatePartial(JObject responseObj)
        {
            var meta = responseObj["_meta"] as JObject ?? new JObject();
            int processed = _indexCacheService.KbService?.IndexProcessed ?? 0;
            int total = _indexCacheService.KbService?.IndexTotal ?? 0;
            int pct = total > 0 ? (int)Math.Round(100.0 * processed / total) : 0;

            meta["partial"] = true;
            meta["indexStatus"] = "scanning";
            meta["indexed_count"] = processed;
            meta["index_total_estimated"] = total;
            meta["indexed_pct"] = pct;
            meta["progress_token"] = "genexus-mcp-bulk-index";
            var state = _indexCacheService.GetState();
            bool recoverable = state?.Recoverable == true
                || string.Equals(state?.Status, "Cold", StringComparison.OrdinalIgnoreCase);
            meta["operationId"] = state?.OperationId != null ? (JToken)state.OperationId : JValue.CreateNull();
            meta["operationState"] = state?.OperationState ?? "Unknown";
            meta["workerAlive"] = state?.WorkerAlive ?? false;
            meta["recoverable"] = recoverable;
            meta["retry_hint"] = recoverable
                ? "The index worker is stalled or unavailable. Recover with genexus_lifecycle action=index force=true, then retry query."
                : "Indexing in background. Re-run query for more results; read/edit/build/list tools work without the index.";
            responseObj["_meta"] = meta;
        }

        private static JObject BuildSuggestedNext(SearchIndex.IndexEntry top)
        {
            if (top == null) return null;
            return BuildSuggestedReadFor(top.Name, top.Type);
        }

        private static JObject BuildSuggestedNext(List<RankedResult> results)
        {
            if (results == null || results.Count == 0) return null;
            return BuildSuggestedNext(results[0].Entry);
        }

        public static JObject BuildSuggestedReadFor(string name, string type)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return new JObject
            {
                ["tool"] = "genexus_read",
                ["args"] = new JObject { ["name"] = name, ["type"] = type }
            };
        }

        private static bool ContainsIgnoreCase(List<string> list, string term)
        {
            if (list == null || list.Count == 0) return false;
            int termLen = term.Length;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && s.Length == termLen && string.Equals(s, term, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private int CalculateSemanticScore(SearchIndex.IndexEntry entry, HashSet<string> terms, string typeFilter)
        {
            int score = 0;
            string name = entry.Name ?? "";
            string desc = entry.Description ?? "";
            int nameLen = name.Length;
            int descLen = desc.Length;
            bool isTableType = string.Equals(entry.Type, "Table", StringComparison.OrdinalIgnoreCase);
            bool isTableFilter = string.Equals(typeFilter, "Table", StringComparison.OrdinalIgnoreCase);

            foreach (var term in terms) {
                int termLen = term.Length;
                if (nameLen >= termLen)
                {
                    if (nameLen == termLen && name.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 10000;
                    else if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 1000;
                    else if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 500;
                }

                if (descLen >= termLen && desc.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 300;

                if (ContainsIgnoreCase(entry.Keywords, term)) score += 800;
                if (ContainsIgnoreCase(entry.Tags, term)) score += 800;

                if (entry.Tables != null && ContainsIgnoreCase(entry.Tables, term))
                {
                    bool boostForAttributeMember = isTableFilter
                                                   && isTableType
                                                   && _indexCacheService.LooksLikeAttributeName(term);
                    score += boostForAttributeMember ? 5000 : 400;
                }
                if (ContainsIgnoreCase(entry.Calls, term)) score += 400;
            }
            return score;
        }

        // Plan 006: alias/synonym-aware type match, factored into the shared
        // IndexEntryFilterBuilder (see that file for why ListService keeps its own
        // exact-match instead of calling this).
        private static bool IsTypeMatch(string type, string query) => IndexEntryFilterBuilder.IsTypeMatchAliasAware(type, query);

        private SearchCriteria ParseQuery(string query)
        {
            var c = new SearchCriteria();
            if (string.IsNullOrEmpty(query)) return c;

            var parsed = QueryGrammar.Parse(query);
            c.DescriptionFilter = parsed.DescriptionFilter;
            c.MetadataFilter = parsed.MetadataFilter;
            c.UsedByFilter = parsed.UsedByFilter;
            c.ParentPathFilter = parsed.ParentPathFilter;
            c.ParentFilter = parsed.ParentFilter;
            c.TypeFilter = parsed.TypeFilter;
            c.NameFilter = parsed.NameFilter;

            if (string.IsNullOrEmpty(c.NameFilter))
            {
                var bareQuoted = BareQuotedNameRegex.Match(query.Trim());
                if (bareQuoted.Success)
                {
                    c.NameFilter = bareQuoted.Groups["v"].Value;
                }
            }

            foreach (var part in parsed.FreeTerms)
            {
                if (part == "*") continue;
                c.Terms.Add(part.ToLowerInvariant());
            }
            return c;
        }

        internal readonly struct RankedResult
        {
            public SearchIndex.IndexEntry Entry { get; }
            public int Score { get; }
            public float VectorSimilarity { get; }

            public RankedResult(SearchIndex.IndexEntry entry, int score, float vectorSimilarity)
            {
                Entry = entry;
                Score = score;
                VectorSimilarity = vectorSimilarity;
            }
        }

        internal static int CompareRankedResults(RankedResult a, RankedResult b)
        {
            int scoreComp = b.Score.CompareTo(a.Score);
            if (scoreComp != 0) return scoreComp;

            string nameA = a.Entry != null ? a.Entry.Name : string.Empty;
            string nameB = b.Entry != null ? b.Entry.Name : string.Empty;
            return string.Compare(nameA ?? string.Empty, nameB ?? string.Empty, StringComparison.Ordinal);
        }

        internal static int CompareRankedResultsByLastUpdate(RankedResult a, RankedResult b)
        {
            if (a.Entry == null && b.Entry == null) return 0;
            if (a.Entry == null) return 1;
            if (b.Entry == null) return -1;

            int dateComp = b.Entry.LastUpdate.CompareTo(a.Entry.LastUpdate);
            if (dateComp != 0) return dateComp;

            int nameComp = string.Compare(a.Entry.Name ?? string.Empty, b.Entry.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (nameComp != 0) return nameComp;

            return string.Compare(a.Entry.Guid ?? string.Empty, b.Entry.Guid ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        private class SearchCriteria {
            public string TypeFilter { get; set; } public string ParentFilter { get; set; } public string ParentPathFilter { get; set; }
            public string UsedByFilter { get; set; } public string DomainFilter { get; set; }
            public string DescriptionFilter { get; set; } public string MetadataFilter { get; set; }
            public string NameFilter { get; set; }
            public HashSet<string> Terms { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
