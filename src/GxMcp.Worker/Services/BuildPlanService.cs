using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Wave-3 item 30: BuildPlan synthesises a {nodes, edges, totalEstimatedSeconds}
    // payload for a target object by walking its callee graph (depth-first, bounded).
    // Per-node estimatedSeconds defaults come from a static type→seconds table because
    // the gateway-side p95 ring buffer isn't reachable from the worker; the agent can
    // override or refine by passing `toolStatsP95` in the args. ASCII viz when
    // format=ascii (parity with `genexus_analyze mode=event_flow`).
    public class BuildPlanService
    {
        private readonly IndexCacheService _indexCache;
        private readonly ObjectService _objectService;
        private readonly CallerGraphService _graph;

        // Fallback per-type estimates (seconds). Empirical defaults; agents can
        // override by passing a `toolStatsP95` JObject in the args.
        private static readonly Dictionary<string, int> DefaultEstimatesSec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Procedure", 4 },
            { "DataProvider", 4 },
            { "WebPanel", 10 },
            { "SDPanel", 10 },
            { "Transaction", 6 },
            { "WorkWithPlus", 30 },
            { "Pattern", 30 },
            { "Domain", 1 },
            { "SDT", 2 },
            { "Theme", 2 }
        };

        public BuildPlanService(IndexCacheService indexCache, ObjectService objectService, CallerGraphService graph)
        {
            _indexCache = indexCache;
            _objectService = objectService;
            _graph = graph;
        }

        public string GeneratePlan(string target, string format, JObject toolStatsP95, int maxNodes)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return McpResponse.Err(
                    code: "MissingTarget",
                    message: "target is required.",
                    hint: "Pass target=<object name>.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        "genexus_build_plan",
                        new JObject { ["target"] = "<object name>" },
                        "Provide the object name to generate a build plan for.")));
            }
            if (maxNodes <= 0 || maxNodes > 500) maxNodes = 100;

            var idx = _indexCache?.GetIndex();
            if (idx == null)
            {
                return McpResponse.Err(
                    code: "IndexNotReady",
                    message: "Search index is not ready.",
                    hint: "Run genexus_lifecycle action=index first, then retry.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        "genexus_lifecycle",
                        new JObject { ["action"] = "index" },
                        "Rebuild the index so build plan can walk the callee graph.")));
            }

            var requestedMatches = _indexCache.FindEntriesByName(target);
            if (requestedMatches.Count > 1)
            {
                return McpResponse.Err(
                    code: "BuildTargetAmbiguous",
                    message: "The build target name resolves to more than one indexed object.",
                    hint: "Pass a typed or module-qualified target before generating a build plan.",
                    target: target,
                    extra: new JObject
                    {
                        ["candidates"] = new JArray(requestedMatches.Select(e => new JObject
                        {
                            ["name"] = e.Name,
                            ["type"] = e.Type,
                            ["guid"] = e.Guid,
                            ["path"] = e.Path,
                            ["entityKey"] = e.EntityKey
                        }))
                    });
            }

            // Walk callees breadth-first. We deliberately mirror the build-order
            // semantics of `genexus_lifecycle build includeCallees=transitive`.
            var nodes = new List<JObject>();
            var edges = new List<JObject>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(target);

            int totalEstimatedSec = 0;
            bool truncated = false;

            // Index dictionary is keyed by "<Type>:<Name>"; pre-build a name→entry view.
            var byName = new Dictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in idx.Objects)
            {
                if (kvp.Value?.Name != null && !byName.ContainsKey(kvp.Value.Name))
                    byName[kvp.Value.Name] = kvp.Value;
            }

            while (queue.Count > 0 && nodes.Count < maxNodes)
            {
                string current = queue.Dequeue();
                if (!seen.Add(current)) continue;

                var currentMatches = _indexCache.FindEntriesByName(current);
                if (currentMatches.Count > 1)
                {
                    return McpResponse.Err(
                        code: "BuildDependencyAmbiguous",
                        message: "A callee name resolves to more than one indexed object.",
                        hint: "Provide a type or module-qualified identity for the dependency before building.",
                        target: current,
                        extra: new JObject
                        {
                            ["candidates"] = new JArray(currentMatches.Select(e => new JObject
                            {
                                ["name"] = e.Name,
                                ["type"] = e.Type,
                                ["guid"] = e.Guid,
                                ["path"] = e.Path,
                                ["entityKey"] = e.EntityKey
                            }))
                        });
                }

                byName.TryGetValue(current, out var entry);
                string typeName = entry?.Type ?? "Unknown";

                int est = EstimateSeconds(current, typeName, toolStatsP95);
                totalEstimatedSec += est;

                nodes.Add(new JObject
                {
                    ["name"] = current,
                    ["type"] = typeName,
                    ["estimatedSeconds"] = est
                });

                if (entry?.Calls != null)
                {
                    foreach (var callee in entry.Calls)
                    {
                        if (string.IsNullOrWhiteSpace(callee)) continue;
                        edges.Add(new JObject { ["from"] = current, ["to"] = callee });
                        if (!seen.Contains(callee)) queue.Enqueue(callee);
                    }
                }
            }
            if (queue.Count > 0) truncated = true;

            var resultPayload = new JObject
            {
                ["nodes"] = new JArray(nodes.Cast<JToken>().ToArray()),
                ["edges"] = new JArray(edges.Cast<JToken>().ToArray()),
                ["totalEstimatedSeconds"] = totalEstimatedSec,
                ["truncated"] = truncated,
                ["graphRevision"] = idx.GraphRevision,
                ["graphComplete"] = !truncated,
                ["note"] = "estimatedSeconds derived from per-type defaults; pass toolStatsP95={tool: ms} to override per node."
            };

            if (string.Equals(format, "ascii", StringComparison.OrdinalIgnoreCase))
            {
                resultPayload["ascii"] = RenderAscii(target, nodes, edges, totalEstimatedSec);
            }
            return McpResponse.Ok(target: target, code: "BuildPlanComputed", result: resultPayload);
        }

        private static int EstimateSeconds(string name, string typeName, JObject toolStatsP95)
        {
            if (toolStatsP95 != null)
            {
                // Caller can pre-inject p95 stats keyed by object name OR by tool name.
                JToken byName = toolStatsP95[name];
                if (byName != null && long.TryParse(byName.ToString(), out long msName) && msName > 0)
                {
                    return (int)Math.Max(1, msName / 1000);
                }
            }
            return DefaultEstimatesSec.TryGetValue(typeName ?? string.Empty, out int sec) ? sec : 5;
        }

        private static string RenderAscii(string target, List<JObject> nodes, List<JObject> edges, int totalSec)
        {
            var sb = new StringBuilder();
            sb.Append("BuildPlan: ").Append(target).Append("  (").Append(totalSec).Append("s estimated)").Append('\n');
            // Group: root first, then direct callees, then everything else.
            var edgesByFrom = edges.GroupBy(e => e["from"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                                   .ToDictionary(g => g.Key, g => g.Select(e => e["to"]?.ToString()).ToList(),
                                                 StringComparer.OrdinalIgnoreCase);
            var rendered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RenderNode(sb, target, nodes, edgesByFrom, rendered, prefix: "", isLast: true);
            return sb.ToString();
        }

        private static void RenderNode(StringBuilder sb, string name, List<JObject> nodes,
            Dictionary<string, List<string>> edgesByFrom, HashSet<string> rendered, string prefix, bool isLast)
        {
            if (!rendered.Add(name))
            {
                sb.Append(prefix).Append(isLast ? "└─ " : "├─ ").Append(name).Append(" (cycle)\n");
                return;
            }
            var node = nodes.FirstOrDefault(n => string.Equals(n["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
            int est = node?["estimatedSeconds"]?.ToObject<int>() ?? 0;
            string typeName = node?["type"]?.ToString() ?? "?";
            sb.Append(prefix).Append(isLast ? "└─ " : "├─ ").Append(name).Append(" (").Append(typeName).Append(", ").Append(est).Append("s)\n");
            if (!edgesByFrom.TryGetValue(name, out var children) || children.Count == 0) return;
            for (int i = 0; i < children.Count; i++)
            {
                bool last = i == children.Count - 1;
                string childPrefix = prefix + (isLast ? "   " : "│  ");
                RenderNode(sb, children[i], nodes, edgesByFrom, rendered, childPrefix, last);
            }
        }
    }
}
