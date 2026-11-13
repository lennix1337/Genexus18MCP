using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>Observes every per-session tools/list_changed (sessionId, envelope); tests only.</summary>
        internal static Action<string, string>? ToolsListChangedObserver;

        /// <summary>
        /// Tells ONE session its tools/list changed. Never broadcast: the enabled groups
        /// are per session, so other clients' lists did not change.
        /// </summary>
        private static bool SendToolsListChanged(string sessionId, string reason)
        {
            string json = JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                method = "notifications/tools/list_changed",
                @params = new { reason }
            });

            bool sent = false;
            if (string.Equals(sessionId, "stdio", StringComparison.Ordinal))
            {
                if (_stdioActive)
                {
                    EmitStdioNotification(json);
                    sent = true;
                }
            }
            else if (_httpSessions.TryGet(sessionId, out var session) && session != null)
            {
                QueueSessionMessage(session, json);
                sent = true;
            }

            ToolsListChangedObserver?.Invoke(sessionId, json);
            return sent;
        }

        /// <summary>A call to an unlisted published tool still runs; this only makes its group visible to the session.</summary>
        internal static bool AutoEnableAdaptiveGroup(string sessionId, bool sessionContextEnabled, string toolName)
        {
            if (!sessionContextEnabled
                || !AdaptiveTools.TryGetGroupOfTool(toolName, out var group)
                || !AdaptiveToolSessions.Enable(sessionId, group.Name))
            {
                return false;
            }

            SendToolsListChanged(sessionId, "adaptive_auto_enable");
            return true;
        }

        internal static JObject HandleAdaptiveMetaTool(JObject? args, string sessionId, bool sessionContextEnabled, bool adaptiveProfile)
        {
            if (!adaptiveProfile)
            {
                return AdaptiveTools.BuildError("ToolNotInProfile",
                    $"{AdaptiveTools.MetaToolName} is only available in the '{AdaptiveTools.ProfileName}' profile.",
                    $"Set Server.ToolProfile or GXMCP_PROFILE to '{AdaptiveTools.ProfileName}'. Other profiles already list their tools.");
            }

            string action = args?["action"]?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
            string[] enabled = sessionContextEnabled ? AdaptiveToolSessions.EnabledGroups(sessionId) : Array.Empty<string>();

            if (action == "list") return AdaptiveTools.BuildListPayload(enabled);

            if (action != "enable")
            {
                return AdaptiveTools.BuildError("InvalidArgument", "action must be 'list' or 'enable'.",
                    "Call action=list to see the groups, then action=enable group=<name>.");
            }

            string groupName = args?["group"]?.ToString() ?? string.Empty;
            if (!AdaptiveTools.TryGetGroup(groupName, out var group))
            {
                return AdaptiveTools.BuildError("UnknownGroup", $"Unknown tool group '{groupName}'.",
                    "Valid groups: " + string.Join(", ", AdaptiveTools.Groups.Select(g => g.Name)) + ".");
            }

            if (!sessionContextEnabled)
            {
                return AdaptiveTools.BuildError("SessionRequired",
                    "This transport is sessionless, so no per-client tool selection is kept.",
                    "Call the tool by name; it runs even when it is not listed.");
            }

            bool newlyEnabled = AdaptiveToolSessions.Enable(sessionId, group.Name);
            bool notified = newlyEnabled && SendToolsListChanged(sessionId, "adaptive_enable");
            return AdaptiveTools.BuildEnablePayload(group, newlyEnabled, notified);
        }
    }
}
