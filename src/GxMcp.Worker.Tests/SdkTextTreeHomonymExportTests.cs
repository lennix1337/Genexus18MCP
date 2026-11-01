using System;
using System.IO;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Two objects of the same type and name in different modules are two objects. The index keeps one entry per
    // Tipo:Nome, so the second homonym reaches the native export only as an explicit identity the index does not hold:
    // the SDK answers it. The export must then write both; selection and plan must not keep one of them.
    // The SDK lookup and the part read are replaced at their boundaries; selection and export are production code.
    public sealed class SdkTextTreeHomonymExportTests
    {
        private const string GuidInA = "40000000-0000-4000-8000-00000000000a";
        private const string GuidInB = "40000000-0000-4000-8000-00000000000b";

        [Fact]
        public void Two_homonyms_one_indexed_and_one_found_by_the_SDK_are_both_exported()
        {
            string root = NewTempDirectory();
            try
            {
                SearchIndex.IndexEntry entryA = Procedure("SynthA", GuidInA);
                SearchIndex.IndexEntry entryB = Procedure("SynthB", GuidInB);
                var cache = new IndexCacheService();
                cache.LoadFromEntries(new[] { entryA });
                cache.MarkIndexComplete(1);
                var service = new SdkTextTreeService(
                    new TextTreeSelectionService(cache),
                    new ObjectService(null, null),
                    cache,
                    ReadPart,
                    explicitTarget => explicitTarget.Name == GuidInB ? entryB : null);

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", GuidInA + ";" + GuidInB,
                    new JObject { ["outputPath"] = root, ["mode"] = "all" }, CancellationToken.None));

                Assert.Equal(2, (int)response["result"]!["exported"]!);
                Assert.Equal(2, Directory.GetFiles(root, "SynthDup.gx", SearchOption.AllDirectories).Length);
            }
            finally
            {
                DeleteTree(root);
            }
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
