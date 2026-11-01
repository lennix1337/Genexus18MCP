using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed record KbPoolStatus(KbHandle Handle, int? Pid, long? WorkingSetBytes, DateTime LastActivityUtc);

    public sealed class WorkerPoolFullException : Exception
    {
        public IReadOnlyList<KbHandle> OpenKbs { get; }
        public WorkerPoolFullException(IReadOnlyList<KbHandle> openKbs)
            : base($"WorkerPool full ({openKbs.Count} KBs open). Close one with genexus_kb action=close before opening another.")
        {
            OpenKbs = openKbs;
        }
    }

    /// <summary>
    /// Issue #369: the Gateway's per-Worker command channel stayed at capacity longer than
    /// <see cref="WorkerProcess.CommandQueueEnqueueTimeout"/>, so the command was refused
    /// rather than buffered without bound. The command was never enqueued, so a retry is
    /// safe and the caller is told so explicitly.
    /// </summary>
    public sealed class WorkerCommandQueueFullException : Exception
    {
        public string? KbAlias { get; }
        public string? CommandId { get; }
        public string? Method { get; }
        public int Capacity { get; }
        public long WaitedMs { get; }

        public WorkerCommandQueueFullException(
            string? kbAlias, string? commandId, string? method, int capacity, double waitedMs)
            : base($"The Gateway command queue for KB '{kbAlias}' stayed full for {waitedMs:F0} ms " +
                   $"(capacity {capacity} commands). This command was NOT sent to the Worker.")
        {
            KbAlias = kbAlias;
            CommandId = commandId;
            Method = method;
            Capacity = capacity;
            WaitedMs = (long)waitedMs;
        }
    }

    /// <summary>
    /// Issue #692. The Worker was already stopping (for example an idle-timeout reap) when
    /// the command was offered, so it was refused before being enqueued. No byte reached
    /// the Worker, so the command was not executed and may be dispatched to a replacement.
    /// </summary>
    /// <summary>Values of <c>error.data.dispatchState</c> on a Gateway-generated Worker failure.</summary>
    public static class WorkerDispatchState
    {
        public const string NotDelivered = "not-delivered";
        public const string DeliveredUnanswered = "delivered-unanswered";
    }

    public sealed class WorkerCommandNotDeliveredException : Exception
    {
        public string? KbAlias { get; }
        public string? CommandId { get; }

        public WorkerCommandNotDeliveredException(string? kbAlias, string? commandId)
            : base($"Worker for KB '{kbAlias}' was stopping; command {commandId ?? "(no id)"} was NOT sent to the Worker.")
        {
            KbAlias = kbAlias;
            CommandId = commandId;
        }
    }

    /// <summary>Immutable snapshot of a worker startup failure. Classification lives in <see cref="SdkDiagnosticClassifier"/>.</summary>
    public sealed record WorkerStartupFailure(
        WorkerStopReason Reason,
        string Code,
        string Diagnostic,
        DateTime AtUtc,
        int? ExitCode);

    /// <summary>
    /// Explicit operational lifecycle states of an entry in the <see cref="WorkerPool"/>.
    /// Replaces scattered volatile flags (Spawning, Draining, DrainFailed, Reloading)
    /// with single-source-of-truth state transitions and invariant enforcement.
    /// </summary>
    public enum WorkerPoolEntryState
    {
        /// <summary>Slot created or registered; no active worker process.</summary>
        Vacant,

        /// <summary>Worker process is currently starting (under SpawnGate).</summary>
        Spawning,

        /// <summary>Worker process is alive, healthy, and eligible to serve commands.</summary>
        Active,

        /// <summary>A planned reload/drain is active; incoming acquires await replacement.</summary>
        Draining,

        /// <summary>Drain failed during worker stop or replacement; entry is fail-closed.</summary>
        DrainFailed
    }

    public sealed class WorkerPool : IWorkerSupervisor
    {
        private readonly Configuration _config;
        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);


        // issue #26 P3: durable alias→handle registry that OUTLIVES the worker
        // process. `_entries` is torn down the instant a worker exits (crash, idle
        // timeout, reload), which used to drop the only record of an ad-hoc opened
        // KB and made the next call fail with "Unknown KB". This map is populated on
        // every acquire/open and cleared ONLY on an explicit Close/Evict, so a KB the
        // user opened stays resolvable (and auto-re-attaches) across worker recycles.
        private readonly ConcurrentDictionary<string, KbHandle> _known =
            new ConcurrentDictionary<string, KbHandle>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, WorkerStartupFailure> _startupFailures =
            new ConcurrentDictionary<string, WorkerStartupFailure>(StringComparer.OrdinalIgnoreCase);

        // Item 53: configured warm-spare count. Pre-spawn up to N workers
        // (bound to declared KBs in config.Environment.KBs[]) so the first
        // client tool call after process start doesn't pay the cold-start
        // cost. Capped at 5 to avoid runaway memory.
        public const int MaxWarmSpareCount = 5;
        private int _warmSpareCount;
        public int WarmSpareCount => _warmSpareCount;
        // PERFORMANCE (G-A1): previously a single `_spawnLock` serialised every Acquire across
        // all KBs. Now each KB has its own SpawnGate (per-Entry SemaphoreSlim), so two clients
        // opening different KBs proceed in parallel. The narrow `_capacityLock` still
        // protects the capacity/eviction window, which is cheap and infrequent.
        private readonly object _capacityLock = new object();

        // (raw string + already-parsed JObject) — see WorkerProcess.OnRpcResponse.
        public event Action<string, JObject>? OnRpcResponse;
        public event Action<string, JObject, string?>? OnRpcResponseWithContext;
        public event Action<KbHandle, WorkerStopReason>? OnWorkerExited;
        internal event Action<KbHandle>? OnWorkerStarted;

        // Plan 031: test-only seam. When set, SpawnWorkerAsync uses this instead of
        // constructing + Start()-ing a real WorkerProcess, so concurrency tests can
        // exercise the DrainAndReplaceAsync / AcquireAsync race without spawning a
        // real OS process. Always null in production.
        internal Func<KbHandle, WorkerProcess>? SpawnFactoryForTest;

        public WorkerPool(Configuration config) { _config = config; }

        private sealed class Entry
        {
            private readonly object _stateLock = new object();
            private WorkerPoolEntryState _state = WorkerPoolEntryState.Vacant;
            private bool _isSpawning;
            private bool _isReloading;

            public KbHandle Handle = null!;
            public WorkerProcess? Worker;
            public DateTime LastActivityUtc = DateTime.UtcNow;
            public readonly SemaphoreSlim SpawnGate = new SemaphoreSlim(1, 1);
            public readonly SemaphoreSlim LifecycleGate = new SemaphoreSlim(1, 1);
            public int InFlightCommands;
            public TaskCompletionSource<bool> DrainComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public WorkerPoolEntryState State { get { lock (_stateLock) return _state; } }
            public bool Draining { get { lock (_stateLock) return _state == WorkerPoolEntryState.Draining; } }
            public bool DrainFailed { get { lock (_stateLock) return _state == WorkerPoolEntryState.DrainFailed; } }
            public bool Spawning { get { lock (_stateLock) return _isSpawning || _state == WorkerPoolEntryState.Spawning; } }
            public bool Reloading { get { lock (_stateLock) return _isReloading; } }

            public void SetSpawning(bool spawning)
            {
                lock (_stateLock)
                {
                    _isSpawning = spawning;
                    if (spawning && _state == WorkerPoolEntryState.Vacant) _state = WorkerPoolEntryState.Spawning;
                    else if (!spawning && _state == WorkerPoolEntryState.Spawning) _state = WorkerPoolEntryState.Vacant;
                }
            }

            public bool TryBeginReload()
            {
                lock (_stateLock)
                {
                    if (_isReloading || _state == WorkerPoolEntryState.Draining) return false;
                    _isReloading = true;
                    _state = WorkerPoolEntryState.Draining;
                    return true;
                }
            }

            public void EndReload()
            {
                lock (_stateLock)
                {
                    _isReloading = false;
                    if (_state == WorkerPoolEntryState.Draining) _state = WorkerPoolEntryState.DrainFailed;
                }
            }

            public void TransitionTo(WorkerPoolEntryState targetState)
            {
                lock (_stateLock)
                {
                    _state = targetState;
                    if (targetState == WorkerPoolEntryState.Active) { _isSpawning = false; _isReloading = false; }
                }
            }
        }

        public IReadOnlyList<KbHandle> ListOpen() =>
            _entries.Values
                .Where(e => e.Worker != null)
                .Select(e => e.Handle)
                .ToArray();

        // issue #26 P3: every KB the user has opened this session, whether or not its
        // worker is currently alive. Callers resolve aliases against this so a momentarily
        // down worker doesn't make its KB "Unknown"; AcquireAsync then respawns on demand.
        public IReadOnlyList<KbHandle> ListKnown() => _known.Values.ToArray();

        public void RegisterKnown(KbHandle handle)
        {
            if (handle != null) _known[handle.NormalizedAlias] = handle;
        }

        public bool TryGetStartupFailure(string alias, out WorkerStartupFailure failure)
        {
            if (alias == null)
            {
                failure = null!;
                return false;
            }
            return _startupFailures.TryGetValue(alias, out failure!);
        }

        // issue #26 P1: true when a worker for this alias is in the middle of spawning
        // (or a planned drain-and-replace is in progress). whoami uses this to report an
        // honest "starting" instead of "respawning" when nothing is actually coming up.
        public bool IsSpawning(string alias)
        {
            if (alias == null) return false;
            return _entries.TryGetValue(alias, out var e) && (e.Spawning || e.Draining);
        }

        public bool IsAtCapacity()
        {
            int max = _config.Server?.MaxOpenKbs ?? 3;
            // Count >= max: when the pool already holds `max` entries, opening one
            // more forces an eviction (SpawnWorkerAsync's threshold), so we ARE at
            // capacity. The old strict ">" allowed max+1 KBs before flagging.
            return _entries.Count >= max;
        }

        public IReadOnlyList<string> GetKnownAliases()
        {
            return _entries.Keys.ToList();
        }

        public WorkerProcess? TryGetWorker(string alias)
        {
            if (alias == null) return null;
            if (_entries.TryGetValue(alias.ToLowerInvariant(), out var entry)) return entry.Worker;
            return null;
        }

        public WorkerProcess? TryGet(string alias)
        {
            if (_entries.TryGetValue(alias.ToLowerInvariant(), out var entry))
            {
                return entry.Worker;
            }

            return null;
        }

        public IReadOnlyList<KbPoolStatus> Snapshot()
        {
            return _entries.Values
                .Where(e => e.Worker != null)
                .Select(e => new KbPoolStatus(
                    e.Handle,
                    e.Worker!.Pid,
                    e.Worker!.WorkingSetBytes,
                    e.LastActivityUtc))
                .ToArray();
        }

        internal Task<WorkerProcess> AcquireAsync(KbHandle handle, CancellationToken ct,
            KbUseLeaseRegistry? leaseRegistry, SessionKbContextStore.Snapshot? snapshot,
            bool requireOwner, bool legacyResolutionPolicy)
        {
            if (!legacyResolutionPolicy && requireOwner)
            {
                if (snapshot?.Lease == null)
                    throw new KbLeaseValidationException("KB_NOT_OWNED", "This stateful operation requires an opened and selected KB owned by the current session.");
                string identity = (handle.Path ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
                leaseRegistry!.Validate(snapshot.Lease.Token, snapshot.OwnerScopeId, handle.NormalizedAlias, snapshot.ContextGeneration, identity);
            }
            return AcquireAsync(handle, ct);
        }

        public async Task<WorkerProcess> AcquireAsync(KbHandle handle, CancellationToken ct)
        {
            var entry = _entries.GetOrAdd(handle.NormalizedAlias, _ => new Entry { Handle = handle });
            // issue #26 P3: remember this KB durably the moment it's acquired, so it
            // stays resolvable after the worker exits and gets torn out of _entries.
            _known[handle.NormalizedAlias] = handle;
            // If this entry is draining (a planned reload is orchestrating a
            // kill → spawn cycle), wait for DrainComplete before proceeding.
            // This covers both the fast path (Worker != null) AND the case where
            // DrainAndReplaceAsync has already removed the old entry and we raced
            // to GetOrAdd a new empty one.
            if (entry.Draining)
            {
                // Bound the drain wait: if DrainAndReplaceAsync wedges between marking
                // Draining=true and signalling DrainComplete, an unbounded await here
                // hung every future acquire for this KB forever (each tool call escaped
                // only via the gateway-wide 60s timeout, then hung again).
                try
                {
                    await entry.DrainComplete.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"Worker reload for KB '{handle.Alias}' did not finish within 60s; acquire aborted instead of hanging.");
                }
                // After the drain the entry was replaced — re-read.
                entry = _entries.GetOrAdd(handle.NormalizedAlias, _ => new Entry { Handle = handle });
            }
            if (entry.DrainFailed)
                throw new InvalidOperationException($"Worker reload for KB '{handle.Alias}' failed; acquire refused until the shutting-down worker exits.");
            if (entry.Worker != null)
            {
                entry.LastActivityUtc = DateTime.UtcNow;
                return entry.Worker;
            }

            return await SpawnWorkerAsync(handle, entry, ct).ConfigureAwait(false);
        }

        // Plan 031: factored out of AcquireAsync so DrainAndReplaceAsync can spawn the
        // replacement worker directly onto the SAME entry it already holds Draining=true
        // on, instead of round-tripping through AcquireAsync's own GetOrAdd (which would
        // create a brand-new, non-draining entry if this one had been removed from
        // _entries in the meantime).
        private async Task<WorkerProcess> SpawnWorkerAsync(KbHandle handle, Entry entry, CancellationToken ct)
        {
            WorkerProcess? startedWorker = null;
            // PERFORMANCE (G-A1): per-KB gate. Two concurrent acquires for the SAME KB
            // serialise here, but concurrent acquires for DIFFERENT KBs are now parallel.
            await entry.SpawnGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (entry.Worker != null) return entry.Worker;

                // Narrow lock around the capacity-window: cheap, prevents two different
                // KBs from both deciding "not full" at the same instant.
                lock (_capacityLock)
                {
                    int max = _config.Server?.MaxOpenKbs ?? 3;
                    // `_entries` already contains the entry being spawned (Worker == null
                    // here), so the number of *other* potentially-open KBs is Count - 1.
                    // Evict when those alone fill the cap; post-spawn total stays <= max.
                    // SelectVictim skips Worker == null entries, so it never evicts the
                    // entry being created.
                    int others = Math.Max(0, _entries.Count - 1);
                    if (others >= max)
                    {
                        var victim = SelectVictim();
                        if (victim != null)
                        {
                            EvictEntry(victim);
                        }

                        others = Math.Max(0, _entries.Count - 1);
                        if (others >= max)
                        {
                            _entries.TryRemove(handle.NormalizedAlias, out _);
                            throw new WorkerPoolFullException(ListOpen());
                        }
                    }
                }

                WorkerProcess? createdWorker = null;
                try
                {
                    WorkerProcess worker = createdWorker = SpawnFactoryForTest != null
                        ? SpawnFactoryForTest(handle)
                        : new WorkerProcess(_config, handle);
                    worker.OnRpcResponse += (json, parsed) => OnRpcResponse?.Invoke(json, parsed);
                worker.OnRpcResponseWithContext += (json, parsed, alias) =>
                    OnRpcResponseWithContext?.Invoke(json, parsed, alias);
                var capturedHandle = handle;
                worker.OnWorkerExited += (reason) =>
                {
                    if (reason == WorkerStopReason.SdkCompatibilityRejected
                        || (!string.IsNullOrWhiteSpace(worker.StartupDiagnostic)
                            && !SdkDiagnosticClassifier.IsInformationalDiagnostic(worker.StartupDiagnostic)))
                    {
                        RememberStartupFailure(capturedHandle, worker.StartupDiagnostic, worker.LastExitCode,
                            "Worker exited during startup.");
                    }

                    // Detach the exited worker BEFORE notifying eager-respawn subscribers:
                    // they can finish spawning a replacement before this callback returns.
                    // Remove only the captured entry, preserving concurrent replacements,
                    // planned drains, and an entry reused by a completed planned reload.
                    if (!entry.Draining && (entry.Worker == null || ReferenceEquals(entry.Worker, worker)))
                        _entries.TryRemove(new KeyValuePair<string, Entry>(capturedHandle.NormalizedAlias, entry));
                    // Keep the durable _known record so this KB remains resolvable.
                    OnWorkerExited?.Invoke(capturedHandle, reason);
                };
                // Publish before Start: an immediate exit may be reported
                // synchronously by WorkerProcess.Start().
                entry.Worker = worker;
                if (SpawnFactoryForTest == null)
                {
                    entry.SetSpawning(true);   // issue #26 P1: a process really is coming up now.
                    try
                    {
                        worker.Start();
                    }
                    finally
                    {
                        entry.SetSpawning(false);
                    }
                    if (worker.SpawnMs.HasValue)
                    {
                        Program.OperationTracker.RegisterSpawnSample(handle.NormalizedAlias, worker.SpawnMs.Value);
                    }
                }
                entry.Worker = worker;
                _startupFailures.TryRemove(handle.NormalizedAlias, out _);
                if (!worker.IsProcessAliveForPool)
                {
                    _entries.TryRemove(handle.NormalizedAlias, out _);
                    throw new InvalidOperationException($"Worker for KB '{handle.Alias}' exited before registration.");
                }
                if (!_entries.TryGetValue(handle.NormalizedAlias, out var current)
                    || !ReferenceEquals(current, entry)
                    || !ReferenceEquals(current.Worker, worker))
                {
                    if (_entries.TryGetValue(handle.NormalizedAlias, out var replacement)
                        && replacement.Worker != null)
                        return replacement.Worker;
                    throw new InvalidOperationException($"Worker for KB '{handle.Alias}' exited during startup.");
                }
                entry.LastActivityUtc = DateTime.UtcNow;
                entry.TransitionTo(WorkerPoolEntryState.Active);
                startedWorker = worker;
                return worker;
                }
                catch (Exception ex)
                {
                    if (!(ex is WorkerPoolFullException))
                    {
                        RememberStartupFailure(handle, createdWorker?.StartupDiagnostic, createdWorker?.LastExitCode, ex.Message);
                    }
                    entry.Worker = null;
                    if (!entry.Draining)
                    {
                        entry.TransitionTo(WorkerPoolEntryState.Vacant);
                        _entries.TryRemove(new KeyValuePair<string, Entry>(handle.NormalizedAlias, entry));
                    }
                    throw;
                }
            }
            finally
            {
                entry.SpawnGate.Release();
                if (startedWorker != null && ReferenceEquals(TryGet(handle.NormalizedAlias), startedWorker))
                {
                    // Observers must not turn a registered Worker into a failed
                    // acquire or poison the reload's DrainFailed state.
                    try { OnWorkerStarted?.Invoke(handle); }
                    catch (Exception ex)
                    {
                        Program.Log($"[WorkerPool] OnWorkerStarted failed for '{handle.Alias}': {ex.Message}");
                    }
                }
            }
        }

        private void RememberStartupFailure(KbHandle handle, string? workerDiagnostic, int? exitCode, string fallback)
        {
            string diagnostic = string.IsNullOrWhiteSpace(workerDiagnostic) ? fallback : workerDiagnostic!;
            if (SdkDiagnosticClassifier.IsInformationalDiagnostic(diagnostic))
            {
                _startupFailures.TryRemove(handle.NormalizedAlias, out _);
                return;
            }

            WorkerStopReason reason = SdkDiagnosticClassifier.IsFatalDiagnostic(diagnostic)
                ? WorkerStopReason.SdkCompatibilityRejected
                : WorkerStopReason.None;
            _startupFailures[handle.NormalizedAlias] = new WorkerStartupFailure(
                reason, SdkDiagnosticClassifier.ClassifyCode(diagnostic), diagnostic, DateTime.UtcNow, exitCode);
        }

        /// <summary>
        /// Gateway-orchestrated graceful worker reload for the given alias.
        /// 1. Marks the entry as Draining so new AcquireAsync callers wait rather than
        ///    receiving the dying worker.
        /// 2. Stops the current worker with PlannedReload so OnWorkerExited skips
        ///    eager respawn.
        /// 3. Waits for the OS process to exit (up to <paramref name="drainTimeoutMs"/>).
        /// 4. Spawns a fresh worker via AcquireAsync, which also registers it.
        /// 5. Signals DrainComplete so waiting callers may proceed.
        /// Returns the new WorkerProcess on success, throws on failure.
        /// </summary>
        public async Task<WorkerProcess> DrainAndReplaceAsync(KbHandle handle, int drainTimeoutMs, CancellationToken ct,
            Func<WorkerProcess?, Task>? afterDrainBeforeSpawn = null)
        {
            if (!_entries.TryGetValue(handle.NormalizedAlias, out var entry))
                throw new InvalidOperationException($"No pool entry for alias '{handle.Alias}'.");

            if (!entry.TryBeginReload())
                throw new InvalidOperationException($"A worker reload is already in progress for alias '{handle.Alias}'.");

            await entry.LifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            bool drainSucceeded = false;
            try
            {
            // Plan 031: the entry now survives the whole drain window (never removed
            // from _entries), so a SECOND drain cycle on the same entry would otherwise
            // reuse the previous cycle's already-completed DrainComplete TCS — any
            // AcquireAsync awaiting it would fall through instantly instead of waiting.
            // Fresh TCS per drain cycle, installed before Draining flips true.
            entry.DrainComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            WorkerProcess? oldWorker = entry.Worker;
            if (oldWorker != null)
            {
                oldWorker.StopWithReason(WorkerStopReason.PlannedReload);
                try
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, drainTimeoutMs)));
                        await oldWorker.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Worker reload for KB '{handle.Alias}' did not stop within {drainTimeoutMs}ms.");
                    }
                    if (!oldWorker.ExitConfirmed)
                        throw new TimeoutException($"Worker reload for KB '{handle.Alias}' did not confirm process exit.");
            }

            // Plan 031: do NOT remove the entry here. The old worker's own OnWorkerExited
            // handler (wired in SpawnWorkerAsync) already skips removal while Draining is
            // true, so the entry — and its Draining flag — stays in place for the whole
            // drain window. That's what keeps a concurrent AcquireAsync on the wait path
            // instead of racing a GetOrAdd that would create a second, fresh, non-draining
            // entry for the same alias. Just drop the stale dead-worker reference so the
            // upcoming SpawnWorkerAsync call takes the spawn path instead of returning it.
            entry.Worker = null;

            // mode=hard hook: with the old worker exited (its exe unlocked) and eager respawn
            // suppressed by the caller, this is the ONLY safe window to swap the worker binary.
            // The old worker's SpawnedExePath tells the caller where to copy the new bits.
            if (afterDrainBeforeSpawn != null)
            {
                await afterDrainBeforeSpawn(oldWorker).ConfigureAwait(false);
            }

            try
            {
                // Spawn directly onto the SAME entry (not via AcquireAsync's GetOrAdd) —
                // the entry never left _entries, so GetOrAdd would just return it anyway,
                // but going direct keeps the intent explicit and avoids depending on that
                // GetOrAdd identity guarantee.
                var newWorker = await SpawnWorkerAsync(handle, entry, ct).ConfigureAwait(false);
                drainSucceeded = true;
                entry.TransitionTo(WorkerPoolEntryState.Active);
                return newWorker;
            }
            catch
            {
                entry.TransitionTo(WorkerPoolEntryState.DrainFailed);
                throw;
            }
            finally
            {
                // Draining must clear before DrainComplete fires: a waiter unblocked by
                // DrainComplete re-checks entry.Worker/Draining immediately, and must see
                // the entry as no longer draining once it wakes.
                entry.DrainComplete.TrySetResult(true);
            }
            }
            finally
            {
                if (!drainSucceeded)
                    entry.TransitionTo(WorkerPoolEntryState.DrainFailed);
                entry.DrainComplete.TrySetResult(true);
                entry.EndReload();
                entry.LifecycleGate.Release();
            }
        }

        public bool Close(string alias, WorkerStopReason reason = WorkerStopReason.ExplicitClose)
        {
            // Explicit close is the ONE place the durable record is intentionally forgotten.
            _known.TryRemove(alias.ToLowerInvariant(), out _);
            _startupFailures.TryRemove(alias.ToLowerInvariant(), out _);
            if (_entries.TryRemove(alias.ToLowerInvariant(), out var entry))
            {
                try { entry.Worker?.StopWithReason(reason); } catch { }
                return true;
            }

            return false;
        }

        // issue #26 P3: drop only the live-worker entry (so the next AcquireAsync spawns
        // a fresh process) WITHOUT forgetting the durable _known record. Used by the
        // eager-respawn path, which must not erase the KB the user opened — Close() does
        // both and would reintroduce the "Unknown KB after recycle" bug.
        public void DropLiveEntry(string alias)
        {
            if (alias == null) return;
            if (_entries.TryRemove(alias.ToLowerInvariant(), out var entry))
            {
                try { entry.Worker?.StopWithReason(WorkerStopReason.ExplicitClose); } catch { }
            }
        }

        // Plan 069: a job that tripped the async-edit stall watchdog means the worker's
        // STA thread is stuck inside a blocked SDK call that will never answer the
        // in-flight command — the old code marked the job 'stalled' but left the KB
        // wedged until the 15-min health-loop detector force-killed the process. This
        // drops the live entry AND stops the worker with WorkerStopReason.Wedged so the
        // OnWorkerExited eager-respawn path (Wedged is NOT in its skip list) immediately
        // brings up a fresh worker. Like DropLiveEntry, the durable _known record is
        // kept so the KB stays resolvable. Returns true when a live entry was recycled.
        public bool RecycleStalledWorker(string alias)
        {
            if (alias == null) return false;
            if (!_entries.TryRemove(alias.ToLowerInvariant(), out var entry)) return false;

            // The kill must succeed for the recycle to be real: if StopWithReason throws
            // (kill failed / process already gone), let it propagate so the caller does NOT
            // report recycledWorker:true. The entry is already dropped, so a fresh
            // AcquireAsync will spawn a replacement while the wedged process, if it still
            // lives, is reaped by the health-loop detector.
            entry.Worker?.StopWithReason(WorkerStopReason.Wedged);
            return true;
        }

        public void StopAll(WorkerStopReason reason = WorkerStopReason.GatewayShutdown)
        {
            foreach (var e in _entries.Values)
            {
                try { e.Worker?.StopWithReason(reason); } catch { }
            }
            _entries.Clear();
        }

        /// <summary>
        /// The least-recently-active entry that is safe to stop, or null when none is.
        ///
        /// <para>
        /// Issue #333. The scan previously skipped only entries with no Worker, so
        /// "oldest" was the only criterion and a Worker mid-build, mid-write or
        /// mid-index was eligible. Opening another KB at capacity could then stop the
        /// oldest Worker's process and take its in-flight command with it - which
        /// contradicts the documented idle-only eviction policy and, for an isolated
        /// Worker, is not recoverable by the caller.
        /// </para>
        ///
        /// <para>
        /// Eligibility is therefore "has a Worker, is not spawning, draining or
        /// reloading, and has no command in flight", and the same
        /// <see cref="_capacityLock"/> that guards the capacity decision is held
        /// throughout, so a command cannot be admitted to the entry between the
        /// decision and the stop. If nothing is eligible the caller reports the pool
        /// as full rather than stopping a busy Worker.
        /// </para>
        /// </summary>
        private Entry? SelectVictim()
        {
            // PERFORMANCE (G-B1): linear scan for the min LastActivityUtc instead of OrderBy
            // (which materialises the whole sequence into an internal buffer just to take the
            // first element). At MaxOpenKbs=3 the absolute time is irrelevant, but the
            // allocation-free path is friendlier to the eviction hot-spot under future growth.
            Entry? victim = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var e in _entries.Values)
            {
                if (e.Worker == null) continue;
                if (!IsEvictable(e)) continue;
                if (e.LastActivityUtc < oldest)
                {
                    oldest = e.LastActivityUtc;
                    victim = e;
                }
            }
            return victim;
        }

        /// <summary>
        /// Whether an entry can be stopped without losing or corrupting work. Callers
        /// must hold <see cref="_capacityLock"/>.
        /// </summary>
        private bool IsEvictable(Entry e) =>
            e.Worker != null
            && e.State == WorkerPoolEntryState.Active
            && Volatile.Read(ref e.InFlightCommands) == 0
            && !(WorkerSideActivity?.Invoke(e.Handle.NormalizedAlias) ?? false);

        /// <summary>
        /// issue #367: work the Worker still runs after its RPC returned (an async build, or a
        /// tracked command whose client timed out). It is not in <c>InFlightCommands</c>, so the
        /// owner of that knowledge reports it here and eviction treats the entry as busy.
        /// </summary>
        internal Func<string, bool>? WorkerSideActivity { get; set; }

        /// <summary>
        /// Tracks one command as in flight on a Worker's entry, so capacity eviction
        /// (issue #333) cannot select it. Returns a releaser the caller must invoke when
        /// the command finishes, whatever the outcome.
        ///
        /// <para>
        /// The increment and the eviction check share <see cref="_capacityLock"/>, so an
        /// entry cannot be judged idle and then have a command admitted before the
        /// decision is acted on. A null result means the alias is not in the pool, which
        /// is not an error here: the caller's Worker was resolved some other way and
        /// there is no entry to protect.
        /// </para>
        /// </summary>
        internal Action? BeginCommand(string? alias) => BeginCommand(alias, null);

        /// <summary>
        /// As <see cref="BeginCommand(string?)"/>, but when <paramref name="worker"/> is given the
        /// reservation is only taken if the entry still owns that Worker (issue #367). An eviction
        /// between acquiring the Worker and reserving it would otherwise route the command to a
        /// stopped process; the caller sees null and acquires again.
        /// </summary>
        internal Action? BeginCommand(string? alias, WorkerProcess? worker)
        {
            if (string.IsNullOrWhiteSpace(alias)) return null;
            Entry? entry;
            lock (_capacityLock)
            {
                if (!_entries.TryGetValue(alias.ToLowerInvariant(), out entry)) return null;
                if (worker != null && !ReferenceEquals(entry.Worker, worker)) return null;
                Interlocked.Increment(ref entry.InFlightCommands);
            }
            // The releaser is idempotent at this level rather than relying on the
            // caller. A double release would drive the count negative, and a negative
            // count reads as "not busy" in IsEvictable - which is exactly the condition
            // that would let a Worker executing a command be evicted.
            int released = 0;
            return () =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0) ReleaseCommand(entry);
            };
        }

        private static void ReleaseCommand(Entry entry)
        {
            if (Interlocked.Decrement(ref entry.InFlightCommands) < 0)
                Interlocked.Exchange(ref entry.InFlightCommands, 0);
        }

        /// <summary>Commands in flight on an entry. Diagnostics and tests only.</summary>
        internal int InFlightCommandsForTest(string alias)
        {
            lock (_capacityLock)
            {
                return _entries.TryGetValue(alias.ToLowerInvariant(), out var e)
                    ? Volatile.Read(ref e.InFlightCommands) : 0;
            }
        }

        /// <summary>
        /// The entry eviction would choose right now, honouring the idle-only policy.
        /// Tests and diagnostics; the real path is <see cref="SelectVictim"/> under the
        /// capacity lock.
        /// </summary>
        internal KbHandle? SelectEvictableVictimForTest()
        {
            lock (_capacityLock)
            {
                var victim = SelectVictim();
                return victim?.Handle;
            }
        }

        /// <summary>Marks an entry as in flight for a test. Mirrors <see cref="BeginCommand"/>.</summary>
        internal void SetInFlightForTest(string alias, int count)
        {
            lock (_capacityLock)
            {
                if (_entries.TryGetValue(alias.ToLowerInvariant(), out var e))
                    Volatile.Write(ref e.InFlightCommands, count);
            }
        }

        /// <summary>
        /// Test-only: captures the entry currently registered for <paramref name="alias"/>.
        /// The returned token is an opaque <c>Entry</c> reference, accepted only by
        /// <see cref="EvictCapturedEntryForTest"/>; <c>Entry</c> itself stays private.
        /// </summary>
        /// <remarks>
        /// The capacity window selects a victim under <see cref="_capacityLock"/> and
        /// removes it afterwards, so an eviction can legitimately act on an entry that is
        /// no longer the one registered for its alias. Capturing and evicting separately
        /// is what lets a test express that without exposing the entry type.
        /// </remarks>
        internal object CaptureEntryForTest(string alias) =>
            _entries[alias.ToLowerInvariant()];

        /// <summary>
        /// Test-only: runs the real <see cref="EvictEntry"/> against an entry captured
        /// earlier by <see cref="CaptureEntryForTest"/>, even if a different entry has
        /// since taken its alias.
        /// </summary>
        internal void EvictCapturedEntryForTest(object entryToken) => EvictEntry((Entry)entryToken);

        private void EvictEntry(Entry entry)
        {
            try { entry.Worker?.StopWithReason(WorkerStopReason.ExplicitClose); } catch { }
            // Remove only the entry we stopped: if this Worker's exit handler (which does
            // not take _capacityLock) already tore the alias out and a concurrent
            // AcquireAsync installed a replacement, the key overload would evict that
            // replacement and leave its process running with no entry to stop or report it.
            _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Handle.NormalizedAlias, entry));
        }

        // BUG-04: the per-call cap on how long ConfigureWarmSpares will wait for
        // pre-spawns to finish before reporting the rest as Skipped. Deliberately
        // generous (cold-start can be tens of seconds) but bounded so the tool call
        // itself can't hang indefinitely on a wedged spawn.
        internal static readonly TimeSpan WarmSpareAwaitCap = TimeSpan.FromSeconds(10);

        // Item 53: configure warm-spare count + optionally pre-spawn against the
        // supplied declared KBs. Caps at MaxWarmSpareCount. Returns:
        //   configured  – the count actually persisted (clamped + capped),
        //   requested   – the original requested value (so the agent sees the clamp),
        //   capped      – true if the request exceeded MaxWarmSpareCount,
        //   prespawned  – aliases of KBs the gateway successfully pre-spawned a worker for,
        //   skipped     – aliases skipped because they were already open, the spawn failed,
        //                 or the spawn was still running when WarmSpareAwaitCap elapsed.
        //
        // BUG-04 fix: previously this fired AcquireAsync fire-and-forget and returned
        // synchronously, so Prespawned/Skipped were built from an empty ConcurrentBag —
        // the tool call reported nothing while workers were still coming up in the
        // background. Now the pending spawns are awaited (bounded by WarmSpareAwaitCap)
        // before the result is built, so the reported lists reflect real outcomes.
        public async Task<WarmSpareResult> ConfigureWarmSpares(int requested, System.Collections.Generic.IReadOnlyList<KbHandle> declaredKbs)
        {
            int requestedOrig = requested;
            bool capped = false;
            if (requested < 0) requested = 0;
            if (requested > MaxWarmSpareCount) { requested = MaxWarmSpareCount; capped = true; }
            Interlocked.Exchange(ref _warmSpareCount, requested);

            var prespawned = new ConcurrentBag<string>();
            var skipped = new ConcurrentBag<string>();
            if (requested == 0 || declaredKbs == null)
            {
                return new WarmSpareResult(requestedOrig, requested, capped, prespawned.ToList(), skipped.ToList());
            }

            int budget = requested;
            var pending = new System.Collections.Generic.List<Task>();
            // Aliases we actually queued a spawn for — used below to tell "still spawning
            // past the cap" apart from "never queued" when reconciling the result.
            var queuedAliases = new System.Collections.Generic.List<string>();
            foreach (var kb in declaredKbs)
            {
                if (budget <= 0) break;
                if (_entries.TryGetValue(kb.NormalizedAlias, out var existing) && existing.Worker != null)
                {
                    skipped.Add(kb.Alias);
                    continue;
                }
                try
                {
                    var capturedKb = kb;
                    var capturedPrespawned = prespawned;
                    var capturedSkipped = skipped;
                    pending.Add(AcquireAsync(capturedKb, CancellationToken.None).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                            capturedPrespawned.Add(capturedKb.Alias);
                        else
                            capturedSkipped.Add(capturedKb.Alias);
                    }, TaskScheduler.Default));
                    queuedAliases.Add(kb.Alias);
                    budget--;
                }
                catch
                {
                    skipped.Add(kb.Alias);
                }
            }

            if (pending.Count > 0)
            {
                var all = Task.WhenAll(pending);
                await Task.WhenAny(all, Task.Delay(WarmSpareAwaitCap)).ConfigureAwait(false);
            }

            // Snapshot the bags now: this is the result we return, regardless of whether
            // any continuation is still pending. Queued spawns that haven't landed in
            // either bag yet (i.e. exceeded WarmSpareAwaitCap) are reported as Skipped —
            // the spawn itself is NOT cancelled and keeps running in the background;
            // it just isn't counted as Prespawned for THIS call's result.
            var prespawnedSnapshot = new System.Collections.Generic.List<string>(prespawned);
            var skippedSnapshot = new System.Collections.Generic.List<string>(skipped);
            var accountedFor = new System.Collections.Generic.HashSet<string>(
                prespawnedSnapshot.Concat(skippedSnapshot), StringComparer.OrdinalIgnoreCase);
            foreach (var alias in queuedAliases)
            {
                if (accountedFor.Add(alias))
                    skippedSnapshot.Add(alias);
            }

            return new WarmSpareResult(requestedOrig, requested, capped, prespawnedSnapshot, skippedSnapshot);
        }

        public sealed record WarmSpareResult(
            int Requested,
            int Configured,
            bool Capped,
            System.Collections.Generic.IReadOnlyList<string> Prespawned,
            System.Collections.Generic.IReadOnlyList<string> Skipped);

        internal void RegisterForTest(KbHandle h, DateTime? lastActivity = null, WorkerProcess? worker = null)
        {
            var entry = new Entry
            {
                Handle = h,
                LastActivityUtc = lastActivity ?? DateTime.UtcNow,
                Worker = worker
            };
            if (worker != null)
            {
                entry.TransitionTo(WorkerPoolEntryState.Active);
            }
            _entries[h.NormalizedAlias] = entry;
            // Mirror AcquireAsync: an acquired KB is also durably "known" (issue #26 P3).
            _known[h.NormalizedAlias] = h;
        }

        internal KbHandle? SelectVictimForTest()
        {
            return _entries.Values.OrderBy(e => e.LastActivityUtc).FirstOrDefault()?.Handle;
        }

        /// <summary>
        /// Test helper: marks the named entry as Draining so the AcquireAsync fast
        /// path is forced into the wait branch.  Returns the DrainComplete TCS so
        /// the caller can signal it when ready.
        /// </summary>
        internal TaskCompletionSource<bool> SetDrainingForTest(string alias)
        {
            if (!_entries.TryGetValue(alias.ToLowerInvariant(), out var entry))
                throw new InvalidOperationException($"No entry for alias '{alias}'.");
            entry.TransitionTo(WorkerPoolEntryState.Draining);
            return entry.DrainComplete;
        }

        /// <summary>
        /// Test helper: finishes the drain started by <see cref="SetDrainingForTest"/> the way
        /// DrainAndReplaceAsync does, clearing Draining before DrainComplete fires so a released
        /// waiter finds a settled entry.
        /// </summary>
        internal void CompleteDrainForTest(string alias)
        {
            if (!_entries.TryGetValue(alias.ToLowerInvariant(), out var entry))
                throw new InvalidOperationException($"No entry for alias '{alias}'.");
            entry.TransitionTo(entry.Worker != null ? WorkerPoolEntryState.Active : WorkerPoolEntryState.Vacant);
            entry.DrainComplete.TrySetResult(true);
        }

        private Entry? FindEntry(string? alias) =>
            alias != null && _entries.TryGetValue(alias.ToLowerInvariant(), out var e) ? e : null;

        /// <summary>Returns true when the entry for <paramref name="alias"/> exists and Draining==true.</summary>
        internal bool IsDrainingForTest(string alias) =>
            FindEntry(alias)?.Draining == true;

        internal bool IsReloadingForTest(string alias) =>
            FindEntry(alias)?.Reloading == true;

        internal WorkerPoolEntryState? GetEntryStateForTest(string alias) =>
            FindEntry(alias)?.State;
    }
}
