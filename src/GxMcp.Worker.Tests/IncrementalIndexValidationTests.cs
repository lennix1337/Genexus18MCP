using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Fase 1 — persistible incremental index. The delta-on-open decision hinges on
    // ValidateOnDiskCache: a body + matching sidecar (schema version + worker-DLL hash +
    // high-water-mark) → delta-eligible; anything else → full rebuild. These tests drive
    // the round trip through a unique temp KB path (the cache file is hashed off it) and
    // clean up via DeleteOnDiskSnapshot.
    public class IncrementalIndexValidationTests
    {
        private static string UniqueKbPath() =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxmcp-idxtest-" + Guid.NewGuid().ToString("N"));

        private static IEnumerable<SearchIndex.IndexEntry> OneEntry(string name, string type) =>
            new[] { new SearchIndex.IndexEntry { Name = name, Type = type, Guid = Guid.NewGuid().ToString() } };

        [Fact]
        public void Body_plus_matching_sidecar_is_delta_eligible()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                var hwm = new DateTime(2026, 6, 1, 12, 0, 0);
                cache.ReplaceAll(OneEntry("Foo", "Procedure"));
                cache.ObserveLastUpdate(hwm);
                cache.FlushNow();           // writes the body
                cache.WriteMetaSidecar(1);  // writes the trustworthy sidecar

                var v = cache.ValidateOnDiskCache();

                Assert.True(v.BodyPresent);
                Assert.True(v.MetaPresent);
                Assert.True(v.SchemaMatch);
                Assert.True(v.DllMatch);    // same process → same worker-DLL hash
                Assert.Equal(hwm, v.HighWaterMark);
                Assert.True(v.CanDelta);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Body_without_sidecar_forces_full_rebuild()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                cache.ReplaceAll(OneEntry("Bar", "Transaction"));
                cache.FlushNow();  // body only — no sidecar (simulates a crashed mid-enrichment worker)

                var v = cache.ValidateOnDiskCache();

                Assert.True(v.BodyPresent);
                Assert.False(v.MetaPresent);
                Assert.False(v.CanDelta);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Missing_body_is_not_delta_eligible()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            // Nothing persisted yet.
            var v = cache.ValidateOnDiskCache();
            Assert.False(v.BodyPresent);
            Assert.False(v.CanDelta);
        }

        [Fact]
        public void Existing_snapshot_survives_in_memory_clear_for_crash_recovery()
        {
            var path = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(path);
            try
            {
                cache.ReplaceAll(OneEntry("Survivor", "Procedure"));
                Assert.True(cache.FlushNow());
                cache.Clear();

                var recovered = new IndexCacheService();
                recovered.Initialize(path);
                try { Assert.True(recovered.GetIndex().Objects.ContainsKey("Procedure:Survivor")); }
                finally { recovered.DeleteOnDiskSnapshot(); }
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void DeleteOnDiskSnapshot_resets_high_water_mark()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                cache.ObserveLastUpdate(new DateTime(2026, 6, 1));
                Assert.NotEqual(DateTime.MinValue, cache.CurrentHighWaterMark);
            }
            finally { cache.DeleteOnDiskSnapshot(); }

            Assert.Equal(DateTime.MinValue, cache.CurrentHighWaterMark);
        }

        [Fact]
        public void GetUnenrichedEntries_returns_only_entries_without_embedding()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry { Name = "LiteStub", Type = "Procedure", Guid = Guid.NewGuid().ToString() }, // no embedding
                    new SearchIndex.IndexEntry { Name = "Enriched", Type = "Transaction", Guid = Guid.NewGuid().ToString(), Embedding = new float[128] }
                });

                var pending = cache.GetUnenrichedEntries();

                Assert.Single(pending);
                Assert.Equal("LiteStub", pending[0].Name);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        // Post-upgrade write-starvation fix — CanDeltaAcrossDll relaxes CanDelta by dropping the
        // DllMatch requirement (but keeps SchemaMatch, so the index LAYOUT must still match). This
        // is what lets a worker-DLL change after an upgrade take the bounded delta + sidecar
        // re-baseline path instead of a full 38k re-walk that blocks writes.
        [Fact]
        public void CanDeltaAcrossDll_true_when_only_worker_dll_mismatches()
        {
            var v = new IndexCacheService.OnDiskCacheValidation
            {
                BodyPresent = true,
                MetaPresent = true,
                SchemaMatch = true,
                DllMatch = false, // the post-upgrade case
                HighWaterMark = new DateTime(2026, 6, 1)
            };

            Assert.False(v.CanDelta);          // full predicate rejects (DLL changed)
            Assert.True(v.CanDeltaAcrossDll);  // relaxed predicate accepts (layout unchanged)
        }

        [Fact]
        public void CanDeltaAcrossDll_false_when_schema_layout_changed()
        {
            // A schema (index-layout) change is the hard boundary: the on-disk body is no longer
            // structurally readable, so even the relaxed predicate must force a full rebuild.
            var v = new IndexCacheService.OnDiskCacheValidation
            {
                BodyPresent = true,
                MetaPresent = true,
                SchemaMatch = false,
                DllMatch = false,
                HighWaterMark = new DateTime(2026, 6, 1)
            };

            Assert.False(v.CanDelta);
            Assert.False(v.CanDeltaAcrossDll);
        }

        [Fact]
        public void CanDeltaAcrossDll_false_without_baseline_high_water_mark()
        {
            // No HWM → no delta baseline to walk from → must full-rebuild even if schema matches.
            var v = new IndexCacheService.OnDiskCacheValidation
            {
                BodyPresent = true,
                MetaPresent = true,
                SchemaMatch = true,
                DllMatch = false,
                HighWaterMark = DateTime.MinValue
            };

            Assert.False(v.CanDeltaAcrossDll);
        }

        [Fact]
        public void CanDeltaAcrossDll_false_when_body_or_sidecar_missing()
        {
            var noBody = new IndexCacheService.OnDiskCacheValidation
            {
                BodyPresent = false,
                MetaPresent = true,
                SchemaMatch = true,
                HighWaterMark = new DateTime(2026, 6, 1)
            };
            var noMeta = new IndexCacheService.OnDiskCacheValidation
            {
                BodyPresent = true,
                MetaPresent = false,
                SchemaMatch = true,
                HighWaterMark = new DateTime(2026, 6, 1)
            };

            Assert.False(noBody.CanDeltaAcrossDll);
            Assert.False(noMeta.CanDeltaAcrossDll);
        }

        [Fact]
        public void ObserveLastUpdate_only_advances_forward()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                var newer = new DateTime(2026, 6, 1);
                cache.ObserveLastUpdate(newer);
                cache.ObserveLastUpdate(new DateTime(2025, 1, 1)); // older — must not regress
                Assert.Equal(newer, cache.CurrentHighWaterMark);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }
    }
}
