using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #957: a KB the Worker could not open must reach the MCP client as KbOpenFailed
    // (with the SDK message) instead of an IndexNotReady that can never clear.
    public class KbOpenFailureReportingTests
    {
        private static JObject WorkerState(JToken? kbOpenFailure) => new JObject
        {
            ["indexStatus"] = "Cold",
            ["freshness"] = "stale",
            ["totalObjects"] = 0,
            ["kbOpenFailure"] = kbOpenFailure
        };

        private static JObject InvalidDirectoryFailure() => new JObject
        {
            ["code"] = "KbOpenFailed",
            ["message"] = "InvalidDirectory",
            ["path"] = "C:/KBs/junction",
            ["atUtc"] = "2026-11-10T12:00:00.0000000Z"
        };

        [Fact]
        public void Worker_open_failure_becomes_a_KbOpenFailed_envelope_with_the_sdk_message()
        {
            Program.ResetIndexStateMirrorForTest();
            Assert.True(Program.ApplyIndexStateFromWorkerResult(WorkerState(InvalidDirectoryFailure()), "kb-a"));

            JObject? envelope = Program.BuildKbOpenFailedEnvelopeForTest("kb-a");

            Assert.NotNull(envelope);
            Assert.Equal("error", envelope!["status"]?.ToString());
            Assert.Equal("KbOpenFailed", envelope["code"]?.ToString());
            Assert.Equal("InvalidDirectory", envelope["message"]?.ToString());
            Assert.Equal("C:/KBs/junction", envelope["path"]?.ToString());
            Assert.Equal("2026-11-10T12:00:00.0000000Z", envelope["failedAtUtc"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(envelope["hint"]?.ToString()));
        }

        [Fact]
        public void No_recorded_failure_leaves_the_gate_to_IndexNotReady()
        {
            Program.ResetIndexStateMirrorForTest();
            Assert.True(Program.ApplyIndexStateFromWorkerResult(WorkerState(JValue.CreateNull()), "kb-a"));

            Assert.Null(Program.BuildKbOpenFailedEnvelopeForTest("kb-a"));
        }

        [Fact]
        public void A_later_state_without_failure_clears_it()
        {
            Program.ResetIndexStateMirrorForTest();
            Program.ApplyIndexStateFromWorkerResult(WorkerState(InvalidDirectoryFailure()), "kb-a");
            Program.ApplyIndexStateFromWorkerResult(WorkerState(JValue.CreateNull()), "kb-a");

            Assert.Null(Program.BuildKbOpenFailedEnvelopeForTest("kb-a"));
        }

        [Fact]
        public void The_failure_is_kept_per_kb_and_shown_in_the_whoami_index_block()
        {
            Program.ResetIndexStateMirrorForTest();
            Program.ApplyIndexStateFromWorkerResult(WorkerState(InvalidDirectoryFailure()), "kb-a");
            Program.ApplyIndexStateFromWorkerResult(WorkerState(JValue.CreateNull()), "kb-b");

            Assert.Equal("InvalidDirectory", Program.BuildIndexBlockForTest("kb-a")["kbOpenFailure"]?["message"]?.ToString());
            Assert.Equal(JTokenType.Null, Program.BuildIndexBlockForTest("kb-b")["kbOpenFailure"]?.Type);
            Assert.Null(Program.BuildKbOpenFailedEnvelopeForTest("kb-b"));
        }
    }
}
