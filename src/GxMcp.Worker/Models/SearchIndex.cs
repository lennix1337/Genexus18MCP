using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
            public List<string> Tags { get; set; } = new List<string>();
            public List<string> Keywords { get; set; } = new List<string>();
            
            // Graph Relationships
            public List<string> Calls { get; set; } = new List<string>();
            public List<string> CalledBy { get; set; } = new List<string>();
            public List<string> Tables { get; set; } = new List<string>();
            public List<string> Rules { get; set; } = new List<string>();
            
            // Business Intelligence fields
            public string BusinessDomain { get; set; }
            public string ConceptualSummary { get; set; }
            
            // Attribute specific
            public string DataType { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
            public bool IsFormula { get; set; }

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
    }
}
