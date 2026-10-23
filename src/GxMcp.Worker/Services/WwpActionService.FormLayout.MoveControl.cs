using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // move_control of the form-layout operations (issue #595); the shared runner is in WwpActionService.FormLayout.cs.
    public sealed partial class WwpActionService
    {
        // ---- move_control: request -----------------------------------------------------------

        internal sealed class MoveControlRequest
        {
            internal string ControlName;
            internal string Kind;
            internal string TableName;
        }

        internal static JObject ValidateMoveControlArgs(JObject args, out MoveControlRequest request)
        {
            request = new MoveControlRequest
            {
                ControlName = args?["controlName"]?.ToString()?.Trim(),
                Kind = args?["controlKind"]?.ToString()?.Trim(),
                TableName = args?["tableName"]?.ToString()?.Trim()
            };
            if (string.IsNullOrWhiteSpace(request.ControlName))
                return TableError("MissingControlName", "controlName is required for move_control: the textBlock controlName, variable name or attribute name to move.");
            if (string.IsNullOrWhiteSpace(request.TableName))
                return TableError("MissingTableName", "tableName is required for move_control: the destination table (name or path of named ancestors).");
            if (!string.IsNullOrWhiteSpace(request.Kind))
            {
                string requestedKind = request.Kind;
                string kind = MovableControlKinds.FirstOrDefault(k => k.Equals(requestedKind, StringComparison.OrdinalIgnoreCase));
                if (kind == null) return TableError("InvalidControlKind", "controlKind must be textBlock, variable or attribute.");
                request.Kind = kind;
            }
            else request.Kind = null;
            return ValidatePlacementArgs(args);
        }

        private static bool ControlMatches(XElement element, MoveControlRequest request)
        {
            if (element.Parent == null || !Is(element.Parent, "table")) return false;
            string kind = MovableControlKinds.FirstOrDefault(k => Is(element, k));
            if (kind == null || (request.Kind != null && !kind.Equals(request.Kind, StringComparison.Ordinal))) return false;
            switch (kind)
            {
                case "textBlock": return string.Equals(Attr(element, "controlName"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                case "variable": return string.Equals(Attr(element, "name"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                default: return AttributeReferenceMatches(Attr(element, "attribute"), request.ControlName);
            }
        }

        private static string ControlKind(XElement element) => MovableControlKinds.FirstOrDefault(k => Is(element, k));

        private static string ControlIdentity(XElement element)
        {
            switch (ControlKind(element))
            {
                case "textBlock": return Attr(element, "controlName");
                case "variable": return Attr(element, "name");
                default: return Attr(element, "attribute");
            }
        }

        private static JObject DescribeControl(XElement element) => new JObject
        {
            ["type"] = ControlKind(element),
            ["identity"] = ControlIdentity(element),
            ["path"] = ContainerPath(element.Parent)
        };

        // ---- move_control: XML plan ----------------------------------------------------------

        internal static JObject PlanMoveControlXml(XDocument document, JObject args)
        {
            JObject invalid = ValidateMoveControlArgs(args, out MoveControlRequest request);
            if (invalid != null) return invalid;

            List<XElement> candidates = document.Descendants().Where(e => ControlMatches(e, request)).ToList();
            if (candidates.Count == 0)
            {
                JObject notFound = TableError("ControlNotFound",
                    "No textBlock, variable or attribute addressed by '" + request.ControlName + "' sits in a form table of the PatternInstance.");
                notFound["availableControls"] = new JArray(document.Descendants()
                    .Where(e => e.Parent != null && Is(e.Parent, "table") && ControlKind(e) != null)
                    .Take(50).Select(DescribeControl));
                return notFound;
            }
            if (candidates.Count > 1)
            {
                JObject ambiguous = TableError("ControlAmbiguous",
                    "'" + request.ControlName + "' addresses more than one control; pass controlKind (textBlock|variable|attribute).");
                ambiguous["matchingControls"] = new JArray(candidates.Select(DescribeControl));
                return ambiguous;
            }
            XElement element = candidates[0];
            XElement source = element.Parent;

            List<XElement> targets = FindFormContainers(document, request.TableName).ToList();
            if (targets.Count > 1)
            {
                JObject ambiguous = TableError("WwpTableAmbiguous",
                    "tableName '" + request.TableName + "' matched more than one container; qualify it with a path of named ancestors.");
                ambiguous["matchingContainers"] = new JArray(targets.Select(DescribeFormContainer));
                return ambiguous;
            }
            if (targets.Count == 0)
            {
                JObject notFound = TableError("WwpTableNotFound", "No table addressed by '" + request.TableName + "' was found in the PatternInstance.");
                notFound["availablePaths"] = new JArray(GetFormActionContainers(document).Where(e => Is(e, "table"))
                    .Select(ContainerPath).Where(p => p.Length > 0).Distinct());
                return notFound;
            }
            XElement target = targets[0];
            if (!Is(target, "table"))
                return TableError("MoveTargetNotTable", "'" + request.TableName + "' is a " + target.Name.LocalName + ", not a table; move_control only moves controls into tables.");
            if (ReferenceEquals(target, source) && !HasPlacement(args))
                return TableError("ControlAlreadyInTable", "'" + request.ControlName + "' is already in '" + ContainerPath(target) + "'; pass position, before or after to reorder it.");

            string snapshot = document.ToString(SaveOptions.DisableFormatting);
            string kind = ControlKind(element), identity = ControlIdentity(element);
            string fromPath = ContainerPath(source), toPath = ContainerPath(target);
            string sourceBefore = source.ToString(SaveOptions.DisableFormatting);
            string targetBefore = target.ToString(SaveOptions.DisableFormatting);
            string elementXml = element.ToString(SaveOptions.DisableFormatting);

            if (!PatternChildOrderEdit.TryRemove(source, element, out PatternChildOrderEdit.Change removal, out string reason))
            {
                RestoreDocument(document, snapshot);
                return OrderedContainerUnsupported(fromPath, reason);
            }
            element.Remove();
            JObject placementError = ResolveInsertIndex(target, args, out int index);
            if (placementError != null) { RestoreDocument(document, snapshot); return placementError; }
            InsertChildAt(target, element, index);
            if (!PatternChildOrderEdit.TryInsert(target, element, out PatternChildOrderEdit.Change insertion, out reason))
            {
                RestoreDocument(document, snapshot);
                return OrderedContainerUnsupported(toPath, reason);
            }
            if (ReferenceEquals(source, target))
            {
                // One container, one list: the reposition is reported as a single change.
                removal = new PatternChildOrderEdit.Change { Before = removal.Before, After = insertion.After, Entry = insertion.Entry, InsertedBefore = insertion.InsertedBefore };
                insertion = removal;
            }

            var control = new JObject
            {
                ["type"] = kind, ["identity"] = identity, ["from"] = fromPath, ["to"] = toPath, ["position"] = index,
                ["orderingIdentifier"] = PatternChildOrderReconciler.GetIdentifier(element, target),
                ["sourceIndexPath"] = new JArray(IndexPath(source))
            };
            return new JObject
            {
                ["changed"] = true,
                ["control"] = control,
                ["movedControls"] = new JArray(control.DeepClone()),
                ["typedDiff"] = new JObject
                {
                    ["operation"] = MoveControlOperation,
                    ["control"] = control.DeepClone(),
                    ["sourceBefore"] = sourceBefore,
                    ["sourceAfter"] = source.ToString(SaveOptions.DisableFormatting),
                    ["targetBefore"] = targetBefore,
                    ["targetAfter"] = target.ToString(SaveOptions.DisableFormatting),
                    ["element"] = elementXml
                },
                ["childrenOrderedList"] = new JObject
                {
                    ["source"] = OrderingJson(removal),
                    ["target"] = OrderingJson(insertion),
                    ["sameContainer"] = ReferenceEquals(source, target)
                }
            };
        }

        // ---- move_control: native ------------------------------------------------------------

        private static bool NativeControlMatches(object element, object parent, MoveControlRequest request)
        {
            if (parent == null || !IsNativeTable(parent)) return false;
            string kind = MovableControlKinds.FirstOrDefault(k => NativeIsKind(element, k));
            if (kind == null || (request.Kind != null && !kind.Equals(request.Kind, StringComparison.Ordinal))) return false;
            switch (kind)
            {
                case "textBlock": return string.Equals(NativeAttribute(element, "controlName"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                case "variable": return string.Equals(NativeAttribute(element, "name"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                default: return AttributeReferenceMatches(NativeAttribute(element, "attribute"), request.ControlName);
            }
        }

        internal static JObject ApplyNativeMoveControl(object part, JObject args, JObject plan)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return TableError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject invalid = ValidateMoveControlArgs(args, out MoveControlRequest request);
            if (invalid != null) return invalid;

            List<KeyValuePair<object, object>> candidates = WalkWithParent(root, null)
                .Where(pair => NativeControlMatches(pair.Key, pair.Value, request)).ToList();
            if (candidates.Count == 0) return TableError("ControlNotFound", "No native control addressed by '" + request.ControlName + "' sits in a form table.");
            if (candidates.Count > 1) return TableError("ControlAmbiguous", "'" + request.ControlName + "' addresses more than one native control; pass controlKind.");
            object element = candidates[0].Key, source = candidates[0].Value;

            JObject targetError = ResolveNativeContainer(root, request.TableName, out object target);
            if (targetError != null)
            {
                targetError["code"] = targetError["code"]?.ToString() == "FormActionContainerAmbiguous" ? "WwpTableAmbiguous" : "WwpTableNotFound";
                return targetError;
            }
            if (!IsNativeTable(target)) return TableError("MoveTargetNotTable", "'" + request.TableName + "' is not a native table.");
            if (ReferenceEquals(source, target) && !HasPlacement(args))
                return TableError("ControlAlreadyInTable", "'" + request.ControlName + "' is already in the destination table.");

            JObject lists = plan?["childrenOrderedList"] as JObject;
            var orderingEdits = new List<KeyValuePair<object, JObject>>();
            if (ReferenceEquals(source, target))
            {
                JObject error = CollectOrderingEdit(target, lists?["target"] as JObject, orderingEdits);
                if (error != null) return error;
            }
            else
            {
                JObject error = CollectOrderingEdit(source, lists?["source"] as JObject, orderingEdits)
                    ?? CollectOrderingEdit(target, lists?["target"] as JObject, orderingEdits);
                if (error != null) return error;
            }

            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Action) }, null);
            int? insertIndex = null;
            bool sameContainer = ReferenceEquals(source, target);
            Action mutation = () =>
            {
                insertIndex = ResolveNativeInsertIndex(target, args, element);
                if (sameContainer)
                {
                    ExecuteElementCommand(source, element, "RemoveElementCommand", NativeChildren(source).IndexOf(element));
                    if (insertIndex.HasValue) ExecuteElementCommand(target, element, "InsertElementCommand", insertIndex.Value);
                    else ExecuteElementCommand(target, element, "AddElementCommand", null);
                }
                else
                {
                    MoveNativeElement(element, target);
                    int appended = NativeChildren(target).IndexOf(element);
                    if (insertIndex.HasValue && appended != insertIndex.Value)
                        ExecuteMoveCommand(target, element, appended, insertIndex.Value);
                }
                foreach (var edit in orderingEdits) SetNativeAttribute(edit.Key, "childrenOrderedList", edit.Value["after"].ToString());
            };
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp move_control", mutation });
            else mutation();

            return new JObject
            {
                ["changed"] = true,
                ["sdkOperation"] = sameContainer
                    ? "RemoveElementCommand + " + (insertIndex.HasValue ? "InsertElementCommand" : "AddElementCommand")
                    : "PatternInstanceElement.MoveElement" + (insertIndex.HasValue ? " + MoveElementCommand" : string.Empty)
            };
        }

        // ---- move_control: verification ------------------------------------------------------

        internal static JObject VerifyMovedControl(XDocument planned, XDocument persisted, JObject plan)
        {
            JObject control = plan["control"] as JObject ?? new JObject();
            var request = new MoveControlRequest { ControlName = control["identity"]?.ToString(), Kind = control["type"]?.ToString() };
            if (request.Kind == "attribute")
            {
                int dash = request.ControlName?.LastIndexOf('-') ?? -1;
                if (dash >= 0) request.ControlName = request.ControlName.Substring(dash + 1);
            }
            string toPath = control["to"]?.ToString();
            int index = control["position"]?.ToObject<int?>() ?? -1;

            List<XElement> found = persisted.Descendants().Where(e => ControlMatches(e, request)).ToList();
            if (found.Count != 1)
                return NotConfirmed("The re-read PatternInstance contains the " + request.Kind + " '" + control["identity"] + "' " + found.Count + " time(s) instead of once.");
            XElement element = found[0];
            if (!string.Equals(ContainerPath(element.Parent), toPath, StringComparison.OrdinalIgnoreCase))
                return NotConfirmed("The re-read control is under '" + ContainerPath(element.Parent) + "' instead of '" + toPath + "'.");
            if (element.Parent.Elements().ToList().IndexOf(element) != index)
                return NotConfirmed("The re-read control is at child index " + element.Parent.Elements().ToList().IndexOf(element) + " instead of " + index + ".");
            XElement plannedElement = planned.Descendants().FirstOrDefault(e => ControlMatches(e, request));
            if (plannedElement == null) return NotConfirmed("The planned PatternInstance has no control '" + control["identity"] + "'.");
            if (!IsRequestedSubset(ProjectXmlControl(plannedElement), ProjectXmlControl(element)))
                return NotConfirmed("The re-read control lost attributes or children it had before the move.");

            XElement plannedRest = StripForComparison(planned.Root, plannedElement);
            XElement persistedRest = StripForComparison(persisted.Root, element);
            if (!XNode.DeepEquals(plannedRest, persistedRest))
                return NotConfirmed("The persisted PatternInstance differs beyond the moved control; other elements changed.");

            string orderingIdentifier = control["orderingIdentifier"]?.ToString();
            XElement plannedSource = ElementAtIndexPath(planned.Root, control["sourceIndexPath"] as JArray);
            var forbidden = new List<KeyValuePair<XElement, string>>();
            if (plannedSource != null && !ReferenceEquals(plannedSource, plannedElement.Parent) && !string.IsNullOrEmpty(orderingIdentifier))
                forbidden.Add(new KeyValuePair<XElement, string>(plannedSource, orderingIdentifier));
            return VerifyOrderedLists(planned.Root, persisted.Root, plannedElement, element,
                requiredIdentifier: orderingIdentifier, forbidden: forbidden);
        }

        internal static JObject VerifyMovedControlProjection(string webForm, JObject plan)
        {
            if (string.IsNullOrWhiteSpace(webForm)) return NotConfirmed("The projected WebForm could not be re-read.");
            XDocument document;
            try { document = XDocument.Parse(webForm, LoadOptions.PreserveWhitespace); }
            catch (XmlException) { return NotConfirmed("The projected WebForm is not valid XML."); }
            JObject control = plan["control"] as JObject ?? new JObject();
            string identity = control["identity"]?.ToString();
            if (control["type"]?.ToString() == "attribute" && identity != null && identity.LastIndexOf('-') >= 0)
                identity = identity.Substring(identity.LastIndexOf('-') + 1);
            bool present = FindWebFormNamedElement(document, identity) != null;
            // A text block keeps its controlName in the WebForm; a variable or attribute may
            // render under a customised control name, so its presence is reported, not required.
            bool required = control["type"]?.ToString() == "textBlock";
            return new JObject
            {
                ["confirmed"] = present || !required,
                ["targetPresent"] = present,
                ["message"] = present || !required ? null : "The projected WebForm does not contain the moved text block '" + identity + "'."
            };
        }
    }
}
