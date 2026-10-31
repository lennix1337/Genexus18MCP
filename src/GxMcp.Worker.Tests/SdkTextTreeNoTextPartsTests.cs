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
    // A native export of an object type with no text parts (WorkWithPlus templates keep their data in SDK
    // properties) answers its own code, not a not-found reply. Only the SDK read is replaced, at its boundary.
    public sealed class SdkTextTreeNoTextPartsTests
    {
        [Fact]
        public void A_part_read_is_a_no_text_parts_reply_only_when_the_object_lists_no_part()
        {
            Assert.True(SdkTextTreeService.IsNoTextPartsReply(Reply("PartNotFound")));
            Assert.True(SdkTextTreeService.IsNoTextPartsReply(Reply("PartNotFound", new JArray())));
            Assert.False(SdkTextTreeService.IsNoTextPartsReply(Reply("PartNotFound", new JArray("Rules"))));
            Assert.False(SdkTextTreeService.IsNoTextPartsReply(Reply("ObjectNotFound")));
            Assert.False(SdkTextTreeService.IsNoTextPartsReply(null));
        }

        [Fact]
        public void An_object_without_text_parts_is_reported_with_its_own_code()
        {
            SdkTextTreeService service = BuildService(Reply("PartNotFound"));
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                string message = (string)response["result"]!["objects"]![0]!["message"]!;
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Contains("ObjectHasNoTextParts", message);
                Assert.DoesNotContain("ObjectNotFound", message);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_part_missing_from_an_object_that_has_other_parts_keeps_its_own_reply()
        {
            SdkTextTreeService service = BuildService(Reply("PartNotFound", new JArray("Rules")));
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                string message = (string)response["result"]!["objects"]![0]!["message"]!;
                Assert.Contains("Part 'Source' not found in SynthTemplate", message);
                Assert.DoesNotContain("ObjectHasNoTextParts", message);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static JObject Reply(string code, JArray availableParts = null)
        {
            var reply = new JObject
            {
                ["code"] = code,
                ["error"] = "Part 'Source' not found in SynthTemplate",
                ["objectName"] = "SynthTemplate",
                ["objectType"] = "WorkWithPlus for Web Template"
            };
            if (availableParts != null) reply["availableParts"] = availableParts;
            return reply;
        }

        private static SdkTextTreeService BuildService(JObject reply)
        {
            var entry = new SearchIndex.IndexEntry
            {
                Name = "SynthTemplate",
                Type = "WorkWithPlus for Web Template",
                Module = "WWPBaseObjects",
                Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11",
                EntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001",
                ParentPath = "WWPBaseObjects/WWPTemplates/Selection",
                ParentFolderPath = "Root Module/WWPBaseObjects/WWPTemplates/Selection",
                Path = "WWPBaseObjects/WWPTemplates/Selection/SynthTemplate"
            };
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[] { entry });
            cache.MarkIndexComplete(1);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) => reply.ToString());
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
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-notextparts-" + Guid.NewGuid().ToString("N"));
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
