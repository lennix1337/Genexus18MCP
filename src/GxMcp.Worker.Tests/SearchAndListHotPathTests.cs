using System;
using System.Collections.Generic;
using System.Diagnostics;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SearchAndListHotPathTests
    {
        private readonly ITestOutputHelper _output;

        public SearchAndListHotPathTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static IndexCacheService BuildRealisticKb(int count)
        {
            var entries = new List<SearchIndex.IndexEntry>(count);
            var types = new[] { "Procedure", "Transaction", "WebPanel", "SDT", "Attribute", "Folder", "Module", "Index" };
            for (int i = 0; i < count; i++)
            {
                string type = types[i % types.Length];
                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = $"CustomerBillingObj{i:D5}",
                    Type = type,
                    Description = "Handles customer billing and invoice totals for account " + (i % 100),
                    Keywords = new List<string> { "customer", "billing", "invoice" },
                    BusinessDomain = i % 3 == 0 ? "Billing" : "Core",
                    Parent = "Billing",
                    ParentPath = "Root Module/Billing",
                    Path = $"Root Module/Billing/CustomerBillingObj{i:D5}",
                    Guid = new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2).ToString(),
                    LastUpdate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc).AddSeconds(-i)
                });
            }

            var cache = new IndexCacheService();
            cache.ReplaceAll(entries);
            cache.MarkIndexComplete(entries.Count);
            return cache;
        }

        [Fact]
        public void Benchmark_SearchListAndVector_HotPaths()
        {
            var cache = BuildRealisticKb(15000);
            var searchSvc = new SearchService(cache);
            var listSvc = new ListService(cache);
            var vectorSvc = new VectorService();

            int searchIters = int.TryParse(Environment.GetEnvironmentVariable("GXMCP_BENCH_ITERS"), out int envIters) && envIters > 0
                ? envIters
                : 5;
            int vectorIters = searchIters * 500;

            // Warmup
            SearchService.ResetQueryCacheForTest();
            ListService.InvalidateCache();
            _ = searchSvc.Search("customer billing warm", limit: 50);
            _ = searchSvc.Search("customer billing warm", typeFilter: "Procedure", limit: 50);
            _ = listSvc.ListObjects(null, limit: 50, offset: 0, invokerNameFilter: "Obj001");
            _ = vectorSvc.ComputeEmbedding("CustomerInvoiceProc Handles customer billing and invoice totals");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int g0Full = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < searchIters; i++)
            {
                _ = searchSvc.Search("customer billing q" + i, limit: 50);
            }
            sw.Stop();
            double msFullQuery = sw.Elapsed.TotalMilliseconds / searchIters;
            g0Full = GC.CollectionCount(0) - g0Full;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int g0Typed = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < searchIters; i++)
            {
                _ = searchSvc.Search("customer billing t" + i, typeFilter: "Procedure", limit: 50);
            }
            sw.Stop();
            double msTypedQuery = sw.Elapsed.TotalMilliseconds / searchIters;
            g0Typed = GC.CollectionCount(0) - g0Typed;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int g0List = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < searchIters; i++)
            {
                _ = listSvc.ListObjects(null, limit: 50, offset: 0, invokerNameFilter: "Obj00" + (i % 100));
            }
            sw.Stop();
            double msList = sw.Elapsed.TotalMilliseconds / searchIters;
            g0List = GC.CollectionCount(0) - g0List;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int g0Vec = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < vectorIters; i++)
            {
                _ = vectorSvc.ComputeEmbedding("CustomerInvoiceProc Handles customer billing and invoice totals for region account");
            }
            sw.Stop();
            double usVec = (sw.Elapsed.TotalMilliseconds * 1000.0) / vectorIters;
            g0Vec = GC.CollectionCount(0) - g0Vec;

            string report = $@"
=== SEARCH_LIST_VECTOR_HOTPATH_BENCHMARK (15,000 objs, iters={searchIters}, vecIters={vectorIters}) ===
  Search_FullScan_15k     : {msFullQuery:F2} ms/op (Gen0={g0Full})
  Search_TypeFilter_15k   : {msTypedQuery:F2} ms/op (Gen0={g0Typed})
  ListObjects_FullScan_15k: {msList:F2} ms/op (Gen0={g0List})
  Vector_ComputeEmbedding : {usVec:F2} us/op (Gen0={g0Vec})
=========================================================================================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        [Fact]
        public void Search_NoiseTypesExcludedByDefaultAndIncludedWhenExplicitlyFiltered()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Billing", Type = "Folder", Description = "Billing folder", Guid = "g1" },
                new SearchIndex.IndexEntry { Name = "Billing", Type = "Module", Description = "Billing module", Guid = "g2" },
                new SearchIndex.IndexEntry { Name = "Billing", Type = "Index", Description = "Billing index", Guid = "g3" },
                new SearchIndex.IndexEntry { Name = "Billing", Type = "Procedure", Description = "Billing proc", Guid = "g4" }
            };
            var cache = new IndexCacheService();
            cache.ReplaceAll(entries);
            cache.MarkIndexComplete(entries.Count);
            var svc = new SearchService(cache);
            SearchService.ResetQueryCacheForTest();

            var defaultRes = JObject.Parse(svc.Search("Billing", limit: 20));
            var defaultResults = Assert.IsType<JArray>(defaultRes["results"]);
            Assert.Single(defaultResults);
            Assert.Equal("Procedure", defaultResults[0]["type"]?.ToString());

            var folderRes = JObject.Parse(svc.Search("Billing", typeFilter: "Folder", limit: 20));
            var folderResults = Assert.IsType<JArray>(folderRes["results"]);
            Assert.Single(folderResults);
            Assert.Equal("Folder", folderResults[0]["type"]?.ToString());
        }

        [Fact]
        public void VectorService_ComputeEmbedding_PreservesFourFoldSymmetryAndUnitNorm()
        {
            var svc = new VectorService();
            float[] emb = svc.ComputeEmbedding("CustomerInvoiceProc Handles customer billing and invoice totals");
            Assert.Equal(128, emb.Length);
            double normSq = 0;
            for (int i = 0; i < 128; i++) normSq += emb[i] * emb[i];
            Assert.InRange(Math.Sqrt(normSq), 0.9999, 1.0001);
            for (int i = 0; i < 32; i++)
            {
                Assert.Equal(emb[i], emb[i + 32]);
                Assert.Equal(emb[i], emb[i + 64]);
                Assert.Equal(emb[i], emb[i + 96]);
            }
            Assert.Equal(1.0f, svc.CosineSimilarity(emb, emb), precision: 4);
        }

        [Fact]
        public void Search_DoesNotMaterializeEmptyListsOnScannedEntries()
        {
            var entry = new SearchIndex.IndexEntry
            {
                Name = "CleanEntryProc",
                Type = "Procedure",
                Description = "Billing helper",
                Guid = "g-clean"
            };
            entry.TrimEmptyLists();
            Assert.Null(entry.TagsOrNull);
            Assert.Null(entry.TablesOrNull);
            Assert.Null(entry.CallsOrNull);
            Assert.Null(entry.KeywordsOrNull);

            var cache = new IndexCacheService();
            cache.ReplaceAll(new List<SearchIndex.IndexEntry> { entry });
            cache.MarkIndexComplete(1);
            var svc = new SearchService(cache);
            SearchService.ResetQueryCacheForTest();

            var res = JObject.Parse(svc.Search("billing", limit: 10));
            Assert.Equal(1, (int)res["count"]);
            Assert.Null(entry.TagsOrNull);
            Assert.Null(entry.TablesOrNull);
            Assert.Null(entry.CallsOrNull);
            Assert.Null(entry.KeywordsOrNull);
        }

        [Theory]
        [InlineData("CustomerBillingObj001", "obj001", 0)]
        [InlineData("CustomerBillingObj001", "OBJ001", 1)]
        [InlineData("CustomerBillingObj001", "customer", 0)]
        [InlineData("CustomerBillingObj001", "customer", 1)]
        [InlineData("Faturamento_Ação_Cliente", "ação", 0)]
        [InlineData("Faturamento_Ação_Cliente", "cliente", 0)]
        [InlineData("ÁrvoreDeClientes", "decli", 0)]
        [InlineData("Short", "LongerNeedle", 0)]
        [InlineData("Exact", "EXACT", 0)]
        [InlineData("Kelvin_\u212A_Test", "k", 0)]
        public void ContainsAndStartsWithFast_MatchOrdinalIgnoreCaseExactly(string haystack, string needle, int startIndex)
        {
            bool isAscii = IndexEntryFilterBuilder.TryPrepareLowerAscii(needle, out string prepared);
            bool expectedContains = haystack.IndexOf(needle, startIndex, StringComparison.OrdinalIgnoreCase) >= 0;
            bool actualContains = IndexEntryFilterBuilder.ContainsIgnoreCaseFast(haystack, startIndex, prepared, isAscii);
            Assert.Equal(expectedContains, actualContains);

            if (startIndex == 0)
            {
                bool expectedStarts = haystack.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
                bool actualStarts = IndexEntryFilterBuilder.StartsWithIgnoreCaseFast(haystack, prepared, isAscii);
                Assert.Equal(expectedStarts, actualStarts);
            }
        }
    }
}
