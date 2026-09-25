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

        internal static bool IsLifecycleBuildDryRun(JObject? args)
        {
            return args?["dryRun"]?.ToObject<bool?>() == true;
        }

        internal static bool ShouldDispatchLifecycleBuildAsync(
            string? toolName, string? lifecycleAction, JObject? args)
        {
            if (!string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                || IsLifecycleBuildDryRun(args))
                return false;

            if (string.Equals(lifecycleAction, "build", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycleAction, "build_all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycleAction, "rebuild", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycleAction, "validate-kb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycleAction, "reorg", StringComparison.OrdinalIgnoreCase))
                return true;

            // A forced index rebuild is a long lifecycle operation as well.  A
            // non-forced index refresh remains on the existing lightweight path unless
            // the caller explicitly asks to wait for it.
            if (string.Equals(lifecycleAction, "index", StringComparison.OrdinalIgnoreCase))
                return args?["force"]?.ToObject<bool?>() == true
                    || args?["wait_until_done"]?.ToObject<bool?>() == true;

            return string.Equals(lifecycleAction, "specify", StringComparison.OrdinalIgnoreCase)
                && args?["wait_until_done"]?.ToObject<bool?>() == true;
        }

        /// <summary>
        /// Build a stable identity for lifecycle admission.  Wait/correlation fields
        /// are intentionally excluded: two callers asking for the same work with a
        /// different wait budget must share the one execution and operation id.
        /// </summary>
        internal static string BuildLifecycleRequestKey(string? action, JObject? args)
        {
            string normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            var normalized = new JObject
            {
                ["action"] = normalizedAction,
                ["targets"] = JArray.FromObject(NormalizeLifecycleTargets(args?["target"]?.ToString())),
                ["environment"] = (args?["environment"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant(),
                ["includeCallees"] = (args?["includeCallees"]?.ToString() ?? "transitive").Trim().ToLowerInvariant(),
                ["buildPlanCap"] = args?["buildPlanCap"]?.ToObject<int?>() ?? 200,
                ["skipFullDeploy"] = args?["skipFullDeploy"]?.ToObject<bool?>() ?? false,
                ["deploy"] = args?["deploy"]?.ToObject<bool?>() ?? false,
                ["callers"] = args?["callers"]?.ToObject<bool?>() ?? true,
                ["callerCap"] = args?["callerCap"]?.ToObject<int?>() ?? 0,
                ["fastIncremental"] = args?["fastIncremental"]?.ToObject<bool?>() ?? false,
                ["force"] = args?["force"]?.ToObject<bool?>() ?? false,
                ["limit"] = args?["limit"]?.ToObject<int?>() ?? 0,
                ["mode"] = (args?["mode"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant()
            };
            return normalized.ToString(Formatting.None);
        }

        internal static string[] NormalizeLifecycleTargets(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return Array.Empty<string>();
            return target
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static LifecycleOperationAliasResolution ResolveLifecycleOperationAlias(string? target)
            => LifecycleOperationAliasResolver.Resolve(target, JobRegistry, _operationTracker);

        private static int TryReconcileMutationRecoveryFromOperation(string operationId, JToken? payload)
        {
            if (_mutationRecovery == null || string.IsNullOrWhiteSpace(operationId) || !IsTerminalPersistedReread(payload)) return 0;
            string trackedStatus = _operationTracker.BuildOperationStatus(operationId)["status"]?.ToString();
            if (string.Equals(trackedStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
                return 0;
            JObject envelope = payload as JObject ?? new JObject();
            JObject evidence = envelope["workerPayload"] as JObject
                ?? envelope["result"] as JObject
                ?? envelope;
            _operationTracker.TryGetContext(operationId, out _, out JObject? operationArgs);
            int reconciled = 0;
            foreach (var requirement in _mutationRecovery.Pending
                .Where(item => string.Equals(item.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                if (!LateResultMatchesRecovery(requirement, evidence, operationArgs))
                    continue;
                bool cleared = MutationRecoveryRegistry.IsKbLevelRecovery(requirement)
                    ? _mutationRecovery.ConfirmVerifiedOperationRead(requirement)
                    : _mutationRecovery.ConfirmRead(
                        requirement.KbAlias,
                        requirement.Target,
                        requirement.Part,
                        requirement);
                if (cleared) reconciled++;
            }
            return reconciled;
        }

        private static bool LateResultMatchesRecovery(
            RecoveryRequirement requirement, JObject evidence, JObject? operationArgs)
        {
            if (MutationRecoveryRegistry.IsKbLevelRecovery(requirement))
                return true;
            string observedTarget = evidence["target"]?.ToString()
                ?? evidence["name"]?.ToString()
                ?? operationArgs?["name"]?.ToString()
                ?? operationArgs?["target"]?.ToString();
            string observedGuid = evidence["guid"]?.ToString()
                ?? evidence["targetGuid"]?.ToString()
                ?? operationArgs?["guid"]?.ToString()
                ?? operationArgs?["objectGuid"]?.ToString();
            string observedEntityKey = evidence["entityKey"]?.ToString()
                ?? operationArgs?["entityKey"]?.ToString();
            string observedType = evidence["type"]?.ToString()
                ?? operationArgs?["type"]?.ToString()
                ?? operationArgs?["typeFilter"]?.ToString();
            string observedPart = evidence["part"]?.ToString()
                ?? operationArgs?["part"]?.ToString();

            if (!string.IsNullOrWhiteSpace(requirement.TargetGuid)
                && !string.Equals(requirement.TargetGuid, observedGuid, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(requirement.TargetEntityKey)
                && !string.Equals(requirement.TargetEntityKey, observedEntityKey, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(requirement.TargetType)
                && !string.Equals(requirement.TargetType, observedType, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(observedTarget)
                && !string.Equals(requirement.Target, observedTarget, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requirement.TargetGuid, observedGuid, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(observedPart)
                && !string.Equals(requirement.Part, observedPart, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        internal static JObject BuildAsyncLifecycleCommand(string lifecycleAction, JObject args, string cancelToken)
        {
            string action = (lifecycleAction ?? string.Empty).Trim().ToLowerInvariant();
            string workerAction;
            string module = "Build";
            if (action == "reorg")
            {
                workerAction = "Reorg";
            }
            else if (action == "validate-kb")
            {
                module = "KB";
                workerAction = "ValidateConditions";
            }
            else if (action == "index")
            {
                module = "KB";
                workerAction = "BulkIndex";
            }
            else
            {
                bool rebuild = action == "rebuild";
                bool buildAll = action == "build_all";
                bool specify = action == "specify";
                bool compileCheck = action == "build"
                    && string.Equals(args?["mode"]?.ToString(), "compile_check", StringComparison.OrdinalIgnoreCase);
                workerAction = specify ? "Specify"
                    : compileCheck ? "CompileCheck"
                    : rebuild ? "RebuildAll"
                    : buildAll ? "BuildAll"
                    : "Build";
            }

            var command = new JObject
            {
                ["module"] = module,
                ["action"] = workerAction,
                ["target"] = args?["target"]?.ToString(),
                ["client"] = "mcp",
                ["includeCallees"] = args?["includeCallees"]?.ToString(),
                ["buildPlanCap"] = (int?)args?["buildPlanCap"],
                ["skipFullDeploy"] = (bool?)args?["skipFullDeploy"],
                ["callers"] = (bool?)args?["callers"] ?? true,
                ["callerCap"] = (int?)args?["callerCap"] ?? 0,
                ["environment"] = args?["environment"]?.ToString(),
                ["dryRun"] = (bool?)args?["dryRun"] ?? false,
                ["deploy"] = (bool?)args?["deploy"] ?? false,
                ["force"] = (bool?)args?["force"] ?? false,
                ["limit"] = (int?)args?["limit"],
                ["fastIncremental"] = (bool?)args?["fastIncremental"] ?? false,
                ["cancelToken"] = cancelToken,
                // The Worker FIFO is the safety boundary for lifecycle execution. The
                // flag is explicit on the wire so independent Gateways/legacy callers
                // share the same per-worker admission contract.
                ["queueLifecycle"] = true
            };
            return command;
        }

        internal static JObject BuildWorkerLifecycleStatusCommand(
            JobEntry job, JObject? args, int waitSeconds, string until)
        {
            var command = new JObject
            {
                ["module"] = "Build",
                ["action"] = "Status",
                ["target"] = job?.WorkerTaskId,
                ["wait"] = Math.Min(Math.Max(waitSeconds, 0), McpRouter.MaxLongPollSeconds),
                ["until"] = string.Equals(until, "terminal", StringComparison.OrdinalIgnoreCase)
                    ? "terminal"
                    : "change",
                // Keep the Worker payload unaggregated. The Gateway applies the
                // caller's compact preference after it has the warning delta.
                ["compact"] = false,
                ["page"] = args?["page"]?.ToObject<int?>() ?? 1,
                ["pageSize"] = args?["pageSize"]?.ToObject<int?>()
                    ?? args?["page_size"]?.ToObject<int?>()
                    ?? 50
            };
            string? since = args?["since"]?.ToString();
            if (!string.IsNullOrWhiteSpace(since)) command["since"] = since;
            return command;
        }

        private static async Task<JObject?> ReadWorkerLifecycleStatusAsync(
            JobEntry job,
            JObject? args,
            int waitSeconds,
            string until,
            JToken? progressToken,
            CancellationToken transportCancellation)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.WorkerTaskId)) return null;
            var command = BuildWorkerLifecycleStatusCommand(job, args, waitSeconds, until);
            int timeoutMs = Math.Min(900000, Math.Max(1000, (waitSeconds + 10) * 1000));
            JObject? envelope = await SendWorkerCommandAsync(
                command,
                timeoutMs,
                $"Timeout polling Worker lifecycle status (job={job.Id})",
                env => env,
                (_, __) => new JObject { ["error"] = "Worker lifecycle status timeout" },
                toolName: "genexus_lifecycle",
                toolArgs: args,
                trackOperation: false,
                progressToken: progressToken,
                heartbeat: progressToken != null && progressToken.Type != JTokenType.Null
                    ? TryWriteStdout
                    : null,
                cancellationToken: transportCancellation).ConfigureAwait(false);
            JObject? payload = envelope?["result"] as JObject ?? envelope;
            if (payload == null || payload["error"] != null) return null;
            return (JObject)payload.DeepClone();
        }

        private static async Task<JObject?> ReadWorkerLifecycleResultAsync(
            string? taskId,
            JObject? args,
            CancellationToken transportCancellation)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return null;
            try
            {
                var command = new JObject
                {
                    ["module"] = "Build",
                    ["action"] = "Result",
                    ["target"] = taskId
                };
                JObject? envelope = await SendWorkerCommandAsync(
                    command,
                    60000,
                    $"Timeout fetching Worker lifecycle result (task={taskId})",
                    env => env,
                    (_, __) => new JObject { ["error"] = "Worker lifecycle result timeout" },
                    toolName: "genexus_lifecycle",
                    toolArgs: args,
                    trackOperation: false,
                    cancellationToken: transportCancellation).ConfigureAwait(false);
                if (envelope == null || envelope["error"] != null) return null;

                JToken? token = envelope["result"] ?? envelope;
                if (token is JObject payload)
                    return IsMissingWorkerLifecycleResult(payload) ? null : (JObject)payload.DeepClone();
                if (token is JValue value && value.Type == JTokenType.String)
                {
                    try
                    {
                        JObject parsed = JObject.Parse(value.Value<string>() ?? string.Empty);
                        return IsMissingWorkerLifecycleResult(parsed) ? null : parsed;
                    }
                    catch { return null; }
                }
                return null;
            }
            catch
            {
                // Result hydration is best-effort. The terminal status delta remains
                // useful even if the Worker has already pruned its task map.
                return null;
            }
        }

        private static bool IsMissingWorkerLifecycleResult(JObject payload)
        {
            string status = payload["status"]?.ToString() ?? payload["Status"]?.ToString() ?? string.Empty;
            string message = payload["message"]?.ToString() ?? payload["Message"]?.ToString() ?? string.Empty;
            return status.Equals("Error", StringComparison.OrdinalIgnoreCase)
                && message.IndexOf("Task ID not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static JObject BuildWorkerLifecycleStatusEnvelope(
            JobEntry job,
            JObject workerPayload,
            JObject? args,
            int waitSeconds,
            string until)
        {
            JObject result = (JObject)workerPayload.DeepClone();
            if (LifecycleResponseShaper.ShouldCompact(args))
                result = LifecycleResponseShaper.CompactObject(result);

            string status = (result["status"] ?? result["Status"])?.ToString()
                ?? job.Status ?? string.Empty;
            bool terminal = !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase);
            var envelope = new JObject
            {
                ["job_id"] = job.Id,
                ["operationId"] = job.Id,
                ["status"] = status.ToLowerInvariant(),
                ["summary"] = job.Summary,
                ["estimated_seconds"] = job.EstimatedSeconds,
                ["result"] = result,
                ["workerTaskId"] = job.WorkerTaskId,
                ["waitUntil"] = string.Equals(until, "terminal", StringComparison.OrdinalIgnoreCase)
                    ? "terminal"
                    : "change",
                ["waitSatisfied"] = terminal || waitSeconds == 0
            };

            // Keep warning cursors/deltas visible at the lifecycle envelope level as
            // well as under result, so op:<id> and Worker-taskId polling have the same
            // chaining surface.
            foreach (string propertyName in new[]
            {
                "newWarnings", "warningCount", "warningTotal", "warningCursor",
                "warningsAggregated", "_meta", "phase", "Phase", "errorCount",
                "ErrorCount", "warningCount", "WarningCount", "exitCode", "ExitCode"
            })
            {
                if (result[propertyName] != null)
                    envelope[propertyName] = result[propertyName]!.DeepClone();
            }

            var queueMetadata = JobRegistry.GetLifecycleQueueMetadata(job.Id);
            foreach (var property in queueMetadata.Properties())
            {
                if (property.Name == "status" || property.Name == "operationId") continue;
                envelope[property.Name] = property.Value?.DeepClone();
            }
            return envelope;
        }

        /// <summary>
        /// Gateway-side <c>genexus_lifecycle</c> intercepts: durable-mutation journal
        /// inspect/reconcile, op:&lt;id&gt; status/result/cancel, gateway metrics and the
        /// job long-poll. Returns null for lifecycle actions that must be forwarded
        /// to the Worker (index, verify, legacy taskId paths).
        /// </summary>
        private static async Task<JObject?> HandleLifecycleGatewayInterceptsAsync(
            JToken? idToken, string toolName, JObject? args, JObject request, string sessionId,
            CancellationToken transportCancellation)
        {
            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                string? lifecycleAction = args?["action"]?.ToString();
                string? lifecycleTarget = args?["target"]?.ToString();
                // Validate the raw correlation id before ResolveJobId strips the
                // optional op: prefix. Otherwise job_id=op:<malformed> would look
                // like an arbitrary legacy Worker id and bypass InvalidOperationId.
                string? lifecycleId = args?["job_id"]?.ToString();
                if (string.IsNullOrWhiteSpace(lifecycleId)) lifecycleId = args?["jobId"]?.ToString();
                if (string.IsNullOrWhiteSpace(lifecycleId)) lifecycleId = lifecycleTarget;

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

                bool isLifecycleCorrelationAction =
                    string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase);
                // `gateway:metrics` is a reserved lifecycle target, not an operation
                // id. Keep it out of the strict alias validator so the existing
                // metrics projection remains reachable after id normalization.
                bool isGatewayMetricsStatus =
                    string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(lifecycleTarget, "gateway:metrics", StringComparison.OrdinalIgnoreCase);
                var lifecycleAlias = isLifecycleCorrelationAction && !isGatewayMetricsStatus
                    ? ResolveLifecycleOperationAlias(lifecycleId)
                    : new LifecycleOperationAliasResolution { Input = lifecycleId ?? string.Empty };
                if (isLifecycleCorrelationAction && !isGatewayMetricsStatus && lifecycleAlias.IsMalformed)
                {
                    var invalidAlias = new JObject
                    {
                        ["status"] = "InvalidOperationId",
                        ["code"] = "InvalidOperationId",
                        ["operationId"] = lifecycleAlias.Input,
                        ["message"] = "Lifecycle operation id must be op:<32-hex Gateway id>, a bare Gateway id, or an 8-hex Worker taskId."
                    };
                    return BuildToolTextResponse(idToken, invalidAlias, isError: true,
                        toolName: "genexus_lifecycle", toolArgs: args, payloadOwned: true);
                }

                if (lifecycleAlias.Kind == LifecycleAliasKind.None
                    && !isGatewayMetricsStatus
                    && !string.IsNullOrWhiteSpace(lifecycleId)
                    && (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase)))
                {
                    var unknownAlias = new JObject
                    {
                        ["status"] = "NotFound",
                        ["code"] = "OperationNotFound",
                        ["operationId"] = lifecycleAlias.NormalizedId,
                        ["message"] = "Gateway operation was not found or has expired; the id format itself is valid."
                    };
                    return BuildToolTextResponse(idToken, unknownAlias, isError: true,
                        toolName: "genexus_lifecycle", toolArgs: args, payloadOwned: true);
                }

                if ((string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)) &&
                    lifecycleAlias.Kind == LifecycleAliasKind.Tracker)
                {
                    string operationId = lifecycleAlias.TrackerOperationId!;
                    // JobRegistry covers build/edit jobs; OperationTracker covers
                    // gateway-internal request lifecycles.  The alias resolver above
                    // keeps both namespaces on the same status/result semantics.
                    {
                        bool waitUntilDone = args?["wait_until_done"]?.ToObject<bool?>() == true;
                        int? requestedWait = args?["wait"]?.ToObject<int?>()
                            ?? args?["wait_seconds"]?.ToObject<int?>();
                        int waitSeconds = Math.Min(Math.Max(
                            requestedWait ?? (waitUntilDone ? McpRouter.MaxLongPollSeconds : 0),
                            0), McpRouter.MaxLongPollSeconds);
                        string until = args?["until"]?.ToString();
                        if (string.IsNullOrWhiteSpace(until))
                            until = waitUntilDone ? "terminal" : "change";
                        JObject opPayload = string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                            ? _operationTracker.BuildOperationResult(operationId)
                            : await _operationTracker.WaitForOperationAsync(
                                operationId, waitSeconds, until, transportCancellation);
                        // A result request is a terminal-state read, but callers may still
                        // request a bounded wait.  Use the same tracker wait for result so
                        // every accepted alias has identical semantics.
                        if (string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase) && waitSeconds > 0)
                        {
                            opPayload = await _operationTracker.WaitForOperationAsync(
                                operationId, waitSeconds,
                                string.IsNullOrWhiteSpace(until) ? "terminal" : until,
                                transportCancellation);
                        }
                        int reconciled = string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                            ? TryReconcileMutationRecoveryFromOperation(operationId, opPayload)
                            : 0;
                        if (reconciled > 0)
                            opPayload["recoveryReconciled"] = reconciled;
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
                    string? requestedCancelId = McpRouter.ResolveJobId(args);
                    var cancelAlias = ResolveLifecycleOperationAlias(requestedCancelId);
                    string? cancelJobId = cancelAlias.Kind == LifecycleAliasKind.Job
                        ? cancelAlias.Job!.Id
                        : null;
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
                        if (ok
                            && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true
                            && RequiresAsyncMutationRecovery(cancellingJob))
                        {
                            _mutationRecovery.RequireRead(
                                cancellingJob.WorkerAlias,
                                cancellingJob.Target,
                                cancellingJob.Part,
                                cancelJobId,
                                cancellingJob.TargetGuid,
                                cancellingJob.TargetEntityKey,
                                cancellingJob.ObjectType,
                                cancellingJob.TargetPath,
                                cancellingJob.ExpectedVersion);
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
                            ["reReadRequired"] = ok
                                && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true
                                && RequiresAsyncMutationRecovery(cancellingJob),
                            ["message"] = cancelMsg
                        };
                        return BuildToolTextResponse(idToken, jp, isError: !ok, toolName: "genexus_lifecycle", toolArgs: args, payloadOwned: true);
                    }
                }

                if (string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase) &&
                    lifecycleAlias.Kind == LifecycleAliasKind.Tracker)
                {
                    string operationId = lifecycleAlias.TrackerOperationId!;
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
                        && (IsAsyncMutationTool(cancelledToolName)
                            || IsAsyncGxServerAction(cancelledToolName, cancelledToolArgs))
                        && !string.IsNullOrWhiteSpace(workerAlias))
                    {
                        foreach (var recoveryTarget in EnumerateMutationRecoveryTargets(cancelledToolName!, cancelledToolArgs))
                            _mutationRecovery.RequireRead(
                                workerAlias, recoveryTarget.Target, recoveryTarget.Part, operationId,
                                cancelledToolArgs?["guid"]?.ToString(), cancelledToolArgs?["entityKey"]?.ToString(),
                                cancelledToolArgs?["type"]?.ToString(), cancelledToolArgs?["path"]?.ToString(),
                                cancelledToolArgs?["baseVersion"]?.ToString() ?? cancelledToolArgs?["expectedVersion"]?.ToString());
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
                    var resultAlias = ResolveLifecycleOperationAlias(McpRouter.ResolveJobId(args));
                    if (resultAlias.Kind == LifecycleAliasKind.Job && resultAlias.Job != null)
                    {
                        string resultJobId = resultAlias.Job.Id;
                        var probe = resultAlias.Job;
                        {
                            // Issue #27 item 1: if the job is still "running", actively
                            // reconcile against the worker before reporting Pending — the
                            // background poller may have wedged.
                            await ReconcileJobWithWorkerAsync(probe, "genexus_lifecycle", args);
                            if (probe.Kind?.StartsWith("lifecycle/", StringComparison.OrdinalIgnoreCase) == true
                                && !string.IsNullOrWhiteSpace(probe.WorkerTaskId)
                                && probe.Result is JObject storedStatus
                                && storedStatus["newWarnings"] != null
                                && !string.Equals(probe.Status, "running", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(probe.Status, "queued", StringComparison.OrdinalIgnoreCase))
                             {
                                 JObject? fullResult = await ReadWorkerLifecycleResultAsync(
                                     probe.WorkerTaskId, args, transportCancellation);
                                 if (fullResult != null)
                                 {
                                     lock (probe.SyncRoot) probe.Result = fullResult;
                                 }
                             }
                            // Envelope shape extracted into McpRouter.BuildJobResultEnvelope
                            // for unit-test coverage and parity with status long-poll.
                            var (resultPayload, isErr) = McpRouter.BuildJobResultEnvelope(probe);
                            int reconciled = TryReconcileMutationRecoveryFromOperation(resultJobId!, resultPayload);
                            if (reconciled > 0)
                                resultPayload["recoveryReconciled"] = reconciled;
                            return BuildToolTextResponse(idToken, resultPayload, isError: isErr, toolName: "genexus_lifecycle", toolArgs: args);
                        }
                        // Unknown id → fall through to the legacy worker-side taskId result path.
                    }
                }

                // Long-poll intercept (Task 4.5): action=status + job_id (BackgroundJobRegistry)
                // wait_seconds is clamped [0, MaxLongPollSeconds]; 0 = immediate poll (default behaviour).
                if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase))
                {
                    string? requestedJobId = McpRouter.ResolveJobId(args);
                    var statusAlias = ResolveLifecycleOperationAlias(requestedJobId);
                    if (statusAlias.Kind == LifecycleAliasKind.Job && statusAlias.Job != null)
                    {
                        string jobId = statusAlias.Job.Id;
                        var probe = statusAlias.Job;
                        {
                            // Issue #27 item 1: reconcile a still-running job against the
                            // worker's real build-task state before long-polling, so a wedged
                            // background poller can't keep a finished build stuck at "running".
                            await ReconcileJobWithWorkerAsync(probe, "genexus_lifecycle", args);
                            if (probe.Kind?.StartsWith("lifecycle/", StringComparison.OrdinalIgnoreCase) == true
                                && !string.IsNullOrWhiteSpace(probe.WorkerTaskId)
                                && probe.Result is JObject storedStatus
                                && storedStatus["newWarnings"] != null
                                && !string.Equals(probe.Status, "running", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(probe.Status, "queued", StringComparison.OrdinalIgnoreCase))
                             {
                                 JObject? fullResult = await ReadWorkerLifecycleResultAsync(
                                     probe.WorkerTaskId, args, transportCancellation);
                                 if (fullResult != null)
                                 {
                                     lock (probe.SyncRoot) probe.Result = fullResult;
                                 }
                             }
                            bool waitUntilDone = args?["wait_until_done"]?.ToObject<bool?>() == true;
                            int? requestedWait = args?["wait"]?.ToObject<int?>()
                                ?? args?["wait_seconds"]?.ToObject<int?>();
                            int waitSeconds = Math.Min(Math.Max(
                                requestedWait ?? (waitUntilDone ? McpRouter.MaxLongPollSeconds : 0),
                                0), McpRouter.MaxLongPollSeconds);
                            string until = args?["until"]?.ToString();
                            if (string.IsNullOrWhiteSpace(until))
                                until = waitUntilDone ? "terminal" : "change";
                            var clientProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];

                            // A canonical Gateway id is only a correlation handle while
                            // the Worker task is live. Read the live task through the
                            // same alias so phase/warning changes and `until` semantics
                            // are not lost behind the Gateway registry's terminal-only
                            // snapshot. `wait_until_done`/until=terminal stays on the
                            // registry path because it must return the stored final
                            // result, including the full warning list.
                            if (!string.Equals(until, "terminal", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(probe.Status, "running", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(probe.WorkerTaskId))
                            {
                                JObject? liveWorkerStatus = await ReadWorkerLifecycleStatusAsync(
                                    probe,
                                    args,
                                    waitSeconds,
                                    until,
                                    clientProgressToken,
                                    transportCancellation);
                                if (liveWorkerStatus != null)
                                {
                                    var liveVerdict = McpRouter.ClassifyWorkerBuildStatus(liveWorkerStatus);
                                    if (liveVerdict.HasValue)
                                    {
                                        JObject storedResult = liveVerdict.Value.result;
                                        JObject? fullResult = await ReadWorkerLifecycleResultAsync(
                                            probe.WorkerTaskId, args, transportCancellation);
                                        if (fullResult != null) storedResult = fullResult;
                                        JobRegistry.Complete(
                                            probe.Id,
                                            liveVerdict.Value.success,
                                            liveVerdict.Value.summary,
                                            storedResult);
                                    }

                                    JObject liveEnvelope = BuildWorkerLifecycleStatusEnvelope(
                                        probe, liveWorkerStatus, args, waitSeconds, until);
                                    bool liveError = false;
                                    if (liveEnvelope["result"] is JObject liveResult
                                        && (liveResult["Status"] ?? liveResult["status"])?.ToString() is string liveStatus
                                        && !string.Equals(liveStatus, "Running", StringComparison.OrdinalIgnoreCase)
                                        && !string.Equals(liveStatus, "Queued", StringComparison.OrdinalIgnoreCase))
                                    {
                                        liveError = LifecycleResponseShaper.ClassifyBuildOutcome(liveResult)
                                            == LifecycleResponseShaper.BuildOutcome.Error;
                                    }
                                    return BuildToolTextResponse(
                                        idToken,
                                        liveEnvelope,
                                        isError: liveError,
                                        toolName: "genexus_lifecycle",
                                        toolArgs: args,
                                        payloadOwned: true);
                                }
                            }

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
                                    heartbeat: hasProgressToken ? TryWriteStdout : null,
                                    cancellationToken: longPollCancellationToken,
                                    until: until);
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
                            // The registry stores the terminal Worker payload for result,
                            // but status is a delta surface. Shape the stored result here
                            // so compact=false status calls do not resend the full
                            // PascalCase Warnings list on every terminal poll.
                            if (pollResult["result"] is JObject storedStatusResult)
                            {
                                JObject statusDelta = LifecycleResponseShaper.BuildStatusWarningDelta(
                                    storedStatusResult,
                                    args?["since"]?.ToString());
                                pollResult["result"] = statusDelta;
                                foreach (string warningProperty in new[]
                                {
                                    "newWarnings", "warningCount", "warningTotal", "warningCursor", "_meta"
                                })
                                {
                                    if (statusDelta[warningProperty] != null)
                                        pollResult[warningProperty] = statusDelta[warningProperty]!.DeepClone();
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
            return null;
        }
    }
}
