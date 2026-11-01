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
            // A reply without the parts list cannot say the object has none: it is a failed read, not a type without parts.
            Assert.False(SdkTextTreeService.IsNoTextPartsReply(Reply("PartNotFound")));
            Assert.True(SdkTextTreeService.IsNoTextPartsReply(Reply("PartNotFound", new JArray())));
            Assert.False(SdkTextTreeService.IsNoTextPartsReply(Reply("PartNotFound", new JArray("Rules"))));
            Assert.False(SdkTextTreeService.IsNoTextPartsReply(Reply("ObjectNotFound")));
            Assert.False(SdkTextTreeService.IsNoTextPartsReply(null));
        }

        [Fact]
        public void An_object_without_text_parts_is_reported_with_its_own_code_and_the_property_commands()
        {
            SdkTextTreeService service = BuildService(new[] { SynthEntry("SynthTemplate") }, (target, part, type, guid, entityKey, path) =>
                Reply("PartNotFound", new JArray()).ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                // A named target is asked for its text, so it stays a failure and is not counted as having no text parts.
                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Equal(0, (int)response["result"]!["noTextParts"]!);
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

        [Fact]
        public void A_multi_part_envelope_is_a_no_text_parts_envelope_only_when_it_lists_no_part()
        {
            Assert.True(SdkTextTreeService.IsNoTextPartsEnvelope(new JObject { ["parts"] = new JObject(), ["availableParts"] = new JArray() }));
            Assert.False(SdkTextTreeService.IsNoTextPartsEnvelope(new JObject { ["parts"] = new JObject(), ["availableParts"] = new JArray("Rules") }));
            Assert.False(SdkTextTreeService.IsNoTextPartsEnvelope(new JObject { ["parts"] = new JObject() }));
            Assert.False(SdkTextTreeService.IsNoTextPartsEnvelope(null));
        }

        [Fact]
        public void A_single_part_read_without_the_parts_list_is_a_failure_not_a_type_without_parts()
        {
            SdkTextTreeService service = BuildService(new[] { SynthEntry("SynthTemplate") }, (target, part, type, guid, entityKey, path) =>
                Reply("PartNotFound").ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                JObject reply = ObjectReply(response, "SynthTemplate");
                Assert.Equal("error", (string)reply["status"]);
                Assert.Equal("PartNotFound", (string)reply["code"]!);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_multi_part_read_without_the_parts_list_is_a_failure_not_a_type_without_parts()
        {
            SdkTextTreeService service = BuildService(
                new[] { SynthEntry("SynthTemplate") },
                (target, part, type, guid, entityKey, path) => throw new InvalidOperationException("part=all reads every part at once"),
                (target, parts, type, guid, entityKey, path) => new JObject { ["parts"] = new JObject() }.ToString());
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, new JObject { ["part"] = "all" }, "SynthTemplate");

                JObject reply = ObjectReply(response, "SynthTemplate");
                Assert.Equal("error", (string)reply["status"]);
                Assert.NotEqual("ObjectHasNoTextParts", (string)reply["code"]);
                Assert.Equal("The SDK returned no textual parts.", (string)reply["message"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // A module named as a target selects the objects under it, its template among them. The SDK gives the template's
        // properties, so the template is written with them and the reply is ok. The module filter does the same with no target.
        [Fact]
        public void A_named_module_exports_its_template_with_its_properties_and_the_reply_ok()
        {
            string root = NewTempDirectory();
            try
            {
                JObject response = JObject.Parse(BuildModuleService().Execute("export_kb_to_text", null, new JObject
                {
                    ["outputPath"] = root,
                    ["mode"] = "all",
                    ["targets"] = new JArray("SynthModule")
                }, CancellationToken.None));

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.Equal(0, (int)response["result"]!["noTextParts"]!);
                Assert.Equal(2, (int)response["result"]!["exported"]!);
                Assert.Equal("ok", (string)ObjectReply(response, "SynthTemplate")["status"]!);
                Assert.Single(Directory.GetFiles(root, "SynthTemplate.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_module_filter_exports_the_template_and_keeps_the_reply_ok()
        {
            string root = NewTempDirectory();
            try
            {
                JObject response = JObject.Parse(BuildModuleService().Execute("export_kb_to_text", null, new JObject
                {
                    ["outputPath"] = root,
                    ["mode"] = "all",
                    ["module"] = "SynthModule"
                }, CancellationToken.None));

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.Equal(0, (int)response["result"]!["noTextParts"]!);
                Assert.Equal(2, (int)response["result"]!["exported"]!);
                Assert.Equal(0, (int)response["result"]!["remaining"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // The export checks for cancellation before each object. The first template is counted before the next check, so
        // the cancelled reply keeps the count of the objects without text parts that the export already passed.
        [Fact]
        public void A_cancelled_native_export_reports_the_objects_without_text_parts_it_already_counted()
        {
            string root = NewTempDirectory();
            var cancellation = new CancellationTokenSource();
            try
            {
                SdkTextTreeService service = BuildService(
                    new[] { SynthEntry("SynthAlpha"), SynthEntry("SynthZulu") },
                    (target, part, type, guid, entityKey, path) =>
                    {
                        string name = target.Substring(target.IndexOf(':') + 1);
                        if (name == "SynthAlpha") cancellation.Cancel();
                        return Reply("PartNotFound", new JArray(), name).ToString();
                    });

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", null, new JObject
                {
                    ["outputPath"] = root,
                    ["mode"] = "all"
                }, cancellation.Token));

                Assert.Equal("Cancelled", (string)response["code"]);
                Assert.Equal(1, (int)response["result"]!["noTextParts"]!);
                Assert.Equal(0, (int)response["result"]!["exported"]!);
            }
            finally
            {
                cancellation.Dispose();
                DeleteTree(root);
            }
        }

        // A KB-wide export counts an object without text parts as skipped, so the rest of the export still completes and
        // a deleted object's file is removed. The earlier export wrote SynthDeleted; the later one no longer holds it.
        [Fact]
        public void A_full_export_counts_an_object_without_text_parts_and_still_removes_a_deleted_object()
        {
            string root = NewTempDirectory();
            try
            {
                SdkTextTreeService earlier = BuildService(
                    new[] { SynthEntry("SynthTemplate"), SynthEntry("SynthDeleted", "Procedure") }, ReadTextOrNoParts);
                Assert.Equal(0, (int)ExportWholeKb(earlier, root)["result"]!["failed"]!);
                string deleted = Directory.GetFiles(root, "SynthDeleted.gx", SearchOption.AllDirectories).Single();

                SdkTextTreeService later = BuildService(new[] { SynthEntry("SynthTemplate") }, ReadTextOrNoParts);
                JObject response = ExportWholeKb(later, root);

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["noTextParts"]!);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.True((int)response["result"]!["filesDeleted"]! >= 1);
                Assert.False(File.Exists(deleted));
                JObject reply = ObjectReply(response, "SynthTemplate");
                Assert.Equal("skipped", (string)reply["status"]!);
                Assert.Equal("ObjectHasNoTextParts", (string)reply["code"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // A module with a template (no text part) and a procedure (text). A target named after the module selects the objects
        // it holds through includeChildren, as it does for any module.
        private static SdkTextTreeService BuildModuleService()
        {
            var module = new SearchIndex.IndexEntry
            {
                Type = "Module",
                Name = "SynthModule",
                Module = "SynthModule",
                Path = "SynthModule",
                Guid = "70000000-0000-4000-8000-00000000000a",
                EntityKey = "70000000-0000-4000-8000-00000000000a-module"
            };
            var template = new SearchIndex.IndexEntry
            {
                Type = "WorkWithPlus for Web Template",
                Name = "SynthTemplate",
                Module = "SynthModule",
                Parent = "Selection",
                ParentPath = "SynthModule/Selection",
                ParentFolderPath = "Root Module/SynthModule/Selection",
                Path = "SynthModule/Selection/SynthTemplate",
                Guid = "70000000-0000-4000-8000-00000000000b",
                EntityKey = "70000000-0000-4000-8000-00000000000b-9001"
            };
            var procedure = new SearchIndex.IndexEntry
            {
                Type = "Procedure",
                Name = "SynthProcedure",
                Module = "SynthModule",
                Parent = "Procs",
                ParentPath = "SynthModule/Procs",
                ParentFolderPath = "Root Module/SynthModule/Procs",
                Path = "SynthModule/Procs/SynthProcedure",
                Guid = "70000000-0000-4000-8000-00000000000c",
                EntityKey = "70000000-0000-4000-8000-00000000000c-9001"
            };
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[] { module, template, procedure });
            cache.MarkIndexComplete(3);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) =>
                {
                    string name = target.Substring(target.IndexOf(':') + 1);
                    return name == "SynthTemplate"
                        ? Reply("PartNotFound", new JArray()).ToString()
                        : new JObject { ["source"] = "Synth text", ["objectName"] = name, ["objectType"] = type }.ToString();
                },
                null,
                null,
                (target, type, guid, entityKey, path) => target.EndsWith(":SynthTemplate", StringComparison.Ordinal)
                    ? WwpTemplateTestFixtures.PropertiesOf("SynthTemplate")
                    : WwpTemplateTestFixtures.NotATemplate(target));
        }

        // The template has no text part; the other objects answer their text, as the read does for a Procedure.
        private static string ReadTextOrNoParts(string target, string part, string type, string guid, string entityKey, string path)
        {
            string name = target.Substring(target.IndexOf(':') + 1);
            return name == "SynthTemplate"
                ? Reply("PartNotFound", new JArray()).ToString()
                : new JObject { ["source"] = "Synth text", ["objectName"] = name, ["objectType"] = type }.ToString();
        }

        private static JObject ExportWholeKb(SdkTextTreeService service, string root)
        {
            var args = new JObject
            {
                ["outputPath"] = root,
                ["mode"] = "all"
            };
            return JObject.Parse(service.Execute("export_kb_to_text", null, args, CancellationToken.None));
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

        private static SearchIndex.IndexEntry SynthEntry(string name, string type = "WorkWithPlus for Web Template")
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = type,
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
            Func<string, IEnumerable<string>, string, string, string, string, string> readPartsText = null,
            Func<string, string, string, string, string, string> readTemplateProperties = null)
        {
            var cache = new IndexCacheService();
            List<SearchIndex.IndexEntry> loaded = entries.ToList();
            cache.LoadFromEntries(loaded);
            cache.MarkIndexComplete(loaded.Count);
            // Unless a test gives the template's properties, the SDK says the object is not a template, so its
            // missing text parts are the no-text-parts case. The template's own export is in SdkTextTreeWwpTemplateExportTests.
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                readPartText,
                null,
                readPartsText,
                readTemplateProperties ?? ((target, type, guid, entityKey, path) => WwpTemplateTestFixtures.NotATemplate(target)));
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
