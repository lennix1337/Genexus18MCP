using Newtonsoft.Json.Linq;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class MutationRecoveryRegistryTests
    {
        [Fact]
        public void TimedOutWrite_BlocksAnotherWriteUntilSamePartIsRead()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead("kb-one", "SyntheticProcedure", "Source", "operation-one");

            Assert.True(registry.TryGet("KB-ONE", "syntheticprocedure", out var requirement));
            JObject blocked = MutationRecoveryRegistry.BuildBlockedEnvelope(requirement);
            Assert.Equal("error", blocked["status"]?.ToString());
            Assert.Equal("PostTimeoutReadRequired", blocked["error"]?["code"]?.ToString());
            Assert.False(blocked["error"]?["retryable"]?.ToObject<bool>());
            Assert.True(blocked["error"]?["reconciliationRequired"]?.ToObject<bool>());
            Assert.Equal("genexus_read", blocked["error"]?["nextSteps"]?[0]?["tool"]?.ToString());
            Assert.False(registry.ConfirmRead("kb-one", "SyntheticProcedure", "Rules"));
            Assert.True(registry.TryGet("kb-one", "SyntheticProcedure", out _));

            Assert.True(registry.ConfirmRead("kb-one", "SyntheticProcedure", "Source"));
            Assert.False(registry.TryGet("kb-one", "SyntheticProcedure", out _));
        }

        [Fact]
        public void TimedOutPropertyBatch_RequiresAuthoritativeTypedReadOfEveryProperty()
        {
            var registry = new MutationRecoveryRegistry();
            var write = new JObject
            {
                ["action"] = "set", ["name"] = "SamplePanel", ["type"] = "WebPanel",
                ["properties"] = new JObject
                {
                    ["MainProgram"] = "True", ["MasterPage"] = "", ["IntegratedSecurityLevel"] = "SecurityNone"
                }
            };
            var timeout = new JObject { ["error"] = new JObject { ["retryable"] = true } };
            Assert.True(Program.RegisterTimeoutRecoveryFence(registry, "kb", "genexus_properties",
                write, "op-1", timeout));
            Assert.False(timeout["error"]?["retryable"]?.Value<bool>());
            Assert.True(timeout["error"]?["reconciliationRequired"]?.Value<bool>());
            Assert.True(registry.TryGet("kb", "SamplePanel", out var requirement));
            var blocked = MutationRecoveryRegistry.BuildBlockedEnvelope(requirement);
            Assert.Equal("PostTimeoutReadRequired", blocked["error"]?["code"]?.ToString());
            Assert.Equal("genexus_properties", blocked["error"]?["nextSteps"]?[0]?["tool"]?.ToString());
            Assert.Equal(3, blocked["error"]?["nextSteps"]?[0]?["args"]?["propertyNames"]?.Count());

            var readArgs = new JObject
            {
                ["action"] = "get", ["name"] = "SamplePanel", ["type"] = "WebPanel",
                ["reconcileTimedOutWrite"] = true,
                ["propertyNames"] = new JArray("MainProgram", "MasterPage", "IntegratedSecurityLevel")
            };
            var values = new JObject
            {
                ["MainProgram"] = "False", ["MasterPage"] = "SampleMaster", ["IntegratedSecurityLevel"] = "SecurityHigh"
            };
            var response = new JObject
            {
                ["status"] = "ok", ["code"] = "PropertiesRead",
                ["result"] = new JObject
                {
                    ["authoritativeRead"] = true, ["versionToken"] = "v2", ["values"] = values
                }
            };
            Assert.Single(registry.FindForRead("kb", readArgs, "Properties"));
            Assert.True(Program.IsCompletePropertiesRecoveryRead(response, requirement, readArgs));
            ((JObject)response["result"]!["values"]!).Remove("MainProgram");
            Assert.False(Program.IsCompletePropertiesRecoveryRead(response, requirement, readArgs));
            ((JObject)response["result"]!)["values"]!["MainProgram"] = "False";
            ((JObject)response["result"]!)["authoritativeRead"] = false;
            Assert.False(Program.IsCompletePropertiesRecoveryRead(response, requirement, readArgs));
            ((JObject)response["result"]!)["authoritativeRead"] = true;
            Assert.True(Program.IsCompletePropertiesRecoveryRead(response, requirement, readArgs));
            readArgs["control"] = "PanelControl";
            Assert.False(Program.IsCompletePropertiesRecoveryRead(response, requirement, readArgs));
            readArgs.Remove("control");
            Assert.True(registry.ConfirmRead("kb", requirement.Target, requirement.Part, requirement));
            Assert.False(registry.TryGet("kb", "SamplePanel", out _));

            // A fence written by an older Gateway has no property-name list.
            // Only an unfiltered full properties read can reconcile it.
            registry.RequireRead("kb", "LegacyPanel", "Properties", "op-old");
            Assert.True(registry.TryGet("kb", "LegacyPanel", out var legacy));
            Assert.False(Program.IsCompletePropertiesRecoveryRead(response, legacy, readArgs));
            var fullRead = new JObject { ["action"] = "get", ["name"] = "LegacyPanel",
                ["reconcileTimedOutWrite"] = true };
            Assert.True(Program.IsCompletePropertiesRecoveryRead(response, legacy, fullRead));
        }
    }
}
