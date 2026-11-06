using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #547: a patch the SDK saved but the exact re-read rejected must say
    /// whether the divergence is a rendering difference or lost content, keep the
    /// physical save visible at the top of the receipt, and never steer the
    /// caller into retrying an already-applied write.
    /// </summary>
    public class PatchPersistenceReceiptTests
    {
        [Theory]
        [InlineData("confirmed")]
        [InlineData("not_persisted")]
        [InlineData("unknown")]
        public void PatchTextVerificationReplacesPriorStateWithFinalVerdict(string expected)
        {
            var payload = new JObject { ["persistedState"] = "stale" };
            if (expected == "confirmed") PatchPersistenceReceipt.MarkVerified(payload, saved: true);
            else if (expected == "not_persisted") PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null);
            else PatchPersistenceReceipt.MarkVerificationUnavailable(payload, saveAttempted: true, reason: "truncation");
            Assert.Equal(expected, payload["persistedState"]?.ToString());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ASubsequentRollbackInvalidatesThePreRollbackTextVerdict(bool verified)
        {
            var payload = new JObject { ["persistedState"] = "not_persisted" };
            PatchPersistenceReceipt.MarkWriterRollback(payload, verified, null, null);
            Assert.Equal("unknown", payload["persistedState"]?.ToString());
        }

        [Fact]
        public void Issue547_MarkNotPersisted_NormalizedMatch_ReportsNormalizationMismatch()
        {
            var payload = new JObject();

            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null, normalizedMatches: true);

            Assert.Equal("WriteNotPersisted", payload["code"]?.ToString());
            Assert.Equal("normalization", payload["mismatchKind"]?.ToString());
            string message = payload["message"]?.ToString() ?? string.Empty;
            Assert.Contains("only by formatting or normalization", message);
            Assert.DoesNotContain("then retry", message);
            Assert.True(payload["saveAttempted"]?.Value<bool>());
            Assert.False(payload["saved"]?.Value<bool>());
            Assert.False(payload["verified"]?.Value<bool>());
        }

        [Fact]
        public void Issue547_MarkNotPersisted_ContentMismatch_DoesNotClaimNormalization()
        {
            var payload = new JObject();

            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null, normalizedMatches: false);

            Assert.Equal("WriteNotPersisted", payload["code"]?.ToString());
            Assert.Equal("content", payload["mismatchKind"]?.ToString());
            string message = payload["message"]?.ToString() ?? string.Empty;
            Assert.Contains("differs from the request in content", message);
            Assert.Contains("Re-read the part and compare", message);
            Assert.DoesNotContain("formatting or normalization", message);
            Assert.DoesNotContain("then retry", message);
            Assert.True(payload["saveAttempted"]?.Value<bool>());
            Assert.False(payload["saved"]?.Value<bool>());
            Assert.False(payload["persisted"]?.Value<bool>());
        }

        [Fact]
        public void Issue547_MarkNotPersisted_WithoutEvidence_OmitsMismatchKindAndNormalizationClaim()
        {
            var payload = new JObject();

            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: "pattern content differs");

            Assert.Null(payload["mismatchKind"]);
            Assert.DoesNotContain("normalization", payload["message"]?.ToString() ?? string.Empty);
            Assert.Equal("pattern content differs", payload["persistedVerifyError"]?.ToString());
        }

        [Fact]
        public void Issue547_MarkNotPersisted_RealSaveFailure_KeepsNotPersistedMessage()
        {
            var payload = new JObject();

            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: false, verifyError: "syntax rejected", normalizedMatches: false);

            Assert.Equal("content", payload["mismatchKind"]?.ToString());
            Assert.Equal("The post-save re-read does not contain the requested patched content.", payload["message"]?.ToString());
            Assert.False(payload["saveAttempted"]?.Value<bool>());
            Assert.False(payload["sdkSaveCompleted"]?.Value<bool>());
            Assert.False(payload["saved"]?.Value<bool>());
        }

        [Fact]
        public void Issue547_MarkNotPersisted_KeepsPhysicalSaveAtTopOfReceipt()
        {
            // The WriteService receipt already carries the physical save evidence.
            var payload = JObject.Parse(
                "{sdkSaveCompleted:true,objectSaved:true,persistedStateKnown:true,"
                + "postSaveVerification:{reReadConfirmed:true,matches:true,mode:'legacy',reason:'normalization'},"
                + "mutation:{verification:'verified',saved:true}}");

            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null, normalizedMatches: true);

            Assert.True(payload["sdkSaveCompleted"]?.Value<bool>());
            Assert.True(payload["objectSaved"]?.Value<bool>());
            Assert.True(payload["saveAttempted"]?.Value<bool>());
            Assert.True(payload["mutation"]?["saved"]?.Value<bool>());
            // The fail-closed claim stays false until the fresh re-read confirms the content.
            Assert.False(payload["saved"]?.Value<bool>());
            Assert.False(payload["verified"]?.Value<bool>());
            Assert.False(payload["persisted"]?.Value<bool>());
            Assert.False(payload["persistedVerified"]?.Value<bool>());
            Assert.Equal("normalization", payload["mismatchKind"]?.ToString());
        }

        [Fact]
        public void Issue547_MarkNotPersisted_FillsMissingSdkSaveCompletedFromSaveAttempt()
        {
            var payload = new JObject();

            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null, normalizedMatches: true);

            Assert.Equal(JTokenType.Boolean, payload["sdkSaveCompleted"]?.Type);
            Assert.True(payload["sdkSaveCompleted"]?.Value<bool>());
        }

        [Fact]
        public void Issue547_MarkNotPersisted_DoesNotOverrideWriteServicePhysicalSaveEvidence()
        {
            var payload = new JObject { ["sdkSaveCompleted"] = false };

            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null, normalizedMatches: false);

            Assert.False(payload["sdkSaveCompleted"]?.Value<bool>());
            Assert.True(payload["saveAttempted"]?.Value<bool>());
        }

        [Fact]
        public void Issue547_NormalizedMatches_KeywordCasingOnly_IsNormalization()
        {
            // The reproduction from the issue: exact rejects Numeric(4) vs NUMERIC(4),
            // but nothing was lost.
            const string requested = "&Preserved : NUMERIC(8,2)\n&AddedOne : Numeric(4)\n&AddedTwo : Character(20)";
            const string persisted = "&Preserved : NUMERIC(8,2)\n&AddedOne : NUMERIC(4)\n&AddedTwo : CHARACTER(20)";
            var verification = TextPersistenceVerifier.Evaluate(requested, persisted, "exact", "Variables");

            Assert.False(verification.Matches);
            Assert.NotEqual(verification.NormalizedRequestedHash, verification.NormalizedPersistedHash);
            Assert.True(PatchPersistenceReceipt.NormalizedMatches(verification, requested, persisted, "Variables"));
        }

        [Fact]
        public void Issue547_NormalizedMatches_WhitespaceOnly_IsNormalization()
        {
            const string requested = "&A : Numeric(4)  \n\n&B : Character(20)";
            const string persisted = "&A : Numeric(4)\n&B : Character(20)";
            var verification = TextPersistenceVerifier.Evaluate(requested, persisted, "exact", "Variables");

            Assert.False(verification.Matches);
            Assert.Equal(verification.NormalizedRequestedHash, verification.NormalizedPersistedHash);
            Assert.True(PatchPersistenceReceipt.NormalizedMatches(verification, requested, persisted, "Variables"));
        }

        [Fact]
        public void Issue547_NormalizedMatches_MissingDeclaration_IsContent()
        {
            const string requested = "&A : Numeric(4)\n&B : Character(20)";
            const string persisted = "&A : NUMERIC(4)";
            var verification = TextPersistenceVerifier.Evaluate(requested, persisted, "exact", "Variables");

            Assert.False(verification.Matches);
            Assert.False(PatchPersistenceReceipt.NormalizedMatches(verification, requested, persisted, "Variables"));
        }

        [Fact]
        public void Issue547_NormalizedMatches_StringLiteralCaseChange_IsContent()
        {
            const string requested = "&Msg = 'Hello'";
            const string persisted = "&Msg = 'hello'";
            var verification = TextPersistenceVerifier.Evaluate(requested, persisted, "exact", "Source");

            Assert.False(PatchPersistenceReceipt.NormalizedMatches(verification, requested, persisted, "Source"));
        }

        [Fact]
        public void Issue547_SelectRecoveryHint_NormalizationMismatch_DoesNotAdviseRetry()
        {
            string hint = PatchService.SelectRecoveryHint(
                "WriteNotPersisted", saveAttemptedOrReported: true, mismatchKind: "normalization", manualRecovery: null, writeHint: null);

            Assert.Contains("verifyMode=normalized or semantic", hint);
            Assert.Contains("Do not retry the patch", hint);
            Assert.DoesNotContain("then retry", hint);
        }

        [Fact]
        public void Issue547_SelectRecoveryHint_ContentMismatch_AdvisesReReadAndCompare()
        {
            string hint = PatchService.SelectRecoveryHint(
                "WriteNotPersisted", saveAttemptedOrReported: true, mismatchKind: "content", manualRecovery: null, writeHint: null);

            Assert.Contains("differs from the request in content", hint);
            Assert.Contains("compare it with the requested change", hint);
            Assert.Contains("Do not retry blindly", hint);
            Assert.DoesNotContain("then retry", hint);
        }

        [Fact]
        public void Issue547_SelectRecoveryHint_SavedWithoutClassification_StillDoesNotAdviseRetry()
        {
            string hint = PatchService.SelectRecoveryHint(
                "WriteNotPersisted", saveAttemptedOrReported: true, mismatchKind: null, manualRecovery: null, writeHint: null);

            Assert.Contains("Do not retry blindly", hint);
            Assert.DoesNotContain("then retry", hint);
        }

        [Fact]
        public void Issue547_SelectRecoveryHint_NoSaveAttempt_AdvisesRetry()
        {
            string hint = PatchService.SelectRecoveryHint(
                "PatchWriteFailed", saveAttemptedOrReported: false, mismatchKind: null, manualRecovery: null, writeHint: null);

            Assert.Equal("Re-read the object source and verify the part is writable, then retry.", hint);
        }

        [Fact]
        public void Issue547_SelectRecoveryHint_PreservesManualRecoveryAndUnavailableHints()
        {
            Assert.Equal(
                "manual",
                PatchService.SelectRecoveryHint("ObjectSaveIncomplete", saveAttemptedOrReported: true, mismatchKind: "content", manualRecovery: "manual", writeHint: "unavailable"));
            Assert.Equal(
                "unavailable",
                PatchService.SelectRecoveryHint("WriteVerificationUnavailable", saveAttemptedOrReported: true, mismatchKind: null, manualRecovery: "manual", writeHint: "unavailable"));
        }
    }
}
