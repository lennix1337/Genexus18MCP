using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public enum CompilationStage
    {
        Planning,
        Specification,
        Compilation,
        DiagnosticHarvesting,
        BindingVerification,
        Completed,
        Failed
    }

    public sealed class DiagnosticEntry
    {
        public string Severity { get; set; } = "error";
        public string Code { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int? Line { get; set; }
        public int? Column { get; set; }
    }

    public interface IEnvironmentManager
    {
        string GetActiveEnvironmentName();
        void SetActiveEnvironment(string name);
    }

    public sealed class KbServiceEnvironmentManager : IEnvironmentManager
    {
        private readonly KbService _kbService;

        public KbServiceEnvironmentManager(KbService kbService)
        {
            _kbService = kbService;
        }

        public string GetActiveEnvironmentName() => _kbService?.GetActiveEnvironment();
        public void SetActiveEnvironment(string name) => _kbService?.SetActiveEnvironment(name);
    }

    /// <summary>
    /// Scoped environment manager ensuring original environment is restored on completion or failure.
    /// </summary>
    public sealed class EnvironmentScope : IDisposable
    {
        private readonly IEnvironmentManager _envManager;
        private readonly string _originalEnv;
        private bool _disposed;

        public EnvironmentScope(IEnvironmentManager envManager, string targetEnv)
        {
            _envManager = envManager;
            if (_envManager != null)
            {
                try
                {
                    _originalEnv = _envManager.GetActiveEnvironmentName();
                    if (!string.IsNullOrWhiteSpace(targetEnv) && !string.Equals(_originalEnv, targetEnv, StringComparison.OrdinalIgnoreCase))
                    {
                        _envManager.SetActiveEnvironment(targetEnv);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[ENVIRONMENT-SCOPE] Failed switching environment to '{targetEnv}': {ex.Message}");
                }
            }
        }

        public EnvironmentScope(KbService kbService, string targetEnv)
            : this(kbService != null ? new KbServiceEnvironmentManager(kbService) : null, targetEnv)
        {
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_envManager != null && !string.IsNullOrWhiteSpace(_originalEnv))
            {
                try
                {
                    string current = _envManager.GetActiveEnvironmentName();
                    if (!string.Equals(current, _originalEnv, StringComparison.OrdinalIgnoreCase))
                    {
                        _envManager.SetActiveEnvironment(_originalEnv);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[ENVIRONMENT-SCOPE] Failed restoring environment to '{_originalEnv}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Deep Compilation Pipeline for GeneXus builds.
    /// Encapsulates environment scoping, in-process specification, log scraping,
    /// structured diagnostic harvesting, and binding verification behind a clean lifecycle interface.
    /// </summary>
    public sealed class CompilationPipeline
    {
        private readonly BuildService _buildService;
        private readonly IEnvironmentManager _envManager;

        private static readonly Regex DiagnosticPattern = new Regex(
            @"(?<file>[^\r\n\(]+)\((?<line>\d+)(?:,(?<col>\d+))?\):\s*(?<severity>error|warning)\s*(?<code>[A-Za-z0-9]+):\s*(?<msg>[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex GxSpecDiagnosticPattern = new Regex(
            @"(?:^|\r|\n)(?:(?<severity>error|warning)\s+)?(?<code>spc\d{4}|src\d{4}):\s*(?<msg>[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public CompilationPipeline()
            : this(null, (IEnvironmentManager)null)
        {
        }

        public CompilationPipeline(BuildService buildService, IEnvironmentManager envManager)
        {
            _buildService = buildService;
            _envManager = envManager;
        }

        public CompilationPipeline(BuildService buildService, KbService kbService)
            : this(buildService, kbService != null ? new KbServiceEnvironmentManager(kbService) : null)
        {
        }

        public string ExecuteBuild(string action, string target, JObject args, JObject request = null)
        {
            if (args == null) args = new JObject();
            if (request == null) request = new JObject();

            if (action == "Status")
            {
                int wait = args["wait"]?.ToObject<int?>() ?? 0;
                string since = args["since"]?.ToString();
                string until = args["until"]?.ToString();
                if (string.IsNullOrWhiteSpace(until)
                    && (args["wait_until_done"]?.ToObject<bool?>() == true
                        || request["wait_until_done"]?.ToObject<bool?>() == true))
                    until = "terminal";
                return _buildService.GetStatusWait(
                    target,
                    wait,
                    since,
                    args["page"]?.ToObject<int?>() ?? 1,
                    args["pageSize"]?.ToObject<int?>() ?? 50,
                    args["compact"]?.ToObject<bool?>() ?? false,
                    until ?? "change");
            }
            if (action == "Result") return _buildService.GetResult(
                target,
                args["page"]?.ToObject<int?>() ?? 1,
                args["pageSize"]?.ToObject<int?>() ?? 50);
            if (action == "Cancel") return _buildService.Cancel(target);

            string requestedEnv = args["environment"]?.ToString() ?? request["environment"]?.ToString();

            using (new EnvironmentScope(_envManager, requestedEnv))
            {
                bool buildDryRun = (request["dryRun"]?.ToObject<bool?>() ?? false) || (args["dryRun"]?.ToObject<bool?>() ?? false);
                if (buildDryRun)
                {
                    var includeCallees = args["includeCallees"]?.ToString();
                    if (string.IsNullOrWhiteSpace(includeCallees)) includeCallees = "transitive";
                    var cap = args["buildPlanCap"]?.ToObject<int?>() ?? 200;
                    return _buildService.BuildDryRun(
                        action,
                        target,
                        includeCallees,
                        cap,
                        includeCallers: args["callers"]?.ToObject<bool?>() ?? true,
                        callerCap: args["callerCap"]?.ToObject<int?>() ?? 0);
                }

                bool? requestedQueueLifecycle = args["queueLifecycle"]?.ToObject<bool?>()
                    ?? request["queueLifecycle"]?.ToObject<bool?>();
                bool queueLifecycle = requestedQueueLifecycle ?? true;

                if (action == "Specify") return _buildService.Specify(target, queueLifecycle);

                if (action == "CompileCheck") return _buildService.CompileCheck(
                    target,
                    args["buildPlanCap"]?.ToObject<int?>() ?? 200,
                    includeCallers: args["callers"]?.ToObject<bool?>() ?? true,
                    callerCap: args["callerCap"]?.ToObject<int?>() ?? 0,
                    queueLifecycle: queueLifecycle);

                if (action == "ReorgPreview") return _buildService.ReorgPreview(target);

                var incCallees = args["includeCallees"]?.ToString();
                var buildCap = args["buildPlanCap"]?.ToObject<int?>() ?? 200;
                if (string.IsNullOrWhiteSpace(incCallees)) incCallees = "transitive";
                bool skipFullDeploy = args["skipFullDeploy"]?.ToObject<bool?>() ?? false;
                string notifyOnFailure = args["notifyOnFailure"]?.ToString();
                bool fastIncremental = args["fastIncremental"]?.ToObject<bool?>() ?? false;
                bool fullDeploy = (args["deploy"]?.ToObject<bool?>() ?? false) || (request["deploy"]?.ToObject<bool?>() ?? false);

                string resultJson = _buildService.Build(
                    action, target, incCallees, buildCap, skipFullDeploy, notifyOnFailure,
                    fastIncremental, specifyOnly: false, compileCheck: false,
                    compileCheckCallers: null, compileCheckTruncated: false,
                    compileCheckGraphAvailable: true, fullDeploy: fullDeploy,
                    queueLifecycle: queueLifecycle);

                // Enrich with harvested diagnostics if build failed and log is present
                return EnrichWithDiagnostics(resultJson);
            }
        }

        private string EnrichWithDiagnostics(string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson)) return resultJson;
            try
            {
                var obj = JObject.Parse(resultJson);
                string status = obj["status"]?.ToString();
                if (string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    string compilerLog = obj["log"]?.ToString() ?? obj["details"]?.ToString() ?? obj["message"]?.ToString();
                    if (!string.IsNullOrEmpty(compilerLog))
                    {
                        var harvested = HarvestDiagnostics(compilerLog);
                        if (harvested != null && harvested.Count > 0)
                        {
                            var diagArr = new JArray();
                            foreach (var d in harvested)
                            {
                                diagArr.Add(new JObject
                                {
                                    ["severity"] = d.Severity,
                                    ["code"] = d.Code,
                                    ["message"] = d.Message,
                                    ["file"] = d.File,
                                    ["line"] = d.Line,
                                    ["column"] = d.Column
                                });
                            }
                            obj["diagnostics"] = diagArr;
                            obj["diagnosticCount"] = harvested.Count;
                            return obj.ToString(Newtonsoft.Json.Formatting.None);
                        }
                    }
                }
            }
            catch { }
            return resultJson;
        }

        public List<DiagnosticEntry> HarvestDiagnostics(string rawCompilerLog)
        {
            var diagnostics = new List<DiagnosticEntry>();
            if (string.IsNullOrWhiteSpace(rawCompilerLog)) return diagnostics;

            // 1. Standard MSBuild / C# compiler diagnostic pattern
            var matches = DiagnosticPattern.Matches(rawCompilerLog);
            foreach (Match m in matches)
            {
                if (!m.Success) continue;
                var entry = new DiagnosticEntry
                {
                    File = m.Groups["file"].Value.Trim(),
                    Severity = m.Groups["severity"].Value.ToLowerInvariant(),
                    Code = m.Groups["code"].Value.Trim(),
                    Message = m.Groups["msg"].Value.Trim()
                };

                if (int.TryParse(m.Groups["line"].Value, out int line)) entry.Line = line;
                if (m.Groups["col"].Success && int.TryParse(m.Groups["col"].Value, out int col)) entry.Column = col;

                diagnostics.Add(entry);
            }

            // 2. GeneXus Specification diagnostic pattern (spc/src codes)
            var gxMatches = GxSpecDiagnosticPattern.Matches(rawCompilerLog);
            foreach (Match m in gxMatches)
            {
                if (!m.Success) continue;
                string sev = m.Groups["severity"].Success
                    ? m.Groups["severity"].Value.ToLowerInvariant()
                    : "error";
                var entry = new DiagnosticEntry
                {
                    Severity = sev,
                    Code = m.Groups["code"].Value.Trim(),
                    Message = m.Groups["msg"].Value.Trim()
                };

                diagnostics.Add(entry);
            }

            return diagnostics;
        }
    }
}