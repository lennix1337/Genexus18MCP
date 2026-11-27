using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public static class ToolProfileFilter
    {
        private static readonly HashSet<string> CoreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_whoami",
            "genexus_wire",
            "genexus_query",
            "genexus_list_objects",
            "genexus_read",
            "genexus_edit",
            "genexus_inspect",
            "genexus_analyze",
            "genexus_dfd",
            "genexus_impact",
            "genexus_object_context",
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
            "genexus_workwith",
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

        /// <summary>
        /// Inputs left out of the schema published by every profile except <c>all</c>.
        /// They stay in <c>tool_definitions.json</c>, so the Gateway validates and
        /// dispatches them as before and <c>genexus://kb/tool-help/&lt;tool&gt;</c> still
        /// documents them: only the list a model reads to pick arguments gets shorter.
        /// Listed here are aliases of an input that stays published, tuning caps,
        /// paging and wait refinements, verification overrides and experimental flags.
        /// Never list a required input or the only input an action takes.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string[]> AdvancedInputs =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_edit"] = new[]
                {
                    "expectedVersion", "indentation", "scope", "estimated_seconds", "includePersistedText",
                    "return_post_state", "verbose", "verifyRollback", "verifyMode", "requireObjectSave",
                    "forceWrite", "concurrencyPolicy", "visualVerify", "contentPath"
                },
                ["genexus_lifecycle"] = new[]
                {
                    "jobId", "page_size", "buildPlanCap", "callerCap", "fastIncremental", "skipFullDeploy",
                    "notifyOnFailure", "log_limit", "log_offset", "page", "pageSize", "since", "until",
                    "freshness", "estimated_seconds", "expectedVersion"
                },
                ["genexus_io"] = new[]
                {
                    "includeModuleMetadata", "includeModulePackages", "includeTableProjections", "indentString",
                    "includeReferences", "importMissingReferences", "forceSave", "listOnly", "skip", "stopOnError",
                    "intervalMs", "initialSync", "full", "includePersistedText", "relativeRoot", "ignore",
                    "pathPrefix", "includeChildren"
                },
                ["genexus_create"] = new[] { "objectType", "updateExisting", "parentPath" }
            };

        private static readonly Dictionary<string, HashSet<string>> AdvancedInputSets =
            AdvancedInputs.ToDictionary(
                kvp => kvp.Key,
                kvp => new HashSet<string>(kvp.Value, StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);

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
            // No allowlist means `all` (or an unknown profile, which fails open): the complete surface.
            var tokens = (profile ?? string.Empty).Split(new[] { ',', '+', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool includeActionEffects = tokens.Length == 0 || tokens.Any(token => string.Equals(token, "all", StringComparison.OrdinalIgnoreCase));
            return allowlist == null
                ? Compact(tools, hideAdvancedInputs: false, includeActionEffects: includeActionEffects)
                : Compact(
                    tools.OfType<JObject>().Where(tool => allowlist.Contains(tool["name"]?.ToString() ?? string.Empty)),
                    hideAdvancedInputs: true);
        }

        /// <summary>Core tools plus those of the enabled adaptive groups, then the meta tool.</summary>
        internal static JArray FilterAdaptive(JArray tools, IEnumerable<string> extraToolNames)
        {
            var allowlist = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase);
            foreach (var name in extraToolNames) allowlist.Add(name);
            var result = Compact(
                tools.OfType<JObject>().Where(t => allowlist.Contains(t["name"]?.ToString() ?? string.Empty)),
                hideAdvancedInputs: true);
            result.Add(AdaptiveTools.BuildMetaToolDefinition());
            return result;
        }

        public static JObject? GetToolNotInProfileError(string? profile, string toolName)
        {
            // adaptive never refuses a published tool: the caller auto-enables its group.
            if (AdaptiveTools.IsAdaptive(profile)) return null;
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
                    "core" or "exploration" or AdaptiveTools.ProfileName => CoreTools,
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

        private static JArray Compact(IEnumerable<JToken> tools, bool hideAdvancedInputs, bool includeActionEffects = false)
        {
            var result = new JArray();
            foreach (JToken tool in tools)
            {
                if (tool is not JObject definition)
                {
                    result.Add(tool.DeepClone());
                    continue;
                }

                string? toolName = definition["name"]?.ToString();
                HashSet<string>? hiddenProps = hideAdvancedInputs
                    && !string.IsNullOrEmpty(toolName)
                    && AdvancedInputSets.TryGetValue(toolName, out var hidden)
                    ? hidden
                    : null;

                var compacted = new JObject();
                bool wroteDescription = false;
                for (JToken? child = definition.First; child != null; child = child.Next)
                {
                    if (child is not JProperty prop) continue;
                    if (string.Equals(prop.Name, "description", StringComparison.Ordinal))
                    {
                        string? desc = prop.Value is JValue dv && dv.Value is string ds
                            ? ds
                            : (prop.Value.Type == JTokenType.Null ? null : prop.Value.ToString());
                        compacted[prop.Name] = BuildCompactToolDescription(toolName, desc);
                        wroteDescription = true;
                    }
                    else if (string.Equals(prop.Name, "inputSchema", StringComparison.Ordinal))
                    {
                        compacted[prop.Name] = CloneCompactedSchema(prop.Value, hiddenProps, isTopLevelSchema: true);
                    }
                    else
                    {
                        compacted[prop.Name] = prop.Value.DeepClone();
                    }
                }
                if (!wroteDescription)
                {
                    compacted["description"] = BuildCompactToolDescription(toolName, null);
                }

                var actionEffects = includeActionEffects ? ActionEffectCatalog.BuildForTool(definition) : null;
                if (actionEffects != null)
                {
                    var meta = compacted["_meta"] as JObject ?? new JObject();
                    meta[ActionEffectCatalog.MetaExtensionKey] = actionEffects;
                    compacted["_meta"] = meta;
                }

                result.Add(compacted);
            }
            return result;
        }

        /// <summary>
        /// Upper bound, in characters, of the purpose summary published in
        /// <c>tools/list</c>. Clients choose a tool from this text alone, so it has
        /// to say what the tool is for; the complete description stays one
        /// <c>resources/read</c> away in the tool-help resource.
        /// </summary>
        internal const int MaxToolSummaryLength = 200;

        private static readonly Regex ToolHelpPointerSentence = new Regex(
            @"\bSee genexus://kb/tool-help/\S+?(?:\.(?=\s|$)|(?=\s|$))(?:\s+for [^.]*\.)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] Abbreviations = { "e.g.", "i.e.", "etc.", "vs.", "approx." };

        internal static string BuildCompactToolDescription(string? name, string? description)
        {
            string pointer = string.IsNullOrWhiteSpace(name)
                ? "Full tool guidance is available from the tool-help resource."
                : $"Full guidance: genexus://kb/tool-help/{name}";
            string summary = BuildToolSummary(description);
            return summary.Length == 0 ? pointer : summary + " " + pointer;
        }

        internal static string BuildToolSummary(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return string.Empty;

            string withoutPointer = description.IndexOf("genexus://kb/tool-help/", StringComparison.Ordinal) >= 0
                ? ToolHelpPointerSentence.Replace(description, " ")
                : description;
            string text = NormalizeWhitespace(withoutPointer);
            if (text.Length <= MaxToolSummaryLength) return text;

            int sentenceEnd = LastSentenceEnd(text, MaxToolSummaryLength);
            if (sentenceEnd >= MaxToolSummaryLength * 3 / 5) return text.Substring(0, sentenceEnd + 1);

            // No sentence fits: prefer the last complete clause (";"), else the last word.
            int clauseEnd = LastClauseEnd(text, MaxToolSummaryLength);
            int end = clauseEnd >= MaxToolSummaryLength * 3 / 5
                ? clauseEnd
                : text.LastIndexOf(' ', MaxToolSummaryLength - 1);
            if (end <= 0) end = MaxToolSummaryLength - 1;
            return TrimDanglingWords(text.Substring(0, end)) + "\u2026";
        }

        private static readonly char[] SummaryTrailingPunctuation = { ' ', ',', ';', ':', '(', '|', '-', '\u2014', '+' };

        // Words that cannot end a phrase; cutting right after one leaves a clause open
        // ("... with what was").
        private static readonly HashSet<string> DanglingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "of", "to", "for", "with", "without", "in", "on", "by", "from", "at", "as", "into",
            "and", "or", "but", "if", "when", "that", "which", "what", "was", "is", "are", "be", "not", "than", "so", "per"
        };

        private static string TrimDanglingWords(string cut)
        {
            string trimmed = cut.TrimEnd(SummaryTrailingPunctuation);
            while (true)
            {
                int space = trimmed.LastIndexOf(' ');
                if (space <= 0 || !DanglingWords.Contains(trimmed.Substring(space + 1))) break;
                trimmed = trimmed.Substring(0, space).TrimEnd(SummaryTrailingPunctuation);
            }
            return trimmed;
        }

        private static int LastClauseEnd(string text, int limit)
        {
            int last = -1;
            int depth = 0;
            int max = Math.Min(limit, text.Length);
            for (int i = 0; i < max; i++)
            {
                char c = text[i];
                if (c == '(' || c == '[') depth++;
                else if ((c == ')' || c == ']') && depth > 0) depth--;
                else if (c == ';' && depth == 0) last = i;
            }
            return last;
        }

        private static int LastSentenceEnd(string text, int limit)
        {
            int last = -1;
            int depth = 0;
            int max = Math.Min(limit, text.Length);
            for (int i = 0; i < max; i++)
            {
                char c = text[i];
                if (c == '(' || c == '[') depth++;
                else if ((c == ')' || c == ']') && depth > 0) depth--;
                else if ((c == '.' || c == '!' || c == '?')
                    && depth == 0
                    && (i + 1 == text.Length || text[i + 1] == ' ')
                    && !EndsWithAbbreviation(text, i))
                {
                    last = i;
                }
            }
            return last;
        }

        private static bool EndsWithAbbreviation(string text, int periodIndex)
        {
            int start = text.LastIndexOf(' ', periodIndex) + 1;
            string word = text.Substring(start, periodIndex - start + 1).TrimStart('(', '[');
            return Abbreviations.Any(a => string.Equals(word, a, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsToolExposed(string? profile, string toolName)
        {
            var allowlist = GetAllowlist(profile);
            return allowlist == null || allowlist.Contains(toolName);
        }

        private static JToken CloneCompactedSchema(JToken token, HashSet<string>? hiddenTopLevelProps, bool isTopLevelSchema)
        {
            if (token is JObject obj)
            {
                var clone = new JObject();
                for (JToken? child = obj.First; child != null; child = child.Next)
                {
                    if (child is not JProperty property) continue;
                    string propName = property.Name;
                    if (string.Equals(propName, "examples", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (string.Equals(propName, "description", StringComparison.OrdinalIgnoreCase)
                        && property.Value is JValue dv && dv.Type == JTokenType.String && dv.Value is string descStr)
                    {
                        clone[propName] = CompactDescription(descStr);
                    }
                    else if (isTopLevelSchema
                        && hiddenTopLevelProps != null
                        && string.Equals(propName, "properties", StringComparison.Ordinal)
                        && property.Value is JObject propsObj)
                    {
                        var filteredProps = new JObject();
                        for (JToken? propChild = propsObj.First; propChild != null; propChild = propChild.Next)
                        {
                            if (propChild is not JProperty inputProp) continue;
                            if (hiddenTopLevelProps.Contains(inputProp.Name)) continue;
                            filteredProps[inputProp.Name] = CloneCompactedSchema(inputProp.Value, null, isTopLevelSchema: false);
                        }
                        clone[propName] = filteredProps;
                    }
                    else if (property.Value is JValue)
                    {
                        clone[propName] = property.Value.DeepClone();
                    }
                    else
                    {
                        clone[propName] = CloneCompactedSchema(property.Value, null, isTopLevelSchema: false);
                    }
                }
                return clone;
            }
            if (token is JArray array)
            {
                var clone = new JArray();
                for (int i = 0; i < array.Count; i++)
                {
                    JToken item = array[i];
                    clone.Add(item is JValue ? item.DeepClone() : CloneCompactedSchema(item, null, isTopLevelSchema: false));
                }
                return clone;
            }
            return token.DeepClone();
        }

        private static string NormalizeWhitespace(string input)
        {
            int srcLen = input.Length;
            char[]? rented = null;
            Span<char> buf = srcLen <= 512
                ? stackalloc char[srcLen]
                : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(srcLen)).AsSpan(0, srcLen);
            try
            {
                int len = 0;
                bool pendingSpace = false;
                bool modified = false;
                for (int i = 0; i < srcLen; i++)
                {
                    char c = input[i];
                    if (char.IsWhiteSpace(c))
                    {
                        if (len > 0)
                        {
                            if (pendingSpace || c != ' ') modified = true;
                            pendingSpace = true;
                        }
                        else
                        {
                            modified = true;
                        }
                        continue;
                    }
                    if (pendingSpace)
                    {
                        buf[len++] = ' ';
                        pendingSpace = false;
                    }
                    buf[len++] = c;
                }
                if (pendingSpace) modified = true;
                if (len == 0) return string.Empty;
                return !modified ? input : new string(buf.Slice(0, len));
            }
            finally
            {
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }

        private static string CompactDescription(string description)
        {
            const int maxLength = 40;
            if (string.IsNullOrEmpty(description)) return string.Empty;

            Span<char> buf = stackalloc char[maxLength + 1];
            int len = 0;
            bool pendingSpace = false;
            bool modified = false;
            bool overflow = false;

            for (int i = 0; i < description.Length; i++)
            {
                char c = description[i];
                if (char.IsWhiteSpace(c))
                {
                    if (len > 0)
                    {
                        if (pendingSpace || c != ' ') modified = true;
                        pendingSpace = true;
                    }
                    else
                    {
                        modified = true;
                    }
                    continue;
                }

                if (pendingSpace)
                {
                    if (len == maxLength)
                    {
                        overflow = true;
                        break;
                    }
                    buf[len++] = ' ';
                    pendingSpace = false;
                }

                if (len == maxLength)
                {
                    overflow = true;
                    break;
                }
                buf[len++] = c;
            }

            if (!overflow)
            {
                if (pendingSpace) modified = true;
                if (len == 0) return string.Empty;
                return !modified ? description : new string(buf.Slice(0, len));
            }

            int end = buf.Slice(0, maxLength).LastIndexOf(' ');
            if (end <= 0) end = maxLength - 1;
            while (end > 0 && buf[end - 1] == ' ') end--;
            buf[end] = '…';
            return new string(buf.Slice(0, end + 1));
        }

        private static System.Runtime.CompilerServices.ConditionalWeakTable<JArray, System.Collections.Concurrent.ConcurrentDictionary<string, JArray>> _profileCacheBySource =
            new System.Runtime.CompilerServices.ConditionalWeakTable<JArray, System.Collections.Concurrent.ConcurrentDictionary<string, JArray>>();

        public static void InvalidateCache()
        {
            _profileCacheBySource = new System.Runtime.CompilerServices.ConditionalWeakTable<JArray, System.Collections.Concurrent.ConcurrentDictionary<string, JArray>>();
        }

        public static JArray GetOrCreateFiltered(JArray tools, string? profile)
        {
            if (tools == null || tools.Count == 0) return new JArray();
            string normalizedProfile = (profile ?? "").Trim().ToLowerInvariant();
            string cacheKey = string.IsNullOrEmpty(normalizedProfile) ? "all" : normalizedProfile;
            var perSource = _profileCacheBySource.GetOrCreateValue(tools);
            return perSource.GetOrAdd(cacheKey, p => Filter(tools, p));
        }
    }
}
