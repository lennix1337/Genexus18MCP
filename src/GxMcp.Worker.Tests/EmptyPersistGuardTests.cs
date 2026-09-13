using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #24 — a non-empty source write that lands as an empty part on disk
    // (GeneXus silently drops source containing inline native-code delimiters
    // `[! !]`) used to return WriteApplied with the empty-string hash. The guard
    // converts that false success into a recoverable WriteNotPersisted error.
    // These cover the pure decision + the envelope rewrite without touching the SDK.
    public class EmptyPersistGuardTests
    {
        // sha256 of the empty string — what WrapWithPersistedState emits when the
        // re-read source is empty (the e3b0c44… hash reported in the issue).
        private const string EmptyHash = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        private const string NonEmptyHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

        private const string JavaInline =
            "java try {\n" +
            "    java byte[] imgBytes = java.nio.file.Files.readAllBytes(java.nio.file.Paths.get([!&blobImagem!]));\n" +
            "java }";

        [Theory]
        [InlineData("Source", true)]
        [InlineData("Events", true)]
        [InlineData("Code", true)]
        public void ShouldReject_NonEmptyInput_PersistedEmpty_OnLogicalSource(string part, bool expected)
        {
            Assert.Equal(expected, WriteService.ShouldRejectEmptyPersist(part, JavaInline, EmptyHash));
        }

        [Fact]
        public void ShouldNotReject_WhenPersistedNonEmpty()
        {
            Assert.False(WriteService.ShouldRejectEmptyPersist("Source", JavaInline, NonEmptyHash));
        }

        [Fact]
        public void ShouldNotReject_WhenInputWasBlank()
        {
            // Intentional blank-out of a source must still succeed.
            Assert.False(WriteService.ShouldRejectEmptyPersist("Source", "   ", EmptyHash));
            Assert.False(WriteService.ShouldRejectEmptyPersist("Source", null, EmptyHash));
        }

        [Fact]
        public void ShouldNotReject_NonLogicalPart()
        {
            // Visual / structure parts have their own verification; the guard is source-only.
            Assert.False(WriteService.ShouldRejectEmptyPersist("WebForm", JavaInline, EmptyHash));
            Assert.False(WriteService.ShouldRejectEmptyPersist("Structure", JavaInline, EmptyHash));
        }

        [Fact]
        public void ShouldNotReject_WhenNoReread()
        {
            Assert.False(WriteService.ShouldRejectEmptyPersist("Source", JavaInline, null));
        }

        [Fact]
        public void Guard_RewritesFalseWriteApplied_ToWriteNotPersisted()
        {
            string applied = new JObject
            {
                ["status"] = "ok",
                ["code"] = "WriteApplied",
                ["target"] = "ProcImg",
                ["persistedHash"] = EmptyHash,
                ["persistedSnippet"] = ""
            }.ToString();

            var result = JObject.Parse(
                WriteService.ApplyEmptyPersistGuard(applied, "ProcImg", "Source", JavaInline));

            Assert.Equal("error", (string)result["status"]);
            Assert.Equal("WriteNotPersisted", (string)result["error"]["code"]);
            Assert.Equal("ProcImg", (string)result["target"]);
            Assert.Equal(JavaInline.Length, (int)result["inputLength"]);
            // The follow-up flag must be set so the next identical write doesn't
            // get stuck on a phantom WriteNoChange.
            Assert.True(WriteService.IsEmptyPersistPending("ProcImg", "Source"));
            Assert.True(result["partialPersistenceDetected"]?.Value<bool>() == true);
            Assert.True(WriteService.ShouldMarkTargetDirty(result.ToString()));
        }

        [Fact]
        public void Guard_LeavesGenuineWriteApplied_Untouched_AndClearsFlag()
        {
            // Prime the flag, then a genuine non-empty persist must clear it.
            string applied = new JObject
            {
                ["status"] = "ok",
                ["code"] = "WriteApplied",
                ["target"] = "ProcOk",
                ["persistedHash"] = NonEmptyHash
            }.ToString();

            // Fire once with empty persist to set the flag.
            WriteService.ApplyEmptyPersistGuard(new JObject
            {
                ["status"] = "ok",
                ["code"] = "WriteApplied",
                ["target"] = "ProcOk",
                ["persistedHash"] = EmptyHash
            }.ToString(), "ProcOk", "Source", JavaInline);
            Assert.True(WriteService.IsEmptyPersistPending("ProcOk", "Source"));

            var result = JObject.Parse(
                WriteService.ApplyEmptyPersistGuard(applied, "ProcOk", "Source", JavaInline));

            Assert.Equal("ok", (string)result["status"]);
            Assert.Equal("WriteApplied", (string)result["code"]);
            Assert.False(WriteService.IsEmptyPersistPending("ProcOk", "Source"));
        }

        [Fact]
        public void Guard_IgnoresNonAppliedEnvelopes()
        {
            string noChange = new JObject
            {
                ["status"] = "ok",
                ["code"] = "WriteNoChange",
                ["target"] = "ProcNc",
                ["persistedHash"] = EmptyHash
            }.ToString();

            var result = JObject.Parse(
                WriteService.ApplyEmptyPersistGuard(noChange, "ProcNc", "Source", JavaInline));

            Assert.Equal("ok", (string)result["status"]);
            Assert.Equal("WriteNoChange", (string)result["code"]);
        }
    }
}
