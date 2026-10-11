using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolProfileTests
    {
        private static JArray CreateSampleTools()
        {
            var tools = new List<string>
            {
                "genexus_whoami",
                "genexus_query",
                "genexus_read",
                "genexus_edit",
                "genexus_create",
                "genexus_structure",
                "genexus_variable",
                "genexus_layout",
                "genexus_wwp",
                "genexus_db",
                "genexus_data_view",
                "genexus_lifecycle",
                "genexus_test",
                "genexus_gxserver",
                "genexus_deploy"
            };

            var arr = new JArray();
            foreach (var t in tools)
            {
                arr.Add(new JObject { ["name"] = t, ["description"] = $"Description for {t}" });
            }
            return arr;
        }

        [Theory]
        [InlineData(null, 15)]
        [InlineData("", 15)]
        [InlineData("all", 15)]
        [InlineData("ALL", 15)]
        public void Filter_AllOrNull_ReturnsAllTools(string? profile, int expectedCount)
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, profile);
            Assert.Equal(expectedCount, filtered.Count);
        }

        [Fact]
        public void Filter_CoreProfile_ReturnsOnlyCoreTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "core");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_whoami", names);
            Assert.Contains("genexus_query", names);
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_edit", names);
            Assert.Contains("genexus_lifecycle", names);

            Assert.DoesNotContain("genexus_create", names);
            Assert.DoesNotContain("genexus_db", names);
            Assert.DoesNotContain("genexus_layout", names);
            Assert.DoesNotContain("genexus_gxserver", names);
        }

        [Fact]
        public void Filter_AuthoringProfile_IncludesCoreAndAuthoringTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "authoring");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_create", names);
            Assert.Contains("genexus_structure", names);
            Assert.Contains("genexus_variable", names);

            Assert.DoesNotContain("genexus_gxserver", names);
            Assert.DoesNotContain("genexus_deploy", names);
        }

        [Fact]
        public void Filter_DevOpsProfile_IncludesDevopsTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "devops");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_lifecycle", names);
            Assert.Contains("genexus_test", names);
            Assert.Contains("genexus_gxserver", names);
            Assert.Contains("genexus_deploy", names);

            Assert.DoesNotContain("genexus_create", names);
            Assert.DoesNotContain("genexus_layout", names);
        }

        [Fact]
        public void Filter_UIProfile_IncludesUITools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "ui");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_layout", names);
            Assert.Contains("genexus_wwp", names);

            Assert.DoesNotContain("genexus_deploy", names);
            Assert.DoesNotContain("genexus_gxserver", names);
        }

        [Fact]
        public void Filter_DbProfile_IncludesDbTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "db");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_db", names);
            Assert.Contains("genexus_data_view", names);

            Assert.DoesNotContain("genexus_layout", names);
            Assert.DoesNotContain("genexus_wwp", names);
        }

        [Fact]
        public void Filter_CompositeProfile_UnionsToolsFromBothProfiles()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "ui, db");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_layout", names);
            Assert.Contains("genexus_db", names);
            Assert.Contains("genexus_data_view", names);
            Assert.Contains("genexus_wwp", names);

            Assert.DoesNotContain("genexus_deploy", names);
        }
        [Fact]
        public void Filter_NamedCapabilityProfilesResolveToExpectedSets()
        {
            var tools = CreateSampleTools();
            var exploration = ToolProfileFilter.Filter(tools, "exploration");
            var safeEdit = ToolProfileFilter.Filter(tools, "safe-edit");
            var build = ToolProfileFilter.Filter(tools, "build");
            var versioning = ToolProfileFilter.Filter(tools, "versioning");
            var deploy = ToolProfileFilter.Filter(tools, "deploy");

            Assert.DoesNotContain("genexus_create", exploration.Select(t => t["name"]?.ToString()));
            Assert.Contains("genexus_create", safeEdit.Select(t => t["name"]?.ToString()));
            Assert.Contains("genexus_test", build.Select(t => t["name"]?.ToString()));
            Assert.Contains("genexus_gxserver", versioning.Select(t => t["name"]?.ToString()));
            Assert.Contains("genexus_deploy", deploy.Select(t => t["name"]?.ToString()));
        }

        [Fact]
        public void Filter_CompositeCapabilityProfilesCanBeCombined()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "exploration+safe-edit");
            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();

            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_create", names);
            Assert.DoesNotContain("genexus_deploy", names);
        }

        [Fact]
        public void Filter_UnknownProfileFailsOpenForBackwardCompatibility()
        {
            var tools = CreateSampleTools();
            Assert.Equal(tools.Count, ToolProfileFilter.Filter(tools, "unknown-profile").Count);
        }

        [Fact]
        public void Filter_CompactsSchemasWithoutMutatingTheSourceDefinitions()
        {
            const string longDescription = "A long property description that exceeds the compact list limit and stays available in tool help.";
            var tool = new JObject
            {
                ["name"] = "genexus_example",
                ["description"] = longDescription,
                ["inputSchema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["field"] = new JObject { ["type"] = "string", ["description"] = longDescription }
                    },
                    ["examples"] = new JArray(new JObject { ["field"] = "example" })
                }
            };
            var tools = new JArray(tool);

            var filtered = ToolProfileFilter.Filter(tools, "all");

            Assert.Equal(longDescription + " Full guidance: genexus://kb/tool-help/genexus_example", filtered[0]!["description"]!.ToString());
            Assert.Null(filtered[0]!["inputSchema"]!["examples"]);
            Assert.True(filtered[0]!["inputSchema"]!["properties"]!["field"]!["description"]!.ToString().Length <= 40);
            Assert.Equal(longDescription, tools[0]!["description"]!.ToString());
            Assert.Equal(longDescription, tools[0]!["inputSchema"]!["properties"]!["field"]!["description"]!.ToString());
            Assert.NotNull(tools[0]!["inputSchema"]!["examples"]);

            string help = ToolHelpCatalog.Get("genexus_example", (JObject)tool.DeepClone())!;
            Assert.Contains(longDescription, help);
            Assert.Contains("\"examples\"", help);
        }

        [Fact]
        public void ToolSummary_KeepsWholeSentencesThatFitTheBudget()
        {
            string first = "Read object code and metadata.";
            string second = "Omitting part returns the complete object in one call, tailored to the object type, with a version token for writes.";
            string third = new string('x', 300);

            string summary = ToolProfileFilter.BuildToolSummary(first + " " + second + " " + third);

            Assert.Equal(first + " " + second, summary);
        }

        [Fact]
        public void ToolSummary_DoesNotSplitOnAbbreviationsOrInsideParentheses()
        {
            string description = "Apply a pattern (e.g. WorkWithPlus. Or another) to a parent object so the IDE behaviour is mirrored exactly. "
                + string.Join(" ", Enumerable.Repeat("filler", 60));

            string summary = ToolProfileFilter.BuildToolSummary(description);

            Assert.StartsWith("Apply a pattern (e.g. WorkWithPlus. Or another) to a parent object so the IDE behaviour is mirrored exactly.", summary);
            Assert.True(summary.Length <= ToolProfileFilter.MaxToolSummaryLength + 1);
        }

        [Fact]
        public void ToolSummary_TruncatesAtAWordBoundaryWhenNoSentenceFits()
        {
            string description = "Creation. " + string.Join(" ", Enumerable.Repeat("action=object|popup|template", 20));

            string summary = ToolProfileFilter.BuildToolSummary(description);

            Assert.EndsWith("\u2026", summary);
            Assert.StartsWith("Creation. action=object|popup|template", summary);
            Assert.True(summary.Length <= ToolProfileFilter.MaxToolSummaryLength + 1);
            Assert.DoesNotContain("  ", summary);
        }

        [Fact]
        public void ToolSummary_DropsTheInlineToolHelpPointerBecauseTheSuffixCarriesIt()
        {
            string description = "Search objects in active KB. See genexus://kb/tool-help/genexus_query. Requires a Ready index.";

            Assert.Equal(
                "Search objects in active KB. Requires a Ready index. Full guidance: genexus://kb/tool-help/genexus_query",
                ToolProfileFilter.BuildCompactToolDescription("genexus_query", description));
        }

        [Fact]
        public void PublishedToolDescriptionsSayWhatEachToolIsFor()
        {
            var tools = JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tool_definitions.json")));

            foreach (JObject tool in ToolProfileFilter.Filter(tools, "all").OfType<JObject>())
            {
                string name = tool["name"]!.ToString();
                string description = tool["description"]!.ToString();
                string pointer = " Full guidance: genexus://kb/tool-help/" + name;
                Assert.EndsWith(pointer, description);
                string summary = description.Substring(0, description.Length - pointer.Length);
                Assert.True(summary.Length >= 20, $"{name} publishes no purpose summary: '{description}'.");
                Assert.True(summary.Length <= ToolProfileFilter.MaxToolSummaryLength + 1, $"{name} summary is {summary.Length} chars.");
            }
        }

        [Fact]
        public void Filter_StandardProfileIncludesCoreAndCommonAuthoringTools()
        {
            var tools = CreateSampleTools();
            tools.Add(new JObject { ["name"] = "genexus_properties" });
            tools.Add(new JObject { ["name"] = "genexus_io" });
            tools.Add(new JObject { ["name"] = "genexus_gxserver" });

            var names = ToolProfileFilter.Filter(tools, "standard")
                .Select(t => t["name"]?.ToString())
                .ToHashSet();

            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_create", names);
            Assert.Contains("genexus_structure", names);
            Assert.Contains("genexus_variable", names);
            Assert.Contains("genexus_properties", names);
            Assert.Contains("genexus_io", names);
            Assert.DoesNotContain("genexus_gxserver", names);
        }

        [Fact]
        public void ToolOutsideActiveProfileReturnsTypedActionableError()
        {
            var error = ToolProfileFilter.GetToolNotInProfileError("standard", "genexus_db");

            Assert.NotNull(error);
            Assert.Equal("ToolNotInProfile", error!["error"]?["code"]?.ToString());
            Assert.Contains("db", error["error"]?["availableProfiles"]?.ToObject<string[]>() ?? System.Array.Empty<string>());
            Assert.Contains("Server.ToolProfile", error["error"]?["hint"]?.ToString());
            Assert.Contains("GXMCP_PROFILE", error["error"]?["hint"]?.ToString());
        }
    }
}
