using System;
using System.Collections.Generic;
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
        }

        public void Dispose() => Program.ResetWorkerLifecycleForTest();

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
                && Volatile.Read(ref bootstrapCount) == 1);
            Assert.Equal(4, spawnAttempts);
            Assert.Equal(2, delays.Count);
            Assert.Equal(1, bootstrapCount);
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
