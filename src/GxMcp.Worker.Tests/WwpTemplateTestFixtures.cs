using System.Collections.Generic;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // The SDK's answers about WorkWithPlus templates. The reply is built by the production shaping function, so the fixtures
    // hold only the values the SDK would give; the tests of the template's export and of its import share them.
    internal static class WwpTemplateTestFixtures
    {
        internal const string TemplateType = "WorkWithPlus for Web Template";
        internal const string TemplateXml = "<selection Name=\"SynthTemplate\" instanceType=\"WebPanel\"><table name=\"TableMain\" /></selection>";

        // The values the SDK holds for SynthTemplate, by SDK property name.
        internal static Dictionary<string, object> SdkValues(string category = "SynthCategory")
        {
            return new Dictionary<string, object>
            {
                ["Description"] = "Synth description",
                ["WWPTemplate_Category"] = category,
                ["WWPTemplate_CategoryOrder"] = 0,
                ["WWPTemplate_Type"] = "Trn.Selection",
                ["WWPTemplate_TemplateXml"] = TemplateXml,
                ["WWPTemplate_MainTemplate"] = "Main",
                ["WWPTemplate_InstanceType"] = "WebPanel",
                ["WWPTemplate_PreviewImage"] = string.Empty,
                ["IsDefault"] = false
            };
        }

        // The reply the SDK read of a template gives, shaped by the production function.
        internal static string PropertiesOf(string objectName)
        {
            Dictionary<string, object> values = SdkValues();
            return WwpTemplateReply.Shape(WwpTemplateXml.ObjectType, objectName, TemplateType, name => values.TryGetValue(name, out object value) ? value : null);
        }

        // The reply the SDK read of an object of another type gives: it is not a template.
        internal static string NotATemplate(string target)
        {
            return WwpTemplateReply.Shape(System.Guid.Empty, target, "Image", name => null);
        }
    }
}
