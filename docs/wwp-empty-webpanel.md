# Buttons and variable grids on an Empty WebPanel

After attaching WorkWithPlus with the Empty template, use the typed operations
below. Replace the synthetic object and column names with the intended screen.
Preview each request with `dryRun: true`, review its diff, then submit the same
request with `dryRun: false`, `expectedVersion` equal to the returned token, and
`rollbackOnFailure: true`. Each save changes the token; preview the next edit again.

```json
{
  "action": "add_grid",
  "name": "SamplePanel",
  "containerName": "TableContent",
  "gridName": "Grid",
  "columns": [
    { "variable": "Code", "basicType": "Character", "length": 13, "description": "Code" },
    { "variable": "Op", "basicType": "Numeric", "length": 2, "decimals": 0, "readOnly": false },
    { "variable": "Date", "basicType": "Date" }
  ],
  "dryRun": true
}
```

Omitting `collection` and `sdt` selects the variable-grid mode. `gridName`
defaults to `Grid`; variable names may carry `&`. Each column requires
`basicType`. Numeric, Character, VarChar and LongVarChar require a positive
`length`; only Numeric accepts `decimals` (less than length). `readOnly`
defaults to true. Columns retain their input order. No SDT identities or
collection-loading code are introduced; use the existing `Grid.Load` event
and `Load` to supply rows. The SDT-collection mode retains its existing
`collection`, `sdt`, item-column and optional `deleteAction` contract.

```json
{
  "action": "add_user_action",
  "name": "SamplePanel",
  "actionName": "Confirm",
  "caption": "Confirm",
  "dryRun": true
}
```

If `TableActions` is absent, the operation creates a Responsive table under
exactly one `TableMain` and adds the button there. An explicit existing
`containerName` still works; other missing containers and ambiguous parents
are refused. The button derives `DoConfirm`; handle that event in the panel's
Events after the controls have been projected, preserving the screen's existing
events. `callObject`, popup and parameters retain their existing behavior.

Both operations use native element commands, exact instance/form snapshots,
optimistic concurrency, save/reread verification, WebForm projection and
rollback. They do not run Specify, Generate, Build or any other KB lifecycle
operation. `Default*` and `childrenOrderedList` baselines are not authored.
The raw structural-XML guard remains unchanged.

Regression coverage includes Empty-template previews, invalid/ambiguous
selectors, typed editable columns, ordered rereads, dropped bindings and
native command adapters. Native Save/projection/rollback in a disposable KB
remains a separate live validation gate; adapter tests do not prove it.
