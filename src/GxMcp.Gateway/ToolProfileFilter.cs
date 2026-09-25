using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public static class ToolProfileFilter
    {
        private static readonly HashSet<string> CoreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_whoami",
            "genexus_query",
            "genexus_list_objects",
            "genexus_read",
            "genexus_edit",
            "genexus_inspect",
            "genexus_analyze",
            "genexus_lifecycle",
            "genexus_search_source",
            "genexus_kb",
            "genexus_doc"
        };

        private static readonly HashSet<string> AuthoringTools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_create",
            "genexus_structure",
            "genexus_variable",
            "genexus_authoring",
            "genexus_refactor",
            "genexus_properties",
            "genexus_format",
            "genexus_delete_object",
            "genexus_api",
            "genexus_data_view",
            "genexus_recipe",
            "genexus_module",
            "genexus_generator_reference",
            "genexus_layout",
            "genexus_edit_form",
            "genexus_k2b_designer",
            "genexus_wwp",
            "genexus_apply_pattern",
            "genexus_edit_and_build"
        };

        private static readonly HashSet<string> StandardTools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_create",
            "genexus_structure",
            "genexus_variable",
            "genexus_properties",
            "genexus_io"
        };

        private static readonly HashSet<string> DevOpsTools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_test",
            "genexus_sdk_probe",
            "genexus_worker_reload",
            "genexus_doctor",
            "genexus_run_object",
            "genexus_compare",
            "genexus_merge",
            "genexus_gxserver",
            "genexus_kb_version",
            "genexus_versioning",
            "genexus_memory",
            "genexus_transfer",
            "genexus_deploy",
            "genexus_telemetry",
            "genexus_security",
            "genexus_io",
            "genexus_connection_recover",
            "genexus_gam",
            "genexus_kb_diff",
            "genexus_kb_import",
            "genexus_sandbox",
            "genexus_worker_pool"
        };

        private static readonly HashSet<string> UITools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_layout",
            "genexus_edit_form",
            "genexus_k2b_designer",
            "genexus_browser",
            "genexus_wwp",
            "genexus_apply_pattern",
            "genexus_structure",
            "genexus_properties"
        };

        private static readonly HashSet<string> DbTools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_db",
            "genexus_data_view",
            "genexus_structure",
            "genexus_navigation"
        };

        public static string ResolveActiveProfile(string? configuredProfile = null)
        {
            string? envProfile = global::System.Environment.GetEnvironmentVariable("GXMCP_PROFILE");
            if (!string.IsNullOrWhiteSpace(envProfile))
            {
                return envProfile.Trim().ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(configuredProfile))
            {
                return configuredProfile.Trim().ToLowerInvariant();
            }

            return "all";
        }

        public static JArray Filter(JArray tools, string? profile)
        {
            if (tools == null || tools.Count == 0) return new JArray();

            var allowlist = GetAllowlist(profile);
            IEnumerable<JToken> selected = allowlist == null
                ? tools
                : tools.OfType<JObject>().Where(tool => allowlist.Contains(tool["name"]?.ToString() ?? string.Empty));
            return Compact(selected);
        }

        public static JObject? GetToolNotInProfileError(string? profile, string toolName)
        {
            var allowlist = GetAllowlist(profile);
            if (allowlist == null || allowlist.Contains(toolName)) return null;

            var availableProfiles = GetProfilesForTool(toolName);
            if (availableProfiles.Count == 0) return null;

            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "ToolNotInProfile",
                    ["message"] = $"Tool '{toolName}' is not exposed by the active profile '{profile}'.",
                    ["activeProfile"] = profile,
                    ["availableProfiles"] = JArray.FromObject(availableProfiles),
                    ["hint"] = $"Set Server.ToolProfile or GXMCP_PROFILE to {string.Join(" or ", availableProfiles.Select(name => $"'{name}'"))}. Profiles can be combined with '+'."
                }
            };
        }

        private static HashSet<string>? GetAllowlist(string? profile)
        {
            string normalizedProfile = (profile ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalizedProfile)) return null;

            var tokens = normalizedProfile.Split(new[] { ',', '+', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Any(token => string.Equals(token, "all", StringComparison.OrdinalIgnoreCase)))
                return null;

            var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool matchedAny = false;
            foreach (var token in tokens)
            {
                var set = token switch
                {
                    "core" or "exploration" => CoreTools,
                    "standard" => StandardTools,
                    "authoring" or "safe-edit" or "safe_edit" => AuthoringTools,
                    "devops" or "cicd" or "build" or "versioning" or "deploy" => DevOpsTools,
                    "ui" or "frontend" => UITools,
                    "db" or "data" => DbTools,
                    _ => null
                };
                if (set == null) continue;

                matchedAny = true;
                foreach (var toolName in set) allowlist.Add(toolName);
            }

            // Preserve historical fail-open behavior for unknown-only profile names.
            return matchedAny ? allowlist : null;
        }

        private static List<string> GetProfilesForTool(string toolName)
        {
            var profiles = new List<string>();
            if (StandardTools.Contains(toolName)) profiles.Add("standard");
            if (CoreTools.Contains(toolName)) profiles.Add("core");
            if (AuthoringTools.Contains(toolName)) profiles.Add("authoring");
            if (DevOpsTools.Contains(toolName)) profiles.Add("devops");
            if (UITools.Contains(toolName)) profiles.Add("ui");
            if (DbTools.Contains(toolName)) profiles.Add("db");
            return profiles;
        }

        private static JArray Compact(IEnumerable<JToken> tools)
        {
            var result = new JArray();
            foreach (JToken tool in tools)
            {
                if (tool is not JObject definition)
                {
                    result.Add(tool.DeepClone());
                    continue;
                }

                var compacted = (JObject)definition.DeepClone();
                string? name = compacted["name"]?.ToString();
                compacted["description"] = string.IsNullOrWhiteSpace(name)
                    ? "Full tool guidance is available from the tool-help resource."
                    : $"Full guidance: genexus://kb/tool-help/{name}";
                if (compacted["inputSchema"] is JToken schema) CompactSchema(schema);
                result.Add(compacted);
            }
            return result;
        }

        private static void CompactSchema(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (string.Equals(property.Name, "examples", StringComparison.OrdinalIgnoreCase))
                    {
                        property.Remove();
                    }
                    else if (string.Equals(property.Name, "description", StringComparison.OrdinalIgnoreCase)
                        && property.Value.Type == JTokenType.String)
                    {
                        property.Value = CompactDescription(property.Value.ToString());
                    }
                    else
                    {
                        CompactSchema(property.Value);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken item in array) CompactSchema(item);
            }
        }

        private static string CompactDescription(string description)
        {
            const int maxLength = 40;
            string normalized = string.Join(" ", description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length <= maxLength) return normalized;

            int end = normalized.LastIndexOf(' ', maxLength - 1);
            if (end <= 0) end = maxLength - 1;
            return normalized.Substring(0, end).TrimEnd() + "…";
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JArray> _profileCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);

        public static void InvalidateCache()
        {
            _profileCache.Clear();
        }

        public static JArray GetOrCreateFiltered(JArray tools, string? profile)
        {
            if (tools == null || tools.Count == 0) return new JArray();
            string normalizedProfile = (profile ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalizedProfile) || normalizedProfile == "all")
            {
                return Filter(tools, "all");
            }

            return _profileCache.GetOrAdd(normalizedProfile, p => Filter(tools, p));
        }
    }
}
