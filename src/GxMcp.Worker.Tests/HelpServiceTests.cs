using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.8.0 (#1) — NL → tool router. Weakly-capable LLMs describe an intent
    // and HelpService maps it to the most likely tool call. Tiny hand-curated
    // keyword scorer.
    public class HelpServiceTests
    {
        [Fact]
        public void EmptyGoal_ReturnsCanonicalError()
        {
            var raw = new HelpService().RouteGoal("");
            Assert.True(EnvelopeConformance.Validate(raw).Ok);
            var obj = JObject.Parse(raw);
            Assert.Equal("error", (string)obj["status"]);
            Assert.Equal("EmptyGoal", (string)obj["error"]["code"]);
        }

        [Theory]
        [InlineData("read the Source of MyPanel", "genexus_read")]
        [InlineData("delete the WebPanel MyPanel", "genexus_delete_object")]
        [InlineData("edit the Source of MyPanel", "genexus_edit")]
        [InlineData("apply the WorkWithPlus pattern to Customer", "genexus_apply_pattern")]
        [InlineData("list objects in the KB", "genexus_list_objects")]
        [InlineData("search for type Transaction", "genexus_query")]
        [InlineData("build the KB", "genexus_lifecycle")]
        [InlineData("rebuild everything", "genexus_lifecycle")]
        [InlineData("reindex the KB", "genexus_lifecycle")]
        [InlineData("status of operation op-1234", "genexus_lifecycle")]
        [InlineData("cancel the build", "genexus_lifecycle")]
        [InlineData("preview the panel", "genexus_preview")]
        [InlineData("impact of changing Customer", "genexus_analyze")]
        [InlineData("rename CustomerOld to CustomerNew", "genexus_rename_across_kb")]
        [InlineData("whoami", "genexus_whoami")]
        public void RouteGoal_PicksExpectedTool(string goal, string expectedTool)
        {
            var raw = new HelpService().RouteGoal(goal);
            Assert.True(EnvelopeConformance.Validate(raw).Ok);
            var obj = JObject.Parse(raw);
            Assert.Equal("ok", (string)obj["status"]);
            Assert.Equal("RouteSuggested", (string)obj["code"]);
            var matches = obj["result"]["matches"] as JArray;
            Assert.NotNull(matches);
            Assert.True(matches.Count >= 1);
            // The top match should be the expected tool.
            Assert.Equal(expectedTool, (string)matches[0]["tool"]);
        }

        [Fact]
        public void RouteGoal_UnknownIntent_FallsBackToOrient()
        {
            var raw = new HelpService().RouteGoal("zxcvbn qwerty asdfgh");
            var obj = JObject.Parse(raw);
            Assert.Equal("ok", (string)obj["status"]);
            Assert.Equal("NoMatch", (string)obj["code"]);
            Assert.Equal("genexus_orient", (string)obj["result"]["fallback"]["tool"]);
        }

        [Fact]
        public void RouteGoal_BuildAction_PreFillsArgs()
        {
            var raw = new HelpService().RouteGoal("build");
            var obj = JObject.Parse(raw);
            var top = obj["result"]["matches"][0];
            Assert.Equal("genexus_lifecycle", (string)top["tool"]);
            Assert.Equal("build", (string)top["args"]["action"]);
        }

        [Fact]
        public void RouteGoal_BuildAllAction_PreFillsExplicitBuildAll()
        {
            var raw = new HelpService().RouteGoal("build all objects in the knowledge base");
            var obj = JObject.Parse(raw);
            var top = obj["result"]["matches"][0];
            Assert.Equal("genexus_lifecycle", (string)top["tool"]);
            Assert.Equal("build_all", (string)top["args"]["action"]);
        }

        [Fact]
        public void RouteGoal_Read_RequiresNamePlaceholder()
        {
            var raw = new HelpService().RouteGoal("read Customer");
            var obj = JObject.Parse(raw);
            var args = obj["result"]["matches"][0]["args"];
            // Required args show up as <placeholder> tokens.
            Assert.Equal("<name>", (string)args["name"]);
        }

        [Fact]
        public void RouteGoal_ConfidenceInRange()
        {
            var raw = new HelpService().RouteGoal("read Customer source");
            var matches = JObject.Parse(raw)["result"]["matches"] as JArray;
            foreach (var m in matches)
            {
                double c = (double)m["confidence"];
                Assert.True(c >= 0 && c <= 2, "confidence out of range: " + c);
            }
        }
    }
}
