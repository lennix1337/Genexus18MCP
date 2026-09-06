using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

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
        private bool _isRunning = false;
        private Thread _watcherThread;
        private readonly Action<string, string, DateTime> _onObjectChanged;
        private readonly HashSet<Guid> _notifiedInLastTick = new HashSet<Guid>();

        // v2.6.6 Stream H (FR#26) — fires when the active environment changes
        // so the gateway can invalidate its cached KbHandle.ActiveEnvironment
        // value. Carries (envName, envVersion); both may be null when the SDK
        // round-trip fails. Subscribers must be allocation-light: this is on
        // the watcher poll path.
        public event Action<string, string> OnEnvironmentChanged;

        private string _lastObservedEnv;
        private string _lastObservedEnvVersion;
        private bool _hasObservedEnv;

        public KbWatcherService(KbService kbService, Action<string, string, DateTime> onObjectChanged, IndexCacheService indexCache = null)
        {
            _kbService = kbService;
            _onObjectChanged = onObjectChanged;
            _indexCache = indexCache;
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
        }

        private void WatcherLoop()
        {
            // Initial delay to let the system settle
            Thread.Sleep(5000);

            while (_isRunning)
            {
                try
                {
                    if (IsWriteInProgress)
                    {
                        // Skip this tick: a save transaction is open; polling DesignModel.Objects
                        // mid-transaction races the SDK and surfaces as random "Erro" later.
                        Logger.Debug("KbWatcher: skipping tick (write in progress).");
                    }
                    else
                    {
                        RunTickOnDispatcherStaThread();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"KbWatcher Loop Error: {ex.Message}");
                }

                // Poll interval: 5 seconds (standard for metadata checks)
                Thread.Sleep(5000);
            }
        }

        // Plan 037: this thread must not touch the SDK directly — a second STA apartment
        // calling kb.DesignModel.*/GetActiveEnvironment can interleave with any SDK call
        // the dispatcher issues on its own STA thread (Program's sdkWorker). Instead, post
        // the SDK-touching part of the tick onto Program.SdkActionQueue, which that same
        // STA thread drains (only when no real dispatched command is pending), and wait
        // here — bounded — for it to finish before scheduling the next tick.
        private void RunTickOnDispatcherStaThread()
        {
            using (var done = new ManualResetEventSlim(false))
            {
            bool queued = Program.EnqueueSdkAction(() =>
            {
                    try
                    {
                        var kb = _kbService.GetKB();
                        if (kb != null)
                        {
                            CheckForChanges(kb);
                            CheckForEnvironmentChange();
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
            });
            if (!queued)
            {
                Logger.Warn("[Watcher] SDK action queue is full; skipping this tick until the next poll.");
            }

                // Bounded wait: if the dispatcher STA thread is busy with a long-running
                // command, don't block this thread forever — the queued job still runs
                // and completes on its own; the next tick is simply posted a cycle later.
                if (queued) done.Wait(15000);
            }
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
        public void CheckForEnvironmentChange()
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
                    return;
                }

                bool envFlipped = !string.Equals(env, _lastObservedEnv, StringComparison.Ordinal);
                bool versionFlipped = !string.Equals(version, _lastObservedEnvVersion, StringComparison.Ordinal);
                if (!envFlipped && !versionFlipped) return;

                _lastObservedEnv = env;
                _lastObservedEnvVersion = version;
                Logger.Info($"[KB-WATCHER] Environment change observed: env='{env}' version='{version}'");

                var handler = OnEnvironmentChanged;
                if (handler != null)
                {
                    try { handler(env, version); }
                    catch (Exception ex) { Logger.Warn("OnEnvironmentChanged subscriber threw: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("CheckForEnvironmentChange error: " + ex.Message);
            }
        }

        // Test-only hook: lets tests fire the event without spinning the watcher thread.
        internal void RaiseEnvironmentChangedForTest(string env, string version)
        {
            var handler = OnEnvironmentChanged;
            handler?.Invoke(env, version);
        }

        private void CheckForChanges(dynamic kb)
        {
            try
            {
                // FAST PATH: Use GetKeys with DateTime to find modified objects since last check.
                var modifiedKeys = kb.DesignModel.Objects.GetKeys(_lastCheckTime);
                
                DateTime nextCheckTime = _lastCheckTime;
                bool foundNewer = false;
                var batch = new List<dynamic>();

                foreach (var key in (System.Collections.IEnumerable)modifiedKeys)
                {
                    try
                    {
                        var obj = kb.DesignModel.Objects.Get((Artech.Udm.Framework.EntityKey)key);
                        if (obj == null) continue;

                        if (obj.LastUpdate > _lastCheckTime)
                        {
                            if (obj.LastUpdate > nextCheckTime) 
                            {
                                nextCheckTime = obj.LastUpdate;
                                foundNewer = true;
                            }
                            batch.Add(obj);
                        }
                        else if (obj.LastUpdate == _lastCheckTime && !_notifiedInLastTick.Contains(obj.Guid))
                        {
                            batch.Add(obj);
                        }
                    }
                    catch { }
                }

                if (batch.Count > 0)
                {
                    if (foundNewer)
                    {
                        _notifiedInLastTick.Clear();
                    }

                    foreach (var obj in batch)
                    {
                        if (obj.LastUpdate == nextCheckTime)
                        {
                            _notifiedInLastTick.Add(obj.Guid);
                        }

                        Logger.Info($"External change detected: {obj.Name} ({obj.TypeDescriptor.Name}) at {obj.LastUpdate}");
                        // Fase 2: keep the in-memory index warm on live edits. The watcher
                        // thread is STA and already holds the KBObject, so UpdateEntry runs in
                        // the right context. This re-enriches the changed object (and collapses
                        // renames via Guid — see UpdateEntry) without waiting for a reindex.
                        // Skipped during write transactions by the IsWriteInProgress gate above.
                        try { _indexCache?.UpdateEntry((global::Artech.Architecture.Common.Objects.KBObject)obj); }
                        catch (Exception ixe) { Logger.Debug($"Watcher index update failed for {obj.Name}: {ixe.Message}"); }
                        _onObjectChanged?.Invoke(obj.Name, obj.TypeDescriptor.Name, obj.LastUpdate);
                    }

                    _lastCheckTime = nextCheckTime;
                }
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("KB is busy"))
                {
                    Logger.Debug($"CheckForChanges error: {ex.Message}");
                }
            }
        }
    }
}
