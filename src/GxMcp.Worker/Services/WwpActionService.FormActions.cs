using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        private static bool IsFormUserActionOperation(string operation) =>
            string.Equals(operation, "add_user_action", StringComparison.OrdinalIgnoreCase);

        private string RunFormUserActionOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, JObject args)
        {
            JObject callObjectError = ValidateCallObjectArgs(args);
            if (callObjectError != null)
                return McpResponse.Err(code: callObjectError["code"].ToString(), message: callObjectError["error"].ToString(), target: target);
            KBObject callObject = null;
            if (HasCallObject(args))
            {
                callObject = ResolveCallObject(args["callObject"].ToString());
                if (callObject == null)
                    return McpResponse.Err(code: "CallObjectNotFound",
                        message: "callObject '" + args["callObject"] + "' is not a WebPanel, Procedure, Transaction, SDPanel or WebComponent in this KB.",
                        hint: "Use Name or Type:Name.", target: target);
                args["_callObjectReference"] = GxObjectReference(callObject);
            }

            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument previewDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject previewMutation = Apply(previewDocument, "add_user_action", args, ResolveProcedure);
            if (previewMutation["error"] != null)
                return McpResponse.Err(code: previewMutation["code"]?.ToString() ?? "WwpFormActionInvalid",
                    message: previewMutation["error"].ToString(), target: target, extra: previewMutation);

            JObject before = Project(beforeDocument);
            JObject after = Project(previewDocument);
            string containerName = previewMutation["containerName"]?.ToString();
            string actionName = previewMutation["actionName"]?.ToString();
            string caption = previewMutation["caption"]?.ToString();
            string eventName = previewMutation["event"]?.ToString();
            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            JObject diff = new JObject { ["before"] = before, ["after"] = after };

            if (dryRun)
                return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["operation"] = "add_user_action",
                    ["diff"] = diff,
                    ["containerName"] = containerName,
                    ["actionName"] = actionName,
                    ["event"] = eventName,
                    ["versionToken"] = versionToken,
                    ["saved"] = false,
                    ["persisted"] = false,
                    ["rollbackOnFailure"] = rollbackOnFailure,
                    ["mutationMode"] = "native-pattern-sdk",
                    ["sdkOperation"] = "PatternInstance.RootElement + AddElementCommand(userAction)"
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

                string expectedVersion = args?["baseVersion"]?.ToString()
                    ?? args?["expectedVersion"]?.ToString()
                    ?? args?["versionToken"]?.ToString();
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion,
                        "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no form action was changed.");

                XDocument lockedBefore = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedMutation = Apply(lockedAfter, "add_user_action", args, ResolveProcedure);
                if (lockedMutation["error"] != null)
                    return McpResponse.Err(code: lockedMutation["code"]?.ToString() ?? "WwpFormActionInvalid",
                        message: lockedMutation["error"].ToString(), target: target, extra: lockedMutation);

                containerName = lockedMutation["containerName"]?.ToString();
                actionName = lockedMutation["actionName"]?.ToString();
                caption = lockedMutation["caption"]?.ToString();
                eventName = lockedMutation["event"]?.ToString();
                JObject lockedBeforeProjection = Project(lockedBefore);
                JObject lockedAfterProjection = Project(lockedAfter);
                diff = new JObject { ["before"] = lockedBeforeProjection, ["after"] = lockedAfterProjection };

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "Exact PatternInstance and parent WebForm snapshots were not available; no form action was added.",
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
                    JObject nativeMutation = ApplyNativeFormUserAction(currentPart, args, callObject);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected",
                            nativeMutation["error"].ToString());

                    persistenceStarted = true;
                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);
                    KBObject persistedInstance;
                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out persistedInstance, out _);
                    if (string.IsNullOrWhiteSpace(persistedXml))
                        throw new WwpTabException("WwpFormActionNotPersisted",
                            "The SDK save completed, but the PatternInstance could not be re-read.");

                    XDocument persistedDocument = XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace);
                    JObject verification = VerifyFormUserAction(
                        lockedBefore, persistedDocument, containerName, actionName, caption,
                        args?["_callObjectReference"]?.ToString(), TryReadPopup(args, out bool? wantedPopup) ? wantedPopup : null,
                        TryReadParameters(args, out List<string> wantedParameters) ? wantedParameters : null);
                    if (verification["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpFormActionNotPersisted",
                            verification["message"]?.ToString()
                                ?? "The SDK save completed, but the requested form action was not confirmed after re-read.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance,
                        "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled",
                            "SDPlus_Editor_Apply_On_Save became False after the native form-action save.");

                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(
                        parent, persistedInstance ?? currentInstance);
                    if (!projected)
                        throw new WwpTabException("WwpProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");
                    string projectedWebForm = ReadPart(parent, "WebForm");
                    if (string.IsNullOrWhiteSpace(projectedWebForm))
                        throw new WwpTabException("WwpProjectionNotConfirmed",
                            "The projected parent WebForm could not be re-read.");

                    WriteService.NotePerTargetWrite(target);
                    JObject persistedProjection = Project(persistedDocument);
                    return McpResponse.Ok(target: target, code: "WwpFormActionUpdated", result: new JObject
                    {
                        ["instance"] = persistedInstance?.Name ?? currentInstance.Name,
                        ["parent"] = parent.Name,
                        ["operation"] = "add_user_action",
                        ["containerName"] = containerName,
                        ["actionName"] = actionName,
                        ["caption"] = caption,
                        ["event"] = eventName,
                        ["diff"] = new JObject
                        {
                            ["before"] = lockedBeforeProjection,
                            ["after"] = persistedProjection
                        },
                        ["versionToken"] = WriteService.ComputeContentVersionToken(
                            persistedInstance ?? currentInstance, persistedXml),
                        ["persisted"] = true,
                        ["saved"] = true,
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["applyOnSaveBefore"] = applyOnSaveBefore,
                        ["applyOnSaveAfter"] = applyOnSaveAfter,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["mutationMode"] = "native-pattern-sdk",
                        ["sdkOperation"] = "PatternInstance.RootElement + AddElementCommand(userAction)",
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollbackPerformed"] = false,
                        ["partialPersistenceDetected"] = false,
                        ["rollbackOnFailure"] = rollbackOnFailure,
                        ["lifecycleExecuted"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false,
                        ["securityPermissionsAdded"] = false
                    });
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    JObject rollback = rollbackOnFailure
                        ? RestoreSnapshots(currentInstance, currentPart, nativeBytes,
                            currentXml, parent, parentWebFormBefore, applyOnSaveBefore)
                        : new JObject
                        {
                            ["performed"] = false,
                            ["skipped"] = true,
                            ["exact"] = false
                        };
                    ExceptionRoot.Log("[WWP-FORMACTION] failed", ex);
                    return McpResponse.Err(code: typed?.Code ?? "WwpFormActionFailed", message: ExceptionRoot.Message(ex),
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
                            ["lifecycleExecuted"] = false,
                            ["specified"] = false,
                            ["generated"] = false,
                            ["built"] = false
                        });
                }
            }
        }

        internal static JObject ApplyNativeFormUserAction(object part, JObject args, KBObject callObject = null)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return FormActionError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");

            string containerName = args?["containerName"]?.ToString();
            if (string.IsNullOrWhiteSpace(containerName)) containerName = args?["container"]?.ToString();
            if (string.IsNullOrWhiteSpace(containerName)) containerName = "TableActions";
            else containerName = containerName.Trim();
            string actionName = args?["actionName"]?.ToString()?.Trim();
            string caption = args?["caption"]?.ToString() ?? args?["description"]?.ToString();

            JObject containerError = ResolveNativeContainer(root, containerName, out object container);
            object newContainerParent = null;
            if (containerError?["code"]?.ToString() == "FormActionContainerNotFound"
                && containerName.Equals("TableActions", StringComparison.OrdinalIgnoreCase))
            {
                containerError = ResolveNativeContainer(root, "TableMain", out newContainerParent);
                if (containerError == null && IsNativeTable(newContainerParent))
                {
                    container = CreateNativeChild(newContainerParent, "table");
                    SetNativeAttribute(container, "name", "TableActions");
                    SetNativeAttribute(container, "type", "Responsive");
                }
                else return containerError ?? FormActionError("FormActionContainerNotFound", "Creating TableActions requires a TableMain table.");
            }
            if (containerError != null) return containerError;
            if (NativeChildren(container).Any(child =>
                NativeType(child).Equals("userAction", StringComparison.OrdinalIgnoreCase)
                && string.Equals(NativeAttribute(child, "name"), actionName, StringComparison.OrdinalIgnoreCase)))
                return FormActionError("ActionAlreadyExists", "Form action '" + actionName + "' already exists in container '" + containerName + "'.");

            object created = CreateNativeChild(container, "userAction");
            SetNativeAttribute(created, "name", actionName);
            SetNativeAttribute(created, "caption", caption);
            ApplyNativeFormActionProperties(created, args);
            if (callObject != null) ApplyCallObjectNative(created, args, callObject);

            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Action) }, null);
            Action mutation = () =>
            {
                ExecuteElementCommand(container, created, "AddElementCommand", null);
                if (newContainerParent != null)
                    ExecuteElementCommand(newContainerParent, container, "AddElementCommand", null);
            };
            if (executeUpdate != null)
                executeUpdate.Invoke(part, new object[] { "genexus_wwp add_user_action", mutation });
            else
                mutation();

            return new JObject
            {
                ["changed"] = true,
                ["containerName"] = containerName,
                ["actionName"] = actionName,
                ["event"] = callObject == null ? new JValue("Do" + actionName) : JValue.CreateNull(),
                ["sdkOperation"] = "CreateChildElement(userAction) + AddElementCommand"
            };
        }

        private static void ApplyNativeFormActionProperties(object action, JObject args)
        {
            SetNativeActionAttribute(action, "condition", args?["enabledWhen"]);
            SetNativeActionAttribute(action, "visibleCondition", args?["visibleWhen"]);
            SetNativeActionAttribute(action, "tooltip", args?["description"]);
            SetNativeActionAttribute(action, "buttonClass", args?["buttonClass"]);
            SetNativeActionAttribute(action, "confirmTitle", args?["confirmTitle"]);

            string selection = args?["selection"]?.ToString();
            if (!string.IsNullOrWhiteSpace(selection))
                SetNativeAttribute(action, "multiRowSelection",
                    selection.Equals("multiple", StringComparison.OrdinalIgnoreCase) ? "True" : "False");

            if (args?["confirmation"] != null)
            {
                SetNativeAttribute(action, "confirm", "True");
                SetNativeAttribute(action, "confirmMessage", args["confirmation"].ToString());
            }

            if (args?["icon"] != null)
            {
                string icon = args["icon"].ToString();
                bool fontIcon = icon.IndexOf("fa-", StringComparison.OrdinalIgnoreCase) >= 0
                    || icon.StartsWith("fas ", StringComparison.OrdinalIgnoreCase)
                    || icon.StartsWith("far ", StringComparison.OrdinalIgnoreCase)
                    || icon.StartsWith("fab ", StringComparison.OrdinalIgnoreCase);
                SetNativeAttribute(action, fontIcon ? "fontIcon" : "image", icon);
                SetNativeAttribute(action, "imageType", fontIcon ? "Font icon" : "Image");
            }
        }

        private static void SetNativeActionAttribute(object action, string name, JToken value)
        {
            if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                SetNativeAttribute(action, name, value.ToString());
        }

        internal static JObject VerifyFormUserAction(XDocument before, XDocument after,
            string containerName, string actionName, string caption,
            string callObjectReference = null, bool? popup = null, IList<string> parameters = null)
        {
            List<XElement> containers = FindFormContainers(after, containerName).ToList();
            if (containers.Count != 1)
                return FormActionError("WwpFormActionNotPersisted",
                    "The re-read PatternInstance does not contain exactly one requested form-action container.");

            XElement action = containers[0].Elements().FirstOrDefault(element =>
                Is(element, "userAction")
                && string.Equals(Attr(element, "name"), actionName, StringComparison.OrdinalIgnoreCase));
            if (action == null)
                return FormActionError("WwpFormActionNotPersisted",
                    "The re-read PatternInstance does not contain the requested UserAction.");
            if (!string.Equals(Attr(action, "caption"), caption, StringComparison.Ordinal))
                return FormActionError("WwpFormActionNotPersisted",
                    "The re-read UserAction does not retain the requested caption.");
            if (action.Attributes().Any(attribute =>
                attribute.Name.LocalName.Equals("event", StringComparison.OrdinalIgnoreCase)))
                return FormActionError("WwpFormActionIntegrityFailed",
                    "The persisted UserAction contains an unsupported event XML attribute.");

            if (!string.IsNullOrEmpty(callObjectReference))
            {
                JObject callError = VerifyCallObject(action, callObjectReference, popup, parameters);
                if (callError != null) return callError;
            }

            return new JObject
            {
                ["confirmed"] = true,
                ["containerName"] = containerName,
                ["actionName"] = actionName,
                ["caption"] = caption,
                ["event"] = string.IsNullOrEmpty(callObjectReference) ? new JValue("Do" + actionName) : JValue.CreateNull(),
                ["beforeAvailable"] = before != null
            };
        }

        private static JObject FormActionError(string code, string message) =>
            new JObject { ["code"] = code, ["error"] = message };
    }
}
