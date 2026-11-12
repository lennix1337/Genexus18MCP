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

        // A write pins the version it was planned against; a dryRun is the call that returns the token, so it cannot pin one (#638).
        internal static bool GridColumnWriteLacksVersion(string operation, string expectedVersion, JObject args)
            => IsGridColumnOperation(operation) && string.IsNullOrWhiteSpace(expectedVersion)
                && args?["dryRun"]?.ToObject<bool?>() != true;

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

        /// <summary>
        /// Projects a presentation-variable addition onto the PatternInstance XML.
        /// WorkWithPlus keys a grid variable by its <c>name</c> and types it with the attributes of its
        /// <c>Variable</c> element (<c>dataType</c>, <c>basicType</c>, <c>basicLength</c>, <c>basicCLength</c>,
        /// <c>domain</c>, ...): the pattern defines no reference attribute and the SDK gives a variable no GUID,
        /// so the column is identified by the variable name and typed from the declaration the owning object
        /// already has (<paramref name="declared"/>) or, without one, from the explicit arguments (#638).
        /// </summary>
        internal static JObject ApplyAddGridVariableXml(XDocument document, JObject args, DeclaredVariable declared = null)
        {
            if (document == null) return Error("InvalidPatternInstance", "PatternInstance XML is required.");
            JObject planError = PlanGridVariable(args, declared, out GridVariablePlan plan);
            if (planError != null) return planError;
            JObject gridError = FindRequestedGrid(document, args?["gridPath"]?.ToString(), out XElement grid);
            if (gridError != null) return gridError;
            if (FindGridColumn(grid, plan.Name) != null)
                return Error("GridVariableAlreadyExists", "Grid variable '" + plan.Name + "' already exists.");

            XElement element = plan.CreateElement(grid.GetDefaultNamespace());
            string before = args?["before"]?.ToString();
            if (!string.IsNullOrWhiteSpace(before))
            {
                var anchor = FindGridColumn(grid, before);
                if (anchor == null) return Error("GridColumnBeforeNotFound", "Before-column '" + before + "' was not found.");
                anchor.AddBeforeSelf(element);
            }
            else grid.Add(element);

            return plan.Describe();
        }

        /// <summary>What <c>add_grid_variable</c> will write: the column and where its type came from.</summary>
        internal sealed class GridVariablePlan
        {
            internal string Name;
            internal string Caption;
            /// <summary><c>declaration</c> (typed from the owning object's Variables part) or <c>arguments</c>.</summary>
            internal string TypeSource;
            internal XElement Column;
            internal KBObject BasedOnObject;

            internal XElement CreateElement(XNamespace ns)
                => new XElement(ns + Column.Name.LocalName, Column.Attributes().Select(a => new XAttribute(a.Name, a.Value)));

            internal JObject Describe() => new JObject
            {
                ["changed"] = true,
                ["operation"] = "add_grid_variable",
                ["variable"] = Name,
                ["caption"] = Caption,
                ["typeSource"] = TypeSource,
                ["dataType"] = Attr(Column, "dataType"),
                ["basicType"] = string.IsNullOrEmpty(Attr(Column, "basicType")) ? null : (JToken)Attr(Column, "basicType"),
                ["domain"] = string.IsNullOrEmpty(Attr(Column, "domain")) ? null : (JToken)Attr(Column, "domain"),
                ["readOnly"] = Attr(Column, "readOnly").Equals("True", StringComparison.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// Resolves the column <c>add_grid_variable</c> writes. A declared variable decides the type, and an explicit
        /// basicType/length/decimals must agree with it; an undeclared one needs the explicit type, which WorkWithPlus
        /// then declares in the owning object when it projects the grid.
        /// </summary>
        internal static JObject PlanGridVariable(JObject args, DeclaredVariable declared, out GridVariablePlan plan)
        {
            plan = null;
            string raw = args?["variable"]?.ToString() ?? args?["variableName"]?.ToString();
            string name = raw?.Trim().TrimStart('&');
            if (string.IsNullOrWhiteSpace(name))
                return Error("MissingGridVariable", "variable is required for add_grid_variable.");
            if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                return Error("InvalidGridColumn", "variable must be a GeneXus variable identifier.");

            // variableReference was the GUID-Name of the variable; neither exists in the pattern or the SDK, so a client
            // that still sends one is accepted as long as it names this variable.
            string reference = args?["variableReference"]?.ToString();
            if (!string.IsNullOrWhiteSpace(reference) && !ReferenceNamesVariable(reference, name))
                return Error("GridVariableIdentityMismatch",
                    "variableReference '" + reference + "' does not name variable '" + name
                    + "'. WorkWithPlus identifies a grid variable by its name, so variableReference is optional; omit it or end it with '-" + name + "'.");

            string explicitType = args?["basicType"]?.ToString() ?? args?["type"]?.ToString();
            JObject spec = new JObject { ["variable"] = name };
            string typeSource;
            KBObject basedOnObject = null;
            string basedOn = null;

            if (declared != null)
            {
                typeSource = "declaration";
                if (declared.IsCollection)
                    return Error("GridVariableTypeUnsupported", "Variable '&" + name + "' is a collection; a grid column holds one value per row.");
                if (declared.BasicType != null)
                {
                    JObject mismatch = CheckDeclaredTypeAgrees(name, declared, args, explicitType);
                    if (mismatch != null) return mismatch;
                }
                if (!string.IsNullOrEmpty(declared.BasedOnReference))
                {
                    basedOn = declared.BasedOnReference;
                    basedOnObject = declared.BasedOnObject;
                }
                else if (declared.BasicType == null)
                    return Error("GridVariableTypeUnsupported", "Variable '&" + name + "' has type " + declared.DbType
                        + ", which has no WorkWithPlus basic type; use add_grid with an SDT collection for structured data.");
                else
                {
                    spec["basicType"] = declared.BasicType;
                    if (declared.IsSized) spec["length"] = declared.Length;
                    if (declared.BasicType == "Numeric") spec["decimals"] = declared.Decimals;
                }
            }
            else
            {
                typeSource = "arguments";
                if (string.IsNullOrWhiteSpace(explicitType))
                    return Error("GridVariableTypeRequired", "Variable '&" + name + "' is not declared on the owning object: pass basicType"
                        + " (and length for Numeric/Character/VarChar/LongVarChar) so WorkWithPlus creates it with the grid, or declare it first"
                        + " (genexus_variable action=add) and omit the type.");
                spec["basicType"] = explicitType;
                JToken length = PresentArg(args, "length");
                if (length != null) spec["length"] = length;
                JToken decimals = PresentArg(args, "decimals");
                if (decimals != null) spec["decimals"] = decimals;
            }

            string caption = args?["caption"]?.ToString();
            spec["description"] = string.IsNullOrWhiteSpace(caption) ? name : caption;
            JToken readOnly = PresentArg(args, "readOnly");
            if (readOnly != null) spec["readOnly"] = readOnly;

            XElement column;
            if (basedOn != null)
            {
                if (readOnly != null && readOnly.Type != JTokenType.Boolean)
                    return Error("InvalidGridVariableReadOnly", "readOnly must be true or false.");
                column = new XElement("gridVariable", new XAttribute("name", name),
                    new XAttribute("description", (string)spec["description"]),
                    new XAttribute("dataType", "Based on"), new XAttribute("domain", basedOn),
                    new XAttribute("readOnly", readOnly?.Value<bool>() == false ? "False" : "True"));
            }
            else
            {
                JObject invalid = ParseVariableColumnSpec(spec, out column);
                if (invalid != null) return invalid;
            }
            plan = new GridVariablePlan { Name = name, Caption = (string)spec["description"], TypeSource = typeSource, Column = column, BasedOnObject = basedOnObject };
            return null;
        }

        // A client that sends an explicit null for an optional argument means the same as leaving it out.
        private static JToken PresentArg(JObject args, string key)
        {
            JToken token = args?[key];
            return token == null || token.Type == JTokenType.Null ? null : token;
        }

        private static bool ReferenceNamesVariable(string reference, string name)
        {
            string trimmed = reference.Trim();
            return string.Equals(trimmed.TrimStart('&'), name, StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("-" + name, StringComparison.OrdinalIgnoreCase);
        }

        // A declaration decides the type; a caller that also states one must state the same, so a mistyped
        // request never silently produces a column that disagrees with the variable it shows.
        private static JObject CheckDeclaredTypeAgrees(string name, DeclaredVariable declared, JObject args, string explicitType)
        {
            string disagreement = null;
            if (!string.IsNullOrWhiteSpace(explicitType) && !string.Equals(explicitType, declared.BasicType, StringComparison.OrdinalIgnoreCase))
                disagreement = "basicType " + explicitType;
            else if (PresentArg(args, "length") is JToken length && declared.IsSized
                && !string.Equals(length.ToString(), declared.Length.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                disagreement = "length " + length;
            else if (PresentArg(args, "decimals") is JToken decimals && declared.BasicType == "Numeric"
                && !string.Equals(decimals.ToString(), declared.Decimals.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                disagreement = "decimals " + decimals;
            if (disagreement == null) return null;
            JObject error = Error("GridVariableTypeMismatch", "Variable '&" + name + "' is declared as " + declared.Describe()
                + "; " + disagreement + " disagrees. Omit the type arguments to use the declaration.");
            error["declared"] = declared.ToJson();
            return error;
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

        /// <summary>
        /// A variable the owning object declares in its Variables part, as <c>add_grid_variable</c> needs it. A variable is a
        /// part item, not a KB object, and the SDK's <c>Variable</c> has no GUID (only an integer <c>Id</c> and its name), so
        /// its identity here is its name and what is read is its type.
        /// </summary>
        internal sealed class DeclaredVariable
        {
            internal string Name;
            /// <summary>The SDK type name (<c>CHARACTER</c>, <c>NUMERIC</c>, <c>GX_SDT</c>, ...).</summary>
            internal string DbType;
            internal int Length;
            internal int Decimals;
            internal bool IsCollection;
            internal bool IsStandard;
            /// <summary>The WorkWithPlus <c>basicType</c> the SDK type maps to; null when the pattern has none.</summary>
            internal string BasicType;
            /// <summary><c>&lt;type GUID&gt;-&lt;name&gt;</c> of the attribute or domain the variable is based on.</summary>
            internal string BasedOnReference;
            internal KBObject BasedOnObject;

            internal bool IsSized => BasicType == "Numeric" || BasicType == "Character" || BasicType == "VarChar" || BasicType == "LongVarChar";

            internal string Describe()
            {
                if (!string.IsNullOrEmpty(BasedOnReference)) return "based on " + BasedOnReference;
                string text = DbType ?? "unknown type";
                if (IsSized) text += "(" + Length + (BasicType == "Numeric" ? "," + Decimals : "") + ")";
                return IsCollection ? "collection of " + text : text;
            }

            internal JObject ToJson()
            {
                var json = new JObject { ["name"] = Name, ["type"] = DbType };
                if (IsSized) json["length"] = Length;
                if (BasicType == "Numeric") json["decimals"] = Decimals;
                if (BasicType != null) json["basicType"] = BasicType;
                if (!string.IsNullOrEmpty(BasedOnReference)) json["basedOn"] = BasedOnReference;
                if (IsCollection) json["isCollection"] = true;
                bool usable = !IsCollection && (BasicType != null || !string.IsNullOrEmpty(BasedOnReference));
                json["gridColumn"] = usable;
                return json;
            }
        }

        // The WorkWithPlus `basicType` of an SDK variable type (WorkWithPlusInstance.xml, VarTypeDef.basicType).
        // Structured types (SDT, Business Component, external object) and geography have no basic type.
        internal static string BasicTypeOf(string dbType)
        {
            switch ((dbType ?? string.Empty).ToUpperInvariant())
            {
                case "NUMERIC": case "INT": case "PACKED": case "ZONED": return "Numeric";
                case "CHARACTER": return "Character";
                case "VARCHAR": return "VarChar";
                case "LONGVARCHAR": return "LongVarChar";
                case "DATE": return "Date";
                case "DATETIME": return "DateTime";
                case "BOOLEAN": return "Boolean";
                case "GUID": return "GUID";
                case "BITMAP": return "Image";
                case "VIDEO": return "Video";
                case "AUDIO": return "Audio";
                case "BINARY": case "BINARYFILE": return "Blob";
                default: return null;
            }
        }

        internal static DeclaredVariable DescribeDeclaredVariable(object variable)
        {
            string name = PartAccessor.GetVariableName(variable).TrimStart('&');
            if (name.Length == 0) return null;
            var described = new DeclaredVariable { Name = name, DbType = GetProperty(variable, "Type")?.ToString() };
            described.BasicType = BasicTypeOf(described.DbType);
            described.Length = ToInt(GetProperty(variable, "Length"));
            described.Decimals = ToInt(GetProperty(variable, "Decimals"));
            described.IsCollection = GetProperty(variable, "IsCollection") is bool collection && collection;
            described.IsStandard = (GetProperty(variable, "IsStandard") is bool standard && standard)
                || (GetProperty(variable, "IsAutoDefined") is bool auto && auto);
            // `Based on` an attribute or a domain keeps the link to it in the pattern (`dataType="Based on"` and `domain`);
            // the declared length and decimals are those of the base and are not repeated.
            KBObject basedOn = (GetProperty(variable, "AttributeBasedOn") ?? GetProperty(variable, "DomainBasedOn")) as KBObject;
            if (basedOn != null)
            {
                described.BasedOnObject = basedOn;
                described.BasedOnReference = GxObjectReference(basedOn);
            }
            return described;
        }

        private static int ToInt(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException) { return 0; }
        }

        /// <summary>
        /// The variables the owning objects declare, first host first (the parent Transaction/WebPanel, then the object that
        /// was named, then the instance), without repeats. Reads the live Variables part of each host.
        /// </summary>
        internal static List<DeclaredVariable> DeclaredVariables(IEnumerable<KBObject> hosts)
            => DistinctDeclared((hosts ?? Enumerable.Empty<KBObject>())
                .Where(host => host != null && PartAccessor.GetVariablesLikePart(host) != null)
                .SelectMany(host => PartAccessor.GetVariableObjects(host)));

        /// <summary>The variables in order, each name once (the first declaration wins), without the ones that have no name.</summary>
        internal static List<DeclaredVariable> DistinctDeclared(IEnumerable<object> variables)
        {
            var declared = new List<DeclaredVariable>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object variable in variables ?? Enumerable.Empty<object>())
            {
                DeclaredVariable described = DescribeDeclaredVariable(variable);
                if (described != null && seen.Add(described.Name)) declared.Add(described);
            }
            return declared;
        }

        /// <summary>The <c>variables[]</c> of <c>list</c>: user-declared variables with the type <c>add_grid_variable</c> would give the column.</summary>
        internal static JArray DeclaredVariablesJson(IEnumerable<KBObject> hosts)
            => ListedVariables(DeclaredVariables(hosts));

        internal static JArray ListedVariables(IEnumerable<DeclaredVariable> declared)
            => new JArray((declared ?? Enumerable.Empty<DeclaredVariable>()).Where(v => !v.IsStandard).Select(v => (JToken)v.ToJson()));

        private static JObject FindRequestedGrid(XDocument document, string gridPath, out XElement grid)
        {
            grid = null;
            var grids = document.Descendants().Where(e => Is(e, "grid")).ToList();
            if (grids.Count == 0) return Error("GridNotFound", "The PatternInstance has no grid.");
            if (!string.IsNullOrWhiteSpace(gridPath) && Regex.IsMatch(gridPath.Trim(), "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                // A grid is also addressed by the name it was given (add_grid's gridName, or its controlName), which is how
                // a client that created it refers to it (#638). A name that matches no grid, or several, is refused.
                string requestedName = gridPath.Trim();
                var named = grids.Where(g => GridMatchesName(g, requestedName)).ToList();
                if (named.Count == 1) { grid = named[0]; return null; }
                if (named.Count > 1)
                    return Error("AmbiguousGrid", "More than one grid is named '" + requestedName + "'; use an absolute type path.");
                var available = grids.Select(g => Attr(g, "name")).Where(n => n.Length > 0).ToList();
                return Error("GridNotFound", "No grid is named '" + requestedName + "'; gridPath is a grid name or an absolute type path with optional zero-based indices"
                    + (available.Count > 0 ? " (named grids: " + string.Join(", ", available) + ")." : "."));
            }
            if (!string.IsNullOrWhiteSpace(gridPath))
            {
                // An absolute type path walked from the document root, with an
                // optional zero-based index per segment resolved among the
                // siblings of that same type. An unindexed segment that matches
                // more than one element is refused rather than guessed.
                if (!Regex.IsMatch(gridPath, @"\A/[A-Za-z_][A-Za-z0-9_]*(\[[0-9]+\])?(/[A-Za-z_][A-Za-z0-9_]*(\[[0-9]+\])?)*\z"))
                    return Error("InvalidGridPath", "gridPath must be a grid name or an absolute type path with optional zero-based indices.");
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
