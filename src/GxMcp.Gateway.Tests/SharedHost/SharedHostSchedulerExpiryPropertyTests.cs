using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 4: Queue-wait expiry
    // Validates: Requirements 3.4
    //
    // Property test for the queue-wait expiry of SharedHostScheduler. It needs no GeneXus SDK, license, or KB.
    // It uses the contract pinned by SharedHostSchedulerTests and SharedHostSchedulerSafetyPropertyTests and adds
    // no member to it.
    //
    // Rules this test pins for the implementation (task 1.9):
    // - Boundary: a queued request expires only when its wait is strictly greater than QueueWaitMs, that is when
    //   now - EnqueuedAtMs > QueueWaitMs. At exactly QueueWaitMs it is not expired and stays queued. Source:
    //   requirements.md Requirement 3 criterion 4 ("has waited longer than the configured queue wait") and
    //   design.md Property 4 ("removes exactly the queued requests whose wait exceeds the queue wait"); "exactly"
    //   rules out removing a request whose wait equals the queue wait. Consequence for the host: NextDeadlineMs is
    //   EnqueuedAtMs + QueueWaitMs (pinned by SharedHostSchedulerTests), and ExpireDue called at exactly that
    //   value removes nothing; the request expires on the first call after it.
    // - ExpireDue removes exactly the queued requests whose wait exceeds QueueWaitMs at the current clock value,
    //   returns them in start order, and leaves every other queued request queued.
    // - ExpiredRequest.Position is the 1-based index of the request in the start order (Snapshot().Queued) as it
    //   stood immediately before that ExpireDue call. When one call expires several requests, each one reports the
    //   position it held in that same pre-call order; positions are not renumbered after earlier removals in the
    //   same call (for start order a1, b1, a2 with a1 and a2 expired, the positions are 1 and 3). Source:
    //   requirements.md Requirement 3 criterion 6 ("the caller's 1-based queue position at removal time") and
    //   design.md Property 4 ("the 1-based queue position it held at removal"); the docs do not spell out the
    //   multi-request case, and this reading reports the position the Diagnostics_Surface showed the caller.
    // - After ExpireDue no queued request has waited longer than QueueWaitMs, so NextDeadlineMs is null or at
    //   least the current clock value.
    // - Skip-expired (Requirement 3 criterion 2): Complete and TryStartNext never start a request whose wait
    //   exceeds QueueWaitMs at the call's clock value, even when ExpireDue has not run since the clock moved; they
    //   start a request whenever a non-expired one is queued and the Worker is available. The next ExpireDue at the
    //   same clock value reports every skipped expired request exactly once, in start order, with a position
    //   within the queue length before the start. This test does not pin whether the skipped requests leave the
    //   queue inside Complete or inside ExpireDue, nor which non-expired request starts (that is Property 2).
    // - An expired request is never started, before or after its expiry, and gets exactly one terminal outcome.
    //
    // Host convention (design.md, "SharedWorkerHost.cs"): the clock is injected and only moves forward; ExpireDue
    // runs after clock advances, except in the skip-expired events, which call Complete or TryStartNext first.
    public class SharedHostSchedulerExpiryPropertyTests
    {
        private const int Iterations = 500;

        [Fact]
        public void Property4_QueueWaitExpiry_HoldsForSeededInterleavings()
        {
            var totals = new Totals();

            SeededInterleavingRunner.Run(
                "Property 4: Queue-wait expiry",
                Iterations,
                c => new Simulation(c, totals).Run());

            // Generator sanity: the runs reached the exact boundary, multi-request expiry, and the skip path.
            Assert.True(totals.ExactBoundaryKept > 0, "no case held a queued request at exactly the queue wait");
            Assert.True(totals.MultiExpiries > 0, "no ExpireDue call expired more than one request");
            Assert.True(totals.SkippedExpired > 0, "no start skipped an expired request");
        }

        [Fact]
        public void ExpireDue_AtExactlyQueueWaitKeepsRequest_AndBatchPositionsUsePreCallStartOrder()
        {
            const int wait = 1000;
            long now = 0;
            long sequence = 0;
            var scheduler = new SharedHostScheduler(new SchedulerLimits(8, 4, wait), () => now);
            QueuedRequest Req(string label, string session) => new QueuedRequest
            {
                ChildRequestId = label,
                SessionId = session,
                ClientRequestIdJson = label,
                Operation = "genexus_read/source",
                EnqueuedAtMs = now,
                Sequence = ++sequence,
                Payload = "{}"
            };

            Assert.True(scheduler.Submit(Req("x0", "X")).Started);
            now = 0;
            Assert.True(scheduler.Submit(Req("a1", "A")).Queued);
            now = 4;
            Assert.True(scheduler.Submit(Req("a2", "A")).Queued);
            now = 6;
            Assert.True(scheduler.Submit(Req("b1", "B")).Queued);
            Assert.Equal(new[] { "a1", "b1", "a2" }, scheduler.Snapshot().Queued.Select(r => r.ChildRequestId).ToArray());

            // a1 has waited exactly the queue wait: not expired.
            now = wait;
            Assert.Empty(scheduler.ExpireDue());
            Assert.Equal(0 + wait, scheduler.NextDeadlineMs);
            Assert.Equal(3, scheduler.Snapshot().Queued.Count());

            // a1 (1005 ms) and a2 (1001 ms) exceed the wait, b1 (999 ms) does not. Positions come from a1, b1, a2.
            now = wait + 5;
            var expired = scheduler.ExpireDue();

            Assert.Equal(new[] { "a1", "a2" }, expired.Select(e => e.Request.ChildRequestId).ToArray());
            Assert.Equal(new[] { 1, 3 }, expired.Select(e => e.Position).ToArray());
            Assert.Equal(new[] { "b1" }, scheduler.Snapshot().Queued.Select(r => r.ChildRequestId).ToArray());
            Assert.Equal(6 + wait, scheduler.NextDeadlineMs);
            Assert.Equal("b1", scheduler.Complete("x0")!.ChildRequestId);
        }

        [Fact]
        public void Complete_AfterDeadlinePassedWithoutExpireDue_SkipsExpiredRequest()
        {
            const int wait = 1000;
            long now = 0;
            long sequence = 0;
            var scheduler = new SharedHostScheduler(new SchedulerLimits(8, 4, wait), () => now);
            QueuedRequest Req(string label, string session) => new QueuedRequest
            {
                ChildRequestId = label,
                SessionId = session,
                ClientRequestIdJson = label,
                Operation = "genexus_read/source",
                EnqueuedAtMs = now,
                Sequence = ++sequence,
                Payload = "{}"
            };

            Assert.True(scheduler.Submit(Req("x0", "X")).Started);
            Assert.True(scheduler.Submit(Req("a1", "A")).Queued);
            now = 500;
            Assert.True(scheduler.Submit(Req("b1", "B")).Queued);

            // a1 is past its deadline but no ExpireDue ran yet; the next start must skip it.
            now = 1200;
            var started = scheduler.Complete("x0");

            Assert.Equal("b1", started!.ChildRequestId);
            var expired = Assert.Single(scheduler.ExpireDue());
            Assert.Equal("a1", expired.Request.ChildRequestId);
            Assert.Equal(1, expired.Position);
            Assert.Empty(scheduler.Snapshot().Queued);
            Assert.Null(scheduler.NextDeadlineMs);
            Assert.Null(scheduler.Complete("b1"));
        }

        private sealed class Totals
        {
            internal int ExactBoundaryKept;
            internal int MultiExpiries;
            internal int SkippedExpired;
        }

        private sealed class Simulation
        {
            private readonly PbtCase _c;
            private readonly Totals _totals;
            private readonly int _depth;
            private readonly int _cap;
            private readonly int _wait;
            private readonly int _sessionCount;
            private readonly int _totalRequests;
            private readonly SharedHostScheduler _scheduler;

            private readonly Dictionary<string, QueuedRequest> _all = new Dictionary<string, QueuedRequest>();
            private readonly List<string> _submitted = new List<string>();
            private readonly Dictionary<string, string> _outcome = new Dictionary<string, string>();
            private readonly HashSet<string> _started = new HashSet<string>();
            private readonly HashSet<string> _expired = new HashSet<string>();

            private long _now;
            private long _sequence;
            private string? _running;
            private bool _available = true;

            internal Simulation(PbtCase c, Totals totals)
            {
                _c = c;
                _totals = totals;
                _sessionCount = c.SmallInt(1, 4);
                _totalRequests = c.SmallInt(1, 64);
                _depth = c.SmallInt(1, 64);
                _cap = c.SmallInt(1, _depth);
                _wait = c.Chance(0.5) ? c.Int(1000, 5000) : c.Int(1000, 600000);
                _now = c.Int(0, 1000000);
                _scheduler = new SharedHostScheduler(new SchedulerLimits(_depth, _cap, _wait), () => _now);
                c.Log($"config sessions={_sessionCount} requests={_totalRequests} depth={_depth} cap={_cap} waitMs={_wait} now={_now}");

                if (c.Chance(0.3))
                {
                    SetUnavailable("initial");
                }
            }

            internal void Run()
            {
                var minSteps = _totalRequests * 3;
                var step = 0;
                while (_submitted.Count < _totalRequests || step < minSteps)
                {
                    step++;
                    var roll = _c.Int(0, 99);
                    if (roll < 40) { if (_submitted.Count < _totalRequests) Submit(); else AdvanceAndExpire(); }
                    else if (roll < 65) AdvanceAndExpire();
                    else if (roll < 75) Finish(skipExpired: false);
                    else if (roll < 88) Finish(skipExpired: true);
                    else if (roll < 93) WorkerUnavailable();
                    else WorkerReady(skipExpired: _c.Bool());
                }

                Drain();
            }

            // ---- events ----

            private void Submit()
            {
                var session = "S" + _c.Int(0, _sessionCount - 1).ToString(CultureInfo.InvariantCulture);
                var id = "r" + (_submitted.Count + 1).ToString(CultureInfo.InvariantCulture);
                var request = new QueuedRequest
                {
                    ChildRequestId = id,
                    SessionId = session,
                    ClientRequestIdJson = id,
                    Operation = "genexus_read/source",
                    CancelToken = null,
                    EnqueuedAtMs = _now,
                    Sequence = ++_sequence,
                    Payload = "{}"
                };
                _all[id] = request;
                _submitted.Add(id);

                var result = _scheduler.Submit(request);
                _c.Log($"submit {id} session={session} at={_now} -> started={result.Started} queued={result.Queued} reject={result.RejectReason ?? "-"}");

                if (result.RejectReason != null)
                {
                    Record(id, "busy:" + result.RejectReason);
                }
                else if (result.Started)
                {
                    MarkStarted(request);
                }
                else
                {
                    Assert.True(result.Queued, "submit " + id + " was neither started, queued, nor rejected");
                }
            }

            private void AdvanceAndExpire()
            {
                Advance();
                ExpireAndCheck();
            }

            private void Finish(bool skipExpired)
            {
                if (_running == null)
                {
                    _c.Log("finish skipped: idle");
                    return;
                }

                if (skipExpired) Advance();

                var before = QueuedNow();
                var running = _running;
                Record(running, "completed");
                _running = null;
                var next = _scheduler.Complete(running);
                _c.Log($"finish {running} at={_now} skip={skipExpired} -> next={next?.ChildRequestId ?? "-"}");

                CheckStartChoice(before, next, expectStart: _available);
                if (skipExpired) ExpireAfterStart(before, next);
            }

            private void WorkerUnavailable()
            {
                if (_running != null || !_available)
                {
                    _c.Log("worker-unavailable skipped");
                    return;
                }
                SetUnavailable(_c.Bool() ? "idle-stop" : "worker-limit");
            }

            private void WorkerReady(bool skipExpired)
            {
                if (_available)
                {
                    _c.Log("worker-ready skipped: available");
                    return;
                }

                if (skipExpired) Advance();

                var before = QueuedNow();
                _scheduler.SetWorkerAvailable(true, WaitReason.StaBusy);
                _available = true;
                var started = _scheduler.TryStartNext();
                _c.Log($"worker-ready at={_now} skip={skipExpired} -> started={started?.ChildRequestId ?? "-"}");

                CheckStartChoice(before, started, expectStart: _running == null);
                if (skipExpired) ExpireAfterStart(before, started);
            }

            private void Drain()
            {
                _c.Log("drain");
                if (!_available) WorkerReady(skipExpired: true);
                var guard = 0;
                while (_running != null)
                {
                    Assert.True(++guard <= _totalRequests + 1, "drain did not terminate");
                    Finish(skipExpired: true);
                }

                Assert.Empty(_scheduler.Snapshot().Queued);
                Assert.Null(_scheduler.NextDeadlineMs);
                foreach (var id in _submitted)
                {
                    Assert.True(_outcome.ContainsKey(id), "request " + id + " has no terminal outcome");
                }
                Assert.Empty(_expired.Intersect(_started));
            }

            // ---- checks ----

            // ExpireDue on a queue that was clean before the last clock advance (normal path): exact set, start
            // order, and pre-call positions.
            private void ExpireAndCheck()
            {
                var before = _scheduler.Snapshot().Queued.ToList();
                var expected = new List<(string Id, int Position)>();
                var exactBoundary = new List<string>();
                for (var i = 0; i < before.Count; i++)
                {
                    var age = _now - before[i].EnqueuedAtMs;
                    if (age > _wait) expected.Add((before[i].ChildRequestId, i + 1));
                    else if (age == _wait) exactBoundary.Add(before[i].ChildRequestId);
                }

                var expired = _scheduler.ExpireDue();
                var actual = expired.Select(e => (e.Request.ChildRequestId, e.Position)).ToList();
                _c.Log($"expire at={_now} -> [{string.Join(",", actual.Select(a => a.Item1 + "@" + a.Item2.ToString(CultureInfo.InvariantCulture)))}]"
                    + $" boundary=[{string.Join(",", exactBoundary)}]");

                Assert.Equal(expected.Select(e => e.Id).ToList(), actual.Select(a => a.Item1).ToList());
                Assert.Equal(expected.Select(e => e.Position).ToList(), actual.Select(a => a.Item2).ToList());

                foreach (var e in expired) RecordExpired(e.Request.ChildRequestId);

                var after = QueuedIds();
                foreach (var id in exactBoundary)
                {
                    Assert.Contains(id, after);
                }
                Assert.Equal(
                    before.Select(r => r.ChildRequestId).Except(expected.Select(e => e.Id)).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    after.OrderBy(x => x, StringComparer.Ordinal).ToList());

                if (exactBoundary.Count > 0) _totals.ExactBoundaryKept++;
                if (expired.Count > 1) _totals.MultiExpiries++;

                CheckNoneOverdue();
            }

            // A start (Complete or TryStartNext) at the current clock, possibly before ExpireDue ran.
            private void CheckStartChoice(List<QueuedRequest> before, QueuedRequest? started, bool expectStart)
            {
                var eligible = before.Where(r => _now - r.EnqueuedAtMs <= _wait).Select(r => r.ChildRequestId).ToList();
                if (started == null)
                {
                    // Work-conserving: an available, idle Worker never leaves a non-expired request waiting.
                    if (expectStart) Assert.Empty(eligible);
                    return;
                }

                Assert.True(expectStart, "a request started while the Worker was unavailable or busy");
                Assert.True(_now - started.EnqueuedAtMs <= _wait,
                    "started request " + started.ChildRequestId + " had waited " + (_now - started.EnqueuedAtMs).ToString(CultureInfo.InvariantCulture)
                    + " ms, more than the queue wait");
                Assert.Contains(started.ChildRequestId, eligible);
                MarkStarted(_all[started.ChildRequestId]);

                if (before.Count > eligible.Count) _totals.SkippedExpired++;
            }

            // ExpireDue right after a start that ran before ExpireDue (skip path): every request that was expired at
            // the start is reported once, in start order, with a position within the pre-start queue length.
            private void ExpireAfterStart(List<QueuedRequest> before, QueuedRequest? started)
            {
                var expected = before.Where(r => _now - r.EnqueuedAtMs > _wait).Select(r => r.ChildRequestId).ToList();

                var expired = _scheduler.ExpireDue();
                var actual = expired.Select(e => e.Request.ChildRequestId).ToList();
                _c.Log($"expire-after-start at={_now} -> [{string.Join(",", expired.Select(e => e.Request.ChildRequestId + "@" + e.Position.ToString(CultureInfo.InvariantCulture)))}]");

                Assert.Equal(expected, actual);
                var previous = 0;
                foreach (var e in expired)
                {
                    Assert.InRange(e.Position, 1, before.Count);
                    Assert.True(e.Position > previous, "expired positions are not in start order");
                    previous = e.Position;
                    RecordExpired(e.Request.ChildRequestId);
                }

                var remaining = before.Select(r => r.ChildRequestId).Except(expected);
                if (started != null) remaining = remaining.Where(id => id != started.ChildRequestId);
                Assert.Equal(
                    remaining.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    QueuedIds().OrderBy(x => x, StringComparer.Ordinal).ToList());

                CheckNoneOverdue();
            }

            private void CheckNoneOverdue()
            {
                var queued = _scheduler.Snapshot().Queued.ToList();
                foreach (var r in queued)
                {
                    Assert.True(_now - r.EnqueuedAtMs <= _wait, "request " + r.ChildRequestId + " is still queued after outliving the queue wait");
                    Assert.DoesNotContain(r.ChildRequestId, _expired);
                    Assert.DoesNotContain(r.ChildRequestId, _started);
                }

                long? expectedDeadline = queued.Count == 0 ? (long?)null : queued.Min(r => r.EnqueuedAtMs) + _wait;
                Assert.Equal(expectedDeadline, _scheduler.NextDeadlineMs);
                if (expectedDeadline.HasValue) Assert.True(expectedDeadline.Value >= _now, "NextDeadlineMs is in the past after ExpireDue");
            }

            // ---- helpers ----

            private void Advance()
            {
                var queued = _scheduler.Snapshot().Queued.ToList();
                long advance;
                var kind = _c.Int(0, 99);
                if (kind < 45 && queued.Count > 0)
                {
                    // Land on, just before, or just after a queued request's deadline.
                    var target = _c.Pick(queued).EnqueuedAtMs + _wait + _c.Int(-1, 1);
                    advance = Math.Max(0, target - _now);
                }
                else if (kind < 75)
                {
                    advance = _c.Int(0, 50);
                }
                else
                {
                    advance = _c.Int(0, 2 * _wait);
                }
                _now += advance;
                _c.Log($"advance +{advance} now={_now}");
            }

            private void SetUnavailable(string cause)
            {
                var reason = cause == "worker-limit" ? WaitReason.WorkerLimit : WaitReason.WorkerStarting;
                _scheduler.SetWorkerAvailable(false, reason);
                _available = false;
                _c.Log($"worker-unavailable cause={cause} reason={reason}");
            }

            private void MarkStarted(QueuedRequest request)
            {
                var id = request.ChildRequestId;
                Assert.False(_expired.Contains(id), "expired request " + id + " was started");
                Assert.True(_started.Add(id), "request " + id + " started twice");
                Assert.Null(_running);
                _running = id;
                _c.Log($"start {id} waited={_now - request.EnqueuedAtMs}");
            }

            private void RecordExpired(string id)
            {
                Assert.DoesNotContain(id, _started);
                Assert.True(_expired.Add(id), "request " + id + " expired twice");
                Record(id, "busy:queue-wait-expired");
            }

            private void Record(string id, string outcome)
            {
                if (_outcome.TryGetValue(id, out var previous))
                {
                    throw new Xunit.Sdk.XunitException("request " + id + " got a second terminal outcome " + outcome + " after " + previous);
                }
                _outcome[id] = outcome;
            }

            private List<QueuedRequest> QueuedNow() => _scheduler.Snapshot().Queued.ToList();

            private List<string> QueuedIds() => _scheduler.Snapshot().Queued.Select(r => r.ChildRequestId).ToList();
        }
    }
}
