using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 003 — sharded on-disk index flush. IndexFlushBoundTests (plan 001) pins the
    // *count* of real writes under a throttled burst; these tests pin the *scope* of a
    // write — a mutation confined to one shard must not force every shard to be
    // re-serialized — plus the two invariants a sharding rewrite must never break:
    // warm-start round-trip fidelity and loading a pre-existing (pre-sharding) snapshot.
    public class IndexShardingTests
    {
        private static string UniqueKbPath() =>
            Path.Combine(Path.GetTempPath(), "gxmcp-shardtest-" + Guid.NewGuid().ToString("N"));

        private static SearchIndex.IndexEntry Entry(string type, string name) =>
            new SearchIndex.IndexEntry { Name = name, Type = type, Guid = Guid.NewGuid().ToString() };

        [Fact]
        public void ReplaceAll_PreservesConcurrentLiteWalkMutation_AndHonorsRemoval()
        {
            var cache = new IndexCacheService();
            var original = Entry("Procedure", "Original");
            cache.ReplaceAll(new[] { original });
            cache.BeginLiteWalk();
            var added = Entry("Procedure", "Added");
            cache.AddOrUpdateBatch(new[] { added });
            cache.RemoveEntryByGuid(original.Guid);
            cache.ReplaceAll(new[] { original });

            var index = cache.GetIndex();
            Assert.True(index.Objects.ContainsKey("Procedure:Added"));
            Assert.False(index.Objects.ContainsKey("Procedure:Original"));
        }

        [Fact]
        public void ShardedLoad_RejectsPostFlushShardMixUsingManifestHash()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(new[] { Entry("Procedure", "HashProbe") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                int shard = IndexCacheService.ShardOf("Procedure:HashProbe");
                File.AppendAllText(cache.ShardFilePathForTest(shard), "crash-mix");
                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                Assert.ThrowsAny<Exception>(() => reloaded.GetIndex());
                Assert.Null(reloaded.TryGetLoadedIndex());
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void VersionedSnapshot_PublishesCertifiedPointerOnlyAfterCompleteSlot()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(new[] { Entry("Procedure", "Certified") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                string firstPointer = File.ReadAllText(cache.SnapshotPointerPathForTest);
                string firstSlot = cache.CertifiedSlotPathForTest;
                Assert.True(Directory.Exists(firstSlot));

                cache.ReplaceAll(new[] { Entry("Procedure", "Rebuilt") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                Assert.NotEqual(firstPointer, File.ReadAllText(cache.SnapshotPointerPathForTest));
                Assert.NotEqual(firstSlot, cache.CertifiedSlotPathForTest);
                Assert.True(File.Exists(Path.Combine(cache.CertifiedSlotPathForTest, "manifest.json")));
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void VersionedSnapshot_IgnoresAbandonedRebuildSlotAndLoadsCertifiedGeneration()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(new[] { Entry("Procedure", "KeepCertified") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                string certified = cache.CertifiedSlotPathForTest;
                string abandoned = Path.Combine(cache.SnapshotSlotsPathForTest, "generation-abandoned");
                Directory.CreateDirectory(abandoned);
                File.WriteAllText(Path.Combine(abandoned, "manifest.json"), "{\"incomplete\":true}");

                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                var index = reloaded.GetIndex();
                Assert.True(index.Objects.ContainsKey("Procedure:KeepCertified"));
                Assert.Equal(certified, reloaded.CertifiedSlotPathForTest);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Flush_OnlyRewritesShardsDirtiedSinceLastFlush()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath(), proactiveLoad: false);
            try
            {
                cache.SetFlushThrottleForTest(0);

                // Find two storage keys that land in different shards so the test
                // actually exercises shard isolation rather than colliding by luck.
                var a = Entry("Procedure", "AlphaProc");
                var b = Entry("Procedure", "BetaProc");
                int shardA = IndexCacheService.ShardOf("Procedure:AlphaProc");
                int shardB = IndexCacheService.ShardOf("Procedure:BetaProc");
                Assert.NotEqual(shardA, shardB); // fixture sanity check, not the assertion under test

                cache.ReplaceAll(new[] { a, b });
                cache.FlushNow();
                cache.ResetShardWriteCountsForTest();

                // Mutate only the "Alpha" entry.
                cache.AddOrUpdateBatch(new[] { new SearchIndex.IndexEntry { Name = "AlphaProc", Type = "Procedure", Guid = a.Guid, Description = "changed" } });
                cache.FlushNow();

                Assert.Equal(1, cache.ShardWriteCountForTest(shardA));
                Assert.Equal(0, cache.ShardWriteCountForTest(shardB));
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Flush_BurstOfUpdatesToSingleKey_StaysBoundedAndTouchesOneShard()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath(), proactiveLoad: false);
            try
            {
                // Clear the "every shard is dirty" state a brand-new instance starts in
                // (nothing confirmed on disk yet) before measuring the burst below —
                // otherwise the baseline flush itself would touch all 16 shards. FlushNow()
                // is a no-op while _index is still null, so force it to exist first.
                cache.GetIndex();
                cache.SetFlushThrottleForTest(0);
                cache.FlushNow();

                cache.SetFlushThrottleForTest(3600);
                cache.ResetFlushWriteCountForTest();
                cache.ResetShardWriteCountsForTest();

                int shard = IndexCacheService.ShardOf("Procedure:BurstProc");
                for (int i = 0; i < 200; i++)
                {
                    cache.AddOrUpdateBatch(new[] { new SearchIndex.IndexEntry { Name = "BurstProc", Type = "Procedure", Guid = "g", Description = "v" + i } });
                    cache.ScheduleThrottledFlush();
                }

                Assert.True(cache.FlushWriteCountForTest <= 2,
                    $"Expected coalesced flush writes (<=2), got {cache.FlushWriteCountForTest}");

                cache.FlushNow();
                // Every other shard must still show zero writes across the whole burst —
                // proves the coalesced write(s) only ever touched the one dirty shard.
                for (int id = 0; id < IndexCacheService.ShardCount; id++)
                {
                    if (id == shard) continue;
                    Assert.Equal(0, cache.ShardWriteCountForTest(id));
                }
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void WarmStart_RoundTrip_LoadsIdenticalIndexAfterShardedFlush()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                var entries = new[] { Entry("Procedure", "One"), Entry("Transaction", "Two"), Entry("WebPanel", "Three") };
                cache.ReplaceAll(entries);
                cache.FlushNow();
                Assert.True(cache.IsFullyFlushed);

                // Fresh instance, same KB path -> same on-disk cache location.
                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                var idx = reloaded.GetIndex();

                Assert.Equal(entries.Length, idx.Objects.Count);
                foreach (var e in entries)
                {
                    string key = $"{e.Type}:{e.Name}";
                    Assert.True(idx.Objects.ContainsKey(key), $"missing {key} after round-trip");
                    Assert.Equal(e.Guid, idx.Objects[key].Guid);
                }
            }
            finally
            {
                cache.DeleteOnDiskSnapshot();
            }
        }

        [Fact]
        public void LegacySingleFileSnapshot_StillLoads_ThenMigratesToShardsOnNextFlush()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                // Simulate an on-disk snapshot written by the pre-sharding code: a single
                // gzipped JSON blob at _indexPathGz, no shard directory/manifest at all.
                var legacy = new SearchIndex();
                legacy.Objects["Procedure:Legacy1"] = new SearchIndex.IndexEntry { Name = "Legacy1", Type = "Procedure", Guid = "legacy-guid-1" };
                string json = legacy.ToJson();
                string gzPath = cache.IndexPathGzForTest;
                Directory.CreateDirectory(Path.GetDirectoryName(gzPath));
                using (var fs = File.Create(gzPath))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                }

                // No shard manifest exists yet -> GetIndex() must fall back to the legacy body.
                Assert.False(File.Exists(cache.ShardManifestPathForTest));
                var idx = cache.GetIndex();
                Assert.True(idx.Objects.ContainsKey("Procedure:Legacy1"));
                Assert.Equal("legacy-guid-1", idx.Objects["Procedure:Legacy1"].Guid);

                // Any subsequent flush must silently migrate: sharded manifest appears,
                // legacy single-file body is cleaned up.
                cache.SetFlushThrottleForTest(0);
                cache.MarkDirty();
                cache.FlushNow();

                Assert.True(File.Exists(cache.ShardManifestPathForTest), "expected shard manifest after migration flush");
                Assert.False(File.Exists(gzPath), "expected legacy snapshot file removed after migration flush");

                // And a fresh instance now loads from the sharded layout and still sees the
                // migrated entry.
                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                var idx2 = reloaded.GetIndex();
                Assert.True(idx2.Objects.ContainsKey("Procedure:Legacy1"));
                Assert.Equal("legacy-guid-1", idx2.Objects["Procedure:Legacy1"].Guid);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void ShardedLoad_RejectsInvalidManifestAndCanDeltaIsFalse()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(new[] { Entry("Procedure", "ManifestProbe") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                cache.ObserveLastUpdate(DateTime.UtcNow);
                cache.WriteMetaSidecar(1);
                File.WriteAllText(cache.ShardManifestPathForTest, "{\"schemaVersion\":999}");

                var validation = cache.ValidateOnDiskCache();
                Assert.False(validation.CanDelta);
                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                Assert.ThrowsAny<Exception>(() => reloaded.GetIndex());
                Assert.Null(reloaded.TryGetLoadedIndex());
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void ShardedLoad_RejectsWrongShardAndDoesNotPublishPartialIndex()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(new[] { Entry("Procedure", "IntegrityProbe") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                int expected = IndexCacheService.ShardOf("Procedure:IntegrityProbe");
                int wrong = (expected + 1) % IndexCacheService.ShardCount;
                File.Copy(cache.ShardFilePathForTest(expected), cache.ShardFilePathForTest(wrong), true);
                File.Delete(cache.ShardFilePathForTest(expected));

                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                Assert.ThrowsAny<Exception>(() => reloaded.GetIndex());
                Assert.Null(reloaded.TryGetLoadedIndex());
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        // ── C3 regression: MarkDirty must mark shards BEFORE bumping the generation ──
        // FlushToDisk captures _dirtyGeneration, then pops+writes dirty shards, then
        // publishes _flushedGeneration = captured gen on success. If MarkDirtyForKey
        // incremented the generation BEFORE marking the shard dirty, a concurrent flush
        // could capture the new generation without the shard in _dirtyShards and certify
        // a mutation whose bytes never reached disk (stale-index-forever on cold start).
        // Fixed order (shard first, Interlocked.Increment last): any flush observing
        // generation N is guaranteed by the Interlocked fence to see every shard marked
        // by mutations <= N, so a certified generation always implies durable bytes.

        [Fact]
        public void MarkDirtyForKey_BumpsGeneration_AndLeavesIndexUnflushedBeforeAnyFlush()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath(), proactiveLoad: false);
            try
            {
                // Deterministic half of the fix: after a fully-confirmed flush, a single
                // MarkDirtyForKey raises the generation by exactly one and flips
                // IsFullyFlushed back to false — the shard went dirty together with the
                // generation, before any subsequent flush could run.
                cache.GetIndex();               // FlushNow no-ops while _index is null
                cache.SetFlushThrottleForTest(0);
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                Assert.True(cache.IsFullyFlushed);

                long before = cache.DirtyGeneration;
                cache.MarkDirtyForKey("Procedure:ProbeProc");

                Assert.Equal(before + 1, cache.DirtyGeneration);
                Assert.False(cache.IsFullyFlushed);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public async System.Threading.Tasks.Task FlushNow_ConcurrentWithRepeatedMarkDirtyForKey_CertifiedGenerationIsAlwaysDurable()
        {
            const int Rounds = 200;
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.GetIndex();
                cache.SetFlushThrottleForTest(0);
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error"); // baseline

                // Background flusher keeps racing the mutations below, widening the
                // generation-capture window the old Increment-then-mark order could
                // lose a mutation in.
                int stop = 0;
                var flusher = Task.Run(() =>
                {
                    while (System.Threading.Volatile.Read(ref stop) == 0)
                        cache.FlushNow(50); // best-effort; may return false while busy
                });

                try
                {
                    for (int i = 1; i <= Rounds; i++)
                    {
                        long before = cache.DirtyGeneration;
                        cache.AddOrUpdateBatch(new[] { new SearchIndex.IndexEntry { Name = "RaceProc", Type = "Procedure", Guid = "race-guid", Description = "v" + i } });
                        Assert.True(cache.DirtyGeneration > before);
                        Assert.True(cache.FlushNow(), $"FlushNow failed to confirm at round {i}");
                        Assert.True(cache.IsFullyFlushed);

                        // The discriminating check: whatever generation FlushNow just
                        // certified MUST contain this round's bytes on disk.
                        var probe = new IndexCacheService();
                        probe.Initialize(kbPath, proactiveLoad: false);
                        var idx = probe.GetIndex();
                        Assert.True(idx.Objects.TryGetValue("Procedure:RaceProc", out var e), $"entry missing from disk at round {i}");
                        Assert.Equal("v" + i, e.Description);
                    }
                }
                finally
                {
                    System.Threading.Volatile.Write(ref stop, 1);
                    await flusher;
                }
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }
    }
}
