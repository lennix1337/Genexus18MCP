using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Single tool-identity registry for canonical names, action projections
    /// and legacy aliases. It projects <c>tool_definitions.json</c> and
    /// <see cref="McpRouter.TryRewriteLegacyTool"/> without maintaining a
    /// second list. OperationClassifier and NextLegalActionsBuilder consume
    /// this registry before applying cache, retry or follow-up policy.
    /// </summary>
    internal static class ToolIdentity
    {
        private static readonly object _loadLock = new object();
        private static JArray? _toolDefs;
        private static IReadOnlyList<string>? _canonicalToolNames;
        private static readonly Dictionary<string, IReadOnlyList<string>> _actionsCache =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<string> CanonicalToolNames
        {
            get
            {
                EnsureLoaded();
                return _canonicalToolNames ?? Array.Empty<string>();
            }
        }

        public static string ResolveCanonical(string nameOrAlias)
        {
            if (string.IsNullOrWhiteSpace(nameOrAlias)) return nameOrAlias;

            EnsureLoaded();
            if (_canonicalToolNames != null &&
                _canonicalToolNames.Any(n => string.Equals(n, nameOrAlias, StringComparison.OrdinalIgnoreCase)))
            {
                return nameOrAlias;
            }

            if (McpRouter.TryRewriteLegacyTool(nameOrAlias, null, out var rewrittenName, out _))
            {
                return rewrittenName;
            }

            return nameOrAlias;
        }

        public static IReadOnlyList<string> ActionsFor(string canonicalTool)
        {
            if (string.IsNullOrWhiteSpace(canonicalTool)) return Array.Empty<string>();

            if (_actionsCache.TryGetValue(canonicalTool, out var cached))
                return cached;

            EnsureLoaded();
            IReadOnlyList<string> result = Array.Empty<string>();
            var tool = _toolDefs?.OfType<JObject>()
                .FirstOrDefault(t => string.Equals(t["name"]?.ToString(), canonicalTool, StringComparison.OrdinalIgnoreCase));
            var actionEnum = tool?["inputSchema"]?["properties"]?["action"]?["enum"] as JArray;
            if (actionEnum != null)
            {
                result = actionEnum.Select(a => a.ToString()).ToList();
            }

            _actionsCache[canonicalTool] = result;
            return result;
        }

        public static bool IsKnownTool(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            EnsureLoaded();
            if (_canonicalToolNames != null &&
                _canonicalToolNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return McpRouter.TryRewriteLegacyTool(name, null, out _, out _);
        }

        public static bool IsRemoved(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return RemovedToolsRegistry.Map.ContainsKey(name);
        }

        // ── Loading (mirrors GatewayArgsValidator.EnsureToolDefsLoaded/LocateToolDefinitions) ──

        private static void EnsureLoaded()
        {
            if (_toolDefs != null) return;
            lock (_loadLock)
            {
                if (_toolDefs != null) return;
                string? path = LocateToolDefinitions();
                if (path == null) return;
                try
                {
                    _toolDefs = JArray.Parse(File.ReadAllText(path));
                    _canonicalToolNames = _toolDefs.OfType<JObject>()
                        .Select(t => t["name"]?.ToString())
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .ToList();
                }
                catch
                {
                    // Silently fail — same degrade-to-no-op behavior as GatewayArgsValidator.
                }
            }
        }

        private static string? LocateToolDefinitions()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return beside;

            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string c1 = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(c1)) return c1;
                string c2 = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(c2)) return c2;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
    }
}
