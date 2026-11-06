using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The child-process launch settings were written out four times - in
    /// <c>BlameService</c>, <c>GithubService</c>, <c>PrDescriptionService</c> and
    /// <c>TimeTravelService</c> - and are now <c>ProcessLauncher</c>.
    ///
    /// Each setting is load-bearing and each was added because leaving it out broke
    /// something, so the launcher is tested against real processes rather than
    /// against a mock: the deadlock it prevents needs two full pipes, and the
    /// "killed, not abandoned" guarantee needs a process that outlives its
    /// timeout.
    ///
    /// The timeout path is the branch a normal run never takes, which is exactly
    /// why it is worth holding: it is what a copy of this block silently loses.
    /// </summary>
    public class ProcessLauncherTests
    {
        private const string Cmd = "cmd.exe";

        [Fact]
        public void StdoutIsCapturedAndTheExitCodeIsReturned()
        {
            var outcome = ProcessLauncher.Run(Cmd, "/c echo hello", Environment.CurrentDirectory, 15000);

            Assert.False(outcome.StartFailed);
            Assert.False(outcome.TimedOut);
            Assert.Equal(0, outcome.ExitCode);
            Assert.Contains("hello", outcome.StdOut);
        }

        [Fact]
        public void ANonZeroExitIsReportedRatherThanThrown()
        {
            var outcome = ProcessLauncher.Run(Cmd, "/c exit 3", Environment.CurrentDirectory, 15000);

            Assert.False(outcome.StartFailed);
            Assert.False(outcome.TimedOut);
            Assert.Equal(3, outcome.ExitCode);
        }

        [Fact]
        public void StderrIsCapturedSeparatelyFromStdout()
        {
            var onStdout = ProcessLauncher.Run(Cmd, "/c echo out", Environment.CurrentDirectory, 15000);
            Assert.Contains("out", onStdout.StdOut);

            var onStderr = ProcessLauncher.Run(Cmd, "/c echo err 1>&2", Environment.CurrentDirectory, 15000);
            Assert.Contains("err", onStderr.StdErr);
        }

        [Fact]
        public void AProcessThatOutlivesItsTimeoutIsKilledAndReported()
        {
            // ping 127.0.0.1 30 times is comfortably longer than the 700 ms budget
            // below and needs no network: loopback replies immediately.
            var started = DateTime.UtcNow;
            var outcome = ProcessLauncher.Run("ping.exe", "-n 30 127.0.0.1", Environment.CurrentDirectory, 700);
            var elapsed = DateTime.UtcNow - started;

            Assert.False(outcome.StartFailed);
            Assert.True(outcome.TimedOut, "expected a timeout, not a clean exit");
            Assert.Equal(-1, outcome.ExitCode);
            Assert.True(elapsed.TotalSeconds < 20,
                "the launcher waited for the child instead of killing it: " + elapsed);
        }

        [Fact]
        public void ATimedOutChildIsKilledNotMerelyAbandoned()
        {
            CheckSignaledChild(releaseBeforeTimeout: true);
            CheckSignaledChild(releaseBeforeTimeout: false);
        }

        private static void CheckSignaledChild(bool releaseBeforeTimeout)
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Path.GetTempPath(), "gxmcp-launcher-" + id);
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, "completed");
            var pidFile = Path.Combine(directory, "pid");
            var readyName = "gxmcp-ready-" + id;
            var releaseName = "gxmcp-release-" + id;
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
            using (var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName))
            {
                // The direct child identifies itself before waiting. This proves the kill
                // against that process, without sleeping and hoping a marker appears later.
                var script = "$ErrorActionPreference='Stop'; " +
                    "$ready=[Threading.EventWaitHandle]::OpenExisting($env:GXMCP_TEST_READY); " +
                    "$release=[Threading.EventWaitHandle]::OpenExisting($env:GXMCP_TEST_RELEASE); " +
                    "[IO.File]::WriteAllText($env:GXMCP_TEST_PID,[string]$PID); " +
                    "$null=$ready.Set(); " +
                    "if($release.WaitOne(30000)){[IO.File]::WriteAllText($env:GXMCP_TEST_MARKER,'completed')}";
                var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var task = Task.Run(() => ProcessLauncher.Run(executable, arguments, directory,
                    releaseBeforeTimeout ? 10000 : 3000, environment: new Dictionary<string, string>
                    {
                        ["GXMCP_TEST_READY"] = readyName,
                        ["GXMCP_TEST_RELEASE"] = releaseName,
                        ["GXMCP_TEST_PID"] = pidFile,
                        ["GXMCP_TEST_MARKER"] = marker
                    }));
                Process child = null;
                try
                {
                    Assert.True(ready.WaitOne(10000), "the child did not reach its wait; the kill check would be vacuous");
                    child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
                    _ = child.Handle; // Hold this process identity even if its PID is later reused.
                    if (releaseBeforeTimeout) release.Set();
                    Assert.True(task.Wait(10000), "the launcher did not return within the test deadline");
                    var outcome = task.GetAwaiter().GetResult();
                    Assert.False(outcome.StartFailed);
                    Assert.Equal(!releaseBeforeTimeout, outcome.TimedOut);
                    Assert.True(child.WaitForExit(3000), "the timed-out child was abandoned rather than killed");
                    Assert.Equal(releaseBeforeTimeout, File.Exists(marker));
                    if (releaseBeforeTimeout) Assert.Equal(0, outcome.ExitCode);
                }
                finally
                {
                    release.Set();
                    if (child != null)
                    {
                        if (!child.WaitForExit(5000)) child.Kill();
                        child.Dispose();
                    }
                    task.Wait(15000);
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [Fact]
        public void AMissingExecutableSurfacesAsAPlatformError()
        {
            // Behaviour preserved from the four blocks this replaced: they all
            // tested `p == null`, which never happens on Windows - Process.Start
            // raises before a process exists. So a missing tool propagates, and
            // that is deliberate here rather than a swallowed configuration error.
            Assert.ThrowsAny<Exception>(() => ProcessLauncher.Run(
                "definitely-not-a-real-executable-9f2c.exe", string.Empty, Environment.CurrentDirectory, 5000));
        }

        [Fact]
        public void AChildCannotBlockOnTheInheritedStandardInput()
        {
            // The worker runs behind the Gateway's MCP stdio pipe. A child that
            // probes stdin there would hang until the timeout; stdin is redirected
            // and closed for exactly that reason, so `more` runs out of input and
            // returns at once instead of waiting.
            var started = DateTime.UtcNow;
            var outcome = ProcessLauncher.Run(Cmd, "/c more", Environment.CurrentDirectory, 8000);
            var elapsed = DateTime.UtcNow - started;

            Assert.False(outcome.TimedOut, "the child blocked on stdin and had to be killed");
            Assert.True(elapsed.TotalSeconds < 8, "took " + elapsed);
        }

        [Fact]
        public void OutputLargerThanOnePipeBufferIsFullyDrained()
        {
            // A sequential ReadToEnd/ReadToEnd/WaitForExit deadlocks once one pipe
            // fills while the other is being read. This writes ~400 KB, well past
            // the 64 KB Windows pipe buffer, so a sequential drain would hang.
            var outcome = ProcessLauncher.Run(
                Cmd, "/c (for /L %i in (1,1,4000) do @echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa)",
                Environment.CurrentDirectory, 25000);

            Assert.False(outcome.TimedOut, "deadlocked draining a full pipe");
            var lines = outcome.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4000, lines.Length);
        }

        [Fact]
        public void TheWorkingDirectoryIsApplied()
        {
            var outcome = ProcessLauncher.Run(Cmd, "/c cd", Environment.CurrentDirectory, 15000);

            Assert.False(outcome.TimedOut);
            Assert.False(string.IsNullOrWhiteSpace(outcome.StdOut));
        }

        [Fact]
        public void TheEnvironmentDictionaryReachesTheChild()
        {
            var outcome = ProcessLauncher.Run(
                Cmd, "/c echo %GXMCP_TEST_PROBE%", Environment.CurrentDirectory, 15000,
                null,
                new Dictionary<string, string> { { "GXMCP_TEST_PROBE", "propagated" } });

            Assert.Contains("propagated", outcome.StdOut);
        }

        [Fact]
        public void NoEncodingIsForcedWhenTheCallerDoesNotAskForOne()
        {
            // BlameService has always read git's output in the console codepage.
            // Defaulting to UTF-8 here would silently change what it sees, so the
            // launcher leaves it unset.
            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ProcessLauncher.cs");

            Assert.Contains("if (outputEncoding != null)", helper);
            Assert.DoesNotContain("StandardOutputEncoding = Encoding.UTF8", helper);
        }

        [Fact]
        public void TheLauncherNeverUsesAShell()
        {
            // With UseShellExecute left true, the argument string is re-parsed by
            // cmd.exe and the quoting Argv got right is undone before the child
            // sees it. This is the one setting that makes the whole helper safe.
            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ProcessLauncher.cs");

            Assert.Contains("UseShellExecute = false", helper);
            Assert.DoesNotContain("UseShellExecute = true", helper);
            Assert.DoesNotContain("cmd.exe /c", helper);
            Assert.DoesNotContain("/c \"", helper);
        }

        [Fact]
        public void TheGitFamilyNoLongerBuildsItsOwnLaunchBlock()
        {
            // Four copies, one implementation. A fourth copy of these settings -
            // most likely to lose the timeout branch - re-opens what this removed.
            foreach (string file in new[] { "BlameService.cs", "GithubService.cs", "PrDescriptionService.cs", "TimeTravelService.cs" })
            {
                string src = RepoSource.Read("src", "GxMcp.Worker", "Services", file);
                Assert.Contains("ProcessLauncher.Run(", src);
                Assert.DoesNotContain("new ProcessStartInfo(", src);
                Assert.DoesNotContain("BeginOutputReadLine", src);
            }

            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ProcessLauncher.cs");
            Assert.Equal(1, SourceAssert.Count(helper, "internal static ProcessOutcome Run("));
        }

        [Fact]
        public void EachCallerKeepsItsOwnOutcomeMapping()
        {
            // The launch is shared; what a timeout or a failed start MEANS is not.
            // TimeTravelService returns -1 with a message, PrDescriptionService
            // throws, and BlameService uses its own text - so consolidating these
            // would change behaviour even though the launch is identical.
            Assert.Contains("stderr = \"git timed out\"", RepoSource.Read("src", "GxMcp.Worker", "Services", "TimeTravelService.cs"));
            Assert.Contains("throw new TimeoutException", RepoSource.Read("src", "GxMcp.Worker", "Services", "PrDescriptionService.cs"));
            Assert.Contains("stderr = \"Failed to start git process.\"", RepoSource.Read("src", "GxMcp.Worker", "Services", "BlameService.cs"));
            Assert.Contains("stderr = \"gh timed out\"", RepoSource.Read("src", "GxMcp.Worker", "Services", "GithubService.cs"));
        }

        }
}
