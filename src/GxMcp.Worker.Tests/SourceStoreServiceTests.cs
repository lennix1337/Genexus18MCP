using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceStoreServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public SourceStoreServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-test-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void TrigramExtractor_ExtractTrigrams_ProducesNormalizedTrigrams()
        {
            var trigrams = TrigramExtractor.ExtractTrigrams("Customer");
            Assert.Contains("cus", trigrams);
            Assert.Contains("ust", trigrams);
            Assert.Contains("sto", trigrams);
            Assert.Contains("tom", trigrams);
            Assert.Contains("ome", trigrams);
            Assert.Contains("mer", trigrams);
            Assert.Equal(6, trigrams.Count);

            Assert.Empty(TrigramExtractor.ExtractTrigrams("ab"));
            Assert.Empty(TrigramExtractor.ExtractTrigrams(null));
        }

        [Fact]
        public void TrigramExtractor_ExtractLiteralRuns_HandlesEscapesAndQuantifiers()
        {
            var runs = TrigramExtractor.ExtractLiteralRuns("Customer_Id");
            Assert.Single(runs);
            Assert.Equal("Customer_Id", runs[0]);

            // Optional character quantifier * breaks runs
            var optionalRuns = TrigramExtractor.ExtractLiteralRuns("Cust*omer");
            Assert.Equal(2, optionalRuns.Count);
            Assert.Equal("Cus", optionalRuns[0]);
            Assert.Equal("omer", optionalRuns[1]);

            // Character class is skipped
            var classRuns = TrigramExtractor.ExtractLiteralRuns("Invoice[0-9]+Amount");
            Assert.Equal(2, classRuns.Count);
            Assert.Equal("Invoice", classRuns[0]);
            Assert.Equal("Amount", classRuns[1]);
        }

        [Fact]
        public void TrigramExtractor_ExtractRequiredTrigramSets_AlternationAndWildcard()
        {
            // Simple pattern
            var single = TrigramExtractor.ExtractRequiredTrigramSets("CustomerQuery", null);
            Assert.NotNull(single);
            Assert.Single(single);
            Assert.Contains("cus", single[0]);

            // Alternation
            var alt = TrigramExtractor.ExtractRequiredTrigramSets("Customer|Invoice", null);
            Assert.NotNull(alt);
            Assert.Equal(2, alt.Count);
            Assert.Contains("cus", alt[0]);
            Assert.Contains("inv", alt[1]);

            // Branch with wildcard/no literal runs >= 3 chars returns null (can match anything)
            var wildcard = TrigramExtractor.ExtractRequiredTrigramSets("Customer|.*", null);
            Assert.Null(wildcard);
        }

        [Fact]
        public void TrigramExtractor_IntersectPostings_CorrectlyFiltersKeys()
        {
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["cus"] = new HashSet<string> { "doc1", "doc2" },
                ["ust"] = new HashSet<string> { "doc1", "doc2" },
                ["sto"] = new HashSet<string> { "doc1" },
                ["tom"] = new HashSet<string> { "doc1" },
                ["inv"] = new HashSet<string> { "doc3" },
                ["nvo"] = new HashSet<string> { "doc3" }
            };

            var branches = TrigramExtractor.ExtractRequiredTrigramSets("Custom", null);
            var candidates = TrigramExtractor.IntersectPostings(index, branches, new[] { "doc1", "doc2", "doc3" });

            Assert.Single(candidates);
            Assert.Contains("doc1", candidates);

            // Alternation test
            var altBranches = TrigramExtractor.ExtractRequiredTrigramSets("Custom|Invo", null);
            var altCandidates = TrigramExtractor.IntersectPostings(index, altBranches, new[] { "doc1", "doc2", "doc3" });
            Assert.Equal(2, altCandidates.Count);
            Assert.Contains("doc1", altCandidates);
            Assert.Contains("doc3", altCandidates);
        }

        [Fact]
        public void SourceStoreService_PutAndTryGet_SavesAndRetrievesSource()
        {
            string guid = "11111111-2222-3333-4444-555555555555";
            string part = "Source";
            string code = "Procedure DoSomething\n// Sample comment\n&Total = 100\nEndProc\n";
            var now = DateTime.UtcNow;

            bool putOk = SourceStoreService.Instance.Put(guid, part, code, now, "v1");
            Assert.True(putOk);

            bool getOk = SourceStoreService.Instance.TryGet(guid, part, out string retrieved);
            Assert.True(getOk);
            Assert.Equal(code, retrieved);
        }

        [Fact]
        public void SourceStoreService_CoverageAndFreshness_CalculatedCorrectly()
        {
            string guid1 = "aaaa1111-2222-3333-4444-555555555555";
            string guid2 = "bbbb1111-2222-3333-4444-555555555555";
            var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            SourceStoreService.Instance.Put(guid1, "Source", "code 1", baseTime, "v1");
            SourceStoreService.Instance.Put(guid2, "Source", "code 2", baseTime, "v1");

            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Guid = guid1,
                    Name = "Proc1",
                    Type = "Procedure",
                    LastUpdate = baseTime // fresh
                },
                new SearchIndex.IndexEntry
                {
                    Guid = guid2,
                    Name = "Proc2",
                    Type = "Procedure",
                    LastUpdate = baseTime.AddMinutes(5) // stale (> baseTime + 2s)
                },
                new SearchIndex.IndexEntry
                {
                    Guid = "cccc1111-2222-3333-4444-555555555555",
                    Name = "Proc3",
                    Type = "Procedure",
                    LastUpdate = baseTime // unindexed / not in store
                }
            };

            var coverage = SourceStoreService.Instance.GetCoverage(entries, null);
            Assert.Equal(3, coverage.TotalObjects);
            Assert.Equal(1, coverage.StoredObjects); // guid1
            Assert.Equal(2, coverage.StaleObjects);  // guid2 plus the missing guid3 record

            Assert.True(SourceStoreService.Instance.IsStoredAndFresh(entries[0], null));
            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entries[1], null));
            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entries[2], null));
        }

        [Fact]
        public void SourceStoreService_SearchStore_FindsPatternWithContext()
        {
            string guid = "dddd1111-2222-3333-4444-555555555555";
            string part = "Source";
            string code = "Line 1: init\nLine 2: setup\nLine 3: target statement\nLine 4: cleanup\nLine 5: return";

            SourceStoreService.Instance.Put(guid, part, code, DateTime.UtcNow, "v1");

            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "TargetProc",
                Type = "Procedure",
                Path = "\\Root\\TargetProc"
            };

            var criteria = new SourceSearchCriteria
            {
                Pattern = "target statement",
                MaxResults = 10
            };
            var rx = new Regex("target statement", RegexOptions.IgnoreCase);

            var hits = SourceStoreService.Instance.SearchStore(new[] { entry }, criteria, rx);

            Assert.Single(hits);
            var hit = hits[0];
            Assert.Equal("TargetProc", hit["objectName"]?.ToString());
            Assert.Equal(3, (int)hit["line"]);
            Assert.Equal("Line 3: target statement", hit["lineText"]?.ToString());

            var before = hit["contextBefore"] as JArray;
            Assert.NotNull(before);
            Assert.Equal(2, before.Count);
            Assert.Equal("Line 1: init", before[0].ToString());
            Assert.Equal("Line 2: setup", before[1].ToString());

            var after = hit["contextAfter"] as JArray;
            Assert.NotNull(after);
            Assert.Equal(2, after.Count);
            Assert.Equal("Line 4: cleanup", after[0].ToString());
            Assert.Equal("Line 5: return", after[1].ToString());
        }

        [Fact]
        public void ScopeEligibilityRequiresEveryRequestedPart()
        {
            string guid = "12121212-2222-3333-4444-555555555555";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "SelectionPopup",
                Type = "WebPanel",
                LastUpdate = DateTime.UtcNow
            };

            SourceStoreService.Instance.Put(guid, "Events", "EventOnlyNeedle()", entry.LastUpdate, "v1");
            Assert.True(SourceStoreService.Instance.IsStoredAndFresh(entry, new List<string> { "events" }));
            Assert.True(SourceStoreService.Instance.IsStoredAndFresh(entry, new List<string> { "source" }));
            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entry, new List<string> { "webForm" }));
            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entry, new List<string> { "events", "rules" }));

            SourceStoreService.Instance.Put(guid, "WebForm", "FormNeedle()", entry.LastUpdate, "v1");
            Assert.True(SourceStoreService.Instance.IsStoredAndFresh(entry, new List<string> { "webForm" }));
            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entry, new List<string> { "events", "rules" }));
        }

        [Fact]
        public void SearchStoreHonorsRequestedPartAndDoesNotRelabelStoredHits()
        {
            string guid = "13131313-2222-3333-4444-555555555555";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "SelectionPopup",
                Type = "WebPanel",
                LastUpdate = DateTime.UtcNow
            };
            SourceStoreService.Instance.Put(guid, "Events", "EventOnlyNeedle()", entry.LastUpdate, "v1");
            SourceStoreService.Instance.Put(guid, "WebForm", "FormOnlyNeedle()", entry.LastUpdate, "v1");

            var criteria = new SourceSearchCriteria
            {
                Pattern = "OnlyNeedle",
                Scope = new List<string> { "webForm" },
                MaxResults = 10
            };
            var webFormHits = SourceStoreService.Instance.SearchStore(
                new[] { entry }, criteria, new Regex("OnlyNeedle", RegexOptions.IgnoreCase));
            Assert.Single(webFormHits);
            Assert.Equal("WebForm", webFormHits[0]["part"]?.ToString());

            criteria.Scope = new List<string> { "events" };
            var eventHits = SourceStoreService.Instance.SearchStore(
                new[] { entry }, criteria, new Regex("OnlyNeedle", RegexOptions.IgnoreCase));
            Assert.Single(eventHits);
            Assert.Equal("Events", eventHits[0]["part"]?.ToString());
        }

        [Fact]
        public void CoverageReportsEachRequestedPartIndependently()
        {
            string guid = "abababab-2222-3333-4444-555555555555";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "SelectionPopup",
                Type = "WebPanel",
                LastUpdate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            };
            SourceStoreService.Instance.Put(guid, "Events", "event", entry.LastUpdate, "v1");
            SourceStoreService.Instance.Put(guid, "WebForm", "form", entry.LastUpdate.AddMinutes(1), "v2");

            var coverage = SourceStoreService.Instance.GetCoverage(
                new[] { entry }, new List<string> { "source", "webForm", "rules" });

            Assert.Equal(1, coverage.PartsByPart["events"].StoredObjects);
            Assert.Equal(1, coverage.PartsByPart["webform"].StoredObjects);
            Assert.Equal(0, coverage.PartsByPart["rules"].StoredObjects);
            Assert.Equal(1, coverage.PartsByPart["rules"].TotalObjects);
            Assert.Equal(0, coverage.StoredObjects); // one requested part is missing
        }

        [Fact]
        public void FreshnessRejectsMissingPerPartRecordFile()
        {
            string guid = "abababab-3333-4444-5555-666666666666";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "MissingFile",
                Type = "Procedure",
                LastUpdate = DateTime.UtcNow
            };
            Assert.True(SourceStoreService.Instance.Put(guid, "Source", "code", entry.LastUpdate, "v1"));

            string path = RecordPath(_tempDir, guid, "Source");
            Assert.True(File.Exists(path));
            File.Delete(path);

            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entry, null));
            Assert.False(SourceStoreService.Instance.TryGet(guid, "Source", out _));
            var coverage = SourceStoreService.Instance.GetCoverage(new[] { entry }, null);
            Assert.Equal(0, coverage.StoredObjects);
            Assert.Equal(1, coverage.StaleObjects);
        }

        [Fact]
        public void ReloadedCatalogDoesNotCertifyAMissingPerPartFile()
        {
            string guid = "abababab-3333-4444-5555-666666666667";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "ReloadedMissingFile",
                Type = "Procedure",
                LastUpdate = DateTime.UtcNow
            };
            Assert.True(SourceStoreService.Instance.Put(guid, "Source", "code", entry.LastUpdate, "v1"));
            SourceStoreService.Instance.FlushCatalog();
            File.Delete(RecordPath(_tempDir, guid, "Source"));

            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);

            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entry, null));
            Assert.False(SourceStoreService.Instance.TryGet(guid, "Source", out _));
        }

        [Fact]
        public void FreshnessRejectsCorruptPerPartRecordFile()
        {
            string guid = "abababab-3333-4444-5555-777777777777";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "CorruptFile",
                Type = "Procedure",
                LastUpdate = DateTime.UtcNow
            };
            Assert.True(SourceStoreService.Instance.Put(guid, "Source", "code", entry.LastUpdate, "v1"));
            File.WriteAllBytes(RecordPath(_tempDir, guid, "Source"), new byte[] { 0x01, 0x02, 0x03 });

            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entry, null));
            var coverage = SourceStoreService.Instance.GetCoverage(new[] { entry }, null);
            Assert.Equal(0, coverage.StoredObjects);
            Assert.Equal(1, coverage.StaleObjects);
        }

        [Fact]
        public void PutRepairsAMissingRecordWhenTheContentHashIsUnchanged()
        {
            string guid = "abababab-3333-4444-5555-888888888888";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "Repairable",
                Type = "Procedure",
                LastUpdate = DateTime.UtcNow
            };
            Assert.True(SourceStoreService.Instance.Put(guid, "Source", "same code", entry.LastUpdate, "v1"));
            File.Delete(RecordPath(_tempDir, guid, "Source"));

            Assert.True(SourceStoreService.Instance.Put(guid, "Source", "same code", entry.LastUpdate, "v1"));
            Assert.True(SourceStoreService.Instance.IsStoredAndFresh(entry, null));
            Assert.True(SourceStoreService.Instance.TryGet(guid, "Source", out string source));
            Assert.Equal("same code", source);
        }

        [Fact]
        public void EmptySourceIsAValidPerPartRecord()
        {
            string guid = "abababab-3333-4444-5555-999999999999";
            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "EmptyPart",
                Type = "Procedure",
                LastUpdate = DateTime.UtcNow
            };
            Assert.True(SourceStoreService.Instance.Put(guid, "Source", string.Empty, entry.LastUpdate, "v1"));
            Assert.True(SourceStoreService.Instance.IsStoredAndFresh(entry, null));
            Assert.True(SourceStoreService.Instance.TryGet(guid, "Source", out string source));
            Assert.Equal(string.Empty, source);
        }

        [Fact]
        public void SourceStoreService_FlushAndReloadCatalog_PreservesStoredState()
        {
            string guid = "eeee1111-2222-3333-4444-555555555555";
            string part = "Source";
            string code = "for each Customer\n  CustomerName = 'Test'\nendfor";

            SourceStoreService.Instance.Put(guid, part, code, DateTime.UtcNow, "v1");
            SourceStoreService.Instance.FlushCatalog();

            // Reset in-memory state and reload from the same directory
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);

            Assert.Equal(1, SourceStoreService.Instance.StoredRecordCount);
            bool ok = SourceStoreService.Instance.TryGet(guid, part, out string reloaded);
            Assert.True(ok);
            Assert.Equal(code, reloaded);
        }

        private static string RecordPath(string root, string guid, string part)
        {
            string normalizedGuid = guid.ToLowerInvariant();
            return Path.Combine(
                root,
                normalizedGuid.Substring(0, 2),
                normalizedGuid + "_" + part.ToLowerInvariant() + ".bin.gz");
        }
    }
}
