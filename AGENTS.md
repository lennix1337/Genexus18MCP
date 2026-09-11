# AGENTS.md

Project-level instructions for AI assistants working on Genexus18MCP. Keep this
file short: detailed, task-specific guidance lives in the linked documents
below and should be read only when the task matches it.

## Project orientation

Genexus18MCP is a two-process MCP server exposing Knowledge Bases from the
explicitly supported GeneXus majors through the selected native SDK. It does
not parse KB files or scrape IDE state; edits use the same SDK paths as the IDE.
Version-specific SDK members are isolated behind compatibility adapters so an
older SDK can fall back to its native source parts without changing the MCP
contract.

```text
MCP client (Claude/Cursor/…)
   │ stdio JSON-RPC
   ▼
GxMcp.Gateway (net10.0-windows: isolated per client in stdio-isolated mode; shared master/proxy in legacy HTTP mode)
   │ pipes JSON-RPC to a worker
   ▼
GxMcp.Worker (net48 STA, one per opened KB)
   │ compatibility adapters + Artech.* SDK
   ▼
Selected supported GeneXus SDK → Knowledge Base on disk
```

- Gateway: `src/GxMcp.Gateway/` (`net10.0-windows`); owns the worker pool and routes MCP tools. In `stdio-isolated` mode (`TransportMode: "stdio-isolated"`), each client runs its own dedicated gateway process without HTTP listener or shared lease. In legacy mode, gateways share a master process on the HTTP port with proxies.
- Worker: `src/GxMcp.Worker/`; hosts the COM-flavoured SDK on an STA thread.
- CLI: `cli/run.js`, `cli/index.js`, and `cli/lib/config.js`; configures MCP
  clients, forwards stdio, and ships the Windows launcher diagnostics.
- Version catalog: `config/gx-versions.json` is the explicit compatibility list;
  `src/GxMcp.Gateway/GeneXusVersionCatalog.cs` is its runtime loader.
  `src/GxMcp.Worker/Compatibility/` contains reusable runtime adapters for SDK
  members that vary between GeneXus majors.
- SDK compatibility is a **GeneXus-major** contract, not an exact DLL build
  contract. Do not block a supported major because `ProductVersion`, patch or
  assembly hashes differ between installations; patch/build drift is expected
  and must be handled by the compatibility adapters plus focused/live smoke
  tests. A different major requires the Worker built for that major (or a
  verified adapter); never run a Worker against an unsupported major. Exact
  build fingerprints may be retained as diagnostics, but must not silently
  become a runtime compatibility gate again.
- Design System compatibility: `DesignSystemSdkAdapter` uses the native helper
  when available and parses the `Tokens`/`Styles` source parts independently
  when an SDK helper member is absent.
- Package artifact: `publish/`; `GxMcp.Gateway.exe` is at its root and
  `worker/GxMcp.Worker.exe` is one level below. The npm package includes it.

<!-- BEGIN GENERATED: gx-compatibility -->
Supported SDK majors: **GeneXus 17, GeneXus 18**.
Primary SDK: **GeneXus 18**.
Source of truth: `config/gx-versions.json`.
<!-- END GENERATED: gx-compatibility -->

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

## Source of truth and tool changes

- Tool schemas: `src/GxMcp.Gateway/tool_definitions.json`.
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

## Build and test

For Worker builds, set the SDK path in the current PowerShell session:

```powershell
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
npm test
npm run lint
npm run test:one -- "test name pattern"
```

Use the narrowest decisive test first, then the repository-wide checks. Known
flaky tests are documented in the test section of `docs/agent_playbook.md`.
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

If the next call reports a stale pipe or crashed Worker, reconnect `/mcp` once.
For a version smoke, call `genexus_whoami` and verify
`geneXus.versionMatches=true`, `matchedMajor`, and `supportedMajors`; the
legacy `supportedMajor` field remains the catalog-primary compatibility alias.

## Required workflow

- **Mandatory architectural discovery (`ripwire`):** Before reading code manually or running blind text greps, always orient on the task with `ripwire`:
  - Search & orientation: `ripwire <dir> --for="<task in words>"` — ranked signatures by PageRank, AST and caller context.
  - Blast radius & callers: `ripwire <dir> --callers=SYM` and `--impact=SYM` (transitive callers before modifying contracts).
  - Contract check: `ripwire <dir> --edit-check=SYM`.
  - Diff & PR review: `ripwire . --pr-context` (automatically enforced in `pr-preflight.ps1`).
- Inspect the actual input/request/route/function/query/response path before
  fixing behavior. Add a regression test when technically viable.
- Make the smallest scoped change; preserve unrelated working-tree changes.
- Every verified bugfix, feature, performance improvement, or architectural
  change gets an immediate entry under `CHANGELOG.md` → `## Unreleased`, using
  `### Added`, `### Changed`, `### Fixed`, or `### Internal`. Release-facing
  style and PR-credit rules are in `docs/release_protocol.md`.
- Any new KB-mutating tool must be registered in `Program.IsMutatingTool` and
  its invalidation behavior must have a regression test. See the detailed
  cache rules in `docs/agent_playbook.md`.
- Do not claim completion without fresh validation. Review the final diff for
  scope, logic, edge cases, compatibility, security, tests, and docs.
- Do not commit, push, merge, release, deploy, or close an issue unless the
  user explicitly asks. Issue closure requires a released fix and release link.

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
| Updated local checkout | `.\install.ps1` | Updates `config.json`, runs `build.ps1`, and registers detected clients (`init --write-clients`). Parameters: `-KBPath`, `-GeneXusPath`. |
| Compile local checkout only | `.\build.ps1` | Regenerates `publish/` without modifying `config.json` or client registrations. |
| Fixed-path release install / upgrade | `powershell -File scripts/install.ps1` | Downloads release `publish.zip` into fixed location. Parameters: `-Kb`, `-Gx`. |
| npx / npm global upgrade | Run `genexus-mcp update` plan | Follow returned guidance and fully restart the AI client. |
| Antigravity launcher pointing to stale cache | `npx genexus-mcp clients add --clients antigravity` | Re-points the launcher to current direct gateway or package cache. |
| Post-sync validation | `npx genexus-mcp clients --format json`<br>`npx genexus-mcp doctor --mcp-smoke --format json` | Validates registration, gateway HTTP loopback, and live MCP protocol smoke. |
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
