using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Pipelines
{
    // Remaining contract types: IdempotencyMiddleware implements IMcpMiddleware. The
    // composable pipeline built on top of them was never wired into request processing
    // (Program.RequestLoop.cs runs ProcessMcpRequestCore directly) and was removed.
    public delegate Task<JObject?> McpPipelineNextDelegate();

    public sealed class McpPipelineContext
    {
        public JObject Request { get; set; }
        public string SessionId { get; set; }
        public string ToolName { get; set; }
        public JObject Arguments { get; set; }
        public string KbAlias { get; set; }
        public bool IsDryRun { get; set; }
        public JObject? Response { get; set; }
        /// <summary>Allows a stage to return a response without invoking later stages.</summary>
        public bool IsShortCircuited => Response != null;
        public Dictionary<string, object?> Properties { get; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        public JToken? Id => Request?["id"];

        public McpPipelineContext(JObject? request, string sessionId = "")
        {
            Request = request ?? new JObject();
            SessionId = sessionId ?? string.Empty;

            var paramsObj = Request["params"] as JObject;
            ToolName = paramsObj?["name"]?.ToString() ?? Request["method"]?.ToString() ?? string.Empty;
            Arguments = (paramsObj?["arguments"] as JObject) ?? new JObject();
            KbAlias = Arguments["kb"]?.ToString() ?? string.Empty;
            IsDryRun = (bool?)Arguments["dryRun"] ?? false;
        }
    }

    public interface IMcpMiddleware
    {
        Task<JObject?> InvokeAsync(McpPipelineContext context, McpPipelineNextDelegate next);
    }
}
