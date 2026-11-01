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
    // A native export of an object type with no text parts (WorkWithPlus templates keep their data in SDK
    // properties) answers its own code, not a missing-object or index reply. Only the SDK read of parts is
    // replaced, at its boundary; selection, planning, writing and the reply shape are production code.
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
        public void An_object_without_text_parts_is_reported_with_its_own_code_and_the_property_commands()
        {
            SdkTextTreeService service = BuildService(new[] { SynthEntry("SynthTemplate") }, (target, part, type, guid, entityKey, path) =>
                Reply("PartNotFound").ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                JObject reply = ObjectReply(response, "SynthTemplate");
                Assert.Equal("error", (string)reply["status"]);
                Assert.Equal("ObjectHasNoTextParts", (string)reply["code"]!);
                Assert.Contains("action=get", (string)reply["hint"]!);
                Assert.Contains("action=set", (string)reply["hint"]!);
                string message = (string)reply["message"]!;
                Assert.Contains("SynthTemplate", message);
                Assert.DoesNotContain("{", message);
                Assert.DoesNotContain("ObjectNotFound", message);
                Assert.DoesNotContain("IndexedObjectUnavailable", message);
                Assert.DoesNotContain("ObjectNotFoundIndexWarming", message);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_part_missing_from_an_object_that_has_other_parts_keeps_its_own_reply()
        {
            SdkTextTreeService service = BuildService(new[] { SynthEntry("SynthTemplate") }, (target, part, type, guid, entityKey, path) =>
                Reply("PartNotFound", new JArray("Rules")).ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                string message = (string)ObjectReply(response, "SynthTemplate")["message"]!;
                Assert.Contains("Part 'Source' not found in SynthTemplate", message);
                Assert.DoesNotContain("ObjectHasNoTextParts", message);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void In_an_export_with_other_targets_the_object_without_text_parts_is_named_and_the_others_still_export()
        {
            // The object without parts sorts first, so the object after it is only exported if the export goes on.
            SdkTextTreeService service = BuildService(
                new[] { SynthEntry("SynthAlpha"), SynthEntry("SynthZulu") },
                (target, part, type, guid, entityKey, path) => target.EndsWith(":SynthAlpha")
                    ? Reply("PartNotFound", new JArray(), "SynthAlpha").ToString()
                    : new JObject { ["source"] = "Synth text", ["objectName"] = "SynthZulu", ["objectType"] = "WorkWithPlus for Web Template" }.ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthAlpha", "SynthZulu");

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Equal("ObjectHasNoTextParts", (string)ObjectReply(response, "SynthAlpha")["code"]!);
                Assert.Equal("ok", (string)ObjectReply(response, "SynthZulu")["status"]!);
                Assert.Single(Directory.GetFiles(root, "SynthZulu.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_multi_part_export_of_an_object_without_text_parts_gives_the_same_code_and_hint()
        {
            // part=all reads the parts together. An object that lists no part answers as the single-part read does.
            var noParts = new JObject
            {
                ["name"] = "SynthTemplate",
                ["type"] = "WorkWithPlus for Web Template",
                ["parts"] = new JObject(),
                ["availableParts"] = new JArray()
            };
            SdkTextTreeService service = BuildService(
                new[] { SynthEntry("SynthTemplate") },
                (target, part, type, guid, entityKey, path) => throw new InvalidOperationException("part=all reads every part at once"),
                (target, parts, type, guid, entityKey, path) => noParts.ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, new JObject { ["part"] = "all" }, "SynthTemplate");

                Assert.Equal("partial", (string)response["status"]);
                JObject reply = ObjectReply(response, "SynthTemplate");
                Assert.Equal("ObjectHasNoTextParts", (string)reply["code"]!);
                Assert.Contains("action=get", (string)reply["hint"]!);
                Assert.Contains("action=set", (string)reply["hint"]!);
                Assert.DoesNotContain("{", (string)reply["message"]!);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
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

        private static JObject Reply(string code, JArray availableParts = null, string objectName = "SynthTemplate")
        {
            var reply = new JObject
            {
                ["code"] = code,
                ["error"] = "Part 'Source' not found in " + objectName,
                ["objectName"] = objectName,
                ["objectType"] = "WorkWithPlus for Web Template"
            };
            if (availableParts != null) reply["availableParts"] = availableParts;
            return reply;
        }

        private static SearchIndex.IndexEntry SynthEntry(string name)
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = "WorkWithPlus for Web Template",
                Module = "WWPBaseObjects",
                Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11",
                EntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001",
                ParentPath = "WWPBaseObjects/WWPTemplates/Selection",
                ParentFolderPath = "Root Module/WWPBaseObjects/WWPTemplates/Selection",
                Path = "WWPBaseObjects/WWPTemplates/Selection/" + name
            };
        }

        private static SdkTextTreeService BuildService(
            IEnumerable<SearchIndex.IndexEntry> entries,
            Func<string, string, string, string, string, string, string> readPartText,
            Func<string, IEnumerable<string>, string, string, string, string, string> readPartsText = null)
        {
            var cache = new IndexCacheService();
            List<SearchIndex.IndexEntry> loaded = entries.ToList();
            cache.LoadFromEntries(loaded);
            cache.MarkIndexComplete(loaded.Count);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                readPartText,
                null,
                readPartsText);
        }

        private static JObject Export(SdkTextTreeService service, string root, params string[] targets)
        {
            return Export(service, root, new JObject(), targets);
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
