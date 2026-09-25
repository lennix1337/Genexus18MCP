using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class K2bDesignerRoutingTests
    {
        [Theory]
        [InlineData("inspect", false)]
        [InlineData("tree", false)]
        [InlineData("preview", false)]
        [InlineData("set_property", true)]
        [InlineData("add_node", true)]
        [InlineData("move_node", true)]
        [InlineData("remove_node", true)]
        public void ActionsRouteToTheIdeAndInvalidateCacheOnlyForWrites(string action, bool writes)
        {
            var args = new JObject { ["name"] = "SamplePanel", ["action"] = action, ["path"] = "root" };
            var route = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_k2b_designer", args)!);

            Assert.Equal("K2bDesigner", route["module"]?.ToString());
            Assert.Equal("SamplePanel", route["target"]?.ToString());
            Assert.Equal(action, route["action"]?.ToString());
            Assert.Equal(writes, Program.IsMutatingTool("genexus_k2b_designer", args));
        }
    }
}
