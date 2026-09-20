using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Regression coverage for the cold-path index-mirror settle.
    //
    // Live evidence (scratch gateway + real KB, 620 objects, GXMCP_VERBOSE_LOGS=1): right
    // after a KB open the gateway's index mirror held the snapshot restored from the warm
    // cache — `status=Ready freshness=stale opState=Starting mirrorAgeMs=0` — while the
    // worker's delta refresh republished Freshness=current ~250ms later. The read gate only
    // re-probes a mirror older than its 2s floor, so the agent's first index-dependent call
    // received `IndexNotReady` (retryAfterMs=5000) against an index that was already usable:
    // measured 9851ms to the first useful result, with the first call returning no data.
    // The settle opens the gate as soon as the delta actually lands; the gate waits on it
    // while it is in flight.
    public class IndexMirrorSettleTests
    {
        [Fact]
        public async Task Settle_ReturnsImmediately_WhenTheIndexIsAlreadyUsable()
        {
            int refreshes = 0;

            await Program.SettleIndexMirrorAfterBootstrapAsync(
                isUsable: () => true,
                refresh: _ => { refreshes++; return Task.FromResult(true); },
                delayMsOverride: 0);

            // An already-open gate must cost nothing: no round-trip, no delay.
            Assert.Equal(0, refreshes);
        }

        [Fact]
        public async Task Settle_OpensTheGate_AtTheFirstSnapshotThatTurnsUsable()
        {
            bool usable = false;
            int refreshes = 0;
            var attempts = new List<int>();

            await Program.SettleIndexMirrorAfterBootstrapAsync(
                isUsable: () => usable,
                refresh: _ =>
                {
                    refreshes++;
                    // The delta lands on the second refresh, mirroring the live ~250ms case.
                    if (refreshes == 2) usable = true;
                    return Task.FromResult(true);
                },
                delayMsOverride: 0,
                onAttempt: (attempt, wasUsable) => attempts.Add(attempt));

            Assert.Equal(2, refreshes);
            Assert.Equal(new[] { 1, 2 }, attempts);
        }

        [Fact]
        public async Task Settle_StopsAtItsBoundedBudget_WhenTheIndexNeverTurnsUsable()
        {
            int refreshes = 0;

            await Program.SettleIndexMirrorAfterBootstrapAsync(
                isUsable: () => false,
                refresh: _ => { refreshes++; return Task.FromResult(true); },
                delayMsOverride: 0);

            // A genuinely cold KB must never leave this loop alive: the budget is fixed, and
            // the regular gate + retryAfterMs path takes over afterwards.
            Assert.Equal(Program.IndexMirrorSettleMaxAttempts, refreshes);
        }

        [Fact]
        public async Task Settle_KeepsItsBudget_WhenARefreshThrows()
        {
            int refreshes = 0;

            await Program.SettleIndexMirrorAfterBootstrapAsync(
                isUsable: () => false,
                refresh: _ =>
                {
                    refreshes++;
                    // A worker that is exiting/busy must not abort the settle or spin it.
                    throw new InvalidOperationException("worker busy");
                },
                delayMsOverride: 0);

            Assert.Equal(Program.IndexMirrorSettleMaxAttempts, refreshes);
        }

        [Fact]
        public async Task Settle_TreatsAFailedRefreshAsStillWorthRechecking()
        {
            bool usable = false;
            int refreshes = 0;

            await Program.SettleIndexMirrorAfterBootstrapAsync(
                isUsable: () => usable,
                refresh: _ =>
                {
                    refreshes++;
                    // First refresh reports failure yet the worker did republish freshness;
                    // the snapshot — not the refresh's bool — is what decides the gate.
                    if (refreshes == 2) usable = true;
                    return Task.FromResult(false);
                },
                delayMsOverride: 0);

            Assert.Equal(2, refreshes);
        }

        [Fact]
        public void Settle_GateWaitCeilingExceedsTheSettleBudget()
        {
            // The gate waits on an in-flight settle so the agent's first call returns data
            // instead of a retry envelope. That wait must not cut the settle short, or the
            // gate re-opens the exact 2s-floor gap this fix removed.
            int settleBudgetMs = Program.IndexMirrorSettleMaxAttempts * Program.IndexMirrorSettleDelayMs;

            Assert.True(Program.IndexMirrorSettleGateWaitCeilingMs >= settleBudgetMs,
                $"gate wait ceiling {Program.IndexMirrorSettleGateWaitCeilingMs}ms must cover "
                + $"the settle budget {settleBudgetMs}ms");
        }

        // The gate cannot rely on joining the bootstrap's settle: a call that reaches the gate in
        // the same instant the bootstrap publishes it loses that race and fast-fails. Observed
        // intermittently with genexus_search_source as the first call (2 of 3 sessions settled,
        // 1 returned the envelope) while genexus_list_objects happened to win it. These cases pin
        // the state that makes the gate settle the mirror itself, so the outcome is deterministic.
        [Theory]
        [InlineData("Ready", "stale", true)]
        [InlineData("Ready", "Stale", true)]
        [InlineData("Ready", "refreshing", true)]
        // Absent freshness infers `current` from a Ready status (same inference the usability
        // predicate uses), which is usable — that state never reaches the gate.
        [InlineData("Ready", null, false)]
        [InlineData("Ready", "", false)]
        [InlineData("Ready", "current", false)]
        [InlineData("Ready", "Current", false)]
        [InlineData("Cold", "stale", false)]
        [InlineData("Rolling", "stale", false)]
        [InlineData("LiteReady", "stale", false)]
        [InlineData("Enriching", "stale", false)]
        [InlineData(null, null, false)]
        public void RestoredSnapshotAwaitingDelta_IdentifiesOnlyTheReadyButNotCurrentState(
            string? status, string? freshness, bool expected)
        {
            Assert.Equal(expected, Program.IsRestoredSnapshotAwaitingDeltaForTest(status, freshness));
        }

        [Fact]
        public void RestoredSnapshotAwaitingDelta_LeavesAGenuineColdStartOnTheFastFailPath()
        {
            // A first-ever build is the case the immediate envelope plus retryAfterMs exists for,
            // and it must never be mistaken for a restored snapshot awaiting its delta — otherwise
            // every read during a multi-minute build would block on a settle it cannot win.
            Assert.False(Program.IsRestoredSnapshotAwaitingDeltaForTest("Cold", "stale"));
            Assert.False(Program.IsRestoredSnapshotAwaitingDeltaForTest("Indexing", "stale"));
            Assert.False(Program.IsRestoredSnapshotAwaitingDeltaForTest("Unknown", "stale"));
        }

        [Fact]
        public async Task Settle_IsInert_WhenTheFirstSnapshotIsAlreadyUsable()
        {
            // The bootstrap publishes the settle for the gate to wait on. When the index is
            // already warm the settle completes synchronously enough that the gate never
            // blocks — asserted through the observable contract (no refresh round-trips).
            int refreshes = 0;
            var before = Program.IndexMirrorSettleInFlight;

            await Program.SettleIndexMirrorAfterBootstrapAsync(
                isUsable: () => true,
                refresh: _ => { refreshes++; return Task.FromResult(true); },
                delayMsOverride: 0);

            Assert.Equal(0, refreshes);
            Assert.Same(before, Program.IndexMirrorSettleInFlight);
        }

        [Fact]
        public void SettleTasks_AreScopedByKbAlias()
        {
            var kbA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var kbB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Program.SetIndexMirrorSettleForTest("KB-A", kbA.Task);
                Program.SetIndexMirrorSettleForTest("KB-B", kbB.Task);

                Assert.Same(kbA.Task, Program.GetIndexMirrorSettleInFlightForTest("KB-A"));
                Assert.Same(kbB.Task, Program.GetIndexMirrorSettleInFlightForTest("KB-B"));
                Assert.NotSame(Program.GetIndexMirrorSettleInFlightForTest("KB-A"),
                    Program.GetIndexMirrorSettleInFlightForTest("KB-B"));

                Program.SetIndexMirrorSettleForTest("KB-A", replacementA.Task);
                Program.ClearIndexMirrorSettleForTest("KB-A", kbA.Task);
                Assert.Same(replacementA.Task, Program.GetIndexMirrorSettleInFlightForTest("KB-A"));
            }
            finally
            {
                Program.ClearIndexMirrorSettleForTest("KB-A", kbA.Task);
                Program.ClearIndexMirrorSettleForTest("KB-A", replacementA.Task);
                Program.ClearIndexMirrorSettleForTest("KB-B", kbB.Task);
            }
        }

        [Fact]
        public async Task Settle_StopsPromptlyWhenCallerIsCancelled()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            int refreshes = 0;

            await Program.SettleIndexMirrorAfterBootstrapAsync(
                isUsable: () => false,
                refresh: _ => { refreshes++; return Task.FromResult(true); },
                delayMsOverride: 100,
                cancellationToken: cancellation.Token);

            Assert.Equal(0, refreshes);
        }
    }
}
