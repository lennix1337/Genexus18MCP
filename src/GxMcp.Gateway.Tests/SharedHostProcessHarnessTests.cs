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
