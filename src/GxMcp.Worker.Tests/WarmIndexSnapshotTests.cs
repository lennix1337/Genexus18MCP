using System.Collections.Generic;
using System.Text;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Item 51 (mcp-improvements-2026-05-22, Tier-S, EXPERIMENTAL) — metadata-
    // validation tests for warm reload. The IWarmSnapshotStore seam lets us
    // exercise Save/TryLoad without touching real disk; the DLL-hash gate is
    // verified by stubbing the current SHA at the same value (happy path) and
    // a different value (fallback).
    public class WarmIndexSnapshotTests
    {
        private sealed class InMemoryStore : IWarmSnapshotStore
        {
            public readonly Dictionary<string, (WarmIndexSnapshotMetadata m, byte[] p)> Items
                = new Dictionary<string, (WarmIndexSnapshotMetadata, byte[])>();
            public bool ThrowOnSave = false;

            public void Save(string path, WarmIndexSnapshotMetadata metadata, byte[] payload)
            {
                if (ThrowOnSave) throw new System.IO.IOException("disk-full");
                Items[path] = (metadata, payload);
            }

            public bool TryLoad(string path, out WarmIndexSnapshotMetadata metadata, out byte[] payload)
            {
                if (Items.TryGetValue(path, out var pair))
                {
                    metadata = pair.m;
                    payload = pair.p;
                    return true;
                }
                metadata = null;
                payload = null;
                return false;
            }
        }

        // Force ComputeWorkerDllSha256 to a controllable value by pointing it at
        // a file we just wrote in TempPath. This is the cheapest reliable way to
        // simulate "DLL changed" without rewriting the production helper.
        private static string WriteFakeDll(string contents)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "warmsnap-fake-" + System.Guid.NewGuid().ToString("N") + ".dll");
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        [Fact]
        public void Save_then_TryLoad_roundtrips_with_matching_dll_hash()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string dll = WriteFakeDll("dll-bytes-v1");
                string path = @"C:\fake-kb\.gx\index-snapshot.bin";
                byte[] payload = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

                WarmIndexSnapshot.Save(path, payload, @"C:\fake-kb", objectCount: 42, workerDllPath: dll);

                var result = WarmIndexSnapshot.TryLoad(path, workerDllPath: dll);

                Assert.True(result.Loaded);
                Assert.False(result.Fallback);
                Assert.Null(result.FallbackReason);
                Assert.NotNull(result.Metadata);
                Assert.Equal(@"C:\fake-kb", result.Metadata.KbPath);
                Assert.Equal(42, result.Metadata.ObjectCount);
                Assert.Equal(payload, result.Payload);
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void TryLoad_falls_back_when_worker_dll_hash_mismatches()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string dllAtSave = WriteFakeDll("old-bytes");
                string dllAtLoad = WriteFakeDll("new-bytes-DIFFERENT");
                string path = @"C:\fake-kb\.gx\index-snapshot.bin";
                byte[] payload = Encoding.UTF8.GetBytes("{}");

                WarmIndexSnapshot.Save(path, payload, @"C:\fake-kb", objectCount: 0, workerDllPath: dllAtSave);

                var result = WarmIndexSnapshot.TryLoad(path, workerDllPath: dllAtLoad);

                Assert.False(result.Loaded);
                Assert.True(result.Fallback);
                Assert.Equal("worker-dll-hash-mismatch", result.FallbackReason);
                // Metadata is still surfaced so the agent can diagnose.
                Assert.NotNull(result.Metadata);
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void TryLoad_falls_back_when_snapshot_missing()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string dll = WriteFakeDll("any");
                var result = WarmIndexSnapshot.TryLoad(@"C:\no-such-path\.gx\index-snapshot.bin", workerDllPath: dll);

                Assert.False(result.Loaded);
                Assert.True(result.Fallback);
                Assert.Equal("snapshot-missing-or-unreadable", result.FallbackReason);
                Assert.Null(result.Payload);
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void DefaultPath_returns_kb_relative_snapshot_path()
        {
            string p = WarmIndexSnapshot.DefaultPath(@"C:\KBs\MyKb");
            Assert.NotNull(p);
            Assert.EndsWith(System.IO.Path.Combine(".gx", "index-snapshot.bin"), p);
            Assert.StartsWith(@"C:\KBs\MyKb", p);
        }

        [Fact]
        public void DiskWarmSnapshotStore_SaveRoundTripsThroughTemporaryDirectory()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gx_warm_atomic_" + System.Guid.NewGuid().ToString("N"));
            string path = System.IO.Path.Combine(dir, "index-snapshot.bin");
            try
            {
                var store = new DiskWarmSnapshotStore();
                var metadata = new WarmIndexSnapshotMetadata { WorkerDllSha256 = "hash", KbPath = dir, SchemaVersion = 1 };
                store.Save(path, metadata, Encoding.UTF8.GetBytes("first"));
                store.Save(path, metadata, Encoding.UTF8.GetBytes("second"));

                WarmIndexSnapshotMetadata loadedMetadata;
                byte[] loadedPayload;
                Assert.True(store.TryLoad(path, out loadedMetadata, out loadedPayload));
                Assert.Equal("second", Encoding.UTF8.GetString(loadedPayload));
                Assert.Empty(System.IO.Directory.GetFiles(dir, "*.tmp*", System.IO.SearchOption.AllDirectories));
            }
            finally
            {
                try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void IndexCache_restores_valid_warm_snapshot_and_rebuilds_derived_indexes()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string kbPath = @"C:\KBs\WarmRestore";
                string snapshotPath = WarmIndexSnapshot.DefaultPath(kbPath);
                var source = new SearchIndex
                {
                    LastUpdated = System.DateTime.UtcNow
                };
                source.Objects["Procedure:WarmProc"] = new SearchIndex.IndexEntry
                {
                    Name = "WarmProc",
                    Type = "Procedure",
                    FullSource = "call MissingProc()"
                };
                byte[] payload = Encoding.UTF8.GetBytes(source.ToJson());
                WarmIndexSnapshot.Save(
                    snapshotPath,
                    payload,
                    kbPath,
                    objectCount: 1,
                    schemaVersion: IndexCacheService.CurrentSchemaVersion,
                    highWaterMarkUtc: System.DateTime.UtcNow.ToString("o"));

                var cache = new IndexCacheService();
                var result = cache.TryRestoreWarmSnapshot(kbPath);

                Assert.True(result["loaded"]?.ToObject<bool>());
                Assert.False(result["fallback"]?.ToObject<bool>());
                Assert.Equal(1, result["objectCount"]?.ToObject<int>());
                var restored = cache.TryGetLoadedIndex();
                Assert.NotNull(restored);
                Assert.True(restored.Objects.ContainsKey("Procedure:WarmProc"));
                Assert.NotNull(restored.ChildrenByParent);
                Assert.NotNull(restored.SourceTokenIndex);
                Assert.True(restored.SourceTokenIndex.ContainsKey("missingproc"));
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void IndexCache_rejects_warm_snapshot_with_wrong_schema()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string kbPath = @"C:\KBs\WarmSchemaMismatch";
                string path = WarmIndexSnapshot.DefaultPath(kbPath);
                WarmIndexSnapshot.Save(
                    path,
                    Encoding.UTF8.GetBytes("{}"),
                    kbPath,
                    objectCount: 0,
                    schemaVersion: IndexCacheService.CurrentSchemaVersion + 1);

                var result = new IndexCacheService().TryRestoreWarmSnapshot(kbPath);

                Assert.False(result["loaded"]?.ToObject<bool>() ?? false);
                Assert.True(result["fallback"]?.ToObject<bool>());
                Assert.Equal("schema-mismatch", result["fallbackReason"]?.ToString());
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }
    }
}
