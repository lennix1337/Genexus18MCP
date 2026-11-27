using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

[Trait("Category", "ProcessSmoke")]
public sealed class GatewayPanicLogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingConfig_ReportsStartupFailureWithoutWritingPanicLogToWorkingDirectory(bool blockedPanicLog)
    {
        const string expectedError = "GX_CONFIG_PATH points to non-existent config file";
        var root = Directory.CreateTempSubdirectory("gx-panic-");
        try
        {
            var workingDirectory = root.CreateSubdirectory("workspace").FullName;
            var logDirectory = root.CreateSubdirectory("logs").FullName;
            var panicLog = Path.Combine(logDirectory, "gateway_panic.log");
            if (blockedPanicLog)
                Directory.CreateDirectory(panicLog); // A file cannot be appended to a directory, even by an admin.

            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.Environment["GX_CONFIG_PATH"] = Path.Combine(root.FullName, "missing.json");
            startInfo.Environment["GXMCP_LOG_DIR"] = logDirectory;
            startInfo.Environment["GENEXUS_MCP_NO_SELF_UPDATE"] = "1";
            startInfo.Environment["GENEXUS_MCP_NO_UPDATE_CHECK"] = "1";

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Gateway.");
            try
            {
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);

                Assert.NotEqual(0, process.ExitCode);
                var errorOutput = await stderr;
                Assert.Contains(expectedError, errorOutput);
                var notification = JObject.Parse(await stdout);
                Assert.Equal("notifications/message", (string?)notification["method"]);
                Assert.Equal("error", (string?)notification["params"]?["level"]);
                Assert.Contains(expectedError, (string?)notification["params"]?["data"]);
                Assert.True(File.Exists(Path.Combine(logDirectory, "gateway_debug.log")), "Gateway did not use the test log directory.");
                Assert.False(File.Exists(Path.Combine(workingDirectory, "gateway_panic.log")), "Gateway wrote a panic log into its working directory.");

                if (blockedPanicLog)
                {
                    Assert.Contains("Could not write gateway panic log", errorOutput);
                    Assert.True(Directory.Exists(panicLog));
                }
                else
                {
                    var contents = File.ReadAllText(panicLog);
                    Assert.Contains("FATAL UNHANDLED", contents);
                    Assert.Contains(expectedError, contents);
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
