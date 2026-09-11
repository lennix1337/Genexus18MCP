# Restricted U16 Events save candidate

Live KB save-and-restore validation completed on 2026-09-10 against an
authorized operational profile, an editable KB version, and GeneXus 18 U16. It
is implemented in the ordinary `requireObjectSave=true` path; there is no hidden
diagnostic writer or override flag. Other SDK versions and unknown object or
part implementations remain blocked before persistence.

The candidate accepts only the exact SDK `WebPanel` type and inspected standard
parts (Events, WebForm, Variables, Rules, Conditions, Help, Documentation).
Loaded Architecture.Common, Genexus.Common, Udm.Framework and Common.Properties
assembly bytes must match the U16 fingerprints. Both profile destination pins
are mandatory and are checked again immediately before assigning Events.

Preflight checks every directly attached delegate field in the object/part
hierarchy, including native Entity BeforeSave/AfterSave/AfterModified callbacks.
Unknown handler or target assemblies are refused; nothing is detached.

The SDK's public global EventsSuspended switch suppresses event-broker dispatch.
A synthetic topic verifies one callback before suspension, none during it, and
one after resumption in both the global broker and the actual opened KB broker.
The per-KB flag alone is insufficient in the inspected U16 implementation.
Both prior flags are restored in finally. A failed restoration poisons the
Worker; the partial-result response is sent before the existing restart path.

SdkGate blocks parallel SDK activity, and the STA message drain refuses
reentrant work while the suppression scope is active. The complete Events save
deliberately lets the audited handlers execute so the SDK can materialize the
part; pattern application is isolated through the SDK `SkipApplyPattern` flag.
The path uses the Events part save followed by the complete object save, with no
EnsureSave fallback, metadata stamp, import, pattern apply/update or build.

Save uses `KBObjectSavePreferences` with ForceSave=true,
ForceSaveDefaultParts=false, SkipValidation=true and UpdateParentModels=false.
Before commit, the source must exactly match and the other object parts and
properties must match their snapshot; unexpected changes trigger an explicit
transaction rollback. The receipt independently verifies state after disposal.
Fresh reads require the public IKBModelObjectsCacheConfiguration API and a new
instance with the same GUID. The former private static Invalidate reflection
was ineffective because the installed SDK method is an instance member.

Completion requires commit, exact Events persistence, a naturally advanced
revision or lastUpdate, unchanged other parts, and unchanged metadata inventory
for all other objects. Missing evidence returns ObjectSaveIncomplete with the
actual known persistence state. No compensating save or blind retry occurs.

An unchanged patch with `requireObjectSave=true` still reaches the guarded
ForceSave path: existing part-only persistence is not proof of a saved object.
The exact existing Events source is preserved for this no-op, and dryRun still
does not save. Both source-token checks also apply to this case.

After any mutation attempt, including rollback or interrupted verification,
the Worker clears its managed read and patch caches for every identity alias.
It replaces the target's index entry only from the final fresh SDK instance.
Dirty tracking follows the persistence outcome: refusals, verified rollbacks
and verified content no-ops add no new dirty mark; changed or uncertain
persistence remains dirty. This bookkeeping does not schedule a build.

The complete metadata inventory is deliberately expensive and concurrent edits
to other objects can prevent this candidate from committing. Metadata equality
does not prove byte equality for another object's parts if an independent
writer persisted those parts without advancing metadata. The optimistic token
checks also do not establish atomic cross-process compare-and-swap semantics.
Those are explicit limits, not new guarantees.

For an authorized live test, capture the complete Events source/token and the
other-part snapshots, preview a unique comment outside generated blocks, and
prove the preview leaves snapshots and metadata unchanged. Test a stale token
before a real save. After a successful isolated save, read again and restore
only if the fresh token/content still match the test's own state. Restoration
must use the same isolated path and the new token; it must not overwrite a
concurrent edit. The final source must match the original exactly. Normal SDK
revision/history advancement is recorded, never manually reset.

## Live validation completed

The fixed worker was loaded by the selected operational profile after its worker
destination was corrected and the gateway was restarted. The profile was pinned
to its expected KB path and editable version; no automatic update was enabled.
`dryRun=true` reported verified SDK and broker isolation instead of
`ObjectSaveIsolationUnverified`.

Two content-changing writes and two restorations were executed through the
normal asynchronous `genexus_edit` route with `requireObjectSave=true`:

- An existing WWP master, part `Events`: temporary validation comment added and
  removed. Both complete saves reported
  `sdk_object_force_save_isolated`, `writeAttempted=true`,
  `objectSaveInvoked=true`, `objectSaved=true`, `partPersisted=true`,
  `otherPartsIntact=true`, `otherObjectMetadataIntact=true`, empty unexpected
  part/object sets, and `reReadConfirmed=true`.
- A new selector WebComponent, part `Events`: the same add/remove test. The
  complete save passed the same checks and the final fresh read confirmed the
  original source without the temporary marker.

For both objects, the pre-commit SDK projection was limited to the known
Rules/Conditions representation normalization; no unexpected persisted part or
object was detected. The save scope set and restored `SkipApplyPattern`, ran
the audited U16 callbacks, and verified both global and KB event brokers.

The selector is classified by GeneXus as a WebComponent while its U16 SDK
runtime class is the audited `Artech.Genexus.Common.Objects.WebPanel`; therefore
the component's own complete Events save is covered by the same verified
contract. Its WWP host was created by a targeted pattern operation. The parent
master's PatternInstance received one narrow selector web-component entry under
the existing table, and the generated WebForm projection was only
inspected after the targeted pattern operation to verify that projection; no
direct WebForm edit was made.

No Build, Generate, Specify, Reorg, database operation, global pattern
application, or direct generated WebForm edit was performed. The final KB
source was reread after restoration; generated markers and the requested custom
logic remain present.
