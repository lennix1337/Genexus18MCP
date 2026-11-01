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
    // An explicit target the index does not hold is answered by the SDK. The entry the SDK returns must pass the same
    // filters and limit as an indexed entry, so a request that excludes it does not export it. The SDK lookup and the
    // part read are replaced at their boundaries; selection and export are production code.
    public sealed class SdkTextTreeLookupFilterTests
    {
        [Fact]
        public void An_entry_the_SDK_finds_outside_the_index_is_refused_by_the_type_filter()
        {
            JObject response = ExportWith(
                new JObject { ["type"] = "Procedure" },
                "SynthOutside",
                out string root,
                found: SynthEntry("SynthOutside", "Transaction", "SynthCore"));
            try
            {
                Assert.Equal("NoObjectsMatched", (string)response["error"]?["code"]);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void An_entry_the_SDK_finds_outside_the_index_is_refused_by_the_ignore_filter()
        {
            JObject response = ExportWith(
                new JObject { ["ignore"] = new JArray("SynthOutside") },
                "SynthOutside",
                out string root,
                found: SynthEntry("SynthOutside", "Procedure", "SynthCore"));
            try
            {
                Assert.Equal("NoObjectsMatched", (string)response["error"]?["code"]);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void An_entry_the_SDK_finds_outside_the_index_is_refused_by_the_module_filter()
        {
            JObject response = ExportWith(
                new JObject { ["module"] = "SynthOther" },
                "SynthOutside",
                out string root,
                found: SynthEntry("SynthOutside", "Procedure", "SynthCore"));
            try
            {
                Assert.Equal("NoObjectsMatched", (string)response["error"]?["code"]);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void An_entry_the_SDK_finds_outside_the_index_is_exported_when_the_filters_accept_it()
        {
            JObject response = ExportWith(
                new JObject { ["type"] = "Procedure", ["module"] = "SynthCore", ["ignore"] = new JArray("SynthOther") },
                "SynthOutside",
                out string root,
                found: SynthEntry("SynthOutside", "Procedure", "SynthCore"));
            try
            {
                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Single(Directory.GetFiles(root, "SynthOutside.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void The_limit_applies_to_the_indexed_and_the_found_entries_together()
        {
            // SynthAlpha sorts before the indexed SynthZulu, so with limit=1 the found entry is the one exported.
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[] { SynthEntry("SynthZulu", "Procedure", "SynthCore") });
            cache.MarkIndexComplete(1);
            var service = new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                ReadPart,
                explicitTarget => explicitTarget.Name == "SynthAlpha"
                    ? SynthEntry("SynthAlpha", "Procedure", "SynthCore")
                    : null);
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, new JObject { ["limit"] = 1 }, "SynthZulu", "SynthAlpha");

                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Single(Directory.GetFiles(root, "SynthAlpha.gx", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(root, "SynthZulu.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static JObject ExportWith(JObject options, string target, out string root, SearchIndex.IndexEntry found)
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[] { SynthEntry("SynthIndexed", "Procedure", "SynthCore") });
            cache.MarkIndexComplete(1);
            var service = new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                ReadPart,
                explicitTarget => explicitTarget.Name == target ? found : null);
            root = NewTempDirectory();
            return Export(service, root, options, target);
        }

        // The part read names the object it read, as the production read does, so the identity check passes.
        private static string ReadPart(string target, string part, string type, string guid, string entityKey, string path)
        {
            return new JObject
            {
                ["source"] = "Synth text",
                ["objectName"] = NameOf(target),
                ["objectType"] = type
            }.ToString();
        }

        // The read is asked for Tipo:Nome, so the object's name is what follows its type.
        private static string NameOf(string typedName)
        {
            return typedName.Substring(typedName.IndexOf(':') + 1);
        }

        private static SearchIndex.IndexEntry SynthEntry(string name, string type, string module)
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = type,
                Module = module,
                Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11",
                EntityKey = "9f0e0000-0000-4000-8000-000000000041-9001",
                ParentPath = module,
                ParentFolderPath = "Root Module/" + module,
                Path = module + "/" + name
            };
        }

        private static JObject Export(SdkTextTreeService service, string root, JObject options, params string[] targets)
        {
            var args = new JObject
            {
                ["outputPath"] = root,
                ["mode"] = "all",
                ["targets"] = new JArray(targets)
            };
            foreach (JProperty option in options.Properties()) args[option.Name] = option.Value;
            return JObject.Parse(service.Execute("export_kb_to_text", null, args, CancellationToken.None));
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-lookup-filter-" + Guid.NewGuid().ToString("N"));
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
