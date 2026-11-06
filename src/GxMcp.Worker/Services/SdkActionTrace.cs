using System;
using System.Diagnostics;
using System.Threading;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    internal sealed class SdkActionTrace
    {
        private long _nextId;
        private string _active;
        private readonly Action<string> _log;
        [ThreadStatic] private static long _currentId;

        internal SdkActionTrace(Action<string> log) { _log = log; }
        internal string Active => Volatile.Read(ref _active);
        internal static long CurrentId => _currentId;

        internal void Run(string name, Action action)
        {
            long id = Interlocked.Increment(ref _nextId);
            string previous = Interlocked.Exchange(ref _active, "id=" + id + " action=" + name);
            long previousId = _currentId;
            _currentId = id;
            var watch = Stopwatch.StartNew();
            _log("[STA-ACTION] begin id=" + id + " action=" + name);
            try { action(); }
            finally
            {
                _log("[STA-ACTION] end id=" + id + " action=" + name + " elapsedMs=" + watch.ElapsedMilliseconds);
                _currentId = previousId;
                Interlocked.Exchange(ref _active, previous);
            }
        }
    }
}
