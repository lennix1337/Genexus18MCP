using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Property-based test for the pure registry v2 liveness classifier.
    // Uses a fake IProcessProbe only; no real process is ever probed.
    public class SharedHostRecordLivenessPropertyTests
    {
        private const int Iterations = 300;

        // A fixed point in 2025 used as the lower bound for generated start times, so that negative
        // probe deltas of up to one day never produce a negative tick value.
        private const long BaseTicks = 638_700_000_000_000_000L;
        private const long OneDayTicks = TimeSpan.TicksPerDay;

        // Independent copy of the design's state values, so the generator does not depend on the code under test.
        private static readonly string[] DesignStates =
        {
            "admitting", "held", "starting", "ready", "replacing",
            "recovering", "no-worker", "refused", "stopping"
        };

        private enum ReadKind { Absent, Corrupted, Parsed }

        private enum ProbeKind { NotRunning, RunningSameMillisecond, RunningNearBoundary, RunningFarAway, StartTimeUnreadable }

        // Feature: concurrent-multi-session-workers, Property 9: Record liveness classification
        // Validates: Requirements 7.1, 7.8
        [Fact]
        public void Classify_MatchesTheLivenessOracle_ForAnyReadResultAndProbeOutcome()
        {
            SeededInterleavingRunner.Run(
                "Feature: concurrent-multi-session-workers, Property 9: Record liveness classification",
                Iterations,
                CheckOneCase);
        }

        private static void CheckOneCase(PbtCase c)
        {
            var probe = new FakeProcessProbe();
            var kind = PickReadKind(c);
            c.Log("read kind: " + kind);

            switch (kind)
            {
                case ReadKind.Absent:
                    CheckAbsent(c, probe);
                    break;
                case ReadKind.Corrupted:
                    CheckCorrupted(c, probe);
                    break;
                default:
                    CheckParsed(c, probe);
                    break;
            }
        }

        // ---------------------------------------------------------------
        // Absent and corrupted records: Stale without probing
        // ---------------------------------------------------------------

        private static void CheckAbsent(PbtCase c, FakeProcessProbe probe)
        {
            // Configure a running process anyway: an absent record must not consult the probe at all.
            probe.Set(c.Int(1, 16_383) * 4, ProcessProbeResult.Running(BaseTicks));

            var result = SharedHostRecordClassifier.Classify(RecordReadResult.Absent, probe);
            LogActual(c, result, probe);

            Assert.Equal(RecordLiveness.Stale, result.Liveness);
            Assert.Equal(StaleReason.OwnerNotRunning, result.Reason);
            Assert.Null(result.Record);
            Assert.Empty(probe.ProbedPids);
        }

        private static void CheckCorrupted(PbtCase c, FakeProcessProbe probe)
        {
            var record = GenerateRecord(c);
            var text = GenerateCorruptedText(c, record);
            c.Log("corrupted text: " + Describe(text));

            probe.Set(record.HostPid, ProcessProbeResult.Running(record.HostStartTimeUtcTicks));

            var read = RecordReadResult.FromJson(text);
            c.Log("read status: " + read.Status);
            Assert.Equal(RecordReadStatus.Unparseable, read.Status);
            Assert.False(string.IsNullOrWhiteSpace(read.Error));

            var result = SharedHostRecordClassifier.Classify(read, probe);
            LogActual(c, result, probe);

            Assert.Equal(RecordLiveness.Stale, result.Liveness);
            Assert.Equal(StaleReason.UnparseableRecord, result.Reason);
            Assert.Empty(probe.ProbedPids);
        }

        // ---------------------------------------------------------------
        // Parsed records: liveness decided by hostPid and hostStartTimeUtcTicks only
        // ---------------------------------------------------------------

        private static void CheckParsed(PbtCase c, FakeProcessProbe probe)
        {
            var record = GenerateRecord(c);
            var probeKind = PickProbeKind(c);
            var hostProbe = GenerateHostProbe(c, probeKind, record.HostStartTimeUtcTicks, out var probedTicks);
            c.Log("host probe: pid=" + Num(record.HostPid) + " kind=" + probeKind +
                  (probedTicks.HasValue
                      ? " ticks=" + Num(probedTicks.Value) + " delta=" + Num(probedTicks.Value - record.HostStartTimeUtcTicks)
                      : string.Empty));
            probe.Set(record.HostPid, hostProbe);

            // Decoy: the Worker process, when recorded, looks perfectly live. A classifier that consulted
            // workerPid or workerStartTimeUtcTicks instead of the host owner would diverge from the oracle.
            if (record.WorkerPid > 0 && record.WorkerPid != record.HostPid)
            {
                var workerDecoy = c.Bool()
                    ? ProcessProbeResult.Running(record.WorkerStartTimeUtcTicks)
                    : ProcessProbeResult.NotRunning;
                c.Log("worker decoy probe: pid=" + Num(record.WorkerPid) + " status=" + workerDecoy.Status);
                probe.Set(record.WorkerPid, workerDecoy);
            }

            var read = RecordReadResult.FromJson(record.ToJson());
            Assert.Equal(RecordReadStatus.Parsed, read.Status);

            var expected = Oracle(probeKind, record.HostStartTimeUtcTicks, probedTicks);
            c.Log("expected: " + expected.Liveness + "/" + expected.Reason);

            var result = SharedHostRecordClassifier.Classify(read, probe);
            LogActual(c, result, probe);

            Assert.Equal(expected.Liveness, result.Liveness);
            Assert.Equal(expected.Reason, result.Reason);
            Assert.Equal(new[] { record.HostPid }, probe.ProbedPids);

            if (expected.Liveness != RecordLiveness.Stale)
            {
                // Live and LivenessUnverified keep the parsed record, with the recorded PIDs and start time unchanged.
                Assert.NotNull(result.Record);
                Assert.Equal(record.HostPid, result.Record!.HostPid);
                Assert.Equal(record.HostStartTimeUtcTicks, result.Record.HostStartTimeUtcTicks);
                Assert.Equal(record.WorkerPid, result.Record.WorkerPid);
                Assert.Equal(record.State, result.Record.State);
            }
        }

        // Independent oracle for Property 9 on a parsed record.
        // - owner not running                                  -> Stale / OwnerNotRunning
        // - owner running, start time unreadable               -> LivenessUnverified / None
        // - owner running, same start time at ms precision     -> Live / None
        // - owner running, different start time at ms precision -> Stale / StartTimeMismatch
        private static (RecordLiveness Liveness, StaleReason Reason) Oracle(ProbeKind kind, long recordedTicks, long? probedTicks)
        {
            switch (kind)
            {
                case ProbeKind.NotRunning:
                    return (RecordLiveness.Stale, StaleReason.OwnerNotRunning);
                case ProbeKind.StartTimeUnreadable:
                    return (RecordLiveness.LivenessUnverified, StaleReason.None);
                default:
                    var recordedMs = TruncateToMillisecond(recordedTicks);
                    var probedMs = TruncateToMillisecond(probedTicks!.Value);
                    return recordedMs == probedMs
                        ? (RecordLiveness.Live, StaleReason.None)
                        : (RecordLiveness.Stale, StaleReason.StartTimeMismatch);
            }
        }

        // Ticks are non-negative here, so subtracting the remainder equals truncation toward zero.
        private static long TruncateToMillisecond(long ticks) => ticks - (ticks % TimeSpan.TicksPerMillisecond);

        // ---------------------------------------------------------------
        // Generators
        // ---------------------------------------------------------------

        private static ReadKind PickReadKind(PbtCase c)
        {
            var roll = c.Int(1, 100);
            if (roll <= 15) return ReadKind.Absent;
            if (roll <= 35) return ReadKind.Corrupted;
            return ReadKind.Parsed;
        }

        private static ProbeKind PickProbeKind(PbtCase c)
        {
            var roll = c.Int(1, 100);
            if (roll <= 18) return ProbeKind.NotRunning;
            if (roll <= 43) return ProbeKind.RunningSameMillisecond;
            if (roll <= 73) return ProbeKind.RunningNearBoundary;
            if (roll <= 85) return ProbeKind.RunningFarAway;
            return ProbeKind.StartTimeUnreadable;
        }

        private static ProcessProbeResult GenerateHostProbe(PbtCase c, ProbeKind kind, long recordedTicks, out long? probedTicks)
        {
            probedTicks = null;
            switch (kind)
            {
                case ProbeKind.NotRunning:
                    return ProcessProbeResult.NotRunning;
                case ProbeKind.StartTimeUnreadable:
                    return ProcessProbeResult.RunningStartTimeUnreadable;
                case ProbeKind.RunningSameMillisecond:
                    // Any tick inside the recorded millisecond, including its first and last tick.
                    var floor = TruncateToMillisecond(recordedTicks);
                    var offset = c.Pick(new[] { 0, (int)TimeSpan.TicksPerMillisecond - 1, c.Int(0, (int)TimeSpan.TicksPerMillisecond - 1) });
                    probedTicks = floor + offset;
                    break;
                case ProbeKind.RunningNearBoundary:
                    // Small deltas around the recorded tick: some stay inside the millisecond, some cross into
                    // the previous or next one. The oracle decides which.
                    var delta = (long)c.Pick(new[] { -1, 1, -10_000, 10_000, -9_999, 9_999, -10_001, 10_001 });
                    if (c.Bool()) delta = c.Int(-20_000, 20_000);
                    probedTicks = recordedTicks + delta;
                    break;
                default:
                    // Large deltas, a stand-in for a reused PID owned by an unrelated process.
                    var far = (long)c.Int(1, 86_400) * TimeSpan.TicksPerSecond + c.Int(0, 9_999);
                    probedTicks = c.Bool() ? recordedTicks + far : recordedTicks - Math.Min(far, OneDayTicks);
                    break;
            }

            return ProcessProbeResult.Running(probedTicks!.Value);
        }

        private static SharedHostRecord GenerateRecord(PbtCase c)
        {
            var state = c.Pick(DesignStates);
            var hostPid = c.Int(1, 16_383) * 4;
            var hostStartTicks = GenerateStartTicks(c);

            // workerPid=0 for a host without a Worker (held, refused, no-worker, or simply not started yet).
            var hasWorker = state != "held" && state != "refused" && state != "no-worker" && c.Chance(0.7);
            var workerPid = hasWorker ? c.Int(1, 16_383) * 4 : 0;
            var workerStartTicks = hasWorker ? hostStartTicks + (long)c.Int(1, 600_000) * TimeSpan.TicksPerMillisecond : 0L;

            var key = "identity-key-" + Num(c.Int(0, 999));
            var updated = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc).AddSeconds(c.Int(0, 86_400));

            var record = new SharedHostRecord
            {
                KbKey = "kb-key-" + Num(c.Int(0, 999)),
                Identity = new SharedHostRecordIdentity
                {
                    Key = key,
                    KbPath = @"C:\KBs\Kb" + Num(c.Int(0, 9)),
                    WorkerExecutable = @"<install-dir>\GxMcp.Worker.exe",
                    InstallationPath = "<gx-install-dir>",
                    Driver = "native-sdk",
                    Major = "18",
                    ProfileConfigPath = @"<profile-dir>\config.json"
                },
                HostPid = hostPid,
                HostStartTimeUtcTicks = hostStartTicks,
                LaunchedByGatewayPid = c.Int(1, 16_383) * 4,
                WorkerPid = workerPid,
                WorkerStartTimeUtcTicks = workerStartTicks,
                PipeName = "GxMcpShared2_" + key,
                Generation = c.SmallInt(0, 20),
                State = state,
                HeldSinceUtc = state == "held" || c.Chance(0.2) ? updated.AddSeconds(-c.Int(0, 600)) : (DateTime?)null,
                ColdStartStartedUtc = state == "starting" || c.Chance(0.2) ? updated.AddSeconds(-c.Int(0, 600)) : (DateTime?)null,
                ColdStartConcurrency = c.SmallInt(1, 4),
                Attachments = c.SmallInt(0, 5),
                InFlight = c.SmallInt(0, 1),
                Queued = c.SmallInt(0, 10),
                BackgroundActive = c.Bool(),
                Evictable = c.Bool(),
                LastActivityUtc = updated,
                Refusal = state == "refused"
                    ? new SharedHostRefusal { Code = "KB_POOL_FULL", Reason = "low-memory", AvailableMb = c.Int(0, 1023), FloorMb = 1024 }
                    : null,
                UpdatedUtc = updated
            };

            c.Log("record: state=" + state + " hostPid=" + Num(hostPid) + " hostStartTicks=" + Num(hostStartTicks) +
                  " workerPid=" + Num(workerPid) + " workerStartTicks=" + Num(workerStartTicks) +
                  " generation=" + Num(record.Generation) + " attachments=" + Num(record.Attachments));
            return record;
        }

        // Start ticks are either aligned to a millisecond, on its last tick, or anywhere inside it.
        private static long GenerateStartTicks(PbtCase c)
        {
            var ms = (long)c.Int(0, 1_000_000_000);
            var sub = c.Pick(new[] { 0, (int)TimeSpan.TicksPerMillisecond - 1, c.Int(0, (int)TimeSpan.TicksPerMillisecond - 1) });
            return BaseTicks + OneDayTicks + ms * TimeSpan.TicksPerMillisecond + sub;
        }

        private static string GenerateCorruptedText(PbtCase c, SharedHostRecord record)
        {
            switch (c.Int(0, 3))
            {
                case 0:
                    // A torn write: a strict prefix of a valid record, which is never a complete JSON object.
                    var json = record.ToJson().TrimEnd();
                    return json.Substring(0, c.Int(0, json.Length - 1));
                case 1:
                    // Valid JSON that is not an object.
                    var count = c.SmallInt(0, 4);
                    return "[" + string.Join(", ", Enumerable.Range(0, count).Select(_ => Num(c.Int(0, 99)))) + "]";
                case 2:
                    return "{ this is not json " + Num(c.Int(0, 999));
                default:
                    return c.Pick(new[] { "", " ", "\r\n", "\t \n" });
            }
        }

        // ---------------------------------------------------------------
        // Logging helpers
        // ---------------------------------------------------------------

        private static void LogActual(PbtCase c, RecordClassification result, FakeProcessProbe probe)
        {
            c.Log("actual: " + result.Liveness + "/" + result.Reason +
                  " record=" + (result.Record == null ? "null" : "pid " + Num(result.Record.HostPid)) +
                  " probed=[" + string.Join(",", probe.ProbedPids.Select(p => Num(p))) + "]");
        }

        private static string Describe(string text)
        {
            var shown = text.Length <= 60 ? text : text.Substring(0, 60) + "...";
            return "length=" + Num(text.Length) + " text=\"" + shown.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

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
