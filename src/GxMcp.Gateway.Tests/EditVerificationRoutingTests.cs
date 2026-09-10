using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class EditVerificationRoutingTests
    {
        [Theory]
        [InlineData(true, true, "strict")]
        [InlineData(true, false, "strict")]
        [InlineData(true, false, "only")]
        [InlineData(false, false, "strict")]
        [InlineData(null, false, "strict")]
        public void EventsPatch_ForwardsCompleteSaveWithoutLosingSafetyArguments(bool? required, bool dryRun, string validate)
        {
            var args = new JObject
            {
                ["name"] = "SyntheticPanel", ["part"] = "Events", ["mode"] = "patch",
                ["operation"] = "Replace", ["context"] = "old", ["content"] = "new",
                ["baseVersion"] = "35", ["dryRun"] = dryRun, ["validate"] = validate,
                ["rollbackOnFailure"] = false
            };
            if (required.HasValue) args["requireObjectSave"] = required.Value;

            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args));

            Assert.Equal("Patch", routed["module"]?.ToString());
            Assert.Equal(required ?? false, routed["requireObjectSave"]?.Value<bool>());
            Assert.Equal("35", routed["baseVersion"]?.ToString());
            Assert.Equal(dryRun, routed["dryRun"]?.Value<bool>());
            Assert.Equal(validate, routed["validate"]?.ToString());
            Assert.False(routed["rollbackOnFailure"]?.Value<bool>());
        }

        [Fact]
        public void LegacyPatch_ForwardsRequireObjectSave()
        {
            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_patch",
                JObject.Parse("{name:'SyntheticPanel',part:'Events',requireObjectSave:true,dryRun:true,baseVersion:'35'}")));
            Assert.True(routed["requireObjectSave"]?.Value<bool>());
            Assert.True(routed["dryRun"]?.Value<bool>());
            Assert.Equal("35", routed["baseVersion"]?.ToString());
        }


        [Fact]
        public void Patch_ForwardsVerificationConcurrencyAndExplicitRollback()
        {
            var args = new JObject
            {
                ["name"] = "SyntheticProcedure",
                ["type"] = "Procedure",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["operation"] = "Replace",
                ["context"] = "old",
                ["content"] = "new",
                ["verifyMode"] = "normalized",
                ["baseVersion"] = "version-token",
                ["rollbackOnFailure"] = true
            };

            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args));
            Assert.Equal("normalized", routed["verifyMode"]?.ToString());
            Assert.Equal("version-token", routed["baseVersion"]?.ToString());
            Assert.True(routed["rollbackOnFailure"]?.Value<bool>());
        }

        [Fact]
        public void Patch_ForwardsVerificationAndPersistenceContractThroughWorkerEnvelope()
        {
            var args = new JObject
            {
                ["name"] = "SyntheticWebPanel",
                ["type"] = "WebPanel",
                ["part"] = "Events",
                ["mode"] = "patch",
                ["operation"] = "Append",
                ["content"] = "// live marker",
                ["context"] = "// anchor",
                ["expectedCount"] = 2,
                ["dryRun"] = false,
                ["verifyRollback"] = true,
                ["return_post_state"] = false,
                ["verbose"] = true,
                ["validate"] = "best-effort",
                ["replaceAll"] = true,
                ["visualVerify"] = true,
                ["validationMode"] = "specify",
                ["rollbackOnFailure"] = true,
                ["verifyMode"] = "exact",
                ["baseVersion"] = "version-token",
                ["autoDeclareVariables"] = true,
                ["requireObjectSave"] = true
            };

            var request = new JObject
            {
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_edit",
                    ["arguments"] = args
                }
            };

            var routed = JObject.FromObject(McpRouter.ConvertToolCall(request)!);
            Assert.True(routed["requireObjectSave"]?.Value<bool>());

            var workerRpc = Program.BuildWorkerRpcRequest(routed, "request-id");
            var workerParams = workerRpc["params"] as JObject;
            Assert.NotNull(workerParams);
            foreach (var field in new[]
            {
                "target", "part", "operation", "context", "expectedCount", "dryRun",
                "verifyRollback", "return_post_state", "verbose", "validate", "replaceAll",
                "visualVerify", "validationMode", "rollbackOnFailure", "verifyMode",
                "baseVersion", "autoDeclareVariables", "requireObjectSave"
            })
            {
                Assert.True(
                    JToken.DeepEquals(routed[field], workerParams![field]),
                    $"Patch field '{field}' was not preserved through the Worker envelope.");
            }
            Assert.Equal("// live marker", workerParams!["payload"]?.ToString());
        }

        [Fact]
        public void Patch_ForwardsAutoDeclareVariables()
        {
            var args = new JObject
            {
                ["name"] = "SyntheticProcedure",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["operation"] = "Replace",
                ["context"] = "old",
                ["content"] = "new &MyVar",
                ["autoDeclareVariables"] = true
            };

            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args)!);
            Assert.True(routed["autoDeclareVariables"]?.Value<bool>());
        }

        [Fact]
        public void FullWrite_ForwardsAutoDeclareVariables()
        {
            var args = new JObject
            {
                ["name"] = "SyntheticProcedure",
                ["part"] = "Source",
                ["mode"] = "full",
                ["content"] = "new &MyVar = 1",
                ["autoDeclareVariables"] = true
            };

            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args)!);
            Assert.True(routed["autoDeclareVariables"]?.Value<bool>());
        }
    }
}
