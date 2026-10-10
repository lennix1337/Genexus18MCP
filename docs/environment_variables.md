# Environment variables

Reference for the environment variables the GeneXus MCP gateway and worker read
at runtime. There is **no `.env` file loader** — set these in the environment
that launches the AI client (which spawns the gateway), or in the MCP-client
config's `env` block for the server entry.

All are optional. Unset means the documented default applies.

**Scope.** This page is the complete set of environment variables the Gateway
and Worker read, plus the harness and third-party names that reach them. Each
section below is labelled with who sets the variables in it:
**operator-facing** (a human may need to set it), **harness/test-only** (set by
this repository's own scripts and tests — do not set them in production), or
**third-party passthrough** (consumed by an external tool; this server does not
interpret the value). Sections that predate this labelling are operator-facing
by default.
`src/GxMcp.Worker.Tests/EnvironmentVariableDocCoverageTests.cs` fails the test
suite when a variable read under `src/` is missing from this page, so a new
`GetEnvironmentVariable` call cannot land undocumented.

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

### STA scheduler busy handling

The GeneXus model is single-threaded: only one SDK operation runs at a time on the Worker's STA thread. The scheduler answers `WorkerBusy` instead of blocking indefinitely, and these two variables control that behavior.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_BUSY_WAIT_MS` | Maximum queue age tolerated for an SDK command. Evaluated when the command is dequeued on the STA thread: an item that waited longer than this is rejected with `WorkerBusy` rather than executed late. Raise it for long builds, lower it to fail fast in interactive use. | `15000` ms |
| `GXMCP_BUSY_REJECT_MS` | Immediate-rejection window for low-priority commands that arrive while a long operation is already running: they are rejected with `WorkerBusy` without entering the queue once the in-flight operation has run for at least this long. `0` or a negative value disables the rejection and always queues instead. | `3000` ms |

- Both values come from the **Worker process environment**, which is fixed when the
  Worker starts. `GXMCP_BUSY_WAIT_MS` is consulted for every queued command and
  `GXMCP_BUSY_REJECT_MS` is read once at startup, but neither takes effect until a
  new Worker starts from a process that already has the new value.
- `GXMCP_BUSY_WAIT_MS`: a non-positive or non-numeric value falls back to the
  default (15000 ms) — `0` does **not** disable it.
- `GXMCP_BUSY_REJECT_MS`: `0` or a negative value disables the rejection; a
  non-numeric value falls back to the default (3000 ms).
- To apply a change, set the variable as described at the top of this page, then
  restart the process that starts the Worker:
  - `isolated` (default): restart the MCP client so it starts a new Gateway.
    Reloading only the Worker keeps the Gateway's old environment.
  - `shared-host` (requires `GatewayMode=stdio-isolated`): close every client
    attached to the KB and wait for the WorkerHost broker to exit on its own; it
    exits only after it has had no attachments and no background activity for the
    idle timeout (`Server.WorkerIdleTimeoutMinutes`; for the broker a value `<= 0`
    means 60 minutes, not "never"). The next Gateway then starts a new broker
    with the new value. Restarting one client, reloading the Worker or recovering
    the connection while the broker is alive only reattaches to the old broker.
  - Legacy HTTP / shared Gateway (`http-shared`, isolated Workers): restart the
    master Gateway process.
- There is no published per-command override. The Worker resolves a `busyWaitMs`
  knob from the RPC envelope, but no Gateway conversion path forwards a tool
  argument or client `_meta` to it, so the environment variable above is the only
  reachable control today.
- See the `WorkerBusy` hints returned by the affected tools for the actionable
  value.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_STATE_DIR` | Optional Worker state root (for example preview configuration and source-store files). | `%LOCALAPPDATA%\GenexusMCP\state\<operational-key-hash>` |
| `GXMCP_JOBS_DIR` | Optional exact directory containing the soft-reload `jobs.json`; managed Gateways set this to the same scoped jobs directory they persist. | `GXMCP_STATE_DIR` |
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

## Team Development credentials (mutating server operations)

**Operator-facing, secrets.** The mutating Team Development paths (apply update,
lock, commit, resolve theirs/automerge) need a server URL plus credentials. Each
value may come from the tool arguments or from these variables; the tool
argument wins.

Supply the secrets through the **environment** (the MCP-client config `env`
block, or the shell that launches the client), not through tool arguments: an
argument is echoed into agent transcripts and tool-call logs. These values are
never written to a log line.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_TEAMDEV_URL` | Team Development server URL. | unset → resolved from the KB's own server link |
| `GXMCP_TEAMDEV_USER` | Username for the OAuth exchange. Not a secret on its own, but pair it with a secret and prefer `GXMCP_TEAMDEV_TOKEN` where the server issues one. | unset |
| `GXMCP_TEAMDEV_PASSWORD` | Password used for the token exchange. **Secret.** | unset |
| `GXMCP_TEAMDEV_TOKEN` | Pre-acquired OAuth token, used as-is instead of performing the user/password exchange. **Secret.** | unset |
| `GXMCP_TEAMDEV_AUTHTYPE` | Authentication type prefixed to a bare username as `AUTHTYPE\user`, because the token endpoint splits on `\` and rejects a username without one. A username that already contains `\` is used unchanged. | `Local` |

The GeneXus SDK authenticates server operations from an OAuth token, not from
the inline user/password on the data objects: the headless Worker never logged
in, so it performs the exchange itself through
`TokenAuthorizationManager.GetToken`. Bad credentials surface as an
authentication error on the mutating call.

## Write safety (advisory owner lock and destination pins)

**Operator-facing.** Two independent guards on mutating calls. Both fail
closed: a lock that cannot be read is treated as "do not overwrite", never as
permission.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_WRITE_OWNER_ID` | Advisory write-lock owner for this session. When set, a write to an object held by a different, unexpired owner is rejected with the advisory-lock error. Leave unset to disable the advisory lock entirely. | unset (no advisory lock) |
| `GXMCP_WRITE_FORCE` | Set to `1` to make the advisory lock above advisory in fact: the owner mismatch is logged and the write proceeds. **This destroys the protection the previous variable provides** — use it only to recover a KB left locked by an owner that no longer exists. | off |
| `GXMCP_EXPECTED_KB_PATH` | Pins the KB the Worker may act on. When set, an SDK-reported KB path that differs from this value fails the write instead of being followed. The pin is *checked*, never *activated*: it does not open the KB. | unset |
| `GXMCP_EXPECTED_KB_VERSION` | Pins the active KB version. When set, a mutating command is rejected if the KB's active version is not the pinned one (or is frozen). Only the explicit `kbversion action=set_active` recovery towards the pinned version is allowed through, and only with auto-update off. | unset |

Both pins survive Worker and Gateway restarts because they live in the
environment that launches the Worker, which is why they are the right place for
a long-lived safety rail on a shared KB.

## Human-in-the-loop elicitation

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_ELICITATION` | `auto` asks the human (through MCP `elicitation/create`) to pick a KB when a call has no KB context, and to approve irreversible calls made without `confirm=true`; `strict` also asks when the agent passed `confirm=true`; `off` disables elicitation. Only clients that declare the `elicitation` capability over stdio are asked. | `auto` |
| `GXMCP_ELICITATION_TIMEOUT_SECONDS` | Seconds to wait for the human's answer. An unanswered confirmation is refused; an unanswered KB picker returns the original error. | `300` |

See [Human-in-the-loop elicitation](elicitation.md).

## Build path (`genexus_edit_and_build` / `genexus_run_object`)

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_INPROCESS_BUILD` | Opt into the in-process build runner. | off |
| `GXMCP_INPROCESS_BUILD_BATCH` | Set to `1` to opt into batched build (opt-in since 2026-05-22). | off |
| `GXMCP_INPROCESS_BUILD_FASTPATH` | Fast-path build variant (benchmark / opt-out lever). | off |
| `GXMCP_BUILD_COMPILE_ONLY` | Compile without the full build pipeline (benchmark / opt-out lever). | off |
| `GXMCP_BUILD_PROFILE` | Select a build profile. | unset |
| `GXMCP_REAP_ORPHAN_MSBUILD` | Reap orphaned MSBuild processes after a build. | off |
| `GXMCP_ALLOW_CONCURRENT_BUILDS` | Set to `1` to stop serializing builds per KB: the Gateway then admits a second lifecycle/build for the same Worker while one is already running, and the Worker stops rejecting the second one. Off by default because one STA Worker plus a shared MSBuild tree does not serialize safely. | off (one build at a time) |

## Live KB and release preflight

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_TEST_KB` | Absolute path to the disposable KB used by `scripts/test-live.ps1` and the release preflight. `release-preflight.ps1` auto-selects `C:/KBs/KBTeste` for GeneXus 18 or `C:/KBs/KBTeste17` for GeneXus 17 when this is unset. | unset (local compatible fixture autodetected; live gate skipped when none exists) |
| `GXMCP_TEST_FIXTURE` | Optional fixture attestation JSON matching `GXMCP_TEST_KB`; required for benchmark population comparisons, not normal live validation. | unset |
| `GXMCP_LIVE_MAJORS` | Comma-, semicolon-, or whitespace-separated catalog majors for the live matrix used by `scripts/release-preflight.ps1` and CI. When set, the matrix validates only these majors; the standalone matrix command validates every catalog major when no `-Majors` flag is supplied. | unset (single-major preflight; all catalog majors for standalone matrix) |
| `GXMCP_LIVE_GX_PATH_MAP` | Semicolon-separated `major=absolute-path` overrides for SDK installations used by the live matrix, for example `17=C:\Program Files (x86)\GeneXus\GeneXus17Trial;18=C:\Program Files (x86)\GeneXus\GeneXus18`. | unset (catalog default paths) |
| `GXMCP_TEAMDEV_PENDING_NAME` | Name of a pre-seeded object with an IDE-created Team Development pending change for the opt-in Gateway regression test. | unset (IDE-origin regression skipped) |
| `GXMCP_REQUIRE_LIVE_BUILD_ALL` | Set to `1` to require the native Build All evidence gate during release preflight. Missing fixtures or an unavailable GeneXus cloud `User` fail the required gate. | off |

## Source-store backfill

| Setting | Purpose | Default |
|----------|---------|---------|
| `Server.SourceStoreBackfill` | `auto` starts bounded P2 slices that populate the compressed source store and refresh stale parts; `off` keeps population lazy. | `auto` |
| `GXMCP_SOURCE_STORE_BACKFILL` | Worker override for `Server.SourceStoreBackfill` (`off`, `false`, or `0` disables it). | inherited config / `auto` |
| `Server.SourceStoreMaxMB` | Maximum compressed source-store size in MB. | `512` |
| `GXMCP_SOURCE_STORE_MAX_MB` | Worker override for `Server.SourceStoreMaxMB`. | inherited config / `512` |

Backfill progress is exposed under `index.sourceStore` (and lifecycle status) as stored/stale/total counts, cursor state and ETA. Each SDK slice is bounded to roughly 50 objects or 250 ms; the Worker never holds the STA while waiting for the next slice.


| Variable | Purpose | Default |
|----------|---------|---------|
| `GENEXUS_MCP_REAPPLY_TIMEOUT_MS` | Worker reapply timeout; the gateway aligns its wait to it. | 300000 (5 min) |
| `GXMCP_PREVIEW_BUDGET_MS` | Time budget for the headless preview render before it stops blocking. | see `PreviewService` |
| `GXMCP_BUILD_TIMEOUT_SEC` | Wall-clock cap for a single `genexus_lifecycle` build/reorg task. On expiry the task is force-failed and any spawned MSBuild tree is killed, so a wedged deploy/reorg step can't leave the status stuck at `Running`. Clamped to `[60, 7200]`. | 900 (2400 for `rebuild`/RebuildAll) |
| `GXMCP_BUILD_NOPROGRESS_SEC` | No-progress watchdog for a running build. Progress includes phase, current object, output-line count, target completion, and diagnostic counts; it is independent from the compact `status` long-poll ETag. On expiry the task is force-failed while preserving the pre-terminal phase in the envelope and message. `0` disables it; values are clamped to `[30, 3600]`. A larger value does not repair a false liveness signal. | 180 |
| `GXMCP_INDEX_NO_PROGRESS_SEC` | No-progress watchdog for the **index build**, independent of the build watchdog above. `IndexBuildWatchdog` cancels the index build when no progress is recorded for this many seconds. Same clamp (`[30, 3600]`) and same caveat: a bigger value only buys more time, it does not repair a false liveness signal. Raise it for a very large first-time index on a slow disk. | `180` |
| `GXMCP_ASYNC_JOB_WATCHDOG_S` | Gateway-side watchdog for an asynchronous edit/write job that never returns. Without it the bound is `max(600s, min(caller estimate x 8, 3600s))`. Set an explicit value in seconds to replace that bound; `0` or a negative value disables the watchdog, so a wedged job stays `running` indefinitely instead of becoming a terminal `stalled` state with recovery steps. | derived from the caller estimate |
| `GXMCP_MTA_CONCURRENCY` | Number of commands the Worker may run concurrently on its **MTA** executor (used by the `com-gxpublic` legacy driver path). Only affects that driver; the native SDK stays single-threaded on the STA regardless. | `8` |
| `GXMCP_MTA_QUEUE_CAPACITY` | Queue depth for the MTA executor above, bounding memory when a legacy-driver caller outruns it. | `256` |
| `GXMCP_BUILD_TASK_CAP` | Maximum completed build statuses retained in the worker's `_tasks` registry. A sweep on every new build evicts terminal entries past the cap (oldest-completed first; never non-terminal, never anything completed <60s ago). Floored at 10 so the gateway's Take(10) task listing stays intact. | 50 |
| `GXMCP_BUILD_TASK_TTL_MIN` | Age in minutes after which a terminal build status is evicted from `_tasks`, even under the cap. Floored at 60 so async pollers (up to 45min hard cap) keep resolving their taskId. | 180 |
| `GXMCP_BUILD_FULLOUTPUT_KEEP_MIN` | Age in minutes after which a terminal build's in-memory `FullOutput` buffer is released. The status envelope keeps answering (counts, shaped output, errors, `fullLogPath`) — only the raw buffer is dropped. | 15 |
| `GXMCP_BUILD_LOG_RETAIN_COUNT` | Newest per-build `build-<taskId>.log` files kept in the worker `logs/` dir; older ones are deleted best-effort after each write. `0` (or negative) disables the sweep. Only `build-*.log` is ever touched. | 50 |
| `GXMCP_READ_CACHE_TTL_SEC` | In-memory read cache TTL for `ObjectService` in seconds. MCP writes invalidate it; external IDE saves need `genexus_read refresh=true` to confirm an independent SDK reload. TTL alone is not evidence of persisted freshness. | 300 (5 min) |
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
| `GXMCP_VERBOSE_LOGS` | Set to `0` to drop the Gateway's per-request log lines (cache-invalidation and tool-latency entries), which otherwise pay a timestamp format, a lock and an `AutoFlush` disk write per request. Cold-start, lifecycle and error logs are unaffected — this is the first lever when Gateway log I/O shows up in a profile. | `1` (verbose on) |
| `GXMCP_IDLE_GC` | Set to `0` to disable the Worker's idle-driven LOH compaction (one `CompactOnce` + collect per idle period, re-armed whenever activity resumes). Turn it off only when the x86 heap is *not* fragmenting; leaving it on is the point. | on |
| `GXMCP_CRASH_LEDGER_PATH` | Absolute path of the JSONL file the Gateway appends Worker crash records to. Point it at a writable location when `%LOCALAPPDATA%` is not available or is not retained. | `%LOCALAPPDATA%\GenexusMCP\worker-crashes.jsonl` |
| `GXMCP_SOURCE_ENCODING` | Encoding name (any name `System.Text.Encoding.GetEncoding` accepts) used to read and write GeneXus source. Only reachable on the legacy reflection/COM drivers; an unknown name is ignored and the detected encoding is used. | detected per KB and driver |
| `GXMCP_SCREENSHOT_DIR` | Extra directory accepted as a source root by `screenshot_publish`. A file outside the OS temp dir, the open KB and this directory is refused with `SourceNotAllowed`. | unset (temp dir and open KB only) |
| `GXMCP_WEBFORM_SAVE_DIAGNOSTICS` | Set to `1` to enable the WebForm save bypass experiments (`SaveModelEntityOutput`, `SaveHeader`, pre-save/bypass state dumps). Off by default: these are reflection-based diagnostics for one specific save bug, not a supported path. | off |
| `GXMCP_LOG_SCAN_MAX_MB` | How far back `genexus_logs` will scan when a filter is supplied, in MiB. The filters (`grep`, `filterCorrelation`, `objectFilter`, `since`) are applied to each line as it is read backwards from the end of the log, so a match anywhere within this range is found while retained memory stays bounded by the number of matches returned. When the budget stops the scan the response says `scanComplete:false` with `scannedBytes` and a hint, because "no match in what we read" is not "no match in the log". Raise it for a very large log; lower it if a no-match search on a huge log is too slow. | `256` |
| `GXMCP_SDK_NO_PROGRESS_STALL_MS` | Milliseconds a busy SDK lane may run **without ever emitting a progress notification** before it is classified `busy-stalled-unproven` and becomes a recovery candidate. The progress marker is only set by operations that emit progress (builds, bulk indexing), so a single object read, a save, an inspect, or a COM call blocked behind a modal dialog emits none and used to stay `busy-unproven` indefinitely - never recovered by `genexus_connection_recover` or shared-host supervision. Deliberately several times the 90s `sdkStallAfterMs` window: a lane that has proved it reports progress is trusted sooner. Raise it for a KB with genuinely long non-progress operations; lower it to recover wedged SDKs faster, at the cost of recycling a Worker that was about to finish. Read by both the Gateway and the shared-host broker so they cannot disagree. | `600000` |
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
| `GXMCP_KB_ID` | Stable physical-KB identity the Gateway injects on Worker spawn. Together with `GXMCP_KB_GENERATION` and `GXMCP_STATE_SCOPE_ID` it makes the edit-snapshot root authoritative, so two KB generations cannot share a snapshot directory. Setting it by hand does not open a KB. |
| `GXMCP_KB_GENERATION` | Context generation counter for the current KB, injected by the Gateway and paired with `GXMCP_KB_ID`. A snapshot directory is reused only when all of scope, KB id and generation match. Do not set by hand. |
| `GXMCP_STATE_SCOPE_ID` | Process/host scope id the Gateway injects on Worker spawn; the Worker refuses an SDK-reported KB path that does not match the `GX_KB_PATH` it was given for this scope. Do not set by hand. |
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

## Legacy structural environment overrides

**Operator-facing, legacy configs only.** These four names change how the
Gateway itself is wired, so they are only honoured for a legacy configuration
document. Under a **strict** document each one is rejected as a structural
override — with two exceptions, checked for *agreement* rather than refused:
`GX_MCP_PORT` must equal `Server.HttpPort` and `GX_MCP_STDIO` must equal
`Server.McpStdio`, or configuration loading fails with a conflict error. The
same rejection applies to `GXMCP_PROFILE`, `GXMCP_NO_STRUCTURED_CONTENT`,
`GXMCP_EMIT_STRUCTURED_CONTENT` and `GXMCP_TERSE`, documented under
[HTTP endpoint](#http-endpoint).

| Variable | Purpose | Default |
|----------|---------|---------|
| `GX_MCP_STDIO` | Set to `true`/`false` to force `Server.McpStdio` on a legacy document. | config value |
| `GX_MCP_PORT` | HTTP port for the legacy Streamable HTTP transport. Values `<= 0` are ignored. | `config.Server.HttpPort` |
| `GXMCP_SHARED_GATEWAY` | Set to `1` to declare the shared (master) Gateway intent explicitly for a legacy transport document. | unset |
| `GX_MCP_SHARED_GATEWAY` | Older spelling of the variable above; both are accepted and both count. | unset |

## GeneXus install and KB path resolution

**Operator-facing.** The install path is normally taken from `config.json` or
the catalog. These names exist so a directly launched Worker — or a dev shell
with no `config.json` — can find the same GeneXus installation and KB.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GX_PROGRAM_DIR` | GeneXus installation directory. Checked first by the Worker's SDK identity detection and by the legacy-driver bootstrap, so it wins over the value below. | unset → `GX_PATH`, then `config.json` |
| `GX_PATH` | Conventional GeneXus build variable, honoured as the fallback for `GX_PROGRAM_DIR`. This is a GeneXus-owned name, not a GxMcp one. | unset |
| `GX_KB_PATH` | Absolute KB path. The Gateway injects this on Worker spawn from the gateway-owned handle, so the Worker acts on the KB the Gateway chose; setting it for a directly launched Worker selects that KB instead. If the SDK reports a different path for a scoped Worker, the Worker refuses the command rather than following it. | Gateway-injected; unset for a direct launch |
| `GX_KB_ALIAS` | Alias reported for the KB in the legacy Team Development sync responses. | unset → the KB directory name |

## Worker transport (direct launch)

**Operator-facing.** A Gateway-managed Worker speaks stdio to its parent and
needs none of this.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GX_MCP_PIPE` | Name of a local named pipe the Worker connects to instead of using stdio, with a 30 s connect timeout. Used by the Gateway when it starts a Worker behind a pipe broker. | unset (stdio) |

## Harness and test-only variables

**Not operator-facing.** This repository's own scripts and test harnesses set
these; they are listed so the set stays complete and nobody has to reverse
engineer a failing live test. **Do not set them in production** — several
enable experimental write paths or skip discovery guards.

| Variable | Purpose | Default |
|----------|---------|---------|
| `GXMCP_LIVE_GATEWAY_EXE` | Absolute path to the `GxMcp.Gateway.exe` a live test harness drives. Must exist or the harness fails fast. | discovered next to the test assembly |
| `GXMCP_LIVE_RPC_TIMEOUT_MS` | Per-RPC timeout for the live Gateway harness, in milliseconds. Accepted range `[1000, 7200000]`; out-of-range and non-numeric values fall back to the default. | `240000` |
| `GXMCP_LIVE_SUMMARY_PATH` | File the live Gateway harness appends its timing/identity summary to, for evidence files. | unset (no summary written) |
| `GXMCP_REQUIRE_WWP` | Set to `1` to un-skip the WorkWithPlus-licensed integration tests; unset or `0` skips them, so a contributor without that licence does not see licensing failures. | unset (skipped) |
| `GXMCP_PARITY_IDE_NAME` | Name of a pre-seeded object patterned in the IDE, paired with the variable below for IDE-vs-MCP parity tests. Both must be set or the test skips. | unset |
| `GXMCP_PARITY_MCP_NAME` | Name of the matching object patterned through MCP. | unset |
| `GXMCP_DSO_NAME` | Name of an existing Design System with nonempty `Tokens` and `Styles` parts, for the read-only preview regression. Unset skips that live test. | unset |
| `GXMCP_UPDATE_GOLDEN` | Set to `1` to overwrite the discovery golden fixture with the current `tools/list` response instead of asserting against it. Never set in CI. | unset |
| `GX_MCP_SDK_PROBE` | Set to `1` to run the full SDK surface probe on every pattern apply. It walks every loaded SDK assembly and writes a multi-megabyte dump; it used to run unconditionally and cost 5–15 s per apply. `genexus_sdk_probe` remains the explicit way to get a dump. | off |
| `GX_MCP_SDK_PROBE_DIR` | Output directory for that dump. An installed package under `node_modules` never receives generated diagnostics in its own tree. | `<repo>\docs\sdk-probe`, else the Worker temp root |
| `GX_MCP_REPO_ROOT` | Repository root used to place the probe output under `docs\sdk-probe`. Honoured only when the directory exists. | unset (heuristic: a parent with a `docs` folder) |
| `GX_MCP_PATTERN_DEBUG` | Set to `1` to dump in-memory pattern state around a pattern apply. | off |
| `GX_MCP_PATTERN_DEBUG_DIR` | Directory for the pattern-debug dumps. | Worker temp root |
| `GX_MCP_PATTERN_DELTA_EXPERIMENT` | Set to `1` to enable the pattern delta-attribute experiment. | off |
| `GX_MCP_PATTERN_DIRECT_SAVE_EXPERIMENT` | Set to `1` to enable the direct-Save reflection experiment on apply. | off |
| `GX_MCP_PATTERN_NATIVE_EXPERIMENT` | Set to `1` to enable the native pattern-mutation experiment. | off |
| `GX_MCP_PATTERN_PRESAVE_EXPERIMENT` | Set to `1` to log the pre-save pattern baseline and run the pattern part hooks. | off |
| `GX_MCP_PATTERN_SEMANTIC_EXPERIMENT` | Set to `1` to enable the semantic grid-variable save experiment. | off |

## Third-party and OS passthrough

**Not interpreted by this server.** These names are read and handed on, or are
standard OS variables consulted while probing the host. Their values are
consumed by an external tool, and GxMcp neither validates nor logs them.

| Variable | Purpose | Default |
|----------|---------|---------|
| `CHROME_DEVTOOLS_AXI_MCP_PATH` | Absolute path to the `chrome-devtools-axi` executable used by the preview browser driver. Used only when the file exists; otherwise the normal `GXMCP_RUNTIME_DIR` / `GXMCP_DEPENDENCIES_DIR` search runs. | unset (normal search) |
| `MCP_PERF_PROFILE` | Set to `legacy` to turn off the v1 performance profile in the Gateway and in `genexus_list_objects`. Anything else (including unset) keeps it on. | v1 on |
| `PATHEXT` | Windows OS variable listing the executable extensions the browser-driver detector probes on `PATH`. Read for discovery only. | `.EXE;.CMD;.BAT;.PS1` when unset |
| `LOCALAPPDATA` | Windows OS variable consulted for the default root of the crash ledger and the Worker runtime directories. When unset or empty the equivalent `Environment.SpecialFolder` folder is used instead, so an unset value is safe. | OS special folder |

---

> **Maintenance note:** a new `GetEnvironmentVariable` read under `src/` must
> appear here. That rule is enforced, not aspirational:
> `src/GxMcp.Worker.Tests/EnvironmentVariableDocCoverageTests.cs` enumerates
> every literal `GetEnvironmentVariable("<NAME>")` under `src/`, restricts the
> set to the `GXMCP_` / `GENEXUS_MCP_` / `GX_MCP_` / `GX_` prefixes, and fails
> when a name is missing from this page. A deliberately internal name goes on
> the test's explicit allowlist *with its reason*, and an allowlisted name still
> has to be documented here — under "Set internally (do not set by hand)", which
> is where a Gateway-injected value belongs.
>
> The scan resolves two call shapes and states its limit rather than claiming
> exhaustiveness. It sees every literal argument, plus every name held in a
> `const` whose identifier ends in `EnvVar` / `Variable` — that second rule
> exists because `WriteDestinationGuard.PathVariable`,
> `ArtifactPathResolver.OutputDirectoryEnvironmentVariable` and the two
> `SemanticCacheStore` cache caps are read that way. It does **not** see a name
> passed as an argument to a helper that forwards it
> (`Worker.ResolveQueueCapacity("GXMCP_MTA_CONCURRENCY", 8)` and three siblings),
> a name read through a loop variable or a local array
> (`Configuration.RejectStrictStructuralEnvironment`, whose six names are all
> documented on this page anyway), or a name that arrives at runtime. A new call
> site of one of those three shapes will not be caught automatically; probing
> each variable at runtime instead would not reach an opt-in branch either.
>
> **Profile-driven credential references (no fixed name).** A `DataStoreAlias`
> in the MCP profile can name the environment variable that holds its
> credentials — `UserIdEnvironmentVariable`, `PasswordEnvironmentVariable` or
> `ConnectionStringEnvironmentVariable`. The Worker reads whatever name the
> profile declares, so there is no fixed variable to document: the name is
> yours to choose. The rule is that it must be a variable *name* (a value
> containing `=` or `;` is rejected), and it must live on the Worker host
> rather than in `config.json`.
>
> This page is the single reference operators are pointed at from `AGENTS.md`
> and `TROUBLESHOOTING.md`.
