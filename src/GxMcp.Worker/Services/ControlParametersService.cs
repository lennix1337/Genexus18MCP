using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// The parameters of a layout-form control that calls a Data Provider (a Dynamic Combo Box
    /// or Dynamic List Box): read them as an ordered list of expressions, and replace them.
    ///
    /// The stored value is a token list (<c>DataProviderParameters</c>, an
    /// <c>ObjectParametersType</c>) inside the control's <c>PATTERN_ELEMENT_CUSTOM_PROPERTIES</c>.
    /// It is never composed here: the caller's expressions go through the SDK's own
    /// parameter-list language, which validates every name against the object's model and
    /// produces the token list the IDE would store. A reference the model cannot resolve is a
    /// diagnostic in the dry run, before anything is written; the write then goes through the
    /// regular visual-write pipeline (version check, transaction, post-save verification,
    /// verified rollback) and is checked again by a fresh read of the control itself.
    /// </summary>
    internal sealed class ControlParametersService
    {
        internal const string PropertyName = "DataProviderParameters";
        private const int MaxExpressions = 64;
        private const int MaxExpressionLength = 2000;

        /// <summary>
        /// Test seam: when set and it returns a reason, the post-write check fails with that reason
        /// so the restore path can be exercised against a real object. It is never set by product
        /// code; a test (or a local probe) arms it in-process.
        /// </summary>
        internal static Func<string> InjectedVerificationFailure { get; set; }

        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;

        internal ControlParametersService(ObjectService objectService, WriteService writeService)
        {
            _objectService = objectService;
            _writeService = writeService;
        }

        internal static bool IsParametersProperty(string propertyName)
            => string.Equals(propertyName?.Trim(), PropertyName, StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- read

        public string Get(string target, string control, string typeFilter)
        {
            try
            {
                var context = Resolve(target, control, typeFilter, out string error);
                if (error != null) return error;

                var result = new JObject
                {
                    ["property"] = PropertyName,
                    ["control"] = DescribeControl(context.Control),
                    ["controlType"] = context.ReadStored("ControlType"),
                    ["dataSourceFrom"] = context.ReadStored("DataSourceFrom"),
                    ["dataProvider"] = DescribeDataProvider(context),
                    ["applicable"] = context.Applicable,
                    ["expressions"] = new JArray(context.BeforeExpressions),
                    ["parameterCount"] = context.BeforeExpressions.Count,
                    ["text"] = ParameterListText.Join(context.BeforeExpressions),
                    ["versionToken"] = context.CurrentVersion
                };
                if (!context.Applicable)
                    result["hint"] = NotApplicableHint(context);
                return McpResponse.Ok(target: target, code: "ControlParametersRead", result: result);
            }
            catch (Exception ex)
            {
                Logger.Error("[ControlParameters] get failed: " + ex);
                return McpResponse.Err(code: "ControlParametersReadFailed", target: target,
                    message: "The control's parameters could not be read: " + (ex.InnerException ?? ex).Message);
            }
        }

        // --------------------------------------------------------------- write

        public string Set(string target, string control, JArray expressions, string expectedVersion,
            bool dryRun, bool rollbackOnFailure, string typeFilter)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(control))
                    return McpResponse.Err(code: "MissingControl", target: target,
                        message: "The control that owns the parameters is required.",
                        hint: "Pass control as the attribute name, att:<id>, the control name or its GUID. genexus_inspect include=controls lists them.");
                if (!rollbackOnFailure && !dryRun)
                    return McpResponse.Err(code: "RollbackRequired", target: target,
                        message: "Writing a control's parameters requires rollbackOnFailure=true to protect the complete object.");
                if (!dryRun && string.IsNullOrWhiteSpace(expectedVersion))
                    return McpResponse.Err(code: "ExpectedVersionRequired", target: target,
                        message: "Writing a control's parameters requires expectedVersion, the versionToken of a prior read of this control.",
                        hint: "Read the parameters with genexus_properties action=get control=<control> propertyName=" + PropertyName + " and pass its versionToken.");

                var requested = NormalizeRequest(expressions, target, out string requestError);
                if (requestError != null) return requestError;

                var context = Resolve(target, control, typeFilter, out string error);
                if (error != null) return error;

                if (!string.IsNullOrWhiteSpace(expectedVersion) && !VersionMatches(expectedVersion, context))
                    return McpResponse.Err(code: "VersionConflict", target: target,
                        message: "The object changed after the parameters were read; nothing was written.",
                        hint: "Read the control's parameters again, reapply the change on that list and retry with the new versionToken.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_properties",
                            new JObject { ["action"] = "get", ["name"] = target, ["control"] = control, ["propertyName"] = PropertyName },
                            "Reads the current parameters and the new versionToken.")),
                        extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = context.CurrentVersion,
                            ["persisted"] = false,
                            ["savePathExercised"] = false
                        });

                if (!context.Applicable)
                    return McpResponse.Err(code: "PropertyNotApplicable", target: target,
                        message: "'" + PropertyName + "' does not apply to this control.",
                        hint: NotApplicableHint(context),
                        extra: new JObject { ["control"] = DescribeControl(context.Control), ["persisted"] = false });

                // 1. references: att:<id> becomes the attribute name; an id the KB does not know is refused.
                var resolved = ParameterListText.ResolveAttributeReferences(
                    requested, id => ResolveAttributeName(context.Object, id), out var unresolved);
                if (unresolved.Count > 0)
                    return ReferenceError(target, context, unresolved);

                // 2. variables: the SDK tolerates a &Name the object never declared; the generator does not.
                var declared = DeclaredVariableNames(context.Object);
                var undeclared = FindUndeclaredVariables(requested, resolved, declared);
                if (undeclared.Count > 0)
                    return ValidationError(target, context, undeclared, "UnknownVariableReference",
                        "expressions[" + undeclared[0].Index + "] uses " + undeclared[0].Expression.Trim() + ", but " + (undeclared[0].Message ?? "that variable is not declared") + "; nothing was written.");

                // 3. the SDK language engine validates each expression, then the list.
                var diagnostics = new List<ParameterListDiagnostic>();
                for (int i = 0; i < resolved.Count; i++)
                    diagnostics.AddRange(ParameterListEngine.Validate(context.Object, resolved[i], i, requested[i]));
                if (diagnostics.Count == 0)
                    diagnostics.AddRange(ParameterListEngine.Validate(context.Object, ParameterListText.Join(resolved)));
                if (diagnostics.Count > 0)
                    return ValidationError(target, context, diagnostics);

                string joined = ParameterListText.Join(resolved);
                string newTokens = ParameterListEngine.ToStorage(context.Object, joined, out string tokenFailure);
                if (newTokens == null)
                    return McpResponse.Err(code: "ParameterTokenizationFailed", target: target, message: tokenFailure,
                        extra: new JObject { ["persisted"] = false, ["savePathExercised"] = false });

                var changes = BuildChanges(context.BeforeExpressions, resolved);
                var result = new JObject
                {
                    ["property"] = PropertyName,
                    ["control"] = DescribeControl(context.Control),
                    ["before"] = new JObject
                    {
                        ["expressions"] = new JArray(context.BeforeExpressions),
                        ["parameterCount"] = context.BeforeExpressions.Count
                    },
                    ["expressions"] = new JArray(resolved),
                    ["parameterCount"] = resolved.Count,
                    ["changes"] = changes,
                    ["validation"] = new JObject
                    {
                        ["engine"] = "sdk-parameter-list",
                        ["expressionsChecked"] = resolved.Count,
                        ["diagnostics"] = new JArray()
                    },
                    ["currentVersion"] = context.CurrentVersion
                };

                if (SameStoredTokens(context.StoredTokens, newTokens))
                {
                    result["persisted"] = false;
                    result["verified"] = true;
                    result["details"] = "The control already has these parameters; nothing to write.";
                    return McpResponse.Ok(target: target, code: "WriteNoChange", result: result);
                }

                string newXml = context.BuildXmlWith(newTokens, out string buildFailure);
                if (newXml == null)
                    return McpResponse.Err(code: "ControlParametersBuildFailed", target: target, message: buildFailure,
                        extra: new JObject { ["persisted"] = false, ["savePathExercised"] = false });

                // 4. the whole prospective form goes through the SDK's layout validator on a detached copy.
                var layoutDiagnostics = ValidateProspectiveLayout(context.Object, newXml);
                if (layoutDiagnostics.Count > 0)
                    return ValidationError(target, context, layoutDiagnostics, "WebFormValidationFailed",
                        "The SDK layout validator found errors in the form that would result.");
                ((JObject)result["validation"])["layoutValidated"] = true;

                if (dryRun)
                {
                    result["persisted"] = false;
                    result["verified"] = true;
                    result["savePathExercised"] = false;
                    result["details"] = "Dry-run: every expression was validated by the SDK and the resulting form validates; nothing was saved.";
                    return McpResponse.Ok(target: target, code: "ControlParametersDryRun", result: result);
                }

                return Apply(target, control, typeFilter, context, newXml, newTokens, resolved, changes, result);
            }
            catch (Exception ex)
            {
                Logger.Error("[ControlParameters] set failed: " + ex);
                return McpResponse.Err(code: "ControlParametersWriteFailed", target: target,
                    message: "The control's parameters were not written: " + (ex.InnerException ?? ex).Message,
                    hint: "Read the control's parameters again to see what is persisted before retrying.",
                    extra: new JObject { ["persistenceStateKnown"] = false });
            }
        }

        private string Apply(string target, string control, string typeFilter, ControlContext before, string newXml,
            string newTokens, List<string> expected, JArray changes, JObject result)
        {
            ObjectMoveSnapshot snapshot;
            try { snapshot = ObjectMoveSnapshot.Capture(before.Object); }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ControlParametersSnapshotFailed", target: target,
                    message: "The complete object could not be captured before editing, so nothing was written: " + ex.Message,
                    extra: new JObject { ["persisted"] = false });
            }

            string raw = _writeService.WriteObject(
                target, "WebForm", newXml, typeFilter,
                autoValidate: true, preferFastSourceSave: false, autoInjectVariables: false, dryRun: false,
                explicitBase64: false, strictVerify: true, rollbackOnFailure: true, forceWrite: false,
                baseVersion: before.CurrentVersion);

            if (!TryParseWriteResponse(raw, out JObject write, out string unreadable))
            {
                Logger.Error("[ControlParameters] the write pipeline answered something that is not JSON: " + unreadable);
                return McpResponse.Err(code: "ControlParametersWriteFailed", target: target,
                    message: "The write pipeline's answer could not be read, so the control's parameters may or may not be persisted: " + unreadable,
                    hint: "Read the control's parameters again to see what is persisted before retrying.",
                    extra: new JObject { ["persistenceStateKnown"] = false });
            }

            bool pipelineOk = string.Equals(write["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(write["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
            if (!pipelineOk)
            {
                write["property"] = PropertyName;
                write["parameterEdit"] = new JObject
                {
                    ["control"] = DescribeControl(before.Control),
                    ["requestedExpressions"] = new JArray(expected),
                    ["beforeExpressions"] = new JArray(before.BeforeExpressions)
                };
                return write.ToString(Newtonsoft.Json.Formatting.None);
            }

            string injected = InjectedVerificationFailure?.Invoke();
            if (injected != null) return RollBack(target, typeFilter, before, snapshot, injected);

            // The pipeline's own check compares the form; this one reads the control back from a
            // fresh instance of the object and compares the parameter list itself.
            var fresh = ReadBack(target, control, typeFilter, out string readBackFailure);
            if (fresh == null)
                return RollBack(target, typeFilter, before, snapshot,
                    "The saved parameters could not be read back from a fresh instance of the object" + (readBackFailure == null ? "." : ": " + readBackFailure));

            // The token list is the engine's deterministic output for the requested text, so the
            // persisted tokens are compared first; the rendered expressions are the fallback for an
            // SDK that respells the token XML on save.
            if (!SameStoredTokens(fresh.StoredTokens, newTokens) && !SameExpressions(fresh.BeforeExpressions, expected))
                return RollBack(target, typeFilter, before, snapshot,
                    "The saved parameters differ from the requested list (persisted: " + ParameterListText.Join(fresh.BeforeExpressions) + ").");

            var freshObject = fresh.Object;
            var otherParts = snapshot.CompareParts(freshObject, "WebForm");
            if (!otherParts.Equal)
                return RollBack(target, typeFilter, before, snapshot,
                    "The save changed another part of the object (" + string.Join(", ", otherParts.ChangedPartKeys ?? new string[0]) + ").");

            string layoutDifference = DescribeLayoutDifference(before.CurrentXml, fresh.CurrentXml);
            if (layoutDifference != null)
                return RollBack(target, typeFilter, before, snapshot,
                    "The save changed something other than the control's parameters: " + layoutDifference);

            PropertyService.InvalidatePropertyCache(before.Object);
            PropertyService.InvalidatePropertyCache(freshObject);

            result["persisted"] = true;
            result["sdkSaveCompleted"] = true;
            result["reReadConfirmed"] = true;
            result["verified"] = true;
            result["expressions"] = new JArray(fresh.BeforeExpressions);
            result["parameterCount"] = fresh.BeforeExpressions.Count;
            result["versionToken"] = fresh.CurrentVersion;
            result["previousVersion"] = before.CurrentVersion;
            result["layoutPreserved"] = true;
            result["otherPartsPreserved"] = true;
            result["preservedParts"] = snapshot.PreservedParts;
            result["details"] = "Parameters updated; read back from a fresh instance and compared with the request.";
            var ok = JObject.Parse(McpResponse.Ok(target: target, code: "ControlParametersApplied", result: result));
            return ok.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>The write pipeline's envelope as JSON; false, with the parse failure, when it is not JSON.</summary>
        internal static bool TryParseWriteResponse(string raw, out JObject write, out string failure)
        {
            write = null;
            failure = null;
            try { write = JObject.Parse(raw ?? string.Empty); }
            catch (JsonReaderException ex) { failure = ex.Message; }
            return write != null;
        }

        // ------------------------------------------------------------ rollback

        // The pipeline accepted the write but the control-level check did not: put the form back
        // as it was, then prove with a fresh read that it IS back. Whatever cannot be proven is
        // said so; "rolled back" is never asserted without that read.
        private string RollBack(string target, string typeFilter, ControlContext before, ObjectMoveSnapshot snapshot, string reason)
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

                var current = _objectService.ReadObjectSourceForVerification(target, "WebForm", typeFilter);
                var currentJson = JObject.Parse(current);
                string currentXml = currentJson["source"]?.ToString();
                var fresh = _objectService.FindObjectFreshByIdentity(before.Object);
                verified = restored && currentXml != null
                    && DescribeLayoutDifference(before.CurrentXml, currentXml, includeTarget: true) == null
                    && fresh != null && snapshot.Compare(fresh).Equal;
            }
            catch (Exception ex)
            {
                rollbackNote = ex.Message;
            }

            PropertyService.InvalidatePropertyCache(before.Object);

            return McpResponse.Err(
                code: "ControlParametersVerificationFailed", target: target,
                message: reason + (verified
                    ? " The complete object was restored and verified by a fresh read."
                    : " The restore could not be verified: read the control again before deciding on another edit."),
                hint: verified ? "Nothing changed; correct the request and retry." : "Do not retry automatically; read the control's parameters and the WebForm first.",
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

        // -------------------------------------------------------------- context

        internal sealed class ControlContext
        {
            public KBObject Object;
            public LayoutControl Control;
            public XDocument Document;
            public XElement Element;
            public string CurrentXml;
            public string CurrentVersion;
            public CustomPropertiesPayload Payload;
            public string StoredTokens;
            public List<string> BeforeExpressions = new List<string>();
            public bool Applicable;

            public string ReadStored(string name)
            {
                if (Payload == null) return null;
                return Payload.TryGet(name, out string value) && !string.IsNullOrEmpty(value) ? value : null;
            }

            public string BuildXmlWith(string tokens, out string failure) => BuildXmlWith(PropertyName, tokens, out failure);

            public string BuildXmlWith(string property, string tokens, out string failure)
            {
                failure = null;
                try
                {
                    var attribute = Element.Attributes().FirstOrDefault(a =>
                        string.Equals(a.Name.LocalName, CustomPropertiesPayload.AttributeName, StringComparison.Ordinal));
                    string value = (Payload ?? new CustomPropertiesPayload()).ToAttributeValue(property, tokens);
                    if (attribute == null) Element.SetAttributeValue(CustomPropertiesPayload.AttributeName, value);
                    else attribute.Value = value;
                    return Document.ToString(SaveOptions.DisableFormatting);
                }
                catch (Exception ex)
                {
                    failure = "The form with the new parameters could not be built: " + ex.Message;
                    return null;
                }
            }
        }

        private ControlContext Resolve(string target, string control, string typeFilter, out string error)
        {
            var context = ResolveControl(target, control, typeFilter, requireParameterEngine: true, out error);
            if (context == null) return null;
            context.Applicable = HasProperty(context.Control, PropertyName);
            if (context.Payload.TryGet(PropertyName, out string tokens)) context.StoredTokens = tokens;

            if (!string.IsNullOrWhiteSpace(context.StoredTokens))
            {
                string text = ParameterListEngine.ToText(context.Object, context.StoredTokens, out string textFailure);
                if (text == null)
                {
                    error = McpResponse.Err(code: "CurrentParametersUnreadable", target: target,
                        message: textFailure, hint: "The stored list is left untouched; nothing can be edited safely until it renders.");
                    return null;
                }
                context.BeforeExpressions = ParameterListText.Split(text);
            }
            return context;
        }

        /// <summary>
        /// The control of a layout form with its element, custom-property payload and the form's
        /// version, read from a fresh instance. Shared by every typed control-property edit.
        /// </summary>
        internal ControlContext ResolveControl(string target, string control, string typeFilter, bool requireParameterEngine, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(control))
            {
                error = McpResponse.Err(code: "MissingControl", target: target,
                    message: "The control that owns the parameters is required.",
                    hint: "Pass control as the attribute name, att:<id>, the control name or its GUID.");
                return null;
            }

            var obj = _objectService.FindObject(target, typeFilter);
            if (obj == null)
            {
                error = McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", target: target,
                    hint: "Check the target name and that the KB is open.",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")));
                return null;
            }

            // A fresh, complete read: the text the version token covers is the text everything
            // below is computed from.
            JObject read;
            try { read = JObject.Parse(_objectService.ReadObjectSourceForVerification(target, "WebForm", typeFilter)); }
            catch (Exception ex)
            {
                error = McpResponse.Err(code: "FreshReadUnavailable", target: target,
                    message: "The WebForm could not be read from a fresh instance: " + ex.Message);
                return null;
            }
            if (string.Equals(read["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                error = read.ToString(Newtonsoft.Json.Formatting.None);
                return null;
            }
            string xml = read["source"]?.ToString();
            string version = read["versionToken"]?.ToString();
            if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(version))
            {
                error = McpResponse.Err(code: "WebFormUnreadable", target: target,
                    message: "The WebForm part is empty or its version could not be read.",
                    hint: "Only objects with a layout-type WebForm (GxMultiForm with <Form type=\"layout\">) have these controls.");
                return null;
            }

            var domDocument = new XmlDocument();
            try { domDocument.LoadXml(xml); }
            catch (Exception ex)
            {
                error = McpResponse.Err(code: "WebFormUnreadable", target: target, message: "The WebForm XML could not be parsed: " + ex.Message);
                return null;
            }
            if (!LayoutFormModel.IsLayoutForm(domDocument))
            {
                error = McpResponse.Err(code: "NotALayoutForm", target: target,
                    message: "The WebForm is not a layout form (<Form type=\"layout\">); there is no typed control model for it.",
                    hint: "Legacy HTML forms keep control properties in genexus_layout.");
                return null;
            }

            string engineReason = null;
            if (requireParameterEngine && !ParameterListEngine.IsAvailable(out engineReason))
            {
                error = McpResponse.Err(code: "LanguageEngineUnavailable", target: target, message: engineReason);
                return null;
            }

            var panel = LayoutFormModel.TryLoad(obj, domDocument, out string loadFailure);
            if (panel == null)
            {
                error = McpResponse.Err(code: "LayoutModelUnavailable", target: target, message: loadFailure);
                return null;
            }

            var matches = LayoutFormModel.Find(panel, control);
            if (matches.Count == 0)
            {
                error = McpResponse.Err(code: "ControlNotFound", target: target,
                    message: "Control '" + control + "' not found in the layout form of " + obj.Name + ".",
                    hint: "Use genexus_inspect include=controls: pass the attribute name, att:<id>, the control name or the control GUID.",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target, ["include"] = new JArray("controls") }, "Lists the controls of this object's layout form.")),
                    extra: new JObject { ["candidates"] = DescribeCandidates(panel) });
                return null;
            }
            if (matches.Count > 1)
            {
                error = McpResponse.Err(code: "AmbiguousControl", target: target,
                    message: "'" + control + "' names " + matches.Count + " controls of the layout form.",
                    hint: "Address one of them by its GUID id or by a unique control name.",
                    extra: new JObject { ["matches"] = new JArray(matches.Select(DescribeControl)) });
                return null;
            }

            var context = new ControlContext
            {
                Object = obj,
                Control = matches[0],
                CurrentXml = xml,
                CurrentVersion = version
            };
            try { context.Document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
            catch (Exception ex)
            {
                error = McpResponse.Err(code: "WebFormUnreadable", target: target, message: "The WebForm XML could not be parsed: " + ex.Message);
                return null;
            }

            context.Element = FindElement(context.Document, context.Control, out string elementProblem);
            if (context.Element == null)
            {
                error = McpResponse.Err(code: "ControlNotAddressable", target: target,
                    message: "The control was found in the model but not as a single element of the form: " + elementProblem,
                    hint: "Address it by its GUID id or give the control a unique control name.");
                return null;
            }

            string payloadText = context.Element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, CustomPropertiesPayload.AttributeName, StringComparison.Ordinal))?.Value;
            if (!CustomPropertiesPayload.TryParse(payloadText, out var payload) || payload == null)
            {
                error = McpResponse.Err(code: "CustomPropertiesUnreadable", target: target,
                    message: "The control's " + CustomPropertiesPayload.AttributeName + " could not be parsed; it is left untouched.");
                return null;
            }
            context.Payload = payload;
            return context;
        }

        internal static bool HasProperty(LayoutControl control, string name)
        {
            try
            {
                if (control.Attributes == null) return false;
                foreach (var property in control.Attributes.Properties)
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex)
            {
                Logger.Debug("[ControlParameters] property enumeration failed: " + (ex.InnerException ?? ex).Message);
            }
            return false;
        }

        private static XElement FindElement(XDocument document, LayoutControl control, out string problem)
        {
            problem = null;
            var candidates = document.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, control.ElementName, StringComparison.Ordinal)).ToList();

            if (!string.IsNullOrEmpty(control.Id))
            {
                var byId = candidates.Where(e => string.Equals(AttrValue(e, "id"), control.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byId.Count == 1) return byId[0];
            }
            if (!string.IsNullOrEmpty(control.ControlName))
            {
                var byName = candidates.Where(e => string.Equals(AttrValue(e, "controlName"), control.ControlName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byName.Count == 1) return byName[0];
            }
            if (!string.IsNullOrEmpty(control.Binding))
            {
                var byBinding = candidates.Where(e => string.Equals(AttrValue(e, "attribute"), control.Binding, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byBinding.Count == 1) return byBinding[0];
                problem = byBinding.Count + " <" + control.ElementName + "> elements are bound to " + control.Binding + ".";
                return null;
            }
            problem = "no element of the form matches it.";
            return null;
        }

        private static string AttrValue(XElement element, string name)
            => element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

        // ------------------------------------------------------------ checking

        private static bool VersionMatches(string expectedVersion, ControlContext context)
            => VersionMatches(expectedVersion, context.CurrentVersion, () => WriteService.ComputeVersionToken(context.Object));

        /// <summary>
        /// The caller's token against the form's current token (<c>ticks:parent:contentHash</c>, as
        /// genexus_read answers). The object-level token that genexus_properties get answers with
        /// (<c>ticks:parent</c>) is accepted too: it still detects a change of the object, only not
        /// of the form's text.
        /// </summary>
        internal static bool VersionMatches(string expectedVersion, string currentFormVersion, Func<string> currentObjectVersion)
        {
            if (string.IsNullOrWhiteSpace(expectedVersion)) return false;
            if (string.Equals(expectedVersion, currentFormVersion, StringComparison.Ordinal)) return true;
            if (expectedVersion.Count(c => c == ':') == 1)
                return string.Equals(expectedVersion, currentObjectVersion?.Invoke(), StringComparison.Ordinal);
            return false;
        }

        internal static bool SameStoredTokens(string existing, string candidate)
        {
            string left = Canonical(existing), right = Canonical(candidate);
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        private static string Canonical(string tokens)
        {
            return CustomPropertiesPayload.CanonicalXmlOrText(tokens);
        }

        internal static bool SameExpressions(IList<string> persisted, IList<string> expected)
        {
            if (persisted.Count != expected.Count) return false;
            for (int i = 0; i < persisted.Count; i++)
                if (!string.Equals(Squash(persisted[i]), Squash(expected[i]), StringComparison.Ordinal)) return false;
            return true;
        }

        // The engine keeps the spacing the author typed; two spellings of one expression are one expression.
        private static string Squash(string expression) => Regex.Replace(expression ?? string.Empty, @"\s+", " ").Trim();

        internal static List<ParameterListDiagnostic> ValidateProspectiveLayout(KBObject obj, string newXml)
        {
            var diagnostics = new List<ParameterListDiagnostic>();
            var document = new XmlDocument();
            document.LoadXml(newXml);
            var messages = LayoutFormModel.ValidateDetached(obj, document, out string unavailable);
            if (unavailable != null)
            {
                diagnostics.Add(new ParameterListDiagnostic { Code = "ValidationUnavailable", Message = unavailable });
                return diagnostics;
            }
            diagnostics.AddRange(messages);
            return diagnostics;
        }

        // Everything but the target control's parameters must be unchanged. The target element is
        // compared with its parameter list masked; with includeTarget the whole form is compared.
        internal static string DescribeLayoutDifference(string beforeXml, string afterXml, bool includeTarget = false)
        {
            if (string.IsNullOrWhiteSpace(afterXml)) return "the saved form could not be read back.";
            string left = includeTarget ? beforeXml : MaskParameters(beforeXml);
            string right = includeTarget ? afterXml : MaskParameters(afterXml);
            return XmlEquivalence.AreEquivalent(left, right, XmlEquivalenceOptions.Layout, out string diff) ? null : diff;
        }

        internal static string MaskParameters(string xml)
        {
            try
            {
                var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                foreach (var element in document.Descendants())
                {
                    var attribute = element.Attributes().FirstOrDefault(a =>
                        string.Equals(a.Name.LocalName, CustomPropertiesPayload.AttributeName, StringComparison.Ordinal));
                    if (attribute == null) continue;
                    if (!CustomPropertiesPayload.TryParse(attribute.Value, out var payload) || payload == null) continue;
                    if (!payload.TryGet(PropertyName, out _)) continue;
                    attribute.Value = payload.ToAttributeValue(PropertyName, "(masked)");
                }
                return document.ToString(SaveOptions.DisableFormatting);
            }
            catch (XmlException)
            {
                // Unparseable text is left unmasked: the comparison that follows reports the parse error.
                return xml;
            }
        }

        private ControlContext ReadBack(string target, string control, string typeFilter, out string failure)
        {
            failure = null;
            var context = Resolve(target, control, typeFilter, out string error);
            if (context == null)
            {
                failure = ReadErrorMessage(error);
                return null;
            }
            // Resolve read the object through the fresh-read path, but the model operations ran on
            // the cached instance; the persisted list was parsed from the fresh text above.
            var fresh = _objectService.FindObjectFreshByIdentity(context.Object);
            if (fresh != null) context.Object = fresh;
            return context;
        }

        /// <summary>The message of an error envelope; says so when the envelope is not JSON.</summary>
        internal static string ReadErrorMessage(string errorEnvelope)
        {
            try { return JObject.Parse(errorEnvelope)["error"]?["message"]?.ToString(); }
            catch (JsonReaderException ex) { return "the error response is not valid JSON (" + ex.Message + ")"; }
        }

        /// <summary>The variables declared in the object's Variables part (the standard ones included), or null when unreadable.</summary>
        private static HashSet<string> DeclaredVariableNames(KBObject obj)
        {
            try
            {
                var part = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => string.Equals(p.GetType().Name, "VariablesPart", StringComparison.Ordinal));
                if (part == null) return null;
                return ReadVariableNames((IEnumerable)((dynamic)part).Variables);
            }
            catch (Exception ex)
            {
                Logger.Debug("[ControlParameters] variables not readable: " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }

        // A variable whose name cannot be read makes the whole list unreadable: a partial list would
        // report the variables it skipped as undeclared.
        internal static HashSet<string> ReadVariableNames(IEnumerable variables)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic variable in variables)
            {
                string name = variable.Name;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.TrimStart('&'));
            }
            return names;
        }

        internal static List<ParameterListDiagnostic> FindUndeclaredVariables(
            IReadOnlyList<string> requested, IReadOnlyList<string> resolved, ISet<string> declared)
        {
            var found = new List<ParameterListDiagnostic>();
            if (declared == null) return found; // unreadable: the SDK validation below still runs
            for (int i = 0; i < resolved.Count; i++)
                foreach (var name in ParameterListText.ExtractVariableNames(resolved[i]))
                    if (!declared.Contains(name))
                        found.Add(new ParameterListDiagnostic
                        {
                            Index = i,
                            Expression = i < requested.Count ? requested[i] : resolved[i],
                            Code = "UnknownVariableReference",
                            Message = "&" + name + " is not declared in this object's Variables part"
                        });
            return found;
        }

        private static string ResolveAttributeName(KBObject obj, int id)
        {
            var attribute = global::Artech.Genexus.Common.Objects.Attribute.Get(obj.Model, id);
            return attribute?.Name;
        }

        // ------------------------------------------------------------- results

        internal static List<string> NormalizeRequest(JArray expressions, string target, out string error)
        {
            error = null;
            var list = new List<string>();
            if (expressions == null)
            {
                error = McpResponse.Err(code: "ExpressionsRequired", target: target,
                    message: "expressions is required: the complete ordered list of parameter expressions.",
                    hint: "Pass expressions:[\"AttributeName\", \"&Variable\", \"'literal'\"]. The list replaces the current one, so include every parameter that must stay.");
                return null;
            }
            if (expressions.Count > MaxExpressions)
            {
                error = McpResponse.Err(code: "TooManyExpressions", target: target,
                    message: "A parameter list is limited to " + MaxExpressions + " expressions.");
                return null;
            }
            for (int i = 0; i < expressions.Count; i++)
            {
                if (expressions[i]?.Type != JTokenType.String)
                {
                    error = McpResponse.Err(code: "InvalidExpression", target: target,
                        message: "expressions[" + i + "] must be a string.");
                    return null;
                }
                string expression = expressions[i].ToString().Trim();
                if (expression.Length == 0)
                {
                    error = McpResponse.Err(code: "InvalidExpression", target: target,
                        message: "expressions[" + i + "] is empty.", hint: "Remove it from the list; an empty entry would become a missing argument.");
                    return null;
                }
                if (expression.Length > MaxExpressionLength || expression.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                {
                    error = McpResponse.Err(code: "InvalidExpression", target: target,
                        message: "expressions[" + i + "] must be one line of at most " + MaxExpressionLength + " characters.");
                    return null;
                }
                if (!ParameterListText.IsSingleExpression(expression))
                {
                    error = McpResponse.Err(code: "InvalidExpression", target: target,
                        message: "expressions[" + i + "] holds more than one expression (a comma outside parentheses and quotes).",
                        hint: "Send one expression per entry.");
                    return null;
                }
                list.Add(expression);
            }
            return list;
        }

        private static string ReferenceError(string target, ControlContext context, List<UnresolvedAttributeReference> unresolved)
        {
            var first = unresolved[0];
            return McpResponse.Err(
                code: "UnknownAttributeReference", target: target,
                message: first.LookupFailure == null
                    ? "expressions[" + first.Index + "] names att:" + first.AttributeId + ", which is not an attribute of this Knowledge Base; nothing was written."
                    : "expressions[" + first.Index + "] names att:" + first.AttributeId + ", which could not be looked up (" + first.LookupFailure + "); nothing was written.",
                hint: "Pass the attribute by name (as in the Transaction structure) or with an att:<id> that exists in this KB. genexus_structure action=get_visual lists them.",
                extra: new JObject
                {
                    ["persisted"] = false,
                    ["savePathExercised"] = false,
                    ["control"] = DescribeControl(context.Control),
                    ["diagnostics"] = new JArray(unresolved.Select(u => new JObject
                    {
                        ["index"] = u.Index,
                        ["expression"] = u.Expression,
                        ["code"] = "UnknownAttributeReference",
                        ["attributeId"] = u.AttributeId,
                        ["message"] = u.Message
                    }))
                });
        }

        private static string ValidationError(string target, ControlContext context, List<ParameterListDiagnostic> diagnostics,
            string code = "InvalidParameterExpression", string message = null)
        {
            var first = diagnostics[0];
            return McpResponse.Err(
                code: code, target: target,
                message: message ?? ("The SDK rejected the parameter list" + (first.Index.HasValue ? " at expressions[" + first.Index + "]" : "") + ": " + first.Message),
                hint: "Fix the listed expressions and retry the dry run. Names must exist in this object's model (attributes, &variables, objects); nothing was written.",
                extra: new JObject
                {
                    ["persisted"] = false,
                    ["savePathExercised"] = false,
                    ["control"] = DescribeControl(context.Control),
                    ["diagnostics"] = new JArray(diagnostics.Select(d => new JObject
                    {
                        ["index"] = d.Index.HasValue ? (JToken)d.Index.Value : JValue.CreateNull(),
                        ["expression"] = d.Expression,
                        ["code"] = d.Code,
                        ["message"] = d.Message
                    }))
                });
        }

        internal static JArray BuildChanges(IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            var changes = new JArray();
            int count = Math.Max(before.Count, after.Count);
            for (int i = 0; i < count; i++)
            {
                string b = i < before.Count ? before[i] : null;
                string a = i < after.Count ? after[i] : null;
                string kind = b == null ? "added" : a == null ? "removed"
                    : string.Equals(Squash(b), Squash(a), StringComparison.Ordinal) ? "unchanged" : "changed";
                changes.Add(new JObject
                {
                    ["index"] = i,
                    ["change"] = kind,
                    ["before"] = b == null ? JValue.CreateNull() : (JToken)b,
                    ["after"] = a == null ? JValue.CreateNull() : (JToken)a
                });
            }
            return changes;
        }

        internal static JObject DescribeControl(LayoutControl control)
        {
            return new JObject
            {
                ["name"] = control.ControlName ?? control.Binding,
                ["element"] = control.ElementName,
                ["specType"] = control.SpecType,
                ["binding"] = control.Binding,
                ["attributeName"] = control.BindingName,
                ["id"] = control.Id,
                ["path"] = control.Path
            };
        }

        internal static JArray DescribeCandidates(LayoutPanel panel)
            => new JArray(panel.Controls
                .Where(c => !string.IsNullOrEmpty(c.Binding) || !string.IsNullOrEmpty(c.ControlName))
                .Take(40).Select(DescribeControl));

        private static JToken DescribeDataProvider(ControlContext context)
        {
            string stored = context.ReadStored("DataProvider");
            if (stored == null) return JValue.CreateNull();
            return DescribeStoredDataProvider(stored, (typeGuid, id) =>
                context.Object.Model.Objects.GetName(new global::Artech.Udm.Framework.EntityKey(typeGuid, id))?.ObjectName);
        }

        // The stored value is "<type guid>-<id>". A lookup that fails is reported beside the stored
        // value, so a missing name is never read as a Data Provider that has none.
        internal static JObject DescribeStoredDataProvider(string stored, Func<Guid, int, string> objectNameOf)
        {
            var entry = new JObject { ["stored"] = stored };
            var match = Regex.Match(stored, @"^([0-9a-fA-F-]{36})-(\d+)$");
            if (!match.Success) return entry;
            try
            {
                string name = objectNameOf(new Guid(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                if (name != null) entry["name"] = name;
            }
            catch (Exception ex)
            {
                entry["nameLookupError"] = (ex.InnerException ?? ex).Message;
            }
            return entry;
        }

        private static string NotApplicableHint(ControlContext context)
            => "Parameters apply to a Dynamic Combo Box or Dynamic List Box whose Data Source is a Data Provider. This control has ControlType="
               + (context.ReadStored("ControlType") ?? "(default edit)") + " and DataSourceFrom=" + (context.ReadStored("DataSourceFrom") ?? "(default)")
               + ". Configure the control in the GeneXus IDE first.";
    }
}
