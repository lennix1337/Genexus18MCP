using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Issue #770: the Conditions (<c>ControlWhere</c>) of a layout-form control, such as a Dynamic
    /// Combo Box, read and replaced without hand-editing the WebForm XML.
    ///
    /// The expression is stored in the control's <c>PATTERN_ELEMENT_CUSTOM_PROPERTIES</c> (the SDK
    /// parses it into a token list on save) and, when the control also carries it in
    /// <c>WebUserControlProperties</c>, there too, so the two carriers never disagree
    /// (<see cref="ControlConditionCarriers"/>). The prospective form is checked by the SDK layout
    /// validator on a detached copy, written through the regular visual-write pipeline (version
    /// check, strict verification, rollback) and read back from a fresh instance of the object; a
    /// failed check restores the whole object and proves it with a fresh read.
    /// </summary>
    internal sealed class ControlConditionsService
    {
        internal const string PropertyName = "ControlWhere";
        internal const string Alias = "Conditions";
        private const int MaxExpressionLength = 4000;

        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly ControlParametersService _controls;

        internal ControlConditionsService(ObjectService objectService, WriteService writeService, ControlParametersService controls)
        {
            _objectService = objectService;
            _writeService = writeService;
            _controls = controls;
        }

        internal static bool IsConditionsProperty(string propertyName)
        {
            string name = propertyName?.Trim();
            return string.Equals(name, PropertyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, Alias, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- read

        /// <summary>True when <paramref name="control"/> is a control of the object's layout form (a legacy form keeps the generic read).</summary>
        internal bool IsLayoutControl(string target, string control, string typeFilter)
        {
            if (string.IsNullOrWhiteSpace(control)) return false;
            try { return _controls.ResolveControl(target, control, typeFilter, requireParameterEngine: false, out _) != null; }
            catch (Exception ex)
            {
                Logger.Debug("[ControlConditions] layout lookup failed: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        public string Get(string target, string control, string typeFilter)
        {
            try
            {
                var context = _controls.ResolveControl(target, control, typeFilter, requireParameterEngine: false, out string error);
                if (error != null) return error;
                bool applicable = ControlParametersService.HasProperty(context.Control, PropertyName);
                var result = new JObject
                {
                    ["property"] = PropertyName,
                    ["control"] = ControlParametersService.DescribeControl(context.Control),
                    ["applicable"] = applicable,
                    ["conditions"] = ReadExpression(context.Element) ?? string.Empty,
                    ["versionToken"] = context.CurrentVersion
                };
                return McpResponse.Ok(target: target, code: "ControlConditionsRead", result: result);
            }
            catch (Exception ex)
            {
                Logger.Error("[ControlConditions] get failed: " + ex);
                return McpResponse.Err(code: "ControlConditionsReadFailed", target: target,
                    message: "The control's Conditions could not be read: " + (ex.InnerException ?? ex).Message);
            }
        }

        // --------------------------------------------------------------- write

        public string Set(string target, string control, string value, string expectedVersion,
            bool dryRun, bool rollbackOnFailure, string typeFilter)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(control))
                    return McpResponse.Err(code: "MissingControl", target: target,
                        message: "The control that owns the Conditions is required.",
                        hint: "Pass control as the attribute name, att:<id>, the control name or its GUID. genexus_inspect include=controls lists them.");
                if (!rollbackOnFailure && !dryRun)
                    return McpResponse.Err(code: "RollbackRequired", target: target,
                        message: "Writing a control's Conditions requires rollbackOnFailure=true to protect the complete object.");
                if (!dryRun && string.IsNullOrWhiteSpace(expectedVersion))
                    return McpResponse.Err(code: "ExpectedVersionRequired", target: target,
                        message: "Writing a control's Conditions requires expectedVersion, the versionToken of a prior read of this control.",
                        hint: "Read the Conditions with genexus_properties action=get control=<control> propertyName=" + PropertyName + " and pass its versionToken.");

                string requestError = ValidateRequest(value, out string requested);
                if (requestError != null)
                    return McpResponse.Err(code: "InvalidConditions", target: target, message: requestError,
                        extra: new JObject { ["persisted"] = false, ["savePathExercised"] = false });

                var context = _controls.ResolveControl(target, control, typeFilter, requireParameterEngine: false, out string error);
                if (error != null) return error;

                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !ControlParametersService.VersionMatches(expectedVersion, context.CurrentVersion, () => WriteService.ComputeVersionToken(context.Object)))
                    return McpResponse.Err(code: "VersionConflict", target: target,
                        message: "The object changed after the Conditions were read; nothing was written.",
                        hint: "Read the control's Conditions again, reapply the change and retry with the new versionToken.",
                        extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = context.CurrentVersion,
                            ["persisted"] = false,
                            ["savePathExercised"] = false
                        });

                if (!ControlParametersService.HasProperty(context.Control, PropertyName))
                    return McpResponse.Err(code: "PropertyNotApplicable", target: target,
                        message: "'" + PropertyName + "' does not apply to this control.",
                        hint: "Conditions apply to a Dynamic Combo Box or Dynamic List Box. Configure the control in the GeneXus IDE first.",
                        extra: new JObject { ["control"] = ControlParametersService.DescribeControl(context.Control), ["persisted"] = false });

                string before = ReadExpression(context.Element) ?? string.Empty;
                var result = new JObject
                {
                    ["property"] = PropertyName,
                    ["control"] = ControlParametersService.DescribeControl(context.Control),
                    ["before"] = before,
                    ["conditions"] = requested,
                    ["currentVersion"] = context.CurrentVersion
                };

                if (string.Equals(before, requested, StringComparison.Ordinal))
                {
                    result["persisted"] = false;
                    result["verified"] = true;
                    result["details"] = "The control already has these Conditions; nothing to write.";
                    return McpResponse.Ok(target: target, code: "WriteNoChange", result: result);
                }

                if (!TryBuildForm(context, requested, out string newXml, out string buildFailure))
                    return McpResponse.Err(code: "ControlConditionsBuildFailed", target: target, message: buildFailure,
                        extra: new JObject { ["persisted"] = false, ["savePathExercised"] = false });

                var diagnostics = ControlParametersService.ValidateProspectiveLayout(context.Object, newXml);
                if (diagnostics.Count > 0)
                    return McpResponse.Err(code: "WebFormValidationFailed", target: target,
                        message: "The SDK layout validator found errors in the form that would result: "
                            + string.Join("; ", diagnostics.Take(8).Select(d => d.Message)) + "; nothing was written.",
                        extra: new JObject { ["persisted"] = false, ["savePathExercised"] = false });
                result["layoutValidated"] = true;

                if (dryRun)
                {
                    result["persisted"] = false;
                    result["verified"] = true;
                    result["savePathExercised"] = false;
                    result["details"] = "Dry-run: the resulting form validates; nothing was saved.";
                    return McpResponse.Ok(target: target, code: "ControlConditionsDryRun", result: result);
                }

                return Apply(target, control, typeFilter, context, newXml, requested, result);
            }
            catch (Exception ex)
            {
                Logger.Error("[ControlConditions] set failed: " + ex);
                return McpResponse.Err(code: "ControlConditionsWriteFailed", target: target,
                    message: "The control's Conditions were not written: " + (ex.InnerException ?? ex).Message,
                    hint: "Read the control's Conditions again to see what is persisted before retrying.",
                    extra: new JObject { ["persistenceStateKnown"] = false });
            }
        }

        private string Apply(string target, string control, string typeFilter, ControlParametersService.ControlContext before,
            string newXml, string requested, JObject result)
        {
            ObjectMoveSnapshot snapshot;
            try { snapshot = ObjectMoveSnapshot.Capture(before.Object); }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ControlConditionsSnapshotFailed", target: target,
                    message: "The complete object could not be captured before editing, so nothing was written: " + ex.Message,
                    extra: new JObject { ["persisted"] = false });
            }

            string raw = _writeService.WriteObject(
                target, "WebForm", newXml, typeFilter,
                autoValidate: true, preferFastSourceSave: false, autoInjectVariables: false, dryRun: false,
                explicitBase64: false, strictVerify: true, rollbackOnFailure: true, forceWrite: false,
                baseVersion: before.CurrentVersion);

            if (!ControlParametersService.TryParseWriteResponse(raw, out JObject write, out string unreadable))
            {
                Logger.Error("[ControlConditions] the write pipeline answered something that is not JSON: " + unreadable);
                return McpResponse.Err(code: "ControlConditionsWriteFailed", target: target,
                    message: "The write pipeline's answer could not be read, so the Conditions may or may not be persisted: " + unreadable,
                    hint: "Read the control's Conditions again to see what is persisted before retrying.",
                    extra: new JObject { ["persistenceStateKnown"] = false });
            }

            bool pipelineOk = string.Equals(write["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(write["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
            if (!pipelineOk)
            {
                write["property"] = PropertyName;
                return write.ToString(Newtonsoft.Json.Formatting.None);
            }

            var fresh = _controls.ResolveControl(target, control, typeFilter, requireParameterEngine: false, out string readError);
            if (fresh == null)
                return RollBack(target, typeFilter, before, snapshot,
                    "The saved Conditions could not be read back from a fresh instance of the object: " + ControlParametersService.ReadErrorMessage(readError));
            var freshObject = _objectService.FindObjectFreshByIdentity(before.Object);
            if (freshObject != null) fresh.Object = freshObject;

            string persisted = ReadExpression(fresh.Element) ?? string.Empty;
            if (!string.Equals(persisted, requested, StringComparison.Ordinal))
                return RollBack(target, typeFilter, before, snapshot,
                    "The saved Conditions differ from the request (persisted: " + persisted + ").");

            if (freshObject != null)
            {
                var otherParts = snapshot.CompareParts(freshObject, "WebForm");
                if (!otherParts.Equal)
                    return RollBack(target, typeFilter, before, snapshot,
                        "The save changed another part of the object (" + string.Join(", ", otherParts.ChangedPartKeys ?? new string[0]) + ").");
            }

            string layoutDifference = DescribeLayoutDifference(before.CurrentXml, fresh.CurrentXml);
            if (layoutDifference != null)
                return RollBack(target, typeFilter, before, snapshot,
                    "The save changed something other than the control's Conditions: " + layoutDifference);

            PropertyService.InvalidatePropertyCache(before.Object);
            if (freshObject != null) PropertyService.InvalidatePropertyCache(freshObject);

            result["persisted"] = true;
            result["sdkSaveCompleted"] = true;
            result["reReadConfirmed"] = true;
            result["verified"] = true;
            result["conditions"] = persisted;
            result["versionToken"] = fresh.CurrentVersion;
            result["previousVersion"] = before.CurrentVersion;
            result["layoutPreserved"] = true;
            result["otherPartsPreserved"] = true;
            result["details"] = "Conditions updated; read back from a fresh instance and compared with the request.";
            return JObject.Parse(McpResponse.Ok(target: target, code: "ControlConditionsApplied", result: result))
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        private string RollBack(string target, string typeFilter, ControlParametersService.ControlContext before,
            ObjectMoveSnapshot snapshot, string reason)
        {
            bool attempted = false, restored = false, verified = false;
            string rollbackNote = null;
            try
            {
                attempted = true;
                string raw = _writeService.WriteObject(
                    target, "WebForm", before.CurrentXml, typeFilter,
                    autoValidate: true, preferFastSourceSave: false, autoInjectVariables: false, dryRun: false,
                    explicitBase64: false, strictVerify: true, rollbackOnFailure: false, forceWrite: false);
                var restoreResponse = JObject.Parse(raw);
                restored = string.Equals(restoreResponse["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(restoreResponse["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
                if (!restored) rollbackNote = restoreResponse["error"]?["message"]?.ToString() ?? restoreResponse["message"]?.ToString();

                var currentJson = JObject.Parse(_objectService.ReadObjectSourceForVerification(target, "WebForm", typeFilter));
                string currentXml = currentJson["source"]?.ToString();
                var freshObject = _objectService.FindObjectFreshByIdentity(before.Object);
                verified = restored && currentXml != null
                    && ControlParametersService.DescribeLayoutDifference(before.CurrentXml, currentXml, includeTarget: true) == null
                    && freshObject != null && snapshot.Compare(freshObject).Equal;
            }
            catch (Exception ex)
            {
                rollbackNote = ex.Message;
            }

            PropertyService.InvalidatePropertyCache(before.Object);

            return McpResponse.Err(
                code: "ControlConditionsVerificationFailed", target: target,
                message: reason + (verified
                    ? " The complete object was restored and verified by a fresh read."
                    : " The restore could not be verified: read the control again before deciding on another edit."),
                hint: verified ? "Nothing changed; correct the request and retry." : "Do not retry automatically; read the control's Conditions and the WebForm first.",
                extra: new JObject
                {
                    ["persisted"] = !verified,
                    ["persistenceStateKnown"] = verified,
                    ["rollback"] = new JObject
                    {
                        ["attempted"] = attempted,
                        ["restored"] = restored,
                        ["verified"] = verified,
                        ["note"] = rollbackNote
                    }
                });
        }

        // ------------------------------------------------------------- helpers

        /// <summary>The expression to store: trimmed text, or an error. An empty value clears the Conditions.</summary>
        internal static string ValidateRequest(string value, out string expression)
        {
            expression = ControlExpressionText.Normalize(value ?? string.Empty);
            if (expression.Length > MaxExpressionLength)
                return "The Conditions are " + expression.Length + " characters long; the limit is " + MaxExpressionLength + ".";
            return null;
        }

        /// <summary>The normalized Conditions an element stores in its custom properties; null when it has none.</summary>
        internal static string ReadExpression(XElement element)
            => ControlConditionCarriers.TryRead(element, CustomPropertiesPayload.AttributeName, PropertyName, out string value)
                ? ControlExpressionText.Of(value)
                : null;

        private static bool TryBuildForm(ControlParametersService.ControlContext context, string expression, out string xml, out string failure)
        {
            xml = null;
            if (!SetExpression(context.Element, expression, out failure)) return false;
            try { xml = context.Document.ToString(SaveOptions.DisableFormatting); }
            catch (Exception ex)
            {
                failure = "The form with the new Conditions could not be built: " + ex.Message;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Stores <paramref name="expression"/> as the Conditions of <paramref name="element"/>: in its
        /// custom properties, and in <c>WebUserControlProperties</c> when that bag already carries the
        /// property, so the two carriers keep spelling one expression.
        /// </summary>
        internal static bool SetExpression(XElement element, string expression, out string failure)
        {
            failure = null;
            if (!SetCarrier(element, CustomPropertiesPayload.AttributeName, expression, createWhenMissing: true, out failure)) return false;
            return SetCarrier(element, ControlConditionCarriers.WebUserControlPropertiesAttribute, expression, createWhenMissing: false, out failure);
        }

        private static bool SetCarrier(XElement element, string carrier, string expression, bool createWhenMissing, out string failure)
        {
            failure = null;
            var attribute = element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, carrier, StringComparison.Ordinal));
            if (attribute == null && !createWhenMissing) return true;
            if (!CustomPropertiesPayload.TryParse(attribute?.Value, out var payload) || payload == null)
            {
                failure = "The control's " + carrier + " could not be parsed; it is left untouched.";
                return false;
            }
            if (attribute != null && !createWhenMissing && !payload.TryGet(PropertyName, out _)) return true;
            string value = payload.ToAttributeValue(PropertyName, expression);
            if (attribute == null) element.SetAttributeValue(carrier, value);
            else attribute.Value = value;
            return true;
        }

        /// <summary>Everything but the Conditions of the form must be unchanged by the save.</summary>
        internal static string DescribeLayoutDifference(string beforeXml, string afterXml)
        {
            if (string.IsNullOrWhiteSpace(afterXml)) return "the saved form could not be read back.";
            return XmlEquivalence.AreEquivalent(MaskConditions(beforeXml), MaskConditions(afterXml), XmlEquivalenceOptions.Layout, out string diff)
                ? null : diff;
        }

        /// <summary>The form with the Conditions of every control replaced by a fixed marker in both carriers.</summary>
        internal static string MaskConditions(string xml)
        {
            try
            {
                var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                foreach (var element in document.Descendants())
                {
                    foreach (string carrier in new[] { CustomPropertiesPayload.AttributeName, ControlConditionCarriers.WebUserControlPropertiesAttribute })
                    {
                        var attribute = element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, carrier, StringComparison.Ordinal));
                        if (attribute == null) continue;
                        if (!CustomPropertiesPayload.TryParse(attribute.Value, out var payload) || payload == null) continue;
                        // Masked whether or not the property is present: the first Conditions a control gets
                        // appear only in the saved form, and a one-sided mask reported them as a layout change.
                        attribute.Value = payload.ToAttributeValue(PropertyName, "(masked)");
                    }
                }
                return document.ToString(SaveOptions.DisableFormatting);
            }
            catch (XmlException)
            {
                // Unparseable text is left unmasked: the comparison that follows reports the parse error.
                return xml;
            }
        }
    }
}
