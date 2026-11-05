using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Registry v2, stale ownership, termination safety and Physical_KB_Identity contract for the Gateway side
    // (task 4.5; Requirements 2.1, 2.5, 7.1, 7.2, 7.3, 7.4, 7.7, 7.9; design decisions A3, A4, D6).
    // These tests are written before the implementation (tasks 4.9 and 4.10) and pin the names below.
    // Tasks 4.4 (launcher), 4.6 (Property 10) and 4.7 (Property 12) build on the same names.
    // The v1 members of SharedWorkerRegistry and their tests in SharedWorkerRegistryTests.cs stay unchanged.
    //
    // ---- kbKey normalization rule (shared with the Worker side) ----
    //   normalized = SharedWorkerIdentity.NormalizePath(kbPath)
    //              = Path.GetFullPath(kbPath)               (relative -> absolute against the current directory)
    //                .TrimEnd('\\', '/')                     (no trailing directory separator)
    //                .ToLowerInvariant()                     (letter case ignored)
    //   kbKey      = lowercase hex of SHA-256(UTF-8 bytes of normalized), 64 characters, no prefix.
    //   If Path.GetFullPath throws, kbPath.Trim() is used with the same TrimEnd and ToLowerInvariant steps.
    //   A null or whitespace KB path has no kbKey: ComputeKbKey throws ArgumentException.
    //   Known answer: "c:\kbs\kbteste" -> 717f20e76c7a9966c16cbba1019e30fc89e4648fde629b0ae6062e504fe0b504.
    //   This is the rule stated by src/GxMcp.Worker.Tests/SharedWorkerHostOwnershipTests.cs (task 4.8): the single
    //   implementation is GxMcp.Common.SharedHost.SharedHostKbKey (src/GxMcp.Shared/SharedHost/SharedHostKbKey.cs,
    //   NormalizeKbPath and Compute), linked into both processes; SharedWorkerIdentity.ComputeKbKey delegates to
    //   SharedHostKbKey.Compute so the Gateway and the host can never compute different keys.
    //
    // ---- SharedWorkerIdentity (src/GxMcp.Gateway/SharedWorkerIdentity.cs), additions ----
    //   string KbKey { get; init; }                                   set by Create; equals ComputeKbKey(KbPath)
    //   static string ComputeKbKey(string? kbPath)
    //   SharedHostRecordIdentity ToRecordIdentity()                   copies Key and the normalized components
    //   static IReadOnlyList<SharedWorkerIdentityDifference> BuildMismatch(
    //       SharedWorkerIdentity requested, SharedHostRecordIdentity live)
    //     Pure. Throws ArgumentNullException for null input and ArgumentException when the two KB paths do not
    //     normalize to the same directory. Live values are normalized with the same rules as Create
    //     (NormalizePath for paths, NormalizeToken for driver and major, null profile -> ""), so case-only or
    //     trailing-separator differences are not reported. Returns one entry per differing component, in the
    //     fixed order of SharedWorkerIdentityComponent.All, with the normalized requested and live values.
    //   static class SharedWorkerIdentityComponent                    the five components besides the KB path
    //     const string InstallationPath = "installationPath";
    //     const string Driver = "driver";
    //     const string Major = "major";
    //     const string WorkerExecutable = "workerExecutable";
    //     const string ProfileConfigPath = "profileConfigPath";        decision D6
    //     static readonly IReadOnlyList<string> All                    the order above
    //   sealed class SharedWorkerIdentityDifference { string Component; string Requested; string Live; }
    //
    // ---- SharedWorkerRegistry v2 members ----
    //   static string DefaultV2Root { get; }        %LOCALAPPDATA%\GxMcp\shared-workers\v2
    //   static string GetV2Root(string? registryRoot = null)          registryRoot ?? DefaultV2Root
    //   static bool IsValidKbKey(string? kbKey)                       64 lowercase hex characters
    //   static string GetRecordPathV2(string kbKey, string? registryRoot = null)
    //     Path.Combine(GetV2Root(registryRoot), kbKey + ".json"); ArgumentException for an invalid kbKey,
    //     so a key can never escape the registry directory.
    //   static string ElectionMutexNameV2(string kbKey)               "Local\GxMcp.SharedWorker.v2.Kb." + kbKey
    //   static string HostMutexNameV2(string kbKey)                   "Local\GxMcp.SharedWorker.v2.Host." + kbKey
    //   static string PipeNameV2(SharedWorkerIdentity identity)       "GxMcpShared2_" + identity.Key
    //   static RecordReadResult ReadRecordV2(string kbKey, string? registryRoot = null)
    //     RecordReadResult.ReadFile(GetRecordPathV2(...)): Absent, Unparseable, Parsed (or Unreadable, fail closed).
    //   static string WriteRecordV2(SharedHostRecord record, string? registryRoot = null)
    //     Atomic: temp file in the same directory, then File.Replace (File.Move when absent). Writes to
    //     GetRecordPathV2(record.KbKey, registryRoot), creates the directory, leaves no temp file, returns the path.
    //   static StaleRecordRemovalResult RemoveStaleRecord(
    //       string kbKey, SharedHostRecord? record, StaleReason reason, SharedWorkerRecoveryContext? context = null)
    //     The file is GetRecordPathV2(kbKey, context.RegistryRoot); a null context uses production defaults.
    //     record is the parsed stale record, or null for an unparseable file. ArgumentException for
    //     StaleReason.None. Re-reads the file first: Absent -> AlreadyAbsent; a different record (other host PID or
    //     start time, or a now-parseable file when record is null) -> RecordChanged; otherwise deletes it.
    //     A failed delete -> RemoveFailed with Error (Requirement 7 criterion 9). Exactly one log entry, on
    //     Removed only: FormatRecoveryLogEntry(RecoveryActionRemovedRecord, StaleRecoveryRegistryNames
    //     .SharedWorkerRegistry, kbKey, record?.HostPid ?? 0, record?.HostStartTimeUtcTicks ?? 0,
    //     context.RecoveringGatewayPid, reason).
    //   static OrphanStopResult StopOrphanExact(
    //       int pid, long startTicks, string expectedImagePath, SharedWorkerRecoveryContext? context = null)
    //     Order: refuse pid <= 0, startTicks <= 0 or empty path (RefusedInvalidArguments); refuse
    //     pid == context.RecoveringGatewayPid or pid in context.GatewayPids (RefusedGatewayPid), before any probe;
    //     re-read the start time (Probe) and the image path (ReadImagePath) immediately before termination;
    //     NotRunning -> AlreadyExited; RunningStartTimeUnreadable -> RefusedStartTimeUnreadable; start time not
    //     equal at millisecond precision -> RefusedStartTimeMismatch; image path unreadable ->
    //     RefusedImagePathUnreadable; image path not equal after NormalizePath -> RefusedImagePathMismatch;
    //     then Terminate(pid) (error text -> KillFailed); then poll Probe every StopPollInterval up to
    //     StopWaitTimeout: NotRunning or a different start time -> Stopped, else StillRunning (criterion 9).
    //     It writes no log entry: the caller logs FormatRecoveryLogEntry(RecoveryActionStoppedProcess, ...),
    //     because only the caller knows the record key and registry name.
    //   static IReadOnlyList<OrphanCandidate> SelectProcessesToTerminate(
    //       IReadOnlyList<OrphanCandidate> candidates,
    //       IReadOnlyDictionary<int, ProcessObservation> observations,
    //       IReadOnlyCollection<int> gatewayPids)
    //     Pure kill-set selector (Property 10). A candidate is selected only when: WorkerPid > 0,
    //     WorkerStartTimeUtcTicks > 0 and ExpectedImagePath is not empty; the observation of WorkerPid is
    //     Running with the same start time at millisecond precision and an ImagePath equal after NormalizePath;
    //     the owner Gateway is not live (not live = OwnerGatewayPid <= 0, or observed NotRunning, or observed
    //     Running with OwnerGatewayStartTimeUtcTicks > 0 and a different start time; RunningStartTimeUnreadable
    //     and a Running owner with no recorded start time are live, fail closed); and WorkerPid is neither in
    //     gatewayPids nor the OwnerGatewayPid of any candidate. A PID without an observation is NotRunning.
    //     Output keeps input order and contains each WorkerPid at most once.
    //   static string FormatRecoveryLogEntry(string action, string registryName, string recordKey, int pid,
    //       long recordedStartTimeUtcTicks, int recoveringGatewayPid, StaleReason reason)
    //     "[Gateway] stale-ownership-recovery action=<action> registry=<registryName> key=<recordKey>
    //      pid=<pid> recordedStartTimeUtcTicks=<ticks> recoveringGatewayPid=<pid> reason=<token>"
    //     on one line, single spaces; pid and ticks <= 0 print as "unknown"; ArgumentException for None.
    //   static string StaleReasonToken(StaleReason reason)
    //     owner-not-running | start-time-mismatch | unparseable-record | orphaned-worker; None -> ArgumentException
    //   const string RecoveryActionRemovedRecord = "removed-record";
    //   const string RecoveryActionStoppedProcess = "stopped-process";
    //
    // ---- Supporting Gateway types (same file as SharedWorkerRegistry) ----
    //   static class StaleRecoveryRegistryNames
    //     const string WorkerOwnershipRegistry = "WorkerOwnershipRegistry";
    //     const string SharedWorkerRegistry = "SharedWorkerRegistry";
    //     const string SingleInstanceLock = "SingleInstanceLock";
    //   delegate bool ImagePathReader(int pid, out string? imagePath, out string? error);
    //     production: ProcessIdentity.TryGetImagePath
    //   sealed class SharedWorkerRecoveryContext   (init-only properties, production defaults)
    //     IProcessProbe Probe                        new ProcessIdentity()
    //     ImagePathReader ReadImagePath              ProcessIdentity.TryGetImagePath
    //     Func<int, string?> Terminate               kill the PID; returns null on success, else the error text
    //     Action<string> Log                         Program.Log
    //     int RecoveringGatewayPid                   Environment.ProcessId
    //     IReadOnlyCollection<int> GatewayPids       empty; every PID recorded as a Gateway that must never die
    //     TimeSpan StopWaitTimeout                   10 seconds (Requirement 7 criterion 9)
    //     TimeSpan StopPollInterval                  100 ms
    //     string? RegistryRoot                       null -> DefaultV2Root
    //   enum OrphanStopOutcome { Stopped, AlreadyExited, RefusedInvalidArguments, RefusedGatewayPid,
    //       RefusedStartTimeUnreadable, RefusedStartTimeMismatch, RefusedImagePathUnreadable,
    //       RefusedImagePathMismatch, KillFailed, StillRunning }
    //   sealed class OrphanStopResult { OrphanStopOutcome Outcome; int Pid; string? Error; bool Stopped; }
    //     Stopped == (Outcome == OrphanStopOutcome.Stopped)
    //   enum StaleRecordRemovalOutcome { Removed, AlreadyAbsent, RecordChanged, RemoveFailed }
    //   sealed class StaleRecordRemovalResult { StaleRecordRemovalOutcome Outcome; string RecordKey;
    //       string Path; string? Error; string? LogEntry; }      LogEntry is the line written, null otherwise
    //   sealed class OrphanCandidate (init-only): string RegistryName; string RecordKey; int OwnerGatewayPid;
    //       long OwnerGatewayStartTimeUtcTicks; int WorkerPid; long WorkerStartTimeUtcTicks;
    //       string ExpectedImagePath
    //   sealed class ProcessObservation (init-only): int Pid; ProcessProbeResult Probe; string? ImagePath
    //
    // ---- WorkerOwnershipRegistry (src/GxMcp.Gateway/WorkerOwnershipRegistry.cs), read-only addition ----
    //   static string DefaultRoot { get; }          %LOCALAPPDATA%\GxMcp\worker-ownership
    //   static IReadOnlyList<WorkerOwnershipRecord> ScanByKbPath(string kbPath, string? registryRoot = null)
    //     Parses every "*.json" file directly under the root (temp files "*.json.tmp-*" are not matched) and
    //     returns the records whose SharedWorkerIdentity.NormalizePath(KbPath) equals NormalizePath(kbPath), for
    //     any Worker executable. Unparseable files are skipped. A missing root gives an empty list. It never
    //     creates, modifies or deletes a file. Acquire, Reconcile and Release are unchanged.
    //
    // No test here touches the real %LOCALAPPDATA% or a real process: every disk test uses a temporary root,
    // every process test uses the fake probe, image reader and terminator below.
    public sealed class SharedWorkerRegistryV2Tests : IDisposable
    {
        private const long AlignedStartTicks = 638_700_000_000_000_000L;
        private const int RecoveringGatewayPid = 3900;
        private const int OrphanPid = 5012;
        private const string OrphanImage = @"C:\Tools\GxMcp\worker\GxMcp.Worker.exe";

        private readonly string _root;

        public SharedWorkerRegistryV2Tests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxmcp-registry-v2-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---------------------------------------------------------------
        // kbKey and identity
        // ---------------------------------------------------------------

        [Fact]
        public void KbKey_IsSha256OfNormalizedKbPath_ForCaseTrailingSeparatorAndRelativeVariants()
        {
            string absolute = Path.Combine(Path.GetTempPath(), "GxMcp-KbKey-" + Guid.NewGuid().ToString("N"), "KbTeste");
            string normalized = Path.GetFullPath(absolute).TrimEnd('\\', '/').ToLowerInvariant();
            string expected = Sha256Hex(normalized);

            string[] variants =
            {
                absolute,
                absolute.ToUpperInvariant(),
                absolute + "\\",
                absolute + "/",
                Path.GetRelativePath(Environment.CurrentDirectory, absolute)
            };

            foreach (string variant in variants)
            {
                Assert.Equal(expected, SharedWorkerIdentity.ComputeKbKey(variant));
                Assert.Equal(expected, Identity(kbPath: variant).KbKey);
            }

            Assert.True(SharedWorkerRegistry.IsValidKbKey(expected));
        }

        [Fact]
        public void KbKey_MatchesTheKnownAnswerAndTheSharedHostKbKeyUsedByTheWorker()
        {
            const string knownAnswer = "717f20e76c7a9966c16cbba1019e30fc89e4648fde629b0ae6062e504fe0b504";

            Assert.Equal(knownAnswer, SharedWorkerIdentity.ComputeKbKey(@"C:\KBs\KbTeste\"));
            Assert.Equal(knownAnswer, SharedHostKbKey.Compute(@"C:\KBs\KbTeste\"));
            Assert.Equal(@"c:\kbs\kbteste", SharedHostKbKey.NormalizeKbPath(@"C:\KBs\KbTeste\"));
            Assert.Equal(knownAnswer, Identity(kbPath: @"c:\kbs\kbteste").KbKey);
        }

        [Fact]
        public void KbKey_DependsOnlyOnTheKbDirectory_WhileIdentityKeyCoversAllComponents()
        {
            var baseline = Identity();
            var otherInstallation = Identity(installation: @"C:\GeneXus17");
            var otherProfile = Identity(profile: @"C:\profiles\other\config.json");
            var otherKb = Identity(kbPath: @"C:\KBs\OtherTeste");

            Assert.Equal(baseline.KbKey, otherInstallation.KbKey);
            Assert.Equal(baseline.KbKey, otherProfile.KbKey);
            Assert.NotEqual(baseline.Key, otherInstallation.Key);
            Assert.NotEqual(baseline.Key, otherProfile.Key);
            Assert.NotEqual(baseline.KbKey, otherKb.KbKey);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void KbKey_RejectsAMissingKbPath(string? kbPath)
        {
            Assert.Throws<ArgumentException>(() => SharedWorkerIdentity.ComputeKbKey(kbPath));
        }

        [Fact]
        public void ToRecordIdentity_CopiesKeyAndNormalizedComponents()
        {
            var identity = Identity();
            SharedHostRecordIdentity record = identity.ToRecordIdentity();

            Assert.Equal(identity.Key, record.Key);
            Assert.Equal(identity.KbPath, record.KbPath);
            Assert.Equal(identity.WorkerExecutable, record.WorkerExecutable);
            Assert.Equal(identity.InstallationPath, record.InstallationPath);
            Assert.Equal(identity.Driver, record.Driver);
            Assert.Equal(identity.Major, record.Major);
            Assert.Equal(identity.ProfileConfigPath, record.ProfileConfigPath);
        }

        [Fact]
        public void IdentityComponents_AreTheFiveDesignComponentsInRequirementOrder()
        {
            Assert.Equal(
                new[] { "installationPath", "driver", "major", "workerExecutable", "profileConfigPath" },
                SharedWorkerIdentityComponent.All.ToArray());
        }

        [Fact]
        public void BuildMismatch_ReportsOnlyTheDifferingComponentWithRequestedAndLiveValues()
        {
            var requested = Identity(profile: @"C:\profiles\a\config.json");
            var live = Identity(profile: @"C:\profiles\b\config.json").ToRecordIdentity();

            var differences = SharedWorkerIdentity.BuildMismatch(requested, live);

            var only = Assert.Single(differences);
            Assert.Equal(SharedWorkerIdentityComponent.ProfileConfigPath, only.Component);
            Assert.Equal(@"c:\profiles\a\config.json", only.Requested);
            Assert.Equal(@"c:\profiles\b\config.json", only.Live);
        }

        [Fact]
        public void BuildMismatch_ReportsAllFiveComponentsInFixedOrder()
        {
            var requested = Identity();
            var live = Identity(
                worker: @"C:\Other\worker\GxMcp.Worker.exe",
                installation: @"C:\GeneXus17",
                driver: "dotnet-reflection",
                major: "17",
                profile: @"C:\profiles\other\config.json").ToRecordIdentity();

            var differences = SharedWorkerIdentity.BuildMismatch(requested, live);

            Assert.Equal(SharedWorkerIdentityComponent.All.ToArray(), differences.Select(d => d.Component).ToArray());
            var major = differences.Single(d => d.Component == SharedWorkerIdentityComponent.Major);
            Assert.Equal("18", major.Requested);
            Assert.Equal("17", major.Live);
            var driver = differences.Single(d => d.Component == SharedWorkerIdentityComponent.Driver);
            Assert.Equal("native-sdk", driver.Requested);
            Assert.Equal("dotnet-reflection", driver.Live);
        }

        [Fact]
        public void BuildMismatch_IgnoresCaseAndTrailingSeparatorDifferencesInTheLiveRecord()
        {
            var requested = Identity();
            var live = new SharedHostRecordIdentity
            {
                Key = requested.Key,
                KbPath = @"C:\KBS\KBTESTE\",
                WorkerExecutable = @"C:\TOOLS\GXMCP\WORKER\GXMCP.WORKER.EXE",
                InstallationPath = @"C:\GENEXUS18\",
                Driver = " Native-SDK ",
                Major = "18",
                ProfileConfigPath = @"C:\PROFILES\DEFAULT\CONFIG.JSON"
            };

            Assert.Empty(SharedWorkerIdentity.BuildMismatch(requested, live));
        }

        [Fact]
        public void BuildMismatch_TreatsAMissingLiveProfileAsEmpty()
        {
            var requested = Identity(profile: null);
            var live = Identity(profile: null).ToRecordIdentity();
            live.ProfileConfigPath = null;

            Assert.Empty(SharedWorkerIdentity.BuildMismatch(requested, live));
        }

        [Fact]
        public void BuildMismatch_RejectsIdentitiesOfDifferentKbDirectories()
        {
            var requested = Identity();
            var live = Identity(kbPath: @"C:\KBs\OtherTeste").ToRecordIdentity();

            Assert.Throws<ArgumentException>(() => SharedWorkerIdentity.BuildMismatch(requested, live));
        }

        // ---------------------------------------------------------------
        // v2 paths, names and the root override
        // ---------------------------------------------------------------

        [Fact]
        public void DefaultV2Root_IsUnderLocalAppDataSharedWorkersV2_AndSeparateFromV1()
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GxMcp", "shared-workers", "v2");
            var identity = Identity();

            Assert.Equal(expected, SharedWorkerRegistry.DefaultV2Root);
            Assert.Equal(expected, SharedWorkerRegistry.GetV2Root());
            Assert.Equal(Path.Combine(expected, identity.KbKey + ".json"), SharedWorkerRegistry.GetRecordPathV2(identity.KbKey));
            Assert.NotEqual(
                Path.GetDirectoryName(SharedWorkerRegistry.GetRecordPath(identity)),
                Path.GetDirectoryName(SharedWorkerRegistry.GetRecordPathV2(identity.KbKey)));
        }

        [Fact]
        public void RegistryRootOverride_RedirectsRecordPaths()
        {
            var identity = Identity();

            Assert.Equal(_root, SharedWorkerRegistry.GetV2Root(_root));
            Assert.Equal(Path.Combine(_root, identity.KbKey + ".json"), SharedWorkerRegistry.GetRecordPathV2(identity.KbKey, _root));
        }

        [Theory]
        [InlineData("")]
        [InlineData("..\\escape")]
        [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
        [InlineData("abc")]
        public void GetRecordPathV2_RejectsAKeyThatIsNotALowercaseSha256Hex(string kbKey)
        {
            Assert.False(SharedWorkerRegistry.IsValidKbKey(kbKey));
            Assert.Throws<ArgumentException>(() => SharedWorkerRegistry.GetRecordPathV2(kbKey, _root));
        }

        [Fact]
        public void V2Names_UseKbKeyForMutexesAndIdentityKeyForThePipe()
        {
            var identity = Identity();

            Assert.Equal("Local\\GxMcp.SharedWorker.v2.Kb." + identity.KbKey, SharedWorkerRegistry.ElectionMutexNameV2(identity.KbKey));
            Assert.Equal("Local\\GxMcp.SharedWorker.v2.Host." + identity.KbKey, SharedWorkerRegistry.HostMutexNameV2(identity.KbKey));
            Assert.Equal("GxMcpShared2_" + identity.Key, SharedWorkerRegistry.PipeNameV2(identity));
        }

        // ---------------------------------------------------------------
        // ReadRecordV2 and WriteRecordV2
        // ---------------------------------------------------------------

        [Fact]
        public void ReadRecordV2_ReturnsAbsent_WhenNoRecordExists()
        {
            var read = SharedWorkerRegistry.ReadRecordV2(Identity().KbKey, _root);

            Assert.Equal(RecordReadStatus.Absent, read.Status);
            Assert.Null(read.Record);
            Assert.False(Directory.Exists(_root));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{ not json")]
        [InlineData("{\"schema\":1,\"hostPid\":4120,\"hostStartTimeUtcTicks\":638700000000000000}")]
        public void ReadRecordV2_ReturnsUnparseable_ForAMalformedOrV1File(string content)
        {
            string kbKey = Identity().KbKey;
            Directory.CreateDirectory(_root);
            File.WriteAllText(SharedWorkerRegistry.GetRecordPathV2(kbKey, _root), content);

            var read = SharedWorkerRegistry.ReadRecordV2(kbKey, _root);

            Assert.Equal(RecordReadStatus.Unparseable, read.Status);
            Assert.Null(read.Record);
            Assert.False(string.IsNullOrWhiteSpace(read.Error));
        }

        [Fact]
        public void WriteRecordV2_ThenReadRecordV2_ReturnsParsedRecord()
        {
            var record = Record(Identity(), hostPid: 4120, generation: 3);

            string path = SharedWorkerRegistry.WriteRecordV2(record, _root);
            var read = SharedWorkerRegistry.ReadRecordV2(record.KbKey, _root);

            Assert.Equal(SharedWorkerRegistry.GetRecordPathV2(record.KbKey, _root), path);
            Assert.Equal(RecordReadStatus.Parsed, read.Status);
            Assert.NotNull(read.Record);
            Assert.Equal(record.KbKey, read.Record!.KbKey);
            Assert.Equal(record.Identity.Key, read.Record.Identity.Key);
            Assert.Equal(4120, read.Record.HostPid);
            Assert.Equal(AlignedStartTicks, read.Record.HostStartTimeUtcTicks);
            Assert.Equal(RecoveringGatewayPid, read.Record.LaunchedByGatewayPid);
            Assert.Equal(3, read.Record.Generation);
            Assert.Equal(SharedHostRecordState.Ready, read.Record.State);
        }

        [Fact]
        public void WriteRecordV2_ReplacesAnExistingRecordAndLeavesNoTempFile()
        {
            var identity = Identity();
            SharedWorkerRegistry.WriteRecordV2(Record(identity, hostPid: 4120, generation: 1), _root);
            SharedWorkerRegistry.WriteRecordV2(Record(identity, hostPid: 4120, generation: 2), _root);

            var read = SharedWorkerRegistry.ReadRecordV2(identity.KbKey, _root);

            Assert.Equal(RecordReadStatus.Parsed, read.Status);
            Assert.Equal(2, read.Record!.Generation);
            Assert.Equal(new[] { identity.KbKey + ".json" },
                Directory.GetFiles(_root).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void WriteRecordV2_IsAtomic_AConcurrentReaderNeverSeesAPartialOrMissingRecord()
        {
            var identity = Identity();
            SharedWorkerRegistry.WriteRecordV2(Record(identity, hostPid: 4120, generation: 1), _root);
            var observed = new List<RecordReadStatus>();
            using var stop = new ManualResetEventSlim(false);
            var reader = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    var status = SharedWorkerRegistry.ReadRecordV2(identity.KbKey, _root).Status;
                    lock (observed) observed.Add(status);
                }
            }) { IsBackground = true };
            reader.Start();

            try
            {
                for (int generation = 2; generation <= 100; generation++)
                    SharedWorkerRegistry.WriteRecordV2(Record(identity, hostPid: 4120, generation: generation), _root);
            }
            finally
            {
                stop.Set();
                reader.Join(TimeSpan.FromSeconds(5));
            }

            lock (observed)
            {
                Assert.NotEmpty(observed);
                Assert.DoesNotContain(RecordReadStatus.Unparseable, observed);
                Assert.DoesNotContain(RecordReadStatus.Absent, observed);
            }
            Assert.Equal(100, SharedWorkerRegistry.ReadRecordV2(identity.KbKey, _root).Record!.Generation);
        }

        // ---------------------------------------------------------------
        // RemoveStaleRecord and the recovery log entry
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(nameof(StaleReason.OwnerNotRunning), "owner-not-running")]
        [InlineData(nameof(StaleReason.StartTimeMismatch), "start-time-mismatch")]
        [InlineData(nameof(StaleReason.UnparseableRecord), "unparseable-record")]
        [InlineData(nameof(StaleReason.OrphanedWorker), "orphaned-worker")]
        public void StaleReasonToken_MapsEachReasonOfRequirement7Criterion7(string reasonName, string token)
        {
            // StaleReason is internal, so the theory passes its name; a public test method cannot take it.
            var reason = Enum.Parse<StaleReason>(reasonName);
            Assert.Equal(token, SharedWorkerRegistry.StaleReasonToken(reason));
        }

        [Fact]
        public void StaleReasonToken_RejectsNone()
        {
            Assert.Throws<ArgumentException>(() => SharedWorkerRegistry.StaleReasonToken(StaleReason.None));
        }

        [Fact]
        public void FormatRecoveryLogEntry_ContainsEveryRequiredFieldOnOneLine()
        {
            string line = SharedWorkerRegistry.FormatRecoveryLogEntry(
                SharedWorkerRegistry.RecoveryActionStoppedProcess,
                StaleRecoveryRegistryNames.WorkerOwnershipRegistry,
                "record-key-1",
                OrphanPid,
                AlignedStartTicks,
                RecoveringGatewayPid,
                StaleReason.OrphanedWorker);

            Assert.Equal(
                "[Gateway] stale-ownership-recovery action=stopped-process registry=WorkerOwnershipRegistry " +
                "key=record-key-1 pid=5012 recordedStartTimeUtcTicks=638700000000000000 " +
                "recoveringGatewayPid=3900 reason=orphaned-worker",
                line);
            Assert.DoesNotContain("\n", line);
        }

        [Fact]
        public void FormatRecoveryLogEntry_PrintsUnknownForAMissingPidAndStartTime()
        {
            string line = SharedWorkerRegistry.FormatRecoveryLogEntry(
                SharedWorkerRegistry.RecoveryActionRemovedRecord,
                StaleRecoveryRegistryNames.SharedWorkerRegistry,
                "record-key-2",
                0,
                0,
                RecoveringGatewayPid,
                StaleReason.UnparseableRecord);

            Assert.Contains(" pid=unknown ", line);
            Assert.Contains(" recordedStartTimeUtcTicks=unknown ", line);
            Assert.EndsWith(" reason=unparseable-record", line);
        }

        [Fact]
        public void RegistryNames_MatchRequirement7Criterion7()
        {
            Assert.Equal("WorkerOwnershipRegistry", StaleRecoveryRegistryNames.WorkerOwnershipRegistry);
            Assert.Equal("SharedWorkerRegistry", StaleRecoveryRegistryNames.SharedWorkerRegistry);
            Assert.Equal("SingleInstanceLock", StaleRecoveryRegistryNames.SingleInstanceLock);
        }

        [Fact]
        public void RemoveStaleRecord_DeletesTheRecordAndWritesOneLogEntryWithAllFields()
        {
            var record = Record(Identity(), hostPid: 4120, generation: 1);
            string path = SharedWorkerRegistry.WriteRecordV2(record, _root);
            var log = new List<string>();

            var result = SharedWorkerRegistry.RemoveStaleRecord(
                record.KbKey, record, StaleReason.OwnerNotRunning, Context(new FakeProcessProbe(), log));

            Assert.Equal(StaleRecordRemovalOutcome.Removed, result.Outcome);
            Assert.Equal(record.KbKey, result.RecordKey);
            Assert.Equal(path, result.Path);
            Assert.False(File.Exists(path));
            string entry = Assert.Single(log);
            Assert.Equal(entry, result.LogEntry);
            Assert.Equal(
                SharedWorkerRegistry.FormatRecoveryLogEntry(
                    SharedWorkerRegistry.RecoveryActionRemovedRecord,
                    StaleRecoveryRegistryNames.SharedWorkerRegistry,
                    record.KbKey,
                    4120,
                    AlignedStartTicks,
                    RecoveringGatewayPid,
                    StaleReason.OwnerNotRunning),
                entry);
        }

        [Fact]
        public void RemoveStaleRecord_RemovesAnUnparseableFileWithUnknownPid()
        {
            string kbKey = Identity().KbKey;
            Directory.CreateDirectory(_root);
            string path = SharedWorkerRegistry.GetRecordPathV2(kbKey, _root);
            File.WriteAllText(path, "{ truncated");
            var log = new List<string>();

            var result = SharedWorkerRegistry.RemoveStaleRecord(
                kbKey, null, StaleReason.UnparseableRecord, Context(new FakeProcessProbe(), log));

            Assert.Equal(StaleRecordRemovalOutcome.Removed, result.Outcome);
            Assert.False(File.Exists(path));
            string entry = Assert.Single(log);
            Assert.Contains("registry=SharedWorkerRegistry", entry);
            Assert.Contains("key=" + kbKey, entry);
            Assert.Contains(" pid=unknown ", entry);
            Assert.Contains("recoveringGatewayPid=" + RecoveringGatewayPid, entry);
            Assert.EndsWith("reason=unparseable-record", entry);
        }

        [Fact]
        public void RemoveStaleRecord_LeavesARecordRewrittenByANewHostAndLogsNothing()
        {
            var identity = Identity();
            var stale = Record(identity, hostPid: 4120, generation: 1);
            SharedWorkerRegistry.WriteRecordV2(Record(identity, hostPid: 4124, generation: 1), _root);
            var log = new List<string>();

            var result = SharedWorkerRegistry.RemoveStaleRecord(
                identity.KbKey, stale, StaleReason.OwnerNotRunning, Context(new FakeProcessProbe(), log));

            Assert.Equal(StaleRecordRemovalOutcome.RecordChanged, result.Outcome);
            Assert.Equal(4124, SharedWorkerRegistry.ReadRecordV2(identity.KbKey, _root).Record!.HostPid);
            Assert.Empty(log);
            Assert.Null(result.LogEntry);
        }

        [Fact]
        public void RemoveStaleRecord_ReportsAlreadyAbsentWithoutALogEntry()
        {
            var record = Record(Identity(), hostPid: 4120, generation: 1);
            var log = new List<string>();

            var result = SharedWorkerRegistry.RemoveStaleRecord(
                record.KbKey, record, StaleReason.OwnerNotRunning, Context(new FakeProcessProbe(), log));

            Assert.Equal(StaleRecordRemovalOutcome.AlreadyAbsent, result.Outcome);
            Assert.Empty(log);
        }

        [Fact]
        public void RemoveStaleRecord_ReportsRemoveFailedWithKeyAndError_WhenTheFileCannotBeDeleted()
        {
            var record = Record(Identity(), hostPid: 4120, generation: 1);
            string path = SharedWorkerRegistry.WriteRecordV2(record, _root);
            var log = new List<string>();

            StaleRecordRemovalResult result;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = SharedWorkerRegistry.RemoveStaleRecord(
                    record.KbKey, record, StaleReason.StartTimeMismatch, Context(new FakeProcessProbe(), log));
            }

            Assert.Equal(StaleRecordRemovalOutcome.RemoveFailed, result.Outcome);
            Assert.Equal(record.KbKey, result.RecordKey);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.True(File.Exists(path));
            Assert.Empty(log);
        }

        [Fact]
        public void RemoveStaleRecord_RejectsReasonNone()
        {
            var record = Record(Identity(), hostPid: 4120, generation: 1);
            SharedWorkerRegistry.WriteRecordV2(record, _root);

            Assert.Throws<ArgumentException>(() => SharedWorkerRegistry.RemoveStaleRecord(
                record.KbKey, record, StaleReason.None, Context(new FakeProcessProbe(), new List<string>())));
            Assert.True(File.Exists(SharedWorkerRegistry.GetRecordPathV2(record.KbKey, _root)));
        }

        // ---------------------------------------------------------------
        // StopOrphanExact
        // ---------------------------------------------------------------

        [Fact]
        public void StopOrphanExact_ReReadsStartTimeAndImagePathBeforeTerminating_AndWaitsForExit()
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks + 5_000));
            var fakes = new FakeProcesses(probe);
            fakes.Images[OrphanPid] = OrphanImage.ToUpperInvariant();
            fakes.ExitOnTerminate = true;

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.Stopped, result.Outcome);
            Assert.True(result.Stopped);
            Assert.Equal(OrphanPid, result.Pid);
            Assert.Equal(new[] { OrphanPid }, fakes.Terminated.ToArray());
            int terminateAt = probe.Events.IndexOf("terminate:" + OrphanPid);
            int probeAt = probe.Events.IndexOf("probe:" + OrphanPid);
            int imageAt = probe.Events.IndexOf("image:" + OrphanPid);
            Assert.InRange(probeAt, 0, terminateAt - 1);
            Assert.InRange(imageAt, 0, terminateAt - 1);
            Assert.Contains("probe:" + OrphanPid, probe.Events.Skip(terminateAt + 1));
        }

        [Fact]
        public void StopOrphanExact_RefusesAStartTimeMismatch()
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks + TimeSpan.TicksPerMillisecond));
            var fakes = new FakeProcesses(probe);
            fakes.Images[OrphanPid] = OrphanImage;

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.RefusedStartTimeMismatch, result.Outcome);
            Assert.False(result.Stopped);
            Assert.Empty(fakes.Terminated);
        }

        [Fact]
        public void StopOrphanExact_RefusesAnUnreadableStartTime()
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.RunningStartTimeUnreadable);
            var fakes = new FakeProcesses(probe);
            fakes.Images[OrphanPid] = OrphanImage;

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.RefusedStartTimeUnreadable, result.Outcome);
            Assert.Empty(fakes.Terminated);
        }

        [Fact]
        public void StopOrphanExact_RefusesAnImagePathMismatch()
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks));
            var fakes = new FakeProcesses(probe);
            fakes.Images[OrphanPid] = @"C:\Windows\System32\notepad.exe";

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.RefusedImagePathMismatch, result.Outcome);
            Assert.Empty(fakes.Terminated);
        }

        [Fact]
        public void StopOrphanExact_RefusesAnUnreadableImagePath()
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks));
            var fakes = new FakeProcesses(probe);

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.RefusedImagePathUnreadable, result.Outcome);
            Assert.Empty(fakes.Terminated);
        }

        [Fact]
        public void StopOrphanExact_ReportsAlreadyExited_WithoutTerminating()
        {
            var fakes = new FakeProcesses(new FakeProcessProbe());

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.AlreadyExited, result.Outcome);
            Assert.Empty(fakes.Terminated);
        }

        [Fact]
        public void StopOrphanExact_RefusesTheRecoveringGatewayPid_EvenWhenEverythingMatches()
        {
            var probe = new FakeProcessProbe();
            probe.Set(RecoveringGatewayPid, ProcessProbeResult.Running(AlignedStartTicks));
            var fakes = new FakeProcesses(probe);
            fakes.Images[RecoveringGatewayPid] = OrphanImage;

            var result = SharedWorkerRegistry.StopOrphanExact(RecoveringGatewayPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.RefusedGatewayPid, result.Outcome);
            Assert.Empty(fakes.Terminated);
            Assert.Empty(probe.Events);
        }

        [Fact]
        public void StopOrphanExact_RefusesAPidRecordedAsAGateway()
        {
            const int otherGatewayPid = 7100;
            var probe = new FakeProcessProbe();
            probe.Set(otherGatewayPid, ProcessProbeResult.Running(AlignedStartTicks));
            var fakes = new FakeProcesses(probe) { GatewayPids = new[] { otherGatewayPid } };
            fakes.Images[otherGatewayPid] = OrphanImage;

            var result = SharedWorkerRegistry.StopOrphanExact(otherGatewayPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.RefusedGatewayPid, result.Outcome);
            Assert.Empty(fakes.Terminated);
        }

        [Theory]
        [InlineData(0, AlignedStartTicks, OrphanImage)]
        [InlineData(-4, AlignedStartTicks, OrphanImage)]
        [InlineData(OrphanPid, 0L, OrphanImage)]
        [InlineData(OrphanPid, AlignedStartTicks, "")]
        public void StopOrphanExact_RefusesInvalidArguments(int pid, long startTicks, string image)
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks));
            var fakes = new FakeProcesses(probe);
            fakes.Images[OrphanPid] = OrphanImage;

            var result = SharedWorkerRegistry.StopOrphanExact(pid, startTicks, image, fakes.Context());

            Assert.Equal(OrphanStopOutcome.RefusedInvalidArguments, result.Outcome);
            Assert.Empty(fakes.Terminated);
        }

        [Fact]
        public void StopOrphanExact_ReportsKillFailed_WhenTerminationReturnsAnError()
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks));
            var fakes = new FakeProcesses(probe) { TerminateError = "access denied" };
            fakes.Images[OrphanPid] = OrphanImage;

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.KillFailed, result.Outcome);
            Assert.Contains("access denied", result.Error);
        }

        [Fact]
        public void StopOrphanExact_ReportsStillRunning_WhenTheProcessOutlivesTheStopWait()
        {
            var probe = new FakeProcessProbe();
            probe.Set(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks));
            var fakes = new FakeProcesses(probe) { ExitOnTerminate = false };
            fakes.Images[OrphanPid] = OrphanImage;

            var result = SharedWorkerRegistry.StopOrphanExact(OrphanPid, AlignedStartTicks, OrphanImage, fakes.Context());

            Assert.Equal(OrphanStopOutcome.StillRunning, result.Outcome);
            Assert.False(result.Stopped);
            Assert.Equal(new[] { OrphanPid }, fakes.Terminated.ToArray());
        }

        // ---------------------------------------------------------------
        // SelectProcessesToTerminate (pure kill-set selector, examples; Property 10 is task 4.6)
        // ---------------------------------------------------------------

        [Fact]
        public void SelectProcessesToTerminate_KeepsOnlyExactOrphansOfDeadOwners()
        {
            const int deadOwner = 3000, liveOwner = 3004;
            var orphan = Candidate("orphan", deadOwner, OrphanPid);
            var ofLiveOwner = Candidate("live-owner", liveOwner, 5016);
            var imageMismatch = Candidate("image", deadOwner, 5020);
            var startMismatch = Candidate("start", deadOwner, 5024);
            var notRunning = Candidate("gone", deadOwner, 5028);
            var gatewayPid = Candidate("gateway", deadOwner, RecoveringGatewayPid);
            var ownerOfOther = Candidate("owner-pid", deadOwner, liveOwner);

            var observations = new Dictionary<int, ProcessObservation>
            {
                [liveOwner] = Observe(liveOwner, ProcessProbeResult.Running(AlignedStartTicks), OrphanImage),
                [OrphanPid] = Observe(OrphanPid, ProcessProbeResult.Running(AlignedStartTicks + 9_999), OrphanImage.ToLowerInvariant()),
                [5016] = Observe(5016, ProcessProbeResult.Running(AlignedStartTicks), OrphanImage),
                [5020] = Observe(5020, ProcessProbeResult.Running(AlignedStartTicks), @"C:\Other\GxMcp.Worker.exe"),
                [5024] = Observe(5024, ProcessProbeResult.Running(AlignedStartTicks + TimeSpan.TicksPerMillisecond), OrphanImage),
                [RecoveringGatewayPid] = Observe(RecoveringGatewayPid, ProcessProbeResult.Running(AlignedStartTicks), OrphanImage)
            };

            var selected = SharedWorkerRegistry.SelectProcessesToTerminate(
                new[] { orphan, ofLiveOwner, imageMismatch, startMismatch, notRunning, gatewayPid, ownerOfOther, orphan },
                observations,
                new[] { RecoveringGatewayPid });

            Assert.Equal(new[] { "orphan" }, selected.Select(c => c.RecordKey).ToArray());
        }

        [Fact]
        public void SelectProcessesToTerminate_TreatsAnOwnerWithUnreadableOrUnrecordedStartTimeAsLive()
        {
            const int unreadableOwner = 3008, legacyOwner = 3012;
            var ofUnreadable = Candidate("unreadable", unreadableOwner, 5032);
            var ofLegacy = Candidate("legacy", legacyOwner, 5036, ownerStartTicks: 0);
            var observations = new Dictionary<int, ProcessObservation>
            {
                [unreadableOwner] = Observe(unreadableOwner, ProcessProbeResult.RunningStartTimeUnreadable, null),
                [legacyOwner] = Observe(legacyOwner, ProcessProbeResult.Running(AlignedStartTicks), null),
                [5032] = Observe(5032, ProcessProbeResult.Running(AlignedStartTicks), OrphanImage),
                [5036] = Observe(5036, ProcessProbeResult.Running(AlignedStartTicks), OrphanImage)
            };

            var selected = SharedWorkerRegistry.SelectProcessesToTerminate(
                new[] { ofUnreadable, ofLegacy }, observations, Array.Empty<int>());

            Assert.Empty(selected);
        }

        // ---------------------------------------------------------------
        // WorkerOwnershipRegistry.ScanByKbPath
        // ---------------------------------------------------------------

        [Fact]
        public void WorkerOwnershipDefaultRoot_IsUnderLocalAppDataWorkerOwnership()
        {
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GxMcp", "worker-ownership"),
                WorkerOwnershipRegistry.DefaultRoot);
        }

        [Fact]
        public void ScanByKbPath_ReturnsOnlyRecordsOfThatKbForAnyWorkerExecutable_AndChangesNoFile()
        {
            Directory.CreateDirectory(_root);
            WriteOwnership("a", @"C:\checkout-a\worker\GxMcp.Worker.exe", @"c:\kbs\kbteste", gatewayPid: 3000);
            WriteOwnership("b", @"C:\checkout-b\worker\GxMcp.Worker.exe", @"C:\KBs\KbTeste\", gatewayPid: 3004);
            WriteOwnership("other", @"C:\checkout-a\worker\GxMcp.Worker.exe", @"c:\kbs\otherteste", gatewayPid: 3008);
            File.WriteAllText(Path.Combine(_root, "broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(_root, "c.json.tmp-1234"),
                JsonConvert.SerializeObject(Ownership("c", @"C:\checkout-c\GxMcp.Worker.exe", @"c:\kbs\kbteste", 3012)));
            var before = Snapshot(_root);

            var records = WorkerOwnershipRegistry.ScanByKbPath(@"C:\KBS\KBTESTE", _root);

            Assert.Equal(new[] { "a", "b" }, records.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { 3000, 3004 }, records.Select(r => r.GatewayPid).OrderBy(p => p).ToArray());
            Assert.Equal(before, Snapshot(_root));
        }

        [Fact]
        public void ScanByKbPath_ReturnsEmptyForAMissingRootWithoutCreatingIt()
        {
            var records = WorkerOwnershipRegistry.ScanByKbPath(@"C:\KBs\KbTeste", _root);

            Assert.Empty(records);
            Assert.False(Directory.Exists(_root));
        }

        // ---------------------------------------------------------------
        // Helpers and fakes
        // ---------------------------------------------------------------

        private static SharedWorkerIdentity Identity(
            string worker = @"C:\Tools\GxMcp\worker\GxMcp.Worker.exe",
            string kbPath = @"C:\KBs\KbTeste",
            string installation = @"C:\GeneXus18",
            string driver = "native-sdk",
            string major = "18",
            string? profile = @"C:\profiles\default\config.json") =>
            SharedWorkerIdentity.Create(worker, kbPath, installation, driver, major, profile);

        private static SharedHostRecord Record(SharedWorkerIdentity identity, int hostPid, int generation) =>
            new SharedHostRecord
            {
                KbKey = identity.KbKey,
                Identity = identity.ToRecordIdentity(),
                HostPid = hostPid,
                HostStartTimeUtcTicks = AlignedStartTicks,
                LaunchedByGatewayPid = RecoveringGatewayPid,
                WorkerPid = OrphanPid,
                WorkerStartTimeUtcTicks = AlignedStartTicks + 100_000_000L,
                PipeName = SharedWorkerRegistry.PipeNameV2(identity),
                Generation = generation,
                State = SharedHostRecordState.Ready,
                UpdatedUtc = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc)
            };

        private SharedWorkerRecoveryContext Context(IProcessProbe probe, List<string> log) =>
            new SharedWorkerRecoveryContext
            {
                Probe = probe,
                ReadImagePath = (int pid, out string? path, out string? error) =>
                {
                    path = null;
                    error = "no fake image for pid " + pid;
                    return false;
                },
                Terminate = pid => throw new InvalidOperationException("RemoveStaleRecord must not terminate pid " + pid),
                Log = log.Add,
                RecoveringGatewayPid = RecoveringGatewayPid,
                GatewayPids = Array.Empty<int>(),
                StopWaitTimeout = TimeSpan.FromMilliseconds(200),
                StopPollInterval = TimeSpan.FromMilliseconds(10),
                RegistryRoot = _root
            };

        private static OrphanCandidate Candidate(string key, int ownerPid, int workerPid, long ownerStartTicks = AlignedStartTicks) =>
            new OrphanCandidate
            {
                RegistryName = StaleRecoveryRegistryNames.WorkerOwnershipRegistry,
                RecordKey = key,
                OwnerGatewayPid = ownerPid,
                OwnerGatewayStartTimeUtcTicks = ownerStartTicks,
                WorkerPid = workerPid,
                WorkerStartTimeUtcTicks = AlignedStartTicks,
                ExpectedImagePath = OrphanImage
            };

        private static ProcessObservation Observe(int pid, ProcessProbeResult probe, string? image) =>
            new ProcessObservation { Pid = pid, Probe = probe, ImagePath = image };

        private static WorkerOwnershipRecord Ownership(string key, string worker, string kbPath, int gatewayPid) =>
            new WorkerOwnershipRecord
            {
                Key = key,
                GatewayPid = gatewayPid,
                GatewayStartTimeUtcTicks = AlignedStartTicks,
                WorkerPid = gatewayPid + 2000,
                WorkerStartTimeUtcTicks = AlignedStartTicks + 1,
                WorkerExecutable = worker,
                KbPath = kbPath
            };

        private void WriteOwnership(string key, string worker, string kbPath, int gatewayPid) =>
            File.WriteAllText(Path.Combine(_root, key + ".json"),
                JsonConvert.SerializeObject(Ownership(key, worker, kbPath, gatewayPid)));

        private static string[] Snapshot(string root) =>
            Directory.GetFiles(root)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Path.GetFileName(f) + "=" + File.GetLastWriteTimeUtc(f).Ticks + ":" + File.ReadAllText(f))
                .ToArray();

        private static string Sha256Hex(string value)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            var sb = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private sealed class FakeProcessProbe : IProcessProbe
        {
            private readonly Dictionary<int, ProcessProbeResult> _results = new Dictionary<int, ProcessProbeResult>();

            internal List<string> Events { get; } = new List<string>();

            internal void Set(int pid, ProcessProbeResult result) => _results[pid] = result;

            internal void Exit(int pid) => _results.Remove(pid);

            public ProcessProbeResult Probe(int pid)
            {
                Events.Add("probe:" + pid);
                return _results.TryGetValue(pid, out var result) ? result : ProcessProbeResult.NotRunning;
            }
        }

        private sealed class FakeProcesses
        {
            private readonly FakeProcessProbe _probe;

            internal FakeProcesses(FakeProcessProbe probe) => _probe = probe;

            internal Dictionary<int, string> Images { get; } = new Dictionary<int, string>();

            internal List<int> Terminated { get; } = new List<int>();

            internal bool ExitOnTerminate { get; set; } = true;

            internal string? TerminateError { get; set; }

            internal IReadOnlyCollection<int> GatewayPids { get; set; } = Array.Empty<int>();

            internal SharedWorkerRecoveryContext Context() =>
                new SharedWorkerRecoveryContext
                {
                    Probe = _probe,
                    ReadImagePath = (int pid, out string? path, out string? error) =>
                    {
                        _probe.Events.Add("image:" + pid);
                        if (Images.TryGetValue(pid, out var image))
                        {
                            path = image;
                            error = null;
                            return true;
                        }

                        path = null;
                        error = "access denied for pid " + pid;
                        return false;
                    },
                    Terminate = pid =>
                    {
                        _probe.Events.Add("terminate:" + pid);
                        Terminated.Add(pid);
                        if (TerminateError != null) return TerminateError;
                        if (ExitOnTerminate) _probe.Exit(pid);
                        return null;
                    },
                    Log = _ => { },
                    RecoveringGatewayPid = RecoveringGatewayPid,
                    GatewayPids = GatewayPids,
                    StopWaitTimeout = TimeSpan.FromMilliseconds(200),
                    StopPollInterval = TimeSpan.FromMilliseconds(10),
                    RegistryRoot = null
                };
        }
    }
}
