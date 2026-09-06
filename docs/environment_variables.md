# Environment variables

Reference for the environment variables the GeneXus MCP gateway and worker read
at runtime. There is **no `.env` file loader** — set these in the environment
that launches the AI client (which spawns the gateway), or in the MCP-client
config's `env` block for the server entry.

All are optional. Unset means the documented default applies.

## Update / self-update

| Variable | Purpose | Default |
|----------|---------|---------|
| `GENEXUS_MCP_NO_UPDATE_CHECK` | Set to `1` to disable the background "is a newer version available?" check performed on `initialize`. Useful on networks that block the GitHub API. | check enabled |
| `GENEXUS_MCP_NO_SELF_UPDATE` | Set to `1` to disable the self-update apply path (the check may still run; nothing is installed). | self-update enabled |

## HTTP endpoint

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_HTTP_TOKEN` | Shared secret required on every `/mcp` HTTP request (`Authorization: Bearer <token>` or `X-GXMCP-Token`). Binding to a non-loopback address **requires** this — without it, non-loopback `/mcp` requests are refused. The default `127.0.0.1` bind with no token is unchanged. | unset (loopback-only, no auth) |
| `GXMCP_NO_STRUCTURED_CONTENT` | Set to `1` (or `true`) to omit the MCP `structuredContent` field from tool results — it duplicates the whole payload already present in `content[0].text`, adding ~45% to each response's byte size. Equivalent config: `Server.EmitStructuredContent: false`. Env wins over config. | unset (structuredContent emitted) |
| `GXMCP_EMIT_STRUCTURED_CONTENT` | Set to `0` (or `false`) to disable `structuredContent` emission, or `1`/`true` to force it on. Complement to `GXMCP_NO_STRUCTURED_CONTENT`. | unset |
| `GXMCP_TERSE` | Set to `1` (or `true`) for terse responses: omits `next_legal_actions` and the `_meta.tokens` block from tool results, keeping only the payload plus error hints. Equivalent config: `Server.TerseResponses: true`. Env wins over config. | unset (full UX sugar emitted) |
| `GXMCP_PROFILE` | Tool profile for `tools/list` surface (`core`, `authoring`, `devops`, `ui`, `db`, `all`). Lean profiles like `core` (11 tools) or `authoring` (29 tools) drastically reduce initial system prompt tokens vs the full 52-tool surface. | `all` |

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

## Timeouts / budgets

| Variable | Purpose | Default |
|----------|---------|---------|
| `GENEXUS_MCP_REAPPLY_TIMEOUT_MS` | Worker reapply timeout; the gateway aligns its wait to it. | 300000 (5 min) |
| `GXMCP_PREVIEW_BUDGET_MS` | Time budget for the headless preview render before it stops blocking. | see `PreviewService` |
| `GXMCP_BUILD_TIMEOUT_SEC` | Wall-clock cap for a single `genexus_lifecycle` build/reorg task. On expiry the task is force-failed and any spawned MSBuild tree is killed, so a wedged deploy/reorg step can't leave the status stuck at `Running`. Clamped to `[60, 7200]`. | 900 (2400 for `rebuild`/RebuildAll) |
| `GXMCP_READ_CACHE_TTL_SEC` | In-memory read cache TTL for `ObjectService` in seconds. Since writes perform deterministic cache invalidation, a longer TTL prevents redundant COM disk re-reads across multi-turn sessions. | 300 (5 min) |
| `GXMCP_COMMAND_QUEUE_CAPACITY` | Maximum number of input commands admitted before the worker returns `WorkerBusy`; protects the reader from unbounded memory growth. | 256 |
| `GXMCP_SDK_COMMAND_QUEUE_CAPACITY` | Maximum number of SDK-bound commands waiting for the STA bridge before the worker returns `WorkerBusy`. | 64 |
| `GXMCP_SDK_QUEUE_CAPACITY` | Maximum number of low-priority SDK actions (watcher/index callbacks) admitted by `SdkExecutor`. | 64 |
| `GXMCP_OUTPUT_QUEUE_CAPACITY` | Maximum number of stdout lines buffered while the pipe writer drains responses/logs; producers apply backpressure instead of growing memory without bound. | 256 |
| `GXMCP_ERROR_QUEUE_CAPACITY` | Maximum number of stderr lines buffered while the error writer drains diagnostics. | 256 |

## Diagnostics / advanced

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_SYNC_LOG` | Set to `1` to also append every log line synchronously (crash forensics). | off |
| `GXMCP_LEGACY_TOOL_ALIASES` | Set to `0` to opt out of legacy tool-name aliases (de-advertised tools reachable by old names). | aliases on |
| `GXMCP_RESILIENT_SPEC` | Set to `1` to opt into the resilient specifier path (slower; opt-in). | off |
| `GXMCP_OCR_ENGINE` | Set to `tesseract` to select the Tesseract OCR engine (requires the Tesseract.NET dependency). | unset |

## Client registration / config location

| Variable | Purpose | Default |
|----------|---------|---------|
| `GX_CONFIG_PATH` | Absolute path to the `config.json` the gateway loads (KB aliases + GeneXus path). This is the **global / multi-project** registration mechanism: point every MCP-client entry at one config regardless of the current working directory. Without it, the launcher looks for `config.json` in the cwd and aborts if absent (`No config.json was found`). `init` writes this into any client config it patches; set it by hand when registering a client manually (e.g. `claude mcp add genexus -e GX_CONFIG_PATH="<path>" -- <launcher>`). | cwd `config.json` |

## Set internally (do not set by hand)

| Variable | Purpose |
|----------|---------|
| `GXMCP_SERVER_VERSION` | The gateway injects the server version into the worker's environment on spawn. Reading it in worker code is fine; setting it externally has no effect. |

> **Maintenance note:** when you add a new `GXMCP_*` / `GENEXUS_MCP_*` variable,
> add a row here. This table is the single reference operators are pointed at
> from `AGENTS.md` and `TROUBLESHOOTING.md`.
