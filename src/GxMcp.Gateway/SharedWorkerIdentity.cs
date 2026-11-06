using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Physical compatibility identity for a shared Worker host. A KB path alone
    /// is not sufficient: different SDK majors, drivers, installations, checkouts,
    /// or profile configuration files must never attach to the same SDK process.
    /// </summary>
    internal sealed class SharedWorkerIdentity
    {
        public string WorkerExecutable { get; init; } = string.Empty;
        public string KbPath { get; init; } = string.Empty;
        public string InstallationPath { get; init; } = string.Empty;
        public string Driver { get; init; } = string.Empty;
        public string Major { get; init; } = string.Empty;
        public string ProfileConfigPath { get; init; } = string.Empty;
        public string Key { get; init; } = string.Empty;

        // Registry v2 key: SHA-256 of the normalized KB directory only (design decision A3), so every Gateway that
        // opens the same KB directory finds the same record, whatever the other components are. Empty when the
        // identity has no KB path.
        public string KbKey { get; init; } = string.Empty;

        internal static SharedWorkerIdentity Create(
            string? workerExecutable,
            string? kbPath,
            string? installationPath,
            string? driver,
            string? major,
            string? profileConfigPath = null)
        {
            string executable = NormalizePath(workerExecutable);
            string kb = NormalizePath(kbPath);
            string installation = NormalizePath(installationPath);
            string normalizedDriver = NormalizeToken(driver);
            string normalizedMajor = NormalizeToken(major);
            string profile = NormalizePath(profileConfigPath);
            string material = string.Join("\n", executable, kb, installation, normalizedDriver, normalizedMajor, profile);

            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            var key = new StringBuilder(digest.Length * 2);
            foreach (byte value in digest)
                key.Append(value.ToString("x2"));

            return new SharedWorkerIdentity
            {
                WorkerExecutable = executable,
                KbPath = kb,
                InstallationPath = installation,
                Driver = normalizedDriver,
                Major = normalizedMajor,
                ProfileConfigPath = profile,
                Key = key.ToString(),
                KbKey = kb.Length == 0 ? string.Empty : SharedHostKbKey.Compute(kb)
            };
        }

        // The kbKey rule lives once in GxMcp.Common.SharedHost.SharedHostKbKey, linked into the Gateway and the
        // Worker_Host. Throws ArgumentException for a null or whitespace KB path.
        internal static string ComputeKbKey(string? kbPath) => SharedHostKbKey.Compute(kbPath);

        // Same rule as the kbKey normalization, applied to every path component.
        internal static string NormalizePath(string? value) => SharedHostKbKey.NormalizeKbPath(value);

        internal static string NormalizeToken(string? value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant();

        internal SharedHostRecordIdentity ToRecordIdentity() =>
            new SharedHostRecordIdentity
            {
                Key = Key,
                KbPath = KbPath,
                WorkerExecutable = WorkerExecutable,
                InstallationPath = InstallationPath,
                Driver = Driver,
                Major = Major,
                ProfileConfigPath = ProfileConfigPath
            };

        // Pure builder for SHARED_WORKER_IDENTITY_MISMATCH (Requirement 2 criterion 5, decision D6). Both sides are
        // compared after normalization, so case-only, trailing-separator and surrounding-whitespace differences are
        // not reported. One entry per differing component, in the order of SharedWorkerIdentityComponent.All.
        internal static IReadOnlyList<SharedWorkerIdentityDifference> BuildMismatch(
            SharedWorkerIdentity requested,
            SharedHostRecordIdentity live)
        {
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            if (live == null) throw new ArgumentNullException(nameof(live));

            string requestedKb = NormalizePath(requested.KbPath);
            string liveKb = NormalizePath(live.KbPath);
            if (!string.Equals(requestedKb, liveKb, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "An identity mismatch compares two identities of one KB directory; got '" + requestedKb +
                    "' and '" + liveKb + "'.",
                    nameof(live));
            }

            var differences = new List<SharedWorkerIdentityDifference>();
            foreach (string component in SharedWorkerIdentityComponent.All)
            {
                string requestedValue;
                string liveValue;
                switch (component)
                {
                    case SharedWorkerIdentityComponent.InstallationPath:
                        requestedValue = NormalizePath(requested.InstallationPath);
                        liveValue = NormalizePath(live.InstallationPath);
                        break;
                    case SharedWorkerIdentityComponent.Driver:
                        requestedValue = NormalizeToken(requested.Driver);
                        liveValue = NormalizeToken(live.Driver);
                        break;
                    case SharedWorkerIdentityComponent.Major:
                        requestedValue = NormalizeToken(requested.Major);
                        liveValue = NormalizeToken(live.Major);
                        break;
                    case SharedWorkerIdentityComponent.WorkerExecutable:
                        requestedValue = NormalizePath(requested.WorkerExecutable);
                        liveValue = NormalizePath(live.WorkerExecutable);
                        break;
                    default:
                        requestedValue = NormalizePath(requested.ProfileConfigPath);
                        liveValue = NormalizePath(live.ProfileConfigPath);
                        break;
                }

                if (!string.Equals(requestedValue, liveValue, StringComparison.Ordinal))
                {
                    differences.Add(new SharedWorkerIdentityDifference
                    {
                        Component = component,
                        Requested = requestedValue,
                        Live = liveValue
                    });
                }
            }

            return differences;
        }
    }

    // The five Physical_KB_Identity components besides the KB directory, in the fixed reporting order of
    // Requirement 2 criterion 5. The profile config path is the fifth component (decision D6).
    internal static class SharedWorkerIdentityComponent
    {
        internal const string InstallationPath = "installationPath";
        internal const string Driver = "driver";
        internal const string Major = "major";
        internal const string WorkerExecutable = "workerExecutable";
        internal const string ProfileConfigPath = "profileConfigPath";

        internal static readonly IReadOnlyList<string> All = new[]
        {
            InstallationPath, Driver, Major, WorkerExecutable, ProfileConfigPath
        };
    }

    // One differing component with its normalized requested and live values.
    internal sealed class SharedWorkerIdentityDifference
    {
        public string Component { get; init; } = string.Empty;
        public string Requested { get; init; } = string.Empty;
        public string Live { get; init; } = string.Empty;
    }

    internal sealed class SharedWorkerRecord
    {
        public SharedWorkerIdentity Identity { get; set; } = new SharedWorkerIdentity();
        public int HostPid { get; set; }
        public long HostStartTimeUtcTicks { get; set; }
        public int WorkerPid { get; set; }
        public long WorkerStartTimeUtcTicks { get; set; }
        public string PipeName { get; set; } = string.Empty;
        public long Generation { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    internal sealed class SharedWorkerRegistryReservation : IDisposable
    {
        private Mutex? _mutex;
        private bool _held;

        internal SharedWorkerRegistryReservation(Mutex mutex)
        {
            _mutex = mutex;
            _held = true;
        }

        public void Dispose()
        {
            if (!_held) return;
            _held = false;
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }
    }

    internal static class SharedWorkerRegistry
    {
        private static readonly object Gate = new object();
        private static readonly string RegistryRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GxMcp", "shared-workers");

        internal static string GetRecordPath(SharedWorkerIdentity identity) =>
            Path.Combine(RegistryRoot, identity.Key + ".json");

        internal static string MutexName(SharedWorkerIdentity identity) =>
            "Local\\GxMcp.SharedWorker." + identity.Key;

        internal static string PipeName(SharedWorkerIdentity identity) =>
            "GxMcpShared_" + identity.Key;

        internal static SharedWorkerRegistryReservation? TryReserve(
            SharedWorkerIdentity identity,
            TimeSpan timeout,
            out SharedWorkerRecord? existing)
        {
            existing = null;
            Directory.CreateDirectory(RegistryRoot);
            var mutex = new Mutex(false, MutexName(identity));
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }

                string path = GetRecordPath(identity);
                existing = ReadRecord(path);
                if (IsRecordLive(existing, identity))
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                    return null;
                }

                ReapStaleRecord(existing, identity);
                TryDelete(path);
                return new SharedWorkerRegistryReservation(mutex);
            }
            catch
            {
                if (acquired) { try { mutex.ReleaseMutex(); } catch { } }
                mutex.Dispose();
                throw;
            }
        }

        internal static bool IsRecordLive(SharedWorkerRecord? record, SharedWorkerIdentity identity)
        {
            if (!IsRecordShapeValid(record, identity)) return false;
            try
            {
                using var host = Process.GetProcessById(record!.HostPid);
                using var worker = Process.GetProcessById(record.WorkerPid);
                return IsSameProcess(host, record.HostPid, record.HostStartTimeUtcTicks)
                    && IsSameProcess(worker, record.WorkerPid, record.WorkerStartTimeUtcTicks);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsSameProcess(Process process, int expectedPid, long expectedStartTimeUtcTicks)
        {
            if (process == null || expectedPid <= 0 || expectedStartTimeUtcTicks <= 0) return false;
            try
            {
                return process.Id == expectedPid
                    && !process.HasExited
                    && process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsRecordShapeValid(SharedWorkerRecord? record, SharedWorkerIdentity identity)
        {
            return record != null
                && string.Equals(record.Identity?.Key, identity.Key, StringComparison.Ordinal)
                && record.HostPid > 0
                && record.HostStartTimeUtcTicks > 0
                && record.WorkerPid > 0
                && record.WorkerStartTimeUtcTicks > 0
                && !string.IsNullOrWhiteSpace(record.PipeName)
                && record.Generation > 0;
        }

        internal static SharedWorkerRecord? ReadRecord(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<SharedWorkerRecord>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        internal static void WriteRecord(string path, SharedWorkerRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            lock (Gate)
            {
                string temp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, JsonConvert.SerializeObject(record, Formatting.None));
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
        }

        private static void ReapStaleRecord(SharedWorkerRecord? record, SharedWorkerIdentity identity)
        {
            if (!IsRecordShapeValid(record, identity)) return;
            TryKillExact(record!.WorkerPid, record.WorkerStartTimeUtcTicks);
            TryKillExact(record.HostPid, record.HostStartTimeUtcTicks);
        }

        private static void TryKillExact(int pid, long startTimeUtcTicks)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!IsSameProcess(process, pid, startTimeUtcTicks)) return;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ---------------------------------------------------------------
        // Registry v2 (one record per KB directory, keyed by kbKey; decisions A3 and A4). The v1 members above stay
        // for compatibility with their tests; the shared-host path uses only the v2 members below.
        // ---------------------------------------------------------------

        internal const string RecoveryActionRemovedRecord = "removed-record";
        internal const string RecoveryActionStoppedProcess = "stopped-process";

        private const int KbKeyLength = 64;
        private static readonly TimeSpan ReplaceRetryWindow = TimeSpan.FromSeconds(2);

        internal static string DefaultV2Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GxMcp", "shared-workers", "v2");

        internal static string GetV2Root(string? registryRoot = null) =>
            string.IsNullOrWhiteSpace(registryRoot) ? DefaultV2Root : registryRoot;

        internal static bool IsValidKbKey(string? kbKey)
        {
            if (kbKey == null || kbKey.Length != KbKeyLength) return false;
            foreach (char ch in kbKey)
            {
                bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        // Only a 64-character lowercase hex key is accepted, so a key can never escape the registry directory.
        internal static string GetRecordPathV2(string kbKey, string? registryRoot = null)
        {
            if (!IsValidKbKey(kbKey))
                throw new ArgumentException("kbKey must be 64 lowercase hexadecimal characters.", nameof(kbKey));
            return Path.Combine(GetV2Root(registryRoot), kbKey + ".json");
        }

        internal static string ElectionMutexNameV2(string kbKey) =>
            "Local\\GxMcp.SharedWorker.v2.Kb." + RequireKbKey(kbKey);

        internal static string HostMutexNameV2(string kbKey) =>
            "Local\\GxMcp.SharedWorker.v2.Host." + RequireKbKey(kbKey);

        internal static string PipeNameV2(SharedWorkerIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            return "GxMcpShared2_" + identity.Key;
        }

        internal static RecordReadResult ReadRecordV2(string kbKey, string? registryRoot = null) =>
            RecordReadResult.ReadFile(GetRecordPathV2(kbKey, registryRoot));

        // Atomic write: a temp file in the same directory, then one rename over the record, so a concurrent reader
        // sees either the previous or the new record, never a partial or missing one. File.Move with overwrite
        // (MoveFileEx MOVEFILE_REPLACE_EXISTING) is used instead of File.Replace: ReplaceFile has a window in which
        // the record path does not exist, and a reader then observed the record as absent.
        internal static string WriteRecordV2(SharedHostRecord record, string? registryRoot = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string path = GetRecordPathV2(record.KbKey, registryRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temp = path + ".tmp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, record.ToJson(), new UTF8Encoding(false));
                // The rename fails with a sharing or access error while a reader has the record open (readers
                // share delete, but a rename over an open file is still refused); retry for a bounded time.
                var retrying = Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        File.Move(temp, path, overwrite: true);
                        break;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                        && retrying.Elapsed < ReplaceRetryWindow)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            return path;
        }

        // Removes one stale registry v2 record (Requirement 7 criteria 2, 7 and 9). The caller holds the election
        // mutex of kbKey, so no other Gateway rewrites the record between the re-read and the delete.
        internal static StaleRecordRemovalResult RemoveStaleRecord(
            string kbKey,
            SharedHostRecord? record,
            StaleReason reason,
            SharedWorkerRecoveryContext? context = null)
        {
            StaleReasonToken(reason); // ArgumentException for StaleReason.None, before the file is touched.
            var ctx = context ?? new SharedWorkerRecoveryContext();
            string path = GetRecordPathV2(kbKey, ctx.RegistryRoot);

            var current = RecordReadResult.ReadFile(path);
            switch (current.Status)
            {
                case RecordReadStatus.Absent:
                    return StaleRecordRemovalResult.Create(StaleRecordRemovalOutcome.AlreadyAbsent, kbKey, path);
                case RecordReadStatus.Unparseable:
                    if (record != null)
                        return StaleRecordRemovalResult.Create(StaleRecordRemovalOutcome.RecordChanged, kbKey, path);
                    break;
                case RecordReadStatus.Parsed:
                    if (record == null
                        || current.Record == null
                        || current.Record.HostPid != record.HostPid
                        || current.Record.HostStartTimeUtcTicks != record.HostStartTimeUtcTicks)
                        return StaleRecordRemovalResult.Create(StaleRecordRemovalOutcome.RecordChanged, kbKey, path);
                    break;
                default:
                    // Unreadable: fail closed, the file is left in place.
                    return StaleRecordRemovalResult.Create(StaleRecordRemovalOutcome.RemoveFailed, kbKey, path,
                        "record could not be re-read before removal: " + current.Error);
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return StaleRecordRemovalResult.Create(StaleRecordRemovalOutcome.RemoveFailed, kbKey, path,
                    ex.GetType().Name + ": " + ex.Message);
            }

            string entry = FormatRecoveryLogEntry(
                RecoveryActionRemovedRecord,
                StaleRecoveryRegistryNames.SharedWorkerRegistry,
                kbKey,
                record?.HostPid ?? 0,
                record?.HostStartTimeUtcTicks ?? 0,
                ctx.RecoveringGatewayPid,
                reason);
            ctx.Log(entry);
            return StaleRecordRemovalResult.Create(StaleRecordRemovalOutcome.Removed, kbKey, path, null, entry);
        }

        // Stops one orphan process only when it is still exactly the recorded process (Requirement 7 criteria 4
        // and 9). A Gateway PID is refused before any probe. The start time and the image path are re-read
        // immediately before termination. No log entry is written here: the caller knows the record key and the
        // registry name and logs FormatRecoveryLogEntry(RecoveryActionStoppedProcess, ...).
        internal static OrphanStopResult StopOrphanExact(
            int pid,
            long startTicks,
            string expectedImagePath,
            SharedWorkerRecoveryContext? context = null)
        {
            var ctx = context ?? new SharedWorkerRecoveryContext();

            if (pid <= 0 || startTicks <= 0 || string.IsNullOrWhiteSpace(expectedImagePath))
                return OrphanStopResult.Create(OrphanStopOutcome.RefusedInvalidArguments, pid,
                    "pid, start time and expected image path are required");

            if (pid == ctx.RecoveringGatewayPid || ContainsPid(ctx.GatewayPids, pid))
                return OrphanStopResult.Create(OrphanStopOutcome.RefusedGatewayPid, pid,
                    "pid " + pid.ToString(CultureInfo.InvariantCulture) + " is a Gateway process");

            var probe = ctx.Probe.Probe(pid);
            switch (probe.Status)
            {
                case ProcessProbeStatus.NotRunning:
                    return OrphanStopResult.Create(OrphanStopOutcome.AlreadyExited, pid, null);
                case ProcessProbeStatus.RunningStartTimeUnreadable:
                    return OrphanStopResult.Create(OrphanStopOutcome.RefusedStartTimeUnreadable, pid,
                        "start time of pid " + pid.ToString(CultureInfo.InvariantCulture) + " cannot be read");
            }

            if (!SharedHostRecordClassifier.SameMillisecond(startTicks, probe.StartTimeUtcTicks))
                return OrphanStopResult.Create(OrphanStopOutcome.RefusedStartTimeMismatch, pid,
                    "pid " + pid.ToString(CultureInfo.InvariantCulture) + " started at " +
                    probe.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture) + ", recorded " +
                    startTicks.ToString(CultureInfo.InvariantCulture));

            if (!ctx.ReadImagePath(pid, out string? imagePath, out string? imageError) || string.IsNullOrWhiteSpace(imagePath))
                return OrphanStopResult.Create(OrphanStopOutcome.RefusedImagePathUnreadable, pid,
                    imageError ?? "image path of pid " + pid.ToString(CultureInfo.InvariantCulture) + " is empty");

            if (!SameImagePath(expectedImagePath, imagePath))
                return OrphanStopResult.Create(OrphanStopOutcome.RefusedImagePathMismatch, pid,
                    "pid " + pid.ToString(CultureInfo.InvariantCulture) + " runs '" + imagePath + "', expected '" +
                    expectedImagePath + "'");

            string? terminateError = ctx.Terminate(pid);
            if (terminateError != null)
                return OrphanStopResult.Create(OrphanStopOutcome.KillFailed, pid, terminateError);

            // Measured on the context clock, so the stop wait of Requirement 7 criterion 9 is deterministic under a
            // fake clock; the default clock is the system clock.
            DateTime waitStarted = ctx.Clock.UtcNow;
            while (true)
            {
                var after = ctx.Probe.Probe(pid);
                bool gone = after.Status == ProcessProbeStatus.NotRunning
                    || (after.Status == ProcessProbeStatus.Running
                        && !SharedHostRecordClassifier.SameMillisecond(startTicks, after.StartTimeUtcTicks));
                if (gone) return OrphanStopResult.Create(OrphanStopOutcome.Stopped, pid, null);

                if (ctx.Clock.UtcNow - waitStarted >= ctx.StopWaitTimeout)
                    return OrphanStopResult.Create(OrphanStopOutcome.StillRunning, pid,
                        "pid " + pid.ToString(CultureInfo.InvariantCulture) + " still running after " +
                        ((long)ctx.StopWaitTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms");

                ctx.Clock.Sleep(ctx.StopPollInterval);
            }
        }

        // Pure kill-set selector (Property 10, Requirement 7 criterion 4). See the contract in
        // SharedWorkerRegistryV2Tests.cs. A PID without an observation counts as NotRunning. When several
        // candidates name one WorkerPid, the first candidate that passes every rule is kept.
        internal static IReadOnlyList<OrphanCandidate> SelectProcessesToTerminate(
            IReadOnlyList<OrphanCandidate> candidates,
            IReadOnlyDictionary<int, ProcessObservation> observations,
            IReadOnlyCollection<int> gatewayPids)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (observations == null) throw new ArgumentNullException(nameof(observations));
            if (gatewayPids == null) throw new ArgumentNullException(nameof(gatewayPids));

            var protectedPids = new HashSet<int>(gatewayPids);
            foreach (var candidate in candidates)
            {
                if (candidate != null && candidate.OwnerGatewayPid > 0) protectedPids.Add(candidate.OwnerGatewayPid);
            }

            var selected = new List<OrphanCandidate>();
            var selectedPids = new HashSet<int>();
            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                if (!WorkerMatches(candidate, observations)) continue;
                if (protectedPids.Contains(candidate.WorkerPid)) continue;
                if (IsOwnerLive(candidate, observations)) continue;
                if (!selectedPids.Add(candidate.WorkerPid)) continue;
                selected.Add(candidate);
            }

            return selected;
        }

        internal static string FormatRecoveryLogEntry(
            string action,
            string registryName,
            string recordKey,
            int pid,
            long recordedStartTimeUtcTicks,
            int recoveringGatewayPid,
            StaleReason reason)
        {
            string token = StaleReasonToken(reason);
            return "[Gateway] stale-ownership-recovery action=" + action +
                " registry=" + registryName +
                " key=" + recordKey +
                " pid=" + (pid > 0 ? pid.ToString(CultureInfo.InvariantCulture) : "unknown") +
                " recordedStartTimeUtcTicks=" + (recordedStartTimeUtcTicks > 0
                    ? recordedStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture)
                    : "unknown") +
                " recoveringGatewayPid=" + recoveringGatewayPid.ToString(CultureInfo.InvariantCulture) +
                " reason=" + token;
        }

        internal static string StaleReasonToken(StaleReason reason)
        {
            switch (reason)
            {
                case StaleReason.OwnerNotRunning: return "owner-not-running";
                case StaleReason.StartTimeMismatch: return "start-time-mismatch";
                case StaleReason.UnparseableRecord: return "unparseable-record";
                case StaleReason.OrphanedWorker: return "orphaned-worker";
                default:
                    throw new ArgumentException("A stale reason other than None is required.", nameof(reason));
            }
        }

        private static string RequireKbKey(string kbKey)
        {
            if (!IsValidKbKey(kbKey))
                throw new ArgumentException("kbKey must be 64 lowercase hexadecimal characters.", nameof(kbKey));
            return kbKey;
        }

        private static bool ContainsPid(IReadOnlyCollection<int>? pids, int pid)
        {
            if (pids == null) return false;
            foreach (int value in pids)
            {
                if (value == pid) return true;
            }
            return false;
        }

        private static bool SameImagePath(string? expected, string? observed)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(observed)) return false;
            return string.Equals(
                SharedWorkerIdentity.NormalizePath(expected),
                SharedWorkerIdentity.NormalizePath(observed),
                StringComparison.Ordinal);
        }

        private static bool WorkerMatches(OrphanCandidate candidate, IReadOnlyDictionary<int, ProcessObservation> observations)
        {
            if (candidate.WorkerPid <= 0
                || candidate.WorkerStartTimeUtcTicks <= 0
                || string.IsNullOrWhiteSpace(candidate.ExpectedImagePath))
                return false;
            if (!observations.TryGetValue(candidate.WorkerPid, out var observation) || observation == null) return false;
            if (observation.Probe.Status != ProcessProbeStatus.Running) return false;
            if (!SharedHostRecordClassifier.SameMillisecond(candidate.WorkerStartTimeUtcTicks, observation.Probe.StartTimeUtcTicks))
                return false;
            return SameImagePath(candidate.ExpectedImagePath, observation.ImagePath);
        }

        // Fail closed: an owner whose start time cannot be read, or a running owner with no recorded start time,
        // counts as live.
        private static bool IsOwnerLive(OrphanCandidate candidate, IReadOnlyDictionary<int, ProcessObservation> observations)
        {
            if (candidate.OwnerGatewayPid <= 0) return false;
            if (!observations.TryGetValue(candidate.OwnerGatewayPid, out var observation) || observation == null) return false;
            switch (observation.Probe.Status)
            {
                case ProcessProbeStatus.NotRunning:
                    return false;
                case ProcessProbeStatus.Running:
                    if (candidate.OwnerGatewayStartTimeUtcTicks <= 0) return true;
                    return SharedHostRecordClassifier.SameMillisecond(
                        candidate.OwnerGatewayStartTimeUtcTicks, observation.Probe.StartTimeUtcTicks);
                default:
                    return true;
            }
        }
    }

    // Registry names written into stale-ownership recovery log entries (Requirement 7 criterion 7).
    internal static class StaleRecoveryRegistryNames
    {
        internal const string WorkerOwnershipRegistry = "WorkerOwnershipRegistry";
        internal const string SharedWorkerRegistry = "SharedWorkerRegistry";
        internal const string SingleInstanceLock = "SingleInstanceLock";
    }

    internal delegate bool ImagePathReader(int pid, out string? imagePath, out string? error);

    // Available_Physical_Memory in MB for the Gateway admission pre-check. False means unreadable.
    internal delegate bool AvailableMemoryReader(out long availableMb, out string? error);

    // Process and registry seams of stale-ownership recovery. The defaults are the production behavior; tests
    // replace every member that touches a real process or the real %LOCALAPPDATA%.
    internal sealed class SharedWorkerRecoveryContext
    {
        public IProcessProbe Probe { get; init; } = new ProcessIdentity();
        public ImagePathReader ReadImagePath { get; init; } = ProcessIdentity.TryGetImagePath;
        public Func<int, string?> Terminate { get; init; } = TerminateProcess;
        public Action<string> Log { get; init; } = Program.Log;
        public int RecoveringGatewayPid { get; init; } = Environment.ProcessId;
        public IReadOnlyCollection<int> GatewayPids { get; init; } = Array.Empty<int>();
        public TimeSpan StopWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan StopPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
        public string? RegistryRoot { get; init; }

        // Launcher seams of the v2 Connect sequence (SharedWorkerHostLauncher.ConnectV2).
        // Null means WorkerOwnershipRegistry.DefaultRoot.
        public string? OwnershipRegistryRoot { get; init; }
        public ISharedWorkerLauncherClock Clock { get; init; } = SharedWorkerSystemClock.Instance;
        public SharedWorkerHostStarter StartHost { get; init; } = SharedWorkerHostLauncher.StartHostProcess;
        public SharedWorkerPipeConnector ConnectPipe { get; init; } = SharedWorkerHostLauncher.ConnectPipeProcess;
        public SharedWorkerElectionMutexFactory AcquireElectionMutex { get; init; } = SharedWorkerHostLauncher.AcquireNamedMutex;
        public TimeSpan RegistryPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

        // Read last by the advisory admission pre-check, right before the host start (decision A2).
        public AvailableMemoryReader ReadAvailableMemory { get; init; } = MachineMemory.TryGetAvailablePhysicalMb;

        // Kills exactly one process (not its tree: only the root was verified). Returns null on success or when the
        // process has already exited, otherwise the error text.
        internal static string? TerminateProcess(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is NotSupportedException)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    internal enum OrphanStopOutcome
    {
        Stopped,
        AlreadyExited,
        RefusedInvalidArguments,
        RefusedGatewayPid,
        RefusedStartTimeUnreadable,
        RefusedStartTimeMismatch,
        RefusedImagePathUnreadable,
        RefusedImagePathMismatch,
        KillFailed,
        StillRunning
    }

    internal sealed class OrphanStopResult
    {
        public OrphanStopOutcome Outcome { get; init; }
        public int Pid { get; init; }
        public string? Error { get; init; }
        public bool Stopped => Outcome == OrphanStopOutcome.Stopped;

        internal static OrphanStopResult Create(OrphanStopOutcome outcome, int pid, string? error) =>
            new OrphanStopResult { Outcome = outcome, Pid = pid, Error = error };
    }

    internal enum StaleRecordRemovalOutcome
    {
        Removed,
        AlreadyAbsent,
        RecordChanged,
        RemoveFailed
    }

    internal sealed class StaleRecordRemovalResult
    {
        public StaleRecordRemovalOutcome Outcome { get; init; }
        public string RecordKey { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string? Error { get; init; }

        // The log line written, only for Removed.
        public string? LogEntry { get; init; }

        internal static StaleRecordRemovalResult Create(
            StaleRecordRemovalOutcome outcome, string recordKey, string path, string? error = null, string? logEntry = null) =>
            new StaleRecordRemovalResult
            {
                Outcome = outcome,
                RecordKey = recordKey,
                Path = path,
                Error = error,
                LogEntry = logEntry
            };
    }

    // One stale-ownership record that names a Worker process (WorkerOwnershipRegistry or SharedWorkerRegistry).
    internal sealed class OrphanCandidate
    {
        public string RegistryName { get; init; } = string.Empty;
        public string RecordKey { get; init; } = string.Empty;
        public int OwnerGatewayPid { get; init; }
        public long OwnerGatewayStartTimeUtcTicks { get; init; }
        public int WorkerPid { get; init; }
        public long WorkerStartTimeUtcTicks { get; init; }
        public string ExpectedImagePath { get; init; } = string.Empty;
    }

    // What a probe and the image reader observed for one PID.
    internal sealed class ProcessObservation
    {
        public int Pid { get; init; }
        public ProcessProbeResult Probe { get; init; }
        public string? ImagePath { get; init; }
    }
}
