using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolCallHotPathRegressionTests
    {
        [Fact]
        public void GatewayArgsValidator_PrimeCacheOverwrite_UpdatesCompiledSchemaImmediately()
        {
            GatewayArgsValidator.ClearCache();
            try
            {
                GatewayArgsValidator.PrimeCache("custom_tool", new JObject
                {
                    ["type"] = "object",
                    ["required"] = new JArray("mode"),
                    ["properties"] = new JObject
                    {
                        ["mode"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("alpha", "beta")
                        }
                    }
                });

                Assert.True(GatewayArgsValidator.Validate("custom_tool", new JObject { ["mode"] = "alpha" }).Ok);
                var invalidFirst = GatewayArgsValidator.Validate("custom_tool", new JObject { ["mode"] = "gamma" });
                Assert.False(invalidFirst.Ok);
                Assert.Contains(invalidFirst.Violations, v => v.Path == "mode" && v.Expected == "one of [alpha, beta]");

                // Overwriting the primed schema must recompile enum/required metadata.
                GatewayArgsValidator.PrimeCache("custom_tool", new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["mode"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("gamma")
                        }
                    }
                });

                Assert.True(GatewayArgsValidator.Validate("custom_tool", new JObject { ["mode"] = "gamma" }).Ok);
                Assert.False(GatewayArgsValidator.Validate("custom_tool", new JObject { ["mode"] = "alpha" }).Ok);
            }
            finally
            {
                GatewayArgsValidator.ClearCache();
            }
        }

        [Fact]
        public void McpRouter_FindToolDefinitionAndOutputSchema_O1MapsMatchLegacyAndCanonicalNames()
        {
            var queryDef = McpRouter.FindToolDefinitionForHelp("genexus_query");
            Assert.NotNull(queryDef);
            Assert.Equal("genexus_query", queryDef!["name"]?.ToString());
            Assert.False(Program.ToolDeclaresOutputSchema("genexus_query"));

            var lifecycleDef = McpRouter.FindToolDefinitionForHelp("genexus_lifecycle");
            Assert.NotNull(lifecycleDef);
            Assert.Equal(lifecycleDef!["outputSchema"] != null, Program.ToolDeclaresOutputSchema("genexus_lifecycle"));

            // Legacy alias resolution still resolves to canonical definition.
            var aliasDef = McpRouter.FindToolDefinitionForHelp("genexus_smoke_test");
            Assert.NotNull(aliasDef);
            Assert.Equal("genexus_browser", aliasDef!["name"]?.ToString());
            Assert.False(Program.ToolDeclaresOutputSchema("genexus_nonexistent_tool"));
            Assert.Null(McpRouter.FindToolDefinitionForHelp("genexus_nonexistent_tool"));
        }

        [Fact]
        public void DidYouMean_Suggest_PreservesPrefixStemSynonymAndReadVerbRules()
        {
            var candidates = new[] { "objectName", "maxResults", "get_visual", "mode" };

            Assert.Equal("objectName", DidYouMean.Suggest("objects", candidates));
            Assert.Equal("maxResults", DidYouMean.Suggest("limit", candidates));
            Assert.Equal("get_visual", DidYouMean.Suggest("read", candidates));
            Assert.Equal("mode", DidYouMean.Suggest("mod", candidates));
            Assert.Null(DidYouMean.Suggest("completely_unrelated_xyz", candidates));
        }
    }
}
