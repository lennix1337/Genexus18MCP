using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class IndexGateToolPolicyTests
    {
        [Theory]
        [InlineData("genexus_list_objects")]
        [InlineData("genexus_query")]
        [InlineData("genexus_read")]
        [InlineData("genexus_search_source")]
        [InlineData("genexus_analyze")]
        [InlineData("genexus_navigation")]
        public void IndexDependentReads_AreBlockedUntilIndexIsUsable(string toolName)
        {
            Assert.True(Program.IsIndexDependentToolForTest(toolName));
        }

        [Theory]
        [InlineData("genexus_edit")]
        [InlineData("genexus_edit_form")]
        [InlineData("genexus_edit_and_build")]
        [InlineData("genexus_create_object")]
        [InlineData("genexus_create_popup")]
        [InlineData("genexus_apply_pattern")]
        public void Mutations_RemainAvailableWhileIndexBuilds(string toolName)
        {
            Assert.False(Program.IsIndexDependentToolForTest(toolName));
        }

        [Fact]
        public void IndexNotReadyResponses_AreNeverSemanticCached()
        {
            Assert.True(Program.IsTransientResponseForCacheForTest(
                JObject.Parse("{'status':'Indexing','code':'IndexNotReady'}")));
        }

        // Issue #209 (policy A): the gate stays fail-closed, but its envelope must be
        // observable (state named), awaitable (the one blocking call named) and retryable.
        [Fact]
        public void IndexNotReadyEnvelope_NamesStateAndWaitAndAlwaysCarriesRetryHint()
        {
            JObject warmStart = Program.BuildIndexNotReadyEnvelopeForTest(
                status: "Ready", freshness: "refreshing", totalObjects: 1200, progress: null, etaMs: null);

            Assert.Equal("IndexNotReady", warmStart["code"]?.ToString());
            Assert.Equal("Indexing", warmStart["status"]?.ToString());
            // The real status is reported (matching whoami); `freshness` explains the block.
            Assert.Equal("Ready", warmStart["indexStatus"]?.ToString());
            Assert.Equal("refreshing", warmStart["freshness"]?.ToString());
            Assert.Equal(1200, warmStart["totalObjects"]?.ToObject<int>());
            // No ETA yet: the fallback still tells the caller how long to back off.
            Assert.Equal(Program.DefaultIndexRetryAfterMs, warmStart["retryAfterMs"]?.ToObject<int>());
            Assert.Contains("freshness=current", warmStart["hint"]?.ToString());
            Assert.False(warmStart.ContainsKey("etaMs"));
        }

        [Fact]
        public void IndexNotReadyEnvelope_PrefersWorkerEtaAndKeepsCurrentStatusName()
        {
            JObject indexing = Program.BuildIndexNotReadyEnvelopeForTest(
                status: "Reindexing", freshness: "stale", totalObjects: 0, progress: 0.25, etaMs: 12000);

            Assert.Equal("Reindexing", indexing["indexStatus"]?.ToString());
            Assert.Equal(12000, indexing["retryAfterMs"]?.ToObject<int>());
            Assert.Equal(12000, indexing["etaMs"]?.ToObject<int>());
            Assert.Equal(0.25, indexing["progress"]?.ToObject<double>());
        }

        [Fact]
        public void IndexNotReadyEnvelope_ExposesRecoverableStalledOperation()
        {
            JObject stalled = Program.BuildIndexNotReadyEnvelopeForTest(
                status: "Reindexing", freshness: "refreshing", totalObjects: 1200,
                progress: 0.25, etaMs: null, operationId: "idx-123",
                operationState: "Stalled", workerAlive: true);

            Assert.Equal("idx-123", stalled["operationId"]?.ToString());
            Assert.Equal("Stalled", stalled["operationState"]?.ToString());
            Assert.True((bool)stalled["workerAlive"]);
            Assert.True((bool)stalled["recoverable"]);
            Assert.Contains("action=index force=true", stalled["hint"]?.ToString());
        }

        [Theory]
        [InlineData("Cold", true)]
        [InlineData(null, true)]
        [InlineData("Ready", false)]
        [InlineData("Reindexing", false)]
        public void IndexNotReadyEnvelope_NamesTheManualRecoveryOnlyWhenNothingIsProgressing(string? status, bool expectRecoveryHint)
        {
            JObject envelope = Program.BuildIndexNotReadyEnvelopeForTest(
                status: status, freshness: "stale", totalObjects: 0, progress: null, etaMs: null);

            bool hinted = envelope["hint"]?.ToString()?.Contains("action=index force=true") == true;
            Assert.Equal(expectRecoveryHint, hinted);
            // Either way the awaitable half stays the first instruction.
            Assert.Contains("freshness=current", envelope["hint"]?.ToString());
        }

        [Theory]
        [InlineData("IndexNotReady")]
        [InlineData("Reindexing")]
        [InlineData("IndexCold")]
        [InlineData("Indexing")]
        [InlineData("Timeout")]
        [InlineData("Cancelled")]
        [InlineData("BuildPlanTooLarge")]
        [InlineData("Running")]
        public void CanonicalTransientCodes_AreNeverSemanticCached(string code)
        {
            Assert.True(Program.IsTransientResponseForCacheForTest(
                JObject.Parse($"{{'status':'ok','code':'{code}'}}")));
        }
    }
}
