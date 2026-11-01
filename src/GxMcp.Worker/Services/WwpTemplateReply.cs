using System;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // The reply of a read of a WorkWithPlus template's properties: the values the export writes, or the reason they cannot be
    // read. The decision on the object's type and the shape of the reply are here, without the SDK, so the tests reach them.
    internal static class WwpTemplateReply
    {
        internal const string NotTemplateCode = "NotWwpTemplate";
        internal const string ReadFailedCode = "WwpTemplatePropertiesReadFailed";
        private const string TypeProperty = "TemplateType";
        private const string XmlProperty = "TemplateXml";

        // An object of another type answers NotTemplateCode; the export keeps its no-text-parts case for it.
        // A value the SDK does not hold is left out. The type and the XML are the two values a template cannot do without:
        // when either one is not read, the read of that template fails.
        internal static string Shape(Guid? typeId, string objectName, string typeName, Func<string, object> readSdkProperty)
        {
            if (typeId != WwpTemplateXml.ObjectType)
                return McpResponse.Err(code: NotTemplateCode, message: "'" + objectName + "' is not a WorkWithPlus template.", target: objectName);

            var values = new JObject();
            foreach (string property in WwpTemplateDocument.PropertyOrder)
            {
                if (property == WwpTemplateDocument.NameProperty)
                {
                    values[property] = objectName;
                    continue;
                }
                string sdkName = WwpTemplateDocument.SdkNameOf(property);
                if (sdkName == null) continue;
                object value = readSdkProperty(sdkName);
                if (value == null)
                {
                    if (property == TypeProperty || property == XmlProperty)
                        return McpResponse.Err(code: ReadFailedCode, message: "The type or the XML of the template '" + objectName + "' could not be read.", target: objectName);
                    continue;
                }
                values[property] = value.ToString();
            }
            return new JObject
            {
                ["objectName"] = objectName,
                ["objectType"] = typeName,
                ["properties"] = values
            }.ToString();
        }
    }
}
