using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Xml;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services.Structure;

namespace GxMcp.Worker.Services
{
    public class StructureService
    {
        private readonly ObjectService _objectService;
        private readonly VisualStructureService _visualStructureService;
        private readonly IndexService _indexService;
        private readonly AttributeWriteService _attributeWriteService;
        private readonly DomainWriteService _domainWriteService;
        private readonly SDTService _sdtService;
        private readonly GroupStructureService _groupStructureService;

        private static readonly System.Text.RegularExpressions.Regex SubRegex = new System.Text.RegularExpressions.Regex(
            @"\bsub\s+['""]?([\w\.]+)['""]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex EventRegex = new System.Text.RegularExpressions.Regex(
            @"\bevent\s+['""]?([\w\.]+)['""]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        public StructureService(ObjectService objectService)
        {
            _objectService = objectService;
            _visualStructureService = new VisualStructureService(objectService);
            _indexService = new IndexService(objectService);
            _attributeWriteService = new AttributeWriteService(objectService);
            _domainWriteService = new DomainWriteService(objectService);
            _sdtService = new SDTService(objectService);
            _groupStructureService = new GroupStructureService(objectService);
        }

        public string UpdateVisualStructure(string targetName, string payload, bool dryRun = false,
            string expectedVersion = null, bool rollbackOnFailure = true, string moduleName = null)
        {
            try {
                var obj = string.IsNullOrWhiteSpace(moduleName)
                    ? _objectService.FindObject(targetName)
                    : ResolveTransaction(targetName, moduleName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                // issue #52: SDT structure updates (root Collection flag + item name, Domain-based
                // and SDT-reference members, nested levels) go through the SDT-specific writer —
                // the Transaction path below can't express any of that.
                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    obj = _objectService.FindObjectFreshByIdentity(obj);
                    if (obj == null) return Models.McpResponse.Err(code: "FreshReadUnavailable",
                        message: "A fresh SDT read could not be confirmed; no update was attempted.",
                        target: targetName, extra: new JObject { ["persisted"] = false });
                    string sdtVersion = WriteService.ComputeVersionToken(obj);
                    if (string.IsNullOrWhiteSpace(expectedVersion))
                        return Models.McpResponse.Err(code: "ExpectedVersionRequired",
                            message: "An SDT structure update requires expectedVersion from get_visual.",
                            target: targetName, extra: new JObject { ["persisted"] = false, ["currentVersion"] = sdtVersion });
                    if (!string.IsNullOrWhiteSpace(expectedVersion)
                        && !string.Equals(expectedVersion, sdtVersion, StringComparison.Ordinal))
                        return Models.McpResponse.Err(code: "VersionConflict",
                            message: "The SDT changed after expectedVersion was captured.", target: targetName,
                            extra: new JObject { ["expectedVersion"] = expectedVersion,
                                ["currentVersion"] = sdtVersion, ["persisted"] = false });
                    if (dryRun)
                    {
                        JObject currentSdt = _sdtService.ReadStructure(obj);
                        JArray beforeChildren = (JArray)currentSdt["children"];
                        bool beforeIsColl = currentSdt["isCollection"]?.ToObject<bool>() ?? false;
                        JObject reqObj;
                        try { reqObj = JObject.Parse(payload); }
                        catch (Exception ex) { return Models.McpResponse.Err(code: "InvalidStructurePayload", message: ex.Message, target: targetName); }
                        var reqChildren = reqObj["children"] as JArray;
                        string mode = reqObj["mode"]?.ToString() ?? "replace";
                        if (mode != "replace" && mode != "add")
                            return Models.McpResponse.Err(code: "InvalidStructureMode", message: "mode must be add or replace.", target: targetName);
                        if (mode == "add" && (reqObj["isCollection"] != null || reqObj["collectionItemName"] != null))
                            return Models.McpResponse.Err(code: "InvalidStructurePayload",
                                message: "mode=add only accepts children; root metadata must be changed with mode=replace.", target: targetName);
                        JArray projected;
                        try { projected = SdtStructurePlan.Project(beforeChildren, reqChildren, mode == "add"); }
                        catch (ArgumentException ex) { return Models.McpResponse.Err(code: "InvalidStructurePayload", message: ex.Message, target: targetName); }
                        bool? reqIsColl = reqObj?["isCollection"]?.ToObject<bool>();

                        JArray sdtDiff = SdtStructurePlan.Diff(beforeChildren, projected);
                        foreach (string key in new[] { "isCollection", "collectionItemName" })
                            if (reqObj[key] != null && !JToken.DeepEquals(currentSdt[key], reqObj[key]))
                                sdtDiff.Add(new JObject { ["path"] = key, ["change"] = "changed",
                                    ["before"] = currentSdt[key]?.DeepClone(), ["after"] = reqObj[key].DeepClone() });
                        bool mutationDetected = sdtDiff.Count > 0 || (reqIsColl.HasValue && reqIsColl.Value != beforeIsColl);

                        return Models.McpResponse.Ok(target: targetName, code: "DryRun", result: new JObject
                        {
                            ["persisted"] = false,
                            ["mutationDetected"] = mutationDetected,
                            ["beforeVersion"] = sdtVersion,
                            ["afterVersion"] = sdtVersion,
                            ["versionToken"] = sdtVersion,
                            ["before"] = beforeChildren,
                            ["requested"] = reqObj,
                            ["projected"] = projected,
                            ["diff"] = sdtDiff,
                            ["implicitLifecycleActions"] = new JArray()
                        });
                    }
                    return _sdtService.UpdateSDTStructure(targetName, payload, expectedVersion);
                }

                var trn = obj as Transaction;
                if (trn == null) return Models.McpResponse.Err(
                    code: "NotATransaction",
                    message: "Object is not a Transaction or SDT.",
                    hint: "Visual structure updates support Transaction and SDT objects.",
                    target: targetName,
                    nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep(
                        tool: "genexus_analyze",
                        args: new Newtonsoft.Json.Linq.JObject { ["name"] = targetName },
                        why: "Confirms the object type before attempting a structure update.")));

                var json = JObject.Parse(payload);
                var children = json["children"] as JArray;
                if (children == null) return Models.McpResponse.Err(
                    code: "InvalidStructurePayload",
                    message: "The payload must contain a 'children' array for visual structure updates.",
                    hint: "Pass a JSON object with a 'children' array describing the Transaction structure.",
                    target: targetName);

                string invalidShowInForms = TransactionAttributeFormsProperty.FindInvalid(children);
                if (invalidShowInForms != null) return Models.McpResponse.Err(
                    code: "InvalidShowInDefaultForms",
                    message: "showInDefaultForms of '" + invalidShowInForms + "' must be true or false.",
                    target: targetName, extra: new JObject { ["persisted"] = false });

                JArray before = _visualStructureService.SerializeVisualLevel(trn.Structure.Root);
                TransactionSnapshot snapshot;
                try { snapshot = CaptureTransactionSnapshot(trn); }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(code: "SnapshotFailed", message: ex.Message,
                        hint: "No structure write was attempted because a lossless snapshot could not be captured.",
                        target: targetName, extra: new JObject { ["persisted"] = false });
                }
                string versionBefore = WriteService.ComputeVersionToken(trn);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, versionBefore, StringComparison.Ordinal))
                    return Models.McpResponse.Err(code: "VersionConflict",
                        message: "The Transaction changed after expectedVersion was captured.",
                        hint: "Re-read the structure and retry with its current versionToken.",
                        target: targetName, extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = versionBefore,
                            ["persisted"] = false
                        });

                JArray previewDiff = CompareRequestedStructure(children, before, "children");
                if (dryRun)
                    return Models.McpResponse.Ok(target: targetName, code: "DryRun", result: new JObject
                    {
                        ["persisted"] = false,
                        ["mutationDetected"] = false,
                        ["beforeVersion"] = versionBefore,
                        ["afterVersion"] = versionBefore,
                        ["versionToken"] = versionBefore,
                        ["before"] = before,
                        ["requested"] = children.DeepClone(),
                        ["diff"] = previewDiff,
                        ["implicitLifecycleActions"] = new JArray()
                    });

                bool committed = false;
                try
                {
                    using (var sdkTrans = trn.Model.KB.BeginTransaction())
                    {
                        try
                        {
                            var locked = trn.Model.Objects.Get(trn.Guid) as Transaction ?? trn;
                            string lockedVersion = WriteService.ComputeVersionToken(locked);
                            if (!string.Equals(lockedVersion, versionBefore, StringComparison.Ordinal))
                                throw new InvalidOperationException("VersionConflict: the Transaction changed before the atomic save.");

                            _visualStructureService.SyncVisualStructure(locked, children);
                            locked.Save(new Artech.Architecture.Common.Objects.KBObjectSavePreferences
                            {
                                ForceSave = true,
                                ForceSaveDefaultParts = true,
                                SkipValidation = false
                            });

                            // Only authored non-default parts are invariants. Default WinForm/WebForm
                            // projections legitimately follow Structure and must not create a false
                            // AuthoredPartsCorrupted result.
                            foreach (KBObjectPart part in locked.Parts)
                            {
                                if (part is StructurePart || part.IsDefault) continue;
                                if (!snapshot.Parts.TryGetValue(part.Type.ToString("D"), out EntitySnapshot data)) continue;
                                RestoreEntity(part, data);
                                part.Dirty = true;
                                part.Save();
                            }

                            sdkTrans.Commit();
                            committed = true;
                            WriteService.NotePerTargetWrite(targetName);
                        }
                        finally { if (!committed) try { sdkTrans.Rollback(); } catch { } }
                    }

                    var persistedTrn = trn.Model.Objects.Get(trn.Guid) as Transaction ?? trn;
                    JArray persisted = _visualStructureService.SerializeVisualLevel(persistedTrn.Structure.Root);
                    JArray diff = CompareRequestedStructure(children, persisted, "children");
                    _objectService.GetKbService().GetIndexCache().UpdateEntry(persistedTrn);
                    if (diff.Count > 0)
                        return RollbackStructureFailure(targetName, "StructureUpdateNotPersisted",
                            "The persisted Transaction does not match the requested structure.",
                            trn, snapshot, before, children, persisted, diff, versionBefore, rollbackOnFailure);

                    if (!VerifyAuthoredPartsPreserved(persistedTrn, snapshot.Parts, out string authoredErr))
                        return RollbackStructureFailure(targetName, "AuthoredPartsCorrupted",
                            "Structure update changed a user-authored non-Structure part: " + authoredErr,
                            trn, snapshot, before, children, persisted, diff, versionBefore, rollbackOnFailure);

                    string versionAfter = WriteService.ComputeVersionToken(persistedTrn);
                    var structureResult = new JObject
                        {
                            ["persisted"] = true,
                            ["mutationDetected"] = true,
                            ["beforeVersion"] = versionBefore,
                            ["afterVersion"] = versionAfter,
                            ["versionToken"] = versionAfter,
                            ["before"] = before,
                            ["requested"] = children.DeepClone(),
                            ["persistedStructure"] = persisted,
                            ["diff"] = diff,
                            ["saved"] = true,
                            ["persistedVerified"] = true,
                            ["authoredPartsPreserved"] = true,
                            ["implicitLifecycleActions"] = new JArray()
                        };
                        // Issue #97 guard-rail: flag subtype attributes the SDK left
                        // classified as stored (SECONDARY) while their same-supertype
                        // siblings are derived (INFERRED) — a silent physical-column bug.
                        var subtypeIssues = TryComputeSubtypeClassificationIssues(persistedTrn ?? trn);
                        if (subtypeIssues.Count > 0)
                        {
                            structureResult["subtypeClassification"] = new JObject
                            {
                                ["check"] = "subtype_inferred_mismatch",
                                ["status"] = "warning",
                                ["issues"] = subtypeIssues,
                                ["hint"] = "Subtype attribute(s) are classified as stored (SECONDARY) instead of derived (INFERRED) — this creates a physical column and breaks supertype propagation. The SDK only recomputes the class through the IDE's SubtypeGroup editor; via MCP, remove the attribute (genexus_structure action=remove_attribute) and re-add it, then re-run genexus_structure action=check_subtypes."
                            };
                        }
                    return Models.McpResponse.Ok(target: targetName, code: "StructureUpdated", result: structureResult);
                }
                catch (Exception ex)
                {
                    var current = trn.Model.Objects.Get(trn.Guid) as Transaction ?? trn;
                    if (ex.Message.StartsWith("VersionConflict:", StringComparison.Ordinal))
                        return Models.McpResponse.Err(
                            code: "VersionConflict",
                            message: ex.Message,
                            hint: "The SDK transaction was rolled back; the concurrent version was preserved and no old snapshot was restored.",
                            target: targetName,
                            extra: new JObject
                            {
                                ["persisted"] = false,
                                ["mutationDetected"] = false,
                                ["beforeVersion"] = versionBefore,
                                ["rollback"] = new JObject
                                {
                                    ["attempted"] = false,
                                    ["skipped"] = true,
                                    ["verified"] = false,
                                    ["reason"] = "Restoring the old snapshot could overwrite a concurrent edit."
                                },
                                ["implicitLifecycleActions"] = new JArray()
                            });
                    bool unchanged = !committed && TransactionMatchesSnapshot(current, snapshot);
                    if (unchanged)
                        return Models.McpResponse.Err(code: ex.Message.StartsWith("VersionConflict:", StringComparison.Ordinal)
                                ? "VersionConflict" : "StructureUpdateFailed",
                            message: ex.Message,
                            hint: "The SDK transaction was rolled back and the original snapshot was verified.",
                            target: targetName, extra: new JObject
                            {
                                ["persisted"] = false,
                                ["mutationDetected"] = false,
                                ["beforeVersion"] = versionBefore,
                                ["rollback"] = new JObject { ["attempted"] = false, ["verified"] = true },
                                ["implicitLifecycleActions"] = new JArray()
                            });
                    return RollbackStructureFailure(targetName, "StructureUpdateFailed", ex.Message,
                        current, snapshot, before, children, null, null, versionBefore, rollbackOnFailure);
                }
            } catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "StructureUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the target Transaction exists and the payload is valid JSON.",
                    target: targetName);
            }
        }

        private string RollbackStructureFailure(string targetName, string code, string message,
            Transaction trn, TransactionSnapshot snapshot, JArray before, JArray requested,
            JArray persisted, JArray diff, string versionBefore, bool rollbackOnFailure)
        {
            var current = trn?.Model?.Objects.Get(trn.Guid) as Transaction ?? trn;
            RestoreResult restored = RestoreTransactionSnapshot(current, snapshot);
            bool rollbackVerified = restored.Success && restored.Verified;
            string versionAfter = null;
            try
            {
                var afterRollback = current?.Model?.Objects.Get(current.Guid) as Transaction ?? current;
                versionAfter = afterRollback == null ? null : WriteService.ComputeVersionToken(afterRollback);
            }
            catch { }

            return Models.McpResponse.Err(code: code,
                message: rollbackVerified
                    ? message + " The complete Transaction snapshot was restored."
                    : message + " The complete Transaction snapshot could not be verified after rollback.",
                hint: rollbackVerified
                    ? "Re-read the Transaction before retrying."
                    : "Stop writing this Transaction and inspect the rollback details.",
                target: targetName,
                extra: new JObject
                {
                    ["persisted"] = !rollbackVerified,
                    ["mutationDetected"] = !rollbackVerified,
                    ["beforeVersion"] = versionBefore,
                    ["afterVersion"] = versionAfter,
                    ["before"] = before,
                    ["requested"] = requested?.DeepClone(),
                    ["persistedStructure"] = persisted,
                    ["diff"] = diff ?? new JArray(),
                    ["rollback"] = new JObject
                    {
                        ["requested"] = rollbackOnFailure,
                        ["enforcedByAtomicContract"] = true,
                        ["attempted"] = true,
                        ["saved"] = restored.Success,
                        ["verified"] = restored.Verified,
                        ["error"] = restored.Error
                    },
                    ["implicitLifecycleActions"] = new JArray()
                });
        }

        private static bool TransactionMatchesSnapshot(Transaction trn, TransactionSnapshot snapshot)
        {
            if (trn == null || snapshot == null) return false;
            try
            {
                var structure = trn.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is StructurePart);
                if (structure == null
                    || !snapshot.Parts.TryGetValue(structure.Type.ToString("D"), out EntitySnapshot expectedStructure)
                    || !EntityMatches(structure, expectedStructure)) return false;
                if (!JToken.DeepEquals(snapshot.Integrity, CaptureStructureIntegrity(trn, null))) return false;
                return VerifyAuthoredPartsPreserved(trn, snapshot.Parts, out _);
            }
            catch { return false; }
        }

        /// <summary>
        /// Reorders an existing TransactionAttribute through TransactionLevel.Items.  The
        /// exact SDK object is removed and inserted again; no KB-global Attribute is deleted
        /// or recreated.  A complete in-memory part snapshot is retained until post-save
        /// verification finishes, and the Structure binary is also written to the normal
        /// .gx snapshot store for audit/recovery.
        /// </summary>
        public string MoveAttribute(string targetName, JObject args)
        {
            args = args ?? new JObject();
            string moduleName = args["transactionModule"]?.ToString();
            string attributeName = args["attribute"]?.ToString();
            string before = args["before"]?.ToString();
            string after = args["after"]?.ToString();
            int? position = args["position"]?.ToObject<int?>();
            bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
            string baseVersion = args["baseVersion"]?.ToString();

            try
            {
                // The cached KBObject can lag behind a previous source write. Re-read the
                // transaction before taking the rollback snapshot; otherwise a subsequent
                // SDK Save can persist the stale empty Rules/Events parts as if they were
                // the current authored content.
                var trn = ResolveTransactionFresh(targetName, moduleName);
                if (trn == null)
                    return Models.McpResponse.Err(
                        code: "TransactionNotFound",
                        message: string.IsNullOrWhiteSpace(moduleName)
                            ? $"Transaction '{targetName}' was not found."
                            : $"Transaction '{targetName}' was not found in module '{moduleName}'.",
                        target: targetName);

                if (!string.IsNullOrWhiteSpace(baseVersion))
                {
                    string currentVersion = WriteService.ComputeVersionToken(trn);
                    if (!string.IsNullOrWhiteSpace(currentVersion)
                        && !string.Equals(baseVersion, currentVersion, StringComparison.Ordinal))
                        return Models.McpResponse.Err(
                            code: "StaleObject",
                            message: "The Transaction changed after the version supplied in baseVersion. The attribute was not moved.",
                            hint: "Re-read the Transaction and retry with its current versionToken.",
                            target: targetName,
                            extra: new JObject
                            {
                                ["expectedVersion"] = baseVersion,
                                ["currentVersion"] = currentVersion
                            });
                }

                ResolvedLevel resolved;
                string levelError;
                if (!TryResolveLevel(trn, args, out resolved, out levelError))
                    return Models.McpResponse.Err(
                        code: "LevelNotFound",
                        message: levelError,
                        hint: "Use level=\"root\", an unambiguous level name, or levelPath=[\"Item\",\"Operation\"].",
                        target: targetName);

                var level = resolved.Level;
                var attributes = level.Attributes.Cast<TransactionAttribute>().ToList();
                AttributeMovePlan plan;
                try
                {
                    plan = AttributeMovePlanner.Create(attributes.Select(a => a.Name),
                        attributeName, before, after, position);
                }
                catch (InvalidOperationException ex) when (ex.Message == "AttributeNotFound")
                {
                    return Models.McpResponse.Err(
                        code: "AttributeNotFound",
                        message: $"Attribute '{attributeName}' does not belong to level '{resolved.DisplayName}'.",
                        target: targetName);
                }
                catch (InvalidOperationException ex) when (ex.Message == "ReferenceAttributeNotFound")
                {
                    string reference = !string.IsNullOrWhiteSpace(before) ? before : after;
                    return Models.McpResponse.Err(
                        code: "ReferenceAttributeNotInLevel",
                        message: $"Reference attribute '{reference}' does not belong to the same level '{resolved.DisplayName}'.",
                        target: targetName);
                }
                catch (ArgumentException ex)
                {
                    return Models.McpResponse.Err(code: "InvalidMoveRequest", message: ex.Message,
                        hint: "Provide exactly one of before, after, or a zero-based position.", target: targetName);
                }

                var targetAttribute = attributes[plan.OldPosition];
                string identity = AttributeIdentity(targetAttribute);
                var preview = BuildMoveResult(targetName, moduleName, resolved, attributeName,
                    plan.OldPosition, plan.NewPosition, before, after, position, dryRun);

                if (dryRun)
                    return Models.McpResponse.Ok(target: targetName, code: "AttributeMovePreview", result: preview);

                TransactionSnapshot snapshot;
                try { snapshot = CaptureTransactionSnapshot(trn, identity); }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "SnapshotFailed",
                        message: "The Transaction could not be snapshotted safely; no write was attempted. " + ex.Message,
                        target: targetName);
                }

                string snapshotPath = PersistStructureSnapshot(trn, snapshot);
                string writeFailure = null;
                using (var sdkTrans = trn.Model.KB.BeginTransaction())
                {
                    try
                    {
                        MoveExistingAttribute(level, targetAttribute, plan.NewPosition);
                        trn.Structure.Dirty = true;
                        // StructurePart.Save() alone does not persist item order on GX18.
                        // Save the Transaction while refusing forced default-part saves;
                        // post-save verification below rejects changes to every authored
                        // (IsDefault=false) non-Structure part.
                        var preservedDefaults = EnumerateTransactionParts(trn)
                            .Where(p => !(p is StructurePart))
                            .OfType<Artech.Architecture.Common.Defaults.IApplyDefaultTarget>()
                            .ToList();
                        foreach (var preserved in preservedDefaults) preserved.PreserveDefaultLock();
                        try
                        {
                            trn.Save(new KBObjectSavePreferences
                            {
                                ForceSave = true,
                                ForceSaveDefaultParts = false,
                                // Reordering an existing member does not change schema
                                // validity. Skip the broad Transaction validator here: it
                                // can reject an already-unspecified nested Transaction for
                                // unrelated diagnostics, while this operation must not run
                                // Specify/Generate implicitly.
                                SkipValidation = true
                            });
                        }
                        finally
                        {
                            foreach (var preserved in preservedDefaults) preserved.PreserveDefaultUnlock();
                        }

                        // A parent Transaction.Save can wipe a direct Rules/Events property
                        // on disk while leaving the in-memory part unchanged. Always write
                        // the authored snapshot back through the native part after the
                        // parent save; comparing the same in-memory instance is insufficient.
                        RestoreAuthoredTransactionParts(trn, snapshot);

                        sdkTrans.Commit();
                        WriteService.NotePerTargetWrite(targetName);
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        writeFailure = ex.Message;
                    }
                }

                if (writeFailure == null)
                {
                    try
                    {
                        // GX18 may apply a default projection again when the parent
                        // transaction commits. Re-save the authored parts in a separate
                        // transaction so that finalization cannot erase Rules/Events.
                        using (var authoredTx = trn.Model.KB.BeginTransaction())
                        {
                            var authoredTarget = RefreshTransaction(trn) ?? trn;
                            RestoreAuthoredTransactionParts(authoredTarget, snapshot);
                            authoredTx.Commit();
                            WriteService.NotePerTargetWrite(targetName);
                        }
                    }
                    catch (Exception ex)
                    {
                        writeFailure = ex.Message;
                    }
                }

                if (writeFailure != null)
                {
                    var restored = RestoreTransactionSnapshot(trn, snapshot);
                    return Models.McpResponse.Err(
                        code: "AttributeMoveFailed",
                        message: writeFailure,
                        hint: "The pre-write snapshot was restored; inspect rollbackVerified before retrying.",
                        target: targetName,
                        extra: new JObject
                        {
                            ["snapshot"] = snapshotPath,
                            ["rolledBack"] = restored.Success,
                            ["rollbackVerified"] = restored.Verified,
                            ["rollbackError"] = restored.Error
                        });
                }

                // Resolve through the SDK object collection after invalidating the read cache.
                // The mutated Transaction instance can still expose pre-save part state, and
                // swallowing Reload() failures made a wiped Rules/Events part look preserved.
                var persisted = ResolveTransactionFresh(targetName, moduleName);
                string verificationError;
                int persistedPosition;
                bool verified = VerifyMove(persisted, resolved.Path, identity, plan.NewPosition,
                    snapshot, out persistedPosition, out verificationError);
                if (!verified)
                {
                    var restored = RestoreTransactionSnapshot(persisted ?? trn, snapshot);
                    return Models.McpResponse.Err(
                        code: "AttributeMoveNotPersisted",
                        message: verificationError,
                        hint: "GeneXus normalized the Structure or another member changed unexpectedly, so the complete Transaction snapshot was restored.",
                        target: targetName,
                        extra: new JObject
                        {
                            ["requestedPosition"] = plan.NewPosition,
                            ["persistedPosition"] = persistedPosition,
                            ["snapshot"] = snapshotPath,
                            ["rolledBack"] = restored.Success,
                            ["rollbackVerified"] = restored.Verified,
                            ["rollbackError"] = restored.Error
                        });
                }

                _objectService.GetKbService().GetIndexCache().UpdateEntry(persisted ?? trn);
                preview["code"] = "AttributeMoved";
                preview["dryRun"] = false;
                preview["previousAttribute"] = plan.NewPosition > 0
                    ? plan.OrderedNames[plan.NewPosition - 1]
                    : JValue.CreateNull();
                preview["persisted"] = true;
                preview["verified"] = true;
                preview["identityPreserved"] = true;
                preview["propertiesPreserved"] = true;
                preview["relativeOrderPreserved"] = true;
                preview["authoredPartsPreserved"] = true;
                preview["snapshot"] = snapshotPath;
                preview["versionToken"] = WriteService.ComputeVersionToken(persisted ?? trn);
                return Models.McpResponse.Ok(target: targetName, code: "AttributeMoved", result: preview);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AttributeMoveFailed",
                    message: ex.Message,
                    hint: "No Specify, Generate, Build, Rebuild, reorganization, or Pattern operation was run.",
                    target: targetName);
            }
        }

        /// <summary>
        /// Removes one native TransactionAttribute reference from a Transaction level while
        /// preserving the KB-global Attribute and every SubType Group membership.  GeneXus 18
        /// does not expose a removal method on TransactionLevel.Attributes, so this uses the
        /// same native TransactionLevel.Items storage as MoveAttribute and verifies a fresh
        /// post-save read before reporting success.
        /// </summary>
        public string RemoveAttribute(string targetName, JObject args)
        {
            args = args ?? new JObject();
            string moduleName = args["transactionModule"]?.ToString();
            string attributeName = args["attribute"]?.ToString() ?? args["name"]?.ToString();
            bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
            bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            string baseVersion = args["baseVersion"]?.ToString()
                ?? args["expectedVersion"]?.ToString()
                ?? args["versionToken"]?.ToString();

            if (string.IsNullOrWhiteSpace(attributeName))
                return Models.McpResponse.Err(
                    code: "AttributeNameRequired",
                    message: "The attribute to remove was not provided.",
                    hint: "Pass attribute=\"<name>\" (the Transaction member name).",
                    target: targetName);

            try
            {
                // Snapshot the persisted SDK instance, not the potentially stale read-cache
                // instance, for the same authored-part preservation guarantee as moves.
                var trn = ResolveTransactionFresh(targetName, moduleName);
                if (trn == null)
                    return Models.McpResponse.Err(
                        code: "TransactionNotFound",
                        message: string.IsNullOrWhiteSpace(moduleName)
                            ? $"Transaction '{targetName}' was not found."
                            : $"Transaction '{targetName}' was not found in module '{moduleName}'.",
                        target: targetName);

                string versionBefore = WriteService.ComputeVersionToken(trn);
                if (!string.IsNullOrWhiteSpace(baseVersion)
                    && !string.IsNullOrWhiteSpace(versionBefore)
                    && !string.Equals(baseVersion, versionBefore, StringComparison.Ordinal))
                    return Models.McpResponse.Err(
                        code: "StaleObject",
                        message: "The Transaction changed after the version supplied in baseVersion. The attribute reference was not removed.",
                        hint: "Re-read the Transaction and retry with its current versionToken.",
                        target: targetName,
                        extra: new JObject
                        {
                            ["expectedVersion"] = baseVersion,
                            ["currentVersion"] = versionBefore
                        });

                ResolvedLevel resolved;
                string levelError;
                if (!TryResolveLevel(trn, args, out resolved, out levelError))
                    return Models.McpResponse.Err(
                        code: "LevelNotFound",
                        message: levelError,
                        hint: "Use level=\"root\", an unambiguous level name, or levelPath=[\"Item\",\"Operation\"].",
                        target: targetName);

                var attributes = resolved.Level.Attributes.Cast<TransactionAttribute>().ToList();
                int oldPosition = attributes.FindIndex(a => string.Equals(a.Name, attributeName, StringComparison.OrdinalIgnoreCase));
                if (oldPosition < 0)
                    return Models.McpResponse.Err(
                        code: "AttributeNotFound",
                        message: $"Attribute '{attributeName}' does not belong to level '{resolved.DisplayName}'.",
                        target: targetName);

                var targetAttribute = attributes[oldPosition];
                var globalAttribute = targetAttribute.Attribute;
                if (globalAttribute == null)
                    return Models.McpResponse.Err(
                        code: "GlobalAttributeNotResolved",
                        message: $"The Transaction member '{attributeName}' is not bound to a KB-global Attribute; no write was attempted.",
                        target: targetName);

                string removedIdentity = AttributeIdentity(targetAttribute);
                string globalGuid = globalAttribute.Guid.ToString("D");
                string globalHash = Hash(CaptureEntity(globalAttribute).Data);
                JArray groupsBefore = CaptureSubtypeGroups(globalAttribute);
                var beforeNames = attributes.Select(a => a.Name).ToList();
                var afterNames = beforeNames.Where((n, i) => i != oldPosition).ToList();
                var result = BuildRemovalResult(targetName, moduleName, resolved, attributeName,
                    oldPosition, beforeNames, afterNames, globalAttribute.Name, globalGuid,
                    groupsBefore, dryRun, rollbackOnFailure, versionBefore);

                if (dryRun)
                    return Models.McpResponse.Ok(target: targetName, code: "AttributeRemovalPreview", result: result);

                TransactionSnapshot snapshot;
                try { snapshot = CaptureTransactionSnapshot(trn, removedIdentity, excludeIdentityDetails: true); }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "SnapshotFailed",
                        message: "The Transaction could not be snapshotted safely; no write was attempted. " + ex.Message,
                        target: targetName);
                }

                string snapshotPath = PersistStructureSnapshot(trn, snapshot, "remove-attribute");
                string writeFailure = null;
                using (var sdkTrans = trn.Model.KB.BeginTransaction())
                {
                    try
                    {
                        RemoveExistingAttribute(resolved.Level, targetAttribute);
                        trn.Structure.Dirty = true;
                        var preservedDefaults = EnumerateTransactionParts(trn)
                            .Where(p => !(p is StructurePart))
                            .OfType<Artech.Architecture.Common.Defaults.IApplyDefaultTarget>()
                            .ToList();
                        foreach (var preserved in preservedDefaults) preserved.PreserveDefaultLock();
                        try
                        {
                            trn.Save(new KBObjectSavePreferences
                            {
                                ForceSave = true,
                                ForceSaveDefaultParts = false,
                                SkipValidation = true
                            });
                        }
                        finally
                        {
                            foreach (var preserved in preservedDefaults) preserved.PreserveDefaultUnlock();
                        }

                        RestoreAuthoredTransactionParts(trn, snapshot);

                        sdkTrans.Commit();
                        WriteService.NotePerTargetWrite(targetName);
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        writeFailure = ex.Message;
                    }
                }

                if (writeFailure == null)
                {
                    try
                    {
                        using (var authoredTx = trn.Model.KB.BeginTransaction())
                        {
                            var authoredTarget = RefreshTransaction(trn) ?? trn;
                            RestoreAuthoredTransactionParts(authoredTarget, snapshot);
                            authoredTx.Commit();
                            WriteService.NotePerTargetWrite(targetName);
                        }
                    }
                    catch (Exception ex)
                    {
                        writeFailure = ex.Message;
                    }
                }

                if (writeFailure != null)
                {
                    var restored = rollbackOnFailure
                        ? RestoreTransactionSnapshot(trn, snapshot)
                        : new RestoreResult { Error = "rollbackOnFailure=false" };
                    return Models.McpResponse.Err(
                        code: "AttributeRemovalFailed",
                        message: writeFailure,
                        hint: rollbackOnFailure
                            ? "The pre-write snapshot was restored; inspect rollbackVerified before retrying."
                            : "Rollback was disabled; re-read the Transaction before retrying.",
                        target: targetName,
                        extra: new JObject
                        {
                            ["snapshot"] = snapshotPath,
                            ["rollbackRequested"] = rollbackOnFailure,
                            ["rolledBack"] = restored.Success,
                            ["rollbackVerified"] = restored.Verified,
                            ["rollbackError"] = restored.Error
                        });
                }

                var persisted = ResolveTransactionFresh(targetName, moduleName);

                string verificationError;
                JObject verification;
                bool verified = VerifyRemoval(persisted, resolved.Path, removedIdentity,
                    attributeName, globalGuid, globalHash, groupsBefore, snapshot,
                    out verification, out verificationError);
                if (!verified)
                {
                    var restored = rollbackOnFailure
                        ? RestoreTransactionSnapshot(persisted ?? trn, snapshot)
                        : new RestoreResult { Error = "rollbackOnFailure=false" };
                    return Models.McpResponse.Err(
                        code: "AttributeRemovalNotPersisted",
                        message: verificationError,
                        hint: rollbackOnFailure
                            ? "The complete pre-write Transaction snapshot was restored."
                            : "Rollback was disabled; inspect the persisted diff before making another edit.",
                        target: targetName,
                        extra: new JObject
                        {
                            ["snapshot"] = snapshotPath,
                            ["verification"] = verification,
                            ["rollbackRequested"] = rollbackOnFailure,
                            ["rolledBack"] = restored.Success,
                            ["rollbackVerified"] = restored.Verified,
                            ["rollbackError"] = restored.Error
                        });
                }

                _objectService.GetKbService().GetIndexCache().UpdateEntry(persisted ?? trn);
                result["snapshot"] = snapshotPath;
                result["saved"] = true;
                result["persistedVerified"] = true;
                result["verification"] = verification;
                result["versionToken"] = WriteService.ComputeVersionToken(persisted ?? trn);
                return Models.McpResponse.Ok(target: targetName, code: "AttributeRemoved", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AttributeRemovalFailed",
                    message: ex.Message,
                    hint: "No global Attribute or SubType Group is deleted by this operation.",
                    target: targetName);
            }
        }

        private Transaction ResolveTransaction(string name, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                return _objectService.FindObject(name, "Transaction") as Transaction;

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null) return null;
            foreach (KBObject obj in kb.DesignModel.Objects.GetByName(null, null, name))
            {
                var candidate = obj as Transaction;
                if (candidate == null) continue;
                string actualModule = null;
                try { actualModule = candidate.Module?.Name; } catch { }
                if (string.Equals(actualModule, moduleName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        private Transaction ResolveTransactionFresh(string name, string moduleName)
        {
            return RefreshTransaction(ResolveTransaction(name, moduleName));
        }

        private Transaction RefreshTransaction(Transaction transaction)
        {
            if (transaction == null) return null;
            _objectService.MarkReadCacheDirty(transaction);
            var kb = _objectService.GetKbService().GetKB();
            // Reload() is not a read-only operation for GX18's lazy PatternVirtual
            // projections: on a Transaction it can detach the direct Rules/Events
            // source from the loaded object and expose an empty projection. Re-resolve
            // through the model after invalidating the MCP read cache, but never call
            // the destructive part-level Reload hook here.
            return kb?.DesignModel.Objects.Get(transaction.Guid) as Transaction ?? transaction;
        }

        private sealed class ResolvedLevel
        {
            public TransactionLevel Level { get; set; }
            public List<string> Path { get; set; }
            public string DisplayName => Path == null || Path.Count == 0 ? "root" : string.Join("/", Path);
        }

        private static bool TryResolveLevel(Transaction trn, JObject args, out ResolvedLevel resolved, out string error)
        {
            resolved = null;
            error = null;
            var pathToken = args["levelPath"] as JArray;
            string levelName = args["level"]?.ToString();
            if (pathToken != null && pathToken.Count > 0 && !string.IsNullOrWhiteSpace(levelName)
                && !string.Equals(levelName, "root", StringComparison.OrdinalIgnoreCase))
            {
                error = "Use either level or levelPath, not both.";
                return false;
            }

            if (pathToken != null && pathToken.Count > 0)
            {
                var names = pathToken.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                TransactionLevel current = trn.Structure.Root;
                if (names.Count > 0 && string.Equals(names[0], current.Name, StringComparison.OrdinalIgnoreCase))
                    names.RemoveAt(0);
                var walked = new List<string>();
                foreach (string name in names)
                {
                    current = current.Levels.Cast<TransactionLevel>()
                        .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (current == null)
                    {
                        error = $"Level path '{string.Join("/", names)}' was not found.";
                        return false;
                    }
                    walked.Add(current.Name);
                }
                resolved = new ResolvedLevel { Level = current, Path = walked };
                return true;
            }

            if (string.IsNullOrWhiteSpace(levelName)
                || string.Equals(levelName, "root", StringComparison.OrdinalIgnoreCase)
                || string.Equals(levelName, trn.Structure.Root.Name, StringComparison.OrdinalIgnoreCase))
            {
                resolved = new ResolvedLevel { Level = trn.Structure.Root, Path = new List<string>() };
                return true;
            }

            var matches = new List<ResolvedLevel>();
            CollectLevels(trn.Structure.Root, new List<string>(), levelName, matches);
            if (matches.Count == 1) { resolved = matches[0]; return true; }
            error = matches.Count == 0
                ? $"Level '{levelName}' was not found."
                : $"Level '{levelName}' is ambiguous; use levelPath.";
            return false;
        }

        private static void CollectLevels(TransactionLevel parent, List<string> path, string wanted, List<ResolvedLevel> matches)
        {
            foreach (TransactionLevel child in parent.Levels)
            {
                var childPath = new List<string>(path) { child.Name };
                if (string.Equals(child.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    matches.Add(new ResolvedLevel { Level = child, Path = childPath });
                CollectLevels(child, childPath, wanted, matches);
            }
        }

        // ── Issue #97: subtype-attribute classification guard-rail ────────────────────
        //
        // A subtype attribute (IS_SUBTYPE=True) added to a Transaction level
        // programmatically can come out of the SDK classified as stored (SECONDARY)
        // instead of derived (INFERRED) — the SDK only recomputes the class through
        // the IDE's SubtypeGroup editor. The result is a silent data-integrity bug:
        // a physical column is created and supertype propagation breaks. The MCP
        // cannot force the recomputation through the SDK (IsInferred is read-only),
        // but it CAN detect the mismatch — an attribute whose same-supertype siblings
        // on the same level are INFERRED while it is not — and surface a structured
        // warning instead of reporting silent success.

        /// <summary>genexus_structure action=check_subtypes entry point.</summary>
        public string CheckSubtypeClassification(string targetName, JObject args)
        {
            args = args ?? new JObject();
            string moduleName = args["transactionModule"]?.ToString();
            try
            {
                var trn = ResolveTransaction(targetName, moduleName);
                if (trn == null)
                    return Models.McpResponse.Err(
                        code: "TransactionNotFound",
                        message: string.IsNullOrWhiteSpace(moduleName)
                            ? $"Transaction '{targetName}' was not found."
                            : $"Transaction '{targetName}' was not found in module '{moduleName}'.",
                        target: targetName);

                var issues = ComputeSubtypeClassificationIssues(trn);
                var result = new JObject
                {
                    ["check"] = "subtype_inferred_mismatch",
                    ["status"] = issues.Count == 0 ? "ok" : "warning",
                    ["transaction"] = targetName,
                    ["issues"] = issues
                };
                if (issues.Count > 0)
                {
                    result["hint"] = "One or more subtype attributes on '" + targetName
                        + "' are classified as stored (SECONDARY) while sibling subtypes of the same supertype on the same level are derived (INFERRED). This creates a physical column and breaks supertype propagation. The SDK only recomputes the class through the IDE's SubtypeGroup editor; via MCP, remove the attribute with genexus_structure action=remove_attribute (or genexus_edit part=Structure with a single remove_attribute op) and re-add it, then re-run this check. Confirm with genexus_properties action=get type=Attribute (Class).";
                }
                return Models.McpResponse.Ok(
                    target: targetName,
                    code: issues.Count == 0 ? "SubtypeClassificationOk" : "SubtypeClassificationWarning",
                    result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "SubtypeClassificationFailed",
                    message: ex.Message,
                    target: targetName);
            }
        }

        /// <summary>
        /// Walks every level of the transaction and returns the subtype attributes whose
        /// classification diverges from their same-supertype siblings on the same level
        /// (sibling subtypes derived from the same supertype must share classification:
        /// all INFERRED or all stored). Empty array = no mismatch. Callers pass the
        /// post-save re-read so the classification reflects what the SDK persisted.
        /// </summary>
        public JArray ComputeSubtypeClassificationIssues(Transaction trn)
        {
            if (trn?.Structure?.Root == null) return new JArray();

            var views = new List<SubtypeAttrView>();
            var levels = new List<(TransactionLevel Level, string Path)>();
            CollectAllLevels(trn.Structure.Root, "root", levels);
            foreach (var (level, path) in levels)
            {
                // OfType (not Cast): a non-attribute item in the collection must be
                // skipped, never throw — the guard-rail runs on the write success path.
                foreach (TransactionAttribute ta in level.Attributes.OfType<TransactionAttribute>())
                {
                    var global = ta.Attribute;
                    if (global?.SuperType == null) continue; // not a subtype attribute
                    views.Add(new SubtypeAttrView
                    {
                        Level = path,
                        Name = ta.Name,
                        Supertype = global.SuperType.Name ?? "?",
                        IsInferred = ta.IsInferred,
                        Guid = ta.Guid.ToString("D")
                    });
                }
            }

            return FindMismatchedSubtypeClassifications(views);
        }

        /// <summary>
        /// Guard-rail wrapper used by the post-write hooks (UpdateVisualStructure and the
        /// genexus_edit Structure DSL path): the classification check is ADVISORY, so a
        /// detection failure must never turn a successful write into an error — any
        /// exception yields an empty issue list.
        /// </summary>
        internal JArray TryComputeSubtypeClassificationIssues(Transaction trn)
        {
            try { return ComputeSubtypeClassificationIssues(trn); }
            catch { return new JArray(); }
        }

        /// <summary>
        /// Pure detection kernel (unit-testable without the SDK). Given a level's subtype
        /// attributes as lightweight views, returns the ones whose classification diverges
        /// from their same-supertype siblings — sibling subtypes derived from the same
        /// supertype on the same level must share classification (all INFERRED or all
        /// stored), so a stored attribute among inferred siblings is the issue #97 bug.
        /// </summary>
        internal static JArray FindMismatchedSubtypeClassifications(IEnumerable<SubtypeAttrView> attrs)
        {
            var issues = new JArray();
            if (attrs == null) return issues;

            // Group by (level, supertype): the #97 rule is about same-supertype SIBLINGS
            // ON THE SAME LEVEL. Two attributes sharing a supertype on DIFFERENT levels
            // of the same transaction are independent memberships and must not flag each
            // other (e.g. a stored subtype on a detail level next to an inferred one on
            // the root level is legitimate).
            var byLevelAndSupertype = new Dictionary<string, List<SubtypeAttrView>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in attrs)
            {
                if (a == null || string.IsNullOrEmpty(a.Supertype)) continue;
                string key = (a.Level ?? string.Empty) + "\u0001" + a.Supertype;
                if (!byLevelAndSupertype.TryGetValue(key, out var list))
                {
                    list = new List<SubtypeAttrView>();
                    byLevelAndSupertype[key] = list;
                }
                list.Add(a);
            }

            foreach (var kv in byLevelAndSupertype)
            {
                bool anyInferred = kv.Value.Any(a => a.IsInferred);
                bool anyStored = kv.Value.Any(a => !a.IsInferred);
                if (!anyInferred || !anyStored) continue; // homogeneous — no mismatch
                foreach (var a in kv.Value.Where(a => !a.IsInferred))
                {
                    issues.Add(new JObject
                    {
                        ["level"] = a.Level,
                        ["attribute"] = a.Name,
                        ["supertype"] = a.Supertype,
                        ["expected"] = "INFERRED",
                        ["actual"] = "SECONDARY",
                        ["guid"] = a.Guid
                    });
                }
            }

            return issues;
        }

        // Lightweight, SDK-free projection of a level's subtype attribute — feeds the
        // pure detection kernel above so the mismatch rule is unit-testable.
        internal sealed class SubtypeAttrView
        {
            public string Level { get; set; }
            public string Name { get; set; }
            public string Supertype { get; set; }
            public bool IsInferred { get; set; }
            public string Guid { get; set; }
        }

        private static void CollectAllLevels(TransactionLevel parent, string path, List<(TransactionLevel Level, string Path)> all)
        {
            all.Add((parent, path));
            foreach (TransactionLevel child in parent.Levels)
            {
                string childPath = string.Equals(path, "root", StringComparison.Ordinal) ? child.Name : path + "/" + child.Name;
                CollectAllLevels(child, childPath, all);
            }
        }

        private static void MoveExistingAttribute(TransactionLevel level, TransactionAttribute target, int newAttributePosition)
        {
            var items = level.Items;
            var attributeSlots = new List<int>();
            var reordered = new List<TransactionAttribute>();
            for (int i = 0; i < items.Count; i++)
            {
                var attr = items[i] as TransactionAttribute;
                if (attr == null) continue;
                attributeSlots.Add(i);
                reordered.Add(attr);
            }

            int oldPosition = reordered.IndexOf(target);
            if (oldPosition < 0)
                throw new InvalidOperationException("The native TransactionAttribute is not present in its level Items collection.");
            reordered.RemoveAt(oldPosition);
            reordered.Insert(newAttributePosition, target);

            // Assign the existing native instances directly in BaseCollection.m_Inner.
            // Even IList.set_Item raises DataChanged and makes GeneXus synchronize the
            // default WinForm/WebForm. The editor's reorder semantics only alter order;
            // bypassing add/remove/set events is therefore deliberate and identity-safe.
            var innerField = FindField(items.GetType(), "m_Inner");
            var inner = innerField?.GetValue(items) as System.Collections.IList;
            if (inner == null)
                throw new InvalidOperationException("The SDK's native Structure item storage could not be resolved.");
            for (int i = 0; i < attributeSlots.Count; i++)
            {
                inner[attributeSlots[i]] = reordered[i];
                reordered[i].Parent = level;
            }
            var dirtyProperty = items.GetType().GetProperty("Dirty",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            dirtyProperty?.SetValue(items, true, null);
        }

        private static void RemoveExistingAttribute(TransactionLevel level, TransactionAttribute target)
        {
            var items = level.Items;
            var innerField = FindField(items.GetType(), "m_Inner");
            var inner = innerField?.GetValue(items) as System.Collections.IList;
            if (inner == null)
                throw new InvalidOperationException("The SDK's native Structure item storage could not be resolved.");

            int itemIndex = -1;
            for (int i = 0; i < inner.Count; i++)
            {
                if (object.ReferenceEquals(inner[i], target)) { itemIndex = i; break; }
            }
            if (itemIndex < 0)
                throw new InvalidOperationException("The native TransactionAttribute is not present in its level Items collection.");

            // Remove only the TransactionAttribute reference.  Calling Delete/Remove on the
            // KB-global Attribute is intentionally never part of this path.
            inner.RemoveAt(itemIndex);
            var dirtyProperty = items.GetType().GetProperty("Dirty",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            dirtyProperty?.SetValue(items, true, null);
        }

        private static JObject BuildRemovalResult(string targetName, string moduleName,
            ResolvedLevel resolved, string attributeName, int oldPosition,
            IList<string> beforeNames, IList<string> afterNames,
            string globalName, string globalGuid, JArray subtypeGroups,
            bool dryRun, bool rollbackOnFailure, string versionBefore)
        {
            return new JObject
            {
                ["operation"] = "remove_attribute",
                ["transaction"] = targetName,
                ["module"] = moduleName,
                ["level"] = resolved.DisplayName,
                ["levelPath"] = new JArray(resolved.Path ?? new List<string>()),
                ["attribute"] = attributeName,
                ["dryRun"] = dryRun,
                ["rollbackOnFailure"] = rollbackOnFailure,
                ["versionBefore"] = versionBefore,
                ["before"] = new JObject
                {
                    ["position"] = oldPosition,
                    ["attributes"] = new JArray(beforeNames)
                },
                ["after"] = new JObject
                {
                    ["position"] = null,
                    ["attributes"] = new JArray(afterNames)
                },
                ["diff"] = new JObject
                {
                    ["removed"] = new JArray(new JObject
                    {
                        ["name"] = attributeName,
                        ["position"] = oldPosition,
                        ["level"] = resolved.DisplayName
                    }),
                    ["added"] = new JArray(),
                    ["moved"] = new JArray()
                },
                ["globalAttribute"] = new JObject
                {
                    ["name"] = globalName,
                    ["guid"] = globalGuid,
                    ["preserved"] = true
                },
                ["subtypeGroups"] = new JObject
                {
                    ["before"] = subtypeGroups.DeepClone(),
                    ["after"] = subtypeGroups.DeepClone(),
                    ["preserved"] = true
                }
            };
        }

        private JArray CaptureSubtypeGroups(Artech.Genexus.Common.Objects.Attribute attribute)
        {
            var memberships = new JArray();
            if (attribute == null) return memberships;
            var kb = _objectService.GetKbService().GetKB();
            if (kb == null) return memberships;

            foreach (KBObject candidate in kb.DesignModel.Objects.GetAll())
            {
                var group = candidate as Group;
                if (group == null) continue;
                var part = group.Parts.Get<GroupStructurePart>();
                if (part == null) continue;
                foreach (var member in part.Members)
                {
                    if (member?.Subtype == null || member.Subtype.Guid != attribute.Guid) continue;
                    memberships.Add(new JObject
                    {
                        ["group"] = group.Name,
                        ["groupGuid"] = group.Guid.ToString("D"),
                        ["subtype"] = member.Subtype.Name,
                        ["supertype"] = member.Supertype?.Name
                    });
                }
            }

            return new JArray(memberships.OfType<JObject>()
                .OrderBy(m => m["group"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(m => (JToken)m));
        }

        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                var field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        private sealed class TransactionSnapshot
        {
            public Dictionary<string, EntitySnapshot> Parts { get; } = new Dictionary<string, EntitySnapshot>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<Guid, EntitySnapshot> GlobalAttributes { get; } = new Dictionary<Guid, EntitySnapshot>();
            public HashSet<Guid> GlobalAttributeIds { get; } = new HashSet<Guid>();
            public bool GlobalAttributeIdsCaptured { get; set; }
            public JObject Integrity { get; set; }
            public string StructurePartKey { get; set; }
        }

        private sealed class EntitySnapshot
        {
            public string Format { get; set; }
            public byte[] Data { get; set; }
            public string VerificationFormat { get; set; }
            public byte[] VerificationData { get; set; }
            public bool VerifyAuthored { get; set; }
        }

        private sealed class RestoreResult
        {
            public bool Success { get; set; }
            public bool Verified { get; set; }
            public string Error { get; set; }
        }

        // Transaction.Rules and Transaction.Events can be exposed twice by GX18: once
        // through the typed property and once through Parts. One duplicate can be an empty
        // placeholder, so coalesce each type and retain the source-bearing native part.
        private static IEnumerable<KBObjectPart> EnumerateTransactionParts(Transaction trn)
        {
            if (trn == null) yield break;

            var candidates = new List<KBObjectPart>();
            if (trn.Rules != null) candidates.Add(trn.Rules);
            if (trn.Events != null) candidates.Add(trn.Events);
            candidates.AddRange(trn.Parts.Cast<KBObjectPart>().Where(p => p != null));

            foreach (var group in candidates.GroupBy(p => p.Type.ToString("D"), StringComparer.OrdinalIgnoreCase))
            {
                var part = group.FirstOrDefault(p => p is Artech.Architecture.Common.Objects.ISource
                    && !string.IsNullOrEmpty(((Artech.Architecture.Common.Objects.ISource)p).Source))
                    ?? group.FirstOrDefault(p => p is Artech.Architecture.Common.Objects.ISource)
                    ?? group.First();
                yield return part;
            }
        }

        private static KBObjectPart FindTransactionPart(Transaction trn, string typeKey)
        {
            return EnumerateTransactionParts(trn)
                .FirstOrDefault(p => string.Equals(p.Type.ToString("D"), typeKey, StringComparison.OrdinalIgnoreCase));
        }

        private static void RestoreAuthoredTransactionParts(Transaction trn, TransactionSnapshot snapshot)
        {
            foreach (var kvp in snapshot.Parts)
            {
                var expected = kvp.Value;
                if (expected == null || !expected.VerifyAuthored) continue;

                var part = FindTransactionPart(trn, kvp.Key);
                if (part == null)
                    throw new InvalidOperationException("Authored Transaction part '" + kvp.Key + "' disappeared during the structure save.");

                RestoreEntity(part, expected);
                part.Dirty = true;
                part.Save();
            }

            // KBObjectPart.Save() updates the part entity but does not reliably flush a
            // direct Transaction part after the parent Save. Mirror the normal source
            // writer's persistence sequence so Rules/Events reach the KB store.
            if (snapshot.Parts.Values.Any(p => p != null && p.VerifyAuthored))
                trn.EnsureSave(false);
        }

        private static TransactionSnapshot CaptureTransactionSnapshot(Transaction trn, string movedIdentity = null,
            bool excludeIdentityDetails = false)
        {
            var snapshot = new TransactionSnapshot();
            foreach (KBObjectPart part in EnumerateTransactionParts(trn))
            {
                string key = part.Type.ToString("D");
                snapshot.Parts[key] = CaptureEntity(part);
                if (part is StructurePart) snapshot.StructurePartKey = key;
            }
            if (string.IsNullOrWhiteSpace(snapshot.StructurePartKey))
                throw new InvalidOperationException("The Transaction Structure part was not found.");
            CaptureGlobalAttributeSnapshots(trn.Structure.Root, snapshot.GlobalAttributes);
            snapshot.GlobalAttributeIdsCaptured = CaptureGlobalAttributeIds(trn, snapshot.GlobalAttributeIds);
            snapshot.Integrity = CaptureStructureIntegrity(trn, movedIdentity, excludeIdentityDetails);
            return snapshot;
        }

        private static void CaptureGlobalAttributeSnapshots(TransactionLevel level,
            Dictionary<Guid, EntitySnapshot> snapshots)
        {
            foreach (TransactionAttribute occurrence in level.Attributes)
            {
                var attribute = occurrence.Attribute;
                if (attribute != null && !snapshots.ContainsKey(attribute.Guid))
                    snapshots[attribute.Guid] = CaptureEntity(attribute);
            }
            foreach (TransactionLevel child in level.Levels)
                CaptureGlobalAttributeSnapshots(child, snapshots);
        }

        private static bool CaptureGlobalAttributeIds(Transaction trn, HashSet<Guid> ids)
        {
            if (trn?.Model?.Objects == null || ids == null) return false;
            try
            {
                foreach (var obj in ((System.Collections.IEnumerable)trn.Model.Objects.GetAll()).Cast<KBObject>())
                {
                    if (obj is Artech.Genexus.Common.Objects.Attribute attribute)
                        ids.Add(attribute.Guid);
                }
                // An empty set is valid for a brand-new/attribute-free model, but
                // only after the complete enumeration succeeded.
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not capture global Attribute identities for rollback: " + ex.Message);
                return false;
            }
        }

        private static JObject CaptureStructureIntegrity(Transaction trn, string movedIdentity,
            bool excludeIdentityDetails = false)
        {
            var details = new JObject();
            var order = new JObject();
            CaptureLevelIntegrity(trn.Structure.Root, "root", movedIdentity, details, order, excludeIdentityDetails);
            return new JObject { ["details"] = details, ["orderWithoutMovedAttribute"] = order };
        }

        private static void CaptureLevelIntegrity(TransactionLevel level, string path, string movedIdentity,
            JObject details, JObject order, bool excludeIdentityDetails = false)
        {
            string levelIdentity = "level:" + level.Guid.ToString("D");
            var attrOrder = new JArray();
            foreach (TransactionAttribute attr in level.Attributes)
            {
                string id = AttributeIdentity(attr);
                if (!excludeIdentityDetails || !string.Equals(id, movedIdentity, StringComparison.OrdinalIgnoreCase))
                    details[id] = new JObject
                    {
                        ["name"] = attr.Name,
                        ["level"] = levelIdentity,
                        ["transactionAttributeId"] = attr.Id,
                        ["transactionAttributeGuid"] = attr.Guid.ToString("D"),
                        ["globalAttributeGuid"] = attr.Attribute?.Guid.ToString("D"),
                        ["itemHash"] = Hash(SerializeItem(attr)),
                        ["globalAttributeHash"] = attr.Attribute == null ? null : Hash(CaptureEntity(attr.Attribute).Data)
                    };
                if (!string.Equals(id, movedIdentity, StringComparison.OrdinalIgnoreCase)) attrOrder.Add(id);
            }
            var levelOrder = new JArray(level.Levels.Cast<TransactionLevel>().Select(l => l.Guid.ToString("D")));
            order[levelIdentity] = new JObject { ["attributes"] = attrOrder, ["levels"] = levelOrder, ["path"] = path };
            foreach (TransactionLevel child in level.Levels)
                CaptureLevelIntegrity(child, path + "/" + child.Name, movedIdentity, details, order, excludeIdentityDetails);
        }

        private static byte[] SerializeItem(Artech.Common.Helpers.Structure.IStructureItem item)
        {
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                ConformanceLevel = ConformanceLevel.Fragment
            })) item.Serialize(writer);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static string AttributeIdentity(TransactionAttribute attr) =>
            "attribute:" + (attr.Attribute?.Guid ?? attr.Guid).ToString("D");

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data ?? new byte[0])).Replace("-", string.Empty);
        }

        private string PersistStructureSnapshot(Transaction trn, TransactionSnapshot snapshot, string operation = "move-attribute")
        {
            try
            {
                string root = EditSnapshotStore.ResolveRoot(_objectService.GetKbService().GetKbPath());
                string content = Convert.ToBase64String(snapshot.Parts[snapshot.StructurePartKey].Data);
                var info = EditSnapshotStore.SaveSnapshot(root, trn.Guid.ToString(), "StructureBinary-" + operation, content);
                return info?.Path;
            }
            catch (Exception ex)
            {
                Logger.Warn("[MOVE-ATTRIBUTE] Persistent snapshot skipped: " + ex.Message);
                return null;
            }
        }

        private static bool VerifyMove(Transaction persisted, List<string> levelPath, string movedIdentity,
            int requestedPosition, TransactionSnapshot before, out int persistedPosition, out string error)
        {
            persistedPosition = -1;
            error = null;
            if (persisted == null) { error = "The Transaction could not be re-read after save."; return false; }

            var args = new JObject { ["levelPath"] = new JArray(levelPath ?? new List<string>()) };
            ResolvedLevel resolved;
            string levelError;
            if (!TryResolveLevel(persisted, args, out resolved, out levelError))
            { error = levelError; return false; }

            var attrs = resolved.Level.Attributes.Cast<TransactionAttribute>().ToList();
            persistedPosition = attrs.FindIndex(a => string.Equals(AttributeIdentity(a), movedIdentity, StringComparison.OrdinalIgnoreCase));
            if (persistedPosition != requestedPosition)
            {
                error = $"GeneXus persisted the attribute at position {persistedPosition}, not requested position {requestedPosition}.";
                return false;
            }

            JObject afterIntegrity = CaptureStructureIntegrity(persisted, movedIdentity);
            if (!JToken.DeepEquals(before.Integrity, afterIntegrity))
            {
                error = "A Structure member identity, property, level membership, or relative order changed unexpectedly.";
                return false;
            }

            if (!VerifyAuthoredPartsPreserved(persisted, before.Parts, out string authoredErr))
            {
                error = authoredErr;
                return false;
            }
            return true;
        }

        private bool VerifyRemoval(Transaction persisted, List<string> levelPath, string removedIdentity,
            string attributeName, string globalGuid, string globalHash, JArray groupsBefore,
            TransactionSnapshot before, out JObject verification, out string error)
        {
            verification = new JObject
            {
                ["referenceRemoved"] = false,
                ["globalAttributePreserved"] = false,
                ["subtypeGroupsPreserved"] = false,
                ["otherStructureMembersPreserved"] = false,
                ["authoredPartsPreserved"] = false
            };
            error = null;
            if (persisted == null) { error = "The Transaction could not be re-read after save."; return false; }

            var levelArgs = new JObject { ["levelPath"] = new JArray(levelPath ?? new List<string>()) };
            ResolvedLevel resolved;
            string levelError;
            if (!TryResolveLevel(persisted, levelArgs, out resolved, out levelError))
            { error = levelError; return false; }

            bool stillPresent = resolved.Level.Attributes.Cast<TransactionAttribute>()
                .Any(a => string.Equals(AttributeIdentity(a), removedIdentity, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Name, attributeName, StringComparison.OrdinalIgnoreCase));
            if (stillPresent)
            {
                error = $"Attribute '{attributeName}' is still present in the persisted level '{resolved.DisplayName}'.";
                return false;
            }
            verification["referenceRemoved"] = true;

            JObject afterIntegrity = CaptureStructureIntegrity(persisted, removedIdentity, excludeIdentityDetails: true);
            if (!JToken.DeepEquals(before.Integrity, afterIntegrity))
            {
                error = "A Structure member other than the requested attribute changed unexpectedly.";
                return false;
            }
            verification["otherStructureMembersPreserved"] = true;

            var global = Artech.Genexus.Common.Objects.Attribute.Get(persisted.Model, attributeName);
            if (global == null || !string.Equals(global.Guid.ToString("D"), globalGuid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(CaptureEntity(global).Data), globalHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "The KB-global Attribute was removed or changed while detaching the Transaction reference.";
                return false;
            }
            verification["globalAttributePreserved"] = true;

            JArray groupsAfter = CaptureSubtypeGroups(global);
            verification["subtypeGroupsBefore"] = groupsBefore.DeepClone();
            verification["subtypeGroupsAfter"] = groupsAfter.DeepClone();
            if (!JToken.DeepEquals(groupsBefore, groupsAfter))
            {
                error = "A SubType Group membership changed while detaching the Transaction reference.";
                return false;
            }
            verification["subtypeGroupsPreserved"] = true;

            if (!VerifyAuthoredPartsPreserved(persisted, before.Parts, out string removeAuthoredErr))
            {
                error = removeAuthoredErr;
                return false;
            }
            verification["authoredPartsPreserved"] = true;
            return true;
        }

        internal static bool ShouldVerifyAuthoredPart(bool isStructure, bool isDefault,
            string verificationFormat, int verificationLength)
        {
            // A non-default part is authored even when its source/XML is empty. Default
            // projections (WebForm/WinForm/Variables and similar parts) are regenerated
            // from the changed Structure by GeneXus and must not be compared as if their
            // generated bytes were user-authored content.
            return !isStructure && !isDefault;
        }

        private static bool VerifyAuthoredPartsPreserved(Transaction persisted, Dictionary<string, EntitySnapshot> beforeParts, out string error)
        {
            error = null;
            if (persisted == null || beforeParts == null) return true;

            foreach (var kvp in beforeParts)
            {
                string key = kvp.Key;
                EntitySnapshot expected = kvp.Value;
                if (expected == null || !expected.VerifyAuthored) continue;

                var part = FindTransactionPart(persisted, key);
                if (part == null || !EntityMatches(part, expected))
                {
                    error = $"Authored part '{part?.TypeDescriptor?.Name ?? key}' changed or was wiped unexpectedly.";
                    return false;
                }
            }

            foreach (KBObjectPart part in EnumerateTransactionParts(persisted))
            {
                if (part is StructurePart) continue;
                EntitySnapshot expected;
                if (!beforeParts.TryGetValue(part.Type.ToString("D"), out expected))
                {
                    if (part.IsDefault) continue;
                    error = $"Part '{part.TypeDescriptor?.Name ?? part.Type.ToString("D")}' changed unexpectedly.";
                    return false;
                }
                if (expected == null || !expected.VerifyAuthored) continue;
                if (!EntityMatches(part, expected))
                {
                    error = $"Part '{part.TypeDescriptor?.Name ?? part.Type.ToString("D")}' changed unexpectedly.";
                    return false;
                }
            }
            return true;
        }

        private RestoreResult RestoreTransactionSnapshot(Transaction trn, TransactionSnapshot snapshot)
        {
            var result = new RestoreResult();
            if (trn == null || snapshot == null) { result.Error = "Snapshot or Transaction unavailable."; return result; }
            try
            {
                using (var tx = trn.Model.KB.BeginTransaction())
                {
                    try
                    {
                        // Restore the Structure first and save the Transaction so GX18
                        // persists the original order. Default form projections follow
                        // that restored Structure automatically.
                        foreach (KBObjectPart part in EnumerateTransactionParts(trn))
                        {
                            if (!(part is StructurePart)) continue;
                            EntitySnapshot data;
                            if (!snapshot.Parts.TryGetValue(part.Type.ToString("D"), out data))
                                throw new InvalidOperationException("Structure snapshot missing during rollback.");
                            RestoreEntity(part, data);
                            part.Dirty = true;
                        }
                        trn.Save(new KBObjectSavePreferences
                        {
                            ForceSave = true,
                            ForceSaveDefaultParts = false,
                            SkipValidation = true
                        });

                        // Restore user-authored non-default parts after the Transaction
                        // save, compensating any unexpected SDK side effect.
                        foreach (KBObjectPart part in EnumerateTransactionParts(trn))
                        {
                            if (part is StructurePart) continue;
                            EntitySnapshot data;
                            if (!snapshot.Parts.TryGetValue(part.Type.ToString("D"), out data)
                                || data == null || !data.VerifyAuthored) continue;
                            RestoreEntity(part, data);
                            part.Dirty = true;
                            part.Save();
                        }

                        foreach (var pair in snapshot.GlobalAttributes)
                        {
                            var attribute = trn.Model.Objects.Get(pair.Key) as Artech.Genexus.Common.Objects.Attribute
                                ?? throw new InvalidOperationException("Global Attribute missing during rollback: " + pair.Key);
                            RestoreEntity(attribute, pair.Value);
                            attribute.Dirty = true;
                            attribute.Save();
                        }
                        RemoveOrphanedGlobalAttributes(trn, snapshot);
                        tx.Commit();
                        result.Success = true;
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                }

                // A parent rollback/save can reapply a default projection after its
                // transaction closes. Persist the authored snapshot once more in an
                // independent transaction before declaring rollback successful.
                using (var authoredTx = trn.Model.KB.BeginTransaction())
                {
                    var authoredTarget = RefreshTransaction(trn) ?? trn;
                    RestoreAuthoredTransactionParts(authoredTarget, snapshot);
                    authoredTx.Commit();
                }

                var verifiedTransaction = RefreshTransaction(trn) ?? trn;
                result.Verified = EnumerateTransactionParts(verifiedTransaction).All(part =>
                {
                    if (!(part is StructurePart) && part.IsDefault)
                    {
                        EntitySnapshot authoredDefault;
                        return !snapshot.Parts.TryGetValue(part.Type.ToString("D"), out authoredDefault)
                            || authoredDefault == null || !authoredDefault.VerifyAuthored
                            || EntityMatches(part, authoredDefault);
                    }
                    EntitySnapshot expected;
                    return snapshot.Parts.TryGetValue(part.Type.ToString("D"), out expected)
                        && expected != null
                        && (part is StructurePart || expected.VerifyAuthored)
                        && EntityMatches(part, expected);
                }) && snapshot.GlobalAttributes.All(pair =>
                {
                    var attribute = trn.Model.Objects.Get(pair.Key) as Artech.Genexus.Common.Objects.Attribute;
                    return attribute != null && EntityMatches(attribute, pair.Value);
                });
                if (!result.Verified) result.Error = "Rollback saved, but byte-for-byte verification did not match every part.";
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        private static void RemoveOrphanedGlobalAttributes(Transaction trn, TransactionSnapshot snapshot)
        {
            if (trn?.Model?.Objects == null || snapshot == null || !snapshot.GlobalAttributeIdsCaptured) return;
            var referenced = new HashSet<Guid>();
            CollectReferencedGlobalAttributes(trn.Structure?.Root, referenced);
            var all = ((System.Collections.IEnumerable)trn.Model.Objects.GetAll())
                .Cast<KBObject>()
                .OfType<Artech.Genexus.Common.Objects.Attribute>()
                .ToList();
            foreach (var attribute in all)
            {
                if (attribute == null || snapshot.GlobalAttributeIds.Contains(attribute.Guid)
                    || referenced.Contains(attribute.Guid)) continue;
                // Delete vs Remove varies between GeneXus majors; the candidate order
                // lives in the compatibility adapter, not inline here.
                if (!SdkDeletionAdapter.TryDeleteOrRemove(attribute))
                    throw new InvalidOperationException(
                        "Could not remove orphaned global Attribute '" + attribute.Name
                        + "': no Delete/Remove member is available on this SDK.");
            }
        }

        private static void CollectReferencedGlobalAttributes(TransactionLevel level, HashSet<Guid> referenced)
        {
            if (level == null) return;
            foreach (TransactionAttribute occurrence in level.Attributes)
            {
                if (occurrence.Attribute != null) referenced.Add(occurrence.Attribute.Guid);
            }
            foreach (TransactionLevel child in level.Levels)
                CollectReferencedGlobalAttributes(child, referenced);
        }

        // SerializeData/DeserializeData/Reload are protected on the SDK Entity base class.
        // Reflection is intentionally isolated here so snapshots preserve the exact native
        // part bytes instead of round-tripping through the lossy public Structure DSL.
        private static byte[] SerializeEntityData(object entity)
        {
            if (entity == null) return null;
            var method = FindEntityMethod(entity.GetType(), "SerializeData", Type.EmptyTypes);
            if (method == null) throw new MissingMethodException(entity.GetType().FullName, "SerializeData");
            try { return method.Invoke(entity, null) as byte[]; }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static EntitySnapshot CaptureEntity(object entity)
        {
            byte[] binary = SerializeEntityData(entity);
            var source = entity as Artech.Architecture.Common.Objects.ISource;
            var part = entity as KBObjectPart;
            var kbObject = entity as KBObject;

            string format;
            byte[] data;
            if (binary != null) { format = "binary"; data = binary; }
            else if (source != null) { format = "source"; data = Encoding.UTF8.GetBytes(source.Source ?? string.Empty); }
            else if (part != null) { format = "xml"; data = Encoding.UTF8.GetBytes(part.SerializeToXml() ?? string.Empty); }
            else if (kbObject != null) { format = "xml-readonly"; data = Encoding.UTF8.GetBytes(kbObject.SerializeToXml() ?? string.Empty); }
            else throw new InvalidOperationException("No lossless snapshot representation is available for " + entity?.GetType().FullName + ".");

            // Verification intentionally uses authored text/XML when available. Native
            // SerializeData can contain SDK bookkeeping that changes on a parent save even
            // when Rules/Events/forms/PatternInstance content is byte-for-byte unchanged.
            string verificationFormat = format;
            byte[] verificationData = data;
            if (source != null)
            {
                verificationFormat = "source";
                verificationData = Encoding.UTF8.GetBytes(source.Source ?? string.Empty);
            }
            else if (part != null)
            {
                verificationFormat = "xml";
                verificationData = Encoding.UTF8.GetBytes(part.SerializeToXml() ?? string.Empty);
            }
            else if (kbObject != null)
            {
                verificationFormat = "xml";
                verificationData = Encoding.UTF8.GetBytes(kbObject.SerializeToXml() ?? string.Empty);
            }

            bool verifyAuthored = ShouldVerifyAuthoredPart(
                isStructure: part is StructurePart,
                isDefault: part?.IsDefault ?? false,
                verificationFormat: verificationFormat,
                verificationLength: verificationData?.Length ?? 0);

            return new EntitySnapshot
            {
                Format = format,
                Data = data,
                VerificationFormat = verificationFormat,
                VerificationData = verificationData,
                VerifyAuthored = verifyAuthored
            };
        }

        private static bool EntityMatches(object entity, EntitySnapshot expected)
        {
            if (expected == null) return false;
            var current = CaptureEntity(entity);
            return string.Equals(current.VerificationFormat, expected.VerificationFormat, StringComparison.Ordinal)
                && expected.VerificationData.SequenceEqual(current.VerificationData);
        }

        private static void RestoreEntity(object entity, EntitySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.VerificationFormat == "source"
                && entity is Artech.Architecture.Common.Objects.ISource sourcePart)
            {
                // SerializeData is a native binary envelope, but restoring that envelope
                // directly on a duplicated lazy source part can leave a KB that only reads
                // correctly in the current worker. Use the SDK's normal source setter so
                // the persisted representation is rebuilt through the same path as edits.
                sourcePart.Source = Encoding.UTF8.GetString(snapshot.VerificationData ?? new byte[0]);
                return;
            }
            if (snapshot.Format == "binary")
            {
                DeserializeEntityData(entity, snapshot.Data);
                return;
            }
            if (snapshot.Format == "source")
            {
                var source = entity as Artech.Architecture.Common.Objects.ISource;
                if (source == null) throw new InvalidOperationException("Snapshot expects an ISource part.");
                source.Source = Encoding.UTF8.GetString(snapshot.Data ?? new byte[0]);
                return;
            }
            if (snapshot.Format == "xml")
            {
                var part = entity as KBObjectPart;
                if (part == null) throw new InvalidOperationException("Snapshot expects a KBObjectPart.");
                part.DeserializeFromXml(Encoding.UTF8.GetString(snapshot.Data ?? new byte[0]));
                return;
            }
            if (snapshot.Format == "xml-readonly") return; // global Attributes are verified, never restored here
            throw new InvalidOperationException("Unknown snapshot format '" + snapshot.Format + "'.");
        }

        private static void DeserializeEntityData(object entity, byte[] data)
        {
            var method = FindEntityMethod(entity.GetType(), "DeserializeData", new[] { typeof(byte[]) });
            if (method == null) throw new MissingMethodException(entity.GetType().FullName, "DeserializeData(byte[])");
            try { method.Invoke(entity, new object[] { data }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static MethodInfo FindEntityMethod(Type type, string name, Type[] parameterTypes)
        {
            while (type != null)
            {
                var method = type.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null, types: parameterTypes, modifiers: null);
                if (method != null) return method;
                type = type.BaseType;
            }
            return null;
        }

        private static JObject BuildMoveResult(string targetName, string moduleName, ResolvedLevel level,
            string attribute, int oldPosition, int newPosition, string before, string after, int? position, bool dryRun)
        {
            var result = new JObject
            {
                ["status"] = "ok",
                ["dryRun"] = dryRun,
                ["target"] = targetName,
                ["module"] = moduleName,
                ["level"] = level.DisplayName,
                ["levelPath"] = new JArray(level.Path ?? new List<string>()),
                ["attribute"] = attribute,
                ["oldPosition"] = oldPosition,
                ["newPosition"] = newPosition,
                ["affectedAttributes"] = new JArray(attribute),
                ["specifyExecuted"] = false,
                ["generateExecuted"] = false,
                ["buildExecuted"] = false,
                ["reorganizationExecuted"] = false,
                ["patternExecuted"] = false
            };
            if (!string.IsNullOrWhiteSpace(before)) result["before"] = before;
            if (!string.IsNullOrWhiteSpace(after)) result["after"] = after;
            if (position.HasValue) result["position"] = position.Value;
            return result;
        }

        // Compare the complete top-level name set, including removals. A visual structure
        // payload is replacement-shaped: names omitted from children are removed by
        // SyncVisualLevel, so checking only requested additions would report a false
        // success when the SDK silently kept an old item.
        internal static JObject CompareStructureNames(
            IEnumerable<string> requestedNames,
            IEnumerable<string> persistedNames)
        {
            var requested = (requestedNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var persisted = (persistedNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new JObject
            {
                ["missing"] = new JArray(requested.Where(r =>
                    !persisted.Any(p => string.Equals(p, r, StringComparison.OrdinalIgnoreCase)))),
                ["unexpected"] = new JArray(persisted.Where(p =>
                    !requested.Any(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase))))
            };
        }

        // issue #59 — post-save re-read of a Transaction's top-level structure names. Returns
        // a StructureUpdateNotPersisted envelope when the complete requested set differs from
        // the persisted structure, or null when the write is confirmed (or unverifiable).
        private string VerifyStructurePersisted(string targetName,
            System.Collections.Generic.List<string> requestedNames,
            System.Collections.Generic.List<string> beforeNames,
            Transaction original)
        {
            if (requestedNames == null) return null;
            try
            {
                var fresh = _objectService.FindObject(targetName) as Transaction;
                if (fresh == null) return null; // unverifiable

                // Circularity guard: same in-memory instance back from FindObject means the
                // re-read would trivially mirror the mutated structure — treat as unverifiable.
                if (original != null && object.ReferenceEquals(fresh, original)) return null;
                var persistedNames = _visualStructureService.SerializeVisualLevel(fresh.Structure.Root)
                    .Select(c => c["name"]?.ToString() ?? string.Empty)
                    .Where(n => n.Length > 0)
                    .ToList();

                var diff = CompareStructureNames(requestedNames, persistedNames);
                var missing = diff["missing"] as JArray ?? new JArray();
                var unexpected = diff["unexpected"] as JArray ?? new JArray();
                if (missing.Count == 0 && unexpected.Count == 0) return null;

                return Models.McpResponse.Err(
                    code: "StructureUpdateNotPersisted",
                    message: $"The SDK saved the Transaction but the re-read did not confirm the requested top-level structure set ({missing.Count} missing, {unexpected.Count} unexpected).",
                    hint: "On this GeneXus build the structure write may not have fully survived. Re-read with genexus_structure action=get_visual and fix the missing items in the GeneXus IDE structure editor if they recur.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_structure",
                        args: new JObject { ["action"] = "get_visual", ["name"] = targetName },
                        why: "Shows the persisted structure so you can see exactly which items landed.")),
                    target: targetName,
                    extra: new JObject
                    {
                        ["missing"] = missing,
                        ["unexpected"] = unexpected,
                        ["requested"] = new JArray(requestedNames),
                        ["persisted"] = new JArray(persistedNames),
                        ["before"] = new JArray(beforeNames ?? new System.Collections.Generic.List<string>()),
                        ["saved"] = false
                    });
            }
            catch (Exception ex)
            {
                Logger.Debug("[STRUCTURE-VERIFY] " + ex.Message);
                return null;
            }
        }

        public string GetVisualStructure(string targetName, string typeFilter = null)
        {
            try {
                Logger.Info($"[StructureService] Loading visual structure for: {targetName} (typeFilter={typeFilter})");
                var obj = _objectService.FindObject(targetName, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());
                
                Logger.Info($"[StructureService] Found object: {obj.Name} ({obj.TypeDescriptor.Name})");

                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    return _sdtService.GetSDTStructure(targetName);
                }

                var result = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name, ["description"] = obj.Description };
                if (obj is Transaction trn) {
                    Logger.Info($"[StructureService] Serializing Transaction Level: {trn.Name}");
                    result["children"] = _visualStructureService.SerializeVisualLevel(trn.Structure.Root);
                }
                else if (obj is Artech.Genexus.Common.Objects.Group grp) {
                    Logger.Info($"[StructureService] Serializing SubType Group: {grp.Name}");
                    return _groupStructureService.GetGroupStructure(targetName);
                }
                else if (obj is Table tbl) {
                    Logger.Info($"[StructureService] Serializing Table Structure: {tbl.Name}");
                    result["children"] = SerializeTableStructure(tbl);
                }
                else {
                    string resolvedType = obj.TypeDescriptor.Name;
                    Logger.Error($"[StructureService] Invalid object type for visual structure: {resolvedType}");
                    return Models.McpResponse.Err(
                        code: "UnsupportedObjectType",
                        message: $"Visual structure is available only for Transaction, Table, Group, or SDT objects. Target '{targetName}' resolved to '{resolvedType}'.",
                        hint: $"Specify type=\"Transaction\" or use name=\"Transaction:{targetName}\" to disambiguate homonyms, or use genexus_analyze to inspect this {resolvedType}.",
                        target: targetName,
                        nextSteps: new Newtonsoft.Json.Linq.JArray(
                            Models.McpResponse.NextStep(
                                tool: "genexus_structure",
                                args: new Newtonsoft.Json.Linq.JObject { ["name"] = targetName, ["type"] = "Transaction" },
                                why: "Disambiguate to the Transaction of the same name if one exists."),
                            Models.McpResponse.NextStep(
                                tool: "genexus_analyze",
                                args: new Newtonsoft.Json.Linq.JObject { ["name"] = targetName },
                                why: "Returns a summary of the object including its type.")));
                }

                result["_meta"] = new JObject
                {
                    ["suggested_next"] = new JObject
                    {
                        ["tool"] = "genexus_read",
                        ["args"] = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name }
                    }
                };

                Logger.Info($"[StructureService] Successfully serialized structure for {obj.Name}");
                return Models.McpResponse.Ok(target: targetName, code: "StructureRead", result: result);
            } catch (Exception ex) {
                Logger.Error($"[StructureService] Error loading visual structure: {ex.Message}\n{ex.StackTrace}");
                return Models.McpResponse.Err(
                    code: "StructureReadFailed",
                    message: ex.Message,
                    hint: "Ensure the target is a Transaction, Table, or SDT.",
                    target: targetName);
            }
        }

        private static JArray CompareRequestedStructure(JArray requested, JArray persisted, string path)
        {
            var diff = new JArray();
            foreach (JObject wanted in (requested ?? new JArray()).OfType<JObject>())
            {
                string name = wanted["name"]?.ToString();
                JObject actual = (persisted ?? new JArray()).OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
                string itemPath = path + "/" + (name ?? "?");
                if (actual == null)
                {
                    diff.Add(new JObject { ["path"] = itemPath, ["requested"] = wanted.DeepClone(), ["persisted"] = JValue.CreateNull() });
                    continue;
                }
                foreach (string property in new[] { "nullable", "type", "description", "formula", "isKey", "basedOnDomain", "basedOnAttribute", "isCollection", "isLevel", "referencedType", "showInDefaultForms" })
                    if (wanted[property] != null && !string.Equals(wanted[property].ToString().Trim(), actual[property]?.ToString().Trim(), StringComparison.OrdinalIgnoreCase))
                        diff.Add(new JObject { ["path"] = itemPath + "/" + property, ["requested"] = wanted[property].DeepClone(), ["persisted"] = actual[property]?.DeepClone() ?? JValue.CreateNull() });
                if (wanted["children"] is JArray requestedChildren)
                    foreach (JToken item in CompareRequestedStructure(requestedChildren, actual["children"] as JArray, itemPath + "/children")) diff.Add(item);
            }
            return diff;
        }

        public string GetVisualIndexes(string targetName) => _indexService.GetVisualIndexes(targetName);

        public string CreateIndex(string targetName, string payload, JObject args) =>
            _indexService.CreateIndex(targetName, payload, args);

        public string DropIndex(string targetName, string payload) => _indexService.DropIndex(targetName, payload);

        public string UpdateGroupStructure(string groupName, string payload) => _groupStructureService.UpdateGroupStructure(groupName, payload);

        public string SetAttributeProperties(string attrName, string payload) => _attributeWriteService.SetAttributeProperties(attrName, payload);

        public string SetDomainProperties(string domainName, string payload) => _domainWriteService.SetDomainProperties(domainName, payload);

        // issue #39 follow-up: set a Transaction level's Description / Image attribute — level
        // properties the structure DSL doesn't express. payload = { level?, descriptionAttribute?, imageAttribute? }.
        // level omitted → the root (first) level.
        public string SetLevelProperties(string targetName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is Transaction trn)) return Models.McpResponse.Err(
                    code: "NotATransaction",
                    message: "Level properties apply only to Transactions.",
                    target: targetName);
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload",
                    message: "payload is required.",
                    hint: "e.g. { \"descriptionAttribute\": \"CustomerName\" } or { \"imageAttribute\": \"CustomerPhoto\" }.",
                    target: targetName);

                var json = JObject.Parse(payload);
                string levelName = json["level"]?.ToString();

                TransactionLevel level = trn.Structure.Root;
                if (!string.IsNullOrWhiteSpace(levelName))
                {
                    level = FindLevel(trn.Structure.Root, levelName);
                    if (level == null) return Models.McpResponse.Err(
                        code: "LevelNotFound",
                        message: $"Level '{levelName}' not found in transaction '{trn.Name}'.",
                        hint: "Omit 'level' to target the root level, or pass an existing sub-level name.",
                        target: targetName);
                }

                TransactionAttribute FindLevelAttr(string an) =>
                    level.Attributes.FirstOrDefault(a => string.Equals(a.Name, an, StringComparison.OrdinalIgnoreCase));

                var applied = new JArray();
                using (var sdkTrans = trn.Model.KB.BeginTransaction())
                {
                    try
                    {
                        if (json["descriptionAttribute"] != null)
                        {
                            string an = json["descriptionAttribute"].ToString();
                            var ta = FindLevelAttr(an);
                            if (ta == null) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "AttributeNotInLevel",
                                message: $"Attribute '{an}' is not part of level '{level.Name}'.",
                                hint: "The description attribute must be one of the level's own attributes.",
                                target: targetName); }
                            level.IsDescriptionAttributeDefault = false;
                            level.DescriptionAttribute = ta;
                            applied.Add("descriptionAttribute");
                        }
                        if (json["imageAttribute"] != null)
                        {
                            string an = json["imageAttribute"].ToString();
                            var ta = FindLevelAttr(an);
                            if (ta == null) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "AttributeNotInLevel",
                                message: $"Attribute '{an}' is not part of level '{level.Name}'.",
                                target: targetName); }
                            level.IsImageAttributeDefault = false;
                            level.ImageAttribute = ta;
                            applied.Add("imageAttribute");
                        }

                        if (applied.Count == 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                            code: "NoPropertiesToApply",
                            message: "payload contained no recognized level properties.",
                            hint: "Recognized: descriptionAttribute, imageAttribute (optionally scoped by level).",
                            target: targetName); }

                        trn.EnsureSave();
                        sdkTrans.Commit();
                        WriteService.NotePerTargetWrite(targetName);
                        return Models.McpResponse.Ok(
                            target: targetName,
                            code: "LevelUpdated",
                            result: new JObject { ["level"] = level.Name, ["applied"] = applied });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "LevelUpdateFailed",
                            message: ex.Message,
                            hint: "Check the worker log for the SDK stack trace.",
                            target: targetName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LevelUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the transaction exists and payload is valid JSON.",
                    target: targetName);
            }
        }

        private static TransactionLevel FindLevel(TransactionLevel root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase)) return root;
            foreach (var child in root.Levels)
            {
                var found = FindLevel(child, name);
                if (found != null) return found;
            }
            return null;
        }

        public string GetLogicStructure(string targetName)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                var result = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name };
                var subs = new JArray();
                var events = new JArray();

                // 1. Get Source Part (Subs usually here)
                try {
                    var sourcePart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.SourcePart>();
                    if (sourcePart != null) ExtractLogicItems(sourcePart.Source, subs, events);
                } catch { }

                // 2. Get Events Part (Events usually here)
                try {
                    var eventsPart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.EventsPart>();
                    if (eventsPart != null) ExtractLogicItems(eventsPart.Source, subs, events);
                } catch { }

                result["subs"] = subs;
                result["events"] = events;
                result["_meta"] = new JObject
                {
                    ["suggested_next"] = new JObject
                    {
                        ["tool"] = "genexus_read",
                        ["args"] = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name }
                    }
                };
                return Models.McpResponse.Ok(target: targetName, code: "LogicStructureRead", result: result);
            }
            catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "LogicStructureReadFailed",
                    message: ex.Message,
                    hint: "Ensure the target object exists and has a Source or Events part.",
                    target: targetName);
            }
        }

        private void ExtractLogicItems(string source, JArray subs, JArray events)
        {
            if (string.IsNullOrEmpty(source)) return;

            var existingSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in subs)
            {
                var str = s?.ToString();
                if (!string.IsNullOrEmpty(str)) existingSubs.Add(str);
            }

            var subMatches = SubRegex.Matches(source);
            for (int i = 0; i < subMatches.Count; i++)
            {
                string name = subMatches[i].Groups[1].Value;
                if (existingSubs.Add(name))
                {
                    subs.Add(name);
                }
            }

            var existingEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in events)
            {
                var str = e?.ToString();
                if (!string.IsNullOrEmpty(str)) existingEvents.Add(str);
            }

            var eventMatches = EventRegex.Matches(source);
            for (int i = 0; i < eventMatches.Count; i++)
            {
                string name = eventMatches[i].Groups[1].Value;
                if (existingEvents.Add(name))
                {
                    events.Add(name);
                }
            }
        }

        private JArray SerializeTableStructure(Table tbl)
        {
            var children = new JArray();
            dynamic dStructure = ((dynamic)tbl).TableStructure;
            if (dStructure != null && dStructure.Attributes != null) {
                foreach (dynamic attr in dStructure.Attributes) children.Add(VisualStructureMapper.MapAttribute(attr));
            }
            return children;
        }
    }
}
