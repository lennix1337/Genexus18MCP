using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Smoke for src/GxMcp.SharedHost.TestHost, the SDK-free process host the shared-host
    // process harness starts (Requirement 13 criteria 3 to 5). It proves the binary runs as
    // the scripted fake child over the broker's child stdio framing, and that --shared-host
    // dispatches to the real broker entry point.
    [Trait("Category", "ProcessSmoke")]
    public sealed class SharedHostTestHostSmokeTests
    {
        internal const string ExecutableVariable = "GXMCP_SHAREDHOST_TESTHOST_EXE";
        private const int TimeoutMs = 5_000;

        [Fact]
        public async Task FakeChild_EmitsSdkReady_AnswersScriptedRequest_AndExitsOnCommand()
        {
            if (!OperatingSystem.IsWindows()) return;
            string? exe = FindTestHostExe();
            if (exe == null) return; // Build artifact gate; collect.ps1 sets the variable, which makes it strict.

            using var process = Start(exe, "--kb \"C:\\KBs\\KbA\" --driver sdk --major 18",
                "{\"readyDelayMs\":150,\"durations\":{\"probe\":40}}");
            try
            {
                await SendAsync(process, "{\"jsonrpc\":\"2.0\",\"id\":\"gxmcp-a-1\",\"method\":\"probe\",\"_meta\":{\"attachmentId\":\"a\"}}");

                JObject ready = await ReadFrameAsync(process, frame => frame.Value<string>("method") == "notifications/worker/sdk_ready");
                Assert.Equal("C:\\KBs\\KbA", ready["params"]?["kb"]?.ToString());

                JObject answer = await ReadFrameAsync(process, frame => frame["id"]?.ToString() == "gxmcp-a-1");
                Assert.Null(answer["error"]);
                Assert.Equal("probe", answer["result"]?["method"]?.ToString());
                Assert.Equal(40, answer["result"]?["scriptedDurationMs"]?.Value<int>());
                Assert.Equal("a", answer["result"]?["attachmentId"]?.ToString());
                Assert.Equal(process.Id, answer["result"]?["pid"]?.Value<int>());

                await SendAsync(process, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"fake/exit\",\"params\":{\"exitCode\":7}}");
                JObject exiting = await ReadFrameAsync(process, frame => frame["id"]?.Type == JTokenType.Integer && frame["id"]!.Value<int>() == 7);
                Assert.True(exiting["result"]?["exiting"]?.Value<bool>());

                Assert.True(process.WaitForExit(TimeoutMs), "The fake child did not exit on fake/exit.");
                Assert.Equal(7, process.ExitCode);
            }
            finally
            {
                Kill(process);
            }
        }

        [Fact]
        public void FakeChild_RejectsAnUnknownScriptKey()
        {
            if (!OperatingSystem.IsWindows()) return;
            string? exe = FindTestHostExe();
            if (exe == null) return;

            using var process = Start(exe, string.Empty, "{\"readyDelay\":10}");
            try
            {
                process.StandardInput.Close(); // a regression to "script accepted" must exit, not wait on stdin
                string stderr = process.StandardError.ReadToEnd();
                Assert.True(process.WaitForExit(TimeoutMs), "The fake child did not exit on a bad script.");
                Assert.Equal(2, process.ExitCode);
                Assert.Contains("unknown script key 'readyDelay'", stderr, StringComparison.Ordinal);
            }
            finally
            {
                Kill(process);
            }
        }

        // Pins the start path: --shared-host reaches SharedWorkerHost.Run, which validates its
        // options before it creates the runtime, so no registry record or pipe is created here.
        [Fact]
        public void SharedHostFlag_RunsTheRealBrokerEntryPoint()
        {
            if (!OperatingSystem.IsWindows()) return;
            string? exe = FindTestHostExe();
            if (exe == null) return;

            using var process = Start(exe, "--shared-host", script: null);
            try
            {
                process.StandardInput.Close(); // if the flag fell through to the fake child, it exits 0 on EOF
                string stderr = process.StandardError.ReadToEnd();
                Assert.True(process.WaitForExit(TimeoutMs), "The broker did not exit on missing options.");
                Assert.Equal(1, process.ExitCode);
                Assert.Contains("[SharedWorkerHost] startup failed", stderr, StringComparison.Ordinal);
            }
            finally
            {
                Kill(process);
            }
        }

        private static Process Start(string exe, string arguments, string? script)
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (script == null) start.Environment.Remove("GXMCP_TESTHOST_SCRIPT");
            else start.Environment["GXMCP_TESTHOST_SCRIPT"] = script;
            return Process.Start(start) ?? throw new InvalidOperationException("Could not start " + exe);
        }

        private static async Task SendAsync(Process process, string frame)
        {
            await process.StandardInput.WriteAsync(frame + "\n");
            await process.StandardInput.FlushAsync();
        }

        private static async Task<JObject> ReadFrameAsync(Process process, Func<JObject, bool> match)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(deadline - DateTime.UtcNow);
                if (line == null) break;
                if (line.StartsWith("WORKER_HANDSHAKE_", StringComparison.Ordinal)) continue;
                var frame = JObject.Parse(line);
                if (match(frame)) return frame;
            }
            throw new TimeoutException("The fake child did not write the expected frame within " + TimeoutMs + " ms.");
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeoutMs);
                }
            }
            catch (InvalidOperationException) { }
        }

        private static string? FindTestHostExe()
        {
            string? configured = Environment.GetEnvironmentVariable(ExecutableVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(configured))
                    throw new InvalidOperationException(
                        ExecutableVariable + " must point to an existing absolute executable: " + configured);
                return Path.GetFullPath(configured);
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Genexus18MCP.sln")))
                directory = directory.Parent;
            if (directory == null) return null;
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                string candidate = Path.Combine(directory.FullName, "src", "GxMcp.SharedHost.TestHost", "bin", configuration, "GxMcp.SharedHost.TestHost.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
