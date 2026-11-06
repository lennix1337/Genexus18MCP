using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 1: Scheduler safety invariants
    // Validates: Requirements 3.1, 3.2, 3.5, 3.8, 3.10, 3.15, 6.7, 8.4, 13.2
    //
    // Required gate property test (Requirement 13 criterion 2). It needs no GeneXus SDK, license, or KB.
    //
    // Each case drives SharedHostScheduler the way the Worker_Host drives it, through seeded interleavings of
    // submit, start, finish, cancel, expiry, detach, Worker-unavailable, Worker-exit, and bypass (Thread_Safe_Commands
    // and control commands) events over 1 to 4 Sessions and 1 to 64 requests, and compares every step with a
    // list-based reference model that applies the same admission and rotation rules.
    //
    // Host conventions this test assumes (they mirror design.md, "SharedWorkerHost.cs"):
    // - The clock is injected and only moves forward. Every clock advance is followed by ExpireDue, as the host's
    //   NextDeadlineMs timer does, so no other call ever sees a request older than the queue wait. Clock values
    //   stay even and the queue wait is odd, so a request is never exactly at its deadline; the boundary and the
    //   skip-expired rule belong to Property 4.
    // - Worker exit: the host calls SetWorkerAvailable(false, WorkerStarting) and then Complete(running id). With
    //   the Worker unavailable, Complete releases the dispatch window, starts nothing, and returns null; the
    //   in-flight request gets SHARED_WORKER_RESTARTED (delivered-unanswered) and the queued requests stay queued.
    // - Worker ready: SetWorkerAvailable(true, StaBusy) followed by TryStartNext.
    // - Replacement failure: the host drains the queue with RemoveSession for each Session in Snapshot().Queued and
    //   answers each removed request with SHARED_WORKER_RESTARTED (not-delivered).
    // - Bypass commands never call Submit; they cause Snapshot calls and, for control cancel of the in-flight
    //   request, a Cancel that matches no queued request.
    // - ExpireDue returns the expired requests in start order. Their exact Position values belong to Property 4;
    //   this test only checks that each one is within the queue length before removal.
    public class SharedHostSchedulerSafetyPropertyTests
    {
        private const int Iterations = 1000;

        [Fact]
        public void Property1_SchedulerSafetyInvariants_HoldForSeededInterleavings()
        {
            SeededInterleavingRunner.Run(
                "Property 1: Scheduler safety invariants",
                Iterations,
                c => new Simulation(c).Run());
        }

        private sealed class Simulation
        {
            private static readonly string?[] TokenPool = { null, null, "tok-1", "tok-2" };

            private readonly PbtCase _c;
            private readonly int _depth;
            private readonly int _cap;
            private readonly int _wait;
            private readonly int _sessionCount;
            private readonly int _totalRequests;
            private readonly SharedHostScheduler _scheduler;
            private readonly ReferenceModel _model = new ReferenceModel();

            private readonly int[] _generation;
            private readonly Dictionary<string, List<string>> _clientIdsBySession = new Dictionary<string, List<string>>();
            private readonly HashSet<string> _detachedSessions = new HashSet<string>();
            private readonly Dictionary<string, QueuedRequest> _all = new Dictionary<string, QueuedRequest>();
            private readonly List<string> _submitted = new List<string>();
            private readonly Dictionary<string, string> _outcome = new Dictionary<string, string>();
            private readonly HashSet<string> _started = new HashSet<string>();

            private long _now;
            private long _sequence;
            private int _inFlight;

            internal Simulation(PbtCase c)
            {
                _c = c;
                _sessionCount = c.SmallInt(1, 4);
                _totalRequests = c.SmallInt(1, 64);
                _depth = c.SmallInt(1, 64);
                _cap = c.SmallInt(1, _depth);
                _wait = 1001 + 2 * c.Int(0, 2499);
                _now = 2L * c.Int(0, 1000000);
                _generation = new int[_sessionCount];
                _scheduler = new SharedHostScheduler(new SchedulerLimits(_depth, _cap, _wait), () => _now);
                _model.Available = true;
                _model.ReasonWhileUnavailable = WaitReason.StaBusy;
                c.Log($"config sessions={_sessionCount} requests={_totalRequests} depth={_depth} cap={_cap} waitMs={_wait} now={_now}");

                if (c.Chance(0.25))
                {
                    SetUnavailable(WaitReason.WorkerStarting, "initial");
                }
            }

            internal void Run()
            {
                CheckInvariants();
                var minSteps = _totalRequests * 3;
                var step = 0;
                while (_submitted.Count < _totalRequests || step < minSteps)
                {
                    step++;
                    var roll = _c.Int(0, 99);
                    if (roll < 35) { if (_submitted.Count < _totalRequests) Submit(); else Finish(); }
                    else if (roll < 55) Finish();
                    else if (roll < 65) Cancel();
                    else if (roll < 75) AdvanceClockAndExpire();
                    else if (roll < 79) Detach();
                    else if (roll < 83) WorkerUnavailable();
                    else if (roll < 87) WorkerExit();
                    else if (roll < 93) WorkerReady();
                    else if (roll < 95) ReplacementFailed();
                    else Bypass();
                    CheckInvariants();
                }

                Drain();
            }

            // ---- events ----

            private void Submit()
            {
                var slot = _c.Int(0, _sessionCount - 1);
                var session = SessionId(slot);
                if (!_clientIdsBySession.TryGetValue(session, out var clientIds))
                {
                    clientIds = new List<string>();
                    _clientIdsBySession[session] = clientIds;
                }
                // JSON-RPC ids are unique per Session but repeat across Sessions (Requirement 8 criterion 3).
                var clientId = (clientIds.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                clientIds.Add(clientId);

                var request = new QueuedRequest
                {
                    ChildRequestId = "r" + (_submitted.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SessionId = session,
                    ClientRequestIdJson = clientId,
                    Operation = "genexus_read/source",
                    CancelToken = _c.Pick(TokenPool),
                    EnqueuedAtMs = _now,
                    Sequence = ++_sequence,
                    Payload = "{}"
                };
                _all[request.ChildRequestId] = request;
                _submitted.Add(request.ChildRequestId);

                string? expectedReject = _model.Count >= _depth ? "queue-full"
                    : _model.CountFor(session) >= _cap ? "session-queue-limit"
                    : null;
                var expectStart = expectedReject == null && _model.Running == null && _model.Available && _model.Count == 0;

                var queueBefore = QueuedIds();
                var deadlineBefore = _scheduler.NextDeadlineMs;

                var result = _scheduler.Submit(request);
                _c.Log($"submit {request.ChildRequestId} session={session} client={clientId} token={request.CancelToken ?? "-"} at={_now}"
                    + $" -> started={result.Started} queued={result.Queued} reject={result.RejectReason ?? "-"}");

                Assert.Equal(expectedReject, result.RejectReason);
                Assert.Equal(expectStart, result.Started);
                Assert.Equal(expectedReject == null && !expectStart, result.Queued);

                if (expectedReject != null)
                {
                    // A rejected submit leaves the queue contents, order, and deadline unchanged (3.5, 3.15).
                    Assert.Equal(queueBefore, QueuedIds());
                    Assert.Equal(deadlineBefore, _scheduler.NextDeadlineMs);
                    Record(request.ChildRequestId, "busy:" + expectedReject);
                }
                else if (expectStart)
                {
                    MarkStarted(request);
                }
                else
                {
                    _model.Enqueue(request);
                }
            }

            private void Finish()
            {
                var running = _model.Running;
                if (running == null)
                {
                    _c.Log("finish skipped: idle");
                    return;
                }

                var expectedNext = _model.Available ? _model.TakeNext() : null;
                ReleaseRunning(running, _detachedSessions.Contains(running.SessionId) ? "completed-discarded" : "completed");
                var next = _scheduler.Complete(running.ChildRequestId);
                _c.Log($"finish {running.ChildRequestId} -> next={next?.ChildRequestId ?? "-"}");

                Assert.Equal(expectedNext?.ChildRequestId, next?.ChildRequestId);
                if (expectedNext != null) MarkStarted(expectedNext);
            }

            private void Cancel()
            {
                var session = SessionId(_c.Int(0, _sessionCount - 1));
                var clientId = _clientIdsBySession.TryGetValue(session, out var ids) && ids.Count > 0 && _c.Chance(0.85)
                    ? _c.Pick(ids)
                    : "\"unknown\"";
                var token = _c.Pick(TokenPool);

                var expected = _model.QueuedOf(session)
                    .Where(r => r.ClientRequestIdJson == clientId || (token != null && r.CancelToken == token))
                    .Select(r => r.ChildRequestId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();

                var result = _scheduler.Cancel(session, clientId, token);
                var removed = result.Removed.Select(r => r.ChildRequestId).OrderBy(id => id, StringComparer.Ordinal).ToList();
                _c.Log($"cancel session={session} client={clientId} token={token ?? "-"} -> removed=[{string.Join(",", removed)}]");

                Assert.Equal(expected, removed);
                foreach (var id in removed)
                {
                    _model.Remove(_all[id]);
                    Record(id, "cancelled");
                }
            }

            private void AdvanceClockAndExpire()
            {
                long advance = _c.Chance(0.6) ? 2L * _c.Int(0, 50) : 2L * _c.Int(0, _wait);
                _now += advance;

                var startOrder = _model.StartOrder();
                var expected = startOrder.Where(r => _now - r.EnqueuedAtMs > _wait).Select(r => r.ChildRequestId).ToList();

                var expired = _scheduler.ExpireDue();
                var actual = expired.Select(e => e.Request.ChildRequestId).ToList();
                _c.Log($"advance +{advance} now={_now} expire -> [{string.Join(",", actual)}]");

                Assert.Equal(expected, actual);
                foreach (var e in expired)
                {
                    Assert.InRange(e.Position, 1, startOrder.Count);
                    _model.Remove(_all[e.Request.ChildRequestId]);
                    Record(e.Request.ChildRequestId, "busy:queue-wait-expired");
                }
            }

            private void Detach()
            {
                var slot = _c.Int(0, _sessionCount - 1);
                var session = SessionId(slot);
                var expected = _model.QueuedOf(session).Select(r => r.ChildRequestId).OrderBy(id => id, StringComparer.Ordinal).ToList();

                var dropped = _scheduler.RemoveSession(session).Select(r => r.ChildRequestId).OrderBy(id => id, StringComparer.Ordinal).ToList();
                _c.Log($"detach {session} -> dropped=[{string.Join(",", dropped)}]");

                Assert.Equal(expected, dropped);
                foreach (var id in dropped)
                {
                    _model.Remove(_all[id]);
                    // The Session is gone; the request is dropped without execution and no response is sent.
                    Record(id, "dropped-detached");
                }
                _detachedSessions.Add(session);
                // The slot's next Gateway attaches as a new Session.
                _generation[slot]++;
            }

            private void WorkerUnavailable()
            {
                if (_model.Running != null || !_model.Available)
                {
                    _c.Log("worker-unavailable skipped");
                    return;
                }
                // Idle child stop (heap recycle, idle reaping) or Worker limit reached for a replacement.
                SetUnavailable(_c.Bool() ? WaitReason.WorkerStarting : WaitReason.WorkerLimit, "idle");
            }

            private void WorkerExit()
            {
                var running = _model.Running;
                if (running == null)
                {
                    if (_model.Available) SetUnavailable(WaitReason.WorkerStarting, "idle-exit");
                    else _c.Log("worker-exit skipped: no Worker");
                    return;
                }

                SetUnavailable(WaitReason.WorkerStarting, "exit");
                ReleaseRunning(running, "restarted:delivered-unanswered");
                var next = _scheduler.Complete(running.ChildRequestId);
                _c.Log($"worker-exit releases {running.ChildRequestId} -> next={next?.ChildRequestId ?? "-"}");

                // Nothing starts on a dead Worker; queued requests wait for the replacement (8.4).
                Assert.Null(next);
            }

            private void WorkerReady()
            {
                if (_model.Available)
                {
                    _c.Log("worker-ready skipped: available");
                    return;
                }

                _scheduler.SetWorkerAvailable(true, WaitReason.StaBusy);
                _model.Available = true;
                _model.ReasonWhileUnavailable = WaitReason.StaBusy;
                var expected = _model.Running == null ? _model.TakeNext() : null;
                var started = _scheduler.TryStartNext();
                _c.Log($"worker-ready -> started={started?.ChildRequestId ?? "-"}");

                Assert.Equal(expected?.ChildRequestId, started?.ChildRequestId);
                if (expected != null) MarkStarted(expected);
                // A second call has nothing more to start while the window is taken or the queue is empty.
                Assert.Null(_scheduler.TryStartNext());
            }

            private void ReplacementFailed()
            {
                if (_model.Available)
                {
                    _c.Log("replacement-failed skipped: available");
                    return;
                }

                // Half the time the replacement is still pending and only the reported wait reason changes.
                if (_c.Bool())
                {
                    var reason = _model.ReasonWhileUnavailable == WaitReason.WorkerStarting ? WaitReason.WorkerLimit : WaitReason.WorkerStarting;
                    SetUnavailable(reason, "reason-change");
                    return;
                }

                var sessions = _scheduler.Snapshot().Queued.Select(r => r.SessionId).Distinct().ToList();
                foreach (var session in sessions)
                {
                    var expected = _model.QueuedOf(session).Select(r => r.ChildRequestId).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    var removed = _scheduler.RemoveSession(session).Select(r => r.ChildRequestId).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    _c.Log($"replacement-failed drains {session} -> [{string.Join(",", removed)}]");
                    Assert.Equal(expected, removed);
                    foreach (var id in removed)
                    {
                        _model.Remove(_all[id]);
                        Record(id, "restarted:not-delivered");
                    }
                }
                Assert.Equal(0, _model.Count);
            }

            private void Bypass()
            {
                // Thread_Safe_Commands and control commands bypass Submit (3.8): status and health read the snapshot,
                // and a control cancel of the in-flight request matches no queued request.
                var before = QueuedIds();
                var deadlineBefore = _scheduler.NextDeadlineMs;
                var runningBefore = _scheduler.Snapshot().Running?.ChildRequestId;

                var snapshots = _c.Int(1, 3);
                for (var i = 0; i < snapshots; i++) _scheduler.Snapshot();

                var running = _model.Running;
                var cancelled = 0;
                if (running != null && _c.Bool())
                {
                    cancelled = _scheduler.Cancel(running.SessionId, running.ClientRequestIdJson, null).Removed.Count;
                }
                _c.Log($"bypass snapshots={snapshots} control-cancel={(running != null ? running.ChildRequestId : "-")} removed={cancelled}");

                Assert.Equal(0, cancelled);
                Assert.Equal(before, QueuedIds());
                Assert.Equal(deadlineBefore, _scheduler.NextDeadlineMs);
                Assert.Equal(runningBefore, _scheduler.Snapshot().Running?.ChildRequestId);
            }

            private void Drain()
            {
                _c.Log("drain");
                if (!_model.Available) WorkerReady();
                CheckInvariants();
                while (_model.Running != null)
                {
                    Finish();
                    CheckInvariants();
                }

                Assert.Equal(0, _model.Count);
                Assert.Empty(_scheduler.Snapshot().Queued);
                Assert.Null(_scheduler.Snapshot().Running);
                Assert.Null(_scheduler.NextDeadlineMs);
                Assert.Equal(0, _inFlight);

                // Every submitted SDK_Operation received exactly one terminal outcome.
                foreach (var id in _submitted)
                {
                    Assert.True(_outcome.ContainsKey(id), "request " + id + " has no terminal outcome");
                }
                Assert.Equal(_submitted.Count, _outcome.Count);
            }

            // ---- helpers ----

            private void SetUnavailable(WaitReason reason, string cause)
            {
                _scheduler.SetWorkerAvailable(false, reason);
                _model.Available = false;
                _model.ReasonWhileUnavailable = reason;
                _c.Log($"worker-unavailable cause={cause} reason={reason}");
            }

            private void MarkStarted(QueuedRequest request)
            {
                Assert.True(_started.Add(request.ChildRequestId), "request " + request.ChildRequestId + " started twice");
                Assert.False(_outcome.ContainsKey(request.ChildRequestId), "request " + request.ChildRequestId + " started after its terminal outcome");
                _inFlight++;
                // At most one SDK_Operation in flight per Worker (6.7).
                Assert.True(_inFlight <= 1, "more than one SDK_Operation in flight");
                _model.Running = request;
                _c.Log($"start {request.ChildRequestId}");
            }

            private void ReleaseRunning(QueuedRequest running, string outcome)
            {
                _inFlight--;
                _model.Running = null;
                Record(running.ChildRequestId, outcome);
            }

            private void Record(string id, string outcome)
            {
                if (_outcome.TryGetValue(id, out var previous))
                {
                    throw new Xunit.Sdk.XunitException("request " + id + " got a second terminal outcome " + outcome + " after " + previous);
                }
                _outcome[id] = outcome;
            }

            private string SessionId(int slot) =>
                "S" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "g" + _generation[slot].ToString(System.Globalization.CultureInfo.InvariantCulture);

            private List<string> QueuedIds() => _scheduler.Snapshot().Queued.Select(r => r.ChildRequestId).ToList();

            private void CheckInvariants()
            {
                var snapshot = _scheduler.Snapshot();
                var queued = snapshot.Queued.Select(r => r.ChildRequestId).ToList();

                Assert.Equal(_model.Running?.ChildRequestId, snapshot.Running?.ChildRequestId);
                Assert.Equal(_model.StartOrder().Select(r => r.ChildRequestId).ToList(), queued);

                // Depth (3.1, 3.5) and Session_Queue_Cap (3.15) bounds; the running request is not counted.
                Assert.True(queued.Count <= _depth, "queue holds " + queued.Count + " requests, depth is " + _depth);
                foreach (var group in snapshot.Queued.GroupBy(r => r.SessionId))
                {
                    Assert.True(group.Count() <= _cap, "Session " + group.Key + " holds " + group.Count() + " queued requests, cap is " + _cap);
                }

                Assert.Equal(_model.NextDeadline(_wait), _scheduler.NextDeadlineMs);

                if (!_model.Available)
                {
                    Assert.Equal(_model.ReasonWhileUnavailable, snapshot.WaitReason);
                }
                else
                {
                    if (_model.Running != null && queued.Count > 0) Assert.Equal(WaitReason.StaBusy, snapshot.WaitReason);
                    // Work-conserving: an available, idle Worker never leaves a request waiting (3.2, 3.10).
                    if (snapshot.Running == null) Assert.Empty(queued);
                }

                foreach (var r in snapshot.Queued)
                {
                    Assert.True(_now - r.EnqueuedAtMs <= _wait, "request " + r.ChildRequestId + " outlived the queue wait");
                    Assert.False(_outcome.ContainsKey(r.ChildRequestId), "queued request " + r.ChildRequestId + " already has a terminal outcome");
                    Assert.DoesNotContain(r.ChildRequestId, _started);
                }
                if (snapshot.Running != null)
                {
                    Assert.False(_outcome.ContainsKey(snapshot.Running.ChildRequestId), "running request already has a terminal outcome");
                }
            }
        }

        // List-based reference model of the Worker_Scheduler: per-Session FIFO lists plus a ring of Sessions that
        // hold at least one queued request, ordered by when each Session last went from empty to non-empty
        // (design.md, "Fairness rule").
        private sealed class ReferenceModel
        {
            private readonly List<string> _ring = new List<string>();
            private readonly Dictionary<string, List<QueuedRequest>> _bySession = new Dictionary<string, List<QueuedRequest>>();

            internal QueuedRequest? Running;
            internal bool Available;
            internal WaitReason ReasonWhileUnavailable;
            internal int Count { get; private set; }

            internal int CountFor(string sessionId) =>
                _bySession.TryGetValue(sessionId, out var list) ? list.Count : 0;

            internal void Enqueue(QueuedRequest request)
            {
                if (!_bySession.TryGetValue(request.SessionId, out var list))
                {
                    list = new List<QueuedRequest>();
                    _bySession[request.SessionId] = list;
                }
                if (list.Count == 0) _ring.Add(request.SessionId);
                list.Add(request);
                Count++;
            }

            internal QueuedRequest? TakeNext()
            {
                if (_ring.Count == 0) return null;
                var session = _ring[0];
                _ring.RemoveAt(0);
                var list = _bySession[session];
                var next = list[0];
                list.RemoveAt(0);
                Count--;
                if (list.Count > 0) _ring.Add(session);
                return next;
            }

            internal void Remove(QueuedRequest request)
            {
                if (!_bySession.TryGetValue(request.SessionId, out var list) || !list.Remove(request))
                {
                    throw new Xunit.Sdk.XunitException("reference model does not hold queued request " + request.ChildRequestId);
                }
                Count--;
                if (list.Count == 0) _ring.Remove(request.SessionId);
            }

            internal List<QueuedRequest> QueuedOf(string sessionId) =>
                _bySession.TryGetValue(sessionId, out var list) ? new List<QueuedRequest>(list) : new List<QueuedRequest>();

            // The order in which the queued requests would start if no other event happened.
            internal List<QueuedRequest> StartOrder()
            {
                var ring = new Queue<string>(_ring);
                var cursors = _ring.ToDictionary(s => s, _ => 0);
                var order = new List<QueuedRequest>(Count);
                while (ring.Count > 0)
                {
                    var session = ring.Dequeue();
                    var list = _bySession[session];
                    order.Add(list[cursors[session]]);
                    cursors[session]++;
                    if (cursors[session] < list.Count) ring.Enqueue(session);
                }
                return order;
            }

            internal long? NextDeadline(int queueWaitMs)
            {
                if (Count == 0) return null;
                return _bySession.Values.SelectMany(l => l).Min(r => r.EnqueuedAtMs) + queueWaitMs;
            }
        }
    }
}
