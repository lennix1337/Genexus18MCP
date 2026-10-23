using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Issue #595: a loose <table> (optionally a panel) created in a WorkWithPlus form, and an
    // existing leaf control moved into a table. Native Pattern SDK element commands first -
    // the path add_tab uses - with the XML projection for the preview and for the post-save
    // verification; exact snapshots, re-read, parent projection and rollback as the other
    // typed WorkWithPlus writes. A container's childrenOrderedList is maintained entry by
    // entry (PatternChildOrderEdit) and refused when it cannot be, never rebuilt by guess.
    public sealed partial class WwpActionService
    {
        private const string AddTableOperation = "add_table";
        private const string MoveControlOperation = "move_control";
        private static readonly string[] MovableControlKinds = { "textBlock", "variable", "attribute" };
        private static readonly Regex LayoutIdentifierPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static bool IsFormLayoutOperation(string operation) =>
            operation == AddTableOperation || operation == MoveControlOperation;

        private sealed class FormLayoutOperation
        {
            internal string Name, DryRunCode, UpdatedCode, FailedCode, NotPersistedCode, LogTag, SdkOperation, StaleMessage, SnapshotMessage;
            internal Func<XDocument, JObject, JObject> Plan;
            internal Func<object, JObject, JObject, JObject> ApplyNative;
            internal Func<XDocument, XDocument, JObject, JObject> VerifyPattern;
            internal Func<string, JObject, JObject> VerifyWebForm;
        }

        private static readonly FormLayoutOperation AddTableDescriptor = new FormLayoutOperation
        {
            Name = AddTableOperation, DryRunCode = "WwpTableDryRun", UpdatedCode = "WwpTableAdded",
            FailedCode = "WwpTableFailed", NotPersistedCode = "WwpTableNotPersisted", LogTag = "ADDTABLE",
            SdkOperation = "PatternInstance.RootElement + CreateChildElement(table) + InsertElementCommand/AddElementCommand",
            StaleMessage = "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no table was added.",
            SnapshotMessage = "Exact PatternInstance and parent WebForm snapshots were not available; no table was added.",
            Plan = PlanAddTableXml, ApplyNative = ApplyNativeAddTable,
            VerifyPattern = VerifyAddedTable, VerifyWebForm = VerifyAddedTableProjection
        };

        private static readonly FormLayoutOperation MoveControlDescriptor = new FormLayoutOperation
        {
            Name = MoveControlOperation, DryRunCode = "WwpControlMoveDryRun", UpdatedCode = "WwpControlMoved",
            FailedCode = "WwpControlMoveFailed", NotPersistedCode = "WwpControlMoveNotPersisted", LogTag = "MOVECONTROL",
            SdkOperation = "PatternInstance.RootElement + RemoveElementCommand + InsertElementCommand/AddElementCommand",
            StaleMessage = "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no control was moved.",
            SnapshotMessage = "Exact PatternInstance and parent WebForm snapshots were not available; no control was moved.",
            Plan = PlanMoveControlXml, ApplyNative = ApplyNativeMoveControl,
            VerifyPattern = VerifyMovedControl, VerifyWebForm = VerifyMovedControlProjection
        };

        private string RunFormLayoutOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, string operation, JObject args)
        {
            args = args ?? new JObject();
            FormLayoutOperation op = operation == AddTableOperation ? AddTableDescriptor : MoveControlDescriptor;
            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;

            XDocument previewDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject preview = op.Plan(previewDocument, args);
            if (preview["error"] != null)
                return McpResponse.Err(code: preview["code"]?.ToString() ?? "WwpLayoutInvalid",
                    message: preview["error"].ToString(), target: target, extra: preview);

            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            if (dryRun)
                return McpResponse.Ok(target: target, code: op.DryRunCode,
                    result: BuildFormLayoutReceipt(instance.Name, op, preview, versionToken, persisted: false, rollbackOnFailure));

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                if (currentInstance == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);
                // The instance that is saved, snapshotted and re-read must be the object that owns the
                // part being changed (as add_grid does). Resolving the part from the requested host
                // while the instance came from the read can yield two copies of the WorkWithPlus
                // object, and saving one with the other's part leaves the new table unpersisted.
                _patterns.BuildPatternPartEnvelope(currentInstance, "PatternInstance", currentXml, PatternRegistry.WorkWithPlusPatternId,
                    out currentInstance, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);

                string expectedVersion = args["baseVersion"]?.ToString()
                    ?? args["expectedVersion"]?.ToString()
                    ?? args["versionToken"]?.ToString();
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion, op.StaleMessage);

                // Recomputed under the per-object lock so a preview is never applied to a
                // PatternInstance that moved in the meantime.
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject plan = op.Plan(lockedAfter, args);
                if (plan["error"] != null)
                    return McpResponse.Err(code: plan["code"]?.ToString() ?? "WwpLayoutInvalid",
                        message: plan["error"].ToString(), target: target, extra: plan);

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired", message: op.SnapshotMessage,
                        target: target, extra: new JObject
                        {
                            ["snapshot"] = snapshots.ToJson(),
                            ["nativeBytesAvailable"] = nativeBytes != null,
                            ["parentResolved"] = parent != null,
                            ["parentWebFormAvailable"] = parentWebFormBefore != null,
                            ["persisted"] = false
                        });

                bool persistenceStarted = false;
                try
                {
                    JObject nativeMutation = op.ApplyNative(currentPart, args, plan);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected",
                            nativeMutation["error"].ToString());

                    persistenceStarted = true;
                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);
                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out KBObject persistedInstance, out _);
                    if (string.IsNullOrWhiteSpace(persistedXml))
                        throw new WwpTabException(op.NotPersistedCode, "The SDK save completed, but the PatternInstance could not be re-read.");

                    XDocument persistedDocument = XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace);
                    JObject verification = op.VerifyPattern(lockedAfter, persistedDocument, plan);
                    if (verification["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException(op.NotPersistedCode,
                            verification["message"]?.ToString()
                                ?? "The SDK save completed, but the requested layout state did not survive the PatternInstance re-read.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance, "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled",
                            "SDPlus_Editor_Apply_On_Save became False after the native layout save.");

                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent, persistedInstance ?? currentInstance);
                    if (!projected)
                        throw new WwpTabException("WwpProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");
                    JObject projection = op.VerifyWebForm(ReadPart(parent, "WebForm"), plan);
                    if (projection["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpProjectionNotConfirmed",
                            projection["message"]?.ToString() ?? "The projected WebForm did not contain the requested layout state.");

                    WriteService.NotePerTargetWrite(target);
                    JObject receipt = BuildFormLayoutReceipt(persistedInstance?.Name ?? currentInstance.Name, op, plan,
                        WriteService.ComputeContentVersionToken(persistedInstance ?? currentInstance, persistedXml),
                        persisted: true, rollbackOnFailure);
                    receipt["parent"] = parent.Name;
                    receipt["patternVerification"] = verification;
                    receipt["projection"] = projection;
                    receipt["applyOnSaveBefore"] = applyOnSaveBefore;
                    receipt["applyOnSaveAfter"] = applyOnSaveAfter;
                    receipt["applyOnSaveReenabled"] = applyOnSaveReenabled;
                    receipt["snapshot"] = snapshots.ToJson();
                    receipt["rollbackPerformed"] = false;
                    receipt["partialPersistenceDetected"] = false;
                    return McpResponse.Ok(target: target, code: op.UpdatedCode, result: receipt);
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    JObject rollback = rollbackOnFailure
                        ? RestoreSnapshots(currentInstance, currentPart, nativeBytes, currentXml, parent, parentWebFormBefore, applyOnSaveBefore)
                        : new JObject { ["performed"] = false, ["skipped"] = true, ["exact"] = false };
                    ExceptionRoot.Log("[WWP-" + op.LogTag + "] failed", ex);
                    return McpResponse.Err(code: typed?.Code ?? op.FailedCode, message: ExceptionRoot.Message(ex),
                        target: target, extra: new JObject
                        {
                            ["operation"] = op.Name,
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

        internal static JObject BuildFormLayoutReceipt(string instanceName, string operation, JObject plan, string versionToken, bool persisted)
            => BuildFormLayoutReceipt(instanceName, operation == AddTableOperation ? AddTableDescriptor : MoveControlDescriptor,
                plan, versionToken, persisted, true);

        private static JObject BuildFormLayoutReceipt(string instanceName, FormLayoutOperation op, JObject plan,
            string versionToken, bool persisted, bool rollbackOnFailure)
        {
            var receipt = new JObject { ["instance"] = instanceName, ["operation"] = op.Name };
            foreach (JProperty property in plan.Properties())
                if (property.Name != "changed" && property.Name != "error" && property.Name != "code")
                    receipt[property.Name] = property.Value.DeepClone();
            receipt["mutationMode"] = "native-pattern-sdk";
            receipt["sdkOperation"] = op.SdkOperation;
            receipt["versionToken"] = versionToken;
            receipt["persisted"] = persisted;
            receipt["saved"] = persisted;
            receipt["patternReReadConfirmed"] = persisted;
            receipt["webFormProjectionConfirmed"] = persisted;
            receipt["rollbackOnFailure"] = rollbackOnFailure;
            receipt["lifecycleExecuted"] = false;
            receipt["specified"] = false;
            receipt["generated"] = false;
            receipt["built"] = false;
            return receipt;
        }

        // ---- layout helpers shared by add_table and move_control ----------------------------

        private static JObject OrderedContainerUnsupported(string path, string reason)
        {
            JObject error = TableError("WwpOrderedContainerUnsupported",
                "The container '" + path + "' carries a childrenOrderedList the MCP cannot maintain (" + reason + "); no mutation was applied.");
            error["containerPath"] = path;
            error["hint"] = "The IDE hides children missing from childrenOrderedList, so the list is never rewritten by guess. Fix the list in the GeneXus IDE or choose another container.";
            return error;
        }

        private static JObject OrderingJson(PatternChildOrderEdit.Change change)
        {
            if (change == null || change.Before == null) return null;
            return new JObject
            {
                ["before"] = change.Before,
                ["after"] = change.After,
                ["entry"] = change.Entry,
                ["insertedBefore"] = change.InsertedBefore,
                ["changed"] = change.Changed
            };
        }

        private static void RestoreDocument(XDocument document, string snapshot)
        {
            document.Root.ReplaceWith(XDocument.Parse(snapshot, LoadOptions.PreserveWhitespace).Root);
        }

        private static void InsertChildAt(XElement container, XElement child, int index)
        {
            List<XElement> siblings = container.Elements().ToList();
            if (index >= siblings.Count) container.Add(child); else siblings[index].AddBeforeSelf(child);
        }

        // The label a sibling is addressed by for before/after: name, controlName, the field
        // name of an attribute reference, "Grid" for the unnamed grid, "ErrorViewer", or a
        // group table's title.
        private static string SiblingIdentity(XElement element)
        {
            string name = Attr(element, "name");
            if (name.Length > 0) return name;
            string controlName = Attr(element, "controlName");
            if (controlName.Length > 0) return controlName;
            string attribute = Attr(element, "attribute");
            if (attribute.Length > 0) { int dash = attribute.LastIndexOf('-'); return dash >= 0 ? attribute.Substring(dash + 1) : attribute; }
            if (Is(element, "grid")) return "Grid";
            if (Is(element, "errorViewer")) return "ErrorViewer";
            string title = Attr(element, "title");
            return title.Length > 0 ? title : string.Empty;
        }

        private static bool SiblingMatches(XElement element, string requested) =>
            string.Equals(SiblingIdentity(element), requested, StringComparison.OrdinalIgnoreCase)
            || (Is(element, "attribute") && AttributeReferenceMatches(Attr(element, "attribute"), requested));

        private static JObject ResolveInsertIndex(XElement container, JObject args, out int index)
        {
            List<XElement> siblings = container.Elements().ToList();
            index = siblings.Count;
            string before = args?["before"]?.ToString()?.Trim();
            string after = args?["after"]?.ToString()?.Trim();
            int? position = args?["position"]?.Type == JTokenType.Integer ? args["position"].Value<int>() : (int?)null;
            if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after))
            {
                string requested = string.IsNullOrWhiteSpace(before) ? after : before;
                int found = siblings.FindIndex(e => SiblingMatches(e, requested));
                if (found < 0)
                {
                    JObject error = TableError("PlacementSiblingNotFound",
                        "No child of '" + ContainerPath(container) + "' is addressed by '" + requested + "'.");
                    error["availableSiblings"] = new JArray(siblings.Select(SiblingIdentity).Where(s => s.Length > 0));
                    return error;
                }
                index = string.IsNullOrWhiteSpace(before) ? found + 1 : found;
            }
            else if (position.HasValue) index = ClampPosition(position, siblings.Count);
            return null;
        }

        // ---- ordered-list verification shared by add_table and move_control ------------------

        private static XElement ElementAtIndexPath(XElement root, JArray path)
        {
            if (path == null) return null;
            XElement cursor = root;
            foreach (JToken step in path)
            {
                cursor = cursor.Elements().ElementAtOrDefault(step.ToObject<int>());
                if (cursor == null) return null;
            }
            return cursor;
        }

        private static JObject NotConfirmed(string message) => new JObject { ["confirmed"] = false, ["message"] = message };

        // The document with one element removed, every childrenOrderedList dropped (compared
        // separately), attributes sorted and formatting whitespace ignored.
        private static XElement StripForComparison(XElement root, XElement removed)
        {
            var clone = new XElement(root);
            if (removed != null)
            {
                List<int> path = IndexPath(removed);
                XElement cursor = clone;
                foreach (int step in path) cursor = cursor.Elements().ElementAt(step);
                cursor.Remove();
            }
            foreach (XElement element in clone.DescendantsAndSelf())
            {
                element.Attributes().Where(a => a.Name.LocalName.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase)).ToList()
                    .ForEach(a => a.Remove());
                element.ReplaceAttributes(element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList());
                element.Nodes().OfType<XText>().Where(t => !(t is XCData) && string.IsNullOrWhiteSpace(t.Value)).ToList().ForEach(t => t.Remove());
            }
            return clone;
        }

        private static List<int> IndexPath(XElement element)
        {
            var path = new List<int>();
            for (XElement cursor = element; cursor.Parent != null; cursor = cursor.Parent)
                path.Insert(0, cursor.Parent.Elements().ToList().IndexOf(cursor));
            return path;
        }

        // Walks the planned and persisted trees in parallel (they are structurally equal once
        // the moved element is set aside) and checks every childrenOrderedList the plan wrote:
        // the persisted list must keep every planned entry, carry the required identifier on
        // the target container and no longer carry the moved identifier on the source.
        private static JObject VerifyOrderedLists(XElement plannedRoot, XElement persistedRoot, XElement plannedSkip, XElement persistedSkip,
            string requiredIdentifier, IList<KeyValuePair<XElement, string>> forbidden)
        {
            bool exact = true;
            var pairs = new List<KeyValuePair<XElement, XElement>>();
            if (!PairElements(plannedRoot, persistedRoot, plannedSkip, persistedSkip, pairs))
                return NotConfirmed("The persisted PatternInstance tree does not line up with the planned one.");
            foreach (var pair in pairs)
            {
                string plannedList = (string)pair.Key.Attribute("childrenOrderedList");
                if (plannedList == null) continue;
                string persistedList = (string)pair.Value.Attribute("childrenOrderedList");
                string label = ContainerPath(pair.Key);
                if (persistedList == null)
                    return NotConfirmed("The re-read container '" + label + "' lost its childrenOrderedList.");
                var persistedEntries = new HashSet<string>(PatternChildOrderEdit.Entries(persistedList), StringComparer.Ordinal);
                // The pattern may rewrite the new entry's level or type code on save; it must
                // still reference the identifier. Every other entry must survive verbatim.
                bool isTarget = ReferenceEquals(pair.Key, plannedSkip?.Parent) && requiredIdentifier != null;
                string lost = PatternChildOrderEdit.Entries(plannedList).FirstOrDefault(e => !persistedEntries.Contains(e)
                    && !(isTarget && PatternChildOrderEdit.Identifier(e).Equals(requiredIdentifier, StringComparison.OrdinalIgnoreCase)));
                if (lost != null)
                    return NotConfirmed("The re-read childrenOrderedList of '" + label + "' lost the entry '" + lost + "'.");
                if (isTarget && !PatternChildOrderEdit.References(persistedList, requiredIdentifier))
                    return NotConfirmed("The re-read childrenOrderedList of '" + label + "' does not reference '" + requiredIdentifier + "'; the IDE would hide it.");
                foreach (var rule in forbidden ?? new List<KeyValuePair<XElement, string>>())
                    if (ReferenceEquals(pair.Key, rule.Key) && PatternChildOrderEdit.References(persistedList, rule.Value))
                        return NotConfirmed("The re-read childrenOrderedList of '" + label + "' still references '" + rule.Value + "'.");
                if (!string.Equals(plannedList, persistedList, StringComparison.Ordinal)) exact = false;
            }
            return new JObject { ["confirmed"] = true, ["orderedListsExact"] = exact };
        }

        private static bool PairElements(XElement planned, XElement persisted, XElement plannedSkip, XElement persistedSkip,
            List<KeyValuePair<XElement, XElement>> pairs)
        {
            if (planned.Name != persisted.Name) return false;
            pairs.Add(new KeyValuePair<XElement, XElement>(planned, persisted));
            List<XElement> left = planned.Elements().Where(e => !ReferenceEquals(e, plannedSkip)).ToList();
            List<XElement> right = persisted.Elements().Where(e => !ReferenceEquals(e, persistedSkip)).ToList();
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!PairElements(left[i], right[i], plannedSkip, persistedSkip, pairs)) return false;
            return true;
        }
    }
}
