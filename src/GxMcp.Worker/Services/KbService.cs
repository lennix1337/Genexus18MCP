using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Entities;
using Artech.Genexus.Common.ModelParts;

namespace GxMcp.Worker.Services
{
    public class KbService
    {
        private BuildService _buildService;
        private readonly IndexCacheService _indexCacheService;
        private readonly VectorService _vectorService = new VectorService();

        // Progress Tracking
        private static volatile int _processedCount = 0;
        private static volatile int _totalCount = 0;
        private static volatile bool _isIndexing = false;
        private static volatile string _currentStatus = "";

        // Fase 0 instrumentation: last KB-open / datastore-probe elapsed, so Program.cs
        // can attribute them in the consolidated [COLD-START-BREAKDOWN] line without
        // re-parsing the per-phase log lines.
        public static long LastOpenElapsedMs = 0;
        public static long LastDatastoreProbeMs = 0;

        private dynamic _kb;
        private volatile bool _isOpenInProgress = false;
        private readonly object _kbLock = new object();

        // issue #38 defect #2: bound the background auto-open retries. A structurally
        // unopenable GX_KB_PATH used to be re-attempted on every GetKB() forever (the
        // logs showed the same open failing every ~5s), churning the worker and flooding
        // the debug log. After MaxAutoOpenFailures consecutive failures we abandon the
        // auto-open path; an explicit genexus_kb action=open still works (it does not go
        // through this guard) and a success resets the counter.
        private const int MaxAutoOpenFailures = 3;
        private volatile bool _autoOpenAbandoned = false;
        private int _autoOpenFailCount = 0;

        public KbService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public IndexCacheService GetIndexCache() { return _indexCacheService; }
        public bool IsInitializing => _isOpenInProgress;
        public bool IsIndexing => _isIndexing;
        public int IndexProcessed => _processedCount;
        public int IndexTotal => _totalCount;
        public string IndexStatus => _currentStatus;
        public bool IsOpen { get { lock (_kbLock) { return _kb != null; } } }

        // v2.6.6 Stream D — expose the open KB handle + lock so BuildService can
        // run GeneXus MSBuild tasks in-process against the same KB instance
        // instead of spawning MSBuild.exe + reopening the KB out-of-process.
        // Callers MUST hold KbLock for the duration of any task.Execute() call.
        public object KbObject { get { lock (_kbLock) { return (object)_kb; } } }
        public object KbLock => _kbLock;

        public string GetKbPath()
        {
            lock (_kbLock)
            {
                if (_kb == null) return null;
                try { return (string)_kb.Location; } catch { return null; }
            }
        }

        public dynamic GetKB()
        {
            lock (_kbLock) 
            { 
                if (_kb == null && !_isOpenInProgress && !_autoOpenAbandoned)
                {
                    string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
                    if (!string.IsNullOrEmpty(kbPath))
                    {
                        Logger.Info($"Auto-opening KB in background: {kbPath}");
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            string r = OpenKB(kbPath);
                            try
                            {
                                bool ok = string.Equals(JObject.Parse(r)["status"]?.ToString(), "ok", StringComparison.Ordinal);
                                if (ok)
                                {
                                    _autoOpenFailCount = 0;
                                }
                                else
                                {
                                    int n = Interlocked.Increment(ref _autoOpenFailCount);
                                    if (n >= MaxAutoOpenFailures)
                                    {
                                        _autoOpenAbandoned = true;
                                        Logger.Error($"[AUTO-OPEN-GIVEUP] Background auto-open of '{kbPath}' failed {n}× — giving up to stop the retry loop. "
                                            + "Fix the path (it must be a KB folder with a .gxw / knowledgebase.connection) and reopen with genexus_kb action=open path=<kbPath>.");
                                    }
                                }
                            }
                            catch { }
                        });
                    }
                }
                return _kb; 
            }
        }

        // issue #38 defect #1: a path is openable as a KB only when it is the KB folder
        // (contains a .gxw or the legacy knowledgebase.connection) or a direct .gxw/.gx
        // file. Cheap filesystem pre-check; the SDK is still the final authority on open.
        private static bool IsPlausibleKbPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                if (File.Exists(path))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    return ext == ".gxw" || ext == ".gx";
                }
                if (!Directory.Exists(path)) return false;
                foreach (var f in Directory.EnumerateFiles(path))
                {
                    string name = Path.GetFileName(f).ToLowerInvariant();
                    if (name.EndsWith(".gxw") || name == "knowledgebase.connection") return true;
                }
                return false;
            }
            catch { return false; }
        }

        public string OpenKB(string path)
        {
            var destinationError = WriteDestinationGuard.CheckOpen(path);
            if (destinationError != null) return destinationError;
            // issue #38 defect #1: reject a structurally-invalid path fast, before the
            // heavy KnowledgeBase.Open call. A GeneXus environment/model subfolder has no
            // .gxw / knowledgebase.connection; opening it always throws deep in the SDK.
            // Failing here keeps the error clear and lets the auto-open give-up counter
            // converge quickly instead of paying a full SDK open per retry.
            if (!IsPlausibleKbPath(path))
            {
                Logger.Error($"[KB-OPEN-INVALID] path={path} is not a KB root (no .gxw / knowledgebase.connection).");
                return Models.McpResponse.Err(
                    code: "KbInvalidPath",
                    message: $"'{path}' is not a GeneXus Knowledge Base root: no .gxw file and no knowledgebase.connection found.",
                    hint: "Point at the KB folder that contains the .gxw (not an environment/model subfolder), or the .gxw/.gx file itself.",
                    target: path);
            }

            // Concurrency redesign: the multi-minute KnowledgeBase.Open used to run while
            // HOLDING _kbLock, which (a) blocked every locked accessor (GetKbPath, IsOpen,
            // GetKB, GetActiveEnvironment) for the whole open, and (b) made the
            // _isOpenInProgress fast-fail unreachable for a second caller (it was queued on
            // the very lock the first caller held). Now the flag is set under the lock,
            // the lock is released, Open runs lock-free, and the handle is published by
            // re-acquiring the lock. A second concurrent caller gets the OpenInProgress
            // envelope immediately.
            lock (_kbLock)
            {
                if (_isOpenInProgress)
                {
                    return Models.McpResponse.Err(
                        code: "OpenInProgress",
                        message: "Another KB open is already in progress on this worker.",
                        hint: "Wait for the current open to finish, then retry.",
                        retryAfterMs: 1500,
                        target: path);
                }

                if (_kb != null)
                {
                    try
                    {
                        if (string.Equals(_kb.Location, path, StringComparison.OrdinalIgnoreCase))
                        {
                            return Models.McpResponse.Ok(target: path, code: "KbAlreadyOpen");
                        }
                    }
                    catch { }
                    try { _kb.Close(); } catch { }
                    _kb = null;
                    try { _indexCacheService.Clear(); }
                    catch (Exception clearEx) { Logger.Warn("Index cache clear during KB switch failed: " + clearEx.Message); }
                }

                _isOpenInProgress = true;
            }

            // PERFORMANCE (instrumentation): time the KB.Open call so cold-start regressions
            // are visible in logs. Previously this critical SDK call had no timing data.
            var sw = Stopwatch.StartNew();
            try
            {
                Logger.Info($"Opening KB: {path}");
                dynamic opened = null;
                // SdkGate: KnowledgeBase.Open is the heaviest SDK call in the process;
                // serialize it against the index/watcher threads like every other SDK region.
                using (SdkGate.Enter())
                {
                    string oldDir = Directory.GetCurrentDirectory();
                    try
                    {
                        // NOTE (process-global CWD mutation): the GeneXus SDK resolves some
                        // KB-relative artifacts (e.g. environment metadata, model.ini probes)
                        // against the current directory during Open on certain installs.
                        // We could not prove it safe to remove without a live-KB regression
                        // matrix, so the mutation is kept — but tightly scoped to the Open
                        // call itself and restored in finally. Anything else that needs the
                        // KB directory should derive it from kb.Location, never from CWD.
                        string kbDir = Path.GetDirectoryName(path);
                        Directory.SetCurrentDirectory(kbDir);

                        var options = new KnowledgeBase.OpenOptions(path);
                        opened = KnowledgeBase.Open(options);
                    }
                    finally
                    {
                        try { Directory.SetCurrentDirectory(oldDir); } catch { }
                    }
                }
                NormalizeGxwVersionMetadata(path);

                // Publish the handle.
                lock (_kbLock) { _kb = opened; }

                // Warm reload is intentionally attempted after the SDK handle is
                // published, but before the caller receives KbOpened. This makes the
                // restored catalogue immediately available to list/search/impact while
                // preserving the regular on-disk cache as a fallback when the explicit
                // warm snapshot is absent or rejected.
                JObject warmReload = null;
                try
                {
                    _indexCacheService.Initialize(path, proactiveLoad: false);
                    warmReload = _indexCacheService.TryRestoreWarmSnapshot(path);
                }
                catch (Exception warmEx)
                {
                    warmReload = new JObject
                    {
                        ["attempted"] = true,
                        ["loaded"] = false,
                        ["fallback"] = true,
                        ["fallbackReason"] = "restore-failed",
                        ["error"] = warmEx.Message
                    };
                    Logger.Warn("Warm index restore failed: " + warmEx.Message);
                }

                sw.Stop();
                LastOpenElapsedMs = sw.ElapsedMilliseconds;
                Logger.Info($"[KB-OPEN] elapsedMs={sw.ElapsedMilliseconds} path={path}");
                // Diagnostic (read-only, no DB connect): record which data store the
                // active environment points at. The GeneXus SDK may try to reach this
                // server during open; when it's unreachable, KB-OPEN above balloons or
                // Diagnostic (read-only, no DB connect): record which data store the
                // active environment points at in the background so it never gates readiness.
                System.Threading.Tasks.Task.Run(() =>
                {
                    var dsSw = Stopwatch.StartNew();
                    try { using (SdkGate.Enter()) Logger.Info($"[KB-OPEN-DATASTORE] {DescribeActiveDataStore(opened)}"); }
                    catch (Exception dsEx) { Logger.Debug($"[KB-OPEN-DATASTORE] probe failed: {dsEx.Message}"); }
                    dsSw.Stop();
                    LastDatastoreProbeMs = dsSw.ElapsedMilliseconds;
                });
                return Models.McpResponse.Ok(
                    target: path,
                    code: "KbOpened",
                    result: new JObject
                    {
                        ["elapsedMs"] = sw.ElapsedMilliseconds,
                        ["warmReload"] = warmReload
                    });
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.Error($"[KB-OPEN-FAIL] elapsedMs={sw.ElapsedMilliseconds} path={path} error={ex.Message}");
                Logger.Error($"ERROR opening KB: {ex.Message}");
                lock (_kbLock) { _kb = null; }
                return Models.McpResponse.Err(
                    code: "KbOpenFailed",
                    message: ex.Message,
                    hint: "Verify the path points to a .gx file (or its containing folder), the file is accessible, and the GeneXus install matches the KB version.",
                    target: path);
            }
            finally
            {
                _isOpenInProgress = false;
            }
        }

        private static void NormalizeGxwVersionMetadata(string kbPath)
        {
            try
            {
                string kbDir = ResolveKbDirectory(kbPath);
                if (string.IsNullOrWhiteSpace(kbDir) || !Directory.Exists(kbDir)) return;

                string gxwPath = Directory.GetFiles(kbDir, "*.gxw", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(gxwPath) || !File.Exists(gxwPath)) return;

                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? string.Empty;
                var stamp = DetectGeneXusVersionStamp(gxPath);
                if (stamp == null) return;

                XDocument doc = XDocument.Load(gxwPath, LoadOptions.PreserveWhitespace);
                XElement root = doc.Root;
                if (root == null || !string.Equals(root.Name.LocalName, "GeneXusInformation", StringComparison.OrdinalIgnoreCase)) return;

                // Each element must be written in the exact format the IDE itself
                // emits, otherwise the IDE re-detects a mismatch and re-shows the
                // "different GeneXus installation than last time" dialog after the
                // MCP touches the KB. The IDE writes, e.g.:
                //   <ProductVersion>18</ProductVersion>
                //   <FriendlyVersion>18.0.179127 U7</FriendlyVersion>
                //   <VersionNumber>18.0.7.179127</VersionNumber>
                bool changed = false;
                changed |= UpsertElementValue(root, "InstallationPath", gxPath);
                changed |= UpsertElementValue(root, "ProductVersion", stamp.ProductVersionShort);
                changed |= UpsertElementValue(root, "FriendlyVersion", stamp.FriendlyVersion);
                changed |= UpsertElementValue(root, "VersionNumber", stamp.VersionNumber);

                if (!changed) return;

                doc.Save(gxwPath, SaveOptions.DisableFormatting);
                Logger.Info($"[KB-METADATA] Updated '{Path.GetFileName(gxwPath)}' to FriendlyVersion '{stamp.FriendlyVersion}'.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[KB-METADATA] Failed to normalize .gxw version metadata: {ex.Message}");
            }
        }

        // Read-only one-line description of the active environment's default data store
        // (name / type / server / schema) for the [KB-OPEN-DATASTORE] diagnostic. Reads
        // SDK metadata only — does NOT open a DB connection. All access is defensive so a
        // missing property or an unexpected SDK shape degrades to a partial string, never
        // throws into the open path.
        private static string DescribeActiveDataStore(dynamic kb)
        {
            if (kb == null) return "kb=null";
            Func<Func<string>, string> s = f => { try { return f() ?? ""; } catch { return ""; } };

            // Primary: the DataStoresPart accessor (shared with DatabaseInfoService). The
            // legacy Environment.DataStores / TargetModel.DataStore paths return null on many
            // KBs, which is why this used to log <unresolved> even though the store exists.
            dynamic def = null;
            try
            {
                var stores = DatabaseInfoService.EnumerateViaDataStoresPart(kb);
                foreach (dynamic ds in stores)
                {
                    if (ds == null) continue;
                    if (def == null) def = ds;
                    bool isDefault = false;
                    try { isDefault = (bool)ds.IsDefault; } catch { }
                    if (isDefault) { def = ds; break; }
                }
            }
            catch { }
            if (def == null) { try { def = kb.DesignModel?.Environment?.TargetModel?.DataStore; } catch { } }
            if (def == null) return "datastore=<unresolved>";

            string name = s(() => (string)def.Name);
            if (name.Length == 0) name = s(() => (string)def.Category.Name);
            if (name.Length == 0) name = s(() => (string)def.Type);
            int dbms = -1; try { dbms = (int)def.Dbms; } catch { }
            string family = ""; try { family = ExecutionPlanFetcher.ResolveDbmsFamily(dbms); } catch { }
            string server = s(() => (string)def.ServerName);
            if (server.Length == 0) server = s(() => (string)def.Server);
            string schema = s(() => (string)def.DatabaseSchema);
            if (schema.Length == 0) schema = s(() => (string)def.Schema);

            return "name=" + (name.Length == 0 ? "?" : name)
                 + " type=" + (family.Length == 0 ? ("dbms" + dbms) : family)
                 + " server=" + (server.Length == 0 ? "<none>" : server)
                 + " schema=" + (schema.Length == 0 ? "<none>" : schema);
        }

        private static string ResolveKbDirectory(string kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath)) return null;
            if (Directory.Exists(kbPath)) return kbPath;
            if (File.Exists(kbPath)) return Path.GetDirectoryName(kbPath);
            string ext = Path.GetExtension(kbPath);
            if (!string.IsNullOrEmpty(ext) && ext.Equals(".gxw", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(kbPath);
            }
            return kbPath;
        }

        private static bool UpsertElementValue(XElement parent, string elementName, string value)
        {
            if (parent == null || string.IsNullOrWhiteSpace(elementName)) return false;
            XElement child = parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase));
            if (child == null)
            {
                parent.Add(new XElement(elementName, value ?? string.Empty));
                return true;
            }

            string current = (child.Value ?? string.Empty).Trim();
            string next = (value ?? string.Empty).Trim();
            if (string.Equals(current, next, StringComparison.Ordinal)) return false;

            child.Value = next;
            return true;
        }

        // The three version strings the IDE writes into a .gxw's <GeneXusInformation>.
        internal sealed class GxVersionStamp
        {
            public string ProductVersionShort;  // e.g. "18"
            public string FriendlyVersion;       // e.g. "18.0.179127 U7"
            public string VersionNumber;         // e.g. "18.0.7.179127"
        }

        // Visible for testing — given a parsed FileVersionInfo (or its equivalent
        // parts), build the IDE-canonical stamp. Kept pure so the unit suite can
        // exercise the ProductVersion-vs-FileVersion fix without a GeneXus install.
        internal static GxVersionStamp BuildStampFromParts(
            int productMajor, int productMinor, int productBuild, int productPrivate)
        {
            // Friendly format the IDE emits is "Major.Minor.Private U{Build}", e.g.
            // ProductVersion 18.0.7.179127 → "18.0.179127 U7". The build number is
            // the *update* level (U7) and the private part is the actual build id.
            return new GxVersionStamp
            {
                ProductVersionShort = productMajor.ToString(),
                FriendlyVersion = $"{productMajor}.{productMinor}.{productPrivate} U{productBuild}",
                VersionNumber = $"{productMajor}.{productMinor}.{productBuild}.{productPrivate}"
            };
        }

        // Parse the dotted ProductVersion *string* into its four numeric parts.
        // Returns null if fewer than four numeric components are present.
        // Strips a "+<git-sha>" / trailing-space InformationalVersion suffix that
        // .NET appends (e.g. "18.0.7.179127+abc123" → [18,0,7,179127]).
        internal static int[] ParseProductVersionString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string head = raw.Trim();
            int cut = head.IndexOfAny(new[] { '+', ' ' });
            if (cut >= 0) head = head.Substring(0, cut);
            string[] parts = head.Split('.');
            if (parts.Length < 4) return null;
            var nums = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i], out nums[i])) return null;
            }
            return nums;
        }

        private static GxVersionStamp DetectGeneXusVersionStamp(string gxPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gxPath) || !Directory.Exists(gxPath)) return null;

                string exePath = Path.Combine(gxPath, "GeneXus.exe");
                if (!File.Exists(exePath)) return null;
                var info = FileVersionInfo.GetVersionInfo(exePath);

                // BUG FIX (friction 2026-05-26): the IDE identifies itself by the
                // assembly *ProductVersion string* (e.g. "18.0.7.179127"), but this
                // method previously read the numeric Product*Part fields. On GeneXus
                // the binary FIXEDFILEINFO encodes the FileVersion build (48055) in
                // BOTH the file and product numeric parts, so the numeric path yields
                // "18.0.48055 U7" — the exact wrong stamp that made the IDE pop the
                // "different GeneXus installation" dialog after every MCP open. Only
                // the ProductVersion *string* carries the real product build (179127),
                // so parse that.
                var parsed = ParseProductVersionString(info.ProductVersion);
                if (parsed != null)
                    return BuildStampFromParts(parsed[0], parsed[1], parsed[2], parsed[3]);

                // Fallback: some installs ship a version.txt with the friendly string.
                string[] candidates =
                {
                    Path.Combine(gxPath, "version.txt"),
                    Path.Combine(gxPath, "Version.txt"),
                    Path.Combine(gxPath, "GeneXus.version")
                };
                foreach (string candidate in candidates)
                {
                    if (!File.Exists(candidate)) continue;
                    string raw = File.ReadAllText(candidate)?.Trim();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        return new GxVersionStamp
                        {
                            ProductVersionShort = raw.Split('.')[0],
                            FriendlyVersion = raw,
                            VersionNumber = raw
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        public string BulkIndex() => BulkIndex(force: false);

        // SP6.T6 — public entry point. When Indexing.UseLitePass is true (default) we run
        // the new fast lite + lazy-enrichment pipeline; otherwise we fall back to the
        // preserved monolithic path (BulkIndexLegacy) for one release.
        public string BulkIndex(bool force)
        {
            if (!Configuration.UseLitePass)
            {
                return BulkIndexLegacy(force);
            }

            Logger.Info($"BulkIndex(force={force}) requested — fast index path (lite + lazy enrichment).");
            if (_isIndexing) return Models.McpResponse.Ok(
                code: "AlreadyInProgress",
                result: new JObject { ["hint"] = "An index build is already running; poll genexus_whoami for progress." });

            // Wait briefly for the KB to open — same warm-up window as the legacy path.
            try
            {
                int waitMs = 0;
                while (waitMs < 15000 && !_indexCacheService.IsInitialized)
                {
                    // Fase 1: resolve the on-disk cache path NOW (post-OpenKB GetKBPath is valid)
                    // so force=true and the warm-start read path agree on a single location —
                    // both must hit %LOCALAPPDATA%\…\index_{hash}.*, never the constructor's
                    // <BaseDir>\cache\search_index.* fallback. Without this the delta sidecar
                    // written under one path is invisible to the other (metaPresent=False).
                    // proactiveLoad:!force — on force we're about to Clear()/DeleteOnDiskSnapshot(),
                    // so don't kick a background GetIndex() that would race and republish stale data.
                    _indexCacheService.EnsureInitialized(proactiveLoad: !force);
                    if (_indexCacheService.IsInitialized) break;
                    Thread.Sleep(200);
                    waitMs += 200;
                }
                if (force)
                {
                    Logger.Info("BulkIndex(fast): force=true — clearing in-memory + on-disk snapshot.");
                    try
                    {
                        _indexCacheService.Clear();
                        // Preserve the last certified snapshot as a crash fallback; new shard writes are atomic.
                        _indexCacheService.MarkReindexStarted(0);
                    }
                    catch (Exception ex) { Logger.Warn("BulkIndex(fast) force-clear failed: " + ex.Message); }
                }
                else if (!_indexCacheService.IsIndexMissing)
                {
                    var loaded = _indexCacheService.GetIndex();
                    if (loaded != null && loaded.Objects.Count > 0)
                    {
                        // Fase 1: instead of trusting the cache forever, validate it and run a
                        // bounded delta refresh (only objects changed since the persisted
                        // high-water-mark). The loaded index serves reads immediately; the
                        // delta runs in the background. Falls through to a full rebuild when the
                        // cache isn't delta-eligible (legacy/no-sidecar, schema or worker-DLL
                        // change, or a body left partially enriched by a crashed worker).
                        var validation = _indexCacheService.ValidateOnDiskCache();
                        // Post-upgrade write-starvation fix: a worker-DLL change alone (DllMatch=False
                        // but SchemaMatch=True) no longer forces a full re-walk that blocks writes for
                        // minutes. Take the bounded delta and let StartDeltaRefreshThread re-baseline the
                        // sidecar's DLL hash via WriteMetaSidecar. See Configuration.DeltaAcrossWorkerDll.
                        bool dllRebaseline = !validation.CanDelta
                            && Configuration.DeltaAcrossWorkerDll
                            && validation.CanDeltaAcrossDll;
                        if (Configuration.UseDeltaOnOpen && (validation.CanDelta || dllRebaseline))
                        {
                            try { _indexCacheService.MarkIndexComplete(loaded.Objects.Count); } catch { }
                            _isIndexing = true;
                            StartDeltaRefreshThread(validation.HighWaterMark, loaded.Objects.Count);
                            Logger.Info($"BulkIndex(fast): warm cache delta-eligible ({loaded.Objects.Count} objects, hwm={validation.HighWaterMark:o}, dllRebaseline={dllRebaseline}) — delta refresh started.");
                            return Models.McpResponse.Ok(
                                code: "DeltaStarted",
                                result: new JObject
                                {
                                    ["objects"] = loaded.Objects.Count,
                                    ["hint"] = "Index is usable now from the warm cache; objects changed since last index are being refreshed in the background."
                                });
                        }
                        Logger.Info($"BulkIndex(fast): cache present but not delta-eligible (canDelta={validation.CanDelta} canDeltaAcrossDll={validation.CanDeltaAcrossDll} metaPresent={validation.MetaPresent} schemaMatch={validation.SchemaMatch} dllMatch={validation.DllMatch}) — full rebuild to re-establish the delta baseline.");
                    }
                }
            }
            catch { /* fall through and rebuild */ }

            _isIndexing = true;
            _processedCount = 0;
            _totalCount = 0;
            _currentStatus = "Lite-index pass starting...";

            var bulkSw = Stopwatch.StartNew();

            var liteThread = new Thread(() =>
            {
                try
                {
                    var liteSw = Stopwatch.StartNew();
                    dynamic kb = GetKB();
                    if (kb == null)
                    {
                        _isIndexing = false;
                        _currentStatus = "Error: KB not open";
                        return;
                    }

                    _currentStatus = "Lite-index pass: walking KB objects...";
                    _indexCacheService.BeginLiteWalk();
                    Logger.Info(_currentStatus);

                    // Fase 0 instrumentation: split the lite-pass wall-clock into
                    // enumerator-materialization vs per-object COM property reads vs
                    // in-loop snapshot flush, plus per-object-type counts and time.
                    // Uses Stopwatch.GetTimestamp() tick accumulators (no per-object
                    // Stopwatch object) so the overhead is negligible against the
                    // ~30ms-per-object COM marshalling cost.
                    var enumSw = Stopwatch.StartNew();
                    var objectList = (System.Collections.IEnumerable)kb.DesignModel.Objects;
                    enumSw.Stop();
                    Logger.Info($"[LITE-ENUM] elapsedMs={enumSw.ElapsedMilliseconds}");

                    var liteEntries = new List<SearchIndex.IndexEntry>();
                    long readTicks = 0, flushTicks = 0;
                    // value: [0]=accumulated read ticks, [1]=object count
                    var typeBuckets = new Dictionary<string, long[]>(StringComparer.Ordinal);

                    foreach (global::Artech.Architecture.Common.Objects.KBObject obj in objectList)
                    {
                        long objStart = Stopwatch.GetTimestamp();
                        _totalCount++;
                        // issue #25 #1: keep the observable "processed" counter moving
                        // during the lite walk. It sat at 0 the whole pass (only the
                        // legacy BulkIndexLegacy path advanced it), so status polls
                        // showed processed:0 with no way to gauge progress. In the lite
                        // pass every walked object IS processed, so track them together.
                        _processedCount = _totalCount;
                        string typeName = null;
                        try { typeName = obj.TypeDescriptor?.Name; } catch { }
                        if (string.IsNullOrEmpty(typeName)) typeName = obj.GetType().Name;

                        string description = null;
                        try { description = obj.Description; } catch { }

                        // v2.6.8: lite pass also captures lifecycle metadata. Reads
                        // are cheap on the KBObject handle (no part load), so we
                        // pay the cost once during the lite walk instead of
                        // forcing the user to wait for enrichment.
                        DateTime lu = DateTime.MinValue, ca = DateTime.MinValue;
                        string lub = null;
                        try { lu = obj.LastUpdate; } catch { }
                        try { ca = obj.VersionDate; } catch { }
                        try { lub = obj.UserName; } catch { }
                        // Fase 1: track the delta baseline (max LastUpdate) during the walk.
                        if (lu != DateTime.MinValue) _indexCacheService.ObserveLastUpdate(lu);
                        var hierarchy = _indexCacheService.ResolveHierarchyForIndex(obj);

                        liteEntries.Add(new SearchIndex.IndexEntry
                        {
                            Guid = obj.Guid.ToString(),
                            EntityKey = SafeEntityKey(obj),
                            EntityTypeGuid = SafeEntityTypeGuid(obj),
                            EntityId = SafeEntityId(obj),
                            Name = obj.Name,
                            Type = typeName,
                            Description = description,
                            Parent = hierarchy.ParentName,
                            ParentPath = hierarchy.ParentPath,
                            ParentFolderPath = string.IsNullOrEmpty(hierarchy.ParentPath) ? "Root Module" : "Root Module/" + hierarchy.ParentPath,
                            Path = hierarchy.Path,
                            Module = hierarchy.ModuleName,
                            LastUpdate = lu,
                            CreatedAt = ca,
                            LastModifiedBy = lub,
                            IsEnriched = false
                        });

                        long objTicks = Stopwatch.GetTimestamp() - objStart;
                        readTicks += objTicks;
                        if (!typeBuckets.TryGetValue(typeName, out var bucket))
                        {
                            bucket = new long[2];
                            typeBuckets[typeName] = bucket;
                        }
                        bucket[0] += objTicks;
                        bucket[1]++;

                        // v2.6.9 perf: stream partial snapshots during the lite walk
                        // so list_objects / query / inspect become usable while the
                        // walk is still in progress. SDK property reads run ~30ms
                        // each over COM marshal so we flush early (first at 100,
                        // then every 500) — first flush lands in ~15-20s on a
                        // 5k-object KB vs ~108s for the full lite pass.
                        if (_totalCount == 100 || (_totalCount > 100 && _totalCount % 500 == 0))
                        {
                            long flushStart = Stopwatch.GetTimestamp();
                            try
                            {
                                // Item 6: incremental AddOrUpdateBatch instead of ReplaceAll so
                                // enriched entries already in the cache are never overwritten with
                                // lite stubs during streaming progress flushes.
                                _indexCacheService.AddOrUpdateBatch(new List<SearchIndex.IndexEntry>(liteEntries));
                                _indexCacheService.MarkUltraLiteReady(_totalCount);
                            }
                            catch { /* best-effort; full ReplaceAll at end is authoritative */ }
                            flushTicks += Stopwatch.GetTimestamp() - flushStart;
                        }

                        if (_totalCount % 500 == 0) Thread.Sleep(1);

                        if (_totalCount % 1000 == 0)
                        {
                            try
                            {
                                GxMcp.Worker.Helpers.ProgressEmitter.Emit(
                                    _totalCount,
                                    Math.Max(_totalCount, 50000),
                                    "Lite-index pass: " + _totalCount + " objects captured");
                            }
                            catch { }
                        }
                    }

                    _indexCacheService.ReplaceAll(liteEntries);
                    _indexCacheService.MarkLitePassComplete(_totalCount);

                    // Fase 1 (robustness): persist the lite catalogue + a delta-eligible sidecar
                    // NOW, not only at full-enrichment completion. On a large KB the enrichment
                    // drain can run for many minutes and the worker may be idle-evicted before it
                    // finishes — in which case a completion-only sidecar would never be written and
                    // every warm start would full-rebuild. Writing it here makes warm start
                    // delta-eligible immediately; DeltaRefreshOnOpen resumes enrichment for any
                    // entries still flagged IsEnriched=false.
                    try { _indexCacheService.FlushNow(); _indexCacheService.WriteMetaSidecar(_totalCount); }
                    catch (Exception fx) { Logger.Warn("Lite-complete flush/sidecar failed: " + fx.Message); }

                    // Wire the enrichment queue BEFORE starting the background drain, so callers
                    // that hit ImpactAnalysis the moment LiteReady is published can promote
                    // their target on demand without a race window.
                    var queue = new EnrichmentQueue(BuildEnricher(kb, "Enrich"));
                    // Wire the queue regardless of mode so AnalyzeService etc. can PromoteAsync a
                    // specific target on demand. Only the EAGER mode pre-enqueues all 38k for the
                    // background drain; lazy mode leaves enrichment fully on-demand.
                    if (!Configuration.LazyEnrichment)
                        foreach (var entry in liteEntries) queue.Enqueue(entry);
                    _indexCacheService.SetEnrichmentQueue(queue);

                    liteSw.Stop();
                    _currentStatus = $"Lite pass complete: {_totalCount} objects.";

                    // Fase 0: attribute the lite-pass wall-clock. readMs is the headline —
                    // if it ~= elapsedMs the COM property reads dominate (and the walk is
                    // STA-bound, not parallelizable); flushMs is the in-loop snapshot cost.
                    double tickToMs = 1000.0 / Stopwatch.Frequency;
                    Logger.Info($"[LITE-WALK] readMs={(long)(readTicks * tickToMs)} flushMs={(long)(flushTicks * tickToMs)} objects={_totalCount}");
                    // Per-type counts + time, sorted desc by total read time, so we can see
                    // which object type dominates (e.g. Attributes on a large KB).
                    string typeBreakdown = string.Join(" ", typeBuckets
                        .OrderByDescending(kv => kv.Value[0])
                        .Select(kv => $"{kv.Key}={kv.Value[1]}/{(long)(kv.Value[0] * tickToMs)}ms"));
                    Logger.Info($"[LITE-TYPE-BREAKDOWN] {typeBreakdown}");
                    Logger.Info($"[BULK-INDEX-LITE] elapsedMs={liteSw.ElapsedMilliseconds} objects={_totalCount} typesDistinct={typeBuckets.Count}");

                    if (Configuration.LazyEnrichment)
                    {
                        // Fase 3 (lazy enrichment): the eager drain of all 38k objects was the
                        // dominant cost (~20min, ~91% of it STA-bound SDK reads per the measured
                        // split: textualScan ~51% + typeExtract ~40%). Most objects are never
                        // queried in a session, so enriching them all up front is mostly wasted.
                        // Instead the full catalogue (LiteReady) is published as Ready now, and
                        // edges/snippets/embeddings are filled in on demand via
                        // EnrichmentQueue.PromoteAsync when a tool (e.g. analyze/impact) needs a
                        // specific target. The lite-complete sidecar already persisted above keeps
                        // warm start delta-eligible.
                        _processedCount = _totalCount;
                        _indexCacheService.MarkIndexComplete(_totalCount);
                        bulkSw.Stop();
                        _currentStatus = "Complete";
                        _isIndexing = false;
                        Logger.Info($"[ENRICH-LAZY] eager drain skipped — {_totalCount} objects catalogued, enrichment on-demand. litePassMs={liteSw.ElapsedMilliseconds}");
                        return;
                    }

                    var enrichThread = new Thread(() =>
                    {
                        try
                        {
                            _indexCacheService.MarkEnrichmentStarted();
                            var enrichSw = Stopwatch.StartNew();
                            Logger.Info($"[ENRICH-START] pending={_totalCount} litePassMs={liteSw.ElapsedMilliseconds}");

                            queue.DrainAsync(default(CancellationToken),
                                (proc, tot) => { _processedCount = proc; _indexCacheService.MarkEnrichmentProgress(proc, tot); })
                                .GetAwaiter().GetResult();
                            _processedCount = _totalCount;
                            _indexCacheService.MarkIndexComplete(_totalCount);
                            // Fase 0.5: coalesced final flush — the per-object enrichment
                            // flushes are now throttled (30s), so force one write here to
                            // guarantee the fully-enriched index reaches disk.
                            // Fase 1: only NOW (enrichment fully drained) write the validation
                            // sidecar — its presence marks the on-disk body as delta-eligible.
                            try
                            {
                                _indexCacheService.FlushNow();
                                _indexCacheService.WriteMetaSidecar(_totalCount);
                            }
                            catch (Exception fx) { Logger.Warn("Final enrich flush/sidecar failed: " + fx.Message); }
                            enrichSw.Stop();
                            bulkSw.Stop();
                            _currentStatus = "Complete";
                            // Per-sub-step split: typeExtract+embedding are synchronous in the
                            // drain; refScan+textualScan run on the background dispatcher and
                            // may still be accumulating here (see GetEnrichTimingSummary note).
                            Logger.Info($"[ENRICH-DONE] elapsedMs={enrichSw.ElapsedMilliseconds} processed={_totalCount} {IndexCacheService.GetEnrichTimingSummary()}");
                            Logger.Info($"[BULK-INDEX-FULL] elapsedMs={bulkSw.ElapsedMilliseconds} processed={_totalCount}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("[BULK-INDEX-ENRICH-FAIL] error=" + ex.Message);
                            try { _indexCacheService.MarkIndexFailed(); } catch { }
                            _currentStatus = "Error: " + ex.Message;
                        }
                        finally { _isIndexing = false; }
                    }) {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = "GxMcp-Enrich"
                    };
                    enrichThread.SetApartmentState(ApartmentState.STA);
                    enrichThread.Start();
                }
                catch (Exception ex)
                {
                    _indexCacheService.EndLiteWalk();
                    Logger.Error("[BULK-INDEX-LITE-FAIL] error=" + ex.Message);
                    try { _indexCacheService.MarkIndexFailed(); } catch { }
                    _currentStatus = "Error: " + ex.Message;
                    _isIndexing = false;
                }
            }) {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "GxMcp-Lite"
            };
            liteThread.SetApartmentState(ApartmentState.STA);
            liteThread.Start();

            return Models.McpResponse.Ok(
                code: "LiteStarted",
                result: new JObject { ["hint"] = "list_objects is usable after a few seconds; analyze impact uses on-demand enrichment." });
        }

        // Shared enrich-one-entry closure used by the lite-pass queue, the delta resume queue,
        // and on-demand PromoteAsync: resolve the full SDK object by EntityKey/GUID/path.
        private IndexEntryEnricher BuildEnricher(dynamic kb, string logLabel)
        {
            return new IndexEntryEnricher(e =>
            {
                try
                {
                    if (e == null) return;
                    string resolutionStrategy;
                    var fullObj = ObjectService.ResolveIndexedObject(kb.DesignModel, e, out resolutionStrategy);
                    if (fullObj == null) return;
                    _indexCacheService.UpdateEntry(fullObj);
                }
                catch (Exception ex) { Logger.Warn(logLabel + " " + (e != null ? e.Name : "?") + " failed: " + ex.Message); }
            });
        }

        // EntityKey is the SDK identity that survives modular-name qualification.
        // Keep its scalar pieces in the index so a warm process can reconstruct it
        // without depending on a facade object's Guid lookup.
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

        // Fase 1: bounded delta refresh on warm start. The in-memory index is already
        // hydrated from the validated on-disk cache and serving reads; here we ask the SDK
        // (same GetKeys(timestamp) primitive KbWatcherService uses) for objects changed
        // since the persisted high-water-mark, re-index ONLY those, advance the hwm, and
        // re-persist. This replaces the full 38k re-walk on every warm start.
        // The refresh also performs a count/change-gated Guid sweep so deletions and
        // rename-to-new-key cases do not linger indefinitely in the restored index.
        private void StartDeltaRefreshThread(DateTime highWaterMark, int loadedCount)
        {
            var deltaThread = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                int changed = 0;
                try
                {
                    dynamic kb = GetKB();
                    if (kb == null) { _currentStatus = "Error: KB not open"; return; }

                    // Wire the on-demand enrichment queue NOW (lazy OR eager) so AnalyzeService can
                    // PromoteAsync a target after a warm-start restart. The lite pass is the only
                    // other place that sets it and it doesn't run on a warm delta — without this the
                    // queue stays null post-restart and on-demand enrichment silently never happens.
                    var enrichQueue = new EnrichmentQueue(BuildEnricher(kb, "Resume-enrich"));
                    _indexCacheService.SetEnrichmentQueue(enrichQueue);

                    // 2s safety margin for clock granularity / same-second edits (mirrors the
                    // watcher's > hwm re-filter below).
                    DateTime safeHwm = highWaterMark.AddSeconds(-2);
                    DateTime newHwm = highWaterMark;

                    var changedKeys = kb.DesignModel.Objects.GetKeys(safeHwm);
                    foreach (var key in (System.Collections.IEnumerable)changedKeys)
                    {
                        try
                        {
                            var obj = kb.DesignModel.Objects.Get((Artech.Udm.Framework.EntityKey)key);
                            if (obj == null) continue;
                            if (obj.LastUpdate <= safeHwm) continue; // re-filter like KbWatcherService
                            _indexCacheService.UpdateEntry(obj);
                            if (obj.LastUpdate > newHwm) newHwm = obj.LastUpdate;
                            changed++;
                        }
                        catch { /* skip individual object failures */ }
                    }

                    // Fase 2: authoritative deletion sweep, count-gated. GetKeys(hwm) never
                    // reports deletions, so a deleted object would linger as a stale entry.
                    // Only walk current GUIDs when the live object count dropped below the
                    // stored count — the common no-deletion case stays a fast delta.
                    int deleted = 0;
                    try
                    {
                        var idx = _indexCacheService.GetIndex();
                        int storedCount = idx.Objects.Count;
                        int currentCount = -1;
                        try { currentCount = (int)kb.DesignModel.Objects.Count; } catch { currentCount = -1; }
                        // Item 3: sweep when count dropped OR when objects changed (changed > 0
                        // means GUIDs may have rotated even if total count is the same).
                        if (currentCount >= 0 && (currentCount < storedCount || changed > 0) && idx.GuidToKey != null)
                        {
                            var currentGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (global::Artech.Architecture.Common.Objects.KBObject o in (System.Collections.IEnumerable)kb.DesignModel.Objects)
                            {
                                try { currentGuids.Add(o.Guid.ToString()); } catch { }
                            }
                            foreach (var storedGuid in idx.GuidToKey.Keys.ToList())
                            {
                                if (!currentGuids.Contains(storedGuid)) { _indexCacheService.RemoveEntryByGuid(storedGuid); deleted++; }
                            }
                            if (deleted > 0) Logger.Info($"[DELTA-DELETIONS] removed={deleted} storedCount={storedCount} currentCount={currentCount}");
                        }
                    }
                    catch (Exception dex) { Logger.Warn("Delta deletion sweep failed: " + dex.Message); }

                    int effectiveCount = _indexCacheService.GetIndex().Objects.Count;
                    _indexCacheService.ObserveLastUpdate(newHwm);
                    _indexCacheService.MarkIndexComplete(effectiveCount);
                    // Persist the merged body + refreshed sidecar (advances the hwm baseline).
                    try { _indexCacheService.FlushNow(); _indexCacheService.WriteMetaSidecar(effectiveCount); }
                    catch (Exception fx) { Logger.Warn("Delta refresh flush/sidecar failed: " + fx.Message); }

                    sw.Stop();
                    Logger.Info($"[DELTA-REFRESH] elapsedMs={sw.ElapsedMilliseconds} changed={changed} deleted={deleted} hwmBefore={highWaterMark:o} hwmAfter={newHwm:o} objects={effectiveCount}");

                    // Fase 1 (robustness): if the persisted body was lite-only or partially
                    // enriched (worker evicted mid-enrichment before), resume enrichment for the
                    // still-un-enriched entries so the warm cache converges to fully enriched.
                    // Embedding==null is the reliable "not enriched" signal.
                    // Fase 3: in lazy mode we DON'T resume-drain everything — changed objects were
                    // already enriched in the delta loop above; the rest stay on-demand.
                    var pendingEnrich = Configuration.LazyEnrichment
                        ? new System.Collections.Generic.List<SearchIndex.IndexEntry>()
                        : _indexCacheService.GetUnenrichedEntries();
                    if (pendingEnrich.Count > 0)
                    {
                        Logger.Info($"[DELTA-RESUME-ENRICH] pending={pendingEnrich.Count}");
                        foreach (var e in pendingEnrich) enrichQueue.Enqueue(e);
                        _indexCacheService.MarkEnrichmentStarted();
                        enrichQueue.DrainAsync(default(CancellationToken),
                            (proc, tot) => _indexCacheService.MarkEnrichmentProgress(proc, tot))
                            .GetAwaiter().GetResult();
                        _indexCacheService.MarkIndexComplete(loadedCount);
                        try { _indexCacheService.FlushNow(); _indexCacheService.WriteMetaSidecar(loadedCount); }
                        catch (Exception fx) { Logger.Warn("Resume-enrich flush/sidecar failed: " + fx.Message); }
                        Logger.Info($"[DELTA-RESUME-ENRICH-DONE] enriched={pendingEnrich.Count} {IndexCacheService.GetEnrichTimingSummary()}");
                    }

                    _currentStatus = "Complete";
                }
                catch (Exception ex)
                {
                    Logger.Error("[DELTA-REFRESH-FAIL] error=" + ex.Message);
                    _currentStatus = "Error: " + ex.Message;
                }
                finally { _isIndexing = false; }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "GxMcp-Delta"
            };
            deltaThread.SetApartmentState(ApartmentState.STA);
            deltaThread.Start();
        }

        // v2.3.8 (post-self-review) — force flag closes the "stale snapshot" gap.
        // Without force, the warm-start cache shortcuts to AlreadyIndexed even when
        // entries are missing edges (Calls/CalledBy) or new objects exist in the KB.
        // With force=true the in-memory + on-disk caches are cleared and the SDK
        // walk re-runs from scratch. SP6.T6 — preserved verbatim as the legacy path
        // behind Indexing.UseLitePass=false for one release before removal.
        private string BulkIndexLegacy(bool force)
        {
            Logger.Info($"BulkIndex(force={force}) requested.");
            if (_isIndexing) return Models.McpResponse.Ok(
                code: "AlreadyInProgress",
                result: new JObject { ["hint"] = "An index build is already running; poll genexus_whoami for progress." });

            // Wait briefly for the KB to open. The Gateway fires BulkIndex from the
            // initialize hook before the worker has opened the KB, so IsIndexMissing
            // would read true (cache path unknown) and trigger a redundant rebuild.
            try
            {
                int waitMs = 0;
                while (waitMs < 15000 && !_indexCacheService.IsInitialized)
                {
                    Thread.Sleep(200);
                    waitMs += 200;
                }
                if (force)
                {
                    Logger.Info("BulkIndex: force=true — clearing in-memory + on-disk snapshot before full rebuild.");
                    try
                    {
                        _indexCacheService.Clear();
                        // Preserve the last certified snapshot as a crash fallback; new shard writes are atomic.
                        _indexCacheService.MarkReindexStarted(0);
                    }
                    catch (Exception ex) { Logger.Warn("BulkIndex force-clear failed (continuing with rebuild anyway): " + ex.Message); }
                }
                else if (!_indexCacheService.IsIndexMissing)
                {
                    // Skip when the on-disk cache already populated the in-memory index — avoids
                    // a redundant full rebuild on every warm start. Callers wanting a real refresh
                    // must pass force=true.
                    var loaded = _indexCacheService.GetIndex();
                    if (loaded != null && loaded.Objects.Count > 0)
                    {
                        // v2.3.8 (post-Task 1.2 fix): warm-start path — publish Ready to
                        // IndexState so whoami stops reporting Cold while the in-memory
                        // index is in fact populated. GetIndex() also calls MarkIndexComplete
                        // on first hydration; this is the second safety net for the case
                        // where the index was already in memory before the BulkIndex call.
                        try { _indexCacheService.MarkIndexComplete(loaded.Objects.Count); } catch { }
                        Logger.Info($"BulkIndex skipped — cache already populated ({loaded.Objects.Count} objects). Pass force=true to rebuild.");
                        return Models.McpResponse.Ok(
                            code: "AlreadyIndexed",
                            result: new JObject
                            {
                                ["objects"] = loaded.Objects.Count,
                                ["hint"] = "Pass force=true to force a full SDK rescan when entries are missing edges or new objects exist."
                            });
                    }
                }
            }
            catch { /* fall through and rebuild */ }

            _isIndexing = true;
            _processedCount = 0;
            _totalCount = 0;
            _currentStatus = "Scanning objects...";

            // Start indexing in a dedicated STA thread to prevent blocking the command consumer
            var indexThread = new Thread(() => {
                // PERFORMANCE (instrumentation): measure end-to-end cold-start indexing time.
                var bulkSw = Stopwatch.StartNew();
                try {
                    dynamic kb = GetKB();
                    if (kb == null) {
                        _isIndexing = false;
                        _currentStatus = "Error: KB not open";
                        return;
                    }

                    _currentStatus = "Capturing KB objects snapshot...";
                    Logger.Info(_currentStatus);

                    var objectList = (System.Collections.IEnumerable)kb.DesignModel.Objects;
                    var objectSnapshot = new List<KeyValuePair<Guid, string>>();
                    foreach (global::Artech.Architecture.Common.Objects.KBObject obj in objectList)
                    {
                        objectSnapshot.Add(new KeyValuePair<Guid, string>(obj.Guid, obj.Name));
                        _totalCount++;
                        if (_totalCount % 500 == 0) Thread.Sleep(1); // Small breather during capture
                    }

                    _currentStatus = $"Indexing {_totalCount} objects using snapshot...";
                    Logger.Info(_currentStatus);

                    // v2.3.8 (Task 1.1): publish reindex start so downstream services (whoami,
                    // search, analyze) can detect Reindexing state from IndexCacheService.GetState().
                    try { _indexCacheService?.MarkReindexStarted(_totalCount); } catch { }

                    var indexSw = Stopwatch.StartNew();
                    foreach (var snapshotEntry in objectSnapshot)
                    {
                        try {
                            // Fetch object safely by stable identity. Name-based dynamic dispatch
                            // can bind to the wrong GeneXus SDK overload during bulk indexing.
                            var obj = kb.DesignModel.Objects.Get(snapshotEntry.Key);
                            if (obj == null) continue;

                            _indexCacheService.UpdateEntry(obj);
                            _processedCount++;
                            
                            int notifyInterval = Math.Max(500, _totalCount / 100);
                            if (_processedCount % notifyInterval == 0 || _processedCount == _totalCount) {
                                _currentStatus = $"Indexing KB: {_processedCount}/{_totalCount} objects";
                                Logger.Info(_currentStatus);

                                // MCP spec notifications/progress — clients render this natively
                                // as a progress bar. progressToken is a stable identifier for the
                                // background operation so clients can correlate repeated updates.
                                Program.SendNotification("notifications/progress", new {
                                    progressToken = GxMcp.Worker.Helpers.ProgressContext.CurrentToken ?? "genexus-mcp-bulk-index",
                                    progress = _processedCount,
                                    total = _totalCount,
                                    message = _currentStatus
                                });

                                // v2.3.8 (Task 1.1): mirror progress into IndexState so callers
                                // polling whoami can render the same ETA without piggy-backing on
                                // MCP notifications.
                                try {
                                    double frac = _totalCount > 0 ? (double)_processedCount / _totalCount : 0;
                                    long elapsedMs = indexSw.ElapsedMilliseconds;
                                    int etaMs = (frac > 0.0001)
                                        ? (int)Math.Max(0, (elapsedMs / frac) - elapsedMs)
                                        : 0;
                                    _indexCacheService?.MarkReindexProgress(frac, etaMs);
                                } catch { }
                            }
                            
                            // Ironclad Throttling: Give the system a breather every 50 objects
                            if (_processedCount % 50 == 0) Thread.Sleep(10);
                        } catch (Exception ex) {
                            Logger.Error($"Error indexing object {snapshotEntry.Value}: {ex.Message}");
                        }
                    }

                    _currentStatus = "Complete";
                    _isIndexing = false;
                    bulkSw.Stop();
                    // v2.3.8 (Task 1.1): publish completion to IndexState.
                    try { _indexCacheService?.MarkIndexComplete(_processedCount); } catch { }
                    Logger.Info($"[BULK-INDEX] elapsedMs={bulkSw.ElapsedMilliseconds} processed={_processedCount} total={_totalCount}");
                } catch (Exception ex) {
                    bulkSw.Stop();
                    Logger.Error($"[BULK-INDEX-FAIL] elapsedMs={bulkSw.ElapsedMilliseconds} error={ex.Message}");
                    _isIndexing = false;
                    _currentStatus = "Error: " + ex.Message;
                    // v2.3.8 (Task 1.1 review): reset IndexState on failure so callers don't
                    // see a permanent "Reindexing" status when bulk indexing throws after
                    // MarkReindexStarted. Wrapped in try/catch for resilience.
                    try { _indexCacheService?.MarkIndexFailed(); } catch { }
                }
            }) { 
                IsBackground = true, 
                Name = "AsyncIndexer", 
                Priority = ThreadPriority.BelowNormal 
            };
            indexThread.SetApartmentState(ApartmentState.STA);
            indexThread.Start();

            return Models.McpResponse.Ok(
                code: "Started",
                result: new JObject { ["hint"] = "Full SDK index started in the background; poll genexus_whoami for progress." });
        }

        public string GetIndexStatus()
        {
            var json = new Newtonsoft.Json.Linq.JObject();
            json["isIndexing"] = _isIndexing;
            json["total"] = _totalCount;
            json["processed"] = _processedCount;
            // issue #25 #1: while the lite walk runs, `total` is the running
            // count of objects walked SO FAR, not the KB's grand total (which is
            // only known once the walk completes). Flag it so a poller doesn't read
            // `total` as a fixed target / compute a bogus percentage.
            json["totalKnown"] = !_isIndexing;
            json["objectsWalked"] = _totalCount;
            json["status"] = _currentStatus;
            json["isBusy"] = _isIndexing || _isOpenInProgress;
            // Issue #27 item 3 (measured): when the index is loaded from the warm/delta
            // cache, the in-session walk counters (_totalCount/_processedCount) are never
            // set, so this reported total:0 / processed:0 / objectsWalked:0 even with a
            // fully-Ready index of thousands of objects — the "processed:0 the whole
            // session, impossible to know the total" confusion (#25.1). Fall back to the
            // authoritative IndexCacheService count when our own walk didn't run this
            // session. A live walk (_isIndexing) keeps its real running progress.
            try
            {
                if (!_isIndexing && _totalCount == 0)
                {
                    int stateTotal = _indexCacheService?.GetState()?.TotalObjects ?? 0;
                    if (stateTotal > 0)
                    {
                        json["total"] = stateTotal;
                        json["objectsWalked"] = stateTotal;
                        json["processed"] = stateTotal; // warm-loaded index is fully populated
                        json["totalKnown"] = true;
                        // The legacy prose 'status' is also empty on the warm-cache path
                        // (same unset-counter root cause). Fill it from the index state so
                        // the compact status isn't blank when the index is actually usable.
                        if (string.IsNullOrEmpty(json["status"]?.ToString()))
                            json["status"] = _indexCacheService?.GetState()?.Status ?? "Ready";
                    }
                }
            }
            catch { /* best-effort telemetry enrichment */ }
            return json.ToString();
        }

        // -----------------------------------------------------------------
        // v2.6.6 Stream H (FR#26) — active-environment cache surface.
        //
        // Worker-side resolver. The gateway calls this on cache miss; the
        // KbHandle holds the value for 60s and serves subsequent reads in
        // O(1). KbWatcherService.OnEnvironmentChanged invalidates the
        // gateway cache when the SDK fires its environment-changed event.
        // -----------------------------------------------------------------
        public string GetActiveEnvironment()
        {
            lock (_kbLock)
            {
                if (_kb == null) return null;
                // SDK exposes multiple shapes across major versions. U5 can return
                // an environment container whose public Name property is empty but
                // whose inherited property bag or TargetModel carries the name.
                // Probe all of those representations independently.
                object[] candidates =
                {
                    TryGet(() => (object)_kb.Environment?.TargetModel),
                    TryGet(() => (object)_kb.DesignModel?.Environment?.TargetModel),
                    TryGet(() => (object)_kb.ActiveModel),
                    TryGet(() => (object)_kb.Environment),
                    TryGet(() => (object)_kb.UserInterface?.ActiveEnvironment),
                    TryGet(() => (object)_kb.DesignModel?.Environment)
                };
                foreach (var candidate in candidates)
                {
                    if (candidate == null) continue;
                    try
                    {
                        if (_kb?.DesignModel != null && ReferenceEquals(candidate, _kb.DesignModel))
                            continue;
                    }
                    catch { }

                    string typeStr = TryGetMember(candidate, "Type")?.ToString();
                    if (string.Equals(typeStr, "Design", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(typeStr, "Backup", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = TryGetEnvironmentName(candidate);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                return null;
            }
        }

        // Resolve the actual <KB>\<environment>\web output root. The SDK's
        // display name can differ from the physical target folder;
        // probing TargetPath/TargetName keeps build evidence scoped to the active
        // environment instead of accidentally selecting another environment's newer files.
        public string GetActiveEnvironmentWebPath()
        {
            lock (_kbLock)
            {
                if (_kb == null) return null;
                string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
                if (string.IsNullOrWhiteSpace(kbPath) || !Directory.Exists(kbPath)) return null;

                object[] candidates =
                {
                    TryGet(() => (object)_kb.Environment),
                    TryGet(() => (object)_kb.UserInterface?.ActiveEnvironment),
                    TryGet(() => (object)_kb.DesignModel?.Environment),
                    TryGet(() => (object)_kb.ActiveModel),
                    TryGet(() => (object)_kb.Environment?.TargetModel),
                    TryGet(() => (object)_kb.Environment?.DesignModel)
                };
                string activeName = GetActiveEnvironment();
                var environmentRoots = Directory.GetDirectories(kbPath)
                    .Where(d => Directory.Exists(Path.Combine(d, "web")))
                    .ToList();
                // Prefer an unambiguous semantic match before probing SDK
                // properties: U5 can expose a stale TargetPath from another
                // environment even while GetActiveEnvironment reports development.
                string classifiedRoot = ResolveEnvironmentRoot(environmentRoots, activeName);
                if (!string.IsNullOrWhiteSpace(classifiedRoot))
                    return Path.Combine(classifiedRoot, "web");
                foreach (var candidate in candidates)
                {
                    if (candidate == null) continue;
                    foreach (var propertyName in new[] { "TargetPath", "OutputPath", "WebPath", "TargetName", "Name", "EnvironmentName" })
                    {
                        var value = TryGetMember(candidate, propertyName)?.ToString();
                        var webPath = ResolveEnvironmentWebPath(kbPath, value);
                        if (!string.IsNullOrWhiteSpace(webPath)
                            && IsCompatibleEnvironmentPath(webPath, activeName))
                            return webPath;
                    }
                }

                return null;
            }
        }

        private static string ResolveEnvironmentRoot(IEnumerable<string> roots, string activeName)
        {
            if (roots == null || string.IsNullOrWhiteSpace(activeName)) return null;
            var rootList = roots.ToList();
            string exact = rootList.FirstOrDefault(d =>
                string.Equals(Path.GetFileName(d), activeName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            bool production = activeName.IndexOf("prod", StringComparison.OrdinalIgnoreCase) >= 0;
            bool development = activeName.IndexOf("dev", StringComparison.OrdinalIgnoreCase) >= 0
                || activeName.IndexOf("desenv", StringComparison.OrdinalIgnoreCase) >= 0;
            var classified = rootList.Where(d =>
            {
                string folder = Path.GetFileName(d) ?? string.Empty;
                bool isProductionFolder = folder.IndexOf("prod", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isDevelopmentFolder = folder.IndexOf("dev", StringComparison.OrdinalIgnoreCase) >= 0
                    || folder.IndexOf("desenv", StringComparison.OrdinalIgnoreCase) >= 0
                    || folder.IndexOf("web", StringComparison.OrdinalIgnoreCase) >= 0;
                return production ? isProductionFolder : development && !isProductionFolder && isDevelopmentFolder;
            }).ToList();
            return classified.Count == 1 ? classified[0] : null;
        }

        private static bool IsCompatibleEnvironmentPath(string webPath, string activeName)
        {
            if (string.IsNullOrWhiteSpace(webPath) || string.IsNullOrWhiteSpace(activeName)) return true;
            string folder = Path.GetFileName(Path.GetDirectoryName(webPath)) ?? string.Empty;
            bool pathIsProduction = folder.IndexOf("prod", StringComparison.OrdinalIgnoreCase) >= 0;
            bool activeIsProduction = activeName.IndexOf("prod", StringComparison.OrdinalIgnoreCase) >= 0;
            bool activeIsDevelopment = activeName.IndexOf("dev", StringComparison.OrdinalIgnoreCase) >= 0
                || activeName.IndexOf("desenv", StringComparison.OrdinalIgnoreCase) >= 0;
            if (activeIsDevelopment && pathIsProduction) return false;
            if (activeIsProduction && !pathIsProduction) return false;
            return true;
        }

        private static string ResolveEnvironmentWebPath(string kbPath, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();
            var candidates = new List<string>();
            if (Path.IsPathRooted(trimmed))
            {
                candidates.Add(trimmed);
                candidates.Add(Path.Combine(trimmed, "web"));
            }
            else
            {
                candidates.Add(Path.Combine(kbPath, trimmed, "web"));
                candidates.Add(Path.Combine(kbPath, trimmed));
            }

            foreach (var candidate in candidates)
            {
                if (string.Equals(Path.GetFileName(candidate), "web", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(candidate))
                    return candidate;
                if (Directory.Exists(Path.Combine(candidate, "web")))
                    return Path.Combine(candidate, "web");
            }
            return null;
        }

        // Enumerate all environment models configured in the open KB.
        public string ListEnvironments()
        {
            lock (_kbLock)
            {
                if (_kb == null) throw new InvalidOperationException("Knowledge Base is not open.");

                string activeName = GetActiveEnvironment();
                string activeWebPath = GetActiveEnvironmentWebPath();
                string kbPath = GetKbPath() ?? Environment.GetEnvironmentVariable("GX_KB_PATH");

                var envModels = EnumerateEnvironmentModels();
                var envArray = new JArray();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var model in envModels)
                {
                    if (model == null) continue;
                    string name = TryGetEnvironmentName(model);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = TryGetMember(model, "Name")?.ToString();
                    }
                    if (string.IsNullOrWhiteSpace(name) || seenNames.Contains(name))
                        continue;

                    seenNames.Add(name);

                    string description = TryGetMember(model, "Description")?.ToString() ?? string.Empty;
                    bool isActive = !string.IsNullOrWhiteSpace(activeName) &&
                                    string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase);

                    string targetPath = TryGetMember(model, "TargetPath")?.ToString();
                    string webPath = null;
                    if (isActive && !string.IsNullOrWhiteSpace(activeWebPath))
                    {
                        webPath = activeWebPath;
                    }
                    else if (!string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(kbPath))
                    {
                        webPath = ResolveEnvironmentWebPath(kbPath, targetPath);
                    }
                    if (string.IsNullOrWhiteSpace(webPath) && !string.IsNullOrWhiteSpace(kbPath))
                    {
                        webPath = ResolveEnvironmentWebPath(kbPath, name);
                    }

                    string generator = ResolveModelGenerator(model);

                    var envObj = new JObject
                    {
                        ["name"] = name,
                        ["description"] = description,
                        ["generator"] = generator,
                        ["isActive"] = isActive,
                        ["webPath"] = webPath
                    };
                    envArray.Add(envObj);
                }

                // If active environment was not among enumerated models, add it as fallback entry
                if (!string.IsNullOrWhiteSpace(activeName) &&
                    !string.Equals(activeName, "Design", StringComparison.OrdinalIgnoreCase) &&
                    !seenNames.Contains(activeName))
                {
                    var activeObj = new JObject
                    {
                        ["name"] = activeName,
                        ["description"] = string.Empty,
                        ["generator"] = null,
                        ["isActive"] = true,
                        ["webPath"] = activeWebPath
                    };
                    envArray.Add(activeObj);
                }

                var response = new JObject
                {
                    ["activeEnvironment"] = activeName,
                    ["environments"] = envArray
                };

                return response.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        private List<object> EnumerateEnvironmentModels()
        {
            var models = new List<object>();
            var seen = new HashSet<object>();

            void AddModel(object m)
            {
                if (m == null) return;

                try
                {
                    if (m is KBModel kbM)
                    {
                        if (_kb?.DesignModel is KBModel dm && (ReferenceEquals(kbM, dm) || kbM.Id == dm.Id))
                            return;
                        if (kbM.Type == Artech.Udm.Framework.ModelType.Design ||
                            kbM.Type == Artech.Udm.Framework.ModelType.Backup)
                            return;
                    }
                    else if (_kb?.DesignModel != null)
                    {
                        if (ReferenceEquals(m, _kb.DesignModel))
                            return;
                        var mId = TryGetMember(m, "Id");
                        var dId = TryGetMember(_kb.DesignModel, "Id");
                        if (mId != null && dId != null && Equals(mId, dId))
                            return;
                    }
                }
                catch { }

                string typeStr = TryGetMember(m, "Type")?.ToString() ?? string.Empty;
                if (string.Equals(typeStr, "Design", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeStr, "Backup", StringComparison.OrdinalIgnoreCase))
                    return;

                if (seen.Add(m))
                    models.Add(m);
            }

            try { AddModel((object)_kb.DesignModel?.Environment?.TargetModel); } catch { }
            try { AddModel((object)_kb.Environment?.TargetModel); } catch { }
            try { AddModel((object)_kb.ActiveModel); } catch { }

            try
            {
                var envModels = _kb.DesignModel?.Environment?.Models;
                if (envModels is System.Collections.IEnumerable envEnum)
                {
                    foreach (var m in envEnum) AddModel(m);
                }
            }
            catch { }

            try
            {
                var envModels = _kb.Environment?.Models;
                if (envModels is System.Collections.IEnumerable envEnum)
                {
                    foreach (var m in envEnum) AddModel(m);
                }
            }
            catch { }

            try
            {
                var allModels = _kb.Models;
                if (allModels != null)
                {
                    try
                    {
                        int designId = _kb.DesignModel.Id;
                        var children = allModels.GetChildren(designId);
                        if (children is System.Collections.IEnumerable childEnum)
                        {
                            foreach (var m in childEnum) AddModel(m);
                        }
                    }
                    catch { }

                    try
                    {
                        var all = allModels.GetAll();
                        if (all is System.Collections.IEnumerable allEnum)
                        {
                            foreach (var m in allEnum)
                            {
                                AddModel(m);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return models;
        }

        private static string ResolveModelGenerator(object model)
        {
            try
            {
                if (model is KBModel kbModel)
                {
                    try
                    {
                        var gxModel = kbModel.GetAs<GxModel>() ?? new GxModel(kbModel);
                        var mainGen = gxModel?.Generator;
                        if (mainGen != null)
                        {
                            string text = mainGen.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                                return text;
                            if (!string.IsNullOrWhiteSpace(mainGen.Description))
                                return mainGen.Description;
                        }
                    }
                    catch { }

                    var part = kbModel.Parts.Get<GeneratorsPart>();
                    if (part?.Generators != null)
                    {
                        var candidate = part.Generators.FirstOrDefault(g => !g.IsReorgGen && g.Category?.Name == "Web")
                                     ?? part.Generators.FirstOrDefault(g => !g.IsReorgGen && (g.ToString() ?? string.Empty).IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0)
                                     ?? part.Generators.FirstOrDefault(g => !g.IsReorgGen)
                                     ?? part.Generators.FirstOrDefault();

                        if (candidate != null)
                        {
                            string text = candidate.ToString();
                            return !string.IsNullOrWhiteSpace(text) ? text : candidate.Description;
                        }
                    }
                }
                else
                {
                    // Fallback for mock/fake objects in unit tests or dynamic wrappers
                    var directGen = TryGetMember(model, "Generator");
                    if (directGen != null)
                    {
                        if (directGen is string s && !string.IsNullOrWhiteSpace(s))
                            return s;

                        string text = directGen.ToString();
                        if (!string.IsNullOrWhiteSpace(text) && text != directGen.GetType().FullName)
                            return text;
                        string desc = TryGetMember(directGen, "Description")?.ToString();
                        if (!string.IsNullOrWhiteSpace(desc))
                            return desc;
                    }

                    dynamic dyn = model;
                    dynamic parts = null;
                    try { parts = dyn.Parts; } catch { }
                    if (parts != null)
                    {
                        dynamic part = null;
                        try { part = parts.Get(typeof(GeneratorsPart)) ?? parts.Get("Generators"); } catch { }
                        if (part?.Generators != null)
                        {
                            object bestGen = null;
                            foreach (dynamic gen in part.Generators)
                            {
                                if (gen == null) continue;
                                bool isReorg = false;
                                try { isReorg = gen.IsReorgGen == true; } catch { }
                                if (isReorg) continue;

                                string catName = null;
                                try { catName = (string)gen.Category?.Name?.ToString(); } catch { }
                                string genStr = null;
                                try { genStr = (string)gen.ToString(); } catch { }

                                if (string.Equals(catName, "Web", StringComparison.OrdinalIgnoreCase))
                                {
                                    bestGen = gen;
                                    break;
                                }
                                if (genStr != null && genStr.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    bestGen = gen;
                                }
                                else if (bestGen == null)
                                {
                                    bestGen = gen;
                                }
                            }

                            if (bestGen != null)
                            {
                                dynamic bg = bestGen;
                                string text = null;
                                try { text = (string)bg.ToString(); } catch { }
                                if (!string.IsNullOrWhiteSpace(text) && text != bestGen.GetType().FullName)
                                    return text;
                                string desc = null;
                                try { desc = (string)bg.Description?.ToString(); } catch { }
                                if (!string.IsNullOrWhiteSpace(desc))
                                    return desc;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // Switch the active GeneXus environment. Prefers direct SDK model activation
        // under _kbLock to avoid MSBuild task UI Dispatcher issues in headless workers,
        // with fallback to the MSBuild task if direct model setting is inconclusive.
        public string SetActiveEnvironment(string environmentName)
        {
            if (string.IsNullOrWhiteSpace(environmentName))
                throw new ArgumentException("Environment name is required.", nameof(environmentName));

            string trimmed = environmentName.Trim();

            lock (_kbLock)
            {
                if (_kb == null) throw new InvalidOperationException("Knowledge Base is not open.");

                string previous = GetActiveEnvironment();

                // Find candidate model for the requested environment
                var envModels = EnumerateEnvironmentModels();
                object targetModel = envModels.FirstOrDefault(m =>
                {
                    if (m == null) return false;
                    string name = TryGetEnvironmentName(m);
                    if (string.IsNullOrWhiteSpace(name))
                        name = TryGetMember(m, "Name")?.ToString();
                    if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                        return true;

                    string targetPath = TryGetMember(m, "TargetPath")?.ToString();
                    if (!string.IsNullOrWhiteSpace(targetPath) &&
                        string.Equals(Path.GetFileName(targetPath), trimmed, StringComparison.OrdinalIgnoreCase))
                        return true;

                    return false;
                });

                bool switched = false;

                // 1. Direct SDK activation (Headless-safe: avoids MSBuild task UI Dispatcher / Output exceptions)
                if (targetModel != null)
                {
                    try
                    {
                        var designEnv = TryGet(() => (object)_kb.DesignModel?.Environment);
                        if (designEnv != null)
                        {
                            var prop = designEnv.GetType().GetProperty("TargetModel", BindingFlags.Public | BindingFlags.Instance);
                            prop?.SetValue(designEnv, targetModel, null);
                            try { designEnv.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.SetValue(designEnv, TryGetEnvironmentName(targetModel), null); } catch { }
                        }

                        var kbEnv = TryGet(() => (object)_kb.Environment);
                        if (kbEnv != null)
                        {
                            var prop = kbEnv.GetType().GetProperty("TargetModel", BindingFlags.Public | BindingFlags.Instance);
                            prop?.SetValue(kbEnv, targetModel, null);
                            try { kbEnv.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.SetValue(kbEnv, TryGetEnvironmentName(targetModel), null); } catch { }
                        }

                        try
                        {
                            dynamic user = _kb.User;
                            if (user != null)
                            {
                                dynamic dynTarget = targetModel;
                                dynamic dynDesign = TryGet(() => dynTarget.GetDesignModel()) ?? _kb.DesignModel;
                                user.SetTargetModel(dynDesign, targetModel);
                                user.Save();
                            }
                        }
                        catch (Exception exUser)
                        {
                            Logger.Warn("[KB-SET-ENV] User.SetTargetModel warning: " + exUser.Message);
                        }

                        string current = GetActiveEnvironment();
                        if (string.Equals(current, trimmed, StringComparison.OrdinalIgnoreCase) ||
                            (targetModel != null && string.Equals(current, TryGetEnvironmentName(targetModel), StringComparison.OrdinalIgnoreCase)))
                        {
                            switched = true;
                            Logger.Info($"[KB-SET-ENV] Switched active environment to '{current}' directly via SDK.");
                        }
                    }
                    catch (Exception exDirect)
                    {
                        Logger.Warn("[KB-SET-ENV] Direct SDK switch failed: " + exDirect.Message);
                    }
                }

                // 2. Fallback to MSBuild task if direct switch did not complete
                if (!switched)
                {
                    try
                    {
                        Type taskType = ResolveMsBuildTaskType("Genexus.MsBuild.Tasks.SetActiveEnvironment");
                        object task = Activator.CreateInstance(taskType);
                        SetTaskProperty(task, "KB", _kb);
                        SetTaskProperty(task, "EnvironmentName", trimmed);
                        SetTaskProperty(task, "RedirectIPC", false);

                        var execute = taskType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
                        if (execute != null && execute.Invoke(task, null) is bool ok && ok)
                        {
                            switched = true;
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        Logger.Warn("[KB-SET-ENV] MSBuild task execution failed: " + (tie.InnerException?.Message ?? tie.Message));
                    }
                    catch (Exception exTask)
                    {
                        Logger.Warn("[KB-SET-ENV] MSBuild task execution error: " + exTask.Message);
                    }
                }

                string active = GetActiveEnvironment();
                bool matches = string.Equals(active, trimmed, StringComparison.OrdinalIgnoreCase) ||
                               (targetModel != null && string.Equals(active, TryGetEnvironmentName(targetModel), StringComparison.OrdinalIgnoreCase));

                if (!matches)
                {
                    throw new InvalidOperationException(
                        $"GeneXus could not activate environment '{trimmed}'. The active environment remained '{active ?? "<unknown>"}'.");
                }

                return new JObject
                {
                    ["previous"] = previous,
                    ["requested"] = trimmed,
                    ["active"] = active,
                    ["changed"] = !string.Equals(previous, active, StringComparison.OrdinalIgnoreCase)
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        private static Type ResolveMsBuildTaskType(string fullName)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);
            if (loaded != null) return loaded;

            string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? string.Empty;
            string assemblyPath = Path.Combine(gxPath, "Genexus.MsBuild.Tasks.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("GeneXus MSBuild task assembly was not found.", assemblyPath);

            var assembly = Assembly.LoadFrom(assemblyPath);
            return assembly.GetType(fullName, true);
        }

        private static void SetTaskProperty(object task, string name, object value)
        {
            var property = task.GetType().GetProperty(
                name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite)
                throw new MissingMemberException(task.GetType().FullName, name);
            property.SetValue(task, value, null);
        }

        private static object TryGet(Func<object> getter)
        {
            try { return getter(); } catch { return null; }
        }

        private static string TryGetEnvironmentName(object candidate)
        {
            if (candidate == null) return null;

            foreach (var propertyName in new[] { "Name", "EnvironmentName" })
            {
                var value = TryGetMember(candidate, propertyName);
                if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                    return value.ToString();

                value = TryGetPropertyBagValue(candidate, propertyName);
                if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                    return value.ToString();
            }

            foreach (var childName in new[] { "TargetModel", "ActiveModel", "Model" })
            {
                var child = TryGetMember(candidate, childName);
                var name = TryGetEnvironmentName(child);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return null;
        }

        private static object TryGetMember(object target, string name) =>
            ReflectionHelper.TryGetMember(target, name);

        private static object TryGetPropertyBagValue(object target, string name) =>
            ReflectionHelper.TryGetPropertyBagValue(target, name);

        public string GetActiveEnvironmentVersion()
        {
            lock (_kbLock)
            {
                if (_kb == null) return null;
                try { var v = _kb.Environment?.Version; if (v != null) return v.ToString(); } catch { }
                try { var v = _kb.UserInterface?.ActiveEnvironment?.Version; if (v != null) return v.ToString(); } catch { }
                try { var v = _kb.ActiveModel?.Version; if (v != null) return v.ToString(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#25) — F5 launcher resolver.
        ///
        /// Mirrors the IDE's F5/Run behaviour: pick the KB's configured
        /// startup/main object. Probes the SDK first; falls back to the
        /// in-memory index for the first object whose Tags contain "Main"
        /// (the same heuristic <c>HealthService.IsMainObject</c> uses).
        /// Returns <c>null</c> when no candidate exists — callers surface
        /// a <c>NoLauncher</c> envelope rather than guessing wrong.
        /// </summary>
        public string GetLauncherObjectName()
        {
            lock (_kbLock)
            {
                if (_kb != null)
                {
                    try { var v = _kb.DefaultStartupObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                    try { var v = _kb.UserInterface?.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                    try { var v = _kb.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                }
            }

            // Fall back to the index — first "Main"-tagged WebPanel/SDPanel
            // wins (matches HealthService.IsMainObject heuristic). Reads the
            // index outside the kb lock to keep startup-thread contention low.
            try
            {
                var idx = _indexCacheService?.GetIndex();
                if (idx == null) return null;
                foreach (var entry in idx.Objects.Values)
                {
                    if (entry == null) continue;
                    bool isMain = (entry.Tags != null && entry.Tags.Any(t => t.Equals("Main", StringComparison.OrdinalIgnoreCase)))
                               || (entry.Description != null && entry.Description.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isMain) continue;
                    string type = entry.Type ?? string.Empty;
                    if (type.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("SDPanel", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Name;
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("GetLauncherObjectName fallback failed: " + ex.Message); }
            return null;
        }

        public string EnsureNotIndexing()
        {
            if (_isIndexing)
            {
                return Models.McpResponse.Err(code: "KbBusyIndexing",
                    message: "Knowledge Base is currently busy performing a background indexing task.",
                    hint: "Poll genexus_lifecycle action=status wait=<sec> — it returns the moment indexing finishes — then retry.",
                    nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_lifecycle", new Newtonsoft.Json.Linq.JObject { ["action"] = "status", ["wait"] = 30 }, "Block until the index build finishes, then retry the call.")),
                    extra: new Newtonsoft.Json.Linq.JObject { ["isBusy"] = true });
            }
            if (_isOpenInProgress)
            {
                return Models.McpResponse.Err(code: "KbOpening",
                    message: "Knowledge Base is currently opening.",
                    hint: "Wait a few seconds and retry; genexus_whoami shows when the worker/KB is ready.",
                    extra: new Newtonsoft.Json.Linq.JObject { ["isBusy"] = true });
            }
            return null;
        }
    }
}
