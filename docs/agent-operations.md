# Agent operations reference

Detailed agent guidance moved out of [`AGENTS.md`](../AGENTS.md) so the file every
agent loads stays small (Antigravity `agy` truncates project instructions above
24,000 bytes). `AGENTS.md` keeps the rules and points here for the details;
read the section that matches the task.

## KB and harness contracts

- KB resolution order is explicit `kb` → MCP-session selection (`genexus_kb action=select` / `set_session_default`) → strict/legacy resolution policy.
- In strict mode (`ResolutionPolicy: "strict"`, default), persisted `DefaultKb`/`ActiveKb` does NOT auto-seed sessions. When 1 KB is open, it resolves as `single-open` only if it does not conflict with a configured default (mismatches yield `KB_CONTEXT_REQUIRED` with `DefaultConflict`). When 0 KBs are open, declared catalog entries are never auto-opened (`KB_CONTEXT_REQUIRED` or `KB_AMBIGUOUS`).
- In legacy mode (`ResolutionPolicy: "legacy"`), the legacy fallback chain (`config-default` → `single-open` → `declared-first`) remains active.
- `genexus_kb action=open` starts/registers a Worker; `action=select` or `set_session_default` sets an in-memory session selection without mutating config files; `action=set_persistent_default` mutates the startup fallback on disk and returns `persistedTo`. `action=set_default` remains as a legacy persistent operation returning `persistedTo`.
- `genexus_whoami` and `genexus_kb action=list` expose alias auditability state:
  `sessionSelection`, `selectionSource` (`session-select`, `single-open`, `config-default`, `declared-first`, `explicit-arg`, `none`), `selectionState` (`valid`, `absent`, `invalid`, `conflicting`), `startupDefault`, `resolutionPolicy`, `config.resolvedFrom`, open, known, and declared KBs.
  KB-bound results carry `kbAlias` in-band and in MCP `_meta`.
- `genexus-mcp init` configures detected clients with neutral configurations by default (no hardcoded KB path). OpenCode must preserve both
  `mcp.<name>` and `mcp.servers.<name>` layouts and unrelated servers.
- Sessionless HTTP clients must use an explicit `kb` or persisted fallback; `select` on sessionless HTTP returns `KB_SESSION_UNAVAILABLE`. Do
  not introduce shared server-side selection between independent clients.
- `genexus_whoami` and `genexus_doctor` must preserve actionable Worker diagnostics: effective sharing mode, physical identity, pipe/host/Worker identity, generation, attachment/connection state, startup/failure detail, and the likely failing stage. Keep credentials, tokens, connection strings, and other sensitive values redacted.

## Live Knowledge Bases for validation

Disposable test Knowledge Bases live under `C:\KBs`. Any directory whose name
contains `teste` is a scratch KB that may be written to, retargeted and left in
a modified state — it is not user data, and a lane that needs to reproduce a
write, a rollback or a persistence failure should use one rather than asking
for a KB. Full end-to-end validation with the real SDK on these available test
KBs is standing-authorized; choose a compatible test KB and run the required
live flow without requesting permission again. A pending live validation is
work to complete, not a permission blocker. This authorization covers test-KB
mutations needed by that flow, not production KBs, Cloud, release or deployment;
keep Cloud off and preserve the existing isolation and provenance checks.

To build a test KB with the SDK outside the MCP (SDK oracle, refreshed
generated code, or a Worker build stopped at GeneXus Cloud), use
`scripts/sdk-local-build.ps1`: SDK `SpecifyOneOnly`/`GenerateOnly` under 32-bit
MSBuild, then `dotnet msbuild -t:Build;Publish` on the generated projects. Never
hand-write `BuildOne`/`BuildAll` projects for this: the Trial install forces
Deploy-to-Cloud and they can reorganize the database. See
[`docs/sdk-local-msbuild.md`](sdk-local-msbuild.md).

The paths below are this maintainer's machine, not a requirement: the portable
rule is the `teste` name. The release preflight resolves its live KB as
`GXMCP_TEST_KB` first, then (single-major mode only) autodetects
`C:\KBs\KBTeste` for GeneXus 18, `C:\KBs\KBTeste17` for 17 and
`C:\KBs\KBTeste<major>` otherwise (`Get-GxMcpLocalLiveKbPath` in
`scripts/release-contract.ps1`). With no KB found the live gate is skipped,
unless `-RequireLive` or Build All evidence is required, which make it fail. See
`docs/environment_variables.md`.

| Path | GeneXus major | Notes |
| :--- | :--- | :--- |
| `C:\KBs\KBTeste` | 18 | Largest (~1,800 files); the default target for SDK-level read/write lanes |
| `C:\KBs\KBTeste17` | 17 | ~66 files; also carries `.gx` and `.gx_mirror` |
| `C:\KBs\KBTeste16` | 16 | Smallest (~10 files); fast for smoke-level checks |
| `C:\KBs\KBTesteProbe2` | 18 | ~122 files; used by probe/conversion work |

Two properties must not be conflated, because they decide which tools can be
validated where:

- **These are native Knowledge Bases.** `GXLOCK`, `DATA*` and `.gx` are
  present, so object sources live in the Knowledge Base database and are read
  through the SDK. `genexus_read`, `genexus_edit`, `genexus_analyze` and the
  `mode=ops` authoring paths work against them normally.
- **`genexus_wire` cannot analyse them, except `pack_task`.** GxWire indexes
  files on disk, so on a native KB it walks generated output and infers a graph
  from whatever text is there. Validating a gxwire change needs a *text tree
  export* of a KB (a directory of `.txt`/`.trn`/`.wp`/`.xml` object files), not a
  native KB directory. This is the defect filed as
  [#499](https://github.com/lennix1337/Genexus18MCP/issues/499), and it is why
  gxwire parser work cannot be confirmed on these paths alone. `pack_task` on
  the open native KB does not use gxwire at all: the Gateway falls through to
  the Worker (`WirePackTaskService`), which resolves `targets[]` and the
  identifiers named in `task` through the SDK-backed index, so it *can* be
  validated live on these KBs, read-only.

A live KB opens on demand:

```text
genexus_kb action=open path=<KB root>   then   action=select to bind the session
```

## Runtime iteration

The gateway serves MCP over stdio by default (`McpStdio: true`). Streamable HTTP is bound at `http://<BindAddress>:<HttpPort>/mcp` only with a shared gateway (`GatewayMode: http-shared` with `McpStdio: false`, or `Server.SharedGateway: true` / `GXMCP_SHARED_GATEWAY=1`). With `HttpPort: 5000` and `McpStdio: true` alone, nothing listens on 5000; the Gateway logs this at startup. Job output is read with `genexus_lifecycle action=result target=op:<id>`, not over HTTP.
Use the handshake and scratch-KB procedure in `docs/agent_playbook.md` or
`docs/mcp_debugging_guide.md` when validating SDK behavior. After Worker edits,
hot-swap with:

```text
genexus_worker_reload mode=hard sourceDir=<repoRoot>\src\GxMcp.Worker\bin\Debug
```

- `GxMcp.Gateway.exe` is a long-running stdio JSON-RPC server and does not accept interactive CLI flags such as `--help`. Running it directly without redirected stdio or via an unsupported command hangs waiting on stdin. Use `genexus-mcp` CLI commands or test harness entry points (`LiveGatewayHarness`, `scripts/test-live.ps1`) to interact with the Gateway.
If the next call reports a stale pipe or crashed Worker, reconnect `/mcp` once.
For a version smoke, call `genexus_whoami` and verify
`geneXus.versionMatches=true`, `matchedMajor`, and `supportedMajors`; the
legacy `supportedMajor` field remains the catalog-primary compatibility alias.

## MCP update and harness synchronization

Before creating or proposing any new script for build, installation, upgrade, or agent registration, inspect existing tooling:
- `install.ps1` (local checkout orchestrator)
- `build.ps1` (compiler & artifact packager)
- `scripts/install.ps1` (fixed-path release installer)
- `cli/run.js` & `cli/lib/config.js` (client registry & discovery)
- `cli/lib/update-check.js` (update planning)
- `docs/llm_cli_mcp_playbook.md` (authoritative CLI playbook)

### Decision matrix

| Scenario | Recommended flow | Notes |
|---|---|---|
| Updated local checkout | `.\install.ps1` | Updates `config.json`, runs `build.ps1`, and registers every writable client against the checkout gateway: it sets `GENEXUS_MCP_GATEWAY_EXE` to `publish\GxMcp.Gateway.exe` and runs `clients add --all-clients` (not `init --write-clients`). Parameters: `-GeneXusPath`, `-SkipClientConfig` (there is no `-KBPath`); the neutral config it writes defines no `Environment.KBPath`. |
| Compile local checkout only | `.\build.ps1` | Regenerates `publish/` without modifying `config.json` or client registrations. |
| Fixed-path release install / upgrade | `powershell -File scripts/install.ps1` | Downloads release `publish.zip` into fixed location. Parameters: `-Kb`, `-Gx`. |
| npx / npm global upgrade | Run `genexus-mcp update` plan | Follow returned guidance and fully restart the AI client. |
| Local checkout: stale launcher | From the repo root: `$env:GENEXUS_MCP_GATEWAY_EXE='<repoRoot>\publish\GxMcp.Gateway.exe'; node cli\run.js clients add --clients <id>` (or re-run `.\install.ps1`) | Do not reach for `npx @latest clients add` here: it rewrites the client to the npm-cache launcher and silently moves the harness off the checkout gateway. |
| Local checkout: post-sync validation | From the repo root: `node cli\run.js clients --format json`<br>`node cli\run.js doctor --mcp-smoke --format json` (same env as `install.ps1`) | Validation only — neither command rewrites a launcher. A client pointing at a *different* existing gateway is reported as `launcherPathDrift` (informational, `commandStale: false`), so a checkout registration read back by an `npx` CLI is no longer a false stale; `doctor`'s `client_config_sync` still warns that the exe is not the packaged one. |
| Distributed package: stale Antigravity launcher | `npx genexus-mcp clients add --clients antigravity` | Re-points the launcher to current direct gateway or package cache. For a local checkout use the checkout branch above instead. |
| Distributed package: post-sync validation | `npx genexus-mcp clients --format json`<br>`npx genexus-mcp doctor --mcp-smoke --format json` | Validates registration, gateway HTTP loopback, and live MCP protocol smoke. |
| Release publication | `.\release.ps1` | Only upon explicit user request. See `docs/release_protocol.md`. |

### Operational safety and side effects

- **Installer vs. build**: `install.ps1` mutates `config.json` and client configs; treat it as an installer, not a neutral build. `build.ps1` is the neutral compiler.
- **Client registration**: `clients add` / `init --write-clients` makes atomic backups and preserves unrelated servers and both OpenCode config formats (`mcp.<name>` and `mcp.servers.<name>`). OpenCode Desktop shares `opencode.jsonc` with OpenCode CLI and is registered automatically.
- **Session reloading is a separate gate**: A healthy CLI, green build, or rewritten client config does NOT mean the current agent session has reloaded MCP. AI clients cache tool schemas at connection start; a full client restart is mandatory before new tools/schemas take effect.
- **Process management**: Prefer the scoped process termination in `build.ps1` (terminating only processes mapped to the current checkout path). Do not use broad `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker` across the machine when other checkouts or instances may be active, unless hitting locked output errors covered by the Scoped Permission.

## Basic legacy GeneXus support

Not native SDK support. In addition to the native SDK majors (GX 16, 17, 18 and Evolution 3), the server supports every legacy version declared in `legacyMajors` via driver-specific degradation:

- GeneXus Evolution 1 (10.1), Evolution 2 (10.2), and GeneXus 15 via `dotnet-reflection` (using `DynamicSdkBridge` and `OptionalSdkInvoker` for module-less vs `QualifiedName` and API differences).
- GeneXus 8.0 and 9.0 via `com-gxpublic` (`ComGxPublicDriver` connecting to classic Win32 `GXPublic.GXPublic` COM automation on an STA thread for `.gxi` KBs).
- Modern tools unsupported in earlier versions return structured degradation envelopes (`UNSUPPORTED_IN_GENEXUS_VERSION`).

## Native gx-engine capabilities

Plan 133 & 134 parity roadmap. Each item is bounded coverage, not the full SDK replacement outcome described in [`engine_backend.md`](engine_backend.md).

- Content-addressed SHA-256 build cache with automatic LRU and `.tmp-*` directory pruning (`BuildCache::prune_if_needed`).
- Autonomous UDM metadata & deterministic version token resolution directly from SQL Server tables (`ModelEntityVersion`, `UtcTicks`, `ParentIdentity`).
- C# ADO.NET cursor generation with `ParDef` parameter inference (`@Param`, `GXType`, length, decimals) and deterministic handle cleanup.
- WebPanel `Enter` event lifecycle generation (`E13_Enter()`).
- Business Component companion class emission (`[Transaction]BC.cs` / `[Transaction]_bc.cs`) with full CRUD and transaction API (`Load`, `Save`, `Insert`, `Update`, `Delete`, `Success`, `Fail`, `GetMessages`) during `rebuild-all`.
- Native single-part edit routing contract (`EngineEditContract.cs`) with fail-closed safety guards and explicit Gateway SDK deferral (`DispatchEngineEdit`).
- Native UDM XML part decoding for `PatternInstance` (WorkWithPlus), `WebForm`, `Variables`, and `Structure` directly from storage without COM STA.
- Transactional live UDM persistence (`UdmWriter`) with atomic T-SQL batching (`plan_save_existing_object_sql`) and fail-closed safety guards.
- Streaming zero-allocation row parsing (`live_rows.rs`) and pre-allocated buffers in read sessions.
- Fast-path wildcard / pattern matching in KB object queries (`matches_name_pattern`).
- Topological dependency ordering with external reference partitioning in `Catalog`.
- Panic-free frontend AST fuzzing robustness (`tests/frontend_fuzz_robustness.rs`).
- **Measured differential performance:** 14.38x overall runtime speedup (peak 22.42x on SDT JSON serialization); 64.19x warm compilation speedup on Procedure fixtures vs. local GeneXus 18 SDK/MSBuild.

## Component and compatibility notes

Moved from `AGENTS.md` (link targets re-rooted for this directory).

- Gateway: `src/GxMcp.Gateway/` (`net10.0-windows`); owns the worker pool and routes MCP tools. In `stdio-isolated` mode (`GatewayMode: "stdio-isolated"`), each client runs its own dedicated Gateway without an HTTP listener or shared Gateway lease. Legacy non-strict configs may still expose `Server.TransportMode`; strict v2 documents use only `GatewayMode` and reject `Server.TransportMode` at parse time. `Server.WorkerSharingMode: "shared-host"` optionally attaches compatible Gateways to one per-KB WorkerHost; `"isolated"` remains the default when separate Workers are required. In legacy HTTP mode, gateways share a master process on the HTTP port with proxies.
- Worker: `src/GxMcp.Worker/`; hosts the COM-flavoured SDK on an STA thread.
- Worker sharing: `src/GxMcp.Gateway/SharedWorker*` and `src/GxMcp.Worker/SharedWorkerHost*` implement the supported broker/attachment path. Sharing is keyed by physical KB, Worker executable, GeneXus installation, driver, and target major; it never merges Gateway sessions, authorization, caches, cancellation, progress, notifications, or artifacts.
- CLI: `cli/run.js`, `cli/index.js`, and `cli/lib/config.js`; configures MCP
  clients, forwards stdio, and ships the Windows launcher diagnostics.
- Version catalog: `config/gx-versions.json` is the explicit compatibility list;
  `supportedMajors` is the native-SDK contract and `legacyMajors` is the basic
  compatibility contract with its driver profile. `src/GxMcp.Gateway/GeneXusVersionCatalog.cs`
  is the runtime loader.
  `src/GxMcp.Worker/Compatibility/` contains reusable runtime adapters for SDK
  members that vary between GeneXus majors.
- Native SDK compatibility is a **GeneXus-major** contract, not an exact DLL build
  contract. Do not block a supported major because `ProductVersion`, patch or
  assembly hashes differ between installations; patch/build drift is expected
  and must be handled by the compatibility adapters plus focused/live smoke
  tests. A different major requires the Worker built for that major (or a
  verified adapter); never run a Worker against an unsupported major. Exact
  build fingerprints may be retained as diagnostics, but must not silently
  become a runtime compatibility gate again.
- Legacy compatibility is intentionally best-effort and driver-backed rather
  than SDK-backed: GeneXus 15 and Evolution 1–2 use `dotnet-reflection`, while
  GeneXus 8.0 and 9.0 use `com-gxpublic`/`GXPublic.GXPublic` COM automation.
- GeneXus X Evolution 3 (10.3) is a native-SDK major with its own Worker build:
  `-p:GxMcpSdkProfile=evolution3` compiles against the Evolution 3 SDK (assemblies
  10.1.0.0), validates with `config/sdk-compatibility.ev3.json`, and publishes to
  `publish/worker-ev3`; the Gateway starts that Worker for 10.3 KBs. A Worker never
  loads the other SDK line (`SdkCompatibilityValidator`).
- Design System compatibility: `DesignSystemSdkAdapter` uses the native helper
  when available and parses the `Tokens`/`Styles` source parts independently
  when an SDK helper member is absent.
- Package artifact: `publish/`; `GxMcp.Gateway.exe` is at its root and
  `worker/GxMcp.Worker.exe` is one level below. The npm package (`genexus-mcp`) includes it.

## gx-engine development details

Moved from `AGENTS.md` (link targets re-rooted for this directory).

- For performance work or speed claims, read
  [Plan 131](../plans/131-sdk-performance-benchmark.md) first. Compare the same
  workload and observable behavior; distinguish warm/cold, selected-object/full-KB,
  and benchmarked/packaged binaries. Keep all raw samples outside Git.

- Drive remaining full-KB failures down with SDK differential evidence and
  regression tests. Preserve refusals and malformed-input errors; account for
  skipped/not-generated objects explicitly. A green package build or unit suite
  does not establish a successful whole-KB build, runtime parity or SDK replacement.

- Before querying UDM SQL tables, read [`docs/udm_storage_notes.md`](udm_storage_notes.md)
  and the SELECTs in `src/gx-engine/src/udm/client/model_reads.rs`; take column
  names from them, never from a guess.
- A native route that replaces an SDK path reproduces that path's side effects
  (caches, `ActiveModelSnapshot`, invalidation) or documents each one it skips.
  Every refusal guard in a native contract has a test that fails when the guard
  is removed.
- Validate the affected reader/compiler/runtime path, including actual runnable
  output when behavior changes. Preserve the final `build.ps1` ordering below;
  identify the exact packaged binary in postchecks rather than borrowing timings
  from another target directory.

## Tool schema and contract changes

Moved from `AGENTS.md` (link targets re-rooted for this directory).

- Tool schemas: `src/GxMcp.Gateway/tool_definitions.json`. It is the tool-list source of
  truth that `scripts/generate-operation-contract-inventory.py` reads; copies under
  `src/*/bin/**` are build outputs, so read the tracked file.
- Discovery golden fixture: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`; keep it alphabetically sorted. Regenerate automatically after intentional schema changes: `$env:GXMCP_UPDATE_GOLDEN='1'; dotnet test src\GxMcp.Gateway.Tests --filter McpDiscoveryContractTests; Remove-Item Env:\GXMCP_UPDATE_GOLDEN`.
- Tool dispatch path: gateway router → `src/GxMcp.Worker/Services/CommandDispatcher.cs` → service method. A tool change requires schema (`tool_definitions.json`), router, dispatcher, service, help catalog (`src/GxMcp.Gateway/ToolHelpCatalog.cs`), and fixture updates.
- Tool schema budget bumps require a `CHANGELOG.md` explanation.

- `genexus_query` and `genexus_list_objects` compact output must be added to
  `Program.GetDefaultCompactFields` when a new output field is introduced.
- For CLI launcher/config changes, update `cli/run.test.js`; use
  `docs/agent_playbook.md` for SDK authoring and tool-specific constraints.
- Release-facing version text is generated from `config/gx-versions.json` by
  `scripts/sync-release-metadata.py`; `release.ps1` runs it before the dirty-tree
  gate, and CI/release verification fails on drift.

- Any new KB-mutating tool must be registered in `Program.IsMutatingTool` and
  its invalidation behavior must have a regression test. See the detailed
  cache rules in `docs/agent_playbook.md`.

## Build checks, ripwire and SDK compatibility

Moved from `AGENTS.md` (link targets re-rooted for this directory).

Named checks live in `.pi/checks.json` (`project_checks` tool; each is capped at
10 minutes, so `build.ps1` stays a shell step). The live
native-vs-SDK parity runner is `scripts/run-native-live-parity.ps1`; it touches a
KB, so it is not a named check.

`ripwire . --quality-delta` (pass: `gating="0"`) is the gating ripwire check;
acknowledge a justified false positive per file with a written reason
(`--scope=<file> --ack-only=gating --quality-ack="<reason>"`) into
`.ripwire_quality_acks`. `ripwire . --test-gate` is informational here: it exits 4
whenever a test reaches the change or an impacted symbol has no indexed test, and
Cargo/xUnit tests have no derivable runner (`run_unknown=1`). Use it to list the
tests to run, then run the real lanes; never record its exit 4 as a failure or
exit 0 as proof.

SDK compatibility changes require a Worker build, focused tests and live smoke
for each installed major, with `GX_PATH` selecting that major's installation.
Use [`scripts/test-live-matrix.ps1`](../scripts/test-live-matrix.ps1) and
[`docs/live-kb-test-harness.md`](live-kb-test-harness.md) for explicit KB/SDK
selection and passed/unavailable/failed evidence. A missing SDK or skipped live
test is not compatibility evidence. Catalog additions require a verified build
and live smoke, not a matching version string.

## Debugging and validation gates

Moved from `AGENTS.md` (link targets re-rooted for this directory).

- For a numbered issue, read the current issue with `gh issue view <number>` and
  inspect local divergence before tracing code: `pwsh -NoProfile -File
  scripts/check-upstream-drift.ps1 -BaseRef origin/main`. This command never
  fetches or changes refs; it reports whether `origin/main` contains a likely
  upstream fix that is absent locally.
- Run `npm run test:live-contract` after changing live harness/configuration
  code. It is part of `npm test` and CI; this catches duplicate KB declarations,
  stale aliases, process ownership and missing fail-closed guards without
  requiring a GeneXus installation.
- For Worker/lease/KB routing changes, a local green suite is insufficient:
  run the smallest live smoke available with `pwsh`, using an explicit KB and
  `GXMCP_LOG_DIR`. Record `workerPid`, selection state, error count and the
  isolated log path. For `shared-host`, use two independent Gateway clients and
  verify one broker/Worker identity, attachment isolation, same-object lock
  rejection, distinct-object progress, and cleanup. A live gate that cannot run
  is `unavailable`, not pass.
- Use `ripwire src --for="<specific behavior>"` before source searches, then
  search narrowed file types (`*.cs`, `*.ps1`, `*.js`) and exclude generated
  `bin`, `obj`, `publish`, `TestResults` and `.trx` artifacts. Broad numeric
  searches are diagnostic noise, not evidence.
- When a shared Worker smoke fails, inspect `worker.diagnostics` and
  `workerHealth` before changing lifecycle code. Distinguish configuration or
  identity mismatch, registry/mutex election, pipe/handshake, attachment,
  startup, respawn, TTL, malformed-frame, and child-exit failures; do not reduce
  them to a generic `no_worker` message.
- Prefer bounded output for routine checks: `dotnet test ...
  --logger "console;verbosity=minimal"`. Repeat with normal verbosity only
  when the focused check fails or no test-run banner is present.

## Required workflow details

Moved from `AGENTS.md` (link targets re-rooted for this directory).

- **Architectural discovery (`ripwire`).** Reach for it when a text search would
  otherwise be the first move — a signature you must locate, a contract you are
  about to change, or a diff you did not write:
  - Locating a signature or orienting on unfamiliar code:
    `ripwire <dir> --for="<task in words>"`.
  - Before changing a contract: `ripwire <dir> --callers=SYM` and
    `--impact=SYM` for the transitive caller set.
  - Reviewing an incoming diff: `ripwire . --pr-context`. This is the path
    `scripts/pr-preflight.ps1 -PullRequest <N>` gates, and it only **fails** the
    run when called with `-RequireRipwire`, so pass that flag when the review must
    not proceed without it.

  On a branch you own and are pushing directly, no gate invokes this for you;
  the trigger above is the whole obligation.

- Inspect a diff with `git show <ref>:<path>` and `--stat`, never by dumping a
  whole file; cap output with `Select-Object -First N`. A single unfiltered diff
  of a large file can cost thousands of tokens.
- After a build failure, the next test run may report the previous binary. Force
  `-t:Rebuild` before believing a result that contradicts your change.

## MCP update and harness synchronization notes

Moved from `AGENTS.md` (link targets re-rooted for this directory).

`install.ps1` rewrites `config.json` and client configs (an installer, not a
build); `.\release.ps1` runs only on explicit user request or under the
standing release rule above. A rewritten client
config or green build does not reload the current session's MCP: a full client
restart is required before new tools take effect. Prefer `build.ps1`'s scoped
process termination over machine-wide `Stop-Process`.
