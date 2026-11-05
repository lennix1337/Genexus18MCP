using System;
using System.IO;
using System.Linq;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // genexus_lifecycle action=specify_only: the published contract (schema, routing, classification, help)
    // and the Gateway side of the lifecycle task (dispatch, admission identity, worker command, compaction).
    public class SpecifyOnlyContractTests
    {
        private const string Guid = "6f1b1f0e-9a3c-4c52-8d0e-2f7a1c5b9d11";
        private const string OtherGuid = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

        private static JObject LifecycleSchema()
        {
            // tool_definitions.json is copied beside the test binaries by the Gateway project.
            string path = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            Assert.True(File.Exists(path), "tool_definitions.json was not copied beside the test binaries.");
            var tools = JArray.Parse(File.ReadAllText(path));
            return (JObject)tools.Single(t => (string?)t["name"] == "genexus_lifecycle");
        }

        [Fact]
        public void Schema_publishes_the_action_and_its_two_inputs()
        {
            var props = (JObject)LifecycleSchema()["inputSchema"]!["properties"]!;
            var actions = ((JArray)props["action"]!["enum"]!).Select(a => (string?)a).ToList();
            Assert.Contains("specify_only", actions);
            Assert.Equal("string", (string?)props["targetGuid"]?["type"]);
            Assert.Equal("string", (string?)props["expectedVersion"]?["type"]);
            Assert.Contains("specify_only", (string?)props["dryRun"]!["description"]);
            // The call waits for its receipt by default; the schema says how to opt out.
            Assert.Contains("specify_only", (string?)props["wait_until_done"]!["description"]);
        }

        [Fact]
        public void Routes_a_plan_to_the_worker_SpecifyOnly_action_carrying_guid_version_environment_and_dryRun()
        {
            var routed = new SystemRouter().ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "specify_only",
                ["targetGuid"] = Guid,
                ["expectedVersion"] = "639000000000000000:root",
                ["environment"] = "Default",
                ["dryRun"] = true
            });
            Assert.NotNull(routed);

            var cmd = JObject.FromObject(routed!);
            Assert.Equal("Build", (string?)cmd["module"]);
            Assert.Equal("SpecifyOnly", (string?)cmd["action"]);
            Assert.Equal(Guid, (string?)cmd["targetGuid"]);
            Assert.Equal("639000000000000000:root", (string?)cmd["expectedVersion"]);
            Assert.Equal("Default", (string?)cmd["environment"]);
            Assert.True((bool?)cmd["dryRun"]);
            Assert.True((bool?)cmd["queueLifecycle"]);
            // The wait belongs to the Gateway's lifecycle job, not to the Worker call.
            Assert.Null(cmd["wait_seconds"]);
        }

        [Fact]
        public void A_target_that_is_a_guid_still_reaches_the_worker_when_targetGuid_is_omitted()
        {
            var cmd = JObject.FromObject(new SystemRouter().ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "specify_only",
                ["target"] = Guid
            })!);
            Assert.Equal(Guid, (string?)cmd["target"]);
            Assert.Equal(JTokenType.Null, cmd["targetGuid"]!.Type);
            Assert.False((bool?)cmd["dryRun"]);
        }

        [Fact]
        public void Is_a_mutating_action_and_only_its_dryRun_is_a_read_only_preview()
        {
            Assert.False(OperationClassifier.IsReadOnly("genexus_lifecycle",
                new JObject { ["action"] = "specify_only", ["targetGuid"] = Guid }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_lifecycle",
                new JObject { ["action"] = "specify_only", ["targetGuid"] = Guid, ["dryRun"] = true }));
        }

        [Fact]
        public void The_plain_specify_action_is_untouched()
        {
            var cmd = JObject.FromObject(new SystemRouter().ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "specify",
                ["target"] = "SampleProc"
            })!);
            Assert.Equal("Specify", (string?)cmd["action"]);
            Assert.Null(cmd["targetGuid"]);
            Assert.False(OperationClassifier.IsReadOnly("genexus_lifecycle",
                new JObject { ["action"] = "specify", ["target"] = "SampleProc", ["dryRun"] = true }));
        }

        [Fact]
        public void Help_describes_the_guarantees_a_caller_relies_on()
        {
            string help = ToolHelpCatalog.Get("genexus_lifecycle") ?? string.Empty;
            Assert.Contains("`specify_only`", help);
            foreach (string term in new[]
            {
                "targetGuid", "dryRun", "expectedVersion", "SpecifyOnlyVersionConflict", "SpecifyOnlyEnvironmentCopyUnsafe",
                "SpecifyOnlyInProcessDisabled", "copy-to-environment", "stepsNotExecuted", "persisted", "rollback",
                "noGenerationObserved", "generatorOutputTouched", "ConcurrentChange", "Unreachable", "wait_until_done",
                "SpecificationNotObserved", "VersionUnverified", "SpecifyOnlyTypeNotSupported", "rebuildApplied"
            })
                Assert.Contains(term, help);
            // Names the receipt no longer has, and a type the copy is no longer made for.
            Assert.DoesNotContain("noGenerationProven", help);
            Assert.DoesNotContain("generatorFilesTouched", help);
            Assert.DoesNotContain("SDPanel and API", help);
        }

        // ---------- the Gateway side of the lifecycle task ----------

        [Fact]
        public void A_real_run_is_dispatched_as_a_lifecycle_job_and_a_plan_is_not()
        {
            var run = new JObject { ["targetGuid"] = Guid };
            Assert.True(Program.ShouldDispatchLifecycleBuildAsync("genexus_lifecycle", "specify_only", run));
            Assert.True(Program.ShouldDispatchLifecycleBuildAsync("genexus_lifecycle", "SPECIFY_ONLY", run));
            Assert.False(Program.ShouldDispatchLifecycleBuildAsync("genexus_lifecycle", "specify_only",
                new JObject { ["targetGuid"] = Guid, ["dryRun"] = true }));
            Assert.False(Program.ShouldDispatchLifecycleBuildAsync("genexus_edit", "specify_only", run));
        }

        [Fact]
        public void The_worker_command_of_a_job_is_SpecifyOnly_with_the_guid_the_version_and_the_cancel_token()
        {
            var cmd = Program.BuildAsyncLifecycleCommand("specify_only", new JObject
            {
                ["targetGuid"] = Guid,
                ["expectedVersion"] = "639000000000000000:root",
                ["environment"] = "Default"
            }, "job-so-1");

            Assert.Equal("Build", (string?)cmd["module"]);
            Assert.Equal("SpecifyOnly", (string?)cmd["action"]);
            Assert.Equal(Guid, (string?)cmd["targetGuid"]);
            Assert.Equal("639000000000000000:root", (string?)cmd["expectedVersion"]);
            Assert.Equal("Default", (string?)cmd["environment"]);
            Assert.Equal("job-so-1", (string?)cmd["cancelToken"]);
            Assert.True((bool?)cmd["queueLifecycle"]);
            Assert.False((bool?)cmd["dryRun"]);
        }

        [Fact]
        public void Other_actions_do_not_carry_a_guid_or_a_version_to_the_worker()
        {
            var cmd = Program.BuildAsyncLifecycleCommand("specify",
                new JObject { ["target"] = "SampleProc", ["targetGuid"] = Guid, ["expectedVersion"] = "1:root" }, "job-sp-1");
            Assert.Equal("Specify", (string?)cmd["action"]);
            Assert.Null(cmd["targetGuid"]);
            Assert.Null(cmd["expectedVersion"]);
        }

        [Fact]
        public void Requests_for_different_objects_or_versions_are_different_operations()
        {
            string Key(JObject args) => Program.BuildLifecycleRequestKey("specify_only", args);

            string a = Key(new JObject { ["targetGuid"] = Guid });
            Assert.NotEqual(a, Key(new JObject { ["targetGuid"] = OtherGuid }));
            Assert.NotEqual(a, Key(new JObject { ["targetGuid"] = Guid, ["expectedVersion"] = "1:root" }));
            Assert.NotEqual(
                Key(new JObject { ["targetGuid"] = Guid, ["expectedVersion"] = "1:root" }),
                Key(new JObject { ["targetGuid"] = Guid, ["expectedVersion"] = "2:root" }));
        }

        [Fact]
        public void The_same_object_asked_for_twice_is_one_operation_whatever_the_guid_spelling_or_the_wait()
        {
            string Key(JObject args) => Program.BuildLifecycleRequestKey("specify_only", args);

            string canonical = Key(new JObject { ["targetGuid"] = Guid });
            Assert.Equal(canonical, Key(new JObject { ["targetGuid"] = Guid.ToUpperInvariant() }));
            Assert.Equal(canonical, Key(new JObject { ["targetGuid"] = "{" + Guid + "}" }));
            Assert.Equal(canonical, Key(new JObject { ["targetGuid"] = Guid.Replace("-", string.Empty) }));
            Assert.Equal(canonical, Key(new JObject { ["targetGuid"] = "  " + Guid + "  " }));
            Assert.Equal(canonical, Key(new JObject { ["targetGuid"] = Guid, ["wait_seconds"] = 5, ["wait_until_done"] = false }));
        }

        [Fact]
        public void The_admission_identity_of_every_other_action_is_unchanged()
        {
            string key = Program.BuildLifecycleRequestKey("build", new JObject { ["target"] = "SampleProc" });
            Assert.DoesNotContain("targetGuid", key);
            Assert.DoesNotContain("expectedVersion", key);
            // And a plain specify of the same name is not the strict operation.
            Assert.NotEqual(
                Program.BuildLifecycleRequestKey("specify", new JObject { ["target"] = "SampleProc" }),
                Program.BuildLifecycleRequestKey("specify_only", new JObject { ["target"] = "SampleProc" }));
        }

        [Fact]
        public void The_accepted_payload_names_the_action()
        {
            var payload = Program.BuildAsyncLifecycleAcceptedPayload(new JobEntry { Id = "so123", EstimatedSeconds = 30 }, "specify_only");
            Assert.Equal("so123", (string?)payload["operationId"]);
            Assert.Equal("op:so123", (string?)payload["pollTarget"]);
            Assert.Contains("Specify-only accepted;", (string?)payload["hint"]);
        }

        // ---------- what the compact shape keeps ----------

        private static JObject WorkerResult(int diagnostics = 2, int messageLength = 20)
            => new JObject
            {
                ["Status"] = "Failed",
                ["ErrorCount"] = 1,
                ["WarningCount"] = 0,
                ["outcome"] = "SpecificationFailed",
                ["specified"] = false,
                ["specifyOnly"] = new JObject
                {
                    ["operation"] = "specify_only",
                    ["outcome"] = "SpecificationFailed",
                    ["diagnostics"] = new JArray(Enumerable.Range(0, diagnostics)
                        .Select(i => (object)new JObject
                        {
                            ["severity"] = "error",
                            ["code"] = "spc" + i.ToString("D4"),
                            ["message"] = new string('m', messageLength)
                        }).ToArray())
                }
            };

        [Fact]
        public void The_compact_shape_keeps_the_verdict_and_the_receipt()
        {
            var compact = LifecycleResponseShaper.CompactObject(WorkerResult());

            Assert.Equal("SpecificationFailed", (string?)compact["outcome"]);
            Assert.Equal(JTokenType.Boolean, compact["specified"]!.Type);
            Assert.False((bool?)compact["specified"]);
            Assert.Equal("specify_only", (string?)compact["specifyOnly"]!["operation"]);
            Assert.Equal(2, ((JArray)compact["specifyOnly"]!["diagnostics"]!).Count);
            Assert.Null(compact["specifyOnly"]!["diagnosticsOmitted"]);
        }

        [Fact]
        public void The_compact_shape_bounds_the_diagnostics_and_keeps_the_first_ones()
        {
            var compact = LifecycleResponseShaper.CompactObject(WorkerResult(
                diagnostics: LifecycleResponseShaper.SpecifyOnlyDiagnosticCap + 10,
                messageLength: LifecycleResponseShaper.LineCap + 200));

            var receipt = (JObject)compact["specifyOnly"]!;
            var diagnostics = (JArray)receipt["diagnostics"]!;
            Assert.Equal(LifecycleResponseShaper.SpecifyOnlyDiagnosticCap, diagnostics.Count);
            Assert.Equal("spc0000", (string?)diagnostics[0]!["code"]);
            Assert.Equal(LifecycleResponseShaper.SpecifyOnlyDiagnosticCap + 10, (int?)receipt["diagnosticsTotal"]);
            Assert.Equal(10, (int?)receipt["diagnosticsOmitted"]);
            Assert.All(diagnostics, d => Assert.True(((string?)d["message"])!.Length < LifecycleResponseShaper.LineCap + 200));
        }

        [Fact]
        public void Compacting_does_not_change_the_workers_own_receipt()
        {
            var raw = WorkerResult(diagnostics: LifecycleResponseShaper.SpecifyOnlyDiagnosticCap + 5);
            var before = raw.ToString();
            LifecycleResponseShaper.CompactObject(raw);
            Assert.Equal(before, raw.ToString());
        }

        [Fact]
        public void A_task_that_is_not_specify_only_gains_no_specify_only_fields()
        {
            var compact = LifecycleResponseShaper.CompactObject(new JObject { ["Status"] = "Succeeded", ["ErrorCount"] = 0, ["WarningCount"] = 0 });
            Assert.Null(compact["outcome"]);
            Assert.Null(compact["specified"]);
            Assert.Null(compact["specifyOnly"]);
        }

        [Fact]
        public void A_status_envelope_surfaces_the_verdict_at_its_top_level_as_well_as_in_the_result()
        {
            var job = new JobEntry { Id = "soenv1", EstimatedSeconds = 30 };

            var envelope = Program.BuildWorkerLifecycleStatusEnvelope(job, WorkerResult(), new JObject(), 0, "terminal");

            Assert.Equal("SpecificationFailed", (string?)envelope["outcome"]);
            Assert.False((bool?)envelope["specified"]);
            Assert.Equal("SpecificationFailed", (string?)envelope["result"]!["outcome"]);
            Assert.Equal("specify_only", (string?)envelope["result"]!["specifyOnly"]!["operation"]);
        }
    }
}
