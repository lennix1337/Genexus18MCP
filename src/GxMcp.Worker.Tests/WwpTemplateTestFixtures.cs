using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests
{
    // The SDK's answers about WorkWithPlus templates, in the shape ObjectService.ReadWwpTemplateProperties replies them.
    // The tests of the template's export and of its import share them, so each value is written once.
    internal static class WwpTemplateTestFixtures
    {
        internal const string TemplateType = "WorkWithPlus for Web Template";

        // The template's name, its type and the values of the official text, as the SDK holds them.
        internal static string PropertiesOf(string objectName)
        {
            return new JObject
            {
                ["objectName"] = objectName,
                ["objectType"] = TemplateType,
                ["properties"] = new JObject
                {
                    ["Name"] = objectName,
                    ["Description"] = "Synth description",
                    ["TemplateCategory"] = "SynthCategory",
                    ["TemplateCategoryOrder"] = "0",
                    ["TemplateType"] = "Trn.Selection",
                    ["TemplateXml"] = "<selection Name=\"SynthTemplate\" instanceType=\"WebPanel\"><table name=\"TableMain\" /></selection>",
                    ["MainTemplate"] = "Main",
                    ["InstanceType"] = "WebPanel",
                    ["PreviewImage"] = string.Empty
                }
            }.ToString();
        }

        // The SDK's answer for an object of another type: it is not a template, so its missing text parts are a case of their own.
        internal static string NotATemplate(string target)
        {
            return McpResponse.Err(code: "NotWwpTemplate", message: target + " is not a WorkWithPlus template.", target: target);
        }
    }
}
