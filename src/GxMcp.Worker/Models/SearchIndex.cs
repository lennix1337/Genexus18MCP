using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Newtonsoft.Json;

namespace GxMcp.Worker.Models
{
    public class SearchIndex
    {
        public ConcurrentDictionary<string, IndexEntry> Objects { get; set; } = new ConcurrentDictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        public DateTime LastUpdated { get; set; }

        // Derived graph consumers (CallerGraphService) use this monotonic revision
        // to publish a stable adjacency snapshot without rescanning the entire KB on
        // every callers/callees request. It is deliberately not persisted: a hydrated
        // index rebuilds its derived state and starts a fresh in-memory generation.
        [JsonIgnore]
        public long GraphRevision;

        [JsonIgnore]
        public ConcurrentDictionary<string, List<IndexEntry>> ChildrenByParent { get; set; }

        // O(1) dedup companion to ChildrenByParent: parent -> set of storage keys already in
        // that parent's list. Lets the incremental insert path skip the O(n) List.Any scan
        // (see IndexCacheService.AddOrUpdateEntryInParentIndex). Maintained under the same
        // per-list lock as ChildrenByParent and rebuilt with it; not serialized (derivable).
        [JsonIgnore]
        public ConcurrentDictionary<string, HashSet<string>> ChildKeysByParent { get; set; }

        // Fase 2: Guid → storage-key reverse map. A rename keeps the Guid stable but changes
        // the Type:Name storage key, so without this a rename leaves a stale entry under the
        // old key. Rebuilt from Objects on load/replace; not serialized (derivable).
        [JsonIgnore]
        public ConcurrentDictionary<string, string> GuidToKey { get; set; }

        // Plan 002: derived secondary indexes so SearchService/ListService can intersect a
        // candidate set instead of scanning Objects.Values when a type and/or businessDomain
        // filter is present. Normalized (case-insensitive) key -> set of storage keys. Built
        // alongside ChildrenByParent/GuidToKey in IndexCacheService.BuildParentIndex and
        // maintained by the same incremental add/remove hooks. Rebuilt from Objects on
        // load/replace; not serialized (derivable, would bloat the on-disk snapshot for no
        // benefit since it's cheap to rebuild).
        [JsonIgnore]
        public ConcurrentDictionary<string, HashSet<string>> TypeIndex { get; set; }

        [JsonIgnore]
        public ConcurrentDictionary<string, HashSet<string>> DomainIndex { get; set; }

        // PERF (perf-review): Name → storage keys multimap so SearchService's
        // `usedby:` filter doesn't scan every object to find entries by name. Unlike
        // IndexCacheService's last-write-wins _byNameIndex (single entry per name),
        // this keeps ALL entries sharing a bare Name across types (Attribute:X and
        // Domain:X both exist), preserving the semantics of the old full scan. Built
        // alongside TypeIndex/DomainIndex in BuildParentIndex and maintained by the
        // same incremental hooks; not serialized (derivable, cheap to rebuild).
        [JsonIgnore]
        public ConcurrentDictionary<string, HashSet<string>> ByNameIndex { get; set; }

        // Source token -> storage keys. Unlike the metadata indexes above this is
        // deliberately derived from FullSource and is never persisted. It lets source
        // search jump directly to likely objects while entries whose FullSource is
        // unavailable still use the conservative SDK fallback.
        [JsonIgnore]
        public ConcurrentDictionary<string, HashSet<string>> SourceTokenIndex { get; set; }

        public class IndexEntry
        {
            public string Guid { get; set; }
            // Modular objects can have a stable Guid that is not sufficient for
            // DesignModel.Objects.Get(Guid). Keep the SDK's typed identity as
            // well; EntityKey is the authoritative lookup key for those objects.
            public string EntityKey { get; set; }
            public string EntityTypeGuid { get; set; }
            public int? EntityId { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public string Description { get; set; }
            public string Parent { get; set; }
            public string ParentPath { get; set; }
            // v2.3.8 (Task 2.2): full folder path including "Root Module" prefix,
            // e.g. "Root Module/ClickSign/X". Distinct from ParentPath which omits
            // the synthetic Root Module bucket.
            public string ParentFolderPath { get; set; }
            public string Path { get; set; }
            public string Module { get; set; }
            private List<string> _tags;
            private List<string> _keywords;
            private List<string> _calls;
            private List<string> _calledBy;
            private List<string> _tables;
            private List<string> _rules;

            public List<string> Tags { get => _tags ?? (_tags = new List<string>(0)); set => _tags = value; }
            public List<string> Keywords { get => _keywords ?? (_keywords = new List<string>(0)); set => _keywords = value; }

            // Graph Relationships
            public List<string> Calls { get => _calls ?? (_calls = new List<string>(0)); set => _calls = value; }
            public List<string> CalledBy { get => _calledBy ?? (_calledBy = new List<string>(0)); set => _calledBy = value; }
            public List<string> Tables { get => _tables ?? (_tables = new List<string>(0)); set => _tables = value; }
            public List<string> Rules { get => _rules ?? (_rules = new List<string>(0)); set => _rules = value; }

            public bool ShouldSerializeTags() => _tags != null && _tags.Count > 0;
            public bool ShouldSerializeKeywords() => _keywords != null && _keywords.Count > 0;
            public bool ShouldSerializeCalls() => _calls != null && _calls.Count > 0;
            public bool ShouldSerializeCalledBy() => _calledBy != null && _calledBy.Count > 0;
            public bool ShouldSerializeTables() => _tables != null && _tables.Count > 0;
            public bool ShouldSerializeRules() => _rules != null && _rules.Count > 0;

            internal void TrimEmptyLists()
            {
                if (_tags != null && _tags.Count == 0) _tags = null;
                if (_keywords != null && _keywords.Count == 0) _keywords = null;
                if (_calls != null && _calls.Count == 0) _calls = null;
                if (_calledBy != null && _calledBy.Count == 0) _calledBy = null;
                if (_tables != null && _tables.Count == 0) _tables = null;
                if (_rules != null && _rules.Count == 0) _rules = null;
            }
            
            // Business Intelligence fields
            public string BusinessDomain { get; set; }
            public string ConceptualSummary { get; set; }
            
            // Attribute specific
            public string DataType { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
            public bool IsFormula { get; set; }
            public string Formula { get; set; }

            // Table/Transaction specific
            public string RootTable { get; set; }
            
            // v2.6.8: temporal/author metadata sourced from KBObject.LastUpdate /
            // VersionDate / UserName. UTC timestamps. Stable for sort=lastUpdate and
            // since/modifiedBefore filters in genexus_list_objects. DateTime.MinValue
            // serializes to a sentinel string but callers should treat it as "unknown".
            public DateTime LastUpdate { get; set; }
            public DateTime CreatedAt { get; set; }
            public string LastModifiedBy { get; set; }

            public bool IsEnriched { get; set; }
            public string SourceSnippet { get; set; }
            public string FullSource { get; set; }
            // Legacy snapshots may contain Rules instead of the primary source. Certify the
            // part so hydration can discard only that ambiguous derived text.
            public string FullSourcePart { get; set; }
            public int Complexity { get; set; }

            // Code metrics (Procedure/DataProvider source), extracted once at enrichment so
            // KB-wide analytics (genexus_analyze mode=code_metrics) is instant + accurate with
            // zero SDK reads. Null on non-source objects / pre-metrics index snapshots.
            public CodeMetrics Metrics { get; set; }
            public string ParmRule { get; set; }
            public float[] Embedding { get; set; }

            // PERFORMANCE (W-B1): cached storage key. Lookup site in AddOrUpdateEntryInParentIndex
            // recomputes string.Format("Type:Name") for every entry on every insert; the value
            // never changes for a given entry, so cache it lazily. [JsonIgnore] keeps the disk
            // payload unchanged.
            [JsonIgnore]
            private string _storageKey;
            [JsonIgnore]
            public string StorageKey
            {
                get { return _storageKey; }
                set { _storageKey = value; }
            }
        }

        // Compact per-object source metrics for KB-wide analytics.
        public class CodeMetrics
        {
            public int ForEach { get; set; }         // 'for each' loops
            public int NestedForEach { get; set; }   // 'for each' inside another 'for each' — optimization smell
            public int Where { get; set; }           // 'where' clauses
            public int New { get; set; }             // 'new()' insert blocks
            public int Commit { get; set; }          // explicit commit statements
            public int Calls { get; set; }           // sub/proc call statements (best-effort)
            public int Lines { get; set; }           // source line count
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
        public static SearchIndex FromJson(string json) => JsonConvert.DeserializeObject<SearchIndex>(json);

        // Stream directly to the target stream without materializing a full-index JSON string
        // on the Large Object Heap (which peaks at ~3x the in-memory index size).
        public void WriteToStream(System.IO.Stream stream, Formatting formatting = Formatting.None)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using (var sw = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false), bufferSize: 16384, leaveOpen: true))
            using (var jw = new JsonTextWriter(sw) { Formatting = formatting, CloseOutput = false })
            {
                JsonSerializer.CreateDefault().Serialize(jw, this);
                jw.Flush();
                sw.Flush();
            }
        }

        public static SearchIndex FromJsonStream(System.IO.Stream stream)
        {
            if (stream == null) return null;
            using (var sr = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16384, leaveOpen: true))
            using (var jr = new JsonTextReader(sr))
            {
                return JsonSerializer.CreateDefault().Deserialize<SearchIndex>(jr);
            }
        }

        // Pass-through for null/empty; otherwise the CLR interned instance. Used
        // for shared-vocabulary fields only (Type/Module/edge names) — never for
        // unique-per-object values (Name/Guid/Description/FullSource), which would
        // pin unbounded distinct strings in the intern pool with zero sharing.
        public static string InternShared(string value)
            => string.IsNullOrEmpty(value) ? value : string.Intern(value);

        // Memory weight: JSON deserialization mints a fresh string instance per
        // occurrence, so the same callee name repeats once per referencing edge
        // (thousands of copies for hub objects). Collapse each distinct shared-
        // vocabulary value to one instance after load. New lists are assigned
        // (never mutated in place) to honor the entry copy-on-write discipline.
        // Call before publishing (both snapshot load paths); AddEdgeCow interns
        // runtime additions, UpdateEntry interns creation-time scalars.
        public static void InternSharedStrings(SearchIndex index)
        {
            if (index?.Objects == null) return;
            foreach (var kv in index.Objects)
            {
                var entry = kv.Value;
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.StorageKey) && !string.IsNullOrEmpty(kv.Key))
                    entry.StorageKey = kv.Key;
                entry.Type = InternShared(entry.Type);
                entry.EntityTypeGuid = InternShared(entry.EntityTypeGuid);
                entry.LastModifiedBy = InternShared(entry.LastModifiedBy);
                entry.FullSourcePart = InternShared(entry.FullSourcePart);
                entry.Module = InternShared(entry.Module);
                entry.Parent = InternShared(entry.Parent);
                entry.ParentPath = InternShared(entry.ParentPath);
                entry.ParentFolderPath = InternShared(entry.ParentFolderPath);
                entry.Path = InternShared(entry.Path);
                entry.RootTable = InternShared(entry.RootTable);
                entry.DataType = InternShared(entry.DataType);
                entry.TrimEmptyLists();
                if (entry.ShouldSerializeCalls()) entry.Calls = InternEdgeList(entry.Calls);
                if (entry.ShouldSerializeCalledBy()) entry.CalledBy = InternEdgeList(entry.CalledBy);
                if (entry.ShouldSerializeTables()) entry.Tables = InternEdgeList(entry.Tables);
                if (entry.ShouldSerializeRules()) entry.Rules = InternEdgeList(entry.Rules);
                if (entry.ShouldSerializeKeywords()) entry.Keywords = InternEdgeList(entry.Keywords);
                if (entry.ShouldSerializeTags()) entry.Tags = InternEdgeList(entry.Tags);
            }
        }

        private static List<string> InternEdgeList(List<string> list)
        {
            if (list == null || list.Count == 0) return list;
            bool changed = false;
            var next = new List<string>(list.Count);
            foreach (var item in list)
            {
                string interned = InternShared(item);
                if (!changed && !ReferenceEquals(item, interned)) changed = true;
                next.Add(interned);
            }
            return changed ? next : list;
        }

        private List<IndexEntry> ResolveKeys(HashSet<string> keys)
        {
            if (keys == null || Objects == null) return new List<IndexEntry>(0);
            lock (keys)
            {
                var results = new List<IndexEntry>(keys.Count);
                foreach (var k in keys)
                {
                    if (Objects.TryGetValue(k, out var entry) && entry != null)
                        results.Add(entry);
                }
                return results;
            }
        }

        /// <summary>
        /// Finds all objects with the given bare name.
        /// Uses ByNameIndex (O(1)) when available, otherwise falls back to scanning Objects.Values.
        /// </summary>
        public List<IndexEntry> FindByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Objects == null)
                return new List<IndexEntry>(0);

            string trimmed = name.Trim();
            if (ByNameIndex != null)
            {
                return ByNameIndex.TryGetValue(trimmed, out var keys)
                    ? ResolveKeys(keys)
                    : new List<IndexEntry>(0);
            }

            return Objects.Values
                .Where(e => e != null && string.Equals(e.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Direct children of every container named <paramref name="parentName"/>, or null when
        /// ChildrenByParent has not been built (callers then scan Objects by <c>Parent</c>).
        /// ChildrenByParent is keyed by the full parent path ("Root Module/A/B"), so a bare
        /// name equals a key only for a top-level container; a nested Folder is found by the
        /// last segment of its key. Entries are kept only when their <c>Parent</c> is that
        /// name, matching the full-scan fallback. The empty key holds root entries that carry
        /// no ParentPath, so it is checked as well.
        /// </summary>
        public List<IndexEntry> FindChildrenByParentName(string parentName)
        {
            if (ChildrenByParent == null) return null;
            var results = new List<IndexEntry>();
            if (string.IsNullOrWhiteSpace(parentName)) return results;

            string name = parentName.Trim();
            foreach (var bucket in ChildrenByParent)
            {
                string key = bucket.Key ?? string.Empty;
                string lastSegment = key.Substring(key.LastIndexOf('/') + 1);
                if (key.Length > 0 && !string.Equals(lastSegment, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                lock (bucket.Value)
                {
                    results.AddRange(bucket.Value.Where(e =>
                        e != null && string.Equals(e.Parent, name, StringComparison.OrdinalIgnoreCase)));
                }
            }
            return results;
        }

        /// <summary>
        /// Finds an object by its Guid.
        /// Uses GuidToKey (O(1)) when available, otherwise falls back to scanning Objects.Values.
        /// </summary>
        public IndexEntry FindByGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid) || Objects == null) return null;
            string trimmed = guid.Trim();
            if (GuidToKey != null && GuidToKey.TryGetValue(trimmed, out var key) && key != null)
            {
                if (Objects.TryGetValue(key, out var entry) && entry != null)
                    return entry;
            }
            return Objects.Values.FirstOrDefault(e => e != null && string.Equals(e.Guid, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds a cached object by its native EntityKey. The watcher uses this
        /// read-only fallback when the SDK no longer returns an object for a key,
        /// so a deletion can be mirrored without guessing from a bare name.
        /// </summary>
        public IndexEntry FindByEntityKey(string entityKey)
        {
            if (string.IsNullOrWhiteSpace(entityKey) || Objects == null) return null;
            string trimmed = entityKey.Trim();
            return Objects.Values.FirstOrDefault(e => e != null && string.Equals(e.EntityKey, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Checks whether any object with the given name exists.
        /// </summary>
        public bool ContainsName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Objects == null) return false;
            string trimmed = name.Trim();
            if (ByNameIndex != null)
            {
                return ByNameIndex.ContainsKey(trimmed);
            }
            return Objects.Values.Any(e => e != null && string.Equals(e.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds all objects of the specified type.
        /// Uses TypeIndex (O(1)) when available, otherwise falls back to scanning Objects.Values.
        /// </summary>
        public List<IndexEntry> FindByType(string type)
        {
            if (string.IsNullOrWhiteSpace(type) || Objects == null)
                return new List<IndexEntry>(0);

            string trimmed = type.Trim();
            if (TypeIndex != null)
            {
                return TypeIndex.TryGetValue(trimmed, out var keys)
                    ? ResolveKeys(keys)
                    : new List<IndexEntry>(0);
            }

            return Objects.Values
                .Where(e => e != null && string.Equals(e.Type, trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Finds all objects matching any of the specified types.
        /// Uses TypeIndex (O(1)) when available, otherwise falls back to scanning Objects.Values.
        /// </summary>
        public List<IndexEntry> FindByTypes(IEnumerable<string> types)
        {
            if (types == null || Objects == null)
                return new List<IndexEntry>(0);

            var typeList = types.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (typeList.Count == 0) return new List<IndexEntry>(0);

            if (TypeIndex != null)
            {
                var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in typeList)
                {
                    if (TypeIndex.TryGetValue(t, out var keys) && keys != null)
                    {
                        lock (keys) { uniqueKeys.UnionWith(keys); }
                    }
                }
                return ResolveKeys(uniqueKeys);
            }

            var typeSet = new HashSet<string>(typeList, StringComparer.OrdinalIgnoreCase);
            return Objects.Values
                .Where(e => e != null && !string.IsNullOrEmpty(e.Type) && typeSet.Contains(e.Type))
                .ToList();
        }

        /// <summary>
        /// Finds all objects of the specified business domain.
        /// Uses DomainIndex (O(1)) when available, otherwise falls back to scanning Objects.Values.
        /// </summary>
        public List<IndexEntry> FindByDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain) || Objects == null)
                return new List<IndexEntry>(0);

            string trimmed = domain.Trim();
            if (DomainIndex != null)
            {
                return DomainIndex.TryGetValue(trimmed, out var keys)
                    ? ResolveKeys(keys)
                    : new List<IndexEntry>(0);
            }

            return Objects.Values
                .Where(e => e != null && string.Equals(e.BusinessDomain, trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
