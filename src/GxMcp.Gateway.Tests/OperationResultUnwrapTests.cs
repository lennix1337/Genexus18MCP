using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class OperationResultUnwrapTests
    {
        private static JObject Receipt() => new JObject { ["objectSaved"] = true, ["persistencePath"] = "sdk_object_force_save_isolated" };

        [Fact]
        public void LifecycleEnvelope_WithReceiptUnderResult_YieldsTheReceipt()
        {
            var envelope = new JObject { ["status"] = "succeeded", ["operationId"] = "op1", ["result"] = Receipt() };

            Assert.True(EventsBundleNativeProofLiveTests.UnwrapOperationResult(envelope)!.Value<bool>("objectSaved"));
        }

        [Fact]
        public void LifecycleEnvelope_WithReceiptAsJsonText_YieldsTheReceipt()
        {
            var envelope = new JObject { ["status"] = "succeeded", ["result"] = Receipt().ToString() };

            Assert.True(EventsBundleNativeProofLiveTests.UnwrapOperationResult(envelope)!.Value<bool>("objectSaved"));
        }

        [Fact]
        public void WorkerPayloadWrappedTwice_YieldsTheReceipt()
        {
            var envelope = new JObject { ["workerPayload"] = new JObject { ["result"] = Receipt() } };

            Assert.Equal("sdk_object_force_save_isolated",
                EventsBundleNativeProofLiveTests.UnwrapOperationResult(envelope)!.Value<string>("persistencePath"));
        }

        [Fact]
        public void EnvelopeWithoutReceipt_YieldsNothing()
        {
            var envelope = new JObject { ["status"] = "failed", ["result"] = "worker crashed" };

            Assert.Null(EventsBundleNativeProofLiveTests.UnwrapOperationResult(envelope));
        }
    }
}
