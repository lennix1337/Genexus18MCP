using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Real-process smoke for the broker queue (RFC #854, task 3.11). It starts
    // GxMcp.SharedHost.TestHost as the broker (--shared-host) with its own executable as the
    // Worker, so the child is the scripted fake, and attaches two SharedWorkerConnection
    // clients. A second session's SDK request must queue in the broker behind the first and be
    // answered when the first completes, instead of WorkerBusy.
    //
    // The registry record goes to a temporary --registry-dir: on .NET Framework the broker's
    // LocalApplicationData lookup ignores an overridden LOCALAPPDATA variable, so the option is
    // the only way to keep this test out of the real %LOCALAPPDATA%\GxMcp registry.
    //
    // The smoke starts two .NET Framework processes. Run in parallel with the rest of the
    // assembly on a hosted runner it once took 14s instead of about 2.5s, so its collection runs
    // on its own, after the parallel ones, to keep process start-up out of CPU contention.
    [Trait("Category", "ProcessSmoke")]
    [Collection("SharedHostQueueProcessSmokeSerial")]
    public sealed class SharedHostQueueProcessSmokeTests
    {
        private const int TimeoutMs = 20_000;
        private const int FirstDurationMs = 1500;
        private const int MinMemoryFloorMb = 256; // SharedHostSettingsResolver range for MemoryFloorMB: 256-8192.

        private readonly ITestOutputHelper _output;

        public SharedHostQueueProcessSmokeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task A_Second_Sessions_Sdk_Request_Queues_Behind_The_First_And_Is_Answered()
        {
            if (!OperatingSystem.IsWindows()) return;
            string? exe = LocateTestHost();
            if (exe == null) return; // Build artifact gate; GXMCP_SHAREDHOST_TESTHOST_EXE makes it strict.

            string root = Path.Combine(Path.GetTempPath(), "gxmcp-queue-smoke-" + Guid.NewGuid().ToString("N"));
            string registryDir = Path.Combine(root, "registry");
            string kbPath = Path.Combine(root, "KbSmoke");
            string installation = Path.Combine(root, "install");
            Directory.CreateDirectory(kbPath);
            Directory.CreateDirectory(installation);

            var identity = SharedWorkerIdentity.Create(exe, kbPath, installation, "sdk", "18");
            string realRecord = SharedWorkerRegistry.GetRecordPathV2(identity.KbKey);
            var stderr = new StringBuilder();
            Process? broker = null;
            SharedWorkerConnection? a = null;
            SharedWorkerConnection? b = null;
            // On a hosted runner this test has taken 10-12s against about 2s locally; the phase
            // timings go to the TRX, and assert-test-durations.ps1 prints them when it is slow.
            var clock = Stopwatch.StartNew();
            long mark = 0;
            void Phase(string name)
            {
                long now = clock.ElapsedMilliseconds;
                _output.WriteLine("phase " + name + " " + (now - mark) + " ms (at " + now + " ms)");
                mark = now;
            }
            try
            {
                broker = StartBroker(exe, identity, kbPath, installation, registryDir, stderr);
                Phase("broker-process-start");
                // Registry v2 (decision A4): one record per KB directory, <kbKey>.json under --registry-dir.
                string recordPath = SharedWorkerRegistry.GetRecordPathV2(identity.KbKey, registryDir);
                SharedHostRecord record = await WaitForRecordAsync(recordPath, broker, stderr);
                Phase("registry-record");
                Assert.Equal(SharedWorkerRegistry.PipeNameV2(identity), record.PipeName);
                Assert.Equal(identity.Key, record.Identity.Key);
                Assert.False(File.Exists(realRecord), "the smoke wrote the real registry record " + realRecord);

                var toA = new ConcurrentQueue<JObject>();
                var toB = new ConcurrentQueue<JObject>();
                a = Connect(identity, record, "smoke-a", toA);
                b = Connect(identity, record, "smoke-b", toB);
                Phase("connect-two-sessions");
                Assert.Equal(a.HostPid, b.HostPid);
                Assert.Equal(a.WorkerPid, b.WorkerPid);
                Assert.NotEqual(a.AttachId, b.AttachId);

                await WaitForStatusAsync(a, s => (string?)s["childState"] == "ready", "a ready child");
                Phase("child-ready");

                // Both sessions use id 1: the broker keeps them apart.
                await a.SendAsync(Probe(FirstDurationMs), CancellationToken.None);
                JObject runningStatus = await WaitForStatusAsync(a, s => s["running"] is JObject, "a running request");
                Assert.Equal(a.AttachId, (string?)runningStatus["running"]!["ownerAttachId"]);
                Phase("first-request-running");

                await b.SendAsync(Probe(50), CancellationToken.None);
                JObject queuedStatus = await WaitForStatusAsync(b, s => ((JArray)s["queue"]!).Count == 1, "a queued request");
                var entry = (JObject)((JArray)queuedStatus["queue"]!)[0];
                Assert.Equal(b.AttachId, (string?)entry["ownerAttachId"]);
                Assert.Equal("sta-busy", (string?)entry["waitReason"]);
                Assert.Equal(a.AttachId, (string?)queuedStatus["running"]!["ownerAttachId"]);
                Phase("second-request-queued");

                JObject first = await WaitForResponseAsync(toA, broker, stderr);
                JObject second = await WaitForResponseAsync(toB, broker, stderr);
                Phase("both-responses");

                foreach (var response in new[] { first, second })
                {
                    Assert.Equal(1, (int)response["id"]!);
                    Assert.Null(response["error"]);
                    Assert.NotEqual("WorkerBusy", (string?)response["result"]?["code"]);
                    Assert.True(response["result"]?["fake"]?.Value<bool>() == true, response.ToString());
                }
                Assert.Equal(a.AttachId, (string?)first["result"]!["attachmentId"]);
                Assert.Equal(b.AttachId, (string?)second["result"]!["attachmentId"]);
                // One SDK lane: the second ran after the first, on the same child.
                Assert.True((int)second["result"]!["sequence"]! > (int)first["result"]!["sequence"]!);
                Assert.Equal((int)first["result"]!["pid"]!, (int)second["result"]!["pid"]!);

                // The broker measured the wait, and SharedWorkerConnection moved it to _meta.sharedHost.
                long firstWait = (long)first["result"]!["_meta"]!["sharedHost"]!["queueWaitMs"]!;
                long secondWait = (long)second["result"]!["_meta"]!["sharedHost"]!["queueWaitMs"]!;
                Assert.Equal(0L, firstWait);
                Assert.True(secondWait >= 1000, "the second request waited only " + secondWait + " ms");
                Assert.Null(second[SharedWorkerConnection.SharedMetadataKey]);
            }
            catch (Exception ex) when (!(ex is Xunit.Sdk.XunitException))
            {
                throw new InvalidOperationException("smoke failed; broker stderr:\n" + Tail(stderr), ex);
            }
            finally
            {
                a?.Dispose();
                b?.Dispose();
                if (broker != null)
                {
                    try { if (!broker.HasExited) broker.Kill(entireProcessTree: true); } catch { }
                    try { broker.WaitForExit(5000); } catch { }
                    broker.Dispose();
                }
                Phase("broker-shutdown");
                for (int i = 0; i < 10 && Directory.Exists(root); i++)
                {
                    try { Directory.Delete(root, recursive: true); }
                    catch (IOException) { await Task.Delay(200); }
                    catch (UnauthorizedAccessException) { await Task.Delay(200); }
                }
                Phase("temp-cleanup");
            }
        }

        private static JObject Probe(int durationMs) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "probe",
            ["params"] = new JObject { ["fakeDurationMs"] = durationMs }
        };

        internal static Process StartBroker(string exe, SharedWorkerIdentity identity, string kbPath, string installation, string registryDir, StringBuilder stderr)
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--shared-host"
                    + " --identity-key \"" + identity.Key + "\""
                    + " --pipe-name \"" + SharedWorkerRegistry.PipeNameV2(identity) + "\""
                    + " --worker-executable \"" + exe + "\""
                    + " --kb \"" + kbPath + "\""
                    + " --installation \"" + installation + "\""
                    + " --driver sdk --major 18"
                    + " --idle-ms 60000"
                    + " --queue-wait-ms 30000 --queue-depth 8 --session-queue-cap 4"
                    // The smoke tests queueing, not admission: the lowest allowed Memory_Floor
                    // (256 MB) keeps a busy machine from refusing the start with low-memory.
                    + " --memory-floor-mb " + MinMemoryFloorMb
                    + " --registry-dir \"" + registryDir + "\"",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // The broker passes its environment to the child, which reads the script from it.
            start.Environment["GXMCP_TESTHOST_SCRIPT"] = "{\"readyDelayMs\":100}";
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + exe);
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            _ = process.StandardOutput.ReadToEndAsync();
            return process;
        }

        internal static async Task<SharedHostRecord> WaitForRecordAsync(string path, Process broker, StringBuilder stderr)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (broker.HasExited)
                    throw new InvalidOperationException("the broker exited with " + broker.ExitCode + ":\n" + Tail(stderr));
                SharedHostRecord? record = RecordReadResult.ReadFile(path).Record;
                if (record != null && record.WorkerPid > 0 && !string.IsNullOrEmpty(record.PipeName)) return record;
                await Task.Delay(50);
            }
            throw new TimeoutException("no registry record at " + path + ":\n" + Tail(stderr));
        }

        private static SharedWorkerConnection Connect(SharedWorkerIdentity identity, SharedHostRecord record, string clientId, ConcurrentQueue<JObject> sink)
        {
            var connection = new SharedWorkerConnection(identity, record, clientId);
            connection.LineReceived += line =>
            {
                var frame = JObject.Parse(line);
                if (frame["id"] != null && frame["method"] == null) sink.Enqueue(frame);
            };
            connection.Connect(TimeoutMs);
            return connection;
        }

        private static async Task<JObject> WaitForStatusAsync(SharedWorkerConnection connection, Func<JObject, bool> match, string what)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            JObject? last = null;
            while (DateTime.UtcNow < deadline)
            {
                last = await connection.RequestStatusAsync(2000);
                if (last != null && match(last)) return last;
                await Task.Delay(25);
            }
            throw new TimeoutException("status never showed " + what + "; last: " + last);
        }

        private static async Task<JObject> WaitForResponseAsync(ConcurrentQueue<JObject> sink, Process broker, StringBuilder stderr)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (sink.TryDequeue(out JObject? frame)) return frame;
                if (broker.HasExited) throw new InvalidOperationException("the broker exited:\n" + Tail(stderr));
                await Task.Delay(20);
            }
            throw new TimeoutException("no response within " + TimeoutMs + " ms:\n" + Tail(stderr));
        }

        private static string Tail(StringBuilder stderr)
        {
            lock (stderr)
            {
                string text = stderr.ToString();
                return text.Length <= 4000 ? text : text.Substring(text.Length - 4000);
            }
        }

        // Same rule as SharedHostTestHostSmokeTests: the variable wins, then bin\Debug or bin\Release.
        internal static string? LocateTestHost()
        {
            string? configured = Environment.GetEnvironmentVariable(SharedHostTestHostSmokeTests.ExecutableVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(configured))
                    throw new InvalidOperationException(
                        SharedHostTestHostSmokeTests.ExecutableVariable + " must point to an existing absolute executable: " + configured);
                return Path.GetFullPath(configured);
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Genexus18MCP.sln")))
                directory = directory.Parent;
            if (directory == null) return null;
            return new[] { "Debug", "Release" }
                .Select(c => Path.Combine(directory.FullName, "src", "GxMcp.SharedHost.TestHost", "bin", c, "GxMcp.SharedHost.TestHost.exe"))
                .FirstOrDefault(File.Exists);
        }
    }

    [CollectionDefinition("SharedHostQueueProcessSmokeSerial", DisableParallelization = true)]
    public sealed class SharedHostQueueProcessSmokeSerialCollection { }
}
