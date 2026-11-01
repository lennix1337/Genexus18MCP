using System.Linq;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The genexus_create catalog is what an agent reads to choose a type. The schema and the help both name
    /// DesignSystem as creatable (it is saved empty by the same SDK path as the other types), and the help says that a
    /// WorkWithPlus template is refused and where it is created. The discovery golden pins the schema text as a whole;
    /// these checks name the facts, so a reword cannot drop one of them without a failing test.
    /// </summary>
    public class CreateCatalogContractTests
    {
        [Fact]
        public void Schema_ListsDesignSystemAmongTheCreatableTypes()
        {
            JObject tool = CreateTool();
            string? summary = (string?)tool["description"];
            string? typeProperty = (string?)tool["inputSchema"]?["properties"]?["type"]?["description"];

            Assert.NotNull(summary);
            Assert.NotNull(typeProperty);
            Assert.Contains("DesignSystem", summary);
            Assert.Contains("DesignSystem", typeProperty);
        }

        [Fact]
        public void Help_NamesDesignSystemWithItsPartsEmpty()
        {
            string? help = ToolHelpCatalog.Get("genexus_create");

            Assert.NotNull(help);
            Assert.Contains("`DesignSystem` — created with its parts empty (Documentation, Source, Styles, Tokens).", help);
        }

        [Fact]
        public void Help_RefusesWorkWithPlusTemplatesInEveryModeWithTheirErrorCodesAndTheIde()
        {
            string? help = ToolHelpCatalog.Get("genexus_create");

            Assert.NotNull(help);
            Assert.Contains("WorkWithPlus for Web Template", help);
            Assert.Contains("UnsupportedObjectType", help);
            Assert.Contains("AtomicCreateValidationFailed", help);
            Assert.Contains("mode=update", help);
            Assert.Contains("GeneXus IDE", help);
        }

        private static JObject CreateTool()
        {
            var tools = JArray.Parse(RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            return (JObject)tools.Single(t => (string?)t["name"] == "genexus_create");
        }
    }
}
