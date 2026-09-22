# Live KB test harness

`scripts/test-live.ps1` requires an explicit KB path before opening a KB or
starting a gateway. Read-only smoke tests can use that path directly; a fixture
manifest is optional metadata for benchmark identity and reproducibility. A
directory called `KBTeste` is a valid explicit local target for operator-
authorized smoke tests and Build All, but is not automatically treated as
disposable.

## Navigation and safety pointers

- `scripts/test-live.ps1`: isolated configuration, build and test gates.
- `scripts/live-build-all.ps1`: HTTP Build All probe and terminal-state polling.
- `scripts/test_live_patch_persistence_kbteste.ps1`: disposable issue probes.
- `Program.KbContext.cs` → `SessionKbContextStore.cs` →
  `KbUseLeaseRegistry.cs`: session selection, canonical aliases and ownership.

Live probes must use bounded asynchronous stdio reads and must not treat a
response carrying `operationId` or `job_id` as final evidence. Disposable probes
must also clean up Gateway/Worker processes on terminating errors. The structured
summary records diagnostic error lines; an actual Worker/Gateway error, missing
diagnostic evidence, explicit-KB open failure, missing process-exit confirmation,
or failed cleanup is `unavailable`, never a pass.
When an indexed read returns `IndexNotReady`, use
`genexus_lifecycle action=status wait=10` before retrying the read; repeated
`genexus_whoami` calls are health checks, not an index-readiness barrier.

## Provisioning prerequisite

The `LiveDesignSystemRead` regression requires `GXMCP_TEST_KB` and
`GXMCP_DSO_NAME` naming an existing DesignSystem with nonempty Tokens and Styles.
Run only `FullyQualifiedName~DesignSystemFreshReadLiveTests` for this read-only
scenario: it materializes both parts, previews identical full and patch edits,
then checks unchanged source and version tokens. It neither creates a fixture
nor saves, selects a KB version or invokes lifecycle. Confirm the authorized
active version before running; destination pins still apply to previews. A
skipped live test does not establish SDK behavior or safe persistence.

Create a synthetic KB through GeneXus or import a verified synthetic XPZ into a
new KB. Independently verify both the Knowledge Base database and generated
application datastores are dedicated to this fixture. Copying a KB directory can
retain the original SQL identity and is insufficient. Record the provisioning
evidence, SDK/generator versions and fixture revision before running tests.

Keep the manifest under ignored `scratchpad/`, with anonymized database IDs and
an evidence reference. Never include connection strings or credentials. Example
(replace every value with the actual provisioned fixture):

```json
{
  "schemaVersion": 1,
  "fixtureId": "synthetic-small-r1",
  "fixtureRevision": "seed-2026-09-05",
  "generator": "GeneXus18-net",
  "kbPath": "C:\\fixtures\\synthetic-small",
  "synthetic": true,
  "disposable": true,
  "isolation": {
    "verified": true,
    "kbDatabaseId": "dedicated-kb-01",
    "applicationDatabaseId": "dedicated-app-01",
    "evidence": "provisioning-record-01",
    "provisionedBy": "GeneXus",
    "verifiedAt": "2026-09-05T12:00:00Z"
  }
}
```

The manifest is an operator attestation, not automated proof of SQL isolation.
Revalidate it whenever a KB is restored or its datastores change. `provisionedBy`
accepts `GeneXus` or `XPZ`. Do not fabricate this record to bypass the gate.
Missing SDK, license, SQL access or synthetic seed means live validation remains
unavailable; fake/unit test success does not replace it.

The harness compares the manifest provenance hashes before a run. For the `.gxw`
workspace it canonicalizes XML line endings and removes only GeneXus-owned
`FriendlyVersion` and `VersionNumber` fields, which the SDK may rewrite while
opening the KB. The connection file is compared byte-for-byte. A mismatch fails
closed and requires a new fixture revision; it is never silently folded into an
existing baseline.

Use the repository generator after the isolation evidence is available. It
requires an explicit `-ConfirmIsolated`, writes UTF-8 JSON without credentials,
calculates both provenance hashes, and refuses to overwrite an existing record
unless `-Force` is supplied for that exact local path:

```powershell
pwsh -NoProfile -File scripts/new-live-fixture-manifest.ps1 `
  -KbPath C:\fixtures\synthetic-small `
  -FixtureId synthetic-small-r1 `
  -FixtureRevision seed-2026-09-05 `
  -Generator GeneXus18-net `
  -KbDatabaseId dedicated-kb-01 `
  -ApplicationDatabaseId dedicated-app-01 `
  -Evidence provisioning-record-01 `
  -ProvisionedBy GeneXus -ConfirmIsolated
```

The generator requires exactly one `.gxw` and one `knowledgebase.connection`.
The database IDs and evidence are still supplied by the operator; the script
does not pretend to prove SQL isolation.

## Execution

```powershell
pwsh -NoProfile -File scripts/tests/test-live.test.ps1
pwsh -NoProfile -File scripts/test-live.ps1 `
  -KbPath C:\fixtures\synthetic-small `
  -FixtureManifest scratchpad\synthetic-small.fixture.json `
  -SkipBuild -RunBenchmark -Iterations 100 `
  -BenchmarkOut scratchpad\synthetic-small.warm.json
```

For a release-critical smoke without the slower optional scenarios, select its
dedicated category and a smaller per-RPC budget explicitly:

```powershell
pwsh -NoProfile -File scripts/test-live.ps1 `
  -KbPath C:\fixtures\synthetic-small `
  -FixtureManifest scratchpad\synthetic-small.fixture.json `
  -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus18' `
  -SkipBuild -TestFilter 'Category=LiveEvents' -RpcTimeoutSeconds 180
```

The script creates a unique config and log directory, passes the exact
published Gateway path to the test harness, selects a free isolated port, and
requires the log to prove that this process entered master stdio mode. A proxy,
duplicate instance, wrong process image, occupied explicit port, or timeout is
reported as a failed/unavailable gate with the run log path. Use the same
`-TestFilter` with `test-live-matrix.ps1` to run the focused smoke for every
selected SDK major.

For classic GX8/GX9, pass the installation folder containing `gxw32.exe`/`gx.exe`
as `-GxPath` and use `-SkipBuild` after publishing the Worker with a native SDK.
The harness recognizes the classic runtime anchor and keeps the prerequisite
failure explicit when the matching GXPublic provider or a real legacy KB is not
available; it never attempts to compile the Worker against the classic folder.
For a mixed installation, configure `Environment.KBs[]`/the object catalog with
the legacy KB's `Driver: "com-gxpublic"`, `InstallationPath`, and `Major: "8"`
or pass those same fields to `genexus_kb action=open`; the Gateway routes that
worker without replacing the global native SDK configuration.

## Reload and lifecycle smoke

`scripts/tests/test-live-reload-smoke.ps1` is a permanent fail-closed smoke for
the worker reload and lifecycle surface (soft drain+replace, forced alias
reload through the shared restore core, and the `mode=hard` guards), exercising
the decomposed partial classes end to end:

```powershell
pwsh -NoProfile -File scripts/tests/test-live-reload-smoke.ps1 `
  -KbPath C:\KBs\KBTeste
```

Parameters: `-KbPath` (or `GXMCP_SMOKE_KB`), `-GxPath`, `-GatewayExe` (defaults
to `publish\GxMcp.Gateway.exe`), `-HttpPort` (random free port by default).
Exit codes: `0` pass, `1` failed checks, `2` unavailable (missing gateway
artifact or KB path). The run writes a structured summary JSON and an isolated
`GXMCP_LOG_DIR` under ignored `scratchpad/`.

### Multi-major matrix

Run the catalog-driven matrix when the same fixture must be checked with more
than one installed SDK:

```powershell
pwsh -NoProfile -File scripts/test-live-matrix.ps1 `
  -KbPath C:\fixtures\synthetic-small `
  -FixtureManifest scratchpad\synthetic-small.fixture.json `
  -Majors 17,18 `
  -GxPathMap '17=C:\Program Files (x86)\GeneXus\GeneXus17Trial;18=C:\Program Files (x86)\GeneXus\GeneXus18' `
  -SkipBuild -RequireBuildAll -RunBenchmark -Iterations 100 `
  -SummaryPath scratchpad\synthetic-small.matrix.json
```

Without `-Majors`, the matrix selects every major in `config/gx-versions.json`.
Without `-GxPathMap`, it uses each catalog entry's `defaultInstallPath`.
Omit `-SkipBuild` when the matrix should build the published artifact once with
the catalog primary SDK; use `-SkipBuild` in release preflight after the release
artifact has already been built. Each row invokes `test-live.ps1` with that
major's SDK and records `passed`, `unavailable`, or `failed` in the
`gxmcp-live-matrix/1` summary. A matrix is passing only when every selected row
passes; unavailable SDKs, licenses, fixtures, or cloud dependencies remain
explicit gaps and never become green evidence.

For the native incremental Build All gate, use the current published Gateway
and require terminal evidence explicitly:

```powershell
pwsh -NoProfile -File scripts/live-build-all.ps1 `
  -KbPath C:\fixtures\synthetic-small `
  -FixtureManifest scratchpad\synthetic-small.fixture.json `
  -GatewayExe publish\GxMcp.Gateway.exe `
  -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus18'
```

`live=pass` requires `buildMode=BuildAll`, `kbOpened=true`,
`buildAllDone=true`, `reorgRequired=false`, `msBuildExitCode=0`, and a
nonempty `fullLogPath`. An exit code of zero without completion evidence is a
failure. A fixture that reaches the GeneXus cloud step without a configured
`User` returns `live=unavailable` (exit code 2) with the environment reason;
it is never counted as a Build All pass. Add `-RequireBuildAll` to
`test-live.ps1`, or set `GXMCP_REQUIRE_LIVE_BUILD_ALL=1` for the preflight, to
make this gate mandatory.

Para comparar uma baseline em modo de gate, informe a identidade completa da
população ao benchmark. O comando recusa baseline sem esses campos ou com
fixture, revisão, gerador/SDK, estado de cache, concorrência, iterações ou
operações diferentes:

```powershell
python scripts/bench-live-http.py --kb C:\fixtures\synthetic-small `
  --fixture-id synthetic-small-r1 --fixture-revision seed-2026-09-05 `
  --generator "GeneXus18-net" --cache-mode warm --concurrency 1 `
  --iterations 12 --ops whoami,kb_list,list_objects,query,search_source,inspect,read,lifecycle_status,pattern_diagnose `
  --compare scratchpad\synthetic-small.baseline.json --fail-on-regression `
  --out scratchpad\synthetic-small.current.json
```

Alternatively set `GXMCP_TEST_KB` and `GXMCP_TEST_FIXTURE`. Without `-SkipBuild`,
the existing `build.ps1` rebuilds publish and may stop processes from this
checkout; schedule that build separately if another client is using it.

The harness rejects occupied ports, verifies the benchmark listener belongs to
its gateway, and passes a generated config containing only the fixture via
`GX_CONFIG_PATH`. Each run retains that credential-free config under
`scratchpad/live-<id>/` for diagnosis. It restores inherited environment variables
on exit. Benchmark cleanup selects descendants by parent PID and creation time,
checks identity again before stopping, and never selects workers by directory.
An already exited parent or failed process enumeration can leave an orphan;
cleanup reports a warning and does not broaden termination to other instances.
The C# harness also emits the selected RPC timeout, process-exit state, stderr
tail, and gateway log path in timeout diagnostics; sensitive key/value values in
the stderr tail are redacted.

## Evidence and remaining gates

The PowerShell regression test loads the production functions through their AST
and uses synthetic process snapshots. It starts no gateway and touches no KB.
It proves fixture rejection and cleanup selection, not live persistence.

For a performance baseline record three cold runs and at least 100 warm samples
per operation for each synthetic size. Retain revision, fixture revision,
generator/SDK versions, hardware, cache state, concurrency and operation inputs
alongside benchmark JSON in ignored `scratchpad/`. Compare only equivalent
populations. Never count failed operations as fast successful samples.

The benchmark stores successful response-byte p50/p95 alongside latency and
never includes failed or skipped calls in either population.

Latency runs over one keep-alive connection (`http_post` in
`scripts/bench-live-http.py`); do not reintroduce a connection per call. A
CPython socket operation carrying a timeout waits through `select()` on Windows,
whose granularity is the ~15.6ms system timer, so roughly one sample in three
used to measure the client's own connect while the gateway answered those calls
in ~1ms. Baselines recorded before that change are not comparable and must be
re-captured. The existing Worker gate currently checks SDK type resolution only. Real
write/reopen persistence, pattern parity, mandatory-scenario/no-skip enforcement,
and cold/warm baseline captures remain required by plan 074; manifests are
optional benchmark metadata and are not required to use a local KB.
