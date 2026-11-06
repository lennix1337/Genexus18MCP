using System;
using System.Diagnostics;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SourceSearchCensusResumeTests
    {
        private readonly ITestOutputHelper output;
        public SourceSearchCensusResumeTests(ITestOutputHelper output) { this.output = output; }

        [Fact]
        public void CensusTimeoutResumesAndReportsPartialCoverage()
        {
            var entries = Enumerable.Range(0, 4).Select(i => new SearchIndex.IndexEntry
            {
                Guid = Guid.NewGuid().ToString(), Name = "Proc" + i,
                Type = "Procedure", FullSource = "needle"
            }).ToArray();
            var index = new IndexCacheService();
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Length);
            var service = new SourceSearchService(index, null);
            int reads = 0;
            service.CoverageForTest = (candidates, scope) =>
            {
                int count = candidates.Count();
                reads += count;
                return new SourceStoreCoverage { TotalObjects = count, StaleObjects = count };
            };
            service.BudgetElapsedMillisecondsForTest = () => reads >= 2 ? 2 : 0;
            var criteria = new SourceSearchCriteria { Pattern = "needle", TimeoutMs = 1 };
            var first = JObject.Parse(service.SearchAsJson(criteria));
            Assert.Equal(false, first["result"]?["coverageComplete"]?.Value<bool>());
            Assert.NotNull(first["result"]?["partialHits"]);
            Assert.NotNull(first["result"]?["totalScanned"]);
            Assert.Equal(2, first["result"]?["coverage"]?["staleObjects"]?.Value<int>());
            criteria.Cursor = first["result"]?["nextCursor"]?.ToString();
            Assert.False(string.IsNullOrEmpty(criteria.Cursor));
            int offset = first["result"]!["nextOffset"]!.Value<int>();
            service.BudgetElapsedMillisecondsForTest = () => 0;
            var second = JObject.Parse(service.SearchAsJson(criteria));
            Assert.Equal(true, second["result"]?["coverageComplete"]?.Value<bool>());
            Assert.Equal(4, second["result"]?["coverage"]?["staleObjects"]?.Value<int>());
            Assert.Equal(4, reads);
            Assert.True(second["result"]!["scannedObjects"]!.Value<int>() >= offset);
        }

        [Fact]
        public void CoverageCursorPreservesLegacyStartIndexWhenNotRepeated()
        {
            var entries = Enumerable.Range(0, 4).Select(i => new SearchIndex.IndexEntry
            {
                Guid = Guid.NewGuid().ToString(), Name = "Proc" + i,
                Type = "Procedure", FullSource = "needle"
            }).ToArray();
            var index = new IndexCacheService();
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Length);
            var service = new SourceSearchService(index, null);
            int reads = 0;
            service.CoverageForTest = (candidates, scope) => { reads++; return new SourceStoreCoverage { TotalObjects = 1, StaleObjects = 1 }; };
            service.BudgetElapsedMillisecondsForTest = () => reads >= 2 ? 2 : 0;
            var first = JObject.Parse(service.SearchAsJson(new SourceSearchCriteria { Pattern = "needle", StartIndex = 2, TimeoutMs = 1 }));
            service.BudgetElapsedMillisecondsForTest = () => 0;
            var second = JObject.Parse(service.SearchAsJson(new SourceSearchCriteria { Pattern = "needle", Cursor = first["result"]!["nextCursor"]!.ToString(), TimeoutMs = 1 }));
            Assert.Equal("Proc2", second["result"]!["hits"]![0]!["objectName"]!.ToString());
        }

        [Fact]
        public void ProgressIsMonotonicAcrossCoverageSourceAndMetadata()
        {
            var entries = Enumerable.Range(0, 3).Select(i => new SearchIndex.IndexEntry
            {
                Guid = Guid.NewGuid().ToString(), Name = "Proc" + i,
                Type = "Procedure", FullSource = "needle", Description = "needle"
            }).ToArray();
            var index = new IndexCacheService();
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Length);
            var service = new SourceSearchService(index, null);
            int reads = 0;
            service.CoverageForTest = (candidates, scope) =>
            {
                int count = candidates.Count();
                reads += count;
                return new SourceStoreCoverage { TotalObjects = count, StaleObjects = count };
            };
            service.BudgetElapsedMillisecondsForTest = () => reads >= 2 ? 2 : 0;
            var criteria = new SourceSearchCriteria { Pattern = "needle", Fields = new System.Collections.Generic.List<string> { "source", "description" }, MaxResults = 1, TimeoutMs = 1 };
            int last = -1;
            var phases = new System.Collections.Generic.HashSet<string>();
            for (int page = 0; page < 12; page++)
            {
                var result = JObject.Parse(service.SearchAsJson(criteria))["result"]!;
                var progress = result["progress"]!;
                Assert.NotNull(progress);
                phases.Add(progress["phase"]!.ToString());
                int position = progress["cumulativePosition"]!.Value<int>();
                Assert.True(position >= last);
                last = position;
                criteria.Cursor = result["nextCursor"]?.ToString();
                if (criteria.Cursor == null) break;
                service.BudgetElapsedMillisecondsForTest = () => 0;
            }
            Assert.Contains("coverage", phases);
            Assert.Contains("source", phases);
            Assert.Contains("metadata", phases);
            Assert.Equal(3, reads);
        }

        [Fact]
        public void OldBoundCursorIsExplicitlyRefused()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[] { new SearchIndex.IndexEntry { Name = "One", Type = "Procedure", FullSource = "needle" } });
            index.MarkIndexComplete(1);
            string old = SourceSearchService.BuildResumeCursor(0, 0, false, "query", "scope", "index", "kb");
            var response = JObject.Parse(new SourceSearchService(index, null).SearchAsJson(new SourceSearchCriteria { Pattern = "needle", Cursor = old }));
            Assert.Equal("UnsupportedCursorVersion", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void OversizedTimeoutReportsEffectiveBudget()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[] { new SearchIndex.IndexEntry { Name = "One", Type = "Procedure", FullSource = "needle" } });
            index.MarkIndexComplete(1);
            var response = JObject.Parse(new SourceSearchService(index, null).SearchAsJson(new SourceSearchCriteria { Pattern = "needle", ObjectName = "One", TimeoutMs = 240000 }));
            Assert.True(response["result"]?["effectiveTimeoutMs"]?.Value<int>() <= 45000);
            Assert.Equal(240000, response["result"]?["requestedTimeoutMs"]?.Value<int>());
        }

        [Fact]
        public void ResumeDoesNotRepeatFreshnessCensus()
        {
            var entries = Enumerable.Range(0, 18000).Select(i => new SearchIndex.IndexEntry
            {
                Guid = Guid.NewGuid().ToString(), Name = "Proc" + i.ToString("D5"),
                Type = "Procedure", FullSource = "needle", Description = "needle"
            }).ToArray();
            var index = new IndexCacheService();
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Length);
            var service = new SourceSearchService(index, objectService: null);
            // This case resumes a completed census; timeout continuation has its own tests.
            service.BudgetElapsedMillisecondsForTest = () => 0;
            int lookups = 0;
            long censusTicks = 0;
            service.CoverageForTest = (candidates, scope) =>
            {
                var timer = Stopwatch.StartNew();
                int count = 0;
                foreach (var candidate in candidates) { lookups++; count++; }
                timer.Stop();
                censusTicks += timer.ElapsedTicks;
                return new SourceStoreCoverage { TotalObjects = count, StaleObjects = count };
            };
            var criteria = new SourceSearchCriteria { Pattern = "needle", MaxResults = 1, TimeoutMs = 30000 };
            var first = JObject.Parse(service.SearchAsJson(criteria));
            Assert.Equal("SourceSearchCompleted", first["code"]?.ToString());
            Assert.Equal(true, first["result"]?["coverageComplete"]?.Value<bool>());
            Assert.Equal(entries.Length, lookups);
            Assert.Equal("Proc00000", first["result"]?["hits"]?[0]?["objectName"]?.ToString());
            criteria.Cursor = first["result"]?["nextCursor"]?.ToString();
            Assert.False(string.IsNullOrEmpty(criteria.Cursor));
            int firstLookups = lookups;
            long firstTicks = censusTicks;
            var timer2 = Stopwatch.StartNew();
            var second = JObject.Parse(service.SearchAsJson(criteria));
            timer2.Stop();
            output.WriteLine("First census lookups={0}, ms={1:F1}; resumed census lookups={2}, ms={3:F1}; resumed total ms={4:F1}; code={5}", firstLookups, firstTicks * 1000.0 / Stopwatch.Frequency, lookups - firstLookups, (censusTicks - firstTicks) * 1000.0 / Stopwatch.Frequency, timer2.Elapsed.TotalMilliseconds, second["code"]);
            Assert.Equal("SourceSearchCompleted", second["code"]?.ToString());
            Assert.Equal("Proc00001", second["result"]?["hits"]?[0]?["objectName"]?.ToString());
            Assert.Equal(firstLookups, lookups);
        }
    }
}
