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
    }

    /// <summary>Persisted, resumable source-store population state.</summary>
    public sealed class SourceStoreBackfillState
    {
        public string State { get; set; } = "off";
        public string Cursor { get; set; }
        public string CatalogSignature { get; set; }
        public int CursorIndex { get; set; }
        public int TotalObjects { get; set; }
        public int ProcessedObjects { get; set; }
        public int StoredObjects { get; set; }
        public int StaleObjects { get; set; }
        public int RetryCount { get; set; }
        public int? EtaMs { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string LastError { get; set; }

        public JObject ToJson()
        {
            return new JObject
            {
                ["state"] = State,
                ["cursor"] = Cursor,
                ["catalogSignature"] = CatalogSignature,
                ["cursorIndex"] = CursorIndex,
                ["totalObjects"] = TotalObjects,
                ["processedObjects"] = ProcessedObjects,
                ["storedObjects"] = StoredObjects,
                ["staleObjects"] = StaleObjects,
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
            return new SourceStoreBackfillState
            {
                State = json["state"]?.ToString() ?? "off",
                Cursor = json["cursor"]?.ToString(),
                CatalogSignature = json["catalogSignature"]?.ToString(),
                CursorIndex = json["cursorIndex"]?.ToObject<int>() ?? 0,
                TotalObjects = json["totalObjects"]?.ToObject<int>() ?? 0,
                ProcessedObjects = json["processedObjects"]?.ToObject<int>() ?? 0,
                StoredObjects = json["storedObjects"]?.ToObject<int>() ?? 0,
                StaleObjects = json["staleObjects"]?.ToObject<int>() ?? 0,
                RetryCount = json["retryCount"]?.ToObject<int>() ?? 0,
                EtaMs = json["etaMs"]?.Type == JTokenType.Integer ? json["etaMs"].ToObject<int>() : (int?)null,
                StartedAtUtc = ParseDate(json["startedAtUtc"]?.ToString()),
                UpdatedAtUtc = ParseDate(json["updatedAtUtc"]?.ToString()),
                CompletedAtUtc = ParseDate(json["completedAtUtc"]?.ToString()),
                LastError = json["lastError"]?.ToString()
            };
        }

        private static DateTime? ParseDate(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, out parsed) ? parsed : (DateTime?)null;
        }
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
        private readonly Stopwatch _runClock = new Stopwatch();
        private Func<IReadOnlyList<SearchIndex.IndexEntry>> _entryProvider;
        private Func<SearchIndex.IndexEntry, IReadOnlyList<SourceStoreBackfillPart>> _partReader;
        // Production readers use this to distinguish "all required parts are
        // already fresh" (an empty result is valid) from "a stale required part
        // was dropped after a failed SDK read".
        private Func<SearchIndex.IndexEntry, IReadOnlyList<string>> _requiredPartProvider;
        private IReadOnlyList<SearchIndex.IndexEntry> _entries = Array.Empty<SearchIndex.IndexEntry>();
        private SourceStoreBackfillState _state = new SourceStoreBackfillState();
        private int _scheduled;
        private bool _startedForCurrentIndex;
        private const int MaxTransientRetries = 5;

        public SourceStoreBackfillService(SourceStoreService store)
            : this(store, null, null)
        {
        }

        internal SourceStoreBackfillService(
            SourceStoreService store,
            Func<string, string, string, DateTime?, string, bool> put,
            Func<Action, bool> enqueueSdkAction)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _put = put ?? store.Put;
            _enqueueSdkAction = enqueueSdkAction ?? GxMcp.Worker.Program.EnqueueSdkAction;
            _state = LoadState();
        }

        public SourceStoreBackfillState GetState()
        {
            lock (_gate) return CloneState(_state);
        }

        public JObject GetStateJson()
        {
            var state = GetState();
            return state.ToJson();
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
                _state.TotalObjects = _entries.Count;
                string catalogSignature = BuildCatalogSignature(_entries);
                if (!string.IsNullOrWhiteSpace(_state.CatalogSignature)
                    && !string.Equals(_state.CatalogSignature, catalogSignature, StringComparison.Ordinal))
                {
                    _state.CursorIndex = 0;
                    _state.Cursor = null;
                    _state.ProcessedObjects = 0;
                    _state.StoredObjects = 0;
                    _state.StaleObjects = 0;
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
                    // A changed catalogue invalidates a previously exhausted cursor.
                    _state.CursorIndex = 0;
                    _state.Cursor = null;
                    _state.ProcessedObjects = 0;
                    _state.StoredObjects = 0;
                    _state.StaleObjects = 0;
                }
                _state.State = _state.CursorIndex >= _entries.Count ? "complete" : "queued";
                _state.UpdatedAtUtc = DateTime.UtcNow;
                _state.CompletedAtUtc = _state.State == "complete" ? DateTime.UtcNow : null;
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
            lock (_gate)
            {
                if (!_startedForCurrentIndex || _entryProvider == null || _partReader == null) return false;
                entries = _entries;
                reader = _partReader;
                requiredPartProvider = _requiredPartProvider;
                start = Math.Max(0, Math.Min(_state.CursorIndex, entries.Count));
                _state.State = "running";
                _runClock.Start();
            }

            int processed = 0;
            int stored = 0;
            int stale = 0;
            bool sliceHadError = false;
            try
            {
                for (int i = start; i < entries.Count && processed < maxObjectsPerSlice; i++)
                {
                    if (watch.ElapsedMilliseconds >= sliceBudgetMs) break;
                    var entry = entries[i];
                    IReadOnlyList<SourceStoreBackfillPart> parts = null;
                    try { parts = reader(entry); }
                    catch (Exception ex)
                    {
                        SetSliceError("The SDK source-part reader failed for object '" + entry.Guid + "': " + ex.Message);
                        sliceHadError = true;
                        break;
                    }

                    if (parts == null)
                    {
                        SetSliceError("The SDK object could not be resolved for source-store backfill: " + entry.Guid);
                        sliceHadError = true;
                        break;
                    }

                    List<string> requiredParts = null;
                    if (requiredPartProvider != null)
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
                            SetSliceError("The required source-part list could not be read for object '" + entry.Guid + "': " + ex.Message);
                            sliceHadError = true;
                            break;
                        }
                    }

                    // Validate the complete read result before writing any part.  A
                    // failed/null part must never be silently omitted from a
                    // partially successful result.
                    var returnedParts = new Dictionary<string, SourceStoreBackfillPart>(StringComparer.OrdinalIgnoreCase);
                    string readFailure = null;
                    foreach (var part in parts)
                    {
                        if (part == null)
                        {
                            readFailure = "The SDK returned a null source part for object '" + entry.Guid + "'.";
                            break;
                        }
                        string canonicalPart = NormalizePartName(entry.Type, part.PartName);
                        if (string.IsNullOrWhiteSpace(canonicalPart))
                        {
                            readFailure = "The SDK returned a source part without a part name for object '" + entry.Guid + "'.";
                            break;
                        }
                        if (part.IsReadFailure)
                        {
                            readFailure = "The SDK failed to read source part '"
                                + CanonicalPartName(entry.Type, canonicalPart)
                                + "' for object '" + entry.Guid + "': null source or read failure."
                                + (string.IsNullOrWhiteSpace(part.ReadError) ? string.Empty : " " + part.ReadError);
                            break;
                        }
                        if (returnedParts.ContainsKey(canonicalPart))
                        {
                            readFailure = "The SDK returned duplicate source part '"
                                + CanonicalPartName(entry.Type, canonicalPart)
                                + "' for object '" + entry.Guid + "'.";
                            break;
                        }
                        returnedParts.Add(canonicalPart, part);
                    }
                    if (readFailure != null)
                    {
                        SetSliceError(readFailure);
                        sliceHadError = true;
                        break;
                    }

                    bool anyStored = false;
                    bool anyStale = false;
                    if (requiredParts != null)
                    {
                        foreach (string requiredPart in requiredParts)
                        {
                            bool fresh = _store.IsStoredAndFresh(entry, new List<string> { requiredPart });
                            if (fresh) continue;
                            anyStale = true;
                            if (!returnedParts.ContainsKey(requiredPart))
                            {
                                readFailure = "The SDK reader returned no evidence for required source part '"
                                    + CanonicalPartName(entry.Type, requiredPart)
                                    + "' of object '" + entry.Guid + "'.";
                                break;
                            }
                        }
                    }
                    if (readFailure != null)
                    {
                        SetSliceError(readFailure);
                        sliceHadError = true;
                        break;
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
                            SetSliceError("Source-store rejected source part '"
                                + CanonicalPartName(entry.Type, pair.Key)
                                + "' for object '" + entry.Guid + "'.");
                            sliceHadError = true;
                            break;
                        }
                        anyStored = true;
                    }
                    if (sliceHadError) break;

                    // A successful Put is not enough to certify coverage.  Read the
                    // actual per-part records back so a missing/corrupt file or a
                    // write that silently dropped an empty part cannot advance the
                    // resumable cursor.
                    var certifyParts = requiredParts ?? returnedParts.Keys.ToList();
                    foreach (string partName in certifyParts)
                    {
                        if (_store.IsStoredAndFresh(entry, new List<string> { partName }))
                            continue;
                        SetSliceError("Source-store certification failed for part '"
                            + CanonicalPartName(entry.Type, partName)
                            + "' of object '" + entry.Guid + "'.");
                        sliceHadError = true;
                        break;
                    }
                    if (sliceHadError) break;

                    if (anyStored) stored++;
                    if (anyStale) stale++;
                    processed++;
                    lock (_gate)
                    {
                        _state.CursorIndex = i + 1;
                        _state.Cursor = EntryIdentity(entry);
                        _state.ProcessedObjects += processed;
                        _state.StoredObjects += stored;
                        _state.StaleObjects += stale;
                        _state.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }

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
                    _state.RetryCount = 0;
                    _state.LastError = null;
                    complete = _state.CursorIndex >= entries.Count;
                    _state.State = complete ? "complete" : "running";
                    if (complete)
                    {
                        _state.CompletedAtUtc = DateTime.UtcNow;
                        _state.EtaMs = 0;
                    }
                    else
                    {
                        int remaining = Math.Max(0, entries.Count - _state.CursorIndex);
                        double perObject = _state.ProcessedObjects <= 0 ? 0 : _runClock.Elapsed.TotalMilliseconds / _state.ProcessedObjects;
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

        private void SetSliceError(string message)
        {
            lock (_gate)
            {
                _state.LastError = string.IsNullOrWhiteSpace(message)
                    ? "The SDK source-part reader failed during source-store backfill."
                    : message;
                _state.State = "error";
                _state.RetryCount++;
                _state.CompletedAtUtc = null;
                _state.UpdatedAtUtc = DateTime.UtcNow;
                PersistStateLocked();
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

        private static string BuildCatalogSignature(IEnumerable<SearchIndex.IndexEntry> entries)
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
