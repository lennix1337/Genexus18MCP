using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
        private static readonly HashSet<string> TabOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add_tab", "move_tab", "remove_tab"
        };

        private static bool IsTabOperation(string operation) => TabOperations.Contains(operation ?? string.Empty);

        private string RunTabOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, string operation, JObject args)
        {
            if (string.IsNullOrWhiteSpace(args?["controlName"]?.ToString()))
                return McpResponse.Err(code: "MissingControlName", message: "controlName is required.", target: target);
            if (operation == "add_tab" && string.IsNullOrWhiteSpace(args?["title"]?.ToString()))
                return McpResponse.Err(code: "MissingTabTitle", message: "title is required for add_tab.", target: target);
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;
            if (operation == "remove_tab" && !dryRun && args?["confirm"]?.ToObject<bool?>() != true)
                return McpResponse.Err(code: "ConfirmationRequired", message: "remove_tab requires confirm=true.", target: target);

            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument afterDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject mutation = ApplyTabXml(afterDocument, operation, args);
            if (mutation["error"] != null)
                return McpResponse.Err(code: mutation["code"]?.ToString() ?? "WwpTabInvalid",
                    message: mutation["error"].ToString(), target: target, extra: mutation);

            JObject before = ProjectTabs(beforeDocument);
            JObject after = ProjectTabs(afterDocument);
            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            var typedDiff = new JObject
            {
                ["operation"] = operation,
                ["controlName"] = args["controlName"].ToString(),
                ["before"] = before,
                ["after"] = after
            };
            if (dryRun)
                return McpResponse.Ok(target: target, code: "WwpTabDryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["typedDiff"] = typedDiff,
                    ["movedAttributes"] = mutation["movedAttributes"]?.DeepClone() ?? new JArray(),
                    ["versionToken"] = versionToken,
                    ["persisted"] = false,
                    ["patternReReadConfirmed"] = false,
                    ["webFormProjectionConfirmed"] = false,
                    ["lifecycleExecuted"] = false
                });

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                if (currentInstance == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);
                // The part is resolved from the instance that was read (issue #638): resolving it from
                // the requested host yields another copy of the WorkWithPlus object, and saving one copy
                // with the other's part persists nothing.
                _patterns.BuildPatternPartEnvelope(currentInstance, "PatternInstance", currentXml, PatternRegistry.WorkWithPlusPatternId,
                    out currentInstance, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);

                string expectedVersion = args?["baseVersion"]?.ToString()
                    ?? args?["expectedVersion"]?.ToString()
                    ?? args?["versionToken"]?.ToString();
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion,
                        "The PatternInstance changed after the caller's read/dry-run; no tab mutation was applied.");

                // Recompute the requested state under the per-object lock. This prevents a
                // dry-run diff from being applied to a newer in-memory PatternInstance.
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedMutation = ApplyTabXml(lockedAfter, operation, args);
                if (lockedMutation["error"] != null)
                    return McpResponse.Err(code: lockedMutation["code"]?.ToString() ?? "WwpTabInvalid",
                        message: lockedMutation["error"].ToString(), target: target, extra: lockedMutation);
                JObject expectedTabs = ProjectTabs(lockedAfter);

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");

                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "The exact PatternInstance/WebForm snapshots could not be captured; no mutation was applied.",
                        target: target, extra: new JObject { ["snapshot"] = snapshots.ToJson(), ["persisted"] = false });

                try
                {
                    JObject nativeMutation = ApplyNativeTabMutation(currentPart, operation, args);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected",
                            nativeMutation["error"].ToString());

                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);

                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out KBObject persistedInstance, out _);
                    JObject persistedTabs = string.IsNullOrWhiteSpace(persistedXml)
                        ? new JObject()
                        : ProjectTabs(XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace));
                    JObject patternVerification = VerifyPatternTabs(expectedTabs, persistedTabs,
                        operation, args["controlName"].ToString());
                    if (patternVerification["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpTabNotPersisted",
                            patternVerification["message"]?.ToString()
                                ?? "The SDK save completed, but the typed tab state did not survive the PatternInstance re-read.");
                    JArray movedAttributes = lockedMutation["movedAttributes"] as JArray ?? new JArray();
                    JObject moveVerification = string.IsNullOrWhiteSpace(persistedXml) ? null
                        : VerifyMovedAttributes(XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace),
                            args["controlName"].ToString(), movedAttributes);
                    if (movedAttributes.Count > 0 && moveVerification?["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpTabNotPersisted",
                            moveVerification?["message"]?.ToString()
                                ?? "The SDK save completed, but a moved attribute did not leave its source table.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance,
                        "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled",
                            "SDPlus_Editor_Apply_On_Save became False after the native tab save.");

                    bool projected = parent != null
                        && WwpProjectionHelper.TryProjectHostOntoParent(parent, persistedInstance ?? currentInstance);
                    if (!projected)
                        throw new WwpTabException("WwpProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");

                    string projectedWebForm = ReadPart(parent, "WebForm");
                    JObject projection = VerifyWebFormProjection(projectedWebForm, expectedTabs,
                        operation, args["controlName"].ToString());
                    if (projection["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpProjectionNotConfirmed",
                            projection["message"]?.ToString() ?? "The projected WebForm did not contain the requested typed tab state.");

                    WriteService.NotePerTargetWrite(target);
                    return McpResponse.Ok(target: target, code: "WwpTabUpdated", result: new JObject
                    {
                        ["instance"] = persistedInstance?.Name ?? currentInstance.Name,
                        ["parent"] = parent.Name,
                        ["operation"] = operation,
                        ["typedDiff"] = new JObject
                        {
                            ["operation"] = operation,
                            ["controlName"] = args["controlName"].ToString(),
                            ["before"] = ProjectTabs(XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace)),
                            ["after"] = persistedTabs
                        },
                        ["movedAttributes"] = movedAttributes,
                        ["versionToken"] = WriteService.ComputeContentVersionToken(persistedInstance ?? currentInstance, persistedXml),
                        ["persisted"] = true,
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["actionEventConfirmed"] = projection["actionEventConfirmed"]?.DeepClone() ?? true,
                        ["applyOnSaveBefore"] = applyOnSaveBefore,
                        ["applyOnSaveAfter"] = applyOnSaveAfter,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
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
                    JObject rollback = RestoreSnapshots(currentInstance, currentPart, nativeBytes,
                        currentXml, parent, parentWebFormBefore, applyOnSaveBefore);
                    ExceptionRoot.Log("[WWP-TAB] failed", ex);
                    return McpResponse.Err(code: typed?.Code ?? "WwpTabFailed", message: ExceptionRoot.Message(ex),
                        target: target, extra: new JObject
                        {
                            ["exceptionType"] = ExceptionRoot.Unwrap(ex)?.GetType().Name,
                            ["failureTrace"] = ExceptionRoot.FailureTrace(ex),
                            ["persisted"] = false,
                            ["patternReReadConfirmed"] = false,
                            ["webFormProjectionConfirmed"] = false,
                            ["snapshot"] = snapshots.ToJson(),
                            ["rollback"] = rollback,
                            ["lifecycleExecuted"] = false
                        });
                }
            }
        }

        internal static JObject ApplyTabXml(XDocument document, string operation, JObject args)
        {
            string controlName = args?["controlName"]?.ToString();
            XElement tabs = document.Descendants().FirstOrDefault(e => Is(e, "tabs"));
            if (tabs == null && operation == "add_tab")
            {
                // #552: a Transaction form has no <tabs> until one is added. It is synthesized
                // only under the form content table (or an explicit containerName); picking an
                // arbitrary <table> could land the tabs inside a popup, a grid or another level.
                JObject containerError = ResolveTabsSynthesisContainer(document, args, out XElement container);
                if (containerError != null) return containerError;
                tabs = new XElement(container.Name.Namespace + "tabs");
                container.Add(tabs);
            }
            if (tabs == null) return TabError("TabsContainerNotFound", "The PatternInstance has no <tabs> container.");
            XElement tab = tabs.Elements().FirstOrDefault(e => IsTabElement(e.Name.LocalName)
                && string.Equals(Attr(e, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase));
            var movedAttributes = new JArray();

            switch (operation)
            {
                case "add_tab":
                    if (tab != null) return TabError("TabAlreadyExists", "Tab '" + controlName + "' already exists.");
                    tab = new XElement(tabs.GetDefaultNamespace() + "tab",
                        new XAttribute("ControlName", controlName),
                        new XAttribute("title", args["title"].ToString()));
                    try
                    {
                        AddXmlChildren(tab, args?["children"] as JArray);
                        JObject moveError = MoveFormAttributesIntoTab(tab, FormScope(tabs), movedAttributes);
                        if (moveError != null) return moveError;
                    }
                    catch (WwpTabException ex) { return TabError(ex.Code, ex.Message); }
                    InsertXmlTab(tabs, tab, args?["position"]?.ToObject<int?>());
                    break;
                case "move_tab":
                    if (tab == null) return TabError("TabNotFound", "Tab '" + controlName + "' was not found.");
                    tab.Remove();
                    InsertXmlTab(tabs, tab, args?["position"]?.ToObject<int?>());
                    break;
                case "remove_tab":
                    if (tab == null) return TabError("TabNotFound", "Tab '" + controlName + "' was not found.");
                    tab.Remove();
                    break;
                default:
                    return TabError("UnknownWwpActionOperation", "Unknown WorkWithPlus tab action '" + operation + "'.");
            }
            return new JObject { ["changed"] = true, ["movedAttributes"] = movedAttributes };
        }

        private static readonly string[] TabsSynthesisContainers = { "TableContent", "TableMain" };

        // The table a synthesized <tabs> goes under: the explicit containerName when given,
        // otherwise the unique TableContent, otherwise the unique TableMain. Anything else is
        // refused with the paths the caller can name, never guessed.
        private static JObject ResolveTabsSynthesisContainer(XDocument document, JObject args, out XElement container)
            => ResolveDefaultFormTable(document, args, "TabsContainerNotFound", "TabsContainerAmbiguous",
                "The PatternInstance has no <tabs> container and", "the new <tabs>", "add_tab", out container);

        // Shared by add_tab and add_table (#595): the same default-container rule, with each
        // operation's own error codes and wording.
        private static JObject ResolveDefaultFormTable(XDocument document, JObject args, string notFoundCode, string ambiguousCode,
            string context, string subject, string operation, out XElement container)
        {
            container = null;
            string requested = args?["containerName"]?.ToString()?.Trim();
            foreach (string name in string.IsNullOrWhiteSpace(requested) ? TabsSynthesisContainers : new[] { requested })
            {
                // The defaults are matched by their own name; an explicit containerName may be a path.
                List<XElement> candidates = (string.IsNullOrWhiteSpace(requested)
                        ? document.Descendants().Where(e => string.Equals(Attr(e, "name"), name, StringComparison.OrdinalIgnoreCase))
                        : FindFormContainers(document, name))
                    .Where(e => Is(e, "table")).ToList();
                if (candidates.Count == 1) { container = candidates[0]; return null; }
                if (candidates.Count > 1)
                {
                    JObject ambiguous = TabError(ambiguousCode,
                        context + " '" + name + "' matched more than one table; pass containerName with a path of named ancestors.");
                    ambiguous["matchingContainers"] = new JArray(candidates.Select(DescribeFormContainer));
                    return ambiguous;
                }
            }
            JObject notFound = TabError(notFoundCode,
                string.IsNullOrWhiteSpace(requested)
                    ? context + " no TableContent or TableMain table to create " + subject + " under; " + operation + " does not pick an arbitrary table."
                    : context + " containerName '" + requested + "' matched no table.");
            notFound["hint"] = "Pass containerName naming the form table (a path of named ancestors is accepted) that should hold " + subject + ".";
            notFound["availablePaths"] = new JArray(GetFormActionContainers(document).Where(e => Is(e, "table"))
                .Select(ContainerPath).Where(p => p.Length > 0).Distinct());
            return notFound;
        }

        // The form section that owns a <tabs>: the top-level child of the instance that
        // contains it (e.g. <transaction>), or the root when <tabs> sits directly under it.
        private static XElement FormScope(XElement tabs)
        {
            XElement scope = tabs;
            while (scope.Parent != null && scope.Parent.Parent != null) scope = scope.Parent;
            return scope == tabs ? (tabs.Parent ?? tabs) : scope;
        }

        // WorkWithPlus references an attribute as "<typeGuid>-<Name>"; callers may pass either form.
        internal static bool AttributeReferenceMatches(string reference, string requested)
        {
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(requested)) return false;
            if (string.Equals(reference, requested, StringComparison.OrdinalIgnoreCase)) return true;
            return reference.EndsWith("-" + requested, StringComparison.OrdinalIgnoreCase)
                || requested.EndsWith("-" + reference, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<XElement> FormTableAttributes(XElement scope) =>
            scope.Descendants().Where(e => Is(e, "attribute") && e.Parent != null && Is(e.Parent, "table"));

        // #552: an attribute child names an attribute that is usually already on the form
        // (in TableAttributes). It is moved, element and XML attributes intact, into the
        // tab's table; adding a second <attribute> would show the field twice. The new tab is
        // still detached here, so the scope search cannot see the children being built.
        // add_table (#595) also asks for the source tables (so the verification can find them
        // again by position) and for each source's childrenOrderedList to drop the entry.
        private static JObject MoveFormAttributesIntoTab(XElement tab, XElement scope, JArray moved,
            List<XElement> sourceParents = null, bool maintainOrderedLists = false)
        {
            var requested = new List<string>();
            var moves = new List<KeyValuePair<XElement, XElement>>();
            foreach (XElement placeholder in tab.Descendants().Where(e => Is(e, "attribute")))
            {
                string name = Attr(placeholder, "attribute");
                if (requested.Any(previous => AttributeReferenceMatches(previous, name)))
                    return TabError("InvalidWwpChild", "Attribute '" + name + "' was requested more than once in the same tab.");
                requested.Add(name);
                List<XElement> sources = FormTableAttributes(scope).Where(e => AttributeReferenceMatches(Attr(e, "attribute"), name)).ToList();
                if (sources.Count > 1)
                {
                    JObject ambiguous = TabError("AttributeAmbiguousOnForm",
                        "Attribute '" + name + "' appears in more than one form table; no tab mutation was applied.");
                    ambiguous["matchingContainers"] = new JArray(sources.Select(e => DescribeFormContainer(e.Parent)));
                    return ambiguous;
                }
                if (sources.Count == 1) moves.Add(new KeyValuePair<XElement, XElement>(placeholder, sources[0]));
            }
            // Every child was validated before the first element moves, so a refusal leaves the form untouched.
            foreach (var move in moves)
            {
                XElement placeholder = move.Key, source = move.Value;
                XElement sourceParent = source.Parent;
                string from = ContainerPath(sourceParent);
                PatternChildOrderEdit.Change ordering = null;
                if (maintainOrderedLists && !PatternChildOrderEdit.TryRemove(sourceParent, source, out ordering, out string reason))
                    return OrderedContainerUnsupported(from, reason);
                source.Remove();
                foreach (XAttribute overridden in placeholder.Attributes().Where(a => !a.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase)))
                    source.SetAttributeValue(overridden.Name, overridden.Value);
                placeholder.ReplaceWith(source);
                var entry = new JObject { ["attribute"] = Attr(source, "attribute"), ["from"] = from };
                if (maintainOrderedLists)
                {
                    entry["childrenOrderedList"] = OrderingJson(ordering);
                    entry["orderingIdentifier"] = PatternChildOrderReconciler.GetIdentifier(source, sourceParent);
                }
                sourceParents?.Add(sourceParent);
                moved.Add(entry);
            }
            return null;
        }

        // After the native save: each moved attribute must be on the form exactly once, inside
        // the requested tab. A copy left in the source table is a duplicate, not a move.
        internal static JObject VerifyMovedAttributes(XDocument persisted, string controlName, JArray moved)
        {
            if (moved == null || moved.Count == 0) return new JObject { ["confirmed"] = true };
            XElement tabs = persisted.Descendants().FirstOrDefault(e => Is(e, "tabs"));
            if (tabs == null) return new JObject { ["confirmed"] = false, ["message"] = "The PatternInstance re-read has no <tabs> container." };
            XElement tab = tabs.Elements().FirstOrDefault(e => IsTabElement(e.Name.LocalName)
                && string.Equals(Attr(e, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase));
            foreach (JObject entry in moved.OfType<JObject>())
            {
                string name = entry["attribute"]?.ToString();
                List<XElement> occurrences = FormTableAttributes(FormScope(tabs))
                    .Where(e => AttributeReferenceMatches(Attr(e, "attribute"), name)).ToList();
                if (occurrences.Count != 1 || tab == null || !occurrences[0].Ancestors().Contains(tab))
                    return new JObject
                    {
                        ["confirmed"] = false,
                        ["message"] = "Attribute '" + name + "' is on the form " + occurrences.Count
                            + " time(s) after the save instead of once inside tab '" + controlName + "'; the move was rolled back."
                    };
            }
            return new JObject { ["confirmed"] = true };
        }

        private static void AddXmlChildren(XElement tab, JArray children)
        {
            if (children == null || children.Count == 0) return;
            var direct = children.OfType<JObject>().ToList();
            if (direct.All(c => string.Equals(c["type"]?.ToString(), "table", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (JObject child in direct) tab.Add(BuildXmlControl(child));
                return;
            }

            // WWP tabs accept tables directly. A flat typed list is a convenience
            // contract and is wrapped in the canonical responsive table.
            var table = new XElement(tab.GetDefaultNamespace() + "table", new XAttribute("type", "Responsive"));
            foreach (JObject child in direct) table.Add(BuildXmlControl(child));
            tab.Add(table);
        }

        private static XElement BuildXmlControl(JObject control)
        {
            string type = control?["type"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(type)) throw new WwpTabException("InvalidWwpChild", "Every child requires type.");
            XNamespace ns = XNamespace.None;
            XElement element;
            if (type.Equals("variable", StringComparison.OrdinalIgnoreCase))
            {
                string name = Required(control, "name", "variable");
                string basicType = control["basicType"]?.ToString() ?? "VarChar";
                element = new XElement(ns + "variable",
                    new XAttribute("name", name), new XAttribute("dataType", "Basic"),
                    new XAttribute("defaultDataType", "Basic"), new XAttribute("basicType", basicType),
                    new XAttribute("defaultBasicType", basicType));
                SetPair(element, "description", control["description"]);
                SetPair(element, "basicCLength", control["length"]);
                SetPair(element, "basicDecimals", control["decimals"]);
                SetPair(element, "controlType", control["controlType"]);
                SetPair(element, "controlValues", control["controlValues"]);
                SetPair(element, "controlEmptyItem", control["controlEmptyItem"]);
                SetPair(element, "controlEmptyItemText", control["controlEmptyItemText"]);
            }
            else if (type.Equals("userAction", StringComparison.OrdinalIgnoreCase))
            {
                string name = Required(control, "name", "userAction");
                element = new XElement(ns + "userAction", new XAttribute("name", name),
                    new XAttribute("caption", control["caption"]?.ToString() ?? name));
            }
            else if (type.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                element = new XElement(ns + "table", new XAttribute("type", "Responsive"));
                SetSingle(element, "name", control["name"]);
                SetPair(element, "numberOfColumns", control["columns"]);
                SetPair(element, "themeClass", control["themeClass"]);
                foreach (JObject child in (control["children"] as JArray ?? new JArray()).OfType<JObject>())
                    element.Add(BuildXmlControl(child));
            }
            else if (type.Equals("attribute", StringComparison.OrdinalIgnoreCase))
            {
                string attName = control["attribute"]?.ToString() ?? control["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(attName)) throw new WwpTabException("InvalidWwpChild", "Attribute child requires attribute or name.");
                element = new XElement(ns + "attribute", new XAttribute("attribute", attName));
                SetPair(element, "description", control["description"]);
            }
            else if (type.Equals("textBlock", StringComparison.OrdinalIgnoreCase))
            {
                // #595: a caption on the form, addressed by controlName as WorkWithPlus writes it.
                string controlName = TextBlockControlName(control);
                element = new XElement(ns + "textBlock", new XAttribute("controlName", controlName),
                    new XAttribute("caption", control["caption"]?.ToString() ?? controlName));
                SetPair(element, "themeClass", control["themeClass"]);
            }
            else throw new WwpTabException("InvalidWwpChildType", "Unsupported child type '" + type + "'. Expected variable|userAction|table|attribute|textBlock.");
            return element;
        }

        private static string TextBlockControlName(JObject control)
        {
            string controlName = control["controlName"]?.ToString() ?? control["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(controlName)) throw new WwpTabException("InvalidWwpChild", "textBlock requires controlName.");
            return controlName.Trim();
        }

        private static JObject ProjectTabs(XDocument document)
        {
            var tabsResult = new JArray();
            XElement tabs = document.Descendants().FirstOrDefault(e => Is(e, "tabs"));
            if (tabs != null)
            {
                int position = 0;
                // A Transaction's existing tabs may be gridTab/tabularTab/webComponentTab (#426);
                // the projection counts every tab kind so order and position survive the re-read.
                foreach (XElement tab in tabs.Elements().Where(e => IsTabElement(e.Name.LocalName)))
                {
                    tabsResult.Add(new JObject
                    {
                        ["controlName"] = Attr(tab, "ControlName"),
                        ["title"] = Attr(tab, "title"),
                        ["position"] = position++,
                        ["children"] = new JArray(tab.Elements().Select(ProjectXmlControl))
                    });
                }
            }
            return new JObject { ["tabs"] = tabsResult };
        }

        private static JObject ProjectXmlControl(XElement element)
        {
            var result = new JObject { ["type"] = element.Name.LocalName };
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.StartsWith("default", StringComparison.OrdinalIgnoreCase)
                    || attribute.Name.LocalName.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase)) continue;
                result[attribute.Name.LocalName] = attribute.Value;
            }
            if (element.HasElements) result["children"] = new JArray(element.Elements().Select(ProjectXmlControl));
            return result;
        }

        internal static JObject ApplyNativeTabMutation(object part, string operation, JObject args)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return TabError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            object tabs = Walk(root).FirstOrDefault(e => NativeType(e).Equals("tabs", StringComparison.OrdinalIgnoreCase));
            object synthesisContainer = null;
            if (tabs == null && operation == "add_tab")
            {
                JObject containerError = ResolveNativeTabsSynthesisContainer(root, args, out synthesisContainer);
                if (containerError != null) return containerError;
            }
            if (tabs == null && synthesisContainer == null) return TabError("TabsContainerNotFound", "The native PatternInstance has no tabs container.");
            string controlName = args["controlName"].ToString();
            List<object> existingTabs = tabs == null ? new List<object>() : NativeChildren(tabs).Where(IsNativeTab).ToList();
            object tab = existingTabs.FirstOrDefault(e => string.Equals(NativeAttribute(e, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase));
            object scope = NativeFormScope(root, tabs ?? synthesisContainer);

            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(Action) }, null);
            Action mutation = () =>
            {
                if (tabs == null)
                {
                    // Synthesized inside the same update as the tab, so a rejected child
                    // leaves no empty <tabs> behind for the snapshot restore to clean up.
                    tabs = CreateNativeChild(synthesisContainer, "tabs");
                    ExecuteElementCommand(synthesisContainer, tabs, "AddElementCommand", null);
                }
                if (operation == "add_tab")
                {
                    if (tab != null) throw new WwpTabException("TabAlreadyExists", "Tab '" + controlName + "' already exists.");
                    object created = CreateNativeChild(tabs, "tab");
                    SetNativeAttribute(created, "ControlName", controlName);
                    SetNativeAttribute(created, "title", args["title"].ToString());
                    AddNativeChildren(created, args["children"] as JArray, scope, new List<string>());
                    int index = ClampPosition(args?["position"]?.ToObject<int?>(), existingTabs.Count);
                    ExecuteElementCommand(tabs, created, "InsertElementCommand", index);
                }
                else if (operation == "move_tab")
                {
                    if (tab == null) throw new WwpTabException("TabNotFound", "Tab '" + controlName + "' was not found.");
                    int oldIndex = NativeChildren(tabs).IndexOf(tab);
                    int newIndex = ClampPosition(args?["position"]?.ToObject<int?>(), Math.Max(0, existingTabs.Count - 1));
                    ExecuteMoveCommand(tabs, tab, oldIndex, newIndex);
                }
                else if (operation == "remove_tab")
                {
                    if (tab == null) throw new WwpTabException("TabNotFound", "Tab '" + controlName + "' was not found.");
                    ExecuteElementCommand(tabs, tab, "RemoveElementCommand", NativeChildren(tabs).IndexOf(tab));
                }
            };
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp " + operation, mutation });
            else mutation();
            return new JObject { ["changed"] = true };
        }

        private static JObject ResolveNativeTabsSynthesisContainer(object root, JObject args, out object container)
            => ResolveDefaultNativeFormTable(root, args, "TabsContainerNotFound", "TabsContainerAmbiguous",
                "The native PatternInstance has no tabs container and", "the new tabs", "add_tab", out container);

        // Native twin of ResolveDefaultFormTable.
        private static JObject ResolveDefaultNativeFormTable(object root, JObject args, string notFoundCode, string ambiguousCode,
            string context, string subject, string operation, out object container)
        {
            container = null;
            string requested = args?["containerName"]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(requested))
            {
                JObject error = ResolveNativeContainer(root, requested, out container);
                if (error == null && IsNativeTable(container)) return null;
                container = null;
                if (error == null) return TabError(notFoundCode, "containerName '" + requested + "' is not a native table.");
                error["code"] = error["code"]?.ToString() == "FormActionContainerAmbiguous" ? ambiguousCode : notFoundCode;
                return error;
            }
            foreach (string name in TabsSynthesisContainers)
            {
                List<object> candidates = Walk(root).Where(e =>
                    string.Equals(NativeAttribute(e, "name"), name, StringComparison.OrdinalIgnoreCase) && IsNativeTable(e)).ToList();
                if (candidates.Count == 1) { container = candidates[0]; return null; }
                if (candidates.Count > 1)
                    return TabError(ambiguousCode,
                        context + " '" + name + "' matched more than one table; pass containerName with a path of named ancestors.");
            }
            JObject notFound = TabError(notFoundCode,
                context + " no TableContent or TableMain table to create " + subject + " under; " + operation + " does not pick an arbitrary table.");
            notFound["hint"] = "Pass containerName naming the form table (a path of named ancestors is accepted) that should hold " + subject + ".";
            return notFound;
        }

        private static bool IsNativeTab(object element) =>
            IsTabElement(NativeType(element)) || IsTabElement(NativeElementXml(element)?.Name.LocalName);

        private static bool IsNativeAttribute(object element) =>
            NativeType(element).Equals("attribute", StringComparison.OrdinalIgnoreCase)
            || NativeElementXml(element)?.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase) == true;

        // Native twin of FormScope: the root child whose subtree holds the element.
        private static object NativeFormScope(object root, object element)
        {
            foreach (object section in NativeChildren(root))
                if (Walk(section).Any(e => ReferenceEquals(e, element))) return section;
            return root;
        }

        private static IEnumerable<KeyValuePair<object, object>> WalkWithParent(object element, object parent)
        {
            if (element == null) yield break;
            yield return new KeyValuePair<object, object>(element, parent);
            foreach (object child in NativeChildren(element))
                foreach (var descendant in WalkWithParent(child, element)) yield return descendant;
        }

        private static void AddNativeChildren(object tab, JArray children, object scope, List<string> requestedAttributes)
        {
            if (children == null || children.Count == 0) return;
            List<JObject> typed = children.OfType<JObject>().ToList();
            if (typed.All(c => string.Equals(c["type"]?.ToString(), "table", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (JObject child in typed) AttachNativeControl(tab, child, scope, requestedAttributes);
                return;
            }
            object table = CreateNativeChild(tab, "table");
            SetNativeAttribute(table, "type", "Responsive");
            SetNativeAttribute(table, "defaultType", "Responsive");
            foreach (JObject child in typed) AttachNativeControl(table, child, scope, requestedAttributes);
            ExecuteElementCommand(tab, table, "AddElementCommand", null);
        }

        // Native twin of MoveFormAttributesIntoTab: the existing element is moved from its
        // table to the tab (MoveNativeElement), so its typed reference and every other
        // attribute travel with it. Returns false when the
        // attribute is not on the form yet and a new element has to be created.
        private static bool MoveNativeFormAttribute(object parent, JObject control, string attName, object scope, List<string> requestedAttributes)
        {
            if (requestedAttributes.Any(previous => AttributeReferenceMatches(previous, attName)))
                throw new WwpTabException("InvalidWwpChild", "Attribute '" + attName + "' was requested more than once in the same tab.");
            requestedAttributes.Add(attName);
            List<KeyValuePair<object, object>> sources = WalkWithParent(scope, null)
                .Where(pair => AttributeReferenceMatches(NativeAttribute(pair.Key, "attribute"), attName)
                    && IsNativeAttribute(pair.Key) && pair.Value != null && IsNativeTable(pair.Value))
                .ToList();
            if (sources.Count > 1)
                throw new WwpTabException("AttributeAmbiguousOnForm", "Attribute '" + attName + "' appears in more than one native form table.");
            if (sources.Count == 0) return false;
            object existing = sources[0].Key;
            object sourceTable = sources[0].Value;
            MoveNativeElement(existing, parent);
            SetNativePair(existing, "description", control["description"]);
            return true;
        }

        private static void AttachNativeControl(object parent, JObject control, object scope, List<string> requestedAttributes)
        {
            string type = control?["type"]?.ToString();
            if (type != "variable" && type != "userAction" && type != "table" && type != "attribute" && type != "textBlock")
                throw new WwpTabException("InvalidWwpChildType", "Unsupported child type '" + type + "'. Expected variable|userAction|table|attribute|textBlock.");
            if (type == "attribute")
            {
                string attName = control["attribute"]?.ToString() ?? control["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(attName)) throw new WwpTabException("InvalidWwpChild", "Attribute child requires attribute or name.");
                if (MoveNativeFormAttribute(parent, control, attName, scope, requestedAttributes)) return;
            }
            object child = CreateNativeChild(parent, type);
            if (type == "variable")
            {
                string name = Required(control, "name", type);
                string basicType = control["basicType"]?.ToString() ?? "VarChar";
                SetNativeAttribute(child, "name", name);
                SetNativePair(child, "dataType", "Basic");
                SetNativePair(child, "basicType", basicType);
                SetNativePair(child, "description", control["description"]);
                SetNativePair(child, "basicCLength", control["length"]);
                SetNativePair(child, "basicDecimals", control["decimals"]);
                SetNativePair(child, "controlType", control["controlType"]);
                SetNativePair(child, "controlValues", control["controlValues"]);
                SetNativePair(child, "controlEmptyItem", control["controlEmptyItem"]);
                SetNativePair(child, "controlEmptyItemText", control["controlEmptyItemText"]);
            }
            else if (type == "userAction")
            {
                string name = Required(control, "name", type);
                SetNativeAttribute(child, "name", name);
                SetNativeAttribute(child, "caption", control["caption"]?.ToString() ?? name);
            }
            else if (type == "attribute")
            {
                string attName = control["attribute"]?.ToString() ?? control["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(attName)) throw new WwpTabException("InvalidWwpChild", "Attribute child requires attribute or name.");
                SetNativeAttribute(child, "attribute", attName);
                SetNativePair(child, "description", control["description"]);
            }
            else if (type == "textBlock")
            {
                string controlName = TextBlockControlName(control);
                SetNativeAttribute(child, "controlName", controlName);
                SetNativeAttribute(child, "caption", control["caption"]?.ToString() ?? controlName);
                SetNativePair(child, "themeClass", control["themeClass"]);
            }
            else
            {
                SetNativePair(child, "type", "Responsive");
                SetNativeAttributeIf(child, "name", control["name"]);
                SetNativePair(child, "numberOfColumns", control["columns"]);
                SetNativePair(child, "themeClass", control["themeClass"]);
                foreach (JObject nested in (control["children"] as JArray ?? new JArray()).OfType<JObject>())
                    AttachNativeControl(child, nested, scope, requestedAttributes);
            }
            ExecuteElementCommand(parent, child, "AddElementCommand", null);
        }

        private static object CreateNativeChild(object parent, string type)
        {
            object children = GetProperty(parent, "Children");
            MethodInfo create = children?.GetType().GetMethod("CreateChildElement", new[] { typeof(string) });
            object child = create?.Invoke(children, new object[] { type });
            if (child == null) throw new WwpTabException("WwpChildRejected", "WorkWithPlus rejected child type '" + type + "' for this container.");
            return child;
        }

        private static void ExecuteElementCommand(object parent, object child, string commandName, int? index)
        {
            Type commandType = parent.GetType().Assembly.GetType("Artech.Packages.Patterns.Objects." + commandName, true);
            object command = index.HasValue
                ? Activator.CreateInstance(commandType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new object[] { parent, child, index.Value }, null)
                : Activator.CreateInstance(commandType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new object[] { parent, child }, null);
            ExecuteCommand(command, commandName);
        }

        private static void ExecuteMoveCommand(object parent, object child, int oldIndex, int newIndex)
        {
            Type commandType = parent.GetType().Assembly.GetType("Artech.Packages.Patterns.Objects.MoveElementCommand", true);
            object command = Activator.CreateInstance(commandType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new object[] { parent, child, oldIndex, newIndex }, null);
            ExecuteCommand(command, "MoveElementCommand");
        }

        // An existing element changes container through PatternInstanceElement.MoveElement: it detaches
        // the element, re-parents it (ChangeParentCommand) and adds it to the new parent. Removing it
        // with RemoveElementCommand and adding it with AddElementCommand leaves Parent on the old
        // container, and the SDK refuses the add ("textBlock cannot be added as child of 'table'").
        private static void MoveNativeElement(object element, object newParent)
        {
            MethodInfo move = element.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "MoveElement" && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(newParent));
            if (move == null)
                throw new WwpTabException("WwpNativeMutationRejected", "PatternInstanceElement.MoveElement is unavailable in this WorkWithPlus package.");
            move.Invoke(element, new[] { newParent });
        }

        private static void ExecuteCommand(object command, string name)
        {
            MethodInfo safe = command.GetType().GetMethod("IsSafeToExecute", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (safe != null && safe.ReturnType == typeof(bool) && !(bool)safe.Invoke(command, null))
                throw new WwpTabException("WwpNativeMutationRejected", name + " was not safe to execute.");
            command.GetType().GetMethod("Execute", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(command, null);
        }

        private static void SetNativePair(object element, string name, JToken value)
        {
            if (value == null) return;
            SetNativePair(element, name, value.ToString());
        }

        private static void SetNativePair(object element, string name, string value)
        {
            if (value == null) return;
            SetNativeAttribute(element, name, value);
            string defaultName = "default" + char.ToUpperInvariant(name[0]) + name.Substring(1);
            SetNativeAttribute(element, defaultName, value);
        }

        private static void SetNativeAttributeIf(object element, string name, JToken value)
        {
            if (value != null) SetNativeAttribute(element, name, value.ToString());
        }

        private static void SetNativeAttribute(object element, string name, string value)
        {
            if (!PatternSemanticAttributeWriter.ApplySemanticAttribute(element, name, value))
                throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected attribute '" + name + "' on " + NativeType(element) + ".");
        }

        private static IEnumerable<object> Walk(object root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (object child in NativeChildren(root))
                foreach (object descendant in Walk(child)) yield return descendant;
        }

        // The label a container is addressed by: name, controlName, or "Grid" for an unnamed
        // grid - the same rule as ContainerLabel on the XML side.
        private static string NativeContainerLabel(object element)
        {
            string label = NativeAttribute(element, "name");
            if (string.IsNullOrWhiteSpace(label)) label = NativeAttribute(element, "controlName");
            if (string.IsNullOrWhiteSpace(label) && IsNativeGrid(element)) label = "Grid";
            return label;
        }

        // Walk with the named ancestors of each element (for path-qualified containers).
        private static IEnumerable<KeyValuePair<object, List<string>>> WalkWithPath(object element, List<string> ancestors)
        {
            if (element == null) yield break;
            string label = NativeContainerLabel(element);
            var path = new List<string>(ancestors);
            if (!string.IsNullOrWhiteSpace(label)) path.Add(label);
            yield return new KeyValuePair<object, List<string>>(element, path);
            foreach (object child in NativeChildren(element))
                foreach (var descendant in WalkWithPath(child, path)) yield return descendant;
        }

        private static List<object> NativeChildren(object element)
        {
            object children = GetProperty(element, "Children");
            if (!(children is IEnumerable enumerable)) return new List<object>();
            return enumerable.Cast<object>().Where(x => x != null).ToList();
        }

        private static string NativeType(object element) => GetProperty(element, "Type")?.ToString()
            ?? GetProperty(element, "Name")?.ToString() ?? string.Empty;

        private static string NativeAttribute(object element, string name)
        {
            object attributes = GetProperty(element, "Attributes");
            if (attributes == null) return null;
            MethodInfo getter = attributes.GetType().GetMethod("GetPropertyValueString", new[] { typeof(string) });
            try { return getter?.Invoke(attributes, new object[] { name })?.ToString(); }
            catch { return null; }
        }

        private static object GetProperty(object value, string name)
        {
            try { return value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value, null); }
            catch { return null; }
        }

        private static void SaveNativePattern(KBObject instance, KBObjectPart part)
        {
            WriteService.ForcePatternPartDirty(part);
            part.Save();
            instance.Save(new KBObjectSavePreferences { ForceSave = true, ForceSaveDefaultParts = true, SkipValidation = true });
        }

        /// <summary>
        /// Do two persisted contents match, byte for byte?
        ///
        /// <para>A missing side is NOT a match. The previous comparison was
        /// <c>Equals(Sha256(a), Sha256(b))</c> and <c>Sha256</c> returned null for a
        /// null input, so a restore that produced no readable content on either side
        /// compared null to null, evaluated true, and reported
        /// <c>patternRestoredExactly: true</c> for a snapshot that was never
        /// restored. A restore check that cannot see a difference must not certify
        /// one.</para>
        /// </summary>
        private static bool ContentMatches(string restored, string expected)
        {
            if (restored == null || expected == null) return false;
            return string.Equals(
                SdkReflection.Sha256Hex(restored),
                SdkReflection.Sha256Hex(expected),
                StringComparison.OrdinalIgnoreCase);
        }

        private SnapshotBundle CaptureSnapshots(KBObject instance, string patternXml, KBObject parent, string webForm)
        {
            string root = EditSnapshotStore.ResolveRoot(_objects.GetKbService().GetKbPath());
            return new SnapshotBundle
            {
                Pattern = EditSnapshotStore.SaveSnapshot(root, instance.Guid.ToString(), "PatternInstance", patternXml),
                PatternSha256 = Sha256(patternXml),
                WebForm = parent == null || webForm == null ? null
                    : EditSnapshotStore.SaveSnapshot(root, parent.Guid.ToString(), "WebForm", webForm),
                WebFormSha256 = webForm == null ? null : Sha256(webForm)
            };
        }

        private JObject RestoreSnapshots(KBObject instance, KBObjectPart part, byte[] nativeBytes,
            string patternXml, KBObject parent, string webForm, string applyOnSaveBefore)
        {
            bool patternRestored = false;
            bool webFormRestored = parent == null || webForm == null;
            bool applyOnSaveRestored = false;
            string patternError = null;
            string webFormError = null;
            try
            {
                RestorePartBytes(part, nativeBytes);
                SaveNativePattern(instance, part);
                if (!IsFalse(applyOnSaveBefore)) WwpApplyOnSaveHelper.TryEnable(instance);
                string restored = _patterns.ReadPatternPartXml(instance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId, out _, out _);
                patternRestored = ContentMatches(restored, patternXml);
                applyOnSaveRestored = string.Equals(ReadObjectProperty(instance,
                    "SDPlus_Editor_Apply_On_Save"), applyOnSaveBefore, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { patternError = (ex.InnerException ?? ex).Message; }

            if (parent != null && webForm != null)
            {
                try
                {
                    JObject response = JObject.Parse(_write.WriteObject(parent.Name, new JObject
                    {
                        ["type"] = parent.TypeDescriptor?.Name, ["part"] = "WebForm", ["mode"] = "full", ["content"] = webForm,
                        ["validate"] = true, ["rollbackOnFailure"] = true
                    }));
                    string restored = ReadPart(parent, "WebForm");
                    webFormRestored = IsSuccess(response) && ContentMatches(restored, webForm);
                }
                catch (Exception ex) { webFormError = (ex.InnerException ?? ex).Message; }
            }
            return new JObject
            {
                ["performed"] = true,
                ["patternRestoredExactly"] = patternRestored,
                ["webFormRestoredExactly"] = webFormRestored,
                ["applyOnSaveRestoredExactly"] = applyOnSaveRestored,
                ["exact"] = patternRestored && webFormRestored && applyOnSaveRestored,
                ["patternError"] = patternError,
                ["webFormError"] = webFormError
            };
        }

        private static byte[] ReadPartBytes(KBObjectPart part)
        {
            try
            {
                byte[] bytes = part?.GetType().GetMethod("GetBytes", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(part, null) as byte[];
                if (bytes != null && bytes.Length > 0) return bytes;
            }
            catch { }

            try
            {
                Type serializer = part?.GetType().Assembly.GetType(
                    "Artech.Packages.Patterns.Objects.PatternInstancePartSerializer", false);
                MethodInfo method = serializer?.GetMethod("SerializeData", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { part.GetType() }, null)
                    ?? serializer?.GetMethod("SerializeData", Compatibility.SdkMemberProbe.Static);
                return method?.Invoke(null, new object[] { part }) as byte[];
            }
            catch { return null; }
        }

        private static void RestorePartBytes(KBObjectPart part, byte[] bytes)
        {
            if (part == null || bytes == null) throw new InvalidOperationException("Native PatternInstance byte snapshot is unavailable.");
            MethodInfo setBytes = part.GetType().GetMethod("SetBytes", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(byte[]) }, null);
            if (setBytes != null)
            {
                setBytes.Invoke(part, new object[] { bytes });
                return;
            }

            Type serializer = part.GetType().Assembly.GetType(
                "Artech.Packages.Patterns.Objects.PatternInstancePartSerializer", false);
            MethodInfo deserialize = serializer?.GetMethod("DeserializeData", BindingFlags.Public | BindingFlags.Static,
                null, new[] { part.GetType(), typeof(byte[]) }, null)
                ?? serializer?.GetMethod("DeserializeData", Compatibility.SdkMemberProbe.Static);
            if (deserialize == null) throw new InvalidOperationException("PatternInstancePartSerializer is unavailable.");
            deserialize.Invoke(null, new object[] { part, bytes });
        }

        private string ReadPart(KBObject obj, string part)
        {
            if (obj == null) return null;
            try
            {
                JObject read = JObject.Parse(_objects.ReadObjectSourceForVerification(obj.Name, part, obj.TypeDescriptor?.Name));
                return read["source"]?.ToString() ?? read["content"]?.ToString();
            }
            catch { return null; }
        }

        private static JObject VerifyWebFormProjection(string webForm, JObject expectedTabs, string operation, string controlName)
        {
            if (string.IsNullOrWhiteSpace(webForm))
                return new JObject { ["confirmed"] = false, ["message"] = "The projected WebForm could not be re-read." };
            XDocument document;
            try { document = XDocument.Parse(webForm, LoadOptions.PreserveWhitespace); }
            catch { return new JObject { ["confirmed"] = false, ["message"] = "The projected WebForm is not valid XML." }; }
            bool targetPresent = FindWebFormNamedElement(document, controlName) != null;
            if (operation == "remove_tab")
                return new JObject { ["confirmed"] = !targetPresent, ["actionEventConfirmed"] = true,
                    ["message"] = targetPresent ? "The removed tab is still present in WebForm." : null };

            var orderedNames = ((JArray)expectedTabs["tabs"]).OfType<JObject>()
                .Select(t => t["controlName"]?.ToString()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            int previous = -1;
            bool ordered = true;
            foreach (string name in orderedNames)
            {
                XElement named = FindWebFormNamedElement(document, name);
                int current = named == null ? -1 : document.Root.DescendantsAndSelf().ToList().IndexOf(named);
                if (current >= 0 && current < previous) ordered = false;
                if (current >= 0) previous = current;
            }
            JObject target = ((JArray)expectedTabs["tabs"]).OfType<JObject>()
                .FirstOrDefault(t => string.Equals(t["controlName"]?.ToString(), controlName, StringComparison.OrdinalIgnoreCase));
            var actions = new List<string>();
            CollectControlNames(target?["children"], "userAction", actions);
            bool eventsConfirmed = actions.All(a => WebFormContainsEvent(document, a));
            return new JObject
            {
                ["confirmed"] = targetPresent && ordered && eventsConfirmed,
                ["targetPresent"] = targetPresent,
                ["orderConfirmed"] = ordered,
                ["actionEventConfirmed"] = eventsConfirmed,
                ["message"] = targetPresent && ordered && eventsConfirmed ? null
                    : "The projected WebForm did not confirm the requested tab order, controls, or user-action event."
            };
        }

        private static JObject VerifyPatternTabs(JObject expected, JObject persisted, string operation, string controlName)
        {
            var expectedTabs = (expected?["tabs"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var persistedTabs = (persisted?["tabs"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            bool orderConfirmed = expectedTabs.Select(t => t["controlName"]?.ToString())
                .SequenceEqual(persistedTabs.Select(t => t["controlName"]?.ToString()), StringComparer.OrdinalIgnoreCase);
            JObject expectedTarget = expectedTabs.FirstOrDefault(t => string.Equals(
                t["controlName"]?.ToString(), controlName, StringComparison.OrdinalIgnoreCase));
            JObject persistedTarget = persistedTabs.FirstOrDefault(t => string.Equals(
                t["controlName"]?.ToString(), controlName, StringComparison.OrdinalIgnoreCase));
            bool targetConfirmed = operation == "remove_tab"
                ? persistedTarget == null
                : expectedTarget != null && persistedTarget != null && IsRequestedSubset(expectedTarget, persistedTarget);
            return new JObject
            {
                ["confirmed"] = orderConfirmed && targetConfirmed,
                ["orderConfirmed"] = orderConfirmed,
                ["propertiesConfirmed"] = targetConfirmed,
                ["message"] = orderConfirmed && targetConfirmed ? null
                    : "The PatternInstance re-read did not preserve the requested tab order, identity, properties, or typed children."
            };
        }

        private static bool IsRequestedSubset(JToken expected, JToken actual)
        {
            if (expected == null) return true;
            if (actual == null) return false;
            if (expected is JObject expectedObject)
            {
                if (!(actual is JObject actualObject)) return false;
                foreach (JProperty property in expectedObject.Properties())
                {
                    if (property.Name.Equals("position", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsRequestedSubset(property.Value, actualObject[property.Name])) return false;
                }
                return true;
            }
            if (expected is JArray expectedArray)
            {
                if (!(actual is JArray actualArray) || expectedArray.Count != actualArray.Count) return false;
                for (int i = 0; i < expectedArray.Count; i++)
                    if (!IsRequestedSubset(expectedArray[i], actualArray[i])) return false;
                return true;
            }
            return string.Equals(expected.ToString(), actual.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectControlNames(JToken token, string type, List<string> result)
        {
            if (!(token is JArray array)) return;
            foreach (JObject child in array.OfType<JObject>())
            {
                if (string.Equals(child["type"]?.ToString(), type, StringComparison.OrdinalIgnoreCase))
                {
                    string name = child["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
                }
                CollectControlNames(child["children"], type, result);
            }
        }

        internal static JObject VerifyWebFormProjectionForTests(string webForm, JObject expectedTabs, string operation, string controlName)
            => VerifyWebFormProjection(webForm, expectedTabs, operation, controlName);

        internal static JObject ProjectTabsForTests(XDocument document) => ProjectTabs(document);

        internal static JObject VerifyPatternTabsForTests(JObject expected, JObject persisted, string operation, string controlName)
            => VerifyPatternTabs(expected, persisted, operation, controlName);

        internal static bool WebFormContainsEventForTests(string webForm, string actionName)
        {
            try { return WebFormContainsEvent(XDocument.Parse(webForm, LoadOptions.PreserveWhitespace), actionName); }
            catch { return false; }
        }

        private static XElement FindWebFormNamedElement(XDocument document, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return document.Root.DescendantsAndSelf().FirstOrDefault(e => e.Attributes().Any(a =>
                (a.Name.LocalName.Equals("ControlName", StringComparison.OrdinalIgnoreCase)
                 || a.Name.LocalName.Equals("controlName", StringComparison.OrdinalIgnoreCase)
                 || a.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase)
                 || a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))
                && string.Equals(a.Value, name, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool WebFormContainsEvent(XDocument document, string actionName)
        {
            if (document?.Root == null || string.IsNullOrWhiteSpace(actionName)) return false;
            return document.Root.DescendantsAndSelf().Any(e => e.Attributes().Any(a =>
                a.Name.LocalName.IndexOf("event", StringComparison.OrdinalIgnoreCase) >= 0
                && a.Value.Split(new[] { '.', ':', '/', '\\', '-', ' ', ',', ';', '(', ')', '[', ']' },
                    StringSplitOptions.RemoveEmptyEntries)
                    .Any(token => string.Equals(token, actionName, StringComparison.OrdinalIgnoreCase))));
        }

        private static string ReadObjectProperty(KBObject obj, string name)
        {
            if (obj == null) return null;
            try
            {
                MethodInfo getter = obj.GetType().GetMethod("GetPropertyValue", new[] { typeof(string) });
                return getter?.Invoke(obj, new object[] { name })?.ToString();
            }
            catch { return null; }
        }

        private static bool IsFalse(string value) => string.Equals(value, "False", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);

        private static void InsertXmlTab(XElement tabs, XElement tab, int? position)
        {
            List<XElement> peers = tabs.Elements().Where(e => IsTabElement(e.Name.LocalName)).ToList();
            int index = ClampPosition(position, peers.Count);
            if (index >= peers.Count) tabs.Add(tab); else peers[index].AddBeforeSelf(tab);
        }

        private static int ClampPosition(int? position, int count) => Math.Max(0, Math.Min(position ?? count, count));
        private static string Required(JObject value, string property, string type)
        {
            string result = value?[property]?.ToString();
            if (string.IsNullOrWhiteSpace(result)) throw new WwpTabException("InvalidWwpChild", type + " requires " + property + ".");
            return result;
        }
        private static void SetSingle(XElement element, string name, JToken value) { if (value != null) element.SetAttributeValue(name, value.ToString()); }
        private static void SetPair(XElement element, string name, JToken value)
        {
            if (value == null) return;
            string text = value.Type == JTokenType.Boolean ? (value.ToObject<bool>() ? "True" : "False") : value.ToString();
            element.SetAttributeValue(name, text);
            element.SetAttributeValue("default" + char.ToUpperInvariant(name[0]) + name.Substring(1), text);
        }
        private static JObject TabError(string code, string message) => new JObject { ["code"] = code, ["error"] = message };
        private static string Sha256(string value)
        {
            return SdkReflection.Sha256Hex(value);
        }

        /// <summary>
        /// The before-state a WWP mutation needs in order to prove what it changed.
        /// </summary>
        /// <remarks>
        /// <c>internal</c> rather than <c>private</c> so
        /// <see cref="WwpSnapshotsIncomplete"/> can take it: the rule that decides
        /// whether a mutation may proceed is five terms long, and it is only worth
        /// stating once if something can check that none of the five was dropped. Its
        /// fields were already <c>internal</c>.
        /// </remarks>
        internal sealed class SnapshotBundle
        {
            internal EditSnapshotStore.SnapshotInfo Pattern;
            internal string PatternSha256;
            internal EditSnapshotStore.SnapshotInfo WebForm;
            internal string WebFormSha256;
            internal JObject ToJson() => new JObject
            {
                ["patternInstance"] = SnapshotJson(Pattern, PatternSha256),
                ["webForm"] = SnapshotJson(WebForm, WebFormSha256)
            };
            private static JToken SnapshotJson(EditSnapshotStore.SnapshotInfo snapshot, string hash) => snapshot == null
                ? JValue.CreateNull()
                : new JObject { ["path"] = snapshot.Path, ["timestamp"] = snapshot.Timestamp,
                    ["bytes"] = snapshot.Bytes, ["sha256"] = hash };
        }

        private sealed class WwpTabException : Exception
        {
            internal readonly string Code;
            internal WwpTabException(string code, string message) : base(message) { Code = code; }
        }
    }
}
