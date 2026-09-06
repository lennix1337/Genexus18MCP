# Nexus IDE for GeneXus

![Nexus IDE for GeneXus](resources/extension-icon.png)

Nexus IDE is a GeneXus-focused extension for VS Code-family editors built directly on top of the repository MCP server.

## What It Does

- mounts the active Knowledge Base as a virtual workspace
- browses objects through a GeneXus explorer
- opens and edits Source, Rules, Events, Variables, Structure, Layout, and Indexes
- reads MCP tools, resources, prompts, and completions directly from the gateway
- supports build, SQL inspection, history, properties, refactor, formatting, and test workflows

## Runtime Model

The extension talks to the local GeneXus MCP gateway at `/mcp`.

Core flow:

1. start or reuse the local gateway lease for the current `port + KB + installation + shadow` identity
2. probe `server/discover` and use the sessionless 2026-07-28 transport when advertised; otherwise initialize the legacy session transport
3. discover tools/resources/prompts with the negotiated protocol metadata
4. drive the virtual filesystem and editor providers through MCP

## Quick Start

1. Run `.\install.ps1` from the repository root.
2. Restart your editor if it was already open.
3. Open the command palette and run `GeneXus: Open KB`.

If the editor CLI is not available in `PATH`, install the generated VSIX manually.

## Development Debug

`F5` runs `Nexus IDE: Prepare Debug`, which:

1. validates the local gateway lease for the active runtime identity
2. builds the worker, gateway, and extension
3. launches the extension host after cleanup/build/compile complete
4. lets the in-host `BackendManager` start or reuse the development gateway on the same HTTP port defined in the repository root `config.json`

The extension now owns the development gateway lifecycle during `F5`, so the workspace prelaunch no longer spawns a second bootstrap task before the host starts.
If a ready leased gateway is already listening, the extension reuses it even when `genexus.autoStartBackend` is disabled.
If the backend drops during reindex, materialization, or file hydration, the in-host `BackendManager` now restarts it directly instead of delegating to a separate bootstrap task.
Each recovery rotates `gateway_debug.log` and `worker_debug.log` into `.prev.log` instead of truncating them, so the previous crash trail is preserved.
`F5` now runs the repository `build.ps1` before launching, so the debug host, `publish/start_mcp.bat`, and the extension backend fallback all receive the same freshly built gateway/worker artifacts.

The gateway stays warm by default. The worker is lazy and exits after `Server.WorkerIdleTimeoutMinutes` of inactivity, so a healthy gateway no longer implies a permanently resident worker process.

Mutation responses are handled conservatively. If a transport loss happens
after a potentially mutating `tools/call` was sent, the client raises
`outcome_unknown` and preserves the supplied `idempotencyKey`/operation key;
it never retries that call with a new request ID. Use the Gateway lifecycle
`inspect` flow, independently read the affected object, and then call
`reconcile` with explicit confirmation before starting any new operation.
Transient retries are limited to an explicit read-only allowlist.

## Testing

Run `npm run check` from `src/nexus-ide` before committing or cutting a release. It chains
the three gates in order and stops at the first failure:

```powershell
npm run check   # compile (tsc) && lint (eslint) && test (mocha via @vscode/test-electron)
```

Individually: `npm run compile` (typecheck), `npm run lint` (ESLint 9; the repo currently
carries a warning-only baseline — 0 errors is the bar, new warnings should be fixed or
justified), `npm test` (downloads/launches a real VS Code instance headlessly and runs the
mocha suites under `src/test/suite/`). `npm test` needs a Windows desktop session (it can't
run on a headless CI runner without a display) — see `docs/nexus-ide-roadmap.md` Phase 0 for
the coverage this protects.

## Main Commands

- `GeneXus: Open KB`
- `GeneXus: Refresh Tree`
- `GeneXus: New Object`
- `GeneXus: Build with this only`
- `GeneXus: Get SQL Create Table (DDL)`
- `GeneXus: Show MCP Discovery Snapshot`
- `GeneXus: Open MCP Resource`
- `GeneXus: Run MCP Prompt`

## Requirements

- GeneXus 18
- .NET 10 SDK (Worker SDK remains .NET Framework 4.8)
- local Knowledge Base configured in the repository root [`config.json`](../../config.json)

## Configuration

The canonical runtime configuration is the repository root [`config.json`](../../config.json).

- `F5` debug and extension runtime pass `GX_CONFIG_PATH` to the gateway so debug, packaged backend, and local scripts read the same file
- the HTTP port defaults to `Server.HttpPort` from the canonical root `config.json`; `genexus.mcpPort` is only an explicit override
- `Server.WorkerIdleTimeoutMinutes` controls how long the gateway keeps an idle worker alive before shutting it down and recreating it on demand
- stdio mode remains a runtime override (`GX_MCP_STDIO`), not an edit to copied build artifacts
- build output copies of `config.json` remain fallback artifacts only

## MCP Surface

The extension is MCP-first and expects the gateway to expose the GeneXus toolset, including:

- `genexus_query`
  - supports optional `typeFilter` and `domainFilter` for lighter server-side browse flows
- `genexus_read`
- `genexus_edit`
- `genexus_inspect`
- `genexus_analyze`
- `genexus_lifecycle`
- `genexus_refactor`
- `genexus_format`
- `genexus_properties`
- `genexus_history`
- `genexus_structure`

## Repository

Project repository: [Genexus18MCP](https://github.com/lennix1337/Genexus18MCP)
