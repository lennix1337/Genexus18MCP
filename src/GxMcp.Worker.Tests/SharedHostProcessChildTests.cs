using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SharedHostProcessChildTests
    {
        [Fact]
        [Trait("Category", "ProcessSmoke")]
        public async Task Kill_Unblocks_A_Write_When_The_Child_Does_Not_Read_Stdin()
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::Out.WriteLine('ready'); Start-Sleep -Seconds 60\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            using (var child = new SharedHostProcessChild(process))
            {
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<IOException> write = null;
                Task kill = null;
                try
                {
                    Task<string> ready = child.StandardOutput.ReadLineAsync();
                    await CompletesWithin(ready, "Child did not report readiness.");
                    Assert.Equal("ready", await ready);
                    write = Task.Factory.StartNew(() =>
                    {
                        entered.SetResult(true);
                        try
                        {
                            child.WriteLine(new string('x', 4 * 1024 * 1024));
                            return (IOException)null;
                        }
                        catch (IOException error) { return error; }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    await CompletesWithin(entered.Task, "Stdin writer did not start.");
                    Assert.NotSame(write, await Task.WhenAny(write, Task.Delay(500)));

                    kill = Task.Factory.StartNew(child.Kill, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    await CompletesWithin(kill, "Kill waited behind a blocked stdin write.");
                    Assert.True(process.HasExited, "child was not terminated");
                    await CompletesWithin(write, "Child termination did not release the stdin writer.");
                    Assert.NotNull(await write);
                }
                finally
                {
                    // Independent cleanup also releases the writer when testing the broken implementation.
                    if (!process.HasExited) process.Kill();
                    process.WaitForExit(5000);
                    if (write != null) await CompletesWithin(write, "Cleanup did not release the stdin writer.");
                    if (kill != null) await CompletesWithin(kill, "Cleanup did not release Kill.");
                }
            }
        }

        private static async Task CompletesWithin(Task task, string failure)
        {
            Assert.True(task == await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))), failure);
            await task;
        }
    }
}
