using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // module.toml marks a module root. An object inside a folder of its module must not turn that folder
    // into a module, and a tree exported before that fix must still validate.
    public sealed class SdkTextTreeModuleMetadataTests
    {
        [Fact]
        public void A_folder_inside_the_module_gives_the_module_root()
        {
            SearchIndex.IndexEntry entry = Entry("Root Module/WWPBaseObjects/WWPTemplates/Selection", "WWPBaseObjects");

            Assert.Equal("WWPBaseObjects", SdkTextTreeService.BuildModuleDirectory(entry, "WWPBaseObjects"));
        }

        [Fact]
        public void An_object_at_the_module_root_gives_the_module_root()
        {
            SearchIndex.IndexEntry entry = Entry("Root Module/WWPBaseObjects", "WWPBaseObjects");

            Assert.Equal("WWPBaseObjects", SdkTextTreeService.BuildModuleDirectory(entry, "WWPBaseObjects"));
        }

        [Fact]
        public void A_nested_module_keeps_its_own_path_and_drops_the_folders_under_it()
        {
            SearchIndex.IndexEntry entry = Entry("Root Module/Outer/Inner/Folder", "Inner");

            Assert.Equal(Path.Combine("Outer", "Inner"), SdkTextTreeService.BuildModuleDirectory(entry, "Inner"));
        }

        [Fact]
        public void The_module_segment_is_found_regardless_of_case()
        {
            SearchIndex.IndexEntry entry = Entry("Root Module/WWPBaseObjects/WWPTemplates", "wwpbaseobjects");

            Assert.Equal("WWPBaseObjects", SdkTextTreeService.BuildModuleDirectory(entry, "wwpbaseobjects"));
        }

        [Fact]
        public void A_path_that_does_not_show_the_module_keeps_its_whole_folder_path()
        {
            SearchIndex.IndexEntry entry = Entry("Root Module/Folder/Sub", "Sales");

            Assert.Equal(Path.Combine("Folder", "Sub"), SdkTextTreeService.BuildModuleDirectory(entry, "Sales"));
        }

        [Fact]
        public void Export_writes_one_module_toml_at_the_module_root_for_an_object_in_a_folder()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/WWPBaseObjects/WWPTemplates/Selection", "WWPBaseObjects"));

                string expected = Path.Combine(root, "src", "WWPBaseObjects", "module.toml");
                Assert.Equal(expected, Assert.Single(Directory.GetFiles(root, "module.toml", SearchOption.AllDirectories)));
                Assert.True(File.Exists(expected));
                Assert.True(File.Exists(Path.Combine(root, "src", "WWPBaseObjects", "WWPTemplates", "Selection", "SynthTemplate.gx")));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_full_export_removes_the_module_toml_that_an_earlier_export_listed()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/SynthOld/Folder", "SynthOld"));
                string stale = Path.Combine(root, "src", "SynthOld", "module.toml");
                Assert.True(File.Exists(stale));

                Export(root, Entry("Root Module/SynthNew/Folder", "SynthNew"), fullSelection: true);

                Assert.False(File.Exists(stale));
                Assert.True(File.Exists(Path.Combine(root, "src", "SynthNew", "module.toml")));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_partial_export_keeps_the_module_toml_that_an_earlier_export_listed()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/SynthOld/Folder", "SynthOld"));
                string kept = Path.Combine(root, "src", "SynthOld", "module.toml");

                Export(root, Entry("Root Module/SynthNew/Folder", "SynthNew"));

                Assert.True(File.Exists(kept));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void Export_writes_module_toml_at_the_module_root_for_an_object_at_the_module_root()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/WWPBaseObjects", "WWPBaseObjects"));

                string expected = Path.Combine(root, "src", "WWPBaseObjects", "module.toml");
                Assert.Equal(expected, Assert.Single(Directory.GetFiles(root, "module.toml", SearchOption.AllDirectories)));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void Export_writes_no_module_toml_for_an_object_without_a_module()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/WWPBaseObjects/WWPTemplates/Selection", module: null));

                Assert.Empty(Directory.GetFiles(root, "module.toml", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_tree_with_module_toml_inside_folders_from_an_earlier_export_still_validates()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/WWPBaseObjects/WWPTemplates/Selection", "WWPBaseObjects"));
                string legacy = Path.Combine(root, "src", "WWPBaseObjects", "WWPTemplates", "Selection", "module.toml");
                Directory.CreateDirectory(Path.GetDirectoryName(legacy));
                File.WriteAllText(legacy, "ModuleIdentifier = \"\"" + Environment.NewLine + "Name = \"WWPBaseObjects\"" + Environment.NewLine + "IsDefault = \"False\"");

                JObject response = JObject.Parse(ObjectTextService.ValidateInMemory(
                    new JObject { ["inputPath"] = root }, CancellationToken.None));

                // A tree written before the change is valid: the validation completes with every file valid.
                Assert.Equal("ObjectTextMemoryValidationCompleted", (string)response["code"]);
                Assert.True((bool?)response["result"]?["valid"] == true, response.ToString());
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_folder_above_the_module_with_the_module_name_does_not_move_the_module_root()
        {
            string root = NewTempDirectory();
            try
            {
                // The module sits under a folder of the same name. Its own index entry gives the root, not the first segment.
                Export(root, Entry("Root Module/SynthCore/SynthCore/Folder", "SynthCore"), modulePath: "SynthCore/SynthCore");

                string expected = Path.Combine(root, "src", "SynthCore", "SynthCore", "module.toml");
                Assert.Equal(expected, Assert.Single(Directory.GetFiles(root, "module.toml", SearchOption.AllDirectories)));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void The_module_toml_names_the_module_by_the_GUID_of_its_index_entry()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/SynthCore/Folder", "SynthCore"));

                string text = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "module.toml", SearchOption.AllDirectories)));
                Assert.Contains("ModuleIdentifier = \"5d2c7a10-7d3e-4b1f-9a55-0c4e8b3f2a11\"", text);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_module_the_index_does_not_hold_gets_no_module_toml_with_an_empty_identifier()
        {
            string root = NewTempDirectory();
            try
            {
                Export(root, Entry("Root Module/SynthCore/Folder", "SynthCore"), indexesModule: false);

                Assert.Empty(Directory.GetFiles(root, "module.toml", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static SearchIndex.IndexEntry Entry(string parentFolderPath, string module)
        {
            return new SearchIndex.IndexEntry
            {
                Name = "SynthTemplate",
                Type = "WorkWithPlus for Web Template",
                Module = module,
                Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11",
                EntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001",
                ParentPath = parentFolderPath.Replace("Root Module/", string.Empty),
                ParentFolderPath = parentFolderPath,
                Path = parentFolderPath.Replace("Root Module/", string.Empty) + "/SynthTemplate"
            };
        }

        // Exports one object through the production export; only the SDK read is replaced. The index holds the object's
        // module as an entry, with the path it is given, unless the test says the index does not hold it.
        private static void Export(
            string root,
            SearchIndex.IndexEntry entry,
            bool fullSelection = false,
            string modulePath = null,
            bool indexesModule = true)
        {
            var cache = new IndexCacheService();
            var loaded = new List<SearchIndex.IndexEntry> { entry };
            if (indexesModule && !string.IsNullOrWhiteSpace(entry.Module))
            {
                loaded.Add(new SearchIndex.IndexEntry
                {
                    Type = "Module",
                    Name = entry.Module,
                    Module = entry.Module,
                    Path = modulePath ?? entry.Module,
                    Guid = "5d2c7a10-7d3e-4b1f-9a55-0c4e8b3f2a11",
                    EntityKey = "5d2c7a10-7d3e-4b1f-9a55-0c4e8b3f2a11-module"
                });
            }
            cache.LoadFromEntries(loaded);
            cache.MarkIndexComplete(loaded.Count);
            var service = new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) => new JObject { ["source"] = "Template SynthTemplate" }.ToString());
            var args = new JObject
            {
                ["outputPath"] = root,
                ["mode"] = "all"
            };
            if (!fullSelection) args["targets"] = new JArray(entry.Name);

            JObject response = JObject.Parse(service.Execute("export_kb_to_text", null, args, CancellationToken.None));

            Assert.Equal(0, (int)response["result"]!["failed"]!);
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-modules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTree(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
