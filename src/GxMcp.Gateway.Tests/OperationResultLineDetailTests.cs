using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A lifecycle result hands the worker's payload back. The line detail of a rollback receipt (changedLines) is kept for the
    /// callers that asked for verbose_errors and left out for the others, since the lines may carry source of the object.
    /// </summary>
    public class OperationResultLineDetailTests
    {
        private static JObject CompletedWith(JObject toolArguments)
        {
            var tracker = new OperationTracker(System.TimeSpan.FromMinutes(5));
            string operationId = tracker.StartOperation("req-line-detail", "genexus_properties", toolArguments, "cid");
            tracker.CompleteFromWorker("req-line-detail", new JObject
            {
                ["status"] = "error",
                ["code"] = "PropertyVerificationFailed",
                ["changedParts"] = new JArray("Events"),
                ["changedLines"] = new JArray(new JObject { ["part"] = "Events", ["side"] = "before", ["line"] = "&Synth = 1" })
            });
            return tracker.BuildOperationResult(operationId);
        }

        [Fact]
        public void AResultWithoutVerboseErrorsLeavesOutTheLineDetail()
        {
            JObject result = CompletedWith(new JObject { ["kb"] = "e1" });

            Assert.Null(result["workerPayload"]!["changedLines"]);
            Assert.Equal("Events", (string)result["workerPayload"]!["changedParts"]![0]!);
        }

        [Fact]
        public void AResultWithVerboseErrorsKeepsTheLineDetail()
        {
            JObject result = CompletedWith(new JObject { ["kb"] = "e1", ["verbose_errors"] = true });

            Assert.Equal("&Synth = 1", (string)result["workerPayload"]!["changedLines"]![0]!["line"]!);
        }
    }
}
