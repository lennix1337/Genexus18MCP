using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (post-Task 7.2): worker-side cancellation for SourceSearchService.
    // The gateway's lifecycle action=cancel + job_id signals a CTS; previously
    // the worker had no way to honor it, so a slow regex over 24k entries ran
    // to completion while the gateway poller exited.
    public class SourceSearchCancellationTests
    {
        private static IndexCacheService Build10kIndex()
        {
            var svc = new IndexCacheService();
            var entries = Enumerable.Range(0, 10000).Select(i => new SearchIndex.IndexEntry
            {
                Name = "Proc" + i,
                Type = "Procedure",
                SourceSnippet = "for each Foo " + i + " endfor"
            }).ToList();
            svc.LoadFromEntries(entries);
            svc.MarkIndexComplete(entries.Count);
            return svc;
        }

        [Fact]
        public void SearchAsJson_PreCancelled_ReturnsCancelledEnvelope()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                MaxResults = 1000,
                TimeoutMs = 30000
            }, cts.Token);

            var obj = JObject.Parse(json);
            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.Equal("Cancelled", obj["code"]?.ToString());
            Assert.NotNull(obj["result"]?["partialHits"]);
            // Pre-cancelled token trips on the very first iteration, so scanned should be 0.
            Assert.Equal(0, obj["result"]!["totalScanned"]!.Value<int>());
            SourceSearchEnvelopeTests.AssertMarkedIncomplete(obj, "Cancelled");
            Assert.Contains("was cancelled", obj["result"]!["partialHint"]!.ToString());
        }

        [Fact]
        public void SearchAsJson_NoCancellation_RunsToCompletion()
        {
            // Regression guard: passing CancellationToken.None must keep the legacy
            // behavior (search runs to completion or timeout, not Cancelled).
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                MaxResults = 5,
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.NotEqual("Cancelled", obj["status"]?.ToString());
        }
    }
}
