using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The journal file is fixed per installed exe, so several Gateway processes
    /// write the same one. These cases use two <see cref="MutationOperationJournal"/>
    /// instances over one path - the shape a single-instance suite cannot see,
    /// because it never has two writers over one file.
    /// </summary>
    public sealed class MutationOperationJournalCrossProcessTests
    {
        private const string Kb = "C:\\KB\\Orders";
        private const string Tool = "genexus_edit";

        [Fact]
        public void AStartedFenceWrittenByOneInstanceIsVisibleToAnother()
        {
            WithJournal("visibility", out string root, out string path);
            try
            {
                // Both instances are constructed before either writes, so the
                // second one's in-memory snapshot is provably empty: seeing the
                // record afterwards can only come from re-reading under the lease.
                var first = new MutationOperationJournal(path);
                var second = new MutationOperationJournal(path);

                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    first.Begin(Kb, Tool, "op-key", "payload-hash"));
                Assert.Equal(MutationOperationJournal.BeginResult.UnknownAfterRestart,
                    second.Begin(Kb, Tool, "op-key", "payload-hash"));
                Assert.Equal(MutationOperationJournal.BeginResult.Conflict,
                    second.Begin(Kb, Tool, "op-key", "different-hash"));

                // The refusals must not have rewritten the record the first
                // instance left behind.
                Assert.Equal("started", ReadEntry(path, "op-key")["Status"]?.ToString());
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void AConcurrentWriterCannotEraseAnotherInstancesStartedRecord()
        {
            WithJournal("interleave", out string root, out string path);
            try
            {
                var first = new MutationOperationJournal(path);
                var second = new MutationOperationJournal(path);

                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    first.Begin(Kb, Tool, "op-key-a", "hash-a"));
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    second.Begin(Kb, Tool, "op-key-b", "hash-b"));
                first.Complete(Kb, Tool, "op-key-a", "hash-a");

                // Assert on the durable bytes, not on a return value: a writer
                // that serialises its own stale entry set is the whole defect.
                Assert.Equal("completed", ReadEntry(path, "op-key-a")["Status"]?.ToString());
                Assert.Equal("started", ReadEntry(path, "op-key-b")["Status"]?.ToString());
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void AnUnheldLeaseFailsClosedRatherThanProceeding()
        {
            WithJournal("lease", out string root, out string path);
            try
            {
                // A clock that advances by the whole cap per read, so a refused
                // acquisition is observed without waiting out the real budget.
                var journal = new MutationOperationJournal(path, new ImmediateExpiringClock());

                // Two holds, because they pin different halves of the lease. An
                // exclusive hold is what a peer Gateway actually leaves behind,
                // but an exclusive holder alone would block a weakened journal
                // open too, so it cannot tell FileShare.None from any other share
                // mode. A permissive holder shares every bit of access with the
                // incoming open, so the journal's own FileShare.None is the only
                // thing that can refuse Begin: if that share mode were weakened
                // the lease would stop excluding other processes and this case
                // would go green with a lock that coordinates nothing.
                foreach (FileShare hold in new[] { FileShare.None, FileShare.ReadWrite })
                {
                    using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, hold))
                    {
                        Assert.Equal(MutationOperationJournal.BeginResult.JournalUnavailable,
                            journal.Begin(Kb, Tool, "op-key", "payload-hash"));
                    }
                    Assert.False(File.Exists(path));
                }

                // Refusing the write must not poison the instance: once the peer
                // releases, the same key starts normally.
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    journal.Begin(Kb, Tool, "op-key", "payload-hash"));
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void ContentionAtConstructionIsReportedBusyAndThenRecovers()
        {
            WithJournal("busy-construct", out string root, out string path);
            try
            {
                Directory.CreateDirectory(root);
                MutationOperationJournal journal;
                // Constructed while a peer holds the lease, so the very first
                // durable read is refused. Busy is not corruption: the peer will
                // release, and the instance must recover rather than stay
                // permanently unavailable the way a rejected file does.
                using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    journal = new MutationOperationJournal(path, new ImmediateExpiringClock());
                    Assert.False(journal.IsHealthy);
                    Assert.Equal("operation_journal_unavailable",
                        journal.Inspect(Kb, Tool, "op-key")["code"]?.ToString());
                    Assert.Equal(MutationOperationJournal.BeginResult.JournalUnavailable,
                        journal.Begin(Kb, Tool, "op-key", "payload-hash"));
                }

                // Same instance, peer gone: the next acquisition takes the lease,
                // clears busy, and loads the file it never got to read. The flag
                // is cleared by acquiring, not by asking, so the write is the
                // observation - a latch here would refuse forever.
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    journal.Begin(Kb, Tool, "op-key", "payload-hash"));
                Assert.True(journal.IsHealthy);
                Assert.Equal("started", ReadEntry(path, "op-key")["Status"]?.ToString());
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void ACorruptJournalDiscoveredByTheReloadStillFailsClosed()
        {
            WithJournal("corrupt-reload", out string root, out string path);
            try
            {
                // Constructed healthy against an absent file, then the file is
                // corrupted underneath. The reload inside Begin's lease is what
                // now has to catch this, so the pre-existing refusal is the thing
                // under test rather than the constructor's.
                var journal = new MutationOperationJournal(path);
                Assert.True(journal.IsHealthy);

                const string truncated = "{\"schemaVersion\":\"genexus-mutation-operations/1\",\"entries\":[";
                File.WriteAllText(path, truncated);

                Assert.Equal(MutationOperationJournal.BeginResult.JournalUnavailable,
                    journal.Begin(Kb, Tool, "op-key", "payload-hash"));
                Assert.False(journal.IsHealthy);
                Assert.Equal(truncated, File.ReadAllText(path));
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void InspectReloadsPeerStartedCompletedAndRemovedRecords()
        {
            WithJournal("inspect-peer", out string root, out string path);
            try
            {
                var writer = new MutationOperationJournal(path);
                var reader = new MutationOperationJournal(path);
                Assert.Equal(MutationOperationJournal.BeginResult.Started, writer.Begin(Kb, Tool, "key", "hash"));
                Assert.Equal("started", reader.Inspect(Kb, Tool, "key")["status"]?.ToString());
                writer.Complete(Kb, Tool, "key", "hash");
                Assert.Equal("completed", reader.Inspect(Kb, Tool, "key")["status"]?.ToString());
                writer.Fail(Kb, Tool, "key", "hash");
                Assert.Equal("not_found", reader.Inspect(Kb, Tool, "key")["status"]?.ToString());
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void InspectFailsClosedIfPeerCorruptsTheJournalAfterConstruction()
        {
            WithJournal("inspect-corrupt", out string root, out string path);
            try
            {
                var journal = new MutationOperationJournal(path);
                File.WriteAllText(path, "not-json");
                var result = journal.Inspect(Kb, Tool, "key");
                Assert.False(result["journalHealthy"]!.Value<bool>());
                Assert.Equal("operation_journal_unavailable", result["code"]?.ToString());
                Assert.True(result["recoveryRequired"]!.Value<bool>());
                Assert.Equal("not-json", File.ReadAllText(path));
            }
            finally { TryDelete(root); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ContentionAfterConstructionIsVisibleAndInspectRecovers(bool attemptBegin)
        {
            WithJournal("inspect-held", out string root, out string path);
            try
            {
                var journal = new MutationOperationJournal(path, new ImmediateExpiringClock());
                using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (attemptBegin)
                    {
                        Assert.Equal(MutationOperationJournal.BeginResult.JournalUnavailable, journal.Begin(Kb, Tool, "key", "hash"));
                        Assert.False(journal.IsHealthy);
                    }
                    var result = journal.Inspect(Kb, Tool, "key");
                    Assert.False(result["journalHealthy"]!.Value<bool>());
                    Assert.Equal("operation_journal_unavailable", result["code"]?.ToString());
                }
                var recovered = journal.Inspect(Kb, Tool, "key");
                Assert.True(recovered["journalHealthy"]!.Value<bool>());
                Assert.Equal("not_found", recovered["status"]?.ToString());
            }
            finally { TryDelete(root); }
        }

        private static JObject ReadEntry(string path, string key)
        {
            JObject document = JObject.Parse(File.ReadAllText(path));
            var entries = Assert.IsType<JArray>(document["entries"]);
            string id = MutationOperationJournal.RecordId(Kb, Tool, key);
            return Assert.IsType<JObject>(Assert.Single(entries, item => item["Id"]?.ToString() == id));
        }

        private static void WithJournal(string name, out string root, out string path)
        {
            root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-xproc-" + name + "-" + Guid.NewGuid().ToString("N"));
            path = Path.Combine(root, "operations.json");
        }

        private static void TryDelete(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }

        /// <summary>
        /// Advances by the whole wait cap per read, so a contended acquisition
        /// gives up after one retry instead of waiting out the real budget. It
        /// must advance: a clock returning a constant would leave the elapsed
        /// comparison at zero and spin until the test host was killed.
        /// </summary>
        private sealed class ImmediateExpiringClock : IMonotonicClock
        {
            private int _reads;

            public TimeSpan Now => TimeSpan.FromTicks(MutationOperationJournal.LeaseWaitCap.Ticks * _reads++);
        }
    }
}
