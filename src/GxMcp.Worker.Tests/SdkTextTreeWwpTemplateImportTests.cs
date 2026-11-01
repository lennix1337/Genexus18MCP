using System;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The native import of a WorkWithPlus template file. The import does not write templates: the file is refused on its own,
    // before any part is sent to the KB. The service has no write service, so a file that does reach the write answers an
    // error, and no KB is written by either path.
    public sealed class SdkTextTreeWwpTemplateImportTests
    {
        private const string TemplateFile = "WorkWithPlusforWebTemplate SynthAlpha\n{\n\t#Properties\n\t\tName = \"SynthAlpha\"\n\n\t\t\n\t#End\n\n}\n";
        private const string ProcedureFile = "Procedure SynthZulu\n{\n#Source\nSynth text\n#End\n}\n";

        [Fact]
        public void A_template_file_is_refused_on_its_own_with_its_code_and_no_part_is_sent()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);

                JObject response = Import(BuildService(), root);

                Assert.Equal("partial", (string)response["status"]);
                Assert.Equal(1, (int)response["result"]!["failed"]!);
                Assert.False((bool)response["result"]!["stoppedOnError"]!);
                JObject item = Assert.Single(response["result"]!["objects"]!.Cast<JObject>());
                Assert.Equal("error", (string)item["status"]!);
                Assert.Equal("WwpTemplateImportRefused", (string)item["code"]!);
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
        public void The_refusal_of_a_template_file_does_not_stop_the_other_files_of_the_batch()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthZulu.gx"), ProcedureFile);

                JObject response = Import(BuildService(), root);

                Assert.False((bool)response["result"]!["stoppedOnError"]!);
                Assert.Equal(2, (int)response["result"]!["failed"]!);
                JObject[] items = response["result"]!["objects"]!.Cast<JObject>().ToArray();
                Assert.Equal("WwpTemplateImportRefused", (string)items.Single(item => (string)item["name"]! == "SynthAlpha")["code"]!);
                // The procedure was attempted: it has its own part result, which the missing write service answers.
                Assert.NotNull(items.Single(item => (string)item["name"]! == "SynthZulu")["partResults"]);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void With_stopOnError_the_batch_stops_at_the_template_file()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);
                File.WriteAllText(Path.Combine(root, "SynthZulu.gx"), ProcedureFile);

                JObject response = Import(BuildService(), root, new JObject { ["stopOnError"] = true });

                Assert.True((bool)response["result"]!["stoppedOnError"]!);
                JObject item = Assert.Single(response["result"]!["objects"]!.Cast<JObject>());
                Assert.Equal("WwpTemplateImportRefused", (string)item["code"]!);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_dry_run_refuses_the_template_file_too()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "SynthAlpha.gx"), TemplateFile);

                JObject response = Import(BuildService(), root, new JObject { ["dryRun"] = true, ["confirm"] = false });

                Assert.True((bool)response["result"]!["dryRun"]!);
                JObject item = Assert.Single(response["result"]!["objects"]!.Cast<JObject>());
                Assert.Equal("WwpTemplateImportRefused", (string)item["code"]!);
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
