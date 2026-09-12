using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 002: derived Type/BusinessDomain indexes so SearchService/ListService can
    // intersect a candidate set instead of scanning index.Objects.Values when a
    // type/domain filter narrows the query. These tests pin two things: (1) the
    // indexes themselves are populated correctly by the same mutation hooks that
    // maintain ChildrenByParent, and (2) the indexed prefilter path (built via
    // AddOrUpdateBatch, which builds TypeIndex/DomainIndex) returns EXACTLY the same
    // result set as the full-scan fallback path (built via LoadFromEntries, which
    // leaves TypeIndex/DomainIndex null) — i.e. this is a perf change, not a
    // semantics change.
    public class SecondaryIndexTests
    {
        private static List<SearchIndex.IndexEntry> SampleEntries()
        {
            return new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", BusinessDomain = "Sales", Description = "handles orders", Guid = "g1" },
                new SearchIndex.IndexEntry { Name = "Proc2", Type = "Procedure", BusinessDomain = "HR", Description = "handles employees", Guid = "g2" },
                new SearchIndex.IndexEntry { Name = "Trn1", Type = "Transaction", BusinessDomain = "Sales", Description = "order transaction", Guid = "g3" },
                new SearchIndex.IndexEntry { Name = "Wp1", Type = "WebPanel", BusinessDomain = "Sales", Description = "dashboard", Guid = "g4" },
                new SearchIndex.IndexEntry { Name = "Attr1", Type = "Attribute", BusinessDomain = "HR", Description = "employee id", Guid = "g5" },
            };
        }

        // ── Step 1: the indexes themselves ──────────────────────────────────────

        [Fact]
        public void TypeIndex_And_DomainIndex_ContainExpectedKeys_AfterInsert()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());

            var idx = svc.TryGetLoadedIndex();
            Assert.NotNull(idx.TypeIndex);
            Assert.NotNull(idx.DomainIndex);

            Assert.Equal(new[] { "Procedure:Proc1", "Procedure:Proc2" },
                idx.TypeIndex["Procedure"].OrderBy(k => k));
            Assert.Equal(new[] { "Transaction:Trn1" }, idx.TypeIndex["Transaction"]);
            Assert.Equal(new[] { "WebPanel:Wp1" }, idx.TypeIndex["WebPanel"]);
            Assert.Equal(new[] { "Attribute:Attr1" }, idx.TypeIndex["Attribute"]);

            Assert.Equal(new[] { "Procedure:Proc1", "Transaction:Trn1", "WebPanel:Wp1" },
                idx.DomainIndex["Sales"].OrderBy(k => k));
            Assert.Equal(new[] { "Attribute:Attr1", "Procedure:Proc2" },
                idx.DomainIndex["HR"].OrderBy(k => k));
        }

        [Fact]
        public void RemoveEntryByGuid_DropsKeyFromTypeAndDomainIndex()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());

            svc.RemoveEntryByGuid("g1"); // Proc1 (Procedure/Sales)

            var idx = svc.TryGetLoadedIndex();
            Assert.DoesNotContain("Procedure:Proc1", idx.TypeIndex["Procedure"]);
            Assert.DoesNotContain("Procedure:Proc1", idx.DomainIndex["Sales"]);
            // sibling of the same type/domain must survive
            Assert.Contains("Transaction:Trn1", idx.DomainIndex["Sales"]);
        }

        [Fact]
        public void RemoveEntry_ByTypeAndName_DropsKeyFromTypeAndDomainIndex()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());

            svc.RemoveEntry("Procedure", "Proc2");

            var idx = svc.TryGetLoadedIndex();
            Assert.DoesNotContain("Procedure:Proc2", idx.TypeIndex["Procedure"]);
            Assert.DoesNotContain("Procedure:Proc2", idx.DomainIndex["HR"]);
        }

        [Fact]
        public void TypeIndex_NotSerialized_RebuiltFromObjectsOnly()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());
            var idx = svc.TryGetLoadedIndex();

            string json = idx.ToJson();
            Assert.DoesNotContain("TypeIndex", json);
            Assert.DoesNotContain("DomainIndex", json);

            var reloaded = SearchIndex.FromJson(json);
            Assert.Null(reloaded.TypeIndex); // derived, not persisted — rebuilt on load via BuildParentIndex
        }

        [Fact]
        public void PromoteSourceForSearch_PopulatesFullSourceAndSourceTokens()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = "g1" }
            });
            svc.EnsureSourceTokenIndex();
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:Proc1"];
            string source = "parm(in:&CustomerId); ProcessInvoicePayment();";

            Assert.True(svc.PromoteSourceForSearch(entry, source));
            Assert.Equal(source, entry.FullSource);
            Assert.Contains("Procedure:Proc1", svc.TryGetLoadedIndex().SourceTokenIndex["processinvoicepayment"]);
            Assert.False(svc.PromoteSourceForSearch(entry, "different source"));

            var reloaded = SearchIndex.FromJson(svc.TryGetLoadedIndex().ToJson());
            Assert.Equal(source, reloaded.Objects["Procedure:Proc1"].FullSource);
        }

        [Fact]
        public void PromoteSourceForSearch_RecordsConfirmedEmptySource()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "EmptyProc", Type = "Procedure", Guid = "empty-guid" }
            });
            svc.EnsureSourceTokenIndex();
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:EmptyProc"];

            Assert.True(svc.PromoteSourceForSearch(entry, string.Empty));
            Assert.NotNull(entry.FullSource);
            Assert.Empty(entry.FullSource);
            Assert.DoesNotContain("Procedure:EmptyProc", svc.TryGetLoadedIndex().SourceTokenIndex.Values.SelectMany(keys => keys));

            var reloaded = SearchIndex.FromJson(svc.TryGetLoadedIndex().ToJson());
            Assert.NotNull(reloaded.Objects["Procedure:EmptyProc"].FullSource);
            Assert.Empty(reloaded.Objects["Procedure:EmptyProc"].FullSource);
        }

        [Fact]
        public void PromoteSourceForSearch_RejectsOversizedSource()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = "g1" }
            });
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:Proc1"];

            Assert.False(svc.PromoteSourceForSearch(entry, new string('x', 256 * 1024 + 1)));
            Assert.Null(entry.FullSource);
        }

        // ── Step 3: indexed prefilter path vs full-scan fallback — same results ──

        private static IndexCacheService BuildIndexed()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries()); // builds TypeIndex/DomainIndex -> prefilter path
            svc.MarkIndexComplete(SampleEntries().Count);
            return svc;
        }

        private static IndexCacheService BuildFullScan()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(SampleEntries()); // TypeIndex/DomainIndex stay null -> fallback path
            return svc;
        }

        private static string[] SearchNames(IndexCacheService cache, string query, string typeFilter, string domainFilter)
        {
            var svc = new SearchService(cache);
            var json = svc.Search(query, typeFilter, domainFilter, limit: 50);
            var results = (JArray)JObject.Parse(json)["results"];
            return results.Select(r => r["name"].ToString()).ToArray();
        }

        private static string[] ListNames(IndexCacheService cache, string typeFilter)
        {
            var svc = new ListService(cache);
            var json = svc.ListObjects(filter: null, limit: 50, offset: 0, typeFilter: typeFilter);
            var results = (JArray)JObject.Parse(json)["results"];
            return results.Select(r => r["name"].ToString()).ToArray();
        }

        [Fact]
        public void Search_TypeOnly_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: "Procedure", domainFilter: null);
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: "Procedure", domainFilter: null);
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void Search_DomainOnly_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: null, domainFilter: "Sales");
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: null, domainFilter: "Sales");
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Trn1", "Wp1" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void Search_TypeAndDomain_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: "Procedure", domainFilter: "Sales");
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: "Procedure", domainFilter: "Sales");
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1" }, indexed);
        }

        [Fact]
        public void Search_TypeAndDescriptionSubstring_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "description:handles", typeFilter: "Procedure", domainFilter: null);
            var fullScan = SearchNames(BuildFullScan(), query: "description:handles", typeFilter: "Procedure", domainFilter: null);
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void Search_TypeAlias_IndexedPathMatchesFullScan()
        {
            // IsTypeMatch is alias-aware ("prc" contains-matches "Procedure") — the
            // indexed path must resolve aliases against TypeIndex bucket keys, not
            // require an exact key match.
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: "prc", domainFilter: null);
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: "prc", domainFilter: null);
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void List_TypeOnly_IndexedPathMatchesFullScan()
        {
            var indexed = ListNames(BuildIndexed(), typeFilter: "Procedure");
            var fullScan = ListNames(BuildFullScan(), typeFilter: "Procedure");
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }
    }
}
