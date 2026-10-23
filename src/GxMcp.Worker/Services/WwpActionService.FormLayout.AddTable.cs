using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // add_table of the form-layout operations (issue #595); the shared runner is in WwpActionService.FormLayout.cs.
    public sealed partial class WwpActionService
    {
        // ---- add_table: request --------------------------------------------------------------

        internal sealed class AddTableRequest
        {
            internal string TableName;
            internal int NumberOfColumns;
            internal string TableType;
            internal bool? IncludeInPanel;
            internal string PanelTitle;
            internal JArray Children;
        }

        internal static JObject ValidateAddTableArgs(JObject args, out AddTableRequest request)
        {
            request = new AddTableRequest { TableName = args?["tableName"]?.ToString()?.Trim(), NumberOfColumns = 1, TableType = "Responsive" };
            if (string.IsNullOrWhiteSpace(request.TableName))
                return TableError("MissingTableName", "tableName is required for add_table.");
            if (!LayoutIdentifierPattern.IsMatch(request.TableName))
                return TableError("InvalidTableName", "tableName must be a GeneXus control identifier (letters, digits and underscore, not starting with a digit).");
            JToken columns = args?["numberOfColumns"];
            if (columns != null && columns.Type != JTokenType.Null)
            {
                if (!(columns.Type == JTokenType.Integer || (columns.Type == JTokenType.String && int.TryParse(columns.ToString(), out _)))
                    || columns.ToObject<int>() < 1)
                    return TableError("InvalidNumberOfColumns", "numberOfColumns must be an integer of at least 1.");
                request.NumberOfColumns = columns.ToObject<int>();
            }
            string tableType = args?["tableType"]?.ToString();
            if (!string.IsNullOrWhiteSpace(tableType))
            {
                if (!TryNormalizeTableType(tableType, out request.TableType, out string typeError))
                    return TableError("InvalidTableType", typeError);
            }
            JToken includeInPanel = args?["includeInPanel"];
            if (includeInPanel != null && includeInPanel.Type != JTokenType.Null)
            {
                if (includeInPanel.Type != JTokenType.Boolean)
                    return TableError("InvalidIncludeInPanel", "includeInPanel must be true or false.");
                request.IncludeInPanel = includeInPanel.Value<bool>();
            }
            string panelTitle = args?["panelTitle"]?.ToString();
            if (!string.IsNullOrWhiteSpace(panelTitle))
            {
                if (request.IncludeInPanel == false)
                    return TableError("InvalidPanelTitle", "panelTitle requires includeInPanel=true; a table outside a panel has no panel title.");
                request.IncludeInPanel = true;
                request.PanelTitle = panelTitle;
            }
            JToken children = args?["children"];
            if (children != null && children.Type != JTokenType.Null)
            {
                if (!(children is JArray array) || array.Any(c => !(c is JObject)))
                    return TableError("InvalidWwpChild", "children must be an array of typed child objects.");
                request.Children = array;
            }
            return ValidatePlacementArgs(args);
        }

        private static bool HasPlacement(JObject args) =>
            args?["position"] != null && args["position"].Type != JTokenType.Null
            || !string.IsNullOrWhiteSpace(args?["before"]?.ToString())
            || !string.IsNullOrWhiteSpace(args?["after"]?.ToString());

        private static JObject ValidatePlacementArgs(JObject args)
        {
            int given = (args?["position"] != null && args["position"].Type != JTokenType.Null ? 1 : 0)
                + (string.IsNullOrWhiteSpace(args?["before"]?.ToString()) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(args?["after"]?.ToString()) ? 0 : 1);
            if (given > 1)
                return TableError("ConflictingPlacement", "Pass only one of position, before or after.");
            if (args?["position"] != null && args["position"].Type != JTokenType.Null
                && (args["position"].Type != JTokenType.Integer || args["position"].Value<int>() < 0))
                return TableError("InvalidPosition", "position must be a zero-based integer index among the container's children.");
            return null;
        }

        // ---- add_table: XML plan -------------------------------------------------------------

        internal static JObject PlanAddTableXml(XDocument document, JObject args)
        {
            JObject invalid = ValidateAddTableArgs(args, out AddTableRequest request);
            if (invalid != null) return invalid;
            JObject containerError = ResolveDefaultFormTable(document, args, "TableContainerNotFound", "TableContainerAmbiguous",
                "add_table:", "the new table", "add_table", out XElement container);
            if (containerError != null) return containerError;

            HashSet<string> taken = TakenControlNames(document);
            if (taken.Contains(request.TableName))
                return TableError("TableAlreadyExists", "A control named '" + request.TableName + "' already exists in the PatternInstance; table names must be unique on the form.");

            string snapshot = document.ToString(SaveOptions.DisableFormatting);
            string containerPath = ContainerPath(container);
            string containerBefore = container.ToString(SaveOptions.DisableFormatting);
            var movedAttributes = new JArray();
            var sourceParents = new List<XElement>();
            XElement table = BuildTableElement(request, container.GetDefaultNamespace());
            try
            {
                foreach (JObject child in request.Children?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    table.Add(BuildXmlControl(child));
                JObject collision = CheckNewControlNames(table, taken, request.TableName);
                if (collision != null) return collision;
                // The table is still detached, so the scope search cannot see its children.
                JObject moveError = MoveFormAttributesIntoTab(table, FormScope(container), movedAttributes, sourceParents, maintainOrderedLists: true);
                if (moveError != null) { RestoreDocument(document, snapshot); return moveError; }
            }
            catch (WwpTabException ex) { RestoreDocument(document, snapshot); return TableError(ex.Code, ex.Message); }

            JObject placementError = ResolveInsertIndex(container, args, out int index);
            if (placementError != null) { RestoreDocument(document, snapshot); return placementError; }
            InsertChildAt(container, table, index);
            if (!PatternChildOrderEdit.TryInsert(container, table, out PatternChildOrderEdit.Change ordering, out string reason))
            {
                RestoreDocument(document, snapshot);
                return OrderedContainerUnsupported(containerPath, reason);
            }
            // Recorded once the document is final, so the verification can find each source
            // table by position rather than by a path an unnamed table would share.
            for (int i = 0; i < sourceParents.Count; i++)
                ((JObject)movedAttributes[i])["sourceIndexPath"] = new JArray(IndexPath(sourceParents[i]));

            return new JObject
            {
                ["changed"] = true,
                ["tableName"] = request.TableName,
                ["containerPath"] = containerPath,
                ["position"] = index,
                ["typedDiff"] = new JObject
                {
                    ["operation"] = AddTableOperation,
                    ["tableName"] = request.TableName,
                    ["containerPath"] = containerPath,
                    ["position"] = index,
                    ["before"] = containerBefore,
                    ["after"] = container.ToString(SaveOptions.DisableFormatting),
                    ["table"] = table.ToString(SaveOptions.DisableFormatting)
                },
                ["children"] = new JArray(table.Elements().Select(ProjectXmlControl)),
                ["movedAttributes"] = movedAttributes,
                ["childrenOrderedList"] = OrderingJson(ordering)
            };
        }

        private static XElement BuildTableElement(AddTableRequest request, XNamespace ns)
        {
            var table = new XElement(ns + "table", new XAttribute("name", request.TableName));
            SetPair(table, "type", request.TableType);
            SetPair(table, "numberOfColumns", request.NumberOfColumns.ToString(CultureInfo.InvariantCulture));
            if (request.IncludeInPanel.HasValue) SetPair(table, "includeInPanel", new JValue(request.IncludeInPanel.Value));
            if (request.PanelTitle != null) table.SetAttributeValue("panelTitle", request.PanelTitle);
            return table;
        }

        private static HashSet<string> TakenControlNames(XDocument document)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in document.Descendants())
            {
                string name = Attr(element, "name");
                if (name.Length > 0) taken.Add(name);
                string controlName = Attr(element, "controlName");
                if (controlName.Length > 0) taken.Add(controlName);
            }
            return taken;
        }

        // A created child (textBlock, variable, userAction, nested table) must not reuse a
        // name that is already on the form: a repeated control would be the IDE's problem to
        // report after the fact. Attribute children are moves and are checked by the mover.
        private static JObject CheckNewControlNames(XElement table, HashSet<string> taken, string tableName)
        {
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tableName };
            foreach (XElement created in table.Descendants().Where(e => !Is(e, "attribute")))
            {
                string identity = Attr(created, "name");
                if (identity.Length == 0) identity = Attr(created, "controlName");
                if (identity.Length == 0) continue;
                if (!requested.Add(identity))
                    return TableError("InvalidWwpChild", "'" + identity + "' is requested more than once in the same add_table call.");
                if (taken.Contains(identity))
                {
                    JObject error = TableError(Is(created, "table") ? "TableAlreadyExists" : "ControlNameAlreadyExists",
                        "A control named '" + identity + "' already exists in the PatternInstance; add_table creates new controls only.");
                    error["hint"] = "Use genexus_wwp action=move_control to move the existing control into the table.";
                    return error;
                }
            }
            return null;
        }

        // ---- add_table: native ---------------------------------------------------------------

        internal static JObject ApplyNativeAddTable(object part, JObject args, JObject plan)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return TableError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject invalid = ValidateAddTableArgs(args, out AddTableRequest request);
            if (invalid != null) return invalid;
            JObject containerError = ResolveDefaultNativeFormTable(root, args, "TableContainerNotFound", "TableContainerAmbiguous",
                "add_table:", "the new table", "add_table", out object container);
            if (containerError != null) return containerError;
            if (Walk(root).Any(e => NativeNameMatches(e, request.TableName)))
                return TableError("TableAlreadyExists", "A native control named '" + request.TableName + "' already exists in the PatternInstance.");

            // Every ordering-list edit is checked against the native value before anything
            // moves, so a mismatch between the native tree and the persisted XML refuses
            // cleanly instead of being discovered halfway through the update.
            var orderingEdits = new List<KeyValuePair<object, JObject>>();
            JObject orderingError = CollectOrderingEdit(container, plan?["childrenOrderedList"] as JObject, orderingEdits);
            if (orderingError != null) return orderingError;
            foreach (JObject moved in (plan?["movedAttributes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (!(moved["childrenOrderedList"] is JObject change) || change["changed"]?.ToObject<bool?>() != true) continue;
                JObject sourceError = ResolveNativeContainer(root, moved["from"]?.ToString(), out object source);
                if (sourceError != null) { sourceError["code"] = "AttributeSourceUnresolved"; return sourceError; }
                orderingError = CollectOrderingEdit(source, change, orderingEdits);
                if (orderingError != null) return orderingError;
            }

            object scope = NativeFormScope(root, container);
            var requestedAttributes = new List<string>();
            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Action) }, null);
            int? insertIndex = null;
            Action mutation = () =>
            {
                object table = CreateNativeChild(container, "table");
                SetNativeAttribute(table, "name", request.TableName);
                SetNativePair(table, "type", request.TableType);
                SetNativeNumberPair(table, "numberOfColumns", request.NumberOfColumns);
                if (request.IncludeInPanel.HasValue) SetNativePair(table, "includeInPanel", request.IncludeInPanel.Value ? "True" : "False");
                if (request.PanelTitle != null) SetNativeAttribute(table, "panelTitle", request.PanelTitle);
                foreach (JObject child in request.Children?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    AttachNativeControl(table, child, scope, requestedAttributes);
                insertIndex = ResolveNativeInsertIndex(container, args, null);
                if (insertIndex.HasValue) ExecuteElementCommand(container, table, "InsertElementCommand", insertIndex.Value);
                else ExecuteElementCommand(container, table, "AddElementCommand", null);
                foreach (var edit in orderingEdits) SetNativeAttribute(edit.Key, "childrenOrderedList", edit.Value["after"].ToString());
            };
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp add_table", mutation });
            else mutation();

            return new JObject
            {
                ["changed"] = true,
                ["tableName"] = request.TableName,
                ["sdkOperation"] = "CreateChildElement(table) + " + (insertIndex.HasValue ? "InsertElementCommand" : "AddElementCommand")
            };
        }

        private static bool NativeNameMatches(object element, string name) =>
            string.Equals(NativeAttribute(element, "name"), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NativeAttribute(element, "controlName"), name, StringComparison.OrdinalIgnoreCase);

        // The native list must be the one the plan was computed from; the plan's new value
        // is then applied verbatim.
        private static JObject CollectOrderingEdit(object container, JObject change, List<KeyValuePair<object, JObject>> edits)
        {
            if (change == null || change["changed"]?.ToObject<bool?>() != true) return null;
            string native = NativeAttributeOrXml(container, "childrenOrderedList");
            if (!OrderedListEquivalent(native, change["before"]?.ToString()))
                return TableError("WwpOrderingMetadataMismatch",
                    "The native childrenOrderedList of '" + NativeContainerLabel(container) + "' differs from the persisted PatternInstance; no mutation was applied.");
            edits.Add(new KeyValuePair<object, JObject>(container, change));
            return null;
        }

        private static bool OrderedListEquivalent(string left, string right) =>
            string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

        // numberOfColumns is declared as a number by the pattern; a string is the fallback for
        // a package that declares it as text. Either way the default twin follows.
        private static void SetNativeNumberPair(object element, string name, int value)
        {
            string text = value.ToString(CultureInfo.InvariantCulture);
            foreach (string attribute in new[] { name, "default" + char.ToUpperInvariant(name[0]) + name.Substring(1) })
            {
                bool applied;
                try { applied = PatternSemanticAttributeWriter.ApplySemanticAttributeObject(element, attribute, value, text); }
                catch (TargetInvocationException ex)
                {
                    // The SDK rejected the typed value; the string form below is the documented fallback.
                    Logger.Debug("[WWP-FORMLAYOUT] typed '" + attribute + "' rejected by the SDK command, retrying as text: " + ExceptionRoot.Message(ex));
                    applied = false;
                }
                if (!applied) SetNativeAttribute(element, attribute, text);
            }
        }

        private static string NativeSiblingIdentity(object element)
        {
            string name = NativeAttribute(element, "name");
            if (!string.IsNullOrWhiteSpace(name)) return name;
            string controlName = NativeAttribute(element, "controlName");
            if (!string.IsNullOrWhiteSpace(controlName)) return controlName;
            string attribute = NativeAttribute(element, "attribute");
            if (!string.IsNullOrWhiteSpace(attribute)) { int dash = attribute.LastIndexOf('-'); return dash >= 0 ? attribute.Substring(dash + 1) : attribute; }
            if (IsNativeGrid(element)) return "Grid";
            if (NativeIsKind(element, "errorViewer")) return "ErrorViewer";
            string title = NativeAttribute(element, "title");
            return string.IsNullOrWhiteSpace(title) ? string.Empty : title;
        }

        private static bool NativeSiblingMatches(object element, string requested) =>
            string.Equals(NativeSiblingIdentity(element), requested, StringComparison.OrdinalIgnoreCase)
            || (IsNativeAttribute(element) && AttributeReferenceMatches(NativeAttribute(element, "attribute"), requested));

        // Null means append. `excluded` is the element being moved within the same container.
        private static int? ResolveNativeInsertIndex(object container, JObject args, object excluded)
        {
            List<object> siblings = NativeChildren(container).Where(c => !ReferenceEquals(c, excluded)).ToList();
            string before = args?["before"]?.ToString()?.Trim();
            string after = args?["after"]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after))
            {
                string requested = string.IsNullOrWhiteSpace(before) ? after : before;
                int found = siblings.FindIndex(e => NativeSiblingMatches(e, requested));
                if (found < 0) throw new WwpTabException("PlacementSiblingNotFound", "No native child is addressed by '" + requested + "'.");
                int index = string.IsNullOrWhiteSpace(before) ? found + 1 : found;
                return index >= siblings.Count ? (int?)null : NativeChildren(container).IndexOf(siblings[index]);
            }
            if (args?["position"]?.Type == JTokenType.Integer)
            {
                int index = ClampPosition(args["position"].Value<int>(), siblings.Count);
                return index >= siblings.Count ? (int?)null : NativeChildren(container).IndexOf(siblings[index]);
            }
            return null;
        }

        private static bool NativeIsKind(object element, string kind) =>
            NativeType(element).Equals(kind, StringComparison.OrdinalIgnoreCase)
            || NativeElementXml(element)?.Name.LocalName.Equals(kind, StringComparison.OrdinalIgnoreCase) == true;

        // ---- add_table: verification ---------------------------------------------------------

        internal static JObject VerifyAddedTable(XDocument planned, XDocument persisted, JObject plan)
        {
            string tableName = plan["tableName"]?.ToString();
            string containerPath = plan["containerPath"]?.ToString();
            int index = plan["position"]?.ToObject<int?>() ?? -1;
            List<XElement> tables = persisted.Descendants().Where(e => Is(e, "table")
                && string.Equals(Attr(e, "name"), tableName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tables.Count != 1)
                return NotConfirmed("The re-read PatternInstance contains table '" + tableName + "' " + tables.Count + " time(s) instead of once. "
                    + DescribeTablesForDiagnostics(planned, persisted));
            XElement table = tables[0];
            if (!string.Equals(ContainerPath(table.Parent), containerPath, StringComparison.OrdinalIgnoreCase))
                return NotConfirmed("The re-read table '" + tableName + "' is under '" + ContainerPath(table.Parent) + "' instead of '" + containerPath + "'.");
            if (table.Parent.Elements().ToList().IndexOf(table) != index)
                return NotConfirmed("The re-read table '" + tableName + "' is at child index " + table.Parent.Elements().ToList().IndexOf(table) + " instead of " + index + ".");
            XElement plannedTable = planned.Descendants().FirstOrDefault(e => Is(e, "table")
                && string.Equals(Attr(e, "name"), tableName, StringComparison.OrdinalIgnoreCase));
            if (plannedTable == null) return NotConfirmed("The planned PatternInstance has no table '" + tableName + "'.");
            if (!IsRequestedSubset(ProjectXmlControl(plannedTable), ProjectXmlControl(table)))
                return NotConfirmed("The re-read table '" + tableName + "' does not carry the requested attributes and typed children in order.");

            foreach (JObject moved in (plan["movedAttributes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = moved["attribute"]?.ToString();
                List<XElement> occurrences = FormTableAttributes(FormScope(table))
                    .Where(e => AttributeReferenceMatches(Attr(e, "attribute"), name)).ToList();
                if (occurrences.Count != 1 || !occurrences[0].Ancestors().Contains(table))
                    return NotConfirmed("Attribute '" + name + "' is on the form " + occurrences.Count
                        + " time(s) after the save instead of once inside table '" + tableName + "'.");
            }

            XElement plannedRest = StripForComparison(planned.Root, plannedTable);
            XElement persistedRest = StripForComparison(persisted.Root, table);
            if (!XNode.DeepEquals(plannedRest, persistedRest))
                return NotConfirmed("The persisted PatternInstance differs beyond the requested table; children, bindings, events or metadata of other elements changed.");
            var forbidden = new List<KeyValuePair<XElement, string>>();
            foreach (JObject moved in (plan["movedAttributes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                XElement source = ElementAtIndexPath(planned.Root, moved["sourceIndexPath"] as JArray);
                string identifier = moved["orderingIdentifier"]?.ToString();
                if (source != null && !string.IsNullOrEmpty(identifier))
                    forbidden.Add(new KeyValuePair<XElement, string>(source, identifier));
            }
            return VerifyOrderedLists(planned.Root, persisted.Root, plannedTable, table, requiredIdentifier: tableName, forbidden: forbidden);
        }

        // A table that is missing after the save is either absent (the SDK dropped it) or present
        // under another identity (the SDK renamed it); the count and the names tell which, so the
        // next report does not have to be reproduced to find out.
        internal static string DescribeTablesForDiagnostics(XDocument planned, XDocument persisted)
        {
            const int Limit = 20;
            List<XElement> plannedTables = planned.Descendants().Where(e => Is(e, "table")).ToList();
            List<XElement> persistedTables = persisted.Descendants().Where(e => Is(e, "table")).ToList();
            string listed = string.Join("; ", persistedTables.Take(Limit).Select(t =>
            {
                string name = Attr(t, "name");
                return (string.IsNullOrEmpty(name) ? "(unnamed)" : name) + " under '" + ContainerPath(t.Parent) + "'";
            }));
            return "Tables expected from the plan: " + plannedTables.Count + "; tables in the re-read PatternInstance: " + persistedTables.Count
                + (persistedTables.Count == 0 ? "." : " (" + listed + (persistedTables.Count > Limit ? "; ..." : "") + ").");
        }

        internal static JObject VerifyAddedTableProjection(string webForm, JObject plan)
        {
            if (string.IsNullOrWhiteSpace(webForm)) return NotConfirmed("The projected WebForm could not be re-read.");
            XDocument document;
            try { document = XDocument.Parse(webForm, LoadOptions.PreserveWhitespace); }
            catch (XmlException) { return NotConfirmed("The projected WebForm is not valid XML."); }
            string tableName = plan["tableName"]?.ToString();
            bool targetPresent = FindWebFormNamedElement(document, tableName) != null;
            List<string> missing = (plan["children"] as JArray)?.OfType<JObject>()
                .Where(c => string.Equals(c["type"]?.ToString(), "textBlock", StringComparison.OrdinalIgnoreCase))
                .Select(c => c["controlName"]?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n) && FindWebFormNamedElement(document, n) == null).ToList() ?? new List<string>();
            bool confirmed = targetPresent && missing.Count == 0;
            return new JObject
            {
                ["confirmed"] = confirmed,
                ["targetPresent"] = targetPresent,
                ["missingControls"] = new JArray(missing),
                ["message"] = confirmed ? null : "The projected WebForm does not contain table '" + tableName + "'"
                    + (missing.Count == 0 ? "." : " with its text blocks (" + string.Join(", ", missing) + ").")
            };
        }
    }
}
