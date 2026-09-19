using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class CommentOnlyPatchTests
    {
        [Fact]
        public void Classify_LineComment_RecognizesStatementDeactivation()
        {
            bool classified = CommentOnlyPatch.TryClassify(
                "Source", "replace", "msg(&Guid.ToString(),nowait)",
                "//msg(&Guid.ToString(),nowait)", out string style);

            Assert.True(classified);
            Assert.Equal("line", style);
        }

        [Fact]
        public void Classify_BlockComment_RecognizesStatementDeactivation()
        {
            bool classified = CommentOnlyPatch.TryClassify(
                "Source", "replace", "msg(&Guid.ToString(),nowait)",
                "/* msg(&Guid.ToString(),nowait) */", out string style);

            Assert.True(classified);
            Assert.Equal("block", style);
        }

        [Fact]
        public void Classify_LineCommentWithTrailingWhitespace_RemainsCommentOnly()
        {
            bool classified = CommentOnlyPatch.TryClassify(
                "Source", "replace", "msg(\"x\",nowait)",
                "//msg(\"x\",nowait)   ", out string style);

            Assert.True(classified);
            Assert.Equal("line", style);
        }

        [Fact]
        public void Classify_LineComment_PreservesTerminalNewline()
        {
            bool classified = CommentOnlyPatch.TryClassify(
                "Source", "replace", "msg(\"x\",nowait)\r\n",
                "//msg(\"x\",nowait)\r\n", out string style);

            Assert.True(classified);
            Assert.Equal("line", style);
        }

        [Fact]
        public void Classify_LineComment_RecognizesFormattedMessageStatement()
        {
            const string statement = "msg(Format(!'%1' , &TemporaryId),nowait)";
            const string commented = "//msg(Format(!'%1' , &TemporaryId),nowait)";

            bool classified = CommentOnlyPatch.TryClassify(
                "Source", "replace", statement, commented, out string style);

            Assert.True(classified);
            Assert.Equal("line", style);
            Assert.Equal(0, CommentOnlyPatch.CountActiveOccurrences(commented, statement));
        }

        [Fact]
        public void CountActiveOccurrences_IgnoresLineAndBlockCommentsAndStringLiterals()
        {
            const string statement = "msg(&Guid.ToString(),nowait)";
            string source = "//msg(&Guid.ToString(),nowait)\n/* msg(&Guid.ToString(),nowait) */\n&Text = \"//msg(&Guid.ToString(),nowait)\"\nmsg(&Guid.ToString(),nowait)";

            Assert.Equal(1, CommentOnlyPatch.CountActiveOccurrences(source, statement));
        }

        [Fact]
        public void Receipt_CommentOnlyDoesNotCountStatementInsideCommentAsOldActiveContent()
        {
            const string before = "msg(&Guid.ToString(),nowait)";
            const string after = "//msg(&Guid.ToString(),nowait)";
            var payload = new JObject();
            var verification = TextPersistenceVerifier.Evaluate(after, after, "exact", "Source");

            bool verified = PatchPersistenceReceipt.AttachVerification(
                payload, verification, after, before, after, after, "exact", "Source", 1, commentOnly: true);

            Assert.True(verified);
            Assert.Equal(0, payload["persistedMatchCount"]?.Value<int>());
            Assert.False(payload["oldContentPresent"]?.Value<bool>());
            Assert.True(payload["replacementPresent"]?.Value<bool>());
            Assert.True(payload["reReadConfirmed"]?.Value<bool>());
            Assert.Equal("fresh-sdk-read", payload["verification"]?["source"]?.ToString());
            Assert.Equal(after, payload["source"]?.ToString());
            Assert.NotNull(payload["content"]?["requested"]?["hash"]);
            Assert.NotNull(payload["content"]?["saved"]?["hash"]);
            Assert.NotNull(payload["content"]?["reRead"]?["hash"]);
        }

        [Fact]
        public void RollbackPolicy_NeverRevertsAConfirmedWrite()
        {
            Assert.False(PatchPersistenceReceipt.ShouldRollback(persistedMatches: true, rollbackOnFailure: true));
            Assert.True(PatchPersistenceReceipt.ShouldRollback(persistedMatches: false, rollbackOnFailure: true));
            Assert.False(PatchPersistenceReceipt.ShouldRollback(persistedMatches: false, rollbackOnFailure: false));
        }

        [Fact]
        public void RollbackPolicyRequiresVersionFenceForConcurrentSafety()
        {
            Assert.True(PatchPersistenceReceipt.CanAttemptRollback(false, true, "version-after-save"));
            Assert.False(PatchPersistenceReceipt.CanAttemptRollback(false, true, null));
            Assert.False(PatchPersistenceReceipt.CanAttemptRollback(false, true, ""));
            Assert.False(PatchPersistenceReceipt.CanAttemptRollback(true, true, "version-after-save"));
        }

        [Fact]
        public void RollbackReceiptDistinguishesUnknownStateFromVerifiedMismatch()
        {
            var payload = new JObject();

            PatchPersistenceReceipt.MarkRollbackNotAttempted(payload, "unknown", verificationUnavailable: true);
            Assert.True(payload["rollback"]?["verificationUnavailable"]?.Value<bool>());
            Assert.False(payload["rollback"]?["attempted"]?.Value<bool>());

            PatchPersistenceReceipt.MarkRollbackNotAttempted(payload, "mismatch", verificationUnavailable: false);
            Assert.False(payload["rollback"]?["verificationUnavailable"]?.Value<bool>());
        }

        [Fact]
        public void Receipt_CommentOnlyMismatchUsesTypedError()
        {
            var payload = new JObject();
            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null, commentOnly: true);

            Assert.Equal("CommentOnlyWriteNotPersisted", payload["code"]?.ToString());
            Assert.False(payload["saved"]?.Value<bool>());
            Assert.True(payload["saveAttempted"]?.Value<bool>());
            Assert.False(payload["persisted"]?.Value<bool>());
            Assert.False(payload["verified"]?.Value<bool>());
        }
    }
}
