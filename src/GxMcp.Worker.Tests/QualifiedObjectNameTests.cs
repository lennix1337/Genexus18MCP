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

        // An ignore entry spares the object it names by its last segment, as it did before the module check. Written with
        // the folder the object sits in, or with an outdated module, it still spares the object: sparing more is the safe side.
        [Fact]
        public void An_ignore_entry_written_with_a_folder_still_spares_the_object_it_names()
        {
            var index = IndexHolding(
                Rooted("Folder", "Procs", "SynthModuleA", "SynthModuleA"),
                Rooted("Procedure", "SynthProcDel", "SynthModuleA", "SynthModuleA/Procs"),
                Rooted("Procedure", "SynthProcKeep", "SynthModuleA", "SynthModuleA/Procs"));

            List<string> targets = CapturedBatchTargets(index, new JObject
            {
                ["name"] = "SynthModuleA.Procs",
                ["ignore"] = new JArray("Procs.SynthProcKeep")
            });

            Assert.Equal(new[] { "Procedure:SynthModuleA.SynthProcDel" }, targets);
        }

        [Fact]
        public void An_ignore_entry_written_with_the_outer_module_still_spares_an_object_of_the_inner_module()
        {
            var index = IndexHolding(
                Rooted("Module", "SynthModuleA", null, ""),
                Rooted("Module", "SynthInner", "SynthInner", "SynthModuleA"),
                Rooted("Procedure", "SynthProcDel", "SynthModuleA", "SynthModuleA"),
                Rooted("Procedure", "SynthProcKeep2", "SynthInner", "SynthModuleA/SynthInner"));

            List<string> targets = CapturedBatchTargets(index, new JObject
            {
                ["name"] = "SynthModuleA",
                ["ignore"] = new JArray("SynthModuleA.SynthProcKeep2")
            });

            Assert.Equal(new[] { "Procedure:SynthModuleA.SynthProcDel" }, targets);
        }

        [Fact]
        public void An_ignore_entry_with_an_outdated_module_still_spares_the_object()
        {
            var index = IndexHolding(Rooted("Procedure", "SynthProcKeep", "SynthModuleA", "SynthModuleA"));

            List<string> targets = CapturedBatchTargets(index, new JObject
            {
                ["name"] = "SynthModuleA.SynthProcKeep",
                ["ignore"] = new JArray("SynthModuleB.SynthProcKeep")
            });

            Assert.Empty(targets);
        }

        // The legacy export reads each selected object by the name the batch delete uses: the module qualifies it.
        [Fact]
        public void The_legacy_export_names_the_selected_object_by_its_module()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-export-target-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var service = new ObjectTextService(new ObjectService(null, null), IndexHolding(ProcInB));

                JObject response = JObject.Parse(service.Execute("export_kb_to_text", null,
                    new JObject { ["name"] = "SynthModuleB.SynthProc", ["outputPath"] = root }, CancellationToken.None));

                Assert.Equal("Procedure:SynthModuleB.SynthProc", (string)response.SelectToken("$..results[0].response.target"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // A folder or module named with its qualifier expands to the objects under that folder or module only. A bare name
        // expands by name, as it did before: the folders of every module with that name.
        [Fact]
        public void A_qualified_folder_expands_to_the_objects_under_that_folder_only()
        {
            var index = IndexHolding(
                Rooted("Folder", "Procs", "SynthModuleA", "SynthModuleA"),
                Rooted("Folder", "Procs", "SynthModuleB", "SynthModuleB"),
                Rooted("Procedure", "SynthProcA", "SynthModuleA", "SynthModuleA/Procs"),
                Rooted("Procedure", "SynthProcB", "SynthModuleB", "SynthModuleB/Procs"));

            List<string> targets = CapturedBatchTargets(index, new JObject { ["name"] = "SynthModuleA.Procs" });

            Assert.Equal(new[] { "Procedure:SynthModuleA.SynthProcA" }, targets);
        }

        [Fact]
        public void A_qualified_nested_module_expands_to_the_objects_under_that_module_only()
        {
            var index = IndexHolding(
                Rooted("Module", "SynthInner", "SynthInner", "SynthModuleA"),
                Rooted("Module", "SynthInner", "SynthInner", "SynthModuleB"),
                Rooted("Procedure", "SynthNestedA", "SynthInner", "SynthModuleA/SynthInner"),
                Rooted("Procedure", "SynthNestedB", "SynthInner", "SynthModuleB/SynthInner"));

            List<string> targets = CapturedBatchTargets(index, new JObject { ["name"] = "SynthModuleA.SynthInner" });

            Assert.Equal(new[] { "Procedure:SynthInner.SynthNestedA" }, targets);
        }

        [Fact]
        public void A_bare_folder_name_still_expands_by_name_to_the_folders_of_every_module()
        {
            var index = IndexHolding(
                Rooted("Folder", "Procs", "SynthModuleA", "SynthModuleA"),
                Rooted("Folder", "Procs", "SynthModuleB", "SynthModuleB"),
                Rooted("Procedure", "SynthProcA", "SynthModuleA", "SynthModuleA/Procs"),
                Rooted("Procedure", "SynthProcB", "SynthModuleB", "SynthModuleB/Procs"));

            List<string> targets = CapturedBatchTargets(index, new JObject { ["name"] = "Procs" });

            Assert.Equal(new[] { "Procedure:SynthModuleA.SynthProcA", "Procedure:SynthModuleB.SynthProcB" }, targets);
        }

        // The manifest selector keeps the last segment: an ignore entry naming the module removes every entry of that name.
        [Fact]
        public void An_ignore_entry_naming_a_module_removes_the_manifest_entries_of_that_name_in_a_listing()
        {
            string root = NewManifestDirectory(("SynthProc", "a.gxtext"), ("SynthProc", "b.gxtext"));
            try
            {
                var service = new ObjectTextService(null, IndexHolding(ProcInB));

                JObject response = JObject.Parse(service.Execute("validate_kb_text_files", null,
                    new JObject { ["inputPath"] = root, ["ignore"] = new JArray("SynthModuleA.SynthProc"), ["listOnly"] = true },
                    CancellationToken.None));

                Assert.Equal("NoObjectsMatched", (string)response["error"]?["code"]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // The Gateway projects this message and hint; the Worker answers them here, so a change to either breaks a test.
        [Fact]
        public void The_module_mismatch_envelope_carries_the_message_and_hint_the_gateway_projects()
        {
            JObject json = JObject.Parse(ObjectService.DeleteModuleMismatchResponse("SynthModuleA.SynthProc", "SynthProc", "SynthModuleB"));

            Assert.Equal("The name 'SynthModuleA.SynthProc' names a module that does not hold the object found. Nothing was deleted.",
                json["error"]?["message"]?.ToString());
            Assert.Equal("Name the object with the module that holds it, or with its bare name when that name is unique in the KB.",
                json["error"]?["hint"]?.ToString());
        }

        // The index keeps the root module in the path; a name is written without it, so the root is never a qualifier.
        [Fact]
        public void A_folder_path_qualifier_selects_an_object_of_the_index_with_the_root_module_in_its_path()
        {
            var index = IndexHolding(Rooted("Procedure", "SynthReal", "SynthModuleA", "SynthModuleA/Procs"));

            Assert.Equal(new[] { "Root Module/SynthModuleA/Procs/SynthReal" }, SelectedPaths(index, "SynthModuleA.Procs.SynthReal"));
        }

        [Fact]
        public void A_qualifier_that_names_the_root_module_selects_nothing()
        {
            var index = IndexHolding(Rooted("Procedure", "SynthReal", "SynthModuleA", "SynthModuleA/Procs"));

            Assert.Empty(SelectedPaths(index, "Root Module.SynthModuleA.SynthReal"));
            Assert.Empty(SelectedPaths(index, "Root Module.SynthReal"));
        }

        // The guard compares the qualifier without regard to case, and checks only a two-part Module.Name.
        [Fact]
        public void The_guard_compares_the_module_without_regard_to_case()
        {
            Assert.True(ObjectService.QualifierNamesModule("Procedure:synthmodulea.SynthProc", "SynthModuleA"));
            Assert.False(ObjectService.QualifierNamesModule("Procedure:synthmodulea.SynthProc", "SynthModuleB"));
        }

        [Fact]
        public void The_guard_does_not_check_a_name_with_three_segments()
        {
            Assert.True(ObjectService.QualifierNamesModule("Procedure:SynthModuleA.Procs.SynthProc", "SynthModuleB"));
        }

        // The batch chose an object with a GUID; a resolution that gives no GUID is not that object, so it is refused.
        [Fact]
        public void The_batch_delete_refuses_a_resolved_object_without_a_guid_when_the_chosen_entry_has_one()
        {
            var objects = new ObjectService(null, null);
            objects.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => FoundIn("SynthModuleA", guid: Guid.Empty));
            var service = new ObjectTextService(objects, IndexHolding(ProcInA));

            JObject response = JObject.Parse(service.Execute("delete_kb_objects", null,
                new JObject { ["name"] = "SynthModuleA.SynthProc", ["confirm"] = true }, CancellationToken.None));

            Assert.Equal(1, (int)response["result"]!["failed"]!);
            Assert.Equal("ObjectModuleMismatch", (string)response.SelectToken("$..results[0].response.error.code"));
        }

        [Fact]
        public void The_chosen_object_check_refuses_a_resolution_without_a_guid()
        {
            Assert.False(ObjectService.IsChosenObject("50000000-0000-4000-8000-00000000000a", Guid.Empty));
        }

        // Without a valid expected GUID there is nothing to compare: the check is off.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-guid")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public void The_chosen_object_check_is_off_without_a_valid_expected_guid(string expectedGuid)
        {
            Assert.True(ObjectService.IsChosenObject(expectedGuid, Guid.Empty));
            Assert.True(ObjectService.IsChosenObject(expectedGuid, Guid.NewGuid()));
        }

        // A folder named with its qualifier and no path holds nothing: the name rule would reach the homonym folders of other modules.
        [Fact]
        public void A_qualified_container_without_a_path_expands_to_nothing()
        {
            var folderWithoutPath = new SearchIndex.IndexEntry
            {
                Type = "Folder",
                Name = "Procs",
                Module = "SynthModuleA",
                Guid = Guid.NewGuid().ToString("D"),
                EntityKey = Guid.NewGuid().ToString("D") + "-9001"
            };
            var index = IndexHolding(folderWithoutPath, Rooted("Procedure", "SynthProcA", "SynthModuleA", "SynthModuleA/Procs"));

            List<string> targets = CapturedBatchTargets(index, new JObject { ["name"] = "SynthModuleA.Procs" });

            Assert.Empty(targets);
        }

        // The module refusal and the GUID refusal share the code; their messages tell the two cases apart.
        [Fact]
        public void The_module_refusal_and_the_guid_refusal_have_different_messages_and_hints()
        {
            string byModule = (string)JObject.Parse(ObjectService.DeleteModuleMismatchResponse("SynthModuleA.SynthProc", "SynthProc", "SynthModuleB"))["error"]?["message"];
            JObject byGuid = JObject.Parse(ObjectService.DeleteChosenObjectMismatchResponse("SynthModuleA.SynthProc", "SynthProc", "SynthModuleA"));
            JObject byGuidBare = JObject.Parse(ObjectService.DeleteChosenObjectMismatchResponse("SynthProc", "SynthProc", "SynthModuleB"));
            string guidMessage = (string)byGuid["error"]?["message"];

            Assert.Equal("ObjectModuleMismatch", (string)byGuid["error"]?["code"]);
            Assert.Contains("GUID", guidMessage);
            Assert.Contains("module 'SynthModuleA'", guidMessage);
            Assert.DoesNotContain("does not hold", guidMessage);
            Assert.Contains("genexus_lifecycle action=index", (string)byGuid["error"]?["hint"]);
            Assert.Contains("GUID", (string)byGuidBare["error"]?["message"]);
            Assert.DoesNotContain("module '", (string)byGuidBare["error"]?["message"]);
            Assert.DoesNotContain("GUID", byModule);
            Assert.NotEqual(byModule, guidMessage);
        }

        // The batch delete's targets for an index and a name, with the resolution as the only thing that answers.
        private static List<string> CapturedBatchTargets(IndexCacheService index, JObject args)
        {
            var targets = new List<string>();
            var objects = new ObjectService(null, null);
            objects.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) =>
            {
                targets.Add(target);
                return new ObjectService.DeleteResolution();
            });
            var service = new ObjectTextService(objects, index);
            args["dryRun"] = true;

            service.Execute("delete_kb_objects", null, args, CancellationToken.None);
            return targets;
        }

        // An entry the index keeps with its path under the root module, as the production index does.
        private static SearchIndex.IndexEntry Rooted(string type, string name, string module, string containerPath)
        {
            string path = string.IsNullOrEmpty(containerPath) ? "Root Module/" + name : "Root Module/" + containerPath + "/" + name;
            return new SearchIndex.IndexEntry
            {
                Type = type,
                Name = name,
                Module = module,
                Parent = containerPath,
                ParentPath = containerPath,
                ParentFolderPath = string.IsNullOrEmpty(containerPath) ? "Root Module" : "Root Module/" + containerPath,
                Path = path,
                Guid = Guid.NewGuid().ToString("D"),
                EntityKey = Guid.NewGuid().ToString("D") + "-9001"
            };
        }

        // The object sits in a folder of the root module, and the name is that folder's path: the folder names the object.
        [Fact]
        public void A_delete_named_by_a_folder_that_holds_the_object_is_not_refused()
        {
            var service = new ObjectService(null, null);
            service.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => FoundIn("Root Module", new[] { "SynthFolder" }));

            string json = service.DeleteObject("Procedure:SynthFolder.SynthProc", "Procedure", confirm: true);

            Assert.DoesNotContain("ObjectModuleMismatch", json);
        }

        [Fact]
        public void A_delete_named_by_a_name_that_is_neither_the_module_nor_a_holding_folder_is_refused()
        {
            var service = new ObjectService(null, null);
            service.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => FoundIn("SynthModuleB", new[] { "SynthModuleB", "SynthFolder" }));

            var json = JObject.Parse(service.DeleteObject("Procedure:SynthOtherFolder.SynthProc", "Procedure", confirm: true));

            Assert.Equal("ObjectModuleMismatch", json["error"]?["code"]?.ToString());
        }

        // The batch chose one object by its module and GUID. A resolution that finds another object under that name is refused.
        [Fact]
        public void The_batch_delete_refuses_an_object_whose_guid_is_not_the_chosen_entry()
        {
            var objects = new ObjectService(null, null);
            objects.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) =>
                FoundIn("SynthModuleA", guid: Guid.Parse("50000000-0000-4000-8000-0000000000ff")));
            var service = new ObjectTextService(objects, IndexHolding(ProcInA));

            JObject response = JObject.Parse(service.Execute("delete_kb_objects", null,
                new JObject { ["name"] = "SynthModuleA.SynthProc", ["confirm"] = true }, CancellationToken.None));

            Assert.Equal(1, (int)response["result"]!["failed"]!);
            Assert.Equal("ObjectModuleMismatch", (string)response.SelectToken("$..results[0].response.error.code"));
        }

        [Fact]
        public void The_batch_delete_does_not_refuse_the_object_whose_guid_is_the_chosen_entry()
        {
            var objects = new ObjectService(null, null);
            objects.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) =>
                FoundIn("SynthModuleA", guid: Guid.Parse("50000000-0000-4000-8000-00000000000a")));
            var service = new ObjectTextService(objects, IndexHolding(ProcInA));

            string json = service.Execute("delete_kb_objects", null,
                new JObject { ["name"] = "SynthModuleA.SynthProc", ["confirm"] = true }, CancellationToken.None);

            Assert.DoesNotContain("ObjectModuleMismatch", json);
        }

        // A module is its own module; the module that holds it is its parent. A root module has no holder.
        [Fact]
        public void A_module_is_named_by_the_module_that_holds_it()
        {
            Guid module = Guid.NewGuid();

            Assert.Equal("SynthModuleA", ObjectService.HolderModuleName("SynthInner", module, module, "SynthModuleA"));
            Assert.Null(ObjectService.HolderModuleName("SynthRoot", module, module, null));
        }

        [Fact]
        public void An_object_is_named_by_its_own_module_not_by_a_module_it_is_not()
        {
            Assert.Equal("SynthModuleA", ObjectService.HolderModuleName("SynthModuleA", Guid.NewGuid(), Guid.NewGuid(), "SynthModuleZ"));
        }

        [Fact]
        public void A_delete_named_by_a_nested_module_and_its_holder_is_not_refused()
        {
            var service = new ObjectService(null, null);
            service.SetDeleteTargetResolverForTest((target, typeFilter, dryRun, confirm) => new ObjectService.DeleteResolution
            {
                Found = true,
                Name = "SynthInner",
                TypeName = "Module",
                Guid = Guid.NewGuid(),
                ModuleName = "SynthModuleA"
            });

            string json = service.DeleteObject("Module:SynthModuleA.SynthInner", "Module", confirm: true);

            Assert.DoesNotContain("ObjectModuleMismatch", json);
        }

        private static ObjectService.DeleteResolution FoundIn(string module, IEnumerable<string> qualifiers = null, Guid? guid = null)
        {
            return new ObjectService.DeleteResolution
            {
                Found = true,
                Name = "SynthProc",
                TypeName = "Procedure",
                Guid = guid ?? Guid.NewGuid(),
                ModuleName = module,
                Qualifiers = qualifiers
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
            // The production key: a folder or module is keyed by its path, so two of the same name in other modules are both kept.
            var cache = new IndexCacheService();
            cache.AddOrUpdateBatch(entries);
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
