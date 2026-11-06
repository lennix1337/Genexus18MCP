using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WriteVerificationIntegrityTests
    {
        [Theory]
        [InlineData("same", false, null, "confirmed")]
        [InlineData("different", false, null, "not_persisted")]
        [InlineData("partial", true, null, "unknown")]
        [InlineData("partial", false, "readFailed", "unknown")]
        public void TextWriteReceipt_ReportsPersistedState(string actual, bool truncated, string failure, string expected)
        {
            var response = new JObject { ["status"] = "ok", ["code"] = "WriteApplied" };
            var receipt = WriteService.ApplyTextVerificationReceipt(response, "Example", "Source", "before", "same", actual, null, truncated, failure, null);
            Assert.Equal(expected, receipt["persistedState"]?.ToString());
            if (expected == "unknown")
            {
                Assert.Equal("WriteVerificationUnavailable", receipt["code"]?.ToString());
                Assert.Equal(failure == null ? "truncation" : "readFailure", receipt["postSaveVerification"]?["reason"]?.ToString());
            }
        }

        [Fact]
        public void TruncatedRead_IsIndeterminate_NotWriteMismatch()
        {
            var result = WriteService.EvaluatePersistedVerification(
                new string('x', 56788),
                new string('x', 16384),
                readTruncated: true,
                readFailure: null);

            Assert.Equal("indeterminate", result.State);
            Assert.Equal("truncation", result.Reason);
            Assert.True(result.IsIndeterminate);
            Assert.False(result.Matches);
        }

        [Fact]
        public void FormattingAndCasingSdkChange_IsReportedAsNormalization()
        {
            var result = WriteService.EvaluatePersistedVerification(
                "parm(in:&Id);\r\nFor each\r\nEndFor",
                "Parm(in:&Id);\n  FOR EACH\nEndFor",
                readTruncated: false,
                readFailure: null);

            Assert.Equal("verified", result.State);
            Assert.Equal("normalization", result.Reason);
            Assert.True(result.Matches);
        }

        [Theory]
        [InlineData("Variables")]
        [InlineData("Structure")]
        [InlineData("Source")]
        [InlineData("Events")]
        public void DefaultVerification_AcceptsRenderedPartLineEndingNormalization(string partName)
        {
            var tolerant = WriteService.EvaluatePersistedVerification(
                "alpha\r\nbeta", "alpha\nbeta", readTruncated: false, readFailure: null,
                verifyMode: null, partName: partName);
            var exact = WriteService.EvaluatePersistedVerification(
                "alpha\r\nbeta", "alpha\nbeta", readTruncated: false, readFailure: null,
                verifyMode: "exact", partName: partName);

            Assert.True(tolerant.Matches);
            Assert.Equal("normalization", tolerant.Reason);
            Assert.False(exact.Matches);
            Assert.Equal("lineEndings", exact.Reason);
        }

        [Theory]
        [InlineData("Variables")]
        [InlineData("Structure")]
        [InlineData("Source")]
        [InlineData("Events")]
        public void FacadeBoundary_PreservesPartAndExactVerification(string partName)
        {
            var args = JObject.Parse($"{{\"part\":\"{partName}\",\"verifyMode\":\"exact\"}}");
            var normalized = WriteService.NormalizeFacadeArgs(args);
            var result = WriteService.EvaluatePersistedVerification(
                "alpha\r\nbeta", "alpha\nbeta", readTruncated: false, readFailure: null,
                verifyMode: normalized.VerifyMode, partName: normalized.PartName);

            Assert.Equal(partName, normalized.PartName);
            Assert.Equal("exact", normalized.VerifyMode);
            Assert.False(result.Matches);
            Assert.Equal("lineEndings", result.Reason);
        }

        [Fact]
        public void DefaultVariablesVerification_AcceptsSdkTypeCasingNormalization()
        {
            const string requested = "&GridState : CustomerSdt, General\r\n&PlainLength : Character(25)";
            const string persisted = "&GridState : CustomerSdt, General\r\n&PlainLength : CHARACTER(25)";

            var tolerant = WriteService.EvaluatePersistedVerification(
                requested, persisted, readTruncated: false, readFailure: null,
                verifyMode: null, partName: "Variables");
            var strict = WriteService.EvaluatePersistedVerification(
                requested, persisted, readTruncated: false, readFailure: null,
                verifyMode: "exact", partName: "Variables");

            Assert.True(tolerant.Matches);
            Assert.Equal("verified", tolerant.State);
            Assert.Equal("normalization", tolerant.Reason);
            Assert.False(strict.Matches);
            Assert.Equal("mismatch", strict.State);
        }

        [Fact]
        public void XmlAttributeOrder_IsReportedAsNormalization()
        {
            var result = WriteService.EvaluatePersistedVerification(
                "<root><item a=\"1\" b=\"2\" /></root>",
                "<root><item b=\"2\" a=\"1\" /></root>",
                readTruncated: false,
                readFailure: null);

            Assert.Equal("verified", result.State);
            Assert.Equal("normalization", result.Reason);
            Assert.True(result.Matches);
        }

        [Fact]
        public void RealDifference_RemainsAContentMismatch()
        {
            var result = WriteService.EvaluatePersistedVerification(
                "msg('new')",
                "msg('old')",
                readTruncated: false,
                readFailure: null);

            Assert.Equal("mismatch", result.State);
            Assert.Equal("contentMismatch", result.Reason);
            Assert.False(result.Matches);
        }

        [Fact]
        public void UndoSdkBusy_ForcesPublicIsBusyTrue()
        {
            var status = new JObject { ["isBusy"] = false };
            var sdkBusy = new JObject
            {
                ["active"] = true,
                ["operation"] = "Undo/Undo",
                ["elapsedMs"] = 42000
            };

            Program.MergeSdkBusyStatus(status, sdkBusy);

            Assert.True(status["isBusy"]?.ToObject<bool>());
            Assert.True(status["sdkBusy"]?["active"]?.ToObject<bool>());
            Assert.Equal("Undo/Undo", status["activeOperation"]?.ToString());
        }

        [Fact]
        public void WorkerCommand_ExposesGatewayOperationIdForBusyDiagnostics()
        {
            string operationId = Program.ExtractOperationId(
                "{\"id\":\"worker-1\",\"method\":\"Patch\",\"action\":\"Apply\",\"_meta\":{\"progressToken\":\"op-123\"}}");

            Assert.Equal("op-123", operationId);
        }
    }
}
