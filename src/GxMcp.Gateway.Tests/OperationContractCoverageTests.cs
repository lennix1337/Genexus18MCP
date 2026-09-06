using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Keeps the executable operation policy aligned with the published tool
    /// contract.  An action added to tool_definitions.json without a policy is
    /// unsafe to cache/retry and must fail this guard before discovery ships.
    /// </summary>
    public sealed class OperationContractCoverageTests
    {
        [Fact]
        public void EveryPublishedActionHasAnExplicitPolicy()
        {
            var missing = ToolIdentity.CanonicalToolNames
                .SelectMany(tool => ToolIdentity.ActionsFor(tool)
                    .Where(action => OperationClassifier.ClassifyAction(tool, action)
                        == OperationClassifier.OperationKind.Unknown)
                    .Select(action => tool + ":" + action))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            Assert.True(missing.Length == 0,
                "Published actions without an explicit read/write policy: "
                + string.Join(", ", missing));
        }

        [Fact]
        public void EveryPublishedActionlessToolHasAnExplicitPolicy()
        {
            var missing = ToolIdentity.CanonicalToolNames
                .Where(tool => ToolIdentity.ActionsFor(tool).Count == 0)
                .Where(tool => OperationClassifier.ClassifyTool(tool, new JObject())
                    == OperationClassifier.OperationKind.Unknown)
                .OrderBy(tool => tool, StringComparer.Ordinal)
                .ToArray();

            Assert.True(missing.Length == 0,
                "Published actionless tools without an explicit policy: "
                + string.Join(", ", missing));
        }

        [Fact]
        public void UnknownToolNamesDoNotBecomeMutationsBySubstring()
        {
            var unknown = new JObject();
            Assert.Equal(OperationClassifier.OperationKind.Unknown,
                OperationClassifier.ClassifyTool("genexus_editor_preview", unknown));
            Assert.False(Program.IsMutatingTool("genexus_editor_preview", unknown));
            Assert.Equal(OperationClassifier.OperationKind.Unknown,
                OperationClassifier.Describe("genexus_editor_preview", unknown).Kind);
        }

        [Fact]
        public void HelpCatalogKeysAreCanonicalOrKnownAliases()
        {
            var unknown = ToolHelpCatalog.KnownTools
                .Where(name => !ToolIdentity.IsKnownTool(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.True(unknown.Length == 0,
                "Help entries reference unknown tools: " + string.Join(", ", unknown));
        }

        [Theory]
        [InlineData("genexus_create_object", "genexus_create")]
        [InlineData("genexus_db_optimize", "genexus_db")]
        [InlineData("genexus_smoke_test", "genexus_browser")]
        public void LegacyAliasesResolveToThePublishedCanonicalTool(string alias, string canonical)
        {
            Assert.True(ToolIdentity.IsKnownTool(alias));
            Assert.Equal(canonical, ToolIdentity.ResolveCanonical(alias));
        }
    }
}
