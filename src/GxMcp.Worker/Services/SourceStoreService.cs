using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class SourceStoreCoverage
    {
        public int StoredObjects { get; set; }
        public int StaleObjects { get; set; }
        public int TotalObjects { get; set; }

        // Aggregate coverage is intentionally retained for the existing search
        // contract, while the per-part projection makes mixed scopes diagnosable.
        // A part is considered stored only when its record is present and fresh.
        public Dictionary<string, SourceStoreCoverage> PartsByPart { get; }
            = new Dictionary<string, SourceStoreCoverage>(StringComparer.OrdinalIgnoreCase);

        public JObject ToJson()
        {
            var result = new JObject
            {
                ["storedObjects"] = StoredObjects,
                ["staleObjects"] = StaleObjects,
                ["totalObjects"] = TotalObjects,
                ["parts"] = new JObject()
            };
            var parts = (JObject)result["parts"];
            foreach (var part in PartsByPart)
                parts[part.Key] = part.Value.ToJson();
            return result;
        }
    }

    public class SourceStoreService
    {
        private static readonly Lazy<SourceStoreService> _instance =
            new Lazy<SourceStoreService>(() => new SourceStoreService());

        public static SourceStoreService Instance => _instance.Value;

        public class RecordSummary
        {
            internal int RecordId { get; set; } = -1;
            public string Guid { get; set; }
            public string PartName { get; set; }
            public DateTime? LastUpdate { get; set; }
            public string VersionToken { get; set; }
            public string ContentHash { get; set; }
            public string RelativeFilePath { get; set; }
            public long FileBytes { get; set; }
            public DateTime StoredAtUtc { get; set; }
        }

        internal sealed class PostingList
        {
            private int[] _ids;
            private int _count;

            public PostingList()
            {
                _ids = new int[4];
                _count = 0;
            }

            public PostingList(int[] sortedIds)
            {
                _ids = sortedIds ?? Array.Empty<int>();
                _count = _ids.Length;
            }

            public int Count
            {
                get { lock (this) return _count; }
            }

            public void Add(int id)
            {
                lock (this)
                {
                    int idx = Array.BinarySearch(_ids, 0, _count, id);
                    if (idx >= 0) return;
                    int insertPos = ~idx;
                    if (_count == _ids.Length)
                    {
                        int newCap = _ids.Length == 0 ? 4 : (_ids.Length < 64 ? _ids.Length * 2 : _ids.Length + (_ids.Length >> 1));
                        var next = new int[newCap];
                        if (_count > 0) Array.Copy(_ids, 0, next, 0, _count);
                        _ids = next;
                    }
                    if (insertPos < _count)
                    {
                        Array.Copy(_ids, insertPos, _ids, insertPos + 1, _count - insertPos);
                    }
                    _ids[insertPos] = id;
                    _count++;
                }
            }

            public bool Remove(int id)
            {
                lock (this)
                {
                    int idx = Array.BinarySearch(_ids, 0, _count, id);
                    if (idx < 0) return _count == 0;
                    _count--;
                    if (idx < _count)
                    {
                        Array.Copy(_ids, idx + 1, _ids, idx, _count - idx);
                    }
                    if (_count > 0 && _ids.Length > 8 && _count <= _ids.Length / 4)
                    {
                        var shrunk = new int[Math.Max(4, _count * 2)];
                        Array.Copy(_ids, 0, shrunk, 0, _count);
                        _ids = shrunk;
                    }
                    return _count == 0;
                }
            }

            public int[] SnapshotIds()
            {
                lock (this)
                {
                    if (_count == 0) return Array.Empty<int>();
                    var copy = new int[_count];
                    Array.Copy(_ids, 0, copy, 0, _count);
                    return copy;
                }
            }

            public void IntersectInto(List<int> candidates)
            {
                lock (this)
                {
                    if (_count == 0)
                    {
                        candidates.Clear();
                        return;
                    }
                    int write = 0;
                    int i = 0;
                    int j = 0;
                    int cCount = candidates.Count;
                    while (i < cCount && j < _count)
                    {
                        int a = candidates[i];
                        int b = _ids[j];
                        if (a == b)
                        {
                            candidates[write++] = a;
                            i++;
                            j++;
                        }
                        else if (a < b)
                        {
                            i++;
                        }
                        else
                        {
                            j++;
                        }
                    }
                    if (write < cCount)
                    {
                        candidates.RemoveRange(write, cCount - write);
                    }
                }
            }
        }

        private enum RecordReadState
        {
            Valid,
            Missing,
            Invalid
        }

        private string _storeDirectory;
        private readonly ConcurrentDictionary<string, RecordSummary> _records =
            new ConcurrentDictionary<string, RecordSummary>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<ulong, PostingList> _trigramIndex =
            new ConcurrentDictionary<ulong, PostingList>();

        private readonly object _idGate = new object();
        private RecordSummary[] _recordsById = Array.Empty<RecordSummary>();
        private string[] _idToKey = Array.Empty<string>();
        private int _nextRecordId;
        private readonly Stack<int> _freeRecordIds = new Stack<int>();

        private readonly object _ioGate = new object();
        private readonly object _flushGate = new object();
        private readonly object _initGate = new object();
        private Timer _flushTimer;
        private bool _isCatalogDirty;
        private volatile bool _initialized;

        // Issue #338. Storage accounting.
        //
        // EnforceStorageBudget used to sum FileBytes across every catalog record to
        // decide whether the budget was exceeded, so it walked the whole catalog on
        // every single insert. Populating a KB with S stored sources cost O(S^2)
        // accounting iterations below the budget - quadratic work whose only purpose
        // is to read a total it already knows.
        //
        // _trackedBytes holds that total, and _accountingTrusted says whether it can be
        // believed. A replace applies only the delta, eviction subtracts, and the
        // counter stops being trusted the moment an update path could have desynced it
        // (a failed write, a concurrent mutation). The next enforcement then rebuilds
        // it with one pass and carries on incrementally, so the invariant is
        // "reconcile at most once per desync", not "assume the counter is always
        // right" and not "rescan on every insert".
        private long _trackedBytes;
        private bool _accountingTrusted;
        private long _accountingScans;

        // Issue #363: files whose delete failed during eviction. They are no longer in the
        // catalog and no longer counted, so they are invisible to every budget decision;
        // the count is here so a stuck file is at least reportable.
        private long _orphanedFileCount;

        // Issue #344. Trigram posting reclamation.
        //
        // Put added memberships for the new source and nothing else, so replacing a
        // source with one whose trigrams barely overlap left the old trigrams still
        // pointing at that record key - stale derived state that grows with every
        // source churn. Disk-budget eviction dropped the catalog record and the file
        // but not the postings, so evicted records stayed reachable through the index.
        //
        // Each record's current trigram set is remembered so the previous one can be
        // subtracted on replacement, and postings are removed on eviction as well. An
        // empty posting set is dropped from the index rather than kept, since a set with
        // no members answers nothing and only retains the key strings.
        //
        // Concurrency: a query that is mid-scan may still observe an id that is being
        // removed. That is the safe direction - a candidate whose record is gone is
        // re-checked against the record dictionary before it is used, so a stale
        // posting costs a miss, never a wrong result. The reverse (removing a posting
        // that a concurrent write just added) is prevented by removing under the same
        // per-set lock the writer uses.
        private readonly ConcurrentDictionary<int, ulong[]> _trigramsByRecord =
            new ConcurrentDictionary<int, ulong[]>();

        private int AssignRecordId(string key, RecordSummary summary)
        {
            lock (_idGate)
            {
                if (_records.TryGetValue(key, out var existing) && existing != null && existing.RecordId >= 0)
                {
                    int existingId = existing.RecordId;
                    summary.RecordId = existingId;
                    _recordsById[existingId] = summary;
                    _idToKey[existingId] = key;
                    return existingId;
                }

                int id = _freeRecordIds.Count > 0 ? _freeRecordIds.Pop() : _nextRecordId++;
                if (id >= _recordsById.Length)
                {
                    int newLen = Math.Max(64, _recordsById.Length * 2);
                    while (newLen <= id) newLen *= 2;
                    var nextRecs = new RecordSummary[newLen];
                    var nextKeys = new string[newLen];
                    Array.Copy(_recordsById, nextRecs, _recordsById.Length);
                    Array.Copy(_idToKey, nextKeys, _idToKey.Length);
                    Volatile.Write(ref _recordsById, nextRecs);
                    Volatile.Write(ref _idToKey, nextKeys);
                }
                summary.RecordId = id;
                _recordsById[id] = summary;
                _idToKey[id] = key;
                return id;
            }
        }

        private void ReleaseRecordId(int id)
        {
            if (id < 0) return;
            lock (_idGate)
            {
                if (id < _recordsById.Length)
                {
                    _recordsById[id] = null;
                    _idToKey[id] = null;
                    _freeRecordIds.Push(id);
                }
            }
        }

        private void ClearRecordIds()
        {
            lock (_idGate)
            {
                _recordsById = Array.Empty<RecordSummary>();
                _idToKey = Array.Empty<string>();
                _nextRecordId = 0;
                _freeRecordIds.Clear();
            }
        }

        private string GetKeyById(int id)
        {
            var arr = Volatile.Read(ref _idToKey);
            return (uint)id < (uint)arr.Length ? arr[id] : null;
        }

        /// <summary>Record keys currently reachable through at least one trigram posting.</summary>
        internal int TrigramIndexedRecordCount => _trigramsByRecord.Count;

        /// <summary>Trigram keys currently held in the index, including any that became empty.</summary>
        internal int TrigramKeyCount => _trigramIndex.Count;

        /// <summary>The trigrams currently indexed for a record. Diagnostics and tests.</summary>
        internal HashSet<string> TrigramsForRecordForTest(string guid, string partName)
        {
            string key = MakeKey(guid, partName);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_records.TryGetValue(key, out var rec) && rec != null && rec.RecordId >= 0
                && _trigramsByRecord.TryGetValue(rec.RecordId, out var packed) && packed != null)
            {
                foreach (ulong t in packed) result.Add(TrigramExtractor.UnpackTrigram(t));
            }
            return result;
        }

        /// <summary>
        /// A snapshot of every trigram posting set. Issue #344: the index is only
        /// reclaimable if a posting naming a dead record is observable, and the only
        /// honest way to assert that is to look at the postings themselves rather than
        /// at a counter that could agree with a leak. The sets are copied so a caller
        /// cannot mutate live index state.
        /// </summary>
        internal IDictionary<string, HashSet<string>> TrigramIndexForTest()
        {
            var snapshot = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _trigramIndex)
            {
                int[] ids = kvp.Value.SnapshotIds();
                if (ids.Length == 0) continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (int id in ids)
                {
                    string k = GetKeyById(id);
                    if (k != null) set.Add(k);
                }
                snapshot[TrigramExtractor.UnpackTrigram(kvp.Key)] = set;
            }
            return snapshot;
        }

        /// <summary>
        /// Whether a posting key still names a live catalog record. A posting that does
        /// not is stale: the query path scans it and then discards it, so every one is
        /// wasted work plus retained memory.
        /// </summary>
        internal bool RecordExistsForTest(string recordKey)
        {
            if (recordKey == null) return false;
            return _records.ContainsKey(recordKey);
        }

        public SourceStoreService()
        {
            _storeDirectory = RuntimePaths.DurableSourceStoreRoot;
            Initialize();
        }

        public void SetStoreDirectoryForTest(string dir)
        {
            lock (_ioGate)
            {
                lock (_flushGate)
                {
                    _flushTimer?.Dispose();
                    _flushTimer = null;
                    _isCatalogDirty = false;
                }
                _storeDirectory = dir;
                _records.Clear();
                _trigramIndex.Clear();
                // Issue #344: the per-record trigram sets describe the index that was
                // just cleared. Leaving them behind would make the next Put believe the
                // new content's trigrams were the previous content's, and subtract the
                // wrong postings instead of removing the right ones.
                _trigramsByRecord.Clear();
                ClearRecordIds();
                // Issue #339: same for the freshness certifications - they describe the
                // store that was just reset.
                _certifications.Clear();
                _initialized = false;
                // Issue #338: clearing the catalog invalidates the incremental total.
                // Without this the next Put would add its delta to a counter still
                // describing the previous record set, and the total would be wrong for
                // the rest of the instance's life.
                MarkAccountingUntrusted();
                Initialize();
            }
        }

        public string StoreDirectory => _storeDirectory;
        public int StoredRecordCount => _records.Count;

        /// <summary>
        /// Removes all stored parts for the specified object GUID from catalog, trigram postings, certifications, and disk.
        /// </summary>
        public int RemoveObject(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return 0;
            string targetGuid = guid.Trim().ToLowerInvariant();
            int removedCount = 0;

            var keysToRemove = _records.Keys
                .Where(k => k.StartsWith(targetGuid + ":", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keysToRemove)
            {
                if (_records.TryRemove(key, out var rec))
                {
                    RemoveTrigramPostings(rec.RecordId);
                    ReleaseRecordId(rec.RecordId);
                    _certifications.TryRemove(key, out _);
                    ApplyAccountingDelta(-rec.FileBytes);
                    try
                    {
                        string fullPath = Path.Combine(_storeDirectory, rec.RelativeFilePath);
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                    }
                    catch
                    {
                        Interlocked.Increment(ref _orphanedFileCount);
                    }
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                MarkCatalogDirty();
            }
            return removedCount;
        }

        /// <summary>
        /// Cleans up stale process-scoped stores from previous gateway/worker lifetimes.
        /// </summary>
        public static int TryCleanupOrphanedStores(string currentStateDir = null, TimeSpan? olderThan = null)
        {
            int cleaned = 0;
            try
            {
                TimeSpan threshold = olderThan ?? TimeSpan.FromHours(1);
                string currentStoreDir = currentStateDir ?? RuntimePaths.DurableSourceStoreRoot;
                string stateBase = Path.GetDirectoryName(Path.GetDirectoryName(currentStoreDir));
                if (string.IsNullOrWhiteSpace(stateBase) || !Directory.Exists(stateBase))
                    return 0;

                DateTime cutoff = DateTime.UtcNow - threshold;
                foreach (string candidateDir in Directory.GetDirectories(stateBase))
                {
                    try
                    {
                        string candidateStore = Path.Combine(candidateDir, "source-store");
                        if (!Directory.Exists(candidateStore)) continue;
                        if (string.Equals(Path.GetFullPath(candidateStore), Path.GetFullPath(currentStoreDir), StringComparison.OrdinalIgnoreCase))
                            continue;

                        DirectoryInfo di = new DirectoryInfo(candidateStore);
                        if (di.LastWriteTimeUtc < cutoff)
                        {
                            Directory.Delete(candidateStore, recursive: true);
                            cleaned++;
                            if (Directory.GetFileSystemEntries(candidateDir).Length == 0)
                            {
                                Directory.Delete(candidateDir, recursive: false);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore transient access / lock errors
                    }
                }
            }
            catch
            {
                // Fail-safe
            }
            return cleaned;
        }

        private void Initialize()
        {
            if (_initialized) return;
            lock (_initGate)
            {
                if (_initialized) return;
                try
                {
                    if (!Directory.Exists(_storeDirectory))
                    {
                        Directory.CreateDirectory(_storeDirectory);
                    }
                    _initialized = true;
                    LoadCatalog();
                    TryCleanupOrphanedStores();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SOURCE-STORE] Failed to initialize source store at {_storeDirectory}: {ex.Message}");
                }
            }
        }

        private static string MakeKey(string guid, string partName)
        {
            return $"{guid?.Trim().ToLowerInvariant()}:{partName?.Trim().ToLowerInvariant()}";
        }

        private bool TryReadStoredRecord(string guid, string partName,
            out string source, out RecordReadState state, out string detail)
        {
            source = null;
            state = RecordReadState.Invalid;
            detail = null;
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(partName))
            {
                detail = "The source-store record key is incomplete.";
                return false;
            }

            Initialize();
            string normalizedGuid = guid.Trim();
            string normalizedPart = ObjectService.NormalizeRawSourcePart(partName);
            string key = MakeKey(normalizedGuid, normalizedPart);
            if (!_records.TryGetValue(key, out var summary) || summary == null)
            {
                state = RecordReadState.Missing;
                detail = "The source-store catalog has no record for this part.";
                return false;
            }

            if (!string.Equals(summary.Guid?.Trim(), normalizedGuid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ObjectService.NormalizeRawSourcePart(summary.PartName), normalizedPart,
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(summary.RelativeFilePath)
                || string.IsNullOrWhiteSpace(summary.ContentHash))
            {
                detail = "The source-store catalog summary is incomplete or belongs to another part.";
                return false;
            }

            string fullPath;
            try
            {
                string root = Path.GetFullPath(_storeDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(root, summary.RelativeFilePath));
                string rootPrefix = root + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    detail = "The source-store record path escapes the store directory.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = "The source-store record path is invalid: " + ex.Message;
                return false;
            }

            if (!File.Exists(fullPath))
            {
                state = RecordReadState.Missing;
                detail = "The source-store record file is missing.";
                return false;
            }

            try
            {
                string payloadGuid = null;
                string payloadPart = null;
                string payloadHash = null;
                bool hasSource = false;
                bool hasLastUpdate = false;
                DateTime? payloadLastUpdate = null;

                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                using (var jr = new JsonTextReader(reader) { DateParseHandling = DateParseHandling.DateTime })
                {
                    if (!jr.Read() || jr.TokenType != JsonToken.StartObject)
                    {
                        detail = "The source-store record is not a JSON object.";
                        return false;
                    }
                    while (jr.Read())
                    {
                        if (jr.TokenType == JsonToken.EndObject) break;
                        if (jr.TokenType != JsonToken.PropertyName) continue;
                        string prop = jr.Value as string;
                        if (!jr.Read()) break;
                        switch (prop)
                        {
                            case "source":
                                if (jr.TokenType == JsonToken.String)
                                {
                                    source = jr.Value as string;
                                    hasSource = source != null;
                                }
                                else if (jr.TokenType != JsonToken.Null)
                                {
                                    detail = "The source-store record has no string source payload.";
                                    return false;
                                }
                                break;
                            case "guid":
                                payloadGuid = jr.Value?.ToString();
                                break;
                            case "part":
                                payloadPart = jr.Value?.ToString();
                                break;
                            case "hash":
                                payloadHash = jr.Value?.ToString();
                                break;
                            case "lastUpdate":
                                hasLastUpdate = true;
                                if (jr.TokenType == JsonToken.Null)
                                {
                                    payloadLastUpdate = null;
                                }
                                else if (jr.TokenType == JsonToken.Date && jr.Value is DateTime dt)
                                {
                                    payloadLastUpdate = dt;
                                }
                                else if (jr.Value != null)
                                {
                                    if (!DateTime.TryParse(jr.Value.ToString(), out var parsed))
                                    {
                                        detail = "The source-store payload has an invalid last-update value.";
                                        source = null;
                                        return false;
                                    }
                                    payloadLastUpdate = parsed;
                                }
                                break;
                            default:
                                jr.Skip();
                                break;
                        }
                    }
                }

                if (!hasSource || source == null)
                {
                    detail = "The source-store record has no string source payload.";
                    source = null;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(payloadGuid) || string.IsNullOrWhiteSpace(payloadPart)
                    || !string.Equals(payloadGuid.Trim(), normalizedGuid, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(ObjectService.NormalizeRawSourcePart(payloadPart), normalizedPart,
                        StringComparison.OrdinalIgnoreCase))
                {
                    detail = "The source-store payload identity does not match its catalog part.";
                    source = null;
                    return false;
                }

                if (!string.Equals(payloadHash, summary.ContentHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(ComputeHash(source), summary.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    detail = "The source-store payload hash does not match its catalog record.";
                    source = null;
                    return false;
                }

                if (summary.FileBytes > 0
                    && new FileInfo(fullPath).Length != summary.FileBytes)
                {
                    detail = "The source-store record file size does not match its catalog record.";
                    source = null;
                    return false;
                }

                if (hasLastUpdate)
                {
                    if (summary.LastUpdate.HasValue != payloadLastUpdate.HasValue
                        || (summary.LastUpdate.HasValue && payloadLastUpdate.HasValue
                            && summary.LastUpdate.Value != payloadLastUpdate.Value))
                    {
                        detail = "The source-store payload timestamp does not match its catalog record.";
                        source = null;
                        return false;
                    }
                }
                else if (summary.LastUpdate.HasValue)
                {
                    detail = "The source-store payload timestamp does not match its catalog record.";
                    source = null;
                    return false;
                }

                state = RecordReadState.Valid;
                return true;
            }
            catch (Exception ex)
            {
                detail = "The source-store record is unreadable or corrupt: " + ex.Message;
                source = null;
                return false;
            }
        }

        public bool Put(string guid, string partName, string source, DateTime? lastUpdate, string versionToken)
        {
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(partName) || source == null)
            {
                return false;
            }

            Initialize();
            guid = guid.Trim().ToLowerInvariant();
            string originalPartName = partName.Trim();
            partName = ObjectService.NormalizeRawSourcePart(partName);
            string key = MakeKey(guid, partName);
            string hash = ComputeHash(source);

            // A catalog summary is not proof that the per-part file still exists
            // or is readable.  Only take the cheap same-content path after the
            // actual record validates; a missing/corrupt file is rewritten.
            if (_records.TryGetValue(key, out var existing)
                && string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                string ignoredSource;
                RecordReadState ignoredState;
                string ignoredDetail;
                bool validRecord = TryReadStoredRecord(
                    guid, partName, out ignoredSource, out ignoredState, out ignoredDetail);
                // A supplied version token is metadata too: without this, a record first
                // stored without one (search, plain read promotion) could never gain it.
                bool metadataSame = (!lastUpdate.HasValue
                        || (existing.LastUpdate.HasValue && existing.LastUpdate.Value == lastUpdate.Value))
                    && (versionToken == null
                        || string.Equals(existing.VersionToken, versionToken, StringComparison.Ordinal));
                if (validRecord && metadataSame)
                    return true;
            }

            try
            {
                string prefix = guid.Length >= 2 ? guid.Substring(0, 2) : "00";
                string subDir = Path.Combine(_storeDirectory, prefix);
                if (!Directory.Exists(subDir)) Directory.CreateDirectory(subDir);

                string fileName = $"{guid}_{partName}.bin.gz";
                string fullPath = Path.Combine(subDir, fileName);
                string relativePath = Path.Combine(prefix, fileName);

                string tmpPath = Path.Combine(subDir, $"{guid}_{partName}.tmp-{System.Guid.NewGuid():N}");
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                using (var sw = new StreamWriter(gz, new UTF8Encoding(false), 16384))
                using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
                {
                    jw.WriteStartObject();
                    jw.WritePropertyName("guid"); jw.WriteValue(guid);
                    jw.WritePropertyName("part"); jw.WriteValue(originalPartName);
                    jw.WritePropertyName("lastUpdate");
                    if (lastUpdate.HasValue) jw.WriteValue(lastUpdate.Value.ToString("o"));
                    else jw.WriteNull();
                    jw.WritePropertyName("versionToken");
                    if (versionToken != null) jw.WriteValue(versionToken);
                    else jw.WriteNull();
                    jw.WritePropertyName("hash"); jw.WriteValue(hash);
                    jw.WritePropertyName("source"); jw.WriteValue(source);
                    jw.WriteEndObject();
                }

                lock (_ioGate)
                {
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    File.Move(tmpPath, fullPath);
                }

                long fileBytes = new FileInfo(fullPath).Length;

                // Update in-memory record summary
                var summary = new RecordSummary
                {
                    Guid = guid,
                    PartName = originalPartName,
                    LastUpdate = lastUpdate,
                    VersionToken = versionToken,
                    ContentHash = hash,
                    RelativeFilePath = relativePath,
                    FileBytes = fileBytes,
                    StoredAtUtc = DateTime.UtcNow
                };

                int recordId = AssignRecordId(key, summary);

                // Issue #344: update trigram postings, replacing rather than accumulating.
                // The previous set for this record id is removed first, so a source replaced by
                // one with a disjoint trigram set does not leave the old trigrams still
                // pointing at it.
                ulong[] packedTrigrams = TrigramExtractor.ExtractPackedTrigrams(source);
                AddRecordTrigrams(recordId, packedTrigrams);

                // Issue #338: apply the size delta for this record instead of making the
                // next budget check rescan the catalog. A replacement must subtract the
                // previous record's bytes first, or the total grows by both versions.
                RecordSummary previous;
                bool replaced = _records.TryGetValue(key, out previous);
                if (replaced)
                    ApplyAccountingDelta(-previous.FileBytes);
                _records[key] = summary;
                // Issue #339: the body was just replaced, so any certification for the
                // previous content no longer describes it. Dropping it here rather than
                // relying on the window is what makes a same-size replacement visible
                // immediately instead of up to a window later.
                _certifications.TryRemove(key, out _);
                ApplyAccountingDelta(fileBytes);
                MarkCatalogDirty();
                EnforceStorageBudget();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SOURCE-STORE] Error saving source record for {guid} ({partName}): {ex.Message}");
                return false;
            }
        }

        public bool TryGet(string guid, string partName, out string source)
        {
            RecordReadState state;
            string detail;
            bool valid = TryReadStoredRecord(guid, partName, out source, out state, out detail);
            if (!valid && !string.IsNullOrWhiteSpace(detail))
            {
                if (state == RecordReadState.Missing)
                    Logger.Debug("[SOURCE-STORE] Source record unavailable for " + guid + " (" + partName + "): " + detail);
                else
                    Logger.Error("[SOURCE-STORE] Error reading source for " + guid + " (" + partName + "): " + detail);
            }
            return valid;
        }

        /// <summary>
        /// A prior validation of one record, remembered so it does not have to be redone.
        ///
        /// <para>
        /// Issue #339. <c>IsPartStoredAndFresh</c> reached
        /// <c>TryReadStoredRecord</c>, which decompresses the body and checks its hash -
        /// and <c>GetCoverage</c> does that for every entry of the query's entry set
        /// before the scan starts, discarding the content it just read. So a narrow query,
        /// or a warm reopen, paid a full read of every unrelated body while holding a
        /// boolean.
        /// </para>
        ///
        /// <para>
        /// <b>The trade-off, stated rather than hidden.</b> Certifying from file metadata
        /// alone would not be safe: a body corrupted in place with identical length and
        /// timestamp would agree with every recorded field and be reported as certified.
        /// That is why this is not a metadata-only trust - the certification is also
        /// bounded in age by <see cref="FreshnessCertificationWindow"/>, so the same body
        /// is re-read and re-hashed periodically and a metadata-only agreement can never
        /// be older than that window. A change of length or timestamp invalidates
        /// immediately, at any age.
        /// </para>
        /// </summary>
        internal sealed class FreshnessCertification
        {
            public long FileBytes { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public string ContentHash { get; set; } = string.Empty;
            public DateTime? StoredLastUpdate { get; set; }
            public long CertifiedAtTicks { get; set; }
        }

        /// <summary>
        /// How long a certification may stand without re-reading the body. Bounds both
        /// the I/O of a query burst and how long a same-metadata corruption can go
        /// unnoticed.
        /// </summary>
        internal static readonly TimeSpan FreshnessCertificationWindow = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Clock seam for the certification window. Issue #339.
        ///
        /// <para>
        /// The window is the safety half of this optimisation, and a safety property that
        /// can only be tested by waiting 30 seconds - or by mutating private state through
        /// reflection - is one that does not get tested. The seam makes "this
        /// certification is now older than the window" expressible directly.
        /// </para>
        /// </summary>
        internal static Func<DateTime> CertificationClock = () => DateTime.UtcNow;


        private readonly ConcurrentDictionary<string, FreshnessCertification> _certifications =
            new ConcurrentDictionary<string, FreshnessCertification>(StringComparer.OrdinalIgnoreCase);

        // Issue #339: read/validation counters. The cost being fixed is I/O, so the
        // guard has to measure I/O rather than assert that a cache exists.
        private long _contentValidations;
        private long _certificationHits;
        private long _certificationMisses;

        /// <summary>Full body reads performed by the freshness path. Baseline for the bounded-read property.</summary>
        internal long ContentValidations => Interlocked.Read(ref _contentValidations);

        /// <summary>Times a freshness check was answered from a prior certification, with no body read.</summary>
        internal long CertificationHits => Interlocked.Read(ref _certificationHits);

        /// <summary>Times a freshness check could not use a certification and had to read the body.</summary>
        internal long CertificationMisses => Interlocked.Read(ref _certificationMisses);

        /// <summary>Certifications currently held.</summary>
        internal int CertificationCount => _certifications.Count;

        /// <summary>Drops one record's certification. Called wherever its content can change.</summary>
        internal void InvalidateCertification(string guid, string partName)
            => _certifications.TryRemove(MakeKey(guid, partName), out _);

        private bool TryUseCertification(
            string key, string relativePath, RecordSummary summary, DateTime nowUtc)
        {
            if (!_certifications.TryGetValue(key, out var cert)) return false;

            var info = new FileInfo(Path.Combine(_storeDirectory, relativePath));
            if (!info.Exists) return false;
            if (cert.FileBytes != info.Length) return false;
            if (cert.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks) return false;
            if (!string.Equals(cert.ContentHash, summary.ContentHash, StringComparison.OrdinalIgnoreCase))
                return false;
            if (cert.StoredLastUpdate != summary.LastUpdate) return false;
            if (nowUtc.Ticks - cert.CertifiedAtTicks > FreshnessCertificationWindow.Ticks) return false;

            return true;
        }

        /// <summary>The certification clock, in one place so no call site can bypass the seam.</summary>
        private static DateTime CertificationNow() => CertificationClock();

        /// <summary>Records a validation of one body against this exact file identity.</summary>
        private void RecordCertification(string key, string relativePath, RecordSummary summary, DateTime nowUtc)
        {
            try
            {
                var info = new FileInfo(Path.Combine(_storeDirectory, relativePath));
                if (!info.Exists) return;
                _certifications[key] = new FreshnessCertification
                {
                    FileBytes = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    ContentHash = summary.ContentHash ?? string.Empty,
                    StoredLastUpdate = summary.LastUpdate,
                    CertifiedAtTicks = nowUtc.Ticks
                };
            }
            catch
            {
                // A certification is an optimisation. Failing to remember one costs a
                // re-read later, so it must never fail the caller's answer.
            }
        }

        /// <summary>
        /// Returns the stored body <em>and</em> whether it is fresh.
        ///
        /// <para>
        /// Issue #339. This always reads the content, and must keep doing so: callers use
        /// <paramref name="source"/> - the search scan promotes it into the index - so
        /// answering "fresh" from a certification while leaving the body unread would
        /// report a hit with nothing behind it. The certification is recorded as a side
        /// effect here, so the boolean-only probes that do not need the content get to
        /// skip the read next time.
        /// </para>
        /// </summary>
        public bool TryGetStoredAndFresh(SearchIndex.IndexEntry entry, string part, out string source)
        {
            source = null;
            if (entry == null || string.IsNullOrWhiteSpace(entry.Guid)
                || string.IsNullOrWhiteSpace(part))
                return false;

            string normalizedPart = ObjectService.NormalizeRawSourcePart(
                ObjectService.ResolveSearchPartName(entry.Type, part));

            RecordReadState state;
            string detail;
            // Counted here as well as in the boolean probe: the counter exists to measure
            // body reads, and this path reads one on every call by design.
            Interlocked.Increment(ref _contentValidations);
            if (!TryReadStoredRecord(entry.Guid, normalizedPart, out source, out state, out detail))
                return false;
            if (!TryCertifyAfterRead(entry, normalizedPart))
            {
                source = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Compares the index entry's timestamp against the stored record. Split out so
        /// the certification fast path can answer freshness without reading the body.
        /// </summary>
        private static bool IndexEntryIsStale(SearchIndex.IndexEntry entry, RecordSummary summary)
            => summary.LastUpdate.HasValue
                && entry.LastUpdate > DateTime.MinValue
                && entry.LastUpdate > summary.LastUpdate.Value.AddSeconds(2);

        private bool TryCertifyAfterRead(SearchIndex.IndexEntry entry, string normalizedPart)
        {
            if (!_records.TryGetValue(MakeKey(entry.Guid, normalizedPart), out var summary))
                return false;
            if (IndexEntryIsStale(entry, summary)) return false;
            RecordCertification(MakeKey(entry.Guid, normalizedPart), summary.RelativeFilePath, summary, DateTime.UtcNow);
            return true;
        }

        /// <summary>
        /// Answers "is this part stored and fresh?" without needing the content.
        ///
        /// <para>
        /// Issue #339. This is the coverage and freshness-probe path, and it used to reach
        /// <c>TryReadStoredRecord</c> - decompressing the body and checking its hash -
        /// purely to produce a boolean that <c>GetCoverage</c> then discarded, for every
        /// entry of the query's entry set before the scan started. A body already
        /// validated against this exact file identity and catalog hash, still inside the
        /// certification window, is answered from that record instead.
        /// </para>
        ///
        /// <para>
        /// Deliberately separate from <see cref="TryGetStoredAndFresh"/>, whose callers
        /// need the content. Conflating the two would report a hit with nothing behind it.
        /// </para>
        /// </summary>
        public bool IsStoredAndFreshCached(SearchIndex.IndexEntry entry, string part)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Guid)
                || string.IsNullOrWhiteSpace(part))
                return false;

            string normalizedPart = ObjectService.NormalizeRawSourcePart(
                ObjectService.ResolveSearchPartName(entry.Type, part));
            string key = MakeKey(entry.Guid, normalizedPart);

            if (!_records.TryGetValue(key, out var summary))
            {
                Interlocked.Increment(ref _certificationMisses);
                return false;
            }
            if (IndexEntryIsStale(entry, summary))
            {
                Interlocked.Increment(ref _certificationMisses);
                return false;
            }

            if (TryUseCertification(key, summary.RelativeFilePath, summary, CertificationNow()))
            {
                Interlocked.Increment(ref _certificationHits);
                return true;
            }

            Interlocked.Increment(ref _certificationMisses);
            Interlocked.Increment(ref _contentValidations);

            // Issue #339: a certification that just failed its checks describes content
            // that is no longer current. Leaving it in place would re-examine it on every
            // subsequent probe - correct, but it makes CertificationCount overstate what
            // is actually usable and turns a stale record into a per-probe tax.
            _certifications.TryRemove(key, out _);

            string ignored;
            if (!TryReadStoredRecord(entry.Guid, normalizedPart, out ignored, out _, out _))
                return false;
            // Only a body that actually validated earns a certification. Caching the
            // failures too would make a transient read error look like a permanent
            // "not stored" and silently shrink coverage.
            RecordCertification(key, summary.RelativeFilePath, summary, CertificationNow());
            return true;
        }

        // ---- Opt-in read paths. Both are off unless the variable is "1" (whitespace ignored). ----

        /// <summary>GXMCP_SOURCE_STORE_READS: serve genexus_read from a version-matched store record.</summary>
        internal static Func<string> ReadFastPathSetting { get; set; } =
            () => Environment.GetEnvironmentVariable("GXMCP_SOURCE_STORE_READS");

        /// <summary>GXMCP_SOURCE_SEARCH_TRIGRAM_PRUNE: skip stored-and-fresh candidates the postings prove cannot match.</summary>
        internal static Func<string> TrigramPruneSetting { get; set; } =
            () => Environment.GetEnvironmentVariable("GXMCP_SOURCE_SEARCH_TRIGRAM_PRUNE");

        internal static bool ReadFastPathEnabled() => IsFlagOn(ReadFastPathSetting);

        internal static bool TrigramPruneEnabled() => IsFlagOn(TrigramPruneSetting);

        private static bool IsFlagOn(Func<string> setting)
        {
            try { return string.Equals(setting?.Invoke()?.Trim(), "1", StringComparison.Ordinal); }
            catch { return false; }
        }

        /// <summary>
        /// Returns a stored part body together with the version token recorded with it, or
        /// false when there is no valid record or the record carries no token. The token is
        /// taken from the catalog summary before the body is read; a concurrent replacement
        /// can therefore pair an old token with a new body, which the caller's token check
        /// (it fingerprints the returned body) rejects.
        /// </summary>
        internal bool TryGetWithVersionToken(string guid, string partName, out string source, out string versionToken)
        {
            source = null;
            versionToken = null;
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(partName)) return false;

            Initialize();
            string normalizedPart = ObjectService.NormalizeRawSourcePart(partName);
            if (!_records.TryGetValue(MakeKey(guid, normalizedPart), out var summary)
                || summary == null || string.IsNullOrEmpty(summary.VersionToken))
                return false;

            string token = summary.VersionToken;
            if (!TryReadStoredRecord(guid, normalizedPart, out string body, out _, out _))
                return false;
            source = body;
            versionToken = token;
            return true;
        }

        /// <summary>
        /// True only when the store proves <paramref name="literal"/> cannot occur in the
        /// current text of this candidate part, so a search may skip it. Issue #264: pruning
        /// on text that is not known to be complete and current produced false "no results",
        /// so both conditions are required:
        /// <list type="number">
        /// <item><description>the part's record is stored and fresh for this index entry
        /// (<see cref="IsStoredAndFreshCached"/>, the same predicate as search coverage), so
        /// its body is the text the scan's store tier would read; and</description></item>
        /// <item><description>a trigram of the lower-cased literal is missing from the
        /// record's own trigram set, built from that body.</description></item>
        /// </list>
        /// Absence is checked first because it is an in-memory lookup: a candidate that may
        /// match costs no extra I/O. A record whose trigram set is empty (body shorter than
        /// three characters, or no set derived) is never pruned. The catalog summary and the
        /// trigram set must be the same instances before and after the freshness check, so a
        /// concurrent <c>Put</c> (which publishes the set before the summary) refuses
        /// rather than pairing one body's freshness with another body's trigrams. Sets loaded
        /// from the persisted postings file are only accepted for the exact catalog (record
        /// hashes included) they were written with, and freshness hash-checks the body against
        /// that catalog. The caller owns case handling: trigrams are lower-cased with the invariant culture.
        /// </summary>
        internal bool ProvesLiteralAbsent(SearchIndex.IndexEntry entry, string part, string literal)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Guid)
                || string.IsNullOrWhiteSpace(part) || literal == null || literal.Length < 3)
                return false;

            ulong[] required = TrigramExtractor.ExtractPackedTrigrams(literal);
            if (required.Length == 0) return false;

            Initialize();
            string normalizedPart = ObjectService.NormalizeRawSourcePart(
                ObjectService.ResolveSearchPartName(entry.Type, part));
            string key = MakeKey(entry.Guid, normalizedPart);

            if (!_records.TryGetValue(key, out var summaryBefore) || summaryBefore == null || summaryBefore.RecordId < 0)
                return false;
            if (!_trigramsByRecord.TryGetValue(summaryBefore.RecordId, out var indexed) || indexed == null || indexed.Length == 0)
                return false;

            bool missing = false;
            for (int i = 0; i < required.Length; i++)
            {
                if (Array.BinarySearch(indexed, required[i]) < 0) { missing = true; break; }
            }
            if (!missing) return false;

            if (!IsStoredAndFreshCached(entry, part)) return false;

            return _records.TryGetValue(key, out var summaryAfter)
                && ReferenceEquals(summaryBefore, summaryAfter)
                && summaryAfter.RecordId >= 0
                && _trigramsByRecord.TryGetValue(summaryAfter.RecordId, out var indexedAfter)
                && ReferenceEquals(indexed, indexedAfter);
        }

        private static List<string> ResolveRequestedParts(string type, List<string> scope)
        {
            var requested = scope == null || scope.Count == 0
                ? new List<string> { "source" }
                : scope;
            return requested
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => ObjectService.ResolveSearchPartName(type, part).Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool IsPartStoredAndFresh(SearchIndex.IndexEntry entry, string part)
        {
            // Issue #339: this is a boolean probe whose content was always discarded, so
            // it now answers from a certification when one is available. The catalog
            // summary is still only an index - a fresh certification means this exact
            // body, against this exact file identity and catalog hash, was read and
            // validated inside the certification window. That is not the same as trusting
            // catalog metadata, which is what would be unsafe here.
            return IsStoredAndFreshCached(entry, part);
        }

        public SourceStoreCoverage GetCoverage(IEnumerable<SearchIndex.IndexEntry> entries, List<string> scope)
        {
            var coverage = new SourceStoreCoverage();
            if (entries == null) return coverage;

            var entryList = entries as IList<SearchIndex.IndexEntry> ?? entries.ToList();
            coverage.TotalObjects = entryList.Count;

            AccumulateCoverage(coverage, entryList, scope);
            return coverage;
        }

        internal void AccumulateCoverage(SourceStoreCoverage coverage, IEnumerable<SearchIndex.IndexEntry> entries, List<string> scope)
        {
            foreach (var e in entries)
                AccumulateCoverage(coverage, e, scope);
        }

        internal void AccumulateCoverage(SourceStoreCoverage coverage, SearchIndex.IndexEntry e, List<string> scope)
        {
            if (string.IsNullOrWhiteSpace(e?.Guid)) return;
            var requestedParts = ResolveRequestedParts(e.Type, scope);
            bool allStored = requestedParts.Count > 0;
            bool anyStale = false;
            foreach (string part in requestedParts)
            {
                if (!coverage.PartsByPart.TryGetValue(part, out var partCoverage))
                {
                    partCoverage = new SourceStoreCoverage();
                    coverage.PartsByPart[part] = partCoverage;
                }
                partCoverage.TotalObjects++;

                if (IsPartStoredAndFresh(e, part))
                {
                    partCoverage.StoredObjects++;
                }
                else
                {
                    // Missing, corrupt, and timestamp-stale records are all
                    // ineligible for coverage.  Keep them in the stale/error
                    // bucket instead of silently treating an absent file as
                    // a healthy catalog entry.
                    partCoverage.StaleObjects++;
                    allStored = false;
                    anyStale = true;
                }
            }

            if (allStored && !anyStale) coverage.StoredObjects++;
            else if (anyStale) coverage.StaleObjects++;
        }

        public bool IsStoredAndFresh(SearchIndex.IndexEntry entry, List<string> scope)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Guid)) return false;
            var requestedParts = ResolveRequestedParts(entry.Type, scope);
            if (requestedParts.Count == 0) return false;
            foreach (string part in requestedParts)
            {
                if (!IsPartStoredAndFresh(entry, part)) return false;
            }
            return true;
        }

        public List<JObject> SearchStore(
            IEnumerable<SearchIndex.IndexEntry> storedEntries,
            SourceSearchCriteria criteria,
            Regex rx,
            CancellationToken ct = default(CancellationToken))
        {
            var hits = new List<JObject>();
            if (storedEntries == null || criteria == null) return hits;

            var allowedById = new Dictionary<int, (SearchIndex.IndexEntry Entry, string Part, RecordSummary Summary)>();
            foreach (var e in storedEntries)
            {
                if (string.IsNullOrWhiteSpace(e?.Guid)) continue;
                var requestedParts = ResolveRequestedParts(e.Type, criteria.Scope);
                if (requestedParts.Count == 0 || !IsStoredAndFresh(e, requestedParts)) continue;
                string guid = e.Guid.Trim().ToLowerInvariant();
                foreach (string part in requestedParts)
                {
                    if (_records.TryGetValue(MakeKey(guid, part), out var rec) && rec != null && rec.RecordId >= 0)
                    {
                        allowedById[rec.RecordId] = (e, part, rec);
                    }
                }
            }

            if (allowedById.Count == 0) return hits;

            // Extract trigram candidate sets
            var branches = TrigramExtractor.ExtractRequiredPackedTrigramSets(criteria.Pattern, criteria.Callee);
            List<int> matchedIds;
            if (branches == null || branches.Count == 0)
            {
                matchedIds = new List<int>(allowedById.Keys);
            }
            else
            {
                var candidateIds = IntersectPackedPostings(branches);
                matchedIds = new List<int>();
                for (int i = 0; i < candidateIds.Count; i++)
                {
                    int id = candidateIds[i];
                    if (allowedById.ContainsKey(id)) matchedIds.Add(id);
                }
            }

            if (matchedIds.Count == 0) return hits;

            // Parallel verification off-STA
            var bag = new ConcurrentBag<JObject>();
            Parallel.ForEach(matchedIds, new ParallelOptions { CancellationToken = ct }, (recordId, state) =>
            {
                if (ct.IsCancellationRequested || bag.Count >= criteria.MaxResults)
                {
                    state.Stop();
                    return;
                }

                if (!allowedById.TryGetValue(recordId, out var target)) return;
                var entry = target.Entry;
                string part = target.Part;
                var rec = target.Summary;
                if (!TryGet(entry.Guid, part, out string src) || string.IsNullOrEmpty(src)) return;

                string canonicalPart = !string.IsNullOrEmpty(rec?.PartName)
                    ? rec.PartName
                    : ObjectService.ResolveSearchPartName(entry.Type, part);
                if (string.Equals(canonicalPart, "events", StringComparison.OrdinalIgnoreCase))
                {
                    canonicalPart = "Events";
                }
                else if (string.Equals(canonicalPart, "source", StringComparison.OrdinalIgnoreCase))
                {
                    canonicalPart = string.Equals(entry.Type, "WebPanel", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(entry.Type, "Transaction", StringComparison.OrdinalIgnoreCase)
                                 ? "Events" : "Source";
                }

                // Match Callee if requested
                if (!string.IsNullOrEmpty(criteria.Callee))
                {
                    var lines = src.Split('\n');
                    foreach (var call in SourceParser.ParseCalls(src, criteria.IncludeComments))
                    {
                        if (ct.IsCancellationRequested || bag.Count >= criteria.MaxResults)
                        {
                            state.Stop();
                            return;
                        }

                        if (!CalleeMatches(call.Callee, criteria.Callee)) continue;
                        if (criteria.ArgMatches != null && !ArgsMatch(call.Args, criteria.ArgMatches)) continue;
                        if (rx != null)
                        {
                            string ln = call.LineNumber - 1 < lines.Length ? lines[call.LineNumber - 1] : "";
                            if (!rx.IsMatch(ln)) continue;
                        }

                        const int ctx = 3;
                        int idx = call.LineNumber - 1;
                        string lineText = idx >= 0 && idx < lines.Length ? lines[idx] : "";
                        var before = new JArray();
                        for (int bi = Math.Max(0, idx - ctx); bi < idx; bi++) before.Add(lines[bi]);
                        var after = new JArray();
                        for (int ai = idx + 1; ai < Math.Min(lines.Length, idx + 1 + ctx); ai++) after.Add(lines[ai]);

                        var hit = new JObject
                        {
                            ["objectName"] = entry.Name,
                            ["type"] = entry.Type,
                            ["guid"] = entry.Guid,
                            ["entityKey"] = entry.EntityKey,
                            ["path"] = entry.Path,
                            ["part"] = canonicalPart,
                            ["callee"] = call.Callee,
                            ["line"] = call.LineNumber,
                            ["lineNumber"] = call.LineNumber,
                            ["lineText"] = lineText,
                            ["contextBefore"] = before,
                            ["contextAfter"] = after,
                            ["args"] = new JArray(call.Args.Select(a => (JToken)a).ToArray())
                        };
                        bag.Add(hit);
                    }
                }
                else if (rx != null)
                {
                    var lines = src.Split('\n');
                    for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                    {
                        if (ct.IsCancellationRequested || bag.Count >= criteria.MaxResults)
                        {
                            state.Stop();
                            return;
                        }

                        string line = lines[lineIdx];
                        if (rx.IsMatch(line))
                        {
                            const int ctx = 3;
                            var before = new JArray();
                            for (int bi = Math.Max(0, lineIdx - ctx); bi < lineIdx; bi++) before.Add(lines[bi]);
                            var after = new JArray();
                            for (int ai = lineIdx + 1; ai < Math.Min(lines.Length, lineIdx + 1 + ctx); ai++) after.Add(lines[ai]);

                            var hit = new JObject
                            {
                                ["objectName"] = entry.Name,
                                ["type"] = entry.Type,
                                ["guid"] = entry.Guid,
                                ["entityKey"] = entry.EntityKey,
                                ["path"] = entry.Path,
                                ["part"] = canonicalPart,
                                ["line"] = lineIdx + 1,
                                ["lineNumber"] = lineIdx + 1,
                                ["lineText"] = line,
                                ["contextBefore"] = before,
                                ["contextAfter"] = after
                            };
                            bag.Add(hit);
                        }
                    }
                }
            });

            hits.AddRange(bag.Take(criteria.MaxResults));
            return hits;
        }

        private static bool CalleeMatches(string actual, string wanted)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(wanted)) return false;
            if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = actual.LastIndexOf('.');
            if (dot >= 0)
            {
                return string.Equals(actual.Substring(dot + 1), wanted, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool ArgsMatch(List<string> actualArgs, Dictionary<int, string> expected)
        {
            foreach (var kvp in expected)
            {
                if (kvp.Key < 0 || kvp.Key >= actualArgs.Count) return false;
                if (!string.Equals(actualArgs[kvp.Key]?.Trim(), kvp.Value?.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private void MarkCatalogDirty()
        {
            _isCatalogDirty = true;
            lock (_flushGate)
            {
                if (_flushTimer == null)
                {
                    _flushTimer = new Timer(_ => FlushCatalog(), null, 1500, Timeout.Infinite);
                }
                else
                {
                    _flushTimer.Change(1500, Timeout.Infinite);
                }
            }
        }

        public void FlushCatalog()
        {
            if (!_isCatalogDirty) return;
            lock (_flushGate)
            {
                if (!_isCatalogDirty) return;
                try
                {
                    string catalogPath = Path.Combine(_storeDirectory, "catalog.json.gz");
                    string tmpPath = catalogPath + $".tmp-{Guid.NewGuid():N}";
                    string catalogDigest;

                    using (var sha = SHA256.Create())
                    {
                        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var gz = new GZipStream(fs, CompressionMode.Compress))
                        using (var cs = new CryptoStream(gz, sha, CryptoStreamMode.Write))
                        using (var sw = new StreamWriter(cs, new UTF8Encoding(false), 16384))
                        using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
                        {
                            jw.WriteStartObject();
                            jw.WritePropertyName("version"); jw.WriteValue(1);
                            jw.WritePropertyName("savedAt"); jw.WriteValue(DateTime.UtcNow.ToString("o"));
                            jw.WritePropertyName("records");
                            jw.WriteStartArray();
                            foreach (var kvp in _records)
                            {
                                var rec = kvp.Value;
                                jw.WriteStartObject();
                                jw.WritePropertyName("k"); jw.WriteValue(kvp.Key);
                                jw.WritePropertyName("g"); jw.WriteValue(rec.Guid);
                                jw.WritePropertyName("p"); jw.WriteValue(rec.PartName);
                                jw.WritePropertyName("u");
                                if (rec.LastUpdate.HasValue) jw.WriteValue(rec.LastUpdate.Value.ToString("o"));
                                else jw.WriteNull();
                                jw.WritePropertyName("v");
                                if (rec.VersionToken != null) jw.WriteValue(rec.VersionToken);
                                else jw.WriteNull();
                                jw.WritePropertyName("h");
                                if (rec.ContentHash != null) jw.WriteValue(rec.ContentHash);
                                else jw.WriteNull();
                                jw.WritePropertyName("f");
                                if (rec.RelativeFilePath != null) jw.WriteValue(rec.RelativeFilePath);
                                else jw.WriteNull();
                                jw.WritePropertyName("b"); jw.WriteValue(rec.FileBytes);
                                jw.WritePropertyName("s"); jw.WriteValue(rec.StoredAtUtc.ToString("o"));
                                jw.WriteEndObject();
                            }
                            jw.WriteEndArray();
                            jw.WriteEndObject();
                            jw.Flush();
                            sw.Flush();
                            cs.FlushFinalBlock();
                        }
                        catalogDigest = Convert.ToBase64String(sha.Hash);
                    }

                    lock (_ioGate)
                    {
                        if (File.Exists(catalogPath)) File.Delete(catalogPath);
                        File.Move(tmpPath, catalogPath);
                    }

                    // Issue #374: persist the derived trigram state next to the catalog it
                    // describes, stamped with the catalog's own digest. Without it every
                    // warm reopen had to read, decompress and hash-check every stored body
                    // to rebuild postings, so startup I/O scaled with the whole store.
                    // Written after the catalog is in place, and stamped with the digest
                    // of what was just written, so a crash between the two leaves postings
                    // that fail the digest check and trigger the normal rebuild.
                    WriteTrigramPostingsFile(catalogDigest);

                    _isCatalogDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SOURCE-STORE] Failed to flush catalog: {ex.Message}");
                }
            }
        }

        private void LoadCatalog()
        {
            string catalogPath = Path.Combine(_storeDirectory, "catalog.json.gz");
            _records.Clear();
            _trigramIndex.Clear();
            // Issue #344: rebuilt from scratch below, so the previous map must not
            // survive into it. A leftover entry would make the first replacement of a
            // loaded record subtract the wrong postings.
            _trigramsByRecord.Clear();
            ClearRecordIds();
            // Issue #339: same for the freshness certifications - they describe the
            // store that was just reset. A reopen therefore starts uncertified, and the
            // first coverage probe validates bodies lazily, which is what keeps a
            // postings hit from standing in for a freshness claim.
            _certifications.Clear();
            if (!File.Exists(catalogPath)) return;

            try
            {
                string catalogDigest;
                using (var sha = SHA256.Create())
                {
                    using (var fs = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    using (var cs = new CryptoStream(gz, sha, CryptoStreamMode.Read))
                    using (var reader = new StreamReader(cs, Encoding.UTF8))
                    using (var jr = new JsonTextReader(reader) { DateParseHandling = DateParseHandling.DateTime })
                    {
                        if (!jr.Read() || jr.TokenType != JsonToken.StartObject) return;
                        bool foundRecords = false;
                        while (jr.Read())
                        {
                            if (jr.TokenType == JsonToken.EndObject) break;
                            if (jr.TokenType != JsonToken.PropertyName) continue;
                            string rootProp = jr.Value as string;
                            if (!jr.Read()) break;
                            if (string.Equals(rootProp, "records", StringComparison.Ordinal))
                            {
                                if (jr.TokenType != JsonToken.StartArray) return;
                                foundRecords = true;
                                while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                                {
                                    if (jr.TokenType != JsonToken.StartObject) { jr.Skip(); continue; }

                                    string guid = null, part = null, v = null, h = null, f = null;
                                    DateTime? u = null, storedAt = null;
                                    bool uInvalid = false, sInvalid = false;
                                    long b = 0;

                                    while (jr.Read() && jr.TokenType != JsonToken.EndObject)
                                    {
                                        if (jr.TokenType != JsonToken.PropertyName) continue;
                                        string prop = jr.Value as string;
                                        if (!jr.Read()) break;
                                        switch (prop)
                                        {
                                            case "g": guid = jr.Value?.ToString(); break;
                                            case "p": part = jr.Value?.ToString(); break;
                                            case "v": v = jr.Value?.ToString(); break;
                                            case "h": h = jr.Value?.ToString(); break;
                                            case "f": f = jr.Value?.ToString(); break;
                                            case "b":
                                                if (jr.TokenType == JsonToken.Integer || jr.TokenType == JsonToken.Float)
                                                    b = Convert.ToInt64(jr.Value);
                                                break;
                                            case "u":
                                                if (jr.TokenType == JsonToken.Date && jr.Value is DateTime dtU) u = dtU;
                                                else if (jr.TokenType != JsonToken.Null && jr.Value != null)
                                                {
                                                    if (DateTime.TryParse(jr.Value.ToString(), out var parsedU)) u = parsedU;
                                                    else uInvalid = true;
                                                }
                                                break;
                                            case "s":
                                                if (jr.TokenType == JsonToken.Date && jr.Value is DateTime dtS) storedAt = dtS;
                                                else if (jr.TokenType != JsonToken.Null && jr.Value != null)
                                                {
                                                    if (DateTime.TryParse(jr.Value.ToString(), out var parsedS)) storedAt = parsedS;
                                                    else sInvalid = true;
                                                }
                                                break;
                                            default:
                                                jr.Skip();
                                                break;
                                        }
                                    }

                                    if (!string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(part))
                                    {
                                        // Keep even an incomplete summary in memory so freshness
                                        // can report it as an invalid/stale part rather than
                                        // silently mistaking it for an unvisited object.  The
                                        // concrete record validator will reject missing paths,
                                        // hashes, or payloads.
                                        string canonicalPart = ObjectService.NormalizeRawSourcePart(part);
                                        string canonicalKey = MakeKey(guid, canonicalPart);
                                        if (string.IsNullOrWhiteSpace(h) || uInvalid || sInvalid || b < 0)
                                            h = null;
                                        var summary = new RecordSummary
                                        {
                                            Guid = guid.Trim(),
                                            PartName = part.Trim(),
                                            LastUpdate = u,
                                            VersionToken = v,
                                            ContentHash = h,
                                            RelativeFilePath = f,
                                            FileBytes = b,
                                            StoredAtUtc = storedAt ?? DateTime.UtcNow
                                        };
                                        AssignRecordId(canonicalKey, summary);
                                        _records[canonicalKey] = summary;
                                        // Issue #339: the catalog was rebuilt from disk, so every
                                        // certification refers to a previous process's validation.
                                        _certifications.TryRemove(canonicalKey, out _);
                                        // Issue #338: the catalog load rebuilt the record set, so
                                        // the incremental total describes the previous set. Force
                                        // one reconciliation pass rather than trying to patch a
                                        // counter across a bulk replace.
                                        MarkAccountingUntrusted();
                                    }
                                }
                            }
                            else
                            {
                                jr.Skip();
                            }
                        }
                        if (!foundRecords) return;
                        // Drain any trailing bytes so CryptoStream hashes the complete stream.
                        while (reader.Read() > 0) { }
                    }
                    catalogDigest = Convert.ToBase64String(sha.Hash);
                }

                // Issue #374: try the persisted postings first. A warm reopen of an unchanged store
                // then reads one derived file instead of every stored body, so startup
                // I/O no longer scales with the store size. A missing, partial, stale
                // or corrupt file falls through to the full rebuild below, so search
                // results are identical either way.
                if (!TryLoadTrigramPostingsFile(catalogDigest))
                {
                    RebuildTrigramPostingsFromBodies();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SOURCE-STORE] Error reading catalog: {ex.Message}");
            }
        }

        // ---- Issue #374: derived trigram state persisted beside the catalog ----

        private static string TrigramPostingsFileName => "trigram-postings.json.gz";

        /// <summary>
        /// Reconstructs the postings by reading and decompressing every stored body. The
        /// fallback for a store with no usable postings file.
        /// </summary>
        private void RebuildTrigramPostingsFromBodies()
        {
            Parallel.ForEach(_records, kvp =>
            {
                if (kvp.Value == null || kvp.Value.RecordId < 0) return;
                if (!TryGet(kvp.Value.Guid, kvp.Value.PartName, out string src) || string.IsNullOrEmpty(src)) return;
                // Issue #344: a loaded record must record its own trigram set. Without
                // this, the first replacement of a record that came from disk had nothing
                // to subtract, so its original postings stayed live and the stale record id was
                // never reclaimable - which is exactly the churn case this fixes.
                AddRecordTrigrams(kvp.Value.RecordId, TrigramExtractor.ExtractPackedTrigrams(src));
            });
        }

        /// <summary>Publishes one record's trigram set into both directions of the index.</summary>
        private void AddRecordTrigrams(int recordId, ulong[] packedTrigrams)
        {
            RemoveTrigramPostings(recordId);
            var trigrams = packedTrigrams ?? Array.Empty<ulong>();
            for (int i = 0; i < trigrams.Length; i++)
            {
                var list = _trigramIndex.GetOrAdd(trigrams[i], _ => new PostingList());
                list.Add(recordId);
            }
            _trigramsByRecord[recordId] = trigrams;
        }

        private List<int> IntersectPackedPostings(List<ulong[]> branches)
        {
            var union = new HashSet<int>();
            var candidates = new List<int>();

            foreach (var branch in branches)
            {
                if (branch == null || branch.Length == 0) continue;

                ulong rarest = 0;
                PostingList rarestList = null;
                int minPostings = int.MaxValue;
                for (int i = 0; i < branch.Length; i++)
                {
                    if (!_trigramIndex.TryGetValue(branch[i], out var list) || list == null)
                    {
                        minPostings = 0;
                        break;
                    }
                    int count = list.Count;
                    if (count == 0)
                    {
                        minPostings = 0;
                        break;
                    }
                    if (count < minPostings)
                    {
                        minPostings = count;
                        rarest = branch[i];
                        rarestList = list;
                    }
                }

                if (minPostings == 0 || rarestList == null) continue;

                candidates.Clear();
                candidates.AddRange(rarestList.SnapshotIds());
                for (int i = 0; i < branch.Length; i++)
                {
                    ulong t = branch[i];
                    if (t == rarest) continue;
                    if (!_trigramIndex.TryGetValue(t, out var list) || list == null)
                    {
                        candidates.Clear();
                        break;
                    }
                    list.IntersectInto(candidates);
                    if (candidates.Count == 0) break;
                }

                for (int i = 0; i < candidates.Count; i++) union.Add(candidates[i]);
            }

            return new List<int>(union);
        }

        /// <summary>
        /// Writes the derived postings, stamped with the catalog digest. Best-effort: a
        /// failure here costs the next start a full rebuild, which is the behaviour before
        /// this file existed. Streams JSON directly to avoid allocating a large DOM in memory.
        /// </summary>
        private void WriteTrigramPostingsFile(string catalogDigest)
        {
            try
            {
                string path = Path.Combine(_storeDirectory, TrigramPostingsFileName);
                string tmp = path + $".tmp-{Guid.NewGuid():N}";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                using (var sw = new StreamWriter(gz, new UTF8Encoding(false), 16384))
                using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
                {
                    jw.WriteStartObject();
                    jw.WritePropertyName("version"); jw.WriteValue(TrigramPostingsSchemaVersion);
                    jw.WritePropertyName("catalogDigest"); jw.WriteValue(catalogDigest);
                    jw.WritePropertyName("records"); jw.WriteValue(_records.Count);
                    jw.WritePropertyName("savedAt"); jw.WriteValue(DateTime.UtcNow.ToString("o"));
                    jw.WritePropertyName("byRecord");
                    jw.WriteStartArray();
                    foreach (var kvp in _trigramsByRecord)
                    {
                        string key = GetKeyById(kvp.Key);
                        if (key == null || kvp.Value == null) continue;
                        jw.WriteStartObject();
                        jw.WritePropertyName("k"); jw.WriteValue(key);
                        jw.WritePropertyName("t");
                        jw.WriteStartArray();
                        ulong[] trigrams = kvp.Value;
                        for (int i = 0; i < trigrams.Length; i++)
                        {
                            jw.WriteValue(TrigramExtractor.UnpackTrigram(trigrams[i]));
                        }
                        jw.WriteEndArray();
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                    jw.WriteEndObject();
                }
                lock (_ioGate)
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[SOURCE-STORE] Could not persist trigram postings: " + ex.Message);
            }
        }

        /// <summary>
        /// Loads persisted postings when they provably belong to the catalog just read.
        /// Returns false - leaving the index empty for
        /// <see cref="RebuildTrigramPostingsFromBodies"/> - for anything unproven.
        /// Streams JSON and builds exact-sized sorted PostingLists without intermediate string sets.
        /// </summary>
        private bool TryLoadTrigramPostingsFile(string catalogDigest)
        {
            string path = Path.Combine(_storeDirectory, TrigramPostingsFileName);
            if (!File.Exists(path)) return false;

            try
            {
                int? version = null;
                string fileDigest = null;
                long? recordCount = null;
                var staged = new List<(int RecordId, ulong[] Trigrams)>(_records.Count);
                var postingCounts = new Dictionary<ulong, int>();
                var localTrigrams = new HashSet<ulong>();

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                using (var jr = new JsonTextReader(reader))
                {
                    if (!jr.Read() || jr.TokenType != JsonToken.StartObject) return false;
                    while (jr.Read())
                    {
                        if (jr.TokenType == JsonToken.EndObject) break;
                        if (jr.TokenType != JsonToken.PropertyName) continue;
                        string prop = jr.Value as string;
                        if (!jr.Read()) break;

                        switch (prop)
                        {
                            case "version":
                                version = jr.Value != null ? Convert.ToInt32(jr.Value) : (int?)null;
                                break;
                            case "catalogDigest":
                                fileDigest = jr.Value?.ToString();
                                break;
                            case "records":
                                recordCount = jr.Value != null ? Convert.ToInt64(jr.Value) : (long?)null;
                                break;
                            case "byRecord":
                                if (version != TrigramPostingsSchemaVersion
                                    || !string.Equals(fileDigest, catalogDigest, StringComparison.Ordinal)
                                    || recordCount != _records.Count)
                                    return false;
                                if (jr.TokenType != JsonToken.StartArray) return false;

                                // Rebuild from the persisted per-record sets. Only keys the catalog still
                                // holds are accepted, so a postings file can never introduce a record that
                                // the catalog does not describe.
                                while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                                {
                                    if (jr.TokenType != JsonToken.StartObject) { jr.Skip(); continue; }
                                    string key = null;
                                    bool hasTrigrams = false;
                                    localTrigrams.Clear();

                                    while (jr.Read() && jr.TokenType != JsonToken.EndObject)
                                    {
                                        if (jr.TokenType != JsonToken.PropertyName) continue;
                                        string itemProp = jr.Value as string;
                                        if (!jr.Read()) break;
                                        if (itemProp == "k")
                                        {
                                            key = jr.Value?.ToString();
                                        }
                                        else if (itemProp == "t" && jr.TokenType == JsonToken.StartArray)
                                        {
                                            hasTrigrams = true;
                                            while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                                            {
                                                if (jr.TokenType == JsonToken.String && jr.Value is string ts
                                                    && TrigramExtractor.TryPackTrigram(ts, out ulong packed))
                                                {
                                                    localTrigrams.Add(packed);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            jr.Skip();
                                        }
                                    }

                                    if (string.IsNullOrEmpty(key) || !hasTrigrams
                                        || !_records.TryGetValue(key, out var rec) || rec == null || rec.RecordId < 0)
                                        continue;

                                    ulong[] arr = localTrigrams.Count == 0 ? Array.Empty<ulong>() : new ulong[localTrigrams.Count];
                                    if (arr.Length > 0)
                                    {
                                        localTrigrams.CopyTo(arr);
                                        Array.Sort(arr);
                                        for (int i = 0; i < arr.Length; i++)
                                        {
                                            postingCounts[arr[i]] = postingCounts.TryGetValue(arr[i], out int c) ? c + 1 : 1;
                                        }
                                    }
                                    staged.Add((rec.RecordId, arr));
                                }
                                break;
                            default:
                                jr.Skip();
                                break;
                        }
                    }
                }

                if (version != TrigramPostingsSchemaVersion
                    || !string.Equals(fileDigest, catalogDigest, StringComparison.Ordinal)
                    || recordCount != _records.Count)
                    return false;

                if (staged.Count != _records.Count)
                {
                    // A record the catalog holds has no set here: the file is partial, so
                    // it cannot answer absence and the bodies have to be read.
                    Logger.Warn($"[SOURCE-STORE] Persisted trigram postings cover {staged.Count} of "
                        + _records.Count + " records; rebuilding from bodies.");
                    return false;
                }

                // Build exact-sized sorted int[] posting arrays in one pass.
                var builders = new Dictionary<ulong, (int[] Ids, int Cursor)>(postingCounts.Count);
                foreach (var kvp in postingCounts)
                {
                    builders[kvp.Key] = (new int[kvp.Value], 0);
                }

                staged.Sort((a, b) => a.RecordId.CompareTo(b.RecordId));
                for (int i = 0; i < staged.Count; i++)
                {
                    int recId = staged[i].RecordId;
                    ulong[] trigrams = staged[i].Trigrams;
                    _trigramsByRecord[recId] = trigrams;
                    for (int j = 0; j < trigrams.Length; j++)
                    {
                        ulong t = trigrams[j];
                        var slot = builders[t];
                        slot.Ids[slot.Cursor] = recId;
                        builders[t] = (slot.Ids, slot.Cursor + 1);
                    }
                }

                foreach (var kvp in builders)
                {
                    _trigramIndex[kvp.Key] = new PostingList(kvp.Value.Ids);
                }
                return true;
            }
            catch (Exception ex)
            {
                // A corrupt postings file is a rebuild, never a wrong answer.
                Logger.Warn("[SOURCE-STORE] Discarding unreadable trigram postings: " + ex.Message);
                return false;
            }
        }

        private const int TrigramPostingsSchemaVersion = 1;

        private void EnforceStorageBudget()
        {
            long maxBytes = Configuration.SourceStoreMaxMB * 1024L * 1024L;
            long currentBytes = CurrentStorageBytes();

            if (currentBytes <= maxBytes) return;

            // LRU eviction: sort by StoredAtUtc ascending
            var sorted = _records.Values.OrderBy(r => r.StoredAtUtc).ToList();
            long targetBytes = (long)(maxBytes * 0.85);

            foreach (var rec in sorted)
            {
                if (currentBytes <= targetBytes) break;
                string key = MakeKey(rec.Guid, rec.PartName);
                if (_records.TryRemove(key, out _))
                {
                    // Issue #344: eviction dropped the catalog record and the file but
                    // left the trigram postings, so an evicted record stayed reachable
                    // through the index and the stale record ids accumulated across evictions.
                    RemoveTrigramPostings(rec.RecordId);
                    ReleaseRecordId(rec.RecordId);
                    // Issue #339: and its certification, which now describes a file that
                    // no longer exists. The FileInfo check would catch it, but leaving a
                    // dead entry behind makes the count meaningless as a diagnostic.
                    _certifications.TryRemove(key, out _);

                    // Issue #363: the record left _records, so its bytes left the catalog
                    // whichever way the delete goes. Only the local loop variable used to
                    // absorb them, so _trackedBytes kept every evicted byte forever: the
                    // next Put read a total that was still over budget, evicted again, and
                    // the store drained progressively while each insert paid a full sort.
                    ApplyAccountingDelta(-rec.FileBytes);

                    try
                    {
                        string fullPath = Path.Combine(_storeDirectory, rec.RelativeFilePath);
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        currentBytes -= rec.FileBytes;
                    }
                    catch
                    {
                        // The bytes are still on disk but no longer tracked by the catalog,
                        // so the counter stays consistent with what it describes. The
                        // orphaned path is remembered for a later sweep instead of
                        // invalidating the counter, which used to make every subsequent
                        // budget check re-sum the whole catalog.
                        Interlocked.Increment(ref _orphanedFileCount);
                    }
                }
            }
            MarkCatalogDirty();
        }

        /// <summary>
        /// Total tracked storage in bytes, reconciling the incremental counter with one
        /// catalog pass whenever it is not trusted.
        /// </summary>
        private long CurrentStorageBytes()
        {
            if (_accountingTrusted) return Interlocked.Read(ref _trackedBytes);

            long total = SumRecordBytes();
            Interlocked.Exchange(ref _trackedBytes, total);
            Volatile.Write(ref _accountingTrusted, true);
            return total;
        }

        /// <summary>
        /// Applies a byte delta to the tracked total. When the counter is untrusted the
        /// delta is dropped on purpose: the next <see cref="CurrentStorageBytes"/> pass
        /// rebuilds the exact total, and folding one unverified delta into a rebuilt
        /// value is how a counter drifts.
        /// </summary>
        private void ApplyAccountingDelta(long deltaBytes)
        {
            if (!_accountingTrusted) return;
            Interlocked.Add(ref _trackedBytes, deltaBytes);
        }

        /// <summary>
        /// Marks the incremental total as untrusted, so the next enforcement reconciles
        /// it with a single pass. Called from any path that could have changed
        /// <see cref="_records"/> without going through the accounting helpers.
        /// </summary>
        private void MarkAccountingUntrusted() => Volatile.Write(ref _accountingTrusted, false);

        /// <summary>
        /// Removes a record's trigram postings and forgets the record's trigram set.
        /// Issue #344. Each posting is removed under the same per-set lock the writer
        /// uses, so a concurrent write that re-adds the id is not undone, and a list
        /// left empty is dropped from the index.
        /// </summary>
        private void RemoveTrigramPostings(int recordId)
        {
            if (recordId < 0) return;
            if (!_trigramsByRecord.TryRemove(recordId, out var previous) || previous == null) return;
            for (int i = 0; i < previous.Length; i++)
            {
                ulong t = previous[i];
                if (!_trigramIndex.TryGetValue(t, out var list) || list == null) continue;
                if (list.Remove(recordId))
                {
                    lock (list)
                    {
                        if (list.Count == 0) _trigramIndex.TryRemove(t, out _);
                    }
                }
            }
        }

        /// <summary>
        /// Number of catalog records walked by <see cref="SumRecordBytes"/>. An
        /// in-process counter alone cannot catch a full-catalog sum reintroduced at a
        /// different call site, so this counts visits at the one place any such sum has
        /// to go through; <c>SourceStoreBudgetAccountingTests</c> pairs it with a
        /// source-shape guard on <see cref="EnforceStorageBudget"/>.
        /// </summary>
        internal long CatalogRecordVisits => Interlocked.Read(ref _accountingScans);

        /// <summary>
        /// The one place a full-catalog byte sum is allowed to happen.
        /// </summary>
        private long SumRecordBytes()
        {
            long total = 0;
            foreach (var r in _records.Values)
            {
                Interlocked.Increment(ref _accountingScans);
                total += r.FileBytes;
            }
            return total;
        }

        /// <summary>
        /// Total tracked storage, reconciled on demand. Exposed so a budget assertion
        /// can check the counter against the catalog it is supposed to describe.
        /// </summary>
        internal long TrackedStorageBytes() => CurrentStorageBytes();

        /// <summary>Catalog records whose bytes the counter currently accounts for.</summary>
        internal int RecordCount => _records.Count;

        // ---- Issue #374 test seams: the reopen path needs to be drivable without a
        // second process, and the failure modes need to be injectable.

        /// <summary>Writes the catalog to disk now, as the flush timer would.</summary>
        internal void FlushCatalogForTest() => FlushCatalog();

        /// <summary>Re-reads the catalog and postings from disk, as a fresh start would.</summary>
        internal void ReloadCatalogForTest() => LoadCatalog();

        /// <summary>The record key the postings index stores a GUID/part pair under.</summary>
        internal string MakeKeyForTest(string guid, string partName)
            => MakeKey(guid, ObjectService.NormalizeRawSourcePart(partName));

        /// <summary>Removes the persisted postings so the next load must rebuild.</summary>
        internal void DiscardPostingsForTest()
        {
            try
            {
                string path = Path.Combine(_storeDirectory, TrigramPostingsFileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// Rewrites the persisted postings with only half their records, standing in for a
        /// file that was truncated or written by an older partial flush.
        /// </summary>
        internal void DropHalfThePostingsForTest()
        {
            string path = Path.Combine(_storeDirectory, TrigramPostingsFileName);
            if (!File.Exists(path)) return;

            string json;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
            {
                json = reader.ReadToEnd();
            }

            var root = JObject.Parse(json);
            var byRecord = root["byRecord"] as JArray;
            if (byRecord != null)
                byRecord = new JArray(byRecord.Take(byRecord.Count / 2).Cast<object>().ToArray());
            root["byRecord"] = byRecord;

            string tmp = path + ".test";
            byte[] bytes = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
            {
                gz.Write(bytes, 0, bytes.Length);
            }
            File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>
        /// Issue #363: evicted files whose delete failed. They survive on disk outside the
        /// catalog and outside the byte budget, so this is the only trace of them.
        /// </summary>
        internal long OrphanedFileCount => Interlocked.Read(ref _orphanedFileCount);

        /// <summary>
        /// Issue #363: whether <see cref="_trackedBytes"/> still equals the sum of the
        /// catalog it describes. The counter is supposed to drift only via
        /// <see cref="MarkAccountingUntrusted"/>, so a trusted-but-divergent counter is a
        /// defect rather than a pending reconciliation. Exposed for guards.
        /// </summary>
        internal bool TrackedBytesMatchCatalog()
            => !_accountingTrusted || CurrentStorageBytes() == SumRecordBytes();

        [ThreadStatic]
        private static byte[] t_hashBuffer;

        private static string ComputeHash(string text)
        {
            string s = text ?? string.Empty;
            using (var sha = SHA256.Create())
            {
                if (s.Length == 0)
                {
                    return FormatHexLower(sha.ComputeHash(Array.Empty<byte>()));
                }

                byte[] buf = t_hashBuffer ?? (t_hashBuffer = new byte[16384]);
                var enc = Encoding.UTF8.GetEncoder();
                int charIdx = 0;
                int remaining = s.Length;
                unsafe
                {
                    fixed (char* pChars = s)
                    fixed (byte* pBuf = buf)
                    {
                        while (remaining > 0)
                        {
                            enc.Convert(pChars + charIdx, remaining, pBuf, buf.Length, flush: false, out int charsUsed, out int bytesUsed, out _);
                            charIdx += charsUsed;
                            remaining -= charsUsed;
                            if (bytesUsed > 0) sha.TransformBlock(buf, 0, bytesUsed, null, 0);
                        }
                        enc.Convert(pChars + charIdx, 0, pBuf, buf.Length, flush: true, out _, out int finalBytes, out _);
                        sha.TransformFinalBlock(buf, 0, finalBytes);
                    }
                }
                return FormatHexLower(sha.Hash);
            }
        }

        private static string FormatHexLower(byte[] hash)
        {
            char[] c = new char[hash.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < hash.Length; i++)
            {
                byte b = hash[i];
                c[i * 2] = hex[b >> 4];
                c[i * 2 + 1] = hex[b & 0xF];
            }
            return new string(c);
        }
    }
}
