using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class BackgroundJobRegistry
    {
        private readonly int _retentionSeconds;
        private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _seenBySession = new();
        // v2.3.8 (Task 7.2): one CTS per running job. The async build/edit pollers
        // observe ct.IsCancellationRequested and terminate their loops; the worker
        // process may still finish its current SDK call (worker-side CT plumbing is
        // a follow-up — see CHANGELOG), but the gateway-side response is deterministic.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();

        // Test seam used to place cancellation deterministically at the
        // compare/assign boundary while reloading a persisted snapshot.
        internal Action? LoadMergeTransitionForTest { get; set; }

        // Lifecycle work is admitted before a worker command is sent.  The SDK STA is
        // intentionally single-flight, so keeping this small FIFO in the Gateway avoids
        // putting a second Build/Specify request behind the STA only to discover
        // BuildAlreadyRunning after minutes.  The queue is keyed by physical worker
        // scope (normally the normalized KB/worker alias), not by MCP session.
        private readonly object _lifecycleQueueLock = new object();
        private readonly Dictionary<string, LinkedList<string>> _lifecycleQueues =
            new Dictionary<string, LinkedList<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource<bool>> _lifecycleAdmissionSignals =
            new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);

        // Issue #27 item 2: a small rolling history of observed build wall-clock times,
        // keyed by kind ("lifecycle/build" | "lifecycle/rebuild"), so the async-build
        // path can report a realistic estimated_seconds instead of the flat 60/120.
        private readonly object _durationLock = new();
        private readonly Dictionary<string, List<int>> _buildDurations = new();
        private const int MaxDurationSamplesPerKind = 24;

        public BackgroundJobRegistry(int retentionSeconds = 600) => _retentionSeconds = retentionSeconds;

        /// <summary>
        /// Result of lifecycle admission.  IsNew is false when the normalized request
        /// joined an already queued/running operation; in that case Job is the canonical
        /// operation and must be polled/awaited instead of starting another worker call.
        /// </summary>
        public sealed class LifecycleAdmission
        {
            internal LifecycleAdmission(JobEntry job, bool isNew)
            {
                Job = job;
                IsNew = isNew;
            }

            public JobEntry Job { get; }
            public bool IsNew { get; }
            public bool Coalesced => !IsNew;
        }

        /// <summary>
        /// Admit a lifecycle operation into the per-worker FIFO.  Admission is a
        /// synchronous critical section and deliberately does not acquire a Worker or
        /// touch the STA.  The returned job is running when it owns the worker slot, or
        /// queued with a one-based position when another operation owns that slot.
        /// </summary>
        internal LifecycleAdmission AdmitLifecycle(
            string session,
            string kind,
            int estimatedSeconds,
            OwnershipFence ownership,
            string workerScope,
            string normalizedRequestKey,
            bool serialize = true)
        {
            ownership ??= new OwnershipFence(session ?? string.Empty, workerScope ?? string.Empty, 0);
            string scope = NormalizeQueueScope(workerScope, ownership);
            string key = normalizedRequestKey ?? string.Empty;

            lock (_lifecycleQueueLock)
            {
                if (serialize)
                {
                    // Coalescing is intentionally done before the STA/build request is
                    // created.  Compare normalized action+targets, not raw JSON, so
                    // harmless argument ordering/whitespace does not execute twice.
                    foreach (var candidate in _jobs.Values)
                    {
                        if (!IsLiveJob(candidate)) continue;
                        if (!string.Equals(candidate.QueueScope, scope, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.Equals(candidate.QueueKey, key, StringComparison.Ordinal)) continue;
                        RefreshQueuePositionLocked(candidate);
                        return new LifecycleAdmission(candidate, isNew: false);
                    }
                }

                bool workerBusy = serialize && HasLiveOperationInScopeLocked(scope);
                var now = DateTime.UtcNow;
                var job = new JobEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Session = session ?? string.Empty,
                    Kind = kind ?? "lifecycle",
                    Status = workerBusy ? "queued" : "running",
                    StartedAt = now,
                    LastUpdatedAt = now,
                    EstimatedSeconds = Math.Max(0, estimatedSeconds),
                    OwnerScopeId = ownership.OwnerScopeId,
                    KbId = ownership.KbId,
                    Generation = ownership.Generation,
                    Epoch = ownership.Epoch,
                    QueueScope = scope,
                    QueueKey = key,
                    QueuedAtUtc = now,
                    ExecutionStartedAtUtc = workerBusy ? (DateTime?)null : now,
                    QueuePosition = workerBusy ? 1 : 0,
                    QueuedMs = 0
                };
                _jobs[job.Id] = job;

                if (workerBusy)
                {
                    if (!_lifecycleQueues.TryGetValue(scope, out var queue))
                    {
                        queue = new LinkedList<string>();
                        _lifecycleQueues[scope] = queue;
                    }
                    queue.AddLast(job.Id);
                    NormalizeQueuePositionsLocked(scope, queue);
                }

                return new LifecycleAdmission(job, isNew: true);
            }
        }

        /// <summary>
        /// Wait until a queued lifecycle job owns its worker slot.  Cancellation of the
        /// waiting caller does not implicitly cancel the shared operation; callers that
        /// want cancellation should invoke Cancel(jobId) explicitly.
        /// </summary>
        internal async Task<bool> WaitForLifecycleAdmissionAsync(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return false;
            TaskCompletionSource<bool>? signal = null;
            lock (_lifecycleQueueLock)
            {
                if (!_jobs.TryGetValue(jobId, out var job) || !IsLiveJob(job))
                    return false;
                if (!string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!_lifecycleAdmissionSignals.TryGetValue(jobId, out signal))
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _lifecycleAdmissionSignals[jobId] = signal;
                }
            }

            if (signal == null) return true;
            try
            {
                bool admitted = await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!admitted) return false;
                if (!_jobs.TryGetValue(jobId, out var admittedJob)) return false;
                lock (admittedJob.SyncRoot)
                {
                    return IsLiveJob(admittedJob)
                        && string.Equals(admittedJob.Status, "running", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Resolve a job by its public Gateway id or by the legacy Worker task id that
        /// was captured when the worker acknowledged the command.
        /// </summary>
        public bool TryResolveLifecycleAlias(string? alias, out JobEntry? job)
        {
            job = null;
            if (string.IsNullOrWhiteSpace(alias)) return false;
            string normalized = alias.Trim();
            if (normalized.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(3);
            if (_jobs.TryGetValue(normalized, out var direct)
                || _jobs.TryGetValue(normalized.ToLowerInvariant(), out direct))
            {
                job = direct;
                return true;
            }
            foreach (var candidate in _jobs.Values)
            {
                if (candidate == null) continue;
                if (string.Equals(candidate.WorkerTaskId, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    job = candidate;
                    return true;
                }
            }
            return false;
        }

        public JobEntry? ResolveLifecycleAlias(string? alias)
            => TryResolveLifecycleAlias(alias, out var job) ? job : null;

        /// <summary>Return the current queue position/metadata for a lifecycle job.</summary>
        public JObject GetLifecycleQueueMetadata(string jobId)
        {
            lock (_lifecycleQueueLock)
            {
                if (!_jobs.TryGetValue(jobId, out var job))
                    return new JObject { ["status"] = "NotFound", ["operationId"] = jobId };
                RefreshQueuePositionLocked(job);
                long queuedMs = job.QueuedMs;
                if (string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase) && job.QueuedAtUtc.HasValue)
                    queuedMs = Math.Max(0L, (long)(DateTime.UtcNow - job.QueuedAtUtc.Value).TotalMilliseconds);
                return new JObject
                {
                    ["status"] = job.Status,
                    ["operationId"] = job.Id,
                    ["queuePosition"] = job.QueuePosition > 0 ? (JToken)job.QueuePosition : JValue.CreateNull(),
                    ["queuedMs"] = queuedMs,
                    ["queueScope"] = job.QueueScope,
                    ["action"] = job.QueueAction,
                    ["target"] = job.QueueTarget,
                    ["enqueuedAt"] = job.QueuedAtUtc?.ToUniversalTime().ToString("o")
                };
            }
        }

        /// <summary>
        /// Queue projection used by whoami/doctor.  Other sessions are intentionally
        /// redacted rather than exposing their target names.
        /// </summary>
        public JArray BuildLifecycleQueueSnapshot(string? viewerSession = null)
        {
            var result = new JArray();
            lock (_lifecycleQueueLock)
            {
                foreach (var scopePair in _lifecycleQueues)
                {
                    var queue = scopePair.Value;
                    foreach (string queuedId in queue)
                    {
                        if (!_jobs.TryGetValue(queuedId, out var job) || !IsLiveJob(job)) continue;
                        RefreshQueuePositionLocked(job);
                        bool own = string.IsNullOrWhiteSpace(viewerSession)
                            || string.Equals(job.Session, viewerSession, StringComparison.Ordinal);
                        var item = new JObject
                        {
                            ["operationId"] = job.Id,
                            ["action"] = job.QueueAction,
                            ["targets"] = job.QueueTarget,
                            ["sessionHint"] = own ? job.Session : "other-attachment",
                            ["owner"] = own ? "self" : "other-attachment",
                            ["enqueuedAt"] = job.QueuedAtUtc?.ToUniversalTime().ToString("o"),
                            ["queuePosition"] = job.QueuePosition,
                            ["queuedMs"] = job.QueuedAtUtc.HasValue
                                ? Math.Max(0L, (long)(DateTime.UtcNow - job.QueuedAtUtc.Value).TotalMilliseconds)
                                : 0L,
                            ["queueScope"] = job.QueueScope
                        };
                        if (!own)
                        {
                            item.Remove("targets");
                            item["target"] = JValue.CreateNull();
                        }
                        result.Add(item);
                    }
                }
            }
            return result.OrderBy(item => item["queueScope"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item["queuePosition"]?.ToObject<int>() ?? int.MaxValue)
                .ToList()
                .Aggregate(new JArray(), (array, item) => { array.Add(item); return array; });
        }

        internal void ResetLifecycleQueueForTest()
        {
            List<JobEntry> queuedJobs;
            lock (_lifecycleQueueLock)
            {
                queuedJobs = _lifecycleQueues.Values
                    .SelectMany(queue => queue)
                    .Select(id => _jobs.TryGetValue(id, out var job) ? job : null)
                    .Where(job => job != null)
                    .Cast<JobEntry>()
                    .Distinct()
                    .ToList();
                foreach (var signal in _lifecycleAdmissionSignals.Values)
                {
                    try { signal.TrySetResult(false); } catch { }
                }
                _lifecycleAdmissionSignals.Clear();
                _lifecycleQueues.Clear();
            }

            foreach (var job in queuedJobs)
            {
                lock (job.SyncRoot)
                {
                    if (!string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase)) continue;
                    job.Status = "cancelled";
                    job.CompletedAt = DateTime.UtcNow;
                    job.LastUpdatedAt = job.CompletedAt.Value;
                    job.Summary = "Lifecycle queue reset";
                }
                DisposeCts(job.Id);
            }
        }

        private static string NormalizeQueueScope(string? workerScope, OwnershipFence ownership)
        {
            if (!string.IsNullOrWhiteSpace(workerScope)) return workerScope.Trim();
            if (!string.IsNullOrWhiteSpace(ownership.KbId)) return ownership.KbId.Trim();
            return "__default-worker__";
        }

        private static bool IsLiveJob(JobEntry? job)
            => job != null && !string.Equals(job.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, "stalled", StringComparison.OrdinalIgnoreCase);

        private static DateTime JobStateTimestamp(JobEntry job)
        {
            if (job == null) return DateTime.MinValue;
            if (job.LastUpdatedAt.HasValue) return job.LastUpdatedAt.Value;
            if (job.CompletedAt.HasValue) return job.CompletedAt.Value;
            return job.StartedAt;
        }

        private bool HasLiveOperationInScopeLocked(string scope)
        {
            return _jobs.Values.Any(job => IsLiveJob(job)
                && string.Equals(job.QueueScope, scope, StringComparison.OrdinalIgnoreCase));
        }

        private void RefreshQueuePositionLocked(JobEntry job)
        {
            if (!string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                job.QueuePosition = 0;
                return;
            }
            if (string.IsNullOrWhiteSpace(job.QueueScope)
                || !_lifecycleQueues.TryGetValue(job.QueueScope, out var queue))
            {
                job.QueuePosition = 0;
                return;
            }
            int position = 1;
            foreach (var id in queue)
            {
                if (!_jobs.TryGetValue(id, out var queued) || !IsLiveJob(queued)) continue;
                if (string.Equals(id, job.Id, StringComparison.OrdinalIgnoreCase))
                {
                    job.QueuePosition = position;
                    return;
                }
                position++;
            }
            job.QueuePosition = 0;
        }

        private void NormalizeQueuePositionsLocked(string scope, LinkedList<string> queue)
        {
            int position = 1;
            var stale = new LinkedList<string>();
            foreach (var id in queue.ToList())
            {
                if (_jobs.TryGetValue(id, out var job) && IsLiveJob(job)
                    && string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
                {
                    job.QueuePosition = position++;
                }
                else
                {
                    stale.AddLast(id);
                }
            }
            foreach (var id in stale) queue.Remove(id);
            if (queue.Count == 0) _lifecycleQueues.Remove(scope);
        }

        private void PromoteNextLifecycleJobLocked(string scope)
        {
            if (!_lifecycleQueues.TryGetValue(scope, out var queue)) return;
            while (queue.First != null)
            {
                string id = queue.First.Value;
                queue.RemoveFirst();
                if (!_jobs.TryGetValue(id, out var next) || !IsLiveJob(next)
                    || !string.Equals(next.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    continue;
                var now = DateTime.UtcNow;
                next.Status = "running";
                next.QueuePosition = 0;
                next.ExecutionStartedAtUtc = now;
                next.LastUpdatedAt = now;
                next.QueuedMs = next.QueuedAtUtc.HasValue
                    ? Math.Max(0L, (long)(now - next.QueuedAtUtc.Value).TotalMilliseconds)
                    : 0;
                NormalizeQueuePositionsLocked(scope, queue);
                if (_lifecycleAdmissionSignals.TryGetValue(id, out var signal))
                {
                    _lifecycleAdmissionSignals.Remove(id);
                    try { signal.TrySetResult(true); } catch { }
                }
                return;
            }
            _lifecycleQueues.Remove(scope);
        }

        private void RemoveQueuedLifecycleJobLocked(JobEntry job)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.QueueScope)) return;
            if (_lifecycleQueues.TryGetValue(job.QueueScope, out var queue))
            {
                queue.Remove(job.Id);
                NormalizeQueuePositionsLocked(job.QueueScope, queue);
            }
            if (_lifecycleAdmissionSignals.TryGetValue(job.Id, out var signal))
            {
                _lifecycleAdmissionSignals.Remove(job.Id);
                try { signal.TrySetResult(false); } catch { }
            }
        }

        private void ReleaseLifecycleSlotAfterTerminal(JobEntry job, bool wasRunning)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.QueueScope)) return;
            lock (_lifecycleQueueLock)
            {
                if (wasRunning)
                    PromoteNextLifecycleJobLocked(job.QueueScope);
                else
                    RemoveQueuedLifecycleJobLocked(job);
            }
        }

        // Record a completed build's wall-clock seconds for future estimation.
        public void RecordBuildDuration(string kind, int seconds)
        {
            if (string.IsNullOrEmpty(kind) || seconds <= 0) return;
            lock (_durationLock)
            {
                if (!_buildDurations.TryGetValue(kind, out var list))
                    _buildDurations[kind] = list = new List<int>();
                list.Add(seconds);
                if (list.Count > MaxDurationSamplesPerKind)
                    list.RemoveAt(0);
            }
        }

        // Median of recent samples for a kind, or null when there's no history yet.
        // Median (not mean) so a single slow outlier doesn't skew the estimate.
        public int? EstimateBuildSeconds(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;
            lock (_durationLock)
            {
                if (!_buildDurations.TryGetValue(kind, out var list) || list.Count == 0)
                    return null;
                var sorted = list.OrderBy(x => x).ToList();
                int mid = sorted.Count / 2;
                int median = (sorted.Count % 2 == 1)
                    ? sorted[mid]
                    : (sorted[mid - 1] + sorted[mid] + 1) / 2;
                // Clamp so a wild sample can't produce an absurd metadata value.
                return Math.Max(5, Math.Min(median, 1800));
            }
        }

        public JobEntry Start(string session, string kind, int estimatedSeconds)
            => Start(session, kind, estimatedSeconds, new OwnershipFence(session, string.Empty, 0));

        internal JobEntry Start(string session, string kind, int estimatedSeconds, OwnershipFence ownership)
        {
            var job = new JobEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Session = session,
                Kind = kind,
                Status = "running",
                StartedAt = DateTime.UtcNow,
                EstimatedSeconds = estimatedSeconds,
                OwnerScopeId = ownership.OwnerScopeId,
                KbId = ownership.KbId,
                Generation = ownership.Generation,
                Epoch = ownership.Epoch
            };
            job.LastUpdatedAt = job.StartedAt;
            _jobs[job.Id] = job;
            return job;
        }

        public void Complete(string jobId, bool success, string? summary, JObject? result = null)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return;
            bool shouldRecordDuration;
            bool wasRunning;
            DateTime startedAt;
            DateTime completedAt;
            lock (job.SyncRoot)
            {
                // Only a live job can transition to a terminal state. Cancelled and
                // stalled are already terminal — a late worker response (or a second
                // poller/reconcile) must never resurrect them. A queued job may also
                // be completed defensively, but it never owns the worker slot.
                if (!IsLiveJob(job)) return;
                wasRunning = string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase);
                job.Status = success ? "succeeded" : "failed";
                job.CompletedAt = DateTime.UtcNow;
                job.LastUpdatedAt = job.CompletedAt.Value;
                if (job.Summary == null) job.Summary = summary;
                if (job.Result == null) job.Result = result;
                // Issue #27 item 2: feed the estimator with the observed wall-clock of a
                // successful build so the next build's estimated_seconds is realistic.
                shouldRecordDuration = success && job.Kind != null
                    && job.Kind.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0;
                startedAt = job.StartedAt;
                completedAt = job.CompletedAt.Value;
            }
            ReleaseLifecycleSlotAfterTerminal(job, wasRunning);
            if (shouldRecordDuration)
            {
                int elapsed = (int)Math.Round((completedAt - startedAt).TotalSeconds);
                RecordBuildDuration(job.Kind!, elapsed);
            }
            DisposeCts(jobId);
        }

        // Issue #79: terminal "stalled" state for an async job whose SDK call exceeded
        // its time bound without returning (typically an IDE modal dialog holding the
        // model, or the SDK retrying a failing validation internally). Distinct from
        // "failed" so agents get an explicit, actionable signal ("the worker never
        // answered — recover with the sync path") instead of a generic failure. Like
        // cancelled, stalled is terminal: Complete()/Cancel() can't resurrect it.
        public void Stall(string jobId, string? summary, JObject? result = null)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return;
            bool wasRunning;
            lock (job.SyncRoot)
            {
                if (!IsLiveJob(job)) return;
                wasRunning = string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase);
                job.Status = "stalled";
                job.CompletedAt = DateTime.UtcNow;
                job.LastUpdatedAt = job.CompletedAt.Value;
                if (job.Summary == null) job.Summary = summary;
                if (job.Result == null) job.Result = result;
            }
            ReleaseLifecycleSlotAfterTerminal(job, wasRunning);
            DisposeCts(jobId);
        }

        // v2.3.8 (Task 7.2): cancel a running job. Signals the CTS (if any pollers
        // registered one) and flips status to "cancelled" so subsequent
        // SnapshotForSession / LongPollJob calls return a terminal envelope.
        public CancellationToken RegisterCancellation(string jobId)
        {
            var cts = _cts.GetOrAdd(jobId, _ => new CancellationTokenSource());
            return cts.Token;
        }

        public bool Cancel(string jobId, string? reason = null)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return false;
            // The status check and terminal transition must be one critical section.
            // Otherwise Complete() can win the lock after this check and Cancel()
            // would overwrite a succeeded/failed result.
            bool wasRunning;
            lock (job.SyncRoot)
            {
                // A terminal job (succeeded/failed/stalled) is done — cancelling it
                // would only rewrite history. Queued jobs are cancellable too; they
                // have not sent a worker command yet.
                if (!IsLiveJob(job)) return false;
                wasRunning = string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase);
                job.Status = "cancelled";
                job.CompletedAt = DateTime.UtcNow;
                job.LastUpdatedAt = job.CompletedAt.Value;
                job.Summary = reason ?? "Cancelled by client";
            }

            ReleaseLifecycleSlotAfterTerminal(job, wasRunning);
            if (_cts.TryGetValue(jobId, out var cts))
            {
                try { cts.Cancel(); } catch { /* already disposed */ }
            }
            DisposeCts(jobId);
            return true;
        }

        private void DisposeCts(string jobId)
        {
            if (_cts.TryRemove(jobId, out var cts))
            {
                try { cts.Dispose(); } catch { }
            }
        }

        public JobEntry? Get(string jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

        public void SetWorkerTaskId(string jobId, string? workerTaskId)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return;
            if (_jobs.TryGetValue(jobId, out var job))
            {
                lock (job.SyncRoot)
                {
                    job.WorkerTaskId = string.IsNullOrWhiteSpace(workerTaskId) ? null : workerTaskId.Trim();
                    job.LastUpdatedAt = DateTime.UtcNow;
                }
            }
        }

        internal bool BelongsTo(JobEntry job, OwnershipFence ownership)
            => job != null && ownership != null
                && string.Equals(job.OwnerScopeId, ownership.OwnerScopeId, StringComparison.Ordinal)
                && string.Equals(job.KbId, ownership.KbId, StringComparison.Ordinal)
                && job.Generation == ownership.Generation
                && job.Epoch == ownership.Epoch;

        public IReadOnlyList<JobEntry> SnapshotForSession(string session)
        {
            var seen = _seenBySession.GetOrAdd(session, _ => new HashSet<string>());
            lock (seen)
            {
                return _jobs.Values
                    .Where(j => j.Session == session)
                    .Where(j => IsLiveJob(j) || !seen.Contains(j.Id))
                    .ToList();
            }
        }

        public void MarkSeen(string session, IEnumerable<string> jobIds)
        {
            var seen = _seenBySession.GetOrAdd(session, _ => new HashSet<string>());
            lock (seen)
            {
                foreach (var id in jobIds)
                {
                    if (_jobs.TryGetValue(id, out var j) && j.Status != "running")
                        seen.Add(id);
                }
            }
        }

        public void SweepExpired()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_retentionSeconds);
            foreach (var kvp in _jobs)
            {
                if (kvp.Value.CompletedAt != null && kvp.Value.CompletedAt < cutoff)
                {
                    if (_jobs.TryRemove(kvp.Key, out var removed))
                        ReleaseLifecycleSlotAfterTerminal(removed, wasRunning: false);
                }
            }
        }

        // Plan 036: _seenBySession accumulates one id per completed job forever with no
        // eviction path. Once SweepExpired() has removed a job from _jobs, its id is
        // useless in every session's seen-set — drop it there too so long-lived gateways
        // don't grow _seenBySession unbounded.
        public void PruneSeenBySession()
        {
            foreach (var kvp in _seenBySession)
            {
                var seen = kvp.Value;
                lock (seen)
                {
                    seen.RemoveWhere(id => !_jobs.ContainsKey(id));
                }
            }
        }

        // Plan 036: test-only visibility into the seen-set (InternalsVisibleTo covers
        // GxMcp.Gateway.Tests) so PruneSeenBySession's effect can be asserted directly
        // instead of indirectly through SnapshotForSession, which already filters
        // swept jobs out via the Session/_jobs lookup regardless of seen-set state.
        internal bool IsSeenForTest(string session, string jobId)
        {
            if (!_seenBySession.TryGetValue(session, out var seen)) return false;
            lock (seen) { return seen.Contains(jobId); }
        }

        public int Count => _jobs.Count;

        // FR#20 (v2.6.6 Stream B): persist JobEntry list across worker soft-reloads.
        // We intentionally snapshot only the value side — _seenBySession is a UI-state
        // concern bound to a session lifetime, not a job, so it's recomputed lazily.
        // Per-job CancellationTokenSources are NOT serialized (they reference live
        // pollers that wouldn't survive a restart anyway).
        public void SaveTo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required", nameof(path));
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var list = _jobs.Values.ToList();
                string json = JsonConvert.SerializeObject(list, Formatting.Indented);
                // Atomic-ish write: dump to .tmp then move so a crash mid-write never
                // leaves a corrupted jobs.json that the next worker would refuse to parse.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                // Caller (gateway shutdown path) logs; rethrow so soft-reload metrics see it.
                throw new IOException("Failed to persist BackgroundJobRegistry to " + path + ": " + ex.Message, ex);
            }
        }

        private static bool ShouldReplaceLoadedJob(JobEntry existing, JobEntry incoming)
        {
            bool existingTerminal = !IsLiveJob(existing);
            bool incomingTerminal = !IsLiveJob(incoming);
            DateTime existingAt = JobStateTimestamp(existing);
            DateTime incomingAt = JobStateTimestamp(incoming);

            // A worker can persist a snapshot just before it finishes. Never
            // let that stale running/queued record resurrect a terminal Gateway
            // job, and never replace a newer in-memory transition with an older
            // snapshot.
            return !((existingTerminal && (!incomingTerminal || incomingAt <= existingAt))
                || (!existingTerminal && incomingAt < existingAt));
        }

        private static void ApplyLoadedJobState(JobEntry target, JobEntry source)
        {
            // Preserve the existing JobEntry instance: callers (and terminal
            // transitions) may already hold it and synchronize through its
            // SyncRoot. Replacing the dictionary value would strand that
            // reference even if the replacement happened under the lock.
            target.Session = source.Session;
            target.Kind = source.Kind;
            target.Status = source.Status;
            target.StartedAt = source.StartedAt;
            target.CompletedAt = source.CompletedAt;
            target.LastUpdatedAt = source.LastUpdatedAt;
            target.EstimatedSeconds = source.EstimatedSeconds;
            target.Summary = source.Summary;
            target.Result = source.Result;
            target.WorkerAlias = source.WorkerAlias;
            target.Target = source.Target;
            target.Part = source.Part;
            target.ObjectType = source.ObjectType;
            target.TargetGuid = source.TargetGuid;
            target.TargetEntityKey = source.TargetEntityKey;
            target.TargetPath = source.TargetPath;
            target.ExpectedVersion = source.ExpectedVersion;
            target.WorkerTaskId = source.WorkerTaskId;
            target.QueueScope = source.QueueScope;
            target.QueueKey = source.QueueKey;
            target.QueueAction = source.QueueAction;
            target.QueueTarget = source.QueueTarget;
            target.QueuedAtUtc = source.QueuedAtUtc;
            target.ExecutionStartedAtUtc = source.ExecutionStartedAtUtc;
            target.QueuedMs = source.QueuedMs;
            target.QueuePosition = source.QueuePosition;
            target.OwnerScopeId = source.OwnerScopeId;
            target.KbId = source.KbId;
            target.Generation = source.Generation;
            target.Epoch = source.Epoch;
        }

        private bool TryMergeLoadedJob(JobEntry incoming)
        {
            if (_jobs.TryGetValue(incoming.Id, out var existing))
            {
                // Keep the comparison and state publication under the same
                // per-job lock used by Cancel/Complete. A terminal transition
                // either wins before this lock (and rejects the snapshot) or
                // waits until the snapshot is published and then transitions
                // the same JobEntry instance; it can never be lost in the gap.
                bool terminalApplied = false;
                bool wasRunning = false;
                lock (existing.SyncRoot)
                {
                    if (!ShouldReplaceLoadedJob(existing, incoming)) return false;
                    LoadMergeTransitionForTest?.Invoke();
                    if (!ShouldReplaceLoadedJob(existing, incoming)) return false;
                    wasRunning = string.Equals(existing.Status, "running", StringComparison.OrdinalIgnoreCase);
                    terminalApplied = !IsLiveJob(incoming);
                    ApplyLoadedJobState(existing, incoming);
                }
                if (terminalApplied)
                {
                    ReleaseLifecycleSlotAfterTerminal(existing, wasRunning);
                    DisposeCts(existing.Id);
                }
                return true;
            }

            LoadMergeTransitionForTest?.Invoke();
            _jobs[incoming.Id] = incoming;
            return true;
        }

        public int LoadFrom(string path, bool deleteAfterRead = true)
        {
            if (!File.Exists(path)) return 0;
            int loaded = 0;
            try
            {
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var list = JsonConvert.DeserializeObject<List<JobEntry>>(json) ?? new List<JobEntry>();
                foreach (var j in list)
                {
                    if (string.IsNullOrWhiteSpace(j?.Id)) continue;
                    if (string.IsNullOrWhiteSpace(j.QueueScope))
                        j.QueueScope = string.IsNullOrWhiteSpace(j.KbId) ? "__default-worker__" : j.KbId;
                    if (!TryMergeLoadedJob(j)) continue;
                    loaded++;
                }
                // Queue metadata is derived runtime state, but the persisted JobEntry
                // contains the physical scope/key. Rebuild the FIFO indexes so a
                // soft reload cannot admit a second lifecycle request into a slot
                // that still has a persisted owner.
                lock (_lifecycleQueueLock)
                {
                    _lifecycleQueues.Clear();
                    // Admission signals are keyed by job id, not queue scope. Keep
                    // a waiter attached when the reloaded job is still live; only
                    // resolve signals whose jobs are absent or terminal.
                    foreach (var jobId in _lifecycleAdmissionSignals.Keys.ToList())
                    {
                        if (_jobs.TryGetValue(jobId, out var liveJob) && IsLiveJob(liveJob))
                            continue;
                        if (_lifecycleAdmissionSignals.TryGetValue(jobId, out var signal))
                        {
                            try { signal.TrySetResult(false); } catch { }
                            _lifecycleAdmissionSignals.Remove(jobId);
                        }
                    }
                    foreach (var j in _jobs.Values
                        .Where(job => IsLiveJob(job)
                            && string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(job => job.QueuedAtUtc ?? job.StartedAt))
                    {
                        if (!_lifecycleQueues.TryGetValue(j.QueueScope!, out var queue))
                        {
                            queue = new LinkedList<string>();
                            _lifecycleQueues[j.QueueScope!] = queue;
                        }
                        queue.AddLast(j.Id);
                    }
                    foreach (var scope in _lifecycleQueues.Keys.ToList())
                        NormalizeQueuePositionsLocked(scope, _lifecycleQueues[scope]);
                }
                if (deleteAfterRead)
                {
                    try { File.Delete(path); }
                    catch (Exception delEx)
                    {
                        // Non-fatal: leaving the file means a subsequent restart re-loads
                        // (idempotent — same IDs overwrite the same entries).
                        System.Diagnostics.Debug.WriteLine("[BackgroundJobRegistry] delete after load failed: " + delEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new IOException("Failed to rehydrate BackgroundJobRegistry from " + path + ": " + ex.Message, ex);
            }
            return loaded;
        }
    }

    public sealed class JobEntry
    {
        public string Id { get; set; } = "";
        public string Session { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "running";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        // Updated whenever a task changes state. Persisting this separately from
        // StartedAt lets the MCP tasks extension expose a monotonic freshness marker
        // without deriving it from a nullable terminal timestamp.
        public DateTime? LastUpdatedAt { get; set; }
        public int EstimatedSeconds { get; set; }
        public string? Summary { get; set; }
        public JObject? Result { get; set; }

        // Async mutations keep the physical worker and authored target identity so a
        // lifecycle cancel can recycle a non-preemptible STA call without guessing which
        // KB owns it, and can require a read-back of the exact part before the next write.
        public string? WorkerAlias { get; set; }
        public string? Target { get; set; }
        public string? Part { get; set; }
        public string? ObjectType { get; set; }
        public string? TargetGuid { get; set; }
        public string? TargetEntityKey { get; set; }
        public string? TargetPath { get; set; }
        public string? ExpectedVersion { get; set; }

        // Issue #27 item 1: the worker-side build task id (BuildTaskStatus key) this
        // job maps to. The async build poller (Program.cs) is fire-and-forget and can
        // wedge — stale worker pipe, STA serialization, worker recycle — leaving the job
        // stuck "running" forever even though the worker's build task already terminated.
        // Storing the worker task id lets any subsequent action=status / action=result
        // poll actively re-query the worker and reconcile the job to its real terminal
        // state instead of trusting only the background poller. See ReconcileJobWithWorkerAsync.
        public string? WorkerTaskId { get; set; }

        // Lifecycle FIFO metadata. QueueScope is the physical worker/KB scope;
        // QueueKey is the normalized action+request identity used for coalescing.
        public string? QueueScope { get; set; }
        public string? QueueKey { get; set; }
        public string? QueueAction { get; set; }
        public string? QueueTarget { get; set; }
        public DateTime? QueuedAtUtc { get; set; }
        public DateTime? ExecutionStartedAtUtc { get; set; }
        public long QueuedMs { get; set; }
        public int QueuePosition { get; set; }

        public string OwnerScopeId { get; set; } = string.Empty;
        public string KbId { get; set; } = string.Empty;
        public long Generation { get; set; }
        public long Epoch { get; set; }

        // Plan 026: guards read-modify-write of Status/CompletedAt/Summary/Result so
        // Complete() and Cancel() can't race and clobber a terminal "cancelled" status.
        [Newtonsoft.Json.JsonIgnore]
        public readonly object SyncRoot = new object();
    }
}
