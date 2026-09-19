using System;
using System.Threading;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Watchdog for an in-progress index build. Owns the heartbeat clock and the
    /// no-progress observation thread so KbService keeps only its build loops.
    ///
    /// The worker threads call <see cref="Beat"/> on every processed object; the
    /// watchdog thread polls the clock and, after <see cref="ResolveNoProgressSeconds"/>
    /// without a beat, reports a stall. Stopping the watchdog (<see cref="Stop"/>)
    /// is the normal completion path; <see cref="Cancel"/> remains an explicit
    /// recovery path for a stalled build. A status poll never cancels the worker.
    /// </summary>
    internal sealed class IndexBuildWatchdog
    {
        private readonly Action _cancelBuild;
        private readonly Action _onStalled;
        private long _lastProgressTicks;
        private volatile bool _stopRequested;
        private volatile bool _stallReported;
        private Thread _watchdogThread;

        public IndexBuildWatchdog(Action cancelBuild, Action onStalled = null)
        {
            _cancelBuild = cancelBuild ?? throw new ArgumentNullException(nameof(cancelBuild));
            _onStalled = onStalled;
        }

        public static int ResolveNoProgressSeconds()
        {
            const int defaultSeconds = 180;
            string raw = Environment.GetEnvironmentVariable("GXMCP_INDEX_NO_PROGRESS_SEC");
            int seconds;
            return int.TryParse(raw, out seconds) ? Math.Max(30, Math.Min(3600, seconds)) : defaultSeconds;
        }

        internal static bool IsProgressStalled(DateTime lastProgressUtc, DateTime nowUtc, int timeoutSeconds)
        {
            return lastProgressUtc != default(DateTime)
                && (nowUtc - lastProgressUtc).TotalSeconds >= Math.Max(1, timeoutSeconds);
        }

        public DateTime LastProgressUtc
            => new DateTime(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void Beat()
        {
            Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
            _stallReported = false;
        }

        public void Start()
        {
            _stopRequested = false;
            _stallReported = false;
            Beat();
            int timeoutSeconds = ResolveNoProgressSeconds();
            _watchdogThread = new Thread(() =>
            {
                while (!_stopRequested)
                {
                    Thread.Sleep(1000);
                    var lastProgress = LastProgressUtc;
                    if (IsProgressStalled(lastProgress, DateTime.UtcNow, timeoutSeconds) && !_stallReported)
                    {
                        Logger.Warn("[INDEX-STALLED] no progress for "
                            + (long)(DateTime.UtcNow - lastProgress).TotalSeconds + "s; worker left running for explicit recovery.");
                        _stallReported = true;
                        try { _onStalled?.Invoke(); } catch (Exception ex) { Logger.Warn("Index stall notification failed: " + ex.Message); }
                    }
                }
            })
            { IsBackground = true, Name = "GxMcp-IndexWatchdog", Priority = ThreadPriority.BelowNormal };
            _watchdogThread.Start();
        }

        /// <summary>Normal completion path: stops the poll loop without cancelling.</summary>
        public void Stop() => _stopRequested = true;

        /// <summary>
        /// Recovery path for a stalled build: stops the loop and invokes the
        /// cancel action (thread aborts + failure marking) exactly once.
        /// </summary>
        public void Cancel()
        {
            _stopRequested = true;
            _cancelBuild();
        }
    }
}
