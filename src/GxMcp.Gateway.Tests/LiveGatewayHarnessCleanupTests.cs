using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LiveGatewayHarnessCleanupTests
    {
        [Fact]
        public async Task TimeoutCleanup_stops_owned_child_without_touching_unrelated_process()
        {
            using var unrelated = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var parent = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"$child = Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; Write-Output $child.Id; Start-Sleep -Seconds 60\"",
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
                LiveGatewayHarness.StopOwnedProcess(parent);
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
    }
}
