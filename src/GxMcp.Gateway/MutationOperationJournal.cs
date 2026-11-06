using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Minimal durable fence for keyed mutations. It intentionally stores hashes
    /// and lifecycle state, never the mutation payload or response. A process
    /// restart can therefore refuse a potentially committed operation without
    /// replaying source, credentials, or other KB content from disk.
    /// </summary>
    internal sealed class MutationOperationJournal
    {
        private const string JournalSchemaVersion = "genexus-mutation-operations/1";
        private const int MaxEntries = 4096;
        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

        /// <summary>
        /// Bound on how long a write waits for the cross-process journal lease
        /// before failing closed. The journal path is fixed per installed exe, so
        /// every Gateway of an installation contends for the same file; the
        /// critical section is a single small read-modify-write, so this is far
        /// more than a healthy peer needs. It exists only so a wedged or
        /// crashed peer cannot block a mutation forever - exceeding it refuses
        /// the write instead of dropping the lease, because a journal written
        /// without the lease is how a committed mutation becomes replayable.
        /// </summary>
        internal static readonly TimeSpan LeaseWaitCap = TimeSpan.FromMilliseconds(2000);

        private const int LeaseRetryDelayMs = 20;
        private const string LeaseBusyError =
            "Mutation operation journal is held by another Gateway process; retry after it finishes.";
        private readonly string _path;
        private readonly string _lockPath;
        private readonly IMonotonicClock _clock;
        private readonly OperationalStateKey? _owner;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private bool _healthy = true;
        private string _error = string.Empty;
        // Contention is not corruption: a peer holding the lease blocks writes
        // now but releases it later, so this is reported as unavailable and
        // cleared by the next successful acquisition rather than latched
        // permanently the way a rejected journal file is.
        private volatile bool _leaseBusy;

        public MutationOperationJournal(string path)
            : this(path, null, new StopwatchMonotonicClock()) { }

        internal MutationOperationJournal(string path, OperationalStateKey owner)
            : this(path, (OperationalStateKey?)owner, new StopwatchMonotonicClock()) { }

        internal MutationOperationJournal(string path, IMonotonicClock clock)
            : this(path, null, clock) { }

        private MutationOperationJournal(string path, OperationalStateKey? owner, IMonotonicClock clock)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _lockPath = _path + ".lock";
            _owner = owner;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Load();
        }

        internal bool IsHealthy { get { lock (_gate) return _healthy && !_leaseBusy; } }
        internal string Error
        {
            get
            {
                lock (_gate) return _leaseBusy ? LeaseBusyError : _error;
            }
        }

        internal enum BeginResult
        {
            Started,
            UnknownAfterRestart,
            Completed,
            Conflict,
            JournalUnavailable
        }

        internal BeginResult Begin(string kbPath, string tool, string key, string payloadHash)
            => Begin(kbPath, tool, key, payloadHash, null);

        internal BeginResult Begin(OperationalStateKey owner, string tool, string key, string payloadHash,
            MutationOperationEvidence? evidence = null)
            => Begin(owner.Token, tool, key, payloadHash, evidence);

        internal BeginResult Begin(
            string kbPath,
            string tool,
            string key,
            string payloadHash,
            MutationOperationEvidence? evidence)
        {
            string id = RecordId(kbPath, tool, key);
            // Failing closed: a refused write leaves no fence, so the caller
            // reports "not executed" instead of replaying a mutation.
            BeginResult result = BeginResult.JournalUnavailable;
            TryExclusiveJournal(() => result = BeginLocked(id, kbPath, tool, key, payloadHash, evidence));
            return result;
        }

        private BeginResult BeginLocked(
            string id,
            string kbPath,
            string tool,
            string key,
            string payloadHash,
            MutationOperationEvidence? evidence)
        {
            if (!_healthy) return BeginResult.JournalUnavailable;
            // The in-memory snapshot can be stale: the journal path is fixed per
            // installed exe, so a second Gateway process writes the same file and
            // its "started" fence is invisible until this instance re-reads it.
            // Deciding before the reload would treat that fence as absent and
            // replay a mutation the peer may already have committed.
            LoadLocked();
            if (!_healthy) return BeginResult.JournalUnavailable;
            PruneLocked();
            if (_entries.TryGetValue(id, out var existing))
            {
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                    return BeginResult.Conflict;
                if (!string.IsNullOrWhiteSpace(existing.TargetHash)
                    && !string.Equals(existing.TargetHash, evidence?.TargetHash, StringComparison.Ordinal))
                    return BeginResult.Conflict;
                if (!string.IsNullOrWhiteSpace(existing.RevisionHash)
                    && !string.Equals(existing.RevisionHash, evidence?.RevisionHash, StringComparison.Ordinal))
                    return BeginResult.Conflict;
                if (!string.IsNullOrWhiteSpace(existing.ModelHash)
                    && !string.Equals(existing.ModelHash, evidence?.ModelHash, StringComparison.Ordinal))
                    return BeginResult.Conflict;
                if (!string.IsNullOrWhiteSpace(existing.EnvironmentHash)
                    && !string.Equals(existing.EnvironmentHash, evidence?.EnvironmentHash, StringComparison.Ordinal))
                    return BeginResult.Conflict;
                return string.Equals(existing.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existing.Status, "reconciled", StringComparison.OrdinalIgnoreCase)
                    ? BeginResult.Completed
                    : BeginResult.UnknownAfterRestart;
            }

            if (_entries.Count >= MaxEntries)
            {
                MarkUnhealthy("mutation operation journal reached its entry limit");
                return BeginResult.JournalUnavailable;
            }

            var entry = new Entry
            {
                Id = id,
                ScopeHash = ScopeHash(kbPath),
                Tool = tool ?? string.Empty,
                KeyHash = Hash(key),
                PayloadHash = payloadHash ?? string.Empty,
                TargetHash = evidence?.TargetHash ?? string.Empty,
                RevisionHash = evidence?.RevisionHash ?? string.Empty,
                ModelHash = evidence?.ModelHash ?? string.Empty,
                EnvironmentHash = evidence?.EnvironmentHash ?? string.Empty,
                Status = "started",
                UpdatedAtUtc = DateTime.UtcNow
            };
            _entries[id] = entry;
            if (!PersistLocked())
            {
                _entries.Remove(id);
                return BeginResult.JournalUnavailable;
            }
            return BeginResult.Started;
        }

        internal void Complete(OperationalStateKey owner, string tool, string key, string payloadHash)
            => Complete(owner.Token, tool, key, payloadHash);

        internal void Complete(string kbPath, string tool, string key, string payloadHash)
        {
            Update(kbPath, tool, key, payloadHash, "completed");
        }

        internal void Fail(OperationalStateKey owner, string tool, string key, string payloadHash)
            => Fail(owner.Token, tool, key, payloadHash);

        internal void Fail(string kbPath, string tool, string key, string payloadHash)
        {
            string id = RecordId(kbPath, tool, key);
            TryExclusiveJournal(() =>
            {
                LoadLocked();
                if (_entries.TryGetValue(id, out var existing)
                    && string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    _entries.Remove(id);
                    PersistLocked();
                }
            });
        }

        /// <summary>
        /// Returns a redacted durable state for an idempotent operation. The journal only
        /// stores hashes and lifecycle metadata, so this inspection never exposes source,
        /// KB paths, credentials, or the original mutation payload.
        /// </summary>
        internal JObject Inspect(string kbPath, string tool, string key)
        {
            // A peer can change the journal after this instance was constructed.
            // Failure to acquire its lease is not evidence that a record is absent.
            JObject result = new JObject
            {
                ["journalHealthy"] = false,
                ["code"] = "operation_journal_unavailable",
                ["error"] = LeaseBusyError,
                ["recordId"] = RecordId(kbPath, tool, key),
                ["operationKey"] = key ?? string.Empty,
                ["tool"] = tool ?? string.Empty,
                ["known"] = false,
                ["retryWithSameKey"] = false,
                ["recoveryRequired"] = true
            };
            TryExclusiveJournal(() =>
            {
                LoadLocked();
                result = InspectLocked(kbPath, tool ?? string.Empty, key ?? string.Empty);
            });
            return result;
        }

        private JObject InspectLocked(string kbPath, string tool, string key)
        {
            string id = RecordId(kbPath, tool, key);
            lock (_gate)
            {
                // Read the combined health, not the corrupt/latch flag alone: a
                // journal this instance cannot coordinate is not evidence that
                // a record is absent, and reporting not_found here would invite
                // a caller to authorise a new key over a live fence.
                bool healthy = _healthy && !_leaseBusy;
                var result = new JObject
                {
                    ["journalHealthy"] = healthy,
                    ["recordId"] = id,
                    ["operationKey"] = key ?? string.Empty,
                    ["tool"] = tool ?? string.Empty,
                    ["known"] = false,
                    ["retryWithSameKey"] = false,
                    ["recoveryRequired"] = !healthy
                };
                if (!healthy)
                {
                    result["code"] = "operation_journal_unavailable";
                    result["error"] = _leaseBusy ? LeaseBusyError : _error;
                    return result;
                }

                PruneLocked();
                if (!_entries.TryGetValue(id, out var entry))
                {
                    result["status"] = "not_found";
                    result["message"] = "No durable operation record exists for this KB, tool, and idempotency key.";
                    return result;
                }

                result["known"] = true;
                result["status"] = entry.Status;
                result["updatedAtUtc"] = entry.UpdatedAtUtc;
                result["payloadHash"] = entry.PayloadHash;
                result["evidenceBound"] = !string.IsNullOrWhiteSpace(entry.TargetHash)
                    || !string.IsNullOrWhiteSpace(entry.RevisionHash)
                    || !string.IsNullOrWhiteSpace(entry.ModelHash)
                    || !string.IsNullOrWhiteSpace(entry.EnvironmentHash);
                if (!string.IsNullOrWhiteSpace(entry.TargetHash)) result["targetIdsHash"] = entry.TargetHash;
                if (!string.IsNullOrWhiteSpace(entry.RevisionHash)) result["revisionHash"] = entry.RevisionHash;
                if (!string.IsNullOrWhiteSpace(entry.ModelHash)) result["modelHash"] = entry.ModelHash;
                if (!string.IsNullOrWhiteSpace(entry.EnvironmentHash)) result["environmentHash"] = entry.EnvironmentHash;
                result["recoveryRequired"] = string.Equals(entry.Status, "started", StringComparison.OrdinalIgnoreCase);
                result["message"] = string.Equals(entry.Status, "started", StringComparison.OrdinalIgnoreCase)
                    ? "The previous process may have committed this operation; inspect the target before authorizing a new key."
                    : "The durable record is terminal; the Gateway will not replay the mutation with this key.";
                return result;
            }
        }

        /// <summary>
        /// Records an explicit external verification for an operation that was unknown
        /// after restart. This never replays or restores data; it only closes the durable
        /// fence so the caller can proceed with a fresh authorization/key.
        /// </summary>
        internal JObject Reconcile(
            string kbPath,
            string tool,
            string key,
            string verification,
            MutationOperationEvidence? observedEvidence = null)
        {
            if (string.IsNullOrWhiteSpace(verification))
                return new JObject
                {
                    ["status"] = "Rejected",
                    ["code"] = "verification_required",
                    ["message"] = "A non-empty verification statement is required; no operation state changed."
                };

            string id = RecordId(kbPath, tool, key);
            JObject result = UnavailableEnvelope(
                "The operation journal is unavailable; repair it before reconciling an operation.");
            TryExclusiveJournal(() => result = ReconcileLocked(id, kbPath, tool, key, verification, observedEvidence));
            return result;
        }

        private JObject ReconcileLocked(
            string id,
            string kbPath,
            string tool,
            string key,
            string verification,
            MutationOperationEvidence? observedEvidence)
        {
            LoadLocked();
            if (!_healthy)
                return UnavailableEnvelope("The operation journal is unavailable; repair it before reconciling an operation.");
            if (!_entries.TryGetValue(id, out var entry))
                return new JObject
                {
                    ["status"] = "NotFound",
                    ["code"] = "operation_not_found",
                    ["message"] = "No durable operation record exists for this KB, tool, and idempotency key."
                };
            if (!string.Equals(entry.Status, "started", StringComparison.OrdinalIgnoreCase))
                return InspectLocked(kbPath, tool, key);

            if (!string.IsNullOrWhiteSpace(entry.TargetHash)
                && !string.Equals(entry.TargetHash, observedEvidence?.TargetHash, StringComparison.Ordinal))
            {
                return new JObject
                {
                    ["status"] = "Rejected",
                    ["code"] = "operation_evidence_mismatch",
                    ["message"] = "The observed target set does not match the durable operation fence; the operation remains unknown."
                };
            }
            if (!string.IsNullOrWhiteSpace(entry.RevisionHash)
                && !string.Equals(entry.RevisionHash, observedEvidence?.RevisionHash, StringComparison.Ordinal))
            {
                return new JObject
                {
                    ["status"] = "Rejected",
                    ["code"] = "operation_evidence_mismatch",
                    ["message"] = "The observed revision does not match the durable operation fence; the operation remains unknown."
                };
            }
            if (!string.IsNullOrWhiteSpace(entry.ModelHash)
                && !string.Equals(entry.ModelHash, observedEvidence?.ModelHash, StringComparison.Ordinal))
            {
                return new JObject
                {
                    ["status"] = "Rejected",
                    ["code"] = "operation_evidence_mismatch",
                    ["message"] = "The observed model identity does not match the durable operation fence; the operation remains unknown."
                };
            }
            if (!string.IsNullOrWhiteSpace(entry.EnvironmentHash)
                && !string.Equals(entry.EnvironmentHash, observedEvidence?.EnvironmentHash, StringComparison.Ordinal))
            {
                return new JObject
                {
                    ["status"] = "Rejected",
                    ["code"] = "operation_evidence_mismatch",
                    ["message"] = "The observed environment identity does not match the durable operation fence; the operation remains unknown."
                };
            }

            entry.Status = "reconciled";
            entry.UpdatedAtUtc = DateTime.UtcNow;
            entry.VerificationHash = Hash(verification);
            if (!PersistLocked())
                return new JObject
                {
                    ["status"] = "Blocked",
                    ["code"] = "operation_journal_unavailable",
                    ["error"] = _error,
                    ["message"] = "The journal could not persist the reconciliation; the unknown fence remains active in memory."
                };
            return InspectLocked(kbPath, tool, key);
        }

        private JObject UnavailableEnvelope(string message)
            => new JObject
            {
                ["status"] = "Blocked",
                ["code"] = "operation_journal_unavailable",
                ["error"] = _error,
                ["message"] = message
            };

        internal int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        internal static string RecordId(OperationalStateKey owner, string tool, string key)
            => owner.JournalKey((tool ?? string.Empty).Trim().ToLowerInvariant() + "|" + Hash(key));

        internal static string RecordId(string kbPath, string tool, string key)
            => ScopeHash(kbPath) + "|" + (tool ?? string.Empty).Trim().ToLowerInvariant() + "|" + Hash(key);

        private void Update(string kbPath, string tool, string key, string payloadHash, string status)
        {
            string id = RecordId(kbPath, tool, key);
            TryExclusiveJournal(() =>
            {
                LoadLocked();
                if (!_entries.TryGetValue(id, out var existing)
                    || !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                    return;
                existing.Status = status;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                PersistLocked();
            });
        }

        /// <summary>
        /// Runs <paramref name="body"/> as the journal's read-modify-write: the
        /// cross-process lease is taken first, then the in-process gate. The
        /// journal path is fixed per installed exe, so this is the only
        /// exclusion between Gateways; ordering it outermost means no path can
        /// wait for a peer's lease while holding this instance's gate.
        /// Returns false when the lease stays held past <see cref="LeaseWaitCap"/>,
        /// in which case <paramref name="body"/> did not run and nothing was
        /// written - the caller must fail closed rather than proceed unlocked.
        /// </summary>
        private bool TryExclusiveJournal(Action body)
        {
            FileStream? lease;
            try
            {
                lease = AcquireLease();
            }
            catch (LeaseBusyException)
            {
                _leaseBusy = true;
                return false;
            }
            using (lease)
            {
                lock (_gate) body();
            }
            return true;
        }

        private FileStream AcquireLease()
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var started = _clock.Now;
            while (true)
            {
                try
                {
                    // FileShare.None is what makes this cross-process: a second
                    // Gateway cannot hold the same lease file, so its
                    // read-modify-write cannot interleave with this one.
                    var lease = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _leaseBusy = false;
                    return lease;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                {
                    if (_clock.Now - started >= LeaseWaitCap) throw new LeaseBusyException();
                    Thread.Sleep(LeaseRetryDelayMs);
                }
            }
        }

        private sealed class LeaseBusyException : IOException { }

        private void Load()
        {
            if (TryExclusiveJournal(LoadLocked)) return;
            // Fail closed: an instance that could not read the durable fence must
            // not serve decisions from an empty snapshot. This is the busy flag
            // rather than MarkUnhealthy, because a peer still holding the lease
            // will release it - the next acquisition reloads and recovers.
            _leaseBusy = true;
            lock (_gate) _entries.Clear();
        }

        private void LoadLocked()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var info = new FileInfo(_path);
                if (info.Length <= 0 || info.Length > MaxBytes)
                    throw new InvalidDataException("journal size is outside the accepted bounds");

                JToken root = JToken.Parse(File.ReadAllText(_path));
                JArray rows;
                if (root is JArray legacyRows)
                {
                    // Accept the pre-v1 array once for an in-place upgrade.
                    rows = legacyRows;
                }
                else if (root is JObject envelope
                    && string.Equals(envelope["schemaVersion"]?.ToString(), JournalSchemaVersion, StringComparison.Ordinal)
                    && envelope["entries"] is JArray versionedRows)
                {
                    rows = versionedRows;
                }
                else
                {
                    throw new InvalidDataException("journal schemaVersion is missing or unsupported");
                }

                if (rows.Count > MaxEntries)
                    throw new InvalidDataException("journal contains too many operation entries");

                // Replace rather than merge: a peer's Complete or Fail may have
                // removed a record this instance still holds in memory, and
                // keeping it would resurrect a fence the durable state dropped.
                _entries.Clear();
                foreach (var row in rows)
                {
                    if (!(row is JObject json))
                        throw new InvalidDataException("journal contains a non-object entry");
                    var entry = json.ToObject<Entry>();
                    if (!IsValid(entry))
                        throw new InvalidDataException("journal contains an invalid operation entry");
                    if (_owner.HasValue
                        && !string.Equals(entry!.ScopeHash, ScopeHash(_owner.Value.Token), StringComparison.Ordinal))
                        throw new InvalidDataException("journal entry belongs to another operational state scope");
                    if (!IsExpired(entry!.Status, entry.UpdatedAtUtc, DateTime.UtcNow))
                    {
                        entry.UpdatedAtUtc = entry.UpdatedAtUtc.ToUniversalTime();
                        _entries[entry.Id] = entry;
                    }
                }

                PruneLocked();
                if (rows.Count != _entries.Count)
                    PersistLocked();
            }
            catch (Exception ex)
            {
                _entries.Clear();
                MarkUnhealthy("Mutation operation journal rejected: " + ex.Message);
            }
        }

        private void PruneLocked()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var id in _entries.Where(pair => IsExpired(pair.Value.Status, pair.Value.UpdatedAtUtc, now)).Select(pair => pair.Key).ToArray())
                _entries.Remove(id);
        }

        /// <summary>
        /// Settled records age out after the retention window. An unknown-outcome
        /// ("started") record never does: the process that wrote it may have committed
        /// the mutation, and expiring it would let the same key replay that mutation.
        /// It leaves the journal only through Complete, Fail, or an explicit Reconcile.
        /// </summary>
        private static bool IsExpired(string status, DateTime updatedAtUtc, DateTime now)
            => !string.Equals(status, "started", StringComparison.OrdinalIgnoreCase)
                && now - updatedAtUtc.ToUniversalTime() > Retention;

        private bool PersistLocked()
        {
            if (!_healthy) return false;
            string? temporary = null;
            try
            {
                if (_entries.Count > MaxEntries)
                    throw new InvalidDataException("mutation operation journal reached its entry limit");
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
                var document = new JObject
                {
                    ["schemaVersion"] = JournalSchemaVersion,
                    ["entries"] = JArray.FromObject(_entries.Values.OrderBy(item => item.UpdatedAtUtc).ToArray())
                };
                byte[] bytes = Encoding.UTF8.GetBytes(document.ToString(Formatting.None));
                if (bytes.LongLength > MaxBytes)
                    throw new InvalidDataException("mutation operation journal exceeded its byte limit");
                using (var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                temporary = null;
                return true;
            }
            catch (Exception ex)
            {
                MarkUnhealthy("Mutation operation journal persistence failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (temporary != null)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        private void MarkUnhealthy(string message)
        {
            _error = message ?? "Mutation operation journal unavailable.";
            _healthy = false;
        }

        private static bool IsValid(Entry? entry)
            => entry != null
                && !string.IsNullOrWhiteSpace(entry.Id)
                && !string.IsNullOrWhiteSpace(entry.ScopeHash)
                && !string.IsNullOrWhiteSpace(entry.Tool)
                && !string.IsNullOrWhiteSpace(entry.KeyHash)
                && !string.IsNullOrWhiteSpace(entry.PayloadHash)
                && (string.IsNullOrWhiteSpace(entry.TargetHash) || entry.TargetHash.Length == 64)
                && (string.IsNullOrWhiteSpace(entry.RevisionHash) || entry.RevisionHash.Length == 64)
                && (string.IsNullOrWhiteSpace(entry.ModelHash) || entry.ModelHash.Length == 64)
                && (string.IsNullOrWhiteSpace(entry.EnvironmentHash) || entry.EnvironmentHash.Length == 64)
                && (string.Equals(entry.Status, "started", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Status, "reconciled", StringComparison.OrdinalIgnoreCase))
                && entry.UpdatedAtUtc != default;

        private static string ScopeHash(string value)
        {
            string normalized = value ?? string.Empty;
            try { normalized = Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant(); }
            catch { normalized = normalized.Trim().ToLowerInvariant(); }
            return Hash(normalized);
        }

        private static string Hash(string value)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
        }

        private sealed class Entry
        {
            public string Id { get; set; } = string.Empty;
            public string ScopeHash { get; set; } = string.Empty;
            public string Tool { get; set; } = string.Empty;
            public string KeyHash { get; set; } = string.Empty;
            public string PayloadHash { get; set; } = string.Empty;
            public string TargetHash { get; set; } = string.Empty;
            public string RevisionHash { get; set; } = string.Empty;
            public string ModelHash { get; set; } = string.Empty;
            public string EnvironmentHash { get; set; } = string.Empty;
            public string Status { get; set; } = "started";
            public string VerificationHash { get; set; } = string.Empty;
            public DateTime UpdatedAtUtc { get; set; }
        }
    }

    /// <summary>
    /// Hash-only evidence binding a mutation to the target identity and the
    /// revision it was based on. Raw names, source, and version values never
    /// enter the durable journal.
    /// </summary>
    public sealed class MutationOperationEvidence
    {
        internal MutationOperationEvidence(
            string? targetHash,
            string? revisionHash,
            string? modelHash = null,
            string? environmentHash = null)
        {
            TargetHash = targetHash ?? string.Empty;
            RevisionHash = revisionHash ?? string.Empty;
            ModelHash = modelHash ?? string.Empty;
            EnvironmentHash = environmentHash ?? string.Empty;
        }

        internal string TargetHash { get; }
        internal string RevisionHash { get; }
        internal string ModelHash { get; }
        internal string EnvironmentHash { get; }

        internal static MutationOperationEvidence? FromArguments(JObject? args)
        {
            if (args == null) return null;

            var targets = new List<string>();
            foreach (var property in args.Properties())
            {
                if (!IsTargetProperty(property.Name)) continue;
                AppendValues(targets, property.Name, property.Value);
            }

            var revisions = new List<string>();
            foreach (var property in args.Properties())
            {
                if (!IsRevisionProperty(property.Name)) continue;
                AppendValues(revisions, property.Name, property.Value);
            }

            var models = new List<string>();
            foreach (var property in args.Properties())
            {
                if (!IsModelProperty(property.Name)) continue;
                AppendValues(models, property.Name, property.Value);
            }

            var environments = new List<string>();
            foreach (var property in args.Properties())
            {
                if (!IsEnvironmentProperty(property.Name)) continue;
                AppendValues(environments, property.Name, property.Value);
            }

            string targetHash = targets.Count == 0 ? string.Empty : Hash(string.Join("|", targets.OrderBy(v => v, StringComparer.Ordinal)));
            string revisionHash = revisions.Count == 0 ? string.Empty : Hash(string.Join("|", revisions.OrderBy(v => v, StringComparer.Ordinal)));
            string modelHash = models.Count == 0 ? string.Empty : Hash(string.Join("|", models.OrderBy(v => v, StringComparer.Ordinal)));
            string environmentHash = environments.Count == 0 ? string.Empty : Hash(string.Join("|", environments.OrderBy(v => v, StringComparer.Ordinal)));
            return targetHash.Length == 0 && revisionHash.Length == 0 && modelHash.Length == 0 && environmentHash.Length == 0
                ? null
                : new MutationOperationEvidence(targetHash, revisionHash, modelHash, environmentHash);
        }

        internal static MutationOperationEvidence? FromObserved(
            JToken? targetIds,
            string? revision,
            string? model = null,
            string? environment = null)
        {
            var targets = new List<string>();
            if (targetIds != null && targetIds.Type != JTokenType.Null)
                AppendValues(targets, "target", targetIds);
            var targetHash = targets.Count == 0 ? string.Empty : Hash(string.Join("|", targets.OrderBy(v => v, StringComparer.Ordinal)));
            var revisionHash = string.IsNullOrWhiteSpace(revision)
                ? string.Empty
                : Hash(new JValue(revision.Trim()).ToString(Newtonsoft.Json.Formatting.None));
            var modelHash = string.IsNullOrWhiteSpace(model)
                ? string.Empty
                : Hash(new JValue(model.Trim()).ToString(Newtonsoft.Json.Formatting.None));
            var environmentHash = string.IsNullOrWhiteSpace(environment)
                ? string.Empty
                : Hash(new JValue(environment.Trim()).ToString(Newtonsoft.Json.Formatting.None));
            return targetHash.Length == 0 && revisionHash.Length == 0 && modelHash.Length == 0 && environmentHash.Length == 0
                ? null
                : new MutationOperationEvidence(targetHash, revisionHash, modelHash, environmentHash);
        }

        private static bool IsTargetProperty(string name)
            => name.Equals("name", StringComparison.OrdinalIgnoreCase)
                || name.Equals("target", StringComparison.OrdinalIgnoreCase)
                || name.Equals("targets", StringComparison.OrdinalIgnoreCase)
                || name.Equals("objectName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("objectNames", StringComparison.OrdinalIgnoreCase)
                || name.Equals("attribute", StringComparison.OrdinalIgnoreCase)
                || name.Equals("objectId", StringComparison.OrdinalIgnoreCase);

        private static bool IsRevisionProperty(string name)
            => name.Equals("versionToken", StringComparison.OrdinalIgnoreCase)
                || name.Equals("expectedVersion", StringComparison.OrdinalIgnoreCase)
                || name.Equals("baseVersion", StringComparison.OrdinalIgnoreCase)
                || name.Equals("baseRevision", StringComparison.OrdinalIgnoreCase)
                || name.Equals("revision", StringComparison.OrdinalIgnoreCase)
                || name.Equals("observedRevision", StringComparison.OrdinalIgnoreCase);

        private static bool IsModelProperty(string name)
            => name.Equals("modelId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("model", StringComparison.OrdinalIgnoreCase)
                || name.Equals("modelGuid", StringComparison.OrdinalIgnoreCase)
                || name.Equals("generatorModel", StringComparison.OrdinalIgnoreCase);

        private static bool IsEnvironmentProperty(string name)
            => name.Equals("environmentId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("environment", StringComparison.OrdinalIgnoreCase)
                || name.Equals("environmentName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dataStore", StringComparison.OrdinalIgnoreCase);

        private static void AppendValues(List<string> values, string name, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            if (token is JArray array)
            {
                foreach (var item in array) AppendValues(values, name, item);
                return;
            }
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    AppendValues(values, name + "." + property.Name, property.Value);
                return;
            }
            values.Add(token.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static string Hash(string value)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
        }
    }
}
