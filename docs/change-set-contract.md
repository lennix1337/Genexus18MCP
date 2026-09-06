# Mutation and change-set contract

The Gateway treats a non-preview mutating call with an `idempotencyKey` as one
logical operation. The in-memory cache coalesces concurrent callers and keeps a
successful receipt for the configured TTL. The restart journal at
`state/mutation-operations.json` stores only a KB-scope hash, canonical tool,
key hash, payload hash, status, and timestamp.

The journal is a safety fence, not a result store. If a process stops after the
mutation starts, the next process returns `operation_unknown` for the same key
instead of dispatching the SDK again. The caller must inspect the target and
choose a new authorization. A different payload under the same key is always a
conflict. Journal entries expire after seven days and writes use a sibling
temporary file followed by an atomic replace. The versioned journal is bounded
and fails closed when its contents or persistence cannot be trusted; it never
falls back to an empty state that could replay a mutation.

Preview calls do not enter the journal. Errors rejected before persistence are
removed from the journal; an exception after dispatch intentionally leaves the
entry unresolved because the SDK outcome cannot be inferred safely.

Post-timeout read fences are stored separately in the versioned
`genexus-mutation-recovery/1` journal. The fence key includes KB, object, and
part, so an unresolved `Source` write cannot be cleared by reading `Rules`.
The Gateway fails closed for non-preview writes if that journal is truncated,
corrupt, oversized, or cannot be atomically replaced; read-only inspection and
repair remain possible without replaying the SDK operation.

`genexus_whoami` exposes the pending fence list under `mutationRecovery` when
one exists (including KB, object, part, operation ID, and journal health). This
is an inspection surface only; it never retries or restores an operation.

For a durable keyed operation, `genexus_lifecycle action=inspect` accepts
`operationTool` and `operationKey` and returns only redacted journal state. After
an independent `genexus_read` confirms the affected target, callers may use
`action=reconcile` with `confirmed=true` and a short `verification` statement.
The Gateway persists only a hash of that statement, closes the unknown fence,
and still requires a fresh idempotency key for any subsequent write. Entries
created from target/revision-aware mutation arguments also bind hash-only target
identities and base revisions. In that case reconciliation must include
`observedTargetIds` and `observedRevision`; a mismatch remains rejected and the
unknown fence stays open.

`genexus_edit.changeSet` now exposes an explicit `preview` → `validate` →
`apply` flow for existing `Source`, `Rules`, and `Variables` parts. Preview
returns a deterministic `changeSetId`, per-target current/requested versions,
and an aggregate `baseRevision`. Validate reads the same set and reports the
same revision. Apply requires both values, re-reads the targets, refuses any
revision drift, and pins missing per-target `expectedVersion` values before
dispatching the MutationEngine.

The first slice is deliberately limited to existing text parts. Create,
delete, structure, pattern, and generated artifacts remain separate adapters;
they must prove their own SDK transaction boundary before joining this
contract.

`genexus_edit.targets[]` already uses the MutationEngine path. Its legacy
`MultiEditCompleted` code is retained for compatibility, while the result is
produced by the same per-target preflight, save/readback, and compensation
logic used by direct mutations. Internal batch callers that submit nested
`changes` remain on the legacy `BatchService` adapter until the explicit
change-set action is certified.

The multi-object mutation engine already applies the same safety boundary to
compensation: a rollback is confirmed only when every rollback write succeeds
and a subsequent read matches the captured content. An unreadable or divergent
read is reported as `rollback.outcome=indeterminate`; a failed rollback write is
`partial`. Multi-target requests can include `expectedVersion` (or
`baseVersion`) per target and are rejected before the first write when a known
version differs. If the current part cannot be read, the engine fails closed
with `ConcurrencyStateUnavailable`; it never treats an unreadable version as a
match.
