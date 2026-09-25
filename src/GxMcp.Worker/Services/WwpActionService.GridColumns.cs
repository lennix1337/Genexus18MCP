using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        internal static bool IsGridColumnOperation(string operation)
            => string.Equals(operation, "move_grid_column", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operation, "add_grid_variable", StringComparison.OrdinalIgnoreCase);

        /// <summary>Projects a move request onto the authoritative PatternInstance XML.</summary>
        internal static JObject ApplyMoveGridColumnXml(XDocument document, JObject args)
        {
            if (document == null) return Error("InvalidPatternInstance", "PatternInstance XML is required.");
            string requested = args?["attribute"]?.ToString() ?? args?["variable"]?.ToString();
            if (string.IsNullOrWhiteSpace(requested))
                return Error("MissingGridColumn", "attribute or variable is required for move_grid_column.");
            JObject gridError = FindRequestedGrid(document, args?["gridPath"]?.ToString(), out XElement grid);
            if (gridError != null) return gridError;
            var matchingColumns = FindGridColumns(grid, requested);
            if (matchingColumns.Count == 0) return Error("GridColumnNotFound", "Grid column '" + requested + "' was not found.");
            if (matchingColumns.Count != 1) return Error("AmbiguousGridColumn", "Grid column '" + requested + "' matched more than one element.");
            var column = matchingColumns[0];
            string pendingCaption = null;
            string requestedAttribute = args?["attribute"]?.ToString();
            string requestedVariable = args?["variable"]?.ToString();
            if (!string.IsNullOrWhiteSpace(requestedAttribute)
                && !Attr(column, "attribute").Equals(requestedAttribute, StringComparison.OrdinalIgnoreCase))
                return Error("GridColumnIdentityMismatch", "attribute does not identify the selected grid column.");
            if (!string.IsNullOrWhiteSpace(requestedVariable)
                && !Attr(column, "variable").Equals(requestedVariable, StringComparison.OrdinalIgnoreCase)
                && !Attr(column, "name").Equals(requestedVariable, StringComparison.OrdinalIgnoreCase))
                return Error("GridColumnIdentityMismatch", "variable does not identify the selected grid column.");
            if (args?["caption"] != null) pendingCaption = args["caption"].ToString();

            string before = args?["before"]?.ToString();
            if (!string.IsNullOrWhiteSpace(before))
            {
                var beforeColumns = FindGridColumns(grid, before);
                if (beforeColumns.Count == 0) return Error("GridColumnBeforeNotFound", "Before-column '" + before + "' was not found.");
                if (beforeColumns.Count != 1) return Error("AmbiguousGridColumn", "Before-column '" + before + "' matched more than one element.");
                var anchor = beforeColumns[0];
                if (anchor == column) return Error("GridColumnPositionUnchanged", "The requested column is already before itself.");
                column.Remove();
                anchor.AddBeforeSelf(column);
            }
            else if (args?["position"] != null)
            {
                int position = Math.Max(0, args["position"].ToObject<int>());
                var peers = grid.Elements().Where(e => Is(e, "gridAttribute") || Is(e, "gridVariable")).Where(e => e != column).ToList();
                column.Remove();
                if (position >= peers.Count) grid.Add(column);
                else peers[position].AddBeforeSelf(column);
            }

            // Written only after every selector and anchor resolved, so a
            // rejected request can never leave a half-applied caption behind.
            if (pendingCaption != null) column.SetAttributeValue("description", pendingCaption);

            return new JObject
            {
                ["changed"] = true,
                ["operation"] = "move_grid_column",
                ["column"] = requested,
                ["before"] = before,
                ["position"] = args?["position"]?.ToObject<int?>()
            };
        }

        /// <summary>Projects a presentation-variable addition onto the PatternInstance XML.</summary>
        internal static JObject ApplyAddGridVariableXml(XDocument document, JObject args)
        {
            if (document == null) return Error("InvalidPatternInstance", "PatternInstance XML is required.");
            string name = args?["variable"]?.ToString() ?? args?["variableName"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return Error("MissingGridVariable", "variable is required for add_grid_variable.");
            string reference = args?["variableReference"]?.ToString() ?? args?["_variableReference"]?.ToString();
            if (string.IsNullOrWhiteSpace(reference))
                return Error("GridVariableIdentityRequired", "A verified variableReference (GUID-Name) is required; the server will not invent an SDK identity.");
            JObject gridError = FindRequestedGrid(document, args?["gridPath"]?.ToString(), out XElement grid);
            if (gridError != null) return gridError;
            if (FindGridColumn(grid, name) != null)
                return Error("GridVariableAlreadyExists", "Grid variable '" + name + "' already exists.");
            // The name check alone would let a second column claim the same SDK
            // variable identity, so the reference must be free as well.
            if (FindGridColumn(grid, reference) != null)
                return Error("GridVariableReferenceInUse", "Grid variable reference '" + reference + "' is already bound to a column.");

            string caption = args?["caption"]?.ToString();
            string basicType = args?["basicType"]?.ToString() ?? args?["type"]?.ToString() ?? "VarChar";
            if (!string.Equals(basicType, "Character", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(basicType, "VarChar", StringComparison.OrdinalIgnoreCase))
                return Error("InvalidGridVariableType", "basicType must be Character or VarChar.");
            int length = args?["length"]?.ToObject<int?>() ?? 0;
            if (length <= 0)
                return Error("InvalidGridVariableLength", "length must be greater than zero for a presentation variable.");

            var element = new XElement(grid.GetDefaultNamespace() + "gridVariable",
                new XAttribute("variable", reference),
                new XAttribute("name", name));
            if (!string.IsNullOrWhiteSpace(caption)) element.SetAttributeValue("description", caption);
            element.SetAttributeValue("basicType", basicType);
            element.SetAttributeValue("basicCLength", length.ToString());

            string before = args?["before"]?.ToString();
            if (!string.IsNullOrWhiteSpace(before))
            {
                var anchor = FindGridColumn(grid, before);
                if (anchor == null) return Error("GridColumnBeforeNotFound", "Before-column '" + before + "' was not found.");
                anchor.AddBeforeSelf(element);
            }
            else grid.Add(element);

            return new JObject
            {
                ["changed"] = true,
                ["operation"] = "add_grid_variable",
                ["variable"] = name,
                ["variableReference"] = reference,
                ["caption"] = caption,
                ["basicType"] = basicType,
                ["length"] = length > 0 ? (JToken)length : JValue.CreateNull()
            };
        }

        private string VerifyGridProjection(KBObject host, JObject requested, JObject args)
        {
            if (host == null) return "The owning SDK object could not be reread for WebForm projection verification.";
            string parentXml;
            try { parentXml = WebFormXmlHelper.ReadEditableXml(host); }
            catch (Exception ex) { return "The owning WebForm could not be reread: " + ex.Message; }
            if (string.IsNullOrWhiteSpace(parentXml))
                return "The owning object has no readable WebForm projection to verify.";
            XDocument document;
            try { document = XDocument.Parse(parentXml, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { return "The owning WebForm projection is not valid XML: " + ex.Message; }
            JObject gridError = FindRequestedGrid(document, args?["gridPath"]?.ToString(), out XElement actualGrid);
            if (gridError != null)
                return "The owning WebForm projection does not contain the requested grid: " + gridError["error"];
            JObject actual = ProjectGridElement(actualGrid);
            var requestedGrids = requested?["grids"] as JArray ?? new JArray();
            JObject expected = requestedGrids.FirstOrDefault(item =>
                item is JObject candidate
                && (string.IsNullOrWhiteSpace(args?["gridPath"]?.ToString())
                    || string.Equals(candidate["name"]?.ToString(), actual["name"]?.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate["controlName"]?.ToString(), actual["controlName"]?.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate["id"]?.ToString(), actual["id"]?.ToString(), StringComparison.OrdinalIgnoreCase))) as JObject;
            if (expected == null) return "The requested grid state is absent from the projection comparison.";
            var expectedComparable = (JObject)expected.DeepClone();
            var actualComparable = (JObject)actual.DeepClone();
            if (string.IsNullOrWhiteSpace(expectedComparable["customProperties"]?.ToString()))
                expectedComparable.Remove("customProperties");
            if (string.IsNullOrWhiteSpace(actualComparable["customProperties"]?.ToString()))
                actualComparable.Remove("customProperties");
            if (!JToken.DeepEquals(expectedComparable, actualComparable))
                return "The owning WebForm projection does not match the requested grid order/identity.";
            return null;
        }

        internal static bool GridVariableIdentityMatches(
            string variableName, string variableGuid, string requestedName, string reference)
        {
            if (string.IsNullOrWhiteSpace(variableName)
                || string.IsNullOrWhiteSpace(requestedName)
                || string.IsNullOrWhiteSpace(variableGuid)
                || string.IsNullOrWhiteSpace(reference)
                || reference.Length <= 36
                || reference[36] != '-'
                || !Guid.TryParse(reference.Substring(0, 36), out Guid expectedGuid)
                || expectedGuid == Guid.Empty
                || !Guid.TryParse(variableGuid, out Guid actualGuid)
                || actualGuid == Guid.Empty)
                return false;

            string normalizedName = requestedName.TrimStart('&');
            string referenceName = reference.Substring(37);
            if (string.IsNullOrWhiteSpace(referenceName)
                || !string.Equals(referenceName.TrimStart('&'), normalizedName, StringComparison.OrdinalIgnoreCase))
                return false;

            // A declaration name is not an identity: homonymous variables are
            // possible.  Both the requested GUID and the requested name must
            // match the independently reread declaration.
            return expectedGuid == actualGuid
                && string.Equals(variableName.TrimStart('&'), normalizedName, StringComparison.OrdinalIgnoreCase);
        }

        private string VerifyGridVariableDeclaration(IEnumerable<KBObject> hosts, string name, string reference)
        {
            bool sawVariablesPart = false;
            foreach (KBObject host in hosts ?? Enumerable.Empty<KBObject>())
            {
                if (host == null) continue;
                if (PartAccessor.GetVariablesLikePart(host) == null) continue;
                sawVariablesPart = true;
                foreach (object variable in PartAccessor.GetVariableObjects(host))
                {
                    string variableName = PartAccessor.GetVariableName(variable);
                    string variableGuid = null;
                    try
                    {
                        var guidProperty = variable.GetType().GetProperty("Guid",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        variableGuid = guidProperty?.GetValue(variable, null)?.ToString();
                    }
                    catch { }
                    if (GridVariableIdentityMatches(variableName, variableGuid, name, reference))
                        return null;
                }
            }
            return sawVariablesPart
                ? "The presentation variable declaration was not found in the independently reread Variables part."
                : "The owning object exposes no readable Variables part for presentation-variable verification.";
        }

        private JObject ResolveGridVariableReference(JObject args, out string reference)
        {
            reference = args?["variableReference"]?.ToString()
                ?? args?["_variableReference"]?.ToString();
            string variableName = args?["variable"]?.ToString()
                ?? args?["variableName"]?.ToString();

            if (!string.IsNullOrWhiteSpace(reference))
            {
                if (reference.Length <= 37
                    || !Guid.TryParse(reference.Substring(0, 36), out Guid guid))
                {
                    return Error("GridVariableIdentityRequired",
                        "variableReference must be a live SDK GUID-Name identity.");
                }
                string suffix = reference.Substring(36);
                if (suffix.StartsWith("-", StringComparison.Ordinal)) suffix = suffix.Substring(1);
                if (string.IsNullOrWhiteSpace(suffix))
                    return Error("GridVariableIdentityRequired", "variableReference must include the SDK variable name after the GUID.");
                if (!string.Equals(suffix, variableName, StringComparison.OrdinalIgnoreCase))
                {
                    return Error("GridVariableIdentityMismatch",
                        "variableReference name does not match the requested variable.");
                }

                KBObject referenced = _objects.FindObject(null, guid: guid.ToString("D"));
                if (referenced == null)
                {
                    return Error("GridVariableIdentityNotFound",
                        "variableReference does not resolve to a live SDK object.");
                }
                if (!string.IsNullOrWhiteSpace(variableName)
                    && !string.Equals(referenced.Name, variableName, StringComparison.OrdinalIgnoreCase))
                {
                    return Error("GridVariableIdentityMismatch",
                        "variableReference resolves to a different SDK object name.");
                }
                string referencedType = referenced.TypeDescriptor?.Name ?? string.Empty;
                if (!string.Equals(referencedType, "Variable", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(referencedType, "Attribute", StringComparison.OrdinalIgnoreCase))
                {
                    return Error("GridVariableIdentityMismatch",
                        "variableReference must identify a Variable or Attribute SDK object.");
                }
                reference = guid.ToString("D") + "-" + referenced.Name;
                return null;
            }

            return Error("GridVariableIdentityRequired",
                "A verified GUID-Name variableReference is required; a name-only lookup cannot disambiguate homonymous SDK variables.");
        }

        private static JObject FindRequestedGrid(XDocument document, string gridPath, out XElement grid)
        {
            grid = null;
            var grids = document.Descendants().Where(e => Is(e, "grid")).ToList();
            if (grids.Count == 0) return Error("GridNotFound", "The PatternInstance has no grid.");
            if (!string.IsNullOrWhiteSpace(gridPath))
            {
                // An absolute type path walked from the document root, with an
                // optional zero-based index per segment resolved among the
                // siblings of that same type. An unindexed segment that matches
                // more than one element is refused rather than guessed.
                if (!Regex.IsMatch(gridPath, @"\A/[A-Za-z_][A-Za-z0-9_]*(\[[0-9]+\])?(/[A-Za-z_][A-Za-z0-9_]*(\[[0-9]+\])?)*\z"))
                    return Error("InvalidGridPath", "gridPath must be an absolute type path with optional zero-based indices.");
                var cursor = document.Root;
                var segments = gridPath.Substring(1).Split('/');
                for (var i = 0; i < segments.Length; i++)
                {
                    var match = Regex.Match(segments[i], @"\A(?<type>[A-Za-z_][A-Za-z0-9_]*)(?:\[(?<index>[0-9]+)\])?\z");
                    var type = match.Groups["type"].Value;
                    var candidates = i == 0
                        ? (Is(document.Root, type) ? new List<XElement> { document.Root } : new List<XElement>())
                        : cursor.Elements().Where(e => Is(e, type)).ToList();
                    if (match.Groups["index"].Success)
                    {
                        if (!int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                            || index >= candidates.Count)
                            return Error("GridNotFound", "gridPath index is outside its matching children.");
                        cursor = candidates[index];
                    }
                    else
                    {
                        if (candidates.Count != 1)
                            return Error(candidates.Count == 0 ? "GridNotFound" : "AmbiguousGrid", "Each unindexed gridPath segment must match exactly one element.");
                        cursor = candidates[0];
                    }
                }
                if (!Is(cursor, "grid")) return Error("InvalidGridPath", "gridPath must resolve to a grid.");
                grid = cursor;
                return null;
            }
            if (grids.Count != 1) return Error("AmbiguousGrid", "The PatternInstance has more than one grid; provide gridPath.");
            grid = grids[0];
            return null;
        }

        private static List<XElement> FindGridColumns(XElement grid, string identity)
        {
            if (grid == null || string.IsNullOrWhiteSpace(identity)) return new List<XElement>();
            return grid.Elements()
                .Where(e => (Is(e, "gridAttribute") || Is(e, "gridVariable"))
                    && (Attr(e, "attribute").Equals(identity, StringComparison.OrdinalIgnoreCase)
                        || Attr(e, "variable").Equals(identity, StringComparison.OrdinalIgnoreCase)
                        || Attr(e, "name").Equals(identity, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static XElement FindGridColumn(XElement grid, string identity)
            => FindGridColumns(grid, identity).FirstOrDefault();
    }
}
