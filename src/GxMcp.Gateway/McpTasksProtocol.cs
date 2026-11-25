using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Adapter for the current MCP tasks extension over the existing durable-ish
    /// BackgroundJobRegistry. Task handles are scoped to the session that created
    /// them; a task id alone is never enough to read or cancel another client job.
    /// </summary>
    internal static class McpTasksProtocol
    {
        internal const string ExtensionId = "io.modelcontextprotocol/tasks";
        private const int TaskTtlMs = 600000;
        private const int PollIntervalMs = 1000;

        /// <summary>
        /// Returns true only for the 2026 request shape that explicitly opts into
        /// the current tasks extension. Legacy 2025 task callers must keep using
        /// their existing lifecycle/result contract.
        /// </summary>
        internal static bool SupportsTasks(JObject request, bool taskScopeEnabled = true)
            => taskScopeEnabled
                && McpRouter.IsModernRequest(request)
                && ClientDeclaredTasksExtension(request);

        internal static JObject? Handle(
            JObject request,
            string sessionId,
            BackgroundJobRegistry registry,
            bool taskScopeEnabled = true,
            OwnershipFence? ownership = null)
        {
            string? method = request["method"]?.ToString();
            if (method != "tasks/get" && method != "tasks/update" && method != "tasks/cancel")
                return null;

            var id = request["id"]?.DeepClone();
            bool modern = McpRouter.IsModernRequest(request);
            if (modern && !ClientDeclaredTasksExtension(request))
            {
                return Error(id, -32021, "The client did not declare the io.modelcontextprotocol/tasks extension.",
                    new JObject
                    {
                        ["requiredCapabilities"] = new JObject
                        {
                            ["extensions"] = new JObject { [ExtensionId] = new JObject() }
                        }
                    });
            }

            if (modern && !taskScopeEnabled)
            {
                return Error(id, -32023,
                    "Modern MCP tasks require the Mcp-Client-Id header so task ownership can be isolated.",
                    new JObject { ["requiredHeader"] = "Mcp-Client-Id" });
            }

            var parameters = request["params"] as JObject;
            var rawTaskId = parameters?["taskId"];
            string? taskId = modern
                ? (rawTaskId?.Type == JTokenType.String ? (string?)rawTaskId : null)
                : rawTaskId?.ToString() ?? parameters?["id"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(taskId))
                return Error(id, -32602, "taskId is required.", new JObject { ["taskId"] = taskId ?? "" });

            var job = registry.Get(taskId);
            if (job == null)
                return Error(id, modern ? -32602 : -32001, modern ? "Invalid taskId." : "Task not found",
                    new JObject { ["taskId"] = taskId });
            ownership ??= new OwnershipFence(sessionId, string.Empty, 0);
            bool sameSession = string.Equals(job.Session, sessionId, StringComparison.Ordinal);
            if (!sameSession || !registry.BelongsTo(job, ownership))
            {
                // The caller's own task whose KB context changed since it started (another
                // KB selected, context renewed): say so, so the client does not read it as a
                // lost task. Another session's task stays an opaque handle.
                if (sameSession)
                {
                    return Error(id, modern ? -32602 : -32003,
                        modern ? "Invalid taskId." : "Task belongs to another MCP session",
                        new JObject
                        {
                            ["taskId"] = taskId,
                            ["reason"] = "kb_context_changed",
                            ["taskKbId"] = job.KbId,
                            ["taskGeneration"] = job.Generation,
                            ["currentKbId"] = ownership.KbId,
                            ["currentGeneration"] = ownership.Generation,
                            ["taskStatus"] = job.Status
                        });
                }
                // Do not turn a task handle into a cross-session existence oracle for
                // modern clients. Legacy callers retain the historical error code.
                return Error(id, modern ? -32602 : -32003,
                    modern ? "Invalid taskId." : "Task belongs to another MCP session",
                    new JObject { ["taskId"] = taskId });
            }

            if (method == "tasks/cancel")
            {
                if (!modern && !string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    return Error(id, -32602, "Cannot cancel a terminal task.",
                        new JObject { ["taskId"] = taskId, ["status"] = job.Status });
                }
                bool cancelled = registry.Cancel(taskId, parameters?["reason"]?.ToString() ?? "Cancelled by client");
                return Success(id, modern
                    ? CompleteAck()
                    : BuildLegacyTask(job, cancelled ? "cancelled" : job.Status));
            }

            if (method == "tasks/update" && modern)
            {
                if (parameters?["inputResponses"] is not JObject)
                    return Error(id, -32602, "inputResponses must be an object.",
                        new JObject { ["taskId"] = taskId });

                // Worker progress remains authoritative. The current Gateway has no
                // elicitation/input requests, so inputResponses are acknowledged and
                // ignored as required by the extension when no key is outstanding.
                return Success(id, CompleteAck());
            }

            return Success(id, modern ? BuildModernTask(job) : BuildLegacyTask(job));
        }

        internal static JObject BuildCreateTaskResult(JobEntry job, string statusMessage)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (job.SyncRoot)
            {
                var task = BuildTaskMetadata(job, "working");
                task["resultType"] = "task";
                if (!string.IsNullOrWhiteSpace(statusMessage)) task["statusMessage"] = statusMessage;
                return task;
            }
        }

        private static JObject BuildLegacyTask(JobEntry job, string? statusOverride = null)
        {
            var task = BuildTaskMetadata(job, statusOverride ?? job.Status);
            task["estimatedSeconds"] = job.EstimatedSeconds;
            if (job.CompletedAt.HasValue) task["completedAt"] = job.CompletedAt.Value.ToUniversalTime().ToString("O");
            if (!string.IsNullOrWhiteSpace(job.Summary)) task["statusMessage"] = job.Summary;
            if (job.Result != null) task["result"] = job.Result.DeepClone();
            task["ownerScopeId"] = job.OwnerScopeId;
            task["kbId"] = job.KbId;
            task["generation"] = job.Generation;
            task["epoch"] = job.Epoch;
            return task;
        }

        private static JObject BuildTaskMetadata(JobEntry job, string status) => new JObject
        {
            ["taskId"] = job.Id,
            ["status"] = status,
            ["createdAt"] = job.StartedAt.ToUniversalTime().ToString("O"),
            ["lastUpdatedAt"] = (job.LastUpdatedAt ?? job.StartedAt).ToUniversalTime().ToString("O"),
            ["pollIntervalMs"] = PollIntervalMs,
            ["ttlMs"] = TaskTtlMs
        };

        private static JObject BuildModernTask(JobEntry job)
        {
            string localStatus;
            string wireStatus;
            JObject? payload;
            JObject task;
            lock (job.SyncRoot)
            {
                localStatus = job.Status;
                payload = job.Result;
                wireStatus = McpTaskResult.GetStatus(localStatus, payload);
                task = BuildTaskMetadata(job, wireStatus);
                task["resultType"] = "complete";
                if (!string.IsNullOrWhiteSpace(job.Summary)) task["statusMessage"] = job.Summary;
                else if (localStatus == "queued") task["statusMessage"] = "Waiting for the worker.";
            }

            McpTaskResult.AttachOutcome(task, wireStatus, localStatus, job.Kind, payload);
            return task;
        }

        private static bool ClientDeclaredTasksExtension(JObject request)
        {
            var metadata = (request["params"] as JObject)?["_meta"] as JObject;
            var capabilities = metadata?["io.modelcontextprotocol/clientCapabilities"] as JObject;
            return capabilities?["extensions"] is JObject extensions
                   && extensions[ExtensionId] is JObject;
        }

        private static JObject CompleteAck() => new JObject { ["resultType"] = "complete" };

        private static JObject Success(JToken? id, JObject task)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = task };

        private static JObject Error(JToken? id, int code, string message, JObject data)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JObject { ["code"] = code, ["message"] = message, ["data"] = data } };
    }
}
