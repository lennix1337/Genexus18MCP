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
