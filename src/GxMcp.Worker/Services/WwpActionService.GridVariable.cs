using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // add_grid_variable (#638): one presentation-variable column in an existing grid, keyed by name (the SDK gives a variable
    // no GUID) and typed from the owning object's declaration or the explicit arguments. A write is reported only when the
    // PatternInstance re-read holds the column where it was asked and the projected parent WebForm holds the variable as a
    // column of the grid control that renders that grid; anything less restores the snapshots.
    public sealed partial class WwpActionService
    {
        private string RunAddGridVariableOperation(string target, KBObject requestedObject, KBObject instance,
            string xml, string versionToken, JObject args)
        {
            string expectedVersion = args?["baseVersion"]?.ToString() ?? args?["expectedVersion"]?.ToString() ?? args?["versionToken"]?.ToString();
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            string variableName = (args?["variable"]?.ToString() ?? args?["variableName"]?.ToString())?.Trim().TrimStart('&');

            KBObject previewHost = WwpProjectionHelper.ResolveHostParent(instance, _objects, xml);
            DeclaredVariable previewDeclared = FindDeclaredVariable(new[] { previewHost, requestedObject, instance }, variableName);
            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument previewDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject previewMutation = ApplyAddGridVariableXml(previewDocument, args, previewDeclared);
            if (previewMutation["error"] != null)
                return McpResponse.Err(code: previewMutation["code"]?.ToString() ?? "WwpGridVariableInvalid",
                    message: previewMutation["error"].ToString(), target: target, extra: previewMutation);
            if (!IsExpectedVersion(expectedVersion, versionToken))
                return BuildWwpStaleObject(target, expectedVersion, versionToken,
                    "The WorkWithPlus PatternInstance changed after the caller's read; no grid variable was added.");

            JObject diff = new JObject { ["before"] = Project(beforeDocument), ["after"] = Project(previewDocument), ["column"] = previewMutation };
            if (dryRun)
                return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["operation"] = "add_grid_variable",
                    ["diff"] = diff,
                    ["versionToken"] = versionToken,
                    ["saved"] = false,
                    ["persisted"] = false,
                    ["rollbackOnFailure"] = rollbackOnFailure,
                    ["declaredVariable"] = previewDeclared?.ToJson(),
                    ["mutationMode"] = "native-pattern-sdk",
                    ["sdkOperation"] = "PatternInstance.RootElement + CreateChildElement(gridVariable) + AddElementCommand/InsertElementCommand"
                });

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                if (currentInstance == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);
                // The part is resolved from the instance that was read: resolving it from the requested host yields another
                // copy of the WorkWithPlus object, and saving one copy with the other's part persists nothing.
                _patterns.BuildPatternPartEnvelope(currentInstance, "PatternInstance", currentXml, PatternRegistry.WorkWithPlusPatternId,
                    out currentInstance, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);

                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion) && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion,
                        "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no grid variable was added.");

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                DeclaredVariable lockedDeclared = FindDeclaredVariable(new[] { parent, requestedObject, currentInstance }, variableName);
                XDocument lockedBefore = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedMutation = ApplyAddGridVariableXml(lockedAfter, args, lockedDeclared);
                if (lockedMutation["error"] != null)
                    return McpResponse.Err(code: lockedMutation["code"]?.ToString() ?? "WwpGridVariableInvalid",
                        message: lockedMutation["error"].ToString(), target: target, extra: lockedMutation);
                JObject planError = PlanGridVariable(args, lockedDeclared, out GridVariablePlan plan);
                if (planError != null)
                    return McpResponse.Err(code: planError["code"]?.ToString() ?? "WwpGridVariableInvalid",
                        message: planError["error"].ToString(), target: target, extra: planError);
                JObject gridError = FindRequestedGrid(lockedBefore, args?["gridPath"]?.ToString(), out XElement requestedGrid);
                if (gridError != null)
                    return McpResponse.Err(code: gridError["code"]?.ToString() ?? "GridNotFound",
                        message: gridError["error"].ToString(), target: target, extra: gridError);
                // The projection renders the WorkWithPlus grid as the WebForm grid control named after it.
                string[] gridControlNames = { Attr(requestedGrid, "controlName"), Attr(requestedGrid, "name") };

                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "Exact PatternInstance and parent WebForm snapshots were not available; no grid variable was added.",
                        target: target, extra: new JObject { ["snapshot"] = snapshots.ToJson(), ["persisted"] = false });

                bool persistenceStarted = false;
                JObject projection = null;
                try
                {
                    JObject nativeMutation = ApplyNativeAddGridVariable(currentPart, lockedBefore, args, plan);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected", nativeMutation["error"].ToString());

                    persistenceStarted = true;
                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);
                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out KBObject persistedInstance, out _);
                    if (string.IsNullOrWhiteSpace(persistedXml))
                        throw new WwpTabException("WwpGridVariableNotPersisted", "The SDK save completed, but the PatternInstance could not be re-read.");

                    XDocument persistedDocument = XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace);
                    JObject verification = VerifyAddGridVariable(lockedBefore, persistedDocument, args, plan);
                    if (verification["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpGridVariableNotPersisted", verification["error"]?.ToString() ?? "The requested grid variable was not confirmed after re-read.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance, "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled", "SDPlus_Editor_Apply_On_Save became False after the native grid variable save.");

                    if (!WwpProjectionHelper.TryProjectHostOntoParent(parent, persistedInstance ?? currentInstance))
                        throw new WwpTabException("WwpProjectionFailed", "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");
                    // The projection declares an undeclared variable in the parent; the column binds that declaration's layout id.
                    string projectedWebForm = ReadPart(parent, "WebForm");
                    projection = VerifyProjectedGridVariable(parentWebFormBefore, projectedWebForm, gridControlNames, plan.Name,
                        DeclaredVariableLayoutId(parent, plan.Name));
                    if (projection["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpProjectionNotConfirmed", projection["message"]?.ToString()
                            ?? "The projected parent WebForm does not hold the requested variable column in the requested grid.");

                    DeclaredVariable afterDeclared = FindDeclaredVariable(new[] { parent, requestedObject, persistedInstance ?? currentInstance }, plan.Name);

                    WriteService.NotePerTargetWrite(target);
                    var added = new JObject
                    {
                        ["instance"] = persistedInstance?.Name ?? currentInstance.Name,
                        ["parent"] = parent.Name,
                        ["operation"] = "add_grid_variable",
                        ["variable"] = plan.Name,
                        ["caption"] = plan.Caption,
                        ["typeSource"] = plan.TypeSource,
                        ["column"] = lockedMutation,
                        ["diff"] = new JObject { ["before"] = Project(lockedBefore), ["after"] = Project(persistedDocument) },
                        ["versionToken"] = WriteService.ComputeContentVersionToken(persistedInstance ?? currentInstance, persistedXml),
                        ["persisted"] = true,
                        ["saved"] = true,
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["webFormProjection"] = projection,
                        ["variableDeclared"] = afterDeclared != null,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["mutationMode"] = "native-pattern-sdk",
                        ["sdkOperation"] = "PatternInstance.RootElement + CreateChildElement(gridVariable) + AddElementCommand/InsertElementCommand",
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollbackPerformed"] = false,
                        ["lifecycleExecuted"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false
                    };
                    if (afterDeclared == null)
                        added["warning"] = "Variable '&" + plan.Name + "' is not declared in " + parent.TypeDescriptor?.Name + " '" + parent.Name
                            + "' after the projection; declare it with genexus_variable action=add.";
                    return McpResponse.Ok(target: target, code: "WwpGridVariableAdded", result: added);
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    JObject rollback = rollbackOnFailure
                        ? RestoreSnapshots(currentInstance, currentPart, nativeBytes, currentXml, parent, parentWebFormBefore, applyOnSaveBefore)
                        : new JObject { ["performed"] = false, ["skipped"] = true, ["exact"] = false };
                    ExceptionRoot.Log("[WWP-GRIDVARIABLE] failed", ex);
                    return McpResponse.Err(code: typed?.Code ?? "WwpGridVariableFailed", message: ExceptionRoot.Message(ex),
                        target: target, extra: new JObject
                        {
                            ["exceptionType"] = ExceptionRoot.Unwrap(ex)?.GetType().Name,
                            ["failureTrace"] = ExceptionRoot.FailureTrace(ex),
                            ["persisted"] = false,
                            ["saved"] = false,
                            ["partialPersistenceDetected"] = PartialPersistenceAfterRollback(persistenceStarted, rollback),
                            ["patternReReadConfirmed"] = false,
                            ["webFormProjectionConfirmed"] = false,
                            ["webFormProjection"] = projection,
                            ["rollback"] = rollback,
                            ["rollbackPerformed"] = rollbackOnFailure,
                            ["stateRestoredExactly"] = rollback["exact"]?.DeepClone() ?? false,
                            ["snapshot"] = snapshots.ToJson(),
                            ["rollbackOnFailure"] = rollbackOnFailure,
                            ["lifecycleExecuted"] = false
                        });
                }
            }
        }

        internal static DeclaredVariable FindDeclaredVariable(IEnumerable<KBObject> hosts, string name)
            => string.IsNullOrWhiteSpace(name) ? null
                : DeclaredVariables(hosts).FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        // The native twin of the XML grid is the grid with the same ordinal among the grids of the instance in document
        // order; the name and the number of columns are compared before it is trusted.
        private static object ResolveNativeGrid(object root, XDocument document, XElement xmlGrid, out JObject error)
        {
            error = null;
            List<XElement> xmlGrids = document.Descendants().Where(e => Is(e, "grid")).ToList();
            List<object> nativeGrids = Walk(root).Where(IsNativeGrid).ToList();
            int ordinal = xmlGrids.IndexOf(xmlGrid);
            if (ordinal < 0 || nativeGrids.Count != xmlGrids.Count)
            {
                error = Error("WwpGridNotResolvable", "The grid could not be matched to a native PatternInstance element ("
                    + xmlGrids.Count + " in the XML, " + nativeGrids.Count + " native).");
                return null;
            }
            object grid = nativeGrids[ordinal];
            if (!string.Equals(NativeAttribute(grid, "name") ?? string.Empty, Attr(xmlGrid, "name"), StringComparison.OrdinalIgnoreCase)
                || NativeChildren(grid).Count(IsNativeGridColumn) != xmlGrid.Elements().Count(e => Is(e, "gridAttribute") || Is(e, "gridVariable")))
            {
                error = Error("WwpGridNotResolvable", "The native grid does not match the XML grid it was resolved from.");
                return null;
            }
            return grid;
        }

        private static bool IsNativeGridColumn(object element)
        {
            if (element == null) return false;
            // The element types are WPGridVariable, TrnGridAttribute, ...: a column, not a gridVariableBasedOn placeholder.
            string type = NativeType(element);
            return type.EndsWith("GridVariable", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("GridAttribute", StringComparison.OrdinalIgnoreCase);
        }

        private static bool NativeColumnIdentifies(object column, string identity)
            => string.Equals(NativeAttribute(column, "name"), identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NativeAttribute(column, "attribute"), identity, StringComparison.OrdinalIgnoreCase);

        // The value a native column attribute takes: booleans and sizes are typed (a text value fails when the instance
        // is loaded), a domain is the KB object itself, everything else is text.
        private static object ToNativeColumnValue(XAttribute attribute, KBObject basedOn)
        {
            string name = attribute.Name.LocalName;
            if (name == "readOnly") return attribute.Value == "True";
            if (name.StartsWith("basic", StringComparison.Ordinal) && name != "basicType")
                return int.Parse(attribute.Value, CultureInfo.InvariantCulture);
            if (name == "domain" && basedOn != null) return basedOn;
            return attribute.Value;
        }

        internal static JObject ApplyNativeAddGridVariable(object part, XDocument before, JObject args, GridVariablePlan plan)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return Error("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject gridError = FindRequestedGrid(before, args?["gridPath"]?.ToString(), out XElement xmlGrid);
            if (gridError != null) return gridError;
            object grid = ResolveNativeGrid(root, before, xmlGrid, out JObject resolveError);
            if (resolveError != null) return resolveError;
            if (NativeChildren(grid).Any(c => IsNativeGridColumn(c) && NativeColumnIdentifies(c, plan.Name)))
                return Error("GridVariableAlreadyExists", "Grid variable '" + plan.Name + "' already exists.");

            int? index = null;
            string beforeIdentity = args?["before"]?.ToString();
            if (!string.IsNullOrWhiteSpace(beforeIdentity))
            {
                List<object> children = NativeChildren(grid);
                object anchor = children.FirstOrDefault(c => IsNativeGridColumn(c) && NativeColumnIdentifies(c, beforeIdentity));
                if (anchor == null) return Error("GridColumnBeforeNotFound", "Before-column '" + beforeIdentity + "' was not found.");
                index = children.IndexOf(anchor);
            }

            Action mutation = () =>
            {
                object variable = CreateNativeChild(grid, "gridVariable");
                foreach (XAttribute attribute in plan.Column.Attributes())
                {
                    if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(variable, attribute.Name.LocalName,
                            ToNativeColumnValue(attribute, plan.BasedOnObject)))
                        throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected variable property '" + attribute.Name.LocalName + "'.");
                }
                // AddElementCommand appends; a position is InsertElementCommand's (as in add_table and the form actions).
                ExecuteElementCommand(grid, variable, index.HasValue ? "InsertElementCommand" : "AddElementCommand", index);
            };
            MethodInfo update = part.GetType().GetMethod("ExecuteUpdate", new[] { typeof(string), typeof(Action) });
            if (update != null) update.Invoke(part, new object[] { "genexus_wwp add_grid_variable", mutation });
            else mutation();
            return new JObject { ["changed"] = true };
        }

        /// <summary>
        /// The re-read grid holds the column with every requested attribute, where it was asked to be, and every other child
        /// of the grid exactly as it was.
        /// </summary>
        internal static JObject VerifyAddGridVariable(XDocument before, XDocument persisted, JObject args, GridVariablePlan plan)
        {
            string gridPath = args?["gridPath"]?.ToString();
            if (FindRequestedGrid(before, gridPath, out XElement beforeGrid) != null
                || FindRequestedGrid(persisted, gridPath, out XElement afterGrid) != null)
                return Error("WwpGridVariableNotPersisted", "The re-read PatternInstance no longer has the requested grid.");

            List<XElement> afterChildren = afterGrid.Elements().ToList();
            List<XElement> added = afterChildren.Where(e => Is(e, "gridVariable") && Attr(e, "name").Equals(plan.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (added.Count != 1)
                return Error("WwpGridVariableNotPersisted", "The re-read grid holds " + added.Count + " columns named '" + plan.Name + "' instead of one.");
            XElement column = added[0];
            foreach (XAttribute requested in plan.Column.Attributes())
            {
                string persistedValue = Attr(column, requested.Name.LocalName);
                // The engine does not store a value that is the pattern's default (Numeric for basicType, Based on for dataType).
                if (persistedValue.Length == 0
                    && ((requested.Name.LocalName == "basicType" && IsDefaultBasicType(requested.Value))
                        || (requested.Name.LocalName == "dataType" && requested.Value == "Based on")))
                    continue;
                bool same = requested.Name.LocalName == "domain"
                    ? string.Equals(persistedValue, requested.Value, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(persistedValue, requested.Value, StringComparison.Ordinal);
                if (!same)
                    return Error("WwpGridVariableNotPersisted", "The re-read column lost or changed '" + requested.Name.LocalName
                        + "' (requested '" + requested.Value + "', persisted '" + persistedValue + "').");
            }

            List<XElement> beforeChildren = beforeGrid.Elements().ToList();
            int expectedIndex = beforeChildren.Count;
            string beforeIdentity = args?["before"]?.ToString();
            if (!string.IsNullOrWhiteSpace(beforeIdentity))
            {
                XElement anchor = FindGridColumn(beforeGrid, beforeIdentity);
                expectedIndex = anchor == null ? expectedIndex : beforeChildren.IndexOf(anchor);
            }
            if (afterChildren.IndexOf(column) != expectedIndex)
                return Error("WwpGridVariableNotPersisted", "The re-read column is at position " + afterChildren.IndexOf(column)
                    + " instead of " + expectedIndex + ".");

            List<XElement> others = afterChildren.Where(e => !ReferenceEquals(e, column)).ToList();
            if (others.Count != beforeChildren.Count || others.Where((e, i) => !XNode.DeepEquals(e, beforeChildren[i])).Any())
                return Error("WwpGridVariableNotPersisted", "The re-read grid changed or reordered a column that was not part of the request.");
            return new JObject { ["confirmed"] = true };
        }

        /// <summary>
        /// The projected parent WebForm holds the requested variable as exactly one column of the grid control that renders the
        /// WorkWithPlus grid (its <c>controlName</c> is the grid's name), and that grid did not hold it before the write. The SDK
        /// binds a grid column as <c>attribute="var:N"</c>, N being the variable's layout id in the parent; <c>&amp;Name</c> is
        /// accepted as the symbolic spelling. A form that merely changed, or holds the variable outside that grid, is not a
        /// confirmation. The column position is verified on the PatternInstance re-read; here it is reported.
        /// </summary>
        internal static JObject VerifyProjectedGridVariable(string webFormBefore, string webFormAfter,
            IReadOnlyCollection<string> gridControlNames, string variableName, int? variableLayoutId)
        {
            var names = (gridControlNames ?? new string[0]).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var result = new JObject
            {
                ["confirmed"] = false,
                ["variable"] = "&" + variableName,
                ["variableLayoutRef"] = variableLayoutId.HasValue ? "var:" + variableLayoutId.Value.ToString(CultureInfo.InvariantCulture) : null,
                ["requestedGridControlNames"] = new JArray(names)
            };
            XDocument after = ParseWebForm(webFormAfter);
            if (after == null)
                return NotConfirmed(result, "The projected parent WebForm could not be re-read as XML.");
            result["webFormGrids"] = new JArray(WebFormGrids(after).Select(g => Attr(g, "controlName")));

            List<XElement> grids = WebFormGrids(after).Where(g => names.Contains(Attr(g, "controlName"), StringComparer.OrdinalIgnoreCase)).ToList();
            if (grids.Count != 1)
                return NotConfirmed(result, "The projected parent WebForm holds " + grids.Count + " grid controls named "
                    + string.Join("/", names) + " instead of one.");
            XElement grid = grids[0];
            string gridName = Attr(grid, "controlName");
            result["gridControlName"] = gridName;
            List<string> columns = WebFormGridColumns(grid);
            result["columns"] = new JArray(columns);

            List<int> matches = columns.Select((reference, index) => new { reference, index })
                .Where(c => BindsVariable(c.reference, variableName, variableLayoutId)).Select(c => c.index).ToList();
            XElement beforeGrid = ParseWebForm(webFormBefore) is XDocument before
                ? WebFormGrids(before).FirstOrDefault(g => string.Equals(Attr(g, "controlName"), gridName, StringComparison.OrdinalIgnoreCase))
                : null;
            List<string> beforeColumns = beforeGrid == null ? new List<string>() : WebFormGridColumns(beforeGrid);
            if (beforeColumns.Any(c => BindsVariable(c, variableName, variableLayoutId)))
                return NotConfirmed(result, "Grid '" + gridName + "' already held a column bound to &" + variableName
                    + " before the write, so the projection cannot confirm it.");
            if (matches.Count != 1)
                return NotConfirmed(result, "The projected grid '" + gridName + "' holds " + matches.Count + " columns bound to &"
                    + variableName + (variableLayoutId.HasValue ? " (var:" + variableLayoutId.Value.ToString(CultureInfo.InvariantCulture) + ")" : "")
                    + " instead of one.");

            List<string> others = columns.Where((c, i) => i != matches[0]).ToList();
            result["confirmed"] = true;
            result["columnIndex"] = matches[0];
            result["otherColumnsPreserved"] = beforeGrid != null && others.SequenceEqual(beforeColumns, StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static JObject NotConfirmed(JObject result, string message)
        {
            result["message"] = message;
            return result;
        }

        private static XDocument ParseWebForm(string webForm)
        {
            if (string.IsNullOrWhiteSpace(webForm)) return null;
            try { return XDocument.Parse(webForm); }
            catch (System.Xml.XmlException) { return null; }
        }

        // A layout grid control (simplegrid, freestylegrid, ...) is an element whose name ends in "grid" and has a controlName.
        private static List<XElement> WebFormGrids(XDocument document)
            => document.Descendants().Where(e => e.Name.LocalName.EndsWith("grid", StringComparison.OrdinalIgnoreCase)
                && Attr(e, "controlName").Length > 0).ToList();

        // The bindings of the grid's own columns, in document order; a nested grid's columns are its own.
        private static List<string> WebFormGridColumns(XElement grid)
            => grid.Descendants()
                .Where(e => e.Ancestors().First(a => a.Name.LocalName.EndsWith("grid", StringComparison.OrdinalIgnoreCase)) == grid)
                .Select(e => e.Attribute("attribute")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

        private static bool BindsVariable(string reference, string variableName, int? variableLayoutId)
        {
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(variableName)) return false;
            string value = reference.Trim();
            if (variableLayoutId.HasValue
                && string.Equals(value, "var:" + variableLayoutId.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
                return true;
            return value.StartsWith("&", StringComparison.Ordinal)
                && string.Equals(value.Substring(1), variableName.TrimStart('&'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The layout id (<c>var:N</c>) of the variable <paramref name="name"/> the parent declares; null when it has none.</summary>
        private static int? DeclaredVariableLayoutId(KBObject parent, string name)
        {
            if (parent == null || string.IsNullOrWhiteSpace(name)) return null;
            object variable = PartAccessor.GetVariableObjects(parent).FirstOrDefault(v =>
                string.Equals(PartAccessor.GetVariableName(v).TrimStart('&'), name, StringComparison.OrdinalIgnoreCase));
            // A fallback index of 0 answers 0 when the SDK exposes no Id, which is refused rather than guessed.
            int? id = variable == null ? null : VariableInjector.GetVariableInternalId(variable, 0);
            return id.HasValue && id.Value > 0 ? id : null;
        }
    }
}
