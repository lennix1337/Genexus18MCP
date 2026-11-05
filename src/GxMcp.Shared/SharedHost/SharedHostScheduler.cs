#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GxMcp.Common.SharedHost
{
    // Why queued requests are waiting. StaBusy while the Worker is available (the dispatch window is taken);
    // otherwise the reason the host reported with SetWorkerAvailable(false, ...).
    internal enum WaitReason
    {
        StaBusy,
        WorkerStarting,
        WorkerLimit
    }

    // Queue bounds of one Worker_Scheduler. Range validation against the configuration (1 to 64, 1000 to 600000 ms)
    // belongs to the settings resolver; this type only refuses values the scheduler cannot work with.
    internal sealed class SchedulerLimits
    {
        internal SchedulerLimits(int queueDepth, int sessionQueueCap, int queueWaitMs)
        {
            if (queueDepth < 1) throw new ArgumentOutOfRangeException(nameof(queueDepth), queueDepth, "Queue depth must be at least 1.");
            if (sessionQueueCap < 1 || sessionQueueCap > queueDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionQueueCap), sessionQueueCap, "Session queue cap must be from 1 to the queue depth.");
            }
            if (queueWaitMs < 1) throw new ArgumentOutOfRangeException(nameof(queueWaitMs), queueWaitMs, "Queue wait must be at least 1 ms.");

            QueueDepth = queueDepth;
            SessionQueueCap = sessionQueueCap;
            QueueWaitMs = queueWaitMs;
        }

        internal int QueueDepth { get; }
        internal int SessionQueueCap { get; }
        internal int QueueWaitMs { get; }
    }

    // One SDK_Operation as the Worker_Host hands it to the scheduler. The scheduler reads only SessionId,
    // ClientRequestIdJson, CancelToken, ChildRequestId, and EnqueuedAtMs; Operation is for diagnostics only and
    // never affects admission or order (Requirement 4 criterion 7). Payload is for the host and is never copied
    // into a diagnostics view.
    internal sealed class QueuedRequest
    {
        public string ChildRequestId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string ClientRequestIdJson { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string? CancelToken { get; set; }
        public long EnqueuedAtMs { get; set; }
        public long Sequence { get; set; }
        public string Payload { get; set; } = string.Empty;
    }

    internal sealed class SubmitResult
    {
        internal const string QueueFull = "queue-full";
        internal const string SessionQueueLimit = "session-queue-limit";

        private SubmitResult(bool started, bool queued, string? rejectReason)
        {
            Started = started;
            Queued = queued;
            RejectReason = rejectReason;
        }

        // The request owns the dispatch window now; the host writes it to the child.
        internal bool Started { get; }

        // The request was appended to the Request_Queue.
        internal bool Queued { get; }

        // "queue-full" or "session-queue-limit" when rejected, null otherwise.
        internal string? RejectReason { get; }

        internal static SubmitResult StartedNow() => new SubmitResult(true, false, null);
        internal static SubmitResult Enqueued() => new SubmitResult(false, true, null);
        internal static SubmitResult Rejected(string reason) => new SubmitResult(false, false, reason);
    }

    internal sealed class CancelResult
    {
        internal CancelResult(IReadOnlyList<QueuedRequest> removed)
        {
            Removed = removed;
        }

        // Queued requests of the cancelling Session that matched; empty when nothing queued matched.
        internal IReadOnlyList<QueuedRequest> Removed { get; }
    }

    internal sealed class ExpiredRequest
    {
        internal ExpiredRequest(QueuedRequest request, int position)
        {
            Request = request;
            Position = position;
        }

        internal QueuedRequest Request { get; }

        // 1-based position in the start order as it stood when the request was removed.
        internal int Position { get; }
    }

    // Diagnostics view of the running request: operation name, owner, and elapsed time only.
    internal sealed class RunningOperationStatus
    {
        internal RunningOperationStatus(string operation, string ownerAttachId, long elapsedMs)
        {
            Operation = operation;
            OwnerAttachId = ownerAttachId;
            ElapsedMs = elapsedMs;
        }

        internal string Operation { get; }
        internal string OwnerAttachId { get; }
        internal long ElapsedMs { get; }
    }

    // Diagnostics view of one queued request. Operation is the running operation name when WaitReason is StaBusy,
    // null otherwise (Requirement 11 criterion 6).
    internal sealed class QueueEntryStatus
    {
        internal QueueEntryStatus(int position, string ownerAttachId, long waitMs, WaitReason waitReason, string? operation)
        {
            Position = position;
            OwnerAttachId = ownerAttachId;
            WaitMs = waitMs;
            WaitReason = waitReason;
            Operation = operation;
        }

        internal int Position { get; }
        internal string OwnerAttachId { get; }
        internal long WaitMs { get; }
        internal WaitReason WaitReason { get; }
        internal string? Operation { get; }
    }

    // Point-in-time copy of the scheduler state. Running and Queued expose QueuedRequest (with Payload) for host use;
    // a status_ack builder reads only IsIdle, RunningStatus, QueueStatus, and QueueDepth.
    internal sealed class SchedulerSnapshot
    {
        internal SchedulerSnapshot(
            QueuedRequest? running,
            IReadOnlyList<QueuedRequest> queued,
            WaitReason waitReason,
            RunningOperationStatus? runningStatus,
            IReadOnlyList<QueueEntryStatus> queueStatus)
        {
            Running = running;
            Queued = queued;
            WaitReason = waitReason;
            RunningStatus = runningStatus;
            QueueStatus = queueStatus;
        }

        // Null when the dispatch window is free.
        internal QueuedRequest? Running { get; }

        // Queued requests in start order.
        internal IReadOnlyList<QueuedRequest> Queued { get; }

        internal WaitReason WaitReason { get; }

        internal bool IsIdle => Running == null;

        internal RunningOperationStatus? RunningStatus { get; }

        internal IReadOnlyList<QueueEntryStatus> QueueStatus { get; }

        internal int QueueDepth => Queued.Count;
    }

    // The Worker_Scheduler: a fair, bounded per-Session queue in front of one Worker with a dispatch window of one
    // SDK_Operation. Pure and single-threaded: the host calls it under its own lock and supplies a monotonic clock.
    // It holds no threads, timers, or I/O.
    //
    // Fairness rule (Requirement 3 criterion 3): a ring of Sessions that hold at least one queued request, ordered by
    // the time each Session last went from empty to non-empty. A start takes the head Session's oldest request and
    // moves that Session to the tail if it still has requests. Removals (cancel, detach, expiry) keep both the ring
    // order and each Session's FIFO order and only drop Sessions left empty.
    //
    // Expiry (Requirement 3 criterion 4) is strict: a request expires when now - EnqueuedAtMs > QueueWaitMs. A start
    // (Complete or TryStartNext) never hands out an expired request even when ExpireDue has not run since the clock
    // moved: it moves the expired requests out of the queue, with their positions in the start order before that
    // call, and the next ExpireDue reports each of them exactly once ahead of any request it expires itself.
    internal sealed class SharedHostScheduler
    {
        private readonly SchedulerLimits _limits;
        private readonly Func<long> _monotonicMs;
        private readonly List<string> _ring = new List<string>();
        private readonly Dictionary<string, List<QueuedRequest>> _bySession = new Dictionary<string, List<QueuedRequest>>(StringComparer.Ordinal);

        // Expired requests a start skipped; reported by the next ExpireDue.
        private readonly List<ExpiredRequest> _skippedExpired = new List<ExpiredRequest>();

        private int _count;
        private QueuedRequest? _running;
        private long _runningStartedAtMs;
        private bool _workerAvailable = true;
        private WaitReason _reasonWhileUnavailable = WaitReason.StaBusy;

        internal SharedHostScheduler(SchedulerLimits limits, Func<long> monotonicMs)
        {
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            _monotonicMs = monotonicMs ?? throw new ArgumentNullException(nameof(monotonicMs));
        }

        // Earliest EnqueuedAtMs + QueueWaitMs over the requests ExpireDue would still have to report, null when none.
        // A skipped expired request keeps its (past) deadline here so the host's timer runs ExpireDue at once.
        internal long? NextDeadlineMs
        {
            get
            {
                long? earliest = null;
                foreach (var list in _bySession.Values)
                {
                    // Per-Session lists are in arrival order, so the first entry has the earliest enqueue time.
                    if (list.Count > 0 && (earliest == null || list[0].EnqueuedAtMs < earliest.Value)) earliest = list[0].EnqueuedAtMs;
                }
                foreach (var skipped in _skippedExpired)
                {
                    if (earliest == null || skipped.Request.EnqueuedAtMs < earliest.Value) earliest = skipped.Request.EnqueuedAtMs;
                }
                return earliest == null ? (long?)null : earliest.Value + _limits.QueueWaitMs;
            }
        }

        // SDK_Operations only. Admission order: queue-full, then session-queue-limit, then start immediately (window
        // free, Worker available, queue empty) or enqueue. A rejection changes nothing.
        internal SubmitResult Submit(QueuedRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (_count >= _limits.QueueDepth) return SubmitResult.Rejected(SubmitResult.QueueFull);
            if (CountFor(request.SessionId) >= _limits.SessionQueueCap) return SubmitResult.Rejected(SubmitResult.SessionQueueLimit);

            if (_running == null && _workerAvailable && _count == 0)
            {
                MarkRunning(request, _monotonicMs());
                return SubmitResult.StartedNow();
            }

            Enqueue(request);
            return SubmitResult.Enqueued();
        }

        // Removes the cancelling Session's queued requests whose ClientRequestIdJson matches, or whose CancelToken
        // matches a non-null cancelToken. Never touches the running request or another Session.
        internal CancelResult Cancel(string sessionId, string clientRequestIdJson, string? cancelToken)
        {
            var removed = new List<QueuedRequest>();
            if (sessionId == null) return new CancelResult(removed.AsReadOnly());

            if (_bySession.TryGetValue(sessionId, out var list))
            {
                for (var i = 0; i < list.Count;)
                {
                    if (Matches(list[i], clientRequestIdJson, cancelToken))
                    {
                        removed.Add(list[i]);
                        list.RemoveAt(i);
                        _count--;
                    }
                    else
                    {
                        i++;
                    }
                }
                if (list.Count == 0) DropSession(sessionId);
            }

            for (var i = 0; i < _skippedExpired.Count;)
            {
                var request = _skippedExpired[i].Request;
                if (string.Equals(request.SessionId, sessionId, StringComparison.Ordinal) && Matches(request, clientRequestIdJson, cancelToken))
                {
                    removed.Add(request);
                    _skippedExpired.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            return new CancelResult(removed.AsReadOnly());
        }

        // Drops every queued request of a detached Session without executing it. Its running request keeps running.
        internal IReadOnlyList<QueuedRequest> RemoveSession(string sessionId)
        {
            var removed = new List<QueuedRequest>();
            if (sessionId == null) return removed.AsReadOnly();

            if (_bySession.TryGetValue(sessionId, out var list))
            {
                removed.AddRange(list);
                _count -= list.Count;
                list.Clear();
                DropSession(sessionId);
            }

            for (var i = 0; i < _skippedExpired.Count;)
            {
                if (string.Equals(_skippedExpired[i].Request.SessionId, sessionId, StringComparison.Ordinal))
                {
                    removed.Add(_skippedExpired[i].Request);
                    _skippedExpired.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            return removed.AsReadOnly();
        }

        // Releases the dispatch window held by childRequestId and returns the next request to start, if any. With the
        // Worker unavailable (for example after a Worker exit) it releases the window, starts nothing, and returns
        // null; the queued requests wait for the replacement. An id that does not own the window changes nothing.
        internal QueuedRequest? Complete(string childRequestId)
        {
            if (_running == null || !string.Equals(_running.ChildRequestId, childRequestId, StringComparison.Ordinal)) return null;

            _running = null;
            _runningStartedAtMs = 0;
            return _workerAvailable ? StartNext() : null;
        }

        // Removes and returns the requests whose wait exceeds the queue wait: first those a start already skipped,
        // then the ones expired now, in start order, each with its 1-based position in the start order before the
        // removal (no renumbering within a batch).
        internal IReadOnlyList<ExpiredRequest> ExpireDue()
        {
            var expired = new List<ExpiredRequest>(_skippedExpired);
            _skippedExpired.Clear();
            RemoveExpired(_monotonicMs(), expired);
            return expired.AsReadOnly();
        }

        // Records whether the Worker can take an SDK_Operation. Starting is up to the host: TryStartNext after the
        // Worker becomes available.
        internal void SetWorkerAvailable(bool available, WaitReason reasonWhileUnavailable)
        {
            _workerAvailable = available;
            _reasonWhileUnavailable = reasonWhileUnavailable;
        }

        // Starts the next request when the window is free and the Worker is available; null otherwise.
        internal QueuedRequest? TryStartNext()
        {
            if (_running != null || !_workerAvailable) return null;
            return StartNext();
        }

        // Point-in-time copy; changes no state.
        internal SchedulerSnapshot Snapshot()
        {
            var now = _monotonicMs();
            var order = BuildStartOrder();
            var reason = CurrentWaitReason();
            var running = _running;

            RunningOperationStatus? runningStatus = running == null
                ? null
                : new RunningOperationStatus(running.Operation, running.SessionId, now - _runningStartedAtMs);

            var entryOperation = reason == WaitReason.StaBusy ? running?.Operation : null;
            var entries = new List<QueueEntryStatus>(order.Count);
            for (var i = 0; i < order.Count; i++)
            {
                var r = order[i];
                entries.Add(new QueueEntryStatus(i + 1, r.SessionId, now - r.EnqueuedAtMs, reason, entryOperation));
            }

            return new SchedulerSnapshot(
                running,
                new ReadOnlyCollection<QueuedRequest>(order),
                reason,
                runningStatus,
                new ReadOnlyCollection<QueueEntryStatus>(entries));
        }

        // ---- internals ----

        private WaitReason CurrentWaitReason() => _workerAvailable ? WaitReason.StaBusy : _reasonWhileUnavailable;

        private int CountFor(string sessionId) =>
            sessionId != null && _bySession.TryGetValue(sessionId, out var list) ? list.Count : 0;

        private static bool Matches(QueuedRequest request, string clientRequestIdJson, string? cancelToken) =>
            string.Equals(request.ClientRequestIdJson, clientRequestIdJson, StringComparison.Ordinal)
            || (cancelToken != null && string.Equals(request.CancelToken, cancelToken, StringComparison.Ordinal));

        private void Enqueue(QueuedRequest request)
        {
            if (!_bySession.TryGetValue(request.SessionId, out var list))
            {
                list = new List<QueuedRequest>();
                _bySession[request.SessionId] = list;
            }
            if (list.Count == 0) _ring.Add(request.SessionId);
            list.Add(request);
            _count++;
        }

        private void DropSession(string sessionId)
        {
            _bySession.Remove(sessionId);
            _ring.Remove(sessionId);
        }

        private void MarkRunning(QueuedRequest request, long now)
        {
            _running = request;
            _runningStartedAtMs = now;
        }

        // Skips (and sets aside for ExpireDue) every expired request, then starts the head Session's oldest request.
        private QueuedRequest? StartNext()
        {
            var now = _monotonicMs();
            RemoveExpired(now, _skippedExpired);
            if (_ring.Count == 0) return null;

            var session = _ring[0];
            _ring.RemoveAt(0);
            var list = _bySession[session];
            var next = list[0];
            list.RemoveAt(0);
            _count--;
            if (list.Count > 0) _ring.Add(session);
            else _bySession.Remove(session);

            MarkRunning(next, now);
            return next;
        }

        // Moves every queued request whose wait exceeds the queue wait at `now` into `sink`, in start order, with its
        // position in the start order as it stood before this call.
        private void RemoveExpired(long now, List<ExpiredRequest> sink)
        {
            if (_count == 0) return;

            var order = BuildStartOrder();
            for (var i = 0; i < order.Count; i++)
            {
                var request = order[i];
                if (now - request.EnqueuedAtMs <= _limits.QueueWaitMs) continue;

                var list = _bySession[request.SessionId];
                list.Remove(request);
                _count--;
                if (list.Count == 0) DropSession(request.SessionId);
                sink.Add(new ExpiredRequest(request, i + 1));
            }
        }

        // The order in which the queued requests would start if no other event happened: round-robin over the ring,
        // one request per Session per turn, each Session in FIFO order.
        private List<QueuedRequest> BuildStartOrder()
        {
            var order = new List<QueuedRequest>(_count);
            if (_count == 0) return order;

            var cursors = new int[_ring.Count];
            var lists = new List<QueuedRequest>[_ring.Count];
            for (var s = 0; s < _ring.Count; s++) lists[s] = _bySession[_ring[s]];

            var remaining = _count;
            while (remaining > 0)
            {
                for (var s = 0; s < lists.Length; s++)
                {
                    if (cursors[s] >= lists[s].Count) continue;
                    order.Add(lists[s][cursors[s]]);
                    cursors[s]++;
                    remaining--;
                }
            }
            return order;
        }
    }
}
