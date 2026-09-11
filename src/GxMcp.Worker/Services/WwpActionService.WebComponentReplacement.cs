using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private const string WebComponentReplacementOperation = "replace_web_component_with_user_action";

        private static bool IsWebComponentReplacementOperation(string operation) =>
            string.Equals(operation, WebComponentReplacementOperation, StringComparison.OrdinalIgnoreCase);

        private sealed class WebComponentReplacementRequest
        {
            internal string SourceName;
            internal string UserActionName;
            internal string TablePath;
            internal string GxObject;
            internal string ControlType;
            internal string Caption;
            internal string WebComponentLoad;
            internal string Trigger;

            internal string[] PathParts => TablePath.Split('>')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();
        }

        private static bool TryCreateReplacementRequest(JObject args,
            out WebComponentReplacementRequest request, out JObject error)
        {
            request = new WebComponentReplacementRequest
            {
                SourceName = args?["sourceName"]?.ToString()?.Trim(),
                UserActionName = args?["userActionName"]?.ToString()?.Trim(),
                TablePath = args?["tablePath"]?.ToString(),
                GxObject = args?["gxobject"]?.ToString()?.Trim(),
                ControlType = args?["controlType"]?.ToString()?.Trim(),
                Caption = args?["caption"]?.ToString(),
                WebComponentLoad = args?["webComponentLoad"]?.ToString()?.Trim(),
                Trigger = args?["trigger"]?.ToString()?.Trim()
            };
            if (string.IsNullOrWhiteSpace(request.SourceName))
                return FailRequest(out error, "MissingSourceName", "sourceName is required.");
            if (string.IsNullOrWhiteSpace(request.UserActionName)) request.UserActionName = request.SourceName;
            if (string.IsNullOrWhiteSpace(request.TablePath))
                return FailRequest(out error, "MissingTablePath", "tablePath is required and must include the source node.");
            if (request.PathParts.Length < 2
                || !string.Equals(request.PathParts[request.PathParts.Length - 1], request.SourceName,
                    StringComparison.OrdinalIgnoreCase))
                return FailRequest(out error, "InvalidTablePath",
                    "tablePath must end with sourceName and identify the containing tables.");
            if (string.IsNullOrWhiteSpace(request.GxObject))
                return FailRequest(out error, "MissingGxObject", "gxobject is required so the existing WebComponent target cannot be changed accidentally.");
            if (string.IsNullOrWhiteSpace(request.ControlType)) request.ControlType = "DropDownComponent";
            if (!string.Equals(request.ControlType, "DropDownComponent", StringComparison.Ordinal))
                return FailRequest(out error, "InvalidControlType", "controlType must be exactly DropDownComponent.");
            if (string.IsNullOrWhiteSpace(request.Caption))
                return FailRequest(out error, "MissingCaption",
                    "caption is required; pass the GeneXus expression used for the selected company in the closed button.");
            if (!string.IsNullOrWhiteSpace(request.WebComponentLoad)
                && !new[] { "On Web Panel load", "On first click", "On every click" }
                    .Contains(request.WebComponentLoad, StringComparer.Ordinal))
                return FailRequest(out error, "InvalidWebComponentLoad", "webComponentLoad is not a valid DropDownComponent value.");
            if (!string.IsNullOrWhiteSpace(request.Trigger)
                && !new[] { "Click", "Hover" }.Contains(request.Trigger, StringComparer.Ordinal))
                return FailRequest(out error, "InvalidTrigger", "trigger must be Click or Hover.");
            error = null;
            return true;
        }

        private static bool FailRequest(out JObject error, string code, string message)
        {
            error = ReplacementError(code, message);
            return false;
        }

        private string RunWebComponentReplacementOperation(string target, KBObject requestedObject,
            KBObject instance, KBObjectPart instancePart, string xml, JObject args)
        {
            if (!TryCreateReplacementRequest(args, out WebComponentReplacementRequest request, out JObject requestError))
                return McpResponse.Err(code: requestError["code"].ToString(), message: requestError["error"].ToString(),
                    target: target, extra: requestError);

            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument afterDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject preview = ApplyReplacementXml(afterDocument, request);
            if (preview["error"] != null)
                return McpResponse.Err(code: preview["code"]?.ToString() ?? "WwpReplacementInvalid",
                    message: preview["error"].ToString(), target: target, extra: preview);

            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            JObject typedDiff = new JObject
            {
                ["operation"] = WebComponentReplacementOperation,
                ["path"] = request.TablePath,
                ["before"] = preview["before"]?.DeepClone(),
                ["after"] = preview["after"]?.DeepClone(),
                ["sdkOperation"] = "CreateChildElement(userAction) + RemoveElementCommand + InsertElementCommand"
            };
            if (args?["dryRun"]?.ToObject<bool?>() == true)
                return McpResponse.Ok(target: target, code: "WwpWebComponentReplacementDryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["typedDiff"] = typedDiff,
                    ["versionToken"] = versionToken,
                    ["persisted"] = false,
                    ["patternReReadConfirmed"] = false,
                    ["webFormProjectionConfirmed"] = false,
                    ["customEventsPreserved"] = false,
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
                        message: "The PatternInstance changed after the caller's read/dry-run; no replacement was applied.",
                        target: target, extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = currentVersion
                        });

                XDocument lockedBefore = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                XDocument lockedPreviewDocument = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedPreview = ApplyReplacementXml(lockedPreviewDocument, request);
                if (lockedPreview["error"] != null)
                    return McpResponse.Err(code: lockedPreview["code"]?.ToString() ?? "WwpReplacementInvalid",
                        message: lockedPreview["error"].ToString(), target: target, extra: lockedPreview);

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects);
                string parentWebFormBefore = parent == null ? null : ReadPart(parent, "WebForm");
                string parentEventsBefore = parent == null ? null : ReadPart(parent, "Events");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (nativeBytes == null || parent == null || parentWebFormBefore == null
                    || snapshots.Pattern == null || snapshots.WebForm == null)
                {
                    Logger.Warn("[WWP-REPLACE] snapshot gate target=" + target
                        + " part=" + (currentPart?.GetType().FullName ?? "null")
                        + " nativeBytes=" + (nativeBytes != null)
                        + " parent=" + (parent != null)
                        + " webForm=" + (parentWebFormBefore != null)
                        + " patternSnapshot=" + (snapshots.Pattern != null)
                        + " webFormSnapshot=" + (snapshots.WebForm != null));
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "Exact PatternInstance and parent WebForm snapshots were not available; no mutation was applied.",
                        target: target, errorExtra: new JObject
                        {
                            ["snapshot"] = snapshots.ToJson(),
                            ["nativeBytesAvailable"] = nativeBytes != null,
                            ["parentResolved"] = parent != null,
                            ["parentWebFormAvailable"] = parentWebFormBefore != null,
                            ["patternSnapshotAvailable"] = snapshots.Pattern != null,
                            ["webFormSnapshotAvailable"] = snapshots.WebForm != null,
                            ["persisted"] = false
                        });
                }

                WwpProjectionHelper.ProjectionResult projectionLifecycle = null;
                try
                {
                    JObject nativeMutation = ApplyNativeWebComponentReplacement(currentPart, request);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected",
                            nativeMutation["error"].ToString());

                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);
                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance",
                        out KBObject persistedInstance, out _);
                    JObject patternVerification = VerifyReplacementXml(lockedBefore,
                        XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace), request);
                    if (patternVerification["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpReplacementNotPersisted",
                            patternVerification["message"]?.ToString()
                                ?? "The SDK save completed, but the native replacement did not survive the PatternInstance re-read.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance,
                        "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled",
                            "SDPlus_Editor_Apply_On_Save became False after the native WWP save.");

                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent,
                        persistedInstance ?? currentInstance, out projectionLifecycle);
                    if (!projected)
                        throw new WwpTabException("WwpProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not complete the parent projection lifecycle.");
                    string projectedWebForm = ReadPart(parent, "WebForm");
                    JObject projection = VerifyReplacementProjection(projectedWebForm, request);
                    if (projection["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpProjectionNotConfirmed",
                            projection["message"]?.ToString() ?? "The projected WebForm did not confirm the DropDownComponent.");

                    string parentEventsAfter = ReadPart(parent, "Events");
                    bool customEventsPreserved = CustomEventContent(parentEventsBefore)
                        == CustomEventContent(parentEventsAfter);
                    if (!customEventsPreserved)
                        throw new WwpTabException("WwpCustomEventsChanged",
                            "The full save changed custom Events outside WorkWithPlus generated regions.");

                    WriteService.NotePerTargetWrite(target);
                    return McpResponse.Ok(target: target, code: "WwpWebComponentReplaced", result: new JObject
                    {
                        ["instance"] = persistedInstance?.Name ?? currentInstance.Name,
                        ["parent"] = parent.Name,
                        ["operation"] = WebComponentReplacementOperation,
                        ["typedDiff"] = new JObject
                        {
                            ["operation"] = WebComponentReplacementOperation,
                            ["path"] = request.TablePath,
                            ["before"] = lockedPreview["before"]?.DeepClone(),
                            ["after"] = patternVerification["after"]?.DeepClone()
                        },
                        ["sdkOperation"] = "CreateChildElement(userAction) + RemoveElementCommand + InsertElementCommand",
                        ["versionToken"] = WriteService.ComputeContentVersionToken(persistedInstance ?? currentInstance, persistedXml),
                        ["saved"] = true,
                        ["persisted"] = true,
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["customEventsPreserved"] = true,
                        ["projection"] = projection,
                        ["applyOnSaveBefore"] = applyOnSaveBefore,
                        ["applyOnSaveAfter"] = applyOnSaveAfter,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollbackPerformed"] = false,
                        ["lifecycleExecuted"] = projectionLifecycle?.LifecycleExecuted == true,
                        ["lifecycleCallbacks"] = ProjectionLifecycleJson(projectionLifecycle),
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false
                    });
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    Logger.Warn("[WWP-REPLACE] save/projection failed: " + ex);
                    JObject rollback = RestoreReplacementSnapshots(currentInstance, currentPart, nativeBytes,
                        currentXml, parent, parentWebFormBefore, applyOnSaveBefore);
                    return McpResponse.Err(code: typed?.Code ?? "WwpReplacementFailed", message: ex.Message,
                        target: target, extra: new JObject
                        {
                            ["saved"] = false,
                            ["persisted"] = false,
                            ["patternReReadConfirmed"] = false,
                            ["webFormProjectionConfirmed"] = false,
                            ["customEventsPreserved"] = false,
                            ["snapshot"] = snapshots.ToJson(),
                            ["rollback"] = rollback,
                            ["lifecycleExecuted"] = projectionLifecycle?.LifecycleExecuted == true,
                            ["lifecycleCallbacks"] = ProjectionLifecycleJson(projectionLifecycle)
                        });
                }
            }
        }

        internal static JObject ApplyReplacementXml(XDocument document, JObject args)
        {
            if (!TryCreateReplacementRequest(args, out WebComponentReplacementRequest request, out JObject error))
                return error;
            return ApplyReplacementXml(document, request);
        }

        private static JObject ApplyReplacementXml(XDocument document, WebComponentReplacementRequest request)
        {
            if (!TryFindXmlTarget(document, request, "webComponent", out XElement parent,
                out XElement source, out JObject error)) return error;
            if (source.Elements().Any(child => child.Nodes().OfType<XElement>().Any()))
                return ReplacementError("WwpReplacementChildrenUnsupported",
                    "The source WebComponent contains non-empty child elements; refusing to drop them during replacement.");

            XElement replacement = BuildReplacementElement(source, request);
            source.ReplaceWith(replacement);
            return new JObject
            {
                ["changed"] = true,
                ["path"] = request.TablePath,
                ["before"] = source.ToString(SaveOptions.DisableFormatting),
                ["after"] = replacement.ToString(SaveOptions.DisableFormatting)
            };
        }

        private static XElement BuildReplacementElement(XElement source, WebComponentReplacementRequest request)
        {
            XElement replacement = new XElement(source.Name.Namespace + "userAction");
            foreach (XAttribute attribute in source.Attributes())
            {
                string name = attribute.Name.LocalName;
                if (attribute.IsNamespaceDeclaration || IsReplacementIdentity(name)
                    || name.StartsWith("default", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase)) continue;
                replacement.SetAttributeValue(attribute.Name, attribute.Value);
            }
            replacement.SetAttributeValue("name", request.UserActionName);
            replacement.SetAttributeValue("gxobject", source.Attributes().First(a =>
                a.Name.LocalName.Equals("gxobject", StringComparison.OrdinalIgnoreCase)).Value);
            replacement.SetAttributeValue("ControlType", request.ControlType);
            replacement.SetAttributeValue("caption", request.Caption);
            if (!string.IsNullOrWhiteSpace(request.WebComponentLoad))
                replacement.SetAttributeValue("webComponentLoad", request.WebComponentLoad);
            if (!string.IsNullOrWhiteSpace(request.Trigger))
                replacement.SetAttributeValue("trigger", request.Trigger);
            return replacement;
        }

        private static JObject ApplyNativeWebComponentReplacement(KBObjectPart part,
            WebComponentReplacementRequest request)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return ReplacementError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            List<object> candidates = Walk(root).Where(element =>
                NativeType(element).Equals("webComponent", StringComparison.OrdinalIgnoreCase)
                && string.Equals(NativeAttribute(element, "name"), request.SourceName, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (object candidate in candidates)
            {
                XElement candidateXml = NativeElementXml(candidate);
                Logger.Debug("[WWP-REPLACE] native candidate name=" + NativeAttribute(candidate, "name")
                    + " path=" + GetProperty(candidate, "Path")
                    + " parent=" + NativeType(GetProperty(candidate, "Parent"))
                    + " parentName=" + NativeAttribute(GetProperty(candidate, "Parent"), "name")
                    + " xml=" + (candidateXml?.ToString(SaveOptions.DisableFormatting) ?? "<unavailable>"));
            }
            List<object> matches = candidates.Where(element =>
                NativePathMatches(element, request.PathParts, request.SourceName)).ToList();
            Logger.Debug("[WWP-REPLACE] native path candidates=" + candidates.Count + " matches=" + matches.Count
                + " requested=" + request.TablePath);
            if (matches.Count == 0) return ReplacementError("WebComponentNotFound", "The requested WebComponent path was not found in the native PatternInstance.");
            if (matches.Count > 1) return ReplacementError("AmbiguousWebComponent", "More than one WebComponent matched the requested path.");

            object source = matches[0];
            object parent = GetProperty(source, "Parent");
            if (parent == null) return ReplacementError("WwpNativeParentUnavailable", "The source WebComponent has no native parent.");
            XElement sourceXml = NativeElementXml(source);
            string existingGxObject = sourceXml == null ? null : Attr(sourceXml, "gxobject");
            if (string.IsNullOrWhiteSpace(existingGxObject)) existingGxObject = NativeAttribute(source, "gxobject");
            bool requestedGxObjectMatches = ReferenceMatches(existingGxObject, request.GxObject);
            Logger.Debug("[WWP-REPLACE] native gxobject existing=" + existingGxObject
                + " requested=" + request.GxObject + " matches=" + requestedGxObjectMatches);
            if (!requestedGxObjectMatches)
                return ReplacementError("GxObjectMismatch", "gxobject does not match the existing WebComponent target; no replacement was applied.");
            if (NativeChildren(source).Any(child => NativeChildren(child).Count > 0))
                return ReplacementError("WwpReplacementChildrenUnsupported", "The source WebComponent contains non-empty child elements; no replacement was applied.");

            object created = CreateNativeChild(parent, "userAction");
            CopySharedNativeAttributes(source, created);
            SetNativeAttribute(created, "name", request.UserActionName);
            object existingGxObjectValue = PatternSemanticAttributeWriter.ReadSemanticAttributeObject(source, "gxobject");
            if (existingGxObjectValue == null || existingGxObjectValue is string)
                throw new WwpTabException("WwpGxObjectUnavailable",
                    "The existing WebComponent gxobject could not be resolved as an SDK KBObject; no replacement was applied.");
            Logger.Debug("[WWP-REPLACE] native gxobject value type=" + existingGxObjectValue.GetType().FullName);
            if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(created, "gxobject",
                    existingGxObjectValue, existingGxObject))
                throw new WwpTabException("WwpAttributeRejected",
                    "WorkWithPlus rejected the existing gxobject KBObject on the replacement UserAction.");
            SetNativeAttribute(created, "ControlType", request.ControlType);
            SetNativeAttribute(created, "caption", request.Caption);
            if (!string.IsNullOrWhiteSpace(request.WebComponentLoad))
                SetNativeAttribute(created, "webComponentLoad", request.WebComponentLoad);
            if (!string.IsNullOrWhiteSpace(request.Trigger))
                SetNativeAttribute(created, "trigger", request.Trigger);

            int position = NativeChildren(parent).IndexOf(source);
            if (position < 0) return ReplacementError("WwpNativePositionUnavailable", "The source WebComponent position is unavailable.");
            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(Action) }, null);
            Action mutation = () =>
            {
                ExecuteElementCommand(parent, source, "RemoveElementCommand", position);
                ExecuteElementCommand(parent, created, "InsertElementCommand", position);
            };
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp " + WebComponentReplacementOperation, mutation });
            else mutation();
            return new JObject { ["changed"] = true, ["position"] = position, ["sdkOperation"] = "CreateChildElement + RemoveElementCommand + InsertElementCommand" };
        }

        private static void CopySharedNativeAttributes(object source, object target)
        {
            XElement sourceXml = NativeElementXml(source);
            if (sourceXml == null) return;
            foreach (XAttribute attribute in sourceXml.Attributes())
            {
                string name = attribute.Name.LocalName;
                if (attribute.IsNamespaceDeclaration || IsReplacementIdentity(name)
                    || name.StartsWith("default", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SharedUserActionAttributes.Contains(name))
                    throw new WwpTabException("WwpReplacementAttributeUnsupported",
                        "The source WebComponent attribute '" + name + "' is not a common UserAction property; no value was dropped.");
                SetNativeAttribute(target, name, attribute.Value);
            }
        }

        private static readonly HashSet<string> SharedUserActionAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "caption", "tooltip", "buttonClass", "imageType", "image", "fontIcon", "condition", "visibleCondition",
            "popupWidth", "cellThemeClass", "flexGrow", "align", "hAlign", "vAlign", "width", "height", "themeClass"
        };

        private static XElement NativeElementXml(object element)
        {
            try
            {
                MethodInfo method = element.GetType().GetMethod("ToXmlString", Type.EmptyTypes);
                string xml = method?.Invoke(element, null)?.ToString();
                return string.IsNullOrWhiteSpace(xml) ? null : XElement.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch { return null; }
        }

        private static bool NativePathMatches(object element, string[] parts, string sourceName)
        {
            object cursor = GetProperty(element, "Parent");
            for (int index = parts.Length - 2; index >= 0; index--)
            {
                if (cursor == null || !IsNativeTable(cursor)
                    || !string.Equals(NativeAttribute(cursor, "name"), parts[index], StringComparison.OrdinalIgnoreCase)) return false;
                cursor = GetProperty(cursor, "Parent");
            }
            return string.Equals(NativeAttribute(element, "name"), sourceName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNativeTable(object element)
        {
            if (element == null) return false;
            string type = NativeType(element);
            if (type.Equals("table", StringComparison.OrdinalIgnoreCase)
                || type.Equals("WPTable", StringComparison.OrdinalIgnoreCase)) return true;
            return NativeElementXml(element)?.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static bool TryFindXmlTarget(XDocument document, WebComponentReplacementRequest request,
            string elementType, out XElement parent, out XElement source, out JObject error)
        {
            parent = null;
            source = null;
            error = null;
            List<XElement> matches = document.Descendants().Where(element =>
                element.Name.LocalName.Equals(elementType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Attr(element, "name"), request.SourceName, StringComparison.OrdinalIgnoreCase)
                && XmlPathMatches(element, request.PathParts, request.SourceName)).ToList();
            if (matches.Count == 0) { error = ReplacementError("WebComponentNotFound", "The requested WebComponent path was not found in PatternInstance."); return false; }
            if (matches.Count > 1) { error = ReplacementError("AmbiguousWebComponent", "More than one WebComponent matched the requested path."); return false; }
            source = matches[0];
            parent = source.Parent;
            string existingGxObject = Attr(source, "gxobject");
            if (!ReferenceMatches(existingGxObject, request.GxObject))
            {
                error = ReplacementError("GxObjectMismatch", "gxobject does not match the existing WebComponent target; no replacement was applied.");
                return false;
            }
            return true;
        }

        private static bool XmlPathMatches(XElement element, string[] parts, string sourceName)
        {
            XElement cursor = element.Parent;
            for (int index = parts.Length - 2; index >= 0; index--)
            {
                if (cursor == null || !cursor.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Attr(cursor, "name"), parts[index], StringComparison.OrdinalIgnoreCase)) return false;
                cursor = cursor.Parent;
            }
            return string.Equals(Attr(element, "name"), sourceName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ReferenceMatches(string existing, string requested)
        {
            if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(requested)) return false;
            if (string.Equals(existing.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            const string knownSdkPrefix = "guid-";
            string existingCanonical = existing.Trim();
            string requestedCanonical = requested.Trim();
            if (existingCanonical.StartsWith(knownSdkPrefix, StringComparison.OrdinalIgnoreCase))
                existingCanonical = existingCanonical.Substring(knownSdkPrefix.Length);
            if (requestedCanonical.StartsWith(knownSdkPrefix, StringComparison.OrdinalIgnoreCase))
                requestedCanonical = requestedCanonical.Substring(knownSdkPrefix.Length);
            return string.Equals(existingCanonical, requestedCanonical, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReplacementIdentity(string name) =>
            name.Equals("name", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gxobject", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ControlType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("caption", StringComparison.OrdinalIgnoreCase)
            || name.Equals("webComponentLoad", StringComparison.OrdinalIgnoreCase)
            || name.Equals("trigger", StringComparison.OrdinalIgnoreCase)
            || name.Equals("type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id", StringComparison.OrdinalIgnoreCase);

        private static JObject VerifyReplacementXml(XDocument before, XDocument after,
            WebComponentReplacementRequest request)
        {
            if (!TryFindXmlTarget(before, request, "webComponent", out _, out XElement oldTarget, out JObject error)) return error;
            List<XElement> newMatches = after.Descendants().Where(element =>
                element.Name.LocalName.Equals("userAction", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Attr(element, "name"), request.UserActionName, StringComparison.OrdinalIgnoreCase)
                && XmlPathMatches(element, request.PathParts, request.UserActionName)).ToList();
            if (newMatches.Count != 1)
                return ReplacementError("WwpReplacementNotPersisted", "The re-read PatternInstance does not contain exactly one replacement UserAction at the requested path.");
            XElement newTarget = newMatches[0];
            string oldGxObject = Attr(oldTarget, "gxobject");
            string newGxObject = Attr(newTarget, "gxobject");
            bool gxObjectMatches = ReferenceMatches(newGxObject, oldGxObject);
            Logger.Debug("[WWP-REPLACE] verify oldGxObject=" + oldGxObject + " newGxObject=" + newGxObject
                + " gxObjectMatches=" + gxObjectMatches + " controlType=" + Attr(newTarget, "ControlType")
                + " caption=" + Attr(newTarget, "caption"));
            if (!string.Equals(Attr(newTarget, "ControlType"), request.ControlType, StringComparison.Ordinal)
                || !gxObjectMatches
                || !string.Equals(Attr(newTarget, "caption"), request.Caption, StringComparison.Ordinal))
                return ReplacementError("WwpReplacementNotPersisted", "The replacement UserAction did not retain ControlType, Gxobject, or caption.");

            XElement beforeCanonical = Canonicalize(before.Root, oldTarget);
            XElement afterCanonical = Canonicalize(after.Root, newTarget);
            if (!XNode.DeepEquals(beforeCanonical, afterCanonical))
                return ReplacementError("WwpReplacementIntegrityFailed", "Nodes or metadata outside the requested replacement changed during save.");
            return new JObject
            {
                ["confirmed"] = true,
                ["path"] = request.TablePath,
                ["before"] = oldTarget.ToString(SaveOptions.DisableFormatting),
                ["after"] = newTarget.ToString(SaveOptions.DisableFormatting)
            };
        }

        private static XElement Canonicalize(XElement element, XElement replaced)
        {
            if (ReferenceEquals(element, replaced)) return new XElement("__replacement__");
            XElement result = new XElement(element.Name,
                element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
                    .Select(attribute => new XAttribute(attribute.Name, attribute.Value)));
            foreach (XNode node in element.Nodes())
            {
                if (node is XElement child) result.Add(Canonicalize(child, replaced));
                else if (node is XCData cdata) result.Add(new XCData(cdata.Value));
                else if (node is XText text && !string.IsNullOrWhiteSpace(text.Value)) result.Add(new XText(text.Value));
            }
            return result;
        }

        private static JObject VerifyReplacementProjection(string webForm, WebComponentReplacementRequest request)
        {
            if (string.IsNullOrWhiteSpace(webForm))
                return ReplacementError("WwpProjectionNotConfirmed", "The parent WebForm could not be re-read after projection.");
            // The generated WebForm intentionally contains only the projected
            // control (for example ddc_EmpresaSelector) and its custom
            // properties. It does not carry the PatternInstance gxobject or
            // the textual ControlType name; those are verified authoritatively
            // in VerifyReplacementXml above.
            XDocument document;
            try { document = XDocument.Parse(webForm, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { return ReplacementError("WwpProjectionNotConfirmed", "The projected WebForm is not valid XML: " + ex.Message); }
            string projectedName = "ddc_" + request.UserActionName;
            List<XElement> matches = document.Descendants().Where(element =>
                string.Equals(Attr(element, "name"), projectedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Attr(element, "id"), projectedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Attr(element, "name"), request.UserActionName, StringComparison.OrdinalIgnoreCase)).ToList();
            matches = matches.Where(element =>
                string.Equals(Attr(element, "caption"), request.Caption, StringComparison.Ordinal)
                || string.Equals(Attr(element, "Caption"), request.Caption, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
                return ReplacementError("WwpProjectionNotConfirmed", "The projected WebForm does not expose the requested selector control and caption.");
            return new JObject
            {
                ["confirmed"] = true,
                ["controlName"] = request.UserActionName,
                ["projectedControlName"] = "ddc_" + request.UserActionName,
                ["captionConfirmed"] = true,
                ["controlTypeAuthority"] = "PatternInstance",
                ["gxobjectAuthority"] = "PatternInstance"
            };
        }

        private static JObject ProjectionLifecycleJson(WwpProjectionHelper.ProjectionResult result)
        {
            if (result == null) return new JObject { ["attempted"] = false };
            return new JObject
            {
                ["attempted"] = result.LifecycleAttempted,
                ["shouldBuild"] = result.ShouldBuild,
                ["beforeStartBuild"] = result.BeforeStartBuild,
                ["afterImportResources"] = result.AfterImportResources,
                ["updateParentObject"] = result.UpdateParentObject,
                ["afterEndBuild"] = result.AfterEndBuild,
                ["parentSaved"] = result.ParentSaved,
                ["completed"] = result.LifecycleExecuted,
                ["failure"] = result.Failure
            };
        }

        private static JObject ReplacementError(string code, string message) =>
            new JObject { ["error"] = message, ["code"] = code };

        private static string CustomEventContent(string source)
        {
            if (source == null) return null;
            const string start = "/* Generated by DVelop Work With Plus Pattern [Start]";
            const string end = "/* Generated by DVelop Work With Plus Pattern [End]";
            StringBuilder result = new StringBuilder();
            int cursor = 0;
            while (cursor < source.Length)
            {
                int startIndex = source.IndexOf(start, cursor, StringComparison.OrdinalIgnoreCase);
                if (startIndex < 0) { result.Append(source, cursor, source.Length - cursor); break; }
                result.Append(source, cursor, startIndex - cursor);
                int endIndex = source.IndexOf(end, startIndex, StringComparison.OrdinalIgnoreCase);
                if (endIndex < 0) { result.Append(source, startIndex, source.Length - startIndex); break; }
                int endComment = source.IndexOf("*/", endIndex, StringComparison.Ordinal);
                cursor = endComment < 0 ? source.Length : endComment + 2;
                result.Append("/* WWP-GENERATED-REGION */");
            }
            return result.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private JObject RestoreReplacementSnapshots(KBObject instance, KBObjectPart part, byte[] nativeBytes,
            string patternXml, KBObject parent, string webForm, string applyOnSaveBefore)
        {
            bool patternRestored = false;
            bool webFormRestored = parent == null || webForm == null;
            string patternError = null;
            string webFormError = null;
            try
            {
                RestorePartBytes(part, nativeBytes);
                SaveNativePattern(instance, part);
                if (!IsFalse(applyOnSaveBefore)) WwpApplyOnSaveHelper.TryEnable(instance);
                string restored = _patterns.ReadPatternPartXml(instance, "PatternInstance", out KBObject restoredInstance, out _);
                patternRestored = string.Equals(Sha256(restored), Sha256(patternXml), StringComparison.OrdinalIgnoreCase);
                if (parent != null && webForm != null && restoredInstance != null)
                {
                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent, restoredInstance);
                    string restoredWebForm = ReadPart(parent, "WebForm");
                    webFormRestored = projected && string.Equals(NormalizeText(restoredWebForm), NormalizeText(webForm), StringComparison.Ordinal);
                }
            }
            catch (Exception ex)
            {
                patternError = (ex.InnerException ?? ex).Message;
                webFormRestored = false;
            }
            return new JObject
            {
                ["performed"] = true,
                ["patternRestoredExactly"] = patternRestored,
                ["webFormRestoredExactly"] = webFormRestored,
                ["exact"] = patternRestored && webFormRestored,
                ["patternError"] = patternError,
                ["webFormError"] = webFormError,
                ["directWebFormWrite"] = false
            };
        }

        private static string NormalizeText(string value) => value?.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
