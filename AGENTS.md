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

- Components, configuration modes, version catalog, SDK-major compatibility contract, legacy drivers, Evolution 3 and package layout: [`docs/agent-operations.md`](docs/agent-operations.md#component-and-compatibility-notes). Legacy-driver `UNSUPPORTED_IN_GENEXUS_VERSION` envelopes: [`docs/agent-operations.md`](docs/agent-operations.md#basic-legacy-genexus-support).

<!-- BEGIN GENERATED: gx-compatibility -->
Supported SDK majors: **GeneXus 16, GeneXus 17, GeneXus 18, GeneXus Evolution 3** (native SDK).
Basic legacy compatibility: **GeneXus Evolution 2, GeneXus Evolution 1, GeneXus 15, GeneXus 9.0, GeneXus 8.0** via `com-gxpublic` and `dotnet-reflection` (not the native SDK build).
Primary SDK: **GeneXus 18**.
Source of truth: `config/gx-versions.json`.
<!-- END GENERATED: gx-compatibility -->

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
- For performance work or speed claims read [Plan 131](plans/131-sdk-performance-benchmark.md) first; full-KB failure work (preserve refusals and malformed-input errors), UDM SQL reads, native-route side effects and runtime-path validation: [`docs/agent-operations.md`](docs/agent-operations.md#gx-engine-development-details).
- For generated-file parity, missing/extra artifacts or SDK output comparisons,
  read [gxe-artifact-parity](.agents/skills/gxe-artifact-parity/SKILL.md).
- Live engine validation uses only disposable `C:\KBs\*teste*` KBs. Native UDM
  access can change KB state; record/restore temporary database settings and
  reconcile uncertain effects before replay. Keep Cloud off and use local
  SDK/MSBuild stages as the oracle; preserve licensing and existing SDK routing.
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

- Tool schema source of truth, golden fixture, dispatch path, schema budget, compact fields, CLI tests, mutating-tool registration and release metadata generation: [`docs/agent-operations.md`](docs/agent-operations.md#tool-schema-and-contract-changes).
- A published action change must also update `docs/mcp_capabilities_inventory.md` and the generated `docs/operation-contract-inventory.json`. Run `python scripts/validate-tool-contracts.py`, `python scripts/generate-operation-contract-inventory.py --check`, and the focused contract tests before pushing; the first gate checks the schema and capabilities table together.

## Build and test

Read [`CODING_STANDARDS.md` → Validation lanes](CODING_STANDARDS.md#validation-lanes)
for the complete check matrix and evidence rules. Start with the narrowest
behavior test; an authorized release uses the canonical preflight described in
[`docs/release_protocol.md`](docs/release_protocol.md), not repeated manual suites.
Run `.\build.ps1` **last** to preserve the published Worker/x86 fingerprint.
Named checks, `ripwire` gating and SDK-compatibility evidence: [`docs/agent-operations.md`](docs/agent-operations.md#build-checks-ripwire-and-sdk-compatibility).

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

- Issue drift check, live-contract test, Worker/lease live smoke, ripwire search hygiene, shared-Worker diagnostics and bounded test output: [`docs/agent-operations.md`](docs/agent-operations.md#debugging-and-validation-gates).
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

- Architectural discovery with `ripwire`, diff inspection and rebuild after a failed build: [`docs/agent-operations.md`](docs/agent-operations.md#required-workflow-details).
- Inspect the actual input/request/route/function/query/response path before
  fixing behavior. Add a regression test when technically viable.
- Edit tracked text with the native edit tools (keep LF line endings), never through a shell or text-mode script. Why and what to review: [`CODING_STANDARDS.md`](CODING_STANDARDS.md) § Text edits.
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
- Do not claim completion without fresh validation. Review the final diff for
  scope, logic, edge cases, compatibility, security, tests, and docs.
- Pull requests are always allowed: create a branch, commit to it, push it and
  open or update the PR without asking. Do not merge, commit or push to `main`,
  release, deploy, or close an issue unless the user explicitly asks. Issue closure requires a released fix and release link.
- Standing release rule (user, 2026-11-08): when no issue and no PR is open
  (issues labeled `fixed-pending-release` do not count: the release closes
  them), checked at the start of a wake or right after the last open issue or PR is
  merged, the agent may publish a minor or patch release of the merged,
  unreleased fixes without asking, choosing the bump by the changes (`### Added`
  or behavior change: minor; only fixes/internal: patch). It follows
  `docs/release_protocol.md` (canonical preflight, `.\release.ps1`), and the
  report names the version and release URL. Major releases still need an
  explicit request.
- After pushing a merged fix that is not yet released, mark each fixed issue `fixed-pending-release`: [`docs/release_protocol.md`](docs/release_protocol.md#post-merge-issue-marking).

## MCP update and harness synchronization

Before creating any script for build, install, upgrade or client registration,
read the decision matrix in
[`docs/agent-operations.md`](docs/agent-operations.md#mcp-update-and-harness-synchronization)
and reuse `install.ps1`, `build.ps1`, `scripts/install.ps1` or `cli/`.
`.\release.ps1` runs only on explicit user request or under the standing release rule above; installer, client-config and restart notes: [`docs/agent-operations.md`](docs/agent-operations.md#mcp-update-and-harness-synchronization-notes).

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
