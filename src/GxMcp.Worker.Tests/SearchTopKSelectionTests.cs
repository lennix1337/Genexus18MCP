using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SearchTopKSelectionTests
    {
        private readonly ITestOutputHelper _output;

        public SearchTopKSelectionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static List<SearchIndex.IndexEntry> BuildCatalog(int count, bool includeTies = false)
        {
            var entries = new List<SearchIndex.IndexEntry>(count);
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < count; i++)
            {
                // Include score/timestamp ties when requested to verify tie-breaking parity.
                int timeBucket = includeTies ? (i / 5) : i;
                string name = includeTies
                    ? $"CustomerOrder_{(count - i) / 3:D5}_{i % 3}"
                    : $"CustomerOrder_{count - i:D5}";

                entries.Add(new SearchIndex.IndexEntry
                {
                    Guid = $"guid-{i:D6}",
                    Name = name,
                    Type = i % 3 == 0 ? "Procedure" : (i % 3 == 1 ? "Transaction" : "WebPanel"),
                    Description = "Customer order processing routine " + i,
                    Keywords = new List<string> { "customer", "order", "processing" },
                    BusinessDomain = "Sales",
                    LastUpdate = baseTime.AddMinutes(timeBucket)
                });
            }
            return entries;
        }

        private static SearchService CreateSearchService(List<SearchIndex.IndexEntry> entries)
        {
            SearchService.ResetQueryCacheForTest();
            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries);
            return new SearchService(cache);
        }

        [Fact]
        public void Search_Relevance_TopK_Matches_FullSort_IncludingTies()
        {
            var entries = BuildCatalog(500, includeTies: true);
            var svc = CreateSearchService(entries);

            SearchService.ResetQueryCacheForTest();
            var topKJson = JObject.Parse(svc.Search("customer order", limit: 50));

            SearchService.ResetQueryCacheForTest();
            var fullSortJson = JObject.Parse(svc.Search("customer order", limit: 250));

            Assert.Equal((int)fullSortJson["total"], (int)topKJson["total"]);
            Assert.Equal(50, (int)topKJson["count"]);
            Assert.True((bool)topKJson["hasMore"]);
            Assert.Equal((string)fullSortJson["_meta"]?["match_quality"], (string)topKJson["_meta"]?["match_quality"]);

            var topKNames = ((JArray)topKJson["results"]).Select(r => (string)r["guid"]).ToList();
            var fullSortNames = ((JArray)fullSortJson["results"]).Take(50).Select(r => (string)r["guid"]).ToList();
            Assert.Equal(fullSortNames, topKNames);
        }

        [Fact]
        public void Search_LastUpdate_TopK_Matches_FullSort_IncludingTies()
        {
            var entries = BuildCatalog(500, includeTies: true);
            var svc = CreateSearchService(entries);

            SearchService.ResetQueryCacheForTest();
            var topKJson = JObject.Parse(svc.Search("customer order", limit: 50, sort: "lastUpdate"));

            SearchService.ResetQueryCacheForTest();
            var fullSortJson = JObject.Parse(svc.Search("customer order", limit: 250, sort: "lastUpdate"));

            Assert.Equal((int)fullSortJson["total"], (int)topKJson["total"]);
            Assert.Equal(50, (int)topKJson["count"]);
            Assert.True((bool)topKJson["hasMore"]);
            Assert.NotNull((string)topKJson["nextCursor"]);

            var topKGuids = ((JArray)topKJson["results"]).Select(r => (string)r["guid"]).ToList();
            var fullSortGuids = ((JArray)fullSortJson["results"]).Take(50).Select(r => (string)r["guid"]).ToList();
            Assert.Equal(fullSortGuids, topKGuids);
        }

        [Fact]
        public void Search_WithCursor_And_LargeLimit_PreservesPagingSemantics()
        {
            var entries = BuildCatalog(300, includeTies: true);
            var svc = CreateSearchService(entries);

            SearchService.ResetQueryCacheForTest();
            var page1 = JObject.Parse(svc.Search("customer order", limit: 50, sort: "lastUpdate"));
            string cursor = (string)page1["nextCursor"];
            Assert.False(string.IsNullOrEmpty(cursor));

            SearchService.ResetQueryCacheForTest();
            var page2 = JObject.Parse(svc.Search("customer order", limit: 50, sort: "lastUpdate", cursor: cursor));
            Assert.Equal(50, (int)page2["count"]);
            Assert.Equal((int)page1["total"], (int)page2["total"]);

            var page1Guids = new HashSet<string>(((JArray)page1["results"]).Select(r => (string)r["guid"]));
            var page2Guids = ((JArray)page2["results"]).Select(r => (string)r["guid"]).ToList();
            Assert.All(page2Guids, g => Assert.DoesNotContain(g, page1Guids));

            // limit > 200 uses full sort path
            SearchService.ResetQueryCacheForTest();
            var largePage = JObject.Parse(svc.Search("customer order", limit: 220, sort: "lastUpdate"));
            Assert.Equal(220, (int)largePage["count"]);
        }

        [Fact]
        public void Search_TopK_AvoidsFullRankedListAllocation_ForSmallLimit()
        {
            var entries = BuildCatalog(30000);
            var svc = CreateSearchService(entries);

            // Warmup
            SearchService.ResetQueryCacheForTest();
            svc.Search("customer order", limit: 50);
            SearchService.ResetQueryCacheForTest();
            svc.Search("customer order", limit: 250);

            AppDomain.MonitoringIsEnabled = true;
            int iterations = 15;

            long beforeTopK = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int i = 0; i < iterations; i++)
            {
                SearchService.ResetQueryCacheForTest();
                svc.Search("customer order", limit: 50);
            }
            double kbTopK = ((AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - beforeTopK) / 1024.0) / iterations;

            long beforeFull = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int i = 0; i < iterations; i++)
            {
                SearchService.ResetQueryCacheForTest();
                svc.Search("customer order", limit: 250);
            }
            double kbFull = ((AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - beforeFull) / 1024.0) / iterations;

            _output.WriteLine($"Allocated per 30k search: limit=50 {kbTopK:F1} KB/op vs limit=250 {kbFull:F1} KB/op");

            // Full List<RankedResult> materialization over 30k entries allocates > 1,200 KB/op
            // (doubling buffer: 4 -> ... -> 32768 * 16B). Top-K bounded heap allocates only O(K).
            Assert.True(kbTopK < 800.0 && kbTopK < kbFull * 0.65,
                $"Expected Top-K (limit=50) to allocate < 800 KB/op and < 65% of full sort (limit=250), but got {kbTopK:F1} KB/op vs {kbFull:F1} KB/op.");
        }

        [Fact]
        public void Benchmark_Search_30k_TopK_Vs_FullSort()
        {
            var entries = BuildCatalog(30000);
            var svc = CreateSearchService(entries);

            // Warmup
            for (int i = 0; i < 3; i++)
            {
                SearchService.ResetQueryCacheForTest();
                svc.Search("customer order", limit: 50);
                SearchService.ResetQueryCacheForTest();
                svc.Search("customer order", limit: 250);
            }

            int iterations = 20;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Before = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                SearchService.ResetQueryCacheForTest();
                svc.Search("customer order", limit: 50);
            }
            sw.Stop();
            int gen0TopK = GC.CollectionCount(0) - gen0Before;
            double msLimit50 = sw.Elapsed.TotalMilliseconds / iterations;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Before = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                SearchService.ResetQueryCacheForTest();
                svc.Search("customer order", limit: 250);
            }
            sw.Stop();
            int gen0Full = GC.CollectionCount(0) - gen0Before;
            double msLimit250 = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== SEARCH_30K_TOPK_BENCHMARK ===
30,000 IndexEntry candidates ({iterations} cold searches each):
  limit=50 : {msLimit50:F2} ms/op, Gen0 Collections: {gen0TopK}
  limit=250: {msLimit250:F2} ms/op, Gen0 Collections: {gen0Full}
=================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
