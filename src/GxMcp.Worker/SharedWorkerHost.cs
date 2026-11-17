using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using GxMcp.Worker.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker
{
    /// <summary>
    /// Broker mode for one normal Worker child. The broker owns the child stdio and
    /// exposes one bounded named-pipe attachment per Gateway. It never touches the
    /// GeneXus SDK; all SDK work remains serialized by the child Worker.
    /// </summary>
    internal static class SharedWorkerHost
    {
        internal static StreamWriter CreateChildStdinWriter(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            return new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024)
            {
                AutoFlush = false,
                NewLine = "\n"
            };
        }

        internal static bool IsRequested(string[] args)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--shared-host", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static int Run(string[] args)
        {
            try
            {
                var options = SharedWorkerHostOptions.Parse(args);
                using (var runtime = new SharedWorkerHostRuntime(options))
                    return runtime.Run();
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("[SharedWorkerHost] startup failed: " + ex); } catch { }
                return 1;
            }
        }
    }

    internal sealed class SharedWorkerHostOptions
    {
        public string IdentityKey { get; private set; }
        public string PipeName { get; private set; }
        public string KbPath { get; private set; }
        public string WorkerExecutable { get; private set; }
        public string InstallationPath { get; private set; }
        public string Driver { get; private set; }
        public string Major { get; private set; }
        public int IdleTimeoutMs { get; private set; }
        public int AttachTimeoutMs { get; private set; }

        /// <summary>Registry v2 key of the KB directory (decision A3): <c>SharedHostKbKey.Compute(--kb)</c>.</summary>
        public string KbKey { get; private set; }

        /// <summary>
        /// The launching Gateway's profile config path (optional <c>--profile-config</c>), the fifth
        /// Physical_KB_Identity component (decision D6). Empty when the Gateway did not pass it.
        /// </summary>
        public string ProfileConfigPath { get; private set; }

        /// <summary>
        /// Directory of the registry v2 record. Empty means the production location
        /// (<c>%LOCALAPPDATA%\GxMcp\shared-workers\v2</c>). Only process tests set it: on .NET
        /// Framework the known-folder lookup ignores an overridden LOCALAPPDATA variable.
        /// </summary>
        public string RegistryDirectory { get; private set; }

        /// <summary>
        /// The queue settings the launching Gateway resolved (RFC #854). The Gateway passes them
        /// as arguments, so the broker never reads them from inherited environment variables;
        /// an absent argument is the documented default.
        /// </summary>
        internal SharedHostSettingsResult Settings { get; private set; }

        /// <summary>
        /// Working-set ceiling of an idle child in MB (<c>--heap-recycle-mb</c>, the launching Gateway's
        /// <c>Server.WorkerHeapRecycleMB</c>); 0 disables the broker-owned heap recycle.
        /// </summary>
        public int HeapRecycleMb { get; private set; } = DefaultHeapRecycleMb;

        internal const int DefaultHeapRecycleMb = 1500;
        private const int MaxHeapRecycleMb = 1024 * 1024;

        internal static SharedWorkerHostOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (args ?? new string[0]).Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--shared-host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) continue;
                values[arg.Substring(2)] = args[++i];
            }

            string executable = Get(values, "worker-executable");
            if (string.IsNullOrWhiteSpace(executable))
                executable = Process.GetCurrentProcess().MainModule.FileName;
            string kb = Get(values, "kb");
            string install = Get(values, "installation");
            string driver = Get(values, "driver");
            string major = Get(values, "major");
            string key = Get(values, "identity-key");
            if (string.IsNullOrWhiteSpace(key))
            {
                if (!SharedWorkerHostProtocol.TryCreateIdentityKey(executable, kb, install, driver, major, out key, out string identityError))
                    throw new InvalidDataException(identityError);
            }
            if (!SharedWorkerHostProtocol.IsIdentityKey(key)) throw new InvalidDataException("Invalid shared Worker identity key.");
            string pipe = Get(values, "pipe-name");
            if (string.IsNullOrWhiteSpace(pipe))
            {
                if (!SharedWorkerHostProtocol.TryBuildPipeName(key, out pipe, out string pipeError))
                    throw new InvalidDataException(pipeError);
            }
            if (!SharedWorkerHostProtocol.IsValidPipeName(pipe)) throw new InvalidDataException("Invalid shared Worker pipe name.");
            if (string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(install) || string.IsNullOrWhiteSpace(driver) || string.IsNullOrWhiteSpace(major))
                throw new InvalidDataException("Shared Worker host requires --kb, --installation, --driver, and --major.");
            string registryDirectory = Get(values, "registry-dir");
            if (!string.IsNullOrWhiteSpace(registryDirectory) && !Path.IsPathRooted(registryDirectory))
                throw new InvalidDataException("--registry-dir must be an absolute path.");

            return new SharedWorkerHostOptions
            {
                IdentityKey = key,
                PipeName = pipe,
                KbPath = kb,
                WorkerExecutable = executable,
                InstallationPath = install,
                Driver = driver,
                Major = major,
                IdleTimeoutMs = ParsePositive(values, "idle-ms", 120000),
                AttachTimeoutMs = ParsePositive(values, "attach-timeout-ms", 30000),
                KbKey = SharedHostKbKey.Compute(kb),
                ProfileConfigPath = Get(values, "profile-config"),
                RegistryDirectory = registryDirectory ?? string.Empty,
                Settings = ParseSettings(values),
                HeapRecycleMb = ParseHeapRecycleMb(values)
            };
        }

        // The values go through the same resolver and ranges as the Gateway's configuration,
        // with no environment lookup: a value the Gateway did not pass is the default.
        private static SharedHostSettingsResult ParseSettings(IDictionary<string, string> values)
        {
            var section = new JObject();
            AddSetting(values, "queue-wait-ms", SharedHostSettingsResolver.QueueWaitMsKey, section);
            AddSetting(values, "queue-depth", SharedHostSettingsResolver.QueueDepthKey, section);
            AddSetting(values, "session-queue-cap", SharedHostSettingsResolver.SessionQueueCapKey, section);
            AddSetting(values, "max-workers", SharedHostSettingsResolver.MaxWorkersKey, section);
            AddSetting(values, "memory-floor-mb", SharedHostSettingsResolver.MemoryFloorMbKey, section);
            AddSetting(values, "cold-start-concurrency", SharedHostSettingsResolver.ColdStartConcurrencyKey, section);
            // The Gateway passes the attach readiness wait in milliseconds; the setting is in whole seconds.
            if (values.TryGetValue("attach-ready-timeout-ms", out string readyRaw))
            {
                int readyMs = ParseInteger("attach-ready-timeout-ms", readyRaw);
                if (readyMs % 1000 != 0)
                    throw new InvalidDataException("--attach-ready-timeout-ms must be a whole number of seconds, got '" + readyRaw + "'.");
                section[SharedHostSettingsResolver.AttachReadyTimeoutSecondsKey] = readyMs / 1000;
            }
            var settings = SharedHostSettingsResolver.Resolve(section, false, string.Empty, _ => null);
            if (!settings.IsValid) throw new InvalidDataException(string.Join(" ", settings.Errors));
            return settings;
        }

        private static void AddSetting(IDictionary<string, string> values, string argument, string key, JObject section)
        {
            if (!values.TryGetValue(argument, out string raw)) return;
            section[key] = ParseInteger(argument, raw);
        }

        private static int ParseInteger(string argument, string raw)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                throw new InvalidDataException("--" + argument + " must be an integer, got '" + raw + "'.");
            return parsed;
        }

        // Server.WorkerHeapRecycleMB: 0 disables; the Gateway clamps negative values to 0 before passing them.
        private static int ParseHeapRecycleMb(IDictionary<string, string> values)
        {
            if (!values.TryGetValue("heap-recycle-mb", out string raw)) return DefaultHeapRecycleMb;
            int parsed = ParseInteger("heap-recycle-mb", raw);
            if (parsed < 0 || parsed > MaxHeapRecycleMb)
                throw new InvalidDataException("--heap-recycle-mb must be from 0 to "
                    + MaxHeapRecycleMb.ToString(CultureInfo.InvariantCulture) + ", got '" + raw + "'.");
            return parsed;
        }

        private static string Get(IDictionary<string, string> values, string key)
            => values.TryGetValue(key, out string value) ? value : string.Empty;

        private static int ParsePositive(IDictionary<string, string> values, string key, int fallback)
        {
            if (values.TryGetValue(key, out string value) && int.TryParse(value, out int parsed) && parsed > 0 && parsed <= 86400000)
                return parsed;
            return fallback;
        }
    }

    /// <summary>
    /// The shared-host broker (RFC #854). Every JSON-RPC request is classified on arrival:
    /// thread-safe and control commands bypass the queue and go straight to the child inbox,
    /// every other request is an SDK_Operation and goes through <see cref="SharedHostScheduler"/>,
    /// which releases one at a time. All runtime state is guarded by <c>_hostLock</c>; lines to
    /// Attachments are collected under the lock and sent after it is released.
    /// </summary>
    internal sealed class SharedWorkerHostRuntime : IDisposable
    {
        // Bound of the child inbox. The scheduler bounds SDK_Operations to one released request,
        // so this bound is for bypass commands and forwarded cancellations; a full inbox answers
        // SHARED_WORKER_BACKPRESSURE instead of blocking the caller.
        internal const int ChildInboxCapacity = 256;

        /// <summary>Exit code of <see cref="Run"/> when another host already holds the host-lifetime mutex.</summary>
        internal const int HostMutexHeldExitCode = 3;

        /// <summary>Host-lifetime mutex prefix (decision A3); the kbKey completes the name.</summary>
        internal const string HostMutexPrefix = @"Local\GxMcp.SharedWorker.v2.Host.";

        private const int SupervisorTickMs = 5000;

        // How long a replacement waits for the operating system to report the previous child's exit.
        private const int ChildExitConfirmMs = 5000;
        private const int RespawnBackoffMs = 250;
        private const int IdleRecordDebounceMs = 1000;
        private const int RespawnWindowMs = 60000;
        private const int MaxRespawnsPerWindow = 3;
        private const int ChildReaderDrainMs = 2000;

        // A build_active or index_active notification newer than this keeps the child busy for eviction and heap
        // recycle; the child repeats it every 20 seconds while the work runs (same window as the Gateway's guard).
        private const int BackgroundActivityWindowMs = 90000;

        private readonly SharedWorkerHostOptions _options;
        private readonly SharedHostSettingsResult _settings;
        private readonly SchedulerLimits _limits;
        private readonly SharedHostScheduler _scheduler;
        private readonly ISharedHostClock _clock;
        private readonly ISharedHostChildLauncher _childLauncher;
        private readonly ISharedHostRecordWriter _recordWriter;
        private readonly ISharedHostMutexFactory _mutexFactory;
        private readonly IProcessProbe _gatewayProbe;
        private readonly bool _runBackgroundThreads;
        private readonly ISharedHostTimer _expiryTimer;
        private readonly ISharedHostTimer _idleRecordTimer;
        private readonly ISharedHostTimer _supervisorTimer;

        // Admission (design "SharedWorkerHostAdmission.cs", child state machine). Every child start, including a
        // crash respawn and a heap-recycle replacement, takes its own Admit under _hostLock.
        private readonly SharedWorkerHostAdmission _admission;
        private readonly SharedHostAdmissionSeams _admissionSeams;
        private readonly ISharedHostTimer _admissionTimer;
        private readonly AutoResetEvent _childLaunched = new AutoResetEvent(false);
        private IDisposable _wakeSubscription;
        private IDisposable _evictSubscription;

        // A start is wanted and no child exists yet: admitting, held, evicting or contended.
        private bool _startPending;
        private StartKind _startKind;
        private long _startRequestedAtMs;
        // When the pending start was first held (or first waited for an eviction); the hold expiry counts from it.
        private long? _heldAtMs;
        private int? _heldPosition;
        // The admission frame a newly attached Attachment receives while no child exists (held, evicting, refused).
        private JObject _pendingAdmissionFrame;
        // The current child was launched and has not reported sdk_ready, failed, or exited yet.
        private bool _coldStartActive;
        private bool _anyChildLaunched;
        private string _respawnDiagnostic = string.Empty;
        // Idle start, part two: the last request completion (ISharedHostClock milliseconds).
        private long _lastCompletionMs = long.MinValue;
        private DateTime? _lastActivityUtc;

        // Gateway process identity from each Attachment's attach frame, keyed by connection id.
        private readonly Dictionary<string, GatewayProcess> _gateways = new Dictionary<string, GatewayProcess>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _hostLock = new object();
        private readonly BlockingCollection<ChildInboxItem> _childInbox = new BlockingCollection<ChildInboxItem>(ChildInboxCapacity);
        private readonly ConcurrentDictionary<string, ISharedHostAttachment> _attachments = new ConcurrentDictionary<string, ISharedHostAttachment>(StringComparer.Ordinal);

        // Requests written (or about to be written) to the child: route, progress owner and
        // what the exit and metadata paths need. Queued SDK_Operations live in _queued only.
        private readonly ConcurrentDictionary<string, SharedWorkerRequestRoute> _routes = new ConcurrentDictionary<string, SharedWorkerRequestRoute>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SharedWorkerRequestRoute> _progressOwners = new ConcurrentDictionary<string, SharedWorkerRequestRoute>(StringComparer.Ordinal);
        private readonly Dictionary<string, RouteInfo> _routeInfo = new Dictionary<string, RouteInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, QueuedEntry> _queued = new Dictionary<string, QueuedEntry>(StringComparer.Ordinal);
        private readonly Queue<long> _respawnHistory = new Queue<long>();
        private readonly int _hostPid;
        private readonly long _hostStartTimeUtcTicks;
        private ISharedHostChild _child;
        private Thread _childReader;
        private Thread _childErrorReader;
        private Thread _childInboxWriter;
        private Thread _acceptThread;
        private string _lastChildError = string.Empty;
        private string _lastChildFailureDiagnostic = string.Empty;
        // Idle start (ISharedHostClock milliseconds): the last detachment, or Start when nothing detached yet.
        private long _lastDetachMs;

        // Last child background-activity notification (ISharedHostClock milliseconds); long.MinValue is none.
        private long _lastBackgroundActivityMs = long.MinValue;
        private IDisposable _hostMutex;
        private long _generation = 1;
        private long _sequence;
        private long? _expiryArmedAtMs;
        private bool _idleRecordArmed;
        private bool _recordWritten;
        private double _serviceMsEstimate;
        private int _fatal;

        // Issue #335: broker-owned recycle election and the generation each attachment
        // observes. Recycle decisions belong here - the broker owns the child - so that
        // two Gateways watching the same Worker cannot each decide to kill it. The
        // election flag is cleared when the new child starts, which is what makes exactly
        // one attachment act per sweep.
        private long _brokerGeneration = 1;
        private int _recycleElected;
        private bool _sdkReady;
        private SharedHostRecord _record;
        private int _disposed;

        // Issue #362. The child's SDK-lane snapshot, captured by the broker from the child
        // rather than read out of this process.
        //
        // The broker is not the Worker: it starts a separate child process and relays
        // frames to it. `Program.GetSdkBusyStatus()` reads process statics, so calling it
        // here described the broker's own (always empty, always idle) scheduler while
        // reporting it next to the child's PID. Every attachment therefore saw
        // sdkBusy=false and a zero queue depth even with the child deadlocked.
        private readonly object _childStatusGate = new object();
        private JObject _childStatus;
        private DateTime _childStatusCapturedAtUtc;

        /// <summary>
        /// How old a child snapshot may be before the acknowledgement says it has no
        /// fresh evidence rather than passing the stale figures off as current.
        /// </summary>
        internal static readonly TimeSpan ChildStatusMaxAge = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Supplies the child's SDK-lane status. The production implementation asks the
        /// child; the seam exists so a guard can drive the decision logic - and the recycle
        /// election that depends on it - without a wedged or real SDK.
        /// </summary>
        internal Func<JObject> ChildStatusProvider { get; set; }

        /// <summary>
        /// Managed heap over which the heartbeat recycle election is raised, in bytes. It acts only on a child status
        /// snapshot that reports <c>childManagedHeapBytes</c>, which the child does not send today; the broker's own
        /// heap recycle (task 5.6) uses the child's working set against <see cref="SharedWorkerHostOptions.HeapRecycleMb"/>
        /// instead and never waits for a Gateway to act on the election.
        /// </summary>
        internal long ChildManagedHeapBudgetBytes = 512L * 1024 * 1024;

        /// <summary>How many heartbeats have been served, for the no-collect guard.</summary>
        internal long HeartbeatsServed => Interlocked.Read(ref _heartbeatsServed);

        private long _heartbeatsServed;

        /// <summary>Records the child's SDK-lane status and its capture time. Anything that observes
        /// it later can therefore judge how old it is.
        /// </summary>
        internal void RecordChildStatus(JObject status)
        {
            lock (_childStatusGate)
            {
                _childStatus = status;
                _childStatusCapturedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// The cached child snapshot, or null with a reason. Never idle defaults: an
        /// absent or stale snapshot is reported as unavailable so a caller cannot mistake
        /// "we did not look" for "nothing is happening".
        /// </summary>
        private bool TryGetFreshChildStatus(out JObject status, out string unavailableReason)
        {
            lock (_childStatusGate)
            {
                status = _childStatus;
                if (status == null)
                {
                    unavailableReason = "No child status has been captured yet.";
                    return false;
                }
                double ageMs = (DateTime.UtcNow - _childStatusCapturedAtUtc).TotalMilliseconds;
                if (ageMs > ChildStatusMaxAge.TotalMilliseconds)
                {
                    unavailableReason = "The last child status is " + (int)ageMs + "ms old (limit "
                        + (int)ChildStatusMaxAge.TotalMilliseconds + "ms).";
                    return false;
                }
            }
            unavailableReason = null;
            return true;
        }

        /// <summary>
        // ---- Issue #362 test seams: the decision logic is exercised without a wedged SDK. ----

        /// <summary>The election decision, as a heartbeat would reach it.</summary>
        internal bool EvaluateRecycleElectionForTest()
        {
            if (!TryGetFreshChildStatus(out var status, out _)) return false;
            return ShouldElectRecycle(status, 0);
        }

        /// <summary>The snapshot freshness decision, so "unavailable" is assertable.</summary>
        internal bool TryGetFreshChildStatusForTest(out JObject status, out string reason)
            => TryGetFreshChildStatus(out status, out reason);

        /// <summary>
        /// Issue #362: the recycle election, which could never be raised before.
        /// <c>_recycleElected</c> was only ever written with 0, and the Gateway acts on a
        /// shared Worker solely when the acknowledgement reports <c>recycleElected</c>, so
        /// heap-pressure and stall recovery for shared Workers could not happen at all.
        ///
        /// <para>
        /// The policy is evaluated here, in the broker, because the broker owns the child.
        /// CompareExchange makes it single-shot per generation, so exactly one attachment
        /// ever sees it true. Progressing work and a non-empty queue block recovery: a
        /// legitimate long build must not be recycled.
        /// </para>
        /// </summary>
        private bool ShouldElectRecycle(JObject status, long privateBytes)
        {
            // Optimization, not the guarantee: the CompareExchange below is what makes the
            // election single-shot. This just avoids re-deriving the policy on every
            // heartbeat once it has already been raised for this generation.
            if (Volatile.Read(ref _recycleElected) != 0) return false;

            string sdkState = status["sdkState"]?.ToString();
            long queueTotal = status["queueTotal"]?.ToObject<long?>() ?? 0;
            if (queueTotal > 0) return false;
            if (string.Equals(sdkState, "busy-progressing", StringComparison.Ordinal)) return false;

            long managedHeap = status["childManagedHeapBytes"]?.ToObject<long?>() ?? 0;
            bool idleOverHeap = string.Equals(sdkState, "idle", StringComparison.Ordinal)
                && managedHeap > ChildManagedHeapBudgetBytes;
            bool stalled = string.Equals(sdkState, "busy-stalled", StringComparison.Ordinal)
                || string.Equals(sdkState, "busy-stalled-unproven", StringComparison.Ordinal);

            if (!idleOverHeap && !stalled) return false;

            // The write is the election. Whoever loses the exchange does not re-run the
            // sweep, so the flag is raised once per generation however many attachments
            // heartbeat concurrently.
            return Interlocked.CompareExchange(ref _recycleElected, 1, 0) == 0;
        }

        internal SharedWorkerHostRuntime(SharedWorkerHostOptions options)
            : this(
                options,
                options?.Settings ?? DefaultSettings(),
                SharedHostSystemClock.Instance,
                new SharedHostProcessLauncher(),
                new SharedHostFileRecordWriter(ResolveRecordPathV2(options)),
                new SharedHostNamedMutexFactory(),
                new ProcessIdentity(),
                SharedHostAdmissionSeams.Production(options),
                runBackgroundThreads: true)
        {
        }

        /// <summary>
        /// The task 3.5 test constructor. Each v2 record write is projected onto
        /// <paramref name="registryWriter"/> as a v1 record; the host-lifetime mutex is always granted and
        /// every Gateway is reported running with its recorded start time.
        /// </summary>
        internal SharedWorkerHostRuntime(
            SharedWorkerHostOptions options,
            SharedHostSettingsResult settings,
            ISharedHostClock clock,
            ISharedHostChildLauncher childLauncher,
            ISharedHostRegistryWriter registryWriter,
            bool runBackgroundThreads)
            : this(
                options,
                settings,
                clock,
                childLauncher,
                new RegistryWriterProjection(registryWriter ?? throw new ArgumentNullException(nameof(registryWriter))),
                GrantingMutexFactory.Instance,
                null,
                runBackgroundThreads)
        {
        }

        /// <summary>
        /// The ownership constructor (task 4.12). No constructor acquires a mutex or writes a record;
        /// <see cref="Start"/> does. A null <paramref name="gatewayProbe"/> reports every Gateway as running
        /// with the start time of its attach frame. Admission uses <see cref="SharedHostAdmissionSeams.Unrestricted"/>
        /// and an always-granted admission mutex, so these tests observe only the mutexes they were written against.
        /// </summary>
        internal SharedWorkerHostRuntime(
            SharedWorkerHostOptions options,
            SharedHostSettingsResult settings,
            ISharedHostClock clock,
            ISharedHostChildLauncher childLauncher,
            ISharedHostRecordWriter recordWriter,
            ISharedHostMutexFactory mutexFactory,
            IProcessProbe gatewayProbe,
            bool runBackgroundThreads)
            : this(options, settings, clock, childLauncher, recordWriter, mutexFactory, gatewayProbe,
                SharedHostAdmissionSeams.Unrestricted, GrantingMutexFactory.Instance, runBackgroundThreads)
        {
        }

        /// <summary>
        /// The admission constructor (task 5.6): <paramref name="admissionSeams"/> supply the registry, host probe,
        /// memory reader, named events and warning log of <see cref="SharedWorkerHostAdmission"/>, and the admission
        /// mutex comes from <paramref name="mutexFactory"/>.
        /// </summary>
        internal SharedWorkerHostRuntime(
            SharedWorkerHostOptions options,
            SharedHostSettingsResult settings,
            ISharedHostClock clock,
            ISharedHostChildLauncher childLauncher,
            ISharedHostRecordWriter recordWriter,
            ISharedHostMutexFactory mutexFactory,
            IProcessProbe gatewayProbe,
            SharedHostAdmissionSeams admissionSeams,
            bool runBackgroundThreads)
            : this(options, settings, clock, childLauncher, recordWriter, mutexFactory, gatewayProbe,
                admissionSeams, mutexFactory, runBackgroundThreads)
        {
        }

        private SharedWorkerHostRuntime(
            SharedWorkerHostOptions options,
            SharedHostSettingsResult settings,
            ISharedHostClock clock,
            ISharedHostChildLauncher childLauncher,
            ISharedHostRecordWriter recordWriter,
            ISharedHostMutexFactory mutexFactory,
            IProcessProbe gatewayProbe,
            SharedHostAdmissionSeams admissionSeams,
            ISharedHostMutexFactory admissionMutexFactory,
            bool runBackgroundThreads)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _childLauncher = childLauncher ?? throw new ArgumentNullException(nameof(childLauncher));
            _recordWriter = recordWriter ?? throw new ArgumentNullException(nameof(recordWriter));
            _mutexFactory = mutexFactory ?? throw new ArgumentNullException(nameof(mutexFactory));
            _gatewayProbe = gatewayProbe;
            _runBackgroundThreads = runBackgroundThreads;
            _admissionSeams = admissionSeams ?? throw new ArgumentNullException(nameof(admissionSeams));
            _admission = new SharedWorkerHostAdmission(settings, clock, admissionMutexFactory ?? mutexFactory, recordWriter, admissionSeams);
            _limits = new SchedulerLimits(settings.QueueDepth, settings.SessionQueueCap, settings.QueueWaitMs);
            _scheduler = new SharedHostScheduler(_limits, () => _clock.NowMs);
            // Nothing is released until the child reports sdk_ready.
            _scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);
            _expiryTimer = _clock.CreateTimer(OnExpiryTimer);
            _idleRecordTimer = _clock.CreateTimer(OnIdleRecordTimer);
            _supervisorTimer = _clock.CreateTimer(OnSupervisorTick);
            _admissionTimer = _clock.CreateTimer(OnAdmissionTimer);
            _lastDetachMs = _clock.NowMs;
            using (var self = Process.GetCurrentProcess())
            {
                _hostPid = self.Id;
                _hostStartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks;
            }
        }

        private static SharedHostSettingsResult DefaultSettings()
            => SharedHostSettingsResolver.Resolve(null, false, string.Empty, _ => null);

        /// <summary>
        /// The registry v2 record path (decision A4): <c>&lt;kbKey&gt;.json</c> under <c>--registry-dir</c> when set,
        /// else under <c>%LOCALAPPDATA%\GxMcp\shared-workers\v2</c>.
        /// </summary>
        internal static string ResolveRecordPathV2(SharedWorkerHostOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string directory = string.IsNullOrWhiteSpace(options.RegistryDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GxMcp", "shared-workers", "v2")
                : options.RegistryDirectory;
            return Path.Combine(directory, options.KbKey + ".json");
        }

        /// <summary>True once Dispose ran, the host stopped fail-closed, or the supervisor tick reaped an idle host.</summary>
        internal bool IsStopRequested => _stop.IsCancellationRequested;

        // ---- Lifecycle ----

        /// <summary>
        /// Design "Startup": (1) take the host-lifetime mutex, or return false with no other effect;
        /// (2) write the record with state "admitting" before the child exists; (3) subscribe to this KB's wake
        /// and evict events; (4) with background threads, start the child inbox writer and the pipe accept loop;
        /// (5) run admission for the first start (record state "starting", "held", "refused" or "admitting");
        /// (6) arm the supervisor tick.
        /// </summary>
        internal bool Start()
        {
            IDisposable mutex = _mutexFactory.TryAcquire(HostMutexPrefix + _options.KbKey);
            if (mutex == null)
            {
                HostLog("another shared Worker host owns this KB (host-lifetime mutex held); exiting.");
                return false;
            }
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    try { mutex.Dispose(); } catch { }
                    return false;
                }
                _hostMutex = mutex;
                _lastDetachMs = _clock.NowMs;
                _record = NewAdmittingRecord();
                WriteRecordLocked();
            }
            try
            {
                _wakeSubscription = _admissionSeams.Signals.Subscribe(
                    SharedWorkerHostAdmission.WakeEventName(_options.KbKey), OnWakeSignal);
                _evictSubscription = _admissionSeams.Signals.Subscribe(
                    SharedWorkerHostAdmission.EvictEventName(_options.KbKey), OnEvictSignal);
            }
            catch (Exception ex)
            {
                // The HeldPollMs timer still re-runs a held admission; a missed eviction only delays another host.
                HostLog("could not subscribe to the admission events: " + ex.Message);
            }
            if (_runBackgroundThreads)
            {
                _childInboxWriter = new Thread(ChildInboxWriterLoop) { IsBackground = true, Name = "SharedWorkerChildWriter" };
                _childInboxWriter.Start();
                StartPipeAcceptLoop();
            }
            var outbox = new Outbox();
            lock (_hostLock)
            {
                BeginStartLocked(StartKind.Initial, outbox);
                _supervisorTimer.Change(_clock.NowMs + SupervisorTickMs);
            }
            Flush(outbox);
            return true;
        }

        private SharedHostRecord NewAdmittingRecord()
        {
            string profile = Normalize(_options.ProfileConfigPath);
            return new SharedHostRecord
            {
                Schema = SharedHostRecord.CurrentSchema,
                KbKey = _options.KbKey,
                Identity = new SharedHostRecordIdentity
                {
                    Key = _options.IdentityKey,
                    KbPath = SharedHostKbKey.NormalizeKbPath(_options.KbPath),
                    WorkerExecutable = Normalize(_options.WorkerExecutable),
                    InstallationPath = Normalize(_options.InstallationPath),
                    Driver = NormalizeToken(_options.Driver),
                    Major = NormalizeToken(_options.Major),
                    ProfileConfigPath = profile.Length == 0 ? null : profile
                },
                HostPid = _hostPid,
                HostStartTimeUtcTicks = _hostStartTimeUtcTicks,
                WorkerPid = 0,
                WorkerStartTimeUtcTicks = 0,
                PipeName = _options.PipeName,
                Generation = (int)_generation,
                State = SharedHostRecordState.Admitting
            };
        }

        // The host's own normalized Physical_KB_Identity components, keyed like an attach frame's "identity".
        private Dictionary<string, string> LiveIdentityComponents()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["installationPath"] = Normalize(_options.InstallationPath),
                ["driver"] = NormalizeToken(_options.Driver),
                ["major"] = NormalizeToken(_options.Major),
                ["workerExecutable"] = Normalize(_options.WorkerExecutable),
                ["profileConfigPath"] = Normalize(_options.ProfileConfigPath)
            };
        }

        /// <summary>
        /// The supervisor: waits on the child exit handle and the stop handle. Idle reaping and Gateway
        /// liveness run on the supervisor tick, which ends this loop through <see cref="IsStopRequested"/>.
        /// Returns <see cref="HostMutexHeldExitCode"/> at once when another host owns the KB.
        /// </summary>
        internal int Run()
        {
            if (!Start()) return HostMutexHeldExitCode;
            WaitHandle stopHandle = _stop.Token.WaitHandle;
            while (!_stop.IsCancellationRequested)
            {
                ISharedHostChild child;
                lock (_hostLock) child = _child;
                if (child == null)
                {
                    if (Volatile.Read(ref _fatal) != 0) break;
                    // No child (admitting, held, refused or no-worker): wake when one is launched.
                    WaitHandle.WaitAny(new WaitHandle[] { stopHandle, _childLaunched }, SupervisorTickMs);
                    continue;
                }
                int signaled = WaitHandle.WaitAny(new[] { stopHandle, child.ExitedHandle }, SupervisorTickMs);
                if (signaled == 0) break;
                if (signaled == 1)
                {
                    OnChildExited(child);
                    if (Volatile.Read(ref _fatal) != 0) break;
                    continue;
                }
            }
            return 0;
        }

        // ---- Supervisor tick (design "Detach and pipe EOF", "Idle reaping"; Requirement 7 criterion 5) ----

        /// <summary>
        /// Every <see cref="SupervisorTickMs"/>: detach each Attachment whose Gateway PID is gone or now
        /// belongs to a process with another start time, then apply the idle rule. A Gateway that dies just
        /// after a tick is detached by the next one, within 10 seconds.
        /// </summary>
        private void OnSupervisorTick()
        {
            if (Volatile.Read(ref _disposed) != 0 || _stop.IsCancellationRequested) return;

            List<KeyValuePair<ISharedHostAttachment, GatewayProcess>> watched;
            lock (_hostLock)
            {
                watched = new List<KeyValuePair<ISharedHostAttachment, GatewayProcess>>();
                foreach (var attachment in _attachments.Values)
                {
                    if (_gateways.TryGetValue(attachment.ConnectionId, out GatewayProcess gateway))
                        watched.Add(new KeyValuePair<ISharedHostAttachment, GatewayProcess>(attachment, gateway));
                }
            }

            // The probe reads process state; it runs outside the host lock.
            foreach (var entry in watched)
            {
                if (!IsGatewayGone(entry.Value)) continue;
                HostLog("Gateway pid " + entry.Value.Pid.ToString(CultureInfo.InvariantCulture)
                    + " is no longer running; detaching its Attachment.");
                Detach(entry.Key);
                try { entry.Key.Stop(); } catch { }
            }

            if (IsIdleExpired())
            {
                HostLog("idle timeout of " + _options.IdleTimeoutMs.ToString(CultureInfo.InvariantCulture)
                    + " ms elapsed since the last detachment or request completion; stopping.");
                try { _stop.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }

            TryHeapRecycle();

            if (Volatile.Read(ref _disposed) == 0 && !_stop.IsCancellationRequested)
                _supervisorTimer.Change(_clock.NowMs + SupervisorTickMs);
        }

        // ---- Heap recycle (Requirement 5 criterion 6; design "Heap recycle") ----

        /// <summary>
        /// Supervisor tick: an idle, ready child whose working set exceeds <see cref="SharedWorkerHostOptions.HeapRecycleMb"/>
        /// is stopped, its exit is confirmed by the operating system, and the replacement takes its own Admit.
        /// Attachments stay connected with their attachId; requests that arrive meanwhile wait in the queue.
        /// </summary>
        private void TryHeapRecycle()
        {
            long limitMb = _options.HeapRecycleMb;
            if (limitMb <= 0) return;

            ISharedHostChild child;
            lock (_hostLock)
            {
                if (!IsChildIdleLocked()) return;
                child = _child;
            }

            long workingSet;
            try { workingSet = child.HasExited ? 0 : child.WorkingSetBytes; }
            catch (Exception ex)
            {
                HostLog("child working set could not be read: " + ex.Message);
                return;
            }
            if (workingSet <= limitMb * 1024L * 1024L) return;

            lock (_hostLock)
            {
                // Re-checked under the lock: a request that arrived after the reading wins over the recycle.
                if (!ReferenceEquals(child, _child) || !IsChildIdleLocked()) return;
                HostLog("child pid " + child.Pid.ToString(CultureInfo.InvariantCulture) + " working set "
                    + (workingSet / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB exceeds --heap-recycle-mb "
                    + limitMb.ToString(CultureInfo.InvariantCulture) + "; replacing it.");
                DetachChildLocked(SharedHostRecordState.Replacing);
            }

            StopDetachedChild(child, "heap recycle", StartKind.Replacement);
        }

        // Ready, no request queued, running or in flight, no start pending, and no recent background activity
        // (Requirement 5 criterion 5: the same rule makes the child evictable).
        private bool IsChildIdleLocked()
        {
            if (_child == null || !_sdkReady || _startPending || Volatile.Read(ref _fatal) != 0) return false;
            if (_queued.Count > 0 || !_routes.IsEmpty || !_scheduler.Snapshot().IsIdle) return false;
            return !IsBackgroundActive();
        }

        private bool IsBackgroundActive()
        {
            long lastBackground = Interlocked.Read(ref _lastBackgroundActivityMs);
            return lastBackground != long.MinValue && _clock.NowMs - lastBackground < BackgroundActivityWindowMs;
        }

        // The child stops being current: nothing more is released to it, and its exit is not a crash.
        private void DetachChildLocked(string state)
        {
            _child = null;
            _sdkReady = false;
            _coldStartActive = false;
            _scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);
            if (_record != null)
            {
                _record.WorkerPid = 0;
                _record.WorkerStartTimeUtcTicks = 0;
                _record.State = state;
            }
            WriteRecordLocked();
        }

        /// <summary>
        /// Kills a child that <see cref="DetachChildLocked"/> took out of service and waits for the operating system to
        /// report its exit. A replacement (<paramref name="next"/> not null) starts only after that confirmation;
        /// a child that does not exit stops the host fail-closed instead (task 4.12).
        /// </summary>
        private void StopDetachedChild(ISharedHostChild child, string why, StartKind? next)
        {
            try { child.Kill(); } catch (Exception ex) { HostLog(why + ": kill failed: " + ex.Message); }
            bool exited = ConfirmChildExited(child);
            try { child.Dispose(); } catch { }

            var outbox = new Outbox();
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (!exited)
                {
                    FailClosedLocked("Shared Worker child pid " + child.Pid.ToString(CultureInfo.InvariantCulture)
                        + " did not exit after " + why + "; no replacement was started and the host stopped fail-closed.", outbox);
                }
                else if (next.HasValue && !_stop.IsCancellationRequested)
                {
                    BeginStartLocked(next.Value, outbox);
                }
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
        }

        // ---- Admission (design "Child state machine", Requirements 5 and 6) ----

        private enum StartKind
        {
            // The first start at host launch.
            Initial,
            // An SDK_Operation arrived while no child exists (after a refusal, an eviction, a dropped or expired hold,
            // or a failed replacement).
            Request,
            // A heap-recycle replacement.
            Replacement,
            // The respawn after a child exit; a launch failure stops the host fail-closed, as before admission.
            Respawn
        }

        /// <summary>Requests a child start and runs the first admission attempt for it.</summary>
        private void BeginStartLocked(StartKind kind, Outbox outbox)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _fatal) != 0 || _stop.IsCancellationRequested) return;
            if (_child != null || _startPending) return;
            _startPending = true;
            _startKind = kind;
            _startRequestedAtMs = _clock.NowMs;
            _heldAtMs = null;
            _heldPosition = null;
            if (_record != null)
            {
                // A new start: no FIFO position and no refusal carry over from an earlier one.
                _record.HeldSinceUtc = null;
                _record.Refusal = null;
                _record.WorkerPid = 0;
                _record.WorkerStartTimeUtcTicks = 0;
            }
            RunAdmissionLocked(outbox);
        }

        private void OnAdmissionTimer()
        {
            var outbox = new Outbox();
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                RunAdmissionLocked(outbox);
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
        }

        // A Cold_Start ended somewhere: a held start re-runs admission at once. A signal that finds no pending
        // start (a dropped or expired hold) starts nothing.
        private void OnWakeSignal()
        {
            var outbox = new Outbox();
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_startPending) return;
                RunAdmissionLocked(outbox);
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
        }

        /// <summary>One admission attempt for the pending start, and the transition its outcome selects.</summary>
        private void RunAdmissionLocked(Outbox outbox)
        {
            if (!_startPending || _child != null || Volatile.Read(ref _disposed) != 0 || _stop.IsCancellationRequested) return;
            _admissionTimer.Change(null);
            long now = _clock.NowMs;

            if (_heldAtMs.HasValue && now - _heldAtMs.Value >= AttachReadyWaitMs)
            {
                ExpireHoldLocked(outbox);
                return;
            }

            SharedHostAdmissionResult result;
            try
            {
                RefreshRecordLocked();
                result = _admission.Admit(_record);
            }
            catch (Exception ex)
            {
                HostLog("admission attempt failed: " + ex.Message + "; retrying.");
                _admissionTimer.Change(now + SharedWorkerHostAdmission.HeldPollMs);
                return;
            }

            switch (result.Outcome)
            {
                case SharedHostAdmissionOutcome.Contended:
                    _admissionTimer.Change(now + SharedWorkerHostAdmission.AdmissionRetryMs);
                    return;

                case SharedHostAdmissionOutcome.Hold:
                {
                    _scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);
                    if (!_heldAtMs.HasValue) _heldAtMs = now;
                    int position = result.Decision?.Position ?? 1;
                    if (_heldPosition != position || _pendingAdmissionFrame == null
                        || !string.Equals((string)_pendingAdmissionFrame["state"], "held", StringComparison.Ordinal))
                    {
                        _heldPosition = position;
                        BroadcastAdmissionLocked(AdmissionFrameData("held", position, null), outbox, keepForNewAttachments: true);
                    }
                    _admissionTimer.Change(now + SharedWorkerHostAdmission.HeldPollMs);
                    return;
                }

                case SharedHostAdmissionOutcome.Evict:
                    // The victim stops its Worker; the next attempt sees the freed slot.
                    _scheduler.SetWorkerAvailable(false, WaitReason.WorkerLimit);
                    if (!_heldAtMs.HasValue) _heldAtMs = now;
                    if (_pendingAdmissionFrame == null
                        || !string.Equals((string)_pendingAdmissionFrame["state"], "evicting", StringComparison.Ordinal))
                        BroadcastAdmissionLocked(AdmissionFrameData("evicting", null, null), outbox, keepForNewAttachments: true);
                    _admissionTimer.Change(now + SharedWorkerHostAdmission.HeldPollMs);
                    return;

                case SharedHostAdmissionOutcome.Refuse:
                    RefuseStartLocked(result.Decision, outbox);
                    return;

                default:
                    LaunchChildLocked(outbox);
                    return;
            }
        }

        private int AttachReadyWaitMs => (int)Math.Min(int.MaxValue, (long)_settings.AttachReadyTimeoutSeconds * 1000L);

        // Refuse: no child, the record says "refused" (Admit wrote it), every waiting request gets the refusal as a
        // tool-result error, and the Attachments stay connected in NoWorker. The next SDK_Operation runs Admit again.
        private void RefuseStartLocked(AdmissionDecision decision, Outbox outbox)
        {
            EndPendingStartLocked();
            _scheduler.SetWorkerAvailable(false, WaitReason.WorkerLimit);
            string code = decision?.Code ?? ConcurrencyErrorCatalog.KbPoolFull;
            string reason = decision?.Reason ?? AdmissionDecision.WorkerLimitReason;
            JObject details = decision?.Details ?? new JObject();
            HostLog("admission refused the Worker start: " + code + " reason=" + reason);

            var refusal = new JObject { ["code"] = code, ["reason"] = reason };
            foreach (var property in details.Properties()) refusal[property.Name] = property.Value.DeepClone();
            BroadcastAdmissionLocked(AdmissionFrameData("refused", null, refusal), outbox, keepForNewAttachments: true);

            FailWaitingRequestsLocked(outbox, entry => BuildToolError(entry.Route.ClientRequestId, code, reason, details,
                _limits.QueueWaitMs, null));
        }

        // Held start expiry (Requirement 6 criterion 6): no child, record "no-worker", and each waiting request gets
        // WORKER_STARTUP_FAILED cold-start-wait-expired with retryAfterMs from 1000 to the wait.
        private void ExpireHoldLocked(Outbox outbox)
        {
            int waitMs = AttachReadyWaitMs;
            HostLog("the held Worker start was not released within " + waitMs.ToString(CultureInfo.InvariantCulture)
                + " ms; dropping it.");
            FailWaitingRequestsLocked(outbox, entry => BuildToolError(entry.Route.ClientRequestId,
                ConcurrencyErrorCatalog.WorkerStartupFailed, "cold-start-wait-expired", null, waitMs, SharedWorkerHostProtocol.MinBusyWaitMs));
            DropPendingStartLocked();
        }

        // Requirement 6 criterion 13: a cancellation or detach removed the last request waiting for a held start.
        // Attachments alone are not waiters; a hold without any waiter from the start (host launch) is kept.
        private void DropHoldIfNoWaitersLocked(int removed)
        {
            if (removed <= 0 || !_startPending || !_heldAtMs.HasValue || _queued.Count > 0) return;
            HostLog("the last request waiting for the held Worker start was removed; dropping the start.");
            DropPendingStartLocked();
        }

        private void DropPendingStartLocked()
        {
            EndPendingStartLocked();
            _scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);
            if (_record != null)
            {
                _record.State = SharedHostRecordState.NoWorker;
                _record.HeldSinceUtc = null;
                _record.WorkerPid = 0;
                _record.WorkerStartTimeUtcTicks = 0;
            }
            WriteRecordLocked();
        }

        private void EndPendingStartLocked()
        {
            _startPending = false;
            _heldAtMs = null;
            _heldPosition = null;
            _admissionTimer.Change(null);
        }

        /// <summary>
        /// Admission said Start: launch the child at once (its record names the child PID). Every launch after the
        /// first, and the first one when an Attachment already waits, announces the child with worker_restarted
        /// and a new generation, so a Gateway that attached before the child existed learns its PID.
        /// </summary>
        private void LaunchChildLocked(Outbox outbox)
        {
            StartKind kind = _startKind;
            EndPendingStartLocked();
            _pendingAdmissionFrame = null;
            // The crash path already advanced the generation when the child exited.
            bool announce = kind == StartKind.Respawn || _anyChildLaunched || !_attachments.IsEmpty;
            if (announce && kind != StartKind.Respawn) _generation++;
            try
            {
                StartChildLocked();
            }
            catch (Exception ex)
            {
                if (kind == StartKind.Respawn)
                {
                    FailClosedLocked("Shared Worker child respawn failed after " + _respawnDiagnostic + ": " + ex.Message, outbox);
                    return;
                }
                HostLog("Worker start failed: " + ex.Message);
                if (_record != null)
                {
                    _record.State = SharedHostRecordState.NoWorker;
                    _record.WorkerPid = 0;
                    _record.WorkerStartTimeUtcTicks = 0;
                }
                EndColdStartLocked();
                // Requirement 5 criterion 9: each queued request gets replacement-failed, the Attachments stay
                // connected, and the next SDK_Operation starts again.
                FailWaitingRequestsLocked(outbox, entry => BuildToolError(entry.Route.ClientRequestId,
                    ConcurrencyErrorCatalog.WorkerStartupFailed, "replacement-failed", null, _limits.QueueWaitMs,
                    SharedWorkerHostProtocol.MinBusyWaitMs));
                return;
            }

            _anyChildLaunched = true;
            _coldStartActive = true;
            BroadcastAdmissionLocked(AdmissionFrameData("starting", null, null), outbox, keepForNewAttachments: false);
            if (announce)
            {
                string stage = kind == StartKind.Respawn ? "child_exit" : kind == StartKind.Replacement ? "heap_recycle" : "worker_start";
                string diagnostic = kind == StartKind.Respawn ? _respawnDiagnostic : "stage=" + stage;
                QueueWorkerRestartedLocked(stage, diagnostic, outbox);
            }
            try { _childLaunched.Set(); } catch (ObjectDisposedException) { }
        }

        // Cold_Start end (child ready, start failed, or child exited before ready): the record is written under the
        // admission mutex and the FIFO head of the held starts is woken.
        private void EndColdStartLocked()
        {
            _coldStartActive = false;
            if (_record == null) return;
            try
            {
                RefreshRecordLocked();
                _admission.EndColdStart(_record);
                _recordWritten = true;
            }
            catch (Exception ex)
            {
                HostLog("could not end the Cold_Start under the admission mutex: " + ex.Message);
                WriteRecordLocked();
            }
        }

        // Answers every queued SDK_Operation (all of them wait for the start) and removes it from the queue.
        private void FailWaitingRequestsLocked(Outbox outbox, Func<QueuedEntry, JObject> answer)
        {
            foreach (string sessionId in _queued.Values.Select(e => e.SessionId).Distinct().ToList())
            {
                ISharedHostAttachment owner = FindAttachment(sessionId);
                foreach (QueuedRequest queued in _scheduler.RemoveSession(sessionId))
                {
                    if (!_queued.TryGetValue(queued.ChildRequestId, out QueuedEntry entry)) continue;
                    _queued.Remove(queued.ChildRequestId);
                    if (owner != null) outbox.Add(owner, answer(entry).ToString(Formatting.None));
                }
            }
        }

        // The tool-result envelope of an admission outcome: result.status "error", result.code and the catalog error
        // with the decision details merged at the top level.
        private static JObject BuildToolError(JToken clientRequestId, string code, string reason, JObject details, int retryBoundMs, int? retryAfterMs)
        {
            var error = ConcurrencyErrorCatalog.Build(code, reason, new ConcurrencyErrorFields
            {
                QueueWaitMs = retryBoundMs,
                RetryAfterMs = retryAfterMs,
                Details = details
            });
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = clientRequestId.DeepClone(),
                ["result"] = new JObject
                {
                    ["status"] = "error",
                    ["code"] = code,
                    ["error"] = error
                }
            };
        }

        private JObject AdmissionFrameData(string state, int? position, JObject refusal)
        {
            var data = new JObject
            {
                ["state"] = state,
                ["elapsedMs"] = Math.Max(0L, _clock.NowMs - _startRequestedAtMs)
            };
            if (position.HasValue) data["position"] = position.Value;
            if (refusal != null) data["refusal"] = refusal;
            return data;
        }

        private void BroadcastAdmissionLocked(JObject data, Outbox outbox, bool keepForNewAttachments)
        {
            _pendingAdmissionFrame = keepForNewAttachments ? data : null;
            foreach (var attachment in _attachments.Values)
                outbox.Add(attachment, HostFrameLine(SharedWorkerHostProtocol.AdmissionFrameType, (JObject)data.DeepClone()));
        }

        // ---- Eviction victim (decision D8) ----

        // Another host chose this one: stop the Worker only if it is still evictable under the lock, so a request
        // that arrived after the decision wins. The Attachments stay connected; the next request starts again.
        private void OnEvictSignal()
        {
            ISharedHostChild child;
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || !IsChildIdleLocked()) return;
                child = _child;
                HostLog("evicted by the admission of another KB; stopping Worker pid " + child.Pid.ToString(CultureInfo.InvariantCulture) + ".");
                DetachChildLocked(SharedHostRecordState.NoWorker);
            }
            StopDetachedChild(child, "eviction", null);
        }

        // ---- detach with stopIfLast (design "Idle reaping") ----

        // A Gateway evicting the KB for Server.MaxOpenKbs: when no Attachment remains, stop the Worker at once.
        private void StopIfLastAttachment()
        {
            ISharedHostChild child;
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_attachments.IsEmpty) return;
                HostLog("the last Attachment detached with stopIfLast; stopping.");
                child = _child;
                EndPendingStartLocked();
                _child = null;
                _sdkReady = false;
                _coldStartActive = false;
                if (_record != null)
                {
                    _record.State = SharedHostRecordState.Stopping;
                    _record.WorkerPid = 0;
                    _record.WorkerStartTimeUtcTicks = 0;
                }
                WriteRecordLocked();
            }
            if (child != null)
            {
                try { child.Kill(); } catch (Exception ex) { HostLog("stopIfLast: kill failed: " + ex.Message); }
                try { child.Dispose(); } catch { }
            }
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }

        // Fields other hosts' admission reads: Attachments, in-flight and queued counts, evictability, activity.
        private void RefreshRecordLocked()
        {
            if (_record == null) return;
            SchedulerSnapshot snapshot = _scheduler.Snapshot();
            bool background = IsBackgroundActive();
            _record.Attachments = _attachments.Count;
            _record.InFlight = _routes.Count;
            _record.Queued = _queued.Count;
            _record.BackgroundActive = background;
            _record.Evictable = _child != null && _sdkReady && !_startPending && _queued.Count == 0
                && _routes.IsEmpty && snapshot.IsIdle && !background;
            _record.LastActivityUtc = _lastActivityUtc;
        }

        // NotRunning, or Running with another start time (PID reuse), is a dead Gateway. An unreadable start
        // time keeps the Attachment (fail closed, Requirement 7 criterion 8).
        private bool IsGatewayGone(GatewayProcess gateway)
        {
            if (_gatewayProbe == null) return false;
            ProcessProbeResult probe;
            try { probe = _gatewayProbe.Probe(gateway.Pid); }
            catch (Exception ex)
            {
                HostLog("Gateway liveness probe failed: " + ex.Message);
                return false;
            }
            switch (probe.Status)
            {
                case ProcessProbeStatus.NotRunning:
                    return true;
                case ProcessProbeStatus.Running:
                    return !SharedHostRecordClassifier.SameMillisecond(gateway.StartTimeUtcTicks, probe.StartTimeUtcTicks);
                default:
                    return false;
            }
        }

        // No Attachment, no queued or running request, no background activity, and at least --idle-ms since
        // the later of the last detachment and the last request completion (Requirement 5 criterion 4), all
        // measured with ISharedHostClock.
        private bool IsIdleExpired()
        {
            long now = _clock.NowMs;
            lock (_hostLock)
            {
                if (!_attachments.IsEmpty || _queued.Count > 0 || !_scheduler.Snapshot().IsIdle) return false;
                long idleSince = Math.Max(_lastDetachMs, _lastCompletionMs);
                if (now - idleSince < _options.IdleTimeoutMs) return false;
            }
            long lastBackground = Interlocked.Read(ref _lastBackgroundActivityMs);
            return lastBackground == long.MinValue || now - lastBackground >= _options.IdleTimeoutMs;
        }

        private ProcessStartInfo BuildChildStartInfo()
        {
            var start = new ProcessStartInfo
            {
                FileName = _options.WorkerExecutable,
                Arguments = "--kb " + Quote(_options.KbPath) + " --driver " + Quote(_options.Driver) + " --major " + Quote(_options.Major),
                WorkingDirectory = Path.GetDirectoryName(_options.WorkerExecutable) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["GX_PROGRAM_DIR"] = _options.InstallationPath;
            start.EnvironmentVariables["GX_KB_PATH"] = _options.KbPath;
            start.EnvironmentVariables["GXMCP_DRIVER"] = _options.Driver;
            start.EnvironmentVariables["GXMCP_TARGET_MAJOR"] = _options.Major;
            string legacyProvider = Environment.GetEnvironmentVariable("GXMCP_GXPUBLIC_PROVIDER");
            if (!string.IsNullOrWhiteSpace(legacyProvider))
                start.EnvironmentVariables["GXMCP_GXPUBLIC_PROVIDER"] = legacyProvider;
            start.EnvironmentVariables["GXMCP_SHARED_CHILD"] = "1";
            // The broker queue owns the wait (RFC #854): the child's own busy-reject wait would
            // answer WorkerBusy for a request the broker released to it.
            start.EnvironmentVariables["GXMCP_BUSY_REJECT_MS"] = "0";
            start.EnvironmentVariables.Remove("GX_MCP_PIPE");
            return start;
        }

        private void StartChildLocked()
        {
            Volatile.Write(ref _lastChildError, string.Empty);
            ISharedHostChild child = _childLauncher.Start(BuildChildStartInfo());
            if (child == null) throw new InvalidOperationException("Could not start shared Worker child.");
            _child = child;
            // Issue #335: a new child starts a new supervision generation and ends any
            // outstanding recycle election. Clearing the election is what makes the sweep
            // single-shot: without it, every attachment would keep seeing "recycle
            // elected" and could each act on it.
            Interlocked.Increment(ref _brokerGeneration);
            Volatile.Write(ref _recycleElected, 0);
            _sdkReady = false;
            if (_record == null) _record = NewAdmittingRecord();
            _record.WorkerPid = child.Pid;
            _record.WorkerStartTimeUtcTicks = child.StartTimeUtcTicks;
            _record.Generation = (int)_generation;
            _record.State = SharedHostRecordState.Starting;
            _record.HeldSinceUtc = null;
            WriteRecordLocked();

            if (!_runBackgroundThreads) return;
            _childReader = new Thread(() => ChildReaderLoop(child)) { IsBackground = true, Name = "SharedWorkerChildReader" };
            _childErrorReader = new Thread(() => ChildErrorReaderLoop(child)) { IsBackground = true, Name = "SharedWorkerChildErrorReader" };
            _childReader.Start();
            _childErrorReader.Start();
        }

        private void StartPipeAcceptLoop()
        {
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SharedWorkerPipeAccept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _options.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        64 * 1024,
                        64 * 1024);
                    server.WaitForConnection();
                    if (_stop.IsCancellationRequested) { server.Dispose(); break; }
                    var attachment = new SharedWorkerHostAttachment(this, server, _options.AttachTimeoutMs);
                    ThreadPool.QueueUserWorkItem(_ => _ = attachment.RunAsync());
                    server = null;
                }
                catch (Exception ex)
                {
                    try { server?.Dispose(); } catch { }
                    HostLog("pipe accept failed: " + ex.Message);
                    // Back off on the stop handle, so Dispose ends the wait at once.
                    if (!_stop.IsCancellationRequested) _stop.Token.WaitHandle.WaitOne(250);
                }
            }
        }

        // ---- Attachments ----

        internal bool Attach(ISharedHostAttachment attachment, SharedWorkerEnvelope request, out string error)
        {
            error = string.Empty;
            if (attachment == null) { error = "attachment is null"; return false; }
            if (!SharedWorkerHostProtocol.TryValidateAttach(request, _options.IdentityKey, out error))
            {
                // Protocol v2: name each Physical_KB_Identity component of the attach frame that differs.
                if (request?.Identity != null
                    && string.Equals(error, "shared Worker identity mismatch", StringComparison.Ordinal))
                {
                    IReadOnlyList<string> differing = SharedWorkerHostProtocol.DescribeIdentityMismatch(request.Identity, LiveIdentityComponents());
                    if (differing.Count > 0) error += " (differing components: " + string.Join(", ", differing) + ")";
                }
                return false;
            }
            JObject ack;
            JObject pendingAdmission = null;
            lock (_hostLock)
            {
                attachment.AttachmentId = Guid.NewGuid().ToString("N");
                if (!_attachments.TryAdd(attachment.ConnectionId, attachment))
                {
                    error = "could not register attachment";
                    return false;
                }
                // Liveness of this Attachment is its Gateway's PID and start time (Requirement 7 criterion 5).
                _gateways[attachment.ConnectionId] = new GatewayProcess(request.GatewayPid, request.GatewayStartTimeUtcTicks);
                ack = new JObject
                {
                    ["attachId"] = attachment.AttachmentId,
                    ["attachmentId"] = attachment.AttachmentId,
                    ["hostPid"] = _record.HostPid,
                    ["hostStartTimeUtcTicks"] = _record.HostStartTimeUtcTicks,
                    ["workerPid"] = _record.WorkerPid,
                    ["workerStartTimeUtcTicks"] = _record.WorkerStartTimeUtcTicks,
                    ["generation"] = _record.Generation,
                    ["ready"] = _sdkReady,
                    // Protocol v2: the child state. workerPid and workerStartTimeUtcTicks are 0 before the child
                    // exists (admitting, held, refused, no-worker); worker_restarted names the child once it starts.
                    ["state"] = _record.State ?? string.Empty,
                    ["launchedByGatewayPid"] = _record.LaunchedByGatewayPid
                };
                // Late attachers learn about a held, evicting or refused start that is already in progress.
                if (_pendingAdmissionFrame != null && _child == null)
                {
                    var admission = (JObject)_pendingAdmissionFrame.DeepClone();
                    admission["elapsedMs"] = Math.Max(0L, _clock.NowMs - _startRequestedAtMs);
                    pendingAdmission = admission;
                }
                ArmIdleRecordLocked();
            }
            SendHostFrame(attachment, "attach_ack", ack);
            if (pendingAdmission != null) SendHostFrame(attachment, SharedWorkerHostProtocol.AdmissionFrameType, pendingAdmission);
            return true;
        }

        /// <summary>
        /// The pipe-EOF path (RunAsync's finally); a detach frame reaches it through
        /// <see cref="ISharedHostAttachment.Stop"/>. Queued requests of the Attachment are dropped
        /// without executing them; its in-flight request keeps running, because an SDK call
        /// cannot be aborted safely, and its response and progress are discarded.
        /// </summary>
        internal void Detach(ISharedHostAttachment attachment)
        {
            if (attachment == null) return;
            var outbox = new Outbox();
            lock (_hostLock)
            {
                // Idempotent: the supervisor tick and the transport both detach a dead Gateway's
                // Attachment, and only the first detachment starts the idle timer.
                if (!_attachments.TryRemove(attachment.ConnectionId, out _)) return;
                _gateways.Remove(attachment.ConnectionId);
                string attachmentId = attachment.AttachmentId;
                int removedCount = 0;
                if (!string.IsNullOrEmpty(attachmentId))
                {
                    foreach (QueuedRequest removed in _scheduler.RemoveSession(attachmentId))
                    {
                        if (_queued.Remove(removed.ChildRequestId)) removedCount++;
                    }
                    RemoveRoutesForAttachment(attachmentId);
                }
                _lastDetachMs = _clock.NowMs;
                DropHoldIfNoWaitersLocked(removedCount);
                ArmIdleRecordLocked();
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
        }

        internal void Receive(ISharedHostAttachment attachment, string line)
        {
            if (attachment == null || string.IsNullOrWhiteSpace(line)) return;
            if (SharedWorkerHostProtocol.TryParseEnvelope(line, out SharedWorkerEnvelope envelope, out _))
            {
                ReceiveHostFrame(attachment, envelope);
                return;
            }

            JObject request;
            try { request = GxMcp.Common.JsonIngress.ParseObject(line); }
            catch { SendHostError(attachment, "malformed JSON-RPC frame"); return; }
            if (!string.Equals(request.Value<string>("jsonrpc"), "2.0", StringComparison.Ordinal))
            {
                SendHostError(attachment, "expected JSON-RPC 2.0 frame");
                return;
            }

            var outbox = new Outbox();
            lock (_hostLock)
            {
                if (!_attachments.ContainsKey(attachment.ConnectionId)) return;
                if (string.Equals(request.Value<string>("method"), "notifications/cancelled", StringComparison.Ordinal))
                    ReceiveCancellationLocked(attachment, request, outbox);
                else
                    ReceiveRequestLocked(attachment, request, outbox);
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
        }

        private void ReceiveHostFrame(ISharedHostAttachment attachment, SharedWorkerEnvelope envelope)
        {
            bool known = envelope.Type == "heartbeat" || envelope.Type == "detach"
                || envelope.Type == SharedWorkerHostProtocol.StatusFrameType;
            if (!known)
            {
                SendHostError(attachment, "unexpected host frame after attach");
                return;
            }
            if (!SharedWorkerHostProtocol.TryValidateSessionEnvelope(
                envelope, true, _options.IdentityKey, attachment.AttachmentId, out string sessionError))
            {
                SendHostError(attachment, sessionError);
                return;
            }

            if (envelope.Type == "heartbeat")
            {
                // Issue #335: the heartbeat_ack now carries the broker's own view of the child.
                //
                // In shared mode the Gateway does not own the child Process, so it
                // cannot ask the OS anything: no PID, no working set, no way to tell a
                // progressing SDK call from a deadlocked one. That left a shared Worker
                // with no heap or stall supervision at all - the exact gap that made
                // these checks worth having for an isolated Worker.
                //
                // The broker is the only party that can answer them, so it does, here -
                // on a frame that already exists. Extending heartbeat_ack rather than
                // adding a frame type keeps this off the protocol-migration path
                // entirely: an older Gateway ignores the new fields, and an older
                // broker's ack still satisfies the newer Gateway's reader.
                SendHostFrame(attachment, "heartbeat_ack", BuildHeartbeatAck(attachment.AttachmentId));
            }
            else if (envelope.Type == "detach")
            {
                attachment.Stop();
                // The transport detaches on pipe EOF too; detaching here makes the stopIfLast check see this
                // Attachment gone. Detach is idempotent.
                Detach(attachment);
                if (envelope.StopIfLast) StopIfLastAttachment();
            }
            else
            {
                // Protocol v2 diagnostics: answered from a snapshot, never queued.
                SendStatusAck(attachment, envelope.RequestId);
            }
        }

        private void SendStatusAck(ISharedHostAttachment attachment, string requestId)
        {
            SchedulerSnapshot snapshot;
            ISharedHostChild child;
            string childState;
            int attachments;
            lock (_hostLock)
            {
                snapshot = _scheduler.Snapshot();
                child = _child;
                childState = _record?.State ?? string.Empty;
                attachments = _attachments.Count;
            }
            var context = new SharedHostStatusContext
            {
                ChildState = childState,
                WorkerPid = child?.Pid ?? 0,
                WorkerWorkingSetMb = child == null ? 0 : child.WorkingSetBytes / (1024 * 1024),
                Attachments = attachments,
                EffectiveSettings = new JObject
                {
                    ["queueWaitMs"] = _limits.QueueWaitMs,
                    ["queueDepth"] = _limits.QueueDepth,
                    ["sessionQueueCap"] = _limits.SessionQueueCap,
                    ["maxWorkers"] = _settings.MaxWorkers,
                    ["memoryFloorMb"] = _settings.MemoryFloorMb,
                    ["coldStartConcurrency"] = _settings.ColdStartConcurrency,
                    ["attachReadyTimeoutSeconds"] = _settings.AttachReadyTimeoutSeconds,
                    ["heapRecycleMb"] = _options.HeapRecycleMb
                }
            };
            JObject ack;
            try { ack = SharedWorkerHostProtocol.BuildStatusAck(_options.IdentityKey, requestId, snapshot, context); }
            catch (ArgumentException) { SendHostError(attachment, "status frame requires a bounded requestId"); return; }
            attachment.Enqueue(ack.ToString(Formatting.None));
        }

        // notifications/cancelled: a queued request of this Attachment is removed and answered
        // Cancelled; otherwise the cancellation is forwarded for this Attachment's in-flight
        // request; a cancellation that matches none of its requests is dropped.
        private void ReceiveCancellationLocked(ISharedHostAttachment attachment, JObject request, Outbox outbox)
        {
            JToken requestId = request["params"]?["requestId"];
            if (requestId == null || requestId.Type == JTokenType.Null) return;
            var removed = _scheduler.Cancel(attachment.AttachmentId, requestId.ToString(Formatting.None), null).Removed;
            if (removed.Count > 0)
            {
                foreach (QueuedRequest queued in removed) AnswerCancelledLocked(attachment, queued, outbox);
                DropHoldIfNoWaitersLocked(removed.Count);
                return;
            }
            if (SharedWorkerHostProtocol.TryRewriteCancellationForChild(
                request, _routes.Values, attachment.AttachmentId, out JObject childCancellation, out _))
            {
                if (!TryEnqueueChildLocked(childCancellation.ToString(Formatting.None)))
                    HostLog("dropped cancellation: the child inbox is full or no child is running");
            }
        }

        private void ReceiveRequestLocked(ISharedHostAttachment attachment, JObject request, Outbox outbox)
        {
            string method = request.Value<string>("method") ?? string.Empty;
            string action = request["action"]?.Type == JTokenType.String ? (string)request["action"] : null;
            bool bypass = IsBypassCommand(request, method, action);
            if (!SharedWorkerHostProtocol.TryRewriteRequestForChild(
                request, attachment.AttachmentId, attachment.NextSequence(), out JObject childRequest, out SharedWorkerRequestRoute route, out string error))
            {
                outbox.Add(attachment, HostFrameLine("host_error", new JObject { ["message"] = error }));
                return;
            }

            if (Volatile.Read(ref _fatal) != 0 || (bypass && _child == null))
            {
                outbox.Add(attachment, SharedWorkerHostProtocol.BuildRestartedError(
                    route.ClientRequestId, ConcurrencyDispatchState.NotDelivered, method, action, _generation, _limits.QueueWaitMs)
                    .ToString(Formatting.None));
                return;
            }

            if (bypass)
            {
                // control/Cancel names a cancelToken; a queued request with that token is
                // removed here, and the command still reaches the child for running work.
                if (string.Equals(method, "control", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(action, "Cancel", StringComparison.Ordinal)
                    && request["params"]?["cancelToken"]?.Type == JTokenType.String)
                {
                    string token = (string)request["params"]["cancelToken"];
                    var cancelled = _scheduler.Cancel(attachment.AttachmentId, null, token).Removed;
                    foreach (QueuedRequest queued in cancelled)
                        AnswerCancelledLocked(attachment, queued, outbox);
                    DropHoldIfNoWaitersLocked(cancelled.Count);
                }
                RegisterRouteLocked(route, new RouteInfo(method, action, false, 0, 0));
                if (!TryEnqueueChildLocked(childRequest.ToString(Formatting.None)))
                {
                    UnregisterRouteLocked(route.ChildRequestId);
                    outbox.Add(attachment, BuildBackpressureError(route.ClientRequestId));
                }
                return;
            }

            long now = _clock.NowMs;
            var entry = new QueuedEntry(route, childRequest, attachment.AttachmentId, method, action);
            var queuedRequest = new QueuedRequest
            {
                ChildRequestId = route.ChildRequestId,
                SessionId = attachment.AttachmentId,
                ClientRequestIdJson = route.ClientRequestId.ToString(Formatting.None),
                Operation = method + "/" + (action ?? string.Empty),
                CancelToken = request["params"]?["cancelToken"]?.Type == JTokenType.String ? (string)request["params"]["cancelToken"] : null,
                EnqueuedAtMs = now,
                Sequence = ++_sequence
            };
            _queued[route.ChildRequestId] = entry;
            SubmitResult submit = _scheduler.Submit(queuedRequest);
            if (submit.Started)
            {
                // Started without waiting: released at the submit instant, so its wait is 0.
                ReleaseLocked(queuedRequest, outbox, now);
            }
            else if (submit.Queued)
            {
                // No child and no start in progress (after a refusal, an eviction, a dropped or expired hold, or a
                // failed replacement): this SDK_Operation runs Admit again. A pending start just gains a waiter.
                if (_child == null && !_startPending) BeginStartLocked(StartKind.Request, outbox);
            }
            else if (!submit.Queued)
            {
                _queued.Remove(route.ChildRequestId);
                SchedulerSnapshot snapshot = _scheduler.Snapshot();
                int ahead = snapshot.QueueDepth + (snapshot.IsIdle ? 0 : 1);
                outbox.Add(attachment, SharedWorkerHostProtocol.BuildBusyResult(
                    route.ClientRequestId, submit.RejectReason, snapshot, _limits, _options.IdentityKey, _options.KbPath,
                    null, EstimateRetryAfterMs(ahead)).ToString(Formatting.None));
            }
        }

        private static bool IsBypassCommand(JObject request, string method, string action)
        {
            if (CommandLaneClassifier.IsThreadSafe(request)) return true;
            // Control commands never wait behind the SDK lane they exist to observe or cancel.
            return string.Equals(method, "health", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(method, "control", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(action, "Cancel", StringComparison.Ordinal));
        }

        private void AnswerCancelledLocked(ISharedHostAttachment attachment, QueuedRequest queued, Outbox outbox)
        {
            if (!_queued.TryGetValue(queued.ChildRequestId, out QueuedEntry entry)) return;
            _queued.Remove(queued.ChildRequestId);
            outbox.Add(attachment, SharedWorkerHostProtocol.BuildCancelledResult(entry.Route.ClientRequestId).ToString(Formatting.None));
        }

        // ---- Release to the child ----

        // Writes a released SDK_Operation to the child inbox with its remaining queue wait as
        // _meta.busyWaitMs. A full inbox answers SHARED_WORKER_BACKPRESSURE and frees the window.
        private void ReleaseLocked(QueuedRequest next, Outbox outbox, long? releasedAtMs = null)
        {
            while (next != null)
            {
                if (!_queued.TryGetValue(next.ChildRequestId, out QueuedEntry entry))
                {
                    next = _scheduler.Complete(next.ChildRequestId);
                    continue;
                }
                _queued.Remove(next.ChildRequestId);
                long now = releasedAtMs ?? _clock.NowMs;
                releasedAtMs = null;
                var meta = entry.ChildRequest["_meta"] as JObject;
                if (meta == null)
                {
                    meta = new JObject();
                    entry.ChildRequest["_meta"] = meta;
                }
                meta["busyWaitMs"] = SharedWorkerHostProtocol.ComputeBusyWaitMs(_limits.QueueWaitMs, now - next.EnqueuedAtMs);
                RegisterRouteLocked(entry.Route, new RouteInfo(entry.Method, entry.Action, true, next.EnqueuedAtMs, now));
                if (TryEnqueueChildLocked(entry.ChildRequest.ToString(Formatting.None))) return;

                UnregisterRouteLocked(entry.Route.ChildRequestId);
                ISharedHostAttachment owner = FindAttachment(entry.SessionId);
                if (owner != null) outbox.Add(owner, BuildBackpressureError(entry.Route.ClientRequestId));
                next = _scheduler.Complete(next.ChildRequestId);
            }
        }

        private bool TryEnqueueChildLocked(string line)
        {
            ISharedHostChild child = _child;
            if (child == null) return false;
            try { return _childInbox.TryAdd(new ChildInboxItem(child, line)); }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>
        /// Writes every pending child-inbox line to the current child, in order, on the
        /// caller's thread. Only for runBackgroundThreads=false; with threads the inbox writer
        /// consumes the inbox with GetConsumingEnumerable.
        /// </summary>
        internal int PumpChildInbox()
        {
            if (_runBackgroundThreads)
                throw new InvalidOperationException("PumpChildInbox is only available without background threads.");
            int written = 0;
            while (_childInbox.TryTake(out ChildInboxItem item))
                if (WriteChildInboxItem(item)) written++;
            return written;
        }

        private void ChildInboxWriterLoop()
        {
            try
            {
                foreach (ChildInboxItem item in _childInbox.GetConsumingEnumerable(_stop.Token))
                    WriteChildInboxItem(item);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { HostLog("child inbox writer stopped: " + ex.Message); }
        }

        // A line addressed to a child that is no longer current is dropped: the exit path
        // already answered its request, and it must never reach the replacement.
        private bool WriteChildInboxItem(ChildInboxItem item)
        {
            ISharedHostChild current;
            lock (_hostLock) current = _child;
            if (current == null || !ReferenceEquals(current, item.Child))
            {
                HostLog("dropped a child inbox line addressed to a previous child");
                return false;
            }
            try
            {
                current.WriteLine(item.Line);
                return true;
            }
            catch (Exception ex)
            {
                HostLog("child stdin closed: " + ex.Message);
                return false;
            }
        }

        // ---- Child output ----

        /// <summary>One stdout line of <paramref name="child"/>, as the child reader loop delivers it.</summary>
        internal void OnChildLine(ISharedHostChild child, string line) => HandleChildLine(child, line);

        // False when the line made the host stop fail-closed.
        private bool HandleChildLine(ISharedHostChild child, string line)
        {
            if (child == null || string.IsNullOrWhiteSpace(line)) return true;
            JObject frame;
            try { frame = GxMcp.Common.JsonIngress.ParseObject(line); }
            catch
            {
                // The normal Worker emits two plain-text handshake lines before
                // switching to newline-delimited JSON-RPC. They are part of the
                // existing stdio contract, not malformed broker frames.
                if (line.StartsWith("WORKER_HANDSHAKE_", StringComparison.Ordinal)) return true;
                BroadcastHostError("Shared Worker child emitted malformed JSON; host stopped fail-closed.");
                _stop.Cancel();
                return false;
            }

            var outbox = new Outbox();
            lock (_hostLock)
            {
                if (!ReferenceEquals(child, _child)) return true;
                string method = frame.Value<string>("method") ?? string.Empty;
                if (string.Equals(method, "notifications/worker/build_active", StringComparison.Ordinal)
                    || string.Equals(method, "notifications/worker/index_active", StringComparison.Ordinal))
                {
                    Interlocked.Exchange(ref _lastBackgroundActivityMs, _clock.NowMs);
                }

                if (frame["id"] != null && frame["id"].Type != JTokenType.Null && string.IsNullOrEmpty(method))
                {
                    HandleChildResponseLocked(child, frame, outbox);
                }
                else
                {
                    if (string.Equals(method, "notifications/worker/sdk_ready", StringComparison.Ordinal))
                        MarkChildReadyLocked(outbox);
                    RouteChildNotificationLocked(frame, method, outbox);
                }
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
            return true;
        }

        private void MarkChildReadyLocked(Outbox outbox)
        {
            _sdkReady = true;
            _pendingAdmissionFrame = null;
            if (_record != null) _record.State = SharedHostRecordState.Ready;
            // The Cold_Start ends here: the ready record is written under the admission mutex and the oldest
            // held start elsewhere is woken.
            if (_coldStartActive) EndColdStartLocked();
            else WriteRecordLocked();
            _scheduler.SetWorkerAvailable(true, WaitReason.StaBusy);
            ReleaseLocked(_scheduler.TryStartNext(), outbox);
        }

        private void HandleChildResponseLocked(ISharedHostChild child, JObject frame, Outbox outbox)
        {
            string childId = frame["id"].ToString();
            // Requirement 5 criterion 4: the idle period also counts from the last request completion.
            _lastCompletionMs = _clock.NowMs;
            _lastActivityUtc = DateTime.UtcNow;
            if (_routes.TryRemove(childId, out SharedWorkerRequestRoute route))
            {
                if (!string.IsNullOrWhiteSpace(route.ChildProgressToken)) _progressOwners.TryRemove(route.ChildProgressToken, out _);
                _routeInfo.TryGetValue(childId, out RouteInfo info);
                _routeInfo.Remove(childId);
                if (SharedWorkerHostProtocol.TryRestoreResponse(frame, route, out JObject restored, out _))
                {
                    if (info != null && info.IsSdk)
                    {
                        SharedWorkerHostProtocol.TryInjectSharedMetadata(restored, info.ReleasedAtMs - info.EnqueuedAtMs, child.Pid);
                        RecordServiceTime(_clock.NowMs - info.ReleasedAtMs);
                    }
                    ISharedHostAttachment owner = FindAttachment(route.AttachmentId);
                    if (owner != null) outbox.Add(owner, restored.ToString(Formatting.None));
                }
            }
            // Always: the window may belong to a request whose Attachment detached.
            CompleteLocked(childId, outbox);
        }

        private void CompleteLocked(string childId, Outbox outbox)
        {
            QueuedRequest next = _scheduler.Complete(childId);
            if (next != null) ReleaseLocked(next, outbox);
            else if (_queued.Count == 0 && _scheduler.Snapshot().IsIdle) ArmIdleRecordLocked();
        }

        private void RouteChildNotificationLocked(JObject frame, string method, Outbox outbox)
        {
            if (SharedWorkerHostProtocol.TryRouteChildNotification(
                frame, _progressOwners, out string attachmentId, out JObject routed, out bool broadcast, out string routeError))
            {
                string json = routed.ToString(Formatting.None);
                if (broadcast)
                {
                    foreach (var attachment in _attachments.Values) outbox.Add(attachment, json);
                }
                else if (!string.IsNullOrWhiteSpace(attachmentId))
                {
                    ISharedHostAttachment owner = FindAttachment(attachmentId);
                    if (owner != null) outbox.Add(owner, json);
                }
                return;
            }
            if (!string.IsNullOrWhiteSpace(method))
                HostLog("dropping unroutable child notification method=" + method + " reason=" + routeError);
        }

        private void ChildReaderLoop(ISharedHostChild child)
        {
            try
            {
                string line;
                long frameBytes;
                // Bounded read (issue #375): a line past the ceiling is never buffered whole.
                while (!_stop.IsCancellationRequested
                    && (line = SharedWorkerHostProtocol.ReadBoundedLine(
                        child.StandardOutput, SharedWorkerHostProtocol.HardFrameCeilingBytes, out frameBytes)) != null)
                {
                    if (frameBytes > SharedWorkerHostProtocol.MaxFrameBytes)
                    {
                        // Issue #347. This used to broadcast a host error and cancel the
                        // broker, so one client's legitimately large read, batch or
                        // persisted-content result took down every other attachment. A
                        // large frame that parses is a valid response that does not fit
                        // the transport, and the answer is a bounded per-request error -
                        // not a shared outage.
                        //
                        // Malformed and abusive frames still stop the host, unchanged: the
                        // oversized path must not become a way past the fail-closed check,
                        // so an unparseable frame is still fatal here.
                        var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(
                            line, out string oversizedRequestId, frameBytes);

                        if (disposition == SharedWorkerHostProtocol.OversizedFrameDisposition.FailClosed)
                        {
                            BroadcastHostError(
                                frameBytes > SharedWorkerHostProtocol.HardFrameCeilingBytes
                                    ? "Shared Worker child emitted a frame beyond the hard ceiling (" + frameBytes
                                      + " bytes); host stopped fail-closed."
                                    : "Shared Worker child emitted an unparseable oversized frame; host stopped fail-closed.");
                            _stop.Cancel();
                            break;
                        }

                        if (disposition == SharedWorkerHostProtocol.OversizedFrameDisposition.RejectRequest)
                        {
                            var refusal = SharedWorkerHostProtocol.BuildOversizedResponse(
                                oversizedRequestId, frameBytes, SharedWorkerHostProtocol.MaxFrameBytes);
                            // Route it exactly as a normal response would be routed, to the
                            // attachment that owns the request, and nowhere else.
                            SharedWorkerRequestRoute oversizedRoute = TakeAnsweredRoute(child, oversizedRequestId);
                            if (oversizedRoute != null)
                            {
                                var clientFrame = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = oversizedRoute.ClientRequestId ?? JValue.CreateNull(),
                                    ["error"] = refusal["error"]
                                };
                                SendJson(_attachments, oversizedRoute.AttachmentId, clientFrame.ToString(Formatting.None));
                            }
                            else
                            {
                                // No live route: the request already completed, timed out or
                                // detached. Log it rather than guessing an owner.
                                HostLog("dropped oversized response for unrouted request id=" + oversizedRequestId);
                            }
                        }
                        else
                        {
                            HostLog("dropped oversized child notification (" + frameBytes + " bytes); no requester to answer");
                        }

                        // The host keeps serving every other attachment.
                        continue;
                    }
                    if (!HandleChildLine(child, line)) break;
                }
            }
            catch (Exception ex) { HostLog("child stdout closed: " + ex.Message); }
            finally
            {
                if (!_stop.IsCancellationRequested)
                    HostLog("shared Worker child stdout ended; the supervisor recovers it on exit");
            }
        }

        // Removes the route of a request the broker answers itself (an oversized response) and
        // ends the request in the scheduler, so the next queued request is released.
        private SharedWorkerRequestRoute TakeAnsweredRoute(ISharedHostChild child, string childRequestId)
        {
            var outbox = new Outbox();
            SharedWorkerRequestRoute route = null;
            lock (_hostLock)
            {
                if (!ReferenceEquals(child, _child)) return null;
                if (_routes.TryRemove(childRequestId, out route)) UnregisterRouteLocked(childRequestId, route);
                CompleteLocked(childRequestId, outbox);
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
            return route;
        }

        private void ChildErrorReaderLoop(ISharedHostChild child)
        {
            try
            {
                string line;
                while (!_stop.IsCancellationRequested && (line = child.StandardError.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        Volatile.Write(ref _lastChildError, RedactDiagnostic(line));
                    HostLog("child stderr: " + line);
                }
            }
            catch { }
        }

        // ---- Child exit ----

        /// <summary>
        /// Called by the supervisor when the child's ExitedHandle is signalled. Requests
        /// already written to the child get SHARED_WORKER_RESTARTED (delivered-unanswered,
        /// retryable from <see cref="WorkerCommandCatalog"/>); queued requests stay queued for
        /// the replacement, or get not-delivered when no replacement starts. Three exits in one
        /// minute stop the host fail-closed. The replacement is started before this returns.
        /// </summary>
        internal void OnChildExited(ISharedHostChild child)
        {
            if (child == null) return;
            // A child that a heap recycle, an eviction or stopIfLast already took out of service is not a crash,
            // and _childReader may already belong to its replacement.
            lock (_hostLock)
            {
                if (!ReferenceEquals(child, _child)) return;
            }
            // Let the reader deliver what the child wrote before it exited.
            Thread reader = _childReader;
            if (_runBackgroundThreads && reader != null && reader != Thread.CurrentThread) reader.Join(ChildReaderDrainMs);

            var outbox = new Outbox();
            string failureDiagnostic;
            bool replace;
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(child, _child)) return;
                failureDiagnostic = BuildChildFailureDiagnostic(child);
                _lastChildFailureDiagnostic = failureDiagnostic;
                bool coldStartEnded = _coldStartActive;
                _child = null;
                _sdkReady = false;
                _scheduler.SetWorkerAvailable(false, WaitReason.WorkerStarting);
                QueuedRequest running = _scheduler.Snapshot().Running;
                if (running != null) _scheduler.Complete(running.ChildRequestId);
                FailDeliveredRequestsLocked(outbox);
                if (_record != null)
                {
                    _record.WorkerPid = 0;
                    _record.WorkerStartTimeUtcTicks = 0;
                }

                long now = _clock.NowMs;
                while (_respawnHistory.Count > 0 && now - _respawnHistory.Peek() >= RespawnWindowMs)
                    _respawnHistory.Dequeue();
                if (_respawnHistory.Count >= MaxRespawnsPerWindow)
                {
                    if (coldStartEnded)
                    {
                        if (_record != null) _record.State = SharedHostRecordState.Stopping;
                        EndColdStartLocked();
                    }
                    FailClosedLocked("Shared Worker child crashed repeatedly; host stopped fail-closed.", outbox);
                    replace = false;
                }
                else
                {
                    _respawnHistory.Enqueue(now);
                    _generation++;
                    _respawnDiagnostic = failureDiagnostic;
                    if (_record != null) _record.State = SharedHostRecordState.Recovering;
                    // A child that exits before sdk_ready ends its Cold_Start: wake the oldest held start elsewhere.
                    if (coldStartEnded) EndColdStartLocked();
                    else WriteRecordLocked();
                    replace = true;
                }
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
            // A replacement starts only after the operating system reports this child as exited, so
            // two SDK children never overlap on one KB. Checked before Dispose, which drops the handle.
            bool exited = !replace || ConfirmChildExited(child);
            try { child.Dispose(); } catch { }
            if (!replace) return;

            // Respawn back-off on the stop handle, outside the host lock.
            if (_runBackgroundThreads && _stop.Token.WaitHandle.WaitOne(RespawnBackoffMs)) return;

            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (!exited)
                {
                    FailClosedLocked("Shared Worker child pid " + child.Pid.ToString(CultureInfo.InvariantCulture)
                        + " did not exit; no replacement was started and the host stopped fail-closed.", outbox);
                }
                else
                {
                    // The respawn takes its own Admit; queued requests keep waiting for it.
                    BeginStartLocked(StartKind.Respawn, outbox);
                }
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
        }

        // True once the operating system reports the child as exited. A child whose exit was signalled but
        // is not yet reported gets ChildExitConfirmMs, then one Kill and another ChildExitConfirmMs.
        private static bool ConfirmChildExited(ISharedHostChild child)
        {
            if (HasChildExited(child)) return true;
            try { child.ExitedHandle.WaitOne(ChildExitConfirmMs); } catch { }
            if (HasChildExited(child)) return true;
            try { child.Kill(); } catch { }
            try { child.ExitedHandle.WaitOne(ChildExitConfirmMs); } catch { }
            return HasChildExited(child);
        }

        private static bool HasChildExited(ISharedHostChild child)
        {
            try { return child.HasExited; }
            catch { return false; }
        }

        private void FailDeliveredRequestsLocked(Outbox outbox)
        {
            foreach (SharedWorkerRequestRoute route in _routes.Values.ToList())
            {
                _routeInfo.TryGetValue(route.ChildRequestId, out RouteInfo info);
                ISharedHostAttachment owner = FindAttachment(route.AttachmentId);
                if (owner == null) continue;
                outbox.Add(owner, SharedWorkerHostProtocol.BuildRestartedError(
                    route.ClientRequestId, ConcurrencyDispatchState.DeliveredUnanswered,
                    info?.Method, info?.Action, _generation, _limits.QueueWaitMs).ToString(Formatting.None));
            }
            _routes.Clear();
            _progressOwners.Clear();
            _routeInfo.Clear();
        }

        // No replacement: every queued request gets not-delivered, then the host stops.
        private void FailClosedLocked(string message, Outbox outbox)
        {
            foreach (string sessionId in _queued.Values.Select(e => e.SessionId).Distinct().ToList())
            {
                ISharedHostAttachment owner = FindAttachment(sessionId);
                foreach (QueuedRequest queued in _scheduler.RemoveSession(sessionId))
                {
                    if (!_queued.TryGetValue(queued.ChildRequestId, out QueuedEntry entry)) continue;
                    _queued.Remove(queued.ChildRequestId);
                    if (owner == null) continue;
                    outbox.Add(owner, SharedWorkerHostProtocol.BuildRestartedError(
                        entry.Route.ClientRequestId, ConcurrencyDispatchState.NotDelivered,
                        entry.Method, entry.Action, _generation, _limits.QueueWaitMs).ToString(Formatting.None));
                }
            }
            _queued.Clear();
            foreach (var attachment in _attachments.Values)
                outbox.Add(attachment, HostFrameLine("host_error", new JObject { ["message"] = message }));
            Volatile.Write(ref _fatal, 1);
            _stop.Cancel();
        }

        private void QueueWorkerRestartedLocked(string failureStage, string failureDiagnostic, Outbox outbox)
        {
            foreach (var attachment in _attachments.Values)
            {
                outbox.Add(attachment, HostFrameLine("worker_restarted", new JObject
                {
                    ["hostPid"] = _record.HostPid,
                    ["hostStartTimeUtcTicks"] = _record.HostStartTimeUtcTicks,
                    ["workerPid"] = _record.WorkerPid,
                    ["workerStartTimeUtcTicks"] = _record.WorkerStartTimeUtcTicks,
                    ["generation"] = _record.Generation,
                    ["ready"] = false,
                    ["failureStage"] = failureStage,
                    ["failureDiagnostic"] = failureDiagnostic
                }));
            }
        }

        // ---- Queue expiry and record debounce ----

        // One timer, armed just after the earliest queue deadline: the scheduler expires
        // strictly after the deadline, so arming at the deadline would find nothing to expire.
        private void RescheduleExpiryLocked(Outbox outbox)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                long? deadline = _scheduler.NextDeadlineMs;
                if (!deadline.HasValue)
                {
                    if (_expiryArmedAtMs.HasValue)
                    {
                        _expiryTimer.Change(null);
                        _expiryArmedAtMs = null;
                    }
                    return;
                }
                if (deadline.Value < _clock.NowMs)
                {
                    // Already expired (for example skipped by a start): answer it now.
                    ExpireDueLocked(outbox);
                    continue;
                }
                long dueAt = deadline.Value + 1;
                if (_expiryArmedAtMs != dueAt)
                {
                    _expiryTimer.Change(dueAt);
                    _expiryArmedAtMs = dueAt;
                }
                return;
            }
        }

        private void OnExpiryTimer()
        {
            var outbox = new Outbox();
            lock (_hostLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _expiryArmedAtMs = null;
                ExpireDueLocked(outbox);
                RescheduleExpiryLocked(outbox);
            }
            Flush(outbox);
        }

        private void ExpireDueLocked(Outbox outbox)
        {
            IReadOnlyList<ExpiredRequest> expired = _scheduler.ExpireDue();
            if (expired.Count == 0) return;
            SchedulerSnapshot snapshot = _scheduler.Snapshot();
            foreach (ExpiredRequest item in expired)
            {
                if (!_queued.TryGetValue(item.Request.ChildRequestId, out QueuedEntry entry)) continue;
                _queued.Remove(item.Request.ChildRequestId);
                ISharedHostAttachment owner = FindAttachment(entry.SessionId);
                if (owner == null) continue;
                outbox.Add(owner, SharedWorkerHostProtocol.BuildBusyResult(
                    entry.Route.ClientRequestId, "queue-wait-expired", snapshot, _limits, _options.IdentityKey, _options.KbPath,
                    item.Position, EstimateRetryAfterMs(item.Position)).ToString(Formatting.None));
            }
        }

        // The record is written on state transitions; going idle adds at most one debounced
        // write per IdleRecordDebounceMs.
        private void ArmIdleRecordLocked()
        {
            if (_idleRecordArmed || Volatile.Read(ref _disposed) != 0) return;
            _idleRecordArmed = true;
            _idleRecordTimer.Change(_clock.NowMs + IdleRecordDebounceMs);
        }

        private void OnIdleRecordTimer()
        {
            lock (_hostLock)
            {
                _idleRecordArmed = false;
                if (Volatile.Read(ref _disposed) != 0) return;
                if (_queued.Count == 0 && _scheduler.Snapshot().IsIdle) WriteRecordLocked();
            }
        }

        // Exponentially weighted mean of recent SDK_Operation service times.
        private void RecordServiceTime(long elapsedMs)
        {
            double sample = Math.Max(0L, elapsedMs);
            _serviceMsEstimate = _serviceMsEstimate <= 0 ? sample : 0.7 * _serviceMsEstimate + 0.3 * sample;
        }

        private long EstimateRetryAfterMs(int requestsAhead)
        {
            double perRequest = Math.Max(SharedWorkerHostProtocol.MinBusyWaitMs, _serviceMsEstimate);
            double estimate = perRequest * Math.Max(1, requestsAhead);
            return (long)Math.Min(Math.Max(estimate, SharedWorkerHostProtocol.MinBusyWaitMs), _limits.QueueWaitMs);
        }

        // ---- Routes ----

        private void RegisterRouteLocked(SharedWorkerRequestRoute route, RouteInfo info)
        {
            _routes[route.ChildRequestId] = route;
            if (!string.IsNullOrWhiteSpace(route.ChildProgressToken)) _progressOwners[route.ChildProgressToken] = route;
            _routeInfo[route.ChildRequestId] = info;
        }

        private void UnregisterRouteLocked(string childRequestId)
        {
            if (_routes.TryRemove(childRequestId, out SharedWorkerRequestRoute route)) UnregisterRouteLocked(childRequestId, route);
            else _routeInfo.Remove(childRequestId);
        }

        private void UnregisterRouteLocked(string childRequestId, SharedWorkerRequestRoute route)
        {
            if (route != null && !string.IsNullOrWhiteSpace(route.ChildProgressToken)) _progressOwners.TryRemove(route.ChildProgressToken, out _);
            _routeInfo.Remove(childRequestId);
        }

        private void RemoveRoutesForAttachment(string attachmentId)
        {
            foreach (var entry in _routes)
                if (string.Equals(entry.Value.AttachmentId, attachmentId, StringComparison.Ordinal))
                {
                    _routes.TryRemove(entry.Key, out _);
                    _routeInfo.Remove(entry.Key);
                }
            foreach (var entry in _progressOwners)
                if (string.Equals(entry.Value.AttachmentId, attachmentId, StringComparison.Ordinal))
                    _progressOwners.TryRemove(entry.Key, out _);
        }

        private ISharedHostAttachment FindAttachment(string attachmentId)
        {
            foreach (var attachment in _attachments.Values)
                if (string.Equals(attachment.AttachmentId, attachmentId, StringComparison.Ordinal)) return attachment;
            return null;
        }

        // ---- Outbound frames ----

        private void Flush(Outbox outbox)
        {
            // Outside the host lock: an Attachment whose output is full stops, and its
            // transport then calls Detach, which takes the lock.
            if (outbox.Count == 0) return;
            var lines = new List<KeyValuePair<ISharedHostAttachment, string>>(outbox);
            outbox.Clear();
            foreach (var line in lines) line.Key.Enqueue(line.Value);
        }

        private string BuildBackpressureError(JToken clientRequestId)
        {
            var data = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.SharedWorkerBackpressure, null,
                new ConcurrencyErrorFields { QueueWaitMs = _limits.QueueWaitMs });
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = clientRequestId.DeepClone(),
                ["error"] = new JObject
                {
                    ["code"] = -32029,
                    ["message"] = "Shared Worker command queue is full; retry this operation.",
                    ["data"] = data
                }
            }.ToString(Formatting.None);
        }

        private string HostFrameLine(string type, JObject data)
        {
            data = data ?? new JObject();
            data["type"] = type;
            data["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion;
            data["identityKey"] = _options.IdentityKey;
            data["gxmcp"] = type;
            data["version"] = SharedWorkerHostProtocol.ProtocolVersion;
            data["key"] = _options.IdentityKey;
            return data.ToString(Formatting.None);
        }

        private void SendHostError(ISharedHostAttachment attachment, string message)
            => SendHostFrame(attachment, "host_error", new JObject { ["message"] = message });

        private void SendHostFrame(ISharedHostAttachment attachment, string type, JObject data)
            => attachment.Enqueue(HostFrameLine(type, data));

        private static void SendJson(ConcurrentDictionary<string, ISharedHostAttachment> attachments, string attachmentId, string json)
        {
            foreach (var attachment in attachments.Values)
                if (string.Equals(attachment.AttachmentId, attachmentId, StringComparison.Ordinal)) { attachment.Enqueue(json); return; }
        }

        /// <summary>
        /// How long a busy SDK lane may go without reporting movement before the broker
        /// calls it stalled. Issue #335. Matches the connection-recover window so the two
        /// paths cannot disagree about the same Worker.
        /// </summary>
        internal const int SdkStallAfterMs = 90_000;

        /// <summary>
        /// Issue #371. The grace period for a busy lane that has never reported progress,
        /// matching the Gateway's window so the two cannot disagree about the same child.
        /// Only operations that emit progress notifications prove they can move; a single
        /// object read, a save, or a COM call blocked behind a modal dialog emits none and
        /// would otherwise read as busy-unproven forever.
        /// </summary>
        internal const int NoProgressStallAfterMsDefault = 600_000;

        internal static readonly int NoProgressStallAfterMs = ResolveNoProgressStallAfterMs();

        private const string NoProgressStallEnvVar = "GXMCP_SDK_NO_PROGRESS_STALL_MS";

        private static int ResolveNoProgressStallAfterMs()
        {
            var raw = Environment.GetEnvironmentVariable(NoProgressStallEnvVar);
            if (int.TryParse(raw, out int configured) && configured > 0) return configured;
            return NoProgressStallAfterMsDefault;
        }

        /// <summary>
        /// Classifies the SDK lane. Issue #335.
        ///
        /// <para>
        /// The distinction that matters is progressing versus stalled. A lane that is busy
        /// and still reporting movement is legitimate long work, and treating it as a
        /// fault recycles a Worker in the middle of an operation - which destroys the
        /// operation, not just the Worker. A lane that has gone quiet past the window has
        /// not moved, and waiting longer only extends a dead Worker into an indefinite one.
        /// </para>
        ///
        /// <para>
        /// A busy lane with no observed progress is <c>busy-unproven</c>, not
        /// <c>busy-stalled</c>: one sample cannot tell a healthy short call from a
        /// deadlocked one, and guessing "stalled" would kill a Worker three seconds into
        /// ordinary work. Past <see cref="NoProgressStallAfterMs"/> of unproven progress it
        /// becomes <c>busy-stalled-unproven</c>, a recovery candidate - issue #371.
        /// </para>
        /// </summary>
        internal static string ClassifySdkState(bool active, bool sawProgress, long lastProgressMs, long elapsedMs = 0)
        {
            if (!active) return "idle";
            if (!sawProgress || lastProgressMs < 0)
                return elapsedMs >= NoProgressStallAfterMs ? "busy-stalled-unproven" : "busy-unproven";
            return lastProgressMs >= SdkStallAfterMs ? "busy-stalled" : "busy-progressing";
        }

        /// <summary>Why <see cref="ClassifySdkState"/> reached its verdict, or null.</summary>
        internal static string SdkStallReason(string sdkState)
            => sdkState == "busy-stalled-unproven" ? "no-progress-ceiling"
                : sdkState == "busy-stalled" ? "progress-stopped"
                : null;

        /// <summary>
        /// Builds the heartbeat acknowledgement, including the broker's view of the child.
        ///
        /// <para>
        /// Issue #335. Everything here is the broker's own observation - it owns the child
        /// process and the SDK lane, and a shared Gateway owns neither. The SDK section is
        /// the part that matters most: <c>progressing</c> and <c>stalled</c> are
        /// deliberately different answers, because treating a slow-but-progressing build as
        /// a fault recycles a Worker mid-operation and loses the work.
        /// </para>
        ///
        /// <para>
        /// Deliberately no exception can escape: a heartbeat that throws would take down
        /// the attachment loop, and a diagnostic that cannot be gathered must degrade to
        /// "unknown" rather than to a lost connection.
        /// </para>
        /// </summary>
        private JObject BuildHeartbeatAck(string attachmentId)
        {
            var ack = new JObject
            {
                ["attachmentId"] = attachmentId,
                ["utc"] = DateTime.UtcNow.ToString("O")
            };

            try
            {
                var child = _child;
                var pid = child?.Pid ?? 0;
                long workingSet = 0, privateBytes = 0;

                if (child != null && !child.HasExited)
                {
                    // Refreshed on read. A process that exited between the check and the
                    // read reports zero rather than a guess; the exit itself is what matters.
                    workingSet = child.WorkingSetBytes;
                    privateBytes = child.PrivateBytes;
                }

                // Issue #362. The SDK-lane section reads the child's own status, captured
                // by the broker from the child, not this process's statics.
                bool fresh = TryGetFreshChildStatus(out var sdk, out string unavailableReason);
                bool sdkActive = fresh && (sdk["sdkBusy"]?.ToObject<bool?>() ?? false);
                bool sawProgress = fresh && (sdk["sdkSawProgress"]?.ToObject<bool?>() ?? false);
                long lastProgressMs = fresh ? (sdk["sdkLastProgressMs"]?.ToObject<long?>() ?? -1) : -1;
                long sdkElapsedMs = fresh ? (sdk["sdkElapsedMs"]?.ToObject<long?>() ?? 0) : 0;
                string sdkState = fresh ? (sdk["sdkState"]?.ToString() ?? "unknown") : "unknown";
                long queueTotal = fresh ? (sdk["queueTotal"]?.ToObject<long?>() ?? 0) : 0;

                // Issue #362: no GC.Collect on the heartbeat path. Two forced blocking gen-2
                // collections per acknowledgement, per attachment, on the process that routes
                // every client's traffic is a latency spike in the routing path to obtain a
                // figure that described the broker rather than the child anyway.
                bool elected = fresh && ShouldElectRecycle(sdk, privateBytes);
                Interlocked.Increment(ref _heartbeatsServed);

                ack["supervision"] = new JObject
                {
                    // Issue #362: false when the child has not reported, or its last report
                    // is too old. Idle defaults were indistinguishable from a healthy child.
                    ["available"] = fresh,
                    ["unavailableReason"] = unavailableReason,
                    // Identity the Gateway cannot otherwise obtain.
                    ["childPid"] = pid,
                    ["childAlive"] = child != null && !child.HasExited,
                    ["attachments"] = _attachments.Count,
                    // Memory, in the same units the isolated path reports. Both are process
                    // figures for the child; the managed heap is the child's own report.
                    ["workingSetBytes"] = workingSet,
                    ["privateBytes"] = privateBytes,
                    // Issue #362: the broker cannot measure the child's managed heap - it is
                    // a different process - so it forwards what the child reported rather
                    // than its own GC figure wearing the child's PID.
                    ["childManagedHeapBytes"] = fresh ? (sdk["childManagedHeapBytes"]?.ToObject<long?>() ?? 0) : 0,
                    ["childManagedHeapSource"] = fresh ? "child" : "unavailable",
                    // Queue and SDK activity, all from the child.
                    ["queueTotal"] = queueTotal,
                    ["queueP0"] = fresh ? (sdk["queueP0"]?.ToObject<long?>() ?? 0) : 0,
                    ["queueP1"] = fresh ? (sdk["queueP1"]?.ToObject<long?>() ?? 0) : 0,
                    ["queueP2"] = fresh ? (sdk["queueP2"]?.ToObject<long?>() ?? 0) : 0,
                    ["sdkBusy"] = sdkActive,
                    ["sdkOperation"] = fresh ? sdk["sdkOperation"]?.ToString() : null,
                    ["sdkElapsedMs"] = sdkElapsedMs,
                    ["sdkSawProgress"] = sawProgress,
                    // -1 means "no progress has ever been observed for this operation",
                    // which is a different answer from 0 ("progress just now").
                    ["sdkLastProgressMs"] = lastProgressMs,
                    // The classification, computed where the evidence is. A Gateway
                    // receiving this does not have to reimplement the distinction, and
                    // cannot disagree with the broker about which child it describes.
                    // Issue #371: elapsed time is part of the input, so a lane that never
                    // emits progress can still be recognised as stuck.
                    ["sdkState"] = sdkState,
                    ["sdkStallReason"] = SdkStallReason(sdkState),
                    ["sdkNoProgressStallAfterMs"] = NoProgressStallAfterMs,
                    ["childStatusCapturedAtUtc"] = fresh ? _childStatusCapturedAtUtc.ToString("O") : null,
                    // Generation, so every attachment can tell it is looking at the same
                    // child. A Gateway must never unilaterally recycle another's child;
                    // this is the value they compare before acting.
                    ["brokerGeneration"] = Interlocked.Read(ref _brokerGeneration),
                    // The broker decides recycling. A Gateway may *ask*; only this flag
                    // says the broker has already elected a victim, and only one
                    // attachment ever sees it true for a given sweep.
                    ["recycleElected"] = elected,
                    ["capturedAtUtc"] = DateTime.UtcNow.ToString("O")
                };
            }
            catch (Exception ex)
            {
                ack["supervision"] = new JObject
                {
                    ["available"] = false,
                    ["error"] = ex.GetType().Name
                };
            }

            return ack;
        }

        private void BroadcastHostError(string message)
        {
            foreach (var attachment in _attachments.Values)
                SendHostError(attachment, message);
        }

        private void WriteRecordLocked()
        {
            if (_record == null) return;
            RefreshRecordLocked();
            _record.UpdatedUtc = DateTime.UtcNow;
            try
            {
                _recordWriter.Write(_record);
                _recordWritten = true;
            }
            catch (Exception ex) { HostLog("registry record write failed: " + ex.Message); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { _expiryTimer.Dispose(); } catch { }
            try { _idleRecordTimer.Dispose(); } catch { }
            try { _supervisorTimer.Dispose(); } catch { }
            try { _admissionTimer.Dispose(); } catch { }
            try { _wakeSubscription?.Dispose(); } catch { }
            try { _evictSubscription?.Dispose(); } catch { }
            foreach (var attachment in _attachments.Values)
            {
                try { attachment.Stop(); } catch { }
            }
            try { _childInbox.CompleteAdding(); } catch { }
            ISharedHostChild child;
            bool deleteRecord;
            IDisposable hostMutex;
            lock (_hostLock)
            {
                child = _child;
                deleteRecord = _recordWritten;
                hostMutex = _hostMutex;
                _hostMutex = null;
            }
            if (child != null)
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                try { child.Dispose(); } catch { }
            }
            // Only the record this runtime wrote, and before the mutex is released: a host that acquires
            // the mutex next must never lose its own record to this one.
            if (deleteRecord)
            {
                try { _recordWriter.Delete(); } catch { }
            }
            if (hostMutex != null)
            {
                try { hostMutex.Dispose(); } catch { }
            }
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private string BuildChildFailureDiagnostic(ISharedHostChild child)
        {
            int exitCode = -1;
            try { exitCode = child != null && child.HasExited ? child.ExitCode : -1; } catch { }
            string stderr = Volatile.Read(ref _lastChildError) ?? string.Empty;
            string detail = "stage=child_exit exitCode=" + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(stderr)) detail += " stderr=" + stderr;
            return detail.Length <= 4096 ? detail : detail.Substring(0, 4096) + "…";
        }

        private static string RedactDiagnostic(string value)
        {
            // Same rule as PreviewService.LogValue: this copy was the only one
            // that knew about connectionstring, so a connection string reaching
            // a log was masked only on the paths that went through here.
            string redacted = LogRedaction.Redact(value);
            return redacted.Length <= 2048 ? redacted : redacted.Substring(0, 2048) + "…";
        }

        private static string Normalize(string value)
        {
            try { return Path.GetFullPath(value).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return (value ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant(); }
        }
        private static string NormalizeToken(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

        private static void HostLog(string message) { try { Console.Error.WriteLine("[SharedWorkerHost] " + message); } catch { } }

        // ---- Ownership seams of the task 3.5 constructor ----

        private readonly struct GatewayProcess
        {
            internal GatewayProcess(int pid, long startTimeUtcTicks)
            {
                Pid = pid;
                StartTimeUtcTicks = startTimeUtcTicks;
            }

            internal int Pid { get; }
            internal long StartTimeUtcTicks { get; }
        }

        // Projects each registry v2 write onto a v1 ISharedHostRegistryWriter (same state, PIDs, start times,
        // pipe name and generation), so the PR A tests keep observing the record they were written against.
        private sealed class RegistryWriterProjection : ISharedHostRecordWriter
        {
            private readonly ISharedHostRegistryWriter _inner;

            internal RegistryWriterProjection(ISharedHostRegistryWriter inner) { _inner = inner; }

            public void Write(SharedHostRecord record)
            {
                if (record == null) throw new ArgumentNullException(nameof(record));
                _inner.Write(new SharedWorkerHostRegistryRecord
                {
                    Identity = new SharedWorkerHostIdentity
                    {
                        WorkerExecutable = record.Identity.WorkerExecutable,
                        KbPath = record.Identity.KbPath,
                        InstallationPath = record.Identity.InstallationPath,
                        Driver = record.Identity.Driver,
                        Major = record.Identity.Major,
                        Key = record.Identity.Key
                    },
                    HostPid = record.HostPid,
                    HostStartTimeUtcTicks = record.HostStartTimeUtcTicks,
                    WorkerPid = record.WorkerPid,
                    WorkerStartTimeUtcTicks = record.WorkerStartTimeUtcTicks,
                    PipeName = record.PipeName,
                    Generation = record.Generation,
                    UpdatedUtc = record.UpdatedUtc,
                    State = record.State
                });
            }

            public void Delete() => _inner.Delete();
        }

        // Always grants: the task 3.5 tests run many runtimes for one KB in one process.
        private sealed class GrantingMutexFactory : ISharedHostMutexFactory
        {
            internal static readonly GrantingMutexFactory Instance = new GrantingMutexFactory();

            public IDisposable TryAcquire(string name) => new Granted();

            private sealed class Granted : IDisposable
            {
                public void Dispose() { }
            }
        }

        // ---- Runtime records ----

        private sealed class Outbox : List<KeyValuePair<ISharedHostAttachment, string>>
        {
            internal void Add(ISharedHostAttachment attachment, string line)
                => Add(new KeyValuePair<ISharedHostAttachment, string>(attachment, line));
        }

        private sealed class ChildInboxItem
        {
            internal ChildInboxItem(ISharedHostChild child, string line)
            {
                Child = child;
                Line = line;
            }

            internal ISharedHostChild Child { get; }
            internal string Line { get; }
        }

        // A queued SDK_Operation: the rewritten frame stays here, not in QueuedRequest.Payload,
        // so the release path adds _meta.busyWaitMs without parsing it again.
        private sealed class QueuedEntry
        {
            internal QueuedEntry(SharedWorkerRequestRoute route, JObject childRequest, string sessionId, string method, string action)
            {
                Route = route;
                ChildRequest = childRequest;
                SessionId = sessionId;
                Method = method;
                Action = action;
            }

            internal SharedWorkerRequestRoute Route { get; }
            internal JObject ChildRequest { get; }
            internal string SessionId { get; }
            internal string Method { get; }
            internal string Action { get; }
        }

        private sealed class RouteInfo
        {
            internal RouteInfo(string method, string action, bool isSdk, long enqueuedAtMs, long releasedAtMs)
            {
                Method = method;
                Action = action;
                IsSdk = isSdk;
                EnqueuedAtMs = enqueuedAtMs;
                ReleasedAtMs = releasedAtMs;
            }

            internal string Method { get; }
            internal string Action { get; }
            internal bool IsSdk { get; }
            internal long EnqueuedAtMs { get; }
            internal long ReleasedAtMs { get; }
        }
    }

    internal sealed class SharedWorkerHostAttachment : ISharedHostAttachment
    {
        private readonly SharedWorkerHostRuntime _host;
        private readonly NamedPipeServerStream _pipe;
        private readonly int _attachTimeoutMs;
        private readonly BlockingCollection<string> _output = new BlockingCollection<string>(128);
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private int _stopped;
        private long _sequence;

        internal SharedWorkerHostAttachment(SharedWorkerHostRuntime host, NamedPipeServerStream pipe, int attachTimeoutMs)
        {
            _host = host;
            _pipe = pipe;
            _attachTimeoutMs = attachTimeoutMs;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, true) { AutoFlush = true, NewLine = "\n" };
            ConnectionId = Guid.NewGuid().ToString("N");
        }

        public string ConnectionId { get; }
        public string AttachmentId { get; set; } = string.Empty;
        public long NextSequence() => Interlocked.Increment(ref _sequence);

        internal async Task RunAsync()
        {
            var writer = new Thread(WriterLoop) { IsBackground = true, Name = "SharedWorkerAttachmentWriter" };
            writer.Start();
            try
            {
                string first = await ReadLineWithTimeoutAsync().ConfigureAwait(false);
                if (first == null) return;
                if (!SharedWorkerHostProtocol.TryParseEnvelope(first, out SharedWorkerEnvelope envelope, out string error)
                    || !string.Equals(envelope.Type, "attach", StringComparison.Ordinal)
                    || !_host.Attach(this, envelope, out error))
                {
                    EnqueueError(error);
                    return;
                }
                string line;
                while (Volatile.Read(ref _stopped) == 0 && (line = await _reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    _host.Receive(this, line);
            }
            catch (Exception ex)
            {
                EnqueueError("attachment closed: " + ex.Message);
            }
            finally { Stop(); _host.Detach(this); }
        }

        private async Task<string> ReadLineWithTimeoutAsync()
        {
            Task<string> read = _reader.ReadLineAsync();
            Task completed = await Task.WhenAny(read, Task.Delay(_attachTimeoutMs)).ConfigureAwait(false);
            if (completed != read)
            {
                EnqueueError("attachment handshake timed out after " + _attachTimeoutMs + "ms");
                Stop();
                return null;
            }
            return await read.ConfigureAwait(false);
        }

        public bool Enqueue(string line)
        {
            if (Volatile.Read(ref _stopped) != 0) return false;
            try
            {
                if (_output.TryAdd(line)) return true;
            }
            catch (InvalidOperationException) { return false; }
            Stop();
            return false;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try { _output.CompleteAdding(); } catch { }
            try { _pipe.Dispose(); } catch { }
        }

        private void EnqueueError(string message)
        {
            try
            {
                var error = new JObject
                {
                    ["type"] = "host_error",
                    ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["gxmcp"] = "host_error",
                    ["version"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["message"] = message ?? "attachment rejected"
                };
                _output.TryAdd(error.ToString(Formatting.None));
            }
            catch { }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (string line in _output.GetConsumingEnumerable())
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
            }
            catch { }
            finally { Stop(); }
        }
    }

    internal sealed class SharedWorkerHostIdentity
    {
        public string WorkerExecutable { get; set; } = string.Empty;
        public string KbPath { get; set; } = string.Empty;
        public string InstallationPath { get; set; } = string.Empty;
        public string Driver { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
    }

    internal sealed class SharedWorkerHostRegistryRecord
    {
        public SharedWorkerHostIdentity Identity { get; set; } = new SharedWorkerHostIdentity();
        public int HostPid { get; set; }
        public long HostStartTimeUtcTicks { get; set; }
        public int WorkerPid { get; set; }
        public long WorkerStartTimeUtcTicks { get; set; }
        public string PipeName { get; set; } = string.Empty;
        public long Generation { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string State { get; set; } = string.Empty;
    }
}
