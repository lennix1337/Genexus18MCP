using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3 item 30: BuildPlanService walks the callee graph of a target and
    // emits {nodes, edges, totalEstimatedSeconds}. These tests pin the envelope
    // shape, estimate fallback for unknown types, and ASCII viz emission.
    public class BuildPlanServiceTests
    {
        private static IndexCacheService BuildIndex(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            svc.MarkIndexComplete(System.Linq.Enumerable.Count(entries));
            return svc;
        }

        private static BuildPlanService BuildSvc(IndexCacheService idx)
        {
            return new BuildPlanService(idx, objectService: null, graph: new CallerGraphService(idx));
        }

        [Fact]
        public void GeneratePlan_WalksCalleeGraph_AndProducesNodesAndEdges()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "RootProc", Type = "Procedure", Calls = new List<string> { "ChildA", "ChildB" }, CalledBy = new List<string>() },
                new SearchIndex.IndexEntry { Name = "ChildA", Type = "Procedure", Calls = new List<string>(), CalledBy = new List<string> { "RootProc" } },
                new SearchIndex.IndexEntry { Name = "ChildB", Type = "WebPanel", Calls = new List<string>(), CalledBy = new List<string> { "RootProc" } }
            };
            var idx = BuildIndex(entries);
            var svc = BuildSvc(idx);

            string json = svc.GeneratePlan("RootProc", format: null, toolStatsP95: null, maxNodes: 100);
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            var nodes = (JArray)obj["result"]!["nodes"]!;
            var edges = (JArray)obj["result"]!["edges"]!;
            Assert.Equal(3, nodes.Count);
            Assert.Equal(2, edges.Count);
            Assert.True(obj["result"]!["totalEstimatedSeconds"]!.ToObject<int>() > 0);
            // Procedure default 4s + Procedure 4s + WebPanel 10s = 18.
            Assert.Equal(4 + 4 + 10, obj["result"]!["totalEstimatedSeconds"]!.ToObject<int>());
        }

        [Fact]
        public void GeneratePlan_AsciiFormat_EmitsRenderedTree()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "X", Type = "Procedure", Calls = new List<string> { "Y" }, CalledBy = new List<string>() },
                new SearchIndex.IndexEntry { Name = "Y", Type = "Procedure", Calls = new List<string>(), CalledBy = new List<string> { "X" } }
            };
            var svc = BuildSvc(BuildIndex(entries));
            string json = svc.GeneratePlan("X", format: "ascii", toolStatsP95: null, maxNodes: 100);
            var obj = JObject.Parse(json);
            string ascii = obj["result"]?["ascii"]?.ToString() ?? "";
            Assert.Contains("BuildPlan: X", ascii);
            Assert.Contains("X (Procedure", ascii);
            Assert.Contains("Y (Procedure", ascii);
        }

        [Fact]
        public void GeneratePlan_MissingTarget_ReturnsErrorEnvelope()
        {
            var svc = BuildSvc(BuildIndex(new SearchIndex.IndexEntry[0]));
            string json = svc.GeneratePlan(target: "", format: null, toolStatsP95: null, maxNodes: 100);
            var obj = JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("MissingTarget", obj["error"]?["code"]?.ToString());
        }

        [Fact]
        public void GeneratePlan_ToolStatsP95Override_DerivesEstimateFromP95Ms()
        {
            // Override per-name p95 in ms; estimate = ms/1000 floored.
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "Heavy", Type = "Procedure", Calls = new List<string>(), CalledBy = new List<string>() }
            };
            var svc = BuildSvc(BuildIndex(entries));
            var p95 = new JObject { ["Heavy"] = 30000 }; // 30 seconds
            string json = svc.GeneratePlan("Heavy", format: null, toolStatsP95: p95, maxNodes: 100);
            var obj = JObject.Parse(json);
            Assert.Equal(30, obj["result"]?["totalEstimatedSeconds"]?.ToObject<int>());
        }

        [Fact]
        public void GeneratePlan_RefusesAmbiguousTargetWithCandidates()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "Shared", Type = "Procedure", Guid = "p1" },
                new SearchIndex.IndexEntry { Name = "Shared", Type = "WebPanel", Guid = "w1" }
            };
            var svc = BuildSvc(BuildIndex(entries));

            var obj = JObject.Parse(svc.GeneratePlan("Shared", format: null, toolStatsP95: null, maxNodes: 100));

            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("BuildTargetAmbiguous", obj["error"]?["code"]?.ToString());
            Assert.Equal(2, obj["candidates"]?.ToObject<JArray>()?.Count);
        }

        [Fact]
        public void GeneratePlan_ReportsGraphRevisionAndCompleteness()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "Root", Type = "Procedure", Calls = new List<string> { "Child" } },
                new SearchIndex.IndexEntry { Name = "Child", Type = "Procedure" }
            };
            var svc = BuildSvc(BuildIndex(entries));

            var obj = JObject.Parse(svc.GeneratePlan("Root", format: null, toolStatsP95: null, maxNodes: 100));

            Assert.True(obj["result"]?["graphRevision"]?.ToObject<long>() > 0);
            Assert.True(obj["result"]?["graphComplete"]?.ToObject<bool>() == true);
        }
    }

    // Wave-3 item 87 — dependency_heatmap composite scoring.
    public class DependencyHeatmapTests
    {
        private static IndexCacheService BuildIndex(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            svc.MarkIndexComplete(System.Linq.Enumerable.Count(entries));
            return svc;
        }

        [Fact]
        public void DependencyHeatmap_ReturnsRankedObjects_ByCompositeScore()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "Hot",  Type = "Procedure",  Guid = "g-hot",
                    Calls = new List<string> { "A", "B" }, CalledBy = new List<string> { "X", "Y", "Z" } },
                new SearchIndex.IndexEntry { Name = "Cool", Type = "Procedure",  Guid = "g-cool",
                    Calls = new List<string> { "A" },           CalledBy = new List<string>() }
            };
            var svc = new AnalyzeService(BuildIndex(entries), objSvc: null, graph: null);

            string json = svc.DependencyHeatmap(kbPath: null, format: null);
            var obj = JObject.Parse(json);
            Assert.Equal("ok", obj["status"]?.ToString());
            var objects = (JArray)obj["result"]!["objects"]!;
            Assert.True(objects.Count >= 2);
            Assert.Equal("Hot", objects[0]["name"]?.ToString());
            Assert.True(objects[0]["score"]!.ToObject<int>() > objects[1]["score"]!.ToObject<int>());
            var factors = (JObject)objects[0]["factors"]!;
            Assert.Equal(2, factors["refCount"]?.ToObject<int>());
            Assert.Equal(3, factors["callerCount"]?.ToObject<int>());
        }

        [Fact]
        public void DependencyHeatmap_AsciiFormat_EmitsBarChart()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "Foo", Type = "Procedure", Guid = "g1",
                    Calls = new List<string> { "X" }, CalledBy = new List<string> { "Y" } }
            };
            var svc = new AnalyzeService(BuildIndex(entries), objSvc: null, graph: null);
            string json = svc.DependencyHeatmap(kbPath: null, format: "ascii");
            var obj = JObject.Parse(json);
            Assert.NotNull(obj["result"]?["ascii"]);
            Assert.Contains("Dependency heatmap", obj["result"]!["ascii"]!.ToString());
            Assert.Contains("Foo", obj["result"]!["ascii"]!.ToString());
        }

        [Fact]
        public void DependencyHeatmap_EditCountFromSnapshots_BoostsScore()
        {
            // Synthesise .gx/snapshots with two bak files for guid "g1".
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-heatmap-" + System.Guid.NewGuid().ToString("N"));
            string snapDir = Path.Combine(tempKb, ".gx", "snapshots");
            Directory.CreateDirectory(snapDir);
            try
            {
                File.WriteAllText(Path.Combine(snapDir, "g1-Source-20260101T120000Z.bak"), "x");
                File.WriteAllText(Path.Combine(snapDir, "g1-Source-20260101T120500Z.bak"), "x");
                var entries = new[]
                {
                    new SearchIndex.IndexEntry { Name = "Edited", Type = "Procedure", Guid = "g1",
                        Calls = new List<string>(), CalledBy = new List<string>() },
                    new SearchIndex.IndexEntry { Name = "Static", Type = "Procedure", Guid = "g2",
                        Calls = new List<string> { "A" }, CalledBy = new List<string>() }
                };
                var svc = new AnalyzeService(BuildIndex(entries), objSvc: null, graph: null);
                string json = svc.DependencyHeatmap(kbPath: tempKb, format: null);
                var obj = JObject.Parse(json);
                var objects = (JArray)obj["result"]!["objects"]!;
                // "Edited" has 2 edits * 5 = 10 score; "Static" has 1 ref = 1. Edited wins.
                Assert.Equal("Edited", objects[0]["name"]?.ToString());
                Assert.Equal(2, objects[0]["factors"]?["editCount"]?.ToObject<int>());
            }
            finally
            {
                try { Directory.Delete(tempKb, recursive: true); } catch { }
            }
        }
    }
}
