using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ServerInstructionsTests
    {
        private static readonly Regex ToolName = new Regex(@"\bgenexus_[a-z0-9_]+\b", RegexOptions.CultureInvariant);

        private static string[] DeclaredTools() =>
            JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tool_definitions.json")))
                .Select(tool => tool["name"]!.ToString())
                .ToArray();

        [Fact]
        public void EveryRoutedToolIsDeclared()
        {
            var declared = DeclaredTools();
            foreach (var tool in ServerInstructions.ReferencedTools)
                Assert.Contains(tool, declared);
        }

        [Fact]
        public void EveryToolNamedInTheTextIsListedByItsFragment()
        {
            var mentioned = ToolName.Matches(ServerInstructions.Build("all")).Select(m => m.Value).Distinct();
            Assert.All(mentioned, tool => Assert.Contains(tool, ServerInstructions.ReferencedTools));
        }

        [Theory]
        [InlineData("all")]
        [InlineData("core")]
        [InlineData("standard")]
        [InlineData("authoring")]
        [InlineData("devops")]
        [InlineData("ui")]
        [InlineData("db")]
        [InlineData("core+db")]
        public void InstructionsOnlyRouteToToolsTheProfileExposes(string profile)
        {
            string text = ServerInstructions.Build(profile);

            Assert.StartsWith("GeneXus Knowledge Base MCP server.", text);
            Assert.Contains("genexus_whoami", text);
            Assert.Contains("genexus://kb/tool-help/<tool>", text);
            foreach (Match match in ToolName.Matches(text))
                Assert.True(ToolProfileFilter.IsToolExposed(profile, match.Value), $"'{match.Value}' is not exposed by profile '{profile}'.");
            Assert.DoesNotContain(",\n", text);
        }

        [Fact]
        public void CoreProfileOmitsAuthoringRoutesButKeepsReadRoutes()
        {
            string core = ServerInstructions.Build("core");

            Assert.Contains("genexus_edit", core);
            Assert.Contains("genexus_read", core);
            Assert.DoesNotContain("genexus_variable", core);
            Assert.DoesNotContain("genexus_apply_pattern", core);
            Assert.Contains("genexus_variable", ServerInstructions.Build("authoring"));
        }

        [Fact]
        public void InitializeAndServerDiscoverPublishTheRoutingGuide()
        {
            var initialize = JObject.FromObject(McpRouter.Handle(JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"initialize","params":{"protocolVersion":"2025-11-25"}}"""))!);
            var discover = JObject.FromObject(McpRouter.Handle(JObject.Parse(
                """{"jsonrpc":"2.0","id":"2","method":"server/discover"}"""))!);

            string expected = ServerInstructions.ForActiveProfile();
            Assert.Equal(expected, initialize["instructions"]?.ToString());
            Assert.Equal(expected, discover["instructions"]?.ToString());
        }
    }
}
