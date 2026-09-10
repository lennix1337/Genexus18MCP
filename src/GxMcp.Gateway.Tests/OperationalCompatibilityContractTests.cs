using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class OperationalCompatibilityContractTests
    {
        private static JObject FindTool(string name)
        {
            string directory = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                string direct = Path.Combine(directory, "tool_definitions.json");
                if (File.Exists(direct))
                    return JArray.Parse(File.ReadAllText(direct))
                        .OfType<JObject>().Single(tool => tool["name"]?.ToString() == name);

                string source = Path.Combine(directory, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(source))
                    return JArray.Parse(File.ReadAllText(source))
                        .OfType<JObject>().Single(tool => tool["name"]?.ToString() == name);

                DirectoryInfo? parent = Directory.GetParent(directory);
                if (parent == null) break;
                directory = parent.FullName;
            }

            throw new FileNotFoundException("Could not locate tool_definitions.json.");
        }

        private static string FindRepositoryFile(string relativePath)
        {
            string directory = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                string candidate = Path.Combine(directory, relativePath);
                if (File.Exists(candidate))
                    return candidate;

                DirectoryInfo? parent = Directory.GetParent(directory);
                if (parent == null) break;
                directory = parent.FullName;
            }

            throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
        }

        [Fact]
        public void ApiRouteWritesKeepVersionTokenAlias()
        {
            JObject properties = (JObject)FindTool("genexus_api")["inputSchema"]!["properties"]!;
            Assert.NotNull(properties["expectedVersion"]);
            Assert.NotNull(properties["versionToken"]);
        }

        [Fact]
        public void WorkWithPlusPublishesTypedTabAndGridContracts()
        {
            JObject schema = (JObject)FindTool("genexus_wwp")["inputSchema"]!;
            var actions = ((JArray)schema["properties"]!["action"]!["enum"]!)
                .Select(value => value.ToString()).ToArray();

            Assert.Contains("add_tab", actions);
            Assert.Contains("move_tab", actions);
            Assert.Contains("remove_tab", actions);
            Assert.Contains("add_grid_attribute", actions);
            Assert.NotNull(schema["properties"]!["baseVersion"]);
            Assert.NotNull(schema["properties"]!["expectedVersion"]);
            Assert.NotNull(schema["properties"]!["versionToken"]);

            JObject control = (JObject)schema["$defs"]!["wwpControl"]!;
            Assert.Equal("type", control["required"]![0]!.ToString());
            Assert.Equal(new[] { "variable", "userAction", "table" },
                ((JArray)control["properties"]!["type"]!["enum"]!).Select(value => value.ToString()));
        }

        [Theory]
        [InlineData("settings_templates")]
        [InlineData("settings_read")]
        [InlineData("settings_edit")]
        [InlineData("list")]
        [InlineData("add_action")]
        public void WorkWithPlusPublishedNameReachesWorkerTarget(string action)
        {
            var args = new JObject
            {
                ["action"] = action, ["name"] = "WorkWithPlus",
                ["dryRun"] = true, ["template"] = "Transaction",
                ["offset"] = 12, ["limit"] = 8,
                ["baseVersion"] = "base", ["expectedVersion"] = "expected", ["versionToken"] = "token"
            };
            AssertWorkWithPlusRoute(args, "WorkWithPlus");
        }

        [Theory]
        [InlineData("guid", "11111111-2222-3333-4444-555555555555", false)]
        [InlineData("entityKey", "11111111-2222-3333-4444-555555555555-1", false)]
        [InlineData("guid", "11111111-2222-3333-4444-555555555555", true)]
        [InlineData("entityKey", "11111111-2222-3333-4444-555555555555-1", true)]
        public void WorkWithPlusKeepsExplicitIdentityForTypedWorkerResolution(string identity, string value, bool withName)
        {
            var args = new JObject { ["action"] = "settings_templates", [identity] = value };
            if (withName) args["name"] = "WorkWithPlus";
            AssertWorkWithPlusRoute(args, withName ? "WorkWithPlus" : null);
        }

        private static void AssertWorkWithPlusRoute(JObject args, string? expectedTarget)
        {
            // Exercise the published schema and the real tools/call entry point, not only a module router.
            JObject schema = (JObject)FindTool("genexus_wwp")["inputSchema"]!;
            Assert.Equal("string", schema["properties"]!["name"]!["type"]!.ToString());
            GatewayArgsValidator.PrimeCache("genexus_wwp", schema);
            Assert.True(GatewayArgsValidator.Validate("genexus_wwp", args).Ok);
            var request = new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "wwp-routing", ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "genexus_wwp", ["arguments"] = args }
            };
            var original = request.DeepClone();

            var routed = JObject.FromObject(McpRouter.ConvertToolCall(request)!);

            Assert.Equal("WwpAction", (string?)routed["module"]);
            Assert.Equal("Run", (string?)routed["action"]);
            Assert.Equal(expectedTarget, (string?)routed["target"]);
            Assert.True(JToken.DeepEquals(args, routed["params"]));
            Assert.True(JToken.DeepEquals(original, request));
        }

        [Fact]
        public void RecipeDoesNotAdvertiseUnsupportedRunAction()
        {
            var actions = ((JArray)FindTool("genexus_recipe")["inputSchema"]!["properties"]!["action"]!["enum"]!)
                .Select(value => value.ToString());
            Assert.DoesNotContain("run", actions);
        }

        [Fact]
        public void ExplainDocumentationAdvertisesCompatibilityOnlyContract()
        {
            JObject analyze = FindTool("genexus_analyze");
            string description = analyze["description"]?.ToString() ?? "";
            string codeDescription = analyze["inputSchema"]?["properties"]?["code"]?["description"]?.ToString() ?? "";

            Assert.Contains("compatibility-only", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NotImplemented", description);
            Assert.Contains("legacy response envelope", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("summary", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("context", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("genexus_read", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("compatibility-only", codeDescription, StringComparison.OrdinalIgnoreCase);

            foreach (string relativePath in new[]
            {
                "README.md",
                "GEMINI.md",
                ".gemini/skills/genexus-mastery/SKILL.md"
            })
            {
                string documentation = File.ReadAllText(FindRepositoryFile(relativePath));
                Assert.Contains("compatibility-only", documentation, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("NotImplemented", documentation);
                Assert.Contains("genexus_read", documentation, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Issue146Documentation_PublishesContextLeaseAndHttpTokenContract()
        {
            string isolation = File.ReadAllText(FindRepositoryFile("docs/kb-isolation-contract.md"));
            string playbook = File.ReadAllText(FindRepositoryFile("docs/llm_cli_mcp_playbook.md"));
            string inventory = File.ReadAllText(FindRepositoryFile("docs/mcp_capabilities_inventory.md"));

            foreach (string documentation in new[] { isolation, playbook, inventory })
            {
                Assert.Contains("local-friendly", documentation, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("hardened", documentation, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("ResolutionPolicy", documentation);
                Assert.Contains("KB_NOT_OWNED", documentation);
                Assert.Contains("KB_LOCKED", documentation);
                Assert.Contains("KB_LEASE_INVALID", documentation);
                Assert.Contains("KB_LEASE_EXPIRED", documentation);
                Assert.Contains("GXMCP_HTTP_TOKEN", documentation);
                Assert.Contains("select", documentation, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("close", documentation, StringComparison.OrdinalIgnoreCase);
            }

            string kbHelp = ToolHelpCatalog.Get("genexus_kb")!;
            Assert.Contains("select", kbHelp, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("close", kbHelp, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("KB_NOT_OWNED", kbHelp);
            Assert.Contains("KB_SESSION_UNAVAILABLE", kbHelp);
            Assert.Contains("GXMCP_HTTP_TOKEN", kbHelp);
        }

        [Fact]
        public void Issue146ReviewedToolContracts_MatchTheirActualExecutionBoundaries()
        {
            JObject editAndBuild = FindTool("genexus_edit_and_build");
            JObject editSchema = (JObject)editAndBuild["inputSchema"]!;
            Assert.Contains("name", editSchema["required"]!.Values<string>());
            Assert.Contains("part", editSchema["required"]!.Values<string>());
            Assert.Contains("pollTarget", editAndBuild["description"]!.ToString());
            Assert.Contains("rollbackOnFailure", editAndBuild["description"]!.ToString());

            JObject probe = FindTool("genexus_sdk_probe");
            var probeModes = ((JArray)probe["inputSchema"]!["properties"]!["mode"]!["enum"]!)
                .Values<string>().ToArray();
            Assert.Equal(new[] { "surface", "capabilities" }, probeModes);
            Assert.Contains("read-only", probe["inputSchema"]!["properties"]!["mode"]!["description"]!.ToString(), StringComparison.OrdinalIgnoreCase);

            JObject recovery = FindTool("genexus_connection_recover");
            JObject recoveryProps = (JObject)recovery["inputSchema"]!["properties"]!;
            Assert.Equal("boolean", recoveryProps["force"]!["type"]!.ToString());
            Assert.Contains("every open worker", recovery["description"]!.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("force=true", recovery["description"]!.ToString(), StringComparison.OrdinalIgnoreCase);

            foreach (string tool in new[] { "genexus_edit_and_build", "genexus_sdk_probe", "genexus_connection_recover" })
            {
                string help = ToolHelpCatalog.Get(tool)!;
                Assert.Contains("KB_NOT_OWNED", help);
                Assert.Contains("KB_LEASE_INVALID", help);
                Assert.Contains("KB_LEASE_EXPIRED", help);
            }
        }
    }
}
