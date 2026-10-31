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
    // The native envelope for a DesignSystem: the export writes the SDK's source inside
    // `DesignSystem <Name> { ... }`, and the native reader takes that envelope back to the same source.
    // This is not the official GeneXus text (#DesignStyles / #Properties blocks), and the tests pin it.
    public sealed class SdkTextTreeDesignSystemFormatTests
    {
        private const string Source = "styles SynthDS\n{\n\t@import SynthBaseDS;\n}";

        [Fact]
        public void A_native_DesignSystem_document_reads_back_to_its_source()
        {
            string document = SdkTextTreeService.BuildObjectDocument("DesignSystem", "SynthDS", Source);

            Assert.True(SdkTextTreeService.TryParseObjectDocument(document, out string type, out string name, out string source, out string error), error);
            Assert.Equal("DesignSystem", type);
            Assert.Equal("SynthDS", name);
            Assert.Equal(Source, source);
        }

        [Fact]
        public void A_native_DesignSystem_document_reads_back_as_its_Source_part()
        {
            string document = SdkTextTreeService.BuildObjectDocument("DesignSystem", "SynthDS", Source);

            Assert.True(SdkTextTreeService.TryParseObjectDocumentParts(document, out string type, out string name,
                out Dictionary<string, string> parts, out string error), error);
            Assert.Equal("DesignSystem", type);
            Assert.Equal("SynthDS", name);
            Assert.Equal(Source, parts["Source"]);
        }

        [Fact]
        public void The_native_export_writes_a_DesignSystem_in_the_native_envelope_not_the_official_blocks()
        {
            string root = NewTempDirectory();
            try
            {
                var entry = new SearchIndex.IndexEntry
                {
                    Name = "SynthDS",
                    Type = "DesignSystem",
                    Module = "Root Module",
                    Guid = "3c9a7f10-2b4e-4d8a-9c61-5e7b2f0d8a44",
                    EntityKey = "9f0e1d2c-3b4a-4958-8a7b-6c5d4e3f2a10-41",
                    ParentPath = "Root Module",
                    ParentFolderPath = "Root Module",
                    Path = "SynthDS"
                };
                var cache = new IndexCacheService();
                cache.LoadFromEntries(new[] { entry });
                cache.MarkIndexComplete(1);
                var service = new SdkTextTreeService(
                    new TextTreeSelectionService(cache),
                    new ObjectService(null, null),
                    cache,
                    (target, part, type, guid, entityKey, path) => new JObject { ["source"] = Source }.ToString());
                var args = new JObject
                {
                    ["outputPath"] = root,
                    ["mode"] = "all",
                    ["targets"] = new JArray("SynthDS")
                };

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", null, args, CancellationToken.None));

                Assert.Equal(0, (int)response["result"]!["failed"]!);
                string content = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "SynthDS.gx", SearchOption.AllDirectories)));
                Assert.StartsWith("DesignSystem SynthDS\n{\n", content);
                Assert.Contains("@import SynthBaseDS;", content);
                Assert.DoesNotContain("#DesignStyles", content);
                Assert.DoesNotContain("#Properties", content);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-designsystem-" + Guid.NewGuid().ToString("N"));
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
