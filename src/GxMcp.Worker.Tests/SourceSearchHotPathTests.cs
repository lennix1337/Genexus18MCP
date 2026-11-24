using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SourceSearchHotPathTests
    {
        private readonly ITestOutputHelper _output;

        public SourceSearchHotPathTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static IndexCacheService BuildIndexedProcedures(int count, int linesPerProc, int matchEveryN)
        {
            var entries = new List<SearchIndex.IndexEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var sb = new StringBuilder(linesPerProc * 48);
                for (int line = 1; line <= linesPerProc; line++)
                {
                    if (matchEveryN > 0 && i % matchEveryN == 0 && (line == 15 || line == 42))
                    {
                        sb.Append("    For Each CustomerBilling Order CustomerId // hit ").Append(i).Append('_').Append(line).Append('\n');
                    }
                    else
                    {
                        sb.Append("    &Value").Append(line).Append(" = &Base + ").Append(line).Append(" // proc ").Append(i).Append('\n');
                    }
                }

                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = $"BillingProc{i:D4}",
                    Type = "Procedure",
                    Guid = new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1).ToString(),
                    Path = $"Root Module/Billing/BillingProc{i:D4}",
                    FullSource = sb.ToString(),
                    FullSourcePart = "Source",
                    LastUpdate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc)
                });
            }

            var cache = new IndexCacheService();
            cache.ReplaceAll(entries);
            cache.MarkIndexComplete(entries.Count);
            return cache;
        }

        [Fact]
        public void Benchmark_SourceSearch_HotPaths()
        {
            var cache = BuildIndexedProcedures(count: 1500, linesPerProc: 60, matchEveryN: 5);
            var svc = new SourceSearchService(cache, objectService: null);

            int iterations = int.TryParse(Environment.GetEnvironmentVariable("GXMCP_BENCH_ITERS"), out int envIters) && envIters > 0
                ? envIters
                : 3;

            var literalWithHits = new SourceSearchCriteria
            {
                Pattern = "CustomerBilling",
                MaxResults = 200,
                TimeoutMs = 30000,
                TypeFilter = "Procedure"
            };
            var literalNoHits = new SourceSearchCriteria
            {
                Pattern = "NonExistentTokenXYZ",
                MaxResults = 50,
                TimeoutMs = 30000,
                TypeFilter = "Procedure"
            };
            var regexWithHits = new SourceSearchCriteria
            {
                Pattern = @"Customer[A-Z]\w+",
                MaxResults = 100,
                TimeoutMs = 30000,
                TypeFilter = "Procedure"
            };

            // Warmup
            var warm1 = JObject.Parse(svc.SearchAsJson(literalWithHits));
            var warm2 = JObject.Parse(svc.SearchAsJson(literalNoHits));
            var warm3 = JObject.Parse(svc.SearchAsJson(regexWithHits));
            Assert.Equal(200, warm1["result"]?["count"]?.Value<int>());
            Assert.Equal(0, warm2["result"]?["count"]?.Value<int>());
            Assert.Equal(100, warm3["result"]?["count"]?.Value<int>());

            double Measure(SourceSearchCriteria criteria, out int gen0, out long bytesDelta)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                int g0 = GC.CollectionCount(0);
                long memBefore = GC.GetTotalMemory(false);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                {
                    _ = svc.SearchAsJson(criteria);
                }
                sw.Stop();
                bytesDelta = Math.Max(0L, GC.GetTotalMemory(false) - memBefore);
                gen0 = GC.CollectionCount(0) - g0;
                return sw.Elapsed.TotalMilliseconds / iterations;
            }

            double msLiteralHits = Measure(literalWithHits, out int g0LitHits, out _);
            double msLiteralMiss = Measure(literalNoHits, out int g0LitMiss, out _);
            double msRegexHits = Measure(regexWithHits, out int g0RegexHits, out _);

            string report = $@"
=== SOURCE_SEARCH_HOTPATH_BENCHMARK (iters={iterations}, 1500 procs x 60 lines) ===
  Literal_200Hits : {msLiteralHits:F2} ms/op (Gen0={g0LitHits})
  Literal_NoHits  : {msLiteralMiss:F2} ms/op (Gen0={g0LitMiss})
  Regex_100Hits   : {msRegexHits:F2} ms/op (Gen0={g0RegexHits})
==============================================================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        [Fact]
        public void SinglePassBuildHitFromSource_MatchesPerLineContextAndTrailingNewlineParity()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Name = "ContextParityProc",
                    Type = "Procedure",
                    Guid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    Path = "Root Module/ContextParityProc",
                    FullSource = "line1\nline2\nline3\nline4\nmatch FooBar once and FooBar twice\nline6\nline7\nline8\nline9\nlast line FooBar\n",
                    FullSourcePart = "Source",
                    LastUpdate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc)
                }
            };
            var cache = new IndexCacheService();
            cache.ReplaceAll(entries);
            cache.MarkIndexComplete(entries.Count);
            var svc = new SourceSearchService(cache, objectService: null);

            // Single-pass path (BuildHitFromSource) vs line-anchored per-line path (BuildHit).
            var singlePassRes = JObject.Parse(svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "foobar",
                CaseSensitive = false,
                MaxResults = 20,
                TypeFilter = "Procedure"
            }));
            var perLineRes = JObject.Parse(svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "^.*foobar",
                CaseSensitive = false,
                MaxResults = 20,
                TypeFilter = "Procedure"
            }));

            var singlePassHits = Assert.IsType<JArray>(singlePassRes["result"]?["hits"]);
            var perLineHits = Assert.IsType<JArray>(perLineRes["result"]?["hits"]);
            Assert.Equal(2, singlePassHits.Count);
            Assert.Equal(perLineHits.ToString(Newtonsoft.Json.Formatting.None), singlePassHits.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(5, singlePassHits[0]["lineNumber"]?.Value<int>());
            Assert.Equal(new[] { "line2", "line3", "line4" }, singlePassHits[0]["contextBefore"]!.ToObject<string[]>());
            Assert.Equal(new[] { "line6", "line7", "line8" }, singlePassHits[0]["contextAfter"]!.ToObject<string[]>());
            Assert.Equal(10, singlePassHits[1]["lineNumber"]?.Value<int>());
            Assert.Equal(new[] { "line7", "line8", "line9" }, singlePassHits[1]["contextBefore"]!.ToObject<string[]>());
            Assert.Equal(new[] { "" }, singlePassHits[1]["contextAfter"]!.ToObject<string[]>());
        }
    }
}
