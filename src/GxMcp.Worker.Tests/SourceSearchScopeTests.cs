using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #27 item 4: genexus_search_source gains an objectName scope (search
    // inside one known object O(object) not O(KB)), a tunable timeoutMs, and a
    // resumable nextCursor on Timeout/Cancel. objectService is null here — the
    // FindObject call is swallowed, so these assert the scoping/cursor envelope
    // structure, not hit content.
    public class SourceSearchScopeTests
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
            // A non-whitelisted type to prove objectName bypasses the type gate.
            entries.Add(new SearchIndex.IndexEntry { Name = "MyPanel", Type = "SDPanel", SourceSnippet = "Foo" });
            svc.LoadFromEntries(entries);
            svc.MarkIndexComplete(entries.Count);
            return svc;
        }

        [Fact]
        public void ExtractLiteralTokens_IgnoresRegexWordBoundaryEscapes()
        {
            var tokens = SourceSearchService.ExtractLiteralTokens(@"\bparm\b", null);

            Assert.Contains("parm", tokens, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("bparm", tokens, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ObjectName_ScopesToSingleObject_TotalObjectsIsOne()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                ObjectName = "Proc42",
                MaxResults = 1000,
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal("SourceSearchCompleted", obj["code"]?.ToString());
            // Scoped to exactly one object out of 10k+ — O(object), not O(KB).
            Assert.Equal(1, obj["result"]!["totalObjects"]!.Value<int>());
        }

        [Fact]
        public void ObjectName_BypassesTypeWhitelist()
        {
            // MyPanel is an SDPanel — not in the Procedure/DataProvider/WebPanel/
            // Transaction whitelist — yet an explicit objectName reaches it.
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                ObjectName = "MyPanel",
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal(1, obj["result"]!["totalObjects"]!.Value<int>());
        }

        [Fact]
        public void ObjectName_CommaSeparated_ScopesToEach()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                ObjectName = "Proc1, Proc2 , Proc3",
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal(3, obj["result"]!["totalObjects"]!.Value<int>());
        }

        [Fact]
        public void Cancelled_CarriesResumableNextCursor()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                MaxResults = 1000,
                StartIndex = 250,
                TimeoutMs = 30000
            }, cts.Token);

            var obj = JObject.Parse(json);
            Assert.Equal("Cancelled", obj["code"]?.ToString());
            // Pre-cancelled trips on the first iteration at the resume index, so the
            // cursor to resume from equals the StartIndex we passed in.
            string nextCursor = obj["result"]!["nextCursor"]!.ToString();
            Assert.True(SourceSearchService.TryParseResumeCursor(
                nextCursor, out int entryIndex, out int skippedHits, out bool metadata));
            Assert.Equal(250, entryIndex);
            Assert.Equal(0, skippedHits);
            Assert.False(metadata);
            Assert.NotNull(obj["result"]!["resumeHint"]);
        }

        [Fact]
        public void ObjectName_ScopesMetadataFieldSearch_ToSingleObject()
        {
            // Plan 018: the fields=[description|caption|...] metadata branch used to
            // rebuild its candidate list from the full index, ignoring objectName.
            // Seed 3 objects sharing a NEEDLE token in Description; only "Target" should
            // be scanned/hit when objectName scopes the search.
            var svc = new IndexCacheService();
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Target", Type = "Procedure", Description = "has NEEDLE token" },
                new SearchIndex.IndexEntry { Name = "Other1", Type = "Procedure", Description = "also has NEEDLE token" },
                new SearchIndex.IndexEntry { Name = "Other2", Type = "Procedure", Description = "also has NEEDLE token" }
            };
            svc.LoadFromEntries(entries);
            svc.MarkIndexComplete(entries.Count);
            var search = new SourceSearchService(svc, objectService: null);

            var json = search.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "NEEDLE",
                ObjectName = "Target",
                Fields = new List<string> { "description" },
                MaxResults = 1000,
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            var hits = (JArray)obj["result"]!["hits"]!;
            Assert.Single(hits);
            Assert.Equal("Target", hits[0]!["objectName"]!.ToString());
        }

        [Fact]
        public void WordBoundaryPattern_DoesNotDropIndexedSourceHits()
        {
            var index = new IndexCacheService();
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Name = "IndexedParm",
                    Type = "Procedure",
                    FullSource = "parm(in:&Value)"
                }
            };
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Count);

            var json = new SourceSearchService(index, objectService: null).SearchAsJson(
                new SourceSearchCriteria
                {
                    Pattern = @"\bparm\b",
                    MaxResults = 10,
                    TimeoutMs = 30000
                });

            var result = JObject.Parse(json)["result"]!;
            Assert.Equal(1, result["count"]!.Value<int>());
            Assert.Equal(1, result["scannedObjects"]!.Value<int>());
            Assert.Equal("IndexedParm", result["hits"]![0]!["objectName"]!.ToString());
        }

        [Fact]
        public void OneCompleteSource_WithMultipleHits_IsPrioritizedBeforeSdkFallback()
        {
            var index = new IndexCacheService();
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "ColdCandidate", Type = "Procedure" },
                new SearchIndex.IndexEntry
                {
                    Name = "IndexedMany",
                    Type = "Procedure",
                    FullSource = "parm(in:&One)\nparm(in:&Two)"
                }
            };
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Count);

            var json = new SourceSearchService(index, objectService: null).SearchAsJson(
                new SourceSearchCriteria
                {
                    Pattern = "parm",
                    MaxResults = 2,
                    TimeoutMs = 30000
                });

            var result = JObject.Parse(json)["result"]!;
            Assert.Equal(2, result["count"]!.Value<int>());
            Assert.Equal(1, result["scannedObjects"]!.Value<int>());
            Assert.Null(result["unresolvedObjects"]);
        }

        [Fact]
        public void CompleteSourceCandidates_FillPageBeforeSdkFallbackCandidates()
        {
            var index = new IndexCacheService();
            var entries = new List<SearchIndex.IndexEntry>
            {
                // This candidate has no persisted source and would require the SDK.
                new SearchIndex.IndexEntry { Name = "ColdCandidate", Type = "Procedure" },
                new SearchIndex.IndexEntry
                {
                    Name = "IndexedOne",
                    Type = "Procedure",
                    FullSource = "parm(in:&One)"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "IndexedTwo",
                    Type = "Procedure",
                    FullSource = "parm(in:&Two)"
                }
            };
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Count);

            var json = new SourceSearchService(index, objectService: null).SearchAsJson(
                new SourceSearchCriteria
                {
                    Pattern = "parm",
                    MaxResults = 2,
                    TimeoutMs = 30000
                });

            var result = JObject.Parse(json)["result"]!;
            Assert.Equal(2, result["count"]!.Value<int>());
            // The complete indexed candidates fill the page before the unresolved
            // candidate can force the SDK fallback path.
            Assert.Equal(2, result["scannedObjects"]!.Value<int>());
            var hits = (JArray)result["hits"]!;
            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, hit => hit!["objectName"]!.ToString() == "IndexedOne");
            Assert.Contains(hits, hit => hit!["objectName"]!.ToString() == "IndexedTwo");
        }

        [Fact]
        public void StartIndex_BeyondEnd_CompletesEmpty()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                StartIndex = 999999,
                MaxResults = 10,
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal(0, obj["result"]!["count"]!.Value<int>());
            Assert.Equal(0, obj["result"]!["scannedObjects"]!.Value<int>());
        }
    }
}
