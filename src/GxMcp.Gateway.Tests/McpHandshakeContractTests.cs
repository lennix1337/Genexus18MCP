using System;
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
    [Collection("Gateway route state")]
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

            Assert.Equal("complete", response["resultType"]?.ToString());
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
        public void ToolsList_AnalyzeExplain_ShouldDeclareCodeArgument()
        {
            var tools = Dispatch("tools/list")["tools"] as JArray;
            Assert.NotNull(tools);

            var analyze = Assert.Single(tools!, tool =>
                string.Equals(tool["name"]?.ToString(), "genexus_analyze", StringComparison.Ordinal));
            Assert.Equal("string", analyze["inputSchema"]?["properties"]?["code"]?["type"]?.ToString());
        }

        [Fact]
        public void ToolsList_LifecycleShouldDeclareCompatibleStructuredOutputSchema()
        {
            var tools = Dispatch("tools/list")["tools"] as JArray;
            Assert.NotNull(tools);

            var lifecycle = Assert.Single(tools!, tool =>
                string.Equals(tool["name"]?.ToString(), "genexus_lifecycle", StringComparison.Ordinal));
            Assert.Equal("object", lifecycle["outputSchema"]?["type"]?.ToString());
            Assert.True(lifecycle["outputSchema"]?["additionalProperties"]?.Value<bool>() == true);
            Assert.Equal("string", lifecycle["outputSchema"]?["properties"]?["status"]?["type"]?.ToString());
        }

        [Theory]
        [InlineData("{Status:'Succeeded',Errors:[]}", null)]
        [InlineData("{Status:'Succeeded',Errors:[],error:null}", null)]
        [InlineData("{Status:'Succeeded',Errors:[],Error:null}", null)]
        [InlineData("{Status:'Running',Errors:[],error:'Diagnostic'}", "Diagnostic")]
        [InlineData("{Status:'Running',Errors:[],Error:'Diagnostic'}", "Diagnostic")]
        public void LifecycleCompactResult_InLeanMode_ConformsToPublishedErrorType(string raw, string? expectedError)
        {
            var previous = Environment.GetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT");
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", "1");
            Program.InvalidateEnvProbeCache();
            try
            {
                var tools = (JArray)Dispatch("tools/list")["tools"]!;
                var lifecycle = Assert.Single(tools, tool => tool["name"]?.ToString() == "genexus_lifecycle");
                Assert.Equal("string", lifecycle["outputSchema"]?["properties"]?["error"]?["type"]?.ToString());

                var compact = LifecycleResponseShaper.Compact(raw, compact: true);
                var response = Program.BuildToolTextResponse(new JValue("1"), JObject.Parse(compact),
                    isError: false, toolName: "genexus_lifecycle");
                var result = (JObject)response["result"]!;
                var structured = Assert.IsType<JObject>(result["structuredContent"]);
                Assert.Equal(structured.ToString(Newtonsoft.Json.Formatting.None), result["content"]![0]!["text"]!.ToString());
                if (expectedError == null)
                    Assert.Null(structured.Property("error"));
                else
                {
                    Assert.Equal(JTokenType.String, structured["error"]!.Type);
                    Assert.Equal(expectedError, structured["error"]!.Value<string>());
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", previous);
                Program.InvalidateEnvProbeCache();
            }
        }

        [Theory]
        [InlineData("Running", null)]
        [InlineData("Completed", null)]
        [InlineData("Cancelled", "")]
        [InlineData("Cancelled", "   ")]
        [InlineData("Cancelled", "Cancelled by client")]
        public void LifecycleOperationStatus_InLeanMode_OmitsEmptyErrorsAndPreservesDiagnostics(string state, string? error)
        {
            var previous = Environment.GetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT");
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", "1");
            Program.InvalidateEnvProbeCache();
            try
            {
                var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
                var operationId = tracker.StartOperation("status-request", "genexus_lifecycle", null, "status-correlation");
                if (state == "Completed")
                    tracker.CompleteFromWorker("status-request", JObject.Parse("{result:{status:'Success'}}"));
                else if (state == "Cancelled")
                    tracker.MarkCancelled(operationId, error ?? string.Empty);

                var response = Program.BuildToolTextResponse(new JValue("1"), tracker.BuildOperationStatus(operationId),
                    isError: false, toolName: "genexus_lifecycle");
                var result = (JObject)response["result"]!;
                var structured = Assert.IsType<JObject>(result["structuredContent"]);
                Assert.Equal(state, structured["status"]?.ToString());
                Assert.Equal(structured.ToString(Newtonsoft.Json.Formatting.None), result["content"]![0]!["text"]!.ToString());
                if (string.IsNullOrWhiteSpace(error))
                    Assert.Null(structured.Property("error"));
                else
                {
                    Assert.Equal(JTokenType.String, structured["error"]!.Type);
                    Assert.Equal(error, structured["error"]!.Value<string>());
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", previous);
                Program.InvalidateEnvProbeCache();
            }
        }

        [Fact]
        public void ToolsList_ShouldBeDeterministicallySortedByName()
        {
            var tools = Dispatch("tools/list")["tools"] as JArray;
            Assert.NotNull(tools);

            var names = tools!.Select(tool => tool["name"]?.ToString() ?? string.Empty).ToList();
            Assert.Equal(names.OrderBy(name => name, System.StringComparer.Ordinal), names);
        }

        [Fact]
        public void ResourcesList_ShouldExposeRequiredPlaybookUris()
        {
            var response = Dispatch("resources/list");

            Assert.Equal("complete", response["resultType"]?.ToString());
            Assert.True(response["ttlMs"]!.Value<int>() > 0);
            Assert.Equal("public", response["cacheScope"]?.ToString());
            var resources = response["resources"] as JArray;
            Assert.NotNull(resources);

            var uris = resources!.Select(r => r?["uri"]?.ToString()).ToHashSet();
            Assert.Contains("genexus://kb/agent-playbook", uris);
            Assert.Contains("genexus://kb/llm-playbook", uris);
            Assert.Contains("genexus://kb/index-status", uris);
            Assert.Contains("genexus://kb/health", uris);
            Assert.Contains("genexus://kb/capabilities", uris);
            Assert.Contains("genexus://kb/skills/nexa", uris);
        }

        [Fact]
        public void ResourcesTemplatesList_ShouldExposeGenexusObjectTemplates()
        {
            var response = Dispatch("resources/templates/list");

            Assert.Equal("complete", response["resultType"]?.ToString());
            Assert.True(response["ttlMs"]!.Value<int>() > 0);
            Assert.Equal("public", response["cacheScope"]?.ToString());
            var templates = response["resourceTemplates"] as JArray;
            Assert.NotNull(templates);
            Assert.NotEmpty(templates!);

            var templateUris = templates!
                .Select(t => t?["uriTemplate"]?.ToString() ?? string.Empty)
                .ToList();

            Assert.Contains(templateUris, uri => uri.StartsWith("genexus://objects/{name}/part/"));
            Assert.Contains(templateUris, uri => uri.Contains("/variables"));
            Assert.Contains(templateUris, uri => uri.Contains("/navigation"));
            Assert.Contains("genexus://kb/skills/nexa/references/{name}", templateUris);
        }

        [Fact]
        public void PromptsList_ShouldExposeCoreWorkflows()
        {
            var response = Dispatch("prompts/list");

            Assert.Equal("complete", response["resultType"]?.ToString());
            Assert.True(response["ttlMs"]!.Value<int>() > 0);
            Assert.Equal("public", response["cacheScope"]?.ToString());
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

            Assert.Equal("complete", response["resultType"]?.ToString());
            Assert.True(response["ttlMs"]!.Value<int>() > 0);
            Assert.Equal("public", response["cacheScope"]?.ToString());
            var contents = response["contents"] as JArray;
            Assert.NotNull(contents);
            Assert.NotEmpty(contents!);

            var entry = contents![0] as JObject;
            Assert.NotNull(entry);
            Assert.Equal("genexus://kb/agent-playbook", entry!["uri"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(entry["text"]?.ToString()));
        }

        [Fact]
        public void ToolResult_ShouldExposeStructuredContentAlongsideText_WhenEnabled()
        {
            // structuredContent is off by default; this test opts in explicitly.
            // The env probe is TTL-cached, so reset it around the change.
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", null);
            Environment.SetEnvironmentVariable("GXMCP_EMIT_STRUCTURED_CONTENT", "1");
            Program.InvalidateEnvProbeCache();
            try
            {
                var response = Program.BuildToolTextResponse(
                    new JValue("1"),
                    new JObject { ["status"] = "ok", ["count"] = 1 },
                    isError: false,
                    toolName: "genexus_read");

                var result = response["result"] as JObject;
                Assert.NotNull(result);
                Assert.Equal("complete", result!["resultType"]?.ToString());
                Assert.Equal("ok", result["structuredContent"]?["status"]?.ToString());
                Assert.Equal("{\"status\":\"ok\",\"count\":1}",
                    result["content"]?[0]?["text"]?.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_EMIT_STRUCTURED_CONTENT", null);
                Program.InvalidateEnvProbeCache();
            }
        }

        [Fact]
        public void ToolResult_ShouldOmitStructuredContent_WhenDisabled()
        {
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", "1");
            // PERF round 3: the env probe is TTL-cached; drop the cache so the
            // freshly-set variable is observed immediately.
            Program.InvalidateEnvProbeCache();
            try
            {
                var response = Program.BuildToolTextResponse(
                    new JValue("1"),
                    new JObject { ["status"] = "ok", ["count"] = 1 },
                    isError: false,
                    toolName: "genexus_read");

                var result = response["result"] as JObject;
                Assert.NotNull(result);
                // text content is always preserved — that's what LLM clients read.
                Assert.Equal("{\"status\":\"ok\",\"count\":1}",
                    result!["content"]?[0]?["text"]?.ToString());
                Assert.Null(result["structuredContent"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", null);
                Program.InvalidateEnvProbeCache();
            }
        }

        [Fact]
        public void LifecycleResult_ShouldKeepStructuredContent_WhenLeanModeIsDisabled()
        {
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", "1");
            Program.InvalidateEnvProbeCache();
            try
            {
                var response = Program.BuildToolTextResponse(
                    new JValue("1"),
                    new JObject { ["status"] = "Ready", ["code"] = "LifecycleStatus" },
                    isError: false,
                    toolName: "genexus_lifecycle");

                var result = response["result"] as JObject;
                Assert.NotNull(result);
                Assert.Equal("Ready", result!["structuredContent"]?["status"]?.ToString());
                Assert.Equal("{\"status\":\"Ready\",\"code\":\"LifecycleStatus\"}",
                    result["content"]?[0]?["text"]?.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", null);
                Program.InvalidateEnvProbeCache();
            }
        }
    }
}
