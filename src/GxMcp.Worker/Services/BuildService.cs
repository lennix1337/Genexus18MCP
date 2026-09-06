using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security;

namespace GxMcp.Worker.Services
{
    public class BuildService : IBuildServiceFacade
    {
        private string _msbuildPath;
        private string _gxDir;
        private KbService _kbService;
        private IndexCacheService _indexCacheService;
        private CallerGraphService _callerGraphService;
        private static readonly ConcurrentDictionary<string, BuildTaskStatus> _tasks = new ConcurrentDictionary<string, BuildTaskStatus>();
        // The generator's User Control factory call together with its (non-nested)
        // argument list: gx.uc.getNew(this,6,0,"DashboardViewer","DV1Container","Dashboardviewer1","DV1").
        private static readonly Regex _userControlFactoryRegex = new Regex(
            @"\bgx\.uc\.getNew\s*\(([^()]*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Any runtime property binding emitted for a User Control instance. A healthy
        // instance always carries at least Class/Visible; a degraded one carries none.
        private static readonly Regex _userControlPropertyBindingRegex = new Regex(
            @"\.setProp\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // issue #42 — builds whose RunBuild is genuinely executing right now, keyed by
        // taskId. Add on entry, remove in finally. This is the source of truth for
        // "is a build in flight" (P2b activeBuilds + P3c concurrent-build reject) rather
        // than scanning _tasks for a non-terminal Status label — an orphaned/crashed
        // "Running" label (thread died without terminalizing) must NOT wedge every future
        // build, and status labels registered out-of-band (tests) must not count as live.
        private static readonly ConcurrentDictionary<string, BuildTaskStatus> _inFlightBuilds = new ConcurrentDictionary<string, BuildTaskStatus>();

        private static readonly System.Collections.Generic.Dictionary<string, int> _phaseProgressMap =
            new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "Starting",   5  },
                { "OpeningKB",  10 },
                { "Specifying", 15 },
                { "Generating", 35 },
                { "Compiling",  50 },
                { "Finishing",  85 },
                { "Linking",    85 },
                { "Done",       100 },
                { "Completed",  100 }
            };

        private static void EmitPhaseProgress(string phase, int total = 100)
        {
            int p;
            if (_phaseProgressMap.TryGetValue(phase, out p))
            {
                GxMcp.Worker.Helpers.ProgressEmitter.Emit(p, total, "Build phase: " + phase);
            }
        }

        // Friction 2026-05-22: MSBuild /m spawns N worker nodes per build; killing
        // only the parent (Process.Kill on net48) orphans the children as zombies.
        // We walk the tree via WMI's Win32_Process.ParentProcessId, kill leaves first,
        // then the parent. Tolerant of races (process already exited).
        internal static int KillProcessTree(System.Diagnostics.Process root)
        {
            if (root == null) return 0;
            int killed = 0;
            try
            {
                int rootPid;
                try { rootPid = root.Id; } catch { return 0; }
                // R10: WMI ParentProcessId is creation-time-only; if rootPid was
                // recycled, descendants of the OLD process get picked up. Capture
                // the root's start time and filter children born before it.
                DateTime rootStart;
                try { rootStart = root.StartTime.ToUniversalTime(); }
                catch { rootStart = DateTime.MinValue; }
                var descendants = CollectDescendants(rootPid, rootStart);
                // Kill leaves first so we don't reparent children to init mid-walk.
                foreach (var pid in descendants)
                {
                    try
                    {
                        using (var child = System.Diagnostics.Process.GetProcessById(pid))
                        {
                            if (!child.HasExited) { child.Kill(); killed++; }
                        }
                    }
                    catch { /* gone already */ }
                }
                try { if (!root.HasExited) { root.Kill(); killed++; } }
                catch { }
            }
            catch (Exception ex)
            {
                Logger.Warn("[KILL-TREE] root pid=" + (root?.Id) + ": " + ex.Message);
            }
            if (killed > 0)
                Logger.Info("[KILL-TREE] killed " + killed + " process(es) under root pid=" + (root?.Id));
            return killed;
        }

        // Plan 012: reap a spawned MSBuild process by PID from a context where the
        // original Process object has already been disposed (e.g. the RunBuild
        // finally block, after the enclosing using-block has run Dispose()).
        // Guards against PID reuse via a start-time match when one was captured.
        internal void ReapByPidIfAlive(int pid, DateTime expectedStart)
        {
            if (pid <= 0) return;
            System.Diagnostics.Process p = null;
            try { p = System.Diagnostics.Process.GetProcessById(pid); }
            catch { return; } // ArgumentException => not running; nothing to reap
            using (p)
            {
                try
                {
                    if (p.HasExited) return;
                    // Guard against PID reuse: only kill if the start time matches
                    // (skip the guard when we failed to capture it).
                    if (expectedStart != DateTime.MinValue)
                    {
                        try { if (p.StartTime != expectedStart) return; } catch { }
                    }
                    Logger.Info("[BUILD-CLEANUP] reaping lingering MSBuild tree pid=" + pid);
                    KillProcessTree(p);
                }
                catch { /* best-effort */ }
            }
        }

        private static List<int> CollectDescendants(int rootPid, DateTime rootStartUtc)
        {
            var result = new List<int>();
            // Build full parent → children map via WMI in one query (cheap).
            var byParent = new Dictionary<int, List<int>>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process"))
                using (var results = searcher.Get())
                {
                    foreach (System.Management.ManagementObject mo in results)
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            int parent = Convert.ToInt32(mo["ParentProcessId"]);
                            // R10: skip children that were born before the root —
                            // they're PID-reuse artifacts. CIM datetime string like
                            // "20260522141234.123456-180"; on parse failure default
                            // to INCLUDE (conservative: better to over-kill our own
                            // descendants than miss them).
                            bool include = true;
                            if (rootStartUtc > DateTime.MinValue)
                            {
                                try
                                {
                                    var cd = mo["CreationDate"] as string;
                                    if (!string.IsNullOrEmpty(cd))
                                    {
                                        var childStart = System.Management.ManagementDateTimeConverter
                                            .ToDateTime(cd).ToUniversalTime();
                                        if (childStart < rootStartUtc) include = false;
                                    }
                                }
                                catch { /* parse failure → include */ }
                            }
                            if (!include) continue;
                            if (!byParent.TryGetValue(parent, out var list))
                            {
                                list = new List<int>();
                                byParent[parent] = list;
                            }
                            list.Add(pid);
                        }
                        catch { }
                        finally { mo?.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[KILL-TREE] WMI scan failed: " + ex.Message);
                return result;
            }

            // BFS from rootPid; collect children, grandchildren, ...
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            var seen = new HashSet<int> { rootPid };
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (byParent.TryGetValue(cur, out var kids))
                {
                    foreach (var k in kids)
                    {
                        if (seen.Add(k))
                        {
                            result.Add(k);
                            queue.Enqueue(k);
                        }
                    }
                }
            }
            // Reverse so leaves come first
            result.Reverse();
            return result;
        }

        private const int TailBufferSize = 30;

        // Regex parsers for MSBuild/GeneXus output
        private static readonly Regex _rxSpecifying = new Regex(@"^\s*Specifying\s+(?<obj>.+?)\s*\.\.\.\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // "Generating ObjectName ..." — exclude "Generating to <path>" (those are output-file lines, not objects)
        private static readonly Regex _rxGenerating = new Regex(@"^\s*Generating\s+(?!to\b)(?<obj>.+?)(?:\s*\.\.\.)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxCompiling  = new Regex(@"^\s*Compil(ation|ing)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // GeneXus spec/gen errors are 'error spc####:', C# compiler errors are 'error CS####:',
        // and MSBuild surfaces some lines as 'error : <message>' (no code). Capture all three forms.
        private static readonly Regex _rxError      = new Regex(@"\berror\s*(:|[A-Z]{2,4}\d+\s*:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxWarning    = new Regex(@"\bwarning\s*(:|[A-Z]{2,4}\d+\s*:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // issue #32 item 5: the GeneXus specifier emits "Object 'X' was not found in the
        // Knowledge Base" (EN) / "Objeto 'X' não foi encontrado na Knowledge Base" (PT-BR)
        // as a warning during a single-object spec pass on a freshly created/edited object,
        // even when the spec Succeeds with 0 errors. It's a spurious signal (the object plainly
        // exists — it's the one being specified) that reads as "the object vanished". Suppress
        // it when the named object is one of the build targets. Captures the quoted object name.
        private static readonly Regex _rxObjectNotFoundWarning = new Regex(
            @"(?:Object|Objeto)\s+['""]?(?<obj>[A-Za-z_][A-Za-z0-9_.]*)['""]?\s+(?:was not found|(?:não|nao)\s+foi\s+encontrad[oa])\s+.*Knowledge\s*Base",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxOpenKb     = new Regex(@"^\s*Opening Knowledge Base\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // In-process builds don't emit MSBuild.exe's "Specifying X..."/"Compiling" text —
        // the GeneXus build engine forwards the SDK section-marker protocol instead:
        //   >S<section>[:-:<label>]  section opens
        //   >E1<section>             section closed OK
        //   >E0<section>             section closed FAIL
        // Parsing these here gives the in-process path the same phase progress and named
        // failure surface the external path already produces from text.
        private static readonly Regex _rxSectionStart = new Regex(
            @"^>S(?<name>[A-Za-z][A-Za-z0-9 _]*?)(?:[: ]|$)", RegexOptions.Compiled);
        private static readonly Regex _rxSectionFail = new Regex(
            @"^>E0(?<name>[A-Za-z][A-Za-z0-9 _]*?)(?:[: ]|$)", RegexOptions.Compiled);

        // Map a GeneXus build section name to a lifecycle phase. Unknown sections
        // (and the outer "Build" wrapper) return null so we don't churn the phase.
        internal static string MapSectionToPhase(string section)
        {
            if (string.IsNullOrWhiteSpace(section)) return null;
            if (section.IndexOf("Specif", StringComparison.OrdinalIgnoreCase) >= 0) return "Specifying";
            if (section.IndexOf("Generat", StringComparison.OrdinalIgnoreCase) >= 0) return "Generating";
            if (section.IndexOf("Compil", StringComparison.OrdinalIgnoreCase) >= 0) return "Compiling";
            if (section.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("WebAppConfig", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0
                || section.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Finishing";
            return null;
        }
        private static readonly Regex _rxBuildDone  = new Regex(@"^\s*(Build|Specification|Compilation)\s+(succeeded|failed|complete)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxBuildOneEnd = new Regex(@"Build One Task\s+(terminado|finished|Sucesso|completed|ended)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Missing dependency surfacing — the GeneXus C# compile leaks CS0246 / CS2001
        // when a referenced object was never spec/gen'd. Pulling the missing type or
        // source file out of the error line gives the agent the next target to build
        // without spelunking the raw output:
        //   CS0246 ... 'wsretdatainicio' não pôde ser encontrado  → object 'retdatainicio'
        //   CS2001 ... 'painelclassesala_bc.cs' não pôde ser encontrado → object 'painelclassesala'
        private static readonly Regex _rxCs0246Missing = new Regex(
            @"error\s+CS0246:.*?['""‘’]([A-Za-z_][\w]*)['""‘’]",
            RegexOptions.Compiled);
        private static readonly Regex _rxCs2001Missing = new Regex(
            @"error\s+CS2001:.*?['""‘’]([^'""‘’]+\.cs)['""‘’]",
            RegexOptions.Compiled);
        // v2.6.6 Stream E (FR#7): index-aware normalizer. The pre-Stream-E version
        // could suggest a stripped name that doesn't exist in the KB (e.g.
        // "acessoperfil" → "cessoperfil" via a phantom 'a' strip; or "wsfoo"
        // when both "wsfoo" AND "foo" happen to be present, but only "wsfoo" is
        // the real object). When an IndexCacheService lookup is available the
        // result is verified:
        //   - stripped name known       → return stripped
        //   - stripped name unknown but
        //     ORIGINAL symbol known     → return original (no normalization)
        //   - neither known             → return null (suggestion dropped)
        // When lookup is null we keep legacy behaviour (return stripped form)
        // so call sites without an index cache still get a best-effort answer.
        private static string NormalizeMissingObjectName(string symbol, IndexCacheService lookup = null)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            // GX naming map: ws<obj>=web-service wrapper, a<obj>=async wrapper,
            //                <obj>_bc=business component, <obj>_level_detail=trn detail,
            //                p<obj>=print wrapper. Strip common prefixes/suffixes so the
            //                agent's next build hits the real KB object name.
            var original = symbol.Trim();
            var s = original;
            int us = s.LastIndexOf('_');
            if (us > 0)
            {
                var tail = s.Substring(us + 1).ToLowerInvariant();
                if (tail == "bc" || tail == "level" || tail == "detail" || tail == "ws" ||
                    tail == "impl" || tail == "client" || tail == "main")
                    s = s.Substring(0, us);
            }
            if (s.Length > 2 && s.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            // NOTE: do NOT strip 'a' / 'p' single-letter prefixes — those collide
            // with real KB object names that happen to start with those letters
            // (acessoperfil, painelclassesala, premiomerito, …). Only the explicit
            // 'ws' web-service wrapper prefix is safe to strip.

            if (lookup == null) return s;

            // Index-aware verification.
            bool strippedKnown = !string.IsNullOrEmpty(s) && lookup.IsObjectKnown(s);
            if (strippedKnown) return s;
            bool originalKnown = lookup.IsObjectKnown(original);
            if (originalKnown) return original;
            // Neither form is in the index — drop the suggestion rather than
            // sending the agent chasing a phantom target.
            return null;
        }

        // Friction 2026-05-22: late-phase failure markers in GeneXus MSBuild output.
        // >RO <name>          → a step ("Running ...") that ran but the build then exited 1
        // >E0 <code>: <msg>   → an explicit error from a later phase (no CS####/spc####)
        // Last match wins (most-recent step is the one that flipped exit code).
        private static readonly Regex _rxLatePhaseRO = new Regex(
            @"^\s*>?RO\s+(?<name>[^\r\n:]+?)\s*(?::\s*(?<msg>.+?))?\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex _rxLatePhaseE0 = new Regex(
            @"^\s*>?E0\s+(?<name>[^\r\n:]+?)\s*:\s*(?<msg>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);
        // "Generation: Sucesso" / "Generation succeeded" / "Compilation: Sucesso" etc.
        // Either Portuguese or English locale; we only check presence, not order.
        private static readonly Regex _rxGenerationOk = new Regex(
            @"^\s*Generation\s*[:\-]?\s*(succeeded|sucesso|ok|success)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex _rxCompilationOk = new Regex(
            @"^\s*Compilation\s*[:\-]?\s*(succeeded|sucesso|ok|success)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        internal static PhaseFailureInfo ExtractPhaseFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            // Prefer explicit E0 errors (carry a real message).
            Match lastE0 = null;
            foreach (Match m in _rxLatePhaseE0.Matches(output))
                lastE0 = m;
            if (lastE0 != null)
            {
                return new PhaseFailureInfo
                {
                    Name = lastE0.Groups["name"].Value.Trim(),
                    Message = lastE0.Groups["msg"].Value.Trim()
                };
            }
            // Fall back to the last RO marker — the step that was running when the build died.
            Match lastRO = null;
            foreach (Match m in _rxLatePhaseRO.Matches(output))
                lastRO = m;
            if (lastRO != null)
            {
                return new PhaseFailureInfo
                {
                    Name = lastRO.Groups["name"].Value.Trim(),
                    Message = string.IsNullOrEmpty(lastRO.Groups["msg"].Value)
                        ? "Step exited non-zero; no explicit error line emitted."
                        : lastRO.Groups["msg"].Value.Trim()
                };
            }
            return null;
        }

        internal static bool DidGenerationAndCompilationSucceed(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            return _rxGenerationOk.IsMatch(output) && _rxCompilationOk.IsMatch(output);
        }

        internal static bool HasBuildAllCompletionEvidence(string output)
        {
            return !string.IsNullOrEmpty(output)
                && output.IndexOf("[GXMCP-BUILD-ALL] BuildAll completed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool DetectBuildAllReorgRequired(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return false;
            string text = output.ToLowerInvariant();
            bool mentionsReorg = text.Contains("reorg") || text.Contains("reorganization") || text.Contains("reorganiza");
            if (!mentionsReorg) return false;
            if (text.Contains("no reorg") || text.Contains("no reorganization")
                || text.Contains("not required") || text.Contains("not needed")
                || text.Contains("does not require") || text.Contains("doesn't require")
                || text.Contains("não necessária") || text.Contains("nao necessaria")
                || text.Contains("não requer") || text.Contains("nao requer"))
                return false;
            return text.Contains("required") || text.Contains("needed") || text.Contains("necess");
        }

        internal static void FinalizeBuildAllStatus(BuildTaskStatus status, string output)
        {
            if (status == null || !string.Equals(status.Action, "BuildAll", StringComparison.OrdinalIgnoreCase)) return;
            status.BuildMode = "BuildAll";
            status.KbOpened = status.KbOpened == true
                || (!string.IsNullOrEmpty(output) && output.IndexOf("[GXMCP-BUILD-ALL] KB opened", StringComparison.OrdinalIgnoreCase) >= 0);
            status.BuildAllDone = status.BuildAllDone == true || HasBuildAllCompletionEvidence(output);
            status.ReorgRequired = status.ReorgRequired == true || DetectBuildAllReorgRequired(output);
            status.MsBuildExitCode = status.ExitCode;

            if (status.ReorgRequired == true)
            {
                status.Status = "ReorgRequired";
                status.Error = "Build All stopped because the selected Knowledge Base requires reorganization. Run genexus_lifecycle action=reorg explicitly, then retry action=build_all.";
                status.Hint = "FailIfReorg=true prevented a silent schema change. Run genexus_lifecycle action=reorg, then retry action=build_all.";
            }
            else if (status.BuildAllDone != true)
            {
                status.Status = "Failed";
                status.Error = "Build All did not emit completion evidence; an exit code of 0 alone is not sufficient to confirm that Build All ran.";
                status.Hint = "Inspect fullLogPath and retry after confirming the SDK/MSBuild task completed.";
            }
        }

        // v2.6.6 Stream E (FR#9): CS2001 referencing "<obj>_bc.cs" is treated as
        // an orphan demotion when the underlying object is not a Transaction in the
        // current index (either missing entirely, or renamed to a different type).
        // Conservative: if the index lookup is unavailable, the predicate returns
        // false so the legacy "error stands" behaviour holds.
        // issue #32 item 5: is `name` one of the objects this build/spec pass targets?
        // Matches Target, any entry in Targets, or the CurrentObject being specified —
        // all case-insensitive. Used to suppress the spurious "not found in KB" warning.
        internal static bool IsBuildTarget(string name, BuildTaskStatus status)
        {
            if (string.IsNullOrWhiteSpace(name) || status == null) return false;
            if (string.Equals(name, status.Target, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, status.CurrentObject, StringComparison.OrdinalIgnoreCase)) return true;
            if (status.Targets != null)
            {
                foreach (var t in status.Targets)
                    if (string.Equals(name, t, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static bool IsBcOrphanError(string line, IndexCacheService lookup)
        {
            if (lookup == null || string.IsNullOrEmpty(line)) return false;
            var m = _rxCs2001Missing.Match(line);
            if (!m.Success) return false;
            var file = m.Groups[1].Value;
            var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
            var bare = slash >= 0 ? file.Substring(slash + 1) : file;
            if (bare.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - 3);
            if (!bare.EndsWith("_bc", StringComparison.OrdinalIgnoreCase)) return false;
            var trnName = bare.Substring(0, bare.Length - 3);
            if (string.IsNullOrEmpty(trnName)) return false;
            try
            {
                var entry = lookup.TryGetEntryByName(trnName);
                if (entry == null) return true; // Transaction gone — _bc.cs is orphan
                // Different type with the same name → not a Transaction → orphan.
                return !string.Equals(entry.Type, "Transaction", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // FR#12 (friction-report 2026-05-14): MSBuild + GeneXus emit dozens of
        // "Copiando módulo X" / "Copying module X" / "Restoring NuGet packages" lines per build.
        // They go to FullOutput (terminal payload) but get filtered out of TailLines so the
        // live tail shows actionable signal. Phase change / Errors / Warnings always pass.
        private static readonly Regex _rxModuleCopyNoise = new Regex(
            @"^\s*(Copiando|Copying|Restoring NuGet|Restaurando NuGet|Wrote\s+|Touching\s+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public class ErrorDetail
        {
            public string raw { get; set; }
            public string rewritten { get; set; }
            public string phase { get; set; }
            public string gxObject { get; set; }
            // issue #28 item 13: "environment" (infra — missing generated sources,
            // unresolved DLL refs, locked outputs, NuGet restore) vs "spec" (spc/gen
            // diagnostics on the authored object) vs "code" (C# compile of authored
            // code). Lets the agent tell "my code is wrong" from "the env can't compile".
            public string category { get; set; }
        }

        // issue #28 item 13: classify a raw error line so build output can be split
        // into environment errors (not the edited object's fault) vs spec/code errors.
        //   environment: CS2001 (missing generated .cs, e.g. GxWebServicesConfig.cs),
        //                MSB3245 (unresolved DLL ref), MSB3027/MSB3021 (locked output),
        //                MSB4018/MSB4062 (task crash), CS0006 (missing metadata file),
        //                NU#### (NuGet restore), gtm####/mtd####/pmm#### (build infrastructure),
        //                rgz####/rgo#### (reorganization infrastructure).
        //   spec:        spc####, gen####, src####, qry####.
        //   code:        everything else that matched _rxError (authored-code CS####).
        private static readonly Regex _rxEnvError = new Regex(
            @"\b(CS2001|CS0006|MSB3245|MSB3027|MSB3021|MSB4018|MSB4062|NU\d{3,4}|gtm\d+|mtd\d+|pmm\d+|rgz\d+|rgo\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxSpecError = new Regex(
            @"\berror\s+(spc|gen|src|qry)\d+\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static string ClassifyErrorCategory(string line)
        {
            if (string.IsNullOrEmpty(line)) return "code";
            if (_rxEnvError.IsMatch(line)) return "environment";
            if (_rxSpecError.IsMatch(line)) return "spec";
            return "code";
        }

        // issue #28 item 14: GeneXus lowercases identifiers in its diagnostics
        // (e.g. "&Objcod is not defined" when the source authored "&ObjCod"). Rewrite
        // each &-prefixed token to the canonical casing the KB actually uses, looked up
        // via the index (attributes/objects are stored with authored casing under a
        // case-insensitive key). Only rewrites when a same-name entry exists with a
        // DIFFERENT casing — never invents a name, never touches non-&-tokens — so a
        // literal string or an unknown identifier is left exactly as GeneXus emitted it.
        private static readonly Regex _rxAmpIdent = new Regex(@"&([A-Za-z_]\w*)", RegexOptions.Compiled);
        internal string NormalizeErrorIdentifierCase(string line)
        {
            if (string.IsNullOrEmpty(line) || _indexCacheService == null || line.IndexOf('&') < 0) return line;
            try
            {
                return _rxAmpIdent.Replace(line, m =>
                {
                    string tok = m.Groups[1].Value;
                    var entry = _indexCacheService.TryGetEntryByName(tok);
                    if (entry != null && !string.IsNullOrEmpty(entry.Name)
                        && string.Equals(entry.Name, tok, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(entry.Name, tok, StringComparison.Ordinal))
                    {
                        return "&" + entry.Name;
                    }
                    return m.Value;
                });
            }
            catch { return line; }
        }

        public class BuildTaskStatus
        {
            public string TaskId { get; set; }
            public string Status { get; set; }            // Accepted | Running | Succeeded | Failed | Error | Cancelled | ReorgRequired
            public string Phase { get; set; }             // Starting | OpeningKB | Specifying | Generating | Compiling | Finishing | Done
            public string Action { get; set; }
            [JsonProperty("buildMode", NullValueHandling = NullValueHandling.Ignore)]
            public string BuildMode { get; set; }
            [JsonProperty("kbOpened", NullValueHandling = NullValueHandling.Ignore)]
            public bool? KbOpened { get; set; }
            [JsonProperty("buildAllDone", NullValueHandling = NullValueHandling.Ignore)]
            public bool? BuildAllDone { get; set; }
            [JsonProperty("reorgRequired", NullValueHandling = NullValueHandling.Ignore)]
            public bool? ReorgRequired { get; set; }
            [JsonProperty("msBuildExitCode", NullValueHandling = NullValueHandling.Ignore)]
            public int? MsBuildExitCode { get; set; }
            public string Environment { get; set; }
            public bool? UpToDate { get; set; }
            // issue #42 hardening (D) — the KB this build belongs to. The concurrent-build
            // reject and activeBuilds surfacing filter on it so a build on KB-A never
            // rejects a build on KB-B under a future shared-worker / warm-spares mode.
            // Single-KB per worker today, so null-safe (a null filter matches all).
            [JsonIgnore] internal string KbPath { get; set; }
            public string Target { get; set; }
            public List<string> Targets { get; set; }   // populated when batch build (multi-target)
            public int? TargetsTotal { get; set; }
            public int? TargetsDone { get; set; }
            public string CurrentObject { get; set; }
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
            /// <summary>
            /// Object names extracted from CS0246/CS2001 compile errors —
            /// dependencies whose .dll is stale or never built. The agent
            /// should chain <c>genexus_lifecycle build target=...</c> on
            /// these before retrying.
            /// </summary>
            public HashSet<string> SuggestedRebuildTargets { get; set; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> NotFoundTargets { get; set; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> UnreachableTargets { get; set; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int LineCount { get; set; }
            public string LastLine { get; set; }
            public List<string> TailLines { get; set; } = new List<string>();
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
            // FR#21 (v2.6.6 Stream C): keep both the raw MSBuild line and the
            // rewritten "[gx-object=... phase=...]" form so debugging is possible.
            public List<ErrorDetail> ErrorsDetailed { get; set; } = new List<ErrorDetail>();
            // issue #28 item 13: environment vs authored-code split, derived from
            // ErrorsDetailed's category. Environment errors (missing generated sources,
            // unresolved DLLs, locked outputs, NuGet) mean the KB won't compile in this
            // environment — NOT that the edited object is wrong. Kept as computed getters
            // so they auto-serialize into every status/result envelope.
            [JsonProperty("envErrors")]
            public List<string> EnvErrors =>
                (ErrorsDetailed ?? new List<ErrorDetail>())
                    .Where(e => e.category == "environment")
                    .Select(e => e.rewritten ?? e.raw)
                    .ToList();
            [JsonProperty("codeErrors")]
            public List<string> CodeErrors =>
                (ErrorsDetailed ?? new List<ErrorDetail>())
                    .Where(e => e.category != "environment")
                    .Select(e => e.rewritten ?? e.raw)
                    .ToList();
            [JsonProperty("envErrorCount")]
            public int EnvErrorCount => (ErrorsDetailed ?? new List<ErrorDetail>()).Count(e => e.category == "environment");
            [JsonProperty("codeErrorCount")]
            public int CodeErrorCount => (ErrorsDetailed ?? new List<ErrorDetail>()).Count(e => e.category != "environment");
            // Populated only when the failure is purely environmental so the agent
            // doesn't chase a phantom code bug. Null (omitted) otherwise.
            [JsonProperty("envErrorsHint")]
            public string EnvErrorsHint =>
                (EnvErrorCount > 0 && CodeErrorCount == 0)
                    ? "Build failed only on environment/infra errors (missing generated sources, unresolved DLL references, locked outputs, metadata/package generation, reorganization, or NuGet restore) — not on the edited object's spec/code. Fix the KB environment (regenerate/restore) and rebuild; the authored object may already be correct."
                    : null;
            [JsonProperty("specErrorCount")]
            public int SpecErrorCount =>
                (ErrorsDetailed ?? new List<ErrorDetail>()).Count(e => e.category == "spec");
            // spc####/gen####/src####/qry#### diagnostics are only trustworthy when the build environment
            // is fully generated. In an ungenerated/broken environment the specifier can
            // emit a spurious spc#### that is invariant to the Source (fixed line number,
            // fires even on known-good objects). Surface a hint whenever spec errors appear
            // — especially alongside environment errors — so the agent doesn't chase a
            // phantom source bug. Never suppresses the error itself.
            [JsonProperty("specErrorsHint")]
            public string SpecErrorsHint =>
                (SpecErrorCount > 0)
                    ? ((EnvErrorCount > 0
                            ? "Both spec (spc####/gen####/src####/qry####) and environment/infra errors are present — the spec errors are likely INDUCED by the ungenerated/broken build environment, not by the object's Source. "
                            : "Spec diagnostics (spc####/gen####/src####/qry####) depend on a fully generated build environment. ")
                       + "If a spc#### cites a fixed line unrelated to the actual Source, or fires regardless of what the Source contains (even on known-good objects), regenerate the environment (genexus_lifecycle action=rebuild, then action=reorg) before treating it as an authored-code error. For build-independent Source validation use genexus_lifecycle action=validate (save-time SDK check).")
                    : null;
            [JsonProperty("suggestedFixes", NullValueHandling = NullValueHandling.Ignore)]
            public List<AutoFixSuggestion> SuggestedFixes
            {
                get
                {
                    var rawLines = (ErrorsDetailed != null && ErrorsDetailed.Count > 0)
                        ? ErrorsDetailed.Select(e => e.raw ?? e.rewritten)
                        : (Errors ?? new List<string>());
                    var diagnosed = ErrorDiagnoser.Diagnose(rawLines, Target ?? CurrentObject);
                    return (diagnosed != null && diagnosed.Count > 0) ? diagnosed : null;
                }
            }
            // FR#9 (v2.6.6 Stream E): CS2001 errors referencing "<obj>_bc.cs" where
            // the underlying Transaction no longer exists (or isn't a Transaction)
            // are demoted to warnings — counted here, full lines preserved in
            // Warnings[] with a "[demoted-bc-orphan]" prefix.
            public int DemotedErrors { get; set; }
            // FR#22 (v2.6.6 Stream C): shaped output envelope (head/tail/full-log-path).
            // The legacy flat string is kept for backwards compat in non-compact mode.
            public object Output { get; set; }
            public string FullLogPath { get; set; }
            public string StartTime { get; set; }
            public string EndTime { get; set; }
            public double? ElapsedSeconds { get; set; }
            public int ExitCode { get; set; }
            public string Error { get; set; }
            // v2.6.6 Stream D: "inproc" when GeneXus MSBuild tasks ran inside the
            // worker against the open KB, "msbuild-exe" when we fell back to the
            // external MSBuild.exe spawn. Surfaced for telemetry / debugging.
            public string BuildPath { get; set; }
            public List<string> CallersToAlsoBuild { get; set; }
            public string Hint { get; set; }
            // v2.3.8 (Task 5.1/5.2): expansion plan applied to the BuildOne
            // sequence. Surfaced under _meta.buildPlan in status/result.
            public BuildPlan BuildPlan { get; set; }
            // Friction 2026-05-22 (experimental): when true and the in-process
            // build path is taken, skip the IdeWebBuildAndDeploy step. See
            // InProcessBuildRunner.Run for the safety story.
            [JsonIgnore] internal bool SkipFullDeploy { get; set; }
            // A2: force the full IdeWebBuildAndDeploy path (Theme/Image/Style/Module
            // copy + WebAppConfig) for a targeted build, so the compiled .dll/.aspx
            // are actually copied to web/bin and the object is runnable — the fast
            // BuildOne path skips that copy. Opt-in per call (deploy=true).
            [JsonIgnore] internal bool FullDeploy { get; set; }
            // issue #28 item 12: spec-check only — run SpecifyOneOnly (Spec+Gen), skip
            // Compile + deploy, so spc*/gen* diagnostics surface without a full build.
            [JsonIgnore] internal bool SpecifyOnly { get; set; }
            // compile_check: spec+gen+compile the targets plus everything that calls
            // them (transitive callers), routed through the targeted BuildOne path so
            // the KB-wide DeveloperMenu regeneration (the dominant cost of a full
            // build-all) is skipped. Surfaced in the status/accepted envelope.
            public bool CompileCheck { get; set; }
            // Callers pulled in by compile_check beyond the objects the user named,
            // and whether the caller graph was capped. Echoed so the agent knows
            // the coverage of the check.
            public List<string> CompileCheckCallers { get; set; }
            public bool CompileCheckTruncated { get; set; }
            // Item 28 (Tier-S, EXPERIMENTAL) — fastIncremental decision metadata.
            // Surfaced under top-level response fields (not status output) so the
            // agent sees the decision exactly once, with the Accepted envelope.
            [JsonIgnore] internal bool FastIncrementalRequested { get; set; }
            public bool FastIncrementalFallback { get; set; }
            public string FastIncrementalFallbackReason { get; set; }
            [JsonIgnore] internal bool FastIncrementalCanSkipDeploy { get; set; }
            [JsonIgnore] internal IReadOnlyList<string> FastIncrementalCanSkipSpecify { get; set; }
            // Item 72 (friction 2026-05-22) — webhook URL to POST a failure summary
            // to when terminal Status == "Failed". Empty / null disables the call.
            [JsonIgnore] internal string NotifyOnFailureUrl { get; set; }
            // Friction 2026-05-22: "Build Failed: 0 errors, 0 warnings" with no
            // signal was forcing the agent to read the raw Output and guess what
            // happened. When ErrorCount=0 but ExitCode!=0 (a late MSBuild step
            // like WebAppConfig fails), surface the last >RO/>E0 line as a
            // structured phase_failure block.
            public PhaseFailureInfo PhaseFailure { get; set; }
            // "partial_success" means Generation: Sucesso + Compilation: Sucesso
            // were observed in the Output but the overall build was marked Failed
            // (typically due to a downstream packaging/deploy step). The compiled
            // DLLs are usually fine and the run-time picks them up.
            public bool? PartialSuccess { get; set; }

            // issue #42 — build-evidence gate. A build can report Status=Succeeded
            // while the generated .cs in the environment output dir (e.g.
            // NETCoreMySQL\web) was never written/updated. After a successful build
            // we probe the requested/dirty targets' generated files for freshness
            // (LastWriteTimeUtc >= StartedAt). When a target that was expected to
            // regenerate has no fresh .cs, it lands in GenerateEvidence.staleOrMissing
            // and a "[generate-gap]" warning is raised so the outcome is no longer a
            // clean success. Null (omitted) when the gate did not run.
            public JObject GenerateEvidence { get; set; }
            // Snapshot of objects marked dirty (edited via MCP, not yet built) at the
            // moment the build started. Captured before the in-process pipeline runs
            // its MarkClean calls, so the gate knows which targets were expected to
            // (re)generate. JsonIgnore — internal scaffolding, surfaced via GenerateEvidence.
            [JsonIgnore] internal List<string> DirtyAtStart { get; set; }
            // issue #42 hardening — pre-build LastWriteTimeUtc of each target's
            // freshest generated .cs, keyed by bare object name (OrdinalIgnoreCase).
            // The evidence gate treats a file as freshly (re)generated when its mtime
            // MOVED past this snapshot, which is immune to wall-clock/FS-granularity
            // skew (comparing "did the file change?" instead of "is mtime >= a clock
            // reading?"). Absent entry (object had no .cs before) falls back to the
            // absolute StartedAt comparison in ProbeGeneratedFreshness.
            [JsonIgnore] internal Dictionary<string, DateTime> PreBuildMtimes { get; set; }

            [JsonIgnore] internal Process Process { get; set; }
            [JsonIgnore] internal DateTime StartedAt { get; set; }
            [JsonIgnore] internal StringBuilder FullOutput { get; set; } = new StringBuilder();
            [JsonIgnore] internal object _lock = new object();
            // v2.6.6 Stream F (FR#19 follow-up): event-driven long-poll. Polling
            // `status` every ~250ms generated 24 round-trips for a 10-minute
            // build. Wait callers block on this signal until HandleLine sees a
            // change worth surfacing (phase / counts / TargetsDone / terminal).
            [JsonIgnore] internal ManualResetEventSlim StateChangeSignal { get; } = new ManualResetEventSlim(false);
            // Aggregated-warnings memo. AggregateWarnings is O(N log codes) but
            // is called on every paginated status poll (the LLM polls many times
            // during a long build). Invalidated whenever WarningCount changes —
            // _aggregateMemoForCount stores the WarningCount the cache was built at.
            [JsonIgnore] internal List<BuildOutputShaper.WarningGroup> AggregatedWarningsCache { get; set; }
            [JsonIgnore] internal int AggregateMemoForCount { get; set; } = -1;

            /// <summary>Compact baseline string used by GetStatusWait for ETag-style change detection.
            /// Stable across pagination, kept short (&lt;50 chars) so it round-trips cheaply.</summary>
            internal string ComputeBaseline()
            {
                return (Phase ?? "") + "|" + (TargetsDone?.ToString() ?? "") + "|" + ErrorCount + "|" + WarningCount + "|" + (Status ?? "");
            }
        }

        // v2.6.6 Stream F: terminal statuses always return immediately from GetStatusWait.
        private static readonly HashSet<string> _terminalStatuses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Succeeded", "Failed", "Cancelled", "Error", "ReorgRequired" };
        private static bool IsTerminalStatus(string s)
            => !string.IsNullOrEmpty(s) && _terminalStatuses.Contains(s);

        // Item 28 (mcp-improvements-2026-05-22, Tier-S) — EXPERIMENTAL. Pluggable
        // decision strategy for the fastIncremental opt-in. Default impl reads
        // EditDirtyTracker + IndexCacheService; tests inject a fake.
        private IFastIncrementalDecision _fastIncrementalDecision;
        public void SetFastIncrementalDecision(IFastIncrementalDecision d) { _fastIncrementalDecision = d; }
        internal IFastIncrementalDecision GetFastIncrementalDecisionForTest() => _fastIncrementalDecision;

        public BuildService()
        {
            _gxDir = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";

            string[] searchPaths = new[] {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
                Path.Combine(_gxDir, "MSBuild.exe"),
                @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
            };

            foreach (var p in searchPaths) { if (File.Exists(p)) { _msbuildPath = p; break; } }
        }

        public void SetKbService(KbService kbService) { _kbService = kbService; }
        public void SetIndexCacheService(IndexCacheService ics) { _indexCacheService = ics; }
        public void SetCallerGraphService(CallerGraphService graph) { _callerGraphService = graph; }
        public KbService KbService => _kbService;

        // v2.3.8 (Task 5.1) — friction report 2026-05-15 #7. Building a
        // WebPanel that calls N procedures emitted CS0246 because each
        // generated csproj lacked references to callee DLLs. Fix: expand the
        // target list via CallerGraphService and order it reverse-topologically
        // (deepest callees first, originally requested targets last), then
        // emit BuildOne for each — every csproj sees its dependencies on
        // disk by the time GeneXus generates it.
        public class PhaseFailureInfo
        {
            // e.g. "WebAppConfig", "Copying Module", "Deploy". Pulled from the last
            // ">RO <name>" or ">E0..." line the worker saw before the build exited.
            public string Name { get; set; }
            public string Message { get; set; }
        }

        public class BuildPlan
        {
            public List<string> Expanded { get; set; } = new List<string>();
            public List<string> Skipped { get; set; } = new List<string>();
            public List<string> AmbiguousTargets { get; set; } = new List<string>();
            public bool Truncated { get; set; }
            public int NodeCap { get; set; }
            public int RequestedNodes { get; set; }
            public string IncludeCallees { get; set; }
        }

        public BuildPlan ExpandTargets(IEnumerable<string> targets, string includeCallees = "transitive", int cap = 200)
        {
            var plan = new BuildPlan { NodeCap = cap, IncludeCallees = includeCallees ?? "transitive" };
            var originalList = (targets ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .ToList();
            var originalSet = new HashSet<string>(originalList, StringComparer.OrdinalIgnoreCase);

            if (_indexCacheService != null)
            {
                foreach (var target in originalList)
                {
                    if (_indexCacheService.FindEntriesByName(target).Count > 1)
                        plan.AmbiguousTargets.Add(target);
                }
                if (plan.AmbiguousTargets.Count > 0)
                {
                    plan.Expanded.AddRange(originalList);
                    return plan;
                }
            }

            // No graph or "none" → preserve original order, but still inject
            // BC variants (FR#8) since the BC heuristic is index-driven, not
            // caller-graph-driven.
            if (_callerGraphService == null || string.Equals(plan.IncludeCallees, "none", StringComparison.OrdinalIgnoreCase))
            {
                var bcOnly = CollectBcVariants(originalList, originalSet, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                plan.Expanded.AddRange(bcOnly);
                plan.Expanded.AddRange(originalList);
                return plan;
            }

            // Collect callees per requested target. We over-fetch (cap+1) to
            // detect truncation before mixing in the originally requested set.
            var calleeOrder = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int requestedTotal = 0;

            foreach (var t in originalList)
            {
                if (string.Equals(plan.IncludeCallees, "direct", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var c in _callerGraphService.GetCallees(t))
                    {
                        requestedTotal++;
                        if (originalSet.Contains(c)) continue;
                        if (seen.Add(c)) calleeOrder.Add(c);
                    }
                }
                else // transitive (default)
                {
                    var trans = _callerGraphService.GetCalleesTransitive(t, maxNodes: cap + 1);
                    requestedTotal += trans.Nodes.Count;
                    if (trans.Truncated)
                    {
                        plan.Truncated = true;
                        plan.RequestedNodes = requestedTotal;
                    }
                    foreach (var c in trans.Nodes)
                    {
                        if (originalSet.Contains(c)) continue;
                        if (seen.Add(c)) calleeOrder.Add(c);
                    }
                }
            }

            if (plan.Truncated)
            {
                // Don't emit a partial plan — caller decides (BuildPlanTooLarge response).
                plan.Expanded = originalList;
                return plan;
            }

            // Reverse so the deepest leaves come first (callees before callers).
            // BFS yields shallowest-first; reversing gives the topological order
            // we want for sequential BuildOne emission.
            calleeOrder.Reverse();

            // v2.6.6 Stream E (FR#8): inject BC variants ahead of the trn so
            // per-object csprojs see the BC .dll on disk by the time the
            // Transaction is generated.
            var bcPrefix = CollectBcVariants(originalList, originalSet, seen);
            if (bcPrefix.Count > 0)
            {
                calleeOrder.InsertRange(0, bcPrefix);
            }

            plan.Expanded.AddRange(calleeOrder);
            plan.Expanded.AddRange(originalList);
            return plan;
        }

        // v2.6.6 Stream E (FR#8): for each Transaction in <originalList>, if the
        // index also carries a "<name>_bc" object the build pipeline needs that
        // variant compiled first. Returns the BC variants in input order; skips
        // ones already in the original set or already collected as callees.
        private List<string> CollectBcVariants(IList<string> originalList, HashSet<string> originalSet, HashSet<string> seen)
        {
            var result = new List<string>();
            if (_indexCacheService == null) return result;
            foreach (var t in originalList)
            {
                var entry = _indexCacheService.TryGetEntryByName(t);
                if (entry == null) continue;
                if (!string.Equals(entry.Type, "Transaction", StringComparison.OrdinalIgnoreCase)) continue;
                var bcName = t + "_bc";
                if (originalSet.Contains(bcName)) continue;
                if (seen.Contains(bcName)) continue;
                // Single name-keyed lookup confirms presence; previously this
                // line redundantly scanned the index a second time via
                // IsObjectKnown(bcName).
                if (_indexCacheService.TryGetEntryByName(bcName) == null) continue;
                result.Add(bcName);
                seen.Add(bcName);
            }
            return result;
        }

        public string Build(string action, string target)
        {
            return Build(action, target, includeCallees: "transitive", buildPlanCap: 200);
        }

        public string Build(string action, string target, string includeCallees, int buildPlanCap)
        {
            return Build(action, target, includeCallees, buildPlanCap, skipFullDeploy: false);
        }

        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy)
            => Build(action, target, includeCallees, buildPlanCap, skipFullDeploy, null);

        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy, string notifyOnFailure)
            => Build(action, target, includeCallees, buildPlanCap, skipFullDeploy, notifyOnFailure, fastIncremental: false);

        // issue #28 item 12: spec-check entry. Runs the SpecifyOneOnly pass (Spec+Gen,
        // no Compile, no deploy) for the target so the agent sees spc*/gen* diagnostics
        // without the full ~compile+deploy build. Reuses the whole build-task pipeline;
        // the #13 error split surfaces the spec diagnostics under codeErrors.
        public string Specify(string target)
            => Build("Build", target, includeCallees: "none", buildPlanCap: 200,
                     skipFullDeploy: false, notifyOnFailure: null, fastIncremental: false, specifyOnly: true);

        // mode=compile_check: "did my edits break the build?" without the ~200s
        // DeveloperMenu regeneration a full build-all pays. Expands the requested
        // objects to include everything that CALLS them (transitive, via the caller
        // graph), then builds that set through the targeted BuildOne path — which
        // spec+gen+compiles each object (surfacing CS/spc/gen errors) but never
        // regenerates the KB-wide DeveloperMenu. Requires target(s): the whole point
        // is to scope the check to the edited objects and their blast radius. For a
        // from-scratch full-KB compile, use action=build with no target.
        // A3: a base transaction (a BC called everywhere) has a huge transitive
        // caller closure — expanding it pulls in fan-in orchestrators like the
        // KB-wide DeveloperMenu and can drag dozens of DLLs / 20-30 min, defeating
        // the whole "fast check" purpose. Default the caller closure to a modest
        // cap and let the agent opt out of caller expansion entirely (callers=false
        // → target-only). When the closure is truncated or large, the result echoes
        // CompileCheckTruncated so the agent knows coverage was bounded.
        public const int CompileCheckDefaultCallerCap = 40;

        public string CompileCheck(string target, int buildPlanCap = 200,
            bool includeCallers = true, int callerCap = 0)
        {
            var seeds = ParseTargets(target);
            if (seeds.Count == 0)
            {
                return McpResponse.Err(
                    code: "CompileCheckNeedsTarget",
                    message: "mode=compile_check requires target=<object(s)> (comma-separated). It compiles the named objects plus everything that calls them, skipping the DeveloperMenu regeneration — so it must know which objects you changed. For a full from-scratch KB compile, use action=build with no target.");
            }

            // Expand to transitive callers so a changed signature surfaces errors in
            // every object that invokes it — the KB-wide breakage a plain targeted
            // build misses. Callers unavailable (no caller graph / index not built)
            // degrades gracefully to just the named objects, with a note.
            // callers=false skips this entirely (target-only check).
            var expanded = new List<string>(seeds);
            var seen = new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase);
            var addedCallers = new List<string>();
            bool truncated = false;
            // Cap the caller closure so a base BC doesn't drag the whole KB. An
            // explicit callerCap>0 overrides the modest default; both stay under
            // buildPlanCap (the hard BuildPlanTooLarge ceiling in ExpandTargets).
            int effectiveCallerCap = callerCap > 0
                ? Math.Min(callerCap, buildPlanCap)
                : Math.Min(CompileCheckDefaultCallerCap, buildPlanCap);
            bool graphAvailable = _callerGraphService != null;
            if (includeCallers && graphAvailable)
            {
                foreach (var s in seeds)
                {
                    TransitiveResult tr;
                    try { tr = _callerGraphService.GetCallersTransitive(s, effectiveCallerCap); }
                    catch { continue; }
                    if (tr == null) continue;
                    if (tr.Truncated) truncated = true;
                    foreach (var c in tr.Nodes)
                    {
                        if (addedCallers.Count >= effectiveCallerCap) { truncated = true; break; }
                        if (seen.Add(c)) { expanded.Add(c); addedCallers.Add(c); }
                    }
                }
            }

            // A3: callers already gives the objects that must recompile against the
            // changed target; re-expanding CALLEES (transitive) here would pull each
            // caller's whole dependency graph back in — re-dragging orchestrators and
            // the DeveloperMenu the check is meant to skip. Keep the plan to exactly
            // {seeds + callers}: includeCallees=none.
            string result = Build("Build", string.Join(",", expanded),
                includeCallees: "none", buildPlanCap: buildPlanCap,
                skipFullDeploy: false, notifyOnFailure: null, fastIncremental: false,
                specifyOnly: false, compileCheck: true,
                compileCheckCallers: addedCallers, compileCheckTruncated: truncated,
                compileCheckGraphAvailable: graphAvailable);
            return result;
        }

        /// <summary>
        /// dryRun=true: expands the build plan without dispatching a build task.
        /// Returns code=DryRun with the resolved targets list so the agent can
        /// preview what would compile.
        /// </summary>
        public string BuildDryRun(string action, string target, string includeCallees, int buildPlanCap)
        {
            try
            {
                if (string.Equals(action, "BuildAll", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        return McpResponse.Err(
                            code: "BuildAllTargetNotAllowed",
                            message: "action=build_all is global and cannot accept target; omit target. Use action=build for directed builds.",
                            extra: new JObject { ["action"] = "build_all", ["target"] = target });
                    }

                    return McpResponse.Ok(
                        code: "DryRun",
                        result: new JObject
                        {
                            ["preview"] = new JObject
                            {
                                ["action"] = "BuildAll",
                                ["buildMode"] = "BuildAll",
                                ["kbWide"] = true,
                                ["failIfReorg"] = true,
                                ["wouldBuild"] = new JArray("<entire selected Knowledge Base>"),
                                ["includeCallees"] = "ignored for Build All",
                                ["buildPlanCap"] = buildPlanCap
                            }
                        });
                }

                var targets = ParseTargets(target);
                BuildPlan plan = null;
                if (action != null && action.Equals("Build", StringComparison.OrdinalIgnoreCase) && targets.Count > 0)
                {
                    plan = ExpandTargets(targets, includeCallees ?? "transitive", buildPlanCap);
                    if (plan.Truncated)
                    {
                        return McpResponse.Err(
                            code: "BuildPlanTooLarge",
                            message: "Build plan exceeded cap in dryRun expansion.",
                            extra: new JObject
                            {
                                ["requestedNodes"] = plan.RequestedNodes,
                                ["cap"] = plan.NodeCap,
                                ["includeCallees"] = plan.IncludeCallees
                            });
                    }
                    if (plan.AmbiguousTargets.Count > 0)
                    {
                        return McpResponse.Err(
                            code: "BuildTargetAmbiguous",
                            message: "One or more build targets resolve to multiple typed GeneXus objects.",
                            extra: new JObject { ["targets"] = JArray.FromObject(plan.AmbiguousTargets) });
                    }
                    targets = plan.Expanded;
                }
                return McpResponse.Ok(
                    code: "DryRun",
                    result: new JObject
                    {
                        ["preview"] = new JObject
                        {
                            ["action"] = action,
                            ["wouldBuild"] = new JArray(targets.ToArray()),
                            ["includeCallees"] = includeCallees ?? "transitive",
                            ["buildPlanCap"] = buildPlanCap
                        }
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "DryRunFailed", message: ex.Message);
            }
        }

        // Item 28 — EXPERIMENTAL. When fastIncremental=true, ask the configured
        // IFastIncrementalDecision what we can skip given the current dirty set.
        // Three short-circuit envelopes are surfaced ahead of the normal
        // "Accepted" response so the agent sees the chosen path:
        //   - nothingDirty=true                    → no build queued
        //   - fastIncrementalFallback=true         → legacy full build queued
        //   - fastIncremental={canSkipDeploy,...}  → fast build queued
        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy, string notifyOnFailure, bool fastIncremental, bool fullDeploy = false)
            => Build(action, target, includeCallees, buildPlanCap, skipFullDeploy, notifyOnFailure, fastIncremental, specifyOnly: false, fullDeploy: fullDeploy);

        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy, string notifyOnFailure, bool fastIncremental, bool specifyOnly, bool fullDeploy = false)
            => Build(action, target, includeCallees, buildPlanCap, skipFullDeploy, notifyOnFailure, fastIncremental, specifyOnly,
                     compileCheck: false, compileCheckCallers: null, compileCheckTruncated: false, compileCheckGraphAvailable: true, fullDeploy: fullDeploy);

        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy, string notifyOnFailure, bool fastIncremental, bool specifyOnly,
                            bool compileCheck, List<string> compileCheckCallers, bool compileCheckTruncated, bool compileCheckGraphAvailable, bool fullDeploy = false)
        {
            if (string.Equals(action, "BuildAll", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(target))
            {
                return McpResponse.Err(
                    code: "BuildAllTargetNotAllowed",
                    message: "action=build_all is global and cannot accept target; omit target. Use action=build for directed builds.",
                    extra: new JObject { ["action"] = "build_all", ["target"] = target });
            }

            // issue #37 item 4: fast-fail reorg on a DBA-managed datastore
            // (Reorganize Server tables = No). GeneXus never applies the delta there,
            // so CheckAndInstallDatabase is a no-op the agent should not queue+poll.
            if (action != null && action.Equals("Reorg", StringComparison.OrdinalIgnoreCase))
            {
                var reorgGuard = CheckReorgDisabled();
                if (reorgGuard != null) return reorgGuard;
            }

            // issue #42 (P3c) — reject a second build while one is already running.
            // Builds are serialized per worker/KB (the SDK is single-model, in-process);
            // two concurrent IdeWebBuildAndDeploy passes race the generated output.
            // Opt out with GXMCP_ALLOW_CONCURRENT_BUILDS=1.
            if (!string.Equals(Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS"), "1", StringComparison.OrdinalIgnoreCase))
            {
                var active = GetActiveBuilds(GetKBPath()).FirstOrDefault();
                if (active != null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        status = "BuildAlreadyRunning",
                        code = "BuildAlreadyRunning",
                        message = "A build is already running (taskId=" + active.TaskId + ", action=" + active.Action
                            + ", phase=" + active.Phase + "). Builds are serialized per worker. Poll it via "
                            + "genexus_lifecycle action=status target=" + active.TaskId
                            + ", or cancel it via action=cancel before starting another.",
                        activeTaskId = active.TaskId,
                        activeAction = active.Action,
                        activePhase = active.Phase,
                        activeTarget = active.Target
                    });
                }
            }

            // Parse target: single name OR comma/semicolon-separated list for batch "Build With These Only"
            var targets = ParseTargets(target);

            // Item 28 — fastIncremental opt-in. The decision is computed BEFORE
            // BuildPlan expansion so the agent sees the same targets it passed.
            FastIncrementalDecision fiDecision = null;
            if (fastIncremental
                && action != null
                && action.Equals("Build", StringComparison.OrdinalIgnoreCase)
                && targets.Count > 0)
            {
                var decider = _fastIncrementalDecision ?? new DefaultFastIncrementalDecision(_indexCacheService);
                try { fiDecision = decider.Decide(GetKBPath(), targets); }
                catch (Exception ex)
                {
                    fiDecision = new FastIncrementalDecision
                    {
                        ForceFullBuild = true,
                        FallbackReason = "decision-threw: " + ex.Message
                    };
                }

                if (fiDecision.NothingDirty)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        status = "NoBuildNeeded",
                        nothingDirty = true,
                        targets = targets,
                        message = "fastIncremental=true and EditDirtyTracker has no dirty entries for the requested targets. No build dispatched.",
                        experimental = true
                    });
                }
            }

            // v2.3.8 (Task 5.1) — expand via CallerGraphService so callees compile
            // before callers and every per-object csproj sees its dependency DLLs.
            // Only applies to Build action with concrete targets; RebuildAll/Reorg/etc. ignore.
            BuildPlan plan = null;
            if (action != null && action.Equals("Build", StringComparison.OrdinalIgnoreCase) && targets.Count > 0)
            {
                plan = ExpandTargets(targets, includeCallees ?? "transitive", buildPlanCap);
                if (plan.Truncated)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        status = "BuildPlanTooLarge",
                        suggested = "Build All from IDE, or set includeCallees='none' / 'direct' / reduce the target set",
                        graph = new { requestedNodes = plan.RequestedNodes, cap = plan.NodeCap },
                        requested = targets,
                        includeCallees = plan.IncludeCallees
                    });
                }
                if (plan.AmbiguousTargets.Count > 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        status = "BuildTargetAmbiguous",
                        code = "BuildTargetAmbiguous",
                        targets = plan.AmbiguousTargets,
                        requested = targets,
                        includeCallees = plan.IncludeCallees
                    });
                }
                targets = plan.Expanded;
            }

            string taskId = Guid.NewGuid().ToString().Substring(0, 8);
            // Resolve through KbService so the result follows the same SDK shape
            // probing used by whoami/environment telemetry.  On GeneXus 18 U5 the
            // active environment is not consistently exposed through
            // DesignModel.Environment, which previously left build responses with
            // no Environment field (#103 item 3).
            string envName = null;
            try { envName = _kbService?.GetActiveEnvironment(); } catch { }
            if (string.IsNullOrWhiteSpace(envName))
            {
                try { envName = _kbService?.GetKB()?.DesignModel?.Environment?.Name; } catch { }
            }
            var status = new BuildTaskStatus {
                TaskId = taskId,
                Action = action,
                BuildMode = string.Equals(action, "BuildAll", StringComparison.OrdinalIgnoreCase) ? "BuildAll" : null,
                Environment = envName,
                Target = target,
                // Always echo the parsed list so the agent can confirm what got dispatched,
                // including the single-target case. Doc contract says "the parsed list".
                Targets = targets.Count > 0 ? targets : null,
                Status = "Running",
                Phase = "Starting",
                StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StartedAt = DateTime.UtcNow,
                BuildPlan = plan,
                SpecifyOnly = specifyOnly
                    && string.Equals(action, "Build", StringComparison.OrdinalIgnoreCase)
                    && targets.Count >= 1,
                SkipFullDeploy = skipFullDeploy
                    && string.Equals(action, "Build", StringComparison.OrdinalIgnoreCase)
                    && targets.Count == 1
                    && string.Equals(includeCallees ?? "transitive", "none", StringComparison.OrdinalIgnoreCase),
                FullDeploy = fullDeploy
                    && string.Equals(action, "Build", StringComparison.OrdinalIgnoreCase),
                NotifyOnFailureUrl = notifyOnFailure,
                // Item 28 — surface decision on the task so status/result echo it.
                FastIncrementalRequested = fastIncremental,
                FastIncrementalFallback = fiDecision?.ForceFullBuild == true,
                FastIncrementalFallbackReason = fiDecision?.ForceFullBuild == true ? fiDecision.FallbackReason : null,
                FastIncrementalCanSkipDeploy = fiDecision?.CanSkipDeploy == true && fiDecision.ForceFullBuild == false,
                FastIncrementalCanSkipSpecify = fiDecision?.ForceFullBuild == false ? fiDecision.CanSkipSpecify : null,
                CompileCheck = compileCheck,
                CompileCheckCallers = (compileCheck && compileCheckCallers != null && compileCheckCallers.Count > 0) ? compileCheckCallers : null,
                CompileCheckTruncated = compileCheck && compileCheckTruncated
            };

            // Best-effort caller lookup for hint (only meaningful for single-object builds)
            if (targets.Count == 1)
            {
                var callers = TryGetDirectCallers(targets[0]);
                if (callers != null && callers.Count > 0)
                {
                    status.CallersToAlsoBuild = callers;
                    status.Hint = "After this build finishes, also build the caller(s) listed in 'callersToAlsoBuild' — generated DLLs of callers are NOT regenerated by an individual object build. You can pass them all to a single 'build' call as a comma-separated 'target' to run them in one MSBuild cycle (saves ~30s of OpenKB per object).";
                }
            }

            _tasks[taskId] = status;

            // issue #42 (P3c): register the in-flight build here — synchronously, before the
            // background task is scheduled — so a second Build() call cannot slip through the
            // "already running" guard during the window before RunBuild's own registration runs.
            if (status.KbPath == null) { try { status.KbPath = GetKBPath(); } catch { } }
            _inFlightBuilds[status.TaskId] = status;

            Task.Run(() => RunBuild(status, action, targets));

            string acceptedMessage;
            if (compileCheck)
            {
                acceptedMessage = $"compile_check started for {targets.Count} object(s) "
                    + (compileCheckGraphAvailable
                        ? $"(named objects + their transitive callers) — spec+gen+compile only, DeveloperMenu regeneration skipped. "
                        : "(named objects only — caller graph unavailable, run genexus_lifecycle action=index to check the full blast radius). ")
                    + "Poll action='status' target=<taskId> for progress.";
            }
            else
            {
                acceptedMessage = string.Equals(action, "BuildAll", StringComparison.OrdinalIgnoreCase)
                    ? "Build All started for the entire selected Knowledge Base. It will stop with ReorgRequired if reorganization is needed. Poll genexus_lifecycle action=status with target=<taskId> for progress."
                    : targets.Count > 1
                    ? $"Batch build started for {targets.Count} objects in a single KB-open cycle. Poll action='status' target=<taskId> for progress."
                    : "Build task started in background. Poll genexus_lifecycle action='status' with target=<taskId> for progress.";
            }

            return JsonConvert.SerializeObject(new {
                status = "Accepted",
                message = acceptedMessage,
                taskId = taskId,
                targets = targets.Count > 0 ? targets : null,
                compileCheck = compileCheck ? new {
                    callersAdded = status.CompileCheckCallers,
                    truncated = status.CompileCheckTruncated,
                    callerGraphAvailable = compileCheckGraphAvailable,
                    note = compileCheckTruncated
                        ? "Caller graph hit the buildPlanCap — some callers were not included. Raise buildPlanCap or check the omitted callers separately."
                        : (!compileCheckGraphAvailable
                            ? "Caller graph unavailable (index not built) — only the named objects were checked, not their callers."
                            : null)
                } : null,
                callersToAlsoBuild = status.CallersToAlsoBuild,
                hint = status.Hint,
                // Item 28 — EXPERIMENTAL. Surfaces decision outcome when fastIncremental=true.
                fastIncrementalFallback = status.FastIncrementalFallback ? (bool?)true : null,
                fallbackReason = status.FastIncrementalFallbackReason,
                fastIncremental = (fastIncremental && fiDecision != null && !fiDecision.ForceFullBuild)
                    ? new {
                        canSkipDeploy = fiDecision.CanSkipDeploy,
                        canSkipSpecify = fiDecision.CanSkipSpecify,
                        experimental = true
                    }
                    : null,
                _meta = plan != null ? new {
                    buildPlan = new {
                        requested = ParseTargets(target),
                        expanded = plan.Expanded,
                        includeCallees = plan.IncludeCallees,
                        cap = plan.NodeCap
                    }
                } : null
            });
        }

        private static List<string> ParseTargets(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return new List<string>();
            return target
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetOEMCP();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleOutputCP();

        private static Encoding _msbuildEncodingCached;
        private static Encoding ResolveMsbuildEncoding()
        {
            if (_msbuildEncodingCached != null) return _msbuildEncodingCached;
            try
            {
                // Worker runs detached from a console — Console.OutputEncoding falls back to
                // UTF-8 in that mode. MSBuild however emits bytes using the host's OEM /
                // console code page (CP850/CP1252 on PT-BR Windows, CP437 on en-US). Ask
                // Win32 directly so we don't trust the framework's stub: GetConsoleOutputCP
                // → live console CP, GetOEMCP → system-wide OEM CP. Either is right; OEM is
                // a stable fallback when no console is attached.
                uint cp = 0;
                try { cp = GetConsoleOutputCP(); } catch { }
                if (cp == 0 || cp == 65001) { try { cp = GetOEMCP(); } catch { } }
                if (cp != 0)
                {
                    // .NET Framework 4.8 ships all OEM/ANSI code pages natively, so no
                    // CodePagesEncodingProvider registration is required (that lives in the
                    // System.Text.Encoding.CodePages NuGet package, .NET 5+ only).
                    _msbuildEncodingCached = Encoding.GetEncoding((int)cp);
                }
                else
                {
                    _msbuildEncodingCached = Console.OutputEncoding ?? Encoding.UTF8;
                }
            }
            catch
            {
                _msbuildEncodingCached = Encoding.UTF8;
            }
            return _msbuildEncodingCached;
        }

        // Issue #27 item 1 follow-up: the most-recent terminal build outcome,
        // reachable WITHOUT a taskId/jobId. Surfaced in the compact lifecycle
        // status so an agent that lost the job id (or whose async poller wedged)
        // can still answer "did my last build pass?" from a plain status call.
        public static JObject GetLatestBuildSummary()
        {
            BuildTaskStatus latest = null;
            foreach (var t in _tasks.Values)
            {
                if (t == null || !IsTerminalStatus(t.Status)) continue;
                if (latest == null) { latest = t; continue; }
                // Order by end (fallback start) timestamp; ISO-8601 sorts lexicographically.
                string a = t.EndTime ?? t.StartTime ?? "";
                string b = latest.EndTime ?? latest.StartTime ?? "";
                if (string.CompareOrdinal(a, b) > 0) latest = t;
            }
            if (latest == null) return null;
            return new JObject
            {
                ["taskId"] = latest.TaskId,
                ["status"] = latest.Status,
                ["target"] = latest.Target,
                ["errorCount"] = latest.ErrorCount,
                ["warningCount"] = latest.WarningCount,
                ["elapsedSeconds"] = latest.ElapsedSeconds,
                ["endTime"] = latest.EndTime
            };
        }

        // issue #42 — non-terminal builds currently in flight. Drives P2b
        // (activeBuilds surfaced in lifecycle status → the client's isBusy view is
        // true during a background build) and P3c (reject a second concurrent build).
        internal static List<BuildTaskStatus> GetActiveBuilds(string kbPath = null)
        {
            var list = new List<BuildTaskStatus>();
            foreach (var t in _inFlightBuilds.Values)
            {
                if (t == null || IsTerminalStatus(t.Status)) continue;
                // A null filter matches every in-flight build (P2b lifecycle-status view).
                // A non-null filter scopes to one KB (P3c reject) so a build on another
                // KB does not block this one under a shared-worker mode; a build with no
                // recorded KbPath is treated as belonging to the current worker (matches).
                if (kbPath != null && t.KbPath != null
                    && !string.Equals(t.KbPath, kbPath, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(t);
            }
            return list;
        }

        // Compact JSON view of the active builds for the lifecycle status envelope.
        public static JArray GetActiveBuildsSummary()
        {
            var arr = new JArray();
            foreach (var t in GetActiveBuilds())
            {
                arr.Add(new JObject
                {
                    ["taskId"] = t.TaskId,
                    ["action"] = t.Action,
                    ["target"] = t.Target,
                    ["phase"] = t.Phase,
                    ["status"] = t.Status,
                    ["elapsedSeconds"] = t.StartedAt != default(DateTime)
                        ? (JToken)Math.Round((DateTime.UtcNow - t.StartedAt).TotalSeconds, 1)
                        : JValue.CreateNull()
                });
            }
            return arr;
        }

        public string GetStatus(string taskId, int page = 1, int pageSize = 50, bool compact = false)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                return JsonConvert.SerializeObject(new { tasks = _tasks.Values.OrderByDescending(t => t.StartTime).Take(10) });
            }

            if (_tasks.TryGetValue(taskId, out var status))
            {
                lock (status._lock)
                {
                    if (status.Status == "Running" && status.StartedAt != default(DateTime))
                        status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);

                    // Serialize the status without the full warnings list, then inject paginated warnings
                    var jo = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
                    StripOutputWhileRunning(jo, status.Status);

                    // FR#23: compact=true returns a sibling "warningsAggregated" grouped
                    // by spc/gen/CS/MSB code so the agent sees N occurrences of the same
                    // spc0022 collapsed to one row. The flat "warnings" array stays for
                    // backwards compat with existing consumers.
                    if (compact)
                    {
                        // Memoize the aggregation across polls — invalidate only
                        // when WarningCount changes.
                        if (status.AggregatedWarningsCache == null
                            || status.AggregateMemoForCount != status.WarningCount)
                        {
                            status.AggregatedWarningsCache = BuildOutputShaper.AggregateWarnings(status.Warnings);
                            status.AggregateMemoForCount = status.WarningCount;
                        }
                        jo["warningsAggregated"] = JArray.FromObject(status.AggregatedWarningsCache);
                    }

                    // Replace the flat warnings array with a paginated wrapper
                    var paginatedWarnings = BatchService.BuildStatusPayload(status.Warnings, page, pageSize);
                    jo["warnings"] = paginatedWarnings["warnings"];
                    jo["_meta"] = paginatedWarnings["_meta"];

                    return jo.ToString(Formatting.None);
                }
            }
            return "{\"status\":\"Error\",\"message\": \"Task ID not found\"}";
        }

        // v2.6.6 Stream F: event-driven status wait. Replaces the gateway's 25s
        // polling cap with a worker-side block that wakes on baseline change
        // (Phase / TargetsDone / ErrorCount / WarningCount / terminal Status).
        // Caller passes the previous snapshot string under `since`; the response
        // surfaces the new baseline under `_meta.snapshot` for chaining.
        // - waitSeconds clamped to [0, 600]; 0 = immediate (today's behaviour).
        // - Terminal task → returns immediately regardless of since.
        // - Unknown taskId → returns immediately (Task ID not found).
        // - Baseline mismatch → returns immediately.
        public string GetStatusWait(string taskId, int waitSeconds, string sinceBaseline, int page = 1, int pageSize = 50, bool compact = false)
        {
            if (waitSeconds < 0) waitSeconds = 0;
            if (waitSeconds > 600) waitSeconds = 600;

            // No taskId, or wait disabled → match legacy GetStatus shape.
            if (string.IsNullOrEmpty(taskId) || waitSeconds == 0)
            {
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            BuildTaskStatus status;
            if (!_tasks.TryGetValue(taskId, out status))
            {
                // Unknown taskId — GetStatus emits "Task ID not found"; return immediately.
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            string currentBaseline;
            bool terminal;
            lock (status._lock)
            {
                currentBaseline = status.ComputeBaseline();
                terminal = IsTerminalStatus(status.Status);
            }

            // Terminal → always return now. Baseline mismatch → caller is behind, return now.
            // Empty sinceBaseline means caller has no prior snapshot; surface current state.
            bool baselineDiffers = !string.IsNullOrEmpty(sinceBaseline)
                                   && !string.Equals(sinceBaseline, currentBaseline, StringComparison.Ordinal);
            if (terminal || baselineDiffers || string.IsNullOrEmpty(sinceBaseline))
            {
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            // Wait. ManualResetEventSlim was reset by a previous waiter or never set;
            // we reset BEFORE wait so an in-flight signal racing with us is honoured.
            // (We re-check the baseline after wait so a spurious wake is harmless.)
            try { status.StateChangeSignal.Reset(); } catch { }
            // Re-check baseline AFTER reset: HandleLine might have signalled and we'd
            // miss the edge otherwise.
            string postResetBaseline;
            lock (status._lock) { postResetBaseline = status.ComputeBaseline(); }
            if (!string.Equals(postResetBaseline, currentBaseline, StringComparison.Ordinal)
                || IsTerminalStatus(GetStatusValue(status)))
            {
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            try
            {
                status.StateChangeSignal.Wait(TimeSpan.FromSeconds(waitSeconds));
            }
            catch (ObjectDisposedException)
            {
                // Task pruned during wait — fall through to GetStatus (will report not found).
            }

            return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
        }

        private static string GetStatusValue(BuildTaskStatus s)
        {
            lock (s._lock) { return s.Status; }
        }

        // Adds _meta.snapshot=<current baseline> to a status JSON response so the
        // caller can chain status wait calls without recomputing the baseline.
        private string AnnotateWithBaseline(string statusJson, string taskId)
        {
            if (string.IsNullOrEmpty(statusJson) || string.IsNullOrEmpty(taskId)) return statusJson;
            if (!_tasks.TryGetValue(taskId, out var status)) return statusJson;
            try
            {
                var jo = JObject.Parse(statusJson);
                string baseline;
                lock (status._lock) { baseline = status.ComputeBaseline(); }
                var meta = jo["_meta"] as JObject;
                if (meta == null) { meta = new JObject(); jo["_meta"] = meta; }
                meta["snapshot"] = baseline;
                return jo.ToString(Formatting.None);
            }
            catch { return statusJson; }
        }

        // FR#19 (friction-report 2026-05-14): during long-poll each `status` call used to return
        // the full Output blob (often 200+ lines), repeated 3× per build. Drop it while Running
        // — TailLines already gives the live tail in a much smaller surface.
        private static void StripOutputWhileRunning(JObject jo, string status)
        {
            if (!string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)) return;
            jo.Remove("Output");
            jo.Remove("output");
        }

        public string GetResult(string taskId, int page = 1, int pageSize = 50)
        {
            if (string.IsNullOrEmpty(taskId))
                return "{\"status\":\"Error\",\"message\": \"taskId required\"}";

            if (_tasks.TryGetValue(taskId, out var status))
            {
                lock (status._lock)
                {
                    if (status.Status == "Running" && status.StartedAt != default(DateTime))
                        status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);

                    var jo = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
                    StripOutputWhileRunning(jo, status.Status);

                    // Replace the flat errors/items array with a paginated wrapper
                    var paginatedResult = BatchService.BuildResultPayload(status.Errors, page, pageSize);
                    jo["items"] = paginatedResult["items"];
                    jo["_meta"] = paginatedResult["_meta"];

                    return jo.ToString(Formatting.None);
                }
            }
            return "{\"status\":\"Error\",\"message\": \"Task ID not found\"}";
        }

        public string Cancel(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return "{\"status\":\"Error\",\"message\": \"taskId required\"}";
            // FR#7 (friction-report 2026-05-14): the old "Task ID not found" was ambiguous —
            // the operation might have completed and been pruned, or never existed, or
            // expired from the registry. Return a more specific message + hint so the LLM
            // does not silently lose state.
            if (!_tasks.TryGetValue(taskId, out var status))
                return JsonConvert.SerializeObject(new
                {
                    error = "Unknown build taskId",
                    taskId,
                    hint = "Task may have completed and been pruned, or the worker restarted. " +
                           "Call lifecycle action=status without a target to list recent tasks."
                });

            try
            {
                Process p;
                lock (status._lock)
                {
                    p = status.Process;
                    if (p == null || p.HasExited)
                        return JsonConvert.SerializeObject(new { status = status.Status, message = "Task already finished" });

                    // R9: write the cancelled state under the lock BEFORE we go kill
                    // children so the next status poll sees "Cancelled" immediately,
                    // even while the descendant kill is still in flight, and so a late
                    // HandleLine (still draining buffered MSBuild output) can't flip
                    // Phase back off "Done" after we've reported the task cancelled.
                    status.Status = "Cancelled";
                    status.Phase = "Done";
                    status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    try { status.StateChangeSignal.Set(); } catch { }
                }

                EmitPhaseProgress("Done");

                // Friction 2026-05-22: p.Kill() on net48 kills only the parent.
                // MSBuild spawns N child nodes (/m); orphaned children pile up
                // as zombies in tasklist after each cancel. Walk the tree and
                // kill all descendants before the parent — fire-and-forget so
                // the cancel RPC returns quickly (best-effort cleanup; the
                // caller has already been told the task is Cancelled).
                var pt = p;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { KillProcessTree(pt); }
                    catch (Exception ex) { Logger.Warn("[KILL-TREE-BG] " + ex.Message); }
                });
                return JsonConvert.SerializeObject(new { status = "Cancelled", taskId = taskId });
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // A7: an abort (genexus_lifecycle action=cancel on op:<id>) fans out a
        // Control:Cancel to the worker, but RunBuild has no CancellationToken to
        // observe, so its MSBuild.exe (and the /m child nodes) kept running and
        // piled up as orphans across sessions. Called from the Control:Cancel
        // handler: kill the process tree of every still-running build task so an
        // abort reaps its nodes the same way an explicit Cancel(taskId) or the
        // timeout watchdog already does. Returns how many builds were cancelled.
        public int CancelAllRunning()
        {
            int cancelled = 0;
            foreach (var status in _tasks.Values.ToArray())
            {
                try
                {
                    Process p;
                    lock (status._lock)
                    {
                        if (!string.Equals(status.Status, "Running", StringComparison.OrdinalIgnoreCase))
                            continue;
                        p = status.Process;
                        if (p == null || p.HasExited) continue;
                        status.Status = "Cancelled";
                        status.Phase = "Done";
                        status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        try { status.StateChangeSignal.Set(); } catch { }
                    }
                    var pt = p;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { KillProcessTree(pt); }
                        catch (Exception ex) { Logger.Warn("[KILL-TREE-BG] CancelAllRunning: " + ex.Message); }
                    });
                    cancelled++;
                }
                catch (Exception ex) { Logger.Warn("[CancelAllRunning] " + ex.Message); }
            }
            return cancelled;
        }

        private List<string> TryGetDirectCallers(string target)
        {
            if (string.IsNullOrEmpty(target) || _indexCacheService == null) return null;
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index == null || index.Objects == null) return null;

                SearchIndex.IndexEntry entry = null;
                if (target.Contains(":")) index.Objects.TryGetValue(target, out entry);
                if (entry == null)
                {
                    var key = index.Objects.Keys.FirstOrDefault(k => k.EndsWith(":" + target, StringComparison.OrdinalIgnoreCase));
                    if (key != null) index.Objects.TryGetValue(key, out entry);
                }
                if (entry == null || entry.CalledBy == null || entry.CalledBy.Count == 0) return null;
                return entry.CalledBy.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
            }
            catch { return null; }
        }

        // issue #37 items 2/3: build/preview could run unbounded (>9min observed) and
        // never terminalize, forcing the agent to poll "Running" forever. Wall-clock cap
        // (seconds) after which the task is force-failed and any spawned MSBuild.exe tree
        // is killed. Override with GXMCP_BUILD_TIMEOUT_SEC; full-KB builds get a larger
        // default (a full KB build is legitimately long). Clamped to [60, 7200].
        internal static int ResolveBuildTimeoutSeconds(string action)
        {
            bool fullKbBuild = action != null
                && (action.Equals("BuildAll", StringComparison.OrdinalIgnoreCase)
                    || action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase));
            int def = fullKbBuild ? 2400 : 900;
            var raw = Environment.GetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var v) && v > 0)
                def = v;
            if (def < 60) def = 60;
            if (def > 7200) def = 7200;
            return def;
        }

        // issue #42 — no-progress watchdog cap (seconds). Default 180s. 0 (or negative)
        // disables. Clamped to [30, 3600] when positive. Distinct from the wall-clock
        // GXMCP_BUILD_TIMEOUT_SEC: this fires when phase/counts stop moving, not at the
        // full cap.
        internal static int ResolveBuildNoProgressSeconds()
        {
            int def = 180;
            var raw = Environment.GetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var v))
            {
                if (v <= 0) return 0; // explicit disable
                def = v;
            }
            if (def < 30) def = 30;
            if (def > 3600) def = 3600;
            return def;
        }

        // issue #42 — actions that emit generated code (and therefore have a .cs the
        // evidence gate can verify). Reorg/Validate/Check don't generate object sources.
        private static bool IsCodeEmittingAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return false;
            return action.Equals("Build", StringComparison.OrdinalIgnoreCase)
                || action.Equals("BuildAll", StringComparison.OrdinalIgnoreCase)
                || action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase)
                || action.Equals("Sync", StringComparison.OrdinalIgnoreCase);
        }

        // Strip a "Type:Name" qualifier down to the bare object name the generator
        // uses for the file (<Name>.cs). Null/empty-safe.
        private static string BareName(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            t = t.Trim();
            return t.Contains(":") ? t.Substring(t.LastIndexOf(':') + 1).Trim() : t;
        }

        // Split a JS argument list on top-level commas, ignoring commas inside string
        // literals. Good enough for the generator's flat factory-call argument lists
        // (the caller only feeds it argument lists that contain no nested parentheses).
        internal static List<string> SplitJsArguments(string argList)
        {
            var args = new List<string>();
            if (argList == null) return args;
            var sb = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < argList.Length; i++)
            {
                char c = argList[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < argList.Length) { sb.Append(argList[++i]); continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
                if (c == ',') { args.Add(sb.ToString().Trim()); sb.Length = 0; continue; }
                sb.Append(c);
            }
            if (sb.Length > 0 || args.Count > 0) args.Add(sb.ToString().Trim());
            return args;
        }

        // True when the argument is the string literal "this" — the placeholder the
        // generator emits for a User Control whose control name it failed to resolve.
        private static bool IsThisPlaceholder(string arg)
        {
            if (string.IsNullOrEmpty(arg) || arg.Length < 3) return false;
            char q = arg[0];
            if ((q != '\'' && q != '"') || arg[arg.Length - 1] != q) return false;
            return string.Equals(arg.Substring(1, arg.Length - 2).Trim(), "this", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// issue #103 item 4. Detects a generated .js whose User Control came out degraded —
        /// the failure mode that renders &lt;span&gt;undefined&lt;/span&gt; at runtime and, once the
        /// screen is saved, writes the bound attribute back empty.
        ///
        /// The signature of a degraded instance, measured against IDE-generated baselines, is:
        ///   - the control-name argument of gx.uc.getNew(...) emitted as the literal string
        ///     "this" instead of the control's name (e.g. "Qv1"), and/or
        ///   - not a single setProp(...) binding left in the file.
        ///
        /// Deliberately NOT a per-instance "Gx Control Type" count: that property is emitted
        /// only sporadically by the generator (1 of 32 User Control objects in the reference
        /// KB carried it, IDE builds included), so requiring one per gx.uc.getNew made every
        /// healthy build report a degradation and buried the real one in the noise.
        /// </summary>
        internal static JObject DetectUserControlBindingDegradation(string objectName, string jsPath, string jsText)
        {
            if (string.IsNullOrEmpty(jsText)) return null;

            var factoryCalls = _userControlFactoryRegex.Matches(jsText);
            if (factoryCalls.Count == 0) return null;

            var placeholders = new JArray();
            foreach (Match m in factoryCalls)
            {
                var args = SplitJsArguments(m.Groups[1].Value);
                // parentObject, ucId, lastId, className, containerName, controlName, fieldName
                if (args.Count < 6) continue;
                if (!IsThisPlaceholder(args[5])) continue;
                placeholders.Add(args[3].Trim('\'', '"'));
            }

            int propertyBindings = _userControlPropertyBindingRegex.Matches(jsText).Count;
            bool noBindings = propertyBindings == 0;
            if (placeholders.Count == 0 && !noBindings) return null;

            string reason;
            if (placeholders.Count > 0 && noBindings)
                reason = "User Control JS lost every setProp binding and emitted \"this\" as the control name.";
            else if (placeholders.Count > 0)
                reason = "User Control JS emitted \"this\" as the control name instead of the control's name.";
            else
                reason = "User Control JS contains gx.uc.getNew instances but not a single setProp binding.";

            return new JObject
            {
                ["object"] = objectName,
                ["jsPath"] = jsPath,
                ["userControlInstances"] = factoryCalls.Count,
                ["propertyBindings"] = propertyBindings,
                ["placeholderControlNames"] = placeholders,
                ["reason"] = reason
            };
        }

        /// <summary>
        /// issue #42 build-evidence gate. After a terminal success for a code-emitting
        /// action, verify the targets that were expected to (re)generate actually have a
        /// FRESH generated .cs on disk (LastWriteTimeUtc >= build start). A GeneXus build
        /// can report Status=Succeeded while the .cs in the environment output dir
        /// (e.g. NETCoreMySQL\web) was never emitted — the reporter's core complaint.
        ///
        /// Populates status.GenerateEvidence and, when a gap is found, raises a
        /// "[generate-gap]" warning + hint so the outcome is no longer a clean success.
        /// Best-effort and non-fatal: any failure here leaves the build result untouched.
        /// </summary>
        private void AttachGenerateEvidence(BuildTaskStatus status, string action, List<string> targets)
        {
            if (status == null) return;
            bool succeeded = string.Equals(status.Status, "Succeeded", StringComparison.OrdinalIgnoreCase)
                             || (status.PartialSuccess == true);
            if (!succeeded) return;

            // Which targets do we expect to have regenerated / specified?
            var checkList = (targets != null && targets.Count > 0)
                ? targets.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : (status.DirtyAtStart ?? new List<string>());
            if (checkList.Count == 0) return;
            Logger.Info("[GENERATE-EVIDENCE] begin action=" + action + " targets=" + string.Join(";", checkList)
                + " specifyOnly=" + status.SpecifyOnly + " kb=" + GetKBPath());

            var unreachableSet = ParseUnreachableFromLog(status.FullLogPath);
            var notFoundSet = ParseNotFoundFromLog(status.FullLogPath);
            if (status.NotFoundTargets != null)
            {
                foreach (var nf in status.NotFoundTargets) notFoundSet.Add(nf);
            }
            if (status.UnreachableTargets != null)
            {
                foreach (var un in status.UnreachableTargets) unreachableSet.Add(un);
            }

            // issue #86: action=specify (specifyOnly) verification
            if (status.SpecifyOnly)
            {
                var specifyUnreachable = new JArray();
                var specifyNotFound = new JArray();
                var specifySpecified = new JArray();

                foreach (var t in checkList)
                {
                    string bare = BareName(t);
                    if (string.IsNullOrEmpty(bare)) continue;

                    if (notFoundSet.Contains(bare))
                    {
                        specifyNotFound.Add(new JObject
                        {
                            ["object"] = bare,
                            ["reason"] = "notFoundInKnowledgeBase"
                        });
                    }
                    else if (unreachableSet.Contains(bare))
                    {
                        specifyUnreachable.Add(new JObject
                        {
                            ["object"] = bare,
                            ["reason"] = "unreachable"
                        });
                    }
                    else
                    {
                        specifySpecified.Add(new JObject
                        {
                            ["object"] = bare
                        });
                    }
                }

                bool ok = specifyUnreachable.Count == 0 && specifyNotFound.Count == 0;
                var specifyEvidence = new JObject
                {
                    ["ok"] = ok,
                    ["objectsChecked"] = checkList.Count,
                    ["objectsSpecified"] = specifySpecified.Count,
                    ["specified"] = specifySpecified
                };

                if (specifyUnreachable.Count > 0) specifyEvidence["unreachable"] = specifyUnreachable;
                if (specifyNotFound.Count > 0) specifyEvidence["notFound"] = specifyNotFound;

                if (!ok)
                {
                    var gapNames = specifyUnreachable.Select(x => (string)x["object"])
                        .Concat(specifyNotFound.Select(x => (string)x["object"]))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    string joinedNames = string.Join(", ", gapNames);

                    specifyEvidence["note"] = "Specify reported success but " + gapNames.Count
                        + " object(s) were unreachable or not found in the Knowledge Base: " + joinedNames
                        + ". The object was not specified by GeneXus.";

                    lock (status._lock)
                    {
                        if (status.Warnings.Count < 50)
                            status.Warnings.Add("[specify-gap] Object unreachable or not found during specify: " + joinedNames);
                        status.WarningCount++;
                        if (string.IsNullOrEmpty(status.Hint))
                        {
                            status.Hint = "Specification gap: object " + joinedNames
                                + " was unreachable (spc0217) or not found. Give it a caller or set as Main object to specify. See generateEvidence.unreachable / generateEvidence.notFound.";
                        }
                    }
                }

                status.GenerateEvidence = specifyEvidence;
                return;
            }

            if (!IsCodeEmittingAction(action)) return;

            string kbPath = GetKBPath();
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath)) return;
            string activeEnvironmentWebPath = null;
            try { activeEnvironmentWebPath = _kbService?.GetActiveEnvironmentWebPath(); } catch { }
            // A running worker always has KbService injected. Keep the unscoped probe
            // only for direct unit/legacy callers that construct BuildService alone;
            // those callers have no SDK environment to resolve or accidentally cross.
            bool environmentResolutionRequired = _kbService != null;
            if (environmentResolutionRequired
                && (string.IsNullOrWhiteSpace(activeEnvironmentWebPath) || !Directory.Exists(activeEnvironmentWebPath)))
            {
                var unresolved = new JObject
                {
                    ["ok"] = false,
                    ["environmentScoped"] = false,
                    ["environmentUnresolved"] = true,
                    ["objectsChecked"] = checkList.Count,
                    ["staleOrMissing"] = new JArray(checkList.Select(t => new JObject
                    {
                        ["object"] = BareName(t),
                        ["reason"] = "activeEnvironmentOutputRootUnavailable"
                    })),
                    ["note"] = "Build completed without a uniquely resolved active-environment Web output root; generated-file evidence was not inferred from another environment."
                };
                lock (status._lock)
                {
                    status.GenerateEvidence = unresolved;
                    status.Status = "Failed";
                    status.ErrorCount++;
                    status.Warnings.Add("[generate-gap] active environment output root could not be resolved; build evidence is unavailable.");
                    status.WarningCount++;
                    if (string.IsNullOrEmpty(status.Hint))
                        status.Hint = "The build was not reported as a clean success because the active environment output root could not be resolved safely.";
                }
                return;
            }

            // issue #42 hardening (A) — set of objects that were dirty (edited via MCP
            // but not yet successfully built) when the build started. GeneXus generation
            // is incremental: an UNCHANGED object is not rewritten, so its .cs mtime
            // stays old. Flagging that as a gap is a false positive that erodes trust in
            // the signal. So a stale-but-present .cs only counts as a gap when the object
            // was actually dirty. A MISSING .cs is always a gap (an explicit build target
            // with no generated code at all is worth surfacing regardless of dirty state).
            var dirtySet = new HashSet<string>(
                (status.DirtyAtStart ?? new List<string>()).Select(BareName).Where(s => s != null),
                StringComparer.OrdinalIgnoreCase);
            var preMtimes = status.PreBuildMtimes;

            // issue #42 follow-up — a Procedure with no callers and not a main/entry
            // object is "unreachable": the GeneXus specifier emits spc0217 and skips
            // codegen on purpose. That is NOT a build failure and NOT a false positive
            // to fix by rebuilding — surfacing it as a generic "missing" gap with a
            // "rebuild with deploy=true" hint misleads the agent (a rebuild won't
            // generate an unreachable object). Parse the build log for spc0217 so those
            // objects land in their own bucket with an accurate reason/hint.
            // unreachableSet already parsed above

            var filesWritten = new JArray();
            var staleOrMissing = new JArray();
            var unreachable = new JArray();
            var upToDate = new JArray();
            int emittedCount = 0;
            foreach (var t in checkList)
            {
                string bare = BareName(t);
                if (string.IsNullOrEmpty(bare)) continue;
                DateTime? prior = null;
                if (preMtimes != null && preMtimes.TryGetValue(bare, out var pm)) prior = pm;
                GeneratedDiffService.GeneratedFileEvidence ev;
                try { ev = GeneratedDiffService.ProbeGeneratedFreshness(kbPath, bare, status.StartedAt, prior, activeEnvironmentWebPath); }
                catch { continue; }
                if (ev.Fresh)
                {
                    emittedCount++;
                    filesWritten.Add(new JObject
                    {
                        ["object"] = bare,
                        ["path"] = ev.FreshestPath,
                        ["writtenUtc"] = ev.FreshestWriteUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    });
                }
                else if (ev.Found && !dirtySet.Contains(bare))
                {
                    // .cs exists but wasn't rewritten, and the object wasn't edited this
                    // session → the incremental generator correctly skipped it. Not a gap.
                    upToDate.Add(new JObject
                    {
                        ["object"] = bare,
                        ["path"] = ev.FreshestPath,
                        ["lastWrittenUtc"] = ev.FreshestWriteUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    });
                }
                else if (!ev.Found && unreachableSet.Contains(bare))
                {
                    // No .cs, but the specifier said spc0217 unreachable — expected, not a gap.
                    unreachable.Add(new JObject
                    {
                        ["object"] = bare,
                        ["reason"] = "unreachable"
                    });
                }
                else
                {
                    staleOrMissing.Add(new JObject
                    {
                        ["object"] = bare,
                        ["reason"] = ev.Found ? "stale" : "missing",
                        ["path"] = ev.FreshestPath,
                        ["lastWrittenUtc"] = ev.FreshestWriteUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    });
                }
            }

            // P4 — referencedButNotBuilt: when the build did NOT expand to callees
            // (includeCallees=none), surface callees of the built targets that lack a
            // generated .cs, so the agent knows the callee still needs its own build.
            var referencedButNotBuilt = new JArray();
            bool calleesExcluded = string.Equals(status.BuildPlan?.IncludeCallees, "none", StringComparison.OrdinalIgnoreCase);
            if (calleesExcluded && _callerGraphService != null && targets != null && targets.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var checkSet = new HashSet<string>(
                    checkList.Select(x => x.Contains(":") ? x.Substring(x.LastIndexOf(':') + 1).Trim() : x),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var t in checkList)
                {
                    string bare = t.Contains(":") ? t.Substring(t.LastIndexOf(':') + 1).Trim() : t;
                    List<string> callees = null;
                    try { callees = _callerGraphService.GetCallees(bare); } catch { }
                    if (callees == null) continue;
                    foreach (var c in callees)
                    {
                        string cbare = c.Contains(":") ? c.Substring(c.LastIndexOf(':') + 1).Trim() : c;
                        if (string.IsNullOrEmpty(cbare) || !seen.Add(cbare)) continue;
                        if (checkSet.Contains(cbare)) continue; // already built
                        bool hasCs;
                        try
                        {
                            // Reuse the same active-environment filter as the primary
                            // freshness probe; a newer callee file in another environment
                            // must not make this build look complete.
                            hasCs = GeneratedDiffService.ProbeGeneratedFreshness(
                                kbPath, cbare, DateTime.MinValue, null, activeEnvironmentWebPath).Found;
                        }
                        catch { hasCs = true; }
                        if (!hasCs) referencedButNotBuilt.Add(cbare);
                    }
                }
            }

            var degradedUserControls = new JArray();
            try
            {
                // A target can be successfully checked without being regenerated.
                // Its existing JS still matters: otherwise a known degraded User
                // Control is silently reported as
                // a clean build whenever GeneXus says "up to date" (#103 item 4).
                var generatedEvidence = filesWritten
                    .Concat(upToDate)
                    .OfType<JObject>()
                    .GroupBy(x => x["object"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First());

                foreach (JObject item in generatedEvidence)
                {
                    string objName = item["object"]?.ToString();
                    string generatedPath = item["path"]?.ToString();
                    if (string.IsNullOrEmpty(objName) || string.IsNullOrEmpty(generatedPath)) continue;

                    string resolvedGeneratedPath = Path.IsPathRooted(generatedPath)
                        ? generatedPath
                        : Path.Combine(kbPath, generatedPath);
                    string jsPath;
                    if (string.Equals(Path.GetExtension(resolvedGeneratedPath), ".js", StringComparison.OrdinalIgnoreCase))
                    {
                        jsPath = resolvedGeneratedPath;
                    }
                    else
                    {
                        string dir = Path.GetDirectoryName(resolvedGeneratedPath);
                        jsPath = string.IsNullOrEmpty(dir)
                            ? null
                            : Path.Combine(dir, objName.ToLowerInvariant() + ".js");
                    }

                    // The .cs freshness gate above identifies the generated object. The
                    // companion JS can retain an older timestamp when GeneXus rewrites
                    // files through its generator cache, so do not discard a real partial
                    // binding signal solely because the JS mtime is not newer.
                    if (!string.IsNullOrEmpty(jsPath) && File.Exists(jsPath))
                    {
                        string jsText = File.ReadAllText(jsPath);
                        var degradation = DetectUserControlBindingDegradation(objName, jsPath, jsText);
                        if (degradation != null)
                        {
                            degradedUserControls.Add(degradation);
                        }
                    }
                }
            }
            catch { }

            var evidence = new JObject
            {
                ["ok"] = staleOrMissing.Count == 0 && degradedUserControls.Count == 0,
                ["objectsChecked"] = checkList.Count,
                ["objectsBuilt"] = emittedCount,
                ["filesWritten"] = filesWritten,
                ["staleOrMissing"] = staleOrMissing
            };
            if (emittedCount == 0 && staleOrMissing.Count == 0)
            {
                evidence["upToDate"] = true;
                lock (status._lock)
                {
                    status.UpToDate = true;
                }
            }
            if (degradedUserControls.Count > 0)
            {
                evidence["degradedUserControls"] = degradedUserControls;
                lock (status._lock)
                {
                    var names = string.Join(", ", degradedUserControls.Select(x => (string)x["object"]));
                    status.Warnings.Add("[user-control-degraded] User Control JS generated without property bindings for: " + names);
                    status.WarningCount++;
                    if (string.IsNullOrEmpty(status.Hint))
                        status.Hint = "User Control degradation detected: generated JS for " + names + " lost its setProp bindings "
                            + "(the control renders as <span>undefined</span> and saving the screen writes the bound attribute back empty). "
                            + "Regenerate the object from the IDE and compare the setProp list against a known-good build.";
                }
            }
            if (upToDate.Count > 0)
                evidence["upToDate"] = upToDate;
            if (unreachable.Count > 0)
                evidence["unreachable"] = unreachable;
            if (referencedButNotBuilt.Count > 0)
                evidence["referencedButNotBuilt"] = referencedButNotBuilt;

            if (staleOrMissing.Count > 0)
            {
                evidence["note"] = "Build reported success but " + staleOrMissing.Count
                    + " object(s) expected to regenerate have no fresh generated .cs on disk. "
                    + "The KB object may be correct while its generated code in the environment output dir was not emitted/updated.";
                lock (status._lock)
                {
                    // Raise a warning so warningCount>0 (a clean success has 0/0) and the
                    // gateway can render effective_status=SucceededWithGaps.
                    var names = string.Join(", ", staleOrMissing.Select(x => (string)x["object"]));
                    if (status.Warnings.Count < 50)
                        status.Warnings.Add("[generate-gap] No fresh generated .cs after a successful build for: " + names);
                    status.WarningCount++;
                    if (string.IsNullOrEmpty(status.Hint))
                        status.Hint = "Generation gap: the build succeeded but the generated .cs for "
                            + names + " was not (re)written. Rebuild with a full deploy (deploy=true), verify the "
                            + "environment output directory, or run genexus_lifecycle action=rebuild. See generateEvidence.staleOrMissing.";
                }
            }
            else if (unreachable.Count > 0)
            {
                var names = string.Join(", ", unreachable.Select(x => (string)x["object"]));
                evidence["note"] = unreachable.Count + " object(s) are unreachable (spc0217): "
                    + names + ". A Procedure with no callers that is not a main/entry object is not "
                    + "code-generated by GeneXus — this is expected, not a build failure. Give it a "
                    + "caller, set it as a main/exposed object, or ignore if intentional. Rebuilding will NOT emit a .cs.";
            }
            else if (referencedButNotBuilt.Count > 0)
            {
                evidence["note"] = referencedButNotBuilt.Count + " referenced object(s) have no generated .cs and were not part of this build (includeCallees=none).";
            }

            status.GenerateEvidence = evidence;
            Logger.Info("[GENERATE-EVIDENCE] complete action=" + action + " objects=" + checkList.Count
                + " emitted=" + emittedCount + " stale=" + staleOrMissing.Count
                + " webRoot=" + (activeEnvironmentWebPath ?? "<none>"));
        }

        // Parse a build log for spc0217 ("Object is unreachable") diagnostics and return
        // the bare object names they reference. Each spc0217 line carries a SourcePosition
        // XML blob with <FullName>Procedure 'Name'</FullName>; we pull the quoted name (or
        // fall back to the last token). Never throws — a missing/unreadable log yields an
        // empty set so the caller behaves exactly as before.
        private static HashSet<string> ParseUnreachableFromLog(string logPath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(logPath)) return set;
            try
            {
                if (!File.Exists(logPath)) return set;
                // Build-one logs are small; cap the read so a huge RebuildAll log can't
                // blow up memory (spc0217 lines, if any, appear early in Specification).
                var info = new FileInfo(logPath);
                if (info.Length > 8 * 1024 * 1024) return set;
                foreach (var line in File.ReadLines(logPath))
                {
                    if (line.IndexOf("spc0217", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int a = line.IndexOf("<FullName>", StringComparison.OrdinalIgnoreCase);
                    if (a >= 0)
                    {
                        a += "<FullName>".Length;
                        int b = line.IndexOf("</FullName>", a, StringComparison.OrdinalIgnoreCase);
                        if (b >= 0)
                        {
                            string full = line.Substring(a, b - a).Trim();
                            if (full.Length > 0)
                            {
                                string name;
                                int q1 = full.IndexOf('\'');
                                int q2 = q1 >= 0 ? full.IndexOf('\'', q1 + 1) : -1;
                                if (q1 >= 0 && q2 > q1)
                                    name = full.Substring(q1 + 1, q2 - q1 - 1).Trim();     // Procedure 'Foo' → Foo
                                else
                                {
                                    var parts = full.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                    name = parts.Length > 0 ? parts[parts.Length - 1] : full;
                                }
                                if (!string.IsNullOrEmpty(name)) set.Add(name);
                                continue;
                            }
                        }
                    }
                    int quote1 = line.IndexOf('\'');
                    int quote2 = quote1 >= 0 ? line.IndexOf('\'', quote1 + 1) : -1;
                    if (quote1 >= 0 && quote2 > quote1)
                    {
                        string name = line.Substring(quote1 + 1, quote2 - quote1 - 1).Trim();
                        if (!string.IsNullOrEmpty(name)) set.Add(name);
                    }
                }
            }
            catch { /* best-effort — empty set on any error */ }
            return set;
        }

        private static HashSet<string> ParseNotFoundFromLog(string logPath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(logPath)) return set;
            try
            {
                if (!File.Exists(logPath)) return set;
                var info = new FileInfo(logPath);
                if (info.Length > 8 * 1024 * 1024) return set;
                foreach (var line in File.ReadLines(logPath))
                {
                    var m = _rxObjectNotFoundWarning.Match(line);
                    if (m.Success)
                    {
                        string obj = m.Groups["obj"].Value;
                        if (!string.IsNullOrEmpty(obj)) set.Add(obj);
                    }
                }
            }
            catch { }
            return set;
        }

        internal static string BuildExternalProjectXml(string importPath, string kbPath, string action, IList<string> targets, BuildTaskStatus status = null)
        {
            importPath = SecurityElement.Escape(importPath ?? string.Empty);
            string kbPathEsc = SecurityElement.Escape(kbPath ?? string.Empty);
            bool buildAll = string.Equals(action, "BuildAll", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            // issue #37 item 1: pin the 4.0 toolset so GeneXus tasks resolve under
            // the .NET Framework MSBuild used by the Worker.
            sb.AppendLine("<Project DefaultTargets=\"Execute\" ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
            sb.AppendLine("  <Import Project=\"" + importPath + "\" />");
            sb.AppendLine("  <Target Name=\"Execute\">");
            sb.AppendLine("    <OpenKnowledgeBase Directory=\"" + kbPathEsc + "\" Output=\"IDE\" />");

            if (buildAll)
            {
                sb.AppendLine("    <Message Importance=\"High\" Text=\"[GXMCP-BUILD-ALL] KB opened\" />");
                sb.AppendLine("    <Message Importance=\"High\" Text=\"[GXMCP-BUILD-ALL] BuildAll started\" />");
                sb.AppendLine("    <BuildAll ForceRebuild=\"false\" CompileMains=\"true\" FailIfReorg=\"true\" DoNotExecuteReorg=\"true\" DetailedNavigation=\"false\" Output=\"IDE\" EventsSuspended=\"true\" />");
                sb.AppendLine("    <Message Importance=\"High\" Text=\"[GXMCP-BUILD-ALL] BuildAll completed\" />");
            }
            else if (targets != null && targets.Count > 0 &&
                (string.Equals(action, "Build", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action, "Rebuild", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action, "RebuildAll", StringComparison.OrdinalIgnoreCase)))
            {
                if (status != null)
                {
                    status.TargetsTotal = targets.Count;
                    status.TargetsDone = 0;
                }
                string joined = string.Join(";", targets.Select(t => SecurityElement.Escape(t ?? string.Empty)));
                sb.AppendLine("    <SpecifyOneOnly ObjectNames=\"" + joined + "\" />");
                bool force = string.Equals(action, "RebuildAll", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "Rebuild", StringComparison.OrdinalIgnoreCase);
                sb.AppendLine("    <IdeWebBuildAndDeploy ForceRebuild=\"" + (force ? "true" : "false") + "\" CompileMains=\"true\" Output=\"IDE\" EventsSuspended=\"true\" />");
            }
            else if (string.Equals(action, "RebuildAll", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(action, "Rebuild", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("    <IdeWebBuildAndDeploy ForceRebuild=\"true\" CompileMains=\"true\" Output=\"IDE\" EventsSuspended=\"true\" />");
            }
            else if (string.Equals(action, "Reorg", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("    <CheckAndInstallDatabase />");
            else if (string.Equals(action, "Validate", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(action, "Check", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("    <CheckKnowledgeBase />");
            else
                sb.AppendLine("    <IdeWebBuildAndDeploy ForceRebuild=\"false\" CompileMains=\"true\" Output=\"IDE\" EventsSuspended=\"true\" />");

            sb.AppendLine("    <CloseKnowledgeBase />");
            if (buildAll)
            {
                // OnError must be the last task in the target. If BuildAll fails
                // (especially on ReorgRequired), the normal close is skipped and
                // this handler releases the SDK KB before MSBuild exits.
                sb.AppendLine("    <OnError ExecuteTargets=\"CloseOnBuildAllError\" />");
            }
            sb.AppendLine("  </Target>");
            if (buildAll)
                sb.AppendLine("  <Target Name=\"CloseOnBuildAllError\"><CloseKnowledgeBase /></Target>");
            sb.AppendLine("</Project>");
            return sb.ToString();
        }

        internal static string BuildMsBuildArguments(string action, string projectPath)
        {
            string msbuildParallelism = string.Equals(action, "BuildAll", StringComparison.OrdinalIgnoreCase)
                ? "/m:1"
                : "/m";
            return "/nologo " + msbuildParallelism + " /v:n /nodeReuse:false /target:Execute \"" + projectPath + "\"";
        }

        private void RunBuild(BuildTaskStatus status, string action, List<string> targets)
        {
            string tempFile = null;
            // Plan 012: captured PID/start-time survive the using-block's Dispose() of
            // `process`, so the finally-block reap can identify the same OS process
            // without dereferencing the already-disposed Process instance.
            int reapPid = 0;
            DateTime reapStart = DateTime.MinValue;
            // FR#24: thread-tag the Logger so worker_debug.log carries [phase=...]
            // on every line emitted from this build (and only this build).
            Helpers.Logger.CurrentPhase = "Starting";
            // issue #37 items 2/3: wall-clock watchdog. Terminalizes the task (and kills any
            // external MSBuild tree) if it exceeds the cap, so a wedged SDK build/deploy step
            // doesn't leave the status stuck at "Running". The underlying thread may still be
            // blocked inside the SDK, but the agent gets a terminal Failed/TimedOut it can act on.
            int timeoutSec = ResolveBuildTimeoutSeconds(action);
            System.Threading.Timer watchdog = null;
            System.Threading.Timer noProgressWatchdog = null;
            System.Threading.Timer buildHeartbeat = null;
            // issue #42 (P2b/P3c) — mark this build in-flight for the whole RunBuild body.
            if (status?.TaskId != null)
            {
                if (status.KbPath == null) { try { status.KbPath = GetKBPath(); } catch { } }
                _inFlightBuilds[status.TaskId] = status;
            }
            try
            {
                // issue #42 (P3a) — emit a build-active heartbeat so the gateway keeps
                // the worker alive during a long background build. A background build
                // is NOT an in-flight RPC, so without this the gateway's idle-reap /
                // heap-recycle timer could kill the worker mid-build. The gateway bumps
                // _lastActivityUtc on each notification (see HandleWorkerRpcResponse).
                buildHeartbeat = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (IsTerminalStatus(status.Status)) return;
                        Program.SendNotification("notifications/worker/build_active",
                            new { taskId = status.TaskId, phase = status.Phase, action = status.Action });
                    }
                    catch { }
                }, null, 5000, 20000);
                watchdog = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (IsTerminalStatus(status.Status)) return;
                        Logger.Warn("[BUILD-TIMEOUT] taskId=" + status.TaskId + " exceeded " + timeoutSec
                                    + "s (phase=" + status.Phase + ") — force-failing and killing any MSBuild tree.");
                        try { var p = status.Process; if (p != null) KillProcessTree(p); } catch { }
                        lock (status._lock)
                        {
                            if (IsTerminalStatus(status.Status)) return;
                            status.Status = "Failed";
                            status.Phase = "Done";
                            status.Error = "Build timed out after " + timeoutSec + "s at phase '" + (status.Phase ?? "?")
                                + "' and was terminated. If this was a full deploy/reorg step (WebAppConfig, CheckAndInstallDatabase), it may still be running in the SDK; check the KB in the IDE. Raise the cap with GXMCP_BUILD_TIMEOUT_SEC if the KB legitimately needs longer.";
                            status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                            status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);
                        }
                        MaybeNotifyOnFailure(status);
                        try { status.StateChangeSignal.Set(); } catch { }
                    }
                    catch (Exception ex) { Logger.Warn("[BUILD-TIMEOUT] watchdog threw: " + ex.Message); }
                }, null, timeoutSec * 1000, System.Threading.Timeout.Infinite);

                // issue #42 — no-progress watchdog. The wall-clock cap above only
                // fires after the FULL timeout (900s/2400s); a build that wedges early
                // (phase + counts frozen) would otherwise sit "Running" for the whole
                // cap. This lighter timer force-fails once no observable progress
                // (phase / error / warning / targetsDone) has been seen for
                // noProgressSec. Disabled when noProgressSec <= 0.
                int noProgressSec = ResolveBuildNoProgressSeconds();
                if (noProgressSec > 0)
                {
                    string lastBaseline = status.ComputeBaseline();
                    DateTime lastProgressUtc = DateTime.UtcNow;
                    int tickMs = Math.Max(5000, Math.Min(30000, noProgressSec * 1000 / 4));
                    noProgressWatchdog = new System.Threading.Timer(_ =>
                    {
                        try
                        {
                            if (IsTerminalStatus(status.Status)) return;
                            string cur = status.ComputeBaseline();
                            if (!string.Equals(cur, lastBaseline, StringComparison.Ordinal))
                            {
                                lastBaseline = cur;
                                lastProgressUtc = DateTime.UtcNow;
                                return;
                            }
                            if ((DateTime.UtcNow - lastProgressUtc).TotalSeconds < noProgressSec) return;
                            Logger.Warn("[BUILD-NOPROGRESS] taskId=" + status.TaskId + " no progress for "
                                        + noProgressSec + "s (phase=" + status.Phase + ") — force-failing.");
                            try { var p = status.Process; if (p != null) KillProcessTree(p); } catch { }
                            lock (status._lock)
                            {
                                if (IsTerminalStatus(status.Status)) return;
                                status.Status = "Failed";
                                status.Phase = "Done";
                                status.Error = "Build made no observable progress for " + noProgressSec + "s at phase '"
                                    + (status.Phase ?? "?") + "' and was terminated (no-progress watchdog). The SDK build step "
                                    + "may be wedged; check the KB in the IDE. Tune with GXMCP_BUILD_NOPROGRESS_SEC (0 disables).";
                                status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);
                            }
                            MaybeNotifyOnFailure(status);
                            try { status.StateChangeSignal.Set(); } catch { }
                        }
                        catch (Exception ex) { Logger.Warn("[BUILD-NOPROGRESS] watchdog threw: " + ex.Message); }
                    }, null, tickMs, tickMs);
                }
                if (_kbService != null)
                {
                    int waits = 0;
                    while (_kbService.IsInitializing && waits < 15) { System.Threading.Thread.Sleep(1000); waits++; }
                }

                string kbPath = GetKBPath();
                if (string.IsNullOrEmpty(kbPath))
                {
                    SetFailure(status, "KB Path not found in Environment (GX_KB_PATH)");
                    return;
                }

                // issue #42 — snapshot the dirty set BEFORE the pipeline runs, since
                // InProcessBuildRunner calls EditDirtyTracker.MarkClean as it builds.
                // The evidence gate in the finally uses this to know which targets were
                // expected to regenerate their .cs.
                try { status.DirtyAtStart = EditDirtyTracker.GetDirty(kbPath); } catch { status.DirtyAtStart = null; }

                // issue #42 hardening (C) — snapshot the current freshest .cs mtime for
                // each target we'll later gate, BEFORE the generator can touch it. Cheap
                // best-effort; failure just falls the gate back to wall-clock comparison.
                try
                {
                    if (IsCodeEmittingAction(action) && !status.SpecifyOnly)
                    {
                        var gateList = (targets != null && targets.Count > 0)
                            ? targets.Where(t => !string.IsNullOrWhiteSpace(t)).Select(BareName).Distinct(StringComparer.OrdinalIgnoreCase)
                            : (status.DirtyAtStart ?? new List<string>()).Select(BareName).Distinct(StringComparer.OrdinalIgnoreCase);
                        var snap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                        string activeEnvironmentWebPath = null;
                        try { activeEnvironmentWebPath = _kbService?.GetActiveEnvironmentWebPath(); } catch { }
                        foreach (var bare in gateList)
                        {
                            if (string.IsNullOrEmpty(bare)) continue;
                            var ev = GeneratedDiffService.ProbeGeneratedFreshness(kbPath, bare, DateTime.MinValue, null, activeEnvironmentWebPath);
                            if (ev.Found && ev.FreshestWriteUtc != null) snap[bare] = ev.FreshestWriteUtc.Value;
                        }
                        status.PreBuildMtimes = snap;
                    }
                }
                catch { status.PreBuildMtimes = null; }

                // v2.6.6 Stream D — in-process build path. Reuse the already-open
                // KbService._kb instance + invoke GeneXus MSBuild tasks directly
                // instead of spawning MSBuild.exe (which re-opens the KB out of
                // process, the dominant cost in targeted builds).
                //
                // 2026-05-21 LIVE-TEST FINDING + FIX: ArtechTask's static ctor
                // activates the GxServiceManager process-singleton and throws
                // `GxException: O Service Manager já foi ativado` if another
                // path (KbService.OpenKB → InitializeSdk) activated SM first.
                // Worker boot now warms the ArtechTask cctor BEFORE
                // InitializeSdk (Program.TryWarmupArtechTaskCctor) so the IDE
                // ordering holds and subsequent in-process builds succeed.
                // ON by default; opt-out with GXMCP_INPROCESS_BUILD=0.
                bool useInProcess =
                    !string.Equals(Environment.GetEnvironmentVariable("GXMCP_INPROCESS_BUILD"), "0", StringComparison.OrdinalIgnoreCase)
                    && _kbService != null && _kbService.IsOpen;
                if (useInProcess)
                {
                    status.Phase = "InProcess-Specifying";
                    EmitPhaseProgress(status.Phase);
                    Logger.Info("[BUILD-INPROCESS] taskId=" + status.TaskId
                                + " kb=" + _kbService.GetKbPath()
                                + " targets=" + (targets != null ? string.Join(";", targets) : "<all>"));
                    var sw = Stopwatch.StartNew();
                    InProcessBuildOutcome outcome = InProcessBuildOutcome.CouldNotRun;
                    try
                    {
                        outcome = InProcessBuildRunner.Run(
                            status, action, targets,
                            (s, l, err) => HandleLine(s, l, err),
                            _kbService.KbObject, _kbService.KbLock,
                            skipFullDeploy: status.SkipFullDeploy,
                            kbPath: _kbService.GetKbPath(),
                            specifyOnly: status.SpecifyOnly,
                            fullDeploy: status.FullDeploy);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("[BUILD-INPROCESS] orchestrator threw: " + ex.Message);
                        outcome = InProcessBuildOutcome.CouldNotRun;
                    }
                    sw.Stop();
                    Logger.Info("[BUILD-INPROCESS-DONE] taskId=" + status.TaskId
                                + " outcome=" + outcome + " elapsedMs=" + sw.ElapsedMilliseconds);
                    // The in-process pipeline actually ran — either it succeeded, or it
                    // failed with diagnostics. In both cases we terminalize from the
                    // captured output. A full MSBuild.exe rebuild would only reproduce a
                    // failure at many times the wall-clock (the "build never returns" the
                    // reporter saw), so it is NOT attempted here. The external fallback is
                    // reserved for outcome == CouldNotRun below.
                    if (outcome == InProcessBuildOutcome.Succeeded
                        || outcome == InProcessBuildOutcome.FailedWithDiagnostics)
                    {
                        status.BuildPath = "inproc";
                        // FR#22: still emit shaped output envelope from FullOutput.
                        string fullText = status.FullOutput.ToString();
                        try
                        {
                            string fullLogPath;
                            BuildOutputShaper.TryWriteFullLog(fullText, status.TaskId, out fullLogPath);
                            status.FullLogPath = fullLogPath;
                            status.Output = BuildOutputShaper.Shape(fullText, status.LineCount, fullLogPath);
                        }
                        catch { }

                        bool failed = outcome == InProcessBuildOutcome.FailedWithDiagnostics
                                      || status.ErrorCount > 0;
                        status.Status = failed ? "Failed" : "Succeeded";
                        FinalizeBuildAllStatus(status, fullText);
                        // A1 (parity with the MSBuild.exe branch below): when the
                        // in-process pipeline reports failure but emitted zero code
                        // errors AND the captured output shows Generation + Compilation
                        // both succeeded, the failure is a downstream/late step
                        // (WebAppConfig, a standalone-module deploy like GAMUser) —
                        // the target's .cs/.dll are already written. Flag it as a
                        // partial success so the gateway renders effective_status=
                        // PartialSuccess (isError=false) instead of a contradictory
                        // "Failed with 0 errors".
                        if (failed && status.ErrorCount == 0
                            && DidGenerationAndCompilationSucceed(fullText))
                        {
                            status.PartialSuccess = true;
                        }
                        status.Phase = "Done";
                        EmitPhaseProgress(status.Phase);

                        // When the pipeline reported failure but emitted no itemized
                        // error line (the build-all IdeWebBuildAndDeploy case — the SDK
                        // signals failure through >E0 section markers, not "error CS####:"
                        // text), leave the agent something actionable: the failed section
                        // and a pointer to the per-object spec check that DOES itemize.
                        if (outcome == InProcessBuildOutcome.FailedWithDiagnostics && status.ErrorCount == 0)
                        {
                            if (status.PhaseFailure == null)
                                status.PhaseFailure = ExtractPhaseFailure(fullText)
                                    ?? new PhaseFailureInfo
                                    {
                                        Name = status.Phase ?? "Build",
                                        Message = "The in-process GeneXus build reported failure without an itemized error line."
                                    };
                            status.Hint = "Build failed in-process with no itemized error list (the SDK signalled failure at the section level). "
                                + "Run genexus_lifecycle action=specify target=<object> for spc*/gen* diagnostics on a specific object, or open the KB in the IDE to see the full build output.";
                        }

                        status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);
                        // Attach the evidence before publishing the terminal signal. The
                        // gateway's async poller stops at the first Succeeded status; if the
                        // signal fires first, it can persist a terminal result without the
                        // evidence that is added in RunBuild's finally block (#103).
                        try { AttachGenerateEvidence(status, action, targets); }
                        catch (Exception ex) { Logger.Warn("[GENERATE-EVIDENCE] gate threw: " + ex.Message); }
                        Logger.Info("Background Build " + status.TaskId + " " + status.Status
                                    + " (inproc, errors=" + status.ErrorCount + ", warnings=" + status.WarningCount
                                    + ", " + status.ElapsedSeconds + "s)");
                        // Wake any event-driven status wait callers on the terminal edge.
                        try { status.StateChangeSignal.Set(); } catch { }
                        // Item 72 (friction 2026-05-22) — POST webhook on terminal Failed (not PartialSuccess).
                        MaybeNotifyOnFailure(status);
                        return;
                    }
                    // outcome == CouldNotRun — the in-process path never executed a build.
                    // issue #28 item 12: never fall back to a full MSBuild.exe spawn for a
                    // spec-check request — that would compile + deploy, the opposite of what
                    // specifyOnly asked for. Report spec-unavailable instead.
                    if (status.SpecifyOnly)
                    {
                        status.Status = "Failed";
                        status.Phase = "Done";
                        EmitPhaseProgress(status.Phase);
                        status.Error = "Spec-check (specifyOnly) could not run in-process (GeneXus MSBuild tasks unavailable in this worker). Not falling back to a full build. Run a normal build to see diagnostics.";
                        status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);
                        try { status.StateChangeSignal.Set(); } catch { }
                        return;
                    }
                    Logger.Warn("[BUILD-INPROCESS-FALLBACK] taskId=" + status.TaskId + " falling back to MSBuild.exe spawn");
                }

                status.BuildPath = "msbuild-exe";

                tempFile = Path.Combine(Path.GetTempPath(), "GxBuild_" + Guid.NewGuid().ToString().Substring(0, 8) + ".msbuild");
                string projectXml = BuildExternalProjectXml(
                    Path.Combine(_gxDir, "Genexus.Tasks.targets"), kbPath, action, targets, status);
                File.WriteAllText(tempFile, projectXml);

                // Use /v:n (normal) so we get per-object progress lines, not /v:q.
                // Build All stays single-node so cancellation and evidence capture do
                // not depend on implicit MSBuild worker-node interleaving.
                var psi = new ProcessStartInfo
                {
                    FileName = _msbuildPath,
                    Arguments = BuildMsBuildArguments(action, tempFile),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _gxDir,
                    // MSBuild on Windows writes via the system OEM/ANSI code page (PT-BR usually
                    // 850/1252). Reading the streams as UTF-8 mangles accented characters
                    // ("Compila��o", "n�", etc.) which is unreadable for LLM consumers. Pin
                    // both streams to the console's actual output encoding so TailLines/Output
                    // stay legible. Fall back to UTF-8 only when CodePagesEncodingProvider
                    // isn't registered.
                    StandardOutputEncoding = ResolveMsbuildEncoding(),
                    StandardErrorEncoding = ResolveMsbuildEncoding()
                };

                if (!string.IsNullOrEmpty(_gxDir))
                {
                    try
                    {
                        psi.EnvironmentVariables["GX_PATH"] = _gxDir;
                        psi.EnvironmentVariables["GX_PROGRAM_DIR"] = _gxDir;
                        psi.EnvironmentVariables["GeneXusPath"] = _gxDir;
                    }
                    catch { }
                }

                using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    status.Process = process;
                    process.OutputDataReceived += (s, e) => HandleLine(status, e.Data, false);
                    process.ErrorDataReceived  += (s, e) => HandleLine(status, e.Data, true);

                    process.Start();
                    reapPid = process.Id;
                    try { reapStart = process.StartTime; } catch { reapStart = DateTime.MinValue; }
                    status.Phase = "OpeningKB";
                    EmitPhaseProgress(status.Phase);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    // issue #37 items 2/3: bound the wait so a wedged MSBuild step doesn't
                    // block this thread forever. The watchdog also fires at the same cap;
                    // killing here lets us record ExitCode and terminalize cleanly.
                    if (!process.WaitForExit(timeoutSec * 1000))
                    {
                        Logger.Warn("[BUILD-TIMEOUT] taskId=" + status.TaskId + " MSBuild.exe exceeded "
                                    + timeoutSec + "s — killing process tree.");
                        KillProcessTree(process);
                        try { process.WaitForExit(5000); } catch { }
                    }

                    status.ExitCode = process.HasExited ? process.ExitCode : -1;
                    status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);

                    // FR#22 (v2.6.6 Stream C): full log → disk, shaped envelope → status.
                    string fullText = status.FullOutput.ToString();
                    string fullLogPath;
                    BuildOutputShaper.TryWriteFullLog(fullText, status.TaskId, out fullLogPath);
                    status.FullLogPath = fullLogPath;
                    status.Output = BuildOutputShaper.Shape(fullText, status.LineCount, fullLogPath);

                    status.Phase = "Done";
                    Helpers.Logger.CurrentPhase = "Done";
                    EmitPhaseProgress(status.Phase);

                    // Don't clobber a terminal state the watchdog already set on timeout.
                    if (!IsTerminalStatus(status.Status))
                    {
                        if (status.ExitCode == 0 && status.ErrorCount == 0)
                            status.Status = "Succeeded";
                        else
                            status.Status = "Failed";
                    }
                    FinalizeBuildAllStatus(status, fullText);

                    // Friction 2026-05-22: when ErrorCount==0 and ExitCode!=0, the
                    // failure is a late MSBuild step (WebAppConfig, deploy task,
                    // file-missing) that doesn't emit a proper "error <code>:" line.
                    // Parse the raw output for >RO/>E0 markers so the agent gets a
                    // named phase_failure instead of "Failed: 0 errors, 0 warnings".
                    if (status.ErrorCount == 0 && status.ExitCode != 0)
                    {
                        status.PhaseFailure = ExtractPhaseFailure(fullText);
                        if (DidGenerationAndCompilationSucceed(fullText))
                        {
                            status.PartialSuccess = true;
                        }
                    }

                    // Publish GenerateEvidence before the terminal signal. The gateway's
                    // async poller stops at the first terminal status and otherwise races
                    // the finally block below, losing the evidence in the stored result.
                    try { AttachGenerateEvidence(status, action, targets); }
                    catch (Exception ex) { Logger.Warn("[GENERATE-EVIDENCE] gate threw: " + ex.Message); }

                    // Stream F: wake any pending status wait callers.
                    try { status.StateChangeSignal.Set(); } catch { }

                    Logger.Info("Background Build " + status.TaskId + " " + status.Status +
                                " (errors=" + status.ErrorCount + ", warnings=" + status.WarningCount +
                                ", " + status.ElapsedSeconds + "s)");
                    // Item 72 (friction 2026-05-22) — POST webhook on terminal Failed (not PartialSuccess).
                    MaybeNotifyOnFailure(status);
                }
            }
            catch (Exception ex)
            {
                SetFailure(status, ex.Message);
                Logger.Error("Background Build " + status.TaskId + " EXCEPTION: " + ex.Message);
            }
            finally
            {
                try { watchdog?.Dispose(); } catch { }
                try { noProgressWatchdog?.Dispose(); } catch { }
                try { buildHeartbeat?.Dispose(); } catch { }
                // issue #42 — build-evidence gate. Single choke point for BOTH the
                // in-process and MSBuild.exe branches (both reach here via return /
                // fall-through). On a terminal success for a code-emitting action,
                // verify the requested/dirty targets actually got fresh generated .cs.
                // The normal terminal paths attach before StateChangeSignal.Set().
                // Keep this as a fallback for exceptional/early-return paths, but do
                // not scan the KB a second time after a successful build.
                try
                {
                    if (status.GenerateEvidence == null)
                        AttachGenerateEvidence(status, action, targets);
                }
                catch (Exception ex) { Logger.Warn("[GENERATE-EVIDENCE] gate threw: " + ex.Message); }
                // Guaranteed MSBuild cleanup: on ANY exit path (success, failure, or
                // exception — not just cancel/timeout) reap the spawned MSBuild process
                // tree if anything is still alive. /nodeReuse:false already tears the /m
                // worker nodes down on normal completion, but a hung/slow child (or an
                // exception before WaitForExit returned) could otherwise linger on the
                // user's machine. This keeps the MCP from ever leaving MSBuild behind.
                try { ReapByPidIfAlive(reapPid, reapStart); }
                catch (Exception ex) { Logger.Warn("[BUILD-CLEANUP] MSBuild reap: " + ex.Message); }
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                status.Process = null;
                // issue #42 — build no longer in flight; unblock the next build.
                if (status?.TaskId != null) _inFlightBuilds.TryRemove(status.TaskId, out _);
                // Don't leak the phase tag onto unrelated work on this thread.
                Helpers.Logger.CurrentPhase = null;
            }
        }

        private void HandleLine(BuildTaskStatus status, string line, bool isError)
        {
            if (line == null) return;
            lock (status._lock)
            {
                // v2.6.6 Stream F: capture pre-mutation baseline so we can fire
                // the StateChangeSignal once at the end IFF anything that affects
                // the wait-baseline actually changed (phase / counts / TargetsDone).
                string baselineBefore = status.ComputeBaseline();
                try
                {

                status.LineCount++;
                status.LastLine = line;
                status.FullOutput.AppendLine(line);

                if (line.IndexOf("[GXMCP-BUILD-ALL] KB opened", StringComparison.OrdinalIgnoreCase) >= 0)
                    status.KbOpened = true;
                if (line.IndexOf("[GXMCP-BUILD-ALL] BuildAll completed", StringComparison.OrdinalIgnoreCase) >= 0)
                    status.BuildAllDone = true;
                if (string.Equals(status.Action, "BuildAll", StringComparison.OrdinalIgnoreCase)
                    && DetectBuildAllReorgRequired(line))
                    status.ReorgRequired = true;

                // FR#12: keep noise out of TailLines but always preserve it in FullOutput.
                // Errors/warnings/phase-change lines never count as noise.
                bool isNoise = _rxModuleCopyNoise.IsMatch(line) &&
                               !_rxError.IsMatch(line) &&
                               !_rxWarning.IsMatch(line);
                if (!isNoise)
                {
                    status.TailLines.Add(line);
                    if (status.TailLines.Count > TailBufferSize) status.TailLines.RemoveAt(0);
                }

                // In-process section markers first (they never match the text parsers
                // below). ">S<section>" advances the phase; ">E0<section>" records a
                // named failure so a terminal Failed with no itemized "error CS####:"
                // line still tells the agent where the build broke.
                var mSecStart = _rxSectionStart.Match(line);
                if (mSecStart.Success)
                {
                    string ph = MapSectionToPhase(mSecStart.Groups["name"].Value);
                    if (ph != null) { status.Phase = ph; EmitPhaseProgress(status.Phase); }
                    return;
                }
                var mSecFail = _rxSectionFail.Match(line);
                if (mSecFail.Success)
                {
                    string sect = mSecFail.Groups["name"].Value.Trim();
                    // Skip the generic outer "Build" wrapper — it names no useful location.
                    if (status.PhaseFailure == null
                        && !string.Equals(sect, "Build", StringComparison.OrdinalIgnoreCase))
                    {
                        status.PhaseFailure = new PhaseFailureInfo
                        {
                            Name = sect,
                            Message = "Section '" + sect + "' failed during the in-process build."
                        };
                    }
                    return;
                }

                if (_rxOpenKb.IsMatch(line))      { status.Phase = "OpeningKB"; EmitPhaseProgress(status.Phase); return; }
                var m = _rxSpecifying.Match(line); if (m.Success) { status.Phase = "Specifying"; EmitPhaseProgress(status.Phase); status.CurrentObject = m.Groups["obj"].Value.Trim(); return; }
                m = _rxGenerating.Match(line);     if (m.Success) { status.Phase = "Generating"; EmitPhaseProgress(status.Phase); status.CurrentObject = m.Groups["obj"].Value.Trim(); return; }
                if (_rxCompiling.IsMatch(line))   { status.Phase = "Compiling"; EmitPhaseProgress(status.Phase); return; }
                if (_rxBuildDone.IsMatch(line))   { status.Phase = "Finishing"; EmitPhaseProgress(status.Phase); }
                if (_rxBuildOneEnd.IsMatch(line) && status.TargetsTotal.HasValue)
                {
                    status.TargetsDone = (status.TargetsDone ?? 0) + 1;
                }

                if (_rxError.IsMatch(line))
                {
                    // v2.6.6 Stream E (FR#9): CS2001 for "<obj>_bc.cs" where the
                    // underlying Transaction has BC disabled (or doesn't exist) is
                    // an orphan — the stale generated source file is not the
                    // requested target's fault. Demote it to a warning so the
                    // build status reflects the actual failure mode.
                    if (IsBcOrphanError(line, _indexCacheService))
                    {
                        status.DemotedErrors++;
                        status.WarningCount++;
                        if (status.Warnings.Count < 50) status.Warnings.Add("[demoted-bc-orphan] " + line.Trim());
                        return;
                    }

                    status.ErrorCount++;

                    // FR#21 (Stream C followup): rewrite the GxBuild_*.msbuild(N,M)
                    // location to [gx-object=... phase=...] so the agent gets an
                    // actionable target instead of a temp-file address. Keep the
                    // raw form too — ErrorsDetailed carries both for debugging.
                    string rawErr = line.Trim();
                    // issue #28 item 14: restore authored identifier casing (&objcod → &ObjCod)
                    // before location-rewriting, so the surfaced line and the detail agree.
                    rawErr = NormalizeErrorIdentifierCase(rawErr);
                    string rewritten;
                    bool didRewrite = GxMcp.Worker.Helpers.BuildOutputShaper.TryRewriteErrorLocation(
                        rawErr, status.CurrentObject, status.Phase, out rewritten);
                    string surface = didRewrite ? rewritten : rawErr;
                    if (status.Errors.Count < 50) status.Errors.Add(surface);
                    if (status.ErrorsDetailed.Count < 50)
                    {
                        status.ErrorsDetailed.Add(new ErrorDetail
                        {
                            raw = rawErr,
                            rewritten = didRewrite ? rewritten : null,
                            phase = status.Phase,
                            gxObject = status.CurrentObject,
                            category = ClassifyErrorCategory(rawErr)
                        });
                    }

                    // CS0246: missing type — wrapper class for an un-built object.
                    var cs0246 = _rxCs0246Missing.Match(line);
                    if (cs0246.Success)
                    {
                        var norm = NormalizeMissingObjectName(cs0246.Groups[1].Value, _indexCacheService);
                        if (!string.IsNullOrEmpty(norm) && status.SuggestedRebuildTargets.Count < 50)
                            status.SuggestedRebuildTargets.Add(norm);
                    }
                    // CS2001: missing source file — extract <obj> from "<obj>_bc.cs" / "<obj>.cs".
                    var cs2001 = _rxCs2001Missing.Match(line);
                    if (cs2001.Success)
                    {
                        var file = cs2001.Groups[1].Value;
                        var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
                        var bare = slash >= 0 ? file.Substring(slash + 1) : file;
                        if (bare.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            bare = bare.Substring(0, bare.Length - 3);
                        var norm = NormalizeMissingObjectName(bare, _indexCacheService);
                        if (!string.IsNullOrEmpty(norm) && status.SuggestedRebuildTargets.Count < 50)
                            status.SuggestedRebuildTargets.Add(norm);
                    }
                }
                else if (_rxWarning.IsMatch(line))
                {
                    if (line.IndexOf("spc0217", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int q1 = line.IndexOf('\'');
                        int q2 = q1 >= 0 ? line.IndexOf('\'', q1 + 1) : -1;
                        if (q1 >= 0 && q2 > q1)
                        {
                            string unObj = line.Substring(q1 + 1, q2 - q1 - 1).Trim();
                            if (!string.IsNullOrEmpty(unObj)) status.UnreachableTargets.Add(unObj);
                        }
                    }

                    // issue #32 item 5: drop the spurious "<obj> not found in the Knowledge
                    // Base" warning when <obj> is one of the objects we're building — the
                    // object exists (it's the spec target); the warning is misleading noise.
                    // issue #86: do NOT suppress when specifyOnly=true, because GeneXus skips
                    // specifying unreachable/uncalled objects entirely.
                    var nf = _rxObjectNotFoundWarning.Match(line);
                    if (nf.Success)
                    {
                        string objName = nf.Groups["obj"].Value;
                        status.NotFoundTargets.Add(objName);
                        if (!status.SpecifyOnly && IsBuildTarget(objName, status))
                        {
                            return;
                        }
                    }
                    status.WarningCount++;
                    if (status.Warnings.Count < 50) status.Warnings.Add(line.Trim());
                }
                }
                finally
                {
                    // Stream F: fire signal exactly once per HandleLine when the
                    // wait-baseline shifted. Reset+Set so wait callers see an edge
                    // even if another thread is still inside Wait().
                    if (!string.Equals(baselineBefore, status.ComputeBaseline(), StringComparison.Ordinal))
                    {
                        try { status.StateChangeSignal.Set(); } catch { }
                    }
                }
            }
        }

        private void SetFailure(BuildTaskStatus status, string error)
        {
            status.Status = "Error";
            status.Phase = "Done";
            EmitPhaseProgress(status.Phase);
            status.Error = error;
            status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);
            try { status.StateChangeSignal.Set(); } catch { }
        }

        public string GetKBPath()
        {
            return Environment.GetEnvironmentVariable("GX_KB_PATH") ?? "";
        }

        // Item 72 (friction 2026-05-22) — fire-and-forget POST to the configured
        // Slack/Discord webhook on terminal Failed state. PartialSuccess is NOT
        // notified (the build's downstream packaging step failed but the DLLs
        // compiled — usually the agent doesn't care). No retries, no auth — by
        // design (the spec asked for ~30 lines).
        internal static void MaybeNotifyOnFailure(BuildTaskStatus status)
        {
            if (status == null) return;
            string url = status.NotifyOnFailureUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!string.Equals(status.Status, "Failed", StringComparison.OrdinalIgnoreCase)) return;
            if (status.PartialSuccess == true) return;

            try
            {
                var payload = BuildNotificationPayload(status);
                PostWebhook(url, payload);
            }
            catch (Exception ex) { Logger.Warn("[NOTIFY-WEBHOOK] " + ex.Message); }
        }

        // Test seam: pure payload builder, no IO.
        internal static string BuildNotificationPayload(BuildTaskStatus status)
        {
            var errorsArr = new JArray();
            if (status.Errors != null)
            {
                foreach (var e in status.Errors.Take(10)) errorsArr.Add(e);
            }
            var detailedArr = new JArray();
            if (status.ErrorsDetailed != null)
            {
                foreach (var d in status.ErrorsDetailed.Take(5))
                {
                    detailedArr.Add(JObject.FromObject(d));
                }
            }
            var jo = new JObject
            {
                ["kb"] = Environment.GetEnvironmentVariable("GX_KB_PATH") ?? string.Empty,
                ["target"] = status.Target ?? string.Empty,
                ["jobId"] = status.TaskId ?? string.Empty,
                ["durationSec"] = status.ElapsedSeconds ?? 0.0,
                ["errors"] = errorsArr,
                ["errorsDetailedHead"] = detailedArr
            };
            return jo.ToString(Formatting.None);
        }

        private static void PostWebhook(string url, string jsonBody)
        {
            // Synchronous one-shot post; the failure path is rare and we don't
            // want to wedge the build thread on a slow network. 5s hard cap.
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 5000;
            byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);
            req.ContentLength = bytes.Length;
            using (var stream = req.GetRequestStream()) { stream.Write(bytes, 0, bytes.Length); }
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            {
                // Drain and discard — we don't care about the body.
                using (var rs = resp.GetResponseStream())
                {
                    if (rs != null) { var buf = new byte[1024]; while (rs.Read(buf, 0, buf.Length) > 0) { } }
                }
            }
        }

        // Item 43 (friction 2026-05-22) — DDL diff/preview pre-reorg. The SDK
        // doesn't expose a clean "compute reorg plan, do not execute" entry
        // point on net48 (CheckAndInstallDatabase always touches the live DB).
        // Stub: return an empty plan + a hint pointing at the live reorg.
        // Refined when the SDK surface probe finds a non-mutating entry point.
        // issue #37 item 4: returns a terminal ReorgDisabled envelope when the default
        // datastore has 'Reorganize Server tables = No', else null (proceed with reorg).
        // Null when the mode can't be resolved — never blocks reorg on uncertainty.
        internal string CheckReorgDisabled()
        {
            try
            {
                dynamic kb = _kbService?.GetKB();
                if (kb == null) return null;
                // Statically type the result — kb is dynamic, so leaving `ds` inferred
                // makes it dynamic and turns re.Value<bool>() into a dynamic generic call
                // that fails to bind ("no overload for 'Value' takes 0 arguments").
                JObject ds = DatabaseInfoService.GetDefaultDataStoreInfo(kb);
                JToken re = ds?["reorgEnabled"];
                if (re != null && re.Type == JTokenType.Boolean && re.Value<bool>() == false)
                {
                    return McpResponse.Ok(
                        code: "ReorgDisabled",
                        result: new JObject
                        {
                            ["reorgEnabled"] = false,
                            ["datastore"] = ds,
                            ["message"] = "This datastore has 'Reorganize Server tables = No' (DBA-managed). GeneXus does not apply schema changes to the server here, so action=reorg would be a no-op. No build was queued.",
                            ["nextSteps"] = "Obtain the schema DDL from the GeneXus IDE Impact Analysis report (or genexus_lifecycle action=reorg_preview for the reorg mode) and apply it via your DB tooling / hand it to the DBA. To let GeneXus manage the schema, set 'Reorganize Server tables = Yes' on the datastore in the IDE."
                        });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[REORG-GUARD] reorg-disabled check failed (proceeding): " + ex.Message);
            }
            return null;
        }

        public string ReorgPreview(string target)
        {
            return new ReorgImpactService(_kbService).Run(new JObject
            {
                ["action"] = "reorg_preview",
                ["name"] = target,
                ["deep"] = true
            });
        }
    }
}
