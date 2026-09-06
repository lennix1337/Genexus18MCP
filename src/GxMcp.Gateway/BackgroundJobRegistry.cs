using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

        // Issue #27 item 2: a small rolling history of observed build wall-clock times,
        // keyed by kind ("lifecycle/build" | "lifecycle/rebuild"), so the async-build
        // path can report a realistic estimated_seconds instead of the flat 60/120.
        private readonly object _durationLock = new();
        private readonly Dictionary<string, List<int>> _buildDurations = new();
        private const int MaxDurationSamplesPerKind = 24;

        public BackgroundJobRegistry(int retentionSeconds = 600) => _retentionSeconds = retentionSeconds;

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
        {
            var job = new JobEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Session = session,
                Kind = kind,
                Status = "running",
                StartedAt = DateTime.UtcNow,
                EstimatedSeconds = estimatedSeconds
            };
            job.LastUpdatedAt = job.StartedAt;
            _jobs[job.Id] = job;
            return job;
        }

        public void Complete(string jobId, bool success, string? summary, JObject? result = null)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return;
            bool shouldRecordDuration;
            DateTime startedAt;
            DateTime completedAt;
            lock (job.SyncRoot)
            {
                // Only a running job can transition to a terminal state. Cancelled and
                // stalled are already terminal — a late worker response (or a second
                // poller/reconcile) must never resurrect them. This also fixes the
                // latent double-complete clobber where a reconcile could overwrite the
                // poller's terminal verdict.
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) return;
                job.Status = success ? "succeeded" : "failed";
                job.CompletedAt = DateTime.UtcNow;
                job.LastUpdatedAt = job.CompletedAt;
                if (job.Summary == null) job.Summary = summary;
                if (job.Result == null) job.Result = result;
                // Issue #27 item 2: feed the estimator with the observed wall-clock of a
                // successful build so the next build's estimated_seconds is realistic.
                shouldRecordDuration = success && job.Kind != null
                    && job.Kind.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0;
                startedAt = job.StartedAt;
                completedAt = job.CompletedAt.Value;
            }
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
            lock (job.SyncRoot)
            {
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) return;
                job.Status = "stalled";
                job.CompletedAt = DateTime.UtcNow;
                job.LastUpdatedAt = job.CompletedAt;
                if (job.Summary == null) job.Summary = summary;
                if (job.Result == null) job.Result = result;
            }
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
            lock (job.SyncRoot)
            {
                // A terminal job (succeeded/failed/stalled) is done — cancelling it
                // would only rewrite history. Only running jobs can be cancelled.
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) return false;
                job.Status = "cancelled";
                job.CompletedAt = DateTime.UtcNow;
                job.LastUpdatedAt = job.CompletedAt;
                job.Summary = reason ?? "Cancelled by client";
            }

            if (_cts.TryGetValue(jobId, out var cts))
            {
                try { cts.Cancel(); } catch { /* already disposed */ }
            }
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

        public IReadOnlyList<JobEntry> SnapshotForSession(string session)
        {
            var seen = _seenBySession.GetOrAdd(session, _ => new HashSet<string>());
            lock (seen)
            {
                return _jobs.Values
                    .Where(j => j.Session == session)
                    .Where(j => j.Status == "running" || !seen.Contains(j.Id))
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
                    _jobs.TryRemove(kvp.Key, out _);
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
                    _jobs[j.Id] = j;
                    loaded++;
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

        // Issue #27 item 1: the worker-side build task id (BuildTaskStatus key) this
        // job maps to. The async build poller (Program.cs) is fire-and-forget and can
        // wedge — stale worker pipe, STA serialization, worker recycle — leaving the job
        // stuck "running" forever even though the worker's build task already terminated.
        // Storing the worker task id lets any subsequent action=status / action=result
        // poll actively re-query the worker and reconcile the job to its real terminal
        // state instead of trusting only the background poller. See ReconcileJobWithWorkerAsync.
        public string? WorkerTaskId { get; set; }

        // Plan 026: guards read-modify-write of Status/CompletedAt/Summary/Result so
        // Complete() and Cancel() can't race and clobber a terminal "cancelled" status.
        [Newtonsoft.Json.JsonIgnore]
        public readonly object SyncRoot = new object();
    }
}
