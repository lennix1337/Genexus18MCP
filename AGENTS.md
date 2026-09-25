# AGENTS.md

Project-level instructions for AI assistants working on Genexus18MCP. Keep this
file short: detailed, task-specific guidance lives in the linked documents
below and should be read only when the task matches it.

## Project orientation

Genexus18MCP is a two-process MCP server exposing Knowledge Bases from the
officially supported native-SDK majors and catalogued legacy versions through
their selected driver. It does not parse KB files or scrape IDE state; native
edits use the same SDK paths as the IDE, while legacy paths use explicit
reflection/COM adapters without changing the MCP contract.

```text
MCP clients (Claude/Cursor/…)
   │ independent stdio JSON-RPC processes
   ▼
GxMcp.Gateway (net10.0-windows, one logical MCP context per client)
   ├─ isolated: owns a direct Worker child
   └─ shared-host: attaches to a per-KB local WorkerHost broker
                         │ one compatible Worker child
                         ▼
                 GxMcp.Worker (net48 STA)
                         │ compatibility adapters + Artech.* SDK
                         ▼
                 Selected SDK/driver → Knowledge Base on disk
```

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
  than SDK-backed: GeneXus 15 and Evolution 1–3 use `dotnet-reflection`, while
  GeneXus 8.0 and 9.0 use `com-gxpublic`/`GXPublic.GXPublic` COM automation.
- Design System compatibility: `DesignSystemSdkAdapter` uses the native helper
  when available and parses the `Tokens`/`Styles` source parts independently
  when an SDK helper member is absent.
- Package artifact: `publish/`; `GxMcp.Gateway.exe` is at its root and
  `worker/GxMcp.Worker.exe` is one level below. The npm package includes it.

<!-- BEGIN GENERATED: gx-compatibility -->
Supported SDK majors: **GeneXus 16, GeneXus 17, GeneXus 18** (native SDK).
Basic legacy compatibility: **GeneXus Evolution 3, GeneXus Evolution 2, GeneXus Evolution 1, GeneXus 15, GeneXus 9.0, GeneXus 8.0** via `com-gxpublic` and `dotnet-reflection` (not the native SDK build).
Primary SDK: **GeneXus 18**.
Source of truth: `config/gx-versions.json`.
<!-- END GENERATED: gx-compatibility -->

- **Basic legacy GeneXus support (not native SDK support):** In addition to the primary native SDK majors (GX 16, 17, 18), the server supports every legacy version declared in `legacyMajors` via driver-specific degradation:
  - GeneXus Evolution 1 (10.1), Evolution 2 (10.2), Evolution 3 (10.3), and GeneXus 15 via `dotnet-reflection` (using `DynamicSdkBridge` and `OptionalSdkInvoker` for module-less vs `QualifiedName` and API differences).
  - GeneXus 8.0 and 9.0 via `com-gxpublic` (`ComGxPublicDriver` connecting to classic Win32 `GXPublic.GXPublic` COM automation on an STA thread for `.gxi` KBs).
  - Modern tools unsupported in earlier versions return structured degradation envelopes (`UNSUPPORTED_IN_GENEXUS_VERSION`).

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

## Source of truth and tool changes

- Tool schemas: `src/GxMcp.Gateway/tool_definitions.json`.
- Discovery golden fixture: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`; keep it alphabetically sorted. Regenerate automatically after intentional schema changes: `$env:GXMCP_UPDATE_GOLDEN='1'; dotnet test src\GxMcp.Gateway.Tests --filter McpDiscoveryContractTests; Remove-Item Env:\GXMCP_UPDATE_GOLDEN`.
- Tool dispatch path: gateway router → `src/GxMcp.Worker/Services/CommandDispatcher.cs` → service method. A tool change requires schema (`tool_definitions.json`), router, dispatcher, service, help catalog (`src/GxMcp.Gateway/ToolHelpCatalog.cs`), and fixture updates.
- Tool schema budget bumps require a `CHANGELOG.md` explanation.
- A published action change must also update `docs/mcp_capabilities_inventory.md` and the generated `docs/operation-contract-inventory.json`. Run `python scripts/validate-tool-contracts.py`, `python scripts/generate-operation-contract-inventory.py --check`, and the focused contract tests before pushing; the first gate checks the schema and capabilities table together.
- `genexus_query` and `genexus_list_objects` compact output must be added to
  `Program.GetDefaultCompactFields` when a new output field is introduced.
- For CLI launcher/config changes, update `cli/run.test.js`; use
  `docs/agent_playbook.md` for SDK authoring and tool-specific constraints.
- Release-facing version text is generated from `config/gx-versions.json` by
  `scripts/sync-release-metadata.py`; `release.ps1` runs it before the dirty-tree
  gate, and CI/release verification fails on drift.

## Build and test

For Worker builds, set the SDK path in the current PowerShell session:

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus16'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj

$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus17Trial'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj

$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj
```

The Worker must be built and focused-tested once per installed major when
changing SDK compatibility. The Gateway package build uses the SDK selected by
`GX_PATH` (the catalog's primary major is the normal distribution default). A
new major is not considered supported merely because its version string starts
with a number; add it to `config/gx-versions.json` only after its Worker build
and live-KB smoke pass.

For a fixture-backed compatibility check across installed majors, use
`scripts/test-live-matrix.ps1`; it selects every catalog major by default,
accepts `-Majors` and `-GxPathMap`, builds the artifact once, and records
`passed`, `unavailable`, or `failed` per major. Release preflight selects this
mode through `-LiveMajors`/`-LiveGxPathMap` or the matching environment variables;
see `docs/live-kb-test-harness.md` for fixture and evidence rules.

```powershell
.\build.ps1
dotnet build Genexus18MCP.sln -v:minimal
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj
dotnet test Genexus18MCP.sln
dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~PropertyService"
dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~McpRouter"
pwsh -NoProfile -File scripts\tests\run-release-script-tests.ps1
python -m unittest discover -s scripts\tests
npm test
npm run lint
npm run test:one -- "test name pattern"
```

A change is validated when **every** lane above is green, not the lanes you
remember. The PowerShell and Python suites catch contract drift the .NET, CLI and
Nexus suites are blind to — a live-test class missing its `ProcessSmoke` trait
passes every .NET lane and is rejected by the release preflight guard.

Run `.\build.ps1` **last**. A solution-level `dotnet test -c Release` rebuilds
the Worker outside the `x86` platform the solution maps it to, breaking the
publish↔source byte identity so the release fingerprint comes back empty and
`test-release-preflight.ps1` fails for a reason unrelated to your change.

Use the narrowest decisive test first, then the repository-wide checks. Known
flaky tests are documented in the test section of `docs/agent_playbook.md`.
Review-time rules live in [`CODING_STANDARDS.md`](CODING_STANDARDS.md).

## Debugging and validation gates

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
- Live PowerShell entry points require PowerShell 7+ and fail immediately with
  a clear message under Windows PowerShell 5.1. Use `pwsh`, never silently
  substitute a legacy host whose cmdlets differ.
If a build/test fails with `MSB3027` or `MSB3021` naming the Gateway/Worker exe,
use the scoped permission below; do not kill unrelated processes.

## Runtime iteration

The gateway serves Streamable HTTP at `http://127.0.0.1:5000/mcp` by default.
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

## Required workflow

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
- Inspect the actual input/request/route/function/query/response path before
  fixing behavior. Add a regression test when technically viable.
- Edit tracked text with the file-editing tool, not by piping it through a shell:
  fixed line indices truncate long files, shell round-trips re-encode UTF-8, and
  index arithmetic with shifting bounds deletes code. Use the shell to read and
  to run commands. See [`CODING_STANDARDS.md`](CODING_STANDARDS.md) § Text edits.
- Inspect a diff with `git show <ref>:<path>` and `--stat`, never by dumping a
  whole file; cap output with `Select-Object -First N`. A single unfiltered diff
  of a large file can cost thousands of tokens.
- After a build failure, the next test run may report the previous binary. Force
  `-t:Rebuild` before believing a result that contradicts your change.
- Every new regression guard gets a mutation check: revert the fix and confirm
  the test goes red. A guard that cannot fail is not coverage.
- Make the smallest scoped change; preserve unrelated working-tree changes.
- Every verified bugfix, feature, performance improvement, or architectural
  change gets an immediate entry under `CHANGELOG.md` → `## Unreleased`, using
  `### Added`, `### Changed`, `### Fixed`, or `### Internal`. Release-facing
  style and PR-credit rules are in `docs/release_protocol.md`.
- For issue-driven fixes, read the current issue before editing and add its
  canonical `https://github.com/lennix1337/Genexus18MCP/issues/<N>` URL to the
  same `## Unreleased` entry. Grouped bullets must list every fixed issue
  explicitly; a PR number or `/pull/<N>` URL is not an issue reference. The
  release entrypoint verifies this ledger before publishing.
- Any new KB-mutating tool must be registered in `Program.IsMutatingTool` and
  its invalidation behavior must have a regression test. See the detailed
  cache rules in `docs/agent_playbook.md`.
- Do not claim completion without fresh validation. Review the final diff for
  scope, logic, edge cases, compatibility, security, tests, and docs.
- Do not commit, push, merge, release, deploy, or close an issue unless the
  user explicitly asks. Issue closure requires a released fix and release link.
- After pushing a merged fix that is not yet released, mark each fixed issue
  with `pwsh -NoProfile -File scripts/release-issues.ps1 -Action MarkFixedPendingRelease -Issue <N>`;
  the release entrypoint closes labeled issues on publish. Do not post issue
  comments unless the user asks.

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

## Task-specific references

Read only the relevant reference:

- SDK tools, authoring order, placement, SDPanels, live validation, and Windows
  gotchas: [`docs/agent_playbook.md`](docs/agent_playbook.md).
- Release execution, merge discipline, npm verification, and changelog voice:
  [`docs/release_protocol.md`](docs/release_protocol.md).
- Release interruption or failed-publication recovery: read
  [`docs/RELEASE.md`](docs/RELEASE.md), then use `scripts/release-status.ps1`
  and `scripts/release-doctor.ps1`; prefer `release.ps1 -Detach` and follow the
  doctor's exact-input/fingerprint check before any `-SkipBuild` or `-SkipTests`.
- Runtime variables: [`docs/environment_variables.md`](docs/environment_variables.md).
- MCP/HTTP debugging: [`docs/mcp_debugging_guide.md`](docs/mcp_debugging_guide.md).
- LLM-facing CLI/MCP operation: [`docs/llm_cli_mcp_playbook.md`](docs/llm_cli_mcp_playbook.md).

## Scoped permission: locked Gateway/Worker outputs

- **Trigger:** `dotnet build` or `dotnet test` fails with `MSB3027`/`MSB3021`
  naming `GxMcp.Gateway.exe` or `GxMcp.Worker.exe`.
- **Action:** run `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` or
  `taskkill /IM GxMcp.Gateway.exe /F`.
- **Rationale:** these are the user's own development processes and can be
  restarted by reconnecting the MCP client or rerunning the harness.
- **Out of scope:** arbitrary process-name matches, GeneXus IDE, Visual Studio,
  other users, system services, remote machines, or any case without the
  specified MSB lock error.
- **Granted:** 2026-05-15 by the user; reviewed 2026-05-15.

## Self-update behavior

On the first `genexus_whoami` of a session, if its cached `update.updateAvailable`
is true, tell the user the current/latest versions and release URL and ask before
installing. Use the returned command only after approval, then require a full
AI-client restart. Respect `GENEXUS_MCP_NO_UPDATE_CHECK=1` and do not nag on
subsequent calls. Environment details are in `docs/environment_variables.md`.
