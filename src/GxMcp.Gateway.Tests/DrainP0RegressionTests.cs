using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class DrainP0RegressionTests
    {
        private static Configuration Config() => new Configuration
        {
            Server = new ServerConfig { MaxOpenKbs = 5 }
        };

        private static KbHandle Handle(string alias = "kb1") => new KbHandle(alias, "C:/" + alias);

        [Fact]
        public async Task DrainTimeoutWithLiveWorker_DoesNotSpawnReplacement()
        {
            var pool = new WorkerPool(Config());
            var handle = Handle();
            int spawns = 0;
            pool.SpawnFactoryForTest = h =>
            {
                Interlocked.Increment(ref spawns);
                var worker = new WorkerProcess(Config(), h);
                worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
                return worker;
            };

            var old = await pool.AcquireAsync(handle, CancellationToken.None);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                pool.DrainAndReplaceAsync(handle, 10, CancellationToken.None));

            Assert.Equal(1, spawns);
            var acquire = pool.AcquireAsync(handle, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => acquire);
            Assert.Same(old, pool.TryGet(handle.Alias));
            Assert.False(pool.IsDrainingForTest(handle.Alias));
        }

        [Fact]
        public async Task StopFailure_CleansUpDrainAndSignalsWaiters_WithoutReplacement()
        {
            var pool = new WorkerPool(Config());
            var handle = Handle();
            int spawns = 0;
            pool.SpawnFactoryForTest = h =>
            {
                Interlocked.Increment(ref spawns);
                var worker = new WorkerProcess(Config(), h);
                worker.StopFailureForTest = _ => new InvalidOperationException("stop failed");
                return worker;
            };

            var old = await pool.AcquireAsync(handle, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pool.DrainAndReplaceAsync(handle, 100, CancellationToken.None));

            Assert.Equal(1, spawns);
            var acquire = pool.AcquireAsync(handle, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => acquire);
            Assert.Same(old, pool.TryGet(handle.Alias));
            Assert.True(old.CancellationRequestedForTest);
            Assert.False(pool.IsDrainingForTest(handle.Alias));
        }

        [Fact]
        public async Task DeadReplacement_IsNotRegisteredAsLiveWorker()
        {
            var pool = new WorkerPool(Config());
            var handle = Handle();
            int spawns = 0;
            pool.SpawnFactoryForTest = h =>
            {
                Interlocked.Increment(ref spawns);
                var worker = new WorkerProcess(Config(), h);
                worker.SetProcessStateForTest(alive: spawns == 1, exitConfirmed: spawns == 1);
                return worker;
            };

            await pool.AcquireAsync(handle, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pool.DrainAndReplaceAsync(handle, 100, CancellationToken.None));

            Assert.Equal(2, spawns);
            Assert.Null(pool.TryGet(handle.Alias));
        }

        [Fact]
        public async Task ConcurrentReloadsForAlias_OnlyOneOwnsDrain()
        {
            var pool = new WorkerPool(Config());
            var handle = Handle();
            int spawns = 0;
            pool.SpawnFactoryForTest = h =>
            {
                Interlocked.Increment(ref spawns);
                var worker = new WorkerProcess(Config(), h);
                worker.SetProcessStateForTest(alive: true, exitConfirmed: true);
                return worker;
            };

            await pool.AcquireAsync(handle, CancellationToken.None);
            var hookEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = pool.DrainAndReplaceAsync(handle, 1000, CancellationToken.None, async _ =>
            {
                hookEntered.TrySetResult(true);
                await release.Task;
            });
            await hookEntered.Task;
            var second = pool.DrainAndReplaceAsync(handle, 1000, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            release.TrySetResult(true);
            await first;
            Assert.Equal(2, spawns);
        }
    }
}
