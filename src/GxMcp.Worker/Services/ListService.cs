using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ListService
    {
        private static readonly BoundedStringCache _listCache = new BoundedStringCache(512);
        private static DateTime _lastIndexTime;
        private static long _lastGraphRevision;

        public static void InvalidateCache()
        {
            _listCache.Clear();
        }

        private readonly KbService _kbService;
        private readonly IndexCacheService _indexCacheService;

        public ListService(KbService kbService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _indexCacheService = indexCacheService;
        }

        // v2.3.8 (Task 2.2): test-only seam — drive ListService with just an
        // IndexCacheService (no KB). Used by ListDiscoveryTests with the
        // LoadFromEntries fixture.
        public ListService(IndexCacheService indexCacheService)
            : this(null, indexCacheService)
        {
        }

        // v2.3.8 (Task 2.2): structured criteria for the new name/description/path
        // filters. Existing callers keep using ListObjects(...); this overload is
        // the supported entrypoint for unit tests and future callers that want
        // typed args. NameFilter matches name only, DescriptionFilter matches
        // description only, PathPrefix is a case-insensitive StartsWith over
        // ParentFolderPath (e.g. "Root Module/ClickSign/"). Legacy Filter still
        // matches both name and description.
        public string List(ListCriteria c)
        {
            if (c == null) c = new ListCriteria();
            return ListObjects(
                filter: c.Filter,
                limit: c.Limit,
                offset: c.Offset,
                parentFilter: null,
                typeFilter: c.TypeFilter,
                parentPathFilter: null,
                verbose: c.Verbose,
                invokerNameFilter: c.NameFilter,
                invokerDescriptionFilter: c.DescriptionFilter,
                invokerPathPrefix: c.PathPrefix,
                sort: c.Sort,
                since: c.Since,
                modifiedBefore: c.ModifiedBefore,
                cursor: c.Cursor);
        }

        internal static bool PathPrefixMatches(string candidatePath, string requestedPrefix)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(requestedPrefix)) return false;
            string candidate = candidatePath.Trim().Replace('\\', '/').TrimEnd('/');
            string prefix = requestedPrefix.Trim().Replace('\\', '/').TrimEnd('/');
            return string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        public string ListObjects(string filter, int limit, int offset, string parentFilter = null, string typeFilter = null, string parentPathFilter = null, bool verbose = false, string invokerNameFilter = null, string invokerDescriptionFilter = null, string invokerPathPrefix = null, string sort = null, DateTime since = default(DateTime), DateTime modifiedBefore = default(DateTime), string cursor = null)
        {
            var sw = Stopwatch.StartNew();
            string source = "none";
            string Finalize(string response)
            {
                sw.Stop();
                Logger.Debug($"[ListService] source={source} limit={limit} offset={offset} parentPath='{parentPathFilter ?? ""}' parent='{parentFilter ?? ""}' typeFilter='{typeFilter ?? ""}' filter='{filter ?? ""}' nameFilter='{invokerNameFilter ?? ""}' descriptionFilter='{invokerDescriptionFilter ?? ""}' pathPrefix='{invokerPathPrefix ?? ""}' verbose={verbose} elapsedMs={sw.ElapsedMilliseconds}");
                return response;
            }

            try
            {
                var array = new JArray();

                // Parse filter: can be a comma-separated list of types or a partial name
                var filterTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string nameFilter = null;

                if (!string.IsNullOrEmpty(filter))
                {
                    if (filter.Contains(","))
                    {
                        foreach (var t in filter.Split(',')) filterTypes.Add(t.Trim());
                    }
                    else if (IsLikelyType(filter))
                    {
                        filterTypes.Add(filter.Trim());
                    }
                    else
                    {
                        nameFilter = filter.Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(typeFilter))
                {
                    foreach (var t in typeFilter.Split(','))
                    {
                        var trimmed = t.Trim();
                        if (!string.IsNullOrEmpty(trimmed)) filterTypes.Add(trimmed);
                    }
                }

                // v2.6.9 perf: fast-fail when the index isn't fully built yet.
                // First list_objects on a cold worker was blocking ~57s while
                // either gzip+JSON parse OR the SDK fallback's full Objects.GetAll
                // enumeration ran inline. Now probe non-blocking; if the index is
                // missing or empty AND the IndexCacheService reports a not-Ready
                // state, kick the background loader/builder and return an
                // "Indexing" envelope so the agent retries in a few seconds.
                var index = _indexCacheService.TryGetLoadedIndex();
                var indexState = _indexCacheService.GetState();
                // v2.6.9 perf: UltraLiteReady / LiteReady / Enriching are all
                // usable for list_objects — lite pass populates every field we
                // project (name/type/path/lastUpdate). UltraLiteReady means the
                // walk is still in progress and the index is growing; we surface
                // `partial:true` so the agent knows to retry for the full set.
                string indexStatusUpper = indexState?.Status ?? string.Empty;
                bool indexFresh = string.Equals(indexState?.Freshness, "current", StringComparison.OrdinalIgnoreCase);
                // issue #26 P9: `indexPartial` is the single source of truth for
                // "the catalogue walk is demonstrably incomplete". Ready/LiteReady/
                // Enriching all mean the full object catalogue HAS been walked
                // (lite pass populates every projected field), so only
                // UltraLiteReady is partial today. Every downstream signal
                // (total/hasMore/pagination/empty-page hints) must key off this
                // flag rather than assuming non-UltraLite == complete.
                bool indexPartial = string.Equals(indexStatusUpper, "UltraLiteReady", StringComparison.OrdinalIgnoreCase);
                // Built-and-empty is a LEGITIMATELY empty KB (the lite walk completed and
                // found no model objects — e.g. a KB whose LocalDB model is missing), NOT
                // a build in progress. Only a null index or a not-yet-built status is
                // "not ready"; count==0 with Ready/LiteReady/Enriching falls through to
                // the honest empty-listing branch below instead of looping IndexNotReady.
                bool indexNotReady = index == null || ((!indexFresh && !indexPartial)
                    || !(string.Equals(indexStatusUpper, "Ready", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(indexStatusUpper, "LiteReady", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(indexStatusUpper, "Enriching", StringComparison.OrdinalIgnoreCase)
                        || indexPartial));
                if (indexNotReady)
                {
                    _indexCacheService.EnsureLoadStarted();
                    bool recoverable = indexState?.Recoverable == true
                        || string.Equals(indexState?.Status, "Cold", StringComparison.OrdinalIgnoreCase);
                    var envelope = new JObject
                    {
                        ["status"] = "Indexing",
                        ["code"] = "IndexNotReady",
                        ["indexStatus"] = indexState?.Status ?? "Cold",
                        ["freshness"] = indexState?.Freshness ?? "stale",
                        ["totalObjects"] = indexState?.TotalObjects ?? 0,
                        ["operationId"] = indexState?.OperationId != null ? (JToken)indexState.OperationId : JValue.CreateNull(),
                        ["operationState"] = indexState?.OperationState ?? "Unknown",
                        ["workerAlive"] = indexState?.WorkerAlive ?? false,
                        ["recoverable"] = recoverable,
                        ["message"] = BuildIndexingMessage(indexState),
                        // Issue #209 (policy A): the gate is fail-closed, so the envelope must be
                        // awaitable + retryable rather than a dead end. Mirrors the retryAfterMs
                        // precedent in SourceSearchService.
                        ["hint"] = (recoverable
                            ? "The index worker is stalled or unavailable. Recover with genexus_lifecycle action=index force=true, then wait with action=status wait=30 freshness=current."
                            : "Wait for the index with genexus_lifecycle action=status wait=30 freshness=current, then re-issue list_objects (genexus_whoami observes progress)."),
                        ["retryAfterMs"] = indexState?.EtaMs ?? 5000
                    };
                    if (indexState?.Progress != null) envelope["progress"] = indexState.Progress.Value;
                    if (indexState?.EtaMs != null) envelope["etaMs"] = indexState.EtaMs.Value;
                    return Finalize(envelope.ToString(Newtonsoft.Json.Formatting.None));
                }
                if (index.Objects.Count == 0 && !indexPartial)
                {
                    // Built-and-empty (Ready / LiteReady / Enriching with 0 entries): the
                    // walk completed and the KB has no model objects. Return an honest
                    // empty listing tagged kb_has_no_objects instead of an eternal
                    // IndexNotReady (which left agents looping `lifecycle action=index
                    // force=true` on empty KBs forever — there is nothing to index).
                    // UltraLiteReady/0 is excluded: the walk is still streaming and
                    // nothing has landed yet, so it must not claim the KB is empty.
                    var empty = BuildPagedResponseInternal(new JArray(), 0, 0, limit <= 0 ? int.MaxValue : limit);
                    var meta = empty["_meta"] as JObject ?? new JObject();
                    meta["empty_reason"] = "kb_has_no_objects";
                    meta["emptyHint"] = "This KB's model reports no objects (empty KB — e.g. a missing LocalDB model). Create objects with genexus_create or open a different KB; re-running lifecycle action=index cannot populate it.";
                    empty["_meta"] = meta;
                    return Finalize(empty.ToString(Newtonsoft.Json.Formatting.None));
                }

                string cacheKey = $"{filter}|{limit}|{offset}|{parentFilter}|{typeFilter}|{parentPathFilter}|{verbose}|{invokerNameFilter}|{invokerDescriptionFilter}|{invokerPathPrefix}|{sort}|{since:yyyyMMddHHmmss}|{modifiedBefore:yyyyMMddHHmmss}|{cursor}";

                if (index.LastUpdated > _lastIndexTime || index.GraphRevision != _lastGraphRevision)
                {
                    _listCache.Clear();
                    _lastIndexTime = index.LastUpdated;
                    _lastGraphRevision = index.GraphRevision;
                }

                if (!indexPartial && !_indexCacheService.IsScanning && _listCache.TryGetValue(cacheKey, out var cached))
                {
                    source = "cache";
                    return Finalize(cached);
                }

                if (index.Objects.Count > 0)
                {
                    IEnumerable<SearchIndex.IndexEntry> entries;
                    source = "index-all";
                    // issue #26 P9: a parent/parentPath filter miss during a partial
                    // walk (indexPartial) is NOT proof the folder is empty/absent —
                    // the walk may simply not have reached it yet. Track the missed
                    // key so we can attach the same partial signaling the
                    // typeFilter-miss path already gets, instead of a bare empty page.
                    bool parentMiss = false;
                    string missedParentKey = null;
                    // Plan 002: true only when `entries` is the unfiltered full Objects.Values
                    // scan (no parent/parentPath filter already narrowed it) — gates the
                    // TypeIndex prefilter below.
                    bool isFullScan = false;

                    if (!string.IsNullOrWhiteSpace(parentPathFilter) &&
                        index.ChildrenByParent != null &&
                        index.ChildrenByParent.TryGetValue(parentPathFilter, out var childrenByPath))
                    {
                        entries = childrenByPath;
                        source = "index-parentPath";
                    }
                    else if (!string.IsNullOrWhiteSpace(parentPathFilter))
                    {
                        entries = Enumerable.Empty<SearchIndex.IndexEntry>();
                        source = "index-parentPath-miss";
                        parentMiss = true;
                        missedParentKey = parentPathFilter;
                    }
                    else if (!string.IsNullOrWhiteSpace(parentFilter) &&
                             index.ChildrenByParent != null &&
                             index.ChildrenByParent.TryGetValue(parentFilter, out var childrenByParent))
                    {
                        entries = childrenByParent;
                        source = "index-parent";
                    }
                    else if (!string.IsNullOrWhiteSpace(parentFilter))
                    {
                        entries = Enumerable.Empty<SearchIndex.IndexEntry>();
                        source = "index-parent-miss";
                        parentMiss = true;
                        missedParentKey = parentFilter;
                    }
                    else
                    {
                        entries = index.Objects.Values;
                        isFullScan = true;
                    }

                    if (filterTypes.Count > 0)
                    {
                        // Plan 002: when no parent/parentPath filter already narrowed `entries`,
                        // intersect the derived TypeIndex buckets instead of scanning every
                        // object. filterTypes here is an EXACT case-insensitive set (unlike
                        // SearchService's alias-aware IsTypeMatch), so each entry maps directly
                        // to a TypeIndex bucket key. Falls back to the full scan below when the
                        // index hasn't built TypeIndex yet (e.g. LoadFromEntries test seam).
                        if (isFullScan && index.TypeIndex != null)
                        {
                            var candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var t in filterTypes)
                            {
                                if (index.TypeIndex.TryGetValue(t, out var keys))
                                {
                                    lock (keys) { candidateKeys.UnionWith(keys); }
                                }
                            }
                            entries = candidateKeys
                                .Select(k => index.Objects.TryGetValue(k, out var e) ? e : null)
                                .Where(e => e != null);
                        }
                        entries = entries.Where(e => filterTypes.Contains(e.Type ?? string.Empty));
                    }

                    // Legacy filter: matches on EITHER name or description (kept
                    // for backward compatibility). Prefer the targeted nameFilter
                    // / descriptionFilter / pathPrefix parameters below.
                    if (!string.IsNullOrEmpty(nameFilter))
                    {
                        var descriptionMatches = IndexEntryFilterBuilder.DescriptionContains(nameFilter);
                        entries = entries.Where(e =>
                            (e.Name ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            descriptionMatches(e));
                    }

                    // v2.3.8 (Task 2.2): targeted discovery filters.
                    // The "nameFilter" parameter on this method historically
                    // refers to the legacy filter token derived from the user's
                    // `filter` arg (matches name OR description). The function
                    // arguments below — invokerNameFilter / invokerDescriptionFilter / invokerPathPrefix —
                    // come from the new `nameFilter`/`descriptionFilter`/`pathPrefix`
                    // tool args and match exactly one column each.
                    if (!string.IsNullOrEmpty(invokerNameFilter))
                    {
                        entries = entries.Where(e =>
                            (e.Name ?? string.Empty).IndexOf(invokerNameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (!string.IsNullOrEmpty(invokerDescriptionFilter))
                    {
                        entries = entries.Where(IndexEntryFilterBuilder.DescriptionContains(invokerDescriptionFilter));
                    }

                    if (!string.IsNullOrEmpty(invokerPathPrefix))
                    {
                        entries = entries.Where(e =>
                            PathPrefixMatches(e.ParentFolderPath, invokerPathPrefix));
                    }

                    // v2.6.8: temporal filters (Since inclusive, ModifiedBefore exclusive).
                    // Items with LastUpdate=MinValue (unknown) are excluded once any
                    // temporal bound is set — "modified before X" is meaningless for
                    // items with no recorded modification timestamp.
                    if (since > DateTime.MinValue)
                    {
                        entries = entries.Where(IndexEntryFilterBuilder.SinceInclusive(since));
                    }
                    if (modifiedBefore > DateTime.MinValue)
                    {
                        entries = entries.Where(IndexEntryFilterBuilder.ModifiedBeforeExclusive(modifiedBefore));
                    }

                    // v2.6.8: sort selector. "lastUpdate" returns newest-first and skips
                    // the Folder/Module bucketing — when the agent asks for "what changed",
                    // grouping by type fights the question.
                    bool sortByLastUpdate = !string.IsNullOrEmpty(sort) &&
                        string.Equals(sort, "lastUpdate", StringComparison.OrdinalIgnoreCase);

                    IComparer<SearchIndex.IndexEntry> comparer = sortByLastUpdate
                        ? (IComparer<SearchIndex.IndexEntry>)LastUpdateIndexEntryComparer.Instance
                        : DefaultIndexEntryComparer.Instance;

                    int startIndex = Math.Max(0, offset);
                    int pageSize = limit <= 0 ? int.MaxValue : limit;
                    int needed = (startIndex <= int.MaxValue - pageSize) ? startIndex + pageSize : int.MaxValue;

                    List<SearchIndex.IndexEntry> orderedIndexEntries;
                    int totalIndex;

                    // PERFORMANCE: If we only need top-K items (common MCP list paging, e.g. limit=50, offset=0)
                    // and no cursor is specified, use a single-pass bounded heap (O(N log K)) instead of sorting all N items.
                    if (string.IsNullOrEmpty(cursor) && needed > 0 && needed <= 200)
                    {
                        orderedIndexEntries = TopKHelper.SelectTopK(entries, needed, comparer, out totalIndex);
                    }
                    else
                    {
                        var candidateList = entries.ToList();
                        totalIndex = candidateList.Count;
                        candidateList.Sort(comparer);
                        orderedIndexEntries = candidateList;
                    }

                    // v2.6.8: stable cursor wins over offset when both arrive. Decode
                    // pulls (lastUpdate, guid); we scan the ordered list to the first
                    // entry strictly older than that tuple. Cursor is opt-in: callers
                    // that ignore it still get offset-based paging.
                    if (!string.IsNullOrEmpty(cursor) && sortByLastUpdate)
                    {
                        var decoded = DecodeCursor(cursor);
                        if (decoded.HasValue)
                        {
                            var (cursorTs, cursorName, cursorGuid) = decoded.Value;
                            // v2.6.8 (review C1): predicate must mirror the OrderBy
                            // tuple — LastUpdate desc, Name asc, Guid asc — or we
                            // silently skip items whose Name sorts after the cursor
                            // but whose Guid sorts before it.
                            int resumeAt = orderedIndexEntries.FindIndex(e =>
                            {
                                if (e.LastUpdate < cursorTs) return true;
                                if (e.LastUpdate != cursorTs) return false;
                                int byName = string.Compare(e.Name ?? string.Empty, cursorName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                                if (byName > 0) return true;
                                if (byName < 0) return false;
                                return string.Compare(e.Guid ?? string.Empty, cursorGuid ?? string.Empty, StringComparison.OrdinalIgnoreCase) > 0;
                            });
                            if (resumeAt >= 0) startIndex = resumeAt;
                            else startIndex = totalIndex; // exhausted
                        }
                    }

                    bool legacyMode = IsLegacyPerfProfile();
                    int endIndex = Math.Min(totalIndex, (int)Math.Min((long)totalIndex, (long)startIndex + pageSize));
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        var entry = orderedIndexEntries[i];
                        array.Add(BuildItemInternal(
                            entry.Name,
                            entry.Type ?? "Unknown",
                            entry.Description,
                            entry.Parent ?? string.Empty,
                            entry.Module ?? string.Empty,
                            entry.Path ?? string.Empty,
                            entry.ParentPath ?? string.Empty,
                            entry.ParentFolderPath ?? string.Empty,
                            verbose,
                            entry.LastUpdate,
                            entry.CreatedAt,
                            entry.LastModifiedBy,
                            legacyMode,
                            entry.Guid,
                            entry.EntityKey,
                            entry.EntityTypeGuid,
                            entry.EntityId
                        ));
                    }

                    // v2.6.8: when sorting by lastUpdate, emit a cursor token built
                    // from the last item of this page so callers can continue without
                    // an offset that drifts as the KB mutates.
                    SearchIndex.IndexEntry lastEmitted = null;
                    if (sortByLastUpdate && array.Count > 0 && endIndex > startIndex)
                    {
                        lastEmitted = orderedIndexEntries[endIndex - 1];
                    }

                    var paged = BuildPagedResponseInternal(array, totalIndex, startIndex, pageSize);
                    // v2.6.9 perf: surface partial-catalogue signal while the lite
                    // walk is still streaming entries.
                    // issue #25 #4: during UltraLiteReady the catalogue is still
                    // growing, so `total`/`hasMore:false` reflect only the walked
                    // subset, not the KB. Make that impossible to ignore: mark the
                    // total partial and force hasMore=true (more objects ARE coming),
                    // so callers never treat this page as the complete set.
                    if (indexPartial)
                    {
                        paged["partial"] = true;
                        paged["indexStatus"] = "UltraLiteReady";
                        paged["totalIsPartial"] = true;
                        paged["hasMore"] = true;
                        paged["partialHint"] = "Index walk is in progress; `total` counts only objects walked so far (NOT the KB total) and more are still arriving. An empty/short result or a missing type/folder does NOT mean it is absent. Re-issue when whoami reports indexStatus=LiteReady or Ready for the full set.";
                        if (paged["pagination"] is JObject pgn)
                        {
                            pgn["total"] = JValue.CreateNull();   // true total unknown while walking
                            pgn["totalIsPartial"] = true;
                            pgn["hasMore"] = true;
                        }
                        // issue #26 P9: a parent/parentPath miss during a partial walk
                        // must not read as an authoritative "folder is empty" — override
                        // the generic empty_reason and attach a targeted hint (mirrors
                        // the typeFilter-miss block below, for the case where no
                        // typeFilter was supplied so that block never runs).
                        if (array.Count == 0 && parentMiss)
                        {
                            var parentMeta = paged["_meta"] as JObject ?? new JObject();
                            parentMeta["empty_reason"] = "partial_walk_incomplete";
                            if (parentMeta["filterHint"] == null)
                            {
                                parentMeta["filterHint"] = "parent/parentPath '" + missedParentKey + "' matched nothing SO FAR, but the index is still walking — this folder may exist and just hasn't been reached yet. Re-issue when whoami reports indexStatus=Ready before concluding it is absent.";
                            }
                            paged["_meta"] = parentMeta;
                        }
                    }
                    if (lastEmitted != null && (startIndex + array.Count) < totalIndex)
                    {
                        // v2.6.8 (review C1+C9): encode (ts, name, guid) so the
                        // resume predicate can replay the full tiebreak chain.
                        // EncodeCursor allows MinValue ts when name/guid present,
                        // so the "Untouched" tail no longer truncates pagination.
                        var token = EncodeCursor(lastEmitted.LastUpdate, lastEmitted.Name, lastEmitted.Guid);
                        if (!string.IsNullOrEmpty(token))
                            paged["nextCursor"] = token;
                    }
                    // Empty typeFilter result: hand back the distinct types present so the agent finds the canonical name.
                    if (array.Count == 0 && filterTypes.Count > 0 && index.Objects.Count > 0)
                    {
                        string[] distinctTypes;
                        if (index.TypeIndex != null && index.TypeIndex.Count > 0)
                        {
                            distinctTypes = index.TypeIndex.Keys
                                .Where(t => !string.IsNullOrEmpty(t))
                                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                .Take(60)
                                .ToArray();
                        }
                        else
                        {
                            distinctTypes = index.Objects.Values
                                .Select(e => e.Type ?? string.Empty)
                                .Where(t => !string.IsNullOrEmpty(t))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                .Take(60)
                                .ToArray();
                        }
                        var meta = paged["_meta"] as JObject ?? new JObject();
                        meta["typesAvailable"] = new JArray(distinctTypes);
                        // issue #25 #4: while the walk is partial, typesAvailable only
                        // lists types walked so far — a "missing" type may simply not
                        // have been reached yet, so don't imply it is absent.
                        meta["filterHint"] = indexPartial
                            ? "typeFilter='" + string.Join(",", filterTypes) + "' matched nothing SO FAR, but the index is still walking — this type may exist and just hasn't been reached yet. typesAvailable lists only types seen so far. Re-issue when indexStatus=Ready before concluding it is absent."
                            : "typeFilter='" + string.Join(",", filterTypes) + "' matched nothing. See typesAvailable for canonical type names actually present in this KB.";
                        paged["_meta"] = meta;
                    }
                    string json = paged.ToString(Newtonsoft.Json.Formatting.None);
                    if (!indexPartial && !_indexCacheService.IsScanning)
                    {
                        _listCache.TryAdd(cacheKey, json);
                    }
                    return Finalize(json);
                }

                // Defensive fallback for exotic states only (e.g. UltraLiteReady with 0
                // entries — the walk is streaming but nothing has landed yet): the built-
                // empty branch above already owns Ready/LiteReady/Enriching with 0 entries,
                // and a null/cold index fast-fails at the gate, so this path is effectively
                // unreachable today. Kept as ground-truth SDK enumeration for future index-
                // loading changes rather than deleted.
                // STA guard: this fallback enumerates via the COM-flavoured SDK
                // (kb.DesignModel), which must not be touched off the STA thread —
                // list runs on the thread-pool (MTA) since it's marked thread-safe
                // in CommandDispatcher.IsThreadSafe. Surface a typed, retriable
                // error instead of risking a native AV.
                if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                    return Finalize("{\"status\":\"Error\",\"code\":\"StaRequired\",\"message\":\"Runtime-SDK enumeration requires the STA thread; the in-memory index is unavailable in this state.\",\"retriable\":true}");
                source = "runtime-sdk";
                var kb = _kbService.GetKB();
                if (kb == null) return Finalize("{\"status\":\"Error\",\"message\":\"KB not open\"}");
                if (kb.DesignModel == null) return Finalize("{\"status\":\"Error\",\"message\":\"KB DesignModel is null\"}");
                var objects = kb.DesignModel.Objects;
                if (objects == null) return Finalize("{\"status\":\"Error\",\"message\":\"KB DesignModel.Objects is null\"}");

                var allObjects = ((System.Collections.IEnumerable)objects.GetAll())
                    .Cast<global::Artech.Architecture.Common.Objects.KBObject>();

                var filteredObjects = allObjects
                    .Select(obj => new RuntimeListEntry
                    {
                        Object = obj,
                        Hierarchy = ResolveHierarchy(obj),
                        TypeName = obj.TypeDescriptor?.Name ?? "Unknown",
                    });

                if (filterTypes.Count > 0)
                {
                    filteredObjects = filteredObjects.Where(x => filterTypes.Contains(x.TypeName));
                }

                if (!string.IsNullOrEmpty(nameFilter))
                {
                    filteredObjects = filteredObjects.Where(x =>
                        (x.Object.Name ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (x.Object.Description ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // v2.3.8 (Task 2.2): targeted discovery filters on the runtime-SDK fallback path.
                if (!string.IsNullOrEmpty(invokerNameFilter))
                {
                    filteredObjects = filteredObjects.Where(x =>
                        (x.Object.Name ?? string.Empty).IndexOf(invokerNameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (!string.IsNullOrEmpty(invokerDescriptionFilter))
                {
                    filteredObjects = filteredObjects.Where(x =>
                        (x.Object.Description ?? string.Empty).IndexOf(invokerDescriptionFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (!string.IsNullOrEmpty(invokerPathPrefix))
                {
                    filteredObjects = filteredObjects.Where(x =>
                    {
                        // ParentPath here is hierarchy.ParentPath (without "Root Module").
                        // Synthesize the same Root-Module-prefixed string used by ParentFolderPath.
                        var pp = x.Hierarchy.ParentPath ?? string.Empty;
                        string folderPath = string.IsNullOrEmpty(pp)
                            ? "Root Module"
                            : "Root Module/" + pp;
                        return PathPrefixMatches(folderPath, invokerPathPrefix);
                    });
                }

                if (parentPathFilter != null)
                {
                    filteredObjects = filteredObjects.Where(x => string.Equals(x.Hierarchy.ParentPath, parentPathFilter, StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrWhiteSpace(parentFilter))
                {
                    filteredObjects = filteredObjects.Where(x => string.Equals(x.Hierarchy.ParentName, parentFilter, StringComparison.OrdinalIgnoreCase));
                }

                // v2.6.8: temporal filters on the SDK fallback path. Reads can throw
                // on partially-loaded objects, so we wrap defensively and drop
                // entries that don't have a usable timestamp when a bound is in play.
                if (since > DateTime.MinValue || modifiedBefore > DateTime.MinValue)
                {
                    filteredObjects = filteredObjects.Where(x =>
                    {
                        DateTime lu;
                        try { lu = x.Object.LastUpdate; } catch { return false; }
                        if (lu <= DateTime.MinValue) return false; // no usable timestamp
                        if (since > DateTime.MinValue && lu < since) return false;
                        if (modifiedBefore > DateTime.MinValue && lu >= modifiedBefore) return false;
                        return true;
                    });
                }

                bool sortByLastUpdateRt = !string.IsNullOrEmpty(sort) &&
                    string.Equals(sort, "lastUpdate", StringComparison.OrdinalIgnoreCase);

                List<RuntimeListEntry> orderedRuntime;
                if (sortByLastUpdateRt)
                {
                    orderedRuntime = filteredObjects
                        .OrderByDescending(x => { try { return x.Object.LastUpdate; } catch { return DateTime.MinValue; } })
                        .ThenBy(x => x.Object.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    orderedRuntime = filteredObjects
                        .OrderBy(x => GetTypeSortBucket(x.TypeName))
                        .ThenBy(x => x.Object.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.TypeName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                int totalRuntime = orderedRuntime.Count;
                int startRuntime = Math.Max(0, offset);
                int pageSizeRuntime = limit <= 0 ? int.MaxValue : limit;
                int endRuntime = Math.Min(totalRuntime, (int)Math.Min((long)totalRuntime, (long)startRuntime + pageSizeRuntime));
                bool runtimeLegacyMode = IsLegacyPerfProfile();
                for (int i = startRuntime; i < endRuntime; i++)
                {
                    var item = orderedRuntime[i];
                    var runtimeParentFolderPath = string.IsNullOrEmpty(item.Hierarchy.ParentPath)
                        ? "Root Module"
                        : "Root Module/" + item.Hierarchy.ParentPath;
                    DateTime rtLastUpdate = default(DateTime);
                    DateTime rtCreatedAt = default(DateTime);
                    string rtLastModifiedBy = null;
                    try { rtLastUpdate = SdkTimestampNormalizer.NormalizeUtc(item.Object.LastUpdate); } catch { }
                    try { rtCreatedAt = SdkTimestampNormalizer.NormalizeUtc(item.Object.VersionDate); } catch { }
                    try { rtLastModifiedBy = item.Object.UserName; } catch { }

                    array.Add(BuildItemInternal(
                        item.Object.Name,
                        item.TypeName,
                        item.Object.Description,
                        item.Hierarchy.ParentName,
                        item.Hierarchy.ModuleName,
                        item.Hierarchy.Path,
                        item.Hierarchy.ParentPath,
                        runtimeParentFolderPath,
                        verbose,
                        rtLastUpdate,
                        rtCreatedAt,
                        rtLastModifiedBy,
                        runtimeLegacyMode,
                        item.Object.Guid.ToString(),
                        SafeEntityKey(item.Object),
                        SafeEntityTypeGuid(item.Object),
                        SafeEntityId(item.Object)
                    ));
                }

                return Finalize(BuildPagedResponseInternal(array, totalRuntime, startRuntime, pageSizeRuntime).ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                source = source + "-error";
                return Finalize("{\"status\":\"Error\",\"message\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}");
            }
        }

        public JObject BuildPagedResponseInternal(JArray results, int total, int offset, int pageSize)
        {
            var response = new JObject();
            response["count"] = results.Count;
            response["total"] = total;
            response["offset"] = offset;
            int consumed = offset + results.Count;
            bool hasMore = consumed < total;
            response["hasMore"] = hasMore;
            if (hasMore)
            {
                response["nextOffset"] = consumed;
            }
            response["results"] = results;

            // v2.8.0: canonical pagination block
            response["pagination"] = new JObject
            {
                ["offset"]     = offset,
                ["limit"]      = pageSize,
                ["returned"]   = results.Count,
                ["total"]      = total,
                ["hasMore"]    = hasMore,
                ["nextOffset"] = hasMore ? (JToken)(int)consumed : JValue.CreateNull()
            };

            var meta = new JObject();

            // Handle empty results: determine and attach empty_reason
            if (results.Count == 0)
            {
                string emptyReason = DetermineEmptyReason(total);
                meta["empty_reason"] = emptyReason;
            }
            else
            {
                // Non-empty results: compute and attach aggregates
                var aggregates = ComputeAggregates(results);
                if (aggregates != null)
                {
                    meta["aggregates"] = aggregates;
                }

                // Add suggested_next if we have results
                var suggestion = BuildSuggestedNext(results);
                if (suggestion != null)
                {
                    meta["suggested_next"] = suggestion;
                }

                // v2.6.8: nudge the agent toward the "what changed?" view when at
                // least one item in this page carries lifecycle data. Cheap pointer,
                // doesn't fight the default sort.
                bool hasLifecycle = results.Cast<JObject>()
                    .Any(it => it["lastUpdate"] != null);
                if (hasLifecycle)
                {
                    meta["alternative_views"] = new JObject
                    {
                        ["recently_changed"] = new JObject
                        {
                            ["tool"] = "genexus_list_objects",
                            ["args"] = new JObject
                            {
                                ["sort"] = "lastUpdate",
                                ["limit"] = 20
                            }
                        }
                    };
                }
            }

            // Only attach _meta if it has content
            if (meta.Count > 0)
            {
                response["_meta"] = meta;
            }

            return response;
        }

        private string DetermineEmptyReason(int total)
        {
            // If total is 0, no items match at all
            if (total == 0)
            {
                // Check if KB is loaded by trying to get it
                // In test contexts where _kbService is null, default to "no_matches"
                if (_kbService != null)
                {
                    var kb = _kbService.GetKB();
                    if (kb == null || kb.DesignModel == null || kb.DesignModel.Objects == null)
                    {
                        return "kb_not_loaded";
                    }
                }

                // KB is loaded but no objects match (either no filter applied or filter matched nothing)
                // We can't directly tell if a filter was applied from this context,
                // so we default to "no_matches"
                return "no_matches";
            }

            // total > 0 but results.Count == 0 means a filter was applied and filtered everything out
            return "filtered_out";
        }

        private JObject ComputeAggregates(JArray items)
        {
            if (items == null || items.Count == 0)
                return null;

            var aggregates = new JObject();

            // total: count of items in the current page result
            aggregates["total"] = items.Count;

            // Single-pass computation of by_type, lastUpdate window, and by_author
            var typeGrouping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byAuthor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            DateTime? minLu = null, maxLu = null;
            int last7 = 0;
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);

            foreach (var token in items)
            {
                if (!(token is JObject item)) continue;

                var type = (string)item["type"] ?? "Unknown";
                typeGrouping[type] = typeGrouping.TryGetValue(type, out int c) ? c + 1 : 1;

                var luTok = (string)item["lastUpdate"];
                if (!string.IsNullOrEmpty(luTok) &&
                    DateTime.TryParse(luTok, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lu))
                {
                    if (minLu == null || lu < minLu) minLu = lu;
                    if (maxLu == null || lu > maxLu) maxLu = lu;
                    if (lu >= cutoff) last7++;
                }

                var who = (string)item["lastModifiedBy"];
                if (!string.IsNullOrEmpty(who))
                {
                    byAuthor[who] = byAuthor.TryGetValue(who, out var ac) ? ac + 1 : 1;
                }
            }

            var byTypeObj = new JObject();
            foreach (var kvp in typeGrouping.OrderBy(x => x.Key))
            {
                byTypeObj[kvp.Key] = kvp.Value;
            }
            aggregates["by_type"] = byTypeObj;

            if (minLu.HasValue && maxLu.HasValue)
            {
                aggregates["lastUpdate"] = new JObject
                {
                    ["min"] = minLu.Value.ToUniversalTime().ToString("o"),
                    ["max"] = maxLu.Value.ToUniversalTime().ToString("o")
                };
                aggregates["modified_last_7d"] = last7;
            }

            if (byAuthor.Count > 0)
            {
                var byAuthorObj = new JObject();
                foreach (var kvp in byAuthor.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                    byAuthorObj[kvp.Key] = kvp.Value;
                aggregates["by_author"] = byAuthorObj;
            }

            return aggregates;
        }

        public static JObject BuildSuggestedNext(JArray items)
        {
            if (items == null || items.Count == 0)
                return null;

            var top = items[0] as JObject;
            if (top == null)
                return null;

            return new JObject
            {
                ["tool"] = "genexus_read",
                ["args"] = new JObject
                {
                    ["name"] = top["name"]?.ToString(),
                    ["type"] = top["type"]?.ToString()
                }
            };
        }

        public static JObject BuildItemForTest(string name, string type, string description, string parent, string module, string path, string parentPath, bool verbose = false)
        {
            return BuildItemInternal(name, type, description, parent, module, path, parentPath, null, verbose, default(DateTime), default(DateTime), null, IsLegacyPerfProfile());
        }

        // v2.6.8: lifecycle metadata projection helper. Returns null when both date
        // is MinValue (sentinel) and user is empty, so the caller can decide whether
        // to skip the field entirely vs. emit a sentinel.
        public static JObject BuildItemForTest(string name, string type, string description, string parent, string module, string path, string parentPath, bool verbose, DateTime lastUpdate, DateTime createdAt, string lastModifiedBy)
        {
            return BuildItemInternal(name, type, description, parent, module, path, parentPath, null, verbose, lastUpdate, createdAt, lastModifiedBy, IsLegacyPerfProfile());
        }

        // Test helper: allows tests to call BuildPagedResponse with mocked data
        // Note: This uses null for _kbService, so DetermineEmptyReason will always return "no_matches" for empty results
        public static JObject BuildPagedResponseForTest(JArray items, int total, int offset, int pageSize)
        {
            var svc = new ListService(null, null);
            return svc.BuildPagedResponseInternal(items, total, offset, pageSize);
        }

        private static JObject BuildItem(string name, string type, string description, string parent, string module, string path, string parentPath, string parentFolderPath, bool verbose = false, DateTime lastUpdate = default(DateTime), DateTime createdAt = default(DateTime), string lastModifiedBy = null)
        {
            // PERFORMANCE (perf-review): resolve the legacy-profile flag once per page
            // instead of once per item — BuildItemInternal runs for every row of every
            // list_objects response, and each Environment.GetEnvironmentVariable call
            // plus string compare per row was pure waste (200 rows = 200 env lookups).
            bool legacyMode = IsLegacyPerfProfile();
            return BuildItemInternal(name, type, description, parent, module, path, parentPath, parentFolderPath, verbose, lastUpdate, createdAt, lastModifiedBy, legacyMode);
        }

        internal static bool IsLegacyPerfProfile()
        {
            string perfProfile = Environment.GetEnvironmentVariable("MCP_PERF_PROFILE");
            return !string.IsNullOrWhiteSpace(perfProfile) &&
                   string.Equals(perfProfile, "legacy", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildItemInternal(string name, string type, string description, string parent, string module, string path, string parentPath, string parentFolderPath, bool verbose, DateTime lastUpdate, DateTime createdAt, string lastModifiedBy, bool isLegacyMode = false,
            string guid = null, string entityKey = null, string entityTypeGuid = null, int? entityId = null)
        {
            var item = new JObject();
            item["name"] = name;
            item["type"] = type;
            if (!string.IsNullOrWhiteSpace(guid)) item["guid"] = guid;

            // In legacy mode, always return full shape for backward compatibility
            if (isLegacyMode || verbose)
            {
                item["description"] = description;
                item["parent"] = parent;
                item["module"] = module;
                item["path"] = path;
                item["parentPath"] = parentPath;
                if (!string.IsNullOrWhiteSpace(entityKey)) item["entityKey"] = entityKey;
                if (!string.IsNullOrWhiteSpace(entityTypeGuid)) item["entityTypeGuid"] = entityTypeGuid;
                if (entityId.HasValue) item["entityId"] = entityId.Value;
            }
            else
            {
                item["path"] = path;
                item["parent"] = parent;
                if (!string.IsNullOrEmpty(parentPath))
                {
                    item["parentPath"] = parentPath;
                }
            }

            // v2.3.8 (Task 2.2): always expose parentFolderPath when known so the
            // agent can pathPrefix-filter the next call without round-tripping
            // through verbose mode.
            if (!string.IsNullOrEmpty(parentFolderPath))
            {
                item["parentFolderPath"] = parentFolderPath;
            }

            // v2.6.8: lifecycle metadata.
            // - lastUpdate is small and answers a real question ("what changed?"),
            //   so we emit it in default shape when known.
            // - createdAt / lastModifiedBy are verbose-only to keep default payload tight.
            if (lastUpdate > DateTime.MinValue)
            {
                item["lastUpdate"] = lastUpdate.ToUniversalTime().ToString("o");
            }
            if (isLegacyMode || verbose)
            {
                if (createdAt > DateTime.MinValue)
                {
                    item["createdAt"] = createdAt.ToUniversalTime().ToString("o");
                }
                if (!string.IsNullOrEmpty(lastModifiedBy))
                {
                    item["lastModifiedBy"] = lastModifiedBy;
                }
            }

            return item;
        }

        private static string SafeEntityKey(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.ToString(); } catch { return null; }
        }

        private static string SafeEntityTypeGuid(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.Type.ToString(); } catch { return null; }
        }

        private static int? SafeEntityId(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.Id; } catch { return null; }
        }

        private HierarchyInfo ResolveHierarchy(dynamic obj)
        {
            string parentName = string.Empty;
            string moduleName = null;
            var parentSegments = new List<string>();

            try
            {
                dynamic currentParent = obj.Parent;
                bool isImmediateParent = true;

                while (currentParent != null)
                {
                    try
                    {
                        if (currentParent.Guid == obj.Guid)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    string parentTypeName = null;
                    try { parentTypeName = currentParent.TypeDescriptor?.Name; } catch { }

                    if (string.Equals(parentTypeName, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isImmediateParent)
                        {
                            parentName = "Root Module";
                        }
                        break;
                    }

                    if (currentParent is global::Artech.Architecture.Common.Objects.Module ||
                        currentParent is global::Artech.Architecture.Common.Objects.Folder)
                    {
                        string currentName = null;
                        try { currentName = currentParent.Name; } catch { }

                        if (!string.IsNullOrWhiteSpace(currentName))
                        {
                            parentSegments.Insert(0, currentName);
                            if (isImmediateParent)
                            {
                                parentName = currentName;
                            }

                            if (moduleName == null &&
                                currentParent is global::Artech.Architecture.Common.Objects.Module)
                            {
                                moduleName = currentName;
                            }
                        }
                    }

                    currentParent = currentParent.Parent;
                    isImmediateParent = false;
                }
            }
            catch { }

            try
            {
                if (moduleName == null && obj.Module != null && obj.Module.Guid != obj.Guid)
                {
                    moduleName = obj.Module.Name;
                }
            }
            catch
            {
            }

            string parentPath = string.Join("/", parentSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
            string resolvedPath = string.IsNullOrWhiteSpace(obj.Name)
                ? parentPath
                : string.IsNullOrEmpty(parentPath) ? (string)obj.Name : parentPath + "/" + (string)obj.Name;

            return new HierarchyInfo
            {
                ParentName = parentName,
                ParentPath = parentPath,
                Path = resolvedPath,
                ModuleName = moduleName ?? string.Empty,
            };
        }

        private static string BuildIndexingMessage(GxMcp.Worker.Models.IndexState state)
        {
            string status = state?.Status ?? "Cold";
            string freshness = state?.Freshness ?? "stale";
            int? etaMs = state?.EtaMs;
            double? progress = state?.Progress;

            string etaSegment = null;
            if (etaMs.HasValue && etaMs.Value > 0)
            {
                int seconds = (int)Math.Ceiling(etaMs.Value / 1000.0);
                etaSegment = seconds <= 1 ? "~1s remaining" : $"~{seconds}s remaining";
            }

            string progressSegment = null;
            if (progress.HasValue && progress.Value > 0 && progress.Value < 1)
            {
                progressSegment = $"{(int)Math.Round(progress.Value * 100)}% complete";
            }

            string phase = !string.Equals(freshness, "current", StringComparison.OrdinalIgnoreCase) && string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase) ? "Refreshing restored index"
                : string.Equals(status, "Reindexing", StringComparison.OrdinalIgnoreCase) ? "Rebuilding index"
                : string.Equals(status, "UltraLiteReady", StringComparison.OrdinalIgnoreCase) ? "Walking KB (ultra-lite pass)"
                : string.Equals(status, "Cold", StringComparison.OrdinalIgnoreCase) ? "Building index from cold start"
                : "Building index";

            var parts = new List<string> { phase };
            if (progressSegment != null) parts.Add(progressSegment);
            if (etaSegment != null) parts.Add(etaSegment);
            return string.Join(", ", parts) + ".";
        }

        private static readonly HashSet<string> _likelyTypes = new HashSet<string>(
            new[] { "Folder", "Module", "Procedure", "Transaction", "WebPanel", "Attribute", "Table", "DataView", "Domain", "WorkPanel", "ExternalObject", "Menu", "SDPanel", "DataProvider", "SDT", "StructuredDataType", "Image" },
            StringComparer.OrdinalIgnoreCase);

        private bool IsLikelyType(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return _likelyTypes.Contains(s);
        }

        internal static int GetTypeSortBucket(string type)
        {
            if (string.Equals(type, "Folder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "Module", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return 1;
        }

        private sealed class RuntimeListEntry
        {
            public global::Artech.Architecture.Common.Objects.KBObject Object { get; set; }
            public HierarchyInfo Hierarchy { get; set; }
            public string TypeName { get; set; }
        }

        private sealed class HierarchyInfo
        {
            public string ParentName { get; set; }
            public string ParentPath { get; set; }
            public string Path { get; set; }
            public string ModuleName { get; set; }
        }

        // v2.6.8: opaque cursor. Layout is intentionally simple —
        // base64url("ts|name|guid") so it's debuggable in logs but still doesn't
        // tempt callers to parse it. Stable while sort=lastUpdate; reset when
        // the sort changes.
        //
        // Carrying Name (in addition to Guid) matches the sort tuple
        // (LastUpdate desc, Name asc, Guid asc) — the resume predicate has to use
        // the same tiebreaks the OrderBy used, or items can be silently skipped
        // when multiple entries share the same LastUpdate. See review C1.
        //
        // ts=MinValue is allowed: it lets callers paginate across the
        // "Untouched" tail of items that have no lifecycle timestamp.
        internal static string EncodeCursor(DateTime ts, string name, string guid)
        {
            // Allow null/empty guid + name only when ts is set — without ANY
            // tiebreaker we couldn't resume safely.
            if (ts <= DateTime.MinValue && string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(name))
                return null;
            string raw = ts.ToUniversalTime().ToString("o") + "|" + (name ?? string.Empty) + "|" + (guid ?? string.Empty);
            var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Back-compat wrapper kept for any out-of-tree callers; emits a cursor
        // with empty Name (will work for non-tie pages, may misorder under ties).
        internal static string EncodeCursor(DateTime ts, string guid) =>
            EncodeCursor(ts, null, guid);

        internal static (DateTime ts, string name, string guid)? DecodeCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return null;
            try
            {
                string padded = cursor.Replace('-', '+').Replace('_', '/');
                switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; }
                string raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
                var parts = raw.Split('|');
                if (parts.Length < 2) return null;
                if (!DateTime.TryParse(parts[0], null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                    return null;
                // 2-part legacy cursor: ts|guid. 3-part new cursor: ts|name|guid.
                if (parts.Length == 2)
                    return (ts, string.Empty, parts[1]);
                return (ts, parts[1], parts[2]);
            }
            catch { return null; }
        }
    }

    internal sealed class DefaultIndexEntryComparer : IComparer<SearchIndex.IndexEntry>
    {
        public static readonly DefaultIndexEntryComparer Instance = new DefaultIndexEntryComparer();

        public int Compare(SearchIndex.IndexEntry x, SearchIndex.IndexEntry y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int bucketX = ListService.GetTypeSortBucket(x.Type);
            int bucketY = ListService.GetTypeSortBucket(y.Type);
            int c = bucketX.CompareTo(bucketY);
            if (c != 0) return c;

            c = string.Compare(x.Name ?? string.Empty, y.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;

            c = string.Compare(x.Type ?? string.Empty, y.Type ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;

            return string.Compare(x.Guid ?? string.Empty, y.Guid ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class LastUpdateIndexEntryComparer : IComparer<SearchIndex.IndexEntry>
    {
        public static readonly LastUpdateIndexEntryComparer Instance = new LastUpdateIndexEntryComparer();

        public int Compare(SearchIndex.IndexEntry x, SearchIndex.IndexEntry y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int c = y.LastUpdate.CompareTo(x.LastUpdate);
            if (c != 0) return c;

            c = string.Compare(x.Name ?? string.Empty, y.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;

            return string.Compare(x.Guid ?? string.Empty, y.Guid ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    // v2.3.8 (Task 2.2): typed criteria for ListService.List. Mirrors the
    // tool-schema args of genexus_list_objects.
    public class ListCriteria
    {
        // Substring match on object NAME only.
        public string NameFilter { get; set; }
        // Substring match on object DESCRIPTION only.
        public string DescriptionFilter { get; set; }
        // Case-insensitive StartsWith over ParentFolderPath, e.g. "Root Module/ClickSign/".
        public string PathPrefix { get; set; }
        // Legacy: matches name OR description (kept for backward compatibility).
        public string Filter { get; set; }
        public string TypeFilter { get; set; }
        public int Limit { get; set; } = 200;
        public int Offset { get; set; } = 0;
        public bool Verbose { get; set; } = false;

        // v2.6.8: temporal & ordering controls.
        // - Sort: "name" (default, stable) or "lastUpdate" (descending; newest first).
        // - Since / ModifiedBefore: inclusive lower / exclusive upper bound on
        //   IndexEntry.LastUpdate. DateTime.MinValue means "no bound".
        // - Cursor: opaque token. When present, the worker uses it instead of Offset
        //   to resume a page; pairs with sort=lastUpdate for stable pagination
        //   against a mutating KB. See ListService.DecodeCursor.
        public string Sort { get; set; }
        public DateTime Since { get; set; }
        public DateTime ModifiedBefore { get; set; }
        public string Cursor { get; set; }
    }
}
