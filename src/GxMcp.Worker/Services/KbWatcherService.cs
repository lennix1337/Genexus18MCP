using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class KbWatcherService
    {
        // Shared gate: WriteService increments while a save transaction is in flight,
        // so the watcher can skip its tick instead of racing GetKeys against a live tx.
        // Both sides go through the SDK's KB.DesignModel.Objects collection; concurrent
        // access during writes was producing intermittent generic "Erro" messages.
        private static int _activeWriteCount = 0;

        public static IDisposable AcquireWriteGate()
        {
            Interlocked.Increment(ref _activeWriteCount);
            return new WriteGate();
        }

        private sealed class WriteGate : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref _activeWriteCount);
            }
        }

        public static bool IsWriteInProgress => Volatile.Read(ref _activeWriteCount) > 0;

        private readonly KbService _kbService;
        private readonly IndexCacheService _indexCache;
        private DateTime _lastCheckTime;
        private volatile bool _isRunning = false;
        private Thread _watcherThread;
        private readonly ManualResetEventSlim _wakeSignal = new ManualResetEventSlim(false);
        private readonly Action<string, string, DateTime> _onObjectChanged;
        private readonly Action<string, string, DateTime> _onObjectDeleted;
        private readonly HashSet<Guid> _notifiedInLastTick = new HashSet<Guid>();

        internal const int BasePollIntervalMs = 5000;
        internal const int MaxIdlePollIntervalMs = 15000;

        internal static int ComputeNextPollIntervalMs(int currentIntervalMs, bool hadActivity)
        {
            if (hadActivity) return BasePollIntervalMs;
            int clamped = Math.Max(BasePollIntervalMs, currentIntervalMs);
            return Math.Min(MaxIdlePollIntervalMs, clamped * 2);
        }

        // v2.6.6 Stream H (FR#26) — fires when the active environment changes
        // so the gateway can invalidate its cached KbHandle.ActiveEnvironment
        // value. Carries (envName, envVersion); both may be null when the SDK
        // round-trip fails. Subscribers must be allocation-light: this is on
        // the watcher poll path.
        public event Action<string, string> OnEnvironmentChanged;

        private string _lastObservedEnv;
        private string _lastObservedEnvVersion;
        private bool _hasObservedEnv;

        public KbWatcherService(KbService kbService, Action<string, string, DateTime> onObjectChanged, IndexCacheService indexCache = null, Action<string, string, DateTime> onObjectDeleted = null)
        {
            _kbService = kbService;
            _onObjectChanged = onObjectChanged;
            _indexCache = indexCache;
            _onObjectDeleted = onObjectDeleted;
            _lastCheckTime = DateTime.UtcNow; // Changed to UtcNow because KBObject.LastUpdate uses UTC. This prevents an initial flood of notifications.
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _watcherThread = new Thread(WatcherLoop)
            {
                IsBackground = true,
                Name = "KbWatcherThread",
                Priority = ThreadPriority.BelowNormal
            };
            _watcherThread.SetApartmentState(ApartmentState.STA);
            _watcherThread.Start();
            
            Logger.Info("KbWatcherService started.");
        }

        public void Stop()
        {
            _isRunning = false;
            try { _wakeSignal.Set(); } catch { }
        }

        private void WatcherLoop()
        {
            // Initial delay to let the system settle
            _wakeSignal.Wait(BasePollIntervalMs);
            int pollIntervalMs = BasePollIntervalMs;

            while (_isRunning)
            {
                bool hadActivity = false;
                try
                {
                    if (IsWriteInProgress)
                    {
                        // Skip this tick: a save transaction is open; polling DesignModel.Objects
                        // mid-transaction races the SDK and surfaces as random "Erro" later.
                        Logger.Debug("KbWatcher: skipping tick (write in progress).");
                        hadActivity = true;
                    }
                    else
                    {
                        hadActivity = RunTickOnDispatcherStaThread();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"KbWatcher Loop Error: {ex.Message}");
                }

                pollIntervalMs = ComputeNextPollIntervalMs(pollIntervalMs, hadActivity);
                _wakeSignal.Reset();
                if (_isRunning)
                    _wakeSignal.Wait(pollIntervalMs);
            }
        }

        // Plan 037: this thread must not touch the SDK directly — a second STA apartment
        // calling kb.DesignModel.*/GetActiveEnvironment can interleave with any SDK call
        // the dispatcher issues on its own STA thread (Program's sdkWorker). Instead, post
        // the SDK-touching part of the tick onto Program.SdkActionQueue, which that same
        // STA thread drains (only when no real dispatched command is pending), and wait
        // here — bounded — for it to finish before scheduling the next tick.
        private bool RunTickOnDispatcherStaThread()
        {
            bool hadActivity = false;
            using (var done = new ManualResetEventSlim(false))
            {
            bool queued = Program.EnqueueSdkAction(() =>
            {
                    try
                    {
                        var kb = _kbService.GetKB();
                        if (kb != null)
                        {
                            if (CheckForChanges(kb)) hadActivity = true;
                            if (CheckForEnvironmentChange()) hadActivity = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"KbWatcher tick error: {ex.Message}");
                    }
                    finally
                    {
                        try { done.Set(); } catch { }
                    }
            }, "kb-watcher");
            if (!queued)
            {
                Logger.Warn("[Watcher] SDK action queue is full; skipping this tick until the next poll.");
            }

                // Bounded wait: if the dispatcher STA thread is busy with a long-running
                // command, don't block this thread forever — the queued job still runs
                // and completes on its own; the next tick is simply posted a cycle later.
                if (queued) done.Wait(15000);
            }
            return hadActivity;
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#26) — detect environment switches.
        ///
        /// The SDK exposes environment-change as a property mutation rather
        /// than a stable event in some versions, so we poll cheaply (string
        /// compare on the same tick as the object-change scan) and only
        /// raise <see cref="OnEnvironmentChanged"/> when the value flips.
        /// First observation seeds the baseline silently.
        /// </summary>
        public bool CheckForEnvironmentChange()
        {
            try
            {
                string env = _kbService.GetActiveEnvironment();
                string version = _kbService.GetActiveEnvironmentVersion();

                if (!_hasObservedEnv)
                {
                    _lastObservedEnv = env;
                    _lastObservedEnvVersion = version;
                    _hasObservedEnv = true;
                    return false;
                }

                bool envFlipped = !string.Equals(env, _lastObservedEnv, StringComparison.Ordinal);
                bool versionFlipped = !string.Equals(version, _lastObservedEnvVersion, StringComparison.Ordinal);
                if (!envFlipped && !versionFlipped) return false;

                _lastObservedEnv = env;
                _lastObservedEnvVersion = version;
                Logger.Info($"[KB-WATCHER] Environment change observed: env='{env}' version='{version}'");

                var handler = OnEnvironmentChanged;
                if (handler != null)
                {
                    try { handler(env, version); }
                    catch (Exception ex) { Logger.Warn("OnEnvironmentChanged subscriber threw: " + ex.Message); }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug("CheckForEnvironmentChange error: " + ex.Message);
                return false;
            }
        }

        // Test-only hook: lets tests fire the event without spinning the watcher thread.
        internal void RaiseEnvironmentChangedForTest(string env, string version)
        {
            var handler = OnEnvironmentChanged;
            handler?.Invoke(env, version);
        }

        private bool CheckForChanges(dynamic kb)
        {
            try
            {
                var modifiedKeys = kb.DesignModel.Objects.GetKeys(_lastCheckTime);
                DateTime nextCheckTime = _lastCheckTime;
                bool foundNewer = false;
                var batch = new List<KBObject>();
                bool hadDeletion = false;

                foreach (var key in (System.Collections.IEnumerable)modifiedKeys)
                {
                    try
                    {
                        if (!TryGetChangedObject(kb, key, out KBObject obj))
                        {
                            hadDeletion = true;
                            continue;
                        }
                        ConsiderChangedObject(obj, batch, ref nextCheckTime, ref foundNewer);
                    }
                    catch { }
                }

                NotifyChangedObjects(batch, nextCheckTime, foundNewer);
                return batch.Count > 0 || hadDeletion;
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("KB is busy"))
                    Logger.Debug($"CheckForChanges error: {ex.Message}");
                return false;
            }
        }

        private bool TryGetChangedObject(dynamic kb, object rawKey, out KBObject changed)
        {
            changed = null;
            var key = (Artech.Udm.Framework.EntityKey)rawKey;
            changed = kb.DesignModel.Objects.Get(key) as KBObject;
            if (changed != null) return true;

            SearchIndex.IndexEntry deleted = null;
            try { deleted = _indexCache?.GetIndex()?.FindByEntityKey(Convert.ToString(key)); } catch { }
            if (deleted == null) return false;

            _indexCache.RemoveEntryByGuid(deleted.Guid);
            Guid deletedGuid;
            if (Guid.TryParse(deleted.Guid, out deletedGuid))
                ObjectService.RemoveReadCacheEntries(deletedGuid, null);
            DateTime deletionTime = DateTime.UtcNow;
            _onObjectDeleted?.Invoke(deleted.Name, deleted.Type, deletionTime);
            Logger.Info($"External deletion detected: {deleted.Name} ({deleted.Type}) at {deletionTime:o}");
            return false;
        }

        private void ConsiderChangedObject(KBObject obj, List<KBObject> batch, ref DateTime nextCheckTime, ref bool foundNewer)
        {
            DateTime objectLastUpdate = SdkTimestampNormalizer.NormalizeUtc(obj.LastUpdate);
            if (objectLastUpdate > _lastCheckTime)
            {
                if (objectLastUpdate > nextCheckTime)
                {
                    nextCheckTime = objectLastUpdate;
                    foundNewer = true;
                }
                batch.Add(obj);
            }
            else if (objectLastUpdate == _lastCheckTime && !_notifiedInLastTick.Contains(obj.Guid))
            {
                batch.Add(obj);
            }
        }

        private void NotifyChangedObjects(List<KBObject> batch, DateTime nextCheckTime, bool foundNewer)
        {
            if (batch.Count == 0) return;
            if (foundNewer) _notifiedInLastTick.Clear();

            foreach (KBObject obj in batch)
            {
                DateTime objectLastUpdate = SdkTimestampNormalizer.NormalizeUtc(obj.LastUpdate);
                if (objectLastUpdate == nextCheckTime) _notifiedInLastTick.Add(obj.Guid);
                Logger.Info($"External change detected: {obj.Name} ({obj.TypeDescriptor.Name}) at {objectLastUpdate:o}");
                // Evict before the index entry gets the new LastUpdate: the managed part
                // cache is keyed by guid|part with no LastUpdate check, so otherwise source
                // search would keep serving the pre-save text for its whole TTL.
                ObjectService.RemoveReadCacheEntries(obj.Guid, null);
                try { _indexCache?.UpdateEntry(obj); }
                catch (Exception ex) { Logger.Debug($"Watcher index update failed for {obj.Name}: {ex.Message}"); }
                _onObjectChanged?.Invoke(obj.Name, obj.TypeDescriptor.Name, objectLastUpdate);
            }
            _lastCheckTime = nextCheckTime;
        }
    }
}
