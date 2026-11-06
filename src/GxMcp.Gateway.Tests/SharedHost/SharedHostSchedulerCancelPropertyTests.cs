using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 3: Cancellation and detach isolation
    // Validates: Requirements 2.6, 3.7, 3.11, 8.3, 8.7
    //
    // Each case builds a seeded queue state over 2 to 4 Sessions (optionally with a running request, optionally with
    // the Worker unavailable so nothing runs), then applies Cancel or RemoveSession for one Session S and compares the
    // scheduler state right before and right after the call.
    //
    // JSON-RPC ids and cancel tokens come from small pools shared by every Session, so the same id and the same token
    // are often held by several Sessions at once. Within one Session an id is used at most once (a JSON-RPC id is
    // unique among a client's live requests), and the running request's id is never reused by its own Session.
    //
    // Contract used (the same one SharedHostSchedulerTests and SharedHostSchedulerSafetyPropertyTests pin):
    // - Cancel(session, clientRequestIdJson, cancelToken) removes the queued requests of that Session whose
    //   ClientRequestIdJson matches, or whose CancelToken matches a non-null cancelToken. It never removes the running
    //   request and never touches another Session.
    // - RemoveSession(session) removes exactly that Session's queued requests; its running request keeps running.
    //
    // "Relative order of the remaining requests is unchanged" is checked as the scheduler's ordering state: each
    // Session's FIFO sequence keeps its order, and the Session rotation (ring) keeps its order, minus any Session that
    // became empty. The expected Snapshot().Queued after the call is the round-robin interleaving of exactly that
    // state. A plain subsequence check of the start order would be wrong: removing a1 from start order a1, b1, a2
    // makes A's next turn come earlier (a2, b1), as SharedHostSchedulerTests already pins for b1.
    //
    // After the operations, the queue is drained and the test checks that no removed request ever starts (removed
    // "without executing it") and that the remaining requests start in the Snapshot().Queued order.
    public class SharedHostSchedulerCancelPropertyTests
    {
        private const int Iterations = 200;

        [Fact]
        public void Property3_CancelOwnedId_RemovesOnlyThatRequestOfCancellingSession()
        {
            SeededInterleavingRunner.Run(
                "Property 3: Cancellation and detach isolation (cancel owned id)",
                Iterations,
                c =>
                {
                    var h = new Harness(c);
                    h.Build();
                    h.CancelOwned();
                    h.DrainAndVerify();
                });
        }

        [Fact]
        public void Property3_CancelUnownedId_ChangesNothing()
        {
            SeededInterleavingRunner.Run(
                "Property 3: Cancellation and detach isolation (cancel unowned id)",
                Iterations,
                c =>
                {
                    var h = new Harness(c);
                    h.Build();
                    h.CancelUnowned();
                    h.DrainAndVerify();
                });
        }

        [Fact]
        public void Property3_RemoveSession_RemovesExactlyThatSessionsQueuedRequests()
        {
            SeededInterleavingRunner.Run(
                "Property 3: Cancellation and detach isolation (remove Session)",
                Iterations,
                c =>
                {
                    var h = new Harness(c);
                    h.Build();
                    h.RemoveSession();
                    h.DrainAndVerify();
                });
        }

        [Fact]
        public void Property3_CancelAndDetachSequences_KeepIsolationAtEveryStep()
        {
            SeededInterleavingRunner.Run(
                "Property 3: Cancellation and detach isolation (sequences)",
                Iterations,
                c =>
                {
                    var h = new Harness(c);
                    h.Build();
                    var ops = c.SmallInt(1, 8);
                    for (var i = 0; i < ops; i++)
                    {
                        var roll = c.Int(0, 2);
                        if (roll == 0) h.CancelOwned();
                        else if (roll == 1) h.CancelUnowned();
                        else h.RemoveSession();
                    }
                    h.DrainAndVerify();
                });
        }

        private sealed class Harness
        {
            private const int QueueWaitMs = 600000;
            private const int Capacity = 64;

            // Shared by every Session, so ids collide across Sessions.
            private static readonly string[] ClientIdPool = { "1", "2", "3", "4", "\"a\"", "\"b\"", "5", "6" };
            private static readonly string?[] TokenPool = { null, null, "tok-1", "tok-2", "tok-3" };
            private const string UnusedToken = "tok-0";
            private const string UnknownClientId = "\"unknown\"";

            private readonly PbtCase _c;
            private readonly SharedHostScheduler _scheduler;
            private readonly List<string> _sessions = new List<string>();
            private readonly Dictionary<string, QueuedRequest> _all = new Dictionary<string, QueuedRequest>();
            private readonly Dictionary<string, HashSet<string>> _usedIds = new Dictionary<string, HashSet<string>>();
            private readonly HashSet<string> _removed = new HashSet<string>();

            private long _now;
            private long _sequence;
            private bool _available = true;
            private QueuedRequest? _running;

            internal Harness(PbtCase c)
            {
                _c = c;
                _now = c.Int(0, 1000000);
                _scheduler = new SharedHostScheduler(new SchedulerLimits(Capacity, Capacity, QueueWaitMs), () => _now);
                var sessionCount = c.SmallInt(2, 4);
                for (var i = 0; i < sessionCount; i++)
                {
                    var id = "S" + i.ToString(CultureInfo.InvariantCulture);
                    _sessions.Add(id);
                    _usedIds[id] = new HashSet<string>();
                }
                c.Log($"config sessions={sessionCount} now={_now}");
            }

            // ---- state construction ----

            internal void Build()
            {
                if (_c.Chance(0.3))
                {
                    _scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);
                    _available = false;
                    _c.Log("worker-unavailable reason=WorkerStarting");
                }

                // Leave room for the two extra submits below within each Session's id pool.
                var submits = _c.SmallInt(1, Math.Min(24, _sessions.Count * ClientIdPool.Length - 2));
                for (var i = 0; i < submits; i++)
                {
                    if (_running != null && _c.Chance(0.15)) CompleteRunning();
                    Submit();
                    _now += _c.Int(0, 5);
                }
                // Guarantee at least one queued request; at most two more submits are needed.
                while (_scheduler.Snapshot().Queued.Count == 0)
                {
                    Submit();
                }
                _c.Log("state " + Describe(_scheduler.Snapshot()));
            }

            private void Submit()
            {
                var candidates = _sessions.Where(s => _usedIds[s].Count < ClientIdPool.Length).ToList();
                var session = _c.Pick(candidates);
                var free = ClientIdPool.Where(id => !_usedIds[session].Contains(id)).ToList();
                var clientId = _c.Pick(free);
                _usedIds[session].Add(clientId);

                var request = new QueuedRequest
                {
                    ChildRequestId = "r" + (_all.Count + 1).ToString(CultureInfo.InvariantCulture),
                    SessionId = session,
                    ClientRequestIdJson = clientId,
                    Operation = "genexus_read/source",
                    CancelToken = _c.Pick(TokenPool),
                    EnqueuedAtMs = _now,
                    Sequence = ++_sequence,
                    Payload = "{}"
                };
                _all[request.ChildRequestId] = request;

                var result = _scheduler.Submit(request);
                _c.Log($"submit {request.ChildRequestId} session={session} client={clientId} token={request.CancelToken ?? "-"}"
                    + $" -> started={result.Started} queued={result.Queued} reject={result.RejectReason ?? "-"}");
                Assert.Null(result.RejectReason);
                if (result.Started) _running = request;
            }

            private void CompleteRunning()
            {
                var finished = _running!;
                _running = _scheduler.Complete(finished.ChildRequestId);
                _c.Log($"finish {finished.ChildRequestId} -> next={_running?.ChildRequestId ?? "-"}");
            }

            // ---- operations under test ----

            internal void CancelOwned()
            {
                var before = _scheduler.Snapshot();
                var owners = before.Queued.Select(r => r.SessionId).Distinct().ToList();
                if (owners.Count == 0)
                {
                    _c.Log("cancel-owned: nothing queued, falls back to cancel-unowned");
                    CancelUnowned();
                    return;
                }

                var session = _c.Pick(owners);
                var own = QueuedOf(before, session);
                var target = _c.Pick(own);
                var token = _c.Chance(0.5) ? null : _c.Pick(TokenPool);

                var expected = own
                    .Where(r => r.ClientRequestIdJson == target.ClientRequestIdJson || (token != null && r.CancelToken == token))
                    .Select(r => r.ChildRequestId)
                    .ToList();
                // With no token the id alone matches, and it is unique among the Session's live requests.
                if (token == null) Assert.Equal(new[] { target.ChildRequestId }, expected);

                var sharedWith = before.Queued
                    .Where(r => r.SessionId != session && _all[r.ChildRequestId].ClientRequestIdJson == target.ClientRequestIdJson)
                    .Select(r => r.ChildRequestId)
                    .ToList();

                var result = _scheduler.Cancel(session, target.ClientRequestIdJson, token);
                _c.Log($"cancel-owned session={session} client={target.ClientRequestIdJson} token={token ?? "-"}"
                    + $" sameIdElsewhere=[{string.Join(",", sharedWith)}] -> removed=[{string.Join(",", result.Removed.Select(r => r.ChildRequestId))}]");

                VerifyRemoval(before, session, result.Removed, expected);
            }

            internal void CancelUnowned()
            {
                var before = _scheduler.Snapshot();
                var session = _c.Pick(_sessions);
                var own = QueuedOf(before, session);
                var ownIds = new HashSet<string>(own.Select(r => r.ClientRequestIdJson));
                var ownTokens = new HashSet<string>(own.Where(r => r.CancelToken != null).Select(r => r.CancelToken!));

                // Ids S does not own among its queued requests: ids of other Sessions' queued requests, the running
                // request's id (whichever Session owns it), and an id nobody uses.
                var otherIds = before.Queued
                    .Where(r => r.SessionId != session)
                    .Select(r => _all[r.ChildRequestId].ClientRequestIdJson)
                    .Where(id => !ownIds.Contains(id))
                    .Distinct()
                    .ToList();
                var candidates = new List<string>(otherIds);
                if (before.Running != null && !ownIds.Contains(before.Running.ClientRequestIdJson))
                {
                    candidates.Add(_all[before.Running.ChildRequestId].ClientRequestIdJson);
                }
                candidates.Add(UnknownClientId);
                var clientId = otherIds.Count > 0 && _c.Chance(0.6) ? _c.Pick(otherIds) : _c.Pick(candidates);

                string? token = null;
                if (_c.Bool())
                {
                    var tokens = new[] { "tok-1", "tok-2", "tok-3", UnusedToken }.Where(t => !ownTokens.Contains(t)).ToList();
                    token = _c.Pick(tokens);
                }

                var deadlineBefore = _scheduler.NextDeadlineMs;
                var result = _scheduler.Cancel(session, clientId, token);
                var after = _scheduler.Snapshot();
                _c.Log($"cancel-unowned session={session} client={clientId} token={token ?? "-"} running={before.Running?.ChildRequestId ?? "-"}"
                    + $" -> removed=[{string.Join(",", result.Removed.Select(r => r.ChildRequestId))}]");

                // Ignored: nothing removed, every request of every Session stays queued or running, order unchanged.
                Assert.Empty(result.Removed);
                Assert.Equal(before.Running?.ChildRequestId, after.Running?.ChildRequestId);
                Assert.Equal(Ids(before.Queued), Ids(after.Queued));
                Assert.Equal(before.WaitReason, after.WaitReason);
                Assert.Equal(deadlineBefore, _scheduler.NextDeadlineMs);
            }

            internal void RemoveSession()
            {
                var before = _scheduler.Snapshot();
                var session = _c.Pick(_sessions);
                var expected = QueuedOf(before, session).Select(r => r.ChildRequestId).ToList();

                var dropped = _scheduler.RemoveSession(session);
                _c.Log($"detach {session} running={before.Running?.ChildRequestId ?? "-"}"
                    + $" -> dropped=[{string.Join(",", dropped.Select(r => r.ChildRequestId))}]");

                VerifyRemoval(before, session, dropped, expected);
            }

            // ---- checks ----

            private void VerifyRemoval(SchedulerSnapshot before, string session, IReadOnlyList<QueuedRequest> removed, List<string> expectedIds)
            {
                var after = _scheduler.Snapshot();
                var removedIds = removed.Select(r => r.ChildRequestId).ToList();

                // Exactly the expected requests, each once, all owned by the acting Session.
                Assert.Equal(expectedIds.OrderBy(id => id, StringComparer.Ordinal), removedIds.OrderBy(id => id, StringComparer.Ordinal));
                Assert.All(removed, r => Assert.Equal(session, r.SessionId));

                // The running request is never removed, whoever owns it (2.6, 8.3).
                Assert.Equal(before.Running?.ChildRequestId, after.Running?.ChildRequestId);

                // Remaining requests keep their per-Session order and the Session rotation order (3.7, 3.11).
                var expectedQueue = InterleaveWithout(before.Queued, new HashSet<string>(removedIds));
                Assert.Equal(expectedQueue, Ids(after.Queued));
                Assert.Equal(QueuedSessionsOtherThan(before, session), QueuedSessionsOtherThan(after, session));

                var expectedDeadline = after.Queued.Count == 0
                    ? (long?)null
                    : after.Queued.Min(r => _all[r.ChildRequestId].EnqueuedAtMs) + QueueWaitMs;
                Assert.Equal(expectedDeadline, _scheduler.NextDeadlineMs);
                if (after.Queued.Count > 0) Assert.Equal(before.WaitReason, after.WaitReason);

                foreach (var id in removedIds) _removed.Add(id);
            }

            internal void DrainAndVerify()
            {
                var queuedOrder = Ids(_scheduler.Snapshot().Queued);
                _c.Log("drain queued=[" + string.Join(",", queuedOrder) + "]");

                var started = new List<string>();
                if (!_available)
                {
                    _scheduler.SetWorkerAvailable(true, WaitReason.StaBusy);
                    _available = true;
                    var first = _scheduler.TryStartNext();
                    if (first != null)
                    {
                        started.Add(first.ChildRequestId);
                        _running = first;
                    }
                }
                var guard = 0;
                while (_running != null)
                {
                    Assert.True(++guard <= _all.Count + 1, "drain did not terminate");
                    CompleteRunning();
                    if (_running != null) started.Add(_running.ChildRequestId);
                }
                _c.Log("drain started=[" + string.Join(",", started) + "]");

                // Removed requests never execute; the remaining ones start in Snapshot().Queued order.
                foreach (var id in _removed) Assert.DoesNotContain(id, started);
                Assert.Equal(queuedOrder, started);
                Assert.Empty(_scheduler.Snapshot().Queued);
                Assert.Null(_scheduler.NextDeadlineMs);
            }

            // ---- helpers ----

            private List<QueuedRequest> QueuedOf(SchedulerSnapshot snapshot, string session) =>
                snapshot.Queued.Where(r => r.SessionId == session).Select(r => _all[r.ChildRequestId]).ToList();

            private static List<string> Ids(IEnumerable<QueuedRequest> requests) => requests.Select(r => r.ChildRequestId).ToList();

            // Per-Session sequences of every other Session, in rotation order: this must be identical before and after
            // an operation by the given Session.
            private List<string> QueuedSessionsOtherThan(SchedulerSnapshot snapshot, string session) =>
                snapshot.Queued
                    .Where(r => r.SessionId != session)
                    .GroupBy(r => r.SessionId)
                    .Select(g => g.Key + ":" + string.Join(",", g.Select(r => r.ChildRequestId)))
                    .ToList();

            // Rebuilds the ring (Session first-appearance order in start order) and per-Session FIFO lists from a
            // start order, drops the removed requests and any Session left empty, and interleaves round-robin again.
            private List<string> InterleaveWithout(IReadOnlyList<QueuedRequest> startOrder, ISet<string> removedIds)
            {
                var ring = new List<string>();
                var lists = new Dictionary<string, Queue<string>>();
                foreach (var r in startOrder)
                {
                    var owner = _all[r.ChildRequestId].SessionId;
                    if (!lists.TryGetValue(owner, out var list))
                    {
                        list = new Queue<string>();
                        lists[owner] = list;
                        ring.Add(owner);
                    }
                    if (!removedIds.Contains(r.ChildRequestId)) list.Enqueue(r.ChildRequestId);
                }

                var turn = new Queue<string>(ring.Where(s => lists[s].Count > 0));
                var order = new List<string>();
                while (turn.Count > 0)
                {
                    var s = turn.Dequeue();
                    order.Add(lists[s].Dequeue());
                    if (lists[s].Count > 0) turn.Enqueue(s);
                }
                return order;
            }

            private string Describe(SchedulerSnapshot snapshot)
            {
                string One(QueuedRequest r)
                {
                    var q = _all[r.ChildRequestId];
                    return $"{q.ChildRequestId}({q.SessionId},{q.ClientRequestIdJson},{q.CancelToken ?? "-"})";
                }
                return $"running={(snapshot.Running != null ? One(snapshot.Running) : "-")} available={_available}"
                    + $" queued=[{string.Join(" ", snapshot.Queued.Select(One))}]";
            }
        }
    }
}
