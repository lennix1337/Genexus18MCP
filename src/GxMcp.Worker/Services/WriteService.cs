using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public partial class WriteService : IWriteServiceFacade
    {
        private readonly ObjectService _objectService;
        private readonly PatternAnalysisService _patternAnalysisService;
        private ValidationService _validationService;
        private static readonly object _persistenceWarmupLock = new object();
        private static bool _persistenceWarmupDone = false;
        private static readonly object _flushLock = new object();
        private static System.Timers.Timer _flushTimer;
        private static bool _pendingCommit = false;

        public WriteService(ObjectService objectService)
        {
            _objectService = objectService;
            _objectServiceRef = objectService; // v2.6.9 — static handle for NotePerTargetWrite → EditDirtyTracker
            _patternAnalysisService = new PatternAnalysisService(objectService);
            InitializeFlushTimer();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => FlushBackground();
        }

        public void SetValidationService(ValidationService vs) { _validationService = vs; }


        private void InitializeFlushTimer()
        {
            if (_flushTimer != null) return;
            lock (_flushLock)
            {
                if (_flushTimer != null) return;
                _flushTimer = new System.Timers.Timer(2000); // 2 seconds debounce
                _flushTimer.AutoReset = false;
                _flushTimer.Elapsed += (s, e) => FlushBackground();
            }
        }

        private void FlushBackground()
        {
            if (!_pendingCommit) return;
            
            lock (_flushLock)
            {
                if (!_pendingCommit) return;
                try
                {
                    Logger.Info("[BACKGROUND-FLUSH] Starting commits...");
                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) return;

                    // Track commit failures: a failed Commit must NOT clear _pendingCommit,
                    // or the write is silently lost (no retry) after the caller was already
                    // told Success. Keep the flag set on failure so the next flush retries.
                    bool commitFailed = false;

                    // Commits
                    var model = kb.DesignModel;
                    if (model != null) {
                        try {
                            var modelCommit = model.GetType().GetMethod("Commit", BindingFlags.Public | BindingFlags.Instance);
                            modelCommit?.Invoke(model, null);
                            Logger.Info("[BACKGROUND-FLUSH] Model.Commit() successful.");
                        } catch (Exception ex) { commitFailed = true; Logger.Error("[BACKGROUND-FLUSH] Model.Commit FAILED (will retry): " + ex.Message); }
                    }

                    try {
                        var kbCommit = kb.GetType().GetMethod("Commit", BindingFlags.Public | BindingFlags.Instance);
                        kbCommit?.Invoke(kb, null);
                        Logger.Info("[BACKGROUND-FLUSH] KB.Commit() successful.");
                    } catch (Exception ex) { commitFailed = true; Logger.Error("[BACKGROUND-FLUSH] KB.Commit FAILED (will retry): " + ex.Message); }

                    if (commitFailed)
                    {
                        Logger.Error("[BACKGROUND-FLUSH] Commit failed; keeping pending flag so the write is retried on the next flush.");
                    }
                    else
                    {
                        _pendingCommit = false;
                        Logger.Info("[BACKGROUND-FLUSH] Full commit cycle complete.");
                    }
                }
                catch (Exception ex)
                {
                    // Leave _pendingCommit set so a later flush retries rather than dropping the write.
                    Logger.Error("[BACKGROUND-FLUSH] ERROR (write left pending for retry): " + ex.Message);
                }
            }
        }

        private void EnsurePersistenceWarmup()
        {
            if (_persistenceWarmupDone) return;

            lock (_persistenceWarmupLock)
            {
                if (_persistenceWarmupDone) return;
                try
                {
                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) return;
                    Logger.Info("[DEBUG-SAVE] Warming up persistence pipeline...");
                    dynamic tx = kb.BeginTransaction();
                    bool committed = false;
                    try
                    {
                        tx.Commit();
                        committed = true;
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try { tx.Rollback(); } catch (Exception rbEx) { Logger.Debug("[DEBUG-SAVE] Warmup rollback failed: " + rbEx.Message); }
                        }
                        try { (tx as IDisposable)?.Dispose(); } catch { }
                    }
                    _persistenceWarmupDone = true;
                    Logger.Info("[DEBUG-SAVE] Persistence warmup complete.");
                }
                catch (Exception ex)
                {
                    Logger.Warn("[DEBUG-SAVE] Persistence warmup failed: " + ex.Message);
                }
            }
        }

        private void ScheduleFlush(bool force = false)
        {
            _pendingCommit = true;
            if (force)
            {
                FlushBackground();
                return;
            }

            lock (_flushLock)
            {
                if (_flushTimer == null) return;
                _flushTimer.Stop();
                _flushTimer.Start();
            }
        }

        private static string GetSdkMessagesSafe(object target)
        {
            try
            {
                return target?.GetSdkMessages();
            }
            catch
            {
                return string.Empty;
            }
        }

        // Documentation / Help parts (DocumentationPart, HelpPart) wrap a WikiPage and do not
        // implement ISource. Their Content/EditableContent properties are read-only on the part,
        // so the generic Source/Content reflection fallback above silently misses them. HelpPart
        // does expose a writable HtmlContent; both expose a writable Page (WikiPage) whose own
        // Content / EditableContent / StorableContent setters accept the new text.
        private static bool TrySetDocumentationContent(
            global::Artech.Architecture.Common.Objects.KBObjectPart part,
            string content,
            out string diagnostic)
        {
            diagnostic = null;
            if (part == null) return false;

            var partType = part.GetType();
            bool isDocumentation =
                partType.GetInterface("Artech.Genexus.Common.Parts.IDocumentation") != null ||
                partType.Name.Equals("DocumentationPart", StringComparison.OrdinalIgnoreCase) ||
                partType.Name.Equals("HelpPart", StringComparison.OrdinalIgnoreCase);
            if (!isDocumentation) return false;

            // 1. HelpPart exposes a writable HtmlContent — preferred when present.
            var htmlProp = partType.GetProperty("HtmlContent", BindingFlags.Public | BindingFlags.Instance);
            if (htmlProp != null && htmlProp.CanWrite && htmlProp.PropertyType == typeof(string))
            {
                try
                {
                    htmlProp.SetValue(part, content ?? string.Empty);
                    diagnostic = "part.HtmlContent";
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Debug("[DEBUG-SAVE] Setting HtmlContent failed: " + ex.Message);
                }
            }

            // 2. Push through the underlying WikiPage. Page may be null on a never-edited part;
            // attempt to materialize one from the module so the first write isn't a no-op.
            var pageProp = partType.GetProperty("Page", BindingFlags.Public | BindingFlags.Instance);
            if (pageProp == null || !pageProp.CanRead) return false;

            object page = null;
            try { page = pageProp.GetValue(part); }
            catch (Exception ex) { Logger.Debug("[DEBUG-SAVE] Reading Page failed: " + ex.Message); }

            if (page == null && pageProp.CanWrite)
            {
                try
                {
                    var wikiPageType = pageProp.PropertyType;
                    object module = null;
                    var moduleProp = partType.GetProperty("Module", BindingFlags.Public | BindingFlags.Instance);
                    if (moduleProp != null && moduleProp.CanRead)
                    {
                        try { module = moduleProp.GetValue(part); } catch { }
                    }

                    if (module != null)
                    {
                        var ctor = wikiPageType.GetConstructor(new[] { module.GetType() });
                        if (ctor != null) page = ctor.Invoke(new[] { module });
                    }
                    if (page == null)
                    {
                        var ctor0 = wikiPageType.GetConstructor(Type.EmptyTypes);
                        if (ctor0 != null) page = ctor0.Invoke(null);
                    }

                    if (page != null)
                    {
                        pageProp.SetValue(part, page);
                        page = pageProp.GetValue(part); // re-read in case the setter wrapped it
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("[DEBUG-SAVE] Instantiating Page failed: " + ex.Message);
                }
            }

            if (page == null) return false;

            foreach (var name in new[] { "EditableContent", "Content", "StorableContent", "InvariantContent" })
            {
                var prop = page.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string)) continue;
                try
                {
                    prop.SetValue(page, content ?? string.Empty);
                    diagnostic = "part.Page." + name;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Debug("[DEBUG-SAVE] Setting page." + name + " failed: " + ex.Message);
                }
            }

            return false;
        }

        private static bool ShouldRetryWithoutPartSave(string partName, global::Artech.Architecture.Common.Objects.KBObjectPart part, Exception ex, string partMessages, JArray issues)
        {
            if (!(part is global::Artech.Architecture.Common.Objects.ISource)) return false;
            if (!WritePolicy.IsLogicalSourcePart(partName))
            {
                return false;
            }

            string exceptionMessage = ex?.Message ?? string.Empty;
            string diagnosticText = WritePolicy.BuildFailureDetails(partMessages, issues);
            return WritePolicy.ShouldRetryWithoutPartSave(partName, exceptionMessage, diagnosticText);
        }

        private static JObject CreateTransactionErrorResponse(string target, string partName, string stage, Exception ex, JArray issues, string retryStrategy, string sdkMessages, global::Artech.Architecture.Common.Objects.KBObject obj = null, string decodedCode = null)
        {
            // Friction-report #2: if the SDK threw bare "Erro" but GetSdkMessages / GetDiagnostics
            // produced the real message (e.g. "src0059: Esperando 'EndFor'..."), surface it as the
            // top-level error instead of the uninformative exception text.
            string enrichedError = WritePolicy.PreferDetailedMessage(ex.Message, sdkMessages, issues);

            // Friction-report 05-13 #3: src0216 undeclared-variable hint.
            string undeclaredHint = null;
            JArray undeclaredVarsArr = null;
            try
            {
                string corpus = (enrichedError ?? string.Empty) + "\n" + (sdkMessages ?? string.Empty);
                if (obj != null && !string.IsNullOrEmpty(decodedCode) &&
                    corpus.IndexOf("src0216", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var declared = CollectDeclaredVariableNames(obj);
                    var undeclared = WritePolicy.FindUndeclaredVariablesForSrc0216(corpus, decodedCode, declared);
                    undeclaredHint = WritePolicy.BuildUndeclaredVariableHint(undeclared);
                    if (!string.IsNullOrEmpty(undeclaredHint))
                    {
                        var arr = new JArray();
                        foreach (var v in undeclared) arr.Add("&" + v);
                        undeclaredVarsArr = arr;
                    }
                }
            }
            catch (Exception hintEx)
            {
                Logger.Debug("[CreateTransactionErrorResponse] undeclared-var hint failed: " + hintEx.Message);
            }

            // issue #39: a Rules save that fails with a bare "Erro" and no SDK diagnostic is
            // almost always an invalid pseudo-rule (e.g. `Unique(att);`). Surface the specific
            // offending keyword with actionable guidance the SDK never gives us.
            string invalidRuleHint = null;
            try
            {
                if (!string.IsNullOrEmpty(partName) &&
                    partName.Equals("Rules", System.StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(decodedCode) &&
                    WritePolicy.IsUninformativeSaveError(enrichedError))
                {
                    invalidRuleHint = WritePolicy.BuildInvalidRuleHint(
                        WritePolicy.FindInvalidRuleKeywords(decodedCode));
                }
            }
            catch (Exception hintEx)
            {
                Logger.Debug("[CreateTransactionErrorResponse] invalid-rule hint failed: " + hintEx.Message);
            }

            string detailText = WritePolicy.BuildFailureDetails(sdkMessages, issues);
            string hint = undeclaredHint
                ?? invalidRuleHint
                ?? (string.IsNullOrWhiteSpace(detailText) ? null : detailText);

            var nextSteps = new JArray(
                McpResponse.NextStep(
                    tool: "genexus_build",
                    args: target != null ? new JObject { ["target"] = target } : null,
                    why: "Building surfaces full SDK diagnostics including the exact line/column of the error."));

            var extra = new JObject();
            if (!string.IsNullOrWhiteSpace(partName)) extra["part"] = partName;
            if (!string.IsNullOrWhiteSpace(stage)) extra["stage"] = stage;
            if (!string.IsNullOrWhiteSpace(retryStrategy)) extra["retryStrategy"] = retryStrategy;
            if (!string.IsNullOrWhiteSpace(detailText)) extra["details"] = detailText;
            if (!string.IsNullOrWhiteSpace(sdkMessages)) extra["sdkMessages"] = sdkMessages;
            if (undeclaredVarsArr != null) extra["undeclaredVariables"] = undeclaredVarsArr;
            extra["stackTrace"] = ex.StackTrace;
            if (issues != null) extra["issues"] = issues;
            if (!string.Equals(enrichedError, (ex.Message ?? string.Empty).Trim(), System.StringComparison.Ordinal))
                extra["originalError"] = ex.Message;

            string errJson = McpResponse.Err(
                code: "TransactionFailed",
                message: enrichedError,
                hint: hint,
                nextSteps: nextSteps,
                target: target,
                extra: extra);
            return JObject.Parse(errJson);
        }

        // Loose equality on the Structure DSL: compare on the set of `Name : Type` tokens,
        // not exact whitespace/length round-trip. We just want to know whether the items we
        // requested are actually present in the persisted state.
        private static bool StructureDslMatches(string expected, string actual)
        {
            var e = ExtractStructureLineSet(expected);
            var a = ExtractStructureLineSet(actual);
            if (e.Count == 0) return true; // nothing was requested; treat as ok
            // Every requested line must appear (by name) in the persisted result.
            return e.IsSubsetOf(a);
        }

        private static HashSet<string> ExtractStructureLineSet(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return set;
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("{") || line.StartsWith("}")) continue;
                // Reduce "AluCod : NUMERIC(8,0)" → "AluCod : NUMERIC" so length/decimals drift
                // doesn't cause a false negative when the DSL renderer drops them.
                int colon = line.IndexOf(':');
                if (colon < 0) { set.Add(line); continue; }
                string name = line.Substring(0, colon).Trim();
                string rest = line.Substring(colon + 1).Trim();
                int paren = rest.IndexOf('(');
                if (paren >= 0) rest = rest.Substring(0, paren).Trim();
                set.Add(name + " : " + rest);
            }
            return set;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // Reads the declared variable names from the object's Variables part. Used to
        // distinguish "user wrote &Var.Foo without declaring &Var" (real bug) from
        // "&Var is declared but the SDT has no Foo" (different real bug — leave alone).
        private static IEnumerable<string> CollectDeclaredVariableNames(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            if (obj == null) yield break;
            global::Artech.Genexus.Common.Parts.VariablesPart varPart = null;
            try
            {
                foreach (var p in obj.Parts)
                {
                    if (p is global::Artech.Genexus.Common.Parts.VariablesPart vp) { varPart = vp; break; }
                }
            }
            catch { }
            if (varPart == null) yield break;

            System.Collections.IEnumerable vars = null;
            try { vars = varPart.Variables as System.Collections.IEnumerable; } catch { }
            if (vars == null) yield break;
            foreach (dynamic v in vars)
            {
                string name = null;
                try { name = v.Name; } catch { }
                if (!string.IsNullOrWhiteSpace(name)) yield return name;
            }
        }

        // Friction 2026-05-22: parallel genexus_edit calls on the same target raced
        // — the first applied, the rest hit "Context block not found" because the
        // file hash changed beneath them. Serialize per-target so callers don't
        // have to. The lock is taken at the facade boundary so BulkWrite, the
        // edit_and_build orchestrator, and the patch path all share it.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _perTargetLocks
            = new System.Collections.Concurrent.ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        // lastWriteAt is set under the per-target lock; readers (patch failure path)
        // use it to detect concurrent modification vs. a real context-mismatch.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastWriteAtUtc
            = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        internal static object AcquirePerTargetLock(string target)
        {
            // A blank/whitespace target must still serialize against other blank-target
            // writers — returning `new object()` gave each caller its own lock and
            // silently disabled the per-target serialization this method exists for.
            // Route them through a shared sentinel key instead.
            string key = string.IsNullOrWhiteSpace(target) ? " <empty-target>" : target;
            return _perTargetLocks.GetOrAdd(key, _ => new object());
        }

        internal static void NotePerTargetWrite(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            _lastWriteAtUtc[target] = DateTime.UtcNow;
            // v2.6.9 — record edit so the next build of this target does a full
            // BuildOne (spec+gen+compile) rather than the compile-only fast path.
            // kbPath best-effort: missing kb maps to a "<no-kb>" bucket which is
            // still safe (everything in that bucket is treated as dirty by
            // EditDirtyTracker for unknown-but-not-clean keys).
            try
            {
                string kbPath = null;
                try { kbPath = _objectServiceRef?.GetKbService()?.GetKbPath(); } catch { }
                EditDirtyTracker.MarkDirty(kbPath, target);
            }
            catch { /* dirty tracking is best-effort */ }
        }

        // Resolved lazily via the WriteService instance ctor so the static
        // NotePerTargetWrite can reach KbService without a per-call lookup.
        private static ObjectService _objectServiceRef;

        internal static bool WasTargetWrittenSince(string target, DateTime sinceUtc)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            return _lastWriteAtUtc.TryGetValue(target, out var t) && t > sinceUtc;
        }

        // Issue #24 — empty-persist guard. A non-empty logical-source write that
        // lands as an empty part on disk (GeneXus silently drops source containing
        // inline native-code delimiters `[! !]`) used to return WriteApplied with
        // the empty-string hash. Two consequences are tracked here:
        //   1) the false success is rewritten to a WriteNotPersisted error (see
        //      ApplyEmptyPersistGuard), and
        //   2) the in-memory SDK part still holds the content the save dropped, so
        //      the next identical write short-circuits to WriteNoChange forever.
        //      We flag the (target, part) so WriteObjectInternal bypasses that
        //      short-circuit and re-attempts (re-firing the guard) instead of
        //      reporting a phantom "no change".
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _suspectEmptyPersist
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static string SuspectKey(string target, string partName)
            => (target ?? string.Empty) + "|" + (string.IsNullOrWhiteSpace(partName) ? "Source" : partName);

        internal static bool IsEmptyPersistPending(string target, string partName)
            => _suspectEmptyPersist.ContainsKey(SuspectKey(target, partName));

        /// <summary>
        /// Issue #24 — decides whether a write that the SDK reported as applied
        /// actually lost the content. True only when a logical source part
        /// (Source / Events / Code) received non-whitespace input but the
        /// re-read persisted source hashes to the empty string. Pure: no SDK,
        /// no I/O — unit-tested directly.
        /// </summary>
        internal static bool ShouldRejectEmptyPersist(string partName, string inputCode, string persistedHash)
        {
            if (string.IsNullOrWhiteSpace(inputCode)) return false;       // intentional blank write
            if (string.IsNullOrEmpty(persistedHash)) return false;        // no re-read happened
            if (!WritePolicy.IsLogicalSourcePart(string.IsNullOrWhiteSpace(partName) ? "Source" : partName)) return false;
            return string.Equals(persistedHash, ComputeSha256(string.Empty), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Issue #24 — post-write guard. Inspects the wrapped response (which already
        /// carries <c>persistedHash</c> from <see cref="WrapWithPersistedState"/>). When
        /// a non-empty logical-source write persisted as empty, rewrite the false
        /// <c>WriteApplied</c> into a <c>WriteNotPersisted</c> error and flag the target so
        /// the next write doesn't get stuck on a phantom WriteNoChange. Otherwise clears
        /// any prior flag (a real non-empty persist recovered the part).
        /// </summary>
        internal static string ApplyEmptyPersistGuard(string wrappedJson, string target, string partName, string inputCode)
        {
            JObject parsed;
            try { parsed = JObject.Parse(wrappedJson); }
            catch { return wrappedJson; }

            // Only fire on a reported-successful write; errors/no-change pass through.
            string status = parsed["status"]?.ToString();
            string code = parsed["code"]?.ToString();
            bool reportedApplied = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(code, "WriteApplied", StringComparison.OrdinalIgnoreCase);
            if (!reportedApplied) return wrappedJson;

            string normPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            string persistedHash = parsed["persistedHash"]?.ToString();

            if (!ShouldRejectEmptyPersist(normPart, inputCode, persistedHash))
            {
                // Confirmed non-empty persist of a logical source — recovery point.
                if (WritePolicy.IsLogicalSourcePart(normPart) && !string.IsNullOrWhiteSpace(inputCode))
                    _suspectEmptyPersist.TryRemove(SuspectKey(target, normPart), out _);
                return wrappedJson;
            }

            _suspectEmptyPersist[SuspectKey(target, normPart)] = true;
            Logger.Error($"[EMPTY-PERSIST] {target} ({normPart}): {inputCode.Length}-char write reported applied but persisted source is empty. Surfacing WriteNotPersisted.");

            var nextSteps = new JArray(
                McpResponse.NextStep(
                    tool: "genexus_history",
                    args: new JObject { ["action"] = "restore", ["discard"] = true, ["target"] = target },
                    why: "Restores the part bytes captured immediately before this write from the latest snapshot."),
                McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = normPart },
                    why: "Confirms whether the persisted source is empty so you can recover before re-editing."));

            return McpResponse.Err(
                code: "WriteNotPersisted",
                message: "Write was reported as applied but the persisted source is empty — the content was not saved.",
                hint: "The SDK save reported success but the part re-read as empty (a save that lands during a background index or an SDK normalization edge can drop content). Restore via genexus_history, or re-run the edit once the KB is idle; if it recurs, edit that object in the GeneXus IDE.",
                nextSteps: nextSteps,
                target: target,
                extra: new JObject
                {
                    ["part"] = normPart,
                    ["inputLength"] = inputCode.Length,
                    ["persistedHash"] = persistedHash
                });
        }

        // IWriteServiceFacade adapter — translates JObject args into the canonical WriteObject call.
        public string WriteObject(string target, JObject args)
        {
            var facadeArgs = NormalizeFacadeArgs(args);

            // Optimistic-concurrency guard (stale-edit data-loss fix): if the caller
            // passed the versionToken from the read this edit is based on, refuse the
            // write when the object changed since (e.g. the user edited it in the IDE).
            // Better a StaleObject error the agent can recover from than silently
            // clobbering the user's concurrent change. No token → no guard (back-compat).
            if (!string.IsNullOrEmpty(facadeArgs.BaseVersion) && !facadeArgs.DryRun)
            {
                string staleErr = CheckStaleVersion(target, facadeArgs.TypeFilter, facadeArgs.BaseVersion);
                if (staleErr != null) return staleErr;
            }

            string raw;
            if (string.Equals(facadeArgs.Mode, "patch", StringComparison.OrdinalIgnoreCase))
            {
                var patchService = new PatchService(_objectService, this, _patternAnalysisService);
                raw = patchService.ApplyPatch(
                    target,
                    facadeArgs.PartName,
                    facadeArgs.Operation,
                    facadeArgs.Content,
                    facadeArgs.Context,
                    facadeArgs.ExpectedCount,
                    facadeArgs.TypeFilter,
                    facadeArgs.DryRun,
                    facadeArgs.VerifyRollback,
                    facadeArgs.ReturnPostState,
                    facadeArgs.Verbose,
                    facadeArgs.ReplaceAll);
            }
            else
            {
                // Async follow-up (next-major item #2): a full visual write re-reads and
                // diff-verifies the persisted WebForm XML, which dominates wall-clock on large
                // PatternInstance writes and is the cause of client timeouts. validate="best-effort"
                // skips that post-write SDK verify (the save status / compiler messages are still
                // checked); validate=null/"strict" keeps it. "only" already short-circuits to dryRun.
                bool strictVerify = !string.Equals(facadeArgs.Validate, "best-effort", StringComparison.OrdinalIgnoreCase);
                raw = WriteObject(
                    target,
                    facadeArgs.PartName,
                    facadeArgs.Content,
                    facadeArgs.TypeFilter,
                    true,
                    false,
                    true,
                    facadeArgs.DryRun,
                    facadeArgs.ExplicitBase64,
                    strictVerify);
            }

            // Friction 2026-05-22: KBs default to WIN1252 (codepage 1252) on
            // Windows. When the caller writes content containing chars outside
            // that codepage (typically a unicode symbol or math glyph used in a
            // caption), the SDK accepts the write, generation succeeds, runtime
            // shows '?'. Warn explicitly so the caller can swap glyphs before
            // building.
            try
            {
                    var unrepresentable = CollectNonWin1252Glyphs(args);
                if (unrepresentable.Count > 0)
                {
                    var parsed = JObject.Parse(raw);
                    var charsetWarn = new JObject
                    {
                        // Friction 2026-05-22 #62: was snake_case "kb_charset_lossy";
                        // standardized to PascalCase Lint* with resolvable docUrl.
                        ["code"] = GotchaCodes.LintKbCharsetLossy,
                        ["docUrl"] = GotchaCodes.DocUrlFor(GotchaCodes.LintKbCharsetLossy),
                        ["message"] = "Content contains characters outside the KB's WIN1252 charset (will render as '?' at runtime): " + string.Join(", ", unrepresentable),
                        ["hint"] = "Replace with ASCII equivalents (e.g. ✓ -> 'OK', ⧖ -> '[wait]'), or change the KB's NLS_CHARACTERSET if you need full unicode."
                    };
                    // Preserve any pre-existing warnings regardless of shape — earlier
                    // writers may produce a JArray, a JObject keyed by code, or even
                    // a scalar summary. Don't clobber.
                    var existing = parsed["warnings"];
                    JArray warnings;
                    if (existing is JArray arr)
                    {
                        warnings = arr;
                    }
                    else if (existing != null && existing.Type != JTokenType.Null)
                    {
                        warnings = new JArray { existing.DeepClone() };
                    }
                    else
                    {
                        warnings = new JArray();
                    }
                    warnings.Add(charsetWarn);
                    parsed["warnings"] = warnings;
                    raw = parsed.ToString(Newtonsoft.Json.Formatting.None);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[CHARSET-WARN] skipped: " + ex.Message);
            }
            return raw;
        }

        // Optimistic-concurrency token for an object: its last-modification timestamp
        // (UTC ticks). Cheap, and advances on any save — the worker's own writes, the
        // background enrichment, or an external IDE edit. The watcher keeps the SDK's
        // LastUpdate current for externally-modified objects, so a token mismatch at
        // write time means the object moved on since the caller's read.
        internal static string ComputeVersionToken(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            if (obj == null) return null;
            try { return obj.LastUpdate.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        // Returns a StaleObject error envelope when the object's current version token
        // no longer matches the caller-supplied baseVersion, else null (proceed).
        private string CheckStaleVersion(string target, string typeFilter, string baseVersion)
        {
            global::Artech.Architecture.Common.Objects.KBObject obj;
            try { obj = _objectService.FindObject(target, typeFilter); }
            catch { return null; }
            if (obj == null) return null; // not-found is handled by the normal write path
            string current = ComputeVersionToken(obj);
            if (current == null) return null; // can't compute a token → don't block the write
            if (string.Equals(current, baseVersion, StringComparison.Ordinal)) return null; // unchanged — proceed

            return McpResponse.Err(
                code: "StaleObject",
                message: "The object changed since you last read it (baseVersion no longer matches its current version). The edit was NOT applied, to avoid overwriting the newer version — e.g. a change made in the GeneXus IDE.",
                hint: "Re-read with genexus_read to get the current content and versionToken, re-apply your change on top of it, then retry the edit passing the new baseVersion.",
                nextSteps: new JArray(McpResponse.NextStep("genexus_read", new JObject { ["name"] = target }, "Fetch the current version before re-editing.")),
                target: target,
                extra: new JObject { ["expectedVersion"] = baseVersion, ["currentVersion"] = current });
        }

        internal static FacadeWriteArgs NormalizeFacadeArgs(JObject args)
        {
            args = args ?? new JObject();

            string facadeValidate = args["validate"]?.ToString();
            bool facadeDryRun = (args["dryRun"]?.ToObject<bool?>() ?? false)
                || string.Equals(facadeValidate, "only", StringComparison.OrdinalIgnoreCase);

            string mode = args["mode"]?.ToString();
            string partName = args["part"]?.ToString() ?? "Source";
            string typeFilter = args["type"]?.ToString();
            string operation = args["operation"]?.ToString() ?? "Replace";
            string context = args["context"]?.ToString();
            string content = args["content"]?.ToString() ?? string.Empty;
            string encoding = args["encoding"]?.ToString();

            if (args["content"] is JObject patchShape && string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase))
            {
                context = patchShape["find"]?.ToString() ?? context;
                content = patchShape["replace"]?.ToString() ?? string.Empty;
            }

            return new FacadeWriteArgs
            {
                Mode = mode,
                Validate = facadeValidate,
                PartName = partName,
                TypeFilter = typeFilter,
                DryRun = facadeDryRun,
                Operation = operation,
                Context = context,
                Content = content,
                ExpectedCount = args["expectedCount"]?.ToObject<int?>() ?? 1,
                VerifyRollback = args["verifyRollback"]?.ToObject<bool?>() ?? false,
                ReturnPostState = args["return_post_state"]?.ToObject<bool?>() ?? true,
                Verbose = args["verbose"]?.ToObject<bool?>() ?? false,
                ReplaceAll = args["replaceAll"]?.ToObject<bool?>() ?? false,
                ExplicitBase64 = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase),
                // Optimistic concurrency: the versionToken the caller got from the
                // genexus_read this edit is based on. When present, the write is
                // refused if the object changed since (StaleObject) — see the guard
                // in the facade. Accept a couple of aliases for ergonomics.
                BaseVersion = args["baseVersion"]?.ToString()
                    ?? args["expectedVersion"]?.ToString()
                    ?? args["versionToken"]?.ToString()
            };
        }

        internal sealed class FacadeWriteArgs
        {
            public string Mode { get; set; }
            public string Validate { get; set; }
            public string PartName { get; set; }
            public string TypeFilter { get; set; }
            public bool DryRun { get; set; }
            public string Operation { get; set; }
            public string Context { get; set; }
            public string Content { get; set; }
            public int ExpectedCount { get; set; }
            public bool VerifyRollback { get; set; }
            public bool ReturnPostState { get; set; }
            public bool Verbose { get; set; }
            public bool ReplaceAll { get; set; }
            public bool ExplicitBase64 { get; set; }
            public string BaseVersion { get; set; }
        }

        // Returns a deduped list of glyphs in the args payload that cannot
        // round-trip through codepage 1252 (KB default on Windows). Only scans
        // properties that contribute to the PERSISTED content — find/context
        // anchors describe the existing source we're matching against (and the
        // caller may legitimately be removing a lossy glyph), so flagging them
        // would produce spurious warnings.
        private static readonly System.Collections.Generic.HashSet<string> _readOnlyPatchKeys
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "find", "context", "anchor", "old_string", "expectedCount" };

        internal static System.Collections.Generic.List<string> CollectNonWin1252Glyphs(JObject args)
        {
            var result = new System.Collections.Generic.List<string>();
            if (args == null) return result;
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            System.Text.Encoding enc;
            try
            {
                enc = System.Text.Encoding.GetEncoding(1252,
                    new System.Text.EncoderExceptionFallback(),
                    new System.Text.DecoderExceptionFallback());
            }
            catch { return result; }

            ScanTokenForLossyGlyphs(args, enc, seen, result);
            return result;
        }

        private static void ScanTokenForLossyGlyphs(JToken token, System.Text.Encoding enc,
            System.Collections.Generic.HashSet<string> seen, System.Collections.Generic.List<string> result)
        {
            if (token == null || result.Count >= 20) return;
            switch (token.Type)
            {
                case JTokenType.Object:
                    foreach (var prop in ((JObject)token).Properties())
                    {
                        if (_readOnlyPatchKeys.Contains(prop.Name)) continue;
                        ScanTokenForLossyGlyphs(prop.Value, enc, seen, result);
                        if (result.Count >= 20) return;
                    }
                    break;
                case JTokenType.Array:
                    foreach (var item in (JArray)token)
                    {
                        ScanTokenForLossyGlyphs(item, enc, seen, result);
                        if (result.Count >= 20) return;
                    }
                    break;
                case JTokenType.String:
                    string s = token.Value<string>();
                    if (string.IsNullOrEmpty(s)) return;
                    var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(s);
                    while (enumerator.MoveNext())
                    {
                        string rune = (string)enumerator.Current;
                        try { enc.GetBytes(rune); }
                        catch (System.Text.EncoderFallbackException)
                        {
                            if (seen.Add(rune)) result.Add(rune);
                            if (result.Count >= 20) return;
                        }
                    }
                    break;
            }
        }

        public string WriteObject(string target, string partName, string code, string typeFilter = null, bool autoValidate = true, bool preferFastSourceSave = false, bool autoInjectVariables = true, bool dryRun = false, bool explicitBase64 = false, bool strictVerify = true)
        {
            // Friction 2026-05-22 fix: hold the per-target lock around the
            // ENTIRE write pipeline (snapshot + internal write + wrap). This is
            // the canonical writer — PatchService, BulkWrite, and the JObject
            // facade all reach here, so the lock covers every parallel-edit case
            // (patch-vs-patch was bypassing the prior lock site). NotePerTargetWrite
            // fires on the success path so a sibling patch racing against this
            // write can detect the concurrent modification and report Stale.
            lock (AcquirePerTargetLock(target))
            {
            // Advisory lock check — honours GXMCP_WRITE_OWNER_ID / GXMCP_WRITE_FORCE env vars.
            // Reads the .gx/locks/<target>__<part>.lock file written by genexus_multi_agent_lock.
            // Returns an error envelope immediately if a different, non-expired owner holds the lock.
            // Best-effort: any exception inside AdvisoryLockCheck is swallowed and the write proceeds.
            if (!dryRun)
            {
                string advisoryOwnerId = System.Environment.GetEnvironmentVariable("GXMCP_WRITE_OWNER_ID");
                bool advisoryForce = string.Equals(
                    System.Environment.GetEnvironmentVariable("GXMCP_WRITE_FORCE"), "1",
                    StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(advisoryOwnerId))
                {
                    string kbPathForLock = null;
                    try { kbPathForLock = _objectService.GetKbService().GetKbPath(); } catch { }
                    var lockError = GxMcp.Worker.Helpers.WritePipeline.AdvisoryLockCheck(
                        kbPathForLock, target, partName ?? "Source", advisoryOwnerId, advisoryForce);
                    if (lockError != null)
                        return lockError.ToString(Newtonsoft.Json.Formatting.None);
                }
            }

            // PERFORMANCE (instrumentation): wrap the entire write pipeline (SDK ops + validation +
            // persistedHash projection) in a Stopwatch so unusually-slow object saves surface in
            // worker_debug.log. Threshold 250ms matches user-performed friction: anything over that
            // is worth diagnosing.
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // v2.6.6 FR#11 — pre-write snapshot. Capture prior persisted content
            // BEFORE WriteObjectInternal mutates SDK state. Snapshot is best-effort:
            // any failure logs at warn and DOES NOT block the write (the caller still
            // gets persistedHash + persistedSnippet on the response envelope).
            GxMcp.Worker.Helpers.EditSnapshotStore.SnapshotInfo snapshot = null;
            if (!dryRun)
            {
                snapshot = TryCapturePreWriteSnapshot(target, partName, typeFilter);
            }

            string raw;
            try
            {
                raw = WriteObjectInternal(target, partName, code, typeFilter, autoValidate, preferFastSourceSave, autoInjectVariables, dryRun, explicitBase64, strictVerify);
            }
            finally
            {
                sw.Stop();
                if (sw.ElapsedMilliseconds > 250)
                {
                    Logger.Info($"[OBJ-SAVE-SLOW] {sw.ElapsedMilliseconds}ms target='{target}' part='{partName}' codeLen={code?.Length ?? 0} dryRun={dryRun}");
                }
            }
            if (!dryRun) NotePerTargetWrite(target);
            // v2.3.8 Task 3.4: every edit response carries persistedHash + persistedSnippet
            // (success, no-change, dry-run, rollback, or error).
            // Default sdkPath = typed-sdk; deeper writers (LayoutService raw-XML) tag their own
            // sdkPath first and WrapWithPersistedState is idempotent so it preserves that.
            string wrapped = WrapWithPersistedState(raw, target, string.IsNullOrWhiteSpace(partName) ? "Source" : partName, GxMcp.Worker.Helpers.WriteResultMeta.TypedSdk, snapshot?.PriorContent, code);

            // Issue #24 — never report WriteApplied when a non-empty source write
            // landed as an empty part on disk. Runs against the persistedHash the
            // wrap just computed; turns the silent data loss into a recoverable error.
            if (!dryRun)
                wrapped = ApplyEmptyPersistGuard(wrapped, target, partName, code);

            // issue #31.2: a no-op write (WrapWithPersistedState flipped code to
            // WriteNoChange because persisted == prior) shouldn't keep the pre-write
            // snapshot .bak it just wrote — delete it and skip the snapshot envelope.
            bool wasNoOp = false;
            try { wasNoOp = string.Equals(JObject.Parse(wrapped)["code"]?.ToString(), "WriteNoChange", StringComparison.OrdinalIgnoreCase); }
            catch { }

            // Attach snapshot envelope to the response so callers can restore.
            if (snapshot != null && !wasNoOp)
            {
                try
                {
                    var parsed = JObject.Parse(wrapped);
                    parsed["snapshot"] = new JObject
                    {
                        ["path"] = snapshot.Path,
                        ["timestamp"] = snapshot.Timestamp,
                        ["guid"] = snapshot.Guid,
                        ["part"] = snapshot.Part,
                        ["compressed"] = snapshot.Compressed,
                        ["bytes"] = snapshot.Bytes
                    };
                    wrapped = parsed.ToString(Newtonsoft.Json.Formatting.None);
                }
                catch (Exception ex)
                {
                    Logger.Debug("[SNAPSHOT] envelope attach failed: " + ex.Message);
                }
            }
            else if (snapshot != null && wasNoOp)
            {
                try { if (!string.IsNullOrEmpty(snapshot.Path) && System.IO.File.Exists(snapshot.Path)) System.IO.File.Delete(snapshot.Path); }
                catch (Exception ex) { Logger.Debug("[SNAPSHOT] no-op cleanup failed: " + ex.Message); }
            }
            return wrapped;
            } // end lock (AcquirePerTargetLock)
        }

        /// <summary>
        /// v2.6.6 FR#11 — capture the on-disk content of <paramref name="partName"/>
        /// to <c>&lt;kbPath&gt;/.gx/snapshots/&lt;guid&gt;-&lt;part&gt;-&lt;utc-iso&gt;.bak</c>
        /// before any destructive WriteObject. Returns the snapshot descriptor or
        /// <c>null</c> when the prior content could not be retrieved (no KB open,
        /// object missing, part has no textual representation, etc.).
        /// </summary>
        private GxMcp.Worker.Helpers.EditSnapshotStore.SnapshotInfo TryCapturePreWriteSnapshot(string target, string partName, string typeFilter)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return null;
                string guid = null;
                try { guid = obj.Guid.ToString(); } catch { }
                if (string.IsNullOrEmpty(guid)) return null;

                string resolvedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                string priorContent = null;
                try
                {
                    // issue #43 #2 (incomplete snapshot): read the FULL part. The old call
                    // passed offset=null/limit=null with client="mcp", which triggers the
                    // ~200-line / 16KB MCP pagination default — so the pre-write .bak captured
                    // only the head of a large part and could NOT restore it. offset=0/limit=0 is
                    // the explicit "no pagination, return everything" opt-out (ReadPagination).
                    // typeFilter is forwarded so a homonym Transaction/Table snapshots the right object.
                    string readJson = _objectService.ReadObjectSource(target, resolvedPart, 0, 0, "mcp", false, typeFilter);
                    if (!string.IsNullOrWhiteSpace(readJson))
                    {
                        var parsed = JObject.Parse(readJson);
                        priorContent = parsed["source"]?.ToString()
                            ?? parsed["content"]?.ToString();
                    }
                }
                catch (Exception readEx)
                {
                    Logger.Debug("[SNAPSHOT] prior-read failed for " + target + "/" + resolvedPart + ": " + readEx.Message);
                    return null;
                }
                if (priorContent == null) return null;

                string kbPath = null;
                try { kbPath = _objectService.GetKbService().GetKbPath(); } catch { }
                string snapshotRoot = GxMcp.Worker.Helpers.EditSnapshotStore.ResolveRoot(kbPath);

                return GxMcp.Worker.Helpers.EditSnapshotStore.SaveSnapshot(snapshotRoot, guid, resolvedPart, priorContent);
            }
            catch (Exception ex)
            {
                Logger.Warn("[SNAPSHOT] capture skipped for " + target + "/" + partName + ": " + ex.Message);
                return null;
            }
        }

        private string WriteObjectInternal(string target, string partName, string code, string typeFilter = null, bool autoValidate = true, bool preferFastSourceSave = false, bool autoInjectVariables = true, bool dryRun = false, bool explicitBase64 = false, bool strictVerify = true)
        {
            try
            {
                partName = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;

                // DEBUG ENCODING: Detect and decode Base64 if needed
                string decodedCode = code;
                bool usedBase64Sniff = false;
                if (!string.IsNullOrEmpty(code))
                {
                    if (explicitBase64)
                    {
                        // Explicit encoding:base64 flag — decode unconditionally.
                        try {
                            byte[] data = Convert.FromBase64String(code);
                            decodedCode = System.Text.Encoding.UTF8.GetString(data);
                            Logger.Info("[DEBUG-SAVE] Payload decoded from Base64 (explicit encoding:base64).");
                        } catch (Exception b64Ex) {
                            Logger.Warn($"[DEBUG-SAVE] encoding=base64 specified but decode failed: {b64Ex.Message}");
                        }
                    }
                    else if (code.EndsWith("=") && code.Length >= 20 && !code.Contains("\n") && !code.Contains(" "))
                    {
                        // Sniff: only try if it looks like a pure base64 token (no spaces/newlines)
                        // AND round-trip produces valid UTF-8 text without NUL bytes.
                        try {
                            byte[] data = Convert.FromBase64String(code);
                            string candidate = System.Text.Encoding.UTF8.GetString(data);
                            if (!candidate.Contains('\0') && candidate.Length > 0) {
                                decodedCode = candidate;
                                usedBase64Sniff = true;
                                Logger.Warn($"[DEBUG-SAVE] Base64 auto-detected via sniff (len={code.Length}); pass encoding:\"base64\" explicitly to suppress this warning.");
                            }
                        } catch { /* Not base64, use as is */ }
                    }
                }

                Logger.Info(string.Format("[DEBUG-SAVE] Request received for {0} (Part: {1}, Code Length: {2})", target, partName, decodedCode?.Length ?? 0));

                // SP4.T1: When no type filter is given and the index contains multiple entries
                // with the same name (different types), return an inline alternatives array so
                // the caller can disambiguate without a separate list_objects round-trip.
                if (typeFilter == null && !target.Contains(":"))
                {
                    var candidates = _objectService.FindCandidateEntries(target);
                    // Only signal ambiguity when there are genuinely different types; a single
                    // entry (or all entries of the same type) is not ambiguous.
                    var distinctTypes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in candidates) { if (!string.IsNullOrEmpty(c.Type)) distinctTypes.Add(c.Type); }
                    if (distinctTypes.Count > 1)
                    {
                        var alternatives = new JArray();
                        foreach (var c in candidates)
                        {
                            alternatives.Add(new JObject
                            {
                                ["name"] = c.Name,
                                ["type"] = c.Type,
                                ["parentPath"] = c.ParentPath ?? c.Path ?? string.Empty
                            });
                        }
                        return McpResponse.Err(
                            code: "AmbiguousObjectName",
                            message: "Ambiguous object name",
                            hint: "Disambiguate by passing 'type' or by using a fully-qualified parentPath. Retry with one of the alternatives' (name, type) pairs.",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_list_objects",
                                args: new JObject { ["name"] = target },
                                why: "Lists all objects matching the name with their type so you can pass the correct type parameter.")),
                            target: target,
                            extra: new JObject { ["alternatives"] = alternatives });
                    }
                }

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) {
                    Logger.Error("[DEBUG-SAVE] Object NOT FOUND: " + target);
                    return CreateWriteError(
                        "Object not found",
                        target,
                        partName,
                        "The shadow file points to an object that is not available in the active Knowledge Base."
                    );
                }

                Logger.Debug(string.Format("[DEBUG-SAVE] Object Found: {0} ({1})", obj.Name, obj.TypeDescriptor.Name));

                if (PatternAnalysisService.IsPatternPart(partName))
                {
                    return WritePatternPart(obj, target, partName, decodedCode, dryRun, strictVerify);
                }

                if (WebFormXmlHelper.IsVisualPart(partName))
                {
                    return WriteVisualPart(obj, target, partName, decodedCode, dryRun, strictVerify);
                }

                if (dryRun)
                {
                    return Models.McpResponse.Ok(
                        target: target,
                        code: "WriteDryRun",
                        result: new JObject
                        {
                            ["part"] = partName,
                            ["details"] = "Dry-run for non-pattern/visual parts: input received; not validated against SDK. Save skipped.",
                            ["verified"] = new JArray("inputReceived"),
                            ["savePathExercised"] = false
                        });
                }

                // ... (rest of the log)
                // 1. VIRTUAL/DSL PARTS INTERCEPTOR (Prioritize over physical part resolution for Structure)
                // issue #31.1: genexus_read / availableParts report an SDT's structure part as
                // "SDTStructure", so authors naturally write to part="SDTStructure". Without
                // accepting that alias here the write bypassed the DSL parser entirely and was a
                // silent no-op (the Numeric(len) token — and any structural change — was dropped).
                if (partName.Equals("Structure", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("SDTStructure", StringComparison.OrdinalIgnoreCase))
                {
                    var objToUpdate = _objectService.FindObject(target, typeFilter);
                    if (objToUpdate != null && (objToUpdate is global::Artech.Genexus.Common.Objects.Transaction || objToUpdate.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)))
                    {
                        try {
                            StructureParser.ParseFromText(objToUpdate, decodedCode);
                            objToUpdate.EnsureSave();
                            // Friction-report 05-13 #2: a Structure write on an SDT or Transaction
                            // must commit synchronously, not via the debounced ScheduleFlush()
                            // timer. Subsequent requests (e.g. a Procedure that references
                            // &Var.Field) re-validate against the persisted KB. If the SDT items
                            // are still only in-memory when the next save runs, the validator
                            // reloads from disk (still seed-only) and fires `src0216 propriedade
                            // inválida` on the field access. Force the flush here so disk reflects
                            // the new items before we return Success.
                            ScheduleFlush(force: true);

                            // Friction-report 05-13 #2 deeper finding: the SDK persists the
                            // SDT to the Design model and to disk, but a parallel Prototype
                            // model (model id 2) — used by the validator when other objects
                            // consume this SDT — never gets the corresponding
                            // ModelEntityVersion rows for the SDTLevelEntity/SDTItemEntity
                            // names. IDE-created SDTs have those rows; SDK-create-via-MCP
                            // doesn't propagate them. Mirror Model 1 → Model 2 for our
                            // newly-saved SDT directly in SQL so the validator can resolve
                            // `&Var.<Field>` from the prototype model after this Structure
                            // write.
                            if (objToUpdate.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    string kbPath = _objectService.GetKbService().GetKbPath();
                                    GxMcp.Worker.Helpers.SdtModelPropagation.TryPropagateToPrototypeModel(objToUpdate, kbPath);
                                }
                                catch (Exception propEx)
                                {
                                    Logger.Warn("[SDT-PROP] propagation failed for " + target + ": " + propEx.Message);
                                }
                            }

                            _objectService.MarkReadCacheDirty(objToUpdate, partName);

                            // Friction-report 05-13 #2: confirm the Save actually persisted the
                            // new items by re-reading the structure from a forced cache miss.
                            // If the DSL we just wrote doesn't round-trip, the SDT structure
                            // part is in the stale-tree failure mode and the validator on the
                            // next consumer (Procedure that references &Var.Field) will report
                            // `src0216` even though our Success response said otherwise.
                            string roundTripError = null;
                            try
                            {
                                var verifyObj = _objectService.FindObject(target, typeFilter);
                                if (verifyObj != null)
                                {
                                    _objectService.MarkReadCacheDirty(verifyObj, partName);
                                    string persisted = GxMcp.Worker.Helpers.StructureParser.SerializeToText(verifyObj);
                                    if (!StructureDslMatches(decodedCode, persisted))
                                    {
                                        roundTripError = "Structure DSL applied in-memory but post-Save read-back didn't include all items. The SDK may have persisted the prior version. Re-read with genexus_read part=Structure and retry; if still wrong, the SDT's persisted EntityVersion is stale (see WebFormCompositionRepair pattern).";
                                        Logger.Warn("[DEBUG-SAVE] SDT Structure round-trip mismatch for " + target + ". expected=\"" + Truncate(decodedCode, 200) + "\" persisted=\"" + Truncate(persisted, 200) + "\"");
                                    }
                                }
                            }
                            catch (Exception verEx)
                            {
                                Logger.Debug("[DEBUG-SAVE] SDT round-trip verify threw: " + verEx.Message);
                            }

                            var okPayload = new JObject { ["details"] = "Structure DSL successfully applied" };
                            if (!string.IsNullOrEmpty(roundTripError))
                            {
                                okPayload["persistedVerified"] = false;
                                okPayload["persistedVerifyError"] = roundTripError;
                            }
                            else
                            {
                                okPayload["persistedVerified"] = true;
                            }
                            return Models.McpResponse.Ok(
                                target: target,
                                code: "WriteApplied",
                                result: okPayload);
                        } catch (GxMcp.Worker.Parsers.StructureRemovalException remEx) {
                            // issue #36.1 — mode:full / remove_attribute is authoritative. When the
                            // SDK refuses to drop an attribute, abort the whole write (nothing is
                            // saved — EnsureSave never ran) and return a hard error instead of a
                            // silent additive merge reported as success.
                            Logger.Error("[DEBUG-SAVE] Structure removal failed for " + target + ": " + remEx.Message);
                            // B9: the cause is not always "key in use". When the SDK build
                            // exposes no removal API at all (SDK-API-UNSUPPORTED marker from
                            // TransactionDslParser), blaming an FK/relation sends the agent
                            // chasing a dependency that doesn't exist. Branch the hint.
                            bool apiUnsupported = remEx.Message?.IndexOf("SDK-API-UNSUPPORTED", StringComparison.OrdinalIgnoreCase) >= 0;
                            return Models.McpResponse.Err(
                                code: apiUnsupported ? "StructureAttributeRemovalUnsupported" : "StructureAttributeNotRemoved",
                                message: remEx.Message + ". The write was aborted; nothing was persisted.",
                                hint: apiUnsupported
                                    ? "Removing an attribute from a Transaction is not writable through the GeneXus SDK in this build — it is IDE-only. The MCP will not silently keep the attribute. Remove the attribute from the KB Explorer / Transaction Structure editor in the GeneXus IDE. (Renaming and adding attributes ARE supported via the MCP.)"
                                    : "mode:full and remove_attribute are authoritative — they will not silently keep an attribute. The SDK refused to drop the attribute(s) above, usually because a key is still referenced by a foreign key, relation, or index elsewhere. Remove those usages first (or rename the attribute instead of replacing it), then retry.",
                                nextSteps: new JArray(McpResponse.NextStep(
                                    tool: "genexus_read",
                                    args: new JObject { ["name"] = target, ["part"] = "Structure" },
                                    why: "Returns the current Structure so you can see which attributes remain and plan the removal.")),
                                target: target);
                        } catch (Exception ex) {
                            Logger.Error("[DEBUG-SAVE] Error parsing Structure DSL: " + ex.Message);
                            return Models.McpResponse.Err(
                                code: "StructureSyntaxInvalid",
                                message: $"Invalid Structure Syntax: {ex.Message}",
                                hint: "Check the DSL for typos in attribute names or type identifiers. Re-read the object to see its current Structure before editing.",
                                nextSteps: new JArray(McpResponse.NextStep(
                                    tool: "genexus_read",
                                    args: new JObject { ["name"] = target, ["part"] = "Structure" },
                                    why: "Returns the current Structure DSL so you can diff against your attempted change.")),
                                target: target);
                        }
                    }
                }

                // issue #26 (Humberto DSO case): a Design System object keeps tokens and
                // styles in two separate ISource parts. When the caller writes the generic
                // "Source"/"Code" alias with a combined `tokens {…} styles {…}` blob, split it
                // and write the styles block to the Styles part here (side-effect), then
                // redirect the main write below to the Tokens block. A single obj.Save()
                // persists both dirtied parts. Without this the whole blob landed in Tokens
                // and Styles stayed empty. Non-DSO writes and explicit Tokens/Styles targets
                // are unaffected.
                if (GxMcp.Worker.Structure.PartAccessor.IsDesignSystem(obj)
                    && (partName.Equals("Source", StringComparison.OrdinalIgnoreCase)
                        || partName.Equals("Code", StringComparison.OrdinalIgnoreCase))
                    && GxMcp.Worker.Helpers.DesignSystemSourceSplitter.TrySplit(decodedCode, out string dsTokens, out string dsStyles))
                {
                    // A Design System keeps tokens and styles in two separate ISource parts.
                    // The generic "Source"/"Code" write carries one combined blob; split it and
                    // route each block to its own part. The main write path below handles ONE
                    // part (with the no-change guard + snapshot + save); the other block, when it
                    // has changed, is applied here as a side-effect and rides the single obj.Save().
                    //
                    // Critical: the main target must be a block that ACTUALLY changed, and the
                    // side-effect must only touch a part when it changed. Redirecting the main
                    // write to an unchanged block makes the no-change guard return WriteNoChange
                    // WITHOUT calling Save(), which silently drops the changed sibling written as
                    // a side-effect. GetPart("Tokens"/"Styles") resolves to the same instances
                    // used here, so comparisons/sets are on the real parts.
                    var tokensSrcPart = GxMcp.Worker.Structure.PartAccessor.GetDesignSystemPart(obj, styles: false) as global::Artech.Architecture.Common.Objects.ISource;
                    var stylesSrcPart = GxMcp.Worker.Structure.PartAccessor.GetDesignSystemPart(obj, styles: true) as global::Artech.Architecture.Common.Objects.ISource;

                    bool tokensChanged = dsTokens != null && tokensSrcPart != null
                        && !WritePolicy.IsUnchangedSourceWrite(tokensSrcPart.Source, dsTokens);
                    bool stylesChanged = dsStyles != null && stylesSrcPart != null
                        && !WritePolicy.IsUnchangedSourceWrite(stylesSrcPart.Source, dsStyles);

                    if (tokensChanged)
                    {
                        // Tokens is the main write. If styles also changed, apply it as a
                        // side-effect so both persist on the single save.
                        if (stylesChanged)
                        {
                            stylesSrcPart.Source = dsStyles;
                            Logger.Info("[DSO-SPLIT] Wrote styles block to Styles part as side-effect (" + dsStyles.Length + " chars).");
                        }
                        partName = "Tokens";
                        decodedCode = dsTokens;
                    }
                    else if (stylesChanged)
                    {
                        // Only styles changed — make it the main write directly; no side-effect,
                        // so the no-change guard compares the real (unwritten) Styles part.
                        partName = "Styles";
                        decodedCode = dsStyles;
                    }
                    else if (dsTokens != null)
                    {
                        // Neither block changed (or parts unresolved) — fall through with Tokens
                        // as the target so the guard returns a legitimate WriteNoChange.
                        partName = "Tokens";
                        decodedCode = dsTokens;
                    }
                    else if (dsStyles != null)
                    {
                        partName = "Styles";
                        decodedCode = dsStyles;
                    }
                }

                global::Artech.Architecture.Common.Objects.KBObjectPart part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);

                if (part == null) {
                    Logger.Error("[DEBUG-SAVE] Part NOT FOUND in object: " + partName);
                    return CreateWriteError(
                        $"Part '{partName}' not found in {obj.TypeDescriptor.Name}",
                        target,
                        partName,
                        "The object does not expose the requested part.",
                        obj
                    );
                }

                // Issue #24 — skip the no-change short-circuit when a prior write to this
                // part persisted empty. The in-memory Source still holds the content the
                // SDK dropped, so it would falsely compare equal to the incoming code and
                // lock the caller into WriteNoChange forever. Re-attempt the save instead.
                if (part is global::Artech.Architecture.Common.Objects.ISource existingSourcePart &&
                    !IsEmptyPersistPending(target, partName) &&
                    WritePolicy.IsUnchangedSourceWrite(existingSourcePart.Source, decodedCode))
                {
                    Logger.Info("[DEBUG-SAVE] Content is identical. Skipping validation and Save.");
                    return Models.McpResponse.Ok(
                        target: target,
                        code: "WriteNoChange",
                        result: new JObject { ["details"] = "No change" });
                }

                // Nirvana v19.4: Auto-Healing (Pre-save validation)
                if (autoValidate && _validationService != null && !partName.Equals("Variables", StringComparison.OrdinalIgnoreCase) && !partName.Equals("Structure", StringComparison.OrdinalIgnoreCase))
                {
                    string validationRes = _validationService.ValidateCode(target, partName, decodedCode);
                    var valJson = JObject.Parse(validationRes);
                    if (valJson["status"]?.ToString() == "Error")
                    {
                        string firstError = valJson["errors"]?[0]?["description"]?.ToString()
                            ?? valJson["error"]?.ToString()
                            ?? "Validation failed.";

                        // If the SDK only returned a generic "Erro" with no diagnostics, the pre-flight
                        // is uninformative. Fall through to the real save so the EnsureSave path can
                        // throw with the full SDK message ("src0059: …", etc).
                        bool isUninformative = string.Equals(firstError?.Trim(), "Erro", StringComparison.OrdinalIgnoreCase);
                        if (isUninformative)
                        {
                            Logger.Warn($"[AUTO-HEALING] Pre-flight returned generic 'Erro' for {target} ({partName}); proceeding to real save for detailed diagnostics.");
                        }
                        else
                        {
                            Logger.Warn($"[AUTO-HEALING] Blocked invalid code for {target} ({partName}): {firstError}");
                            return validationRes; // Return the error immediately to the LLM
                        }
                    }
                }

                // 1. SET CONTENT
                bool contentSet = false;
                if (part is global::Artech.Genexus.Common.Parts.VariablesPart varPart)
                {
                    VariableInjector.SetVariablesFromText(varPart, decodedCode);
                    contentSet = true;
                }
                else if (part is global::Artech.Architecture.Common.Objects.ISource sourcePart)
                {
                    sourcePart.Source = decodedCode;
                    
                    // Auto-inject variables based on the new code (Optimized with Index)
                    if (autoInjectVariables)
                    {
                        try {
                            var index = _objectService.GetKbService().GetIndexCache().GetIndex();
                            VariableInjector.InjectVariables(obj, decodedCode, index);
                        } catch (Exception ex) {
                            Logger.Warn("[DEBUG-SAVE] Auto-inject variables failed: " + ex.Message);
                        }
                    }
                    contentSet = true;
                }
                else if (TrySetDocumentationContent(part, decodedCode, out string docDiagnostic))
                {
                    Logger.Info("[DEBUG-SAVE] Documentation content set via " + docDiagnostic);
                    contentSet = true;
                }
                else
                {
                    try {
                        if (decodedCode.Trim().StartsWith("<") && !partName.Equals("Structure", StringComparison.OrdinalIgnoreCase)) {
                            part.DeserializeFromXml(decodedCode);
                            contentSet = true;
                        } else {
                            var contentProp = part.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.Instance)
                                           ?? part.GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
                            if (contentProp != null && contentProp.CanWrite) {
                                contentProp.SetValue(part, decodedCode);
                                contentSet = true;
                            }
                        }
                    } catch (Exception ex) {
                        // Surface the underlying error so the agent gets actionable feedback
                        // instead of a generic "could not set content" downstream.
                        Logger.Warn($"[DEBUG-SAVE] Content set failed for {target} ({partName}): {ex.Message}");
                    }
                }

                if (!contentSet) {
                    Logger.Warn("[DEBUG-SAVE] No suitable method found to update part content.");
                }

                // 2. FORCE DIRTY (Crucial)
                try {
                    // Mark Part as Dirty
                    var pType = part.GetType();
                    var pDirtyProp = pType.GetProperty("Dirty", BindingFlags.Public | BindingFlags.Instance) 
                                  ?? pType.GetProperty("IsDirty", BindingFlags.Public | BindingFlags.Instance);
                    if (pDirtyProp != null) {
                        pDirtyProp.SetValue(part, true);
                        Logger.Debug("[DEBUG-SAVE] Part property '" + pDirtyProp.Name + "' set to TRUE");
                    }

                    // Mark Header Object as Dirty (Essential for Save)
                    var oType = obj.GetType();
                    var oDirtyProp = oType.GetProperty("Dirty", BindingFlags.Public | BindingFlags.Instance)
                                  ?? oType.GetProperty("IsDirty", BindingFlags.Public | BindingFlags.Instance);
                    if (oDirtyProp != null) {
                        oDirtyProp.SetValue(obj, true);
                        Logger.Debug("[DEBUG-SAVE] Object property '" + oDirtyProp.Name + "' set to TRUE");
                    }
                } catch (Exception ex) { Logger.Debug("[DEBUG-SAVE] Force Dirty failed: " + ex.Message); }

                // 3. PERSISTENCE SEQUENCE
                try
                {
                    EnsurePersistenceWarmup();

                    if (preferFastSourceSave &&
                        part is global::Artech.Architecture.Common.Objects.ISource &&
                        WritePolicy.IsLogicalSourcePart(partName))
                    {
                        try
                        {
                            var saveMethod = obj.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (saveMethod != null)
                            {
                                Logger.Info("[DEBUG-SAVE] Fast persistence path: obj.Save() without explicit transaction.");
                                saveMethod.Invoke(obj, null);
                                ScheduleFlush();
                                _objectService.MarkReadCacheDirty(obj, partName);
                                {
                                    var fpResult = new JObject { ["fastPath"] = "save_without_transaction" };
                                    string fpMsgs = GetSdkMessagesSafe(part);
                                    if (!string.IsNullOrWhiteSpace(fpMsgs)) fpResult["sdkMessages"] = fpMsgs;
                                    return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: fpResult);
                                }
                            }

                            Logger.Info("[DEBUG-SAVE] Fast persistence path fallback: obj.EnsureSave(false) without explicit transaction.");
                            obj.EnsureSave(false);
                            ScheduleFlush();
                            _objectService.MarkReadCacheDirty(obj, partName);
                            {
                                var fpResult = new JObject { ["fastPath"] = "ensure_save_without_transaction" };
                                string fpMsgs = GetSdkMessagesSafe(part);
                                if (!string.IsNullOrWhiteSpace(fpMsgs)) fpResult["sdkMessages"] = fpMsgs;
                                return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: fpResult);
                            }
                        }
                        catch (Exception fastEx)
                        {
                            Logger.Warn("[DEBUG-SAVE] Fast persistence path failed. Falling back to full transaction path. Reason: " + fastEx.Message);
                        }
                    }

                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) throw new Exception("KB not opened");

                    // 1. Start Transaction
                    Logger.Info("[DEBUG-SAVE] Starting SDK Transaction...");
                    var transaction = kb.BeginTransaction();
                    string failureStage = "transaction";
                    string retryStrategy = "standard";
                    string lastSdkMessages = string.Empty;
                    bool transactionCommitted = false;
                    bool transactionFinished = false;
                    string finalizeError = null;
                    // Block KbWatcher from polling DesignModel.Objects while we're inside the tx.
                    var writeGate = KbWatcherService.AcquireWriteGate();

                    try {
                        // 2. Checkout
                        try {
                            var checkoutMethod = obj.GetType().GetMethod("Checkout", BindingFlags.Public | BindingFlags.Instance);
                            checkoutMethod?.Invoke(obj, null);
                            Logger.Debug("[DEBUG-SAVE] SDK Checkout invoked.");
                        } catch (Exception coEx) {
                            // Checkout failure is usually benign (object not under VC), but
                            // a hard error here can cascade into "object not editable" later.
                            Logger.Debug("[DEBUG-SAVE] SDK Checkout skipped: " + coEx.Message);
                        }

                        // 3. Save Part (CRITICAL: Save the part explicitly first)
                        failureStage = "part_save";
                        Logger.Info(string.Format("[DEBUG-SAVE] Invoking part.Save() for {0}...", part.TypeDescriptor?.Name));
                        bool skippedPartSave = false;
                        if (preferFastSourceSave &&
                            part is global::Artech.Architecture.Common.Objects.ISource &&
                            WritePolicy.IsLogicalSourcePart(partName))
                        {
                            skippedPartSave = true;
                            retryStrategy = "object_save_only_fast_path";
                            Logger.Info($"[DEBUG-SAVE] Fast source save path enabled for {target} ({partName}). Skipping part.Save().");
                        }
                        else
                        {
                            try {
                                part.Save();
                                Logger.Info("[DEBUG-SAVE] part.Save() completed.");
                            } catch (Exception exPart) {
                                string partMsgs = GetSdkMessagesSafe(part);
                                lastSdkMessages = partMsgs;
                                var saveIssues = SdkDiagnosticsHelper.GetDiagnostics(obj);
                                if (ShouldRetryWithoutPartSave(partName, part, exPart, partMsgs, saveIssues))
                                {
                                    skippedPartSave = true;
                                    retryStrategy = "object_save_only";
                                    Logger.Warn($"[DEBUG-SAVE] part.Save() failed generically for {target} ({partName}). Retrying with object-level save only.");
                                }
                                else
                                {
                                    string detailText = WritePolicy.BuildFailureDetails(partMsgs, saveIssues);
                                    Logger.Warn($"[DEBUG-SAVE] part.Save() threw exception: {exPart.Message}. Details: {detailText}");
                                    throw new Exception(
                                        string.IsNullOrWhiteSpace(detailText)
                                            ? $"Part save failed: {exPart.Message}"
                                            : $"Part save failed: {exPart.Message}. Details: {detailText}",
                                        exPart);
                                }
                            }
                        }

                        // Check for messages even if it didn't throw (some SDK errors are non-throwing)
                        string checkMsgs = GetSdkMessagesSafe(part);
                        lastSdkMessages = checkMsgs;
                        if (!skippedPartSave && !string.IsNullOrEmpty(checkMsgs) && (checkMsgs.Contains("Erro") || checkMsgs.Contains("Error"))) {
                            Logger.Warn($"[DEBUG-SAVE] part.Save() reported internal errors: {checkMsgs}");
                            throw new Exception($"Part save reported errors: {checkMsgs}");
                        }

                        // 4. Save Object (Unified approach)
                        try 
                        {
                            failureStage = "object_save";
                            Logger.Info("[DEBUG-SAVE] Invoking obj.EnsureSave(check: true)...");
                            obj.EnsureSave(true);
                            Logger.Info("[DEBUG-SAVE] obj.EnsureSave(true) completed.");
                        }
                        catch (Exception ex) when (ex.Message.Contains("Validation failed") || ex.Message.Contains("Save failed"))
                        {
                            Logger.Warn($"[DEBUG-SAVE] Standard save failed: {ex.Message}. Retrying with check=false...");
                            // RETRY WITHOUT VALIDATION (User request)
                            retryStrategy = retryStrategy == "standard" ? "ensure_save_without_validation" : $"{retryStrategy}+ensure_save_without_validation";
                            obj.EnsureSave(false);
                            Logger.Info("[DEBUG-SAVE] obj.EnsureSave(false) completed successfully.");
                        }
                        
                        // 5. Transaction Commit
                        failureStage = "commit";
                        Logger.Info("[DEBUG-SAVE] Committing SDK Transaction...");
                        transaction.Commit();
                        transactionCommitted = true;
                        transactionFinished = true;
                        Logger.Info("[DEBUG-SAVE] SDK Transaction Committed.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("[DEBUG-SAVE] SDK TRANSACTION ERROR: " + ex.ToString());
                        var issues = SdkDiagnosticsHelper.GetDiagnostics(obj);
                        // After a Commit-stage failure, the transaction may already be in a
                        // half-finalized state where Rollback throws. Guard it so we still
                        // return the structured error JSON instead of crashing the worker.
                        if (!transactionCommitted)
                        {
                            try { transaction.Rollback(); }
                            catch (Exception rbEx)
                            {
                                Logger.Warn("[DEBUG-SAVE] Rollback after error also failed: " + rbEx.Message);
                                finalizeError = rbEx.Message;
                            }
                        }
                        transactionFinished = true;
                        lastSdkMessages = string.IsNullOrWhiteSpace(lastSdkMessages) ? GetSdkMessagesSafe(part) : lastSdkMessages;
                        return CreateTransactionErrorResponse(target, partName, failureStage, ex, issues, retryStrategy, lastSdkMessages, obj, decodedCode).ToString();
                    }
                    finally
                    {
                        // Defense in depth: ensure the transaction is never left undisposed,
                        // even if Commit/Rollback escaped via a path we didn't anticipate.
                        if (!transactionFinished && !transactionCommitted)
                        {
                            try { transaction.Rollback(); } catch { }
                        }
                        try { (transaction as IDisposable)?.Dispose(); } catch { }
                        try { writeGate.Dispose(); } catch { }
                        if (finalizeError != null)
                        {
                            Logger.Debug("[DEBUG-SAVE] Transaction finalize note: " + finalizeError);
                        }
                    }

                    // FAST SAVE: Run heavy indexing in background. UpdateEntry reads
                    // live SDK/COM object state (obj.Guid/Name/TypeDescriptor, attribute
                    // types, transaction structure, parts), so it MUST run on the STA
                    // background queue inside SdkGate — not a raw Task.Run pool thread,
                    // which would race concurrent SDK activity (see SdkGate.cs invariant
                    // and the matching pattern in IndexCacheService.UpdateEntry's enrich step).
                    var objToIndex = obj;
                    Program.EnqueueBackground(() => {
                        try {
                            using (SdkGate.Enter())
                            {
                                _objectService.GetKbService().GetIndexCache().UpdateEntry(objToIndex);
                            }
                        } catch (Exception ex) { Logger.Error("[DEBUG-SAVE] Background Index update failed: " + ex.Message); }
                    });
                    
                    // Final persistence with debounce to avoid sync commit on every write.
                    ScheduleFlush();

                    Logger.Info("[DEBUG-SAVE] SAVE & COMMIT COMPLETE.");
                    _objectService.MarkReadCacheDirty(obj, partName);

                    // Item 7 (friction-report 2026-05-22): spc0150 preflight — attribute writes
                    // inside For each/endfor in a WebPanel Events part compile clean in the MCP
                    // write but fail at KB build time with "Attribute cannot be assigned in this
                    // context". Warn immediately so the agent can restructure before building.
                    try
                    {
                        if (string.Equals(partName, "Events", StringComparison.OrdinalIgnoreCase)
                            && obj != null
                            && string.Equals(obj.TypeDescriptor?.Name, "WebPanel", StringComparison.OrdinalIgnoreCase))
                        {
                            var spcFindings = GxMcp.Worker.Helpers.Spc0150PreflightScanner.Scan(decodedCode);
                            if (spcFindings != null && spcFindings.Count > 0)
                            {
                                var warnPayload = new JObject
                                {
                                    ["warnings"] = new JArray
                                    {
                                        new JObject
                                        {
                                            // Friction 2026-05-22 #62: was "PreflightSpc0150"; renamed to the
                                            // canonical Lint* form so the audit-test enumerator picks it up.
                                            ["code"] = GotchaCodes.LintSpc0150ForEachAttributeWrite,
                                            ["docUrl"] = GotchaCodes.DocUrlFor(GotchaCodes.LintSpc0150ForEachAttributeWrite),
                                            ["message"] = "WebPanel Events with attribute writes inside `For each` → spc0150 at build time. Move to a Procedure (recipe extract_to_procedure).",
                                            ["suggested_recipe"] = "extract_to_procedure"
                                        }
                                    }
                                };
                                return Models.McpResponse.Ok(
                                    target: target,
                                    code: "WriteApplied",
                                    result: warnPayload);
                            }
                        }
                    }
                    catch (Exception spcEx)
                    {
                        Logger.Debug("[SPC0150] preflight scan skipped: " + spcEx.Message);
                    }

                    // Build success result — include retryStrategy and warnings when validation was bypassed
                    var writeResult = new JObject();
                    var writeWarnings = new JArray();
                    if (explicitBase64 || usedBase64Sniff)
                        writeResult["decodedBase64"] = true;
                    if (!string.Equals(retryStrategy, "standard", StringComparison.Ordinal))
                    {
                        writeResult["retryStrategy"] = retryStrategy;
                        if (retryStrategy.Contains("ensure_save_without_validation"))
                            writeWarnings.Add("validation was bypassed on save; run a build to verify");
                        if (retryStrategy.Contains("object_save_only"))
                            writeWarnings.Add("part-level save was skipped; only object-level save succeeded");
                    }
                    if (writeWarnings.Count > 0)
                        writeResult["warnings"] = writeWarnings;
                    return Models.McpResponse.Ok(
                        target: target,
                        code: "WriteApplied",
                        result: writeResult.Count > 0 ? writeResult : null);
                }
                catch (Exception saveEx)
                {
                    Logger.Error("[DEBUG-SAVE] CRITICAL SDK EXCEPTION: " + saveEx.ToString());
                    return BuildEnrichedSaveError("SDK Save failed", saveEx, obj, target, partName).ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[DEBUG-SAVE] OUTER EXCEPTION: " + ex.ToString());
                // obj may not have been bound yet (exception before FindObject).
                return BuildEnrichedSaveError(null, ex, null, target, partName).ToString();
            }
        }

        // Friction-report #2: when the bare-error catches fire (outside the transaction's own
        // catch), still consult SdkDiagnosticsHelper + GetSdkMessages so an "Erro" exception
        // doesn't escape to the agent unenriched.
        private static JObject BuildEnrichedSaveError(string prefix, Exception ex, global::Artech.Architecture.Common.Objects.KBObject obj, string target, string partName)
        {
            JArray issues = null;
            string sdkMsgs = null;
            if (obj != null)
            {
                try { issues = SdkDiagnosticsHelper.GetDiagnostics(obj); } catch { }
                try { sdkMsgs = obj.GetSdkMessages(); } catch { }
            }
            string baseMessage = ex?.Message ?? string.Empty;
            string enriched = WritePolicy.PreferDetailedMessage(baseMessage, sdkMsgs, issues);
            string display = string.IsNullOrEmpty(prefix) ? enriched : prefix + ": " + enriched;

            var extra = new JObject();
            if (!string.IsNullOrWhiteSpace(partName)) extra["part"] = partName;
            if (!string.IsNullOrWhiteSpace(sdkMsgs)) extra["sdkMessages"] = sdkMsgs;
            if (issues != null && issues.Count > 0) extra["issues"] = issues;
            if (!string.Equals(enriched, baseMessage.Trim(), System.StringComparison.Ordinal))
                extra["originalError"] = baseMessage;

            var nextSteps = new JArray(
                McpResponse.NextStep(
                    tool: "genexus_build",
                    args: target != null ? new JObject { ["target"] = target } : null,
                    why: "Building surfaces full SDK diagnostics for the failing object."));

            string errJson = McpResponse.Err(
                code: "SdkSaveFailed",
                message: display,
                hint: "Check sdkMessages and issues for the root SDK error; fix and retry.",
                nextSteps: nextSteps,
                target: target,
                extra: extra);
            return JObject.Parse(errJson);
        }

        private string CreateWriteError(
            string error,
            string target,
            string partName,
            string details,
            global::Artech.Architecture.Common.Objects.KBObject obj = null,
            GxMcp.Worker.Helpers.XmlEquivalenceDiff structuredDiff = null,
            string code = null)
        {
            // Build verifyDiff for extra block when a structured diff is available.
            JObject verifyDiffObj = null;
            if (structuredDiff != null)
            {
                verifyDiffObj = new JObject();
                if (!string.IsNullOrEmpty(structuredDiff.ElementName)) verifyDiffObj["element"] = structuredDiff.ElementName;
                if (!string.IsNullOrEmpty(structuredDiff.Path)) verifyDiffObj["path"] = structuredDiff.Path;
                if (structuredDiff.RejectedAttributes != null && structuredDiff.RejectedAttributes.Length > 0)
                    verifyDiffObj["rejectedAttributes"] = new JArray(structuredDiff.RejectedAttributes);
                if (structuredDiff.AddedAttributes != null && structuredDiff.AddedAttributes.Length > 0)
                    verifyDiffObj["addedAttributes"] = new JArray(structuredDiff.AddedAttributes);
                if (structuredDiff.LeftAttributes != null) verifyDiffObj["persistedAttributes"] = new JArray(structuredDiff.LeftAttributes);
                if (structuredDiff.RightAttributes != null) verifyDiffObj["requestedAttributes"] = new JArray(structuredDiff.RightAttributes);
            }

            // Build hint from suggestion and nextSteps from availableParts.
            string hint = BuildSuggestion(error, partName);

            // C17: enrich Events-part SDK failures (src0208 auto-stub collision;
            // src0233/src0216 control-not-yet-projected) with the actionable cause.
            string eventHint = WritePolicy.BuildEventDiagnosticHint(details) ?? WritePolicy.BuildEventDiagnosticHint(error);
            if (eventHint != null)
                hint = string.IsNullOrEmpty(hint) ? eventHint : hint + " " + eventHint;

            JArray nextSteps = null;
            JArray availablePartsArr = null;
            if (obj != null)
            {
                var apArr = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                if (apArr.Length > 0)
                {
                    availablePartsArr = new JArray(apArr);
                    if (hint == null)
                        hint = "This object exposes parts: " + string.Join(", ", apArr) + ". Pass one of these as 'part'.";
                    nextSteps = new JArray(
                        McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = obj.Name },
                            why: "Returns availableParts so the next write picks a valid part name."));
                }
            }
            if (nextSteps == null)
            {
                nextSteps = new JArray(
                    McpResponse.NextStep(
                        tool: "genexus_read",
                        args: target != null ? new JObject { ["name"] = target } : null,
                        why: "Inspect the object to determine the correct part or type."));
            }

            var extra = new JObject();
            if (!string.IsNullOrWhiteSpace(partName)) extra["part"] = partName;
            if (!string.IsNullOrWhiteSpace(details)) extra["details"] = details;
            if (obj != null)
            {
                extra["objectName"] = obj.Name;
                extra["objectType"] = obj.TypeDescriptor?.Name;
            }
            if (availablePartsArr != null) extra["availableParts"] = availablePartsArr;
            if (verifyDiffObj != null) extra["verifyDiff"] = verifyDiffObj;

            return McpResponse.Err(
                code: code ?? "WriteFailed",
                message: error,
                hint: hint,
                nextSteps: nextSteps,
                target: target,
                extra: extra);
        }

        private static string BuildSuggestion(string error, string partName)
        {
            if (string.IsNullOrEmpty(error)) return null;
            if (error.IndexOf("Part not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Try mode='full', or read the object first to see availableParts.";
            if (error.IndexOf("does not expose text source", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Resolve via parent Transaction (e.g., type='Transaction') or use mode='full'.";
            if (error.IndexOf("verification failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return "See 'verifyDiff' for rejected/added attrs. SDK sanitises attrs outside its element schema (e.g. 'style' on classref-bound elements). Drop those attrs or move them to a Theme class.";
            if (error.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Check XML well-formedness; verify single root element and quoted attribute values.";
            if (error.IndexOf("Object not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Disambiguate with type=<Transaction|Procedure|...> or list_objects to confirm the name.";
            return null;
        }



    }
}
