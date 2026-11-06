using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LiveGatewayHarnessDiagnosticsTests
    {
        private static JObject ToolResponse(JObject payload) => new JObject
        {
            ["result"] = new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = payload.ToString() })
            }
        };

        [Theory]
        [InlineData("result")]
        [InlineData("kb")]
        [InlineData("sessionSelection")]
        public void PayloadDiagnostics_AcceptAPlainValueWhereOtherToolsReturnAnObject(string field)
        {
            // Without GXMCP_TEST_KB the harness spawns nothing; with it set, this unit check is not the place to run.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GXMCP_TEST_KB"))) return;
            using var harness = new LiveGatewayHarness();

            // genexus_lifecycle status answers `result` as a string; reading a child of it must not throw.
            harness.RecordPayloadDiagnostics(ToolResponse(new JObject { [field] = "running" }));
        }
    }
}
