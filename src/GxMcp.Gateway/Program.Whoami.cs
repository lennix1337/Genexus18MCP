using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        internal static string? DetectGeneXusVersion(string? installationPath)
        {
            if (string.IsNullOrWhiteSpace(installationPath)) return null;
            string[] candidates = {
                Path.Combine(installationPath, "version.txt"),
                Path.Combine(installationPath, "Version.txt"),
                Path.Combine(installationPath, "GeneXus.version")
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    string raw = File.ReadAllText(candidate).Trim();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        return raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    }
                }
                catch { }
            }
            // Fallback: read FileVersion from GeneXus.exe metadata (standard install layout)
            try
            {
                string exePath = Path.Combine(installationPath, "GeneXus.exe");
                if (File.Exists(exePath))
                {
                    var info = FileVersionInfo.GetVersionInfo(exePath);
                    string? version = info.ProductVersion ?? info.FileVersion;
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return version.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        internal const string SupportedGeneXusMajor = "18";

        private static void LogGeneXusVersionCheck(Configuration config)
        {
            string? gxPath = config.GeneXus?.InstallationPath;
            string? detected = DetectGeneXusVersion(gxPath);
            if (string.IsNullOrEmpty(gxPath))
            {
                Log("[Gateway] GeneXus installation path is not configured.");
                return;
            }
            if (detected == null)
            {
                Log($"[Gateway] GeneXus version not detected at '{gxPath}' (no version.txt). Target major: {SupportedGeneXusMajor}.");
                return;
            }
            Log($"[Gateway] Detected GeneXus version: {detected} (target major: {SupportedGeneXusMajor}).");
            if (!detected.StartsWith(SupportedGeneXusMajor, StringComparison.OrdinalIgnoreCase))
            {
                Log($"[Gateway] WARNING: detected GeneXus version '{detected}' may not match MCP target major '{SupportedGeneXusMajor}'. Some tools may behave unexpectedly.");
            }
        }

        // v2.3.8 Task 1.2: gateway-side mirror of the worker's IndexCacheService.GetState().
        // The worker is the source of truth (CommandDispatcher.GetIndexState), but the gateway
        // keeps a last-known snapshot so `whoami` returns instantly without round-tripping
        // and works even when no worker is currently attached (default Cold/0).
        //
        // IMPORTANT: the ONLY production writer is TryRefreshIndexStateFromWorkerAsync (called
        // from whoami and from the SDK-bound short-circuit's self-heal path). There is no
        // background push from the worker, so this mirror can lag reality until something
        // refreshes it — the short-circuit below compensates with a synchronous re-check rather
        // than trusting a stale snapshot. Do not assume search/list/lifecycle calls keep it warm.
        private sealed class IndexStateSnapshot
        {
            public string Status = "Cold";
            public int TotalObjects;
            public DateTime? LastIndexedAt;
            public double? Progress;
            public int? EtaMs;
            public DateTime RefreshedAtUtc = DateTime.MinValue;
            // PERFORMANCE (W-M2): mirror the worker's flush-failure telemetry so whoami
            // surfaces silent snapshot persistence failures (disk full / permission).
            public int FlushFailuresConsecutive;
            public DateTime? FlushLastSuccessUtc;
            public string? FlushLastError;
            // v2.6.8: top-5 recently-changed projection from the worker's
            // in-memory index. Cached so subsequent whoami calls don't pay
            // another round-trip — refreshed every TryRefreshIndexStateFromWorkerAsync.
            public JArray? RecentlyChanged;
        }
        private static IndexStateSnapshot _lastKnownIndexState = new IndexStateSnapshot();
        private static readonly object _lastKnownIndexStateLock = new object();

        // Per-KB database configuration (DataStores). Read once per KB via the worker
        // on first whoami after open; cached until the gateway restarts or KB switches.
        // DataStore config is stable across a session — re-fetching every whoami is
        // pure waste. Keyed by normalized KB alias.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JObject> _databaseInfoByKb
            = new System.Collections.Concurrent.ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        // PERFORMANCE (perf round 5): whoami is the most-called first-turn tool, and
        // DetectGeneXusVersion hits the disk on EVERY call (up to 3 version.txt probes
        // plus FileVersionInfo on GeneXus.exe). The GeneXus install version is stable
        // across a gateway session, so memoize per install path with a 60s TTL.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Version, DateTime AtUtc)> _gxVersionCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (string?, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan GxVersionCacheTtl = TimeSpan.FromSeconds(60);

        // PERFORMANCE (perf round 5): kbValid walks the KB root directory (EnumerateFiles)
        // on every whoami. KB validity is stable across a session — memoize per path with
        // a 15s TTL so a tight whoami retry loop stops paying the directory walk.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Valid, DateTime AtUtc)> _kbValidCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan KbValidCacheTtl = TimeSpan.FromSeconds(15);

        private static string? GetCachedGxVersion(string? gxPath)
        {
            if (string.IsNullOrWhiteSpace(gxPath)) return null;
            if (_gxVersionCache.TryGetValue(gxPath!, out var cached)
                && (DateTime.UtcNow - cached.AtUtc) < GxVersionCacheTtl)
            {
                return cached.Version;
            }
            string? fresh = DetectGeneXusVersion(gxPath);
            _gxVersionCache[gxPath!] = (fresh, DateTime.UtcNow);
            return fresh;
        }

        private static bool IsKbPathValid(string kbPath)
        {
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath)) return false;
            if (_kbValidCache.TryGetValue(kbPath, out var cached)
                && (DateTime.UtcNow - cached.AtUtc) < KbValidCacheTtl)
            {
                return cached.Valid;
            }
            bool valid = false;
            try
            {
                valid = Directory.EnumerateFiles(kbPath).Any(f =>
                    f.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(f).Equals("KnowledgeBase.Connection", StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            _kbValidCache[kbPath] = (valid, DateTime.UtcNow);
            return valid;
        }

        internal static void UpdateLastKnownIndexState(string status, int totalObjects, DateTime? lastIndexedAt, double? progress, int? etaMs,
            int flushFailuresConsecutive = 0, DateTime? flushLastSuccessUtc = null, string? flushLastError = null,
            JArray? recentlyChanged = null)
        {
            IndexStateSnapshot? previous;
            lock (_lastKnownIndexStateLock)
            {
                previous = _lastKnownIndexState;
                _lastKnownIndexState = new IndexStateSnapshot
                {
                    Status = string.IsNullOrEmpty(status) ? "Cold" : status,
                    TotalObjects = totalObjects,
                    LastIndexedAt = lastIndexedAt,
                    Progress = progress,
                    EtaMs = etaMs,
                    RefreshedAtUtc = DateTime.UtcNow,
                    FlushFailuresConsecutive = flushFailuresConsecutive,
                    FlushLastSuccessUtc = flushLastSuccessUtc,
                    FlushLastError = flushLastError,
                    // Preserve prior recentlyChanged when the caller doesn't pass a fresh
                    // value — search/lifecycle pushes update telemetry without it.
                    RecentlyChanged = recentlyChanged ?? _lastKnownIndexState?.RecentlyChanged
                };
            }
            // Keep AutoTypeInjector's name→type map warm whenever we get fresh index data.
            // Plan 038: scope the refresh to the KB it actually came from. This feeder runs
            // from whoami's own dispatch (a meta-tool — _currentKb is null there), so fall
            // back to the single open KB, mirroring TryRefreshDatabaseInfoFromWorkerAsync's
            // established pattern; skip (don't guess) when more than one KB is open.
            if (recentlyChanged != null)
            {
                string? alias = ResolveKbAliasForIndexRefresh();
                if (!string.IsNullOrEmpty(alias))
                    AutoTypeInjector.RefreshFromRecentlyChanged(alias!, recentlyChanged);
            }

            // Root-cause fix (Table-shadow auto-injection): the top-5 RecentlyChanged
            // window cannot establish real uniqueness — a Transaction's physical Table
            // shadow can win the window without the sibling Transaction ever appearing.
            // Once the index reaches a usable state, fetch the FULL name→[types] map
            // once per KB alias (fire-and-forget; the per-alias gate keeps it to one
            // fetch per gateway process) and rebuild the injector from it, so
            // Transaction+Table → Transaction resolves deterministically.
            try
            {
                string? mapAlias = ResolveKbAliasForIndexRefresh();
                if (!string.IsNullOrEmpty(mapAlias))
                {
                    // A new index pass invalidates both the full map and the recent
                    // window. Without this reset, rename/delete/retype changes remain
                    // hidden behind the once-per-alias fetch gate until gateway restart.
                    bool indexWasInvalidated = previous != null
                        && IndexStatusUsable(previous.Status)
                        && !IndexStatusUsable(status);
                    if (indexWasInvalidated)
                    {
                        InvalidateFullNameTypeMap(mapAlias!);
                    }

                    if (IndexStatusUsable(status))
                        MaybeFetchFullNameTypeMap(mapAlias!);
                }
            }
            catch { /* never break the telemetry update over a background fetch */ }
        }

        // Plan 038: same fallback pattern as TryRefreshDatabaseInfoFromWorkerAsync — try the
        // per-request resolved KB first, else the single open KB if unambiguous, else null.
        private static string? ResolveKbAliasForIndexRefresh()
        {
            KbHandle? kb = _currentKb.Value;
            string? alias = kb?.NormalizedAlias;
            if (!string.IsNullOrEmpty(alias)) return alias;
            if (_workerPool == null) return null;
            try
            {
                var open = _workerPool.ListOpen();
                if (open != null && open.Count == 1) return open[0].NormalizedAlias;
            }
            catch { }
            return null;
        }

        // Root-cause fix: name→type map fed from the FULL index. The top-5
        // RecentlyChanged window cannot establish real uniqueness (a Transaction's
        // Table shadow can win the window without its sibling Transaction appearing),
        // so once the index reaches a usable state we fetch the complete map once per
        // KB alias and rebuild the injector from it (Transaction+Table → Transaction is
        // now resolved deterministically instead of just refused).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _fullNameTypeMapFetched
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _fullNameTypeMapGeneration
            = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _fullNameTypeMapStateLock = new object();

        // Deliberately EXCLUDES UltraLiteReady: the lite pass streams partial
        // snapshots every 500-1000 objects, so a full map fetched at that point would
        // be incomplete — and the once-per-KB gate would pin the partial map forever.
        // Fetch only once the lite pass COMPLETED (LiteReady/Enriching/Ready).
        private static bool IndexStatusUsable(string status) =>
            string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "LiteReady", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Enriching", StringComparison.OrdinalIgnoreCase);

        // Fire-and-forget fetch of the worker's full name→[types] map, applied to the
        // AutoTypeInjector's per-KB map. Never blocks whoami; best-effort (any failure
        // leaves the recentlyChanged-primed map in place).
        private static void MaybeFetchFullNameTypeMap(string alias)
        {
            if (_workerPool == null) return;
            if (!TryArmFullNameTypeMapFetch(alias, out int generation)) return; // once per KB per process
            _ = Task.Run(async () =>
            {
                bool applied = false;
                try
                {
                    var cmd = new JObject { ["module"] = "kb", ["action"] = "GetNameTypeMap" };
                    JObject? env = await SendWorkerCommandAsync(
                        cmd,
                        4000,
                        "Timeout fetching full name→type map for auto-injection",
                        e => e,
                        (_, correlationId) => new JObject { ["__timeout"] = true, ["correlationId"] = correlationId },
                        toolName: "genexus_whoami",
                        toolArgs: null,
                        trackOperation: false);
                    if (env == null || env["__timeout"] != null) return;
                    JObject? result = env["result"] as JObject;
                    if (result == null) return;

                    // A reindex or close/reopen can invalidate this request while the
                    // worker is still answering. Never let an older map overwrite the
                    // new KB generation.
                    applied = ApplyNameTypeMapFromWorkerResultIfCurrent(alias, result, generation);
                }
                catch (Exception ex)
                {
                    Log($"[Whoami] full name→type map fetch failed: {ex.Message}");
                }
                finally
                {
                    // Re-arm the once-per-KB gate on any failure so the next whoami
                    // push retries — a one-shot timeout would otherwise regress this
                    // KB to window-only injection for the whole process lifetime.
                    if (!applied)
                        ReleaseFullNameTypeMapGate(alias, generation);
                }
            });
        }

        // Once-per-KB gate: mark this alias as fetched. Returns true only for the
        // first caller; every later whoami push for the same alias no-ops here.
        internal static bool TryArmFullNameTypeMapFetch(string alias) =>
            TryArmFullNameTypeMapFetch(alias, out _);

        private static bool TryArmFullNameTypeMapFetch(string alias, out int generation)
        {
            lock (_fullNameTypeMapStateLock)
            {
                generation = _fullNameTypeMapGeneration.GetOrAdd(alias, 0);
                return _fullNameTypeMapFetched.TryAdd(alias, 0);
            }
        }

        // Re-arm the once-per-KB gate (call on fetch failure) so the next whoami
        // push retries instead of permanently regressing to window-only injection.
        internal static void ReleaseFullNameTypeMapGate(string alias) =>
            ReleaseFullNameTypeMapGate(alias, GetFullNameTypeMapGeneration(alias));

        private static void ReleaseFullNameTypeMapGate(string alias, int generation)
        {
            lock (_fullNameTypeMapStateLock)
            {
                if (_fullNameTypeMapGeneration.TryGetValue(alias, out int current) && current == generation)
                    _fullNameTypeMapFetched.TryRemove(alias, out _);
            }
        }

        internal static int GetFullNameTypeMapGeneration(string alias)
        {
            lock (_fullNameTypeMapStateLock)
            {
                return _fullNameTypeMapGeneration.GetOrAdd(alias, 0);
            }
        }

        internal static void InvalidateFullNameTypeMap(string alias)
        {
            lock (_fullNameTypeMapStateLock)
            {
                _fullNameTypeMapGeneration.AddOrUpdate(alias, 1, (_, current) => unchecked(current + 1));
                AutoTypeInjector.ClearAll(alias);
                _fullNameTypeMapFetched.TryRemove(alias, out _);
            }
        }

        internal static bool ApplyNameTypeMapFromWorkerResultIfCurrent(string alias, JObject? workerResult, int expectedGeneration)
        {
            lock (_fullNameTypeMapStateLock)
            {
                if (!_fullNameTypeMapGeneration.TryGetValue(alias, out int current) || current != expectedGeneration)
                    return false;

                return ApplyNameTypeMapFromWorkerResult(alias, workerResult);
            }
        }

        // Parses the worker's GetNameTypeMap reply and rebuilds the AutoTypeInjector
        // name→type map for `alias`. Extracted from MaybeFetchFullNameTypeMap so the
        // envelope-shape handling is unit-testable without a worker (mirrors
        // ApplyIndexStateFromWorkerResult). `workerResult` is env["result"] — the
        // JSON-RPC result the worker returned for the GetNameTypeMap command.
        // Returns true when a map was applied.
        internal static bool ApplyNameTypeMapFromWorkerResult(string alias, JObject? workerResult)
        {
            if (workerResult == null) return false;

            // v2.8.1 canonical envelope: { status:"ok", code:"NameTypeMap",
            // result:{ nameTypeMap, totalNames } } — descend into result when the
            // current level lacks nameTypeMap.
            JObject payload = workerResult;
            if (payload["nameTypeMap"] == null && payload["result"] is JObject canonicalInner && canonicalInner["nameTypeMap"] != null)
                payload = canonicalInner;

            var nameTypeMap = payload["nameTypeMap"] as JObject;
            if (nameTypeMap == null) return false;

            AutoTypeInjector.ApplyFullNameTypeMap(alias, nameTypeMap);
            Log($"[Whoami] applied full name→type map for '{alias}' ({nameTypeMap.Count} names).");
            return true;
        }

        // True when the index has enough populated entries for SDK-bound reads/edits.
        // v2.6.9 perf: accept UltraLiteReady + LiteReady + Enriching as usable too. The lite
        // pass streams partial snapshots every 500-1000 objects (UltraLiteReady), then completes
        // (LiteReady), then enrichment runs in background (Enriching → Ready). All four states
        // have name/type/path/lifecycle populated — enough for list_objects, query, inspect,
        // read, explain, edit. mode=impact does on-demand per-target enrichment. (Note: "Complete"
        // is KbService._currentStatus vocabulary, NOT an IndexCacheService state — the worker
        // publishes "Ready" here when indexing finishes; the two must not be conflated.)
        private static bool IsIndexUsableForReads(IndexStateSnapshot snap)
        {
            // Usability is decided by the index STATUS alone, not the object count. A
            // Ready/LiteReady/Enriching index with 0 objects is a legitimately-built EMPTY
            // KB (the lite walk completed and found no model objects — e.g. a KB whose
            // LocalDB model is missing): the worker's ListService/SearchService return an
            // honest empty listing there, so reads must be forwarded, not fast-failed with
            // IndexNotReady (which left agents looping `lifecycle action=index force=true`
            // on empty KBs forever). Only Cold / Reindexing / unknown states block.
            if (snap == null) return false;
            string s = snap.Status ?? string.Empty;
            return string.Equals(s, "Ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "LiteReady", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Enriching", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "UltraLiteReady", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildIndexBlock()
        {
            IndexStateSnapshot snap;
            lock (_lastKnownIndexStateLock) { snap = _lastKnownIndexState; }
            return new JObject
            {
                ["status"] = snap.Status,
                ["totalObjects"] = snap.TotalObjects,
                ["lastIndexedAt"] = snap.LastIndexedAt.HasValue
                    ? (JToken)snap.LastIndexedAt.Value.ToUniversalTime().ToString("o")
                    : JValue.CreateNull(),
                ["progress"] = snap.Progress.HasValue ? (JToken)snap.Progress.Value : JValue.CreateNull(),
                ["etaMs"] = snap.EtaMs.HasValue ? (JToken)snap.EtaMs.Value : JValue.CreateNull(),
                // PERFORMANCE (W-M2): expose flush health so a degraded snapshot is
                // visible without combing through worker_debug.log.
                ["flushHealth"] = new JObject
                {
                    ["consecutiveFailures"] = snap.FlushFailuresConsecutive,
                    ["lastSuccessUtc"] = snap.FlushLastSuccessUtc.HasValue
                        ? (JToken)snap.FlushLastSuccessUtc.Value.ToUniversalTime().ToString("o")
                        : JValue.CreateNull(),
                    ["lastError"] = snap.FlushLastError != null ? (JToken)snap.FlushLastError : JValue.CreateNull()
                },
                // v2.6.8: top-5 recently-changed objects — set only when the worker
                // had populated lifecycle data to surface. Omitted otherwise so the
                // whoami payload stays tight for cold/legacy KBs.
                ["recentlyChanged"] = snap.RecentlyChanged != null
                    ? (JToken)snap.RecentlyChanged.DeepClone()
                    : JValue.CreateNull()
            };
        }

        // v2.3.8 Task 1.2: live-fetch index state from worker (source of truth).
        // Called from BuildWhoamiPayloadAsync; on success refreshes _lastKnownIndexState
        // so subsequent timeouts/worker outages still see the last good value.
        // Short timeout (1500ms): whoami is supposed to be near-instant.
        private static async Task<bool> TryRefreshIndexStateFromWorkerAsync(int timeoutMs = 1500)
        {
            if (_workerPool == null) return false;
            try
            {
                var cmd = new JObject
                {
                    ["module"] = "kb",
                    ["action"] = "GetIndexState"
                };
                JObject? env = await SendWorkerCommandAsync(
                    cmd,
                    timeoutMs,
                    "Timeout fetching index state for whoami",
                    e => e,
                    (_, correlationId) => new JObject { ["__timeout"] = true, ["correlationId"] = correlationId },
                    toolName: "genexus_whoami",
                    toolArgs: null,
                    trackOperation: false);

                if (env == null) return false;
                if (env["__timeout"] != null) return false;
                JObject? result = env["result"] as JObject;
                if (result == null) return false;

                return ApplyIndexStateFromWorkerResult(result);
            }
            catch (Exception ex)
            {
                Log($"[Whoami] index state fetch failed; using cached snapshot: {ex.Message}");
                return false;
            }
        }

        // Parses the worker's GetIndexState payload and refreshes _lastKnownIndexState.
        // Extracted from TryRefreshIndexStateFromWorkerAsync so the envelope-shape handling
        // is unit-testable without spinning up a worker. `workerResult` is env["result"] —
        // the JSON-RPC result the worker returned for the GetIndexState command.
        // Returns true when a state was applied.
        internal static bool ApplyIndexStateFromWorkerResult(JObject workerResult)
        {
            if (workerResult == null) return false;

            // The worker may wrap the JSON-RPC result as either a JObject or a string payload.
            JObject state = workerResult;
            if (state["indexStatus"] == null && state["status"] == null && state["data"] is JValue dv && dv.Type == JTokenType.String)
            {
                try { state = JObject.Parse(dv.ToString()); } catch { return false; }
            }

            // v2.8.1 — v2.8.0 (commit e639b04) wrapped the worker's GetIndexState reply in the
            // canonical McpResponse.Ok envelope { status:"ok", code:"IndexState", result:{ … } },
            // which nests the real index payload one level below env["result"]. Without descending
            // we read the envelope's status:"ok" / totalObjects:null (→0), so the gateway's
            // SDK-bound short-circuit fast-failed every read/query/list with IndexNotReady even
            // though the worker's index was Ready. Descend only when the current level lacks
            // indexStatus but the nested result carries it — pre-2.8.0 flat replies pass through.
            if (state["indexStatus"] == null && state["result"] is JObject canonicalInner && canonicalInner["indexStatus"] != null)
            {
                state = canonicalInner;
            }

            string status = state["indexStatus"]?.ToString() ?? state["status"]?.ToString() ?? "Cold";
            int totalObjects = state["totalObjects"]?.ToObject<int?>() ?? 0;
            DateTime? lastIndexedAt = null;
            var liTok = state["lastIndexedAt"];
            if (liTok != null && liTok.Type != JTokenType.Null)
            {
                if (DateTime.TryParse(liTok.ToString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                {
                    lastIndexedAt = parsed;
                }
            }
            double? progress = state["progress"]?.ToObject<double?>();
            int? etaMs = state["etaMs"]?.ToObject<int?>();
            int flushFailuresConsecutive = state["flushFailuresConsecutive"]?.ToObject<int?>() ?? 0;
            DateTime? flushLastSuccessUtc = null;
            var fls = state["flushLastSuccessUtc"];
            if (fls != null && fls.Type != JTokenType.Null &&
                DateTime.TryParse(fls.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var flsParsed))
            {
                flushLastSuccessUtc = flsParsed;
            }
            string? flushLastError = state["flushLastError"]?.Type == JTokenType.Null ? null : state["flushLastError"]?.ToString();

            JArray? recentlyChanged = state["recentlyChanged"] as JArray;
            UpdateLastKnownIndexState(status, totalObjects, lastIndexedAt, progress, etaMs,
                flushFailuresConsecutive, flushLastSuccessUtc, flushLastError, recentlyChanged);
            return true;
        }

        private static async Task<bool> TryRefreshDatabaseInfoFromWorkerAsync(int timeoutMs = 800)
        {
            if (_workerPool == null) return false;
            KbHandle? kb = _currentKb.Value;
            string? alias = kb?.NormalizedAlias;
            if (string.IsNullOrEmpty(alias))
            {
                // whoami is a meta-tool, so the per-request KB isn't resolved and _currentKb is
                // null — which left the database block stuck at "Pending" because this refresh
                // returned before ever dispatching GetDatabaseInfo. (The index refresh isn't
                // affected: it doesn't key by alias.) Fall back to the single open KB so the
                // block populates; with multiple KBs open there's no unambiguous "the" KB, so skip.
                try
                {
                    var open = _workerPool.ListOpen();
                    if (open != null && open.Count == 1) alias = open[0].NormalizedAlias;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(alias)) return false;
            if (_databaseInfoByKb.ContainsKey(alias!)) return true;
            try
            {
                var cmd = new JObject { ["module"] = "kb", ["action"] = "GetDatabaseInfo" };
                JObject? env = await SendWorkerCommandAsync(
                    cmd, timeoutMs,
                    "Timeout fetching database info for whoami",
                    e => e,
                    (_, correlationId) => new JObject { ["__timeout"] = true, ["correlationId"] = correlationId },
                    toolName: "genexus_whoami",
                    toolArgs: null,
                    trackOperation: false);

                JObject? info = ExtractDatabaseInfoFromWorkerResult(env);
                if (info == null) return false;
                _databaseInfoByKb[alias!] = info;
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Whoami] database info fetch failed: {ex.Message}");
                return false;
            }
        }

        // Extracts the database-info payload from a worker GetDatabaseInfo reply, or null when the
        // reply didn't signal success. `env` is the JSON-RPC response (its `result` holds the
        // worker's payload). Extracted from TryRefreshDatabaseInfoFromWorkerAsync so the envelope
        // handling is unit-testable without a worker.
        //
        // v2.8.1 — v2.8.0 (commit e639b04) wrapped GetDatabaseInfo in the canonical McpResponse.Ok
        // envelope { status:"ok", code:"DatabaseInfoCollected", result:{ default, … } }. whoami's
        // database block and the SQL-dialect injection read the store fields (default/additional)
        // off the top level, so descend into the nested result. Pre-2.8.0 flat replies (store
        // fields beside status, no nested result) pass through unchanged.
        internal static JObject? ExtractDatabaseInfoFromWorkerResult(JObject? env)
        {
            if (env == null || env["__timeout"] != null) return null;
            JObject? result = env["result"] as JObject;
            if (result == null) return null;

            JObject info = result;
            if (result["status"] == null && result["data"] is JValue dv && dv.Type == JTokenType.String)
            {
                try { info = JObject.Parse(dv.ToString()); } catch { return null; }
            }
            // success is signalled by env["status"] == "ok"; legacy envelope carried
            // status:"Success" inside result. Accept either form. Check the OUTER envelope
            // before unwrapping, since the canonical "status" lives there.
            bool dbInfoOk = string.Equals(env["status"]?.ToString(), "ok", StringComparison.Ordinal)
                         || string.Equals(info["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
            if (!dbInfoOk) return null;

            if (info["result"] is JObject canonicalInner && (info["code"] != null || info["status"] != null))
            {
                info = canonicalInner;
            }
            return info;
        }

        private static JObject? GetCachedDatabaseInfo()
        {
            KbHandle? kb = _currentKb.Value;
            string? alias = kb?.NormalizedAlias;
            if (string.IsNullOrEmpty(alias)) return null;
            return _databaseInfoByKb.TryGetValue(alias!, out var info) ? info : null;
        }

        // v2.3.8 Task 1.2: async variant that performs a live fetch against the worker
        // before assembling whoami. The sync BuildWhoamiPayload() is kept for tests and
        // any caller that doesn't want to block on a worker round-trip.
        internal static Task<JObject> BuildWhoamiPayloadAsync() => BuildWhoamiPayloadAsync(false, null);

        internal static Task<JObject> BuildWhoamiPayloadAsync(bool verbose)
            => BuildWhoamiPayloadAsync(verbose, null);

        internal static async Task<JObject> BuildWhoamiPayloadAsync(bool verbose, string? sessionId)
        {
            // Skip the worker round-trip when our cached snapshot is recent enough.
            // whoami is the most-called first-turn tool — a stale-by-a-few-seconds
            // index status is far better UX than a 1.5s blocking call. Search/lifecycle
            // paths refresh the snapshot whenever they receive new telemetry, so the
            // cache stays warm during real use.
            IndexStateSnapshot snap;
            lock (_lastKnownIndexStateLock) { snap = _lastKnownIndexState; }
            bool cacheFresh = snap.RefreshedAtUtc != DateTime.MinValue
                && (DateTime.UtcNow - snap.RefreshedAtUtc).TotalSeconds < 15;

            // v2.6.8: degraded mode. If the worker process is dead or still booting,
            // don't even attempt the round-trip — clients with tight timeouts (VS
            // Code Codex closes the transport after a few seconds) will get a
            // structured "Booting" answer instantly instead of a hang.
            bool workerHealthy = IsActiveWorkerHealthy();
            if (!cacheFresh && workerHealthy)
            {
                // v2.6.9 perf: 400ms aggressive timeout — when the worker is slow to
                // respond (cold STA thread, busy with another call), we don't want
                // every whoami to block here. After the timeout, if the cache is
                // STILL empty (RefreshedAtUtc == MinValue), stamp a placeholder
                // snapshot so the next whoami call inside the 15s window hits
                // cacheFresh and returns instantly. The worker's own telemetry push
                // path (index/lifecycle progress) will overwrite the placeholder
                // with real data as soon as it arrives. Net effect on the bench:
                // first whoami pays ~400ms once, subsequent calls drop to ms-range.
                bool refreshed = await TryRefreshIndexStateFromWorkerAsync(timeoutMs: 400).ConfigureAwait(false);
                // DB info is stable; fetch once per KB and cache forever. Await on first
                // whoami of a session so the database block populates inline; subsequent
                // calls short-circuit on the cache and pay nothing. The timeout is generous
                // (3s) because GetDatabaseInfo enumerates the DataStoresPart across the
                // environment's models, which the SDK lazy-loads on first touch after a cold
                // start — 600ms missed it, leaving database stuck at "Pending" for the session.
                await TryRefreshDatabaseInfoFromWorkerAsync(timeoutMs: 3000).ConfigureAwait(false);
                if (!refreshed)
                {
                    lock (_lastKnownIndexStateLock)
                    {
                        if (_lastKnownIndexState.RefreshedAtUtc == DateTime.MinValue)
                        {
                            // Stamp a "Unknown" placeholder. Status stays Cold so the
                            // agent can still see that the index hasn't reported yet;
                            // we just stop hammering the round-trip every call.
                            _lastKnownIndexState = new IndexStateSnapshot
                            {
                                Status = "Cold",
                                TotalObjects = 0,
                                RefreshedAtUtc = DateTime.UtcNow,
                                RecentlyChanged = _lastKnownIndexState?.RecentlyChanged
                            };
                        }
                    }
                }
            }
            var payload = BuildWhoamiPayload(verbose, sessionId);
            if (!workerHealthy)
            {
                // v2.6.8 (review C7): workerHealth is purely additive — emit it
                // whenever the worker is down, regardless of cache freshness, so
                // the agent always has a signal that tool calls may transient-fail.
                // The index.status downgrade stays gated on !cacheFresh because the
                // last-good snapshot is still useful when it's recent.
                if (!cacheFresh && payload["index"] is JObject idx)
                {
                    idx["status"] = "Booting";
                }
                payload["workerHealth"] = BuildHonestWorkerHealth(payload);
            }
            return payload;
        }

        // v2.6.8: cheap health check used by whoami's degraded-mode path. Returns
        // true only when a worker process exists and HasExited is false; any error
        // looking at the pool is treated as "not healthy" so we err on the side of
        // skipping the round-trip.
        private static bool IsActiveWorkerHealthy()
        {
            try
            {
                if (_workerPool == null) return false;
                // v2.6.8 (review C3): prefer the AsyncLocal-bound KB if present;
                // otherwise probe every open worker — healthy if at least one is
                // alive. This avoids KbResolver.Resolve(null, ...) throwing on
                // multi-KB or no-default-KB setups (which the outer catch would
                // turn into a false 'respawning' signal).
                KbHandle? kb = _currentKb.Value;
                if (kb != null)
                {
                    var worker = _workerPool.TryGet(kb.NormalizedAlias);
                    return worker != null && worker.Pid.HasValue;
                }
                var open = _workerPool.ListOpen();
                if (open == null || open.Count == 0) return false;
                foreach (var handle in open)
                {
                    var w = _workerPool.TryGet(handle.NormalizedAlias);
                    if (w != null && w.Pid.HasValue) return true;
                }
                return false;
            }
            catch { return false; }
        }

        // issue #26 P1: report the worker's REAL state instead of a blanket "respawning".
        // The old code always said "respawning" whenever no live worker was found — even
        // when nothing was actually spawning and no self-heal would ever happen, stranding
        // the agent for minutes. This distinguishes:
        //   starting        — a process really is coming up right now (or we just kicked one)
        //   respawn_failed   — eager respawn exhausted its retries; needs manual recovery
        //   no_worker        — no KB opened yet / nothing to run
        // and, crucially, SELF-HEALS: when there's a known KB but no worker and none
        // spawning, it kicks an AcquireAsync so the worker actually comes back without the
        // agent having to run worker_reload force by hand.
        private static JObject BuildHonestWorkerHealth(JObject payload)
        {
            try
            {
                var pool = _workerPool;
                if (pool == null)
                    return new JObject { ["status"] = "no_worker", ["hint"] = "Gateway not fully initialised yet. Retry whoami shortly." };

                // Which KB does the session care about? Prefer whoami's resolved active
                // alias, else the AsyncLocal, else the single/first known KB.
                string? alias = payload?["kb"]?["active"]?.ToString();
                if (string.IsNullOrEmpty(alias)) alias = _currentKb.Value?.NormalizedAlias;
                var known = pool.ListKnown();
                if (string.IsNullOrEmpty(alias)) alias = known.FirstOrDefault()?.NormalizedAlias;

                if (!string.IsNullOrEmpty(alias) && pool.IsSpawning(alias))
                {
                    return new JObject
                    {
                        ["status"] = "starting",
                        ["hint"] = "Worker process is coming up (cold load ~10–15s). Retry in a few seconds; tool calls during this window may return 'crashed/exited' and should be retried."
                    };
                }

                if (!string.IsNullOrEmpty(alias)
                    && _respawnFailures.TryGetValue(alias!, out var fail))
                {
                    return new JObject
                    {
                        ["status"] = "respawn_failed",
                        ["alias"] = alias,
                        ["error"] = fail.Error,
                        ["failedAtUtc"] = fail.AtUtc,
                        ["hint"] = "The gateway tried and failed to respawn this KB's worker. Recover with genexus_worker_reload mode=soft force=true, then reopen/retry. This is NOT a transient cold-start."
                    };
                }

                // No live worker, not spawning, no recorded failure. If we know the KB,
                // self-heal by kicking a spawn now (fire-and-forget) and report "starting".
                if (!string.IsNullOrEmpty(alias))
                {
                    var handle = known.FirstOrDefault(h =>
                        string.Equals(h.NormalizedAlias, alias, StringComparison.OrdinalIgnoreCase));
                    if (handle != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await pool.AcquireAsync(handle, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token).ConfigureAwait(false); }
                            catch (Exception ex) { _respawnFailures[handle.NormalizedAlias] = (DateTime.UtcNow, ex.Message); }
                        });
                        return new JObject
                        {
                            ["status"] = "starting",
                            ["alias"] = alias,
                            ["hint"] = "No live worker was found for this KB; the gateway is starting one now. Retry in a few seconds."
                        };
                    }
                }

                return new JObject
                {
                    ["status"] = "no_worker",
                    ["hint"] = "No KB worker is running. Open a KB with genexus_kb action=open path=<kbPath> (or set a DefaultKb), then retry."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "unknown", ["hint"] = "Worker health probe failed: " + ex.Message };
            }
        }

        // PERFORMANCE (perf round 5): CrashLedger.Summarize reads the ledger file on
        // every call; whoami is the most-called first-turn tool. Cache the summary with
        // a 10s TTL — a stale-by-seconds death count is far better than a disk read per
        // whoami. Invalidated implicitly by the TTL; writes to the ledger go through
        // CrashLedger.Record which does NOT touch this cache (staleness bounded at 10s).
        private static readonly object _crashSummaryLock = new object();
        private static JObject _crashSummary = new JObject();
        private static DateTime _crashSummaryAtUtc = DateTime.MinValue;
        private static readonly TimeSpan CrashSummaryCacheTtl = TimeSpan.FromSeconds(10);

        internal static JObject BuildWhoamiPayload() => BuildWhoamiPayload(false, null);

        // issue #25 #5: whoami defaulted to dumping ~3k tokens of STATIC content
        // (playbooks + skills catalog) plus a session-growing stats/heatmap block
        // on EVERY call — wasteful when the agent just wants a health check after a
        // worker respawn. Lean by default (verbose=false): keep the dynamic health
        // blocks (kb/geneXus/update/worker/index/database/metricsSummary/suggestedNext)
        // and drop the static reference blocks. verbose=true restores the full payload.
        // For a pure connection/index health probe, genexus_doctor is even leaner.
        internal static JObject BuildWhoamiPayload(bool verbose)
            => BuildWhoamiPayload(verbose, null);

        internal static JObject BuildWhoamiPayload(bool verbose, string? sessionId)
        {
            var cfg = _activeConfig;
            string? gxPath = cfg?.GeneXus?.InstallationPath;

            // issue #26 P4/P1: report the KB the session is ACTUALLY working against,
            // not the raw Environment.KBPath scaffold (which `kb open` never updated, so
            // whoami used to keep showing the empty "YourKB" while real work went to a
            // different, explicitly-opened KB). A default is authoritative when several
            // workers are live; with no selection, only a single worker is reported as
            // active so whoami never invents a target in a multi-KB session.
            string? activeAlias = null;
            string? kbPath = null;
            IReadOnlyList<KbHandle> openKbs = Array.Empty<KbHandle>();
            IReadOnlyList<KbHandle> knownKbs = Array.Empty<KbHandle>();
            string? configuredDefault = GetConfiguredDefaultKb();
            string? sessionSelected = !string.IsNullOrWhiteSpace(sessionId)
                ? GetSessionSelectedKb(sessionId!)
                : null;
            try
            {
                var pool = _workerPool;
                openKbs = pool?.ListOpen() ?? Array.Empty<KbHandle>();
                knownKbs = pool?.ListKnown() ?? Array.Empty<KbHandle>();
                KbHandle? pick = sessionSelected != null
                    ? openKbs.FirstOrDefault(h => string.Equals(h.Alias, sessionSelected, StringComparison.OrdinalIgnoreCase))
                        ?? knownKbs.FirstOrDefault(h => string.Equals(h.Alias, sessionSelected, StringComparison.OrdinalIgnoreCase))
                    : (configuredDefault != null
                        ? openKbs.FirstOrDefault(h => string.Equals(h.Alias, configuredDefault, StringComparison.OrdinalIgnoreCase))
                        : null)
                        ?? (openKbs.Count == 1 ? openKbs[0] : null)
                        ?? (configuredDefault != null
                            ? knownKbs.FirstOrDefault(h => string.Equals(h.Alias, configuredDefault, StringComparison.OrdinalIgnoreCase))
                            : null)
                        ?? (knownKbs.Count == 1 ? knownKbs[0] : null);
                if (pick != null) { activeAlias = pick.Alias; kbPath = pick.Path; }
                else if (!string.IsNullOrWhiteSpace(sessionSelected))
                {
                    var sessionDecl = cfg?.Environment?.KBs?.FirstOrDefault(
                        k => string.Equals(k.Alias, sessionSelected, StringComparison.OrdinalIgnoreCase));
                    if (sessionDecl != null) { activeAlias = sessionDecl.Alias; kbPath = sessionDecl.Path; }
                }
                else if (!string.IsNullOrWhiteSpace(configuredDefault))
                {
                    var decl = cfg?.Environment?.KBs?.FirstOrDefault(
                        k => string.Equals(k.Alias, configuredDefault, StringComparison.OrdinalIgnoreCase));
                    if (decl != null) { activeAlias = decl.Alias; kbPath = decl.Path; }
                }
            }
            catch { }
            // Do not report the legacy KBPath scaffold as the selected target when
            // this session points at an alias that is currently unavailable.
            if (string.IsNullOrEmpty(kbPath) && string.IsNullOrWhiteSpace(sessionSelected))
                kbPath = cfg?.Environment?.KBPath;
            string? kbName = !string.IsNullOrEmpty(kbPath) ? Path.GetFileName(kbPath!.TrimEnd('\\', '/')) : null;
            bool kbExists = !string.IsNullOrEmpty(kbPath) && Directory.Exists(kbPath);
            bool kbValid = kbExists && IsKbPathValid(kbPath!);
            string? gxVersion = GetCachedGxVersion(gxPath);

            var payload = new JObject
            {
                ["connected"] = cfg != null,
                ["kb"] = new JObject
                {
                    ["name"] = kbName,
                    ["path"] = kbPath,
                    ["exists"] = kbExists,
                    ["looksValid"] = kbValid,
                    // issue #26 P4: the alias actually active this session (null if none
                    // opened yet), and how many workers are live — so the agent can tell
                    // an opened KB apart from the config scaffold.
                    ["active"] = activeAlias,
                    ["selected"] = sessionSelected,
                    ["default"] = configuredDefault,
                    ["openCount"] = openKbs.Count,
                    // Keep whoami lean: aliases are enough to choose a target. Detailed
                    // process/path telemetry remains in genexus_kb action=list.
                    ["openKbs"] = JArray.FromObject(openKbs
                        .Select(k => k.Alias)
                        .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)),
                    ["knownKbs"] = JArray.FromObject(knownKbs
                        .Select(k => k.Alias)
                        .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)),
                    ["declaredKbs"] = JArray.FromObject((cfg?.Environment?.KBs ?? new List<KbEntry>())
                        .Select(k => k.Alias)
                        .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase))
                },
                ["geneXus"] = new JObject
                {
                    ["installationPath"] = gxPath,
                    ["version"] = gxVersion,
                    ["supportedMajor"] = SupportedGeneXusMajor,
                    ["versionMatches"] = gxVersion != null && gxVersion.StartsWith(SupportedGeneXusMajor, StringComparison.OrdinalIgnoreCase)
                },
                ["config"] = new JObject
                {
                    ["path"] = Configuration.CurrentConfigPath
                },
                ["mcp"] = new JObject
                {
                    ["serverVersion"] = McpRouter.ServerVersion,
                    ["protocolVersion"] = McpRouter.SupportedProtocolVersion
                },
                ["worker"] = BuildWorkerBlock(),
                // Self-update awareness — LLM-visible structured data sourced from the
                // 24h-cached UpdateNotifier result. Lets the agent check whoami.update.
                // updateAvailable before each session and proactively offer the upgrade
                // command instead of relying on the stderr-style notifications/message.
                ["update"] = UpdateNotifier.GetCachedStatusSync() ?? new JObject {
                    ["currentVersion"] = McpRouter.ServerVersion,
                    ["updateAvailable"] = false
                },
                // v2.3.8 Task 1.2: surface index readiness so agents know whether to
                // call `lifecycle action=index` before relying on search/analyze.
                ["index"] = BuildIndexBlock(),
                // Per-KB database configuration. SQL-generating tools should default to
                // database.default.dialect (oracle / sqlserver / mysql / postgres / db2 / …)
                // instead of guessing. Populated once per session via the worker, cached
                // until gateway restart. Null when no KB has been selected yet.
                ["database"] = GetCachedDatabaseInfo() ?? new JObject { ["status"] = "Pending" },
                // Compact tool-call health summary. Full per-tool breakdown remains at
                // `genexus_lifecycle status target=gateway:metrics`; whoami carries only
                // the roll-up so first-turn cost stays minimal while still surfacing red
                // flags (high error/timeout ratio, a tool with a >10s p95).
                ["metricsSummary"] = _operationTracker?.BuildMetricsSummary() ?? new JObject(),
                // v2.8.0 — concrete next-action hints so a weakly-capable LLM
                // doesn't have to guess "what do I call now?" after whoami.
                // Heuristics inspect KB / index / worker / update state and emit
                // {tool, args, why} triples matching the canonical envelope's
                // nextSteps shape. Empty when state is healthy + nothing pending.
                ["suggestedNext"] = BuildSuggestedNextBlock(kbPath, kbExists, kbValid)
            };

            // A post-timeout mutation is never retried implicitly. Surface the
            // durable fence only when it needs attention so the first-turn
            // whoami remains compact while still offering an inspect path.
            var recovery = BuildMutationRecoveryBlock();
            if (recovery["pendingCount"]?.ToObject<int>() > 0
                || recovery["journalHealthy"]?.ToObject<bool>() == false)
                payload["mutationRecovery"] = recovery;

            if (verbose)
            {
                // Item 73: per-tool latency breakdown. In-memory, resets on gateway restart.
                payload["stats"] = BuildStatsWithHeatmap();
                // Inline playbooks for the flows that drove the largest token spend
                // in real sessions. Each entry is a 1-line route, not full docs — for
                // full recipes the agent can fetch genexus_recipe(name=...).
                payload["playbooks"] = BuildPlaybooksBlock();
                // v2.8.0 — first-class skill catalog. Each entry is one resources/read away.
                payload["skills"] = BuildSkillsCatalogBlock();
            }
            else
            {
                // Terse mode: drop the reference pointer block too — a terse deployment
                // is one where the agent already knows the surface (~280 bytes saved).
                if (Program.TerseResponsesEnabled())
                {
                    payload.Remove("reference");
                    // Forensics/telemetry sugar that terse deployments don't need per call:
                    // death history with recent entries + roll-up metrics summary
                    // (~2.7KB combined). Status/pid stay in worker.status/pid.
                    if (payload["worker"] is JObject w)
                    {
                        w.Remove("deaths");
                        w.Remove("toolLatency");
                    }
                    payload.Remove("metricsSummary");
                    // Heuristic "what do I call next" triples — redundant for an agent
                    // that already knows the surface (~450 bytes).
                    payload.Remove("suggestedNext");
                    // Top-5 recently-changed with per-object metadata (~500B when set);
                    // genexus_list_objects sort=lastUpdate covers this on demand.
                    if (payload["index"] is JObject idx)
                    {
                        idx.Remove("recentlyChanged");
                        // Flush health + progress telemetry: doctor/debug data, not
                        // per-turn state. status/totalObjects/lastIndexedAt stay.
                        idx.Remove("flushHealth");
                        if ((string?)idx["progress"] == null) idx.Remove("progress");
                        if ((string?)idx["etaMs"]?.ToString() == "" && idx["etaMs"]?.Type == JTokenType.Null) idx.Remove("etaMs");
                    }
                    // Update block: only actionable when updateAvailable=true. When up
                    // to date, keep a single boolean instead of the full registry dump.
                    if (payload["update"] is JObject upd && upd["updateAvailable"]?.ToObject<bool>() != true)
                    {
                        payload["update"] = new JObject { ["currentVersion"] = upd["currentVersion"], ["updateAvailable"] = false };
                    }
                    // KB alias lists: openKbs/knownKbs/declaredKbs are the same alias
                    // in the common single-KB case; openCount+active already say it all.
                    if (payload["kb"] is JObject kb)
                    {
                        var open = kb["openKbs"] as JArray;
                        var known = kb["knownKbs"] as JArray;
                        var declared = kb["declaredKbs"] as JArray;
                        bool allSame = open != null && known != null && declared != null
                            && open.Count == known.Count && known.Count == declared.Count
                            && open.ToString() == known.ToString() && known.ToString() == declared.ToString();
                        if (allSame) { kb.Remove("openKbs"); kb.Remove("knownKbs"); }
                        // selected/default echo active in the common case; drop when
                        // equal, or when selected is null (default-KB path — active
                        // already tells the agent which KB the session resolves to).
                        string? sel = (string?)kb["selected"];
                        if (sel == null || string.Equals(sel, (string?)kb["active"])) kb.Remove("selected");
                        if (string.Equals((string?)kb["default"], (string?)kb["active"])) kb.Remove("default");
                    }
                }

                // Lean default: point at where the static reference lives instead of
                // re-shipping it every call. Terse mode already removed this above;
                // only re-add when NOT terse.
                if (!Program.TerseResponsesEnabled())
                {
                    payload["reference"] = new JObject
                    {
                        ["hint"] = "Call genexus_whoami(verbose=true) once for inline playbooks + skills catalog, or genexus_recipe / resources/list on demand. genexus_doctor gives a minimal connection+index health check.",
                        ["playbooksVia"] = "genexus_whoami(verbose=true)",
                        ["skillsVia"] = "resources/list"
                    };
                }
            }
            return payload;
        }

        private static JObject BuildMutationRecoveryBlock()
        {
            var registry = _mutationRecovery;
            var block = new JObject
            {
                ["journalHealthy"] = registry.IsHealthy,
                ["pendingCount"] = registry.Count,
                ["automaticRetry"] = false,
                ["hint"] = "Read each fenced object part before authorizing another mutation."
            };
            if (!registry.IsHealthy)
                block["journalError"] = registry.JournalError;
            if (registry.Count > 0)
            {
                block["pending"] = JArray.FromObject(registry.Pending.Select(item => new
                {
                    kb = item.KbAlias,
                    target = item.Target,
                    part = item.Part,
                    operationId = item.OperationId,
                    requiredAtUtc = item.RequiredAtUtc
                }));
            }
            return block;
        }

        // v2.8.0 — skill catalog block. Mirrors SkillCatalog.All so the
        // gateway doesn't duplicate the source-of-truth content; it only
        // surfaces titles + URIs + a one-line "when to read".
        internal static JArray BuildSkillsCatalogBlock()
        {
            var arr = new JArray();
            foreach (var skill in SkillCatalog.All)
            {
                arr.Add(new JObject
                {
                    ["uri"] = "genexus://kb/skills/" + skill.Key,
                    ["title"] = skill.Title,
                    ["summary"] = skill.Description,
                    ["whenToRead"] = SkillWhenToRead(skill.Key),
                    ["readVia"] = new JObject
                    {
                        ["tool"] = "resources/read",
                        ["args"] = new JObject { ["uri"] = "genexus://kb/skills/" + skill.Key }
                    }
                });
            }
            return arr;
        }

        private static string SkillWhenToRead(string key)
        {
            switch (key)
            {
                case "navigation":
                    return "BEFORE setting any panel property whose name involves Call, Protocol, IsMain, Main, Master, or before invoking Call/Return/ReplaceMainPanel — read this first. CallProtocol does NOT apply to Web Panel / SD Panel and 'Modal' is not a valid value.";
                case "gam-integrated-security":
                    return "BEFORE writing any code that depends on GAM authentication / authorization, or before touching Integrated Security Level / Login flow — verify the real property name and accepted values here.";
                case "sd-panel-mobile":
                    return "BEFORE marking a Smart Device object as Main, claiming an 'IsMain' property exists, or setting Native Mobile application-level properties — confirm the real name (it's 'Main program') and which object types support it.";
                case "webpanel-events":
                    return "BEFORE writing Web Panel event code (Start / Refresh / Load) — confirm the firing order and what attribute access each event has. Refresh runs BEFORE Load (per record), not after.";
                default:
                    return "Read before invoking related properties or methods you aren't fully certain about.";
            }
        }

        // v2.8.0 — first-turn navigation aid. Returns a JArray of
        // {tool, args, why} suggestions based on observable state. The array
        // is intentionally short (max 3 entries) and ordered by urgency so
        // an LLM that only reads the first item still picks the right call.
        // Phase 2: once-per-KB-alias gate for the memory orientation suggestion,
        // mirroring UpdateNotifier._triggered's once-per-process gate.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _memorySuggestionShown
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        /// <summary>
        /// Counts live (non-tombstoned, latest-per-id) records in
        /// &lt;kbPath&gt;/.gx/memory/memory.jsonl. Gateway-side filesystem read —
        /// mirrors the fold in Worker's MemoryService.LoadLive without depending
        /// on the Worker assembly. Never throws; returns 0 on any error.
        /// </summary>
        private static int CountLiveMemories(string kbPath)
        {
            try
            {
                string filePath = Path.Combine(kbPath, ".gx", "memory", "memory.jsonl");
                if (!File.Exists(filePath)) return 0;

                var latestById = new System.Collections.Generic.Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject entry;
                    try { entry = JObject.Parse(line); }
                    catch { continue; }

                    string? id = entry["id"]?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;

                    if (!latestById.TryGetValue(id!, out var current) ||
                        string.CompareOrdinal(entry["updatedUtc"]?.ToString(), current["updatedUtc"]?.ToString()) >= 0)
                    {
                        latestById[id!] = entry;
                    }
                }

                return latestById.Values.Count(e => e["tombstone"]?.Value<bool>() != true);
            }
            catch
            {
                return 0;
            }
        }

        internal static JArray BuildSuggestedNextBlock(string? kbPath, bool kbExists, bool kbValid)
        {
            var arr = new JArray();
            try
            {
                // Worker boot / health overrides everything — if it's not up,
                // every other tool call will return crashed/exited.
                if (!IsActiveWorkerHealthy())
                {
                    arr.Add(new JObject
                    {
                        ["tool"] = "genexus_whoami",
                        ["args"] = new JObject(),
                        ["why"] = "Worker is booting or respawning. Re-call whoami in 5–10s; the workerHealth block flips to ready once the SDK finishes loading."
                    });
                    return arr;
                }

                // No KB opened yet — that's the canonical first step.
                if (string.IsNullOrEmpty(kbPath) || !kbExists || !kbValid)
                {
                    arr.Add(new JObject
                    {
                        ["tool"] = "genexus_kb",
                        ["args"] = new JObject { ["action"] = "open", ["path"] = kbPath ?? "<absolute-path-to-.gx-or-folder>" },
                        ["why"] = "No valid KB is selected. Open one before any read/edit tool can resolve objects."
                    });
                    return arr;
                }

                // Index state drives discovery tools. Cold / 0 objects means
                // search / list / impact will all return empty until indexed — but a
                // BUILT index with 0 objects is a genuinely empty KB, so the nudge must
                // not loop force=true reindexing forever (there is nothing to index).
                IndexStateSnapshot snap;
                lock (_lastKnownIndexStateLock) { snap = _lastKnownIndexState; }
                var indexSuggestion = BuildIndexSuggestion(snap.Status, snap.TotalObjects);
                if (indexSuggestion != null) arr.Add(indexSuggestion);

                // Update available — surface as a soft hint, not blocking.
                try
                {
                    var update = UpdateNotifier.GetCachedStatusSync();
                    if (update != null && update["updateAvailable"]?.ToObject<bool>() == true)
                    {
                        string latest = update["latestVersion"]?.ToString() ?? "<latest>";
                        arr.Add(new JObject
                        {
                            ["tool"] = "genexus_orient",
                            ["args"] = new JObject { ["topic"] = "update" },
                            ["why"] = $"GeneXus MCP v{latest} is available. Ask the user before installing — npx genexus-mcp@latest init."
                        });
                    }
                }
                catch { /* update check best-effort */ }

                // Phase 2 — memory orientation. Once per KB alias per gateway process
                // lifetime (mirrors UpdateNotifier._triggered), nudge the agent to
                // recall saved memories when the KB actually has some. Gateway reads
                // the memory.jsonl straight off disk — it's a separate Gateway assembly
                // from the Worker, so it can't reuse MemoryService.
                try
                {
                    string? alias = _currentKb.Value?.NormalizedAlias;
                    if (!string.IsNullOrEmpty(kbPath) && !string.IsNullOrEmpty(alias)
                        && _memorySuggestionShown.TryAdd(alias!, 0))
                    {
                        int liveCount = CountLiveMemories(kbPath!);
                        if (liveCount >= 30)
                        {
                            // Phase 3 — past a certain size, recalling everything is less
                            // useful than compacting first (dreaming).
                            arr.Add(new JObject
                            {
                                ["tool"] = "genexus_memory",
                                ["args"] = new JObject { ["action"] = "consolidate", ["dryRun"] = true },
                                ["why"] = $"This KB has {liveCount} memories — consolidate to merge redundant facts and keep context lean."
                            });
                        }
                        else if (liveCount > 0)
                        {
                            arr.Add(new JObject
                            {
                                ["tool"] = "genexus_memory",
                                ["args"] = new JObject { ["action"] = "recall" },
                                ["why"] = $"This KB has {liveCount} saved memories — recall them for context."
                            });
                        }
                    }
                }
                catch { /* memory orientation best-effort */ }

                // Default exploration nudge when everything is healthy — gives
                // a dumb LLM a deterministic 'what now' instead of guessing.
                if (arr.Count == 0)
                {
                    arr.Add(new JObject
                    {
                        ["tool"] = "genexus_list_objects",
                        ["args"] = new JObject { ["limit"] = 25 },
                        ["why"] = "KB is open and indexed. Listing objects is the cheapest way to anchor the next decision."
                    });
                }

                // v2.8.0 — always nudge the LLM toward authoritative skills
                // before it invents a property or method that doesn't exist
                // (CallProtocol=Modal, IsMain property, etc.). Cheap reminder;
                // the resource bodies are fact-checked against docs.genexus.com.
                arr.Add(new JObject
                {
                    ["tool"] = "resources/read",
                    ["args"] = new JObject { ["uri"] = "genexus://kb/skills/navigation" },
                    ["why"] = "Before claiming a navigation property/method exists (CallProtocol values, IsMain, ReplaceMainPanel, CallOptions.Target), read this skill — it lists what the SDK actually exposes and the common LLM pitfalls."
                });
            }
            catch { /* never let suggestion logic break whoami */ }
            return arr;
        }

        // Index-state → suggested next action for whoami. Returns null when the index is
        // healthy (built and non-empty) and no index-related nudge applies. This is the
        // single decision point that keeps agents from looping `lifecycle action=index
        // force=true` on a genuinely-empty KB: a BUILT index with 0 objects (Ready /
        // LiteReady / Enriching — e.g. a KB whose LocalDB model is missing) means the walk
        // already completed and found nothing, so re-running the reindex cannot help.
        // Only a Cold/Unknown (never-built) index gets the force=true nudge.
        internal static JObject BuildIndexSuggestion(string status, int totalObjects)
        {
            string s = status ?? string.Empty;
            bool indexCold = string.Equals(s, "Cold", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(s, "Unknown", StringComparison.OrdinalIgnoreCase);
            if (indexCold)
            {
                return new JObject
                {
                    ["tool"] = "genexus_lifecycle",
                    ["args"] = new JObject { ["action"] = "index", ["force"] = true },
                    ["why"] = "Index is cold. Run a full index so list_objects / query / impact return real data."
                };
            }
            if (totalObjects == 0)
            {
                // A walk/rebuild is still in progress (nothing has landed yet) — nudge the
                // agent to observe progress, never to create objects or re-trigger the index.
                bool walkInProgress = string.Equals(s, "UltraLiteReady", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(s, "Reindexing", StringComparison.OrdinalIgnoreCase);
                if (walkInProgress)
                {
                    return new JObject
                    {
                        ["tool"] = "genexus_whoami",
                        ["args"] = new JObject(),
                        ["why"] = "Index build is still in progress — poll whoami until indexStatus reaches Ready before listing objects."
                    };
                }
                // Ready / LiteReady / Enriching with 0 objects: the walk completed and found
                // no model objects. force=true reindexing cannot change that.
                return new JObject
                {
                    ["tool"] = "genexus_create",
                    ["args"] = new JObject { ["action"] = "object", ["type"] = "Transaction" },
                    ["why"] = "This KB's model is empty (0 objects indexed after a completed build — e.g. a missing LocalDB model). Create an object or open a different KB; force=true reindexing cannot populate it."
                };
            }
            return null;
        }

        // Friction 2026-05-22: which worker exe is actually running was opaque —
        // users had to inspect tasklist + git status to know if their rebuilt
        // worker was in use, or if the gateway was still serving from publish/.
        // Surface it here so every whoami answers "which binary am I running"
        // without a side investigation.
        private static JObject BuildWorkerBlock()
        {
            try
            {
                WorkerProcess? wp = null;
                KbHandle? kb = _currentKb.Value;
                if (kb != null && _workerPool != null)
                {
                    wp = _workerPool.TryGet(kb.NormalizedAlias);
                }
                if (wp == null && _workerPool != null)
                {
                    // No KB selected (multi-KB session or first turn) — pick the first open.
                    var open = _workerPool.ListOpen();
                    if (open != null)
                    {
                        foreach (var h in open)
                        {
                            var w = _workerPool.TryGet(h.NormalizedAlias);
                            if (w != null) { wp = w; break; }
                        }
                    }
                }
                if (wp == null)
                {
                    return new JObject
                    {
                        ["status"] = "not_spawned",
                        ["hint"] = "Worker hasn't been spawned this session yet. Triggered on the first KB tool call."
                    };
                }
                string? exe = wp.SpawnedExePath;
                DateTime? builtAt = wp.SpawnedExeBuiltAtUtc;
                string? sourceLabel = null;
                if (!string.IsNullOrEmpty(exe))
                {
                    string e = exe!.Replace('/', '\\');
                    if (e.IndexOf(@"\publish\worker\", StringComparison.OrdinalIgnoreCase) >= 0)
                        sourceLabel = "publish (config.json WorkerExecutable)";
                    else if (e.IndexOf(@"\src\GxMcp.Worker\bin\Debug\", StringComparison.OrdinalIgnoreCase) >= 0)
                        sourceLabel = "dev-Debug (bin/Debug fallback)";
                    else if (e.IndexOf(@"\src\GxMcp.Worker\bin\Release\", StringComparison.OrdinalIgnoreCase) >= 0)
                        sourceLabel = "dev-Release (bin/Release fallback)";
                    else
                        sourceLabel = "custom";
                }
                // Item 52: worker memory + uptime for proactive reload hints.
                long? memoryMb = null;
                int? uptimeMin = null;
                string? reloadHint = null;
                try
                {
                    long? wsBytes = wp.WorkingSetBytes;
                    if (wsBytes.HasValue) memoryMb = wsBytes.Value / (1024 * 1024);
                    if (wp.SpawnedAtUtc.HasValue)
                        uptimeMin = (int)(DateTime.UtcNow - wp.SpawnedAtUtc.Value).TotalMinutes;
                    if (memoryMb > 1500)
                        reloadHint = "Consider genexus_worker_reload — heap >1.5GB or uptime >2h";
                    else if (uptimeMin > 120)
                        reloadHint = "Consider genexus_worker_reload — heap >1.5GB or uptime >2h";
                }
                catch { /* non-fatal: process may have exited between checks */ }

                var workerBlock = new JObject
                {
                    ["status"] = wp.Pid.HasValue ? "running" : "stopped",
                    ["pid"] = wp.Pid,
                    ["memoryMb"] = memoryMb,
                    ["uptimeMin"] = uptimeMin
                };
                // Terse mode: exePath/exeSource/builtAtUtc/spawnMs/sdkInitMs are
                // install forensics for doctor-style debugging (~300 bytes), not
                // per-turn data. reloadHint (actionable) is kept.
                if (!TerseResponsesEnabled())
                {
                    workerBlock["exePath"] = exe;
                    workerBlock["exeSource"] = sourceLabel;
                    workerBlock["builtAtUtc"] = builtAt?.ToString("o");
                    workerBlock["spawnMs"] = wp.SpawnMs;
                    workerBlock["sdkInitMs"] = wp.SdkInitMs;
                }
                if (reloadHint != null) workerBlock["reloadHint"] = reloadHint;

                // Durable death history (survives worker log rotation). Lets the agent —
                // and support — see how often the worker actually dies and why, instead of
                // guessing. Only attached when at least one death has been recorded.
                // PERFORMANCE (perf round 5): CrashLedger.Summarize reads the ledger file
                // on every call — memoize per whoami with a 10s TTL (deaths are rare; a
                // stale-by-seconds count is far better than a disk read per whoami).
                try
                {
                    JObject deaths;
                    lock (_crashSummaryLock)
                    {
                        if (_crashSummaryAtUtc == DateTime.MinValue
                            || (DateTime.UtcNow - _crashSummaryAtUtc) > CrashSummaryCacheTtl)
                        {
                            _crashSummary = CrashLedger.Summarize(recentN: 3);
                            _crashSummaryAtUtc = DateTime.UtcNow;
                        }
                        deaths = _crashSummary;
                    }
                    if ((deaths["total"]?.ToObject<int?>() ?? 0) > 0)
                        workerBlock["deaths"] = deaths;
                }
                catch { /* forensics are best-effort — never break whoami */ }

                // Per-tool latency (top by total time). Lets the agent see where session time
                // actually goes instead of guessing which tool is slow. Only when there's data.
                try
                {
                    var latency = ToolLatencyStats.Summarize(topN: 5);
                    if ((latency["totalCalls"]?.ToObject<long?>() ?? 0) > 0)
                        workerBlock["toolLatency"] = latency;
                }
                catch { /* instrumentation is best-effort — never break whoami */ }

                return workerBlock;
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["error"] = ex.Message
                };
            }
        }

        private static JObject BuildPlaybooksBlock()
        {
            return new JObject
            {
                ["wwp_on_transaction"] = "genexus_apply_pattern { name: <Trn>, pattern: 'WorkWithPlus' } → generates WW<Trn>+View+Export family; no template needed.",
                ["wwp_on_webpanel"] = "genexus_apply_pattern { name: <WebPanel>, pattern: 'WorkWithPlus', settings: { template: '<TemplateName>' } } → direct-attach via host WorkWithPlus<WebPanel>. CHECK PARENT TYPE FIRST with genexus_inspect: WebPanel + SDPanel are direct-attach; Transaction is family-gen; other types are REJECTED.",
                ["edit_wwp_layout"] = "genexus_edit { name: WorkWithPlus<X>, part: 'PatternInstance', mode: 'patch', ... } → edit the host's XML; the MCP auto-projects to the WebForm.",
                ["create_popup"] = "genexus_create_popup { name, spec: { title, inputs:[{type,varName,...}], buttons:[{caption,event}] } } → one call replaces ~6 edits (Form layout=true + inputs + buttons + parms).",
                ["smart_read_1_call"] = "genexus_read { name: <Object> } (omit 'part') → returns the COMPLETE object in 1 roundtrip: rules with parm, source/events, variables, structure, and called signatures. ALWAYS prefer this over reading separate parts.",
                ["task_360_context"] = "genexus_analyze { name: <Object>, mode: 'context' } → returns 360° task context in 1 roundtrip: full object + called procedures' parm signatures + referenced tables' schemas/PKs + SDTs + callers. Ideal before editing or refactoring.",
                ["read_object_structure"] = "genexus_inspect { name, include:['parts','variables','signature'] } → cheap snapshot before any edit. ALWAYS run this first when unsure of object type.",
                ["unbreak_build"] = "Build failed with CS0246/CS2001? Check response.suggested_retry — it already carries `target` as a CSV of the missing objects. Fire `genexus_lifecycle { action:'build', target:<that CSV>, includeCallees:'direct' }` BEFORE asking the user. Don't grep raw error[] paths by hand and don't hand the list back to the user.",
                // Friction 2026-05-22: each of these cost multiple iterations the first time.
                ["unbreak_html_form"] = "HTML-form (Form type=html) gotchas — verify BEFORE writing code:\n  1. gxButton ignores custom event=... (always fires Enter). Workaround: route via a single Enter event + dispatch by sender.\n  2. gxTextBlock CaptionExpression Type=Variable renders the literal &<varName>; in HTML form. Use Caption=&varName (no Expression).\n  3. gxAttribute referencing a freshly-added variable fails 'Visual write failed' until the variable is committed. Add the variable + flush, then add the gxAttribute in a second edit.\n  4. Buttons are NOT addressable from Events as Btn<Name>.Enabled / .Caption (src0265). Drive visibility/enable via control properties at design-time or via &flag variables bound through CaptionExpression.",
                ["bulk_edit"] = "Need N patches on the same object? Use genexus_bulk_edit instead of N parallel genexus_edit. Parallel edits race on the file hash: only the first applies, the rest return 'Context block not found'.",
                ["wait_long_builds"] = "Don't poll genexus_lifecycle status in a loop — pass wait_until_done:true on the build call OR wait_seconds:600 on status. One turn instead of 12.",
                ["xml_comments_in_form"] = "XML comments (<!-- ... -->) inside HTML form Source are emitted as visible text by the generator. Strip them before genexus_edit (or use mode=patch to avoid touching them).",
                ["partial_success"] = "If a build returns Status=Failed but response.partial_success=true, Generation+Compilation already succeeded — try running the object once before rebuilding. The DLL is updated; only a late MSBuild step (often WebAppConfig) failed.",
                ["recipes_index"] = "For full step-by-step recipes call genexus_recipe { name: 'wwp_on_webpanel' | 'wwp_on_transaction' | 'create_popup' | 'edit_pattern_instance' | 'add_custom_button' | 'list' }.",
                // Friction 2026-05-22 items 2, 3, 13.
                ["html_form_inline_js"] = "GeneXus HTML form sanitization (KB-agnostic but observed in v18):\n  - <script>, <iframe>, <img onerror> inside gxTextBlock Format=\"HTML\" render as\n    literal escaped text — they do NOT execute.\n  - Inline event-attrs in raw HTML are preserved: onclick on <input type=radio>,\n    onmousedown/onunload on <body>, likely other on* on <td>/<div>.\n  - For post-event JS in a popup, hook via <body onmousedown> (installs listeners\n    on first mousedown) + addEventListener at runtime, instead of emitting <script>.",
                ["popup_call_async"] = ".Popup() is async. The line immediately after .Popup() sees out-params STILL EMPTY —\nchecking &OutVar.IsEmpty() there always returns true. Values arrive on a subsequent\nRefresh fired by AUTO_REFRESH=VARS_CHANGE, IF that property detects a change. In\nseveral KBs AUTO_REFRESH does NOT fire after popup close and the visibility set in\nStart does not restore.\n\nRecipe for \"blocking popup + locked screen + auto-reload\": see\ngenexus://kb/tool-help/recipes/popup_blocking_with_reload.",
                ["verify_in_browser"] = "chrome-devtools-axi (global npm CLI) drives Chrome via CDP.\nUsage:\n  chrome-devtools-axi open <url>      # navigate + return a11y tree\n  chrome-devtools-axi click @<uid>    # click by accessible uid\n  chrome-devtools-axi eval <js>       # eval JS in page (IIFE for multi-line)\n  chrome-devtools-axi screenshot <path>\n\nFor GeneXus popups (iframe): document.getElementById('gxp0_ifrm').contentDocument\nto access internal DOM.",
                // Friction 2026-05-22 items 26 + 67 — control-type glossary.
                ["control_type_glossary"] = "GeneXus 18 control types — which to use when:\n  • <gxButton> — standalone button. caption/event/buttonClass; ignores OnClickEvent\n    in HTML form (always fires Enter). Use 'Event' attribute, not 'OnClickEvent'.\n  • <gxAttribute ControlType=\"Button\"> — attribute-bound button. Rare; prefer\n    gxButton unless you need attribute binding.\n  • <gxAttribute> default (no ControlType) — IS editable. Sanitizer-safe target\n    for hidden-value bridges (set via document.getElementById('vNAME').value).\n  • <gxAttribute ControlType=\"Radio\"|\"Combo\"> on Form type='free style' →\n    rendered READ-ONLY. Switch Form type='layout' OR use raw HTML radios\n    inside gxTextBlock Format='HTML' + a hidden gxAttribute.\n  • <gxTextBlock> Format='Text' — plain text, sanitized.\n  • <gxTextBlock> Format='HTML' — raw HTML. <script>/<iframe>/<img onerror>\n    inside CDATA are escaped (see html_form_inline_js); inline event-attrs are kept.\n  • <gxImage> — clickable when 'eventGX' is set; mirrors the gxAttribute pattern.",
                ["dep_tree_view"] = "Need an ASCII tree of who-calls-what? Use genexus_analyze mode='hierarchy'\nfor a tree under target, or mode='callers' for per-call-site detail with line+context.\nmode='impact' is the flat caller list — fastest when you only need a count."
            };
        }

        // Wave-3 item 30: surface the live p95 ring-buffer to SystemRouter so the
        // build-plan worker call can derive per-node estimatedSeconds. Returns an
        // empty object when the tracker is null (first-call before init).
        internal static JObject GetToolP95MapForBuildPlan()
        {
            return _operationTracker?.BuildToolP95Map() ?? new JObject();
        }

        // Item 94: assemble the existing stats block + heatmap array. Purely additive
        // (no field renamed in stats.tools); heatmap is the new key. Heatmap is per-tool
        // [{tool,totalMs,percentOfSession,lastUsedAt}] sorted descending by totalMs.
        private static JObject BuildStatsWithHeatmap()
        {
            JObject stats = _operationTracker?.BuildToolStatsBlock() ?? new JObject();
            if (_operationTracker != null)
            {
                stats["heatmap"] = _operationTracker.BuildHeatmapBlock();
            }
            return stats;
        }
    }
}
