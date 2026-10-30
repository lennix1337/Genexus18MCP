# WorkWithPlus WebPanel layout authoring

`genexus_wwp action=add_layout` adds native nested controls to a WorkWithPlus
instance bound to a WebPanel or WebComponent: tables, variables and buttons
(`userAction`) placed under a `tablePath`. The action edits the PatternInstance
through the pattern's own commands, then verifies the saved instance and the
projected parent WebForm.

## Request

```json
{
  "action": "add_layout",
  "name": "WorkWithPlusSamplePanel",
  "tablePath": "TableMain > TableContent",
  "expectedVersion": "<version read from the PatternInstance>",
  "dryRun": true,
  "children": [
    {"type": "table", "name": "TableDetails", "tableType": "Responsive", "children": [
      {"type": "variable", "name": "Application"},
      {"type": "userAction", "name": "Consult", "caption": "Consult"}
    ]}
  ]
}
```

- `expectedVersion` is required, also for `dryRun`. Read the instance first
  (`part: PatternInstance`) and pass its `versionToken`. A stale value returns
  `StaleObject` and nothing is written.
- `children` is a typed tree added after `tablePath`. The supported types are
  `table` (`tableType` `Responsive` or `Regular`, plus nested `children`),
  `variable` and `userAction` (optional `caption`). Names must be valid and unique
  in the instance.
- Every variable must already be declared on the parent object. Declare it with
  `genexus_variable` first; otherwise the call returns `WwpVariableNotDeclared`
  before any child is created.
- Existing content, declarations and metadata are preserved. The action only appends
  children: it never reorders or rewrites existing ones. It refuses names that already
  exist in the instance, attributes outside the typed children, and tables or actions
  added to a container that already has a `childrenOrderedList`
  (`WwpOrderedContainerUnsupported`).

## Preview and write

With `dryRun: true` the call returns `WwpLayoutDryRun` with the typed diff and the
current `versionToken`, and changes nothing. With `dryRun: false` and the same
version, the action:

1. snapshots the PatternInstance, the parent WebForm, Events and Variables;
2. adds the children through the native pattern commands and saves the instance;
3. projects the saved instance onto the parent WebForm through the WorkWithPlus
   projection callback;
4. verifies the state inside the transaction (pattern, projected WebForm, native
   variable bindings, and Events and Variables that must not change), then commits;
5. re-reads the saved instance and the parent WebForm to confirm the commit.

A failure before the commit aborts the transaction and independently verifies the
restored state (`restorationVerification`, `stateRestoredExactly` and `rollbackVerified`
appear only after a rollback). A failure after the commit does not perform a
compensating write; inspect the returned receipt (`commitCompleted`,
`rollbackPerformed`, `postSaveVerification`, `projection`, `snapshot`).
No build or generate step runs implicitly.

## Failures

A failed call says where it stopped. `savePhase` names the stage and the error code
follows it, unless the action raised a typed one (`StaleObject`, `WwpTableNotFound`,
`WwpAttributeRejected`, ...):

| `savePhase` | Stage | Code of an untyped exception |
| --- | --- | --- |
| `transaction-fresh-read` | fresh read and boundary check | `WwpLayoutPreSaveFailed` |
| `native-mutation` | creating and attaching the children | `WwpLayoutPreSaveFailed` |
| `object-save` | the native save of the instance | `WwpLayoutSaveFailed` |
| `projection` | the WorkWithPlus projection callback | `WwpProjectionFailed` |
| `pre-commit-verification` | the checks inside the transaction | `WwpLayoutNotVerified` |
| `commit` | the transaction commit | `WwpLayoutCommitFailed` |
| `post-commit-verification` | the checks after the commit | `WwpLayoutNotVerified` |

`nativeOperation` carries the same stage (and the child being edited). A refused
verification lists the checks it failed in the message and in `failedChecks`, with the
projection step in `failureStage`; the full `preCommitVerification` and
`postSaveVerification` receipts are in `verbose_errors`. `persisted` is `false` when the
call wrote nothing (a concurrent change refused at the boundary is not a partial write),
`null` with `partialPersistenceDetected: true` when the state could not be proven
restored, and the target is marked for the next full build after a commit or in that
case.

`position`, `before` and `after` are refused with `WwpLayoutPlacementUnsupported`: the
action only appends after the existing children of `tablePath`.

## Limits

- Only WorkWithPlus instances bound to a WebPanel or WebComponent are supported
  (`WwpLayoutParentUnsupported` otherwise).
- Child types other than `variable`, `userAction` and `table` are refused
  (`InvalidWwpChild`).
- Attribute-based variables, and variable types without a verified layout adapter,
  are refused (`WwpVariableBindingUnsupported`).
