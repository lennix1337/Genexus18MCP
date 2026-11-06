using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 2: Fair rotation and per-Session FIFO
    // Validates: Requirements 3.3, 4.7
    //
    // For any interleaving of submits from several Sessions, with any assignment of Operation_Class values to the
    // requests, the start sequence keeps each Session's requests in arrival order, and between two consecutive
    // starts of one Session at most one request of each other Session starts; reassigning Operation_Class values
    // does not change the start sequence.
    //
    // Each case generates one event script (submit, finish, idle Worker stop, Worker exit, Worker ready, clock
    // advance) over 2 to 4 Sessions and 1 to 48 requests, then runs that same script against a fresh
    // SharedHostScheduler once per Operation_Class assignment: random, flipped (every read becomes a write and the
    // reverse), all read, and all write. The Operation_Class of a request is carried only by QueuedRequest.Operation,
    // the command name the host passes in; the scheduler must not look at it (Requirement 4 criterion 7).
    //
    // Every run is compared step by step with a list-based reference model of the Fairness rule (design.md,
    // "SharedHostScheduler.cs"): a ring of Sessions that hold at least one queued request, ordered by when each
    // Session last went from empty to non-empty.
    //
    // Scope: cancellation and detach belong to Property 3 and expiry to Property 4, so this script never cancels or
    // detaches, and the queue wait is the maximum (600000 ms) while the clock advances far less than that. Host
    // conventions follow SharedHostSchedulerSafetyPropertyTests: ExpireDue after every clock advance; Worker exit is
    // SetWorkerAvailable(false, WorkerStarting) then Complete(running id); Worker ready is
    // SetWorkerAvailable(true, StaBusy) then TryStartNext.
    //
    // The fairness bound is checked where Requirement 3 criterion 3 applies it: when a Session's request starts and
    // that Session still has queued requests, each other Session starts at most once before that Session's next
    // start. A Session that empties and enqueues again later re-enters the ring at the tail, which the reference
    // model comparison covers.
    public class SharedHostSchedulerFairnessPropertyTests
    {
        private const int Iterations = 300;
        private const int QueueWaitMs = 600000;

        private static readonly string[] ReadOperations = { "read/ExtractSource", "object/Read", "search/SearchSource" };
        private static readonly string[] WriteOperations = { "write/ModifyVariable", "object/Delete", "patch/Apply" };

        [Fact]
        public void Property2_FairRotationAndPerSessionFifo_HoldForAnyOperationClassAssignment()
        {
            SeededInterleavingRunner.Run(
                "Property 2: Fair rotation and per-Session FIFO",
                Iterations,
                c =>
                {
                    var script = Script.Generate(c);
                    Trace? baseline = null;
                    string baselineLabel = string.Empty;
                    foreach (var (label, operations) in OperationAssignments(c, script.RequestCount))
                    {
                        c.Log("run assignment=" + label);
                        var trace = new Execution(c, script, operations).Run();
                        CheckFifoAndFairness(trace);

                        if (baseline == null)
                        {
                            baseline = trace;
                            baselineLabel = label;
                            continue;
                        }

                        // Reassigning Operation_Class values changes neither admission nor the start sequence (4.7).
                        Assert.True(
                            baseline.SubmitOutcomes.SequenceEqual(trace.SubmitOutcomes),
                            "submit outcomes differ between assignment " + baselineLabel + " and " + label);
                        Assert.True(
                            baseline.StartIds.SequenceEqual(trace.StartIds),
                            "start sequence differs between assignment " + baselineLabel + " [" + string.Join(",", baseline.StartIds)
                            + "] and " + label + " [" + string.Join(",", trace.StartIds) + "]");
                    }
                });
        }

        // Builds the Operation_Class assignments for one case. Each request gets a random class and a random command
        // name of that class; the other assignments keep the name index and change only the class.
        private static List<(string Label, string[] Operations)> OperationAssignments(PbtCase c, int requestCount)
        {
            var isWrite = new bool[requestCount];
            var nameIndex = new int[requestCount];
            for (var i = 0; i < requestCount; i++)
            {
                isWrite[i] = c.Bool();
                nameIndex[i] = c.Int(0, ReadOperations.Length - 1);
            }

            string[] Build(Func<int, bool> writeAt)
            {
                var ops = new string[requestCount];
                for (var i = 0; i < requestCount; i++)
                {
                    ops[i] = writeAt(i) ? WriteOperations[nameIndex[i]] : ReadOperations[nameIndex[i]];
                }
                return ops;
            }

            return new List<(string, string[])>
            {
                ("random", Build(i => isWrite[i])),
                ("flipped", Build(i => !isWrite[i])),
                ("all-read", Build(_ => false)),
                ("all-write", Build(_ => true)),
            };
        }

        // Per-Session FIFO and the fairness bound, checked on the full start sequence of one run.
        private static void CheckFifoAndFairness(Trace trace)
        {
            // Every admitted request starts (no cancel, detach, or expiry in this script), and each Session's
            // requests start in arrival order (3.3).
            var sessions = trace.AdmittedBySession.Keys.Union(trace.Starts.Select(s => s.SessionId)).ToList();
            foreach (var session in sessions)
            {
                var admitted = trace.AdmittedBySession.TryGetValue(session, out var list) ? list : new List<long>();
                var started = trace.Starts.Where(s => s.SessionId == session).Select(s => s.Sequence).ToList();
                Assert.True(
                    admitted.SequenceEqual(started),
                    "Session " + session + " admitted sequences [" + string.Join(",", admitted)
                    + "] but started [" + string.Join(",", started) + "]");
            }

            for (var i = 0; i < trace.Starts.Count; i++)
            {
                var first = trace.Starts[i];
                if (!first.SessionStillQueued) continue;

                var j = i + 1;
                while (j < trace.Starts.Count && trace.Starts[j].SessionId != first.SessionId) j++;
                Assert.True(
                    j < trace.Starts.Count,
                    "Session " + first.SessionId + " still had queued requests after " + first.Id + " started, but never started again");

                // Between two consecutive starts of one Session, at most one request of each other Session starts (3.3).
                var between = trace.Starts.Skip(i + 1).Take(j - i - 1).ToList();
                foreach (var group in between.GroupBy(s => s.SessionId))
                {
                    Assert.True(
                        group.Count() <= 1,
                        "between " + first.Id + " and " + trace.Starts[j].Id + " of Session " + first.SessionId + ", Session "
                        + group.Key + " started " + group.Count() + " requests: " + string.Join(",", group.Select(s => s.Id)));
                }
            }
        }

        private enum EventKind
        {
            Submit,
            Finish,
            WorkerIdleStop,
            WorkerExit,
            WorkerReady,
            Advance
        }

        private readonly struct ScriptEvent
        {
            internal ScriptEvent(EventKind kind, int slot = 0, WaitReason reason = WaitReason.StaBusy, long advanceMs = 0)
            {
                Kind = kind;
                Slot = slot;
                Reason = reason;
                AdvanceMs = advanceMs;
            }

            internal EventKind Kind { get; }
            internal int Slot { get; }
            internal WaitReason Reason { get; }
            internal long AdvanceMs { get; }
        }

        // One generated interleaving, replayed unchanged for every Operation_Class assignment.
        private sealed class Script
        {
            internal int SessionCount { get; private set; }
            internal int RequestCount { get; private set; }
            internal int Depth { get; private set; }
            internal int Cap { get; private set; }
            internal long StartMs { get; private set; }
            internal bool StartUnavailable { get; private set; }
            internal List<ScriptEvent> Events { get; } = new List<ScriptEvent>();

            internal static Script Generate(PbtCase c)
            {
                var script = new Script
                {
                    SessionCount = c.SmallInt(2, 4),
                    RequestCount = c.SmallInt(1, 48),
                    StartMs = c.Int(0, 1000000),
                    StartUnavailable = c.Chance(0.25)
                };
                script.Depth = c.SmallInt(1, 64);
                script.Cap = c.SmallInt(1, script.Depth);

                var submits = 0;
                var minSteps = script.RequestCount * 2;
                var step = 0;
                while (submits < script.RequestCount || step < minSteps)
                {
                    step++;
                    var roll = c.Int(0, 99);
                    if (roll < 45 && submits < script.RequestCount)
                    {
                        script.Events.Add(new ScriptEvent(EventKind.Submit, slot: c.Int(0, script.SessionCount - 1)));
                        submits++;
                    }
                    else if (roll < 75) script.Events.Add(new ScriptEvent(EventKind.Finish));
                    else if (roll < 80) script.Events.Add(new ScriptEvent(EventKind.WorkerIdleStop, reason: c.Bool() ? WaitReason.WorkerStarting : WaitReason.WorkerLimit));
                    else if (roll < 84) script.Events.Add(new ScriptEvent(EventKind.WorkerExit));
                    else if (roll < 92) script.Events.Add(new ScriptEvent(EventKind.WorkerReady));
                    else script.Events.Add(new ScriptEvent(EventKind.Advance, advanceMs: c.Int(0, 100)));
                }

                c.Log($"script sessions={script.SessionCount} requests={script.RequestCount} depth={script.Depth} cap={script.Cap}"
                    + $" waitMs={QueueWaitMs} now={script.StartMs} startUnavailable={script.StartUnavailable} events={script.Events.Count}");
                return script;
            }
        }

        private sealed class StartRecord
        {
            internal StartRecord(string id, string sessionId, long sequence, bool sessionStillQueued)
            {
                Id = id;
                SessionId = sessionId;
                Sequence = sequence;
                SessionStillQueued = sessionStillQueued;
            }

            internal string Id { get; }
            internal string SessionId { get; }
            internal long Sequence { get; }

            // True when the Session still held queued requests right after this start.
            internal bool SessionStillQueued { get; }
        }

        private sealed class Trace
        {
            internal List<string> SubmitOutcomes { get; } = new List<string>();
            internal List<StartRecord> Starts { get; } = new List<StartRecord>();
            internal Dictionary<string, List<long>> AdmittedBySession { get; } = new Dictionary<string, List<long>>();
            internal IEnumerable<string> StartIds => Starts.Select(s => s.Id);
        }

        // Runs one script against a fresh scheduler and the reference model.
        private sealed class Execution
        {
            private readonly PbtCase _c;
            private readonly Script _script;
            private readonly string[] _operations;
            private readonly SharedHostScheduler _scheduler;
            private readonly ReferenceModel _model = new ReferenceModel();
            private readonly Trace _trace = new Trace();

            private long _now;
            private long _sequence;
            private int _submitted;

            internal Execution(PbtCase c, Script script, string[] operations)
            {
                _c = c;
                _script = script;
                _operations = operations;
                _now = script.StartMs;
                _scheduler = new SharedHostScheduler(new SchedulerLimits(script.Depth, script.Cap, QueueWaitMs), () => _now);
                _model.Available = true;
                if (script.StartUnavailable) SetUnavailable(WaitReason.WorkerStarting, "initial");
            }

            internal Trace Run()
            {
                foreach (var e in _script.Events)
                {
                    switch (e.Kind)
                    {
                        case EventKind.Submit: Submit(e.Slot); break;
                        case EventKind.Finish: Finish(); break;
                        case EventKind.WorkerIdleStop: WorkerIdleStop(e.Reason); break;
                        case EventKind.WorkerExit: WorkerExit(); break;
                        case EventKind.WorkerReady: WorkerReady(); break;
                        case EventKind.Advance: Advance(e.AdvanceMs); break;
                    }
                    CheckQueue();
                }

                _c.Log("drain");
                if (!_model.Available) WorkerReady();
                while (_model.Running != null)
                {
                    Finish();
                    CheckQueue();
                }
                Assert.Equal(0, _model.Count);
                Assert.Empty(_scheduler.Snapshot().Queued);
                Assert.Null(_scheduler.Snapshot().Running);
                return _trace;
            }

            private void Submit(int slot)
            {
                var index = _submitted++;
                var session = "S" + slot.ToString(CultureInfo.InvariantCulture);
                var request = new QueuedRequest
                {
                    ChildRequestId = "r" + (index + 1).ToString(CultureInfo.InvariantCulture),
                    SessionId = session,
                    ClientRequestIdJson = (index + 1).ToString(CultureInfo.InvariantCulture),
                    Operation = _operations[index],
                    CancelToken = null,
                    EnqueuedAtMs = _now,
                    Sequence = ++_sequence,
                    Payload = "{}"
                };

                string? expectedReject = _model.Count >= _script.Depth ? "queue-full"
                    : _model.CountFor(session) >= _script.Cap ? "session-queue-limit"
                    : null;
                var expectStart = expectedReject == null && _model.Running == null && _model.Available && _model.Count == 0;

                var result = _scheduler.Submit(request);
                _c.Log($"submit {request.ChildRequestId} session={session} op={request.Operation} at={_now}"
                    + $" -> started={result.Started} queued={result.Queued} reject={result.RejectReason ?? "-"}");

                Assert.Equal(expectedReject, result.RejectReason);
                Assert.Equal(expectStart, result.Started);
                Assert.Equal(expectedReject == null && !expectStart, result.Queued);
                _trace.SubmitOutcomes.Add(request.ChildRequestId + ":" + (result.RejectReason ?? (result.Started ? "started" : "queued")));

                if (expectedReject != null) return;

                if (!_trace.AdmittedBySession.TryGetValue(session, out var admitted))
                {
                    admitted = new List<long>();
                    _trace.AdmittedBySession[session] = admitted;
                }
                admitted.Add(request.Sequence);

                if (expectStart) MarkStarted(request);
                else _model.Enqueue(request);
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
                _c.Log($"finish {running.ChildRequestId} -> next={next?.ChildRequestId ?? "-"} expected={expectedNext?.ChildRequestId ?? "-"}");

                Assert.Equal(expectedNext?.ChildRequestId, next?.ChildRequestId);
                if (expectedNext != null) MarkStarted(expectedNext);
            }

            private void WorkerIdleStop(WaitReason reason)
            {
                if (_model.Running != null || !_model.Available)
                {
                    _c.Log("worker-idle-stop skipped");
                    return;
                }
                SetUnavailable(reason, "idle");
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
                var expected = _model.Running == null ? _model.TakeNext() : null;
                var started = _scheduler.TryStartNext();
                _c.Log($"worker-ready -> started={started?.ChildRequestId ?? "-"} expected={expected?.ChildRequestId ?? "-"}");

                Assert.Equal(expected?.ChildRequestId, started?.ChildRequestId);
                if (expected != null) MarkStarted(expected);
            }

            private void Advance(long ms)
            {
                _now += ms;
                var expired = _scheduler.ExpireDue();
                _c.Log($"advance +{ms} now={_now} expired={expired.Count}");
                // The clock never gets near the queue wait in this script.
                Assert.Empty(expired);
            }

            private void SetUnavailable(WaitReason reason, string cause)
            {
                _scheduler.SetWorkerAvailable(false, reason);
                _model.Available = false;
                _c.Log($"worker-unavailable cause={cause} reason={reason}");
            }

            private void MarkStarted(QueuedRequest request)
            {
                Assert.Null(_model.Running);
                _model.Running = request;
                var stillQueued = _model.CountFor(request.SessionId) > 0;
                _trace.Starts.Add(new StartRecord(request.ChildRequestId, request.SessionId, request.Sequence, stillQueued));
                _c.Log($"start {request.ChildRequestId} session={request.SessionId} sessionStillQueued={stillQueued}");
            }

            private void CheckQueue()
            {
                var snapshot = _scheduler.Snapshot();
                Assert.Equal(_model.Running?.ChildRequestId, snapshot.Running?.ChildRequestId);
                Assert.Equal(
                    _model.StartOrder().Select(r => r.ChildRequestId).ToList(),
                    snapshot.Queued.Select(r => r.ChildRequestId).ToList());
            }
        }

        // List-based reference model of the Fairness rule: per-Session FIFO lists plus a ring of Sessions that hold at
        // least one queued request, ordered by when each Session last went from empty to non-empty.
        private sealed class ReferenceModel
        {
            private readonly List<string> _ring = new List<string>();
            private readonly Dictionary<string, List<QueuedRequest>> _bySession = new Dictionary<string, List<QueuedRequest>>();

            internal QueuedRequest? Running;
            internal bool Available;
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
        }
    }
}
