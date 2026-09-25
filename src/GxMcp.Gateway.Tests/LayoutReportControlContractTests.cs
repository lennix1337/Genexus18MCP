using System;
using System.IO;
using System.Linq;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LayoutReportControlContractTests
    {
        [Theory]
        [InlineData("add_report_control", "AddReportControl")]
        [InlineData("move_report_control", "MoveReportControl")]
        [InlineData("remove_report_control", "RemoveReportControl")]
        public void RouterMapsTypedReportControlActions(string action, string mapped)
        {
            var command = JObject.FromObject(new LayoutRouter().ConvertToolCall("genexus_layout", new JObject
            {
                ["action"] = action,
                ["name"] = "SampleReport",
                ["printBlockName"] = "Header",
                ["controlName"] = "FilterValue"
            })!);

            Assert.Equal("Layout", command["module"]?.ToString());
            Assert.Equal(mapped, command["action"]?.ToString());
            Assert.Equal("SampleReport", command["target"]?.ToString());
            Assert.Equal("Header", command["printBlockName"]?.ToString());
            Assert.Equal("FilterValue", command["controlName"]?.ToString());
        }

        [Fact]
        public void RouterForwardsReportControlConcurrencyAndDryRunFields()
        {
            var command = JObject.FromObject(new LayoutRouter().ConvertToolCall("genexus_layout", new JObject
            {
                ["action"] = "add_report_control",
                ["name"] = "SampleReport",
                ["printBlockName"] = "Header",
                ["controlName"] = "FilterValue",
                ["kind"] = "variable",
                ["binding"] = "&SelectedFilter",
                ["left"] = 10,
                ["top"] = 20,
                ["baseVersion"] = "v-token",
                ["dryRun"] = true
            })!);

            Assert.Equal("variable", command["kind"]?.ToString());
            Assert.Equal("&SelectedFilter", command["binding"]?.ToString());
            Assert.Equal("v-token", command["baseVersion"]?.ToString());
            Assert.True(command["dryRun"]?.ToObject<bool>());
        }
        [Fact]
        public void PublishedLayoutSchemaDocumentsReportVersionRequirementWithoutCombinators()
        {
            string? root = AppContext.BaseDirectory;
            string? definitions = null;
            while (root != null)
            {
                string candidate = Path.Combine(root, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) { definitions = candidate; break; }
                root = Directory.GetParent(root)?.FullName;
            }
            Assert.NotNull(definitions);
            var tools = JArray.Parse(File.ReadAllText(definitions!));
            var layout = (JObject)tools.Single(tool => (string)tool["name"]! == "genexus_layout");
            var schema = (JObject)layout["inputSchema"]!;
            Assert.Contains("required", (string)schema["properties"]!["baseVersion"]!["description"]!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(schema["allOf"]);
        }
    }
}
