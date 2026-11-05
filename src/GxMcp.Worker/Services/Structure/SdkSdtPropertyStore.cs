using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services.Structure
{
    /// <summary>
    /// The SDK half of an SDT member property write. Mirrors the proven SDT structure write: a fresh read for every
    /// observation, the version re-checked inside the SDK transaction, a part-level baseline proving no other part moved,
    /// and a second transaction for the rollback that only runs when the persisted version is this write's own.
    /// </summary>
    internal sealed class SdkSdtPropertyStore : ISdtPropertyStore
    {
        private readonly ObjectService _objectService;
        private readonly KBObject _seed;
        private readonly string _sdtName;
        private readonly IList<string> _itemPath;
        private readonly bool _captureBaseline;
        private ObjectMoveSnapshot _baseline;
        private KBObject _current;
        private string _ownedWriteVersion;
        // The stored properties this write left, read right after its save. The object stamp has one-second resolution, so
        // two saves in the same second share a version token; the rollback also needs the stored state to be exactly this one.
        private JObject _ownedMembers;

        internal SdkSdtPropertyStore(ObjectService objectService, KBObject seed, string sdtName, IList<string> itemPath, bool captureBaseline)
        {
            _objectService = objectService;
            _seed = seed;
            _sdtName = sdtName;
            _itemPath = itemPath;
            _captureBaseline = captureBaseline;
        }

        public SdtPropertyState ReadFresh()
        {
            KBObject fresh;
            try { fresh = _objectService.FindObjectFreshByIdentity(_seed); }
            catch (Exception ex)
            {
                throw new SdtUserFacingException("FreshReadUnavailable", "A fresh SDT read could not be confirmed: " + ex.Message);
            }
            if (fresh == null)
                throw new SdtUserFacingException("FreshReadUnavailable", "A fresh SDT read could not be confirmed.");
            if (_captureBaseline && _baseline == null)
            {
                try { _baseline = ObjectMoveSnapshot.Capture(fresh); }
                catch (Exception ex) { throw new SdtUserFacingException("SnapshotFailed", ex.Message); }
            }
            _current = fresh;
            return ReadState(fresh);
        }

        public SdtPropertyState ApplyAndStage(SdtPropertyChange change, string expectedVersion, Func<SdtPropertyState, string> accept)
        {
            KBObject obj = _current;
            if (obj == null) throw new InvalidOperationException("ReadFresh must run before a write.");
            SdtWritePhase phase = SdtWritePhase.NotStarted;
            bool committed = false;
            using (var transaction = obj.Model.KB.BeginTransaction())
            {
                try
                {
                    KBObject locked = obj.Model.Objects.Get(obj.Guid) ?? obj;
                    string lockedVersion = WriteService.ComputeVersionToken(locked);
                    if (!SdtWriteGuards.VersionStillCurrent(expectedVersion, lockedVersion))
                        throw new SdtVersionConflictException(lockedVersion);

                    phase = SdtWritePhase.Mutating;
                    Apply(locked, change);
                    phase = SdtWritePhase.Saving;
                    locked.Save();
                    phase = SdtWritePhase.Saved;
                    _ownedWriteVersion = WriteService.ComputeVersionToken(locked);

                    SdtPropertyState staged = ReadState(locked);
                    _ownedMembers = (JObject)staged.Members.DeepClone();
                    string reason = accept(staged);
                    if (reason != null) throw new SdtStageRejectedException(reason, staged);

                    transaction.Commit();
                    committed = true;
                    phase = SdtWritePhase.Committed;
                    WriteService.NotePerTargetWrite(_sdtName);
                    return staged;
                }
                catch (SdtVersionConflictException)
                {
                    if (!committed) try { transaction.Rollback(); } catch { }
                    throw;
                }
                catch (SdtStageRejectedException)
                {
                    if (!committed) try { transaction.Rollback(); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    if (!committed) try { transaction.Rollback(); } catch { }
                    throw new SdtWriteFailure(phase, ex);
                }
            }
        }

        public bool Restore(SdtPropertyChange inverse, SdtPropertyState expected)
        {
            try
            {
                // The persisted version must be the one this write produced: anything else may be another writer's work.
                if (string.IsNullOrWhiteSpace(_ownedWriteVersion)) return false;
                using (var transaction = _seed.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        KBObject current = _objectService.FindObjectFreshByIdentity(_seed);
                        if (current == null)
                            throw new InvalidOperationException("SDT member rollback refused: the object could not be read again.");
                        string refusal = SdtWriteGuards.RestoreRefusal(
                            _ownedWriteVersion, _ownedMembers, WriteService.ComputeVersionToken(current), CaptureMembersOf(current));
                        if (refusal != null)
                            throw new InvalidOperationException("SDT member rollback refused: " + refusal + ".");
                        Apply(current, inverse);
                        current.Save();
                        SdtPropertyState staged = ReadState(current);
                        if (!SdtItemPropertyEngine.SameStored(expected, staged) || !staged.OtherPartsEqual)
                            throw new InvalidOperationException("The staged SDT member rollback did not restore the prior state.");
                        transaction.Commit();
                        committed = true;
                    }
                    finally
                    {
                        if (!committed) try { transaction.Rollback(); } catch { }
                    }
                }
                SdtPropertyState restored = ReadFresh();
                bool verified = SdtItemPropertyEngine.SameStored(expected, restored) && restored.OtherPartsEqual;
                if (verified)
                {
                    // The undo is a second save: the per-target stamp and the index entry follow the restored object too.
                    try
                    {
                        WriteService.NotePerTargetWrite(_sdtName);
                        NoteVerifiedWrite();
                    }
                    catch (Exception ex) { Logger.Warn("SDT member property cache refresh failed after a verified rollback: " + ex.Message); }
                }
                return verified;
            }
            catch (Exception ex)
            {
                Logger.Error("SDT member property rollback failed: " + ex.Message);
                return false;
            }
        }

        public void NoteVerifiedWrite()
        {
            if (_current != null) _objectService.GetKbService().GetIndexCache().UpdateEntry(_current);
        }

        private void Apply(KBObject target, SdtPropertyChange change)
        {
            KBObjectPart structure = ObjectService.FindSdtStructurePartOf(target);
            if (structure == null) throw new InvalidOperationException("SDT structure part not found.");
            object root = ((dynamic)structure).Root;
            if (root == null) throw new InvalidOperationException("SDT structure root not found.");
            List<string> canonical;
            dynamic member = SdtSdkReader.ResolveMember(root, _itemPath, out canonical);
            dynamic entity = member.ItemEntity;
            if (change.Reset) entity.ResetProperty(change.NativeName);
            else entity.SetPropertyValue(change.NativeName, change.Value);
            GxMcp.Worker.Parsers.SdtDslParser.MarkPartDirty((object)structure, _sdtName);
        }

        private static object OpenRoot(KBObject obj)
        {
            KBObjectPart structure = ObjectService.FindSdtStructurePartOf(obj);
            object root = null;
            if (structure != null)
            {
                try { root = ((dynamic)structure).Root; } catch { }
            }
            if (root == null)
                throw new SdtUserFacingException("StructureUnavailable", "The SDT structure root could not be read.");
            return root;
        }

        private static JObject CaptureMembersOf(KBObject obj)
        {
            return SdtSdkReader.CaptureMembers(OpenRoot(obj));
        }

        private SdtPropertyState ReadState(KBObject obj)
        {
            object root = OpenRoot(obj);
            JObject members = SdtSdkReader.CaptureMembers(root);
            List<string> canonical;
            object member = SdtSdkReader.ResolveMember(root, _itemPath, out canonical);
            bool isLevel = !(bool)((dynamic)member).IsLeafItem;
            IList<SdtPropertyInfo> catalog = SdtSdkReader.BuildCatalog(member, isLevel);
            var state = new SdtPropertyState
            {
                Version = WriteService.ComputeVersionToken(obj),
                Members = members,
                TargetKind = isLevel ? "level" : "item",
                TargetPath = canonical,
                Catalog = catalog,
                Effective = SdtSdkReader.ReadEffective(member, catalog)
            };
            if (_baseline != null) state.OtherPartsEqual = _baseline.CompareParts(obj, "SDTStructure").Equal;
            return state;
        }
    }
}
