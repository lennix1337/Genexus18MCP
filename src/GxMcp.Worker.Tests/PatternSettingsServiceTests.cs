using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternSettingsServiceTests
    {
        private static JArray Nodes() => JArray.Parse(@"[
          {'path':'/0','isTemplate':true},
          {'path':'/0/0','properties':{
            'themeClass':{'value':'TableMainTransaction','editable':true},
            'DefaultThemeClass':{'value':'Original','editable':false},
            'ChildrenOrderedList':{'value':'a,b','editable':false},
            'Name':{'value':'TableMain','editable':false}}},
          {'path':'/1/0','properties':{'themeClass':{'value':'Other','editable':true}}}
        ]");

        private static JObject Args() => new JObject
        {
            ["nodePath"] = "/0/0", ["property"] = "themeClass",
            ["value"] = "TableMainTransaction example-entry", ["baseVersion"] = "v1", ["dryRun"] = true
        };

        [Fact]
        public void DryRunIsPureAndDiffContainsExactlyOneProperty()
        {
            var nodes = Nodes(); var original = nodes.DeepClone(); var args = Args(); var originalArgs = args.DeepClone();
            JObject result = PatternSettingsService.Plan(nodes, "/0", args, "v1");
            Assert.True(JToken.DeepEquals(original, nodes));
            Assert.True(JToken.DeepEquals(originalArgs, args));
            Assert.False((bool)result["saved"]);
            Assert.False((bool)result["saveAvailable"]);
            Assert.Single((JArray)result["diff"]);
            Assert.Equal("TableMainTransaction", (string)result["diff"][0]["before"]);
            Assert.Equal("TableMainTransaction example-entry", (string)result["diff"][0]["after"]);
        }

        [Fact]
        public void ConcurrentChangeRejectsThePlanWithoutChangingSnapshot()
        {
            var nodes = Nodes(); var original = nodes.DeepClone();
            var error = Assert.Throws<InvalidOperationException>(() => PatternSettingsService.Plan(nodes, "/0", Args(), "v2"));
            Assert.Contains("VersionConflict", error.Message);
            Assert.True(JToken.DeepEquals(original, nodes));
        }

        [Theory]
        [InlineData("DefaultThemeClass")]
        [InlineData("ChildrenOrderedList")]
        [InlineData("Name")]
        [InlineData("Guid")]
        [InlineData("Missing")]
        public void InternalIdentityAndUnknownPropertiesAreRejected(string property)
        {
            var args = Args(); args["property"] = property;
            Assert.Throws<ArgumentException>(() => PatternSettingsService.Plan(Nodes(), "/0", args, "v1"));
        }

        [Theory]
        [InlineData("/1/0")]
        [InlineData("/01/0")]
        [InlineData("/")]
        [InlineData(null)]
        public void CannotEditOutsideSelectedTemplate(string path)
        {
            var args = Args(); args["nodePath"] = path;
            Assert.Throws<ArgumentException>(() => PatternSettingsService.Plan(Nodes(), "/0", args, "v1"));
        }

        [Fact]
        public void PaginationReassemblesEveryNodeAndHandlesPastEnd()
        {
            var nodes = Nodes(); var result = new JArray(); int offset = 0;
            while (true)
            {
                var page = PatternSettingsService.Page(nodes, offset, 1, "v1");
                foreach (var node in (JArray)page["nodes"]) result.Add(node.DeepClone());
                Assert.Equal(nodes.Count, (int)page["totalNodes"]);
                Assert.Equal("v1", (string)page["versionToken"]);
                if (!(bool)page["truncated"]) break;
                offset = (int)page["nextOffset"];
            }
            Assert.True(JToken.DeepEquals(nodes, result));
            Assert.Empty((JArray)PatternSettingsService.Page(nodes, 99, 0, "v1")["nodes"]);
            Assert.Equal(nodes.Count, (int)PatternSettingsService.Page(nodes, 0, 0, "v1")["returned"]);
        }

        [Fact]
        public void SaveGateRefusesEvenAnApprovedVersionedPlan()
        {
            var plan = PatternSettingsService.Plan(Nodes(), "/0", Args(), "v1");
            var original = plan.DeepClone();
            Assert.Contains("SettingsIsolationUnverified", PatternSettingsService.FinishEdit(plan, false, "v1"));
            Assert.Contains("VersionRequired", PatternSettingsService.FinishEdit(plan, false, null));
            Assert.Contains("SettingsDryRun", PatternSettingsService.FinishEdit(plan, true, null));
            Assert.True(JToken.DeepEquals(original, plan));
        }

        [Fact]
        public void TokenCoversIdentityWholeSettingsAndEffectiveValues()
        {
            var nodes = Nodes(); var token = PatternSettingsService.Token("kb/model/settings", "xml", nodes);
            Assert.Equal(token, PatternSettingsService.Token("kb/model/settings", "xml", nodes));
            Assert.NotEqual(token, PatternSettingsService.Token("other/model/settings", "xml", nodes));
            Assert.NotEqual(token, PatternSettingsService.Token("kb/model/settings", "other template changed", nodes));
            nodes[2]["properties"]["themeClass"]["value"] = "Concurrent";
            Assert.NotEqual(token, PatternSettingsService.Token("kb/model/settings", "xml", nodes));
        }
    }
}
