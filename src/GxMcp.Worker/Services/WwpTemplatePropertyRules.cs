using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Property names of a WorkWithPlus template object as genexus_properties accepts them. The SDK keeps the
    // template's type, layout and main template in WWPTemplate_* properties; the official text export calls the
    // same values TemplateType, TemplateXml and so on. Only the template object type takes these names, so every
    // other object keeps its names exactly as the SDK reports them.
    internal static class WwpTemplatePropertyRules
    {
        internal const string TypeProperty = "WWPTemplate_Type";
        internal const string XmlProperty = WwpTemplateXml.SourceProperty;

        internal static readonly IReadOnlyDictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TemplateType"] = TypeProperty,
                ["TemplateCategory"] = "WWPTemplate_Category",
                ["TemplateCategoryOrder"] = "WWPTemplate_CategoryOrder",
                ["TemplateXml"] = XmlProperty,
                ["MainTemplate"] = "WWPTemplate_MainTemplate",
                ["InstanceType"] = "WWPTemplate_InstanceType",
                ["PreviewImage"] = "WWPTemplate_PreviewImage"
            };

        // The SDK property a requested name stands for. Names that are not aliases, and every name on another
        // object type, come back unchanged.
        internal static string NativeName(Guid objectType, string requested)
        {
            if (objectType != WwpTemplateXml.ObjectType || requested == null) return requested;
            return Aliases.TryGetValue(requested.Trim(), out string native) ? native : requested;
        }

        // Two names in one call that resolve to the same SDK property, such as an alias and the name it stands for.
        // Returns the refusal message, or null when every name stands for its own property.
        internal static string FindConflict(Guid objectType, IEnumerable<string> requested)
        {
            if (objectType != WwpTemplateXml.ObjectType) return null;
            var firstNameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in requested)
            {
                string native = NativeName(objectType, name);
                if (firstNameOf.TryGetValue(native, out string first))
                    return $"'{first}' and '{name}' name the same WorkWithPlus template property ({native}); send only one of them.";
                firstNameOf[native] = name;
            }
            return null;
        }

        // Read side: a requested alias gets an item of its own, named as asked and carrying the stored property as
        // nativeName, so the shaper filters and reports it like any other requested name. An alias without a stored
        // item adds nothing and is reported as not found, as before.
        internal static void AddAliasReadItems(Guid objectType, JObject fullPropsResult, IEnumerable<string> requested)
        {
            if (objectType != WwpTemplateXml.ObjectType || !(fullPropsResult?["properties"] is JArray props)) return;
            foreach (string requestedName in requested)
            {
                string name = requestedName?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                string native = NativeName(objectType, name);
                if (string.Equals(native, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (props.OfType<JObject>().Any(p => string.Equals(p["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                JObject stored = props.OfType<JObject>()
                    .FirstOrDefault(p => string.Equals(p["name"]?.ToString(), native, StringComparison.OrdinalIgnoreCase));
                if (stored == null) continue;
                var item = new JObject { ["name"] = name, ["nativeName"] = native };
                foreach (var field in stored.Properties())
                    if (field.Name != "name") item[field.Name] = field.Value.DeepClone();
                props.Add(item);
            }
        }
    }
}
