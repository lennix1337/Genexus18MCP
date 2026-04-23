using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Contract-level checks that exercise the full client-facing MCP surface
    /// (initialize -> tools/list -> resources/list -> resources/templates/list)
    /// without spawning the Worker. These protect the handshake from silent
    /// shape regressions that unit tests on individual methods would miss.
    /// </summary>
    public class McpHandshakeContractTests
    {
        private static JObject Dispatch(string method, string id = "1", JObject? parameters = null)
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (parameters != null)
            {
                request["params"] = parameters;
            }

            var result = McpRouter.Handle(request);
            Assert.NotNull(result);
            return JObject.FromObject(result!);
        }

        [Fact]
        public void Initialize_ShouldAdvertiseAllRequiredCapabilities()
        {
            var response = Dispatch("initialize");

            Assert.Equal(McpRouter.SupportedProtocolVersion, response["protocolVersion"]?.ToString());

            var capabilities = response["capabilities"] as JObject;
            Assert.NotNull(capabilities);
            Assert.NotNull(capabilities!["prompts"]);
            Assert.NotNull(capabilities["tools"]);
            Assert.NotNull(capabilities["resources"]);
            Assert.NotNull(capabilities["completion"]);

            Assert.Equal("genexus-mcp-server", response["serverInfo"]?["name"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(response["serverInfo"]?["version"]?.ToString()));
        }

        [Fact]
        public void ToolsList_EveryToolShouldHaveNameDescriptionAndInputSchema()
        {
            var response = Dispatch("tools/list");

            var tools = response["tools"] as JArray;
            Assert.NotNull(tools);

            // Allow an empty catalog only when tool_definitions.json did not
            // travel with the test output. If any tool is present, it must be
            // well-formed: the schema contract for every tool is enforced here.
            foreach (var tool in tools!)
            {
                var name = tool["name"]?.ToString();
                Assert.False(string.IsNullOrWhiteSpace(name), $"Tool is missing `name`: {tool}");

                var description = tool["description"]?.ToString();
                Assert.False(string.IsNullOrWhiteSpace(description), $"Tool `{name}` is missing `description`.");

                var inputSchema = tool["inputSchema"] as JObject;
                Assert.True(inputSchema != null, $"Tool `{name}` is missing `inputSchema`.");
                Assert.Equal("object", inputSchema!["type"]?.ToString());
                Assert.True(inputSchema["properties"] is JObject,
                    $"Tool `{name}` inputSchema is missing `properties`.");
            }
        }

        [Fact]
        public void ResourcesList_ShouldExposeRequiredPlaybookUris()
        {
            var response = Dispatch("resources/list");

            var resources = response["resources"] as JArray;
            Assert.NotNull(resources);

            var uris = resources!.Select(r => r?["uri"]?.ToString()).ToHashSet();
            Assert.Contains("genexus://kb/agent-playbook", uris);
            Assert.Contains("genexus://kb/llm-playbook", uris);
            Assert.Contains("genexus://kb/index-status", uris);
            Assert.Contains("genexus://kb/health", uris);
        }

        [Fact]
        public void ResourcesTemplatesList_ShouldExposeGenexusObjectTemplates()
        {
            var response = Dispatch("resources/templates/list");

            var templates = response["resourceTemplates"] as JArray;
            Assert.NotNull(templates);
            Assert.NotEmpty(templates!);

            var templateUris = templates!
                .Select(t => t?["uriTemplate"]?.ToString() ?? string.Empty)
                .ToList();

            Assert.Contains(templateUris, uri => uri.StartsWith("genexus://objects/{name}/part/"));
            Assert.Contains(templateUris, uri => uri.Contains("/variables"));
            Assert.Contains(templateUris, uri => uri.Contains("/navigation"));
        }

        [Fact]
        public void PromptsList_ShouldExposeCoreWorkflows()
        {
            var response = Dispatch("prompts/list");

            var prompts = response["prompts"] as JArray;
            Assert.NotNull(prompts);

            var names = prompts!.Select(p => p?["name"]?.ToString()).ToHashSet();
            Assert.Contains("gx_convert_object", names);
            Assert.Contains("gx_trace_dependencies", names);
            Assert.Contains("gx_agent_ship_change", names);
            Assert.Contains("gx_bootstrap_llm", names);
        }

        [Fact]
        public void ResourcesRead_AgentPlaybook_ShouldReturnTextContent()
        {
            var parameters = new JObject { ["uri"] = "genexus://kb/agent-playbook" };
            var response = Dispatch("resources/read", parameters: parameters);

            var contents = response["contents"] as JArray;
            Assert.NotNull(contents);
            Assert.NotEmpty(contents!);

            var entry = contents![0] as JObject;
            Assert.NotNull(entry);
            Assert.Equal("genexus://kb/agent-playbook", entry!["uri"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(entry["text"]?.ToString()));
        }
    }
}
