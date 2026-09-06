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
            bool taskScopeEnabled = true)
        {
            string? method = request["method"]?.ToString();
            if (method != "tasks/get" && method != "tasks/update" && method != "tasks/cancel")
                return null;

            var id = request["id"]?.DeepClone();
            bool modern = McpRouter.IsModernRequest(request);
            if (modern && !ClientDeclaredTasksExtension(request))
            {
                return Error(id, -32021, "The client did not declare the io.modelcontextprotocol/tasks extension.",
                    new JObject { ["requiredExtension"] = "io.modelcontextprotocol/tasks" });
            }

            if (modern && !taskScopeEnabled)
            {
                return Error(id, -32023,
                    "Modern MCP tasks require the Mcp-Client-Id header so task ownership can be isolated.",
                    new JObject { ["requiredHeader"] = "Mcp-Client-Id" });
            }

            var parameters = request["params"] as JObject;
            string taskId = parameters?["taskId"]?.ToString() ?? parameters?["id"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(taskId))
                return Error(id, -32602, "taskId is required.", new JObject { ["taskId"] = taskId });

            var job = registry.Get(taskId);
            if (job == null)
                return Error(id, modern ? -32602 : -32001, "Task not found", new JObject { ["taskId"] = taskId });
            if (!string.Equals(job.Session, sessionId, StringComparison.Ordinal))
            {
                // Do not turn a task handle into a cross-session existence oracle for
                // modern clients. Legacy callers retain the historical error code.
                return Error(id, modern ? -32602 : -32003,
                    modern ? "Invalid taskId." : "Task belongs to another MCP session",
                    new JObject { ["taskId"] = taskId });
            }

            if (method == "tasks/cancel")
            {
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    return Error(id, -32602, "Cannot cancel a terminal task.",
                        new JObject { ["taskId"] = taskId, ["status"] = job.Status });
                }
                bool cancelled = registry.Cancel(taskId, parameters?["reason"]?.ToString() ?? "Cancelled by client");
                return Success(id, modern
                    ? CompleteAck()
                    : BuildTask(job, cancelled ? "cancelled" : job.Status));
            }

            if (method == "tasks/update" && modern)
            {
                var inputResponses = parameters?["inputResponses"];
                if (inputResponses != null && inputResponses.Type != JTokenType.Object)
                    return Error(id, -32602, "inputResponses must be an object.",
                        new JObject { ["taskId"] = taskId });

                // Worker progress remains authoritative. The current Gateway has no
                // elicitation/input requests, so inputResponses are acknowledged and
                // ignored as required by the extension when no key is outstanding.
                return Success(id, CompleteAck());
            }

            return Success(id, BuildTask(job, modern: modern));
        }

        internal static JObject BuildCreateTaskResult(JobEntry job, string statusMessage)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            var task = BuildTask(job, statusOverride: "working", modern: true);
            task["resultType"] = "task";
            if (!string.IsNullOrWhiteSpace(statusMessage)) task["statusMessage"] = statusMessage;
            return task;
        }

        internal static JObject BuildTask(JobEntry job, string? statusOverride = null, bool modern = false)
        {
            string status = statusOverride ?? job.Status;
            if (modern) status = MapModernStatus(status);
            var task = new JObject
            {
                ["taskId"] = job.Id,
                ["status"] = status,
                ["createdAt"] = job.StartedAt.ToUniversalTime().ToString("O"),
                ["lastUpdatedAt"] = (job.LastUpdatedAt ?? job.StartedAt).ToUniversalTime().ToString("O"),
                ["pollIntervalMs"] = PollIntervalMs,
                ["ttlMs"] = TaskTtlMs
            };
            if (modern) task["resultType"] = "complete";
            task["estimatedSeconds"] = job.EstimatedSeconds;
            if (job.CompletedAt.HasValue) task["completedAt"] = job.CompletedAt.Value.ToUniversalTime().ToString("O");
            if (!string.IsNullOrWhiteSpace(job.Summary)) task["statusMessage"] = job.Summary;
            if (job.Result != null) task["result"] = job.Result.DeepClone();
            return task;
        }

        private static string MapModernStatus(string status)
        {
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)) return "working";
            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)) return "completed";
            return status.ToLowerInvariant();
        }

        private static bool ClientDeclaredTasksExtension(JObject request)
        {
            var metadata = (request["params"] as JObject)?["_meta"] as JObject;
            var capabilities = metadata?["io.modelcontextprotocol/clientCapabilities"] as JObject;
            return capabilities?["extensions"] is JObject extensions
                   && extensions.Property(ExtensionId) != null;
        }

        private static JObject CompleteAck() => new JObject { ["resultType"] = "complete" };

        private static JObject Success(JToken? id, JObject task)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = task };

        private static JObject Error(JToken? id, int code, string message, JObject data)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JObject { ["code"] = code, ["message"] = message, ["data"] = data } };
    }
}
