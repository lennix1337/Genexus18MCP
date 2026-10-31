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
    // The export reads each object by the GUID and EntityKey of its index entry. When the index is out of date, that
    // identity resolves another object, or none, and the export must say so instead of writing the entry's name over
    // the object it found. The read is replaced at its boundary; selection, planning, writing and the reply are production code.
    public sealed class SdkTextTreeStaleIndexTests
    {
        [Fact]
        public void A_read_that_resolves_a_renamed_object_is_a_stale_index_and_writes_nothing()
        {
            SdkTextTreeService service = BuildService(
                new[] { SynthEntry("SynthAntigo", "Procedure") },
                (target, part, type, guid, entityKey, path) => new JObject
                {
                    ["source"] = "Synth text of the renamed object",
                    ["objectName"] = "SynthNovo",
                    ["objectType"] = "Procedure"
                }.ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthAntigo");

                Assert.Equal("partial", (string)response["status"]);
                JObject reply = ObjectReply(response, "SynthAntigo");
                Assert.Equal("error", (string)reply["status"]);
                Assert.Equal("IndexEntryStale", (string)reply["code"]!);
                Assert.Contains("SynthNovo", (string)reply["message"]!);
                Assert.Contains("genexus_lifecycle action=index", (string)reply["hint"]!);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_read_that_resolves_an_object_of_another_type_is_a_stale_index_and_writes_nothing()
        {
            SdkTextTreeService service = BuildService(
                new[] { SynthEntry("SynthAntigo", "Procedure") },
                (target, part, type, guid, entityKey, path) => new JObject
                {
                    ["source"] = "Synth text of the other object",
                    ["objectName"] = "SynthAntigo",
                    ["objectType"] = "Transaction"
                }.ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthAntigo");

                JObject reply = ObjectReply(response, "SynthAntigo");
                Assert.Equal("IndexEntryStale", (string)reply["code"]!);
                Assert.Contains("Transaction", (string)reply["message"]!);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_read_that_finds_no_object_is_a_stale_index_with_the_index_hint()
        {
            SdkTextTreeService service = BuildService(
                new[] { SynthEntry("SynthAntigo", "Procedure") },
                (target, part, type, guid, entityKey, path) => McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found: " + target,
                    target: target));
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthAntigo");

                JObject reply = ObjectReply(response, "SynthAntigo");
                Assert.Equal("IndexEntryStale", (string)reply["code"]!);
                Assert.Contains("genexus_lifecycle action=index", (string)reply["hint"]!);
                Assert.DoesNotContain("similar names", (string)reply["message"]!);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_read_that_names_the_index_object_is_written_under_its_index_name()
        {
            SdkTextTreeService service = BuildService(
                new[] { SynthEntry("SynthAntigo", "Procedure") },
                (target, part, type, guid, entityKey, path) => new JObject
                {
                    ["source"] = "Synth text",
                    ["objectName"] = "SynthAntigo",
                    ["objectType"] = "Procedure"
                }.ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthAntigo");

                JObject reply = ObjectReply(response, "SynthAntigo");
                Assert.Equal("ok", (string)reply["status"]);
                Assert.Null(reply["code"]);
                Assert.Single(Directory.GetFiles(root, "SynthAntigo.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static JObject ObjectReply(JObject response, string name)
        {
            return response["result"]!["objects"]!.Cast<JObject>().Single(item => (string)item["name"]! == name);
        }

        private static SearchIndex.IndexEntry SynthEntry(string name, string type)
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = type,
                Module = "SynthCore",
                Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11",
                EntityKey = "9f0e0000-0000-4000-8000-000000000041-9001",
                ParentPath = "SynthCore",
                ParentFolderPath = "Root Module/SynthCore",
                Path = "SynthCore/" + name
            };
        }

        private static SdkTextTreeService BuildService(
            IEnumerable<SearchIndex.IndexEntry> entries,
            Func<string, string, string, string, string, string, string> readPartText)
        {
            var cache = new IndexCacheService();
            List<SearchIndex.IndexEntry> loaded = entries.ToList();
            cache.LoadFromEntries(loaded);
            cache.MarkIndexComplete(loaded.Count);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                readPartText);
        }

        private static JObject Export(SdkTextTreeService service, string root, params string[] targets)
        {
            var args = new JObject
            {
                ["outputPath"] = root,
                ["mode"] = "all",
                ["targets"] = new JArray(targets)
            };
            return JObject.Parse(service.Execute("export_kb_to_text", null, args, CancellationToken.None));
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-stale-index-" + Guid.NewGuid().ToString("N"));
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
