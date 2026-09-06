# Live KB test harness

`scripts/test-live.ps1` requires an explicit KB path and fixture manifest before
building, opening a KB, or starting a gateway. Missing prerequisites fail with
`live=unavailable` and a nonzero exit code. A directory called `KBTeste` is not
evidence that a database is disposable.

## Provisioning prerequisite

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

## Execution

```powershell
pwsh -NoProfile -File scripts/tests/test-live.test.ps1
pwsh -NoProfile -File scripts/test-live.ps1 `
  -KbPath C:\fixtures\synthetic-small `
  -FixtureManifest scratchpad\synthetic-small.fixture.json `
  -SkipBuild -RunBenchmark -Iterations 100 `
  -BenchmarkOut scratchpad\synthetic-small.warm.json
```

Para comparar uma baseline em modo de gate, informe a identidade completa da
população ao benchmark. O comando recusa baseline sem esses campos ou com
fixture, revisão, gerador/SDK, estado de cache, concorrência, iterações ou
operações diferentes:

```powershell
python scripts/bench-live-http.py --kb C:\fixtures\synthetic-small `
  --fixture-id synthetic-small-r1 --fixture-revision seed-2026-09-05 `
  --generator "GeneXus18-net" --cache-mode warm --concurrency 1 `
  --iterations 100 --compare scratchpad\synthetic-small.baseline.json `
  --fail-on-regression --out scratchpad\synthetic-small.current.json
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
never includes failed or skipped calls in either population. The existing Worker gate currently checks SDK type resolution only. Real
write/reopen persistence, pattern parity, mandatory-scenario/no-skip enforcement,
and cold/warm baseline captures remain required by plan 074; this harness safety
increment alone is not release acceptance.
