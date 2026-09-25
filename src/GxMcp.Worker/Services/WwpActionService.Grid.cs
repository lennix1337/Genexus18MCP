using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        private static bool IsGridAttributeOperation(string operation) =>
            string.Equals(operation, "add_grid_attribute", StringComparison.OrdinalIgnoreCase);

        private string RunGridAttributeOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, JObject args)
        {
            string attributeName = args?["attribute"]?.ToString()?.Trim();
            string caption = args?["caption"]?.ToString();
            if (string.IsNullOrWhiteSpace(attributeName))
                return McpResponse.Err(code: "MissingAttribute", message: "attribute is required.", target: target);
            if (string.IsNullOrWhiteSpace(caption))
                return McpResponse.Err(code: "MissingCaption", message: "caption is required.", target: target);
            string expectedVersion = args?["baseVersion"]?.ToString()
                ?? args?["expectedVersion"]?.ToString()
                ?? args?["versionToken"]?.ToString();
            if (string.IsNullOrWhiteSpace(expectedVersion))
                return McpResponse.Err(code: "ExpectedVersionRequired",
                    message: "baseVersion is required for add_grid_attribute, including dryRun previews.",
                    target: target, extra: new JObject { ["currentVersion"] = WriteService.ComputeContentVersionToken(instance, xml) });

            KBObject attribute = _objects.FindObject(attributeName, "Attribute");
            if (attribute == null || !string.Equals(attribute.TypeDescriptor?.Name, "Attribute", StringComparison.OrdinalIgnoreCase))
                return McpResponse.Err(code: "AttributeNotFound",
                    message: "Attribute '" + attributeName + "' was not found.", target: target);
            string attributeReference = attribute.Guid + "-" + attribute.Name;

            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument afterDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject mutation = ApplyGridAttributeXml(afterDocument, attributeReference, attribute.Name, caption);
            if (mutation["error"] != null)
                return McpResponse.Err(code: mutation["code"]?.ToString() ?? "WwpGridAttributeInvalid",
                    message: mutation["error"].ToString(), target: target, extra: mutation);

            bool existedBefore = GridAttributeState(beforeDocument, attribute.Name)["present"]?.ToObject<bool>() == true;
            JArray unrelatedChanges = FindGridAttributeUnrelatedChanges(
                beforeDocument, afterDocument, attribute.Name, existedBefore);
            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            JObject requestedChange = RequestedGridAttributeChange(beforeDocument, afterDocument, attribute.Name);
            if (args?["dryRun"]?.ToObject<bool?>() == true)
                return McpResponse.Ok(target: target, code: "WwpGridAttributeDryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["operation"] = "add_grid_attribute",
                    ["attribute"] = attribute.Name,
                    ["caption"] = caption,
                    ["requestedChange"] = requestedChange,
                    ["unrelatedChanges"] = unrelatedChanges,
                    ["versionToken"] = versionToken,
                    ["persisted"] = false,
                    ["patternReReadConfirmed"] = false,
                    ["webFormProjectionConfirmed"] = false,
                    ["rollbackPerformed"] = false,
                    ["lifecycleExecuted"] = false,
                    ["specified"] = false,
                    ["generated"] = false,
                    ["built"] = false
                });

            if (unrelatedChanges.Count != 0)
                return McpResponse.Err(code: "WwpGridAttributeDryRunNotIsolated",
                    message: "The requested grid-attribute change was not isolated; no mutation was applied.",
                    target: target, extra: new JObject { ["unrelatedChanges"] = unrelatedChanges });

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                _patterns.BuildPatternPartEnvelope(lockedTarget, "PatternInstance", currentXml, PatternRegistry.WorkWithPlusPatternId,
                    out _, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return McpResponse.Err(code: "WWPInstanceNotFound",
                        message: "The WorkWithPlus PatternInstance could not be re-resolved before save.", target: target);

                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return McpResponse.Err(code: "StaleObject",
                        message: "The PatternInstance changed after the caller's read/dry-run; no grid attribute was changed.",
                        target: target, extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = currentVersion
                        });

                XDocument lockedBefore = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedMutation = ApplyGridAttributeXml(
                    lockedAfter, attributeReference, attribute.Name, caption);
                if (lockedMutation["error"] != null)
                    return McpResponse.Err(code: lockedMutation["code"]?.ToString() ?? "WwpGridAttributeInvalid",
                        message: lockedMutation["error"].ToString(), target: target, extra: lockedMutation);
                bool lockedExistedBefore = GridAttributeState(lockedBefore, attribute.Name)["present"]?.ToObject<bool>() == true;
                JArray lockedUnrelated = FindGridAttributeUnrelatedChanges(
                    lockedBefore, lockedAfter, attribute.Name, lockedExistedBefore);
                if (lockedUnrelated.Count != 0)
                    return McpResponse.Err(code: "WwpGridAttributeDryRunNotIsolated",
                        message: "The recomputed grid-attribute change was not isolated; no mutation was applied.",
                        target: target, extra: new JObject { ["unrelatedChanges"] = lockedUnrelated });

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");

                if (nativeBytes == null || parent == null || parentWebFormBefore == null
                    || snapshots.Pattern == null || snapshots.WebForm == null)
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "Complete PatternInstance and WebForm snapshots are required; no mutation was applied.",
                        target: target, extra: new JObject { ["snapshot"] = snapshots.ToJson(), ["persisted"] = false });

                bool persistenceStarted = false;
                try
                {
                    JObject nativeMutation = ApplyNativeGridAttributeMutation(
                        currentPart, attributeReference, attribute.Name, caption);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected",
                            nativeMutation["error"].ToString());

                    persistenceStarted = true;
                    SaveNativePattern(currentInstance, currentPart);
                    string applyOnSaveAfterSave = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                    bool applyOnSaveReenabled = false;
                    if (!IsFalse(applyOnSaveBefore) && IsFalse(applyOnSaveAfterSave))
                        applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);

                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out KBObject persistedInstance, out _);
                    XDocument persistedDocument = string.IsNullOrWhiteSpace(persistedXml)
                        ? null : XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace);
                    JObject persistedState = GridAttributeState(persistedDocument, attribute.Name);
                    JArray persistedUnrelated = persistedDocument == null
                        ? new JArray("PatternInstance could not be re-read.")
                        : FindGridAttributeUnrelatedChanges(
                            lockedBefore, persistedDocument, attribute.Name, lockedExistedBefore);
                    bool patternConfirmed = persistedState["present"]?.ToObject<bool>() == true
                        && persistedState["count"]?.ToObject<int?>() == 1
                        && string.Equals(persistedState["caption"]?.ToString(), caption, StringComparison.Ordinal)
                        && persistedUnrelated.Count == 0;
                    if (!patternConfirmed)
                        throw new WwpTabException("WwpGridAttributeNotPersisted",
                            "The PatternInstance re-read did not contain only the requested grid attribute and caption.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance,
                        "SDPlus_Editor_Apply_On_Save");
                    if (!string.Equals(applyOnSaveAfter, applyOnSaveBefore, StringComparison.OrdinalIgnoreCase))
                        throw new WwpTabException("WwpApplyOnSaveChanged",
                            "SDPlus_Editor_Apply_On_Save changed during the grid-attribute save.");

                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(
                        parent, persistedInstance ?? currentInstance);
                    if (!projected)
                        throw new WwpTabException("WwpProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");

                    string projectedWebForm = ReadPart(parent, "WebForm");
                    bool projectionConfirmed = !string.IsNullOrWhiteSpace(projectedWebForm)
                        && projectedWebForm.IndexOf(attribute.Name, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!projectionConfirmed)
                        throw new WwpTabException("WwpProjectionNotConfirmed",
                            "The projected WebForm did not contain the requested grid attribute.");

                    WriteService.NotePerTargetWrite(target);
                    return McpResponse.Ok(target: target, code: "WwpGridAttributeUpdated", result: new JObject
                    {
                        ["instance"] = persistedInstance?.Name ?? currentInstance.Name,
                        ["parent"] = parent.Name,
                        ["operation"] = "add_grid_attribute",
                        ["attribute"] = attribute.Name,
                        ["caption"] = caption,
                        ["requestedChange"] = RequestedGridAttributeChange(
                            lockedBefore, persistedDocument, attribute.Name),
                        ["unrelatedChanges"] = persistedUnrelated,
                        ["versionToken"] = WriteService.ComputeContentVersionToken(
                            persistedInstance ?? currentInstance, persistedXml),
                        ["persisted"] = true,
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["applyOnSaveBefore"] = applyOnSaveBefore,
                        ["applyOnSaveAfter"] = applyOnSaveAfter,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollbackPerformed"] = false,
                        ["partialPersistenceDetected"] = false,
                        ["lifecycleExecuted"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false
                    });
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    JObject rollback = RestoreSnapshots(currentInstance, currentPart, nativeBytes,
                        currentXml, parent, parentWebFormBefore, applyOnSaveBefore);
                    return McpResponse.Err(code: typed?.Code ?? "WwpGridAttributeFailed", message: ex.Message,
                        target: target, extra: new JObject
                        {
                            ["persisted"] = false,
                            ["partialPersistenceDetected"] = persistenceStarted,
                            ["patternReReadConfirmed"] = false,
                            ["webFormProjectionConfirmed"] = false,
                            ["snapshot"] = snapshots.ToJson(),
                            ["rollback"] = rollback,
                            ["rollbackPerformed"] = true,
                            ["stateRestoredExactly"] = rollback["exact"]?.DeepClone() ?? false,
                            ["lifecycleExecuted"] = false,
                            ["specified"] = false,
                            ["generated"] = false,
                            ["built"] = false
                        });
                }
            }
        }

        internal static JObject ApplyGridAttributeXml(XDocument document, string attributeReference,
            string attributeName, string caption)
        {
            if (document == null) return GridError("InvalidPatternInstance", "PatternInstance XML is required.");
            List<XElement> grids = document.Descendants().Where(e => Is(e, "grid")).ToList();
            if (grids.Count == 0) return GridError("GridNotFound", "The PatternInstance has no grid.");
            if (grids.Count != 1) return GridError("AmbiguousGrid", "The PatternInstance has more than one grid; no grid was guessed.");
            List<XElement> matches = grids[0].Elements().Where(e => Is(e, "gridAttribute")
                && IsAttributeReference(Attr(e, "attribute"), attributeName)).ToList();
            if (matches.Count > 1) return GridError("DuplicateGridAttribute", "The grid contains duplicate references to the requested attribute.");

            XElement item = matches.SingleOrDefault();
            bool added = item == null;
            string oldCaption = item == null ? null : Attr(item, "description");
            if (added)
            {
                item = new XElement(grids[0].GetDefaultNamespace() + "gridAttribute",
                    new XAttribute("attribute", attributeReference),
                    new XAttribute("description", caption));
                grids[0].Add(item);
            }
            else item.SetAttributeValue("description", caption);
            return new JObject { ["changed"] = added || !string.Equals(oldCaption, caption, StringComparison.Ordinal), ["added"] = added };
        }

        internal static JArray FindGridAttributeUnrelatedChanges(XDocument before, XDocument after,
            string attributeName, bool existedBefore)
        {
            if (before == null || after == null) return new JArray("PatternInstance is unavailable.");
            XDocument left = XDocument.Parse(before.ToString(SaveOptions.DisableFormatting), LoadOptions.PreserveWhitespace);
            XDocument right = XDocument.Parse(after.ToString(SaveOptions.DisableFormatting), LoadOptions.PreserveWhitespace);
            NormalizeRequestedGridAttribute(left, attributeName, existedBefore);
            NormalizeRequestedGridAttribute(right, attributeName, existedBefore);
            return string.Equals(CanonicalXml(left), CanonicalXml(right), StringComparison.Ordinal)
                ? new JArray()
                : new JArray("PatternInstance changed outside the requested grid attribute.");
        }

        private static void NormalizeRequestedGridAttribute(XDocument document, string attributeName, bool existedBefore)
        {
            foreach (XElement item in document.Descendants().Where(e => Is(e, "gridAttribute")
                && IsAttributeReference(Attr(e, "attribute"), attributeName)).ToList())
            {
                if (existedBefore) item.SetAttributeValue("description", "__requested_caption__");
                else item.Remove();
            }
        }

        private static string CanonicalXml(XDocument document)
        {
            var value = new StringBuilder();
            foreach (XElement element in document.Elements()) AppendCanonical(element, value);
            return value.ToString();
        }

        private static void AppendCanonical(XElement element, StringBuilder value)
        {
            value.Append('<').Append(element.Name);
            foreach (XAttribute attribute in element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
                value.Append('|').Append(attribute.Name).Append('=').Append(attribute.Value);
            value.Append('>');
            foreach (XNode node in element.Nodes())
            {
                XElement child = node as XElement;
                if (child != null) AppendCanonical(child, value);
                else if (node is XText text && !string.IsNullOrWhiteSpace(text.Value)) value.Append(text.Value);
            }
            value.Append("</").Append(element.Name).Append('>');
        }

        private static JObject RequestedGridAttributeChange(XDocument before, XDocument after, string attributeName) =>
            new JObject
            {
                ["path"] = "PatternInstance/grid/gridAttribute[attribute='" + attributeName + "']",
                ["before"] = GridAttributeState(before, attributeName),
                ["after"] = GridAttributeState(after, attributeName)
            };

        private static JObject GridAttributeState(XDocument document, string attributeName)
        {
            List<XElement> items = document?.Descendants().Where(e => Is(e, "gridAttribute")
                && IsAttributeReference(Attr(e, "attribute"), attributeName)).ToList()
                ?? new List<XElement>();
            XElement item = items.FirstOrDefault();
            return item == null
                ? new JObject { ["present"] = false, ["count"] = 0 }
                : new JObject
                {
                    ["present"] = true,
                    ["count"] = items.Count,
                    ["attribute"] = attributeName,
                    ["caption"] = Attr(item, "description")
                };
        }

        private static bool IsAttributeReference(string reference, string attributeName) =>
            string.Equals(reference, attributeName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(reference) && reference.EndsWith("-" + attributeName, StringComparison.OrdinalIgnoreCase));

        private static JObject ApplyNativeGridAttributeMutation(KBObjectPart part, string attributeReference,
            string attributeName, string caption)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return GridError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            List<object> grids = Walk(root).Where(e => NativeType(e).Equals("grid", StringComparison.OrdinalIgnoreCase)).ToList();
            if (grids.Count == 0) return GridError("GridNotFound", "The native PatternInstance has no grid.");
            if (grids.Count != 1) return GridError("AmbiguousGrid", "The native PatternInstance has more than one grid; no grid was guessed.");
            object grid = grids[0];
            List<object> matches = NativeChildren(grid).Where(e => NativeType(e).Equals("gridAttribute", StringComparison.OrdinalIgnoreCase)
                && IsAttributeReference(NativeAttribute(e, "attribute"), attributeName)).ToList();
            if (matches.Count > 1) return GridError("DuplicateGridAttribute", "The grid contains duplicate references to the requested attribute.");

            Action mutation = () =>
            {
                object item = matches.SingleOrDefault();
                if (item == null)
                {
                    item = CreateNativeChild(grid, "gridAttribute");
                    SetNativeAttribute(item, "attribute", attributeReference);
                    SetNativeAttribute(item, "description", caption);
                    ExecuteElementCommand(grid, item, "AddElementCommand", null);
                }
                else SetNativeAttribute(item, "description", caption);
            };
            var executeUpdate = part.GetType().GetMethod("ExecuteUpdate", new[] { typeof(string), typeof(Action) });
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp add_grid_attribute", mutation });
            else mutation();
            return new JObject { ["changed"] = true };
        }

        private static JObject GridError(string code, string message) => new JObject { ["code"] = code, ["error"] = message };
    }
}
