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
    /// <summary>
    /// Opt-in native diagnostics after an SDK save: <c>genexus_edit validationMode=native</c> asks
    /// gx-engine (<c>kb-diagnose</c>) to parse the object the edit just saved and attaches a compact
    /// <c>nativeDiagnostics</c> block. It is advisory and fail-soft: the edit has already succeeded, so
    /// nothing here may fail, slow down past the budget, or be retried. Every problem becomes
    /// <c>status=unavailable</c> with a reason. The check is syntax-level (see the engine's
    /// <c>native_diagnostics</c> module); it is not the SDK specify pass (<c>validationMode=specify</c>).
    /// </summary>
    internal sealed partial class EngineRoutedOperations
    {
        internal const string NativeValidationMode = "native";

        /// <summary>Hard wall-clock budget for the whole diagnostics step, engine start included.</summary>
        internal const int NativeDiagnosticsBudgetMs = 2000;

        internal const int NativeDiagnosticsMaxErrors = 10;
        private const int NativeDiagnosticsMaxMessageChars = 300;
        private const string NativeDiagnosticsOperation = "genexus_edit.nativeDiagnostics";

        /// <summary>True only for a <c>genexus_edit</c> call that asked for the native pass. Costs one string compare otherwise.</summary>
        internal static bool WantsNativeDiagnostics(string? toolName, JObject? args) =>
            string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase)
            && args?["validationMode"] is { Type: JTokenType.String } mode
            && string.Equals(mode.ToString(), NativeValidationMode, StringComparison.OrdinalIgnoreCase);

        /// <summary>The block for a saved object; never throws.</summary>
        internal async Task<JObject> DiagnoseSavedObjectAsync(
            JObject? args, string? kbPath, string? kbAlias, int budgetMs, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            JObject block;
            try
            {
                block = await DiagnoseSavedObjectCoreAsync(args, kbPath, kbAlias, budgetMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[Engine] native diagnostics failed: {ex.GetType().Name}: {EngineFailureText.ForLog(ex.Message)}");
                block = NativeUnavailable("native diagnostics failed before an answer (" + ex.GetType().Name + ")");
            }
            block["elapsedMs"] = clock.ElapsedMilliseconds;
            return block;
        }

        private async Task<JObject> DiagnoseSavedObjectCoreAsync(
            JObject? args, string? kbPath, string? kbAlias, int budgetMs, CancellationToken cancellationToken)
        {
            JObject a = args ?? new JObject();
            if (a["dryRun"] is { Type: JTokenType.Boolean } dry && dry.Value<bool>())
                return NativeUnavailable("dryRun saved nothing to diagnose");
            if (a["targets"] is JArray || a["changeSet"] != null)
                return NativeUnavailable("only single-object edits are diagnosed");

            string? name = NativeObjectName(a);
            if (name == null)
                return NativeUnavailable("a single ASCII object `name` is required");
            if (string.IsNullOrWhiteSpace(kbPath))
                return NativeUnavailable("no Knowledge Base is open");
            if (!File.Exists(Path.Combine(kbPath, "knowledgebase.connection")))
                return NativeUnavailable("the engine reads native SQL Knowledge Bases (knowledgebase.connection) only");

            EngineBackendSettings settings = EngineBackendSettings.Resolve(_activeConfig());
            if (!settings.EngineEnabled)
                return NativeUnavailable("Engine.Backend=sdk disables the native engine");

            if (cancellationToken.IsCancellationRequested)
                return NativeUnavailable(CancelledReason(cancellationToken, budgetMs));

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(budgetMs);
            var ctx = new EngineRouteContext(
                settings, "udm_storage_access", NativeDiagnosticsOperation, "genexus_edit", a, kbPath, kbAlias, budget.Token);
            if (!EngineKbArgMatches(ctx, a))
                return NativeUnavailable("`kb` selects a different Knowledge Base than the session's");

            GxEngineInvocation invocation;
            try
            {
                var engineArgs = new List<string> { "--diagnostics=json", "kb-diagnose", kbPath, name };
                engineArgs.AddRange(await ActiveVersionArgumentsAsync(ctx).ConfigureAwait(false));
                invocation = await GxEngineClient.InvokeAsync(settings, engineArgs, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return NativeUnavailable(CancelledReason(cancellationToken, budgetMs));
            }

            string? versionScope = EngineActiveModel.VersionScopeRefusal(invocation);
            if (versionScope != null)
                return NativeUnavailable("the engine does not serve the KB version in force: " + EngineFailureText.Truncate(versionScope, 160));
            if (!invocation.Ok)
            {
                return NativeUnavailable(invocation.ErrorCode switch
                {
                    "EngineCancelled" or "EngineTimeout" => CancelledReason(cancellationToken, budgetMs),
                    "EngineNotFound" => "gx-engine was not found",
                    _ => $"gx-engine refused the check ({invocation.ErrorCode ?? "failed"}"
                         + (invocation.ExitCode.HasValue ? $", exit {invocation.ExitCode}" : "")
                         + "): " + EngineFailureText.Truncate(
                             EngineFailureText.Redact(invocation.StandardError).Replace("\r", " ").Replace("\n", " ").Trim(), 160),
                });
            }
            if (!TryParseEngineJson(invocation.StandardOutput, out JObject? json, out _) || json == null
                || !TryShapeNativeDiagnostics(json, name, out JObject? block))
                return NativeUnavailable("the engine answer was not recognized");
            return block!;
        }

        private static string CancelledReason(CancellationToken caller, int budgetMs) =>
            caller.IsCancellationRequested
                ? "the request was cancelled"
                : $"no answer within the {budgetMs} ms budget";

        /// <summary>The ASCII object name, or null. A leading '-' would be read as an engine flag.</summary>
        private static string? NativeObjectName(JObject args)
        {
            string? name = args["name"] is { Type: JTokenType.String } n ? n.ToString().Trim()
                : args["objectName"] is { Type: JTokenType.String } o ? o.ToString().Trim()
                : null;
            if (string.IsNullOrEmpty(name) || name.StartsWith('-') || name.Any(c => c > 0x7f || char.IsControl(c)))
                return null;
            return name;
        }

        private static JObject NativeUnavailable(string reason) =>
            new JObject { ["status"] = "unavailable", ["reason"] = reason };

        /// <summary>
        /// Validates the engine's <c>kb-diagnose</c> answer and projects it. Anything outside the contract is
        /// refused rather than approximated.
        /// </summary>
        internal static bool TryShapeNativeDiagnostics(JObject json, string requestedName, out JObject? block)
        {
            block = null;
            string? status = json["status"] is { Type: JTokenType.String } s ? s.ToString() : null;
            if (!string.Equals(json["name"]?.ToString(), requestedName, StringComparison.OrdinalIgnoreCase)) return false;
            if (status == "unavailable")
            {
                string reason = json["reason"] is { Type: JTokenType.String } r && r.ToString().Length > 0
                    ? r.ToString() : "the engine reported no reason";
                block = NativeUnavailable(EngineFailureText.Truncate(reason, 240));
                return true;
            }
            if (status != "ok" && status != "errors") return false;
            if (json["errors"] is not JArray errors) return false;
            if ((status == "ok") != (errors.Count == 0)) return false;

            var shaped = new JArray();
            foreach (JToken item in errors.Take(NativeDiagnosticsMaxErrors))
            {
                if (item is not JObject error || error["message"] is not { Type: JTokenType.String } message) return false;
                var entry = new JObject();
                foreach (string coordinate in new[] { "line", "column" })
                {
                    JToken? value = error[coordinate];
                    if (value == null) continue;
                    if (value.Type != JTokenType.Integer || value.Value<long>() < 1 || value.Value<long>() > int.MaxValue) return false;
                    entry[coordinate] = value.Value<int>();
                }
                entry["message"] = EngineFailureText.Truncate(message.ToString(), NativeDiagnosticsMaxMessageChars);
                shaped.Add(entry);
            }

            // `ok` is a syntax result, never a build result; the scope says so next to the status.
            block = new JObject { ["status"] = status, ["scope"] = "syntax" };
            if (status == "errors")
            {
                block["errors"] = shaped;
                if (errors.Count > shaped.Count) block["truncated"] = true;
                // The engine parser covers a subset of GeneXus and stops at the first error.
                block["note"] = "gx-engine parser (GeneXus subset); confirm with validationMode=specify";
            }
            return true;
        }
    }
}
