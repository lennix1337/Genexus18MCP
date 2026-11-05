using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace GxMcp.Worker
{
    // Seams of the shared-host broker (RFC #854). The contract is pinned in the header of
    // src/GxMcp.Worker.Tests/SharedWorkerHostSchedulerWiringTests.cs. The production
    // implementations below are the only ones the Worker uses; tests pass fakes through the
    // six-argument SharedWorkerHostRuntime constructor. This file is SDK-free and is linked
    // into GxMcp.SharedHost.TestHost.

    /// <summary>Monotonic clock and timer factory of the broker.</summary>
    internal interface ISharedHostClock
    {
        /// <summary>Monotonic milliseconds.</summary>
        long NowMs { get; }

        ISharedHostTimer CreateTimer(Action callback);
    }

    /// <summary>A one-shot timer armed at an absolute monotonic time.</summary>
    internal interface ISharedHostTimer : IDisposable
    {
        /// <summary>Arms the timer at <paramref name="dueAtMs"/>, replacing any earlier arming; null disarms it.</summary>
        void Change(long? dueAtMs);
    }

    internal interface ISharedHostChildLauncher
    {
        /// <summary>Starts the child; throws when the start fails.</summary>
        ISharedHostChild Start(ProcessStartInfo startInfo);
    }

    internal interface ISharedHostChild : IDisposable
    {
        int Pid { get; }
        long StartTimeUtcTicks { get; }
        bool HasExited { get; }

        /// <summary>-1 while running.</summary>
        int ExitCode { get; }

        /// <summary>Refreshed on read; 0 after exit.</summary>
        long WorkingSetBytes { get; }

        /// <summary>Refreshed on read; 0 after exit.</summary>
        long PrivateBytes { get; }

        /// <summary>Signalled when the process exits.</summary>
        WaitHandle ExitedHandle { get; }

        TextReader StandardOutput { get; }
        TextReader StandardError { get; }

        /// <summary>Writes one frame to stdin and flushes.</summary>
        void WriteLine(string line);

        void Kill();
    }

    internal interface ISharedHostRegistryWriter
    {
        void Write(SharedWorkerHostRegistryRecord record);
        void Delete();
    }

    /// <summary>One Gateway attachment as the broker runtime sees it.</summary>
    internal interface ISharedHostAttachment
    {
        string ConnectionId { get; }
        string AttachmentId { get; set; }
        long NextSequence();

        /// <summary>Queues one outbound line to this Attachment; false when it is stopped or full.</summary>
        bool Enqueue(string line);

        /// <summary>Ends the transport, which then calls <c>SharedWorkerHostRuntime.Detach</c>.</summary>
        void Stop();
    }

    /// <summary>Stopwatch clock; each timer wraps one <see cref="System.Threading.Timer"/>.</summary>
    internal sealed class SharedHostSystemClock : ISharedHostClock
    {
        internal static readonly SharedHostSystemClock Instance = new SharedHostSystemClock();

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public long NowMs => _stopwatch.ElapsedMilliseconds;

        public ISharedHostTimer CreateTimer(Action callback) => new SystemTimer(this, callback);

        private sealed class SystemTimer : ISharedHostTimer
        {
            private readonly SharedHostSystemClock _clock;
            private readonly Action _callback;
            private readonly Timer _timer;
            private int _disposed;

            internal SystemTimer(SharedHostSystemClock clock, Action callback)
            {
                _clock = clock;
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            }

            public void Change(long? dueAtMs)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try
                {
                    if (!dueAtMs.HasValue)
                    {
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
                        return;
                    }
                    long delay = Math.Max(0L, dueAtMs.Value - _clock.NowMs);
                    _timer.Change(Math.Min(delay, (long)int.MaxValue - 1), Timeout.Infinite);
                }
                catch (ObjectDisposedException) { }
            }

            private void Fire()
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                // A timer callback runs on the thread pool, where an escaping exception ends the process.
                try { _callback(); }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine("[SharedWorkerHost] timer callback failed: " + ex.Message); } catch { }
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { _timer.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Starts the child with <see cref="Process.Start()"/>.</summary>
    internal sealed class SharedHostProcessLauncher : ISharedHostChildLauncher
    {
        public ISharedHostChild Start(ProcessStartInfo startInfo)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Could not start shared Worker child.");
                return new SharedHostProcessChild(process);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
    }

    internal sealed class SharedHostProcessChild : ISharedHostChild
    {
        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private readonly ManualResetEvent _exited = new ManualResetEvent(false);
        private readonly object _writeGate = new object();

        internal SharedHostProcessChild(Process process)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            Pid = process.Id;
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            _stdin = SharedWorkerHost.CreateChildStdinWriter(process.StandardInput.BaseStream);
            process.Exited += (_, __) => SignalExited();
            // The process may have exited before the handler was attached.
            try { if (process.HasExited) SignalExited(); } catch { }
        }

        public int Pid { get; }
        public long StartTimeUtcTicks { get; }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int ExitCode
        {
            get
            {
                try { return _process.HasExited ? _process.ExitCode : -1; }
                catch { return -1; }
            }
        }

        public long WorkingSetBytes
        {
            get
            {
                try
                {
                    if (_process.HasExited) return 0;
                    _process.Refresh();
                    return _process.WorkingSet64;
                }
                catch { return 0; }
            }
        }

        public long PrivateBytes
        {
            get
            {
                try
                {
                    if (_process.HasExited) return 0;
                    _process.Refresh();
                    return _process.PrivateMemorySize64;
                }
                catch { return 0; }
            }
        }

        public WaitHandle ExitedHandle => _exited;
        public TextReader StandardOutput => _process.StandardOutput;
        public TextReader StandardError => _process.StandardError;

        public void WriteLine(string line)
        {
            lock (_writeGate)
            {
                _stdin.WriteLine(line);
                _stdin.Flush();
            }
        }

        public void Kill()
        {
            try { lock (_writeGate) _stdin.Close(); } catch { }
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(3000);
                }
            }
            catch { }
        }

        private void SignalExited()
        {
            try { _exited.Set(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            try { lock (_writeGate) _stdin.Dispose(); } catch { }
            try { _process.Dispose(); } catch { }
        }
    }

    /// <summary>The registry record file: a temporary file plus <see cref="File.Replace(string, string, string)"/>.</summary>
    internal sealed class SharedHostFileRegistryWriter : ISharedHostRegistryWriter
    {
        private readonly string _recordPath;

        internal SharedHostFileRegistryWriter(string recordPath)
        {
            if (string.IsNullOrWhiteSpace(recordPath)) throw new ArgumentException("A registry record path is required.", nameof(recordPath));
            _recordPath = recordPath;
        }

        internal string RecordPath => _recordPath;

        public void Write(SharedWorkerHostRegistryRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string directory = Path.GetDirectoryName(_recordPath);
            Directory.CreateDirectory(directory);
            string temp = _recordPath + ".tmp-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(record, Formatting.None), new UTF8Encoding(false));
                if (File.Exists(_recordPath)) File.Replace(temp, _recordPath, null);
                else File.Move(temp, _recordPath);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        public void Delete()
        {
            try { if (File.Exists(_recordPath)) File.Delete(_recordPath); } catch { }
        }
    }
}
