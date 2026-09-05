# Record safety and performance evidence

## Method

Compared the service at `aef84fb` with the revised implementation through the
same ADO.NET SQL Server connection and trusted internal metadata seam. The
legacy service was unchanged except for dependency injection. The database
executed real SELECT/INSERT/UPDATE/COMMIT/ROLLBACK against disposable, isolated
synthetic tables (100, 1,000 and 10,000 rows, integer key and 160-character
payload). Each case had two warmups and seven measured iterations.

The seam replaces SDK metadata discovery only; it does not simulate SQL,
transactions or triggers. Credentials and business data are not included here.
Median elapsed times include connection checkout, SQL, materialization and
serialization. These are local measurements, not production throughput claims.
Concurrent machine activity and sample size limit conclusions about small
sub-millisecond differences.

## Observed results

With 10,000 synthetic rows:

| Scenario | Before median | After median | Structural difference |
| --- | ---: | ---: | --- |
| Explicit-key insert preview | 375.987 ms | 0.756 ms | 10,000 returned rows to zero |
| Refuse ambiguous update | 385.999 ms | 0.774 ms | 10,000 returned rows to two |
| Query one existing key | 0.522 ms | 0.432 ms | One datastore round trip remains |
| Preview + update + commit/reread | 3.650 ms | 2.384 ms | 12 to 9 server round trips |

Rows here are returned to the client, not SQL Server logical-page reads. A
missing index can still make an equality predicate expensive. No evidence
supports extrapolating these ratios to every KB or network.

## Real database checks

- A dry-run returned a preview without persisting application rows.
- A competing update between preview and write was refused.
- Changing the approved values invalidated the preview.
- An explicitly declared audit trigger persisted and was reread.
- An undeclared trigger change rolled back before commit.
- An injected reread-connection failure after a real commit reported
  `persisted=true`, `rereadConfirmed=false` and `retrySafe=false`.
- An identity insert with a trigger returning an unrelated SELECT result
  returned the actual generated key; update row counts also ignored trigger
  result sets.

The last check exposed SQL Server error 257 in an intermediate candidate:
`sql_variant` cannot be implicitly assigned to a typed output parameter.
The final implementation uses a matching, closed-mapping SQL type. The fake
provider now rejects the unsafe assignment, preventing false test approval.

Application-data validation was SELECT-only: 100 rows, three key columns, no
values disclosed. All synthetic tables/triggers were removed after each run.
No KB lifecycle, application build/reorganization or business-data write ran.

## Costs and limits

- Every record query now reaches the database, including repeated empty queries.
  This intentionally gives up cache-hit latency. Remote-network overhead was
  not benchmarked; source caching was not changed.
- Writes require single-use v2 receipts from the same reviewed operation.
  Query/v1 tokens, expired receipts and receipts from another Worker are refused.
- Inserts no longer treat unrelated rows as part of their concurrency scope.
- Rollback before commit remains mandatory. After observed post-commit
  divergence, automatic compensation is refused to preserve a concurrently
  deleted/recreated row. Recovery needs inspection and a fresh approved write.
- Commit uncertainty and deleted/recreated-row interleavings have deterministic
  provider tests; the database experiment did not cut the SQL Server network
  during COMMIT.
- The native SDK connection-discovery path returned
  `DataStoreConnectionUnavailable` in the inspected profile before these changes.
  Direct ADO evidence does not prove that end-to-end SDK resolution works there.
- Neither these tests nor the report certify every provider, trigger, schema,
  production workload or absence of future bugs.
