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

Not native SDK support. In addition to the primary native SDK majors (GX 16, 17, 18), the server supports every legacy version declared in `legacyMajors` via driver-specific degradation:

- GeneXus Evolution 1 (10.1), Evolution 2 (10.2), Evolution 3 (10.3), and GeneXus 15 via `dotnet-reflection` (using `DynamicSdkBridge` and `OptionalSdkInvoker` for module-less vs `QualifiedName` and API differences).
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
