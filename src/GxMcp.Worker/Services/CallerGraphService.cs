using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // v2.3.8 (Task 1.3): unified caller/callee graph navigation backed by the
    // search index. Previously the impact-analysis BFS lived inline in
    // AnalyzeService.ImpactAnalysis and a parallel KB-level scan lived in
    // AnalyzeService.Inspect (via obj.GetReferencesTo()). This service is the
    // single source of truth for index-based graph queries; AnalyzeService and
    // BuildService delegate into it in later tasks (1.4, 5.1).
    public class TransitiveResult
    {
        public List<string> Nodes { get; set; } = new List<string>();
        public bool Truncated { get; set; }
        public int Depth { get; set; }
    }

    public class CallerGraphService
    {
        private readonly IndexCacheService _index;
        private readonly object _adjacencyGate = new object();
        private SearchIndex _adjacencyIndex;
        private long _adjacencyRevision = -1;
        private GraphAdjacency _adjacency;

        // The index already stores both directions of most SDK edges, but callers and
        // callees are queried by name. Keeping a derived, immutable adjacency snapshot
        // turns each query from an O(objects + source) scan into an O(degree) lookup.
        // The snapshot is rebuilt only after IndexCacheService advances GraphRevision.
        private sealed class GraphAdjacency
        {
            public readonly Dictionary<string, List<string>> Callers =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<string>> Callees =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        public CallerGraphService(IndexCacheService index)
        {
            _index = index;
        }

        // Backwards-compatible ctor matching the plan's signature
        // (ObjectService is currently unused for index-based callers; kept for
        // future expansion when we may want a KB fallback for unindexed objects).
        public CallerGraphService(IndexCacheService index, ObjectService objectService)
        {
            _index = index;
        }

        // Returns the names of objects whose SourceSnippet contains a call site
        // to targetName, OR whose pre-computed CalledBy entry references it.
        // Prefers the inverted index (CalledBy) when present (fast path) and
        // falls back to a regex scan over SourceSnippet (covers cases where the
        // SDK reference walker missed the callsite — see FR#3 in IndexCacheService).
        public List<string> GetCallers(string targetName)
        {
            if (string.IsNullOrEmpty(targetName) || _index == null) return new List<string>();
            var idx = _index.GetIndex();
            if (idx == null) return new List<string>();
            // A bare name is not a graph identity. If multiple typed entities
            // share it, fail closed instead of merging unrelated caller sets.
            if (FindEntriesByName(idx, targetName).Count != 1) return new List<string>();

            var adjacency = GetAdjacency(idx);
            if (!adjacency.Callers.TryGetValue(targetName, out var callers))
                return new List<string>();
            return new List<string>(callers);
        }

        // Direct callees of objectName. Uses the entry's Calls list (the unified
        // forward edge — populated both from SDK references and from textual
        // scanning in IndexCacheService).
        public List<string> GetCallees(string objectName)
        {
            if (string.IsNullOrEmpty(objectName) || _index == null) return new List<string>();
            var idx = _index.GetIndex();
            if (idx == null) return new List<string>();
            if (FindEntriesByName(idx, objectName).Count != 1) return new List<string>();

            var adjacency = GetAdjacency(idx);
            if (!adjacency.Callees.TryGetValue(objectName, out var callees))
                return new List<string>();
            return new List<string>(callees);
        }

        private static List<SearchIndex.IndexEntry> FindEntriesByName(SearchIndex index, string name)
        {
            return index?.Objects?.Values
                .Where(entry => entry != null
                    && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList()
                ?? new List<SearchIndex.IndexEntry>();
        }

        private GraphAdjacency GetAdjacency(SearchIndex index)
        {
            long revision = System.Threading.Interlocked.Read(ref index.GraphRevision);
            var current = _adjacency;
            if (ReferenceEquals(_adjacencyIndex, index) && current != null && _adjacencyRevision == revision)
                return current;

            lock (_adjacencyGate)
            {
                revision = System.Threading.Interlocked.Read(ref index.GraphRevision);
                if (ReferenceEquals(_adjacencyIndex, index) && _adjacency != null && _adjacencyRevision == revision)
                    return _adjacency;

                var rebuilt = BuildAdjacency(index);
                _adjacencyIndex = index;
                _adjacencyRevision = revision;
                _adjacency = rebuilt;
                return rebuilt;
            }
        }

        private static GraphAdjacency BuildAdjacency(SearchIndex index)
        {
            var adjacency = new GraphAdjacency();
            var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var callerSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var calleeSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (index?.Objects == null) return adjacency;

            foreach (var entry in index.Objects.Values)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.Name)) knownNames.Add(entry.Name);
            }

            foreach (var entry in index.Objects.Values)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;

                if (entry.CalledBy != null)
                {
                    foreach (var caller in entry.CalledBy)
                        AddEdge(callerSets, entry.Name, caller);
                }

                if (entry.Calls != null)
                {
                    foreach (var callee in entry.Calls)
                    {
                        AddEdge(calleeSets, entry.Name, callee);
                        AddEdge(callerSets, callee, entry.Name);
                    }
                }

                // Keep the old textual fallback semantics, but pay the regex cost once
                // per entry instead of once per GetCallers target. Only identifiers that
                // resolve to a known indexed object become graph edges.
                if (!string.IsNullOrEmpty(entry.SourceSnippet))
                {
                    foreach (Match match in Regex.Matches(entry.SourceSnippet, @"\b(\w+)\s*\(", RegexOptions.IgnoreCase))
                    {
                        string called = match.Groups[1].Value;
                        if (!knownNames.Contains(called)) continue;
                        if (string.Equals(called, entry.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        AddEdge(calleeSets, entry.Name, called);
                        AddEdge(callerSets, called, entry.Name);
                    }
                }
            }

            foreach (var pair in callerSets)
                adjacency.Callers[pair.Key] = pair.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var pair in calleeSets)
                adjacency.Callees[pair.Key] = pair.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            return adjacency;
        }

        private static void AddEdge(Dictionary<string, HashSet<string>> graph, string key, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
            if (!graph.TryGetValue(key, out var values))
            {
                values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                graph[key] = values;
            }
            values.Add(value);
        }

        // v2.6.6 Stream E (FR#8): when a Transaction has BC enabled the GeneXus
        // compiler emits an <name>_bc class that must be built alongside the
        // Transaction itself. The CallerGraph BFS misses this because the _bc
        // variant is not a callee — it's a sibling compile unit. This helper
        // returns the implicit BC variant targets the build expansion should
        // prepend (so the _bc compiles before the trn that consumes it).
        //
        // Heuristic (no HasBc field on IndexEntry yet):
        //   1. <transactionName> exists in the index as Type=Transaction, AND
        //   2. <transactionName>_bc exists in the index (any type).
        // Returns the bc variant name(s); empty list when no match.
        public List<string> GetBcVariantTargets(string transactionName)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(transactionName) || _index == null) return result;
            try
            {
                var idx = _index.GetIndex();
                if (idx?.Objects == null) return result;

                // Find the requested name (case-insensitive).
                SearchIndex.IndexEntry trn = null;
                foreach (var v in idx.Objects.Values)
                {
                    if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                    if (string.Equals(v.Name, transactionName, StringComparison.OrdinalIgnoreCase))
                    {
                        trn = v;
                        break;
                    }
                }
                if (trn == null) return result;
                if (!string.Equals(trn.Type, "Transaction", StringComparison.OrdinalIgnoreCase)) return result;

                string bcName = transactionName + "_bc";
                foreach (var v in idx.Objects.Values)
                {
                    if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                    if (string.Equals(v.Name, bcName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(v.Name);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                GxMcp.Worker.Helpers.Logger.Warn(
                    "[GetBcVariantTargets] lookup failed for '" + transactionName + "': " + ex.Message);
            }
            return result;
        }

        // BFS over callers (reverse edges), capped at maxNodes (exclusive of the root). Cycle-safe.
        // v2.3.8 (Task 1.4): symmetric to GetCalleesTransitive. AnalyzeService.ImpactAnalysis
        // previously inlined this BFS over CalledBy; it now delegates here.
        public TransitiveResult GetCallersTransitive(string root, int maxNodes = 200)
            => GetCallersTransitive(root, maxNodes, System.Threading.CancellationToken.None);

        public TransitiveResult GetCallersTransitive(string root, int maxNodes, System.Threading.CancellationToken ct)
        {
            var result = new TransitiveResult();
            if (string.IsNullOrEmpty(root) || maxNodes <= 0) return result;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Name, int Depth)>();
            queue.Enqueue((root, 0));
            visited.Add(root);

            int maxDepth = 0;

            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested) { result.Truncated = true; result.Depth = maxDepth; return result; }
                var (name, d) = queue.Dequeue();
                maxDepth = Math.Max(maxDepth, d);

                foreach (var caller in GetCallers(name))
                {
                    if (string.IsNullOrEmpty(caller)) continue;
                    if (!visited.Add(caller)) continue;

                    result.Nodes.Add(caller);
                    if (result.Nodes.Count >= maxNodes)
                    {
                        result.Truncated = true;
                        result.Depth = Math.Max(maxDepth, d + 1);
                        return result;
                    }
                    queue.Enqueue((caller, d + 1));
                }

                if (visited.Count > 0 && visited.Count % 25 == 0)
                {
                    GxMcp.Worker.Helpers.ProgressEmitter.Emit(
                        progress: System.Math.Min(95, visited.Count),
                        total: System.Math.Max(100, visited.Count + queue.Count),
                        message: "Impact analysis: " + visited.Count + " visited, " + queue.Count + " pending");
                }
            }

            result.Depth = maxDepth;
            return result;
        }

        // BFS over callees, capped at maxNodes (exclusive of the root). Cycle-safe.
        public TransitiveResult GetCalleesTransitive(string root, int maxNodes = 200)
            => GetCalleesTransitive(root, maxNodes, System.Threading.CancellationToken.None);

        public TransitiveResult GetCalleesTransitive(string root, int maxNodes, System.Threading.CancellationToken ct)
        {
            var result = new TransitiveResult();
            if (string.IsNullOrEmpty(root) || maxNodes <= 0) return result;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Name, int Depth)>();
            queue.Enqueue((root, 0));
            visited.Add(root);

            int maxDepth = 0;

            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested) { result.Truncated = true; result.Depth = maxDepth; return result; }
                var (name, d) = queue.Dequeue();
                maxDepth = Math.Max(maxDepth, d);

                foreach (var callee in GetCallees(name))
                {
                    if (string.IsNullOrEmpty(callee)) continue;
                    if (!visited.Add(callee)) continue;

                    result.Nodes.Add(callee);
                    if (result.Nodes.Count >= maxNodes)
                    {
                        result.Truncated = true;
                        result.Depth = Math.Max(maxDepth, d + 1);
                        return result;
                    }
                    queue.Enqueue((callee, d + 1));
                }

                if (visited.Count > 0 && visited.Count % 25 == 0)
                {
                    GxMcp.Worker.Helpers.ProgressEmitter.Emit(
                        progress: System.Math.Min(95, visited.Count),
                        total: System.Math.Max(100, visited.Count + queue.Count),
                        message: "Impact analysis: " + visited.Count + " visited, " + queue.Count + " pending");
                }
            }

            result.Depth = maxDepth;
            return result;
        }
    }
}
