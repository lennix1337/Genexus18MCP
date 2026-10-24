using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issues #650 and #651. The index walk records an object's folder as
    /// <c>ParentPath</c>, and for an object whose SDK chain ends at the "Root Module"
    /// container that path already starts with "Root Module" (so does <c>Path</c>). The
    /// lite walk built <c>ParentFolderPath</c> as <c>"Root Module/" + ParentPath</c>, which
    /// gave "Root Module/Root Module/Folder" and made <c>pathPrefix="Root Module/Folder/"</c>
    /// match only the entries that were later rebuilt one at a time (an edit, the watcher).
    /// </summary>
    public class ParentFolderPathTests
    {
        private static SearchIndex.IndexEntry Entry(string name, string type, string parentPath, string parentFolderPath, string module = "Root Module")
        {
            return new SearchIndex.IndexEntry
            {
                Guid = System.Guid.NewGuid().ToString(),
                Name = name,
                Type = type,
                Parent = parentPath?.Split('/').Last(),
                ParentPath = parentPath,
                ParentFolderPath = parentFolderPath,
                Path = parentPath + "/" + name,
                Module = module,
            };
        }

        /// <summary>The six objects of the report: what the lite walk wrote, plus the one rebuilt later.</summary>
        private static List<SearchIndex.IndexEntry> SampleFolderObjects() => new List<SearchIndex.IndexEntry>
        {
            Entry("SchedulePlan", "Procedure", "Root Module/SampleFolder", "Root Module/SampleFolder"),
            Entry("RunRecord", "Transaction", "Root Module/SampleFolder", "Root Module/Root Module/SampleFolder"),
            Entry("RunDependency", "Transaction", "Root Module/SampleFolder", "Root Module/Root Module/SampleFolder"),
            Entry("RunControl", "Transaction", "Root Module/SampleFolder", "Root Module/Root Module/SampleFolder"),
            Entry("RunLog", "Transaction", "Root Module/SampleFolder", "Root Module/Root Module/SampleFolder"),
            Entry("ReserveSchedule", "Procedure", "Root Module/SampleFolder", "Root Module/Root Module/SampleFolder"),
            Entry("RunTask", "Procedure", "Root Module", "Root Module/Root Module"),
        };

        /// <summary>
        /// <c>ListService</c> keeps one static response cache, invalidated only when the index's
        /// <c>LastUpdated</c> moves forward or its graph revision changes. Two fixtures loaded
        /// within one clock tick look like the same index to it, and the same criteria then
        /// replay the other test's answer, so every list here starts from an empty cache.
        /// </summary>
        private static ListService ServiceOver(IndexCacheService cache)
        {
            ListService.InvalidateCache();
            return new ListService(cache);
        }

        private static string[] Names(string json) =>
            ((JArray)JObject.Parse(json)["results"]).Select(r => (string)r["name"]).OrderBy(n => n).ToArray();

        [Theory]
        [InlineData(null, "Root Module")]
        [InlineData("", "Root Module")]
        [InlineData("   ", "Root Module")]
        [InlineData("Root Module", "Root Module")]
        [InlineData("root module", "root module")]
        [InlineData("Root Module/SampleFolder", "Root Module/SampleFolder")]
        [InlineData("SampleFolder", "Root Module/SampleFolder")]
        [InlineData("SampleFolder/Sub", "Root Module/SampleFolder/Sub")]
        [InlineData("Root ModuleX", "Root Module/Root ModuleX")]
        public void ComposeParentFolderPath_PrefixesTheRootOnce(string parentPath, string expected)
        {
            Assert.Equal(expected, IndexCacheService.ComposeParentFolderPath(parentPath));
        }

        [Fact]
        public void PathPrefix_FindsEveryObjectOfAFolder_AfterTheIndexIsNormalized()
        {
            var index = new SearchIndex();
            foreach (var e in SampleFolderObjects()) index.Objects[e.Type + ":" + e.Name] = e;

            new IndexCacheService().NormalizeLegacyHierarchy(index);

            var cache = new IndexCacheService();
            cache.LoadFromEntries(index.Objects.Values);
            var service = ServiceOver(cache);

            Assert.Equal(
                new[] { "ReserveSchedule", "RunControl", "RunDependency", "RunLog", "RunRecord", "SchedulePlan" },
                Names(service.List(new ListCriteria { PathPrefix = "Root Module/SampleFolder/", Limit = 200 })));
            ListService.InvalidateCache();
            Assert.Equal(
                new[] { "ReserveSchedule", "RunControl", "RunDependency", "RunLog", "RunRecord", "SchedulePlan" },
                Names(service.List(new ListCriteria { PathPrefix = "Root Module/SampleFolder", Limit = 200 })));
        }

        [Fact]
        public void PathPrefix_MissesTheLiteEntries_WhenTheIndexIsNotNormalized()
        {
            // The state the reporter saw: one object of six, the one whose entry was rebuilt later.
            var cache = new IndexCacheService();
            cache.LoadFromEntries(SampleFolderObjects());
            var service = ServiceOver(cache);

            Assert.Equal(
                new[] { "SchedulePlan" },
                Names(service.List(new ListCriteria { PathPrefix = "Root Module/SampleFolder/", Limit = 200 })));
        }

        [Fact]
        public void Normalization_LeavesRootObjectsAtTheRootAndDoesNotDoublePrefix()
        {
            var index = new SearchIndex();
            foreach (var e in SampleFolderObjects()) index.Objects[e.Type + ":" + e.Name] = e;

            new IndexCacheService().NormalizeLegacyHierarchy(index);

            Assert.Equal("Root Module", index.Objects["Procedure:RunTask"].ParentFolderPath);
            Assert.All(
                index.Objects.Values.Where(e => e.Name != "RunTask"),
                e => Assert.Equal("Root Module/SampleFolder", e.ParentFolderPath));
        }

        [Fact]
        public void Normalization_KeepsAFolderPathWhenThereIsNoParentPathToDeriveItFrom()
        {
            var index = new SearchIndex();
            var orphan = new SearchIndex.IndexEntry
            {
                Name = "Orphan", Type = "Procedure", ParentPath = "", ParentFolderPath = "Root Module/Legacy", Path = "Orphan"
            };
            index.Objects["Procedure:Orphan"] = orphan;

            new IndexCacheService().NormalizeLegacyHierarchy(index);

            // InferLegacyParentPath may fill ParentPath; what matters is that a folder path
            // that has nothing to be derived from is not replaced by "Root Module".
            Assert.StartsWith("Root Module", orphan.ParentFolderPath);
        }

        [Fact]
        public void ObjectContext_ReportsTheContainerNextToTheModule()
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(SampleFolderObjects());
            var graphTools = new GraphToolsService(null, null, cache, new CallerGraphService(cache));

            var inFolder = (JObject)JObject.Parse(graphTools.ObjectContext(new JObject { ["name"] = "SchedulePlan" }))["result"]["object"];
            var atRoot = (JObject)JObject.Parse(graphTools.ObjectContext(new JObject { ["name"] = "RunTask" }))["result"]["object"];

            Assert.Equal("Root Module/SampleFolder/SchedulePlan", (string)inFolder["path"]);
            Assert.Equal("Root Module", (string)inFolder["module"]);
            Assert.Equal("SampleFolder", (string)inFolder["parent"]);
            Assert.Equal("Root Module/SampleFolder", (string)inFolder["parentPath"]);
            Assert.Equal("Root Module/SampleFolder", (string)inFolder["parentFolderPath"]);

            // Two objects that `module` alone cannot tell apart are told apart by the container.
            Assert.Equal("Root Module", (string)atRoot["module"]);
            Assert.Equal("Root Module", (string)atRoot["parentFolderPath"]);
            Assert.NotEqual((string)inFolder["parentFolderPath"], (string)atRoot["parentFolderPath"]);
        }

        [Fact]
        public void EveryFolderPathProducerGoesThroughTheIdempotentComposer()
        {
            // The lite walk and the SDK-backed listing need an SDK Module/Folder chain, which no
            // test here can build; what is pinned is that none of them concatenates the root
            // prefix itself again.
            string kb = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "KbService.cs"));
            string list = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ListService.cs"));
            string index = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "IndexCacheService.cs"));

            Assert.Equal(1, SourceAssert.Count(kb, "ParentFolderPath = IndexCacheService.ComposeParentFolderPath(hierarchy.ParentPath)"));
            Assert.Equal(0, SourceAssert.Count(kb, "\"Root Module/\" + "));
            Assert.Equal(2, SourceAssert.Count(list, "IndexCacheService.ComposeParentFolderPath("));
            Assert.Equal(0, SourceAssert.Count(list, "\"Root Module/\" + "));
            Assert.Equal(1, SourceAssert.Count(index, "ParentFolderPath = SearchIndex.InternShared(ComposeParentFolderPath(hierarchy.ParentPath))"));
        }
    }
}
