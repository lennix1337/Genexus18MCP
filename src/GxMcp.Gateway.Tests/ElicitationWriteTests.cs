using System.Collections.Concurrent;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed partial class ElicitationTests
    {
        private static WorkerProcess CreateInMemoryWriteSink(
            Configuration config, KbHandle handle, ConcurrentQueue<JObject> writes)
        {
            var worker = new WorkerProcess(config, handle);
            worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
            worker.FakeWorkerForTest = rpc =>
            {
                var result = new JObject();
                if (rpc["method"]?.ToString() == "Object" && rpc["action"]?.ToString() == "Delete")
                {
                    writes.Enqueue((JObject)rpc.DeepClone());
                    result["deleted"] = true;
                    result["correlationId"] = "elicitation-write-test";
                }
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"], ["result"] = result };
            };
            worker.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}");
            return worker;
        }

        [Theory]
        [InlineData("accept", null, 1, false)]
        [InlineData("decline", "HUMAN_DECLINED", 0, false)]
        [InlineData("cancel", "HUMAN_CANCELLED", 0, false)]
        [InlineData("cancel", "HUMAN_CANCELLED", 0, true)]
        [InlineData("unsupported-action", "HUMAN_CONFIRMATION_UNAVAILABLE", 0, false)]
        public async Task DestructiveWrite_DispatchesOnlyAfterHumanApproval(string action, string? errorCode, int writes, bool strict)
        {
            int callsWhenAsked = -1;
            ElicitationFixture? probe = null;
            using var fixture = ElicitationFixture.Create(_ =>
            {
                callsWhenAsked = probe!.Writes.Count;
                return new JObject { ["action"] = action, ["content"] = new JObject { ["confirm"] = true } };
            }, fakeWrites: true);
            probe = fixture;
            if (strict) ElicitationBroker.ModeOverrideForTest = ElicitationMode.Strict;

            // The agent already asked to execute; its flag is not a human decision.
            var response = await fixture.CallToolAsync("genexus_delete_object", new JObject
            {
                ["name"] = "OldPanel", ["type"] = "WebPanel", ["kb"] = "customer", ["confirm"] = true
            });

            Assert.Equal("elicitation/create", Assert.Single(fixture.Elicitations)["method"]?.ToString());
            Assert.Equal(0, callsWhenAsked);
            Assert.Equal(writes, fixture.Writes.Count);
            Assert.Equal(errorCode, Program.ExtractErrorCode(response));
            if (errorCode != null)
            {
                Assert.Contains("nothing was changed", response.ToString(Formatting.None));
            }
            else
            {
                Assert.False(response["result"]?["isError"]?.Value<bool>());
                Assert.True(Assert.Single(fixture.Writes)["params"]?["confirm"]?.Value<bool>());
            }
        }

        [Fact]
        public async Task DestructiveWrite_WithoutCapability_MatchesElicitationOffByteForByte()
        {
            using var fixture = ElicitationFixture.Create(_ => new JObject { ["action"] = "decline" },
                clientSupportsElicitation: false, fakeWrites: true);
            var call = ElicitationFixture.ToolCall("genexus_delete_object", new JObject
            {
                ["name"] = "OldPanel", ["type"] = "WebPanel", ["kb"] = "customer", ["confirm"] = true
            });

            var response = await Program.ProcessMcpRequest(call, fixture.Session);
            ElicitationBroker.ObserveInitialize(JObject.Parse("{\"params\":{\"capabilities\":{\"elicitation\":{}}}}"), fixture.Session);
            ElicitationBroker.ModeOverrideForTest = ElicitationMode.Off;
            var baseline = await Program.ProcessMcpRequest((JObject)call.DeepClone(), fixture.Session);

            Assert.Empty(fixture.Elicitations);
            Assert.True(response?["result"]?["isError"]?.Value<bool>() == false, response?.ToString());
            Assert.Equal(2, fixture.Writes.Count);
            Assert.Equal(baseline!.ToString(Formatting.None), response!.ToString(Formatting.None));
        }

        [Theory]
        [InlineData(false, "genexus_edit", "{\"name\":\"OldPanel\",\"part\":\"Source\",\"content\":\"// replacement\"}")]
        [InlineData(false, "genexus_io", "{\"action\":\"import_part\",\"name\":\"OldPanel\",\"inputPath\":\"replacement.txt\"}")]
        [InlineData(false, "genexus_lifecycle", "{\"action\":\"reorg\"}")]
        [InlineData(true, "genexus_edit", "{\"name\":\"OldPanel\",\"part\":\"Source\",\"content\":\"// replacement\"}")]
        [InlineData(true, "genexus_io", "{\"action\":\"import_part\",\"name\":\"OldPanel\",\"inputPath\":\"replacement.txt\"}")]
        [InlineData(true, "genexus_lifecycle", "{\"action\":\"reorg\"}")]
        public async Task DestructiveWrite_WithoutElicitationForwardsOriginalRequest(
            bool clientSupportsElicitation, string tool, string arguments)
        {
            using var fixture = ElicitationFixture.Create(answer: null, clientSupportsElicitation: clientSupportsElicitation);
            if (clientSupportsElicitation) ElicitationBroker.ModeOverrideForTest = ElicitationMode.Off;
            var call = ElicitationFixture.ToolCall(tool, JObject.Parse(arguments));
            string original = call.ToString(Formatting.None);

            var (forwarded, refusal) = await Program.ConfirmDestructiveCallAsync(call, fixture.Session, default);

            Assert.Null(refusal);
            Assert.Same(call, forwarded);
            Assert.Equal(original, forwarded.ToString(Formatting.None));
            Assert.Empty(fixture.Elicitations);
        }

        [Fact]
        public async Task DestructiveWrite_OnlyItsOwnSessionCanApprove()
        {
            using var fixture = ElicitationFixture.Create(answer: null, fakeWrites: true);
            var pending = fixture.CallToolAsync("genexus_delete_object", new JObject
            {
                ["name"] = "OldPanel", ["type"] = "WebPanel", ["kb"] = "customer", ["confirm"] = true
            });
            var reply = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Assert.Single(fixture.Elicitations)["id"],
                ["result"] = new JObject { ["action"] = "accept", ["content"] = new JObject { ["confirm"] = true } }
            };

            await Program.ProcessMcpRequest(reply, fixture.Session + "-other");
            reply["result"] = new JObject { ["action"] = "decline" };
            await Program.ProcessMcpRequest(reply, fixture.Session);

            Assert.Equal(Program.HumanDeclinedCode, Program.ExtractErrorCode(await pending));
            Assert.Empty(fixture.Writes);
        }

        [Theory]
        [InlineData("genexus_lifecycle", "{\"action\":\"reorg\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"build_all\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"rebuild\"}")]
        [InlineData("genexus_build", "{\"action\":\"BuildAll\"}")]
        [InlineData("genexus_io", "{\"action\":\"delete_kb_objects\",\"name\":\"OldPanel\",\"confirm\":true}")]
        [InlineData("genexus_io", "{\"action\":\"import_part\",\"name\":\"OldPanel\",\"inputPath\":\"replacement.txt\"}")]
        [InlineData("genexus_io", "{\"action\":\"import_text_to_kb\",\"inputPath\":\"batch\"}")]
        [InlineData("genexus_io", "{\"action\":\"import_text_to_kb\",\"format\":\"native\",\"dryRun\":false}")]
        [InlineData("genexus_edit", "{\"name\":\"OldPanel\",\"part\":\"Source\",\"mode\":\"full\",\"content\":\"// replacement\"}")]
        [InlineData("genexus_edit", "{\"targets\":[{\"name\":\"OldPanel\",\"part\":\"Source\",\"content\":\"// replacement\"}]}")]
        public async Task OtherDestructiveWrites_DeclineStopsBeforeDispatch(string tool, string arguments)
        {
            using var fixture = ElicitationFixture.Create(_ => new JObject { ["action"] = "decline" });
            var response = await fixture.CallToolAsync(tool, JObject.Parse(arguments));

            Assert.Equal("elicitation/create", Assert.Single(fixture.Elicitations)["method"]?.ToString());
            Assert.Equal(Program.HumanDeclinedCode, Program.ExtractErrorCode(response));
        }
    }
}
