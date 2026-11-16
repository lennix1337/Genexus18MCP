using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // CrashLedger redirects a PROCESS-GLOBAL static path for its tests. Any worker-exit
    // test running in parallel would route its own CrashLedger.Record into our temp file
    // and corrupt the counts, so this collection is serialized against the whole assembly.
    [CollectionDefinition("CrashLedgerSerial", DisableParallelization = true)]
    public sealed class CrashLedgerSerialCollection { }

    // The ledger is the measurement foundation for worker-death work: worker_debug.log
    // rotates on every spawn, so without a durable store death causes can't be counted.
    // These pin the classification (which exits are "real" deaths) and the round-trip.
    [Collection("CrashLedgerSerial")]
    public class CrashLedgerTests : IDisposable
    {
        private readonly string _tmp;

        public CrashLedgerTests()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "gxmcp-crashledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
            CrashLedger.SetPathForTest(_tmp);
        }

        public void Dispose()
        {
            CrashLedger.SetPathForTest(null);
            try { if (File.Exists(_tmp)) File.Delete(_tmp); } catch { }
        }

        [Theory]
        [InlineData(WorkerStopReason.IdleTimeout, 0, false)]
        [InlineData(WorkerStopReason.GatewayShutdown, 0, false)]
        [InlineData(WorkerStopReason.ExplicitClose, 0, false)]
        [InlineData(WorkerStopReason.PlannedReload, 0, false)]
        [InlineData(WorkerStopReason.BusyReject, 17, false)]
        [InlineData(WorkerStopReason.None, 0, false)]   // clean stdin-EOF shutdown
        [InlineData(WorkerStopReason.None, -1, true)]    // crash: unobserved, nonzero exit
        [InlineData(WorkerStopReason.None, 139, true)]   // crash: native fault exit code
        [InlineData(WorkerStopReason.Wedged, null, true)] // force-reaped hung worker
        public void IsUnexpected_ClassifiesDeaths(WorkerStopReason reason, int? exitCode, bool expected)
        {
            Assert.Equal(expected, CrashLedger.IsUnexpected(reason, exitCode));
        }

        [Fact]
        public void Record_ThenSummarize_RoundTripsCountsAndReasons()
        {
            CrashLedger.Record("kbA", WorkerStopReason.IdleTimeout, 0, 100, 30.0, 200L * 1024 * 1024, "genexus_read", 45, 3, true);
            CrashLedger.Record("kbA", WorkerStopReason.None, 139, 101, 12.5, 1600L * 1024 * 1024, "genexus_search_source", 45, 3, true);
            CrashLedger.Record("kbA", WorkerStopReason.Wedged, null, 102, 900.0, 800L * 1024 * 1024, "genexus_lifecycle", 45, 3, true);

            var s = CrashLedger.Summarize(recentN: 5);

            Assert.Equal(3, s["total"]!.ToObject<int>());
            Assert.Equal(2, s["unexpected"]!.ToObject<int>());   // None/139 + Wedged
            Assert.Equal(1, s["byReason"]!["IdleTimeout"]!.ToObject<int>());
            Assert.Equal(1, s["byReason"]!["None"]!.ToObject<int>());
            Assert.Equal(1, s["byReason"]!["Wedged"]!.ToObject<int>());
            // memMB is recorded in whole MB for the crash record.
            var recent = (Newtonsoft.Json.Linq.JArray)s["recent"]!;
            Assert.Equal(3, recent.Count);
            // recent is newest-first.
            Assert.Equal("Wedged", recent[0]!["reason"]!.ToString());
        }

        [Fact]
        public void Record_KeepsTheStopDetail_SoAnIdleStopCanBeJudgedFromTheLedgerAlone()
        {
            const string detail = "idleSec=1800 idleTimeoutSec=1800 buildActiveAgeSec=none indexActiveAgeSec=none graceSec=90 queued=0 inFlight=0";
            CrashLedger.Record("kbA", WorkerStopReason.IdleTimeout, -1, 100, 320.3, 147L * 1024 * 1024, "Build (ID: x)", 45, 3, true, detail: detail);
            CrashLedger.Record("kbA", WorkerStopReason.None, 139, 101, 1.0, 1024, "op", 1, 1, true);

            var recent = (Newtonsoft.Json.Linq.JArray)CrashLedger.Summarize(recentN: 5)["recent"]!;

            Assert.Equal(2, recent.Count);
            Assert.Null(recent[0]!["detail"]);
            Assert.Equal(detail, recent[1]!["detail"]!.ToString());
        }

        [Fact]
        public void Record_RingCaps_KeepsMostRecent()
        {
            int n = CrashLedger.MaxRecords + 150;
            for (int i = 0; i < n; i++)
                CrashLedger.Record("kbA", WorkerStopReason.None, i, i, 1.0, 1024, "op" + i, 1, 1, true);

            var s = CrashLedger.Summarize(recentN: 1);
            int total = s["total"]!.ToObject<int>();
            Assert.True(total <= CrashLedger.MaxRecords + 100, $"ledger grew unbounded: {total}");
            Assert.True(total >= CrashLedger.MaxRecords, $"ledger over-trimmed: {total}");

            // The most recent op survived the trim.
            var recent = (Newtonsoft.Json.Linq.JArray)s["recent"]!;
            Assert.Equal("op" + (n - 1), recent[0]!["lastOp"]!.ToString());
        }

        [Fact]
        public void Summarize_EmptyLedger_ReturnsZeroTotals()
        {
            var s = CrashLedger.Summarize();
            Assert.Equal(0, s["total"]!.ToObject<int>());
            Assert.Equal(0, s["unexpected"]!.ToObject<int>());
        }
    }
}
