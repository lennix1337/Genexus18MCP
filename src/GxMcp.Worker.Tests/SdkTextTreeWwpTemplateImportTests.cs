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
    // The native import of a WorkWithPlus template file. The import does not write templates. In a tree, the template file is
    // skipped with its own code, counted apart, and does not stop the batch; a template the request names is an error. The
    // service has no write service, so a file that does reach the write answers an error, and no KB is written by either path.
    public sealed class SdkTextTreeWwpTemplateImportTests
    {
        private const string TemplateFile = "WorkWithPlusforWebTemplate SynthAlpha\n{\n\t#Properties\n\t\tName = \"SynthAlpha\"\n\n\t\t\n\t#End\n\n}\n";
        private const string AliasTemplateFile = "WWPTemplate SynthAlpha\n{\n\t#Properties\n\t\tName = \"SynthAlpha\"\n\n\t\t\n\t#End\n\n}\n";
        private const string OtherTemplateFile = "WorkWithPlusforWebTemplate SynthBeta\n{\n\t#Properties\n\t\tName = \"SynthBeta\"\n\n\t\t\n\t#End\n\n}\n";
        private const string ProcedureFile = "Procedure SynthZulu\n{\n#Source\nSynth text\n#End\n}\n";

        [Fact]
        public void A_template_file_of_a_tree_is_skipped_with_its_own_code_and_no_part_is_sent()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);

                JObject response = Import(BuildService(), root);

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.Equal(1, (int)response["result"]!["templatesNotImported"]!);
                Assert.False((bool)response["result"]!["stoppedOnError"]!);
                JObject item = Assert.Single(response["result"]!["objects"]!.Cast<JObject>());
                Assert.Equal("skipped", (string)item["status"]!);
                Assert.Equal("WwpTemplateNotImported", (string)item["code"]!);
                Assert.Equal("SynthAlpha", (string)item["name"]!);
                Assert.Equal("WorkWithPlusforWebTemplate", (string)item["type"]!);
                Assert.Contains("genexus_properties", (string)item["hint"]!);
                Assert.Null(item["partResults"]);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_tree_whose_only_file_is_a_template_imports_as_ok_and_writes_nothing()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);

                JObject response = Import(BuildService(), root, new JObject { ["dryRun"] = false, ["confirm"] = true });

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal("ObjectTextNativeImportCompleted", (string)response["code"]!);
                Assert.Equal(0, (int)response["result"]!["succeeded"]!);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.Equal(1, (int)response["result"]!["templatesNotImported"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void Templates_do_not_stop_a_tree_even_with_stopOnError()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthBeta.gx"), OtherTemplateFile);

                JObject response = Import(BuildService(), root, new JObject { ["stopOnError"] = true });

                Assert.Equal("ok", (string)response["status"]);
                Assert.False((bool)response["result"]!["stoppedOnError"]!);
                Assert.Equal(0, (int)response["result"]!["remaining"]!);
                Assert.Equal(2, (int)response["result"]!["templatesNotImported"]!);
                Assert.All(response["result"]!["objects"]!.Cast<JObject>(), item => Assert.Equal("skipped", (string)item["status"]!));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_template_skipped_in_a_tree_does_not_hide_the_failure_of_the_other_files()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthZulu.gx"), ProcedureFile);

                JObject response = Import(BuildService(), root);

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Equal(1, (int)response["result"]!["templatesNotImported"]!);
                JObject[] items = response["result"]!["objects"]!.Cast<JObject>().ToArray();
                Assert.Equal("skipped", (string)items.Single(item => (string)item["name"]! == "SynthAlpha")["status"]!);
                // The procedure was attempted: it has its own part result, which the missing write service answers.
                Assert.NotNull(items.Single(item => (string)item["name"]! == "SynthZulu")["partResults"]);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_template_with_the_alias_header_is_skipped_too()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), AliasTemplateFile);

                JObject response = Import(BuildService(), root);

                JObject item = Assert.Single(response["result"]!["objects"]!.Cast<JObject>());
                Assert.Equal("WwpTemplateNotImported", (string)item["code"]!);
                Assert.Null(item["partResults"]);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_template_the_request_names_as_a_target_is_an_error_and_stops_with_stopOnError()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthBeta.gx"), OtherTemplateFile);

                JObject response = Import(BuildService(), root, new JObject { ["targets"] = new JArray("SynthAlpha"), ["stopOnError"] = true });

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Equal(0, (int)response["result"]!["templatesNotImported"]!);
                Assert.True((bool)response["result"]!["stoppedOnError"]!);
                JObject item = Assert.Single(response["result"]!["objects"]!.Cast<JObject>());
                Assert.Equal("error", (string)item["status"]!);
                Assert.Equal("WwpTemplateImportRefused", (string)item["code"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // The request's selector comes before the refusal: a template it does not name is not reported at all.
        [Fact]
        public void A_template_the_request_does_not_select_is_not_reported()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthZulu.gx"), ProcedureFile);

                JObject response = Import(BuildService(), root, new JObject { ["targets"] = new JArray("SynthZulu") });

                Assert.Equal(0, (int)response["result"]!["templatesNotImported"]!);
                Assert.DoesNotContain(response["result"]!["objects"]!.Cast<JObject>(), item => (string)item["name"]! == "SynthAlpha");
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // The export of a KB with a template writes the template, and the import of that very tree is not partial because of it.
        [Fact]
        public void A_plan_of_a_tree_the_export_wrote_is_ok_and_its_template_is_skipped()
        {
            SdkTextTreeService service = BuildTreeService();
            string root = NewTempDirectory();
            try
            {
                JObject export = JObject.Parse(service.Execute("export_kb_to_text", null, new JObject
                {
                    ["outputPath"] = root,
                    ["mode"] = "all"
                }, CancellationToken.None));
                Assert.Equal(2, (int)export["result"]!["exported"]!);

                JObject plan = JObject.Parse(service.Execute("import_text_to_kb", null, new JObject
                {
                    ["inputPath"] = root,
                    ["dryRun"] = true,
                    ["listOnly"] = true,
                    ["stopOnError"] = true
                }, CancellationToken.None));

                Assert.Equal("ok", (string)plan["status"]);
                Assert.Equal(0, (int)plan["result"]!["failed"]!);
                Assert.False((bool)plan["result"]!["stoppedOnError"]!);
                JObject[] items = plan["result"]!["objects"]!.Cast<JObject>().ToArray();
                Assert.Equal("WwpTemplateNotImported", (string)items.Single(item => (string)item["name"]! == "SynthAlpha")["code"]!);
                Assert.Equal("planned", (string)items.Single(item => (string)item["name"]! == "SynthZulu")["status"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // A wildcard selects the objects of the tree and names none of them: the templates are skipped, with or without stopOnError.
        [Theory]
        [InlineData("targets", false)]
        [InlineData("targets", true)]
        [InlineData("name", false)]
        [InlineData("name", true)]
        public void A_wildcard_selects_the_templates_of_a_tree_without_naming_them(string form, bool stopOnError)
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthBeta.gx"), OtherTemplateFile);
                var options = new JObject { ["stopOnError"] = stopOnError };
                if (form == "targets") options["targets"] = new JArray("*");
                else options["name"] = "*";

                JObject response = Import(BuildService(), root, options);

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.Equal(2, (int)response["result"]!["templatesNotImported"]!);
                Assert.False((bool)response["result"]!["stoppedOnError"]!);
                Assert.All(response["result"]!["objects"]!.Cast<JObject>(), item => Assert.Equal("WwpTemplateNotImported", (string)item["code"]!));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // The wildcard does not name the template, but a template the request names by its name is still an error.
        [Fact]
        public void A_template_named_by_its_name_is_still_an_error_next_to_a_wildcard()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthBeta.gx"), OtherTemplateFile);

                JObject response = Import(BuildService(), root, new JObject { ["targets"] = new JArray("*", "SynthAlpha") });

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.Equal(1, (int)response["result"]!["templatesNotImported"]!);
                JObject[] items = response["result"]!["objects"]!.Cast<JObject>().ToArray();
                Assert.Equal("WwpTemplateImportRefused", (string)items.Single(item => (string)item["name"]! == "SynthAlpha")["code"]!);
                Assert.Equal("WwpTemplateNotImported", (string)items.Single(item => (string)item["name"]! == "SynthBeta")["code"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // A typed target names the template with the type the export reports or with the file's keyword: each spelling names it.
        [Theory]
        [InlineData("WorkWithPlus for Web Template")]
        [InlineData("WorkWithPlusforWebTemplate")]
        [InlineData("workwithplus for web template")]
        [InlineData("WWPTemplate")]
        public void A_typed_target_names_the_template_whatever_the_spelling_of_its_type(string requestedType)
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                var target = new JObject { ["type"] = requestedType, ["name"] = "SynthAlpha" };

                JObject response = Import(BuildService(), root, new JObject { ["targets"] = new JArray(target) });

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                JObject item = Assert.Single(response["result"]!["objects"]!.Cast<JObject>());
                Assert.Equal("WwpTemplateImportRefused", (string)item["code"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // A typed target of another type names no template: the template is not reported at all.
        [Fact]
        public void A_typed_target_of_another_type_does_not_name_the_template()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                var target = new JObject { ["type"] = "Procedure", ["name"] = "SynthAlpha" };

                JObject response = Import(BuildService(), root, new JObject { ["targets"] = new JArray(target) });

                Assert.Equal(0, (int)response["result"]!["templatesNotImported"]!);
                Assert.Equal(0, (int)response["result"]!["failed"]!);
                Assert.Empty(response["result"]!["objects"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        // The type filter takes the template's type with either spelling, and a filter of another type selects no template.
        [Fact]
        public void A_type_filter_with_the_template_type_selects_the_template_files()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);

                JObject response = Import(BuildService(), root, new JObject { ["type"] = "WorkWithPlus for Web Template" });

                Assert.Equal("ok", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["templatesNotImported"]!);
                Assert.Equal("WwpTemplateNotImported", (string)Assert.Single(response["result"]!["objects"]!.Cast<JObject>())["code"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_type_filter_of_another_type_selects_no_template()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);

                JObject response = Import(BuildService(), root, new JObject { ["type"] = "Procedure" });

                Assert.Equal(0, (int)response["result"]!["templatesNotImported"]!);
                Assert.Empty(response["result"]!["objects"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static SdkTextTreeService BuildService()
        {
            return new SdkTextTreeService(
                new TextTreeSelectionService(new IndexCacheService()),
                new ObjectService(null, null),
                new IndexCacheService());
        }

        // The export of a template and a procedure, with the SDK reads replaced at their boundaries, for the round trip.
        private static SdkTextTreeService BuildTreeService()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry
                {
                    Name = "SynthAlpha",
                    Type = WwpTemplateTestFixtures.TemplateType,
                    Module = "SynthModuleA",
                    Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11",
                    EntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001",
                    ParentFolderPath = "Root Module/SynthModuleA/Selection",
                    Path = "SynthModuleA/Selection/SynthAlpha"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "SynthZulu",
                    Type = "Procedure",
                    Module = "SynthModuleA",
                    Guid = "7c2d1e63-1e9b-4b68-8a40-3a7e2d1b0f22",
                    EntityKey = "7c2d1e63-1e9b-4b68-8a40-3a7e2d1b0f22-9002",
                    ParentFolderPath = "Root Module/SynthModuleA/Procs",
                    Path = "SynthModuleA/Procs/SynthZulu"
                }
            };
            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries.ToList());
            cache.MarkIndexComplete(entries.Length);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) => target.EndsWith(":SynthZulu", StringComparison.Ordinal)
                    ? new JObject { ["source"] = "Synth text" }.ToString()
                    : new JObject { ["code"] = "PartNotFound", ["availableParts"] = new JArray() }.ToString(),
                null,
                null,
                (target, type, guid, entityKey, path) => WwpTemplateTestFixtures.PropertiesOf("SynthAlpha"));
        }

        private static JObject Import(SdkTextTreeService service, string root, JObject options = null)
        {
            var args = new JObject
            {
                ["inputPath"] = root,
                ["confirm"] = true,
                ["dryRun"] = false
            };
            if (options != null)
                foreach (JProperty option in options.Properties()) args[option.Name] = option.Value;
            return JObject.Parse(service.Execute("import_text_to_kb", null, args, CancellationToken.None));
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-wwptemplate-import-" + Guid.NewGuid().ToString("N"));
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
