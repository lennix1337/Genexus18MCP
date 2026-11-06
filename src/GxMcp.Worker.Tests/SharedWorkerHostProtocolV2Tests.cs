// Unit tests for the pure protocol version 2 helpers of SharedWorkerHostProtocol (RFC #854,
// task 3.10): host frame types, the Busy_Envelope, the Cancelled answer, the child-exit error,
// busyWaitMs, the _gxmcpShared response metadata and the status_ack frame. The snapshots come
// from the real SharedHostScheduler on a test clock; no SDK, KB or process is involved.
using System;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SharedWorkerHostProtocolV2Tests
    {
        private const string LeakMarker = "LEAK-MARKER-V2";
        private static readonly string IdentityKey = new string('b', 64);

        // ---- Host frames ----

        [Fact]
        public void Protocol_Version_Is_2()
        {
            Assert.Equal(2, SharedWorkerHostProtocol.ProtocolVersion);
        }

        [Theory]
        [InlineData("status")]
        [InlineData("status_ack")]
        [InlineData("admission")]
        public void New_Frame_Types_Parse(string type)
        {
            var frame = SharedWorkerHostProtocol.CreateHostFrame(type, IdentityKey);
            Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame.ToString(Formatting.None), out var envelope, out string error), error);
            Assert.Equal(type, envelope.Type);
        }

        [Fact]
        public void A_Version_1_Frame_Is_Rejected()
        {
            var frame = SharedWorkerHostProtocol.CreateHostFrame("heartbeat", IdentityKey);
            frame["protocolVersion"] = 1;
            frame["version"] = 1;
            Assert.False(SharedWorkerHostProtocol.TryParseEnvelope(frame.ToString(Formatting.None), out _, out string error));
            Assert.Equal("host protocol version mismatch", error);
        }

        [Fact]
        public void A_Status_Frame_Is_A_Valid_Session_Frame_Only_With_A_Request_Id()
        {
            var frame = SharedWorkerHostProtocol.CreateHostFrame("status", IdentityKey);
            frame["attachId"] = "att-1";
            frame["attachmentId"] = "att-1";
            frame["requestId"] = "status-7";
            Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame.ToString(Formatting.None), out var envelope, out string error), error);
            Assert.Equal("status-7", envelope.RequestId);
            Assert.True(SharedWorkerHostProtocol.TryValidateSessionEnvelope(envelope, true, IdentityKey, "att-1", out error), error);

            frame.Remove("requestId");
            Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame.ToString(Formatting.None), out envelope, out error), error);
            Assert.False(SharedWorkerHostProtocol.TryValidateSessionEnvelope(envelope, true, IdentityKey, "att-1", out error));
            Assert.Equal("status request id is missing or invalid", error);
        }

        [Fact]
        public void A_Detach_Frame_Carries_StopIfLast()
        {
            var frame = SharedWorkerHostProtocol.CreateHostFrame("detach", IdentityKey);
            frame["attachmentId"] = "att-1";
            Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame.ToString(Formatting.None), out var envelope, out _));
            Assert.False(envelope.StopIfLast);

            frame["stopIfLast"] = true;
            Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame.ToString(Formatting.None), out envelope, out _));
            Assert.True(envelope.StopIfLast);
        }

        // ---- Busy_Envelope ----

        [Theory]
        [InlineData("queue-full", "queueDepth", 8)]
        [InlineData("session-queue-limit", "sessionQueueCap", 4)]
        [InlineData("queue-wait-expired", "queuePosition", 3)]
        public void Busy_Envelope_Is_A_Tool_Result_With_Exactly_One_Queue_Field(string reason, string field, int expected)
        {
            long now = 1000;
            var scheduler = Scheduler(() => now);
            Submit(scheduler, "r1", "att-a", "build/BuildAll", now);
            now += 4250;
            var snapshot = scheduler.Snapshot();

            var frame = SharedWorkerHostProtocol.BuildBusyResult(
                new JValue("client-9"), reason, snapshot, new SchedulerLimits(8, 4, 30000),
                IdentityKey, @"C:\KBs\KbA", reason == "queue-wait-expired" ? 3 : (int?)null, retryAfterMs: 5000);

            Assert.Equal("2.0", (string)frame["jsonrpc"]);
            Assert.Equal("client-9", (string)frame["id"]);
            Assert.Null(frame.Property("error"));
            Assert.Equal("error", (string)frame["result"]["status"]);
            Assert.Equal("WorkerBusy", (string)frame["result"]["code"]);

            var error = (JObject)frame["result"]["error"];
            var catalog = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.WorkerBusy, reason, new ConcurrencyErrorFields { QueueWaitMs = 30000 });
            Assert.Equal("WorkerBusy", (string)error["code"]);
            Assert.Equal(reason, (string)error["reason"]);
            Assert.Equal((string)catalog["message"], (string)error["message"]);
            Assert.Equal((string)catalog["hint"], (string)error["hint"]);
            Assert.True((bool)error["retryable"]);
            Assert.Equal("not-delivered", (string)error["dispatchState"]);
            Assert.Equal(5000L, (long)error["retryAfterMs"]);
            Assert.Equal(IdentityKey, (string)error["kbIdentityKey"]);
            Assert.Equal(@"C:\KBs\KbA", (string)error["kbPath"]);
            Assert.Equal("build/BuildAll", (string)error["runningOperation"]);
            Assert.Equal(4250L, (long)error["runningElapsedMs"]);

            var present = new[] { "queuePosition", "queueDepth", "sessionQueueCap" }.Where(f => error[f] != null).ToArray();
            Assert.Equal(new[] { field }, present);
            Assert.Equal(expected, (int)error[field]);
        }

        [Theory]
        [InlineData(null, 1000L)]
        [InlineData(10L, 1000L)]
        [InlineData(900000L, 30000L)]
        public void Busy_Envelope_RetryAfter_Is_Clamped_To_1000_And_The_Queue_Wait(long? requested, long expected)
        {
            var scheduler = Scheduler(() => 1000);
            Submit(scheduler, "r1", "att-a", "object/Read", 1000);
            var frame = SharedWorkerHostProtocol.BuildBusyResult(
                new JValue(1), "queue-full", scheduler.Snapshot(), new SchedulerLimits(8, 4, 30000),
                IdentityKey, null, null, requested);

            var error = (JObject)frame["result"]["error"];
            Assert.Equal(expected, (long)error["retryAfterMs"]);
            Assert.Null(error["kbPath"]);
        }

        [Fact]
        public void Busy_Envelope_Carries_No_Payload_Or_Argument_Of_Any_Request()
        {
            var scheduler = Scheduler(() => 1000);
            Submit(scheduler, "r1", "att-a", "object/Write", 1000);
            Submit(scheduler, "r2", "att-b", "object/Read", 1000);

            var frame = SharedWorkerHostProtocol.BuildBusyResult(
                new JValue(5), "queue-full", scheduler.Snapshot(), new SchedulerLimits(2, 1, 30000),
                IdentityKey, @"C:\KBs\KbA");

            string text = frame.ToString(Formatting.None);
            Assert.DoesNotContain(LeakMarker, text, StringComparison.Ordinal);
            Assert.DoesNotContain("leaseToken", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sourceText", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("att-a", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Busy_Envelope_Rejects_An_Unknown_Reason_And_A_Missing_Expiry_Position()
        {
            var snapshot = Scheduler(() => 1000).Snapshot();
            var limits = new SchedulerLimits(8, 4, 30000);
            Assert.Throws<ArgumentException>(() => SharedWorkerHostProtocol.BuildBusyResult(new JValue(1), "busy", snapshot, limits, IdentityKey, null));
            Assert.Throws<ArgumentException>(() => SharedWorkerHostProtocol.BuildBusyResult(new JValue(1), "queue-wait-expired", snapshot, limits, IdentityKey, null));
            Assert.Throws<ArgumentException>(() => SharedWorkerHostProtocol.BuildBusyResult(JValue.CreateNull(), "queue-full", snapshot, limits, IdentityKey, null));
        }

        // ---- Cancelled and restarted ----

        [Fact]
        public void Cancelled_Answer_Is_An_Ok_Tool_Result_With_The_Client_Id()
        {
            var frame = SharedWorkerHostProtocol.BuildCancelledResult(new JValue(7));
            Assert.Equal(7, (int)frame["id"]);
            Assert.Null(frame["error"]);
            Assert.Equal("ok", (string)frame["result"]["status"]);
            Assert.Equal("Cancelled", (string)frame["result"]["code"]);
        }

        [Theory]
        [InlineData("delivered-unanswered", "object", "Read", true)]
        [InlineData("delivered-unanswered", "object", "Create", false)]
        [InlineData("delivered-unanswered", "no-such-command", null, false)]
        [InlineData("not-delivered", "object", "Create", true)]
        public void Restarted_Error_Uses_The_Catalog_Row_And_The_Operation_Class(string dispatchState, string method, string action, bool retryable)
        {
            var frame = SharedWorkerHostProtocol.BuildRestartedError(new JValue("c-1"), dispatchState, method, action, 4, 30000);

            Assert.Equal("c-1", (string)frame["id"]);
            Assert.Null(frame["result"]);
            var error = (JObject)frame["error"];
            Assert.Equal(-32050, (int)error["code"]);
            var data = (JObject)error["data"];
            Assert.Equal("SHARED_WORKER_RESTARTED", (string)data["code"]);
            Assert.Equal(dispatchState, (string)data["dispatchState"]);
            Assert.Equal(retryable, (bool)data["retryable"]);
            Assert.Equal(4L, (long)data["generation"]);
            Assert.Equal((string)data["message"], (string)error["message"]);
            Assert.Equal(retryable, data["retryAfterMs"] != null);
        }

        [Fact]
        public void Restarted_Error_Refuses_A_Delivered_Rejected_State()
        {
            Assert.Throws<ArgumentException>(() =>
                SharedWorkerHostProtocol.BuildRestartedError(new JValue(1), "delivered-rejected", "object", "Read", 1, 30000));
        }

        // ---- busyWaitMs and _gxmcpShared ----

        [Theory]
        [InlineData(30000, 0L, 30000)]
        [InlineData(30000, 10000L, 20000)]
        [InlineData(30000, 29500L, 1000)]
        [InlineData(30000, 90000L, 1000)]
        [InlineData(30000, -5L, 30000)]
        public void BusyWaitMs_Is_The_Remaining_Queue_Wait_With_A_1000_Ms_Floor(int queueWaitMs, long waited, int expected)
        {
            Assert.Equal(expected, SharedWorkerHostProtocol.ComputeBusyWaitMs(queueWaitMs, waited));
        }

        [Fact]
        public void Shared_Metadata_Is_A_Top_Level_Object_With_Exactly_Wait_And_Pid()
        {
            var payload = new JObject { ["status"] = "ok", ["_meta"] = new JObject { ["existing"] = "kept" } };
            var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["result"] = payload.DeepClone() };

            Assert.True(SharedWorkerHostProtocol.TryInjectSharedMetadata(response, 1700, 4321));

            var shared = (JObject)response["_gxmcpShared"];
            Assert.Equal(new[] { "queueWaitMs", "workerPid" }, shared.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(JTokenType.Integer, shared["queueWaitMs"].Type);
            Assert.Equal(1700L, (long)shared["queueWaitMs"]);
            Assert.Equal(4321, (int)shared["workerPid"]);
            Assert.True(JToken.DeepEquals(payload, response["result"]), "the result must stay unchanged");
        }

        [Fact]
        public void Shared_Metadata_Is_Not_Added_To_An_Error_Response()
        {
            var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["error"] = new JObject { ["code"] = -32000 } };
            Assert.False(SharedWorkerHostProtocol.TryInjectSharedMetadata(response, 10, 4321));
            Assert.Null(response["_gxmcpShared"]);
        }

        // ---- status_ack ----

        [Fact]
        public void Status_Ack_Reports_Running_And_Queue_From_The_Snapshot_Only()
        {
            long now = 1000;
            var scheduler = Scheduler(() => now);
            Submit(scheduler, "r1", "att-a", "object/Read", now);
            now += 200;
            Submit(scheduler, "r2", "att-b", "object/Write", now);
            now += 300;

            var ack = SharedWorkerHostProtocol.BuildStatusAck(IdentityKey, "status-1", scheduler.Snapshot(), new SharedHostStatusContext
            {
                ChildState = "ready",
                WorkerPid = 5012,
                WorkerWorkingSetMb = 640,
                Attachments = 2,
                EffectiveSettings = new JObject { ["queueDepth"] = 8 }
            });

            Assert.Equal("status_ack", (string)ack["type"]);
            Assert.Equal("status_ack", (string)ack["gxmcp"]);
            Assert.Equal(2, (int)ack["protocolVersion"]);
            Assert.Equal(2, (int)ack["version"]);
            Assert.Equal(IdentityKey, (string)ack["identityKey"]);
            Assert.Equal("status-1", (string)ack["requestId"]);
            Assert.Equal("ready", (string)ack["childState"]);
            Assert.Equal(5012, (int)ack["workerPid"]);
            Assert.Equal(640L, (long)ack["workerWorkingSetMb"]);
            Assert.False((bool)ack["idle"]);
            Assert.Equal("object/Read", (string)ack["running"]["operation"]);
            Assert.Equal("att-a", (string)ack["running"]["ownerAttachId"]);
            Assert.Equal(500L, (long)ack["running"]["elapsedMs"]);

            var entry = (JObject)((JArray)ack["queue"]).Single();
            Assert.Equal(1, (int)entry["position"]);
            Assert.Equal("att-b", (string)entry["ownerAttachId"]);
            Assert.Equal(300L, (long)entry["waitMs"]);
            Assert.Equal("sta-busy", (string)entry["waitReason"]);
            Assert.Equal("object/Read", (string)entry["operation"]);
            Assert.Equal(1, (int)ack["queueDepth"]);
            Assert.Equal(2, (int)ack["attachments"]);
            Assert.Equal(8, (int)ack["effectiveSettings"]["queueDepth"]);
            Assert.Null(ack["admission"]);

            string text = ack.ToString(Formatting.None);
            Assert.DoesNotContain(LeakMarker, text, StringComparison.Ordinal);
            Assert.DoesNotContain("leaseToken", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cancelToken", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("r2", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Status_Ack_Reports_An_Idle_Worker_And_A_Starting_Wait_Without_An_Operation()
        {
            long now = 1000;
            var scheduler = Scheduler(() => now);
            scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);
            Submit(scheduler, "r1", "att-a", "object/Read", now);

            var ack = SharedWorkerHostProtocol.BuildStatusAck(IdentityKey, "s", scheduler.Snapshot(), null);

            Assert.True((bool)ack["idle"]);
            Assert.Equal(JTokenType.Null, ack["running"].Type);
            var entry = (JObject)((JArray)ack["queue"]).Single();
            Assert.Equal("worker-starting", (string)entry["waitReason"]);
            Assert.Null(entry["operation"]);
        }

        [Fact]
        public void Status_Ack_Requires_A_Bounded_Request_Id()
        {
            var snapshot = Scheduler(() => 1000).Snapshot();
            Assert.Throws<ArgumentException>(() => SharedWorkerHostProtocol.BuildStatusAck(IdentityKey, "", snapshot, null));
            Assert.Throws<ArgumentException>(() => SharedWorkerHostProtocol.BuildStatusAck(IdentityKey, new string('x', 129), snapshot, null));
        }

        // ---- Helpers ----

        private static SharedHostScheduler Scheduler(Func<long> clock) =>
            new SharedHostScheduler(new SchedulerLimits(8, 4, 30000), clock);

        private static void Submit(SharedHostScheduler scheduler, string childId, string session, string operation, long now)
        {
            var result = scheduler.Submit(new QueuedRequest
            {
                ChildRequestId = childId,
                SessionId = session,
                ClientRequestIdJson = "\"" + childId + "\"",
                Operation = operation,
                CancelToken = LeakMarker + "-cancel",
                EnqueuedAtMs = now,
                Sequence = now,
                Payload = new JObject
                {
                    ["params"] = new JObject { ["leaseToken"] = LeakMarker + "-lease", ["sourceText"] = LeakMarker + "-source" }
                }.ToString(Formatting.None)
            });
            Assert.True(result.Started || result.Queued, "the scheduler refused a test request: " + result.RejectReason);
        }
    }
}
