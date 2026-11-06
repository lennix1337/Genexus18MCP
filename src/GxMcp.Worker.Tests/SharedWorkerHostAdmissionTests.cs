// Host admission tests for the shared-host Worker_Host (RFC #854, task 5.2; Requirements 1.7, 5.1,
// 5.3, 5.4, 5.5, 5.6, 5.7, 5.9, 6.1 to 6.6, 6.10, 6.12 and 6.13; design "SharedHostAdmissionPolicy.cs",
// "SharedWorkerHostAdmission.cs (new)", child state machine, heap recycle, idle reaping, decision D8).
//
// These tests are written before the implementation (tasks 5.5 and 5.6). They fix the seam contract
// below; tasks 5.5 and 5.6 implement exactly these types, members and names. Every seam is internal
// (GxMcp.Worker grants InternalsVisibleTo("GxMcp.Worker.Tests")). The pure decision is the existing
// SharedHostAdmissionPolicy (src/GxMcp.Shared/SharedHost); this file tests the I/O wrapper and the
// runtime integration around it. The seams of task 3.5 (SharedWorkerHostSchedulerWiringTests.cs) and
// task 4.8 (SharedWorkerHostOwnershipTests.cs) are reused unchanged; the fakes below are private to
// this file on purpose.
//
// ---- New seams (namespace GxMcp.Worker, file SharedWorkerHostSeams.cs) ----
//
//   internal interface ISharedHostMemoryReader
//       bool TryReadAvailableMb(out long availableMb, out string error);
//     One call is one Available_Physical_Memory reading in MB. False with a non-empty error when the
//     reading fails. Production: internal sealed class SharedHostMachineMemoryReader, a wrapper of
//     MachineMemory.TryGetAvailablePhysicalMb.
//
//   internal interface ISharedHostRegistryEnumerator
//       IReadOnlyList<RecordReadResult> ReadAll();
//     Every registry v2 record file, each read with RecordReadResult.ReadFile, in any order. The
//     host's own record may be among them. Production: internal sealed class
//     SharedHostFileRegistryEnumerator(string directory) over "*.json" in the directory of
//     SharedWorkerHostRuntime.ResolveRecordPathV2(options).
//
//   internal interface ISharedHostSignalHub
//       void Signal(string name);
//       IDisposable Subscribe(string name, Action onSignal);
//     Named auto-reset events (Local\...). Signal sets the event and never blocks. Subscribe runs
//     onSignal once per signal until the returned handle is disposed. Production: internal sealed
//     class SharedHostNamedEventHub (EventWaitHandle plus ThreadPool.RegisterWaitForSingleObject).
//     The fake below runs callbacks synchronously on the signalling thread, so the runtime must not
//     assume another thread.
//
//   internal sealed class SharedHostAdmissionSeams
//       internal ISharedHostRegistryEnumerator Registry { get; set; }
//       internal IProcessProbe HostProbe { get; set; }        // liveness of other hosts' records
//       internal ISharedHostMemoryReader Memory { get; set; }
//       internal ISharedHostSignalHub Signals { get; set; }
//       internal Action<string> Warn { get; set; }            // one call = one warning log entry
//       internal static SharedHostAdmissionSeams Production(SharedWorkerHostOptions options);
//       internal static SharedHostAdmissionSeams Unrestricted { get; }
//     Unrestricted = empty registry, a reader that always returns int.MaxValue MB, a hub that never
//     signals, no-op Warn. The six- and eight-argument runtime constructors use Unrestricted, so the
//     PR A and task 4.8 tests keep their behavior; the one-argument constructor uses Production.
//
// ---- New file src/GxMcp.Worker/SharedWorkerHostAdmission.cs (task 5.5) ----
//
//   internal enum SharedHostAdmissionOutcome { Start, Hold, Evict, Refuse, Contended }
//
//   internal sealed class SharedHostAdmissionResult
//       internal SharedHostAdmissionOutcome Outcome { get; }
//       internal AdmissionDecision Decision { get; }   // null for Contended
//       internal long? AvailableMb { get; }            // null when unread or unreadable
//
//   internal sealed class SharedWorkerHostAdmission
//       internal const string AdmissionMutexName = @"Local\GxMcp.SharedHost.v2.Admission";
//       internal const string WakeEventPrefix   = @"Local\GxMcp.SharedHost.v2.Wake.";
//       internal const string EvictEventPrefix  = @"Local\GxMcp.SharedHost.v2.Evict.";
//       internal const int HeldPollMs = 1000;     // held or evicting host re-runs admission
//       internal const int AdmissionRetryMs = 50;  // retry after a Contended attempt
//       internal static string WakeEventName(string kbKey)  => WakeEventPrefix + kbKey;
//       internal static string EvictEventName(string kbKey) => EvictEventPrefix + kbKey;
//
//       internal SharedWorkerHostAdmission(
//           SharedHostSettingsResult settings,
//           ISharedHostClock clock,
//           ISharedHostMutexFactory mutexFactory,
//           ISharedHostRecordWriter recordWriter,
//           SharedHostAdmissionSeams seams);
//
//       internal SharedHostAdmissionResult Admit(SharedHostRecord self, bool maxOpenKbsExceeded = false);
//         (1) mutexFactory.TryAcquire(AdmissionMutexName); on null return Contended with no
//             enumeration, no memory reading and no record write.
//         (2) Registry.ReadAll(); classify each read with SharedHostRecordClassifier.Classify(read,
//             HostProbe); drop the entry whose kbKey equals self.KbKey and add self as Live; map each
//             record to AdmissionHostView (Key=kbKey, State, WorkerPid, Evictable, Attachments,
//             LastActivityUtc, HeldSinceUtc, Liveness).
//         (3) Memory.TryReadAvailableMb last: after every enumeration and probe of this attempt and
//             before the decision. A failed read calls Warn once with the reader's error text and
//             passes AvailableMb = null. A reading is never reused by a later attempt.
//         (4) SharedHostAdmissionPolicy.Decide with settings.MaxWorkers, ColdStartConcurrency and
//             MemoryFloorMb.
//         (5) Update and write self before releasing the mutex: Start -> State "starting",
//             ColdStartStartedUtc set, ColdStartConcurrency = settings value, HeldSinceUtc null;
//             Hold -> State "held", HeldSinceUtc kept from the first hold of this start; Refuse ->
//             State "refused" with Refusal {code, reason, availableMb, floorMb}; Evict -> State
//             "admitting" and Signal(EvictEventName(victimKey)).
//         (6) Release the mutex.
//
//       internal void EndColdStart(SharedHostRecord self);
//         Called after the host's Cold_Start ended (child ready, start failed, or child exited before
//         ready) with self already in its new state. Under the admission mutex: write self, then
//         Signal(WakeEventName(head)) where head is the live "held" record other than self with the
//         oldest heldSinceUtc (ordinal kbKey as tie-break); no signal when there is none.
//
// ---- Runtime integration (src/GxMcp.Worker/SharedWorkerHost.cs, task 5.6) ----
//
//   internal SharedWorkerHostRuntime(
//       SharedWorkerHostOptions options,
//       SharedHostSettingsResult settings,
//       ISharedHostClock clock,
//       ISharedHostChildLauncher childLauncher,
//       ISharedHostRecordWriter recordWriter,
//       ISharedHostMutexFactory mutexFactory,
//       IProcessProbe gatewayProbe,
//       SharedHostAdmissionSeams admissionSeams,
//       bool runBackgroundThreads)
//     The eight-argument constructor delegates here with SharedHostAdmissionSeams.Unrestricted.
//
//   SharedWorkerHostOptions.HeapRecycleMb   // --heap-recycle-mb, default 1500, 0 disables
//
//   Start: after the "admitting" write, Admit(self). Start -> start the child at once (the record
//   then carries the child PID). Hold -> no child; Subscribe(WakeEventName(own kbKey)) re-runs
//   admission on each signal; a HeldPollMs timer re-runs it too, which detects a crashed Cold_Start
//   owner. Evict -> no child; re-run admission on the HeldPollMs timer. Contended -> re-run after
//   AdmissionRetryMs. Refuse -> no child; the host keeps its Attachments in NoWorker.
//   Every start of a child, also a heap-recycle replacement, takes its own Admit.
//
//   Waiting requests: SDK_Operations queued while no child is ready. On Refuse each one is answered
//   with a tool-result envelope (result.status "error", result.code = code, result.error =
//   ConcurrencyErrorCatalog.Build(code, reason, ...) with the decision Details merged at the top
//   level of result.error: availableMb and floorMb for low-memory, maxWorkers and openKbs[] {key,
//   sessions, lastActivityUtc} for worker-limit). Attachments get an "admission" host frame with
//   state ("held", "starting", "refused", "evicting"), position (held), elapsedMs and refusal. An
//   Attachment that attaches while the start is held gets an "admission" frame with state "held".
//   After a refusal, a failed replacement, an expired hold or a dropped hold, the next SDK_Operation
//   runs Admit again.
//
//   Held start expiry: a start held for the attach readiness wait (AttachReadyTimeoutSeconds) is
//   dropped: no child, record state "no-worker", and each waiting request gets
//   WORKER_STARTUP_FAILED, reason "cold-start-wait-expired", retryAfterMs in [1000, wait ms].
//   Held start drop: when a cancellation or a detach removes the last waiting request of a held
//   start (Attachments alone do not count as waiters), the hold is dropped: record state
//   "no-worker", HeldSinceUtc null, and a later wake signal starts nothing.
//   Cold_Start end: on sdk_ready, on a failed child start, or on a child exit before sdk_ready the
//   host calls EndColdStart.
//   Eviction victim: Subscribe(EvictEventName(own kbKey)); on a signal the host re-checks its own
//   evictability under its lock (no in-flight, no queued request, no background activity). Evictable
//   -> Kill the child, record workerPid 0 and state "no-worker", Attachments stay connected. Busy ->
//   nothing changes.
//   Heap recycle (supervisor tick, 5000 ms): scheduler idle and child WorkingSetBytes above
//   HeapRecycleMb -> Kill the old child, then Admit, then start the replacement. Attachments keep
//   their attachId. A replacement whose launch throws answers each queued request with
//   WORKER_STARTUP_FAILED, reason "replacement-failed", and the next SDK_Operation starts again.
//   Idle reaping: measured from the later of the last detach and the last request completion.
//   detach with stopIfLast=true from the last Attachment kills the child at once and sets
//   IsStopRequested.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SharedWorkerHostAdmissionTests
    {
        private const string AdmissionMutex = @"Local\GxMcp.SharedHost.v2.Admission";
        private const string WakePrefix = @"Local\GxMcp.SharedHost.v2.Wake.";
        private const string EvictPrefix = @"Local\GxMcp.SharedHost.v2.Evict.";
        private const string KbA = @"C:\KBs\KbA";
        private const string KbB = @"C:\KBs\KbB";
        private const string KbC = @"C:\KBs\KbC";
        private const int TickMs = 5000;
        private const int LongQueueWaitMs = 600000;
        private const long OtherHostStartTicks = 638100000000000000L;

        private static readonly DateTime OldActivity = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime NewActivity = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);

        // ---- Names (design "SharedWorkerHostAdmission.cs") ----

        [Fact]
        public void Admission_Names_Match_The_Design()
        {
            string key = SharedHostKbKey.Compute(KbA);
            Assert.Equal(AdmissionMutex, SharedWorkerHostAdmission.AdmissionMutexName);
            Assert.Equal(WakePrefix + key, SharedWorkerHostAdmission.WakeEventName(key));
            Assert.Equal(EvictPrefix + key, SharedWorkerHostAdmission.EvictEventName(key));
            Assert.Equal(1000, SharedWorkerHostAdmission.HeldPollMs);
        }

        // ---- Memory reading (Requirement 6 criteria 1 and 2) ----

        [Fact]
        public void Memory_Is_Read_Last_Under_The_Admission_Mutex_Within_1000_Ms_Before_The_Child_Is_Created()
        {
            using (var h = new Harness())
            {
                h.AddOther(KbB, SharedHostRecordState.Ready, workerPid: 7001, hostPid: 7101);

                Assert.True(h.Runtime.Start());

                var child = Assert.Single(h.Launcher.Started);
                var read = Assert.Single(h.Memory.Reads);
                Assert.True(read.AdmissionMutexHeld, "memory must be read under the admission mutex");
                Assert.InRange(child.StartedAtMs - read.AtMs, 0, 1000);

                int memory = h.Log.IndexOf("memory");
                int acquired = h.Log.IndexOf("mutex+:" + AdmissionMutex);
                int released = h.Log.IndexOf("mutex-:" + AdmissionMutex);
                Assert.InRange(acquired, 0, memory - 1);
                Assert.True(h.Log.LastIndexOf("enumerate", memory) >= 0, "the registry must be enumerated before the memory reading");
                Assert.DoesNotContain(h.Log.Skip(memory + 1).TakeWhile(e => e != "mutex-:" + AdmissionMutex), e => e == "enumerate" || e.StartsWith("probe:", StringComparison.Ordinal));
                Assert.InRange(h.Log.IndexOf("probe:7101"), 0, memory - 1);
                Assert.InRange(h.Log.IndexOf("write:" + SharedHostRecordState.Starting, memory), memory + 1, released - 1);
                Assert.True(h.Log.IndexOf("launch:" + child.Pid) > memory);
            }
        }

        [Fact]
        public void Every_Start_Takes_A_Fresh_Memory_Reading_And_A_Refusal_Restarts_On_The_Next_Request()
        {
            using (var h = new Harness())
            {
                h.Memory.NextMb = 900;
                Assert.True(h.Runtime.Start());
                Assert.Empty(h.Launcher.Started);
                Assert.Equal(SharedHostRecordState.Refused, h.Records.LastState);
                Assert.Single(h.Memory.Reads);

                var a = h.Attach();
                h.Memory.NextMb = 4096;
                h.Send(a, Sdk(1));

                Assert.Equal(2, h.Memory.Reads.Count);
                var child = Assert.Single(h.Launcher.Started);
                Assert.InRange(child.StartedAtMs - h.Memory.Reads[1].AtMs, 0, 1000);
                h.Ready();
                Assert.Equal("1", ClientIdOf(Assert.Single(h.Pump())));
            }
        }

        [Fact]
        public void Low_Memory_Refuses_Every_Waiting_Request_Without_Taking_A_Cold_Start_Slot()
        {
            using (var h = new Harness())
            {
                var other = h.AddOther(KbB, SharedHostRecordState.Starting, workerPid: 7001, hostPid: 7101);
                Assert.True(h.Runtime.Start());
                Assert.Equal(SharedHostRecordState.Held, h.Records.LastState);

                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(1));
                h.Send(b, Sdk(2));

                h.Memory.NextMb = 900;
                other.State = SharedHostRecordState.Ready;
                h.Signals.Signal(WakePrefix + h.KbKey);

                Assert.Empty(h.Launcher.Started);
                Assert.DoesNotContain(SharedHostRecordState.Starting, h.Records.States);
                Assert.DoesNotContain(h.Log, e => e.StartsWith("signal:" + EvictPrefix, StringComparison.Ordinal));
                Assert.DoesNotContain(h.Log, e => e.StartsWith("kill:", StringComparison.Ordinal));
                Assert.Equal(7001, other.WorkerPid);

                var last = h.Records.Last;
                Assert.Equal(SharedHostRecordState.Refused, (string)last["state"]);
                Assert.Equal(ConcurrencyErrorCatalog.KbPoolFull, (string)last["refusal"]["code"]);
                Assert.Equal("low-memory", (string)last["refusal"]["reason"]);
                Assert.Equal(900L, (long)last["refusal"]["availableMb"]);
                Assert.Equal(1024L, (long)last["refusal"]["floorMb"]);

                foreach (var (attachment, id) in new[] { (a, 1), (b, 2) })
                {
                    var error = AssertToolError(attachment, id, ConcurrencyErrorCatalog.KbPoolFull, "low-memory", retryable: false);
                    Assert.Equal(900L, (long)error["availableMb"]);
                    Assert.Equal(1024L, (long)error["floorMb"]);
                    Assert.Null(error["retryAfterMs"]);
                    Assert.Contains("Close open KBs", (string)error["hint"], StringComparison.Ordinal);
                    Assert.Contains(attachment.HostFrames(), f => (string)f["type"] == "admission" && (string)f["state"] == "refused");
                }
            }
        }

        [Fact]
        public void Unreadable_Memory_Passes_The_Floor_For_That_Start_Only_With_One_Warning_Per_Failed_Read()
        {
            using (var h = new Harness(extraArgs: new[] { "--heap-recycle-mb", "100" }))
            {
                h.Memory.Failure = "test memory read failure";
                h.Launcher.NextWorkingSetMb = 200;

                Assert.True(h.Runtime.Start());
                Assert.Single(h.Launcher.Started);
                Assert.Single(h.Warnings);
                Assert.Contains("test memory read failure", h.Warnings[0], StringComparison.Ordinal);
                h.Ready();

                // Heap recycle forces a second start; its reading fails again and passes again.
                h.Clock.Advance(TickMs);
                Assert.Equal(2, h.Launcher.Started.Count);
                Assert.Equal(2, h.Warnings.Count);
                h.Ready();

                // The pass applied to those starts only: a readable value below the floor refuses.
                h.Memory.Failure = null;
                h.Memory.NextMb = 900;
                h.Clock.Advance(TickMs);
                Assert.Equal(2, h.Launcher.Started.Count);
                Assert.Equal(2, h.Warnings.Count);
                Assert.Equal(SharedHostRecordState.Refused, h.Records.LastState);
            }
        }

        [Fact]
        public void Unreadable_Memory_Still_Enforces_The_Worker_Limit()
        {
            using (var h = new Harness(maxWorkers: 1))
            {
                h.AddOther(KbB, SharedHostRecordState.Ready, workerPid: 7001, hostPid: 7101, evictable: false, attachments: 1);
                h.Memory.Failure = "test memory read failure";

                Assert.True(h.Runtime.Start());

                Assert.Empty(h.Launcher.Started);
                Assert.Equal(SharedHostRecordState.Refused, h.Records.LastState);
                Assert.Equal("worker-limit", (string)h.Records.Last["refusal"]["reason"]);
            }
        }

        // ---- Cold-start concurrency, hold and FIFO release (Requirement 6 criteria 3, 4 and 5) ----

        [Fact]
        public void A_Start_At_Cold_Start_Concurrency_Is_Held_And_Later_Requests_Join_The_One_Held_Start()
        {
            using (var h = new Harness())
            {
                var other = h.AddOther(KbB, SharedHostRecordState.Starting, workerPid: 7001, hostPid: 7101);

                Assert.True(h.Runtime.Start());
                Assert.Empty(h.Launcher.Started);
                var held = h.Records.Last;
                Assert.Equal(SharedHostRecordState.Held, (string)held["state"]);
                Assert.Equal(JTokenType.Date, held["heldSinceUtc"].Type);
                Assert.Equal(0, (int)held["workerPid"]);
                Assert.True(h.Signals.IsSubscribed(WakePrefix + h.KbKey), "a held host must wait on its wake event");

                var a = h.Attach();
                var b = h.Attach();
                Assert.Contains(a.HostFrames(), f => (string)f["type"] == "admission" && (string)f["state"] == "held" && (int)f["position"] == 1);
                h.Send(a, Sdk(1));
                h.Send(b, Sdk(2));
                h.Send(a, Sdk(3));
                Assert.Empty(h.Launcher.Started);
                Assert.Empty(a.JsonRpc());
                Assert.Empty(b.JsonRpc());

                int readsBeforeRelease = h.Memory.Reads.Count;
                long releasedAtMs = h.Clock.NowMs;
                other.State = SharedHostRecordState.Ready;
                h.Signals.Signal(WakePrefix + h.KbKey);

                // One start for every waiting request, within 500 ms, with every check run again.
                var child = Assert.Single(h.Launcher.Started);
                Assert.InRange(child.StartedAtMs - releasedAtMs, 0, 500);
                Assert.Equal(readsBeforeRelease + 1, h.Memory.Reads.Count);
                Assert.Equal(SharedHostRecordState.Starting, h.Records.LastState);

                h.Ready();
                Assert.Equal("1", ClientIdOf(Assert.Single(h.Pump())));
            }
        }

        [Theory]
        [InlineData("ready")]
        [InlineData("exited")]
        public void A_Host_Whose_Cold_Start_Ends_Wakes_The_Oldest_Held_Start(string end)
        {
            using (var h = new Harness())
            {
                Assert.True(h.Runtime.Start());
                var first = Assert.Single(h.Launcher.Started);

                h.AddOther(KbC, SharedHostRecordState.Held, workerPid: 0, hostPid: 7102, heldSinceUtc: NewActivity);
                h.AddOther(KbB, SharedHostRecordState.Held, workerPid: 0, hostPid: 7101, heldSinceUtc: OldActivity);

                if (end == "ready") h.Ready();
                else h.ExitChild(first);

                string head = WakePrefix + SharedHostKbKey.Compute(KbB);
                var signal = Assert.Single(h.Signals.Sent, s => s.Name == head);
                Assert.True(signal.AdmissionMutexHeld, "the wake signal is sent under the admission mutex");
                Assert.DoesNotContain(h.Signals.Sent, s => s.Name == WakePrefix + SharedHostKbKey.Compute(KbC));
            }
        }

        [Fact]
        public void A_Released_Start_Reapplies_The_Worker_Limit_And_Refuses_With_Per_Kb_Details()
        {
            using (var h = new Harness(maxWorkers: 1))
            {
                var other = h.AddOther(KbB, SharedHostRecordState.Starting, workerPid: 7001, hostPid: 7101, evictable: false, attachments: 2, lastActivityUtc: OldActivity);
                Assert.True(h.Runtime.Start());
                Assert.Equal(SharedHostRecordState.Held, h.Records.LastState);

                var a = h.Attach();
                h.Send(a, Sdk(1));

                other.State = SharedHostRecordState.Ready;
                h.Signals.Signal(WakePrefix + h.KbKey);

                Assert.Empty(h.Launcher.Started);
                Assert.DoesNotContain(h.Log, e => e.StartsWith("signal:" + EvictPrefix, StringComparison.Ordinal));
                Assert.Equal(SharedHostRecordState.Refused, h.Records.LastState);

                var error = AssertToolError(a, 1, ConcurrencyErrorCatalog.KbPoolFull, "worker-limit", retryable: false);
                Assert.Equal(1, (int)error["maxWorkers"]);
                var open = Assert.Single((JArray)error["openKbs"]);
                Assert.Equal(SharedHostKbKey.Compute(KbB), (string)open["key"]);
                Assert.Equal(2, (int)open["sessions"]);
                Assert.Equal(OldActivity, ((DateTime)open["lastActivityUtc"]).ToUniversalTime());
                Assert.False(a.Stopped, "a refusal keeps the Attachment connected");
            }
        }

        [Fact]
        public void A_Held_Start_That_Is_Not_Released_Expires_With_Cold_Start_Wait_Expired()
        {
            const int attachReadySeconds = 10;
            using (var h = new Harness(attachReadySeconds: attachReadySeconds))
            {
                h.AddOther(KbB, SharedHostRecordState.Starting, workerPid: 7001, hostPid: 7101);
                long heldAtMs = h.Clock.NowMs;
                Assert.True(h.Runtime.Start());
                var a = h.Attach();
                h.Send(a, Sdk(1));

                for (int i = 0; i < 9; i++) h.Clock.Advance(1000);
                h.Clock.Advance(999);
                Assert.Equal(heldAtMs + attachReadySeconds * 1000 - 1, h.Clock.NowMs);
                Assert.Empty(a.JsonRpc());
                Assert.Equal(SharedHostRecordState.Held, h.Records.LastState);

                h.Clock.Advance(1001);

                var error = AssertToolError(a, 1, ConcurrencyErrorCatalog.WorkerStartupFailed, "cold-start-wait-expired", retryable: true);
                Assert.InRange((long)error["retryAfterMs"], 1000, attachReadySeconds * 1000);
                Assert.Empty(h.Launcher.Started);
                Assert.Equal(SharedHostRecordState.NoWorker, h.Records.LastState);
                Assert.False(a.Stopped);
            }
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("detach")]
        public void Removing_The_Last_Waiter_Drops_The_Held_Start(string removal)
        {
            using (var h = new Harness())
            {
                var other = h.AddOther(KbB, SharedHostRecordState.Starting, workerPid: 7001, hostPid: 7101);
                Assert.True(h.Runtime.Start());
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Sdk(1));
                h.Send(a, Sdk(2));

                if (removal == "cancel")
                {
                    h.Send(a, CancelNotification(1));
                    Assert.Equal(SharedHostRecordState.Held, h.Records.LastState);
                    h.Send(a, CancelNotification(2));
                }
                else
                {
                    a.Stop();
                }

                var last = h.Records.Last;
                Assert.Equal(SharedHostRecordState.NoWorker, (string)last["state"]);
                Assert.Equal(JTokenType.Null, last["heldSinceUtc"].Type);

                // The dropped start never creates a Worker, even when a Cold_Start slot frees up.
                other.State = SharedHostRecordState.Ready;
                h.Signals.Signal(WakePrefix + h.KbKey);
                h.Clock.Advance(SharedWorkerHostAdmission.HeldPollMs);
                Assert.Empty(h.Launcher.Started);
                Assert.False(b.Stopped);

                // The next request starts again.
                h.Send(b, Sdk(3));
                Assert.Single(h.Launcher.Started);
            }
        }

        [Theory]
        [InlineData("not-running")]
        [InlineData("start-time-mismatch")]
        public void A_Dead_Cold_Start_Owner_Stops_Counting_Within_5000_Ms(string death)
        {
            using (var h = new Harness())
            {
                h.AddOther(KbB, SharedHostRecordState.Starting, workerPid: 7001, hostPid: 7101);
                Assert.True(h.Runtime.Start());
                var a = h.Attach();
                h.Send(a, Sdk(1));
                Assert.Empty(h.Launcher.Started);

                long diedAtMs = h.Clock.NowMs;
                h.HostProbe.Results[7101] = death == "not-running"
                    ? ProcessProbeResult.NotRunning
                    : ProcessProbeResult.Running(OtherHostStartTicks + TimeSpan.TicksPerDay);

                while (h.Launcher.Started.Count == 0 && h.Clock.NowMs - diedAtMs < 5000) h.Clock.Advance(250);

                var child = Assert.Single(h.Launcher.Started);
                Assert.InRange(child.StartedAtMs - diedAtMs, 0, 5000);
                Assert.DoesNotContain(h.Signals.Sent, s => s.Name.StartsWith(EvictPrefix, StringComparison.Ordinal));
            }
        }

        [Fact]
        public void A_Contended_Admission_Mutex_Reads_Nothing_And_Is_Retried()
        {
            using (var h = new Harness())
            {
                h.Mutexes.HeldElsewhere.Add(AdmissionMutex);
                var self = h.NewRecordFor(KbA);
                var admission = new SharedWorkerHostAdmission(h.Settings, h.Clock, h.Mutexes, h.Records, h.Seams);

                var result = admission.Admit(self);

                Assert.Equal(SharedHostAdmissionOutcome.Contended, result.Outcome);
                Assert.Null(result.Decision);
                Assert.Empty(h.Memory.Reads);
                Assert.DoesNotContain("enumerate", h.Log);
                Assert.Empty(h.Records.States);

                // Through the runtime: no child while contended, a start shortly after the release.
                Assert.True(h.Runtime.Start());
                Assert.Empty(h.Launcher.Started);
                h.Mutexes.HeldElsewhere.Remove(AdmissionMutex);
                h.Clock.Advance(SharedWorkerHostAdmission.AdmissionRetryMs);
                Assert.Single(h.Launcher.Started);
            }
        }

        // ---- Eviction (Requirement 5 criteria 5 and 7, decision D8) ----

        [Theory]
        [InlineData("zero-attachments-first")]
        [InlineData("oldest-activity")]
        public void Eviction_Prefers_Zero_Attachment_Hosts_Then_The_Oldest_Activity(string scenario)
        {
            using (var h = new Harness(maxWorkers: 2))
            {
                SharedHostRecord b, c;
                if (scenario == "zero-attachments-first")
                {
                    b = h.AddOther(KbB, SharedHostRecordState.Ready, workerPid: 7001, hostPid: 7101, attachments: 1, lastActivityUtc: OldActivity);
                    c = h.AddOther(KbC, SharedHostRecordState.Ready, workerPid: 7002, hostPid: 7102, attachments: 0, lastActivityUtc: NewActivity);
                }
                else
                {
                    b = h.AddOther(KbB, SharedHostRecordState.Ready, workerPid: 7001, hostPid: 7101, attachments: 0, lastActivityUtc: OldActivity);
                    c = h.AddOther(KbC, SharedHostRecordState.Ready, workerPid: 7002, hostPid: 7102, attachments: 0, lastActivityUtc: NewActivity);
                }
                var victim = scenario == "zero-attachments-first" ? c : b;
                var spared = ReferenceEquals(victim, b) ? c : b;

                Assert.True(h.Runtime.Start());

                var signal = Assert.Single(h.Signals.Sent, s => s.Name.StartsWith(EvictPrefix, StringComparison.Ordinal));
                Assert.Equal(EvictPrefix + victim.KbKey, signal.Name);
                Assert.True(signal.AdmissionMutexHeld, "the evict signal is sent under the admission mutex");
                Assert.Empty(h.Launcher.Started);
                Assert.DoesNotContain(SharedHostRecordState.Starting, h.Records.States);

                // The victim stops its Worker; the next admission attempt starts this host's Worker.
                victim.WorkerPid = 0;
                victim.State = SharedHostRecordState.NoWorker;
                int reads = h.Memory.Reads.Count;
                h.Clock.Advance(SharedWorkerHostAdmission.HeldPollMs);

                Assert.Single(h.Launcher.Started);
                Assert.True(h.Memory.Reads.Count > reads, "the start after an eviction takes a fresh reading");
                Assert.DoesNotContain(h.Signals.Sent, s => s.Name == EvictPrefix + spared.KbKey);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Eviction_Victim_Rechecks_Its_Own_Evictability(bool busy)
        {
            using (var h = new Harness())
            {
                h.StartReady();
                var child = h.Launcher.Started.Single();
                var a = h.Attach();
                Assert.True(h.Signals.IsSubscribed(EvictPrefix + h.KbKey), "a running host must wait on its evict event");
                if (busy)
                {
                    h.Send(a, Sdk(1));
                    Assert.Single(h.Pump());
                }

                h.Signals.Signal(EvictPrefix + h.KbKey);

                if (busy)
                {
                    Assert.False(child.HasExited, "a request that arrived before the eviction wins over it");
                    Assert.Equal(child.Pid, (int)h.Records.Last["workerPid"]);
                }
                else
                {
                    Assert.True(child.HasExited);
                    Assert.Equal(0, (int)h.Records.Last["workerPid"]);
                    Assert.Equal(SharedHostRecordState.NoWorker, h.Records.LastState);
                }
                Assert.False(a.Stopped, "eviction keeps the Attachments connected");
            }
        }

        // ---- Heap recycle and replacement (Requirement 5 criteria 6 and 9) ----

        [Fact]
        public void Heap_Recycle_Stops_The_Old_Child_Before_Admission_And_Keeps_Attachments()
        {
            using (var h = new Harness(extraArgs: new[] { "--heap-recycle-mb", "100" }))
            {
                h.Launcher.NextWorkingSetMb = 200;
                h.StartReady();
                var old = h.Launcher.Started.Single();
                var a = h.Attach();
                var b = h.Attach();
                string aId = a.AttachmentId, bId = b.AttachmentId;
                long overAtMs = h.Clock.NowMs;
                h.Launcher.NextWorkingSetMb = 64;

                while (h.Launcher.Started.Count < 2 && h.Clock.NowMs - overAtMs < 60000) h.Clock.Advance(TickMs);

                Assert.Equal(2, h.Launcher.Started.Count);
                var replacement = h.Launcher.Started[1];
                Assert.True(old.HasExited);
                Assert.True(replacement.PreviousExitedAtLaunch, "the old child must have exited before the replacement starts");
                int kill = h.Log.IndexOf("kill:" + old.Pid);
                int secondRead = IndexOfNth(h.Log, "memory", 2);
                Assert.InRange(kill, 0, secondRead - 1);
                Assert.True(h.Log.IndexOf("launch:" + replacement.Pid) > secondRead);
                Assert.Equal(replacement.Pid, (int)h.Records.Last["workerPid"]);
                Assert.False(a.Stopped);
                Assert.False(b.Stopped);
                Assert.Equal(aId, a.AttachmentId);
                Assert.Equal(bId, b.AttachmentId);
            }
        }

        [Fact]
        public void Heap_Recycle_Zero_Disables_Recycling()
        {
            using (var h = new Harness(extraArgs: new[] { "--heap-recycle-mb", "0" }))
            {
                h.Launcher.NextWorkingSetMb = 4000;
                h.StartReady();
                h.Attach();

                for (int i = 0; i < 13; i++) h.Clock.Advance(TickMs);

                Assert.Single(h.Launcher.Started);
                Assert.False(h.Launcher.Started[0].HasExited);
            }
        }

        [Fact]
        public void A_Failed_Replacement_Fails_Queued_Requests_And_The_Next_Request_Starts_A_Worker()
        {
            using (var h = new Harness(extraArgs: new[] { "--heap-recycle-mb", "100" }))
            {
                h.Launcher.NextWorkingSetMb = 200;
                h.StartReady();
                var a = h.Attach();

                // Another Cold_Start holds the slot, so the replacement is held and requests queue.
                var other = h.AddOther(KbB, SharedHostRecordState.Starting, workerPid: 7001, hostPid: 7101);
                h.Clock.Advance(TickMs);
                Assert.True(h.Launcher.Started[0].HasExited);
                Assert.Single(h.Launcher.Started);
                h.Send(a, Sdk(1));

                h.Launcher.FailNextStarts = true;
                other.State = SharedHostRecordState.Ready;
                h.Signals.Signal(WakePrefix + h.KbKey);

                var error = AssertToolError(a, 1, ConcurrencyErrorCatalog.WorkerStartupFailed, "replacement-failed", retryable: true);
                Assert.InRange((long)error["retryAfterMs"], 1000, LongQueueWaitMs);
                Assert.False(a.Stopped, "a failed replacement keeps the Attachments connected");

                h.Launcher.FailNextStarts = false;
                h.Launcher.NextWorkingSetMb = 64;
                h.Send(a, Sdk(2));
                Assert.Equal(2, h.Launcher.Started.Count);
                h.Ready();
                Assert.Equal("2", ClientIdOf(Assert.Single(h.Pump())));
            }
        }

        // ---- Idle reaping and stopIfLast (Requirement 5 criterion 4, design "Idle reaping") ----

        [Fact]
        public void Idle_Reaping_Is_Measured_From_The_Later_Of_The_Last_Detach_And_The_Last_Completion()
        {
            const int idleMs = 20000;
            using (var h = new Harness(idleMs: idleMs))
            {
                h.StartReady();
                var a = h.Attach();
                h.Send(a, Sdk(1));
                var inFlight = Assert.Single(h.Pump());
                a.Stop();

                h.Clock.Advance(15000);
                h.Respond(inFlight);
                long completedAtMs = h.Clock.NowMs;

                // The detach was 15000 ms earlier: measured from it, the timeout would end 5000 ms from here.
                while (h.Clock.NowMs + 1000 < completedAtMs + idleMs)
                {
                    h.Clock.Advance(1000);
                    Assert.False(h.Runtime.IsStopRequested, "reaped " + (h.Clock.NowMs - completedAtMs) + " ms after the last completion");
                }

                while (!h.Runtime.IsStopRequested && h.Clock.NowMs - (completedAtMs + idleMs) <= 60000) h.Clock.Advance(1000);
                Assert.True(h.Runtime.IsStopRequested);
                Assert.InRange(h.Clock.NowMs - (completedAtMs + idleMs), 0, 60000);
            }
        }

        [Fact]
        public void Detach_With_Stop_If_Last_Stops_The_Worker_Only_When_It_Was_The_Last_Attachment()
        {
            using (var h = new Harness())
            {
                h.StartReady();
                var child = h.Launcher.Started.Single();
                var a = h.Attach();
                var b = h.Attach();

                h.Send(b, HostFrame(h, "detach", b, new JObject { ["stopIfLast"] = true }));
                Assert.True(b.Stopped);
                Assert.False(child.HasExited, "another Attachment remains");
                Assert.False(h.Runtime.IsStopRequested);

                h.Send(a, HostFrame(h, "detach", a, new JObject { ["stopIfLast"] = true }));
                Assert.True(a.Stopped);
                Assert.True(child.HasExited, "stopIfLast from the last Attachment stops the Worker at once");
                Assert.True(h.Runtime.IsStopRequested);
            }
        }

        [Fact]
        public void Detach_Without_Stop_If_Last_Keeps_The_Worker_Until_The_Idle_Timeout()
        {
            using (var h = new Harness())
            {
                h.StartReady();
                var child = h.Launcher.Started.Single();
                var a = h.Attach();

                h.Send(a, HostFrame(h, "detach", a));

                Assert.True(a.Stopped);
                Assert.False(child.HasExited);
                Assert.False(h.Runtime.IsStopRequested);
            }
        }

        // ---- Assertions and builders ----

        private static JObject AssertToolError(FakeAttachment attachment, int id, string code, string reason, bool retryable)
        {
            var frame = attachment.JsonRpc().LastOrDefault(f => f["id"] != null && f["id"].ToString() == id.ToString(CultureInfo.InvariantCulture));
            Assert.True(frame != null, "no response for request " + id + "; lines: " + string.Join(" | ", attachment.Lines.Select(l => l.ToString(Formatting.None))));
            Assert.Null(frame["error"]);
            var result = Assert.IsType<JObject>(frame["result"]);
            Assert.Equal("error", (string)result["status"]);
            Assert.Equal(code, (string)result["code"]);
            var error = Assert.IsType<JObject>(result["error"]);
            Assert.Equal(code, (string)error["code"]);
            Assert.Equal(reason, (string)error["reason"]);
            Assert.Equal(retryable, (bool)error["retryable"]);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string)error["dispatchState"]);
            return error;
        }

        private static int IndexOfNth(List<string> log, string entry, int n)
        {
            int seen = 0;
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i] == entry && ++seen == n) return i;
            }
            return -1;
        }

        private static JObject Sdk(int id) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "object",
            ["action"] = "Read",
            ["params"] = new JObject()
        };

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

        // The broker rewrites request ids; the test tags each request with its id in params.
        private static string ClientIdOf(JObject childLine) => (string)childLine["params"]?["testClientId"];

        private static SharedWorkerHostOptions ParseOptions(string kbPath, IEnumerable<string> extra)
        {
            var args = new List<string>
            {
                "--shared-host",
                "--worker-executable", @"C:\GeneXus\GxMcp.Worker.exe",
                "--kb", kbPath,
                "--installation", @"C:\GeneXus",
                "--driver", "native",
                "--major", "18"
            };
            args.AddRange(extra);
            return SharedWorkerHostOptions.Parse(args.ToArray());
        }

        // ---- Harness ----

        private sealed class Harness : IDisposable
        {
            private int _gatewayPid = 4242;

            internal Harness(
                int maxWorkers = 4,
                int coldStartConcurrency = 1,
                int memoryFloorMb = 1024,
                int attachReadySeconds = 120,
                int idleMs = 1800000,
                string[] extraArgs = null)
            {
                var args = new List<string> { "--idle-ms", idleMs.ToString(CultureInfo.InvariantCulture) };
                if (extraArgs != null) args.AddRange(extraArgs);
                Options = ParseOptions(KbA, args);
                KbKey = SharedHostKbKey.Compute(KbA);
                Settings = new SharedHostSettingsResult(
                    LongQueueWaitMs, 8, 4, maxWorkers, memoryFloorMb, coldStartConcurrency, attachReadySeconds,
                    new Dictionary<string, string>(), new List<string>());
                Records = new FakeRecordWriter(Log);
                Mutexes = new FakeMutexFactory(Log);
                Launcher = new FakeChildLauncher(Log, Clock);
                HostProbe = new FakeProcessProbe(Log);
                Registry = new FakeRegistry(Log, Records);
                Memory = new FakeMemoryReader(Log, Clock, Mutexes);
                Signals = new FakeSignalHub(Log, Mutexes);
                using (var self = Process.GetCurrentProcess())
                    HostProbe.Results[self.Id] = ProcessProbeResult.Running(self.StartTime.ToUniversalTime().Ticks);
                Seams = new SharedHostAdmissionSeams
                {
                    Registry = Registry,
                    HostProbe = HostProbe,
                    Memory = Memory,
                    Signals = Signals,
                    Warn = message => Warnings.Add(message)
                };
                Runtime = new SharedWorkerHostRuntime(
                    Options, Settings, Clock, Launcher, Records, Mutexes, new AlwaysRunningProbe(), Seams, runBackgroundThreads: false);
            }

            internal SharedWorkerHostOptions Options { get; }
            internal string KbKey { get; }
            internal SharedHostSettingsResult Settings { get; }
            internal List<string> Log { get; } = new List<string>();
            internal List<string> Warnings { get; } = new List<string>();
            internal FakeClock Clock { get; } = new FakeClock();
            internal FakeRecordWriter Records { get; }
            internal FakeMutexFactory Mutexes { get; }
            internal FakeChildLauncher Launcher { get; }
            internal FakeProcessProbe HostProbe { get; }
            internal FakeRegistry Registry { get; }
            internal FakeMemoryReader Memory { get; }
            internal FakeSignalHub Signals { get; }
            internal SharedHostAdmissionSeams Seams { get; }
            internal SharedWorkerHostRuntime Runtime { get; }

            // A live record of another host. The test mutates it in place; every enumeration reads it fresh.
            internal SharedHostRecord AddOther(
                string kbPath,
                string state,
                int workerPid,
                int hostPid,
                bool evictable = true,
                int attachments = 0,
                DateTime? lastActivityUtc = null,
                DateTime? heldSinceUtc = null)
            {
                var record = NewRecordFor(kbPath);
                record.HostPid = hostPid;
                record.HostStartTimeUtcTicks = OtherHostStartTicks;
                record.State = state;
                record.WorkerPid = workerPid;
                record.WorkerStartTimeUtcTicks = workerPid > 0 ? OtherHostStartTicks + 1 : 0;
                record.Evictable = evictable;
                record.Attachments = attachments;
                record.LastActivityUtc = lastActivityUtc;
                record.HeldSinceUtc = heldSinceUtc;
                HostProbe.Results[hostPid] = ProcessProbeResult.Running(OtherHostStartTicks);
                Registry.Others.Add(record);
                return record;
            }

            internal SharedHostRecord NewRecordFor(string kbPath)
            {
                using (var self = Process.GetCurrentProcess())
                {
                    return new SharedHostRecord
                    {
                        KbKey = SharedHostKbKey.Compute(kbPath),
                        Identity = new SharedHostRecordIdentity
                        {
                            Key = new string('a', 64),
                            KbPath = SharedHostKbKey.NormalizeKbPath(kbPath),
                            WorkerExecutable = @"c:\genexus\gxmcp.worker.exe",
                            InstallationPath = @"c:\genexus",
                            Driver = "native",
                            Major = "18"
                        },
                        HostPid = self.Id,
                        HostStartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
                        PipeName = "GxMcpShared2_" + new string('a', 64),
                        State = SharedHostRecordState.Admitting
                    };
                }
            }

            internal void StartReady()
            {
                Assert.True(Runtime.Start());
                Assert.Single(Launcher.Started);
                Ready();
            }

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
                    ["gatewayPid"] = _gatewayPid++,
                    ["gatewayStartTimeUtcTicks"] = 638000000000000000L,
                    ["attachNonce"] = Guid.NewGuid().ToString("N")
                }.ToString(Formatting.None);
                Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame, out SharedWorkerEnvelope envelope, out string parseError), parseError);
                Assert.True(Runtime.Attach(attachment, envelope, out string error), error);
                return attachment;
            }

            internal void Send(FakeAttachment attachment, JObject frame)
            {
                if (frame["id"] != null && frame["method"] != null && frame["params"] is JObject parameters)
                    parameters["testClientId"] = frame["id"].ToString();
                Runtime.Receive(attachment, frame.ToString(Formatting.None));
            }

            internal List<JObject> Pump()
            {
                var child = Launcher.Started.Last();
                int before = child.Received.Count;
                Runtime.PumpChildInbox();
                return child.Received.Skip(before).ToList();
            }

            internal void Ready() => Runtime.OnChildLine(Launcher.Started.Last(), new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/worker/sdk_ready"
            }.ToString(Formatting.None));

            internal void Respond(JObject childRequest) => Runtime.OnChildLine(Launcher.Started.Last(), new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = childRequest["id"].DeepClone(),
                ["result"] = new JObject { ["status"] = "ok" }
            }.ToString(Formatting.None));

            internal void ExitChild(FakeChild child)
            {
                child.Exit(1);
                Runtime.OnChildExited(child);
            }

            public void Dispose() => Runtime.Dispose();
        }

        // ---- Fakes ----

        // Same semantics as the task 3.5 fake: Advance moves the time, then runs once each the timers due at
        // or before the new time, earliest first, on the test thread.
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

        private sealed class FakeMutexFactory : ISharedHostMutexFactory
        {
            private readonly List<string> _log;
            private readonly HashSet<string> _held = new HashSet<string>(StringComparer.Ordinal);

            internal FakeMutexFactory(List<string> log) { _log = log; }

            internal HashSet<string> HeldElsewhere { get; } = new HashSet<string>(StringComparer.Ordinal);

            internal bool IsHeld(string name) => _held.Contains(name);

            public IDisposable TryAcquire(string name)
            {
                if (HeldElsewhere.Contains(name) || _held.Contains(name)) return null;
                _held.Add(name);
                _log.Add("mutex+:" + name);
                return new Handle(this, name);
            }

            private sealed class Handle : IDisposable
            {
                private readonly FakeMutexFactory _owner;
                private readonly string _name;
                private bool _disposed;

                internal Handle(FakeMutexFactory owner, string name) { _owner = owner; _name = name; }

                public void Dispose()
                {
                    if (_disposed) return;
                    _disposed = true;
                    _owner._held.Remove(_name);
                    _owner._log.Add("mutex-:" + _name);
                }
            }
        }

        private sealed class AlwaysRunningProbe : IProcessProbe
        {
            public ProcessProbeResult Probe(int pid) => ProcessProbeResult.Running(638000000000000000L);
        }

        private sealed class FakeProcessProbe : IProcessProbe
        {
            private readonly List<string> _log;

            internal FakeProcessProbe(List<string> log) { _log = log; }

            internal Dictionary<int, ProcessProbeResult> Results { get; } = new Dictionary<int, ProcessProbeResult>();

            public ProcessProbeResult Probe(int pid)
            {
                _log.Add("probe:" + pid.ToString(CultureInfo.InvariantCulture));
                return Results.TryGetValue(pid, out ProcessProbeResult result) ? result : ProcessProbeResult.NotRunning;
            }
        }

        // Keeps a parsed copy of every write: the runtime updates one record object in place.
        private sealed class FakeRecordWriter : ISharedHostRecordWriter
        {
            private readonly List<string> _log;

            internal FakeRecordWriter(List<string> log) { _log = log; }

            internal List<JObject> Snapshots { get; } = new List<JObject>();
            internal string LastJson { get; private set; }
            internal JObject Last => Snapshots.Last();
            internal string LastState => (string)Snapshots.Last()["state"];
            internal List<string> States => Snapshots.Select(s => (string)s["state"]).ToList();

            public void Write(SharedHostRecord record)
            {
                LastJson = record.ToJson();
                Snapshots.Add(JObject.Parse(LastJson));
                _log.Add("write:" + record.State);
            }

            public void Delete() => LastJson = null;
        }

        // Every registry v2 record on the "machine": the other hosts plus this host's last written record.
        private sealed class FakeRegistry : ISharedHostRegistryEnumerator
        {
            private readonly List<string> _log;
            private readonly FakeRecordWriter _self;

            internal FakeRegistry(List<string> log, FakeRecordWriter self)
            {
                _log = log;
                _self = self;
            }

            internal List<SharedHostRecord> Others { get; } = new List<SharedHostRecord>();

            public IReadOnlyList<RecordReadResult> ReadAll()
            {
                _log.Add("enumerate");
                var reads = Others.Select(r => RecordReadResult.FromJson(r.ToJson())).ToList();
                if (_self.LastJson != null) reads.Add(RecordReadResult.FromJson(_self.LastJson));
                return reads;
            }
        }

        private sealed class MemoryRead
        {
            internal long AtMs;
            internal bool AdmissionMutexHeld;
        }

        private sealed class FakeMemoryReader : ISharedHostMemoryReader
        {
            private readonly List<string> _log;
            private readonly FakeClock _clock;
            private readonly FakeMutexFactory _mutexes;

            internal FakeMemoryReader(List<string> log, FakeClock clock, FakeMutexFactory mutexes)
            {
                _log = log;
                _clock = clock;
                _mutexes = mutexes;
            }

            internal long NextMb { get; set; } = 4096;

            // Non-null makes every read fail with this text.
            internal string Failure { get; set; }

            internal List<MemoryRead> Reads { get; } = new List<MemoryRead>();

            public bool TryReadAvailableMb(out long availableMb, out string error)
            {
                _log.Add("memory");
                Reads.Add(new MemoryRead { AtMs = _clock.NowMs, AdmissionMutexHeld = _mutexes.IsHeld(AdmissionMutex) });
                if (Failure != null)
                {
                    availableMb = 0;
                    error = Failure;
                    return false;
                }
                availableMb = NextMb;
                error = null;
                return true;
            }
        }

        private sealed class SentSignal
        {
            internal string Name;
            internal bool AdmissionMutexHeld;
        }

        // Callbacks run synchronously on the signalling thread.
        private sealed class FakeSignalHub : ISharedHostSignalHub
        {
            private readonly List<string> _log;
            private readonly FakeMutexFactory _mutexes;
            private readonly Dictionary<string, List<Action>> _subscribers = new Dictionary<string, List<Action>>(StringComparer.Ordinal);

            internal FakeSignalHub(List<string> log, FakeMutexFactory mutexes)
            {
                _log = log;
                _mutexes = mutexes;
            }

            internal List<SentSignal> Sent { get; } = new List<SentSignal>();

            internal bool IsSubscribed(string name) => _subscribers.TryGetValue(name, out var list) && list.Count > 0;

            public void Signal(string name)
            {
                _log.Add("signal:" + name);
                Sent.Add(new SentSignal { Name = name, AdmissionMutexHeld = _mutexes.IsHeld(AdmissionMutex) });
                if (!_subscribers.TryGetValue(name, out var list)) return;
                foreach (var callback in list.ToList()) callback();
            }

            public IDisposable Subscribe(string name, Action onSignal)
            {
                if (!_subscribers.TryGetValue(name, out var list))
                {
                    list = new List<Action>();
                    _subscribers[name] = list;
                }
                list.Add(onSignal);
                return new Subscription(list, onSignal);
            }

            private sealed class Subscription : IDisposable
            {
                private readonly List<Action> _list;
                private readonly Action _callback;

                internal Subscription(List<Action> list, Action callback) { _list = list; _callback = callback; }

                public void Dispose() => _list.Remove(_callback);
            }
        }

        private sealed class FakeChildLauncher : ISharedHostChildLauncher
        {
            private readonly List<string> _log;
            private readonly FakeClock _clock;
            private int _nextPid = 61000;

            internal FakeChildLauncher(List<string> log, FakeClock clock)
            {
                _log = log;
                _clock = clock;
            }

            internal List<FakeChild> Started { get; } = new List<FakeChild>();
            internal bool FailNextStarts { get; set; }
            internal long NextWorkingSetMb { get; set; } = 64;

            public ISharedHostChild Start(ProcessStartInfo startInfo)
            {
                if (FailNextStarts)
                {
                    _log.Add("launch-failed");
                    throw new InvalidOperationException("Could not start shared Worker child.");
                }
                bool previousExited = Started.All(c => c.HasExited);
                var child = new FakeChild(++_nextPid, _clock.NowMs, previousExited, NextWorkingSetMb, _log);
                Started.Add(child);
                _log.Add("launch:" + child.Pid.ToString(CultureInfo.InvariantCulture));
                return child;
            }
        }

        private sealed class FakeChild : ISharedHostChild
        {
            private readonly ManualResetEvent _exited = new ManualResetEvent(false);
            private readonly List<string> _log;

            internal FakeChild(int pid, long startedAtMs, bool previousExitedAtLaunch, long workingSetMb, List<string> log)
            {
                Pid = pid;
                StartedAtMs = startedAtMs;
                PreviousExitedAtLaunch = previousExitedAtLaunch;
                WorkingSetMb = workingSetMb;
                StartTimeUtcTicks = DateTime.UtcNow.Ticks;
                _log = log;
            }

            internal long StartedAtMs { get; }
            internal bool PreviousExitedAtLaunch { get; }
            internal List<JObject> Received { get; } = new List<JObject>();

            public int Pid { get; }
            public long StartTimeUtcTicks { get; }
            public bool HasExited { get; private set; }
            public int ExitCode { get; private set; } = -1;

            // Fixed at launch from FakeChildLauncher.NextWorkingSetMb.
            internal long WorkingSetMb { get; set; }

            public long WorkingSetBytes => HasExited ? 0 : WorkingSetMb * 1024L * 1024L;
            public long PrivateBytes => HasExited ? 0 : 48L * 1024 * 1024;
            public WaitHandle ExitedHandle => _exited;
            public TextReader StandardOutput => TextReader.Null;
            public TextReader StandardError => TextReader.Null;

            public void WriteLine(string line)
            {
                if (HasExited) throw new IOException("The child has exited.");
                Received.Add(JObject.Parse(line));
            }

            public void Kill()
            {
                if (!HasExited) _log.Add("kill:" + Pid.ToString(CultureInfo.InvariantCulture));
                Exit(-1);
            }

            internal void Exit(int code)
            {
                HasExited = true;
                ExitCode = code;
                _exited.Set();
            }

            public void Dispose() { }
        }

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
            internal bool Stopped => Volatile.Read(ref _stopped) != 0;

            public string ConnectionId { get; }
            public string AttachmentId { get; set; } = string.Empty;

            public long NextSequence() => Interlocked.Increment(ref _sequence);

            public bool Enqueue(string line)
            {
                if (Stopped) return false;
                Lines.Add(JObject.Parse(line));
                return true;
            }

            public void Stop()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                _runtime.Detach(this);
            }

            internal List<JObject> JsonRpc() => Lines.Where(l => l["jsonrpc"] != null).ToList();

            internal List<JObject> HostFrames() => Lines.Where(l => l["jsonrpc"] == null).ToList();
        }
    }
}
