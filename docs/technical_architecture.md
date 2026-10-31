# GeneXus MCP Technical Architecture

This document describes the current runtime architecture. It is not a historical roadmap.

## Runtime model

The system uses a dual-process design:

- `GxMcp.Gateway` on .NET 10 owns the MCP protocol surface.
- `GxMcp.Worker` on .NET Framework 4.8 owns GeneXus SDK execution.

```mermaid
graph LR
    A[Client] -->|stdio or HTTP /mcp| B[Gateway]
    B -->|worker RPC| C[Worker]
    C -->|Artech SDK| D[Knowledge Base]
```

## Transport

### Official transports

- stdio MCP
- HTTP MCP at `/mcp`

### HTTP behavior

- Legacy clients use `initialize` and `MCP-Session-Id` with the negotiated
  `2025-11-25` contract.
- Modern clients may use `2026-07-28` per-request metadata and
  `server/discover`; they do not share a server-side KB selection.
- Session-aware HTTP uses `MCP-Session-Id`; sessionless requests must carry an
  explicit `kb` or use the persisted fallback.
- Loopback `/mcp` requests accept only `localhost`, loopback literals, or the
  configured loopback bind in `Host`; this closes the browser DNS-rebinding
  path while preserving token-protected non-loopback binds.
- `GET /mcp` is used for SSE notifications.
- `DELETE /mcp` closes the session.

Modern `2026-07-28` clients can open `subscriptions/listen` with a POST
request. The response is a bounded SSE stream whose first message is
`notifications/subscriptions/acknowledged`; the client opts into tool/resource
list changes and exact resource URIs in `params.notifications`. Each stream has
its own random subscription handle, a bounded queue, a keepalive, and cleanup on
disconnect. Legacy `resources/subscribe` remains session-scoped and is not
accepted on the sessionless path. Resource notifications retain the legacy `uri`
for compatibility and add `kbAlias`, `cacheRevision`, and a KB-qualified
`resourceUri`; clients that have multiple KBs open can subscribe to the scoped
URI without conflating same-named objects, and `resources/read` resolves that
scoped URI back to the explicit KB before dispatching to the Worker.

The `io.modelcontextprotocol/tasks` extension is advertised by
`server/discover`. Modern task requests must opt in through
`params._meta.io.modelcontextprotocol/clientCapabilities.extensions`; legacy
session clients remain backward compatible. On sessionless HTTP, task callers
must also send a stable `Mcp-Client-Id` header; requests without that explicit
scope fail closed before looking up a task. Task handles (`tasks/get`,
`tasks/update`, and `tasks/cancel`) are scoped to the transport identity that
created the background job. Asynchronous `tools/call` responses use
`resultType:"task"`; polling uses `resultType:"complete"`, while update and
cancel return empty acknowledgements. Modern task status values map to the MCP
extension states, and terminal tasks reject cancellation. A cancellation
notification still matches the exact JSON-RPC id token, so numeric `1` and
string `"1"` remain separate requests.

Worker progress frames use a private Gateway operation id for correlation, then
restore the original client `progressToken` before delivery. They are routed
only to the originating stdio or legacy HTTP session; the sessionless modern
POST path does not broadcast unsolicited progress between clients. A modern
`notifications/cancelled` POST is accepted as a no-op because that transport
has no prior stream identity; clients cancel by closing the response stream.

### KB lifecycle build modes

`genexus_lifecycle action=build` remains the directed or incremental build
contract, while `action=rebuild` remains the forced Rebuild All contract; like
`build_all` it is KB-global and rejects `target` with `TargetNotSupported`.
`action=build_all` is an explicit KB-global incremental Build All: it omits
`target`, invokes the SDK `BuildAll` task in-process when the installed
GeneXus version exposes it, and otherwise runs an equivalent temporary MSBuild
project with `ForceRebuild=false`. Both paths set `FailIfReorg=true` and
`DoNotExecuteReorg=true`; a required schema reorganization terminates as
`ReorgRequired` with a retry hint instead of changing the database implicitly.
Terminal results include `buildMode`, `kbOpened`, `buildAllDone`,
`reorgRequired`, `msBuildExitCode`, and `fullLogPath`, and a zero exit code is
not accepted as completion evidence by itself.

Both SDK Build All paths permit the SDK's configured pre/post-build commands,
including their normal conditions and external effects. The in-process runner
refuses to start during SDK write-event isolation and restores the global
broker's previous suspension state even if the task throws. Other operations'
write-event isolation is unchanged.

The in-process Rebuild All (`rebuild`) and Build One (targeted `build`) tasks
still run with that broker suspended, which also keeps the SDK's configured
pre/post-build commands from being dispatched; `build_all` is the only action
that enables it explicitly. `ArtechTask.EventsSuspended` writes the same
Worker-wide switch and no build task resets it, so the runner also restores the
prior state after each of these tasks, whatever their outcome: a build no longer
decides whether later SDK callbacks and isolated Events saves run in that
Worker. Builds that call the build services directly (explicit `Type:Name` or
GUID targets, batch and compile-only paths) never touch the switch; they now
always see the state the Worker started with, not one an earlier Build One or
Rebuild All left behind. Other process-global state a task sets, such as the
build command factory `IdeWebBuildAndDeploy` installs, is not restored.

`buildEvents.policy="sdk"` describes the event policy, not proof that a command
ran. `buildEvents.executionVerified=null` explicitly leaves that outcome
unverified, even when `buildAllDone=true`; compact responses preserve this
metadata. Inspect `fullLogPath` for SDK command output and independently verify
the intended effects. A missing output line alone does not prove non-execution.
For a controlled comparison of IDE, MSBuild and MCP, use the same disposable KB,
environment, SDK and harmless marker commands, with Cloud disabled. Check both
marker files and command output; never rerun an uncertain external command just
to obtain a log line.

### Reorganization guard in lifecycle builds

Every build path that can reach the SDK's database step asks for a refusal
instead of a silent reorganization: `FailIfReorg`, which already contains the
`DoNotExecuteReorg` bit (`BuildOptions.FailIfReorg` is `0xC00000`;
`DoNotExecuteReorg` is `0x400000`). The paths are `BuildOne` (the worker's
`NoReorgBuildCommandFactory` sets the flags when the SDK builds the command),
the result-bearing `Build` overload used for explicit `Type:Name` or GUID
targets and for multi-target builds without callees, the `BuildAll` and
`IdeWebBuildAndDeploy` tasks, and the external MSBuild project.

The SDK chain for a targeted refusal, as decompiled from GeneXus 18 U16
(18.0.16):

- `BuildOne.Execute` calls `GenexusBLServices.Build.Build`, which creates its
  command through `BuildCommandFactory.Factory.Create`. `CheckReorgNeeded` adds
  `ReorganizeCommand(options)` when the model needs a reorganization.
- `ReorganizeCommand.Do` maps the flags to `ReorganizeOptions` and calls
  `RunBase.Reorganize`. With `FailIfReorg` set, that method raises
  `Messages.FailIfReorg` before the `EXECUTE_CMD` step, which is the database
  change. The refusal is written as a build error line.
- `IdeWebBuildAndDeploy.Execute` installs `WebBuildCommandFactory`, whose
  `GetReorganizeCommand` returns `WebReorganizeCommand`. Its
  `WaitUserConfirmation` reads the console for up to four minutes before the
  refusal, even with `FailIfReorg`, and the SDK does not restore the previous
  factory afterwards, so a targeted `BuildOne` later in the same worker process
  uses the web command too. The guard does not cover this wait.

The worker classifies the refusal as `ReorgRequired` by matching the sentence
the running SDK resolves for `Messages.FailIfReorg` in the worker's UI culture.
An English-form pattern is the fallback when that resource cannot be read.
Diagnostics for a missing or unapplied `FailIfReorg` or `DoNotExecuteReorg`
property are build errors and never match that classifier.

Across the installed SDKs, `RunBase.Reorganize` and `ReorganizeCommand.Do` are
the same in GeneXus 18 U11, U12 and U16, but `BuildOne` is not: U16 resolves the
object with `ObjectsResolverHelper` and returns `false` for an unresolved name,
while U11 and U12 use `ObjectNameHelper` and return `true`. To check a method,
decompile the SDK assembly from the GeneXus installation directory with
`ilspycmd -t <type> <dll>`.

Do not set `DoNotGenerateReorg` to skip the guard: `ReorganizeCommand.Do` then
skips the reorganization and reports success.

## Discovery-first surface

Clients are expected to discover capabilities dynamically:

- `tools/list`
- `resources/list`
- `resources/templates/list`
- `prompts/list`
- `completion/complete`

The extension uses this MCP discovery flow directly.

## Worker responsibilities

- Open and manage the active KB
- Read and write object parts
- Execute analysis, refactor, formatting, lifecycle, history, structure, and property operations
- Isolate GeneXus runtime constraints from the gateway process

All SDK-touching watcher actions are admitted through the existing STA bridge
with a bounded `SdkExecutor`. Reentrant calls execute inline on the owner thread;
calls that arrive while the admission budget is full receive a typed busy result.
Pure response shaping remains outside the SDK thread.

### Text patch responsibilities

The `genexus_edit mode=patch` implementation keeps its external contract in
`PatchService`, but separates deterministic work from SDK side effects:

- `PatchTextEditor` owns pure context matching, Replace, InsertAfter, fuzzy
  matching, diagnostics, and edit-distance calculations.
- `PatchService` orchestrates snapshots, optimistic concurrency, the single
  write attempt, forced post-save reads, cache invalidation, and rollback.
- `PatchPersistenceReceipt` builds the stable persistence evidence (`saved`,
  `verified`, hashes, old-context presence, and rollback status).
- `TextPersistenceVerifier` owns exact, normalized, and semantic equivalence.
- `CommentOnlyPatch` classifies line/block comment replacements and counts the
  previous statement only when it remains active outside comments or strings.

For Source/Rules, `exact` compares every logical character while treating CRLF
and LF as equivalent SDK renderings. This prevents a persisted comment from
being rolled back solely because U16 returned a different line-ending style.
Comment-only writes require `baseVersion`; a divergent forced re-read returns
`CommentOnlyWriteNotPersisted`, and an explicitly requested rollback restores
and verifies the pre-write snapshot.

These boundaries are internal. Refactoring them must preserve tool arguments,
write count, transaction behavior, and the prohibition on implicit KB lifecycle
actions. New typed failure codes may be added only when they replace an
ambiguous or false-success result without changing the write semantics.

For textual patches, `saved=true` is reserved for a complete post-save read that
also verifies the requested content; `saveAttempted` reports the SDK write
attempt separately. An explicit rollback is fenced with the version observed
after the failed write. If that version is unavailable, rollback is refused to
avoid overwriting a concurrent edit. PatternInstance verification refreshes the
resolved WorkWithPlus child as well as the requested parent, so a fresh parent
read cannot silently reuse a cached pattern object.

## Gateway responsibilities

- MCP routing
- HTTP session lifecycle
- Worker lifecycle and restart boundaries
- Dynamic tool publication from `tool_definitions.json`
- Resource, prompt, and completion exposure
- `genexus://kb/capabilities` is a stable resource URI whose contents are
  fetched from the explicit, read-only SDK capability probe for the selected
  KB; tools/list remains deterministic and never hides tools per session.
- Legacy HTTP sessions may subscribe to a resource URI. Subscription state is
  private to the creating session and `notifications/resources/updated` is
  filtered before entering its SSE queue; the sessionless 2026 transport does
  not accept the legacy subscription methods without a negotiated stream.
- Task-handle projection over the session-scoped background job registry
- Additive response normalization for `tools/call` payloads (`mcp-axi/2` metadata under `_meta` and lightweight aggregates)
- Canonical operation policy for cache/idempotency decisions: published
  actions and legacy aliases are normalized through `ToolIdentity` and
  `OperationClassifier`; unclassified operations remain fail-closed and use
  the compatibility path until their contract is added.

Gateway latency summaries are bounded and aggregate-only. Each completed or
timed-out call records the canonical tool, result class, end-to-end duration,
queue wait, SDK dispatch time, gateway transformation/serialization time, cache
outcome, and response byte count; p50/p95 values are retained in memory and no
KB content, object names, paths, or request IDs enter the metric labels. The
Gateway also exposes the opt-in `Genexus.Mcp.Gateway` `ActivitySource` and
`Meter` (`genexus_mcp_tool_calls` and `genexus_mcp_tool_duration_ms`); without
an external listener they produce no spans or network traffic.

Semantic reads use a bounded cache keyed by the normalized KB, canonicalized
arguments, and (when available) the active model/environment identity. Each KB
has an independent generation. A confirmed or uncertain mutation advances only
that generation and evicts its entries; a read that started in an older
generation cannot repopulate it after the mutation. Entry freshness uses an
absolute TTL independent from LRU access time, so repeated hits cannot keep a
stale answer alive forever. Preview/validate-only operations do not invalidate
the generation.

The `genexus_analyze` `mode=context` path applies a bounded context bundle after
SDK resolution. The bundle hashes the complete result, keeps JSON item
boundaries intact, pages large collections with a continuation cursor, and
turns oversized object parts into `genexus://objects/{name}/part/{part}` read
references. A partial page reports its omitted sections and budget so an agent
can continue deliberately instead of guessing what was cut.

Direct caller/callee lookups use a derived adjacency snapshot in the Worker.
The snapshot combines SDK `Calls`/`CalledBy` edges with the indexed textual
fallback once per `SearchIndex.GraphRevision`; subsequent graph reads only copy
the requested node's sorted degree list. Mutations and edge enrichment advance
the revision, while the revision itself remains in-memory derived state and is
never written to the on-disk index.

Rename fallbacks use the same graph signals for preview, then rewrite only
identifier tokens in source parts. The tokenizer excludes line/block comments,
quoted strings, and larger identifiers, and previews report the graph revision
plus source/line/column evidence. A native SDK refactor remains preferred when a
future capability probe proves its semantics.

## Tool call response contract (gateway-normalized)

For MCP `tools/call`, the gateway returns standard MCP `content[].text`, but normalizes JSON payloads with additive metadata:

- `_meta.schemaVersion = "mcp-axi/2"` (v2.0.0; field is underscore-prefixed per MCP convention)
- `_meta.tool = <tool-name>`
- collection helpers when inferable: `returned`, `total`, `empty`, `hasMore`, `nextOffset`
- truncation hints: `_meta.truncated=true` plus actionable `help` message
- v2.0.0 fields: `_meta.idempotent` (cache hit), `_meta.batched` (`targets[]`), `_meta.dryRun` (preview), `_meta.removedTools` (advertised on `initialize`)
- idempotent marker: `noChange=true` when the worker reports successful no-op

Compatibility rules:

- Existing worker fields are preserved.
- Enrichment is additive and does not change no-args launcher behavior.
- Clients that ignore new fields remain compatible.

## Design constraints

- New features should target MCP tools, resources, prompts, or completion endpoints.
- New extension flows must only target MCP contracts.
- Resource reads should be preferred for stable browsable context.
- Large object reads should be paginated with `genexus_read` or coordinated with `genexus_read(targets=[...])` (plural form, v2.0.0+; the legacy `genexus_batch_read` was removed).
