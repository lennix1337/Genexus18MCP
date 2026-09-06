# LLM Playbook: Best Use of AXI CLI + MCP

This guide is the practical reference for agents using `genexus-mcp` through shell (AXI CLI) and MCP (`tools/call`).

## Objective

- Minimize tokens and retries.
- Keep responses deterministic and machine-parsable.
- Use the right interface for each job.

## Interface Selection

Use AXI CLI when:
- You need environment/bootstrap checks (`home`, `status`, `doctor`).
- You need local installer/config introspection (`config show`, `tools list`).
- You want predictable shell-native exit codes (`0/1/2`) and strict `--fields`.

Use MCP when:
- You need KB operations (`genexus_query`, `genexus_read`, `genexus_edit`, etc.).
- You need long-running operation tracking (`genexus_lifecycle` + `op:<operationId>`).
- You are inside an MCP-native client loop.

## AXI CLI Contract (LLM-facing)

Entry points:
- `genexus-mcp home` or `genexus-mcp axi home`
- `genexus-mcp llm help`
- `genexus-mcp status`
- `genexus-mcp doctor --mcp-smoke`
- `genexus-mcp clients` (which AI agents are installed/registered; `clients add|remove --clients <csv>`)
- `genexus-mcp tools list`
- `genexus-mcp config show`

Rules:
- Parse `stdout` only.
- Expect envelope fields: `ok`, `error`, `help`, `meta`.
- Expect `meta.schemaVersion=axi-cli/1` and `meta.command`.
- Usage errors return `error.code=usage_error` and exit code `2`.
- Operational errors return exit code `1`.

Recommended flags:
- `--format json` for strict parsers.
- `--fields` to reduce payload size.
- `--full` only when needed.
- `--quiet` in automation contexts.

## Stdio startup failures

If an MCP client reports only `exit status 1` or `0xffffffff`, inspect the wrapper's
last-failure breadcrumb on Windows:

```powershell
Get-Content "$env:LOCALAPPDATA\GenexusMCP\logs\last-stdio-error.txt"
```

It contains the UTC timestamp, exit code, and a bounded stderr tail from the gateway
bootstrap. For Antigravity, `init` and `clients add --clients antigravity` use the
current package's gateway executable directly when available; if `genexus-mcp clients`
marks that path stale, re-register it with `npx genexus-mcp@latest clients add --clients antigravity`.
Do not infer an npx incompatibility or switch to a global npm install before reading
this file.

## MCP Contract (LLM-facing)

For `tools/call`, parse `result.content[0].text` as JSON.

Gateway AXI-like enrichments are additive (under `_meta` — underscore-prefixed per MCP convention):
- `_meta.schemaVersion = mcp-axi/2` (v2.0.0+)
- `_meta.tool = <tool-name>`
- Collection helpers when inferable: `returned`, `total`, `empty`, `hasMore`, `nextOffset`
- Truncation signal: `_meta.truncated=true` + contextual `help`
- Idempotent writes may include `noChange=true`
- v2.0.0 fields:
  - `_meta.idempotent=true` on idempotency-cache hits.
  - `_meta.batched=true` when the request used the `targets[]` plural form.
  - `_meta.dryRun=true` on `dryRun` preview responses; full preview shape: `{plan:{touchedObjects, xmlDiff, brokenRefs, warnings}}`.
  - `_meta.removedTools` advertised on `initialize` so agents can self-correct before a runtime `-32601`.

Optional shaping for `genexus_query` and `genexus_list_objects`:
- `fields`: array or CSV projection.
- `axiCompact`: defaults to `true`; pass `axiCompact: false` to receive the full payload.

## High-Value Query Patterns

Discovery:
- `genexus_query(query='@quick', limit=20)`
- `genexus_list_objects(parentPath='Module/Folder', limit=200, offset=0)`

Disambiguation:
- Prefer `parentPath` over `parent` when folder names repeat.

Efficient reads (1-roundtrip):
- `genexus_read(name='Obj')` — omitting `part` (or passing `part='all'`/`'360'`) returns the complete object in 1 call (rules with parm, source/events, variables, structure, called signatures), eliminating 2–3 exploration turns.
- `genexus_read(name='Obj', part='Source', offset=1, limit=200)` — for targeted single-part reading.
- For many files, prefer `genexus_read(targets=['A','B','C'], part='Source')` (plural form).
- Follow `next_legal_actions` on tool responses: the server delivers pre-populated next tool calls with arguments (read → edit → compile) to minimize cross-turn reasoning.

Safe edits (v2.0.0):
- Preview before applying: any `genexus_edit` call accepts `dryRun: true` and returns `{plan: {touchedObjects, xmlDiff, brokenRefs, warnings}}` without mutation.
- Three edit modes:
  - `mode='xml'` (default) — full XML replacement.
  - `mode='ops'` — typed semantic ops, e.g. `ops=[{op:'set_attribute', name:'Phone', type:'Character(20)'}]`. Catalog: `set_attribute`, `add_attribute`, `remove_attribute`, `add_rule`, `remove_rule`, `set_property`.
  - `mode='patch'` — JSON-Patch RFC 6902 array, e.g. `patch=[{op:'replace', path:'/description', value:'new'}]`. Legacy string-form text patch (`mode='patch'` with string `patch`) still works for backward compatibility.
- Multi-object coordinated changes: `genexus_edit(targets=[{name:'A', mode:'ops', ops:[...]}, {name:'B', mode:'xml', content:'...'}])`. Mutually exclusive with singular `name`.
- Safe retries: pass `idempotencyKey: '<token>'` (charset `[A-Za-z0-9_-]`, 1–128 chars). Same key + same payload = cached result; same key + different payload = `idempotency_conflict` error. `dryRun` bypasses the cache.

Removed in v2.0.0:
- `genexus_batch_read` → use `genexus_read` with `targets[]`.
- `genexus_batch_edit` → use `genexus_edit` with `targets[]`.
- `genexus_edit` `changes` arg → use `targets[]`.
- Calling a removed tool returns `-32601` with `error.data.replacedBy` and `error.data.argHint` for self-correction. `initialize` advertises `_meta.removedTools` upfront.

## Timeout and Long-Running Operations

If a tool exceeds gateway budget, the response is still machine-actionable:
- `result.isError=true`
- payload `status='Running'`
- `operationId` and `correlationId`
- `help` with explicit lifecycle follow-up

Follow-up flow:
1. Call `genexus_lifecycle(action='status', target='op:<operationId>')`.
2. When complete, call `genexus_lifecycle(action='result', target='op:<operationId>')`.

Do not treat timeout as hard failure when `operationId` is present.

For a complete incremental build of the selected Knowledge Base, use
`genexus_lifecycle(action='build_all')` without `target`. It runs the native
`BuildAll` task when available, reports completion evidence, and returns
`ReorgRequired` instead of applying a schema reorganization implicitly. Use
`action='build'` for a directed target and `action='rebuild'` for the forced
Rebuild All contract.

## Pagination and Token Budgeting

Read/list defaults are intentionally broad for humans, but not for LLMs.

Always set:
- `limit` and `offset` for list/search.
- `offset` and `limit` for `genexus_read`.

Prefer:
- Multiple narrow calls over one large payload.
- `genexus_query` and `genexus_list_objects` return a compact projection by default (`name`, `type`, `path`[, `parentPath`]). Pass `axiCompact: false` only when you need the full payload (description, metadata, etc.), or use `fields` for a custom subset.

## Error Handling Policy for Agents

AXI CLI:
- Branch by process exit code first.
- Then parse `error.code` and `error.message`.

MCP:
- If JSON-RPC `error` exists, treat as transport/protocol failure.
- If `result.isError=true`, treat as domain/tool failure or running operation.
- When payload includes `help`, follow the first actionable step.

## Minimal End-to-End Recipes

Bootstrap + health:
1. `genexus-mcp home --format json`
2. `genexus-mcp doctor --mcp-smoke --format json`

Find + inspect + patch:
1. `genexus_query(query='name', limit=20)`
2. `genexus_read(name='ObjectName', part='Source', offset=1, limit=200)`
3. `genexus_edit(... dryRun=true ...)`
4. `genexus_edit(... dryRun=false ...)`

Large list with deterministic follow-up:
1. `genexus_list_objects(parentPath='Root Module', limit=200, offset=0)` — compact projection is on by default; pass `axiCompact: false` if you need the full payload.
2. If `hasMore=true`, call again with `offset=nextOffset`.
