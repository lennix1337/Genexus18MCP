using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // WWPTemplate stores XML in a string property, outside PatternSettingsPart.
    // This detached reader/planner never constructs a WWP wrapper or SDK object.
    internal static class WwpTemplateXml
    {
        internal const string SourceProperty = "WWPTemplate_TemplateXml";
        internal static readonly Guid ObjectType = new Guid("083f1b21-5715-45e1-8a8d-ceadef141e02");

        private static XDocument Parse(string source)
        {
            using (var input = new StringReader(source ?? string.Empty))
            using (var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
            }))
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }

        private static IEnumerable<KeyValuePair<string, XElement>> Walk(XElement element, string path)
        {
            yield return new KeyValuePair<string, XElement>(path, element);
            int index = 0;
            foreach (var child in element.Elements())
            {
                foreach (var item in Walk(child, path + "/" + index)) yield return item;
                index++;
            }
        }

        private static bool Editable(XElement element, XAttribute attribute)
            => element.Name == "table" && attribute.Name == "themeClass";

        internal static JArray Project(string source, string selector)
        {
            var nodes = new JArray();
            foreach (var item in Walk(Parse(source).Root, selector))
            {
                var properties = new JObject();
                foreach (var attribute in item.Value.Attributes())
                    properties[attribute.Name.ToString()] = new JObject
                    {
                        ["value"] = attribute.Value, ["valueSource"] = "stored-template-xml",
                        ["editable"] = Editable(item.Value, attribute)
                    };
                nodes.Add(new JObject
                {
                    ["path"] = item.Key, ["name"] = item.Value.Name.ToString(),
                    ["type"] = item.Value.Name.ToString(), ["properties"] = properties,
                    ["isTemplate"] = item.Key == selector
                });
            }
            return nodes;
        }

        internal static JObject Read(JObject template, int offset, int limit, string token)
        {
            string source = (string)template["source"];
            var page = PatternSettingsService.Page(Project(source, (string)template["path"]), offset, limit, token);
            page["template"] = CatalogEntry(template);
            page["sourceLength"] = source.Length;
            page["sourceIncluded"] = offset == 0 && limit == 0;
            if (offset == 0 && limit == 0) page["source"] = source;
            page["effectivePropertiesResolved"] = false;
            page["propertyScope"] = "Stored attributes; WorkWithPlus default resolvers and validators were not invoked.";
            return page;
        }

        internal static JObject CatalogEntry(JObject template)
        {
            var entry = (JObject)template.DeepClone();
            entry.Remove("source");
            return entry;
        }

        internal static JObject Plan(JObject template, JObject args, string token)
        {
            string source = (string)template["source"], selector = (string)template["path"];
            var plan = PatternSettingsService.Plan(Project(source, selector), selector, args, token);
            var element = Walk(Parse(source).Root, selector).Single(n => n.Key == (string)args["nodePath"]).Value;
            var attribute = element.Attribute((string)args["property"]);
            var position = (IXmlLineInfo)attribute;
            int start = LineStart(source, position.LineNumber) + position.LinePosition - 1 + attribute.Name.ToString().Length;
            while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
            if (start == source.Length || source[start++] != '=') throw new InvalidOperationException("XML attribute position could not be verified.");
            while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
            if (start == source.Length || (source[start] != '\'' && source[start] != '"')) throw new InvalidOperationException("XML attribute quote could not be verified.");
            char quote = source[start++];
            int end = source.IndexOf(quote, start);
            if (end < start) throw new InvalidOperationException("XML attribute end could not be verified.");
            string value = (string)args["value"];
            XmlConvert.VerifyXmlChars(value);
            string replacement = value == attribute.Value ? source.Substring(start, end - start)
                : value.Replace("&", "&amp;").Replace("<", "&lt;")
                    .Replace(quote.ToString(), quote == '\'' ? "&apos;" : "&quot;")
                    .Replace("\r", "&#xD;").Replace("\n", "&#xA;").Replace("\t", "&#x9;");
            string proposed = source.Substring(0, start) + replacement + source.Substring(end);
            var verified = Walk(Parse(proposed).Root, selector).Single(n => n.Key == (string)args["nodePath"]).Value;
            if ((string)verified.Attribute(attribute.Name) != value)
                throw new InvalidOperationException("The proposed XML attribute did not round-trip exactly.");
            plan["template"] = CatalogEntry(template);
            plan["textEdit"] = new JObject
            {
                ["offset"] = start, ["length"] = end - start, ["offsetUnit"] = "utf16",
                ["before"] = source.Substring(start, end - start), ["after"] = replacement
            };
            plan["sourceChanged"] = proposed != source;
            plan["validation"] = "Existing table themeClass only. XML was parsed and the replacement verified; every character outside this attribute value is preserved. SDK/WWP resolvers and save validators were not invoked.";
            return plan;
        }

        private static int LineStart(string source, int lineNumber)
        {
            int line = 1, offset = 0;
            while (line < lineNumber && offset < source.Length)
            {
                char ch = source[offset++];
                if (ch == '\r') { if (offset < source.Length && source[offset] == '\n') offset++; line++; }
                else if (ch == '\n') line++;
            }
            if (line != lineNumber) throw new InvalidOperationException("XML line position could not be verified.");
            return offset;
        }
    }
}
