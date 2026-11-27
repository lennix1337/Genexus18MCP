using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Opt-in server policies for KB targeting (Issue #1112):
    /// <list type="bullet">
    /// <item><see cref="ServerConfig.RequireExplicitKbForWrites"/>: refuses KB-mutating tool calls
    /// that omit an explicit <c>kb</c> selector, listing configured <c>Environment.KBs</c> aliases.</item>
    /// <item><see cref="ServerConfig.OpenOnlyConfiguredKbs"/>: restricts <c>genexus_kb action=open</c>
    /// (and ad-hoc path selection/resolution) to aliases declared in <c>Environment.KBs</c>.</item>
    /// </list>
    /// Reuses <see cref="OperationClassifier"/> and <see cref="Program.DescribeDestructiveOperation"/>
    /// instead of duplicating tool/action write lists.
    /// </summary>
    internal static class KbAccessPolicy
    {
        internal const string ExplicitKbRequiredForWriteCode = "EXPLICIT_KB_REQUIRED_FOR_WRITE";
        internal const string OpenOnlyConfiguredKbsCode = "KB_OPEN_ONLY_CONFIGURED";

        // Gateway/process tools that do not mutate a session-selected KB (pool/process
        // lifecycle, local recipe macros, or isolated sandbox clones with explicit `from`).
        private static readonly HashSet<string> NonKbWriteTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_worker_reload",
            "genexus_worker_pool",
            "genexus_connection_recover",
            "genexus_sandbox",
            "genexus_recipe"
        };

        internal static IReadOnlyList<string> GetConfiguredAliases(Configuration? config)
        {
            var entries = config?.Environment?.KBs;
            if (entries == null || entries.Count == 0)
                return Array.Empty<string>();

            var aliases = new List<string>(entries.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Alias)) continue;
                string alias = entry.Alias.Trim();
                if (seen.Add(alias))
                    aliases.Add(alias);
            }
            return aliases;
        }

        internal static KbEntry? FindConfiguredKb(Configuration? config, string? alias)
        {
            if (config?.Environment?.KBs == null || string.IsNullOrWhiteSpace(alias))
                return null;

            string trimmed = alias.Trim();
            return config.Environment.KBs.FirstOrDefault(
                entry => entry != null && string.Equals(entry.Alias, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool HasExplicitKbArgument(string? toolName, JObject? args)
        {
            if (args == null) return false;
            if (!string.IsNullOrWhiteSpace(args["kb"]?.ToString())
                || !string.IsNullOrWhiteSpace(args["kbAlias"]?.ToString()))
            {
                return true;
            }

            if (string.Equals(toolName, "genexus_kb_import", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args["to"]?.ToString()))
            {
                return true;
            }

            return false;
        }

        internal static bool RequiresExplicitKbForWrite(string? toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            var effectiveArgs = OperationClassifier.NormalizeArguments(toolName, args, out var canonical);
            if (NonKbWriteTools.Contains(canonical))
                return false;

            if (string.Equals(canonical, OperationClassifier.KbTool, StringComparison.OrdinalIgnoreCase))
            {
                // Pool/session/creation actions operate on the WorkerPool or create a new KB
                // rather than mutating the session's active KB. Worker-bound KB writes
                // (set_environment, set_startup) DO mutate a target KB and require explicit `kb`.
                string? action = effectiveArgs["action"]?.ToString();
                return OperationClassifier.IsKbEnvironmentMutation(canonical, action)
                    || string.Equals(action, "set_startup", StringComparison.OrdinalIgnoreCase);
            }

            bool isMutation = OperationClassifier.IsMutationCandidate(toolName, args)
                && !OperationClassifier.IsEffectFreeDryRunPreview(toolName, args);
            if (isMutation)
                return true;

            return Program.DescribeDestructiveOperation(toolName, args) != null;
        }

        internal static bool ShouldRefuseWriteWithoutExplicitKb(
            Configuration? config,
            string? toolName,
            JObject? args,
            bool? hasExplicitKb = null)
            => config?.Server?.RequireExplicitKbForWrites == true
                && !(hasExplicitKb ?? HasExplicitKbArgument(toolName, args))
                && RequiresExplicitKbForWrite(toolName, args);

        internal static KbSelectionResult? TryBuildOpenOnlyConfiguredKbsUnresolved(
            Configuration? config,
            string kbArg,
            string selectionSource)
        {
            if (config?.Server?.OpenOnlyConfiguredKbs != true)
                return null;

            bool looksLikePath = LooksLikeFilesystemPath(kbArg);
            return KbSelectionResult.Unresolved(
                selectionSource,
                "invalid",
                OpenOnlyConfiguredKbsCode,
                FormatOpenOnlyConfiguredKbsMessage(
                    config,
                    requestedPath: looksLikePath ? kbArg : null,
                    requestedAlias: looksLikePath ? null : kbArg));
        }

        internal static JObject BuildExplicitKbRequiredForWriteError(
            string toolName,
            JObject? args,
            Configuration? config)
        {
            var aliases = GetConfiguredAliases(config);
            string? action = args?["action"]?.ToString();
            string operation = string.IsNullOrWhiteSpace(action)
                ? toolName
                : $"{toolName} (action={action})";

            string aliasText = aliases.Count > 0
                ? $"Configured KB aliases: {string.Join(", ", aliases)}."
                : "No KB aliases are currently configured in Environment.KBs.";
            string hint = aliases.Count > 0
                ? $"Retry with kb=<alias> using one of the configured aliases: {string.Join(", ", aliases)}."
                : "Declare KB aliases in config.Environment.KBs[] and pass kb=<alias>, or disable Server.RequireExplicitKbForWrites.";

            string message = $"{operation} is a write operation and Server.RequireExplicitKbForWrites is enabled. "
                + $"Pass an explicit 'kb' argument. {aliasText} Nothing was written.";

            var aliasesArray = new JArray(aliases);
            return new JObject
            {
                ["status"] = "error",
                ["code"] = ExplicitKbRequiredForWriteCode,
                ["configuredAliases"] = (JArray)aliasesArray.DeepClone(),
                ["wrote"] = false,
                ["persisted"] = false,
                ["error"] = new JObject
                {
                    ["code"] = ExplicitKbRequiredForWriteCode,
                    ["message"] = message,
                    ["hint"] = hint,
                    ["tool"] = toolName,
                    ["setting"] = "Server.RequireExplicitKbForWrites",
                    ["configuredAliases"] = aliasesArray
                }
            };
        }

        internal static string FormatOpenOnlyConfiguredKbsMessage(
            Configuration? config,
            string? requestedPath = null,
            string? requestedAlias = null)
        {
            var aliases = GetConfiguredAliases(config);
            string target = !string.IsNullOrWhiteSpace(requestedPath)
                ? $"filesystem path '{requestedPath}'"
                : !string.IsNullOrWhiteSpace(requestedAlias)
                    ? $"unconfigured KB '{requestedAlias}'"
                    : "an unconfigured Knowledge Base";

            string aliasText = aliases.Count > 0
                ? $"Configured KB aliases: {string.Join(", ", aliases)}."
                : "No KB aliases are currently configured in Environment.KBs.";

            return $"Opening {target} is refused because Server.OpenOnlyConfiguredKbs is enabled. {aliasText}";
        }

        internal static JObject BuildOpenOnlyConfiguredKbsError(
            Configuration? config,
            string? requestedPath = null,
            string? requestedAlias = null)
        {
            var aliases = GetConfiguredAliases(config);
            string message = FormatOpenOnlyConfiguredKbsMessage(config, requestedPath, requestedAlias);
            string hint = aliases.Count > 0
                ? $"Use alias=<alias> with one of the configured aliases: {string.Join(", ", aliases)}."
                : "Declare KB aliases in config.Environment.KBs[] before opening a KB, or disable Server.OpenOnlyConfiguredKbs.";

            var aliasesArray = new JArray(aliases);
            var errorObj = new JObject
            {
                ["code"] = OpenOnlyConfiguredKbsCode,
                ["message"] = message,
                ["hint"] = hint,
                ["setting"] = "Server.OpenOnlyConfiguredKbs",
                ["configuredAliases"] = aliasesArray
            };
            if (!string.IsNullOrWhiteSpace(requestedPath))
                errorObj["requestedPath"] = requestedPath;
            if (!string.IsNullOrWhiteSpace(requestedAlias))
                errorObj["requestedAlias"] = requestedAlias;

            return new JObject
            {
                ["status"] = "error",
                ["code"] = OpenOnlyConfiguredKbsCode,
                ["configuredAliases"] = (JArray)aliasesArray.DeepClone(),
                ["error"] = errorObj
            };
        }

        internal static bool LooksLikeFilesystemPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string trimmed = value.Trim();
            if (trimmed.IndexOf(Path.DirectorySeparatorChar) >= 0
                || trimmed.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                return true;
            try { return Path.IsPathRooted(trimmed); }
            catch { return false; }
        }
    }
}
