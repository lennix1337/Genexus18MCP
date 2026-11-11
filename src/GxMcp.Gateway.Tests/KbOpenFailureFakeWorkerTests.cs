using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // https://github.com/lennix1337/Genexus18MCP/issues/957
    // The real MCP path (ProcessMcpRequest -> worker pool -> WorkerProcess command queue ->
    // response routing) with a fake Worker behind the queue: no SDK, no KB. The fake answers
    // GetIndexState the way KbService/CommandDispatcher do once the Worker's own open failed.
    [Collection("Gateway route state")]
    public sealed class KbOpenFailureFakeWorkerTests : IDisposable
    {
        private const string SdkReady = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gxmcp-issue957-" + Guid.NewGuid().ToString("N"));
        private readonly List<IDisposable> _disposables = new();
        private readonly ConcurrentDictionary<string, FakeWorkerState> _workers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<WorkerProcess> _spawned = new();
        private readonly string _session = "issue957-" + Guid.NewGuid().ToString("N");
        private sealed class FakeWorkerState
        {
            public JObject? Failure;
            public bool Silent;
            public int GetIndexStateCount;
            public int OtherCommandCount;
        }

        private string MakeKb(string name)
        {
            string kb = Path.Combine(_directory, name);
            Directory.CreateDirectory(kb);
            File.WriteAllText(Path.Combine(kb, name + ".gxw"), "");
            return kb;
        }

        private FakeWorkerState Fake(string kbDirectory) =>
            _workers.GetOrAdd(Path.GetFileName(kbDirectory), _ => new FakeWorkerState());

        private void Start(bool sdkReadyAtSpawn = true)
        {
            Program.ResetIndexStateMirrorForTest();
            // The index bootstrap sends its own Worker commands; keep them out of the probe's counts.
            Program.IndexBootstrapTriggerForTest = () => { };
            Directory.CreateDirectory(_directory);
            string configPath = Path.Combine(_directory, "config.json");
            File.WriteAllText(configPath, "{}");
            var config = new Configuration
            {
                Environment = new EnvironmentConfig { ResolutionPolicy = "strict" }
            };
            _disposables.Add(Program.ConfigureRouteStateForTest(config, configPath, handle =>
            {
                var worker = new WorkerProcess(config, handle);
                worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
                FakeWorkerState state = _workers.GetOrAdd(Path.GetFileName(handle.Path.TrimEnd('\\', '/')), _ => new FakeWorkerState());
                worker.FakeWorkerForTest = rpc => Answer(state, rpc);
                lock (_spawned) _spawned.Add(worker);
                if (sdkReadyAtSpawn) worker.HandleWorkerRpcResponseForTest(SdkReady);
                return worker;
            }));
            _disposables.Add(Program.AttachWorkerResponseRoutingForTest());
        }

        private static JObject? Answer(FakeWorkerState state, JObject rpc)
        {
            bool isIndexState = rpc["method"]?.ToString() == "kb" && rpc["action"]?.ToString() == "GetIndexState";
            if (isIndexState) Interlocked.Increment(ref state.GetIndexStateCount);
            else Interlocked.Increment(ref state.OtherCommandCount);
            if (state.Silent) return null;
            JObject result = isIndexState
                ? new JObject
                {
                    ["indexStatus"] = "Cold",
                    ["freshness"] = "stale",
                    ["totalObjects"] = 0,
                    ["kbOpenFailure"] = state.Failure?.DeepClone()
                }
                : new JObject();
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"], ["result"] = result };
        }

        private static JObject Failure(string path) => new JObject
        {
            ["code"] = "KbOpenFailed",
            ["message"] = "InvalidDirectory",
            ["path"] = path,
            ["atUtc"] = "2026-11-10T12:00:00.0000000Z"
        };

        private async Task<(string Text, bool IsError)> Call(string tool, JObject args)
        {
            var response = await Program.ProcessMcpRequest(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "issue957-" + Guid.NewGuid().ToString("N"),
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = tool, ["arguments"] = args }
            }, _session);
            return (response!["result"]!["content"]![0]!["text"]!.ToString(),
                response["result"]!["isError"]?.Value<bool>() ?? false);
        }

        private Task<(string Text, bool IsError)> Open(string kbDirectory) =>
            Call("genexus_kb", new JObject { ["action"] = "open", ["path"] = kbDirectory });

        [Fact]
        public async Task Open_reports_the_failure_the_worker_recorded_instead_of_opened()
        {
            Start();
            string kb = MakeKb("junction");
            Fake(kb).Failure = Failure(kb);

            var (text, isError) = await Open(kb);

            Assert.True(isError, text);
            var payload = JObject.Parse(text);
            Assert.Equal("KbOpenFailed", payload["code"]?.ToString());
            Assert.Equal("InvalidDirectory", payload["message"]?.ToString());
            Assert.Equal(kb, payload["path"]?.ToString());
            Assert.Null(payload["opened"]);
            Assert.DoesNotContain("\"opened\"", text);
            Assert.DoesNotContain("IndexNotReady", text);
        }

        [Fact]
        public async Task A_tool_blocked_by_the_index_reports_the_open_failure_without_IndexNotReady()
        {
            Start();
            string kb = MakeKb("autoopen");
            FakeWorkerState worker = Fake(kb);

            // The Worker's own startup open fails after the client already got "opened".
            var (openText, openError) = await Open(kb);
            Assert.False(openError, openText);
            Assert.Equal("autoopen", JObject.Parse(openText)["opened"]?.ToString());
            worker.Failure = Failure(kb);
            Program.InvalidateIndexStateForKb("autoopen");
            int otherBefore = Volatile.Read(ref worker.OtherCommandCount);

            var (text, isError) = await Call("genexus_list_objects", new JObject { ["kb"] = "autoopen" });

            Assert.True(isError, text);
            var payload = JObject.Parse(text);
            Assert.Equal("KbOpenFailed", payload["code"]?.ToString());
            Assert.Equal("InvalidDirectory", payload["message"]?.ToString());
            Assert.Equal(kb, payload["path"]?.ToString());
            Assert.DoesNotContain("IndexNotReady", text);
            Assert.DoesNotContain("\"opened\"", text);
            Assert.Equal(otherBefore, Volatile.Read(ref worker.OtherCommandCount));
        }

        [Fact]
        public async Task A_successful_open_clears_the_failure_of_the_previous_attempt()
        {
            Start();
            string kb = MakeKb("reopened");
            FakeWorkerState worker = Fake(kb);
            worker.Failure = Failure(kb);
            var (failedText, failed) = await Open(kb);
            Assert.True(failed, failedText);
            Assert.NotNull(Program.BuildKbOpenFailedEnvelopeForTest("reopened"));

            worker.Failure = null;
            var (text, isError) = await Open(kb);

            Assert.False(isError, text);
            Assert.Equal("reopened", JObject.Parse(text)["opened"]?.ToString());
            Assert.Null(Program.BuildKbOpenFailedEnvelopeForTest("reopened"));
        }

        [Fact]
        public async Task A_new_open_drops_the_earlier_failure_even_when_the_worker_does_not_answer()
        {
            Start();
            string kb = MakeKb("flaky");
            FakeWorkerState worker = Fake(kb);
            worker.Failure = Failure(kb);
            var (failedText, failed) = await Open(kb);
            Assert.True(failed, failedText);

            worker.Failure = null;
            worker.Silent = true;
            var (text, isError) = await Open(kb);

            Assert.False(isError, text);
            Assert.Equal("flaky", JObject.Parse(text)["opened"]?.ToString());
            Assert.Null(Program.BuildKbOpenFailedEnvelopeForTest("flaky"));
        }

        [Fact]
        public async Task A_failure_of_another_path_does_not_contaminate_a_new_open()
        {
            Start();
            string broken = MakeKb("broken");
            string healthy = MakeKb("healthy");
            Fake(broken).Failure = Failure(broken);

            var (brokenText, brokenError) = await Open(broken);
            Assert.True(brokenError, brokenText);
            var (text, isError) = await Open(healthy);

            Assert.False(isError, text);
            var payload = JObject.Parse(text);
            Assert.Equal("healthy", payload["opened"]?.ToString());
            Assert.Null(payload["code"]);
            Assert.Null(Program.BuildKbOpenFailedEnvelopeForTest("healthy"));
            Assert.Equal(broken, Program.BuildKbOpenFailedEnvelopeForTest("broken")!["path"]?.ToString());
        }

        [Fact]
        public async Task An_unanswered_probe_respects_the_ceiling_and_leaves_no_pending_request()
        {
            Start(sdkReadyAtSpawn: false);
            string kb = MakeKb("slowstart");
            FakeWorkerState worker = Fake(kb);
            int pendingBefore = Program.PendingRequestCountForTest;

            var clock = Stopwatch.StartNew();
            var (text, isError) = await Open(kb);
            clock.Stop();

            Assert.False(isError, text);
            Assert.Equal("slowstart", JObject.Parse(text)["opened"]?.ToString());
            Assert.True(clock.ElapsedMilliseconds < Program.KbOpenFailureProbeCeilingMs + 1500,
                $"open took {clock.ElapsedMilliseconds} ms, ceiling is {Program.KbOpenFailureProbeCeilingMs} ms");
            Assert.Equal(pendingBefore, Program.PendingRequestCountForTest);

            // The cancelled probe must not wake up when the Worker becomes ready later.
            Volatile.Write(ref worker.GetIndexStateCount, 0);
            lock (_spawned) foreach (var w in _spawned) w.HandleWorkerRpcResponseForTest(SdkReady);
            await Task.Delay(500);
            Assert.Equal(0, Volatile.Read(ref worker.GetIndexStateCount));
            Assert.Equal(pendingBefore, Program.PendingRequestCountForTest);
        }

        [Fact]
        public async Task A_probe_cancelled_while_waiting_for_the_answer_removes_its_pending_request()
        {
            Start(sdkReadyAtSpawn: false);
            string kb = MakeKb("silent");
            FakeWorkerState worker = Fake(kb);
            worker.Silent = true;
            int pendingBefore = Program.PendingRequestCountForTest;

            // Ready after 1 s: the 1.5 s answer wait then runs past the 2 s ceiling.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                lock (_spawned) foreach (var w in _spawned) w.HandleWorkerRpcResponseForTest(SdkReady);
            });

            var clock = Stopwatch.StartNew();
            var (text, isError) = await Open(kb);
            clock.Stop();

            Assert.False(isError, text);
            Assert.Equal("silent", JObject.Parse(text)["opened"]?.ToString());
            Assert.True(clock.ElapsedMilliseconds < Program.KbOpenFailureProbeCeilingMs + 1500,
                $"open took {clock.ElapsedMilliseconds} ms, ceiling is {Program.KbOpenFailureProbeCeilingMs} ms");
            Assert.True(Volatile.Read(ref worker.GetIndexStateCount) >= 1, "the probe should have reached the Worker");
            Assert.Equal(pendingBefore, Program.PendingRequestCountForTest);
        }

        public void Dispose()
        {
            Program.IndexBootstrapTriggerForTest = null;
            Program.ClearSessionSelectedKb(_session);
            for (int i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}
