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
    // Two procedures share the name SynthProc, one in each module. The index keeps one object per Type:Name, so it holds
    // only one of them: each test builds the index that way and checks what a name qualified by a module selects and
    // deletes from it.
    public sealed class QualifiedObjectNameTests
    {
        private static readonly SearchIndex.IndexEntry ProcInA = Procedure("SynthProc", "SynthModuleA", "SynthModuleA", "50000000-0000-4000-8000-00000000000a");
        private static readonly SearchIndex.IndexEntry ProcInB = Procedure("SynthProc", "SynthModuleB", "SynthModuleB", "50000000-0000-4000-8000-00000000000b");

        [Theory]
        [InlineData("SynthModuleB.SynthProc")]
        [InlineData("synthmoduleb.synthproc")]
        public void A_name_qualified_by_the_module_of_the_object_selects_it(string name)
        {
            Assert.Equal(new[] { "SynthModuleB/SynthProc" }, SelectedPaths(IndexHolding(ProcInB), name));
        }

        [Fact]
        public void A_name_qualified_by_another_module_does_not_select_the_homonym_the_index_holds()
        {
            Assert.Empty(SelectedPaths(IndexHolding(ProcInB), "SynthModuleA.SynthProc"));
        }

        [Fact]
        public void A_bare_name_still_selects_the_object_the_index_holds()
        {
            Assert.Equal(new[] { "SynthModuleB/SynthProc" }, SelectedPaths(IndexHolding(ProcInB), "SynthProc"));
        }

        // A folder is not a module: its name in front of an object does not select it, while the module or the full path does.
        [Fact]
        public void A_folder_name_in_front_of_an_object_does_not_select_it()
        {
            var index = IndexHolding(Procedure("SynthProcInFolder", "SynthModuleA", "SynthModuleA/Procs", "50000000-0000-4000-8000-00000000000d"));

            Assert.Empty(SelectedPaths(index, "Procs.SynthProcInFolder"));
            Assert.Equal(new[] { "SynthModuleA/Procs/SynthProcInFolder" }, SelectedPaths(index, "SynthModuleA.Procs.SynthProcInFolder"));
            Assert.Equal(new[] { "SynthModuleA/Procs/SynthProcInFolder" }, SelectedPaths(index, "SynthModuleA.SynthProcInFolder"));
        }

        // A module nested in another is named by its whole chain, or by its own name; the outer name alone is not enough.
        [Fact]
        public void A_nested_module_is_named_by_its_chain_or_by_its_own_name()
        {
            var index = IndexHolding(Procedure("SynthNested", "SynthInner", "SynthModuleA/SynthInner", "50000000-0000-4000-8000-00000000000e"));
            const string nested = "SynthModuleA/SynthInner/SynthNested";

            Assert.Equal(new[] { nested }, SelectedPaths(index, "SynthModuleA.SynthInner.SynthNested"));
            Assert.Equal(new[] { nested }, SelectedPaths(index, "SynthInner.SynthNested"));
            Assert.Empty(SelectedPaths(index, "SynthModuleA.SynthNested"));
        }

        // The chain of modules holding an object that sits in a folder of the inner module is a qualifier too.
        [Fact]
        public void A_nested_module_chain_names_an_object_inside_a_folder_of_the_inner_module()
        {
            var index = IndexHolding(Procedure("SynthNested2", "SynthInner", "SynthModuleA/SynthInner/Procs", "50000000-0000-4000-8000-000000000010"));
            const string nested = "SynthModuleA/SynthInner/Procs/SynthNested2";

            Assert.Equal(new[] { nested }, SelectedPaths(index, "SynthModuleA.SynthInner.SynthNested2"));
            Assert.Equal(new[] { nested }, SelectedPaths(index, "SynthInner.SynthNested2"));
            Assert.Equal(new[] { nested }, SelectedPaths(index, "SynthModuleA.SynthInner.Procs.SynthNested2"));
            Assert.Empty(SelectedPaths(index, "SynthModuleA.Procs.SynthNested2"));
        }

        // The legacy export selects through the same rule: a name qualified by another module writes nothing.
        [Fact]
        public void A_legacy_export_named_by_another_module_writes_nothing()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-qualified-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var service = new ObjectTextService(null, IndexHolding(ProcInB));

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", null,
                    new JObject { ["name"] = "SynthModuleA.SynthProc", ["outputPath"] = root }, CancellationToken.None));

                Assert.Equal("NoObjectsMatched", (string)response["error"]?["code"]);
                Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void The_batch_delete_names_the_object_it_chose_by_its_module()
        {
            Assert.Equal(new[] { "Procedure:SynthModuleA.SynthProc" }, CapturedDeleteTargets(IndexHolding(ProcInA), "SynthModuleA.SynthProc"));
        }

        [Fact]
        public void A_bare_name_delete_names_the_object_it_chose_by_its_module()
        {
            Assert.Equal(new[] { "Procedure:SynthModuleA.SynthProc" }, CapturedDeleteTargets(IndexHolding(ProcInA), "SynthProc"));
        }

        [Fact]
        public void A_batch_delete_of_another_module_names_nothing()
        {
            Assert.Empty(CapturedDeleteTargets(IndexHolding(ProcInB), "SynthModuleA.SynthProc"));
        }

        // An object outside any module has no module to name, so its delete keeps the bare name.
        [Fact]
        public void An_object_outside_any_module_is_deleted_by_its_bare_name()
        {
            var index = IndexHolding(Procedure("SynthRootProc", null, null, "50000000-0000-4000-8000-00000000000f"));

            Assert.Equal(new[] { "Procedure:SynthRootProc" }, CapturedDeleteTargets(index, "SynthRootProc"));
        }

        // The resolution finds the object in another module than the one the target names: nothing is written.
        [Fact]
        public void A_delete_named_by_a_module_refuses_the_object_of_another_module()
        {
            var service = new ObjectService(null, null);
            service.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => FoundIn("SynthModuleB"));

            var json = JObject.Parse(service.DeleteObject("Procedure:SynthModuleA.SynthProc", "Procedure", confirm: true));

            Assert.Equal("ObjectModuleMismatch", json["error"]?["code"]?.ToString());
            Assert.Equal("SynthModuleB", json["foundModule"]?.ToString());
            Assert.False(json["persisted"]?.ToObject<bool>());
        }

        [Fact]
        public void A_delete_named_by_the_module_of_the_object_is_not_refused_for_its_module()
        {
            var service = new ObjectService(null, null);
            service.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => FoundIn("SynthModuleA"));

            string json = service.DeleteObject("Procedure:SynthModuleA.SynthProc", "Procedure", confirm: true);

            Assert.DoesNotContain("ObjectModuleMismatch", json);
        }

        [Fact]
        public void A_bare_name_delete_is_not_checked_against_a_module()
        {
            var service = new ObjectService(null, null);
            service.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => FoundIn("SynthModuleB"));

            string json = service.DeleteObject("Procedure:SynthProc", "Procedure", confirm: true);

            Assert.DoesNotContain("ObjectModuleMismatch", json);
        }

        [Fact]
        public void The_batch_delete_counts_a_refused_object_of_another_module_as_failed()
        {
            var objects = new ObjectService(null, null);
            objects.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => FoundIn("SynthModuleB"));
            var service = new ObjectTextService(objects, IndexHolding(ProcInA));

            JObject response = JObject.Parse(service.Execute("delete_kb_objects", null,
                new JObject { ["name"] = "SynthModuleA.SynthProc", ["confirm"] = true }, CancellationToken.None));

            Assert.Equal(1, (int)response["result"]!["failed"]!);
            Assert.Equal(0, (int)response["result"]!["succeeded"]!);
            Assert.Equal("ObjectModuleMismatch", (string)response.SelectToken("$..results[0].response.error.code"));
        }

        // The root module is no module: an object the index lists under it is deleted by its bare name.
        [Fact]
        public void An_object_listed_under_the_root_module_is_deleted_by_its_bare_name()
        {
            var index = IndexHolding(Procedure("SynthRootProc2", "Root Module", "SynthRootProc2Dir", "50000000-0000-4000-8000-000000000011"));

            Assert.Equal(new[] { "Procedure:SynthRootProc2" }, CapturedDeleteTargets(index, "SynthRootProc2"));
        }

        // The manifest the legacy export writes carries no module for its entries, so a name qualified by a module cannot be
        // checked against it. The name matches the entries by their last segment, as it did before the module check.
        [Fact]
        public void A_qualified_name_selects_the_manifest_entries_it_names_as_before()
        {
            string root = NewManifestDirectory(("SynthProc", "a.gxtext"), ("SynthProc", "b.gxtext"));
            try
            {
                var service = new ObjectTextService(null, IndexHolding(ProcInB));

                JObject response = JObject.Parse(service.Execute("validate_kb_text_files", null,
                    new JObject { ["inputPath"] = root, ["name"] = "SynthModuleA.SynthProc" }, CancellationToken.None));

                Assert.Equal(new[] { "a.gxtext", "b.gxtext" }, ValidatedFiles(response));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void A_bare_name_selects_every_manifest_entry_with_that_name()
        {
            string root = NewManifestDirectory(("SynthProc", "a.gxtext"), ("SynthProc", "b.gxtext"));
            try
            {
                var service = new ObjectTextService(null, IndexHolding(ProcInB));

                JObject response = JObject.Parse(service.Execute("validate_kb_text_files", null,
                    new JObject { ["inputPath"] = root, ["name"] = "SynthProc" }, CancellationToken.None));

                Assert.Equal(new[] { "a.gxtext", "b.gxtext" }, ValidatedFiles(response));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // A manifest of the legacy shape: one Procedure entry per file, with no module.
        private static string NewManifestDirectory(params (string Name, string File)[] entries)
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var items = new JArray();
            foreach (var entry in entries)
            {
                File.WriteAllText(Path.Combine(root, entry.File), "// synthetic text\r\n");
                items.Add(new JObject { ["name"] = entry.Name, ["type"] = "Procedure", ["part"] = "Source", ["file"] = entry.File });
            }
            File.WriteAllText(Path.Combine(root, "_gxmcp-object-text-manifest.json"), new JObject
            {
                ["kind"] = "GeneXusObjectText",
                ["schemaVersion"] = 1,
                ["part"] = "Source",
                ["objects"] = items
            }.ToString());
            return root;
        }

        private static List<string> ValidatedFiles(JObject response)
        {
            return response.SelectTokens("$.result.results[*].file").Select(token => (string)token).ToList();
        }

        private static ObjectService.DeleteResolution FoundIn(string module)
        {
            return new ObjectService.DeleteResolution
            {
                Found = true,
                Name = "SynthProc",
                TypeName = "Procedure",
                Guid = Guid.NewGuid(),
                ModuleName = module
            };
        }

        private static List<string> SelectedPaths(IndexCacheService index, string name)
        {
            var selection = new TextTreeSelectionService(index);
            bool selected = selection.TrySelectEntries(null, new JObject { ["name"] = name }, allowAll: false,
                out List<SearchIndex.IndexEntry> entries, out string error);

            Assert.True(selected, error);
            return entries.Select(entry => entry.Path).ToList();
        }

        // The delete asks the resolution for the target it would name. A resolution that finds nothing ends the call there,
        // so no KB object is needed.
        private static List<string> CapturedDeleteTargets(IndexCacheService index, string name)
        {
            var targets = new List<string>();
            var objects = new ObjectService(null, null);
            objects.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) =>
            {
                targets.Add(target);
                return new ObjectService.DeleteResolution();
            });
            var service = new ObjectTextService(objects, index);

            service.Execute("delete_kb_objects", null, new JObject { ["name"] = name, ["dryRun"] = true }, CancellationToken.None);
            return targets;
        }

        private static IndexCacheService IndexHolding(params SearchIndex.IndexEntry[] entries)
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries);
            cache.MarkIndexComplete(entries.Length);
            return cache;
        }

        private static SearchIndex.IndexEntry Procedure(string name, string module, string parentPath, string guid)
        {
            return new SearchIndex.IndexEntry
            {
                Type = "Procedure",
                Name = name,
                Module = module,
                Parent = parentPath,
                ParentPath = parentPath,
                ParentFolderPath = parentPath == null ? null : "Root Module/" + parentPath,
                Path = parentPath == null ? name : parentPath + "/" + name,
                Guid = guid,
                EntityKey = guid + "-9001"
            };
        }
    }
}
