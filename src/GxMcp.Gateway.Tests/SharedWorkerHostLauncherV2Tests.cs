using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2 Connect sequence of the shared-host launcher (task 4.4; Requirements 2.1, 2.3, 2.4, 2.7, 2.8, 7.1, 7.2,
    // 7.3, 7.7, 7.8, 7.9; design "SharedWorkerHostLauncher.cs (v2)", decisions A3, A4, D7).
    // These tests are written before the implementation (task 4.11) and pin the seam contract below; task 4.11
    // implements exactly these types, members and names. The registry, identity and recovery members come from
    // the task 4.5 contract (header of SharedWorkerRegistryV2Tests.cs) and are used here unchanged.
    //
    // ---- Entry point (src/GxMcp.Gateway/SharedWorkerHostLauncher.cs) ----
    //   internal static ISharedWorkerAttachment ConnectV2(SharedWorkerLaunchRequest request, SharedWorkerRecoveryContext context)
    //     Order (design): (1) identity = SharedWorkerIdentity.Create(request.WorkerPath, request.Kb.Path,
    //     request.InstallationPath, request.Driver, request.Major, request.ProfileConfigPath); kbKey = identity.KbKey.
    //     (2) read = SharedWorkerRegistry.ReadRecordV2(kbKey, context.RegistryRoot), classified with
    //     SharedHostRecordClassifier.Classify(read, context.Probe).
    //     (3) Live: SharedWorkerIdentity.BuildMismatch; a difference -> SHARED_WORKER_IDENTITY_MISMATCH; otherwise
    //     attach (step 9) to that record without the election mutex and without starting a host.
    //     (4) LivenessUnverified -> KB_WORKER_OWNED_ELSEWHERE, reason liveness-unverified, holderPid = record
    //     HostPid when the record parsed; the record file and every process are left unchanged.
    //     (5) WorkerOwnershipRegistry.ScanByKbPath(request.Kb.Path, context.OwnershipRegistryRoot): an owner Gateway
    //     that is live (PID and start time, Requirement 7 criterion 1) with a running Worker ->
    //     KB_WORKER_OWNED_ELSEWHERE, reason owner-does-not-accept-attachments, no termination. An owner Gateway that
    //     is not live with a Worker that matches PID, start time and image path -> SharedWorkerRegistry.StopOrphanExact(
    //     WorkerPid, WorkerStartTimeUtcTicks, WorkerExecutable, context); on Stopped (or AlreadyExited) delete the
    //     ownership record file and log FormatRecoveryLogEntry(RecoveryActionStoppedProcess,
    //     StaleRecoveryRegistryNames.WorkerOwnershipRegistry, record Key, WorkerPid, WorkerStartTimeUtcTicks,
    //     context.RecoveringGatewayPid, StaleReason.OrphanedWorker) plus one RecoveryActionRemovedRecord entry for the
    //     deleted file (same registry, key and reason). Any other stop outcome ends recovery with
    //     SHARED_WORKER_RECOVERY_FAILED before anything else changes.
    //     (6) context.AcquireElectionMutex(SharedWorkerRegistry.ElectionMutexNameV2(kbKey), timeout).
    //     (7) Stale v2 record (parsed or unparseable): SharedWorkerRegistry.RemoveStaleRecord(kbKey, record, reason,
    //     context); RemoveFailed -> SHARED_WORKER_RECOVERY_FAILED; AlreadyAbsent and Removed continue; RecordChanged
    //     re-reads and re-classifies. Steps 2 to 6 must complete within 5 seconds of the first read
    //     (Requirement 7 criterion 2).
    //     (8) context.StartHost(new SharedWorkerHostStartRequest { ... }), then poll ReadRecordV2 every
    //     context.RegistryPollInterval (through context.Clock.Sleep) until the record is Live, bounded by
    //     request.LaunchTimeoutMs; then dispose the election mutex.
    //     (9) attachment = context.ConnectPipe(identity, record, clientId, timeoutMs). IOException,
    //     TimeoutException or InvalidOperationException -> SHARED_WORKER_ATTACH_FAILED, reason pipe-refused.
    //     (10) Readiness: ready when attachment.IsSdkReady is true, or when the re-read v2 record of the same host
    //     (same HostPid and HostStartTimeUtcTicks) has State == SharedHostRecordState.Ready. Poll every
    //     context.RegistryPollInterval through context.Clock.Sleep, measured with context.Clock.UtcNow, up to
    //     request.Config.EffectiveSharedHost.AttachReadyTimeoutSeconds (decision D7). A timeout disposes the
    //     attachment and throws SHARED_WORKER_ATTACH_FAILED, reason readiness-timeout. A ready host returns the
    //     attachment within one poll interval of becoming ready.
    //     No step starts a second host while the recorded host is live. All typed errors are thrown as
    //     SharedWorkerLaunchException; catalog codes are built with ConcurrencyErrorCatalog.Build.
    //   internal static SharedWorkerConnection Connect(Configuration, KbHandle, string workerPath,
    //       string installationPath, string driver, string major, string? legacyProvider = null, int timeoutMs = 30000)
    //     Keeps its signature; delegates to ConnectV2 with production defaults and ProfileConfigPath =
    //     Configuration.CurrentConfigPath, and returns the attachment as SharedWorkerConnection.
    //
    // ---- SharedWorkerLaunchRequest (new, same file; init-only) ----
    //   Configuration Config; KbHandle Kb; string WorkerPath; string InstallationPath; string Driver; string Major;
    //   string? ProfileConfigPath; string? LegacyProvider; int LaunchTimeoutMs = 30000
    //
    // ---- SharedWorkerRecoveryContext additions (src/GxMcp.Gateway/SharedWorkerIdentity.cs, next to the 4.5 members) ----
    //   string? OwnershipRegistryRoot                     null -> WorkerOwnershipRegistry.DefaultRoot
    //   ISharedWorkerLauncherClock Clock                  SharedWorkerSystemClock.Instance
    //   SharedWorkerHostStarter StartHost                 production: Process.Start of the broker (today's StartHost body)
    //   SharedWorkerPipeConnector ConnectPipe             production: SharedWorkerConnection over the record's pipe
    //   SharedWorkerElectionMutexFactory AcquireElectionMutex   production: named System.Threading.Mutex,
    //                                                     AbandonedMutexException counts as acquired
    //   TimeSpan RegistryPollInterval                     100 ms
    //   StopOrphanExact (task 4.9) measures StopWaitTimeout with Clock.UtcNow and waits with Clock.Sleep(StopPollInterval),
    //   so the 10-second stop wait of Requirement 7 criterion 9 is deterministic under a fake clock. The 4.5 tests
    //   leave Clock at its default and keep their behavior.
    //
    // ---- New seam types (src/GxMcp.Gateway/SharedWorkerHostLauncher.cs) ----
    //   internal interface ISharedWorkerLauncherClock { DateTime UtcNow { get; } void Sleep(TimeSpan duration); }
    //   internal sealed class SharedWorkerSystemClock : ISharedWorkerLauncherClock
    //       static SharedWorkerSystemClock Instance; UtcNow = DateTime.UtcNow; Sleep = Thread.Sleep
    //   internal interface ISharedWorkerAttachment : IDisposable
    //       SharedWorkerAttachInfo? AttachInfo { get; }
    //       bool IsSdkReady { get; }                          latest "ready" flag from attach_ack or heartbeat_ack
    //     SharedWorkerConnection implements it and gains a constructor (SharedWorkerIdentity, SharedHostRecord, string clientId).
    //   internal delegate ISharedWorkerAttachment SharedWorkerPipeConnector(
    //       SharedWorkerIdentity identity, SharedHostRecord record, string clientId, int timeoutMs);
    //     Throws IOException, TimeoutException or InvalidOperationException when the pipe refuses or drops the
    //     connection or the attach handshake fails.
    //   internal delegate void SharedWorkerHostStarter(SharedWorkerHostStartRequest request);
    //     Throws InvalidOperationException when the host process cannot be started. The production starter passes
    //     --registry-dir when request.RegistryRoot is set.
    //   internal sealed class SharedWorkerHostStartRequest (init-only)
    //       SharedWorkerIdentity Identity; string KbKey; KbHandle Kb; string WorkerPath; string InstallationPath;
    //       string Driver; string Major; string? LegacyProvider; string PipeName (= PipeNameV2(identity));
    //       string Arguments (= BuildHostArguments(...)); string? RegistryRoot
    //   internal delegate IDisposable? SharedWorkerElectionMutexFactory(string mutexName, TimeSpan timeout);
    //     Returns the held mutex, or null when it was not acquired within timeout. Disposing releases it.
    //   internal sealed class SharedWorkerLaunchException : InvalidOperationException
    //       string Code; string? Reason; JObject Error            Error is the typed error object
    //   internal static class SharedWorkerRecoveryFailure      Requirement 7 criterion 9 (no catalog row exists)
    //       const string Code = "SHARED_WORKER_RECOVERY_FAILED";
    //       const string RemoveFailed = "remove-failed"; const string KillFailed = "kill-failed";
    //       const string StillRunning = "still-running";
    //     Error: code, message, retryable=false, dispatchState="not-delivered", recordKey, registry,
    //     failureReason, and blockingPid when known.
    //
    // ---- Typed error details pinned here ----
    //   KB_WORKER_OWNED_ELSEWHERE: holderPid; owner-does-not-accept-attachments adds holderStartTimeUtcTicks and
    //   releaseActions = [ { action: "close-holder-session", pid }, { action: "set-worker-sharing-mode",
    //   setting: "Server.WorkerSharingMode", value: "shared-host" } ]. HolderPid is passed to the catalog so the
    //   hint names it.
    //
    // No test here starts a process, opens a pipe, or touches the real %LOCALAPPDATA%: the registries use
    // temporary roots and every time bound is measured on the fake clock, never with a wall-clock sleep.
    public sealed class SharedWorkerHostLauncherV2Tests : IDisposable
    {
        private const long AlignedStartTicks = 638_700_000_000_000_000L;
        private const int RecoveringGatewayPid = 3900;
        private const int HostPid = 4120;
        private const int NewHostPid = 4124;
        private const int LiveWorkerPid = 5000;
        private const int OwnerGatewayPid = 3000;
        private const long OwnerGatewayStartTicks = AlignedStartTicks + 20_000_000L;
        private const int OrphanWorkerPid = 5012;
        private const long OrphanWorkerStartTicks = AlignedStartTicks + 30_000_000L;
        private const string WorkerPath = @"C:\Tools\GxMcp\worker\GxMcp.Worker.exe";
        private const string InstallationPath = @"C:\GeneXus18";
        private const string Driver = "native-sdk";
        private const string Major = "18";
        private const string ProfilePath = @"C:\profiles\default\config.json";
        private const string KbPath = @"C:\KBs\KbTeste";

        private readonly string _root;
        private readonly string _v2Root;
        private readonly string _ownershipRoot;
        private readonly Harness _h;

        public SharedWorkerHostLauncherV2Tests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxmcp-launcher-v2-" + Guid.NewGuid().ToString("N"));
            _v2Root = Path.Combine(_root, "shared-workers", "v2");
            _ownershipRoot = Path.Combine(_root, "worker-ownership");
            _h = new Harness(_v2Root, _ownershipRoot);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---------------------------------------------------------------
        // Requirement 2 criteria 1 and 3: attach to a live host
        // ---------------------------------------------------------------

        [Fact]
        public void LiveRecord_AttachesToTheRecordedHost_WithoutElectionOrASecondHost()
        {
            var identity = Identity();
            var record = SeedLiveHost(identity, SharedHostRecordState.Ready);

            var attachment = SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context());

            var connect = Assert.Single(_h.Connects);
            Assert.Same(connect.Attachment, attachment);
            Assert.Equal(HostPid, connect.Record.HostPid);
            Assert.Equal(SharedWorkerRegistry.PipeNameV2(identity), connect.Record.PipeName);
            Assert.Equal(identity.Key, connect.Identity.Key);
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Election.Requested);
            Assert.Empty(_h.Terminated);
            Assert.False(connect.Attachment.Disposed);
            Assert.Equal(record.ToJson(), File.ReadAllText(RecordPath(identity)));
        }

        [Fact]
        public void KbPathVariants_ResolveToOneKbKey_AndAttachToTheSameHost()
        {
            string absolute = Path.Combine(Path.GetTempPath(), "GxMcp-Launcher-" + Guid.NewGuid().ToString("N"), "KbTeste");
            var identity = Identity(absolute);
            SeedLiveHost(identity, SharedHostRecordState.Ready);
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
                Assert.Equal(identity.KbKey, Identity(variant).KbKey);
                SharedWorkerHostLauncher.ConnectV2(Request(variant), _h.Context());
            }

            Assert.Equal(variants.Length, _h.Connects.Count);
            Assert.All(_h.Connects, c =>
            {
                Assert.Equal(identity.KbKey, c.Identity.KbKey);
                Assert.Equal(identity.Key, c.Identity.Key);
                Assert.Equal(HostPid, c.Record.HostPid);
            });
            Assert.Empty(_h.Starts);
            Assert.Single(Directory.GetFiles(_v2Root));
        }

        [Fact]
        public void ReadyHost_AnswersTheOpenWithin5000Ms()
        {
            SeedLiveHost(Identity(), SharedHostRecordState.Ready);
            _h.ConnectCost = TimeSpan.FromMilliseconds(40);

            SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context());

            Assert.InRange(_h.Clock.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(5000));
        }

        // ---------------------------------------------------------------
        // Requirement 2 criterion 7: a starting host answers within 5000 ms after ready
        // ---------------------------------------------------------------

        [Fact]
        public void StartingHost_WaitsForReady_AndAnswersWithin5000MsAfterReady()
        {
            var identity = Identity();
            var record = SeedLiveHost(identity, SharedHostRecordState.Starting);
            _h.AttachmentReady = false;
            TimeSpan readyAt = TimeSpan.FromSeconds(30);
            _h.Clock.At(readyAt, () =>
            {
                record.State = SharedHostRecordState.Ready;
                SharedWorkerRegistry.WriteRecordV2(record, _v2Root);
                foreach (var c in _h.Connects) c.Attachment.IsSdkReady = true;
            });

            var attachment = SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context());

            Assert.Same(Assert.Single(_h.Connects).Attachment, attachment);
            Assert.False(((FakeAttachment)attachment).Disposed);
            Assert.InRange(_h.Clock.Elapsed, readyAt, readyAt + TimeSpan.FromMilliseconds(5000));
            Assert.Empty(_h.Starts);
        }

        // ---------------------------------------------------------------
        // Requirement 2 criterion 8: readiness-timeout and pipe-refused
        // ---------------------------------------------------------------

        [Fact]
        public void HostThatNeverBecomesReady_GivesReadinessTimeoutAfterTheAttachReadinessWait_WithNoSecondHost()
        {
            var identity = Identity();
            var record = SeedLiveHost(identity, SharedHostRecordState.Starting);
            _h.AttachmentReady = false;

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(Request(attachReadyTimeoutSeconds: 10), _h.Context()));

            AssertCatalogError(ex, ConcurrencyErrorCatalog.SharedWorkerAttachFailed, "readiness-timeout", retryable: true);
            Assert.InRange(_h.Clock.Elapsed, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11));
            Assert.True(Assert.Single(_h.Connects).Attachment.Disposed);
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Election.Requested);
            Assert.Equal(record.ToJson(), File.ReadAllText(RecordPath(identity)));
        }

        [Fact]
        public void ReadinessWait_DefaultsToTheEffectiveAttachReadyTimeoutOf120Seconds()
        {
            SeedLiveHost(Identity(), SharedHostRecordState.Starting);
            _h.AttachmentReady = false;

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context()));

            Assert.Equal("readiness-timeout", ex.Reason);
            Assert.InRange(_h.Clock.Elapsed, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(121));
        }

        [Fact]
        public void PipeRefused_GivesAttachFailed_WithNoAttachmentAndNoSecondHost()
        {
            var identity = Identity();
            var record = SeedLiveHost(identity, SharedHostRecordState.Ready);
            _h.ConnectFailure = () => new IOException("All pipe instances are busy.");

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context()));

            AssertCatalogError(ex, ConcurrencyErrorCatalog.SharedWorkerAttachFailed, "pipe-refused", retryable: true);
            Assert.Equal(1, _h.ConnectAttempts);
            Assert.Empty(_h.Connects);
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Election.Requested);
            Assert.Equal(record.ToJson(), File.ReadAllText(RecordPath(identity)));
        }

        // ---------------------------------------------------------------
        // Requirement 2 criterion 4: a live Isolated_Mode owner
        // ---------------------------------------------------------------

        [Fact]
        public void LiveIsolatedModeOwner_GivesOwnedElsewhereWithHolderAndReleaseActions_AndKillsNothing()
        {
            _h.Probe.Set(OwnerGatewayPid, ProcessProbeResult.Running(OwnerGatewayStartTicks));
            _h.Probe.Set(LiveWorkerPid, ProcessProbeResult.Running(AlignedStartTicks));
            _h.Images[LiveWorkerPid] = WorkerPath;
            WriteOwnership("isolated-owner", OwnerGatewayPid, OwnerGatewayStartTicks, LiveWorkerPid, AlignedStartTicks);
            var before = Snapshot();

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context()));

            var error = AssertCatalogError(ex, ConcurrencyErrorCatalog.KbWorkerOwnedElsewhere,
                "owner-does-not-accept-attachments", retryable: false);
            Assert.Equal(OwnerGatewayPid, (int)error["holderPid"]!);
            Assert.Equal(OwnerGatewayStartTicks, (long)error["holderStartTimeUtcTicks"]!);
            Assert.Contains(OwnerGatewayPid.ToString(), (string)error["hint"]!);
            var actions = Assert.IsType<JArray>(error["releaseActions"]);
            Assert.Contains(actions, a =>
                (string?)a["action"] == "close-holder-session" && (int?)a["pid"] == OwnerGatewayPid);
            Assert.Contains(actions, a =>
                (string?)a["action"] == "set-worker-sharing-mode"
                && (string?)a["setting"] == "Server.WorkerSharingMode"
                && (string?)a["value"] == "shared-host");
            Assert.Empty(_h.Terminated);
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Connects);
            Assert.Empty(_h.Election.Requested);
            Assert.Equal(before, Snapshot());
        }

        // ---------------------------------------------------------------
        // Requirement 7 criterion 8: liveness-unverified
        // ---------------------------------------------------------------

        [Fact]
        public void UnreadableHostStartTime_GivesLivenessUnverified_AndLeavesRecordsAndProcessesUnchanged()
        {
            var identity = Identity();
            SharedWorkerRegistry.WriteRecordV2(Record(identity, HostPid, SharedHostRecordState.Ready), _v2Root);
            _h.Probe.Set(HostPid, ProcessProbeResult.RunningStartTimeUnreadable);
            WriteOwnership("dead-owner", OwnerGatewayPid, OwnerGatewayStartTicks, OrphanWorkerPid, OrphanWorkerStartTicks);
            _h.Probe.Set(OrphanWorkerPid, ProcessProbeResult.Running(OrphanWorkerStartTicks));
            _h.Images[OrphanWorkerPid] = WorkerPath;
            var before = Snapshot();

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context()));

            var error = AssertCatalogError(ex, ConcurrencyErrorCatalog.KbWorkerOwnedElsewhere,
                "liveness-unverified", retryable: false);
            Assert.Equal(HostPid, (int)error["holderPid"]!);
            Assert.Equal(before, Snapshot());
            Assert.Empty(_h.Terminated);
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Connects);
            Assert.Empty(_h.Election.Requested);
            Assert.Empty(_h.Log);
        }

        // ---------------------------------------------------------------
        // Requirement 7 criteria 1, 2 and 7: stale v2 record
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("owner-not-running")]
        [InlineData("start-time-mismatch")]
        [InlineData("unparseable-record")]
        public void StaleRecord_IsRemovedWithTheRequiredLogEntry_AndElectionStartsWithin5Seconds(string kind)
        {
            var identity = Identity();
            var stale = Record(identity, HostPid, SharedHostRecordState.Ready);
            StaleReason reason;
            switch (kind)
            {
                case "owner-not-running":
                    SharedWorkerRegistry.WriteRecordV2(stale, _v2Root);
                    reason = StaleReason.OwnerNotRunning;
                    break;
                case "start-time-mismatch":
                    SharedWorkerRegistry.WriteRecordV2(stale, _v2Root);
                    _h.Probe.Set(HostPid, ProcessProbeResult.Running(AlignedStartTicks + TimeSpan.TicksPerMillisecond));
                    reason = StaleReason.StartTimeMismatch;
                    break;
                default:
                    Directory.CreateDirectory(_v2Root);
                    File.WriteAllText(RecordPath(identity), "{ truncated");
                    reason = StaleReason.UnparseableRecord;
                    break;
            }
            bool parsed = reason != StaleReason.UnparseableRecord;
            _h.OnStart = request => PublishNewHost(request, SharedHostRecordState.Ready);

            var attachment = SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context());

            string expectedEntry = SharedWorkerRegistry.FormatRecoveryLogEntry(
                SharedWorkerRegistry.RecoveryActionRemovedRecord,
                StaleRecoveryRegistryNames.SharedWorkerRegistry,
                identity.KbKey,
                parsed ? HostPid : 0,
                parsed ? AlignedStartTicks : 0,
                RecoveringGatewayPid,
                reason);
            Assert.Equal(1, _h.Log.Count(line => line == expectedEntry));
            Assert.Single(_h.Log, line => line.Contains("stale-ownership-recovery"));

            var election = Assert.Single(_h.Election.Requested);
            Assert.Equal(SharedWorkerRegistry.ElectionMutexNameV2(identity.KbKey), election.Name);
            Assert.InRange(election.At - FakeClock.Origin, TimeSpan.Zero, TimeSpan.FromSeconds(5));

            var start = Assert.Single(_h.Starts);
            Assert.Equal(identity.KbKey, start.KbKey);
            Assert.Equal(identity.Key, start.Identity.Key);
            Assert.Equal(SharedWorkerRegistry.PipeNameV2(identity), start.PipeName);
            Assert.Equal(_v2Root, start.RegistryRoot);

            var connect = Assert.Single(_h.Connects);
            Assert.Same(connect.Attachment, attachment);
            Assert.Equal(NewHostPid, connect.Record.HostPid);
            Assert.True(_h.Election.ReleasedBeforeConnect, "the election mutex must be released before the attach");
            Assert.Empty(_h.Terminated);
        }

        // ---------------------------------------------------------------
        // Requirement 7 criteria 3 and 7: orphaned Isolated_Mode Worker
        // ---------------------------------------------------------------

        [Fact]
        public void OrphanedIsolatedModeWorker_IsStoppedAndItsRecordRemoved_ThenElectionStarts()
        {
            var identity = Identity();
            WriteOwnership("orphan", OwnerGatewayPid, OwnerGatewayStartTicks, OrphanWorkerPid, OrphanWorkerStartTicks);
            _h.Probe.Set(OrphanWorkerPid, ProcessProbeResult.Running(OrphanWorkerStartTicks));
            _h.Images[OrphanWorkerPid] = WorkerPath.ToUpperInvariant();
            _h.ExitOnTerminate = true;
            _h.OnStart = request => PublishNewHost(request, SharedHostRecordState.Ready);

            var attachment = SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context());

            Assert.Equal(new[] { OrphanWorkerPid }, _h.Terminated.ToArray());
            Assert.False(File.Exists(Path.Combine(_ownershipRoot, "orphan.json")));
            Assert.Contains(
                SharedWorkerRegistry.FormatRecoveryLogEntry(
                    SharedWorkerRegistry.RecoveryActionStoppedProcess,
                    StaleRecoveryRegistryNames.WorkerOwnershipRegistry,
                    "orphan",
                    OrphanWorkerPid,
                    OrphanWorkerStartTicks,
                    RecoveringGatewayPid,
                    StaleReason.OrphanedWorker),
                _h.Log);
            Assert.Contains(_h.Log, line =>
                line.Contains("action=" + SharedWorkerRegistry.RecoveryActionRemovedRecord)
                && line.Contains("registry=" + StaleRecoveryRegistryNames.WorkerOwnershipRegistry)
                && line.Contains("key=orphan ")
                && line.Contains("recoveringGatewayPid=" + RecoveringGatewayPid)
                && line.EndsWith("reason=orphaned-worker"));
            Assert.InRange(_h.Clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            Assert.Single(_h.Election.Requested);
            Assert.Single(_h.Starts);
            Assert.Same(Assert.Single(_h.Connects).Attachment, attachment);
            Assert.DoesNotContain(OwnerGatewayPid, _h.Terminated);
        }

        // ---------------------------------------------------------------
        // Requirement 7 criterion 9: recovery failure
        // ---------------------------------------------------------------

        [Fact]
        public void OrphanStillRunning10SecondsAfterStop_EndsRecoveryWithATypedError_AndLeavesRemainingRecordsUnchanged()
        {
            var identity = Identity();
            SharedWorkerRegistry.WriteRecordV2(Record(identity, HostPid, SharedHostRecordState.Ready), _v2Root);
            WriteOwnership("orphan", OwnerGatewayPid, OwnerGatewayStartTicks, OrphanWorkerPid, OrphanWorkerStartTicks);
            _h.Probe.Set(OrphanWorkerPid, ProcessProbeResult.Running(OrphanWorkerStartTicks));
            _h.Images[OrphanWorkerPid] = WorkerPath;
            _h.ExitOnTerminate = false;
            var before = Snapshot();

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context(stopWaitTimeout: TimeSpan.FromSeconds(10))));

            Assert.Equal(SharedWorkerRecoveryFailure.Code, ex.Code);
            var error = ex.Error;
            Assert.Equal(SharedWorkerRecoveryFailure.Code, (string)error["code"]!);
            Assert.Equal("orphan", (string)error["recordKey"]!);
            Assert.Equal(StaleRecoveryRegistryNames.WorkerOwnershipRegistry, (string)error["registry"]!);
            Assert.Equal(OrphanWorkerPid, (int)error["blockingPid"]!);
            Assert.Equal(SharedWorkerRecoveryFailure.StillRunning, (string)error["failureReason"]!);
            Assert.False((bool)error["retryable"]!);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string)error["dispatchState"]!);
            Assert.InRange(_h.Clock.Elapsed, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11));
            Assert.Equal(new[] { OrphanWorkerPid }, _h.Terminated.ToArray());
            Assert.Equal(before, Snapshot());
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Connects);
            Assert.Empty(_h.Election.Requested);
        }

        [Fact]
        public void StaleRecordThatCannotBeRemoved_EndsRecoveryWithATypedError_AndStartsNoHost()
        {
            var identity = Identity();
            string path = SharedWorkerRegistry.WriteRecordV2(Record(identity, HostPid, SharedHostRecordState.Ready), _v2Root);
            string content = File.ReadAllText(path);

            SharedWorkerLaunchException ex;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                    SharedWorkerHostLauncher.ConnectV2(Request(), _h.Context()));
            }

            Assert.Equal(SharedWorkerRecoveryFailure.Code, ex.Code);
            Assert.Equal(identity.KbKey, (string)ex.Error["recordKey"]!);
            Assert.Equal(StaleRecoveryRegistryNames.SharedWorkerRegistry, (string)ex.Error["registry"]!);
            Assert.Equal(SharedWorkerRecoveryFailure.RemoveFailed, (string)ex.Error["failureReason"]!);
            Assert.Equal(content, File.ReadAllText(path));
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Connects);
            Assert.Equal(_h.Election.Requested.Count, _h.Election.Released);
            Assert.DoesNotContain(_h.Log, line => line.Contains("stale-ownership-recovery"));
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static SharedWorkerIdentity Identity(string kbPath = KbPath) =>
            SharedWorkerIdentity.Create(WorkerPath, kbPath, InstallationPath, Driver, Major, ProfilePath);

        private static SharedWorkerLaunchRequest Request(string kbPath = KbPath, int? attachReadyTimeoutSeconds = null)
        {
            var section = attachReadyTimeoutSeconds.HasValue
                ? new JObject { ["AttachReadyTimeoutSeconds"] = attachReadyTimeoutSeconds.Value }
                : null;
            var settings = SharedHostSettingsResolver.Resolve(section, strictDocument: false, "stdio-isolated", _ => null);
            Assert.True(settings.IsValid, string.Join("; ", settings.Errors));
            return new SharedWorkerLaunchRequest
            {
                Config = new Configuration { EffectiveSharedHost = settings },
                Kb = new KbHandle("kbteste", kbPath),
                WorkerPath = WorkerPath,
                InstallationPath = InstallationPath,
                Driver = Driver,
                Major = Major,
                ProfileConfigPath = ProfilePath,
                LegacyProvider = null,
                LaunchTimeoutMs = 30000
            };
        }

        private static SharedHostRecord Record(SharedWorkerIdentity identity, int hostPid, string state) =>
            new SharedHostRecord
            {
                KbKey = identity.KbKey,
                Identity = identity.ToRecordIdentity(),
                HostPid = hostPid,
                HostStartTimeUtcTicks = AlignedStartTicks,
                LaunchedByGatewayPid = 3800,
                WorkerPid = state == SharedHostRecordState.Ready ? LiveWorkerPid : 0,
                WorkerStartTimeUtcTicks = state == SharedHostRecordState.Ready ? AlignedStartTicks : 0,
                PipeName = SharedWorkerRegistry.PipeNameV2(identity),
                Generation = 1,
                State = state,
                UpdatedUtc = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc)
            };

        private SharedHostRecord SeedLiveHost(SharedWorkerIdentity identity, string state)
        {
            var record = Record(identity, HostPid, state);
            SharedWorkerRegistry.WriteRecordV2(record, _v2Root);
            _h.Probe.Set(HostPid, ProcessProbeResult.Running(AlignedStartTicks));
            return record;
        }

        // What the broker would do after the fake starter "launched" it: publish a live v2 record.
        private void PublishNewHost(SharedWorkerHostStartRequest request, string state)
        {
            var record = Record(request.Identity, NewHostPid, state);
            record.HostStartTimeUtcTicks = AlignedStartTicks + 50_000_000L;
            SharedWorkerRegistry.WriteRecordV2(record, _v2Root);
            _h.Probe.Set(NewHostPid, ProcessProbeResult.Running(record.HostStartTimeUtcTicks));
        }

        private string RecordPath(SharedWorkerIdentity identity) =>
            SharedWorkerRegistry.GetRecordPathV2(identity.KbKey, _v2Root);

        private void WriteOwnership(string key, int gatewayPid, long gatewayStart, int workerPid, long workerStart)
        {
            Directory.CreateDirectory(_ownershipRoot);
            File.WriteAllText(Path.Combine(_ownershipRoot, key + ".json"), JsonConvert.SerializeObject(new WorkerOwnershipRecord
            {
                Key = key,
                GatewayPid = gatewayPid,
                GatewayStartTimeUtcTicks = gatewayStart,
                WorkerPid = workerPid,
                WorkerStartTimeUtcTicks = workerStart,
                WorkerExecutable = WorkerPath,
                KbPath = KbPath
            }));
        }

        private string[] Snapshot() =>
            Directory.Exists(_root)
                ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => f + "=" + File.GetLastWriteTimeUtc(f).Ticks + ":" + File.ReadAllText(f))
                    .ToArray()
                : Array.Empty<string>();

        private static JObject AssertCatalogError(SharedWorkerLaunchException ex, string code, string? reason, bool retryable)
        {
            Assert.Equal(code, ex.Code);
            Assert.Equal(reason, ex.Reason);
            var error = ex.Error;
            Assert.NotNull(error);
            Assert.Equal(code, (string)error["code"]!);
            Assert.Equal(reason, (string?)error["reason"]);
            Assert.Equal(retryable, (bool)error["retryable"]!);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string)error["dispatchState"]!);
            Assert.False(string.IsNullOrWhiteSpace((string?)error["hint"]));
            return error;
        }

        // ---------------------------------------------------------------
        // Fakes
        // ---------------------------------------------------------------

        private sealed class FakeClock : ISharedWorkerLauncherClock
        {
            internal static readonly DateTime Origin = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
            private static readonly TimeSpan Ceiling = TimeSpan.FromHours(1);
            private readonly List<(TimeSpan At, Action Action)> _scheduled = new List<(TimeSpan, Action)>();

            public DateTime UtcNow { get; private set; } = Origin;

            internal TimeSpan Elapsed => UtcNow - Origin;

            internal void At(TimeSpan offset, Action action) => _scheduled.Add((offset, action));

            public void Sleep(TimeSpan duration) => Advance(duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1));

            internal void Advance(TimeSpan duration)
            {
                UtcNow += duration;
                // A launcher that never stops waiting fails here instead of hanging the test run.
                if (Elapsed > Ceiling) throw new InvalidOperationException("fake clock passed one hour; the launcher does not stop waiting");
                foreach (var due in _scheduled.Where(s => s.At <= Elapsed).OrderBy(s => s.At).ToList())
                {
                    _scheduled.Remove(due);
                    due.Action();
                }
            }
        }

        private sealed class FakeProbe : IProcessProbe
        {
            private readonly Dictionary<int, ProcessProbeResult> _results = new Dictionary<int, ProcessProbeResult>();

            internal void Set(int pid, ProcessProbeResult result) => _results[pid] = result;

            internal void Exit(int pid) => _results.Remove(pid);

            public ProcessProbeResult Probe(int pid) =>
                _results.TryGetValue(pid, out var result) ? result : ProcessProbeResult.NotRunning;
        }

        private sealed class FakeAttachment : ISharedWorkerAttachment
        {
            public SharedWorkerAttachInfo? AttachInfo { get; set; }

            public bool IsSdkReady { get; set; }

            internal bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }

        private sealed class ConnectCall
        {
            internal SharedWorkerIdentity Identity { get; init; } = null!;
            internal SharedHostRecord Record { get; init; } = null!;
            internal FakeAttachment Attachment { get; init; } = null!;
        }

        private sealed class FakeElection
        {
            private int _held;

            internal List<(string Name, DateTime At)> Requested { get; } = new List<(string, DateTime)>();

            internal int Released { get; private set; }

            internal bool ReleasedBeforeConnect { get; private set; }

            internal bool IsHeld => _held > 0;

            internal IDisposable? Acquire(string name, DateTime at)
            {
                Requested.Add((name, at));
                _held++;
                return new Releaser(this);
            }

            internal void OnConnect() => ReleasedBeforeConnect = !IsHeld && Released > 0;

            private sealed class Releaser : IDisposable
            {
                private readonly FakeElection _owner;
                private bool _disposed;

                internal Releaser(FakeElection owner) => _owner = owner;

                public void Dispose()
                {
                    if (_disposed) return;
                    _disposed = true;
                    _owner._held--;
                    _owner.Released++;
                }
            }
        }

        private sealed class Harness
        {
            private readonly string _v2Root;
            private readonly string _ownershipRoot;

            internal Harness(string v2Root, string ownershipRoot)
            {
                _v2Root = v2Root;
                _ownershipRoot = ownershipRoot;
            }

            internal FakeClock Clock { get; } = new FakeClock();
            internal FakeProbe Probe { get; } = new FakeProbe();
            internal FakeElection Election { get; } = new FakeElection();
            internal Dictionary<int, string> Images { get; } = new Dictionary<int, string>();
            internal List<int> Terminated { get; } = new List<int>();
            internal List<string> Log { get; } = new List<string>();
            internal List<SharedWorkerHostStartRequest> Starts { get; } = new List<SharedWorkerHostStartRequest>();
            internal List<ConnectCall> Connects { get; } = new List<ConnectCall>();
            internal int ConnectAttempts { get; private set; }
            internal bool ExitOnTerminate { get; set; } = true;
            internal bool AttachmentReady { get; set; } = true;
            internal TimeSpan ConnectCost { get; set; } = TimeSpan.Zero;
            internal Func<Exception>? ConnectFailure { get; set; }
            internal Action<SharedWorkerHostStartRequest>? OnStart { get; set; }

            internal SharedWorkerRecoveryContext Context(TimeSpan? stopWaitTimeout = null) =>
                new SharedWorkerRecoveryContext
                {
                    Probe = Probe,
                    ReadImagePath = (int pid, out string? path, out string? error) =>
                    {
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
                        Terminated.Add(pid);
                        if (ExitOnTerminate) Probe.Exit(pid);
                        return null;
                    },
                    Log = Log.Add,
                    RecoveringGatewayPid = RecoveringGatewayPid,
                    GatewayPids = Array.Empty<int>(),
                    StopWaitTimeout = stopWaitTimeout ?? TimeSpan.FromSeconds(10),
                    StopPollInterval = TimeSpan.FromMilliseconds(100),
                    RegistryRoot = _v2Root,
                    OwnershipRegistryRoot = _ownershipRoot,
                    Clock = Clock,
                    RegistryPollInterval = TimeSpan.FromMilliseconds(100),
                    AcquireElectionMutex = (name, timeout) => Election.Acquire(name, Clock.UtcNow),
                    // The admission pre-check (task 5.7) reads memory before StartHost; a fixed reading keeps these
                    // tests independent of the machine's free memory.
                    ReadAvailableMemory = (out long availableMb, out string? error) =>
                    {
                        availableMb = 1L << 20;
                        error = null;
                        return true;
                    },
                    StartHost = request =>
                    {
                        Starts.Add(request);
                        OnStart?.Invoke(request);
                    },
                    ConnectPipe = (identity, record, clientId, timeoutMs) =>
                    {
                        ConnectAttempts++;
                        Election.OnConnect();
                        if (ConnectFailure != null) throw ConnectFailure();
                        Clock.Advance(ConnectCost);
                        var attachment = new FakeAttachment
                        {
                            IsSdkReady = AttachmentReady,
                            AttachInfo = new SharedWorkerAttachInfo
                            {
                                AttachId = "attach-" + ConnectAttempts,
                                HostPid = record.HostPid,
                                HostStartTimeUtcTicks = record.HostStartTimeUtcTicks,
                                WorkerPid = record.WorkerPid,
                                WorkerStartTimeUtcTicks = record.WorkerStartTimeUtcTicks,
                                Generation = record.Generation,
                                SdkReady = AttachmentReady
                            }
                        };
                        Connects.Add(new ConnectCall { Identity = identity, Record = record, Attachment = attachment });
                        return attachment;
                    }
                };
        }
    }
}
