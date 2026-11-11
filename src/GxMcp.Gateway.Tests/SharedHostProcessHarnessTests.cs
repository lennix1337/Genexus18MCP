using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    // Process checks of Requirement 13 criteria 3 to 8 (.kiro/specs/concurrent-multi-session-workers,
    // tasks 7.3 to 7.8), run through SharedHostProcessHarness. Each test creates its own harness, and
    // RunAsync asserts after every test that no process, record or temporary root is left behind.
    // These are required gate tests: a failure is fixed in the owning broker, launcher or connection
    // component, not by weakening the check.
    [Trait("Category", "ProcessSmoke")]
    [Collection("SharedHostQueueProcessSmokeSerial")]
    public sealed class SharedHostProcessHarnessTests
    {
        private readonly ITestOutputHelper _output;

        public SharedHostProcessHarnessTests(ITestOutputHelper output) => _output = output;

        // Requirement 13 criterion 3, 2.2, 4.1 and 8.1: two Gateways that open one KB at the same time elect
        // one Worker_Host with one Worker, get distinct attachIds, and each receives only its own response to a
        // request whose JSON-RPC id is the same in both Gateways.
        [Fact]
        public Task Two_gateways_share_one_worker_and_receive_only_their_own_same_id_responses() => RunAsync(async harness =>
        {
            string kb = harness.CreateKbDirectory();
            var clients = await harness.StartClientsAsync(2);
            var a = clients[0];
            var b = clients[1];

            // Requirement 2.2: both opens start together with no Worker_Host running, so they elect together.
            var opens = await Task.WhenAll(a.OpenOrThrowAsync(kb), b.OpenOrThrowAsync(kb));
            var openA = opens[0];
            var openB = opens[1];
            _output.WriteLine("open a: " + openA.ToString(Newtonsoft.Json.Formatting.None));
            _output.WriteLine("open b: " + openB.ToString(Newtonsoft.Json.Formatting.None));

            // One Worker_Host and one Worker: the same identity in both attach replies and in the registry record.
            int hostPid = (int)openA["hostPid"]!;
            long hostTicks = (long)openA["hostStartTimeUtcTicks"]!;
            int workerPid = (int)openA["workerPid"]!;
            long workerTicks = (long)openA["workerStartTimeUtcTicks"]!;
            Assert.True(hostPid > 0 && workerPid > 0, openA.ToString());
            Assert.NotEqual(hostPid, workerPid);
            Assert.Equal(hostPid, (int)openB["hostPid"]!);
            Assert.Equal(hostTicks, (long)openB["hostStartTimeUtcTicks"]!);
            Assert.Equal(workerPid, (int)openB["workerPid"]!);
            Assert.Equal(workerTicks, (long)openB["workerStartTimeUtcTicks"]!);

            var record = harness.ReadRecord(kb);
            Assert.NotNull(record);
            Assert.Equal(hostPid, record!.HostPid);
            Assert.Equal(workerPid, record.WorkerPid);
            Assert.Single(harness.ReadRecords());

            // Distinct Attachments.
            string? attachA = (string?)openA["attachId"];
            string? attachB = (string?)openB["attachId"];
            Assert.False(string.IsNullOrWhiteSpace(attachA), openA.ToString());
            Assert.False(string.IsNullOrWhiteSpace(attachB), openB.ToString());
            Assert.NotEqual(attachA, attachB);

            // The broker reports both Attachments and the same Worker PID to each Gateway.
            var statusA = await a.WaitForStatusAsync(kb, s => (int?)s["attachments"] == 2, "two attachments");
            var statusB = await b.WaitForStatusAsync(kb, s => (int?)s["attachments"] == 2, "two attachments");
            Assert.Equal(workerPid, (int)statusA["workerPid"]!);
            Assert.Equal(workerPid, (int)statusB["workerPid"]!);

            // Requirement 4.1: of every broker and Worker process the harness saw (host-started events of both
            // clients, attach replies, the record), only the elected pair still runs.
            harness.AdoptRecordProcesses();
            var running = harness.RemainingProcesses();
            Assert.Equal(new[] { hostPid }, running.Where(p => p.Role == "broker").Select(p => p.Pid).ToArray());
            Assert.Equal(new[] { workerPid }, running.Where(p => p.Role == "worker").Select(p => p.Pid).ToArray());
            var hostStarts = a.Events.Concat(b.Events).Where(e => (string?)e["event"] == "host-started").ToList();
            _output.WriteLine("host-started events: " + hostStarts.Count);
            Assert.Contains(hostStarts, e => (int?)e["pid"] == hostPid);

            // Requirement 8.1: the same JSON-RPC id from both Gateways. The fake echoes the method and the
            // attachment the broker stamped, so each response names the Gateway it belongs to. The 150 ms
            // duration makes the second request queue behind the first while both ids are outstanding.
            const int sharedId = 7;
            int framesA = a.Frames(kb).Count;
            int framesB = b.Frames(kb).Count;
            var responses = await Task.WhenAll(
                a.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(sharedId, 150, "probe-a")),
                b.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(sharedId, 150, "probe-b")));
            AssertOwnResponse(responses[0], sharedId, "probe-a", attachA!, workerPid);
            AssertOwnResponse(responses[1], sharedId, "probe-b", attachB!, workerPid);

            // A barrier call on each attachment: the broker writes one pipe in order, so a misrouted copy of the
            // other Gateway's id-7 response would arrive before this barrier's response.
            await a.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(sharedId + 1, 0, "barrier-a"));
            await b.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(sharedId + 1, 0, "barrier-b"));

            AssertExactlyOneResponse(a.Frames(kb).Skip(framesA).ToList(), sharedId, "probe-a", a.Name);
            AssertExactlyOneResponse(b.Frames(kb).Skip(framesB).ToList(), sharedId, "probe-b", b.Name);
        });

        // SharedWorkerConnection sends a heartbeat every 10 s, so Requirement 13 criterion 4 allows 20 s for the
        // broker to report one Attachment. The broker detaches on pipe EOF or a detach frame at once, and the
        // supervisor tick (5 s) detaches a dead Gateway's Attachment within 10 s as a backstop, so a passing run
        // stays well inside the 10 s per-test budget; the 20 s bound is only the requirement's ceiling.
        private const int HeartbeatIntervalMs = 10_000;

        // Requirement 13 criterion 4 and 2.6: when one of two Gateways attached to one Worker_Host goes away, the
        // remaining Gateway keeps working on the same Worker and the broker counts one Attachment within 2
        // heartbeat intervals. Two departures, each with the same survivor: a graceful close (detach frame), then
        // a forcibly terminated Gateway process (pipe EOF, supervisor dead-Gateway check as the backstop). The
        // survivor has a request in flight while the other Gateway leaves, and that request must still complete.
        [Fact]
        public Task Remaining_gateway_keeps_the_worker_when_the_other_gateway_closes_or_dies() => RunAsync(async harness =>
        {
            string kb = harness.CreateKbDirectory();
            var clients = await harness.StartClientsAsync(3);
            var survivor = clients[0];
            var closing = clients[1];
            var killed = clients[2];

            var opens = await Task.WhenAll(survivor.OpenOrThrowAsync(kb), closing.OpenOrThrowAsync(kb));
            var openSurvivor = opens[0];
            string survivorAttach = (string)openSurvivor["attachId"]!;
            int workerPid = (int)openSurvivor["workerPid"]!;
            long workerTicks = (long)openSurvivor["workerStartTimeUtcTicks"]!;
            Assert.True(workerPid > 0 && workerTicks > 0, openSurvivor.ToString());
            Assert.Equal(workerPid, (int)opens[1]["workerPid"]!);
            await survivor.WaitForStatusAsync(kb, s => (int?)s["attachments"] == 2, "two attachments");

            int nextId = 1;
            async Task Depart(string how, Func<Task> leave)
            {
                // A request of the survivor is in flight while the other Gateway leaves (Requirement 2.6).
                int inFlightId = nextId++;
                var inFlight = survivor.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(inFlightId, 300, "in-flight-" + how));
                var clock = Stopwatch.StartNew();
                await leave();
                var status = await survivor.WaitForStatusAsync(kb, s => (int?)s["attachments"] == 1,
                    "one attachment after the " + how, 2 * HeartbeatIntervalMs);
                long detachMs = clock.ElapsedMilliseconds;
                _output.WriteLine(how + ": one attachment after " + detachMs + " ms; status "
                    + status.ToString(Newtonsoft.Json.Formatting.None));
                Assert.True(detachMs <= 2 * HeartbeatIntervalMs, how + " took " + detachMs + " ms");
                Assert.Equal(workerPid, (int)status["workerPid"]!);

                AssertOwnResponse(await inFlight, inFlightId, "in-flight-" + how, survivorAttach, workerPid);
                int afterId = nextId++;
                var after = await survivor.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(afterId, 0, "after-" + how));
                AssertOwnResponse(after, afterId, "after-" + how, survivorAttach, workerPid);

                // The same Worker process: PID plus start time, in the record too.
                Assert.True(harness.IsSameProcess(workerPid, workerTicks), "the Worker changed after the " + how);
                Assert.Equal(workerPid, harness.ReadRecord(kb)!.WorkerPid);
                Assert.DoesNotContain(survivor.Events, e => (string?)e["event"] == "disconnected");
            }

            await Depart("graceful close", () => closing.CloseAsync(kb));

            await killed.OpenOrThrowAsync(kb);
            await survivor.WaitForStatusAsync(kb, s => (int?)s["attachments"] == 2, "two attachments again");
            await Depart("client kill", () => { harness.KillClient(killed); return Task.CompletedTask; });
        });

        // Requirement 13 criterion 5: the length of the operation on the first KB and the latency allowance.
        private const int LongOperationMs = 5_000;
        private const int ProbeAllowanceMs = 500;
        private const int BaselineProbeCount = 5;
        private const int MinimumBusyProbeCount = 10;

        // Requirement 13 criterion 5, 1.1, 1.3 and 1.4: one Gateway with two KBs has one Worker_Host and one
        // Worker per KB, so an SDK_Operation of 5,000 ms on the first KB does not delay SDK_Operations on the
        // second. Every probe sent to the second KB while the first KB's operation runs completes within 500 ms
        // of the median of 5 baseline probes on the idle second KB. Latency is measured in this process around
        // each client call, so the client IPC cost is in the baseline and in the probes alike.
        [Fact]
        public Task Long_operation_on_one_kb_does_not_delay_probes_to_another_kb() => RunAsync(async harness =>
        {
            string kb1 = harness.CreateKbDirectory();
            string kb2 = harness.CreateKbDirectory();
            var client = await harness.StartClientAsync();

            var opens = await Task.WhenAll(client.OpenOrThrowAsync(kb1), client.OpenOrThrowAsync(kb2));
            var open1 = opens[0];
            var open2 = opens[1];
            _output.WriteLine("open kb1: " + open1.ToString(Newtonsoft.Json.Formatting.None));
            _output.WriteLine("open kb2: " + open2.ToString(Newtonsoft.Json.Formatting.None));

            // Requirement 1.1: a Worker_Host and a Worker per KB.
            int host1 = (int)open1["hostPid"]!;
            int host2 = (int)open2["hostPid"]!;
            Assert.True(host1 > 0 && host2 > 0, open1 + " " + open2);

            // ConnectV2 returns once the registry record is Ready, and the reply above is built from AttachInfo right
            // after. A start that was held (or still admitting) when its attachment was accepted was acknowledged with
            // workerPid 0; the broker's worker_restarted frame that names the Worker is applied later by the
            // connection's read loop (SharedWorkerHostLauncher.ConnectPipeProcess: "worker_restarted names the child
            // later"). So a reply can still be that acknowledgement (workerPid 0, state "held") although the Worker is
            // up and the open has returned. Cold_Start concurrency is 1 by default, so when the two opens race one of
            // the starts is held until the other ends; which one, and whether the read loop has run by the time the
            // reply is built, depends on scheduling (each open blocks a thread-pool thread of the client process while
            // it waits for the record, and the read loop needs one too). The Worker of each KB is therefore read from
            // the broker's live status, and a reply that does name a Worker must name that one.
            var status1 = await client.WaitForStatusAsync(kb1, s => (int?)s["workerPid"] > 0, "a Worker for the first KB");
            var status2 = await client.WaitForStatusAsync(kb2, s => (int?)s["workerPid"] > 0, "a Worker for the second KB");
            int worker1 = (int)status1["workerPid"]!;
            int worker2 = (int)status2["workerPid"]!;
            int reply1 = (int)open1["workerPid"]!;
            int reply2 = (int)open2["workerPid"]!;
            Assert.True((reply1 == 0 || reply1 == worker1) && (reply2 == 0 || reply2 == worker2),
                "open replies " + open1 + " " + open2 + " against live Workers " + worker1 + " and " + worker2);
            Assert.NotEqual(host1, host2);
            Assert.NotEqual(worker1, worker2);
            Assert.Equal(2, harness.ReadRecords().Count);
            string attach1 = (string)open1["attachId"]!;
            string attach2 = (string)open2["attachId"]!;

            int nextId = 1;
            async Task<long> Probe(string kb, string method, string attachId, int workerPid)
            {
                int id = nextId++;
                var clock = Stopwatch.StartNew();
                var response = await client.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(id, 0, method));
                long ms = clock.ElapsedMilliseconds;
                AssertOwnResponse(response, id, method, attachId, workerPid);
                return ms;
            }

            // One unmeasured call per KB, so first-call costs are not in the baseline.
            await Probe(kb1, "warm-up-1", attach1, worker1);
            await Probe(kb2, "warm-up-2", attach2, worker2);

            var baseline = new List<long>();
            for (int i = 0; i < BaselineProbeCount; i++) baseline.Add(await Probe(kb2, "baseline", attach2, worker2));
            long median = baseline.OrderBy(ms => ms).ElementAt(BaselineProbeCount / 2);
            long bound = median + ProbeAllowanceMs;
            _output.WriteLine("baseline ms: " + string.Join(", ", baseline) + "; median " + median + "; bound " + bound);

            // The long operation runs on the first KB's Worker before the first busy probe is sent.
            int longId = nextId++;
            var operationClock = Stopwatch.StartNew();
            var operation = client.CallAsync(kb1, SharedHostGatewayClient.ProbeFrame(longId, LongOperationMs, "long-operation"),
                SharedHostProcessHarness.DefaultTimeoutMs);
            var running = await client.WaitForStatusAsync(kb1, s => s["running"] is JObject, "the long operation running");
            Assert.Equal(attach1, (string?)running["running"]!["ownerAttachId"]);

            // Probes spread over the operation; every one must finish while the operation is still running.
            var busy = new List<long>();
            while (operationClock.ElapsedMilliseconds < LongOperationMs - 600 || busy.Count < MinimumBusyProbeCount)
            {
                long ms = await Probe(kb2, "busy-probe", attach2, worker2);
                Assert.False(operation.IsCompleted,
                    "busy probe " + (busy.Count + 1) + " took " + ms + " ms (bound " + bound + " ms) and finished after the long operation ("
                    + operationClock.ElapsedMilliseconds + " ms)");
                busy.Add(ms);
                await Task.Delay(250);
            }
            _output.WriteLine("busy-probe ms (" + busy.Count + "): " + string.Join(", ", busy) + "; max " + busy.Max());

            Assert.True(busy.Count >= MinimumBusyProbeCount, busy.Count + " busy probes");
            Assert.True(busy.All(ms => ms <= bound),
                "busy probes over " + bound + " ms (baseline median " + median + " + " + ProbeAllowanceMs + "): "
                + string.Join(", ", busy.Where(ms => ms > bound)));

            // The first KB's operation completes on its own Worker, after its scripted duration.
            AssertOwnResponse(await operation, longId, "long-operation", attach1, worker1);
            _output.WriteLine("long operation ms: " + operationClock.ElapsedMilliseconds);
            Assert.True(operationClock.ElapsedMilliseconds >= LongOperationMs, operationClock.ElapsedMilliseconds + " ms");
        });

        // Requirement 13 criterion 6: the bound from the start of the new Gateway's KB open to its completed probe.
        private const int RecoveryBoundMs = 10_000;

        // Requirement 13 criterion 6, 7.2 and 7.4: a Gateway, its broker and its Worker are forcibly terminated
        // (no shutdown handling), so the broker's registry v2 record stays behind naming dead processes. The
        // harness also plants an Isolated_Mode ownership record of the same KB whose owner is the dead Gateway
        // and whose Worker PID is a running sentinel with a different start time. A new Gateway opens the KB and
        // completes a probe within 10 s of starting the open; the sentinel still runs, both stale records are
        // gone, a new broker serves the KB, and the recovery is logged per removed record.
        [Fact]
        public Task New_gateway_recovers_after_forced_termination_without_stopping_an_unrelated_process() => RunAsync(async harness =>
        {
            string kb = harness.CreateKbDirectory();
            var clients = await harness.StartClientsAsync(2);
            var crashed = clients[0];
            var recovering = clients[1];
            var sentinel = harness.StartSentinel();

            var open = await crashed.OpenOrThrowAsync(kb);
            int oldHostPid = (int)open["hostPid"]!;
            long oldHostTicks = (long)open["hostStartTimeUtcTicks"]!;
            int oldWorkerPid = (int)open["workerPid"]!;
            long oldWorkerTicks = (long)open["workerStartTimeUtcTicks"]!;
            Assert.True(oldHostPid > 0 && oldWorkerPid > 0, open.ToString());
            await crashed.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(1, 0, "before-crash"));

            // The sentinel's PID with a start time five minutes off, owned by the Gateway that is about to die.
            long sentinelRecordedTicks = SharedHostProcessHarness.DifferentStartTime(sentinel.StartTimeUtcTicks);
            Assert.False(harness.IsSameProcess(sentinel.Pid, sentinelRecordedTicks));
            string ownershipPath = harness.PlantOwnershipRecord(
                kb, crashed.Pid, crashed.Tracked.StartTimeUtcTicks, sentinel.Pid, sentinelRecordedTicks);

            // Forced termination of the Gateway, the broker and the Worker, in that order, none of them graceful.
            harness.KillClient(crashed);
            var oldHost = harness.Track(oldHostPid, oldHostTicks, "broker");
            var oldWorker = harness.Track(oldWorkerPid, oldWorkerTicks, "worker");
            harness.KillOwned(oldHost, entireTree: false);
            harness.KillOwned(oldWorker, entireTree: false);
            Assert.False(harness.IsRunning(crashed.Tracked), "the crashed Gateway still runs");
            Assert.False(harness.IsRunning(oldHost), "the old broker still runs");
            Assert.False(harness.IsRunning(oldWorker), "the old Worker still runs");

            // The stale records are still in place, and the sentinel runs.
            var staleRecord = harness.ReadRecord(kb);
            Assert.NotNull(staleRecord);
            Assert.Equal(oldHostPid, staleRecord!.HostPid);
            Assert.True(File.Exists(ownershipPath), "the planted ownership record is missing");
            Assert.True(harness.IsRunning(sentinel), "the sentinel stopped before the recovery");

            // The new Gateway: open plus one probe within 10 s of starting the open.
            var clock = Stopwatch.StartNew();
            var reopen = await recovering.OpenOrThrowAsync(kb);
            var probe = await recovering.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(2, 0, "after-recovery"));
            long recoveryMs = clock.ElapsedMilliseconds;
            _output.WriteLine("recovery open " + (long?)reopen["openMs"] + " ms, open plus probe " + recoveryMs + " ms; reopen "
                + reopen.ToString(Newtonsoft.Json.Formatting.None));
            Assert.True(recoveryMs <= RecoveryBoundMs, "open plus probe took " + recoveryMs + " ms");

            int newHostPid = (int)reopen["hostPid"]!;
            long newHostTicks = (long)reopen["hostStartTimeUtcTicks"]!;
            int newWorkerPid = (int)reopen["workerPid"]!;
            AssertOwnResponse(probe, 2, "after-recovery", (string)reopen["attachId"]!, newWorkerPid);
            Assert.False(newHostPid == oldHostPid && newHostTicks == oldHostTicks, "the dead broker was reported as the host");
            Assert.True(harness.IsSameProcess(newHostPid, newHostTicks), "the new broker is not running");

            // Requirement 7.4: the sentinel, named in a record with another start time, was not terminated.
            Assert.True(harness.IsRunning(sentinel), "the sentinel was terminated by the recovery");

            // Requirement 7.2: both stale records were removed; the registry record now names the new broker.
            Assert.False(File.Exists(ownershipPath), "the stale ownership record is still present");
            var record = harness.ReadRecord(kb);
            Assert.NotNull(record);
            Assert.Equal(newHostPid, record!.HostPid);
            Assert.Equal(newHostTicks, record.HostStartTimeUtcTicks);
            Assert.Equal(newWorkerPid, record.WorkerPid);

            // Requirement 7.7: one log entry per removed record, and no stopped process.
            var recovery = recovering.Events
                .Where(e => (string?)e["event"] == "log")
                .Select(e => (string?)e["text"] ?? string.Empty)
                .Where(t => t.Contains("stale-ownership-recovery", StringComparison.Ordinal))
                .ToList();
            foreach (string line in recovery) _output.WriteLine("recovery log: " + line);
            Assert.Contains(recovery, t => t.Contains("action=removed-record registry=SharedWorkerRegistry", StringComparison.Ordinal)
                && t.Contains(" pid=" + oldHostPid + " ", StringComparison.Ordinal)
                && t.EndsWith(" reason=owner-not-running", StringComparison.Ordinal));
            // The sentinel's PID runs with another start time, so the record names another process; nothing was stopped.
            Assert.Contains(recovery, t => t.Contains("action=removed-record registry=WorkerOwnershipRegistry", StringComparison.Ordinal)
                && t.Contains(" pid=" + sentinel.Pid + " ", StringComparison.Ordinal)
                && t.EndsWith(" reason=start-time-mismatch", StringComparison.Ordinal));
            Assert.DoesNotContain(recovery, t => t.Contains("action=stopped-process", StringComparison.Ordinal));
        });

        // Requirement 13 criterion 7: the highest allowed Memory_Floor, and the margin below it that the measured
        // Available_Physical_Memory must keep for the floor to stay above the broker's own later reading.
        private const int MaxMemoryFloorMb = 8192;
        private const int MemoryMarginMb = 1024;
        private const long InjectedLowMemoryMb = 1000;

        // Requirement 13 criterion 7 and 6.2: with the Memory_Floor above Available_Physical_Memory, a Worker start
        // is refused with KB_POOL_FULL low-memory carrying the measured value and the floor, and no Worker process
        // starts. A running KB with its own Worker (floor 256) makes "unchanged" meaningful. Two refusals:
        // - the Gateway pre-check, before any broker process starts (the client elects for a KB with no broker);
        // - the broker's authoritative admission, for a broker the harness starts directly with the same floor: it
        //   writes a "refused" record and starts no child, and the Gateway open returns that typed refusal.
        // Both read the real machine value. When it is above 8192 - 1024 MB the floor cannot be set above it, so the
        // pre-check reading is replaced by an injected value instead; the broker has no such seam, and its part is
        // then reported as not exercised.
        [Fact]
        public Task Worker_start_below_the_memory_floor_is_refused_and_starts_no_worker() => RunAsync(async harness =>
        {
            Assert.True(MachineMemory.TryGetAvailablePhysicalMb(out long measuredMb, out string? memoryError),
                "available physical memory is unreadable: " + memoryError);
            bool injected = measuredMb > MaxMemoryFloorMb - MemoryMarginMb;
            _output.WriteLine("available physical memory " + measuredMb + " MB; floor " + MaxMemoryFloorMb + " MB; "
                + (injected ? "pre-check reading injected (" + InjectedLowMemoryMb + " MB), broker path not exercised" : "real readings"));

            // The running KB, with the lowest floor so its own start is never refused.
            string running = harness.CreateKbDirectory();
            var runningClient = await harness.StartClientAsync();
            var runningOpen = await runningClient.OpenOrThrowAsync(running);
            int runningWorker = (int)runningOpen["workerPid"]!;
            long runningWorkerTicks = (long)runningOpen["workerStartTimeUtcTicks"]!;
            Assert.True(runningWorker > 0 && runningWorkerTicks > 0, runningOpen.ToString());
            var before = harness.TestHostProcesses();
            Assert.Contains(before, p => p.Pid == runningWorker);

            var lowSettings = new SharedHostHarnessSettings { MemoryFloorMb = MaxMemoryFloorMb };
            var low = await harness.StartClientAsync(new SharedHostClientOptions
            {
                Settings = lowSettings,
                InjectedAvailableMemoryMb = injected ? InjectedLowMemoryMb : (long?)null
            });

            // Pre-check: no broker exists for this KB, so the client would elect and start one.
            string precheckKb = harness.CreateKbDirectory();
            var precheck = await low.OpenAsync(precheckKb);
            _output.WriteLine("pre-check refusal: " + precheck.ToString(Newtonsoft.Json.Formatting.None));
            long precheckMb = AssertLowMemoryRefusal(precheck);
            if (injected) Assert.Equal(InjectedLowMemoryMb, precheckMb);
            Assert.Null(harness.ReadRecord(precheckKb));
            Assert.DoesNotContain(low.Events, e => (string?)e["event"] == "host-started");

            var brokers = new List<int>();
            if (!injected)
            {
                // Broker: its own admission reads the machine value and refuses the start without a child.
                string brokerKb = harness.CreateKbDirectory();
                var broker = await harness.StartBrokerAsync(brokerKb, lowSettings,
                    r => r.State == SharedHostRecordState.Refused || r.WorkerPid > 0);
                brokers.Add(broker.Pid);
                var record = harness.ReadRecord(brokerKb)!;
                _output.WriteLine("broker record: " + record.ToJson());
                Assert.Equal(SharedHostRecordState.Refused, record.State);
                Assert.Equal(0, record.WorkerPid);
                Assert.NotNull(record.Refusal);
                Assert.Equal("KB_POOL_FULL", record.Refusal!.Code);
                Assert.Equal("low-memory", record.Refusal.Reason);
                Assert.Equal((long?)MaxMemoryFloorMb, record.Refusal.FloorMb);
                Assert.True(record.Refusal.AvailableMb > 0 && record.Refusal.AvailableMb < MaxMemoryFloorMb,
                    "broker reading " + record.Refusal.AvailableMb);

                var attach = await low.OpenAsync(brokerKb);
                _output.WriteLine("broker refusal: " + attach.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(record.Refusal.AvailableMb, AssertLowMemoryRefusal(attach));
                Assert.Equal(SharedHostRecordState.Refused, harness.ReadRecord(brokerKb)!.State);
                Assert.Equal(0, harness.ReadRecord(brokerKb)!.WorkerPid);
            }

            // No Worker started on the machine: the TestHost processes are the earlier ones plus the refusing broker.
            var after = harness.TestHostProcesses();
            _output.WriteLine("TestHost processes before [" + string.Join(", ", before.Select(p => p.Pid)) + "] after ["
                + string.Join(", ", after.Select(p => p.Pid)) + "] refusing brokers [" + string.Join(", ", brokers) + "]");
            Assert.Equal(before, after.Where(p => !brokers.Contains(p.Pid)).ToList());
            Assert.True(harness.IsSameProcess(runningWorker, runningWorkerTicks), "the running KB's Worker changed");
            var probe = await runningClient.CallAsync(running, SharedHostGatewayClient.ProbeFrame(1, 0, "after-refusal"));
            AssertOwnResponse(probe, 1, "after-refusal", (string)runningOpen["attachId"]!, runningWorker);
        });

        // The typed refusal of Requirement 6 criterion 2 in an open reply; returns the measured value.
        private static long AssertLowMemoryRefusal(JObject reply)
        {
            Assert.False((bool?)reply["ok"] ?? true, reply.ToString());
            Assert.Equal("KB_POOL_FULL", (string?)reply["code"]);
            Assert.Equal("low-memory", (string?)reply["reason"]);
            var error = reply["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("KB_POOL_FULL", (string?)error!["code"]);
            Assert.Equal("low-memory", (string?)error["reason"]);
            Assert.False((bool)error["retryable"]!);
            Assert.Equal("not-delivered", (string?)error["dispatchState"]);
            Assert.Equal(MaxMemoryFloorMb, (long)error["floorMb"]!);
            long availableMb = (long)error["availableMb"]!;
            Assert.True(availableMb > 0 && availableMb < MaxMemoryFloorMb, "measured " + availableMb);
            Assert.False(string.IsNullOrWhiteSpace((string?)error["hint"]), error.ToString());
            return availableMb;
        }

        // Requirement 13 criterion 8: the fake child's scripted SDK initialization, the bound between the two
        // starts, and the release bound of Requirement 6 criterion 4.
        private const int ColdStartMs = 1500;
        private const int StartSpreadMs = 1000;
        private const int ReleaseBoundMs = 500;

        // Requirement 13 criterion 8, 6.3, 6.4 and 11.6: two Gateways request Worker starts for two KBs within
        // 1000 ms with cold-start concurrency 1. One broker starts its Worker; the other holds its start without a
        // Worker process (record "held" with heldSinceUtc) until the first Cold_Start ends, then starts ("starting").
        // Worker timestamps come from the fake child (fake/status: initStartedUtcTicks, readyUtcTicks) and from
        // the OS (the Worker's process start time in the attach reply). While the start is held, a request sent to
        // the held KB queues with wait reason worker-starting in the broker's status_ack, and the Gateway's
        // Diagnostics_Surface builder (SharedHostDiagnostics) reports the held start with that waiting request.
        [Fact]
        public Task Second_cold_start_waits_for_the_first_and_reports_worker_starting() => RunAsync(async harness =>
        {
            var settings = new SharedHostHarnessSettings
            {
                ColdStartConcurrency = 1,
                ChildScript = new JObject { ["readyDelayMs"] = ColdStartMs }
            };
            var clients = await harness.StartClientsAsync(2, new SharedHostClientOptions { Settings = settings });
            string[] kbs = { harness.CreateKbDirectory(), harness.CreateKbDirectory() };

            // Every record change of both KBs while the opens run, with the time this process saw it.
            var timeline = new ConcurrentQueue<(DateTime SeenUtc, int Kb, SharedHostRecord Record)>();
            using var stopPolling = new CancellationTokenSource();
            var poller = Task.Run(async () =>
            {
                var last = new string?[2];
                while (!stopPolling.IsCancellationRequested)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var record = harness.ReadRecord(kbs[i]);
                        string? json = record?.ToJson();
                        if (record == null || json == last[i]) continue;
                        last[i] = json;
                        timeline.Enqueue((DateTime.UtcNow, i, record));
                    }
                    await Task.Delay(10);
                }
            });

            var opens = new[] { clients[0].OpenAsync(kbs[0]), clients[1].OpenAsync(kbs[1]) };

            // The held start: wait for one record in state "held".
            var clock = Stopwatch.StartNew();
            (DateTime SeenUtc, int Kb, SharedHostRecord Record) held = default;
            while (held.Record == null)
            {
                Assert.False(opens.All(o => o.IsCompleted), "both opens finished and no start was held");
                Assert.True(clock.ElapsedMilliseconds < SharedHostProcessHarness.DefaultTimeoutMs, "no start was held");
                held = timeline.FirstOrDefault(t => t.Record.State == SharedHostRecordState.Held);
                if (held.Record == null) await Task.Delay(10);
            }
            int second = held.Kb;
            int first = 1 - second;
            string heldKb = kbs[second];
            _output.WriteLine("held: kb" + (second + 1) + " " + held.Record.ToJson());
            Assert.Equal(0, held.Record.WorkerPid);
            Assert.NotNull(held.Record.HeldSinceUtc);

            // A request for the held KB queues behind the held start: an attachment of this process sends it.
            var identity = harness.IdentityFor(heldKb);
            var waiterResponses = new ConcurrentQueue<JObject>();
            using var waiter = new SharedWorkerConnection(identity, held.Record, "cold-start-waiter");
            waiter.LineReceived += line =>
            {
                var frame = JObject.Parse(line);
                if (frame["id"] != null && frame["method"] == null) waiterResponses.Enqueue(frame);
            };
            waiter.Connect(5000);
            await waiter.SendAsync(SharedHostGatewayClient.ProbeFrame(1, 0, "while-held"), CancellationToken.None);

            JObject? status = null;
            var statusClock = Stopwatch.StartNew();
            while (statusClock.ElapsedMilliseconds < ColdStartMs)
            {
                status = await waiter.RequestStatusAsync(1000);
                if (status?["queue"] is JArray q && q.Count > 0) break;
                await Task.Delay(10);
            }
            _output.WriteLine("held status: " + status?.ToString(Newtonsoft.Json.Formatting.None));
            Assert.NotNull(status);
            Assert.Equal(SharedHostRecordState.Held, (string?)status!["childState"]);
            var entry = Assert.Single((JArray)status["queue"]!);
            Assert.Equal(waiter.AttachId, (string?)entry["ownerAttachId"]);
            Assert.Equal("worker-starting", (string?)entry["waitReason"]);

            // The Diagnostics_Surface builder over this attachment and the harness registry.
            var configured = SharedHostSettingsResolver.Resolve(settings.ToSection(), false, string.Empty, _ => null);
            var probe = new ProcessIdentity();
            var diagnostics = new SharedHostDiagnostics(new SharedHostDiagnosticsInputs
            {
                PoolView = () => new[]
                {
                    new SharedHostDiagnosticsHost
                    {
                        IdentityKey = identity.KbKey,
                        HostPid = held.Record.HostPid,
                        RequestStatusAsync = (timeoutMs, ct) => waiter.RequestStatusAsync(timeoutMs, ct)
                    }
                },
                ReadRegistry = () => kbs
                    .Select(kb => RecordReadResult.ReadFile(harness.RecordPath(kb)))
                    .Where(read => read.Record != null)
                    .Select(read => new SharedHostDiagnosticsRecord
                    {
                        Record = read.Record!,
                        Liveness = SharedHostRecordClassifier.Classify(read, probe).Liveness
                    })
                    .ToList(),
                Configured = configured,
                GatewayPid = Environment.ProcessId
            });
            var block = await diagnostics.BuildAsync();
            _output.WriteLine("diagnostics admission: " + block["admission"]!.ToString(Newtonsoft.Json.Formatting.None));
            var admission = (JObject)block["admission"]!;
            Assert.Equal(1, (int)admission["coldStartConcurrency"]!);
            Assert.Equal(1, (int)admission["coldStartsInProgress"]!);
            var heldStart = Assert.Single((JArray)admission["heldStarts"]!);
            Assert.Equal(identity.KbKey, (string?)heldStart["identityKey"]);
            Assert.Equal(1, (int)heldStart["waiting"]!);
            var kbEntry = Assert.Single((JArray)block["kbs"]!);
            Assert.Contains(((JArray)kbEntry["queue"]!).OfType<JObject>(), q => (string?)q["waitReason"] == "worker-starting");

            var replies = await Task.WhenAll(opens);
            stopPolling.Cancel();
            await poller;
            var openFirst = replies[first];
            var openSecond = replies[second];
            _output.WriteLine("open kb" + (first + 1) + " (first): " + openFirst.ToString(Newtonsoft.Json.Formatting.None));
            _output.WriteLine("open kb" + (second + 1) + " (second): " + openSecond.ToString(Newtonsoft.Json.Formatting.None));
            Assert.True((bool?)openFirst["ok"] == true && (bool?)openSecond["ok"] == true, openFirst + " " + openSecond);

            // The two starts were requested within 1000 ms of each other.
            double spreadMs = Math.Abs(((DateTime)replies[0]["openStartedUtc"]! - (DateTime)replies[1]["openStartedUtc"]!).TotalMilliseconds);
            _output.WriteLine("open spread " + spreadMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            Assert.True(spreadMs <= StartSpreadMs, "the opens started " + spreadMs + " ms apart");

            // Record states: the first start was never held; the second was held, then started.
            var states = Enumerable.Range(0, 2).Select(i => timeline.Where(t => t.Kb == i).Select(t => t.Record.State).ToList()).ToArray();
            _output.WriteLine("states kb" + (first + 1) + ": " + string.Join(" > ", states[first]));
            _output.WriteLine("states kb" + (second + 1) + ": " + string.Join(" > ", states[second]));
            Assert.DoesNotContain(SharedHostRecordState.Held, states[first]);
            Assert.Contains(SharedHostRecordState.Starting, states[first]);
            int heldAt = states[second].IndexOf(SharedHostRecordState.Held);
            int startingAt = states[second].IndexOf(SharedHostRecordState.Starting);
            Assert.True(heldAt >= 0 && startingAt > heldAt, "kb" + (second + 1) + " states: " + string.Join(" > ", states[second]));
            var released = timeline.First(t => t.Kb == second && t.Record.State == SharedHostRecordState.Starting).Record;
            Assert.NotNull(released.ColdStartStartedUtc);

            // Worker timestamps from each fake child, read through its broker.
            async Task<JObject> ChildStatus(int kb, int id, JObject open)
            {
                var response = await clients[kb].CallAsync(kbs[kb], new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = "fake/status"
                });
                var result = (JObject)response["result"]!;
                Assert.Equal((int)open["workerPid"]!, (int)result["pid"]!);
                return result;
            }
            var childFirst = await ChildStatus(first, 100, openFirst);
            var childSecond = await ChildStatus(second, 101, openSecond);
            var firstReady = new DateTime((long)childFirst["readyUtcTicks"]!, DateTimeKind.Utc);
            var firstInit = new DateTime((long)childFirst["initStartedUtcTicks"]!, DateTimeKind.Utc);
            var secondInit = new DateTime((long)childSecond["initStartedUtcTicks"]!, DateTimeKind.Utc);
            var secondProcessStart = new DateTime((long)openSecond["workerStartTimeUtcTicks"]!, DateTimeKind.Utc);
            var secondRelease = released.ColdStartStartedUtc!.Value.ToUniversalTime();
            var heldSince = held.Record.HeldSinceUtc!.Value.ToUniversalTime();
            string Iso(DateTime t) => t.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            _output.WriteLine("first: init " + Iso(firstInit) + " ready " + Iso(firstReady)
                + "; second: held since " + Iso(heldSince) + " released " + Iso(secondRelease)
                + " process start " + Iso(secondProcessStart) + " init " + Iso(secondInit)
                + "; release after first ready " + (secondRelease - firstReady).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms");

            // The child clocks are coarse (about 16 ms on .NET Framework), hence the margin.
            Assert.True(firstReady - firstInit >= TimeSpan.FromMilliseconds(ColdStartMs - 50), "the first Cold_Start was too short");
            // Requirement 6.3: the second Worker process and its SDK initialization begin after the first Cold_Start
            // ended. The child's ready time precedes the broker's end of that Cold_Start, so this bound is safe.
            Assert.True(secondRelease >= firstReady, "the second start was released at " + Iso(secondRelease) + ", before " + Iso(firstReady));
            Assert.True(secondProcessStart >= firstReady, "the second Worker started at " + Iso(secondProcessStart) + ", before " + Iso(firstReady));
            Assert.True(secondInit >= firstReady, "the second SDK initialization began at " + Iso(secondInit) + ", before " + Iso(firstReady));
            // Requirement 6.4: released within 500 ms after the first Cold_Start ended.
            Assert.True(secondRelease - firstReady <= TimeSpan.FromMilliseconds(ReleaseBoundMs),
                "the held start was released " + (secondRelease - firstReady).TotalMilliseconds + " ms after the first Cold_Start ended");

            // The request that waited for the held start ran on the second Worker once it was ready.
            var waitClock = Stopwatch.StartNew();
            JObject? answered = null;
            while (answered == null && waitClock.ElapsedMilliseconds < 5000)
            {
                if (!waiterResponses.TryDequeue(out answered)) await Task.Delay(20);
            }
            Assert.NotNull(answered);
            AssertOwnResponse(answered!, 1, "while-held", waiter.AttachId!, (int)openSecond["workerPid"]!);
        });

        private static void AssertOwnResponse(JObject response, int id, string method, string attachId, int workerPid)
        {
            Assert.Null(response["error"]);
            Assert.Equal(id, (int)response["id"]!);
            var result = response["result"] as JObject;
            Assert.NotNull(result);
            Assert.True(result!["fake"]?.Value<bool>() == true, response.ToString());
            Assert.Equal(method, (string?)result["method"]);
            Assert.Equal(attachId, (string?)result["attachmentId"]);
            Assert.Equal(workerPid, (int)result["pid"]!);
        }

        // Every response with the shared id on one attachment: exactly one, and it is the sender's own.
        private static void AssertExactlyOneResponse(IReadOnlyList<JObject> frames, int id, string method, string client)
        {
            var sameId = frames.Where(f => f["method"] == null && f["id"]?.Type == JTokenType.Integer && (int)f["id"]! == id).ToList();
            Assert.True(sameId.Count == 1,
                client + " received " + sameId.Count + " responses with id " + id + ": "
                + string.Join(" | ", sameId.Select(f => f.ToString(Newtonsoft.Json.Formatting.None))));
            Assert.Equal(method, (string?)sameId[0]["result"]?["method"]);
        }

        // Per-test harness creation, cleanup and clean-state assertions (shared by tasks 7.3 to 7.8).
        private async Task RunAsync(Func<SharedHostProcessHarness, Task> check)
        {
            var harness = SharedHostProcessHarness.TryCreate(_output);
            if (harness == null) return; // Build artifact gate; GXMCP_SHAREDHOST_TESTHOST_EXE makes it strict.
            var clock = Stopwatch.StartNew();
            string root = harness.Root;
            SharedHostCleanupReport report;
            try
            {
                await check(harness);
                _output.WriteLine("checks " + clock.ElapsedMilliseconds + " ms");
            }
            finally
            {
                report = harness.Cleanup();
                _output.WriteLine("cleanup done " + clock.ElapsedMilliseconds + " ms: " + report);
            }

            Assert.True(report.IsClean, report.ToString());
            Assert.False(Directory.Exists(root), "the harness root is still present: " + root);
            foreach (var process in report.Tracked)
                Assert.False(harness.IsSameProcess(process.Pid, process.StartTimeUtcTicks), process + " is still running");
        }
    }
}
