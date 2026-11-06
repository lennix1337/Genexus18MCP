using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>One source part observed by a backfill slice.</summary>
    public sealed class SourceStoreBackfillPart
    {
        public string PartName { get; set; }
        public string Source { get; set; }
        public DateTime? LastUpdate { get; set; }
        public string VersionToken { get; set; }

        // A null Source is an SDK read failure, not an empty part.  Keep that
        // distinction explicit so a reader cannot accidentally turn a failed
        // read into a successful no-op by returning an empty list.
        public bool ReadSucceeded { get; set; } = true;
        public string ReadError { get; set; }

        // Empty source is valid read evidence (GeneXus exposes empty parts as
        // empty strings), but it is still a successfully observed part and
        // must not be confused with a missing record.
        public bool IsEmpty => Source != null && Source.Length == 0;
        public bool IsReadFailure => !ReadSucceeded || Source == null;
        public bool IsDeleted { get; set; }
    }

    /// <summary>Persisted, resumable source-store population state.</summary>
    public sealed class SourceStoreBackfillState
    {
        public string State { get; set; } = "off";
        public string TriggerState { get; set; }
        public string Cursor { get; set; }
        public string CatalogSignature { get; set; }
        public int CursorIndex { get; set; }
        public int TotalObjects { get; set; }
        public int ProcessedObjects { get; set; }
        public int StoredObjects { get; set; }
        public int StaleObjects { get; set; }
        public int RemovedObjects { get; set; }
        public int RetryCount { get; set; }
        public int? EtaMs { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string LastError { get; set; }
        public List<string> ReadFailedObjects { get; set; } = new List<string>();
        public int FailedObjects => ReadFailedObjects?.Count ?? 0;

        /// <summary>
        /// Upper bound on the GUIDs reported through whoami and diagnostic snapshots;
        /// <see cref="FailedObjects"/> always carries the full count.
        /// </summary>
        public const int MaxReportedReadFailedObjects = 50;

        /// <summary>
        /// True once the walk has certified every readable object. Objects listed in
        /// <see cref="ReadFailedObjects"/> fail the same SDK read a source search would
        /// attempt, so <c>completeWithFailures</c> certifies coverage like <c>complete</c>.
        /// </summary>
        public bool IsComplete => State == "complete" || State == "completeWithFailures";

        public string ResolveTriggerState()
        {
            if (string.Equals(State, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(State, "idle", StringComparison.OrdinalIgnoreCase))
            {
                return State;
            }
            if (!string.IsNullOrWhiteSpace(TriggerState)) return TriggerState;
            if (string.Equals(State, "off", StringComparison.OrdinalIgnoreCase))
            {
                return Configuration.SourceStoreBackfillEnabled ? "waitingForTrigger" : "disabledByConfig";
            }
            return State;
        }

        public JObject ToJson(int? maxReadFailedObjects = null)
        {
            var reportedFailures = ReadFailedObjects ?? new List<string>();
            if (maxReadFailedObjects.HasValue)
                reportedFailures = reportedFailures.Take(maxReadFailedObjects.Value).ToList();
            return new JObject
            {
                ["state"] = State,
                ["triggerState"] = ResolveTriggerState(),
                ["cursor"] = Cursor,
                ["catalogSignature"] = CatalogSignature,
                ["cursorIndex"] = CursorIndex,
                ["totalObjects"] = TotalObjects,
                ["processedObjects"] = ProcessedObjects,
                ["storedObjects"] = StoredObjects,
                ["staleObjects"] = StaleObjects,
                ["removedObjects"] = RemovedObjects,
                ["failedObjects"] = FailedObjects,
                ["readFailedObjects"] = new JArray(reportedFailures),
                ["retryCount"] = RetryCount,
                ["etaMs"] = EtaMs == null ? JValue.CreateNull() : new JValue(EtaMs.Value),
                ["startedAtUtc"] = StartedAtUtc?.ToString("o"),
                ["updatedAtUtc"] = UpdatedAtUtc?.ToString("o"),
                ["completedAtUtc"] = CompletedAtUtc?.ToString("o"),
                ["lastError"] = LastError
            };
        }

        public static SourceStoreBackfillState FromJson(JObject json)
        {
            if (json == null) return new SourceStoreBackfillState();

            // The persisted "triggerState" is a snapshot resolved from "state" at
            // write time.  It is deliberately not loaded back: reloading it would
            // freeze the value while State keeps progressing after a restart
            // (issue #554), so the live value is always derived from State.
            var state = new SourceStoreBackfillState
            {
                State = json["state"]?.ToString() ?? "off",
                Cursor = json["cursor"]?.ToString(),
                CatalogSignature = json["catalogSignature"]?.ToString(),
                CursorIndex = json["cursorIndex"]?.ToObject<int>() ?? 0,
                TotalObjects = json["totalObjects"]?.ToObject<int>() ?? 0,
                ProcessedObjects = json["processedObjects"]?.ToObject<int>() ?? 0,
                StoredObjects = json["storedObjects"]?.ToObject<int>() ?? 0,
                StaleObjects = json["staleObjects"]?.ToObject<int>() ?? 0,
                RemovedObjects = json["removedObjects"]?.ToObject<int>() ?? 0,
                RetryCount = json["retryCount"]?.ToObject<int>() ?? 0,
                EtaMs = json["etaMs"]?.Type == JTokenType.Integer ? json["etaMs"].ToObject<int>() : (int?)null,
                StartedAtUtc = ParseDate(json["startedAtUtc"]?.ToString()),
                UpdatedAtUtc = ParseDate(json["updatedAtUtc"]?.ToString()),
                CompletedAtUtc = ParseDate(json["completedAtUtc"]?.ToString()),
                LastError = json["lastError"]?.ToString(),
                ReadFailedObjects = json["readFailedObjects"] is JArray arr
                    ? arr.Select(t => t.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>()
            };
            return state;
        }

        private static DateTime? ParseDate(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, out parsed) ? parsed : (DateTime?)null;
        }
    }

    /// <summary>
    /// Run clock behind the backfill ETA.  Tests inject a deterministic clock
    /// through the internal constructor; production uses a Stopwatch.
    /// </summary>
    internal interface ISourceStoreBackfillClock
    {
        TimeSpan Elapsed { get; }
        void Start();
        void Stop();
        void Reset();
    }

    internal sealed class StopwatchBackfillClock : ISourceStoreBackfillClock
    {
        private readonly Stopwatch _stopwatch = new Stopwatch();
        public TimeSpan Elapsed => _stopwatch.Elapsed;
        public void Start() => _stopwatch.Start();
        public void Stop() => _stopwatch.Stop();
        public void Reset() => _stopwatch.Reset();
    }

    /// <summary>
    /// Low-priority, resumable source-store backfill.  Each SDK-facing slice is
    /// submitted through the Worker's STA action queue and is bounded by both an
    /// object count and wall-clock budget; the queue is never held while waiting for
    /// the next slice.
    /// </summary>
    public sealed class SourceStoreBackfillService
    {
        private static readonly Lazy<SourceStoreBackfillService> _instance =
            new Lazy<SourceStoreBackfillService>(() => new SourceStoreBackfillService(SourceStoreService.Instance));
        public static SourceStoreBackfillService Instance => _instance.Value;

        private readonly SourceStoreService _store;
        private readonly Func<string, string, string, DateTime?, string, bool> _put;
        private readonly Func<Action, bool> _enqueueSdkAction;
        private readonly object _gate = new object();
        private readonly ISourceStoreBackfillClock _runClock;
        // ProcessedObjects when _runClock last started from zero.  The ETA rate is
        // measured only over the objects this clock actually timed, so a count
        // persisted before a restart cannot deflate it and a counter reset cannot
        // inflate it (issue #554).
        private int _runClockBaseline;
        private Func<IReadOnlyList<SearchIndex.IndexEntry>> _entryProvider;
        private Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> _partReader;
        // Production readers use this to distinguish "all required parts are
        // already fresh" (an empty result is valid) from "a stale required part
        // was dropped after a failed SDK read".
        private Func<SearchIndex.IndexEntry, IReadOnlyList<string>> _requiredPartProvider;
        private IReadOnlyList<SearchIndex.IndexEntry> _entries = Array.Empty<SearchIndex.IndexEntry>();
        private SourceStoreBackfillState _state = new SourceStoreBackfillState();
        private readonly Dictionary<string, int> _objectRetries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _scheduled;
        private bool _startedForCurrentIndex;
        // Bumped by every Start.  A slice captures the generation together with the
        // entry snapshot it walks; once a later Start has replaced that snapshot the
        // slice's cursor arithmetic refers to a list nobody resumes, so the slice
        // abandons instead of overwriting the restarted walk (issue #596).
        private int _walkGeneration;
        internal const int MaxTransientRetries = 5;

        public SourceStoreBackfillService(SourceStoreService store)
            : this(store, null, null)
        {
        }

        internal SourceStoreBackfillService(
            SourceStoreService store,
            Func<string, string, string, DateTime?, string, bool> put,
            Func<Action, bool> enqueueSdkAction,
            ISourceStoreBackfillClock runClock = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _put = put ?? store.Put;
            _enqueueSdkAction = enqueueSdkAction ?? (action => GxMcp.Worker.Program.EnqueueSdkAction(action, "source-store-backfill"));
            _runClock = runClock ?? new StopwatchBackfillClock();
            _state = LoadState();
        }

        public SourceStoreBackfillState GetState()
        {
            lock (_gate) return CloneState(_state);
        }

        public JObject GetStateJson()
        {
            var state = GetState();
            return state.ToJson(SourceStoreBackfillState.MaxReportedReadFailedObjects);
        }

        /// <summary>The store this backfill populates; the start path reads freshness through it.</summary>
        internal SourceStoreService Store => _store;

        /// <summary>Number of Start calls so far; test observability for the start guard.</summary>
        internal int WalkGenerationForTest
        {
            get { lock (_gate) return _walkGeneration; }
        }

        /// <summary>
        /// Starts (or resumes) backfill for the currently loaded index.  The callbacks
        /// are invoked only from an SDK STA slice, never from the caller's thread.
        /// </summary>
        public bool Start(
            Func<IReadOnlyList<SearchIndex.IndexEntry>> entryProvider,
            Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> partReader,
            int maxObjectsPerSlice = 50,
            int sliceBudgetMs = 250,
            bool schedule = true,
            Func<SearchIndex.IndexEntry, IReadOnlyList<string>> requiredPartProvider = null)
        {
            if (!Configuration.SourceStoreBackfillEnabled) return false;
            if (entryProvider == null || partReader == null) return false;

            lock (_gate)
            {
                _entryProvider = entryProvider;
                _partReader = partReader;
                _requiredPartProvider = requiredPartProvider;
                _entries = (entryProvider() ?? Array.Empty<SearchIndex.IndexEntry>())
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Guid))
                    .OrderByDescending(e => e.LastUpdate)
                    .ThenBy(e => EntryIdentity(e), StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly();
                _walkGeneration++;
                _state.TotalObjects = _entries.Count;
                string catalogSignature = BuildCatalogSignature(_entries);
                if (!string.IsNullOrWhiteSpace(_state.CatalogSignature)
                    && !string.Equals(_state.CatalogSignature, catalogSignature, StringComparison.Ordinal))
                {
                    _runClock.Reset();
                    _state.CursorIndex = 0;
                    _state.Cursor = null;
                    _state.ProcessedObjects = 0;
                    _state.StoredObjects = 0;
                    _state.StaleObjects = 0;
                    _state.RemovedObjects = 0;
                    _state.ReadFailedObjects.Clear();
                    _objectRetries.Clear();
                }
                _state.CatalogSignature = catalogSignature;
                if (_state.StartedAtUtc == null || _state.State == "off" || _state.State == "error")
                {
                    _state.StartedAtUtc = DateTime.UtcNow;
                    _state.RetryCount = 0;
                }
                if (!string.IsNullOrWhiteSpace(_state.Cursor))
                {
                    int cursorPosition = _entries.ToList().FindIndex(entry =>
                        string.Equals(EntryIdentity(entry), _state.Cursor, StringComparison.OrdinalIgnoreCase));
                    if (cursorPosition >= 0) _state.CursorIndex = cursorPosition + 1;
                }
                if (_state.CursorIndex >= _entries.Count && _state.CursorIndex > 0)
                {
                    // An exhausted cursor restarts the walk so stale parts get refreshed.
                    // Known read failures are kept: only a changed catalogue signature
                    // (above) re-tries a deterministically unreadable object.
                    _runClock.Reset();
                    _state.CursorIndex = 0;
                    _state.Cursor = null;
                    _state.ProcessedObjects = 0;
                    _state.StoredObjects = 0;
                    _state.StaleObjects = 0;
                    _state.RemovedObjects = 0;
                    _objectRetries.Clear();
                }
                _state.State = _state.CursorIndex >= _entries.Count
                    ? (_state.ReadFailedObjects.Count > 0 ? "completeWithFailures" : "complete")
                    : "queued";
                _state.UpdatedAtUtc = DateTime.UtcNow;
                _state.CompletedAtUtc = (_state.State == "complete" || _state.State == "completeWithFailures") ? DateTime.UtcNow : null;
                _startedForCurrentIndex = true;
                PersistStateLocked();
            }
            if (schedule) QueueNextSlice(maxObjectsPerSlice, sliceBudgetMs);
            return true;
        }

        public void Stop(string reason = null)
        {
            lock (_gate)
            {
                _state.State = string.IsNullOrWhiteSpace(reason) ? "off" : "paused";
                _state.LastError = reason;
                _state.UpdatedAtUtc = DateTime.UtcNow;
                _startedForCurrentIndex = false;
                PersistStateLocked();
            }
        }

        /// <summary>Runs exactly one slice synchronously; exposed for deterministic tests.</summary>
        public bool RunOneSliceForTest(int maxObjectsPerSlice = 50, int sliceBudgetMs = 250)
            => RunSlice(maxObjectsPerSlice, sliceBudgetMs, scheduleContinuation: false);

        private void QueueNextSlice(int maxObjectsPerSlice, int sliceBudgetMs)
        {
            if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
            Task.Run(() =>
            {
                bool retryRequested = false;
                try
                {
                    // Yield once so the SDK action queue can service interactive work
                    // before the next P2 slice is posted.
                    Thread.Sleep(1);
                    if (!_enqueueSdkAction(() => RunSlice(maxObjectsPerSlice, sliceBudgetMs, true)))
                    {
                        lock (_gate)
                        {
                            _state.State = "error";
                            _state.LastError = "SDK action queue rejected source-store backfill slice.";
                            _state.RetryCount++;
                            _state.UpdatedAtUtc = DateTime.UtcNow;
                            PersistStateLocked();
                        }
                        retryRequested = true;
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _state.State = "error";
                        _state.LastError = ex.Message;
                        _state.RetryCount++;
                        _state.UpdatedAtUtc = DateTime.UtcNow;
                        PersistStateLocked();
                    }
                    retryRequested = true;
                }
                finally
                {
                    Interlocked.Exchange(ref _scheduled, 0);
                }

                if (retryRequested) QueueRetrySlice(maxObjectsPerSlice, sliceBudgetMs);
            });
        }

        private void QueueRetrySlice(int maxObjectsPerSlice, int sliceBudgetMs)
        {
            int retry;
            lock (_gate)
            {
                if (_state.State != "error" || _state.RetryCount >= MaxTransientRetries) return;
                retry = _state.RetryCount;
            }
            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(Math.Min(2000, 100 * (retry + 1)));
                    QueueNextSlice(maxObjectsPerSlice, sliceBudgetMs);
                }
                catch (Exception ex)
                {
                    Logger.Warn("[SOURCE-STORE] retry scheduling failed: " + ex.Message);
                }
            });
        }

        private bool RunSlice(int maxObjectsPerSlice, int sliceBudgetMs, bool scheduleContinuation)
        {
            if (maxObjectsPerSlice <= 0) maxObjectsPerSlice = 50;
            if (sliceBudgetMs <= 0) sliceBudgetMs = 250;
            var watch = Stopwatch.StartNew();
            IReadOnlyList<SearchIndex.IndexEntry> entries;
            Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> reader;
            Func<SearchIndex.IndexEntry, IReadOnlyList<string>> requiredPartProvider;
            int start;
            int generation;
            lock (_gate)
            {
                if (!_startedForCurrentIndex || _entryProvider == null || _partReader == null) return false;
                entries = _entries;
                reader = _partReader;
                requiredPartProvider = _requiredPartProvider;
                generation = _walkGeneration;
                start = Math.Max(0, Math.Min(_state.CursorIndex, entries.Count));
                _state.State = "running";
                if (_runClock.Elapsed == TimeSpan.Zero) _runClockBaseline = _state.ProcessedObjects;
                _runClock.Start();
            }

            int processed = 0;
            bool sliceHadError = false;
            // Set when a later Start replaced the snapshot this slice walks.  Every
            // cursor/counter write below is relative to `entries`, so the slice stops
            // at the first stale observation and leaves the restarted walk untouched;
            // that Start has already queued its own slice.
            bool stale = false;
            try
            {
                for (int i = start; i < entries.Count && processed < maxObjectsPerSlice; i++)
                {
                    if (watch.ElapsedMilliseconds >= sliceBudgetMs) break;
                    var entry = entries[i];
                    bool knownReadFailure;
                    lock (_gate)
                    {
                        if (IsStaleSliceLocked(generation)) { stale = true; break; }
                        knownReadFailure = _state.ReadFailedObjects.Contains(entry.Guid, StringComparer.OrdinalIgnoreCase);
                        if (knownReadFailure)
                        {
                            _state.ProcessedObjects++;
                            _state.CursorIndex = i + 1;
                            _state.Cursor = EntryIdentity(entry);
                            _state.UpdatedAtUtc = DateTime.UtcNow;
                        }
                    }
                    if (knownReadFailure)
                    {
                        processed++;
                        continue;
                    }

                    IReadOnlyList<SourceStoreBackfillPart> parts = null;
                    string objectError = null;
                    var readWatch = Stopwatch.StartNew();
                    try { parts = reader(entry); }
                    catch (Exception ex)
                    {
                        objectError = "The SDK source-part reader failed for object '" + entry.Guid + "': " + ex.Message;
                    }
                    finally
                    {
                        if (readWatch.ElapsedMilliseconds >= 250)
                            Logger.Info("[SOURCE-STORE-SLOW-READ] actionId=" + SdkActionTrace.CurrentId
                                + " elapsedMs=" + readWatch.ElapsedMilliseconds);
                    }

                    if (objectError == null && parts == null)
                    {
                        objectError = "The SDK object could not be resolved for source-store backfill: " + entry.Guid;
                    }

                    if (objectError == null && parts.Count == 1 && parts[0].IsDeleted)
                    {
                        _store.RemoveObject(entry.Guid);
                        lock (_gate)
                        {
                            if (IsStaleSliceLocked(generation)) { stale = true; break; }
                            _state.RemovedObjects++;
                            _state.ProcessedObjects++;
                            _state.CursorIndex = i + 1;
                            _state.Cursor = EntryIdentity(entry);
                            _state.UpdatedAtUtc = DateTime.UtcNow;
                        }
                        processed++;
                        continue;
                    }

                    List<string> requiredParts = null;
                    if (objectError == null && requiredPartProvider != null)
                    {
                        try
                        {
                            var suppliedParts = requiredPartProvider(entry);
                            if (suppliedParts == null)
                                throw new InvalidOperationException("the required-part provider returned null");
                            requiredParts = NormalizePartNames(entry.Type, suppliedParts);
                        }
                        catch (Exception ex)
                        {
                            objectError = "The required source-part list could not be read for object '" + entry.Guid + "': " + ex.Message;
                        }
                    }

                    // Validate the complete read result before writing any part.  A
                    // failed/null part must never be silently omitted from a
                    // partially successful result.
                    var returnedParts = new Dictionary<string, SourceStoreBackfillPart>(StringComparer.OrdinalIgnoreCase);
                    if (objectError == null)
                    {
                        foreach (var part in parts)
                        {
                            if (part == null)
                            {
                                objectError = "The SDK returned a null source part for object '" + entry.Guid + "'.";
                                break;
                            }
                            string canonicalPart = NormalizePartName(entry.Type, part.PartName);
                            if (string.IsNullOrWhiteSpace(canonicalPart))
                            {
                                objectError = "The SDK returned a source part without a part name for object '" + entry.Guid + "'.";
                                break;
                            }
                            if (part.IsReadFailure)
                            {
                                objectError = "The SDK failed to read source part '"
                                    + CanonicalPartName(entry.Type, canonicalPart)
                                    + "' for object '" + entry.Guid + "': null source or read failure."
                                    + (string.IsNullOrWhiteSpace(part.ReadError) ? string.Empty : " " + part.ReadError);
                                break;
                            }
                            if (returnedParts.ContainsKey(canonicalPart))
                            {
                                objectError = "The SDK returned duplicate source part '"
                                    + CanonicalPartName(entry.Type, canonicalPart)
                                    + "' for object '" + entry.Guid + "'.";
                                break;
                            }
                            returnedParts.Add(canonicalPart, part);
                        }
                    }

                    bool anyStored = false;
                    bool anyStale = false;
                    if (objectError == null && requiredParts != null)
                    {
                        foreach (string requiredPart in requiredParts)
                        {
                            bool fresh = _store.IsStoredAndFresh(entry, new List<string> { requiredPart });
                            if (fresh) continue;
                            anyStale = true;
                            if (!returnedParts.ContainsKey(requiredPart))
                            {
                                objectError = "The SDK reader returned no evidence for required source part '"
                                    + CanonicalPartName(entry.Type, requiredPart)
                                    + "' of object '" + entry.Guid + "'.";
                                break;
                            }
                        }
                    }

                    if (objectError != null)
                    {
                        int attempt;
                        lock (_gate)
                        {
                            if (IsStaleSliceLocked(generation)) { stale = true; break; }
                            attempt = _objectRetries.TryGetValue(entry.Guid, out int count) ? count : 0;
                            if (attempt < 1) _objectRetries[entry.Guid] = attempt + 1;
                        }
                        if (attempt < 1)
                        {
                            // One probe retry per object. Only slice-level failures (SDK
                            // queue, store) consume the transient retry budget; otherwise
                            // a run of unreadable objects would wedge the walk in error.
                            if (SetSliceError(objectError, generation, consumesRetryBudget: false)) sliceHadError = true;
                            else stale = true;
                            break;
                        }
                        else
                        {
                            Logger.Warn("[SOURCE-STORE] " + objectError + " Advancing past failing object.");
                            lock (_gate)
                            {
                                if (IsStaleSliceLocked(generation)) { stale = true; break; }
                                if (!_state.ReadFailedObjects.Contains(entry.Guid, StringComparer.OrdinalIgnoreCase))
                                {
                                    _state.ReadFailedObjects.Add(entry.Guid);
                                }
                                _state.LastError = objectError;
                                _state.ProcessedObjects++;
                                _state.CursorIndex = i + 1;
                                _state.Cursor = EntryIdentity(entry);
                                _state.UpdatedAtUtc = DateTime.UtcNow;
                            }
                            processed++;
                            continue;
                        }
                    }

                    foreach (var pair in returnedParts)
                    {
                        var part = pair.Value;
                        bool fresh = _store.IsStoredAndFresh(entry, new List<string> { pair.Key });
                        if (!fresh) anyStale = true;
                        if (!_put(
                            entry.Guid,
                            CanonicalPartName(entry.Type, pair.Key),
                            part.Source,
                            part.LastUpdate ?? entry.LastUpdate,
                            part.VersionToken))
                        {
                            if (SetSliceError("Source-store rejected source part '"
                                + CanonicalPartName(entry.Type, pair.Key)
                                + "' for object '" + entry.Guid + "'.", generation)) sliceHadError = true;
                            else stale = true;
                            break;
                        }
                        anyStored = true;
                    }
                    if (sliceHadError || stale) break;

                    // A successful Put is not enough to certify coverage.  Read the
                    // actual per-part records back so a missing/corrupt file or a
                    // write that silently dropped an empty part cannot advance the
                    // resumable cursor.
                    var certifyParts = requiredParts ?? returnedParts.Keys.ToList();
                    foreach (string partName in certifyParts)
                    {
                        if (_store.IsStoredAndFresh(entry, new List<string> { partName }))
                            continue;
                        if (SetSliceError("Source-store certification failed for part '"
                            + CanonicalPartName(entry.Type, partName)
                            + "' of object '" + entry.Guid + "'.", generation)) sliceHadError = true;
                        else stale = true;
                        break;
                    }
                    if (sliceHadError || stale) break;

                    processed++;
                    lock (_gate)
                    {
                        if (IsStaleSliceLocked(generation)) { stale = true; break; }
                        _state.CursorIndex = i + 1;
                        _state.Cursor = EntryIdentity(entry);
                        _state.ProcessedObjects++;
                        if (anyStored) _state.StoredObjects++;
                        if (anyStale) _state.StaleObjects++;
                        _state.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }

                if (stale) return false;

                if (sliceHadError)
                {
                    if (scheduleContinuation) QueueRetrySlice(maxObjectsPerSlice, sliceBudgetMs);
                    return false;
                }

                // Every object is certified before its cursor advances (see the
                // per-part read-back above).  Do not perform an unbounded second
                // catalogue walk here: this method is the bounded STA slice.
                bool complete;
                lock (_gate)
                {
                    if (IsStaleSliceLocked(generation)) return false;
                    _state.RetryCount = 0;
                    _state.LastError = null;
                    complete = _state.CursorIndex >= entries.Count;
                    _state.State = complete
                        ? (_state.ReadFailedObjects.Count > 0 ? "completeWithFailures" : "complete")
                        : "running";
                    if (complete)
                    {
                        _state.CompletedAtUtc = DateTime.UtcNow;
                        _state.EtaMs = 0;
                    }
                    else
                    {
                        int remaining = Math.Max(0, entries.Count - _state.CursorIndex);
                        int timedObjects = _state.ProcessedObjects - _runClockBaseline;
                        double perObject = timedObjects <= 0 ? 0 : _runClock.Elapsed.TotalMilliseconds / timedObjects;
                        _state.EtaMs = (int)Math.Max(0, Math.Min(int.MaxValue, perObject * remaining));
                    }
                    PersistStateLocked();
                }
                if (!complete && scheduleContinuation) QueueNextSlice(maxObjectsPerSlice, sliceBudgetMs);
                return true;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (IsStaleSliceLocked(generation)) return false;
                    _state.State = "error";
                    _state.LastError = ex.Message;
                    _state.RetryCount++;
                    _state.UpdatedAtUtc = DateTime.UtcNow;
                    PersistStateLocked();
                }
                return false;
            }
            finally
            {
                _runClock.Stop();
                watch.Stop();
            }
        }

        /// <summary>
        /// True when a later Start replaced the entry snapshot a slice captured at
        /// <paramref name="generation"/>.  Callers hold <see cref="_gate"/>.
        /// </summary>
        private bool IsStaleSliceLocked(int generation) => _walkGeneration != generation;

        /// <summary>
        /// Records a slice-level error.  Returns false, leaving the state untouched,
        /// when the slice belongs to a replaced walk: its error describes a snapshot
        /// nobody resumes and must not wedge the restarted walk in <c>error</c>.
        /// </summary>
        private bool SetSliceError(string message, int generation, bool consumesRetryBudget = true)
        {
            lock (_gate)
            {
                if (IsStaleSliceLocked(generation)) return false;
                _state.LastError = string.IsNullOrWhiteSpace(message)
                    ? "The SDK source-part reader failed during source-store backfill."
                    : message;
                _state.State = "error";
                if (consumesRetryBudget) _state.RetryCount++;
                _state.CompletedAtUtc = null;
                _state.UpdatedAtUtc = DateTime.UtcNow;
                PersistStateLocked();
                return true;
            }
        }

        private static string NormalizePartName(string type, string partName)
        {
            string resolved = ObjectService.ResolveSearchPartName(type, partName);
            return ObjectService.NormalizeRawSourcePart(resolved);
        }

        private static string CanonicalPartName(string type, string partName)
        {
            string normalized = NormalizePartName(type, partName);
            switch (normalized)
            {
                case "source": return "Source";
                case "events": return "Events";
                case "rules": return "Rules";
                case "conditions": return "Conditions";
                case "webform": return "WebForm";
                case "layout": return "Layout";
                default: return ObjectService.ResolveSearchPartName(type, partName) ?? partName;
            }
        }

        private static List<string> NormalizePartNames(string type, IEnumerable<string> partNames)
        {
            var supplied = (partNames ?? Enumerable.Empty<string>()).ToList();
            if (supplied.Any(part => string.IsNullOrWhiteSpace(part)))
                throw new InvalidOperationException("the required-part list contains an empty part name");
            return supplied
                .Select(part => NormalizePartName(type, part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string EntryIdentity(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;
            return !string.IsNullOrWhiteSpace(entry.StorageKey) ? entry.StorageKey : entry.Guid;
        }

        /// <summary>
        /// Signature of the catalogue a Start would walk for <paramref name="index"/>:
        /// the same identity filter Start applies, so the value is comparable with
        /// <see cref="SourceStoreBackfillState.CatalogSignature"/>.  Null without objects.
        /// </summary>
        internal static string BuildCatalogSignature(SearchIndex index)
        {
            if (index?.Objects == null) return null;
            return BuildCatalogSignature(index.Objects.Values
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Guid)));
        }

        internal static string BuildCatalogSignature(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            string canonical = string.Join("|", (entries ?? Array.Empty<SearchIndex.IndexEntry>())
                .Select(EntryIdentity)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private SourceStoreBackfillState LoadState()
        {
            try
            {
                string path = StatePath;
                if (!File.Exists(path)) return new SourceStoreBackfillState();
                return SourceStoreBackfillState.FromJson(JObject.Parse(File.ReadAllText(path)));
            }
            catch { return new SourceStoreBackfillState(); }
        }

        private string StatePath => Path.Combine(_store.StoreDirectory, "backfill-state.json");

        private void PersistStateLocked()
        {
            try
            {
                Directory.CreateDirectory(_store.StoreDirectory);
                string path = StatePath;
                string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tmp, _state.ToJson().ToString(Formatting.None));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Logger.Warn("[SOURCE-STORE] backfill state persistence failed: " + ex.Message);
            }
        }

        private static SourceStoreBackfillState CloneState(SourceStoreBackfillState state)
            => SourceStoreBackfillState.FromJson(state.ToJson());
    }
}
