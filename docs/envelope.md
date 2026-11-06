# MCP response envelope (v2.8.0)

Every tool response from the worker emits this shape. The gateway is pass-through; whatever the worker writes is what the client sees.

## Wire shape

```json
{
  "status": "ok" | "error" | "partial" | "accepted",
  "code":   "MachineReadableId",
  "target": "<object name, optional>",

  "result": { "...payload...": "" },

  "error": {
    "code":      "StableErrorCode",
    "message":   "Short human sentence.",
    "hint":      "One-line plain-English fix.",
    "retryable": true,
    "reconciliationRequired": false,
    "nextSteps": [{ "tool": "...", "args": {}, "why": "..." }]
  },

  "operationId": "...",
  "pollTarget":  "..."
}
```

| status | When | Required fields |
|---|---|---|
| `ok` | Tool completed successfully | `status`; `result` recommended |
| `error` | Tool failed | `status`, `error.code`, `error.message`. `retryable` and `reconciliationRequired` are explicit booleans when relevant; `hint` and `nextSteps[]` strongly recommended. |
| `partial` | Tool succeeded but with caveats (warnings, partial data) | `status`, `result`, `warnings[]` |
| `accepted` | Long-running tool returns immediately with a handle | `status`, `operationId`. Add `pollTarget` so the LLM knows which lifecycle target to poll. |

## Idempotency (`clientRequestId`)

Mutating tool calls accept an optional `clientRequestId` (a string the client picks, e.g. a UUID). The worker caches the response keyed by id for 5 minutes; a retry with the same id returns the cached response with `_meta.replayed: true` and `_meta.replayedFromUtc: <ISO>` set. The canonical `status` / `code` / `result` / `error` fields are unchanged on a replay.

Use this to make retries safe after socket drops, gateway timeouts, or LLM-side cancellations:

```json
// First call
POST tools/call {
  "name": "genexus_delete_object",
  "arguments": { "name": "X", "clientRequestId": "uuid-1234" }
}

// → response: {"status":"ok","code":"ObjectDeleted","target":"X",...}

// Retry after socket drop (same arguments, same clientRequestId)
POST tools/call { ...same as above... }

// → cached response: {"status":"ok","code":"ObjectDeleted",...,"_meta":{"replayed":true,"replayedFromUtc":"2026-05-28T..."}}
```

Excluded from caching: `ping`, `control` (cancel side-channel). Cache lives in worker memory; restarting the worker drops it.

## Streaming progress (`notifications/progress`)

Long-running tools push progress to the MCP client as JSON-RPC notifications instead of forcing a poll. The notification follows the MCP spec shape, enriched with two extra fields:

```json
{
  "jsonrpc": "2.0",
  "method":  "notifications/progress",
  "params": {
    "progressToken": "<the token the client supplied at request time>",
    "progress":      <int, items completed so far>,
    "total":         <int, items in the unit of work — 100 for percent-style>,
    "message":       "<short status sentence>",
    "stage":         "<optional, short stage label e.g. 'indexing','compiling','projecting'>",
    "elapsedMs":     <optional, ms since operation start>
  }
}
```

The `progressToken` is the value the client sent in the request's `_meta.progressToken`. Without that, the worker stays silent (the client didn't opt in). `stage` and `elapsedMs` are extensions on top of the spec — clients that don't render them ignore them safely.

Tools that emit progress today: KB index, pattern apply (projection), build/rebuild (gateway-side heartbeat). Tools that should over time: long edits, refactor, restore. Polling via `genexus_lifecycle action=status target=op:<id>` remains supported.

For `genexus_lifecycle` builds without an explicit low `estimated_seconds`, the Gateway uses the accepted async path. `wait_until_done` remains bounded for each request, while the job continues to be polled independently; Build All/Rebuild All use a 2700-second Gateway hard cap, leaving the Worker’s default 2400-second watchdog time to terminalize the build. `GXMCP_BUILD_TIMEOUT_SEC` is honored with the same `[60,7200]` clamp plus a 300-second Gateway margin.

## Persistence-sensitive property writes

`genexus_properties` does not treat an SDK setter call as proof of persistence. Scalar writes are re-read from a freshly resolved object after `EnsureSave`/commit; a mismatch returns `error.code: "PropertyNotPersisted"` instead of a success envelope. Batch writes use the same verification for every property and only include `result.persistedVerified: true` when every property was independently confirmed.

Some GeneXus SDK members are derived or immutable after creation. In particular, changing `Type` on an existing Attribute is not supported by the SDK property surface. The request returns `error.code: "UnsupportedOperation"`, with the original type preserved; clients must not retry it. Create the Attribute with the desired type or change it in the GeneXus IDE. `OutputSDT` on a Data Provider uses its typed SDK API and is also read back after commit.

Batch requests reject object moves (`Folder`, `Module`, and aliases) and typed-only `OutputSDT` combinations explicitly rather than silently skipping or applying them as scalar strings. Use `action=move` for moves and a single `action=set` request for `OutputSDT`.

## Shared-host concurrency errors

This section applies only when `Server.WorkerSharingMode` is `shared-host`. Isolated_Mode (`isolated`, the default) responses are unchanged.

In shared-host mode, several Gateways attach to one Worker per KB. Every concurrency failure uses one fixed `error.code` from the table below. The broker, the Worker, and the Gateway all build these errors from the same catalog (`src/GxMcp.Shared/SharedHost/ConcurrencyErrorCatalog.cs`), so a code carries the same flags whichever process raises it. Besides `code`, `message` and `hint`, every error carries:

- `retryable`: always an explicit boolean.
- `dispatchState`: exactly one of the following values:
  - `not-delivered`: the request never reached the Worker process.
  - `delivered-unanswered`: the request reached the Worker, which exited or disconnected before it answered.
  - `delivered-rejected`: the Worker received the request and rejected it before any SDK save.
- `reason`: present only on rows that list reason values, and then always one of those values.
- `retryAfterMs`: an integer, present only when `retryable` is `true`.

### Concurrency_Error_Table

| Code | `reason` values | `retryable` | `dispatchState` |
|---|---|---|---|
| `WorkerBusy` | `queue-wait-expired`, `queue-full`, `session-queue-limit` | `true` | `not-delivered` |
| `KB_POOL_FULL` | `worker-limit`, `max-open-kbs`, `low-memory` | `false` | `not-delivered` |
| `KB_WORKER_OWNED_ELSEWHERE` | `owner-does-not-accept-attachments`, `liveness-unverified` | `false` | `not-delivered` |
| `SHARED_WORKER_IDENTITY_MISMATCH` | none | `false` | `not-delivered` |
| `SHARED_WORKER_ATTACH_FAILED` | `readiness-timeout`, `pipe-refused` | `true` | `not-delivered` |
| `WORKER_STARTUP_FAILED` | `replacement-failed`, `cold-start-wait-expired` | `true` | `not-delivered` |
| `SHARED_WORKER_BACKPRESSURE` | none | `true` | `not-delivered` |
| `SHARED_WORKER_RESTARTED` | none | see below | `not-delivered` or `delivered-unanswered` |
| `TargetLockedByOtherAgent` | none | `true` | `delivered-rejected` |
| `StaleObject` | none | `false` | `delivered-rejected` |
| `LockCheckFailed` | none | `true` | `delivered-rejected` |
| `VersionCheckUnavailable` | none | `true` | `delivered-rejected` |

`SHARED_WORKER_RESTARTED` has `retryable=true` when `dispatchState` is `not-delivered`, which happens when a queued request is dropped because the Worker exited. When `dispatchState` is `delivered-unanswered`, `retryable` is `true` only when the command's Operation_Class is read. It is `false` for a write and for a command that has no Operation_Class entry, because the write may already have been applied.

### `retryAfterMs`

`retryAfterMs` is present only when `retryable` is `true`. It is an integer that is never negative.

- For `TargetLockedByOtherAgent`, the value ranges from 0 to the milliseconds remaining until the lock expiry. When the raiser names no delay, the value is the full remaining time, so the retry happens at the expiry.
- For every other code, the value ranges from 0 to the configured queue wait, inclusive. When the raiser names no delay, the value is 1000 ms, capped by the queue wait.

A delay requested by the raiser is clamped into that range.

### Hints

The catalog builds each hint from the code, the reason and `dispatchState`. A hint never contains source content, request payloads, Lease tokens, or credentials. The hint text is quoted exactly from the catalog:

| Code | Condition | `hint` |
|---|---|---|
| `WorkerBusy` | any reason | "Retry the request after retryAfterMs milliseconds." |
| `KB_POOL_FULL` | `worker-limit`, `max-open-kbs` | "Close an open KB with genexus_kb action=close, or wait for idle Worker reaping, then open the KB again." |
| `KB_POOL_FULL` | `low-memory` | "Close open KBs, GeneXus IDE instances, or idle MCP sessions to free memory, then open the KB again." |
| `KB_WORKER_OWNED_ELSEWHERE` | holder PID known | "Ask the user to release the KB in the holder Gateway with PID `<pid>`, then open the KB again." |
| `KB_WORKER_OWNED_ELSEWHERE` | holder PID unknown | "Ask the user to release the KB in the holder Gateway, then open the KB again." |
| `SHARED_WORKER_IDENTITY_MISMATCH` | - | "Align the GeneXus installation, driver, target, and Worker executable with the live Worker_Host, or close the sessions attached to it, then open the KB again." |
| `SHARED_WORKER_ATTACH_FAILED` | any reason | "Retry the request after retryAfterMs milliseconds." |
| `WORKER_STARTUP_FAILED` | any reason | "Retry the request after retryAfterMs milliseconds." |
| `SHARED_WORKER_BACKPRESSURE` | - | "Retry the request after retryAfterMs milliseconds." |
| `SHARED_WORKER_RESTARTED` | `retryable=true` | "Retry the request after retryAfterMs milliseconds." |
| `SHARED_WORKER_RESTARTED` | `retryable=false` | "Read the target first to check whether the write was applied before you resend it." |
| `TargetLockedByOtherAgent` | - | "Retry after the lock expiry, at the earliest after retryAfterMs milliseconds, or coordinate with the lock holder." |
| `StaleObject` | - | "Re-read the object part to get its current version token before writing again." |
| `LockCheckFailed` | - | "Retry the write after retryAfterMs milliseconds." |
| `VersionCheckUnavailable` | - | "Retry the write after retryAfterMs milliseconds." |

`<pid>` stands for the holder Gateway PID as a decimal integer.

### Normalization of Worker conflict codes

In shared-host mode, the Gateway normalizes the error before it reaches the client. It rewrites `retryable`, `dispatchState` and `hint` from the catalog row. It also writes `retryAfterMs` when the error is retryable and removes it otherwise, and writes `reason` on rows that list reasons. `code` never changes. The raiser's non-empty `message` is kept, and every other field, inside and outside `error`, stays as the raiser wrote it. Normalization is idempotent and never invents a missing reason.

- The Worker conflict codes `TargetLockedByOtherAgent`, `StaleObject`, `LockCheckFailed` and `VersionCheckUnavailable` always carry `dispatchState="delivered-rejected"`. The target object part keeps the content and version token it had before the rejected request.
- `TargetLockedByOtherAgent` also gains `part`. The value is the error's own part, else the part named by the request, else `Source`. Its `retryAfterMs` is derived from `lockExpiresAt`: the whole milliseconds from now until that time, at least 0. A missing or unparseable `lockExpiresAt` gives 0.
- The default terse output keeps `retryable`, `dispatchState`, `retryAfterMs`, `lockHolder` and `lockExpiresAt` for every table code, so a client can make the retry decision without asking for the full envelope.

```json
{"status":"error","target":"CustomerProc",
 "error":{
   "code":"TargetLockedByOtherAgent",
   "message":"The target object part is locked by another owner; the write was rejected before any save.",
   "hint":"Retry after the lock expiry, at the earliest after retryAfterMs milliseconds, or coordinate with the lock holder.",
   "retryable":true,
   "dispatchState":"delivered-rejected",
   "retryAfterMs":42000,
   "part":"Source",
   "lockHolder":"lease-b",
   "lockExpiresAt":"2026-03-04T10:00:42.0000000Z"
 }}
```

### `SHARED_WORKER_RECOVERY_FAILED`

This code is not a row of the Concurrency_Error_Table, and the Gateway passes it through without normalization. Stale ownership recovery returns it when it cannot complete. The remaining ownership records stay unchanged, and recovery never stops a process that it cannot verify. The error always has `retryable=false` and `dispatchState="not-delivered"`, and it has no `retryAfterMs`. It names the failure in `failureReason` rather than in `reason`:

| `failureReason` | Meaning |
|---|---|
| `remove-failed` | A stale ownership record could not be removed. |
| `kill-failed` | Stopping the verified orphan Worker child failed. |
| `still-running` | The orphan Worker child was still running when the stop wait ended. |

Its other fields are `recordKey`, `registry` (`WorkerOwnershipRegistry`, `SharedWorkerRegistry` or `SingleInstanceLock`), `blockingPid` when a process blocks recovery, and `detail` when one is available. The hint is "Ask the user to stop the process with PID `<pid>` or remove the stale record, then open the KB again." when `blockingPid` is known, and "Ask the user to remove the stale record, then open the KB again." otherwise.

### Launcher refusals

When the Gateway cannot attach a shared Worker, the launcher raises a typed refusal (`SharedWorkerLaunchException`) that carries the catalog error object, for example `KB_POOL_FULL`, `KB_WORKER_OWNED_ELSEWHERE`, `SHARED_WORKER_IDENTITY_MISMATCH`, `WORKER_STARTUP_FAILED` or `SHARED_WORKER_RECOVERY_FAILED`. The refusal reaches the client in one of two shapes:

- `genexus_kb` returns an `isError` tool result. Its payload is the catalog error object at the top level (`code`, `reason`, `retryable`, `dispatchState`, `hint` and, when retryable, `retryAfterMs`), with `error` set to the refusal message.
- Any other refusal that reaches the stdio request loop becomes a JSON-RPC error with `code` -32000, the refusal message as `message`, and the catalog error object in `error.data`. A notification gets no response.

```json
{"jsonrpc":"2.0","id":17,
 "error":{"code":-32000,"message":"<refusal message>",
   "data":{"code":"KB_POOL_FULL","reason":"low-memory","retryable":false,"dispatchState":"not-delivered","hint":"..."}}}
```

### Forced writes over a foreign lock

In the shared Worker child, a write sent with `force=true` over an unexpired lock held by another owner goes ahead. The Worker logs exactly one `WARN` line for it, which replaces the Isolated_Mode warning:

```text
[WritePipeline] force-override audit {"event":"forceOverride","overridingOwner":"lease-a","previousHolder":"lease-b","target":"CustomerProc","part":"Source","atUtc":"2026-03-04T10:00:00.0000000Z"}
```

The JSON is compact and has exactly these properties in this order. `overridingOwner` is the writing owner, `previousHolder` is the lock owner, `part` is the part as the request passed it, and `atUtc` is the round-trip (`o`) UTC timestamp. No line is written when there is no unexpired foreign lock. A corrupt or unreadable lock still returns `LockCheckFailed` and is never logged as an override.

## Migration rules (worker services)

1. **Construct via `McpResponse.Ok / .Err / .Partial / .Accepted`.** No inline `new JObject { ["status"] = "Success" }`. The helpers are the only source of envelope truth.
2. **Tool-specific payload goes under `result`.** Never spread payload at the top level. `{"part":"Source"}` becomes `{"result":{"part":"Source"}}`.
3. **No `details: string` for errors.** That information goes into `hint` (one-line fix) or, when long, into structured fields inside `error` (e.g., `error.verifyDiff`, `error.sdkSaveError`). Free-form prose is dead.
4. **`nextSteps[]` on every error path.** At least one concrete `{tool, args, why}` entry. If you genuinely can't suggest one, write a comment explaining why.
5. **Use a stable `code` enum.** Codes are `PascalCase`, machine-readable, never change wording across releases. Catalogue in `docs/error_codes.md` (to be created as codes accumulate).
6. **Make retry decisions explicit.** Transient failures should set `error.retryable:true` and usually `retryAfterMs`; ambiguous commit state must set `error.reconciliationRequired:true` and `retryable:false` until reconciliation completes.
7. **No legacy field aliases.** No `noChange:true`, no top-level `details`, no `action`, no `status:"Success"` etc. Clients targeting v2.8.0+ read only the canonical shape.

## Status code mapping (legacy → canon)

For reference while migrating; legacy values must not appear in v2.8.0 output.

| Legacy `status` | Canonical |
|---|---|
| `"Success"` / `"Ok"` | `"ok"` with `code:"<context-specific>"` (e.g. `WriteApplied`, `ReadOk`, `BuildOk`) |
| `"DryRun"` | `"ok"` with `code:"DryRun"` |
| `"NoChange"` | `"ok"` with `code:"NoChange"` |
| `"Skipped"` | `"ok"` with `code:"Skipped"` |
| `"Error"` | `"error"` with `error.code:"<specific>"` |
| `"Running"` (returned mid-flight) | `"accepted"` with `operationId` |

## Example: before / after

### Success
```json
// Before (legacy)
{"status":"Success","action":"Write","target":"X","part":"Source","details":"Visual XML updated and verified."}

// After (canonical)
{"status":"ok","code":"WriteApplied","target":"X",
 "result":{"part":"Source","details":"Visual XML updated and verified."}}
```

### NoChange
```json
// Before
{"status":"Success","action":"Write","target":"X","part":"Source","noChange":true,"noChangeReason":"literal_identical"}

// After
{"status":"ok","code":"NoChange","target":"X",
 "result":{"part":"Source","noChangeReason":"literal_identical"}}
```

### Error with nextSteps
```json
// Before
{"status":"Error","message":"Part 'WriteObject' not found.","target":"X","details":"Use a real part name.","availableParts":["Source","Variables","Rules"]}

// After
{"status":"error","target":"X",
 "error":{
   "code":"PartNotFound",
   "message":"Part 'WriteObject' not found.",
   "hint":"Pass a real part name. This object exposes Source, Variables, Rules.",
   "nextSteps":[
     {"tool":"genexus_read","args":{"name":"X"},"why":"Returns availableParts so the next write picks a valid one."}
   ]
 }}
```

### Async accept
```json
// Before
{"status":"Success","job_id":"abc123","operationId":"abc123"}

// After
{"status":"accepted","target":"X","operationId":"abc123","pollTarget":"op:abc123"}
```
