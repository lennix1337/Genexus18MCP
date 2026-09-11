using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Tests for the async lifecycle build path selection logic (Tasks 4.3 + 4.4).
    ///
    /// The full gateway stack requires a live worker process, so these tests focus on the
    /// <see cref="BuildPathSelector"/> decision function that drives the fast-path/async-path
    /// split. The BackgroundJobRegistry contract is covered separately in
    /// BackgroundJobRegistryTests; piggyback wiring is in PiggybackTests.
    /// </summary>
    public class AsyncBuildTests
    {
        // -----------------------------------------------------------------------
        // BuildPathSelector.UseSync — pure decision function
        // -----------------------------------------------------------------------

        [Fact]
        public void ShortEstimate_BelowThreshold_UsesSync()
        {
            // estimated_seconds=5 < threshold=20 → sync fast-path
            Assert.True(BuildPathSelector.UseSync(estimatedSeconds: 5, thresholdSeconds: 20));
        }

        [Fact]
        public void LongEstimate_AtOrAboveThreshold_UsesAsync()
        {
            // estimated_seconds=60 >= threshold=20 → async path
            Assert.False(BuildPathSelector.UseSync(estimatedSeconds: 60, thresholdSeconds: 20));
        }

        [Fact]
        public void EstimateExactlyAtThreshold_UsesAsync()
        {
            // Boundary: estimated == threshold is NOT below threshold, so async
            Assert.False(BuildPathSelector.UseSync(estimatedSeconds: 20, thresholdSeconds: 20));
        }

        [Fact]
        public void EstimateOneLessThanThreshold_UsesSync()
        {
            // 19 < 20 → sync
            Assert.True(BuildPathSelector.UseSync(estimatedSeconds: 19, thresholdSeconds: 20));
        }

        [Fact]
        public void ZeroThreshold_AlwaysAsync()
        {
            // threshold=0: nothing is "below" zero, every build goes async
            Assert.False(BuildPathSelector.UseSync(estimatedSeconds: 0, thresholdSeconds: 0));
            Assert.False(BuildPathSelector.UseSync(estimatedSeconds: 5, thresholdSeconds: 0));
        }

        [Fact]
        public void VeryHighThreshold_AlwaysSync()
        {
            // threshold=9999: even a 600s estimate is sync
            Assert.True(BuildPathSelector.UseSync(estimatedSeconds: 600, thresholdSeconds: 9999));
        }

        // -----------------------------------------------------------------------
        // ServerConfig default
        // -----------------------------------------------------------------------

        [Fact]
        public void ServerConfig_DefaultThreshold_Is20()
        {
            var cfg = new ServerConfig();
            Assert.Equal(20, cfg.BuildSyncThresholdSeconds);
        }

        // -----------------------------------------------------------------------
        // Default estimate heuristics (documented, not code-tested via full stack,
        // but we verify the thresholds make logical sense with the defaults)
        // -----------------------------------------------------------------------

        [Fact]
        public void DefaultSingleObjectEstimate_60s_IsAsyncWithDefaultThreshold()
        {
            // Single object build defaults to 60s estimated; 60 >= 20 → async
            Assert.False(BuildPathSelector.UseSync(estimatedSeconds: 60, thresholdSeconds: 20));
        }

        [Fact]
        public void DefaultRebuildEstimate_120s_IsAsyncWithDefaultThreshold()
        {
            // RebuildAll defaults to 120s; 120 >= 20 → async
            Assert.False(BuildPathSelector.UseSync(estimatedSeconds: 120, thresholdSeconds: 20));
        }

        [Fact]
        public void CallerCanOptIntoSync_ByPassingLowEstimate()
        {
            // If caller passes estimated_seconds=10, they opt into sync fast-path
            Assert.True(BuildPathSelector.UseSync(estimatedSeconds: 10, thresholdSeconds: 20));
        }

        [Theory]
        [InlineData("build", 1800)]
        [InlineData("build_all", 2700)]
        [InlineData("rebuild", 2700)]
        public void AsyncBuildHardCap_LeavesRoomForWorkerWatchdog(string action, int expectedSeconds)
        {
            Assert.Equal(expectedSeconds, Program.ResolveAsyncBuildHardCapSeconds(action));
        }
    }
}
