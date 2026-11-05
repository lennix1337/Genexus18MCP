using GxMcp.Common.SharedHost;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // RFC #854, Requirement 3 criterion 6: the shared-host broker answers a request it cannot
    // start with a Busy_Envelope tool result. The Gateway trims error envelopes to a terse
    // allowlist by default, so these tests pin that the fields the agent needs to wait and
    // retry (retryAfterMs, dispatchState, the KB identity and the queue state) reach the client,
    // and that an Isolated_Mode WorkerBusy envelope keeps its existing terse shape.
    public class SharedHostBusyEnvelopeTerseTests
    {
        // The result object of SharedWorkerHostProtocol.BuildBusyResult (the Worker-side builder
        // is not compiled into the Gateway, so the shape is rebuilt here from the same catalog).
        private static JObject SharedBusyResult(string reason, JObject details) => new JObject
        {
            ["status"] = "error",
            ["code"] = ConcurrencyErrorCatalog.WorkerBusy,
            ["error"] = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.WorkerBusy, reason, new ConcurrencyErrorFields
            {
                QueueWaitMs = 30000,
                RetryAfterMs = 4000,
                Details = details
            })
        };

        [Fact]
        public void TerseProjection_KeepsTheRetryContractAndQueueStateOfASharedBusyEnvelope()
        {
            var input = SharedBusyResult("queue-wait-expired", new JObject
            {
                ["kbIdentityKey"] = "kb-identity-a",
                ["kbPath"] = @"C:\KBs\KbA",
                ["runningOperation"] = "Build",
                ["runningElapsedMs"] = 12000,
                ["queuePosition"] = 2
            });

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("WorkerBusy", (string?)trimmed["code"]);
            Assert.Equal("queue-wait-expired", (string?)trimmed["reason"]);
            Assert.True((bool)trimmed["retryable"]!);
            Assert.Equal(4000, (int)trimmed["retryAfterMs"]!);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string?)trimmed["dispatchState"]);
            Assert.Equal("kb-identity-a", (string?)trimmed["kbIdentityKey"]);
            Assert.Equal(@"C:\KBs\KbA", (string?)trimmed["kbPath"]);
            Assert.Equal("Build", (string?)trimmed["runningOperation"]);
            Assert.Equal(12000, (long)trimmed["runningElapsedMs"]!);
            Assert.Equal(2, (int)trimmed["queuePosition"]!);
        }

        [Theory]
        [InlineData("queue-full", "queueDepth", 64)]
        [InlineData("session-queue-limit", "sessionQueueCap", 8)]
        public void TerseProjection_KeepsTheQueueBoundOfTheReason(string reason, string key, int value)
        {
            var input = SharedBusyResult(reason, new JObject { ["kbIdentityKey"] = "kb-identity-a", [key] = value });

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal(value, (int)trimmed[key]!);
            Assert.NotNull(trimmed["retryAfterMs"]);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string?)trimmed["dispatchState"]);
        }

        [Fact]
        public void TerseProjection_LeavesTheIsolatedWorkerBusyEnvelopeUnchanged()
        {
            // The Isolated_Mode Worker's own WorkerBusy (Program.cs queue-full refusal) carries no
            // dispatchState, so it keeps the terse shape it had before RFC #854.
            var input = JObject.Parse(@"{status:'error',error:{code:'WorkerBusy',message:'The SDK command queue is full; the request was not started.',
                hint:'Retry after the active SDK work yields or cancel the running operation.',retryAfterMs:250,queue:'SDK command queue',capacity:64}}");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("WorkerBusy", (string?)trimmed["code"]);
            Assert.Null(trimmed["retryAfterMs"]);
            Assert.Null(trimmed["dispatchState"]);
            Assert.Null(trimmed["queue"]);
            Assert.Null(trimmed["capacity"]);
        }
    }
}
