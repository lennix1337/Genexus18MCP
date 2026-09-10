using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Artech.Architecture.Common.Objects;
using Artech.Packages.Patterns.Objects;
using Artech.Packages.Patterns.Specification;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Settings have a separate SDK serialization contract from ordinary KB parts.
    // Never route them through the instance editor, reconciler or apply pipeline.
    public sealed class PatternSettingsService
    {
        private readonly ObjectService _objects;
        public PatternSettingsService(ObjectService objects) { _objects = objects; }

        internal static string Serialize(PatternSettings settings)
        {
            var root = settings.PatternPart?.RootElement
                ?? throw new InvalidOperationException("The SDK did not expose the Settings root. This does not mean it is empty.");
            var doc = new XmlDocument { XmlResolver = null };
            var element = doc.CreateElement(root.Name);
            doc.AppendChild(element);
            root.ToXml(element, SerializationFormat.Internal);
            var text = new StringBuilder();
            using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
                doc.Save(writer);
            return text.ToString();
        }

        internal static string Token(string identity, string xml, JArray nodes)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    identity + "\n" + xml + "\n" + nodes.ToString(Newtonsoft.Json.Formatting.None))));
        }

        internal static bool Protected(string name)
        {
            return string.IsNullOrEmpty(name) || name.StartsWith("Default", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ChildrenOrderedList", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Id", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Guid", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<KeyValuePair<string, PatternInstanceElement>> Walk(PatternInstanceElement root, string path = "/")
        {
            yield return new KeyValuePair<string, PatternInstanceElement>(path, root);
            int index = 0;
            foreach (PatternInstanceElement child in root.Children)
            {
                foreach (var node in Walk(child, path.TrimEnd('/') + "/" + index)) yield return node;
                index++;
            }
        }

        private static JArray Project(PatternSettings settings)
        {
            var nodes = new JArray();
            foreach (var item in Walk(settings.PatternPart.RootElement))
            {
                var node = item.Value;
                var properties = new JObject();
                foreach (var attribute in node.Specification.Attributes)
                {
                    var descriptor = node.Attributes.GetPropertyDescriptor(attribute.PropertyDescriptorName);
                    properties[attribute.Name] = new JObject
                    {
                        ["value"] = attribute.GetValueString(node),
                        ["type"] = attribute.Type,
                        ["displayName"] = attribute.DisplayName,
                        ["editable"] = !Protected(attribute.Name) && !attribute.IsKeyAttribute
                            && attribute.Visible && !attribute.ReadOnly && descriptor != null
                            && descriptor.IsApplicable && !descriptor.IsReadOnly
                    };
                }
                nodes.Add(new JObject
                {
                    ["path"] = item.Key, ["name"] = node.Name, ["type"] = node.Type,
                    ["caption"] = node.Caption, ["properties"] = properties,
                    ["isTemplate"] = node.Type == "InstanceTemplate" || node.Type == "WPInstanceTemplate"
                        || node.Type == "RPTemplate"
                });
            }
            return nodes;
        }

        internal static JObject Page(IEnumerable<JToken> nodes, int offset, int limit, string token)
        {
            if (offset < 0 || limit < 0) throw new ArgumentException("offset and limit must be nonnegative.");
            var all = nodes.ToList();
            var page = new JArray(limit == 0 ? all.Skip(offset) : all.Skip(offset).Take(limit));
            bool more = offset + page.Count < all.Count;
            return new JObject
            {
                ["nodes"] = page, ["offset"] = offset, ["returned"] = page.Count,
                ["totalNodes"] = all.Count, ["truncated"] = more,
                ["nextOffset"] = more ? (JToken)new JValue(offset + page.Count) : JValue.CreateNull(),
                ["versionToken"] = token, ["explicitFullRead"] = limit == 0,
                ["saved"] = false, ["saveAvailable"] = false
            };
        }

        internal static JObject Plan(JArray nodes, string templatePath, JObject args, string token)
        {
            string expected = (string)(args["baseVersion"] ?? args["expectedVersion"] ?? args["versionToken"]);
            if (!string.IsNullOrEmpty(expected) && expected != token)
                throw new InvalidOperationException("VersionConflict: Settings changed; read all pages again.");
            string path = (string)args["nodePath"];
            string property = (string)args["property"];
            if (path == null || !(path == templatePath || path.StartsWith(templatePath.TrimEnd('/') + "/", StringComparison.Ordinal)))
                throw new ArgumentException("nodePath must be inside the selected template.");
            var node = nodes.OfType<JObject>().SingleOrDefault(n => (string)n["path"] == path);
            var definition = node?["properties"]?[property ?? ""];
            if (Protected(property) || definition == null || (bool?)definition["editable"] != true)
                throw new ArgumentException("Property is unknown, internal, identifying, inapplicable or read-only.");
            if (args["value"]?.Type != JTokenType.String)
                throw new ArgumentException("value must be a string in the SDK property format.");
            return new JObject
            {
                ["versionToken"] = token, ["templatePath"] = templatePath,
                ["diff"] = new JArray(new JObject { ["nodePath"] = path, ["property"] = property,
                    ["before"] = definition["value"].DeepClone(), ["after"] = args["value"].DeepClone() }),
                ["saved"] = false, ["saveAvailable"] = false,
                ["validation"] = "Property eligibility and exact proposed value only; SDK conversion and save validators have not run."
            };
        }

        public string Run(string target, JObject args)
        {
            try
            {
                var seed = _objects.FindObject(target, "Pattern Settings", (string)args["guid"], (string)args["entityKey"], null);
                var settings = seed?.Model.Objects.Get(seed.Key) as PatternSettings;
                if (settings == null) return McpResponse.Err(code: "PatternSettingsNotFound", message: "A persisted Pattern Settings object is required.");
                string xml = Serialize(settings);
                JArray nodes = Project(settings);
                if (Serialize(settings) != xml)
                    return McpResponse.Err(code: "VersionConflict", message: "Settings changed while reading the SDK tree.");
                string token = Token(settings.Model.KB.Location + "|" + settings.Model.Id + "|" + settings.Guid, xml, nodes);
                string expected = (string)(args["baseVersion"] ?? args["expectedVersion"] ?? args["versionToken"]);
                if (!string.IsNullOrEmpty(expected) && expected != token)
                    return McpResponse.Err(code: "VersionConflict", message: "Settings changed; restart the read or dryRun.");
                string action = (string)args["action"];
                var templates = nodes.OfType<JObject>().Where(n => (bool)n["isTemplate"]).ToList();
                if (action == "settings_templates")
                    return McpResponse.Ok(code: "SettingsTemplatesRead", result: Page(templates, (int?)args["offset"] ?? 0, (int?)args["limit"] ?? 50, token));
                string selector = (string)args["template"];
                var matches = templates.Where(n => (string)n["path"] == selector
                    || (string)n["properties"]?["Name"]?["value"] == selector
                    || (string)n["caption"] == selector).ToList();
                if (matches.Count != 1) return McpResponse.Err(code: "TemplateNotUnique", message: "Select exactly one template using its catalog path.");
                string templatePath = (string)matches[0]["path"];
                if (action == "settings_read")
                    return McpResponse.Ok(code: "SettingsTemplateRead", result: Page(nodes.Where(n => (string)n["path"] == templatePath
                        || ((string)n["path"]).StartsWith(templatePath + "/", StringComparison.Ordinal)),
                        (int?)args["offset"] ?? 0, (int?)args["limit"] ?? 50, token));
                if (action != "settings_edit") return McpResponse.Err(code: "SettingsUnknownAction", message: "Unknown Settings operation.");
                JObject plan = Plan(nodes, templatePath, args, token);
                return FinishEdit(plan, (bool?)args["dryRun"] == true, expected);
            }
            catch (Exception ex) { return McpResponse.Err(code: "SettingsReadOrPlanFailed", message: ex.Message); }
        }

        internal static string FinishEdit(JObject plan, bool dryRun, string expected)
        {
            if (dryRun) return McpResponse.Ok(code: "SettingsDryRun", result: plan);
            if (string.IsNullOrEmpty(expected)) return McpResponse.Err(code: "VersionRequired", message: "Supply the dryRun versionToken as baseVersion.");
            // Fail closed: skipping Apply calls or setting SkipApplyPattern does not
            // prove WWP save-event isolation or cross-process compare-and-swap.
            return McpResponse.Err(code: "SettingsIsolationUnverified",
                message: "Save refused: SDK/WWP event isolation and atomic concurrency have not been certified. No property was assigned and Save was not called.", extra: plan);
        }
    }
}
