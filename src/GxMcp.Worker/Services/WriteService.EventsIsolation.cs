using System;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public partial class WriteService
    {
        internal JObject InspectEventsIsolation(string target, string typeFilter)
        {
            using (SdkGate.Enter())
            {
                try { return EventsSaveIsolation.Preflight(_objectService.FindObject(target, typeFilter)); }
                catch (Exception ex) { return new JObject { ["verified"] = false, ["blocker"] = "ObjectSaveIsolationUnverified", ["reason"] = ex.Message }; }
            }
        }

        internal string WriteIsolatedEvents(string target, string source, string typeFilter, string baseVersion)
        {
            using (SdkGate.Enter())
            {
                var receipt = new JObject();
                KBObject verifiedState;
                Guid guid;
                string response;
                try
                {
                    response = WriteIsolatedEventsCore(target, source, typeFilter, baseVersion, receipt, out verifiedState, out guid);
                }
                finally
                {
                    // Rollback and interrupted Save also leave cached SDK-derived
                    // reads stale. Invalidate every alias without touching the SDK.
                    if (receipt["mutationAttempted"]?.Value<bool>() == true)
                        InvalidateIsolatedEventsReadCaches();
                }

                if (receipt["mutationAttempted"]?.Value<bool>() != true) return response;
                var envelope = JObject.Parse(response);
                var body = envelope["result"] as JObject ?? envelope;
                body["readCachesInvalidated"] = true;
                bool dirty = ShouldMarkIsolatedEventsDirty(receipt);
                if (dirty) NotePerTargetWrite(target);
                body["targetMarkedDirty"] = dirty;
                try
                {
                    var index = _objectService.GetKbService()?.GetIndexCache();
                    // Drop the old entry first. Never refresh from the pre-save seed
                    // or from a speculative in-transaction instance.
                    if (index?.TryGetLoadedIndex() != null) index.RemoveEntryByGuid(guid.ToString());
                    if (verifiedState != null && !SdkEventSuppressionScope.IsPoisoned)
                    {
                        index?.UpdateEntry(verifiedState);
                        body["indexRefresh"] = index == null ? "unavailable" : "fresh-sdk-state";
                    }
                    else body["indexRefresh"] = index?.TryGetLoadedIndex() == null ? "not-loaded" : "entry-invalidated";
                }
                catch (Exception ex)
                {
                    // A cache refresh must not hide a committed/partial save receipt.
                    body["indexRefreshError"] = ex.Message;
                }
                return envelope.ToString();
            }
        }

        internal static void InvalidateIsolatedEventsReadCaches()
        {
            PatchService.InvalidateAllSourceCaches();
            ObjectReader.InvalidateAll();
            ObjectService.InvalidateAllReadCaches();
            ListService.InvalidateCache();
            SummarizeService.InvalidateCache();
        }

        // Mirrors WriteObject's outcome-based dirty tracking: refusal, verified
        // rollback and a verified content no-op stay clean; uncertain persistence
        // remains dirty. This path returns a canonical envelope, not WriteApplied.
        internal static bool ShouldMarkIsolatedEventsDirty(JObject receipt)
        {
            if (receipt?["writeAttempted"]?.Value<bool>() != true
                || receipt["rollbackVerified"]?.Value<bool>() == true) return false;
            bool verifiedNoContentChange = receipt["verificationCompleted"]?.Value<bool>() == true
                && receipt["objectSaved"]?.Value<bool>() == true
                && receipt["partPersisted"]?.Value<bool>() == true
                && receipt["otherPartsIntact"]?.Value<bool>() == true
                && receipt["otherObjectMetadataIntact"]?.Value<bool>() == true
                && receipt["sourceChanged"]?.Value<bool?>() == false
                && receipt["persistenceUncertain"]?.Value<bool>() != true;
            return !verifiedNoContentChange;
        }

        private string WriteIsolatedEventsCore(string target, string source, string typeFilter, string baseVersion,
            JObject receipt, out KBObject verifiedState, out Guid guid)
        {
            verifiedState = null;
            guid = Guid.Empty;
            using (SdkGate.Enter())
            {
                KBObject seed = _objectService.FindObject(target, typeFilter);
                JObject isolation;
                try { isolation = EventsSaveIsolation.Preflight(seed); }
                catch (Exception ex)
                {
                    return McpResponse.Err(code: "ObjectSaveIsolationUnverified", target: target,
                        message: ex.Message, extra: new JObject { ["writeAttempted"] = false, ["partPersisted"] = false, ["objectSaved"] = false });
                }
                var kb = seed.KB;
                guid = seed.Guid;
                receipt.Merge(new JObject
                {
                    ["requireObjectSave"] = true, ["persistencePath"] = "sdk_object_force_save_isolated",
                    ["isolation"] = isolation, ["writeAttempted"] = false,
                    ["objectSaveInvoked"] = false, ["objectSaved"] = false, ["partPersisted"] = false,
                    ["metadataUpdated"] = false, ["metadataStampPersisted"] = false,
                    ["implicitOperations"] = new JArray(), ["retrySafe"] = false
                });
                bool committed = false;
                try
                {
                // The audited U16 callback chain must run for a complete object
                // save; suppressing it prevents the Events source from being
                // materialized by the SDK. Preflight fingerprints every loaded
                // callback assembly, while the pattern scope disables only the
                // known WWP reapplication path.
                using (KbWatcherService.AcquireWriteGate())
                {
                    var original = EventsSaveIsolation.Fresh(kb, guid);
                    string originalSource = EventsSaveIsolation.Source(original);
                    receipt["sourceChanged"] = !string.Equals(originalSource, source, StringComparison.Ordinal);
                    string originalToken = EventsSaveIsolation.Token(original);
                    string originalPlacement = EventsSaveIsolation.Placement(original);
                    var revisionBefore = original.VersionId;
                    var lastUpdateBefore = original.LastUpdate;
                    if (string.IsNullOrWhiteSpace(baseVersion) || originalToken != baseVersion)
                        return McpResponse.Err(code: "VersionConflict", target: target,
                            message: "The Events version changed; no save was attempted.",
                            extra: new JObject { ["currentVersion"] = originalToken, ["writeAttempted"] = false });
                    var snapshot = ObjectMoveSnapshot.Capture(original);
                    var inventory = EventsSaveIsolation.Inventory(kb);
                    receipt["inventoryCount"] = inventory.Count;
                    receipt["inventoryComplete"] = true;
                    receipt["revisionBefore"] = revisionBefore;
                    receipt["lastUpdateBefore"] = lastUpdateBefore.ToUniversalTime().ToString("o");
                    string failure = null;
                    using (var transaction = kb.BeginTransaction())
                    {
                        try
                        {
                            var current = EventsSaveIsolation.Fresh(kb, guid);
                            if (EventsSaveIsolation.Token(current) != originalToken)
                            {
                                transaction.Rollback();
                                return McpResponse.Err(code: "VersionConflict", target: target,
                                    message: "The Events version changed at the transaction boundary; no save was attempted.",
                                    extra: new JObject { ["currentVersion"] = EventsSaveIsolation.Token(current), ["writeAttempted"] = false });
                            }
                            EventsSaveIsolation.CheckTarget(current);
                            receipt["mutationAttempted"] = true;
                            var eventsPart = GxMcp.Worker.Structure.PartAccessor.GetPart(current, "Events");
                            if (!(eventsPart is ISource eventsSource)) throw new InvalidOperationException("The audited WebPanel does not expose an Events source part.");
                            eventsSource.Source = source;
                            receipt["sourceAfterSetLength"] = (eventsSource.Source ?? string.Empty).Length;
                            receipt["sourceAfterSetMatch"] = EventsSaveIsolation.SourceEquivalent(eventsSource.Source, source);
                            receipt["writeAttempted"] = true;
                            receipt["objectSaveInvoked"] = true;
                            // Timestamp is independent of dirty classification, as in
                            // WriteObject. It records the attempt even after rollback.
                            if (!string.IsNullOrWhiteSpace(target)) _lastWriteAtUtc[target] = DateTime.UtcNow;
                            PatternSaveIsolationScope patternScope = null;
                            try
                            {
                                patternScope = new PatternSaveIsolationScope(current);
                                receipt["patternSaveIsolation"] = new JObject
                                {
                                    ["skipApplyPatternSet"] = true,
                                    ["wasAlreadySet"] = patternScope.WasAlreadySet
                                };
                                // Persist the authored Events part first, then invoke
                                // the complete object Save in the same transaction.
                                // U16 can refresh the in-memory Events projection while
                                // the object save runs, so retain the part-save state
                                // for the pre-commit assertion and prove persistence
                                // with the fresh read after commit.
                                eventsPart.Save();
                                string sourceAfterPartSave = EventsSaveIsolation.Source(current);
                                receipt["eventsPartSaveInvoked"] = true;
                                receipt["sourceAfterPartSaveLength"] = sourceAfterPartSave.Length;
                                receipt["sourceAfterPartSaveMatch"] = EventsSaveIsolation.SourceEquivalent(sourceAfterPartSave, source);
                                current.Save(new KBObjectSavePreferences
                                {
                                    ForceSave = true, ForceSaveDefaultParts = false,
                                    SkipValidation = true, UpdateParentModels = false
                                });
                                string sourceAfterObjectSave = EventsSaveIsolation.Source(current);
                                receipt["preCommitPartSourceMatch"] = EventsSaveIsolation.SourceEquivalent(sourceAfterPartSave, source);
                                receipt["preCommitObjectSourceMatch"] = EventsSaveIsolation.SourceEquivalent(sourceAfterObjectSave, source);
                            }
                            finally
                            {
                                if (patternScope != null)
                                {
                                    patternScope.Dispose();
                                    ((JObject)receipt["patternSaveIsolation"])["skipApplyPatternRestored"] = patternScope.Restored;
                                }
                            }
                            receipt["objectSaveReturned"] = true;
                            // U16 can expose a new cache instance with an empty
                            // Events projection until the surrounding SDK
                            // transaction commits. Verify the object that just
                            // completed Save before commit; the mandatory fresh
                            // read after transaction disposal is the persistence
                            // proof below.
                            var comparison = snapshot.Compare(current, "Events");
                            var changedOthers = EventsSaveIsolation.ChangedOthers(inventory, EventsSaveIsolation.Inventory(kb), guid);
                            receipt["preCommitObjectStateVerified"] = true;
                            receipt["preCommitFreshReadDeferred"] = true;
                            receipt["preCommitSourceMatch"] = receipt["preCommitObjectSourceMatch"]?.Value<bool>() == true;
                            receipt["preCommitSourceLength"] = EventsSaveIsolation.Source(current).Length;
                            receipt["requestedSourceLength"] = source.Length;
                            receipt["preCommitSourceHash"] = EventsSaveIsolation.ContentHash(EventsSaveIsolation.Source(current));
                            receipt["requestedSourceHash"] = EventsSaveIsolation.ContentHash(source);
                            receipt["preCommitOtherPartsIntact"] = comparison.Equal;
                            bool preCommitPatternProjectionAllowed = isolation["patternPart"]?.Value<bool>() == true
                                && EventsSaveIsolation.IsExpectedPatternProjection(comparison);
                            receipt["preCommitPatternProjectionAllowed"] = preCommitPatternProjectionAllowed;
                            receipt["preCommitPlacementMatch"] = EventsSaveIsolation.Placement(current) == originalPlacement;
                            receipt["preCommitChangedObjectProperties"] = comparison.ChangedParts;
                            receipt["preCommitChangedPartKeys"] = new JArray(comparison.ChangedPartKeys);
                            receipt["preCommitChangedPartDeltas"] = snapshot.DescribeDifferences(current, "Events");
                            receipt["unexpectedChangedParts"] = comparison.ChangedParts;
                            receipt["unexpectedChangedObjects"] = changedOthers;
                            if (receipt["preCommitSourceMatch"]?.Value<bool>() != true
                                || (!comparison.Equal && !preCommitPatternProjectionAllowed)
                                || changedOthers.Count != 0
                                || EventsSaveIsolation.Placement(current) != originalPlacement)
                                throw new InvalidOperationException("The pre-commit SDK object state did not match the requested isolated change.");
                            transaction.Commit();
                            committed = true;
                        }
                        catch (Exception ex)
                        {
                            failure = ex.Message;
                            try { transaction.Rollback(); receipt["transactionRolledBack"] = true; }
                            catch (Exception rollbackEx) { receipt["rollbackError"] = rollbackEx.Message; }
                        }
                    }
                    // Mandatory fresh verification occurs after transaction disposal,
                    // while broker suppression remains in force. No compensating Save.
                    var persisted = EventsSaveIsolation.Fresh(kb, guid);
                    int postCommitReadAttempts = 1;
                    if (!EventsSaveIsolation.SourceEquivalent(EventsSaveIsolation.Source(persisted), source))
                    {
                        // The first U16 cache refresh can race the transaction's
                        // post-commit projection. A second fresh SDK instance is
                        // a read-only reconciliation, never a second write.
                        persisted = EventsSaveIsolation.Fresh(kb, guid);
                        postCommitReadAttempts++;
                    }
                    var finalComparison = snapshot.Compare(persisted, "Events");
                    var finalOthers = EventsSaveIsolation.ChangedOthers(inventory, EventsSaveIsolation.Inventory(kb), guid);
                    bool partPersisted = EventsSaveIsolation.SourceEquivalent(EventsSaveIsolation.Source(persisted), source);
                    bool metadataAdvanced = persisted.VersionId > revisionBefore || persisted.LastUpdate > lastUpdateBefore;
                    bool otherPartsIntact = finalComparison.Equal && EventsSaveIsolation.Placement(persisted) == originalPlacement;
                    receipt["partPersisted"] = partPersisted;
                    receipt["objectSaved"] = committed;
                    receipt["metadataUpdated"] = committed && metadataAdvanced;
                    receipt["metadataEvidence"] = "fresh-sdk-read-after-object-save";
                    receipt["postCommitReadAttempts"] = postCommitReadAttempts;
                    receipt["otherPartsIntact"] = otherPartsIntact;
                    receipt["unexpectedChangedParts"] = finalComparison.ChangedParts;
                    receipt["unexpectedChangedObjects"] = finalOthers;
                    receipt["otherObjectMetadataIntact"] = finalOthers.Count == 0;
                    receipt["revisionAfter"] = persisted.VersionId;
                    receipt["lastUpdateAfter"] = persisted.LastUpdate.ToUniversalTime().ToString("o");
                    receipt["versionToken"] = EventsSaveIsolation.Token(persisted);
                    receipt["reReadConfirmed"] = partPersisted;
                    receipt["persisted"] = partPersisted;
                    receipt["saved"] = committed;
                    receipt["rollbackVerified"] = !committed && EventsSaveIsolation.SourceEquivalent(EventsSaveIsolation.Source(persisted), originalSource)
                        && otherPartsIntact && finalOthers.Count == 0
                        && persisted.VersionId == revisionBefore && persisted.LastUpdate == lastUpdateBefore;
                    receipt["verificationCompleted"] = true;
                    verifiedState = persisted;
                    if (committed && partPersisted && metadataAdvanced && otherPartsIntact && finalOthers.Count == 0)
                        return McpResponse.Ok(code: "Applied", target: target, result: receipt);
                    return McpResponse.Err(code: "ObjectSaveIncomplete", target: target,
                        message: failure ?? "Complete isolated object persistence could not be confirmed. Inspect the returned persistence state; do not retry blindly.",
                        extra: receipt);
                }
                }
                catch (Exception ex)
                {
                    receipt["objectSaved"] = committed;
                    receipt["verificationCompleted"] = false;
                    receipt["persistenceUncertain"] = receipt["writeAttempted"]?.Value<bool>() == true;
                    receipt["partPersisted"] = JValue.CreateNull();
                    receipt["workerRestartRequired"] = SdkEventSuppressionScope.IsPoisoned;
                    return McpResponse.Err(code: "ObjectSaveIncomplete", target: target,
                        message: "SDK persistence or verification was interrupted: " + ex.Message + ". No compensating write was performed.", extra: receipt);
                }
            }
        }
    }
}
