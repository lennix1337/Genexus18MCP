using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Real routing seam for the experimental gx-engine backend
    /// (docs/engine_backend.md).
    ///
    /// When <c>Engine.Backend=gxengine</c>, the parity capabilities that declare
    /// product consumers in <see cref="EngineCapabilityRegistry"/> are served by
    /// gx-engine instead of the SDK Worker (evidence-gated native routes):
    ///
    /// <list type="bullet">
    /// <item><c>udm_type_query</c> serves only a bare <c>type:</c>
    /// <c>genexus_query</c> through the engine's <c>kb-query</c> invocation.</item>
    /// <item><c>parallel_rebuild_all</c> serves
    /// <c>genexus_lifecycle.build</c> and <c>genexus_lifecycle.rebuild</c>
    /// through the engine's <c>rebuild-all</c> invocation.</item>
    /// </list>
    ///
    /// <c>genexus_read</c> and <c>genexus_edit</c> remain outside the engine
    /// under the explicit deferral policy in <see cref="DeferredOperationCapabilityIds"/>.
    /// The native read candidate is not registered until complete response and
    /// workflow parity is verified. Under <c>gxengine</c>, deferred calls are refused
    /// when <c>Engine.Fallback=error</c> and handed to the SDK otherwise;
    /// <c>auto</c> and <c>sdk</c> leave them on the SDK untouched.
    ///
    /// Each operation admits a documented, deliberately narrow argument space
    /// (see docs/engine_backend.md, "Served operations and admitted space"). A
    /// call outside that space is never approximated: with
    /// <c>Engine.Fallback=sdk</c> it falls through to the SDK path unchanged, and
    /// with <c>Engine.Fallback=error</c> it is refused with
    /// <c>EngineOperationOutsideAdmittedSpace</c>. An engine invocation that
    /// fails is treated the same way, so an engine outage can never silently
    /// change a write into something weaker.
    ///
    /// Engine-served results carry an <c>engine</c> block
    /// (<c>{ backend: "gxengine", capability, operation, fallback }</c>) so the
    /// caller can tell which backend answered.
    /// </summary>
    internal sealed partial class EngineRoutedOperations
    {
        private readonly Func<Configuration?> _activeConfig;
        private readonly Action<string> _log;
        private readonly Func<JToken, bool, string?, JObject?, JObject> _buildToolResult;

        internal EngineRoutedOperations(
            Func<Configuration?> activeConfig,
            Action<string> log,
            Func<JToken, bool, string?, JObject?, JObject> buildToolResult,
            Func<string, CancellationToken, Task<EngineActiveModel?>>? activeModel = null)
        {
            _activeConfig = activeConfig;
            _log = log;
            _buildToolResult = buildToolResult;
            _activeModel = activeModel;
        }

        /// <summary>
        /// Reads the active KB version of the Worker's SDK session for a Knowledge Base path (null when
        /// no ready Worker reports one). Every engine invocation names it, so the engine serves the
        /// Design model the SDK serves; see <see cref="EngineActiveModel"/>.
        /// </summary>
        private readonly Func<string, CancellationToken, Task<EngineActiveModel?>>? _activeModel;

        internal const string EngineOutsideSpaceCode = "EngineOperationOutsideAdmittedSpace";
        internal const string EngineInvocationFailedCode = "EngineInvocationFailed";

        /// <summary>
        /// Engine builds are serialized: two concurrent rebuild-all runs would
        /// interleave writes in the same output directory.
        /// </summary>
        private static readonly SemaphoreSlim EngineBuildGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Operations deliberately held outside the engine, mapped to the capability
        /// record that documents why (<c>udm_storage_access</c>: native MCP read parity
        /// and persistent writes are not established). This is routing policy kept apart from
        /// <see cref="EngineCapabilityStatus"/>: the record stays Partial and
        /// declares no consumer, so a deferral can never read as parity. An operation
        /// listed here is never served by the engine.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string> DeferredOperationCapabilityIds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_read"] = "udm_storage_access",
                ["genexus_edit"] = "udm_storage_access",
            };

        /// <summary>
        /// True when the deferral policy applies under <paramref name="settings"/>:
        /// only <c>Backend=gxengine</c> holds read/edit outside the engine (refusal or
        /// SDK fallback). <c>sdk</c> and <c>auto</c> never reach the engine path.
        /// </summary>
        internal static bool DeferralApplies(EngineBackendSettings settings) =>
            settings.EngineEnabled && settings.Backend != EngineBackendSettings.AutoBackend;

        /// <summary>The deferred operations in force under <paramref name="settings"/> (doctor reports these).</summary>
        internal static IReadOnlyList<string> DeferredOperationsFor(EngineBackendSettings settings) =>
            DeferralApplies(settings)
                ? DeferredOperationCapabilityIds.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

        // The admitted argument space of each served operation. `genexus_edit` has
        // none: it is deferred (DeferredOperationCapabilityIds).
        private static readonly EngineAdmittedSpace ReadSpace = new EngineAdmittedSpace(
            "read", "name", "objectName", "part", "offset", "limit", "format", "kb");

        private static readonly EngineAdmittedSpace QuerySpace = new EngineAdmittedSpace(
            "query", "query", "typeFilter", "limit", "exactMatch", "kb");

        private static readonly EngineAdmittedSpace LifecycleSpace = new EngineAdmittedSpace(
            "lifecycle", "action", "target", "mode", "callers", "includeCallees",
            "wait_until_done", "estimated_seconds", "skipFullDeploy", "kb");

        /// <summary>
        /// Maps one MCP tool call onto an operation key the registry can route.
        /// Only the operations registered as capability consumers are eligible;
        /// every other tool name (or lifecycle action) returns null and keeps its
        /// existing path.
        /// </summary>
        internal static string? EngineOperationKeyFor(string? toolName, string? lifecycleAction)
        {
            if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)) return "genexus_read";
            if (string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase)) return "genexus_query";
            if (string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase)) return "genexus_edit";
            if (string.Equals(toolName, "genexus_kb", StringComparison.OrdinalIgnoreCase)
                && string.Equals(lifecycleAction, "get_environment", StringComparison.OrdinalIgnoreCase))
                return "genexus_kb.get_environment";
            if (string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase)) return "genexus_list_objects";
            if (string.Equals(toolName, "genexus_inspect", StringComparison.OrdinalIgnoreCase)) return "genexus_inspect";
            if (string.Equals(toolName, "genexus_kb_version", StringComparison.OrdinalIgnoreCase)
                && string.Equals(lifecycleAction, "list", StringComparison.OrdinalIgnoreCase))
                return "genexus_kb_version.list";
            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(lifecycleAction, "build", StringComparison.OrdinalIgnoreCase))
                    return "genexus_lifecycle.build";
                if (string.Equals(lifecycleAction, "rebuild", StringComparison.OrdinalIgnoreCase))
                    return "genexus_lifecycle.rebuild";
            }
            return null;
        }

        /// <summary>
        /// The capability id an operation reports and is routed under: the deferral
        /// record for a deferred operation, otherwise the parity capability that
        /// declares <paramref name="operationKey"/> as a consumer; null when neither
        /// exists. The registry stays the single source of truth for served routes:
        /// routing follows it instead of duplicating the mapping here.
        /// </summary>
        internal static string? EngineCapabilityIdForOperation(string operationKey)
        {
            if (string.IsNullOrWhiteSpace(operationKey)) return null;
            if (DeferredOperationCapabilityIds.TryGetValue(operationKey, out string? deferredId)) return deferredId;
            return EngineCapabilityRegistry.All
                .Where(capability => capability.MayRouteToEngine)
                .FirstOrDefault(capability => capability.Consumers.Any(
                    consumer => string.Equals(consumer, operationKey, StringComparison.OrdinalIgnoreCase)))
                ?.Id;
        }

        /// <summary>
        /// Entry point called from the tool dispatch pipeline before the SDK
        /// index gate. Returns null when the call must continue on its existing
        /// (SDK) path; otherwise returns the complete tool result for an
        /// engine-served call.
        /// </summary>
        internal async Task<JObject?> TryDispatchAsync(
            string toolName,
            string? lifecycleAction,
            JObject? toolArgs,
            string? kbPath,
            string? kbAlias,
            CancellationToken cancellationToken)
        {
            string? operationKey = EngineOperationKeyFor(toolName, lifecycleAction);
            if (operationKey == null) return null;

            string? capabilityId = EngineCapabilityIdForOperation(operationKey);
            if (capabilityId == null) return null;

            EngineBackendSettings settings = EngineBackendSettings.Resolve(_activeConfig());
            if (DeferredOperationCapabilityIds.ContainsKey(operationKey))
            {
                // Explicit deferral policy, not an evidence-gated route: it skips the
                // parity gate (its record is Partial on purpose) and only the backend
                // switch decides whether the fail-closed read/edit dispatch is reached.
                if (!DeferralApplies(settings)) return null;
            }
            else
            {
                EngineRouteDecision decision = EngineRouter.Decide(capabilityId, settings);

                if (decision.Target == EngineRouteTarget.Reject)
                {
                    var rejected = EngineErrorPayload(
                        decision.Code ?? EngineRouter.NotParityCode,
                        decision.Message ?? $"Engine capability '{capabilityId}' is not at parity.",
                        settings, capabilityId, operationKey, null);
                    return _buildToolResult(rejected, true, toolName, toolArgs);
                }
                if (decision.Target == EngineRouteTarget.Sdk) return null;
            }

            if (cancellationToken.IsCancellationRequested)
                throw new PreDispatchCancelledException(cancellationToken);

            // No Knowledge Base resolved yet: the SDK path owns the
            // KB_CONTEXT_REQUIRED answer, so the engine path stays out of it.
            if (string.IsNullOrWhiteSpace(kbPath)) return null;

            var context = new EngineRouteContext(
                settings, capabilityId, operationKey, toolName, toolArgs, kbPath!, kbAlias, cancellationToken);

            try
            {
                switch (operationKey)
                {
                    case "genexus_read":
                        return await DispatchEngineReadAsync(context).ConfigureAwait(false);
                    case "genexus_query":
                        return await DispatchEngineQueryAsync(context).ConfigureAwait(false);
                    case "genexus_kb.get_environment":
                        return await DispatchEngineEnvironmentAsync(context).ConfigureAwait(false);
                    case "genexus_list_objects":
                        return await DispatchEngineProcedureListAsync(context).ConfigureAwait(false);
                    case "genexus_kb_version.list":
                        return await DispatchEngineVersionListAsync(context).ConfigureAwait(false);
                    case "genexus_inspect":
                        return await DispatchEngineInspectAsync(context).ConfigureAwait(false);
                    case "genexus_edit":
                        return DispatchEngineEdit(context);
                    case "genexus_lifecycle.build":
                    case "genexus_lifecycle.rebuild":
                        return await DispatchEngineLifecycleAsync(context, lifecycleAction ?? "build").ConfigureAwait(false);
                    default:
                        return null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A report-shaping failure must not replay a completed engine build
                // on the SDK. Before invocation, the configured fallback still applies.
                _log($"[Engine] routing failed for {operationKey}: {ex.GetType().Name}: {EngineFailureText.ForLog(ex.Message)}");
                if (!settings.RejectUnsupported && !context.BuildInvocationCompleted) return null;
                var failure = EngineErrorPayload(EngineInvocationFailedCode, ex.Message, settings, capabilityId, operationKey, null);
                if (context.BuildInvocationCompleted) RequireEngineBuildReconciliation(failure);
                return _buildToolResult(failure, true, toolName, toolArgs);
            }
        }

        // ------------------------------------------------------------------
        // Candidate genexus_read -> kb-read --page; not registered for production routing.
        // ------------------------------------------------------------------

        internal async Task<JObject?> DispatchEngineReadAsync(EngineRouteContext ctx)
        {
            JObject args = ctx.Args ?? new JObject();
            string? argumentRefusal = RefusalForArguments(ctx, ReadSpace);
            if (argumentRefusal != null) return EngineOutsideResult(ctx, argumentRefusal);

            string? planRefusal = EngineReadContract.TryPlan(args, out EngineReadPlan? plan);
            if (planRefusal != null || plan == null)
                return EngineOutsideResult(ctx, planRefusal ?? "the read could not be planned");

            var (json, _, executable, failure) = await InvokeActiveScopeEngineJsonAsync(
                ctx, EngineReadContract.ToEngineArguments(ctx.KbPath, plan), "kb-read").ConfigureAwait(false);
            if (json == null) return failure;

            JObject engineInfo = EngineInfoBlock(ctx, executable);
            if (!EngineReadContract.TryShape(plan, json, engineInfo, out JObject? payload, out string? shapeRefusal)
                || payload == null)
            {
                return EngineOutsideResult(ctx, shapeRefusal ?? "the engine read answer could not be shaped");
            }

            _log($"[Engine] genexus_read served by gx-engine: {plan.Name}/{plan.Part} "
                + $"offset={payload["offset"]} limit={payload["limit"]} totalLines={payload["totalLines"]}");
            return _buildToolResult(payload, false, ctx.ToolName, ctx.Args);
        }

        /// <summary>
        /// The reason a query argument set is outside the engine space: an
        /// unlisted argument, or a `kb` that is not the session's Knowledge Base.
        /// </summary>
        private static string? RefusalForArguments(EngineRouteContext ctx, EngineAdmittedSpace space)
        {
            JObject args = ctx.Args ?? new JObject();
            string? unsupportedArgument = space.RefusalFor(args);
            if (unsupportedArgument != null) return unsupportedArgument;
            return EngineKbArgMatches(ctx, args)
                ? null
                : $"`kb` selects a different Knowledge Base than the session's ('{ctx.KbAlias ?? ctx.KbPath}')";
        }

        /// <summary>
        /// Runs one query invocation and parses its JSON answer. A null JSON
        /// means the call failed: <c>Failure</c> is then the result to return
        /// (null when the fallback continues on the SDK).
        /// </summary>
        private async Task<(JObject? Json, GxEngineInvocation Invocation, JObject? Failure)> InvokeEngineJsonAsync(
            EngineRouteContext ctx, List<string> engineArgs, string command, IEnumerable<string>? versionArguments = null)
        {
            engineArgs.AddRange(versionArguments ?? await ActiveVersionArgumentsAsync(ctx).ConfigureAwait(false));
            GxEngineInvocation invocation = await GxEngineClient.InvokeAsync(
                ctx.Settings, engineArgs, ctx.CancellationToken).ConfigureAwait(false);
            string? versionScope = EngineActiveModel.VersionScopeRefusal(invocation);
            if (versionScope != null) return (null, invocation, EngineVersionScopeResult(ctx, versionScope));
            if (!invocation.Ok) return (null, invocation, EngineInvocationFailureResult(ctx, invocation));
            if (TryParseEngineJson(invocation.StandardOutput, out JObject? json, out string? parseError) && json != null)
                return (json, invocation, null);
            return (null, invocation, EngineInvocationFailureResult(ctx, GxEngineInvocation.Failure(
                invocation.Executable, "EngineOutputUnrecognized",
                $"{command} did not produce a JSON object on stdout ({parseError}).",
                stdout: invocation.StandardOutput)));
        }

        // ------------------------------------------------------------------
        // genexus_query -> kb-query
        // ------------------------------------------------------------------

        private async Task<JObject?> DispatchEngineQueryAsync(EngineRouteContext ctx)
        {
            JObject args = ctx.Args ?? new JObject();
            string? argumentRefusal = RefusalForArguments(ctx, QuerySpace);
            if (argumentRefusal != null) return EngineOutsideResult(ctx, argumentRefusal);

            string? planRefusal = EngineQueryContract.TryPlan(args, out EngineQueryPlan? plan);
            if (planRefusal != null || plan == null) return EngineOutsideResult(ctx, planRefusal ?? "the query could not be planned");

            var (json, invocation, failure) = await InvokeEngineJsonAsync(
                ctx, EngineQueryContract.ToEngineArguments(ctx.KbPath, plan), "kb-query").ConfigureAwait(false);
            if (json == null) return failure;

            if (!EngineQueryContract.TryShape(plan, json, EngineInfoBlock(ctx, invocation.Executable), out JObject? payload, out string? shapeRefusal)
                || payload == null)
                return EngineOutsideResult(ctx, shapeRefusal ?? "the engine answer could not be shaped");
            _log($"[Engine] genexus_query served by gx-engine: query='{EngineArgString(args, "query")}' ({payload["count"]}/{payload["total"]})");
            return _buildToolResult(payload, false, ctx.ToolName, ctx.Args);
        }

        // ------------------------------------------------------------------
        // genexus_edit -> kb-write (held outside the served space)
        // ------------------------------------------------------------------

        /// <summary>
        /// Explicit deferral policy for genexus_edit: holds writes outside the Gateway
        /// to safeguard IDE pattern synchronization and cross-reference integrity.
        /// </summary>
        private JObject? DispatchEngineEdit(EngineRouteContext ctx) =>
            EngineOutsideResult(ctx,
                "engine UDM writes are not persisted through Gateway genexus_edit yet to safeguard IDE pattern synchronization; the SDK path must serve `genexus_edit`");

        // ------------------------------------------------------------------
        // genexus_lifecycle build/rebuild -> rebuild-all
        // ------------------------------------------------------------------

        private async Task<JObject?> DispatchEngineLifecycleAsync(EngineRouteContext ctx, string action)
        {
            JObject args = ctx.Args ?? new JObject();
            string? unsupportedArgument = LifecycleSpace.RefusalFor(args);
            if (unsupportedArgument != null) return EngineOutsideResult(ctx, unsupportedArgument);
            if (!EngineKbArgMatches(ctx, args))
                return EngineOutsideResult(ctx, $"`kb` selects a different Knowledge Base than the session's ('{ctx.KbAlias ?? ctx.KbPath}')");
            string? target = EngineArgString(args, "target")?.Trim();
            bool targetClosure = !string.IsNullOrWhiteSpace(target);
            if (targetClosure && !IsEngineTargetClosure(args, action, target!))
                return EngineOutsideResult(ctx, "directed engine builds require action=build, mode=compile_check, callers=false and transitive callees, using an object name (not a GUID, typed selector or path)");
            string? mode = EngineArgString(args, "mode");
            if (!targetClosure && !string.IsNullOrWhiteSpace(mode))
                return EngineOutsideResult(ctx, $"mode={mode} without an admitted target is outside the engine lifecycle space");
            if (!targetClosure && (EngineArgPresent(args["includeCallees"]) || EngineArgPresent(args["callers"])))
                return EngineOutsideResult(ctx, "`includeCallees` and `callers` are outside the engine lifecycle space yet without an admitted `target`");
            if (args["skipFullDeploy"]?.ToObject<bool?>() == false)
                return EngineOutsideResult(ctx, "full deploy (`skipFullDeploy=false`) is outside the engine lifecycle space yet; the engine specifies and emits, it does not deploy");

            // The engine runs the build to completion inside the call, so it only
            // serves calls that asked for that contract: wait_until_done=true
            // (the blocking lifecycle contract) or an estimated_seconds the
            // gateway itself would have run synchronously. A bare build/rebuild
            // would otherwise change an accept-then-poll answer into a blocking
            // one, so it stays on the SDK path (or is refused under Fallback=error).
            bool waitUntilDone = args["wait_until_done"]?.ToObject<bool?>() == true;
            int? callerEstimate = args["estimated_seconds"]?.ToObject<int?>();
            int syncThresholdSeconds = _activeConfig()?.Server?.BuildSyncThresholdSeconds ?? 20;
            bool syncIntent = waitUntilDone
                || (callerEstimate.HasValue && BuildPathSelector.UseSync(callerEstimate.Value, syncThresholdSeconds));
            if (!syncIntent)
                return EngineOutsideResult(ctx, "this call would run as an accepted async job; engine builds run to completion inside the call, so pass wait_until_done=true (or an estimated_seconds below the gateway's sync threshold) to request that contract");

            // A target build must not pick up whole-KB or another target's old sources.
            string outputDir = targetClosure
                ? Path.Combine(ctx.KbPath, ".gx-engine", "build-targets",
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target!))).ToLowerInvariant())
                : Path.Combine(ctx.KbPath, ".gx-engine", "build");
            var engineArgs = new List<string>
            {
                "--diagnostics=json", "rebuild-all",
                "--kb", ctx.KbPath,
                "--output", outputDir,
            };
            if (targetClosure)
            {
                engineArgs.AddRange(new[] { "--object", target!, "--closure" });
            }
            if (string.Equals(action, "rebuild", StringComparison.OrdinalIgnoreCase))
                engineArgs.Add("--clean");

            try
            {
                await EngineBuildGate.WaitAsync(ctx.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
            {
                throw new PreDispatchCancelledException(ctx.CancellationToken);
            }
            try
            {
                if (ctx.CancellationToken.IsCancellationRequested)
                    throw new PreDispatchCancelledException(ctx.CancellationToken);
                // Sampled inside the gate: a build that queued behind another must name the version
                // active when it starts, not the one active when it was requested.
                EngineActiveModel? sampledModel = await SampleActiveModelAsync(ctx).ConfigureAwait(false);
                if (sampledModel != null) engineArgs.AddRange(sampledModel.EngineArguments());
                GxEngineInvocation invocation = await GxEngineClient.InvokeAsync(ctx.Settings, engineArgs, ctx.CancellationToken).ConfigureAwait(false);
                ctx.BuildInvocationCompleted = true;
                ctx.CancellationToken.ThrowIfCancellationRequested();
                // Exit 4: the engine refused the KB version in force before reading or writing anything,
                // so the call is outside the engine space like any other refused argument.
                string? versionScope = EngineActiveModel.VersionScopeRefusal(invocation);
                if (versionScope != null) return EngineVersionScopeResult(ctx, versionScope);
                // Exit 5 with the exact credentials-preflight contract: the engine stopped before any
                // catalog read, output or build effect, so this is the one started-process failure that
                // is proven to have changed nothing. Checked before the active-version re-read, which
                // only matters to a build that may have run.
                string? sqlCredentialsMessage = EngineSqlCredentialsPreflight.UnavailableMessage(invocation);
                if (sqlCredentialsMessage != null) return EngineSqlCredentialsUnavailableResult(ctx, action, invocation, sqlCredentialsMessage);
                // A started process may have written outputs for the sampled version; if the session's
                // active version moved or can no longer be confirmed, no report below is trustworthy.
                bool neverStarted = invocation.ErrorCode == "EngineNotFound"
                    || invocation.ErrorCode == "EngineStartFailed";
                if (!neverStarted && sampledModel != null
                    && !await ActiveModelUnchangedAsync(ctx, sampledModel).ConfigureAwait(false))
                {
                    var changed = EngineErrorPayload(
                        EngineInvocationFailedCode,
                        $"the active KB version (Design model {sampledModel.DesignModelId}, KB version {sampledModel.KbVersionId}) changed or could not be confirmed while the engine build ran; its outcome is uncertain.",
                        ctx.Settings, ctx.CapabilityId, ctx.OperationKey, invocation);
                    changed["action"] = action;
                    RequireEngineBuildReconciliation(changed);
                    return _buildToolResult(changed, true, ctx.ToolName, ctx.Args);
                }
                bool haveReport = TryParseEngineJson(invocation.StandardOutput, out JObject? report, out _)
                    && IsRecognizedEngineBuildReport(report!);
                int errorCount = (report?["errors"] as JArray)?.Count ?? 0;
                bool partial = haveReport && IsPartialEngineReport(report!, invocation.ExitCode);
                bool failed = errorCount > 0
                    || string.Equals(report?["status"]?.ToString(), "failed", StringComparison.OrdinalIgnoreCase);
                bool inconsistentReport = haveReport && !invocation.Ok && !partial && !failed;
                if (inconsistentReport) haveReport = false;

                if (invocation.Ok && haveReport && !partial && !failed)
                {
                    var payload = new JObject
                    {
                        ["status"] = "Success",
                        ["action"] = action,
                        ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
                        ["report"] = EngineBuildReportPayload(report!),
                    };
                    _log($"[Engine] genexus_lifecycle.{action} served by gx-engine");
                    return _buildToolResult(payload, false, ctx.ToolName, ctx.Args);
                }

                if (partial)
                {
                    // Exit 3: the engine built what it could, but skipped or refused some
                    // objects (web UI, no generator). A partial build is not a success, so it
                    // is reported with isError: true: a client that checks only isError must not
                    // read it as done. status "Partial" and the per-object counts say what was
                    // left out; the message does not call it a failure.
                    var partialPayload = new JObject
                    {
                        ["status"] = "Partial",
                        ["action"] = action,
                        ["error"] = "EngineBuildPartial",
                        ["message"] = EnginePartialMessage(report!),
                        ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
                        ["report"] = EngineBuildReportPayload(report!),
                    };
                    _log($"[Engine] genexus_lifecycle.{action} engine build partial");
                    return _buildToolResult(partialPayload, true, ctx.ToolName, ctx.Args);
                }

                if (haveReport)
                {
                    // The engine ran the whole pipeline and its report carries the
                    // build's errors: that is the build's answer, not an engine
                    // outage, so it is surfaced regardless of Engine.Fallback.
                    var failedPayload = new JObject
                    {
                        ["status"] = "Failed",
                        ["action"] = action,
                        ["error"] = "EngineBuildFailed",
                        ["message"] = $"the engine build finished with {errorCount} error(s).",
                        ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
                        ["report"] = EngineBuildReportPayload(report!),
                    };
                    string? stderr = EngineExcerpt(invocation.StandardError);
                    if (stderr != null) failedPayload["stderr"] = stderr;
                    _log($"[Engine] genexus_lifecycle.{action} engine build failed with {errorCount} error(s)");
                    return _buildToolResult(failedPayload, true, ctx.ToolName, ctx.Args);
                }

                // Only a proven pre-start failure is safe to replay on the SDK.
                // A running process can have built files even without a JSON report.
                string outageCode = invocation.Ok || inconsistentReport
                    ? "EngineOutputUnrecognized"
                    : (string.IsNullOrWhiteSpace(invocation.ErrorCode) ? EngineInvocationFailedCode : invocation.ErrorCode!);
                string outageMessage = inconsistentReport
                    ? "rebuild-all reported success but did not exit successfully; its outcome is uncertain."
                    : invocation.Ok ? "rebuild-all produced no recognized JSON report on stdout."
                    : invocation.ErrorMessage ?? "gx-engine rebuild failed.";
                LogEngineLifecycleFailure(ctx, action, outageCode, outageMessage, invocation, neverStarted);
                if (neverStarted && !ctx.Settings.RejectUnsupported) return null;
                var outage = EngineErrorPayload(
                    outageCode, outageMessage,
                    ctx.Settings, ctx.CapabilityId, ctx.OperationKey, invocation);
                outage["action"] = action;
                if (!neverStarted) RequireEngineBuildReconciliation(outage);
                return _buildToolResult(outage, true, ctx.ToolName, ctx.Args);
            }
            finally
            {
                EngineBuildGate.Release();
            }
        }

        // ------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------

        /// <summary>One resolved engine route for one tool call.</summary>
        internal sealed class EngineRouteContext
        {
            internal EngineRouteContext(
                EngineBackendSettings settings, string capabilityId, string operationKey,
                string toolName, JObject? args, string kbPath, string? kbAlias,
                CancellationToken cancellationToken)
            {
                Settings = settings;
                CapabilityId = capabilityId;
                OperationKey = operationKey;
                ToolName = toolName;
                Args = args;
                KbPath = kbPath;
                KbAlias = kbAlias;
                CancellationToken = cancellationToken;
            }

            internal bool BuildInvocationCompleted { get; set; }
            internal EngineBackendSettings Settings { get; }
            internal string CapabilityId { get; }
            internal string OperationKey { get; }
            internal string ToolName { get; }
            internal JObject? Args { get; }
            internal string KbPath { get; }
            internal string? KbAlias { get; }
            internal CancellationToken CancellationToken { get; }
        }

        private static bool IsEngineTargetClosure(JObject args, string action, string target)
        {
            string? callees = EngineArgString(args, "includeCallees");
            return string.Equals(action, "build", StringComparison.OrdinalIgnoreCase)
                && string.Equals(EngineArgString(args, "mode"), "compile_check", StringComparison.OrdinalIgnoreCase)
                && args["callers"]?.ToObject<bool?>() == false
                && (string.IsNullOrWhiteSpace(callees) || string.Equals(callees, "transitive", StringComparison.OrdinalIgnoreCase))
                && System.Text.RegularExpressions.Regex.IsMatch(target, @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z");
        }

        private static JObject EngineInfoBlock(EngineRouteContext ctx, string? executable)
        {
            var info = new JObject
            {
                ["backend"] = "gxengine",
                ["capability"] = ctx.CapabilityId,
                ["operation"] = ctx.OperationKey,
                ["fallback"] = ctx.Settings.Fallback,
            };
            if (!string.IsNullOrWhiteSpace(executable)) info["executable"] = executable;
            if (ctx.OperationKey.StartsWith("genexus_lifecycle.", StringComparison.Ordinal))
            {
                string? target = EngineArgString(ctx.Args ?? new JObject(), "target")?.Trim();
                info["scope"] = string.IsNullOrEmpty(target) ? "whole-kb" : "target-closure";
                if (!string.IsNullOrEmpty(target)) info["target"] = target;
            }
            return info;
        }

        private static JObject EngineErrorPayload(
            string code, string message, EngineBackendSettings settings,
            string capabilityId, string operationKey, GxEngineInvocation? invocation)
        {
            var info = new JObject
            {
                ["backend"] = "gxengine",
                ["capability"] = capabilityId,
                ["operation"] = operationKey,
                ["fallback"] = settings.Fallback,
            };
            if (invocation != null)
            {
                if (!string.IsNullOrWhiteSpace(invocation.Executable)) info["executable"] = invocation.Executable;
                if (invocation.ExitCode.HasValue) info["exitCode"] = invocation.ExitCode.Value;
                if (invocation.TimedOut) info["timedOut"] = true;
            }
            var payload = new JObject
            {
                ["error"] = code,
                ["message"] = EngineFailureText.Redact(message),
                ["engine"] = info,
            };
            if (invocation != null)
            {
                string? stderr = EngineExcerpt(invocation.StandardError);
                string? stdout = EngineExcerpt(invocation.StandardOutput);
                if (stderr != null) payload["stderr"] = stderr;
                if (stdout != null) payload["stdout"] = stdout;
            }
            if (settings.RejectUnsupported)
                payload["hint"] = "Engine.Fallback=error refuses the SDK path; fix the engine invocation, or set Engine.Fallback=sdk to serve this call through the SDK.";
            return payload;
        }

        /// <summary>
        /// Outcome for a call the engine path does not serve yet: Fallback=sdk
        /// continues on the SDK path unchanged (null), Fallback=error refuses.
        /// </summary>
        /// <summary>
        /// The engine arguments naming the SDK session's active KB version. None when no ready Worker
        /// reports one for this Knowledge Base: the engine then serves only a Knowledge Base with a single
        /// Design model and refuses any other (exit 4) before reading it.
        /// </summary>
        private async Task<IEnumerable<string>> ActiveVersionArgumentsAsync(EngineRouteContext ctx) =>
            (await SampleActiveModelAsync(ctx).ConfigureAwait(false))?.EngineArguments() ?? Array.Empty<string>();

        private async Task<EngineActiveModel?> SampleActiveModelAsync(EngineRouteContext ctx)
        {
            if (_activeModel == null) return null;
            EngineActiveModel? model;
            try
            {
                model = await _activeModel(ctx.KbPath, ctx.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
            {
                throw new PreDispatchCancelledException(ctx.CancellationToken);
            }
            catch (Exception ex)
            {
                _log($"[Engine] active KB version not read for {ctx.OperationKey}: {ex.Message}");
                return null;
            }
            if (model == null || !model.Describes(ctx.KbPath))
            {
                _log($"[Engine] {ctx.OperationKey}: no active KB version reported for '{ctx.KbPath}'; the engine serves only a KB without versions");
                return null;
            }
            _log($"[Engine] {ctx.OperationKey}: active KB version {model.KbVersionId} ('{model.KbVersionName}'), Design model {model.DesignModelId}");
            return model;
        }

        /// <summary>
        /// Whether the session still reports the Design model and KB version <paramref name="sampled"/>
        /// named to the engine. A failed read or a vanished snapshot is not confirmation.
        /// </summary>
        private async Task<bool> ActiveModelUnchangedAsync(EngineRouteContext ctx, EngineActiveModel sampled)
        {
            EngineActiveModel? current;
            try
            {
                current = await _activeModel!(ctx.KbPath, ctx.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[Engine] {ctx.OperationKey}: active KB version not re-read after the build: {ex.Message}");
                return false;
            }
            bool unchanged = current != null && current.Describes(ctx.KbPath)
                && current.DesignModelId == sampled.DesignModelId
                && current.KbVersionId == sampled.KbVersionId;
            if (!unchanged)
                _log($"[Engine] {ctx.OperationKey}: active KB version changed or vanished while the build ran");
            return unchanged;
        }

        /// <summary>A version-scope refusal of the engine (exit 4): nothing was read or written.</summary>
        private JObject? EngineVersionScopeResult(EngineRouteContext ctx, string refusal)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            return EngineOutsideResult(ctx, $"the engine does not serve the KB version in force ({refusal})");
        }

        private JObject? EngineOutsideResult(EngineRouteContext ctx, string reason)
        {
            if (!ctx.Settings.RejectUnsupported)
            {
                _log($"[Engine] {ctx.OperationKey} deferred to the SDK (outside admitted space): {EngineFailureText.ForLog(reason)}");
                return null;
            }
            string message = $"The engine path does not serve this call yet: {reason}. Engine.Fallback=error refuses the SDK path.";
            _log($"[Engine] {ctx.OperationKey} refused (outside admitted space): {EngineFailureText.ForLog(reason)}");
            return _buildToolResult(
                EngineErrorPayload(EngineOutsideSpaceCode, message, ctx.Settings, ctx.CapabilityId, ctx.OperationKey, null),
                true, ctx.ToolName, ctx.Args);
        }

        /// <summary>
        /// Outcome for a failed engine invocation: Fallback=sdk continues on the
        /// SDK path unchanged (null), Fallback=error refuses with the invocation
        /// diagnostics attached.
        /// </summary>
        private JObject? EngineInvocationFailureResult(EngineRouteContext ctx, GxEngineInvocation invocation)
        {
            // Cancellation is not an engine outage: never replay the request on the SDK.
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!ctx.Settings.RejectUnsupported) return null;
            string code = string.IsNullOrWhiteSpace(invocation.ErrorCode) ? EngineInvocationFailedCode : invocation.ErrorCode!;
            _log($"[Engine] {ctx.OperationKey} invocation failed ({code}): {invocation.ErrorMessage}");
            return _buildToolResult(
                EngineErrorPayload(code, invocation.ErrorMessage ?? "the engine invocation failed.", ctx.Settings, ctx.CapabilityId, ctx.OperationKey, invocation),
                true, ctx.ToolName, ctx.Args);
        }

        /// <summary>
        /// Parses the operation JSON printed on stdout, tolerating leading noise
        /// before the first object (the rebuild report is emitted through the
        /// same stream).
        /// </summary>
        internal static bool TryParseEngineJson(string stdout, out JObject? json, out string? error)
        {
            json = null;
            error = null;
            if (string.IsNullOrWhiteSpace(stdout))
            {
                error = "stdout was empty";
                return false;
            }
            string trimmed = stdout.Trim();
            int firstBrace = trimmed.IndexOf('{');
            if (firstBrace < 0)
            {
                error = "no JSON object found on stdout";
                return false;
            }
            try
            {
                json = JObject.Parse(firstBrace == 0 ? trimmed : trimmed.Substring(firstBrace));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Compact projection of an invocation stream for a payload field.</summary>
        private static string? EngineExcerpt(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return EngineFailureText.Truncate(EngineFailureText.Redact(text).Trim(), 800);
        }

        /// <summary>
        /// One bounded, credential-masked log line for an engine lifecycle failure that is neither a
        /// build report nor a success, so the failing stage is visible without the MCP client's payload.
        /// </summary>
        private void LogEngineLifecycleFailure(
            EngineRouteContext ctx, string action, string code, string message,
            GxEngineInvocation invocation, bool neverStarted)
        {
            string outcome = neverStarted
                ? (ctx.Settings.RejectUnsupported ? "refused (Engine.Fallback=error)" : "continuing on the SDK (engine never started)")
                : "reconciliation required, no SDK replay";
            _log($"[Engine] genexus_lifecycle.{action} engine failed ({code}, exit {(invocation.ExitCode?.ToString() ?? "none")}, timedOut={invocation.TimedOut}): {outcome}. "
                + $"message: {EngineFailureText.ForLog(message)}; stderr: {EngineFailureText.ForLog(invocation.StandardError)}");
        }

        /// <summary>
        /// Outcome for the engine's credentials preflight refusal. Nothing was read, emitted or built, so no
        /// reconciliation is owed: Fallback=sdk serves the call on the SDK (null), Fallback=error refuses it
        /// with the engine's own actionable message.
        /// </summary>
        private JObject? EngineSqlCredentialsUnavailableResult(
            EngineRouteContext ctx, string action, GxEngineInvocation invocation, string engineMessage)
        {
            if (!ctx.Settings.RejectUnsupported)
            {
                _log($"[Engine] genexus_lifecycle.{action} {EngineSqlCredentialsPreflight.Category}: {EngineFailureText.ForLog(engineMessage)}; no KB read or build started, continuing on the SDK (Engine.Fallback=sdk)");
                return null;
            }
            _log($"[Engine] genexus_lifecycle.{action} {EngineSqlCredentialsPreflight.Category}: {EngineFailureText.ForLog(engineMessage)}; no KB read or build started, refused (Engine.Fallback=error)");
            var refusal = EngineErrorPayload(
                EngineSqlCredentialsPreflight.ErrorCode, engineMessage,
                ctx.Settings, ctx.CapabilityId, ctx.OperationKey, invocation);
            refusal["action"] = action;
            refusal["hint"] = "The engine stopped before reading the Knowledge Base or building anything, so nothing changed and the call can be repeated. "
                + "Set Engine.Fallback=sdk to serve this call through the SDK, or Engine.Backend=sdk to turn the engine off.";
            return _buildToolResult(refusal, true, ctx.ToolName, ctx.Args);
        }

        private static JObject EngineBuildReportPayload(JObject report)
        {
            var payload = new JObject();
            foreach (string key in new[]
            {
                "total_ms", "catalog_load_ms", "read_ms", "spec_ms", "emit_ms", "compile_ms",
                "total_objects", "procedures_count", "transactions_count", "sdts_count", "webpanels_count",
            })
            {
                if (report[key] != null) payload[key] = report[key];
            }
            if (report["emitted_files"] is JArray files) payload["emitted_file_count"] = files.Count;
            foreach (string key in new[] { "errors", "warnings" })
            {
                if (report[key] is JArray list && list.Count > 0) payload[key] = list;
            }
            // Status and the per-object counts: a partial run is only honest if the caller
            // can see what was skipped. Copied through as the engine reports them.
            foreach (string key in new[]
            {
                "status", "generated_count", "compiled_count", "skipped_count",
                "not_generated_count", "refused_count",
            })
            {
                if (report[key] != null) payload[key] = report[key];
            }
            foreach (string key in new[] { "skipped_objects", "not_generated_objects", "refused_objects" })
            {
                if (report[key] is JArray list && list.Count > 0) payload[key] = list;
            }
            return payload;
        }

        private static void RequireEngineBuildReconciliation(JObject payload)
        {
            payload["reconciliationRequired"] = true;
            payload["mutationOutcomeUnknown"] = true;
            payload["hint"] = "Inspect the operation and generated outputs before retrying; SDK fallback cannot safely replay this build.";
        }

        private static bool IsRecognizedEngineBuildReport(JObject report)
        {
            if (report["errors"] != null && !(report["errors"] is JArray)) return false;
            JToken? status = report["status"];
            if (status != null)
            {
                if (status.Type != JTokenType.String) return false;
                string value = status.ToString();
                if (value.Equals("complete", StringComparison.OrdinalIgnoreCase))
                    return report["errors"] is JArray;
                return value.Equals("partial", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("failed", StringComparison.OrdinalIgnoreCase);
            }
            // Older reports have no status; require their pipeline fields rather
            // than treating an arbitrary JSON object as a completed build.
            return report["errors"] is JArray
                && report["emitted_files"] is JArray
                && (report["total_ms"]?.Type == JTokenType.Integer
                    || report["total_ms"]?.Type == JTokenType.Float);
        }

        /// <summary>
        /// A rebuild-all run that exited 3 and reported no errors: partial by the engine's own
        /// contract. The report's status is the authority when present; the exit code is the
        /// fallback for an older engine that does not print it.
        /// </summary>
        private static bool IsPartialEngineReport(JObject report, int? exitCode)
        {
            if (report["errors"] is JArray errors && errors.Count > 0) return false;
            if (report["status"] is JValue status && status.Type == JTokenType.String)
                return string.Equals(status.ToString(), "partial", StringComparison.OrdinalIgnoreCase);
            return exitCode == 3;
        }

        private static string EnginePartialMessage(JObject report)
        {
            int skipped = report["skipped_count"]?.Value<int?>() ?? 0;
            int notGenerated = report["not_generated_count"]?.Value<int?>() ?? 0;
            int refused = report["refused_count"]?.Value<int?>() ?? 0;
            return $"the engine build is partial: {skipped} web UI object(s) skipped, " +
                   $"{notGenerated} without a generator, {refused} refused; nothing was reported as built for them.";
        }

        private static bool EngineKbArgMatches(EngineRouteContext ctx, JObject args)
        {
            string? kbArg = EngineArgString(args, "kb");
            if (string.IsNullOrWhiteSpace(kbArg)) return true;
            string candidate = kbArg!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var identifiers = new List<string?>
            {
                ctx.KbAlias,
                ctx.KbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFileName(ctx.KbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            };
            return identifiers.Any(identifier =>
                !string.IsNullOrWhiteSpace(identifier)
                && string.Equals(identifier!.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool EngineArgPresent(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.String) return !string.IsNullOrWhiteSpace(token.ToString());
            if (token is JArray array) return array.Count > 0;
            if (token is JObject obj) return obj.Count > 0;
            return true;
        }

        internal static string? EngineArgString(JObject? args, string name)
        {
            JToken? token = args?[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.ToString();
        }
    }
}
