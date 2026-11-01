using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // genexus_apply_pattern validate=true: the verdict is ok only for a validation build that ended Succeeded with no
    // itemized error. The validation block names the build's end state, so the caller can tell why it failed.
    public class ApplyPatchValidationTests
    {
        private static JObject TerminalWithStatus(string? status)
            => status == null ? new JObject() : new JObject { ["Status"] = status };

        private static JObject VerdictOf(JObject terminal, int errorCount = 0)
        {
            var block = new JObject();
            ApplyPatchValidation.WriteVerdict(block, terminal, errorCount);
            return block;
        }

        [Theory]
        [InlineData("Succeeded")]
        [InlineData("succeeded")]
        public void SucceededWithoutErrors_IsOk(string status)
        {
            var block = VerdictOf(TerminalWithStatus(status));

            Assert.Equal("ok", (string?)block["status"]);
            Assert.Equal(status, (string?)block["buildStatus"]);
        }

        [Theory]
        [InlineData("Failed")]
        [InlineData("Cancelled")]
        [InlineData("Error")]
        [InlineData("ReorgRequired")]
        public void AnyOtherEndState_IsFailedAndNamed(string status)
        {
            var block = VerdictOf(TerminalWithStatus(status));

            Assert.Equal("failed", (string?)block["status"]);
            Assert.Equal(status, (string?)block["buildStatus"]);
        }

        [Fact]
        public void SucceededWithItemizedErrors_IsFailed()
        {
            var block = VerdictOf(TerminalWithStatus("Succeeded"), errorCount: 2);

            Assert.Equal("failed", (string?)block["status"]);
        }

        [Fact]
        public void SucceededWithAnErrorObject_IsFailed()
        {
            var terminal = TerminalWithStatus("Succeeded");
            terminal["error"] = "build error";

            Assert.Equal("failed", (string?)VerdictOf(terminal)["status"]);
        }

        [Fact]
        public void NoEndState_IsFailedWithoutInventingOne()
        {
            var block = VerdictOf(TerminalWithStatus(null));

            Assert.Equal("failed", (string?)block["status"]);
            Assert.Null(block["buildStatus"]);
        }

        [Fact]
        public void LowercaseStatusKey_IsReadToo()
        {
            var terminal = new JObject { ["status"] = "Succeeded" };

            Assert.Equal("ok", (string?)VerdictOf(terminal)["status"]);
        }

        [Fact]
        public void ReorgRequiredStatus_IsMarkedInTheBlock()
        {
            var block = VerdictOf(TerminalWithStatus("ReorgRequired"));

            Assert.Equal(true, (bool?)block["reorgRequired"]);
        }

        [Fact]
        public void ReorgRequiredFlag_IsMarkedInTheBlock()
        {
            var terminal = TerminalWithStatus("Failed");
            terminal["reorgRequired"] = true;

            Assert.Equal(true, (bool?)VerdictOf(terminal)["reorgRequired"]);
        }

        [Fact]
        public void OrdinaryFailure_DoesNotMarkReorganization()
        {
            var block = VerdictOf(TerminalWithStatus("Failed"));

            Assert.Null(block["reorgRequired"]);
        }

        [Fact]
        public void ReorgRequiredFlagFalse_IsNotWritten()
        {
            var terminal = TerminalWithStatus("Failed");
            terminal["reorgRequired"] = false;

            Assert.Null(VerdictOf(terminal)["reorgRequired"]);
        }

        [Theory]
        [InlineData("ok", false)]
        [InlineData("skipped", false)]
        [InlineData("failed", true)]
        [InlineData("error", true)]
        [InlineData("timeout", true)]
        public void PromotesToError_UnlessOkOrSkipped(string status, bool promotes)
        {
            var block = new JObject { ["status"] = status };

            Assert.Equal(promotes, ApplyPatchValidation.PromotesToError(block));
        }
    }
}
