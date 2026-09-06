using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Nominal affinity oracles for the shared SDK gate and executor. These tests
    /// stay SDK-free: the real GeneXus live gate remains responsible for proving
    /// that the same boundary is used by the installed SDK.
    /// </summary>
    public sealed class SdkAffinityTests
    {
        [Fact]
        public void SdkGateIsReentrantAndReportsOwnership()
        {
            Assert.False(SdkGate.IsHeldByCurrentThread);
            using (SdkGate.Enter())
            {
                Assert.True(SdkGate.IsHeldByCurrentThread);
                using (SdkGate.Enter())
                {
                    Assert.True(SdkGate.IsHeldByCurrentThread);
                }
                Assert.True(SdkGate.IsHeldByCurrentThread);
            }
            Assert.False(SdkGate.IsHeldByCurrentThread);
        }

        [Fact]
        public void SdkGateTryEnterFailsFastForAnotherThread()
        {
            using (SdkGate.Enter())
            {
                bool acquired = false;
                var thread = new Thread(() => acquired = SdkGate.TryEnter(25) != null);
                thread.Start();
                thread.Join();
                Assert.False(acquired);
            }

            using (SdkGate.TryEnter(1000)) { }
        }

        [Fact]
        public async Task SdkExecutorPublishesAllCallsOnTheOwnerCallback()
        {
            var callbacks = new ConcurrentQueue<Action>();
            var executor = new SdkExecutor(
                isOwnerThread: () => false,
                post: callback => { callbacks.Enqueue(callback); return true; },
                capacity: 4);
            try
            {
                var results = new List<Task<int>>();
                for (int i = 0; i < 3; i++)
                    results.Add(executor.InvokeAsync(() => Thread.CurrentThread.ManagedThreadId));

                var callbackThreadIds = new HashSet<int>();
                while (callbacks.TryDequeue(out var callback))
                {
                    callbackThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
                    callback();
                }

                Assert.Single(callbackThreadIds);
                Assert.Equal(3, results.Count(task => task.Status == TaskStatus.RanToCompletion));
                Assert.All(results, task => Assert.Equal(callbackThreadIds.Single(), task.Result));
                await Task.WhenAll(results);
            }
            finally
            {
                executor.Dispose();
            }
        }
    }
}
