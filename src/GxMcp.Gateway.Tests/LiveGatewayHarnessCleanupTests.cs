using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Trait("Category", "ProcessSmoke")]
    public class LiveGatewayHarnessCleanupTests
    {
        [Fact]
        public async Task TimeoutCleanup_stops_owned_child_without_touching_unrelated_process()
        {
            // The child and the unrelated process share one lightweight image (ping.exe), so cleanup
            // must stop the owned tree, not processes by name; only the parent needs PowerShell.
            using var unrelated = Process.Start(new ProcessStartInfo
            {
                FileName = "ping.exe",
                Arguments = "-n 61 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var parent = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"$child = Start-Process ping.exe -ArgumentList '-n 61 127.0.0.1' -WindowStyle Hidden -PassThru; Write-Output $child.Id; Start-Sleep -Seconds 60\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true
                }
            };
            Process? child = null;
            try
            {
                parent.Start();
                var childId = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                child = Process.GetProcessById(int.Parse(childId!));
                // The stub ignores stdin, so the graceful-exit wait would only add 2s of waiting.
                LiveGatewayHarness.StopOwnedProcess(parent, gracefulExitMs: 0);
                Assert.True(child.WaitForExit(5000), "The owned child outlived timeout cleanup.");
                Assert.False(unrelated.HasExited);
            }
            finally
            {
                // Every process here was created by this test; no name/path-wide cleanup.
                try { if (!parent.HasExited) parent.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                parent.Dispose();
                if (child != null)
                {
                    if (!child.HasExited) child.Kill();
                    child.Dispose();
                }
                if (!unrelated.HasExited) unrelated.Kill();
            }
        }

        [Fact]
        public void TimeoutDiagnostics_use_configured_timeout_and_redact_sensitive_values()
        {
            const string timeoutVariable = "GXMCP_LIVE_RPC_TIMEOUT_MS";
            string? previous = Environment.GetEnvironmentVariable(timeoutVariable);
            try
            {
                Environment.SetEnvironmentVariable(timeoutVariable, "4321");
                Assert.Equal(4321, LiveGatewayHarness.ResolveDefaultRpcTimeoutMs());

                Environment.SetEnvironmentVariable(timeoutVariable, "not-a-timeout");
                Assert.Equal(240_000, LiveGatewayHarness.ResolveDefaultRpcTimeoutMs());

                string diagnostics = LiveGatewayHarness.BuildRpcTimeoutDiagnostics(
                    "tools/call", 4321, processExited: true,
                    "Password=secret; token=abc123; useful failure", "C:\\logs\\gateway_debug.log");
                Assert.Contains("tools/call", diagnostics);
                Assert.Contains("4321ms", diagnostics);
                Assert.Contains("gateway_debug.log", diagnostics);
                Assert.DoesNotContain("secret", diagnostics, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("abc123", diagnostics, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(timeoutVariable, previous);
            }
        }

        [Fact]
        public void ProcessImage_assertion_accepts_the_actual_process_image()
        {
            using var current = Process.GetCurrentProcess();
            string image = current.MainModule?.FileName
                ?? throw new InvalidOperationException("Current process image is unavailable.");
            LiveGatewayHarness.AssertProcessImage(current, Path.GetFullPath(image));
        }
    }
}
