using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Drives the selection that delete_kb_objects uses: ObjectTextService.TrySelectEntries with includeChildren,
    // which is the default of TextBatchOptions for that action.
    public sealed class TextSelectorContainerIdentityTests
    {
        private const string ModuleAGuid = "10000000-0000-4000-8000-00000000000a";
        private const string ModuleBGuid = "10000000-0000-4000-8000-00000000000b";
        private const string ProcsAGuid = "20000000-0000-4000-8000-00000000000a";
        private const string ProcsBGuid = "20000000-0000-4000-8000-00000000000b";
        private const string ProcAGuid = "30000000-0000-4000-8000-00000000000a";
        private const string ProcBGuid = "30000000-0000-4000-8000-00000000000b";

        [Theory]
        [InlineData(ProcsBGuid)]
        [InlineData(ProcsBGuid + "-folder")]
        public void A_folder_identity_with_children_selects_nothing_from_a_same_named_folder_in_another_module(string target)
        {
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                target,
                new JObject(),
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Empty(entries);
        }

        [Theory]
        [InlineData(ModuleBGuid)]
        [InlineData(ModuleBGuid + "-module")]
        public void A_module_identity_with_children_selects_nothing(string target)
        {
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                target,
                new JObject(),
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Empty(entries);
        }

        [Theory]
        [InlineData(ProcBGuid)]
        [InlineData(ProcBGuid + "-object")]
        public void An_object_identity_selects_only_that_object(string target)
        {
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                target,
                new JObject(),
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Equal(new[] { "SynthProcB" }, entries.Select(entry => entry.Name).ToArray());
        }

        [Fact]
        public void A_type_filter_limits_the_children_of_a_container_too()
        {
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                "SynthB",
                new JObject { ["type"] = "Transaction" },
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Empty(entries);
        }

        [Fact]
        public void A_module_name_with_children_still_expands_to_its_own_objects()
        {
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                "SynthB",
                new JObject(),
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Contains(entries, entry => entry.Name == "SynthProcB");
            Assert.DoesNotContain(entries, entry => entry.Name == "SynthProcA");
        }

        [Fact]
        public void A_folder_name_with_children_still_selects_the_children_of_every_same_named_folder()
        {
            // The expansion is by name, not by identity: the same-named folder of the other module keeps its children.
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                "Procs",
                new JObject(),
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Equal(new[] { "SynthProcA", "SynthProcB" }, entries.Select(entry => entry.Name).ToArray());
        }

        [Fact]
        public void A_module_filter_narrows_a_folder_name_with_children_to_its_own_module()
        {
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                "Procs",
                new JObject { ["module"] = "SynthB" },
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Equal(new[] { "SynthProcB" }, entries.Select(entry => entry.Name).ToArray());
        }

        [Fact]
        public void A_path_prefix_narrows_a_folder_name_with_children_to_its_own_path()
        {
            ObjectTextService service = BuildService();

            bool selected = service.TrySelectEntries(
                "Procs",
                new JObject { ["pathPrefix"] = "SynthB/Procs" },
                allowAll: false,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Equal(new[] { "SynthProcB" }, entries.Select(entry => entry.Name).ToArray());
        }

        private static ObjectTextService BuildService()
        {
            var entries = new[]
            {
                Module("SynthA", ModuleAGuid),
                Module("SynthB", ModuleBGuid),
                Folder("Procs", "SynthA", ProcsAGuid, "SynthA"),
                Folder("Procs", "SynthB", ProcsBGuid, "SynthB"),
                Procedure("SynthProcA", "SynthA", "SynthA/Procs", ProcAGuid),
                Procedure("SynthProcB", "SynthB", "SynthB/Procs", ProcBGuid)
            };
            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries);
            cache.MarkIndexComplete(entries.Length);
            return new ObjectTextService(null, cache);
        }

        private static SearchIndex.IndexEntry Module(string name, string guid)
        {
            return new SearchIndex.IndexEntry
            {
                Type = "Module",
                Name = name,
                Module = name,
                Path = name,
                Guid = guid,
                EntityKey = guid + "-module"
            };
        }

        private static SearchIndex.IndexEntry Folder(string name, string module, string guid, string parentPath)
        {
            return new SearchIndex.IndexEntry
            {
                Type = "Folder",
                Name = name,
                Module = module,
                Parent = parentPath,
                ParentPath = parentPath,
                ParentFolderPath = parentPath,
                Path = parentPath + "/" + name,
                Guid = guid,
                EntityKey = guid + "-folder"
            };
        }

        private static SearchIndex.IndexEntry Procedure(string name, string module, string parentPath, string guid)
        {
            return new SearchIndex.IndexEntry
            {
                Type = "Procedure",
                Name = name,
                Module = module,
                Parent = parentPath,
                ParentPath = parentPath,
                ParentFolderPath = parentPath,
                Path = parentPath + "/" + name,
                Guid = guid,
                EntityKey = guid + "-object"
            };
        }
    }
}
