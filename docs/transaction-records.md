# Typed Transaction records

The `genexus_db` umbrella exposes three root-level Transaction record actions:

- `records_query` reads rows using Transaction attributes and equality filters.
- `records_insert` previews or inserts one row.
- `records_update` previews or updates one primary-keyed row.

Every response identifies the adapter as `dataAccess="typed_sql"` and sets
`businessRulesExecuted=false`. The adapter validates GeneXus metadata and
transaction safety, but it does not execute a generated Business Component,
Transaction rules, defaults, or application triggers as GeneXus logic.

The adapter obtains the table name, root attributes, types, lengths, decimals,
and primary key from the SDK Transaction structure. Attribute names are resolved
against that metadata and values are sent as database parameters; callers cannot
provide SQL identifiers or predicates.

## DB2 and IBM i datastores (read-only)

Datastores whose DBMS is Db2, Db2/AS400 or Db2Universal (including DB2 for IBM i
reached through the JT400 JDBC driver) map to the `db2` dialect. `records_query`
generates `SELECT ... FETCH FIRST n ROWS ONLY` with positional `?` parameters and
uppercase double-quoted identifiers (GeneXus creates unquoted DB2 DDL). The
library (`DTALIB` / schema) is used as the qualifier. `sample_data`, `drift_check`
and `sql_navigation` do not generate dialect-specific SQL; `sql_navigation` plans
are always `planUnavailable` (syntax-only `EXPLAIN`), as on every other DBMS.

`records_insert` and `records_update` are refused on DB2 with
`DataStoreWritesUnsupported` before any connection is opened.

The Worker is .NET and cannot load the JDBC driver the GeneXus datastore names.
Executing a query needs an IBM ADO.NET provider registered by invariant name
(`IBM.Data.DB2.iSeries`, `IBM.Data.DB2` or `IBM.Data.Db2`), either via IBM i
Access Client Solutions or by registering the `IBM.Data.DB2.iSeries` NuGet
package in `DbProviderFactories` (`App.config`); without one the request fails
with `DataStoreProviderUnavailable` naming what to install.

When the GeneXus SDK does not expose connection metadata (for example when the
connection is configured in the runtime environment or JNDI datasource rather
than in KB datastore properties), `records_query` can select an explicit
SQL Server or DB2 `dataStoreAlias`. Configure the alias in the MCP profile under
`Environment.DataStoreAliases` (or under the matching
`Environment.KBs[].DataStoreAliases` entry):

```json
{
  "Environment": {
    "DataStoreAliases": {
      "production": {
        "dataStore": "Default",
        "family": "sqlserver",
        "server": "SERVER\\INSTANCE",
        "database": "APP_PROD",
        "schema": "dbo",
        "integratedSecurity": true
      }
    }
  }
}
```

For SQL authentication, set `integratedSecurity` to `false` and reference host
environment variables with `userIdEnvironmentVariable` and
`passwordEnvironmentVariable`. A `connectionStringEnvironmentVariable` is also
accepted when the complete secret connection string is kept only in the Worker
host environment. The value of a secret is never returned or logged by the MCP.
Read-only profile aliases support SQL Server and DB2 only (for DB2 on IBM i,
`database` is optional and `integratedSecurity` defaults to `false`, so host
`userIdEnvironmentVariable`/`passwordEnvironmentVariable` or
`connectionStringEnvironmentVariable` must be provided). When the active datastore
is PostgreSQL, Oracle, or MySQL and the SDK does not expose connection
metadata, `records_query` fails closed with `DataStoreConnectionUnavailable`
whose hint states the SQL Server and DB2-only alias limitation instead of
suggesting a `dataStoreAlias` the service would reject with
`DataStoreAliasProviderUnsupported`. No connection string is invented and no
credential is exposed.
The alias is available only to `records_query`; insert/update operations reject it
before opening a connection. The alias is reread immediately before the query; a
profile, KB, or active-datastore change is rejected as `DataStoreConfigurationChanged`
before SQL is executed.
The successful response includes the effective alias, masked server/database,
`healthCheck="confirmed"`, `elapsedMs`, `rowCount`, and both the compatibility
`records` field and the `rows` field.

Example:

```json
{
  "action": "records_query",
  "dataStoreAlias": "production",
  "transaction": "Order",
  "where": { "OrderId": 1 },
  "limit": 20,
  "timeoutSeconds": 20
}
```

An absent, malformed, duplicate, or cross-KB alias fails before opening the
database. The query remains read-only and does not persist profile/configuration
changes or run Specify, Generate, Build, Rebuild, Compile, Reorg, publication,
execution, or tests.

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
