using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class PropertiesRouterBatchTests
    {
        [Fact]
        public void PropertiesRecoveryAndSafeSet_ForwardTypedArguments()
        {
            var router = new GxMcp.Gateway.Routers.PropertiesRouter();
            var read = JObject.FromObject(router.ConvertToolCall("genexus_properties", new JObject
            {
                ["action"] = "get", ["name"] = "SamplePanel", ["type"] = "WebPanel",
                ["propertyNames"] = new JArray("MainProgram", "MasterPage", "IntegratedSecurityLevel"),
                ["reconcileTimedOutWrite"] = true
            })!);
            Assert.True(read["reconcileTimedOutWrite"]?.Value<bool>());
            Assert.Equal(3, ((JArray)read["propertyNames"]!).Count);

            var write = JObject.FromObject(router.ConvertToolCall("genexus_properties", new JObject
            {
                ["action"] = "set", ["name"] = "SamplePanel", ["type"] = "WebPanel",
                ["properties"] = new JObject { ["MasterPage"] = "" },
                ["expectedVersion"] = "v2", ["dryRun"] = true
            })!);
            Assert.Equal("v2", write["expectedVersion"]?.ToString());
            Assert.True(write["dryRun"]?.Value<bool>());
            Assert.True(write["rollbackOnFailure"]?.Value<bool>());
        }

        [Fact]
        public void PropertiesGet_ForwardsBatchTargetsThroughWorkerEnvelope()
        {
            var request = new JObject
            {
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_properties",
                    ["arguments"] = JObject.Parse(@"{
  ""action"": ""get"",
  ""targets"": [
    { ""name"": ""Customer"", ""type"": ""Transaction"" },
    { ""name"": ""Missing"", ""type"": ""Transaction"" }
  ],
  ""projection"": ""minimal""
}")
                }
            };

            var message = McpRouter.ConvertToolCall(request);

            Assert.NotNull(message);
            var routed = JObject.FromObject(message!);
            Assert.Equal("Property", routed["module"]?.ToString());
            Assert.Equal("Get", routed["action"]?.ToString());
            var targets = routed["targets"] as JArray;
            Assert.NotNull(targets);
            Assert.Equal(2, targets!.Count);
            Assert.Equal("Customer", targets[0]?["name"]?.ToString());
            Assert.Equal("Transaction", targets[0]?["type"]?.ToString());
            Assert.Equal("Missing", targets[1]?["name"]?.ToString());

            var workerRpc = Program.BuildWorkerRpcRequest(routed, "properties-batch-test");
            var workerParams = workerRpc["params"] as JObject;
            Assert.NotNull(workerParams);
            Assert.True(JToken.DeepEquals(targets, workerParams!["targets"]));
        }
    }
}
