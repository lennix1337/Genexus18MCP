using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Opt-in trigram pruning of source-search candidates (GXMCP_SOURCE_SEARCH_TRIGRAM_PRUNE).
    /// Issue #264 removed an earlier pre-filter because it pruned on text that was not known
    /// to be complete and current. The rule under test: a candidate is skipped only when its
    /// part is stored-and-fresh AND the postings prove the literal cannot occur; every other
    /// candidate is scanned as before, and the visible results do not depend on the flag.
    /// </summary>
    public class SourceSearchTrigramPruneTests : IDisposable
    {
        private const string Needle = "NeedleCall";
        private static readonly DateTime Stamp = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _tempDir;
        private readonly Func<string> _originalSetting;
        private readonly CultureInfo _originalCulture;

        public SourceSearchTrigramPruneTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-trigram-prune-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
            _originalSetting = SourceStoreService.TrigramPruneSetting;
            _originalCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        }

        public void Dispose()
        {
            SourceStoreService.TrigramPruneSetting = _originalSetting;
            CultureInfo.CurrentCulture = _originalCulture;
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private static SearchIndex.IndexEntry Entry(string name, DateTime lastUpdate) => new SearchIndex.IndexEntry
        {
            Name = name,
            Type = "WebPanel",
            Guid = Guid.NewGuid().ToString(),
            LastUpdate = lastUpdate
        };

        private static void Store(SearchIndex.IndexEntry entry, string text, DateTime stamp)
            => Assert.True(SourceStoreService.Instance.Put(entry.Guid, "rules", text, stamp, null));

        private static void Cache(SearchIndex.IndexEntry entry, string text)
            => Assert.True(ObjectService.CacheRawSourceFromReadPayload(Guid.Parse(entry.Guid), "rules",
                new JObject { ["source"] = text }, null, "mcp", false));

        [Fact]
        public void Flag_Is_Off_Unless_Exactly_One()
        {
            foreach (string value in new[] { null, "", "0", "true", "on", "yes" })
            {
                SourceStoreService.TrigramPruneSetting = () => value;
                Assert.False(SourceStoreService.TrigramPruneEnabled());
            }
            SourceStoreService.TrigramPruneSetting = () => " 1 ";
            Assert.True(SourceStoreService.TrigramPruneEnabled());
        }

        [Fact]
        public void Only_Plain_Literals_Are_Eligible()
        {
            var invariant = CultureInfo.InvariantCulture;
            Assert.Equal("Needle_Call1", SourceSearchService.GetTrigramPruneLiteral(
                new SourceSearchCriteria { Pattern = "Needle_Call1" }, invariant));

            foreach (string pattern in new[] { "Ne.dle", "Needle(", "a|bcd", "Need\\w", "Needle Call", "ab", "", null })
                Assert.Null(SourceSearchService.GetTrigramPruneLiteral(new SourceSearchCriteria { Pattern = pattern }, invariant));

            Assert.Null(SourceSearchService.GetTrigramPruneLiteral(
                new SourceSearchCriteria { Pattern = "Needle", Callee = "Needle" }, invariant));
            Assert.Null(SourceSearchService.GetTrigramPruneLiteral(
                new SourceSearchCriteria { Pattern = "Needle", ArgMatches = new Dictionary<int, string> { [0] = "x" } }, invariant));

            // Turkish/Azerbaijani case-insensitive matching maps I/i differently from the
            // invariant culture the postings use; case-sensitive matching is unaffected.
            var turkish = new CultureInfo("tr-TR");
            Assert.Null(SourceSearchService.GetTrigramPruneLiteral(new SourceSearchCriteria { Pattern = "Index" }, turkish));
            Assert.Equal("Index", SourceSearchService.GetTrigramPruneLiteral(
                new SourceSearchCriteria { Pattern = "Index", CaseSensitive = true }, turkish));
        }

        [Fact]
        public void Store_Proves_Absence_Only_For_A_Fresh_Record_Without_The_Literal()
        {
            var store = SourceStoreService.Instance;

            var without = Entry("Without", Stamp);
            Store(without, "Other(); x = 1;", Stamp);
            Assert.True(store.ProvesLiteralAbsent(without, "rules", Needle));

            var with = Entry("With", Stamp);
            Store(with, "y = needlecall();", Stamp);
            Assert.False(store.ProvesLiteralAbsent(with, "rules", Needle));

            var notStored = Entry("NotStored", Stamp);
            Assert.False(store.ProvesLiteralAbsent(notStored, "rules", Needle));

            // Issue #264: the record lacks the literal, but the entry moved past it, so the
            // stored body is not the current text and must not decide anything.
            var stale = Entry("Stale", Stamp.AddMinutes(1));
            Store(stale, "Other();", Stamp);
            Assert.False(store.ProvesLiteralAbsent(stale, "rules", Needle));

            // A body shorter than a trigram has an empty set: never pruned.
            var tiny = Entry("Tiny", Stamp);
            Store(tiny, "ab", Stamp);
            Assert.False(store.ProvesLiteralAbsent(tiny, "rules", Needle));

            Assert.False(store.ProvesLiteralAbsent(without, "rules", "ab"));
        }

        [Fact]
        public void A_Candidate_That_Is_Not_Stored_And_Fresh_Is_Never_Pruned()
        {
            // Issue #264 regression: the stored body (no needle) is stale; the current text
            // in the part cache has the needle. Pruning on the stored body would hide it.
            var entry = Entry("StaleButCurrentInCache", Stamp.AddMinutes(1));
            Store(entry, "Other();", Stamp);
            Cache(entry, "x = " + Needle + "();");

            SourceStoreService.TrigramPruneSetting = () => "1";
            var result = Search(IndexOf(new[] { entry }), null);

            Assert.Equal(1, result["count"].Value<int>());
            var probe = result["partExecution"]["storeProbe"];
            Assert.True(probe["executed"].Value<bool>());
            Assert.Equal(1, probe["candidates"].Value<int>());
            Assert.Equal(0, probe["hits"].Value<int>());
        }

        [Fact]
        public void Hits_And_Pages_Are_The_Same_With_The_Flag_On_And_Off()
        {
            var entries = BuildMixedCorpus();
            var index = IndexOf(entries);

            SourceStoreService.TrigramPruneSetting = () => null;
            var off = Search(index, null);
            SourceStoreService.TrigramPruneSetting = () => "1";
            var on = Search(index, null);

            Assert.True(JToken.DeepEquals(off["hits"], on["hits"]), "hits differ:\n" + off["hits"] + "\n" + on["hits"]);
            Assert.Equal(3, on["count"].Value<int>());
            Assert.Equal(off["scannedObjects"].Value<int>(), on["scannedObjects"].Value<int>());
            Assert.True(JToken.DeepEquals(
                off["partExecution"]["parts"]["rules"]["scannedObjects"],
                on["partExecution"]["parts"]["rules"]["scannedObjects"]));

            var offProbe = off["partExecution"]["storeProbe"];
            Assert.False(offProbe["executed"].Value<bool>());
            Assert.Equal(0, offProbe["candidates"].Value<int>());
            var onProbe = on["partExecution"]["storeProbe"];
            Assert.True(onProbe["executed"].Value<bool>());
            Assert.Equal(entries.Length, onProbe["candidates"].Value<int>());
            // Only the stored, fresh, needle-free candidate is skipped.
            Assert.Equal(1, onProbe["hits"].Value<int>());

            // Page one hit at a time and resume through the cursor: same sequence either way.
            SourceStoreService.TrigramPruneSetting = () => null;
            var offPages = Paginate(index);
            SourceStoreService.TrigramPruneSetting = () => "1";
            var onPages = Paginate(index);
            Assert.Equal(offPages.Count, onPages.Count);
            for (int i = 0; i < offPages.Count; i++)
                Assert.True(JToken.DeepEquals(offPages[i], onPages[i]), "page " + i + " differs");
        }

        private SearchIndex.IndexEntry[] BuildMixedCorpus()
        {
            var storedWith = Entry("A_StoredWithNeedle", Stamp);
            Store(storedWith, "a = 1;\nb = " + Needle + "();", Stamp);

            var storedWithout = Entry("B_StoredWithoutNeedle", Stamp);
            Store(storedWithout, "Other();\nMore();", Stamp);

            var cachedWith = Entry("C_CachedWithNeedle", Stamp);
            Cache(cachedWith, Needle.ToLowerInvariant() + "();");

            var cachedWithout = Entry("D_CachedWithoutNeedle", Stamp);
            Cache(cachedWithout, "Nothing();");

            var staleStored = Entry("E_StaleStoredCurrentCached", Stamp.AddMinutes(1));
            Store(staleStored, "Other();", Stamp);
            Cache(staleStored, "c = " + Needle + "();");

            return new[] { storedWith, storedWithout, cachedWith, cachedWithout, staleStored };
        }

        private static IndexCacheService IndexOf(SearchIndex.IndexEntry[] entries)
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Length);
            return index;
        }

        // A cursor is bound to the index instance that issued it, so one index serves
        // every page and both flag settings.
        private static JToken Search(IndexCacheService index, string cursor, int maxResults = 10)
        {
            string raw = new SourceSearchService(index, new ObjectService(null, null))
                .SearchAsJson(new SourceSearchCriteria
                {
                    Pattern = Needle,
                    Scope = new List<string> { "rules" },
                    MaxResults = maxResults,
                    TimeoutMs = 30000,
                    Cursor = cursor
                });
            var result = JObject.Parse(raw)["result"];
            Assert.True(result != null, raw);
            return result;
        }

        private static List<JToken> Paginate(IndexCacheService index)
        {
            var pages = new List<JToken>();
            string cursor = null;
            for (int guard = 0; guard < 20; guard++)
            {
                var page = Search(index, cursor, maxResults: 1);
                pages.Add(page["hits"]);
                cursor = page["nextCursor"]?.ToString();
                if (string.IsNullOrEmpty(cursor)) return pages;
            }
            throw new InvalidOperationException("pagination did not terminate");
        }
    }
}
