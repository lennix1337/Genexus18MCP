# Typed Transaction records

The `genexus_db` umbrella exposes three root-level Transaction record actions:

- `records_query` reads rows using Transaction attributes and equality filters.
- `records_insert` previews or inserts one row.
- `records_update` previews or updates one primary-keyed row.

The adapter obtains the table name, root attributes, types, lengths, decimals,
and primary key from the SDK Transaction structure. Attribute names are resolved
against that metadata and values are sent as database parameters; callers cannot
provide SQL identifiers or predicates.

Writes are read-only by default. A persisted write requires a v2 `versionToken`
returned by the same write's dry-run. Query and legacy v1 tokens cannot authorize
writes. Receipts are single-use, expire after 15 minutes and belong to the Worker
that issued them. Up to 1,024 pending receipts are retained; older receipts may
be evicted under pressure. Restarting the Worker requires a new preview. The
receipt binds KB, environment, datastore, connection, Transaction identity,
schema, operation, values, scoped rows and rollback policy. Only an approval
digest is retained, never sources or row contents.

The write path rechecks the token inside
a serializable database transaction, captures the complete affected-row
snapshot, writes, rereads before commit, commits, and rereads again through a
new connection. Failure before commit rolls back the original transaction.
After commit, a divergent reread never triggers automatic compensation: equal
values cannot prove that another writer did not delete and recreate the key.
Recovery requires inspection and a newly approved operation. This deliberately
reduces automatic recovery compared with the earlier compensating implementation
to avoid overwriting a concurrent replacement.

`rollbackOnFailure` remains accepted (default true) and bound to the preview
for compatibility. It never disables the mandatory rollback on validation
failure before commit, and it no longer authorizes compensation after commit.
A committed write may therefore remain applied when confirmation fails.

Inserts inspect only their explicit primary key; SQL Server inserts with one
generated key do not scan existing rows. Updates stop after the second match
and report `matchedCountExact=false`, not a full count. Each operation changes
at most one row; multiple related calls are not an atomic batch.

All `records_*` calls bypass the Gateway semantic cache, including empty queries
and previews. Repeating a query reaches the datastore again. This costs a fresh
round trip but avoids stale absence or stale success. Source-cache policy is
unchanged.

Declare trigger-maintained non-key attributes in `databaseManagedFields` when
needed. They cannot also appear in `values`. This exception is preview-bound
and applies only before commit; the complete observed state is still compared
after commit. An undeclared trigger change rolls back before commit. A
post-commit mismatch reports `ConcurrentChangePreserved`, with
`automaticCompensationSupported=false` and no further write.

Never automatically retry a write after timeout or an uncertain outcome:

| persistenceState | persisted | Meaning |
| --- | --- | --- |
| Confirmed | true | Commit and independent reread succeeded |
| CommittedUnverified | true | Commit succeeded; reread unavailable |
| Indeterminate | null | Commit or rollback outcome unknown |
| Diverged | null | Post-commit mismatch; restoration not confirmed |
| NotPersisted | false | No commit confirmed; inspect rollback status |

Inspect `commitState`, `rereadConfirmed`, keys and rollback diagnostics before
deciding the next action. `retrySafe=false` prohibits blind repetition. A new
preview is required even after a successful write.

Example preview:

```json
{
  "action": "records_update",
  "transaction": "SampleIntegration",
  "where": { "ProcessCode": "DEMO001", "Provider": "ProviderV3" },
  "values": { "CommunicationId": 42 },
  "dryRun": true,
  "rollbackOnFailure": true
}
```

The response contains `persisted=false`, the typed diff, the matched keys, and
the token to use for an explicitly authorized write. Insert and update results
return reread records and their key values, which supports chaining a generated
communication key into a related integration row.

This capability does not call Specify, Generate, Build, Rebuild, Compile,
Reorg, publication, execution, or tests. It operates on existing physical data
only; it does not create or alter GeneXus objects or database schema.
