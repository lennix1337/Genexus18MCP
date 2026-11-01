using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerPoolTests
    {
        private static Configuration CfgWithMax(int max) =>
            new Configuration { Server = new ServerConfig { MaxOpenKbs = max } };

        [Fact]
        public void ListOpen_excludes_entries_without_worker()
        {
            // ListOpen filters on Worker != null; with RegisterForTest the Worker is null,
            // so ListOpen returns empty (intentional — entries-in-flight aren't "open").
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            var open = pool.ListOpen();
            Assert.Empty(open);
        }

        [Fact]
        public void SelectVictim_picks_oldest_lastActivity()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"), lastActivity: DateTime.UtcNow.AddMinutes(-10));
            pool.RegisterForTest(new KbHandle("b", "C:/B"), lastActivity: DateTime.UtcNow.AddMinutes(-1));
            var victim = pool.SelectVictimForTest();
            Assert.NotNull(victim);
            Assert.Equal("a", victim!.Alias);
        }

        // ── issue #333: eviction is idle-only ──────────────────────────────────
        //
        // The victim scan used to skip only entries with no Worker, so "oldest" was the
        // only criterion and a Worker mid-build, mid-write or mid-index was eligible.
        // Opening another KB at capacity could then stop the oldest Worker's process
        // and take its in-flight command with it. These guards pin the eligibility
        // rule; the entries are registered with a Worker present, because an entry
        // without one was never a candidate and would make every assertion vacuous.

        [Fact]
        public void Eviction_Skips_A_Worker_With_A_Command_In_Flight()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("alpha", "C:/Alpha"),
                lastActivity: DateTime.UtcNow.AddMinutes(-10), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("alpha", "C:/Alpha")));
            pool.RegisterForTest(new KbHandle("beta", "C:/Beta"),
                lastActivity: DateTime.UtcNow.AddMinutes(-1), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("beta", "C:/Beta")));

            // Alpha is the oldest, so the pre-#333 scan chose it.
            Assert.Equal("alpha", pool.SelectVictimForTest()!.Alias);

            // A command is in flight on Alpha: it must stop being eligible, and Beta
            // must be chosen instead even though it is more recently active.
            pool.SetInFlightForTest("alpha", 1);
            Assert.Equal("beta", pool.SelectEvictableVictimForTest()!.Alias);

            // Command finished: Alpha is eligible again, and being the oldest it wins.
            pool.SetInFlightForTest("alpha", 0);
            Assert.Equal("alpha", pool.SelectEvictableVictimForTest()!.Alias);
        }

        [Fact]
        public void Eviction_Reports_No_Victim_When_Every_Worker_Is_Busy()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("alpha", "C:/Alpha"),
                lastActivity: DateTime.UtcNow.AddMinutes(-10), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("alpha", "C:/Alpha")));
            pool.RegisterForTest(new KbHandle("beta", "C:/Beta"),
                lastActivity: DateTime.UtcNow.AddMinutes(-1), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("beta", "C:/Beta")));

            pool.SetInFlightForTest("alpha", 2);
            pool.SetInFlightForTest("beta", 1);

            // All busy: the caller reports the pool as full rather than stopping a
            // Worker that is executing something.
            Assert.Null(pool.SelectEvictableVictimForTest());
        }

        [Fact]
        public void Eviction_Skips_A_Draining_Or_Reloading_Worker()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("alpha", "C:/Alpha"),
                lastActivity: DateTime.UtcNow.AddMinutes(-10), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("alpha", "C:/Alpha")));
            pool.RegisterForTest(new KbHandle("beta", "C:/Beta"),
                lastActivity: DateTime.UtcNow.AddMinutes(-1), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("beta", "C:/Beta")));

            // A planned reload is already replacing Alpha's process; stopping it would
            // race the reload.
            pool.SetDrainingForTest("alpha");
            Assert.Equal("beta", pool.SelectEvictableVictimForTest()!.Alias);
        }

        [Fact]
        public void Begin_And_Release_Command_Balance_Across_Repeated_Calls()
        {
            // A leaked count would make an entry permanently ineligible for eviction,
            // so the release has to be idempotent and has to actually decrement.
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("alpha", "C:/Alpha"),
                worker: new WorkerProcess(CfgWithMax(2), new KbHandle("alpha", "C:/Alpha")));

            Assert.Equal(0, pool.InFlightCommandsForTest("alpha"));

            var first = pool.BeginCommand("alpha");
            var second = pool.BeginCommand("alpha");
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(2, pool.InFlightCommandsForTest("alpha"));

            first!();
            Assert.Equal(1, pool.InFlightCommandsForTest("alpha"));

            // Releasing twice must not under-count.
            first();
            Assert.Equal(1, pool.InFlightCommandsForTest("alpha"));

            second!();
            Assert.Equal(0, pool.InFlightCommandsForTest("alpha"));
        }

        // ── issue #367: work that outlives the RPC still blocks eviction ──────────

        [Fact]
        public void Eviction_Skips_A_Worker_With_Worker_Side_Activity_After_Its_Rpc_Returned()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("alpha", "C:/Alpha"),
                lastActivity: DateTime.UtcNow.AddMinutes(-10), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("alpha", "C:/Alpha")));
            pool.RegisterForTest(new KbHandle("beta", "C:/Beta"),
                lastActivity: DateTime.UtcNow.AddMinutes(-1), worker: new WorkerProcess(CfgWithMax(2), new KbHandle("beta", "C:/Beta")));

            // Alpha acknowledged an async build and returned: InFlightCommands is 0, the job is not.
            bool alphaBuilding = true;
            pool.WorkerSideActivity = alias => alias == "alpha" && alphaBuilding;
            Assert.Equal("beta", pool.SelectEvictableVictimForTest()!.Alias);

            // Every Worker busy: nothing is eligible, so the caller reports the pool as full.
            pool.SetInFlightForTest("beta", 1);
            Assert.Null(pool.SelectEvictableVictimForTest());

            // The job ended: Alpha is eligible again.
            alphaBuilding = false;
            pool.SetInFlightForTest("beta", 0);
            Assert.Equal("alpha", pool.SelectEvictableVictimForTest()!.Alias);
        }

        [Fact]
        public void Begin_Command_Refuses_A_Worker_The_Entry_No_Longer_Owns()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            var current = new WorkerProcess(CfgWithMax(2), new KbHandle("alpha", "C:/Alpha"));
            var stale = new WorkerProcess(CfgWithMax(2), new KbHandle("alpha", "C:/Alpha"));
            pool.RegisterForTest(new KbHandle("alpha", "C:/Alpha"), worker: current);

            // The Worker handed out before an eviction/replace must not be reserved.
            Assert.Null(pool.BeginCommand("alpha", stale));
            Assert.Equal(0, pool.InFlightCommandsForTest("alpha"));

            var release = pool.BeginCommand("alpha", current);
            Assert.NotNull(release);
            Assert.Equal(1, pool.InFlightCommandsForTest("alpha"));
            release!();
        }

        [Fact]
        public void Job_Registry_Reports_A_Live_Job_Only_Until_It_Ends_Or_Stalls()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("session", "lifecycle/build", 60);
            job.WorkerAlias = "alpha";

            Assert.True(registry.HasLiveJobOnWorker("ALPHA"));
            Assert.False(registry.HasLiveJobOnWorker("beta"));

            job.Status = "stalled";
            Assert.False(registry.HasLiveJobOnWorker("alpha"));
        }

        [Fact]
        public void Begin_Command_For_An_Unknown_Alias_Is_A_NoOp()
        {
            // A Worker resolved outside the pool (warmup, direct construction) has no
            // entry to protect; that is not an error.
            var pool = new WorkerPool(CfgWithMax(2));
            Assert.Null(pool.BeginCommand("ghost"));
            Assert.Null(pool.BeginCommand(null));
            Assert.Equal(0, pool.InFlightCommandsForTest("ghost"));
        }

        [Fact]
        public void IsAtCapacity_respects_MaxOpenKbs()
        {
            // IsAtCapacity uses ">=", matching SpawnWorkerAsync's eviction threshold.
            // At-max (count == max) IS at capacity: opening one more KB would evict.
            var pool = new WorkerPool(CfgWithMax(2));
            Assert.False(pool.IsAtCapacity());
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            Assert.False(pool.IsAtCapacity());
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            Assert.True(pool.IsAtCapacity());  // exactly at max — at capacity
            pool.RegisterForTest(new KbHandle("c", "C:/C"));
            Assert.True(pool.IsAtCapacity());  // over max — at capacity
        }

        [Fact]
        public void Close_returns_true_when_present_false_when_absent()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            Assert.True(pool.Close("a"));
            Assert.False(pool.Close("a"));
            Assert.False(pool.Close("ghost"));
        }

        [Fact]
        public void Close_is_case_insensitive()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("ProductionKb", "C:/P"));
            Assert.True(pool.Close("PRODUCTIONKB"));
        }

        // The spawn path must never reach a real WorkerProcess.Start() from these tests: the
        // factory below stands in for it and counts how often it was asked to spawn.
        private static Func<KbHandle, WorkerProcess> CountingSpawn(Configuration cfg, Action onSpawn) => handle =>
        {
            onSpawn();
            var worker = new WorkerProcess(cfg, handle);
            worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
            return worker;
        };

        // Drain regression: when an entry is draining, AcquireAsync must wait for
        // DrainComplete rather than returning the dying worker immediately.
        [Fact]
        public async Task AcquireAsync_during_drain_waits_for_DrainComplete()
        {
            var cfg = CfgWithMax(5);
            var pool = new WorkerPool(cfg);
            int spawns = 0;
            pool.SpawnFactoryForTest = CountingSpawn(cfg, () => Interlocked.Increment(ref spawns));
            var handle = new KbHandle("drainKb", "C:/Drain");
            // Register an entry with Worker=null so the fast path hits the draining check.
            pool.RegisterForTest(handle);
            pool.SetDrainingForTest("drainkb");
            Assert.True(pool.IsDrainingForTest("drainkb"));

            var acquireTask = pool.AcquireAsync(handle, CancellationToken.None);
            // Give it a moment to start waiting: a wait that wrongly returns shows up as a
            // completed task or a spawn.
            await Task.Delay(50);
            Assert.False(acquireTask.IsCompleted, "AcquireAsync should be blocked while draining.");
            Assert.Equal(0, Volatile.Read(ref spawns));

            // Finishing the drain releases the waiter; with Worker still null it falls through
            // to the spawn path, served by the counting factory.
            pool.CompleteDrainForTest("drainkb");
            var worker = await acquireTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(worker);
            Assert.Equal(1, Volatile.Read(ref spawns));
        }

        // Drain regression: a caller cancelled while the drain is still running leaves with an
        // OperationCanceledException and never reaches the spawn path. The drain is never
        // completed here, so there is no race between the cancellation and DrainComplete.
        [Fact]
        public async Task AcquireAsync_cancelled_during_drain_is_cancelled_without_spawning()
        {
            var cfg = CfgWithMax(5);
            var pool = new WorkerPool(cfg);
            int spawns = 0;
            pool.SpawnFactoryForTest = CountingSpawn(cfg, () => Interlocked.Increment(ref spawns));
            var handle = new KbHandle("drainKb", "C:/Drain");
            pool.RegisterForTest(handle);
            pool.SetDrainingForTest("drainkb");

            using var cts = new CancellationTokenSource();
            var acquireTask = pool.AcquireAsync(handle, cts.Token);
            Assert.False(acquireTask.IsCompleted, "AcquireAsync should be blocked while draining.");

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquireTask);
            Assert.True(acquireTask.IsCanceled);
            Assert.Equal(0, Volatile.Read(ref spawns));
        }

        // Drain regression: IsDrainingForTest reflects the draining flag accurately.
        [Fact]
        public void IsDrainingForTest_reflects_draining_state()
        {
            var pool = new WorkerPool(CfgWithMax(5));
            pool.RegisterForTest(new KbHandle("kb1", "C:/KB1"));
            Assert.False(pool.IsDrainingForTest("kb1"));
            pool.SetDrainingForTest("kb1");
            Assert.True(pool.IsDrainingForTest("kb1"));
        }

        // Plan 069: RecycleStalledWorker drops the live entry (like DropLiveEntry) but
        // stops the worker with the Wedged reason so the eager-respawn path fires; the
        // durable known set still survives so the KB stays resolvable.
        [Fact]
        public void RecycleStalledWorker_drops_live_entry_but_keeps_known()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("adhoc", "C:/KB/AdHoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");

            Assert.True(pool.RecycleStalledWorker("adhoc"));

            // Live entry gone (TryGet null), but still resolvable via the known set.
            Assert.Null(pool.TryGet("adhoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");
        }

        [Fact]
        public void RecycleStalledWorker_absent_alias_returns_false()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            Assert.False(pool.RecycleStalledWorker("ghost"));
            Assert.False(pool.RecycleStalledWorker(null!));
        }

        [Fact]
        public void RecycleStalledWorker_is_case_insensitive()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("ProdKb", "C:/P"));
            Assert.True(pool.RecycleStalledWorker("PRODKB"));
        }

        // issue #26 P3: the durable known set survives a live-entry drop (worker recycle)
        // but is cleared by an explicit Close.
        [Fact]
        public void DropLiveEntry_keeps_known_but_removes_open()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("adhoc", "C:/KB/AdHoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");

            pool.DropLiveEntry("adhoc");

            // Live entry gone (TryGet null), but still resolvable via the known set.
            Assert.Null(pool.TryGet("adhoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");
        }

        [Fact]
        public void Close_clears_known_registry()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("adhoc", "C:/KB/AdHoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");

            pool.Close("adhoc");

            Assert.DoesNotContain(pool.ListKnown(), h => h.Alias == "adhoc");
        }

        // Fix 9b (revised): IsAtCapacity and SpawnWorkerAsync share the same threshold.
        [Fact]
        public void IsAtCapacity_and_AcquireAsync_use_same_threshold()
        {
            // Both use count >= max. IsAtCapacity at exactly max is true;
            // consistent with SpawnWorkerAsync, which evicts when the other entries
            // alone already fill the cap (post-spawn total stays <= max).
            var pool = new WorkerPool(CfgWithMax(1));
            Assert.False(pool.IsAtCapacity()); // 0 entries, max=1 → 0 >= 1 is false
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            Assert.True(pool.IsAtCapacity());  // 1 entry, max=1 >= 1 is true
        }

        [Theory]
        [InlineData("GXMCP_SDK_COMPATIBLE", false)]
        [InlineData("GXMCP_SDK_FINGERPRINT_DRIFT", false)]
        [InlineData("GXMCP_SDK_VERSION_MISMATCH", true)]
        [InlineData("WORKER_STARTUP_FAILED", false)]
        public void Sdk_diagnostic_classifies_only_rejections_as_fatal(string code, bool fatal)
        {
            Assert.Equal(fatal, SdkDiagnosticClassifier.IsFatalCode(code));
        }

        [Fact]
        public void ClassifyCode_skips_informational_code_before_fatal_code()
        {
            string diagnostic = "GXMCP_SDK_COMPATIBLE major=18 GXMCP_SDK_VERSION_MISMATCH major=19";
            Assert.Equal("GXMCP_SDK_VERSION_MISMATCH", SdkDiagnosticClassifier.ClassifyCode(diagnostic));
        }

        [Fact]
        public void EntryState_Transitions_Follow_Explicit_StateMachine_And_Guard_Eviction()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            var vacantHandle = new KbHandle("vacant", "C:/Vacant");
            var activeHandle = new KbHandle("active", "C:/Active");

            pool.RegisterForTest(vacantHandle);
            Assert.Equal(WorkerPoolEntryState.Vacant, pool.GetEntryStateForTest("vacant"));

            var worker = new WorkerProcess(CfgWithMax(2), activeHandle);
            pool.RegisterForTest(activeHandle, worker: worker);
            Assert.Equal(WorkerPoolEntryState.Active, pool.GetEntryStateForTest("active"));

            // Drain transition
            pool.SetDrainingForTest("active");
            Assert.Equal(WorkerPoolEntryState.Draining, pool.GetEntryStateForTest("active"));
            Assert.True(pool.IsDrainingForTest("active"));

            // While draining, entry cannot be evicted
            Assert.Null(pool.SelectEvictableVictimForTest());

            // Complete drain restores Active
            pool.CompleteDrainForTest("active");
            Assert.Equal(WorkerPoolEntryState.Active, pool.GetEntryStateForTest("active"));
            Assert.False(pool.IsDrainingForTest("active"));

            // Now evictable again
            Assert.Equal("active", pool.SelectEvictableVictimForTest()?.Alias);
        }
    }
}
