using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    public class AnalyzeRouterLinterTests
    {
        [Fact]
        public void LinterFixDryRun_IsForwardedToWorkerParameters()
        {
            var command = new AnalyzeRouter().ConvertToolCall(
                "genexus_analyze",
                new JObject
                {
                    ["name"] = "Panel",
                    ["mode"] = "linter",
                    ["fix"] = true,
                    ["dryRun"] = true
                });

            var json = JObject.FromObject(command!);

            Assert.Equal("Linter", json["module"]?.ToString());
            Assert.Equal("linter", json["action"]?.ToString());
            Assert.True(json["params"]?["fix"]?.Value<bool>());
            Assert.True(json["params"]?["dryRun"]?.Value<bool>());
        }
    }
}
