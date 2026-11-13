using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Stable error codes of <c>genexus_wire</c>. Every failure path of the tool
    /// answers with one of these, a human message and a <c>diagnosticContext</c>;
    /// none of them may surface as the transport's generic <c>Internal error</c> (#593).
    /// </summary>
    internal static class GxwireErrorCodes
    {
        internal const string BinaryNotFound = "GxwireNotFound";
        internal const string KbContextRequired = "KB_CONTEXT_REQUIRED";
        internal const string RequiresTextSourceTree = "GXWIRE_REQUIRES_TEXT_SOURCE_TREE";
        internal const string CacheMissing = "GxwireCacheMissing";
        internal const string Timeout = "GxwireTimeout";
        internal const string ExecutionFailed = "GxwireExecutionFailed";
        internal const string OutputUnparsable = "GxwireOutputUnparsable";
        internal const string ProcessStartFailed = "ProcessStartFailed";
        internal const string UnexpectedException = "GxwireException";
        internal const string TargetRequired = "TargetRequired";
        internal const string TaskRequired = "TaskRequired";
        internal const string PathEndpointsRequired = "PathEndpointsRequired";
        internal const string UnknownAction = "UnknownAction";
        internal const string InvalidArgument = "InvalidArgument";
    }

    internal sealed class GxwireTool
    {
        private readonly Action<string> _log;
        private readonly Func<string, string?> _resolveKbPath;
        private readonly Func<string?> _sessionKbPath;
        private readonly Func<string?> _configuredKbPath;
        private readonly Func<JObject> _buildDiagnosticContext;

        /// <param name="log">Gateway log sink.</param>
        /// <param name="resolveKbPath">Resolves an explicit <c>kb</c> argument to its path.</param>
        /// <param name="sessionKbPath">Path of the session's current Knowledge Base, if any.</param>
        /// <param name="configuredKbPath">Knowledge Base path of the active configuration, if any.</param>
        /// <param name="buildDiagnosticContext">The gateway-wide diagnostic context every error envelope repeats.</param>
        internal GxwireTool(
            Action<string> log,
            Func<string, string?> resolveKbPath,
            Func<string?> sessionKbPath,
            Func<string?> configuredKbPath,
            Func<JObject> buildDiagnosticContext)
        {
            _log = log;
            _resolveKbPath = resolveKbPath;
            _sessionKbPath = sessionKbPath;
            _configuredKbPath = configuredKbPath;
            _buildDiagnosticContext = buildDiagnosticContext;
        }

        /// <summary>
        /// The three places <c>genexus_wire</c> touches the outside world, made
        /// replaceable so every failure path can be exercised without a gxwire
        /// binary: where the executable is, how it is run, and where its index lives.
        /// Production uses <see cref="Default"/>.
        /// </summary>
        internal sealed class GxwireToolSeams
        {
            internal static readonly GxwireToolSeams Default = new GxwireToolSeams();

            internal Func<string?> LocateExecutable { get; init; } = LocateGxwireExecutable;

            /// <summary>(exePath, argument list, timeoutSeconds) → the completed run. Arguments are never joined into a command line.</summary>
            internal Func<string, IReadOnlyList<string>, int, Task<GxwireProcessRun>> Launch { get; init; } = LaunchGxwireAsync;

            internal Func<string?, string> ResolveCachePath { get; init; } = GxwireCacheLocation.Resolve;
        }

        /// <summary>
        /// What is known about one invocation at the moment it fails. Rendered
        /// into <c>diagnosticContext</c> so the caller can tell a missing binary
        /// from an expired budget from a crashed engine without reading logs.
        /// </summary>
        private sealed class GxwireRunContext
        {
            internal string Action { get; set; } = "pack_task";
            internal string Format { get; set; } = "xml";
            internal string? KbRoot { get; set; }
            internal string? CachePath { get; set; }
            internal bool? CacheExists { get; set; }
            internal string? ExePath { get; set; }
            internal int? ExitCode { get; set; }
            internal long? ElapsedMs { get; set; }
            internal int? TimeoutSeconds { get; set; }
            internal string? StderrTail { get; set; }
            internal string? StdoutHead { get; set; }
            internal string? ExceptionType { get; set; }
        }

        /// <summary>Bounds on what an error envelope repeats from the child's pipes.</summary>
        internal const int GxwireStderrTailChars = 2048;
        internal const int GxwireStdoutHeadChars = 2048;
        internal const int GxwireExceptionMessageChars = 1024;

        private const string GxwireTextTreeExportHint =
            "Export a text source tree first: genexus_io action=export_kb_to_text format=native part=all outputPath=<folder> "
            + "writes the SDK text tree under <folder>\\src. Then run genexus_wire action=index dir=<folder> once and query with the same dir. "
            + "To read this Knowledge Base through the SDK instead, use genexus_read, genexus_query, genexus_search_source, genexus_analyze or genexus_inspect.";

        private static JObject BuildGxwireTextTreeExportPointer() => new JObject
        {
            ["tool"] = "genexus_io",
            ["action"] = "export_kb_to_text",
            ["args"] = new JObject
            {
                ["format"] = "native",
                ["part"] = "all",
                ["outputPath"] = "<folder>"
            },
            ["then"] = "genexus_wire action=index dir=<folder>"
        };

        /// <summary>
        /// Runs one <c>genexus_wire</c> call and always returns an envelope.
        ///
        /// The previous shape wrapped only the process launch in a handler, so a
        /// failure in argument parsing, Knowledge Base resolution, cache
        /// resolution or envelope shaping escaped to the stdio loop's generic
        /// handler and reached the client as <c>Internal error</c> with no code
        /// and no cause (#593). Everything now runs under one handler, and the
        /// exception detail stays bounded inside the envelope.
        /// </summary>
        internal async Task<JObject> ExecuteGxwireToolAsync(
            JObject? tArgs,
            string sessionId,
            bool sessionContextEnabled,
            GxwireToolSeams? seams = null)
        {
            seams ??= GxwireToolSeams.Default;
            var ctx = new GxwireRunContext
            {
                Action = ReadGxwireArg(tArgs, "action") ?? "pack_task",
                Format = ReadGxwireArg(tArgs, "format") ?? "xml"
            };

            try
            {
                return await ExecuteGxwireToolCoreAsync(tArgs, seams, ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.ExceptionType = ex.GetType().Name;
                _log($"[genexus_wire] action={ctx.Action} failed with {ctx.ExceptionType}: {ex.Message}");
                return BuildGxwireError(
                    GxwireErrorCodes.UnexpectedException,
                    "genexus_wire failed before gxwire could report a result: " + Head(ex.Message, GxwireExceptionMessageChars),
                    ctx,
                    hint: "This is a gateway-side failure, not a gxwire verdict about the Knowledge Base. Retry with action=doctor to confirm the binary and cache, then report the diagnosticContext if it persists.");
            }
        }

        private async Task<JObject> ExecuteGxwireToolCoreAsync(
            JObject? tArgs,
            GxwireToolSeams seams,
            GxwireRunContext ctx)
        {
            // Values reach gxwire as separate argv entries, so a value that itself
            // starts with '-' would be read as a flag (e.g. --cache-path).
            foreach (string key in GxwireCallerValueKeys)
            {
                string? supplied = ReadGxwireArg(tArgs, key);
                if (supplied != null && supplied.StartsWith("-", StringComparison.Ordinal))
                {
                    return BuildGxwireError(
                        GxwireErrorCodes.InvalidArgument,
                        $"Argument '{key}' must not start with '-'.",
                        ctx,
                        hint: "Values are passed to gxwire as separate arguments; a leading '-' would be parsed as a gxwire flag. Prefix relative paths with './' if needed.");
                }
            }

            string? exePath = seams.LocateExecutable();
            if (string.IsNullOrEmpty(exePath))
            {
                return BuildGxwireError(
                    GxwireErrorCodes.BinaryNotFound,
                    "gxwire.exe binary not found. Build gxwire via 'cargo build --release' or run build.ps1 to generate publish/gxwire.exe.",
                    ctx,
                    hint: "The engine is looked up next to the Gateway executable, under publish/, under src/gxwire/target/release/ and on PATH.");
            }
            ctx.ExePath = exePath;

            string action = ctx.Action;
            string format = ctx.Format;
            int budget = ReadGxwireInt(tArgs, "token_budget") ?? 1200;
            bool full = ReadGxwireBool(tArgs, "full") ?? false;

            string? kbDir = null;
            string? dirArg = ReadGxwireArg(tArgs, "dir");
            string? kbArg = ReadGxwireArg(tArgs, "kb");
            if (!string.IsNullOrWhiteSpace(dirArg))
            {
                kbDir = dirArg;
            }
            else if (!string.IsNullOrWhiteSpace(kbArg))
            {
                kbDir = _resolveKbPath(kbArg);
            }
            else if (!string.IsNullOrWhiteSpace(_sessionKbPath()))
            {
                kbDir = _sessionKbPath();
            }
            else if (!string.IsNullOrWhiteSpace(_configuredKbPath()))
            {
                kbDir = _configuredKbPath();
            }
            ctx.KbRoot = kbDir;

            bool isDoctor = string.Equals(action, "doctor", StringComparison.OrdinalIgnoreCase);
            JArray? nativeKb = null;
            if (!isDoctor)
            {
                if (string.IsNullOrWhiteSpace(kbDir) || !Directory.Exists(kbDir))
                {
                    return BuildGxwireError(
                        GxwireErrorCodes.KbContextRequired,
                        "No Knowledge Base path resolved or directory does not exist. Open a KB with genexus_kb or specify 'kb'/'dir'.",
                        ctx);
                }

                // A native Knowledge Base keeps its object sources in the KB
                // database, not in files. Walking its folder would index
                // generated output - gigabytes of it - and the resulting graph
                // would be inferred from whatever text happened to be there,
                // which reads exactly like a real graph and is not one (#499).
                // Decided here, before any process exists, so a large KB root is
                // never walked on the way to a refusal (#593).
                nativeKb = NativeKnowledgeBaseEvidence.Describe(kbDir!);
                if (nativeKb != null)
                {
                    return BuildGxwireError(
                        GxwireErrorCodes.RequiresTextSourceTree,
                        "genexus_wire reads GeneXus sources from files, but this is a native Knowledge Base: its object sources live in the KB database, not on disk. Indexing the folder would walk generated output (environment, build, obj, UserControls, Locks, OutputLogs...), not the Knowledge Base.",
                        ctx,
                        hint: GxwireTextTreeExportHint,
                        next: "genexus_io action=export_kb_to_text format=native part=all outputPath=<folder>",
                        extend: envelope =>
                        {
                            envelope["path"] = kbDir;
                            envelope["evidence"] = new JArray(nativeKb);
                            envelope["textTreeExport"] = BuildGxwireTextTreeExportPointer();
                            envelope["sdkAlternative"] = new JArray("genexus_read", "genexus_query", "genexus_search_source", "genexus_analyze", "genexus_inspect");
                            // pack_task needs no export once the KB is open: this refusal is only reached
                            // when it is not (or dir names another native KB), so say how to reach the SDK path.
                            GxwireNativePackTask.WithPackTaskHint(envelope, tArgs);
                        });
                }
            }
            else if (!string.IsNullOrWhiteSpace(kbDir))
            {
                // doctor is allowed on a native root so it can say what is there,
                // but its "run --index" advice is wrong for one: say so in-band.
                nativeKb = NativeKnowledgeBaseEvidence.Describe(kbDir);
            }

            // The index goes to the MCP cache directory, never into the analysed
            // folder. The published contract declares genexus_wire read-only, and
            // with a native KB that folder is the Knowledge Base itself (#499).
            string? gxwireCachePath = null;
            try { gxwireCachePath = seams.ResolveCachePath(kbDir); }
            catch (Exception ex)
            {
                // Not fatal: gxwire still works, it just falls back to its own
                // in-tree default and stops being read-only. Reported, not hidden.
                Console.Error.WriteLine("[genexus_wire] could not resolve a cache path outside the analysed directory: " + ex.Message);
            }
            ctx.CachePath = gxwireCachePath;
            ctx.CacheExists = gxwireCachePath != null ? File.Exists(gxwireCachePath) : (bool?)null;

            // status answers from the index. Without one gxwire parses the whole
            // tree on the fly inside the 30 s query budget, which on a large tree
            // is a guaranteed timeout and on a huge one a multi-minute walk; the
            // honest answer is that there is no index yet (#593).
            if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase) && ctx.CacheExists == false)
            {
                return BuildGxwireError(
                    GxwireErrorCodes.CacheMissing,
                    "No gxwire index exists for this directory yet, so action=status has nothing to report.",
                    ctx,
                    hint: $"Run genexus_wire action=index dir={kbDir} once; the index is then reused by status and every query. The index budget is {GxwireProcessRunner.DefaultIndexTimeoutSeconds} s by default and {GxwireProcessRunner.TimeoutEnvVar} raises it.",
                    next: $"genexus_wire action=index dir={kbDir}");
            }

            var gxArgs = new List<string>();

            switch (action.ToLowerInvariant())
            {
                case "doctor":
                    gxArgs.Add("--doctor");
                    if (!string.IsNullOrWhiteSpace(kbDir))
                    {
                        gxArgs.AddRange(new[] { "--dir", kbDir });
                    }
                    gxArgs.AddRange(new[] { "--format", format });
                    break;

                case "status":
                    gxArgs.AddRange(new[] { "--dir", kbDir!, "--format", format });
                    break;

                case "index":
                    gxArgs.AddRange(new[] { "--index", "--dir", kbDir! });
                    break;

                case "callers":
                    string target = ReadGxwireArg(tArgs, "target") ?? ReadGxwireArg(tArgs, "symbol") ?? ReadGxwireArg(tArgs, "name") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        return BuildGxwireError(GxwireErrorCodes.TargetRequired, "Action 'callers' requires 'target'.", ctx);
                    }
                    gxArgs.AddRange(new[] { "--callers", target, "--dir", kbDir!, "--format", format, "--token-budget", budget.ToString(CultureInfo.InvariantCulture) });
                    if (full) gxArgs.Add("--full");
                    break;

                case "impact":
                    string impactTarget = ReadGxwireArg(tArgs, "target") ?? ReadGxwireArg(tArgs, "symbol") ?? ReadGxwireArg(tArgs, "name") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(impactTarget))
                    {
                        return BuildGxwireError(GxwireErrorCodes.TargetRequired, "Action 'impact' requires 'target'.", ctx);
                    }
                    gxArgs.AddRange(new[] { "--impact", impactTarget, "--dir", kbDir!, "--format", format, "--token-budget", budget.ToString(CultureInfo.InvariantCulture) });
                    if (full) gxArgs.Add("--full");
                    break;

                case "for":
                    string forTask = ReadGxwireArg(tArgs, "task") ?? ReadGxwireArg(tArgs, "query") ?? ReadGxwireArg(tArgs, "target") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(forTask))
                    {
                        return BuildGxwireError(GxwireErrorCodes.TaskRequired, "Action 'for' requires 'task'.", ctx);
                    }
                    gxArgs.AddRange(new[] { "--for", forTask, "--dir", kbDir!, "--format", format, "--token-budget", budget.ToString(CultureInfo.InvariantCulture) });
                    if (full) gxArgs.Add("--full");
                    break;

                case "pack_task":
                case "pack-task":
                    string packTask = ReadGxwireArg(tArgs, "task") ?? ReadGxwireArg(tArgs, "query") ?? ReadGxwireArg(tArgs, "target") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(packTask))
                    {
                        return BuildGxwireError(GxwireErrorCodes.TaskRequired, "Action 'pack_task' requires 'task'.", ctx);
                    }
                    gxArgs.AddRange(new[] { "--pack-task", packTask, "--dir", kbDir!, "--format", format, "--token-budget", budget.ToString(CultureInfo.InvariantCulture) });
                    if (full) gxArgs.Add("--full");
                    break;

                case "slice":
                    string sliceTarget = ReadGxwireArg(tArgs, "target") ?? ReadGxwireArg(tArgs, "symbol") ?? ReadGxwireArg(tArgs, "name") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(sliceTarget))
                    {
                        return BuildGxwireError(GxwireErrorCodes.TargetRequired, "Action 'slice' requires 'target' (e.g. 'ProcName:VarName').", ctx);
                    }
                    gxArgs.AddRange(new[] { "--slice", sliceTarget, "--dir", kbDir!, "--format", format, "--token-budget", budget.ToString(CultureInfo.InvariantCulture) });
                    break;

                case "safe_delete":
                case "safe-delete":
                    string delTarget = ReadGxwireArg(tArgs, "target") ?? ReadGxwireArg(tArgs, "symbol") ?? ReadGxwireArg(tArgs, "name") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(delTarget))
                    {
                        return BuildGxwireError(GxwireErrorCodes.TargetRequired, "Action 'safe_delete' requires 'target'.", ctx);
                    }
                    gxArgs.AddRange(new[] { "--safe-delete", delTarget, "--dir", kbDir!, "--format", format });
                    break;

                case "path":
                case "trace":
                    string fromSym = ReadGxwireArg(tArgs, "from") ?? ReadGxwireArg(tArgs, "source") ?? ReadGxwireArg(tArgs, "origin") ?? string.Empty;
                    string toSym = ReadGxwireArg(tArgs, "to") ?? ReadGxwireArg(tArgs, "target") ?? ReadGxwireArg(tArgs, "destination") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(fromSym) || string.IsNullOrWhiteSpace(toSym))
                    {
                        return BuildGxwireError(GxwireErrorCodes.PathEndpointsRequired, "Action 'path' requires 'from' and 'to' parameters.", ctx);
                    }
                    gxArgs.AddRange(new[] { "--from", fromSym, "--to", toSym, "--dir", kbDir!, "--format", format });
                    if (full) gxArgs.Add("--full");
                    break;

                default:
                    return BuildGxwireError(
                        GxwireErrorCodes.UnknownAction,
                        $"Unknown action '{action}'.",
                        ctx,
                        hint: "Supported: pack_task, status, for, callers, impact, path, slice, safe_delete, index, doctor.");
            }

            // Appended once, after every action has contributed its own flags, so
            // no action can be added later that forgets to keep the analysed
            // directory read-only.
            if (!string.IsNullOrWhiteSpace(gxwireCachePath))
            {
                gxArgs.AddRange(new[] { "--cache-path", gxwireCachePath });
            }

            int timeoutSeconds = GxwireProcessRunner.ResolveTimeoutSeconds(action);
            ctx.TimeoutSeconds = timeoutSeconds;

            GxwireProcessRun run;
            try
            {
                run = await seams.Launch(exePath!, gxArgs, timeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.ExceptionType = ex.GetType().Name;
                return BuildGxwireError(
                    GxwireErrorCodes.ProcessStartFailed,
                    "Failed to run the gxwire process: " + Head(ex.Message, GxwireExceptionMessageChars),
                    ctx,
                    hint: "Check that gxwire.exe is executable from the Gateway account (action=doctor reports the resolved binary).");
            }

            ctx.ElapsedMs = run.ElapsedMs;
            ctx.ExitCode = run.ExitCode;
            ctx.StderrTail = Tail(run.StandardError, GxwireStderrTailChars);
            ctx.StdoutHead = Head(run.StandardOutput, GxwireStdoutHeadChars);

            if (run.TimedOut)
            {
                bool isIndex = string.Equals(action, "index", StringComparison.OrdinalIgnoreCase);
                string manualIndex = $"gxwire.exe --index \"{kbDir}\"" + (gxwireCachePath != null ? $" --cache-path \"{gxwireCachePath}\"" : string.Empty);
                string hint = isIndex
                    ? $"The tree is too large for the {run.TimeoutSeconds} s index budget. Raise {GxwireProcessRunner.TimeoutEnvVar} and retry, or build the index outside the tool call from a shell: {manualIndex} - then call action=status. gxwire walks every file under dir and has no folder exclusions yet, so point it at a text source tree, not at a Knowledge Base root."
                    : ctx.CacheExists == false
                        ? $"No index exists for this directory, so gxwire parsed the whole tree inside the {run.TimeoutSeconds} s query budget. Run genexus_wire action=index dir={kbDir} first (its budget is {GxwireProcessRunner.DefaultIndexTimeoutSeconds} s), then repeat this query."
                        : $"Retry with a larger {GxwireProcessRunner.TimeoutEnvVar}, or rebuild the index with action=index if the tree changed a lot since it was built.";
                return BuildGxwireError(
                    GxwireErrorCodes.Timeout,
                    $"gxwire exceeded its {run.TimeoutSeconds}s budget for action={action} and was terminated after {run.ElapsedMs} ms. The partial index on disk is reused on the next call.",
                    ctx,
                    hint: hint,
                    next: isIndex ? manualIndex : $"genexus_wire action=index dir={kbDir}",
                    extend: envelope =>
                    {
                        envelope["action"] = action;
                        envelope["timeoutSeconds"] = run.TimeoutSeconds;
                        envelope["elapsedMs"] = run.ElapsedMs;
                    });
            }

            string stdout = run.StandardOutput;
            string stderr = run.StandardError;
            if (run.ExitCode != 0)
            {
                string? engineCode = ExtractGxwireEngineCode(stderr) ?? ExtractGxwireEngineCode(stdout);
                return BuildGxwireError(
                    GxwireErrorCodes.ExecutionFailed,
                    run.ExitCode == null
                        ? $"gxwire ended without an exit code for action={action}."
                        : $"gxwire exited with code {run.ExitCode} for action={action}." + (engineCode != null ? $" Engine code: {engineCode}." : string.Empty),
                    ctx,
                    hint: engineCode != null
                        ? "The engine refused the request itself; the stderr tail carries its message and the symbol it could not resolve."
                        : "The stderr tail carries the engine's own message. A crash on a huge tree usually means dir points at a Knowledge Base root instead of a text source tree.",
                    extend: envelope =>
                    {
                        envelope["exitCode"] = run.ExitCode;
                        if (engineCode != null) envelope["engineCode"] = engineCode;
                        envelope["stderr"] = ctx.StderrTail;
                        envelope["stdout"] = ctx.StdoutHead;
                    });
            }

            string trimmedStdout = stdout.Trim();

            if (string.Equals(action, "index", StringComparison.OrdinalIgnoreCase))
            {
                // index reports on stderr only; what the caller needs is whether
                // the index now exists and how big it is.
                bool cacheExistsNow = gxwireCachePath != null && File.Exists(gxwireCachePath);
                ctx.CacheExists = cacheExistsNow;
                var indexed = new JObject
                {
                    ["status"] = "ok",
                    ["action"] = action,
                    ["engine"] = "gxwire",
                    ["format"] = format,
                    ["output"] = trimmedStdout,
                    ["cachePath"] = gxwireCachePath,
                    ["cacheExists"] = cacheExistsNow,
                    ["elapsedMs"] = run.ElapsedMs
                };
                int? nodes = ExtractGxwireNodeCount(stderr);
                if (nodes.HasValue) indexed["nodes"] = nodes.Value;
                string summary = LastNonEmptyLine(stderr);
                if (summary.Length > 0) indexed["summary"] = Head(summary, 512);
                if (!cacheExistsNow && gxwireCachePath != null)
                {
                    indexed["warning"] = "gxwire exited 0 but the index file was not found at cachePath; action=status will report GxwireCacheMissing.";
                }
                return indexed;
            }

            if (trimmedStdout.Length == 0)
            {
                return BuildGxwireError(
                    GxwireErrorCodes.OutputUnparsable,
                    $"gxwire exited 0 for action={action} but produced no output.",
                    ctx,
                    hint: "Run action=doctor to confirm the binary and the index; the stderr tail in diagnosticContext carries anything the engine printed.");
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                JToken parsedJson;
                try
                {
                    parsedJson = JToken.Parse(trimmedStdout);
                }
                catch (Exception ex)
                {
                    ctx.ExceptionType = ex.GetType().Name;
                    return BuildGxwireError(
                        GxwireErrorCodes.OutputUnparsable,
                        $"gxwire returned format=json for action={action} but its output is not JSON: " + Head(ex.Message, 256),
                        ctx,
                        hint: "The stdoutHead in diagnosticContext shows what was emitted. Retry with format=xml or format=toon to read the raw text.");
                }
                return new JObject
                {
                    ["status"] = "ok",
                    ["action"] = action,
                    ["engine"] = "gxwire",
                    ["data"] = parsedJson
                };
            }

            var ok = new JObject
            {
                ["status"] = "ok",
                ["action"] = action,
                ["engine"] = "gxwire",
                ["format"] = format,
                ["output"] = trimmedStdout
            };
            if (isDoctor && nativeKb != null)
            {
                // The engine's "run --index" advice does not apply to a native KB
                // root; without this the caller is sent to walk the whole KB.
                ok["textSourceTreeRequired"] = true;
                ok["evidence"] = new JArray(nativeKb);
                ok["hint"] = "dir is a native Knowledge Base root, so do not index it. " + GxwireTextTreeExportHint;
                ok["textTreeExport"] = BuildGxwireTextTreeExportPointer();
            }
            return ok;
        }

        /// <summary>
        /// Production launcher: starts gxwire with both pipes redirected and runs
        /// it under the budget. The process tree is killed on the way out whether
        /// the budget expired or a cancellation arrived from outside, so the child
        /// never outlives the call (#498).
        /// </summary>
        internal static async Task<GxwireProcessRun> LaunchGxwireAsync(string exePath, IReadOnlyList<string> arguments, int timeoutSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in arguments) psi.ArgumentList.Add(argument);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned no process for gxwire.");
            try
            {
                return await GxwireProcessRunner.RunAsync(proc, timeoutSeconds).ConfigureAwait(false);
            }
            finally
            {
                GxwireProcessRunner.TryKillProcessTree(proc);
            }
        }

        private JObject BuildGxwireError(
            string code,
            string message,
            GxwireRunContext ctx,
            string? hint = null,
            string? next = null,
            Action<JObject>? extend = null)
        {
            var envelope = new JObject
            {
                ["status"] = "error",
                ["error"] = code,
                ["code"] = code,
                ["message"] = message
            };
            if (!string.IsNullOrEmpty(hint)) envelope["hint"] = hint;
            if (!string.IsNullOrEmpty(next)) envelope["next"] = next;
            extend?.Invoke(envelope);
            envelope["diagnosticContext"] = BuildGxwireDiagnosticContext(ctx);
            return envelope;
        }

        /// <summary>
        /// The shared diagnostic context (SDK, KB, how to report) plus what this
        /// invocation knew when it failed. Every field is bounded; nothing from
        /// the child's pipes is repeated beyond the tail/head limits.
        /// </summary>
        private JObject BuildGxwireDiagnosticContext(GxwireRunContext ctx)
        {
            JObject context;
            try { context = _buildDiagnosticContext(); }
            catch { context = new JObject(); }

            context["tool"] = "genexus_wire";
            context["engine"] = "gxwire";
            context["action"] = ctx.Action;
            context["format"] = ctx.Format;
            context["kbRoot"] = ctx.KbRoot;
            context["cachePath"] = ctx.CachePath;
            context["cacheExists"] = ctx.CacheExists;
            if (ctx.ExePath != null) context["exePath"] = ctx.ExePath;
            if (ctx.TimeoutSeconds.HasValue) context["timeoutSeconds"] = ctx.TimeoutSeconds.Value;
            if (ctx.ElapsedMs.HasValue) context["elapsedMs"] = ctx.ElapsedMs.Value;
            if (ctx.ExitCode.HasValue) context["exitCode"] = ctx.ExitCode.Value;
            if (ctx.StderrTail != null) context["stderrTail"] = ctx.StderrTail;
            if (ctx.StdoutHead != null) context["stdoutHead"] = ctx.StdoutHead;
            if (ctx.ExceptionType != null) context["exceptionType"] = ctx.ExceptionType;
            return context;
        }

        private static readonly string[] GxwireCallerValueKeys =
        {
            "format", "dir", "kb", "target", "symbol", "name", "task", "query", "from", "source", "origin", "to", "destination"
        };

        private static string? ReadGxwireArg(JObject? args, string key)
        {
            var token = args?[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            string value = token.Type == JTokenType.String ? token.ToString() : token.ToString(Newtonsoft.Json.Formatting.None);
            value = value.Trim();
            return value.Length == 0 ? null : value;
        }

        private static int? ReadGxwireInt(JObject? args, string key)
        {
            var token = args?[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            return int.TryParse(token.ToString().Trim(), out int parsed) ? parsed : (int?)null;
        }

        private static bool? ReadGxwireBool(JObject? args, string key)
        {
            var token = args?[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            return bool.TryParse(token.ToString().Trim(), out bool parsed) ? parsed : (bool?)null;
        }

        private static readonly Regex GxwireEngineCodePattern = new Regex(
            "(?:code=\"(?<xml>[A-Za-z_][A-Za-z0-9_]*)\")|(?:\"code\"\\s*:\\s*\"(?<json>[A-Za-z_][A-Za-z0-9_]*)\")|(?:^\\s*code:\\s*(?<toon>[A-Za-z_][A-Za-z0-9_]*)\\s*$)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// gxwire reports its own refusals (SymbolNotFound, ...) on stderr in the
        /// requested format and exits 1. The code is lifted out so the caller can
        /// branch on it without parsing the tail.
        /// </summary>
        internal static string? ExtractGxwireEngineCode(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var match = GxwireEngineCodePattern.Match(text);
            if (!match.Success) return null;
            foreach (string group in new[] { "xml", "json", "toon" })
            {
                if (match.Groups[group].Success) return match.Groups[group].Value;
            }
            return null;
        }

        private static readonly Regex GxwireNodeCountPattern = new Regex(
            "Nodes:\\s*(?<nodes>\\d+)", RegexOptions.Compiled);

        internal static int? ExtractGxwireNodeCount(string? stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return null;
            var match = GxwireNodeCountPattern.Match(stderr);
            return match.Success && int.TryParse(match.Groups["nodes"].Value, out int nodes) ? nodes : (int?)null;
        }

        private static string LastNonEmptyLine(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length > 0) return line;
            }
            return string.Empty;
        }

        internal static string Head(string? text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string trimmed = text.Trim();
            if (trimmed.Length <= maxChars) return trimmed;
            return trimmed.Substring(0, maxChars) + "…";
        }

        internal static string Tail(string? text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string trimmed = text.Trim();
            if (trimmed.Length <= maxChars) return trimmed;
            return "…" + trimmed.Substring(trimmed.Length - maxChars);
        }

        internal static string? LocateGxwireExecutable()
        {
            string appDir = AppContext.BaseDirectory;
            string candidate = Path.Combine(appDir, "gxwire.exe");
            if (File.Exists(candidate)) return candidate;

            string candidatePublish = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "publish", "gxwire.exe"));
            if (File.Exists(candidatePublish)) return candidatePublish;

            string candidateRust = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "src", "gxwire", "target", "release", "gxwire.exe"));
            if (File.Exists(candidateRust)) return candidateRust;

            string candidateCwd = Path.Combine(Directory.GetCurrentDirectory(), "publish", "gxwire.exe");
            if (File.Exists(candidateCwd)) return candidateCwd;

            string candidateCwdRust = Path.Combine(Directory.GetCurrentDirectory(), "src", "gxwire", "target", "release", "gxwire.exe");
            if (File.Exists(candidateCwdRust)) return candidateCwdRust;

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string p in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string onPath = Path.Combine(p, "gxwire.exe");
                        if (File.Exists(onPath)) return onPath;
                    }
                    catch { }
                }
            }

            return null;
        }
    }
}
