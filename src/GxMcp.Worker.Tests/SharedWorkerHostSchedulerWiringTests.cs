// Broker wiring tests for the shared-host Worker_Scheduler (RFC #854, task 3.5).
//
// These tests are written before the implementation (task 3.11). They fix the seam
// contract below; task 3.11 implements exactly these types, members and names, and
// tasks 3.6, 3.7 and 3.8 reuse the fakes in this file's style. Every seam is internal
// (GxMcp.Worker grants InternalsVisibleTo("GxMcp.Worker.Tests")), so production
// behavior is unchanged: the production constructor wires the system implementations.
//
// Classifier used by the runtime (not called by these tests):
//   Receive classifies a JSON-RPC request with CommandLaneClassifier.IsThreadSafe(JObject)
//   (task 3.3, src/GxMcp.Worker/Services/CommandLaneClassifier.cs) plus the control
//   commands (notifications/cancelled, control/Cancel, health). A bypass command goes
//   straight to the child inbox; every other request is an SDK_Operation and goes to
//   SharedHostScheduler.Submit. The tests share the "CommandLaneClassifierEnvironment"
//   collection because the classifier reads GXMCP_EXPECTED_KB_PATH/_VERSION.
//
// New types (namespace GxMcp.Worker, file SharedWorkerHost.cs or a sibling file that the
// TestHost project also links):
//
//   internal interface ISharedHostClock
//       long NowMs { get; }                          // monotonic milliseconds
//       ISharedHostTimer CreateTimer(Action callback);
//     Production: Stopwatch-based clock; timers wrap one System.Threading.Timer each.
//
//   internal interface ISharedHostTimer : IDisposable
//       void Change(long? dueAtMs);
//     Arms the timer to run its callback once at the absolute monotonic time dueAtMs,
//     replacing any earlier arming; null disarms it. The queue-expiry timer is armed from
//     SharedHostScheduler.NextDeadlineMs and must be armed at an instant at which
//     ExpireDue removes something (the scheduler expires strictly after the deadline), so
//     it never re-arms itself at the current time in a loop.
//
//   internal interface ISharedHostChildLauncher
//       ISharedHostChild Start(ProcessStartInfo startInfo);   // throws when the start fails
//     The runtime builds the ProcessStartInfo exactly as StartChild does today (plus
//     GXMCP_BUSY_REJECT_MS=0) and hands it to the launcher. Production: Process.Start.
//
//   internal interface ISharedHostChild : IDisposable
//       int Pid { get; }
//       long StartTimeUtcTicks { get; }
//       bool HasExited { get; }
//       int ExitCode { get; }                 // -1 while running
//       long WorkingSetBytes { get; }         // refreshed on read; 0 after exit
//       long PrivateBytes { get; }            // refreshed on read; 0 after exit
//       WaitHandle ExitedHandle { get; }      // signalled when the process exits
//       TextReader StandardOutput { get; }
//       TextReader StandardError { get; }
//       void WriteLine(string line);          // writes one frame to stdin and flushes
//       void Kill();
//     Keep the "_child = child;" assignment followed by the generation increment and the
//     election reset: SharedWorkerSupervisionTests pins that text.
//
//   internal interface ISharedHostRegistryWriter
//       void Write(SharedWorkerHostRegistryRecord record);
//       void Delete();
//     Production: today's WriteRecord body (temp file plus File.Replace) and the
//     Dispose-time delete.
//
//   internal interface ISharedHostAttachment
//       string ConnectionId { get; }
//       string AttachmentId { get; set; }
//       long NextSequence();
//       bool Enqueue(string line);            // one outbound line to this Attachment
//       void Stop();                          // the transport then calls runtime.Detach
//     SharedWorkerHostAttachment implements it. EnqueueChild, TryDequeueChild and
//     ClearChildQueue are removed: the child inbox belongs to the runtime.
//
// Changed or new members of SharedWorkerHostRuntime:
//
//   internal SharedWorkerHostRuntime(
//       SharedWorkerHostOptions options,
//       SharedHostSettingsResult settings,       // QueueDepth, SessionQueueCap, QueueWaitMs
//       ISharedHostClock clock,
//       ISharedHostChildLauncher childLauncher,
//       ISharedHostRegistryWriter registryWriter,
//       bool runBackgroundThreads)
//     The existing SharedWorkerHostRuntime(options) constructor delegates to it with the
//     system implementations and runBackgroundThreads=true. With false, the runtime
//     starts no thread and no pipe: no accept loop, no child reader, stderr reader or
//     inbox writer, and no supervisor. The test drives every event through the entry
//     points below on its own thread.
//   internal void Start()
//     Starts the first child through the launcher and writes the record (state
//     "starting"); with background threads it also starts the threads. Run() calls it.
//   internal bool Attach(ISharedHostAttachment attachment, SharedWorkerEnvelope request, out string error)
//   internal void Receive(ISharedHostAttachment attachment, string line)
//   internal void Detach(ISharedHostAttachment attachment)
//     Detach is the pipe-EOF path (RunAsync's finally); a "detach" frame calls
//     attachment.Stop(), which ends the transport and so reaches Detach.
//   internal void OnChildLine(ISharedHostChild child, string line)
//     One stdout line of that child, as the child reader loop delivers it. Lines of a
//     child that is no longer current are ignored.
//   internal void OnChildExited(ISharedHostChild child)
//     Called by the supervisor when the child's ExitedHandle is signalled. Applies the
//     exit outcomes, the three-exits-in-one-minute fail-closed rule, and starts the
//     replacement through the launcher before it returns.
//   internal int PumpChildInbox()
//     Writes every pending child-inbox line to the current child, in order, on the
//     caller's thread, and returns how many it wrote. Only for runBackgroundThreads=false;
//     with threads the inbox writer consumes the inbox with GetConsumingEnumerable.
//
// Wire shapes fixed here:
//   - Busy_Envelope: a JSON-RPC result, never a JSON-RPC error:
//     { jsonrpc, id=<client id>, result={ status="error", code="WorkerBusy",
//       error=ConcurrencyErrorCatalog.Build(WorkerBusy, reason, ...) plus queuePosition,
//       queueDepth or sessionQueueCap, runningOperation, runningElapsedMs } }.
//   - Cancelled answer for a request removed from the broker queue:
//     { jsonrpc, id=<client id>, result={ status="ok", code="Cancelled", ... } }, the
//     McpResponse.Ok(code: "Cancelled") envelope that control/Cancel uses today.
//   - Child exit: { jsonrpc, id=<client id>, error={ code=-32050, message,
//     data=ConcurrencyErrorCatalog.Build(SHARED_WORKER_RESTARTED, null, { DispatchState,
//     OperationClass=WorkerCommandCatalog.Classify(method, action) }) plus generation } }.
//   - Released SDK_Operations carry _meta.busyWaitMs = max(1000, QueueWaitMs - waited).
//   - status frame -> status_ack host frame with the same requestId; never queued.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Collection("CommandLaneClassifierEnvironment")]
    public sealed class SharedWorkerHostSchedulerWiringTests
    {
        private const int DefaultQueueWaitMs = 30000;

        // ---- Bypass lane (Requirement 3 criterion 8) ----

        [Fact]
        public void Thread_Safe_And_Control_Commands_Bypass_The_Queue_And_Do_Not_Count()
        {
            using (var h = new Harness(queueDepth: 2, sessionQueueCap: 2))
            {
                var a = h.Attach();
                h.Send(a, Sdk(1));
                Assert.Single(h.Pump());
                h.Send(a, Sdk(2));
                Assert.Empty(h.Pump());

                h.Send(a, Request(10, "ping", null));
                h.Send(a, Request(11, "list", "Objects"));
                h.Send(a, Request(12, "health", null));
                h.Send(a, Request(13, "system", "GetSdkBusyStatus"));
                h.Send(a, Request(14, "control", "Cancel", new JObject { ["cancelToken"] = "no-such-token" }));

                var bypassed = h.Pump();
                Assert.Equal(new[] { "10", "11", "12", "13", "14" }, bypassed.Select(ClientIdOf).ToArray());
                Assert.Empty(a.JsonRpc());

                // Five bypass commands did not take queue slots: one SDK slot is still free.
                h.Send(a, Sdk(3));
                Assert.Empty(a.JsonRpc());
                h.Send(a, Sdk(4));
                AssertBusy(a.JsonRpc().Single(), 4, SubmitResult.QueueFull, DefaultQueueWaitMs);
            }
        }

        // ---- SDK lane, dispatch window of one (Requirements 3.1, 3.2, 3.10, 6.7, 6.8) ----

        [Fact]
        public void An_Sdk_Operation_On_An_Idle_Worker_Is_Released_At_Once()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                h.Send(a, Sdk(1));

                var released = h.Pump();
                Assert.Single(released);
                Assert.Equal("1", ClientIdOf(released[0]));
                Assert.Empty(a.JsonRpc());
            }
        }

        [Fact]
        public void Only_One_Sdk_Operation_Is_Released_At_A_Time()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(1));
                h.Send(b, Sdk(1));
                h.Send(a, Sdk(2));
                h.Send(b, Sdk(2));

                var released = h.Pump();
                Assert.Single(released);
                Assert.Empty(h.Pump());
                Assert.Empty(a.JsonRpc());
                Assert.Empty(b.JsonRpc());

                // The queue grows without another Worker process.
                Assert.Single(h.Launcher.Started);
            }
        }

        [Fact]
        public void The_Next_Request_Is_Released_On_The_Child_Response_In_Fair_Order()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(1));
                var first = h.Pump().Single();
                h.Send(a, Sdk(2));
                h.Send(a, Sdk(3));
                h.Send(b, Sdk(1));
                h.Send(b, Sdk(2));

                long before = h.Clock.NowMs;
                var order = new List<string>();
                var current = first;
                for (int i = 0; i < 4; i++)
                {
                    h.Respond(current);
                    var next = h.Pump();
                    // Released by the response itself: no clock movement, no timer.
                    Assert.Equal(before, h.Clock.NowMs);
                    current = Assert.Single(next);
                    order.Add(OwnerOf(current, a, b) + ClientIdOf(current));
                }

                // Per-Session FIFO with rotation in first-enqueue order.
                Assert.Equal(new[] { "a2", "b1", "a3", "b2" }, order.ToArray());
            }
        }

        [Fact]
        public void Released_Requests_Carry_The_Remaining_Queue_Wait_As_BusyWaitMs()
        {
            using (var h = new Harness(queueWaitMs: DefaultQueueWaitMs))
            {
                var a = h.Attach();
                h.Send(a, Sdk(1));
                var r1 = h.Pump().Single();
                Assert.Equal(DefaultQueueWaitMs, BusyWaitOf(r1));

                h.Send(a, Sdk(2));
                h.Send(a, Sdk(3));

                h.Clock.Advance(10000);
                h.Respond(r1);
                var r2 = h.Pump().Single();
                Assert.Equal(DefaultQueueWaitMs - 10000, BusyWaitOf(r2));

                h.Clock.Advance(19500);
                h.Respond(r2);
                var r3 = h.Pump().Single();
                // 29500 ms waited leaves 500 ms; the floor is 1000 ms.
                Assert.Equal(1000, BusyWaitOf(r3));
            }
        }

        // ---- Busy_Envelope (Requirements 3.4, 3.5, 3.6, 3.15) ----

        [Fact]
        public void Queue_Full_Is_Answered_With_A_Tool_Result_Busy_Envelope()
        {
            using (var h = new Harness(queueDepth: 1, sessionQueueCap: 1))
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(1));
                h.Pump();
                h.Send(a, Sdk(2));
                h.Send(b, Sdk(9));

                var error = AssertBusy(b.JsonRpc().Single(), 9, SubmitResult.QueueFull, DefaultQueueWaitMs);
                Assert.Equal(1, (int)error["queueDepth"]);
                Assert.Null(error["sessionQueueCap"]);
                Assert.Null(error["queuePosition"]);
                Assert.Empty(a.JsonRpc());
                Assert.Empty(h.Pump());
            }
        }

        [Fact]
        public void Session_Queue_Limit_Is_Answered_With_A_Tool_Result_Busy_Envelope()
        {
            using (var h = new Harness(queueDepth: 4, sessionQueueCap: 1))
            {
                var a = h.Attach();
                h.Send(a, Sdk(1));
                h.Pump();
                h.Send(a, Sdk(2));
                h.Send(a, Sdk(3));

                var error = AssertBusy(a.JsonRpc().Single(), 3, SubmitResult.SessionQueueLimit, DefaultQueueWaitMs);
                Assert.Equal(1, (int)error["sessionQueueCap"]);
                Assert.Null(error["queueDepth"]);
            }
        }

        [Fact]
        public void Queue_Wait_Expiry_Is_Answered_From_The_Timer()
        {
            using (var h = new Harness(queueWaitMs: 1000))
            {
                var a = h.Attach();
                h.Send(a, Sdk(1));
                var running = h.Pump().Single();
                h.Send(a, Sdk(2));

                h.Clock.Advance(900);
                Assert.Empty(a.JsonRpc());

                h.Clock.Advance(101);
                var error = AssertBusy(a.JsonRpc().Single(), 2, "queue-wait-expired", 1000);
                Assert.Equal(1, (int)error["queuePosition"]);

                // The expired request is never written to the child.
                h.Respond(running);
                Assert.Empty(h.Pump());
            }
        }

        // ---- Cancellation (Requirements 3.7, 8.3, 8.7) ----

        [Fact]
        public void Cancelling_A_Queued_Request_Answers_Cancelled_To_That_Attachment_Only()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(1));
                var running = h.Pump().Single();
                h.Send(a, Sdk(7));
                h.Send(b, Sdk(7));

                h.Send(a, CancelNotification(7));

                var answer = a.JsonRpc().Single();
                Assert.Equal("7", answer["id"].ToString());
                Assert.Null(answer["error"]);
                Assert.Equal("ok", (string)answer["result"]["status"]);
                Assert.Equal("Cancelled", (string)answer["result"]["code"]);
                Assert.Empty(b.JsonRpc());
                // Removed from the broker queue, so nothing is forwarded to the child.
                Assert.Empty(h.Pump());

                h.Respond(running);
                var next = h.Pump().Single();
                Assert.Equal("b", OwnerOf(next, a, b));
                Assert.Equal("7", ClientIdOf(next));
            }
        }

        [Fact]
        public void Cancelling_An_In_Flight_Request_Is_Forwarded_For_That_Attachment_Only()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(7));
                var running = h.Pump().Single();
                h.Send(b, Sdk(7));

                h.Send(a, CancelNotification(7));

                var forwarded = h.Pump().Single();
                Assert.Equal("notifications/cancelled", (string)forwarded["method"]);
                Assert.Equal(running["id"].ToString(), forwarded["params"]["requestId"].ToString());
                Assert.Empty(a.JsonRpc());
                Assert.Empty(b.JsonRpc());
            }
        }

        [Fact]
        public void A_Cancel_That_Matches_No_Own_Request_Is_Dropped()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(b, Sdk(99));
                var running = h.Pump().Single();
                h.Send(b, Sdk(98));

                h.Send(a, CancelNotification(99));
                h.Send(a, CancelNotification(98));

                Assert.Empty(h.Pump());
                Assert.Empty(a.JsonRpc());
                Assert.Empty(b.JsonRpc());

                // b's requests are untouched: 98 is still queued behind 99.
                h.Respond(running);
                Assert.Equal("99", b.JsonRpc().Single()["id"].ToString());
                Assert.Equal("98", ClientIdOf(h.Pump().Single()));
            }
        }

        // ---- Detach and pipe EOF (Requirements 3.11, 8.8) ----

        [Fact]
        public void Detach_Frame_Purges_Queued_Requests_And_Discards_In_Flight_Output()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                AssertDisconnectPurges(h, a, b, () => h.Send(a, HostFrame(h, "detach", a)));
            }
        }

        [Fact]
        public void Pipe_Eof_Purges_Queued_Requests_And_Discards_In_Flight_Output()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                AssertDisconnectPurges(h, a, b, () => h.Runtime.Detach(a));
            }
        }

        private static void AssertDisconnectPurges(Harness h, FakeAttachment a, FakeAttachment b, Action disconnect)
        {
            h.Send(a, Sdk(1, progressToken: "p-a"));
            var running = h.Pump().Single();
            h.Send(a, Sdk(2));
            h.Send(a, Sdk(3));
            h.Send(b, Sdk(4));

            disconnect();
            int linesAtDisconnect = a.Lines.Count;

            h.Progress(running);
            h.Respond(running);

            Assert.Equal(linesAtDisconnect, a.Lines.Count);
            Assert.Empty(b.JsonRpc());

            // a's queued requests are gone; b's request is next.
            var next = h.Pump().Single();
            Assert.Equal("b", OwnerOf(next, a, b));
            Assert.Equal("4", ClientIdOf(next));
            h.Respond(next);
            Assert.Equal("4", b.JsonRpc().Single()["id"].ToString());
            Assert.Empty(h.Pump());
        }

        // ---- Routing (Requirements 8.1, 8.2) ----

        [Fact]
        public void Responses_And_Progress_Reach_Only_The_Owning_Attachment()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(5, progressToken: "token-a"));
                var fromA = h.Pump().Single();
                h.Send(b, Sdk(5, progressToken: "token-b"));

                h.Progress(fromA);
                h.Respond(fromA);

                var toA = a.JsonRpc();
                Assert.Equal(2, toA.Count);
                Assert.Equal("token-a", (string)toA[0]["params"]["progressToken"]);
                Assert.Equal("5", toA[1]["id"].ToString());
                Assert.Empty(b.JsonRpc());

                var fromB = h.Pump().Single();
                h.Respond(fromB);
                Assert.Equal("5", b.JsonRpc().Single()["id"].ToString());
                Assert.Equal(2, a.JsonRpc().Count);
            }
        }

        // ---- Child exit (Requirement 8.4, design: child exit outcomes) ----

        [Theory]
        [InlineData("object", "Read")]
        [InlineData("object", "Create")]
        public void Child_Exit_Fails_Delivered_Requests_And_Keeps_Queued_Ones_For_The_Replacement(string method, string action)
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Request(1, method, action));
                h.Pump();
                h.Send(b, Sdk(2));
                var oldChild = h.Child;

                h.ExitChild(oldChild);

                bool expectedRetryable = WorkerCommandCatalog.Classify(method, action) == OperationClass.Read;
                var restarted = a.JsonRpc().Single();
                AssertRestarted(restarted, 1, ConcurrencyDispatchState.DeliveredUnanswered, expectedRetryable);
                Assert.Empty(b.JsonRpc());

                Assert.Equal(2, h.Launcher.Started.Count);
                Assert.NotSame(oldChild, h.Child);
                h.Ready();
                var replayed = h.Pump().Single();
                Assert.Equal("2", ClientIdOf(replayed));
                Assert.Equal("b", OwnerOf(replayed, a, b));
                Assert.DoesNotContain(oldChild.Received, line => ClientIdOf(line) == "2");
            }
        }

        [Fact]
        public void Child_Exit_Without_A_Replacement_Fails_Queued_Requests_As_Not_Delivered()
        {
            using (var h = new Harness())
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(1));
                h.Pump();
                h.Send(b, Sdk(2));
                h.Send(b, Sdk(3));

                h.Launcher.FailNextStarts = true;
                h.ExitChild(h.Child);

                AssertRestarted(a.JsonRpc().Single(), 1, ConcurrencyDispatchState.DeliveredUnanswered,
                    WorkerCommandCatalog.Classify("object", "Read") == OperationClass.Read);
                var toB = b.JsonRpc();
                Assert.Equal(2, toB.Count);
                AssertRestarted(toB[0], 2, ConcurrencyDispatchState.NotDelivered, true);
                AssertRestarted(toB[1], 3, ConcurrencyDispatchState.NotDelivered, true);
            }
        }

        [Fact]
        public void Every_Child_Is_Started_With_Busy_Reject_Disabled()
        {
            using (var h = new Harness())
            {
                h.Attach();
                h.ExitChild(h.Child);

                Assert.Equal(2, h.Launcher.Started.Count);
                foreach (var start in h.Launcher.StartInfos)
                {
                    Assert.Equal("0", start.EnvironmentVariables["GXMCP_BUSY_REJECT_MS"]);
                    Assert.Equal("1", start.EnvironmentVariables["GXMCP_SHARED_CHILD"]);
                }
            }
        }

        // ---- Registry record (design: record written on state transitions) ----

        [Fact]
        public void The_Registry_Record_Is_Written_Only_On_State_Change()
        {
            using (var h = new Harness(ready: false))
            {
                Assert.NotEmpty(h.Registry.States);
                Assert.Equal("starting", h.Registry.States.Last());
                h.Ready();
                Assert.Equal("ready", h.Registry.States.Last());
                int afterReady = h.Registry.States.Count;

                var a = h.Attach();
                var b = h.Attach();
                for (int i = 1; i <= 10; i++)
                {
                    h.Send(i % 2 == 0 ? a : b, Sdk(i));
                    h.Send(a, HostFrame(h, "status", a, new JObject { ["requestId"] = "s" + i }));
                }
                for (int i = 0; i < 10; i++)
                {
                    var released = h.Pump();
                    if (released.Count == 0) break;
                    h.Respond(released.Single());
                }
                // Traffic is not a state change; the idle debounce allows one write.
                Assert.InRange(h.Registry.States.Count - afterReady, 0, 1);

                // A minute of idle time: a 250 ms polling loop would have written 240 times.
                int beforeIdle = h.Registry.States.Count;
                for (int i = 0; i < 240; i++) h.Clock.Advance(250);
                Assert.InRange(h.Registry.States.Count - beforeIdle, 0, 1);

                int beforeExit = h.Registry.States.Count;
                h.ExitChild(h.Child);
                Assert.True(h.Registry.States.Count > beforeExit, "a child exit is a state change and must be recorded");
            }
        }

        // ---- status frame (protocol v2) ----

        [Fact]
        public void A_Status_Frame_Is_Answered_Without_Entering_The_Queue()
        {
            using (var h = new Harness(queueDepth: 1, sessionQueueCap: 1))
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Request(1, "object", "Read"));
                var running = h.Pump().Single();
                h.Send(a, Sdk(2));

                // The queue is full, and the status frame still gets an answer.
                h.Send(b, HostFrame(h, "status", b, new JObject { ["requestId"] = "status-1" }));

                var ack = b.HostFrames().Single(f => (string)f["type"] == "status_ack");
                Assert.Equal("status-1", (string)ack["requestId"]);
                Assert.Equal("object/read", ((string)ack["running"]["operation"]).ToLowerInvariant());
                Assert.Single((JArray)ack["queue"]);
                Assert.DoesNotContain(b.HostFrames(), f => (string)f["type"] == "host_error");
                Assert.Empty(b.JsonRpc());
                Assert.Empty(h.Pump());

                // The queue is unchanged: request 2 is still next.
                h.Respond(running);
                Assert.Equal("2", ClientIdOf(h.Pump().Single()));
            }
        }

        // ---- No polling loops (maintainer request on #854) ----

        [Fact]
        public void The_Broker_Has_No_Sleep_Polling_Loops()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "SharedWorkerHost.cs");

            Assert.DoesNotContain("Thread.Sleep(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ClearChildQueue", source, StringComparison.Ordinal);
            Assert.Contains("GetConsumingEnumerable", source, StringComparison.Ordinal);
            Assert.Contains("SharedHostScheduler", source, StringComparison.Ordinal);
        }

        // ---- Assertions ----

        private static JObject AssertBusy(JObject frame, int expectedId, string reason, int queueWaitMs)
        {
            Assert.Equal(expectedId.ToString(), frame["id"].ToString());
            Assert.Null(frame["error"]);
            var result = Assert.IsType<JObject>(frame["result"]);
            Assert.Equal("error", (string)result["status"]);
            Assert.Equal(ConcurrencyErrorCatalog.WorkerBusy, (string)result["code"]);
            var error = Assert.IsType<JObject>(result["error"]);
            Assert.Equal(ConcurrencyErrorCatalog.WorkerBusy, (string)error["code"]);
            Assert.Equal(reason, (string)error["reason"]);
            Assert.True((bool)error["retryable"]);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string)error["dispatchState"]);
            Assert.InRange((long)error["retryAfterMs"], 1000, queueWaitMs);
            Assert.False(string.IsNullOrEmpty((string)error["runningOperation"]));
            Assert.NotNull(error["runningElapsedMs"]);
            return error;
        }

        private static void AssertRestarted(JObject frame, int expectedId, string dispatchState, bool retryable)
        {
            Assert.Equal(expectedId.ToString(), frame["id"].ToString());
            Assert.Null(frame["result"]);
            var error = Assert.IsType<JObject>(frame["error"]);
            Assert.Equal(-32050, (int)error["code"]);
            var data = Assert.IsType<JObject>(error["data"]);
            Assert.Equal(ConcurrencyErrorCatalog.SharedWorkerRestarted, (string)data["code"]);
            Assert.Equal(dispatchState, (string)data["dispatchState"]);
            Assert.Equal(retryable, (bool)data["retryable"]);
        }

        // ---- Frame builders ----

        private static JObject Sdk(int id, string progressToken = null) => Request(id, "object", "Read", null, progressToken);

        private static JObject Request(int id, string method, string action, JObject parameters = null, string progressToken = null)
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            };
            if (action != null) request["action"] = action;
            if (progressToken != null) request["_meta"] = new JObject { ["progressToken"] = progressToken };
            return request;
        }

        private static JObject CancelNotification(int requestId) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JObject { ["requestId"] = requestId, ["reason"] = "test" }
        };

        private static JObject HostFrame(Harness h, string type, FakeAttachment attachment, JObject extra = null)
        {
            var frame = extra ?? new JObject();
            frame["type"] = type;
            frame["gxmcp"] = type;
            frame["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion;
            frame["version"] = SharedWorkerHostProtocol.ProtocolVersion;
            frame["identityKey"] = h.Options.IdentityKey;
            frame["key"] = h.Options.IdentityKey;
            frame["attachId"] = attachment.AttachmentId;
            frame["attachmentId"] = attachment.AttachmentId;
            return frame;
        }

        // The client id of a line the child received: the broker rewrites the id, so the
        // test tags each request with the id it sent, in params, and reads it back here.
        private static string ClientIdOf(JObject childLine) => (string)childLine["params"]?["testClientId"];

        private static string OwnerOf(JObject childLine, FakeAttachment a, FakeAttachment b)
        {
            string owner = (string)childLine["_meta"]?["attachmentId"];
            if (owner == a.AttachmentId) return "a";
            if (owner == b.AttachmentId) return "b";
            return "?";
        }

        private static int BusyWaitOf(JObject childLine) => (int)childLine["_meta"]["busyWaitMs"];

        // ---- Harness and fakes ----

        private sealed class Harness : IDisposable
        {
            internal Harness(
                int queueDepth = 8,
                int sessionQueueCap = 4,
                int queueWaitMs = DefaultQueueWaitMs,
                bool ready = true)
            {
                Options = SharedWorkerHostOptions.Parse(new[]
                {
                    "--shared-host",
                    "--worker-executable", @"C:\GeneXus\GxMcp.Worker.exe",
                    "--kb", @"C:\KBs\KbA",
                    "--installation", @"C:\GeneXus",
                    "--driver", "native",
                    "--major", "18"
                });
                var settings = new SharedHostSettingsResult(
                    queueWaitMs, queueDepth, sessionQueueCap, 4, 1024, 1, 120,
                    new Dictionary<string, string>(), new List<string>());
                Runtime = new SharedWorkerHostRuntime(Options, settings, Clock, Launcher, Registry, runBackgroundThreads: false);
                Runtime.Start();
                if (ready) Ready();
            }

            internal SharedWorkerHostOptions Options { get; }
            internal FakeClock Clock { get; } = new FakeClock();
            internal FakeChildLauncher Launcher { get; } = new FakeChildLauncher();
            internal FakeRegistryWriter Registry { get; } = new FakeRegistryWriter();
            internal SharedWorkerHostRuntime Runtime { get; }
            internal FakeChild Child => Launcher.Started.Last();

            internal FakeAttachment Attach()
            {
                var attachment = new FakeAttachment(Runtime);
                string frame = new JObject
                {
                    ["type"] = "attach",
                    ["gxmcp"] = "attach",
                    ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["version"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["identityKey"] = Options.IdentityKey,
                    ["key"] = Options.IdentityKey,
                    ["gatewayPid"] = 4242,
                    ["gatewayStartTimeUtcTicks"] = 638000000000000000L,
                    ["attachNonce"] = Guid.NewGuid().ToString("N")
                }.ToString(Formatting.None);
                Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame, out SharedWorkerEnvelope envelope, out string parseError), parseError);
                Assert.True(Runtime.Attach(attachment, envelope, out string error), error);
                Assert.False(string.IsNullOrEmpty(attachment.AttachmentId));
                return attachment;
            }

            internal void Send(FakeAttachment attachment, JObject frame)
            {
                if (frame["id"] != null && frame["method"] != null && frame["params"] is JObject parameters)
                    parameters["testClientId"] = frame["id"].ToString();
                Runtime.Receive(attachment, frame.ToString(Formatting.None));
            }

            // Lines written to the current child by this pump, parsed.
            internal List<JObject> Pump()
            {
                var child = Child;
                int before = child.Received.Count;
                Runtime.PumpChildInbox();
                return child.Received.Skip(before).ToList();
            }

            internal void Ready() => Runtime.OnChildLine(Child, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/worker/sdk_ready"
            }.ToString(Formatting.None));

            internal void Respond(JObject childRequest) => Runtime.OnChildLine(Child, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = childRequest["id"].DeepClone(),
                ["result"] = new JObject { ["status"] = "ok" }
            }.ToString(Formatting.None));

            internal void Progress(JObject childRequest) => Runtime.OnChildLine(Child, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new JObject
                {
                    ["progressToken"] = childRequest["_meta"]["progressToken"].DeepClone(),
                    ["progress"] = 1
                }
            }.ToString(Formatting.None));

            internal void ExitChild(FakeChild child)
            {
                child.Exit(1);
                Runtime.OnChildExited(child);
            }

            public void Dispose() => Runtime.Dispose();
        }

        // Monotonic clock under test control. Advance moves the time and then runs, once each,
        // the callbacks of the timers due at or before the new time, earliest first, on the
        // test thread. A timer a callback re-arms at or before the current time runs on the
        // next Advance, so a self-re-arming timer cannot spin a test.
        private sealed class FakeClock : ISharedHostClock
        {
            private readonly List<FakeTimer> _timers = new List<FakeTimer>();

            public long NowMs { get; private set; } = 1000;

            public ISharedHostTimer CreateTimer(Action callback)
            {
                var timer = new FakeTimer(callback);
                _timers.Add(timer);
                return timer;
            }

            internal void Advance(long ms)
            {
                NowMs += ms;
                var due = _timers
                    .Where(t => !t.Disposed && t.DueAtMs.HasValue && t.DueAtMs.Value <= NowMs)
                    .OrderBy(t => t.DueAtMs.Value)
                    .ToList();
                foreach (var timer in due)
                {
                    if (timer.Disposed || !timer.DueAtMs.HasValue || timer.DueAtMs.Value > NowMs) continue;
                    timer.DueAtMs = null;
                    timer.Callback();
                }
            }
        }

        private sealed class FakeTimer : ISharedHostTimer
        {
            internal FakeTimer(Action callback) { Callback = callback; }
            internal Action Callback { get; }
            internal long? DueAtMs { get; set; }
            internal bool Disposed { get; private set; }
            public void Change(long? dueAtMs) { DueAtMs = dueAtMs; }
            public void Dispose() { Disposed = true; DueAtMs = null; }
        }

        private sealed class FakeChildLauncher : ISharedHostChildLauncher
        {
            private int _nextPid = 50000;

            internal List<FakeChild> Started { get; } = new List<FakeChild>();
            internal List<ProcessStartInfo> StartInfos { get; } = new List<ProcessStartInfo>();
            internal bool FailNextStarts { get; set; }

            public ISharedHostChild Start(ProcessStartInfo startInfo)
            {
                StartInfos.Add(startInfo);
                if (FailNextStarts) throw new InvalidOperationException("Could not start shared Worker child.");
                var child = new FakeChild(++_nextPid);
                Started.Add(child);
                return child;
            }
        }

        private sealed class FakeChild : ISharedHostChild
        {
            private readonly ManualResetEvent _exited = new ManualResetEvent(false);

            internal FakeChild(int pid)
            {
                Pid = pid;
                StartTimeUtcTicks = DateTime.UtcNow.Ticks;
            }

            internal List<JObject> Received { get; } = new List<JObject>();

            public int Pid { get; }
            public long StartTimeUtcTicks { get; }
            public bool HasExited { get; private set; }
            public int ExitCode { get; private set; } = -1;
            public long WorkingSetBytes => HasExited ? 0 : 64L * 1024 * 1024;
            public long PrivateBytes => HasExited ? 0 : 48L * 1024 * 1024;
            public WaitHandle ExitedHandle => _exited;
            public TextReader StandardOutput => TextReader.Null;
            public TextReader StandardError => TextReader.Null;

            public void WriteLine(string line)
            {
                if (HasExited) throw new IOException("The child has exited.");
                Received.Add(JObject.Parse(line));
            }

            public void Kill() => Exit(-1);

            internal void Exit(int code)
            {
                HasExited = true;
                ExitCode = code;
                _exited.Set();
            }

            public void Dispose() { }
        }

        private sealed class FakeRegistryWriter : ISharedHostRegistryWriter
        {
            internal List<string> States { get; } = new List<string>();
            internal int Deletes { get; private set; }

            public void Write(SharedWorkerHostRegistryRecord record) => States.Add(record.State);
            public void Delete() => Deletes++;
        }

        // In-memory Attachment. Stop mirrors the pipe transport: closing the pipe ends
        // RunAsync, whose finally block detaches the Attachment from the runtime.
        private sealed class FakeAttachment : ISharedHostAttachment
        {
            private readonly SharedWorkerHostRuntime _runtime;
            private long _sequence;
            private int _stopped;

            internal FakeAttachment(SharedWorkerHostRuntime runtime)
            {
                _runtime = runtime;
                ConnectionId = Guid.NewGuid().ToString("N");
            }

            internal List<JObject> Lines { get; } = new List<JObject>();

            public string ConnectionId { get; }
            public string AttachmentId { get; set; } = string.Empty;

            public long NextSequence() => Interlocked.Increment(ref _sequence);

            public bool Enqueue(string line)
            {
                if (Volatile.Read(ref _stopped) != 0) return false;
                Lines.Add(JObject.Parse(line));
                return true;
            }

            public void Stop()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                _runtime.Detach(this);
            }

            // JSON-RPC frames delivered so far (responses, errors, notifications).
            internal List<JObject> JsonRpc() => Lines.Where(l => l["jsonrpc"] != null).ToList();

            internal List<JObject> HostFrames() => Lines.Where(l => l["jsonrpc"] == null).ToList();
        }
    }
}
