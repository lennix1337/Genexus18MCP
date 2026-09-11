using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Plan 031: DrainAndReplaceAsync used to remove the pool entry (or rely on the
    // dying worker's OnWorkerExited handler removing it first, synchronously, since
    // WorkerProcess.StopProcess fires that handler before returning). A concurrent
    // AcquireAsync racing the removal could GetOrAdd a brand-new, non-draining entry
    // for the same alias, spawn a SECOND real worker, and never see the replacement
    // DrainAndReplaceAsync installs on its own (now-orphaned) entry. Fixed by keeping
    // the entry (and Draining=true) alive across the whole drain window and having the
    // exited-worker handler skip removal while Draining is set.
    public class DrainWindowConcurrencyTests
    {
        private static Configuration CfgWithMax(int max) =>
            new Configuration { Server = new ServerConfig { MaxOpenKbs = max } };

        [Fact]
        public async Task DrainAndReplaceAsync_SwapsWorker_WithoutSpawningSecondEntry()
        {
            var pool = new WorkerPool(CfgWithMax(5));
            var handle = new KbHandle("kb1", "C:/KB1");

            int spawnCount = 0;
            pool.SpawnFactoryForTest = h =>
            {
                Interlocked.Increment(ref spawnCount);
                return new WorkerProcess(CfgWithMax(5), h);
            };

            var firstWorker = await pool.AcquireAsync(handle, CancellationToken.None);
            Assert.Equal(1, spawnCount);

            var newWorker = await pool.DrainAndReplaceAsync(handle, drainTimeoutMs: 1000, CancellationToken.None);

            Assert.Equal(2, spawnCount);
            Assert.NotSame(firstWorker, newWorker);
            // Only one live entry for the alias — no orphaned second entry was created.
            Assert.Same(newWorker, pool.TryGet("kb1"));
        }

        [Fact]
        public async Task ConcurrentAcquire_DuringDrain_WaitsAndReturnsReplacementWorker_NoExtraSpawn()
        {
            var pool = new WorkerPool(CfgWithMax(5));
            var handle = new KbHandle("kb1", "C:/KB1");

            int spawnCount = 0;
            pool.SpawnFactoryForTest = h =>
            {
                Interlocked.Increment(ref spawnCount);
                return new WorkerProcess(CfgWithMax(5), h);
            };

            var firstWorker = await pool.AcquireAsync(handle, CancellationToken.None);
            Assert.Equal(1, spawnCount);

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hookEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var drainTask = pool.DrainAndReplaceAsync(handle, drainTimeoutMs: 1000, CancellationToken.None,
                afterDrainBeforeSpawn: async _ =>
                {
                    hookEntered.TrySetResult(true);
                    // Hold the drain window open while a concurrent AcquireAsync races in.
                    await gate.Task.ConfigureAwait(false);
                });

            // Wait until the drain has actually started (old worker stopped, hook entered)
            // before firing the concurrent acquire, so it reliably hits the Draining wait path.
            await hookEntered.Task;

            var acquireTask = pool.AcquireAsync(handle, CancellationToken.None);
            await Task.Delay(50);
            Assert.False(acquireTask.IsCompleted, "AcquireAsync should be blocked on the live drain, not racing a fresh spawn.");

            gate.TrySetResult(true);

            var newWorker = await drainTask;
            var acquiredWorker = await acquireTask;

            Assert.Equal(2, spawnCount); // exactly one respawn — no orphaned second worker.
            Assert.Same(newWorker, acquiredWorker);
            Assert.NotSame(firstWorker, acquiredWorker);
        }

        [Fact]
        public async Task ConcurrentAcquire_AfterDrainTimeout_FailsClosedWithoutReturningClosingWorker()
        {
            var pool = new WorkerPool(CfgWithMax(5));
            var handle = new KbHandle("kb1", "C:/KB1");
            pool.SpawnFactoryForTest = h =>
            {
                var worker = new WorkerProcess(CfgWithMax(5), h);
                worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
                return worker;
            };

            var oldWorker = await pool.AcquireAsync(handle, CancellationToken.None);
            var drainTask = pool.DrainAndReplaceAsync(handle, drainTimeoutMs: 25, CancellationToken.None);
            var acquireTask = pool.AcquireAsync(handle, CancellationToken.None);

            await Assert.ThrowsAsync<TimeoutException>(() => drainTask);
            await Assert.ThrowsAsync<InvalidOperationException>(() => acquireTask);
            Assert.Same(oldWorker, pool.TryGet(handle.Alias));
        }
    }
}
