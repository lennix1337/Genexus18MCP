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
            string summary = (string)tool["description"];
            string typeProperty = (string)tool["inputSchema"]["properties"]["type"]["description"];

            Assert.Contains("DesignSystem", summary);
            Assert.Contains("DesignSystem", typeProperty);
        }

        [Fact]
        public void Help_NamesDesignSystemAsCreatedEmpty()
        {
            string help = ToolHelpCatalog.Get("genexus_create");

            Assert.Contains("`DesignSystem` — created empty", help);
        }

        [Fact]
        public void Help_RefusesWorkWithPlusTemplatesWithTheirErrorCodeAndTheIde()
        {
            string help = ToolHelpCatalog.Get("genexus_create");

            Assert.Contains("WorkWithPlus for Web Template", help);
            Assert.Contains("UnsupportedObjectType", help);
            Assert.Contains("GeneXus IDE", help);
        }

        private static JObject CreateTool()
        {
            var tools = JArray.Parse(RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            return (JObject)tools.Single(t => (string)t["name"] == "genexus_create");
        }
    }
}
