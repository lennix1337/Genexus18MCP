using System;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A property write that fails after its batch committed reports <c>committed</c>, <c>persisted</c> (a boolean) and
    /// <c>persistenceStateKnown</c> in the error envelope. The terse projection keeps all three. A tracked operation keeps the
    /// envelope the worker wrote, nested under its <c>result</c>, which is the shape <c>action=result</c> returns.
    /// </summary>
    public class PropertyWriteReceiptProjectionTests
    {
        private static JObject FailedAfterCommit(bool persisted, bool stateKnown)
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
                ["persisted"] = persisted,
                ["persistenceStateKnown"] = stateKnown
            };
        }

        [Fact]
        public void TerseProjectionKeepsCommittedPersistedAndTheFlagOfAnUnknownWrite()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(FailedAfterCommit(persisted: false, stateKnown: false), verbose: false);

            Assert.True((bool)trimmed["committed"]!);
            Assert.False((bool)trimmed["persisted"]!);
            Assert.False((bool)trimmed["persistenceStateKnown"]!);
            Assert.Equal("PropertyVerificationFailed", (string)trimmed["code"]!);
        }

        [Fact]
        public void ATimeoutOfAMutationSaysItsPersistenceIsNotKnown()
        {
            // The timeout envelope keeps persisted null (the #656 contract) and says the state is not known.
            var timeout = new JObject { ["status"] = "error" };

            Program.MarkMutationOutcomeUnknown(timeout);

            Assert.Equal(JTokenType.Null, timeout["persisted"].Type);
            Assert.False((bool)timeout["persistenceStateKnown"]!);
        }

        [Fact]
        public void TerseProjectionKeepsAKnownNotPersistedState()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(FailedAfterCommit(persisted: false, stateKnown: true), verbose: false);

            Assert.False((bool)trimmed["persisted"]!);
            Assert.True((bool)trimmed["persistenceStateKnown"]!);
        }

        [Fact]
        public void TheLifecycleResultOfAFailedPropertyWriteCarriesTheReceiptAsTheWorkerWroteIt()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string opId = tracker.StartOperation("prop-receipt-1", "genexus_properties", new JObject { ["action"] = "set" }, "cid-prop-1");

            // The worker answers with its JSON-RPC envelope: the receipt sits under result, not at the top of the message.
            tracker.CompleteFromWorker("prop-receipt-1", new JObject
            {
                ["id"] = 7,
                ["result"] = FailedAfterCommit(persisted: true, stateKnown: true)
            });
            var result = tracker.BuildOperationResult(opId);

            Assert.Equal("Failed", (string)result["status"]!);
            var receipt = (JObject)((JObject)result["workerPayload"]!)["result"]!;
            Assert.True((bool)receipt["committed"]!);
            Assert.True((bool)receipt["persisted"]!);
            Assert.True((bool)receipt["persistenceStateKnown"]!);
        }
    }
}
