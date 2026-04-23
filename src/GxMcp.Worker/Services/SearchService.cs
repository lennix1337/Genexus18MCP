using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class SearchService
    {
        private readonly IndexCacheService _indexCacheService;
        private readonly VectorService _vectorService = new VectorService();
        private static readonly ConcurrentDictionary<string, string> _queryCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastIndexTime = DateTime.MinValue;

        public SearchService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        public string Search(string query, string typeFilter = null, string domainFilter = null, int limit = 50)
        {
            try
            {
                 if (_indexCacheService.IsIndexMissing && !_indexCacheService.IsScanning) 
                     return "{\"error\": \"Index missing. Please run genexus_lifecycle(action='index') to build the search index before using this tool.\"}";
 
                 var index = _indexCacheService.GetIndex();
                 if (index == null || index.Objects.Count == 0) {
                     if (_indexCacheService.IsScanning) return "{\"count\": 0, \"results\": [], \"info\": \"Indexing in progress...\"}";
                     return "{\"error\": \"Index empty.\"}";
                 }

                if (index.LastUpdated > _lastIndexTime) { _queryCache.Clear(); _lastIndexTime = index.LastUpdated; }

                bool isQuick = !string.IsNullOrEmpty(query) && query.IndexOf("@quick", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isQuick)
                {
                    query = Regex.Replace(query, @"\s*@quick\b", "", RegexOptions.IgnoreCase).Trim();
                }

                string cacheKey = string.Format("{0}|{1}|{2}|{3}|{4}", query ?? "", typeFilter ?? "", domainFilter ?? "", limit, isQuick ? "quick" : "full");
                if (_queryCache.TryGetValue(cacheKey, out var cached)) return cached;

                var criteria = ParseQuery(query);
                if (!string.IsNullOrEmpty(typeFilter)) criteria.TypeFilter = typeFilter;
                if (!string.IsNullOrEmpty(domainFilter)) criteria.DomainFilter = domainFilter;

                IEnumerable<SearchIndex.IndexEntry> sourceSet = null;

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
                }

                var queryResults = sourceSet.AsParallel();

                if (!string.IsNullOrEmpty(criteria.TypeFilter))
                    queryResults = queryResults.Where(e => IsTypeMatch(e.Type, criteria.TypeFilter));
                
                if (!string.IsNullOrEmpty(criteria.DomainFilter))
                    queryResults = queryResults.Where(e => string.Equals(e.BusinessDomain, criteria.DomainFilter, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(criteria.DescriptionFilter))
                    queryResults = queryResults.Where(e => (e.Description ?? "").IndexOf(criteria.DescriptionFilter, StringComparison.OrdinalIgnoreCase) >= 0);

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

                if (!string.IsNullOrEmpty(criteria.UsedByFilter))
                {
                    queryResults = queryResults.Where(e => 
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
                            score = CalculateSemanticScore(entry, criteria.Terms);

                            // SHORT-CIRCUIT: If exact keyword match failed entirely and it's a structural container, skip vector math entirely
                            if (score <= 0 && (entry.Type == "Folder" || entry.Type == "Module"))
                                return new RankedResult { Score = -1 };

                            if (!isQuick && entry.Embedding != null && queryEmbedding != null)
                            {
                                vectorScore = _vectorService.CosineSimilarity(queryEmbedding, entry.Embedding);
                            }
                            if (!isQuick && score <= 0 && vectorScore < 0.45f)
                                return new RankedResult { Score = -1 };
                        }
                        else
                        {
                            score = (entry.Type == "Folder" || entry.Type == "Module") ? 1000 : 1; // Default browsing order
                        }

                        int finalScore = score + (int)(vectorScore * 1000);
                        return new RankedResult { Entry = entry, Score = finalScore, VectorSimilarity = vectorScore };
                    })
                    .Where(r => r != null) // Safety check
                    .Where(r => r.Score > 0)
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => r.Entry.Name)
                    .ToList();

                int total = rankedAll.Count;
                var effectiveLimit = limit <= 0 ? total : limit;
                var scoredResults = rankedAll.Take(effectiveLimit).ToList();
                bool hasMore = total > scoredResults.Count;

                string json;
                if (isQuick)
                {
                    json = Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        count = scoredResults.Count,
                        total,
                        hasMore,
                        results = scoredResults.Select(r => new {
                            guid = r.Entry.Guid,
                            name = r.Entry.Name,
                            type = r.Entry.Type,
                            parent = r.Entry.Parent,
                            module = r.Entry.Module,
                            path = r.Entry.Path,
                            parentPath = r.Entry.ParentPath
                        })
                    });
                }
                else
                {
                    json = Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        count = scoredResults.Count,
                        total,
                        hasMore,
                        results = scoredResults.Select(r => new {
                            guid = r.Entry.Guid,
                            name = r.Entry.Name,
                            type = r.Entry.Type,
                            description = r.Entry.Description,
                            parm = r.Entry.ParmRule,
                            snippet = r.Entry.SourceSnippet,
                            parent = r.Entry.Parent,
                            module = r.Entry.Module,
                            path = r.Entry.Path,
                            parentPath = r.Entry.ParentPath,
                            dataType = r.Entry.DataType,
                            length = r.Entry.Length,
                            decimals = r.Entry.Decimals,
                            table = r.Entry.RootTable,
                            similarity = r.VectorSimilarity
                        })
                    });
                }

                _queryCache.TryAdd(cacheKey, json);

                if (!isQuick && criteria.Terms.Count > 0 && scoredResults.Count > 0)
                {
                    var topGuids = scoredResults.Take(5)
                        .Where(r => !string.IsNullOrEmpty(r.Entry.Guid))
                        .Select(r => new Guid(r.Entry.Guid))
                        .ToList();

                    Program.BackgroundQueue.Enqueue(() => {
                        try {
                            var kb = _indexCacheService.KbService?.GetKB();
                            if (kb == null) return;
                            foreach (var guid in topGuids) {
                                var obj = kb.DesignModel.Objects.Get(guid);
                                if (obj != null) Logger.Debug($"[Warm-up] Loaded {obj.Name} into SDK cache.");
                            }
                        } catch { }
                    });
                }

                return json;
            }
            catch (Exception ex) { return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        private int CalculateSemanticScore(SearchIndex.IndexEntry entry, HashSet<string> terms)
        {
            int score = 0;
            string name = entry.Name ?? "";
            string desc = entry.Description ?? "";
            
            foreach (var term in terms) {
                if (name.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 2000;
                else if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 1000;
                else if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 500;
                
                if (desc.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 300;

                if (entry.Keywords != null && entry.Keywords.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 800;
                if (entry.Tags != null && entry.Tags.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 800;

                if (entry.Tables != null && entry.Tables.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 400;
                if (entry.Calls != null && entry.Calls.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 400;
            }
            return score;
        }

        private bool IsTypeMatch(string type, string query)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(query)) return false;
            string t = type.ToLower(); string q = query.ToLower();
            if (q == "prc" || q == "procedure" || q == "proc") return t.Contains("procedure");
            if (q == "trn" || q == "transaction") return t.Contains("transaction");
            if (q == "tab" || q == "table") return t == "table";
            if (q == "wp" || q == "webpanel") return t.Contains("webpanel");
            if (q == "dp" || q == "dataprovider") return t.Contains("dataprovider");
            if (q == "sdt") return t.Contains("sdt");
            if (q == "attr" || q == "attribute") return t.Contains("attribute");
            return t.Contains(q);
        }

        private SearchCriteria ParseQuery(string query)
        {
            var c = new SearchCriteria();
            if (string.IsNullOrEmpty(query)) return c;

            query = ExtractFilter(query, "description", value => c.DescriptionFilter = value);
            query = ExtractFilter(query, "metadata", value => c.MetadataFilter = value);
            query = ExtractFilter(query, "usedby", value => c.UsedByFilter = value);
            query = ExtractFilter(query, "parentPath", value => c.ParentPathFilter = value);
            query = ExtractFilter(query, "parent", value => c.ParentFilter = value);
            query = ExtractFilter(query, "type", value => c.TypeFilter = value);

            foreach (var part in query.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries)) {
                c.Terms.Add(part.ToLowerInvariant());
            }
            return c;
        }

        private string ExtractFilter(string query, string filterName, Action<string> assign)
        {
            var pattern = string.Format(@"(?:^|\s){0}:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", Regex.Escape(filterName));
            var match = Regex.Match(query, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return query;

            var value = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value;

            if (!string.IsNullOrWhiteSpace(value))
            {
                assign(value);
            }

            return query.Remove(match.Index, match.Length).Trim();
        }

        private class RankedResult { public SearchIndex.IndexEntry Entry { get; set; } public int Score { get; set; } public float VectorSimilarity { get; set; } }
        private class SearchCriteria { 
            public string TypeFilter { get; set; } public string ParentFilter { get; set; } public string ParentPathFilter { get; set; }
            public string UsedByFilter { get; set; } public string DomainFilter { get; set; } 
            public string DescriptionFilter { get; set; } public string MetadataFilter { get; set; }
            public HashSet<string> Terms { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase); 
        }
    }
}
