using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A hint that names a tool the server does not publish sends the agent into a dead
    /// end (UNKNOWN_TOOL). Every tool named in nextSteps, replacedBy or a prompt must
    /// exist in tool_definitions.json, and a named action must exist on that tool.
    /// </summary>
    public sealed class PublishedToolHintTests
    {
        private static readonly Regex BacktickedTool = new Regex(@"`(genexus_[a-z0-9_]+)`", RegexOptions.Compiled);

        private static bool IsPublished(string tool) =>
            ToolIdentity.CanonicalToolNames.Contains(tool, StringComparer.OrdinalIgnoreCase);

        [Fact]
        public async Task InvalidArgsNextSteps_NameOnlyPublishedTools()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_inspect", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["name"] = new JObject { ["type"] = "string" } },
                ["required"] = new JArray("name")
            });

            var response = await Program.ProcessMcpRequest(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "published-hint-1",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_inspect",
                    ["arguments"] = new JObject { ["include"] = new JArray("metadata") }
                }
            });
            var text = ((response!["result"] as JObject)!["content"] as JArray)![0]!["text"]!.ToString();
            var nextSteps = JObject.Parse(text)["error"]?["nextSteps"] as JArray;

            Assert.NotNull(nextSteps);
            Assert.NotEmpty(nextSteps!);
            Assert.All(nextSteps!.OfType<JObject>(), step =>
            {
                string tool = step["tool"]!.ToString();
                Assert.True(IsPublished(tool), "nextSteps names an unpublished tool: " + tool);
            });
        }

        [Fact]
        public void RemovedToolReplacements_ArePublishedToolsWithTheNamedAction()
        {
            var problems = RemovedToolsRegistry.Map
                .Select(kv =>
                {
                    string replacement = kv.Value.ReplacedBy;
                    if (!IsPublished(replacement)) return kv.Key + " -> unpublished tool " + replacement;
                    var action = Regex.Match(kv.Value.ArgHint ?? string.Empty, @"action=([A-Za-z0-9_\-]+)");
                    if (action.Success
                        && !ToolIdentity.ActionsFor(replacement).Contains(action.Groups[1].Value, StringComparer.Ordinal))
                        return kv.Key + " -> " + replacement + " has no action " + action.Groups[1].Value;
                    return null;
                })
                .Where(p => p != null)
                .ToArray();

            Assert.True(problems.Length == 0, string.Join("; ", problems));
        }

        [Fact]
        public void VisualChangePrompt_NamesOnlyPublishedTools()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_agent_visual_change","arguments":{"name":"Customer","changeGoal":"Resize logo"}}}""");

            var json = JObject.FromObject(McpRouter.Handle(request)!);
            string text = json["messages"]![0]!["content"]?["text"]?.ToString() ?? string.Empty;

            var named = BacktickedTool.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.NotEmpty(named);
            Assert.All(named, tool => Assert.True(IsPublished(tool), "prompt names an unpublished tool: " + tool));
        }
    }
}
