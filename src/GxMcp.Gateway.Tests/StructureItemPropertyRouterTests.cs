using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class StructureItemPropertyRouterTests
    {
        [Fact]
        public void GetItemProperty_ForwardsTheMemberPathAndThePropertyName()
        {
            var args = JObject.Parse(@"{
              'action':'get_item_property',
              'name':'SampleResponse',
              'type':'SDT',
              'itemPath':['Level','debug'],
              'property':'JsonNullSerialization'
            }");

            var json = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_structure", args)!);

            Assert.Equal("Structure", json["module"]!.ToString());
            Assert.Equal("GetSdtItemProperty", json["action"]!.ToString());
            Assert.Equal("SampleResponse", json["target"]!.ToString());
            Assert.Equal("SDT", json["type"]!.ToString());
            Assert.Equal(JTokenType.Array, json["itemPath"]!.Type);
            Assert.Equal("Level", json["itemPath"]![0]!.ToString());
            Assert.Equal("debug", json["itemPath"]![1]!.ToString());
            Assert.Equal("JsonNullSerialization", json["property"]!.ToString());
        }

        [Fact]
        public void SetItemProperty_ForwardsValueVersionAndPreviewFlag()
        {
            var args = JObject.Parse(@"{
              'action':'set_item_property',
              'name':'SampleResponse',
              'itemPath':['debug'],
              'property':'Json Null Serialization',
              'value':'Empty',
              'expectedVersion':'638000000000000000:root',
              'dryRun':true
            }");

            var json = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_structure", args)!);

            Assert.Equal("SetSdtItemProperty", json["action"]!.ToString());
            Assert.Equal("SampleResponse", json["target"]!.ToString());
            Assert.Equal("Json Null Serialization", json["property"]!.ToString());
            Assert.Equal("Empty", json["value"]!.ToString());
            Assert.Equal("638000000000000000:root", json["expectedVersion"]!.ToString());
            Assert.True(json["dryRun"]!.Value<bool>());
        }
    }
}
