using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            int worker1 = (int)open1["workerPid"]!;
            int worker2 = (int)open2["workerPid"]!;
            Assert.True(host1 > 0 && host2 > 0 && worker1 > 0 && worker2 > 0, open1 + " " + open2);
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
