using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // The validation block of a finished validate build, read from the shape the worker's Build/Status reply has after
    // ApplyWarningDelta: PascalCase counters and lists, lowercase warningCount and newWarnings, and an emptied warnings list.
    public class ApplyPatchBlockTests
    {
        private static JObject StatusReply(string status, int errorCount, int warningCount, params string[] errors)
            => new JObject
            {
                ["TaskId"] = "SynthTask",
                ["Status"] = status,
                ["Phase"] = "Done",
                ["Action"] = "Build",
                ["Target"] = "SynthHost",
                ["ErrorCount"] = errorCount,
                ["WarningCount"] = warningCount,
                ["Errors"] = new JArray(errors),
                ["newWarnings"] = new JArray(),
                ["warningCount"] = warningCount,
                ["warningTotal"] = warningCount,
                ["warnings"] = new JArray(),
                ["warningCursor"] = 0,
            };

        private static JObject BlockOf(JObject reply)
        {
            var block = new JObject();
            ApplyPatchValidation.WriteBlock(block, reply, (string?)reply["TaskId"]);
            return block;
        }

        [Fact]
        public void SucceededWithWarnings_KeepsTheCounterAndTheTask()
        {
            var block = BlockOf(StatusReply("Succeeded", errorCount: 0, warningCount: 2));

            Assert.Equal("ok", (string?)block["status"]);
            Assert.Equal("Succeeded", (string?)block["buildStatus"]);
            Assert.Equal(0, (int?)block["errorCount"]);
            Assert.Equal(2, (int?)block["warningCount"]);
            Assert.Equal("SynthTask", (string?)block["taskId"]);
            Assert.Null(block["errors"]);
            Assert.Null(block["warnings"]);
        }

        [Fact]
        public void FailedWithTwelveErrors_KeepsTheFirstTen()
        {
            string[] errors = Enumerable.Range(1, 12).Select(i => $"SynthError{i:D2}").ToArray();

            var block = BlockOf(StatusReply("Failed", errorCount: 12, warningCount: 0, errors));

            Assert.Equal("failed", (string?)block["status"]);
            Assert.Equal(12, (int?)block["errorCount"]);
            var kept = Assert.IsType<JArray>(block["errors"]);
            Assert.Equal(10, kept.Count);
            Assert.Equal("SynthError10", (string?)kept[9]);
        }

        [Fact]
        public void FailedWithMoreErrorsThanTheWorkerListed_ReportsTheCounter()
        {
            // The worker counts every error in ErrorCount and keeps at most 50 messages in Errors.
            string[] listed = Enumerable.Range(1, 50).Select(i => $"SynthError{i:D2}").ToArray();

            var block = BlockOf(StatusReply("Failed", errorCount: 60, warningCount: 0, listed));

            Assert.Equal(60, (int?)block["errorCount"]);
            Assert.Equal(10, Assert.IsType<JArray>(block["errors"]).Count);
        }

        [Fact]
        public void ReorgRequiredReply_IsFailedAndFlagged()
        {
            var reply = StatusReply("ReorgRequired", errorCount: 0, warningCount: 0);
            reply["reorgRequired"] = true;

            var block = BlockOf(reply);

            Assert.Equal("failed", (string?)block["status"]);
            Assert.Equal("ReorgRequired", (string?)block["buildStatus"]);
            Assert.Equal(true, (bool?)block["reorgRequired"]);
        }

        [Fact]
        public void LowercaseErrorsAreReadWhenThePascalCaseListIsAbsent()
        {
            var reply = new JObject
            {
                ["Status"] = "Failed",
                ["ErrorCount"] = 1,
                ["errors"] = new JArray("SynthLegacyError"),
            };

            var block = BlockOf(reply);

            Assert.Equal("SynthLegacyError", (string?)block["errors"]?[0]);
        }

        [Fact]
        public void PascalCaseWarningsAreCappedAtFive()
        {
            var reply = new JObject
            {
                ["Status"] = "Succeeded",
                ["WarningCount"] = 7,
                ["Warnings"] = new JArray(Enumerable.Range(1, 7).Select(i => $"SynthWarning{i}")),
            };

            var block = BlockOf(reply);

            Assert.Equal(5, Assert.IsType<JArray>(block["warnings"]).Count);
        }
    }
}
