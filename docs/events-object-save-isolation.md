# Required Events save isolation

`requireObjectSave=true` uses the restricted U16 candidate described in
[Events save candidate](events-save-u16-candidate.md). Unverified SDK hashes,
object/part types, direct callbacks, destinations, cache removal, or broker
suppression return `ObjectSaveIsolationUnverified` before persistence. Dry runs
include the dynamic preflight result and a `writeBlocker` when ineligible, and
say what they did not do (see [Preview scope](#preview-scope)).
There is no caller-controlled bypass flag. Live candidate validation is pending.

This is separate from the KB/version destination guard. A correct destination
does not establish that save handlers have no automatic effects.

## Routing and failure evidence

`genexus_edit` already forwards the option in this upstream baseline; the
legacy `genexus_patch` now does too. `genexus_write` validates its facade
arguments and forwards supported Events patch requests to the same
`PatchService.ApplyPatch` path. The isolation check runs after dry-run handling
and the pre-write version check, before the full snapshot or SDK write.

The original complete-save receipt remains available for legacy paths.
`ObjectSaveIncomplete` preserves the actual part persistence state when any
required evidence is absent. Its classification is tested through the same
pure receipt function used in production. Revision advancement requires a
numeric increase, or a later lastUpdate, and still requires the upstream
`metadataStampPersisted` evidence. A changed/decreased revision alone is
insufficient.

## Preview scope

A `requireObjectSave` preview checks the textual patch and the isolation
preflight (SDK bundle, pins, callbacks, brokers). It runs no SDK validation of
the Events source and no save, so it cannot say the save will succeed:

| Field | Meaning |
| :--- | :--- |
| `patchMatch` | The patch matched the current source. |
| `isolationEligible` | The preflight accepted the bundle, pins and callbacks (`objectSaveIsolation.verified`). |
| `validationExecuted` | Always `false`: no SDK validation ran. |
| `saveExecuted` | Always `false`: nothing was saved. |
| `persistabilityVerified` | Always `false` for a preview; only a completed, verified save establishes persistence. |
| `semanticValidation` | `{performed: false, reason}`: the SDK's validation of the Events source (unknown functions, syntax) runs only inside the part save, which a preview must not run. |
| `previewScope` | The same statement in words. |
| `canApply` | Whether this exact request could be applied now: the patch matched (or leaves the source unchanged), the isolation audit accepted the destination, bundle and callbacks, `patch.indentation` (when `mode=validate`) would not refuse it, and `baseVersion` was sent. It is the field to read first. |
| `applyBlockers` | Present only when `canApply` is `false`: every independent reason, in the order the checks run, each as `{code, layer, message, remediation, evidence?}`. Fixing the first one does not reveal the next one later. |
| `previewSummary` | One sentence with the verdict, the first blocker, and the statements that nothing was written and the SDK validated nothing. |
| `writeAttempted` | Always `false` for a preview. |

A preview keeps `status: ok` and `code: Applied` for compatibility: the textual
simulation did succeed. That pair is not permission to write; `canApply` is. A
preview carries no `next_legal_actions`, because there is nothing to build,
preview or undo, and a blocked one never suggests an apply that cannot happen.

A clean preview followed by a failed save is expected when the SDK rejects the
source only at save time: it validates the Events source in `eventsPart.Save()`.
The failure then says where and why (see below). Checked on a disposable GeneXus
18 U16 Knowledge Base: an in-memory `KBObject.Validate(OutputMessages)` on a copy
of the object returned `false` for such a source but produced no messages, so it
is not used; and `SkipValidation=true` of the object save does not apply to the
earlier part save.

## Why the save is refused before any write

`ObjectSaveIsolationUnverified` is the envelope; `reasonCode` and `blockers[]`
say which prerequisite failed and who owns the fix. The preflight evaluates every
independent prerequisite before refusing, so one pass reports all of them.

| Code | Layer | Meaning | Who fixes it |
| :--- | :--- | :--- | :--- |
| `WriteDestinationPinsMissing` | pins | Neither `GXMCP_EXPECTED_KB_PATH` nor `GXMCP_EXPECTED_KB_VERSION` reaches the Worker. The isolated save never runs unguarded. | The Gateway owner: set both in the environment that launches the Gateway (for example the `env` block of the MCP client's server entry), after confirming them with the SDK, and restart the Gateway. |
| `WriteDestinationConfigurationIncomplete` | pins | Only one pin is set (`evidence.missing` names the other). | Same. |
| `WriteDestinationKbMismatch` | destination | The opened KB is not the pinned one (the preflight reports identity hashes, not paths). | Correct the pin or open the right KB. |
| `WriteDestinationVersionMismatch` | destination | The KB's active version is not the pinned version. | Restore it with `genexus_kb_version action=set_active`, `targetVersion` equal to the pin and `autoUpdate=false`, or correct the pin. |
| `WriteDestinationVersionNotWritable` | destination | The pinned version is frozen or its writability is unknown. | Pin a writable version. |
| `SdkBundleNotAudited` | sdk-bundle | The loaded SDK/WorkWithPlus bundle is not one of the audited ones (`evidence.loadedBundle` lists each assembly's hash prefix and file version, `evidence.auditedBundles` the audited ids). **Pins do not fix this.** | An audit of that exact bundle (see below). There is no allowlist by version number. |
| `SdkBundleAssemblyMissing`, `SdkBundleAssemblyAmbiguous`, `SdkBundleAuditFailed` | sdk-bundle | A required assembly is not loaded, two implementations of one assembly are loaded, or the complete-bundle audit failed. | Same. |
| `PatternInstancesNotVerified` | sdk-bundle | The bundle is audited, but its native proof covers only Knowledge Bases without pattern instances and this one has some (the message gives the count), or they could not be counted. | Not fixable by configuration: a proof that covers pattern instances is needed first. |
| `UnverifiedObjectPart`, `UnverifiedSdkCallback`, `IsolationTargetTypeUnsupported` | object, callbacks | The object has a part type, a direct callback or a type outside the audited set. | Not fixable by configuration. |
| `IsolationPreflightFailed` | preflight | Any other preflight failure, with its message and exception type. | Read the Worker log for that request (the response's `correlationId`, when it carries one). |

When any pin is set, the destination fence that runs before the preflight refuses
a partial, divergent or unwritable destination first, with the same
`WriteDestination*` codes, `persisted: false` and `writeAttempted: false` (its
envelope reports the expected and actual KB as it always has). The preflight takes
the same decision for what the fence cannot see, such as a version that changes
between the two, and for the case where neither pin is set.

A preview adds more reasons it cannot apply, listed before the isolation ones in
the order the request is judged: `PatchNoMatch`; `IndentationMismatch` or
`IndentationNotComparable`, the refusal `patch.indentation.mode=validate` would
raise on the real write (layer `patch`; the preview reports it and goes on, so
without this entry it would say the request can be applied); and
`BaseVersionRequired` (no `baseVersion`; `expectedVersion` is its alias).

Only the audited bundles are eligible (`evidence.auditedBundles` lists their ids):

| Bundle id | Recognized by | Native proof |
| :--- | :--- | :--- |
| `18.0.16.189550`, `18.0.16.189595` | The hashes of the assemblies listed in `EventsSaveIsolation`, each with the exact WorkWithPlus implementation when that package is loaded. | [Events canonical snapshot](events-canonical-snapshot.md). |
| `18.0.16.189595+wwp16.2.0.8386` | The complete-bundle manifest `config/events-u16-wwp16.2-audit.json` (452 files). | A synthetic WebPanel with a persisted WorkWithPlus host in a test Knowledge Base, 2026-10-14 ([Events canonical snapshot](events-canonical-snapshot.md)). |
| `18.0.11.54344+wwp16.1.2.8167` | The complete-bundle manifest `config/events-u11-wwp16.1-audit.json` (459 files). | A synthetic WebPanel in a Knowledge Base without pattern instances, 2026-10-28; scoped, see [Scope of a native proof](#scope-of-a-native-proof). |

An installation of another update level (for example GeneXus 18 U12), or one with
another WorkWithPlus, is refused with `SdkBundleNotAudited` whatever the pins say,
because the audit is of the whole loaded bundle, not of a version string. Making
another bundle eligible takes the same audit these had: inspection of the installed
assemblies, their save callbacks and pattern handlers, and native proofs in a
disposable Knowledge Base (rollback, unchanged sibling parts and objects, no
implicit operation).

### Complete-bundle manifests

When the WorkWithPlus package is loaded, `EventsBundleAudits` audits the bundle as a
whole. It picks the manifest by the exact bytes of the loaded
`DVelop.Patterns.WorkWithPlus` (nothing else selects one, and a mixture of two
bundles matches none) and checks, in this order:

1. the copy of the manifest distributed beside the Worker is byte-identical to the
   one embedded in the Worker (editing the distributed copy authorizes nothing; a
   missing or unreadable copy is a refusal of its own);
2. every file the manifest lists exists inside the SDK installation (a path that
   escapes it is refused) and has the SHA-256 of the manifest;
3. every loaded `Artech.`, `GeneXus.` or `DVelop.` assembly is listed, with the same
   name and hash; the unknown ones are all named in one refusal, so a single run
   shows the whole gap;
4. the nine critical assemblies match the manifest, which `MatchAuditedBuild` then
   reports as the audited build.

Any failure is `SdkBundleAuditFailed`, with a message that names what did not match.
A manifest records the file list, the nine critical assemblies, the save API and the
APIs the save must not call, what the IL review of the save hooks found
(`runtimeInspection`) and a `nativeVerification` section. While
`nativeVerification.status` is `pending` only its `syntheticObject` may be saved and
every other object is refused as `SdkBundleAuditFailed`; `verified` is recorded
together with the digest of the native proof (`proofEvidenceSha256`).

`scripts/new-events-bundle-audit-manifest.ps1` generates a manifest from an
installation. It only reads and hashes files (it never opens a Knowledge Base or
loads an assembly), and what it generates authorizes nothing until the native proof
is recorded. A Worker hard reload copies the manifests beside the swapped Worker.

### Scope of a native proof

`nativeVerification.scope` states the condition on the Knowledge Base under which a
proof holds. The U16 manifest has none: its proof covers a WebPanel with a persisted
WorkWithPlus host. The U11 manifest declares `kb-without-pattern-instances`: its
proof is for a Knowledge Base with no pattern instance at all (WorkWithPlus or any
other pattern), where the save hooks of the pattern packages have nothing to apply.
The SDK loads the WorkWithPlus package of every installation that has it, whether or
not the Knowledge Base uses it, and that is why the loaded bundle is audited at all.

After the bundle itself verifies, the Worker counts the `PatternInstance` objects of
the whole model (by class and, independently, by type name; never a sample) and
refuses the save as `PatternInstancesNotVerified` when there is any, or when the
count cannot be made. It fails closed, applies while the status is `pending` too,
and a scope the Worker does not know is refused the same way.

The U11 proof saved the Events part of a synthetic WebPanel through the protected
pipeline in a Knowledge Base without pattern instances (the Worker counted zero):
preview without mutation, stale token refused, asynchronous save with a complete
receipt (object saved, part persisted, other parts and the metadata of other objects
intact, no implicit operation, revision advanced by exactly one), fresh reread,
Worker restart, idempotent save, syntax-error rollback, a queue with one winner and
one `VersionConflict`, and the refusal of any other object while the proof was
pending. The synthetic objects were removed and the Knowledge Base ended with the
same object count. The sanitized result (only each check's name and whether it
passed) is [`evidence/events-u11-wwp16.1-native-proof.json`](evidence/events-u11-wwp16.1-native-proof.json);
its SHA-256 is the manifest's `proofEvidenceSha256`. Not covered: a persisted
WorkWithPlus host, a Knowledge Base that has pattern instances (refused, and covered
by unit tests only), other object types and parts, and other update levels.

## Applied versus pending configuration

The pins are process environment variables: the Gateway forwards its own `GXMCP_*`
environment to the Worker, so they are whatever the process that launched the
Gateway declared (for example the `env` block of an MCP client's server entry). An
edit of that entry therefore reaches a Worker only through a Gateway started after
the edit. The isolation report says what the Worker actually has, without reading
the process environment:

- `objectSaveIsolation.effectivePins` (or the same field of the refusal) is what
  the Worker has: `variables` says `absent`, `blank` or `present` per pin, `kbIdentity`
  is a one-way hash of the pinned KB path (the path itself is never reported) and
  `version` is the pinned version name, to compare with `genexus_kb_version action=list`.
- `profileRevision` is a different fact: the revision of the Gateway configuration
  file (`profileRevisionSource: native-config`), not of the entry that declared
  the pins. Adding a pin to that entry does not change it.

A refusal that reports `absent` for a pin the owner has already written means the
Gateway predates the edit: restart it, with no operation in flight, and repeat the
preview.

## Failure diagnostics

A failed isolated save returns `ObjectSaveIncomplete` (or
`ObjectSaveIsolationUnverified` before any write). Besides the persistence
evidence (`objectSaved`, `partPersisted`, `transactionRolledBack`,
`rollbackVerified`, `persistenceUncertain`), the receipt answers where and why:

| Field | Meaning |
| :--- | :--- |
| `savePhase` | The stage the save stopped at. Frozen at the failure; `completed` on success. |
| `verificationPhase` | The stage reached by the verification that follows a rollback, when a failure came first. |
| `exceptionType` | Full type name of the exception. |
| `innerMessages[]`, `innerExceptionTypes[]` | The inner exception chain (at most 5), sanitized. |
| `diagnostics[]` | The error-severity SDK messages a caller fixes: `code` (for example `src0294`), `message`, `object`, `part`, `rawLine`, `column`. The position is the SDK's own, unconverted: `rawLine` was 1-based on the Events text of a plain WebPanel (calls on lines 2, 3 and 4 reported 2, 3 and 4) and the IDE showed a negative value (`-1657`) for a Pattern-generated source, so it is not necessarily a line of the Events text. There is deliberately no normalized `line`. `locatedIn` (and `locatedInObjectId`) is the SDK's own name for the object that owns the position, e.g. `Web Panel 'Name'`; when it differs from `object`, the error is located in something other than the saved object. |
| `sdkMessages[]` | Every SDK message (warnings included) with `code`, `part`, `rawLine`, `column`, `severity`, `message`, read before the rollback. At most 25; `sdkMessagesTruncated` says more existed. |
| `sourceValidationErrors[]` | The error-severity SDK messages on the Events part. |
| `originalError` | The exception's own message, kept when the reported message replaced a bare `Erro`. |
| `sdkMessagesError` | Why the SDK messages could not be read, when they could not. |
| `verificationFailure` | A second failure while verifying after the rollback; it never overwrites the first. |
| `saveFailureMessage` | The save failure's message, when an interruption replaced it as `message`. |

`savePhase` values, in order: `preflight`, `fresh-read`, `version-conflict-check`,
`baseline-capture` (snapshot, whole-KB inventory, transaction start),
`transaction-fresh-read`, `target-recheck`, `source-assignment`, `part-save`
(pattern isolation set-up and `eventsPart.Save()`), `object-save` (the complete
`KBObject.Save` and the isolation teardown), `pre-commit-verification`, `commit`,
`post-commit-reread`, `post-commit-verification`, `completed`.

Where the SDK keeps the messages: the part save throws a
`ValidationException` whose `Message` is the bare `Erro` and whose `Output` holds
the validation errors, one per offending call, with code and position (verified on
a disposable U16 Knowledge Base with three calls to a function that does not exist
on numeric SDT members: three `src0294` diagnostics at lines 2, 3 and 4, column 16,
while `Messages` of the parts and of the object stayed empty). The receipt reads
that `Output` (and `FullMessage` when it cannot be enumerated) as well as the
`Messages` of the object and its parts. Messages that come from the exception of
the part save are attributed to the `Events` part; from any later stage, to the
object.

A failed pre-commit check names what moved instead of a bare mismatch: the Events
source differs from the request, other parts changed (with their names), other
objects changed, or the placement changed. Saving a Work With pattern-generated
WebPanel whose `Rules` is a single newline, for example, is refused there because
the SDK rewrites that part to empty during the object save (`other parts of the
object changed (Rules)`); the transaction is rolled back and nothing persists.

The message names the stage and prefers the SDK's own text over a bare `Erro`
(the same `WritePolicy.PreferDetailedMessage` the normal write path uses), then an
informative inner exception; it lists up to three errors as `code: text (line N,
column M)` and counts the rest. With nothing readable, it says the SDK raised a
generic error and where to look. Every string is sanitized: local paths, credentials, tokens,
connection-string parts and raw GeneXus source are redacted, text is single-line
and bounded to 500 characters. None of this changes what the save does or how it
rolls back.

### Failure-path evidence

The failure path was exercised against the real SDK (GeneXus 18 U16, 18.0.16.189595)
on a disposable Knowledge Base, with synthetic objects only and no Specify,
Generate or Build, for three kinds of WebPanel: a plain one, one hosting a
WorkWithPlus instance, and one whose Events the Work With pattern generated. Each
run read a baseline (parts, variables, properties, version token), previewed the
patch, ran the real isolated save with three calls to a function that does not
exist on numeric SDT members, tried the same patch with an outdated `baseVersion`,
and read everything again.

- The preview passed in all three (`patchMatch`, `isolationEligible`;
  `semanticValidation.performed=false`): it cannot see the rejection.
- The real save failed at `part-save` with three `src0294` diagnostics (position and
  owner as the SDK reports them), `saved=false`, `persisted=false`,
  `rollbackVerified=true`, `implicitOperations=[]`.
- The outdated token was refused (`VersionConflict`) before any save was attempted.
- Afterwards the version tokens, the Events text, the variables and the properties
  hashed identically to the baseline; no invalid source persisted.
- A syntax error in the pattern-generated Events was rejected at `part-save` too
  (eleven `src0102`/`src0051` diagnostics), so the SDK validates that source at save.

Not reproduced: the negative `rawLine` (`-1657`, "by WorkWithPlus") a user saw in the
IDE. It needs a source generated by a WorkWithPlus instance, which cannot be created
headless; the receipt passes whatever the SDK reports (unit-tested with `-1657`).
The installation used for these runs carries a WorkWithPlus build different from the
audited one, which the isolation correctly refuses, so those runs used a throw-away
Worker with that single hash comparison relaxed. The shipped Worker was not changed.

## Edits by other processes while the save runs

The save proves that it wrote no object other than the target by comparing three whole-KB inventories: one before
the transaction opens, one inside it before the commit, and one after the commit. An entry is the object's version,
`LastUpdate`, placement, name and type. On a KB with tens of thousands of objects each inventory takes about a
minute, so a developer with the IDE open will often save another object while a protected save runs. That edit is
not an effect of the save, and it used to fail the operation with `ObjectSaveIncomplete` even though the Events
source had been written and read back.

An object that differs between two inventories is now judged by its SDK stamp against the **save window**:

- The SDK stamps an object when it saves it. The target's own new stamp, read right after the object save, was
  assigned by this Worker somewhere between its first write call and that read, so it fixes the offset between the
  SDK's clock and the Worker's. The window is the interval, in the SDK's clock, from the opening of the transaction
  to its closing, widened by that span and by a 2 s margin on both ends. Every write the save makes carries a stamp
  inside it.
- The window is trusted only when the offset is one the Worker knows (its UTC clock or its local clock, within
  5 s). When the target's stamp did not advance, the persisted stamp differs from the one read after the object save,
  the timeline is inconsistent or the offset is unknown, the window is unavailable (`saveWindow.reason`) and no
  object is cleared: the save behaves exactly as it did before.

| Verdict | `reason` | When |
| :--- | :--- | :--- |
| `concurrent` | `BeforeSaveWindow`, `AfterSaveWindow` | The object exists in both inventories with the same placement, name and type, its version advanced by exactly one, its stamp advanced, and the new stamp lies before the window or, once the transaction has closed, after it. Another process wrote it. |
| `blocking` | `InsideSaveWindow` | The new stamp lies inside the window (while the transaction is open, anywhere after it opened). The save could have written it. |
| `blocking` | `VersionNotAdvancedByOne` | The version did not advance, or advanced by more than one: a second save, which may be the save's own, cannot be told apart. |
| `blocking` | `StampNotAdvanced` | The stamp did not move forward. |
| `blocking` | `MovedRenamedOrRetyped` | Placement, name or type changed. |
| `blocking` | `Created`, `Deleted` | The object appeared or disappeared. |
| `blocking` | `SaveWindowUnavailable` | The window is unavailable. |

A `concurrent` change does not fail the save. The receipt lists it in `concurrentChanges[]` (`guid`, `name`, `type`,
`change`, `verdict`, `reason`, `versionBefore`/`versionAfter`, `lastUpdateBefore`/`lastUpdateAfter` as the SDK
stamps them, `lastModifiedBy`), with `concurrentChangeCount` and `concurrentChangesTruncated` (at most 50 are
described). `saveWindow` says whether the window was calibrated (`from`, `to`, `seconds`, `stampClockOffsetSeconds`)
or why not (`reason`).

A `blocking` change fails the save as before: before the commit it rolls the transaction back (the stage is
`pre-commit-verification`), after the commit it leaves the Events source written. The receipt then names the
objects: `unexpectedChangedObjects` (GUIDs, as before) and `unexpectedChangedObjectDetails[]`. When only other
objects are in doubt, `writeVerifiedForTarget` is `true` (the Events source is committed and read back, the
other parts are intact, the revision advanced), `reasonCode` is `OtherObjectsChangedDuringSave`, `retrySafe` is
`false`, and the message says so in one line instead of the generic one. Do not repeat the edit; compare the listed
objects with the IDE.

A refusal before the commit rolls the transaction back, and when the other object's change belonged to the transaction
the rollback undoes it, so the inventory read afterwards shows nothing. The receipt therefore keeps what the pre-commit
inventory showed in fields the final verdict never rewrites: `preCommitUnexpectedChangedObjects` (GUIDs) and
`preCommitUnexpectedChangedObjectDetails[]` (the same members as `unexpectedChangedObjectDetails[]`, plus
`preCommitUnexpectedChangedObjectDetailsTruncated` when more than 50 were found). `unexpectedChangedObjects` and
`unexpectedChangedObjectDetails[]` describe the final inventory only: a refusal whose rollback was verified reports
them empty and still names its cause in the `preCommit` ones. The default (terse) error projection keeps both sets.

`otherObjectMetadataIntact` now means that no object is blocking. `rollbackVerified` follows the same rule, so an
edit by another process outside the window no longer turns a verified rollback into an unverified one.

What does not change: the target checks (`baseVersion` and the in-transaction token, pins, audited bundle,
callbacks), the comparison of the target's other parts and placement, the explicit rollback, the single-writer gate,
and the fingerprint that decides whether an object differs.

Limits:

- The window covers the whole transaction, including the pre-commit inventory, which on a large KB is as long as any
  other. A save of another object inside that interval still fails the operation. The inventory taken inside the
  transaction is not proved to write nothing, so the window is not narrowed to the moments that write.
- Two saves of one object by the same developer (version +2) stay `blocking`, whoever made them.
- The stamps of other machines are compared with a window derived from this Worker's own write. Clock skew can keep
  an object `blocking`; it cannot clear one.
- The window rests on two facts about the SDK that the calibration checks only for the target: this process stamps
  every object it saves with the same clock, and it writes only while the transaction is open. A write the SDK made
  on its own after the transaction closed (a background task) would be taken for another process's.
- Metadata equality is still not byte equality: an independent writer that persists an object's parts without
  advancing its metadata is not detected, with or without this classification.

## SDK audit and limitations

The original static inspection of GeneXus 18 U16's
`Artech.Packages.Patterns.Package.OnAfterSave` shows an
`AfterSaveKBObject` subscription that can call `ApplyPattern` for an object's
pattern items. It honors `SkipApplyPattern`, but this is not proof that other
installed packages honor the flag. WorkWithPlus has its own save event
subscriptions, with dependencies whose protected implementations could not
be fully inspected. That audit did not identify a global suppression contract.
The subsequent candidate uses the public `EventsService.Events.EventsSuspended`
switch, validates dispatch suppression dynamically, and also audits direct
entity callbacks that this broker switch cannot suppress.

The existing Events object-save path calls SDK persistence methods; omitting
an explicit Apply invocation does not disable these event subscriptions.
Sibling-part snapshots compare only the edited object's other parts. They
do not prove unchanged dependent objects or absence of generated artifacts,
and cannot retrospectively prevent forbidden operations.

The static audit opened no KB and changed no installed binaries or profiles.
Offline routing/receipt/guard tests validate refusal and preview behavior;
they do not prove real SDK isolation. Actual isolated persistence, independent
writer conflict tests, fresh SDK read-back and dependent-object/operation
audits remain pending an authorized disposable KB and a supported event
isolation mechanism. The existing optimistic token checks are retained;
this change makes no new atomic cross-process concurrency guarantee.

## Example: request complete persistence without accepting part-only success

Use a dedicated profile with the destination pins described in
[write-destination-pin.md](write-destination-pin.md). Read Events first and
choose an exact, unique anchor outside generated pattern blocks. The example
uses synthetic object/control names; it is not a live-KB test.

```javascript
native_genexus_read({name: "SampleModule.SamplePanel", type: "WebPanel",
  part: "Events", limit: 0})
native_genexus_edit({name: "SampleModule.SamplePanel", type: "WebPanel",
  part: "Events", mode: "patch", operation: "Replace",
  context: "// account caption anchor", expectedCount: 1,
  content: "// account caption anchor\nAccountMenu.Caption = &DisplayName",
  baseVersion: "<read-versionToken>", requireObjectSave: true,
  dryRun: true, autoDeclareVariables: false, rollbackOnFailure: false})
```

The same request in the abbreviated form, which is the one to prefer:

```javascript
native_genexus_edit({name: "SampleModule.SamplePanel", type: "WebPanel",
  part: "Events", mode: "patch", expectedCount: 1,
  patch: {find: "// account caption anchor",
          replace: "// account caption anchor\nAccountMenu.Caption = &DisplayName"},
  baseVersion: "<read-versionToken>", requireObjectSave: true, dryRun: true})
```

What makes the arguments valid, and what is refused rather than adapted:

- `requireObjectSave` takes `mode: "patch"` with `part: "Events"` (or full `Source`).
  Full `Events` is refused with `RequireObjectSaveUnsupportedMode` and other
  parts with `RequireObjectSaveUnsupportedPart`. That is a documented contract,
  not a GeneXus syntax error, and nothing falls back to another mode.
- `baseVersion` (alias `expectedVersion`) is the `versionToken` of a fresh read
  of that part, and is required for a real write. A preview without it reports
  `canApply: false`; a stale one is refused with `VersionConflict`; two aliases
  that disagree are refused with `VersionAliasesConflict` before dispatch.
- A line break in `find`, `replace` or `content` must reach the MCP as a real
  line break (the JSON escape `\n` inside the string). A doubled backslash is
  stored as the two characters backslash and `n`, and the MCP does not convert it.
- `expectedCount` makes zero or several matches a refusal instead of a guess.

An ineligible preview returns `canApply: false`, `applyBlockers[]` and
`writeBlocker: ObjectSaveIsolationUnverified`.
Repeating an ineligible patch with `dryRun:false` returns that error before
writing, with `writeAttempted:false`, `partPersisted:false`, and
`objectSaved:false`. An eligible candidate must still pass the checks at the
actual save boundary and the mandatory post-save verification.
Do not remove `requireObjectSave` or retry the request through another write
mode to bypass it. Live integration evidence for the candidate remains pending.
No build or pattern application is requested
by these example arguments.

## Offline validation

- Full Worker U16 suite: 2,476 passed, four integration tests skipped.
- Full Gateway suite: 1,560 passed, 13 integration tests skipped.
- U11/U12: Worker builds passed and 56 focused tests passed per SDK,
  including 36 destination-guard cases (dispatcher tests are in the same class).
- The pre-existing SDK fingerprint manifest accepts U10 only; installed-SDK
  offline tests used `GxMcpSkipSdkValidation=true` without changing the
  manifest. Distribution compatibility was not validated.

No integration fixture was opened. These results establish routing, refusal,
receipt classification and destination checks under test, not successful SDK
object persistence or its side effects.
