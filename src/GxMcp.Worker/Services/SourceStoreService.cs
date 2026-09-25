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
            public string Guid { get; set; }
            public string PartName { get; set; }
            public DateTime? LastUpdate { get; set; }
            public string VersionToken { get; set; }
            public string ContentHash { get; set; }
            public string RelativeFilePath { get; set; }
            public long FileBytes { get; set; }
            public DateTime StoredAtUtc { get; set; }
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
        private readonly ConcurrentDictionary<string, HashSet<string>> _trigramIndex =
            new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly object _ioGate = new object();
        private readonly object _flushGate = new object();
        private readonly object _initGate = new object();
        private Timer _flushTimer;
        private bool _isCatalogDirty;
        private volatile bool _initialized;

        public SourceStoreService()
        {
            _storeDirectory = Path.Combine(RuntimePaths.StateRoot, "source-store");
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
                _initialized = false;
                Initialize();
            }
        }

        public string StoreDirectory => _storeDirectory;
        public int StoredRecordCount => _records.Count;

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

        private static DateTime? ParseDateToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date) return token.Value<DateTime>();
            DateTime parsed;
            return DateTime.TryParse(token.ToString(), out parsed) ? parsed : (DateTime?)null;
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
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                {
                    var payload = JObject.Parse(reader.ReadToEnd());
                    var sourceToken = payload["source"];
                    if (sourceToken == null || sourceToken.Type != JTokenType.String)
                    {
                        detail = "The source-store record has no string source payload.";
                        return false;
                    }
                    source = sourceToken.Value<string>();
                    if (source == null)
                    {
                        detail = "The source-store record source is null.";
                        return false;
                    }

                    string payloadGuid = payload["guid"]?.ToString();
                    string payloadPart = payload["part"]?.ToString();
                    if (string.IsNullOrWhiteSpace(payloadGuid) || string.IsNullOrWhiteSpace(payloadPart)
                        || !string.Equals(payloadGuid.Trim(), normalizedGuid, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ObjectService.NormalizeRawSourcePart(payloadPart), normalizedPart,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        detail = "The source-store payload identity does not match its catalog part.";
                        source = null;
                        return false;
                    }

                    string payloadHash = payload["hash"]?.ToString();
                    if (!string.Equals(payloadHash, summary.ContentHash, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ComputeHash(source), summary.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
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

                    var lastUpdateToken = payload["lastUpdate"];
                    DateTime? payloadLastUpdate = null;
                    if (lastUpdateToken != null && lastUpdateToken.Type != JTokenType.Null)
                    {
                        DateTime parsed;
                        if (lastUpdateToken.Type == JTokenType.Date)
                        {
                            parsed = lastUpdateToken.Value<DateTime>();
                        }
                        else if (!DateTime.TryParse(lastUpdateToken.ToString(), out parsed))
                        {
                            detail = "The source-store payload has an invalid last-update value.";
                            source = null;
                            return false;
                        }
                        payloadLastUpdate = parsed;
                    }
                    if (summary.LastUpdate.HasValue != payloadLastUpdate.HasValue
                        || (summary.LastUpdate.HasValue && payloadLastUpdate.HasValue
                            && summary.LastUpdate.Value != payloadLastUpdate.Value))
                    {
                        detail = "The source-store payload timestamp does not match its catalog record.";
                        source = null;
                        return false;
                    }
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
                bool metadataSame = !lastUpdate.HasValue
                    || (existing.LastUpdate.HasValue && existing.LastUpdate.Value == lastUpdate.Value);
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

                var payload = new JObject
                {
                    ["guid"] = guid,
                    ["part"] = originalPartName,
                    ["lastUpdate"] = lastUpdate?.ToString("o"),
                    ["versionToken"] = versionToken,
                    ["hash"] = hash,
                    ["source"] = source
                };

                byte[] rawBytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
                string tmpPath = Path.Combine(subDir, $"{guid}_{partName}.tmp-{System.Guid.NewGuid():N}");
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                {
                    gz.Write(rawBytes, 0, rawBytes.Length);
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

                // Update trigram postings
                var trigrams = TrigramExtractor.ExtractTrigrams(source);
                foreach (var t in trigrams)
                {
                    var set = _trigramIndex.GetOrAdd(t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    lock (set)
                    {
                        set.Add(key);
                    }
                }

                _records[key] = summary;
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

        public bool TryGetStoredAndFresh(SearchIndex.IndexEntry entry, string part, out string source)
        {
            source = null;
            if (entry == null || string.IsNullOrWhiteSpace(entry.Guid)
                || string.IsNullOrWhiteSpace(part))
                return false;

            RecordReadState state;
            string detail;
            string normalizedPart = ObjectService.NormalizeRawSourcePart(
                ObjectService.ResolveSearchPartName(entry.Type, part));
            if (!TryReadStoredRecord(entry.Guid, normalizedPart, out source, out state, out detail))
                return false;
            if (!_records.TryGetValue(MakeKey(entry.Guid, normalizedPart), out var summary))
            {
                source = null;
                return false;
            }
            if (summary.LastUpdate.HasValue
                && entry.LastUpdate > DateTime.MinValue
                && entry.LastUpdate > summary.LastUpdate.Value.AddSeconds(2))
            {
                source = null;
                return false;
            }
            return true;
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
            string source;
            // The summary is only an index.  Freshness is certified by reading
            // and validating the concrete per-part record, including its hash,
            // identity, timestamp and file bounds.
            return TryGetStoredAndFresh(entry, part, out source);
        }

        public SourceStoreCoverage GetCoverage(IEnumerable<SearchIndex.IndexEntry> entries, List<string> scope)
        {
            var coverage = new SourceStoreCoverage();
            if (entries == null) return coverage;

            var entryList = entries as IList<SearchIndex.IndexEntry> ?? entries.ToList();
            coverage.TotalObjects = entryList.Count;

            foreach (var e in entryList)
            {
                if (string.IsNullOrWhiteSpace(e?.Guid)) continue;
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

            return coverage;
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

            var entriesByGuid = new Dictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
            var allowedPartsByGuid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in storedEntries)
            {
                if (string.IsNullOrWhiteSpace(e?.Guid)) continue;
                var requestedParts = ResolveRequestedParts(e.Type, criteria.Scope);
                if (requestedParts.Count == 0 || !IsStoredAndFresh(e, requestedParts)) continue;
                string guid = e.Guid.Trim().ToLowerInvariant();
                entriesByGuid[guid] = e;
                allowedPartsByGuid[guid] = new HashSet<string>(requestedParts, StringComparer.OrdinalIgnoreCase);
            }

            if (entriesByGuid.Count == 0) return hits;

            // Extract trigram candidate sets
            var branches = TrigramExtractor.ExtractRequiredTrigramSets(criteria.Pattern, criteria.Callee);
            var candidateKeys = TrigramExtractor.IntersectPostings(_trigramIndex, branches, _records.Keys);

            // Filter candidates to those belonging to the given storedEntries
            var matchedCandidateKeys = new List<string>();
            foreach (var k in candidateKeys)
            {
                int colon = k.IndexOf(':');
                if (colon > 0)
                {
                    string guid = k.Substring(0, colon);
                    string part = k.Substring(colon + 1);
                    if (entriesByGuid.ContainsKey(guid)
                        && allowedPartsByGuid.TryGetValue(guid, out var allowedParts)
                        && allowedParts.Contains(part))
                    {
                        matchedCandidateKeys.Add(k);
                    }
                }
            }

            if (matchedCandidateKeys.Count == 0) return hits;

            // Parallel verification off-STA
            var bag = new ConcurrentBag<JObject>();
            Parallel.ForEach(matchedCandidateKeys, new ParallelOptions { CancellationToken = ct }, (key, state) =>
            {
                if (ct.IsCancellationRequested || bag.Count >= criteria.MaxResults)
                {
                    state.Stop();
                    return;
                }

                int colon = key.IndexOf(':');
                string guid = key.Substring(0, colon);
                string part = key.Substring(colon + 1);

                if (!entriesByGuid.TryGetValue(guid, out var entry)) return;
                if (!TryGet(guid, part, out string src) || string.IsNullOrEmpty(src)) return;

                string canonicalPart = null;
                if (_records.TryGetValue(key, out var rec) && !string.IsNullOrEmpty(rec.PartName))
                    canonicalPart = rec.PartName;
                if (string.IsNullOrEmpty(canonicalPart))
                    canonicalPart = ObjectService.ResolveSearchPartName(entry.Type, part);
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

                    var recordsArray = new JArray();
                    foreach (var kvp in _records)
                    {
                        var rec = kvp.Value;
                        recordsArray.Add(new JObject
                        {
                            ["k"] = kvp.Key,
                            ["g"] = rec.Guid,
                            ["p"] = rec.PartName,
                            ["u"] = rec.LastUpdate?.ToString("o"),
                            ["v"] = rec.VersionToken,
                            ["h"] = rec.ContentHash,
                            ["f"] = rec.RelativeFilePath,
                            ["b"] = rec.FileBytes,
                            ["s"] = rec.StoredAtUtc.ToString("o")
                        });
                    }

                    var root = new JObject
                    {
                        ["version"] = 1,
                        ["savedAt"] = DateTime.UtcNow.ToString("o"),
                        ["records"] = recordsArray
                    };

                    byte[] bytes = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    {
                        gz.Write(bytes, 0, bytes.Length);
                    }

                    lock (_ioGate)
                    {
                        if (File.Exists(catalogPath)) File.Delete(catalogPath);
                        File.Move(tmpPath, catalogPath);
                    }

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
            if (!File.Exists(catalogPath)) return;

            try
            {
                using (var fs = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    var root = JObject.Parse(json);
                    var array = root["records"] as JArray;
                    if (array == null) return;

                    foreach (var item in array)
                    {
                        string guid = item["g"]?.ToString();
                        string part = item["p"]?.ToString();
                        var updateToken = item["u"];
                        DateTime? u = ParseDateToken(updateToken);
                        string v = item["v"]?.ToString();
                        string h = item["h"]?.ToString();
                        string f = item["f"]?.ToString();
                        long b = item["b"]?.Value<long>() ?? 0;
                        var storedToken = item["s"];
                        DateTime? storedAt = ParseDateToken(storedToken);
                        DateTime s = storedAt ?? DateTime.UtcNow;

                        if (!string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(part))
                        {
                            // Keep even an incomplete summary in memory so freshness
                            // can report it as an invalid/stale part rather than
                            // silently mistaking it for an unvisited object.  The
                            // concrete record validator will reject missing paths,
                            // hashes, or payloads.
                            string canonicalPart = ObjectService.NormalizeRawSourcePart(part);
                            string canonicalKey = MakeKey(guid, canonicalPart);
                            if (string.IsNullOrWhiteSpace(h)
                                || (updateToken != null && updateToken.Type != JTokenType.Null && !u.HasValue)
                                || (storedToken != null && storedToken.Type != JTokenType.Null && !storedAt.HasValue)
                                || b < 0)
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
                                StoredAtUtc = s
                            };
                            _records[canonicalKey] = summary;
                        }
                    }

                    // Build trigrams in parallel across loaded records
                    Parallel.ForEach(_records, kvp =>
                    {
                        if (TryGet(kvp.Value.Guid, kvp.Value.PartName, out string src) && !string.IsNullOrEmpty(src))
                        {
                            var trigrams = TrigramExtractor.ExtractTrigrams(src);
                            foreach (var t in trigrams)
                            {
                                var set = _trigramIndex.GetOrAdd(t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                                lock (set)
                                {
                                    set.Add(kvp.Key);
                                }
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SOURCE-STORE] Error reading catalog: {ex.Message}");
            }
        }

        private void EnforceStorageBudget()
        {
            long maxBytes = Configuration.SourceStoreMaxMB * 1024L * 1024L;
            long currentBytes = 0;
            foreach (var r in _records.Values) currentBytes += r.FileBytes;

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
                    try
                    {
                        string fullPath = Path.Combine(_storeDirectory, rec.RelativeFilePath);
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        currentBytes -= rec.FileBytes;
                    }
                    catch { }
                }
            }
            MarkCatalogDirty();
        }

        private static string ComputeHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
