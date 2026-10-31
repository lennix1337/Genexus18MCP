using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // A partial native export reaches the client with each object's own reply, including the ObjectHasNoTextParts
    // reason of an object without text parts. The gateway shapes genexus_io results without projecting their
    // objects, and a partial export is not an error envelope, so its replies are not trimmed away.
    public class ExportPartialProjectionTests
    {
        [Fact]
        public void A_partial_export_reaches_the_client_with_each_objects_reply_and_is_not_an_error()
        {
            var noTextParts = new JObject
            {
                ["status"] = "error",
                ["target"] = "SynthTemplate",
                ["error"] = new JObject
                {
                    ["code"] = "ObjectHasNoTextParts",
                    ["message"] = "'SynthTemplate' (WorkWithPlus for Web Template) has no text parts to export.",
                    ["hint"] = "Read the properties with genexus_properties action=get and change them with action=set."
                }
            };
            var payload = new JObject
            {
                ["status"] = "partial",
                ["code"] = "ObjectTextNativeExportPartial",
                ["result"] = new JObject
                {
                    ["format"] = "native",
                    ["planned"] = 2,
                    ["exported"] = 1,
                    ["failed"] = 1,
                    ["objects"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "SynthTemplate",
                            ["type"] = "WorkWithPlus for Web Template",
                            ["status"] = "error",
                            ["message"] = noTextParts.ToString()
                        },
                        new JObject
                        {
                            ["name"] = "SynthOk",
                            ["type"] = "Procedure",
                            ["status"] = "ok",
                            ["bytes"] = 42
                        }
                    }
                }
            };

            var shaped = Program.BuildToolResultContent(payload, false, "genexus_io", new JObject
            {
                ["action"] = "export_kb_to_text",
                ["format"] = "native",
                ["projection"] = "minimal"
            });

            Assert.False(shaped["isError"]?.Value<bool>());
            JObject inner = JObject.Parse(shaped["content"]?[0]?["text"]?.ToString() ?? "{}");
            Assert.Equal("partial", (string)inner["status"]);
            JArray objects = (JArray)inner["result"]!["objects"]!;
            Assert.Equal(2, objects.Count);
            Assert.Contains("ObjectHasNoTextParts", (string)objects[0]!["message"]!);
            Assert.Equal("ok", (string)objects[1]!["status"]!);
        }
    }
}
