using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Contract tests for issue #1113: every multi-action (action-bearing) tool in
    /// tools/list publishes additive per-action effect metadata under
    /// _meta["gxmcp/actionEffects"] so MCP clients can derive call gating from the
    /// server instead of maintaining a parallel copy.
    /// </summary>
    [Collection("Gateway route state")]
    public sealed class ActionEffectMetadataTests
    {
        private const string ExtensionKey = "gxmcp/actionEffects";

        private static readonly HashSet<string> ValidEffects = new HashSet<string>(StringComparer.Ordinal)
        {
            "read",
            "write",
            "expensive",
            "destructive",
            "external"
        };

        private static JArray FetchPublishedToolsList()
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "action-effects-test",
                ["method"] = "tools/list"
            };
            var response = McpRouter.Handle(request);
            Assert.NotNull(response);
            var tools = JObject.FromObject(response!)["tools"] as JArray;
            Assert.NotNull(tools);
            return tools!;
        }

        private static JObject GetTool(JArray tools, string name)
            => Assert.Single(tools.OfType<JObject>(), t => string.Equals(t["name"]?.ToString(), name, StringComparison.Ordinal));

        private static HashSet<string> EvaluateFromPublishedMetadata(JObject tool, JObject args)
        {
            var meta = tool["_meta"]?[ExtensionKey] as JObject;
            Assert.NotNull(meta);
            var actions = meta!["actions"] as JObject;
            Assert.NotNull(actions);

            string? action = args["action"]?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(action));
            var baseEffects = actions![action!] as JArray;
            Assert.NotNull(baseEffects);

            var current = baseEffects!.Select(token => token.ToString()).ToHashSet(StringComparer.Ordinal);
            if (meta["modifiers"] is JArray modifiers)
            {
                foreach (var modifier in modifiers.OfType<JObject>())
                {
                    if (modifier["actions"] is JArray scopedActions
                        && !scopedActions.Any(a => string.Equals(a.ToString(), action, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    string? param = modifier["param"]?.ToString();
                    Assert.False(string.IsNullOrWhiteSpace(param));
                    if (!MatchesCondition(args[param!], modifier["when"]))
                    {
                        continue;
                    }

                    if (modifier["set"] is JArray setEffects)
                    {
                        current = setEffects.Select(t => t.ToString()).ToHashSet(StringComparer.Ordinal);
                    }
                    else if (modifier["add"] is JArray addEffects)
                    {
                        foreach (var added in addEffects.Select(t => t.ToString()))
                        {
                            if (added == "write") current.Remove("read");
                            if (added == "read") current.Remove("write");
                            current.Add(added);
                        }
                    }
                }
            }

            return current;
        }

        private static bool MatchesCondition(JToken? value, JToken? when)
        {
            if (when == null) return false;
            if (when.Type == JTokenType.Boolean)
            {
                bool expected = when.Value<bool>();
                return value != null && value.Type == JTokenType.Boolean && value.Value<bool>() == expected;
            }
            if (when.Type == JTokenType.String)
            {
                string mode = when.Value<string>() ?? string.Empty;
                return mode switch
                {
                    "notFalse" => value == null
                        || value.Type == JTokenType.Null
                        || (value.Type == JTokenType.Boolean && value.Value<bool>()),
                    "present" => value != null
                        && value.Type != JTokenType.Null
                        && !(value.Type == JTokenType.String && string.IsNullOrWhiteSpace(value.Value<string>())),
                    _ => false
                };
            }
            if (when is JObject obj && obj["contains"]?.Type == JTokenType.String)
            {
                string needle = obj["contains"]!.Value<string>()!;
                if (value is JArray array)
                    return array.Any(item => string.Equals(item?.ToString(), needle, StringComparison.OrdinalIgnoreCase));
                return string.Equals(value?.ToString(), needle, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        [Fact]
        public void EveryMultiActionTool_PublishesCompleteActionEffectsExtension()
        {
            var tools = FetchPublishedToolsList();
            var multiActionTools = tools.OfType<JObject>()
                .Where(t => t["inputSchema"]?["properties"]?["action"]?["enum"] is JArray)
                .ToList();

            Assert.NotEmpty(multiActionTools);

            foreach (var tool in multiActionTools)
            {
                string toolName = tool["name"]!.ToString();
                var enumActions = ((JArray)tool["inputSchema"]!["properties"]!["action"]!["enum"]!)
                    .Select(a => a.ToString())
                    .ToList();

                var meta = tool["_meta"]?[ExtensionKey] as JObject;
                Assert.True(meta != null, $"{toolName}: missing _meta['{ExtensionKey}']");
                Assert.Equal(1, meta!["version"]?.Value<int>());

                var actions = meta["actions"] as JObject;
                Assert.True(actions != null, $"{toolName}: missing actions map in _meta['{ExtensionKey}']");

                var publishedActionNames = actions!.Properties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                Assert.True(publishedActionNames.SetEquals(enumActions),
                    $"{toolName}: published actionEffects keys do not match action enum.");

                foreach (var actionName in enumActions)
                {
                    var effects = actions[actionName] as JArray;
                    Assert.True(effects != null && effects.Count > 0, $"{toolName}:{actionName} must declare non-empty effects.");
                    var set = effects!.Select(e => e.ToString()).ToHashSet(StringComparer.Ordinal);
                    Assert.True(set.All(e => ValidEffects.Contains(e)),
                        $"{toolName}:{actionName} contains unknown effect tag: {string.Join(", ", set)}");
                    Assert.True(set.Contains("read") ^ set.Contains("write"),
                        $"{toolName}:{actionName} must contain exactly one of 'read' or 'write'.");
                    if (set.Contains("destructive"))
                    {
                        Assert.True(set.Contains("write"),
                            $"{toolName}:{actionName} cannot be 'destructive' without 'write'.");
                    }

                    // Must agree with OperationClassifier on base read vs write.
                    var kind = OperationClassifier.ClassifyAction(toolName, actionName);
                    if (kind == OperationClassifier.OperationKind.ReadOnly)
                        Assert.Contains("read", set);
                    else if (kind == OperationClassifier.OperationKind.Mutating)
                        Assert.Contains("write", set);
                }

                // Every modifier must reference real schema properties and real action enum values.
                if (meta["modifiers"] is JArray modifiers)
                {
                    Assert.NotEmpty(modifiers);
                    var schemaProps = (tool["inputSchema"]?["properties"] as JObject)?
                        .Properties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                        ?? new HashSet<string>(StringComparer.Ordinal);

                    foreach (var modifier in modifiers.OfType<JObject>())
                    {
                        string? param = modifier["param"]?.ToString();
                        Assert.False(string.IsNullOrWhiteSpace(param), $"{toolName}: modifier missing param.");
                        Assert.True(schemaProps.Contains(param!),
                            $"{toolName}: modifier param '{param}' is not declared in inputSchema.properties.");
                        Assert.True(modifier["when"] != null, $"{toolName}: modifier for '{param}' missing 'when'.");
                        Assert.True((modifier["set"] is JArray) ^ (modifier["add"] is JArray),
                            $"{toolName}: modifier for '{param}' must specify exactly one of 'set' or 'add'.");

                        if (modifier["actions"] is JArray scoped)
                        {
                            Assert.NotEmpty(scoped);
                            foreach (var a in scoped)
                            {
                                Assert.True(publishedActionNames.Contains(a.ToString()),
                                    $"{toolName}: modifier references unknown action '{a}'.");
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        public void ActionlessTools_DoNotPublishActionEffectsExtension()
        {
            var tools = FetchPublishedToolsList();
            var actionless = tools.OfType<JObject>()
                .Where(t => t["inputSchema"]?["properties"]?["action"]?["enum"] is not JArray)
                .ToList();

            Assert.NotEmpty(actionless);
            foreach (var tool in actionless)
            {
                Assert.Null(tool["_meta"]?[ExtensionKey]);
            }
        }

        [Fact]
        public void ExistingToolAnnotationsAndInputSchema_RemainUnchangedInMeaning()
        {
            var tools = FetchPublishedToolsList();
            var lifecycle = GetTool(tools, "genexus_lifecycle");

            Assert.False(lifecycle["annotations"]?["readOnlyHint"]?.Value<bool>());
            Assert.True(lifecycle["annotations"]?["destructiveHint"]?.Value<bool>());
            Assert.True(lifecycle["annotations"]?["openWorldHint"]?.Value<bool>());
            Assert.Null(lifecycle["inputSchema"]?[ExtensionKey]);
        }

        [Theory]
        // genexus_lifecycle: read vs write vs expensive vs destructive vs external + dryRun/notifyOnFailure/deploy/force
        [InlineData("genexus_lifecycle", "{\"action\":\"status\"}", new[] { "read" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"reorg_preview\"}", new[] { "read", "expensive" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"build\",\"target\":\"Customer\"}", new[] { "write", "expensive" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"build\",\"target\":\"Customer\",\"dryRun\":true}", new[] { "read" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"build\",\"target\":\"Customer\",\"notifyOnFailure\":\"https://hooks.example\"}", new[] { "write", "expensive", "external" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"build\",\"target\":\"Customer\",\"deploy\":true}", new[] { "write", "expensive", "external" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"build_all\"}", new[] { "write", "expensive", "destructive", "external" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"build_all\",\"dryRun\":true}", new[] { "read" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"rebuild\"}", new[] { "write", "expensive", "destructive", "external" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"rebuild\",\"dryRun\":true}", new[] { "read" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"reorg\"}", new[] { "write", "expensive", "destructive" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"reorg\",\"dryRun\":true}", new[] { "write", "expensive", "destructive" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"index\"}", new[] { "write" })]
        [InlineData("genexus_lifecycle", "{\"action\":\"index\",\"force\":true}", new[] { "write", "expensive" })]
        // genexus_browser: buildFirst, updateBaseline, capture=screenshot change otherwise read-only preview
        [InlineData("genexus_browser", "{\"action\":\"preview\"}", new[] { "read", "external" })]
        [InlineData("genexus_browser", "{\"action\":\"preview\",\"buildFirst\":true}", new[] { "write", "expensive", "external" })]
        [InlineData("genexus_browser", "{\"action\":\"preview\",\"updateBaseline\":true}", new[] { "write", "external" })]
        [InlineData("genexus_browser", "{\"action\":\"preview\",\"capture\":[\"html\",\"screenshot\"]}", new[] { "write", "external" })]
        [InlineData("genexus_browser", "{\"action\":\"preview\",\"buildFirst\":true,\"updateBaseline\":true}", new[] { "write", "expensive", "external" })]
        // rebuild parameter escalates to expensive
        [InlineData("genexus_module", "{\"action\":\"package\"}", new[] { "write" })]
        [InlineData("genexus_module", "{\"action\":\"package\",\"rebuild\":true}", new[] { "write", "expensive" })]
        [InlineData("genexus_gam", "{\"action\":\"deploy\"}", new[] { "write", "destructive" })]
        [InlineData("genexus_gam", "{\"action\":\"deploy\",\"rebuild\":true}", new[] { "write", "expensive", "destructive" })]
        [InlineData("genexus_gxserver", "{\"action\":\"pipeline_run\"}", new[] { "write", "external" })]
        [InlineData("genexus_gxserver", "{\"action\":\"pipeline_run\",\"rebuild\":true}", new[] { "write", "expensive", "external" })]
        // default-dryRun=true actions: omitted/true => read, explicit false => write
        [InlineData("genexus_transfer", "{\"action\":\"import\",\"file\":\"a.xpz\"}", new[] { "read" })]
        [InlineData("genexus_transfer", "{\"action\":\"import\",\"file\":\"a.xpz\",\"dryRun\":false}", new[] { "write", "expensive", "destructive" })]
        [InlineData("genexus_connection_recover", "{\"action\":\"journal_repair\"}", new[] { "read" })]
        [InlineData("genexus_connection_recover", "{\"action\":\"journal_repair\",\"dryRun\":false}", new[] { "write" })]
        [InlineData("genexus_db", "{\"action\":\"records_insert\"}", new[] { "read" })]
        [InlineData("genexus_db", "{\"action\":\"records_insert\",\"dryRun\":false}", new[] { "write" })]
        [InlineData("genexus_db", "{\"action\":\"records_update\",\"dryRun\":false}", new[] { "write", "destructive" })]
        // genexus_navigation latest=true reads cache vs default refreshes cache
        [InlineData("genexus_navigation", "{\"action\":\"view\",\"name\":\"Customer\"}", new[] { "write" })]
        [InlineData("genexus_navigation", "{\"action\":\"view\",\"name\":\"Customer\",\"latest\":true}", new[] { "read" })]
        // genexus_deploy leaves machine & is destructive
        [InlineData("genexus_deploy", "{\"action\":\"list_targets\"}", new[] { "read" })]
        [InlineData("genexus_deploy", "{\"action\":\"deploy\"}", new[] { "write", "expensive", "destructive", "external" })]
        // genexus_io listOnly and dryRun
        [InlineData("genexus_io", "{\"action\":\"delete_kb_objects\"}", new[] { "write", "destructive" })]
        [InlineData("genexus_io", "{\"action\":\"delete_kb_objects\",\"dryRun\":true}", new[] { "read" })]
        [InlineData("genexus_io", "{\"action\":\"import_text_to_kb\",\"listOnly\":true}", new[] { "read" })]
        public void PublishedActionEffects_EvaluatesParametersDeterministically(
            string toolName, string argsJson, string[] expectedEffects)
        {
            var tools = FetchPublishedToolsList();
            var tool = GetTool(tools, toolName);
            var actual = EvaluateFromPublishedMetadata(tool, JObject.Parse(argsJson));

            Assert.Equal(
                expectedEffects.OrderBy(e => e, StringComparer.Ordinal),
                actual.OrderBy(e => e, StringComparer.Ordinal));
        }

        [Theory]
        [InlineData("genexus_data_view", "{\"action\":\"delete\",\"transaction\":\"T\",\"dataViewName\":\"DV\"}")]
        [InlineData("genexus_transfer", "{\"action\":\"import\",\"file\":\"a.xpz\",\"dryRun\":false}")]
        [InlineData("genexus_io", "{\"action\":\"delete_kb_objects\"}")]
        [InlineData("genexus_io", "{\"action\":\"import_part\",\"name\":\"P\",\"inputPath\":\"p.txt\"}")]
        [InlineData("genexus_io", "{\"action\":\"import_text_to_kb\",\"inputPath\":\"dir\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"reorg\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"reorg\",\"dryRun\":true}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"build_all\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"rebuild\"}")]
        [InlineData("genexus_deploy", "{\"action\":\"deploy\"}")]
        public void EveryElicitationGatedMultiActionCall_IsPublishedAsDestructive(string toolName, string argsJson)
        {
            var args = JObject.Parse(argsJson);
            Assert.NotNull(Program.DescribeDestructiveOperation(toolName, args));

            var tools = FetchPublishedToolsList();
            var tool = GetTool(tools, toolName);
            var effects = EvaluateFromPublishedMetadata(tool, args);

            Assert.Contains("destructive", effects);
            Assert.Contains("write", effects);
        }

        [Theory]
        [InlineData("core")]
        [InlineData("standard")]
        [InlineData("authoring")]
        [InlineData("devops")]
        [InlineData("ui")]
        [InlineData("db")]
        [InlineData("adaptive")]
        [InlineData("unknown-profile")]
        public async System.Threading.Tasks.Task NonAllProfiles_OmitActionEffectsFromToolsList(string profile)
        {
            string? previous = Environment.GetEnvironmentVariable("GXMCP_PROFILE");
            Environment.SetEnvironmentVariable("GXMCP_PROFILE", profile);
            try
            {
                var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" };
                var response = await Program.ProcessMcpRequest(request, "action-effects-" + profile, true);
                var tools = Assert.IsType<JArray>(response?["result"]?["tools"]);
                Assert.Contains(tools, t => t["inputSchema"]?["properties"]?["action"]?["enum"] is JArray);
                Assert.All(tools, tool => Assert.Null(tool["_meta"]?[ExtensionKey]));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_PROFILE", previous);
            }
        }
    }
}
