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
    // Two objects of the same type and name in different modules are two objects, and both can be named by identity:
    // one is indexed and the other is found by the SDK. The native tree keeps one object per Type:Name, so only the first
    // object of the plan is written; the other answers HomonymInSameTree and the reply is partial. The tree the export
    // leaves is one the validator accepts. The SDK lookup and the part read are replaced at their boundaries.
    public sealed class SdkTextTreeHomonymExportTests
    {
        private const string GuidInA = "40000000-0000-4000-8000-00000000000a";
        private const string GuidInB = "40000000-0000-4000-8000-00000000000b";

        [Fact]
        public void The_first_homonym_is_written_and_the_second_answers_HomonymInSameTree()
        {
            string root = NewTempDirectory();
            try
            {
                SdkTextTreeService service = BuildService();

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", GuidInA + ";" + GuidInB,
                    new JObject { ["outputPath"] = root, ["mode"] = "all" }, CancellationToken.None));

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                JObject second = response["result"]!["objects"]!.Cast<JObject>().Single(item => (string)item["status"]! == "error");
                Assert.Equal("HomonymInSameTree", (string)second["code"]!);
                Assert.Contains("SynthB", (string)second["file"]!);
                Assert.Contains("Type:Name", (string)second["hint"]!);

                string[] written = Directory.GetFiles(root, "SynthDup.gx", SearchOption.AllDirectories);
                Assert.Single(written);
                Assert.Contains("SynthA", written[0]);

                JObject validation = JObject.Parse(TextTreeFileService.ValidateInMemory(
                    new JObject { ["inputPath"] = root }, CancellationToken.None));
                Assert.True((bool?)validation["result"]?["valid"] == true, validation.ToString());
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_later_full_export_keeps_one_file_and_one_manifest_entry_for_the_pair()
        {
            string root = NewTempDirectory();
            try
            {
                SdkTextTreeService service = BuildService();
                JObject first = JObject.Parse(service.Execute("export_kb_to_text", GuidInA + ";" + GuidInB,
                    new JObject { ["outputPath"] = root, ["mode"] = "all" }, CancellationToken.None));
                Assert.Equal(1, (int)first["result"]!["exported"]!);
                Assert.Equal(1, ManifestEntries(root, "SynthDup"));

                JObject later = JObject.Parse(service.Execute("export_kb_to_text", null,
                    new JObject { ["outputPath"] = root, ["mode"] = "all", ["overwrite"] = true }, CancellationToken.None));

                Assert.Equal("ok", (string)later["status"]);
                Assert.Equal(0, (int)later["result"]!["filesDeleted"]!);
                Assert.Equal(1, ManifestEntries(root, "SynthDup"));
                Assert.Single(Directory.GetFiles(root, "SynthDup.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // The index holds SynthA.SynthDup; the SDK answers the GUID of SynthB.SynthDup, the homonym the index does not keep.
        private static SdkTextTreeService BuildService()
        {
            SearchIndex.IndexEntry entryB = Procedure("SynthB", GuidInB);
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[] { Procedure("SynthA", GuidInA) });
            cache.MarkIndexComplete(1);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                ReadPart,
                explicitTarget => explicitTarget.Name == GuidInB ? entryB : null);
        }

        // The part read names the object it read, as the production read does, so the identity check passes for both.
        private static string ReadPart(string target, string part, string type, string guid, string entityKey, string path)
        {
            return new JObject
            {
                ["source"] = "Synth text",
                ["objectName"] = "SynthDup",
                ["objectType"] = "Procedure"
            }.ToString();
        }

        private static SearchIndex.IndexEntry Procedure(string module, string guid)
        {
            return new SearchIndex.IndexEntry
            {
                Type = "Procedure",
                Name = "SynthDup",
                Module = module,
                Parent = "Folder",
                ParentPath = module + "/Folder",
                ParentFolderPath = "Root Module/" + module + "/Folder",
                Path = module + "/Folder/SynthDup",
                Guid = guid,
                EntityKey = guid + "-9001"
            };
        }

        private static int ManifestEntries(string root, string name)
        {
            string manifest = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Single(path => !path.EndsWith(".gx", StringComparison.OrdinalIgnoreCase) && File.ReadAllText(path).Contains("GeneXusSdkTextTree"));
            return JObject.Parse(File.ReadAllText(manifest))["objects"]!.Count(item => (string)item["name"] == name);
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-homonyms-" + Guid.NewGuid().ToString("N"));
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
