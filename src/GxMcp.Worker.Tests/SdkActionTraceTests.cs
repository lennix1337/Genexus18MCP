using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdkActionTraceTests
    {
        [Fact]
        public async Task RunningActionIsVisibleAndExceptionClosesEvent()
        {
            var events = new ConcurrentQueue<string>();
            var trace = new SdkActionTrace(events.Enqueue);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var task = Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
                    trace.Run("source-store-backfill", () =>
                    {
                        entered.Set();
                        release.Wait();
                        throw new InvalidOperationException();
                    })));
                try
                {
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                    Assert.Equal("id=1 action=source-store-backfill", trace.Active);
                    Assert.Contains("[STA-ACTION] begin id=1 action=source-store-backfill", events);
                }
                finally { release.Set(); }
                await task;
                Assert.Null(trace.Active);
                Assert.Contains(events, e => e.StartsWith("[STA-ACTION] end id=1 action=source-store-backfill elapsedMs="));
            }
        }
    }
}
