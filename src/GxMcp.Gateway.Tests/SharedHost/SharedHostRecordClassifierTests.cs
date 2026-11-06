using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Unit tests for the registry v2 record model, the pure liveness classifier,
    // and the production process probe (ProcessIdentity).
    // Requirements 7.1, 7.8 (classification).
    public class SharedHostRecordClassifierTests
    {
        // A start time aligned to a whole millisecond, so that adding fewer than
        // 10_000 ticks stays inside the same millisecond.
        private const long AlignedStartTicks = 638_700_000_000_000_000L;
        private const int HostPid = 4120;

        // A PID that cannot exist on Windows (PIDs are multiples of 4 and far smaller).
        private const int ImpossiblePid = int.MaxValue;

        private static readonly string[] DesignStates =
        {
            "admitting", "held", "starting", "ready", "replacing",
            "recovering", "no-worker", "refused", "stopping"
        };

        // ---------------------------------------------------------------
        // Classifier
        // ---------------------------------------------------------------

        [Fact]
        public void Classify_AbsentRecord_IsStaleOwnerNotRunning_WithoutProbing()
        {
            var probe = new FakeProcessProbe();

            var result = SharedHostRecordClassifier.Classify(RecordReadResult.Absent, probe);

            Assert.Equal(RecordLiveness.Stale, result.Liveness);
            Assert.Equal(StaleReason.OwnerNotRunning, result.Reason);
            Assert.Null(result.Record);
            Assert.Empty(probe.ProbedPids);
        }

        [Theory]
        [InlineData("{ this is not json")]
        [InlineData("")]
        [InlineData("[1, 2, 3]")]
        public void Classify_UnparseableRecord_IsStaleUnparseableRecord_WithoutProbing(string text)
        {
            var probe = new FakeProcessProbe();
            var read = RecordReadResult.FromJson(text);

            Assert.Equal(RecordReadStatus.Unparseable, read.Status);
            Assert.False(string.IsNullOrWhiteSpace(read.Error));

            var result = SharedHostRecordClassifier.Classify(read, probe);

            Assert.Equal(RecordLiveness.Stale, result.Liveness);
            Assert.Equal(StaleReason.UnparseableRecord, result.Reason);
            Assert.Empty(probe.ProbedPids);
        }

        [Fact]
        public void Classify_OwnerNotRunning_IsStaleOwnerNotRunning()
        {
            var probe = new FakeProcessProbe();
            probe.Set(HostPid, ProcessProbeResult.NotRunning);

            var result = SharedHostRecordClassifier.Classify(Parsed(SampleRecord()), probe);

            Assert.Equal(RecordLiveness.Stale, result.Liveness);
            Assert.Equal(StaleReason.OwnerNotRunning, result.Reason);
            Assert.Equal(new[] { HostPid }, probe.ProbedPids);
        }

        [Theory]
        [InlineData(10_000L)]   // exactly one millisecond later
        [InlineData(-1L)]       // one tick earlier crosses into the previous millisecond
        [InlineData(-10_000L)]  // one millisecond earlier
        public void Classify_StartTimeDiffersAtMillisecondPrecision_IsStaleStartTimeMismatch(long deltaTicks)
        {
            var probe = new FakeProcessProbe();
            probe.Set(HostPid, ProcessProbeResult.Running(AlignedStartTicks + deltaTicks));

            var result = SharedHostRecordClassifier.Classify(Parsed(SampleRecord()), probe);

            Assert.Equal(RecordLiveness.Stale, result.Liveness);
            Assert.Equal(StaleReason.StartTimeMismatch, result.Reason);
        }

        [Fact]
        public void Classify_StartTimeUnreadable_IsLivenessUnverified_AndKeepsTheRecordedPid()
        {
            var probe = new FakeProcessProbe();
            probe.Set(HostPid, ProcessProbeResult.RunningStartTimeUnreadable);

            var result = SharedHostRecordClassifier.Classify(Parsed(SampleRecord()), probe);

            Assert.Equal(RecordLiveness.LivenessUnverified, result.Liveness);
            Assert.Equal(StaleReason.None, result.Reason);
            Assert.NotNull(result.Record);
            Assert.Equal(HostPid, result.Record!.HostPid);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(9_999L)] // still inside the same millisecond
        public void Classify_OwnerRunningWithEqualStartTimeAtMillisecondPrecision_IsLive(long deltaTicks)
        {
            var probe = new FakeProcessProbe();
            probe.Set(HostPid, ProcessProbeResult.Running(AlignedStartTicks + deltaTicks));

            var result = SharedHostRecordClassifier.Classify(Parsed(SampleRecord()), probe);

            Assert.Equal(RecordLiveness.Live, result.Liveness);
            Assert.Equal(StaleReason.None, result.Reason);
            Assert.NotNull(result.Record);
        }

        [Fact]
        public void Classify_LiveHostWithoutWorker_IsLive()
        {
            // A held or refused start has workerPid=0; liveness is decided by the host owner only.
            var record = SampleRecord();
            record.State = "held";
            record.WorkerPid = 0;
            record.WorkerStartTimeUtcTicks = 0;
            var probe = new FakeProcessProbe();
            probe.Set(HostPid, ProcessProbeResult.Running(AlignedStartTicks));

            var result = SharedHostRecordClassifier.Classify(Parsed(record), probe);

            Assert.Equal(RecordLiveness.Live, result.Liveness);
            Assert.Equal(new[] { HostPid }, probe.ProbedPids);
        }

        // ---------------------------------------------------------------
        // Registry v2 record JSON
        // ---------------------------------------------------------------

        [Fact]
        public void States_ListsExactlyTheDesignValues()
        {
            Assert.Equal(DesignStates.OrderBy(s => s, StringComparer.Ordinal),
                SharedHostRecordState.All.OrderBy(s => s, StringComparer.Ordinal));
        }

        [Fact]
        public void NewRecord_HasSchema2()
        {
            Assert.Equal(2, new SharedHostRecord().Schema);
        }

        [Theory]
        [InlineData("admitting")]
        [InlineData("held")]
        [InlineData("starting")]
        [InlineData("ready")]
        [InlineData("replacing")]
        [InlineData("recovering")]
        [InlineData("no-worker")]
        [InlineData("refused")]
        [InlineData("stopping")]
        public void Record_RoundTripsEveryState(string state)
        {
            var record = SampleRecord();
            record.State = state;

            var back = RoundTrip(record);

            Assert.Equal(state, back.State);
            AssertSameRecord(record, back);
        }

        [Fact]
        public void Record_RoundTripsWorkerPidZero_AndRefusal()
        {
            var record = SampleRecord();
            record.State = "refused";
            record.WorkerPid = 0;
            record.WorkerStartTimeUtcTicks = 0;
            record.Refusal = new SharedHostRefusal
            {
                Code = "KB_POOL_FULL",
                Reason = "low-memory",
                AvailableMb = 900,
                FloorMb = 1024
            };

            var json = record.ToJson();
            var obj = JObject.Parse(json);
            Assert.Equal(0, obj.Value<int>("workerPid"));
            var refusal = (JObject)obj["refusal"]!;
            Assert.Equal("KB_POOL_FULL", refusal.Value<string>("code"));
            Assert.Equal("low-memory", refusal.Value<string>("reason"));
            Assert.Equal(900, refusal.Value<long>("availableMb"));
            Assert.Equal(1024, refusal.Value<long>("floorMb"));

            var back = RoundTrip(record);

            Assert.Equal(0, back.WorkerPid);
            Assert.NotNull(back.Refusal);
            Assert.Equal("KB_POOL_FULL", back.Refusal!.Code);
            Assert.Equal("low-memory", back.Refusal.Reason);
            Assert.Equal(900, back.Refusal.AvailableMb);
            Assert.Equal(1024, back.Refusal.FloorMb);
            AssertSameRecord(record, back);
        }

        [Fact]
        public void Record_WritesDesignPropertyNames_AndNullsForAbsentOptionalFields()
        {
            var record = SampleRecord();
            record.HeldSinceUtc = null;
            record.Refusal = null;

            var obj = JObject.Parse(record.ToJson());

            foreach (var name in new[]
            {
                "schema", "kbKey", "identity", "hostPid", "hostStartTimeUtcTicks", "launchedByGatewayPid",
                "workerPid", "workerStartTimeUtcTicks", "pipeName", "generation", "state", "heldSinceUtc",
                "coldStartStartedUtc", "coldStartConcurrency", "attachments", "inFlight", "queued",
                "backgroundActive", "evictable", "lastActivityUtc", "refusal", "updatedUtc"
            })
            {
                Assert.True(obj.ContainsKey(name), "missing property " + name);
            }

            var identity = (JObject)obj["identity"]!;
            foreach (var name in new[]
            {
                "key", "kbPath", "workerExecutable", "installationPath", "driver", "major", "profileConfigPath"
            })
            {
                Assert.True(identity.ContainsKey(name), "missing identity property " + name);
            }

            Assert.Equal(2, obj.Value<int>("schema"));
            Assert.Equal(JTokenType.Null, obj["refusal"]!.Type);
            Assert.Equal(JTokenType.Null, obj["heldSinceUtc"]!.Type);
        }

        [Fact]
        public void FromJson_ParsesTheDesignRecordShape()
        {
            const string json = @"{
  ""schema"": 2,
  ""kbKey"": ""kb-key-a"",
  ""identity"": {
    ""key"": ""identity-key-a"",
    ""kbPath"": ""C:\\KBs\\KbA"",
    ""workerExecutable"": ""<install-dir>\\GxMcp.Worker.exe"",
    ""installationPath"": ""<gx-install-dir>"",
    ""driver"": ""native-sdk"",
    ""major"": ""18"",
    ""profileConfigPath"": ""<profile-dir>\\config.json""
  },
  ""hostPid"": 4120,
  ""hostStartTimeUtcTicks"": 638700000000000000,
  ""launchedByGatewayPid"": 3900,
  ""workerPid"": 0,
  ""workerStartTimeUtcTicks"": 0,
  ""pipeName"": ""GxMcpShared2_identity-key-a"",
  ""generation"": 3,
  ""state"": ""held"",
  ""heldSinceUtc"": ""2026-09-25T12:00:00.0000000Z"",
  ""coldStartStartedUtc"": null,
  ""coldStartConcurrency"": 1,
  ""attachments"": 2,
  ""inFlight"": 0,
  ""queued"": 3,
  ""backgroundActive"": false,
  ""evictable"": false,
  ""lastActivityUtc"": ""2026-09-25T12:03:10.0000000Z"",
  ""refusal"": null,
  ""updatedUtc"": ""2026-09-25T12:03:10.0000000Z""
}";

            var read = RecordReadResult.FromJson(json);

            Assert.Equal(RecordReadStatus.Parsed, read.Status);
            var r = read.Record!;
            Assert.Equal(2, r.Schema);
            Assert.Equal("kb-key-a", r.KbKey);
            Assert.Equal("identity-key-a", r.Identity.Key);
            Assert.Equal(@"C:\KBs\KbA", r.Identity.KbPath);
            Assert.Equal("native-sdk", r.Identity.Driver);
            Assert.Equal("18", r.Identity.Major);
            Assert.Equal(4120, r.HostPid);
            Assert.Equal(638_700_000_000_000_000L, r.HostStartTimeUtcTicks);
            Assert.Equal(3900, r.LaunchedByGatewayPid);
            Assert.Equal(0, r.WorkerPid);
            Assert.Equal("GxMcpShared2_identity-key-a", r.PipeName);
            Assert.Equal(3, r.Generation);
            Assert.Equal("held", r.State);
            Assert.Equal(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc), r.HeldSinceUtc!.Value.ToUniversalTime());
            Assert.Null(r.ColdStartStartedUtc);
            Assert.Equal(2, r.Attachments);
            Assert.Equal(3, r.Queued);
            Assert.Null(r.Refusal);
        }

        [Fact]
        public void ReadFile_MissingFileIsAbsent_InvalidFileIsUnparseable_ValidFileIsParsed()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gxmcp-tests-record-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var missing = RecordReadResult.ReadFile(Path.Combine(dir, "missing.json"));
                Assert.Equal(RecordReadStatus.Absent, missing.Status);
                Assert.Null(missing.Record);

                var invalidPath = Path.Combine(dir, "invalid.json");
                File.WriteAllText(invalidPath, "{ truncated");
                var invalid = RecordReadResult.ReadFile(invalidPath);
                Assert.Equal(RecordReadStatus.Unparseable, invalid.Status);
                Assert.False(string.IsNullOrWhiteSpace(invalid.Error));

                var validPath = Path.Combine(dir, "valid.json");
                var record = SampleRecord();
                File.WriteAllText(validPath, record.ToJson());
                var valid = RecordReadResult.ReadFile(validPath);
                Assert.Equal(RecordReadStatus.Parsed, valid.Status);
                AssertSameRecord(record, valid.Record!);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ---------------------------------------------------------------
        // ProcessIdentity (production IProcessProbe)
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessIdentity_ProbeCurrentProcess_IsRunningWithItsStartTime()
        {
            using var self = Process.GetCurrentProcess();
            var expectedMs = self.StartTime.ToUniversalTime().Ticks / 10_000;

            IProcessProbe probe = new ProcessIdentity();
            var result = probe.Probe(self.Id);

            Assert.Equal(ProcessProbeStatus.Running, result.Status);
            Assert.Equal(expectedMs, result.StartTimeUtcTicks / 10_000);
        }

        [Fact]
        public void ProcessIdentity_ProbeImpossiblePid_IsNotRunning()
        {
            var result = new ProcessIdentity().Probe(ImpossiblePid);

            Assert.Equal(ProcessProbeStatus.NotRunning, result.Status);
        }

        [Fact]
        public void ProcessIdentity_ClassifiesARecordOwnedByTheCurrentProcessAsLive()
        {
            using var self = Process.GetCurrentProcess();
            var record = SampleRecord();
            record.HostPid = self.Id;
            record.HostStartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks;

            var result = SharedHostRecordClassifier.Classify(Parsed(record), new ProcessIdentity());

            Assert.Equal(RecordLiveness.Live, result.Liveness);
        }

        [Fact]
        public void ProcessIdentity_TryGetImagePath_ReturnsTheCurrentProcessImage()
        {
            var expected = Environment.ProcessPath;
            Assert.False(string.IsNullOrEmpty(expected));

            var ok = ProcessIdentity.TryGetImagePath(Environment.ProcessId, out var imagePath, out var error);

            Assert.True(ok, "TryGetImagePath failed: " + error);
            Assert.Null(error);
            Assert.Equal(Path.GetFullPath(expected!), Path.GetFullPath(imagePath!), ignoreCase: true);
        }

        [Fact]
        public void ProcessIdentity_TryGetImagePath_ImpossiblePid_FailsWithANamedError()
        {
            var ok = ProcessIdentity.TryGetImagePath(ImpossiblePid, out var imagePath, out var error);

            Assert.False(ok);
            Assert.Null(imagePath);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static RecordReadResult Parsed(SharedHostRecord record)
        {
            var read = RecordReadResult.FromJson(record.ToJson());
            Assert.Equal(RecordReadStatus.Parsed, read.Status);
            return read;
        }

        private static SharedHostRecord RoundTrip(SharedHostRecord record)
        {
            var read = RecordReadResult.FromJson(record.ToJson());
            Assert.Equal(RecordReadStatus.Parsed, read.Status);
            Assert.NotNull(read.Record);
            return read.Record!;
        }

        private static SharedHostRecord SampleRecord()
        {
            return new SharedHostRecord
            {
                KbKey = "kb-key-a",
                Identity = new SharedHostRecordIdentity
                {
                    Key = "identity-key-a",
                    KbPath = @"C:\KBs\KbA",
                    WorkerExecutable = @"<install-dir>\GxMcp.Worker.exe",
                    InstallationPath = "<gx-install-dir>",
                    Driver = "native-sdk",
                    Major = "18",
                    ProfileConfigPath = @"<profile-dir>\config.json"
                },
                HostPid = HostPid,
                HostStartTimeUtcTicks = AlignedStartTicks,
                LaunchedByGatewayPid = 3900,
                WorkerPid = 5012,
                WorkerStartTimeUtcTicks = AlignedStartTicks + 100_000_000L,
                PipeName = "GxMcpShared2_identity-key-a",
                Generation = 3,
                State = "ready",
                HeldSinceUtc = new DateTime(2026, 9, 25, 11, 59, 0, DateTimeKind.Utc),
                ColdStartStartedUtc = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc),
                ColdStartConcurrency = 1,
                Attachments = 2,
                InFlight = 1,
                Queued = 3,
                BackgroundActive = true,
                Evictable = true,
                LastActivityUtc = new DateTime(2026, 9, 25, 12, 3, 10, DateTimeKind.Utc),
                Refusal = null,
                UpdatedUtc = new DateTime(2026, 9, 25, 12, 3, 10, DateTimeKind.Utc)
            };
        }

        private static void AssertSameRecord(SharedHostRecord expected, SharedHostRecord actual)
        {
            Assert.Equal(expected.Schema, actual.Schema);
            Assert.Equal(expected.KbKey, actual.KbKey);
            Assert.Equal(expected.Identity.Key, actual.Identity.Key);
            Assert.Equal(expected.Identity.KbPath, actual.Identity.KbPath);
            Assert.Equal(expected.Identity.WorkerExecutable, actual.Identity.WorkerExecutable);
            Assert.Equal(expected.Identity.InstallationPath, actual.Identity.InstallationPath);
            Assert.Equal(expected.Identity.Driver, actual.Identity.Driver);
            Assert.Equal(expected.Identity.Major, actual.Identity.Major);
            Assert.Equal(expected.Identity.ProfileConfigPath, actual.Identity.ProfileConfigPath);
            Assert.Equal(expected.HostPid, actual.HostPid);
            Assert.Equal(expected.HostStartTimeUtcTicks, actual.HostStartTimeUtcTicks);
            Assert.Equal(expected.LaunchedByGatewayPid, actual.LaunchedByGatewayPid);
            Assert.Equal(expected.WorkerPid, actual.WorkerPid);
            Assert.Equal(expected.WorkerStartTimeUtcTicks, actual.WorkerStartTimeUtcTicks);
            Assert.Equal(expected.PipeName, actual.PipeName);
            Assert.Equal(expected.Generation, actual.Generation);
            Assert.Equal(expected.State, actual.State);
            Assert.Equal(ToUtc(expected.HeldSinceUtc), ToUtc(actual.HeldSinceUtc));
            Assert.Equal(ToUtc(expected.ColdStartStartedUtc), ToUtc(actual.ColdStartStartedUtc));
            Assert.Equal(expected.ColdStartConcurrency, actual.ColdStartConcurrency);
            Assert.Equal(expected.Attachments, actual.Attachments);
            Assert.Equal(expected.InFlight, actual.InFlight);
            Assert.Equal(expected.Queued, actual.Queued);
            Assert.Equal(expected.BackgroundActive, actual.BackgroundActive);
            Assert.Equal(expected.Evictable, actual.Evictable);
            Assert.Equal(ToUtc(expected.LastActivityUtc), ToUtc(actual.LastActivityUtc));
            Assert.Equal(ToUtc(expected.UpdatedUtc), ToUtc(actual.UpdatedUtc));
            Assert.Equal(expected.Refusal is null, actual.Refusal is null);
        }

        private static DateTime? ToUtc(DateTime? value) => value?.ToUniversalTime();

        private sealed class FakeProcessProbe : IProcessProbe
        {
            private readonly Dictionary<int, ProcessProbeResult> _results = new Dictionary<int, ProcessProbeResult>();

            public List<int> ProbedPids { get; } = new List<int>();

            public void Set(int pid, ProcessProbeResult result) => _results[pid] = result;

            public ProcessProbeResult Probe(int pid)
            {
                ProbedPids.Add(pid);
                return _results.TryGetValue(pid, out var result) ? result : ProcessProbeResult.NotRunning;
            }
        }
    }
}
