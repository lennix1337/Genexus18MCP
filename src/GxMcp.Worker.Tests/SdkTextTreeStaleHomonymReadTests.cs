using System;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A Transaction and its Table share a name. When the Transaction was deleted outside the MCP and the index is out of
    // date, the single-part read must answer for the Transaction: a bare name would match both entries and answer
    // AmbiguousName, not IndexEntryStale. This test uses the production ObjectService read, with no KB open.
    public sealed class SdkTextTreeStaleHomonymReadTests
    {
        private const string TransactionGuid = "50000000-0000-4000-8000-00000000000a";
        private const string TableGuid = "50000000-0000-4000-8000-00000000000b";

        [Fact]
        public void A_stale_transaction_whose_table_shares_its_name_is_IndexEntryStale_not_AmbiguousName()
        {
            string root = NewTempDirectory();
            try
            {
                var cache = new IndexCacheService();
                cache.LoadFromEntries(new[] { Entry("Transaction", TransactionGuid), Entry("Table", TableGuid) });
                cache.MarkIndexComplete(2);
                var build = new BuildService();
                var kb = new KbService(cache);
                kb.SetBuildService(build);
                build.SetKbService(kb);
                cache.SetBuildService(build);
                var service = new SdkTextTreeService(new TextTreeSelectionService(cache), new ObjectService(kb, build), cache);

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", TransactionGuid,
                    new JObject { ["outputPath"] = root, ["mode"] = "all" }, CancellationToken.None));

                JObject reply = response["result"]!["objects"]!.Cast<JObject>()
                    .Single(item => (string)item["type"]! == "Transaction");
                Assert.Equal("IndexEntryStale", (string)reply["code"]!);
                Assert.DoesNotContain("AmbiguousName", reply.ToString());
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static SearchIndex.IndexEntry Entry(string type, string guid)
        {
            return new SearchIndex.IndexEntry
            {
                Type = type,
                Name = "SynthHomonym",
                Module = "SynthCore",
                Parent = "SynthCore",
                ParentPath = "SynthCore",
                ParentFolderPath = "Root Module/SynthCore",
                Path = "SynthCore/SynthHomonym",
                Guid = guid,
                EntityKey = guid + "-9001"
            };
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-stale-homonym-" + Guid.NewGuid().ToString("N"));
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
