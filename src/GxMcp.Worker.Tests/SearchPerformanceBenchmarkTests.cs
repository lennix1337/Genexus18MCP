using System;
using System.Collections.Generic;
using System.Diagnostics;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SearchPerformanceBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public SearchPerformanceBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_SearchRanking_Throughput()
        {
            var entries = new List<SearchIndex.IndexEntry>(2000);
            for (int i = 0; i < 2000; i++)
            {
                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = "CustomerInvoiceProc" + i,
                    Type = i % 5 == 0 ? "Folder" : (i % 2 == 0 ? "Transaction" : "Procedure"),
                    Description = "Processes billing and invoice records for customer account #" + i,
                    Keywords = new List<string> { "customer", "billing", "invoice", "account" },
                    BusinessDomain = "Billing",
                    Guid = Guid.NewGuid().ToString(),
                    LastUpdate = DateTime.UtcNow.AddMinutes(-i)
                });
            }

            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries);
            var searchSvc = new SearchService(cache);

            // Warmup
            for (int i = 0; i < 10; i++)
            {
                searchSvc.Search("customer billing invoice", limit: 50);
                searchSvc.Search("Account", typeFilter: "Procedure", limit: 50);
            }

            int iterations = 1000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                searchSvc.Search("customer query " + (i % 50), limit: 50);
            }
            sw.Stop();
            int gen0Count = GC.CollectionCount(0) - gen0Start;
            double msPerOp = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== SEARCH_RANKING_BENCHMARK ===
Search ranking (2,000 candidate objects, 1,000 searches):
  Latency: {msPerOp:F3} ms/op
  Gen0 Collections: {gen0Count}
================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        [Fact]
        public void Benchmark_Search_WithBuiltSecondaryIndexes_AndColdQueries()
        {
            var entries = new List<SearchIndex.IndexEntry>(2000);
            for (int i = 0; i < 2000; i++)
            {
                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = "IndexedCustomer" + i,
                    Type = i % 2 == 0 ? "Procedure" : "Transaction",
                    Description = "indexed customer billing record " + i,
                    Keywords = new List<string> { "indexed", "customer", "billing" },
                    BusinessDomain = i % 2 == 0 ? "Billing" : "Sales",
                    Guid = "indexed-guid-" + i
                });
            }

            var cache = new IndexCacheService();
            cache.ReplaceAll(entries);
            var searchSvc = new SearchService(cache);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 250; i++)
            {
                // Unique queries intentionally bypass the bounded query cache.
                searchSvc.Search("IndexedCustomer" + i, typeFilter: "Procedure", limit: 10);
            }
            sw.Stop();

            string report = string.Format(
                "\n=== SEARCH_SECONDARY_INDEX_BENCHMARK ===\nBuilt entries: {0}\nUnique cold queries: 250\nLatency: {1:F3} ms/op\n===========================================",
                entries.Count, sw.Elapsed.TotalMilliseconds / 250.0);
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
