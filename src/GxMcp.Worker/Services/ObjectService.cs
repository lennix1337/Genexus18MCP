using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class ObjectService
    {
        private sealed class ReadCacheEntry
        {
            public string Payload { get; set; } = string.Empty;
            public DateTime UpdatedUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, ReadCacheEntry> _readCache =
            new ConcurrentDictionary<string, ReadCacheEntry>(StringComparer.OrdinalIgnoreCase);
        // Large source bodies are useful to repeat source searches but must not share the
        // general JSON/read cache: one response can be megabytes and the normal cache has
        // no size-aware eviction. Keep a tiny, TTL-bound side cache for search-only reuse;
        // persisted FullSource remains capped separately at 256 KiB.
        private static readonly ConcurrentDictionary<string, ReadCacheEntry> _largeRawSourceCache =
            new ConcurrentDictionary<string, ReadCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> _emptyRawSourceCache =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // PERFORMANCE: ReadCacheTtl extended to 300s (5 minutes) default, with GXMCP_READ_CACHE_TTL_SEC
        // override. Since writes already perform deterministic cache invalidation (MarkReadCacheDirty /
        // InvalidateCache), retaining read cache across multi-turn agent reasoning avoids redundant
        // COM round-trips to disk.
        private static readonly TimeSpan ReadCacheTtl = ResolveReadCacheTtl();

        private static TimeSpan ResolveReadCacheTtl()
        {
            string env = Environment.GetEnvironmentVariable("GXMCP_READ_CACHE_TTL_SEC");
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out int sec) && sec > 0)
            {
                return TimeSpan.FromSeconds(sec);
            }
            return TimeSpan.FromMinutes(5);
        }

        // Records successful deletions so a follow-up DeleteObject call that arrives
        // after a gateway timeout (worker finished after the pipe died) is reported as
        // success-confirmed-after-timeout instead of the ambiguous "Object not found".
        // Key: "<type>:<name>" lower-case. Pruned lazily on hit + TTL on lookup.
        private static readonly ConcurrentDictionary<string, DateTime> _recentDeletions =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RecentDeletionTtl = TimeSpan.FromMinutes(5);
        private const int MaxIncomingReferences = 256;

        private enum NativeResolutionStatus
        {
            Found,
            Absent,
            Failed
        }

        private sealed class NativeResolution
        {
            public NativeResolutionStatus Status;
            public KBObject Object;
            public Exception Error;
        }

        private static string RecentDeletionKey(string type, string name) =>
            ((type ?? "") + ":" + (name ?? "")).ToLowerInvariant();

        // v2.6.8 (review C8): crash-line detector. Anchored markers only —
        // bare "critical" inside a log message (e.g., "critical section",
        // "no critical errors") must NOT trip the matcher.
        private static readonly System.Text.RegularExpressions.Regex _crashLinePattern =
            new System.Text.RegularExpressions.Regex(
                @"\[(ERROR|CRITICAL|FATAL)\]|\bCRITICAL\s+(?:Init|Error|Failure|Exception)\b|\bUnhandled\s+exception\b",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private readonly KbService _kbService;
        private readonly BuildService _buildService;
        private DataInsightService _dataInsightService;
        private UIService _uiService;
        private PatternAnalysisService _patternAnalysisService;
        private WriteService _writeService;

        public ObjectService(KbService kbService, BuildService buildService)
        {
            _kbService = kbService;
            _buildService = buildService;
        }

        public void SetDataInsightService(DataInsightService ds) { _dataInsightService = ds; }
        public void SetUIService(UIService ui) { _uiService = ui; }
        public void SetPatternAnalysisService(PatternAnalysisService patternAnalysisService) { _patternAnalysisService = patternAnalysisService; }
        public void SetWriteService(WriteService writeService) { _writeService = writeService; }
        public KbService GetKbService() { return _kbService; }

        public SearchIndex GetIndex() { return _kbService.GetIndexCache().GetIndex(); }

        // Non-blocking index accessor: returns the in-memory index if it's already
        // loaded, otherwise null — never triggers a synchronous 30-60s cold load on
        // the STA thread. Prefer this on hot paths (object resolution, not-found
        // suggestions) so one cold KB doesn't stall every queued tool call.
        public SearchIndex GetLoadedIndexOrNull() { return _kbService.GetIndexCache().TryGetLoadedIndex(); }

        /// <summary>
        /// Returns all index entries whose name matches <paramref name="name"/> (case-insensitive).
        /// Works without a KB open — the index may be pre-populated via IndexCacheService.UpdateIndex().
        /// Returns an empty list when the index is empty or no entries match.
        /// </summary>
        public List<SearchIndex.IndexEntry> FindCandidateEntries(string name)
        {
            if (string.IsNullOrEmpty(name)) return new List<SearchIndex.IndexEntry>();
            // B14: this feeds the advisory homonym/ambiguity pre-check on the write hot
            // path. It MUST NOT trigger a blocking 30s-3min synchronous index load — that
            // is exactly what stalled Structure writes on a duplicate-name Transaction and
            // hit the gateway timeout. Use the non-blocking accessor; when the index isn't
            // warm yet the ambiguity hint is simply skipped and FindObject (already
            // non-blocking) resolves the object anyway.
            var index = GetLoadedIndexOrNull();
            if (index?.Objects == null) return new List<SearchIndex.IndexEntry>();
            var results = new List<SearchIndex.IndexEntry>();
            foreach (var entry in index.Objects.Values)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    results.Add(entry);
            }
            return results;
        }

        public string CreateObject(string type, string name)
        {
            return CreateObject(type, name, null);
        }

        public string CreateObject(string type, string name, JObject options)
        {
            var sw = Stopwatch.StartNew();
            // Item 21 (friction 2026-05-22) — universal dryRun: report planned shape
            // without calling Save(). Resolved type guid + pre-flight duplicate check
            // still run so the agent sees the same validation failures as the live call.
            bool dryRun = options?["dryRun"]?.ToObject<bool?>() ?? false;
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return McpResponse.Err(code: "NoKb", message: "No KB open");

                // issue #50 (revised): a requested folder/module destination IS honored — the
                // object is created in Root Module, then moved via MoveObject (see ObjectMover
                // for why the earlier "SDK can't place objects" verdict was a facade-DLL
                // decompilation artefact). The move runs after Save and is verified below.
                // NOTE: read "destModule", never "module" — at the worker layer options["module"]
                // is the routing key ("Object"), not a destination.
                string requestedFolder = options?["folder"]?.ToString();
                string requestedModule = options?["destModule"]?.ToString();
                string requestedParent = options?["parentPath"]?.ToString();
                string requestedPlacement = !string.IsNullOrWhiteSpace(requestedFolder) ? requestedFolder
                    : !string.IsNullOrWhiteSpace(requestedModule) ? requestedModule
                    : !string.IsNullOrWhiteSpace(requestedParent) ? requestedParent : null;
                string requestedPlacementKind = !string.IsNullOrWhiteSpace(requestedFolder) ? "Folder"
                    : !string.IsNullOrWhiteSpace(requestedModule) ? "Module" : null;

                Logger.Info(string.Format("Creating Object: {0} ({1})", name, type));

                // Map string type to Guid. First try the well-known descriptor table (covers
                // every type with a concrete wrapper class), then fall back to ObjClass.<Name>
                // reflection so types without a public wrapper (SDPanel, Dashboard, Query,
                // WorkflowDiagram, ConversationalFlows, TestSuite, WikiPage, WorkWithWeb,
                // WorkWithDevices, etc.) are still creatable by name.
                Guid typeGuid = ResolveObjectTypeGuid(type);
                if (typeGuid == Guid.Empty)
                {
                    return McpResponse.Err(
                        code: "UnsupportedObjectType",
                        message: "Unsupported object type: " + type,
                        hint: "Known types: Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, ExternalObject, Image, Theme, ThemeClass, DesignSystem, ColorPalette, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, Dashboard, Query, WorkflowDiagram, ConversationalFlows, TestSuite, API, URLRewrite, MiniApp, SuperApp, OfflineDatabase, DataView, Group, Language, TranslationMessage, WorkWithDevices, WorkWithWeb.");
                }

                // Pre-flight duplicate check: gives a clear, structured error before the SDK throws.
                try
                {
                    var existing = kb.DesignModel.Objects.Get(typeGuid, name);
                    if (existing != null)
                    {
                        return McpResponse.Err(
                            code: "AlreadyExists",
                            message: type + " '" + name + "' already exists.",
                            target: name);
                    }
                }
                catch { /* lookup is best-effort; if it throws, Save will surface the duplicate error anyway */ }

                // A Transaction dry-run must not even construct the SDK object. The
                // legacy initializer creates a KB-global seed Attribute as a side effect,
                // so returning after KBObject.Create was not a read-only preview.
                if (dryRun && type.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
                {
                    return McpResponse.Ok(
                        target: name,
                        code: "DryRun",
                        result: new JObject
                        {
                            ["dryRun"] = true,
                            ["persisted"] = false,
                            ["mutationDetected"] = false,
                            ["type"] = type,
                            ["name"] = name,
                            ["seededDescription"] = name + "Id : Numeric(8,0) [Key]",
                            ["hint"] = "Re-run without dryRun to create the Transaction and its seed attribute."
                        });
                }

                KBObject newObj = CreateObjectInstance(type, name, options, out string seededDescription, out JObject domainMeta);

                if (dryRun)
                {
                    // Item 21 (friction 2026-05-22) — return planned shape without persisting.
                    // SDK in-memory artefact is discarded (GC-collected) since we don't hold
                    // a reference past this method. Pre-flight checks (type resolution,
                    // duplicate name) already ran above so the LLM sees real validation.
                    return McpResponse.Ok(
                        target: name,
                        code: "DryRun",
                        result: new JObject
                        {
                            ["dryRun"] = true,
                            ["type"] = type,
                            ["name"] = name,
                            ["seededDescription"] = seededDescription,
                            ["hint"] = "Re-run without dryRun to call Save()."
                        });
                }

                newObj.Save();

                // Best-effort: refresh search index so subsequent list/query calls see this object
                // without waiting for a full reindex.
                try
                {
                    var idx = _kbService?.GetIndexCache();
                    if (idx != null) idx.UpdateEntry(newObj);
                }
                catch (Exception ex) { Logger.Error("CreateObject: index UpdateEntry failed for " + name + ": " + ex.Message); }

                Logger.Info(string.Format("Object created successfully in {0}ms", sw.ElapsedMilliseconds));

                // Honor a requested folder/module placement by moving the just-created
                // object. If the move fails the object still exists in Root Module, so we
                // surface a partial-success payload rather than throwing away the creation.
                JObject placementMeta = null;
                if (requestedPlacement != null)
                {
                    string moveJson = MoveObject(name, requestedPlacement, typeFilter: type, destKind: requestedPlacementKind);
                    JObject moveResp = null;
                    try { moveResp = JObject.Parse(moveJson); } catch { }
                    bool moveOk = string.Equals(moveResp?["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
                    placementMeta = new JObject
                    {
                        ["requested"] = requestedPlacement,
                        ["kind"] = requestedPlacementKind,
                        ["moved"] = moveOk,
                        ["detail"] = moveResp ?? (JToken)moveJson
                    };
                    if (!moveOk)
                        placementMeta["note"] = "Object created in Root Module but the move into '" + requestedPlacement +
                            "' did not complete. See detail; move it with genexus_properties action=move, or in the IDE.";
                }

                string idStr = "";
                try { idStr = newObj.Key?.Id.ToString() ?? ""; } catch { try { idStr = newObj.Guid.ToString(); } catch { } }
                var responsePayload = new JObject
                {
                    ["type"] = type,
                    ["name"] = name,
                    ["id"] = idStr
                };
                if (placementMeta != null) responsePayload["placement"] = placementMeta;
                JObject metaObj = null;
                if (domainMeta != null)
                {
                    metaObj = domainMeta;
                }
                if (!string.IsNullOrEmpty(seededDescription))
                {
                    // Surface the auto-seeded payload so the agent knows the object isn't empty
                    // before calling read/edit. Agents that immediately overwrite Structure
                    // need this signal to avoid being surprised by the seed item appearing in
                    // round-trip reads.
                    metaObj = new JObject
                    {
                        ["seeded"] = new JArray { seededDescription },
                        ["seededHint"] = "An initial item was auto-added so the SDK accepts the empty Save. Overwrite via genexus_edit part=Structure (full mode) to replace it."
                    };
                }
                // WebPanel / SDPanel get a hint pointing at apply_pattern. Agents asked for
                // "a WebPanel with WorkWithPlus" otherwise tend to hand-build the layout via
                // WebForm edits, which compiles but produces a page with none of WWP's
                // grid/filter/action infrastructure. The hint short-circuits that drift.
                bool isWebPanel = type.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("SDPanel", StringComparison.OrdinalIgnoreCase);
                if (isWebPanel)
                {
                    if (metaObj == null) metaObj = new JObject();
                    metaObj["patternHint"] =
                        "Empty " + type + " created. Two real paths to WorkWithPlus: " +
                        "(A) Apply WWP directly on this " + type + " — call genexus_apply_pattern name=" + name +
                        " pattern=WorkWithPlus settings={template:'<TemplateName>'} to attach a 'WorkWithPlus" + name +
                        "' host. The MCP runs the SDK's IPatternBuildProcess.UpdateParentObject so the projection " +
                        "lands on this " + type + "'s WebForm immediately. Subsequent genexus_edit on the host's " +
                        "PatternInstance auto-projects too. " +
                        "(B) Apply WWP to a Transaction — generates the full 'WW<Trn>' family (Selection list + " +
                        "View detail + exports). Pick (A) for custom WebPanel-based screens (queries, dashboards), " +
                        "(B) for CRUD-around-a-Transaction. " +
                        "Available templates: list via `genexus_list_objects typeFilter=\"WorkWithPlus for Web Template\"` " +
                        "(common: MatIsoTemplate, TransactionResp2, PopoverEmpty).";
                    metaObj["nextStep"] = new JObject
                    {
                        ["forWwpOnThisWebPanel"] = new JObject
                        {
                            ["tool"] = "genexus_apply_pattern",
                            ["arguments"] = new JObject
                            {
                                ["name"] = name,
                                ["pattern"] = "WorkWithPlus",
                                ["settings"] = new JObject { ["template"] = "<TemplateName>" }
                            }
                        },
                        ["forWwpFromTransaction"] = new JObject
                        {
                            ["step1"] = new JObject { ["tool"] = "genexus_create_object", ["arguments"] = new JObject { ["type"] = "Transaction", ["name"] = "<TrnName>" } },
                            ["step2"] = new JObject { ["tool"] = "genexus_apply_pattern", ["arguments"] = new JObject { ["name"] = "<TrnName>", ["pattern"] = "WorkWithPlus" } },
                            ["step3"] = new JObject { ["tool"] = "genexus_edit", ["arguments"] = new JObject { ["name"] = "WorkWithPlus<TrnName>", ["part"] = "PatternInstance" } }
                        }
                    };
                }
                if (metaObj != null) responsePayload["_meta"] = metaObj;
                return McpResponse.Ok(target: name, code: "ObjectCreated", result: responsePayload);
            }
            catch (Exception ex)
            {
                Logger.Error("CreateObject failed: " + ex.Message);
                return McpResponse.Err(code: "CreateObjectFailed", message: ex.Message);
            }
        }

        public KBObject CreateObjectInstance(string type, string name, JObject options, out string seededDescription, out JObject domainMeta)
        {
            seededDescription = null;
            domainMeta = null;

            var kb = _kbService.GetKB();
            if (kb == null) throw new Exception("KB not opened");

            var typeGuid = ResolveObjectTypeGuid(type);
            if (typeGuid == Guid.Empty)
                throw new ArgumentException($"Unknown object type: {type}");

            KBObject newObj = KBObject.Create(kb.DesignModel, typeGuid);
            newObj.Name = name;
            if (newObj is Artech.Architecture.Common.Objects.Module)
                newObj.Module = kb.DesignModel.RootModule;

            // Initialize with some default content if possible
            if (newObj.GetType().Name == "Procedure")
            {
                var partProp = newObj.GetType().GetProperty("ProcedurePart");
                if (partProp != null) {
                    object part = partProp.GetValue(newObj);
                    if (part != null) {
                        var sourceProp = part.GetType().GetProperty("Source");
                        if (sourceProp != null) sourceProp.SetValue(part, "// Procedure: " + name + "\n\n");
                    }
                }
            }
            else if (newObj.GetType().Name == "DataProvider")
            {
                var partsProp = newObj.GetType().GetProperty("Parts");
                if (partsProp != null) {
                    var parts = (System.Collections.IEnumerable)partsProp.GetValue(newObj);
                    foreach (object p in parts)
                    {
                        if (p.GetType().Name == "SourcePart")
                        {
                            var sourceProp = p.GetType().GetProperty("Source");
                            if (sourceProp != null) sourceProp.SetValue(p, "// Data Provider: " + name + "\n\n");
                            break;
                        }
                    }
                }
            }

            if (type.Equals("SDT", StringComparison.OrdinalIgnoreCase) || type.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase))
            {
                string firstItem = options?["firstItem"]?.ToString();
                string firstItemType = options?["firstItemType"]?.ToString();
                if (string.IsNullOrWhiteSpace(firstItem)) firstItem = "Item1";
                if (string.IsNullOrWhiteSpace(firstItemType)) firstItemType = "VARCHAR";
                string seededType = InitializeSDTWithDefaultItem(newObj, name, firstItem, firstItemType);
                seededDescription = firstItem + " : " + (seededType ?? firstItemType);
            }
            else if (newObj is Artech.Genexus.Common.Objects.Transaction newTrn)
            {
                InitializeTransactionWithDefaultKey(newTrn, name);
                seededDescription = name + "Id : Numeric(8,0) [Key]";
            }
            else if (type.Equals("Domain", StringComparison.OrdinalIgnoreCase))
            {
                domainMeta = InitializeDomain(newObj, name, options, kb);
            }

            NormalizeIntegratedSecurityLevel(newObj);
            return newObj;
        }

        // IntegratedSecurityLevel is a Combo property whose stored value is one of the ids
        // { SecurityNone, SecurityLow, SecurityHigh } (displayed as None / Authentication /
        // Authorization). A valid object exposes either the id or its desc; anything else —
        // notably the unresolved/stale value a raw KBObject.Create leaves behind, which the
        // IDE renders as "(Unknown)" — is invalid and must be normalized to None.
        private static readonly string[] _validSecurityLevels =
        {
            "SecurityNone", "SecurityLow", "SecurityHigh",   // combo ids
            "None", "Authentication", "Authorization"        // combo descs
        };

        private static bool IsValidSecurityLevel(string value)
            => !string.IsNullOrEmpty(value) &&
               Array.Exists(_validSecurityLevels, v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

        // Set IntegratedSecurityLevel to None when a freshly-created object left it at an
        // invalid value ("Unknown"). Best-effort and non-throwing: objects without the
        // property (Domain, SDT, Theme, …) are simply skipped. The Combo accepts the id
        // ("SecurityNone"), not the desc ("None"), so we try the id first and verify via
        // read-back — keeping whichever representation the SDK actually applies.
        private static void NormalizeIntegratedSecurityLevel(KBObject obj)
        {
            if (obj == null) return;
            try
            {
                dynamic props = obj.Properties;
                if (props == null) return;

                dynamic prop = null;
                try { prop = props["IntegratedSecurityLevel"]; } catch { }
                if (prop == null) return;

                string current = ReadPropertyValue(prop);
                if (IsValidSecurityLevel(current)) return; // already None/Authentication/Authorization

                // Try the combo id first, then the desc, verifying each via read-back.
                foreach (var candidate in new[] { "SecurityNone", "None" })
                {
                    TrySetPropertyString(obj, "IntegratedSecurityLevel", candidate);
                    if (IsValidSecurityLevel(ReadPropertyValue(prop)))
                    {
                        Logger.Debug("[CREATE] Normalized IntegratedSecurityLevel '" + (current ?? "<null>") + "' -> None (via '" + candidate + "') on " + obj.Name);
                        return;
                    }
                }
                Logger.Debug("[CREATE] Could not normalize IntegratedSecurityLevel (value '" + (current ?? "<null>") + "') on " + obj.Name);
            }
            catch (Exception ex) { Logger.Debug("[CREATE] NormalizeIntegratedSecurityLevel skipped: " + ex.Message); }
        }

        private static string ReadPropertyValue(dynamic prop)
        {
            try { return prop.Value?.ToString(); } catch { return null; }
        }

        private static void TrySetPropertyString(KBObject obj, string propName, string value)
        {
            try
            {
                var t = (Type)((object)obj).GetType();
                var mi = t.GetMethod("SetPropertyValueString",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(string) }, null);
                if (mi != null)
                    mi.Invoke((object)obj, new object[] { propName, value });
                else
                    obj.SetPropertyValue(propName, value);
            }
            catch (Exception ex) { Logger.Debug("[CREATE] TrySetPropertyString(" + propName + "='" + value + "') failed: " + ex.Message); }
        }

        // Item 32: objectFilter added. sinceMode now also accepts an ISO-8601 timestamp;
        // the legacy 'crash' sentinel is still honoured for backward compatibility.
        // logPathOverride: test-only seam so unit tests can inject a temp file path.
        public string ReadLogs(int lines, string filterCorrelation, string grepPattern, string sinceMode = null, string objectFilter = null, string logPathOverride = null)
        {
            try
            {
                if (lines <= 0) lines = 100;
                if (lines > 2000) lines = 2000;

                // issue #40: read from the same resolved dir the Logger writes to
                // (may be relocated out of node_modules).
                string logPath = !string.IsNullOrEmpty(logPathOverride)
                    ? logPathOverride
                    : Path.Combine(GxMcp.Worker.Helpers.Logger.LogDirectory, "worker_debug.log");
                if (!File.Exists(logPath))
                {
                    return "{\"status\":\"Error\", \"error\":\"Log file not found at " + CommandDispatcher.EscapeJsonString(logPath) + "\"}";
                }

                // Stream-read tail
                var allLines = new List<string>();
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string ln;
                    while ((ln = sr.ReadLine()) != null) allLines.Add(ln);
                }

                IEnumerable<string> filtered = allLines;

                // v2.6.8: since=crash slices the log starting at the most recent
                // [ERROR]/[CRITICAL] line — the agent (and the user reporting a
                // crash) gets the stack + immediate context without having to
                // hunt for it manually.
                bool sliceFromCrash = string.Equals(sinceMode, "crash", StringComparison.OrdinalIgnoreCase);
                int crashIndex = -1;
                if (sliceFromCrash)
                {
                    for (int i = allLines.Count - 1; i >= 0; i--)
                    {
                        string ln = allLines[i];
                        if (_crashLinePattern.IsMatch(ln))
                        {
                            crashIndex = i;
                            break;
                        }
                    }
                    if (crashIndex >= 0)
                    {
                        // Take 5 lines of context before + everything after.
                        int start = Math.Max(0, crashIndex - 5);
                        filtered = allLines.Skip(start);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(sinceMode))
                {
                    // Item 32: since=<ISO timestamp> — skip lines whose leading timestamp
                    // is before the requested cutoff. Log format: [yyyy-MM-dd HH:mm:ss.fff]
                    // Lines that don't carry a parseable timestamp are kept (defensive).
                    if (DateTime.TryParse(sinceMode, null, System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out DateTime sinceDt))
                    {
                        // Normalize to UTC so a client-supplied "...Z" timestamp compares correctly
                        // against log-line timestamps (the worker writes them in local time).
                        DateTime sinceUtc = sinceDt.Kind == DateTimeKind.Utc ? sinceDt : sinceDt.ToUniversalTime();
                        filtered = allLines.Where(l =>
                        {
                            // Try to parse the leading [yyyy-MM-dd HH:mm:ss.fff] prefix.
                            if (l.Length > 2 && l[0] == '[')
                            {
                                int close = l.IndexOf(']');
                                if (close > 0)
                                {
                                    string ts = l.Substring(1, close - 1);
                                    if (DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.AssumeLocal, out DateTime lineTs))
                                        return lineTs.ToUniversalTime() >= sinceUtc;
                                }
                            }
                            return true; // unparseable timestamp — keep line
                        });
                    }
                }

                // Item 32: object-name filter — only lines mentioning the object.
                if (!string.IsNullOrWhiteSpace(objectFilter))
                    filtered = filtered.Where(l => l.IndexOf(objectFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!string.IsNullOrWhiteSpace(filterCorrelation))
                    filtered = filtered.Where(l => l.IndexOf(filterCorrelation, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(grepPattern))
                {
                    try
                    {
                        var rx = new System.Text.RegularExpressions.Regex(grepPattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                            GxMcp.Worker.Services.SourceSearchService.RegexMatchTimeout);
                        // Materialize INSIDE the try: Where() is deferred, so a match-timeout
                        // thrown by rx.IsMatch would otherwise surface at the later ToList()
                        // (outside this catch) as a generic error instead of the fallback.
                        filtered = filtered.Where(l => rx.IsMatch(l)).ToList();
                    }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                    {
                        // A valid-but-pathological pattern must not hang the STA thread; degrade
                        // to the same substring fallback an invalid pattern gets.
                        filtered = filtered.Where(l => l.IndexOf(grepPattern, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    catch { /* invalid regex falls back to substring */ filtered = filtered.Where(l => l.IndexOf(grepPattern, StringComparison.OrdinalIgnoreCase) >= 0); }
                }

                var matchList = filtered.ToList();
                int skip = Math.Max(0, matchList.Count - lines);
                var tail = matchList.Skip(skip).ToList();
                var result = new JObject
                {
                    // Item 32: surface the log path so the agent can read adjacent logs
                    // (gateway_debug.log, probe.log, etc.) directly via genexus_asset.
                    ["logPath"] = logPath,
                    // Back-compat alias: prior shape exposed the file location as "path".
                    ["path"] = logPath,
                    ["logDir"] = GxMcp.Worker.Helpers.Logger.LogDirectory,
                    ["totalLines"] = allLines.Count,
                    ["matched"] = tail.Count,
                    ["lines"] = string.Join("\n", tail)
                };
                if (sliceFromCrash)
                {
                    result["sinceMode"] = "crash";
                    result["crashLineIndex"] = crashIndex;
                    if (crashIndex < 0)
                    {
                        result["hint"] = "No ERROR/CRITICAL markers found in the log — worker has not crashed (or the log has rotated).";
                    }
                }
                return McpResponse.Ok(code: "LogsRead", result: result);
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\", \"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string WorkerReload(string sourceDir)
        {
            try
            {
                string currentExe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(currentExe)) currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                string publishDir = Path.GetDirectoryName(currentExe) ?? "";
                int currentPid = Process.GetCurrentProcess().Id;

                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    return "{\"status\":\"Error\", \"error\":\"sourceDir must point to a directory with the new worker binaries (typically src/GxMcp.Worker/bin/Release).\"}";
                }

                // Spawn a detached PowerShell helper that:
                //   1) waits for THIS worker pid to exit (releases the .exe lock)
                //   2) copies sourceDir/* → publishDir/* with retries (the gateway can
                //      respawn the worker faster than we copy, re-locking the .exe — we
                //      then kill the respawned worker so the next gateway respawn picks
                //      up the new bits)
                //   3) writes worker_reload.last_result.json next to publishDir so
                //      callers can diagnose silent failures
                //
                // Previous version used `-ErrorAction SilentlyContinue` on a single
                // Copy-Item, which masked the lock race entirely — the reload returned
                // Success while the binary on disk was unchanged.
                // SECURITY: never interpolate sourceDir/publishDir into the PowerShell
                // command string — sourceDir is a raw tool argument and a crafted value
                // (e.g. containing a double-quote) could break out of the -Command quoting
                // and inject script. Pass both paths via process environment variables
                // instead (not shell-parsed); the script reads them with $env:.
                string ps =
                    "$pid_target=" + currentPid + "; " +
                    "$src=$env:GXMCP_RELOAD_SRC; $dst=$env:GXMCP_RELOAD_DST; " +
                    "$log = Join-Path $dst 'worker_reload.last_result.json'; " +
                    "function Write-Status($status, $detail) { " +
                    "  @{ status = $status; detail = $detail; timestamp = (Get-Date).ToString('o'); src = $src; dst = $dst } | " +
                    "  ConvertTo-Json | Set-Content -Path $log -Encoding utf8 -ErrorAction SilentlyContinue " +
                    "} " +
                    "try { Wait-Process -Id $pid_target -Timeout 30 -ErrorAction SilentlyContinue } catch {} " +
                    "$copied=$false; $lastErr=''; " +
                    "for ($i=0; $i -lt 20 -and -not $copied; $i++) { " +
                    "  try { " +
                    "    Copy-Item \\\"$src\\*\\\" \\\"$dst\\\" -Recurse -Force -ErrorAction Stop; " +
                    "    $copied=$true " +
                    "  } catch { " +
                    "    $lastErr = $_.Exception.Message; " +
                    "    $w = Get-Process -Name GxMcp.Worker -ErrorAction SilentlyContinue; " +
                    "    if ($w) { try { $w | Stop-Process -Force -ErrorAction SilentlyContinue } catch {} } " +
                    "    Start-Sleep -Milliseconds 500 " +
                    "  } " +
                    "} " +
                    "if ($copied) { " +
                    "  $w = Get-Process -Name GxMcp.Worker -ErrorAction SilentlyContinue; " +
                    "  if ($w) { try { $w | Stop-Process -Force -ErrorAction SilentlyContinue } catch {} } " +
                    "  Write-Status 'Success' 'Binaries copied; respawned worker (if any) killed so gateway brings up a fresh one with new bits.' " +
                    "} else { " +
                    "  Write-Status 'Error' \\\"Copy failed after retries. Last error: $lastErr\\\" " +
                    "}; ";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // Paths flow to the helper as environment variables, never interpolated
                // into the command line — see the security note above.
                psi.EnvironmentVariables["GXMCP_RELOAD_SRC"] = sourceDir;
                psi.EnvironmentVariables["GXMCP_RELOAD_DST"] = publishDir;
                Process.Start(psi);

                Logger.Info("WorkerReload: helper spawned (will copy " + sourceDir + " -> " + publishDir + " after exit). Exiting in 1s.");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1000);
                    Logger.Info("WorkerReload: exiting now for respawn.");
                    Environment.Exit(0);
                });
                return McpResponse.Accepted(
                    target: null,
                    operationId: null,
                    extra: new JObject
                    {
                        ["sourceDir"] = sourceDir,
                        ["publishDir"] = publishDir,
                        ["note"] = "Worker exits in 1s; detached helper copies binaries with retries and kills any worker that respawned mid-copy so the gateway brings up a fresh one. Inspect '" + System.IO.Path.Combine(publishDir, "worker_reload.last_result.json") + "' for the copy outcome."
                    });
            }
            catch (Exception ex)
            {
                Logger.Error("WorkerReload failed: " + ex.Message);
                return McpResponse.Err(code: "WorkerReloadFailed", message: ex.Message);
            }
        }

        public string DeleteObject(string target, string typeFilter, bool confirm, bool dryRun = false, string expectedVersion = null)
        {
            Guid objGuid = Guid.Empty;
            string objName = target;
            string objType = typeFilter;
            bool deleteStarted = false;
            bool transactionCommitted = false;
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null)
                    return Models.McpResponse.Err(code: "KbNotOpen", message: "No KB open.",
                        hint: "Open a KB first with genexus_kb action=open.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_kb", new JObject { ["action"] = "open" }, "Open the target KB before deleting.")));

                if (!dryRun && !confirm)
                {
                    return Models.McpResponse.Err(code: "ConfirmRequired",
                        message: "Delete requires explicit confirm=true (irreversible operation).",
                        hint: "Re-issue genexus_delete_object with confirm=true. Consider dryRun=true first to preview the impact.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_delete_object", new JObject { ["name"] = target, ["confirm"] = true }, "Re-issue with confirm=true to perform the irreversible delete.")),
                        target: target);
                }

                var obj = FindObject(target, typeFilter);
                if (obj == null)
                {
                    // A prior delete_object call may have timed out at the gateway pipe
                    // while the worker's obj.Delete() completed successfully a few seconds
                    // later. The retry now reaches a KB where the object is genuinely gone
                    // — without this probe we'd return a misleading "Object not found".
                    // Match any recently-deleted entry whose name equals the target
                    // (type may be absent on the retry call).
                    var nowUtc = DateTime.UtcNow;
                    foreach (var kv in _recentDeletions)
                    {
                        if (nowUtc - kv.Value > RecentDeletionTtl)
                        {
                            _recentDeletions.TryRemove(kv.Key, out _);
                            continue;
                        }
                        int colon = kv.Key.IndexOf(':');
                        if (colon < 0) continue;
                        string cachedType = kv.Key.Substring(0, colon);
                        string cachedName = kv.Key.Substring(colon + 1);
                        if (!string.Equals(cachedName, target, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrEmpty(typeFilter) && !string.Equals(cachedType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

                        var deletedAt = CommandDispatcher.EscapeJsonString(kv.Value.ToString("o"));
                        Logger.Info(string.Format("DeleteObject: {0} ({1}) already removed at {2} — confirming via recent-deletion cache.", cachedName, cachedType, deletedAt));
                        return McpResponse.Ok(target: cachedName, code: "ObjectDeleted", result: new JObject
                        {
                            ["deleted"] = cachedName,
                            ["type"] = cachedType,
                            ["confirmedAfterTimeout"] = true,
                            ["deletedAtUtc"] = deletedAt,
                            ["note"] = "Object was already deleted in a prior call (likely one whose response timed out at the client). No action taken on this retry."
                        });
                    }
                    return HealingService.FormatNotFoundError(target, GetLoadedIndexOrNull());
                }

                objName = obj.Name;
                objType = obj.TypeDescriptor?.Name ?? "Unknown";
                objGuid = obj.Guid;
                string qualifiedName = GetQualifiedObjectName(obj);
                string versionBefore = WriteService.ComputeVersionToken(obj);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, versionBefore, StringComparison.Ordinal))
                {
                    return McpResponse.Err(
                        code: "VersionConflict",
                        message: "The object changed after the supplied expectedVersion was read.",
                        hint: "Run genexus_delete_object with dryRun=true again and retry with its versionBefore token.",
                        target: objName,
                        extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = versionBefore,
                            ["persisted"] = false
                        });
                }

                bool referencesTruncated;
                JArray references = CollectIncomingReferences(obj, kb, out referencesTruncated);

                // dryRun: return what would be deleted without mutating the KB.
                if (dryRun)
                {
                    var rereadResult = ResolveByNativeIdentity(kb, objGuid, objType);
                    if (rereadResult.Status == NativeResolutionStatus.Failed)
                        return McpResponse.Err(code: "ObjectResolutionFailed",
                            message: "The SDK could not re-resolve the object for the dry-run verification: " + rereadResult.Error?.Message,
                            hint: "No mutation was attempted; retry after the KB/SDK becomes readable.", target: objName,
                            extra: new JObject { ["persisted"] = false, ["verificationSucceeded"] = false });
                    var reread = rereadResult.Object;
                    if (rereadResult.Status == NativeResolutionStatus.Absent)
                        return McpResponse.Err(
                            code: "ObjectChanged",
                            message: "The object disappeared before the dry-run verification completed.",
                            hint: "Re-read the Knowledge Base before retrying.",
                            target: objName,
                            extra: new JObject { ["persisted"] = false, ["verificationSucceeded"] = false });
                    string versionAfter = WriteService.ComputeVersionToken(reread);
                    bool unchanged = reread != null
                        && string.Equals(versionBefore, versionAfter, StringComparison.Ordinal);
                    return McpResponse.Ok(
                        target: objName,
                        code: "DryRun",
                        result: new JObject
                        {
                            ["resolvedType"] = objType,
                            ["resolvedGuid"] = objGuid.ToString(),
                            ["qualifiedName"] = qualifiedName,
                            ["references"] = references,
                            ["referencesTruncated"] = referencesTruncated,
                            ["referenceLimit"] = MaxIncomingReferences,
                            ["wouldDelete"] = true,
                            ["persisted"] = false,
                            ["mutationDetected"] = !unchanged,
                            ["versionBefore"] = versionBefore,
                            ["versionAfter"] = versionAfter,
                            ["rereadConfirmed"] = unchanged,
                            ["implicitLifecycleActions"] = new JArray(),
                            ["preview"] = new JObject
                            {
                                ["wouldDelete"] = new JObject
                                {
                                    ["name"] = objName,
                                    ["type"] = objType,
                                    ["guid"] = objGuid.ToString()
                                }
                            }
                        });
                }

                Logger.Info(string.Format("Deleting Object: {0} ({1}, guid={2})", objName, objType, objGuid));

                using (var tx = kb.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        var currentResult = ResolveByNativeIdentity(kb, objGuid, objType);
                        if (currentResult.Status == NativeResolutionStatus.Failed)
                        {
                            tx.Rollback();
                            return McpResponse.Err(
                                code: "ObjectResolutionFailed",
                                message: "The SDK could not resolve the target inside the deletion transaction: " + currentResult.Error?.Message,
                                hint: "The transaction was rolled back; retry after the KB/SDK becomes readable.",
                                target: objName,
                                extra: new JObject { ["persisted"] = false, ["verificationSucceeded"] = false });
                        }
                        var current = currentResult.Object;
                        if (currentResult.Status == NativeResolutionStatus.Absent)
                        {
                            tx.Rollback();
                            return McpResponse.Err(
                                code: "VersionConflict",
                                message: "The object no longer exists at deletion time.",
                                hint: "Re-read the Knowledge Base before retrying.",
                                target: objName,
                                extra: new JObject { ["persisted"] = false });
                        }

                        string transactionVersion = WriteService.ComputeVersionToken(current);
                        if (!string.IsNullOrWhiteSpace(expectedVersion)
                            && !string.Equals(expectedVersion, transactionVersion, StringComparison.Ordinal))
                        {
                            tx.Rollback();
                            return McpResponse.Err(
                                code: "VersionConflict",
                                message: "The object changed before the deletion transaction started.",
                                hint: "Run genexus_delete_object with dryRun=true again and retry with its versionBefore token.",
                                target: objName,
                                extra: new JObject
                                {
                                    ["expectedVersion"] = expectedVersion,
                                    ["currentVersion"] = transactionVersion,
                                    ["persisted"] = false
                                });
                        }

                        references = CollectIncomingReferences(current, kb, out referencesTruncated);
                        deleteStarted = true;
                        current.Delete();

                        var afterDelete = ResolveByNativeIdentity(kb, objGuid, objType);
                        if (afterDelete.Status == NativeResolutionStatus.Failed)
                        {
                            tx.Rollback();
                            return McpResponse.Err(
                                code: "ObjectResolutionFailed",
                                message: "The SDK could not verify Delete() inside the transaction: " + afterDelete.Error?.Message,
                                hint: "The transaction was rolled back; retry after the KB/SDK becomes readable.",
                                target: objName,
                                extra: new JObject { ["persisted"] = false, ["verificationSucceeded"] = false });
                        }
                        if (afterDelete.Status == NativeResolutionStatus.Found)
                        {
                            tx.Rollback();
                            return McpResponse.Err(
                                code: "DeleteNotPersisted",
                                message: "The SDK still resolves the object after Delete().",
                                hint: "The transaction was rolled back; inspect SDK locks or concurrent edits before retrying.",
                                target: objName,
                                extra: new JObject
                                {
                                    ["persisted"] = false,
                                    ["rollback"] = new JObject { ["attempted"] = true, ["verified"] = true }
                                });
                        }

                        tx.Commit();
                        committed = true;
                        transactionCommitted = true;
                    }
                    finally
                    {
                        if (!committed) try { tx.Rollback(); } catch { }
                    }
                }

                var afterCommit = ResolveByNativeIdentity(kb, objGuid, objType);
                if (afterCommit.Status == NativeResolutionStatus.Failed)
                {
                    return McpResponse.Err(
                        code: "DeleteVerificationUnavailable",
                        message: "The deletion transaction committed, but the SDK could not verify the original object identity: " + afterCommit.Error?.Message,
                        hint: "Do not retry blindly; re-read the KB after the SDK is healthy.",
                        target: objName,
                        extra: new JObject
                        {
                            ["commitSucceeded"] = true,
                            ["verificationSucceeded"] = false,
                            ["persisted"] = JValue.CreateNull()
                        });
                }
                if (afterCommit.Status == NativeResolutionStatus.Found)
                {
                    return McpResponse.Err(
                        code: "DeleteNotPersisted",
                        message: "The object was still resolvable after the deletion transaction committed.",
                        hint: "The SDK did not persist the deletion; inspect concurrent edits or repository locks before retrying.",
                        target: objName,
                        extra: new JObject
                        {
                            ["commitSucceeded"] = true,
                            ["verificationSucceeded"] = true,
                            ["persisted"] = false,
                            ["rereadConfirmed"] = false
                        });
                }

                Logger.Info(string.Format("Object deleted: {0} ({1})", objName, objType));

                // Record the deletion before any further work — even if the index
                // RemoveEntry below throws, the cache still lets a follow-up retry
                // be answered correctly.
                _recentDeletions[RecentDeletionKey(objType, objName)] = DateTime.UtcNow;

                // Keep the search index honest: without this, list_objects keeps returning
                // the deleted object for several minutes until a full reindex. Same
                // mechanism CreateObject uses, but in reverse.
                try
                {
                    var idx = _kbService?.GetIndexCache();
                    if (idx != null) idx.RemoveEntryByGuid(objGuid.ToString());
                }
                catch (Exception ex) { Logger.Error("DeleteObject: index RemoveEntry failed for " + objName + ": " + ex.Message); }

                return McpResponse.Ok(target: objName, code: "ObjectDeleted", result: new JObject
                {
                    ["deleted"] = objName,
                    ["type"] = objType,
                    ["resolvedType"] = objType,
                    ["resolvedGuid"] = objGuid.ToString(),
                    ["qualifiedName"] = qualifiedName,
                    ["references"] = references,
                    ["referencesTruncated"] = referencesTruncated,
                    ["referenceLimit"] = MaxIncomingReferences,
                    ["wouldDelete"] = true,
                    ["persisted"] = true,
                    ["rereadConfirmed"] = true,
                    ["versionBefore"] = versionBefore,
                    ["versionAfter"] = JValue.CreateNull(),
                    ["implicitLifecycleActions"] = new JArray()
                });
            }
            catch (Exception ex)
            {
                Logger.Error("DeleteObject failed: " + ex.Message);
                bool stillExists = true;
                try
                {
                    var kb = _kbService.GetKB();
                    if (objGuid == Guid.Empty) stillExists = true;
                    else
                    {
                        var resolution = ResolveByNativeIdentity(kb, objGuid, objType);
                        stillExists = resolution.Status == NativeResolutionStatus.Found;
                        if (resolution.Status == NativeResolutionStatus.Failed) stillExists = true;
                    }
                }
                catch { stillExists = true; }
                return McpResponse.Err(
                    code: "DeleteFailed",
                    message: ex.InnerException?.Message ?? ex.Message,
                    hint: stillExists
                        ? "The object remains in the Knowledge Base; resolve the reported SDK error before retrying."
                        : "The object is absent despite the error; re-read the Knowledge Base before taking further action.",
                    target: objName,
                    extra: new JObject
                    {
                        ["persisted"] = objGuid == Guid.Empty ? false : (stillExists ? false : JValue.CreateNull()),
                        ["verificationSucceeded"] = objGuid != Guid.Empty && !stillExists,
                        ["rollback"] = new JObject
                        {
                            ["attempted"] = deleteStarted && !transactionCommitted,
                            ["verified"] = stillExists
                        },
                        ["implicitLifecycleActions"] = new JArray()
                    });
            }
        }

        private static string GetQualifiedObjectName(KBObject obj)
        {
            if (obj == null) return null;
            try
            {
                dynamic value = obj;
                string qualified = value.QualifiedName?.ToString();
                if (!string.IsNullOrWhiteSpace(qualified)) return qualified;
            }
            catch { }
            return obj.Name;
        }

        private static NativeResolution ResolveByNativeIdentity(KnowledgeBase kb, Guid guid, string typeName)
        {
            if (kb == null || guid == Guid.Empty)
                return new NativeResolution { Status = NativeResolutionStatus.Absent };
            try
            {
                KBObject result;
                if (string.Equals(typeName, "Domain", StringComparison.OrdinalIgnoreCase))
                    result = Artech.Genexus.Common.Objects.Domain.Get(kb.DesignModel, guid);
                else
                    result = kb.DesignModel.Objects.Get(guid);
                return new NativeResolution
                {
                    Status = result == null ? NativeResolutionStatus.Absent : NativeResolutionStatus.Found,
                    Object = result
                };
            }
            catch (Exception ex)
            {
                Logger.Error("Native object resolution failed for " + guid + ": " + ex.Message);
                return new NativeResolution { Status = NativeResolutionStatus.Failed, Error = ex };
            }
        }

        private static JArray CollectIncomingReferences(KBObject obj, KnowledgeBase kb, out bool truncated)
        {
            var result = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            truncated = false;
            int inspected = 0;
            foreach (var reference in obj.GetReferencesTo())
            {
                if (++inspected > MaxIncomingReferences)
                {
                    truncated = true;
                    break;
                }
                string key = null;
                try { key = reference.From?.ToString(); } catch { }
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;

                KBObject source = null;
                try { source = kb.DesignModel.Objects.Get(reference.From); } catch { }
                var item = new JObject { ["nativeKey"] = key };
                if (source != null)
                {
                    item["name"] = source.Name;
                    item["type"] = source.TypeDescriptor?.Name ?? "Unknown";
                    item["guid"] = source.Guid.ToString();
                    item["qualifiedName"] = GetQualifiedObjectName(source);
                }
                else
                {
                    item["resolved"] = false;
                }
                result.Add(item);
            }
            return result;
        }

        // Mirrors the SDT init: a freshly created Transaction with zero attributes fails the
        // SDK validation on Save. We seed it with a Numeric(4) key attribute named
        // "<TrnName>Id" — same convention the GeneXus IDE uses when you create a new Trn.
        private static void InitializeTransactionWithDefaultKey(Artech.Genexus.Common.Objects.Transaction trn, string trnName)
        {
            try
            {
                dynamic root = null;
                try { root = trn.Structure?.Root; } catch { }
                if (root == null) { Logger.Error("InitializeTransactionWithDefaultKey: Structure.Root null for " + trnName); return; }

                // If, somehow, attributes already exist, leave the Trn alone.
                try { foreach (var _ in root.Attributes) return; } catch { }

                string keyName = trnName + "Id";

                // Reuse an existing global Attribute with the conventional "<TrnName>Id" name;
                // otherwise create one (Numeric(4)) — same convention the GeneXus IDE uses.
                Artech.Genexus.Common.Objects.Attribute globalAttr = null;
                try { globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(trn.Model, keyName); }
                catch (Exception ex) { Logger.Error("InitializeTransactionWithDefaultKey: global attr lookup failed: " + ex.Message); }

                if (globalAttr == null)
                {
                    try
                    {
                        var attrGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                        var newAttr = KBObject.Create(trn.Model, attrGuid);
                        newAttr.Name = keyName;
                        globalAttr = newAttr as Artech.Genexus.Common.Objects.Attribute;
                        if (globalAttr != null)
                        {
                            try { globalAttr.Type = Artech.Genexus.Common.eDBType.NUMERIC; } catch { }
                            try { globalAttr.Length = 4; } catch { }
                            try { globalAttr.Decimals = 0; } catch { }
                        }
                        newAttr.Save();
                        if (globalAttr == null) globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(trn.Model, keyName);
                        Logger.Info("InitializeTransactionWithDefaultKey: created global Attribute " + keyName);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("InitializeTransactionWithDefaultKey: global Attribute creation failed: " + (ex.InnerException?.Message ?? ex.Message));
                        return;
                    }
                }

                if (globalAttr == null)
                {
                    Logger.Error("InitializeTransactionWithDefaultKey: global Attribute still null for " + keyName);
                    return;
                }

                // TransactionLevel exposes a typed AddAttribute(Attribute) that returns the wrapped
                // TransactionAttribute. Use it directly via dynamic dispatch (root is dynamic).
                try
                {
                    var trnAttr = root.AddAttribute(globalAttr);
                    try { trnAttr.IsKey = true; } catch { }
                    Logger.Info("InitializeTransactionWithDefaultKey: added key '" + keyName + "' to " + trnName);
                }
                catch (Exception ex)
                {
                    Logger.Error("InitializeTransactionWithDefaultKey: AddAttribute failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("InitializeTransactionWithDefaultKey failed: " + ex.Message);
            }
        }

        private static readonly Guid SDT_STRUCTURE_PART_GUID = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        // Locate an SDT's structure part (SDTStructurePart) by GUID / descriptor / class name.
        private static KBObjectPart FindSdtStructurePartOf(KBObject sdt)
        {
            if (sdt == null) return null;
            foreach (KBObjectPart p in sdt.Parts)
            {
                try
                {
                    if (p.Type == SDT_STRUCTURE_PART_GUID) return p;
                    string descName = p.TypeDescriptor?.Name ?? "";
                    string className = p.GetType().Name;
                    if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                        return p;
                }
                catch { }
            }
            return null;
        }

        // issue #51: an SDT cloned through the textual structure DSL (SdkObjectCloner's default
        // per-part copy) loses the root Collection flag, the collection item name, and any
        // Domain/SDT-typed member — the flat "Name : TYPE" text projection encodes none of them,
        // so a collection SDT round-tripped as a flat, non-collection SDT. SerializeToXml /
        // DeserializeFromXml on the SDTStructure part only round-trips the <Properties> bag (not
        // the structure items, same as pattern parts — measured), so instead copy the structure
        // at the object-model level: replicate the root Collection metadata + every item / level
        // (including its type, length/decimals, collection flag, and ATTCUSTOMTYPE reference for
        // Domain/SDT-typed members). Returns a McpResponse JSON envelope on success, or null when
        // the native path isn't applicable / copied nothing (caller falls back to the text path).
        public string CloneSdtStructurePart(string sourceName, string targetName)
        {
            try
            {
                var srcObj = FindObject(sourceName);
                var tgtObj = FindObject(targetName);
                if (srcObj == null || tgtObj == null) return null;
                if (!string.Equals(srcObj.TypeDescriptor?.Name, "SDT", StringComparison.OrdinalIgnoreCase)) return null;

                var srcPart = FindSdtStructurePartOf(srcObj);
                var tgtPart = FindSdtStructurePartOf(tgtObj);
                if (srcPart == null || tgtPart == null) return null;

                dynamic srcRoot = null, tgtRoot = null;
                try { srcRoot = ((dynamic)srcPart).Root; } catch { }
                try { tgtRoot = ((dynamic)tgtPart).Root; } catch { }
                if (srcRoot == null || tgtRoot == null) return null;

                // Root-level collection metadata (the flag the text DSL cannot express).
                try { tgtRoot.IsCollection = (bool)srcRoot.IsCollection; } catch { }
                try
                {
                    string cin = (string)srcRoot.CollectionItemName;
                    if (!string.IsNullOrEmpty(cin)) tgtRoot.CollectionItemName = cin;
                }
                catch { }

                Artech.Architecture.Common.Objects.KBModel model = null;
                try { model = srcObj.Model; } catch { }

                // Drop the seed item CreateObject added, then copy the source structure verbatim.
                ClearSdtItems(tgtRoot);
                int copied = CopySdtItems(srcRoot, tgtRoot, model);
                if (copied == 0) return null; // nothing copied → let the text path try

                tgtObj.Save();

                bool isCollection = false;
                string collectionItemName = null;
                try { isCollection = (bool)((dynamic)tgtPart).Root.IsCollection; } catch { }
                try { collectionItemName = (string)((dynamic)tgtPart).Root.CollectionItemName; } catch { }

                try { var idx = _kbService?.GetIndexCache(); if (idx != null) idx.UpdateEntry(tgtObj); }
                catch (Exception ex) { Logger.Error("CloneSdtStructurePart: index UpdateEntry failed for " + targetName + ": " + ex.Message); }

                var result = new JObject
                {
                    ["part"] = "SDTStructure",
                    ["clonedVia"] = "objectModel",
                    ["itemsCopied"] = copied,
                    ["isCollection"] = isCollection
                };
                if (!string.IsNullOrEmpty(collectionItemName)) result["collectionItemName"] = collectionItemName;
                return McpResponse.Ok(target: targetName, code: "Success", result: result);
            }
            catch (Exception ex)
            {
                Logger.Error("CloneSdtStructurePart failed for " + targetName + ": " + (ex.InnerException?.Message ?? ex.Message));
                return null; // fall back to the text path
            }
        }

        // issue #116: DataSelector structure (Parameters, Conditions, Orders, DefinedBy)
        // cannot be cloned through the textual DSL path. Clone it natively via the SDK
        // object model (and XML deserialization fallback). Returns a McpResponse JSON
        // envelope on success, or null when not applicable.
        public string CloneDataSelectorStructurePart(string sourceName, string targetName)
        {
            try
            {
                var srcObj = FindObject(sourceName) as DataSelector;
                var tgtObj = FindObject(targetName) as DataSelector;
                if (srcObj == null || tgtObj == null) return null;

                var srcPart = srcObj.DataSelectorStructure ?? srcObj.Parts.Get<DataSelectorStructurePart>();
                var tgtPart = tgtObj.DataSelectorStructure ?? tgtObj.Parts.Get<DataSelectorStructurePart>();
                if (srcPart == null || tgtPart == null) return null;

                bool xmlRoundTripped = false;
                try
                {
                    string xml = srcPart.SerializeToXml();
                    if (!string.IsNullOrWhiteSpace(xml) && !IsEmptyPropertiesXml(xml))
                    {
                        tgtPart.DeserializeFromXml(xml);
                        xmlRoundTripped = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("CloneDataSelectorStructurePart: XML deserialization fallback: " + ex.Message);
                }

                int parametersCopied = 0;
                int conditionsCopied = 0;
                int ordersCopied = 0;

                if (tgtPart.Root == null)
                {
                    try { tgtPart.Root = new DataSelectorLevel(tgtPart); } catch { }
                }

                // Parameters fallback if XML deserialization didn't populate parameters
                try
                {
                    if (srcPart.Parameters != null && srcPart.Parameters.Count > 0 && (tgtPart.Parameters == null || tgtPart.Parameters.Count == 0))
                    {
                        dynamic dParams = tgtPart.Parameters;
                        foreach (dynamic p in srcPart.Parameters)
                        {
                            try
                            {
                                if (p != null)
                                {
                                    dParams.Add(p);
                                    parametersCopied++;
                                }
                            }
                            catch { }
                        }
                    }
                    else if (tgtPart.Parameters != null)
                    {
                        parametersCopied = tgtPart.Parameters.Count;
                    }
                }
                catch { }

                // Conditions fallback if XML deserialization didn't populate conditions
                try
                {
                    var tgtConds = tgtPart.GetConditions();
                    int existingConds = tgtConds != null ? tgtConds.Count() : 0;
                    if (existingConds == 0)
                    {
                        var srcConditions = srcPart.GetConditions() ?? Enumerable.Empty<DataSelectorCondition>();
                        foreach (var cond in srcConditions)
                        {
                            string expr = cond.Source?.Source ?? cond.ToString();
                            if (!string.IsNullOrWhiteSpace(expr))
                            {
                                tgtPart.Root.AddCondition(expr);
                                conditionsCopied++;
                            }
                        }
                    }
                    else
                    {
                        conditionsCopied = existingConds;
                    }
                }
                catch { }

                // Orders fallback
                try
                {
                    var tgtOrders = tgtPart.GetOrders();
                    ordersCopied = tgtOrders != null ? tgtOrders.Count() : 0;
                }
                catch { }

                tgtObj.Save();

                try { var idx = _kbService?.GetIndexCache(); if (idx != null) idx.UpdateEntry(tgtObj); }
                catch (Exception ex) { Logger.Error("CloneDataSelectorStructurePart: index UpdateEntry failed for " + targetName + ": " + ex.Message); }

                var result = new JObject
                {
                    ["part"] = "DataSelectorStructure",
                    ["clonedVia"] = xmlRoundTripped ? "xml+objectModel" : "objectModel",
                    ["parametersCopied"] = parametersCopied,
                    ["conditionsCopied"] = conditionsCopied,
                    ["ordersCopied"] = ordersCopied
                };
                return McpResponse.Ok(target: targetName, code: "Success", result: result);
            }
            catch (Exception ex)
            {
                Logger.Error("CloneDataSelectorStructurePart failed for " + targetName + ": " + (ex.InnerException?.Message ?? ex.Message));
                return null;
            }
        }

        // Remove every item currently under an SDT structure node (used to drop the create-time seed).
        private static void ClearSdtItems(dynamic node)
        {
            try
            {
                var dead = new List<object>();
                foreach (dynamic it in node.Items) dead.Add(it);
                foreach (dynamic d in dead) { try { node.Items.Remove(d); } catch { } }
            }
            catch { }
        }

        // Recursively copy every item/level from a source SDT structure node onto a target node,
        // preserving type, length/decimals, the per-item collection flag, the Domain link
        // (DomainBasedOn) and SDT-reference (GX_SDT) so Domain-based / SDT-typed members survive
        // the clone instead of collapsing to their base primitive type. Returns items copied.
        private static int CopySdtItems(dynamic srcNode, dynamic tgtNode, Artech.Architecture.Common.Objects.KBModel model)
        {
            int n = 0;
            Type tgtNodeType = ((object)tgtNode).GetType();
            Type eDBTypeT = tgtNodeType.Assembly.GetType("Artech.Genexus.Common.eDBType");
            System.Reflection.MethodInfo addItem = eDBTypeT != null
                ? tgtNodeType.GetMethod("AddItem", new[] { typeof(string), eDBTypeT }) : null;
            System.Reflection.MethodInfo addLevel = tgtNodeType.GetMethod("AddLevel", new[] { typeof(string) });

            foreach (dynamic srcItem in srcNode.Items)
            {
                string name;
                try { name = (string)srcItem.Name; } catch { continue; }

                bool isLeaf;
                try { isLeaf = srcItem.IsLeafItem; }
                catch
                {
                    bool hasChildren = false;
                    try { foreach (var _ in srcItem.Items) { hasChildren = true; break; } } catch { }
                    isLeaf = !hasChildren;
                }

                bool isCollection = false;
                try { isCollection = (bool)srcItem.IsCollection; } catch { }

                if (!isLeaf)
                {
                    if (addLevel == null) continue;
                    dynamic newLevel;
                    try { newLevel = addLevel.Invoke((object)tgtNode, new object[] { name }); }
                    catch (Exception ex) { Logger.Error("CopySdtItems: AddLevel('" + name + "') failed: " + (ex.InnerException?.Message ?? ex.Message)); continue; }
                    if (newLevel == null) continue;
                    try { newLevel.IsCollection = isCollection; } catch { }
                    n += 1 + CopySdtItems(srcItem, newLevel, model);
                }
                else
                {
                    if (addItem == null) continue;
                    object dbType;
                    try { dbType = srcItem.Type; } catch { continue; }
                    dynamic newItem;
                    try { newItem = addItem.Invoke((object)tgtNode, new object[] { name, dbType }); }
                    catch (Exception ex) { Logger.Error("CopySdtItems: AddItem('" + name + "') failed: " + (ex.InnerException?.Message ?? ex.Message)); continue; }
                    if (newItem == null) continue;
                    try { newItem.Length = srcItem.Length; } catch { }
                    try { newItem.Decimals = srcItem.Decimals; } catch { }
                    try { newItem.IsCollection = isCollection; } catch { }
                    CopySdtItemTypeReference((object)srcItem, (object)newItem, (string)dbType?.ToString(), model);
                    n++;
                }
            }
            return n;
        }

        // Preserve a leaf member's non-primitive typing: a Domain-based member (DomainBasedOn) or a
        // member referencing another SDT (GX_SDT). The base eDBType was already applied by AddItem;
        // this re-establishes the reference the type token alone doesn't carry.
        private static void CopySdtItemTypeReference(object srcItem, object newItem, string typeToken, Artech.Architecture.Common.Objects.KBModel model)
        {
            // Attribute link — copy the AttributeBasedOn reference directly.
            try
            {
                dynamic abo = ((dynamic)srcItem).AttributeBasedOn;
                if (abo != null) { try { ((dynamic)newItem).AttributeBasedOn = abo; return; } catch { } }
            }
            catch { }

            // Domain link — copy the DomainBasedOn reference (a shared Domain KBObject) directly.
            try
            {
                dynamic dbo = ((dynamic)srcItem).DomainBasedOn;
                if (dbo != null) { try { ((dynamic)newItem).DomainBasedOn = dbo; return; } catch { } }
            }
            catch { }

            // SDT reference — resolve the referenced SDT's name from the source's ATTCUSTOMTYPE and
            // re-bind on the target (setting the property value verbatim doesn't stick across items).
            if (model != null && typeToken != null && typeToken.StartsWith("GX_SDT", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string refName = GxMcp.Worker.Helpers.SdtMemberResolver.ResolveReferencedTypeName(srcItem, model);
                    if (!string.IsNullOrEmpty(refName))
                    {
                        var sdtObj = GxMcp.Worker.Helpers.VariableInjector.ResolveTypeObject(model, refName);
                        if (sdtObj != null) GxMcp.Worker.Helpers.VariableInjector.BindSdtItemToSdt(newItem, sdtObj);
                    }
                }
                catch (Exception ex) { Logger.Debug("CopySdtItemTypeReference (SDT) failed: " + ex.Message); }
            }
        }

        // Returns the eDBType name actually seeded (e.g. "VARCHAR", "NUMERIC") for the
        // response's seededDescription, or null if seeding fell through / already populated.
        private static string InitializeSDTWithDefaultItem(KBObject sdt, string sdtName, string itemName = "Item1", string itemTypeName = "VARCHAR")
        {
            string seededTypeName = null;
            try
            {
                KBObjectPart structure = null;
                foreach (KBObjectPart p in sdt.Parts)
                {
                    if (p.Type == SDT_STRUCTURE_PART_GUID) { structure = p; break; }
                    try {
                        string descName = p.TypeDescriptor?.Name ?? "";
                        string className = p.GetType().Name;
                        if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                        { structure = p; break; }
                    } catch { }
                }

                if (structure == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: SDTStructurePart not found for " + sdtName);
                    return seededTypeName;
                }

                dynamic ds = structure;
                dynamic root = null;
                try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }
                if (root == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: Root not found for " + sdtName);
                    return seededTypeName;
                }

                dynamic items = null;
                try { items = root.Items; } catch { try { items = root.Children; } catch { } }
                if (items == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: items collection not found for " + sdtName);
                    return seededTypeName;
                }

                // Skip if structure is already populated
                try {
                    foreach (dynamic existing in items) { return seededTypeName; }
                } catch { }

                // Resolve the requested item type to its canonical eDBType name (VarChar→VARCHAR,
                // Character→CHARACTER, Numeric→NUMERIC, ...). Falls back to VARCHAR when unknown.
                string wantedEnumName = "VARCHAR";
                if (GxMcp.Worker.Helpers.VariableInjector.TryParseDbType(itemTypeName, out var wantedDbType))
                    wantedEnumName = wantedDbType.ToString();

                Type rootType = ((object)root).GetType();
                var asm = rootType.Assembly;

                // Preferred path: invoke the real SDK API root.AddItem(string, eDBType) — same
                // approach SdtDslParser uses. Ctor + items.Add doesn't work because SDTItem has
                // no public ctor we can satisfy; that path always returned null and left the
                // SDT empty, which then made Save() reject it.
                Type eDBTypeT = asm.GetType("Artech.Genexus.Common.eDBType");
                if (eDBTypeT != null)
                {
                    MethodInfo addItem = rootType.GetMethod("AddItem", new Type[] { typeof(string), eDBTypeT });
                    if (addItem != null)
                    {
                        try
                        {
                            object typeVal = Enum.Parse(eDBTypeT, wantedEnumName);
                            object added = addItem.Invoke(root, new object[] { itemName, typeVal });
                            if (added != null)
                            {
                                Logger.Info("InitializeSDTWithDefaultItem: seeded '" + itemName + "' (" + wantedEnumName + ") into " + sdtName + " via AddItem(string, eDBType)");
                                // Friction-report 05-13 #2 (the exact trap SdtDslParser documents):
                                // AddItem mutates the in-memory Items collection, but the
                                // SDTStructurePart may not flag itself dirty — the following
                                // obj.Save() then persists the OLD serialized XML, so the seed
                                // claims success while the SDT stays empty (issue #79 bonus
                                // report). Force the part dirty exactly like the SDT write path
                                // (SDTService.UpdateSDTStructure) does before returning.
                                GxMcp.Worker.Parsers.SdtDslParser.MarkPartDirty((object)structure, sdtName);
                                return wantedEnumName;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("InitializeSDTWithDefaultItem: AddItem(string, eDBType) threw for " + sdtName + ": " + (ex.InnerException?.Message ?? ex.Message));
                        }
                    }
                    else
                    {
                        var sigs = string.Join("; ", rootType.GetMethods().Where(m => m.Name == "AddItem").Select(m => "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
                        Logger.Error("InitializeSDTWithDefaultItem: AddItem(string, eDBType) not found on " + rootType.FullName + ". Sigs=[" + sigs + "]");
                    }
                }
                else
                {
                    Logger.Error("InitializeSDTWithDefaultItem: Artech.Genexus.Common.eDBType not resolvable in " + asm.GetName().Name);
                }

                // Fallback: legacy ctor path (kept in case SDK API surface differs in some build).
                Type sdtItemType = null;
                string[] namespaces = { "Artech.Genexus.Common.Parts", "Artech.Genexus.Common.Objects", "Artech.Genexus.Common", "Artech.Genexus.Common.Parts.SDT", rootType.Namespace };
                foreach (var ns in namespaces)
                {
                    if (string.IsNullOrEmpty(ns)) continue;
                    sdtItemType = asm.GetType(ns + ".SDTItem") ?? asm.GetType(ns + ".SDTLevel") ?? asm.GetType(ns + ".StructureItem") ?? asm.GetType(ns + ".StructureLevel");
                    if (sdtItemType != null) break;
                }
                if (sdtItemType == null)
                {
                    foreach (var loadedAsm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (var t in loadedAsm.GetTypes())
                            {
                                if (!t.IsClass || t.IsAbstract) continue;
                                string n = t.Name;
                                if (n.Equals("SDTItem", StringComparison.OrdinalIgnoreCase) || n.Equals("SDTLevel", StringComparison.OrdinalIgnoreCase))
                                {
                                    sdtItemType = t;
                                    break;
                                }
                            }
                        }
                        catch { }
                        if (sdtItemType != null) break;
                    }
                }
                if (sdtItemType == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: SDTItem type not resolved for " + sdtName + " (fallback path).");
                    return seededTypeName;
                }

                dynamic newItem = null;
                Exception lastCtorEx = null;
                object[][] ctorArgVariants = new object[][] {
                    new object[] { root },
                    new object[] { structure },
                    new object[] { },
                    new object[] { sdt },
                    new object[] { structure, root }
                };
                foreach (var args in ctorArgVariants)
                {
                    try { newItem = Activator.CreateInstance(sdtItemType, args); if (newItem != null) break; }
                    catch (Exception ex) { lastCtorEx = ex; }
                }
                if (newItem == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: ctor fallback failed for " + sdtName + ". LastEx: " + lastCtorEx?.Message);
                    return seededTypeName;
                }
                newItem.Name = itemName;
                try
                {
                    if (eDBTypeT != null) newItem.Type = Enum.Parse(eDBTypeT, wantedEnumName);
                } catch { }
                items.Add(newItem);
                seededTypeName = wantedEnumName;
                // Friction-report 05-13 #2: mark the part dirty here too so the fallback
                // path persists the item on the next obj.Save() (see AddItem path above).
                GxMcp.Worker.Parsers.SdtDslParser.MarkPartDirty((object)structure, sdtName);
                Logger.Info("InitializeSDTWithDefaultItem: seeded '" + itemName + "' (" + wantedEnumName + ") into " + sdtName + " via ctor fallback");
            }
            catch (Exception ex)
            {
                Logger.Error("InitializeSDTWithDefaultItem failed: " + ex.Message);
            }
            return seededTypeName;
        }

        private static readonly ConcurrentDictionary<string, Guid> _typeGuidCache =
            new ConcurrentDictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve a friendly object-type name (e.g. "WebPanel", "Dashboard", "WorkflowDiagram") to
        /// the KBObject type Guid. First consults a static table of typed-wrapper descriptors;
        /// then falls back to reading the matching static Guid field on
        /// <c>Artech.Genexus.Common.ObjClass</c> via reflection. Returns Guid.Empty when unknown.
        /// </summary>
        private static Guid ResolveObjectTypeGuid(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return Guid.Empty;
            string key = NormalizeTypeAlias(type);
            if (_typeGuidCache.TryGetValue(key, out var cached)) return cached;

            Guid g = ResolveFromTypedDescriptor(key);
            if (g == Guid.Empty) g = ResolveFromObjClassField(key);
            if (g != Guid.Empty) _typeGuidCache[key] = g;
            return g;
        }

        private static string NormalizeTypeAlias(string type)
        {
            string t = (type ?? string.Empty).Trim();
            if (t.Equals("Pattern Settings", StringComparison.OrdinalIgnoreCase)) return "PatternSettings";
            if (t.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase)) return "SDT";
            if (t.Equals("Structure", StringComparison.OrdinalIgnoreCase)) return "SDT";
            if (t.Equals("Trn", StringComparison.OrdinalIgnoreCase)) return "Transaction";
            if (t.Equals("Proc", StringComparison.OrdinalIgnoreCase)) return "Procedure";
            if (t.Equals("WP", StringComparison.OrdinalIgnoreCase)) return "WebPanel";
            if (t.Equals("BusinessProcessDiagram", StringComparison.OrdinalIgnoreCase)) return "WorkflowDiagram";
            if (t.Equals("BPD", StringComparison.OrdinalIgnoreCase)) return "WorkflowDiagram";
            if (t.Equals("PanelForSD", StringComparison.OrdinalIgnoreCase)) return "SDPanel";
            return t;
        }

        internal static bool ResolutionTypeMatches(string actualType, string requestedType)
        {
            return string.IsNullOrWhiteSpace(requestedType)
                || string.Equals(NormalizeTypeAlias(actualType), NormalizeTypeAlias(requestedType), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ResolutionTypeMatches(KBObject obj, string requestedType)
        {
            return obj != null && (ResolutionTypeMatches(obj.TypeDescriptor?.Name, requestedType)
                || ResolutionTypeMatches(obj.GetType().Name, requestedType));
        }

        private static Guid ResolveFromTypedDescriptor(string type)
        {
            try
            {
                if (type.Equals("Module", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Architecture.Common.Objects.Module>().Id;
                if (type.Equals("Procedure", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Procedure>().Id;
                if (type.Equals("Transaction", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Transaction>().Id;
                if (type.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.WebPanel>().Id;
                if (type.Equals("SDT", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.SDT>().Id;
                if (type.Equals("DataProvider", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DataProvider>().Id;
                if (type.Equals("Attribute", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                if (type.Equals("Table", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Table>().Id;
                if (type.Equals("Domain", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Domain>().Id;
                if (type.Equals("DataSelector", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DataSelector>().Id;
                if (type.Equals("DataView", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DataView>().Id;
                if (type.Equals("Index", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Index>().Id;
                if (type.Equals("ExternalObject", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.ExternalObject>().Id;
                if (type.Equals("Theme", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Theme>().Id;
                if (type.Equals("Image", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Image>().Id;
                if (type.Equals("Menu", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Menu>().Id;
                if (type.Equals("Menubar", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Menubar>().Id;
                if (type.Equals("Stencil", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Stencil>().Id;
                if (type.Equals("UserControl", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.UserControl>().Id;
                if (type.Equals("WorkPanel", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.WorkPanel>().Id;
                if (type.Equals("Report", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Report>().Id;
                if (type.Equals("API", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.API>().Id;
                if (type.Equals("URLRewrite", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.URLRewrite>().Id;
                if (type.Equals("MiniApp", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.MiniApp>().Id;
                if (type.Equals("SuperApp", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.SuperApp>().Id;
                if (type.Equals("DesignSystem", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DesignSystem>().Id;
                if (type.Equals("ColorPalette", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.ColorPalette>().Id;
                if (type.Equals("OfflineDatabase", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.OfflineDatabase>().Id;
                if (type.Equals("Group", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Group>().Id;
                if (type.Equals("Language", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Language>().Id;
            }
            catch (Exception ex)
            {
                Logger.Error("ResolveFromTypedDescriptor failed for " + type + ": " + ex.Message);
            }
            return Guid.Empty;
        }

        private static Type _objClassType;

        private static Guid ResolveFromObjClassField(string type)
        {
            // Static Guid fields on Artech.Genexus.Common.ObjClass cover Dashboard, SDPanel, Query,
            // WorkflowDiagram, ConversationalFlows, TestSuite, ThemeClass, WorkWithDevices, etc. —
            // anything the IDE creates that doesn't have its own typed wrapper.
            Type t = _objClassType;
            if (t == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = asm.GetName().Name;
                    if (n == null || !n.StartsWith("Artech.Genexus.Common", StringComparison.Ordinal)) continue;
                    try { t = asm.GetType("Artech.Genexus.Common.ObjClass", throwOnError: false); }
                    catch { continue; }
                    if (t != null) { _objClassType = t; break; }
                }
            }
            if (t == null) return Guid.Empty;

            var fi = t.GetField(type, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
            if (fi == null) return Guid.Empty;
            try
            {
                return fi.GetValue(null) is Guid g ? g : Guid.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error("ResolveFromObjClassField: read " + type + " failed: " + ex.Message);
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Initialize a freshly-created Domain with caller-supplied dataType/length/decimals/signed/enumValues/basedOn.
        /// Defaults to Character(20) when no dataType is provided — matches the IDE's "new domain" default
        /// and gives the SDK a valid type before Save.
        /// </summary>
        /// <returns>_meta JObject describing what was applied (echoed to caller), or null on hard error.</returns>
        private static JObject InitializeDomain(KBObject domainObj, string domainName, JObject options, Artech.Architecture.Common.Objects.KnowledgeBase kb)
        {
            var meta = new JObject();
            try
            {
                string dataType = options?["dataType"]?.ToString();
                int? length = options?["length"]?.ToObject<int?>();
                int? decimals = options?["decimals"]?.ToObject<int?>();
                bool? signed = options?["signed"]?.ToObject<bool?>();
                string description = options?["description"]?.ToString();
                string basedOnName = options?["basedOn"]?.ToString();
                var enumArr = options?["enumValues"] as JArray;

                // basedOn short-circuits dataType: a domain-based-on-domain inherits its type.
                bool basedOnApplied = false;
                if (!string.IsNullOrEmpty(basedOnName))
                {
                    object basedOn = null;
                    try
                    {
                        foreach (var obj in kb.DesignModel.Objects.GetByName(null, null, basedOnName))
                        {
                            if (obj is Artech.Genexus.Common.Objects.Domain d) { basedOn = d; break; }
                        }
                    }
                    catch (Exception ex) { Logger.Error("InitializeDomain: basedOn lookup failed: " + ex.Message); }

                    if (basedOn == null)
                    {
                        meta["basedOnError"] = "Domain '" + basedOnName + "' not found in KB. Created standalone Character(20) instead.";
                    }
                    else if (!DomainPropertyApplier.ApplyDomainBasedOn(domainObj, basedOn))
                    {
                        meta["basedOnError"] = "Failed to apply DomainBasedOn=" + basedOnName + ".";
                    }
                    else
                    {
                        meta["basedOn"] = basedOnName;
                        basedOnApplied = true;
                    }
                }

                if (!basedOnApplied)
                {
                    if (string.IsNullOrEmpty(dataType)) dataType = "Character";
                    if (!length.HasValue && dataType.Equals("Character", StringComparison.OrdinalIgnoreCase)) length = 20;
                    if (!length.HasValue && dataType.Equals("VarChar", StringComparison.OrdinalIgnoreCase)) length = 40;
                    if (!length.HasValue && dataType.Equals("Numeric", StringComparison.OrdinalIgnoreCase)) length = 8;

                    if (!DomainPropertyApplier.ApplyPrimitive(domainObj, dataType, length, decimals, signed))
                    {
                        Logger.Error("InitializeDomain: ApplyPrimitive failed for " + domainName + " (dataType=" + dataType + ")");
                        meta["typeError"] = "Could not apply dataType='" + dataType + "'. Supported: Character, VarChar, Numeric, Date, DateTime, Time, Boolean, LongVarChar, Blob, Image, GUID.";
                    }
                    else
                    {
                        meta["dataType"] = dataType;
                        if (length.HasValue) meta["length"] = length.Value;
                        if (decimals.HasValue) meta["decimals"] = decimals.Value;
                        if (signed.HasValue) meta["signed"] = signed.Value;
                    }
                }

                if (!string.IsNullOrEmpty(description))
                {
                    try { domainObj.Description = description; } catch { /* best-effort */ }
                }

                if (enumArr != null && enumArr.Count > 0)
                {
                    // ISSUE-55 ground truth (2026-07-31, GeneXus 18.0.10): the SDK persists
                    // enum values in the stored XML as RAW literals for every family —
                    // verified against the template's own character enum (HttpMethod stores
                    // <Value>GET</Value>, no quotes). Quoted values ("R") are silently dropped
                    // by the property bag write (post-apply bag read-back is null), which is
                    // exactly the "empty combobox / enum not persisted" report. Values pass
                    // through verbatim regardless of family.
                    var specs = DomainEnumValues.FromJson(enumArr);

                    int applied = DomainPropertyApplier.ApplyEnumValues(domainObj, specs);
                    if (applied < 0)
                    {
                        meta["enumError"] = "Could not write EnumValues — SDK helper not resolvable. Domain saved without enum values; set them via IDE.";
                    }
                    else if (applied > 0)
                    {
                        var arr = new JArray();
                        foreach (var s in specs.Take(applied)) arr.Add(new JObject { ["name"] = s.Name, ["value"] = s.Value });
                        meta["enumValues"] = arr;
                        meta["enumHint"] = "Enum values applied verbatim. Verify via genexus_types action=describe name=" + domainName + ".";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("InitializeDomain failed for " + domainName + ": " + ex.Message);
                meta["initError"] = ex.Message;
            }
            return meta;
        }

        // v2.8.5: deterministic ambiguous-name resolution. When a bare name matches
        // multiple objects (classic: a Transaction and its generated Table share a
        // name), the old ordering left Table and Transaction tied at rank 0 and the
        // pick was whatever the dictionary enumerated first — nondeterministic, and
        // the reason genexus_inspect (which got Table) and genexus_analyze impact
        // (which prefers Transaction) silently resolved DIFFERENT objects for the
        // same name. Editable logic objects now rank above the generated Table/View,
        // with a stable type tiebreak so the result is repeatable across calls.
        internal static SearchIndex.IndexEntry PrioritizeNameMatches(IList<SearchIndex.IndexEntry> matches)
        {
            if (matches == null || matches.Count == 0) return null;
            return matches
                .OrderBy(m => (m.Type == "Folder" || m.Type == "Module") ? 100 : 0)
                .ThenBy(m => (m.Type == "File" || m.Type == "Image") ? 50 : 0)
                .ThenBy(m => IsGeneratedPhysical(m.Type) ? 10 : 0)
                .ThenBy(m => m.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        internal static bool IsGeneratedPhysical(string type)
        {
            return string.Equals(type, "Table", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "View", StringComparison.OrdinalIgnoreCase);
        }

        // Immediate KB Explorer parent (folder/module) name of an object, normalized so
        // the Root Module reads as "Root Module" (its SDK Name is empty / DesignModel).
        private static string ImmediateParentName(KBObject obj)
        {
            try
            {
                var parent = obj?.Parent;
                if (parent == null) return "Root Module";
                string ptype = null;
                try { ptype = parent.TypeDescriptor?.Name; } catch { }
                if (string.Equals(ptype, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    return "Root Module";
                string pname = null;
                try { pname = parent.Name; } catch { }
                return string.IsNullOrWhiteSpace(pname) ? "Root Module" : pname;
            }
            catch { return null; }
        }

        // Move an object into a Folder or Module (its KB Explorer parent). Replaces the
        // old FolderPlacementUnsupported/FolderMoveNotSupported rejects — see ObjectMover
        // for why the "SDK can't do it" verdict was wrong (facade-DLL decompilation).
        public string MoveObject(string target, string destination, string typeFilter = null, string destKind = null,
            bool dryRun = false, string baseVersion = null, bool rollbackOnFailure = true)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return McpResponse.Err(code: "NoKb", message: "No KB open");
                if (string.IsNullOrWhiteSpace(target))
                    return McpResponse.Err(code: "BadArgs", message: "target (object to move) is required.");
                if (string.IsNullOrWhiteSpace(destination))
                    return McpResponse.Err(code: "BadArgs", message: "destination (folder or module name) is required.");

                var obj = FindObject(target, typeFilter);
                if (obj == null)
                    return McpResponse.Err(code: "ObjectNotFound", message: "Object '" + target + "' not found.",
                        hint: "Check the name/type. genexus_list_objects lists available objects.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")),
                        target: target);

                // Resolve the destination container. Honor an explicit destKind
                // (Folder/Module); otherwise try Folder first, then Module.
                KBObject container = null;
                string kind = destKind?.Trim();
                if (!string.IsNullOrEmpty(kind))
                {
                    container = FindObject(destination, kind);
                }
                else
                {
                    container = FindObject(destination, "Folder") ?? FindObject(destination, "Module");
                }
                if (container == null)
                    return McpResponse.Err(code: "DestinationNotFound",
                        message: "No Folder or Module named '" + destination + "' in this KB.",
                        hint: "Create it first (genexus_create type=Folder name=" + destination + " or type=Module), or check the name.",
                        target: target);

                string containerType = null;
                try { containerType = container.TypeDescriptor?.Name; } catch { }

                if (container.Guid == obj.Guid)
                    return McpResponse.Err(code: "BadArgs", message: "Cannot move an object into itself.", target: target);

                string from = ImmediateParentName(obj);
                string beforeVersion = WriteService.ComputeVersionToken(obj);
                if (!string.IsNullOrWhiteSpace(baseVersion)
                    && !string.Equals(baseVersion, beforeVersion, StringComparison.Ordinal))
                {
                    return McpResponse.Err(code: "VersionConflict",
                        message: "The object changed after it was read; the move was not attempted.",
                        hint: "Re-read the object and retry with the new versionToken.",
                        target: target,
                        extra: new JObject { ["baseVersion"] = baseVersion, ["currentVersion"] = beforeVersion });
                }

                ObjectMoveSnapshot snapshot;
                try { snapshot = ObjectMoveSnapshot.Capture(obj); }
                catch (Exception ex)
                {
                    return McpResponse.Err(code: "MoveSnapshotFailed",
                        message: "Could not capture every object part before moving: " + ex.Message,
                        hint: "No move was attempted.", target: target);
                }

                if (dryRun)
                {
                    MarkReadCacheDirty(obj);
                    KBObject dryFresh = null;
                    try { dryFresh = kb.DesignModel.Objects.Get(obj.Guid); } catch { }
                    string afterDryVersion = WriteService.ComputeVersionToken(dryFresh ?? obj);
                    var dryComparison = snapshot.Compare(dryFresh ?? obj);
                    bool versionUnchanged = string.Equals(beforeVersion, afterDryVersion, StringComparison.Ordinal);
                    if (!versionUnchanged || !dryComparison.Equal)
                    {
                        return McpResponse.Err(code: "DryRunMutationDetected",
                            message: "The persisted object changed while evaluating the move preview.",
                            hint: "The move was not executed. Re-read the object before retrying.",
                            target: target,
                            extra: new JObject
                            {
                                ["persisted"] = false,
                                ["versionUnchanged"] = versionUnchanged,
                                ["beforeVersion"] = beforeVersion,
                                ["afterVersion"] = afterDryVersion,
                                ["requestedHash"] = snapshot.Hash,
                                ["persistedHash"] = dryComparison.PersistedHash,
                                ["changedParts"] = dryComparison.ChangedParts,
                                ["implicitOperations"] = new JArray()
                            });
                    }
                    return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                    {
                        ["move"] = target,
                        ["from"] = from,
                        ["to"] = destination,
                        ["containerType"] = containerType,
                        ["persisted"] = false,
                        ["verified"] = true,
                        ["preservedParts"] = snapshot.PreservedParts,
                        ["before"] = new JObject { ["parent"] = from, ["hash"] = snapshot.Hash },
                        ["afterProjected"] = new JObject { ["parent"] = destination, ["hash"] = snapshot.Hash },
                        ["versionToken"] = beforeVersion,
                        ["versionUnchanged"] = true,
                        ["implicitOperations"] = new JArray(),
                        ["hint"] = "Re-run without dryRun to persist the move."
                    });
                }

                Helpers.ObjectMover.MoveResult res = default(Helpers.ObjectMover.MoveResult);
                KBObject fresh = null;
                KBObject originalParent = null;
                try { originalParent = obj.Parent; } catch { }
                string to = null;
                ObjectMoveSnapshot.Comparison comparison = null;
                bool committed = false;
                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        MarkReadCacheDirty(obj);
                        var current = kb.DesignModel.Objects.Get(obj.Guid) ?? obj;
                        string transactionVersion = WriteService.ComputeVersionToken(current);
                        if (!string.IsNullOrWhiteSpace(baseVersion)
                            && !string.Equals(baseVersion, transactionVersion, StringComparison.Ordinal))
                        {
                            return McpResponse.Err(code: "VersionConflict",
                                message: "The object changed before the move transaction acquired it; no write was committed.",
                                hint: "Re-read the object and retry with the new versionToken.", target: target,
                                extra: new JObject { ["baseVersion"] = baseVersion, ["currentVersion"] = transactionVersion });
                        }

                        res = Helpers.ObjectMover.SetParentAndSave(current, container);
                        if (!res.Ok) throw new InvalidOperationException(res.Error ?? "The SDK move failed.");

                        // Validate inside the transaction before exposing the new placement.
                        // This is especially important for the SaveWithParent fallback on U16,
                        // which can rebuild default Procedure parts while reporting success.
                        MarkReadCacheDirty(current);
                        var pending = kb.DesignModel.Objects.Get(obj.Guid) ?? current;
                        var pendingComparison = snapshot.Compare(pending);
                        string pendingParent = ImmediateParentName(pending);
                        if (!string.Equals(pendingParent, destination, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The SDK did not persist the requested parent inside the move transaction.");
                        if (!pendingComparison.Equal)
                            throw new InvalidOperationException("The SDK changed object content inside the move transaction: "
                                + string.Join(", ", pendingComparison.ChangedParts.Values<string>()));

                        tx.Commit();
                        committed = true;
                    }
                    catch (Exception ex)
                    {
                        if (!committed) try { tx.Rollback(); } catch { }
                        var rollback = VerifyMoveRollback(kb, obj, snapshot, from);
                        if (!(rollback["verified"]?.ToObject<bool>() ?? false) && rollbackOnFailure)
                            rollback = CompensateMoveRollback(kb, obj.Guid, originalParent, snapshot, from);
                        return McpResponse.Err(code: "MoveFailed",
                            message: "Could not safely persist the move: " + ex.Message,
                            target: target,
                            extra: new JObject
                            {
                                ["saved"] = res.Ok,
                                ["persisted"] = false,
                                ["verified"] = false,
                                ["requestedHash"] = snapshot.Hash,
                                ["persistedHash"] = rollback["persistedHash"]?.DeepClone(),
                                ["rollback"] = rollback,
                                ["implicitOperations"] = new JArray()
                            });
                    }
                }

                // Re-read after commit as an independent persistence barrier. If the SDK
                // reports a different persisted state now, restore the complete snapshot.
                try
                {
                    MarkReadCacheDirty(obj);
                    fresh = kb.DesignModel.Objects.Get(obj.Guid) ?? obj;
                    to = ImmediateParentName(fresh);
                    comparison = snapshot.Compare(fresh);
                }
                catch (Exception ex)
                {
                    var rollback = rollbackOnFailure
                        ? CompensateMoveRollback(kb, obj.Guid, originalParent, snapshot, from)
                        : VerifyMoveRollback(kb, obj, snapshot, from, attempted: false);
                    return McpResponse.Err(code: "MoveVerificationFailed",
                        message: "The moved object could not be re-read for persistence verification: " + ex.Message,
                        target: target,
                        extra: new JObject
                        {
                            ["saved"] = true, ["persisted"] = false, ["verified"] = false,
                            ["requestedHash"] = snapshot.Hash,
                            ["persistedHash"] = rollback["persistedHash"]?.DeepClone(),
                            ["rollback"] = rollback,
                            ["implicitOperations"] = new JArray()
                        });
                }

                bool finalMoved = string.Equals(to, destination, StringComparison.OrdinalIgnoreCase);
                if (!finalMoved || !comparison.Equal)
                {
                    var rollback = rollbackOnFailure
                        ? CompensateMoveRollback(kb, obj.Guid, originalParent, snapshot, from)
                        : VerifyMoveRollback(kb, obj, snapshot, from, attempted: false);
                    return McpResponse.Err(code: !finalMoved ? "MoveNotPersisted" : "MoveContentNotPreserved",
                        message: !finalMoved
                            ? "The SDK reported success, but the destination was not persisted."
                            : "The object moved, but persisted content changed (" +
                              string.Join(", ", comparison.ChangedParts.Values<string>()) + "); success was withheld.",
                        hint: "The pre-move snapshot was restored when rollbackOnFailure was enabled.", target: target,
                        extra: new JObject
                        {
                            ["saved"] = true, ["persisted"] = false, ["verified"] = false,
                            ["from"] = from, ["to"] = to, ["requestedHash"] = snapshot.Hash,
                            ["persistedHash"] = comparison.PersistedHash,
                            ["changedParts"] = comparison.ChangedParts, ["rollback"] = rollback,
                            ["implicitOperations"] = new JArray()
                        });
                }

                try
                {
                    var idx = _kbService?.GetIndexCache();
                    if (idx != null)
                    {
                        // Drop the stale (old-parent) hierarchy cache + child slot first, else
                        // UpdateEntry reuses the cached parent and list/inspect keep showing the
                        // old folder (the object moved on disk but the index would lie).
                        idx.InvalidateHierarchy(fresh.Guid);
                        idx.UpdateEntry(fresh);
                    }
                }
                catch (Exception ex) { Logger.Error("MoveObject: index refresh failed for " + target + ": " + ex.Message); }

                Logger.Info(string.Format("Moved '{0}' from '{1}' to '{2}' via {3} in {4}ms", target, from, to, res.Strategy, sw.ElapsedMilliseconds));
                return McpResponse.Ok(target: target, code: "ObjectMovedAndVerified", result: new JObject
                {
                    ["moved"] = target,
                    ["from"] = from,
                    ["to"] = to,
                    ["containerType"] = containerType,
                    ["strategy"] = res.Strategy,
                    ["saved"] = true,
                    ["persisted"] = true,
                    ["verified"] = true,
                    ["preservedParts"] = snapshot.PreservedParts,
                    ["requestedHash"] = snapshot.Hash,
                    ["persistedHash"] = comparison?.PersistedHash,
                    ["previousVersionToken"] = beforeVersion,
                    ["versionToken"] = WriteService.ComputeVersionToken(fresh),
                    ["generated"] = false,
                    ["implicitOperations"] = new JArray()
                });
            }
            catch (Exception ex)
            {
                Logger.Error("MoveObject failed for '" + target + "': " + ex.Message);
                return McpResponse.Err(code: "MoveError", message: ex.Message, target: target);
            }
        }

        private JObject VerifyMoveRollback(KnowledgeBase kb, KBObject seed, ObjectMoveSnapshot snapshot,
            string expectedParent, bool attempted = true)
        {
            try
            {
                MarkReadCacheDirty(seed);
                var restored = kb.DesignModel.Objects.Get(seed.Guid) ?? seed;
                var comparison = snapshot.Compare(restored);
                bool parentRestored = string.Equals(ImmediateParentName(restored), expectedParent, StringComparison.OrdinalIgnoreCase);
                bool stateMatchesSnapshot = parentRestored && comparison.Equal;
                return new JObject
                {
                    ["attempted"] = attempted,
                    ["verified"] = attempted && stateMatchesSnapshot,
                    ["stateMatchesSnapshot"] = stateMatchesSnapshot,
                    ["parentRestored"] = parentRestored,
                    ["contentRestored"] = comparison.Equal,
                    ["persistedHash"] = comparison.PersistedHash,
                    ["changedParts"] = comparison.ChangedParts
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["attempted"] = attempted, ["verified"] = false, ["error"] = ex.Message };
            }
        }

        private JObject CompensateMoveRollback(KnowledgeBase kb, Guid objectGuid, KBObject originalParent,
            ObjectMoveSnapshot snapshot, string expectedParent)
        {
            try
            {
                using (var tx = kb.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        var current = kb.DesignModel.Objects.Get(objectGuid);
                        if (current == null) throw new InvalidOperationException("The moved object no longer exists.");
                        snapshot.RestoreObject(current);
                        current.Dirty = true;
                        current.Save();
                        snapshot.RestoreParts(current);
                        if (originalParent != null)
                        {
                            var moveBack = Helpers.ObjectMover.SetParentAndSave(current, originalParent);
                            if (!moveBack.Ok) throw new InvalidOperationException("Parent rollback failed: " + moveBack.Error);
                        }
                        tx.Commit();
                        committed = true;
                    }
                    finally { if (!committed) try { tx.Rollback(); } catch { } }
                }

                var seed = kb.DesignModel.Objects.Get(objectGuid);
                var verified = VerifyMoveRollback(kb, seed, snapshot, expectedParent);
                verified["compensating"] = true;
                return verified;
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["attempted"] = true,
                    ["compensating"] = true,
                    ["verified"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        private static KBObject ResolveTypedObjectDirect(Artech.Architecture.Common.Objects.KBModel model, string type, string name)
        {
            if (model == null || string.IsNullOrWhiteSpace(name)) return null;
            string norm = !string.IsNullOrWhiteSpace(type) ? NormalizeTypeAlias(type) : null;
            try
            {
                // 1. Try ObjectNameHelper (built-in module-aware resolver)
                var helperObj = global::Artech.Architecture.Common.Helpers.ObjectNameHelper.Get(model, name) as KBObject;
                if (helperObj != null)
                {
                    // If helper resolved to a physical Table, promote to the source-bearing Transaction if one exists with the same name
                    if (string.IsNullOrEmpty(norm) && (helperObj is global::Artech.Genexus.Common.Objects.Table ||
                        string.Equals(helperObj.TypeDescriptor?.Name, "Table", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var trn = global::Artech.Genexus.Common.Objects.Transaction.Get(model, new global::Artech.Architecture.Common.Objects.QualifiedName(name));
                            if (trn != null) helperObj = trn;
                        }
                        catch { }
                    }

                    string hType = helperObj.TypeDescriptor?.Name ?? helperObj.GetType().Name;
                    if (ResolutionTypeMatches(helperObj, norm))
                    {
                        Logger.Debug(string.Format("ResolveTypedObjectDirect: ObjectNameHelper matched '{0}' as {1}", name, hType));
                        return helperObj;
                    }
                    else
                    {
                        Logger.Debug(string.Format("ResolveTypedObjectDirect: ObjectNameHelper returned '{0}' of type {1}, but wanted {2}", name, hType, norm));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(string.Format("ResolveTypedObjectDirect ObjectNameHelper error for '{0}': {1}", name, ex.Message));
            }

            // 2. Native SDK GetByName across modules
            try
            {
                Guid? filterGuid = !string.IsNullOrEmpty(norm) ? (Guid?)ResolveObjectTypeGuid(norm) : null;
                if (filterGuid == Guid.Empty) filterGuid = null;

                var sdkMatches = model.Objects.GetByName(null, filterGuid, name);
                if (sdkMatches != null)
                {
                    foreach (KBObject o in sdkMatches)
                    {
                        if (ResolutionTypeMatches(o, norm))
                        {
                            // If it's a Table and untyped, prefer Transaction
                            if (string.IsNullOrEmpty(norm) && (o is global::Artech.Genexus.Common.Objects.Table || string.Equals(o.TypeDescriptor?.Name, "Table", StringComparison.OrdinalIgnoreCase)))
                            {
                                try
                                {
                                    var trn = global::Artech.Genexus.Common.Objects.Transaction.Get(model, new global::Artech.Architecture.Common.Objects.QualifiedName(name));
                                    if (trn != null) return trn;
                                }
                                catch { }
                            }
                            return o;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(string.Format("ResolveTypedObjectDirect GetByName error for '{0}': {1}", name, ex.Message));
            }

            if (!string.IsNullOrEmpty(norm))
            {
                try
                {
                    // 3. Try simple QualifiedName (root-module relative)
                    var qName = new global::Artech.Architecture.Common.Objects.QualifiedName(name);

                    if (norm.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                        return global::Artech.Genexus.Common.Objects.SDT.Get(model, qName);
                    if (norm.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
                        return global::Artech.Genexus.Common.Objects.Transaction.Get(model, qName);
                    if (norm.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
                        return global::Artech.Genexus.Common.Objects.Procedure.Get(model, qName);
                    if (norm.Equals("WebPanel", StringComparison.OrdinalIgnoreCase))
                        return global::Artech.Genexus.Common.Objects.WebPanel.Get(model, qName);
                    if (norm.Equals("DataProvider", StringComparison.OrdinalIgnoreCase))
                        return global::Artech.Genexus.Common.Objects.DataProvider.Get(model, qName);
                    if (norm.Equals("DataSelector", StringComparison.OrdinalIgnoreCase))
                        return global::Artech.Genexus.Common.Objects.DataSelector.Get(model, qName);
                    if (norm.Equals("API", StringComparison.OrdinalIgnoreCase))
                        return global::Artech.Genexus.Common.Objects.API.Get(model, qName);
                }
                catch (Exception ex)
                {
                    Logger.Debug(string.Format("ResolveTypedObjectDirect static Get error for '{0}' as {1}: {2}", name, norm, ex.Message));
                }
            }
            return null;
        }

        private static readonly string[] DirectCandidates = { "Procedure", "Transaction", "WebPanel", "SDT", "DataProvider", "DataSelector" };
        private static readonly string[] CandidateIndexTypes = { "Procedure", "Transaction", "WebPanel", "SDT", "DataProvider", "DataSelector", "Domain", "Table" };
        private static readonly string[] DefaultPartsToFetch = { "Source", "Rules", "Events", "Variables", "Documentation", "Help", "Methods" };
        private static readonly char[] ColonSeparator = { ':' };

        private static int GetMatchPriority(SearchIndex.IndexEntry m)
        {
            if (m == null) return 1000;
            if (m.Type == "Folder" || m.Type == "Module") return 100;
            if (m.Type == "File" || m.Type == "Image") return 50;
            if (IsGeneratedPhysical(m.Type)) return 10;
            return 0;
        }

        private static int CompareMatches(SearchIndex.IndexEntry a, SearchIndex.IndexEntry b)
        {
            int pa = GetMatchPriority(a);
            int pb = GetMatchPriority(b);
            if (pa != pb) return pa.CompareTo(pb);
            return string.Compare(a.Type ?? string.Empty, b.Type ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [ThreadStatic]
        private static JObject _lastResolutionDiagnostic;

        private static void SetIndexedUnavailable(SearchIndex.IndexEntry entry, string requestedTarget, string typeFilter, string strategy = null)
        {
            if (entry == null) return;
            _lastResolutionDiagnostic = new JObject
            {
                ["diagnostic"] = "IndexedObjectUnavailable",
                ["indexed"] = true,
                ["persisted"] = false,
                ["requested"] = requestedTarget,
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["guid"] = entry.Guid,
                ["entityKey"] = entry.EntityKey,
                ["path"] = entry.Path,
                ["module"] = entry.Module,
                ["requestedType"] = typeFilter,
                ["resolutionStrategy"] = strategy ?? "identity+qualified-path",
                ["hint"] = "The search index contains this object, but the active SDK could not resolve its native identity. Refresh the KB/index and verify that the object is persisted in the active model."
            };
        }

        internal JObject GetLastResolutionDiagnostic() => _lastResolutionDiagnostic?.DeepClone() as JObject;

        private static bool IsEntryType(SearchIndex.IndexEntry entry, string type)
        {
            return entry != null && ResolutionTypeMatches(entry.Type, type);
        }

        private static bool IdentityNameMatches(SearchIndex.IndexEntry entry, string target)
        {
            if (entry == null || string.IsNullOrWhiteSpace(target)) return false;
            string value = target.Trim().Replace('\\', '/');
            string path = (entry.Path ?? string.Empty).Trim().Replace('\\', '/');
            string pathWithoutRoot = path.StartsWith("Root Module/", StringComparison.OrdinalIgnoreCase)
                ? path.Substring("Root Module/".Length) : path;
            string dottedPath = pathWithoutRoot.Replace('/', '.');
            return string.Equals(entry.Name, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pathWithoutRoot, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(dottedPath, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals((entry.Module ?? string.Empty) + "/" + entry.Name, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals((entry.Module ?? string.Empty) + "." + entry.Name, value, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> QualifiedIdentityCandidates(SearchIndex.IndexEntry entry)
        {
            var yieldValues = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = value =>
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                string normalized = value.Trim().Replace('\\', '/');
                if (seen.Add(normalized)) yieldValues.Add(normalized);
                string withoutRoot = normalized.StartsWith("Root Module/", StringComparison.OrdinalIgnoreCase)
                    ? normalized.Substring("Root Module/".Length) : normalized;
                if (seen.Add(withoutRoot)) yieldValues.Add(withoutRoot);
                string dotted = withoutRoot.Replace('/', '.');
                if (seen.Add(dotted)) yieldValues.Add(dotted);
            };
            add(entry?.Path);
            add(entry?.ParentPath == null ? null : entry.ParentPath.TrimEnd('/') + "/" + entry.Name);
            add(string.IsNullOrWhiteSpace(entry?.Module) ? null : entry.Module + "/" + entry.Name);
            add(string.IsNullOrWhiteSpace(entry?.Name) ? null : entry.Name);
            return yieldValues;
        }

        internal static bool TryParseEntityKey(string raw, out Guid typeGuid, out int id)
        {
            typeGuid = Guid.Empty;
            id = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string value = raw.Trim();
            bool wrapped = value.StartsWith("EntityKey(", StringComparison.OrdinalIgnoreCase);
            if (wrapped)
            {
                if (!value.EndsWith(")", StringComparison.Ordinal)) return false;
                value = value.Substring("EntityKey(".Length, value.Length - "EntityKey(".Length - 1).Trim();
            }
            var match = System.Text.RegularExpressions.Regex.Match(value,
                @"\A(?<type>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*"
                + (wrapped ? "," : "[:-]") + @"\s*(?<id>[0-9]+)\z");
            return match.Success
                && Guid.TryParse(match.Groups["type"].Value, out typeGuid)
                && int.TryParse(match.Groups["id"].Value, out id);
        }

        internal static string ResolveTargetForIdentity(string target, string guid, string entityKey, string path)
        {
            if (!string.IsNullOrWhiteSpace(target)) return target;
            return !string.IsNullOrWhiteSpace(path) ? path :
                (!string.IsNullOrWhiteSpace(entityKey) ? entityKey : guid);
        }

        internal static bool NormalizeResolutionTarget(ref string target, ref string type, ref string guid)
        {
            target = target?.Trim();
            type = string.IsNullOrWhiteSpace(type) ? null : type.Trim();
            if (target != null && target.Contains(":") && !TryParseEntityKey(target, out _, out _))
            {
                var parts = target.Split(ColonSeparator, 2);
                if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])
                    || !ResolutionTypeMatches(parts[0], type)) return false;
                type = parts[0].Trim();
                target = parts[1].Trim();
            }
            if (Guid.TryParse(target, out var targetGuid))
            {
                if (!string.IsNullOrWhiteSpace(guid)
                    && (!Guid.TryParse(guid, out var explicitGuid) || explicitGuid != targetGuid)) return false;
                guid = targetGuid.ToString();
            }
            return true;
        }

        internal static KBObject ResolveIndexedObject(KBModel model, SearchIndex.IndexEntry entry, out string strategy)
        {
            strategy = null;
            if (model == null || entry == null) return null;

            if (Guid.TryParse(entry.EntityTypeGuid, out var entityTypeGuid) && entry.EntityId.HasValue)
            {
                try
                {
                    var byEntityKey = model.Objects.Get(new global::Artech.Udm.Framework.EntityKey(entityTypeGuid, entry.EntityId.Value));
                    if (byEntityKey != null) { strategy = "entityKey-fields"; return byEntityKey; }
                }
                catch { }
            }

            if (TryParseEntityKey(entry.EntityKey, out entityTypeGuid, out int entityId))
            {
                try
                {
                    var byEntityKey = model.Objects.Get(new global::Artech.Udm.Framework.EntityKey(entityTypeGuid, entityId));
                    if (byEntityKey != null) { strategy = "entityKey"; return byEntityKey; }
                }
                catch { }
            }

            if (Guid.TryParse(entry.Guid, out var objectGuid))
            {
                try
                {
                    var byGuid = model.Objects.Get(objectGuid);
                    if (byGuid != null) { strategy = "guid"; return byGuid; }
                }
                catch { }
            }

            foreach (var candidate in QualifiedIdentityCandidates(entry))
            {
                var byPath = ResolveTypedObjectDirect(model, entry.Type, candidate);
                if (byPath != null)
                {
                    strategy = "qualified-path";
                    return byPath;
                }
            }

            return null;
        }

        internal KBObject FindObject(SearchIndex.IndexEntry entry)
        {
            _lastResolutionDiagnostic = null;
            var kb = _kbService.GetKB();
            if (kb == null || entry == null) return null;
            string strategy;
            var resolved = ResolveIndexedObject(kb.DesignModel, entry, out strategy);
            if (resolved == null) SetIndexedUnavailable(entry, entry.Name, entry.Type, strategy);
            return resolved;
        }

        private static SearchIndex.IndexEntry FindIndexEntry(SearchIndex index, string target, string type)
        {
            if (index?.Objects == null || string.IsNullOrWhiteSpace(target)) return null;
            string key = string.IsNullOrWhiteSpace(type) ? null : type.Trim() + ":" + target.Trim();
            if (key != null && index.Objects.TryGetValue(key, out var exact)) return exact;

            if (index.ByNameIndex != null)
            {
                string simpleName = target.Trim();
                int lastSlash = Math.Max(simpleName.LastIndexOf('/'), simpleName.LastIndexOf('.'));
                if (lastSlash >= 0 && lastSlash < simpleName.Length - 1)
                {
                    simpleName = simpleName.Substring(lastSlash + 1);
                }

                if (index.ByNameIndex.TryGetValue(simpleName, out var keys))
                {
                    foreach (var k in keys)
                    {
                        if (index.Objects.TryGetValue(k, out var candidate) && IsEntryType(candidate, type) && IdentityNameMatches(candidate, target))
                        {
                            return candidate;
                        }
                    }
                }
            }

            return index.Objects.Values.FirstOrDefault(e => IsEntryType(e, type) && IdentityNameMatches(e, target));
        }

        public KBObject FindObject(string target, string typeFilter = null, string guid = null, string entityKey = null, string path = null)
        {
            _lastResolutionDiagnostic = null;
            if (string.IsNullOrWhiteSpace(target))
            {
                if (string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(entityKey) && string.IsNullOrWhiteSpace(path))
                    return null;
                target = ResolveTargetForIdentity(target, guid, entityKey, path);
            }
            // Parse name syntax once before every resolution strategy, including
            // explicit identities; conflicting selectors must not select a homonym.
            if (!NormalizeResolutionTarget(ref target, ref typeFilter, ref guid)) return null;
            var sw = Stopwatch.StartNew();
            var kb = _kbService.GetKB();
            if (kb == null) return null;

            // Explicit identity is authoritative. Do not silently fall back to a
            // same-named object when a caller supplied a GUID/EntityKey/path.
            if (!string.IsNullOrWhiteSpace(guid) || !string.IsNullOrWhiteSpace(entityKey))
            {
                try
                {
                    KBObject resolved = null;
                    if (!string.IsNullOrWhiteSpace(guid))
                    {
                        if (!Guid.TryParse(guid, out var explicitGuid)) return null;
                        resolved = kb.DesignModel.Objects.Get(explicitGuid);
                    }
                    if (!string.IsNullOrWhiteSpace(entityKey))
                    {
                        if (!TryParseEntityKey(entityKey, out var explicitTypeGuid, out int explicitId)) return null;
                        var byKey = kb.DesignModel.Objects.Get(new global::Artech.Udm.Framework.EntityKey(explicitTypeGuid, explicitId));
                        if (byKey == null || (!string.IsNullOrWhiteSpace(guid) && (resolved == null || byKey.Guid != resolved.Guid))) return null;
                        resolved = byKey;
                    }
                    return ResolutionTypeMatches(resolved, typeFilter) ? resolved : null;
                }
                catch { return null; }
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                var loadedIndex = GetLoadedIndexOrNull();
                var identityEntry = FindIndexEntry(loadedIndex, path, typeFilter);
                if (identityEntry != null)
                {
                    var probe = new SearchIndex.IndexEntry
                    {
                        Guid = identityEntry.Guid,
                        EntityKey = identityEntry.EntityKey,
                        EntityTypeGuid = identityEntry.EntityTypeGuid,
                        EntityId = identityEntry.EntityId,
                        Name = identityEntry.Name,
                        Type = identityEntry.Type,
                        Path = path.Trim(),
                        ParentPath = identityEntry.ParentPath,
                        Module = identityEntry.Module
                    };
                    string resolvedBy;
                    var resolved = ResolveIndexedObject(kb.DesignModel, probe, out resolvedBy);
                    if (ResolutionTypeMatches(resolved, typeFilter)) return resolved;
                    SetIndexedUnavailable(probe, target, typeFilter, resolvedBy);
                    return null;
                }

                foreach (var candidate in QualifiedIdentityCandidates(new SearchIndex.IndexEntry
                {
                    Type = typeFilter,
                    Path = path,
                    Module = null
                }))
                {
                    var resolved = ResolveTypedObjectDirect(kb.DesignModel, typeFilter, candidate);
                    if (resolved != null) return resolved;
                }
                return null;
            }

            string typePart = typeFilter;
            string namePart = target.Trim();

            // Domains are native typed entities but are not reliably returned by the
            // generic DesignModel.Objects name index. Resolve them through the SDK's
            // own Domain identity API before consulting the generic index/fallback.
            if (string.Equals(typePart, "Domain", StringComparison.OrdinalIgnoreCase))
            {
                string domainName = NormalizeDomainLookupName(namePart);
                var domain = VariableInjector.ResolveDomain(kb.DesignModel, domainName);
                if (domain != null)
                {
                    Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Native-Domain) in {1}ms", target, sw.ElapsedMilliseconds));
                    return domain;
                }
            }

            // Direct typed probe: if a specific type is requested, probe the SDK directly (<1ms)
            if (typePart != null)
            {
                var directTyped = ResolveTypedObjectDirect(kb.DesignModel, typePart, namePart);
                if (directTyped != null)
                {
                    Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Direct-Typed-Static: {1}) in {2}ms", target, typePart, sw.ElapsedMilliseconds));
                    return directTyped;
                }

                Guid directTypeGuid = ResolveObjectTypeGuid(typePart);
                if (directTypeGuid != Guid.Empty)
                {
                    try
                    {
                        var directObj = kb.DesignModel.Objects.Get(directTypeGuid, namePart);
                        if (directObj != null)
                        {
                            Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Direct-Typed: {1}) in {2}ms", target, typePart, sw.ElapsedMilliseconds));
                            return directObj;
                        }
                    }
                    catch { }
                }
            }
            else
            {
                // Direct untyped probe via native SDK ObjectNameHelper (<1ms)
                var directUntyped = ResolveTypedObjectDirect(kb.DesignModel, null, namePart);
                if (directUntyped != null)
                {
                    Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Direct-Untyped-Helper) in {1}ms", target, sw.ElapsedMilliseconds));
                    return directUntyped;
                }

                // Fast direct probe on primary logic types using native SDK static Get methods (<1ms)
                for (int i = 0; i < DirectCandidates.Length; i++)
                {
                    var directTyped = ResolveTypedObjectDirect(kb.DesignModel, DirectCandidates[i], namePart);
                    if (directTyped != null)
                    {
                        Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Direct-Probe-Static: {1}) in {2}ms", target, DirectCandidates[i], sw.ElapsedMilliseconds));
                        return directTyped;
                    }
                }
            }

            // 1. FAST PATH: Use Search Index — non-blocking. If the index hasn't been
            // loaded yet we DON'T cold-load it here (that blocks the shared STA thread
            // 30-60s and stalls every queued tool call); we fall through to the SDK's
            // own name index below, which is fast and also sees not-yet-indexed objects.
            var index = GetLoadedIndexOrNull();
            if (index != null && index.Objects != null)
            {
                if (typePart != null)
                {
                    string key = string.Format("{0}:{1}", typePart, namePart);
                    var entry = FindIndexEntry(index, namePart, typePart);
                    if (entry != null)
                    {
                        string resolvedBy;
                        KBObject obj = ResolveIndexedObject(kb.DesignModel, entry, out resolvedBy);
                        if (ResolutionTypeMatches(obj, typePart)) {
                            Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Index-Typed) in {1}ms", target, sw.ElapsedMilliseconds));
                            return obj;
                        }
                        SetIndexedUnavailable(entry, target, typePart, resolvedBy);
                    }
                }
                else
                {
                    // Global search in index
                    // OPTIMIZATION: Prioritize logic types if no filter is provided
                    var matches = new List<SearchIndex.IndexEntry>();
                    for (int i = 0; i < CandidateIndexTypes.Length; i++)
                    {
                        string candKey = CandidateIndexTypes[i] + ":" + namePart;
                        if (index.Objects.TryGetValue(candKey, out var directEntry))
                        {
                            matches.Add(directEntry);
                        }
                    }

                    if (matches.Count == 0)
                    {
                        // PERFORMANCE (perf-review): resolve candidates through the
                        // derived ByNameIndex multimap (name → storage keys) instead of
                        // scanning every ~38k index entry. Same pattern SearchService's
                        // usedby filter already uses. Falls back to the full scan only
                        // when the index hasn't built ByNameIndex yet (LoadFromEntries
                        // test seam / older in-memory indexes).
                        if (index.ByNameIndex != null
                            && index.ByNameIndex.TryGetValue(namePart, out var nameKeys))
                        {
                            if (nameKeys != null)
                            {
                                lock (nameKeys)
                                {
                                    foreach (var key in nameKeys)
                                    {
                                        if (!index.Objects.TryGetValue(key, out var entry) || entry == null) continue;
                                        matches.Add(entry);
                                    }
                                }
                            }
                        }
                        else
                        {
                            foreach (var kv in index.Objects)
                            {
                                var entry = kv.Value;
                                if (IdentityNameMatches(entry, namePart) ||
                                    kv.Key.EndsWith(":" + namePart, StringComparison.OrdinalIgnoreCase))
                                {
                                    matches.Add(entry);
                                }
                            }
                        }
                    }

                    if (matches.Count > 0)
                    {
                        if (matches.Count > 1)
                        {
                            matches.Sort(CompareMatches);
                        }

                        foreach (var m in matches)
                        {
                            string resolvedBy;
                            KBObject obj = ResolveIndexedObject(kb.DesignModel, m, out resolvedBy);
                            if (obj != null)
                            {
                                Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Index-Ordered: {1}) in {2}ms", target, m.Type, sw.ElapsedMilliseconds));
                                return obj;
                            }
                            SetIndexedUnavailable(m, target, typePart, resolvedBy);
                        }
                    }
                }
            }

            // 2. SLOW PATH: Fallback to SDK GetByName (for safety with new objects not yet indexed)
            // If the index wasn't loaded, kick off the background warm so subsequent
            // lookups hit the fast path — idempotent, fire-and-forget, never blocks.
            if (index == null) { try { _kbService.GetIndexCache().EnsureLoadStarted(); } catch { /* best-effort */ } }
            if (typePart != null)
            {
                Guid typeGuid = ResolveObjectTypeGuid(typePart);
                if (typeGuid != Guid.Empty)
                {
                    try
                    {
                        var directObj = kb.DesignModel.Objects.Get(typeGuid, namePart);
                        if (directObj != null) return directObj;
                    }
                    catch { }

                    try
                    {
                        var desc = KBObjectDescriptor.Get(typeGuid);
                        if (desc != null)
                        {
                            var sdkMatchesDesc = kb.DesignModel.Objects.GetByName(null, desc, namePart);
                            foreach (KBObject o in sdkMatchesDesc) return o;
                        }
                    }
                    catch { }
                }

                var sdkMatchesTyped = kb.DesignModel.Objects.GetByName(null, null, namePart);
                foreach (KBObject obj in sdkMatchesTyped)
                {
                    if (ResolutionTypeMatches(obj, typePart))
                    {
                        Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Typed-SDK) in {1}ms", target, sw.ElapsedMilliseconds));
                        return obj;
                    }
                }
                return null;
            }

            // Global search without type filter:
            // 1. Direct typed probes across primary logic types (fastest & most reliable across modules)
            string[] probeCandidateTypes = { "Procedure", "Transaction", "WebPanel", "SDT", "DataProvider", "DataSelector", "Domain", "API" };
            foreach (var cand in probeCandidateTypes)
            {
                Guid candGuid = ResolveObjectTypeGuid(cand);
                if (candGuid != Guid.Empty)
                {
                    try
                    {
                        var directObj = kb.DesignModel.Objects.Get(candGuid, namePart);
                        if (directObj != null)
                        {
                            Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Probe-Direct: {1}) in {2}ms", target, cand, sw.ElapsedMilliseconds));
                            return directObj;
                        }
                    }
                    catch { }

                    try
                    {
                        var desc = KBObjectDescriptor.Get(candGuid);
                        if (desc != null)
                        {
                            var sdkMatchesDesc = kb.DesignModel.Objects.GetByName(null, desc, namePart);
                            foreach (KBObject o in sdkMatchesDesc)
                            {
                                Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Probe-Desc: {1}) in {2}ms", target, cand, sw.ElapsedMilliseconds));
                                return o;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2. Try ObjectNameHelper (resolves Transactions, Procedures, WebPanels, SDTs across modules)
            try
            {
                var helperObj = global::Artech.Architecture.Common.Helpers.ObjectNameHelper.Get(kb.DesignModel, namePart) as KBObject;
                if (helperObj != null) return helperObj;
            }
            catch { }

            // 3. Fallback to generic GetByName
            var sdkMatches = kb.DesignModel.Objects.GetByName(null, null, namePart);
            KBObject firstPrimaryLogic = null;
            KBObject firstLogicMatch = null;
            KBObject firstMatch = null;

            foreach (KBObject obj in sdkMatches)
            {
                if (firstMatch == null) firstMatch = obj;

                string type = obj.TypeDescriptor?.Name;
                if (type != "Folder" && type != "Module" && type != "File" && type != "Image")
                {
                    if (firstLogicMatch == null) firstLogicMatch = obj;
                    if (firstPrimaryLogic == null && !IsGeneratedPhysical(type)) firstPrimaryLogic = obj;
                }
            }

            var result = firstPrimaryLogic ?? firstLogicMatch ?? firstMatch;
            if (result != null)
            {
                Logger.Debug(string.Format("FindObject '{0}' SUCCESS (SDK-Fallback) in {1}ms", target, sw.ElapsedMilliseconds));
                return result;
            }

            return null;
        }

        internal static string NormalizeDomainLookupName(string name)
        {
            string value = (name ?? string.Empty).Trim().Replace('\\', '/');
            const string rootPrefix = "Root Module/";
            if (value.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return value.Substring(rootPrefix.Length);
            return value.IndexOf('/') >= 0 ? value.Replace('/', '.') : value;
        }

        internal KBObject FindObjectFresh(string target, string typeFilter = null)
        {
            var seed = FindObject(target, typeFilter);
            if (seed == null) return null;

            MarkReadCacheDirty(seed);
            var kb = _kbService.GetKB();
            try
            {
                var byEntityKey = kb?.DesignModel.Objects.Get(seed.Key);
                if (byEntityKey != null) return byEntityKey;
            }
            catch { }
            try { return kb?.DesignModel.Objects.Get(seed.Guid); } catch { return seed; }
        }

        private string FormatReadNotFound(string target)
        {
            var diagnostic = GetLastResolutionDiagnostic();
            if (diagnostic != null)
            {
                return McpResponse.Err(
                    code: "IndexedObjectUnavailable",
                    message: "The search index contains the object, but the active SDK could not resolve its native identity.",
                    hint: diagnostic["hint"]?.ToString(),
                    target: target,
                    errorExtra: diagnostic);
            }
            return HealingService.FormatNotFoundError(target, GetLoadedIndexOrNull());
        }

        internal JObject BuildObjectIdentity(KBObject obj)
        {
            var identity = new JObject();
            if (obj == null) return identity;
            string guid = null;
            string entityKey = null;
            try { guid = obj.Guid.ToString(); identity["guid"] = guid; } catch { }
            try
            {
                if (obj.Key != null)
                {
                    entityKey = obj.Key.ToString();
                    identity["entityKey"] = entityKey;
                    identity["entityTypeGuid"] = obj.Key.Type.ToString();
                    identity["entityId"] = obj.Key.Id;
                }
            }
            catch { }

            // Keep read/inspect identity aligned with list/search entries. The
            // native object is authoritative, while the index supplies the
            // qualified path for modular handles whose parent chain is partial.
            try
            {
                var index = _kbService?.GetIndexCache()?.TryGetLoadedIndex();
                SearchIndex.IndexEntry entry = null;
                if (!string.IsNullOrEmpty(guid) && index?.GuidToKey != null && index.GuidToKey.TryGetValue(guid, out var key))
                {
                    index.Objects?.TryGetValue(key, out entry);
                }
                if (entry == null && index != null && !string.IsNullOrEmpty(obj.Name) && index.ByNameIndex != null && index.ByNameIndex.TryGetValue(obj.Name, out var candidateKeys))
                {
                    foreach (var cKey in candidateKeys)
                    {
                        if (index.Objects != null && index.Objects.TryGetValue(cKey, out var candidate))
                        {
                            if (!string.IsNullOrEmpty(guid) && string.Equals(candidate.Guid, guid, StringComparison.OrdinalIgnoreCase))
                            {
                                entry = candidate;
                                break;
                            }
                            if (!string.IsNullOrEmpty(entityKey) && string.Equals(candidate.EntityKey, entityKey, StringComparison.OrdinalIgnoreCase))
                            {
                                entry = candidate;
                                break;
                            }
                        }
                    }
                }
                if (entry == null)
                {
                    entry = index?.Objects?.Values?.FirstOrDefault(e =>
                        (!string.IsNullOrEmpty(guid) && string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(entityKey) && string.Equals(e.EntityKey, entityKey, StringComparison.OrdinalIgnoreCase)));
                }

                if (!string.IsNullOrWhiteSpace(entry?.Path))
                {
                    identity["path"] = entry.Path;
                }
                else
                {
                    var hierarchy = _kbService?.GetIndexCache()?.ResolveHierarchyForIndex(obj);
                    if (hierarchy.HasValue && !string.IsNullOrWhiteSpace(hierarchy.Value.Path)) identity["path"] = hierarchy.Value.Path;
                }
            }
            catch { }
            return identity;
        }

        public string ExtractAllParts(string target, string client = "ide", string typeFilter = null,
            string guid = null, string entityKey = null, string path = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                target = ResolveTargetForIdentity(target, guid, entityKey, path);
                var obj = FindObject(target, typeFilter, guid, entityKey, path);
                if (obj == null) return FormatReadNotFound(target);

                var result = new JObject
                {
                    ["name"] = obj.Name,
                    ["identity"] = BuildObjectIdentity(obj),
                    ["parts"] = new JObject()
                };
                string[] partsToFetch = obj is Artech.Packages.Patterns.Objects.PatternSettings
                    ? new[] { "PatternSettings" } : DefaultPartsToFetch;

                foreach (var pName in partsToFetch)
                {
                    string partJson = ReadObjectSourceInternal(obj, pName, null, null, client);
                    try {
                        var pObj = JObject.Parse(partJson);
                        if (obj is Artech.Packages.Patterns.Objects.PatternSettings || pObj["source"] != null)
                        {
                            ((JObject)result["parts"])[pName] = obj is Artech.Packages.Patterns.Objects.PatternSettings ? pObj : pObj["source"];
                        }
                    } catch { }
                }

                Logger.Info(string.Format("ExtractAllParts for {0} complete in {1}ms", obj.Name, sw.ElapsedMilliseconds));
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ReadObject(string target, string typeFilter = null,
            string guid = null, string entityKey = null, string path = null)
        {
            target = ResolveTargetForIdentity(target, guid, entityKey, path);
            var obj = FindObject(target, typeFilter, guid, entityKey, path);
            if (obj == null) return FormatReadNotFound(target);

            var parts = new JArray();
            foreach (KBObjectPart p in obj.Parts)
            {
                parts.Add(new JObject {
                    ["name"] = p.TypeDescriptor?.Name ?? p.Type.ToString(),
                    ["guid"] = p.Type.ToString()
                });
            }
            if (obj is Artech.Genexus.Common.Objects.API api && api.ServiceGroupSource != null
                && !parts.OfType<JObject>().Any(p => string.Equals(p["name"]?.ToString(), "Methods", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(new JObject
                {
                    ["name"] = "Methods",
                    ["guid"] = api.ServiceGroupSource.Type.ToString()
                });
            }

            string parentName = null;
            string moduleName = null;

            try { parentName = obj.Parent?.Name; } catch { }
            try { moduleName = obj.Module?.Name; } catch { }

            // v2.8.0 — availableParts proactive on success. LLMs no longer
            // need to hit a PartNotFound error to learn the object's shape;
            // the part list is already on the first read. Mirrors the same
            // field name the error envelope carries so a dumb LLM uses one
            // accessor regardless of outcome.
            var availableParts = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);

            return Models.McpResponse.Ok(
                target: obj.Name,
                code: "ObjectRead",
                result: new JObject
                {
                    ["name"] = obj.Name,
                    ["type"] = obj.TypeDescriptor?.Name,
                    ["identity"] = BuildObjectIdentity(obj),
                    ["parent"] = parentName,
                    ["module"] = moduleName,
                    ["parts"] = parts,
                    ["availableParts"] = new JArray(availableParts)
                });
        }

        public string ReadObjectSource(string target, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false, string typeFilter = null,
            string guid = null, string entityKey = null, string path = null)
        {
            target = ResolveTargetForIdentity(target, guid, entityKey, path);
            var obj = FindObject(target, typeFilter, guid, entityKey, path);
            if (obj == null) return FormatReadNotFound(target);

            string resolvedPart = ResolvePartName(obj, partName);
            if (!(obj is Artech.Packages.Patterns.Objects.PatternSettings) && ShouldUseReadCache(client, minimize))
            {
                string cacheKey = BuildReadCacheKey(obj.Guid, resolvedPart, offset, limit, client, minimize);
                if (TryGetReadCache(cacheKey, out string cachedPayload))
                {
                    return cachedPayload;
                }

                string payload = ReadObjectSourceInternal(obj, resolvedPart, offset, limit, client, minimize);
                if (TryGetCacheablePayload(payload, out JObject parsedPayload))
                {
                    SetReadCache(cacheKey, payload);
                    // A full MCP read already paid the SDK source round-trip. Seed the
                    // raw-source cache as well so a subsequent search_source call can
                    // reuse the exact text instead of resolving and reading every
                    // candidate a second time.
                    bool cachedRawSource = CacheRawSourceFromReadPayload(
                        obj.Guid, resolvedPart, parsedPayload, offset, client, minimize);
                    if (cachedRawSource)
                    {
                        // Persist only the same bounded, complete source accepted by the
                        // raw cache. The helper uses the already-loaded index and never
                        // forces a cold index load from the read hot path.
                        TryPromoteCompleteSourceRead(
                            obj.Guid, resolvedPart, parsedPayload, offset, client, minimize);
                    }
                }

                return payload;
            }

            return ReadObjectSourceInternal(obj, resolvedPart, offset, limit, client, minimize);
        }

        /// <summary>
        /// Performs a fresh, complete read for post-write verification. This deliberately
        /// bypasses the MCP read cache, minimization, and the implicit 200-line/16-KB page.
        /// A verification read must compare the complete persisted part with the complete
        /// request; a client-facing, context-budgeted projection is not evidence of a
        /// persistence mismatch.
        /// </summary>
        internal string ReadObjectSourceForVerification(string target, string partName, string typeFilter = null)
        {
            var obj = FindObjectFresh(target, typeFilter);
            if (obj == null) return FormatReadNotFound(target);

            string resolvedPart = ResolvePartName(obj, partName);
            string response = ReadObjectSourceInternal(
                obj,
                resolvedPart,
                offset: 0,
                limit: 0,
                client: "mcp",
                minimize: false);
            try
            {
                var payload = JObject.Parse(response);
                payload["verificationSource"] = "fresh-sdk-read";
                return payload.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return response; }
        }

        /// <summary>
        /// Read multiple named parts of an object in one call.
        /// Returns a JSON object: { name, type, parts: { Source: "...", Variables: "..." } }
        /// Parts that are not found or produce no source are silently omitted.
        /// When requestedParts is null/empty the full default set is returned (backward-compatible).
        /// </summary>
        public string ReadObjectSourceParts(string target, IEnumerable<string> requestedParts, string typeFilter = null,
            string guid = null, string entityKey = null, string path = null)
        {
            target = ResolveTargetForIdentity(target, guid, entityKey, path);
            var obj = FindObject(target, typeFilter, guid, entityKey, path);
            if (obj == null) return FormatReadNotFound(target);

            if (DataSelectorReadService.IsDataSelector(obj))
            {
                return DataSelectorReadService.Read((DataSelector)obj, requestedParts);
            }

            string[] partsToFetch = (requestedParts != null && requestedParts.Any())
                ? requestedParts.Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray()
                : obj is Artech.Packages.Patterns.Objects.PatternSettings ? new[] { "PatternSettings" } : DefaultPartsToFetch;

            var partsObj = new JObject();
            foreach (var pName in partsToFetch)
            {
                try
                {
                    string partJson = ReadObjectSourceInternal(obj, pName, null, null, "mcp", false);
                    var pObj = JObject.Parse(partJson);
                    if (obj is Artech.Packages.Patterns.Objects.PatternSettings || pObj["source"] != null)
                        partsObj[pName] = obj is Artech.Packages.Patterns.Objects.PatternSettings ? pObj : pObj["source"];
                    else if (pObj["error"] == null)
                        // For XML/binary parts, include the raw response
                        partsObj[pName] = pObj;
                }
                catch { /* skip parts that error */ }
            }

            return new JObject
            {
                ["name"] = obj.Name,
                ["type"] = obj.TypeDescriptor?.Name,
                ["identity"] = BuildObjectIdentity(obj),
                ["parts"] = partsObj
            }.ToString();
        }

        private string ReadPartTextSafe(KBObject obj, string partName)
        {
            try
            {
                string resolvedPart = ResolvePartName(obj, partName);
                string json = ReadObjectSourceInternal(obj, resolvedPart, null, null, "mcp", false);
                if (string.IsNullOrEmpty(json)) return null;
                var parsed = JObject.Parse(json);
                if (parsed["source"] != null) return parsed["source"].ToString();
                if (parsed["error"] == null && parsed["contentType"] != null) return json;
            }
            catch { }
            return null;
        }

        public JArray GetVariablesCompact(KBObject obj, string referencedSource = null)
        {
            var varsArr = new JArray();
            if (obj == null) return varsArr;

            try
            {
                var varPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj);
                if (varPart != null)
                {
                    dynamic p = varPart;
                    foreach (var v in p.Variables)
                    {
                        dynamic dv = v;
                        string vName = null;
                        try { vName = (string)dv.Name; } catch { }
                        if (string.IsNullOrEmpty(vName)) continue;

                        bool isStandard = false;
                        try { isStandard = (bool)dv.IsStandard; } catch { }

                        // Prune SDK built-in variables that are not referenced in the object's code
                        if (isStandard)
                        {
                            if (string.IsNullOrEmpty(referencedSource) ||
                                referencedSource.IndexOf("&" + vName, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }
                        }

                        string typeStr = "Unknown";
                        try { typeStr = dv.Type?.ToString(); } catch { }

                        int length = 0;
                        try { length = Convert.ToInt32(dv.Length); } catch { }

                        int decimals = 0;
                        try { decimals = Convert.ToInt32(dv.Decimals); } catch { }

                        bool isColl = false;
                        try { isColl = (bool)dv.IsCollection; } catch { }

                        string domainName = null;
                        try { domainName = (string)dv.Domain?.Name ?? (string)dv.Attribute?.Domain?.Name; } catch { }

                        string sdtName = null;
                        try
                        {
                            if (string.Equals(typeStr, "GX_SDT", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(typeStr, "SDT", StringComparison.OrdinalIgnoreCase))
                            {
                                sdtName = (string)dv.DataType?.ToString();
                            }
                        }
                        catch { }

                        var vObj = new JObject
                        {
                            ["name"] = vName.StartsWith("&") ? vName : "&" + vName,
                            ["type"] = typeStr
                        };
                        if (length > 0) vObj["length"] = length;
                        if (decimals > 0) vObj["decimals"] = decimals;
                        if (isColl) vObj["isCollection"] = true;
                        if (!string.IsNullOrEmpty(domainName)) vObj["domain"] = domainName;
                        if (!string.IsNullOrEmpty(sdtName)) vObj["sdt"] = sdtName;

                        varsArr.Add(vObj);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("GetVariablesCompact error: " + ex.Message);
            }

            return varsArr;
        }

        /// <summary>
        /// SOTA 1-roundtrip object read: delivers all authored parts, rules, variables,
        /// and called signatures tailored to the target object type in a single fast call.
        /// Eliminates the multi-roundtrip exploration loop.
        /// </summary>
        public string ReadFullObject(string target, string typeFilter = null,
            string guid = null, string entityKey = null, string path = null)
        {
            target = ResolveTargetForIdentity(target, guid, entityKey, path);
            var obj = FindObject(target, typeFilter, guid, entityKey, path);
            if (obj == null) return FormatReadNotFound(target);

            if (obj is Artech.Packages.Patterns.Objects.PatternSettings)
                return new JObject
                {
                    ["name"] = obj.Name, ["type"] = obj.TypeDescriptor?.Name,
                    ["identity"] = BuildObjectIdentity(obj), ["explicitFullRead"] = true,
                    ["parts"] = new JObject { ["PatternSettings"] = JObject.Parse(ReadObjectSourceInternal(obj, "PatternSettings", 0, 0, "mcp")) }
                }.ToString();

            string typeName = obj.TypeDescriptor?.Name ?? "Object";
            var result = new JObject
            {
                ["name"] = obj.Name,
                ["type"] = typeName,
                ["identity"] = BuildObjectIdentity(obj)
            };

            string parentName = null;
            string moduleName = null;
            try { parentName = obj.Parent?.Name; } catch { }
            try { moduleName = obj.Module?.Name; } catch { }
            if (!string.IsNullOrEmpty(parentName)) result["parent"] = parentName;
            if (!string.IsNullOrEmpty(moduleName)) result["module"] = moduleName;

            var parts = new JObject();
            string combinedCode = "";

            if (typeName.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
            {
                string rules = ReadPartTextSafe(obj, "Rules");
                if (!string.IsNullOrWhiteSpace(rules))
                {
                    parts["rules"] = rules;
                    combinedCode += "\n" + rules;
                }

                string source = ReadPartTextSafe(obj, "Source");
                if (!string.IsNullOrWhiteSpace(source))
                {
                    parts["source"] = source;
                    combinedCode += "\n" + source;
                }

                string conditions = ReadPartTextSafe(obj, "Conditions");
                if (!string.IsNullOrWhiteSpace(conditions))
                {
                    parts["conditions"] = conditions;
                    combinedCode += "\n" + conditions;
                }
            }
            else if (typeName.Equals("WebPanel", StringComparison.OrdinalIgnoreCase) ||
                     typeName.Equals("WebComponent", StringComparison.OrdinalIgnoreCase) ||
                     typeName.Equals("SDPanel", StringComparison.OrdinalIgnoreCase) ||
                     typeName.Equals("Dashboard", StringComparison.OrdinalIgnoreCase) ||
                     typeName.Equals("Prompt", StringComparison.OrdinalIgnoreCase))
            {
                string rules = ReadPartTextSafe(obj, "Rules");
                if (!string.IsNullOrWhiteSpace(rules))
                {
                    parts["rules"] = rules;
                    combinedCode += "\n" + rules;
                }

                string events = ReadPartTextSafe(obj, "Events");
                if (string.IsNullOrWhiteSpace(events)) events = ReadPartTextSafe(obj, "SDEEvents");
                if (!string.IsNullOrWhiteSpace(events))
                {
                    parts["events"] = events;
                    combinedCode += "\n" + events;
                }

                string conditions = ReadPartTextSafe(obj, "Conditions");
                if (!string.IsNullOrWhiteSpace(conditions))
                {
                    parts["conditions"] = conditions;
                    combinedCode += "\n" + conditions;
                }

                try
                {
                    if (_uiService != null)
                    {
                        var uiStruct = _uiService.GetSimplifiedUIStructure(obj);
                        if (uiStruct != null && uiStruct.Count > 0) result["uiStructure"] = uiStruct;
                    }
                }
                catch { }
            }
            else if (typeName.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
            {
                string structDsl = ReadPartTextSafe(obj, "Structure");
                if (!string.IsNullOrWhiteSpace(structDsl)) parts["structure"] = structDsl;

                string rules = ReadPartTextSafe(obj, "Rules");
                if (!string.IsNullOrWhiteSpace(rules))
                {
                    parts["rules"] = rules;
                    combinedCode += "\n" + rules;
                }

                string events = ReadPartTextSafe(obj, "Events");
                if (!string.IsNullOrWhiteSpace(events))
                {
                    parts["events"] = events;
                    combinedCode += "\n" + events;
                }

                try
                {
                    dynamic trn = obj;
                    result["isBusinessComponent"] = (bool)trn.IsBusinessComponent;
                }
                catch { }
            }
            else if (typeName.Equals("Table", StringComparison.OrdinalIgnoreCase))
            {
                string structDsl = ReadPartTextSafe(obj, "Structure");
                if (!string.IsNullOrWhiteSpace(structDsl)) parts["structure"] = structDsl;
            }
            else if (typeName.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                string structDsl = ReadPartTextSafe(obj, "Structure");
                if (!string.IsNullOrWhiteSpace(structDsl)) parts["structure"] = structDsl;

                try
                {
                    dynamic sdt = obj;
                    result["isCollection"] = (bool)sdt.IsCollection;
                    if (!string.IsNullOrEmpty((string)sdt.CollectionItemName))
                        result["collectionItemName"] = (string)sdt.CollectionItemName;
                }
                catch { }
            }
            else if (typeName.Equals("DataSelector", StringComparison.OrdinalIgnoreCase))
            {
                return DataSelectorReadService.Read((DataSelector)obj, null);
            }
            else if (typeName.Equals("API", StringComparison.OrdinalIgnoreCase))
            {
                string methods = ReadPartTextSafe(obj, "Methods");
                if (!string.IsNullOrWhiteSpace(methods))
                {
                    parts["methods"] = methods;
                    combinedCode += "\n" + methods;
                }

                // Keep the API's existing metadata/variables parts in the full
                // read; Methods is an additional native part, not a replacement.
                foreach (var p in GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj))
                {
                    if (string.Equals(p, "Methods", StringComparison.OrdinalIgnoreCase)) continue;
                    string src = ReadPartTextSafe(obj, p);
                    if (!string.IsNullOrWhiteSpace(src))
                    {
                        parts[p] = src;
                        combinedCode += "\n" + src;
                    }
                }
            }
            else
            {
                var availParts = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                foreach (var p in availParts)
                {
                    string src = ReadPartTextSafe(obj, p);
                    if (!string.IsNullOrWhiteSpace(src))
                    {
                        parts[p] = src;
                        combinedCode += "\n" + src;
                    }
                }
            }

            result["parts"] = parts;

            // Signature
            try
            {
                var (parmRule, parms) = GetParametersInternal(obj);
                if (!string.IsNullOrEmpty(parmRule))
                {
                    result["signature"] = parmRule;
                }
            }
            catch { }

            // Variables (with SDK standard variables pruned unless referenced in combined code)
            var vars = GetVariablesCompact(obj, combinedCode);
            if (vars != null && vars.Count > 0) result["variables"] = vars;

            // Called signatures
            if (!string.IsNullOrWhiteSpace(combinedCode))
            {
                var callsResult = new JObject();
                AddCallSignatures(obj, combinedCode, callsResult);
                if (callsResult["calls"] != null) result["calledSignatures"] = callsResult["calls"];
            }

            var avail = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
            if (avail != null && avail.Length > 0) result["availableParts"] = new JArray(avail);

            return Models.McpResponse.Ok(target: obj.Name, code: "FullObjectRead", result: result);
        }

        // Managed cache only: safe even when an interrupted SDK save poisoned the
        // session. Strict persisted-state verification uses the public SDK cache API.
        internal static void InvalidateAllReadCaches()
        {
            _readCache.Clear();
            _largeRawSourceCache.Clear();
            _emptyRawSourceCache.Clear();
        }

        public void MarkReadCacheDirty(KBObject obj, string partName = null)
        {
            if (obj == null)
            {
                return;
            }

            // B13: the read cache is keyed by the RESOLVED part name (ResolvePartName,
            // e.g. "SDTStructure" -> "Structure"), but this was matching on the raw
            // partName. When they differed the dirty-mark missed the live cache entry,
            // so the post-write re-read returned stale content and the write was falsely
            // reported changed:false / WriteNoChange even though the diff and disk showed
            // the change. Resolve the part name the same way ReadObjectSource does.
            string resolvedPart = string.IsNullOrWhiteSpace(partName) ? null : ResolvePartName(obj, partName);
            string normalizedPart = string.IsNullOrWhiteSpace(resolvedPart) ? null : resolvedPart.Trim().ToLowerInvariant();
            string objectPrefix = obj.Guid.ToString("N") + "|";
            foreach (var kvp in _readCache)
            {
                string key = kvp.Key;
                if (!key.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalizedPart != null && !key.StartsWith(objectPrefix + normalizedPart + "|", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _readCache.TryRemove(key, out _);
            }

            foreach (var kvp in _largeRawSourceCache)
            {
                string key = kvp.Key;
                if (!key.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (normalizedPart != null
                    && !key.StartsWith(objectPrefix + normalizedPart + "|", StringComparison.OrdinalIgnoreCase))
                    continue;
                _largeRawSourceCache.TryRemove(key, out _);
            }

            foreach (var kvp in _emptyRawSourceCache)
            {
                string key = kvp.Key;
                if (!key.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (normalizedPart != null
                    && !key.StartsWith(objectPrefix + normalizedPart + "|", StringComparison.OrdinalIgnoreCase))
                    continue;
                _emptyRawSourceCache.TryRemove(key, out _);
            }

            // SDK object cache invalidation is expensive; do it only after writes.
            InvalidateCache(obj);
        }

        // PERFORMANCE (perf round 1): raw part-source getter used by SourceSearchService's
        // full-KB scans. Routes through the SAME _readCache the JSON read path uses, so the
        // existing write-invalidation above (which removes every "<guid>|…" key) invalidates
        // these entries for free — a full source scan never re-reads unchanged parts from
        // the SDK on repeat searches. Key format: "<guid>|<part>|raw" (3 segments, distinct
        // from BuildReadCacheKey's 6-segment JSON keys, and matched by the same guid prefix).
        // Full, non-minimized MCP reads seed this cache too, avoiding a duplicate SDK read
        // when an agent reads a source and then searches it.
        // A null result means the SDK read failed and is never cached. A successfully read
        // empty part is kept in a short-lived negative cache, so repeated scans do not pay
        // the same SDK round-trip for objects that have no source. Ordinary raw sources up
        // to 256 KiB use _readCache; larger bodies use the bounded side cache below up to
        // 2 MiB. Direct MCP-read promotion remains limited to 256 KiB; source-search promotion
        // applies its separate 2 MiB per-entry / 8 MiB aggregate persisted-source budget.
        private const int RawSourceCacheMaxBytes = 256 * 1024;
        private const int LargeRawSourceCacheMaxBytes = 2 * 1024 * 1024;
        private const int LargeRawSourceCacheMaxEntries = 4;

        public string ReadPartSourceRaw(KBObject obj, string partName)
        {
            if (obj == null) return null;
            string key = BuildRawSourceCacheKey(obj.Guid, partName);
            if (TryGetReadCache(key, out string cached)) return cached;
            if (TryGetLargeRawSourceCache(key, out cached)) return cached;

            string normalizedPart = NormalizeRawSourcePart(partName);
            string src = ReadPartSourceUncached(obj, normalizedPart);
            if (src == null) return null;
            if (src.Length == 0)
                SetEmptyRawSourceCache(key);
            else if (src.Length <= RawSourceCacheMaxBytes)
                SetReadCache(key, src);
            else if (src.Length <= LargeRawSourceCacheMaxBytes)
                SetLargeRawSourceCache(key, src);
            return src;
        }

        // PERFORMANCE (perf round 2): cache-only probe used by SourceSearchService's scan
        // loop to skip the FindObject SDK call entirely when the part source for this index
        // entry is already cached. The index entry carries the object's guid, so the cache
        // key is computed without touching the SDK — a full-KB source scan becomes dictionary
        // lookups + regex over cached text on repeat searches instead of one COM round-trip
        // per candidate. Returns false when the guid/part isn't cached (caller then does the
        // normal FindObject + ReadPartSourceRaw path). Same key format as ReadPartSourceRaw.
        public bool TryGetPartSourceRaw(string guid, string partName, out string src)
        {
            src = string.Empty;
            if (string.IsNullOrEmpty(guid)) return false;
            // The index stores Guid as Guid.ToString() (format "D": 36 chars with hyphens),
            // while ReadPartSourceRaw writes cache keys from obj.Guid.ToString("N") (32 chars,
            // no hyphens). Normalize here so the probe key ALWAYS matches the writer key —
            // otherwise the round-2 cache-first fast path would silently miss on every call.
            string normalizedGuid;
            try { normalizedGuid = Guid.Parse(guid).ToString("N").ToLowerInvariant(); }
            catch { normalizedGuid = guid.Trim().ToLowerInvariant(); }
            string normalizedPart = NormalizeRawSourcePart(partName);
            string key = normalizedGuid + "|" + normalizedPart + "|raw";
            if (TryGetReadCache(key, out src)) return true;
            if (TryGetLargeRawSourceCache(key, out src)) return true;
            if (TryGetEmptyRawSourceCache(key)) return true;

            // Large sources are kept in the bounded side cache above. As a final fallback,
            // consult the JSON read cache and seed the ordinary raw cache only when the
            // cached payload is within the compact-cache limit.
            return TryGetFullReadSourceCache(normalizedGuid, normalizedPart, out src);
        }

        private static string NormalizeRawSourcePart(string partName)
        {
            return string.IsNullOrWhiteSpace(partName) ? "source" : partName.Trim().ToLowerInvariant();
        }

        private static string BuildRawSourceCacheKey(Guid objectGuid, string partName)
        {
            return objectGuid.ToString("N").ToLowerInvariant()
                + "|" + NormalizeRawSourcePart(partName) + "|raw";
        }

        private static bool TryGetFullReadSourceCache(string normalizedGuid, string normalizedPart, out string source)
        {
            source = string.Empty;
            string prefix = normalizedGuid + "|" + normalizedPart + "|";

            // ReadObjectSource uses -1 for omitted offset/limit. Check explicit full
            // reads first (the common source-search preparation path), then a complete
            // unpaginated short read as a safe fallback.
            return TryGetSourceFromJsonReadCache(prefix + "-1|0|mcp|0", normalizedGuid, normalizedPart, out source)
                || TryGetSourceFromJsonReadCache(prefix + "0|0|mcp|0", normalizedGuid, normalizedPart, out source)
                || TryGetSourceFromJsonReadCache(prefix + "-1|-1|mcp|0", normalizedGuid, normalizedPart, out source);
        }

        private static bool TryGetSourceFromJsonReadCache(
            string key,
            string normalizedGuid,
            string normalizedPart,
            out string source)
        {
            source = string.Empty;
            if (!TryGetReadCache(key, out string payload)) return false;

            try
            {
                var parsed = JObject.Parse(payload);
                if (!TryGetCompleteSource(parsed, out source)) return false;

                // Avoid a second large string in the dedicated raw cache; the current
                // search can still consume the source returned from the JSON payload.
                if (source.Length <= RawSourceCacheMaxBytes)
                {
                    SetReadCache(normalizedGuid + "|" + normalizedPart + "|raw", source);
                }
                return true;
            }
            catch
            {
                source = string.Empty;
                return false;
            }
        }

        private static bool TryGetCompleteSource(JObject payload, out string source)
        {
            source = string.Empty;
            if (payload == null || payload["error"] != null) return false;
            if (payload["isBase64"]?.Value<bool>() == true) return false;

            bool truncated = payload["truncated"]?.Value<bool>() == true
                || payload["isTruncatedByWorker"]?.Value<bool>() == true;
            if (truncated) return false;

            JToken sourceToken = payload["source"];
            if (sourceToken == null || sourceToken.Type != JTokenType.String) return false;
            source = sourceToken.Value<string>();
            return !string.IsNullOrEmpty(source);
        }

        // The actual SDK read for ReadPartSourceRaw — mirrors SourceSearchService's legacy
        // TryGetPartSource so the cache layer can live here without changing semantics.
        private static string ReadPartSourceUncached(KBObject obj, string normalizedPart)
        {
            try
            {
                if (normalizedPart == "source")
                {
                    if (obj is Procedure procedure && procedure.ProcedurePart != null)
                        return procedure.ProcedurePart.Source ?? "";
                    dynamic sp = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
                    return sp?.Source ?? "";
                }
                if (normalizedPart == "rules")
                {
                    if (obj is Procedure procedure)
                        return procedure.Rules?.Source ?? "";
                    if (obj is Transaction transaction)
                        return transaction.Rules?.Source ?? "";
                    if (obj is WebPanel webPanel)
                        return webPanel.Rules?.Source ?? "";
                    try { return ((dynamic)obj).Rules?.Source ?? ""; } catch { return null; }
                }
                if (normalizedPart == "conditions")
                {
                    try { return ((dynamic)obj).Conditions?.Source ?? ""; } catch { return null; }
                }
                if (normalizedPart == "events")
                {
                    if (obj is Transaction transaction)
                        return transaction.Events?.Source ?? "";
                    if (obj is WebPanel webPanel)
                        return webPanel.Events?.Source ?? "";
                    try { return ((dynamic)obj).Events?.Source ?? ""; } catch { return null; }
                }
                if (normalizedPart == "webform" || normalizedPart == "layout")
                {
                    try { return GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj) ?? ""; } catch { return null; }
                }
            }
            catch { return null; }
            return "";
        }

        public string ExportObjectToText(string target, string outputPath, string partName = null, string typeFilter = null, bool overwrite = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputPath))
                    return McpResponse.Err(code: "OutputPathRequired", message: "Output path is required.", hint: "Provide a valid outputPath.", target: target);

                var obj = FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, GetLoadedIndexOrNull());

                string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                string exportJson = ReadObjectSourceInternal(obj, normalizedPart, 0, int.MaxValue, "mcp", false);
                JObject exportResult = JObject.Parse(exportJson);
                string source = exportResult["source"]?.ToString();
                if (string.IsNullOrEmpty(source))
                {
                    return McpResponse.Err(
                        code: "ExportFailed",
                        message: "Export failed: part did not return text content.",
                        hint: exportResult["error"]?.ToString() ?? "The object part did not return text content.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Returns availableParts so you can pick a valid part name.")),
                        target: target);
                }

                // NOTE (path-safety consolidation): `outputPath` is intentionally NOT gated to the
                // KB root — export_part is designed to write anywhere on disk the caller chooses
                // (tool_definitions.json example: outputPath="C:\\tmp\\Customer.gxp"). There is no
                // "root" here to check containment against, so PathSafety doesn't apply.
                string fullPath = Path.GetFullPath(outputPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (File.Exists(fullPath) && !overwrite)
                    return McpResponse.Err(code: "FileAlreadyExists", message: "Output file already exists. Set overwrite=true to replace it.", hint: "Pass overwrite=true to replace the existing file.", target: fullPath);

                File.WriteAllText(fullPath, source, new UTF8Encoding(false));
                return McpResponse.Ok(target: target, code: "ExportCompleted", result: new JObject
                {
                    ["part"] = normalizedPart,
                    ["path"] = fullPath,
                    ["bytes"] = new FileInfo(fullPath).Length
                });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ExportFailed", message: "Export failed: " + ex.Message, hint: "Check that the outputPath is writable and the object part exists.", target: target);
            }
        }

        public string ImportObjectFromText(string target, string inputPath, string partName = null, string typeFilter = null, bool dryRun = false)
        {
            try
            {
                if (_writeService == null)
                    return McpResponse.Err(code: "WriteServiceUnavailable", message: "Import failed: Write service is not available.", hint: "Ensure the worker is fully initialized before calling import.", target: target);

                if (string.IsNullOrWhiteSpace(inputPath))
                    return McpResponse.Err(code: "InputPathRequired", message: "Input path is required.", hint: "Provide a valid inputPath.", target: target);

                // NOTE (path-safety consolidation): `inputPath` is intentionally NOT gated to the
                // KB root either — import_part reads a text file from wherever the caller points it
                // (the export/import pair is designed to round-trip through an arbitrary filesystem
                // location). No "root" to check containment against.
                string fullPath = Path.GetFullPath(inputPath);
                if (!File.Exists(fullPath))
                    return McpResponse.Err(code: "InputFileNotFound", message: "Input file not found.", hint: "Verify the inputPath points to an existing file.", target: fullPath);

                string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                var obj = FindObject(target, typeFilter);
                if (obj == null)
                {
                    if (dryRun)
                    {
                        return McpResponse.Err(
                            code: "ObjectNotFound",
                            message: "Object not found; dry-run did not create it.",
                            hint: "Create the object first or omit dryRun only when the import is intended to create a typed object.",
                            nextSteps: new JArray(McpResponse.NextStep("genexus_create_object", new JObject { ["name"] = target }, "Create the object first, then retry the import.")),
                            target: target,
                            extra: new JObject { ["wouldCreate"] = !string.IsNullOrWhiteSpace(typeFilter), ["persisted"] = false });
                    }
                    if (string.IsNullOrWhiteSpace(typeFilter))
                    {
                        return McpResponse.Err(
                            code: "ObjectNotFound",
                            message: "Object not found. Provide 'type' to create it before importing.",
                            hint: "Pass typeFilter so the import can auto-create the object if missing.",
                            nextSteps: new JArray(McpResponse.NextStep("genexus_create_object", new JObject { ["name"] = target }, "Create the object first, then retry the import.")),
                            target: target);
                    }

                    string createResult = CreateObject(typeFilter, target);
                    JObject createJson = JObject.Parse(createResult);
                    if (!string.Equals(createJson["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        return createResult;
                    }
                }

                string importedText = File.ReadAllText(fullPath);
                string writeResult = _writeService.WriteObject(target, normalizedPart, importedText, typeFilter, autoValidate: false, dryRun: dryRun);
                JObject writeJson = JObject.Parse(writeResult);

                // WriteService.WriteObject only ever returns via McpResponse.Ok/Err (canonical
                // envelope) — no legacy "Success" status to fall back to.
                if (string.Equals(writeJson["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    writeJson["path"] = fullPath;
                    writeJson["part"] = normalizedPart;
                    writeJson["importedBytes"] = new FileInfo(fullPath).Length;
                }

                return writeJson.ToString();
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ImportFailed", message: "Import failed: " + ex.Message, hint: "Check that the inputPath is readable and the target object/part is valid.", target: target);
            }
        }

        // issue #29: a WorkWithDevices virtual part serializes to an empty properties element
        // ("<Properties />" or "<Properties></Properties>") when it has no own content.
        private static bool IsEmptyPropertiesXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return true;
            string t = xml.Trim();
            return t.Equals("<Properties />", StringComparison.OrdinalIgnoreCase)
                || t.Equals("<Properties/>", StringComparison.OrdinalIgnoreCase)
                || t.Equals("<Properties></Properties>", StringComparison.OrdinalIgnoreCase);
        }

        private string ReadObjectSourceInternal(KBObject obj, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false)
        {
            if (obj is Artech.Packages.Patterns.Objects.PatternSettings settings
                && (string.IsNullOrWhiteSpace(partName) || partName.Equals("Source", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("PatternSettings", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    string xml = PatternSettingsService.Serialize(settings);
                    var page = ReadPagination.ApplyDefault(xml, offset, limit, client);
                    return new JObject
                    {
                        ["part"] = "PatternSettings", ["source"] = page.Content,
                        ["serializationFormat"] = "Internal", ["contentType"] = "application/xml",
                        ["isEmpty"] = false, ["truncated"] = page.Truncated,
                        ["isTruncatedByWorker"] = page.Truncated, ["explicitFullRead"] = page.ExplicitFullRead,
                        ["offset"] = page.Offset, ["limit"] = page.LinesReturned, ["totalLines"] = page.TotalLines,
                        ["suggestedNextOffset"] = page.SuggestedNextOffset,
                        ["versionToken"] = WriteService.ComputeContentVersionToken(obj, xml),
                        ["saveAvailable"] = false
                    }.ToString();
                }
                catch (Exception ex)
                {
                    return McpResponse.Err(code: "SettingsTreeUnavailable", message: ex.Message,
                        hint: "The Settings tree could not be serialized. Do not infer an empty Settings object.");
                }
            }
            if (DataSelectorReadService.IsDataSelector(obj)
                && (string.IsNullOrWhiteSpace(partName)
                    || partName.Equals("Source", StringComparison.OrdinalIgnoreCase)
                    || DataSelectorReadService.IsVirtualPart(partName)))
            {
                IEnumerable<string> requested = string.IsNullOrWhiteSpace(partName)
                    || partName.Equals("Source", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : new[] { partName };
                return DataSelectorReadService.Read((DataSelector)obj, requested);
            }

            partName = ResolvePartName(obj, partName);

            Logger.Info($"ReadObjectSourceInternal: {obj.Name} (Part: {partName}, Client: {client})");
            var sw = Stopwatch.StartNew();
            try
            {
                string targetName = obj.Name;

                if (WebFormXmlHelper.IsVisualPart(partName))
                {
                    string xml = WebFormXmlHelper.ReadEditableXml(obj);
                    if (string.IsNullOrEmpty(xml))
                    {
                        var diagnosticPart = WebFormXmlHelper.GetWebFormPart(obj);
                        string details = diagnosticPart == null 
                            ? "No visual part (Layout/WebForm) found." 
                            : $"Rejected part {diagnosticPart.TypeDescriptor?.Name} (Class: {diagnosticPart.GetType().Name}, GUID: {diagnosticPart.Type}) as a valid visual part.";

                        return Models.McpResponse.Err(
                            code: "VisualXmlUnavailable",
                            message: "Visual XML not available.",
                            hint: details,
                            nextSteps: new JArray(Models.McpResponse.NextStep(
                                tool: "genexus_read",
                                args: new JObject { ["name"] = targetName },
                                why: "Use the availableParts list to pick a part this object actually exposes.")),
                            target: targetName,
                            extra: new JObject
                            {
                                ["part"] = partName,
                                ["objectName"] = obj.Name,
                                ["objectType"] = obj.TypeDescriptor?.Name,
                                ["availableParts"] = new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj))
                            });
                    }

                    var visualResult = new JObject
                    {
                        ["part"] = partName,
                        ["contentType"] = "application/xml",
                        ["xmlKind"] = "GxMultiForm"
                    };
                    ProcessTextResponse(xml, visualResult, client);
                    return visualResult.ToString();
                }

                if (PatternAnalysisService.IsPatternPart(partName))
                {
                    global::Artech.Architecture.Common.Objects.KBObject resolvedObject = null;
                    string resolvedPartName = partName;
                    string patternXml = _patternAnalysisService?.ReadPatternPartXml(obj, partName, out resolvedObject, out resolvedPartName);
                    if (string.IsNullOrEmpty(patternXml))
                    {
                        // PatternVirtual fallback: serialise the matching part directly when the WWP+ analyser bails.
                        try
                        {
                            var rawPart = obj.Parts.Cast<global::Artech.Architecture.Common.Objects.KBObjectPart>()
                                .FirstOrDefault(p =>
                                    string.Equals(p.TypeDescriptor?.Name, partName, StringComparison.OrdinalIgnoreCase) ||
                                    p.GetType().Name.IndexOf(partName, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (rawPart != null)
                            {
                                patternXml = rawPart.SerializeToXml();
                                resolvedPartName = rawPart.TypeDescriptor?.Name ?? rawPart.GetType().Name;
                            }
                        }
                        catch (Exception fbEx) { Logger.Debug("[PatternRead] raw-serialize fallback failed: " + fbEx.Message); }

                        if (string.IsNullOrEmpty(patternXml))
                            return Models.McpResponse.Err(
                                code: "PatternXmlUnavailable",
                                message: "Pattern XML not available.",
                                hint: "The requested WorkWithPlus pattern part could not be resolved through the current SDK path. Confirm the object has a pattern attached (genexus_inspect target=...).",
                                nextSteps: new JArray(Models.McpResponse.NextStep(
                                    tool: "genexus_inspect",
                                    args: new JObject { ["name"] = targetName },
                                    why: "Confirms whether a WorkWithPlus pattern is attached and which parts are exposed.")),
                                target: targetName,
                                extra: new JObject
                                {
                                    ["part"] = partName,
                                    ["objectName"] = obj.Name,
                                    ["objectType"] = obj.TypeDescriptor?.Name,
                                    ["availableParts"] = new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj))
                                });
                    }

                    var patternResult = new JObject
                    {
                        ["part"] = partName,
                        ["contentType"] = "application/xml",
                        ["xmlKind"] = resolvedPartName
                    };

                    if (resolvedObject != null && resolvedObject.Guid != obj.Guid)
                    {
                        patternResult["resolvedObject"] = resolvedObject.Name;
                        patternResult["resolvedType"] = resolvedObject.TypeDescriptor?.Name;
                    }

                    ProcessTextResponse(patternXml, patternResult, client);
                    return patternResult.ToString();
                }

                Guid partGuid = GxMcp.Worker.Structure.PartAccessor.GetPartGuid(obj.TypeDescriptor.Name, partName);
                
                KBObjectPart part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);

                JObject result = new JObject();
                result["part"] = partName;

                // issue #26 (Humberto DSO case): reading the generic "Source" of a Design
                // System returns BOTH parts — tokens block then styles block — so the whole
                // object is visible and an agent can round-trip it back through a single
                // part="Source" write (which re-splits). Explicit part="Tokens"/"Styles"
                // still reads just that one part via the normal ISource path below.
                if (GxMcp.Worker.Structure.PartAccessor.IsDesignSystem(obj)
                    && (partName.Equals("Source", StringComparison.OrdinalIgnoreCase)
                        || partName.Equals("Code", StringComparison.OrdinalIgnoreCase)))
                {
                    var tp = GxMcp.Worker.Structure.PartAccessor.GetDesignSystemPart(obj, styles: false) as ISource;
                    var sp = GxMcp.Worker.Structure.PartAccessor.GetDesignSystemPart(obj, styles: true) as ISource;
                    string tsrc = tp?.Source ?? "";
                    string ssrc = sp?.Source ?? "";
                    string combined = tsrc;
                    if (ssrc.Length > 0)
                        combined = combined.Length > 0 ? tsrc.TrimEnd() + "\n\n" + ssrc : ssrc;
                    ProcessSourceContent(obj, combined, offset, limit, result, client);
                    Logger.Info("ReadSource (DesignSystem tokens+styles) SUCCESS");
                    return result.ToString();
                }

                // Virtual/DSL Parts (Structure for Trn/Table/SDT)
                // We process this BEFORE the generic part check because Tables might not have a physical Part GUID mapped,
                // and even if they do, we want our custom DSL representation.
                bool isStructurePartAlias = partName.Equals("Structure", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("TableStructure", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("SDTStructure", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("TrnStructure", StringComparison.OrdinalIgnoreCase);
                // Friction-report #9b: use the SDK type test (is Table) instead of comparing
                // GetType().Name as a string — subclassed/proxied Table instances were falling
                // through to the generic part.SerializeToXml() branch and returning <Properties />.
                bool isStructurableObject = obj is Transaction
                    || obj is Table
                    || string.Equals(obj.TypeDescriptor?.Name, "Table", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(obj.TypeDescriptor?.Name, "Transaction", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(obj.TypeDescriptor?.Name, "SDT", StringComparison.OrdinalIgnoreCase);
                if (isStructurePartAlias && isStructurableObject)
                {
                    string structureText = StructureParser.SerializeToText(obj);
                    ProcessTextResponse(structureText, result, client);
                    Logger.Info("ReadSource (Structure DSL) SUCCESS");
                    return result.ToString();
                }

                if (part == null)
                {
                    result["error"] = $"Part '{partName}' not found in {obj.Name}";
                    try
                    {
                        var avail = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                        if (avail != null && avail.Length > 0)
                        {
                            result["availableParts"] = new JArray(avail);
                            result["hint"] = $"Valid parts for {obj.TypeDescriptor?.Name ?? "object"}: {string.Join(", ", avail)}.";
                        }
                    }
                    catch { }
                    return result.ToString();
                }

                // Theme and StyleSheet parts are textual authoring surfaces, but
                // they are not ISource parts in every GeneXus SDK build. Expose
                // their real CSS/source instead of the opaque <Properties /> XML
                // fallback so reads, snapshots and write verification all observe
                // the same content that the IDE edits.
                string styleText = ThemeStyleEditHelper.ReadText(part);
                string concretePartName = part.GetType()?.Name ?? string.Empty;
                if (styleText != null
                    && (concretePartName.IndexOf("ThemeStylesPart", StringComparison.OrdinalIgnoreCase) >= 0
                        || concretePartName.IndexOf("DesignStylesPart", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    result["contentType"] = "text/css";
                    result["stylePartKind"] = concretePartName.IndexOf("ThemeStylesPart", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "ThemeStylesPart"
                        : "DesignStylesPart";
                    ProcessSourceContent(obj, styleText, offset, limit, result, client);
                    Logger.Info("ReadSource (Theme/StyleSheet) SUCCESS");
                    return result.ToString();
                }

                // Handle Variables Part specially
                var varPart = (part as global::Artech.Genexus.Common.Parts.VariablesPart) ?? GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj);
                if (varPart != null && (part is global::Artech.Genexus.Common.Parts.VariablesPart || part.GetType().Name.IndexOf("Variables", StringComparison.OrdinalIgnoreCase) >= 0 || partName.Equals("Variables", StringComparison.OrdinalIgnoreCase)))
                {
                    string varText = VariableInjector.GetVariablesAsText(varPart);
                    ProcessTextResponse(varText, result, client);
                    Logger.Info("ReadSource (Variables) SUCCESS");
                }
                else if (part is ISource sourcePart)
                {
                    string content = sourcePart.Source ?? "";
                    if (minimize && content.Length > 5000)
                    {
                        content = content.Substring(0, 2500) + "\n... [TRUNCATED FOR BREVITY - USE PAGINATION] ...\n" + content.Substring(content.Length - 1000);
                    }
                    ProcessSourceContent(obj, content, offset, limit, result, client);
                    Logger.Info("ReadSource (ISource) SUCCESS");
                }
                else
                {
                    // Reflection Fallback for Data Providers and other parts that encapsulate a Source string but don't implement ISource natively.
                    var contentProp = part.GetType().GetProperty("Source", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                   ?? part.GetType().GetProperty("Content", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    if (contentProp != null && contentProp.CanRead && contentProp.PropertyType == typeof(string))
                    {
                        string content = (string)contentProp.GetValue(part) ?? "";
                        if (minimize && content.Length > 5000)
                        {
                            content = content.Substring(0, 2500) + "\n... [TRUNCATED FOR BREVITY] ...\n" + content.Substring(content.Length - 1000);
                        }
                        ProcessSourceContent(obj, content, offset, limit, result, client);
                        Logger.Info("ReadSource (Reflection) SUCCESS");
                    }
                    else
                    {
                        string xml = part.SerializeToXml();
                        ProcessTextResponse(xml, result, client);
                        Logger.Info("ReadSource (XML) SUCCESS");

                        // issue #29: SDPanel layout/variables/conditions are WorkWithDevices
                        // virtual projection parts — SerializeToXml() returns an empty
                        // "<Properties />" even when the panel is populated, because the real
                        // content is projected from the pattern and not stored on the part.
                        // Flag this explicitly so an agent does NOT conclude the object is
                        // empty. The event code IS readable via the SDEvents/SDRules parts.
                        if (GxMcp.Worker.Structure.PartAccessor.IsWorkWithDevicesProjectionPart(part)
                            && IsEmptyPropertiesXml(xml))
                        {
                            result["projected"] = true;
                            result["limitation"] = "SDPanelProjectionPart";
                            result["note"] = $"'{partName}' is a Smart Device Panel (WorkWithDevices) virtual projection part. "
                                + "Its content is projected from the panel's pattern and is NOT extractable as XML through the SDK — "
                                + "an empty '<Properties />' here does NOT mean the panel is empty. "
                                + "The panel's event code IS readable via part=SDEvents (and rules via part=SDRules). "
                                + "Layout and variables can only be edited in the GeneXus IDE.";
                            result["readableParts"] = new JArray("SDEvents", "SDRules");
                        }
                    }
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private static string ResolvePartName(KBObject obj, string partName)
        {
            bool defaulted = string.IsNullOrWhiteSpace(partName);
            // The gateway sends "Source" as the default when no part is given, so treat both
            // the empty and the generic-"Source" case as "caller wants the primary part".
            bool genericSource = defaulted || partName.Equals("Source", StringComparison.OrdinalIgnoreCase);
            if (!genericSource) return partName; // explicit non-Source part — honor as-is

            if (obj is Procedure) return "Source";
            if (obj is Transaction || obj is WebPanel) return defaulted ? "Events" : "Source";
            if (obj is Artech.Genexus.Common.Objects.API) return "Methods";
            // Data Selectors have no ISource part. Keep the generic alias so the
            // typed read path can return their complete persisted definition.
            if (DataSelectorReadService.IsDataSelector(obj)) return "Source";

            // issue #31.5: SDTs (and other objects without a Source part) previously errored
            // "Part 'Source' not found". Fall back to the object's primary part instead:
            // keep Source when it really exists, else SDTStructure for an SDT, else the first
            // available part.
            try { if (GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Source") != null) return "Source"; }
            catch { }

            string typeName = obj?.TypeDescriptor?.Name ?? "";
            if (typeName.Equals("SDT", StringComparison.OrdinalIgnoreCase) ||
                typeName.IndexOf("StructuredDataType", StringComparison.OrdinalIgnoreCase) >= 0)
                return "SDTStructure";

            try
            {
                var parts = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                if (parts != null && parts.Length > 0) return parts[0];
            }
            catch { }

            return "Source";
        }

        private static bool ShouldUseReadCache(string client, bool minimize)
        {
            return string.Equals(client, "mcp", StringComparison.OrdinalIgnoreCase) && !minimize;
        }

        internal static string BuildReadCacheKey(Guid objectGuid, string partName, int? offset, int? limit, string client, bool minimize)
        {
            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "source" : partName.Trim().ToLowerInvariant();
            string normalizedClient = string.IsNullOrWhiteSpace(client) ? "mcp" : client.Trim().ToLowerInvariant();
            int normalizedOffset = offset ?? -1;
            int normalizedLimit = limit ?? -1;
            return string.Concat(
                objectGuid.ToString("N"),
                "|",
                normalizedPart,
                "|",
                normalizedOffset.ToString(),
                "|",
                normalizedLimit.ToString(),
                "|",
                normalizedClient,
                "|",
                minimize ? "1" : "0");
        }

        private static bool TryGetReadCache(string key, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!_readCache.TryGetValue(key, out var entry) || entry == null)
            {
                return false;
            }

            if (DateTime.UtcNow - entry.UpdatedUtc > ReadCacheTtl)
            {
                _readCache.TryRemove(key, out _);
                return false;
            }

            payload = entry.Payload;
            return !string.IsNullOrWhiteSpace(payload);
        }

        private static void SetReadCache(string key, string payload)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            _emptyRawSourceCache.TryRemove(key, out _);
            _readCache[key] = new ReadCacheEntry
            {
                Payload = payload,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static bool TryGetLargeRawSourceCache(string key, out string source)
        {
            source = string.Empty;
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (!_largeRawSourceCache.TryGetValue(key, out var entry) || entry == null)
                return false;
            if (DateTime.UtcNow - entry.UpdatedUtc > ReadCacheTtl)
            {
                _largeRawSourceCache.TryRemove(key, out _);
                return false;
            }
            source = entry.Payload;
            return !string.IsNullOrEmpty(source);
        }

        private static void SetLargeRawSourceCache(string key, string source)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(source)
                || source.Length > LargeRawSourceCacheMaxBytes)
                return;

            _emptyRawSourceCache.TryRemove(key, out _);
            _largeRawSourceCache[key] = new ReadCacheEntry
            {
                Payload = source,
                UpdatedUtc = DateTime.UtcNow
            };

            if (_largeRawSourceCache.Count <= LargeRawSourceCacheMaxEntries) return;
            string oldestKey = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var kvp in _largeRawSourceCache)
            {
                if (kvp.Value == null || kvp.Value.UpdatedUtc < oldest)
                {
                    oldest = kvp.Value?.UpdatedUtc ?? DateTime.MinValue;
                    oldestKey = kvp.Key;
                }
            }
            if (oldestKey != null) _largeRawSourceCache.TryRemove(oldestKey, out _);
        }

        private static bool TryGetEmptyRawSourceCache(string key)
        {
            if (string.IsNullOrWhiteSpace(key)
                || !_emptyRawSourceCache.TryGetValue(key, out DateTime cachedUtc))
                return false;
            if (DateTime.UtcNow - cachedUtc > ReadCacheTtl)
            {
                _emptyRawSourceCache.TryRemove(key, out _);
                return false;
            }
            return true;
        }

        private static void SetEmptyRawSourceCache(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _readCache.TryRemove(key, out _);
            _largeRawSourceCache.TryRemove(key, out _);
            _emptyRawSourceCache[key] = DateTime.UtcNow;
        }

        private static bool TryGetCacheablePayload(string payload, out JObject parsedPayload)
        {
            parsedPayload = null;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                parsedPayload = JObject.Parse(payload);
                return parsedPayload["error"] == null;
            }
            catch
            {
                return false;
            }
        }

        // Keep this bridge deliberately strict: only a complete MCP response can seed
        // the raw cache. Paginated/minimized/base64 responses must never masquerade as
        // complete source, otherwise search_source could return false negatives.
        internal static bool CacheRawSourceFromReadPayload(
            Guid objectGuid,
            string partName,
            JObject payload,
            int? offset,
            string client,
            bool minimize)
        {
            if (objectGuid == Guid.Empty || payload == null || minimize
                || !string.Equals(client, "mcp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (offset.HasValue && offset.Value != 0) return false;
            if (payload["error"] != null) return false;

            if (!TryGetCompleteSource(payload, out string source)
                || source.Length > LargeRawSourceCacheMaxBytes) return false;

            string cacheKey = BuildRawSourceCacheKey(objectGuid, partName);
            if (source.Length <= RawSourceCacheMaxBytes)
                SetReadCache(cacheKey, source);
            else
                SetLargeRawSourceCache(cacheKey, source);
            return true;
        }

        internal bool TryPromoteCompleteSourceRead(
            Guid objectGuid,
            string partName,
            JObject payload,
            int? offset,
            string client,
            bool minimize)
        {
            if (objectGuid == Guid.Empty || payload == null || minimize
                || !string.Equals(client, "mcp", StringComparison.OrdinalIgnoreCase)
                || (offset.HasValue && offset.Value != 0)
                || !string.Equals(NormalizeRawSourcePart(partName), "source", StringComparison.OrdinalIgnoreCase)
                || !TryGetCompleteSource(payload, out string source)
                || source.Length > RawSourceCacheMaxBytes)
            {
                return false;
            }

            IndexCacheService indexCache = _kbService?.GetIndexCache();
            SearchIndex index = indexCache?.TryGetLoadedIndex();
            if (index?.Objects == null) return false;

            string guid = objectGuid.ToString();
            SearchIndex.IndexEntry entry = index.Objects.Values.FirstOrDefault(candidate =>
                candidate != null
                && string.Equals(candidate.Guid, guid, StringComparison.OrdinalIgnoreCase));
            return entry != null && indexCache.PromoteSourceForSearch(entry, source);
        }

        private void ProcessSourceContent(KBObject obj, string content, int? offset, int? limit, JObject result, string client = "ide")
        {
            // v2.3.8 (Task 6.2) — delegate to ReadPagination so byte-budget + line-budget
            // are applied uniformly and suggestedNextOffset/Limit surface for chained reads.
            var page = GxMcp.Worker.Helpers.ReadPagination.ApplyDefault(content, offset, limit, client);
            bool mcpDefault = !offset.HasValue && !limit.HasValue && client == "mcp";
            bool includeDerivedMetadata = client != "mcp";

            result["isTruncatedByWorker"] = page.Truncated;
            result["truncated"] = page.Truncated;
            // Issue #27 item 7: signal an explicit full read (limit=0) so the gateway
            // relaxes its source context-budget cut instead of silently re-capping.
            if (page.ExplicitFullRead) result["explicitFullRead"] = true;
            if (page.Truncated && mcpDefault)
                result["message"] = "MCP read defaulted to ~200 lines / 16 KB to control context size. Use offset/limit to paginate, or limit=0 to read in full.";

            string paginatedContent = page.Content;
            if (page.Truncated)
                paginatedContent += "\n\n// ... [CONTENT TRUNCATED. USE PAGINATION (offset/limit) TO READ FURTHER] ... //\n";

            ProcessTextResponse(paginatedContent, result, client);
            result["offset"] = page.Offset;
            result["limit"] = page.LinesReturned;
            result["totalLines"] = page.TotalLines;
            result["totalBytes"] = page.TotalBytes;
            // Optimistic-concurrency token (stale-edit fix): pass this back as
            // baseVersion on genexus_edit so a concurrent IDE change is caught as
            // StaleObject instead of being silently overwritten.
            try { var vt = WriteService.ComputeContentVersionToken(obj, content); if (vt != null) result["versionToken"] = vt; } catch { }
            if (page.SuggestedNextOffset.HasValue) result["suggestedNextOffset"] = page.SuggestedNextOffset.Value;
            if (page.SuggestedNextLimit.HasValue) result["suggestedNextLimit"] = page.SuggestedNextLimit.Value;

            if (includeDerivedMetadata)
            {
                AddVariableMetadata(obj, paginatedContent, result);
                AddCallSignatures(obj, paginatedContent, result);
            }
        }

        private void ProcessTextResponse(string text, JObject result, string client)
        {
            result["isEmpty"] = string.IsNullOrEmpty(text);

            if (client == "mcp")
            {
                Logger.Debug("ProcessTextResponse: Using Plain Text for MCP");
                result["source"] = text;
                result["isBase64"] = false;
            }
            else
            {
                Logger.Debug($"ProcessTextResponse: Using Base64 for {client}");
                result["source"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                result["isBase64"] = true;
            }
        }

        private void AddCallSignatures(KBObject obj, string source, JObject result)
        {
            try
            {
                var calledObjectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Regex for common call patterns in GeneXus
                var callMatches = System.Text.RegularExpressions.Regex.Matches(source, 
                    @"\b(?:call|udp|submit)\s*\(\s*(\w+)|\b(\w+)\s*\.\s*(?:call|udp|submit)\b", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in callMatches)
                {
                    string name = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
                    calledObjectNames.Add(name);
                }

                if (calledObjectNames.Count > 0)
                {
                    var calls = new JArray();
                    foreach (var name in calledObjectNames)
                    {
                        var target = FindObject(name);
                        if (target != null && target.Guid != obj.Guid)
                        {
                            var (parmRule, parms) = GetParametersInternal(target);
                            var cObj = new JObject
                            {
                                ["name"] = target.Name,
                                ["type"] = target.TypeDescriptor.Name
                            };
                            if (!string.IsNullOrEmpty(parmRule)) cObj["parmRule"] = parmRule;
                            calls.Add(cObj);
                        }
                    }
                    if (calls.Count > 0) result["calls"] = calls;
                }
            }
            catch (Exception ex) { Logger.Debug("AddCallSignatures failed: " + ex.Message); }
        }

        private void AddVariableMetadata(KBObject obj, string source, JObject result)
        {
            try
            {
                // Nirvana v19.4: Auto-Inject Full Context (Variables + Data Schema + Pattern)
                var varPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj)
                    ?? obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.IndexOf("Variables", StringComparison.OrdinalIgnoreCase) >= 0);
                if (varPart != null)
                {
                    var referencedVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var matches = System.Text.RegularExpressions.Regex.Matches(source, @"&(\w+)");
                    foreach (System.Text.RegularExpressions.Match match in matches) {
                        referencedVars.Add(match.Groups[1].Value);
                    }

                    if (referencedVars.Count > 0)
                    {
                        var variables = new JArray();
                        var varListProp = varPart.GetType().GetProperty("Variables");
                        if (varListProp != null)
                        {
                            var varList = varListProp.GetValue(varPart) as System.Collections.IEnumerable;
                            if (varList != null)
                            {
                                foreach (object vObj in varList)
                                {
                                    dynamic v = vObj;
                                    string vName = v.Name;
                                    if (referencedVars.Contains(vName))
                                    {
                                        variables.Add(new JObject {
                                            ["name"] = vName,
                                            ["type"] = v.Type.ToString(),
                                            ["length"] = Convert.ToInt32(v.Length),
                                            ["decimals"] = Convert.ToInt32(v.Decimals),
                                            ["isCollection"] = (bool)v.IsCollection
                                        });
                                    }
                                }
                            }
                        }
                        if (variables.Count > 0) result["variables"] = variables;
                    }
                }

                // Inject Data Context (Tables used in this object)
                if (_dataInsightService != null)
                {
                    try {
                        var dataContextJson = _dataInsightService.GetDataContext(obj.Name);
                        if (!string.IsNullOrEmpty(dataContextJson) && !dataContextJson.Contains("\"error\""))
                        {
                            var dataContext = JObject.Parse(dataContextJson);
                            if (dataContext["dataSchema"] != null) result["dataSchema"] = dataContext["dataSchema"];
                            if (dataContext["patternMetadata"] != null) result["patternMetadata"] = dataContext["patternMetadata"];
                        }
                    } catch { /* Silent fail to ensure read stability */ }
                }
            }
            catch { }
        }

        public class ParameterInfo
        {
            public string Name { get; set; }
            public string Accessor { get; set; }
            public string Type { get; set; }
        }

        public (string parmRule, List<ParameterInfo> parameters) GetParametersInternal(KBObject obj)
        {
            string parmRule = "";
            var parameters = new List<ParameterInfo>();

            try
            {
                if (obj is Procedure proc) parmRule = proc.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is Transaction trn) parmRule = trn.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is WebPanel wp) parmRule = wp.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is DataProvider dp) parmRule = (string)dp.GetType().GetProperty("Rules")?.GetValue(dp)?.GetType().GetProperty("Source")?.GetValue(((dynamic)dp).Rules) ?? "";
                
                if (string.IsNullOrEmpty(parmRule) && obj is DataProvider dp2) {
                    // DataProvider might have parm in Source instead of Rules in some versions/objects
                    try { 
                        string sourceStr = ((dynamic)dp2).Source.Source;
                        foreach (string line in sourceStr.Split('\n'))
                        {
                            if (line.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase))
                            {
                                parmRule = line;
                                break;
                            }
                        }
                    } catch {}
                }

                if (string.IsNullOrEmpty(parmRule))
                {
                    string rText = ReadPartTextSafe(obj, "Rules");
                    if (!string.IsNullOrEmpty(rText))
                    {
                        parmRule = rText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (!string.IsNullOrEmpty(parmRule))
                {
                    parmRule = parmRule.Trim();
                    var match = System.Text.RegularExpressions.Regex.Match(parmRule, @"parm\s*\((.*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var parmContent = match.Groups[1].Value;
                        var parts = parmContent.Split(',');
                        foreach (var part in parts)
                        {
                            var p = part.Trim();
                            var pInfo = new ParameterInfo { Name = p, Accessor = "in", Type = "Unknown" };
                            if (p.StartsWith("inout:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "inout"; pInfo.Name = p.Substring(6).Trim(); }
                            else if (p.StartsWith("in:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "in"; pInfo.Name = p.Substring(3).Trim(); }
                            else if (p.StartsWith("out:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "out"; pInfo.Name = p.Substring(4).Trim(); }
                            
                            if (pInfo.Name.StartsWith("&")) pInfo.Name = pInfo.Name.Substring(1);
                            parameters.Add(pInfo);
                        }
                    }
                }

                TryResolveParameterTypes(obj, parameters);
            }
            catch { }

            return (parmRule, parameters);
        }

        private static void TryResolveParameterTypes(KBObject obj, List<ParameterInfo> parameters)
        {
            if (parameters == null || parameters.Count == 0) return;
            try
            {
                dynamic vPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj)
                    ?? obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.IndexOf("Variables", StringComparison.OrdinalIgnoreCase) >= 0);
                if (vPart == null) return;

                var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in vPart.Variables)
                {
                    string name = null;
                    string formatted = null;
                    try
                    {
                        name = (string)((dynamic)v).Name;
                        string baseType = ((dynamic)v).Type?.ToString() ?? "Unknown";
                        int len = 0, dec = 0;
                        try { len = (int)((dynamic)v).Length; } catch { }
                        try { dec = (int)((dynamic)v).Decimals; } catch { }

                        // SDT-typed: prefer SDT name when available
                        string sdtName = null;
                        try { sdtName = ((dynamic)v).PromptInformation?.SDTName as string; } catch { }
                        if (!string.IsNullOrEmpty(sdtName))
                        {
                            formatted = sdtName;
                        }
                        else if (len > 0)
                        {
                            formatted = dec > 0 ? $"{baseType}({len},{dec})" : $"{baseType}({len})";
                        }
                        else
                        {
                            formatted = baseType;
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(formatted))
                        byName[name] = formatted;
                }

                foreach (var p in parameters)
                {
                    if (string.IsNullOrEmpty(p.Name)) continue;
                    if (byName.TryGetValue(p.Name, out var t) && !string.IsNullOrEmpty(t))
                        p.Type = t;
                }
            }
            catch { /* keep "Unknown" on failure */ }
        }

        private static void InvalidateCache(object obj)
        {
            try
            {
                var type = typeof(Artech.Architecture.Common.Objects.KBObject).Assembly.GetType("Artech.Architecture.Common.Cache.SingleInstanceModelObjectCache");
                if (type != null)
                {
                    var method = type.GetMethod("Invalidate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    method?.Invoke(null, new object[] { obj });
                    Logger.Debug("InvalidateCache: Object invalidated via reflection.");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("InvalidateCache reflection failed: " + ex.Message);
            }
        }
    }
}
