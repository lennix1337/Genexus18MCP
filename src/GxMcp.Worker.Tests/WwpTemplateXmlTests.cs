using System;
using System.Linq;
using System.Xml;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpTemplateXmlTests
    {
        private const string Selector = "wwp:11111111-2222-3333-4444-555555555555";
        private const string Xml = "<?xml version='1.0'?>\r\n<transaction Name='Transaction' defaultName='Original'>\r\n"
            + " <!-- template metadata must stay verbatim -->\r\n <table name='TableMain' themeClass = 'TableMainTransaction'"
            + " defaultThemeClass='TableMainTransaction' childrenOrderedList='x&amp;y'><text caption='A &amp; B'/></table>\r\n</transaction>";

        private static JObject Template(string source = Xml) => new JObject
        {
            ["path"] = Selector, ["templateGuid"] = Selector.Substring(4), ["storage"] = "wwp-object",
            ["name"] = "Transaction", ["caption"] = "Default data", ["type"] = "Trn.Transaction",
            ["mainTemplate"] = "Main", ["source"] = source
        };

        private static JObject Args(string property = "themeClass", string value = "TableMainTransaction example-entry") => new JObject
        {
            ["nodePath"] = Selector + "/0", ["property"] = property, ["value"] = value,
            ["baseVersion"] = "v1", ["dryRun"] = true
        };

        private static string ApplyTextEdit(string source, JObject plan)
        {
            var edit = plan["textEdit"];
            int start = (int)edit["offset"], length = (int)edit["length"];
            Assert.Equal(source.Substring(start, length), (string)edit["before"]);
            return source.Substring(0, start) + (string)edit["after"] + source.Substring(start + length);
        }

        [Fact]
        public void ReadsSeparateTransactionTemplateAndAllNodesWithoutRewritingSource()
        {
            var template = Template(); var original = template.DeepClone();
            var catalog = WwpTemplateXml.CatalogEntry(template);
            Assert.Null(catalog["source"]);
            Assert.Equal("Default data", (string)catalog["caption"]);
            var page = WwpTemplateXml.Read(template, 0, 0, "v1");
            Assert.Equal(Xml, (string)page["source"]);
            Assert.Equal(3, (int)page["totalNodes"]);
            Assert.Equal("TableMain", (string)page["nodes"][1]["properties"]["name"]["value"]);
            Assert.Equal("TableMainTransaction", (string)page["nodes"][1]["properties"]["themeClass"]["value"]);
            Assert.False((bool)page["effectivePropertiesResolved"]);
            Assert.True(JToken.DeepEquals(original, template));
        }

        [Fact]
        public void PagedReadReassemblesNodesAndExplicitFullReadIncludesExactXml()
        {
            var combined = new JArray(); int offset = 0;
            while (true)
            {
                var page = WwpTemplateXml.Read(Template(), offset, 1, "v1");
                Assert.False((bool)page["sourceIncluded"]);
                Assert.Null(page["source"]);
                Assert.Equal(Xml.Length, (int)page["sourceLength"]);
                Assert.Equal("v1", (string)page["versionToken"]);
                foreach (var node in (JArray)page["nodes"]) combined.Add(node.DeepClone());
                if (!(bool)page["truncated"]) break;
                offset = (int)page["nextOffset"];
            }
            Assert.True(JToken.DeepEquals(WwpTemplateXml.Project(Xml, Selector), combined));
            Assert.Empty((JArray)WwpTemplateXml.Read(Template(), 99, 0, "v1")["nodes"]);
            Assert.Throws<ArgumentException>(() => WwpTemplateXml.Read(Template(), -1, 1, "v1"));
        }

        [Fact]
        public void DryRunChangesOnlyTheChosenAttributeValueAndNeverMutatesInput()
        {
            var template = Template(); var args = Args();
            var originalTemplate = template.DeepClone(); var originalArgs = args.DeepClone();
            var plan = WwpTemplateXml.Plan(template, args, "v1");
            Assert.Equal(Xml.Replace("themeClass = 'TableMainTransaction'", "themeClass = 'TableMainTransaction example-entry'"), ApplyTextEdit(Xml, plan));
            Assert.Single((JArray)plan["diff"]);
            Assert.True((bool)plan["sourceChanged"]);
            Assert.False((bool)plan["saved"]);
            Assert.False((bool)plan["saveAvailable"]);
            Assert.True(JToken.DeepEquals(originalTemplate, template));
            Assert.True(JToken.DeepEquals(originalArgs, args));
            Assert.Contains("SettingsIsolationUnverified", PatternSettingsService.FinishEdit(plan, false, "v1"));
        }

        [Theory]
        [InlineData("\n", "\"")]
        [InlineData("\r", "'")]
        [InlineData("\r\n", "'")]
        public void SurgicalEditHandlesLineEndingsQuotesAndEntities(string newline, string quote)
        {
            string source = "<transaction>" + newline + "  <table name='TableMain' themeClass=" + quote + "A &amp; B" + quote + "/></transaction>";
            string value = "x & < ' \"\r\n\t";
            var plan = WwpTemplateXml.Plan(Template(source), Args(value: value), "v1");
            string changed = ApplyTextEdit(source, plan);
            Assert.Equal(value, (string)WwpTemplateXml.Project(changed, Selector)[1]["properties"]["themeClass"]["value"]);
            Assert.StartsWith(source.Substring(0, (int)plan["textEdit"]["offset"]), changed);
            Assert.EndsWith(quote + "/></transaction>", changed);
        }

        [Fact]
        public void NoOpRetainsOriginalEntitySpelling()
        {
            string source = "<transaction><table themeClass='A&#32;B'/></transaction>";
            var plan = WwpTemplateXml.Plan(Template(source), Args(value: "A B"), "v1");
            Assert.False((bool)plan["sourceChanged"]);
            Assert.Equal(source, ApplyTextEdit(source, plan));
        }

        [Theory]
        [InlineData("Name")]
        [InlineData("name")]
        [InlineData("defaultThemeClass")]
        [InlineData("childrenOrderedList")]
        [InlineData("Guid")]
        [InlineData("newProperty")]
        [InlineData("caption")]
        public void CannotPreviewInternalUnknownOrUnauditedProperties(string property)
        {
            Assert.Throws<ArgumentException>(() => WwpTemplateXml.Plan(Template(), Args(property), "v1"));
        }

        [Fact]
        public void StaleTokenAndOtherTemplatePathAreRejected()
        {
            Assert.Contains("VersionConflict", Assert.Throws<InvalidOperationException>(() => WwpTemplateXml.Plan(Template(), Args(), "v2")).Message);
            var args = Args(); args["nodePath"] = "wwp:another/0";
            Assert.Throws<ArgumentException>(() => WwpTemplateXml.Plan(Template(), args, "v1"));
        }

        [Fact]
        public void XmlDtdAndInvalidReplacementAreRejected()
        {
            Assert.Throws<XmlException>(() => WwpTemplateXml.Project("<!DOCTYPE transaction [<!ENTITY test 'x'>]><transaction/>", Selector));
            Assert.Throws<XmlException>(() => WwpTemplateXml.Plan(Template(), Args(value: "bad\u0001value"), "v1"));
        }

        [Fact]
        public void TemplateRequiresUniqueSettingsMainLinkAndTokenCoversXmlAndRevision()
        {
            var settings = JArray.Parse("[{'path':'/15/0','type':'InstanceTemplate','properties':{'Name':{'value':'Main'}}}]");
            var template = Template();
            PatternSettingsService.BindTemplate(template, settings);
            Assert.True((bool)template["settingsLinkVerified"]);
            Assert.Equal("/15/0", (string)template["settingsPath"]);
            string token = PatternSettingsService.Token("kb/version/settings", "settings xml", new JArray(settings, template));
            template["source"] = Xml + " ";
            Assert.NotEqual(token, PatternSettingsService.Token("kb/version/settings", "settings xml", new JArray(settings, template)));
            template["source"] = Xml; template["revision"] = 2;
            Assert.NotEqual(token, PatternSettingsService.Token("kb/version/settings", "settings xml", new JArray(settings, template)));
            settings.Add(settings[0].DeepClone());
            PatternSettingsService.BindTemplate(template, settings);
            Assert.False((bool)template["settingsLinkVerified"]);
            settings.Clear();
            PatternSettingsService.BindTemplate(template, settings);
            Assert.False((bool)template["settingsLinkVerified"]);
        }

        [Fact]
        public void ChangedSettingsMainInvalidatesTokenAndTemplateLinkEvenWithUnchangedTemplateXml()
        {
            var settings = JArray.Parse("[{'path':'/15/0','type':'InstanceTemplate','properties':{'Name':{'value':'Main'}}}]");
            var template = Template();
            PatternSettingsService.BindTemplate(template, settings);
            string token = PatternSettingsService.Token("kb/model/version/settings", "unchanged xml", new JArray(settings, template));
            var rereadNodes = (JArray)settings.DeepClone();
            rereadNodes[0]["properties"]["Name"]["value"] = "Renamed";
            var rereadTemplate = (JObject)template.DeepClone();
            PatternSettingsService.BindTemplate(rereadTemplate, rereadNodes);
            Assert.False((bool)rereadTemplate["settingsLinkVerified"]);
            Assert.Equal((string)template["source"], (string)rereadTemplate["source"]);
            Assert.NotEqual(token, PatternSettingsService.Token("kb/model/version/settings", "unchanged xml", new JArray(rereadNodes, rereadTemplate)));
            Assert.NotEqual(token, PatternSettingsService.Token("kb/model/other-version/settings", "unchanged xml", new JArray(settings, template)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("WP.Layout")]
        [InlineData("Unknown")]
        public void EmptyMainNeverAssumesGlobalSettingsLink(string kind)
        {
            var template = Template();
            template["type"] = kind; template["mainTemplate"] = "";
            var settings = JArray.Parse("[{'path':'/15/0','type':'InstanceTemplate','properties':{'Name':{'value':''}}}]");
            PatternSettingsService.BindTemplate(template, settings);
            Assert.False((bool)template["settingsLinkVerified"]);
            Assert.Equal(JTokenType.Null, template["settingsPath"].Type);
            Assert.Equal(Xml, (string)WwpTemplateXml.Read(template, 0, 0, "v1")["source"]);
        }
    }
}
