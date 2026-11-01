using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // An earlier export lists a module.toml. A later export removes it only when it covered the whole KB and finished:
    // a limit, an ignore list, a skip, a failed object or a stop leaves that module unexported, so its file stays.
    // The SDK read is replaced at its boundary; the removal is production code.
    public sealed class SdkTextTreeStaleRemovalTests
    {
        [Fact]
        public void A_full_export_with_a_failed_object_keeps_the_module_toml_of_an_earlier_export()
        {
            string root = NewTempDirectory();
            try
            {
                string kept = WriteEarlierModule(root);

                JObject response = ExportFull(root, new JObject(), new[] { Entry("SynthNew", "SynthNew") }, failsFor: "SynthNew");

                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.True(File.Exists(kept));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_full_export_stopped_by_an_error_keeps_the_module_toml_of_an_earlier_export()
        {
            string root = NewTempDirectory();
            try
            {
                string kept = WriteEarlierModule(root);

                ExportFull(root, new JObject { ["stopOnError"] = true }, new[] { Entry("SynthNew", "SynthNew") }, failsFor: "SynthNew");

                Assert.True(File.Exists(kept));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_full_export_with_a_limit_keeps_the_module_toml_of_an_earlier_export()
        {
            string root = NewTempDirectory();
            try
            {
                string kept = WriteEarlierModule(root);

                JObject response = ExportFull(root, new JObject { ["limit"] = 1 },
                    new[] { Entry("SynthNew", "SynthNew"), Entry("SynthNewer", "SynthNew") }, failsFor: null);

                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.True(File.Exists(kept));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_full_export_with_an_ignore_list_keeps_the_module_toml_of_an_earlier_export()
        {
            string root = NewTempDirectory();
            try
            {
                string kept = WriteEarlierModule(root);

                ExportFull(root, new JObject { ["ignore"] = new JArray("SynthNewer") },
                    new[] { Entry("SynthNew", "SynthNew"), Entry("SynthNewer", "SynthNew") }, failsFor: null);

                Assert.True(File.Exists(kept));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_full_export_with_a_skip_keeps_the_module_toml_of_an_earlier_export()
        {
            string root = NewTempDirectory();
            try
            {
                string kept = WriteEarlierModule(root);

                ExportFull(root, new JObject { ["skip"] = 1 }, new[] { Entry("SynthNew", "SynthNew") }, failsFor: null);

                Assert.True(File.Exists(kept));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_complete_full_export_still_removes_the_module_toml_of_an_earlier_export()
        {
            string root = NewTempDirectory();
            try
            {
                string stale = WriteEarlierModule(root);

                JObject response = ExportFull(root, new JObject(), new[] { Entry("SynthNew", "SynthNew") }, failsFor: null);

                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.False(File.Exists(stale));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // An earlier export of SynthOld. Its module.toml is the file the later exports must not remove by mistake.
        private static string WriteEarlierModule(string root)
        {
            ExportFull(root, new JObject(), new[] { Entry("SynthOld", "SynthOld") }, failsFor: null, targetsOnly: true);
            string file = Path.Combine(root, "src", "SynthOld", "module.toml");
            Assert.True(File.Exists(file));
            return file;
        }

        private static JObject ExportFull(
            string root,
            JObject options,
            IEnumerable<SearchIndex.IndexEntry> entries,
            string failsFor,
            bool targetsOnly = false)
        {
            List<SearchIndex.IndexEntry> loaded = entries.ToList();
            var cache = new IndexCacheService();
            cache.LoadFromEntries(loaded.Concat(loaded.Select(entry => Module(entry.Module))).ToArray());
            cache.MarkIndexComplete(loaded.Count * 2);
            var service = new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) => NameOf(target) == failsFor
                    ? McpResponse.Err(code: "ReadPartFailed", message: "Synth read failure", target: target)
                    : new JObject { ["source"] = "Synth text", ["objectName"] = NameOf(target), ["objectType"] = type }.ToString());
            var args = new JObject
            {
                ["outputPath"] = root,
                ["mode"] = "all"
            };
            foreach (JProperty option in options.Properties()) args[option.Name] = option.Value;
            if (targetsOnly) args["targets"] = new JArray(loaded.Select(entry => entry.Name));
            return JObject.Parse(service.Execute("export_kb_to_text", null, args, CancellationToken.None));
        }

        // The read is asked for Type:Name, so the object's name is what follows its type.
        private static string NameOf(string typedName)
        {
            return typedName.Substring(typedName.IndexOf(':') + 1);
        }

        private static SearchIndex.IndexEntry Entry(string name, string module)
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = "Procedure",
                Module = module,
                Guid = Guid.NewGuid().ToString(),
                EntityKey = Guid.NewGuid().ToString() + "-9001",
                ParentPath = module + "/Folder",
                ParentFolderPath = "Root Module/" + module + "/Folder",
                Path = module + "/Folder/" + name
            };
        }

        private static SearchIndex.IndexEntry Module(string name)
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = "Module",
                Module = name,
                Guid = Guid.NewGuid().ToString(),
                EntityKey = Guid.NewGuid().ToString() + "-module",
                Path = name
            };
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-stale-removal-" + Guid.NewGuid().ToString("N"));
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
