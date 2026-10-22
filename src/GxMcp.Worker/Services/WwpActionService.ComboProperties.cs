using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Packages.Patterns.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // set_combo_properties: the DVelop combo properties of one WorkWithPlus attribute
    // (Parameters, LoadDataDynamically, ConditionsDynamicAttsOrVars). The property model is the
    // SDK's own: names and values are checked against the element specification and converters
    // before anything is written, the write is the Pattern SDK change command, and the result is
    // proved by a fresh re-read of the whole instance and of the projected parent WebForm.
    public sealed partial class WwpActionService
    {
        internal const string SetComboPropertiesOperation = "set_combo_properties";

        private static bool IsComboPropertyOperation(string operation) => operation == SetComboPropertiesOperation;

        private sealed class ComboTarget
        {
            internal XElement XmlTarget;
            internal int Ordinal;
            internal WwpSdkComboElement Element;
        }

        private sealed class WwpComboException : Exception
        {
            internal readonly string Code;
            internal readonly JObject Detail;
            internal WwpComboException(string code, string message, JObject detail = null) : base(message)
            {
                Code = code;
                Detail = detail;
            }
        }

        private static string FailComboRequest(string target, JObject error) =>
            McpResponse.Err(code: error["code"]?.ToString() ?? "WwpComboInvalid",
                message: error["error"]?.ToString() ?? "The combo property request is invalid.", target: target, extra: error);

        private string RunComboPropertyOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, JObject args)
        {
            string attributeName = args?["attribute"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(attributeName))
                return McpResponse.Err(code: "ComboMissingAttribute", message: "attribute is required (the attribute name or its <typeGuid>-<Name> reference).", target: target);
            JObject requestError = WwpComboProperties.Parse(args["properties"], out List<WwpComboProperties.Requested> requested);
            if (requestError != null) return FailComboRequest(target, requestError);

            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            string containerName = args["containerName"]?.ToString()?.Trim();
            string expectedVersion = args["baseVersion"]?.ToString()
                ?? args["expectedVersion"]?.ToString()
                ?? args["versionToken"]?.ToString();
            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            if (!dryRun && string.IsNullOrWhiteSpace(expectedVersion))
                return McpResponse.Err(code: "ExpectedVersionRequired",
                    message: "baseVersion is required to write combo properties (preview with dryRun first and pass the returned versionToken).",
                    target: target, extra: new JObject { ["currentVersion"] = versionToken });

            JObject located = LocateComboTarget(xml, instancePart, attributeName, containerName, out ComboTarget found);
            if (located != null) return FailComboRequest(target, located);
            WwpComboPlan plan = WwpComboPlanner.Plan(found.Element, requested, WwpComboPlanner.CandidateNames(found.XmlTarget));
            if (plan.Error != null) return FailComboRequest(target, plan.Error);

            JObject report = plan.ToReport(found.Element.Path, found.Element.Reference);
            if (dryRun)
                return McpResponse.Ok(target: target, code: "WwpComboPropertiesDryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["operation"] = SetComboPropertiesOperation,
                    ["attribute"] = found.Element.Reference,
                    ["element"] = found.Element.Path,
                    ["changed"] = plan.AnyChange,
                    ["properties"] = report["properties"],
                    ["sdkSerialization"] = new JObject
                    {
                        ["path"] = report["serializationPath"],
                        ["unknownToSdk"] = report["unknownToSdk"],
                        ["discardedOnSave"] = report["discardedOnSave"],
                        ["savePathExercised"] = false
                    },
                    ["versionToken"] = versionToken,
                    ["patternSha256"] = Sha256(xml),
                    ["persisted"] = false,
                    ["patternReReadConfirmed"] = false,
                    ["webFormProjectionConfirmed"] = false,
                    ["lifecycleExecuted"] = false
                });

            if (!plan.AnyChange)
                return McpResponse.Ok(target: target, code: "WwpComboPropertiesNoChange", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["operation"] = SetComboPropertiesOperation,
                    ["attribute"] = found.Element.Reference,
                    ["element"] = found.Element.Path,
                    ["changed"] = false,
                    ["properties"] = report["properties"],
                    ["versionToken"] = versionToken,
                    ["patternSha256"] = Sha256(xml),
                    ["persisted"] = false,
                    ["lifecycleExecuted"] = false,
                    ["note"] = "Every requested value already equals what the SDK stores; nothing was written or projected."
                });

            return WriteComboProperties(target, requestedObject, attributeName, containerName, requested, expectedVersion, rollbackOnFailure);
        }

        private string WriteComboProperties(string target, KBObject requestedObject, string attributeName, string containerName,
            List<WwpComboProperties.Requested> requested, string expectedVersion, bool rollbackOnFailure)
        {
            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string readXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out _, out _);
                // The instance that is saved, snapshotted and re-read must be the object that owns
                // the part being changed. For a Transaction/WebPanel target the read and the envelope
                // each resolve their own copy of the WorkWithPlus object, so the part's owner is used
                // for everything below; saving a different copy would leave the change unpersisted.
                _patterns.BuildPatternPartEnvelope(lockedTarget, "PatternInstance", readXml, PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(readXml))
                    return BuildWwpInstanceNotResolvable(target);
                string currentXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out _, out _);
                if (string.IsNullOrWhiteSpace(currentXml)) currentXml = readXml;

                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion,
                        "The PatternInstance changed after the caller's read/dry-run; no combo property was written.");

                // The plan is recomputed under the per-object lock so a dry-run diff is never applied to a newer instance.
                JObject located = LocateComboTarget(currentXml, currentPart, attributeName, containerName, out ComboTarget lockedElement);
                if (located != null) return FailComboRequest(target, located);
                WwpComboPlan plan = WwpComboPlanner.Plan(lockedElement.Element, requested, WwpComboPlanner.CandidateNames(lockedElement.XmlTarget));
                if (plan.Error != null) return FailComboRequest(target, plan.Error);
                JObject report = plan.ToReport(lockedElement.Element.Path, lockedElement.Element.Reference);
                string reference = lockedElement.Element.Reference;

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "The exact PatternInstance/WebForm snapshots could not be captured; no combo property was written.",
                        target: target, extra: new JObject { ["snapshot"] = snapshots.ToJson(), ["persisted"] = false });

                bool saveAttempted = false;
                try
                {
                    Exception applyFailure = null;
                    ExecuteNativeUpdate(currentPart, "genexus_wwp " + SetComboPropertiesOperation, () =>
                    {
                        try
                        {
                            string refused = WwpComboPlanner.Apply(lockedElement.Element, plan);
                            if (refused != null)
                                throw new WwpComboException("WwpComboMutationRejected",
                                    "The Pattern SDK refused to set '" + refused + "' through its change command; nothing was saved.");
                        }
                        catch (Exception ex) { applyFailure = ex; }
                    });
                    if (applyFailure != null) throw applyFailure;

                    saveAttempted = true;
                    SaveNativePattern(currentInstance, currentPart);
                    WriteService.NotePerTargetWrite(target);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);

                    string persistedXml = ReadFreshInstance(currentInstance, out KBObject persistedInstance);
                    JObject patternVerification = VerifyPersistedComboProperties(currentXml, persistedXml, persistedInstance,
                        reference, lockedElement.Ordinal, plan);
                    if (!(bool)patternVerification["confirmed"])
                        throw new WwpComboException("WwpComboNotPersisted",
                            patternVerification["message"]?.ToString() ?? "The fresh re-read did not confirm the requested combo properties.",
                            patternVerification);

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance, "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpComboException("WwpComboApplyOnSaveDisabled", "SDPlus_Editor_Apply_On_Save became False after the native save.");

                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent, persistedInstance ?? currentInstance,
                        out WwpProjectionHelper.ProjectionResult projectionResult);
                    if (!projected)
                        throw new WwpComboException("WwpComboProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm"
                            + (string.IsNullOrWhiteSpace(projectionResult?.Failure) ? "." : ": " + projectionResult.Failure));

                    string projectedWebForm = ReadPart(parent, "WebForm");
                    JObject projection = VerifyComboProjection(parentWebFormBefore, projectedWebForm);
                    if (!(bool)projection["confirmed"])
                        throw new WwpComboException("WwpComboProjectionNotConfirmed",
                            projection["message"]?.ToString() ?? "The projected WebForm was not confirmed.", projection);

                    // The projection must not have changed the instance it projects: read it again.
                    string finalXml = ReadFreshInstance(currentInstance, out KBObject finalInstance);
                    JObject finalVerification = VerifyPersistedComboProperties(currentXml, finalXml, finalInstance,
                        reference, lockedElement.Ordinal, plan);
                    if (!(bool)finalVerification["confirmed"])
                        throw new WwpComboException("WwpComboNotPersisted",
                            "After the projection the instance no longer holds the requested combo properties: " + finalVerification["message"],
                            finalVerification);

                    JObject derivedContract = DescribeDerivedContract(finalInstance ?? currentInstance, finalXml, parent);
                    var updated = new JObject
                    {
                        ["instance"] = (finalInstance ?? currentInstance).Name,
                        ["parent"] = parent.Name,
                        ["operation"] = SetComboPropertiesOperation,
                        ["attribute"] = reference,
                        ["element"] = lockedElement.Element.Path,
                        ["changed"] = true,
                        ["properties"] = report["properties"],
                        ["confirmedOnReread"] = finalVerification,
                        ["versionTokenBefore"] = currentVersion,
                        ["versionToken"] = WriteService.ComputeContentVersionToken(finalInstance ?? currentInstance, finalXml),
                        ["patternSha256Before"] = Sha256(currentXml),
                        ["patternSha256After"] = Sha256(finalXml),
                        ["webFormSha256Before"] = Sha256(parentWebFormBefore),
                        ["webFormSha256After"] = Sha256(projectedWebForm),
                        ["projection"] = new JObject
                        {
                            ["executed"] = true,
                            ["patternBuildProcess"] = DescribeProjection(projectionResult),
                            ["verification"] = projection
                        },
                        ["persisted"] = true,
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["applyOnSaveBefore"] = applyOnSaveBefore,
                        ["applyOnSaveAfter"] = applyOnSaveAfter,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollbackPerformed"] = false,
                        ["lifecycleExecuted"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false,
                        // The projection above updated the parent; the helpers the instance derives are
                        // separate KB objects this operation does not rebuild. Say whether they still
                        // agree with the parent's calls instead of leaving that to a later compile error.
                        ["derivedContract"] = derivedContract
                    };
                    if (derivedContract["consistent"]?.Type == JTokenType.Boolean && !(bool)derivedContract["consistent"])
                        updated["nextSteps"] = new JArray(McpResponse.NextStep(
                            tool: "genexus_wwp",
                            args: new JObject { ["action"] = RegenerateDerivedOperation, ["name"] = target, ["dryRun"] = true },
                            why: "A derived helper no longer matches the calls the parent makes; plan its regeneration."));
                    return McpResponse.Ok(target: target, code: "WwpComboPropertiesUpdated", result: updated);
                }
                catch (Exception ex)
                {
                    Exception root = ExceptionRoot.Unwrap(ex) ?? ex;
                    WwpComboException typed = root as WwpComboException;
                    JObject rollback = rollbackOnFailure
                        ? RestoreAfterComboFailure(currentInstance, currentPart, nativeBytes, currentXml, parent, parentWebFormBefore,
                            applyOnSaveBefore, snapshots, saveAttempted)
                        : new JObject { ["attempted"] = false, ["rollbackVerified"] = false, ["reason"] = "rollbackOnFailure=false" };
                    bool restored = (bool?)rollback["rollbackVerified"] == true;
                    ExceptionRoot.Log("[WWP-COMBO] failed", ex);
                    if (typed?.Detail != null) Logger.Info("[WWP-COMBO] detail: " + typed.Detail.ToString(Newtonsoft.Json.Formatting.None));
                    var extra = new JObject
                    {
                        ["exceptionType"] = root.GetType().Name,
                        ["failureTrace"] = ExceptionRoot.FailureTrace(ex),
                        ["properties"] = report["properties"],
                        ["persisted"] = saveAttempted && !restored,
                        ["partialPersistence"] = saveAttempted && !restored,
                        ["patternReReadConfirmed"] = false,
                        ["webFormProjectionConfirmed"] = false,
                        ["versionTokenBefore"] = currentVersion,
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollback"] = rollback,
                        ["rollbackPerformed"] = (bool?)rollback["attempted"] != false,
                        ["rollbackVerified"] = restored,
                        ["lifecycleExecuted"] = false
                    };
                    if (typed?.Detail != null) extra["detail"] = typed.Detail;
                    return McpResponse.Err(code: typed?.Code ?? "WwpComboFailed", message: ExceptionRoot.Message(ex), target: target, extra: extra);
                }
            }
        }

        // Read-only: do the derived helpers of the instance still agree with the calls the parent
        // makes? Never fails the write that already succeeded; an unreadable unit is reported as such.
        private JObject DescribeDerivedContract(KBObject host, string hostXml, KBObject parent)
        {
            try
            {
                if (WwpDerivedEngine.EnumerateDerived(host).Count == 0)
                    return new JObject { ["evaluated"] = true, ["derivedObjects"] = 0, ["helpers"] = new JArray(), ["consistent"] = true };
                DerivedUnit unit = CaptureUnit(host, hostXml, parent);
                JObject contract = EvaluateContract(unit);
                return new JObject
                {
                    ["evaluated"] = true,
                    ["derivedObjects"] = unit.Derived.Count,
                    ["helpers"] = new JArray(unit.Derived.Where(d => d.Helper).Select(d => d.State.Name)),
                    ["consistent"] = contract["consistent"],
                    ["details"] = contract["helpers"]
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["evaluated"] = false, ["error"] = ExceptionRoot.Message(ex) };
            }
        }

        private static void ExecuteNativeUpdate(KBObjectPart part, string label, Action mutation)
        {
            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(Action) }, null);
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { label, mutation });
            else mutation();
        }

        // A fresh SDK resolution of the instance, so the verification does not read back the very
        // in-memory object the write just changed.
        private string ReadFreshInstance(KBObject instance, out KBObject freshInstance)
        {
            string xml = _patterns.ReadPatternPartXmlFresh(instance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                out freshInstance, out _, out JObject diagnostic);
            if (string.IsNullOrWhiteSpace(xml))
                throw new WwpComboException("WwpComboNotPersisted",
                    "The fresh re-read of the PatternInstance returned nothing" + (diagnostic?["message"] == null ? "." : ": " + diagnostic["message"]));
            return xml;
        }

        private JObject VerifyPersistedComboProperties(string beforeXml, string persistedXml, KBObject persistedInstance,
            string reference, int ordinal, WwpComboPlan plan)
        {
            Dictionary<string, string> effective = null;
            try
            {
                _patterns.BuildPatternPartEnvelope(persistedInstance, "PatternInstance", persistedXml, PatternRegistry.WorkWithPlusPatternId,
                    out _, out KBObjectPart freshPart);
                PatternInstanceElement root = (freshPart as PatternInstancePart)?.RootElement;
                if (root != null)
                {
                    List<WwpSdkComboElement> matches = FindNativeAttributeElements(root, reference);
                    if (ordinal < matches.Count)
                        effective = plan.Items.ToDictionary(i => i.Definition.SdkName, i => matches[ordinal].Effective(i.Definition.SdkName),
                            StringComparer.Ordinal);
                }
            }
            catch (Exception ex) { Logger.Debug("[WWP-COMBO] native effective read skipped: " + ex.Message); }

            JObject verification = WwpComboVerifier.Verify(XDocument.Parse(beforeXml, LoadOptions.PreserveWhitespace),
                XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace), reference, ordinal, plan.Expected(), effective);
            verification["nativeEffectiveRead"] = effective != null;
            return verification;
        }

        private JObject RestoreAfterComboFailure(KBObject instance, KBObjectPart part, byte[] nativeBytes, string patternXml,
            KBObject parent, string webForm, string applyOnSaveBefore, SnapshotBundle snapshots, bool saveAttempted)
        {
            JObject rollback = RestoreSnapshots(instance, part, nativeBytes, patternXml, parent, webForm, applyOnSaveBefore);
            // The restore reports what it read from the same in-memory objects it wrote; the claim
            // that it was verified rests on an independent fresh re-read matching the snapshot hashes.
            string freshPattern = null, freshWebForm = null;
            try { freshPattern = _patterns.ReadPatternPartXmlFresh(instance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId, out _, out _, out _); }
            catch (Exception ex) { rollback["freshPatternReadError"] = (ex.InnerException ?? ex).Message; }
            try { freshWebForm = ReadPart(parent, "WebForm"); }
            catch (Exception ex) { rollback["freshWebFormReadError"] = (ex.InnerException ?? ex).Message; }
            bool patternMatches = ContentMatches(freshPattern, patternXml);
            bool webFormMatches = ContentMatches(freshWebForm, webForm);
            rollback["attempted"] = true;
            rollback["saveHadHappened"] = saveAttempted;
            rollback["patternSha256Expected"] = snapshots.PatternSha256;
            rollback["patternSha256Restored"] = freshPattern == null ? null : Sha256(freshPattern);
            rollback["webFormSha256Expected"] = snapshots.WebFormSha256;
            rollback["webFormSha256Restored"] = freshWebForm == null ? null : Sha256(freshWebForm);
            rollback["verifiedByFreshReread"] = patternMatches && webFormMatches;
            rollback["rollbackVerified"] = patternMatches && webFormMatches && (bool?)rollback["applyOnSaveRestoredExactly"] != false;
            return rollback;
        }

        private static JObject DescribeProjection(WwpProjectionHelper.ProjectionResult result) => new JObject
        {
            ["shouldBuild"] = result?.ShouldBuild,
            ["beforeStartBuild"] = result?.BeforeStartBuild,
            ["afterImportResources"] = result?.AfterImportResources,
            ["updateParentObject"] = result?.UpdateParentObject,
            ["afterEndBuild"] = result?.AfterEndBuild,
            ["parentSaved"] = result?.ParentSaved,
            ["failure"] = result?.Failure
        };

        /// <summary>
        /// What can be proved about the projected parent WebForm from its text alone: it is
        /// still a well-formed form of the same kind and it still carries every attribute control
        /// it carried before. The WebForm stores the combo configuration as engine tokens, so the
        /// values themselves are proved on the PatternInstance re-read, not here.
        /// </summary>
        internal static JObject VerifyComboProjection(string before, string after)
        {
            var result = new JObject
            {
                ["confirmed"] = false,
                ["level"] = "structural",
                ["changed"] = !string.Equals(Sha256(before ?? string.Empty), Sha256(after ?? string.Empty), StringComparison.Ordinal)
            };
            if (string.IsNullOrWhiteSpace(after))
            {
                result["message"] = "The projected WebForm could not be re-read.";
                return result;
            }
            XDocument projected;
            try { projected = XDocument.Parse(after, LoadOptions.PreserveWhitespace); }
            catch (XmlException)
            {
                result["message"] = "The projected WebForm is not valid XML.";
                return result;
            }
            result["wellFormed"] = true;
            XDocument original = null;
            if (!string.IsNullOrWhiteSpace(before))
            {
                // Without a readable "before" there is nothing to compare the control count with:
                // counting it as zero would confirm a projection that dropped every control.
                try { original = XDocument.Parse(before, LoadOptions.PreserveWhitespace); }
                catch (XmlException)
                {
                    result["message"] = "The WebForm before the projection is not valid XML, so the projection cannot be compared with it.";
                    return result;
                }
            }
            if (original?.Root != null && !string.Equals(original.Root.Name.LocalName, projected.Root?.Name.LocalName, StringComparison.Ordinal))
            {
                result["message"] = "The projected WebForm changed its root element from '" + original.Root.Name.LocalName + "' to '" + projected.Root?.Name.LocalName + "'.";
                return result;
            }
            int controlsBefore = original == null ? 0 : CountAttributeControls(original);
            int controlsAfter = CountAttributeControls(projected);
            result["attributeControlsBefore"] = controlsBefore;
            result["attributeControlsAfter"] = controlsAfter;
            if (controlsBefore != controlsAfter)
            {
                result["message"] = "The projection changed the number of attribute controls on the WebForm (" + controlsBefore + " -> " + controlsAfter + ").";
                return result;
            }
            result["confirmed"] = true;
            return result;
        }

        private static int CountAttributeControls(XDocument document) =>
            document.Descendants().Count(e => e.Attribute("attribute") != null);

        private static IEnumerable<PatternInstanceElement> WalkNativeElements(PatternInstanceElement element)
        {
            yield return element;
            foreach (PatternInstanceElement child in element.Children)
                foreach (PatternInstanceElement descendant in WalkNativeElements(child))
                    yield return descendant;
        }

        private static List<WwpSdkComboElement> FindNativeAttributeElements(PatternInstanceElement root, string requested) =>
            WalkNativeElements(root)
                .Where(e => e.Specification != null && e.Specification.Attributes.Any(a => string.Equals(a.Name, "attribute", StringComparison.Ordinal)))
                .Select(e => new WwpSdkComboElement(e))
                .Where(e => AttributeReferenceMatches(e.Reference, requested))
                .ToList();

        /// <summary>
        /// Finds the one attribute element the request is about, in the XML projection and in the
        /// native tree (they list the same elements in the same order). Null when found.
        /// </summary>
        private static JObject LocateComboTarget(string xml, KBObjectPart part, string requested, string containerName, out ComboTarget found)
        {
            found = null;
            XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            List<XElement> xmlMatches = WwpComboPlanner.FindAttributeElements(document, requested);
            if (xmlMatches.Count == 0)
                return new JObject
                {
                    ["code"] = "ComboAttributeNotFound",
                    ["error"] = "The instance has no attribute '" + requested + "' on its form.",
                    ["available"] = new JArray(document.Descendants()
                        .Where(e => e.Name.LocalName.EndsWith("ttribute", StringComparison.OrdinalIgnoreCase) && e.Attribute("attribute") != null)
                        .Select(e => WwpComboPlanner.NameFromReference((string)e.Attribute("attribute"))).Distinct().Take(50))
                };

            PatternInstanceElement root = (part as PatternInstancePart)?.RootElement;
            if (root == null)
                return new JObject { ["code"] = "WwpComboNativeRootUnavailable", ["error"] = "PatternInstance RootElement is unavailable." };
            List<WwpSdkComboElement> nativeMatches = FindNativeAttributeElements(root, requested);
            if (nativeMatches.Count != xmlMatches.Count)
                return new JObject
                {
                    ["code"] = "WwpComboNativeXmlMismatch",
                    ["error"] = "The native PatternInstance lists " + nativeMatches.Count + " element(s) for '" + requested
                        + "' and its XML projection lists " + xmlMatches.Count + "; no combo property was changed."
                };

            // Only elements whose specification carries the combo properties can be the target (a
            // description attribute or an order key names the same attribute but is no control).
            var indexes = Enumerable.Range(0, xmlMatches.Count)
                .Where(i => nativeMatches[i].HasProperty(WwpComboProperties.All[0].SdkName)).ToList();
            if (indexes.Count == 0)
                return new JObject
                {
                    ["code"] = "ComboElementNotAControl",
                    ["error"] = "'" + requested + "' is in the instance only as " + string.Join(", ", nativeMatches.Select(m => m.Element.Specification.Name).Distinct())
                        + ", which carry no combo properties.",
                    ["matches"] = new JArray(nativeMatches.Select(m => new JObject { ["element"] = m.Path }))
                };
            if (!string.IsNullOrWhiteSpace(containerName))
            {
                List<XElement> containers = FindFormContainers(document, containerName).ToList();
                if (containers.Count == 0)
                    return new JObject { ["code"] = "ComboContainerNotFound", ["error"] = "Container '" + containerName + "' was not found." };
                indexes = indexes.Where(i => xmlMatches[i].Ancestors().Any(a => containers.Contains(a))).ToList();
            }
            if (indexes.Count != 1)
                return new JObject
                {
                    ["code"] = indexes.Count == 0 ? "ComboAttributeNotInContainer" : "ComboAttributeAmbiguous",
                    ["error"] = indexes.Count == 0
                        ? "Attribute '" + requested + "' is not inside container '" + containerName + "'."
                        : "Attribute '" + requested + "' appears " + indexes.Count + " times on the form; pass containerName with a path of named ancestors.",
                    ["matches"] = new JArray(indexes.Select(i => new JObject
                    {
                        ["element"] = nativeMatches[i].Path,
                        ["container"] = ContainerPath(xmlMatches[i].Parent)
                    }))
                };

            found = new ComboTarget { XmlTarget = xmlMatches[indexes[0]], Ordinal = indexes[0], Element = nativeMatches[indexes[0]] };
            return null;
        }
    }
}
