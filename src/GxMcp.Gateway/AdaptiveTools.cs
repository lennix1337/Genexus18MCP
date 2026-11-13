using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The <c>adaptive</c> tool profile: <c>tools/list</c> serves the core tools plus
    /// the tiny <c>genexus_tools</c> meta tool, and a session enables further groups
    /// on demand. Groups partition every published tool that is not in the core set.
    /// </summary>
    internal static class AdaptiveTools
    {
        internal const string ProfileName = "adaptive";
        internal const string MetaToolName = "genexus_tools";

        internal sealed class Group
        {
            public Group(string name, string purpose, params string[] tools)
            {
                Name = name;
                Purpose = purpose;
                Tools = tools;
            }

            public string Name { get; }
            public string Purpose { get; }
            public string[] Tools { get; }
        }

        internal static readonly Group[] Groups =
        {
            new Group("authoring", "Create objects and edit their structure, variables, properties, refactors, APIs and import/export.",
                "genexus_create", "genexus_structure", "genexus_variable", "genexus_properties", "genexus_authoring",
                "genexus_delete_object", "genexus_format", "genexus_refactor", "genexus_api", "genexus_io",
                "genexus_recipe", "genexus_generator_reference"),
            new Group("ui", "Forms, layouts, WorkWithPlus patterns, K2B designer and browser.",
                "genexus_layout", "genexus_edit_form", "genexus_k2b_designer", "genexus_browser", "genexus_wwp",
                "genexus_workwith", "genexus_apply_pattern"),
            new Group("data", "Database, data views and navigation.",
                "genexus_db", "genexus_data_view", "genexus_navigation"),
            new Group("build", "Edit-and-build, tests, running objects, deploy and sandbox.",
                "genexus_edit_and_build", "genexus_test", "genexus_run_object", "genexus_deploy", "genexus_sandbox"),
            new Group("versioning", "GeneXus Server, KB versions, compare, merge, diff, import and transfer.",
                "genexus_gxserver", "genexus_kb_version", "genexus_versioning", "genexus_compare", "genexus_merge",
                "genexus_kb_diff", "genexus_kb_import", "genexus_transfer", "genexus_module"),
            new Group("diagnostics", "Workers, SDK probe, doctor, telemetry, security, GAM and per-KB memory.",
                "genexus_worker_reload", "genexus_worker_pool", "genexus_connection_recover", "genexus_doctor",
                "genexus_sdk_probe", "genexus_telemetry", "genexus_security", "genexus_gam", "genexus_memory")
        };

        private static readonly Dictionary<string, Group> ByName =
            Groups.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Group> ByTool = Groups
            .SelectMany(g => g.Tools.Select(t => (Tool: t, Group: g)))
            .ToDictionary(x => x.Tool, x => x.Group, StringComparer.OrdinalIgnoreCase);

        internal static bool IsAdaptive(string? profile) =>
            string.Equals(profile?.Trim(), ProfileName, StringComparison.OrdinalIgnoreCase);

        internal static bool TryGetGroup(string name, out Group group) =>
            ByName.TryGetValue((name ?? string.Empty).Trim(), out group!);

        internal static bool TryGetGroupOfTool(string toolName, out Group group) =>
            ByTool.TryGetValue(toolName ?? string.Empty, out group!);

        internal static bool IsMetaTool(string? toolName) =>
            string.Equals(toolName, MetaToolName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Kept short on purpose: it is paid by every adaptive client on every session.</summary>
        internal static JObject BuildMetaToolDefinition() => new JObject
        {
            ["name"] = MetaToolName,
            ["description"] = "List or enable tool groups for this session. Any tool can also be called directly by name.",
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("list", "enable") },
                    ["group"] = new JObject { ["type"] = "string" }
                },
                ["required"] = new JArray("action")
            },
            ["annotations"] = new JObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = true }
        };

        internal static JObject BuildListPayload(IReadOnlyCollection<string> enabled) => new JObject
        {
            ["status"] = "ok",
            ["profile"] = ProfileName,
            ["groups"] = new JArray(Groups.Select(g => new JObject
            {
                ["name"] = g.Name,
                ["purpose"] = g.Purpose,
                ["enabled"] = enabled.Contains(g.Name, StringComparer.OrdinalIgnoreCase),
                ["tools"] = new JArray(g.Tools)
            })),
            ["hint"] = "genexus_tools action=enable group=<name> adds a group to tools/list for this session. Listed or not, every tool can be called by name."
        };

        internal static JObject BuildEnablePayload(Group group, bool newlyEnabled, bool notified) => new JObject
        {
            ["status"] = "ok",
            ["group"] = group.Name,
            ["alreadyEnabled"] = !newlyEnabled,
            ["tools"] = new JArray(group.Tools),
            ["listChangedSent"] = notified,
            ["next"] = "Enabled for this session only. A notifications/tools/list_changed was sent. "
                + "If your client ignores it the new tools are NOT in your tool list yet: call tools/list again (or restart tool discovery). "
                + "You can call these tools by name right now; they run even when not listed."
        };

        internal static JObject BuildError(string code, string message, string hint) => new JObject
        {
            ["status"] = "error",
            ["error"] = new JObject { ["code"] = code, ["message"] = message, ["hint"] = hint }
        };

        /// <summary>Tools of the enabled groups, for allowlist construction.</summary>
        internal static IEnumerable<string> ToolsOf(IEnumerable<string> enabledGroups) =>
            enabledGroups.Where(n => ByName.ContainsKey(n)).SelectMany(n => ByName[n].Tools);
    }

    /// <summary>
    /// Enabled groups per MCP session id. State exists only for session-bound
    /// transports (stdio = one client per process, legacy HTTP session ids); the
    /// sessionless 2026 binding keeps none, so independent clients never share a
    /// selection. Entries are dropped on initialize and when an HTTP session ends.
    /// </summary>
    internal static class AdaptiveToolSessions
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Enabled =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);

        /// <returns>true when the group was not enabled before.</returns>
        internal static bool Enable(string sessionId, string group) =>
            Enabled.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))
                .TryAdd(group, 0);

        internal static string[] EnabledGroups(string sessionId) =>
            Enabled.TryGetValue(sessionId, out var groups)
                ? groups.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

        internal static void Forget(string sessionId) => Enabled.TryRemove(sessionId, out _);
    }
}
