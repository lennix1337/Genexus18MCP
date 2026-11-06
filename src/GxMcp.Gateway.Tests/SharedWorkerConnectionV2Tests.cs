// Gateway-side tests for shared-host protocol version 2 (RFC #854, task 3.9).
//
// These tests are written before the implementation (task 3.12). They fix the
// contract below; task 3.12 implements exactly these members and names.
//
// Existing members whose behavior changes:
//   - SharedWorkerConnection.ProtocolVersion == 2. BuildAttachRequest, the heartbeat,
//     detach and status frames carry version/protocolVersion 2, and TryParseAttachAck
//     rejects a version 1 acknowledgement with "protocol version mismatch".
//   - SharedWorkerConnection read loop: a JSON-RPC response that carries a top-level
//     "_gxmcpShared" object is rewritten before LineReceived fires. The object moves to
//     result._meta.sharedHost (existing result._meta members are kept) and the
//     top-level "_gxmcpShared" key is removed. "status_ack" frames never reach
//     LineReceived.
//   - Program.ShouldRedispatchUndeliveredCommand is unchanged: a tool-result WorkerBusy
//     envelope (result.status="error") is never re-dispatched.
//
// New members:
//   - SharedWorkerConnection.RequestStatusAsync(int timeoutMs,
//         CancellationToken cancellationToken = default) : Task<JObject?>
//     Writes one frame { gxmcp="status", type="status", version=2, protocolVersion=2,
//     key, identityKey, attachId, attachmentId, requestId=<non-empty id> } and returns
//     the "status_ack" frame whose requestId matches. Returns null when no matching
//     acknowledgement arrives within timeoutMs; a timeout leaves the attachment
//     connected, and a late acknowledgement is dropped (not passed to LineReceived).
//     timeoutMs <= 0 throws ArgumentOutOfRangeException.
//   - SharedWorkerConnection.TryMoveSharedMetadata(JObject response) : bool (static)
//     The pure rewrite used by the read loop. Returns true when "_gxmcpShared" was
//     present and moved, false (response unchanged) otherwise.
//   - WorkerProcess.ResolvePendingTimeoutMs(int baseTimeoutMs, bool isSdkOperation) : int
//     Isolated_Mode returns baseTimeoutMs unchanged. Shared_Host_Mode extends the
//     timeout of an SDK_Operation by Configuration.EffectiveSharedHost.QueueWaitMs,
//     saturating at int.MaxValue; a non-SDK command and baseTimeoutMs <= 0 (no timeout)
//     are unchanged.
//   - WorkerProcess.BuildSharedHostDisconnectError(string kbAlias, string? method,
//         string? action, int queueWaitMs) : JObject (static)
//     The JSON-RPC error object for a request written to the shared-host pipe and
//     unanswered when the host disconnected: { code=-32603, message=<non-empty>,
//     data=<ConcurrencyErrorCatalog.Build(SHARED_WORKER_RESTARTED, null,
//     { DispatchState=delivered-unanswered, OperationClass=WorkerCommandCatalog.Classify(
//     method, action), QueueWaitMs=queueWaitMs })> }. data may carry extra keys.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class SharedWorkerConnectionV2Tests
    {
        private const int IoTimeoutMs = 5000;

        private static SharedWorkerIdentity Identity() => SharedWorkerIdentity.Create(
            @"C:\Worker\GxMcp.Worker.exe",
            @"C:\KBs\KbA",
            @"C:\GeneXus18",
            "native-sdk",
            "18");

        // ---------------------------------------------------------------- handshake

        [Fact]
        public void ProtocolVersionIsTwo()
        {
            Assert.Equal(2, SharedWorkerConnection.ProtocolVersion);
        }

        [Fact]
        public void AttachRequestCarriesProtocolVersionTwo()
        {
            JObject frame = SharedWorkerConnection.BuildAttachRequest(Identity(), "client-a", 123, 456, "nonce-a");

            Assert.Equal(2, frame.Value<int>("version"));
            Assert.Equal(2, frame.Value<int>("protocolVersion"));
        }

        [Fact]
        public void AttachAckWithProtocolVersionOneIsRejected()
        {
            var identity = Identity();
            string line = AttachAck(identity, version: 1).ToString(Formatting.None);

            Assert.False(SharedWorkerConnection.TryParseAttachAck(line, identity, out _, out string error));
            Assert.Contains("protocol version", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AttachAckWithProtocolVersionTwoIsAccepted()
        {
            var identity = Identity();
            string line = AttachAck(identity, version: 2).ToString(Formatting.None);

            Assert.True(SharedWorkerConnection.TryParseAttachAck(line, identity, out var info, out string error));
            Assert.Empty(error);
            Assert.Equal("attach-a", info!.AttachId);
        }

        // Task 5.6, design "Protocol v2 frames": a host without a Worker child yet acknowledges the attach with
        // workerPid and workerStartTimeUtcTicks both 0 and its child state.
        [Theory]
        [InlineData("admitting")]
        [InlineData("held")]
        [InlineData("refused")]
        [InlineData("no-worker")]
        [InlineData("recovering")]
        [InlineData("replacing")]
        public void AttachAckBeforeTheWorkerExistsIsAcceptedWithItsState(string state)
        {
            var identity = Identity();
            string line = PreWorkerAck(identity, state).ToString(Formatting.None);

            Assert.True(SharedWorkerConnection.TryParseAttachAck(line, identity, out var info, out string error), error);
            Assert.Equal(0, info!.WorkerPid);
            Assert.Equal(0L, info.WorkerStartTimeUtcTicks);
            Assert.Equal(state, info.State);
            Assert.False(info.SdkReady);
        }

        [Theory]
        [InlineData(null, 0, 0L, false)]          // no state: a zero Worker identity stays invalid
        [InlineData("ready", 0, 0L, false)]       // a ready host always has a Worker
        [InlineData("starting", 0, 0L, false)]    // a starting record names its child
        [InlineData("held", 0, 13L, false)]       // partial Worker identity
        [InlineData("held", 12, 0L, false)]       // partial Worker identity
        [InlineData("held", -1, -1L, false)]      // negative identity
        [InlineData("held", 0, 0L, true)]         // ready without a Worker
        public void AttachAckWithAnIncompleteWorkerIdentityIsStillRejected(string? state, int workerPid, long workerStart, bool ready)
        {
            var identity = Identity();
            var ack = PreWorkerAck(identity, state);
            ack["workerPid"] = workerPid;
            ack["workerStartTimeUtcTicks"] = workerStart;
            ack["ready"] = ready;

            Assert.False(SharedWorkerConnection.TryParseAttachAck(ack.ToString(Formatting.None), identity, out _, out string error));
            Assert.Contains("missing process identity", error, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AHeldAttachLearnsTheWorkerPidFromWorkerRestarted()
        {
            var identity = Identity();
            await using var host = FakeHost.Start(identity, ackVersion: 2, ack: () => PreWorkerAck(identity, "held"));
            using var connection = host.CreateConnection();

            connection.Connect(IoTimeoutMs);
            Assert.True(connection.IsConnected);
            Assert.Equal(0, connection.WorkerPid);

            await host.SendAsync(WorkerRestarted(identity, workerPid: 21, workerStart: 22, generation: 3));

            await WaitUntil(() => connection.WorkerPid == 21);
            Assert.True(connection.IsConnected);
            Assert.Equal(3, connection.Generation);
        }

        [Fact]
        public async Task WorkerRestartedWithoutAWorkerIdentityStillDisconnects()
        {
            var identity = Identity();
            await using var host = FakeHost.Start(identity, ackVersion: 2, ack: () => PreWorkerAck(identity, "held"));
            using var connection = host.CreateConnection();
            connection.Connect(IoTimeoutMs);

            await host.SendAsync(WorkerRestarted(identity, workerPid: 0, workerStart: 0, generation: 3));

            await WaitUntil(() => !connection.IsConnected);
            Assert.Equal(0, connection.WorkerPid);
        }

        private static JObject PreWorkerAck(SharedWorkerIdentity identity, string? state)
        {
            var ack = AttachAck(identity, version: 2);
            ack["workerPid"] = 0;
            ack["workerStartTimeUtcTicks"] = 0;
            ack["generation"] = 1;
            ack["ready"] = false;
            if (state != null) ack["state"] = state;
            return ack;
        }

        private static JObject WorkerRestarted(SharedWorkerIdentity identity, int workerPid, long workerStart, long generation) => new JObject
        {
            ["gxmcp"] = "worker_restarted",
            ["type"] = "worker_restarted",
            ["version"] = 2,
            ["protocolVersion"] = 2,
            ["key"] = identity.Key,
            ["identityKey"] = identity.Key,
            ["hostPid"] = 10,
            ["hostStartTimeUtcTicks"] = 11,
            ["workerPid"] = workerPid,
            ["workerStartTimeUtcTicks"] = workerStart,
            ["generation"] = generation,
            ["ready"] = false,
            ["failureStage"] = "worker_start"
        };

        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(IoTimeoutMs);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "the condition was not met within " + IoTimeoutMs + " ms");
                await Task.Delay(20);
            }
        }

        [Fact]
        public async Task ConnectPerformsVersionTwoHandshakeOverThePipe()
        {
            await using var host = FakeHost.Start(Identity(), ackVersion: 2);
            using var connection = host.CreateConnection();

            connection.Connect(IoTimeoutMs);

            JObject attach = await host.AttachFrame.WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            Assert.Equal("attach", attach["gxmcp"]?.ToString());
            Assert.Equal(2, attach.Value<int>("version"));
            Assert.Equal(2, attach.Value<int>("protocolVersion"));
            Assert.True(connection.IsConnected);
            Assert.Equal("attach-a", connection.AttachId);
        }

        // ---------------------------------------------------------------- status

        [Fact]
        public async Task RequestStatusAsyncReturnsTheMatchingStatusAck()
        {
            await using var host = FakeHost.Start(Identity(), ackVersion: SharedWorkerConnection.ProtocolVersion);
            host.AnswerStatus = true;
            using var connection = host.CreateConnection();
            connection.Connect(IoTimeoutMs);

            JObject? ack = await connection.RequestStatusAsync(1000);

            Assert.NotNull(ack);
            Assert.Equal("status_ack", ack!["gxmcp"]?.ToString());
            JObject status = await host.NextFrame("status").WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            Assert.Equal(2, status.Value<int>("version"));
            Assert.Equal("attach-a", status["attachId"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(status["requestId"]?.ToString()));
            Assert.Equal(status["requestId"]?.ToString(), ack["requestId"]?.ToString());
        }

        [Fact]
        public async Task RequestStatusAsyncReturnsNullOnTimeoutAndStaysConnected()
        {
            await using var host = FakeHost.Start(Identity(), ackVersion: SharedWorkerConnection.ProtocolVersion);
            host.AnswerStatus = false;
            using var connection = host.CreateConnection();
            connection.Connect(IoTimeoutMs);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            JObject? ack = await connection.RequestStatusAsync(200);
            watch.Stop();

            Assert.Null(ack);
            Assert.True(watch.ElapsedMilliseconds >= 150, "returned before the timeout: " + watch.ElapsedMilliseconds + " ms");
            Assert.True(watch.ElapsedMilliseconds < 3000, "timeout not honored: " + watch.ElapsedMilliseconds + " ms");
            Assert.True(connection.IsConnected);
        }

        [Fact]
        public async Task LateStatusAckIsNotForwardedAsAWorkerLine()
        {
            await using var host = FakeHost.Start(Identity(), ackVersion: SharedWorkerConnection.ProtocolVersion);
            host.AnswerStatus = false;
            using var connection = host.CreateConnection();
            var lines = new ConcurrentQueue<string>();
            var rpcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.LineReceived += line =>
            {
                lines.Enqueue(line);
                if (line.Contains("\"rpc-after-late-ack\"", StringComparison.Ordinal)) rpcSeen.TrySetResult(true);
            };
            connection.Connect(IoTimeoutMs);

            Assert.Null(await connection.RequestStatusAsync(100));
            JObject status = await host.NextFrame("status").WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            await host.SendAsync(new JObject
            {
                ["gxmcp"] = "status_ack",
                ["type"] = "status_ack",
                ["version"] = SharedWorkerConnection.ProtocolVersion,
                ["requestId"] = status["requestId"]?.ToString()
            });
            await host.SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = "rpc-after-late-ack", ["result"] = new JObject() });

            await rpcSeen.Task.WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            Assert.DoesNotContain(lines, l => l.Contains("status_ack", StringComparison.Ordinal));
            Assert.True(connection.IsConnected);
        }

        [Fact]
        public async Task RequestStatusAsyncRejectsANonPositiveTimeout()
        {
            await using var host = FakeHost.Start(Identity(), ackVersion: SharedWorkerConnection.ProtocolVersion);
            using var connection = host.CreateConnection();
            connection.Connect(IoTimeoutMs);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.RequestStatusAsync(0));
        }

        // ---------------------------------------------------------------- _gxmcpShared

        [Fact]
        public void SharedMetadataMovesIntoResultMetaAndLeavesThePayload()
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "r1",
                ["result"] = new JObject
                {
                    ["status"] = "ok",
                    ["_meta"] = new JObject { ["cacheOutcome"] = "miss" }
                },
                ["_gxmcpShared"] = new JObject { ["queueWaitMs"] = 1234, ["workerPid"] = 4321 }
            };

            Assert.True(SharedWorkerConnection.TryMoveSharedMetadata(response));

            Assert.Null(response["_gxmcpShared"]);
            Assert.Equal(1234, response["result"]?["_meta"]?["sharedHost"]?.Value<int>("queueWaitMs"));
            Assert.Equal(4321, response["result"]?["_meta"]?["sharedHost"]?.Value<int>("workerPid"));
            Assert.Equal("miss", response["result"]?["_meta"]?["cacheOutcome"]?.ToString());
            Assert.Equal("ok", response["result"]?["status"]?.ToString());
        }

        [Fact]
        public void ResponseWithoutSharedMetadataIsUnchanged()
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "r2",
                ["result"] = new JObject { ["status"] = "ok" }
            };
            var before = (JObject)response.DeepClone();

            Assert.False(SharedWorkerConnection.TryMoveSharedMetadata(response));
            Assert.True(JToken.DeepEquals(before, response));
        }

        [Fact]
        public async Task ReadLoopDeliversResponsesWithSharedMetadataMoved()
        {
            await using var host = FakeHost.Start(Identity(), ackVersion: SharedWorkerConnection.ProtocolVersion);
            using var connection = host.CreateConnection();
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.LineReceived += line => received.TrySetResult(line);
            connection.Connect(IoTimeoutMs);

            await host.SendAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "r3",
                ["result"] = new JObject { ["status"] = "ok" },
                ["_gxmcpShared"] = new JObject { ["queueWaitMs"] = 50, ["workerPid"] = 777 }
            });

            JObject line = JObject.Parse(await received.Task.WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs)));
            Assert.Null(line["_gxmcpShared"]);
            Assert.Equal(50, line["result"]?["_meta"]?["sharedHost"]?.Value<int>("queueWaitMs"));
            Assert.Equal(777, line["result"]?["_meta"]?["sharedHost"]?.Value<int>("workerPid"));
        }

        // ---------------------------------------------------------------- pending timeout

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void IsolatedPendingTimeoutIsUnchanged(bool isSdkOperation)
        {
            var config = ConfigWithQueueWait(sharingMode: "isolated", queueWaitMs: 5000);
            var worker = new WorkerProcess(config, new KbHandle("kba", @"C:\KBs\KbA"));

            Assert.Equal(60000, worker.ResolvePendingTimeoutMs(60000, isSdkOperation));
            Assert.Equal(0, worker.ResolvePendingTimeoutMs(0, isSdkOperation));
        }

        [Fact]
        public void SharedPendingTimeoutOfAnSdkOperationIsExtendedByTheEffectiveQueueWait()
        {
            var config = ConfigWithQueueWait(sharingMode: "shared-host", queueWaitMs: 5000);
            Assert.Equal(5000, config.EffectiveSharedHost.QueueWaitMs);
            var worker = new WorkerProcess(config, new KbHandle("kba", @"C:\KBs\KbA"));

            Assert.Equal(65000, worker.ResolvePendingTimeoutMs(60000, isSdkOperation: true));
        }

        [Fact]
        public void SharedPendingTimeoutOfANonSdkCommandOrWithoutTimeoutIsUnchanged()
        {
            var config = ConfigWithQueueWait(sharingMode: "shared-host", queueWaitMs: 5000);
            var worker = new WorkerProcess(config, new KbHandle("kba", @"C:\KBs\KbA"));

            Assert.Equal(60000, worker.ResolvePendingTimeoutMs(60000, isSdkOperation: false));
            Assert.Equal(0, worker.ResolvePendingTimeoutMs(0, isSdkOperation: true));
            Assert.Equal(-1, worker.ResolvePendingTimeoutMs(-1, isSdkOperation: true));
        }

        [Fact]
        public void SharedPendingTimeoutSaturatesAtIntMaxValue()
        {
            var config = ConfigWithQueueWait(sharingMode: "shared-host", queueWaitMs: 5000);
            var worker = new WorkerProcess(config, new KbHandle("kba", @"C:\KBs\KbA"));

            Assert.Equal(int.MaxValue, worker.ResolvePendingTimeoutMs(int.MaxValue - 10, isSdkOperation: true));
        }

        // ---------------------------------------------------------------- host disconnect

        [Theory]
        [InlineData("ping", "")]
        [InlineData("kb", "Open")]
        [InlineData("no_such_method", "NoSuchAction")]
        [InlineData(null, null)]
        public void HostDisconnectErrorMatchesTheCatalog(string? method, string? action)
        {
            const int queueWaitMs = 5000;
            JObject error = WorkerProcess.BuildSharedHostDisconnectError("kba", method, action, queueWaitMs);

            JObject expected = ConcurrencyErrorCatalog.Build(
                ConcurrencyErrorCatalog.SharedWorkerRestarted,
                null,
                new ConcurrencyErrorFields
                {
                    DispatchState = ConcurrencyDispatchState.DeliveredUnanswered,
                    OperationClass = WorkerCommandCatalog.Classify(method, action),
                    QueueWaitMs = queueWaitMs
                });

            Assert.Equal(-32603, error.Value<int>("code"));
            Assert.False(string.IsNullOrWhiteSpace(error["message"]?.ToString()));
            var data = Assert.IsType<JObject>(error["data"]);
            foreach (JProperty property in expected.Properties())
                Assert.True(JToken.DeepEquals(property.Value, data[property.Name]),
                    "data." + property.Name + " expected " + property.Value + " but was " + data[property.Name]);
        }

        [Fact]
        public void HostDisconnectRetryableFollowsTheOperationClassForEveryCatalogEntry()
        {
            var commands = WorkerCommandCatalog.Entries
                .Select(e => (e.Method, e.Action))
                .Concat(new[] { ("unknown_method", "Whatever"), ("ping", "UnlistedAction") });

            foreach (var (method, action) in commands)
            {
                JObject error = WorkerProcess.BuildSharedHostDisconnectError("kba", method, action, 5000);
                bool isRead = WorkerCommandCatalog.Classify(method, action) == OperationClass.Read;

                Assert.Equal(ConcurrencyErrorCatalog.SharedWorkerRestarted, error["data"]?["code"]?.ToString());
                Assert.Equal(ConcurrencyDispatchState.DeliveredUnanswered, error["data"]?["dispatchState"]?.ToString());
                Assert.True(isRead == (error["data"]?.Value<bool>("retryable") ?? !isRead),
                    method + "/" + action + ": retryable must be " + isRead);
            }
        }

        [Fact]
        public void HostDisconnectErrorIsNotRedispatched()
        {
            JObject error = WorkerProcess.BuildSharedHostDisconnectError("kba", "ping", "", 5000);
            var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = "r4", ["error"] = error };

            Assert.False(Program.ShouldRedispatchUndeliveredCommand(response, 0));
        }

        // ---------------------------------------------------------------- WorkerBusy

        [Theory]
        [InlineData("queue-wait-expired")]
        [InlineData("queue-full")]
        [InlineData("session-queue-limit")]
        public void ToolResultWorkerBusyIsNotRedispatched(string reason)
        {
            JObject busy = ConcurrencyErrorCatalog.Build(
                ConcurrencyErrorCatalog.WorkerBusy,
                reason,
                new ConcurrencyErrorFields { QueueWaitMs = 30000, RetryAfterMs = 1000 });
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, busy["dispatchState"]?.ToString());
            Assert.True(busy.Value<bool>("retryable"));

            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "r5",
                ["result"] = new JObject { ["status"] = "error", ["error"] = busy }
            };

            Assert.False(Program.ShouldRedispatchUndeliveredCommand(response, 0));
        }

        // ---------------------------------------------------------------- helpers

        private static Configuration ConfigWithQueueWait(string sharingMode, int queueWaitMs)
        {
            var section = new JObject { [SharedHostSettingsResolver.QueueWaitMsKey] = queueWaitMs };
            return new Configuration
            {
                Server = new ServerConfig { WorkerSharingMode = sharingMode },
                EffectiveSharedHost = SharedHostSettingsResolver.Resolve(section, strictDocument: false, string.Empty, _ => null)
            };
        }

        private static JObject AttachAck(SharedWorkerIdentity identity, int version) => new JObject
        {
            ["gxmcp"] = "attach_ack",
            ["type"] = "attach_ack",
            ["version"] = version,
            ["protocolVersion"] = version,
            ["key"] = identity.Key,
            ["identityKey"] = identity.Key,
            ["attachId"] = "attach-a",
            ["hostPid"] = 10,
            ["hostStartTimeUtcTicks"] = 11,
            ["workerPid"] = 12,
            ["workerStartTimeUtcTicks"] = 13,
            ["generation"] = 2,
            ["ready"] = true
        };

        /// <summary>
        /// A scripted Worker_Host on a real named pipe: answers the attach handshake,
        /// records every frame it reads, and optionally answers status frames.
        /// </summary>
        private sealed class FakeHost : IAsyncDisposable
        {
            private readonly SharedWorkerIdentity _identity;
            private readonly int _ackVersion;
            private readonly string _pipeName = "GxMcpTestV2_" + Guid.NewGuid().ToString("N");
            private readonly NamedPipeServerStream _server;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly TaskCompletionSource<JObject> _attach = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _firstFrameByType =
                new ConcurrentDictionary<string, TaskCompletionSource<JObject>>(StringComparer.Ordinal);
            private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
            private StreamWriter? _writer;
            private Task? _loop;
            private Func<JObject>? _ack;

            private FakeHost(SharedWorkerIdentity identity, int ackVersion)
            {
                _identity = identity;
                _ackVersion = ackVersion;
                _server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }

            internal volatile bool AnswerStatus;

            internal Task<JObject> AttachFrame => _attach.Task;

            // ack replaces the default attach acknowledgement.
            internal static FakeHost Start(SharedWorkerIdentity identity, int ackVersion, Func<JObject>? ack = null)
            {
                var host = new FakeHost(identity, ackVersion) { _ack = ack };
                host._loop = Task.Run(host.RunAsync);
                return host;
            }

            internal SharedWorkerConnection CreateConnection() => new SharedWorkerConnection(
                _identity,
                new SharedWorkerRecord
                {
                    Identity = _identity,
                    PipeName = _pipeName,
                    HostPid = 10,
                    WorkerPid = 12,
                    WorkerStartTimeUtcTicks = 13,
                    Generation = 2
                },
                "client-a");

            internal Task<JObject> NextFrame(string type)
                => _firstFrameByType.GetOrAdd(type, _ => new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

            internal async Task SendAsync(JObject frame)
            {
                await _writeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await _writer!.WriteLineAsync(frame.ToString(Formatting.None)).ConfigureAwait(false);
                    await _writer.FlushAsync().ConfigureAwait(false);
                }
                finally { _writeGate.Release(); }
            }

            private async Task RunAsync()
            {
                try
                {
                    await _server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(_server, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
                    _writer = new StreamWriter(_server, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
                    while (!_cts.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                        if (line == null) break;
                        JObject frame;
                        try { frame = JObject.Parse(line); } catch { continue; }
                        string type = frame["gxmcp"]?.ToString() ?? frame["type"]?.ToString() ?? string.Empty;
                        // Creates the completion source for this frame type when no test asked for it yet.
                        _ = NextFrame(type);
                        _firstFrameByType[type].TrySetResult(frame);

                        if (type == "attach")
                        {
                            _attach.TrySetResult(frame);
                            await SendAsync(_ack?.Invoke() ?? AttachAck(_identity, _ackVersion)).ConfigureAwait(false);
                        }
                        else if (type == "status" && AnswerStatus)
                        {
                            await SendAsync(new JObject
                            {
                                ["gxmcp"] = "status_ack",
                                ["type"] = "status_ack",
                                ["version"] = _ackVersion,
                                ["protocolVersion"] = _ackVersion,
                                ["requestId"] = frame["requestId"]?.ToString(),
                                ["queue"] = new JObject { ["depth"] = 0 }
                            }).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }

            public async ValueTask DisposeAsync()
            {
                try { _cts.Cancel(); } catch { }
                try { _server.Dispose(); } catch { }
                if (_loop != null)
                {
                    try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                }
                _cts.Dispose();
                _writeGate.Dispose();
            }
        }
    }
}
