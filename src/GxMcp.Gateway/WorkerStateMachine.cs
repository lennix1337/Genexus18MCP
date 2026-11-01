using System;
using System.Collections.Concurrent;
using System.Threading;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Explicit operational lifecycle states of a <see cref="WorkerProcess"/>.
    /// Replaces scattered boolean flags and completion state checks with a verified state machine.
    /// </summary>
    public enum WorkerLifecycleState
    {
        Unstarted = 0,
        Starting = 1,
        PipeConnecting = 2,
        SdkInitializing = 3,
        Ready = 4,
        Busy = 5,
        Stopping = 6,
        Exited = 7,
        Crashed = 8,
        Disposed = 9
    }

    /// <summary>
    /// Thread-safe explicit state machine governing <see cref="WorkerProcess"/> lifecycle transitions.
    /// </summary>
    public sealed class WorkerStateMachine
    {
        private readonly object _lock = new object();
        private WorkerLifecycleState _state = WorkerLifecycleState.Unstarted;
        private WorkerStopReason _stopReason = WorkerStopReason.None;

        public WorkerLifecycleState CurrentState
        {
            get { lock (_lock) return _state; }
        }

        public WorkerStopReason StopReason
        {
            get { lock (_lock) return _stopReason; }
            internal set { lock (_lock) _stopReason = value; }
        }

        public bool IsStarting
        {
            get
            {
                lock (_lock)
                {
                    return _state == WorkerLifecycleState.Starting
                        || _state == WorkerLifecycleState.PipeConnecting
                        || _state == WorkerLifecycleState.SdkInitializing;
                }
            }
        }

        public bool IsStoppingOrStopped
        {
            get
            {
                lock (_lock)
                {
                    return _state == WorkerLifecycleState.Stopping
                        || _state == WorkerLifecycleState.Exited
                        || _state == WorkerLifecycleState.Crashed
                        || _state == WorkerLifecycleState.Disposed;
                }
            }
        }

        public bool IsOperational
        {
            get
            {
                lock (_lock)
                {
                    return _state == WorkerLifecycleState.Ready
                        || _state == WorkerLifecycleState.Busy;
                }
            }
        }

        public bool TryTransition(WorkerLifecycleState from, WorkerLifecycleState to)
        {
            lock (_lock)
            {
                if (_state != from) return false;
                _state = to;
                return true;
            }
        }

        public void TransitionTo(WorkerLifecycleState target, WorkerStopReason reason = WorkerStopReason.None)
        {
            lock (_lock)
            {
                if (_state == WorkerLifecycleState.Disposed) return;
                if ((_state == WorkerLifecycleState.Exited || _state == WorkerLifecycleState.Crashed)
                    && target != WorkerLifecycleState.Disposed)
                {
                    return;
                }

                _state = target;
                if (reason != WorkerStopReason.None)
                {
                    _stopReason = reason;
                }
            }
        }
    }

    /// <summary>
    /// Unified heartbeat and activity tracker for <see cref="WorkerProcess"/>.
    /// Consolidates interactive command activity, transport IO, background build/index heartbeats,
    /// and in-flight command deadlines into a single authoritative supervision model.
    /// </summary>
    public sealed class WorkerSupervisionTracker
    {
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        private long _lastResponseTicks = DateTime.UtcNow.Ticks;
        private long _lastBuildActiveTicks;
        private long _lastIndexActiveTicks;
        private readonly ConcurrentDictionary<string, DateTime> _inFlightStartTimes = new(StringComparer.Ordinal);

        public DateTime LastActivityUtc
        {
            get => new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            internal set => Interlocked.Exchange(ref _lastActivityTicks, value.Ticks);
        }

        public DateTime LastResponseUtc
        {
            get => new DateTime(Interlocked.Read(ref _lastResponseTicks), DateTimeKind.Utc);
            internal set => Interlocked.Exchange(ref _lastResponseTicks, value.Ticks);
        }

        public int InFlightCount => _inFlightStartTimes.Count;

        public void MarkActivity()
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        public void MarkResponse()
        {
            Interlocked.Exchange(ref _lastResponseTicks, DateTime.UtcNow.Ticks);
        }

        private void RecordBackgroundHeartbeat(ref long targetTicks, DateTime? utc)
        {
            if (utc == null)
            {
                MarkActivity();
            }
            Interlocked.Exchange(ref targetTicks, (utc ?? DateTime.UtcNow).Ticks);
        }

        private static bool IsHeartbeatActive(long ticks, TimeSpan graceWindow)
        {
            return ticks != 0 && (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)) < graceWindow;
        }

        public void MarkBuildActive(DateTime? utc = null) => RecordBackgroundHeartbeat(ref _lastBuildActiveTicks, utc);
        public void MarkIndexActive(DateTime? utc = null) => RecordBackgroundHeartbeat(ref _lastIndexActiveTicks, utc);

        public bool IsBuildActive(TimeSpan graceWindow) => IsHeartbeatActive(Interlocked.Read(ref _lastBuildActiveTicks), graceWindow);
        public bool IsIndexActive(TimeSpan graceWindow) => IsHeartbeatActive(Interlocked.Read(ref _lastIndexActiveTicks), graceWindow);

        public void TrackInFlightStart(string id, DateTime? startUtc = null)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _inFlightStartTimes[id] = startUtc ?? DateTime.UtcNow;
            }
        }

        public bool CompleteInFlight(string? id)
        {
            return id != null && _inFlightStartTimes.TryRemove(id, out _);
        }

        public void ClearInFlight()
        {
            _inFlightStartTimes.Clear();
        }

        public bool HasWedgedCommand(TimeSpan timeout, out TimeSpan oldestAge)
        {
            oldestAge = TimeSpan.Zero;
            DateTime oldestStart = DateTime.MaxValue;
            bool any = false;
            foreach (var kv in _inFlightStartTimes)
            {
                if (kv.Value < oldestStart)
                {
                    oldestStart = kv.Value;
                    any = true;
                }
            }
            if (!any) return false;
            oldestAge = DateTime.UtcNow - oldestStart;
            return oldestAge >= timeout;
        }

        public bool ShouldStopForIdle(TimeSpan idleTimeout, int queuedCommands, int inFlightCommands, bool isStarting, TimeSpan backgroundGraceWindow)
        {
            if (idleTimeout <= TimeSpan.Zero) return false;
            if (queuedCommands > 0 || inFlightCommands > 0) return false;
            if (isStarting) return false;
            if (IsBuildActive(backgroundGraceWindow) || IsIndexActive(backgroundGraceWindow)) return false;
            return DateTime.UtcNow - LastActivityUtc >= idleTimeout;
        }
    }
}
