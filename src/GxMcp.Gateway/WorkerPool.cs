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

        // Plan 031: test-only seam. When set, SpawnWorkerAsync uses this instead of
        // constructing + Start()-ing a real WorkerProcess, so concurrency tests can
        // exercise the DrainAndReplaceAsync / AcquireAsync race without spawning a
        // real OS process. Always null in production.
        internal Func<KbHandle, WorkerProcess>? SpawnFactoryForTest;

        public WorkerPool(Configuration config) { _config = config; }

        private sealed class Entry
        {
            public KbHandle Handle = null!;
            public WorkerProcess? Worker;
            public DateTime LastActivityUtc = DateTime.UtcNow;
            public readonly SemaphoreSlim SpawnGate = new SemaphoreSlim(1, 1);
            // Draining: set to true when a planned worker reload is in progress.
            // AcquireAsync callers that hit the fast path while Draining==true wait
            // on DrainComplete before returning the freshly-spawned replacement.
            public volatile bool Draining;
            // issue #26 P1: true while a worker process is actively being spawned for
            // this entry (gate held, Start() not yet returned). Lets whoami/health tell
            // "a process really IS coming up" apart from "no worker and nothing spawning"
            // instead of reporting a perpetual, misleading "respawning".
            public volatile bool Spawning;
            public TaskCompletionSource<bool> DrainComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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

                WorkerProcess worker = SpawnFactoryForTest != null
                    ? SpawnFactoryForTest(handle)
                    : new WorkerProcess(_config, handle);
                worker.OnRpcResponse += (json, parsed) => OnRpcResponse?.Invoke(json, parsed);
                worker.OnRpcResponseWithContext += (json, parsed, alias) =>
                    OnRpcResponseWithContext?.Invoke(json, parsed, alias);
                var capturedHandle = handle;
                worker.OnWorkerExited += (reason) =>
                {
                    OnWorkerExited?.Invoke(capturedHandle, reason);
                    // Drop the live-worker entry (a fresh AcquireAsync respawns) but keep
                    // the durable _known record — issue #26 P3: the KB must stay resolvable.
                    // Plan 031: skip removal while a planned drain owns this entry —
                    // DrainAndReplaceAsync manages the entry's lifecycle itself across the
                    // whole binary-swap window and relies on Draining staying true (and the
                    // entry staying present) to keep protecting concurrent AcquireAsync
                    // callers from creating a second, fresh, non-draining entry.
                    if (_entries.TryGetValue(capturedHandle.NormalizedAlias, out var currentEntry) && currentEntry.Draining)
                        return;
                    _entries.TryRemove(capturedHandle.NormalizedAlias, out _);
                };
                if (SpawnFactoryForTest == null)
                {
                    entry.Spawning = true;   // issue #26 P1: a process really is coming up now.
                    try
                    {
                        worker.Start();
                    }
                    finally
                    {
                        entry.Spawning = false;
                    }
                    if (worker.SpawnMs.HasValue)
                    {
                        Program.OperationTracker.RegisterSpawnSample(handle.NormalizedAlias, worker.SpawnMs.Value);
                    }
                }
                entry.Worker = worker;
                entry.LastActivityUtc = DateTime.UtcNow;
                return worker;
            }
            finally
            {
                entry.SpawnGate.Release();
            }
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

            // Plan 031: the entry now survives the whole drain window (never removed
            // from _entries), so a SECOND drain cycle on the same entry would otherwise
            // reuse the previous cycle's already-completed DrainComplete TCS — any
            // AcquireAsync awaiting it would fall through instantly instead of waiting.
            // Fresh TCS per drain cycle, installed before Draining flips true.
            entry.DrainComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Mark draining before touching the process so any concurrent AcquireAsync
            // that passes the Worker != null check enters the wait path.
            entry.Draining = true;

            WorkerProcess? oldWorker = entry.Worker;
            if (oldWorker != null)
            {
                oldWorker.StopWithReason(WorkerStopReason.PlannedReload);
                // Wait for the OS process to exit.  We don't rethrow on timeout —
                // the OS process will linger but we still spawn a fresh one.
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(drainTimeoutMs);
                    await oldWorker.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* timeout or caller cancel — proceed */ }
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
                try { await afterDrainBeforeSpawn(oldWorker).ConfigureAwait(false); }
                catch (Exception ex) { Program.Log($"[Gateway] worker_reload afterDrain hook failed: {ex.Message}"); }
            }

            try
            {
                // Spawn directly onto the SAME entry (not via AcquireAsync's GetOrAdd) —
                // the entry never left _entries, so GetOrAdd would just return it anyway,
                // but going direct keeps the intent explicit and avoids depending on that
                // GetOrAdd identity guarantee.
                var newWorker = await SpawnWorkerAsync(handle, entry, ct).ConfigureAwait(false);
                return newWorker;
            }
            finally
            {
                // Draining must clear before DrainComplete fires: a waiter unblocked by
                // DrainComplete re-checks entry.Worker/Draining immediately, and must see
                // the entry as no longer draining once it wakes.
                entry.Draining = false;
                entry.DrainComplete.TrySetResult(true);
            }
        }

        public bool Close(string alias, WorkerStopReason reason = WorkerStopReason.ExplicitClose)
        {
            // Explicit close is the ONE place the durable record is intentionally forgotten.
            _known.TryRemove(alias.ToLowerInvariant(), out _);
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
                if (e.LastActivityUtc < oldest)
                {
                    oldest = e.LastActivityUtc;
                    victim = e;
                }
            }
            return victim;
        }

        private void EvictEntry(Entry entry)
        {
            try { entry.Worker?.StopWithReason(WorkerStopReason.ExplicitClose); } catch { }
            _entries.TryRemove(entry.Handle.NormalizedAlias, out _);
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
            _entries[h.NormalizedAlias] = new Entry
            {
                Handle = h,
                LastActivityUtc = lastActivity ?? DateTime.UtcNow,
                Worker = worker
            };
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
            entry.Draining = true;
            return entry.DrainComplete;
        }

        /// <summary>Returns true when the entry for <paramref name="alias"/> exists and Draining==true.</summary>
        internal bool IsDrainingForTest(string alias) =>
            _entries.TryGetValue(alias.ToLowerInvariant(), out var e) && e.Draining;
    }
}
