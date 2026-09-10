# GeneXus MCP Protocol Guide

> Required repo skills before editing KB logic:
>
> 1. [GeneXus MCP Mastery](./.gemini/skills/genexus-mastery/SKILL.md)
> 2. [GeneXus 18 Guidelines](./.gemini/skills/genexus18-guidelines/SKILL.md)

This repository is MCP-first. The official transport is MCP over stdio or HTTP at `/mcp`. The old `/api/command` endpoint is no longer part of the gateway surface.

## Correct MCP flow

1. Initialize the session with `initialize`.
2. Discover the live surface with `tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`, and `completion/complete` when needed.
3. Execute work with `tools/call`, `resources/read`, and `prompts/get`.
4. For HTTP MCP, send `MCP-Protocol-Version: 2025-06-18` and reuse the returned `MCP-Session-Id`.

## Recommended tool usage

- `genexus_query`: find objects, references, signatures, and dependency entry points. Supports optional `typeFilter` and `domainFilter` for server-side narrowing.
- `genexus_read`: read object parts with pagination. Pass singular `name` for one object or `targets[]` for coordinated multi-object reads (mutually exclusive). For MCP clients, keep reads paginated; the server intentionally returns a source-first first page when no explicit `offset`/`limit` is provided. For XML metadata parts such as `Layout`, `WebForm`, and `PatternInstance`, the gateway applies a larger metadata budget to avoid truncating the editable XML.
- `genexus_edit`: apply focused edits to a part or replace content through the MCP write path. Three modes:
  - `mode: 'xml'` (default): full XML replacement.
  - `mode: 'ops'`: typed semantic op catalog (`set_attribute`, `add_attribute`, `remove_attribute`, `add_rule`, `remove_rule`, `set_property`).
  - `mode: 'patch'`: JSON-Patch (RFC 6902) array over the canonical JSON object representation; legacy string-form text patch is also accepted for backward compatibility.
  Pass `targets[]` (array of edit-request objects) for atomic multi-object edits — mutually exclusive with singular `name`.
  `PatternInstance` targets the authoritative WorkWithPlus instance when the requested object is a WebPanel managed by WorkWithPlus.
- All write tools (`genexus_edit`, `genexus_create_object`, `genexus_refactor`, `genexus_forge`, `genexus_import_object`) accept:
  - `dryRun: true` — returns a preview envelope `{_meta:{dryRun,schemaVersion}, plan:{touchedObjects, xmlDiff, brokenRefs, warnings}}` without mutating the KB.
  - `idempotencyKey: '<token>'` (`[A-Za-z0-9_-]{1,128}`) — deduplicates retries; concurrent calls with the same key are coalesced; cache is per-KB, sliding TTL 15 min default. `dryRun` bypasses the cache.
- `genexus_inspect`: get structured conversion or object context.
- `genexus_analyze`: one tool with `mode`: `linter | navigation | hierarchy | impact | data_context | ui_context | pattern_metadata | summary | explain`. `explain` is compatibility-only: it preserves the legacy envelope and returns `NotImplemented`; use `summary` for a concise object overview, `context` for 360° task context, or `genexus_read` for source. The standalone `genexus_summarize` and `genexus_explain_code` were replaced in v2.3.0.
- `genexus_lifecycle`: build, validate, index, and KB lifecycle operations.
- `genexus_sql`: `action=ddl` for Transaction/Table DDL or `action=navigation` for the SQL of a procedure's For Each (replaces `genexus_get_sql` and `genexus_get_sql_for_navigation` in v2.3.0).
- `genexus_kb`: multi-KB pool management (`action`: `list | open | close | set_default`). Replaces `genexus_open_kb` in v2.3.0.
- `genexus_create_object`: create new KB objects.
- `genexus_refactor`: supported rename and extraction refactors.
- `genexus_add_variable`: add variables through the worker contract.
- `genexus_format`: format source through the worker formatter.
- `genexus_properties`: get or set object properties.
- `genexus_asset`: find, read, and write KB assets such as `.xlsx` templates. Reads are metadata-first; request `includeContent=true` only for files small enough to carry as Base64 safely.
- `genexus_history`: list, read, save, and restore object history.
- `genexus_structure`: read or update logical and visual structure.
- `genexus_doc`: access documentation, health, and visualization flows.

## Multi-KB (v2.3.0+)

The gateway can hold up to `Server.MaxOpenKbs` (default 3) KBs open simultaneously, each in its own Worker process. Calls to **different** KBs execute in parallel — no serialization between them. Calls to the **same** KB remain serialized by the SDK's STA constraint.

- Every non-meta tool accepts an optional `kb` argument: an alias from `config.Environment.KBs[]` or an absolute path (registered ad-hoc on first use).
- With 0 KBs open and a `DefaultKb` configured, that KB is acquired lazily. With exactly 1 KB open the `kb` arg is optional; with 2+ open it is required (server returns `KB_AMBIGUOUS` with the open list).
- `genexus_kb action=list` returns each open KB's PID, working-set memory, and idle seconds — useful to pick a candidate before triggering eviction via `action=close`.
- Pool full (`MaxOpenKbs` reached, no idle Worker to evict): server returns `KB_POOL_FULL`. Close one explicitly or wait for idle eviction (`WorkerIdleTimeoutMinutes`).
- Legacy `Environment.KBPath` configs auto-migrate to `KBs[]` + `DefaultKb` on load; existing single-KB workflows keep working with no client change.

## Resource-first patterns

Prefer resources when the data is naturally browsable or cacheable:

- `genexus://objects/{name}/part/{part}`
- `genexus://objects/{name}/variables`
- `genexus://objects/{name}/navigation`
- `genexus://objects/{name}/summary`
- `genexus://objects/{name}/indexes`
- `genexus://objects/{name}/logic-structure`
- `genexus://attributes/{name}`
- `genexus://kb/index-status`
- `genexus://kb/health`

## Codebase discovery with ripwire (on PATH as `ripwire`)

Reach for it BEFORE blind grep and whole-file reads:
- Orient on a task: `ripwire <dir> --for="<task in words>"` — ranked, quality-annotated signatures.
- Callers & blast radius: `ripwire <dir> --callers=SYM` and `--impact=SYM` (transitive callers before modifying contracts).
- Pre-edit contract check: `ripwire <dir> --edit-check=SYM`.
- Diff review: `ripwire . --pr-context` (enforced by `scripts/pr-preflight.ps1`).

`ripwire` is an optional architectural quality gate, not a runtime dependency.
The PR preflight reports it as skipped when unavailable; use
`scripts/pr-preflight.ps1 -RequireRipwire` to make it mandatory for a local run.

## Operating rules

- Do not design new features around non-MCP transport contracts.
- Do not use retired tool names. Removed (with their replacements):
  - `genexus_patch`, `genexus_read_source`, `genexus_write_object` (pre-v2.0.0).
  - `genexus_batch_read` → `genexus_read` with `targets[]` (v2.0.0).
  - `genexus_batch_edit` → `genexus_edit` with `targets[]` (v2.0.0).
  - `genexus_open_kb` → `genexus_kb action=open` (v2.3.0).
  - `genexus_get_sql` → `genexus_sql action=ddl` (v2.3.0).
  - `genexus_get_sql_for_navigation` → `genexus_sql action=navigation` (v2.3.0).
  - `genexus_summarize` → `genexus_analyze mode=summary` (v2.3.0).
  - `genexus_explain_code` → `genexus_analyze mode=explain` (v2.3.0; compatibility-only `NotImplemented` envelope). Use `genexus_analyze mode=summary|context` or `genexus_read` for supported analysis.
  Calls to removed tools return JSON-RPC `-32601` with `error.data.replacedBy` and `error.data.argHint`; `initialize` advertises `_meta.removedTools` for proactive detection.
- Do not pass the legacy `changes` argument to `genexus_edit` — it was removed in v2.0.0; use `targets[]` instead.
- Read first, then edit. Use paginated reads for large objects.
- Prefer MCP discovery over hardcoded assumptions about available tools or resources.
- After C# changes, run `.\build.ps1`. For current validation commands, follow the README.
