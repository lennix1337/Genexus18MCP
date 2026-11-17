using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class SharedWorkerAttachInfo
    {
        public string AttachId { get; init; } = string.Empty;
        public int HostPid { get; init; }
        public long HostStartTimeUtcTicks { get; init; }
        public int WorkerPid { get; init; }
        public long WorkerStartTimeUtcTicks { get; init; }
        public long Generation { get; init; }
        public bool SdkReady { get; init; }

        /// <summary>The host's child state from the attach acknowledgement (protocol v2); empty when absent.</summary>
        public string State { get; init; } = string.Empty;
    }

    /// <summary>One <c>admission</c> frame from the host Admission_Guard (protocol v2).</summary>
    internal sealed class SharedWorkerAdmissionUpdate
    {
        /// <summary>"held", "starting", "refused" or "evicting".</summary>
        public string State { get; init; } = string.Empty;

        /// <summary>1-based FIFO position, set for "held".</summary>
        public int? Position { get; init; }

        public long? ElapsedMs { get; init; }

        /// <summary>The refusal object (code, reason, measured values), set for "refused".</summary>
        public JObject? Refusal { get; init; }
    }

    /// <summary>
    /// One Gateway-side connection to a shared Worker host. The host protocol is
    /// deliberately separate from JSON-RPC: only the attach/heartbeat/detach
    /// envelopes use the <c>gxmcp</c> marker; regular Worker JSON-RPC is passed
    /// through as one newline-delimited frame per request or notification.
    /// </summary>
    internal sealed class SharedWorkerConnection : ISharedWorkerAttachment
    {
        // RFC #854: version 2 adds the status/status_ack diagnostics frames and the
        // top-level "_gxmcpShared" response metadata. A version 1 host is refused at
        // attach, because it would answer neither.
        internal const int ProtocolVersion = 2;
        internal const int MaxFrameBytes = 4 * 1024 * 1024;

        /// <summary>Top-level response key the broker uses for queue metadata (RFC #854).</summary>
        internal const string SharedMetadataKey = "_gxmcpShared";

        /// <summary>
        /// Absolute ceiling past which a frame is abusive rather than merely large.
        /// Issue #347: between <see cref="MaxFrameBytes"/> and this ceiling an oversized
        /// frame is parsed and, if well-formed, refused per request; past it we refuse to
        /// buffer and fail closed.
        /// </summary>
        internal const int HardFrameCeilingBytes = 4 * MaxFrameBytes;

        private readonly SharedWorkerIdentity _identity;
        private readonly string _pipeName;
        private readonly string _clientId;
        private readonly string _nonce = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject?>> _pendingStatus =
            new ConcurrentDictionary<string, TaskCompletionSource<JObject?>>(StringComparer.Ordinal);
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private Timer? _heartbeatTimer;
        private Task? _readerTask;
        private int _disposed;
        private int _disconnected;
        private string? _lastError;

        internal SharedWorkerConnection(SharedWorkerIdentity identity, SharedWorkerRecord record, string clientId)
            : this(identity, (record ?? throw new ArgumentNullException(nameof(record))).PipeName, clientId)
        {
        }

        // Registry v2: the pipe name comes from the v2 record of the KB directory.
        internal SharedWorkerConnection(SharedWorkerIdentity identity, SharedHostRecord record, string clientId)
            : this(identity, (record ?? throw new ArgumentNullException(nameof(record))).PipeName, clientId)
        {
        }

        private SharedWorkerConnection(SharedWorkerIdentity identity, string? pipeName, string clientId)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _pipeName = pipeName ?? string.Empty;
            _clientId = string.IsNullOrWhiteSpace(clientId) ? Guid.NewGuid().ToString("N") : clientId;
        }

        public SharedWorkerAttachInfo? AttachInfo { get; private set; }

        // Latest SDK readiness seen on this attachment: the attach_ack or worker_restarted "ready" flag, the
        // routed notifications/worker/sdk_ready notification, or the launcher's readiness check (MarkSdkReady).
        public bool IsSdkReady => Volatile.Read(ref _sdkReady) != 0;

        private int _sdkReady;

        // Called by the launcher when readiness was observed through the v2 record rather than on this pipe.
        internal void MarkSdkReady() => Volatile.Write(ref _sdkReady, 1);
        internal bool IsConnected => Volatile.Read(ref _disconnected) == 0 && Volatile.Read(ref _disposed) == 0;
        internal int? HostPid => AttachInfo?.HostPid;
        internal int? WorkerPid => AttachInfo?.WorkerPid;
        internal long? Generation => AttachInfo?.Generation;
        internal string? AttachId => AttachInfo?.AttachId;
        internal string? LastError => _lastError;
        internal string? LastRestartDiagnostic { get; private set; }

        internal event Action<string>? LineReceived;
        internal event Action<Exception?>? Disconnected;
        internal event Action<string?>? WorkerRestarted;

        /// <summary>
        /// The broker's supervision report for the shared child, from heartbeat_ack.
        /// Issue #335. Absent on an older broker, which is why the subscribe is optional
        /// rather than assumed.
        /// </summary>
        internal event Action<JObject>? SupervisionReceived;

        /// <summary>The latest <c>admission</c> frame from the host, or null when none arrived.</summary>
        internal SharedWorkerAdmissionUpdate? LastAdmission
        {
            get => Volatile.Read(ref _lastAdmission);
            private set => Volatile.Write(ref _lastAdmission, value);
        }

        private SharedWorkerAdmissionUpdate? _lastAdmission;

        /// <summary>Raised for each <c>admission</c> frame after <see cref="LastAdmission"/> is updated.</summary>
        internal event Action<SharedWorkerAdmissionUpdate>? AdmissionReceived;

        internal void Connect(int timeoutMs)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (string.IsNullOrWhiteSpace(_pipeName))
                throw new InvalidOperationException("Shared Worker registry record has no pipe name.");

            _pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            _pipe.Connect(timeoutMs);
            _reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            WriteLineAsync(BuildAttachRequest(_identity, _clientId, Environment.ProcessId, GetCurrentProcessStartTicks(), _nonce), timeoutMs)
                .GetAwaiter()
                .GetResult();

            string? ackLine = ReadLineWithTimeout(timeoutMs);
            if (!TryParseAttachAck(ackLine, _identity, out SharedWorkerAttachInfo? attachInfo, out string error))
                throw new InvalidOperationException("Shared Worker attach handshake failed: " + error);

            AttachInfo = attachInfo;
            if (attachInfo?.SdkReady == true) MarkSdkReady();
            _readerTask = Task.Run(ReadLoopAsync);
            _heartbeatTimer = new Timer(_ => _ = SendHeartbeatAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        internal Task SendAsync(JObject rpc, CancellationToken cancellationToken)
        {
            if (rpc == null) throw new ArgumentNullException(nameof(rpc));
            return WriteLineAsync(rpc.ToString(Formatting.None), cancellationToken);
        }

        /// <summary>
        /// Asks the host for its queue and child state (RFC #854 diagnostics). Returns the
        /// <c>status_ack</c> frame whose <c>requestId</c> matches, or null when none arrives
        /// within <paramref name="timeoutMs"/> or the attachment is not connected. A timeout
        /// leaves the attachment connected; an acknowledgement that arrives later is
        /// dropped by the read loop. Only <paramref name="cancellationToken"/> throws.
        /// </summary>
        internal async Task<JObject?> RequestStatusAsync(int timeoutMs, CancellationToken cancellationToken = default)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (!IsConnected) return null;

            string requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingStatus[requestId] = completion;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(timeoutMs);
                try
                {
                    await WriteLineAsync(new JObject
                    {
                        ["gxmcp"] = "status",
                        ["type"] = "status",
                        ["version"] = ProtocolVersion,
                        ["protocolVersion"] = ProtocolVersion,
                        ["key"] = _identity.Key,
                        ["identityKey"] = _identity.Key,
                        ["attachId"] = AttachId ?? string.Empty,
                        ["attachmentId"] = AttachId ?? string.Empty,
                        ["requestId"] = requestId
                    }, budget.Token).ConfigureAwait(false);
                    return await completion.Task.WaitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidDataException)
                {
                    return null;
                }
            }
            finally
            {
                _pendingStatus.TryRemove(requestId, out _);
            }
        }

        /// <summary>
        /// Moves the broker's top-level <c>_gxmcpShared</c> object into
        /// <c>result._meta.sharedHost</c>, keeping any existing <c>result._meta</c> members,
        /// and removes the top-level key (RFC #854, Requirement 11 criterion 4). A response
        /// without a result object has nowhere to carry the metadata, so the key is only
        /// removed there: it is transport metadata and must not reach the MCP client.
        /// Returns false, leaving the response unchanged, when the key is absent.
        /// </summary>
        internal static bool TryMoveSharedMetadata(JObject response)
        {
            if (response == null) return false;
            JToken? shared = response[SharedMetadataKey];
            if (shared == null) return false;
            response.Remove(SharedMetadataKey);

            if (response["result"] is JObject result)
            {
                JToken? existingMeta = result["_meta"];
                if (existingMeta is JObject meta)
                {
                    meta["sharedHost"] = shared;
                }
                else if (existingMeta == null || existingMeta.Type == JTokenType.Null)
                {
                    result["_meta"] = new JObject { ["sharedHost"] = shared };
                }
                // A non-object result._meta is the Worker's own and is left as it is.
            }
            return true;
        }

        internal static JObject BuildAttachRequest(
            SharedWorkerIdentity identity,
            string clientId,
            int gatewayPid,
            long gatewayStartTimeUtcTicks,
            string nonce)
        {
            return new JObject
            {
                ["gxmcp"] = "attach",
                ["type"] = "attach",
                ["version"] = ProtocolVersion,
                ["protocolVersion"] = ProtocolVersion,
                ["key"] = identity.Key,
                ["identityKey"] = identity.Key,
                ["clientId"] = clientId,
                ["gatewayPid"] = gatewayPid,
                ["gatewayStartTimeUtcTicks"] = gatewayStartTimeUtcTicks,
                ["nonce"] = nonce,
                ["attachNonce"] = nonce,
                // Protocol v2: the requested Physical_KB_Identity components, so a refusing host can name each
                // component that differs from its own.
                ["identity"] = new JObject
                {
                    ["installationPath"] = identity.InstallationPath,
                    ["driver"] = identity.Driver,
                    ["major"] = identity.Major,
                    ["workerExecutable"] = identity.WorkerExecutable,
                    ["profileConfigPath"] = identity.ProfileConfigPath
                }
            };
        }

        internal static bool TryParseAttachAck(
            string? line,
            SharedWorkerIdentity identity,
            out SharedWorkerAttachInfo? info,
            out string error)
        {
            info = null;
            error = "empty attach acknowledgement";
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                JObject frame = GxMcp.Common.JsonIngress.ParseObject(line);
                string frameType = frame["gxmcp"]?.ToString() ?? frame["type"]?.ToString() ?? string.Empty;
                if (!string.Equals(frameType, "attach_ack", StringComparison.Ordinal))
                {
                    error = "expected gxmcp=attach_ack";
                    return false;
                }
                int? frameVersion = frame.Value<int?>("version") ?? frame.Value<int?>("protocolVersion");
                if (frameVersion != ProtocolVersion)
                {
                    error = "protocol version mismatch";
                    return false;
                }
                string frameKey = frame["key"]?.ToString() ?? frame["identityKey"]?.ToString() ?? string.Empty;
                if (!string.Equals(frameKey, identity.Key, StringComparison.Ordinal))
                {
                    error = "shared Worker identity mismatch";
                    return false;
                }

                string attachId = frame["attachId"]?.ToString() ?? string.Empty;
                int hostPid = frame.Value<int?>("hostPid") ?? 0;
                long hostStart = frame.Value<long?>("hostStartTimeUtcTicks") ?? 0;
                int workerPid = frame.Value<int?>("workerPid") ?? 0;
                long workerStart = frame.Value<long?>("workerStartTimeUtcTicks") ?? 0;
                long generation = frame.Value<long?>("generation") ?? 0;
                bool sdkReady = frame.Value<bool?>("ready") ?? false;
                string state = frame["state"]?.Type == JTokenType.String ? (string)frame["state"]! : string.Empty;
                // Protocol v2: a host that is admitting, held, refused or without a Worker acknowledges with
                // workerPid and workerStartTimeUtcTicks both 0 and names its child state; worker_restarted then
                // names the child. A partial Worker identity, or a zero one without a pre-child state, stays invalid.
                bool workerKnown = workerPid > 0 && workerStart > 0;
                bool beforeWorker = workerPid == 0 && workerStart == 0 && !sdkReady && IsPreWorkerState(state);
                if (string.IsNullOrWhiteSpace(attachId) || hostPid <= 0 || hostStart <= 0 || (!workerKnown && !beforeWorker) || generation <= 0)
                {
                    error = "attach acknowledgement is missing process identity";
                    return false;
                }

                info = new SharedWorkerAttachInfo
                {
                    AttachId = attachId,
                    HostPid = hostPid,
                    HostStartTimeUtcTicks = hostStart,
                    WorkerPid = workerPid,
                    WorkerStartTimeUtcTicks = workerStart,
                    Generation = generation,
                    SdkReady = sdkReady,
                    State = state
                };
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = "malformed attach acknowledgement: " + ex.Message;
                return false;
            }
        }

        // Registry v2 states in which the host has no Worker child (design "Registry v2 record").
        private static bool IsPreWorkerState(string state)
            => string.Equals(state, SharedHostRecordState.Admitting, StringComparison.Ordinal)
                || string.Equals(state, SharedHostRecordState.Held, StringComparison.Ordinal)
                || string.Equals(state, SharedHostRecordState.Refused, StringComparison.Ordinal)
                || string.Equals(state, SharedHostRecordState.NoWorker, StringComparison.Ordinal)
                || string.Equals(state, SharedHostRecordState.Recovering, StringComparison.Ordinal)
                || string.Equals(state, SharedHostRecordState.Replacing, StringComparison.Ordinal);

        internal static bool IsControlFrame(JObject frame, string name)
            => frame != null && string.Equals(frame["gxmcp"]?.ToString(), name, StringComparison.Ordinal);

        public void Dispose() => Dispose(stopIfLast: false);

        /// <summary>
        /// Writes the detach frame and closes the attachment. <paramref name="stopIfLast"/> asks the host to stop
        /// its Worker when this was its last Attachment (a Server.MaxOpenKbs eviction). A host that predates the
        /// flag ignores the extra property. Idempotent: only the first call writes a detach frame.
        /// </summary>
        internal void Dispose(bool stopIfLast)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                WriteLineAsync(BuildDetachFrame(_identity, AttachId, stopIfLast), 500).GetAwaiter().GetResult();
            }
            catch { }
            try { _cts.Cancel(); } catch { }
            try { _heartbeatTimer?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _writeGate.Dispose(); } catch { }
            SignalDisconnected(null);
        }

        internal static JObject BuildDetachFrame(SharedWorkerIdentity identity, string? attachId, bool stopIfLast)
        {
            return new JObject
            {
                ["gxmcp"] = "detach",
                ["type"] = "detach",
                ["version"] = ProtocolVersion,
                ["protocolVersion"] = ProtocolVersion,
                ["key"] = identity.Key,
                ["identityKey"] = identity.Key,
                ["attachId"] = attachId ?? string.Empty,
                ["attachmentId"] = attachId ?? string.Empty,
                ["stopIfLast"] = stopIfLast
            };
        }

        /// <summary>
        /// Reads an <c>admission</c> frame (protocol v2). Absent or mistyped members become null rather than
        /// failing: the frame is advisory host control, so a partial one must not disconnect the attachment.
        /// </summary>
        internal static SharedWorkerAdmissionUpdate ParseAdmission(JObject frame)
        {
            JToken? position = frame["position"];
            JToken? elapsed = frame["elapsedMs"];
            return new SharedWorkerAdmissionUpdate
            {
                State = frame["state"]?.Type == JTokenType.String ? (string)frame["state"]! : string.Empty,
                Position = position?.Type == JTokenType.Integer ? position.Value<int>() : (int?)null,
                ElapsedMs = elapsed?.Type == JTokenType.Integer ? elapsed.Value<long>() : (long?)null,
                Refusal = frame["refusal"] as JObject
            };
        }

        private async Task ReadLoopAsync()
        {
            Exception? failure = null;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    string? line = await _reader!.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    long frameBytes = Encoding.UTF8.GetByteCount(line);
                    if (frameBytes > MaxFrameBytes)
                    {
                        // Issue #347. Throwing here signalled a disconnect, which took down
                        // every attachment on this broker - one client's legitimately large
                        // response became everyone's outage. A frame that parses is a valid
                        // response that does not fit the transport, so it becomes a bounded
                        // per-request error delivered to the requester alone.
                        //
                        // A frame that does not parse, or that is beyond the hard ceiling,
                        // is malformed or abusive and still disconnects: the oversized
                        // path must not become a way past the fail-closed check.
                        string? oversizedRefusal = SharedWorkerOversizedFrame.TryBuildRefusal(
                            line, frameBytes, MaxFrameBytes, HardFrameCeilingBytes);
                        if (oversizedRefusal == null)
                            throw new InvalidDataException(
                                "Shared Worker frame exceeded the maximum size and could not be attributed to a request.");

                        LineReceived?.Invoke(oversizedRefusal);
                        continue;
                    }

                    JObject? frame = TryParseObject(line);
                    if (frame != null && IsControlFrame(frame, "worker_restarted"))
                    {
                        if (!TryUpdateWorkerInfo(frame, out string restartError))
                            throw new InvalidOperationException(restartError);
                        try { WorkerRestarted?.Invoke(LastRestartDiagnostic); } catch { }
                        continue;
                    }
                    if (frame != null && IsControlFrame(frame, "heartbeat_ack"))
                    {
                        // Issue #335: the broker's supervision block, surfaced rather than
                        // swallowed. This acknowledgement used to be discarded, which is
                        // precisely why a shared Worker had no vitals: the Gateway cannot
                        // ask the OS about a process it does not own, so the only party that
                        // can answer is the one whose report was being thrown away here.
                        //
                        // Extending heartbeat_ack rather than adding a frame type is what
                        // keeps this off a protocol migration: an older broker sends no
                        // block and the handler is simply not called.
                        if (frame["supervision"] is JObject supervision)
                            SupervisionReceived?.Invoke(supervision);
                        continue;
                    }
                    if (frame != null && IsControlFrame(frame, "heartbeat"))
                        continue;
                    if (frame != null && IsControlFrame(frame, "admission"))
                    {
                        // RFC #854: the host Admission_Guard reports held/starting/refused/evicting here.
                        // Host control, never a Worker line, and never a reason to disconnect.
                        var admission = ParseAdmission(frame);
                        LastAdmission = admission;
                        try { AdmissionReceived?.Invoke(admission); } catch { }
                        continue;
                    }
                    if (frame != null && IsControlFrame(frame, "host_error"))
                        throw new InvalidOperationException(frame["message"]?.ToString() ?? "Shared Worker host error.");
                    if (frame != null && IsControlFrame(frame, "status_ack"))
                    {
                        // RFC #854: a status acknowledgement is host control, never a Worker
                        // line. One whose requester already timed out is dropped here.
                        string statusRequestId = frame["requestId"]?.ToString() ?? string.Empty;
                        if (statusRequestId.Length > 0 && _pendingStatus.TryRemove(statusRequestId, out var statusWaiter))
                            statusWaiter.TrySetResult(frame);
                        continue;
                    }
                    if (frame != null && string.Equals(frame["method"]?.ToString(), "notifications/worker/sdk_ready", StringComparison.Ordinal))
                        MarkSdkReady();
                    if (frame != null && TryMoveSharedMetadata(frame))
                        line = frame.ToString(Formatting.None);
                    LineReceived?.Invoke(line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                failure = ex;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                SignalDisconnected(failure);
            }
        }

        private async Task SendHeartbeatAsync()
        {
            if (!IsConnected) return;
            try
            {
                await WriteLineAsync(new JObject
                {
                    ["gxmcp"] = "heartbeat",
                    ["type"] = "heartbeat",
                    ["version"] = ProtocolVersion,
                    ["protocolVersion"] = ProtocolVersion,
                    ["key"] = _identity.Key,
                    ["identityKey"] = _identity.Key,
                    ["attachId"] = AttachId ?? string.Empty,
                    ["attachmentId"] = AttachId ?? string.Empty,
                    ["utc"] = DateTime.UtcNow.ToString("O")
                }, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SignalDisconnected(ex);
            }
        }

        private Task WriteLineAsync(JObject frame, int timeoutMs)
            => WriteLineWithTimeoutAsync(frame, timeoutMs);

        private async Task WriteLineWithTimeoutAsync(JObject frame, int timeoutMs)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            await WriteLineAsync(frame, timeout.Token).ConfigureAwait(false);
        }

        private Task WriteLineAsync(JObject frame, CancellationToken cancellationToken)
            => WriteLineAsync(frame.ToString(Formatting.None), cancellationToken);

        private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
                throw new InvalidDataException("Shared Worker frame exceeded the maximum size.");
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_writer == null) throw new IOException("Shared Worker pipe is not connected.");
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private string? ReadLineWithTimeout(int timeoutMs)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            Task<string?> read = _reader!.ReadLineAsync();
            try
            {
                return read.WaitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Shared Worker attach acknowledgement timed out.");
            }
        }

        private void SignalDisconnected(Exception? failure)
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
            _lastError = failure?.ToString();
            foreach (var statusRequestId in _pendingStatus.Keys)
            {
                if (_pendingStatus.TryRemove(statusRequestId, out var statusWaiter))
                    statusWaiter.TrySetResult(null);
            }
            try { Disconnected?.Invoke(failure); } catch { }
        }

        private bool TryUpdateWorkerInfo(JObject frame, out string error)
        {
            error = string.Empty;
            int hostPid = frame.Value<int?>("hostPid") ?? 0;
            long hostStart = frame.Value<long?>("hostStartTimeUtcTicks") ?? 0;
            int workerPid = frame.Value<int?>("workerPid") ?? 0;
            long workerStart = frame.Value<long?>("workerStartTimeUtcTicks") ?? 0;
            long generation = frame.Value<long?>("generation") ?? 0;
            if (hostPid <= 0 || hostStart <= 0 || workerPid <= 0 || workerStart <= 0 || generation <= 0)
            {
                error = "worker restart frame is missing process identity";
                return false;
            }
            if (AttachInfo == null || hostPid != AttachInfo.HostPid || hostStart != AttachInfo.HostStartTimeUtcTicks)
            {
                error = "worker restart frame changed host identity";
                return false;
            }
            if (generation <= AttachInfo.Generation)
            {
                error = "worker restart generation did not advance";
                return false;
            }
            AttachInfo = new SharedWorkerAttachInfo
            {
                AttachId = AttachInfo.AttachId,
                HostPid = hostPid,
                HostStartTimeUtcTicks = hostStart,
                WorkerPid = workerPid,
                WorkerStartTimeUtcTicks = workerStart,
                Generation = generation,
                SdkReady = frame.Value<bool?>("ready") ?? false
            };
            Volatile.Write(ref _sdkReady, AttachInfo.SdkReady ? 1 : 0);
            LastRestartDiagnostic = frame["failureDiagnostic"]?.ToString();
            return true;
        }

        private static JObject? TryParseObject(string line)
        {
            try { return GxMcp.Common.JsonIngress.ParseObject(line); } catch { return null; }
        }

        private static long GetCurrentProcessStartTicks()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
            catch { return 0; }
        }
    }
}
