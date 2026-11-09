using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    // Self-tests of SharedHostProcessHarness (task 7.2): the harness starts real processes, keeps them out of
    // the user's registries, and its cleanup leaves no process and no record behind.
    [Trait("Category", "ProcessSmoke")]
    [Collection("SharedHostQueueProcessSmokeSerial")]
    public sealed class SharedHostProcessHarnessSelfTests
    {
        private readonly ITestOutputHelper _output;

        public SharedHostProcessHarnessSelfTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Broker_and_client_cleanup_leaves_no_process_and_no_record()
        {
            var harness = SharedHostProcessHarness.TryCreate(_output);
            if (harness == null) return; // Build artifact gate; GXMCP_SHAREDHOST_TESTHOST_EXE makes it strict.
            var clock = Stopwatch.StartNew();
            SharedHostCleanupReport report;
            string root = harness.Root;
            try
            {
                string kb = harness.CreateKbDirectory();
                var broker = await harness.StartBrokerAsync(kb);
                _output.WriteLine("broker " + clock.ElapsedMilliseconds + " ms");
                var client = await harness.StartClientAsync();
                _output.WriteLine("client " + clock.ElapsedMilliseconds + " ms");

                var open = await client.OpenOrThrowAsync(kb);
                Assert.Equal(broker.Pid, (int)open["hostPid"]!);
                Assert.True((int)open["workerPid"]! > 0, open.ToString());
                Assert.NotEqual(client.Pid, broker.Pid);

                var response = await client.CallAsync(kb, SharedHostGatewayClient.ProbeFrame(1, 50));
                Assert.Null(response["error"]);
                Assert.True(response["result"]?["fake"]?.Value<bool>() == true, response.ToString());
                Assert.Equal((string?)open["attachId"], (string?)response["result"]!["attachmentId"]);

                var status = await client.WaitForStatusAsync(kb, s => (int?)s["attachments"] == 1, "one attachment");
                Assert.Equal((int)open["workerPid"]!, (int)status["workerPid"]!);
                Assert.NotNull(harness.ReadRecord(kb));
                Assert.Empty(harness.RealRegistryRecords());
                _output.WriteLine("checks " + clock.ElapsedMilliseconds + " ms");
            }
            finally
            {
                report = harness.Cleanup();
                _output.WriteLine("cleanup " + clock.ElapsedMilliseconds + " ms: " + report);
            }

            AssertClean(harness, report, root);
            Assert.Contains(report.Tracked, t => t.Role == "broker");
            Assert.Contains(report.Tracked, t => t.Role == "worker");
            Assert.Contains(report.Tracked, t => t.Role == "gateway-client");
            Assert.NotEmpty(report.RecordsRemoved);
        }

        [Fact]
        public async Task A_broker_elected_by_a_killed_client_is_still_cleaned_up()
        {
            var harness = SharedHostProcessHarness.TryCreate(_output);
            if (harness == null) return;
            SharedHostCleanupReport report;
            string root = harness.Root;
            try
            {
                string kb = harness.CreateKbDirectory();
                var client = await harness.StartClientAsync();
                var open = await client.OpenOrThrowAsync(kb);
                int hostPid = (int)open["hostPid"]!;
                Assert.Contains(client.Events, e => (string?)e["event"] == "host-started" && (int?)e["pid"] == hostPid);

                harness.KillClient(client);
                Assert.True(client.Process.HasExited);
                // The broker outlives a crashed Gateway; its record stays in the harness registry.
                var record = harness.ReadRecord(kb);
                Assert.NotNull(record);
                Assert.True(harness.IsSameProcess(record!.HostPid, record.HostStartTimeUtcTicks), "the broker exited with its client");
                Assert.Empty(harness.RealRegistryRecords());
            }
            finally
            {
                report = harness.Cleanup();
                _output.WriteLine(report.ToString());
            }

            AssertClean(harness, report, root);
            Assert.Contains(report.Killed, t => t.Role == "broker");
        }

        [Fact]
        public async Task Planted_records_never_adopt_an_unrelated_process()
        {
            var harness = SharedHostProcessHarness.TryCreate(_output);
            if (harness == null) return;
            SharedHostCleanupReport report;
            string root = harness.Root;
            try
            {
                var broker = await harness.StartBrokerAsync(harness.CreateKbDirectory());
                var sentinel = harness.StartSentinel();
                string otherStartTime = harness.CreateKbDirectory();
                string otherImage = harness.CreateKbDirectory();
                // A TestHost PID with another start time: a reused PID, not the recorded process.
                harness.PlantSharedHostRecord(otherStartTime, broker.Pid,
                    SharedHostProcessHarness.DifferentStartTime(broker.Tracked.StartTimeUtcTicks));
                // The recorded PID and start time, but the image is not the TestHost.
                harness.PlantSharedHostRecord(otherImage, sentinel.Pid, sentinel.StartTimeUtcTicks);
                harness.PlantOwnershipRecord(otherImage, sentinel.Pid, sentinel.StartTimeUtcTicks);

                Assert.Equal(broker.Pid, harness.ReadRecord(otherStartTime)!.HostPid);
                Assert.Empty(harness.AdoptRecordProcesses());
                Assert.Equal(4, harness.RemainingRecordFiles().Count);
                Assert.True(harness.IsRunning(sentinel));
            }
            finally
            {
                report = harness.Cleanup();
                _output.WriteLine(report.ToString());
            }

            AssertClean(harness, report, root);
            Assert.Equal(4, report.RecordsRemoved.Count);
        }

        private static void AssertClean(SharedHostProcessHarness harness, SharedHostCleanupReport report, string root)
        {
            Assert.True(report.IsClean, report.ToString());
            Assert.False(Directory.Exists(root), "the harness root is still present: " + root);
            // Independent of the report: every PID the harness recorded is gone or now names another process.
            foreach (var process in report.Tracked)
                Assert.False(harness.IsSameProcess(process.Pid, process.StartTimeUtcTicks), process + " is still running");
            Assert.DoesNotContain(report.Tracked, t => t.StartTimeUtcTicks <= 0);
        }
    }
}
