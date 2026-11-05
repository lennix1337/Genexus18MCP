# GxMcp.SharedHost.TestHost

Test-only process host for the shared-host process harness
(`.kiro/specs/concurrent-multi-session-workers`, Requirement 13 criteria 3 to 8).
It is a net48 x86 console with no GeneXus SDK reference, and it is never packaged:
`build.ps1` copies only the Gateway and Worker outputs into `publish/`.

It compiles the real broker sources by link, exactly as the Worker compiles them:
`SharedWorkerHost.cs`, `SharedWorkerHostProtocol.cs`, `Helpers/LogRedaction.cs`,
`GxMcp.Shared/JsonIngress.cs`, and `GxMcp.Shared/SharedHost/*.cs`. If broker code
ever gains an SDK dependency, this project stops compiling.

## Modes

- `--shared-host ...` runs `SharedWorkerHost.Run`, the same entry point the Worker's
  `Main` calls before any SDK code. Pass this executable as `--worker-executable`
  (or let it default to itself) so the broker starts this binary as its child.
  A process test also passes `--registry-dir <absolute temp dir>`: on .NET Framework
  the broker's `LocalApplicationData` lookup ignores an overridden `LOCALAPPDATA`
  variable, so the option is what keeps a test out of the real
  `%LOCALAPPDATA%\GxMcp\shared-workers` registry.
- Any other command line runs the scripted fake child described below. The broker
  starts its child with `--kb <path> --driver <driver> --major <major>`; the fake
  reads only `--kb` (echoed in `sdk_ready`) and `--fake-script`.

## Child stdio framing

The fake speaks the framing the broker uses with `GxMcp.Worker` in shared-child
mode (`SharedWorkerHostRuntime.ChildInboxWriterLoop` / `ChildReaderLoop`):

- UTF-8 without BOM, one JSON-RPC 2.0 frame per `\n`-terminated line, both ways.
- stdout may start with plain-text `WORKER_HANDSHAKE_START` and
  `WORKER_HANDSHAKE_FEATURES ...` lines, which the broker skips. Any other
  non-JSON stdout line makes the broker stop fail-closed, so the fake writes
  diagnostics only to stderr.
- A response echoes the request `id` token unchanged. The broker rewrites client ids
  to `gxmcp-<attachId>-<seq>-<guid>` strings and restores them on the way back.
- `notifications/worker/sdk_ready` with `params.kb` marks the child ready.

## Script

The script is a JSON object read from, in order:

1. the `--fake-script <value>` argument;
2. the `GXMCP_TESTHOST_SCRIPT` environment variable (the broker passes its own
   environment to the child, so this is the way to script a broker-started child);
3. the defaults below.

A value that starts with `@` names a UTF-8 JSON file (`@C:\temp\script.json`).
Unknown keys and out-of-range values are rejected: the fake writes the reason to
stderr and exits with code 2.

| Key | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `readyDelayMs` | integer | `0` | 0 to 600000 | Delay before `notifications/worker/sdk_ready` is written |
| `handshake` | boolean | `true` | | Write the two `WORKER_HANDSHAKE_*` lines first |
| `workingSetMb` | integer | `0` | 0 to 1024 | MiB of touched memory held from start, so the broker's `WorkingSet64` reading of the child reflects it |
| `defaultDurationMs` | integer | `0` | 0 to 3600000 | Duration of a request with no other duration |
| `durations` | object | `{}` | values 0 to 3600000 | Duration per JSON-RPC `method` name |

A request's duration is `params.fakeDurationMs` when it is an integer in range,
else `durations[method]`, else `defaultDurationMs`.

Example:

```json
{ "readyDelayMs": 200, "workingSetMb": 64, "durations": { "genexus_build": 5000 } }
```

## Requests

- SDK-like requests (any method not listed below) run one at a time on one thread,
  only after `sdk_ready`, like the real child's SDK lane. Each sleeps its scripted
  duration and answers `result` with `fake`, `method`, `scriptedDurationMs`,
  `elapsedMs`, `sequence`, `pid`, and `attachmentId` (the `_meta.attachmentId` the
  broker stamped on the forwarded request).
- Control methods answer at once, even while an SDK-like request runs:
  - `fake/status`: `pid`, `ready`, `workingSetBytes`, `workingSetTargetMb`,
    `queued`, `completed`.
  - `fake/setWorkingSet` with `params.mb` (0 to 1024): resizes the held memory and
    answers with the status. Lowering it releases the memory; the OS may trim the
    working set later.
  - `fake/exit` with optional `params.exitCode` (integer, default 0) and
    `params.respond` (boolean, default true): answers `{ "exiting": true }` unless
    `respond` is false, then exits with that code. Use a non-zero code or
    `respond: false` to simulate a child crash.
- Notifications (frames without an `id`) and malformed lines are ignored.
- When stdin closes, the fake answers the requests it already accepted and exits 0.

## Tests

`src/GxMcp.Gateway.Tests/SharedHost/SharedHostTestHostSmokeTests.cs` starts the fake
child, reads `sdk_ready`, and makes it exit. It locates the executable through
`GXMCP_SHAREDHOST_TESTHOST_EXE` (set by `scripts/coverage/collect.ps1`, which builds
this project before the Gateway tests) or under `bin\Debug` / `bin\Release`.

`src/GxMcp.Gateway.Tests/SharedHost/SharedHostQueueProcessSmokeTests.cs` runs the real
broker with this binary as its child and two `SharedWorkerConnection` clients, and checks
that a second session's SDK request queues behind the first and is answered. It finds the
executable the same way.
