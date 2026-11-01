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
    // Type:Name, so the same identity selects nothing there: the delete loop is not reached and nothing is written.
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

        // An ignore entry given by identity removes its object on every path. The list only takes entries away, so the
        // object the caller asked to keep never reaches the delete loop, and the native export drops it too.
        [Theory]
        [InlineData(ProcGuid)]
        [InlineData(ProcEntityKey)]
        public void The_delete_drops_an_ignore_entry_given_by_identity_and_keeps_that_object(string ignored)
        {
            var service = new ObjectTextService(null, BuildIndex());

            JObject response = JObject.Parse(service.Execute("delete_kb_objects", null, new JObject
            {
                ["targets"] = new JArray("SynthProcA", "SynthProcB"),
                ["ignore"] = new JArray(ignored),
                ["confirm"] = true
            }, CancellationToken.None));

            List<string> reached = ObjectNames(response);
            Assert.Contains("SynthProcA", reached);
            Assert.DoesNotContain("SynthProcB", reached);
        }

        [Fact]
        public void The_delete_drops_an_ignore_entry_given_by_name_and_keeps_that_object()
        {
            var service = new ObjectTextService(null, BuildIndex());

            JObject response = JObject.Parse(service.Execute("delete_kb_objects", null, new JObject
            {
                ["targets"] = new JArray("SynthProcA", "SynthProcB"),
                ["ignore"] = new JArray("Procedure:SynthProcB"),
                ["confirm"] = true
            }, CancellationToken.None));

            List<string> reached = ObjectNames(response);
            Assert.Contains("SynthProcA", reached);
            Assert.DoesNotContain("SynthProcB", reached);
        }

        // The delete and the legacy export select through this method, with identities off for the targets.
        [Theory]
        [InlineData(ProcGuid)]
        [InlineData(ProcEntityKey)]
        public void The_selection_of_the_delete_and_the_legacy_export_drops_an_ignore_entry_given_by_identity(string ignored)
        {
            var selection = new TextTreeSelectionService(BuildIndex());
            var args = new JObject
            {
                ["targets"] = new JArray("SynthProcA", "SynthProcB"),
                ["ignore"] = new JArray(ignored)
            };

            bool selected = selection.TrySelectEntries(null, args, allowAll: false,
                out List<SearchIndex.IndexEntry> entries, out string error);

            Assert.True(selected, error);
            Assert.Equal(new[] { "SynthProcA" }, entries.Select(entry => entry.Name).ToArray());
        }

        [Theory]
        [InlineData(ProcGuid)]
        [InlineData(ProcEntityKey)]
        public void The_native_export_drops_an_ignore_entry_given_by_identity(string ignored)
        {
            string root = NewTempDirectory();
            try
            {
                var service = BuildNamedNativeService();

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", null, new JObject
                {
                    ["outputPath"] = root,
                    ["mode"] = "all",
                    ["targets"] = new JArray("SynthProcA", "SynthProcB"),
                    ["ignore"] = new JArray(ignored)
                }, CancellationToken.None));

                Assert.Equal(1, (int)response["result"]!["exported"]!);
                Assert.Single(Directory.GetFiles(root, "SynthProcA.gx", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(root, "SynthProcB.gx", SearchOption.AllDirectories));
            }
            finally
            {
                DeleteTree(root);
            }
        }

        private static List<string> ObjectNames(JObject response)
        {
            return response.SelectTokens("$..results[*].name").Select(token => (string)token!).ToList();
        }

        // Each read names the object it was asked for, so every target passes the identity check on its own.
        private static SdkTextTreeService BuildNamedNativeService()
        {
            IndexCacheService cache = BuildIndex();
            return new SdkTextTreeService(
                new TextTreeSelectionService(cache),
                new ObjectService(null, null),
                cache,
                (target, part, type, guid, entityKey, path) => new JObject
                {
                    ["source"] = "Synth text",
                    ["objectName"] = target.Substring(target.IndexOf(':') + 1),
                    ["objectType"] = type
                }.ToString());
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
