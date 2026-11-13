using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The model-evaluation harness (scripts/run-agent-model-eval.py --profile) needs the tools/list a
    // client gets under each tool profile without running the Gateway. These fixtures are produced with
    // the same steps McpRouter uses for tools/list (load tool_definitions.json, apply schema
    // compatibility, order by name, ToolProfileFilter) and are verified here, so they cannot drift.
    // Regenerate with GXMCP_UPDATE_GOLDEN=1, like the other discovery fixtures.
    public class ToolProfileListFixtureTests
    {
        public static readonly string[] Profiles = { "core", "standard", "authoring", "devops", "ui", "db" };

        private static string FindUp(params string[] segments)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate " + string.Join("/", segments));
        }

        private static string DiscoveryDir() =>
            FindUp("src", "GxMcp.Gateway.Tests", "Fixtures", "Contract", "Discovery");

        private static JArray Served(string profile)
        {
            var parsed = JArray.Parse(File.ReadAllText(FindUp("src", "GxMcp.Gateway", "tool_definitions.json")));
            ToolSchemaCompatibility.Apply(parsed);
            var ordered = new JArray(parsed.OfType<JObject>()
                .OrderBy(d => d["name"]?.ToString() ?? string.Empty, StringComparer.Ordinal));
            return ModelVisible(ToolProfileFilter.Filter(ordered, profile));
        }

        // Only what a model sees (and what the harness sends): name, description, inputSchema. outputSchema is
        // dropped because the golden normalizer rewrites volatile property names inside it, and it is unused here.
        private static JArray ModelVisible(JArray tools) => new JArray(tools.OfType<JObject>().Select(t =>
        {
            var visible = new JObject { ["name"] = t["name"], ["description"] = t["description"] };
            if (t["inputSchema"] != null) visible["inputSchema"] = t["inputSchema"];
            return visible;
        }));

        // The golden normalizer rewrites schema properties that are named like volatile fields (id, operationId)
        // to placeholders, so those two properties are excluded from the comparison with the golden only.
        private static JToken WithoutVolatileProperties(JToken tools)
        {
            var copy = tools.DeepClone();
            foreach (var properties in copy.SelectTokens("$..properties").OfType<JObject>().ToList())
            {
                properties.Remove("id");
                properties.Remove("operationId");
            }
            return copy;
        }

        private static JObject Envelope(string profile) => new JObject
        {
            ["profile"] = profile,
            ["tools"] = Served(profile)
        };

        [Fact]
        public void AllProfile_PipelineMatchesTheDiscoveryGolden()
        {
            // Guards the helper above: if it diverges from what the router serves, the profile fixtures are meaningless.
            var golden = JObject.Parse(File.ReadAllText(Path.Combine(DiscoveryDir(), "tools-list.response.json")));
            Assert.True(JToken.DeepEquals(WithoutVolatileProperties(ModelVisible((JArray)golden["tools"]!)), WithoutVolatileProperties(Served("all"))),
                "profile fixture pipeline no longer reproduces tools-list.response.json for profile 'all'.");
        }

        [Theory]
        [InlineData("core")]
        [InlineData("standard")]
        [InlineData("authoring")]
        [InlineData("devops")]
        [InlineData("ui")]
        [InlineData("db")]
        public void ProfileToolsList_MatchesFixture(string profile)
        {
            var actual = Envelope(profile);
            Assert.NotEmpty((JArray)actual["tools"]!);
            var path = Path.Combine(DiscoveryDir(), $"tools-list.{profile}.response.json");

            if (Environment.GetEnvironmentVariable("GXMCP_UPDATE_GOLDEN") == "1")
            {
                // LF endings: the repository stores text fixtures as LF.
                File.WriteAllText(path, actual.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n");
                return;
            }

            var expected = JObject.Parse(File.ReadAllText(path));
            Assert.True(JToken.DeepEquals(expected, actual),
                $"tools-list.{profile}.response.json is stale. Regenerate with GXMCP_UPDATE_GOLDEN=1.");
        }

        [Fact]
        public void EveryProfileIsStrictlySmallerThanAll()
        {
            int all = Served("all").Count;
            Assert.All(Profiles, p => Assert.InRange(Served(p).Count, 1, all - 1));
        }
    }
}
