using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolProfileCompactionRegressionTests
    {
        [Fact]
        public void GetOrCreateFiltered_AllProfile_CachesPerSourceAndRespectsInvalidate()
        {
            var sourceA = new JArray
            {
                new JObject
                {
                    ["name"] = "genexus_query",
                    ["description"] = "Search KB objects.",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["query"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Search   text\twith   extra whitespace   beyond forty characters total length",
                                ["examples"] = new JArray("Customer")
                            }
                        }
                    }
                }
            };
            var sourceB = new JArray
            {
                new JObject
                {
                    ["name"] = "genexus_read",
                    ["description"] = "Read object parts.",
                    ["inputSchema"] = new JObject { ["type"] = "object" }
                }
            };

            ToolProfileFilter.InvalidateCache();
            var firstA = ToolProfileFilter.GetOrCreateFiltered(sourceA, "all");
            var secondA = ToolProfileFilter.GetOrCreateFiltered(sourceA, "");
            Assert.Same(firstA, secondA);

            // Different source JArray must not collide with sourceA's cached entry.
            var firstB = ToolProfileFilter.GetOrCreateFiltered(sourceB, "all");
            Assert.NotSame(firstA, firstB);
            Assert.Equal("genexus_read", firstB[0]["name"]?.ToString());

            // InvalidateCache forces recompaction.
            ToolProfileFilter.InvalidateCache();
            var afterInvalidate = ToolProfileFilter.GetOrCreateFiltered(sourceA, "all");
            Assert.NotSame(firstA, afterInvalidate);

            // Source JArray was never mutated (examples still present on source, removed on compacted).
            Assert.NotNull(sourceA[0]["inputSchema"]?["properties"]?["query"]?["examples"]);
            Assert.Null(afterInvalidate[0]["inputSchema"]?["properties"]?["query"]?["examples"]);
        }

        [Fact]
        public void Compact_DescriptionAndAdvancedInputs_HandlesBoundaryCasesWithoutMutatingSource()
        {
            string exact40 = new string('a', 40);
            string exact40WithTrailingWs = "  " + exact40 + "\t\n ";
            string spaceAt39ThenMore = new string('b', 39) + " extra";
            string noSpace45 = new string('c', 45);

            var tools = new JArray
            {
                new JObject
                {
                    ["name"] = "genexus_edit",
                    ["description"] = "Edit object parts.   See genexus://kb/tool-help/genexus_edit for details.",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["pExact40"] = new JObject { ["type"] = "string", ["description"] = exact40 },
                            ["pExact40Ws"] = new JObject { ["type"] = "string", ["description"] = exact40WithTrailingWs },
                            ["pSpaceAt39"] = new JObject { ["type"] = "string", ["description"] = spaceAt39ThenMore },
                            ["pNoSpace45"] = new JObject { ["type"] = "string", ["description"] = noSpace45 },
                            ["pWhitespaceOnly"] = new JObject { ["type"] = "string", ["description"] = "   \t\r\n  " },
                            ["forceWrite"] = new JObject { ["type"] = "boolean", ["description"] = "Advanced override flag." }
                        }
                    }
                }
            };

            var coreFiltered = ToolProfileFilter.Filter(tools, "core");
            var coreProps = (JObject)coreFiltered[0]["inputSchema"]!["properties"]!;
            Assert.Equal(exact40, coreProps["pExact40"]!["description"]?.ToString());
            Assert.Equal(exact40, coreProps["pExact40Ws"]!["description"]?.ToString());
            Assert.Equal(new string('b', 39) + "…", coreProps["pSpaceAt39"]!["description"]?.ToString());
            Assert.Equal(new string('c', 39) + "…", coreProps["pNoSpace45"]!["description"]?.ToString());
            Assert.Equal(string.Empty, coreProps["pWhitespaceOnly"]!["description"]?.ToString());
            Assert.Null(coreProps["forceWrite"]);

            var allFiltered = ToolProfileFilter.Filter(tools, "all");
            var allProps = (JObject)allFiltered[0]["inputSchema"]!["properties"]!;
            Assert.NotNull(allProps["forceWrite"]);
        }
    }
}
