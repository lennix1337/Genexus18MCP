using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [CollectionDefinition("WorkerLifecycleSerial", DisableParallelization = true)]
    public sealed class WorkerLifecycleSerialCollection { }

    [Collection("WorkerLifecycleSerial")]
    public sealed class WorkerCrashRespawnLifecycleTests : IDisposable
    {
        public WorkerCrashRespawnLifecycleTests()
        {
            Program.ResetWorkerLifecycleForTest();
            Program.IndexBootstrapTriggerForTest = () => { };
        }

        public void Dispose() => Program.ResetWorkerLifecycleForTest();

        [Fact]
        public async Task GxPublicWorkerSkipsNativeIndexBootstrapWithoutAffectingOtherKbs()
        {
            var config = new Configuration();
            var gx9 = new KbHandle("gx9", @"C:\Models\Gx9", null, "com-gxpublic", "9");
            var gx18 = new KbHandle("gx18", @"C:\Models\Gx18", null, "native-sdk", "18");
            int bootstraps = 0;
            Program.IndexBootstrapTriggerForTest = () => Interlocked.Increment(ref bootstraps);
            Program.StartWorkerForTest(config);
            var pool = Program.GetWorkerPool()!;
            pool.SpawnFactoryForTest = handle => new WorkerProcess(config, handle);

            await pool.AcquireAsync(gx9, CancellationToken.None);
            Assert.Equal(0, bootstraps);

            await pool.AcquireAsync(gx18, CancellationToken.None);
            Assert.Equal(1, bootstraps);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WorkerStartedSubscriberFailure_DoesNotPoisonAcquireOrReload(bool reload)
        {
            var config = new Configuration();
            var kb = new KbHandle("observer-kb", @"C:\Models\ObserverKb");
            var pool = new WorkerPool(config);
            pool.SpawnFactoryForTest = handle => new WorkerProcess(config, handle);
            if (reload) await pool.AcquireAsync(kb, CancellationToken.None);
            pool.OnWorkerStarted += _ => throw new InvalidOperationException("observer failed");

            var worker = reload
                ? await pool.DrainAndReplaceAsync(kb, 1000, CancellationToken.None)
                : await pool.AcquireAsync(kb, CancellationToken.None);

            Assert.Same(worker, pool.TryGet(kb.NormalizedAlias));
            Assert.Same(worker, await pool.AcquireAsync(kb, CancellationToken.None));
        }

        [Theory]
        [InlineData(WorkerStopReason.IdleTimeout)]
        [InlineData(WorkerStopReason.ExplicitClose)]
        public async Task IntentionalExit_LazyReopenBootstrapsOnceWithoutEagerRespawn(WorkerStopReason reason)
        {
            var config = new Configuration();
            var kb = new KbHandle("warm-kb", @"C:\Models\WarmKb");
            int spawns = 0;
            int bootstraps = 0;
            Program.IndexBootstrapTriggerForTest = () => Interlocked.Increment(ref bootstraps);
            Program.StartWorkerForTest(config);
            var pool = Program.GetWorkerPool()!;
            pool.SpawnFactoryForTest = handle =>
            {
                Interlocked.Increment(ref spawns);
                return new WorkerProcess(config, handle);
            };
            var initial = await pool.AcquireAsync(kb, CancellationToken.None);
            // Explicit open also requests bootstrap; a subsequent lazy reopen must
            // not inherit that process's one-shot latch.
            typeof(Program).GetMethod("TriggerIndexBootstrapOnce",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { kb.NormalizedAlias });
            Assert.Equal(1, bootstraps);

            initial.StopWithReason(reason);
            Assert.Null(pool.TryGet(kb.NormalizedAlias));
            Assert.Equal(1, spawns);
            Assert.Equal(1, bootstraps);

            var replacements = await Task.WhenAll(
                System.Linq.Enumerable.Range(0, 8)
                    .Select(_ => pool.AcquireAsync(kb, CancellationToken.None)));
            Assert.All(replacements, replacement => Assert.Same(replacements[0], replacement));
            Assert.NotSame(initial, replacements[0]);
            Assert.Equal(2, spawns);
            Assert.Equal(2, bootstraps);
        }

        [Fact]
        public async Task UnexpectedExit_AbortsAllPendingRequests_ExactlyOnce()
        {
            var config = new Configuration();
            var kb = new KbHandle("crash-kb", @"C:\Models\CrashKb");
            var worker = new WorkerProcess(config, kb);
            var first = Program.AddPendingRequestForTest("pending-1", "crash-kb");
            var second = Program.AddPendingRequestForTest("pending-2", "crash-kb");
            Program.StartWorkerForTest(config);
            Program.GetWorkerPool()!.SpawnFactoryForTest = _ => worker;
            await Program.GetWorkerPool()!.AcquireAsync(kb, CancellationToken.None);

            worker.SimulateUnexpectedExitForTest();
            worker.SimulateUnexpectedExitForTest();

            var results = await Task.WhenAll(first, second);
            Assert.Equal(0, Program.PendingRequestCountForTest);
            Assert.Equal(2, results.Length);
            Assert.All(results, response =>
            {
                Assert.Contains("crashed/exited", response);
                Assert.Contains("\"error\"", response);
            });
        }

        [Fact]
        public async Task UnexpectedExit_RetriesFailedRespawns_ThenRecoversAndBootstrapsReplacementOnly()
        {
            var config = new Configuration();
            var kb = new KbHandle("respawn-kb", @"C:\Models\RespawnKb");
            var initial = new WorkerProcess(config, kb);
            var replacement = new WorkerProcess(config, kb);
            int spawnAttempts = 0;
            int bootstrapCount = 0;
            var delays = new List<TimeSpan>();

            Program.IndexBootstrapTriggerForTest = () => Interlocked.Increment(ref bootstrapCount);
            Program.RespawnDelayForTest = delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            };
            Program.StartWorkerForTest(config);
            var pool = Program.GetWorkerPool()!;
            pool.SpawnFactoryForTest = _ =>
            {
                int attempt = Interlocked.Increment(ref spawnAttempts);
                if (attempt == 1) return initial;
                if (attempt <= 3) throw new InvalidOperationException("deterministic spawn failure");
                return replacement;
            };
            await pool.AcquireAsync(kb, CancellationToken.None);

            initial.SimulateUnexpectedExitForTest();

            await EventuallyAsync(() => ReferenceEquals(pool.TryGet("respawn-kb"), replacement)
                && delays.Count == 2
                && Volatile.Read(ref bootstrapCount) == 2);
            Assert.Equal(4, spawnAttempts);
            Assert.Equal(2, delays.Count);
            Assert.Equal(2, bootstrapCount);
            Assert.Same(replacement, pool.TryGet("respawn-kb"));
        }

        [Fact]
        public async Task UnexpectedExit_PreservesReplacementCreatedByExitSubscriber()
        {
            var config = new Configuration();
            var kb = new KbHandle("replacement-kb", @"C:\Models\ReplacementKb");
            var initial = new WorkerProcess(config, kb);
            var replacement = new WorkerProcess(config, kb);
            var pool = new WorkerPool(config);
            int spawnAttempts = 0;
            pool.SpawnFactoryForTest = _ => ++spawnAttempts == 1 ? initial : replacement;
            pool.OnWorkerExited += (handle, reason) =>
            {
                if (reason != WorkerStopReason.None) return;
                // Reproduce an eager respawn completing before the old exit
                // callback returns, without scheduler timing or real processes.
                pool.DropLiveEntry(handle.NormalizedAlias);
                pool.AcquireAsync(handle, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Same(replacement, pool.TryGet(handle.NormalizedAlias));
            };
            try
            {
                await pool.AcquireAsync(kb, CancellationToken.None);
                initial.SimulateUnexpectedExitForTest();
                Assert.Equal(2, spawnAttempts);
                Assert.Same(replacement, pool.TryGet(kb.NormalizedAlias));
            }
            finally { pool.StopAll(); }
        }

        [Fact]
        public async Task UnexpectedExit_DoesNotRemoveDifferentEntryForSameAlias()
        {
            var config = new Configuration();
            var kb = new KbHandle("replacement-kb", @"C:\Models\ReplacementKb");
            var initial = new WorkerProcess(config, kb);
            var replacement = new WorkerProcess(config, kb);
            var pool = new WorkerPool(config);
            pool.SpawnFactoryForTest = _ => initial;
            try
            {
                await pool.AcquireAsync(kb, CancellationToken.None);
                pool.RegisterForTest(kb, worker: replacement);
                initial.SimulateUnexpectedExitForTest();
                Assert.Same(replacement, pool.TryGet(kb.NormalizedAlias));
            }
            finally { pool.StopAll(); }
        }

        private static async Task EventuallyAsync(Func<bool> condition)
        {
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow >= timeout)
                    throw new TimeoutException("condition was not reached");
                await Task.Delay(10);
            }
        }
    }
}
