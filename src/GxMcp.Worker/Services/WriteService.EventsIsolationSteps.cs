using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public partial class WriteService
    {
        // The SDK half of the isolated Events save. IsolatedEventsSaveFlow decides
        // the order, the rollback and what is reported; each member here is the
        // verbatim SDK work for one step and records its own receipt evidence.
        private sealed class SdkIsolatedEventsSaveSteps : IIsolatedEventsSaveSteps
        {
            private readonly KnowledgeBase _kb;
            private readonly Guid _guid;
            private readonly string _target;
            private readonly string _source;
            private readonly JObject _isolation;
            private readonly JObject _receipt;

            private KBObject _original;
            private string _originalSource;
            private string _originalPlacement;
            private int _revisionBefore;
            private DateTime _lastUpdateBefore;
            private ObjectMoveSnapshot _snapshot;
            private JObject _timings;
            private Dictionary<Guid, EventsInventoryEntry> _inventory;
            private IDisposable _transaction;
            private Action _commit;
            private Action _rollback;
            private KBObject _current;
            private KBObjectPart _eventsPart;
            private Stopwatch _saveWatch;
            private PatternSaveIsolationScope _patternScope;
            private string _sourceAfterPartSave;
            private KBObject _persisted;
            private int _postCommitReadAttempts;

            // The Worker clock around the writes, and the target's stamp as the open transaction holds it after the
            // object save. Together they say which stamp an object written by this save can carry (EventsSaveWindow).
            private DateTime _transactionStartUtc;
            private DateTime _transactionEndUtc;
            private bool _transactionEnded;
            private DateTime _writeStartUtc;
            private DateTime _objectSavedUtc;
            private long? _stampAfterObjectSave;
            private string _unconfirmedMessage;

            internal SdkIsolatedEventsSaveSteps(
                KnowledgeBase kb, Guid guid, string target, string source, JObject isolation, JObject receipt)
            {
                _kb = kb;
                _guid = guid;
                _target = target;
                _source = source;
                _isolation = isolation;
                _receipt = receipt;
            }

            public string OriginalToken { get; private set; }

            public bool WorkerRestartRequired => SdkEventSuppressionScope.IsPoisoned;

            public bool Verified { get; private set; }

            public string UnconfirmedMessage => _unconfirmedMessage;

            /// <summary>The fresh SDK instance the verification read; null until the verification completed.</summary>
            internal KBObject VerifiedState { get; private set; }

            public void ReadOriginal()
            {
                _original = EventsSaveIsolation.Fresh(_kb, _guid);
                _originalSource = EventsSaveIsolation.Source(_original);
                _receipt["sourceChanged"] = !string.Equals(_originalSource, _source, StringComparison.Ordinal);
                // The preview reports noContentChange; the receipt of an applied no-op says it too. With
                // requireObjectSave the guarded ForceSave still runs for an unchanged source (documented in
                // events-save-u16-candidate.md), so writeAttempted/objectSaveInvoked stay true beside it.
                _receipt["noContentChange"] = !_receipt["sourceChanged"].Value<bool>();
                OriginalToken = EventsSaveIsolation.Token(_original);
                _originalPlacement = EventsSaveIsolation.Placement(_original);
                _revisionBefore = _original.VersionId;
                _lastUpdateBefore = _original.LastUpdate;
            }

            public void CaptureBaseline()
            {
                _snapshot = ObjectMoveSnapshot.Capture(_original);
                _receipt["preservedPartSnapshotBefore"] = _snapshot.DescribeParts("Events");
                _timings = new JObject();
                _receipt["isolationTimings"] = _timings;
                var inventoryWatch = Stopwatch.StartNew();
                _inventory = EventsSaveIsolation.Inventory(_kb);
                _timings["inventoryBeforeMs"] = inventoryWatch.ElapsedMilliseconds;
                _receipt["inventoryCount"] = _inventory.Count;
                _receipt["inventoryComplete"] = true;
                _receipt["revisionBefore"] = _revisionBefore;
                _receipt["lastUpdateBefore"] = _lastUpdateBefore.ToUniversalTime().ToString("o");
            }

            public void BeginTransaction()
            {
                _transactionStartUtc = DateTime.UtcNow;
                var transaction = _kb.BeginTransaction();
                _transaction = transaction;
                _commit = transaction.Commit;
                _rollback = transaction.Rollback;
            }

            public string ReadFreshInTransaction()
            {
                _current = EventsSaveIsolation.Fresh(_kb, _guid);
                return EventsSaveIsolation.Token(_current);
            }

            public void RecheckTarget() => EventsSaveIsolation.CheckTarget(_current);

            public void AssignSource()
            {
                _writeStartUtc = DateTime.UtcNow;
                _receipt["mutationAttempted"] = true;
                _eventsPart = GxMcp.Worker.Structure.PartAccessor.GetPart(_current, "Events");
                if (!(_eventsPart is ISource eventsSource)) throw new InvalidOperationException("The audited WebPanel does not expose an Events source part.");
                eventsSource.Source = _source;
                _receipt["sourceAfterSetLength"] = (eventsSource.Source ?? string.Empty).Length;
                _receipt["sourceAfterSetMatch"] = EventsSaveIsolation.SourceEquivalent(eventsSource.Source, _source);
                _receipt["writeAttempted"] = true;
                _receipt["objectSaveInvoked"] = true;
                // Timestamp is independent of dirty classification, as in
                // WriteObject. It records the attempt even after rollback.
                if (!string.IsNullOrWhiteSpace(_target)) _lastWriteAtUtc[_target] = DateTime.UtcNow;
            }

            public void SavePart()
            {
                _saveWatch = Stopwatch.StartNew();
                _patternScope = new PatternSaveIsolationScope(_current);
                _receipt["patternSaveIsolation"] = new JObject
                {
                    ["skipApplyPatternSet"] = true,
                    ["wasAlreadySet"] = _patternScope.WasAlreadySet
                };
                // Persist the authored Events part first, then invoke
                // the complete object Save in the same transaction.
                // U16 can refresh the in-memory Events projection while
                // the object save runs, so retain the part-save state
                // for the pre-commit assertion and prove persistence
                // with the fresh read after commit.
                _eventsPart.Save();
                _sourceAfterPartSave = EventsSaveIsolation.Source(_current);
                _receipt["eventsPartSaveInvoked"] = true;
                _receipt["sourceAfterPartSaveLength"] = _sourceAfterPartSave.Length;
                _receipt["sourceAfterPartSaveMatch"] = EventsSaveIsolation.SourceEquivalent(_sourceAfterPartSave, _source);
            }

            public void SaveObject()
            {
                _current.Save(new KBObjectSavePreferences
                {
                    ForceSave = true, ForceSaveDefaultParts = false,
                    SkipValidation = true, UpdateParentModels = false
                });
                // The SDK stamps the object while it saves it; this stamp is what ties the SDK's clock to the Worker's.
                _objectSavedUtc = DateTime.UtcNow;
                _stampAfterObjectSave = SdkTimestampNormalizer.NormalizeUtc(_current.LastUpdate).Ticks;
                string sourceAfterObjectSave = EventsSaveIsolation.Source(_current);
                _receipt["preCommitPartSourceMatch"] = EventsSaveIsolation.SourceEquivalent(_sourceAfterPartSave, _source);
                _receipt["preCommitObjectSourceMatch"] = EventsSaveIsolation.SourceEquivalent(sourceAfterObjectSave, _source);
            }

            public void EndSaveIsolation()
            {
                if (_saveWatch != null) _timings["saveMs"] = _saveWatch.ElapsedMilliseconds;
                if (_patternScope != null)
                {
                    _patternScope.Dispose();
                    ((JObject)_receipt["patternSaveIsolation"])["skipApplyPatternRestored"] = _patternScope.Restored;
                }
            }

            public void VerifyPreCommit()
            {
                _receipt["objectSaveReturned"] = true;
                // U16 can expose a new cache instance with an empty
                // Events projection until the surrounding SDK
                // transaction commits. Verify the object that just
                // completed Save before commit; the mandatory fresh
                // read after transaction disposal is the persistence
                // proof below.
                var comparison = _snapshot.Compare(_current, new[] { "Events" });
                var inventoryWatch = Stopwatch.StartNew();
                // The transaction is still open: only a stamp from before it shows that someone else wrote an object.
                var changedOthers = EventsOtherObjectChanges.Classify(
                    _inventory, EventsSaveIsolation.Inventory(_kb), _guid, BuildSaveWindow(transactionClosed: false));
                _timings["inventoryPreCommitMs"] = inventoryWatch.ElapsedMilliseconds;
                _receipt["preCommitObjectStateVerified"] = true;
                _receipt["preCommitFreshReadDeferred"] = true;
                _receipt["preCommitSourceMatch"] = _receipt["preCommitObjectSourceMatch"]?.Value<bool>() == true;
                _receipt["preCommitSourceLength"] = EventsSaveIsolation.Source(_current).Length;
                _receipt["requestedSourceLength"] = _source.Length;
                _receipt["preCommitSourceHash"] = EventsSaveIsolation.ContentHash(EventsSaveIsolation.Source(_current));
                _receipt["requestedSourceHash"] = EventsSaveIsolation.ContentHash(_source);
                _receipt["preCommitOtherPartsIntact"] = comparison.Equal;
                bool preCommitPatternProjectionAllowed = _isolation["patternPart"]?.Value<bool>() == true
                    && EventsSaveIsolation.IsExpectedPatternProjection(comparison);
                _receipt["preCommitPatternProjectionAllowed"] = preCommitPatternProjectionAllowed;
                _receipt["preCommitPlacementMatch"] = EventsSaveIsolation.Placement(_current) == _originalPlacement;
                _receipt["preCommitChangedObjectProperties"] = comparison.ChangedParts;
                _receipt["preCommitChangedPartKeys"] = new JArray(comparison.ChangedPartKeys);
                _receipt["preCommitChangedPartDeltas"] = _snapshot.DescribeDifferences(_current, "Events");
                _receipt["unexpectedChangedParts"] = comparison.ChangedParts;
                // Apart from the final verdict: the rollback this refusal triggers can undo the change it names.
                IsolatedEventsSaveVerdict.RecordPreCommitOthers(_receipt, changedOthers);
                string changedParts = comparison.ChangedParts is JArray changedList
                    ? string.Join(", ", changedList.Select(part => part.ToString()))
                    : comparison.ChangedParts?.ToString();
                string mismatch = IsolatedEventsSaveDiagnostics.DescribePreCommitMismatch(
                    _receipt["preCommitSourceMatch"]?.Value<bool>() == true,
                    changedParts,
                    comparison.Equal || preCommitPatternProjectionAllowed,
                    changedOthers.Blocking.Count,
                    EventsSaveIsolation.Placement(_current) == _originalPlacement);
                if (mismatch != null) throw new InvalidOperationException(mismatch);
            }

            public void Commit() => _commit();

            public void Rollback() => _rollback();

            public void EndTransaction()
            {
                var transaction = _transaction;
                _transaction = null;
                transaction?.Dispose();
                if (transaction != null && !_transactionEnded)
                {
                    _transactionEndUtc = DateTime.UtcNow;
                    _transactionEnded = true;
                }
            }

            // Where, in the SDK's stamp clock, a write made by this save can be stamped. The target's own new stamp,
            // read right after the object save, is the anchor: it was assigned somewhere between the first write call
            // and that read. Without an advanced stamp there is no anchor and no object can be cleared.
            private EventsSaveWindow BuildSaveWindow(bool transactionClosed)
            {
                if (_stampAfterObjectSave == null) return EventsSaveWindow.Unavailable("TargetNotSaved");
                if (_stampAfterObjectSave.Value <= SdkTimestampNormalizer.NormalizeUtc(_lastUpdateBefore).Ticks)
                    return EventsSaveWindow.Unavailable("TargetStampNotAdvanced");
                if (transactionClosed && !_transactionEnded) return EventsSaveWindow.Unavailable("TransactionNotClosed");
                return EventsSaveWindow.Calibrate(
                    _transactionStartUtc.Ticks, transactionClosed ? _transactionEndUtc.Ticks : (long?)null,
                    _writeStartUtc.Ticks, _objectSavedUtc.Ticks, _stampAfterObjectSave.Value,
                    TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).Ticks);
            }

            public void RereadAfterCommit()
            {
                var persisted = EventsSaveIsolation.Fresh(_kb, _guid);
                _postCommitReadAttempts = 1;
                if (!EventsSaveIsolation.SourceEquivalent(EventsSaveIsolation.Source(persisted), _source))
                {
                    // The first U16 cache refresh can race the transaction's
                    // post-commit projection. A second fresh SDK instance is
                    // a read-only reconciliation, never a second write.
                    persisted = EventsSaveIsolation.Fresh(_kb, _guid);
                    _postCommitReadAttempts++;
                }
                _persisted = persisted;
            }

            public void VerifyAfterCommit(bool committed)
            {
                var persisted = _persisted;
                var finalComparison = _snapshot.Compare(persisted, new[] { "Events" });
                _receipt["preservedPartSnapshotAfter"] = ObjectMoveSnapshot.Capture(persisted).DescribeParts("Events");
                var inventoryWatch = Stopwatch.StartNew();
                EventsSaveWindow window = BuildSaveWindow(transactionClosed: true);
                // A committed save must have persisted the stamp the window was calibrated with.
                if (window.Calibrated && committed
                    && SdkTimestampNormalizer.NormalizeUtc(persisted.LastUpdate).Ticks != _stampAfterObjectSave)
                    window = EventsSaveWindow.Unavailable("PersistedStampDiffersFromSaveStamp");
                var finalOthers = EventsOtherObjectChanges.Classify(_inventory, EventsSaveIsolation.Inventory(_kb), _guid, window);
                _timings["inventoryFinalMs"] = inventoryWatch.ElapsedMilliseconds;
                bool partPersisted = EventsSaveIsolation.SourceEquivalent(EventsSaveIsolation.Source(persisted), _source);
                bool metadataAdvanced = persisted.VersionId > _revisionBefore || persisted.LastUpdate > _lastUpdateBefore;
                bool otherPartsIntact = finalComparison.Equal && EventsSaveIsolation.Placement(persisted) == _originalPlacement;
                _receipt["partPersisted"] = partPersisted;
                _receipt["objectSaved"] = committed;
                _receipt["transactionCommitted"] = committed;
                _receipt["idempotent"] = _receipt["sourceChanged"]?.Value<bool>() == false;
                _receipt["metadataUpdated"] = committed && metadataAdvanced;
                _receipt["metadataStampPersisted"] = committed && metadataAdvanced;
                _receipt["metadataEvidence"] = "fresh-sdk-read-after-object-save";
                _receipt["postCommitReadAttempts"] = _postCommitReadAttempts;
                _receipt["otherPartsIntact"] = otherPartsIntact;
                _receipt["unexpectedChangedParts"] = finalComparison.ChangedParts;
                _receipt["revisionAfter"] = persisted.VersionId;
                _receipt["lastUpdateAfter"] = persisted.LastUpdate.ToUniversalTime().ToString("o");
                _receipt["versionToken"] = EventsSaveIsolation.Token(persisted);
                _receipt["persistedHash"] = EventsSaveIsolation.ContentHash(EventsSaveIsolation.Source(persisted));
                _receipt["reReadConfirmed"] = partPersisted;
                _receipt["persisted"] = committed && partPersisted;
                _receipt["saved"] = committed;
                _receipt["verificationCompleted"] = true;
                VerifiedState = persisted;
                Verified = IsolatedEventsSaveVerdict.Apply(
                    _receipt,
                    new IsolatedEventsTargetEvidence
                    {
                        Committed = committed,
                        PartPersisted = partPersisted,
                        MetadataAdvanced = metadataAdvanced,
                        OtherPartsIntact = otherPartsIntact,
                        RestoredToOriginal = EventsSaveIsolation.SourceEquivalent(EventsSaveIsolation.Source(persisted), _originalSource)
                            && persisted.VersionId == _revisionBefore && persisted.LastUpdate == _lastUpdateBefore
                    },
                    finalOthers, window, out _unconfirmedMessage);
            }

            public IEnumerable<KeyValuePair<string, object>> SdkMessageSources()
            {
                var sources = new List<KeyValuePair<string, object>>();
                KBObject instance = _persisted ?? _current ?? _original;
                if (instance == null) return sources;
                foreach (KBObjectPart part in instance.Parts)
                    sources.Add(new KeyValuePair<string, object>(part.TypeDescriptor?.Name ?? part.GetType().Name, part));
                sources.Add(new KeyValuePair<string, object>("Object", instance));
                return sources;
            }

            public void Dispose() => EndTransaction();
        }
    }
}
