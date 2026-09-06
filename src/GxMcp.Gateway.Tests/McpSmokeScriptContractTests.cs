using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Contract test for scripts/mcp_smoke.ps1 — the diagnostic script users run via
    /// `genexus-mcp doctor --mcp-smoke` when their MCP connection is broken.
    ///
    /// Regression class this pins: first-party scripts that speak the gateway's HTTP
    /// protocol drifting out of sync with the gateway's header validation. That exact
    /// bug shipped in v2.38.0-v2.45.1: the smoke script omitted the required
    /// `Accept: application/json, text/event-stream` header, so the gateway's own
    /// ValidatePostHeaders rejected it with 406 and the doctor reported "smoke failed"
    /// on every healthy server — masking real connection problems.
    ///
    /// The unit tests on McpHttpProtocol cover the server side of that contract; this
    /// test covers the client side (our script) against a live in-process gateway.
    /// Skipped on non-Windows (the script is PowerShell) — same platform gate as the
    /// Worker test suite.
    /// </summary>
    public class McpSmokeScriptContractTests
    {
        private static bool IsWindows =>
            OperatingSystem.IsWindows();

        private static string FindRepoRoot()
        {
            // Walk up from the test assembly location until we find .git or CHANGELOG.md.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md"))
                    && (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                        || File.Exists(Path.Combine(dir.FullName, ".git"))))
                    return dir.FullName;
                dir = dir.Parent!;
            }
            return string.Empty;
        }

        [Fact]
        public async System.Threading.Tasks.Task McpSmokeScript_Succeeds_AgainstLiveGateway()
        {
            if (!IsWindows)
            {
                // mcp_smoke.ps1 requires PowerShell (Windows). Skip on other platforms.
                return;
            }
            string repoRoot = FindRepoRoot();
            if (string.IsNullOrEmpty(repoRoot))
            {
                return; // Repo root not found from test assembly.
            }

            string script = Path.Combine(repoRoot, "scripts", "mcp_smoke.ps1");
            Assert.True(File.Exists(script), $"mcp_smoke.ps1 not found at {script}");

            string gatewayExe = FindGatewayExe(repoRoot);
            Assert.True(File.Exists(gatewayExe),
                "Gateway exe not found. Searched bin/Debug and .test-bin/gateway — " +
                "run 'dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj' first.\n" +
                "Searched:\n" + gatewayExeCandidates(repoRoot));

            // Launch the just-built gateway with an ephemeral port and a scratch config
            // so the test never touches the user's running instance or KB. The config
            // mirrors what doctor's smoke targets: HTTP-only loopback, no stdio.
            // A port can be claimed between GetFreePort and gateway startup on a busy
            // CI runner. Retry only that bounded infrastructure race; protocol failures
            // still fail immediately and retain the full smoke-script assertions.
            const int maxStartupAttempts = 3;
            var startupFailures = new List<string>();
            for (int startupAttempt = 1; startupAttempt <= maxStartupAttempts; startupAttempt++)
            {
                int port = GetFreePort();
                string workDir = Path.Combine(Path.GetTempPath(), "gxmcp-smoketest-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(workDir);
                Process? proc = null;
                var gatewayOutput = new StringBuilder();
                var gatewayError = new StringBuilder();
                try
                {
                    string configPath = Path.Combine(workDir, "gateway.smoke.json");
                    File.WriteAllText(configPath, $@"
{{
  ""Server"": {{
    ""HttpPort"": {port},
    ""McpStdio"": false,
    ""WorkerIdleTimeoutMinutes"": 1
  }},
  ""Environment"": {{}}
}}
");

                    proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = gatewayExe,
                            Arguments = string.Empty,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            WorkingDirectory = Path.GetDirectoryName(gatewayExe)!,
                        }
                    };
                    proc.StartInfo.EnvironmentVariables["GX_CONFIG_PATH"] = configPath;
                    // Drain output so a chatty gateway can't block on a full pipe and keep
                    // it for diagnostics if startup fails.
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null) gatewayOutput.AppendLine(e.Data);
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null) gatewayError.AppendLine(e.Data);
                    };
                    bool started;
                    try
                    {
                        started = proc.Start();
                    }
                    catch (Exception ex)
                    {
                        startupFailures.Add($"tentativa {startupAttempt}/{maxStartupAttempts}, porta {port}: " +
                            $"falha ao iniciar ({ex.GetType().Name}: {ex.Message})");
                        continue;
                    }
                    Assert.True(started);
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    // Wait until the HTTP listener answers (gateway startup + lease check).
                    bool listening = await WaitForHttpAsync(port, timeoutSeconds: 30);
                    if (!listening)
                    {
                        string exitCode = proc.HasExited ? proc.ExitCode.ToString() : "em execução";
                        startupFailures.Add(
                            $"tentativa {startupAttempt}/{maxStartupAttempts}, porta {port}: " +
                            $"não respondeu em 30s (exitCode={exitCode}, stdout={gatewayOutput}, stderr={gatewayError})");
                        continue;
                    }

                    // THE CONTRACT: run the actual smoke script unmodified. If anyone
                    // tightens gateway header validation without updating the script —
                    // or loosens the script below the gateway's requirements — this fails.
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -BaseUrl \"http://127.0.0.1:{port}/mcp\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = repoRoot,
                    };
                    using var smoke = Process.Start(psi)!;
                    bool smokeExited = smoke.WaitForExit(120_000);
                    if (!smokeExited)
                    {
                        try { smoke.Kill(entireProcessTree: true); } catch { /* best effort */ }
                        smoke.WaitForExit(5_000);
                    }
                    string stdout = smoke.StandardOutput.ReadToEnd();
                    string stderr = smoke.StandardError.ReadToEnd();

                    Assert.True(smokeExited && smoke.ExitCode == 0,
                        "mcp_smoke.ps1 FAILED against a healthy gateway — first-party diagnostic " +
                        "script has drifted from the gateway's protocol contract.\n" +
                        "--- stdout ---\n" + stdout + "\n--- stderr ---\n" + stderr);
                    Assert.Contains("[SMOKE] PASS", stdout);
                    return;
                }
                finally
                {
                    if (proc != null)
                    {
                        try
                        {
                            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                        }
                        catch { /* best-effort cleanup */ }
                        proc.Dispose();
                    }
                    try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
                }
            }

            Assert.Fail(
                "Gateway did not start listening after " + maxStartupAttempts + " attempts.\n" +
                string.Join("\n", startupFailures));
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // Candidate exe locations: the standard build output and the coverage run's
        // BaseOutputPath (scripts/coverage/collect.ps1 redirects to .test-bin/gateway).
        private static System.Collections.Generic.IEnumerable<string> gatewayExeCandidates(string repoRoot)
        {
            yield return Path.Combine(repoRoot, "src", "GxMcp.Gateway", "bin", "Debug", "net10.0-windows", "GxMcp.Gateway.exe");
            yield return Path.Combine(repoRoot, "src", "GxMcp.Gateway", "bin", "Release", "net10.0-windows", "GxMcp.Gateway.exe");
            yield return Path.Combine(repoRoot, ".test-bin", "gateway", "Debug", "net10.0-windows", "GxMcp.Gateway.exe");
        }

        private static string FindGatewayExe(string repoRoot)
        {
            foreach (var candidate in gatewayExeCandidates(repoRoot))
            {
                if (File.Exists(candidate)) return candidate;
            }
            return string.Empty;
        }

        private static async System.Threading.Tasks.Task<bool> WaitForHttpAsync(int port, int timeoutSeconds)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var resp = await client.GetAsync($"http://127.0.0.1:{port}/mcp");
                    // Any HTTP answer means the listener is up (405/406/400 are fine —
                    // GET /mcp isn't a valid call but proves the socket is serving).
                    return true;
                }
                catch { await System.Threading.Tasks.Task.Delay(500); }
            }
            return false;
        }
    }
}
