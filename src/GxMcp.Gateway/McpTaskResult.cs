using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>Projects the terminal outcome of a registry job onto the Tasks GA wire result.</summary>
    internal static class McpTaskResult
    {
        internal static string GetStatus(string localStatus, JObject? payload) => localStatus.ToLowerInvariant() switch
        {
            "queued" or "running" => "working",
            "succeeded" => "completed",
            "failed" when payload != null => "completed",
            "cancelled" => "cancelled",
            _ => "failed"
        };

        internal static void AttachOutcome(JObject task, string wireStatus, string localStatus, string kind, JObject? payload)
        {
            // The registry retains the worker payload for legacy status/result readers.
            // GA polling wraps it as a CallToolResult, including when the tool reports an error.
            if (wireStatus == "completed")
            {
                var segments = kind.Split('/', 2);
                string? action = segments.Length == 2 && segments[0] != "edit" ? segments[1] : null;
                var toolArgs = action == null ? null : new JObject { ["action"] = action };
                string? toolName = segments[0] switch
                {
                    "edit" when segments.Length == 2 => segments[1],
                    "lifecycle" => "genexus_lifecycle",
                    "gxserver" => "genexus_gxserver",
                    // Transfer reads share the versioning job kind for legacy readers.
                    "versioning" when action is "export" or "inspect" => "genexus_transfer",
                    "versioning" => "genexus_versioning",
                    _ => null
                };
                task["result"] = Program.BuildToolResultContent(payload ?? new JObject(), string.Equals(localStatus, "failed", StringComparison.OrdinalIgnoreCase), toolName, toolArgs);
            }
            else if (wireStatus == "failed")
            {
                task["error"] = new JObject
                {
                    ["code"] = -32603,
                    ["message"] = "The task did not produce a tool result."
                };
            }
        }
    }
}
