using System;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A property write that failed after its batch committed reports <c>committed</c> and <c>persisted</c> at the top of its
    /// envelope. The terse projection keeps both, and a tracked operation keeps them in the result it stores, so
    /// <c>action=result</c> shows whether the values landed.
    /// </summary>
    public class PropertyWriteReceiptProjectionTests
    {
        private static JObject FailedAfterCommit(JToken persisted)
        {
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "PropertyVerificationFailed",
                    ["message"] = "The saved property could not be read back."
                },
                ["committed"] = true,
                ["persisted"] = persisted
            };
        }

        [Fact]
        public void TerseProjectionKeepsCommittedAndTheUnknownPersistedValue()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(FailedAfterCommit("unknown"), verbose: false);

            Assert.True((bool)trimmed["committed"]!);
            Assert.Equal("unknown", (string)trimmed["persisted"]!);
            Assert.Equal("PropertyVerificationFailed", (string)trimmed["code"]!);
        }

        [Fact]
        public void TerseProjectionKeepsFalseWhenTheRollbackWasVerified()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(FailedAfterCommit(false), verbose: false);

            Assert.True((bool)trimmed["committed"]!);
            Assert.False((bool)trimmed["persisted"]!);
        }

        [Fact]
        public void TheLifecycleResultOfAFailedPropertyWriteCarriesTheReceiptAsTheWorkerWroteIt()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string opId = tracker.StartOperation("prop-receipt-1", "genexus_properties", new JObject { ["action"] = "set" }, "cid-prop-1");

            tracker.CompleteFromWorker("prop-receipt-1", FailedAfterCommit("unknown"));
            var result = tracker.BuildOperationResult(opId);

            Assert.Equal("Failed", (string)result["status"]!);
            var worker = (JObject)result["workerPayload"]!;
            Assert.True((bool)worker["committed"]!);
            Assert.Equal("unknown", (string)worker["persisted"]!);
        }
    }
}
