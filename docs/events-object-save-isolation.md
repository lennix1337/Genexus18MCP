# Required Events save isolation

`requireObjectSave=true` uses the restricted U16 candidate described in
[Events save candidate](events-save-u16-candidate.md). Unverified SDK hashes,
object/part types, direct callbacks, destinations, cache removal, or broker
suppression return `ObjectSaveIsolationUnverified` before persistence. Dry runs
include the dynamic preflight result and a `writeBlocker` when ineligible.
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

An ineligible preview returns `writeBlocker: ObjectSaveIsolationUnverified`.
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
