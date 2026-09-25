using System.Collections.Generic;
using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 2.1): SourceSearchService must surface index readiness and
    // hard-timeout state in structured envelopes instead of returning silent
    // empty hits.
    public class SourceSearchEnvelopeTests
    {
        [Fact]
        public void Search_OnColdIndex_ReturnsIndexColdEnvelope()
        {
            var index = new IndexCacheService(); // Cold by default — never loaded.
            var svc = new SourceSearchService(index, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "Clicksign" });
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.Equal("IndexCold", obj["code"]?.ToString());
            Assert.NotNull(obj["result"]?["retryAfterMs"]);
            Assert.Null(obj["result"]?["hits"]); // never silent empty
            Assert.Null(obj["result"]?["count"]);
        }

        [Fact]
        public void Search_OnReadyIndex_ReturnsHitsEnvelope()
        {
            var fixture = TestFixtures.SmallCallGraph();
            fixture.Index.MarkIndexComplete(3);
            // ObjectService is null; the per-entry FindObject call will throw and
            // be swallowed, leaving hits empty — but the envelope shape itself
            // (status=ok, hits present under result) is what we assert here.
            var svc = new SourceSearchService(fixture.Index, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "B" });
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.Equal("SourceSearchCompleted", obj["code"]?.ToString());
            Assert.NotNull(obj["result"]?["hits"]);
        }

        [Fact]
        public void Search_ReadyIndex_EntryWithoutSnippet_IsStillScanned()
        {
            // issue #25 #3: SourceSnippet is never populated for the searched
            // types (Procedure/DataProvider/WebPanel/Transaction) in production,
            // so the literal pre-filter must NOT exclude an entry just because its
            // snippet is empty — otherwise a token that lives in the body but not
            // the object name is silently dropped as an empty-success. The entry
            // must survive the pre-filter and be scanned (its full source read).
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Name = "Foo",
                    Type = "Procedure",
                    Calls = new List<string>(),
                    CalledBy = new List<string>(),
                    SourceSnippet = null // real production shape for code objects
                }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            idx.MarkIndexComplete(entries.Count);

            // ObjectService null → FindObject throws & is swallowed, so no hit is
            // produced, but the entry still enters the scan loop (scannedObjects>=1).
            var svc = new SourceSearchService(idx, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "TokenNotInObjectName" });
            var obj = JObject.Parse(json);

            Assert.Equal("SourceSearchCompleted", obj["code"]?.ToString());
            Assert.False(obj["result"]?["partial"]?.ToObject<bool>() ?? true);
            // Pre-fix this was 0 (entry wrongly filtered out by the snippet pre-filter).
            Assert.Equal(1, obj["result"]?["scannedObjects"]?.ToObject<int>());
        }

        [Fact]
        public void Search_UsesIndexedFullSourceAndSkipsUnrelatedObjects()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Name = "HasToken", Type = "Procedure",
                    FullSource = "msg(IndexedToken)",
                    SourceSnippet = null
                },
                new SearchIndex.IndexEntry
                {
                    Name = "Other", Type = "Procedure",
                    FullSource = "msg(Unrelated)",
                    SourceSnippet = null
                }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            idx.MarkIndexComplete(entries.Count);

            // Null ObjectService proves the hit came from FullSource; no SDK read is
            // available to the search loop in this fixture.
            var svc = new SourceSearchService(idx, null);
            var obj = JObject.Parse(svc.SearchAsJson(new SourceSearchCriteria { Pattern = "IndexedToken" }));

            Assert.Equal("SourceSearchCompleted", obj["code"]?.ToString());
            Assert.Equal(1, obj["result"]?["scannedObjects"]?.ToObject<int>());
            Assert.Equal("HasToken", obj["result"]?["hits"]?[0]?["objectName"]?.ToString());
        }

        [Fact]
        public void Search_PartialIndex_ZeroHits_ReturnsDistinctCode()
        {
            // issue #25 #1/#3: a zero result on a still-walking index must NOT look
            // like an authoritative empty-success. It carries a distinct code plus
            // partial:true and a hint.
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Name = "Foo",
                    Type = "Procedure",
                    Calls = new List<string>(),
                    CalledBy = new List<string>()
                }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries, markReady: false);
            idx.MarkUltraLiteReady(entries.Count);

            var svc = new SourceSearchService(idx, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "Whatever" });
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.Equal("PartialIndexNoMatch", obj["code"]?.ToString());
            Assert.True(obj["result"]?["partial"]?.ToObject<bool>());
            Assert.NotNull(obj["result"]?["partialHint"]);
            Assert.Equal("UltraLiteReady", obj["result"]?["indexStatus"]?.ToString());
        }

        [Fact]
        public void Search_HardTimeoutExceeded_ReturnsTimeoutEnvelope()
        {
            // Pathological fixture: enough Procedure entries to make the
            // per-entry loop measurably exceed a 0ms budget. We use TimeoutMs=0
            // so the very first loop iteration trips the timeout regardless of
            // host speed — the assertion targets the envelope shape, not a real
            // wall-clock race.
            var entries = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < 1000; i++)
            {
                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = "P" + i,
                    Type = "Procedure",
                    Calls = new List<string>(),
                    CalledBy = new List<string>(),
                    SourceSnippet = "match"
                });
            }
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            idx.MarkIndexComplete(entries.Count);

            var svc = new SourceSearchService(idx, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "match",
                TimeoutMs = 0
            });
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.Equal("Timeout", obj["code"]?.ToString());
            Assert.NotNull(obj["result"]?["partialHits"]);
            Assert.NotNull(obj["result"]?["totalScanned"]);
            Assert.NotNull(obj["result"]?["totalObjects"]);
            Assert.NotNull(obj["result"]?["coverage"]?["requestedParts"]);
            Assert.NotNull(obj["result"]?["partExecution"]?["requestedParts"]);
            Assert.True(obj["result"]?["sliceContinuation"]?.ToObject<bool>() ?? false);
        }
    }
}
