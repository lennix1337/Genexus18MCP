using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // WebForm/Layout (visual part) write path extracted from WriteService.cs (plan 007).
    // Pure move, no logic changes — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {

        // Detects the case where an agent is about to edit WebForm/Layout on an object
        // that already has a populated PatternInstance (the canonical surface for that
        // screen). On the next pattern apply/save, WWP regenerates the WebForm from the
        // pattern, silently overwriting the agent's hand edits. The warning steers the
        // agent toward `genexus_edit part=PatternInstance` without blocking the write —
        // power users still need the WebForm escape hatch for non-WWP customizations.
        private JArray BuildPatternShadowWarningsIfAny(
            global::Artech.Architecture.Common.Objects.KBObject obj,
            string partName)
        {
            // The probe itself moved to PatternAnalysisService.BuildPatternShadowWarning,
            // shared with PatchService: the two copies had drifted into giving
            // different advice for the same EditingWebFormUnderPattern code.
            return _patternAnalysisService.BuildPatternShadowWarning(obj, partName);
        }

        // The probe itself moved to PatternAnalysisService.BuildPatternShadowWarning,
        // shared with PatchService: the two copies had drifted into giving different
        // advice for the same EditingWebFormUnderPattern code.
        private static void AttachWarnings(JObject payload, JArray warnings)
        {
            if (payload == null || warnings == null || warnings.Count == 0) return;
            payload["warnings"] = warnings;
        }

        // Item 6: extract GotchaHtmlFormatScriptStripped entries from layoutGotchas and
        // return them as top-level warnings[] items so callers that only inspect "warnings"
        // see the advisory even when they don't parse "layoutGotchas".
        private static JArray BuildHtmlFormatWarnings(JArray layoutGotchas)
        {
            if (layoutGotchas == null || layoutGotchas.Count == 0) return null;
            JArray result = null;
            foreach (var item in layoutGotchas)
            {
                if (!(item is JObject jo)) continue;
                if (!string.Equals(jo["code"]?.ToString(), "GotchaHtmlFormatScriptStripped", StringComparison.Ordinal)) continue;
                if (result == null) result = new JArray();
                result.Add(new JObject
                {
                    ["code"] = "GotchaHtmlFormatScriptStripped",
                    ["docUrl"] = GotchaCodes.DocUrlFor("GotchaHtmlFormatScriptStripped"),
                    ["message"] = "Format=\"HTML\" gxTextBlock with <script> inside CDATA — GeneXus generator escapes this on render. Code appears as literal text. Use <body onmousedown> + addEventListener for runtime JS instead."
                });
            }
            return result;
        }

        // Merges two JArrays of warning objects — returns null when both inputs are empty/null.
        private static JArray MergeWarnings(JArray a, JArray b)
        {
            if ((a == null || a.Count == 0) && (b == null || b.Count == 0)) return null;
            var merged = new JArray();
            if (a != null) foreach (var t in a) merged.Add(t);
            if (b != null) foreach (var t in b) merged.Add(t);
            return merged;
        }

        private static JArray ToValidationDiagnostics(IEnumerable<WebFormPreSaveValidator.ValidationMessage> messages)
        {
            var result = new JArray();
            if (messages == null) return result;
            foreach (var message in messages)
            {
                if (message == null) continue;
                var item = new JObject
                {
                    ["severity"] = message.Severity ?? "Information",
                    ["message"] = message.Message ?? string.Empty
                };
                if (!string.IsNullOrWhiteSpace(message.Code)) item["code"] = message.Code;
                if (!string.IsNullOrWhiteSpace(message.Source)) item["source"] = message.Source;
                if (message.Positions != null && message.Positions.Count > 0) item["positions"] = new JArray(message.Positions);
                result.Add(item);
            }
            return result;
        }

        // The refusal's message quotes the first SDK errors (code, text, position) so a caller that
        // reads only the message still learns what was rejected; validationDiagnostics carries them all.
        internal static string WithValidationSummary(string message, IEnumerable<WebFormPreSaveValidator.ValidationMessage> messages)
        {
            string summary = WebFormPreSaveValidator.SummarizeErrors(messages);
            return summary.Length == 0 ? message : message + " " + summary;
        }

        /// <summary>
        /// The WebFormValidationFailed envelope of a visual write the SDK validator refused: the same shape for the dry run
        /// of a no-change write, the dry run of a change and the save itself (issue #830).
        /// </summary>
        internal static string BuildWebFormValidationRefusal(
            string target, string partName, string message, WebFormPreSaveValidator.ValidationReport report, bool dryRun)
        {
            var extra = new JObject
            {
                ["part"] = partName,
                ["validationAvailable"] = report.ValidatorAvailable,
                ["validationDiagnostics"] = ToValidationDiagnostics(report.Messages),
                ["persisted"] = false
            };
            if (dryRun) extra["dryRun"] = true;
            if (!string.IsNullOrWhiteSpace(report.Error)) extra["validationError"] = report.Error;
            return Models.McpResponse.Err(
                code: "WebFormValidationFailed",
                message: WithValidationSummary(message, report.Messages),
                hint: "Fix the listed controls/attributes and retry. Pass forceWrite=true only when you explicitly accept the SDK validation errors.",
                target: target,
                extra: extra);
        }

        // The validator's own messages are the only account of why it refused a form. They used
        // to be returned to the caller and nowhere else, so a refusal that never reached the
        // caller (or was reshaped on the way) left the Worker log with no trace of its cause.
        private static void LogValidationMessages(string phase, WebFormPreSaveValidator.ValidationReport report)
        {
            if (report == null) return;
            try
            {
                Logger.Info("[WebFormValidate] " + phase + " available=" + report.ValidatorAvailable
                    + " messages=" + (report.Messages?.Count ?? 0) + (string.IsNullOrWhiteSpace(report.Error) ? string.Empty : " error=" + SanitizeVisualSaveText(report.Error)));
                if (report.Messages == null) return;
                foreach (var message in report.Messages.Take(20))
                    Logger.Info("[WebFormValidate]   " + (message.Severity ?? "Information") + ": " + SanitizeVisualSaveText(message.Message)
                        + (string.IsNullOrWhiteSpace(message.Source) ? string.Empty : " (" + SanitizeVisualSaveText(message.Source) + ")"));
            }
            catch { }
        }

        private static void AttachWebFormValidation(JObject payload, WebFormPreSaveValidator.ValidationReport report)
        {
            if (payload == null || report == null) return;
            payload["validationAvailable"] = report.ValidatorAvailable;
            payload["validationDiagnostics"] = ToValidationDiagnostics(report.Messages);
            if (!string.IsNullOrWhiteSpace(report.Error)) payload["validationError"] = report.Error;
        }

        private WebFormPreSaveValidator.ValidationReport ValidateProspectiveWebForm(
            global::Artech.Architecture.Common.Objects.KBObjectPart webFormPart,
            string normalizedInput,
            string baselineXml)
        {
            var unavailable = new WebFormPreSaveValidator.ValidationReport();
            try
            {
                var kb = _objectService.GetKbService().GetKB();
                if (kb == null)
                {
                    unavailable.Error = "KB not opened.";
                    return unavailable;
                }

                using (var transaction = kb.BeginTransaction())
                {
                    try
                    {
                        WebFormXmlHelper.ApplyEditableXml(webFormPart, normalizedInput, baselineXml: baselineXml);
                        var prospective = WebFormPreSaveValidator.ValidateDetailed(webFormPart);
                        LogValidationMessages("prospective", prospective);
                        return prospective;
                    }
                    finally
                    {
                        try { transaction.Rollback(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                unavailable.Error = ex.InnerException?.Message ?? ex.Message;
                Logger.Debug("[WebFormValidate] prospective validation skipped: " + unavailable.Error);
                return unavailable;
            }
        }

        // issue #404: the Query structure is written through the part's own deserializer, then read
        // back; a write whose read-back differs is reported, never claimed.
        private string WriteQueryStructure(
            global::Artech.Architecture.Common.Objects.KBObject obj, string target, string text, bool dryRun)
        {
            var part = QueryStructureText.FindPart(obj);
            try { QueryStructureText.Parse(text); }
            catch (FormatException ex) { return CreateWriteError("Invalid query structure", target, "QueryStructure", ex.Message, obj); }

            if (dryRun)
                return Models.McpResponse.Ok(target: target, code: "DryRun", result: new JObject { ["part"] = "QueryStructure", ["savePathExercised"] = false });

            try
            {
                QueryStructureText.Apply(part, text);
                // The deserializer does not mark the part dirty, so without this the object save
                // writes nothing for it and the call would report a write that never happened.
                part.Dirty = true;
                part.Save();
                obj.Save();
            }
            catch (Exception ex)
            {
                return CreateWriteError("Query structure write failed", target, "QueryStructure", ex.InnerException?.Message ?? ex.Message, obj);
            }

            var submitted = QueryStructureText.Parse(text);
            var kept = QueryStructureText.Parse(QueryStructureText.Render(QueryStructureText.FindPart(obj)));
            if (!submitted.Select(l => l.Count).SequenceEqual(kept.Select(l => l.Count)))
                return CreateWriteError("Query structure not persisted", target, "QueryStructure", "The SDK did not keep every submitted element, parameter, filter or order.", obj);

            return Models.McpResponse.Ok(target: target, code: "Success", result: new JObject { ["part"] = "QueryStructure", ["persisted"] = true });
        }

        /// <summary>
        /// Issue #711: refuses a visual write that leaves a control's Conditions (ControlWhere)
        /// different in its two property bags, before anything is saved. Null when the write may
        /// proceed. See <see cref="ControlConditionCarriers"/> for which bag the IDE reads.
        /// </summary>
        internal static string RefuseDivergentConditionCarriers(
            string target, string partName, string currentXml, string requestedXml, bool dryRun)
        {
            List<ControlConditionCarriers.Divergence> divergences;
            try { divergences = ControlConditionCarriers.FindIntroduced(currentXml, requestedXml); }
            catch (Exception ex)
            {
                Logger.Debug("[VisualWrite] condition-carrier check skipped: " + ex.Message);
                return null;
            }
            if (divergences.Count == 0) return null;

            var items = new JArray();
            foreach (var d in divergences)
            {
                items.Add(new JObject
                {
                    ["element"] = d.Element,
                    ["control"] = d.Control,
                    ["property"] = d.Property,
                    ["idePropertyName"] = "Conditions",
                    [ControlConditionCarriers.WebUserControlPropertiesAttribute] = d.WebUserControlExpression,
                    [CustomPropertiesPayload.AttributeName] = d.CustomPropertiesExpression,
                    ["changedByRequest"] = new JArray(
                        new[]
                        {
                            d.CustomPropertiesChanged ? CustomPropertiesPayload.AttributeName : null,
                            d.WebUserControlChanged ? ControlConditionCarriers.WebUserControlPropertiesAttribute : null
                        }.Where(x => x != null))
                });
            }
            var first = divergences[0];
            return Models.McpResponse.Err(
                code: "ControlConditionCarriersDiverge",
                message: "The request would leave " + first.Property + " (the IDE's Conditions) of control '" + first.Control
                    + "' different in " + CustomPropertiesPayload.AttributeName + " and "
                    + ControlConditionCarriers.WebUserControlPropertiesAttribute + "; nothing was written.",
                hint: "For a built-in control type such as Dynamic Combo Box, the IDE's Conditions property and the control's SDK "
                    + "property read " + first.Property + " from " + CustomPropertiesPayload.AttributeName + ". Write the expression there "
                    + "(plain text such as 'Att = &Var;' is accepted; the SDK stores it as a TokenDataList), and in the same edit set "
                    + ControlConditionCarriers.WebUserControlPropertiesAttribute + "'s " + first.Property
                    + " to the same expression or remove that entry, so both bags agree.",
                target: target,
                extra: new JObject
                {
                    ["part"] = partName,
                    ["divergences"] = items,
                    ["canonicalCarrier"] = CustomPropertiesPayload.AttributeName,
                    ["persisted"] = false,
                    ["savePathExercised"] = false,
                    ["dryRun"] = dryRun
                });
        }

        private string WriteVisualPart(
            global::Artech.Architecture.Common.Objects.KBObject obj,
            string target,
            string partName,
            string xml,
            bool dryRun = false,
            bool strictVerify = true,
            bool forceWrite = false,
            bool rollbackOnFailure = false)
        {
            var webFormPart = WebFormXmlHelper.GetWebFormPart(obj);
            if (webFormPart == null)
            {
                return CreateWriteError(
                    "Visual part not found",
                    target,
                    partName,
                    "The object does not expose a WebForm part for visual editing.",
                    obj);
            }

            // Probe ONCE up front so every response branch (NoChange / DryRun / Success)
            // can attach the same warning set without duplicating the resolution work.
            JArray patternShadowWarnings = BuildPatternShadowWarningsIfAny(obj, partName);

            // Captured from the precheck so the save-failure handler can emit a
            // deterministic transition hint without re-parsing post-rollback state.
            string currentFormType = null;
            string incomingFormType = null;

            string normalizedInput;
            try
            {
                normalizedInput = WebFormXmlHelper.NormalizeEditableXmlInput(xml, partName);
            }
            catch (Exception ex)
            {
                return CreateWriteError("Invalid visual XML", target, partName, ex.Message, obj);
            }

            // Friction 2026-05-22: project layout gotchas against the prospective
            // content BEFORE the SDK save. Surfaces HTML-form/gxButton/gxAttribute
            // limitations at validate=only time so the caller fixes the XML in
            // the same turn instead of after a build + browser cycle.
            JArray prospectiveGotchas = null;
            try
            {
                var hits = GxMcp.Worker.Helpers.LayoutGotchaScanner.Scan(normalizedInput, obj);
                if (hits != null && hits.Count > 0)
                {
                    prospectiveGotchas = new JArray();
                    foreach (var g in hits)
                    {
                        prospectiveGotchas.Add(new JObject
                        {
                            ["code"] = g.Code,
                            ["docUrl"] = g.DocUrl,
                            ["severity"] = g.Severity,
                            ["element"] = g.Element,
                            ["controlId"] = g.ControlId,
                            ["message"] = g.Message,
                            ["workaround"] = g.Workaround
                        });
                    }

                    // C18: an Error-severity gotcha means the input is structurally
                    // dangerous (e.g. gxButton-in-gxButton, pathological nesting) and can
                    // crash the worker via an UNCATCHABLE StackOverflow in the SDK's
                    // recursive layout parser. Refuse the write BEFORE ApplyEditableXml —
                    // this is the only defense (no try/catch survives a StackOverflow).
                    GxMcp.Worker.Helpers.LayoutGotchaScanner.Gotcha blocker = null;
                    foreach (var h in hits)
                    {
                        if (string.Equals(h.Severity, "Error", StringComparison.OrdinalIgnoreCase))
                        {
                            blocker = h;
                            break;
                        }
                    }
                    if (blocker != null)
                    {
                        return CreateWriteError(
                            "Rejected structurally invalid layout to protect the worker",
                            target, partName,
                            blocker.Message + " " + blocker.Workaround, obj);
                    }
                }
            }
            catch (Exception scanEx)
            {
                Logger.Debug("[GOTCHA-PREVIEW] scan failed: " + scanEx.Message);
            }

            string currentXml = null;
            bool conditionCarriersChecked = false;
            try
            {
                currentXml = WebFormXmlHelper.ReadEditableXml(obj);
                currentFormType = TryExtractFormType(currentXml);
                incomingFormType = TryExtractFormType(normalizedInput);
                if (XmlEquivalence.AreEquivalent(currentXml, normalizedInput, out _))
                {
                    WebFormPreSaveValidator.ValidationReport noChangeValidation = null;
                    if (dryRun)
                    {
                        var noChangeValidationProjection = WebFormTypedPropertyWriter.ProjectDescriptorFixupOntoDetachedXml(
                            currentXml, currentXml);
                        noChangeValidation = ValidateProspectiveWebForm(
                            webFormPart, noChangeValidationProjection.Xml ?? currentXml, currentXml);
                        if (WebFormPreSaveValidator.HasErrors(noChangeValidation.Messages) && !forceWrite)
                            return BuildWebFormValidationRefusal(target, partName,
                                "The WebForm SDK validator found errors in the current visual XML.", noChangeValidation, dryRun: true);
                    }
                    var noChangeResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = dryRun ? "Dry-run: no change would be applied." : "No change",
                        ["dryRun"] = dryRun
                    };
                    if (noChangeValidation != null)
                    {
                        AttachWebFormValidation(noChangeResp, noChangeValidation);
                        if (forceWrite && WebFormPreSaveValidator.HasErrors(noChangeValidation.Messages))
                            noChangeResp["validationOverridden"] = true;
                    }
                    var noChangeProjection = WebFormTypedPropertyWriter.ProjectDescriptorFixupOntoDetachedXml(
                        currentXml, currentXml);
                    noChangeResp["transformationsSimulated"] = true;
                    noChangeResp["validationInput"] = "detached-projection";
                    noChangeResp["detachedTransformation"] = BuildDetachedTransformationEvidence(noChangeProjection);
                    var noChangeHtmlWarnings = BuildHtmlFormatWarnings(prospectiveGotchas);
                    AttachWarnings(noChangeResp, MergeWarnings(patternShadowWarnings, noChangeHtmlWarnings));
                    if (prospectiveGotchas != null) noChangeResp["layoutGotchas"] = prospectiveGotchas;
                    return Models.McpResponse.Ok(target: target, code: "WriteNoChange", result: noChangeResp);
                }
                conditionCarriersChecked = true;
                string carrierRefusal = RefuseDivergentConditionCarriers(target, partName, currentXml, normalizedInput, dryRun);
                if (carrierRefusal != null) return carrierRefusal;
                if (dryRun)
                {
                    // Run the same conservative descriptor projection on a detached
                    // XML copy before asking the SDK validator to inspect it. The
                    // live part is still inside a rollback-only transaction.
                    var dryProjection = WebFormTypedPropertyWriter.ProjectDescriptorFixupOntoDetachedXml(
                        currentXml, normalizedInput);
                    var dryValidation = ValidateProspectiveWebForm(
                        webFormPart, dryProjection.Xml ?? normalizedInput, currentXml);
                    if (WebFormPreSaveValidator.HasErrors(dryValidation.Messages) && !forceWrite)
                        return BuildWebFormValidationRefusal(target, partName,
                            "The WebForm SDK validator found errors in the requested visual XML.", dryValidation, dryRun: true);
                    var dryResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = "Dry-run: input parsed and would update visual XML. Save skipped.",
                        ["verified"] = new JArray("xmlParse", "layoutGotchas", "diffVsCurrent"),
                        ["savePathExercised"] = false,
                        ["dryRun"] = true
                    };
                    AttachWebFormValidation(dryResp, dryValidation);
                    dryResp["transformationsSimulated"] = true;
                    dryResp["validationInput"] = "detached-projection";
                    dryResp["detachedTransformation"] = BuildDetachedTransformationEvidence(dryProjection);
                    var dryHtmlWarnings = BuildHtmlFormatWarnings(prospectiveGotchas);
                    AttachWarnings(dryResp, MergeWarnings(patternShadowWarnings, dryHtmlWarnings));
                    var suspects = GxMcp.Worker.Helpers.WebFormSchemaHints.ScanForRejectedAttributes(normalizedInput);
                    if (suspects.Count > 0)
                    {
                        var arr = new JArray();
                        foreach (var s in suspects)
                        {
                            var entry = new JObject
                            {
                                ["element"] = s.Element,
                                ["attribute"] = s.Attribute,
                                ["reason"] = s.Reason
                            };
                            // Issue #360: name the attribute to write instead when the
                            // diagnostic knows one. "Not in the hint table" alone left the
                            // caller with a bare Format attribute that verified and did
                            // nothing, which is the trap the issue reported.
                            if (!string.IsNullOrEmpty(s.Fix)) entry["fix"] = s.Fix;
                            arr.Add(entry);
                        }
                        dryResp["preflightWarnings"] = arr;
                        dryResp["warning"] = "Dry-run found " + suspects.Count + " attribute(s) that the generator will not read as written; see preflightWarnings (each entry carries a fix when one is known).";
                    }
                    if (prospectiveGotchas != null) dryResp["layoutGotchas"] = prospectiveGotchas;
                    return Models.McpResponse.Ok(target: target, code: "WriteDryRun", result: dryResp);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[DEBUG-SAVE] Visual no-change precheck skipped: " + ex.Message);
                if (dryRun)
                {
                    return CreateWriteError(
                        "Visual dry-run precheck failed",
                        target,
                        partName,
                        "Dry-run input parsed, but reading current visual part failed (" + ex.Message + "). State comparison skipped.",
                        obj,
                        code: "VisualReadFailed");
                }
            }

            if (!conditionCarriersChecked)
            {
                // The current XML could not be read, so every divergence counts as introduced.
                string carrierRefusal = RefuseDivergentConditionCarriers(target, partName, currentXml, normalizedInput, dryRun);
                if (carrierRefusal != null) return carrierRefusal;
            }

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return CreateWriteError("KB not opened", target, partName, "Open a Knowledge Base before writing visual metadata.", obj);
            }

            bool sdkCommitCompleted = false;
            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    WebFormXmlHelper.ApplyEditableXml(webFormPart, normalizedInput, baselineXml: currentXml);

                    var validationReport = WebFormPreSaveValidator.ValidateDetailed(webFormPart);
                    if (WebFormPreSaveValidator.HasErrors(validationReport.Messages) && !forceWrite)
                    {
                        transaction.Rollback();
                        return BuildWebFormValidationRefusal(target, partName,
                            "The WebForm SDK validator found errors; the visual write was not persisted.", validationReport, dryRun: false);
                    }

                    // ── DIAGNOSTIC: byte-level state RIGHT BEFORE obj.Save ────────────────
                    Helpers.WebFormSaveDiagnostics.DumpState(webFormPart, obj, "BEFORE-SAVE");

                    JObject webFormPartSaveError = null;
                    // The SDK's own refusal of the object save (e.g. an attribute reference
                    // that does not resolve in the KB). It used to reach only the worker log
                    // before the fallback save ran, so the client saw a bare verification
                    // mismatch with nothing to act on.
                    JObject forceSaveError = null;
                    try
                    {
                        webFormPart.Save();
                    }
                    catch (Exception wfSaveEx)
                    {
                        var rootEx = wfSaveEx.InnerException ?? wfSaveEx;
                        webFormPartSaveError = new JObject
                        {
                            ["type"] = rootEx.GetType().FullName,
                            ["message"] = rootEx.Message,
                            ["where"] = "webFormPart.Save()"
                        };
                        Logger.Info("[VisualWrite] webFormPart.Save() threw: " + rootEx.GetType().Name + ": " + rootEx.Message + " — captured for error envelope if needed.");
                    }

                    // KBObjectManager.PrepareSave skips persistence silently when kbObject.Mode ==
                    // Mode.Unchanged. Our XmlNode/Properties mutations don't propagate to obj.Mode
                    // in the headless worker, so the default obj.Save() path always no-ops. Pass
                    // KBObjectSavePreferences { ForceSave = true } to bypass that check — same
                    // mechanism the SDK uses internally for generated objects (Layers.BL line 1025).
                    try
                    {
                        var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                        {
                            ForceSave = true,
                            ForceSaveDefaultParts = true,
                            SkipValidation = true
                        };
                        obj.Save(prefs);
                        Logger.Info("[VisualWrite] obj.Save(KBObjectSavePreferences{ForceSave=true}) completed.");
                    }
                    catch (Exception fsEx)
                    {
                        var inner = fsEx.InnerException ?? fsEx;
                        Logger.Info("[VisualWrite] obj.Save(ForceSave) threw: " + inner.GetType().Name + ": " + inner.Message + " — falling back to EnsureSave.");
                        forceSaveError = BuildVisualSaveErrorEvidence(fsEx, "obj.Save(ForceSave)");
                        obj.EnsureSave(true);
                    }

                    // ── DIAGNOSTIC: byte-level state RIGHT AFTER obj.Save ─────────────────
                    Helpers.WebFormSaveDiagnostics.DumpState(webFormPart, obj, "AFTER-SAVE");

                    // ── BYPASS: Direct SaveModelEntityOutput / SaveWithParent / SaveHeader ──
                    // These three reflection-based bypass calls are diagnostic experiments
                    // and are OFF by default. Set GXMCP_WEBFORM_SAVE_DIAGNOSTICS=1 to enable.
                    if (string.Equals(Environment.GetEnvironmentVariable("GXMCP_WEBFORM_SAVE_DIAGNOSTICS"), "1", StringComparison.Ordinal))
                    {
                        // The SDK's SaveWithParent path completes successfully and SerializeData()
                        // returns the right bytes, but they don't reach disk. Try writing bytes
                        // directly via Entity.SaveModelEntityOutput(outputTypeId, version, ts, bytes).
                        Helpers.WebFormSaveDiagnostics.TryDirectSaveModelEntityOutput(webFormPart, obj);

                        // If PerformSave's iteration of kbObject.Parts is skipping our part
                        // (IsVirtualPart=true, ShouldIgnorePart=true, or wrong instance), this
                        // sidesteps the loop entirely and forces SaveWithParent with our ref.
                        Helpers.WebFormSaveDiagnostics.TryDirectSaveWithParent(webFormPart, obj);

                        // SaveHeader writes the entity's primary row, which likely contains the data BLOB column.
                        Helpers.WebFormSaveDiagnostics.TryDirectSaveHeader(webFormPart);
                    }

                    // ── DIAGNOSTIC: state after all bypasses, before transaction.Commit ──
                    if (string.Equals(Environment.GetEnvironmentVariable("GXMCP_WEBFORM_SAVE_DIAGNOSTICS"), "1", StringComparison.Ordinal))
                    {
                        Helpers.WebFormSaveDiagnostics.DumpState(webFormPart, obj, "AFTER-BYPASSES");
                    }

                    transaction.Commit();
                    sdkCommitCompleted = true;
                    // Placed here rather than on the return paths below: every one of them
                    // already carries committed: true, so one mark after the commit covers
                    // the success and the committed-but-unverified exits alike.
                    NotePerTargetWrite(target);
                    // Issue #711: a layout write can change a control's custom properties
                    // (Conditions/ControlWhere), which genexus_properties serves from a
                    // short-lived cache; drop it so the next typed read is the committed value.
                    PropertyService.InvalidatePropertyCache(obj);
                    // Force synchronous flush so the data actually lands on disk before we re-read
                    // for verification. ScheduleFlush() default is timer-based and async — if the
                    // worker is killed before the timer fires (or before ProcessExit), unflushed
                    // KB writes are lost.
                    ScheduleFlush(force: true);
                    Logger.Info("[VisualWrite] ScheduleFlush(force=true) completed.");

                    // Async follow-up (next-major item #2): the re-read + XML diff below dominates
                    // wall-clock on large PatternInstance/WebForm writes and is the cause of client
                    // timeouts. validate="best-effort" (strictVerify=false) skips the re-read/diff;
                    // a genuine SDK save error captured during the save is still surfaced. The commit
                    // + forced flush already happened, so the bytes are on disk either way.
                    if (strictVerify)
                    {
                        var refreshedObj = _objectService.FindObject(target);
                        string persistedXml = WebFormXmlHelper.ReadEditableXml(refreshedObj ?? obj);
                        if (!XmlEquivalence.AreEquivalent(persistedXml, normalizedInput, XmlEquivalenceOptions.Layout, out var visualDiff, out var visualStructured))
                        {
                            if (webFormPartSaveError != null)
                                Logger.Info("[VisualWrite] webFormPart.Save() error captured during verification failure: " + webFormPartSaveError.ToString(Newtonsoft.Json.Formatting.None));
                            string visualVerifyErr = CreateWriteError(
                                "Visual write verification failed",
                                target,
                                partName,
                                DescribeVisualVerificationMismatch(persistedXml, visualDiff, forceSaveError ?? webFormPartSaveError),
                                obj,
                                visualStructured);
                            // The transaction is already committed at this point. Keep
                            // physical persistence separate from content verification;
                            // callers must not read this mismatch as "nothing was saved".
                            return DecorateVisualVerificationFailure(visualVerifyErr, webFormPartSaveError, forceSaveError,
                                sdkCommitCompleted, rollbackOnFailure, prospectiveGotchas, target, partName);
                        }
                    }
                    else if (webFormPartSaveError != null)
                    {
                        // best-effort: skip the diff but never swallow a real SDK save failure.
                        string sdkErr = CreateWriteError(
                            "Visual write reported an SDK save error",
                            target,
                            partName,
                            "The SDK save path returned an error (post-write XML diff was skipped because validate=best-effort).",
                            obj,
                            null);
                        try
                        {
                            var sdkErrJobj = JObject.Parse(sdkErr);
                            var errObj = sdkErrJobj["error"] as JObject ?? sdkErrJobj;
                            AttachVisualSaveError(errObj, webFormPartSaveError, forceSaveError);
                            if (sdkCommitCompleted)
                            {
                                AttachVisualCommitEvidence(sdkErrJobj, committed: true, verified: false);
                                AttachVisualCommitEvidence(errObj, committed: true, verified: false);
                            }
                            sdkErrJobj["rollbackRequested"] = rollbackOnFailure;
                            AddVersioningRestoreStep(sdkErrJobj, target, partName);
                            return sdkErrJobj.ToString();
                        }
                        catch { return sdkErr; }
                    }

                    var okResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = strictVerify
                            ? "Visual XML updated and verified."
                            : "Visual XML updated (post-write verify skipped: validate=best-effort). Build to confirm generation.",
                        ["sdkSaveCompleted"] = sdkCommitCompleted,
                        ["saved"] = sdkCommitCompleted,
                        ["physicalCommit"] = sdkCommitCompleted,
                        ["commitState"] = sdkCommitCompleted ? "Committed" : "NotCommitted",
                        ["commitConfirmed"] = sdkCommitCompleted,
                        ["persistenceState"] = sdkCommitCompleted ? "Committed" : "NotPersisted",
                        ["persistedVerified"] = strictVerify && sdkCommitCompleted
                    };
                    // The write verified (or verification was skipped) even though the first
                    // save call threw and the fallback save ran. Say so instead of hiding it.
                    if (forceSaveError != null) okResp["forceSaveFallback"] = forceSaveError;
                    AttachWebFormValidation(okResp, validationReport);
                    if (forceWrite && WebFormPreSaveValidator.HasErrors(validationReport.Messages))
                    {
                        okResp["validationOverridden"] = true;
                    }
                    // Item 6 (friction-report 2026-05-22): promote GotchaHtmlFormatScriptStripped
                    // gotchas into the top-level warnings[] so callers that only inspect "warnings"
                    // (not "layoutGotchas") still see the HTML-escape advisory on success.
                    var htmlFormatWarnings = BuildHtmlFormatWarnings(prospectiveGotchas);
                    var allWarnings = MergeWarnings(patternShadowWarnings, htmlFormatWarnings);
                    AttachWarnings(okResp, allWarnings);
                    if (prospectiveGotchas != null) okResp["layoutGotchas"] = prospectiveGotchas;
                    return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: okResp);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    // Friction 2026-05-22: prior version surfaced only ex.Message
                    // which was often a generic wrapper. Walk InnerException so the
                    // root SDK error (e.g. "Invalid reference: variable 'XYZ' not
                    // declared") makes it to the response details.
                    string chain = FormatExceptionChain(ex);

                    // Friction 2026-05-25 — recognize specific failure shapes
                    // observed in real-world sessions and translate them into
                    // structured next-steps the LLM can act on. The bare
                    // "Visual write failed" envelope used to cost callers
                    // several iterations to diagnose; with a recognizable hint
                    // they correct on the next call.
                    string hint = null;
                    string code = null;

                    // Deterministic detection: if the incoming XML changed the
                    // <Form type="…"> attribute vs the persisted body, the save
                    // failure is almost certainly the transition itself, not a
                    // generic visual-write error. This fires regardless of what
                    // the SDK message says.
                    if (!string.IsNullOrEmpty(currentFormType)
                        && !string.IsNullOrEmpty(incomingFormType)
                        && !string.Equals(currentFormType, incomingFormType, StringComparison.OrdinalIgnoreCase))
                    {
                        code = "FormTypeTransitionUnsupported";
                        hint = $"Form type transition detected ({currentFormType} → {incomingFormType}). " +
                               $"Visual writes that change the Form type only succeed when the request body is a COMPLETE target-type document: " +
                               $"a single <Form type=\"{incomingFormType}\"> root containing all children required by the target schema. " +
                               $"Partial patches and html→layout sub-tree edits are rejected by the SDK without diagnostics. " +
                               $"On WorkWithPlus KBs the layout body additionally requires the dual-form <detail><layout><table> wrapping — " +
                               $"see genexus_playbook topic=wwp_dual_form.";
                    }

                    if (hint == null && !string.IsNullOrEmpty(chain))
                    {
                        if (chain.IndexOf("marca (table) não corresponde", StringComparison.OrdinalIgnoreCase) >= 0
                            || chain.IndexOf("convertion of formId", StringComparison.OrdinalIgnoreCase) >= 0
                            || chain.IndexOf("WebLayoutHandler", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hint = "This KB requires the WorkWithPlus dual-form layout schema: " +
                                   "<Form id=\"1\" type=\"layout\"><detail><layout id=\"GUID\"><table controlName tableType=\"Responsive\" class=\"<themeGUID>-N\">...</table></layout></detail></Form>. " +
                                   "The flat-table body is rejected by WebLayoutHandler.LoadPanelElement. " +
                                   "Use genexus_create_popup (auto-detects WWP and emits the right schema) " +
                                   "or hand-roll the dual-form XML harvested from an existing layout-form WebPanel in the same KB.";
                        }
                        else if (chain.IndexOf("variable", StringComparison.OrdinalIgnoreCase) >= 0
                                 && chain.IndexOf("not declared", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hint = "The visual XML references a variable that doesn't exist on the object. " +
                                   "Add the variable first via genexus_add_variable, then retry the write.";
                        }
                        else if (chain.IndexOf("Form type", StringComparison.OrdinalIgnoreCase) >= 0
                                 && (chain.IndexOf("transition", StringComparison.OrdinalIgnoreCase) >= 0
                                     || chain.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            hint = "Form type transitions (html → layout) are not supported via patch/visual writes. " +
                                   "Use mode='full' with the COMPLETE target-type body (including the new <Form type=\"layout\"> root and all children).";
                        }
                    }

                    // Friction 2026-05-28 — when we have a typed code, replace the
                    // bare "Visual write failed" message with one that names the
                    // specific failure. LLMs that don't parse `code` (most of
                    // them) still see the actionable text in the human message.
                    string visualMessage = "Visual write failed";
                    if (string.Equals(code, "FormTypeTransitionUnsupported", StringComparison.Ordinal))
                    {
                        visualMessage = $"Form type transition not supported via this write path "
                            + $"({currentFormType ?? "?"} → {incomingFormType ?? "?"}). "
                            + "Use mode='full' with a complete target-type body.";
                    }
                    // Build nextSteps and extra from obj availability.
                    JArray visualNextSteps = new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = partName ?? "WebForm" },
                        why: "Re-read the current visual XML before retrying the write."));
                    var visualExtra = new JObject { ["part"] = partName };
                    if (!string.IsNullOrEmpty(chain)) visualExtra["details"] = chain;
                    if (!string.IsNullOrEmpty(currentFormType)) visualExtra["fromFormType"] = currentFormType;
                    if (!string.IsNullOrEmpty(incomingFormType)) visualExtra["toFormType"] = incomingFormType;
                    if (obj != null)
                    {
                        visualExtra["objectName"] = obj.Name;
                        visualExtra["objectType"] = obj.TypeDescriptor?.Name;
                        var apArr = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                        if (apArr.Length > 0) visualExtra["availableParts"] = new JArray(apArr);
                    }
                    return McpResponse.Err(
                        code: code ?? "VisualWriteFailed",
                        message: visualMessage,
                        hint: hint,
                        nextSteps: visualNextSteps,
                        target: target,
                        extra: visualExtra);
                }
            }
        }

        private static JObject BuildDetachedTransformationEvidence(
            WebFormTypedPropertyWriter.DescriptorProjectionResult projection)
        {
            var transformations = new JArray();
            if (projection?.Notices != null)
            {
                foreach (var notice in projection.Notices)
                {
                    transformations.Add(new JObject
                    {
                        ["element"] = notice.Element,
                        ["controlId"] = notice.ControlId,
                        ["source"] = notice.Source,
                        ["canonical"] = notice.Canonical,
                        ["action"] = notice.Action,
                        ["reason"] = notice.Reason
                    });
                }
            }

            var attributeChanges = new JArray();
            if (projection?.AttributeChanges != null)
            {
                foreach (var change in projection.AttributeChanges)
                {
                    attributeChanges.Add(new JObject
                    {
                        ["element"] = change.Element,
                        ["controlId"] = change.ControlId,
                        ["attribute"] = change.Attribute,
                        ["kind"] = change.Kind,
                        ["before"] = BoundedTransformationValue(change.Before),
                        ["after"] = BoundedTransformationValue(change.After),
                        ["canonical"] = change.Canonical
                    });
                }
            }

            string detachedHash = projection == null || projection.Xml == null
                ? null
                : ComputeContentFingerprint(projection.Xml);
            return new JObject
            {
                ["simulated"] = true,
                ["detached"] = true,
                ["legacyHtml"] = projection?.IsLegacyHtml ?? false,
                ["transformations"] = transformations,
                ["attributeChanges"] = attributeChanges,
                ["detachedXmlHash"] = detachedHash
            };
        }

        private static string BoundedTransformationValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= 240 ? value : value.Substring(0, 240) + "…[truncated]";
        }

        internal static void AttachVisualCommitEvidence(JObject response, bool committed, bool verified)
        {
            if (response == null) return;
            response["sdkSaveCompleted"] = committed;
            response["saved"] = committed;
            response["physicalCommit"] = committed;
            response["commitConfirmed"] = committed;
            response["commitState"] = committed ? "Committed" : "NotCommitted";
            response["persistenceState"] = verified ? "Verified" : committed ? "CommittedUnverified" : "NotPersisted";
            response["verified"] = verified;
            // For a committed visual write, persisted describes the physical
            // commit; persistedVerified/verified describe content equivalence.
            response["persisted"] = committed;
            response["persistedVerified"] = verified;
        }

        // Issue #830: a verification failure after the commit carries saved:true and persisted:true, which a caller
        // reads as "my layout is in the object". They report the physical commit of the layout the SDK kept; the
        // content verdict is persistedVerified. It says nothing about verifyDiff: the hint before it (BuildSuggestion) points
        // there, and a difference of an attribute value or of text leaves verifyDiff without a rejected list.
        internal const string VisualCommitMeaningHint =
            "saved and persisted report that the SDK committed the layout it kept, not that your content is in the object: "
            + "persistedVerified=false says the object does not hold what was requested.";

        /// <summary>
        /// The verification failure of a visual write as the caller receives it: the SDK's own save error, the commit evidence
        /// (the layout the SDK kept is committed even though the content does not verify), the hint, the rollback request and the
        /// restore step. Returns <paramref name="failureJson"/> untouched when it cannot be read.
        /// </summary>
        internal static string DecorateVisualVerificationFailure(string failureJson, JObject webFormPartSaveError, JObject forceSaveError,
            bool sdkCommitCompleted, bool rollbackOnFailure, JArray gotchas, string target, string partName)
        {
            try
            {
                var verifyJobj = JObject.Parse(failureJson);
                var errObj = verifyJobj["error"] as JObject ?? verifyJobj;
                AttachVisualSaveError(errObj, webFormPartSaveError, forceSaveError);
                if (sdkCommitCompleted)
                {
                    AttachVisualCommitEvidence(verifyJobj, committed: true, verified: false);
                    AttachVisualCommitEvidence(errObj, committed: true, verified: false);
                }
                AppendVisualVerificationGuidance(errObj, sdkCommitCompleted, gotchas);
                verifyJobj["rollbackRequested"] = rollbackOnFailure;
                AddVersioningRestoreStep(verifyJobj, target, partName);
                return verifyJobj.ToString();
            }
            catch
            {
                return failureJson;
            }
        }

        /// <summary>
        /// What a visual verification failure adds to its hint (issue #830): what saved/persisted mean after a commit, and the
        /// gotcha that the scan of the request found and the SDK then trimmed. The failure envelope carries no
        /// layoutGotchas of its own, so the gotcha is explained in the hint.
        /// </summary>
        internal static void AppendVisualVerificationGuidance(JObject errObj, bool sdkCommitCompleted, JArray gotchas)
        {
            if (sdkCommitCompleted) AppendVisualHint(errObj, VisualCommitMeaningHint);
            AppendVisualHint(errObj, GotchaGuidance(gotchas, GotchaCodes.GotchaGridConditionIgnored));
        }

        // The message and workaround of the first gotcha with the given code, as one hint, or null (issue #830).
        internal static string GotchaGuidance(JArray gotchas, string code)
        {
            if (gotchas == null) return null;
            foreach (var gotcha in gotchas.OfType<JObject>())
            {
                if (!string.Equals(gotcha["code"]?.ToString(), code, StringComparison.Ordinal)) continue;
                string text = ((gotcha["message"]?.ToString() ?? string.Empty) + " " + (gotcha["workaround"]?.ToString() ?? string.Empty)).Trim();
                return text.Length == 0 ? null : text;
            }
            return null;
        }

        internal static void AppendVisualHint(JObject errObj, string text)
        {
            if (errObj == null || string.IsNullOrEmpty(text)) return;
            string current = errObj["hint"]?.ToString();
            if (!string.IsNullOrEmpty(current) && current.IndexOf(text, StringComparison.Ordinal) >= 0) return;
            errObj["hint"] = string.IsNullOrEmpty(current) ? text : current + " " + text;
        }

        private const int VisualSaveErrorTextLimit = 500;

        private static readonly System.Text.RegularExpressions.Regex VisualSaveWindowsPath =
            new System.Text.RegularExpressions.Regex(
                @"(?:[A-Za-z]:\\|\\\\)[^\s'""<>|]+",
                System.Text.RegularExpressions.RegexOptions.Compiled,
                TimeSpan.FromSeconds(1));

        private static readonly System.Text.RegularExpressions.Regex VisualSaveAttributeRef =
            new System.Text.RegularExpressions.Regex(
                @"\batt:\d+\b",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));

        // One line, no local paths, bounded. SDK messages are the cause the caller needs, but they
        // can quote machine paths; those add nothing to the diagnosis.
        internal static string SanitizeVisualSaveText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string text;
            try { text = VisualSaveWindowsPath.Replace(value, "<path>"); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { text = value; }
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*[\r\n]+\s*", " ").Trim();
            return text.Length <= VisualSaveErrorTextLimit
                ? text
                : text.Substring(0, VisualSaveErrorTextLimit) + "…[truncated]";
        }

        // The SDK's refusal of a save call: exception type, the first inner message (the wrapper
        // around it is usually reflection noise) and the whole chain.
        internal static JObject BuildVisualSaveErrorEvidence(Exception ex, string where)
        {
            if (ex == null) return null;
            var first = ex.InnerException ?? ex;
            return new JObject
            {
                ["where"] = where,
                ["type"] = first.GetType().FullName,
                ["message"] = SanitizeVisualSaveText(first.Message),
                ["chain"] = SanitizeVisualSaveText(FormatExceptionChain(ex))
            };
        }

        // The mismatch text for a verification failure. An empty persisted side is reported as what
        // it is (the part could not be read back), not as a content difference.
        internal static string DescribeVisualVerificationMismatch(string persistedXml, string visualDiff, JObject sdkSaveError)
        {
            string diff = visualDiff ?? "n/a";
            if (string.IsNullOrWhiteSpace(persistedXml))
                diff += " The persisted WebForm could not be read back after the save, so what was persisted is unknown: read the part again before retrying.";

            if (sdkSaveError == null)
                return "The SDK save path completed, but the persisted WebForm XML does not match the requested content. Diff: " + diff;

            string type = sdkSaveError["type"]?.ToString();
            string message = sdkSaveError["message"]?.ToString();
            return "The SDK reported a save error (" + (string.IsNullOrEmpty(type) ? "unknown type" : type)
                + ": " + (string.IsNullOrEmpty(message) ? "no message" : message)
                + ") and the persisted WebForm XML does not match the requested content. Diff: " + diff;
        }

        // Actionable guidance for the commonest SDK refusal: a reference the KB cannot resolve.
        internal static string BuildVisualSaveRejectedHint(JObject sdkSaveError)
        {
            string message = sdkSaveError?["message"]?.ToString();
            if (string.IsNullOrWhiteSpace(message)) return null;

            System.Text.RegularExpressions.Match attribute;
            try { attribute = VisualSaveAttributeRef.Match(message); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { attribute = System.Text.RegularExpressions.Match.Empty; }
            bool unknown = message.IndexOf("desconhecido", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0;
            if (attribute.Success && unknown)
                // "Unknown" is what the SDK says when it cannot resolve the reference at that moment. It
                // is not proof that the id is absent: a valid att:<id> is refused the same way when the
                // SDK looks it up by NAME (the stored form replayed as if a person had typed it). So the
                // hint says what to check, not that the id is wrong.
                return "The SDK rejected the reference " + attribute.Value + " while saving. Either that attribute id does not exist in this KB, "
                    + "or the SDK resolved a stored att:<id> as an attribute name; check the id against the KB (genexus_structure get_visual) first. "
                    + "Also check the attribute ids in the XML you sent, including PATTERN_ELEMENT_CUSTOM_PROPERTIES and TokenDataList parameters, "
                    + "and read the part again to confirm what was persisted.";

            return "The SDK rejected the save; the error above is its own message. Read the part again to confirm what was persisted before retrying.";
        }

        // Puts the SDK's own save error on a failure envelope. webFormPart.Save() keeps its original
        // sdkSaveError slot; the object-level save error takes it when that one did not fail.
        internal static void AttachVisualSaveError(JObject errObj, JObject webFormPartSaveError, JObject forceSaveError)
        {
            if (errObj == null) return;
            var primary = webFormPartSaveError ?? forceSaveError;
            if (primary == null) return;

            errObj["sdkSaveError"] = primary;
            if (webFormPartSaveError != null && forceSaveError != null) errObj["forceSaveError"] = forceSaveError;
            errObj["sdkSaveRejected"] = true;

            string hint = BuildVisualSaveRejectedHint(forceSaveError ?? webFormPartSaveError);
            if (!string.IsNullOrEmpty(hint))
            {
                string current = errObj["hint"]?.ToString();
                errObj["hint"] = string.IsNullOrEmpty(current) ? hint : current + " " + hint;
            }
        }

        internal static void AddVersioningRestoreStep(JObject response, string target, string partName)
        {
            if (response == null) return;
            var error = response["error"] as JObject ?? response;
            var steps = error["nextSteps"] as JArray ?? new JArray();
            bool alreadyPresent = steps.OfType<JObject>().Any(step =>
                string.Equals(step["tool"]?.ToString(), "genexus_versioning", StringComparison.OrdinalIgnoreCase)
                && string.Equals(step["args"]?["action"]?.ToString(), "history_restore", StringComparison.OrdinalIgnoreCase));
            if (!alreadyPresent)
            {
                steps.Add(new JObject
                {
                    ["tool"] = "genexus_versioning",
                    ["args"] = new JObject
                    {
                        ["action"] = "history_restore",
                        ["name"] = target,
                        ["part"] = string.IsNullOrWhiteSpace(partName) ? "WebForm" : partName,
                        ["discard"] = true
                    },
                    ["why"] = "Restore the pre-write KB snapshot after a committed visual verification mismatch."
                });
            }
            error["nextSteps"] = steps;
            // Keep a top-level alias for legacy clients that looked for nextSteps
            // outside the canonical error object.
            if (response["nextSteps"] == null) response["nextSteps"] = steps;
        }

        // Walks ex.InnerException so the deepest message — usually the real SDK
        // diagnostic — ends up in the response. Outer wrappers are still surfaced
        // but only when they add information beyond the inner message.
        internal static string TryExtractFormType(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                var node = doc.SelectSingleNode("//Form[@type]") as System.Xml.XmlElement;
                return node?.GetAttribute("type");
            }
            catch
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    xml,
                    @"<Form\b[^>]*\btype\s*=\s*[""']([^""']+)[""']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return match.Success ? match.Groups[1].Value : null;
            }
        }

        internal static string FormatExceptionChain(Exception ex)
        {
            if (ex == null) return null;
            var sb = new System.Text.StringBuilder();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            Exception cur = ex;
            while (cur != null)
            {
                string typeName = cur.GetType().Name;
                string msg = cur.Message?.Trim();
                if (!string.IsNullOrEmpty(msg) && seen.Add(msg))
                {
                    if (sb.Length > 0) sb.Append(" -> ");
                    sb.Append(typeName).Append(": ").Append(msg);
                }
                cur = cur.InnerException;
                if (seen.Count > 5) break;
            }
            return sb.Length == 0 ? ex.Message : sb.ToString();
        }

    }
}
