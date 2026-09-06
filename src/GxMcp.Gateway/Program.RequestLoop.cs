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
        /// <summary>
        /// Carries the caller's stable operation identity into the Worker RPC.
        /// The Gateway journal remains the durable fence; the Worker identity is
        /// a second in-process guard for retries that arrive after a transport
        /// timeout but before the first response reaches the client.
        /// </summary>
        internal static void AttachClientRequestIdentity(JObject workerCommand, JObject? toolArgs)
        {
            if (workerCommand == null || toolArgs == null) return;

            string? identity = toolArgs["clientRequestId"]?.ToString();
            if (string.IsNullOrWhiteSpace(identity))
                identity = toolArgs["idempotencyKey"]?.ToString();
            if (string.IsNullOrWhiteSpace(identity)) return;

            var workerParams = workerCommand["params"] as JObject;
            if (workerParams == null)
            {
                workerParams = new JObject();
                workerCommand["params"] = workerParams;
            }

            // A router/adapter may already have bound a stronger identity. Do
            // not silently replace it with a client argument.
            if (string.IsNullOrWhiteSpace(workerParams["clientRequestId"]?.ToString()))
                workerParams["clientRequestId"] = identity;
        }

        internal static bool UpsertKbCatalogEntry(JObject environment, string alias, string path)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("KB alias is required.", nameof(alias));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("KB path is required.", nameof(path));

            if (environment["KBs"] is JArray array)
            {
                bool exists = array.OfType<JObject>().Any(entry =>
                {
                    var aliasProperty = entry.Properties().FirstOrDefault(p =>
                        string.Equals(p.Name, "Alias", StringComparison.OrdinalIgnoreCase));
                    return string.Equals(aliasProperty?.Value?.ToString(), alias, StringComparison.OrdinalIgnoreCase);
                });
                if (exists) return false;

                array.Add(new JObject { ["Alias"] = alias, ["Path"] = path });
                return true;
            }

            if (environment["KBs"] is JObject map)
            {
                var existing = map.Properties().FirstOrDefault(p =>
                    string.Equals(p.Name, alias, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return false;

                map[alias] = path;
                return true;
            }

            environment["KBs"] = new JArray
            {
                new JObject { ["Alias"] = alias, ["Path"] = path }
            };
            return true;
        }

        internal static bool IsLifecycleBuildDryRun(JObject args)
        {
            return args?["dryRun"]?.ToObject<bool?>() == true;
        }

        internal static JObject BuildAsyncLifecycleCommand(string lifecycleAction, JObject args, string cancelToken)
        {
            bool rebuild = string.Equals(lifecycleAction, "rebuild", StringComparison.OrdinalIgnoreCase);
            bool buildAll = string.Equals(lifecycleAction, "build_all", StringComparison.OrdinalIgnoreCase);
            return new JObject
            {
                ["module"] = "Build",
                ["action"] = rebuild ? "RebuildAll" : buildAll ? "BuildAll" : "Build",
                ["target"] = args?["target"]?.ToString(),
                ["client"] = "mcp",
                ["includeCallees"] = args?["includeCallees"]?.ToString(),
                ["buildPlanCap"] = args?["buildPlanCap"]?.ToObject<int?>(),
                ["skipFullDeploy"] = args?["skipFullDeploy"]?.ToObject<bool?>(),
                ["dryRun"] = args?["dryRun"]?.ToObject<bool?>() ?? false,
                ["deploy"] = args?["deploy"]?.ToObject<bool?>() ?? false,
                ["cancelToken"] = cancelToken
            };
        }

        internal static async Task<JObject?> ProcessMcpRequest(
            JObject request,
            string sessionId = "stdio",
            bool sessionContextEnabled = true,
            CancellationToken transportCancellation = default,
            bool taskScopeEnabled = true)
        {
            string? method = request["method"]?.ToString();
            var idToken = request["id"];
            _currentKb.Value = null;

            // Resource subscriptions are stateful protocol operations. Route them
            // before McpRouter's static discovery handler so an ACK is only issued
            // after the URI is validated and attached to the creating HTTP session.
            var subscriptionResponse = McpSubscriptionProtocol.Handle(request, sessionId, _httpSessions);
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
            var taskResponse = McpTasksProtocol.Handle(request, sessionId, JobRegistry, taskScopeEnabled);
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

                bool needsKbResolution =
                    (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase) && !isMetaTool)
                    || (string.Equals(method, "resources/read", StringComparison.OrdinalIgnoreCase)
                        && McpRouter.ConvertResourceCall(request) != null);

                if (needsKbResolution)
                {
                    try
                    {
                        string? kbArg = null;
                        if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
                        {
                            var paramsObj = request["params"] as JObject;
                            var argsObj = paramsObj?["arguments"] as JObject;
                            kbArg = argsObj?["kb"]?.ToString();
                            // Strip `kb` from worker-bound args (worker is single-KB scoped).
                            argsObj?.Remove("kb");
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
                        bool sessionContextInitialized = sessionContextEnabled
                            && TryGetSessionSelectedKb(sessionId, out sessionDefaultAlias);
                        _currentKb.Value = _kbResolver.Resolve(
                            kbArg,
                            _workerPool.ListOpen(),
                            _workerPool.ListKnown(),
                            sessionDefaultAlias,
                            sessionContextInitialized);
                    }
                    catch (KbResolutionException ex)
                    {
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

                // Friction 2026-05-22: genexus_worker_reload force=true bypasses
                // the JSON-RPC pipe and kills the worker directly. The soft path
                // is unreachable when the worker is wedged on a hung preview
                // subprocess — by definition it can't ACK the reload command.
                if (string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase)
                    && args?["force"]?.ToObject<bool?>() == true)
                {
                    // Refuse the force path when configuration isn't loaded — otherwise we'd
                    // kill the worker pool with no way to bring it back up and the caller
                    // would only learn from subsequent tool failures.
                    if (_activeConfig == null)
                    {
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Force-reload refused: no active configuration. The gateway hasn't completed startup or a prior config load failed; respawning the worker without a config would leave the pool empty." })
                        };
                    }
                    try
                    {
                        var handlesToRestore = _workerPool?.ListOpen().ToList() ?? new List<KbHandle>();
                        if (_workerPool != null)
                        {
                            using (SuppressEagerRespawn())
                            {
                                _workerPool.StopAll();
                            }
                        }
                        _semanticCache.InvalidateScope(string.Empty);
                        System.Threading.Interlocked.Increment(ref SemanticCacheEpoch);
                        StartWorker(_activeConfig);
                        var readyAliases = new JArray();
                        var failedAliases = new JArray();
                        foreach (var handle in handlesToRestore)
                        {
                            try
                            {
                                using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                                var replacement = await _workerPool!.AcquireAsync(handle, readyCts.Token).ConfigureAwait(false);
                                bool ready = await McpRouter.AwaitWithHeartbeat(
                                    replacement.SdkReadyTask, timeoutMs: 180_000,
                                    progressToken: null, heartbeat: null,
                                    toolName: "worker_reload force").ConfigureAwait(false);
                                if (ready) readyAliases.Add(handle.Alias);
                                else failedAliases.Add(new JObject { ["alias"] = handle.Alias, ["reason"] = "sdkReadyTimeout" });
                            }
                            catch (Exception restoreEx)
                            {
                                failedAliases.Add(new JObject { ["alias"] = handle.Alias, ["reason"] = restoreEx.Message });
                            }
                        }
                        BroadcastToolsListChanged("worker_reloaded_force");
                        BroadcastResourcesListChanged("worker_reloaded_force");
                        var ok = new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["result"] = BuildToolResultContent(
                                new JObject
                                {
                                    ["status"] = failedAliases.Count == 0 ? "Forced" : "ReloadFailed",
                                    ["online"] = handlesToRestore.Count > 0 && readyAliases.Count > 0 && failedAliases.Count == 0,
                                    ["restoredWorkers"] = readyAliases,
                                    ["failedWorkers"] = failedAliases,
                                    ["detail"] = handlesToRestore.Count == 0
                                        ? "Worker pool was reset; no previously-open worker existed to restore. A worker will start on the next KB request."
                                        : failedAliases.Count == 0
                                            ? "Worker process(es) were killed, respawned, and confirmed SDK-ready. Any in-flight worker job was abandoned."
                                            : "Worker process(es) were killed, but one or more replacements did not become SDK-ready."
                                },
                                isError: failedAliases.Count > 0,
                                toolName: toolName,
                                toolArgs: args)
                        };
                        return ok;
                    }
                    catch (Exception ex)
                    {
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Force-reload failed: " + ex.Message })
                        };
                    }
                }

                // Non-force genexus_worker_reload: gateway-orchestrated graceful drain + respawn.
                // Previously this fell through to the worker as a normal RPC; the worker ACK'd
                // then exited, leaving a window where AcquireAsync returned the dying process.
                // Now the gateway marks the pool entry as draining (blocking concurrent
                // AcquireAsync callers), sends StopWithReason(PlannedReload), waits for the OS
                // process to exit, spawns a fresh worker, awaits its SdkReadyTask, then responds.
                if (string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase)
                    && args?["force"]?.ToObject<bool?>() != true)
                {
                    if (_activeConfig == null || _workerPool == null)
                    {
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Reload refused: gateway has no active configuration." })
                        };
                    }
                    string? reloadAlias = args?["alias"]?.ToString();
                    KbHandle? reloadKb = null;
                    if (!string.IsNullOrWhiteSpace(reloadAlias))
                    {
                        reloadKb = _workerPool.ListOpen().FirstOrDefault(h =>
                            string.Equals(h.NormalizedAlias, reloadAlias.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        reloadKb = _workerPool.ListOpen().FirstOrDefault();
                    }
                    if (reloadKb == null)
                    {
                        return BuildToolTextResponse(idToken,
                            new JObject { ["status"] = "NoWorker", ["detail"] = "No open KB worker found to reload." },
                            isError: false, toolName: toolName, toolArgs: args);
                    }
                    // mode=hard: sourceDir carries new worker binaries to swap in. The gateway
                    // does the copy in the drain window (old worker exited → exe unlocked → eager
                    // respawn suppressed) so it can't lose the race to a respawn. Previously
                    // sourceDir was ignored here (plain drain+respawn ran the OLD binary).
                    string? reloadSrcDir = args?["sourceDir"]?.ToString();
                    try
                    {
                        using (SuppressEagerRespawn())
                        {
                            Func<WorkerProcess?, Task>? swapHook = string.IsNullOrWhiteSpace(reloadSrcDir)
                                ? (Func<WorkerProcess?, Task>?)null
                                : (oldW =>
                                {
                                    string? tgtDir = null;
                                    try { tgtDir = System.IO.Path.GetDirectoryName(oldW?.SpawnedExePath); } catch { }
                                    CopyWorkerBinaries(reloadSrcDir!, tgtDir);
                                    return Task.CompletedTask;
                                });
                            // Drain timeout: 30s for the old worker process to exit.
                            var newWorker = await _workerPool.DrainAndReplaceAsync(reloadKb, drainTimeoutMs: 30_000, ct: CancellationToken.None, afterDrainBeforeSpawn: swapHook).ConfigureAwait(false);
                            // Wait for the new worker's SDK to be ready (cap at 180s to avoid hanging).
                            bool sdkReady = await McpRouter.AwaitWithHeartbeat(
                                newWorker.SdkReadyTask, timeoutMs: 180_000,
                                progressToken: null, heartbeat: null,
                                toolName: "worker_reload").ConfigureAwait(false);
                            BroadcastToolsListChanged(
                                "worker_reloaded_soft",
                                reloadKb.NormalizedAlias,
                                _semanticCache.GetRevision(reloadKb.NormalizedAlias));
                            BroadcastResourcesListChanged(
                                "worker_reloaded_soft",
                                reloadKb.NormalizedAlias,
                                _semanticCache.GetRevision(reloadKb.NormalizedAlias));
                            return BuildToolTextResponse(idToken,
                                new JObject
                                {
                                    ["status"] = "Reloaded",
                                    ["swappedAndReady"] = sdkReady,
                                    ["detail"] = sdkReady
                                        ? "Worker gracefully drained and replaced; new worker is SDK-ready."
                                        : "Worker replaced but new worker did not signal SDK-ready within 180s."
                                },
                                isError: false, toolName: toolName, toolArgs: args);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Reload] Soft reload failed for KB '{reloadKb.Alias}': {ex.Message}");
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Soft reload failed: " + ex.Message })
                        };
                    }
                }

                // genexus_telemetry: gateway-only short-circuit for ring-buffer views (executions, watch_event).
                // Other actions (logs/friction_*/learning_report/profile_*) fall through to the router.
                if (string.Equals(toolName, "genexus_telemetry", StringComparison.OrdinalIgnoreCase))
                {
                    string? telAction = args?["action"]?.ToString()?.ToLowerInvariant();
                    if (telAction == "executions")
                    {
                        string targetName = args?["target"]?.ToString() ?? args?["name"]?.ToString();
                        int lastN = args?["last"]?.ToObject<int?>() ?? 10;
                        JObject historyPayload = _operationTracker?.BuildExecutionHistory(targetName, lastN)
                            ?? new JObject { ["status"] = "Unwired", ["code"] = "TrackerUnavailable", ["runs"] = new JArray() };
                        return BuildToolTextResponse(idToken, historyPayload, isError: false, toolName: "genexus_telemetry", toolArgs: args, payloadOwned: true);
                    }
                    if (telAction == "watch_event")
                    {
                        string watchTarget = args?["target"]?.ToString() ?? args?["name"]?.ToString();
                        string watchEvent = args?["event"]?.ToString();
                        int watchLast = args?["last"]?.ToObject<int?>() ?? 10;
                        JObject watchPayload = _operationTracker?.BuildWatchEvent(watchTarget, watchEvent, watchLast)
                            ?? new JObject { ["status"] = "Unwired", ["code"] = "TrackerUnavailable", ["runs"] = new JArray() };
                        return BuildToolTextResponse(idToken, watchPayload, isError: false, toolName: "genexus_telemetry", toolArgs: args, payloadOwned: true);
                    }
                    // Any other action falls through to OperationsRouter ConvertTelemetryUmbrella.
                }

                // genexus_kb — meta-tool for managing the WorkerPool (list/open/close).
                // Handled entirely in the Gateway; never reaches a Worker. The
                // set_startup/get_startup actions are SDK-bound and fall through
                // to the router pipeline (SystemRouter forwards them to the
                // worker's KB module).
                if (string.Equals(toolName, "genexus_kb", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args?["action"]?.ToString(), "set_startup", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args?["action"]?.ToString(), "get_startup", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args?["action"]?.ToString(), "list_environments", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args?["action"]?.ToString(), "get_environment", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args?["action"]?.ToString(), "set_environment", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        if (_workerPool == null)
                        {
                            throw new InvalidOperationException("WorkerPool not initialised.");
                        }

                        string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "list";
                        switch (action)
                        {
                            case "list":
                                var openKbs = _workerPool.ListOpen();
                                var knownKbs = _workerPool.ListKnown();
                                string? configuredAlias = GetConfiguredDefaultKb();
                                string? selectedAlias = sessionContextEnabled
                                    ? GetSessionSelectedKb(sessionId)
                                    : null;
                                var activeHandle = !string.IsNullOrWhiteSpace(selectedAlias)
                                    ? openKbs.FirstOrDefault(k =>
                                        string.Equals(k.Alias, selectedAlias, StringComparison.OrdinalIgnoreCase))
                                    : openKbs.FirstOrDefault(k =>
                                        string.Equals(k.Alias, configuredAlias, StringComparison.OrdinalIgnoreCase))
                                        ?? (openKbs.Count == 1 ? openKbs[0] : null);
                                payload = new JObject
                                {
                                    ["openKbs"] = JArray.FromObject(_workerPool.Snapshot()
                                        .Select(s => new
                                        {
                                            alias = s.Handle.Alias,
                                            path = s.Handle.Path,
                                            pid = s.Pid,
                                            workingSetBytes = s.WorkingSetBytes,
                                            workingSetMB = s.WorkingSetBytes.HasValue
                                                ? Math.Round(s.WorkingSetBytes.Value / (1024.0 * 1024.0), 1)
                                                : (double?)null,
                                            lastActivityUtc = s.LastActivityUtc,
                                            idleSeconds = (int)Math.Max(0, (DateTime.UtcNow - s.LastActivityUtc).TotalSeconds)
                                        })),
                                    ["knownKbs"] = JArray.FromObject(knownKbs
                                        .Select(k => new
                                        {
                                            alias = k.Alias,
                                            path = k.Path,
                                            open = _workerPool.TryGetWorker(k.NormalizedAlias) != null
                                        })),
                                    ["activeKb"] = selectedAlias ?? activeHandle?.Alias ?? configuredAlias,
                                    ["selectedKb"] = selectedAlias,
                                    ["maxOpenKbs"] = _activeConfig?.Server?.MaxOpenKbs ?? 3,
                                    ["defaultKb"] = configuredAlias,
                                    ["declaredKbs"] = JArray.FromObject(
                                        (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                                            .Select(k => new { alias = k.Alias, path = k.Path }))
                                };
                                // Terse mode: the list action is a health snapshot; strip
                                // process telemetry (workingSet/pid/idle) and redundant
                                // alias lists when every catalog says the same thing.
                                if (TerseResponsesEnabledCached() && payload["openKbs"] is JArray openArr)
                                {
                                    foreach (var item in openArr.OfType<JObject>())
                                    {
                                        item.Remove("workingSetBytes");
                                        item.Remove("workingSetMB");
                                        item.Remove("pid");
                                        item.Remove("lastActivityUtc");
                                        item.Remove("idleSeconds");
                                    }
                                    var known = payload["knownKbs"] as JArray;
                                    var declared = payload["declaredKbs"] as JArray;
                                    var declaredAliases = declared?.Select(d => d["alias"]?.ToString()).ToList();
                                    bool knownMatches = known != null && declaredAliases != null
                                        && known.Count == declaredAliases.Count
                                        && known.Select(k => k["alias"]?.ToString()).OrderBy(x => x)
                                            .SequenceEqual(declaredAliases.OrderBy(x => x));
                                    if (knownMatches) payload.Remove("knownKbs");
                                    if (openArr.Count == 0) payload.Remove("openKbs");
                                }
                                break;
                            case "set_default":
                            {
                                string? alias = args?["alias"]?.ToString();
                                if (string.IsNullOrWhiteSpace(alias))
                                    throw new ArgumentException("Missing 'alias' for action=set_default.");
                                if (_activeConfig == null || string.IsNullOrWhiteSpace(Configuration.CurrentConfigPath))
                                    throw new InvalidOperationException("No active config to persist.");
                                var declared = _activeConfig.Environment?.KBs?.FirstOrDefault(
                                    k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                // issue #26 P4: accept any alias that is currently open or was
                                // opened this session (ad-hoc via `open path=...`), not just the
                                // ones pre-declared in config.json. When the alias exists only as
                                // an open/known handle, promote it to a declared KbEntry so the
                                // default survives a restart — instead of dead-ending with
                                // KB_NOT_FOUND right after `open` succeeded.
                                string resolvedAlias;
                                string? resolvedPath = null;
                                if (declared != null)
                                {
                                    resolvedAlias = declared.Alias;
                                    resolvedPath = declared.Path;
                                }
                                else
                                {
                                    var known = _workerPool.ListKnown().FirstOrDefault(
                                        k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                    if (known == null)
                                        throw new KbResolutionException("KB_NOT_FOUND",
                                            $"Alias '{alias}' is neither declared in config.Environment.KBs[] nor currently open. Use 'open' with a path first, or add it to config.");
                                    resolvedAlias = known.Alias;
                                    resolvedPath = known.Path;
                                }
                                // Patch the JSON on disk to preserve any fields we don't model.
                                string configPath = Configuration.CurrentConfigPath!;
                                JObject root;
                                try { root = JObject.Parse(System.IO.File.ReadAllText(configPath)); }
                                catch (Exception ex) { throw new InvalidOperationException($"Failed to read config.json: {ex.Message}"); }
                                if (root["Environment"] is not JObject envObj)
                                {
                                    envObj = new JObject();
                                    root["Environment"] = envObj;
                                }
                                bool promoted = false;
                                if (declared == null && !string.IsNullOrWhiteSpace(resolvedPath))
                                {
                                    // Persist the ad-hoc KB as a declared entry so it's resolvable
                                    // after a restart, mirroring the in-memory _known registry.
                                    promoted = UpsertKbCatalogEntry(envObj, resolvedAlias, resolvedPath);
                                    if (promoted)
                                    {
                                        _activeConfig.Environment!.KBs.Add(new KbEntry { Alias = resolvedAlias, Path = resolvedPath });
                                    }
                                }
                                envObj["DefaultKb"] = resolvedAlias;
                                // The Node CLI uses ActiveKb while the gateway uses
                                // DefaultKb. Keep both markers aligned so switching from
                                // OpenCode or MCP cannot leave the two clients disagreeing.
                                envObj["ActiveKb"] = resolvedAlias;
                                System.IO.File.WriteAllText(configPath, root.ToString(Formatting.Indented));
                                _activeConfig.Environment!.DefaultKb = resolvedAlias;
                                _activeConfig.Environment!.ActiveKb = resolvedAlias;
                                if (sessionContextEnabled)
                                    SetSessionSelectedKb(sessionId, resolvedAlias);
                                payload = new JObject
                                {
                                    ["defaultKb"] = resolvedAlias,
                                    ["selectedKb"] = resolvedAlias,
                                    ["persistedTo"] = configPath,
                                    ["promotedToDeclared"] = promoted
                                };
                                break;
                            }
                            case "open":
                            {
                                string? alias = args?["alias"]?.ToString();
                                string? path = args?["path"]?.ToString();
                                KbHandle handleToOpen;
                                // Path wins: register ad-hoc (with optional caller-supplied alias).
                                if (!string.IsNullOrWhiteSpace(path))
                                {
                                    string finalAlias = string.IsNullOrWhiteSpace(alias)
                                        ? System.IO.Path.GetFileName(path!.TrimEnd('\\', '/')).ToLowerInvariant()
                                        : alias!;
                                    if (string.IsNullOrEmpty(finalAlias)) finalAlias = "adhoc";

                                    // issue #38 defect #1: reject a path that is not a real KB root
                                    // BEFORE spawning a worker. A GeneXus environment/model subfolder
                                    // (no .gxw / no knowledgebase.connection) would otherwise be handed
                                    // to a freshly-spawned worker as GX_KB_PATH, whose open fails but
                                    // whose auto-open keeps retrying forever, eventually wedging the
                                    // gateway (every later call → "Master error: NotFound"). Fail fast
                                    // here so no worker is ever spawned for an unopenable path.
                                    if (!Configuration.IsPlausibleKbPath(path!))
                                    {
                                        isError = true;
                                        payload = new JObject
                                        {
                                            ["error"] = $"'{path}' is not a GeneXus Knowledge Base root: no .gxw file and no knowledgebase.connection found. "
                                                + "Point at the KB folder that contains the .gxw (not an environment/model subfolder).",
                                            ["code"] = "KbInvalidPath",
                                            ["path"] = path
                                        };
                                        return BuildToolTextResponse(idToken, payload, isError, "genexus_kb", args, payloadOwned: true);
                                    }

                                    handleToOpen = new KbHandle(finalAlias, path!);
                                }
                                // No path → resolve the alias against config-declared KBs.
                                else if (!string.IsNullOrWhiteSpace(alias) && _activeConfig != null)
                                {
                                    handleToOpen = new KbResolver(_activeConfig).Resolve(alias, _workerPool.ListOpen(), _workerPool.ListKnown());
                                }
                                else
                                {
                                    throw new ArgumentException("Provide 'path' (ad-hoc) or 'alias' of a KB declared in config.Environment.KBs[].");
                                }

                                var w = await _workerPool.AcquireAsync(handleToOpen, CancellationToken.None);
                                // Opening a worker must not mutate the persisted default or
                                // another MCP session's selection. Use set_default to select
                                // this KB for the current session, or pass kb explicitly.
                                string? selectedAfterOpen = sessionContextEnabled
                                    ? GetSessionSelectedKb(sessionId)
                                    : null;
                                bool selected = string.Equals(
                                    selectedAfterOpen,
                                    handleToOpen.Alias,
                                    StringComparison.OrdinalIgnoreCase);
                                payload = new JObject
                                {
                                    ["opened"] = handleToOpen.Alias,
                                    ["path"] = handleToOpen.Path,
                                    ["workerPid"] = w?.Pid,
                                    ["selected"] = selected,
                                    ["active"] = selected
                                };
                                if (!selected)
                                {
                                    payload["hint"] = $"KB '{handleToOpen.Alias}' is open but not selected for this MCP session. Use genexus_kb action=set_default alias={handleToOpen.Alias} or pass kb explicitly.";
                                }
                                break;
                            }
                            case "close":
                            {
                                string? alias = args?["alias"]?.ToString();
                                if (string.IsNullOrWhiteSpace(alias))
                                {
                                    throw new ArgumentException("Missing 'alias' for action=close.");
                                }
                                bool closed = _workerPool.Close(alias!);
                                // The alias may be reopened against a different KB later;
                                // never let cached name/type resolutions cross that boundary.
                                InvalidateFullNameTypeMap(alias!);
                                bool selectionCleared = sessionContextEnabled
                                    && closed
                                    && string.Equals(GetSessionSelectedKb(sessionId), alias, StringComparison.OrdinalIgnoreCase);
                                if (selectionCleared)
                                    ClearSessionSelectedKb(sessionId);
                                payload = new JObject
                                {
                                    ["closed"] = closed,
                                    ["alias"] = alias,
                                    ["selectionCleared"] = selectionCleared,
                                    ["selectedKb"] = sessionContextEnabled
                                        ? GetSessionSelectedKb(sessionId)
                                        : null
                                };
                                break;
                            }
                            default:
                                throw new ArgumentException($"Unknown action '{action}'. Use list|open|close.");
                        }
                    }
                    catch (KbResolutionException ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = ex.Code };
                    }
                    catch (WorkerPoolFullException ex)
                    {
                        isError = true;
                        payload = new JObject
                        {
                            ["error"] = ex.Message,
                            ["code"] = "KB_POOL_FULL",
                            ["openKbs"] = JArray.FromObject(ex.OpenKbs.Select(k => k.Alias))
                        };
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message };
                    }

                    return BuildToolTextResponse(idToken, payload, isError, "genexus_kb", args);
                }

                // Item 53: genexus_worker_pool action=warm_spares — gateway-side
                // meta-tool. Configures N pre-spawned workers bound to declared KBs
                // so the first KB-bound call doesn't pay cold-start. Capped at
                // WorkerPool.MaxWarmSpareCount; spareCount<0 disables. Never reaches
                // a worker — purely a gateway lifecycle knob.
                if (string.Equals(toolName, "genexus_worker_pool", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        if (_workerPool == null) throw new InvalidOperationException("WorkerPool not initialised.");
                        string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "warm_spares";
                        if (action != "warm_spares")
                            throw new ArgumentException($"Unknown action '{action}'. Use warm_spares.");
                        int spareCount = args?["spareCount"]?.ToObject<int?>() ?? args?["count"]?.ToObject<int?>() ?? 0;
                        var declared = (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                            .Select(k => new KbHandle(k.Alias, k.Path))
                            .ToList();
                        var result = await _workerPool.ConfigureWarmSpares(spareCount, declared);
                        payload = new JObject
                        {
                            ["status"] = result.Configured == 0 ? "Disabled" : "Configured",
                            ["requested"] = result.Requested,
                            ["configured"] = result.Configured,
                            ["capped"] = result.Capped,
                            ["maxAllowed"] = WorkerPool.MaxWarmSpareCount,
                            ["prespawned"] = JArray.FromObject(result.Prespawned),
                            ["skipped"] = JArray.FromObject(result.Skipped),
                            ["declaredKbCount"] = declared.Count
                        };
                        if (result.Capped)
                            payload["warning"] = $"spareCount={result.Requested} exceeded cap {WorkerPool.MaxWarmSpareCount}; clamped to {result.Configured}.";
                        if (result.Configured > declared.Count)
                            payload["note"] = $"requested {result.Configured} spares but only {declared.Count} KBs declared in config.Environment.KBs[]; declare more aliases to use the full budget.";
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_worker_pool", args, payloadOwned: true);
                }

                // genexus_connection_recover — gateway-side self-healing meta-tool.
                // Diagnoses the connection/worker state and automatically applies the
                // least-invasive recovery: healthy → no-op report; worker dead/wedged →
                // kill + respawn with SDK-ready confirmation; stale cache → clear.
                // Replaces the manual scripts/mcp_recover.ps1 flow for agent-driven use.
                if (string.Equals(toolName, "genexus_connection_recover", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        if (_workerPool == null) throw new InvalidOperationException("WorkerPool not initialised.");
                        if (_activeConfig == null) throw new InvalidOperationException("No active configuration loaded.");

                        bool force = args?["force"]?.ToObject<bool?>() == true;
                        var openKbs = _workerPool.ListOpen();
                        // Workers that died since their last call are dropped from
                        // ListOpen (entry requires Worker != null) but remain in
                        // ListKnown — include them so recover also re-opens KBs whose
                        // worker silently disappeared, not just wedged live ones.
                        var knownNotOpen = _workerPool.ListKnown()
                            .Where(k => !openKbs.Any(o => string.Equals(o.NormalizedAlias, k.NormalizedAlias, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        payload = new JObject();

                        // 1. Diagnose current state.
                        var diagnoses = new JArray();
                        foreach (var kb in openKbs)
                        {
                            var entryState = "unknown";
                            try
                            {
                                var whoamiProbe = await SendWorkerCommandAsync(
                                    new JObject { ["module"] = "Ping", ["action"] = "Ping", ["client"] = "mcp" },
                                    5000, "recover-ping timeout",
                                    wr => wr,
                                    (_, cid) => new JObject { ["__timeout"] = true },
                                    toolName: "connection_recover_probe",
                                    trackOperation: false);
                                entryState = whoamiProbe?["__timeout"]?.ToObject<bool>() == true ? "unresponsive" : "responsive";
                            }
                            catch { entryState = "unreachable"; }
                            diagnoses.Add(new JObject
                            {
                                ["alias"] = kb.Alias,
                                ["state"] = entryState
                            });
                        }
                        payload["diagnosis"] = diagnoses;

                        var unresponsive = diagnoses.Where(d => d["state"]?.ToString() != "responsive").ToList();
                        bool anyUnhealthy = force || unresponsive.Count > 0 || knownNotOpen.Count > 0;

                        // 2. Recover only what's broken (or everything when force=true).
                        if (!anyUnhealthy)
                        {
                            payload["status"] = "Healthy";
                            payload["action"] = "none";
                            payload["detail"] = "All workers responsive; no recovery needed.";
                        }
                        else
                        {
                            var targetsToRestore = openKbs
                                .Where(k => force || unresponsive.Any(d =>
                                    string.Equals(d["alias"]?.ToString(), k.Alias, StringComparison.OrdinalIgnoreCase)))
                                .Concat(force ? knownNotOpen : knownNotOpen.Where(k => openKbs.Count == 0))
                                .ToList();

                            using (SuppressEagerRespawn())
                            {
                                _workerPool.StopAll(WorkerStopReason.Wedged);
                            }
                            _semanticCache.InvalidateScope(string.Empty);
                            System.Threading.Interlocked.Increment(ref SemanticCacheEpoch);

                            var restored = new JArray();
                            var failed = new JArray();
                            foreach (var handle in targetsToRestore)
                            {
                                try
                                {
                                    using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                                    var replacement = await _workerPool.AcquireAsync(handle, readyCts.Token).ConfigureAwait(false);
                                    bool ready = await McpRouter.AwaitWithHeartbeat(
                                        replacement.SdkReadyTask, timeoutMs: 180_000,
                                        progressToken: null, heartbeat: null,
                                        toolName: "genexus_connection_recover").ConfigureAwait(false);
                                    if (ready) restored.Add(handle.Alias);
                                    else failed.Add(new JObject { ["alias"] = handle.Alias, ["reason"] = "sdkReadyTimeout" });
                                }
                                catch (Exception restoreEx)
                                {
                                    failed.Add(new JObject { ["alias"] = handle.Alias, ["reason"] = restoreEx.Message });
                                }
                            }

                            payload["status"] = failed.Count == 0 ? "Recovered" : "PartialRecovery";
                            payload["action"] = $"killed and respawned {(force ? "all" : "unresponsive")} workers; semantic cache cleared";
                            payload["restoredWorkers"] = restored;
                            payload["failedWorkers"] = failed;
                            isError = failed.Count > 0;
                        }

                        BroadcastToolsListChanged("connection_recover");
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "RecoveryFailed" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_connection_recover", args, payloadOwned: true);
                }

                // Item 54: genexus_sandbox — gateway-side filesystem clone of a KB.
                // No SDK touch; pure file copy under <configRoot>/sandboxes/<name>/.
                // remove is idempotent. create on an existing target returns
                // {status:"AlreadyExists"} unless overwrite=true.
                if (string.Equals(toolName, "genexus_sandbox", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "create";
                        string name = args?["name"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Missing 'name'.");
                        // Sanitize: alphanumeric + dash/underscore only.
                        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
                            throw new ArgumentException("name must match [A-Za-z0-9_-]+ (no spaces/path-separators).");

                        string configDir = !string.IsNullOrEmpty(Configuration.CurrentConfigPath)
                            ? System.IO.Path.GetDirectoryName(Configuration.CurrentConfigPath!)!
                            : AppContext.BaseDirectory;
                        string sandboxRoot = System.IO.Path.Combine(configDir, "sandboxes");
                        string targetPath = System.IO.Path.Combine(sandboxRoot, name);

                        if (action == "remove")
                        {
                            bool existed = System.IO.Directory.Exists(targetPath);
                            if (existed)
                            {
                                try { System.IO.Directory.Delete(targetPath, true); }
                                catch (Exception ex) { throw new InvalidOperationException($"Failed to remove sandbox: {ex.Message}"); }
                            }
                            payload = new JObject
                            {
                                ["status"] = existed ? "Removed" : "NotFound",
                                ["name"] = name,
                                ["path"] = targetPath
                            };
                        }
                        else if (action == "create")
                        {
                            string from = args?["from"]?.ToString();
                            if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Missing 'from' (source KB alias or path).");
                            bool overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false;

                            // Resolve `from` as alias against config, else treat as path.
                            string sourcePath = null;
                            var fromKb = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                                k => string.Equals(k.Alias, from, StringComparison.OrdinalIgnoreCase));
                            if (fromKb != null) sourcePath = fromKb.Path;
                            else if (System.IO.Directory.Exists(from)) sourcePath = from;
                            else throw new ArgumentException($"'from'='{from}' is not a declared KB alias and not an existing directory.");

                            if (System.IO.Directory.Exists(targetPath))
                            {
                                if (!overwrite)
                                {
                                    payload = new JObject
                                    {
                                        ["status"] = "AlreadyExists",
                                        ["name"] = name,
                                        ["path"] = targetPath,
                                        ["hint"] = "Pass overwrite=true to replace, or action=remove first."
                                    };
                                    return BuildToolTextResponse(idToken, payload, false, "genexus_sandbox", args, payloadOwned: true);
                                }
                                try { System.IO.Directory.Delete(targetPath, true); }
                                catch (Exception ex) { throw new InvalidOperationException($"Failed to remove existing sandbox before overwrite: {ex.Message}"); }
                            }

                            System.IO.Directory.CreateDirectory(sandboxRoot);
                            var copy = SandboxCopyHelper.CopyDirectory(sourcePath, targetPath);
                            payload = new JObject
                            {
                                ["status"] = "Created",
                                ["name"] = name,
                                ["path"] = targetPath,
                                ["from"] = sourcePath,
                                ["filesCopied"] = copy.Files,
                                ["bytesCopied"] = copy.Bytes,
                                ["durationMs"] = copy.DurationMs,
                                ["alias"] = "sandbox-" + name,
                                ["hint"] = $"Open with: genexus_kb action=open path=\"{targetPath}\" alias=sandbox-{name}"
                            };
                        }
                        else
                        {
                            throw new ArgumentException($"Unknown action '{action}'. Use create|remove.");
                        }
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_sandbox", args, payloadOwned: true);
                }

                // Item 55: genexus_kb_diff — gateway-side object-index diff between two
                // KB directories. No SDK touch. Walks each KB's filesystem Objects/<Type>/<Name>/
                // tree (and .gx/index-snapshot.bin if present) and returns
                // {onlyInA, onlyInB, modified[]}.
                if (string.Equals(toolName, "genexus_kb_diff", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        string kbA = args?["kbA"]?.ToString();
                        string kbB = args?["kbB"]?.ToString();
                        if (string.IsNullOrWhiteSpace(kbA) || string.IsNullOrWhiteSpace(kbB))
                            throw new ArgumentException("Both 'kbA' and 'kbB' are required (alias or path).");
                        string pathA = ResolveKbPath(kbA) ?? throw new ArgumentException($"'kbA'='{kbA}' not a declared alias and not an existing directory.");
                        string pathB = ResolveKbPath(kbB) ?? throw new ArgumentException($"'kbB'='{kbB}' not a declared alias and not an existing directory.");
                        if (string.Equals(System.IO.Path.GetFullPath(pathA), System.IO.Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("kbA and kbB resolve to the same path.");
                        payload = KbDiffHelper.Diff(pathA, pathB);
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_diff", args, payloadOwned: true);
                }

                // Item 56: genexus_kb_import — limited filesystem-level copy of an
                // object's files between two KBs. Full SDK-level import would require
                // opening a second KB inside the worker (one SDK can only host one
                // KB at a time), so this ships as a directory-copy + index-rescan
                // recommendation. Callers should run genexus_lifecycle action=index
                // afterwards.
                if (string.Equals(toolName, "genexus_kb_import", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        string from = args?["from"]?.ToString();
                        string name = args?["name"]?.ToString();
                        string type = args?["type"]?.ToString();
                        if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Missing 'from' (source KB alias or path).");
                        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Missing 'name' (object name to import).");
                        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Missing 'type' (object type, e.g. WebPanel, Procedure).");
                        string sourcePath = ResolveKbPath(from) ?? throw new ArgumentException($"'from'='{from}' not a declared alias and not an existing directory.");
                        // Target KB resolution. Prefer an explicit 'to' (alias or path) so the
                        // import isn't silently pointed at whichever KB happens to be first in
                        // the open-worker list; fall back to first-open / DefaultKb for
                        // back-compat when 'to' is omitted.
                        string to = args?["to"]?.ToString();
                        string targetPath = null;
                        if (!string.IsNullOrWhiteSpace(to))
                        {
                            targetPath = ResolveKbPath(to) ?? throw new ArgumentException($"'to'='{to}' not a declared alias and not an existing directory.");
                        }
                        else
                        {
                            var openKbs = _workerPool?.ListOpen();
                            if (openKbs != null && openKbs.Count > 0) targetPath = openKbs[0].Path;
                            else if (!string.IsNullOrEmpty(_activeConfig?.Environment?.DefaultKb))
                            {
                                var d = _activeConfig.Environment.KBs?.FirstOrDefault(k =>
                                    string.Equals(k.Alias, _activeConfig.Environment.DefaultKb, StringComparison.OrdinalIgnoreCase));
                                targetPath = d?.Path;
                            }
                        }
                        if (string.IsNullOrEmpty(targetPath))
                        {
                            payload = new JObject
                            {
                                ["status"] = "MissingFeature",
                                ["code"] = "NoActiveKb",
                                ["error"] = "kb_import requires an open active KB. Open one via genexus_kb action=open."
                            };
                            isError = true;
                            return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_import", args, payloadOwned: true);
                        }
                        if (string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("source and target KB resolve to the same path.");
                        payload = KbImportHelper.ImportObject(sourcePath, targetPath, name, type);
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_import", args, payloadOwned: true);
                }

                if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
                {
                    string? lifecycleAction = args?["action"]?.ToString();
                    string? lifecycleTarget = args?["target"]?.ToString();

                    // Durable mutation recovery is intentionally Gateway-local. After a
                    // restart the Worker cannot safely infer whether a timed-out write
                    // committed, so inspect/reconcile expose only the redacted journal
                    // state and require an explicit verification before closing a fence.
                    if (string.Equals(lifecycleAction, "inspect", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(lifecycleAction, "reconcile", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string operationKey = args?["operationKey"]?.ToString()
                                ?? args?["idempotencyKey"]?.ToString()
                                ?? string.Empty;
                            string operationTool = args?["operationTool"]?.ToString()
                                ?? args?["tool"]?.ToString()
                                ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(operationKey) || string.IsNullOrWhiteSpace(operationTool))
                                throw new UsageException("usage_error", "action=inspect|reconcile requires operationKey and operationTool.");
                            IdempotencyMiddleware.ValidateKey(operationKey);

                            string? requestedKb = args?["kb"]?.ToString();
                            string journalKbPath = !string.IsNullOrWhiteSpace(requestedKb)
                                ? (ResolveKbPath(requestedKb) ?? throw new UsageException("kb_not_found", "The requested kb alias/path could not be resolved."))
                                : (_currentKb.Value?.Path ?? _activeConfig?.Environment?.KBPath ?? string.Empty);
                            if (string.IsNullOrWhiteSpace(journalKbPath))
                                throw new UsageException("no_active_kb", "Open or select a KB before inspecting durable operations.");

                            JObject operationPayload;
                            if (string.Equals(lifecycleAction, "inspect", StringComparison.OrdinalIgnoreCase))
                            {
                                operationPayload = _idempotencyCache.InspectOperation(journalKbPath, operationTool, operationKey);
                            }
                            else
                            {
                                bool confirmed = args?["confirmed"]?.ToObject<bool?>() == true;
                                string verification = args?["verification"]?.ToString()
                                    ?? args?["verificationToken"]?.ToString()
                                    ?? string.Empty;
                                if (!confirmed || string.IsNullOrWhiteSpace(verification))
                                    throw new UsageException("verification_required", "Reconcile requires confirmed=true and a non-empty verification statement after an independent genexus_read.");
                                JToken? observedTargets = args?["observedTargetIds"]
                                    ?? args?["targetIds"];
                                string? observedRevision = args?["observedRevision"]?.ToString()
                                    ?? args?["revision"]?.ToString();
                                var observedEvidence = MutationOperationEvidence.FromObserved(observedTargets, observedRevision);
                                operationPayload = _idempotencyCache.ReconcileOperation(
                                    journalKbPath,
                                    operationTool,
                                    operationKey,
                                    verification,
                                    observedEvidence);
                            }

                            bool recoveryError = string.Equals(operationPayload["status"]?.ToString(), "Blocked", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(operationPayload["status"]?.ToString(), "Rejected", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(operationPayload["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(operationPayload["code"]?.ToString(), "operation_journal_unavailable", StringComparison.OrdinalIgnoreCase);
                            return BuildToolTextResponse(idToken, operationPayload, isError: recoveryError, toolName: toolName, toolArgs: args, payloadOwned: true);
                        }
                        catch (UsageException ux)
                        {
                            var usagePayload = new JObject
                            {
                                ["status"] = "Error",
                                ["code"] = ux.Code,
                                ["message"] = ux.Message
                            };
                            return BuildToolTextResponse(idToken, usagePayload, isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
                        }
                    }

                    if ((string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(lifecycleTarget) &&
                        lifecycleTarget.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                    {
                        string operationId = lifecycleTarget.Substring(3);
                        // v2.6.2 (Item B follow-up): JobRegistry covers build/edit jobs;
                        // OperationTracker covers gateway-internal request lifecycles.
                        // status/result with op:<id> should resolve in EITHER — fall through
                        // to the JobRegistry long-poll path below when the id is a job.
                        if (JobRegistry.Get(operationId) == null)
                        {
                            JObject opPayload = string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                                ? _operationTracker.BuildOperationResult(operationId)
                                : _operationTracker.BuildOperationStatus(operationId);
                            return BuildToolTextResponse(
                                idToken,
                                opPayload,
                                isError: string.Equals(opPayload["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase),
                                toolName: "genexus_lifecycle",
                                toolArgs: args,
                                payloadOwned: true);
                        }
                    }

                    // FR#7 (friction-report 2026-05-14): support best-effort cancellation for
                    // op:<id> targets. Previously this fell through to the worker, which only
                    // knows about build taskIds, returning "Task ID not found". Now we mark
                    // the op as Cancelled in the tracker and abandon any matching pending
                    // request. The worker thread may still finish its SDK call, but the
                    // client gets a deterministic answer.
                    // v2.3.8 (Task 7.2 + post-fix) — cancel via job_id: short-circuit the
                    // async pollers (build/edit) by signaling the registered CTS, flip the
                    // job to "cancelled", AND fan a Control:Cancel command out to the
                    // worker so the in-flight thread-safe handler (search/impact) can
                    // stop mid-loop. Without the fan-out the worker kept running its
                    // current call to completion while the gateway poller exited.
                    if (string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        string? cancelJobId = McpRouter.ResolveJobId(args);
                        if (!string.IsNullOrWhiteSpace(cancelJobId) && JobRegistry.Get(cancelJobId!) != null)
                        {
                            var cancellingJob = JobRegistry.Get(cancelJobId!);
                            bool ok = JobRegistry.Cancel(cancelJobId!, "Cancelled by client via lifecycle action=cancel.");
                            // Fire-and-forget worker signal. Thread-safe Control:Cancel runs
                            // on the parallel dispatch path so it interleaves with in-flight calls.
                            _ = SendWorkerCommandAsync(
                                new JObject
                                {
                                    ["method"] = "control",
                                    ["module"] = "Control",
                                    ["action"] = "Cancel",
                                    ["params"] = new JObject { ["cancelToken"] = cancelJobId }
                                },
                                5000, "cancel-fanout",
                                env => env,
                                (_, __) => new JObject(),
                                toolName: "genexus_lifecycle", toolArgs: args, trackOperation: false);
                            bool workerRecycled = false;
                            if (ok
                                && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true
                                && !string.IsNullOrWhiteSpace(cancellingJob.WorkerAlias)
                                && _workerPool != null)
                            {
                                try { workerRecycled = _workerPool.RecycleStalledWorker(cancellingJob.WorkerAlias); }
                                catch (Exception recycleEx) { Log($"[AsyncEdit] Cancel recycle failed for job={cancelJobId}: {recycleEx.Message}"); }
                            }
                            if (ok && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                _mutationRecovery.RequireRead(
                                    cancellingJob.WorkerAlias,
                                    cancellingJob.Target,
                                    cancellingJob.Part,
                                    cancelJobId);
                            }
                            // Issue #79: Cancel only acts on running jobs now, so a
                            // terminal job surfaces a truthful message instead of the
                            // misleading "not found in registry".
                            var terminalJob = ok ? null : JobRegistry.Get(cancelJobId);
                            string cancelMsg = ok
                                ? "Job marked Cancelled and Control:Cancel fanned out to the worker. Handlers honouring CancellationToken (search, analyze, build expansion) will terminate within one iteration."
                                : terminalJob != null
                                    ? "Job is already in a terminal state ('" + terminalJob.Status + "'); nothing to cancel."
                                    : "Job not found in registry (may have completed and been pruned).";
                            var jp = new JObject
                            {
                                ["status"] = ok ? "Cancelled" : "NotFound",
                                ["jobId"] = cancelJobId,
                                ["operationId"] = cancelJobId,
                                ["recycledWorker"] = workerRecycled,
                                ["reReadRequired"] = ok && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true,
                                ["message"] = cancelMsg
                            };
                            return BuildToolTextResponse(idToken, jp, isError: !ok, toolName: "genexus_lifecycle", toolArgs: args, payloadOwned: true);
                        }
                    }

                    if (string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(lifecycleTarget) &&
                        lifecycleTarget.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                    {
                        string operationId = lifecycleTarget.Substring(3);
                        _operationTracker.TryGetContext(operationId, out var cancelledToolName, out var cancelledToolArgs);
                        bool existed = false;
                        // A5: fan a Control:Cancel out to the worker (mirroring the job_id
                        // path above) so cooperative handlers trip their CTS and free the
                        // single STA queue, instead of leaving the worker running the
                        // cancelled op to completion while every later call queues behind it.
                        _ = SendWorkerCommandAsync(
                            new JObject
                            {
                                ["method"] = "control",
                                ["module"] = "Control",
                                ["action"] = "Cancel",
                                ["params"] = new JObject { ["cancelToken"] = operationId }
                            },
                            5000, "cancel-fanout",
                            env => env,
                            (_, __) => new JObject(),
                            toolName: "genexus_lifecycle", toolArgs: args, trackOperation: false);
                        // Try to find and abandon the pending request bound to this op.
                        string? abandonedRequestId = null;
                        string? workerAlias = null;
                        foreach (var kvp in _pendingRequests.ToArray())
                        {
                            if (string.Equals(kvp.Value.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
                            {
                                if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                                {
                                    abandonedRequestId = kvp.Key;
                                    workerAlias = pending.WorkerAlias;
                                    pending.CompletionSource.TrySetResult(JsonConvert.SerializeObject(new
                                    {
                                        jsonrpc = "2.0",
                                        id = kvp.Key,
                                        error = new { code = -32603, message = "Operation cancellation requested by client; the SDK call may still be running." }
                                    }));
                                    break;
                                }
                            }
                        }

                        bool workerRecycled = false;
                        if (!string.IsNullOrWhiteSpace(workerAlias) && _workerPool != null)
                        {
                            try { workerRecycled = _workerPool.RecycleStalledWorker(workerAlias); }
                            catch (Exception recycleEx) { Log($"[Operation] Cancel recycle failed for op={operationId}: {recycleEx.Message}"); }
                        }
                        existed = _operationTracker.MarkCancelled(operationId,
                            workerRecycled
                                ? "Cancelled by client; the non-preemptible worker was recycled. Re-read the object before another write."
                                : "Cancelled by client; no live worker remained to recycle. Re-read the object before another write.");
                        if (existed
                            && IsAsyncMutationTool(cancelledToolName)
                            && !string.IsNullOrWhiteSpace(workerAlias))
                        {
                            _mutationRecovery.RequireRead(
                                workerAlias,
                                cancelledToolArgs?["name"]?.ToString(),
                                cancelledToolArgs?["part"]?.ToString() ?? "Source",
                                operationId);
                        }

                        var cancelPayload = new JObject
                        {
                            ["status"] = existed ? "Cancelled" : "NotFound",
                            ["operationId"] = operationId,
                            ["abandonedRequestId"] = abandonedRequestId,
                            ["recycledWorker"] = workerRecycled,
                            ["reReadRequired"] = existed,
                            ["message"] = existed
                                ? "Operation reached terminal Cancelled state. The worker was recycled when it was still executing the non-preemptible SDK call; re-read the target before another write."
                                : "Operation not found in tracker (may have completed and been pruned, or never existed)."
                        };
                        return BuildToolTextResponse(idToken, cancelPayload, isError: !existed, toolName: "genexus_lifecycle", toolArgs: args, payloadOwned: true);
                    }

                    if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(lifecycleTarget, "gateway:metrics", StringComparison.OrdinalIgnoreCase))
                    {
                        return BuildToolTextResponse(idToken, _operationTracker.BuildMetricsPayload(), isError: false, toolName: "genexus_lifecycle", toolArgs: args);
                    }

                    // action=result + op:<jobId> — return the stored JobEntry.Result directly.
                    // v2.6.3 fixed status/cancel for op:<id> via JobRegistry but result still
                    // forwarded to the worker, which only knows about its internal taskId and
                    // returned "Task ID not found" for completed jobs (visible in
                    // _meta.background_jobs). Symmetric handler closes that gap.
                    if (string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase))
                    {
                        string? resultJobId = McpRouter.ResolveJobId(args);
                        if (!string.IsNullOrWhiteSpace(resultJobId))
                        {
                            var probe = JobRegistry.Get(resultJobId);
                            if (probe != null)
                            {
                                // Issue #27 item 1: if the job is still "running", actively
                                // reconcile against the worker before reporting Pending — the
                                // background poller may have wedged.
                                await ReconcileJobWithWorkerAsync(probe, "genexus_lifecycle", args);
                                // Envelope shape extracted into McpRouter.BuildJobResultEnvelope
                                // for unit-test coverage and parity with status long-poll.
                                var (resultPayload, isErr) = McpRouter.BuildJobResultEnvelope(probe);
                                return BuildToolTextResponse(idToken, resultPayload, isError: isErr, toolName: "genexus_lifecycle", toolArgs: args);
                            }
                            // Unknown id → fall through to the legacy worker-side taskId result path.
                        }
                    }

                    // Long-poll intercept (Task 4.5): action=status + job_id (BackgroundJobRegistry)
                    // wait_seconds is clamped [0, MaxLongPollSeconds]; 0 = immediate poll (default behaviour).
                    if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase))
                    {
                        string? jobId = McpRouter.ResolveJobId(args);
                        if (!string.IsNullOrWhiteSpace(jobId))
                        {
                            // Check registry first; only route to the long-poll path when the job
                            // is known to the registry. Unknown IDs fall through to the legacy
                            // worker-side taskId status path for backward compatibility.
                            var probe = JobRegistry.Get(jobId);
                            if (probe != null)
                            {
                                // Issue #27 item 1: reconcile a still-running job against the
                                // worker's real build-task state before long-polling, so a wedged
                                // background poller can't keep a finished build stuck at "running".
                                await ReconcileJobWithWorkerAsync(probe, "genexus_lifecycle", args);
                                int waitSeconds = Math.Min(Math.Max(args?["wait_seconds"]?.ToObject<int?>() ?? 0, 0), McpRouter.MaxLongPollSeconds);
                                var clientProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                                bool hasProgressToken = clientProgressToken != null && clientProgressToken.Type != JTokenType.Null;
                                string pendingLongPollKey = RegisterPendingLongPoll(
                                    sessionId,
                                    idToken,
                                    transportCancellation,
                                    out var longPollCancellationToken);
                                JObject pollResult;
                                try
                                {
                                    pollResult = await McpRouter.LongPollJob(
                                        JobRegistry, jobId, waitSeconds,
                                        progressToken: clientProgressToken,
                                        heartbeat: hasProgressToken ? (n => TryWriteStdout(n.ToString(Formatting.None))) : null,
                                        cancellationToken: longPollCancellationToken);
                                }
                                finally
                                {
                                    UnregisterPendingLongPoll(pendingLongPollKey);
                                }
                                bool isError = pollResult["error"] != null;
                                // Friction 2026-05-22 item 10: a "Build succeeded: 0w/0e/exit=0"
                                // result was previously dropped onto an <e>error{}> envelope when
                                // the JobEntry.Status string didn't match what callers expected.
                                // Run the build-outcome classifier on the inner BuildTaskStatus
                                // (if present) so the envelope's isError matches the actual outcome.
                                if (!isError && pollResult["result"] is JObject buildPayloadEarly)
                                {
                                    var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(buildPayloadEarly);
                                    if (outcome == LifecycleResponseShaper.BuildOutcome.Error) isError = true;
                                    else if (outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess)
                                    {
                                        // Surface partial_success on the outer envelope as a warning marker.
                                        pollResult["partial_success"] = true;
                                        if (pollResult["envelope"] == null) pollResult["envelope"] = "warning";
                                    }
                                    else if (outcome == LifecycleResponseShaper.BuildOutcome.Success)
                                    {
                                        // Defensive: a job stamped "failed" by the registry but whose
                                        // BuildTaskStatus says Succeeded/0/0/exit=0 should not be an error.
                                        // (We've seen this race: registry summary stamped before final
                                        // status normalized.) Keep isError=false in that case.
                                    }
                                }
                                // v2.3.8 (post-Task 6.1 fix): the DispatchCore compact pass below
                                // only runs for the worker-side legacy taskId path. job_id results
                                // arrive through this short-circuit and were skipping the shaper —
                                // callers using wait_seconds>0 + job_id were getting the verbose
                                // BuildTaskStatus payload under result. Compact here too.
                                if (!isError
                                    && LifecycleResponseShaper.ShouldCompact(args)
                                    && pollResult["result"] is JObject innerResult)
                                {
                                    try { pollResult["result"] = LifecycleResponseShaper.CompactObject(innerResult); } // perf: no serialize→parse round-trip
                                    catch { /* shaper passthrough on non-JSON */ }
                                }
                                return BuildToolTextResponse(idToken, pollResult, isError: isError, toolName: "genexus_lifecycle", toolArgs: args);
                            }
                            // Not in registry → fall through to existing worker-side status path (legacy taskId).
                        }
                    }
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

                // Inner dispatch: returns { isError, content } (tool result payload, no JSON-RPC envelope)
                async Task<JObject> DispatchCore(JObject tcParams)
                {
                    string tName = tcParams["name"]?.ToString() ?? "";
                    var tArgs = tcParams["arguments"] as JObject;
                    string kbScope = _currentKb.Value?.NormalizedAlias ?? "";

                    // 1. CACHE INVALIDATION: If it's a write operation or a re-index, clear the cache.
                    // PERF: compute once so the semantic-cache read below can skip the
                    // (payload-serializing) key construction for mutating tools — the cache
                    // was just cleared, so a lookup would be a guaranteed miss anyway.
                    // genexus_edit used to serialize its full source payload into a key
                    // for that doomed lookup on every call.
                    bool isMutating = IsMutatingTool(tName, tArgs);
                    if (isMutating
                        && !IsMutationPreview(tArgs)
                        && !_mutationRecovery.IsHealthy)
                    {
                        return BuildToolResultContent(
                            MutationRecoveryRegistry.BuildJournalBlockedEnvelope(_mutationRecovery.JournalError),
                            isError: true,
                            toolName: tName,
                            toolArgs: tArgs);
                    }
                    if (isMutating && !IsMutationPreview(tArgs))
                    {
                        RecoveryRequirement? recoveryRequirement = null;
                        foreach (string recoveryTarget in EnumerateMutationTargets(tName, tArgs))
                        {
                            if (_mutationRecovery.TryGet(kbScope, recoveryTarget, out var found))
                            {
                                recoveryRequirement = found;
                                break;
                            }
                        }
                        if (recoveryRequirement != null)
                        {
                            return BuildToolResultContent(
                                MutationRecoveryRegistry.BuildBlockedEnvelope(recoveryRequirement),
                                isError: true,
                                toolName: tName,
                                toolArgs: tArgs);
                        }
                    }
                    if (isMutating && !IsMutationPreview(tArgs))
                    {
                        // A confirmed or uncertain mutation invalidates the entire
                        // affected KB generation. Object names in request arguments
                        // do not describe the population returned by list/query reads,
                        // so substring invalidation can preserve stale collections.
                        // The generation fence below still lets unrelated KBs retain
                        // their warm entries and blocks in-flight old reads.
                        long revision = _semanticCache.InvalidateScope(kbScope, out int removed);
                        if (_verboseRequestLogs)
                            Log($"[Cache] Generation invalidation for '{kbScope}' by {tName}: revision={revision}, {removed} entr(y|ies) dropped");
                        // Per-KB generations fence related in-flight reads without
                        // invalidating warm entries (or writes) for unrelated KBs.
                        // Only an unknown/global scope needs the process-wide epoch.
                        if (string.IsNullOrWhiteSpace(kbScope))
                            System.Threading.Interlocked.Increment(ref SemanticCacheEpoch);
                        BroadcastResourcesListChanged($"cache_invalidated:{tName}", kbScope, revision);
                        BroadcastResourceUpdated(
                            "genexus://objects",
                            $"tool:{tName}",
                            kbScope,
                            revision);
                    }

                    // 2. SEMANTIC CACHE: Try to get from cache for classifier-approved
                    // read-only tools. CreateSemanticCacheKey repeats the safety gate
                    // so side-effectful action parameters cannot be cached accidentally.
                    // Skip caching for live-progress lifecycle reads (status/result/cancel) and logs —
                    // these must always reflect current worker state, not a stale snapshot.
                    string lcAction = tArgs?["action"]?.ToString()?.ToLowerInvariant();
                    bool isLiveLifecycle = string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                                           && (lcAction == "status" || lcAction == "result" || lcAction == "cancel");
                    // genexus_gxserver reflects live server/model state — its reads
                    // (status/pending/conflicts/history) change after a commit/update/resolve,
                    // so a cached snapshot goes stale (an identical action=conflicts after a
                    // resolve returned the pre-resolve count). Never cache it.
                    bool isLiveTool = isLiveLifecycle
                                      || string.Equals(tName, "genexus_logs", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(tName, "genexus_gxserver", StringComparison.OrdinalIgnoreCase);

                    // Scope the semantic cache by the resolved KB: the same tool+args
                    // against two different open KBs must not share envelopes (the
                    // worker is single-KB, so an identical read against KB-B could
                    // otherwise replay KB-A's cached result).
                    // PERF: only build the key (which serializes the whole args tree) when
                    // a lookup can possibly hit. Mutating tools just cleared the cache
                    // (guaranteed miss) and live tools never read from it, so for those
                    // the key — and the expensive payload ToString for genexus_edit/write
                    // — is pure waste.
                    long cacheRevisionAtDispatch = _semanticCache.GetRevision(kbScope);
                    string? modelScope = null;
                    string? environmentScope = null;
                    var currentKbForCache = _currentKb.Value;
                    if (currentKbForCache?.IsEnvCacheFresh == true)
                    {
                        modelScope = currentKbForCache.ActiveEnvironmentVersion;
                        environmentScope = currentKbForCache.ActiveEnvironment;
                    }
                    string? cKey = CreateSemanticCacheKey(
                        kbScope, tName, tArgs, isMutating, isLiveTool,
                        cacheRevisionAtDispatch, modelScope, environmentScope);
                    if (cKey != null && _semanticCache.TryGet(cKey, out var cachedResponse))
                    {
                        if (_verboseRequestLogs) Log($"[Cache] HIT for {tName}");
                        var cached = cachedResponse["result"] as JObject;
                        if (cached != null)
                        {
                            var hit = (JObject)cached.DeepClone();
                            var hitMeta = hit["_meta"] as JObject ?? new JObject();
                            hit["_meta"] = hitMeta;
                            hitMeta["cacheOutcome"] = "hit";
                            return hit;
                        }
                    }

                    // Rebuild full request so ConvertToolCall works
                    var fullReq = new JObject
                    {
                        ["method"] = "tools/call",
                        ["params"] = tcParams
                    };

                    // v2.6.9 perf: gateway-side fast-fail for SDK-bound tools when
                    // the worker is still doing its initial BulkIndex on the STA thread.
                    // Without this the request queues behind a 30-60s SDK enumeration
                    // and the agent eats the full 60s gateway-timeout before learning
                    // the index isn't ready. Once _lastKnownIndexState reports a usable
                    // status we forward normally; when it doesn't, the block below does a
                    // synchronous refresh-and-recheck before fast-failing so a ready index
                    // isn't masked by a stale mirror. Worker-side ListService has its own
                    // fast-fail for callers that bypass this short-circuit.
                    //
                    // Allow-list of tools that are blocked behind the STA thread and
                    // therefore benefit from the short-circuit. Gateway-served tools
                    // (whoami, recipe, kb_diff, sandbox, etc.) bypass naturally because they
                    // never hit the worker dispatcher. genexus_doctor is deliberately NOT
                    // listed: it runs off the STA thread (worker "health" method) and reads
                    // the on-disk snapshot, returning its own SearchIndexMissing/Empty report
                    // with retry hints — far more useful than a generic IndexNotReady while
                    // indexing, and it doubles as an escape hatch when the mirror is wrong.
                    if (string.Equals(tName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_query", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_inspect", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_search_source", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_analyze", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_explain", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_inject_context", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_db_optimize", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_api", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_types", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_edit", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_edit_form", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_edit_and_build", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_save_as", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_create_object", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_create_popup", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_bulk_edit", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_navigation", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_kb_explorer", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_run_object", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_diff_generated", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_what_if", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_db_drift", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_orient", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_security", StringComparison.OrdinalIgnoreCase))
                    {
                        IndexStateSnapshot idxSnap;
                        lock (_lastKnownIndexStateLock) { idxSnap = _lastKnownIndexState; }
                        bool indexUsable = IsIndexUsableForReads(idxSnap);
                        if (!indexUsable)
                        {
                            // The gateway's index mirror is only written by TryRefreshIndexStateFromWorkerAsync
                            // (whoami), so a "not usable" snapshot may just be STALE — the worker finished
                            // indexing but no whoami has refreshed the mirror since. Rather than fast-fail
                            // on a possibly-stale cache (which left agents stuck on IndexNotReady until they
                            // manually called whoami), do ONE bounded synchronous refresh and re-check.
                            // GetIndexState runs off the worker's STA thread, so this stays fast even while a
                            // cold-start BulkIndex is in flight. Skip the refresh when the mirror was just
                            // refreshed (index genuinely still building) so tight retry loops don't each pay
                            // a round-trip.
                            bool cacheStale = idxSnap == null
                                || idxSnap.RefreshedAtUtc == DateTime.MinValue
                                || (DateTime.UtcNow - idxSnap.RefreshedAtUtc).TotalSeconds > 2;
                            if (cacheStale && await TryRefreshIndexStateFromWorkerAsync(timeoutMs: 1200))
                            {
                                lock (_lastKnownIndexStateLock) { idxSnap = _lastKnownIndexState; }
                                indexUsable = IsIndexUsableForReads(idxSnap);
                            }
                        }
                        if (!indexUsable)
                        {
                            var indexingEnvelope = new JObject
                            {
                                ["status"] = "Indexing",
                                ["code"] = "IndexNotReady",
                                ["indexStatus"] = idxSnap?.Status ?? "Cold",
                                ["totalObjects"] = idxSnap?.TotalObjects ?? 0,
                                ["message"] = BuildIndexingMessage(idxSnap?.Status, idxSnap?.Progress, idxSnap?.EtaMs),
                                ["hint"] = "Call genexus_whoami to observe progress, then re-issue this tool."
                            };
                            if (idxSnap?.Progress != null) indexingEnvelope["progress"] = idxSnap.Progress.Value;
                            if (idxSnap?.EtaMs != null) indexingEnvelope["etaMs"] = idxSnap.EtaMs.Value;
                            return BuildToolResultContent(indexingEnvelope, false, tName, tArgs);
                        }
                    }

                    // Gateway-served tools (no worker involvement)
                    if (string.Equals(tName, "genexus_whoami", StringComparison.OrdinalIgnoreCase))
                    {
                        bool whoamiVerbose = tArgs?["verbose"]?.ToObject<bool>() ?? false;
                        JObject whoami = await BuildWhoamiPayloadAsync(
                            whoamiVerbose,
                            sessionContextEnabled ? sessionId : null);
                        return BuildToolResultContent(whoami, false, tName, tArgs);
                    }

                    if (string.Equals(tName, "genexus_recipe", StringComparison.OrdinalIgnoreCase))
                    {
                        string action = tArgs?["action"]?.ToString()?.ToLowerInvariant();
                        JObject payload;
                        bool isErr;

                        if (string.Equals(action, "suggest_macro", StringComparison.OrdinalIgnoreCase))
                        {
                            int windowMinutes = tArgs?["windowMinutes"]?.ToObject<int?>() ?? 30;
                            int minReps = tArgs?["minRepetitions"]?.ToObject<int?>() ?? 3;
                            var svc = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                            payload = svc.Suggest(windowMinutes, minReps);
                            isErr = string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (string.Equals(action, "crystallize", StringComparison.OrdinalIgnoreCase))
                        {
                            string macroName = tArgs?["macroName"]?.ToString();
                            string description = tArgs?["description"]?.ToString();
                            var steps = tArgs?["steps"] as JArray;

                            // If steps were not supplied, try to re-derive from current history
                            // using the proposedName as the discriminator.
                            if (steps == null || steps.Count == 0)
                            {
                                var svc = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                                JObject sugg = svc.Suggest(60, 2);
                                if (sugg["candidateMacros"] is JArray arr)
                                {
                                    foreach (var c in arr)
                                    {
                                        if (string.Equals(c?["proposedName"]?.ToString(), macroName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            steps = c["steps"] as JArray;
                                            if (string.IsNullOrWhiteSpace(description))
                                                description = c["suggestedDescription"]?.ToString();
                                            break;
                                        }
                                    }
                                }
                            }

                            var svc2 = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                            payload = svc2.Crystallize(macroName, description, steps);
                            isErr = string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            // Default: legacy behavior. action=list/describe via RecipeCatalog.Get.
                            // If action is provided and is list/describe, route via Dispatch.
                            string recipeName = tArgs?["name"]?.ToString();
                            if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(action, "describe", StringComparison.OrdinalIgnoreCase))
                            {
                                payload = RecipeCatalog.Dispatch(action, recipeName);
                            }
                            else if (!string.IsNullOrEmpty(action))
                            {
                                payload = new JObject
                                {
                                    ["status"] = "Error",
                                    ["error"] = $"Unknown action '{action}'.",
                                    ["hint"] = "Supported: list, describe, suggest_macro, crystallize."
                                };
                            }
                            else
                            {
                                payload = RecipeCatalog.Get(recipeName);
                            }
                            isErr = payload?["error"] != null || string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                        }

                        return BuildToolResultContent(payload, isErr, tName, tArgs);
                    }

                    // Async build intercept (Tasks 4.3 + 4.4):
                    // build / rebuild actions go through path selection:
                    //   - estimated_seconds < BuildSyncThresholdSeconds  → sync fast-path (fall through)
                    //   - estimated_seconds >= BuildSyncThresholdSeconds  → async Task.Run, return job_id immediately
                    if (string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(lcAction, "build", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(lcAction, "build_all", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(lcAction, "rebuild", StringComparison.OrdinalIgnoreCase))
                        && !IsLifecycleBuildDryRun(tArgs))
                    {
                        // Issue #27 item 2: prefer a data-driven estimate (median of recent
                        // build wall-clocks for this action) over the flat 60/120 the reporter
                        // saw. Routing still keys on an EXPLICIT caller estimate only, so the
                        // sync/async split is unchanged for callers that don't pass one — the
                        // historical value only makes the reported estimated_seconds realistic
                        // (history is recorded on async builds; letting it force the sync path
                        // would create an oscillation the caller never asked for).
                        int? callerEstimate = tArgs?["estimated_seconds"]?.ToObject<int?>();
                        int estimatedSeconds = callerEstimate
                                               ?? JobRegistry.EstimateBuildSeconds($"lifecycle/{lcAction}")
                                               ?? (string.Equals(lcAction, "rebuild", StringComparison.OrdinalIgnoreCase) ? 120 : 60);
                        int threshold = _activeConfig?.Server?.BuildSyncThresholdSeconds ?? 20;

                        bool useSync = callerEstimate.HasValue && BuildPathSelector.UseSync(callerEstimate.Value, threshold);
                        if (!useSync)
                        {
                            // --- ASYNC PATH (Task 4.3) ---
                            // Register the job first, then fire-and-forget the actual build.
                            // The worker call is synchronous over the JSON-RPC pipe, so we wrap
                            // it in Task.Run so the gateway thread returns to the caller immediately.
                            var job = JobRegistry.Start(sessionId, $"lifecycle/{lcAction}", estimatedSeconds);
                            Log($"[AsyncBuild] Dispatching job={job.Id} action={lcAction} target={tArgs?["target"]?.ToString() ?? "(all)"} estimated={estimatedSeconds}s");

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    // Step 1: Kick off the build on the worker — returns {status:"Accepted", taskId:...}
                                    // v2.3.8 (Task 5.2) — forward callee-expansion knobs through the async path.
                                    // Keep dryRun out of this branch entirely; the condition above routes
                                    // previews through the normal worker command, where BuildDryRun runs.
                                    var buildCmd = BuildAsyncLifecycleCommand(lcAction, tArgs, job.Id);

                                    JObject? ackEnvelope = await SendWorkerCommandAsync(
                                        buildCmd,
                                        60000,
                                        $"Timeout starting async build (job={job.Id})",
                                        env => env,
                                        (_, correlationId) => new JObject { ["error"] = "Gateway timeout starting build.", ["correlationId"] = correlationId },
                                        toolName: tName, toolArgs: tArgs, trackOperation: false);

                                    JObject? ack = (ackEnvelope?["result"] as JObject) ?? ackEnvelope;
                                    if (ack == null || ack["error"] != null)
                                    {
                                        JobRegistry.Complete(job.Id, false,
                                            $"Build start failed: {ack?["error"]?.ToString() ?? "unknown error"}", ack);
                                        return;
                                    }

                                    string? taskId = ack["taskId"]?.ToString();
                                    // Issue #27 item 1: record the worker task id on the job so a
                                    // later status/result poll can reconcile against the worker's
                                    // live build-task state if this background poller wedges.
                                    if (!string.IsNullOrEmpty(taskId)) job.WorkerTaskId = taskId;
                                    if (string.IsNullOrEmpty(taskId))
                                    {
                                        // No taskId means the worker returned a synchronous result already
                                        // (or an error response). Complete with what we have.
                                        bool syncSuccess = !string.Equals(ack["status"]?.ToString(), "Error",
                                            StringComparison.OrdinalIgnoreCase) && ack["error"] == null;
                                        JobRegistry.Complete(job.Id, syncSuccess,
                                            syncSuccess ? "Build completed (sync)" : $"Build error: {ack["error"]?.ToString() ?? "unknown"}",
                                            ack);
                                        return;
                                    }

                                    Log($"[AsyncBuild] job={job.Id} taskId={taskId} — polling status until terminal");

                                    // v2.3.8 (Task 7.2) — register a CTS so lifecycle action=cancel
                                    // with this job_id can short-circuit the polling loop.
                                    var pollCt = JobRegistry.RegisterCancellation(job.Id);

                                    // Step 2: Poll worker Build/Status until status is terminal.
                                    // Terminal states from BuildTaskStatus: Succeeded | Failed | Error | Cancelled | ReorgRequired.
                                    JObject? finalStatus = null;
                                    int failedPolls = 0;
                                    int pollCount = 0;
                                    var hardCap = DateTime.UtcNow.AddMinutes(30);
                                    while (DateTime.UtcNow < hardCap)
                                    {
                                        if (pollCt.IsCancellationRequested)
                                        {
                                            // Best-effort: tell the worker to kill the MSBuild child if any.
                                            try
                                            {
                                                _ = SendWorkerCommandAsync(
                                                    new JObject { ["module"] = "Build", ["action"] = "Cancel", ["target"] = taskId },
                                                    5000, "cancel-fanout",
                                                    env => env,
                                                    (_, __) => new JObject(),
                                                    toolName: tName, toolArgs: tArgs, trackOperation: false);
                                            }
                                            catch { /* fire-and-forget */ }
                                            finalStatus = new JObject { ["status"] = "Cancelled", ["taskId"] = taskId };
                                            break;
                                        }
                                        // Adaptive poll: builds take minutes, so the 2s
                                        // interval only matters at the tail — but a fast
                                        // first probe (500ms) catches sync-fast builds that
                                        // finish between Start and the first Status call.
                                        await Task.Delay(pollCount == 0 ? 500 : 2000).ConfigureAwait(false);
                                        pollCount++;

                                        var statusCmd = new JObject
                                        {
                                            ["module"] = "Build",
                                            ["action"] = "Status",
                                            ["target"] = taskId
                                        };
                                        JObject? statusEnv = await SendWorkerCommandAsync(
                                            statusCmd,
                                            30000,
                                            $"Timeout polling build status (job={job.Id})",
                                            env => env,
                                            (_, correlationId) => new JObject { ["error"] = "Status poll timeout", ["correlationId"] = correlationId },
                                            toolName: tName, toolArgs: tArgs, trackOperation: false);

                                        // issue #113 — a dead worker must fail the job fast instead of
                                        // looping until hardCap (30 min) with the caller still waiting
                                        // on wait_until_done / transport. Any error envelope here means
                                        // the poll didn't reach the worker (crashed/exited/pipe gone);
                                        // a single miss is tolerated, consecutive misses are terminal.
                                        if (statusEnv == null || statusEnv["error"] != null)
                                        {
                                            failedPolls++;
                                            Log($"[AsyncBuild] job={job.Id} status poll failed ({failedPolls}/{BuildStatusPollPolicy.MaxConsecutiveFailures})"
                                                + (statusEnv?["error"] != null ? $": {statusEnv["error"]}" : ": empty response"));
                                            if (BuildStatusPollPolicy.ShouldAbort(failedPolls))
                                            {
                                                string abortMsg = "Worker process exited mid-build (no response to " + failedPolls
                                                    + " consecutive status polls). The build did NOT complete — check the crash ledger via "
                                                    + "genexus_whoami diagnostics, then re-run genexus_lifecycle action=build.";
                                                finalStatus = new JObject { ["status"] = "Failed", ["taskId"] = taskId, ["error"] = abortMsg };
                                                JobRegistry.Complete(job.Id, false, abortMsg, finalStatus);
                                                Log($"[AsyncBuild] job={job.Id} aborted: worker exited mid-build after {failedPolls} failed status polls.");
                                                return;
                                            }
                                            continue;
                                        }
                                        failedPolls = 0;

                                        finalStatus = (statusEnv?["result"] as JObject) ?? statusEnv;
                                        string? s = finalStatus?["status"]?.ToString() ?? finalStatus?["Status"]?.ToString();
                                        if (string.Equals(s, "Succeeded", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(s, "Cancelled", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(s, "ReorgRequired", StringComparison.OrdinalIgnoreCase))
                                        {
                                            break;
                                        }
                                    }

                                    // Step 3: Complete the JobRegistry entry with the real final status.
                                    string? finalState = finalStatus?["status"]?.ToString() ?? finalStatus?["Status"]?.ToString() ?? "Timeout";
                                    var finalOutcome = finalStatus != null
                                        ? LifecycleResponseShaper.ClassifyBuildOutcome(finalStatus)
                                        : LifecycleResponseShaper.BuildOutcome.Error;
                                    bool success = finalOutcome == LifecycleResponseShaper.BuildOutcome.Success;
                                    int errs = finalStatus?["errorCount"]?.ToObject<int?>() ?? finalStatus?["ErrorCount"]?.ToObject<int?>() ?? 0;
                                    int warns = finalStatus?["warningCount"]?.ToObject<int?>() ?? finalStatus?["WarningCount"]?.ToObject<int?>() ?? 0;
                                    string summary = string.Equals(finalState, "ReorgRequired", StringComparison.OrdinalIgnoreCase)
                                        ? "Build All stopped because the KB requires reorganization; run action=reorg explicitly and retry."
                                        : success
                                        ? $"Build succeeded: {warns} warnings, {errs} errors"
                                        : $"Build {finalState}: {errs} errors, {warns} warnings";
                                    JobRegistry.Complete(job.Id, success, summary, finalStatus);
                                    Log($"[AsyncBuild] Completed job={job.Id} status={finalState} errors={errs} warnings={warns}");
                                }
                                catch (Exception ex)
                                {
                                    JobRegistry.Complete(job.Id, false, $"Build exception: {ex.Message}");
                                    Log($"[AsyncBuild] Exception in job={job.Id}: {ex.Message}");
                                }
                            });

                            // Friction 2026-05-22: wait_until_done=true blocks in a single turn
                            // up to MaxLongPollSeconds instead of forcing the caller to poll. Falls
                            // back to job_id+running if the build outruns the cap.
                            bool waitUntilDone = tArgs?["wait_until_done"]?.ToObject<bool?>() ?? false;
                            if (waitUntilDone)
                            {
                                int blockingCap = tArgs?["wait_seconds"]?.ToObject<int?>() ?? McpRouter.MaxLongPollSeconds;
                                var clientProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                                bool hasProgressToken = clientProgressToken != null && clientProgressToken.Type != JTokenType.Null;
                                string pendingLongPollKey = RegisterPendingLongPoll(
                                    sessionId,
                                    idToken,
                                    transportCancellation,
                                    out var longPollCancellationToken);
                                JObject pollResult;
                                try
                                {
                                    pollResult = await McpRouter.LongPollJob(
                                        JobRegistry, job.Id, blockingCap,
                                        progressToken: clientProgressToken,
                                        heartbeat: hasProgressToken ? (n => TryWriteStdout(n.ToString(Formatting.None))) : null,
                                        cancellationToken: longPollCancellationToken);
                                }
                                finally
                                {
                                    UnregisterPendingLongPoll(pendingLongPollKey);
                                }
                                // Classify the terminal status so the MCP envelope's isError
                                // matches the build outcome. LongPollJob surfaces JobEntry.Status
                                // which is one of: running, succeeded, failed, cancelled.
                                // running == we hit the long-poll cap without termination — not an
                                // error per se, the caller can re-poll.
                                //
                                // Friction 2026-05-22 item 10: this used to compare against
                                // "completed" (which the registry never emits — it stamps
                                // "succeeded"/"failed"). Result: every successful build wrapped
                                // in an error envelope. Fix routes the inner BuildTaskStatus
                                // through ClassifyBuildOutcome so 0/0/exit=0 = success and
                                // partial_success surfaces as a warning marker, not an error.
                                string terminalStatus = pollResult["status"]?.ToString();
                                bool stillRunning = string.Equals(terminalStatus, "running", StringComparison.OrdinalIgnoreCase);
                                bool isErr;
                                if (stillRunning) isErr = false;
                                else if (pollResult["result"] is JObject buildPayloadFinal)
                                {
                                    var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(buildPayloadFinal);
                                    isErr = outcome == LifecycleResponseShaper.BuildOutcome.Error;
                                    if (outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess)
                                    {
                                        pollResult["partial_success"] = true;
                                        if (pollResult["envelope"] == null) pollResult["envelope"] = "warning";
                                    }
                                }
                                else
                                {
                                    // No structured result — trust the registry summary string.
                                    bool succeeded = string.Equals(terminalStatus, "succeeded", StringComparison.OrdinalIgnoreCase);
                                    isErr = !succeeded;
                                }
                                if (!isErr
                                    && LifecycleResponseShaper.ShouldCompact(tArgs)
                                    && pollResult["result"] is JObject innerResult2)
                                {
                                    try { pollResult["result"] = LifecycleResponseShaper.CompactObject(innerResult2); } // perf: no serialize→parse round-trip
                                    catch { /* shaper passthrough on non-JSON */ }
                                }
                                return BuildToolResultContent(pollResult, isErr, tName, tArgs);
                            }

                            // Return immediately with job_id
                            var asyncResponse = BuildAsyncLifecycleAcceptedPayload(job, lcAction);
                            if (McpTasksProtocol.SupportsTasks(request))
                            {
                                return McpTasksProtocol.BuildCreateTaskResult(
                                    job,
                                    asyncResponse["hint"]?.ToString() ?? "Build accepted; poll tasks/get for completion.");
                            }
                            return BuildToolResultContent(asyncResponse, false, tName, tArgs);
                        }
                        // else: UseSync == true → fall through to the normal synchronous dispatch below
                        Log($"[AsyncBuild] Short build (estimated={estimatedSeconds}s < threshold={threshold}s): using sync fast-path");
                    }

                    object? rawWorkerCmd = null;
                    if (string.Equals(tName, "genexus_export_object", StringComparison.OrdinalIgnoreCase))
                    {
                        rawWorkerCmd = new
                        {
                            module = "Object",
                            action = "ExportText",
                            target = tArgs?["name"]?.ToString(),
                            path = tArgs?["outputPath"]?.ToString(),
                            part = tArgs?["part"]?.ToString() ?? "Source",
                            type = tArgs?["type"]?.ToString(),
                            overwrite = tArgs?["overwrite"]?.ToObject<bool?>() ?? false
                        };
                    }
                    else if (string.Equals(tName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
                    {
                        rawWorkerCmd = new
                        {
                            module = "Object",
                            action = "ImportText",
                            target = tArgs?["name"]?.ToString(),
                            path = tArgs?["inputPath"]?.ToString(),
                            part = tArgs?["part"]?.ToString() ?? "Source",
                            type = tArgs?["type"]?.ToString()
                        };
                    }

                    try
                    {
                        rawWorkerCmd ??= McpRouter.ConvertToolCall(fullReq);
                    }
                    catch (UsageException ux)
                    {
                        // Return as error result (not JSON-RPC level — that happens in wrapper below)
                        return new JObject
                        {
                            ["isError"] = true,
                            ["usageException"] = true,
                            ["code"] = -32602,
                            ["message"] = ux.Message,
                            ["usageCode"] = ux.Code
                        };
                    }

                    var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
                    if (workerCmd == null)
                    {
                        return BuildToolResultContent(
                            new JObject { ["error"] = "Tool conversion produced no worker command." },
                            isError: true,
                            toolName: tName,
                            toolArgs: tArgs);
                    }

                    workerCmd["client"] = "mcp";
                    AttachClientRequestIdentity(workerCmd, tArgs);
                    int timeoutMs = GetToolTimeoutMs(tName, tArgs);

                    // async=true on edit/variable tools → fire-and-forget; result piggybacks via _meta.background_jobs.
                    bool isAsyncGxServer = (tArgs?["async"]?.ToObject<bool?>() ?? false)
                                           && IsAsyncGxServerAction(tName, tArgs);
                    // A preview is deliberately synchronous: it must never be represented as
                    // a background mutation job, acquire a second operation identity, or outlive
                    // the caller. The worker's dry-run branch performs no Save.
                    bool editAsync = ShouldRunMutationAsync(tName, tArgs) || isAsyncGxServer;
                    if (editAsync)
                    {
                        // gxserver update on a stale KB runs many minutes; give it a longer
                        // default estimate so the poll cadence is sensible.
                        int estEdit = tArgs?["estimated_seconds"]?.ToObject<int?>() ?? (isAsyncGxServer ? 120 : 30);
                        string jobLabel = isAsyncGxServer ? $"gxserver/{tArgs?["action"]?.ToString()}" : $"edit/{tName}";
                        var editJob = JobRegistry.Start(sessionId, jobLabel, estEdit);
                        editJob.WorkerAlias = _currentKb.Value?.NormalizedAlias;
                        editJob.Target = tArgs?["name"]?.ToString();
                        editJob.Part = tArgs?["part"]?.ToString() ?? "Source";
                        editJob.ObjectType = tArgs?["type"]?.ToString();
                        Log($"[AsyncEdit] Dispatching job={editJob.Id} tool={tName} estimated={estEdit}s");
                        // v2.6.2 (Item B): inject cancelToken=jobId so the worker's
                        // blanket-register at dispatch entry makes lifecycle cancel resolvable.
                        if (workerCmd?["params"] is JObject capturedParams)
                            capturedParams["cancelToken"] = editJob.Id;
                        var capturedCmd = workerCmd;
                        var capturedName = tName;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Issue #79: SendWorkerCommandAsync is called with timeoutMs=0
                                // (wait forever) because a legitimately slow SDK save must not be
                                // cut off — but that means a BLOCKED SDK call (IDE modal dialog
                                // holding the model, or the SDK retrying a failing validation
                                // internally) left the job 'running' indefinitely with no
                                // actionable signal. Race the worker wait against a generous
                                // watchdog bound; on fire, mark the job terminal 'stalled' with
                                // recovery steps instead of hanging forever. gxserver update/commit
                                // is excluded (a server apply can legitimately run arbitrarily
                                // long — an 850-object changelist exceeded the 10 min sync
                                // ceiling), so its jobs wait without a stall bound.
                                int watchdogMs = isAsyncGxServer ? int.MaxValue : AsyncEditWatchdogMs(estEdit);
                                var cancelToken = JobRegistry.RegisterCancellation(editJob.Id);
                                var watchdogDelay = Task.Delay(watchdogMs, cancelToken);
                                var workerTask = SendWorkerCommandAsync(
                                    capturedCmd, 0,
                                    $"Timeout waiting for async edit: {capturedName}",
                                    r => r, (_, __) => new JObject { ["status"] = "Running" },
                                    operationIdentity: editJob.Id);
                                var completed = await Task.WhenAny(workerTask, watchdogDelay).ConfigureAwait(false);
                                if (completed != workerTask)
                                {
                                    // Cancelled (delay faulted via the CTS) or deadline hit. A
                                    // cancel already flipped the job to 'cancelled'; only stall
                                    // when it is genuinely still running.
                                    var now = JobRegistry.Get(editJob.Id);
                                    if (now != null && string.Equals(now.Status, "running", StringComparison.OrdinalIgnoreCase))
                                    {
                                        int boundSeconds = watchdogMs == int.MaxValue ? -1 : watchdogMs / 1000;
                                        string boundText = boundSeconds > 0 ? boundSeconds + "s" : "unbounded (watchdog disabled)";
                                        // Plan 069: a genuinely stalled job means the worker's STA
                                        // thread is stuck inside a blocked SDK call that will never
                                        // answer the in-flight command. Marking the job 'stalled'
                                        // alone left the KB wedged until the 15-min health-loop
                                        // detector killed the process. Recycle the worker NOW
                                        // (force-kill + Wedged → eager respawn) so the KB is usable
                                        // again right away instead of ~15 minutes later. _currentKb
                                        // is an AsyncLocal, so it still resolves the KB this job
                                        // was dispatched against from inside this Task.Run.
                                        bool workerRecycled = false;
                                        var stalledKb = _currentKb.Value;
                                        // A microsecond race: the worker may have answered between
                                        // WhenAny returning the watchdog and this branch. Never
                                        // force-kill a worker that just completed its save — only
                                        // recycle when the command is genuinely still in flight.
                                        if (stalledKb == null)
                                        {
                                            Log($"[AsyncEdit] No KB resolved for job={editJob.Id}; skipping stalled-worker recycle (health loop will reap the wedged process).");
                                        }
                                        else if (workerTask.IsCompleted)
                                        {
                                            Log($"[AsyncEdit] Job={editJob.Id} worker answered just after the watchdog fired — skipping recycle.");
                                        }
                                        else if (_workerPool == null)
                                        {
                                            Log($"[AsyncEdit] No worker pool available for job={editJob.Id}; skipping stalled-worker recycle (health loop will reap the wedged process).");
                                        }
                                        else
                                        {
                                            try { workerRecycled = _workerPool.RecycleStalledWorker(stalledKb.NormalizedAlias); }
                                            catch (Exception recycleEx)
                                            {
                                                Log($"[AsyncEdit] Stalled-worker recycle failed for KB '{stalledKb.Alias}': {recycleEx.Message}");
                                            }
                                        }
                                        JobRegistry.Stall(
                                            editJob.Id,
                                            capturedName + " did not return within the " + boundText
                                                + " time bound; SDK call likely blocked (IDE modal dialog or retrying validation) — see result for recovery steps.",
                                            BuildStalledAsyncMutationEnvelope(editJob.Id, capturedName, estEdit, boundSeconds, workerRecycled));
                                        _mutationRecovery.RequireRead(
                                            editJob.WorkerAlias,
                                            editJob.Target,
                                            editJob.Part,
                                            editJob.Id);
                                        Log($"[AsyncEdit] Watchdog fired for job={editJob.Id} tool={capturedName} after {watchdogMs}ms — marked stalled (workerRecycled={workerRecycled}).");
                                    }
                                    return;
                                }
                                var inner = await workerTask;
                                bool ok = IsSuccessfulBackgroundToolCompletion(inner);
                                JobRegistry.Complete(editJob.Id, ok, BuildAsyncMutationCompletionSummary(capturedName, ok), inner);
                            }
                            catch (Exception ex)
                            {
                                string failurePrefix = string.Equals(capturedName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(capturedName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(capturedName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(capturedName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase)
                                    ? "Variable update exception"
                                    : "Edit exception";
                                JobRegistry.Complete(editJob.Id, false, $"{failurePrefix}: {ex.Message}");
                                Log($"[AsyncEdit] Exception in job={editJob.Id}: {ex.Message}");
                            }
                        });
                        var asyncEditResponse = isAsyncGxServer
                            ? BuildAsyncAcceptedPayload(editJob, $"GXserver {tArgs?["action"]?.ToString()} accepted;")
                            : (string.Equals(tName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(tName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(tName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(tName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase)
                            ? BuildAsyncVariableAcceptedPayload(editJob)
                            : BuildAsyncEditAcceptedPayload(editJob));
                        if (McpTasksProtocol.SupportsTasks(request))
                        {
                            return McpTasksProtocol.BuildCreateTaskResult(
                                editJob,
                                asyncEditResponse["hint"]?.ToString() ?? "Operation accepted; poll tasks/get for completion.");
                        }
                        return BuildToolResultContent(asyncEditResponse, false, tName, tArgs);
                    }

                    JObject? innerResult = null;
                    // MCP keepalive: when the client supplied a progressToken, emit
                    // notifications/progress while the worker runs so long synchronous
                    // tools (apply_pattern, delete, analyze, …) don't trip the client's
                    // request timeout. No-op when the client omits the token.
                    var toolProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                    bool toolHasProgressToken = toolProgressToken != null && toolProgressToken.Type != JTokenType.Null;
                    // C1 (race fix): capture the cache epoch before dispatch; if a concurrent
                    // mutation clears the cache while this read is in flight, the store below
                    // is skipped because the epoch will no longer match.
                    int cacheEpochAtDispatch = System.Threading.Interlocked.CompareExchange(ref SemanticCacheEpoch, 0, 0);
                    innerResult = await SendWorkerCommandAsync(
                        workerCmd,
                        timeoutMs,
                        $"Timeout waiting for tool: {tName}",
                        resultObj =>
                        {
                            JToken? finalResult = null;
                            try {
                                finalResult = TruncateResponseIfNeeded(resultObj["result"] ?? resultObj["error"], tName);
                            } catch (Exception exTrunc) {
                                Log($"[Gateway] Error during truncation: {exTrunc.Message}");
                                finalResult = resultObj["result"] ?? resultObj["error"];
                            }

                            if (string.Equals(tName, "genexus_edit_and_build", StringComparison.OrdinalIgnoreCase)
                                && finalResult is JObject editAndBuildPayload)
                            {
                                NormalizeEditAndBuildPayload(editAndBuildPayload);
                            }

                            bool isErr = resultObj["error"] != null || string.Equals(resultObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                            // Inner-payload error detection — tools that return their
                            // failure envelope as result.{error|status} (e.g. genexus_read
                            // returning {"part":"Source","error":"Part 'Source' not found ..."})
                            // were previously sent with isError=false because we only checked
                            // the outer envelope. Mirror the inner shape so MCP clients that
                            // branch on isError stay correct.
                            if (!isErr && finalResult is JObject innerErrObj)
                            {
                                bool innerHasError = innerErrObj["error"] != null
                                    || string.Equals(innerErrObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(innerErrObj["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(innerErrObj["status"]?.ToString(), "NotImplemented", StringComparison.OrdinalIgnoreCase);
                                if (innerHasError) isErr = true;
                            }

                            if (!isErr && string.Equals(tName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                            {
                                _mutationRecovery.ConfirmRead(
                                    kbScope,
                                    tArgs?["name"]?.ToString(),
                                    tArgs?["part"]?.ToString() ?? "Source");
                            }

                            // Friction 2026-05-22 #63: attach suggested_next_step on every error
                            // envelope (verbose OR terse). McpRouter.AttachSuggestedNextStep is a
                            // pure pattern-match over code/message text; routes patch NoMatch →
                            // near-match inspection, visual write failures → LayoutGotchaScanner,
                            // KB_AMBIGUOUS → kb=<alias>, spc0150 → extract_to_procedure recipe.
                            if (isErr && finalResult is JObject preTrimErr && preTrimErr["suggested_next_step"] == null)
                            {
                                var hint = McpRouter.AttachSuggestedNextStep(preTrimErr);
                                if (hint != null) preTrimErr["suggested_next_step"] = hint;
                            }

                            // TerseErrors: trim error envelopes to {message, code, hint} by default.
                            // verbose_errors=true restores the full payload. Gated by PerfProfile.V1Enabled.
                            // Runs BEFORE ResponseSizeGuard so the guard sees the trimmed (smaller) payload.
                            if (isErr && PerfProfile.V1Enabled && finalResult is JObject errObj)
                            {
                                bool verbose = tArgs?["verbose_errors"]?.ToObject<bool?>() ?? false;
                                finalResult = McpRouter.TrimErrorEnvelope(errObj, verbose);
                            }

                            // ResponseSizeGuard: replace oversize JObject results with a truncation sentinel.
                            // Gated by PerfProfile.V1Enabled so it can be disabled via MCP_PERF_PROFILE=legacy.
                            if (!isErr && PerfProfile.V1Enabled && finalResult is JObject finalJObj)
                            {
                                var guard = new ResponseSizeGuard();
                                var (guarded, _) = guard.Apply(finalJObj, tName, tArgs);
                                finalResult = guarded;
                            }

                            // StripNulls: remove null-valued properties from the result to reduce wire size.
                            // Runs after ResponseSizeGuard so the guard measures the pre-stripped payload.
                            if (PerfProfile.V1Enabled && finalResult is JObject stripObj)
                            {
                                McpRouter.StripNulls(stripObj);
                            }

                            // v2.3.8 (Task 6.1) — compact lifecycle status by default.
                            // Replaces the verbose BuildTaskStatus payload (full Errors/Warnings/Output)
                            // with counts + top-10 errors + warning dedup. compact=false restores legacy shape.
                            if (!isErr
                                && string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(lcAction, "status", StringComparison.OrdinalIgnoreCase)
                                && finalResult is JObject lifecycleObj
                                && LifecycleResponseShaper.ShouldCompact(tArgs))
                            {
                                // perf: tree-based compact, no serialize→parse round-trip.
                                // The old code wrapped a parse failure; CompactObject works on
                                // an already-parsed JObject and cannot throw for JSON reasons.
                                finalResult = LifecycleResponseShaper.CompactObject(lifecycleObj);
                            }

                            // The worker envelope has no further consumers after this callback.
                            // Transfer the selected payload out of it so KB metadata can be
                            // attached without cloning the entire response tree.
                            DetachResponsePayload(resultObj, finalResult);
                            var toolResult = BuildToolResultContent(finalResult, isErr, tName, tArgs, payloadOwned: true);
                            if (cKey != null)
                            {
                                var cacheMeta = toolResult["_meta"] as JObject ?? new JObject();
                                toolResult["_meta"] = cacheMeta;
                                cacheMeta["cacheOutcome"] = "miss";
                            }

                            // v2.3.8 (post-self-review) — don't cache transient envelopes.
                            // A "Reindexing"/"IndexCold"/"Timeout"/"Cancelled"/"BuildPlanTooLarge"
                            // response is a snapshot of the worker's current state, not a stable
                            // semantic answer. Caching it kept analyze impact pinned to the
                            // first response (often Timeout during a reindex), so callers got
                            // the same stale envelope on every retry until cache eviction.
                            bool isTransient = false;
                            if (finalResult is JObject transientCheck)
                            {
                                var s = transientCheck["status"]?.ToString();
                                isTransient = string.Equals(s, "Reindexing", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "IndexCold", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "Timeout", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "Cancelled", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "Running", StringComparison.OrdinalIgnoreCase);
                            }

                            // PERF: `cKey != null` also excludes mutating tools (which
                            // cleared the cache and must not pollute it with a write
                            // result), plus parameter-dependent side effects recognized by
                            // OperationClassifier. The old `Contains("write")/Contains("patch")`
                            // check missed tools like genexus_edit.
                            // C1 (race fix): if a mutation invalidated the cache while this read
                            // was in flight, the pre-mutation envelope must not be stored.
                            if (!isErr && !isTransient && !isLiveTool && cKey != null
                                && System.Threading.Volatile.Read(ref SemanticCacheEpoch) == cacheEpochAtDispatch
                                && _semanticCache.GetRevision(kbScope) == cacheRevisionAtDispatch)
                            {
                                // Store full envelope in semantic cache (rebuilt on hit above)
                                _semanticCache.Set(cKey, new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["result"] = toolResult
                                });
                            }

                            return toolResult;
                        },
                        (operationId, correlationId) =>
                        {
                            string message = $"GeneXus MCP Worker timed out executing tool: {tName}.";
                            var timeoutPayload = new JObject
                            {
                                ["status"] = "Running",
                                ["error"] = "Gateway timeout waiting for worker response.",
                                ["message"] = message,
                                ["correlationId"] = correlationId,
                                ["retriable"] = true
                            };

                            bool recordWrite = IsTransactionRecordOperation(tName!, tArgs) && IsMutatingTool(tName!, tArgs);
                            if (recordWrite) MarkRecordWriteOutcomeUnknown(timeoutPayload);
                            var help = new JArray();
                            if (recordWrite)
                                help.Add("Do not repeat the write. Poll the original operation result, then query the record keys against the datastore.");
                            if (!string.IsNullOrWhiteSpace(operationId))
                            {
                                timeoutPayload["operationId"] = operationId;
                                help.Add($"Operation is still running. Query genexus_lifecycle(action='status', target='op:{operationId}') or action='result'.");
                                if (tName != null && (tName.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || tName.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || tName.IndexOf("variable", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    // Writes have usually persisted by the time the gateway times out; poll result, then read — don't retry the edit.
                                    help.Add("For long writes the change is usually already persisted; check action='result' once, then read back instead of retrying.");
                                    _mutationRecovery.RequireRead(
                                        kbScope,
                                        tArgs?["name"]?.ToString(),
                                        tArgs?["part"]?.ToString() ?? "Source",
                                        operationId);
                                    timeoutPayload["reReadRequired"] = true;
                                }
                            }
                            else if (!recordWrite)
                            {
                                help.Add("Retry with narrower scope or lower limit.");
                            }
                            timeoutPayload["help"] = help;

                            return BuildToolResultContent(timeoutPayload, isError: true, toolName: tName, toolArgs: tArgs);
                        },
                        toolName: tName,
                        toolArgs: tArgs,
                        trackOperation: true,
                        progressToken: toolProgressToken,
                        heartbeat: toolHasProgressToken ? (n => TryWriteStdout(n.ToString(Formatting.None))) : null,
                        // E9: bind this worker request to the MCP client request id so
                        // notifications/cancelled can find and abort it (the _pendingRequests
                        // key is a gateway GUID, invisible to the client).
                        mcpRequestId: idToken?.ToString(),
                        mcpRequestIdToken: idToken,
                        mcpSessionId: sessionId);

                    // apply_pattern { validate: true } — post-apply build of the
                    // generated host so the LLM sees compile failures in a single
                    // tool call. Without this the agent declared "Success" and only
                    // discovered the broken binding by opening the IDE. Runs OUTSIDE
                    // the SendWorkerCommandAsync onSuccess lambda (which is sync) so
                    // we can await the validation build here.
                    if (innerResult != null
                        && !(innerResult["isError"]?.ToObject<bool?>() ?? false)
                        && string.Equals(tName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase)
                        && (tArgs?["validate"]?.ToObject<bool?>() ?? false))
                    {
                        string applyText = (innerResult["content"] as JArray)?[0]?["text"]?.ToString() ?? "";
                        // Round-trip is structural: innerResult.content[0].text was serialized
                        // by BuildToolResultContent inside SendWorkerCommandAsync's sync onSuccess
                        // lambda (after truncation/normalization) and no parsed payload survives;
                        // we must parse to read patternHost AND to fold the validation block back in.
                        JObject applyPayload = null;
                        try { applyPayload = JObject.Parse(applyText); } catch { }
                        string hostName = applyPayload?["patternHost"]?.ToString();

                        var vBlock = new JObject { ["target"] = hostName };
                        if (string.IsNullOrWhiteSpace(hostName))
                        {
                            vBlock["status"] = "skipped";
                            vBlock["reason"] = "No patternHost in apply response — nothing to validate.";
                        }
                        else
                        {
                            // Worker's BuildService.Build is async internally — kicks off
                            // Task.Run and returns a Running envelope with a taskId in ms.
                            // We must (1) fire Build/Build, (2) poll Build/Status with that
                            // taskId until terminal, (3) fetch Build/Result for errors.
                            var vSw = System.Diagnostics.Stopwatch.StartNew();
                            JObject startCmd = new JObject
                            {
                                ["module"] = "Build",
                                ["action"] = "Build",
                                ["target"] = hostName,
                                ["includeCallees"] = "direct"
                            };
                            JObject? startEnv = await SendWorkerCommandAsync(
                                startCmd, 10000,
                                "validate-start timeout",
                                env => env,
                                (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                                toolName: "genexus_apply_pattern.validate.start",
                                toolArgs: null, trackOperation: false);

                            string taskId = null;
                            JObject startInner = startEnv?["result"] as JObject;
                            if (startInner == null && startEnv?["result"] is JValue sjv && sjv.Type == JTokenType.String)
                            {
                                try { startInner = JObject.Parse(sjv.ToString()); } catch { }
                            }
                            taskId = startInner?["TaskId"]?.ToString() ?? startInner?["taskId"]?.ToString();

                            JObject terminal = null;
                            if (!string.IsNullOrEmpty(taskId))
                            {
                                // Poll up to 180s. Adaptive interval: most validation
                                // builds of a single object finish in 1-3s, so poll fast
                                // (250ms) for the first 2s and fall back to 1s — cuts the
                                // common-case wait from ~1-4s to ~0.25-1s.
                                for (int i = 0; i < 180 && vSw.ElapsedMilliseconds < 180000; i++)
                                {
                                    await Task.Delay(i < 8 ? 250 : 1000);
                                    var statusCmd = new JObject
                                    {
                                        ["module"] = "Build",
                                        ["action"] = "Status",
                                        ["target"] = taskId
                                    };
                                    JObject? statusEnv = await SendWorkerCommandAsync(
                                        statusCmd, 8000, "validate-poll timeout",
                                        env => env,
                                        (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                                        toolName: "genexus_apply_pattern.validate.poll",
                                        toolArgs: null, trackOperation: false);
                                    JObject sObj = statusEnv?["result"] as JObject;
                                    if (sObj == null && statusEnv?["result"] is JValue pjv && pjv.Type == JTokenType.String)
                                    {
                                        try { sObj = JObject.Parse(pjv.ToString()); } catch { }
                                    }
                                    string sStatus = sObj?["Status"]?.ToString() ?? sObj?["status"]?.ToString();
                                    if (!string.IsNullOrEmpty(sStatus) && !string.Equals(sStatus, "Running", StringComparison.OrdinalIgnoreCase))
                                    {
                                        terminal = sObj;
                                        break;
                                    }
                                }
                            }
                            vSw.Stop();
                            vBlock["durationMs"] = vSw.ElapsedMilliseconds;

                            if (string.IsNullOrEmpty(taskId))
                            {
                                vBlock["status"] = "error";
                                vBlock["error"] = "Worker did not return a taskId for the validation build.";
                                if (startInner != null) vBlock["startResponse"] = startInner;
                            }
                            else if (terminal == null)
                            {
                                vBlock["status"] = "timeout";
                                vBlock["error"] = "Validation build did not finish in 180s.";
                                vBlock["taskId"] = taskId;
                            }
                            else
                            {
                                int errorCount = terminal["ErrorCount"]?.ToObject<int?>() ?? terminal["errorCount"]?.ToObject<int?>() ?? (terminal["Errors"] as JArray)?.Count ?? 0;
                                int warningCount = terminal["WarningCount"]?.ToObject<int?>() ?? terminal["warningCount"]?.ToObject<int?>() ?? (terminal["Warnings"] as JArray)?.Count ?? 0;
                                string termStatus = terminal["Status"]?.ToString() ?? "";
                                bool buildOk = errorCount == 0
                                    && !string.Equals(termStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                                    && terminal["error"] == null;
                                vBlock["status"] = buildOk ? "ok" : "failed";
                                vBlock["errorCount"] = errorCount;
                                vBlock["warningCount"] = warningCount;
                                vBlock["taskId"] = taskId;
                                var errArr = terminal["Errors"] as JArray ?? terminal["errors"] as JArray;
                                if (errArr != null && errArr.Count > 0)
                                    vBlock["errors"] = new JArray(errArr.Take(10));
                                var warnArr = terminal["Warnings"] as JArray ?? terminal["warnings"] as JArray;
                                if (warnArr != null && warnArr.Count > 0)
                                    vBlock["warnings"] = new JArray(warnArr.Take(5));
                            }
                        }

                        // Fold the validation block back into the toolResult text
                        // payload, and promote isError when validation didn't pass.
                        if (applyPayload != null)
                        {
                            applyPayload["validation"] = vBlock;
                            var newText = applyPayload.ToString(Formatting.None);
                            var contentArr = innerResult["content"] as JArray;
                            if (contentArr != null && contentArr.Count > 0 && contentArr[0] is JObject c0)
                            {
                                c0["text"] = newText;
                            }
                            if (!string.Equals(vBlock["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(vBlock["status"]?.ToString(), "skipped", StringComparison.OrdinalIgnoreCase))
                            {
                                innerResult["isError"] = true;
                            }
                        }
                    }

                    return innerResult ?? BuildToolResultContent(
                        new JObject { ["error"] = "Tool did not produce a result." },
                        isError: true,
                        toolName: tName,
                        toolArgs: tArgs);
                }

                JObject toolInnerResult;
                try
                {
                    toolInnerResult = await idempotencyMiddleware.Invoke(toolCallParams, DispatchCore);
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
