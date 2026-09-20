using System;
using System.Collections.Generic;
using System.Linq;
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
        [InlineData("genexus_inspect")]
        [InlineData("genexus_navigation")]
        [InlineData("genexus_security")]
        public void IndexDependentReads_AreBlockedUntilIndexIsUsable(string toolName)
        {
            Assert.True(Program.IsIndexDependentToolForTest(toolName));
        }

        // The gate is applied to the name the request loop settled on, which is AFTER the legacy
        // alias rewrite (Program.RequestLoop, default ON). A gate entry the rewrite consumes
        // therefore never matches, and its umbrella is what actually needs the gate.
        //
        // KNOWN GAP (documented, deliberately not "fixed" here): the three entries below are in
        // that state, and their umbrellas — genexus_db and genexus_versioning — are NOT gated, so
        // the protection was silently dropped when those tools were consolidated. Closing it
        // needs per-action gating, because the umbrellas mix index-dependent actions (types_*,
        // drift_*, diff_generated) with ones that clearly do not (records_query runs SQL against
        // the database, history_save writes a snapshot); gating the whole umbrella would
        // fail-close on calls that work today. See
        // docs/benchmarks/2026-09-20-live-rounds.md for the measurement.
        //
        // This list exists only to record that already-measured state. Adding to it is not a fix:
        // a newly consumed entry fails this test instead of quietly joining the gap.
        private static readonly string[] KnownAliasConsumedGateEntries =
        {
            "genexus_db_drift",
            "genexus_types",
            "genexus_diff_generated"
        };

        [Fact]
        public void NoNewGateEntryIsConsumedByTheLegacyAliasRewrite()
        {
            var consumed = Program.IndexDependentTools
                .Where(name => McpRouter.TryRewriteLegacyTool(name, new JObject(), out _, out _))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(KnownAliasConsumedGateEntries.OrderBy(n => n, StringComparer.Ordinal), consumed);
        }

        [Fact]
        public void GateEntriesStillCoverTheCanonicalReadTools()
        {
            // Guards the other direction: a refactor that reintroduces a switch/set must not drop
            // any tool the gate is measured and documented to protect.
            var expected = new[]
            {
                "genexus_analyze", "genexus_inspect", "genexus_list_objects",
                "genexus_navigation", "genexus_query", "genexus_read",
                "genexus_search_source", "genexus_security"
            };

            foreach (var name in expected) Assert.Contains(name, Program.IndexDependentTools);
        }

        [Theory]
        [InlineData("genexus_db", "drift_check", true)]
        [InlineData("genexus_db", "drift_report", true)]
        [InlineData("genexus_db", "types_list", true)]
        [InlineData("genexus_db", "types_describe", true)]
        [InlineData("genexus_db", "types_validate", true)]
        [InlineData("genexus_db", "records_query", false)]
        [InlineData("genexus_db", "sql_ddl", false)]
        [InlineData("genexus_db", "sample_data", false)]
        [InlineData("genexus_versioning", "diff_generated", true)]
        [InlineData("genexus_versioning", "diff", false)]
        public void CanonicalUmbrellaActionsHaveTheCorrectIndexPolicy(
            string toolName, string action, bool expected)
        {
            Assert.Equal(expected, Program.IsIndexDependentToolForTest(
                toolName, new JObject { ["action"] = action }));
        }

        [Fact]
        public void LegacyIndexAliasesRemainGatedAfterRewrite()
        {
            foreach (var legacy in new[]
            {
                "genexus_db_drift", "genexus_types", "genexus_diff_generated"
            })
            {
                Assert.True(McpRouter.TryRewriteLegacyTool(legacy, new JObject(),
                    out string rewrittenName, out JObject rewrittenArgs));
                Assert.True(Program.IsIndexDependentToolForTest(rewrittenName, rewrittenArgs));
            }
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
            Assert.True(stalled["workerAlive"]?.ToObject<bool>() == true);
            Assert.True(stalled["recoverable"]?.ToObject<bool>() == true);
            Assert.Contains("action=index force=true", stalled["hint"]?.ToString());
        }

        [Theory]
        [InlineData("Starting", false, false)]
        [InlineData("Building", true, false)]
        [InlineData("Building", false, true)]
        [InlineData("WorkerExited", false, true)]
        public void IndexNotReadyEnvelope_OnlySuggestsForceForRecoverableActivity(
            string operationState, bool workerAlive, bool expectRecoveryHint)
        {
            JObject envelope = Program.BuildIndexNotReadyEnvelopeForTest(
                status: "Cold", freshness: "stale", totalObjects: 0,
                progress: null, etaMs: null, operationId: "idx-active",
                operationState: operationState, workerAlive: workerAlive);

            Assert.Equal(expectRecoveryHint, envelope["recoverable"]?.ToObject<bool>() == true);
            Assert.Equal(expectRecoveryHint,
                envelope["hint"]?.ToString()?.Contains("action=index force=true") == true);
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
        [InlineData("stale", "Idle", false, true)]
        [InlineData("current", "Idle", false, false)]
        [InlineData("refreshing", "Building", true, false)]
        [InlineData("stale", "Starting", false, false)]
        public void ReadyIndex_RecoveryDistinguishesIdleStaleFromCompletedOrStarting(
            string freshness, string operationState, bool workerAlive, bool recoverable)
        {
            var envelope = Program.BuildIndexNotReadyEnvelopeForTest(
                "Ready", freshness, 1200, null, null,
                operationState: operationState, workerAlive: workerAlive);
            Assert.Equal(recoverable, envelope["recoverable"]!.Value<bool>());
            Assert.Equal(recoverable, envelope["hint"]!.Value<string>()!.Contains("action=index force=false"));
            Assert.DoesNotContain("force=true", envelope["hint"]!.Value<string>()!);
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
