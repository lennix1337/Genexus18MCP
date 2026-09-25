using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;


namespace GxMcp.Gateway
{
    partial class Program
    {

        internal static async Task<JObject?> ProcessMcpRequest(
            JObject request,
            string sessionId = "stdio",
            bool sessionContextEnabled = true,
            CancellationToken transportCancellation = default,
            bool taskScopeEnabled = true)
        {
            return await ProcessMcpRequestCore(
                request, sessionId, sessionContextEnabled, transportCancellation, taskScopeEnabled)
                .ConfigureAwait(false);
        }

        private static JObject BuildStableKbContextError(string code, string message)
        {
            string hint = string.Equals(code, "KB_LEASE_EXPIRED", StringComparison.OrdinalIgnoreCase)
                ? "The session's KB lease expired. Run genexus_kb action=select for the intended alias to create a fresh context, then retry."
                : string.Equals(code, "KB_LEASE_INVALID", StringComparison.OrdinalIgnoreCase)
                    ? "The session's KB lease is invalid for this context. Run genexus_kb action=select for the intended alias to create a fresh context, then retry."
                    : string.Equals(code, "KB_NOT_OWNED", StringComparison.OrdinalIgnoreCase)
                        ? "This session does not own an active KB lease. Run genexus_kb action=select for the intended alias, or pass kb explicitly."
                        : "Select and open a KB in this session before retrying the stateful operation.";
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["hint"] = hint
                }
            };
        }

        private static JObject? ValidateCurrentSessionLease(string sessionId)
        {
            if (!_sessionKbContexts.TryGetSnapshot(sessionId, out var snapshot) || snapshot == null)
                return BuildStableKbContextError("KB_CONTEXT_REQUIRED",
                    "This stateful operation requires an opened and selected KB context for the current session.");
            if (snapshot.Lease == null)
                return BuildStableKbContextError("KB_NOT_OWNED",
                    "This stateful operation requires an active KB lease owned by the current session.");

            try
            {
                // Keep a selected session alive while it is actively used. The
                // registry intentionally keeps the same token/generation, so the
                // immutable session snapshot remains valid after renewal.
                var renewal = _kbLeases.Renew(snapshot.Lease.Token, snapshot.OwnerScopeId, TimeSpan.FromMinutes(10));
                if (renewal.Status == KbUseLeaseOperationStatus.Success && renewal.Lease != null)
                    _sessionKbContexts.RefreshLease(sessionId, renewal.Lease);
                _kbLeases.Validate(snapshot.Lease.Token, snapshot.OwnerScopeId, snapshot.KbId,
                    snapshot.ContextGeneration, snapshot.Lease.Identity);
                return null;
            }
            catch (KbLeaseValidationException ex)
            {
                string stableCode = ex.Code switch
                {
                    "KB_CONTEXT_REQUIRED" => "KB_CONTEXT_REQUIRED",
                    "KB_NOT_OWNED" => "KB_NOT_OWNED",
                    "KB_LEASE_INVALID" => "KB_LEASE_INVALID",
                    "KB_LEASE_EXPIRED" => "KB_LEASE_EXPIRED",
                    _ => "KB_LEASE_INVALID"
                };
                return BuildStableKbContextError(stableCode, ex.Message);
            }
        }

        private static async Task<JObject?> ProcessMcpRequestCore(
            JObject request,
            string sessionId = "stdio",
            bool sessionContextEnabled = true,
            CancellationToken transportCancellation = default,
            bool taskScopeEnabled = true)
        {
            string? method = request["method"]?.ToString();
            var idToken = request["id"];
            _currentKb.Value = null;
            _currentSessionContext.Value = null;
            _currentOperationRequiresOwner.Value = false;
            _currentExplicitKb.Value = false;

            // Resource subscriptions are stateful protocol operations. Route them
            // before McpRouter's static discovery handler so an ACK is only issued
            // after the URI is validated and attached to the creating HTTP session.
            var subscriptionResponse = McpSubscriptionProtocol.Handle(request, sessionId, _httpSessions, GetCurrentOwnership(sessionId));
            if (subscriptionResponse != null) return subscriptionResponse;

            // Protocol-level methods and gateway-owned resources must not depend on
            // KB resolution. This keeps initialize/discovery usable before any KB
            // is opened and avoids turning a static resource read into KB_AMBIGUOUS.
            var mcpResponse = McpRouter.Handle(request);
            if (mcpResponse is McpRouterError routerError)
            {
                var error = new JObject
                {
                    ["code"] = routerError.Code,
                    ["message"] = routerError.Message
                };
                if (routerError.Data != null) error["data"] = routerError.Data.DeepClone();

                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = error
                };
            }
            if (mcpResponse != null)
            {
                if (string.Equals(method, "initialize", StringComparison.OrdinalIgnoreCase)
                    && sessionContextEnabled)
                {
                    InitializeSessionKbContext(sessionId);
                    TriggerWorkerWarmupOnce();
                    TriggerIndexBootstrapOnce();
                    UpdateNotifier.TriggerOnce();
                }
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = idToken?.DeepClone(), ["result"] = JToken.FromObject(mcpResponse) };
            }

            // MCP tasks extension: route task handles before KB resolution. This keeps
            // status/cancel responsive while the worker is saturated and enforces the
            // creating session as the task's ownership boundary.
            var taskResponse = McpTasksProtocol.Handle(request, sessionId, JobRegistry, taskScopeEnabled, GetCurrentOwnership(sessionId));
            if (taskResponse != null) return taskResponse;

            // Reject removed tools early with JSON-RPC -32601 + structured `data`.
            // This is gateway-owned and must not require a KB just to explain that
            // the client should use the replacement tool.
            if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
            {
                string? earlyToolName = (request["params"] as JObject)?["name"]?.ToString();
                if (!string.IsNullOrEmpty(earlyToolName) &&
                    RemovedToolsRegistry.Map.TryGetValue(earlyToolName, out var removedInfo))
                {
                    return new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idToken?.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32601,
                            ["message"] = $"Method not found: {earlyToolName}",
                            ["data"] = new JObject
                            {
                                ["replacedBy"] = removedInfo.ReplacedBy,
                                ["argHint"] = removedInfo.ArgHint
                            }
                        }
                    };
                }
            }

            // Resolve KB once per request and stash in AsyncLocal so SendWorkerCommandAsync routes correctly.
            // Meta-tools (whoami, logs, doc, worker_reload, kb) don't address a specific KB and
            // must not trigger KB_AMBIGUOUS when several KBs are open.
            if (_kbResolver != null && _workerPool != null)
            {
                bool isMetaTool = false;
                string? toolNameForResolver = null;
                if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
                {
                    toolNameForResolver = (request["params"] as JObject)?["name"]?.ToString();
                    isMetaTool = !string.IsNullOrEmpty(toolNameForResolver) && IsMetaTool(toolNameForResolver);
                }

                bool gatewayMetricsStatusCall = string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(toolNameForResolver, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(((request["params"] as JObject)?["arguments"] as JObject)?["action"]?.ToString(), "status", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(((request["params"] as JObject)?["arguments"] as JObject)?["target"]?.ToString(), "gateway:metrics", StringComparison.OrdinalIgnoreCase);
                bool statefulMetaTool = !gatewayMetricsStatusCall
                    && string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase)
                    && OperationClassifier.RequiresSessionLease(
                        toolNameForResolver,
                        (request["params"] as JObject)?["arguments"] as JObject);
                var resolverArgs = (request["params"] as JObject)?["arguments"] as JObject;
                string resolverAction = resolverArgs?["action"]?.ToString() ?? string.Empty;
                bool kbEnvironmentTool = OperationClassifier.IsKbEnvironmentAction(toolNameForResolver, resolverAction);
                bool kbEnvironmentMutation = OperationClassifier.IsKbEnvironmentMutation(toolNameForResolver, resolverAction);
                bool explicitReadOnlyMetaKb = !string.IsNullOrWhiteSpace(resolverArgs?["kb"]?.ToString() ?? resolverArgs?["kbAlias"]?.ToString())
                    && OperationClassifier.IsKbScopedReadMetaTool(toolNameForResolver);
                statefulMetaTool |= kbEnvironmentMutation;
                bool needsKbResolution = !gatewayMetricsStatusCall
                    && ((string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase) && (!isMetaTool || statefulMetaTool))
                    || kbEnvironmentTool
                    || explicitReadOnlyMetaKb
                    || (string.Equals(method, "resources/read", StringComparison.OrdinalIgnoreCase)
                        && McpRouter.ConvertResourceCall(request) != null));

                if (needsKbResolution)
                {
                    try
                    {
                        string? kbArg = null;
                        if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
                        {
                            var paramsObj = request["params"] as JObject;
                            var argsObj = paramsObj?["arguments"] as JObject;
                            kbArg = argsObj?["kb"]?.ToString() ?? argsObj?["kbAlias"]?.ToString() ?? argsObj?["alias"]?.ToString();
                            // Strip `kb` from worker-bound args (worker is single-KB scoped),
                            // but keep it for gateway-only reload so lease bypass and the
                            // explicit target remain available to the orchestrator below.
                            if (!string.Equals(toolNameForResolver, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase))
                            {
                                argsObj?.Remove("kb");
                                argsObj?.Remove("kbAlias");
                            }
                        }
                        else if (string.Equals(method, "resources/read", StringComparison.OrdinalIgnoreCase))
                        {
                            // A modern subscription can return a KB-qualified
                            // `resourceUri`. Resolve that alias explicitly so a
                            // same-named resource in another open KB is never read
                            // by fallback selection.
                            McpRouter.TryGetScopedResourceKb(request, out kbArg);
                        }
                        string? sessionDefaultAlias = null;
                        if (sessionContextEnabled)
                        {
                            TryGetSessionSelectedKb(sessionId, out sessionDefaultAlias);
                        }
                        _currentKb.Value = _kbResolver.Resolve(
                            kbArg,
                            _workerPool.ListOpen(),
                            _workerPool.ListKnown(),
                            sessionDefaultAlias,
                            out _);
                        _currentExplicitKb.Value = !string.IsNullOrWhiteSpace(kbArg);
                        SessionKbContextStore.Snapshot? sessionSnapshot = null;
                        if (sessionContextEnabled)
                            _sessionKbContexts.TryGetSnapshot(sessionId, out sessionSnapshot);
                        if (sessionSnapshot?.Lease != null
                            && _currentKb.Value != null
                            && (string.Equals(sessionSnapshot.KbId, _currentKb.Value.KbId, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(sessionSnapshot.KbId, _currentKb.Value.NormalizedAlias, StringComparison.OrdinalIgnoreCase)))
                        {
                            var renewal = _kbLeases.Renew(
                                sessionSnapshot.Lease.Token,
                                sessionSnapshot.OwnerScopeId,
                                TimeSpan.FromMinutes(10));
                            if (renewal.Status == KbUseLeaseOperationStatus.Success && renewal.Lease != null)
                            {
                                _sessionKbContexts.RefreshLease(sessionId, renewal.Lease);
                                sessionSnapshot = new SessionKbContextStore.Snapshot(
                                    sessionSnapshot.OwnerScopeId,
                                    sessionSnapshot.KbId,
                                    sessionSnapshot.ContextGeneration,
                                    renewal.Lease);
                            }
                            else if (_currentExplicitKb.Value && renewal.Status == KbUseLeaseOperationStatus.Expired)
                            {
                                long generation = sessionSnapshot.ContextGeneration + 1;
                                string identity = sessionSnapshot.Lease.Identity ?? (_currentKb.Value.Path ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
                                string canonicalAlias = sessionSnapshot.KbId ?? CanonicalizeKbAlias(_currentKb.Value.Alias);
                                var freshLease = _kbLeases.Open(sessionId, canonicalAlias, generation, identity, "session-" + generation, TimeSpan.FromMinutes(10));
                                _sessionKbContexts.Set(sessionId, _currentKb.Value.Alias, canonicalAlias, freshLease);
                                sessionSnapshot = new SessionKbContextStore.Snapshot(
                                    sessionId,
                                    canonicalAlias,
                                    generation,
                                    freshLease);
                            }
                        }
                        _currentSessionContext.Value = sessionSnapshot;
                        var resolvedArgs = (request["params"] as JObject)?["arguments"] as JObject;
                        _currentOperationRequiresOwner.Value = !string.Equals(_activeConfig?.Environment?.ResolutionPolicy, "legacy", StringComparison.OrdinalIgnoreCase)
                            && ((OperationClassifier.RequiresSessionLease(toolNameForResolver ?? string.Empty, resolvedArgs)
                                && !_currentExplicitKb.Value)
                                || kbEnvironmentMutation);
                    }
                    catch (KbResolutionException ex)
                    {
                        if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase)
                            && (OperationClassifier.RequiresSessionLease(
                                    toolNameForResolver,
                                    (request["params"] as JObject)?["arguments"] as JObject)
                                || OperationClassifier.IsKbEnvironmentMutation(
                                    toolNameForResolver,
                                    ((request["params"] as JObject)?["arguments"] as JObject)?["action"]?.ToString()))
                            && (string.Equals(ex.Code, "KB_CONTEXT_REQUIRED", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ex.Code, "KB_NOT_OWNED", StringComparison.OrdinalIgnoreCase)))
                        {
                            return BuildToolTextResponse(
                                idToken,
                                BuildStableKbContextError(ex.Code, ex.Message),
                                isError: true,
                                toolName: toolNameForResolver,
                                toolArgs: (request["params"] as JObject)?["arguments"] as JObject,
                                payloadOwned: true);
                        }
                        // Friction 2026-05-22 #63: surface suggested_next_step on KB_AMBIGUOUS
                        // (and KB_NOT_FOUND) so the agent knows to retry with kb=<alias>.
                        var dataObj = new JObject
                        {
                            ["code"] = ex.Code,
                            ["openKbs"] = JArray.FromObject(_workerPool!.ListOpen().Select(k => k.Alias))
                        };
                        var nextStep = McpRouter.AttachSuggestedNextStep(
                            new JObject { ["code"] = ex.Code, ["message"] = ex.Message });
                        if (nextStep != null) dataObj["suggested_next_step"] = nextStep;
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = new JObject
                            {
                                ["code"] = -32602,
                                ["message"] = ex.Message,
                                ["data"] = dataObj
                            }
                        };
                    }
                }
                else
                {
                    // Meta-tools still strip any stray `kb` arg (cosmetic) and leave _currentKb null.
                    var argsObj = (request["params"] as JObject)?["arguments"] as JObject;
                    argsObj?.Remove("kb");
                    _currentKb.Value = null;
                }
            }

            // Tool Calls
            if (method == "tools/call")
            {
                // ... (logic handled below) ...
            }

            // Resource Calls
            if (method == "resources/read")
            {
                var rawWorkerCmd = McpRouter.ConvertResourceCall(request);
                var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
                if (workerCmd != null)
                {
                    workerCmd["client"] = "mcp";
                    return await SendWorkerCommandAsync(
                        workerCmd,
                        60000,
                        $"Timeout waiting for resource: {request["params"]?["uri"]}",
                        resultObj =>
                        {
                            if (resultObj["error"] != null || string.Equals(resultObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
                            {
                                // v2.8.0: error details live under error.{message,hint} in the canonical envelope.
                                var errBlock = resultObj["error"] as JObject;
                                string errorMsg = errBlock?["message"]?.ToString()
                                    ?? errBlock?["hint"]?.ToString()
                                    ?? resultObj["error"]?.ToString()
                                    ?? "Unknown error reading resource";

                                // Enrich with suggestions if available (from HealingService)
                                string? suggestion = resultObj["suggestion"]?.ToString() ?? errBlock?["hint"]?.ToString();
                                if (!string.IsNullOrEmpty(suggestion) && !errorMsg.Contains(suggestion)) errorMsg += "\n" + suggestion;

                                string? tip = resultObj["actionable_tip"]?.ToString();
                                if (!string.IsNullOrEmpty(tip)) errorMsg += "\nTip: " + tip;

                                return new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = idToken?.DeepClone(),
                                    ["error"] = JToken.FromObject(new { code = -32603, message = $"GeneXus MCP Worker error: {errorMsg}" })
                                };
                            }

                            // v2.8.0: tool payload is under result.source; fall back to result as string.
                            var resultPayload = resultObj["result"] as JObject;
                            var content = resultPayload?["source"]?.ToString()
                                ?? resultObj["result"]?.ToString()
                                ?? "";
                            return new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = idToken?.DeepClone(),
                                    ["result"] = JToken.FromObject(new
                                    {
                                        resultType = "complete",
                                        // Object parts are KB-local and can change after any
                                        // write; advertise a non-cacheable private resource
                                        // response instead of letting clients retain stale text.
                                        ttlMs = 0,
                                        cacheScope = "private",
                                        contents = new[]
                                        {
                                        new
                                        {
                                            uri = request["params"]?["uri"]?.ToString(),
                                            mimeType = "text/plain",
                                            text = content
                                        }
                                    }
                                })
                            };
                        },
                        (_, correlationId) => new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32000, message = "GeneXus MCP Worker timed out reading resource.", correlationId })
                        },
                        toolName: "resources/read",
                        toolArgs: request["params"] as JObject,
                        trackOperation: false);
                }

                // A resource URI is a valid MCP method even when it is not backed
                // by a worker command. Return the protocol's invalid-params error
                // instead of falling through to the generic method-not-found path.
                string missingResourceUri = request["params"]?["uri"]?.ToString() ?? string.Empty;
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = new JObject
                    {
                        ["code"] = -32602,
                        ["message"] = "Resource not found.",
                        ["data"] = new JObject { ["uri"] = missingResourceUri }
                    }
                };
            }

            // Tool Calls (Actual logic)
            if (method == "tools/call")
            {
                var paramsObj = request["params"] as JObject;
                string toolName = paramsObj?["name"]?.ToString() ?? "";
                var args = paramsObj?["arguments"] as JObject;

                // Soft-alias rewrite for consolidated umbrella tools. Runs here (before
                // the gateway-only handlers below AND before McpRouter.ConvertToolCall
                // downstream) so every code path sees the post-rewrite name and args.
                // Default ON; opt out with GXMCP_LEGACY_TOOL_ALIASES=0.
                if (!string.IsNullOrEmpty(toolName)
                    && !Program.LegacyToolAliasesDisabledCached()
                    && McpRouter.TryRewriteLegacyTool(toolName, args, out var rewrittenName, out var rewrittenArgs))
                {
                    toolName = rewrittenName;
                    args = rewrittenArgs;
                    if (paramsObj != null)
                    {
                        paramsObj["name"] = rewrittenName;
                        paramsObj["arguments"] = rewrittenArgs;
                    }
                }

                if (!string.IsNullOrEmpty(toolName))
                {
                    string activeToolProfile = ToolProfileFilter.ResolveActiveProfile(_activeConfig?.Server?.ToolProfile);
                    JObject? profileError = ToolProfileFilter.GetToolNotInProfileError(activeToolProfile, toolName);
                    if (profileError != null)
                    {
                        return BuildToolTextResponse(idToken, profileError, isError: true,
                            toolName: toolName, toolArgs: args, payloadOwned: true);
                    }
                }

                // Gateway-side schema pre-validation: reject malformed args immediately,
                // before forwarding to the worker. Saves an STA round-trip and gives LLM
                // clients faster feedback on typos / missing required fields.
                if (!string.IsNullOrEmpty(toolName))
                {
                    var validationResult = GatewayArgsValidator.Validate(toolName, args);
                    if (!validationResult.Ok)
                    {
                        var firstViolation = validationResult.Violations[0];
                        string hint = firstViolation.Actual == "missing"
                            ? $"Required field '{firstViolation.Path}' is missing — expected {firstViolation.Expected}."
                            : firstViolation.Actual == "unknown argument"
                                ? $"Unknown argument '{firstViolation.Path}'."
                                : $"Field '{firstViolation.Path}': expected {firstViolation.Expected}, got {firstViolation.Actual}.";
                        if (!string.IsNullOrEmpty(firstViolation.Suggestion))
                        {
                            hint += $" Did you mean '{firstViolation.Suggestion}'?";
                        }

                        var violationsArr = new Newtonsoft.Json.Linq.JArray(
                            validationResult.Violations.Select(v =>
                            {
                                var vo = new JObject
                                {
                                    ["path"] = v.Path,
                                    ["expected"] = v.Expected,
                                    ["actual"] = v.Actual
                                };
                                if (!string.IsNullOrEmpty(v.Suggestion)) vo["suggestion"] = v.Suggestion;
                                return vo;
                            }));

                        // Terse mode: keep the violation list (that's the actionable part)
                        // and drop the repeated hint/nextSteps scaffolding — an agent that
                        // already saw one full InvalidArgs envelope doesn't need the
                        // "call genexus_orient" pointer re-shipped on every retry.
                        var invalidArgsPayload = TerseResponsesEnabledCached()
                            ? new JObject
                            {
                                ["status"] = "error",
                                ["error"] = new JObject
                                {
                                    ["code"] = "InvalidArgs",
                                    ["message"] = $"Arguments for tool '{toolName}' failed schema validation.",
                                    ["hint"] = hint,
                                    ["violations"] = violationsArr
                                }
                            }
                            : new JObject
                        {
                            ["status"] = "error",
                            ["error"] = new JObject
                            {
                                ["code"] = "InvalidArgs",
                                ["message"] = $"Arguments for tool '{toolName}' failed schema validation.",
                                ["hint"] = hint,
                                ["nextSteps"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["tool"] = "genexus_orient",
                                        ["args"] = new JObject(),
                                        ["why"] = "Lists each tool's input schema."
                                    }
                                },
                                ["violations"] = violationsArr
                            }
                        };

                        return BuildToolTextResponse(idToken, invalidArgsPayload, isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
                    }
                }

                // Reject stateful calls before any gateway handler can select a
                // process-wide worker. Stateless recipe/catalog reads remain global.
                bool gatewayMetricsStatusCallAtDispatch =
                    string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(args?["action"]?.ToString(), "status", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(args?["target"]?.ToString(), "gateway:metrics", StringComparison.OrdinalIgnoreCase);
                if ((!gatewayMetricsStatusCallAtDispatch
                        && OperationClassifier.RequiresSessionLease(toolName, args)
                        && !_currentExplicitKb.Value
                        || (!gatewayMetricsStatusCallAtDispatch
                            && OperationClassifier.IsKbEnvironmentMutation(toolName, args?["action"]?.ToString())))
                    && !(string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase)
                        && CanReloadWithoutLease(args))
                    && !string.Equals(_activeConfig?.Environment?.ResolutionPolicy, "legacy", StringComparison.OrdinalIgnoreCase))
                {
                    var ownershipError = ValidateCurrentSessionLease(sessionId);
                    if (ownershipError != null)
                        return BuildToolTextResponse(idToken, ownershipError, isError: true,
                            toolName: toolName, toolArgs: args, payloadOwned: true);
                }

                // Auto-inject 'type' when the LLM omits it but 'name' resolves to a
                // unique object in the cached index.  Runs AFTER schema pre-validation so
                // we never inject into a known-bad call.  Mutates args in-place; the
                // injectedType local is checked after dispatch to annotate the response.
                string? _autoInjectedType = null;
                if (!string.IsNullOrEmpty(toolName) && args != null
                    && AutoTypeInjector.TryInject(_currentKb.Value?.NormalizedAlias ?? "", toolName, args, out var _ait))
                {
                    _autoInjectedType = _ait;
                    // Keep paramsObj["arguments"] in sync (same ref, but be explicit)
                    if (paramsObj != null) paramsObj["arguments"] = args;
                }

                if (string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleWorkerReloadToolAsync(idToken, toolName, args).ConfigureAwait(false);
                }

                var gatewayToolResponse = await HandleGatewayToolAsync(idToken, toolName, args, sessionId, sessionContextEnabled).ConfigureAwait(false);
                if (gatewayToolResponse != null)
                {
                    return gatewayToolResponse;
                }

                var lifecycleGatewayResponse = await HandleLifecycleGatewayInterceptsAsync(
                    idToken, toolName, args, request, sessionId, transportCancellation).ConfigureAwait(false);
                if (lifecycleGatewayResponse != null)
                {
                    return lifecycleGatewayResponse;
                }

                // Idempotency middleware wraps the rest of the tool dispatch
                // Scope cache by the resolved KB so independent KBs don't share idempotency.
                string activeKbPath = _currentKb.Value?.Path ?? _activeConfig?.Environment?.KBPath ?? "";
                string? idempotencyModelScope = null;
                string? idempotencyEnvironmentScope = null;
                var idempotencyKb = _currentKb.Value;
                if (idempotencyKb?.IsEnvCacheFresh == true)
                {
                    idempotencyModelScope = idempotencyKb.ActiveEnvironmentVersion;
                    idempotencyEnvironmentScope = idempotencyKb.ActiveEnvironment;
                }
                var idempotencyMiddleware = new IdempotencyMiddleware(
                    _idempotencyCache,
                    activeKbPath,
                    idempotencyModelScope,
                    idempotencyEnvironmentScope);
                var toolCallParams = request["params"] as JObject ?? new JObject();


                JObject toolInnerResult;
                try
                {
                    toolInnerResult = await idempotencyMiddleware.Invoke(
                        toolCallParams,
                        tcParams => DispatchToolCallCoreAsync(tcParams, request, sessionId, sessionContextEnabled, idToken, transportCancellation));
                }
                catch (UsageException ux)
                {
                    return new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idToken?.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32602,
                            ["message"] = ux.Message,
                            ["data"] = new JObject { ["usageCode"] = ux.Code }
                        }
                    };
                }
                catch (IdempotencyConflictException icx)
                {
                    // A5: a write retried under the same idempotencyKey with a DIFFERENT
                    // payload (e.g. after a TaskStop cancelled the first attempt and the
                    // agent retries with fresh args) previously threw uncaught — bubbling
                    // to the outer handler and surfacing as a generic error / null →
                    // permanent BadRequest wall for the whole session until the TTL expired.
                    // Return a clean, actionable JSON-RPC error instead.
                    return new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idToken?.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32602,
                            ["message"] = icx.Message,
                            ["data"] = new JObject { ["usageCode"] = "IdempotencyConflict" }
                        }
                    };
                }

                // Handle UsageException surfaced from DispatchCore as a result object
                if (toolInnerResult["usageException"]?.ToObject<bool>() == true)
                {
                    return new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idToken?.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = toolInnerResult["code"]?.ToObject<int?>() ?? -32602,
                            ["message"] = toolInnerResult["message"]?.ToString() ?? "Usage error",
                            ["data"] = new JObject { ["usageCode"] = toolInnerResult["usageCode"]?.ToString() }
                        }
                    };
                }

                // Piggyback background_jobs: attach snapshot of running/unseen-completed jobs to _meta.
                // Gated by PerfProfile.V1Enabled. Completions are marked seen so they surface exactly once.
                if (PerfProfile.V1Enabled)
                    McpRouter.PiggybackJobs(toolInnerResult, sessionId, JobRegistry);

                // Item 61: inject _meta.tokens (used/limit/hint) into every tool response
                // so the LLM can reason about response size and self-paginate.
                McpRouter.InjectMetaTokens(toolInnerResult);

                // Auto-inject annotation: when we inferred 'type' for this call, surface
                // it in the payload under _meta.autoInjected so the LLM can self-correct.
                if (_autoInjectedType != null)
                    InjectAutoTypeAnnotation(toolInnerResult, _autoInjectedType);

                // Wrap tool result in JSON-RPC envelope
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["result"] = toolInnerResult
                };
            }

            // Explicitly return an error for unknown tools if convert failed
            if (method == "tools/call")
            {
                string fallbackToolName = (request["params"] as JObject)?["name"]?.ToString() ?? "";
                return new JObject {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = JToken.FromObject(new { code = -32601, message = $"Method not found: {fallbackToolName}" })
                };
            }

            // Fix 6a: unknown method with an id is a request (not a notification) — must
            // respond. Notifications have no "id" field; those return null (no response).
            if (idToken != null && !string.IsNullOrEmpty(method)
                && !method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken.DeepClone(),
                    ["error"] = new JObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" }
                };
            }

            // Fix 6e: notifications/cancelled — abort matching pending request(s).
            // The cancelled id is the MCP client's request id; _pendingRequests is keyed
            // by gateway-generated GUIDs, so match through the McpRequestId bridge set
            // at dispatch. Entries already completed (or calls dispatched without an
            // MCP id) are not in the map under this id — ignore silently.
            if (string.Equals(method, "notifications/cancelled", StringComparison.OrdinalIgnoreCase))
            {
                var cancelledId = request["params"]?["requestId"];
                if (cancelledId != null)
                {
                    string cancelled = cancelledId.ToString();
                    Log($"[Protocol] notifications/cancelled for requestId={cancelled}");
                    int aborted = 0;
                    foreach (var kvp in _pendingRequests.ToArray())
                    {
                        if (!RequestIdentityMatches(kvp.Value.McpSessionId, kvp.Value.McpRequestIdToken, sessionId, cancelledId))
                            continue;
                        if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                        {
                            _operationTracker.MarkFailedByRequest(kvp.Key, "Cancelled by client");
                            // Same envelope shape as the worker-crash abort path: resolve the
                            // awaiter with a JSON-RPC error instead of letting it run to timeout.
                            var errorJson = JsonConvert.SerializeObject(new
                            {
                                jsonrpc = "2.0",
                                id = kvp.Key,
                                error = new { code = -32800, message = "Request cancelled by client" }
                            });
                            pending.CompletionSource.TrySetResult(errorJson);
                            aborted++;
                        }
                    }
                    if (aborted > 0)
                        Log($"[Protocol] notifications/cancelled aborted {aborted} pending worker request(s) for requestId={cancelled}.");

                    int abortedLongPolls = CancelPendingLongPolls(sessionId, cancelledId);
                    if (abortedLongPolls > 0)
                        Log($"[Protocol] notifications/cancelled aborted {abortedLongPolls} pending lifecycle long-poll(s) for requestId={cancelled}.");
                }
                return null;
            }

            return null;
        }

    }
}
