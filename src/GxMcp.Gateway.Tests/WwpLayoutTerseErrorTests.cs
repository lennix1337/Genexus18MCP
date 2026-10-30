using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // genexus_wwp add_layout: a refused write that the rollback restored must still say, in the terse error,
    // why it was refused. The verbose receipts stay out of it.
    public class WwpLayoutTerseErrorTests
    {
        [Fact]
        public void RolledBackFailureKeepsItsCauseInTheTerseProjection()
        {
            // The envelope the Worker builds when the verification before the commit refused the write and the
            // rollback restored the baseline: the cause is the failed check, and the rollback evidence beside it.
            var input = JObject.Parse(@"{
                ""status"":""error"",
                ""target"":""SamplePanel"",
                ""error"":{""code"":""WwpLayoutNotVerified"",
                    ""message"":""The native save did not preserve and project the requested state; transaction aborted (failed checks: projection (existingControlOrder)).""},
                ""savePhase"":""pre-commit-verification"",
                ""nativeOperation"":{""stage"":""verify""},
                ""failedChecks"":[""projection""],
                ""failureStage"":""existingControlOrder"",
                ""commitCompleted"":false,
                ""rollbackPerformed"":true,
                ""stateRestoredExactly"":true,
                ""rollbackVerified"":true,
                ""restorationVerification"":{""host"":{""equal"":true},""parent"":{""equal"":true}},
                ""boundaryVerification"":{""identityConfirmed"":true,""host"":{""equal"":true,""persistedHash"":""h1""},""parent"":{""equal"":true,""persistedHash"":""h2""}},
                ""preCommitVerification"":{""confirmed"":false,""projection"":{""confirmed"":false,""failureStage"":""existingControlOrder""}},
                ""attemptedStateSnapshots"":{""webForm"":""snapshot-path"",""events"":""snapshot-path""}
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (string key in new[] { "savePhase", "nativeOperation", "failedChecks", "failureStage", "commitCompleted",
                "rollbackPerformed", "stateRestoredExactly", "rollbackVerified", "restorationVerification" })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
            Assert.Equal("WwpLayoutNotVerified", (string)trimmed["code"]!);
            // The first line of the message is what the terse projection keeps, so the cause is in it.
            Assert.Contains("existingControlOrder", (string)trimmed["message"]!);
            // The terse error stays small: the verbose receipts are not projected.
            foreach (string key in new[] { "boundaryVerification", "preCommitVerification", "attemptedStateSnapshots" })
                Assert.Null(trimmed[key]);
        }

        [Fact]
        public void TransactionEndAndVerificationReadErrorsSurviveTheTerseProjection()
        {
            // Both come from the receipt of a write whose transaction ended badly or whose re-read failed; the
            // terse projection is the only place the caller reads them.
            var input = JObject.Parse(@"{
                ""status"":""error"",
                ""error"":{""code"":""WwpLayoutNotVerified"",""message"":""Post-save verification failed.""},
                ""commitCompleted"":true,
                ""transactionFinalizationError"":""disposal failed"",
                ""verificationError"":""re-read failed"",
                ""stateRestoredExactly"":false
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (string key in new[] { "commitCompleted", "transactionFinalizationError", "verificationError", "stateRestoredExactly" })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
        }

        [Theory]
        [InlineData("WwpLayoutNotVerified")]
        [InlineData("WwpLayoutSaveFailed")]
        [InlineData("WwpProjectionFailed")]
        [InlineData("WwpProjectionNotConfirmed")]
        public void EveryAddLayoutFailureFamilyOpensTheReceiptBlock(string code)
        {
            var input = JObject.Parse(@"{
                ""status"":""error"",
                ""error"":{""code"":""" + code + @""",""message"":""Inspect the saved PatternInstance before retrying.""},
                ""commitCompleted"":true,""persistenceState"":""partial"",
                ""snapshot"":""snapshot-path"",""beforeHash"":""h0"",""rollbackPerformed"":false
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (string key in new[] { "commitCompleted", "persistenceState", "snapshot", "beforeHash", "rollbackPerformed" })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
        }

        [Fact]
        public void AnUnrelatedErrorThatReportsPersistenceStateKeepsItsTerseShape()
        {
            // Patch, WebForm and TransactionRecords errors report persistenceState without being a pattern write
            // receipt: their snapshot path and hashes must not travel in the terse error.
            var input = JObject.Parse(@"{
                ""status"":""error"",
                ""error"":{""code"":""WriteNotPersisted"",""message"":""The write was not persisted.""},
                ""persistenceState"":""unchanged"",
                ""snapshot"":""snapshot-path"",""beforeHash"":""h0"",""projection"":{""confirmed"":false}
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (string key in new[] { "persistenceState", "snapshot", "beforeHash", "projection" })
                Assert.Null(trimmed[key]);
        }
    }
}
