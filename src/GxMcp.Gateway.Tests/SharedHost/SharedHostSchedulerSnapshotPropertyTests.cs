using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 17: Diagnostics snapshot consistency
    // Validates: Requirements 11.2, 11.6
    //
    // For any scheduler state, the snapshot reports a running operation with its owner attachment and elapsed time
    // exactly when one is in flight (otherwise idle), and lists queued requests with contiguous positions starting at
    // 1, each with exactly one wait reason. The diagnostics view carries the operation name only: no request
    // arguments, source content, Lease tokens, or credentials (design.md, "status_ack").
    //
    // Contract this test adds to SchedulerSnapshot (names follow the status_ack fields in design.md, which list
    // `running` (`operation`, `ownerAttachId`, `elapsedMs`) or `idle`, `queue[]` (`position`, `ownerAttachId`,
    // `waitMs`, `waitReason`, `operation` when `sta-busy`), and `queueDepth`). The existing members Running, Queued,
    // and WaitReason keep the meaning pinned by SharedHostSchedulerTests.cs; they expose QueuedRequest (with Payload)
    // for host use only, and the status_ack builder must read only the members below.
    // - IsIdle: true exactly when Running is null.
    // - RunningStatus: null when idle; otherwise a RunningOperationStatus with
    //     Operation     the running request's QueuedRequest.Operation ("method/action" name),
    //     OwnerAttachId the running request's SessionId,
    //     ElapsedMs     clock value at the Snapshot call minus the clock value when the scheduler handed the request
    //                   out to start (Submit with Started, Complete, or TryStartNext).
    // - QueueStatus: IReadOnlyList<QueueEntryStatus>, one entry per Queued request, in the same (start) order, with
    //     Position      1-based, contiguous (entry i has Position i + 1),
    //     OwnerAttachId the queued request's SessionId,
    //     WaitMs        clock value at the Snapshot call minus EnqueuedAtMs,
    //     WaitReason    exactly one WaitReason, equal to the snapshot's WaitReason,
    //     Operation     the running operation name when WaitReason is StaBusy, null otherwise (Requirement 11.6).
    // - QueueDepth: the number of queued requests (Queued.Count).
    // - RunningOperationStatus and QueueEntryStatus hold no QueuedRequest reference and no string other than the
    //   operation name and the attachment identifier. The snapshot is a point-in-time copy: later scheduler calls
    //   do not change a snapshot already taken, and Snapshot itself changes no state.
    //
    // Host conventions this test assumes (same as Property 1): the clock is injected and only moves forward, every
    // clock advance is followed by ExpireDue, clock values stay even and the queue wait is odd; the Worker becomes
    // unavailable only while idle or on Worker exit (SetWorkerAvailable(false, WorkerStarting) then Complete).
    public class SharedHostSchedulerSnapshotPropertyTests
    {
        private const int Iterations = 200;

        // Fake secret-looking marker seeded into request arguments, source text, Lease tokens, credentials, and cancel
        // tokens. No real secret is used.
        private const string Marker = "SECRET-MARKER-";

        [Fact]
        public void Property17_DiagnosticsSnapshot_IsConsistentAndCarriesOperationNameOnly()
        {
            SeededInterleavingRunner.Run(
                "Property 17: Diagnostics snapshot consistency",
                Iterations,
                c => new Simulation(c).Run());
        }

        private sealed class Simulation
        {
            private static readonly string[] Operations =
            {
                "genexus_read/source", "genexus_edit/save", "search/searchsource", "genexus_build/build"
            };

            private readonly PbtCase _c;
            private readonly int _depth;
            private readonly int _cap;
            private readonly int _wait;
            private readonly int _sessionCount;
            private readonly int _totalRequests;
            private readonly SharedHostScheduler _scheduler;
            private readonly ReferenceModel _model = new ReferenceModel();
            private readonly int[] _generation;
            private readonly Dictionary<string, int> _clientIdCounters = new Dictionary<string, int>();

            private long _now;
            private long _sequence;
            private int _submitted;
            private long _runningStartedAtMs;

            // A snapshot taken earlier and its rendering at that time, to check that later calls do not change it.
            private SchedulerSnapshot? _held;
            private string? _heldRender;

            internal Simulation(PbtCase c)
            {
                _c = c;
                _sessionCount = c.SmallInt(1, 4);
                _totalRequests = c.SmallInt(1, 32);
                _depth = c.SmallInt(1, 16);
                _cap = c.SmallInt(1, _depth);
                _wait = 1001 + 2 * c.Int(0, 2499);
                _now = 2L * c.Int(0, 1000000);
                _generation = new int[_sessionCount];
                _scheduler = new SharedHostScheduler(new SchedulerLimits(_depth, _cap, _wait), () => _now);
                _model.Available = true;
                _model.ReasonWhileUnavailable = WaitReason.StaBusy;
                c.Log($"config sessions={_sessionCount} requests={_totalRequests} depth={_depth} cap={_cap} waitMs={_wait} now={_now}");

                if (c.Chance(0.25)) SetUnavailable(WaitReason.WorkerStarting, "initial");
            }

            internal void Run()
            {
                CheckSnapshot();
                var minSteps = _totalRequests * 3;
                var step = 0;
                while (_submitted < _totalRequests || step < minSteps)
                {
                    step++;
                    var roll = _c.Int(0, 99);
                    if (roll < 40) { if (_submitted < _totalRequests) Submit(); else Finish(); }
                    else if (roll < 58) Finish();
                    else if (roll < 66) Cancel();
                    else if (roll < 80) AdvanceClockAndExpire();
                    else if (roll < 84) Detach();
                    else if (roll < 88) WorkerUnavailable();
                    else if (roll < 91) WorkerExit();
                    else WorkerReady();
                    CheckSnapshot();
                }

                if (!_model.Available) WorkerReady();
                CheckSnapshot();
                while (_model.Running != null)
                {
                    Finish();
                    CheckSnapshot();
                }
                Assert.True(_scheduler.Snapshot().IsIdle);
            }

            // ---- events ----

            private void Submit()
            {
                var session = SessionId(_c.Int(0, _sessionCount - 1));
                _clientIdCounters.TryGetValue(session, out var counter);
                counter++;
                _clientIdCounters[session] = counter;
                _submitted++;
                var n = _submitted.ToString(CultureInfo.InvariantCulture);

                var request = new QueuedRequest
                {
                    ChildRequestId = "r" + n,
                    SessionId = session,
                    ClientRequestIdJson = counter.ToString(CultureInfo.InvariantCulture),
                    Operation = _c.Pick(Operations),
                    CancelToken = _c.Bool() ? Marker + n + "-cancel" : null,
                    EnqueuedAtMs = _now,
                    Sequence = ++_sequence,
                    Payload = "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"arguments\":{"
                        + "\"name\":\"" + Marker + n + "-argument\","
                        + "\"source\":\"Event Start\\n  &x = '" + Marker + n + "-source'\\nEndEvent\","
                        + "\"leaseToken\":\"" + Marker + n + "-lease\","
                        + "\"password\":\"" + Marker + n + "-credential\"}}}"
                };

                var result = _scheduler.Submit(request);
                _c.Log($"submit {request.ChildRequestId} session={session} op={request.Operation} at={_now}"
                    + $" -> started={result.Started} queued={result.Queued} reject={result.RejectReason ?? "-"}");

                if (result.Started) MarkStarted(request);
                else if (result.Queued) _model.Enqueue(request);
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
                _model.Running = null;
                var next = _scheduler.Complete(running.ChildRequestId);
                _c.Log($"finish {running.ChildRequestId} at={_now} -> next={next?.ChildRequestId ?? "-"}");

                Assert.Equal(expectedNext?.ChildRequestId, next?.ChildRequestId);
                if (next != null) MarkStarted(next);
            }

            private void Cancel()
            {
                var session = SessionId(_c.Int(0, _sessionCount - 1));
                var queued = _model.QueuedOf(session);
                string clientId = "\"unknown\"";
                string? token = null;
                if (queued.Count > 0 && _c.Chance(0.85))
                {
                    var target = _c.Pick(queued);
                    if (target.CancelToken != null && _c.Bool()) token = target.CancelToken;
                    else clientId = target.ClientRequestIdJson;
                }

                var removed = _scheduler.Cancel(session, clientId, token).Removed;
                _c.Log($"cancel session={session} client={clientId} token={(token == null ? "-" : "set")}"
                    + $" -> removed=[{string.Join(",", removed.Select(r => r.ChildRequestId))}]");
                foreach (var r in removed) _model.Remove(r);
            }

            private void AdvanceClockAndExpire()
            {
                long advance = _c.Chance(0.6) ? 2L * _c.Int(0, 50) : 2L * _c.Int(0, _wait);
                _now += advance;

                var expected = _model.StartOrder().Where(r => _now - r.EnqueuedAtMs > _wait).Select(r => r.ChildRequestId).ToList();
                var expired = _scheduler.ExpireDue();
                var actual = expired.Select(e => e.Request.ChildRequestId).ToList();
                _c.Log($"advance +{advance} now={_now} expire -> [{string.Join(",", actual)}]");

                Assert.Equal(expected, actual);
                foreach (var e in expired) _model.Remove(e.Request);
            }

            private void Detach()
            {
                var slot = _c.Int(0, _sessionCount - 1);
                var session = SessionId(slot);
                var dropped = _scheduler.RemoveSession(session);
                _c.Log($"detach {session} -> dropped=[{string.Join(",", dropped.Select(r => r.ChildRequestId))}]");
                foreach (var r in dropped) _model.Remove(r);
                _generation[slot]++;
            }

            private void WorkerUnavailable()
            {
                if (_model.Running != null || !_model.Available)
                {
                    _c.Log("worker-unavailable skipped");
                    return;
                }
                SetUnavailable(_c.Bool() ? WaitReason.WorkerStarting : WaitReason.WorkerLimit, "idle");
            }

            private void WorkerExit()
            {
                var running = _model.Running;
                if (running == null)
                {
                    if (!_model.Available) SetUnavailable(_c.Bool() ? WaitReason.WorkerStarting : WaitReason.WorkerLimit, "reason-change");
                    else _c.Log("worker-exit skipped: idle");
                    return;
                }

                SetUnavailable(WaitReason.WorkerStarting, "exit");
                _model.Running = null;
                var next = _scheduler.Complete(running.ChildRequestId);
                _c.Log($"worker-exit releases {running.ChildRequestId} -> next={next?.ChildRequestId ?? "-"}");
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
                _c.Log($"worker-ready at={_now} -> started={started?.ChildRequestId ?? "-"}");

                Assert.Equal(expected?.ChildRequestId, started?.ChildRequestId);
                if (started != null) MarkStarted(started);
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
                Assert.Null(_model.Running);
                _model.Running = request;
                _runningStartedAtMs = _now;
                _c.Log($"start {request.ChildRequestId} at={_now}");
            }

            private string SessionId(int slot) =>
                "S" + slot.ToString(CultureInfo.InvariantCulture) + "g" + _generation[slot].ToString(CultureInfo.InvariantCulture);

            private void CheckSnapshot()
            {
                // A snapshot taken earlier is a point-in-time copy (later calls do not change it).
                if (_held != null)
                {
                    Assert.Equal(_heldRender, Render(_held));
                }

                var snapshot = _scheduler.Snapshot();
                var running = _model.Running;

                // Running operation exactly when one is in flight, otherwise idle (11.2).
                Assert.Equal(running?.ChildRequestId, snapshot.Running?.ChildRequestId);
                if (running == null)
                {
                    Assert.True(snapshot.IsIdle, "idle scheduler not reported as idle");
                    Assert.Null(snapshot.RunningStatus);
                }
                else
                {
                    Assert.False(snapshot.IsIdle, "scheduler with a running request reported as idle");
                    var status = snapshot.RunningStatus;
                    Assert.NotNull(status);
                    Assert.Equal(running.Operation, status!.Operation);
                    Assert.Equal(running.SessionId, status.OwnerAttachId);
                    Assert.Equal(_now - _runningStartedAtMs, (long)status.ElapsedMs);
                }

                // Queued entries: start order, contiguous 1-based positions, owner, wait time, one wait reason (11.6).
                var order = _model.StartOrder();
                Assert.Equal(order.Select(r => r.ChildRequestId).ToList(), snapshot.Queued.Select(r => r.ChildRequestId).ToList());
                Assert.Equal(order.Count, snapshot.QueueDepth);
                Assert.InRange(snapshot.QueueDepth, 0, _depth);
                Assert.Equal(order.Count, snapshot.QueueStatus.Count);

                var expectedReason = _model.Available ? WaitReason.StaBusy : _model.ReasonWhileUnavailable;
                if (order.Count > 0)
                {
                    Assert.Equal(expectedReason, snapshot.WaitReason);
                    // An available Worker with queued requests is busy with a running request (work-conserving).
                    if (expectedReason == WaitReason.StaBusy) Assert.NotNull(running);
                }
                for (var i = 0; i < order.Count; i++)
                {
                    var entry = snapshot.QueueStatus[i];
                    Assert.Equal(i + 1, entry.Position);
                    Assert.Equal(order[i].SessionId, entry.OwnerAttachId);
                    Assert.Equal(_now - order[i].EnqueuedAtMs, (long)entry.WaitMs);
                    Assert.Equal(expectedReason, entry.WaitReason);
                    Assert.Equal(expectedReason == WaitReason.StaBusy ? running!.Operation : null, entry.Operation);
                }

                // Operation name only: no arguments, source, Lease tokens, credentials, or cancel tokens (11.6).
                AssertNoSecrets(snapshot.RunningStatus, "RunningStatus");
                for (var i = 0; i < snapshot.QueueStatus.Count; i++)
                {
                    AssertNoSecrets(snapshot.QueueStatus[i], "QueueStatus[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                }

                // Snapshot changes no state: a second snapshot at the same clock value reports the same view.
                var render = Render(snapshot);
                Assert.Equal(render, Render(_scheduler.Snapshot()));

                if (_held == null || _c.Chance(0.3))
                {
                    _held = snapshot;
                    _heldRender = render;
                }
            }
        }

        private static string Render(SchedulerSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.Append("idle=").Append(snapshot.IsIdle).Append(" depth=").Append(snapshot.QueueDepth);
            var running = snapshot.RunningStatus;
            sb.Append(" running=");
            if (running == null) sb.Append('-');
            else sb.Append(running.Operation).Append('|').Append(running.OwnerAttachId).Append('|').Append(((long)running.ElapsedMs).ToString(CultureInfo.InvariantCulture));
            foreach (var e in snapshot.QueueStatus)
            {
                sb.Append(" [").Append(e.Position.ToString(CultureInfo.InvariantCulture)).Append('|').Append(e.OwnerAttachId)
                    .Append('|').Append(((long)e.WaitMs).ToString(CultureInfo.InvariantCulture)).Append('|').Append(e.WaitReason)
                    .Append('|').Append(e.Operation ?? "-").Append(']');
            }
            return sb.ToString();
        }

        // Reads every instance field and readable property of a diagnostics view, at any visibility, and fails if one
        // holds a QueuedRequest or a string (directly or in a collection) that contains the seeded marker.
        private static void AssertNoSecrets(object? view, string label)
        {
            if (view == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = view.GetType();
            foreach (var field in type.GetFields(flags))
            {
                Assert.False(typeof(QueuedRequest).IsAssignableFrom(field.FieldType), label + "." + field.Name + " holds a QueuedRequest");
                AssertValueHasNoSecret(field.GetValue(view), label + "." + field.Name);
            }
            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                Assert.False(typeof(QueuedRequest).IsAssignableFrom(property.PropertyType), label + "." + property.Name + " holds a QueuedRequest");
                AssertValueHasNoSecret(property.GetValue(view), label + "." + property.Name);
            }
        }

        private static void AssertValueHasNoSecret(object? value, string label)
        {
            switch (value)
            {
                case null:
                    return;
                case string s:
                    Assert.False(s.Contains(Marker, StringComparison.Ordinal), label + " carries request arguments or secret-like content");
                    return;
                case QueuedRequest _:
                    throw new Xunit.Sdk.XunitException(label + " holds a QueuedRequest");
                case IEnumerable items:
                    foreach (var item in items) AssertValueHasNoSecret(item, label + "[]");
                    return;
            }
        }

        // List-based reference model of the Worker_Scheduler queue order: per-Session FIFO lists plus a ring of
        // Sessions that hold at least one queued request, ordered by when each Session last went from empty to
        // non-empty (design.md, "Fairness rule").
        private sealed class ReferenceModel
        {
            private readonly List<string> _ring = new List<string>();
            private readonly Dictionary<string, List<QueuedRequest>> _bySession = new Dictionary<string, List<QueuedRequest>>();

            internal QueuedRequest? Running;
            internal bool Available;
            internal WaitReason ReasonWhileUnavailable;

            internal void Enqueue(QueuedRequest request)
            {
                if (!_bySession.TryGetValue(request.SessionId, out var list))
                {
                    list = new List<QueuedRequest>();
                    _bySession[request.SessionId] = list;
                }
                if (list.Count == 0) _ring.Add(request.SessionId);
                list.Add(request);
            }

            internal QueuedRequest? TakeNext()
            {
                if (_ring.Count == 0) return null;
                var session = _ring[0];
                _ring.RemoveAt(0);
                var list = _bySession[session];
                var next = list[0];
                list.RemoveAt(0);
                if (list.Count > 0) _ring.Add(session);
                return next;
            }

            internal void Remove(QueuedRequest request)
            {
                if (!_bySession.TryGetValue(request.SessionId, out var list)
                    || list.RemoveAll(r => r.ChildRequestId == request.ChildRequestId) != 1)
                {
                    throw new Xunit.Sdk.XunitException("reference model does not hold queued request " + request.ChildRequestId);
                }
                if (list.Count == 0) _ring.Remove(request.SessionId);
            }

            internal List<QueuedRequest> QueuedOf(string sessionId) =>
                _bySession.TryGetValue(sessionId, out var list) ? new List<QueuedRequest>(list) : new List<QueuedRequest>();

            internal List<QueuedRequest> StartOrder()
            {
                var ring = new Queue<string>(_ring);
                var cursors = _ring.ToDictionary(s => s, _ => 0);
                var order = new List<QueuedRequest>();
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
        }
    }
}
