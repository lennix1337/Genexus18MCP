# Environment variables

Reference for the environment variables the GeneXus MCP gateway and worker read
at runtime. There is **no `.env` file loader** — set these in the environment
that launches the AI client (which spawns the gateway), or in the MCP-client
config's `env` block for the server entry.

All are optional. Unset means the documented default applies.

## K2B IDE Designer bridge

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_K2B_IDE_KB` | Exact KB directory allowed in the GeneXus IDE extension. Set before launching the IDE. | unset (extension inactive) |
| `GXMCP_K2B_IDE_PIPE` | Local named pipe shared by the IDE extension and MCP Worker. Set in both processes. | unset (bridge unavailable) |
| `GXMCP_K2B_IDE_TARGET` | Optional WebPanel name restriction for the IDE session. | unset (any open WebPanel in that KB) |
| `GXMCP_K2B_IDE_LOG` | Optional IDE extension diagnostic log path. | unset |

See [K2B IDE Designer bridge](k2b-ide-bridge.md) for installation and the
read-preview-save workflow.

## Update / self-update

| Variable | Purpose | Default |
|----------|---------|---------|
| `GENEXUS_MCP_NO_UPDATE_CHECK` | Set to `1` to disable the background "is a newer version available?" check performed on `initialize`. Useful on networks that block the GitHub API. | check enabled |
| `GENEXUS_MCP_NO_SELF_UPDATE` | Set to `1` to disable the self-update apply path (the check may still run; nothing is installed). | self-update enabled |

## HTTP endpoint

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_HTTP_TOKEN` | Shared secret required on every `/mcp` HTTP request (`Authorization: Bearer <token>` or `X-GXMCP-Token`). Binding to a non-loopback address **requires** this — without it, non-loopback `/mcp` requests are refused. The default `127.0.0.1` bind with no token is unchanged. | unset (loopback-only, no auth) |
| `GXMCP_NO_STRUCTURED_CONTENT` | Set to `1` (or `true`) to omit the MCP `structuredContent` field from tool results — it duplicates the whole payload already present in `content[0].text`, adding ~45% to each response's byte size. Equivalent config: `Server.EmitStructuredContent: false`. Env wins over config. `genexus_lifecycle` is the deliberate exception because it advertises `outputSchema`; successful lifecycle results keep `structuredContent` to satisfy strict MCP clients. | unset (structuredContent emitted) |
| `GXMCP_EMIT_STRUCTURED_CONTENT` | Set to `0` (or `false`) to disable `structuredContent` emission, or `1`/`true` to force it on. Complement to `GXMCP_NO_STRUCTURED_CONTENT`. | unset |
| `GXMCP_TERSE` | Set to `1` (or `true`) for terse responses: omits `next_legal_actions` and the `_meta.tokens` block from tool results, keeping only the payload plus error hints. Equivalent config: `Server.TerseResponses: true`. Env wins over config. | unset (full UX sugar emitted) |
| `GXMCP_PROFILE` | Tool profile for `tools/list` surface (`core`, `authoring`, `devops`, `ui`, `db`, `all`). Lean profiles like `core` (11 tools) or `authoring` (29 tools) drastically reduce initial system prompt tokens vs the full 52-tool surface. | `all` |

## Generated documentation artifacts (`genexus_doc`)

`genexus_doc action=wiki` and `action=visualize` write durable files outside the installed Worker directory. The default root is `%LOCALAPPDATA%\GxMcp\Artifacts`; each open KB gets a stable `kb-<identity>` child with separate `docs` and `html` directories. This per-KB scope is applied even when a custom root is configured, so two KBs cannot silently share `Customer.md` or a graph file. Package upgrades can replace the Worker installation without moving this output.

| Setting | Purpose | Default |
|---------|---------|---------|
| `Server.ArtifactOutputDirectory` | Optional config.json root for generated wiki/HTML files. The Worker adds the per-KB scope below it. | `%LOCALAPPDATA%\GxMcp\Artifacts` |
| `GXMCP_ARTIFACT_OUTPUT_DIR` | Worker-only/environment override when the Worker is launched directly, or when `Server.ArtifactOutputDirectory` is omitted. An explicit Server setting wins when the Gateway starts a Worker. | unset |

The effective path remains in every successful response: wiki returns `result.file` and `result.outputDirectory`; visualize returns `result.url` and `result.outputDirectory`. Visualizer/health read the active KB's canonical `IndexCacheService` snapshot. Generated filenames are validated as single path components; separators and traversal are rejected rather than sanitized.

## Worker runtime files

Worker-owned state, diagnostics, build logs, and temporary output stay outside the executable/package directory. Managed Workers receive an opaque operational-state key from the Gateway so separate KB generations do not share default runtime folders. Build responses keep the same `fullLogPath` contract; only the destination root changes.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_STATE_DIR` | Optional Worker state root (for example preview configuration and soft-reload path hints). The Gateway still persists job snapshots under its own scoped state directory. | `%LOCALAPPDATA%\GenexusMCP\state\<operational-key-hash>` |
| `GXMCP_LOG_DIR` | Optional Worker log root; the Gateway sets a per-operational-state directory for managed Workers. The Gateway's own debug log also honors this variable. | `%LOCALAPPDATA%\GenexusMCP\logs\<operational-key-hash>` |

Temporary Worker diagnostics use `%LOCALAPPDATA%\GenexusMCP\tmp\<operational-key-hash>`. Preview configuration/baselines use the state root; build output and rotated Worker logs use the log root. `GXMCP_BUILD_LOG_RETAIN_COUNT` still controls retention there.

## AI-completion proxy (`genexus_ai_complete`)

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_AI_COMPLETE_URL` | OpenAI-compatible chat-completions endpoint the tool forwards to. | unset → tool returns `AiEndpointNotConfigured` |
| `GXMCP_AI_COMPLETE_KEY` | Bearer key for the endpoint above. | unset |
| `GXMCP_AI_COMPLETE_MODEL` | Model name sent in the request body. | provider/endpoint default |
| `GXMCP_AI_COMPLETE_DEBUG` | Set to `1` to include the raw upstream error body in a failed response (may carry account/billing/request-id detail). Off by default; failures return a length-only breadcrumb. | off |

## GAM credentials (headless preview / login)

Precedence is: tool `auth` argument > these env vars > built-in default.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_GAM_USER` | GAM username for headless authentication. | unset |
| `GXMCP_GAM_PASS` | GAM password. **Secret** — prefer the MCP-client config `env` block over a shell profile. | unset |
| `GXMCP_GAM_LOGIN_URL` | GAM login URL override. | unset |

## Build path (`genexus_edit_and_build` / `genexus_run_object`)

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_INPROCESS_BUILD` | Opt into the in-process build runner. | off |
| `GXMCP_INPROCESS_BUILD_BATCH` | Set to `1` to opt into batched build (opt-in since 2026-05-22). | off |
| `GXMCP_INPROCESS_BUILD_FASTPATH` | Fast-path build variant (benchmark / opt-out lever). | off |
| `GXMCP_BUILD_COMPILE_ONLY` | Compile without the full build pipeline (benchmark / opt-out lever). | off |
| `GXMCP_BUILD_PROFILE` | Select a build profile. | unset |
| `GXMCP_REAP_ORPHAN_MSBUILD` | Reap orphaned MSBuild processes after a build. | off |

## Live KB and release preflight

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_TEST_KB` | Absolute path to the disposable KB used by `scripts/test-live.ps1` and the release preflight. `release-preflight.ps1` auto-selects `C:/KBs/KBTeste` for GeneXus 18 or `C:/KBs/KBTeste17` for GeneXus 17 when this is unset. | unset (local compatible fixture autodetected; live gate skipped when none exists) |
| `GXMCP_TEST_FIXTURE` | Optional fixture attestation JSON matching `GXMCP_TEST_KB`; required for benchmark population comparisons, not normal live validation. | unset |
| `GXMCP_LIVE_MAJORS` | Comma-, semicolon-, or whitespace-separated catalog majors for the live matrix used by `scripts/release-preflight.ps1` and CI. When set, the matrix validates only these majors; the standalone matrix command validates every catalog major when no `-Majors` flag is supplied. | unset (single-major preflight; all catalog majors for standalone matrix) |
| `GXMCP_LIVE_GX_PATH_MAP` | Semicolon-separated `major=absolute-path` overrides for SDK installations used by the live matrix, for example `17=C:\Program Files (x86)\GeneXus\GeneXus17Trial;18=C:\Program Files (x86)\GeneXus\GeneXus18`. | unset (catalog default paths) |
| `GXMCP_TEAMDEV_PENDING_NAME` | Name of a pre-seeded object with an IDE-created Team Development pending change for the opt-in Gateway regression test. | unset (IDE-origin regression skipped) |
| `GXMCP_REQUIRE_LIVE_BUILD_ALL` | Set to `1` to require the native Build All evidence gate during release preflight. Missing fixtures or an unavailable GeneXus cloud `User` fail the required gate. | off |

## Timeouts / budgets

| Variable | Purpose | Default |
|----------|---------|---------|
| `GENEXUS_MCP_REAPPLY_TIMEOUT_MS` | Worker reapply timeout; the gateway aligns its wait to it. | 300000 (5 min) |
| `GXMCP_PREVIEW_BUDGET_MS` | Time budget for the headless preview render before it stops blocking. | see `PreviewService` |
| `GXMCP_BUILD_TIMEOUT_SEC` | Wall-clock cap for a single `genexus_lifecycle` build/reorg task. On expiry the task is force-failed and any spawned MSBuild tree is killed, so a wedged deploy/reorg step can't leave the status stuck at `Running`. Clamped to `[60, 7200]`. | 900 (2400 for `rebuild`/RebuildAll) |
| `GXMCP_BUILD_NOPROGRESS_SEC` | No-progress watchdog for a running build. Progress includes phase, current object, output-line count, target completion, and diagnostic counts; it is independent from the compact `status` long-poll ETag. On expiry the task is force-failed while preserving the pre-terminal phase in the envelope and message. `0` disables it; values are clamped to `[30, 3600]`. A larger value does not repair a false liveness signal. | 180 |
| `GXMCP_BUILD_TASK_CAP` | Maximum completed build statuses retained in the worker's `_tasks` registry. A sweep on every new build evicts terminal entries past the cap (oldest-completed first; never non-terminal, never anything completed <60s ago). Floored at 10 so the gateway's Take(10) task listing stays intact. | 50 |
| `GXMCP_BUILD_TASK_TTL_MIN` | Age in minutes after which a terminal build status is evicted from `_tasks`, even under the cap. Floored at 60 so async pollers (up to 45min hard cap) keep resolving their taskId. | 180 |
| `GXMCP_BUILD_FULLOUTPUT_KEEP_MIN` | Age in minutes after which a terminal build's in-memory `FullOutput` buffer is released. The status envelope keeps answering (counts, shaped output, errors, `fullLogPath`) — only the raw buffer is dropped. | 15 |
| `GXMCP_BUILD_LOG_RETAIN_COUNT` | Newest per-build `build-<taskId>.log` files kept in the worker `logs/` dir; older ones are deleted best-effort after each write. `0` (or negative) disables the sweep. Only `build-*.log` is ever touched. | 50 |
| `GXMCP_READ_CACHE_TTL_SEC` | In-memory read cache TTL for `ObjectService` in seconds. Since writes perform deterministic cache invalidation, a longer TTL prevents redundant COM disk re-reads across multi-turn sessions. | 300 (5 min) |
| `GXMCP_READ_CACHE_MAX` | Maximum entries in the worker `ObjectService` read cache; oldest evicted past the cap (floored at 16). Pairs with the TTL above: TTL bounds age, this bounds count. | 256 |
| `GXMCP_SEMANTIC_CACHE_MAX_BYTES` | Total serialized-payload ceiling (bytes) for the gateway semantic cache, evicted LRU alongside the 256-entry count cap (`GXMCP_SEMANTIC_CACHE_MAX`). Caps worst-case memory when a few giant read envelopes would otherwise crowd out hundreds of small ones. | 67108864 (64MB) |
| `GXMCP_FRICTION_LOG_MAX_LINES` | Newest lines kept in `<kb>/.gx/friction.jsonl`; older tail-trimmed best-effort after each append. `0` (or negative) disables rotation. | 5000 |
| `GXMCP_SNAPSHOT_SWEEP` | Orphan index-snapshot sweep on worker boot: `index_<hash>` families whose meta names a KB path that no longer exists are deleted (current KB never touched). Set to `0` to disable. | on |
| `GXMCP_COMMAND_QUEUE_CAPACITY` | Maximum number of input commands admitted before the worker returns `WorkerBusy`; protects the reader from unbounded memory growth. | 256 |
| `GXMCP_SDK_COMMAND_QUEUE_CAPACITY` | Maximum number of SDK-bound commands waiting for the STA bridge before the worker returns `WorkerBusy`. | 64 |
| `GXMCP_SDK_QUEUE_CAPACITY` | Maximum number of low-priority SDK actions (watcher/index callbacks) admitted by `SdkExecutor`. | 64 |
| `GXMCP_OUTPUT_QUEUE_CAPACITY` | Maximum number of stdout lines buffered while the pipe writer drains responses/logs; producers apply backpressure instead of growing memory without bound. | 256 |
| `GXMCP_ERROR_QUEUE_CAPACITY` | Maximum number of stderr lines buffered while the error writer drains diagnostics. | 256 |

## Diagnostics / advanced

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_VERSION_CATALOG` | Optional absolute path to an alternate `gx-versions.json` catalog. Use this only for controlled validation or packaging; the published Gateway and Worker load the catalog copied into their `config` directory. | bundled `config/gx-versions.json` |
| `GXMCP_SYNC_LOG` | Set to `1` to also append every log line synchronously (crash forensics). | off |
| `GXMCP_LEGACY_TOOL_ALIASES` | Set to `0` to opt out of legacy tool-name aliases (de-advertised tools reachable by old names). | aliases on |
| `GXMCP_RESILIENT_SPEC` | Set to `1` to opt into the resilient specifier path (slower; opt-in). | off |
| `GXMCP_OCR_ENGINE` | Set to `tesseract` to select the Tesseract OCR engine (requires the Tesseract.NET dependency). | unset |
| `DOTNET_gcServer` | .NET runtime switch (not GxMcp-owned): set to `0` to run the Gateway on Workstation GC instead of the built-in Server GC. Measured 2026-09 on 90 steady-state JSON calls: no memory win either way (WS ~99 vs ~105MB, within noise) with latency parity, so the Server default stays; use `0` only on hard memory-constrained hosts. No rebuild needed. | `1` (Server, via csproj) |

## Client registration / config location

| Variable | Purpose | Default |
|----------|---------|---------|
| `GX_CONFIG_PATH` | Absolute path to the `config.json` the gateway loads (KB aliases + GeneXus path). This is the **global / multi-project** registration mechanism: point every MCP-client entry at one config regardless of the current working directory. Without it, the launcher looks for `config.json` in the cwd and aborts if absent (`No config.json was found`). `init` writes this into any client config it patches; set it by hand when registering a client manually (e.g. `claude mcp add genexus -e GX_CONFIG_PATH="<path>" -- <launcher>`). | cwd `config.json` |

## Set internally (do not set by hand)

| Variable | Purpose |
|----------|---------|
| `GXMCP_SERVER_VERSION` | The gateway injects the server version into the worker's environment on spawn. Reading it in worker code is fine; setting it externally has no effect. |
| `GXMCP_DRIVER` | Gateway-selected Worker driver (`native-sdk`, `dotnet-reflection`, or `com-gxpublic`), injected when it launches a Worker or shared Worker host. Do not set by hand. |
| `GXMCP_TARGET_MAJOR` | Gateway-selected GeneXus major injected with the Worker driver; the Worker uses it for version-specific compatibility/provider behavior. Do not set by hand. |
| `GXMCP_PROFILE_CONFIG_PATH` | The gateway injects the absolute profile path into the worker so preview `axiCli` values are resolved relative to the MCP profile instead of the process current directory. |
| `GXMCP_OPERATIONAL_STATE_KEY` | The Gateway injects an opaque key for the current Worker operational state scope; Worker runtime roots hash it for per-scope separation. Do not set by hand. |
| `GXMCP_SHARED_CHILD` | `SharedWorkerHost` injects `1` when it starts the shared Worker child. The child skips the stdin `ping` shortcut for literal `ping` (trimmed, case-insensitive), `"method":"ping"`, and `"action":"Ping"`: it emits no inline `Ready` envelope and queues those lines normally (a full queue may return `WorkerBusy`). Readiness still arrives via `notifications/worker/sdk_ready`; a queued `"method":"ping"` returns `Ok` (`Pong`) when dispatched. Do not set by hand. |

In shared-host mode, `heartbeat_ack` acknowledges the Gateway↔host attachment; it is not a heartbeat response sent by the host on behalf of the child.

## Legacy GXPublic provider

`com-gxpublic` uses the 32-bit GXPublic OLE DB provider registered for the selected legacy GeneXus major. A Gateway-managed Worker probes registered providers for that major and injects the selected ProgID; this detected value takes precedence over an inherited environment value. A directly launched Worker can use the operator-configurable preferred ProgID below, which is tried before the major-specific candidates.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_GXPUBLIC_PROVIDER` | Preferred 32-bit GXPublic OLE DB ProgID for a directly launched Worker. Gateway-managed sessions use the provider selected by the Gateway registry probe. | unset (Gateway selection or Worker candidate order) |

## Preview browser driver

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_RUNTIME_DIR` | Optional runtime/dependency directory searched for `chrome-devtools-axi` before the Worker/backend directories. Relative values are resolved from the Worker directory. | unset |
| `GXMCP_DEPENDENCIES_DIR` | Optional dependency directory searched for `chrome-devtools-axi` before the Worker/backend directories. Relative values are resolved from the Worker directory. | unset |

> **Maintenance note:** when you add a new `GXMCP_*` / `GENEXUS_MCP_*` variable,
> add a row here. This table is the single reference operators are pointed at
> from `AGENTS.md` and `TROUBLESHOOTING.md`.
