# AGENTS.md

Project-level instructions for AI assistants working on Genexus18MCP.

## Project orient

Two-process MCP server that exposes a GeneXus 18 Knowledge Base to AI agents (Claude Desktop, Claude Code, Cursor, etc.) via the native GeneXus SDK — no parsing of KB files, no scraped IDE state, edits go through the same code paths the IDE uses. Codebase is C# / .NET for everything that touches the SDK; the npm package (`genexus-mcp`) is a thin Node wrapper shipping pre-built Windows binaries and writing MCP-client config.

```
MCP client (Claude/Cursor/…)
   │   stdio JSON-RPC
   ▼
GxMcp.Gateway  (long-running, one per client)
   │   pipes JSON-RPC over stdio
   ▼
GxMcp.Worker   (one per opened KB; STA thread; hosts Artech.* SDK in-process)
   │   COM-flavoured SDK calls
   ▼
GeneXus 18 SDK  (C:\Program Files (x86)\GeneXus\GeneXus18\Artech.*.dll)
   ▼
Knowledge Base on disk
```

- **Gateway** (`src/GxMcp.Gateway/`, **net8.0-windows**) — speaks MCP stdio with the client, owns a `WorkerPool` indexed by KB alias, routes tool calls through `Routers/*.cs` to a per-KB worker. `Program.cs` is the MCP loop + `whoami` builder + worker lifecycle.
- **Worker** (`src/GxMcp.Worker/`, **net48 STA**) — owns the GeneXus SDK in-process. STA thread is mandatory because the SDK is COM-flavoured. `Services/CommandDispatcher.cs` is the RPC switchboard; `KbService` opens KBs; `IndexCacheService` maintains an on-disk `SearchIndex` cache; `Services/{ListService,SearchService,AnalyzeService,WriteService,…}` implement the tools.
- **CLI** (`cli/run.js`) — what `npx genexus-mcp` invokes. Reads MCP client configs (Claude Desktop, Codex, Cursor, VS Code), writes the server entry pointing at `publish/start_mcp.bat`, then forwards stdio to the gateway. Tests are pure Node (`cli/run.test.js`).
- **publish/** — the deployable artifact. Both `install.ps1` (build-from-source) and `npm publish` (via `publish.zip`) ship from this directory. `GxMcp.Gateway.exe` at the root, `worker/GxMcp.Worker.exe` one level down. This layout is asserted by the npm-publish workflow.

### Tool surface lives in two synchronized places

- `src/GxMcp.Gateway/tool_definitions.json` — single source of truth for MCP tool schemas. `ToolSchemaSizeTests` enforces a token budget; bumping the constant requires a `CHANGELOG.md` entry recording the new value and why (the test itself only keeps the last few bumps for quick context).
- `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json` — golden fixture for the discovery `tools/list` envelope. **Must stay alphabetically sorted by tool name.** When you add/change a schema field in `tool_definitions.json`, regenerate the corresponding section in the golden fixture or the contract test fails.

### Adding or modifying a tool

The dispatch path goes: gateway router (`src/GxMcp.Gateway/Routers/*Router.cs`) ↔ worker dispatcher (`src/GxMcp.Worker/Services/CommandDispatcher.cs`). To add a tool: schema in `tool_definitions.json` → router case → dispatcher action → service method → golden fixture update.

**AxiCompact projection:** `genexus_query` and `genexus_list_objects` default to a compact field allowlist defined in `Program.GetDefaultCompactFields`. Adding a field to a tool's output also requires whitelisting it there, or it gets stripped before reaching the client.

## Build / test commands

Set this once per shell when working with Worker code (build-time reference path):

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
```

### Build

```powershell
.\build.ps1                                  # full Gateway+Worker build + deploy to publish/
dotnet build Genexus18MCP.sln -v:minimal     # quick solution build (no publish/ refresh)
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj
```

If the build fails with `MSB3027` / `MSB3021` citing `GxMcp.Gateway.exe` or `GxMcp.Worker.exe` locked, the running dev gateway/worker is holding the binary — see the "Kill the Gateway/Worker" permission below.

### Test

```powershell
dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj      # net48;  ~1,790 tests
dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj    # net8.0;   ~760 tests
dotnet test Genexus18MCP.sln                                     # both
npm test                                                          # cli tests only (node --test)

# Single test or filter
dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~TemporalListTests"
dotnet test ...csproj --filter "FullyQualifiedName=GxMcp.Worker.Tests.TemporalListTests.SortByLastUpdate_OrdersDescending"
```

Known flaky in parallel runs: `EdgeCaseRegressionTests.Dispatcher_PatchApply_ValidateOnly_MapsToDryRun_ViaConvention`, sometimes `PatternApplyServiceTests.*` — all pass in isolation. Treat a single failure as a flake until you reproduce it isolated.

### Reload a running worker without restarting the MCP client

After editing Worker code, you can hot-swap the running worker:

- From inside any MCP session: `genexus_worker_reload mode=hard sourceDir=C:\Projetos\Genexus18MCP\src\GxMcp.Worker\bin\Debug`
- Force-kill path (use when worker is wedged and not responding): `genexus_worker_reload mode=soft force=true`

After a worker-reload the gateway's pipe handle can go stale — if the next call returns `Worker for KB '…' crashed/exited`, reconnect MCP via `/mcp` (Claude Code) once.

### Iterate against a running gateway over HTTP (default dev loop — no client restart)

The gateway serves a Streamable-HTTP MCP endpoint on `http://127.0.0.1:5000/mcp`
alongside the stdio link (port from `Server.HttpPort`, default 5000; loopback needs no
token). **Prefer driving this endpoint over asking the user to restart their MCP client
for every change** — a client restart and an HTTP call reach the same running gateway, so
the HTTP path is the default way to live-iterate.

- **Handshake:** `POST /mcp` with `Accept: application/json, text/event-stream` and an
  `initialize` request; reuse the `MCP-Session-Id` response header on every later call.
  Tool results come back as `result.content[0].text` holding the worker's JSON (parse twice
  — it's JSON-in-JSON).
- **No gateway up?** Launch one: `publish/start_mcp.bat` (or the client's own). On stdin EOF
  a detached gateway stays alive (`Program.cs` falls into `Task.Delay(-1)`), so the HTTP
  endpoint keeps serving. Then open a KB with `genexus_kb action=open path=<kb>`.
- After editing Worker code, hot-swap via `genexus_worker_reload` (above); over HTTP the
  reload survives even when the stdio `/mcp` link would need a reconnect.

## Continuous CHANGELOG maintenance (Mandatory)

- **Immediate Unreleased logging:** Every completed bugfix, feature, performance improvement, or architectural change MUST immediately be added to `CHANGELOG.md` under the `## Unreleased` section as soon as implementation is verified.
- **Format:** Group entries under `### Added`, `### Changed`, `### Fixed`, or `### Internal` with clear descriptions of what was improved/fixed and why.

## Release protocol (Mandatory on user release request)

Whenever the user requests a release (e.g. "cria release", "corta release", "faz release"):
1. **Always cut BOTH GitHub Release AND publish to npm (`genexus-mcp`).**
2. **Standard release execution:** Run `.\release.ps1 -Version <X.Y.Z>` (or execute build → bump versions in `package.json`, `.csproj`, `CHANGELOG.md` → pack `publish.zip` using normalized forward slashes → write `publish.zip.sha256` → commit & tag `vX.Y.Z` → `gh release create` → `gh release upload publish.zip`).
3. **Automated npm publish verification:** GitHub Actions (`.github/workflows/release.yml`) triggers on release/upload of `publish.zip` and publishes `genexus-mcp` to npm via OIDC. Always verify workflow completion with `gh run list --workflow release.yml` and `npm view genexus-mcp@latest version`.
4. **Issue closure:** If the release resolves open GitHub issues, comment on each issue with the release link (`https://github.com/lennix1337/Genexus18MCP/releases/tag/vX.Y.Z`) and close them.

## Permissions granted to the assistant

Each entry must include: **trigger** (the precise condition that activates the
permission), **action** (what the assistant may do), and **rationale** (why this
is preferable to asking). Permissions should be reviewed quarterly to catch
broad rules that accumulate over time.

### Kill the Gateway/Worker when they lock build outputs

- **Trigger:** `dotnet build` / `dotnet test` fails with `MSB3027` or `MSB3021`
  citing `GxMcp.Gateway.exe` or `GxMcp.Worker.exe` as the locking process.
- **Action:** `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` (PowerShell)
  or `taskkill /IM GxMcp.Gateway.exe /F`.
- **Rationale:** these are the user's own dev processes; pausing to ask each
  time adds friction without protecting anything (the user can restart by
  reconnecting the MCP client or rerunning the harness). Permission does NOT
  extend to other processes, system services, or remote machines.
- **Out of scope:** killing arbitrary processes by name match, killing
  GeneXus IDE / Visual Studio, force-killing build daemons under a different
  user, force-killing anything when no MSB lock error is present.
- **Granted:** 2026-05-15 by user. Last reviewed: 2026-05-15.

## Self-update protocol (LLM-facing)

When an AI agent connects to this MCP, it can — and should — proactively check whether the server it's running against is up to date.

### How to check

Call `genexus_whoami`. The response includes an `update` block:

```json
"update": {
  "currentVersion": "2.5.0",
  "latestVersion": "2.5.3",
  "updateAvailable": true,
  "checkedAt": "2026-05-19T19:22:00Z",
  "releaseUrl": "https://github.com/lennix1337/Genexus18MCP/releases/tag/v2.5.3",
  "command": "npx genexus-mcp@latest init",
  "restartRequired": true
}
```

The check is performed by the gateway in the background on `initialize`, cached for 24h in `%LOCALAPPDATA%\GenexusMCP\update-check.json`. `whoami` just reads the cache — instant, no network round-trip on the user's tool call.

### What the LLM should do

- **On the first `whoami` of a session**, look at `update.updateAvailable`. If `true`, surface it to the user in plain language: *"Heads up — GeneXus MCP v{latestVersion} is out (you're on v{currentVersion}). Release notes: {releaseUrl}. Want me to install it?"*
- **If the user agrees**, run the upgrade via the Bash / shell tool the client provides. The exact command lives in `update.command` (default `npx genexus-mcp@latest init`); pass the user's KB and GeneXus paths from `whoami.kb.path` and `whoami.geneXus.installationPath` if running the non-interactive form: `npx genexus-mcp@latest init --kb "<kb>" --gx "<gx>"`.
- **Then tell the user to fully restart the AI client.** The gateway can't hot-reload itself (it's the process the client spawned); the new binaries are picked up on the next launch. `update.restartRequired` is the explicit signal.
- **Do not auto-update without asking.** Installs touch the user's MCP client config and the user expects to see the upgrade prompt before paths change.
- **Don't nag.** Mention the available update once per session, not on every tool call. The cached `checkedAt` is your hint — if it's the same value as a few turns ago, the user has been told.

### When the update check is disabled

Set environment variable `GENEXUS_MCP_NO_UPDATE_CHECK=1` to disable the background check entirely. Some corporate networks block GitHub API; in those cases `update` returns `{currentVersion, updateAvailable: false, note: "no update-check yet ..."}` and the LLM should respect the absence and not pester.

> All runtime environment variables (HTTP token, GAM credentials, AI-completion proxy, build-path and diagnostic knobs) are catalogued in [`docs/environment_variables.md`](docs/environment_variables.md).

## Tool playbook — v2.6.6 additions

Discoverable via `tools/list`; full schema in `src/GxMcp.Gateway/tool_definitions.json`. Each entry below is a 2-3 line orientation for the LLM agent.

- **`genexus_lifecycle action=status wait=<sec> since=<baseline>`** — event-driven progress. Worker blocks on the task's `ManualResetEventSlim` and returns the moment the state transitions out of `baseline` (or `wait` seconds elapse). Replaces 1-2s polling loops.
- **`genexus_history action=restore discard=true target=<obj>`** — IDE-parity Discard-changes. Restores the part bytes from the most recent `EditSnapshotStore` entry under `.gx/snapshots/`; no commit / rollback / VCS round-trip. Envelope surfaces `restoredFrom` (timestamp + snapshot path).
- **`genexus_preview action=run`** — F5 launcher. Resolves the KB's startup object via `KbService.GetLauncherObjectName` (`StartupObject` env property → `DefaultObject` fallback) and opens it in the headless bridge. No `target` argument required.
- **`genexus_analyze mode=parent_context target=<webpanel>`** — popup-vs-standalone classification. Returns `{ openedAs: "popup"|"standalone", hint }` so the agent knows whether the panel was generated for `genexus_create_popup` or as a top-level screen. The same `popupHint` is inlined into the create_popup response so both sides agree on the first call.

## Tool playbook — v2.6.9 additions (Wave 3 + Futures-promoted)

These 18 tools graduated from `DEFERRED`/`Future` stubs into live services. Each is a 2-line orientation; full schema lives in `tool_definitions.json`.

- **`genexus_tutorial step=N`** — static 6-step walkthrough (orient → list → inspect → read → edit → build). Call once on a fresh session; returns `{title, narrative, suggestedCall, next}`. No KB state.
- **`genexus_watch_event target=<obj> event=<name>`** — filters the in-memory `OperationTracker` for runs against `target` whose payload mentions `event`. Returns `{runs:[…]}`. Not a runtime breakpoint; resets on gateway restart.
- **`genexus_learning action=report`** — aggregates `.gx/friction.jsonl` (written by `genexus_friction_log`) into `{totalEntries, byTool[], byCode[], severityHistogram}`. Read-only; pair with `genexus_friction_log action=tail` for raw lines.
- **`genexus_sd_panel action=inspect|create|edit name=<sdpanel>`** — type-locked SDPanel entry for mobile-first agents. Rejects non-SDPanel targets. Returns the underlying envelope tagged `kind="SDPanel"`.
- **`genexus_multi_agent_lock action=acquire|release|status target=<obj> ownerId=<id>`** — advisory file lock per `(kbPath, target, part)` under `.gx/locks`. Auto-expires after `ttlSec` (default 300, max 86400). Use before edits when multiple agents may collide.
- **`genexus_what_if change={kind,target,attribute,oldType,newType}`** — read-only impact preview. Walks `ImpactAnalysis` callers + source-substring scan; cross-family type swaps (Numeric ↔ Character) flagged as breaks. Returns `{breaks[], probably_safe[], unknown[]}`.
- **`genexus_voice transcript="add button called Save"`** — maps NL transcripts to a suggested tool call via built-in regex intents (add button, rename X to Y, build all, screenshot X, list transactions, …). Returns `{matched, dispatchedTool, dispatchedArgs}`. Agent confirms; no dispatch.
- **`genexus_ai_complete context=<prompt>`** — forwards the prompt to an OpenAI-compatible chat endpoint (env `GXMCP_AI_COMPLETE_URL`/`_KEY`/`_MODEL`). Returns `{completion, tokensIn, tokensOut, model}` or `{code:"AiEndpointNotConfigured"}` when env unset.
- **`genexus_time_travel name=<obj> at=<ISO-or-sha>`** — recovers an object's part bytes from a past git commit. `at=<ISO>` resolves to most-recent commit ≤ timestamp; `at=<sha>` is direct. Read-only; returns `{recoveredFromCommit, parts:[{path,content}]}` or `KbNotInGit`.
- **`genexus_auto_test action=generate_from_prod_log path=<jsonl>`** — reads `{atUtc,tool,target,params}` lines, dedupes by tool×target, emits GXtest stub source. Returns `{linesRead, stubsGenerated:[{name,source}], skipped[]}`. Nothing written to the KB.
- **`genexus_reverse_pattern action=infer source=[X,Y,…]`** — intersects variables / events / parm-signatures across ≥2 objects to flag pattern candidates. Returns `{commonVariables[], commonEvents[], commonParmSignature, divergencePoints[], hint}`. Does NOT emit a real pattern.
- **`genexus_cross_browser target=<webpanel>`** — resolves the runtime URL once, then renders in chrome (via `chrome-devtools-axi`) and firefox/webkit (via `npx playwright`) in parallel. Returns `{url, results:[{browser,ok,screenshotPath,consoleErrors,ms}], anyFailed}`. Per-browser `{skipped:true,code:"BrowserDriverUnavailable"}` when a driver is missing.
- **`genexus_rename_across_kb from=<name> to=<name> type=Attribute?`** — KB-wide rename. Patches every call-site found via the index's `CalledBy` edges by routing through `RefactorService.Rename{Object|Attribute}`. Returns the standard refactor envelope with patched-site count.
- **`genexus_kb_diff kbA=<alias-or-path> kbB=<alias-or-path>`** — gateway-side filesystem diff between two KB roots. Walks `Objects/<Type>/<Name>/`; no SDK touch. Returns `{onlyInA[], onlyInB[], modified[]}`. For SDK-level inspection use `genexus_inspect`.
- **`genexus_worker_pool action=warm_spares spareCount=N`** — gateway lifecycle knob: pre-spawns N warm workers bound to declared KBs so the first KB-bound call doesn't pay cold-start. `spareCount<=0` disables; capped at 5. Returns `{status, spareCount, configured}`.
- **`genexus_sandbox action=create from=<alias> name=<id>`** — gateway-side filesystem clone of a KB to `<configRoot>/sandboxes/<name>/` for throwaway edits. `action=remove` is idempotent; `overwrite=true` replaces. Returns `{status, path, filesCopied, bytesCopied, alias}`. Open the sandbox with `genexus_kb action=open path=<path> alias=sandbox-<name>`.
- **`genexus_github action=create_pr title=<t> body=<b>`** — shells out to the `gh` CLI from the KB path (or `workingDir`). Returns `{status, url}` on success, `{code:"GhCliNotInstalled"}` when the CLI is missing, or `{code:"GhExitNonZero", exitCode, stderr}`.
- **`genexus_kb_import from=<alias> name=<obj> type=<TypeName>`** — gateway-side filesystem copy of one object's part files from another KB into the active KB. Dependencies NOT resolved. Returns `{status, filesCopied, targetDir}`. Must run `genexus_lifecycle action=index force=true` afterwards to make the imported object discoverable.

## Tool playbook — SDK-endpoint expansion (P0/P1/P2 + one P3)

Ten SDK capabilities wired from `docs/sdk_uncovered_endpoints_2026-07-20.md` (see `docs/sdk_endpoints_roadmap.md`). Several wrap services **not registered in the headless worker** — resolved by constructing the public concrete impl directly (`SdkServiceLocator.ConstructOrResolve`, see `reference_headless_service_registration_wall` and the roadmap's "wall" table). All live-verified over HTTP on a real KB.

- **`genexus_transfer action=export|inspect|import`** — real XPZ over `IKnowledgeManagerService` (dependency-aware, unlike the fs-copy `genexus_io`/`genexus_kb_import`). `export` needs `targets[]`+`outputFile`; `inspect file=<xpz>` explores without importing; `import` is destructive — `dryRun` defaults true (previews via ExploreExport), `dryRun=false` needs `confirm=true`.
- **`genexus_deploy action=list_targets|deploy`** — `IDeploymentService`/`IDeploymentTargetService`. `list_targets` (default, read-only) enumerates configured targets (AWS EB, Tomcat, IIS, …); `deploy` needs `confirm=true`.
- **`genexus_security action=scan_native`** — the SDK's own Security Scanner (`ISecurityScannerService.Scan` via a `SecurityScanPlan.GetForModel` plan + an `IScannerOuput` collector). Distinct from `scan_secrets` (regex) and `audit_gam` (env props).
- **`genexus_analyze mode=kb_stats`** — last object/table change + last reorg + derived `reorgLikelyNeeded` (`IModelInformationService`); optional per-object-type operation history with `typeGuid`.
- **`genexus_analyze mode=table_relations name=<Transaction>`** — associated table, other transactions on it, and redundant / possibly-redundant attributes (`ITablesService`; table read via `transaction.Structure.Root.AssociatedTable`).
- **`genexus_db action=reorg_impact`** — cheap timestamp heuristic by default; `deep=true` runs `ISpecifierService.ImpactDatabase` (specification, build-heavy). For the DDL SQL use `sql_ddl`.
- **`genexus_gxserver action=pipeline_list|pipeline_runs|pipeline_output|pipeline_run|pipeline_abort`** — CI pipelines over `IContinuousIntegrationService` on a GXserver-linked KB. `pipeline_run`/`pipeline_abort` need `confirm=true`; off a linked KB returns `{connected:false}`.
- **`genexus_gxserver action=ignored`** (+ `ignoredForCommit` on `action=pending`) — the IDE's Commit > "Ignored Objects" set. Read from each object's `ModelEntityOutput` type `505` in the design model (the marker the IDE writes for "Add to 'Ignored Objects'"); the Common `ITeamDevClientService` has no ignore read and the UI.Framework one won't resolve headless. Full reverse-engineering + reproducible capture recipe: [`docs/teamdev_commit_ignore_505.md`](docs/teamdev_commit_ignore_505.md).
- **`genexus_layout action=list_controls`** — control-definition catalog (user controls + built-ins) via `IUserControlsManagerService`.
- **`genexus_layout action=design_system [name=<DSO>]`** — a Design System Object's token groups, theme classes, images and referenced DSOs (`DesignSystemHelper`). Omit `name` to use the first DSO. Read-only (DSO write ops exist in the SDK but aren't wired).
- **`genexus_create action=curl_procedure name=<Proc> curl="curl …"`** — scaffold a REST-consumer Procedure from a curl command (`ICurlGeneratorService`, IDE "Import from cURL").

## Authoring notes (issue #30)

### API-object routing grammar

`genexus_create type=API` scaffolds a REST API object; the Source is a route table
the GeneXus specifier parses. The grammar (reverse-engineered against GeneXus 18.0.7):

```
Verb { <route> => <Object>; <route2> => <Object2>; }
```

- Routes on the LHS are **bare identifiers** (not quoted string literals).
- The mapped object is on the RHS, separated by `=>` (not `:`), each rule `;`-terminated.
- **One HTTP-verb block per API object.** A second top-level verb block fails at
  spec (`Get {…} Post {…}` → `mismatched input 'Post' expecting <EOF>`), and a
  `@Post`/`@Get` decorator inside a block fails (`mismatched input '@'`). This is
  the GeneXus grammar itself, not an MCP restriction — the MCP does not rewrite it.
- To expose multiple verbs on the same resource, use **per-procedure REST**
  (set the Procedure's `REST=True` / `Expose as ...`) reached at `<app>/rest/<ProcName>`,
  rather than trying to mix verbs in one API object.

### Folder / module placement (SDK-backed since v2.35.0)

Objects **can** be placed into a Folder or Module through the tools — the same
operation as KB Explorer drag-and-drop / right-click → Move in the IDE.

- **Move an existing object:** `genexus_properties action=move name=<obj>
  destination=<Folder or Module>`. The container kind is auto-detected; pass
  `destKind=Folder|Module` only to disambiguate a shared name. `dryRun=true`
  previews `from` / `to` without writing.
- **Write-back is verified.** The parent is re-read after the save, so a write
  the SDK silently drops returns `MoveNotPersisted` rather than a false success.
- **Create directly in a container:** `genexus_create … folder=<name>` or
  `module=<name>` creates the object in Root Module and then moves it, reporting
  the outcome under `placement`.
- **`action=set` on a placement property is routed, not rejected.**
  `propertyName=Folder|FolderId|FolderGuid|Module|ModuleId|Parent|ParentKey|ParentId`
  is recognised as a reparent (`PropertyService.IsObjectPlacementProperty`) and
  dispatched to `ObjectService.MoveObject` instead of a scalar property write.
- **`list` / `inspect` re-file immediately** — `IndexCacheService.InvalidateHierarchy`
  drops the per-Guid hierarchy cache and the old parent's `ChildrenByParent` slot
  before the index entry is updated.

Create the containers themselves with `genexus_create type=Folder|Module`.

> **Why the SDK looks like it can't do this.** `KBObject`'s `Parent` / `ParentKey` /
> `Module` setters *do* read as empty stubs at the IL level — but only in the
> facade/reference assembly. Decompiling that assembly is what produced the
> earlier "placement is unsupported" verdict. The runtime persist goes through
> `Artech.Udm.Framework.EntityManager.SaveWithParent` (see
> `src/GxMcp.Worker/Helpers/ObjectMover.cs`, with `UpdateParent` and `KBObject.Save`
> as fallbacks). Don't re-derive the old conclusion from a reference-assembly
> decompile.

### Control-bound events must be written AFTER the layout

A control-bound event (`Event &Ctrl.Click`, `&Ctrl.ControlValueChanged`, `&Ctrl.Display=…`)
references a control that must already exist in the projected form, or the SDK rejects
it with `src0233`/`src0216`. Write the layout / apply the PatternInstance **first**, then
the Events part. Also: a `userAction name="Foo"` auto-generates an empty `Event 'DoFoo'`
stub — **fill that stub**, don't add a second `Event 'DoFoo'` (that collides with
`src0208 "event already defined"`). Write errors carrying these codes now surface the
actionable hint inline; see `WritePolicy.BuildEventDiagnosticHint`.

### SDPanel (Smart Device Panel) parts are WorkWithDevices projections (issue #29)

An `SDPanel` is not a plain object with self-contained parts — it's driven by the
WorkWithDevices pattern. Its parts are `Artech.Patterns.WorkWithDevices.Parts.Virtual*Part`
projections, and their GUIDs differ from the Web equivalents (leading hex nibble masked,
e.g. Web Events `c44bd5ff-…` → SD Events `144bd5ff-…`). Consequences the tools now handle:

- **Readable:** `SDEvents` (the panel's event code) and `SDRules` are `ISource` virtual
  parts and read fine. They are surfaced in `availableParts`, and `part=Source`/`Events`
  resolves to `SDEvents` (previously it hit `SDRules`, which is almost always empty — the
  source of the "reads empty" report). `part=SDEvents` / `part=SDRules` also work by name.
- **Not extractable:** `SDLayout`, `SDVariables`, `SDConditions` are non-`ISource` virtual
  parts. `SerializeToXml()` returns an empty `<Properties />` even when the panel has
  content, because the data is projected from the pattern, not stored on the part. Reading
  one now returns `projected:true` + a `note` explaining this — an empty result here does
  **not** mean the panel is empty. Layout/variables are authored in the GeneXus IDE.

## Release discipline

> **HARD RULE — releases require the maintainer's explicit go-ahead, every time.**
> Do **not** run `./release.ps1`, create a tag, or publish a GitHub Release
> because you judged the work "done" or "ready to ship". A release happens
> **only** when the maintainer explicitly says to release *this* change (e.g.
> "sobe a 2.26.1", "pode soltar", "release it"). Implementing, building, and
> testing a change is authorized by the task; **shipping it is a separate,
> explicit decision that is the maintainer's alone.** Finishing the code is not
> permission to release — when in doubt, stop after tests pass and ask. Approval
> for one release never carries to the next.

- Before any release (`./release.ps1`, tag, or GitHub Release), update
  `CHANGELOG.md` with an entry for the exact version being released.

### One-shot release command

Cutting a release is a single command — `./release.ps1` handles version
bumps, build, zip, commit, tag, push, and `gh release create` (with
`publish.zip` attached **in the same API call** as create):

```powershell
.\release.ps1 -Version 2.6.9         # full bump → build → ship
.\release.ps1                        # no version bump; use current package.json
.\release.ps1 -Version 2.6.9 -DryRun # rehearse without touching origin
```

**Don't** run `gh release create` by hand. The workflow at
`.github/workflows/release.yml` expects a `publish.zip` asset on the
release; creating without the asset publishes a release that the
workflow fails on with `publish.zip missing` (the script attaches it in
one call so the workflow's first `release.published` event succeeds).

The Worker can't build on GitHub-hosted runners (it references Artech.\*
DLLs from a local GeneXus 18 install which isn't on `ubuntu-latest`),
so the zip has to be produced on a Windows machine with GeneXus
installed. `release.ps1` does this.

### npmjs.com webpage lag after publish

After `release.ps1` finishes and the workflow turns green, the package
is live on the npm **registry** immediately:

```powershell
npm view genexus-mcp version            # → 2.6.8 right away
npm view genexus-mcp dist-tags --json   # { "latest": "2.6.8" }
npm install -g genexus-mcp@latest       # gets 2.6.8
```

The npmjs.com **website** (`npmjs.com/package/genexus-mcp`) is served
from a separate CDN that caches the rendered page and **can lag the
registry by 10–30 minutes**. The right-sidebar "Version" label and the
"Published N hours ago" line can still show the previous version even
when the README badge (`shields.io`, queries the live registry) already
shows the new one. This is a known npmjs.com UI quirk, not a publish
failure. Don't re-cut the release; just wait or verify via
`npm view` / `registry.npmjs.org/genexus-mcp/latest`.

When a user reports "still on old version after install", the actual
fixes (in order) are:

1. `where.exe genexus-mcp` — multiple matches mean an older install
   (e.g. from `install.ps1` build-from-source) is masking the npm one.
   Remove the non-npm copy from `PATH`.
2. `npm cache clean --force && npm uninstall -g genexus-mcp && npm install -g genexus-mcp@<version>` — pins past any cached metadata.
3. Confirm `genexus-mcp doctor` reports the expected version.

### CHANGELOG voice — release-facing, not roadmap-internal

**HARD RULE.** When this rule and any AI-agent generation reflex conflict,
this rule wins. Re-edit until each bullet survives a "would the end user
care?" reread. The same rule is mirrored at `~/.claude/CLAUDE.md` so it
applies to every project that ships a CHANGELOG.

Entries in `CHANGELOG.md` are read by users on GitHub Releases / npm /
package pages — they should describe **what the user gets**, not how the
sausage was made. Follow these rules:

- **Lead with user-facing capability**, not internal nomenclature. "**`genexus_preview`** — render a WebPanel via headless Chrome..." not "**W4 — Render preview implementation**".
- **No roadmap / workstream codes** (W1, W2, FR#3, SP4.T5, etc.) in the user-facing portion. Cross-reference docs in a single line at the top of the version (`See docs/mcp-roadmap-ide-parity.md for design context.`) if relevant; never sprinkle codes through the bullets.
- **No internal-only context** in user-facing bullets: friction-report cross-references, session narratives, code-archeology asides, "post-roadmap status" tables, agent IDs, commit hashes. Keep those for `docs/` and PR descriptions.
- **Use the four standard sections** (in this order, omit unused ones): `### Added`, `### Fixed`, `### Changed`, `### Removed`. Plus `### Internal` at the **bottom** for engineer-only notes (test counts, schema-budget bumps, internal helper renames, fixture regen instructions).
- **One bullet per capability**, lead bold-name (tool / class / behavior), then 1–4 sentences of plain English. No CLR type dumps in the user-facing copy — link them under `### Internal` if needed.
- **Concrete example values** when they aid comprehension (`"AttributeBlue"`, `Class="…"`), not opaque GUIDs unless the bug was about GUIDs.
- **Past tense for fixes** ("Raw-XML writes that emitted `OnClickEvent=…` were silently ignored…"); imperative-or-present for new features ("Apply a GeneXus pattern… ").
- **Don't reference KB-specific names** (Maria Daiane, AcademicoHomolog1, dani.aspx) in the changelog. The release goes out to everyone; their KB has different objects.
- **Don't claim test counts in the user-facing section.** Test counts and skipped-test caveats go under `### Internal`.

Compare these two takes on the same fix:

> ❌ Roadmap-internal voice
> #### W1 — SDK-routed layout writes (gxButton OnClickEvent fix)
> **`gxButton` custom `OnClickEvent` now wires correctly in WebForm-html.** Friction-report 2026-05-19 #1 root cause: the SDK maps the descriptor name `OnClickEvent` to a per-element XML attribute (gxButton → `Event`, gxAttribute/gxImage → `eventGX`). Raw-XML writes that emit `OnClickEvent=` literally are silently dropped by the HTML generator. Fix: `WebFormTypedPropertyWriter.ApplyDescriptorPathFixup(part)` — post-write hook that walks every IWebTag and routes any descriptor-name attribute through `Artech.Common.Properties.PropertiesObject.SetPropertyValue` / `SetPropertyValueString` via reflection.

> ✅ Release-facing voice
> ### Fixed
> **`gxButton OnClickEvent` for custom events.** Raw-XML writes that emitted `OnClickEvent="'MyEvent'"` were silently ignored by the HTML generator, which only reads the per-element XML attribute the SDK assigns (`Event` for `gxButton`, `eventGX` for `gxAttribute` / `gxImage`). The MCP now routes descriptor-named properties through the SDK's typed property API so the canonical XML attribute is emitted. Applies on every layout save; idempotent.

When in doubt, re-read the entry as if you were a developer who just installed the package and is wondering what changed — would they care about this sentence? If not, demote to `### Internal` or delete.
