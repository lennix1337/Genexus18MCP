using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class PatternAnalysisService
    {
        private static readonly Guid PatternInstancePartGuid = new Guid("a51ced48-7bee-0001-ab12-04e9e32123d1");
        private readonly ObjectService _objectService;

        public PatternAnalysisService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetWWPStructure(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "The requested object is not available in the active Knowledge Base.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_search",
                        args: new JObject { ["query"] = target },
                        why: "Search for objects matching the name to find the correct identifier.")),
                    target: target);

                // Fast type guard — WWP only applies to WorkWithPlus instances or to
                // Transaction/WebPanel parents that may own one. For Procedure/SDT/
                // Domain/etc., ResolveWWPInstance would still walk model.Objects.GetAll()
                // (10s+ on large KBs) only to return null. Reject upfront.
                string typeName = obj.TypeDescriptor?.Name ?? "";
                bool wwpEligible = string.Equals(typeName, "WorkWithPlus", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(typeName, "Transaction", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(typeName, "WebPanel", StringComparison.OrdinalIgnoreCase);
                if (!wwpEligible)
                {
                    return Models.McpResponse.Err(
                        code: "TypeNotEligibleForWWP",
                        message: $"Object type not eligible for WorkWithPlus pattern.",
                        hint: $"pattern_metadata applies to WorkWithPlus / Transaction / WebPanel objects; '{target}' is a {typeName}.",
                        nextSteps: new JArray(Models.McpResponse.NextStep(
                            tool: "genexus_inspect",
                            args: new JObject { ["name"] = target },
                            why: "Inspect the object to confirm its type and available parts.")),
                        target: target,
                        extra: new JObject { ["objectName"] = obj.Name, ["objectType"] = typeName });
                }

                KBObject instanceObj = ResolveWWPInstance(obj);
                if (instanceObj == null) return Models.McpResponse.Err(
                    code: "WWPInstanceNotFound",
                    message: "WorkWithPlus instance not found.",
                    hint: "No WorkWithPlus instance was resolved for the requested object.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_list_objects",
                        args: new JObject { ["type"] = "WorkWithPlus" },
                        why: "List all WorkWithPlus instances to find the one associated with this object.")),
                    target: target);

                var part = FindPatternPart(instanceObj, "PatternInstance");
                if (part == null) return Models.McpResponse.Err(
                    code: "PatternInstancePartNotFound",
                    message: "PatternInstance part not found.",
                    hint: "The WorkWithPlus instance does not expose a PatternInstance part.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = instanceObj.Name },
                        why: "Returns availableParts so you can identify the correct part name.")),
                    target: target,
                    extra: new JObject
                    {
                        ["objectName"] = instanceObj.Name,
                        ["objectType"] = instanceObj.TypeDescriptor?.Name,
                        ["availableParts"] = new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(instanceObj))
                    });

                string xml = ExtractEditablePatternXml(part, instanceObj);
                if (string.IsNullOrEmpty(xml)) return Models.McpResponse.Err(
                    code: "PatternInstanceXmlUnavailable",
                    message: "PatternInstance XML not available.",
                    hint: "The PatternInstance content could not be extracted from the resolved WorkWithPlus instance.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = instanceObj.Name, ["part"] = "PatternInstance" },
                        why: "Attempt a direct part read to surface the raw content or a more specific error.")),
                    target: target,
                    extra: new JObject { ["objectName"] = instanceObj.Name, ["objectType"] = instanceObj.TypeDescriptor?.Name });

                var result = ParseWWPXml(xml);
                result["resolvedObject"] = instanceObj.Name;
                result["resolvedType"] = instanceObj.TypeDescriptor?.Name;
                result["rawSnippet"] = xml.Length > 5000 ? xml.Substring(0, 5000) : xml;
                return Models.McpResponse.Ok(target: target, code: "PatternMetadataRead", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "PatternMetadataFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log; the WorkWithPlus object or PatternInstance XML may be corrupt.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_lifecycle",
                        args: new JObject { ["action"] = "index", ["force"] = true },
                        why: "Rebuilds the index which may fix object resolution failures.")),
                    target: target);
            }
        }

        public static bool IsPatternPart(string partName)
        {
            return string.Equals(partName, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "PatternVirtual", StringComparison.OrdinalIgnoreCase);
        }

        public KBObject ResolveWWPInstance(KBObject obj)
        {
            if (obj == null) return null;
            if (obj.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase)) return obj;

            var model = obj.Model;
            if (model == null) return null;

            string instanceName = "WorkWithPlus" + obj.Name;
            var namedMatch = _objectService?.FindObject(instanceName, "WorkWithPlus");
            if (namedMatch != null) return namedMatch;

            try
            {
                var childMatch = model.Objects.GetChildren(obj)
                    .FirstOrDefault(o => o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (childMatch != null) return childMatch;
            }
            catch
            {
            }

            return null;
        }

        public KBObjectPart FindPatternPart(KBObject instanceObj, string partName)
        {
            if (instanceObj == null || string.IsNullOrWhiteSpace(partName)) return null;

            return instanceObj.Parts.Cast<KBObjectPart>().FirstOrDefault(p =>
            {
                if (string.Equals(partName, "PatternInstance", StringComparison.OrdinalIgnoreCase))
                {
                    return p.Name.Equals("PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                           p.GetType().Name.Contains("PatternInstance") ||
                           p.Type.Equals(PatternInstancePartGuid);
                }

                return p.Name.Equals(partName, StringComparison.OrdinalIgnoreCase) ||
                       p.GetType().Name.IndexOf(partName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       string.Equals(p.TypeDescriptor?.Name, partName, StringComparison.OrdinalIgnoreCase);
            });
        }

        public string ReadPatternPartXml(KBObject obj, string partName, out KBObject resolvedObject, out string resolvedPartName)
        {
            resolvedObject = ResolveWWPInstance(obj);
            resolvedPartName = partName;
            if (resolvedObject == null) return null;

            var part = FindPatternPart(resolvedObject, partName);
            if (part == null) return null;

            resolvedPartName = !string.IsNullOrWhiteSpace(part.Name) ? part.Name : partName;
            return ExtractEditablePatternXml(part, resolvedObject);
        }

        /// <summary>
        /// Reads a PatternInstance through a fresh SDK resolution for both the
        /// requested object and the resolved WorkWithPlus instance. Verification
        /// must not invalidate only the parent and then re-use a cached child.
        /// </summary>
        public string ReadPatternPartXmlFresh(KBObject obj, string partName, out KBObject resolvedObject, out string resolvedPartName)
        {
            resolvedObject = ResolveWWPInstanceFresh(obj);
            resolvedPartName = partName;
            if (resolvedObject == null) return null;

            var part = FindPatternPart(resolvedObject, partName);
            if (part == null) return null;

            resolvedPartName = !string.IsNullOrWhiteSpace(part.Name) ? part.Name : partName;
            return ExtractEditablePatternXml(part, resolvedObject);
        }

        private KBObject ResolveWWPInstanceFresh(KBObject obj)
        {
            if (obj == null) return null;
            if (obj.TypeDescriptor?.Name?.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase) == true)
                return obj;

            string instanceName = "WorkWithPlus" + obj.Name;
            var namedMatch = _objectService?.FindObjectFresh(instanceName, "WorkWithPlus");
            if (namedMatch != null) return namedMatch;

            try
            {
                var childMatch = obj.Model?.Objects.GetChildren(obj)
                    .FirstOrDefault(o => o.TypeDescriptor?.Name?.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase) == true);
                if (childMatch != null)
                    return _objectService?.FindObjectFresh(childMatch.Name, "WorkWithPlus");
            }
            catch
            {
            }

            return null;
        }

        public string BuildPatternPartEnvelope(KBObject obj, string partName, string innerXml, out KBObject resolvedObject, out KBObjectPart resolvedPart)
        {
            resolvedObject = ResolveWWPInstance(obj);
            resolvedPart = null;
            if (resolvedObject == null) return null;

            resolvedPart = FindPatternPart(resolvedObject, partName);
            if (resolvedPart == null) return null;

            string partXml = SerializeEditablePatternEnvelope(resolvedObject, resolvedPart);
            if (string.IsNullOrWhiteSpace(partXml)) return null;

            try
            {
                var outer = XDocument.Parse(partXml, LoadOptions.PreserveWhitespace);
                var dataElement = outer.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
                if (dataElement == null) return null;

                dataElement.ReplaceNodes(new XCData(innerXml));
                return outer.ToString(SaveOptions.DisableFormatting);
            }
            catch
            {
                return null;
            }
        }

        private KBObject FindWWPInstance(Transaction trn)
        {
            var model = trn.Model;
            
            // Search by name using SDK compliant way
            // In GX SDK, names are resolved via ResolveName or looking into the collection
            var instance = model.Objects.GetAll()
                                .FirstOrDefault(o => o.Name.Equals("WorkWithPlus" + trn.Name, StringComparison.OrdinalIgnoreCase));
            
            if (instance != null) return instance;

            // Search by children
            return model.Objects.GetChildren(trn)
                        .FirstOrDefault(o => o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase));
        }

        private string ExtractEditablePatternXml(KBObjectPart part, KBObject instanceObj)
        {
            string xml = TryReadPatternInnerXml(part);
            if (!string.IsNullOrEmpty(xml) && !LooksLikePartPropertiesOnly(xml)) return xml;

            try
            {
                return ExtractInnerXmlFromSerializedFragment(SerializeEditablePatternEnvelope(instanceObj, part));
            }
            catch
            {
                return null;
            }
        }

        private string TryReadPatternInnerXml(KBObjectPart part)
        {
            if (part == null) return null;

            if (part is ISource sourcePart && !string.IsNullOrWhiteSpace(sourcePart.Source))
            {
                return NormalizeXml(sourcePart.Source);
            }

            try
            {
                dynamic dPart = part;
                string[] propertyNames = { "InstanceXml", "Specification", "Settings" };
                foreach (string propertyName in propertyNames)
                {
                    try
                    {
                        string candidate = dPart.Properties.Get<string>(propertyName);
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            return NormalizeXml(candidate);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return ExtractInnerXmlFromSerializedFragment(SerializePatternPart(part));
        }

        public string ExtractEditablePatternXmlForDiagnostics(KBObjectPart part)
        {
            return ExtractInnerXmlFromSerializedFragment(SerializePatternPart(part));
        }

        private string SerializePatternPart(KBObjectPart part)
        {
            try
            {
                return part.SerializeToXml();
            }
            catch
            {
                return null;
            }
        }

        private string SerializeEditablePatternEnvelope(KBObject instanceObj, KBObjectPart part)
        {
            if (instanceObj == null || part == null) return null;

            try
            {
                using (var writer = new System.IO.StringWriter())
                {
                    instanceObj.Serialize(writer);
                    string objectXml = writer.ToString();
                    var doc = XDocument.Parse(objectXml, LoadOptions.PreserveWhitespace);
                    var partElement = doc.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName.Equals("Part", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)e.Attribute("type"), part.Type.ToString(), StringComparison.OrdinalIgnoreCase));
                    return partElement?.ToString(SaveOptions.DisableFormatting);
                }
            }
            catch
            {
                return SerializePatternPart(part);
            }
        }

        private string ExtractInnerXmlFromSerializedFragment(string serializedXml)
        {
            if (string.IsNullOrWhiteSpace(serializedXml)) return null;

            try
            {
                var outer = XDocument.Parse(serializedXml, LoadOptions.PreserveWhitespace);
                var dataElement = outer.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
                if (dataElement != null && !string.IsNullOrWhiteSpace(dataElement.Value))
                {
                    return NormalizeXml(dataElement.Value);
                }
            }
            catch
            {
            }

            return NormalizeXml(serializedXml);
        }

        private string NormalizeXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return xml;

            try
            {
                return XDocument.Parse(xml, LoadOptions.PreserveWhitespace).ToString();
            }
            catch
            {
                return xml;
            }
        }

        private bool LooksLikePartPropertiesOnly(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                return doc.Root != null &&
                       doc.Root.Name.LocalName.Equals("Properties", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private JObject ParseWWPXml(string xml)
        {
            var result = new JObject();
            try
            {
                string realXml = xml;
                if (xml.Contains("<![CDATA["))
                {
                    int start = xml.IndexOf("<![CDATA[") + 9;
                    int end = xml.LastIndexOf("]]>");
                    if (end > start)
                    {
                        realXml = xml.Substring(start, end - start);
                    }
                }

                XDocument doc = XDocument.Parse(realXml);
                var root = doc.Root;
                
                result["template"] = root?.Attribute("template")?.Value ?? root?.Attribute("Template")?.Value;
                
                var attributes = new JArray();
                foreach (var att in doc.Descendants().Where(e => e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase)))
                {
                    var aObj = new JObject();
                    string rawAtt = att.Attribute("attribute")?.Value ?? "";
                    // WWP format is often GUID-AttributeName
                    string name = rawAtt.Contains("-") ? rawAtt.Substring(rawAtt.LastIndexOf("-") + 1) : rawAtt;
                    
                    aObj["name"] = name;
                    aObj["description"] = att.Attribute("description")?.Value;
                    aObj["visible"] = att.Attribute("visible")?.Value;
                    aObj["readOnly"] = att.Attribute("readOnly")?.Value;
                    attributes.Add(aObj);
                }
                result["attributes"] = attributes;

                var variables = new JArray();
                foreach (var varNode in doc.Descendants().Where(e => e.Name.LocalName.Equals("variable", StringComparison.OrdinalIgnoreCase)))
                {
                    var vObj = new JObject();
                    vObj["name"] = varNode.Attribute("name")?.Value ?? varNode.Attribute("Name")?.Value;
                    vObj["description"] = varNode.Attribute("description")?.Value;
                    vObj["readOnly"] = varNode.Attribute("readOnly")?.Value;
                    variables.Add(vObj);
                }
                result["variables"] = variables;

                var actions = new JArray();
                foreach (var act in doc.Descendants().Where(e => e.Name.LocalName.Contains("Action")))
                {
                    var actObj = new JObject();
                    actObj["name"] = act.Attribute("name")?.Value ?? act.Attribute("Name")?.Value;
                    actObj["caption"] = act.Attribute("caption")?.Value ?? act.Attribute("Caption")?.Value;
                    actions.Add(actObj);
                }
                result["actions"] = actions;

                var tabs = new JArray();
                foreach (var tab in doc.Descendants().Where(e => e.Name.LocalName.Equals("tab", StringComparison.OrdinalIgnoreCase)))
                {
                    var tObj = new JObject();
                    tObj["name"] = tab.Attribute("Name")?.Value ?? tab.Attribute("caption")?.Value;
                    tObj["caption"] = tab.Attribute("caption")?.Value;
                    tabs.Add(tObj);
                }
                result["tabs"] = tabs;

                var grids = new JArray();
                foreach (var grid in doc.Descendants().Where(e => e.Name.LocalName.Equals("grid", StringComparison.OrdinalIgnoreCase)))
                {
                    var gObj = new JObject();
                    gObj["name"] = grid.Attribute("name")?.Value ?? grid.Attribute("Name")?.Value;
                    grids.Add(gObj);
                }
                result["grids"] = grids;
            }
            catch (Exception ex)
            {
                result["parsingError"] = ex.Message;
            }
            return result;
        }
    }
}
