using System;
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
            try
            {
                if (obj == null) return null;
                if (!WebFormXmlHelper.IsVisualPart(partName)) return null;

                var resolved = _patternAnalysisService.ResolveWWPInstance(obj);
                // Fallback for generated WW family (WW<Trn>, View<Trn>, etc.) whose host
                // is `WorkWithPlus<TrnBaseName>` rather than `WorkWithPlus<obj.Name>`.
                if (resolved == null && !string.IsNullOrEmpty(obj.Name))
                {
                    string[] candidatePrefixes = { "WW", "View", "ViewWW", "Prompt" };
                    foreach (var pre in candidatePrefixes)
                    {
                        if (!obj.Name.StartsWith(pre, StringComparison.Ordinal)) continue;
                        string baseName = obj.Name.Substring(pre.Length);
                        if (string.IsNullOrEmpty(baseName)) continue;
                        var host = _objectService.FindObject("WorkWithPlus" + baseName);
                        if (host != null && string.Equals(host.TypeDescriptor?.Name, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
                        {
                            resolved = host;
                            break;
                        }
                    }
                }
                if (resolved == null) return null;

                var part = _patternAnalysisService.FindPatternPart(resolved, "PatternInstance");
                if (part == null) return null;

                return new JArray
                {
                    new JObject
                    {
                        ["code"] = "EditingWebFormUnderPattern",
                        ["severity"] = "warning",
                        ["message"] =
                            "This object is covered by a WorkWithPlus PatternInstance ('" + resolved.Name +
                            "'). Hand edits to " + partName + " can be overwritten on the next pattern apply/save. " +
                            "Consider editing part=PatternInstance instead (genexus_edit name=" + resolved.Name +
                            " part=PatternInstance ...). Toggle SDPlus_Editor_Apply_On_Save=False on " + resolved.Name +
                            " if you must keep a hard override on the visual part.",
                        ["patternInstance"] = resolved.Name
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.Debug("[WriteVisualPart] PatternShadow warning probe skipped: " + ex.Message);
                return null;
            }
        }

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

        private string WriteVisualPart(global::Artech.Architecture.Common.Objects.KBObject obj, string target, string partName, string xml, bool dryRun = false, bool strictVerify = true)
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

            try
            {
                string currentXml = WebFormXmlHelper.ReadEditableXml(obj);
                currentFormType = TryExtractFormType(currentXml);
                incomingFormType = TryExtractFormType(normalizedInput);
                if (XmlEquivalence.AreEquivalent(currentXml, normalizedInput, out _))
                {
                    var noChangeResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = dryRun ? "Dry-run: no change would be applied." : "No change"
                    };
                    var noChangeHtmlWarnings = BuildHtmlFormatWarnings(prospectiveGotchas);
                    AttachWarnings(noChangeResp, MergeWarnings(patternShadowWarnings, noChangeHtmlWarnings));
                    if (prospectiveGotchas != null) noChangeResp["layoutGotchas"] = prospectiveGotchas;
                    return Models.McpResponse.Ok(target: target, code: "WriteNoChange", result: noChangeResp);
                }
                if (dryRun)
                {
                    var dryResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = "Dry-run: input parsed and would update visual XML. Save skipped.",
                        ["verified"] = new JArray("xmlParse", "layoutGotchas", "diffVsCurrent"),
                        ["savePathExercised"] = false
                    };
                    var dryHtmlWarnings = BuildHtmlFormatWarnings(prospectiveGotchas);
                    AttachWarnings(dryResp, MergeWarnings(patternShadowWarnings, dryHtmlWarnings));
                    var suspects = GxMcp.Worker.Helpers.WebFormSchemaHints.ScanForRejectedAttributes(normalizedInput);
                    if (suspects.Count > 0)
                    {
                        var arr = new JArray();
                        foreach (var s in suspects)
                            arr.Add(new JObject { ["element"] = s.Element, ["attribute"] = s.Attribute, ["reason"] = s.Reason });
                        dryResp["preflightWarnings"] = arr;
                        dryResp["warning"] = "Dry-run detected " + suspects.Count + " attribute(s) likely to be sanitised by the SDK on save. See preflightWarnings.";
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

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return CreateWriteError("KB not opened", target, partName, "Open a Knowledge Base before writing visual metadata.", obj);
            }

            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    WebFormXmlHelper.ApplyEditableXml(webFormPart, normalizedInput);

                    // ── DIAGNOSTIC: byte-level state RIGHT BEFORE obj.Save ────────────────
                    Helpers.WebFormSaveDiagnostics.DumpState(webFormPart, obj, "BEFORE-SAVE");

                    JObject webFormPartSaveError = null;
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
                        if (!XmlEquivalence.AreEquivalent(persistedXml, normalizedInput, out var visualDiff, out var visualStructured))
                        {
                            if (webFormPartSaveError != null)
                                Logger.Info("[VisualWrite] webFormPart.Save() error captured during verification failure: " + webFormPartSaveError.ToString(Newtonsoft.Json.Formatting.None));
                            string visualVerifyErr = CreateWriteError(
                                "Visual write verification failed",
                                target,
                                partName,
                                "The SDK save path completed, but the persisted WebForm XML does not match the requested content. Diff: " + (visualDiff ?? "n/a"),
                                obj,
                                visualStructured);
                            // Attach snapshot descriptor + restore hint so the caller can roll back.
                            try
                            {
                                var verifyJobj = JObject.Parse(visualVerifyErr);
                                var errObj = verifyJobj["error"] as JObject ?? verifyJobj;
                                if (webFormPartSaveError != null) errObj["sdkSaveError"] = webFormPartSaveError;
                                // Inject restore nextStep into nextSteps array.
                                var nsArr = verifyJobj["nextSteps"] as JArray ?? new JArray();
                                nsArr.Add(new JObject
                                {
                                    ["tool"] = "genexus_history",
                                    ["args"] = new JObject { ["action"] = "restore", ["discard"] = true, ["target"] = target },
                                    ["why"] = "Restore to the pre-write snapshot to undo the failed visual write."
                                });
                                verifyJobj["nextSteps"] = nsArr;
                                return verifyJobj.ToString();
                            }
                            catch
                            {
                                return visualVerifyErr;
                            }
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
                            errObj["sdkSaveError"] = webFormPartSaveError;
                            var nsArr = sdkErrJobj["nextSteps"] as JArray ?? new JArray();
                            nsArr.Add(new JObject
                            {
                                ["tool"] = "genexus_history",
                                ["args"] = new JObject { ["action"] = "restore", ["discard"] = true, ["target"] = target },
                                ["why"] = "Restore to the pre-write snapshot to undo the failed visual write."
                            });
                            sdkErrJobj["nextSteps"] = nsArr;
                            return sdkErrJobj.ToString();
                        }
                        catch { return sdkErr; }
                    }

                    var okResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = strictVerify
                            ? "Visual XML updated and verified."
                            : "Visual XML updated (post-write verify skipped: validate=best-effort). Build to confirm generation."
                    };
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

        // Walks ex.InnerException so the deepest message — usually the real SDK
        // diagnostic — ends up in the response. Outer wrappers are still surfaced
        // Friction 2026-05-28 — surface the PatternChildOrderReconciler
        // report on both DryRun and verify-failed envelopes so the caller
        // sees which parents the reconciler had to fix (or skip) without
        // needing live worker logs. validate=only callers rely on this to
        // catch malformed childrenOrderedList before paying for a write.
        private static void AttachReconcileReport(
            JObject envelope,
            GxMcp.Worker.Helpers.PatternChildOrderReconciler.Report report)
        {
            if (envelope == null || report == null || !report.HasContent) return;
            var jo = new JObject
            {
                ["parentsUpdated"] = report.ParentsUpdated
            };
            if (report.Changes != null && report.Changes.Count > 0)
            {
                jo["changes"] = new JArray(report.Changes);
            }
            if (report.Skips != null && report.Skips.Count > 0)
            {
                jo["skips"] = new JArray(report.Skips);
                jo["skipsHint"] = "Reconciler refused to rebuild childrenOrderedList for these parents — the XML is missing identifiers (controlName/Name/attribute) or has an unknown child kind. Fix those entries before retrying.";
            }
            envelope["childOrderReconcile"] = jo;
        }

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
