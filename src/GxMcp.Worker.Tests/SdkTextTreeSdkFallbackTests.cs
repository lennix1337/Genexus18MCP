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
    // An explicit export target the index does not hold is answered by the SDK, as genexus_read answers it.
    // The index is consulted first: a target it holds never reaches the SDK. Only the SDK lookup and the part
    // read are replaced at their boundaries; selection, planning, writing and the not-found reply are production.
    public sealed class SdkTextTreeSdkFallbackTests
    {
        private const string SynthTemplateGuid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11";
        private const string SynthTemplateEntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001";

        [Fact]
        public void A_target_the_index_lacks_is_exported_when_the_SDK_holds_it()
        {
            var lookedUp = new List<string>();
            SearchIndex.IndexEntry sdkEntry = SynthTemplate();
            SdkTextTreeService service = BuildService(
                new[] { OtherObject() },
                explicitTarget =>
                {
                    lookedUp.Add(explicitTarget.Name);
                    return explicitTarget.Name == "SynthTemplate" ? sdkEntry : null;
                });
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Equal(new[] { "SynthTemplate" }, lookedUp.ToArray());
                Assert.Single(Directory.GetFiles(root, "SynthTemplate.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_target_the_index_holds_is_not_looked_up_in_the_SDK()
        {
            int lookups = 0;
            SdkTextTreeService service = BuildService(
                new[] { SynthTemplate() },
                explicitTarget =>
                {
                    lookups++;
                    return null;
                });
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Equal(0, lookups);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_target_neither_the_index_nor_the_SDK_holds_is_not_found_without_the_warming_hint_while_the_index_is_ready()
        {
            SdkTextTreeService service = BuildService(new[] { OtherObject() }, explicitTarget => null);
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal("ObjectNotFound", ErrorCode(response));
                Assert.Contains("SynthTemplate", (string)response["error"]!["message"]!);
                Assert.DoesNotContain("warming", ErrorHint(response) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_target_neither_the_index_nor_the_SDK_holds_gets_the_warming_reply_while_the_index_is_not_ready()
        {
            SdkTextTreeService service = BuildService(new SearchIndex.IndexEntry[0], explicitTarget => null);
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal("ObjectNotFoundIndexWarming", ErrorCode(response));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Fact]
        public void A_target_the_SDK_holds_is_exported_while_the_index_is_not_ready()
        {
            SearchIndex.IndexEntry sdkEntry = SynthTemplate();
            SdkTextTreeService service = BuildService(new SearchIndex.IndexEntry[0],
                explicitTarget => explicitTarget.Name == "SynthTemplate" ? sdkEntry : null);
            string root = NewTempDirectory();
            try
            {
                JObject response = Export(service, root, "SynthTemplate");

                Assert.Equal(1, (int)response["result"]!["exported"]!);
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
                Type = "WorkWithPlus for Web Template",
                Module = "WWPBaseObjects",
                Guid = SynthTemplateGuid,
                EntityKey = SynthTemplateEntityKey,
                ParentPath = "WWPBaseObjects/WWPTemplates/Selection",
                ParentFolderPath = "Root Module/WWPBaseObjects/WWPTemplates/Selection",
                Path = "WWPBaseObjects/WWPTemplates/Selection/SynthTemplate"
            };
        }

        private static SearchIndex.IndexEntry OtherObject()
        {
            return new SearchIndex.IndexEntry
            {
                Name = "SynthOther",
                Type = "Procedure",
                Module = "SynthModule",
                Guid = "0d4c2a18-7e3b-4f6a-8c5d-1b9e7f2a3c64",
                ParentPath = "SynthModule",
                ParentFolderPath = "Root Module/SynthModule",
                Path = "SynthModule/SynthOther"
            };
        }

        // An empty index is "not ready": it is loaded but holds no objects yet.
        private static SdkTextTreeService BuildService(
            SearchIndex.IndexEntry[] indexed,
            Func<TextSelector, SearchIndex.IndexEntry> sdkEntryLookup)
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(indexed);
            if (indexed.Length > 0) cache.MarkIndexComplete(indexed.Length);
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) => new JObject { ["source"] = "Template SynthTemplate" }.ToString(),
                sdkEntryLookup);
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

        private static string ErrorCode(JObject response) => (string)(response["error"]?["code"] ?? response["code"]);

        private static string ErrorHint(JObject response) => (string)(response["error"]?["hint"] ?? response["hint"]);

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-fallback-" + Guid.NewGuid().ToString("N"));
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
