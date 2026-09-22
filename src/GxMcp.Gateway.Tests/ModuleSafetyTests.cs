using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class ModuleSafetyTests
    {
        [Theory]
        [InlineData("install")]
        [InlineData("install_builtin")]
        public void Installation_preview_is_read_only_but_application_is_mutating(string action)
        {
            var args = new JObject { ["action"] = action, ["dryRun"] = true };
            Assert.Null(Program.ValidateModulePreview("genexus_module", args));
            Assert.True(OperationClassifier.IsReadOnly("genexus_module", args));
            Assert.False(OperationClassifier.IsMutationCandidate("genexus_module", args));
            args["dryRun"] = false;
            Assert.True(OperationClassifier.IsMutationCandidate("genexus_module", args));
            Assert.False(OperationClassifier.IsReadOnly("genexus_module", args));
        }

        [Theory]
        [InlineData("update")]
        [InlineData("package")]
        [InlineData("publish")]
        [InlineData("restore")]
        [InlineData("add_modules_server")]
        [InlineData("list")]
        [InlineData("unknown")]
        public void Unsupported_preview_is_rejected_instead_of_forwarded(string action)
        {
            var args = new JObject { ["action"] = action, ["dryRun"] = true };
            var error = Program.ValidateModulePreview("genexus_module", args)!["error"]!;
            Assert.Equal("ModuleDryRunUnsupported", (string?)error["code"]);
            Assert.False((bool)error["persisted"]!);
            Assert.Empty((JArray)error["implicitLifecycleOperations"]!);
            args["dryRun"] = false;
            Assert.Null(Program.ValidateModulePreview("genexus_module", args));
        }

        [Fact]
        public void Installation_timeout_receipt_remains_unknown_in_terse_output()
        {
            var payload = JObject.Parse("{status:'error',error:{code:'WorkerTimeout',message:'timed out',retryable:true,reconciliationRequired:false},correlationId:'original'}");
            Program.MarkModuleInstallOutcomeUnknown(payload);
            var error = McpRouter.TrimErrorEnvelope((JObject)payload["error"]!, verbose: false);
            Assert.Equal("ModuleInstallOutcomeUnknown", (string?)error["code"]);
            Assert.Equal(JTokenType.Null, error["persisted"]!.Type);
            Assert.False((bool)error["persistedStateKnown"]!);
            Assert.False((bool)error["verifiedByReadback"]!);
            Assert.False((bool)error["retryable"]!);
            Assert.True((bool)error["reconciliationRequired"]!);
            Assert.Null(error["rolledBack"]);
            Assert.Equal("original", (string?)payload["correlationId"]);
            Assert.Equal(JTokenType.Null, payload["persisted"]!.Type);
            Assert.Contains("Do not repeat", (string?)error["hint"]);
            Assert.Contains("no longer busy", (string?)error["hint"]);
            Assert.DoesNotContain("Poll", (string?)error["hint"]);
        }

        [Fact]
        public void Canonical_worker_failure_preserves_module_receipt_without_debug_details()
        {
            var input = JObject.Parse(@"{status:'error',error:{code:'ModuleInstallFailed',message:'not verified',retryable:false,reconciliationRequired:true},
                module:'Example',version:'1.0',stage:'install',dryRun:false,noMutation:false,persisted:true,persistedStateKnown:true,
                verifiedByReadback:false,partialPersistenceDetected:true,stateChangedSincePlan:true,
                verificationScope:'module identity, version, dependency metadata and object inventory',
                inventory:[{module:'Example',known:true,matches:false,before:{objects:[]},after:{objects:[{guid:'one'}]}}],
                plan:[{module:'Example',version:'1.0',objects:[{guid:'one'}]}],attemptedModules:['Example'],dependencies:[],
                recoveryActions:['Inspect inventory; do not retry'],rollback:{attempted:false,verified:false,supported:false},
                diagnostic:{exceptionType:'ArgumentNullException',parameter:'path',callSites:['Native.Install'],stack:'private stack',debugPath:'private path'},
                implicitLifecycleOperations:[],debugPath:'private path',stack:'private stack'}");
            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in new[] { "module", "version", "stage", "dryRun", "noMutation", "persisted", "persistedStateKnown",
                "verifiedByReadback", "partialPersistenceDetected", "stateChangedSincePlan", "verificationScope", "inventory", "plan",
                "attemptedModules", "dependencies", "recoveryActions", "rollback", "implicitLifecycleOperations" })
                Assert.True(JToken.DeepEquals(input[key], output[key]), key);
            Assert.False((bool)output["retryable"]!);
            Assert.True((bool)output["reconciliationRequired"]!);
            Assert.Equal("path", (string?)output["diagnostic"]?["parameter"]);
            Assert.Equal("Native.Install", (string?)output["diagnostic"]?["callSites"]?[0]);
            Assert.DoesNotContain("private", output.ToString());
        }

        [Fact]
        public void Module_only_receipts_are_not_added_to_unrelated_errors()
        {
            var input = JObject.Parse("{error:{code:'OtherFailure',message:'failure'},inventory:[1],plan:[2],diagnostic:{parameter:'path'}}");
            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Null(output["inventory"]);
            Assert.Null(output["plan"]);
            Assert.Null(output["diagnostic"]);
        }

        [Fact]
        public void Installation_does_not_require_name_and_remains_synchronous()
        {
            var args = new JObject { ["action"] = "install", ["opcFile"] = "package.opc", ["async"] = true };
            Assert.True(Program.IsModuleInstallation("genexus_module", args));
            Assert.False(Program.ShouldRunMutationAsync("genexus_module", args));
            Assert.False(Program.IsModuleInstallation("genexus_edit", args));
        }

        [Theory]
        [InlineData("{error:{code:-32603,message:'Worker for KB crashed/exited.'}}")]
        [InlineData("{error:{code:-32800,message:'Request cancelled by client'}}")]
        [InlineData("{result:{status:'error',error:{code:'WorkerNativeCrashRecovered',message:'Native fault'}}}")]
        [InlineData("{result:{status:'error',error:{code:'Cancelled',message:'cancelled'}}}")]
        public void Interrupted_installation_never_claims_rollback_or_safe_retry(string json)
        {
            var args = new JObject { ["action"] = "install" };
            var response = JObject.Parse(json);
            Assert.False(Program.ShouldRetryWorkerCrash(response, "genexus_module", args, 1));
            Program.NormalizeInterruptedModuleInstallation(response, "genexus_module", args);
            var payload = (JObject)(response["result"] ?? response["error"]!);
            var trimmed = McpRouter.TrimErrorEnvelope(payload, verbose: false);
            Assert.Equal("ModuleInstallOutcomeUnknown", (string?)trimmed["code"]);
            Assert.Equal(JTokenType.Null, trimmed["persisted"]!.Type);
            Assert.False((bool)trimmed["persistedStateKnown"]!);
            Assert.False((bool)trimmed["retryable"]!);
            Assert.True((bool)trimmed["reconciliationRequired"]!);
            Assert.Null(trimmed["rolledBack"]);
        }

        [Fact]
        public void Preview_crash_can_retry_and_known_conflicts_keep_their_receipt()
        {
            var args = new JObject { ["action"] = "install_builtin", ["dryRun"] = true };
            var response = JObject.Parse("{error:{code:-32603,message:'Worker crashed/exited.'}}");
            var original = response.DeepClone();
            Assert.True(Program.ShouldRetryWorkerCrash(response, "genexus_module", args, 1));
            Program.NormalizeInterruptedModuleInstallation(response, "genexus_module", args);
            Assert.True(JToken.DeepEquals(original, response));
            args["dryRun"] = false;
            response = JObject.Parse("{result:{status:'error',error:{code:'ModuleInstallConflict'},persisted:false,noMutation:true}}");
            original = response.DeepClone();
            Program.NormalizeInterruptedModuleInstallation(response, "genexus_module", args);
            Assert.True(JToken.DeepEquals(original, response));
        }

        [Theory]
        [InlineData("install")]
        [InlineData("install_builtin")]
        public void Published_retry_policy_requires_inventory_reconciliation(string action)
        {
            var args = new JObject { ["action"] = action };
            Assert.Equal("reconcile_inventory", OperationClassifier.Describe("genexus_module", args).Retry);
            args["dryRun"] = true;
            Assert.Equal("safe", OperationClassifier.Describe("genexus_module", args).Retry);
        }
    }
}
