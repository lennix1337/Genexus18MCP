using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // A partial native export reaches the client with each object's own reply: its code and hint are fields, so a
    // client branches on them without parsing the message. The gateway shapes genexus_io results without projecting
    // their objects, and a partial export is not an error envelope, so its replies are not trimmed away.
    public class ExportPartialProjectionTests
    {
        [Fact]
        public void A_partial_export_reaches_the_client_with_each_objects_reply_and_is_not_an_error()
        {
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
                            ["message"] = "'SynthTemplate' (WorkWithPlus for Web Template) has no text parts to export: its data are SDK properties.",
                            ["code"] = "ObjectHasNoTextParts",
                            ["hint"] = "Read the properties with genexus_properties action=get and change them with action=set. The native export does not write them."
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
            Assert.Equal("partial", (string?)inner["status"]);
            JArray objects = (JArray)inner["result"]!["objects"]!;
            Assert.Equal(2, objects.Count);
            Assert.Equal("ObjectHasNoTextParts", (string)objects[0]!["code"]!);
            Assert.Contains("genexus_properties action=get", (string)objects[0]!["hint"]!);
            Assert.DoesNotContain("{", (string)objects[0]!["message"]!);
            Assert.Equal("ok", (string)objects[1]!["status"]!);
        }

        [Fact]
        public void A_partial_import_reaches_the_client_with_the_refusal_of_each_template_file()
        {
            var payload = new JObject
            {
                ["status"] = "partial",
                ["code"] = "ObjectTextNativeImportPartial",
                ["result"] = new JObject
                {
                    ["format"] = "native",
                    ["failed"] = 1,
                    ["objects"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "SynthTemplate",
                            ["type"] = "WorkWithPlusforWebTemplate",
                            ["status"] = "error",
                            ["code"] = "WwpTemplateImportRefused",
                            ["message"] = "'SynthTemplate' is a WorkWithPlus template: the native import does not write templates, so nothing was written to the KB.",
                            ["hint"] = "Change the template's properties with genexus_properties action=set, TemplateXml included."
                        }
                    }
                }
            };

            var shaped = Program.BuildToolResultContent(payload, false, "genexus_io", new JObject
            {
                ["action"] = "import_text_to_kb",
                ["format"] = "native",
                ["projection"] = "minimal"
            });

            Assert.False(shaped["isError"]?.Value<bool>());
            JObject inner = JObject.Parse(shaped["content"]?[0]?["text"]?.ToString() ?? "{}");
            Assert.Equal("partial", (string?)inner["status"]);
            JObject item = (JObject)((JArray)inner["result"]!["objects"]!)[0];
            Assert.Equal("WwpTemplateImportRefused", (string)item["code"]!);
            Assert.Contains("genexus_properties action=set", (string)item["hint"]!);
        }
    }
}
