using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class VisualWriteCommitEvidenceTests
    {
        [Fact]
        public void Receipt_CommittedVisualMismatch_ReportsPhysicalCommitSeparately()
        {
            var response = JObject.Parse(
                "{\"status\":\"error\",\"code\":\"WriteFailed\",\"sdkSaveCompleted\":true,\"commitState\":\"Committed\"}");

            var receipt = WriteService.ApplyTextVerificationReceipt(
                response,
                "LegacyPanel",
                "WebForm",
                "before",
                "requested",
                "persisted-but-damaged",
                "v1",
                false,
                null,
                "exact");

            Assert.Equal("Committed", receipt["commitState"]?.ToString());
            Assert.True(receipt["sdkSaveCompleted"]?.Value<bool>());
            Assert.True(receipt["saved"]?.Value<bool>());
            Assert.True(receipt["persisted"]?.Value<bool>());
            Assert.False(receipt["verified"]?.Value<bool>());
            Assert.False(receipt["persistedVerified"]?.Value<bool>());
        }

        [Fact]
        public void VisualRestoreNextStep_UsesVersioningUmbrellaAndValidNamePartArgs()
        {
            var response = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "WriteFailed" }
            };

            WriteService.AddVersioningRestoreStep(response, "LegacyPanel", "WebForm");

            var step = Assert.Single((JArray)response["error"] ["nextSteps"]);
            Assert.Equal("genexus_versioning", step["tool"]?.ToString());
            Assert.Equal("history_restore", step["args"]?["action"]?.ToString());
            Assert.Equal("LegacyPanel", step["args"]?["name"]?.ToString());
            Assert.Equal("WebForm", step["args"]?["part"]?.ToString());
            Assert.True(step["args"]?["discard"]?.Value<bool>());
            Assert.NotNull(response["nextSteps"]);
            Assert.DoesNotContain("genexus_history", response.ToString());
        }

        [Fact]
        public void UnavailableRollback_DoesNotClaimRestoreOrUndoPhysicalCommit()
        {
            var response = new JObject
            {
                ["status"] = "error",
                ["commitState"] = "Committed",
                ["sdkSaveCompleted"] = true
            };

            WriteService.MarkVisualRollbackUnavailable(
                response, "No atomic version fence was available.");

            Assert.False(response["rollback"] ["rolledBack"]?.Value<bool>());
            Assert.False(response["rollback"] ["verified"]?.Value<bool>());
            Assert.False(response["rollback"] ["attempted"]?.Value<bool>());
            Assert.True(response["persisted"]?.Value<bool>());
            Assert.Equal("Committed", response["commitState"]?.ToString());
        }
    }
}
