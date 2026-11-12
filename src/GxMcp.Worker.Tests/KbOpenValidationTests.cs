using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #38 defect #1: OpenKB fails fast with a clear KbInvalidPath envelope for a
    // path that is structurally not a KB root, before paying the heavy SDK open. This is
    // the defense-in-depth mirror of the gateway pre-check and keeps the auto-open
    // give-up counter converging quickly.
    public class KbOpenValidationTests
    {
        private static KbService NewKb() => new KbService(new IndexCacheService());

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-kbopen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void OpenKB_EnvironmentSubfolder_ReturnsKbInvalidPath()
        {
            string kbDir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(kbDir, "Demo.gxw"), "");
                string envDir = Path.Combine(kbDir, "Desenv");
                Directory.CreateDirectory(envDir);

                string raw = NewKb().OpenKB(envDir);
                var env = JObject.Parse(raw);

                Assert.Equal("error", env["status"]?.ToString());
                Assert.Equal("KbInvalidPath", env["error"]?["code"]?.ToString() ?? env["code"]?.ToString());
            }
            finally { Directory.Delete(kbDir, true); }
        }

        [Fact]
        public void OpenKB_MissingPath_ReturnsKbInvalidPath()
        {
            string missing = Path.Combine(Path.GetTempPath(), "gxmcp-missing-" + Guid.NewGuid().ToString("N"));
            var env = JObject.Parse(NewKb().OpenKB(missing));
            Assert.Equal("error", env["status"]?.ToString());
            Assert.Equal("KbInvalidPath", env["error"]?["code"]?.ToString() ?? env["code"]?.ToString());
        }

        // issue #957: the open envelope used to be dropped by the startup/background
        // auto-open, so a failed open left no trace a later request could read.
        [Fact]
        public void OpenKB_Failure_IsRecordedForLaterRequests()
        {
            var kb = NewKb();
            string missing = Path.Combine(Path.GetTempPath(), "gxmcp-missing-" + Guid.NewGuid().ToString("N"));
            Assert.Null(kb.LastOpenFailure);

            string raw = kb.OpenKB(missing);

            JObject failure = kb.LastOpenFailure;
            Assert.NotNull(failure);
            Assert.Equal("KbInvalidPath", failure["code"]?.ToString());
            Assert.Equal(JObject.Parse(raw)["error"]?["message"]?.ToString(), failure["message"]?.ToString());
            Assert.Equal(missing, failure["path"]?.ToString());
            Assert.True(DateTime.TryParse(failure["atUtc"]?.ToString(), out _));
        }

        [Fact]
        public void RecordOpenFailure_IsClearedByASuccessfulOpen()
        {
            var kb = NewKb();
            kb.RecordOpenFailure("C:\\KBs\\x", "KbOpenFailed", "InvalidDirectory");
            Assert.Equal("InvalidDirectory", kb.LastOpenFailure?["message"]?.ToString());

            kb.RecordOpenOutcome("C:\\KBs\\x", Models.McpResponse.Ok(target: "C:\\KBs\\x", code: "KbOpened"));

            Assert.Null(kb.LastOpenFailure);
        }

        [Fact]
        public void OpenInProgress_DoesNotReplaceTheRecordedFailure()
        {
            var kb = NewKb();
            kb.RecordOpenFailure("C:\\KBs\\x", "KbOpenFailed", "InvalidDirectory");

            kb.RecordOpenOutcome("C:\\KBs\\x", Models.McpResponse.Err("OpenInProgress", "busy"));

            Assert.Equal("KbOpenFailed", kb.LastOpenFailure?["code"]?.ToString());
        }
    }
}
