using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Common.SharedHost;

namespace GxMcp.Worker
{
    internal sealed class SharedWorkerEnvelope
    {
        public string Type { get; set; } = string.Empty;
        public int ProtocolVersion { get; set; }
        public string IdentityKey { get; set; } = string.Empty;
        public int GatewayPid { get; set; }
        public long GatewayStartTimeUtcTicks { get; set; }
        public string AttachNonce { get; set; } = string.Empty;
        public string AttachmentId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        // Protocol v2: the correlation id of a "status" frame, echoed by "status_ack".
        public string RequestId { get; set; } = string.Empty;

        // Protocol v2: a "detach" frame that asks the host to stop its Worker when this was the last Attachment.
        public bool StopIfLast { get; set; }

        // Protocol v2: the "identity" object of an "attach" frame (the requesting Gateway's Physical_KB_Identity
        // components), so the host can name each component that differs from its own. Null when absent.
        public JObject Identity { get; set; }
    }

    /// <summary>
    /// Host-side values of a <c>status_ack</c> frame that the scheduler snapshot does not hold.
    /// Every member is diagnostics data about the host itself; nothing here comes from a request.
    /// </summary>
    internal sealed class SharedHostStatusContext
    {
        public string ChildState { get; set; } = string.Empty;
        public int WorkerPid { get; set; }
        public long WorkerWorkingSetMb { get; set; }
        public int Attachments { get; set; }
        public JObject EffectiveSettings { get; set; }
        public JObject Admission { get; set; }
    }

    internal sealed class SharedWorkerRequestRoute
    {
        public string AttachmentId { get; set; } = string.Empty;
        public JToken ClientRequestId { get; set; } = JValue.CreateNull();
        public string ChildRequestId { get; set; } = string.Empty;
        public JToken ClientProgressToken { get; set; } = JValue.CreateNull();
        public string ChildProgressToken { get; set; } = string.Empty;
    }

    /// <summary>
    /// Pure framing and correlation helpers shared by the host loop and its tests.
    /// The SDK is deliberately not referenced here: host transport failures must be
    /// testable without opening a Knowledge Base.
    /// </summary>
    internal static class SharedWorkerHostProtocol
    {
        // Version 2 (RFC #854) adds the status, status_ack and admission frames, the
        // detach stopIfLast flag, the Busy_Envelope and the _gxmcpShared response metadata.
        internal const int ProtocolVersion = 2;
        internal const int MaxFrameBytes = 4 * 1024 * 1024;

        internal const string StatusFrameType = "status";
        internal const string StatusAckFrameType = "status_ack";
        internal const string AdmissionFrameType = "admission";

        // Top-level member of a routed SDK_Operation response; the Gateway moves it to result._meta.sharedHost.
        internal const string SharedMetadataKey = "_gxmcpShared";

        // JSON-RPC error code of a child exit, unchanged from protocol version 1.
        internal const int RestartedErrorCode = -32050;

        // Lower bound of the busyWaitMs a released SDK_Operation carries, and of a Busy_Envelope retryAfterMs.
        internal const int MinBusyWaitMs = 1000;

        // Upper bound of a status requestId; the host echoes it, so it stays small.
        internal const int MaxStatusRequestIdLength = 128;

        /// <summary>
        /// Absolute ceiling beyond which a frame is treated as abusive rather than
        /// merely large. Issue #347.
        ///
        /// <para>
        /// Between <see cref="MaxFrameBytes"/> and this ceiling a frame is parsed and,
        /// if it is well-formed, answered with a per-request error - a valid large read
        /// must not be able to affect other attachments. Above the ceiling we stop
        /// fail-closed instead: past a point the frame is no longer a plausible response,
        /// and refusing to buffer it is the point of having a ceiling at all.
        /// </para>
        /// </summary>
        internal const int HardFrameCeilingBytes = 4 * MaxFrameBytes;

        /// <summary>
        /// What the host should do with a child frame that exceeds
        /// <see cref="MaxFrameBytes"/>.
        /// </summary>
        internal enum OversizedFrameDisposition
        {
            /// <summary>
            /// The frame is well-formed JSON-RPC carrying a request id. The requester
            /// gets a bounded structured error and the host keeps serving everyone else.
            /// </summary>
            RejectRequest,

            /// <summary>
            /// The frame is well-formed but has no request id, so there is nobody to
            /// answer. It is dropped with a log; the host survives, because a valid
            /// notification that is too large is not evidence of a broken transport.
            /// </summary>
            DropNotification,

            /// <summary>
            /// The frame is malformed, or beyond the hard ceiling. This is the
            /// fail-closed case and it is unchanged from before: corrupt or compromised
            /// transport data still stops the host.
            /// </summary>
            FailClosed,
        }

        /// <summary>
        /// Decides what to do with an oversized child frame, and recovers the request id
        /// when the frame is answerable.
        ///
        /// <para>
        /// Issue #347. The host used to treat every frame over <see cref="MaxFrameBytes"/>
        /// as fatal, so a legitimate large <c>read</c>, batch or persisted-content result
        /// cancelled the broker and took down every attachment - one client's big
        /// response became every client's outage. The distinction this makes is not about
        /// size: a large frame that parses is a valid response that does not fit the
        /// transport, and the right answer is a bounded per-request error. A frame that
        /// does not parse, or that is past the hard ceiling, is malformed or abusive and
        /// still stops the host.
        /// </para>
        /// </summary>
        /// <param name="line">The raw frame text, already read from the child.</param>
        /// <param name="correlationId">
        /// The frame's JSON-RPC id when the frame is answerable, otherwise null.
        /// </param>
        /// <param name="byteCount">UTF-8 byte length, computed by the caller.</param>
        internal static OversizedFrameDisposition ClassifyOversizedFrame(string line, out string correlationId, long byteCount)
        {
            correlationId = null;
            if (line == null) return OversizedFrameDisposition.FailClosed;
            // Issue #375. Past the ceiling the reader keeps only a bounded prefix, so the frame is
            // never parsed whole. A valid response still carries its id before its payload, which
            // is enough to answer that one request; a frame whose owner cannot be established
            // from the prefix stays fail-closed.
            if (byteCount > HardFrameCeilingBytes) return ClassifyFromPrefix(line, out correlationId);

            JObject frame;
            try { frame = GxMcp.Common.JsonIngress.ParseObject(line); }
            catch
            {
                // Malformed stays malformed. The oversized path must not become a way to
                // smuggle unparseable data past the fail-closed check.
                return OversizedFrameDisposition.FailClosed;
            }
            if (frame == null) return OversizedFrameDisposition.FailClosed;

            var id = frame["id"];
            if (id == null || id.Type == JTokenType.Null)
                return OversizedFrameDisposition.DropNotification;

            correlationId = id.ToString();
            return string.IsNullOrEmpty(correlationId)
                ? OversizedFrameDisposition.FailClosed
                : OversizedFrameDisposition.RejectRequest;
        }

        /// <summary>
        /// Reads the leading top-level properties of a truncated frame. A frame whose <c>id</c>
        /// appears before any payload property is answerable; one that announces a <c>method</c>
        /// with no id is a notification. Everything else cannot be attributed.
        /// </summary>
        private static OversizedFrameDisposition ClassifyFromPrefix(string prefix, out string correlationId)
        {
            correlationId = null;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(prefix)) { DateParseHandling = DateParseHandling.None })
                {
                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return OversizedFrameDisposition.FailClosed;
                    while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                    {
                        string name = (string)reader.Value;
                        if (name == "id")
                        {
                            if (!reader.Read() || (reader.TokenType != JsonToken.String && reader.TokenType != JsonToken.Integer))
                                return OversizedFrameDisposition.FailClosed;
                            correlationId = Convert.ToString(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
                            return string.IsNullOrEmpty(correlationId) ? OversizedFrameDisposition.FailClosed : OversizedFrameDisposition.RejectRequest;
                        }
                        if (name == "method") return OversizedFrameDisposition.DropNotification;
                        if (name != "jsonrpc") return OversizedFrameDisposition.FailClosed;
                        reader.Read();
                    }
                }
            }
            catch (JsonException) { }
            return OversizedFrameDisposition.FailClosed;
        }

        /// <summary>
        /// Reads one line while retaining at most <paramref name="maxRetainedChars"/> characters;
        /// the rest of an over-long line is consumed and discarded, so an oversized child frame
        /// cannot grow the broker's memory (issue #375). <paramref name="totalBytes"/> is the
        /// UTF-8 size of the whole line. Returns null at end of stream.
        /// </summary>
        internal static string ReadBoundedLine(TextReader reader, int maxRetainedChars, out long totalBytes)
        {
            totalBytes = 0;
            var retained = new StringBuilder();
            bool any = false;
            int c;
            while ((c = reader.Read()) >= 0)
            {
                any = true;
                if (c == '\n') break;
                if (c == '\r' && reader.Peek() == '\n') continue;
                // A high surrogate counts the pair's 4 bytes; its low half then adds nothing.
                totalBytes += c < 0x80 ? 1 : c < 0x800 ? 2 : char.IsHighSurrogate((char)c) ? 4 : char.IsLowSurrogate((char)c) ? 0 : 3;
                if (retained.Length < maxRetainedChars) retained.Append((char)c);
            }
            return any ? retained.ToString() : null;
        }

        /// <summary>
        /// The bounded error sent to the requester whose response did not fit. It is
        /// deliberately small and structured: the point is that the caller learns its own
        /// result was refused and can page or request an artifact, not that it receives a
        /// truncated payload that looks complete.
        /// </summary>
        internal static JObject BuildOversizedResponse(string correlationId, long byteCount, long limitBytes)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = correlationId,
                ["error"] = new JObject
                {
                    ["code"] = "WorkerResponseTooLarge",
                    ["message"] = "This response is " + byteCount + " bytes, over the shared-transport limit of " + limitBytes
                        + " bytes, so it was refused rather than truncated. Other clients are unaffected.",
                    ["data"] = new JObject
                    {
                        ["responseBytes"] = byteCount,
                        ["maxFrameBytes"] = limitBytes,
                        // Explicit, because the alternative failure is a caller reading a
                        // truncated payload as the whole result.
                        ["complete"] = false,
                        ["truncated"] = false,
                        ["retryable"] = true,
                        ["hint"] = "Narrow the request (smaller page/limit), or write the full result to a KB-scoped artifact and read it in pieces. Do NOT treat this as an empty result."
                    }
                }
            };
        }

        /// <summary>
        /// The Busy_Envelope (design.md "Busy_Envelope (tool-result shape)", Requirement 3 criterion 6):
        /// a JSON-RPC result, never a JSON-RPC error, whose <c>result.error</c> is the WorkerBusy row of
        /// <see cref="ConcurrencyErrorCatalog"/> plus this KB's identity and scheduler snapshot.
        ///
        /// <para>
        /// Only the snapshot's diagnostics views (<see cref="SchedulerSnapshot.RunningStatus"/>) are read,
        /// never a <see cref="QueuedRequest"/>, so no payload, argument, source text, Lease token or
        /// credential can reach the envelope. Exactly one of <c>queuePosition</c> (queue-wait-expired),
        /// <c>queueDepth</c> (queue-full) and <c>sessionQueueCap</c> (session-queue-limit) is present.
        /// </para>
        /// </summary>
        /// <param name="clientRequestId">The JSON-RPC id exactly as the client sent it.</param>
        /// <param name="reason">One of the WorkerBusy reasons of the catalog.</param>
        /// <param name="snapshot">This KB's scheduler snapshot at the rejection instant.</param>
        /// <param name="limits">This KB's effective queue bounds.</param>
        /// <param name="kbIdentityKey">The identity key of the KB the request was sent to.</param>
        /// <param name="kbPath">That KB's path; omitted when empty.</param>
        /// <param name="queuePosition">The 1-based position at removal; required for queue-wait-expired.</param>
        /// <param name="retryAfterMs">The estimated wait; clamped to 1000 ms and the queue wait. Null means 1000 ms.</param>
        internal static JObject BuildBusyResult(
            JToken clientRequestId,
            string reason,
            SchedulerSnapshot snapshot,
            SchedulerLimits limits,
            string kbIdentityKey,
            string kbPath,
            int? queuePosition = null,
            long? retryAfterMs = null)
        {
            if (clientRequestId == null || clientRequestId.Type == JTokenType.Null)
                throw new ArgumentException("A Busy_Envelope answers a request with a non-null id.", nameof(clientRequestId));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            if (string.IsNullOrWhiteSpace(kbIdentityKey))
                throw new ArgumentException("A Busy_Envelope names the KB identity key.", nameof(kbIdentityKey));

            var details = new JObject { ["kbIdentityKey"] = kbIdentityKey };
            if (!string.IsNullOrWhiteSpace(kbPath)) details["kbPath"] = kbPath;

            var running = snapshot.RunningStatus;
            if (running != null)
            {
                details["runningOperation"] = running.Operation;
                details["runningElapsedMs"] = Math.Max(0L, running.ElapsedMs);
            }

            if (string.Equals(reason, "queue-wait-expired", StringComparison.Ordinal))
            {
                if (!queuePosition.HasValue || queuePosition.Value < 1)
                    throw new ArgumentException("queue-wait-expired requires a 1-based queue position.", nameof(queuePosition));
                details["queuePosition"] = queuePosition.Value;
            }
            else if (string.Equals(reason, SubmitResult.QueueFull, StringComparison.Ordinal))
            {
                details["queueDepth"] = limits.QueueDepth;
            }
            else if (string.Equals(reason, SubmitResult.SessionQueueLimit, StringComparison.Ordinal))
            {
                details["sessionQueueCap"] = limits.SessionQueueCap;
            }
            // Any other reason is refused by ConcurrencyErrorCatalog.Build below.

            long requested = Math.Max(MinBusyWaitMs, retryAfterMs ?? MinBusyWaitMs);
            var error = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.WorkerBusy, reason, new ConcurrencyErrorFields
            {
                QueueWaitMs = limits.QueueWaitMs,
                RetryAfterMs = (int)Math.Min(requested, int.MaxValue),
                Details = details
            });

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = clientRequestId.DeepClone(),
                ["result"] = new JObject
                {
                    ["status"] = "error",
                    ["code"] = ConcurrencyErrorCatalog.WorkerBusy,
                    ["error"] = error
                }
            };
        }

        /// <summary>
        /// The answer to a request that a cancellation removed from the broker queue before it reached
        /// the child: the <c>McpResponse.Ok(code: "Cancelled")</c> envelope that control/Cancel returns.
        /// </summary>
        internal static JObject BuildCancelledResult(JToken clientRequestId)
        {
            if (clientRequestId == null || clientRequestId.Type == JTokenType.Null)
                throw new ArgumentException("A cancelled answer needs the request's non-null id.", nameof(clientRequestId));
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = clientRequestId.DeepClone(),
                ["result"] = new JObject
                {
                    ["status"] = "ok",
                    ["code"] = "Cancelled",
                    ["result"] = new JObject
                    {
                        ["cancelled"] = true,
                        ["dispatchState"] = ConcurrencyDispatchState.NotDelivered
                    }
                }
            };
        }

        /// <summary>
        /// The JSON-RPC error for a request the child cannot answer because it exited (Requirement 8
        /// criterion 4, Requirement 12 criterion 4). <c>data</c> is the SHARED_WORKER_RESTARTED catalog
        /// row plus <c>generation</c>. <paramref name="method"/> and <paramref name="action"/> are used only
        /// to look up the Operation_Class; they are not copied into the error.
        /// </summary>
        /// <param name="dispatchState">not-delivered or delivered-unanswered.</param>
        /// <param name="queueWaitMs">The effective queue wait, the bound of retryAfterMs.</param>
        internal static JObject BuildRestartedError(
            JToken clientRequestId,
            string dispatchState,
            string method,
            string action,
            long generation,
            int queueWaitMs)
        {
            if (clientRequestId == null || clientRequestId.Type == JTokenType.Null)
                throw new ArgumentException("A restart error needs the request's non-null id.", nameof(clientRequestId));

            var data = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.SharedWorkerRestarted, null, new ConcurrencyErrorFields
            {
                QueueWaitMs = queueWaitMs,
                DispatchState = dispatchState,
                OperationClass = WorkerCommandCatalog.Classify(method, action),
                Details = new JObject { ["generation"] = generation }
            });

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = clientRequestId.DeepClone(),
                ["error"] = new JObject
                {
                    ["code"] = RestartedErrorCode,
                    ["message"] = (string)data["message"],
                    ["data"] = data
                }
            };
        }

        /// <summary>
        /// The <c>_meta.busyWaitMs</c> a released SDK_Operation carries: the queue wait it has left,
        /// never below <see cref="MinBusyWaitMs"/>.
        /// </summary>
        internal static int ComputeBusyWaitMs(int queueWaitMs, long waitedMs)
        {
            long remaining = (long)queueWaitMs - Math.Max(0L, waitedMs);
            return (int)Math.Max(MinBusyWaitMs, Math.Min(remaining, int.MaxValue));
        }

        /// <summary>
        /// Adds the top-level <c>_gxmcpShared</c> object <c>{ queueWaitMs, workerPid }</c> to a routed
        /// SDK_Operation response (Requirement 11 criterion 4). Only a successful response (a
        /// <c>result</c> and no <c>error</c>) is changed, because the Gateway moves the object into
        /// <c>result._meta.sharedHost</c>; nothing inside <c>result</c> is touched here. A bypass
        /// response must not be passed to this method: its absence is the contract.
        /// </summary>
        /// <returns>True when the metadata was added.</returns>
        internal static bool TryInjectSharedMetadata(JObject response, long queueWaitMs, int workerPid)
        {
            if (response == null || response["result"] == null || response["error"] != null) return false;
            response[SharedMetadataKey] = new JObject
            {
                ["queueWaitMs"] = Math.Max(0L, queueWaitMs),
                ["workerPid"] = workerPid
            };
            return true;
        }

        /// <summary>
        /// The <c>status_ack</c> host frame for one <c>status</c> request (design.md "Protocol v2 frames",
        /// Requirement 11 criteria 2 and 6). It reads only <see cref="SchedulerSnapshot.IsIdle"/>,
        /// <see cref="SchedulerSnapshot.RunningStatus"/>, <see cref="SchedulerSnapshot.QueueStatus"/> and
        /// <see cref="SchedulerSnapshot.QueueDepth"/>, so it carries operation names (method/action) and
        /// never request arguments, source content, Lease tokens or credentials.
        /// </summary>
        internal static JObject BuildStatusAck(
            string identityKey,
            string requestId,
            SchedulerSnapshot snapshot,
            SharedHostStatusContext context)
        {
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > MaxStatusRequestIdLength)
                throw new ArgumentException("status_ack echoes a non-empty, bounded request id.", nameof(requestId));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            context = context ?? new SharedHostStatusContext();

            var frame = CreateHostFrame(StatusAckFrameType, identityKey);
            frame["requestId"] = requestId;
            frame["childState"] = context.ChildState ?? string.Empty;
            frame["workerPid"] = context.WorkerPid;
            frame["workerWorkingSetMb"] = Math.Max(0L, context.WorkerWorkingSetMb);

            var running = snapshot.IsIdle ? null : snapshot.RunningStatus;
            frame["idle"] = running == null;
            frame["running"] = running == null
                ? (JToken)JValue.CreateNull()
                : new JObject
                {
                    ["operation"] = running.Operation,
                    ["ownerAttachId"] = running.OwnerAttachId,
                    ["elapsedMs"] = Math.Max(0L, running.ElapsedMs)
                };

            var queue = new JArray();
            foreach (var entry in snapshot.QueueStatus ?? (IReadOnlyList<QueueEntryStatus>)new QueueEntryStatus[0])
            {
                var item = new JObject
                {
                    ["position"] = entry.Position,
                    ["ownerAttachId"] = entry.OwnerAttachId,
                    ["waitMs"] = Math.Max(0L, entry.WaitMs),
                    ["waitReason"] = ToWireName(entry.WaitReason)
                };
                if (entry.WaitReason == WaitReason.StaBusy && !string.IsNullOrEmpty(entry.Operation))
                    item["operation"] = entry.Operation;
                queue.Add(item);
            }
            frame["queue"] = queue;
            frame["queueDepth"] = snapshot.QueueDepth;
            frame["attachments"] = Math.Max(0, context.Attachments);
            frame["effectiveSettings"] = context.EffectiveSettings?.DeepClone() ?? new JObject();
            if (context.Admission != null) frame["admission"] = context.Admission.DeepClone();
            return frame;
        }

        /// <summary>The wire name of a wait reason (Requirement 11 criterion 6).</summary>
        internal static string ToWireName(WaitReason reason)
        {
            switch (reason)
            {
                case WaitReason.WorkerStarting: return "worker-starting";
                case WaitReason.WorkerLimit: return "worker-limit";
                default: return "sta-busy";
            }
        }

        /// <summary>The common host-frame envelope: type, gxmcp, protocolVersion, version, identityKey, key.</summary>
        internal static JObject CreateHostFrame(string type, string identityKey)
        {
            return new JObject
            {
                ["type"] = type,
                ["gxmcp"] = type,
                ["protocolVersion"] = ProtocolVersion,
                ["version"] = ProtocolVersion,
                ["identityKey"] = identityKey ?? string.Empty,
                ["key"] = identityKey ?? string.Empty
            };
        }

        // Registry v2 (decision A4): a v1 "GxMcpShared_" name is not a valid host pipe, so a v1 Gateway and a v2
        // host never attach to each other.
        private const string PipePrefix = "GxMcpShared2_";

        // Physical_KB_Identity components of an attach frame's "identity" object, in reporting order.
        internal static readonly IReadOnlyList<string> IdentityComponents = new[]
        {
            "installationPath", "driver", "major", "workerExecutable", "profileConfigPath"
        };

        private static readonly HashSet<string> HostFrameTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "attach", "attach_ack", "heartbeat", "heartbeat_ack", "detach", "busy", "host_error",
            StatusFrameType, StatusAckFrameType, AdmissionFrameType
        };

        internal static bool TryParseEnvelope(string line, out SharedWorkerEnvelope envelope, out string error)
        {
            envelope = null;
            error = "empty host frame";
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
            {
                error = "host frame exceeds the maximum size";
                return false;
            }

            try
            {
                var frame = GxMcp.Common.JsonIngress.ParseObject(line);
                string type = frame.Value<string>("type") ?? frame.Value<string>("gxmcp") ?? string.Empty;
                if (!HostFrameTypes.Contains(type))
                {
                    error = "unknown host frame type";
                    return false;
                }

                envelope = FromJson(frame, type);
                if (envelope.ProtocolVersion != ProtocolVersion)
                {
                    error = "host protocol version mismatch";
                    envelope = null;
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = "malformed host frame: " + ex.Message;
                envelope = null;
                return false;
            }
        }

        internal static bool TryValidateAttach(SharedWorkerEnvelope envelope, string expectedIdentityKey, out string error)
        {
            error = string.Empty;
            if (envelope == null || !string.Equals(envelope.Type, "attach", StringComparison.Ordinal))
            {
                error = "expected attach frame";
                return false;
            }
            if (envelope.ProtocolVersion != ProtocolVersion)
            {
                error = "host protocol version mismatch";
                return false;
            }
            if (!IsIdentityKey(expectedIdentityKey) || !string.Equals(envelope.IdentityKey, expectedIdentityKey, StringComparison.Ordinal))
            {
                error = "shared Worker identity mismatch";
                return false;
            }
            if (envelope.GatewayPid <= 0 || envelope.GatewayStartTimeUtcTicks <= 0)
            {
                error = "Gateway process identity is missing";
                return false;
            }
            if (string.IsNullOrWhiteSpace(envelope.AttachNonce) || envelope.AttachNonce.Length < 16 || envelope.AttachNonce.Length > 128)
            {
                error = "attach nonce is missing or invalid";
                return false;
            }
            return true;
        }

        internal static bool TryValidateSessionEnvelope(SharedWorkerEnvelope envelope, bool attached, out string error)
        {
            error = string.Empty;
            if (envelope == null || !attached)
            {
                error = "connection is not attached";
                return false;
            }
            if (envelope.ProtocolVersion != ProtocolVersion)
            {
                error = "host protocol version mismatch";
                return false;
            }
            if (envelope.Type != "heartbeat" && envelope.Type != "detach" && envelope.Type != StatusFrameType)
            {
                error = "frame is not valid for an attached session";
                return false;
            }
            if (string.IsNullOrWhiteSpace(envelope.AttachmentId))
            {
                error = "attachment id is missing";
                return false;
            }
            if (envelope.Type == StatusFrameType
                && (string.IsNullOrWhiteSpace(envelope.RequestId) || envelope.RequestId.Length > MaxStatusRequestIdLength))
            {
                error = "status request id is missing or invalid";
                return false;
            }
            return true;
        }

        internal static bool TryValidateSessionEnvelope(
            SharedWorkerEnvelope envelope,
            bool attached,
            string expectedIdentityKey,
            string expectedAttachmentId,
            out string error)
        {
            if (!TryValidateSessionEnvelope(envelope, attached, out error)) return false;
            if (!string.Equals(envelope.IdentityKey, expectedIdentityKey, StringComparison.Ordinal))
            {
                error = "shared Worker identity mismatch";
                return false;
            }
            if (!string.Equals(envelope.AttachmentId, expectedAttachmentId, StringComparison.Ordinal))
            {
                error = "attachment identity mismatch";
                return false;
            }
            return true;
        }

        internal static bool TryRewriteRequestForChild(
            JObject clientRequest,
            string attachmentId,
            long sequence,
            out JObject childRequest,
            out SharedWorkerRequestRoute route,
            out string error)
        {
            childRequest = null;
            route = null;
            error = string.Empty;
            if (clientRequest == null || !string.Equals(clientRequest.Value<string>("jsonrpc"), "2.0", StringComparison.Ordinal))
            {
                error = "request is not JSON-RPC 2.0";
                return false;
            }
            if (clientRequest["id"] == null || clientRequest["id"].Type == JTokenType.Null)
            {
                error = "shared Worker requests require a non-null id";
                return false;
            }
            if (string.IsNullOrWhiteSpace(attachmentId) || sequence <= 0)
            {
                error = "request route identity is invalid";
                return false;
            }

            string childId = "gxmcp-" + attachmentId + "-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var rewritten = (JObject)clientRequest.DeepClone();
            rewritten["id"] = childId;

            JToken clientProgress = JValue.CreateNull();
            string childProgress = string.Empty;
            var meta = rewritten["_meta"] as JObject ?? new JObject();
            // The broker is the authoritative owner of the child request.  The
            // scheduler uses this field to create distinct client buckets; without
            // it every shared-host command falls into the default bucket.
            meta["attachmentId"] = attachmentId;
            if (meta["progressToken"] != null && meta["progressToken"].Type != JTokenType.Null)
            {
                clientProgress = clientRequest["_meta"]?["progressToken"]?.DeepClone() ?? JValue.CreateNull();
                childProgress = "gxmcp-progress-" + attachmentId + "-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                meta["progressToken"] = childProgress;
            }
            rewritten["_meta"] = meta;

            route = new SharedWorkerRequestRoute
            {
                AttachmentId = attachmentId,
                ClientRequestId = clientRequest["id"].DeepClone(),
                ChildRequestId = childId,
                ClientProgressToken = clientProgress,
                ChildProgressToken = childProgress
            };
            childRequest = rewritten;
            return true;
        }

        internal static bool TryRestoreResponse(
            JObject childResponse,
            SharedWorkerRequestRoute route,
            out JObject restored,
            out string error)
        {
            restored = null;
            error = string.Empty;
            if (childResponse == null || route == null || childResponse["id"] == null)
            {
                error = "response route is incomplete";
                return false;
            }
            if (!string.Equals(childResponse["id"]?.ToString(), route.ChildRequestId, StringComparison.Ordinal))
            {
                error = "response id does not belong to the route";
                return false;
            }
            restored = (JObject)childResponse.DeepClone();
            restored["id"] = route.ClientRequestId.DeepClone();
            return true;
        }

        internal static bool TryRewriteCancellationForChild(
            JObject clientNotification,
            IEnumerable<SharedWorkerRequestRoute> routes,
            string attachmentId,
            out JObject childNotification,
            out string error)
        {
            childNotification = null;
            error = string.Empty;
            if (clientNotification == null
                || !string.Equals(clientNotification.Value<string>("jsonrpc"), "2.0", StringComparison.Ordinal)
                || !string.Equals(clientNotification.Value<string>("method"), "notifications/cancelled", StringComparison.Ordinal))
            {
                error = "request is not a cancellation notification";
                return false;
            }

            JToken clientRequestId = clientNotification["params"]?["requestId"];
            if (clientRequestId == null || clientRequestId.Type == JTokenType.Null || string.IsNullOrWhiteSpace(attachmentId))
            {
                error = "cancellation notification is missing its request id or attachment";
                return false;
            }

            foreach (SharedWorkerRequestRoute route in routes ?? new List<SharedWorkerRequestRoute>())
            {
                if (!string.Equals(route.AttachmentId, attachmentId, StringComparison.Ordinal)
                    || !JToken.DeepEquals(route.ClientRequestId, clientRequestId))
                    continue;

                childNotification = (JObject)clientNotification.DeepClone();
                ((JObject)childNotification["params"])["requestId"] = route.ChildRequestId;
                return true;
            }

            error = "cancellation request is not owned by this attachment";
            return false;
        }

        internal static bool TryRouteChildNotification(
            JObject childNotification,
            IDictionary<string, SharedWorkerRequestRoute> progressOwners,
            out string attachmentId,
            out JObject routed,
            out bool broadcast,
            out string error)
        {
            attachmentId = null;
            routed = null;
            broadcast = false;
            error = string.Empty;
            if (childNotification == null)
            {
                error = "notification is null";
                return false;
            }

            string method = childNotification.Value<string>("method") ?? string.Empty;
            if (IsExplicitBroadcastNotification(method))
            {
                routed = (JObject)childNotification.DeepClone();
                broadcast = true;
                return true;
            }
            if (!string.Equals(method, "notifications/progress", StringComparison.Ordinal))
            {
                error = "notification has no attachment routing rule";
                return false;
            }

            string childToken = childNotification["params"]?["progressToken"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(childToken) || progressOwners == null || !progressOwners.TryGetValue(childToken, out var route))
            {
                error = "progress token is not owned by an attached request";
                return false;
            }
            routed = (JObject)childNotification.DeepClone();
            routed["params"]["progressToken"] = route.ClientProgressToken.DeepClone();
            attachmentId = route.AttachmentId;
            return true;
        }

        private static bool IsExplicitBroadcastNotification(string method)
        {
            return string.Equals(method, "notifications/resources/updated", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/sdk_ready", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/restarting", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/build_active", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/index_active", StringComparison.Ordinal);
        }

        internal static bool TryCreateIdentityKey(
            string workerExecutable,
            string kbPath,
            string installationPath,
            string driver,
            string major,
            out string identityKey,
            out string error)
        {
            identityKey = string.Empty;
            error = string.Empty;
            string executable = NormalizePath(workerExecutable);
            string kb = NormalizePath(kbPath);
            string installation = NormalizePath(installationPath);
            string normalizedDriver = NormalizeToken(driver);
            string normalizedMajor = NormalizeToken(major);
            if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(installation)
                || string.IsNullOrWhiteSpace(normalizedDriver) || string.IsNullOrWhiteSpace(normalizedMajor))
            {
                error = "shared Worker identity requires executable, KB, installation, driver, and major";
                return false;
            }

            string material = string.Join("\n", executable, kb, installation, normalizedDriver, normalizedMajor);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) builder.Append(value.ToString("x2"));
                identityKey = builder.ToString();
            }
            return true;
        }

        internal static bool TryBuildPipeName(string identityKey, out string pipeName, out string error)
        {
            pipeName = string.Empty;
            error = string.Empty;
            if (!IsIdentityKey(identityKey))
            {
                error = "identity key must be a lowercase SHA-256 hex value";
                return false;
            }
            pipeName = PipePrefix + identityKey;
            return true;
        }

        internal static bool IsValidPipeName(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName) || !pipeName.StartsWith(PipePrefix, StringComparison.Ordinal)) return false;
            return IsIdentityKey(pipeName.Substring(PipePrefix.Length));
        }

        internal static bool IsIdentityKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        private static SharedWorkerEnvelope FromJson(JObject frame, string type)
        {
            return new SharedWorkerEnvelope
            {
                Type = type,
                ProtocolVersion = frame.Value<int?>("protocolVersion") ?? frame.Value<int?>("version") ?? 0,
                IdentityKey = frame.Value<string>("identityKey") ?? frame.Value<string>("key") ?? string.Empty,
                GatewayPid = frame.Value<int?>("gatewayPid") ?? 0,
                GatewayStartTimeUtcTicks = frame.Value<long?>("gatewayStartTimeUtcTicks") ?? 0,
                AttachNonce = frame.Value<string>("attachNonce") ?? frame.Value<string>("nonce") ?? string.Empty,
                AttachmentId = frame.Value<string>("attachmentId") ?? frame.Value<string>("attachId") ?? string.Empty,
                Message = frame.Value<string>("message") ?? string.Empty,
                RequestId = ReadScalarString(frame["requestId"]),
                StopIfLast = frame["stopIfLast"]?.Type == JTokenType.Boolean && frame.Value<bool>("stopIfLast"),
                Identity = frame["identity"] as JObject
            };
        }

        /// <summary>
        /// The Physical_KB_Identity components of an attach frame's <c>identity</c> object that differ from the
        /// host's own (normalized) components. Paths compare after path normalization, driver and major as
        /// trimmed lowercase tokens; an absent or null component compares as empty. Empty when the frame carries
        /// no identity object.
        /// </summary>
        internal static IReadOnlyList<string> DescribeIdentityMismatch(JObject requested, IDictionary<string, string> live)
        {
            var differing = new List<string>();
            if (requested == null || live == null) return differing;
            foreach (string component in IdentityComponents)
            {
                JToken token = requested[component];
                string value = token == null || token.Type == JTokenType.Null ? string.Empty : token.ToString();
                bool isPath = !string.Equals(component, "driver", StringComparison.Ordinal)
                    && !string.Equals(component, "major", StringComparison.Ordinal);
                string normalized = isPath ? NormalizePath(value) : NormalizeToken(value);
                live.TryGetValue(component, out string liveValue);
                if (!string.Equals(normalized, liveValue ?? string.Empty, StringComparison.Ordinal))
                    differing.Add(component);
            }
            return differing;
        }

        private static string ReadScalarString(JToken token)
        {
            if (token == null) return string.Empty;
            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer)
                return Convert.ToString(((JValue)token).Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            return string.Empty;
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                return Path.GetFullPath(value)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant();
            }
            catch
            {
                return value.Trim().TrimEnd('\\', '/').ToLowerInvariant();
            }
        }

        private static string NormalizeToken(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
