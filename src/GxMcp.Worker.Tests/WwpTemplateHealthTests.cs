using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpTemplateHealthTests
    {
        [Theory]
        [InlineData("Trn.Selection", "<selection />", null)]
        [InlineData("", "<selection />", "emptyType")]
        [InlineData("   ", "<selection />", "emptyType")]
        [InlineData("Trn.Selection", "", "emptyTemplateXml")]
        [InlineData("Trn.Selection", "   ", "emptyTemplateXml")]
        [InlineData("Trn.Selection", "<selection><table>", "malformedTemplateXml")]
        [InlineData("", "", "emptyType")]
        public void Diagnose_names_the_first_problem_that_breaks_the_editor(string type, string templateXml, string expected)
        {
            Assert.Equal(expected, WwpTemplateHealth.Diagnose(type, templateXml));
        }

        [Fact]
        public void Summarize_counts_every_broken_template_and_lists_only_the_limit()
        {
            var templates = Enumerable.Range(1, 7)
                .Select(i => new WwpTemplateHealth.TemplateRecord { Name = "SynthTemplate" + i, Type = "", TemplateXml = "<selection />" })
                .Append(new WwpTemplateHealth.TemplateRecord { Name = "SynthUsable", Type = "Trn.Transaction", TemplateXml = "<transaction />" });

            var block = WwpTemplateHealth.Summarize(templates);

            Assert.True(block["available"].Value<bool>());
            Assert.Equal(8, block["read"].Value<int>());
            Assert.Equal(7, block["invalid"].Value<int>());
            Assert.Equal(WwpTemplateHealth.ListLimit, ((JArray)block["listed"]).Count);
            Assert.Equal("SynthTemplate1", (string)block["listed"][0]["name"]);
            Assert.Equal("emptyType", (string)block["listed"][0]["problem"]);
            Assert.Equal("high", (string)block["severity"]);
            Assert.NotNull(block["hint"]);
        }

        [Fact]
        public void Unreadable_templates_are_counted_apart_and_never_classified()
        {
            var templates = new[]
            {
                new WwpTemplateHealth.TemplateRecord { Name = "SynthBroken", Type = "", TemplateXml = "<selection />" },
                new WwpTemplateHealth.TemplateRecord { Unreadable = true },
                new WwpTemplateHealth.TemplateRecord { Name = "SynthUsable", Type = "Trn.Selection", TemplateXml = "<selection />" }
            };

            var block = WwpTemplateHealth.Summarize(templates);

            Assert.Equal(2, block["read"].Value<int>());
            Assert.Equal(1, block["unreadable"].Value<int>());
            Assert.Equal(1, block["invalid"].Value<int>());
        }

        [Fact]
        public void Usable_templates_report_no_severity_and_no_hint()
        {
            var block = WwpTemplateHealth.Summarize(new[]
            {
                new WwpTemplateHealth.TemplateRecord { Name = "SynthUsable", Type = "Trn.Selection", TemplateXml = "<selection />" }
            });

            Assert.Equal(0, block["invalid"].Value<int>());
            Assert.Null(block["severity"]);
            Assert.Null(block["hint"]);
        }
    }
}
