using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceStoreBackfillServiceTests
    {
        [Theory]
        [InlineData("Transaction", "Events,Rules,WebForm", "Events,Rules,WebForm")]
        [InlineData("Attribute", "", "")]
        [InlineData("Table", "", "")]
        [InlineData("Procedure", "Source,Rules,Layout", "Source,Rules,Layout")]
        [InlineData("DataSelector", "Conditions", "Conditions")]
        public void RequiredPartsOnlyIncludeSupportedSdkParts(string type, string available, string expected)
        {
            var parts = ObjectService.GetSourceStoreRequiredParts(type,
                available.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(expected.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), parts);
        }

        [Theory]
        [InlineData("Transaction", "Events,Rules,WebForm")]
        [InlineData("Attribute", "")]
        [InlineData("Table", "")]
        public void SliceCompletesWithoutRequestingUnsupportedParts(string type, string available)
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry(Guid.NewGuid().ToString("N"), 1);
                entry.Type = type;
                var required = ObjectService.GetSourceStoreRequiredParts(type,
                    available.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                var service = new SourceStoreBackfillService(store);
                Assert.True(service.Start(() => new[] { entry }, e => required
                    .Select(p => new SourceStoreBackfillPart { PartName = p, Source = "", ReadSucceeded = true }).ToList(),
                    requiredPartProvider: e => required, schedule: false));
                Assert.True(service.RunOneSliceForTest(1, 1000));
                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(1, state.ProcessedObjects);
                Assert.True(string.IsNullOrEmpty(state.LastError));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void ScheduledSdkActionExcludesSlowStorePut()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-backfill-sta-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                long actionMs = -1;
                var finished = new ManualResetEventSlim();
                var service = new SourceStoreBackfillService(store,
                    (guid, part, source, updated, token) => { Thread.Sleep(500); return store.Put(guid, part, source, updated, token); },
                    action => { var watch = System.Diagnostics.Stopwatch.StartNew(); action(); actionMs = watch.ElapsedMilliseconds; finished.Set(); return true; });
                service.Start(() => new[] { Entry(Guid.NewGuid().ToString(), 1) },
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "x" } });
                Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
                Assert.True(actionMs < 300, "SDK action held for " + actionMs + " ms");
                Assert.True(SpinWait.SpinUntil(() => service.GetState().State == "complete", TimeSpan.FromSeconds(10)));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

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
                // The enqueued action is only the SDK read; the store write and
                // certification finish off the STA after it returns.
                Assert.True(SpinWait.SpinUntil(
                    () => service.GetState().State == "complete",
                    TimeSpan.FromSeconds(5)));
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

        [Fact]
        public void ConfirmedDeletedObjectAdvancesCursorAndRemovesFromStore()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-deleted-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry1 = Entry("00000000-0000-0000-0000-000000000091", 5);
                var entry2 = Entry("00000000-0000-0000-0000-000000000092", 1);

                // Pre-populate entry1 in store to prove RemoveObject cleans it up
                store.Put(entry1.Guid, "Source", "old source", entry1.LastUpdate, "v0");
                Assert.True(store.StoredRecordCount > 0);

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry1, entry2 },
                    e =>
                    {
                        if (e.Guid == entry1.Guid)
                        {
                            return new[] { new SourceStoreBackfillPart { IsDeleted = true } };
                        }
                        return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "fresh source" } };
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source" });

                bool sliceSuccess = service.RunOneSliceForTest(10, 1000);
                Assert.True(sliceSuccess);

                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(2, state.CursorIndex);
                Assert.Equal(2, state.ProcessedObjects);
                Assert.Equal(1, state.StoredObjects);
                Assert.Equal(1, state.RemovedObjects);
                Assert.True(string.IsNullOrEmpty(state.LastError));

                // Verify entry1 was cleaned up from the store
                Assert.False(store.RecordExistsForTest(entry1.Guid + ":Source"));
                // Verify entry2 is stored and fresh
                Assert.True(store.IsStoredAndFresh(entry2, new List<string> { "Source" }));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void TriggerStateReflectsWaitingForTriggerWhenOff()
        {
            var state = new SourceStoreBackfillState { State = "off" };
            var json = state.ToJson();
            Assert.Equal("off", json["state"]?.ToString());
            Assert.Equal("waitingForTrigger", json["triggerState"]?.ToString());

            state.TriggerState = "running";
            Assert.Equal("running", state.ToJson()["triggerState"]?.ToString());
        }

        [Fact]
        public void DurableSourceStoreRoot_IndependentOfGatewayProcessScopeId()
        {
            string originalScope = Environment.GetEnvironmentVariable("GXMCP_STATE_SCOPE_ID");
            string originalKey = Environment.GetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY");
            string originalKbId = Environment.GetEnvironmentVariable("GXMCP_KB_ID");
            string originalKbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
            string originalGen = Environment.GetEnvironmentVariable("GXMCP_KB_GENERATION");

            try
            {
                Environment.SetEnvironmentVariable("GXMCP_KB_ID", "kb-alpha");
                Environment.SetEnvironmentVariable("GX_KB_PATH", @"C:\TestKBs\Alpha");
                Environment.SetEnvironmentVariable("GXMCP_KB_GENERATION", "1");

                // Process 1 scope
                Environment.SetEnvironmentVariable("GXMCP_STATE_SCOPE_ID", Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY", "proc-1-token");
                string root1 = RuntimePaths.DurableSourceStoreRoot;

                // Process 2 scope (simulating gateway restart with a different StateScopeId)
                Environment.SetEnvironmentVariable("GXMCP_STATE_SCOPE_ID", Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY", "proc-2-token");
                string root2 = RuntimePaths.DurableSourceStoreRoot;

                Assert.Equal(root1, root2);

                // Now bump generation: durable root MUST change
                Environment.SetEnvironmentVariable("GXMCP_KB_GENERATION", "2");
                string root3 = RuntimePaths.DurableSourceStoreRoot;
                Assert.NotEqual(root1, root3);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_STATE_SCOPE_ID", originalScope);
                Environment.SetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY", originalKey);
                Environment.SetEnvironmentVariable("GXMCP_KB_ID", originalKbId);
                Environment.SetEnvironmentVariable("GX_KB_PATH", originalKbPath);
                Environment.SetEnvironmentVariable("GXMCP_KB_GENERATION", originalGen);
            }
        }

        [Fact]
        public void TryCleanupOrphanedStores_RemovesExpiredProcessScopedStores()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "gxmcp-test-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempBase);
            try
            {
                string stateDir = Path.Combine(tempBase, "state");
                string currentStore = Path.Combine(stateDir, "current-hash", "source-store");
                string oldStore = Path.Combine(stateDir, "old-hash", "source-store");

                Directory.CreateDirectory(currentStore);
                Directory.CreateDirectory(oldStore);

                File.WriteAllText(Path.Combine(currentStore, "catalog.json.gz"), "current");
                File.WriteAllText(Path.Combine(oldStore, "catalog.json.gz"), "old");

                // Set old store last write time to 2 hours ago
                Directory.SetLastWriteTimeUtc(oldStore, DateTime.UtcNow.AddHours(-2));

                int cleaned = SourceStoreService.TryCleanupOrphanedStores(currentStore, TimeSpan.FromHours(1));
                Assert.True(cleaned >= 1);
                Assert.True(Directory.Exists(currentStore));
                Assert.False(Directory.Exists(oldStore));
            }
            finally
            {
                try { Directory.Delete(tempBase, true); } catch { }
            }
        }

        [Fact]
        public void Slice_DoesNotGrowTriangularly_AccurateLinearCounts()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-linear-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entries = Enumerable.Range(1, 5)
                    .Select(i => Entry($"00000000-0000-0000-0000-00000000000{i}", i))
                    .ToArray();

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => entries,
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 2000,
                    schedule: false);

                Assert.True(service.RunOneSliceForTest(10, 2000));
                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(5, state.TotalObjects);
                // Linear count check (issue #554: previously triangular sum 1+2+3+4+5=15)
                Assert.Equal(5, state.ProcessedObjects);
                Assert.Equal(5, state.StoredObjects);
                Assert.Equal(5, state.StaleObjects);
                Assert.Equal(0, state.RemovedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Slice_MultiSliceCountsGrowLinearly_RemovedIsNotDoubleCounted()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-multislice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entries = Enumerable.Range(1, 5)
                    .Select(i => Entry($"00000000-0000-0000-0000-00000000000{i}", i))
                    .ToArray();
                var deleted = entries[2];

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => entries,
                    e => e.Guid == deleted.Guid
                        ? new[] { new SourceStoreBackfillPart { IsDeleted = true } }
                        : new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } },
                    maxObjectsPerSlice: 2,
                    sliceBudgetMs: 2000,
                    schedule: false);

                // Slice 1: entries 1 and 2 (both stored).
                Assert.True(service.RunOneSliceForTest(2, 2000));
                var afterFirst = service.GetState();
                Assert.Equal("running", afterFirst.State);
                Assert.Equal(2, afterFirst.ProcessedObjects);
                Assert.Equal(2, afterFirst.StoredObjects);
                Assert.Equal(2, afterFirst.StaleObjects);
                Assert.Equal(0, afterFirst.RemovedObjects);

                // Slice 2: entry 3 (deleted) and entry 4.
                Assert.True(service.RunOneSliceForTest(2, 2000));
                var afterSecond = service.GetState();
                Assert.Equal("running", afterSecond.State);
                Assert.Equal(4, afterSecond.ProcessedObjects);
                Assert.Equal(3, afterSecond.StoredObjects);
                Assert.Equal(3, afterSecond.StaleObjects);
                Assert.Equal(1, afterSecond.RemovedObjects);

                // Slice 3: entry 5.
                Assert.True(service.RunOneSliceForTest(2, 2000));
                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(5, state.TotalObjects);
                Assert.Equal(5, state.ProcessedObjects);
                Assert.Equal(4, state.StoredObjects);
                Assert.Equal(4, state.StaleObjects);
                Assert.Equal(1, state.RemovedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Slice_HandlesDeletedObjectsWithoutDoubleCounting()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-del-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var e1 = Entry("00000000-0000-0000-0000-000000000010", 1);
                var eDel = Entry("00000000-0000-0000-0000-000000000020", 2);
                var e2 = Entry("00000000-0000-0000-0000-000000000030", 3);

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { e1, eDel, e2 },
                    e => e.Guid == eDel.Guid
                        ? new[] { new SourceStoreBackfillPart { IsDeleted = true } }
                        : new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 2000,
                    schedule: false);

                Assert.True(service.RunOneSliceForTest(10, 2000));
                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(3, state.ProcessedObjects);
                Assert.Equal(2, state.StoredObjects);
                Assert.Equal(1, state.RemovedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Slice_AdvancesPastUnreadableObjectAfterRetry_AndCompletesWithFailures()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-fail-advance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var eGood1 = Entry("00000000-0000-0000-0000-000000000001", 10);
                var eBad = Entry("00000000-0000-0000-0000-000000000002", 20);
                var eGood2 = Entry("00000000-0000-0000-0000-000000000003", 30);

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { eGood1, eBad, eGood2 },
                    e =>
                    {
                        if (e.Guid == eBad.Guid)
                            throw new InvalidOperationException("Corrupted SDK part for object 2");
                        return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } };
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 2000,
                    schedule: false);

                // Slice 1: processes eGood1, hits eBad on attempt 0 -> fails slice without advancing past eBad
                Assert.False(service.RunOneSliceForTest(10, 2000));
                var stateAfterFail = service.GetState();
                Assert.Equal("error", stateAfterFail.State);
                Assert.Equal(1, stateAfterFail.CursorIndex); // CursorIndex stopped before advancing past eBad
                Assert.Equal(1, stateAfterFail.ProcessedObjects);

                // Slice 2 (the retry): hits eBad on attempt 1 -> records in readFailedObjects, advances past eBad, processes eGood2
                Assert.True(service.RunOneSliceForTest(10, 2000));
                var stateFinal = service.GetState();
                Assert.Equal("completeWithFailures", stateFinal.State);
                Assert.Equal(3, stateFinal.CursorIndex);
                Assert.Equal(3, stateFinal.ProcessedObjects);
                Assert.Equal(2, stateFinal.StoredObjects);
                Assert.Equal(1, stateFinal.FailedObjects);
                Assert.Contains(eBad.Guid, stateFinal.ReadFailedObjects);

                var json = service.GetStateJson();
                Assert.Equal("completeWithFailures", json["state"]?.ToString());
                Assert.Equal(1, json["failedObjects"]?.ToObject<int>());
                var failedArr = json["readFailedObjects"] as Newtonsoft.Json.Linq.JArray;
                Assert.NotNull(failedArr);
                Assert.Contains(eBad.Guid, failedArr.Select(t => t.ToString()));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void TriggerState_DynamicallyResolvesWhenErrorOrIdle()
        {
            var json = new Newtonsoft.Json.Linq.JObject
            {
                ["state"] = "error",
                ["triggerState"] = "running"
            };
            var state = SourceStoreBackfillState.FromJson(json);
            // Must not stay frozen on "running" when state is error (issue #554)
            Assert.Equal("error", state.ResolveTriggerState());
            Assert.Equal("error", state.ToJson()["triggerState"]?.ToString());

            var idleJson = new Newtonsoft.Json.Linq.JObject
            {
                ["state"] = "idle",
                ["triggerState"] = "running"
            };
            var idleState = SourceStoreBackfillState.FromJson(idleJson);
            Assert.Equal("idle", idleState.ResolveTriggerState());
        }

        [Fact]
        public void TriggerState_AfterReloadFollowsLiveState()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-trigger-reload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000041", 1);

                // A Worker that was mid-run persists {state:"running", triggerState:"running"}.
                var persisted = new SourceStoreBackfillState { State = "running" }.ToJson();
                Assert.Equal("running", persisted["triggerState"]?.ToString());
                File.WriteAllText(Path.Combine(root, "backfill-state.json"), persisted.ToString());

                // Restart: the reloaded triggerState must follow the live state (issue #554).
                var service = new SourceStoreBackfillService(store);
                Assert.True(service.Start(
                    () => new[] { entry },
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source" } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false));
                var queued = service.GetStateJson();
                Assert.Equal("queued", queued["state"]?.ToString());
                Assert.Equal("queued", queued["triggerState"]?.ToString());

                Assert.True(service.RunOneSliceForTest(10, 1000));
                var complete = service.GetStateJson();
                Assert.Equal("complete", complete["state"]?.ToString());
                Assert.Equal("complete", complete["triggerState"]?.ToString());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Slice_AdvancesPastPartReadFailure_AfterRetry()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-part-fail-advance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var eGood1 = Entry("00000000-0000-0000-0000-000000000001", 10);
                var eBad = Entry("00000000-0000-0000-0000-000000000002", 20);
                var eGood2 = Entry("00000000-0000-0000-0000-000000000003", 30);

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { eGood1, eBad, eGood2 },
                    e => e.Guid == eBad.Guid
                        ? new[]
                        {
                            new SourceStoreBackfillPart
                            {
                                PartName = "Source",
                                Source = null,
                                ReadSucceeded = false,
                                ReadError = "SDK read failed"
                            }
                        }
                        : new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 2000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source" });

                Assert.False(service.RunOneSliceForTest(10, 2000));
                var afterFail = service.GetState();
                Assert.Equal("error", afterFail.State);
                Assert.Equal(1, afterFail.CursorIndex);
                Assert.Contains("read failure", afterFail.LastError);

                Assert.True(service.RunOneSliceForTest(10, 2000));
                var final = service.GetState();
                Assert.Equal("completeWithFailures", final.State);
                Assert.Equal(3, final.CursorIndex);
                Assert.Equal(3, final.ProcessedObjects);
                Assert.Equal(2, final.StoredObjects);
                Assert.Equal(1, final.FailedObjects);
                Assert.Contains(eBad.Guid, final.ReadFailedObjects);
                Assert.False(store.RecordExistsForTest(eBad.Guid + ":Source"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Eta_AfterRestart_IsMeasuredOnlyOverObjectsTimedByThisProcess()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-eta-restart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                // 103 objects; the persisted run already processed the first 100.
                var entries = Enumerable.Range(1, 103)
                    .Select(i => Entry("00000000-0000-0000-0000-" + i.ToString("D12"), i))
                    .ToArray();
                var persisted = new SourceStoreBackfillState
                {
                    State = "running",
                    Cursor = entries[99].StorageKey,
                    CursorIndex = 100,
                    TotalObjects = entries.Length,
                    ProcessedObjects = 100,
                    StoredObjects = 100,
                    StaleObjects = 100
                }.ToJson();
                File.WriteAllText(Path.Combine(root, "backfill-state.json"), persisted.ToString());

                // Restart: the run clock is fresh while ProcessedObjects is not.
                var clock = new FakeBackfillClock();
                var service = new SourceStoreBackfillService(store, null, null, clock);
                Assert.True(service.Start(
                    () => entries,
                    e =>
                    {
                        clock.Advance(20);
                        return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } };
                    },
                    maxObjectsPerSlice: 1,
                    sliceBudgetMs: 5000,
                    schedule: false));
                Assert.Equal(100, service.GetState().CursorIndex);

                // One object timed at 20 ms with two remaining: 20 ms/object * 2 = 40 ms.
                // Dividing the 20 ms by the 101 processed objects instead would yield 0.
                Assert.True(service.RunOneSliceForTest(1, 5000));
                var state = service.GetState();
                Assert.Equal("running", state.State);
                Assert.Equal(101, state.ProcessedObjects);
                Assert.Equal(40, state.EtaMs);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ConsecutiveUnreadableObjects_DoNotExhaustTransientRetryBudget()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-fail-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var unreadable = Enumerable.Range(1, SourceStoreBackfillService.MaxTransientRetries + 1)
                    .Select(i => Entry($"00000000-0000-0000-0000-0000000000{i:D2}", i))
                    .ToArray();
                var readable = Entry("00000000-0000-0000-0000-000000000099", 99);
                var catalog = unreadable.Concat(new[] { readable }).ToArray();

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => catalog,
                    e =>
                    {
                        if (e.Guid == readable.Guid)
                            return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code" } };
                        throw new InvalidOperationException("Unreadable object " + e.Guid);
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 2000,
                    schedule: false);

                Assert.False(service.RunOneSliceForTest(10, 2000));
                var afterFirstProbe = service.GetState();
                Assert.Equal("error", afterFirstProbe.State);
                Assert.Equal(0, afterFirstProbe.RetryCount);

                var state = afterFirstProbe;
                for (int slice = 0; slice < 14 && !state.IsComplete; slice++)
                {
                    service.RunOneSliceForTest(10, 2000);
                    state = service.GetState();
                    Assert.True(
                        state.RetryCount < SourceStoreBackfillService.MaxTransientRetries,
                        $"slice {slice}: retryCount {state.RetryCount} would park the backfill in error");
                }

                Assert.Equal("completeWithFailures", state.State);
                Assert.Equal(unreadable.Length, state.FailedObjects);
                Assert.Equal(1, state.StoredObjects);
                Assert.Equal(catalog.Length, state.ProcessedObjects);
                Assert.Equal(0, state.RetryCount);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void CompletedWithFailures_SameCatalog_DoesNotRetryFailedObjects()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-fail-same-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var eGood1 = Entry("00000000-0000-0000-0000-000000000001", 10);
                var eBad = Entry("00000000-0000-0000-0000-000000000002", 20);
                var eGood2 = Entry("00000000-0000-0000-0000-000000000003", 30);
                var catalog = new[] { eGood1, eBad, eGood2 };
                int badReads = 0;
                Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> reader = e =>
                {
                    if (e.Guid == eBad.Guid)
                    {
                        badReads++;
                        throw new InvalidOperationException("Deterministically unreadable object");
                    }
                    return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } };
                };

                var first = new SourceStoreBackfillService(store);
                first.Start(() => catalog, reader, maxObjectsPerSlice: 10, sliceBudgetMs: 2000, schedule: false);
                Assert.False(first.RunOneSliceForTest(10, 2000));
                Assert.True(first.RunOneSliceForTest(10, 2000));
                Assert.Equal("completeWithFailures", first.GetState().State);
                Assert.Equal(2, badReads);

                // Worker restart: state is reloaded from disk and the exhausted cursor re-walks
                // the same catalogue to refresh stale parts.
                var reloaded = new SourceStoreBackfillService(store);
                Assert.Equal(1, reloaded.GetState().FailedObjects);
                Assert.True(reloaded.Start(() => catalog, reader, maxObjectsPerSlice: 10, sliceBudgetMs: 2000, schedule: false));
                Assert.Equal("queued", reloaded.GetState().State);
                Assert.True(reloaded.RunOneSliceForTest(10, 2000));

                var state = reloaded.GetState();
                Assert.Equal("completeWithFailures", state.State);
                Assert.Equal(3, state.CursorIndex);
                Assert.Equal(3, state.ProcessedObjects);
                Assert.Equal(1, state.FailedObjects);
                Assert.Contains(eBad.Guid, state.ReadFailedObjects);
                Assert.Equal(2, badReads);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void CatalogSignatureChange_ClearsReadFailedObjects()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-fail-catalog-change-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var eGood1 = Entry("00000000-0000-0000-0000-000000000001", 10);
                var eBad = Entry("00000000-0000-0000-0000-000000000002", 20);
                var eGood2 = Entry("00000000-0000-0000-0000-000000000003", 30);
                var eAdded = Entry("00000000-0000-0000-0000-000000000004", 40);
                int badReads = 0;
                Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> reader = e =>
                {
                    if (e.Guid == eBad.Guid)
                    {
                        badReads++;
                        throw new InvalidOperationException("Deterministically unreadable object");
                    }
                    return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } };
                };

                var service = new SourceStoreBackfillService(store);
                service.Start(() => new[] { eGood1, eBad, eGood2 }, reader, maxObjectsPerSlice: 10, sliceBudgetMs: 2000, schedule: false);
                Assert.False(service.RunOneSliceForTest(10, 2000));
                Assert.True(service.RunOneSliceForTest(10, 2000));
                Assert.Equal(1, service.GetState().FailedObjects);

                service.Start(() => new[] { eGood1, eBad, eGood2, eAdded }, reader, maxObjectsPerSlice: 10, sliceBudgetMs: 2000, schedule: false);
                var restarted = service.GetState();
                Assert.Equal("queued", restarted.State);
                Assert.Equal(0, restarted.CursorIndex);
                Assert.Equal(0, restarted.FailedObjects);
                Assert.Empty(restarted.ReadFailedObjects);

                Assert.False(service.RunOneSliceForTest(10, 2000));
                Assert.True(service.RunOneSliceForTest(10, 2000));
                Assert.Equal(4, badReads);
                Assert.Equal("completeWithFailures", service.GetState().State);
                Assert.Equal(1, service.GetState().FailedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void StateJson_CapsReportedReadFailedObjects_AndPersistsTheFullList()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-fail-cap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                int total = SourceStoreBackfillState.MaxReportedReadFailedObjects + 10;
                var persisted = new SourceStoreBackfillState
                {
                    State = "completeWithFailures",
                    ReadFailedObjects = Enumerable.Range(1, total).Select(i => Guid.NewGuid().ToString("D")).ToList()
                };
                Assert.Equal(total, ((Newtonsoft.Json.Linq.JArray)persisted.ToJson()["readFailedObjects"]).Count);
                File.WriteAllText(Path.Combine(root, "backfill-state.json"), persisted.ToJson().ToString());

                var service = new SourceStoreBackfillService(store);
                Assert.Equal(total, service.GetState().FailedObjects);

                var json = service.GetStateJson();
                Assert.Equal(total, json["failedObjects"]?.ToObject<int>());
                Assert.Equal(
                    SourceStoreBackfillState.MaxReportedReadFailedObjects,
                    ((Newtonsoft.Json.Linq.JArray)json["readFailedObjects"]).Count);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Eta_AfterCatalogueReset_DoesNotCarryThePreviousRunClock()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-eta-reset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var slow1 = Entry("00000000-0000-0000-0000-000000000051", 1);
                var slow2 = Entry("00000000-0000-0000-0000-000000000052", 2);

                // Run 1: two objects at 100 ms each leave 200 ms on the clock.
                var clock = new FakeBackfillClock();
                var service = new SourceStoreBackfillService(store, null, null, clock);
                Assert.True(service.Start(
                    () => new[] { slow1, slow2 },
                    e =>
                    {
                        clock.Advance(100);
                        return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } };
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 5000,
                    schedule: false));
                Assert.True(service.RunOneSliceForTest(10, 5000));
                Assert.Equal("complete", service.GetState().State);
                Assert.Equal(TimeSpan.FromMilliseconds(200), clock.Elapsed);

                // Run 2: the catalogue changed, so counters reset; the clock must too.
                var inserted = Entry("00000000-0000-0000-0000-000000000053", 0);
                Assert.True(service.Start(
                    () => new[] { inserted, slow1, slow2 },
                    e =>
                    {
                        clock.Advance(5);
                        return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "fresh-" + e.Guid } };
                    },
                    maxObjectsPerSlice: 1,
                    sliceBudgetMs: 5000,
                    schedule: false));
                Assert.Equal(0, service.GetState().ProcessedObjects);
                Assert.Equal(TimeSpan.Zero, clock.Elapsed);

                // One object timed at 5 ms with two remaining: 5 ms/object * 2 = 10 ms.
                // A clock still carrying the previous run would report (200 + 5) * 2 = 410 ms.
                Assert.True(service.RunOneSliceForTest(1, 5000));
                var state = service.GetState();
                Assert.Equal("running", state.State);
                Assert.Equal(1, state.ProcessedObjects);
                Assert.Equal(10, state.EtaMs);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void StaleSlice_CannotOverwriteTheCursorOfARestartedWalk()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-stale-slice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var e1 = Entry("00000000-0000-0000-0000-000000000061", 1);
                var e2 = Entry("00000000-0000-0000-0000-000000000062", 2);
                var e3 = Entry("00000000-0000-0000-0000-000000000063", 3);
                var inserted = Entry("00000000-0000-0000-0000-000000000064", 0);
                var service = new SourceStoreBackfillService(store);
                int restarts = 0;
                Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> reader = null;
                reader = e =>
                {
                    if (e.Guid == e2.Guid && restarts == 0)
                    {
                        // The catalogue changes while this slice is mid-walk (issue #596):
                        // the new Start swaps the entry snapshot and zeroes the cursor.
                        restarts++;
                        Assert.True(service.Start(
                            () => new[] { inserted, e1, e2, e3 },
                            reader,
                            maxObjectsPerSlice: 10,
                            sliceBudgetMs: 5000,
                            schedule: false));
                    }
                    return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } };
                };

                Assert.True(service.Start(() => new[] { e1, e2, e3 }, reader, maxObjectsPerSlice: 10, sliceBudgetMs: 5000, schedule: false));
                string firstSignature = service.GetState().CatalogSignature;

                // The slice stored e1 against the old list, then observed the restart
                // while reading e2: it must abandon without touching the new walk.
                Assert.False(service.RunOneSliceForTest(10, 5000));
                Assert.Equal(1, restarts);
                var restarted = service.GetState();
                Assert.Equal("queued", restarted.State);
                Assert.Equal(0, restarted.CursorIndex);
                Assert.True(string.IsNullOrEmpty(restarted.Cursor));
                Assert.Equal(0, restarted.ProcessedObjects);
                Assert.Equal(4, restarted.TotalObjects);
                Assert.NotEqual(firstSignature, restarted.CatalogSignature);
                Assert.True(string.IsNullOrEmpty(restarted.LastError));

                // The restarted walk runs its own snapshot from the beginning.
                Assert.True(service.RunOneSliceForTest(10, 5000));
                var complete = service.GetState();
                Assert.Equal("complete", complete.State);
                Assert.Equal(4, complete.CursorIndex);
                Assert.Equal(4, complete.ProcessedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void StaleSlice_DoesNotRecordItsErrorAgainstTheRestartedWalk()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-stale-error-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var e1 = Entry("00000000-0000-0000-0000-000000000071", 1);
                var e2 = Entry("00000000-0000-0000-0000-000000000072", 2);
                var inserted = Entry("00000000-0000-0000-0000-000000000073", 0);
                var service = new SourceStoreBackfillService(store);
                Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> healthy =
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } };
                Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> failing = e =>
                {
                    // Restart first, then fail: the failure belongs to the replaced walk.
                    Assert.True(service.Start(() => new[] { inserted, e1, e2 }, healthy, schedule: false));
                    throw new InvalidOperationException("stale reader failure");
                };

                Assert.True(service.Start(() => new[] { e1, e2 }, failing, schedule: false));
                Assert.False(service.RunOneSliceForTest(10, 5000));

                var restarted = service.GetState();
                Assert.Equal("queued", restarted.State);
                Assert.Equal(0, restarted.RetryCount);
                Assert.True(string.IsNullOrEmpty(restarted.LastError));
                Assert.Empty(restarted.ReadFailedObjects);

                Assert.True(service.RunOneSliceForTest(10, 5000));
                Assert.Equal("complete", service.GetState().State);
                Assert.Equal(3, service.GetState().ProcessedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Theory]
        [InlineData("complete", true)]
        [InlineData("completeWithFailures", true)]
        [InlineData("running", false)]
        [InlineData("error", false)]
        public void IsComplete_CertifiesCoverageForBothCompleteStates(string state, bool expected)
        {
            Assert.Equal(expected, new SourceStoreBackfillState { State = state }.IsComplete);
        }

        /// <summary>Deterministic run clock: time only moves when a test calls Advance.</summary>
        private sealed class FakeBackfillClock : ISourceStoreBackfillClock
        {
            private bool _running;
            public TimeSpan Elapsed { get; private set; }
            public void Start() => _running = true;
            public void Stop() => _running = false;
            public void Reset() { _running = false; Elapsed = TimeSpan.Zero; }
            public void Advance(int milliseconds)
            {
                if (_running) Elapsed += TimeSpan.FromMilliseconds(milliseconds);
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
