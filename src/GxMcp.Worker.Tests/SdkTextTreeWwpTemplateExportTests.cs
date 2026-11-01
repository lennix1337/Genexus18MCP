using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The native export of a WorkWithPlus template. The SDK gives the template no text part, so its properties are read and
    // written in the layout of the official text. Only the SDK boundaries are replaced here; selection, planning, writing and
    // the reply are production code.
    public sealed class SdkTextTreeWwpTemplateExportTests
    {
        // The official layout of SynthTemplate with LF line endings: tabs for indentation, a blank line after each property, a
        // line with two tabs before #End, and a blank line before the closing brace.
        private static readonly string ExpectedDocument = string.Join("\n", new[]
        {
            "WorkWithPlusforWebTemplate SynthTemplate",
            "{",
            "\t#Properties",
            "\t\tName = \"SynthTemplate\"",
            "",
            "\t\tDescription = \"Synth description\"",
            "",
            "\t\tTemplateCategory = \"SynthCategory\"",
            "",
            "\t\tTemplateCategoryOrder = \"0\"",
            "",
            "\t\tTemplateType = \"Trn.Selection\"",
            "",
            "\t\tTemplateXml = \"<selection Name=\\\"SynthTemplate\\\" instanceType=\\\"WebPanel\\\"><table name=\\\"TableMain\\\" /></selection>\"",
            "",
            "\t\tMainTemplate = \"Main\"",
            "",
            "\t\tInstanceType = \"WebPanel\"",
            "",
            "\t\tPreviewImage = \"\"",
            "",
            "\t\tIsDefault = \"False\"",
            "",
            "\t\t",
            "\t#End",
            "",
            "}",
            ""
        });

        [Fact]
        public void A_template_is_written_with_its_properties_in_the_layout_of_the_official_text()
        {
            SdkTextTreeService service = BuildService(new[] { TemplateEntry("SynthTemplate") }, Template("SynthTemplate"));
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Equal(0, (int)response["result"]!["noTextParts"]!);
                string file = Directory.GetFiles(root, "SynthTemplate.gx", SearchOption.AllDirectories).Single();
                Assert.Equal(ExpectedDocument, File.ReadAllText(file));

                JObject item = ManifestItem(root, "SynthTemplate");
                Assert.Equal(new[] { "Properties" }, item["parts"]!.Values<string>().ToArray());
                Assert.Equal(Sha256(ExpectedDocument), (string)item["sha256"]!);
                Assert.Equal(Encoding.UTF8.GetByteCount(ExpectedDocument), (long)item["bytes"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_template_read_by_one_part_is_written_with_its_properties_too()
        {
            SdkTextTreeService service = BuildService(new[] { TemplateEntry("SynthTemplate") }, Template("SynthTemplate"));
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, new JObject { ["part"] = "Source" }, "SynthTemplate");

                Assert.Equal("ok", (string)response["status"]);
                string file = Directory.GetFiles(root, "SynthTemplate.gx", SearchOption.AllDirectories).Single();
                Assert.Equal(ExpectedDocument, File.ReadAllText(file));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_template_is_skipped_by_an_incremental_export_while_it_is_unchanged()
        {
            SearchIndex.IndexEntry entry = TemplateEntry("SynthTemplate");
            entry.LastUpdate = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            string root = NewTempDirectory();
            try
            {
                JObject first = Export(BuildService(new[] { entry }, Template("SynthTemplate")), root, "SynthTemplate");
                Assert.Equal(1, (int)first["result"]!["exported"]!);

                JObject second = Export(BuildService(new[] { entry }, Template("SynthTemplate")), root,
                    new JObject { ["mode"] = "newAndModified" }, "SynthTemplate");

                Assert.Equal(0, (int)second["result"]!["exported"]!);
                Assert.Equal(1, (int)second["result"]!["skipped"]!);
                Assert.Equal("skipped", (string)ObjectReply(second, "SynthTemplate")["status"]!);
                Assert.Single(Directory.GetFiles(root, "SynthTemplate.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_template_whose_properties_cannot_be_read_fails_alone_and_the_others_still_export()
        {
            string failed = McpResponse.Err(code: "WwpTemplatePropertiesReadFailed", message: "Template properties read failed: synth", target: "SynthAlpha");
            SdkTextTreeService service = BuildService(
                new[] { TemplateEntry("SynthAlpha"), ProcedureEntry("SynthZulu") },
                (target, type, guid, entityKey, path) => failed,
                (target, part, type, guid, entityKey, path) => target.EndsWith(":SynthZulu", StringComparison.Ordinal)
                    ? new JObject { ["source"] = "Synth text" }.ToString()
                    : NoTextPart(target, part, type, guid, entityKey, path),
                (target, parts, type, guid, entityKey, path) => target.EndsWith(":SynthZulu", StringComparison.Ordinal)
                    ? new JObject { ["parts"] = new JObject { ["Source"] = "Synth text" } }.ToString()
                    : NoTextPartsEnvelope(target, parts, type, guid, entityKey, path));
            string root = NewTempDirectory();
            try
            {
                JObject response = ExportWholeKb(service, root);

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Equal(1, (int)response["result"]!["exported"]!);
                JObject reply = ObjectReply(response, "SynthAlpha");
                Assert.Equal("error", (string)reply["status"]!);
                Assert.Equal("WwpTemplatePropertiesReadFailed", (string)reply["code"]!);
                Assert.Equal("ok", (string)ObjectReply(response, "SynthZulu")["status"]!);
                Assert.Empty(Directory.GetFiles(root, "SynthAlpha.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_template_whose_properties_name_another_object_is_a_stale_index_and_writes_nothing()
        {
            SdkTextTreeService service = BuildService(new[] { TemplateEntry("SynthTemplate") }, Template("SynthOther"));
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                JObject reply = ObjectReply(response, "SynthTemplate");
                Assert.Equal("error", (string)reply["status"]!);
                Assert.Equal("IndexEntryStale", (string)reply["code"]!);
                Assert.Contains("index", (string)reply["hint"]!);
                Assert.Empty(Directory.GetFiles(root, "*.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void The_template_read_carries_the_index_identity_of_its_object()
        {
            SearchIndex.IndexEntry entry = TemplateEntry("SynthTemplate");
            var seen = new List<string>();
            SdkTextTreeService service = BuildService(new[] { entry }, (target, type, guid, entityKey, path) =>
            {
                seen.AddRange(new[] { target, type, guid, entityKey, path });
                return WwpTemplateTestFixtures.PropertiesOf("SynthTemplate");
            });
            string root = NewTempDirectory();
            try
            {
                Export(service, root, "SynthTemplate");

                Assert.Equal(new[] { "WorkWithPlus for Web Template:SynthTemplate", WwpTemplateTestFixtures.TemplateType, entry.Guid, entry.EntityKey, entry.Path }, seen);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static Func<string, string, string, string, string, string> Template(string objectName)
        {
            return (target, type, guid, entityKey, path) => WwpTemplateTestFixtures.PropertiesOf(objectName);
        }

        private static string NoTextPart(string target, string part, string type, string guid, string entityKey, string path)
        {
            return new JObject { ["code"] = "PartNotFound", ["availableParts"] = new JArray() }.ToString();
        }

        private static string NoTextPartsEnvelope(string target, IEnumerable<string> parts, string type, string guid, string entityKey, string path)
        {
            return new JObject { ["parts"] = new JObject(), ["availableParts"] = new JArray() }.ToString();
        }

        private static SdkTextTreeService BuildService(
            IEnumerable<SearchIndex.IndexEntry> entries,
            Func<string, string, string, string, string, string> readTemplateProperties,
            Func<string, string, string, string, string, string, string> readPartText = null,
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
                readPartText ?? NoTextPart,
                null,
                readPartsText ?? NoTextPartsEnvelope,
                readTemplateProperties);
        }

        private static SearchIndex.IndexEntry TemplateEntry(string name)
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = WwpTemplateTestFixtures.TemplateType,
                Module = "SynthModuleA",
                Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11",
                EntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001",
                ParentPath = "SynthModuleA/Selection",
                ParentFolderPath = "Root Module/SynthModuleA/Selection",
                Path = "SynthModuleA/Selection/" + name
            };
        }

        private static SearchIndex.IndexEntry ProcedureEntry(string name)
        {
            return new SearchIndex.IndexEntry
            {
                Name = name,
                Type = "Procedure",
                Module = "SynthModuleA",
                Guid = "7c2d1e63-1e9b-4b68-8a40-3a7e2d1b0f22",
                EntityKey = "7c2d1e63-1e9b-4b68-8a40-3a7e2d1b0f22-9002",
                ParentPath = "SynthModuleA/Procs",
                ParentFolderPath = "Root Module/SynthModuleA/Procs",
                Path = "SynthModuleA/Procs/" + name
            };
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

        private static JObject ExportWholeKb(SdkTextTreeService service, string root)
        {
            return JObject.Parse(service.Execute("export_kb_to_text", null, new JObject
            {
                ["outputPath"] = root,
                ["mode"] = "all"
            }, CancellationToken.None));
        }

        private static JObject ObjectReply(JObject response, string name)
        {
            return response["result"]!["objects"]!.Cast<JObject>().Single(item => (string)item["name"]! == name);
        }

        private static JObject ManifestItem(string root, string name)
        {
            JArray objects = JObject.Parse(File.ReadAllText(Path.Combine(root, SdkTextTreeService.ManifestFileName)))["objects"] as JArray;
            return objects!.Cast<JObject>().Single(item => (string)item["name"]! == name);
        }

        private static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-wwptemplate-export-" + Guid.NewGuid().ToString("N"));
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
