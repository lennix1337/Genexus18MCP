using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #965: a GeneXus 8.0/9.0 Worker (<c>com-gxpublic</c>) is launched against an
    /// installation that has no .NET SDK. It used to die in <c>Main</c> with a
    /// <c>FileNotFoundException</c> for <c>Artech.Architecture.Common</c> because building
    /// the command dispatcher (and its first command) resolved SDK types the legacy driver
    /// never uses. These tests run the real Worker executable against an empty installation
    /// directory, where any such load is fatal.
    /// </summary>
    public class LegacyComWorkerStartupTests
    {
        [Fact]
        public void ComDriverWorker_StartsAndAnswersCommands_WithoutLoadingSdkAssemblies()
        {
            var result = RunWorker(
                new Dictionary<string, string>(),
                "{\"jsonrpc\":\"2.0\",\"id\":\"p1\",\"method\":\"ping\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":\"r1\",\"method\":\"read\",\"action\":\"Read\",\"target\":\"Customer\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":\"q1\",\"method\":\"search\",\"action\":\"Query\",\"target\":\"Customer\"}");

            Assert.DoesNotContain("Artech.", result.Output);
            Assert.DoesNotContain("Main FATAL", result.Output);
            Assert.Equal("Pong", result.Responses["p1"]["result"]?["code"]?.ToString());
            // The read is answered by the COM driver (not an unhandled dispatcher failure).
            Assert.NotEqual("DispatcherException", result.Responses["r1"]["result"]?["error"]?["code"]?.ToString());
            Assert.NotEqual("DispatcherException", result.Responses["q1"]["result"]?["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ComDriverWorker_WithWriteDestinationPins_RefusesWritesWithoutLoadingSdkAssemblies()
        {
            var result = RunWorker(
                new Dictionary<string, string>
                {
                    ["GXMCP_EXPECTED_KB_PATH"] = @"C:\KBs\pinned-teste",
                    ["GXMCP_EXPECTED_KB_VERSION"] = "Trunk",
                },
                "{\"jsonrpc\":\"2.0\",\"id\":\"w1\",\"method\":\"write\",\"action\":\"Save\",\"target\":\"Customer\"}");

            Assert.DoesNotContain("Artech.", result.Output);
            Assert.Equal("WriteDestinationKbMismatch", result.Responses["w1"]["result"]?["error"]?["code"]?.ToString());
        }

        // The test output folder carries the GeneXus SDK assemblies (the Worker compiles against them), which
        // would hide the failure: a GeneXus 9 machine has none. Stage the Worker with only its own
        // third-party dependencies, so any attempt to load Artech.* has nowhere to resolve from.
        private static string StageWorkerWithoutSdk(string directory)
        {
            string source = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(directory);
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                bool wanted = name.StartsWith("GxMcp.Worker.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Npgsql.dll", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.Bcl.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase);
                if (wanted) File.Copy(file, Path.Combine(directory, name), true);
            }
            Assert.Empty(Directory.GetFiles(directory, "Artech.*"));
            return Path.Combine(directory, "GxMcp.Worker.exe");
        }

        private sealed class WorkerRun
        {
            public string Output;
            public Dictionary<string, JObject> Responses = new Dictionary<string, JObject>();
        }

        private static WorkerRun RunWorker(Dictionary<string, string> extraEnvironment, params string[] requests)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "gxmcp-965-" + Guid.NewGuid().ToString("N"));
            string exe = StageWorkerWithoutSdk(Path.Combine(scratch, "worker"));
            string fakeInstall = Path.Combine(scratch, "GeneXus90");
            string kb = Path.Combine(scratch, "kb");
            string logs = Path.Combine(scratch, "logs");
            Directory.CreateDirectory(fakeInstall);
            Directory.CreateDirectory(kb);
            Directory.CreateDirectory(logs);

            var start = new ProcessStartInfo(exe, "--kb \"" + kb + "\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.EnvironmentVariables["GX_PROGRAM_DIR"] = fakeInstall;
            start.EnvironmentVariables["GX_KB_PATH"] = kb;
            start.EnvironmentVariables["GXMCP_LOG_DIR"] = logs;
            start.EnvironmentVariables["GXMCP_DRIVER"] = "com-gxpublic";
            start.EnvironmentVariables["GXMCP_TARGET_MAJOR"] = "9";
            start.EnvironmentVariables.Remove("GXMCP_EXPECTED_KB_PATH");
            start.EnvironmentVariables.Remove("GXMCP_EXPECTED_KB_VERSION");
            foreach (var pair in extraEnvironment) start.EnvironmentVariables[pair.Key] = pair.Value;

            var run = new WorkerRun();
            var output = new System.Text.StringBuilder();
            var gate = new object();
            var pending = new HashSet<string>();
            foreach (string request in requests) pending.Add(JObject.Parse(request)["id"].ToString());
            var allAnswered = new ManualResetEventSlim(false);

            using (var process = new Process { StartInfo = start })
            {
                DataReceivedEventHandler onLine = (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (gate)
                    {
                        output.AppendLine(e.Data);
                        if (!e.Data.StartsWith("{")) return;
                        try
                        {
                            var json = JObject.Parse(e.Data);
                            string id = json["id"]?.ToString();
                            if (id != null && pending.Remove(id))
                            {
                                run.Responses[id] = json;
                                if (pending.Count == 0) allAnswered.Set();
                            }
                        }
                        catch (Newtonsoft.Json.JsonException) { }
                    }
                };
                process.OutputDataReceived += onLine;
                process.ErrorDataReceived += onLine;
                try
                {
                    Assert.True(process.Start(), "The Worker did not start.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    // Let startup finish (or fail) before the first command, as the Gateway does.
                    Thread.Sleep(3000);
                    foreach (string request in requests) process.StandardInput.WriteLine(request);
                    process.StandardInput.Flush();
                    bool answered = allAnswered.Wait(TimeSpan.FromSeconds(30));
                    lock (gate) run.Output = output.ToString();
                    Assert.True(answered, "The Worker did not answer every request (it may have crashed at startup). Exe: " + exe + " Output:\n" + run.Output);
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch (IOException) { }
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        process.WaitForExit(5000);
                    }
                    try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            return run;
        }
    }
}
