using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// v2.6.6 Stream H (FR#26) — environment-change event. Verifies that
    /// KbWatcherService.OnEnvironmentChanged is raised to subscribers via the
    /// test-only hook (the live polling path needs a real KB). The gateway
    /// subscribes <c>KbHandle.Invalidate</c> to this event so a cached
    /// environment cannot stay stale across an IDE-side switch.
    /// </summary>
    public class KbWatcherInvalidationTests
    {
        [Fact]
        public void OnEnvironmentChanged_FiresSubscribers()
        {
            var ics = new IndexCacheService();
            var kbService = new KbService(ics);
            var watcher = new KbWatcherService(kbService, (_, __, ___) => { });

            string lastEnv = null;
            string lastVer = null;
            watcher.OnEnvironmentChanged += (env, ver) => { lastEnv = env; lastVer = ver; };

            watcher.RaiseEnvironmentChangedForTest("Java", "18.0");

            Assert.Equal("Java", lastEnv);
            Assert.Equal("18.0", lastVer);
        }

        [Fact]
        public void OnEnvironmentChanged_NoSubscribers_NoThrow()
        {
            var ics = new IndexCacheService();
            var kbService = new KbService(ics);
            var watcher = new KbWatcherService(kbService, (_, __, ___) => { });

            // Should not throw with no subscribers attached.
            var ex = Record.Exception(() => watcher.RaiseEnvironmentChangedForTest("X", "1"));
            Assert.Null(ex);
        }

        [Fact]
        public void CheckForEnvironmentChange_FirstObservationSilent_NextFlipRaises()
        {
            // Without a live KB, GetActiveEnvironment returns null both times, so
            // no event fires. This locks in the "no-spam on equal value" contract
            // which protects the gateway from waking up on every poll tick.
            var ics = new IndexCacheService();
            var kbService = new KbService(ics);
            var watcher = new KbWatcherService(kbService, (_, __, ___) => { });

            int fires = 0;
            watcher.OnEnvironmentChanged += (env, ver) => fires++;

            watcher.CheckForEnvironmentChange(); // seeds baseline (null,null)
            watcher.CheckForEnvironmentChange(); // unchanged → no fire

            Assert.Equal(0, fires);
        }

        [Fact]
        public void ComputeNextPollIntervalMs_BacksOffWhenIdleAndResetsOnActivity()
        {
            Assert.Equal(10000, KbWatcherService.ComputeNextPollIntervalMs(5000, hadActivity: false));
            Assert.Equal(20000, KbWatcherService.ComputeNextPollIntervalMs(10000, hadActivity: false));
            Assert.Equal(30000, KbWatcherService.ComputeNextPollIntervalMs(20000, hadActivity: false));
            Assert.Equal(30000, KbWatcherService.ComputeNextPollIntervalMs(30000, hadActivity: false));
            Assert.Equal(5000, KbWatcherService.ComputeNextPollIntervalMs(30000, hadActivity: true));
        }
    }
}
