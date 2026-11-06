using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;

namespace GxMcp.Worker
{
    // Seams of the shared-host broker (RFC #854). The contract is pinned in the header of
    // src/GxMcp.Worker.Tests/SharedWorkerHostSchedulerWiringTests.cs. The production
    // implementations below are the only ones the Worker uses; tests pass fakes through the
    // six-argument or the eight-argument SharedWorkerHostRuntime constructor (the ownership
    // seams are pinned in the header of SharedWorkerHostOwnershipTests.cs). This file is
    // SDK-free and is linked into GxMcp.SharedHost.TestHost.

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

    /// <summary>
    /// The registry v2 record writer (decision A4). A separate interface from
    /// <see cref="ISharedHostRegistryWriter"/>, whose v1 record the PR A tests implement.
    /// </summary>
    internal interface ISharedHostRecordWriter
    {
        void Write(SharedHostRecord record);
        void Delete();
    }

    /// <summary>Named mutexes of the broker (decision A3).</summary>
    internal interface ISharedHostMutexFactory
    {
        /// <summary>
        /// Takes ownership of the named mutex without waiting. Returns the held mutex, or null when another
        /// owner holds it; an abandoned mutex counts as acquired. Disposing the handle releases the mutex and
        /// may happen on any thread.
        /// </summary>
        IDisposable TryAcquire(string name);
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
            // Terminate before touching stdin: a child that stopped reading can hold
            // the writer inside WriteLine, so taking _writeGate would deadlock recovery.
            // Dispose closes stdin after termination has released that writer.
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

    /// <summary>
    /// The registry v2 record file (<c>shared-workers\v2\&lt;kbKey&gt;.json</c>): a temporary file plus
    /// <see cref="File.Replace(string, string, string)"/>, so a reader never sees a partial record.
    /// </summary>
    internal sealed class SharedHostFileRecordWriter : ISharedHostRecordWriter
    {
        private readonly string _recordPath;

        internal SharedHostFileRecordWriter(string recordPath)
        {
            if (string.IsNullOrWhiteSpace(recordPath)) throw new ArgumentException("A registry record path is required.", nameof(recordPath));
            _recordPath = recordPath;
        }

        internal string RecordPath => _recordPath;

        public void Write(SharedHostRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string directory = Path.GetDirectoryName(_recordPath);
            Directory.CreateDirectory(directory);
            string temp;
            using (var self = Process.GetCurrentProcess())
                temp = _recordPath + ".tmp-" + self.Id + "-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, record.ToJson(), new UTF8Encoding(false));
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

    /// <summary>
    /// Production <see cref="ISharedHostMutexFactory"/> backed by <see cref="Mutex"/>. A mutex is owned by the
    /// thread that acquired it, so each held mutex lives on a dedicated thread that releases it when the handle
    /// is disposed; the handle can therefore be disposed from any thread.
    /// </summary>
    internal sealed class SharedHostNamedMutexFactory : ISharedHostMutexFactory
    {
        public IDisposable TryAcquire(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A mutex name is required.", nameof(name));
            var held = new HeldMutex(name);
            return held.Acquired ? held : null;
        }

        private sealed class HeldMutex : IDisposable
        {
            private const int ThreadJoinMs = 10000;

            private readonly ManualResetEventSlim _decided = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private readonly Thread _owner;
            private Exception _error;
            private int _disposed;

            internal HeldMutex(string name)
            {
                _owner = new Thread(() => Hold(name)) { IsBackground = true, Name = "SharedHostMutexOwner" };
                _owner.Start();
                _decided.Wait();
                if (!Acquired) _owner.Join(ThreadJoinMs);
                if (_error != null) throw new InvalidOperationException("Could not open the shared host mutex '" + name + "'.", _error);
            }

            internal bool Acquired { get; private set; }

            private void Hold(string name)
            {
                Mutex mutex = null;
                bool owned = false;
                try
                {
                    mutex = new Mutex(false, name);
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    Acquired = owned;
                    _decided.Set();
                    if (owned) _release.Wait();
                }
                catch (UnauthorizedAccessException)
                {
                    // The mutex exists under another security context: another owner holds it.
                    _decided.Set();
                }
                catch (Exception ex)
                {
                    _error = ex;
                    _decided.Set();
                }
                finally
                {
                    if (owned) { try { mutex.ReleaseMutex(); } catch { } }
                    try { mutex?.Dispose(); } catch { }
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _release.Set();
                _owner.Join(ThreadJoinMs);
            }
        }
    }

    // ---- Admission seams (task 5.5; contract pinned in the header of SharedWorkerHostAdmissionTests.cs) ----

    /// <summary>One Available_Physical_Memory reading per call.</summary>
    internal interface ISharedHostMemoryReader
    {
        /// <summary>False with a non-empty <paramref name="error"/> when the reading fails.</summary>
        bool TryReadAvailableMb(out long availableMb, out string error);
    }

    /// <summary>Every registry v2 record file, each read with <see cref="RecordReadResult.ReadFile"/>, in any order.</summary>
    internal interface ISharedHostRegistryEnumerator
    {
        IReadOnlyList<RecordReadResult> ReadAll();
    }

    /// <summary>Named auto-reset events of the admission protocol (<c>Local\...</c>).</summary>
    internal interface ISharedHostSignalHub
    {
        /// <summary>Sets the event; never blocks.</summary>
        void Signal(string name);

        /// <summary>Runs <paramref name="onSignal"/> once per signal until the returned handle is disposed.</summary>
        IDisposable Subscribe(string name, Action onSignal);
    }

    /// <summary>The I/O seams of <see cref="SharedWorkerHostAdmission"/>.</summary>
    internal sealed class SharedHostAdmissionSeams
    {
        internal ISharedHostRegistryEnumerator Registry { get; set; }

        /// <summary>Liveness of other hosts' records.</summary>
        internal IProcessProbe HostProbe { get; set; }

        internal ISharedHostMemoryReader Memory { get; set; }
        internal ISharedHostSignalHub Signals { get; set; }

        /// <summary>One call is one warning log entry.</summary>
        internal Action<string> Warn { get; set; }

        /// <summary>The registry directory of the v2 record, the process probe, GlobalMemoryStatusEx and named events.</summary>
        internal static SharedHostAdmissionSeams Production(SharedWorkerHostOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return new SharedHostAdmissionSeams
            {
                Registry = new SharedHostFileRegistryEnumerator(Path.GetDirectoryName(SharedWorkerHostRuntime.ResolveRecordPathV2(options))),
                HostProbe = new ProcessIdentity(),
                Memory = new SharedHostMachineMemoryReader(),
                Signals = new SharedHostNamedEventHub(),
                Warn = message => { try { Console.Error.WriteLine("[SharedWorkerHost] " + message); } catch { } }
            };
        }

        /// <summary>
        /// Empty registry, unlimited memory, a hub that never signals and a no-op warning. A fresh instance per
        /// read, so a caller that changes a property cannot affect another runtime.
        /// </summary>
        internal static SharedHostAdmissionSeams Unrestricted => new SharedHostAdmissionSeams
        {
            Registry = EmptyRegistry.Instance,
            HostProbe = NotRunningProbe.Instance,
            Memory = UnlimitedMemory.Instance,
            Signals = SilentSignalHub.Instance,
            Warn = _ => { }
        };

        private sealed class EmptyRegistry : ISharedHostRegistryEnumerator
        {
            internal static readonly EmptyRegistry Instance = new EmptyRegistry();
            public IReadOnlyList<RecordReadResult> ReadAll() => Array.Empty<RecordReadResult>();
        }

        // Never consulted: the empty registry yields no record to probe.
        private sealed class NotRunningProbe : IProcessProbe
        {
            internal static readonly NotRunningProbe Instance = new NotRunningProbe();
            public ProcessProbeResult Probe(int pid) => ProcessProbeResult.NotRunning;
        }

        private sealed class UnlimitedMemory : ISharedHostMemoryReader
        {
            internal static readonly UnlimitedMemory Instance = new UnlimitedMemory();

            public bool TryReadAvailableMb(out long availableMb, out string error)
            {
                availableMb = int.MaxValue;
                error = null;
                return true;
            }
        }

        private sealed class SilentSignalHub : ISharedHostSignalHub
        {
            internal static readonly SilentSignalHub Instance = new SilentSignalHub();
            public void Signal(string name) { }
            public IDisposable Subscribe(string name, Action onSignal) => NoSubscription.Instance;

            private sealed class NoSubscription : IDisposable
            {
                internal static readonly NoSubscription Instance = new NoSubscription();
                public void Dispose() { }
            }
        }
    }

    /// <summary>Wraps <see cref="MachineMemory.TryGetAvailablePhysicalMb"/>.</summary>
    internal sealed class SharedHostMachineMemoryReader : ISharedHostMemoryReader
    {
        public bool TryReadAvailableMb(out long availableMb, out string error)
        {
            try
            {
                if (MachineMemory.TryGetAvailablePhysicalMb(out availableMb, out error)) return true;
                if (string.IsNullOrEmpty(error)) error = "Available physical memory could not be read.";
                return false;
            }
            catch (Exception ex)
            {
                availableMb = 0;
                error = "Available physical memory could not be read: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// The <c>*.json</c> files of the registry v2 directory. A missing directory is an empty registry; any other
    /// enumeration failure propagates, so admission never decides on a registry it could not list.
    /// </summary>
    internal sealed class SharedHostFileRegistryEnumerator : ISharedHostRegistryEnumerator
    {
        private readonly string _directory;

        internal SharedHostFileRegistryEnumerator(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A registry directory is required.", nameof(directory));
            _directory = directory;
        }

        internal string RegistryDirectory => _directory;

        public IReadOnlyList<RecordReadResult> ReadAll()
        {
            string[] files;
            try { files = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly); }
            catch (DirectoryNotFoundException) { return Array.Empty<RecordReadResult>(); }

            var reads = new List<RecordReadResult>(files.Length);
            foreach (string file in files)
            {
                // Temporary files of an in-progress write end in ".tmp-<pid>-<guid>"; only completed records count.
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                reads.Add(RecordReadResult.ReadFile(file));
            }
            return reads;
        }
    }

    /// <summary>
    /// Named auto-reset <see cref="EventWaitHandle"/>s. A subscription waits through
    /// <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, int, bool)"/>, so
    /// its callback runs on a thread-pool thread.
    /// </summary>
    internal sealed class SharedHostNamedEventHub : ISharedHostSignalHub
    {
        public void Signal(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An event name is required.", nameof(name));
            try
            {
                // Created when no subscriber exists yet; the signal is then lost with the last handle, and the
                // held host's HeldPollMs timer covers it.
                using (var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name))
                    handle.Set();
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        public IDisposable Subscribe(string name, Action onSignal)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An event name is required.", nameof(name));
            if (onSignal == null) throw new ArgumentNullException(nameof(onSignal));
            return new Subscription(new EventWaitHandle(false, EventResetMode.AutoReset, name), onSignal);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly object _gate = new object();
            private readonly EventWaitHandle _handle;
            private readonly Action _onSignal;
            private readonly RegisteredWaitHandle _registration;
            private bool _disposed;

            internal Subscription(EventWaitHandle handle, Action onSignal)
            {
                _handle = handle;
                _onSignal = onSignal;
                try
                {
                    _registration = ThreadPool.RegisterWaitForSingleObject(handle, OnSignaled, null, Timeout.Infinite, executeOnlyOnce: false);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            private void OnSignaled(object state, bool timedOut)
            {
                lock (_gate) { if (_disposed) return; }
                // A thread-pool callback must not let an exception escape: it would end the process.
                try { _onSignal(); }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine("[SharedWorkerHost] signal callback failed: " + ex.Message); } catch { }
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                }
                try { _registration.Unregister(null); } catch { }
                try { _handle.Dispose(); } catch { }
            }
        }
    }
}
