using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>Time source of the v2 Connect sequence; tests replace it with a fake clock.</summary>
    internal interface ISharedWorkerLauncherClock
    {
        DateTime UtcNow { get; }

        void Sleep(TimeSpan duration);
    }

    internal sealed class SharedWorkerSystemClock : ISharedWorkerLauncherClock
    {
        internal static SharedWorkerSystemClock Instance { get; } = new SharedWorkerSystemClock();

        private SharedWorkerSystemClock()
        {
        }

        public DateTime UtcNow => DateTime.UtcNow;

        public void Sleep(TimeSpan duration)
        {
            if (duration > TimeSpan.Zero) Thread.Sleep(duration);
        }
    }

    /// <summary>One attachment to a shared Worker host, as the launcher sees it.</summary>
    internal interface ISharedWorkerAttachment : IDisposable
    {
        SharedWorkerAttachInfo? AttachInfo { get; }

        // Latest "ready" flag from attach_ack or heartbeat_ack.
        bool IsSdkReady { get; }
    }

    /// <summary>
    /// Opens the attach pipe named by <paramref name="record"/>. Throws IOException, TimeoutException or
    /// InvalidOperationException when the pipe refuses or drops the connection or the attach handshake fails.
    /// </summary>
    internal delegate ISharedWorkerAttachment SharedWorkerPipeConnector(
        SharedWorkerIdentity identity, SharedHostRecord record, string clientId, int timeoutMs);

    /// <summary>Starts the broker process. Throws InvalidOperationException when it cannot be started.</summary>
    internal delegate void SharedWorkerHostStarter(SharedWorkerHostStartRequest request);

    /// <summary>Returns the held election mutex, or null when it was not acquired within timeout.</summary>
    internal delegate IDisposable? SharedWorkerElectionMutexFactory(string mutexName, TimeSpan timeout);

    internal sealed class SharedWorkerHostStartRequest
    {
        public SharedWorkerIdentity Identity { get; init; } = new SharedWorkerIdentity();
        public string KbKey { get; init; } = string.Empty;
        public KbHandle Kb { get; init; } = null!;
        public string WorkerPath { get; init; } = string.Empty;
        public string InstallationPath { get; init; } = string.Empty;
        public string Driver { get; init; } = string.Empty;
        public string Major { get; init; } = string.Empty;
        public string? LegacyProvider { get; init; }

        // SharedWorkerRegistry.PipeNameV2(Identity).
        public string PipeName { get; init; } = string.Empty;

        // SharedWorkerHostLauncher.BuildHostArguments(...); the production starter appends --registry-dir.
        public string Arguments { get; init; } = string.Empty;
        public string? RegistryRoot { get; init; }

        // The launching Gateway's configuration, for the environment the broker inherits.
        public Configuration? Config { get; init; }
    }

    internal sealed class SharedWorkerLaunchRequest
    {
        public Configuration Config { get; init; } = new Configuration();
        public KbHandle Kb { get; init; } = null!;
        public string WorkerPath { get; init; } = string.Empty;
        public string InstallationPath { get; init; } = string.Empty;
        public string Driver { get; init; } = string.Empty;
        public string Major { get; init; } = string.Empty;
        public string? ProfileConfigPath { get; init; }
        public string? LegacyProvider { get; init; }
        public int LaunchTimeoutMs { get; init; } = 30000;
    }

    /// <summary>A typed refusal of the v2 Connect sequence. <see cref="Error"/> is the typed error object.</summary>
    internal sealed class SharedWorkerLaunchException : InvalidOperationException
    {
        internal SharedWorkerLaunchException(string code, string? reason, JObject error, Exception? inner = null)
            : base(BuildMessage(code, reason, error), inner)
        {
            Code = code;
            Reason = reason;
            Error = error;
        }

        internal string Code { get; }

        internal string? Reason { get; }

        internal JObject Error { get; }

        private static string BuildMessage(string code, string? reason, JObject error)
        {
            string message = (string?)error?["message"] ?? "Shared Worker launch failed.";
            return code + (reason != null ? " (" + reason + ")" : string.Empty) + ": " + message;
        }
    }

    /// <summary>
    /// SHARED_WORKER_RECOVERY_FAILED (Requirement 7 criterion 9). It has no Concurrency_Error_Table row, so it is
    /// built here with the same core fields the catalog writes.
    /// </summary>
    internal static class SharedWorkerRecoveryFailure
    {
        internal const string Code = "SHARED_WORKER_RECOVERY_FAILED";
        internal const string RemoveFailed = "remove-failed";
        internal const string KillFailed = "kill-failed";
        internal const string StillRunning = "still-running";

        internal static JObject Build(string recordKey, string registry, string failureReason, int? blockingPid, string? detail)
        {
            var error = new JObject
            {
                ["code"] = Code,
                ["message"] = "Stale ownership recovery could not complete; the remaining ownership records were left unchanged.",
                ["hint"] = blockingPid.HasValue
                    ? "Ask the user to stop the process with PID " + blockingPid.Value.ToString(CultureInfo.InvariantCulture)
                        + " or remove the stale record, then open the KB again."
                    : "Ask the user to remove the stale record, then open the KB again.",
                ["retryable"] = false,
                ["dispatchState"] = ConcurrencyDispatchState.NotDelivered,
                ["recordKey"] = recordKey,
                ["registry"] = registry,
                ["failureReason"] = failureReason
            };
            if (blockingPid.HasValue) error["blockingPid"] = blockingPid.Value;
            if (!string.IsNullOrWhiteSpace(detail)) error["detail"] = detail;
            return error;
        }

        internal static SharedWorkerLaunchException Exception(
            string recordKey, string registry, string failureReason, int? blockingPid, string? detail) =>
            new SharedWorkerLaunchException(Code, null, Build(recordKey, registry, failureReason, blockingPid, detail));
    }

    /// <summary>Discovers or elects the one broker for a KB directory (registry v2, decisions A3 and A4).</summary>
    internal static class SharedWorkerHostLauncher
    {
        private static readonly TimeSpan ElectionWait = TimeSpan.FromMilliseconds(250);
        private const int MaxRecordChangedRetries = 3;
        internal static SharedWorkerRecoveryContext? RecoveryContextForTest;

        internal static SharedWorkerConnection Connect(
            Configuration config,
            KbHandle kb,
            string workerPath,
            string installationPath,
            string driver,
            string major,
            string? legacyProvider = null,
            int timeoutMs = 30000)
        {
            var request = new SharedWorkerLaunchRequest
            {
                Config = config,
                Kb = kb,
                WorkerPath = workerPath,
                InstallationPath = installationPath,
                Driver = driver,
                Major = major,
                ProfileConfigPath = Configuration.CurrentConfigPath,
                LegacyProvider = legacyProvider,
                LaunchTimeoutMs = timeoutMs
            };
            ISharedWorkerAttachment attachment = ConnectV2(request, RecoveryContextForTest ?? new SharedWorkerRecoveryContext());
            if (attachment is SharedWorkerConnection connection) return connection;
            attachment.Dispose();
            throw new InvalidOperationException("The shared Worker pipe connector returned an unsupported attachment type.");
        }

        /// <summary>
        /// The v2 Connect sequence (design "SharedWorkerHostLauncher.cs (v2)"): classify the KB directory's record,
        /// attach to a live host, refuse an unverifiable or Isolated_Mode owner, recover stale ownership, elect and
        /// start one host, attach, and wait for readiness. No step starts a second host while the recorded host is
        /// live.
        /// </summary>
        internal static ISharedWorkerAttachment ConnectV2(SharedWorkerLaunchRequest request, SharedWorkerRecoveryContext context)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (request.Kb == null) throw new ArgumentException("A KB is required.", nameof(request));

            var identity = SharedWorkerIdentity.Create(
                request.WorkerPath, request.Kb.Path, request.InstallationPath, request.Driver, request.Major,
                request.ProfileConfigPath);
            string kbKey = identity.KbKey;
            if (!SharedWorkerRegistry.IsValidKbKey(kbKey))
                throw new ArgumentException("The KB path is required for a shared Worker host.", nameof(request));

            var clock = context.Clock;
            string clientId = "gateway-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            DateTime deadline = clock.UtcNow.AddMilliseconds(Math.Max(1000, request.LaunchTimeoutMs));
            bool ownershipScanned = false;

            while (true)
            {
                var classification = ReadAndClassify(kbKey, context, out RecordReadResult read);
                if (classification.Liveness == RecordLiveness.Live)
                    return AttachToLive(identity, classification.Record!, request, context, clientId);
                if (classification.Liveness == RecordLiveness.LivenessUnverified)
                    throw LivenessUnverified(request, classification.Record?.HostPid);

                if (!ownershipScanned)
                {
                    RecoverIsolatedOwnership(request, context);
                    ownershipScanned = true;
                }

                IDisposable? election = context.AcquireElectionMutex(SharedWorkerRegistry.ElectionMutexNameV2(kbKey), ElectionWait);
                if (election == null)
                {
                    // Another Gateway is electing: its host becomes live, or the election mutex is released.
                    if (clock.UtcNow >= deadline) throw HostUnavailable(request, "the KB election mutex stayed held");
                    clock.Sleep(context.RegistryPollInterval);
                    continue;
                }

                SharedHostRecord? live;
                try
                {
                    live = ElectAndStart(identity, kbKey, request, context, deadline);
                }
                finally
                {
                    election.Dispose();
                }

                if (live == null) continue; // The record changed under the election; classify it again.
                return AttachToLive(identity, live, request, context, clientId);
            }
        }

        // Steps 7 and 8 under the election mutex: stale recovery, host start, wait for a live record. Returns the
        // live record, or null when the record kept changing and the caller must classify it again.
        private static SharedHostRecord? ElectAndStart(
            SharedWorkerIdentity identity,
            string kbKey,
            SharedWorkerLaunchRequest request,
            SharedWorkerRecoveryContext context,
            DateTime deadline)
        {
            for (int attempt = 0; ; attempt++)
            {
                var classification = ReadAndClassify(kbKey, context, out RecordReadResult read);
                if (classification.Liveness == RecordLiveness.Live) return classification.Record;
                if (classification.Liveness == RecordLiveness.LivenessUnverified)
                    throw LivenessUnverified(request, classification.Record?.HostPid);
                if (read.Status == RecordReadStatus.Absent) break;

                var removal = SharedWorkerRegistry.RemoveStaleRecord(kbKey, classification.Record, classification.Reason, context);
                if (removal.Outcome == StaleRecordRemovalOutcome.RemoveFailed)
                {
                    throw SharedWorkerRecoveryFailure.Exception(
                        kbKey, StaleRecoveryRegistryNames.SharedWorkerRegistry, SharedWorkerRecoveryFailure.RemoveFailed,
                        null, removal.Error);
                }
                if (removal.Outcome != StaleRecordRemovalOutcome.RecordChanged) break;
                if (attempt + 1 >= MaxRecordChangedRetries) return null;
            }

            RunAdmissionPreCheck(kbKey, request, context);

            var settings = request.Config.EffectiveSharedHost;
            context.StartHost(new SharedWorkerHostStartRequest
            {
                Identity = identity,
                KbKey = kbKey,
                Kb = request.Kb,
                WorkerPath = request.WorkerPath,
                InstallationPath = request.InstallationPath,
                Driver = request.Driver,
                Major = request.Major,
                LegacyProvider = request.LegacyProvider,
                PipeName = SharedWorkerRegistry.PipeNameV2(identity),
                Arguments = BuildHostArguments(request.Config, identity, request.WorkerPath, request.Kb.Path,
                    request.InstallationPath, request.Driver, request.Major, request.ProfileConfigPath),
                RegistryRoot = context.RegistryRoot,
                Config = request.Config
            });

            var clock = context.Clock;
            while (true)
            {
                var classification = ReadAndClassify(kbKey, context, out _);
                if (classification.Liveness == RecordLiveness.Live) return classification.Record;
                if (clock.UtcNow >= deadline) throw HostUnavailable(request, "the started host published no live record");
                clock.Sleep(context.RegistryPollInterval);
            }
        }

        // Decision A2: the advisory pre-check under the election mutex, before a broker process is spent on a start
        // the host would refuse. Only Refuse acts here; Start, Hold and Evict launch the host, which re-decides
        // authoritatively under the machine-wide admission mutex. Server.MaxOpenKbs belongs to the WorkerPool.
        private static void RunAdmissionPreCheck(string kbKey, SharedWorkerLaunchRequest request, SharedWorkerRecoveryContext context)
        {
            var settings = request.Config.EffectiveSharedHost;
            var hosts = ReadAdmissionHosts(context);

            // Memory is read last, after every enumeration and probe.
            long? availableMb = null;
            if (context.ReadAvailableMemory(out long measuredMb, out string? memoryError))
                availableMb = measuredMb;
            else
                context.Log("[Gateway] shared_host_precheck memory_unreadable kbKey=" + kbKey + " error=" + (memoryError ?? "unknown"));

            var decision = SharedHostAdmissionPolicy.Decide(new AdmissionInput
            {
                SelfKey = kbKey,
                Hosts = hosts,
                MaxWorkers = settings.MaxWorkers,
                MaxOpenKbsExceeded = false,
                ColdStartConcurrency = settings.ColdStartConcurrency,
                MemoryFloorMb = settings.MemoryFloorMb,
                AvailableMb = availableMb
            });
            if (decision.Kind != AdmissionDecisionKind.Refuse) return;

            var details = (JObject?)decision.Details?.DeepClone() ?? new JObject();
            if (string.Equals(decision.Reason, AdmissionDecision.WorkerLimitReason, StringComparison.Ordinal))
            {
                details["limit"] = "Server.SharedHost." + SharedHostSettingsResolver.MaxWorkersKey;
                details["value"] = settings.MaxWorkers;
            }
            else if (string.Equals(decision.Reason, AdmissionDecision.LowMemoryReason, StringComparison.Ordinal))
            {
                details["limit"] = "Server.SharedHost." + SharedHostSettingsResolver.MemoryFloorMbKey;
                details["value"] = settings.MemoryFloorMb;
            }
            throw Catalog(request, decision.Code ?? ConcurrencyErrorCatalog.KbPoolFull, decision.Reason, null, details, null);
        }

        // Every registry v2 record of the registry root, classified with the context probe. Absent, unparseable and
        // unreadable files carry no record and never take a slot, so they are skipped.
        private static List<AdmissionHostView> ReadAdmissionHosts(SharedWorkerRecoveryContext context)
        {
            var hosts = new List<AdmissionHostView>();
            string root = SharedWorkerRegistry.GetV2Root(context.RegistryRoot);
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(root)) return hosts;
                files = Directory.GetFiles(root, "*.json");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The host check is authoritative; an unreadable registry leaves the pre-check without records.
                context.Log("[Gateway] shared_host_precheck registry_unreadable error=" + ex.GetType().Name + ": " + ex.Message);
                return hosts;
            }

            foreach (string file in files)
            {
                // "*.json" can also match longer extensions on Windows; temp files end in ".tmp-<pid>-<guid>".
                if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase)) continue;
                var classification = SharedHostRecordClassifier.Classify(RecordReadResult.ReadFile(file), context.Probe);
                var record = classification.Record;
                if (record == null) continue;
                hosts.Add(new AdmissionHostView
                {
                    Key = record.KbKey,
                    State = record.State,
                    WorkerPid = record.WorkerPid,
                    Evictable = record.Evictable,
                    Attachments = record.Attachments,
                    LastActivityUtc = record.LastActivityUtc,
                    HeldSinceUtc = record.HeldSinceUtc,
                    Liveness = classification.Liveness
                });
            }
            return hosts;
        }

        // A live host in state "refused" holds an admission refusal: return it instead of attaching. The record and
        // every process stay unchanged. A refusal the catalog does not know is not reported here; the attach then
        // proceeds as before.
        private static void ThrowOnRefusedRecord(SharedHostRecord record, SharedWorkerLaunchRequest request)
        {
            if (!string.Equals(record.State, SharedHostRecordState.Refused, StringComparison.Ordinal)) return;
            var refusal = record.Refusal;
            if (refusal == null || string.IsNullOrEmpty(refusal.Code)) return;

            var details = new JObject();
            if (refusal.AvailableMb.HasValue) details["availableMb"] = refusal.AvailableMb.Value;
            if (refusal.FloorMb.HasValue) details["floorMb"] = refusal.FloorMb.Value;
            string? reason = string.IsNullOrEmpty(refusal.Reason) ? null : refusal.Reason;
            JObject error;
            try
            {
                error = ConcurrencyErrorCatalog.Build(refusal.Code, reason, new ConcurrencyErrorFields
                {
                    QueueWaitMs = request.Config.EffectiveSharedHost.QueueWaitMs,
                    Details = details
                });
            }
            catch (ArgumentException)
            {
                return;
            }
            throw new SharedWorkerLaunchException(refusal.Code, reason, error);
        }

        private static RecordClassification ReadAndClassify(string kbKey, SharedWorkerRecoveryContext context, out RecordReadResult read)
        {
            read = SharedWorkerRegistry.ReadRecordV2(kbKey, context.RegistryRoot);
            return SharedHostRecordClassifier.Classify(read, context.Probe);
        }

        // Step 3 and steps 9-10: identity check, attach, readiness wait.
        private static ISharedWorkerAttachment AttachToLive(
            SharedWorkerIdentity identity,
            SharedHostRecord record,
            SharedWorkerLaunchRequest request,
            SharedWorkerRecoveryContext context,
            string clientId)
        {
            ThrowOnIdentityMismatch(identity, record, request);
            ThrowOnRefusedRecord(record, request);

            ISharedWorkerAttachment attachment;
            try
            {
                attachment = context.ConnectPipe(identity, record, clientId, Math.Max(1000, Math.Min(request.LaunchTimeoutMs, 30000)));
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException
                || (ex is InvalidOperationException && !(ex is SharedWorkerLaunchException)))
            {
                throw AttachFailed(request, "pipe-refused", ex);
            }

            var clock = context.Clock;
            int waitSeconds = request.Config.EffectiveSharedHost.AttachReadyTimeoutSeconds;
            DateTime readyDeadline = clock.UtcNow.AddSeconds(waitSeconds);
            try
            {
                while (true)
                {
                    if (attachment.IsSdkReady) return attachment;
                    if (IsRecordReady(record, request, context))
                    {
                        (attachment as SharedWorkerConnection)?.MarkSdkReady();
                        return attachment;
                    }
                    if (clock.UtcNow >= readyDeadline) break;
                    clock.Sleep(context.RegistryPollInterval);
                }
            }
            catch
            {
                attachment.Dispose();
                throw;
            }

            attachment.Dispose();
            throw AttachFailed(request, "readiness-timeout", null);
        }

        // The re-read record of the same host (same PID and start time) reports state "ready".
        private static bool IsRecordReady(SharedHostRecord attached, SharedWorkerLaunchRequest request, SharedWorkerRecoveryContext context)
        {
            var read = SharedWorkerRegistry.ReadRecordV2(attached.KbKey, context.RegistryRoot);
            var current = read.Record;
            return read.Status == RecordReadStatus.Parsed
                && current != null
                && current.HostPid == attached.HostPid
                && current.HostStartTimeUtcTicks == attached.HostStartTimeUtcTicks
                && string.Equals(current.State, SharedHostRecordState.Ready, StringComparison.Ordinal);
        }

        private static void ThrowOnIdentityMismatch(SharedWorkerIdentity identity, SharedHostRecord record, SharedWorkerLaunchRequest request)
        {
            IReadOnlyList<SharedWorkerIdentityDifference> differences;
            try
            {
                differences = SharedWorkerIdentity.BuildMismatch(identity, record.Identity ?? new SharedHostRecordIdentity());
            }
            catch (ArgumentException)
            {
                // A record of this kbKey for another KB directory: never attach to it.
                differences = new[]
                {
                    new SharedWorkerIdentityDifference
                    {
                        Component = "kbPath",
                        Requested = identity.KbPath,
                        Live = SharedWorkerIdentity.NormalizePath(record.Identity?.KbPath)
                    }
                };
            }
            if (differences.Count == 0) return;

            var list = new JArray();
            foreach (var difference in differences)
            {
                list.Add(new JObject
                {
                    ["component"] = difference.Component,
                    ["requested"] = difference.Requested,
                    ["live"] = difference.Live
                });
            }
            var details = new JObject { ["differences"] = list, ["hostPid"] = record.HostPid };
            throw Catalog(request, ConcurrencyErrorCatalog.SharedWorkerIdentityMismatch, null, null, details, null);
        }

        // Step 5: Isolated_Mode ownership records of the same KB directory. A live owner refuses the attach; an
        // orphaned Worker of a dead owner is stopped and its record removed before the election.
        private static void RecoverIsolatedOwnership(SharedWorkerLaunchRequest request, SharedWorkerRecoveryContext context)
        {
            string root = context.OwnershipRegistryRoot ?? WorkerOwnershipRegistry.DefaultRoot;
            var records = WorkerOwnershipRegistry.ScanByKbPath(request.Kb.Path, root);
            if (records.Count == 0) return;

            // Every refusal is decided before any record or process changes.
            foreach (var record in records)
            {
                switch (ProbeOwner(record, context))
                {
                    case OwnerState.Live:
                        throw OwnerDoesNotAcceptAttachments(request, record);
                    case OwnerState.Unverified:
                        throw LivenessUnverified(request, record.GatewayPid);
                }

                if (record.WorkerPid > 0
                    && context.Probe.Probe(record.WorkerPid).Status == ProcessProbeStatus.RunningStartTimeUnreadable)
                    throw LivenessUnverified(request, record.GatewayPid);
            }

            foreach (var record in records) RecoverOrphan(record, root, context);
        }

        private static void RecoverOrphan(WorkerOwnershipRecord record, string root, SharedWorkerRecoveryContext context)
        {
            string registry = StaleRecoveryRegistryNames.WorkerOwnershipRegistry;
            string key = record.Key ?? string.Empty;
            bool stopped = false;

            var workerProbe = record.WorkerPid > 0 ? context.Probe.Probe(record.WorkerPid) : ProcessProbeResult.NotRunning;
            bool sameWorker = workerProbe.Status == ProcessProbeStatus.Running
                && record.WorkerStartTimeUtcTicks > 0
                && SharedHostRecordClassifier.SameMillisecond(record.WorkerStartTimeUtcTicks, workerProbe.StartTimeUtcTicks);
            if (sameWorker)
            {
                var stop = SharedWorkerRegistry.StopOrphanExact(
                    record.WorkerPid, record.WorkerStartTimeUtcTicks, record.WorkerExecutable, context);
                switch (stop.Outcome)
                {
                    case OrphanStopOutcome.Stopped:
                        stopped = true;
                        break;
                    case OrphanStopOutcome.AlreadyExited:
                        break;
                    case OrphanStopOutcome.StillRunning:
                        throw SharedWorkerRecoveryFailure.Exception(key, registry, SharedWorkerRecoveryFailure.StillRunning,
                            record.WorkerPid, stop.Error);
                    default:
                        throw SharedWorkerRecoveryFailure.Exception(key, registry, SharedWorkerRecoveryFailure.KillFailed,
                            record.WorkerPid, stop.Outcome + (stop.Error != null ? ": " + stop.Error : string.Empty));
                }
            }

            if (stopped)
            {
                context.Log(SharedWorkerRegistry.FormatRecoveryLogEntry(
                    SharedWorkerRegistry.RecoveryActionStoppedProcess, registry, key,
                    record.WorkerPid, record.WorkerStartTimeUtcTicks, context.RecoveringGatewayPid, StaleReason.OrphanedWorker));
            }

            if (!IsSafeRecordKey(key))
                throw SharedWorkerRecoveryFailure.Exception(key, registry, SharedWorkerRecoveryFailure.RemoveFailed, null,
                    "the ownership record key is not a file name");
            string path = Path.Combine(root, key + ".json");
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw SharedWorkerRecoveryFailure.Exception(key, registry, SharedWorkerRecoveryFailure.RemoveFailed, null,
                    ex.GetType().Name + ": " + ex.Message);
            }

            context.Log(SharedWorkerRegistry.FormatRecoveryLogEntry(
                SharedWorkerRegistry.RecoveryActionRemovedRecord, registry, key,
                record.WorkerPid, record.WorkerStartTimeUtcTicks, context.RecoveringGatewayPid, StaleReason.OrphanedWorker));
        }

        private enum OwnerState
        {
            NotLive,
            Live,
            Unverified
        }

        // Requirement 7 criterion 1: an owner is live when its PID runs with the recorded start time. A running
        // owner without a recorded start time counts as live (an older record), an unreadable start time as
        // unverified; both fail closed.
        private static OwnerState ProbeOwner(WorkerOwnershipRecord record, SharedWorkerRecoveryContext context)
        {
            if (record.GatewayPid <= 0) return OwnerState.NotLive;
            var probe = context.Probe.Probe(record.GatewayPid);
            switch (probe.Status)
            {
                case ProcessProbeStatus.NotRunning:
                    return OwnerState.NotLive;
                case ProcessProbeStatus.Running:
                    if (record.GatewayStartTimeUtcTicks <= 0) return OwnerState.Live;
                    return SharedHostRecordClassifier.SameMillisecond(record.GatewayStartTimeUtcTicks, probe.StartTimeUtcTicks)
                        ? OwnerState.Live
                        : OwnerState.NotLive;
                default:
                    return OwnerState.Unverified;
            }
        }

        private static bool IsSafeRecordKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains("..", StringComparison.Ordinal)) return false;
            return key.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        // ---- Typed errors ----

        private static SharedWorkerLaunchException LivenessUnverified(SharedWorkerLaunchRequest request, int? holderPid)
        {
            int? pid = holderPid > 0 ? holderPid : null;
            JObject? details = pid.HasValue ? new JObject { ["holderPid"] = pid.Value } : null;
            return Catalog(request, ConcurrencyErrorCatalog.KbWorkerOwnedElsewhere, "liveness-unverified", pid, details, null);
        }

        private static SharedWorkerLaunchException OwnerDoesNotAcceptAttachments(SharedWorkerLaunchRequest request, WorkerOwnershipRecord owner)
        {
            var details = new JObject
            {
                ["holderPid"] = owner.GatewayPid,
                ["holderStartTimeUtcTicks"] = owner.GatewayStartTimeUtcTicks,
                ["releaseActions"] = new JArray
                {
                    new JObject { ["action"] = "close-holder-session", ["pid"] = owner.GatewayPid },
                    new JObject
                    {
                        ["action"] = "set-worker-sharing-mode",
                        ["setting"] = "Server.WorkerSharingMode",
                        ["value"] = "shared-host"
                    }
                }
            };
            return Catalog(request, ConcurrencyErrorCatalog.KbWorkerOwnedElsewhere, "owner-does-not-accept-attachments",
                owner.GatewayPid, details, null);
        }

        private static SharedWorkerLaunchException AttachFailed(SharedWorkerLaunchRequest request, string reason, Exception? inner) =>
            Catalog(request, ConcurrencyErrorCatalog.SharedWorkerAttachFailed, reason, null, null, inner);

        // No live host within the launch timeout: reported as the attach readiness failure, so the caller retries.
        private static SharedWorkerLaunchException HostUnavailable(SharedWorkerLaunchRequest request, string detail) =>
            Catalog(request, ConcurrencyErrorCatalog.SharedWorkerAttachFailed, "readiness-timeout", null,
                new JObject { ["detail"] = detail }, null);

        private static SharedWorkerLaunchException Catalog(
            SharedWorkerLaunchRequest request, string code, string? reason, int? holderPid, JObject? details, Exception? inner)
        {
            var error = ConcurrencyErrorCatalog.Build(code, reason, new ConcurrencyErrorFields
            {
                QueueWaitMs = request.Config.EffectiveSharedHost.QueueWaitMs,
                HolderPid = holderPid,
                Details = details
            });
            return new SharedWorkerLaunchException(code, reason, error, inner);
        }

        // ---- Production seams ----

        /// <summary>Named election mutex; an abandoned mutex counts as acquired.</summary>
        internal static IDisposable? AcquireNamedMutex(string mutexName, TimeSpan timeout)
        {
            var mutex = new Mutex(false, mutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            catch
            {
                mutex.Dispose();
                throw;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }
            return new SharedWorkerRegistryReservation(mutex);
        }

        /// <summary>
        /// Attaches over the record's pipe. A host without a Worker child yet (admitting, held, refused, no-worker)
        /// acknowledges with workerPid 0 and its child state, and the attachment is kept; worker_restarted names
        /// the child later. An acknowledgement that lacks the Worker identity without such a state is retried until
        /// the child exists or the timeout ends.
        /// </summary>
        internal static ISharedWorkerAttachment ConnectPipeProcess(
            SharedWorkerIdentity identity, SharedHostRecord record, string clientId, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs));
            while (true)
            {
                var connection = new SharedWorkerConnection(identity, record, clientId);
                try
                {
                    int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    connection.Connect(remaining);
                    return connection;
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.IndexOf("missing process identity", StringComparison.Ordinal) >= 0
                    && DateTime.UtcNow.AddMilliseconds(200) < deadline)
                {
                    connection.Dispose();
                    Thread.Sleep(200);
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }
        }

        /// <summary>Starts the broker process (registry v2: --registry-dir when a registry root is set).</summary>
        internal static void StartHostProcess(SharedWorkerHostStartRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var kb = request.Kb;
            string arguments = request.Arguments;
            if (!string.IsNullOrWhiteSpace(request.RegistryRoot))
                arguments += " --registry-dir " + Quote(request.RegistryRoot!.TrimEnd('\\', '/'));

            var info = new ProcessStartInfo
            {
                FileName = request.WorkerPath,
                WorkingDirectory = Path.GetDirectoryName(request.WorkerPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Arguments = arguments
            };
            info.EnvironmentVariables["GX_PROGRAM_DIR"] = request.InstallationPath ?? string.Empty;
            info.EnvironmentVariables["GX_KB_PATH"] = kb.Path ?? string.Empty;
            info.EnvironmentVariables["GXMCP_KB_ID"] = kb.KbId ?? string.Empty;
            info.EnvironmentVariables["GXMCP_KB_GENERATION"] = kb.ContextGeneration.ToString(CultureInfo.InvariantCulture);
            info.EnvironmentVariables["GXMCP_DRIVER"] = request.Driver ?? string.Empty;
            info.EnvironmentVariables["GXMCP_TARGET_MAJOR"] = request.Major ?? string.Empty;
            info.EnvironmentVariables["GXMCP_OPERATIONAL_STATE_KEY"] = request.Identity.Key;
            // Keep the Worker's durable jobs root identical to the Gateway's
            // StateScope.JobsPath during a soft reload.
            var stateScope = StateScope.Create(id: StateScope.ProcessScopeId);
            info.EnvironmentVariables["GXMCP_JOBS_DIR"] = Path.GetDirectoryName(
                stateScope.JobsPath(kb.KbId ?? kb.Alias, kb.ContextGeneration))!;
            if (!string.IsNullOrWhiteSpace(Configuration.CurrentConfigPath))
                info.EnvironmentVariables["GXMCP_PROFILE_CONFIG_PATH"] = Configuration.CurrentConfigPath;
            if (!string.IsNullOrWhiteSpace(request.LegacyProvider))
                info.EnvironmentVariables["GXMCP_GXPUBLIC_PROVIDER"] = request.LegacyProvider;
            var config = request.Config;
            if (config?.Server != null)
            {
                if (config.Server.SourceStoreMaxMB > 0)
                    info.EnvironmentVariables["GXMCP_SOURCE_STORE_MAX_MB"] = config.Server.SourceStoreMaxMB.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(config.Server.SourceStoreBackfill))
                    info.EnvironmentVariables["GXMCP_SOURCE_STORE_BACKFILL"] = config.Server.SourceStoreBackfill;
            }
            info.EnvironmentVariables["GXMCP_SHARED_HOST"] = "1";
            try
            {
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    string key = entry.Key?.ToString() ?? string.Empty;
                    if (key.StartsWith("GXMCP_", StringComparison.OrdinalIgnoreCase) && !info.EnvironmentVariables.ContainsKey(key))
                        info.EnvironmentVariables[key] = entry.Value?.ToString() ?? string.Empty;
                }
            }
            catch { }

            Process? host = Process.Start(info);
            if (host == null) throw new InvalidOperationException("Could not start shared Worker host process.");
            // Keep stdout/stderr drained without making host startup depend on a Gateway
            // reader. Diagnostics remain in the normal gateway log only when startup fails.
            _ = host.StandardOutput.ReadToEndAsync();
            _ = host.StandardError.ReadToEndAsync();
            Program.Log("[Gateway] shared_worker_host_started pid=" + host.Id + " identity=" + request.Identity.Key + " kbKey=" + request.KbKey);
        }

        /// <summary>
        /// The broker command line. Every Server.SharedHost setting is the Gateway's resolved
        /// <see cref="Configuration.EffectiveSharedHost"/> value (the attach readiness wait in
        /// milliseconds), and --heap-recycle-mb is Server.WorkerHeapRecycleMB, passed as arguments so the
        /// broker does not depend on inherited environment variables (RFC #854). The pipe name is
        /// the registry v2 name, the only prefix the host accepts (decision A4).
        /// </summary>
        internal static string BuildHostArguments(
            Configuration config,
            SharedWorkerIdentity identity,
            string workerPath,
            string? kbPath,
            string installationPath,
            string driver,
            string major,
            string? profileConfigPath = null)
        {
            var invariant = CultureInfo.InvariantCulture;
            int idleMs = ResolveIdleTimeoutMs(config);
            var settings = config.EffectiveSharedHost;
            string arguments = "--shared-host"
                + " --identity-key " + Quote(identity.Key)
                + " --pipe-name " + Quote(SharedWorkerRegistry.PipeNameV2(identity))
                + " --worker-executable " + Quote(workerPath)
                + " --kb " + Quote(kbPath ?? string.Empty)
                + " --installation " + Quote(installationPath)
                + " --driver " + Quote(driver)
                + " --major " + Quote(major)
                + " --idle-ms " + idleMs.ToString(invariant)
                + " --queue-wait-ms " + settings.QueueWaitMs.ToString(invariant)
                + " --queue-depth " + settings.QueueDepth.ToString(invariant)
                + " --session-queue-cap " + settings.SessionQueueCap.ToString(invariant)
                + " --max-workers " + settings.MaxWorkers.ToString(invariant)
                + " --memory-floor-mb " + settings.MemoryFloorMb.ToString(invariant)
                + " --cold-start-concurrency " + settings.ColdStartConcurrency.ToString(invariant)
                + " --attach-ready-timeout-ms " + ((long)settings.AttachReadyTimeoutSeconds * 1000L).ToString(invariant)
                + " --heap-recycle-mb " + ResolveHeapRecycleMb(config).ToString(invariant);
            // The fifth identity component (decision D6), so the host's record and mismatch report name it.
            if (!string.IsNullOrWhiteSpace(profileConfigPath))
                arguments += " --profile-config " + Quote(profileConfigPath!);
            return arguments;
        }

        // An absent Server section uses the same default as an isolated Worker (issue #692).
        internal static int ResolveIdleTimeoutMs(Configuration config)
        {
            int minutes = config.Server?.WorkerIdleTimeoutMinutes ?? new ServerConfig().WorkerIdleTimeoutMinutes;
            if (minutes <= 0) return 60 * 60 * 1000;
            return Math.Min(minutes * 60 * 1000, 24 * 60 * 60 * 1000);
        }

        // Server.WorkerHeapRecycleMB with the same default and clamp as an isolated Worker (WorkerProcess); 0 disables.
        internal static int ResolveHeapRecycleMb(Configuration config) =>
            Math.Max(0, config.Server?.WorkerHeapRecycleMB ?? new ServerConfig().WorkerHeapRecycleMB);

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
