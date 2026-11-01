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
    // The GUID or EntityKey of an object selects it only on the native export. The delete and the legacy export act by
    // Tipo:Nome, so the same identity selects nothing there: the delete loop is not reached and nothing is written.
    // The delete and the legacy export run through ObjectTextService.Execute with no ObjectService, so a selection that
    // reached the loop would fail on the object read, not answer NoObjectsMatched.
    public sealed class IdentitySelectionScopeTests
    {
        private const string ProcGuid = "30000000-0000-4000-8000-00000000000b";
        private const string ProcEntityKey = "9c1d0e2f-3a4b-4c5d-8e6f-7a8b9c0d1e2f-9001";

        [Theory]
        [InlineData(ProcGuid)]
        [InlineData(ProcEntityKey)]
        public void The_delete_with_the_identity_of_an_object_selects_nothing(string target)
        {
            var service = new ObjectTextService(null, BuildIndex());

            JObject response = JObject.Parse(service.Execute("delete_kb_objects", target,
                new JObject { ["confirm"] = true }, CancellationToken.None));

            Assert.Equal("NoObjectsMatched", (string)response["error"]?["code"]);
        }

        [Theory]
        [InlineData(ProcGuid)]
        [InlineData(ProcEntityKey)]
        public void The_legacy_export_with_the_identity_of_an_object_selects_nothing(string target)
        {
            string root = NewTempDirectory();
            try
            {
                var service = new ObjectTextService(null, BuildIndex());

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", target,
                    new JObject { ["outputPath"] = root }, CancellationToken.None));

                Assert.Equal("NoObjectsMatched", (string)response["error"]?["code"]);
                Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        [Theory]
        [InlineData(ProcGuid)]
        [InlineData(ProcEntityKey)]
        public void The_native_export_still_selects_an_object_by_its_identity(string target)
        {
            string root = NewTempDirectory();
            try
            {
                var service = BuildNativeService();

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", target,
                    new JObject { ["outputPath"] = root, ["mode"] = "all" }, CancellationToken.None));

                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Single(Directory.GetFiles(root, "SynthProcB.gx", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(root, "SynthProcA.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static SdkTextTreeService BuildNativeService()
        {
            IndexCacheService cache = BuildIndex();
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) => new JObject
                {
                    ["source"] = "Synth text",
                    ["objectName"] = "SynthProcB",
                    ["objectType"] = "Procedure"
                }.ToString());
        }

        private static IndexCacheService BuildIndex()
        {
            var entries = new[]
            {
                Module("SynthA", "10000000-0000-4000-8000-00000000000a"),
                Module("SynthB", "10000000-0000-4000-8000-00000000000b"),
                Procedure("SynthProcA", "SynthA", "SynthA/Procs", "30000000-0000-4000-8000-00000000000a", "ea000000-0000-4000-8000-00000000000a-9001"),
                Procedure("SynthProcB", "SynthB", "SynthB/Procs", ProcGuid, ProcEntityKey)
            };
            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries);
            cache.MarkIndexComplete(entries.Length);
            return cache;
        }

        private static SearchIndex.IndexEntry Module(string name, string guid)
        {
            return new SearchIndex.IndexEntry
            {
                Type = "Module",
                Name = name,
                Module = name,
                Path = name,
                Guid = guid,
                EntityKey = guid + "-module"
            };
        }

        private static SearchIndex.IndexEntry Procedure(string name, string module, string parentPath, string guid, string entityKey)
        {
            return new SearchIndex.IndexEntry
            {
                Type = "Procedure",
                Name = name,
                Module = module,
                Parent = parentPath,
                ParentPath = parentPath,
                ParentFolderPath = "Root Module/" + parentPath,
                Path = parentPath + "/" + name,
                Guid = guid,
                EntityKey = entityKey
            };
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-identity-scope-" + Guid.NewGuid().ToString("N"));
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
