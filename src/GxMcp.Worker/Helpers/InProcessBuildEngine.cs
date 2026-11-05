using System;
using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

namespace GxMcp.Worker.Helpers
{
    // v2.6.6 Stream D — IBuildEngine adapter used to invoke GeneXus MSBuild
    // tasks (SpecifyOneOnly, IdeWebBuildAndDeploy) in-process against the
    // worker's already-open KB. Logging events are forwarded to a sink
    // delegate so the BuildService line parser (HandleLine) keeps producing
    // phase / error / warning telemetry exactly as it did from MSBuild.exe.
    //
    // BuildProjectFile is a no-op: GeneXus tasks don't actually invoke
    // sub-projects, so a true return is safe.
    public sealed class InProcessBuildEngine : IBuildEngine
    {
        // Sink signature mirrors BuildService.HandleLine(status, line, isError).
        // The runner closes over its own BuildTaskStatus so this engine only
        // needs (string line, bool isError).
        private readonly Action<string, bool> _sink;

        // Friction 2026-05-22: capture EVERY logged event (incl. low-severity
        // diagnostic messages MSBuild tasks emit) so we can post-mortem why
        // a task returned false even with no error event raised. Trimmed to
        // 200 entries to bound memory.
        private readonly System.Collections.Generic.Queue<string> _trace = new System.Collections.Generic.Queue<string>();
        private const int TraceCap = 200;
        // Phase profiler: when GXMCP_BUILD_PROFILE=1, log every >S/>E1/>E0
        // section marker with a wall-clock timestamp + delta from build start.
        // Output lands in worker_debug.log with the [BUILD-PROFILE] tag for
        // post-mortem grep. Cheap when disabled (single env-var lookup at ctor).
        // Always-on profiling: cheap (one regex match + Logger.Info per ~30 marker lines per build).
        // Worth the ~50µs per line to never have to instrument again. Disable with GXMCP_BUILD_PROFILE=0.
        private static readonly bool _profileEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GXMCP_BUILD_PROFILE"), "0", StringComparison.Ordinal);
        private readonly System.Diagnostics.Stopwatch _profileSw = System.Diagnostics.Stopwatch.StartNew();
        private static readonly Regex _rxSectionStartProfile = new Regex(@"^>S(?<name>[^:]+?)(?:[: ]|$)", RegexOptions.Compiled);
        private static readonly Regex _rxSectionEndProfile = new Regex(@"^>E[01](?<name>[^:]+?)(?:[: ]|$)", RegexOptions.Compiled);
        // Names of the sections the SDK opened (">SSpecification:-:...", ">SDefault (.NET Framework) Generation:-:..."),
        // in order. specify_only reports them as evidence of which steps actually ran.
        private static readonly Regex _rxSectionStartName = new Regex(@"^>S(?<name>[^:\r\n]+)", RegexOptions.Compiled);
        private readonly System.Collections.Generic.List<string> _startedSections = new System.Collections.Generic.List<string>();

        public System.Collections.Generic.IReadOnlyList<string> StartedSections
        {
            get { lock (_startedSections) return _startedSections.ToArray(); }
        }

        // Every GeneXus diagnostic code (spc0217, gen0001, ...) any message mentioned. The line parsers downstream
        // classify a line by its shape; this answers "did the SDK say spc0217 at all", whatever the shape. The SDK
        // glues the code to its output marker (">O2spc0217: ..."), so the code is delimited by "not a letter before,
        // not a letter or digit after" rather than by a word boundary.
        private static readonly Regex _rxDiagnosticCode = new Regex(@"(?<![A-Za-z])(?:spc|gen|src|qry)\d{4}(?![0-9A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly System.Collections.Generic.HashSet<string> _seenCodes =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool SawCode(string code)
        {
            lock (_seenCodes) return _seenCodes.Contains(code);
        }

        private void Record(string s)
        {
            lock (_trace)
            {
                _trace.Enqueue(s);
                while (_trace.Count > TraceCap) _trace.Dequeue();
            }
            if (s != null)
            {
                int sectionPayloadStart = s.IndexOf("] ", StringComparison.Ordinal);
                var sectionMatch = _rxSectionStartName.Match(sectionPayloadStart >= 0 ? s.Substring(sectionPayloadStart + 2) : s);
                if (sectionMatch.Success)
                    lock (_startedSections) _startedSections.Add(sectionMatch.Groups["name"].Value.Trim());
                foreach (Match code in _rxDiagnosticCode.Matches(s))
                    lock (_seenCodes) _seenCodes.Add(code.Value);
            }
            if (_profileEnabled && s != null)
            {
                try
                {
                    // Strip the [MSG/.../etc] prefix that Record() prepends so the
                    // regex sees the raw GeneXus marker.
                    int payloadStart = s.IndexOf("] ", StringComparison.Ordinal);
                    string payload = payloadStart >= 0 ? s.Substring(payloadStart + 2) : s;
                    var m = _rxSectionStartProfile.Match(payload);
                    if (m.Success)
                        Logger.Info("[BUILD-PROFILE] " + _profileSw.ElapsedMilliseconds + "ms START " + m.Groups["name"].Value);
                    else
                    {
                        m = _rxSectionEndProfile.Match(payload);
                        if (m.Success)
                            Logger.Info("[BUILD-PROFILE] " + _profileSw.ElapsedMilliseconds + "ms END   " + m.Groups["name"].Value);
                    }
                }
                catch { /* profiling is best-effort */ }
            }
            // Track partial-success markers from the GeneXus build pipeline.
            // The output protocol uses prefixes:
            //   >S<section>:-:<label> — section starts
            //   >E1<section> / >E0<section> — section ends OK (1) / FAIL (0)
            //   >L<message> — log line inside current section
            //   >R<runtime error message>
            // We care about two markers:
            //   - the "Compilation" section ending with E1 (success) means the .dll
            //     was produced. Anything failing AFTER this point (WebAppConfig,
            //     module copies) is non-blocking for serving the object.
            //   - any error before that means the build is broken in a way that
            //     matters; we must not silently swallow it.
            if (s != null)
            {
                // Anchored regexes: the marker MUST be at the start of the GeneXus
                // payload. Each Log*Event prepends a [MSG/Severity] / [WARN] / [ERROR]
                // / [CUSTOM] tag (used by DrainTrace), so we strip that prefix before
                // matching — otherwise `^>E1...` never anchors and CompileSucceeded
                // stays false on every build. Bug 2026-05-22 #2: the anchored regex
                // alone (without prefix-strip) silently broke partial-success
                // detection, sending every BuildOne back to the 5-min MSBuild.exe
                // fallback.
                int payloadStart = s.IndexOf("] ", StringComparison.Ordinal);
                string payload = payloadStart >= 0 ? s.Substring(payloadStart + 2) : s;
                if (_rxCompileEnd.IsMatch(payload))
                {
                    System.Threading.Volatile.Write(ref _compileSucceeded, 1);
                }
                if (_rxWebAppConfigStart.IsMatch(payload))
                {
                    System.Threading.Volatile.Write(ref _webAppConfigStarted, 1);
                }
                if (_rxPreCompileFail.IsMatch(payload))
                {
                    System.Threading.Volatile.Write(ref _preCompileSectionFailed, 1);
                }
            }
        }

        // Section markers follow the protocol:
        //   >E1<SectionName>[:-:...]  — section ended OK; SectionName ends in "Compilation"
        //   >SWebAppConfig[:-:...]    — WebAppConfig section started
        // The \b after Compilation/WebAppConfig ensures we don't match a longer identifier
        // that just happens to start with these words.
        private static readonly Regex _rxCompileEnd = new Regex(
            @"^>E1[A-Za-z][A-Za-z0-9 _]*Compilation\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxWebAppConfigStart = new Regex(
            @"^>SWebAppConfig\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // ">E0Specification" / ">E0Default (.NET Framework) Generation". BuildOne keeps
        // going after a failed spec: it still compiles the DeveloperMenu and the target's
        // previously generated .cs, so ">E1...Compilation" alone does not prove the
        // requested object was rebuilt.
        private static readonly Regex _rxPreCompileFail = new Regex(
            @"^>E0[^:]*(?:Specification|Generation)(?::|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Reset section flags between targets in a multi-target fast-path loop.
        // Without this, target N+1 inherits target N's success markers and a
        // late-stage failure on N+1 (before its own compile) gets misclassified
        // as "compile OK, post-compile failed → partial success".
        public void ResetSectionFlags()
        {
            System.Threading.Volatile.Write(ref _compileSucceeded, 0);
            System.Threading.Volatile.Write(ref _webAppConfigStarted, 0);
            System.Threading.Volatile.Write(ref _preCompileSectionFailed, 0);
        }
        public string DrainTrace()
        {
            lock (_trace)
            {
                if (_trace.Count == 0) return "(empty)";
                return string.Join("\n", _trace);
            }
        }

        // Set when "<...>Compilation" section ended with E1 (success marker).
        private int _compileSucceeded;
        public bool CompileSucceeded => System.Threading.Volatile.Read(ref _compileSucceeded) == 1;

        // Set when WebAppConfig (or any post-compile) section started — so when
        // CompileSucceeded && WebAppConfigStarted && Execute returned false, the
        // failure is in the deploy/config layer, after the DLL is already written.
        private int _webAppConfigStarted;
        public bool WebAppConfigStarted => System.Threading.Volatile.Read(ref _webAppConfigStarted) == 1;

        // Set when a Specification or Generation section ended with E0.
        private int _preCompileSectionFailed;
        public bool PreCompileSectionFailed => System.Threading.Volatile.Read(ref _preCompileSectionFailed) == 1;

        public InProcessBuildEngine(Action<string, bool> sink)
        {
            _sink = sink ?? ((l, e) => { });
        }

        public bool ContinueOnError => true;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
        {
            return true;
        }

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
            try { if (e != null && !string.IsNullOrEmpty(e.Message)) { Record("[CUSTOM] " + e.Message); _sink(e.Message, false); } } catch { }
        }

        public void LogErrorEvent(BuildErrorEventArgs e)
        {
            try
            {
                if (e == null) return;
                // Re-emit in a shape close to what MSBuild.exe writes so the existing
                // _rxError ("error CODE:" or "error :") catches it. Prefer the supplied
                // code+message if both are present.
                string line;
                if (!string.IsNullOrEmpty(e.Code))
                    line = "error " + e.Code + ": " + e.Message;
                else
                    line = "error : " + e.Message;
                Record("[ERROR] " + line);
                _sink(line, true);
            }
            catch { }
        }

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
            try
            {
                if (e == null || string.IsNullOrEmpty(e.Message)) return;
                Record("[MSG/" + e.Importance + "] " + e.Message);
                _sink(e.Message, false);
            }
            catch { }
        }

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
            try
            {
                if (e == null) return;
                string line;
                if (!string.IsNullOrEmpty(e.Code))
                    line = "warning " + e.Code + ": " + e.Message;
                else
                    line = "warning : " + e.Message;
                Record("[WARN] " + line);
                _sink(line, false);
            }
            catch { }
        }
    }
}
