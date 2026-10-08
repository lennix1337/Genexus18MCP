using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        private static bool IsVariableGrid(JObject args) => args != null
            && string.IsNullOrWhiteSpace(args["collection"]?.ToString())
            && string.IsNullOrWhiteSpace(args["sdt"]?.ToString());

        private static string VariableGridName(JObject args) => args["gridName"]?.ToString()?.Trim() ?? "Grid";

        internal static JObject ValidateVariableGridArgs(JObject args, out List<GridColumn> columns)
        {
            columns = new List<GridColumn>();
            if ((args["gridName"] != null && args["gridName"].Type != JTokenType.String)
                || !Regex.IsMatch(VariableGridName(args), "^[A-Za-z_][A-Za-z0-9_]*$"))
                return Error("InvalidGridName", "gridName must be a GeneXus control identifier.");
            if (string.IsNullOrWhiteSpace(args["containerName"]?.ToString()))
                return Error("MissingGridContainer", "containerName is required (a table of the instance).");
            if (args["deleteAction"] != null && args["deleteAction"].Type != JTokenType.Null
                && (args["deleteAction"].Type != JTokenType.Boolean || args["deleteAction"].Value<bool>()))
                return Error("InvalidDeleteAction", "deleteAction is available only for SDT collection grids.");
            if (!(args["columns"] is JArray array) || array.Count == 0)
                return Error("MissingGridColumns", "columns must contain {variable, basicType, description, readOnly, length, decimals} objects.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] basicTypes = { "Numeric", "Character", "VarChar", "LongVarChar", "Date", "DateTime", "Boolean", "GUID", "Image", "Audio", "Video", "Blob" };
            foreach (JToken entry in array)
            {
                var column = entry as JObject;
                string name = column?["variable"]?.ToString()?.Trim().TrimStart('&');
                if (column?["variable"]?.Type != JTokenType.String || string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                    return Error("InvalidGridColumn", "Every variable column requires a variable identifier.");
                if (!seen.Add(name)) return Error("DuplicateGridColumn", "Variable '" + name + "' appears more than once.");
                if (column.Properties().Any(p => !new[] { "variable", "basicType", "description", "readOnly", "length", "decimals" }.Contains(p.Name)))
                    return Error("InvalidGridColumn", "Unsupported variable-column property.");
                if (column["description"] != null && column["description"].Type != JTokenType.String)
                    return Error("InvalidGridColumn", "description must be a string.");
                string basicType = basicTypes.FirstOrDefault(t => string.Equals(t, column["basicType"]?.ToString(), StringComparison.OrdinalIgnoreCase));
                if (basicType == null) return Error("InvalidGridVariableType", "Every variable column requires a supported basicType.");
                bool sized = basicType == "Numeric" || basicType == "Character" || basicType == "VarChar" || basicType == "LongVarChar";
                JToken lengthToken = column["length"];
                if ((sized && lengthToken == null) || (lengthToken != null && (lengthToken.Type != JTokenType.Integer
                    || !int.TryParse(lengthToken.ToString(), out int size) || size <= 0)) || (!sized && lengthToken != null))
                    return Error("InvalidGridVariableLength", "length must be a positive integer for Numeric/Character/VarChar/LongVarChar only.");
                JToken decimals = column["decimals"];
                if (decimals != null && (basicType != "Numeric" || decimals.Type != JTokenType.Integer
                    || !int.TryParse(decimals.ToString(), out int scale) || scale < 0 || scale >= lengthToken.Value<int>()))
                    return Error("InvalidGridVariableDecimals", "decimals must be non-negative and smaller than Numeric length.");
                if (column["readOnly"] != null && column["readOnly"].Type != JTokenType.Boolean)
                    return Error("InvalidGridVariableReadOnly", "readOnly must be true or false.");
                var variable = new XElement("gridVariable", new XAttribute("name", name),
                    new XAttribute("description", column["description"]?.ToString() ?? name),
                    new XAttribute("dataType", "Basic"), new XAttribute("basicType", basicType),
                    new XAttribute("readOnly", column["readOnly"]?.Value<bool>() == false ? "False" : "True"));
                if (sized) variable.SetAttributeValue(basicType == "Numeric" ? "basicLength" : basicType == "LongVarChar" ? "basicLVCLength" : "basicCLength", lengthToken.ToString());
                if (basicType == "Numeric") variable.SetAttributeValue("basicDecimals", decimals?.ToString() ?? "0");
                columns.Add(new GridColumn { Item = name, Description = Attr(variable, "description"), Variable = variable });
            }
            return null;
        }

        private static XElement FindVariableGrid(XDocument doc, JObject args) => doc.Descendants().FirstOrDefault(e => Is(e, "grid")
            && string.Equals(Attr(e, "name"), VariableGridName(args), StringComparison.OrdinalIgnoreCase));

        private static JObject ApplyVariableGridXml(XDocument document, JObject args, IList<GridColumn> columns)
        {
            if (document == null) return Error("InvalidPatternInstance", "PatternInstance XML is required.");
            var containers = FindFormContainers(document, args["containerName"].ToString().Trim()).ToList();
            if (containers.Count != 1) return Error(containers.Count == 0 ? "GridContainerNotFound" : "GridContainerAmbiguous", "containerName must resolve to exactly one table.");
            XElement container = containers[0];
            if (!Is(container, "table")) return Error("GridContainerNotTable", "A grid goes in a table.");
            if (FindVariableGrid(document, args) != null) return Error("GridAlreadyExists", "A grid with this gridName already exists.");
            XNamespace ns = container.Name.Namespace;
            var grid = new XElement(ns + "grid", new XAttribute("name", VariableGridName(args)));
            foreach (GridColumn column in columns) grid.Add(new XElement(ns + "gridVariable", column.Variable.Attributes()));
            container.Add(grid);
            return new JObject { ["changed"] = true, ["gridName"] = VariableGridName(args), ["columns"] = new JArray(columns.Select(c => c.Item)) };
        }

        internal static JObject VerifyVariableGrid(XDocument after, JObject args, IList<GridColumn> columns)
        {
            var containers = FindFormContainers(after, args["containerName"].ToString().Trim()).ToList();
            var grids = containers.SelectMany(c => c.Elements().Where(e => Is(e, "grid") && Attr(e, "name").Equals(VariableGridName(args), StringComparison.OrdinalIgnoreCase))).ToList();
            if (containers.Count != 1 || grids.Count != 1 || !string.IsNullOrEmpty(Attr(grids[0], "SDTCollection")))
                return Error("WwpGridNotPersisted", "The variable grid was not retained in the requested table.");
            var persisted = grids[0].Elements().Where(e => Is(e, "gridVariable")).ToList();
            if (grids[0].Elements().Count() != columns.Count || persisted.Count != columns.Count
                || persisted.Any(e => !string.IsNullOrEmpty(Attr(e, "domain")) || !string.IsNullOrEmpty(Attr(e, "sdtItem")))
                || persisted.Where((e, i) => columns[i].Variable.Attributes().Any(a => !string.Equals(Attr(e, a.Name.LocalName), a.Value, StringComparison.Ordinal))).Any())
                return Error("WwpGridNotPersisted", "A variable column lost its identity, order, type, description or readOnly value.");
            return new JObject { ["confirmed"] = true };
        }

        private static JObject ProjectAddedGrid(XDocument document, JObject args, string collection) => IsVariableGrid(args)
            ? ProjectGridElement(FindVariableGrid(document, args)) : ProjectGridOver(document, collection);

        internal static JObject ApplyNativeVariableGrid(object part, JObject args, IList<GridColumn> columns)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return Error("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject error = ResolveNativeContainer(root, args["containerName"].ToString().Trim(), out object container);
            if (error != null) return error;
            if (!IsNativeTable(container)) return Error("GridContainerNotTable", "A grid goes in a table.");
            Action mutation = () =>
            {
                object grid = CreateNativeChild(container, "grid");
                SetNativeAttribute(grid, "name", VariableGridName(args));
                foreach (GridColumn column in columns)
                {
                    object variable = CreateNativeChild(grid, "gridVariable");
                    foreach (XAttribute attribute in column.Variable.Attributes())
                    {
                        object value = attribute.Name.LocalName == "readOnly" ? (object)(attribute.Value == "True")
                            : attribute.Name.LocalName.StartsWith("basic", StringComparison.Ordinal) && attribute.Name.LocalName != "basicType"
                                ? int.Parse(attribute.Value, CultureInfo.InvariantCulture) : (object)attribute.Value;
                        if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(variable, attribute.Name.LocalName, value))
                            throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected variable property '" + attribute.Name.LocalName + "'.");
                    }
                    ExecuteElementCommand(grid, variable, "AddElementCommand", null);
                }
                ExecuteElementCommand(container, grid, "AddElementCommand", null);
            };
            MethodInfo update = part.GetType().GetMethod("ExecuteUpdate", new[] { typeof(string), typeof(Action) });
            if (update != null) update.Invoke(part, new object[] { "genexus_wwp add_grid variables", mutation });
            else mutation();
            return new JObject { ["changed"] = true };
        }
    }
}
