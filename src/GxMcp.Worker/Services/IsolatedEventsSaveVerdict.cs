using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>What the post-commit read says about the target object itself; the other objects are judged apart.</summary>
    internal sealed class IsolatedEventsTargetEvidence
    {
        internal bool Committed { get; set; }
        internal bool PartPersisted { get; set; }
        internal bool MetadataAdvanced { get; set; }
        internal bool OtherPartsIntact { get; set; }

        /// <summary>After a rollback: the target is back to its original Events source, revision and stamp.</summary>
        internal bool RestoredToOriginal { get; set; }
    }

    /// <summary>
    /// How the isolated Events save adds its evidence up. It holds no SDK type, so the verdict is testable without a
    /// Knowledge Base: the SDK half reads the target and the inventories, this decides what they prove.
    /// </summary>
    internal static class IsolatedEventsSaveVerdict
    {
        /// <summary>Receipt <c>reasonCode</c> when the target is verified and only other objects could not be cleared.</summary>
        internal const string OtherObjectsChangedCode = "OtherObjectsChangedDuringSave";

        /// <summary>
        /// Records what the pre-commit inventory showed about the other objects, in fields of their own. A refusal at
        /// this stage rolls the transaction back, and when the change belonged to the transaction the rollback undoes
        /// it: the inventory read afterwards is clean, and <see cref="Apply"/> rewrites <c>unexpectedChangedObjects</c>
        /// from it. The <c>preCommit</c> fields are never rewritten, so the refusal keeps naming the object that caused
        /// it; <c>unexpectedChangedObjects</c> is set here too only for a save that ends before the verdict runs.
        /// </summary>
        internal static void RecordPreCommitOthers(JObject receipt, EventsOtherObjectChanges others)
        {
            receipt["unexpectedChangedObjects"] = others.BlockingGuids();
            receipt["preCommitUnexpectedChangedObjects"] = others.BlockingGuids();
            if (others.Blocking.Count == 0) return;
            receipt["preCommitUnexpectedChangedObjectDetails"] = others.BlockingDetails();
            if (others.Blocking.Count > EventsOtherObjectChanges.MaxDescribed)
                receipt["preCommitUnexpectedChangedObjectDetailsTruncated"] = true;
        }

        /// <summary>
        /// Writes the verdict into the receipt and returns whether the isolated save is verified. A change another
        /// process made to another object outside the save window is reported (<c>concurrentChanges</c>) and does not
        /// fail the save; any other change to another object still does, and is then described in full. The fields
        /// written here describe the final inventory only; what the pre-commit inventory showed stays where
        /// <see cref="RecordPreCommitOthers"/> put it.
        /// <paramref name="unconfirmedMessage"/> is the one-line reason when the target is verified and only other
        /// objects could not be cleared, and null otherwise.
        /// </summary>
        internal static bool Apply(
            JObject receipt, IsolatedEventsTargetEvidence target, EventsOtherObjectChanges others, EventsSaveWindow window,
            out string unconfirmedMessage)
        {
            bool othersIntact = others.Blocking.Count == 0;
            bool targetVerified = target.Committed && target.PartPersisted && target.MetadataAdvanced && target.OtherPartsIntact;
            bool verified = targetVerified && othersIntact;

            receipt["unexpectedChangedObjects"] = others.BlockingGuids();
            receipt["otherObjectMetadataIntact"] = othersIntact;
            receipt["saveWindow"] = window.Describe();
            if (!othersIntact)
            {
                receipt["unexpectedChangedObjectDetails"] = others.BlockingDetails();
                if (others.Blocking.Count > EventsOtherObjectChanges.MaxDescribed) receipt["unexpectedChangedObjectDetailsTruncated"] = true;
            }
            if (others.Concurrent.Count > 0)
            {
                receipt["concurrentChanges"] = others.ConcurrentDetails();
                receipt["concurrentChangeCount"] = others.Concurrent.Count;
                if (others.Concurrent.Count > EventsOtherObjectChanges.MaxDescribed) receipt["concurrentChangesTruncated"] = true;
            }
            receipt["writeVerifiedForTarget"] = targetVerified;
            receipt["verified"] = verified;
            receipt["rollbackVerified"] = !target.Committed && target.RestoredToOriginal && target.OtherPartsIntact && othersIntact;

            unconfirmedMessage = null;
            if (targetVerified && !othersIntact)
            {
                receipt["reasonCode"] = OtherObjectsChangedCode;
                receipt["retrySafe"] = false;
                unconfirmedMessage = "The Events source was committed and read back, and the other parts of the object are intact, but "
                    + others.Blocking.Count + " other object(s) changed while the save ran and could not be placed outside its window"
                    + " (see unexpectedChangedObjectDetails). The write is verified for the target only: compare those objects"
                    + " with the IDE before relying on this save, and do not repeat the edit.";
            }
            return verified;
        }
    }
}
