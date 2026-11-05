using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Required gate unit tests for the Worker_Scheduler (Requirement 13 criterion 1).
    // They need no GeneXus SDK, license, or KB: SharedHostScheduler is pure and single-threaded,
    // and the clock is injected.
    //
    // Contract these tests pin for SharedHostScheduler and its records (design.md, "SharedHostScheduler.cs"):
    // - SchedulerLimits(queueDepth, sessionQueueCap, queueWaitMs) with QueueDepth, SessionQueueCap, QueueWaitMs.
    // - SubmitResult: Started (the request owns the dispatch window now), Queued (appended to the
    //   Request_Queue), RejectReason ("queue-full" or "session-queue-limit", null otherwise).
    // - CancelResult.Removed: the queued requests of the cancelling Session that matched by
    //   ClientRequestIdJson or by a non-null CancelToken; empty when nothing queued matched.
    // - ExpiredRequest: Request and Position, the 1-based position in start order at removal time.
    // - SchedulerSnapshot: Running (null when the window is free), Queued (queued requests in start
    //   order), WaitReason (why the queued requests wait).
    // - NextDeadlineMs: earliest EnqueuedAtMs + QueueWaitMs over queued requests, null when none is queued.
    public class SharedHostSchedulerTests
    {
        private const int Wait = 30000;

        private long _now;
        private long _sequence;

        private SharedHostScheduler NewScheduler(int depth = 8, int cap = 4, int waitMs = Wait)
        {
            return new SharedHostScheduler(new SchedulerLimits(depth, cap, waitMs), () => _now);
        }

        private QueuedRequest Req(string label, string session, string clientId = "1", string? cancelToken = null)
        {
            return new QueuedRequest
            {
                ChildRequestId = label,
                SessionId = session,
                ClientRequestIdJson = clientId,
                Operation = "genexus_read/source",
                CancelToken = cancelToken,
                EnqueuedAtMs = _now,
                Sequence = ++_sequence,
                Payload = "{}"
            };
        }

        private static List<string> QueuedLabels(SharedHostScheduler scheduler)
        {
            return scheduler.Snapshot().Queued.Select(r => r.ChildRequestId).ToList();
        }

        // Completes the running request repeatedly and returns the labels in start order.
        private static List<string> DrainFrom(SharedHostScheduler scheduler, string runningLabel)
        {
            var order = new List<string>();
            var next = scheduler.Complete(runningLabel);
            while (next != null)
            {
                order.Add(next.ChildRequestId);
                next = scheduler.Complete(next.ChildRequestId);
            }
            return order;
        }

        private static void StartRunning(SharedHostScheduler scheduler, QueuedRequest request)
        {
            var result = scheduler.Submit(request);
            Assert.True(result.Started);
        }

        [Fact]
        public void Submit_WhenIdleAndQueueEmpty_StartsImmediatelyWithoutEnqueueing()
        {
            var scheduler = NewScheduler();

            var result = scheduler.Submit(Req("a1", "A"));

            Assert.True(result.Started);
            Assert.False(result.Queued);
            Assert.Null(result.RejectReason);
            var snapshot = scheduler.Snapshot();
            Assert.Equal("a1", snapshot.Running!.ChildRequestId);
            Assert.Empty(snapshot.Queued);
            Assert.Null(scheduler.NextDeadlineMs);
        }

        [Fact]
        public void Submit_WhileRunning_QueuesWithoutBusyEnvelope()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("x0", "X"));

            var result = scheduler.Submit(Req("a1", "A"));

            Assert.False(result.Started);
            Assert.True(result.Queued);
            Assert.Null(result.RejectReason);
            Assert.Equal(new[] { "a1" }, QueuedLabels(scheduler));
        }

        [Fact]
        public void StartOrder_TwoSessions_IsA1B1A2B2A3()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("x0", "X"));
            foreach (var r in new[] { Req("a1", "A"), Req("a2", "A"), Req("a3", "A"), Req("b1", "B"), Req("b2", "B") })
            {
                Assert.True(scheduler.Submit(r).Queued);
            }

            Assert.Equal(new[] { "a1", "b1", "a2", "b2", "a3" }, DrainFrom(scheduler, "x0"));
        }

        [Fact]
        public void StartOrder_ThreeSessions_RotatesRoundRobinInFirstEnqueueOrder()
        {
            var scheduler = NewScheduler(depth: 8, cap: 3);
            StartRunning(scheduler, Req("x0", "X"));
            foreach (var r in new[]
            {
                Req("a1", "A"), Req("a2", "A"), Req("a3", "A"),
                Req("b1", "B"), Req("b2", "B"),
                Req("c1", "C"), Req("c2", "C"), Req("c3", "C")
            })
            {
                Assert.True(scheduler.Submit(r).Queued);
            }

            Assert.Equal(
                new[] { "a1", "b1", "c1", "a2", "b2", "c2", "a3", "c3" },
                DrainFrom(scheduler, "x0"));
        }

        [Fact]
        public void ExpireDue_RemovesRequestOlderThanQueueWait_WithOneBasedStartOrderPosition()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("a0", "A"));
            _now = 0;
            scheduler.Submit(Req("a1", "A"));
            _now = 10;
            scheduler.Submit(Req("a2", "A"));
            _now = 20;
            scheduler.Submit(Req("b1", "B"));

            // a1 starts, Session A rotates behind B: start order is now b1, a2.
            var started = scheduler.Complete("a0");
            Assert.Equal("a1", started!.ChildRequestId);
            Assert.Equal(new[] { "b1", "a2" }, QueuedLabels(scheduler));

            _now = 10 + Wait - 1;
            Assert.Empty(scheduler.ExpireDue());

            _now = 10 + Wait + 1;
            var expired = scheduler.ExpireDue();

            var only = Assert.Single(expired);
            Assert.Equal("a2", only.Request.ChildRequestId);
            Assert.Equal(2, only.Position);
            Assert.Equal(new[] { "b1" }, QueuedLabels(scheduler));
            Assert.Equal(new[] { "b1" }, DrainFrom(scheduler, "a1"));
        }

        [Fact]
        public void Submit_WhenQueueHoldsDepth_RejectsQueueFullWithoutEnqueueing()
        {
            var scheduler = NewScheduler(depth: 2, cap: 2);
            StartRunning(scheduler, Req("x0", "X"));
            scheduler.Submit(Req("a1", "A"));
            scheduler.Submit(Req("b1", "B"));
            var deadlineBefore = scheduler.NextDeadlineMs;

            _now = 5;
            var result = scheduler.Submit(Req("c1", "C"));

            Assert.False(result.Started);
            Assert.False(result.Queued);
            Assert.Equal("queue-full", result.RejectReason);
            Assert.Equal(new[] { "a1", "b1" }, QueuedLabels(scheduler));
            Assert.Equal(deadlineBefore, scheduler.NextDeadlineMs);
        }

        [Fact]
        public void Submit_WhenQueueFullAndSessionAtCap_ReportsQueueFirst()
        {
            var scheduler = NewScheduler(depth: 2, cap: 2);
            StartRunning(scheduler, Req("x0", "X"));
            scheduler.Submit(Req("a1", "A"));
            scheduler.Submit(Req("a2", "A"));

            var result = scheduler.Submit(Req("a3", "A"));

            Assert.Equal("queue-full", result.RejectReason);
            Assert.Equal(new[] { "a1", "a2" }, QueuedLabels(scheduler));
        }

        [Fact]
        public void Submit_WhenSessionHoldsCap_RejectsSessionQueueLimitWithoutEnqueueing()
        {
            var scheduler = NewScheduler(depth: 8, cap: 2);
            StartRunning(scheduler, Req("x0", "X"));
            scheduler.Submit(Req("a1", "A"));
            scheduler.Submit(Req("b1", "B"));
            scheduler.Submit(Req("a2", "A"));

            var rejected = scheduler.Submit(Req("a3", "A"));

            Assert.False(rejected.Started);
            Assert.False(rejected.Queued);
            Assert.Equal("session-queue-limit", rejected.RejectReason);
            Assert.Equal(new[] { "a1", "b1", "a2" }, QueuedLabels(scheduler));

            // Another Session below the cap is still admitted.
            Assert.True(scheduler.Submit(Req("b2", "B")).Queued);
        }

        [Fact]
        public void Cancel_QueuedRequest_RemovesOnlyFromCancellingSession()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("x0", "X"));
            scheduler.Submit(Req("a1", "A", clientId: "1"));
            scheduler.Submit(Req("b1", "B", clientId: "1"));
            scheduler.Submit(Req("a2", "A", clientId: "2"));
            scheduler.Submit(Req("b2", "B", clientId: "2"));

            var result = scheduler.Cancel("B", "1", null);

            var removed = Assert.Single(result.Removed);
            Assert.Equal("b1", removed.ChildRequestId);
            // Snapshot.Queued is in start order; the relative order of A's requests is kept.
            Assert.Equal(new[] { "a1", "b2", "a2" }, QueuedLabels(scheduler));
            Assert.Equal(new[] { "a1", "b2", "a2" }, DrainFrom(scheduler, "x0"));
        }

        [Fact]
        public void Cancel_ByCancelToken_MatchesOnlyCancellingSession()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("x0", "X"));
            scheduler.Submit(Req("a1", "A", clientId: "1", cancelToken: "tok"));
            scheduler.Submit(Req("b1", "B", clientId: "1", cancelToken: "tok"));

            var result = scheduler.Cancel("A", "\"unrelated\"", "tok");

            Assert.Equal(new[] { "a1" }, result.Removed.Select(r => r.ChildRequestId).ToArray());
            Assert.Equal(new[] { "b1" }, QueuedLabels(scheduler));
        }

        [Fact]
        public void Cancel_WithoutMatchInSession_LeavesQueueUnchanged()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("x0", "X"));
            scheduler.Submit(Req("b1", "B", clientId: "7"));

            var result = scheduler.Cancel("A", "7", null);

            Assert.Empty(result.Removed);
            Assert.Equal(new[] { "b1" }, QueuedLabels(scheduler));
        }

        [Fact]
        public void ThreadSafeAndControlCommands_NeverChangeQueueCounts()
        {
            // Thread_Safe_Commands and control commands bypass Submit (the host routes them straight to
            // the child inbox). The only scheduler calls they cause are Snapshot (status, health) and
            // Cancel of an in-flight request (control cancel). With a running SDK_Operation and a full
            // queue, none of these changes the queue count, order, or deadline.
            var scheduler = NewScheduler(depth: 2, cap: 2);
            StartRunning(scheduler, Req("a0", "A", clientId: "0"));
            scheduler.Submit(Req("a1", "A", clientId: "1"));
            scheduler.Submit(Req("b1", "B", clientId: "1"));
            var deadlineBefore = scheduler.NextDeadlineMs;

            for (int i = 0; i < 5; i++)
            {
                scheduler.Snapshot();
            }
            var cancelRunning = scheduler.Cancel("A", "0", null);

            Assert.Empty(cancelRunning.Removed);
            var snapshot = scheduler.Snapshot();
            Assert.Equal("a0", snapshot.Running!.ChildRequestId);
            Assert.Equal(new[] { "a1", "b1" }, snapshot.Queued.Select(r => r.ChildRequestId).ToArray());
            Assert.Equal(deadlineBefore, scheduler.NextDeadlineMs);
        }

        [Fact]
        public void RemoveSession_DropsOnlyThatSessionsQueuedRequests()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("a0", "A"));
            scheduler.Submit(Req("a1", "A"));
            scheduler.Submit(Req("b1", "B"));
            scheduler.Submit(Req("a2", "A"));
            scheduler.Submit(Req("c1", "C"));
            scheduler.Submit(Req("b2", "B"));

            var dropped = scheduler.RemoveSession("A");

            Assert.Equal(new[] { "a1", "a2" }, dropped.Select(r => r.ChildRequestId).OrderBy(x => x).ToArray());
            var snapshot = scheduler.Snapshot();
            // The in-flight request of the detached Session keeps running.
            Assert.Equal("a0", snapshot.Running!.ChildRequestId);
            Assert.Equal(new[] { "b1", "c1", "b2" }, snapshot.Queued.Select(r => r.ChildRequestId).ToArray());
            Assert.Equal(new[] { "b1", "c1", "b2" }, DrainFrom(scheduler, "a0"));
        }

        [Fact]
        public void NextDeadlineMs_TracksEarliestQueuedDeadline()
        {
            var scheduler = NewScheduler();
            StartRunning(scheduler, Req("x0", "X"));
            Assert.Null(scheduler.NextDeadlineMs);

            _now = 100;
            scheduler.Submit(Req("a1", "A", clientId: "1"));
            _now = 250;
            scheduler.Submit(Req("b1", "B", clientId: "1"));
            Assert.Equal(100 + Wait, scheduler.NextDeadlineMs);

            scheduler.Cancel("A", "1", null);
            Assert.Equal(250 + Wait, scheduler.NextDeadlineMs);

            scheduler.Complete("x0");
            Assert.Null(scheduler.NextDeadlineMs);
        }

        [Fact]
        public void WorkerUnavailable_QueuesWithReportedWaitReason_ThenStartsWhenAvailable()
        {
            var scheduler = NewScheduler();
            scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);

            var first = scheduler.Submit(Req("a1", "A"));
            scheduler.Submit(Req("b1", "B"));

            Assert.False(first.Started);
            Assert.True(first.Queued);
            Assert.Null(scheduler.Snapshot().Running);
            Assert.Equal(WaitReason.WorkerStarting, scheduler.Snapshot().WaitReason);

            scheduler.SetWorkerAvailable(false, WaitReason.WorkerLimit);
            Assert.Equal(WaitReason.WorkerLimit, scheduler.Snapshot().WaitReason);
            Assert.Null(scheduler.TryStartNext());

            scheduler.SetWorkerAvailable(true, WaitReason.StaBusy);
            var started = scheduler.TryStartNext();

            Assert.Equal("a1", started!.ChildRequestId);
            var snapshot = scheduler.Snapshot();
            Assert.Equal("a1", snapshot.Running!.ChildRequestId);
            Assert.Equal(new[] { "b1" }, snapshot.Queued.Select(r => r.ChildRequestId).ToArray());
            Assert.Equal(WaitReason.StaBusy, snapshot.WaitReason);
            Assert.Null(scheduler.TryStartNext());
        }
    }
}
