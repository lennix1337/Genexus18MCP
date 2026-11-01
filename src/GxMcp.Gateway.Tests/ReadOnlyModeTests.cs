using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// GXMCP_READ_ONLY: a probe session that is supposed to only look at a KB must not be able to
    /// write to it, whatever the caller believes about the call. The gate fails closed: only calls
    /// the classifier proves read-only, and effect-free dryRun previews, get through.
    /// </summary>
    public class ReadOnlyModeTests
    {
        private static readonly ReadOnlyMode On = ReadOnlyMode.Parse("1");

        [Theory]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData(" YES ", true)]
        [InlineData("0", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Switch_IsOnlyOnForAnExplicitTrueValue(string? raw, bool enabled)
        {
            Assert.Equal(enabled, ReadOnlyMode.Parse(raw).IsEnabled);
        }

        [Theory]
        [InlineData("genexus_edit", "{\"name\":\"Trn1\",\"part\":\"Source\",\"content\":\"x\"}")]
        [InlineData("genexus_create", "{\"type\":\"WebPanel\",\"name\":\"CapTest1\"}")]
        [InlineData("genexus_delete_object", "{\"name\":\"Live770Probe\",\"confirm\":true}")]
        [InlineData("genexus_edit", "{\"name\":\"Trn1\",\"dryRun\":true,\"changeSet\":{\"action\":\"apply\"}}")]
        [InlineData("genexus_edit", "{\"name\":\"Trn1\",\"dryRun\":\"maybe\"}")]
        [InlineData("genexus_tool_nobody_classified", "{}")]
        public void On_RefusesWritesAndUnknownTools(string tool, string args)
        {
            Assert.True(On.Refuses(tool, JObject.Parse(args)));
        }

        [Theory]
        [InlineData("genexus_read", "{\"name\":\"Trn1\",\"part\":\"Source\"}")]
        [InlineData("genexus_query", "{\"query\":\"Trn1\"}")]
        [InlineData("genexus_list_objects", "{}")]
        [InlineData("genexus_inspect", "{\"name\":\"Trn1\"}")]
        [InlineData("genexus_analyze", "{\"name\":\"Trn1\"}")]
        [InlineData("genexus_doctor", "{}")]
        [InlineData("genexus_whoami", "{}")]
        [InlineData("genexus_search_source", "{\"query\":\"x\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"status\"}")]
        [InlineData("genexus_kb", "{\"action\":\"list\"}")]
        [InlineData("genexus_properties", "{\"action\":\"get\",\"name\":\"Trn1\"}")]
        [InlineData("genexus_edit", "{\"name\":\"Trn1\",\"part\":\"Source\",\"content\":\"x\",\"dryRun\":true}")]
        public void On_LetsProvenReadsAndEffectFreePreviewsThrough(string tool, string args)
        {
            Assert.False(On.Refuses(tool, JObject.Parse(args)));
        }

        [Fact]
        public void Off_RefusesNothing_SoTheDefaultBehaviourIsUnchanged()
        {
            var off = ReadOnlyMode.Parse(null);
            Assert.False(off.Refuses("genexus_delete_object", JObject.Parse("{\"name\":\"X\",\"confirm\":true}")));
        }

        [Fact]
        public async Task ToolsCall_InReadOnlyMode_IsRefusedByTheGatewayBeforeAnyWorker()
        {
            ReadOnlyMode.Override.Value = On;
            try
            {
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "read-only",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_delete_object",
                        ["arguments"] = JObject.Parse("{\"name\":\"Live770Probe\",\"confirm\":true}")
                    }
                };
                var response = await Program.ProcessMcpRequest(request);
                var payload = JObject.Parse(response!.SelectToken("result.content[0].text")!.ToString());
                Assert.Equal("WriteBlockedReadOnlyMode", payload.SelectToken("$..code")?.ToString());
                Assert.True(response.SelectToken("result.isError")?.Value<bool>());
            }
            finally
            {
                ReadOnlyMode.Override.Value = null;
            }
        }

        [Fact]
        public void Refusal_StatesNothingWasWritten()
        {
            var envelope = ReadOnlyMode.BuildRefusal("genexus_edit");
            Assert.Equal("WriteBlockedReadOnlyMode", envelope["code"]?.ToString());
            Assert.False(envelope["wrote"]!.Value<bool>());
            Assert.False(envelope["persisted"]!.Value<bool>());
            Assert.Contains("GXMCP_READ_ONLY", envelope["message"]!.ToString());
        }
    }
}
