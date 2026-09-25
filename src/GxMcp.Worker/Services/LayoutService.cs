using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public partial class LayoutService
    {
        private static readonly char[] CaptionLineBreaks = { (char)13, (char)10 };
        private readonly ObjectService _objectService;

        public LayoutService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetTree(string target, string controlFilter = null, int limit = 500)
        {
            try
            {
                if (limit <= 0) limit = 500;
                if (limit > 2000) limit = 2000;

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);
                }

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var root = contextResult.Document.Root;
                if (root == null)
                {
                    return Models.McpResponse.Err(
                        code: "InvalidVisualXml",
                        message: "Invalid visual XML: root element is missing.",
                        hint: "The object's visual part may be corrupted; try re-opening the KB or inspecting the part directly.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual parts are available for this object.")),
                        target: target);
                }

                var nodes = new JArray();
                int total = 0;
                int emitted = 0;
                var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Walk(root, "/" + root.Name.LocalName, nodes, ref total, ref emitted, limit, controlFilter, null, stats);

                var res = new JObject
                {
                    ["n"] = obj.Name,
                    ["t"] = obj.TypeDescriptor.Name,
                    ["s"] = contextResult.Surface.ToString(),
                    ["total"] = total,
                    ["count"] = emitted,
                    ["stats"] = JObject.FromObject(stats),
                    ["nodes"] = nodes,
                    ["versionToken"] = WriteService.ComputeContentVersionToken(
                        obj, contextResult.Document.ToString(SaveOptions.DisableFormatting)),
                    ["empty"] = emitted == 0,
                    ["help"] = new JArray 
                    {
                        "Use genexus_layout(action='set_property', control='ControlName', propertyName='Caption', value='New Value') to modify.",
                        "Use genexus_layout(action='get_preview') to see visual rendering."
                    }
                };

                return Models.McpResponse.Ok(target: target, code: "LayoutRead", result: res);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutReadException",
                    message: ex.Message,
                    hint: "Inspect the object type and retry; if the KB is closed reopen it first.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Verify the object exists and has a visual part.")),
                    target: target);
            }
        }

        public string FindControls(string target, string propertyName = null, string query = null, int limit = 200)
        {
            try
            {
                if (limit <= 0) limit = 200;
                if (limit > 2000) limit = 2000;

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);
                }

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var root = contextResult.Document.Root;
                if (root == null)
                {
                    return Models.McpResponse.Err(
                        code: "InvalidVisualXml",
                        message: "Invalid visual XML: root element is missing.",
                        hint: "The object's visual part may be corrupted; try re-opening the KB or inspecting the part directly.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual parts are available for this object.")),
                        target: target);
                }

                string normalizedProperty = string.IsNullOrWhiteSpace(propertyName) ? null : propertyName;
                string normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query;

                var nodes = new JArray();
                int total = 0;
                int emitted = 0;
                var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Walk(root, "/" + root.Name.LocalName, nodes, ref total, ref emitted, limit, null, new FindCriteria
                {
                    PropertyName = normalizedProperty,
                    Query = normalizedQuery
                }, stats);

                var result = new JObject
                {
                    ["n"] = obj.Name,
                    ["t"] = obj.TypeDescriptor.Name,
                    ["s"] = contextResult.Surface.ToString(),
                    ["total"] = total,
                    ["count"] = emitted,
                    ["stats"] = JObject.FromObject(stats),
                    ["nodes"] = nodes,
                    ["versionToken"] = WriteService.ComputeContentVersionToken(
                        obj, contextResult.Document.ToString(SaveOptions.DisableFormatting)),
                    ["empty"] = emitted == 0,
                    ["help"] = new JArray 
                    {
                        "Use genexus_layout(action='set_property', control='ControlName', propertyName='Caption', value='New Value') to modify.",
                        "Use genexus_layout(action='get_preview') to see visual rendering of the layout."
                    }
                };
                return Models.McpResponse.Ok(target: target, code: "LayoutRead", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutFindException",
                    message: ex.Message,
                    hint: "Inspect the object type and retry; if the KB is closed reopen it first.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Verify the object exists and has a visual part.")),
                    target: target);
            }
        }

        public string SetProperty(string target, string controlName, string propertyName, string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(controlName))
                    return Models.McpResponse.Err(
                        code: "MissingControlName",
                        message: "Missing control name.",
                        hint: "Provide 'control' with the visual control identifier.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Returns the tree of control names for this object.")),
                        target: target);
                if (string.IsNullOrWhiteSpace(propertyName))
                    return Models.McpResponse.Err(
                        code: "MissingPropertyName",
                        message: "Missing property name.",
                        hint: "Provide 'propertyName' for the visual mutation (e.g. 'Caption', 'Class', 'Visible').",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Shows available controls and their current property values.")),
                        target: target);

                var obj = _objectService.FindObject(target);
                if (obj == null)
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var doc = contextResult.Document;
                string baselineXml = doc.ToString();
                var element = FindControlElement(doc, controlName);
                if (element == null)
                    return Models.McpResponse.Err(
                        code: "ControlNotFound",
                        message: "Control not found: '" + controlName + "'.",
                        hint: "Use get_tree to enumerate the control names present in this object's layout.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists all controls and their ControlName values.")),
                        target: target);

                if (string.Equals(propertyName, "Caption", StringComparison.OrdinalIgnoreCase)
                    && HasCaptionLineBreak(value))
                {
                    return Models.McpResponse.Err(
                        code: "CaptionNewlineUnsupported",
                        message: "Caption values cannot contain embedded line breaks.",
                        hint: "Use a single-line Caption. GeneXus may rename the control when a multiline caption is saved.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "set_property", ["name"] = target, ["controlName"] = controlName, ["propertyName"] = "Caption", ["value"] = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ") }, "Retry with a single-line caption.")),
                        target: target);
                }

                string attrName;
                string previous;
                if (IsTextPropertyName(propertyName))
                {
                    attrName = "InnerText";
                    previous = element.Value;
                    element.Value = value ?? string.Empty;
                }
                else
                {
                    attrName = ResolveCanonicalAttributeName(element, propertyName);

                    // gxTextBlock and other legacy controls authoritatively store the caption
                    // as a CaptionExpression Tokens XML, while WebForm controls (like gxButton)
                    // use the Caption attribute directly.
                    // Keep both in sync when CaptionExpression exists, but never delete Caption.
                    if (string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase))
                    {
                        previous = element.Attribute("Caption")?.Value ?? ExtractConstantCaptionFromTokens(element.Attribute("CaptionExpression")?.Value);
                        element.SetAttributeValue("Caption", value ?? string.Empty);
                        if (element.Attribute("CaptionExpression") != null)
                        {
                            element.SetAttributeValue("CaptionExpression", BuildConstantCaptionTokens(value ?? string.Empty));
                        }
                    }
                    else
                    {
                        previous = element.Attribute(attrName) != null ? element.Attribute(attrName).Value : null;
                        element.SetAttributeValue(attrName, value ?? string.Empty);
                    }
                }

                string normalized = doc.ToString();
                Logger.Info($"SetProperty: Target XML updated for {controlName}. attrName={attrName}. Current element attributes: {string.Join(", ", System.Linq.Enumerable.Select(element.Attributes(), a => a.Name.LocalName + "=" + a.Value))}");
                Logger.Info($"SetProperty: New XML Sample (first 500 chars): " + (normalized.Length > 500 ? normalized.Substring(0, 500) : normalized));
                
                var persistError = PersistVisualXml(
                    obj,
                    contextResult,
                    target,
                    normalized,
                    baselineXml: baselineXml,
                    compositionRepairToken: value);
                if (persistError != null) return persistError;

                var persistedObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target);
                var persistedContext = LoadVisualContext(persistedObject ?? obj, target, VisualSurface.Any);
                if (persistedContext.Error != null) return persistedContext.Error;

                var persistedElement = FindControlElement(persistedContext.Document, controlName);
                if (persistedElement == null)
                    return Models.McpResponse.Err(
                        code: "LayoutReadBackFailed",
                        message: "Layout read-back failed: control not found after save.",
                        hint: "The SDK may have renamed or dropped the control on save; use get_tree to verify the persisted layout.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the persisted layout to confirm the current control names.")),
                        target: target);

                string persistedValue;
                if (string.Equals(attrName, "InnerText", StringComparison.Ordinal))
                {
                    persistedValue = persistedElement.Value;
                }
                else if (string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase) || string.Equals(attrName, "CaptionExpression", StringComparison.OrdinalIgnoreCase))
                {
                    persistedValue = persistedElement.Attribute("Caption")?.Value
                        ?? ExtractConstantCaptionFromTokens(persistedElement.Attribute("CaptionExpression")?.Value);
                }
                else
                {
                    persistedValue = persistedElement.Attribute(attrName)?.Value;
                }

                bool match = IsPersistedValueMatch(attrName, value, persistedValue);
                bool isProcedure = string.Equals(obj.TypeDescriptor?.Name, "Procedure", StringComparison.OrdinalIgnoreCase);

                if (!match)
                {
                    if (isProcedure)
                    {
                        // Reports can defer SDK persistence. Retry a few read-backs before failing.
                        // Adaptive backoff: most persistence lands within ~350ms, so probe
                        // fast first (100/200ms) and only fall back to 350ms — cuts the
                        // common-case retry wait from up to 2.1s to under 600ms.
                        int[] backoffMs = { 100, 200, 350, 350, 500, 500 };
                        for (int attempt = 0; attempt < backoffMs.Length && !match; attempt++)
                        {
                            System.Threading.Thread.Sleep(backoffMs[attempt]);
                            var retryObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                            var retryContext = LoadVisualContext(retryObject, target, VisualSurface.Any);
                            if (retryContext.Error != null) break;

                            var retryElement = FindControlElement(retryContext.Document, controlName);
                            if (retryElement == null) break;

                            persistedValue = string.Equals(attrName, "InnerText", StringComparison.Ordinal)
                                ? retryElement.Value
                                : ((string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase) || string.Equals(attrName, "CaptionExpression", StringComparison.OrdinalIgnoreCase))
                                    ? (retryElement.Attribute("Caption")?.Value ?? ExtractConstantCaptionFromTokens(retryElement.Attribute("CaptionExpression")?.Value))
                                    : (retryElement.Attribute(attrName)?.Value));
                            match = IsPersistedValueMatch(attrName, value, persistedValue);
                        }
                    }
                    if (!match)
                    {
                        // Roll back to baseline XML on verification failure
                        if (!string.IsNullOrEmpty(baselineXml))
                        {
                            try
                            {
                                PersistVisualXml(obj, contextResult, target, baselineXml, baselineXml: null);
                            }
                            catch (Exception rbEx)
                            {
                                Logger.Warn($"SetProperty: rollback to baseline failed: {rbEx.Message}");
                            }
                        }

                        return Models.McpResponse.Err(
                            code: "LayoutWriteVerificationFailed",
                            message: "Layout write verification failed: persisted value does not match requested value after SDK save and read-back. Original layout was rolled back.",
                            hint: "The SDK may have normalised the value on save; read back the property to check the canonical form.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Reads the current persisted value of the control.")),
                            target: target,
                            extra: new JObject { ["rolledBack"] = true });
                    }
                }

                var result = new JObject
                {
                    ["name"] = obj.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["control"] = controlName,
                    ["propertyName"] = attrName,
                    ["previousValue"] = previous,
                    ["value"] = persistedValue
                };
                GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(result, GxMcp.Worker.Helpers.WriteResultMeta.RawXml);
                return Models.McpResponse.Ok(target: target, code: "LayoutWritten", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutSetPropertyException",
                    message: ex.Message,
                    hint: "Check the control name and property value, then retry.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the current layout to confirm the control still exists.")),
                    target: target);
            }
        }

        public string GetVisualPreview(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);
                }

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var snapshotService = new VisualSnapshotService();
                string base64 = snapshotService.GetSnapshotBase64(contextResult.Document.ToString());

                return Models.McpResponse.Ok(target: target, code: "LayoutPreview", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["type"] = obj.TypeDescriptor.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["snapshot"] = base64
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutPreviewException",
                    message: ex.Message,
                    hint: "Verify the object has a renderable visual part and retry.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Checks which visual surfaces are available for this object.")),
                    target: target);
            }
        }

        public string SetProperties(string target, JArray changes)
        {
            try
            {
                if (changes == null || changes.Count == 0)
                    return Models.McpResponse.Err(
                        code: "MissingChanges",
                        message: "Missing changes array.",
                        hint: "Provide 'changes' with at least one mutation item (each requires 'control' and 'propertyName').",
                        // no-nextStep: caller has no prior context to suggest a follow-up before they supply the argument
                        target: target);

                var obj = _objectService.FindObject(target);
                if (obj == null)
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var doc = contextResult.Document;
                string baselineXml = doc.ToString();
                var applied = new JArray();

                foreach (var token in changes)
                {
                    var change = token as JObject;
                    if (change == null) continue;

                    string controlName = change["control"]?.ToString();
                    string propertyName = change["propertyName"]?.ToString();
                    string value = change["value"]?.ToString();

                    if (string.IsNullOrWhiteSpace(controlName) || string.IsNullOrWhiteSpace(propertyName))
                    {
                        return Models.McpResponse.Err(
                            code: "InvalidChangeEntry",
                            message: "Invalid change entry: each item requires 'control' and 'propertyName'.",
                            hint: "Ensure every object in 'changes' has both a 'control' and a 'propertyName' field.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists control names available for this object.")),
                            target: target);
                    }

                    var element = FindControlElement(doc, controlName);
                    if (element == null)
                    {
                        return Models.McpResponse.Err(
                        code: "ControlNotFound",
                        message: "Control not found: '" + controlName + "'.",
                        hint: "Use get_tree to enumerate the control names present in this object's layout.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists all controls and their ControlName values.")),
                        target: target);
                    }

                    string attrName;
                    string previous;
                    if (IsTextPropertyName(propertyName))
                    {
                        attrName = "InnerText";
                        previous = element.Value;
                        element.Value = value ?? string.Empty;
                    }
                    else
                    {
                        attrName = ResolveCanonicalAttributeName(element, propertyName);
                        if (string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase))
                        {
                            previous = element.Attribute("Caption")?.Value ?? ExtractConstantCaptionFromTokens(element.Attribute("CaptionExpression")?.Value);
                            element.SetAttributeValue("Caption", value ?? string.Empty);
                            if (element.Attribute("CaptionExpression") != null)
                            {
                                element.SetAttributeValue("CaptionExpression", BuildConstantCaptionTokens(value ?? string.Empty));
                            }
                        }
                        else
                        {
                            previous = element.Attribute(attrName) != null ? element.Attribute(attrName).Value : null;
                            element.SetAttributeValue(attrName, value ?? string.Empty);
                        }
                    }

                    applied.Add(new JObject
                    {
                        ["control"] = controlName,
                        ["propertyName"] = attrName,
                        ["previousValue"] = previous,
                        ["value"] = value ?? string.Empty
                    });
                }

                string normalized = doc.ToString();
                var persistError = PersistVisualXml(
                    obj,
                    contextResult,
                    target,
                    normalized,
                    baselineXml: baselineXml);
                if (persistError != null) return persistError;

                var persistedObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target);
                var persistedContext = LoadVisualContext(persistedObject ?? obj, target, VisualSurface.Any);
                if (persistedContext.Error != null) return persistedContext.Error;

                foreach (var token in applied)
                {
                    var appliedItem = token as JObject;
                    if (appliedItem == null) continue;

                    string controlName = appliedItem["control"]?.ToString();
                    string attrName = appliedItem["propertyName"]?.ToString();
                    string expected = appliedItem["value"]?.ToString() ?? string.Empty;

                    var persistedEl = FindControlElement(persistedContext.Document, controlName);
                    if (persistedEl == null)
                    {
                        bool rolledBack = false;
                        if (!string.IsNullOrEmpty(baselineXml))
                        {
                            try
                            {
                                var rbErr = PersistVisualXml(obj, contextResult, target, baselineXml);
                                rolledBack = rbErr == null;
                            }
                            catch { }
                        }
                        return Models.McpResponse.Err(
                            code: "LayoutReadBackFailed",
                            message: "Layout read-back failed: control '" + controlName + "' was not found after save." + (rolledBack ? " Changes were rolled back." : ""),
                            hint: "The SDK may have renamed or dropped the control on save; use get_tree to verify.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the persisted layout to confirm current control names.")),
                            target: target,
                            extra: new JObject { ["rolledBack"] = rolledBack, ["control"] = controlName, ["property"] = attrName });
                    }

                    string actual = string.Equals(attrName, "InnerText", StringComparison.Ordinal)
                        ? (persistedEl.Value ?? string.Empty)
                        : (persistedEl.Attribute(attrName) != null
                            ? persistedEl.Attribute(attrName).Value
                            : (string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase)
                                ? ExtractConstantCaptionFromTokens(persistedEl.Attribute("CaptionExpression")?.Value)
                                : string.Empty));
                    if (!IsPersistedValueMatch(attrName, expected, actual))
                    {
                        bool rolledBack = false;
                        if (!string.IsNullOrEmpty(baselineXml))
                        {
                            try
                            {
                                var rbErr = PersistVisualXml(obj, contextResult, target, baselineXml);
                                rolledBack = rbErr == null;
                            }
                            catch { }
                        }
                        return Models.McpResponse.Err(
                            code: "LayoutWriteVerificationFailed",
                            message: "Layout write verification failed: persisted value for control '" + controlName + "' property '" + attrName + "' does not match requested value." + (rolledBack ? " Changes were rolled back." : ""),
                            hint: "The SDK may have normalised the value on save; read back the property to check the canonical form.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Reads the current persisted value of the control.")),
                            target: target,
                            extra: new JObject { ["rolledBack"] = rolledBack, ["control"] = controlName, ["property"] = attrName, ["expected"] = expected, ["actual"] = actual });
                    }
                }

                var bulkResult = new JObject
                {
                    ["name"] = obj.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["applied"] = applied,
                    ["count"] = applied.Count
                };
                GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(bulkResult, GxMcp.Worker.Helpers.WriteResultMeta.RawXml);
                return Models.McpResponse.Ok(target: target, code: "LayoutWritten", result: bulkResult);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutSetPropertiesException",
                    message: ex.Message,
                    hint: "Check the change entries and retry; use get_tree to confirm valid control names.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm control names before retrying.")),
                    target: target);
            }
        }

        public string RenamePrintBlock(string target, string currentName, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(newName))
                {
                    return Models.McpResponse.Err(
                        code: "MissingPrintBlockNames",
                        message: "Missing print block names.",
                        hint: "Provide both 'currentName' and 'newName' to rename a print block.",
                        // no-nextStep: caller must supply the argument values before any tool call is meaningful
                        target: target);
                }

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);
                }

                var context = LoadVisualContext(obj, target, VisualSurface.Report);
                if (context.Error != null) return context.Error;
                if (context.VisualPart == null)
                {
                    return Models.McpResponse.Err(
                        code: "ReportPartNotFound",
                        message: "Report part not found.",
                        hint: "This operation requires a Procedure with a report layout part; verify the target is a report-capable Procedure.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual surfaces are present for this object.")),
                        target: target);
                }

                var kb = _objectService.GetKbService().GetKB();
                if (kb == null)
                {
                    return Models.McpResponse.Err(
                        code: "KbNotOpened",
                        message: "KB not opened.",
                        hint: "Open a Knowledge Base before mutating the report layout.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_kb", new JObject { ["action"] = "open" }, "Opens the configured Knowledge Base.")),
                        retryAfterMs: 2000,
                        target: target);
                }

                string sourceSnapshot = GetProcedureSourceSnapshot(obj);

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, context.Document.ToString(), out string normalizeError))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockSourceSyncFailed",
                                message: "Rename print block source sync failed: " + normalizeError,
                                hint: "The Procedure source could not be updated to match the renamed print block; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current print block names.")),
                                target: target);
                        }

                        if (!ReportLayoutHelper.RenamePrintBlock(context.VisualPart, currentName, newName, persist: false))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockFailed",
                                message: "Rename print block failed: the SDK could not stage the rename operation.",
                                hint: "Verify that 'currentName' matches an existing print block in the report layout.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists all print blocks in the report layout.")),
                                target: target);
                        }

                        if (!TryRenamePrintCommandInSourceInMemory(obj, currentName, newName, out string sourcePrepareError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockSourceSyncFailed",
                                message: "Rename print block source sync failed: " + sourcePrepareError,
                                hint: "The Procedure source rename step failed; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm current print block names.")),
                                target: target);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockFailed",
                                message: "Rename print block failed: " + partSaveError,
                                hint: "The visual part save failed after staging; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to check if the rename partially persisted.")),
                                target: target);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        TryRestoreProcedureSource(obj, sourceSnapshot);
                        tx.Rollback();
                        return Models.McpResponse.Err(
                            code: "RenamePrintBlockFailed",
                            message: "Rename print block failed: " + ex.Message,
                            hint: "An unexpected exception occurred; the transaction was rolled back.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                            target: target);
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");

                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                if (refreshed.Error != null) return refreshed.Error;

                bool exists = refreshed.Document.Descendants("PrintBlock")
                    .Any(pb => string.Equals(Attr(pb, "Name"), newName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Attr(pb, "ControlName"), newName, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    for (int attempt = 0; attempt < 20 && !exists; attempt++)
                    {
                        System.Threading.Thread.Sleep(500);
                        var retryObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                        var retry = LoadVisualContext(retryObj, target, VisualSurface.Report);
                        if (retry.Error != null) break;
                        exists = retry.Document.Descendants("PrintBlock")
                            .Any(pb => string.Equals(Attr(pb, "Name"), newName, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(Attr(pb, "ControlName"), newName, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (!exists)
                {
                    var healObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? obj;
                    var healContext = LoadVisualContext(healObj, target, VisualSurface.Report);
                    if (healContext.Error == null && healContext.Document != null)
                    {
                        if (TryNormalizeReportPrintCommandsInSourceInMemory(healObj, healContext.Document.ToString(), out _))
                        {
                            TryFlushSourceForLayoutMutation(healObj, out _);
                        }
                    }

                    return Models.McpResponse.Err(
                        code: "RenamePrintBlockVerificationFailed",
                        message: "Rename print block verification failed: the renamed print block was not found in the persisted report XML.",
                        hint: "The SDK committed the transaction but the read-back did not surface the new name; open the Procedure in the IDE to inspect.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to see the current print block names.")),
                        target: target);
                }

                return Models.McpResponse.Ok(target: target, code: "PrintBlockRenamed", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["operation"] = "RenamePrintBlock",
                    ["currentName"] = currentName,
                    ["newName"] = newName
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "RenamePrintBlockException",
                    message: ex.Message,
                    hint: "An unexpected exception occurred; retry or inspect the Procedure in the IDE.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                    target: target);
            }
        }

        public string AddPrintBlock(string target, string printBlockName, int? height)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(printBlockName))
                {
                    return Models.McpResponse.Err(
                        code: "MissingPrintBlockName",
                        message: "Missing print block name.",
                        hint: "Provide 'printBlockName' with a non-empty identifier for the new print block.",
                        // no-nextStep: caller must supply the argument value before any tool call is meaningful
                        target: target);
                }

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);
                }

                var context = LoadVisualContext(obj, target, VisualSurface.Report);
                if (context.Error != null) return context.Error;
                if (context.VisualPart == null)
                {
                    return Models.McpResponse.Err(
                        code: "ReportPartNotFound",
                        message: "Report part not found.",
                        hint: "This operation requires a Procedure with a report layout part; verify the target is a report-capable Procedure.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual surfaces are present for this object.")),
                        target: target);
                }

                var kb = _objectService.GetKbService().GetKB();
                if (kb == null)
                {
                    return Models.McpResponse.Err(
                        code: "KbNotOpened",
                        message: "KB not opened.",
                        hint: "Open a Knowledge Base before mutating the report layout.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_kb", new JObject { ["action"] = "open" }, "Opens the configured Knowledge Base.")),
                        retryAfterMs: 2000,
                        target: target);
                }

                string sourceSnapshot = GetProcedureSourceSnapshot(obj);

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, context.Document.ToString(), out string normalizeError))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockSourceSyncFailed",
                                message: "Add print block source sync failed: " + normalizeError,
                                hint: "The Procedure source could not be updated; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current print blocks.")),
                                target: target);
                        }

                        if (!ReportLayoutHelper.AddPrintBlock(context.VisualPart, printBlockName, height, persist: false))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockFailed",
                                message: "Add print block failed: the SDK could not stage the new print block.",
                                hint: "Ensure the Procedure has a report layout part and the printBlockName is unique.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists existing print blocks to check for name conflicts.")),
                                target: target);
                        }

                        if (!TryInsertPrintCommandInSourceInMemory(obj, printBlockName, out string sourcePrepareError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockSourceSyncFailed",
                                message: "Add print block source sync failed: " + sourcePrepareError,
                                hint: "The Procedure source insertion step failed; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm current print blocks.")),
                                target: target);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockFailed",
                                message: "Add print block failed: " + partSaveError,
                                hint: "The visual part save failed after staging; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to check if the block partially persisted.")),
                                target: target);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        TryRestoreProcedureSource(obj, sourceSnapshot);
                        tx.Rollback();
                        return Models.McpResponse.Err(
                            code: "AddPrintBlockFailed",
                            message: "Add print block failed: " + ex.Message,
                            hint: "An unexpected exception occurred; the transaction was rolled back.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                            target: target);
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");

                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                if (refreshed.Error != null) return refreshed.Error;

                var added = refreshed.Document.Descendants("PrintBlock")
                    .FirstOrDefault(pb => string.Equals(Attr(pb, "Name"), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(Attr(pb, "ControlName"), printBlockName, StringComparison.OrdinalIgnoreCase));
                if (added == null)
                {
                    for (int attempt = 0; attempt < 20 && added == null; attempt++)
                    {
                        System.Threading.Thread.Sleep(500);
                        var retryObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                        var retry = LoadVisualContext(retryObj, target, VisualSurface.Report);
                        if (retry.Error != null) break;
                        added = retry.Document.Descendants("PrintBlock")
                            .FirstOrDefault(pb => string.Equals(Attr(pb, "Name"), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(Attr(pb, "ControlName"), printBlockName, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (added == null)
                {
                    var healObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? obj;
                    var healContext = LoadVisualContext(healObj, target, VisualSurface.Report);
                    if (healContext.Error == null && healContext.Document != null)
                    {
                        if (TryNormalizeReportPrintCommandsInSourceInMemory(healObj, healContext.Document.ToString(), out _))
                        {
                            TryFlushSourceForLayoutMutation(healObj, out _);
                        }
                    }

                    return Models.McpResponse.Err(
                        code: "AddPrintBlockVerificationFailed",
                        message: "Add print block verification failed: the new print block was not found in the persisted report XML.",
                        hint: "The SDK committed the transaction but the read-back did not surface the new block; open the Procedure in the IDE to inspect.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to see current print blocks.")),
                        target: target);
                }

                return Models.McpResponse.Ok(target: target, code: "PrintBlockAdded", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["operation"] = "AddPrintBlock",
                    ["printBlockName"] = printBlockName,
                    ["height"] = Attr(added, "Height")
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AddPrintBlockException",
                    message: ex.Message,
                    hint: "An unexpected exception occurred; retry or inspect the Procedure in the IDE.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                    target: target);
            }
        }

        public string DeletePrintBlock(string target, string printBlockName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(printBlockName))
                {
                    return Models.McpResponse.Err(
                        code: "MissingArgument",
                        message: "printBlockName is required.",
                        hint: "Pass the name of the print block to remove, e.g. printBlockName=header.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists all print blocks in the report layout.")),
                        target: target);
                }

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);
                }

                var context = LoadVisualContext(obj, target, VisualSurface.Report);
                if (context.Error != null) return context.Error;
                if (context.VisualPart == null)
                {
                    return Models.McpResponse.Err(
                        code: "ReportPartNotFound",
                        message: "Report part not found.",
                        hint: "This operation requires a Procedure with a report layout part; verify the target is a report-capable Procedure.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual surfaces are present for this object.")),
                        target: target);
                }

                var kb = _objectService.GetKbService().GetKB();
                if (kb == null)
                {
                    return Models.McpResponse.Err(
                        code: "KbNotOpened",
                        message: "KB not opened.",
                        hint: "Open a Knowledge Base before mutating the report layout.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_kb", new JObject { ["action"] = "open" }, "Opens the configured Knowledge Base.")),
                        retryAfterMs: 2000,
                        target: target);
                }

                string sourceSnapshot = GetProcedureSourceSnapshot(obj);

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!ReportLayoutHelper.DeletePrintBlock(context.VisualPart, printBlockName, persist: false))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "DeletePrintBlockFailed",
                                message: "Delete print block failed: the SDK could not stage the removal of '" + printBlockName + "'.",
                                hint: "Ensure the print block exists and is not protected (Header/Footer bands may be required by the report).",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists existing print blocks.")),
                                target: target);
                        }

                        if (!TryRemovePrintCommandFromSourceInMemory(obj, printBlockName, out string sourceSyncError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "DeletePrintBlockSourceSyncFailed",
                                message: "Delete print block source sync failed: " + sourceSyncError,
                                hint: "The Procedure source could not be updated; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm current print blocks.")),
                                target: target);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "DeletePrintBlockPersistFailed",
                                message: "Delete print block persistence failed: " + partSaveError,
                                hint: "The SDK could not save the layout part; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm current print blocks.")),
                                target: target);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");

                // Cold read-back to prove the block is really gone from disk.
                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                bool stillThere = refreshed.Error == null && refreshed.Document != null && refreshed.Document.Descendants("PrintBlock")
                    .Any(pb => string.Equals(Attr(pb, "Name"), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Attr(pb, "ControlName"), printBlockName, StringComparison.OrdinalIgnoreCase));
                if (stillThere)
                {
                    return Models.McpResponse.Err(
                        code: "DeletePrintBlockVerificationFailed",
                        message: "Delete print block verification failed: '" + printBlockName + "' is still present after commit.",
                        hint: "The transaction committed but the read-back still shows the block; open the Procedure in the IDE to inspect.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to see current print blocks.")),
                        target: target);
                }

                return Models.McpResponse.Ok(target: target, code: "PrintBlockDeleted", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["operation"] = "DeletePrintBlock",
                    ["printBlockName"] = printBlockName
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DeletePrintBlockException",
                    message: ex.Message,
                    hint: "An unexpected exception occurred; retry or inspect the Procedure in the IDE.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                    target: target);
            }
        }

        public string InspectSurface(string target, int limit = 50)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name matches an entry in the active Knowledge Base.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                        target: target);
                }

                var parts = new[] { "Layout", "PatternVirtual", "WebForm" };
                var surfaces = new JArray();
                var partsCatalog = new JArray();

                foreach (KBObjectPart p in obj.Parts)
                {
                    partsCatalog.Add(new JObject
                    {
                        ["name"] = p.TypeDescriptor?.Name ?? p.GetType().Name,
                        ["guid"] = p.Type.ToString(),
                        ["type"] = p.GetType().FullName,
                        ["isSource"] = p is ISource
                    });
                }

                int totalCandidates = 0;

                foreach (var partName in parts)
                {
                    var part = PartAccessor.GetPart(obj, partName);
                    if (part == null) continue;

                    var partInfo = new JObject
                    {
                        ["part"] = partName,
                        ["type"] = part.GetType().FullName,
                        ["isSource"] = part is ISource
                    };

                    var xmlCandidates = new JArray();
                    var candidatesCollected = CollectXmlCandidates(part, includeNonPublic: true, includeNested: true);
                    totalCandidates += candidatesCollected.Count;

                    foreach (var candidate in candidatesCollected.OrderByDescending(c => c.Score).Take(limit))
                    {
                        xmlCandidates.Add(new JObject
                        {
                            ["member"] = candidate.MemberName,
                            ["sourcePath"] = candidate.SourcePath,
                            ["kind"] = candidate.MemberKind,
                            ["writable"] = candidate.MemberWritable,
                            ["depth"] = candidate.Depth,
                            ["score"] = candidate.Score,
                            ["root"] = candidate.Document?.Root?.Name.LocalName,
                            ["nodes"] = candidate.Document?.Descendants().Count() ?? 0,
                            ["controlAttrs"] = candidate.Document?.Descendants().Count(e => e.Attribute("ControlName") != null) ?? 0
                        });
                    }

                    partInfo["candidatesCount"] = candidatesCollected.Count;
                    partInfo["candidatesReturned"] = xmlCandidates.Count;
                    partInfo["xmlCandidates"] = xmlCandidates;
                    surfaces.Add(partInfo);
                }

                bool isEmpty = surfaces.Count == 0;

                var resultObj = new JObject();
                resultObj["name"] = obj.Name;
                resultObj["type"] = obj.TypeDescriptor.Name;
                resultObj["empty"] = isEmpty;
                resultObj["totalSurfaces"] = surfaces.Count;
                resultObj["totalParts"] = partsCatalog.Count;
                resultObj["totalCandidates"] = totalCandidates;
                resultObj["partsCatalog"] = partsCatalog;
                resultObj["surfaces"] = surfaces;
                
                if (isEmpty)
                {
                    resultObj["help"] = "No structural definitions found. Object lacks supported visual XML parts.";
                }
                else if (totalCandidates > limit)
                {
                    resultObj["help"] = $"Output truncated ({limit} out of {totalCandidates} candidates shown per surface).";
                }

                return Models.McpResponse.Ok(target: target, code: "LayoutSurfaceInspected", result: resultObj);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutInspectSurfaceException",
                    message: ex.Message,
                    hint: "Verify the object exists in the active KB and retry.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Confirms the object is present in the KB.")),
                    target: target);
            }
        }


        private static void Walk(XElement current, string path, JArray nodes, ref int total, ref int emitted, int limit, string controlFilter, FindCriteria findCriteria, Dictionary<string, int> stats)
        {
            if (current == null) return;
            total++;

            string tag = current.Name.LocalName;
            if (stats != null)
            {
                stats[tag] = stats.TryGetValue(tag, out int currentCount) ? currentCount + 1 : 1;
            }

            string controlName = Attr(current, "ControlName");
            bool matchesByControl = string.IsNullOrWhiteSpace(controlFilter) ||
                                    string.Equals(controlName, controlFilter, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Attr(current, "InternalName"), controlFilter, StringComparison.OrdinalIgnoreCase);
            bool matchesByCriteria = MatchesCriteria(current, findCriteria);
            bool matches = matchesByControl && matchesByCriteria;

            if (matches && emitted < limit)
            {
                var node = new JObject
                {
                    ["p"] = path,
                    ["t"] = tag,
                    ["n"] = controlName,
                    ["c"] = Attr(current, "Caption") ?? Attr(current, "Text"),
                    ["k"] = Attr(current, "Class"),
                    ["v"] = Attr(current, "Attribute") ?? Attr(current, "Variable"),
                    ["left"] = Attr(current, "Left") ?? Attr(current, "X"),
                    ["top"] = Attr(current, "Top") ?? Attr(current, "Y"),
                    ["width"] = Attr(current, "Width"),
                    ["height"] = Attr(current, "Height"),
                    ["font"] = Attr(current, "Font") ?? Attr(current, "FontName"),
                    ["fontSize"] = Attr(current, "FontSize"),
                    ["alignment"] = Attr(current, "Alignment"),
                    ["picture"] = Attr(current, "Picture")
                };
                nodes.Add(node);
                emitted++;
            }

            int idx = 0;
            foreach (var child in current.Elements())
            {
                idx++;
                Walk(child, path + "/" + child.Name.LocalName + "[" + idx + "]", nodes, ref total, ref emitted, limit, controlFilter, findCriteria, stats);
            }
        }

        private static bool MatchesCriteria(XElement element, FindCriteria criteria)
        {
            if (criteria == null) return true;
            if (string.IsNullOrWhiteSpace(criteria.PropertyName) && string.IsNullOrWhiteSpace(criteria.Query)) return true;

            string searchValue;
            if (!string.IsNullOrWhiteSpace(criteria.PropertyName))
            {
                if (IsTextPropertyName(criteria.PropertyName))
                {
                    searchValue = element.Value;
                }
                else
                {
                    var resolved = ResolveCanonicalAttributeName(element, criteria.PropertyName);
                    searchValue = Attr(element, resolved);
                }
            }
            else
            {
                searchValue = string.Join(" ",
                    Attr(element, "ControlName"),
                    Attr(element, "InternalName"),
                    Attr(element, "Caption"),
                    Attr(element, "Class"),
                    Attr(element, "Attribute"),
                    Attr(element, "Variable"),
                    element.Name.LocalName);
            }

            if (string.IsNullOrWhiteSpace(criteria.Query)) return true;
            return (searchValue ?? string.Empty).IndexOf(criteria.Query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static XElement FindControlElement(XDocument doc, string controlName)
        {
            if (string.IsNullOrWhiteSpace(controlName)) return null;

            if (controlName.StartsWith("/", StringComparison.Ordinal))
            {
                return FindElementByPath(doc, controlName);
            }

            var match = doc
                .Descendants()
                .FirstOrDefault(el =>
                    string.Equals(Attr(el, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Attr(el, "InternalName"), controlName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // Fallback: legacy gxTextBlock / fieldset / table emit an `id` attribute instead of
            // ControlName. Search those only after the canonical fields miss so we don't
            // accidentally hijack a name when both forms coexist on different elements.
            return doc
                .Descendants()
                .FirstOrDefault(el =>
                    string.Equals(Attr(el, "id"), controlName, StringComparison.OrdinalIgnoreCase));
        }

        private static XElement FindElementByPath(XDocument doc, string path)
        {
            if (doc?.Root == null || string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            XElement current = doc.Root;
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                string name = segment;
                int index = 1;

                int idxStart = segment.LastIndexOf('[');
                int idxEnd = segment.LastIndexOf(']');
                if (idxStart > 0 && idxEnd > idxStart)
                {
                    name = segment.Substring(0, idxStart);
                    int parsedIndex;
                    if (int.TryParse(segment.Substring(idxStart + 1, idxEnd - idxStart - 1), out parsedIndex) && parsedIndex > 0)
                    {
                        index = parsedIndex;
                    }
                }

                if (i == 0)
                {
                    if (!string.Equals(current.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                    continue;
                }

                var byName = current.Elements(name).ElementAtOrDefault(index - 1);
                if (byName != null)
                {
                    current = byName;
                    continue;
                }

                var byAbsoluteIndex = current.Elements().ElementAtOrDefault(index - 1);
                if (byAbsoluteIndex != null && string.Equals(byAbsoluteIndex.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    current = byAbsoluteIndex;
                    continue;
                }

                current = null;
                if (current == null) return null;
            }

            return current;
        }

        private static string ResolveCanonicalAttributeName(XElement element, string requested)
        {
            var knownAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "caption", "Caption" },
                { "text", "Caption" },
                { "class", "Class" },
                { "visible", "Visible" },
                { "enabled", "Enabled" },
                { "readonly", "ReadOnly" },
                { "x", "Left" },
                { "left", "Left" },
                { "y", "Top" },
                { "top", "Top" }
            };

            if (knownAliases.TryGetValue(requested ?? string.Empty, out string alias))
            {
                requested = alias;
            }

            var existing = element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, requested, StringComparison.OrdinalIgnoreCase));

            return existing != null ? existing.Name.LocalName : (requested ?? string.Empty);
        }

        private static string Attr(XElement element, string name)
        {
            var attr = element.Attribute(name);
            return attr != null ? attr.Value : null;
        }

        private static string NormalizeTextPreview(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string compact = value.Trim().Replace("\r", " ").Replace("\n", " ");
            while (compact.Contains("  "))
            {
                compact = compact.Replace("  ", " ");
            }

            if (compact.Length > 160) compact = compact.Substring(0, 160);
            return compact;
        }

        private static bool IsTextPropertyName(string propertyName)
        {
            return string.Equals(propertyName, "text", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "innertext", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "nodevalue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasCaptionLineBreak(string value)
        {
            return (value ?? string.Empty).IndexOfAny(CaptionLineBreaks) >= 0;
        }

        private static string BuildConstantCaptionTokens(string value)
        {
            var tokens = new XElement("Tokens",
                new XElement("Token",
                    new XElement("Type", "Constant"),
                    new XElement("Data", new XCData(value ?? string.Empty))));
            return tokens.ToString(SaveOptions.DisableFormatting);
        }

        private static string ExtractConstantCaptionFromTokens(string captionExpression)
        {
            if (string.IsNullOrEmpty(captionExpression)) return string.Empty;
            try
            {
                var tokens = XElement.Parse(captionExpression);
                var data = tokens
                    .Elements("Token")
                    .Elements("Data")
                    .FirstOrDefault();
                return data?.Value ?? string.Empty;
            }
            catch
            {
                return captionExpression;
            }
        }

        private static bool IsPersistedValueMatch(string propertyName, string expected, string actual)
        {
            string normalizedExpected = expected ?? string.Empty;
            string normalizedActual = actual ?? string.Empty;

            if (string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
            {
                return true;
            }

            if ((string.Equals(propertyName, "Left", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Top", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Width", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Height", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "BorderWidth", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(normalizedExpected, out int expectedInt) &&
                int.TryParse(normalizedActual, out int actualInt) &&
                expectedInt == actualInt)
            {
                return true;
            }

            // The report SDK often serializes colors as nested "Color [ ... ]" descriptors or RGB tokens.
            if (ColorHelper.IsColorAttributeName(propertyName))
            {
                if (ColorHelper.IsColorEquivalent(normalizedExpected, normalizedActual))
                {
                    return true;
                }

                if (normalizedActual.IndexOf(normalizedExpected, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ExtractColorLeafToken(string raw)
            => ColorHelper.ExtractColorLeafToken(raw);

        private static bool TryParseColorToken(string raw, out System.Drawing.Color color)
            => ColorHelper.TryParseColor(raw, out color);



        private sealed class FindCriteria
        {
            public string PropertyName { get; set; }
            public string Query { get; set; }
        }

        private sealed class ParseResult
        {
            public XDocument Document { get; private set; }
            public string Error { get; private set; }

            public static ParseResult FromDocument(XDocument document) => new ParseResult { Document = document };
            public static ParseResult FromError(string error) => new ParseResult { Error = error };
        }

        private sealed class LayoutContextResult
        {
            public VisualSurface Surface { get; private set; }
            public dynamic WebFormPart { get; private set; }
            public ISource SourcePart { get; private set; }
            public KBObjectPart VisualPart { get; private set; }
            public string PartName { get; private set; }
            public string MemberName { get; private set; }
            public string MemberSourcePath { get; private set; }
            public bool MemberWritable { get; private set; }
            public XDocument Document { get; private set; }
            public string Error { get; private set; }

            public static LayoutContextResult FromError(string error) => new LayoutContextResult { Error = error };

            public static LayoutContextResult FromWebForm(dynamic webFormPart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.WebForm,
                    WebFormPart = webFormPart,
                    Document = document
                };
            }

            public static LayoutContextResult FromLayoutSource(ISource sourcePart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.LayoutSource,
                    PartName = "Layout",
                    SourcePart = sourcePart,
                    Document = document
                };
            }

            public static LayoutContextResult FromLayoutSource(string partName, ISource sourcePart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.LayoutSource,
                    PartName = partName,
                    SourcePart = sourcePart,
                    Document = document
                };
            }

            public static LayoutContextResult FromReport(KBObjectPart reportPart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.Report,
                    PartName = "Layout",
                    VisualPart = reportPart,
                    Document = document
                };
            }

            public static LayoutContextResult FromPartXml(string partName, KBObjectPart part, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.PartXml,
                    PartName = partName,
                    VisualPart = part,
                    Document = document
                };
            }

            public static LayoutContextResult FromPartMemberXml(string partName, KBObjectPart part, XDocument document, string memberName, string memberSourcePath, bool memberWritable)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.PartMemberXml,
                    PartName = partName,
                    VisualPart = part,
                    MemberName = memberName,
                    MemberSourcePath = memberSourcePath,
                    MemberWritable = memberWritable,
                    Document = document
                };
            }
        }

        private sealed class MemberXmlCandidate
        {
            public string Xml { get; set; }
            public XDocument Document { get; set; }
            public int Score { get; set; }
            public PropertyInfo Property { get; set; }
            public MethodInfo GetterMethod { get; set; }
            public string MemberName { get; set; }
            public string SourcePath { get; set; }
            public int Depth { get; set; }
            public string MemberKind { get; set; }
            public bool MemberWritable { get; set; }
        }

        private sealed class ReferenceObjectComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceObjectComparer Instance = new ReferenceObjectComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private enum VisualSurface
        {
            Any,
            Report,
            WebForm,
            LayoutSource,
            PartXml,
            PartMemberXml
        }
    }
}
