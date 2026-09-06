using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Services.Structure;

namespace GxMcp.Worker.Services
{
    public class IndexCacheService
    {
        // PERFORMANCE (W-A3): bump the disk-flush throttle so write bursts during bulk index
        // don't thrash. The in-memory index is always current; the on-disk copy is for
        // cold-start only, so a slightly stale snapshot is acceptable.
        private const int FlushThrottleSeconds = 30;

        private SearchIndex _index;
        private string _indexPath;

        // ── Dirty-generation tracking (stale-index-forever fix) ─────────────────
        // Every in-memory mutation bumps _dirtyGeneration; a successful FlushToDisk
        // records the generation it captured at snapshot time into _flushedGeneration.
        // FlushNow() only reports success once the generation that was dirty at its
        // entry is confirmed on disk — so callers (KbService) can gate the meta
        // sidecar on a body that actually contains their changes. _flushedGeneration
        // starts at -1 so a clean index still gets one real write on FlushNow().
        private long _dirtyGeneration = 0;
        private long _flushedGeneration = -1;

        // Plan 003: bare MarkDirty() (no key known at the call site) conservatively marks
        // every shard dirty — used by whole-index replace paths (ReplaceAll/UpdateIndex).
        // MarkDirtyForKey is the precise per-object path (UpdateEntry/RemoveEntry/…) that
        // only dirties the one shard the mutated storage key falls into, which is what
        // lets FlushToDisk skip clean shards instead of re-serializing everything.
        // Ordem importa: shard primeiro, geração depois. Um flush que capturar a geração
        // após o Increment tem garantia (fence do Interlocked) de enxergar os shards já
        // marcados; na ordem inversa, um flush podia capturar a geração nova sem o shard
        // e gravar _flushedGeneration sem a mutação (stale-index-forever).
        internal void MarkDirty() { MarkAllShardsDirty(); System.Threading.Interlocked.Increment(ref _dirtyGeneration); }
        internal void MarkDirtyForKey(string storageKey) { MarkShardDirty(storageKey); System.Threading.Interlocked.Increment(ref _dirtyGeneration); }
        internal long DirtyGeneration => System.Threading.Interlocked.Read(ref _dirtyGeneration);

        // ── Sharded on-disk snapshot (plan 003) ─────────────────────────────────
        // Fixed number of buckets keyed by a stable hash of the storage key ("Type:Name").
        // A flush only re-serializes shards containing entries dirtied since the last
        // flush, so cost scales with dirty-entry count instead of total index size.
        internal const int ShardCount = 16;

        // Which shards have unflushed changes. Starts fully populated: FlushedGeneration
        // begins at -1 (nothing confirmed on disk yet), so until the first real flush
        // succeeds every shard is — by definition — potentially dirty. This also covers
        // the legacy-snapshot migration case: loading an old single-file body doesn't
        // clear any shard, so the very next flush writes the complete sharded layout.
        private readonly ConcurrentDictionary<int, byte> _dirtyShards = MakeAllDirty();
        private static ConcurrentDictionary<int, byte> MakeAllDirty()
        {
            var d = new ConcurrentDictionary<int, byte>();
            for (int i = 0; i < ShardCount; i++) d[i] = 1;
            return d;
        }
        private void MarkAllShardsDirty() { for (int i = 0; i < ShardCount; i++) _dirtyShards[i] = 1; }
        private void MarkShardDirty(string storageKey) { if (!string.IsNullOrEmpty(storageKey)) _dirtyShards[ShardOf(storageKey)] = 1; }

        // FNV-1a 32-bit over the upper-invariant chars. NOT String.GetHashCode — that's
        // randomized per-process in .NET (security hardening), so it can't be used to
        // decide an on-disk shard id that has to mean the same thing across runs.
        internal static int ShardOf(string storageKey)
        {
            if (string.IsNullOrEmpty(storageKey)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < storageKey.Length; i++)
                {
                    char c = storageKey[i];
                    char upper = (c >= 'a' && c <= 'z') ? (char)(c - 32) : char.ToUpperInvariant(c);
                    hash ^= upper;
                    hash *= 16777619;
                }
                return (int)(hash % ShardCount);
            }
        }

        // Test observability: per-shard write counter so a shard-isolation test can assert
        // that dirtying one entry only rewrites that entry's shard file.
        private readonly ConcurrentDictionary<int, long> _shardWriteCounts = new ConcurrentDictionary<int, long>();
        internal long ShardWriteCountForTest(int shardId) => _shardWriteCounts.TryGetValue(shardId, out var v) ? v : 0;
        internal void ResetShardWriteCountsForTest() => _shardWriteCounts.Clear();

        // PERF-02: HasPendingEnrichment used to scan the whole Objects map on every
        // qualifying search — O(n) per call, worst-case a full walk once everything is
        // enriched (no early exit). Cache the result against _dirtyGeneration: any mutation
        // bumps the generation, so a cache hit means Objects is byte-for-byte the same set
        // it was scanned against. Steady state (drained) → cache hit, no scan; during
        // enrichment the generation moves each write but the rescan early-exits at the
        // first un-enriched entry. A Tuple<gen,result> read/written through a volatile
        // reference is torn-read-free. A missed generation bump can only cost an extra
        // scan, never a wrong-but-cached answer.
        private volatile System.Tuple<long, bool> _pendingEnrichCache;
        internal long FlushedGeneration => System.Threading.Interlocked.Read(ref _flushedGeneration);
        internal bool IsFullyFlushed => FlushedGeneration >= DirtyGeneration;
        // PERFORMANCE (W-A3): mirror path with .gz extension. Derived from _indexPath so
        // the two never drift; new flushes go here gzipped, legacy plain-JSON still readable.
        private string _indexPathGz => _indexPath + ".gz";
        // Plan 003: sharded snapshot directory + manifest, derived from _indexPath the same
        // way _indexPathGz is so the paths never drift relative to each other.
        private string _shardDirPath => string.IsNullOrEmpty(_indexPath) ? null : _indexPath + "_shards";
        private string _shardManifestPath => string.IsNullOrEmpty(_shardDirPath) ? null : Path.Combine(_shardDirPath, "manifest.json");
        private string ShardFilePath(int shardId) => Path.Combine(_shardDirPath, string.Format("shard_{0:00}.json.gz", shardId));

        // Test observability only — lets tests locate on-disk shard/manifest files without
        // duplicating the path-derivation logic above.
        internal string ShardDirPathForTest => _shardDirPath;
        internal string ShardManifestPathForTest => _shardManifestPath;
        internal string ShardFilePathForTest(int shardId) => ShardFilePath(shardId);
        internal string IndexPathForTest => _indexPath;
        internal string IndexPathGzForTest => _indexPathGz;
        private BuildService _buildService;
        private bool _initialized = false;
        private readonly object _lock = new object();
        private DateTime _lastFlushTime = DateTime.MinValue;
        private bool _savingInProgress = false;
        // PERFORMANCE (W-M2): track consecutive flush failures so a silently failing
        // disk (full / locked / permission) surfaces through whoami instead of going
        // unnoticed until the next cold start finds a stale snapshot.
        private static int _consecutiveFlushFailures = 0;
        private static DateTime _lastFlushSuccessUtc = DateTime.MinValue;
        private static string _lastFlushErrorMessage = null;
        public static int ConsecutiveFlushFailures => System.Threading.Volatile.Read(ref _consecutiveFlushFailures);
        public static DateTime LastFlushSuccessUtc => _lastFlushSuccessUtc;
        public static string LastFlushErrorMessage => _lastFlushErrorMessage;

        // Fase 0 instrumentation: per-sub-step enrichment timing accumulators (raw
        // Stopwatch ticks, Interlocked because the ref/textual scans run on the
        // background dispatcher thread). These decide whether Fase 3 parallelization
        // is worth it: typeExtract+embedding are CPU-only (parallelizable), refScan is
        // STA-bound (GetReferences). Snapshot via GetEnrichTimingSummary() at drain end.
        // NOTE: refScan/textualScan are enqueued on Program.EnqueueBackground and may
        // still be accumulating when [ENRICH-DONE] is logged — read as "work so far".
        private static long _enrichTypeExtractTicks = 0;
        private static long _enrichEmbeddingTicks = 0;
        private static long _enrichRefScanTicks = 0;
        private static long _enrichTextualScanTicks = 0;
        public static string GetEnrichTimingSummary()
        {
            double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long te = System.Threading.Interlocked.Read(ref _enrichTypeExtractTicks);
            long em = System.Threading.Interlocked.Read(ref _enrichEmbeddingTicks);
            long rs = System.Threading.Interlocked.Read(ref _enrichRefScanTicks);
            long ts = System.Threading.Interlocked.Read(ref _enrichTextualScanTicks);
            return $"typeExtractMs={(long)(te * tickToMs)} embeddingMs={(long)(em * tickToMs)} refScanMs={(long)(rs * tickToMs)} textualScanMs={(long)(ts * tickToMs)}";
        }

        private readonly VectorService _vectorService = new VectorService();

        // PERFORMANCE (W-M5): cache resolved hierarchy by object Guid to avoid re-walking
        // the parent chain on repeated UpdateEntry calls for the same object. Invalidated
        // on Clear/RemoveEntry. Bulk-index hits each Guid once so the win comes from
        // incremental re-indexing after saves.
        private readonly ConcurrentDictionary<Guid, (string ParentName, string ParentPath, string Path, string ModuleName)> _hierarchyCache
            = new ConcurrentDictionary<Guid, (string, string, string, string)>();

        // v2.3.8 (Task 1.1): unified IndexState surface so downstream services (whoami,
        // search, analyze) share a single source of truth for index readiness.
        private IndexState _state = new IndexState { Status = "Cold", TotalObjects = 0 };
        private readonly object _stateLock = new object();

        // issue #25 #1: an index-state-change signal so a `lifecycle status wait`
        // call can block and return the moment the state transitions (or a walk
        // progress tick lands) instead of polling. Fires on every Mark* transition
        // and on MarkReindexProgress. Arm-then-check pattern in the caller avoids
        // missing a transition between the state read and the wait.
        private readonly System.Threading.ManualResetEventSlim _stateSignal =
            new System.Threading.ManualResetEventSlim(false);
        private void SignalStateChanged() { try { _stateSignal.Set(); } catch { } }
        /// <summary>Arm the state-change signal before reading state, so a change
        /// that lands before the subsequent WaitStateSignal is not missed.</summary>
        public void ArmStateSignal() { try { _stateSignal.Reset(); } catch { } }
        /// <summary>Block up to <paramref name="timeoutMs"/> for the next index-state
        /// transition or walk progress tick. Returns true if signalled, false on timeout.</summary>
        public bool WaitStateSignal(int timeoutMs)
        {
            if (timeoutMs <= 0) return false;
            try { return _stateSignal.Wait(timeoutMs); } catch { return false; }
        }

        public IndexState GetState()
        {
            lock (_stateLock)
            {
                return new IndexState
                {
                    Status = _state.Status,
                    LastIndexedAt = _state.LastIndexedAt,
                    TotalObjects = _state.TotalObjects,
                    Progress = _state.Progress,
                    EtaMs = _state.EtaMs,
                    LitePassCompletedUtc = _state.LitePassCompletedUtc,
                    EnrichmentStartedUtc = _state.EnrichmentStartedUtc
                };
            }
        }

        public void MarkReindexStarted(int totalEstimated)
        {
            lock (_stateLock)
            {
                _state.Status = "Reindexing";
                _state.Progress = 0;
                _state.EtaMs = null;
                _state.LastIndexedAt = null;
                _state.TotalObjects = totalEstimated;
            }
            // Fase 0: re-arm the once-per-build [TIME-TO-USABLE] guards so a repeated
            // force-reindex in the same process logs the milestones again.
            System.Threading.Interlocked.Exchange(ref _loggedUltraLiteUsable, 0);
            System.Threading.Interlocked.Exchange(ref _loggedLiteUsable, 0);
            // Reset the enrichment-timing accumulators so [INDEX-SAVE]/[ENRICH-DONE] report
            // per-build cost, not a process-global cumulative total across multiple builds.
            System.Threading.Interlocked.Exchange(ref _enrichTypeExtractTicks, 0);
            System.Threading.Interlocked.Exchange(ref _enrichEmbeddingTicks, 0);
            System.Threading.Interlocked.Exchange(ref _enrichRefScanTicks, 0);
            System.Threading.Interlocked.Exchange(ref _enrichTextualScanTicks, 0);
            SignalStateChanged();
        }

        public void MarkReindexProgress(double progress, int etaMs)
        {
            lock (_stateLock)
            {
                _state.Progress = progress;
                _state.EtaMs = etaMs;
            }
            SignalStateChanged();
        }

        public void MarkIndexFailed()
        {
            lock (_stateLock)
            {
                _state.Status = "Cold";
                _state.Progress = null;
                _state.EtaMs = null;
            }
            SignalStateChanged();
        }

        public void MarkIndexComplete(int totalObjects)
        {
            lock (_stateLock)
            {
                _state.Status = "Ready";
                _state.LastIndexedAt = DateTime.UtcNow;
                _state.TotalObjects = totalObjects;
                _state.Progress = null;
                _state.EtaMs = null;
            }
            SignalStateChanged();
        }

        // Fase 0: log [TIME-TO-USABLE] once per usable milestone so the SM warmup
        // (captured in sdkReadyMs) is cleanly separated from the object walk
        // (sinceSdkReadyMs). processMs is absolute since process start.
        private static int _loggedUltraLiteUsable = 0;
        private static int _loggedLiteUsable = 0;

        public void MarkLitePassComplete(int totalObjects)
        {
            lock (_stateLock)
            {
                _state.Status = "LiteReady";
                _state.TotalObjects = totalObjects;
                _state.LitePassCompletedUtc = DateTime.UtcNow;
                _state.Progress = 1.0;
                _state.EtaMs = 0;
            }
            if (System.Threading.Interlocked.Exchange(ref _loggedLiteUsable, 1) == 0)
            {
                long p = Program.ProcessElapsedMs;
                Logger.Info($"[TIME-TO-USABLE] event=liteReady processMs={p} sdkReadyMs={Program.SdkReadyAtMs} sinceSdkReadyMs={Math.Max(0, p - Program.SdkReadyAtMs)} objects={totalObjects}");
            }
            SignalStateChanged();
        }

        // v2.6.9 perf: signal that the lite walk has emitted enough entries to
        // make list_objects / query / inspect useful, even though it hasn't
        // walked the full catalogue yet. Status stays at "UltraLiteReady" until
        // MarkLitePassComplete promotes it to "LiteReady". Partial flag lets
        // ListService surface a `partial:true` hint so the agent knows the
        // catalogue is still growing.
        public void MarkUltraLiteReady(int objectsSoFar)
        {
            lock (_stateLock)
            {
                // Don't downgrade if we've already advanced past UltraLite.
                if (_state.Status == "LiteReady" || _state.Status == "Enriching" || _state.Status == "Ready")
                    return;
                _state.Status = "UltraLiteReady";
                _state.TotalObjects = objectsSoFar;
                _state.Progress = null;
                _state.EtaMs = null;
            }
            if (System.Threading.Interlocked.Exchange(ref _loggedUltraLiteUsable, 1) == 0)
            {
                long p = Program.ProcessElapsedMs;
                Logger.Info($"[TIME-TO-USABLE] event=ultraLiteReady processMs={p} sdkReadyMs={Program.SdkReadyAtMs} sinceSdkReadyMs={Math.Max(0, p - Program.SdkReadyAtMs)} objects={objectsSoFar}");
            }
            SignalStateChanged();
        }

        public void MarkEnrichmentStarted()
        {
            lock (_stateLock)
            {
                _state.Status = "Enriching";
                _state.EnrichmentStartedUtc = DateTime.UtcNow;
                _state.Progress = 0;
            }
            SignalStateChanged();
        }

        // issue #25 follow-up (P3): surface Enriching-phase progress. Previously only
        // Reindexing set Progress/EtaMs, so an agent polling during enrichment saw a
        // fixed "Enriching" with no denominator. Now Progress reflects enriched/total
        // and EtaMs is a linear estimate from elapsed time.
        public void MarkEnrichmentProgress(int processed, int total)
        {
            if (total <= 0) return;
            double frac = Math.Min(1.0, (double)processed / total);
            int? eta = null;
            lock (_stateLock)
            {
                if (_state.Status != "Enriching") return; // don't clobber a later state
                _state.Progress = frac;
                if (_state.EnrichmentStartedUtc.HasValue && processed > 0 && frac > 0)
                {
                    double elapsedMs = (DateTime.UtcNow - _state.EnrichmentStartedUtc.Value).TotalMilliseconds;
                    eta = (int)Math.Max(0, (elapsedMs / frac) - elapsedMs);
                    _state.EtaMs = eta;
                }
            }
            SignalStateChanged();
        }

        public IndexCacheService()
        {
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

        // v2.3.8 (Task 1.3): test-only seam. Bypasses disk load so unit tests for
        // graph services (CallerGraphService) can drive a deterministic in-memory
        // index without spinning up a KB. Not intended for production code paths.
        internal void LoadFromEntries(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            LoadFromEntries(entries, markReady: true);
        }

        // markReady=false leaves the state where it was (Cold by default) so tests
        // can drive a partial state (MarkUltraLiteReady/MarkLitePassComplete) after
        // loading a fixture — MarkIndexComplete would otherwise pin it to Ready and
        // the downgrade guard would block the transition.
        internal void LoadFromEntries(IEnumerable<SearchIndex.IndexEntry> entries, bool markReady)
        {
            lock (_lock)
            {
                var idx = new SearchIndex();
                if (entries != null)
                {
                    foreach (var e in entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                        string type = string.IsNullOrEmpty(e.Type) ? "Object" : e.Type;
                        string key = type + ":" + e.Name;
                        idx.Objects[key] = e;
                    }
                }
                idx.LastUpdated = DateTime.UtcNow;
                idx.GraphRevision = 1;
                _index = idx;
                _initialized = true;
                PrimeHierarchyCacheFromIndex(idx);
            }
            // v2.6.9 perf: flip state to Ready so the gateway-side / list-service
            // fast-fail path doesn't treat fixture-loaded indexes as still-building.
            if (markReady) MarkIndexComplete(_index.Objects.Count);
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public KbService KbService => _buildService?.KbService;

        // Name-keyed projection of the index, rebuilt lazily when the underlying
        // _index reference changes. Lookups are O(1); previously each call
        // iterated up to 38k entries (review-finding: build with 100 error lines
        // hammered the index ~3.8M times). _byNameIndex is invalidated by
        // setting _byNameIndexFor = null whenever _index is replaced.
        private Dictionary<string, SearchIndex.IndexEntry> _byNameIndex;
        private SearchIndex _byNameIndexFor;
        private readonly object _byNameLock = new object();

        private Dictionary<string, SearchIndex.IndexEntry> GetByNameIndex()
        {
            var idx = GetIndex();
            if (idx?.Objects == null) return null;
            lock (_byNameLock)
            {
                if (!ReferenceEquals(idx, _byNameIndexFor) || _byNameIndex == null)
                {
                    var built = new Dictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in idx.Objects.Values)
                    {
                        if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                        // Last-write-wins on name collision (rare; storage key
                        // is Type:Name so two objects can share a bare Name).
                        built[v.Name] = v;
                    }
                    _byNameIndex = built;
                    _byNameIndexFor = idx;
                }
                return _byNameIndex;
            }
        }

        // v2.6.6 Stream E (FR#7): index-aware verification used by the normalizer
        // (BuildService.NormalizeMissingObjectName) and by the orphan-sweep /
        // CS2001-demotion paths.
        public bool IsObjectKnown(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                var byName = GetByNameIndex();
                return byName != null && byName.ContainsKey(name);
            }
            catch (Exception ex)
            {
                Logger.Warn("[IsObjectKnown] lookup failed for '" + name + "': " + ex.Message);
                return false;
            }
        }

        // v2.6.6 Stream E (FR#3/#9): companion to IsObjectKnown — returns the
        // first index entry matching the case-insensitive Name, or null.
        public SearchIndex.IndexEntry TryGetEntryByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var byName = GetByNameIndex();
                if (byName == null) return null;
                byName.TryGetValue(name, out var entry);
                return entry;
            }
            catch (Exception ex)
            {
                Logger.Warn("[TryGetEntryByName] lookup failed for '" + name + "': " + ex.Message);
                return null;
            }
        }

        /// <summary>Returns every typed entry sharing a bare name.</summary>
        public IReadOnlyList<SearchIndex.IndexEntry> FindEntriesByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Array.Empty<SearchIndex.IndexEntry>();
            try
            {
                var idx = GetIndex();
                if (idx?.Objects == null) return Array.Empty<SearchIndex.IndexEntry>();
                if (idx.ByNameIndex != null && idx.ByNameIndex.TryGetValue(name, out var keys))
                    return keys.Where(key => idx.Objects.TryGetValue(key, out var entry) && entry != null)
                        .Select(key => idx.Objects[key]).ToList();
                // Test/legacy snapshots may not carry derived indexes yet. Keep
                // correctness while the next rebuild creates the multimap.
                return idx.Objects.Values.Where(entry => entry != null
                    && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn("[FindEntriesByName] lookup failed for '" + name + "': " + ex.Message);
                return Array.Empty<SearchIndex.IndexEntry>();
            }
        }

         public bool IsIndexMissing
         {
             get
             {
                 try
                 {
                     if (string.IsNullOrEmpty(_indexPath)) return true;
                     // PERFORMANCE (W-A3): accept the gzipped (legacy) snapshot, the plain
                     // (older legacy) snapshot, or (plan 003) a sharded snapshot's manifest.
                     bool shardManifestPresent = !string.IsNullOrEmpty(_shardManifestPath) && File.Exists(_shardManifestPath);
                     if (!File.Exists(_indexPathGz) && !File.Exists(_indexPath) && !shardManifestPresent) return true;

                     var index = GetIndex();
                     return index == null || index.Objects.Count == 0;
                 }
                 catch { return true; }
             }
         }
 
         public bool IsScanning => KbService?.IsIndexing ?? false;

         public bool IsInitialized => _initialized;

        // Public so the index build (KbService.BulkIndex) can force the on-disk cache path
        // to be resolved BEFORE any read/write. Otherwise force=true (which never touches
        // GetIndex/IsIndexMissing) writes to the constructor's <BaseDir>\cache\search_index.*
        // fallback while the warm-start read path resolves the %LOCALAPPDATA%\…\index_{hash}.*
        // path — so the sidecar written by one is never found by the other (metaPresent=False,
        // delta never engages).
        // proactiveLoad=false resolves the on-disk cache path WITHOUT kicking the background
        // GetIndex() warm load. BulkIndex(force=true) needs the path resolved (so the right
        // %LOCALAPPDATA%\…\index_{hash}.* files get deleted) but must NOT start a background
        // load that would race Clear()/DeleteOnDiskSnapshot() and briefly republish the stale
        // snapshot as Ready.
        public void EnsureInitialized(bool proactiveLoad = true)
        {
            if (_initialized) return;
            try
            {
                string kbPath = _buildService.GetKBPath();
                if (!string.IsNullOrEmpty(kbPath)) Initialize(kbPath, proactiveLoad);
            }
            catch { }
        }

        public void Initialize(string kbPath, bool proactiveLoad = true)
        {
            if (string.IsNullOrEmpty(kbPath)) return;
            if (kbPath.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase)) kbPath = Path.GetDirectoryName(kbPath);

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string cacheDir = Path.Combine(localAppData, "GxMcp", "Cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string hash = GetHash(kbPath);
                _indexPath = Path.Combine(cacheDir, string.Format("index_{0}.json", hash));
                _initialized = true;
                // Fase 1 diagnostic: log the resolved cache paths so a sidecar-not-found on warm
                // start (metaPresent=False) can be traced to a hash/path mismatch between runs.
                Logger.Info(string.Format("[INDEX-CACHE-PATHS] kbPathIn={0} hash={1} gz={2} meta={3}", kbPath, hash, _indexPathGz, _metaPath));

                // PERFORMANCE: Pro-active loading in background. Skipped when proactiveLoad=false
                // (force reindex) so it doesn't race the imminent Clear()/DeleteOnDiskSnapshot().
                if (proactiveLoad) Task.Run(() => GetIndex());
            }
            catch (Exception ex) { Logger.Error("IndexCache Init Error: " + ex.Message); }
        }

        private string GetHash(string input)
        {
            // Fase 1: canonicalize the KB path so the same KB always maps to the same cache
            // hash across worker runs — otherwise a sidecar written under one path-spelling
            // (trailing slash, casing, forward vs back slashes) isn't found under another and
            // the warm-start delta never engages (metaPresent=False).
            // NOTE (path-safety consolidation): no PathSafety.TryResolveWithinRoot/MakeRelative call
            // here — `input` (the KB path itself, from BuildService.GetKBPath()) is being
            // canonicalized to build a cache-key hash, not resolved against a root or turned into a
            // relative path. There's no "root" in this operation, so the shared helper doesn't apply.
            string canonical = input ?? string.Empty;
            try { canonical = Path.GetFullPath(canonical); } catch { /* keep raw on malformed paths */ }
            canonical = canonical.TrimEnd('\\', '/').ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 16);
            }
        }

        private void BuildParentIndex(SearchIndex index)
        {
            var byParent = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>>(StringComparer.OrdinalIgnoreCase);
            // O(1) dedup companion, built in lockstep with byParent (see AddOrUpdateEntryInParentIndex).
            var keysByParent = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // Fase 2: (re)build the Guid → storage-key map alongside the parent index — both
            // derive from a single full pass over Objects, so do them together.
            var guidToKey = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Plan 002: Type/BusinessDomain derived indexes, built in the same pass.
            var typeIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var domainIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // PERF (perf-review): Name → storage keys multimap (all entries sharing a
            // bare Name across types), so usedby:/name lookups skip the O(n) scan.
            var nameIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in index.Objects)
            {
                var entry = kv.Value;
                string parent = entry.ParentPath ?? entry.Parent ?? "";
                if (!byParent.TryGetValue(parent, out var list))
                {
                    list = new System.Collections.Generic.List<SearchIndex.IndexEntry>();
                    byParent[parent] = list;
                    keysByParent[parent] = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                // PERFORMANCE: Since we are iterating dictionary values, there are NO duplicates.
                // Using .Add() directly changes this from an O(N^2) operation to O(N).
                list.Add(entry);
                string storageKey = GetEntryStorageKey(entry);
                keysByParent[parent].Add(storageKey);

                if (!string.IsNullOrEmpty(entry.Guid)) guidToKey[entry.Guid] = kv.Key;

                if (!string.IsNullOrWhiteSpace(entry.Type))
                {
                    typeIndex.GetOrAdd(entry.Type, _ => new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(storageKey);
                }
                if (!string.IsNullOrWhiteSpace(entry.BusinessDomain))
                {
                    domainIndex.GetOrAdd(entry.BusinessDomain, _ => new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(storageKey);
                }
                if (!string.IsNullOrWhiteSpace(entry.Name))
                {
                    nameIndex.GetOrAdd(entry.Name, _ => new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(storageKey);
                }
            }
            index.ChildrenByParent = byParent;
            index.ChildKeysByParent = keysByParent;
            index.GuidToKey = guidToKey;
            index.TypeIndex = typeIndex;
            index.DomainIndex = domainIndex;
            index.ByNameIndex = nameIndex;
            if (index.GraphRevision <= 0)
            {
                index.GraphRevision = 1;
            }
        }

        private static void TouchGraph(SearchIndex index)
        {
            if (index != null)
            {
                System.Threading.Interlocked.Increment(ref index.GraphRevision);
            }
        }

        // Plan 002: maintain TypeIndex/DomainIndex in the same incremental hooks that
        // maintain ChildrenByParent (AddOrUpdateEntryInParentIndex / RemoveEntryFromParentIndex),
        // so every mutation path that already keeps the parent index current (UpdateEntry,
        // AddOrUpdateBatch, RemoveEntry/RemoveEntryByGuid, the rename-collapse branch) picks
        // this up for free without touching each call site individually.
        private void AddOrUpdateEntryInSecondaryIndexes(SearchIndex index, SearchIndex.IndexEntry entry)
        {
            if (entry == null) return;
            string entryKey = GetEntryStorageKey(entry);

            if (index?.TypeIndex != null && !string.IsNullOrWhiteSpace(entry.Type))
            {
                var set = index.TypeIndex.GetOrAdd(entry.Type, _ => new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (set) { set.Add(entryKey); }
            }
            if (index?.DomainIndex != null && !string.IsNullOrWhiteSpace(entry.BusinessDomain))
            {
                var set = index.DomainIndex.GetOrAdd(entry.BusinessDomain, _ => new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (set) { set.Add(entryKey); }
            }
            if (index?.ByNameIndex != null && !string.IsNullOrWhiteSpace(entry.Name))
            {
                var set = index.ByNameIndex.GetOrAdd(entry.Name, _ => new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (set) { set.Add(entryKey); }
            }
        }

        private void RemoveEntryFromSecondaryIndexes(SearchIndex index, SearchIndex.IndexEntry entry)
        {
            if (entry == null) return;
            string entryKey = GetEntryStorageKey(entry);

            if (index?.TypeIndex != null && !string.IsNullOrWhiteSpace(entry.Type)
                && index.TypeIndex.TryGetValue(entry.Type, out var typeSet))
            {
                lock (typeSet) { typeSet.Remove(entryKey); }
            }
            if (index?.DomainIndex != null && !string.IsNullOrWhiteSpace(entry.BusinessDomain)
                && index.DomainIndex.TryGetValue(entry.BusinessDomain, out var domainSet))
            {
                lock (domainSet) { domainSet.Remove(entryKey); }
            }
            if (index?.ByNameIndex != null && !string.IsNullOrWhiteSpace(entry.Name)
                && index.ByNameIndex.TryGetValue(entry.Name, out var nameSet))
            {
                lock (nameSet) { nameSet.Remove(entryKey); }
            }
        }

        private string GetEntryStorageKey(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;
            // PERFORMANCE (W-B1): cache the computed key on the entry to avoid repeated
            // string.Format in AddOrUpdateEntryInParentIndex's List.Any lookup.
            if (!string.IsNullOrEmpty(entry.StorageKey)) return entry.StorageKey;

            string key;
            if (string.Equals(entry.Type, "Folder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Type, "Module", StringComparison.OrdinalIgnoreCase))
            {
                string scopedPath = entry.Path ?? entry.Name ?? string.Empty;
                key = string.Format("{0}:{1}", entry.Type ?? string.Empty, scopedPath);
            }
            else
            {
                key = string.Format("{0}:{1}", entry.Type ?? string.Empty, entry.Name ?? string.Empty);
            }
            entry.StorageKey = key;
            return key;
        }

        private void NormalizeLegacyHierarchy(SearchIndex index)
        {
            if (index?.Objects == null) return;

            foreach (var entry in index.Objects.Values)
            {
                if (string.IsNullOrWhiteSpace(entry.ParentPath))
                {
                    entry.ParentPath = InferLegacyParentPath(entry);
                }

                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    entry.Path = InferLegacyPath(entry);
                }

                if (string.IsNullOrWhiteSpace(entry.ParentFolderPath))
                {
                    entry.ParentFolderPath = ComposeParentFolderPath(entry.ParentPath);
                }
            }
        }

        private string InferLegacyParentPath(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;

            if (string.IsNullOrWhiteSpace(entry.Parent) ||
                string.Equals(entry.Parent, "Root Module", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(entry.Module) &&
                string.Equals(entry.Parent, entry.Module, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Module;
            }

            // When an entry lives inside a Folder under a Module, qualify the
            // folder name with its parent module to preserve hierarchy. Without
            // this, folders sharing names across modules (e.g. "Procs") collapse
            // into a single bucket in the parent index.
            if (!string.IsNullOrWhiteSpace(entry.Module))
            {
                return string.Format("{0}/{1}", entry.Module, entry.Parent);
            }

            return entry.Parent;
        }

        // v2.3.8 (Task 2.2): full folder path always prefixed with the synthetic
        // "Root Module" so callers can pathPrefix-filter by the same string they
        // see in the GeneXus IDE. Empty hierarchy.ParentPath means object lives
        // directly under DesignModel -> we surface "Root Module" as the folder.
        private static string ComposeParentFolderPath(string hierarchyParentPath)
        {
            if (string.IsNullOrWhiteSpace(hierarchyParentPath))
                return "Root Module";
            // Already prefixed? Be tolerant — never double-prefix.
            if (hierarchyParentPath.Equals("Root Module", StringComparison.OrdinalIgnoreCase) ||
                hierarchyParentPath.StartsWith("Root Module/", StringComparison.OrdinalIgnoreCase))
                return hierarchyParentPath;
            return "Root Module/" + hierarchyParentPath;
        }

        private string InferLegacyPath(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;

            var parentPath = entry.ParentPath ?? InferLegacyParentPath(entry);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return entry.Name ?? string.Empty;
            }

            return string.Format("{0}/{1}", parentPath, entry.Name ?? string.Empty);
        }

        private void AddOrUpdateEntryInParentIndex(SearchIndex index, SearchIndex.IndexEntry entry)
        {
            if (index?.ChildrenByParent == null || entry == null) return;
            string parent = entry.ParentPath ?? entry.Parent ?? "";
            var list = index.ChildrenByParent.GetOrAdd(parent, _ => new System.Collections.Generic.List<SearchIndex.IndexEntry>());
            var keys = index.ChildKeysByParent?.GetOrAdd(parent, _ => new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase));

            lock (list)
            {
                string entryKey = GetEntryStorageKey(entry);
                // O(1) dedup via the companion key-set instead of an O(n) List.Any scan.
                // HashSet.Add returns false when the key is already present. Fall back to the
                // linear scan only if the companion set is somehow absent (defensive).
                bool isNew = keys != null
                    ? keys.Add(entryKey)
                    : !list.Any(e => string.Equals(GetEntryStorageKey(e), entryKey, StringComparison.OrdinalIgnoreCase));
                if (isNew)
                {
                    list.Add(entry);
                }
            }
            AddOrUpdateEntryInSecondaryIndexes(index, entry);
        }

        // Fase 2: drop an entry from its parent-children list (used by rename collapse and
        // RemoveEntryByGuid). Matches by storage key under the same per-list lock the add path uses.
        private void RemoveEntryFromParentIndex(SearchIndex index, SearchIndex.IndexEntry entry)
        {
            if (index?.ChildrenByParent == null || entry == null) return;
            string parent = entry.ParentPath ?? entry.Parent ?? "";
            if (!index.ChildrenByParent.TryGetValue(parent, out var list)) return;
            var keys = index.ChildKeysByParent != null && index.ChildKeysByParent.TryGetValue(parent, out var k) ? k : null;
            string entryKey = GetEntryStorageKey(entry);
            lock (list)
            {
                list.RemoveAll(e => string.Equals(GetEntryStorageKey(e), entryKey, StringComparison.OrdinalIgnoreCase));
                keys?.Remove(entryKey);
            }
            RemoveEntryFromSecondaryIndexes(index, entry);
        }

        // Fase 2: remove an object by its (stable) Guid — used by the warm-start deletion
        // sweep when an object that was in the persisted index no longer exists in the KB.
        // Resolves the storage key via GuidToKey so renames don't strand it.
        // Called after an object's folder/module parent changes (genexus_properties
        // action=move / create-with-folder). The per-Guid hierarchy cache and the object's
        // ChildrenByParent slot are keyed on the OLD parent; the move leaves the Type:Name
        // storage key unchanged, so a plain UpdateEntry would reuse the cached (stale) parent
        // and never drop the old-parent child entry. Drop both here so the following
        // UpdateEntry re-walks the parent chain and re-files the entry under the new parent.
        public void InvalidateHierarchy(Guid guid)
        {
            _hierarchyCache.TryRemove(guid, out _);
            var index = _index;
            if (index?.GuidToKey == null || index.Objects == null) return;
            lock (_lock)
            {
                if (index.GuidToKey.TryGetValue(guid.ToString(), out var key)
                    && index.Objects.TryGetValue(key, out var existing)
                    && index.ChildrenByParent != null)
                {
                    RemoveEntryFromParentIndex(index, existing);
                }
            }
        }

        public void RemoveEntryByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            var index = GetIndex();
            string removedKey = null;
            lock (_lock)
            {
                if (index.GuidToKey == null || !index.GuidToKey.TryGetValue(guid, out var key)) return;
                if (index.Objects.TryRemove(key, out var removed))
                {
                    removedKey = key;
                    TouchGraph(index);
                    if (index.ChildrenByParent != null) RemoveEntryFromParentIndex(index, removed);
                    if (Guid.TryParse(guid, out var g)) _hierarchyCache.TryRemove(g, out _);
                }
                index.GuidToKey.TryRemove(guid, out _);
            }
            MarkDirtyForKey(removedKey);
        }

        private (string ParentName, string ParentPath, string Path, string ModuleName) ResolveHierarchy(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            // PERFORMANCE (W-M5): fast-path for objects whose hierarchy has already been resolved.
            if (obj != null && _hierarchyCache.TryGetValue(obj.Guid, out var cached))
            {
                return cached;
            }

            string parentName = string.Empty;
            string moduleName = null;
            var parentSegments = new System.Collections.Generic.List<string>();

            try
            {
                dynamic currentParent = obj?.Parent;
                bool isImmediateParent = true;

                while (currentParent != null)
                {
                    try
                    {
                        if (currentParent.Guid == obj.Guid)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    string parentTypeName = null;
                    try { parentTypeName = currentParent.TypeDescriptor?.Name; } catch { }

                    if (string.Equals(parentTypeName, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isImmediateParent)
                        {
                            parentName = "Root Module";
                        }
                        break;
                    }

                    bool isContainer =
                        currentParent is global::Artech.Architecture.Common.Objects.Module ||
                        currentParent is global::Artech.Architecture.Common.Objects.Folder;

                    if (!isContainer)
                    {
                        currentParent = currentParent.Parent;
                        isImmediateParent = false;
                        continue;
                    }

                    string currentName = null;
                    try { currentName = currentParent.Name; } catch { }

                    if (!string.IsNullOrWhiteSpace(currentName))
                    {
                        parentSegments.Insert(0, currentName);
                        if (isImmediateParent)
                        {
                            parentName = currentName;
                        }

                        if (moduleName == null &&
                            currentParent is global::Artech.Architecture.Common.Objects.Module)
                        {
                            moduleName = currentName;
                        }
                    }

                    currentParent = currentParent.Parent;
                    isImmediateParent = false;
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(moduleName))
            {
                try
                {
                    if (obj?.Module != null && obj.Module.Guid != obj.Guid)
                    {
                        moduleName = obj.Module.Name;
                    }
                }
                catch
                {
                }
            }

            // Some modular SDK objects expose Module but not the full Parent chain
            // during the lite walk. Keep the module in the qualified path so identity
            // lookup remains deterministic after a cache rebuild.
            if (parentSegments.Count == 0 && !string.IsNullOrWhiteSpace(moduleName))
            {
                parentSegments.Add(moduleName);
                parentName = moduleName;
            }

            string parentPath = string.Join("/", parentSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
            string path = parentPath;
            if (!string.IsNullOrWhiteSpace(obj?.Name))
            {
                path = string.IsNullOrEmpty(parentPath) ? obj.Name : parentPath + "/" + obj.Name;
            }

            var result = (parentName, parentPath, path, moduleName);
            if (obj != null)
            {
                _hierarchyCache[obj.Guid] = result;
            }
            return result;
        }

        // v2.6.9 perf: non-blocking probe. Returns the in-memory index if it's
        // already loaded, otherwise returns null without taking the loader lock.
        // Use this from hot paths (list_objects, query) that prefer to fast-fail
        // with an "Indexing" envelope rather than block 30-60s on a cold load.
        // GetIndex() retains the blocking-load behaviour for callers that
        // genuinely need the index synchronously.
        public SearchIndex TryGetLoadedIndex()
        {
            return _index;
        }

        // Best-effort: kick off the background load if it hasn't started yet.
        // Idempotent — safe to call repeatedly. Caller still uses
        // TryGetLoadedIndex() to test readiness without blocking.
        public void EnsureLoadStarted()
        {
            if (_index != null) return;
            try { EnsureInitialized(); } catch { /* best-effort */ }
            try { System.Threading.Tasks.Task.Run(() => GetIndex()); } catch { /* best-effort */ }
        }

        public SearchIndex GetIndex()
        {
            if (_index != null) return _index;
            EnsureInitialized();

            lock (_lock)
            {
                if (_index != null) return _index;
                try
                {
                    // Plan 003: prefer the sharded snapshot (manifest present = the shard
                    // directory is trustworthy); fall back to the legacy single-file gz/plain
                    // snapshot so existing installs keep working without re-indexing. Loading
                    // the legacy body doesn't clear any shard's dirty flag, so the very next
                    // flush re-emits it as a sharded snapshot (silent migration).
                    SearchIndex loaded = null;
                    if (!string.IsNullOrEmpty(_shardManifestPath) && File.Exists(_shardManifestPath))
                    {
                        Logger.Debug(string.Format("Loading sharded index from disk: {0}", _shardDirPath));
                        loaded = LoadShardedIndex();
                    }
                    else
                    {
                        string json = null;
                        if (File.Exists(_indexPathGz))
                        {
                            Logger.Debug(string.Format("Loading gzipped index from disk: {0}", _indexPathGz));
                            json = ReadGzippedText(_indexPathGz);
                        }
                        else if (File.Exists(_indexPath))
                        {
                            Logger.Debug(string.Format("Loading legacy plain index from disk: {0}", _indexPath));
                            json = File.ReadAllText(_indexPath);
                        }
                        if (!string.IsNullOrEmpty(json)) loaded = SearchIndex.FromJson(json);
                    }

                    if (loaded != null)
                    {
                        _index = loaded;
                        NormalizeLegacyHierarchy(_index);
                        BuildParentIndex(_index);
                        PrimeHierarchyCacheFromIndex(_index);
                        Logger.Info(string.Format("Index loaded. Objects: {0}", _index.Objects.Count));
                        // v2.3.8 (post-Task 1.2 fix): when we hydrate the in-memory index
                        // from the on-disk cache (warm start), publish Ready to IndexState
                        // so whoami doesn't keep reporting Cold while list/search hit a
                        // fully-populated index. Without this the state machine only
                        // transitioned via BulkIndex's MarkIndexComplete, which is skipped
                        // on warm starts (AlreadyIndexed path in KbService.BulkIndex).
                        if (_index.Objects.Count > 0)
                        {
                            MarkIndexComplete(_index.Objects.Count);
                        }
                    }
                }
                catch (Exception ex) { Logger.Error("Load Index Error: " + ex.Message); }

                if (_index == null) _index = new SearchIndex();
                return _index;
            }
        }

        private static string ReadGzippedText(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // Plan 003: reconstruct the full index by reading every shard file present. A
        // missing shard file is a legitimately empty shard (never had any dirty entry),
        // not a corruption signal — each shard's own tmp+move write is independently
        // atomic, so a shard file that exists is always a complete, valid snapshot of
        // that shard as of some past flush.
        private SearchIndex LoadShardedIndex()
        {
            var idx = new SearchIndex();
            for (int id = 0; id < ShardCount; id++)
            {
                string shardPath = ShardFilePath(id);
                if (!File.Exists(shardPath)) continue;
                try
                {
                    string json = ReadGzippedText(shardPath);
                    if (string.IsNullOrEmpty(json)) continue;
                    var bucket = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, SearchIndex.IndexEntry>>(json);
                    if (bucket == null) continue;
                    foreach (var kv in bucket) idx.Objects[kv.Key] = kv.Value;
                }
                catch (Exception ex) { Logger.Warn(string.Format("Shard {0} load failed ({1}): {2}", id, shardPath, ex.Message)); }
            }
            return idx;
        }

        public bool LooksLikeAttributeName(string term)
        {
            if (string.IsNullOrEmpty(term)) return false;
            return GetIndex().Objects.ContainsKey($"Attribute:{term}");
        }

        public void UpdateIndex(SearchIndex index)
        {
            lock (_lock)
            {
                BuildParentIndex(index);
                TouchGraph(index);
                _index = index;
            }
            MarkDirty();
            // Fire and forget save to disk with throttling (W-A3: 10s → 30s)
            ScheduleThrottledFlush();
        }

        // Fase 0.5: route per-object enrichment flushes through the 30s throttle so a
        // bulk enrichment pass doesn't re-serialize the whole (growing) index on every
        // object — measured 261 full-index serializations in one pass on a 38.6k KB,
        // serializeMs climbing 300→1800ms as the index swelled 12→45MB. The direct
        // Task.Run(FlushToDisk) calls bypassed the throttle that UpdateIndex already had.
        //
        // Trailing-edge debounce: writes landing INSIDE the throttle window used to be
        // dropped on the floor (nothing re-armed a flush), so the last burst of changes
        // before the process went idle never reached disk until exit. Now a one-shot
        // timer fires at the end of the window and flushes whatever is still dirty.
        private double _flushThrottleSeconds = FlushThrottleSeconds;
        internal void SetFlushThrottleForTest(double seconds) { _flushThrottleSeconds = seconds; }

        // Test observability only — counts real writes (FlushToDisk's success path),
        // not no-op calls. Lets regression tests pin how many times the on-disk index
        // is actually re-serialized under a burst of dirty updates (see plans/001).
        private long _flushWriteCount;
        internal long FlushWriteCountForTest => System.Threading.Interlocked.Read(ref _flushWriteCount);
        internal void ResetFlushWriteCountForTest() => System.Threading.Interlocked.Exchange(ref _flushWriteCount, 0);
        private System.Threading.Timer _trailingFlushTimer;
        private int _trailingFlushArmed = 0;
        private readonly object _trailingTimerLock = new object();

        internal void ScheduleThrottledFlush()
        {
            if (!_savingInProgress && (DateTime.Now - _lastFlushTime).TotalSeconds > _flushThrottleSeconds)
            {
                Task.Run(() => FlushToDisk());
                return;
            }
            ArmTrailingFlush();
        }

        private void ArmTrailingFlush()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _trailingFlushArmed, 1, 0) != 0) return;
            try
            {
                lock (_trailingTimerLock)
                {
                    if (_trailingFlushTimer == null)
                        _trailingFlushTimer = new System.Threading.Timer(OnTrailingFlush, null,
                            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    double elapsed = (DateTime.Now - _lastFlushTime).TotalSeconds;
                    int dueMs = (int)Math.Max(250, (_flushThrottleSeconds - elapsed) * 1000);
                    _trailingFlushTimer.Change(dueMs, System.Threading.Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref _trailingFlushArmed, 0);
                Logger.Warn("ArmTrailingFlush failed: " + ex.Message);
            }
        }

        private void OnTrailingFlush(object state)
        {
            System.Threading.Interlocked.Exchange(ref _trailingFlushArmed, 0);
            try
            {
                if (IsFullyFlushed) return;
                bool flushed = FlushToDisk();
                // A concurrent flush was in flight (or this one failed) and changes are
                // still pending — re-arm so the trailing write isn't lost.
                if (!flushed && !IsFullyFlushed) ArmTrailingFlush();
            }
            catch (Exception ex) { Logger.Warn("Trailing flush failed: " + ex.Message); }
        }

        // Force a confirmed-current flush regardless of the throttle window. Call at the
        // end of a bulk enrichment drain / delta refresh BEFORE writing the meta sidecar.
        // Returns true only once every change that was dirty at the moment of this call
        // is confirmed on disk; returns false (never lies) when the deadline elapses —
        // in which case the caller MUST NOT stamp the sidecar, otherwise the persisted
        // high-water-mark would claim changes the body doesn't contain and the next warm
        // start's delta would skip them forever.
        public bool FlushNow(int timeoutMs = 30000)
        {
            if (_index == null) return false; // nothing loaded — nothing to certify
            long target = System.Threading.Interlocked.Read(ref _dirtyGeneration);
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
            while (true)
            {
                if (System.Threading.Interlocked.Read(ref _flushedGeneration) >= target) return true;
                FlushToDisk(); // no-ops (returns false) while another flush is mid-flight
                if (System.Threading.Interlocked.Read(ref _flushedGeneration) >= target) return true;
                if (DateTime.UtcNow >= deadline)
                {
                    Logger.Warn($"[FLUSH-NOW] timed out after {timeoutMs}ms (flushedGen={FlushedGeneration} targetGen={target}) — caller must not stamp the meta sidecar.");
                    return false;
                }
                System.Threading.Thread.Sleep(20);
            }
        }

        // ===== Fase 1: persistible incremental index (version stamp + high-water-mark + delta-on-open) =====

        // Bump whenever IndexEntry's serialized shape changes. A mismatch on warm start
        // forces a full cold rebuild (IntelliJ stub-index pattern) instead of silently
        // deserialising a 38k index into a different layout.
        public const int CurrentSchemaVersion = 1;

        // Sidecar holding the validation header (schema version, worker-DLL hash, high-water-mark).
        // Written ONLY when enrichment fully completes (PersistEnrichedComplete) or after a delta
        // refresh — never on the throttled mid-enrichment body flushes. So the sidecar's presence
        // means "the body on disk is fully enriched and this hwm is trustworthy"; a worker that
        // dies mid-enrichment leaves a body but no sidecar → next warm start does a full rebuild.
        private string _metaPath => string.IsNullOrEmpty(_indexPath) ? null : _indexPath.Replace(".json", ".meta.json");

        // High-water-mark: max KBObject.LastUpdate observed. Stored as ticks for lock-free CAS.
        // Instance field (per index / per KB) — not process-global.
        private long _hwmTicks = 0;

        /// <summary>Advance the high-water-mark if <paramref name="lastUpdate"/> is newer. Lock-free.</summary>
        public void ObserveLastUpdate(DateTime lastUpdate)
        {
            long t = lastUpdate.Ticks;
            long cur;
            while (true)
            {
                cur = System.Threading.Interlocked.Read(ref _hwmTicks);
                if (t <= cur) return;
                if (System.Threading.Interlocked.CompareExchange(ref _hwmTicks, t, cur) == cur) return;
            }
        }

        /// <summary>Reset the high-water-mark (called on a full reindex so a stale max doesn't persist).</summary>
        public void ResetHighWaterMark() => System.Threading.Interlocked.Exchange(ref _hwmTicks, 0);

        /// <summary>
        /// Snapshot of entries that were never fully enriched (no embedding) — used by the
        /// warm-start delta path to resume enrichment when the persisted body was lite-only
        /// or partially enriched (e.g. a worker idle-evicted before completing).
        /// Embedding==null/empty is the reliable signal: UpdateEntry always sets a 128-float
        /// embedding, lite stubs never carry one.
        /// </summary>
        // issue #25 follow-up (P0): cheap early-exit probe. In lazy mode the index
        // flips to Status="Ready" while most entries are still un-enriched (empty
        // Calls/CalledBy/Embedding), so tools that read those edges can silently
        // under-report. This lets a tool flag `enrichmentPending` when a zero/empty
        // edge result might be an artifact of pending enrichment rather than truth.
        public bool HasPendingEnrichment()
        {
            var idx = GetIndex();
            if (idx?.Objects == null) return false;

            // Return the cached answer when nothing has mutated the index since we last
            // scanned (see _pendingEnrichCache). This turns the common "already drained"
            // case — a full O(n) walk that finds nothing — into an O(1) generation compare.
            long gen = System.Threading.Interlocked.Read(ref _dirtyGeneration);
            var cached = _pendingEnrichCache;
            if (cached != null && cached.Item1 == gen) return cached.Item2;

            bool pending = false;
            foreach (var e in idx.Objects.Values)
            {
                if (e == null) continue;
                if (e.Embedding == null || e.Embedding.Length == 0) { pending = true; break; }
            }

            _pendingEnrichCache = System.Tuple.Create(gen, pending);
            return pending;
        }

        public List<SearchIndex.IndexEntry> GetUnenrichedEntries()
        {
            var idx = GetIndex();
            var result = new List<SearchIndex.IndexEntry>();
            if (idx?.Objects == null) return result;
            foreach (var e in idx.Objects.Values)
            {
                if (e == null) continue;
                if (e.Embedding == null || e.Embedding.Length == 0) result.Add(e);
            }
            return result;
        }

        /// <summary>Current high-water-mark as a DateTime (MinValue if none observed).</summary>
        public DateTime CurrentHighWaterMark
        {
            // UTC: the ticks come from KBObject.LastUpdate, which the watcher and GetKeys()
            // treat as UTC (compared against DateTime.UtcNow). Labelling it Utc keeps the
            // persisted *Utc field honest and makes any future cross-comparison safe.
            get { long t = System.Threading.Interlocked.Read(ref _hwmTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); }
        }

        /// <summary>
        /// Persist the validation sidecar. Call AFTER a body flush, only when the body is in a
        /// trustworthy (fully-enriched or delta-merged) state. Atomic temp-then-Replace/Move.
        /// </summary>
        public void WriteMetaSidecar(int objectCount)
        {
            string metaPath = _metaPath;
            if (string.IsNullOrEmpty(metaPath)) return;
            try
            {
                var meta = new WarmIndexSnapshotMetadata
                {
                    WorkerDllSha256 = WarmIndexSnapshot.ComputeWorkerDllSha256(),
                    KbPath = _buildService?.GetKBPath(),
                    CapturedAtUtc = DateTime.UtcNow.ToString("o"),
                    ObjectCount = objectCount,
                    SchemaVersion = CurrentSchemaVersion,
                    HighWaterMarkUtc = CurrentHighWaterMark == DateTime.MinValue ? null : CurrentHighWaterMark.ToString("o")
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(meta);
                string dir = Path.GetDirectoryName(metaPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = metaPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                // Atomic swap: File.Replace replaces the destination in one NTFS operation,
                // so there is no Delete→Move window where the sidecar is missing — a crash
                // in that window used to lose the sidecar and force a full reindex on the
                // next warm start. Mirrors MemoryService.WriteCompacted. First write has
                // no destination yet, so it falls back to a plain Move (Replace throws).
                if (File.Exists(metaPath))
                    File.Replace(tmp, metaPath, null);   // atomic swap on NTFS; consumes tmp
                else
                    File.Move(tmp, metaPath);
                Logger.Info($"[INDEX-META] sidecar written: schema={CurrentSchemaVersion} hwm={meta.HighWaterMarkUtc ?? "<none>"} objects={objectCount}");
            }
            catch (Exception ex) { Logger.Warn("WriteMetaSidecar failed: " + ex.Message); }
        }

        /// <summary>Result of validating the on-disk cache against the current worker + schema.</summary>
        public sealed class OnDiskCacheValidation
        {
            public bool BodyPresent;
            public bool MetaPresent;
            public bool SchemaMatch;
            public bool DllMatch;
            public DateTime HighWaterMark = DateTime.MinValue;
            // Delta-on-open is only safe when the body is present AND a trustworthy sidecar
            // (matching schema + worker DLL) accompanies it. Anything else → full rebuild.
            public bool CanDelta => BodyPresent && MetaPresent && SchemaMatch && DllMatch && HighWaterMark != DateTime.MinValue;

            // Relaxed predicate for the post-upgrade case: the worker DLL changed (DllMatch=False)
            // but the index LAYOUT is unchanged (SchemaMatch=True), so the on-disk body is still
            // structurally readable. Gated by Configuration.DeltaAcrossWorkerDll in the caller —
            // running a bounded delta here (and re-baselining the sidecar's DLL hash) avoids the
            // full 38k re-walk that would otherwise block writes for minutes after every upgrade.
            // The only thing skipped vs. a full rebuild is retro-applying enrichment-LOGIC changes
            // to objects that didn't change on disk; a forced reindex still does that.
            public bool CanDeltaAcrossDll => BodyPresent && MetaPresent && SchemaMatch && HighWaterMark != DateTime.MinValue;
        }

        /// <summary>
        /// Inspect the on-disk cache (cheap — reads the small sidecar, not the body) to decide
        /// whether a warm start can do a bounded delta refresh or must full-rebuild.
        /// </summary>
        public OnDiskCacheValidation ValidateOnDiskCache()
        {
            var v = new OnDiskCacheValidation();
            try
            {
                EnsureInitialized();
                v.BodyPresent = (!string.IsNullOrEmpty(_indexPathGz) && File.Exists(_indexPathGz))
                                 || (!string.IsNullOrEmpty(_indexPath) && File.Exists(_indexPath))
                                 || (!string.IsNullOrEmpty(_shardManifestPath) && File.Exists(_shardManifestPath));
                string metaPath = _metaPath;
                v.MetaPresent = !string.IsNullOrEmpty(metaPath) && File.Exists(metaPath);
                Logger.Info(string.Format("[INDEX-CACHE-PATHS] validate: bodyPresent={0} metaPresent={1} gz={2} meta={3}", v.BodyPresent, v.MetaPresent, _indexPathGz, metaPath));
                if (v.MetaPresent)
                {
                    var meta = Newtonsoft.Json.JsonConvert.DeserializeObject<WarmIndexSnapshotMetadata>(File.ReadAllText(metaPath));
                    v.SchemaMatch = meta != null && meta.SchemaVersion == CurrentSchemaVersion;
                    string currentDll = WarmIndexSnapshot.ComputeWorkerDllSha256();
                    v.DllMatch = meta != null && string.Equals(meta.WorkerDllSha256, currentDll, StringComparison.OrdinalIgnoreCase);
                    if (meta != null && !string.IsNullOrEmpty(meta.HighWaterMarkUtc)
                        && DateTime.TryParse(meta.HighWaterMarkUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var hwm))
                    {
                        v.HighWaterMark = hwm;
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("ValidateOnDiskCache failed (treating as full-rebuild): " + ex.Message); }
            return v;
        }

        // Plan 003: small sidecar recording that the shard directory is a complete,
        // trustworthy sharded snapshot (as opposed to a legacy single-file body).
        // Written only after a flush round that wrote EVERY shard it attempted
        // successfully — never on a partial round — so its mere presence means
        // "load from shards", same certification spirit as WriteMetaSidecar.
        private sealed class ShardManifest
        {
            public int ShardCount { get; set; }
            public int SchemaVersion { get; set; }
            public int ObjectCount { get; set; }
            public string CapturedAtUtc { get; set; }
        }

        private void WriteShardManifest(int objectCount)
        {
            string manifestPath = _shardManifestPath;
            if (string.IsNullOrEmpty(manifestPath)) return;
            try
            {
                var manifest = new ShardManifest
                {
                    ShardCount = ShardCount,
                    SchemaVersion = CurrentSchemaVersion,
                    ObjectCount = objectCount,
                    CapturedAtUtc = DateTime.UtcNow.ToString("o")
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(manifest);
                string dir = Path.GetDirectoryName(manifestPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = manifestPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
                File.Move(tmp, manifestPath);
            }
            catch (Exception ex) { Logger.Warn("WriteShardManifest failed: " + ex.Message); }
        }

        // Returns true when this call wrote a snapshot to disk that is at least as new
        // as the dirty generation captured before serialization started; false when it
        // skipped (flush already in flight / no index) or failed. Never throws.
        //
        // Plan 003 (sharded flush): only shards dirtied since the last successful flush
        // are (re)serialized — clean shards are left untouched on disk, so cost scales
        // with dirty-entry count rather than total index size. Each dirty shard id is
        // popped from _dirtyShards BEFORE its content is read/written, so any mutation
        // landing concurrently (even mid-write) re-marks the shard dirty for the next
        // round instead of being silently dropped by an end-of-round clear.
        private bool FlushToDisk()
        {
            if (_savingInProgress) return false;

            SearchIndex snapshot = null;
            lock (_lock)
            {
                if (_savingInProgress) return false;
                if (_index == null) return false;

                _savingInProgress = true;
                // ELITE: Snapshot the index to serialize it OUTSIDE the lock.
                // ConcurrentDictionary iteration is thread-safe and provides a consistent snapshot view.
                snapshot = _index;
            }

            // Capture the generation BEFORE enumeration begins: every mutation that
            // happened-before this read is visible to the serializer below, so on
            // success the on-disk body provably contains generation `gen`.
            long gen = System.Threading.Interlocked.Read(ref _dirtyGeneration);

            var idsToWrite = new List<int>();
            foreach (var id in _dirtyShards.Keys.ToArray())
                if (_dirtyShards.TryRemove(id, out _)) idsToWrite.Add(id);

            try
            {
                string dir = _shardDirPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var settings = new Newtonsoft.Json.JsonSerializerSettings {
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore,
                    Formatting = Newtonsoft.Json.Formatting.None
                };
                var serializer = Newtonsoft.Json.JsonSerializer.Create(settings);

                // Single O(N) pass bucketing entries into the shards we're about to write
                // (clean shards are never even visited for bucketing, let alone written).
                // PERF NOTE (perf-review round 2): a per-shard key registry was tried here
                // to make this O(dirty) — reverted deliberately. A shard file REPLACES its
                // previous contents, so any registry miss would silently DROP live entries
                // from disk. ~38k FNV hashes costs ~2ms on a background thread; correctness
                // wins.
                var buckets = new Dictionary<int, Dictionary<string, SearchIndex.IndexEntry>>();
                foreach (var id in idsToWrite) buckets[id] = new Dictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
                if (idsToWrite.Count > 0)
                {
                    foreach (var kv in snapshot.Objects)
                    {
                        if (buckets.TryGetValue(ShardOf(kv.Key), out var bucket)) bucket[kv.Key] = kv.Value;
                    }
                }

                var flushSw = System.Diagnostics.Stopwatch.StartNew();
                bool allOk = true;
                long totalGzBytes = 0;

                foreach (var id in idsToWrite)
                {
                    try
                    {
                        string shardPath = ShardFilePath(id);
                        string tmpPath = shardPath + ".tmp";
                        // PERFORMANCE (W-A3): write gzipped via a temp file + atomic move so
                        // partial writes never leave a corrupt shard on disk. LOH fix carried
                        // over from the single-file design: stream straight through gzip
                        // instead of building the whole shard as one JSON string first.
                        using (var fs = File.Create(tmpPath))
                        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                        using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                        using (var jsonWriter = new Newtonsoft.Json.JsonTextWriter(writer))
                        {
                            serializer.Serialize(jsonWriter, buckets[id]);
                        }
                        try { totalGzBytes += new FileInfo(tmpPath).Length; } catch { }
                        if (File.Exists(shardPath)) File.Delete(shardPath);
                        File.Move(tmpPath, shardPath);
                        _shardWriteCounts.AddOrUpdate(id, 1, (k, v) => v + 1);
                    }
                    catch (Exception exShard)
                    {
                        allOk = false;
                        _dirtyShards[id] = 1; // not durable yet — retry this shard next round
                        Logger.Error(string.Format("[INDEX-SAVE] shard {0} flush failed: {1}", id, exShard.Message));
                    }
                }

                long totalMs = flushSw.ElapsedMilliseconds;
                int entryCount = snapshot.Objects?.Count ?? 0;
                // Fase 3 measurement: piggyback the running enrichment sub-step split on the
                // throttled flush so the SDK-bound (refScan/typeExtract) vs CPU-only
                // (embedding/textualScan) proportion is observable without waiting for the
                // (pathologically slow) full drain to reach [ENRICH-DONE].
                Logger.Info($"[INDEX-SAVE] shardsWritten={idsToWrite.Count}/{ShardCount} gzKB={totalGzBytes / 1024} totalMs={totalMs} entries={entryCount} gen={gen} | {GetEnrichTimingSummary()}");

                if (!allOk)
                {
                    // Partial round: some shards durable, some not. Don't certify `gen`,
                    // don't touch the legacy files or manifest — the next flush retries
                    // only the shards still marked dirty above.
                    int nFail = System.Threading.Interlocked.Increment(ref _consecutiveFlushFailures);
                    _lastFlushErrorMessage = "partial shard flush failure";
                    Logger.Error($"Flush Error (consecutive={nFail}): {_lastFlushErrorMessage}");
                    return false;
                }

                WriteShardManifest(entryCount);
                // Migration cleanup: once the sharded body is confirmed fully durable, the
                // legacy single-file snapshot (if any) is no longer needed for warm start.
                try { if (File.Exists(_indexPathGz)) File.Delete(_indexPathGz); } catch { }
                try { if (File.Exists(_indexPath)) File.Delete(_indexPath); } catch { }

                System.Threading.Interlocked.Exchange(ref _consecutiveFlushFailures, 0);
                _lastFlushSuccessUtc = DateTime.UtcNow;
                _lastFlushErrorMessage = null;
                // Publish the confirmed-on-disk generation (monotonic max).
                long cur;
                while ((cur = System.Threading.Interlocked.Read(ref _flushedGeneration)) < gen
                       && System.Threading.Interlocked.CompareExchange(ref _flushedGeneration, gen, cur) != cur) { }
                System.Threading.Interlocked.Increment(ref _flushWriteCount);
                return true;
            }
            catch (Exception ex) {
                // Round-level failure (e.g. directory creation) before/around the per-shard
                // loop — restore every popped id so nothing is lost.
                foreach (var id in idsToWrite) _dirtyShards[id] = 1;
                int n = System.Threading.Interlocked.Increment(ref _consecutiveFlushFailures);
                _lastFlushErrorMessage = ex.Message;
                Logger.Error($"Flush Error (consecutive={n}): {ex.Message}");
                return false;
            }
            finally {
                _savingInProgress = false;
                _lastFlushTime = DateTime.Now;
            }
        }

        private string GetJsonHash(string json)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
                return BitConverter.ToString(bytes).Replace("-", "");
            }
        }

        // v2.6.8: defensive reads for KBObject SDK accessors that can throw on
        // partially-loaded objects. Callers want a sentinel ("unknown") rather than
        // a crashed indexer.
        private static DateTime SafeReadDate(Func<DateTime> read)
        {
            try { return read(); } catch { return DateTime.MinValue; }
        }

        private static string SafeReadString(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }

        public void UpdateEntry(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            var index = GetIndex();
            var hierarchy = ResolveHierarchy(obj);

            var entry = new SearchIndex.IndexEntry
            {
                Guid = obj.Guid.ToString(),
                EntityKey = SafeEntityKey(obj),
                EntityTypeGuid = SafeEntityTypeGuid(obj),
                EntityId = SafeEntityId(obj),
                Name = obj.Name,
                Type = obj.TypeDescriptor.Name,
                Description = obj.Description,
                Parent = hierarchy.ParentName,
                ParentPath = hierarchy.ParentPath,
                ParentFolderPath = ComposeParentFolderPath(hierarchy.ParentPath),
                Path = hierarchy.Path,
                Module = hierarchy.ModuleName,
                LastUpdate = SafeReadDate(() => obj.LastUpdate),
                CreatedAt = SafeReadDate(() => obj.VersionDate),
                LastModifiedBy = SafeReadString(() => obj.UserName),
                // This entry IS being enriched (type/embedding synchronously here; edges async
                // via EnrichEdges). Mark it so the LIVE index entry reflects enriched state —
                // otherwise AnalyzeService re-reads IsEnriched=false and re-promotes on every
                // call (the enricher only flips the orphaned lite stub, not this replacement).
                IsEnriched = true
            };

            // Fase 1: advance the high-water-mark so incremental updates keep the
            // persisted delta baseline current.
            ObserveLastUpdate(entry.LastUpdate);

            long teStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (obj is global::Artech.Genexus.Common.Objects.Attribute attr)
            {
                entry.DataType = attr.Type.ToString();
                entry.Length = attr.Length;
                entry.Decimals = attr.Decimals;
                entry.IsFormula = attr.Formula != null;
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Table tbl)
            {
                entry.RootTable = tbl.Name;
                try {
                    var children = new Newtonsoft.Json.Linq.JArray();
                    dynamic dStructure = ((dynamic)tbl).TableStructure;
                    if (dStructure != null && dStructure.Attributes != null) {
                        foreach (dynamic tableAttr in dStructure.Attributes) 
                            children.Add(VisualStructureMapper.MapAttribute(tableAttr));
                    }
                    entry.SourceSnippet = children.ToString(Newtonsoft.Json.Formatting.None);
                } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Transaction trn)
            {
                entry.RootTable = trn.Structure.Root.Name;
                try { entry.ParmRule = trn.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase)); } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.SDT sdt)
            {
                try {
                    entry.SourceSnippet = StructureParser.SerializeToText(obj);
                    entry.Complexity = entry.SourceSnippet?.Split('\n').Length ?? 0;
                } catch (Exception ex) {
                    Logger.Error($"SDT index snippet failed for {obj.Name}: {ex.Message}");
                }
            }

            // Calculate Complexity for Procedures/DataProviders
            if (obj is global::Artech.Genexus.Common.Objects.Procedure || obj is global::Artech.Genexus.Common.Objects.DataProvider)
            {
                try {
                    dynamic sourcePart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
                    if (sourcePart != null) {
                        string src = sourcePart.Source ?? "";
                        entry.Complexity = src.Split('\n').Length;
                        // Source is already in hand here — extract code metrics for KB-wide
                        // analytics (genexus_analyze mode=code_metrics) at ~no extra cost.
                        entry.Metrics = GxMcp.Worker.Helpers.CodeMetricsExtractor.Extract(src);
                    }
                } catch { }
            }

            System.Threading.Interlocked.Add(ref _enrichTypeExtractTicks, System.Diagnostics.Stopwatch.GetTimestamp() - teStart);

            string key = GetEntryStorageKey(entry);

            // Compute Embedding
            string semanticText = $"{entry.Name} {entry.Type} {entry.Description} {entry.RootTable} {entry.ParmRule}";
            long emStart = System.Diagnostics.Stopwatch.GetTimestamp();
            entry.Embedding = _vectorService.ComputeEmbedding(semanticText);
            System.Threading.Interlocked.Add(ref _enrichEmbeddingTicks, System.Diagnostics.Stopwatch.GetTimestamp() - emStart);

            // Fase 2: rename collapse. The Guid is stable across a rename but the Type:Name
            // storage key changes — without this the old key lingers as a duplicate/stale
            // entry. If this Guid was previously stored under a different key, drop the old one.
            if (index.GuidToKey != null && !string.IsNullOrEmpty(entry.Guid)
                && index.GuidToKey.TryGetValue(entry.Guid, out var oldKey)
                && !string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase))
            {
                if (index.Objects.TryRemove(oldKey, out var stale))
                {
                    if (index.ChildrenByParent != null) RemoveEntryFromParentIndex(index, stale);
                    MarkShardDirty(oldKey); // the old key's shard lost an entry too
                }
            }

            // Atomic update using ConcurrentDictionary
            index.Objects.AddOrUpdate(key, entry, (k, existing) => entry);
            TouchGraph(index);
            if (index.ChildrenByParent != null)
            {
                AddOrUpdateEntryInParentIndex(index, entry);
            }
            if (index.GuidToKey != null && !string.IsNullOrEmpty(entry.Guid))
            {
                index.GuidToKey[entry.Guid] = key;
            }

            // ENRICHMENT: Dependencies (Calls/Tables/CalledBy) via GetReferences + textual scan,
            // fire-and-forget on the background STA dispatcher. NOTE: this populates the target's
            // OUTGOING edges (Calls) and adds the target to its callees' CalledBy lists — it does
            // NOT populate the target's own CalledBy (incoming/callers), which only fill in as the
            // CALLERS get enriched. So in lazy mode impact analysis (callers) correctly relies on
            // the live SDK cross-check rather than the index — see AnalyzeService.
            Guid objGuid = obj.Guid;
            var objKey = obj.Key;
            Program.EnqueueBackground(() => {
                try {
                    var kb = _buildService.KbService?.GetKB();
                    if (kb == null) return;
                    // SdkGate: GetKB/Get/GetReferences are SDK calls; the background
                    // dispatcher thread must not interleave them with other apartments.
                    using (SdkGate.Enter())
                    {
                        var bgObj = kb.DesignModel.Objects.Get(objKey);
                        if (bgObj == null) bgObj = kb.DesignModel.Objects.Get(objGuid);
                        if (bgObj == null) return;
                        EnrichEdges((global::Artech.Architecture.Common.Objects.KBObject)bgObj, entry, index);
                    }
                } catch (Exception ex) { Logger.Error("Background Indexing Error: " + ex.Message); }
            });

            MarkDirtyForKey(key);
            // Fire and forget save to disk (throttled — see ScheduleThrottledFlush)
            ScheduleThrottledFlush();
        }

        internal (string ParentName, string ParentPath, string Path, string ModuleName) ResolveHierarchyForIndex(global::Artech.Architecture.Common.Objects.KBObject obj)
            => ResolveHierarchy(obj);

        private static string SafeEntityKey(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.ToString(); } catch { return null; }
        }

        private static string SafeEntityTypeGuid(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.Type.ToString(); } catch { return null; }
        }

        private static int? SafeEntityId(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.Id; } catch { return null; }
        }

        // ── Copy-on-write edge lists ────────────────────────────────────────────
        // FlushToDisk serializes LIVE entries on a threadpool thread with no lock.
        // Mutating a published List<string> in place (Calls/Tables/CalledBy) raced
        // that serialization ("Collection was modified" flush failures / torn
        // snapshots). These helpers never mutate a published list: they replace the
        // property with a new list instance, so any reader/serializer holding the
        // old reference sees a stable snapshot. The per-entry lock only serializes
        // concurrent WRITERS against each other.
        internal static bool AddEdgeCow(
            SearchIndex.IndexEntry entry,
            Func<SearchIndex.IndexEntry, List<string>> get,
            Action<SearchIndex.IndexEntry, List<string>> set,
            string value)
        {
            if (entry == null || string.IsNullOrEmpty(value)) return false;
            lock (entry)
            {
                var current = get(entry);
                // PERFORMANCE (perf-review): hub entries (popular Tables/SDTs referenced
                // by hundreds of procedures) made each duplicate-check a linear
                // List.Contains over a list holding thousands of names — and the check
                // runs once per referencing caller during enrichment, O(k²) per hub.
                // A per-entry companion HashSet (lazily built once the list passes a
                // small threshold; invalidated when the published list instance changes
                // or its count no longer matches) turns the duplicate check into O(1).
                var cache = SeenFor(get);
                HashSet<string> hashSet = null;
                if (cache != null && cache.TryGetValue(entry, out hashSet)
                    && (hashSet == null || hashSet.Count != (current?.Count ?? 0)))
                {
                    hashSet = null; // replaced/stale — rebuild below
                }
                if (hashSet == null && current != null && current.Count > 8)
                {
                    hashSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
                    cache.Remove(entry);
                    cache.Add(entry, hashSet);
                }
                if (current == null) current = new List<string>();
                if (hashSet != null)
                {
                    if (!hashSet.Add(value)) return false;
                }
                else if (current.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
                var next = new List<string>(current.Count + 1);
                next.AddRange(current);
                next.Add(value);
                set(entry, next);
                return true;
            }
        }

        // One companion-set table per edge kind (Calls/Tables/CalledBy), keyed by the
        // getter delegate identity so AddEdgeCow can find its cache without touching
        // the entry model. Unknown getters return null → falls back to List.Contains.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SearchIndex.IndexEntry, HashSet<string>> _callsSeen = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SearchIndex.IndexEntry, HashSet<string>> _tablesSeen = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SearchIndex.IndexEntry, HashSet<string>> _calledBySeen = new();
        private static readonly Func<SearchIndex.IndexEntry, List<string>> GetCallsFn = x => x.Calls;
        private static readonly Func<SearchIndex.IndexEntry, List<string>> GetTablesFn = x => x.Tables;
        private static readonly Func<SearchIndex.IndexEntry, List<string>> GetCalledByFn = x => x.CalledBy;

        private static System.Runtime.CompilerServices.ConditionalWeakTable<SearchIndex.IndexEntry, HashSet<string>> SeenFor(
            Func<SearchIndex.IndexEntry, List<string>> get)
        {
            if (ReferenceEquals(get, GetCallsFn)) return _callsSeen;
            if (ReferenceEquals(get, GetTablesFn)) return _tablesSeen;
            if (ReferenceEquals(get, GetCalledByFn)) return _calledBySeen;
            return null;
        }

        internal static bool AddCallCow(SearchIndex.IndexEntry e, string name)
            => AddEdgeCow(e, x => x.Calls, (x, l) => x.Calls = l, name);
        internal static bool AddTableCow(SearchIndex.IndexEntry e, string name)
            => AddEdgeCow(e, x => x.Tables, (x, l) => x.Tables = l, name);
        internal static bool AddCalledByCow(SearchIndex.IndexEntry e, string name)
            => AddEdgeCow(e, x => x.CalledBy, (x, l) => x.CalledBy = l, name);

        // Dependency enrichment (extracted so the call path is readable): populate this entry's
        // Calls/Tables and the inverted CalledBy edges via the SDK reference graph + the FR#3
        // textual call-site scan. Runs on the background STA dispatcher.
        private void EnrichEdges(global::Artech.Architecture.Common.Objects.KBObject bgObj, SearchIndex.IndexEntry entry, SearchIndex index)
        {
            long rsStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var references = ((dynamic)bgObj).GetReferences();
            bool changed = false;
            if (references != null)
            {
                foreach (dynamic reference in references)
                {
                    try
                    {
                        dynamic targetKey = reference.To;
                        if (targetKey == null) continue;
                        string targetName = targetKey.Name;
                        if (string.IsNullOrEmpty(targetName)) {
                            string keyStr = targetKey.ToString();
                            int colon = keyStr.IndexOf(':');
                            if (colon >= 0 && colon < keyStr.Length - 1)
                                targetName = keyStr.Substring(colon + 1);
                            else
                                targetName = keyStr;
                        }
                        if (string.IsNullOrEmpty(targetName)) continue;

                        string targetType = targetKey.TypeDescriptor.Name;
                        if (targetType == "Attribute" || targetType == "Table") {
                            if (AddTableCow(entry, targetName)) changed = true;
                        } else {
                            if (AddCallCow(entry, targetName)) changed = true;
                        }

                        // Inverted Index (CalledBy) — copy-on-write, see AddEdgeCow.
                        // E4: resolve the target's REAL storage key instead of assuming the bare
                        // "Type:Name" shape — GetEntryStorageKey scopes Folder/Module keys with
                        // their path (Type:Path), so a direct lookup silently missed those
                        // targets and their CalledBy edges were dropped.
                        string targetGuid = null;
                        try { object g = targetKey.Guid; if (g != null) targetGuid = g.ToString(); } catch { /* key shape without Guid */ }
                        string targetIndexKey = ResolveTargetStorageKey(index, targetType, targetName, targetGuid);
                        if (!string.IsNullOrEmpty(targetIndexKey)
                            && index.Objects.TryGetValue(targetIndexKey, out var targetEntry)) {
                            if (AddCalledByCow(targetEntry, entry.Name)) { changed = true; MarkShardDirty(targetIndexKey); }
                        }
                    } catch { }
                }
            }
            System.Threading.Interlocked.Add(ref _enrichRefScanTicks, System.Diagnostics.Stopwatch.GetTimestamp() - rsStart);

            // FR#3 (friction-report 2026-05-14): GetReferences misses call sites in Event Start
            // blocks on some KB shapes. Augment with a textual scan over Source/Events/Rules:
            // any identifier that already exists as a non-Attribute object in the index is
            // treated as a call. The hard "must exist in index" filter eliminates false
            // positives from keywords / language built-ins.
            long tsStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                bool textualChanged = EnrichCallsFromTextualScan(bgObj, entry, index);
                if (textualChanged) changed = true;
            }
            catch (Exception scanEx) { Logger.Debug("[FR#3] Textual call scan failed: " + scanEx.Message); }
            System.Threading.Interlocked.Add(ref _enrichTextualScanTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tsStart);

            // Plan 003: entry's own shard was marked already (or is marked below); target
            // shards touched via CalledBy were marked inline above / in the textual scan.
            // Only bump the generation here — precise per-shard marking already happened.
            if (changed) { TouchGraph(index); System.Threading.Interlocked.Increment(ref _dirtyGeneration); MarkShardDirty(GetEntryStorageKey(entry)); ScheduleThrottledFlush(); }
        }

        // E4: resolve an edge target to its REAL storage key (the shape GetEntryStorageKey
        // produces), so CalledBy no longer silently drops matches when the stored key isn't
        // the bare Type:Name (Folder/Module keys are scoped with their path). Preference:
        // 1) the maintained Guid→storage-key map (authoritative, rename-safe), guarded by a
        //    presence check so a stale guid mapping can never shadow a live direct key;
        // 2) the direct Type:Name key — Objects' comparer is OrdinalIgnoreCase, so original
        //    casing differences are already covered there;
        // 3) the ByNameIndex multimap filtered by type as a deterministic fallback.
        // Read-only: never mutates the stored key format (on-disk cache stays compatible).
        private static string ResolveTargetStorageKey(SearchIndex index, string targetType, string targetName, string targetGuid)
        {
            if (index?.Objects == null || string.IsNullOrEmpty(targetName)) return null;

            if (!string.IsNullOrEmpty(targetGuid)
                && index.GuidToKey != null
                && index.GuidToKey.TryGetValue(targetGuid, out var byGuid)
                && !string.IsNullOrEmpty(byGuid)
                && index.Objects.ContainsKey(byGuid))
            {
                return byGuid;
            }

            string direct = string.Format("{0}:{1}", targetType, targetName);
            if (index.Objects.ContainsKey(direct)) return direct;

            if (index.ByNameIndex != null && index.ByNameIndex.TryGetValue(targetName, out var candidates))
            {
                lock (candidates)
                {
                    foreach (var candidate in candidates)
                    {
                        if (index.Objects.TryGetValue(candidate, out var entry) && entry != null
                            && string.Equals(entry.Type, targetType, StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate;
                        }
                    }
                }
            }

            return null;
        }

        // FR#3 (friction-report 2026-05-14): textual call-site scan that augments the SDK
        // reverse-dep index. Walks every ISource part on the object, harvests identifiers
        // that look like object calls (Name() / Name.Method() / Name(args)), and binds
        // them as Calls / CalledBy if the identifier matches a known object in the index.
        private static readonly System.Text.RegularExpressions.Regex _identifierCall =
            new System.Text.RegularExpressions.Regex(
                @"\b([A-Z][A-Za-z0-9_]{2,})(?:\s*\.\s*[A-Za-z0-9_]+)?\s*\(",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private bool EnrichCallsFromTextualScan(
            global::Artech.Architecture.Common.Objects.KBObject obj,
            SearchIndex.IndexEntry entry,
            SearchIndex index)
        {
            if (obj == null || entry == null || index?.Objects == null) return false;

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in obj.Parts.Cast<global::Artech.Architecture.Common.Objects.KBObjectPart>())
            {
                string src = null;
                try { if (part is ISource sp) src = sp.Source; }
                catch { }
                if (string.IsNullOrEmpty(src)) continue;

                foreach (System.Text.RegularExpressions.Match m in _identifierCall.Matches(src))
                {
                    if (!m.Success) continue;
                    string name = m.Groups[1].Value;
                    if (name.Length < 3) continue;
                    candidates.Add(name);
                }
            }

            if (candidates.Count == 0) return false;
            bool changed = false;

            foreach (var name in candidates)
            {
                if (string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase)) continue;

                // Find any matching object in the index across object types we treat as callable.
                // Attributes/Tables don't qualify as "calls" — those go into Tables already.
                foreach (var typePrefix in _callableTypes)
                {
                    string key = typePrefix + ":" + name;
                    if (!index.Objects.TryGetValue(key, out var target) || target == null) continue;

                    if (AddCallCow(entry, target.Name)) changed = true;
                    if (AddCalledByCow(target, entry.Name)) { changed = true; MarkShardDirty(key); }
                    break; // first matching object type wins
                }
            }

            return changed;
        }

        private static readonly string[] _callableTypes = new[]
        {
            "Procedure", "DataProvider", "WebPanel", "Transaction", "Menubar", "WorkPanel",
            "BusinessProcessDiagram", "SDT", "Domain", "ExternalObject"
        };

        public void RemoveEntry(string type, string name)
        {
            var index = GetIndex();
            bool removed = false;
            var removedKeys = new List<string>();
            lock (_lock)
            {
                // PERFORMANCE (perf-review): resolve candidate keys through the
                // ByNameIndex multimap instead of scanning all ~38k entries while
                // holding _lock. Type filter still applies (a name can exist across
                // types); the old full scan is kept as a fallback for indexes that
                // haven't built the multimap yet.
                List<KeyValuePair<string, SearchIndex.IndexEntry>> keysToRemove = null;
                if (index.ByNameIndex != null && !string.IsNullOrEmpty(name)
                    && index.ByNameIndex.TryGetValue(name, out var candidateKeys) && candidateKeys != null)
                {
                    keysToRemove = new List<KeyValuePair<string, SearchIndex.IndexEntry>>();
                    lock (candidateKeys)
                    {
                        foreach (var key in candidateKeys)
                        {
                            if (!index.Objects.TryGetValue(key, out var entry) || entry == null) continue;
                            if (string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))
                                keysToRemove.Add(new KeyValuePair<string, SearchIndex.IndexEntry>(key, entry));
                        }
                    }
                }
                else
                {
                    keysToRemove = index.Objects
                        .Where(pair =>
                            string.Equals(pair.Value.Type, type, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                foreach (var pair in keysToRemove)
                {
                    if (index.Objects.TryRemove(pair.Key, out var removedEntry))
                    {
                        removed = true;
                        TouchGraph(index);
                        removedKeys.Add(pair.Key);
                        // Surgical removal (mirrors RemoveEntryByGuid) — the previous
                        // implementation rebuilt the ENTIRE parent index via UpdateIndex
                        // per deletion, an O(N) rebuild for an O(1) operation.
                        if (index.ChildrenByParent != null && removedEntry != null)
                            RemoveEntryFromParentIndex(index, removedEntry);
                        // PERFORMANCE (W-M5): invalidate hierarchy cache for removed objects.
                        if (!string.IsNullOrEmpty(removedEntry?.Guid) && Guid.TryParse(removedEntry.Guid, out var g))
                        {
                            _hierarchyCache.TryRemove(g, out _);
                        }
                        // Fase 2: keep the Guid→key map in sync.
                        if (index.GuidToKey != null && !string.IsNullOrEmpty(removedEntry?.Guid))
                            index.GuidToKey.TryRemove(removedEntry.Guid, out _);
                    }
                }
            }

            if (removed)
            {
                System.Threading.Interlocked.Increment(ref _dirtyGeneration);
                foreach (var k in removedKeys) MarkShardDirty(k);
                ScheduleThrottledFlush();
            }
        }

        private void PrimeHierarchyCacheFromIndex(SearchIndex index)
        {
            if (index?.Objects == null) return;
            foreach (var entry in index.Objects.Values)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Guid)) continue;
                if (!Guid.TryParse(entry.Guid, out var g)) continue;
                _hierarchyCache[g] = (entry.Parent ?? string.Empty, entry.ParentPath ?? string.Empty, entry.Path ?? string.Empty, entry.Module);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _index = null;
                _hierarchyCache.Clear(); // PERFORMANCE (W-M5): drop stale hierarchy on KB unload.
            }
        }

        // SP6.T6 — fast-index lite pass uses this to bulk-replace the in-memory index with
        // stub entries (no SourceSnippet/Calls/CalledBy/Embedding). Keys follow the same
        // GetEntryStorageKey scheme so subsequent UpdateEntry calls AddOrUpdate cleanly.
        // Anti-demotion guard: an already-enriched live entry (concurrent UpdateEntry /
        // on-demand PromoteAsync during the walk) is NEVER overwritten by an incoming
        // lite stub — the enriched entry is carried over into the new index.
        public void ReplaceAll(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            lock (_lock)
            {
                var previous = _index;
                var idx = new SearchIndex();
                if (entries != null)
                {
                    foreach (var e in entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                        string key = GetEntryStorageKey(e);
                        if (!e.IsEnriched && previous != null
                            && previous.Objects.TryGetValue(key, out var existing)
                            && existing != null && existing.IsEnriched)
                        {
                            idx.Objects[key] = existing; // keep the enriched live entry
                            continue;
                        }
                        idx.Objects[key] = e;
                    }
                }
                idx.LastUpdated = DateTime.UtcNow;
                BuildParentIndex(idx);
                _index = idx;
                _initialized = true;
                PrimeHierarchyCacheFromIndex(idx);
            }
            MarkDirty();
        }

        // Streaming companion to ReplaceAll: incrementally AddOrUpdate the given batch
        // into the LIVE index instead of rebuilding a brand-new SearchIndex from the
        // full accumulated list (the lite walk used to do that every 500 objects —
        // O(N²) on a 38.6k KB and it clobbered concurrent UpdateEntry promotions by
        // demoting just-enriched entries back to stubs). Enriched entries are never
        // overwritten by stubs here either.
        public void AddOrUpdateBatch(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            if (entries == null) return;
            SearchIndex idx;
            lock (_lock)
            {
                if (_index == null) _index = new SearchIndex();
                idx = _index;
                if (idx.ChildrenByParent == null || idx.GuidToKey == null || idx.ByNameIndex == null) BuildParentIndex(idx);
                _initialized = true;
            }

            bool any = false;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                string key = GetEntryStorageKey(e);
                if (!e.IsEnriched && idx.Objects.TryGetValue(key, out var existing)
                    && existing != null && existing.IsEnriched)
                {
                    continue; // never demote an enriched entry to a stub
                }
                idx.Objects[key] = e;
                if (idx.ChildrenByParent != null) AddOrUpdateEntryInParentIndex(idx, e);
                if (idx.GuidToKey != null && !string.IsNullOrEmpty(e.Guid)) idx.GuidToKey[e.Guid] = key;
                MarkShardDirty(key);
                any = true;
            }
            if (any)
            {
                idx.LastUpdated = DateTime.UtcNow;
                TouchGraph(idx);
                System.Threading.Interlocked.Increment(ref _dirtyGeneration);
            }
        }

        // SP6.T6 — wires the EnrichmentQueue produced by KbService so callers (analyze, etc.)
        // can PromoteAsync individual entries on demand instead of waiting for the background
        // drain to reach them.
        private EnrichmentQueue _enrichmentQueue;

        public void SetEnrichmentQueue(EnrichmentQueue queue) { _enrichmentQueue = queue; }
        public EnrichmentQueue GetEnrichmentQueue() { return _enrichmentQueue; }

        // v2.3.8 (post-self-review) — companion to Clear() for force-reindex.
        // Removes the on-disk snapshot files so the next GetIndex() hydration
        // returns null and triggers a full SDK rebuild instead of re-loading
        // the stale gz/plain blob.
        public void DeleteOnDiskSnapshot()
        {
            lock (_lock)
            {
                try { if (!string.IsNullOrEmpty(_indexPathGz) && File.Exists(_indexPathGz)) File.Delete(_indexPathGz); } catch (Exception ex) { Logger.Warn("Delete gz snapshot failed: " + ex.Message); }
                try { if (!string.IsNullOrEmpty(_indexPath) && File.Exists(_indexPath)) File.Delete(_indexPath); } catch (Exception ex) { Logger.Warn("Delete plain snapshot failed: " + ex.Message); }
                // Plan 003: drop the sharded snapshot directory too.
                try { if (!string.IsNullOrEmpty(_shardDirPath) && Directory.Exists(_shardDirPath)) Directory.Delete(_shardDirPath, true); } catch (Exception ex) { Logger.Warn("Delete shard dir failed: " + ex.Message); }
                // Fase 1: drop the validation sidecar + hwm so a forced rebuild starts clean.
                try { if (!string.IsNullOrEmpty(_metaPath) && File.Exists(_metaPath)) File.Delete(_metaPath); } catch (Exception ex) { Logger.Warn("Delete meta sidecar failed: " + ex.Message); }
                ResetHighWaterMark();
                // Nothing durable on disk anymore — every shard needs (re)writing on the
                // next flush, same as a fresh IndexCacheService instance.
                MarkAllShardsDirty();
            }
        }
    }
}
