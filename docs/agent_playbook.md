# Genexus18MCP Agent Playbook

Detailed, task-specific guidance for agents working on this repository. The
short project rules and navigation pointers live in `AGENTS.md`; read the
relevant section here when a task touches the corresponding behavior.

## Full Source save verification

For `genexus_edit mode=full part=Source`, send the last complete read's
`expectedVersion` (or `baseVersion`) and an explicit `verifyMode`. A full read
with `limit=0` preserves every SDK character, including EOLs and the final
newline. Post-save verification invalidates caches, resolves the same object
through the public-read route, and uses that same complete representation.
A missing/freshness-unconfirmed read is unknown evidence, never a text mismatch.

`verifyMode=exact` compares ordinal text; EOL and module-qualification changes
remain differences, with typed reasons and bounded expected/read line previews.
The older patch exact contract still treats CRLF/LF as logically equivalent.
`sdkSaveCompleted` and `saved` describe physical save completion independently
from `persisted`/`verified` (requested text confirmed) and `persistedStateKnown`
(reliable reread). Unknown save completion is null. `postSaveVerification`
identifies `representation=genexus_read`, matches and the observed versionToken.
Async result polling retains this evidence even on error. Do not retry a write
because verification failed; obtain a new complete read and version first.

Dry runs do not save. No implicit lifecycle action or forceWrite is added.
Full Source rollback does not perform a second write without an atomic
version-conditional SDK restore: a requested restore returns
`rollback.reason=AtomicRollbackUnavailable`, `attempted=false`, preserving the
snapshot and observed state for explicit recovery. If the reread already equals
the snapshot, no save is required. This prevents a newer concurrent edit from
being overwritten by an unsafe best-effort restore.
Full Source `requireObjectSave=true` checks `objectSaved` emitted only after the
SDK object save and transaction commit return; missing evidence returns
`ObjectSaveIncomplete`. An unchanged source remains a no-op without a new save.
Non-exact full writes retain existing SDK casing/module-qualification tolerance.
The Events patch isolation
block remains in effect. See [#265](https://github.com/lennix1337/Genexus18MCP/issues/265).

## Formal legacy configuration migration

Migration is explicit and copy-based; startup commands do not alter legacy configuration. Run:

```text
genexus-mcp config migrate --from <legacy.json> --output <neutral.json> --format json
```

The source is preserved and copied to an atomic `*.pre-migrate*.bak` backup. The
output is a KB-free neutral runtime config and is verified by read-back. If an
existing output cannot be verified, it is restored atomically and the receipt
reports `rollback.rolledBack=true`. Use `--reject-non-migratable` to reject
rather than report legacy KB fields that cannot move to a neutral runtime.

`kb add`, `kb remove`, and `kb switch` intentionally retain their legacy
meaning and destination: they update `Environment.KBs`, `Environment.KBPath`,
`Environment.ActiveKb`, and `Environment.DefaultKb` in the config selected by
`GX_CONFIG_PATH` or `config.json` in the current directory. They are not aliases
for neutral MCP session selection. For a neutral config, select the KB explicitly
through the MCP session action; do not expect `init` or `clients add` to migrate
or rewrite a config as a side effect.

## Engineering safeguards

### Adding a mutating tool

Any new tool that mutates KB state must be registered in
`Program.IsMutatingTool` (`src/GxMcp.Gateway/Program.ToolPayload.cs`). Otherwise
the semantic cache can replay stale reads until the gateway restarts.

The cache stores the first successful read per `(kbAlias, tool, args)` and a
mutation clears the whole cache. Name-substring verbs cover edit/create/refactor/
variable tools; action-gated tools (`gxserver`, `transfer`, `structure`, `db`,
`lifecycle`, `properties`) need explicit action checks against
`src/GxMcp.Gateway/tool_definitions.json`. Extend
`SemanticCacheInvalidationTests` with the new mutating tool and its read-only
counterparts.

### Proving SDK fixes against a real KB

Unit tests are not enough for SDK behavior. For a controlled Streamable HTTP
validation cycle:

1. Build Gateway and Worker to `bin/Debug`.
2. Write a temporary config with an unused HTTP port, `McpStdio: false`, the
   debug Worker executable, and a scratch KB path.
3. Launch `GxMcp.Gateway.exe` with `GX_CONFIG_PATH` set. A detached launcher can
   time out while the child continues to serve; verify the port separately.
4. POST `initialize` to `/mcp` with
   `Accept: application/json, text/event-stream`; reuse `MCP-Session-Id`.
   Tool text is JSON-in-JSON in `result.content[0].text`. For single-tool
   probes, `scripts/mcp-probe.ps1 -Tool <name> -Arguments '<json>'` wraps
   this handshake (session id persists in a temp file across invocations).
5. Exercise the actual flow. For persistence claims, kill and relaunch the
   gateway before reopening the KB so the semantic cache cannot mask a failure.
6. Delete scratch objects, close the KB, stop only the scratch gateway, and
   remove temporary files. Never kill a user's running npm/stdio gateway.

For SDK compatibility work, build the Worker once per installed major by
setting `GX_PATH` explicitly, for example `GeneXus17Trial` and `GeneXus18`.
The Gateway's `whoami.geneXus.supportedMajors` is the explicit runtime catalog;
add a new major to `config/gx-versions.json` only after its Worker build and
live-KB smoke path pass.

When a verified fixture is available, use
`scripts/test-live-matrix.ps1` to exercise the same built artifact once per
selected catalog major. It accepts `-Majors` and `-GxPathMap`, writes a
`gxmcp-live-matrix/1` summary, and treats unavailable SDK/license/fixture
environments as explicit non-passing evidence. The detailed invocation and
fixture contract live in [`live-kb-test-harness.md`](live-kb-test-harness.md).

### GeneXus SDK Model hierarchy, facades, and reflection

- **Canonical GxModel Facade:** Any `KBModel` instance can be cast to the canonical
  `Artech.Genexus.Common.GxModel` via `kbModel.GetAs<GxModel>()` or `new GxModel(kbModel)`.
  This provides direct typed access to the environment's primary active generator
  (`gxModel.Generator` as `GxGenerator`), active datastore (`gxModel.DataStore`), web path
  (`gxModel.WebTargetFullPath`), and configured generators collection (`gxModel.Generators`).
- **Environment Models vs. Design Model:** A Knowledge Base contains a single
  conceptual root model (`kb.DesignModel`, `ModelType.Design`, physical folder `Data001`)
  from which target environments branch. Target environments are executable models
  (`ModelType.Prototype`, `ModelType.Production`). Operations enumerating or targeting
  environments (`genexus_kb action=list_environments|set_environment`) must always
  exclude `DesignModel` (identified by `ModelType.Design`, `ReferenceEquals`, or `Id == DesignModel.Id`).
- **Ambiguous properties in reflection:** In GeneXus SDK inheritance trees, derived
  classes frequently shadow base entity properties with `new` (for example, `KBModel.Type`
  declares `ModelType Type`, while base `Entity.Type` declares `Guid Type`). Calling
  `Type.GetProperty("Type", BindingFlags.Public | BindingFlags.Instance)` throws .NET's
  `AmbiguousMatchException`. Always use `ReflectionHelper.TryGetMember` to traverse
  `DeclaredOnly` properties from the most-derived type upward.

### Windows shell and process gotchas

- In Bash-on-Windows, put the whole PowerShell command in single quotes so
  `$env:VAR` and `$_` reach PowerShell unchanged.
- When running PowerShell commands with `pwsh -Command` on Windows, wrap multiline
  scripts or blocks containing variables (`$var`, `$_`) in single-quoted here-strings
  (`@' ... '@`) or write transient scripts to `scratchpad/` to avoid premature shell
  variable interpolation and parser errors.
- Native Windows Python cannot read `/tmp/...`; convert paths with `cygpath -w`
  or use the real Windows temporary path.
- Git Bash passes `taskkill` switches as `//PID` and `//F`.
- There is no `head`/`tail` on Windows shells; use `Select-Object -First/-Last`
  instead of piping through Unix names.
- `python3 -c` scripts come back empty without an error — multiline or with
  nested quoting alike — always put local Python in a script file (e.g.
  `scratchpad/`) instead of `-c`.
- `fc` resolves to `Format-Custom` in PowerShell; call `fc.exe` for byte compares.
- Long-lived children can keep the shell pipe open after a successful spawn;
  treat the timeout as expected and verify the process/port separately.
- `git merge-tree` and `git commit-tree` can simulate merges without touching
  the working tree.

### Worker reload and known flakes

After editing Worker code, reload without restarting the MCP client with
`genexus_worker_reload mode=hard sourceDir=<repoRoot>\src\GxMcp.Worker\bin\Debug`.
Use `genexus_worker_reload mode=soft force=true` only when the Worker is wedged.
The gateway pipe can become stale after reload; reconnect `/mcp` once if the
next call reports a crashed or exited Worker.

The following are known flaky in parallel runs and should be reproduced in
isolation before being treated as regressions:

- `EdgeCaseRegressionTests.Dispatcher_PatchApply_ValidateOnly_MapsToDryRun_ViaConvention`
- `PatternApplyServiceTests.*`

### `Nexus IDE checks` blocked by a VS Code update

If the `Nexus IDE checks` preflight phase fails with `Code is currently being
updated`, the Electron test runtime refused to start because a VS Code update is
holding the Inno Setup mutex on this host. This is host state, not a repository
regression: finish the update (close every VS Code window and let the installer
complete) and re-run, passing `-ResumeSummaryPath <previous summary>` to reuse
the phases that already passed.

The phase is classified in `scripts/release-preflight.ps1` from its own captured
output and records the cause and remediation on the phase record
(`details.code = 'vscode-update-in-progress'`, `details.remediation`, and
`details.mutexConfirmed`). It stays `failed` on purpose: the preflight aggregate
accepts `unavailable` as an approved terminal status, so downgrading the phase
would certify a release that ran no Electron test at all. A consumer may read
the label, never treat it as an outcome.

The confirmation probe is Windows-only and resolves the mutex from the same
runtime the test launches, following `src/nexus-ide/src/test/runTest.ts`:
`NEXUS_IDE_VSCODE_VERSION` (default `1.111.0`), cache
`src/nexus-ide/.vscode-test`, and the **top-level** `win32MutexName` of
`resources/app/product.json` in `vscode-win32-x64-archive-<version>\<hash>\`.
The key also appears inside the `embedded` object for the Sessions sub-product
and is deliberately ignored. When the runtime cannot be resolved the probe
reports unconfirmed rather than guessing.

## Live tool playbook

The authoritative schemas remain in `src/GxMcp.Gateway/tool_definitions.json`.
These notes explain when an agent should reach for the newer capabilities.

### Lifecycle and authoring helpers

- **`genexus_lifecycle action=status wait=<sec> since=<baseline>`** — waits for a
  state transition instead of polling.
- **`genexus_history action=restore discard=true target=<obj>`** — restores the
  latest `EditSnapshotStore` part snapshot without a VCS round-trip.
- **`genexus_preview action=run`** — launches the KB startup object (`StartupObject`
  then `DefaultObject`) through the headless bridge.
- **`genexus_analyze mode=parent_context target=<webpanel>`** — distinguishes a
  popup from a standalone panel; `genexus_create_popup` returns the same hint.
- **`genexus_tutorial step=N`** — returns the six-step orient/list/inspect/read/
  edit/build walkthrough.
- **`genexus_watch_event target=<obj> event=<name>`** — filters in-memory
  `OperationTracker` runs; it is not a persistent breakpoint.
- **`genexus_learning action=report`** — aggregates `.gx/friction.jsonl`; pair
  with `genexus_friction_log action=tail` for raw entries.
- **`genexus_sd_panel action=inspect|create|edit name=<sdpanel>`** — type-locked
  SDPanel operations tagged `kind="SDPanel"`.
- **`genexus_multi_agent_lock action=acquire|release|status target=<obj> ownerId=<id>`**
  — advisory `.gx/locks` coordination with a default five-minute TTL.
- **`genexus_what_if change={kind,target,attribute,oldType,newType}`** — read-only
  impact preview; Numeric/Character family changes are flagged as breaks.
- **`genexus_voice transcript="..."`** — maps a natural-language transcript to
  a suggested call but does not dispatch it.
- **`genexus_ai_complete context=<prompt>`** — uses the configured
  OpenAI-compatible completion endpoint, or returns `AiEndpointNotConfigured`.
- **`genexus_structure action=get_visual name=<target> type=Transaction`** —
  disambiguates Transaction from same-named Table or WebPanel homonyms. Write actions
  (`remove_attribute`, `move_attribute`, `update_visual`) are never retried across
  worker crashes.

### History, testing, patterns, and cross-KB work

- **`genexus_time_travel name=<obj> at=<ISO-or-sha>`** — recovers part bytes from
  a historical Git commit; returns `KbNotInGit` when appropriate.
- **`genexus_auto_test action=generate_from_prod_log path=<jsonl>`** — emits
  GXtest stubs from deduplicated production log lines without writing the KB.
- **`genexus_reverse_pattern action=infer source=[X,Y,…]`** — intersects at
  least two objects to identify common variables, events, and parm signatures;
  it does not create a pattern.
- **`genexus_cross_browser target=<webpanel>`** — renders the resolved URL in
  available Chrome and Firefox/WebKit drivers and reports per-browser results.
- **`genexus_rename_across_kb from=<name> to=<name> type=Attribute?`** — routes
  indexed call sites through the SDK refactor service.
- **`genexus_kb_diff kbA=<alias-or-path> kbB=<alias-or-path>`** — compares KB
  object folders on disk without opening the SDK.
- **`genexus_worker_pool action=warm_spares spareCount=N`** — pre-spawns up to
  five workers for declared KBs; non-positive values disable it.
- **`genexus_sandbox action=create|remove from=<alias> name=<id>`** — clones a
  KB under the config sandbox directory for throwaway edits.
- **`genexus_github action=create_pr title=<t> body=<b>`** — invokes `gh` from
  the KB/working directory and returns a URL or structured CLI error.
- **`genexus_kb_import from=<alias> name=<obj> type=<TypeName>`** — copies one
  object's files without dependency resolution; run
  `genexus_lifecycle action=index force=true` afterward.

### SDK endpoint expansion

- **`genexus_transfer action=export|inspect|import`** — dependency-aware XPZ
  transfer through `IKnowledgeManagerService`; import is destructive and needs
  `dryRun=false confirm=true`.
- **`genexus_deploy action=list_targets|deploy`** — lists deployment targets;
  deployment needs `confirm=true`.
- **`genexus_security action=scan_native`** — runs the SDK Security Scanner,
  distinct from regex secret scanning and GAM auditing.
- **`genexus_analyze mode=kb_stats`** — reports last object/table changes,
  reorganization state, and optional per-type operation history.
- **`genexus_analyze mode=table_relations name=<Transaction>`** — reports the
  associated table, related transactions, and redundant attributes.
- **`genexus_db action=reorg_impact`** — uses a cheap timestamp heuristic unless
  `deep=true`, which runs the build-heavy database impact service.
- **`genexus_gxserver action=pipeline_list|pipeline_runs|pipeline_output|pipeline_run|pipeline_abort`**
  — manages CI pipelines on a GXserver-linked KB; run/abort need confirmation.
- **`genexus_gxserver action=ignored`** — reads the IDE's ignored-object marker
  (`ModelEntityOutput` type 505); see `docs/teamdev_commit_ignore_505.md`.
- **`genexus_layout action=list_controls`** — lists built-in and user control
  definitions.
- **`genexus_layout action=design_system [name=<DSO>]`** — inspects DSO tokens,
  themes, images, and references.
- **`genexus_create action=curl_procedure name=<Proc> curl="curl …"`** — creates
  a REST-consumer Procedure from cURL through the SDK service.

## Authoring constraints

### API routing grammar

`genexus_create type=API` accepts one top-level HTTP verb block per API object:

```text
Verb { <route> => <Object>; <route2> => <Object2>; }
```

Routes are bare identifiers, mappings use `=>`, and rules end in `;`. Mixing
top-level verb blocks or adding `@Get`/`@Post` decorators is rejected by the
GeneXus 18 grammar. Expose multiple verbs with per-Procedure REST instead.

### Folder and module placement

- Move an existing object with
  `genexus_properties action=move name=<obj> destination=<Folder-or-Module>`.
  Use `targetModule` or `destKind=Folder|Module` only when needed.
- Use `dryRun=true` to preview the destination. For writes, pass the prior
  `versionToken` as `baseVersion`; the service snapshots parts and authored
  properties, verifies the committed parent/content, and rolls back on failure
  by default.
- Move performs no Specify, Generate, Build, Rebuild, reorganization,
  execution, or tests.
- Create directly in a container with `folder=<name>` or `module=<name>`;
  placement is reported in the response.
- Placement properties passed to `action=set` are routed to the move service.
- After a move, `list`/`inspect` invalidate hierarchy caches immediately.

The containers themselves are created with `genexus_create type=Folder|Module`.
The facade's empty `KBObject.Parent`/`Module` setters are misleading; runtime
persistence goes through `Artech.Udm.Framework.EntityManager.UpdateParent` in
`src/GxMcp.Worker/Helpers/ObjectMover.cs`, with full snapshot verification.

### Control-bound events

Write the layout or PatternInstance before the Events part. A control-bound
event references a control that must already exist, otherwise the SDK returns
`src0233`/`src0216`. A `userAction name="Foo"` creates an empty `DoFoo` stub;
fill that stub rather than adding a second event (`src0208`).

### SDPanel projections

SDPanels are WorkWithDevices projections, not self-contained ordinary parts.
`SDEvents` and `SDRules` are readable virtual source parts; `part=Source` and
`part=Events` resolve to `SDEvents`. `SDLayout`, `SDVariables`, and
`SDConditions` are non-source projections and may serialize as empty properties;
an empty result does not mean the panel is empty. Layout and variables are
authored in the GeneXus IDE.

### Inspection and property conventions

Metadata and property-reading tools (`genexus_properties`, `genexus_variable`, etc.) should follow these conventions:
- **Envelope parity**: single-property reads return `{ propertyName, value, values: { [name]: value }, property, properties: [property], versionToken }`. Multi-property reads return `{ target, values: { [name]: value }, properties: [...], missingProperties: [...] }`.
- **Flat key-value dictionary**: always populate `values` as a flat `{ [name]: value }` dictionary for direct consumption by LLMs and client scripts without requiring traversal of nested array objects.
- **Pattern and query filtering**: support `query` parameter matching both case-insensitive substrings and glob wildcards (`*` and `?`).
- **Projections**: support `projection: "minimal" | "standard" | "full"` to control metadata payload weight (`minimal` emits compact key/value mappings; `full` includes all SDK descriptor flags).
- **Suggestions on missing keys**: when a requested property is missing, compute nearest candidates using Levenshtein distance and return actionable `suggestions` and `nextSteps` in the error/response envelope instead of opaque failures.

