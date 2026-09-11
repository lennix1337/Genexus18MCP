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
                    ["versionToken"] = versionToken,
                    ["persisted"] = false,
                    ["patternReReadConfirmed"] = false,
                    ["webFormProjectionConfirmed"] = false,
                    ["lifecycleExecuted"] = false
                });

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance",
                    out KBObject currentInstance, out _);
                _patterns.BuildPatternPartEnvelope(lockedTarget, "PatternInstance", currentXml,
                    out _, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return McpResponse.Err(code: "WWPInstanceNotFound",
                        message: "The WorkWithPlus PatternInstance could not be re-resolved before save.", target: target);

                string expectedVersion = args?["baseVersion"]?.ToString()
                    ?? args?["expectedVersion"]?.ToString()
                    ?? args?["versionToken"]?.ToString();
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return McpResponse.Err(code: "StaleObject",
                        message: "The PatternInstance changed after the caller's read/dry-run; no tab mutation was applied.",
                        target: target, extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = currentVersion
                        });

                // Recompute the requested state under the per-object lock. This prevents a
                // dry-run diff from being applied to a newer in-memory PatternInstance.
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedMutation = ApplyTabXml(lockedAfter, operation, args);
                if (lockedMutation["error"] != null)
                    return McpResponse.Err(code: lockedMutation["code"]?.ToString() ?? "WwpTabInvalid",
                        message: lockedMutation["error"].ToString(), target: target, extra: lockedMutation);
                JObject expectedTabs = ProjectTabs(lockedAfter);

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");

                if (nativeBytes == null || parent == null || parentWebFormBefore == null
                    || snapshots.Pattern == null || snapshots.WebForm == null)
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

                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance",
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
                    return McpResponse.Err(code: typed?.Code ?? "WwpTabFailed", message: ex.Message,
                        target: target, extra: new JObject
                        {
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
            if (tabs == null) return TabError("TabsContainerNotFound", "The PatternInstance has no <tabs> container.");
            XElement tab = tabs.Elements().FirstOrDefault(e => Is(e, "tab")
                && string.Equals(Attr(e, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase));

            switch (operation)
            {
                case "add_tab":
                    if (tab != null) return TabError("TabAlreadyExists", "Tab '" + controlName + "' already exists.");
                    tab = new XElement(tabs.GetDefaultNamespace() + "tab",
                        new XAttribute("ControlName", controlName),
                        new XAttribute("title", args["title"].ToString()));
                    AddXmlChildren(tab, args?["children"] as JArray);
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
            return new JObject { ["changed"] = true };
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
            else throw new WwpTabException("InvalidWwpChildType", "Unsupported child type '" + type + "'. Expected variable|userAction|table.");
            return element;
        }

        private static JObject ProjectTabs(XDocument document)
        {
            var tabsResult = new JArray();
            XElement tabs = document.Descendants().FirstOrDefault(e => Is(e, "tabs"));
            if (tabs != null)
            {
                int position = 0;
                foreach (XElement tab in tabs.Elements().Where(e => Is(e, "tab")))
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

        private static JObject ApplyNativeTabMutation(KBObjectPart part, string operation, JObject args)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return TabError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            object tabs = Walk(root).FirstOrDefault(e => NativeType(e).Equals("tabs", StringComparison.OrdinalIgnoreCase));
            if (tabs == null) return TabError("TabsContainerNotFound", "The native PatternInstance has no tabs container.");
            string controlName = args["controlName"].ToString();
            List<object> existingTabs = NativeChildren(tabs).Where(e => NativeType(e).Equals("tab", StringComparison.OrdinalIgnoreCase)).ToList();
            object tab = existingTabs.FirstOrDefault(e => string.Equals(NativeAttribute(e, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase));

            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(Action) }, null);
            Action mutation = () =>
            {
                if (operation == "add_tab")
                {
                    if (tab != null) throw new WwpTabException("TabAlreadyExists", "Tab '" + controlName + "' already exists.");
                    object created = CreateNativeChild(tabs, "tab");
                    SetNativeAttribute(created, "ControlName", controlName);
                    SetNativeAttribute(created, "title", args["title"].ToString());
                    AddNativeChildren(created, args["children"] as JArray);
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

        private static void AddNativeChildren(object tab, JArray children)
        {
            if (children == null || children.Count == 0) return;
            List<JObject> typed = children.OfType<JObject>().ToList();
            if (typed.All(c => string.Equals(c["type"]?.ToString(), "table", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (JObject child in typed) AttachNativeControl(tab, child);
                return;
            }
            object table = CreateNativeChild(tab, "table");
            SetNativeAttribute(table, "type", "Responsive");
            SetNativeAttribute(table, "defaultType", "Responsive");
            foreach (JObject child in typed) AttachNativeControl(table, child);
            ExecuteElementCommand(tab, table, "AddElementCommand", null);
        }

        private static void AttachNativeControl(object parent, JObject control)
        {
            string type = control?["type"]?.ToString();
            if (type != "variable" && type != "userAction" && type != "table")
                throw new WwpTabException("InvalidWwpChildType", "Unsupported child type '" + type + "'. Expected variable|userAction|table.");
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
            else
            {
                SetNativePair(child, "type", "Responsive");
                SetNativeAttributeIf(child, "name", control["name"]);
                SetNativePair(child, "numberOfColumns", control["columns"]);
                SetNativePair(child, "themeClass", control["themeClass"]);
                foreach (JObject nested in (control["children"] as JArray ?? new JArray()).OfType<JObject>())
                    AttachNativeControl(child, nested);
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
                MethodInfo setBytes = part.GetType().GetMethod("SetBytes", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(byte[]) }, null);
                if (setBytes == null || nativeBytes == null) throw new InvalidOperationException("Native PatternInstance byte snapshot is unavailable.");
                setBytes.Invoke(part, new object[] { nativeBytes });
                SaveNativePattern(instance, part);
                if (!IsFalse(applyOnSaveBefore)) WwpApplyOnSaveHelper.TryEnable(instance);
                string restored = _patterns.ReadPatternPartXml(instance, "PatternInstance", out _, out _);
                patternRestored = string.Equals(Sha256(restored), Sha256(patternXml), StringComparison.OrdinalIgnoreCase);
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
                        ["part"] = "WebForm", ["mode"] = "full", ["content"] = webForm,
                        ["validate"] = true, ["rollbackOnFailure"] = true
                    }));
                    string restored = ReadPart(parent, "WebForm");
                    webFormRestored = IsSuccess(response)
                        && string.Equals(Sha256(restored), Sha256(webForm), StringComparison.OrdinalIgnoreCase);
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
            try { return part?.GetType().GetMethod("GetBytes", BindingFlags.Public | BindingFlags.Instance)?.Invoke(part, null) as byte[]; }
            catch { return null; }
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
            List<XElement> peers = tabs.Elements().Where(e => Is(e, "tab")).ToList();
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
            if (value == null) return null;
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private sealed class SnapshotBundle
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
