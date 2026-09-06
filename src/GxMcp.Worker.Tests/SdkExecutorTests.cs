using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdkExecutorTests
    {
        [Fact]
        public void QueueCapacityUsesSafeBoundsAndFallback()
        {
            string previous = Environment.GetEnvironmentVariable("GXMCP_COMMAND_QUEUE_CAPACITY");
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_COMMAND_QUEUE_CAPACITY", "0");
                Assert.Equal(256, Program.ResolveQueueCapacity("GXMCP_COMMAND_QUEUE_CAPACITY", 256));
                Environment.SetEnvironmentVariable("GXMCP_COMMAND_QUEUE_CAPACITY", "32");
                Assert.Equal(32, Program.ResolveQueueCapacity("GXMCP_COMMAND_QUEUE_CAPACITY", 256));
                Environment.SetEnvironmentVariable("GXMCP_COMMAND_QUEUE_CAPACITY", "99999");
                Assert.Equal(256, Program.ResolveQueueCapacity("GXMCP_COMMAND_QUEUE_CAPACITY", 256));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_COMMAND_QUEUE_CAPACITY", previous);
            }
        }

        [Fact]
        public void OutputAndErrorQueueCapacitiesUseTheSameSafeBounds()
        {
            string previousOutput = Environment.GetEnvironmentVariable("GXMCP_OUTPUT_QUEUE_CAPACITY");
            string previousError = Environment.GetEnvironmentVariable("GXMCP_ERROR_QUEUE_CAPACITY");
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_OUTPUT_QUEUE_CAPACITY", "128");
                Environment.SetEnvironmentVariable("GXMCP_ERROR_QUEUE_CAPACITY", "0");
                Assert.Equal(128, Program.ResolveQueueCapacity("GXMCP_OUTPUT_QUEUE_CAPACITY", 256));
                Assert.Equal(256, Program.ResolveQueueCapacity("GXMCP_ERROR_QUEUE_CAPACITY", 256));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_OUTPUT_QUEUE_CAPACITY", previousOutput);
                Environment.SetEnvironmentVariable("GXMCP_ERROR_QUEUE_CAPACITY", previousError);
            }
        }

        [Fact]
        public async Task DispatchRunsOnOwnerCallbackAndReentrantInvokeDoesNotDeadlock()
        {
            var posted = new ConcurrentQueue<System.Action>();
            var executor = new SdkExecutor(() => false, action => { posted.Enqueue(action); return true; }, 8);
            try
            {
                var outer = executor.InvokeAsync(() =>
                {
                    var nested = new SdkExecutor(() => true, _ => true, 1).InvokeAsync(() => 42);
                    return nested.Result;
                });
                Assert.True(posted.TryDequeue(out var callback));
                callback!();
                Assert.Equal(42, await outer);
            }
            finally { executor.Dispose(); }
        }

        [Fact]
        public async Task FullAdmissionReturnsBusyWithoutPosting()
        {
            var posted = new ConcurrentQueue<System.Action>();
            var executor = new SdkExecutor(() => false, action => { posted.Enqueue(action); return true; }, 1);
            try
            {
                var first = executor.InvokeAsync(() => 1);
                var second = executor.InvokeAsync(() => 2);
                Assert.True(second.IsFaulted);
                Assert.IsType<SdkBusyException>(second.Exception!.InnerException);
                Assert.Single(posted);
                posted.TryDequeue(out var callback);
                callback!();
                Assert.Equal(1, await first);
            }
            finally { executor.Dispose(); }
        }

        [Fact]
        public void CancellationBeforeDispatchDoesNotRunSdkOperation()
        {
            var posted = new ConcurrentQueue<System.Action>();
            var executor = new SdkExecutor(() => false, action => { posted.Enqueue(action); return true; }, 1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            int calls = 0;
            var task = executor.InvokeAsync(() => { calls++; return 1; }, cts.Token);
            Assert.True(task.IsCanceled);
            Assert.Equal(0, calls);
            Assert.Empty(posted);
            executor.Dispose();
        }

        [Fact]
        public async Task DisposeCancelsPostedCallbackWithoutRunningSdkOperation()
        {
            var posted = new ConcurrentQueue<System.Action>();
            var executor = new SdkExecutor(() => false, action => { posted.Enqueue(action); return true; }, 1);
            int calls = 0;

            var task = executor.InvokeAsync(() => { Interlocked.Increment(ref calls); return 1; });
            executor.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
            Assert.True(posted.TryDequeue(out var callback));
            callback!();
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task CancellationAfterEnqueueReleasesSlotBeforeCallbackRuns()
        {
            var posted = new ConcurrentQueue<System.Action>();
            var executor = new SdkExecutor(() => false, action => { posted.Enqueue(action); return true; }, 1);
            using var cts = new CancellationTokenSource();
            int calls = 0;

            var cancelled = executor.InvokeAsync(() => { Interlocked.Increment(ref calls); return 1; }, cts.Token);
            cts.Cancel();
            Assert.True(cancelled.IsCanceled);

            // The first callback is still queued, but its admission slot is no
            // longer held. A second call can be admitted before the first drains.
            var accepted = executor.InvokeAsync(() => 2);
            Assert.Equal(2, posted.Count);
            Assert.True(posted.TryDequeue(out var first));
            first!();
            Assert.True(posted.TryDequeue(out var second));
            second!();

            Assert.Equal(2, await accepted);
            Assert.Equal(0, calls);
            executor.Dispose();
        }

        [Fact]
        public async Task FailedPostReleasesAdmissionSlot()
        {
            bool allowPost = false;
            var posted = new ConcurrentQueue<System.Action>();
            var executor = new SdkExecutor(() => false, action =>
            {
                if (!allowPost) return false;
                posted.Enqueue(action);
                return true;
            }, 1);
            try
            {
                var failed = executor.InvokeAsync(() => 1);
                Assert.True(failed.IsFaulted);
                Assert.IsType<SdkBusyException>(failed.Exception!.InnerException);

                allowPost = true;
                var accepted = executor.InvokeAsync(() => 2);
                Assert.True(posted.TryDequeue(out var callback));
                callback!();
                Assert.Equal(2, await accepted);
            }
            finally { executor.Dispose(); }
        }
    }
}
