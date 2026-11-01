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
    // A native text export reads each target with the identity its index entry holds (GUID, EntityKey,
    // path), as genexus_read does, and an EntityKey or GUID target selects its entry. Only the SDK read
    // is replaced, at SdkTextTreeService's read boundary; selection, planning and writing run as shipped.
    public sealed class SdkTextTreeExportIdentityTests
    {
        private const string TemplateType = "WorkWithPlus for Web Template";
        private const string TemplateGuid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11";
        private const string TemplateEntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001";

        [Fact]
        public void Each_read_carries_the_index_identity_of_its_object()
        {
            SearchIndex.IndexEntry entry = SynthTemplate();
            var reads = new List<string[]>();
            SdkTextTreeService service = BuildService(entry, (target, part, type, guid, entityKey, path) =>
            {
                reads.Add(new[] { target, part, type, guid, entityKey, path });
                return new JObject { ["source"] = "Template SynthTemplate" }.ToString();
            });
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.Equal(new[] { TemplateType + ":SynthTemplate", "Source", TemplateType, TemplateGuid, TemplateEntityKey, entry.Path },
                    Assert.Single(reads));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void An_EntityKey_target_selects_its_template()
        {
            SdkTextTreeService service = BuildService(SynthTemplate(), (target, part, type, guid, entityKey, path) =>
                new JObject { ["source"] = "Template SynthTemplate" }.ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, TemplateEntityKey);

                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_failed_read_is_attempted_once_under_the_object_name()
        {
            var reads = new List<string>();
            SdkTextTreeService service = BuildService(SynthTemplate(), (target, part, type, guid, entityKey, path) =>
            {
                reads.Add(target);
                return new JObject { ["status"] = "error", ["error"] = "Object not found: SynthTemplate" }.ToString();
            });
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Equal(new[] { TemplateType + ":SynthTemplate" }, reads.ToArray());
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static SearchIndex.IndexEntry SynthTemplate()
        {
            return new SearchIndex.IndexEntry
            {
                Name = "SynthTemplate",
                Type = TemplateType,
                Module = "WWPBaseObjects",
                Guid = TemplateGuid,
                EntityKey = TemplateEntityKey,
                ParentPath = "WWPBaseObjects/WWPTemplates/Selection",
                ParentFolderPath = "Root Module/WWPBaseObjects/WWPTemplates/Selection",
                Path = "WWPBaseObjects/WWPTemplates/Selection/SynthTemplate"
            };
        }

        private static SdkTextTreeService BuildService(
            SearchIndex.IndexEntry entry,
            Func<string, string, string, string, string, string, string> readPartText)
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[] { entry });
            cache.MarkIndexComplete(1);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                readPartText);
        }

        private static JObject Export(SdkTextTreeService service, string root, string target)
        {
            var args = new JObject
            {
                ["outputPath"] = root,
                ["mode"] = "all",
                ["targets"] = new JArray(target)
            };
            return JObject.Parse(service.Execute("export_kb_to_text", null, args, CancellationToken.None));
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-export-" + Guid.NewGuid().ToString("N"));
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
