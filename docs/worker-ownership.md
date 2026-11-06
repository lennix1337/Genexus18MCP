# Worker ownership and orphan cleanup

`WorkerProcess` owns a checkout-scoped lease for each spawned worker. The lease key is a SHA-256 digest of the normalized worker executable path and KB path, so two checkouts serving the same KB do not share cleanup records. A named Windows mutex serializes reservation writes across gateway processes.

The registry stores the owning gateway PID, worker PID, and the worker's UTC start time. Starting a worker with an existing live lease fails closed; it never enumerates or kills unrelated processes. When the gateway exits cleanly, `StopProcess` releases the record before notifying the pool so a replacement can reserve it. If a gateway dies, the next reservation may reap only the recorded worker when both PID and start time still match, preventing PID-reuse cleanup.

A health-check tick performs a once-per-minute reconciliation against this worker's exact registry record. This is a narrow legacy/orphan fallback, not a system-wide process or WMI scan. The old per-start `KillOrphanWorkers` sweep was removed. The existing command-line/WMI lookup used for the exit-code-17 diagnostic remains separate and is not a cleanup mechanism.

This design intentionally scopes ownership by executable path as well as KB path. A debug worker from checkout A and a debug worker from checkout B therefore have independent records and cannot reap one another. Operators should remove stale files under `%LOCALAPPDATA%\GxMcp\worker-ownership` only when no corresponding gateway is running; normal recovery handles stale records automatically.

The default ownership contract remains Gateway-owned and isolated. The supported
`Server.WorkerSharingMode="shared-host"` path uses a separate shared-worker
registry keyed by the normalized physical KB plus executable, GeneXus
installation, driver, and target major. A broker process owns the SDK Worker;
Gateways attach and detach through connection-scoped named pipes. A Gateway
shutdown closes only its attachment and never kills a Worker that still has
another attachment. The broker registry is discovery/recovery metadata, not a
sole liveness proof: PID start time, pipe handshake, connection heartbeat, and
generation must all agree before an attach is accepted.

When the child Worker exits unexpectedly, the broker keeps existing attachments,
fails only requests that were in flight with a retryable
`SHARED_WORKER_RESTARTED` envelope, and elects no second Gateway: the broker
increments the generation and respawns the compatible child itself. Three
crashes in one minute stop the host fail-closed. Each attachment has a bounded
output queue; a slow or disconnected Gateway cannot block another attachment.
The host remains alive while attached and reaps its child after the configured
idle timeout when the last attachment detaches.

The shared mode also propagates a Gateway-local write owner into the Worker. Every ordinary write with an owner takes a short operation-scoped lock for its target/part and releases it in a `finally`; an explicit lock held by the same owner is preserved, while foreign, corrupt, or unreadable locks fail closed unless the caller explicitly requests `force`. This prevents two attached clients from entering the SDK write path for the same target concurrently without changing the Gateway-local session/cache boundaries.

The shared mode is a supported `stdio-isolated` Worker-ownership option. Use
`Server.WorkerSharingMode: "shared-host"` when compatible Gateways should reuse
one SDK Worker; keep `"isolated"` when the agent intentionally needs one Worker
per Gateway or per operation. The configuration must resolve to the same
executable, installation, driver, target major, and physical KB. The normal
`stdio-isolated` mode with `WorkerSharingMode: "isolated"` remains available for
multi-Worker workflows.

## Shared-host registry v2

This section describes the shared-host ownership contract pinned by the
registry, launcher, and host ownership tests for issue #854. It applies only
when `Server.WorkerSharingMode` is `shared-host`. Isolated mode keeps the
Gateway-owned lease described above, unchanged.

### KB key and record location

Every shared-host name is derived from one `kbKey` per physical KB. The KB path
is normalized with `Path.GetFullPath`, trailing `\` and `/` separators are
trimmed, and the result is lowercased with the invariant culture. The `kbKey`
is the lowercase hex SHA-256 of the UTF-8 bytes of that normalized path: 64
characters, no prefix. `C:\KBs\KbA`, `c:\kbs\kba\`, and a relative path that
resolves to the same directory all produce the same key. The Gateway and the
broker share one implementation (`SharedHostKbKey`), so they cannot compute
different keys.

The v2 record for a KB is one JSON file:

```text
%LOCALAPPDATA%\GxMcp\shared-workers\v2\<kbKey>.json
```

The broker accepts `--registry-dir <dir>` to place the record in another
directory (tests use a temporary root). A key that is not 64 lowercase hex
characters is rejected, so a record path cannot escape the registry directory.
Writes are atomic: a temporary file in the same directory replaces the record.

The v1 shared-worker registry still exists in the code base, but shared mode
does not read or write it. The broker pipe name uses the v2 prefix
`GxMcpShared2_<key>`, where `<key>` is the identity key; a v1 `GxMcpShared_`
pipe name is rejected.

### Election and host-lifetime mutexes

Two named mutexes per `kbKey` serialize ownership:

| Mutex | Name | Held by | Purpose |
| --- | --- | --- | --- |
| Election | `Local\GxMcp.SharedWorker.v2.Kb.<kbKey>` | a Gateway, briefly | Only one Gateway at a time removes a stale record and starts a host. Gateways that open the same KB together elect exactly one host and all attach to it. |
| Host lifetime | `Local\GxMcp.SharedWorker.v2.Host.<kbKey>` | the broker, for its whole life | Only one broker can serve a KB. A broker that cannot take this mutex exits at once with exit code 3, before it writes a record, opens a pipe, or starts a child. |

A Gateway that finds a live host for the KB attaches to it without taking the
election mutex and without starting a host.

### Record states

A broker that holds the host-lifetime mutex writes its record with state
`admitting` before the Worker child exists. That record carries schema 2, the
`kbKey`, the normalized identity, the broker PID and start time, the pipe name,
and `workerPid` 0. The record moves to `starting` once the child is launched and
carries the child PID; it reaches `ready` when the Worker has opened the KB.
The other defined states are `held`, `replacing`, `recovering`, `no-worker`,
`refused`, and `stopping`.

### Identity match

A KB path alone does not authorize an attach. The record identity has five
components besides the KB path, compared after normalization:

- `installationPath`
- `driver`
- `major`
- `workerExecutable`
- `profileConfigPath`

When the KB matches a live host but one or more components differ, the Gateway
refuses the attach with `SHARED_WORKER_IDENTITY_MISMATCH` (`retryable=false`,
`dispatchState="not-delivered"`). The error names each differing component with
its requested value and its live value. Differences only in letter case or a
trailing separator are not reported. The Gateway starts no Worker for that KB,
and the live host keeps serving its existing attachments.

### Liveness and fail-closed refusals

A record is live only when a process with the recorded PID is running and that
process started at the recorded start time, compared at millisecond precision.
Every other record is stale, including a file that cannot be parsed. A live
host must also accept a connection on its pipe before an attachment completes.

The Gateway returns `KB_WORKER_OWNED_ELSEWHERE` (`retryable=false`,
`dispatchState="not-delivered"`) and changes no record and no process in two
cases:

- `owner-does-not-accept-attachments`: an isolated-mode Gateway that is live
  holds a running Worker for the KB. The error carries the holder Gateway PID,
  its start time, and two release actions: close the holder session with that
  PID, or set `Server.WorkerSharingMode` to `shared-host` in the holder's
  configuration. The holder is never terminated.
- `liveness-unverified`: the record parses and its PID is running, but that
  process's start time cannot be read. The record is treated as live and the
  error carries the recorded PID.

When a live host does not become ready within the attach readiness wait, or its
pipe refuses or drops the connection, the Gateway returns
`SHARED_WORKER_ATTACH_FAILED` with reason `readiness-timeout` or
`pipe-refused` (`retryable=true`, `dispatchState="not-delivered"`). It creates
no attachment and does not start a second host while the recorded host is live.

### Stale recovery

A Gateway recovers stale ownership for the requested KB only. It never scans or
kills unrelated processes.

1. A stale v2 record is removed under the election mutex, and election
   continues within 5 seconds of the first read. Before deleting, the Gateway
   re-reads the file; a record that changed in the meantime is classified again
   instead of being deleted.
2. An isolated-mode Worker ownership record whose owner Gateway is not live, but
   whose Worker still runs, is an orphan. The Gateway stops that Worker only
   after re-reading, immediately before termination, its PID, start time, and
   image path, and only when all three match the record. A PID that belongs to
   a Gateway, including the recovering Gateway, is never terminated. The
   ownership record is removed once the Worker has exited.
3. If a record cannot be removed, the stop fails, or a stopped Worker still runs
   10 seconds after the stop request, recovery ends with
   `SHARED_WORKER_RECOVERY_FAILED` (`retryable=false`,
   `dispatchState="not-delivered"`). The error names the record key, the
   registry, the failure reason (`remove-failed`, `kill-failed`, or
   `still-running`), and the blocking PID when known. The remaining records are
   left unchanged.

Each removed record and each stopped process produces one Gateway log line:

```text
[Gateway] stale-ownership-recovery action=<removed-record|stopped-process> registry=<name> key=<recordKey> pid=<pid> recordedStartTimeUtcTicks=<ticks> recoveringGatewayPid=<pid> reason=<reason>
```

`registry` is `WorkerOwnershipRegistry`, `SharedWorkerRegistry`, or
`SingleInstanceLock`. `reason` is `owner-not-running`, `start-time-mismatch`,
`unparseable-record`, or `orphaned-worker`. A PID or start time that is not
known prints as `unknown`.

### Dead Gateway attachments

The broker runs a supervisor tick every 5000 ms. Each tick probes the Gateway
PID recorded from every attachment's attach frame and detaches attachments
whose Gateway process has exited. A Gateway that dies just after a tick is
detached by the next one, within 10 seconds. The Worker idle timeout starts at
the moment the last attachment is detached.

### Shared child `SingleInstanceLock`

A Worker started by a broker runs with `GXMCP_SHARED_CHILD=1`. In that mode its
`SingleInstanceLock` file holds:

```json
{"pid":1234,"startedUtc":"<acquisition time, round-trip format>","processStartUtcTicks":638700000000000000,"exe":"C:\\Tools\\GxMcp\\worker\\GxMcp.Worker.exe"}
```

The lock is held only when the recorded PID is running and started at
`processStartUtcTicks` (millisecond precision). A stale lock file, for example
one left by a crashed Worker or a reused PID, is overwritten with the new
Worker's values and startup continues without user action. A lock held by a
running Worker is refused: the new Worker exits before it opens the KB, and the
holder's lock file is left byte-for-byte unchanged, including when the refused
instance is disposed. Isolated-mode Workers keep their existing lock behavior.
