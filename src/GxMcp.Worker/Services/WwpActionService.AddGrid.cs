using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // add_grid (#420): a grid over an SDT collection, written the way WorkWithPlus's own wizard
    // writes it: every column is a gridVariable named after the collection variable and bound to
    // an SDT item through sdtItem, with domain = <SDT type GUID>-<Module.Sdt>.
    public sealed partial class WwpActionService
    {
        private const string DeleteActionName = "UDelete";

        private static bool IsAddGridOperation(string operation) => string.Equals(operation, "add_grid", StringComparison.OrdinalIgnoreCase);

        internal sealed class GridColumn
        {
            internal string Item;
            internal string Description;
            internal XElement Variable;
        }

        /// <summary>Columns are item names or {item, description}; at least one, no repeated item.</summary>
        internal static JObject ParseGridColumns(JToken token, out List<GridColumn> columns)
        {
            columns = new List<GridColumn>();
            if (!(token is JArray array) || array.Count == 0)
                return Error("MissingGridColumns", "columns must be a non-empty array of SDT item names or {item, description}.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken entry in array)
            {
                string item = entry.Type == JTokenType.String ? entry.Value<string>() : (entry as JObject)?["item"]?.ToString();
                string description = entry is JObject obj ? obj["description"]?.ToString() : null;
                if (string.IsNullOrWhiteSpace(item) || !Regex.IsMatch(item.Trim(), "^[A-Za-z_][A-Za-z0-9_]*$"))
                    return Error("InvalidGridColumn", "Every column needs an SDT item name.");
                item = item.Trim();
                if (!seen.Add(item))
                    return Error("DuplicateGridColumn", "SDT item '" + item + "' appears more than once in columns.");
                columns.Add(new GridColumn { Item = item, Description = string.IsNullOrWhiteSpace(description) ? item : description });
            }
            return null;
        }

        internal static JObject ValidateAddGridArgs(JObject args, out string collection, out List<GridColumn> columns)
        {
            columns = new List<GridColumn>();
            collection = args?["collection"]?.ToString()?.Trim();
            if (IsVariableGrid(args))
                return ValidateVariableGridArgs(args, out columns);
            if (string.IsNullOrEmpty(collection) || !Regex.IsMatch(collection, "^&[A-Za-z_][A-Za-z0-9_]*$"))
                return Error("InvalidGridCollection", "collection must be an SDT collection variable such as &Lines.");
            if (string.IsNullOrWhiteSpace(args["sdt"]?.ToString()))
                return Error("MissingGridSdt", "sdt (the SDT behind the collection) is required.");
            if (string.IsNullOrWhiteSpace(args["containerName"]?.ToString()))
                return Error("MissingGridContainer", "containerName is required (a table of the instance).");
            JToken delete = args["deleteAction"];
            if (delete != null && delete.Type != JTokenType.Boolean && delete.Type != JTokenType.Null)
                return Error("InvalidDeleteAction", "deleteAction must be true or false.");
            return ParseGridColumns(args["columns"], out columns);
        }

        private static bool WantsDeleteAction(JObject args) => args?["deleteAction"]?.Type == JTokenType.Boolean && args["deleteAction"].Value<bool>();

        private static XElement FindGridOver(XDocument document, string collection)
            => document.Descendants().FirstOrDefault(e => Is(e, "grid")
                && string.Equals(Attr(e, "SDTCollection"), collection, StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds the grid to the XML projection. Needs <c>_sdtReference</c> (and <c>_sdtItems</c> when known).</summary>
        internal static JObject ApplyAddGridXml(XDocument document, JObject args)
        {
            JObject invalid = ValidateAddGridArgs(args, out string collection, out List<GridColumn> columns);
            if (invalid != null) return invalid;
            if (IsVariableGrid(args)) return ApplyVariableGridXml(document, args, columns);
            string reference = args["_sdtReference"]?.ToString();
            if (string.IsNullOrWhiteSpace(reference)) return Error("GridSdtUnresolved", "sdt '" + args["sdt"] + "' was not resolved to an SDT.");
            if (args["_sdtItems"] is JArray known && known.Count > 0)
            {
                foreach (GridColumn column in columns)
                    if (!known.Any(k => string.Equals(k.ToString(), column.Item, StringComparison.OrdinalIgnoreCase)))
                        return Error("GridColumnNotInSdt", "'" + column.Item + "' is not an item of SDT '" + args["sdt"] + "'.");
            }

            string containerName = args["containerName"].ToString().Trim();
            List<XElement> matches = FindFormContainers(document, containerName).ToList();
            if (matches.Count > 1)
                return new JObject
                {
                    ["code"] = "GridContainerAmbiguous",
                    ["error"] = "Container '" + containerName + "' matched more than one element; qualify it with a path.",
                    ["matchingContainers"] = new JArray(matches.Select(DescribeFormContainer))
                };
            XElement container = matches.SingleOrDefault();
            if (container == null) return Error("GridContainerNotFound", "Container '" + containerName + "' was not found.");
            if (!Is(container, "table")) return Error("GridContainerNotTable", "A grid goes in a table, not in an action group.");
            if (FindGridOver(document, collection) != null)
                return Error("GridAlreadyExists", "The instance already has a grid over " + collection + ".");

            XNamespace ns = container.GetDefaultNamespace();
            string variableName = collection.Substring(1);
            var grid = new XElement(ns + "grid",
                new XAttribute("cellThemeClass", "SectionGrid GridNoBorderCell"),
                new XAttribute("SDTCollection", collection));
            foreach (GridColumn column in columns)
                grid.Add(new XElement(ns + "gridVariable",
                    new XAttribute("domain", reference), new XAttribute("name", variableName),
                    new XAttribute("description", column.Description), new XAttribute("sdtItem", column.Item)));
            if (WantsDeleteAction(args))
                grid.Add(new XElement(ns + "userAction",
                    new XAttribute("tooltip", "Delete item"), new XAttribute("imageClass", "fas fa-times"),
                    new XAttribute("ControlType", "Image"), new XAttribute("name", DeleteActionName),
                    new XAttribute("imageType", "Font icon")));
            container.Add(grid);

            return new JObject
            {
                ["changed"] = true,
                ["collection"] = collection,
                ["sdt"] = args["sdt"].ToString(),
                ["containerName"] = containerName,
                ["columns"] = new JArray(columns.Select(c => c.Item)),
                ["deleteAction"] = WantsDeleteAction(args)
            };
        }

        /// <summary>The re-read grid must hold every column in order, the SDT reference and (when asked) the delete action.</summary>
        internal static JObject VerifyAddGrid(XDocument after, string collection, string reference, IList<GridColumn> columns, bool deleteAction)
        {
            XElement grid = FindGridOver(after, collection);
            if (grid == null) return FormActionError("WwpGridNotPersisted", "The re-read PatternInstance has no grid over " + collection + ".");
            List<XElement> persisted = grid.Elements().Where(e => Is(e, "gridVariable")).ToList();
            if (persisted.Count != columns.Count
                || !persisted.Select(e => Attr(e, "sdtItem")).SequenceEqual(columns.Select(c => c.Item), StringComparer.Ordinal))
                return FormActionError("WwpGridNotPersisted", "The re-read grid lost, reordered or renamed a column.");
            if (persisted.Any(e => !string.Equals(Attr(e, "domain"), reference, StringComparison.OrdinalIgnoreCase)))
                return FormActionError("WwpGridNotPersisted", "The re-read grid lost the SDT reference.");
            if (persisted.Select((e, i) => string.Equals(Attr(e, "description"), columns[i].Description, StringComparison.Ordinal)).Any(ok => !ok))
                return FormActionError("WwpGridNotPersisted", "The re-read grid changed a column description.");
            if (deleteAction && !grid.Elements().Any(e => Is(e, "userAction") && Attr(e, "name") == DeleteActionName))
                return FormActionError("WwpGridNotPersisted", "The re-read grid lost the delete action.");
            return new JObject { ["confirmed"] = true };
        }

        private static List<string> SdtItemNames(KBObject sdt)
        {
            var names = new List<string>();
            try
            {
                foreach (KBObjectPart part in sdt.Parts)
                {
                    object root = part.GetType().GetProperty("Root")?.GetValue(part, null);
                    if (!(root?.GetType().GetProperty("Items")?.GetValue(root, null) is System.Collections.IEnumerable items)) continue;
                    foreach (object item in items)
                    {
                        string name = item.GetType().GetProperty("Name")?.GetValue(item, null)?.ToString();
                        if (!string.IsNullOrEmpty(name)) names.Add(name);
                    }
                    if (names.Count > 0) break;
                }
            }
            catch { }
            return names;
        }

        // ---- native (SDK) side -------------------------------------------------------------

        /// <summary>The table or action group a form control goes into; a bare name must be unique, otherwise a path.</summary>
        private static JObject ResolveNativeContainer(object root, string containerName, out object container)
        {
            container = null;
            var containers = new List<object>();
            var containerPaths = new List<string>();
            var available = new JArray();
            foreach (var entry in WalkWithPath(root, new List<string>()))
            {
                if (!IsNativeTable(entry.Key) && !IsNativeActionGroup(entry.Key)) continue;
                string path = string.Join("/", entry.Value);
                available.Add(path);
                if (ContainerPathMatches(entry.Value, NativeAttribute(entry.Key, "name"), NativeAttribute(entry.Key, "controlName"), containerName))
                {
                    containers.Add(entry.Key);
                    containerPaths.Add(path);
                }
            }
            if (containers.Count > 1)
            {
                JObject ambiguous = FormActionError("FormActionContainerAmbiguous",
                    "Form action container '" + containerName + "' matched more than one native table or action group; qualify it with a path such as General/Table/Actions.");
                ambiguous["matchingContainers"] = new JArray(containerPaths.Select(path => new JObject { ["path"] = path }));
                return ambiguous;
            }
            if (containers.Count == 0)
            {
                JObject notFound = FormActionError("FormActionContainerNotFound",
                    "Form action container '" + containerName + "' was not found in the native PatternInstance.");
                notFound["availablePaths"] = available;
                return notFound;
            }
            container = containers[0];
            return null;
        }

        // Several grid attributes are typed, and writing their displayed text fails when the
        // instance is loaded with InvalidCastException: domain is a KBObject, cellThemeClass and
        // imageClass are DropDownValueOptionCustomType { Value }. They are written with their type.
        private static void SetNativeDropDown(object element, string attribute, string value)
        {
            Type optionType = element.GetType().Assembly.GetTypes().FirstOrDefault(t => t.Name == "DropDownValueOptionCustomType");
            object option = optionType == null ? null : Activator.CreateInstance(optionType, true);
            PropertyInfo valueProperty = optionType?.GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (option == null || valueProperty == null || !valueProperty.CanWrite)
                throw new WwpTabException("WwpAttributeRejected", "The WorkWithPlus package does not expose DropDownValueOptionCustomType for '" + attribute + "'.");
            valueProperty.SetValue(option, value, null);
            if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(element, attribute, option))
                throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected typed attribute '" + attribute + "'.");
        }

        private static JObject ApplyNativeAddGrid(KBObjectPart part, JObject args, KBObject sdt)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return FormActionError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject invalid = ValidateAddGridArgs(args, out string collection, out List<GridColumn> columns);
            if (invalid != null) return invalid;
            JObject containerError = ResolveNativeContainer(root, args["containerName"].ToString().Trim(), out object container);
            if (containerError != null) return containerError;
            if (!IsNativeTable(container)) return Error("GridContainerNotTable", "A grid goes in a table, not in an action group.");
            if (Walk(root).Any(e => NativeType(e).Equals("grid", StringComparison.OrdinalIgnoreCase)
                && string.Equals(NativeAttribute(e, "SDTCollection"), collection, StringComparison.OrdinalIgnoreCase)))
                return Error("GridAlreadyExists", "The instance already has a grid over " + collection + ".");

            string variableName = collection.Substring(1);
            bool deleteAction = WantsDeleteAction(args);
            Action mutation = () =>
            {
                object grid = CreateNativeChild(container, "grid");
                SetNativeAttribute(grid, "SDTCollection", collection);
                SetNativeDropDown(grid, "cellThemeClass", "SectionGrid GridNoBorderCell");
                foreach (GridColumn column in columns)
                {
                    object variable = CreateNativeChild(grid, "gridVariable");
                    SetNativeAttribute(variable, "name", variableName);
                    SetNativeAttribute(variable, "description", column.Description);
                    SetNativeAttribute(variable, "sdtItem", column.Item);
                    if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(variable, "domain", sdt))
                        throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected the SDT as the column domain.");
                    ExecuteElementCommand(grid, variable, "AddElementCommand", null);
                }
                if (deleteAction)
                {
                    object delete = CreateNativeChild(grid, "userAction");
                    SetNativeAttribute(delete, "name", DeleteActionName);
                    SetNativeAttribute(delete, "tooltip", "Delete item");
                    SetNativeAttribute(delete, "ControlType", "Image");
                    SetNativeAttribute(delete, "imageType", "Font icon");
                    SetNativeDropDown(delete, "imageClass", "fas fa-times");
                    ExecuteElementCommand(grid, delete, "AddElementCommand", null);
                }
                ExecuteElementCommand(container, grid, "AddElementCommand", null);
            };

            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Action) }, null);
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp add_grid", mutation });
            else mutation();

            return new JObject
            {
                ["changed"] = true,
                ["collection"] = collection,
                ["columns"] = new JArray(columns.Select(c => c.Item)),
                ["sdkOperation"] = "CreateChildElement(grid/gridVariable/userAction) + AddElementCommand"
            };
        }

        private string RunAddGridOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, JObject args)
        {
            JObject invalid = ValidateAddGridArgs(args, out string collection, out List<GridColumn> columns);
            if (invalid != null)
                return McpResponse.Err(code: invalid["code"].ToString(), message: invalid["error"].ToString(), target: target);
            KBObject sdt = null;
            string reference = null;
            if (!IsVariableGrid(args))
            {
                sdt = _objects.FindObject(args["sdt"].ToString().Trim(), "SDT");
                if (sdt == null || !string.Equals(sdt.TypeDescriptor?.Name, "SDT", StringComparison.OrdinalIgnoreCase))
                    return McpResponse.Err(code: "GridSdtNotFound", message: "sdt '" + args["sdt"] + "' is not an SDT in this KB.", target: target);
                reference = GxObjectReference(sdt);
                args["_sdtReference"] = reference;
                args["_sdtItems"] = new JArray(SdtItemNames(sdt));
            }

            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument previewDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject previewMutation = ApplyAddGridXml(previewDocument, args);
            if (previewMutation["error"] != null)
                return McpResponse.Err(code: previewMutation["code"]?.ToString() ?? "WwpGridInvalid",
                    message: previewMutation["error"].ToString(), target: target, extra: previewMutation);

            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            bool deleteAction = WantsDeleteAction(args);
            JObject diff = new JObject { ["before"] = Project(beforeDocument), ["after"] = Project(previewDocument), ["grid"] = ProjectAddedGrid(previewDocument, args, collection) };

            if (dryRun)
                return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["operation"] = "add_grid",
                    ["diff"] = diff,
                    ["versionToken"] = versionToken,
                    ["saved"] = false,
                    ["persisted"] = false,
                    ["rollbackOnFailure"] = rollbackOnFailure,
                    ["mutationMode"] = "native-pattern-sdk",
                    ["sdkOperation"] = "PatternInstance.RootElement + AddElementCommand(grid)"
                });

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                if (currentInstance == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);
                _patterns.BuildPatternPartEnvelope(currentInstance, "PatternInstance", currentXml, PatternRegistry.WorkWithPlusPatternId,
                    out currentInstance, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);

                string expectedVersion = args["baseVersion"]?.ToString() ?? args["expectedVersion"]?.ToString() ?? args["versionToken"]?.ToString();
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion) && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion,
                        "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no grid was added.");

                XDocument lockedBefore = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedMutation = ApplyAddGridXml(lockedAfter, args);
                if (lockedMutation["error"] != null)
                    return McpResponse.Err(code: lockedMutation["code"]?.ToString() ?? "WwpGridInvalid",
                        message: lockedMutation["error"].ToString(), target: target, extra: lockedMutation);

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "Exact PatternInstance and parent WebForm snapshots were not available; no grid was added.",
                        target: target, extra: new JObject { ["snapshot"] = snapshots.ToJson(), ["persisted"] = false });

                bool persistenceStarted = false;
                try
                {
                    JObject nativeMutation = IsVariableGrid(args)
                        ? ApplyNativeVariableGrid(currentPart, args, columns)
                        : ApplyNativeAddGrid(currentPart, args, sdt);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected", nativeMutation["error"].ToString());

                    persistenceStarted = true;
                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);
                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out KBObject persistedInstance, out _);
                    if (string.IsNullOrWhiteSpace(persistedXml))
                        throw new WwpTabException("WwpGridNotPersisted", "The SDK save completed, but the PatternInstance could not be re-read.");

                    XDocument persistedDocument = XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace);
                    JObject verification = IsVariableGrid(args)
                        ? VerifyVariableGrid(persistedDocument, args, columns)
                        : VerifyAddGrid(persistedDocument, collection, reference, columns, deleteAction);
                    if (verification["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpGridNotPersisted", verification["error"]?.ToString() ?? "The requested grid was not confirmed after re-read.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance, "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled", "SDPlus_Editor_Apply_On_Save became False after the native grid save.");

                    if (!WwpProjectionHelper.TryProjectHostOntoParent(parent, persistedInstance ?? currentInstance))
                        throw new WwpTabException("WwpProjectionFailed", "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");
                    string projectedWebForm = ReadPart(parent, "WebForm");
                    if (string.IsNullOrWhiteSpace(projectedWebForm) || string.Equals(NormalizeText(projectedWebForm), NormalizeText(parentWebFormBefore), StringComparison.Ordinal))
                        throw new WwpTabException("WwpProjectionNotConfirmed", "The parent WebForm did not change after projection.");

                    WriteService.NotePerTargetWrite(target);
                    return McpResponse.Ok(target: target, code: "WwpGridAdded", result: new JObject
                    {
                        ["instance"] = persistedInstance?.Name ?? currentInstance.Name,
                        ["parent"] = parent.Name,
                        ["operation"] = "add_grid",
                        ["collection"] = collection,
                        ["columns"] = new JArray(columns.Select(c => c.Item)),
                        ["deleteAction"] = deleteAction,
                        ["gridName"] = IsVariableGrid(args) ? VariableGridName(args) : null,
                        ["diff"] = new JObject { ["before"] = Project(lockedBefore), ["after"] = Project(persistedDocument), ["grid"] = ProjectAddedGrid(persistedDocument, args, collection) },
                        ["versionToken"] = WriteService.ComputeContentVersionToken(persistedInstance ?? currentInstance, persistedXml),
                        ["persisted"] = true,
                        ["saved"] = true,
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["mutationMode"] = "native-pattern-sdk",
                        ["sdkOperation"] = "PatternInstance.RootElement + AddElementCommand(grid)",
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollbackPerformed"] = false,
                        ["lifecycleExecuted"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false
                    });
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    JObject rollback = rollbackOnFailure
                        ? RestoreSnapshots(currentInstance, currentPart, nativeBytes, currentXml, parent, parentWebFormBefore, applyOnSaveBefore)
                        : new JObject { ["performed"] = false, ["skipped"] = true, ["exact"] = false };
                    ExceptionRoot.Log("[WWP-ADDGRID] failed", ex);
                    return McpResponse.Err(code: typed?.Code ?? "WwpGridFailed", message: ExceptionRoot.Message(ex),
                        target: target, extra: new JObject
                        {
                            ["exceptionType"] = ExceptionRoot.Unwrap(ex)?.GetType().Name,
                            ["failureTrace"] = ExceptionRoot.FailureTrace(ex),
                            ["persisted"] = false,
                            ["saved"] = false,
                            ["partialPersistenceDetected"] = persistenceStarted,
                            ["patternReReadConfirmed"] = false,
                            ["webFormProjectionConfirmed"] = false,
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

        private static JObject ProjectGridOver(XDocument document, string collection)
        {
            XElement grid = FindGridOver(document, collection);
            if (grid == null) return null;
            return new JObject
            {
                ["collection"] = collection,
                ["columns"] = new JArray(grid.Elements().Where(e => Is(e, "gridVariable")).Select(e => new JObject
                {
                    ["item"] = Attr(e, "sdtItem"), ["description"] = Attr(e, "description"), ["domain"] = Attr(e, "domain")
                })),
                ["actions"] = new JArray(grid.Elements().Where(e => Is(e, "userAction")).Select(e => Attr(e, "name")))
            };
        }
    }
}
