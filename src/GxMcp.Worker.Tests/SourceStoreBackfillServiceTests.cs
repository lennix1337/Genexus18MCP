using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceStoreBackfillServiceTests
    {
        [Fact]
        public void SlicePersistsProgressAndResumesFromCursor()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entries = new[]
                {
                    Entry("00000000-0000-0000-0000-000000000001", 20),
                    Entry("00000000-0000-0000-0000-000000000002", 10)
                };
                var service = new SourceStoreBackfillService(store);
                bool started = service.Start(
                    () => entries,
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source-" + e.Guid } },
                    maxObjectsPerSlice: 1,
                    sliceBudgetMs: 1000,
                    schedule: false);
                Assert.True(started);

                Assert.True(service.RunOneSliceForTest(1, 1000));
                var afterFirst = service.GetState();
                Assert.Equal("running", afterFirst.State);
                Assert.Equal(1, afterFirst.CursorIndex);
                Assert.Equal(1, afterFirst.StoredObjects);

                Assert.True(service.RunOneSliceForTest(1, 1000));
                var afterSecond = service.GetState();
                Assert.Equal("complete", afterSecond.State);
                Assert.Equal(2, afterSecond.StoredObjects);
                Assert.True(File.Exists(Path.Combine(root, "backfill-state.json")));

                var resumed = new SourceStoreBackfillService(store);
                Assert.Equal(2, resumed.GetState().ProcessedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void CatalogueIdentityChangeResetsTheResumableCursor()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var first = Entry("00000000-0000-0000-0000-000000000011", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(() => new[] { first }, e => Array.Empty<SourceStoreBackfillPart>(), schedule: false);
                service.RunOneSliceForTest(1, 1000);
                Assert.Equal(1, service.GetState().CursorIndex);

                var inserted = Entry("00000000-0000-0000-0000-000000000012", 2);
                service.Start(
                    () => new[] { inserted, first },
                    e => Array.Empty<SourceStoreBackfillPart>(),
                    schedule: false);

                Assert.Equal(0, service.GetState().CursorIndex);
                Assert.Equal("queued", service.GetState().State);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void ReaderFailureDoesNotAdvanceCursorOrCertifyCompleteCoverage()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-error-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entries = new[]
                {
                    Entry("00000000-0000-0000-0000-000000000004", 1),
                    Entry("00000000-0000-0000-0000-000000000005", 2)
                };
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => entries,
                    e => throw new InvalidOperationException("SDK read failed"),
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false);

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Contains("SDK read failed", state.LastError);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void NullSourceDoesNotAdvanceCursorOrCertifyComplete()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-null-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000006", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart
                        {
                            PartName = "Source",
                            Source = null
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false);

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Equal(0, state.StoredObjects);
                Assert.Null(state.CompletedAtUtc);
                Assert.Contains("null", state.LastError, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void MissingRequiredPartEvidenceDoesNotAdvanceOrCertify()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-missing-part-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000010", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart { PartName = "Source", Source = "source" }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source", "Rules" });

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Null(state.CompletedAtUtc);
                Assert.Contains("Rules", state.LastError);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ExplicitPartReadFailureDoesNotPartiallyAdvanceTheObject()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-read-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000011", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart { PartName = "Source", Source = "source" },
                        new SourceStoreBackfillPart
                        {
                            PartName = "Rules",
                            Source = null,
                            ReadSucceeded = false,
                            ReadError = "SDK reader failed"
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source", "Rules" });

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Equal(0, store.StoredRecordCount);
                Assert.Contains("Rules", state.LastError);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void EmptyPartReadIsPersistedAsSuccessfulEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-empty-part-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000012", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart
                        {
                            PartName = "Source",
                            Source = string.Empty,
                            ReadSucceeded = true
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source" });

                Assert.True(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(1, state.StoredObjects);
                Assert.True(store.TryGet(entry.Guid, "Source", out string source));
                Assert.Equal(string.Empty, source);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void EmptyReaderResultIsValidWhenEveryRequiredPartIsAlreadyFresh()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-all-fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000013", 1);
                Assert.True(store.Put(entry.Guid, "Source", string.Empty, entry.LastUpdate, "v1"));
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => Array.Empty<SourceStoreBackfillPart>(),
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source" });

                Assert.True(service.RunOneSliceForTest(10, 1000));
                Assert.Equal("complete", service.GetState().State);
                Assert.Equal(1, service.GetState().ProcessedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void FailedSourceStorePutDoesNotAdvanceCursorOrCertifyComplete()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-put-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000007", 1);
                var service = new SourceStoreBackfillService(
                    store,
                    (guid, partName, source, lastUpdate, versionToken) => false,
                    null);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart
                        {
                            PartName = "Source",
                            Source = "source"
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false);

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Equal(0, state.StoredObjects);
                Assert.Null(state.CompletedAtUtc);
                Assert.Contains("rejected", state.LastError, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void SdkEnqueueRejectionSchedulesRetryAndEventuallyRunsSlice()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-enqueue-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000008", 1);
                int enqueueAttempts = 0;
                int sliceCompleted = 0;
                var service = new SourceStoreBackfillService(
                    store,
                    null,
                    action =>
                    {
                        if (Interlocked.Increment(ref enqueueAttempts) == 1) return false;
                        action();
                        Volatile.Write(ref sliceCompleted, 1);
                        return true;
                    });

                Assert.True(service.Start(
                    () => new[] { entry },
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source" } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: true));
                Assert.True(SpinWait.SpinUntil(
                    () => Volatile.Read(ref sliceCompleted) == 1,
                    TimeSpan.FromSeconds(5)));
                Assert.Equal(2, Volatile.Read(ref enqueueAttempts));
                Assert.Equal("complete", service.GetState().State);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public async Task RepeatedSdkEnqueueRejectionsStopAtTheRetryLimit()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-enqueue-limit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000009", 1);
                int enqueueAttempts = 0;
                var service = new SourceStoreBackfillService(
                    store,
                    null,
                    action =>
                    {
                        Interlocked.Increment(ref enqueueAttempts);
                        return false;
                    });

                service.Start(
                    () => new[] { entry },
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source" } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: true);

                Assert.True(SpinWait.SpinUntil(
                    () => Volatile.Read(ref enqueueAttempts) >= 5,
                    TimeSpan.FromSeconds(5)));
                await Task.Delay(250);
                Assert.Equal(5, Volatile.Read(ref enqueueAttempts));
                Assert.Equal(5, service.GetState().RetryCount);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void FreshStoredPartIsNotReadAgain()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000003", 1);
                int reads = 0;
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e =>
                    {
                        if (store.IsStoredAndFresh(e, new List<string> { "source" }))
                            return Array.Empty<SourceStoreBackfillPart>();
                        reads++;
                        return Array.Empty<SourceStoreBackfillPart>();
                    }, schedule: false);
                store.Put(entry.Guid, "source", "cached", entry.LastUpdate, "v1");
                service.RunOneSliceForTest(10, 1000);
                Assert.Equal(0, reads);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static SearchIndex.IndexEntry Entry(string guid, int minutes)
            => new SearchIndex.IndexEntry
            {
                Guid = guid,
                StorageKey = "Type:" + guid,
                Type = "Procedure",
                Name = guid,
                LastUpdate = DateTime.UtcNow.AddMinutes(-minutes)
            };
    }
}
