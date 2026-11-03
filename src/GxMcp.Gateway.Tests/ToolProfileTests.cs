using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.TestSupport;
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
            var tools = LoadCanonicalToolDefinitions();

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

        /// <summary>
        /// #830 item 6b: <c>genexus_kb_version</c> (action=list among them) is hidden from the
        /// standard profile that generated configs use. The docs name the profiles that expose it;
        /// this pins that contract so the references cannot drift from the profile lists.
        /// </summary>
        [Fact]
        public void KbVersion_IsExposedByDevopsAndAllOnly_AndTheHintNamesDevops()
        {
            Assert.Null(ToolProfileFilter.GetToolNotInProfileError("devops", "genexus_kb_version"));
            Assert.Null(ToolProfileFilter.GetToolNotInProfileError("all", "genexus_kb_version"));

            var error = ToolProfileFilter.GetToolNotInProfileError("standard", "genexus_kb_version");
            Assert.NotNull(error);
            Assert.Equal(new[] { "devops" }, error!["error"]!["availableProfiles"]!.ToObject<string[]>());
            Assert.Contains("'devops'", error["error"]!["hint"]!.ToString());
        }

        private static readonly string[] DocumentedProfiles = new[]
        {
            "standard",
            "core",
            "authoring",
            "devops",
            "ui",
            "db",
            "all"
        };

        private static JArray LoadCanonicalToolDefinitions()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside))
            {
                return JArray.Parse(File.ReadAllText(beside));
            }

            return JArray.Parse(RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
        }

        [Fact]
        public void PlaybookToolProfilesTable_MatchesCodeAllowlists()
        {
            var tools = LoadCanonicalToolDefinitions();
            string playbook = RepoSource.Read("docs", "llm_cli_mcp_playbook.md");

            const string sectionHeading = "### Tool profiles and schema budgets";
            int sectionIndex = playbook.IndexOf(sectionHeading, StringComparison.Ordinal);
            Assert.True(sectionIndex >= 0, $"Section '{sectionHeading}' was not found in docs/llm_cli_mcp_playbook.md.");

            string sectionText = playbook.Substring(sectionIndex + sectionHeading.Length);
            var nextHeadingMatch = Regex.Match(sectionText, @"\n#{1,3}\s+");
            if (nextHeadingMatch.Success)
            {
                sectionText = sectionText.Substring(0, nextHeadingMatch.Index);
            }

            string[] lines = sectionText.Replace("\r\n", "\n").Split('\n');
            var parsedRows = new List<(string Profile, int Count)>();

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("|", StringComparison.Ordinal)) continue;

                // Skip markdown table header and divider lines
                if (line.StartsWith("| Profile", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("|---", StringComparison.Ordinal))
                {
                    continue;
                }

                var match = Regex.Match(line, @"^\|\s*`([^`]+)`\s*\|\s*(\d+)\s*\|");
                Assert.True(match.Success, $"Unrecognized table row format in tool profiles section: '{line}'");

                string profile = match.Groups[1].Value;
                int count = int.Parse(match.Groups[2].Value);
                parsedRows.Add((profile, count));
            }

            Assert.True(
                parsedRows.Count == DocumentedProfiles.Length,
                $"Expected exactly {DocumentedProfiles.Length} tool profile rows in playbook, found {parsedRows.Count}.");

            for (int i = 0; i < DocumentedProfiles.Length; i++)
            {
                Assert.True(
                    string.Equals(DocumentedProfiles[i], parsedRows[i].Profile, StringComparison.OrdinalIgnoreCase),
                    $"Expected row {i + 1} to be profile '{DocumentedProfiles[i]}', but found '{parsedRows[i].Profile}'.");
            }

            foreach (var (profile, publishedCount) in parsedRows)
            {
                int actualCount = ToolProfileFilter.Filter(tools, profile).Count;
                Assert.True(
                    publishedCount == actualCount,
                    $"Tool count for profile '{profile}' in playbook is {publishedCount}, but code allowlist yields {actualCount}.");
            }
        }

        [Fact]
        public void EnvironmentVariables_ProfileDescription_MatchesCodeAllowlists()
        {
            var tools = LoadCanonicalToolDefinitions();
            string envDoc = RepoSource.Read("docs", "environment_variables.md");

            string[] lines = envDoc.Replace("\r\n", "\n").Split('\n');
            string? profileLine = lines.FirstOrDefault(l => l.Contains("| `GXMCP_PROFILE` |"));
            Assert.True(profileLine != null, "Row for `GXMCP_PROFILE` not found in docs/environment_variables.md.");

            // Verify the 7-profile enumeration: (`standard`, `core`, `authoring`, `devops`, `ui`, `db`, `all`)
            var enumMatch = Regex.Match(profileLine!, @"surface\s*\(([^)]+)\)");
            Assert.True(enumMatch.Success, $"Could not find profile enumeration '( ... )' in GXMCP_PROFILE row: '{profileLine}'");

            var enumProfiles = Regex.Matches(enumMatch.Groups[1].Value, @"`([a-z_]+)`")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.True(
                enumProfiles.Count == DocumentedProfiles.Length,
                $"Expected {DocumentedProfiles.Length} profiles in GXMCP_PROFILE enumeration, found {enumProfiles.Count}: [{string.Join(", ", enumProfiles)}]");

            var missingProfiles = DocumentedProfiles.Except(enumProfiles, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(
                missingProfiles.Count == 0,
                $"GXMCP_PROFILE enumeration is missing profile(s): [{string.Join(", ", missingProfiles)}]");

            // Verify core tools count: `core` (15 tools)
            var coreMatch = Regex.Match(profileLine!, @"`core`\s*\((\d+)\s+tools\)");
            Assert.True(coreMatch.Success, $"Could not find '`core` (N tools)' pattern in GXMCP_PROFILE row: '{profileLine}'");
            int publishedCoreCount = int.Parse(coreMatch.Groups[1].Value);
            int actualCoreCount = ToolProfileFilter.Filter(tools, "core").Count;
            Assert.True(
                publishedCoreCount == actualCoreCount,
                $"`core` count in environment_variables.md is {publishedCoreCount}, but code allowlist yields {actualCoreCount}.");

            // Verify authoring tools count: `authoring` (35 tools)
            var authMatch = Regex.Match(profileLine!, @"`authoring`\s*\((\d+)\s+tools\)");
            Assert.True(authMatch.Success, $"Could not find '`authoring` (N tools)' pattern in GXMCP_PROFILE row: '{profileLine}'");
            int publishedAuthCount = int.Parse(authMatch.Groups[1].Value);
            int actualAuthCount = ToolProfileFilter.Filter(tools, "authoring").Count;
            Assert.True(
                publishedAuthCount == actualAuthCount,
                $"`authoring` count in environment_variables.md is {publishedAuthCount}, but code allowlist yields {actualAuthCount}.");

            // Verify all / full surface count: full 60-tool surface
            var allMatch = Regex.Match(profileLine!, @"full\s+(\d+)-tool\s+surface");
            Assert.True(allMatch.Success, $"Could not find 'full N-tool surface' pattern in GXMCP_PROFILE row: '{profileLine}'");
            int publishedAllCount = int.Parse(allMatch.Groups[1].Value);
            int actualAllCount = ToolProfileFilter.Filter(tools, "all").Count;
            Assert.True(
                publishedAllCount == actualAllCount,
                $"Full surface tool count in environment_variables.md is {publishedAllCount}, but code allowlist yields {actualAllCount}.");
        }
    }
}
