# AGENTS.md

Project-level instructions for AI assistants working on Genexus18MCP. Keep this
file short: detailed, task-specific guidance lives in the linked documents
below and should be read only when the task matches it.

## Project orientation

Genexus18MCP is a two-process MCP server exposing Knowledge Bases from the
officially supported native-SDK majors and catalogued legacy versions through
their selected driver. The default SDK route does not parse KB files or scrape
IDE state; native edits use the same SDK paths as the IDE, while legacy paths use
explicit reflection/COM adapters without changing the MCP contract. The default
experimental gx-engine route uses independent native UDM readers and compiler
paths for admitted blocking builds and field-limited type-only queries; unsupported
operations retain the SDK. See the gx-engine pointers below for admission rules.

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
  `worker/GxMcp.Worker.exe` is one level below. The npm package includes it.

<!-- BEGIN GENERATED: gx-compatibility -->
Supported SDK majors: **GeneXus 16, GeneXus 17, GeneXus 18, GeneXus Evolution 3** (native SDK).
Basic legacy compatibility: **GeneXus Evolution 2, GeneXus Evolution 1, GeneXus 15, GeneXus 9.0, GeneXus 8.0** via `com-gxpublic` and `dotnet-reflection` (not the native SDK build).
Primary SDK: **GeneXus 18**.
Source of truth: `config/gx-versions.json`.
<!-- END GENERATED: gx-compatibility -->

- Legacy drivers (`DynamicSdkBridge`, `ComGxPublicDriver`) and their
  `UNSUPPORTED_IN_GENEXUS_VERSION` envelopes:
  [`docs/agent-operations.md`](docs/agent-operations.md#basic-legacy-genexus-support).

## gx-engine development

- For compiler/storage work, read [`src/gx-engine/README.md`](src/gx-engine/README.md);
  for MCP routing, read [`docs/engine_backend.md`](docs/engine_backend.md).
  The objective is independent full SDK replacement; current bounded coverage
  is not that outcome. `Engine.Backend` defaults to `gxengine`: admitted blocking
  builds, field-limited type-only queries, and single-part reads (`kb-read`) can be
  served natively. Native UDM writes (`kb-write` / `UdmWriter`) support transactional
  live T-SQL persistence in `gx-engine`; Gateway MCP `genexus_edit` retains SDK
  fallback to safeguard IDE pattern synchronization and cross-reference integrity.
  `auto` routes only builds; explicit `sdk` opts out entirely. Preserve capability/argument
  gates, SDK fallback before build start, and reconciliation instead of replay
  after a started build fails or has an uncertain outcome.
- For performance work or speed claims, read
  [Plan 131](plans/131-sdk-performance-benchmark.md) first. Compare the same
  workload and observable behavior; distinguish warm/cold, selected-object/full-KB,
  and benchmarked/packaged binaries. Keep all raw samples outside Git.
- For generated-file parity, missing/extra artifacts or SDK output comparisons,
  read [gxe-artifact-parity](.agents/skills/gxe-artifact-parity/SKILL.md).
- Drive remaining full-KB failures down with SDK differential evidence and
  regression tests. Preserve refusals and malformed-input errors; account for
  skipped/not-generated objects explicitly. A green package build or unit suite
  does not establish a successful whole-KB build, runtime parity or SDK replacement.
- Live engine validation uses only disposable `C:\KBs\*teste*` KBs. Native UDM
  access can change KB state; record/restore temporary database settings and
  reconcile uncertain effects before replay. Keep Cloud off and use local
  SDK/MSBuild stages as the oracle; preserve licensing and existing SDK routing.
- Before querying UDM SQL tables, read [`docs/udm_storage_notes.md`](docs/udm_storage_notes.md)
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
- **Current native capabilities (Plan 133 & 134 parity roadmap):** listed in
  [`docs/agent-operations.md`](docs/agent-operations.md#native-gx-engine-capabilities).

## KB and harness contracts

KB resolution order (explicit `kb` → session selection → strict/legacy policy),
`open`/`select`/`set_persistent_default` semantics, alias audit fields, client
`init` layouts and the `genexus_whoami`/`genexus_doctor` diagnostic contract are in
[`docs/agent-operations.md`](docs/agent-operations.md#kb-and-harness-contracts); read it
before changing KB selection, sessions or diagnostics. Never introduce shared
server-side selection between independent clients, and keep credentials, tokens
and connection strings redacted in diagnostics.

## Source of truth and tool changes

- Tool schemas: `src/GxMcp.Gateway/tool_definitions.json`. It is the tool-list source of
  truth that `scripts/generate-operation-contract-inventory.py` reads; copies under
  `src/*/bin/**` are build outputs, so read the tracked file.
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

Read [`CODING_STANDARDS.md` → Validation lanes](CODING_STANDARDS.md#validation-lanes)
for the complete check matrix and evidence rules. Start with the narrowest
behavior test; an authorized release uses the canonical preflight described in
[`docs/release_protocol.md`](docs/release_protocol.md), not repeated manual suites.
Run `.\build.ps1` **last** to preserve the published Worker/x86 fingerprint.
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
Use [`scripts/test-live-matrix.ps1`](scripts/test-live-matrix.ps1) and
[`docs/live-kb-test-harness.md`](docs/live-kb-test-harness.md) for explicit KB/SDK
selection and passed/unavailable/failed evidence. A missing SDK or skipped live
test is not compatibility evidence. Catalog additions require a verified build
and live smoke, not a matching version string.

Backend/default changes additionally require the
[behavioral compatibility checklist](CODING_STANDARDS.md#backend-and-default-changes).
Known test limitations live in [`docs/agent_playbook.md`](docs/agent_playbook.md).

## Live Knowledge Bases for validation

Any directory under `C:KBs` whose name contains `teste` is a disposable scratch
KB: full end-to-end validation with the real SDK on it, including the mutations
that flow needs, is standing-authorized, so run the live flow instead of asking.
It does not cover production KBs, Cloud, release or deployment; keep Cloud off.
To build a test KB outside the MCP use only `scripts/sdk-local-build.ps1`; never
hand-write `BuildOne`/`BuildAll` projects (the Trial install forces
Deploy-to-Cloud and they can reorganize the database). The KB table, release
preflight KB resolution and what `genexus_wire` can or cannot validate on native
KBs are in [`docs/agent-operations.md`](docs/agent-operations.md#live-knowledge-bases-for-validation).

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
- Use `pwsh` for PowerShell entry points, never Windows PowerShell 5.1.
  `scripts/run-gate.ps1` requires PowerShell 7.3+ so a failed native command
  cannot be masked by a later success; live entry points require 7+.
If a build/test fails with `MSB3027` or `MSB3021` naming the Gateway/Worker exe,
use the scoped permission below; do not kill unrelated processes.

## Runtime iteration

The Gateway is a stdio JSON-RPC server: never run `GxMcp.Gateway.exe` directly
(it waits on stdin). Use the `genexus-mcp` CLI or the test harnesses. Transport
modes, Worker hot-swap and version smoke fields are in
[`docs/agent-operations.md`](docs/agent-operations.md#runtime-iteration).

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
- Edit tracked text with the native edit tools (keep LF line endings), not by piping it through a shell or text-mode script:
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
- Pull requests are always allowed: create a branch, commit to it, push it and
  open or update the PR without asking. Do not merge, commit or push to `main`,
  release, deploy, or close an issue unless the user explicitly asks. Issue closure requires a released fix and release link.
- After pushing a merged fix that is not yet released, mark each fixed issue
  with `pwsh -NoProfile -File scripts/release-issues.ps1 -Action MarkFixedPendingRelease -Issue <N>`;
  the release entrypoint closes labeled issues on publish. Do not post issue
  comments unless the user asks.

## MCP update and harness synchronization

Before creating any script for build, install, upgrade or client registration,
read the decision matrix in
[`docs/agent-operations.md`](docs/agent-operations.md#mcp-update-and-harness-synchronization)
and reuse `install.ps1`, `build.ps1`, `scripts/install.ps1` or `cli/`.
`install.ps1` rewrites `config.json` and client configs (an installer, not a
build); `.\release.ps1` runs only on explicit user request. A rewritten client
config or green build does not reload the current session's MCP: a full client
restart is required before new tools take effect. Prefer `build.ps1`'s scoped
process termination over machine-wide `Stop-Process`.

## Task-specific references

Read only the relevant reference; [`docs/README.md`](docs/README.md) indexes every
document under `docs/` by area:

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
