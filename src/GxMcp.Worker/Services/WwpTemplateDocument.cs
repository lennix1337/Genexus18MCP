using System.Collections.Generic;
using System.Text;

namespace GxMcp.Worker.Services
{
    // The text of a WorkWithPlus template in the layout of the official text export: the type keyword without spaces and
    // the name in the header, then a #Properties section with one quoted value per property. The official export writes
    // CRLF line endings; this one writes LF, as every native file does. The indentation is tabs, as in the official text.
    internal static class WwpTemplateDocument
    {
        // With a space ("WorkWithPlus for Web Template") the header would not be read back as a type and a name.
        internal const string Keyword = "WorkWithPlusforWebTemplate";
        internal const string Section = "Properties";
        internal const string NameProperty = "Name";

        // The properties in the order the official text writes them.
        internal static readonly string[] PropertyOrder =
        {
            NameProperty, "Description", "TemplateCategory", "TemplateCategoryOrder", "TemplateType",
            "TemplateXml", "MainTemplate", "InstanceType", "PreviewImage", "IsDefault"
        };

        // The SDK property each name reads. The WWPTemplate_* names are the aliases of WwpTemplatePropertyRules; Description
        // is the object's own property. Null for the name, which is the object's name, and for a property the SDK does not hold.
        internal static string SdkNameOf(string property)
        {
            if (property == null || property == NameProperty) return null;
            if (property == "Description") return "Description";
            return WwpTemplatePropertyRules.Aliases.TryGetValue(property, out string native) ? native : null;
        }

        // Writes the document. A property missing from values is left out; the values are the official names.
        internal static string Build(string name, IReadOnlyDictionary<string, string> values)
        {
            var document = new StringBuilder();
            document.Append(Keyword).Append(' ').Append(name).Append('\n');
            document.Append("{\n");
            document.Append('\t').Append('#').Append(Section).Append('\n');
            foreach (string property in PropertyOrder)
            {
                if (!values.TryGetValue(property, out string value)) continue;
                document.Append("\t\t").Append(property).Append(" = \"").Append(EscapeValue(value)).Append("\"\n\n");
            }
            document.Append("\t\t\n");
            document.Append("\t#End\n\n");
            document.Append("}\n");
            return document.ToString();
        }

        // The official text escapes a double quote as \" and nothing else was observed; the other escapes are this export's own
        // convention, so that every value stays on one line and reads back exactly.
        internal static string EscapeValue(string value)
        {
            var escaped = new StringBuilder((value ?? string.Empty).Length + 8);
            foreach (char ch in value ?? string.Empty)
            {
                switch (ch)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default: escaped.Append(ch); break;
                }
            }
            return escaped.ToString();
        }
    }
}
