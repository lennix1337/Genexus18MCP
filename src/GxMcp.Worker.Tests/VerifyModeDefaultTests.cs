using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #557: `genexus_edit mode=ops add_attribute` without a description reported
    // `WriteNotPersisted` when `verifyMode` was omitted while the same write passed with
    // `verifyMode: "semantic"`. The receipts showed `postSaveVerification.mode:
    // "normalized"`: the schema declared `"default": "normalized"`, so clients that
    // materialize schema defaults sent that value explicitly and the omitted case never
    // reached the Structure default. These tests pin the effective mode at each
    // resolution point and the receipt contract for the explicit `normalized` control.
    public class VerifyModeDefaultTests
    {
        private const string Before = "McpTstR03Id* : NUMERIC(4) // \"Mcp Tst R03 Id\"";
        private const string Requested = Before + "\nMcpTstR03A : NUMERIC(4)";
        private const string Persisted = Before + "\nMcpTstR03A : NUMERIC(4) // \"Mcp Tst R03 A\"";

        private static JObject Receipt(string verifyMode, string actual = Persisted)
        {
            var response = new JObject
            {
                ["status"] = "ok",
                ["code"] = "WriteApplied",
                ["sdkSaveCompleted"] = true
            };
            return WriteService.ApplyTextVerificationReceipt(
                response, "McpTstR03", "Structure", Before, Requested, actual, "v2", false, null, verifyMode);
        }

        [Theory]
        [InlineData("Structure", null, "semantic")]
        [InlineData("Structure", "", "semantic")]
        [InlineData("Structure", "normalized", "normalized")]
        [InlineData("Structure", "exact", "exact")]
        [InlineData("Source", null, null)]
        [InlineData("Rules", "normalized", "normalized")]
        public void OpsPath_OmittedVerifyMode_DefaultsFromThePart(string part, string requested, string expected)
        {
            Assert.Equal(expected, WriteService.ResolveOpsVerifyMode(part, requested));
        }

        [Theory]
        [InlineData(null, "Structure", "semantic")]
        [InlineData(" ", "Structure", "semantic")]
        [InlineData("normalized", "Structure", "normalized")]
        [InlineData("exact", "Structure", "exact")]
        [InlineData(null, "Source", "normalized")]
        [InlineData(null, "Events", "normalized")]
        [InlineData(null, "Rules", "normalized")]
        [InlineData("semantic", "Source", "semantic")]
        public void ReceiptPath_OmittedVerifyMode_DefaultsFromThePart(string verifyMode, string part, string expected)
        {
            Assert.Equal(expected, WriteService.ResolveEffectiveVerifyMode(verifyMode, part));
        }

        [Fact]
        public void Structure_OmittedVerifyMode_AcceptsTheSdkGeneratedDescription()
        {
            var receipt = Receipt(null);

            Assert.Equal("WriteApplied", receipt["code"]);
            Assert.True(receipt["verified"].Value<bool>());
            Assert.True(receipt["persisted"].Value<bool>());
            Assert.Null(receipt["partialPersistenceDetected"]);
            Assert.Null(receipt["mutation"]["diff"]["mismatchKind"]);
        }

        [Fact]
        public void Structure_ExplicitSemantic_AcceptsTheSdkGeneratedDescription()
        {
            var receipt = Receipt("semantic");

            Assert.Equal("WriteApplied", receipt["code"]);
            Assert.True(receipt["verified"].Value<bool>());
            Assert.Equal("semantic", receipt["postSaveVerification"]["mode"]);
        }

        [Fact]
        public void Structure_ExplicitNormalized_IsHonouredAndLabelsTheGeneratedDescription()
        {
            // E3: an explicit `normalized` asked for the textual comparison and keeps its
            // verdict. E2: the receipt names the only difference, so the caller can tell
            // this SDK rendering from a lost write.
            var receipt = Receipt("normalized");

            Assert.Equal("WriteNotPersisted", receipt["code"]);
            Assert.True(receipt["saved"].Value<bool>());
            Assert.True(receipt["partialPersistenceDetected"].Value<bool>());
            Assert.Equal("normalized", receipt["postSaveVerification"]["mode"]);
            Assert.Equal("sdk-generated-description", receipt["error"]["mismatchKind"]);
            Assert.Equal("sdk-generated-description", receipt["mutation"]["diff"]["mismatchKind"]);
            Assert.Contains("verifyMode=semantic", receipt["error"]["hint"].ToString());
            Assert.Contains("Do not retry automatically", receipt["error"]["hint"].ToString());
        }

        [Fact]
        public void Structure_ExplicitNormalized_LabelsTheGeneratedDescription_WhenTheKeywordWasMixedCase()
        {
            // GX18 U16 proof: a request spelled `Numeric(4)` comes back `NUMERIC(4) // "…"`.
            // The keyword casing is the SDK's rendering, not a lost declaration, so the
            // label (and its hint) must still be earned.
            var response = new JObject { ["status"] = "ok", ["code"] = "WriteApplied", ["sdkSaveCompleted"] = true };
            var receipt = WriteService.ApplyTextVerificationReceipt(
                response, "McpTstR03", "Structure", Before, Before + "\nMcpTstR03A : Numeric(4)", Persisted, "v2", false, null, "normalized");

            Assert.Equal("WriteNotPersisted", receipt["code"]);
            Assert.Equal("sdk-generated-description", receipt["error"]["mismatchKind"]);
            Assert.Equal("sdk-generated-description", receipt["mutation"]["diff"]["mismatchKind"]);
            Assert.Contains("verifyMode=semantic", receipt["error"]["hint"].ToString());
        }

        [Fact]
        public void Structure_RealContentMismatch_IsNotLabelledAsGeneratedDescription()
        {
            var receipt = Receipt("normalized", actual: Before + "\nMcpTstR03A : NUMERIC(6) // \"Mcp Tst R03 A\"");

            Assert.Equal("WriteNotPersisted", receipt["code"]);
            Assert.Null(receipt["error"]["mismatchKind"]);
            Assert.Null(receipt["mutation"]["diff"]["mismatchKind"]);
            Assert.DoesNotContain("verifyMode=semantic", receipt["error"]["hint"].ToString());
        }
    }
}
