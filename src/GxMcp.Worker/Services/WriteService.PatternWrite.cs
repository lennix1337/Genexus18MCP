using System;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    // Pattern-part write path (WritePatternPart + DeserializeDataFrom application
    // helpers) extracted from WriteService.cs (plan 007). Pure move, no logic changes
    // — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        private string WritePatternPart(global::Artech.Architecture.Common.Objects.KBObject obj, string target, string partName, string xml, bool dryRun = false, bool strictVerify = true)
        {
            string normalizedInput;
            GxMcp.Worker.Helpers.PatternChildOrderReconciler.Report reconcileReport;
            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                // Auto-reconcile childrenOrderedList so callers (LLMs) can add/remove/reorder
                // children purely by editing XML — the helper rebuilds each parent's
                // ordering attribute from the live child tree. Without this, an LLM that
                // adds a <textBlock> but forgets to update childrenOrderedList ships a
                // technically-valid XML that the IDE renders incorrectly. See
                // src/GxMcp.Worker/Helpers/PatternChildOrderReconciler.cs for the rules.
                reconcileReport = GxMcp.Worker.Helpers.PatternChildOrderReconciler.Reconcile(doc);
                if (reconcileReport.ParentsUpdated > 0)
                {
                    Logger.Info("[PATTERN-WRITE] Auto-reconciled childrenOrderedList on " + reconcileReport.ParentsUpdated + " parent(s).");
                    foreach (var change in reconcileReport.Changes)
                    {
                        Logger.Info("[PATTERN-WRITE]   " + change);
                    }
                }
                foreach (var skip in reconcileReport.Skips)
                {
                    Logger.Warn("[PATTERN-WRITE]   skipped: " + skip);
                }
                normalizedInput = doc.ToString();
            }
            catch (Exception ex)
            {
                return CreateWriteError("Invalid pattern XML", target, partName, ex.Message, obj, code: "PatternInvalidXml");
            }

            try
            {
                string currentXml = _patternAnalysisService.ReadPatternPartXml(obj, partName, out _, out _);
                if (XmlEquivalence.AreEquivalent(currentXml, normalizedInput, out _))
                {
                    return Models.McpResponse.Ok(
                        target: target,
                        code: "WriteNoChange",
                        result: new JObject
                        {
                            ["part"] = partName,
                            ["details"] = dryRun ? "Dry-run: no change would be applied." : "No change"
                        });
                }
                if (dryRun)
                {
                    var dryResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = "Dry-run: input parsed and would update pattern XML. Save skipped.",
                        ["verified"] = new JArray("xmlParse", "childrenOrderedList", "diffVsCurrent"),
                        ["savePathExercised"] = false
                    };
                    if (string.Equals(partName, "PatternInstance", StringComparison.OrdinalIgnoreCase))
                    {
                        dryResp["warning"] = "Dry-run verified XML structure and diff against current pattern. Note: WorkWithPlus pattern saves can still be rejected by the WWP validator on save.";
                    }
                    AttachReconcileReport(dryResp, reconcileReport);
                    return Models.McpResponse.Ok(target: target, code: "WriteDryRun", result: dryResp);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[DEBUG-SAVE] Pattern no-change precheck skipped: " + ex.Message);
                if (dryRun)
                {
                    return CreateWriteError(
                        "Pattern dry-run precheck failed",
                        target,
                        partName,
                        "Dry-run input parsed, but reading current pattern failed (" + ex.Message + "). State comparison skipped.",
                        obj,
                        code: "PatternReadFailed");
                }
            }

            LogRequestedPatternPayloadIfEnabled(normalizedInput);

            try
            {
                var preXml = _patternAnalysisService.ReadPatternPartXml(obj, partName, out _, out _);
                if (!string.IsNullOrWhiteSpace(preXml))
                {
                    var snap = PatternSnapshotStore.SaveSnapshot(obj.Guid.ToString(), partName, preXml);
                    if (!string.IsNullOrEmpty(snap)) Logger.Debug("[PatternSnapshot] Saved pre-write snapshot: " + snap);
                }
            }
            catch (Exception ex) { Logger.Debug("[PatternSnapshot] skipped: " + ex.Message); }

            var envelope = _patternAnalysisService.BuildPatternPartEnvelope(obj, partName, normalizedInput, out var resolvedObject, out var resolvedPart);
            if (resolvedObject == null || resolvedPart == null || string.IsNullOrWhiteSpace(envelope))
            {
                return CreateWriteError(
                    "Pattern part not found",
                    target,
                    partName,
                    "The authoritative WorkWithPlus pattern part could not be resolved for writing.",
                    obj,
                    code: "PatternPartNotFound");
            }

            LogPatternDiagnosticsIfEnabled(obj, resolvedObject, resolvedPart, normalizedInput);

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return CreateWriteError("KB not opened", target, partName, "Open a Knowledge Base before writing pattern metadata.", obj);
            }

            // Friction 2026-05-26 — the part-level Save() exception used to be
            // silently swallowed, so verification failures had no SDK trace to
            // attach. We now capture the first non-null throw and bubble it on
            // the verify-failed envelope as `sdkSaveError` so the agent sees the
            // actual SDK rejection (typically a property/validator complaint)
            // instead of just "Pattern write verification failed".
            JObject sdkSaveError = null;
            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    LogPatternValidationState("before apply", resolvedObject);
                    // The SDK round-trip is: SerializeToXml() -> <Part><Data><![CDATA[<instance>...]]></Data></Part>.
                    // DeserializeFromXml expects the same wrapper. Passing the bare <instance>...</instance>
                    // (innerXml / normalizedInput) caused the SDK to ignore the payload, leaving the part
                    // unchanged in memory — Save() was a no-op and persistedHash kept matching the pre-write
                    // value. BuildPatternPartEnvelope already produced the wrapped form; route it through.
                    ApplyPatternEnvelope(resolvedPart, envelope, normalizedInput);
                    LogPatternInMemoryStateIfEnabled(obj, resolvedPart, partName, normalizedInput);
                    LogPatternValidationState("after apply before presave", resolvedObject);
                    RunPatternPreSaveExperimentIfEnabled(resolvedObject, resolvedPart, normalizedInput);

                    try
                    {
                        resolvedPart.Save();
                    }
                    catch (Exception partSaveEx)
                    {
                        // Capture; do not rethrow — the outer obj.Save(prefs)
                        // path may still succeed (it has different bookkeeping)
                        // and we want the verification step to be the source of
                        // truth for "did the bytes actually land". The captured
                        // error is attached only when verification fails.
                        var rootEx = partSaveEx.InnerException ?? partSaveEx;
                        sdkSaveError = new JObject
                        {
                            ["type"] = rootEx.GetType().FullName,
                            ["message"] = rootEx.Message,
                            ["where"] = "resolvedPart.Save()"
                        };
                        var chain = FormatExceptionChain(partSaveEx);
                        if (!string.IsNullOrEmpty(chain) && !string.Equals(chain, rootEx.Message, StringComparison.Ordinal))
                        {
                            sdkSaveError["chain"] = chain;
                        }
                        Logger.Info("[PATTERN-WRITE] resolvedPart.Save() threw: " + rootEx.GetType().Name + ": " + rootEx.Message + " — captured for verify-failed envelope.");
                    }

                    // KBObjectManager.PrepareSave silently skips persistence when kbObject.Mode == Mode.Unchanged.
                    // DeserializeFromXml mutates internal state but does NOT propagate to obj.Mode in the
                    // headless worker, so EnsureSave(true) was a no-op (persistedHash stayed identical across
                    // runs). Mirror the WriteVisualPart fix (line ~1924): invalidate the part's last-modification
                    // snapshot so dirty tracking notices, then call obj.Save(ForceSave=true) which bypasses the
                    // Mode.Unchanged short-circuit — same path the SDK uses for generated objects.
                    InvalidatePartLastModification(resolvedPart);

                    if (!TryPatternDirectSaveExperiment(resolvedObject))
                    {
                        bool forceSaved = false;
                        try
                        {
                            var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                            {
                                ForceSave = true,
                                ForceSaveDefaultParts = true,
                                SkipValidation = true
                            };
                            resolvedObject.Save(prefs);
                            forceSaved = true;
                            Logger.Info("[PATTERN-WRITE] resolvedObject.Save(KBObjectSavePreferences{ForceSave=true}) completed.");
                        }
                        catch (Exception fsEx)
                        {
                            var inner = fsEx.InnerException ?? fsEx;
                            Logger.Info("[PATTERN-WRITE] ForceSave threw: " + inner.GetType().Name + ": " + inner.Message + " — falling back to EnsureSave(true).");
                        }

                        if (!forceSaved)
                        {
                            resolvedObject.EnsureSave(true);
                        }
                    }
                    transaction.Commit();
                    // Force synchronous flush so the bytes hit disk before the verification read; the default
                    // timer-based ScheduleFlush() can lose writes if the worker is recycled before it fires.
                    ScheduleFlush(force: true);

                    // Async follow-up (next-major item #2): ReadPatternPartXml + the XML diff below
                    // dominate wall-clock on large PatternInstance writes and are the cause of client
                    // timeouts. validate="best-effort" (strictVerify=false) skips them; a real SDK save
                    // error captured above is still surfaced. Commit + forced flush already ran.
                    global::Artech.Architecture.Common.Objects.KBObject refreshedObject = null;
                    if (strictVerify)
                    {
                    string persistedXml = _patternAnalysisService.ReadPatternPartXml(obj, partName, out refreshedObject, out _);

                    if (!XmlEquivalence.AreEquivalent(persistedXml, normalizedInput, out var patternDiff, out var patternStructured))
                    {
                        // Friction 2026-05-25 item #5 — return rich diagnostics
                        // so the agent can see exactly what the SDK normalised
                        // away. Without persisted/requested snippets the agent
                        // had to guess which attribute/child was rejected; with
                        // them it can do a textual compare and fix the next
                        // call. Keep snippets capped (~800 chars) so they
                        // survive the TrimErrorEnvelope allowlist without
                        // blowing the wire budget.
                        const int snippetCap = 800;
                        string persistedSnippet = string.IsNullOrEmpty(persistedXml)
                            ? null
                            : (persistedXml.Length > snippetCap ? persistedXml.Substring(0, snippetCap) + "…[truncated]" : persistedXml);
                        string requestedSnippet = string.IsNullOrEmpty(normalizedInput)
                            ? null
                            : (normalizedInput.Length > snippetCap ? normalizedInput.Substring(0, snippetCap) + "…[truncated]" : normalizedInput);

                        var verifyErr = CreateWriteError(
                            "Pattern write verification failed",
                            target,
                            partName,
                            "The SDK save path completed, but the persisted WorkWithPlus pattern XML does not match the requested content. Compare 'persistedSnippet' (what the SDK kept) vs 'requestedSnippet' (what you sent) to see which attribute/child was sanitised. Diff: " + (patternDiff ?? "n/a"),
                            refreshedObject ?? resolvedObject,
                            patternStructured,
                            code: "PatternVerificationMismatch");
                        // Inject snippets + the captured SDK save exception into
                        // the error sub-object so the canonical envelope shape is maintained.
                        // sdkSaveError is where the actual SDK validator complaint lives when
                        // the part-level Save() threw before the outer obj.Save(prefs) succeeded.
                        try
                        {
                            var verifyJobj = JObject.Parse(verifyErr);
                            var errObj = verifyJobj["error"] as JObject;
                            if (errObj != null)
                            {
                                if (persistedSnippet != null) errObj["persistedSnippet"] = persistedSnippet;
                                if (requestedSnippet != null) errObj["requestedSnippet"] = requestedSnippet;
                                if (sdkSaveError != null) errObj["sdkSaveError"] = sdkSaveError;
                                // Move verifyDiff from top-level into error sub-object per v2.8.0 spec.
                                if (verifyJobj["verifyDiff"] != null && errObj["verifyDiff"] == null)
                                {
                                    errObj["verifyDiff"] = verifyJobj["verifyDiff"];
                                    verifyJobj.Remove("verifyDiff");
                                }
                                // Move childOrderReconcile into error when verification failed.
                                if (verifyJobj["childOrderReconcile"] != null && errObj["childOrderReconcile"] == null)
                                {
                                    errObj["childOrderReconcile"] = verifyJobj["childOrderReconcile"];
                                    verifyJobj.Remove("childOrderReconcile");
                                }
                                // Attach reconcile report under error.childOrderReconcile.
                                if (reconcileReport != null && reconcileReport.HasContent && errObj["childOrderReconcile"] == null)
                                {
                                    var rcObj = new JObject { ["parentsUpdated"] = reconcileReport.ParentsUpdated };
                                    if (reconcileReport.Changes != null && reconcileReport.Changes.Count > 0)
                                        rcObj["changes"] = new JArray(reconcileReport.Changes);
                                    if (reconcileReport.Skips != null && reconcileReport.Skips.Count > 0)
                                    {
                                        rcObj["skips"] = new JArray(reconcileReport.Skips);
                                        rcObj["skipsHint"] = "Reconciler refused to rebuild childrenOrderedList for these parents.";
                                    }
                                    errObj["childOrderReconcile"] = rcObj;
                                }
                            }
                            else
                            {
                                // Fallback: attach at top level if error obj is missing.
                                if (persistedSnippet != null) verifyJobj["persistedSnippet"] = persistedSnippet;
                                if (requestedSnippet != null) verifyJobj["requestedSnippet"] = requestedSnippet;
                                if (sdkSaveError != null) verifyJobj["sdkSaveError"] = sdkSaveError;
                                AttachReconcileReport(verifyJobj, reconcileReport);
                            }
                            // Inject restore nextStep so the caller knows how to roll back.
                            {
                                var nsArr = verifyJobj["nextSteps"] as JArray ?? new JArray();
                                nsArr.Add(new JObject
                                {
                                    ["tool"] = "genexus_history",
                                    ["args"] = new JObject { ["action"] = "restore", ["discard"] = true, ["target"] = target },
                                    ["why"] = "Restore to the pre-write snapshot to undo the failed pattern write."
                                });
                                verifyJobj["nextSteps"] = nsArr;
                            }
                            return verifyJobj.ToString();
                        }
                        catch
                        {
                            return verifyErr;
                        }
                    }
                    } // end if (strictVerify)
                    else if (sdkSaveError != null)
                    {
                        // best-effort: skip the diff but never swallow a real SDK save failure.
                        var sdkErr = CreateWriteError(
                            "Pattern write reported an SDK save error",
                            target,
                            partName,
                            "The SDK save path returned an error (post-write XML diff was skipped because validate=best-effort).",
                            resolvedObject,
                            null,
                            code: "PatternVerificationMismatch");
                        try
                        {
                            var sdkErrJobj = JObject.Parse(sdkErr);
                            var errObj = sdkErrJobj["error"] as JObject ?? sdkErrJobj;
                            errObj["sdkSaveError"] = sdkSaveError;
                            var nsArr = sdkErrJobj["nextSteps"] as JArray ?? new JArray();
                            nsArr.Add(new JObject
                            {
                                ["tool"] = "genexus_history",
                                ["args"] = new JObject { ["action"] = "restore", ["discard"] = true, ["target"] = target },
                                ["why"] = "Restore to the pre-write snapshot to undo the failed pattern write."
                            });
                            sdkErrJobj["nextSteps"] = nsArr;
                            return sdkErrJobj.ToString();
                        }
                        catch { return sdkErr; }
                    }

                    var success = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = strictVerify
                            ? "Pattern XML updated and verified."
                            : "Pattern XML updated (post-write verify skipped: validate=best-effort). Build to confirm generation."
                    };

                    if (resolvedObject.Guid != obj.Guid)
                    {
                        success["resolvedObject"] = resolvedObject.Name;
                        success["resolvedType"] = resolvedObject.TypeDescriptor?.Name;
                    }

                    // Friction 2026-05-26 — re-assert "Apply this pattern on
                    // save" on the WorkWithPlus host. The raw obj.Save(prefs)
                    // path above (ForceSave=true, SkipValidation=true) clears
                    // the flag the IDE renders as a checkbox, so every MCP edit
                    // used to leave the next IDE session with the checkbox
                    // unchecked. PatternApplyService does this on first apply /
                    // reapply via the same helper; mirror it here so edits via
                    // genexus_edit part=PatternInstance keep the IDE state
                    // intact. Best-effort: missing WWP package, foreign host
                    // types, or SDK miss → flag left as-is, logged at info.
                    bool applyOnSaveReenabled = false;
                    if (string.Equals(resolvedObject.TypeDescriptor?.Name, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            applyOnSaveReenabled = GxMcp.Worker.Helpers.WwpApplyOnSaveHelper.TryEnable(resolvedObject);
                        }
                        catch (Exception aosEx)
                        {
                            Logger.Debug("[APPLY-ON-SAVE] WritePatternPart post-save helper threw: " + aosEx.Message);
                        }
                        success["applyOnSaveReenabled"] = applyOnSaveReenabled;
                    }

                    // F18 (auto-project): when we just wrote a PatternInstance on a
                    // WorkWithPlus host, run the IDE's projection step so the bound
                    // parent's WebForm reflects the edit. Without this, the edit
                    // persists but the WebPanel layout never updates until the next
                    // explicit apply_pattern call. Best-effort — projection failures
                    // don't roll back the edit (the part is already saved).
                    if (string.Equals(resolvedObject.TypeDescriptor?.Name, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var parentForProjection = WwpProjectionHelper.ResolveHostParent(resolvedObject, _objectService);
                            if (parentForProjection != null)
                            {
                                bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parentForProjection, resolvedObject);
                                if (projected)
                                {
                                    success["projection"] = new JObject
                                    {
                                        ["projectionStatus"] = "Projected",
                                        ["parent"] = parentForProjection.Name,
                                        ["parentType"] = parentForProjection.TypeDescriptor?.Name,
                                        ["note"] = "PatternInstance edit was auto-projected onto the parent's WebForm via IPatternBuildProcess.UpdateParentObject."
                                    };

                                    // F21: refresh index cache so list_objects/inspect/query
                                    // see the parent's new state immediately (avoid the
                                    // stale-index window that bit us in F9). Best-effort.
                                    try
                                    {
                                        var idx = _objectService?.GetKbService()?.GetIndexCache();
                                        if (idx != null)
                                        {
                                            var refreshed = _objectService.FindObject(parentForProjection.Name);
                                            if (refreshed != null) idx.UpdateEntry(refreshed);
                                        }
                                    }
                                    catch (Exception idxEx) { Logger.Debug("[WWP-PROJECT] post-project index sync skipped: " + idxEx.Message); }
                                }
                                else
                                {
                                    success["projection"] = new JObject
                                    {
                                        ["projectionStatus"] = "Skipped",
                                        ["parent"] = parentForProjection.Name,
                                        ["note"] = "Projection helper declined — see worker log for details. Edit was persisted; WebForm may need a manual apply_pattern to refresh."
                                    };
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug("[WWP-PROJECT] auto-project on edit skipped: " + ex.Message);
                        }
                    }

                    // Echo the auto-reconcile report so LLM callers can see exactly
                    // which childrenOrderedList values the MCP rewrote and why. The IDE
                    // hides children that aren't listed; if a caller forgot to update
                    // the attribute by hand, this block tells them the MCP did it for
                    // them (or, with `skips`, that it could NOT and the layout may not
                    // render — that's an actionable signal).
                    if (reconcileReport.HasContent)
                    {
                        var ordering = new JObject
                        {
                            ["parentsUpdated"] = reconcileReport.ParentsUpdated,
                            ["explanation"] = "WorkWithPlus uses `childrenOrderedList` on each container element to drive IDE rendering order. The MCP rebuilds it from the XML's actual child order so callers only need to place elements where they want them in the tree."
                        };
                        if (reconcileReport.Changes.Count > 0)
                        {
                            ordering["changes"] = new JArray(reconcileReport.Changes);
                        }
                        if (reconcileReport.Skips.Count > 0)
                        {
                            ordering["skipped"] = new JArray(reconcileReport.Skips);
                            ordering["willRender"] = false;
                            ordering["skipNote"] = "These parents were left untouched because their childrenOrderedList could not be inferred safely; the affected children may not render in the IDE until the list is corrected manually.";
                            // issue #36.3 — escalate the skip from a footnote to a top-level
                            // warning: the write persisted but the affected controls will NOT
                            // render in the IDE, which is an actionable failure the caller
                            // must see, not a note buried in the reconciliation block.
                            success["warning"] = "Layout persisted, but " + reconcileReport.Skips.Count +
                                " container(s) could not be reconciled and their controls will NOT render in the IDE. Give the container a name/title, or fix its childrenOrderedList in the IDE. See childrenOrderedListReconciliation.skipped.";
                        }
                        success["childrenOrderedListReconciliation"] = ordering;
                    }

                    return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: success);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return CreateWriteError("Pattern write failed", target, partName, ex.Message, resolvedObject ?? obj, code: "PatternSaveFailed");
                }
            }
        }

        // Public so PatternApplyService.TryDirectAttachPatternInstance can reuse the
        // same Dirty/Mode bookkeeping when seeding a freshly-constructed PatternInstance.
        public static void ForcePatternPartDirty(global::Artech.Architecture.Common.Objects.KBObjectPart part) => InvalidatePartLastModification(part);

        // Public for the same reason — direct-attach needs to seed the pattern data
        // via the same DeserializeDataFrom(XmlElement) path used by the regular write.
        public static bool ApplyPatternDataFromXml(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string innerXml) =>
            TryApplyPatternDataFromXml(resolvedPart, innerXml);

        private static void InvalidatePartLastModification(global::Artech.Architecture.Common.Objects.KBObjectPart part)
        {
            if (part == null) return;

            // PatternInstancePart does not implement InvalidateLastModification (the WebForm
            // dirty-tracking hook) and does not expose any string setter ("Source",
            // "EditableContent", "InstanceXml") — only DeserializeFromXml(string), which
            // mutates internal state but leaves the part's Mode = Unchanged. Without setting
            // Dirty/Mode the SDK's KBObjectManager.PrepareSave short-circuits even under
            // KBObjectSavePreferences.ForceSave because the entity-level dirty check on the
            // PART (not the object) wins. Force both flags explicitly.
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var invalidate = part.GetType().GetMethod("InvalidateLastModification", flags, null, Type.EmptyTypes, null);
                if (invalidate != null)
                {
                    invalidate.Invoke(part, null);
                    Logger.Info("[PATTERN-WRITE] Invoked InvalidateLastModification on pattern part.");
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-WRITE] InvalidateLastModification skipped: " + ex.Message);
            }

            try
            {
                var dirtyProp = part.GetType().GetProperty("Dirty", flags);
                if (dirtyProp != null && dirtyProp.CanWrite && dirtyProp.PropertyType == typeof(bool))
                {
                    dirtyProp.SetValue(part, true);
                    Logger.Info("[PATTERN-WRITE] Forced part.Dirty = true.");
                }
            }
            catch (Exception ex) { Logger.Info("[PATTERN-WRITE] Set Dirty=true skipped: " + ex.Message); }

            try
            {
                var modeProp = part.GetType().GetProperty("Mode", flags);
                if (modeProp != null && modeProp.CanWrite)
                {
                    // Mode is an enum: Unchanged | Added | Modified | Deleted. Lift to Modified.
                    var modified = Enum.Parse(modeProp.PropertyType, "Modified", ignoreCase: true);
                    modeProp.SetValue(part, modified);
                    Logger.Info("[PATTERN-WRITE] Forced part.Mode = Modified.");
                }
            }
            catch (Exception ex) { Logger.Info("[PATTERN-WRITE] Set Mode=Modified skipped: " + ex.Message); }
        }

        private static bool TryApplyPatternDataFromXml(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string innerXml)
        {
            if (resolvedPart == null || string.IsNullOrWhiteSpace(innerXml)) return false;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var method = resolvedPart.GetType()
                    .GetMethods(flags)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, "DeserializeDataFrom", StringComparison.Ordinal) &&
                        m.GetParameters().Length == 1 &&
                        typeof(System.Xml.XmlElement).IsAssignableFrom(m.GetParameters()[0].ParameterType));
                if (method == null)
                {
                    Logger.Info("[PATTERN-WRITE] DeserializeDataFrom(XmlElement) not found on " + resolvedPart.GetType().FullName);
                    return false;
                }

                // SerializeDataTo(XmlElement target) writes the pattern data AS CHILDREN of target.
                // The inverse, DeserializeDataFrom, reads from the same shape — i.e. expects to be
                // passed the PARENT element whose first/only child is the <instance>. Passing
                // <instance> directly causes the SDK to read its (non-existent) <instance> child
                // and persist an empty <instance/>. Wrap the innerXml in a transient parent;
                // use a temp doc for parsing then ImportNode (preserving whitespace would inject
                // XmlWhitespace nodes that fail the SDK's strict XmlElement cast).
                var sourceDoc = new System.Xml.XmlDocument { PreserveWhitespace = false };
                sourceDoc.LoadXml(innerXml);

                var hostDoc = new System.Xml.XmlDocument { PreserveWhitespace = false };
                var parent = hostDoc.CreateElement("Data");
                hostDoc.AppendChild(parent);
                var imported = hostDoc.ImportNode(sourceDoc.DocumentElement, deep: true);
                parent.AppendChild(imported);

                method.Invoke(resolvedPart, new object[] { parent });
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Warn("[PATTERN-WRITE] DeserializeDataFrom(XmlElement) threw: " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
        }

        private void ApplyPatternEnvelope(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string envelopeXml, string innerXml)
        {
            // The canonical pattern-part data mutation is DeserializeDataFrom(XmlElement).
            // SerializeToXml/DeserializeFromXml only round-trip the <Properties> bag (IsDefault etc),
            // NOT the actual pattern XML. Discovered via runtime reflection — see
            // src/GxMcp.Worker/Services/WriteService.cs commit history for the inspection trail.
            if (TryApplyPatternDataFromXml(resolvedPart, innerXml))
            {
                Logger.Info("[PATTERN-WRITE] DeserializeDataFrom(XmlElement) applied with " + (innerXml?.Length ?? 0) + " chars.");
                return;
            }

            if (TryApplyNativePatternMutationExperiment(resolvedPart, innerXml))
            {
                Logger.Info("[PATTERN-DEBUG] Native pattern mutation experiment applied.");
                return;
            }

            // SerializeToXml/DeserializeFromXml on KBObjectPart round-trip the wrapped envelope
            // (<Part type="..."><Data><![CDATA[...]]></Data></Part>). Passing the bare inner XML
            // is silently ignored, so prefer the envelope and fall back to innerXml only if
            // envelope construction failed upstream.
            string payload = !string.IsNullOrWhiteSpace(envelopeXml) ? envelopeXml : innerXml;

            var executeUpdateMethod = resolvedPart.GetType().GetMethod(
                "ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(Action) },
                null);

            if (executeUpdateMethod != null)
            {
                Action updateAction = () => resolvedPart.DeserializeFromXml(payload);
                executeUpdateMethod.Invoke(resolvedPart, new object[] { "MCP pattern update", updateAction });
                return;
            }

            resolvedPart.DeserializeFromXml(payload);
        }
    }
}
