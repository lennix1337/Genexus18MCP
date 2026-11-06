using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using GxMcp.Gateway.Tests.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    // Process harness for the shared-host checks of Requirement 13 criteria 3 to 8
    // (.kiro/specs/concurrent-multi-session-workers, tasks 7.3 to 7.8).
    //
    // Processes:
    // - Brokers are real GxMcp.SharedHost.TestHost processes in --shared-host mode, with the
    //   TestHost path as the Worker executable, so every Worker child is the scripted fake
    //   (src/GxMcp.SharedHost.TestHost/README.md). The harness starts a broker itself
    //   (StartBroker), or a Gateway client elects and starts it through the production
    //   SharedWorkerHostLauncher.ConnectV2 sequence.
    // - Gateway clients are separate processes: this test assembly run through
    //   `dotnet GxMcp.Gateway.Tests.dll --shared-host-client <options.json>`
    //   (SharedHostClientProgram below; the project sets GenerateProgramFile=false for it).
    //   Each client runs the Gateway's own ConnectV2 and SharedWorkerConnection code, and the
    //   harness drives it over stdin/stdout with one JSON object per line.
    //
    // Why not the real GxMcp.Gateway.exe: its shared-host registry root, ownership registry root
    // and StateScope directories come from Environment.GetFolderPath(LocalApplicationData), which
    // reads the known folder and ignores an overridden LOCALAPPDATA variable, and the exe has no
    // setting that moves them. Driving it would read and write the user's real
    // %LOCALAPPDATA%\GxMcp records. The client mode passes the temporary roots through
    // SharedWorkerRecoveryContext instead. The one production step it replaces is
    // SharedWorkerHostLauncher.StartHostProcess: the harness starter runs the same
    // BuildHostArguments command line plus --registry-dir, but skips the SDK Worker environment
    // (GX_*, GXMCP_JOBS_DIR and the StateScope directory it creates), which the fake child ignores.
    //
    // Isolation: every path lives under %TEMP%\gxmcp-shh-<guid>. Brokers always get --registry-dir
    // (on .NET Framework the broker's known-folder lookup also ignores LOCALAPPDATA), and every
    // spawned process gets LOCALAPPDATA pointed at <root>\localappdata as a second guard.
    //
    // Cleanup kills only processes this harness started or saw: clients and brokers it started,
    // brokers a client reported starting, the host and Worker PIDs of attach acknowledgements, and
    // the PIDs of records in its own registry whose start time matches and whose image is the
    // TestHost. Each kill checks PID plus start time first; nothing is matched by process name.
    //
    // Tests that use the harness carry [Trait("Category", "ProcessSmoke")] and the
    // SharedHostQueueProcessSmokeSerial collection, and stay within the 10 s per-test budget of
    // scripts/ci/test-time-budget.json (there is no separate ProcessSmoke budget).
    internal sealed class SharedHostProcessHarness : IDisposable
    {
        internal const string Driver = "sdk";
        internal const string Major = "18";
        internal const int DefaultTimeoutMs = 20_000;

        private readonly object _gate = new object();
        private readonly List<TrackedProcess> _tracked = new List<TrackedProcess>();
        private readonly List<SharedHostGatewayClient> _clients = new List<SharedHostGatewayClient>();
        private readonly List<string> _kbDirectories = new List<string>();
        private readonly ITestOutputHelper? _output;
        private readonly ProcessIdentity _probe = new ProcessIdentity();
        private int _kbCounter;
        private int _clientCounter;
        private bool _cleaned;

        private SharedHostProcessHarness(string testHostPath, ITestOutputHelper? output)
        {
            _output = output;
            TestHostPath = testHostPath;
            Root = Path.Combine(Path.GetTempPath(), "gxmcp-shh-" + Guid.NewGuid().ToString("N"));
            RegistryDir = Path.Combine(Root, "registry");
            OwnershipDir = Path.Combine(Root, "ownership");
            LocalAppDataDir = Path.Combine(Root, "localappdata");
            LogDir = Path.Combine(Root, "logs");
            InstallationDir = Path.Combine(Root, "install");
            foreach (string dir in new[] { RegistryDir, OwnershipDir, LocalAppDataDir, LogDir, InstallationDir })
                Directory.CreateDirectory(dir);
        }

        internal string TestHostPath { get; }
        internal string Root { get; }
        internal string RegistryDir { get; }
        internal string OwnershipDir { get; }
        internal string LocalAppDataDir { get; }
        internal string LogDir { get; }
        internal string InstallationDir { get; }

        /// <summary>
        /// Null when the TestHost is not built (the same gate as the other process smokes);
        /// GXMCP_SHAREDHOST_TESTHOST_EXE makes a missing or relative path an error.
        /// </summary>
        internal static SharedHostProcessHarness? TryCreate(ITestOutputHelper? output = null)
        {
            if (!OperatingSystem.IsWindows()) return null;
            string? exe = SharedHostQueueProcessSmokeTests.LocateTestHost();
            return exe == null ? null : new SharedHostProcessHarness(exe, output);
        }

        // ---- KB directories and identities ----

        /// <summary>Creates a neutral, empty KB directory under the harness root.</summary>
        internal string CreateKbDirectory()
        {
            int n = Interlocked.Increment(ref _kbCounter);
            string dir = Path.Combine(Root, "kb-" + n.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            lock (_gate) _kbDirectories.Add(dir);
            return dir;
        }

        internal SharedWorkerIdentity IdentityFor(string kbDir) =>
            SharedWorkerIdentity.Create(TestHostPath, kbDir, InstallationDir, Driver, Major);

        internal string RecordPath(string kbDir) =>
            SharedWorkerRegistry.GetRecordPathV2(IdentityFor(kbDir).KbKey, RegistryDir);

        // ---- Brokers ----

        /// <summary>
        /// Starts a broker for <paramref name="kbDir"/> with the production command line
        /// (SharedWorkerHostLauncher.BuildHostArguments) plus --registry-dir, and waits for its record.
        /// </summary>
        internal async Task<SharedHostBroker> StartBrokerAsync(string kbDir, SharedHostHarnessSettings? settings = null)
        {
            settings ??= new SharedHostHarnessSettings();
            var identity = IdentityFor(kbDir);
            string arguments = SharedWorkerHostLauncher.BuildHostArguments(
                    settings.ToConfiguration(TestHostPath, InstallationDir), identity, TestHostPath, kbDir,
                    InstallationDir, Driver, Major)
                + " --registry-dir \"" + RegistryDir + "\"";
            var stderr = new StringBuilder();
            var start = new ProcessStartInfo
            {
                FileName = TestHostPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(TestHostPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ApplyChildEnvironment(start, settings);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + TestHostPath);
            var tracked = Track(process, "broker");
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            _ = process.StandardOutput.ReadToEndAsync();
            Log("broker started pid=" + tracked.Pid + " kb=" + Path.GetFileName(kbDir));

            SharedHostRecord record = await SharedHostQueueProcessSmokeTests.WaitForRecordAsync(
                SharedWorkerRegistry.GetRecordPathV2(identity.KbKey, RegistryDir), process, stderr);
            TrackRecordProcesses(record);
            return new SharedHostBroker(process, tracked, identity, kbDir, stderr);
        }

        // ---- Gateway clients ----

        /// <summary>Starts one Gateway client process and waits for its ready line.</summary>
        internal async Task<SharedHostGatewayClient> StartClientAsync(SharedHostClientOptions? options = null)
        {
            options ??= new SharedHostClientOptions();
            int n = Interlocked.Increment(ref _clientCounter);
            string name = "client-" + n.ToString(CultureInfo.InvariantCulture);
            string optionsPath = Path.Combine(Root, name + ".json");
            var document = new JObject
            {
                ["testHost"] = TestHostPath,
                ["installation"] = InstallationDir,
                ["registryDir"] = RegistryDir,
                ["ownershipDir"] = OwnershipDir,
                ["sharedHost"] = options.Settings.ToSection(),
                ["workerIdleTimeoutMinutes"] = options.Settings.WorkerIdleTimeoutMinutes,
                ["availableMemoryMb"] = options.InjectedAvailableMemoryMb.HasValue
                    ? new JValue(options.InjectedAvailableMemoryMb.Value)
                    : JValue.CreateNull()
            };
            File.WriteAllText(optionsPath, document.ToString(Formatting.Indented), new UTF8Encoding(false));

            string assembly = typeof(SharedHostProcessHarness).Assembly.Location;
            var start = new ProcessStartInfo
            {
                FileName = LocateDotnetHost(),
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add(SharedHostClientProgram.ModeArgument);
            start.ArgumentList.Add(optionsPath);
            ApplyChildEnvironment(start, options.Settings);
            // Program.Log of the client goes here instead of next to the test binaries.
            start.Environment["GXMCP_LOG_DIR"] = LogDir;

            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start a Gateway client.");
            var tracked = Track(process, "gateway-client");
            var client = new SharedHostGatewayClient(this, name, process, tracked);
            lock (_gate) _clients.Add(client);
            await client.WaitReadyAsync(DefaultTimeoutMs);
            Log(name + " started pid=" + tracked.Pid);
            return client;
        }

        /// <summary>Starts <paramref name="count"/> clients in parallel.</summary>
        internal async Task<IReadOnlyList<SharedHostGatewayClient>> StartClientsAsync(int count, SharedHostClientOptions? options = null)
        {
            var tasks = Enumerable.Range(0, count).Select(_ => StartClientAsync(options)).ToArray();
            return await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Forcibly terminates the client process only (no tree, no shutdown handling), so brokers it
        /// started keep running, like a Gateway that crashed.
        /// </summary>
        internal void KillClient(SharedHostGatewayClient client)
        {
            if (!KillOwned(client.Tracked, entireTree: false))
                throw new InvalidOperationException(client.Name + " was not running with its recorded start time.");
            client.Process.WaitForExit(5000);
        }

        // ---- Sentinels and planted records ----

        /// <summary>An unrelated long-running process (ping.exe) for the "never kills an unrelated process" checks.</summary>
        internal TrackedProcess StartSentinel()
        {
            var start = new ProcessStartInfo
            {
                FileName = "ping.exe",
                Arguments = "-n 300 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            start.Environment["LOCALAPPDATA"] = LocalAppDataDir;
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the sentinel.");
            _ = process.StandardOutput.ReadToEndAsync();
            return Track(process, "sentinel");
        }

        /// <summary>A start time that differs from <paramref name="startTimeUtcTicks"/> by five minutes.</summary>
        internal static long DifferentStartTime(long startTimeUtcTicks) => startTimeUtcTicks - TimeSpan.FromMinutes(5).Ticks;

        /// <summary>Writes a registry v2 record for <paramref name="kbDir"/> into the harness registry.</summary>
        internal string PlantSharedHostRecord(
            string kbDir,
            int hostPid,
            long hostStartTimeUtcTicks,
            int workerPid = 0,
            long workerStartTimeUtcTicks = 0,
            string state = SharedHostRecordState.Ready,
            int launchedByGatewayPid = 0)
        {
            var identity = IdentityFor(kbDir);
            var record = new SharedHostRecord
            {
                KbKey = identity.KbKey,
                Identity = new SharedHostRecordIdentity
                {
                    Key = identity.Key,
                    KbPath = identity.KbPath,
                    WorkerExecutable = identity.WorkerExecutable,
                    InstallationPath = identity.InstallationPath,
                    Driver = identity.Driver,
                    Major = identity.Major
                },
                HostPid = hostPid,
                HostStartTimeUtcTicks = hostStartTimeUtcTicks,
                LaunchedByGatewayPid = launchedByGatewayPid,
                WorkerPid = workerPid,
                WorkerStartTimeUtcTicks = workerStartTimeUtcTicks,
                PipeName = SharedWorkerRegistry.PipeNameV2(identity),
                Generation = 1,
                State = state,
                UpdatedUtc = DateTime.UtcNow
            };
            return SharedWorkerRegistry.WriteRecordV2(record, RegistryDir);
        }

        /// <summary>Writes an Isolated_Mode ownership record for <paramref name="kbDir"/> into the harness ownership root.</summary>
        internal string PlantOwnershipRecord(
            string kbDir, int gatewayPid, long gatewayStartTimeUtcTicks, int workerPid = 0, long workerStartTimeUtcTicks = 0)
        {
            string key = WorkerOwnershipRegistry.BuildKey(TestHostPath, kbDir);
            var record = new WorkerOwnershipRecord
            {
                Key = key,
                GatewayPid = gatewayPid,
                GatewayStartTimeUtcTicks = gatewayStartTimeUtcTicks,
                WorkerPid = workerPid,
                WorkerStartTimeUtcTicks = workerStartTimeUtcTicks,
                WorkerExecutable = SharedWorkerIdentity.NormalizePath(TestHostPath),
                KbPath = SharedWorkerIdentity.NormalizePath(kbDir)
            };
            string path = Path.Combine(OwnershipDir, key + ".json");
            File.WriteAllText(path, JsonConvert.SerializeObject(record), new UTF8Encoding(false));
            return path;
        }

        // ---- Observation ----

        internal SharedHostRecord? ReadRecord(string kbDir) => RecordReadResult.ReadFile(RecordPath(kbDir)).Record;

        /// <summary>Every parsed registry v2 record of the harness registry.</summary>
        internal IReadOnlyList<SharedHostRecord> ReadRecords() =>
            RecordFiles(RegistryDir)
                .Select(f => RecordReadResult.ReadFile(f).Record)
                .Where(r => r != null)
                .Select(r => r!)
                .ToList();

        internal async Task<SharedHostRecord> WaitForRecordAsync(string kbDir, Func<SharedHostRecord, bool> match, string what, int timeoutMs = DefaultTimeoutMs)
        {
            var clock = Stopwatch.StartNew();
            SharedHostRecord? last = null;
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                last = ReadRecord(kbDir);
                if (last != null && match(last))
                {
                    TrackRecordProcesses(last);
                    return last;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("the record of " + Path.GetFileName(kbDir) + " never showed " + what
                + "; last: " + (last == null ? "none" : last.ToJson()));
        }

        /// <summary>Probe result of <paramref name="pid"/>; null when it is not running.</summary>
        internal long? StartTimeOf(int pid)
        {
            var result = _probe.Probe(pid);
            return result.Status == ProcessProbeStatus.Running ? result.StartTimeUtcTicks : (long?)null;
        }

        internal bool IsRunning(TrackedProcess process) => IsSameProcess(process.Pid, process.StartTimeUtcTicks);

        internal bool IsSameProcess(int pid, long startTimeUtcTicks)
        {
            long? actual = StartTimeOf(pid);
            return actual.HasValue && startTimeUtcTicks > 0 && SharedHostRecordClassifier.SameMillisecond(startTimeUtcTicks, actual.Value);
        }

        internal IReadOnlyList<TrackedProcess> TrackedProcesses()
        {
            lock (_gate) return _tracked.ToList();
        }

        /// <summary>Tracked processes that still run with their recorded start time.</summary>
        internal IReadOnlyList<TrackedProcess> RemainingProcesses() => TrackedProcesses().Where(IsRunning).ToList();

        /// <summary>Record files (registry v2 and ownership) still present under the harness root.</summary>
        internal IReadOnlyList<string> RemainingRecordFiles() =>
            RecordFiles(RegistryDir).Concat(RecordFiles(OwnershipDir)).ToList();

        /// <summary>
        /// Records of this harness's KB directories in the user's real registries. Always expected empty.
        /// </summary>
        internal IReadOnlyList<string> RealRegistryRecords()
        {
            List<string> kbs;
            lock (_gate) kbs = _kbDirectories.ToList();
            var found = new List<string>();
            foreach (string kb in kbs)
            {
                string v2 = SharedWorkerRegistry.GetRecordPathV2(IdentityFor(kb).KbKey);
                if (File.Exists(v2)) found.Add(v2);
                string ownership = Path.Combine(WorkerOwnershipRegistry.DefaultRoot, WorkerOwnershipRegistry.BuildKey(TestHostPath, kb) + ".json");
                if (File.Exists(ownership)) found.Add(ownership);
            }
            return found;
        }

        // ---- Tracking ----

        internal TrackedProcess Track(Process process, string role)
        {
            long ticks;
            try { ticks = process.StartTime.ToUniversalTime().Ticks; }
            catch (InvalidOperationException) { ticks = 0; }
            catch (System.ComponentModel.Win32Exception) { ticks = 0; }
            return Track(process.Id, ticks, role, process);
        }

        internal TrackedProcess Track(int pid, long startTimeUtcTicks, string role, Process? handle = null)
        {
            lock (_gate)
            {
                var existing = _tracked.FirstOrDefault(t => t.Pid == pid && SharedHostRecordClassifier.SameMillisecond(t.StartTimeUtcTicks, startTimeUtcTicks));
                if (existing != null) return existing;
                var tracked = new TrackedProcess(pid, startTimeUtcTicks, role, handle);
                _tracked.Add(tracked);
                return tracked;
            }
        }

        // The host and Worker of a record read from this harness's registry.
        private void TrackRecordProcesses(SharedHostRecord record)
        {
            if (record.HostPid > 0 && record.HostStartTimeUtcTicks > 0) Track(record.HostPid, record.HostStartTimeUtcTicks, "broker");
            if (record.WorkerPid > 0 && record.WorkerStartTimeUtcTicks > 0) Track(record.WorkerPid, record.WorkerStartTimeUtcTicks, "worker");
        }

        /// <summary>
        /// Tracks the host and Worker PIDs of the harness registry records that run with the recorded start time
        /// and whose image is the TestHost. Returns the newly tracked processes. A planted record that names an
        /// unrelated process (other start time or other image) is never adopted.
        /// </summary>
        internal IReadOnlyList<TrackedProcess> AdoptRecordProcesses()
        {
            var adopted = new List<TrackedProcess>();
            foreach (var record in ReadRecords())
            {
                foreach (var (pid, ticks, role) in new[]
                {
                    (record.HostPid, record.HostStartTimeUtcTicks, "broker"),
                    (record.WorkerPid, record.WorkerStartTimeUtcTicks, "worker")
                })
                {
                    if (pid <= 0 || !IsSameProcess(pid, ticks) || !HasTestHostImage(pid)) continue;
                    bool known;
                    lock (_gate) known = _tracked.Any(t => t.Pid == pid && SharedHostRecordClassifier.SameMillisecond(t.StartTimeUtcTicks, ticks));
                    if (known) continue;
                    adopted.Add(Track(pid, ticks, role));
                }
            }
            return adopted;
        }

        private bool HasTestHostImage(int pid) =>
            ProcessIdentity.TryGetImagePath(pid, out string? image, out _)
            && string.Equals(Path.GetFullPath(image!), Path.GetFullPath(TestHostPath), StringComparison.OrdinalIgnoreCase);

        /// <summary>Kills <paramref name="process"/> when it still runs with its recorded start time.</summary>
        internal bool KillOwned(TrackedProcess process, bool entireTree = true)
        {
            if (process.StartTimeUtcTicks <= 0) return false;
            Process target;
            try { target = Process.GetProcessById(process.Pid); }
            catch (ArgumentException) { return false; }
            using (target)
            {
                try
                {
                    if (target.HasExited) return false;
                    if (!SharedHostRecordClassifier.SameMillisecond(process.StartTimeUtcTicks, target.StartTime.ToUniversalTime().Ticks))
                        return false;
                    target.Kill(entireTree);
                    target.WaitForExit(5000);
                    return true;
                }
                catch (InvalidOperationException) { return false; }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
        }

        // ---- Cleanup ----

        /// <summary>
        /// Stops every client, every tracked and adopted process, deletes the harness root, and reports what is
        /// left. Idempotent; Dispose calls it.
        /// </summary>
        internal SharedHostCleanupReport Cleanup()
        {
            var clock = Stopwatch.StartNew();
            List<SharedHostGatewayClient> clients;
            lock (_gate)
            {
                if (_cleaned) return SharedHostCleanupReport.AlreadyCleaned;
                _cleaned = true;
                clients = _clients.ToList();
            }

            foreach (var client in clients) client.RequestExit();
            foreach (var client in clients)
            {
                try { client.Process.WaitForExit(1000); } catch (InvalidOperationException) { }
            }

            AdoptRecordProcesses();
            var killed = new List<TrackedProcess>();
            // Newest first: clients and brokers before the children they started.
            foreach (var process in TrackedProcesses().Reverse())
                if (KillOwned(process)) killed.Add(process);

            var remainingProcesses = RemainingProcesses();
            var recordsBeforeDelete = RemainingRecordFiles();
            var realRecords = RealRegistryRecords();
            foreach (var client in clients) client.Dispose();
            foreach (var process in TrackedProcesses()) process.Handle?.Dispose();

            for (int i = 0; i < 25 && Directory.Exists(Root); i++)
            {
                try { Directory.Delete(Root, recursive: true); }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }

            var report = new SharedHostCleanupReport(
                TrackedProcesses(), killed, remainingProcesses, recordsBeforeDelete, RemainingRecordFiles(),
                realRecords, Directory.Exists(Root));
            Log("cleanup " + clock.ElapsedMilliseconds + " ms: killed=" + killed.Count
                + " remainingProcesses=" + remainingProcesses.Count + " recordsRemoved=" + recordsBeforeDelete.Count
                + " rootLeft=" + report.RootLeft);
            Log("tracked " + string.Join(" ", report.Tracked.Select(t =>
                t + "@" + t.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture))));
            return report;
        }

        public void Dispose() => Cleanup();

        // ---- Helpers ----

        internal void Log(string line) => _output?.WriteLine("[harness] " + line);

        private void ApplyChildEnvironment(ProcessStartInfo start, SharedHostHarnessSettings settings)
        {
            start.Environment["LOCALAPPDATA"] = LocalAppDataDir;
            // A broker passes its environment to its child, which reads the script from it.
            start.Environment["GXMCP_TESTHOST_SCRIPT"] = settings.ChildScript.ToString(Formatting.None);
        }

        private static IEnumerable<string> RecordFiles(string dir)
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            // "*.json" can also match longer extensions on Windows; temp files are never records.
            return Directory.GetFiles(dir, "*.json")
                .Where(f => string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase));
        }

        // dotnet test sets DOTNET_HOST_PATH for the test host; a direct vstest run falls back to PATH.
        private static string LocateDotnetHost()
        {
            string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(host) && File.Exists(host)) return host;
            string? current = Environment.ProcessPath;
            if (current != null && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
                return current;
            return "dotnet";
        }
    }

    /// <summary>One process the harness started or saw, identified by PID plus start time.</summary>
    internal sealed class TrackedProcess
    {
        internal TrackedProcess(int pid, long startTimeUtcTicks, string role, Process? handle)
        {
            Pid = pid;
            StartTimeUtcTicks = startTimeUtcTicks;
            Role = role;
            Handle = handle;
        }

        internal int Pid { get; }
        internal long StartTimeUtcTicks { get; }
        internal string Role { get; }
        internal Process? Handle { get; }

        public override string ToString() => Role + ":" + Pid.ToString(CultureInfo.InvariantCulture);
    }

    internal sealed class SharedHostCleanupReport
    {
        internal static readonly SharedHostCleanupReport AlreadyCleaned = new SharedHostCleanupReport(
            Array.Empty<TrackedProcess>(), Array.Empty<TrackedProcess>(), Array.Empty<TrackedProcess>(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false);

        internal SharedHostCleanupReport(
            IReadOnlyList<TrackedProcess> tracked,
            IReadOnlyList<TrackedProcess> killed,
            IReadOnlyList<TrackedProcess> remainingProcesses,
            IReadOnlyList<string> recordsRemoved,
            IReadOnlyList<string> remainingRecordFiles,
            IReadOnlyList<string> realRegistryRecords,
            bool rootLeft)
        {
            Tracked = tracked;
            Killed = killed;
            RemainingProcesses = remainingProcesses;
            RecordsRemoved = recordsRemoved;
            RemainingRecordFiles = remainingRecordFiles;
            RealRegistryRecords = realRegistryRecords;
            RootLeft = rootLeft;
        }

        internal IReadOnlyList<TrackedProcess> Tracked { get; }
        internal IReadOnlyList<TrackedProcess> Killed { get; }
        internal IReadOnlyList<TrackedProcess> RemainingProcesses { get; }

        /// <summary>Record files the cleanup found under the harness root and deleted with it.</summary>
        internal IReadOnlyList<string> RecordsRemoved { get; }
        internal IReadOnlyList<string> RemainingRecordFiles { get; }
        internal IReadOnlyList<string> RealRegistryRecords { get; }
        internal bool RootLeft { get; }

        internal bool IsClean => RemainingProcesses.Count == 0 && RemainingRecordFiles.Count == 0
            && RealRegistryRecords.Count == 0 && !RootLeft;

        public override string ToString() =>
            "remainingProcesses=[" + string.Join(", ", RemainingProcesses) + "] remainingRecords=["
            + string.Join(", ", RemainingRecordFiles) + "] realRegistryRecords=[" + string.Join(", ", RealRegistryRecords)
            + "] rootLeft=" + RootLeft;
    }

    /// <summary>Server.SharedHost settings and the fake child script for brokers and clients.</summary>
    internal sealed class SharedHostHarnessSettings
    {
        internal int? QueueWaitMs { get; set; }
        internal int? QueueDepth { get; set; }
        internal int? SessionQueueCap { get; set; }
        internal int? MaxWorkers { get; set; }

        /// <summary>Defaults to 256 MB, the lowest allowed floor, so a busy machine does not refuse unrelated checks.</summary>
        internal int? MemoryFloorMb { get; set; } = 256;
        internal int? ColdStartConcurrency { get; set; }
        internal int? AttachReadyTimeoutSeconds { get; set; }

        /// <summary>Server.WorkerIdleTimeoutMinutes (broker --idle-ms); cleanup stops brokers either way.</summary>
        internal int WorkerIdleTimeoutMinutes { get; set; } = 5;

        /// <summary>GXMCP_TESTHOST_SCRIPT for the fake child (README.md, "Script").</summary>
        internal JObject ChildScript { get; set; } = new JObject { ["readyDelayMs"] = 100 };

        internal JObject ToSection()
        {
            var section = new JObject();
            void Add(string key, int? value) { if (value.HasValue) section[key] = value.Value; }
            Add(SharedHostSettingsResolver.QueueWaitMsKey, QueueWaitMs);
            Add(SharedHostSettingsResolver.QueueDepthKey, QueueDepth);
            Add(SharedHostSettingsResolver.SessionQueueCapKey, SessionQueueCap);
            Add(SharedHostSettingsResolver.MaxWorkersKey, MaxWorkers);
            Add(SharedHostSettingsResolver.MemoryFloorMbKey, MemoryFloorMb);
            Add(SharedHostSettingsResolver.ColdStartConcurrencyKey, ColdStartConcurrency);
            Add(SharedHostSettingsResolver.AttachReadyTimeoutSecondsKey, AttachReadyTimeoutSeconds);
            return section;
        }

        internal Configuration ToConfiguration(string testHostPath, string installation) =>
            BuildConfiguration(ToSection(), WorkerIdleTimeoutMinutes, testHostPath, installation);

        // Shared by the harness and the client process, so both build the same broker command line.
        internal static Configuration BuildConfiguration(JObject section, int idleMinutes, string testHostPath, string installation)
        {
            var settings = SharedHostSettingsResolver.Resolve(section, false, string.Empty, _ => null);
            if (!settings.IsValid) throw new ArgumentException(string.Join(" ", settings.Errors), nameof(section));
            return new Configuration
            {
                Server = new ServerConfig { WorkerSharingMode = "shared-host", WorkerIdleTimeoutMinutes = idleMinutes },
                GeneXus = new GeneXusConfig { WorkerExecutable = testHostPath, InstallationPath = installation },
                EffectiveSharedHost = settings
            };
        }
    }

    internal sealed class SharedHostClientOptions
    {
        internal SharedHostHarnessSettings Settings { get; set; } = new SharedHostHarnessSettings();

        /// <summary>
        /// Replaces the Available_Physical_Memory reading of the client's admission pre-check
        /// (SharedWorkerRecoveryContext.ReadAvailableMemory). The broker has no such seam: it reads the
        /// machine value, so a check that needs a refusal below the 8192 MB maximum floor gets it from the
        /// Gateway pre-check, before any broker process starts.
        /// </summary>
        internal long? InjectedAvailableMemoryMb { get; set; }
    }

    internal sealed class SharedHostBroker
    {
        private readonly StringBuilder _stderr;

        internal SharedHostBroker(Process process, TrackedProcess tracked, SharedWorkerIdentity identity, string kbDir, StringBuilder stderr)
        {
            Process = process;
            Tracked = tracked;
            Identity = identity;
            KbDir = kbDir;
            _stderr = stderr;
        }

        internal Process Process { get; }
        internal TrackedProcess Tracked { get; }
        internal SharedWorkerIdentity Identity { get; }
        internal string KbDir { get; }
        internal int Pid => Tracked.Pid;

        internal string StderrTail()
        {
            lock (_stderr)
            {
                string text = _stderr.ToString();
                return text.Length <= 4000 ? text : text.Substring(text.Length - 4000);
            }
        }
    }

    /// <summary>
    /// Harness-side proxy of one Gateway client process. Every call is a JSON line on the client's stdin; replies
    /// carry the same "cid", and frames from the broker arrive as "frame" events per KB directory.
    /// </summary>
    internal sealed class SharedHostGatewayClient : IDisposable
    {
        private readonly SharedHostProcessHarness _harness;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<JObject>> _frames = new ConcurrentDictionary<string, ConcurrentQueue<JObject>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<JObject> _events = new ConcurrentQueue<JObject>();
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _stderr = new StringBuilder();
        private readonly object _writeLock = new object();
        private int _nextCid;

        internal SharedHostGatewayClient(SharedHostProcessHarness harness, string name, Process process, TrackedProcess tracked)
        {
            _harness = harness;
            Name = name;
            Process = process;
            Tracked = tracked;
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "shared-host-harness-" + name };
            reader.Start();
        }

        internal string Name { get; }
        internal Process Process { get; }
        internal TrackedProcess Tracked { get; }
        internal int Pid => Tracked.Pid;

        /// <summary>Events other than frames (host-started, admission, disconnected, log), in arrival order.</summary>
        internal IReadOnlyList<JObject> Events => _events.ToList();

        internal async Task WaitReadyAsync(int timeoutMs)
        {
            var finished = await Task.WhenAny(_ready.Task, Task.Delay(timeoutMs));
            if (finished != _ready.Task || !_ready.Task.Result)
                throw new TimeoutException(Name + " did not report ready within " + timeoutMs + " ms:\n" + StderrTail());
        }

        /// <summary>
        /// Runs ConnectV2 for <paramref name="kbDir"/> in the client. The reply has "ok"; on success attachId,
        /// hostPid, hostStartTimeUtcTicks, workerPid, workerStartTimeUtcTicks, generation, sdkReady, openMs; on a
        /// typed refusal code, reason and the catalog "error" object.
        /// </summary>
        internal async Task<JObject> OpenAsync(string kbDir, int timeoutMs = SharedHostProcessHarness.DefaultTimeoutMs)
        {
            JObject reply = await CommandAsync(new JObject { ["op"] = "open", ["kb"] = kbDir, ["timeoutMs"] = timeoutMs }, timeoutMs + 5000);
            if ((bool?)reply["ok"] == true)
            {
                int hostPid = (int?)reply["hostPid"] ?? 0;
                long hostTicks = (long?)reply["hostStartTimeUtcTicks"] ?? 0;
                int workerPid = (int?)reply["workerPid"] ?? 0;
                long workerTicks = (long?)reply["workerStartTimeUtcTicks"] ?? 0;
                if (hostPid > 0 && hostTicks > 0) _harness.Track(hostPid, hostTicks, "broker");
                if (workerPid > 0 && workerTicks > 0) _harness.Track(workerPid, workerTicks, "worker");
            }
            return reply;
        }

        /// <summary>OpenAsync that throws when the open is refused.</summary>
        internal async Task<JObject> OpenOrThrowAsync(string kbDir, int timeoutMs = SharedHostProcessHarness.DefaultTimeoutMs)
        {
            JObject reply = await OpenAsync(kbDir, timeoutMs);
            if ((bool?)reply["ok"] != true) throw new InvalidOperationException(Name + " could not open " + Path.GetFileName(kbDir) + ": " + reply);
            return reply;
        }

        /// <summary>Forwards a JSON-RPC frame to the broker over the client's attachment for <paramref name="kbDir"/>.</summary>
        internal Task SendAsync(string kbDir, JObject frame) =>
            CommandAsync(new JObject { ["op"] = "send", ["kb"] = kbDir, ["frame"] = frame }, SharedHostProcessHarness.DefaultTimeoutMs)
                .ContinueWith(t => EnsureOk(t.Result, "send"), TaskScheduler.Default);

        /// <summary>Sends <paramref name="frame"/> and waits for the response with the same JSON-RPC id on that KB.</summary>
        internal async Task<JObject> CallAsync(string kbDir, JObject frame, int timeoutMs = SharedHostProcessHarness.DefaultTimeoutMs)
        {
            JToken id = frame["id"] ?? throw new ArgumentException("A call needs a JSON-RPC id.", nameof(frame));
            int seen = Frames(kbDir).Count;
            await SendAsync(kbDir, frame);
            return await WaitForFrameAsync(kbDir, f => f["method"] == null && JToken.DeepEquals(f["id"], id), "the response to id " + id, timeoutMs, seen);
        }

        /// <summary>An SDK-like request the fake child answers after <paramref name="durationMs"/>.</summary>
        internal static JObject ProbeFrame(int id, int durationMs = 0, string method = "probe") => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = new JObject { ["fakeDurationMs"] = durationMs }
        };

        /// <summary>The broker's status frame for the attachment (attachments, workerPid, childState, running, queue, settings).</summary>
        internal async Task<JObject?> StatusAsync(string kbDir, int timeoutMs = 2000)
        {
            JObject reply = EnsureOk(await CommandAsync(new JObject { ["op"] = "status", ["kb"] = kbDir, ["timeoutMs"] = timeoutMs }, timeoutMs + 5000), "status");
            return reply["status"] as JObject;
        }

        internal async Task<JObject> WaitForStatusAsync(string kbDir, Func<JObject, bool> match, string what, int timeoutMs = SharedHostProcessHarness.DefaultTimeoutMs)
        {
            var clock = Stopwatch.StartNew();
            JObject? last = null;
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                last = await StatusAsync(kbDir);
                if (last != null && match(last)) return last;
                await Task.Delay(25);
            }
            throw new TimeoutException(Name + " status never showed " + what + "; last: " + last);
        }

        /// <summary>Detaches from the broker of <paramref name="kbDir"/> (the Gateway's normal close).</summary>
        internal async Task CloseAsync(string kbDir, bool stopIfLast = false) =>
            EnsureOk(await CommandAsync(new JObject { ["op"] = "close", ["kb"] = kbDir, ["stopIfLast"] = stopIfLast }, SharedHostProcessHarness.DefaultTimeoutMs), "close");

        /// <summary>Frames received from the broker on the attachment for <paramref name="kbDir"/>, in arrival order.</summary>
        internal IReadOnlyList<JObject> Frames(string kbDir) =>
            _frames.TryGetValue(Path.GetFullPath(kbDir), out var queue) ? queue.ToList() : new List<JObject>();

        internal async Task<JObject> WaitForFrameAsync(string kbDir, Func<JObject, bool> match, string what, int timeoutMs = SharedHostProcessHarness.DefaultTimeoutMs, int skip = 0)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                var found = Frames(kbDir).Skip(skip).FirstOrDefault(match);
                if (found != null) return found;
                if (Process.HasExited) throw new InvalidOperationException(Name + " exited:\n" + StderrTail());
                await Task.Delay(20);
            }
            throw new TimeoutException(Name + " received no frame with " + what + " within " + timeoutMs + " ms:\n" + StderrTail());
        }

        /// <summary>Asks the client to detach everything and exit; no wait.</summary>
        internal void RequestExit()
        {
            try
            {
                if (Process.HasExited) return;
                lock (_writeLock)
                {
                    Process.StandardInput.WriteLine(new JObject { ["op"] = "exit" }.ToString(Formatting.None));
                    Process.StandardInput.Flush();
                    Process.StandardInput.Close();
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { } // Includes ObjectDisposedException.
        }

        internal string StderrTail()
        {
            lock (_stderr)
            {
                string text = _stderr.ToString();
                return text.Length <= 4000 ? text : text.Substring(text.Length - 4000);
            }
        }

        public void Dispose()
        {
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
        }

        private async Task<JObject> CommandAsync(JObject command, int timeoutMs)
        {
            int cid = Interlocked.Increment(ref _nextCid);
            command["cid"] = cid;
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[cid] = completion;
            lock (_writeLock)
            {
                Process.StandardInput.WriteLine(command.ToString(Formatting.None));
                Process.StandardInput.Flush();
            }
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
            _pending.TryRemove(cid, out _);
            if (finished != completion.Task)
                throw new TimeoutException(Name + " did not answer " + (string?)command["op"] + " within " + timeoutMs + " ms:\n" + StderrTail());
            return await completion.Task;
        }

        private JObject EnsureOk(JObject reply, string op)
        {
            if ((bool?)reply["ok"] != true) throw new InvalidOperationException(Name + " " + op + " failed: " + reply);
            return reply;
        }

        private void ReadLoop()
        {
            try
            {
                string? line;
                while ((line = Process.StandardOutput.ReadLine()) != null)
                {
                    JObject message;
                    try { message = JObject.Parse(line); }
                    catch (JsonException) { lock (_stderr) _stderr.AppendLine("stdout: " + line); continue; }

                    if (message["cid"] is JToken cid && cid.Type == JTokenType.Integer)
                    {
                        if (_pending.TryGetValue((int)cid, out var completion)) completion.TrySetResult(message);
                        continue;
                    }
                    string? kind = (string?)message["event"];
                    switch (kind)
                    {
                        case "ready":
                            _ready.TrySetResult(true);
                            break;
                        case "frame":
                            string kb = Path.GetFullPath((string?)message["kb"] ?? string.Empty);
                            if (message["frame"] is JObject frame) _frames.GetOrAdd(kb, _ => new ConcurrentQueue<JObject>()).Enqueue(frame);
                            break;
                        case "host-started":
                            int pid = (int?)message["pid"] ?? 0;
                            long ticks = (long?)message["startTimeUtcTicks"] ?? 0;
                            if (pid > 0 && ticks > 0) _harness.Track(pid, ticks, "broker");
                            _events.Enqueue(message);
                            break;
                        default:
                            _events.Enqueue(message);
                            break;
                    }
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { } // Includes ObjectDisposedException.
            finally
            {
                _ready.TrySetResult(false);
                foreach (var pending in _pending.Values)
                    pending.TrySetException(new InvalidOperationException(Name + " exited:\n" + StderrTail()));
            }
        }
    }

    /// <summary>
    /// Entry point of a Gateway client process: `dotnet GxMcp.Gateway.Tests.dll --shared-host-client <options.json>`.
    /// Reads one command per stdin line ("open", "send", "status", "close", "exit") and writes replies and
    /// events as one JSON object per stdout line. Diagnostics go to stderr only.
    /// </summary>
    internal static class SharedHostClientProgram
    {
        internal const string ModeArgument = "--shared-host-client";

        public static int Main(string[] args)
        {
            if (args.Length != 2 || !string.Equals(args[0], ModeArgument, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Usage: GxMcp.Gateway.Tests " + ModeArgument + " <options.json>. Run the tests with dotnet test.");
                return 2;
            }
            try
            {
                new SharedHostClientRuntime(JObject.Parse(File.ReadAllText(args[1]))).Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("shared-host client failed: " + ex);
                return 1;
            }
        }
    }

    internal sealed class SharedHostClientRuntime
    {
        private readonly object _writeLock = new object();
        private readonly ConcurrentDictionary<string, SharedWorkerConnection> _connections =
            new ConcurrentDictionary<string, SharedWorkerConnection>(StringComparer.OrdinalIgnoreCase);
        private readonly TextWriter _out;
        private readonly Configuration _config;
        private readonly SharedWorkerRecoveryContext _context;
        private readonly string _testHost;
        private readonly string _installation;
        private readonly string _registryDir;

        internal SharedHostClientRuntime(JObject options)
        {
            _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _testHost = (string)options["testHost"]!;
            _installation = (string)options["installation"]!;
            _registryDir = (string)options["registryDir"]!;
            _config = SharedHostHarnessSettings.BuildConfiguration(
                (JObject?)options["sharedHost"] ?? new JObject(),
                (int?)options["workerIdleTimeoutMinutes"] ?? 5,
                _testHost,
                _installation);
            long? injected = options["availableMemoryMb"]?.Type == JTokenType.Integer ? (long)options["availableMemoryMb"]! : (long?)null;
            AvailableMemoryReader memory = MachineMemory.TryGetAvailablePhysicalMb;
            if (injected.HasValue)
            {
                long value = injected.Value;
                memory = (out long availableMb, out string? error) => { availableMb = value; error = null; return true; };
            }
            _context = new SharedWorkerRecoveryContext
            {
                RegistryRoot = _registryDir,
                OwnershipRegistryRoot = (string)options["ownershipDir"]!,
                Log = text => Emit(new JObject { ["event"] = "log", ["text"] = text }),
                StartHost = StartHost,
                ReadAvailableMemory = memory
            };
        }

        internal void Run()
        {
            Emit(new JObject { ["event"] = "ready", ["pid"] = Environment.ProcessId });
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            string? line;
            while ((line = stdin.ReadLine()) != null)
            {
                JObject command;
                try { command = JObject.Parse(line); }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine("unparseable command: " + ex.Message);
                    continue;
                }
                if (string.Equals((string?)command["op"], "exit", StringComparison.Ordinal)) break;
                _ = Task.Run(() => Handle(command));
            }

            foreach (var connection in _connections.Values)
            {
                try { connection.Dispose(); } catch { }
            }
            _out.Flush();
            // Connection threads must not keep the process alive.
            Environment.Exit(0);
        }

        private void Handle(JObject command)
        {
            JToken? cid = command["cid"];
            JObject reply;
            try
            {
                string op = (string?)command["op"] ?? string.Empty;
                string kb = Path.GetFullPath((string?)command["kb"] ?? throw new InvalidDataException("kb is required"));
                switch (op)
                {
                    case "open":
                        reply = Open(kb, (int?)command["timeoutMs"] ?? SharedHostProcessHarness.DefaultTimeoutMs);
                        break;
                    case "send":
                        Connection(kb).SendAsync((JObject)command["frame"]!, CancellationToken.None).GetAwaiter().GetResult();
                        reply = new JObject { ["ok"] = true };
                        break;
                    case "status":
                        JObject? status = Connection(kb).RequestStatusAsync((int?)command["timeoutMs"] ?? 2000).GetAwaiter().GetResult();
                        reply = new JObject { ["ok"] = true, ["status"] = status };
                        break;
                    case "close":
                        if (_connections.TryRemove(kb, out var closing)) closing.Dispose((bool?)command["stopIfLast"] ?? false);
                        reply = new JObject { ["ok"] = true };
                        break;
                    default:
                        reply = new JObject { ["ok"] = false, ["error"] = "unknown op '" + op + "'" };
                        break;
                }
            }
            catch (Exception ex)
            {
                reply = new JObject { ["ok"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
            }
            reply["cid"] = cid;
            Emit(reply);
        }

        private JObject Open(string kb, int timeoutMs)
        {
            var request = new SharedWorkerLaunchRequest
            {
                Config = _config,
                Kb = new KbHandle(Path.GetFileName(kb), kb, _installation, SharedHostProcessHarness.Driver, SharedHostProcessHarness.Major),
                WorkerPath = _testHost,
                InstallationPath = _installation,
                Driver = SharedHostProcessHarness.Driver,
                Major = SharedHostProcessHarness.Major,
                LaunchTimeoutMs = timeoutMs
            };
            var clock = Stopwatch.StartNew();
            DateTime startedUtc = DateTime.UtcNow;
            ISharedWorkerAttachment attachment;
            try
            {
                attachment = SharedWorkerHostLauncher.ConnectV2(request, _context);
            }
            catch (SharedWorkerLaunchException ex)
            {
                return new JObject
                {
                    ["ok"] = false,
                    ["code"] = ex.Code,
                    ["reason"] = ex.Reason,
                    ["error"] = ex.Error,
                    ["openStartedUtc"] = startedUtc,
                    ["openMs"] = clock.ElapsedMilliseconds
                };
            }
            if (!(attachment is SharedWorkerConnection connection))
            {
                attachment.Dispose();
                throw new InvalidOperationException("ConnectV2 returned an unsupported attachment type.");
            }
            connection.LineReceived += line =>
            {
                JObject frame;
                try { frame = JObject.Parse(line); }
                catch (JsonException) { return; }
                Emit(new JObject { ["event"] = "frame", ["kb"] = kb, ["frame"] = frame });
            };
            connection.Disconnected += ex => Emit(new JObject { ["event"] = "disconnected", ["kb"] = kb, ["error"] = ex?.Message });
            connection.AdmissionReceived += update => Emit(new JObject
            {
                ["event"] = "admission",
                ["kb"] = kb,
                ["state"] = update.State,
                ["position"] = update.Position,
                ["elapsedMs"] = update.ElapsedMs
            });
            if (_connections.TryRemove(kb, out var previous)) previous.Dispose();
            _connections[kb] = connection;

            var info = connection.AttachInfo;
            return new JObject
            {
                ["ok"] = true,
                ["attachId"] = info?.AttachId,
                ["hostPid"] = info?.HostPid ?? 0,
                ["hostStartTimeUtcTicks"] = info?.HostStartTimeUtcTicks ?? 0,
                ["workerPid"] = info?.WorkerPid ?? 0,
                ["workerStartTimeUtcTicks"] = info?.WorkerStartTimeUtcTicks ?? 0,
                ["generation"] = info?.Generation ?? 0,
                ["state"] = info?.State,
                ["sdkReady"] = connection.IsSdkReady,
                ["openStartedUtc"] = startedUtc,
                ["openMs"] = clock.ElapsedMilliseconds
            };
        }

        private SharedWorkerConnection Connection(string kb) =>
            _connections.TryGetValue(kb, out var connection)
                ? connection
                : throw new InvalidOperationException("no open attachment for " + Path.GetFileName(kb));

        // Replaces SharedWorkerHostLauncher.StartHostProcess (see the file header): same command line plus
        // --registry-dir; LOCALAPPDATA and GXMCP_TESTHOST_SCRIPT are inherited from this client.
        private void StartHost(SharedWorkerHostStartRequest request)
        {
            string arguments = request.Arguments;
            if (!string.IsNullOrWhiteSpace(request.RegistryRoot))
                arguments += " --registry-dir \"" + request.RegistryRoot!.TrimEnd('\\', '/') + "\"";
            var info = new ProcessStartInfo
            {
                FileName = request.WorkerPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(request.WorkerPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.Environment["GXMCP_SHARED_HOST"] = "1";
            Process host = Process.Start(info) ?? throw new InvalidOperationException("Could not start the shared Worker host.");
            _ = host.StandardOutput.ReadToEndAsync();
            _ = host.StandardError.ReadToEndAsync();
            long ticks;
            try { ticks = host.StartTime.ToUniversalTime().Ticks; }
            catch (InvalidOperationException) { ticks = 0; }
            Emit(new JObject
            {
                ["event"] = "host-started",
                ["kb"] = request.Kb.Path,
                ["pid"] = host.Id,
                ["startTimeUtcTicks"] = ticks
            });
        }

        private void Emit(JObject message)
        {
            string text = message.ToString(Formatting.None);
            lock (_writeLock) _out.WriteLine(text);
        }
    }
}
