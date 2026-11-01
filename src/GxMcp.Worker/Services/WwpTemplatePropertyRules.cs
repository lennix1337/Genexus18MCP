using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
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

        // The root element each known template type carries. Every template of the evidence (a WorkWithPlus 16.2 export
        // of 124 templates) has the same root for its type. Canonical roots such as selection or transaction have no Name
        // attribute, and the other types name the root after the template, so the Name attribute is not checked.
        internal static readonly IReadOnlyDictionary<string, string> RootByType =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Component"] = "component",
                ["Regular"] = "template",
                ["Report"] = "template",
                ["Wizard"] = "template",
                ["WizardStep"] = "step",
                ["Trn.Selection"] = "selection",
                ["Trn.Transaction"] = "transaction",
                ["Trn.View"] = "view",
                ["Trn.Association"] = "association",
                ["Trn.AssociationSelection"] = "associationSelection",
                ["Trn.Prompt"] = "prompt",
                ["Trn.PromptMultiple"] = "promptMultiple"
            };

        // Whether a property of the call is one of the two values that make a template readable: its type and its XML.
        internal static bool TouchesFinalState(string nativeName)
            => string.Equals(nativeName, TypeProperty, StringComparison.OrdinalIgnoreCase)
                || string.Equals(nativeName, XmlProperty, StringComparison.OrdinalIgnoreCase);

        // The type and XML a template has once the call is applied: a value the call sends wins, and a value it does not
        // send is the stored one. The keys of sent are SDK names, compared without case.
        internal static (string Type, string Xml) FinalState(IDictionary<string, string> sent, Func<string, string> readStored)
            => (sent.TryGetValue(TypeProperty, out string type) ? type : readStored(TypeProperty),
                sent.TryGetValue(XmlProperty, out string xml) ? xml : readStored(XmlProperty));

        // The checks that decide whether WorkWithPlus can list and open a template: a known type, and XML that parses
        // with the root of that type. Returns one entry per failed check, with its name and message; empty when valid.
        internal static JArray Check(string type, string xml)
        {
            var violations = new JArray();
            if (type == null)
                violations.Add(Violation("TypeNotRead", "WWPTemplate_Type could not be read from the template, so its type cannot be checked."));
            else if (string.IsNullOrWhiteSpace(type))
                violations.Add(Violation("TypeEmpty", "WWPTemplate_Type is empty. WorkWithPlus cannot list a template without a type (Unknown TemplateType)."));
            else if (!RootByType.ContainsKey(type))
                violations.Add(Violation("TypeUnknown", $"'{type}' is not one of the template types known for WorkWithPlus 16.2. Known WorkWithPlus 16.2 types: {string.Join(", ", RootByType.Keys)}."));

            if (xml == null)
            {
                violations.Add(Violation("XmlNotRead", "WWPTemplate_TemplateXml could not be read from the template, so its XML cannot be checked."));
                return violations;
            }
            if (string.IsNullOrWhiteSpace(xml))
            {
                violations.Add(Violation("XmlEmpty", "WWPTemplate_TemplateXml is empty."));
                return violations;
            }
            if (xml.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                violations.Add(Violation("XmlDtd", "WWPTemplate_TemplateXml declares a DTD, and DTDs are not accepted in a template."));
                return violations;
            }
            XElement root;
            try
            {
                root = WwpTemplateXml.Parse(xml).Root;
            }
            catch (XmlException ex)
            {
                violations.Add(Violation("XmlMalformed", "WWPTemplate_TemplateXml is not well-formed XML: " + ex.Message));
                return violations;
            }
            if (RootByType.TryGetValue(type ?? string.Empty, out string expectedRoot) && root.Name.LocalName != expectedRoot)
                violations.Add(Violation("RootMismatch", $"The root of a '{type}' template must be <{expectedRoot}>, found <{root.Name.LocalName}>."));
            return violations;
        }

        private static JObject Violation(string check, string message) => new JObject { ["check"] = check, ["message"] = message };
    }
}
