using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    public sealed class ChangeSetRoutingTests
    {
        [Fact]
        public void EditChangeSet_RoutesToMutationModuleWithOriginalArguments()
        {
            var args = new JObject
            {
                ["changeSet"] = new JObject
                {
                    ["action"] = "preview",
                    ["changes"] = new JArray(new JObject
                    {
                        ["name"] = "Customer",
                        ["part"] = "Source",
                        ["content"] = "new source"
                    })
                }
            };

            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args));

            Assert.Equal("Mutation", routed["module"]?.ToString());
            Assert.Equal("ChangeSet", routed["action"]?.ToString());
            Assert.Equal("preview", routed["params"]?["changeSet"]?["action"]?.ToString());
            Assert.Equal("Customer", routed["params"]?["changeSet"]?["changes"]?[0]?["name"]?.ToString());
        }
    }
}
