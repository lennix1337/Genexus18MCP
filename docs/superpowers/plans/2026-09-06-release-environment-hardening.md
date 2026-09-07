# Release Environment Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the MCP 3.0 release workflow reproducible, provenance-safe, observable, and complete across release artifacts, live Build All validation, warning baselines, version metadata, and preflight checks.

**Architecture:** Keep `release.ps1` as the single maintainer entrypoint. It will create one committed release state before writing the artifact manifest, then package the release and publish the checksum as an asset. A reusable preflight script will own the full local validation matrix, while the live harness will expose an explicit Build All gate that reports unavailable fixtures without treating them as passes.

**Tech Stack:** PowerShell 7, Git/GitHub CLI, .NET 10/xUnit, Python 3 `unittest`, npm/Node 22, GeneXus 18 SDK, GitHub Actions.

---

### Task 1: Make artifact provenance bind exactly to the release commit

**Files:**
- Modify: `release.ps1`
- Modify: `scripts/write-release-manifest.ps1`
- Modify: `scripts/verify-release-manifest.py`
- Modify: `.github/workflows/release.yml`
- Modify: `.gitignore`
- Create: `scripts/tests/test-release-orchestration.ps1`
- Test: `scripts/tests/release-manifest.tests.ps1`, `scripts/tests/test_verify_release_manifest.py`

- [x] **Step 1: Define the release commit contract in tests.**

Add a temporary-repository test that creates a clean Git repository, commits a versioned source state, writes a manifest, and asserts `manifest.sourceCommit` equals `git rev-parse HEAD`; add a negative assertion that `working-tree` is rejected. Add an archive test that downloads both `publish.zip` and `publish.zip.sha256` as release assets and verifies the hash.

Run:

```powershell
pwsh -NoProfile -File scripts/tests/test-release-orchestration.ps1
python -m unittest scripts/tests/test_verify_release_manifest.py -v
```

Expected: the new orchestration test fails before the implementation because the current release order writes the manifest before committing.

- [x] **Step 2: Commit the release metadata before manifest generation.**

In `release.ps1`, after version/changelog promotion and before `build.ps1`, stage only the release-managed tracked files and create the release commit. Keep `-AllowDirty` as the explicit opt-in for other tracked paths. The manifest writer must then observe a clean committed tree. Remove the later version-bump commit and replace it with an assertion that only ignored/generated files changed.

The release sequence must be:

```text
promote metadata -> commit release state -> build -> tests -> manifest -> zip -> checksum asset -> tag commit -> push -> GitHub release
```

- [x] **Step 3: Move the checksum out of the tagged source tree.**

Add `publish.zip.sha256` to `.gitignore`, remove the tracked sidecar from the repository index, and keep it attached to the GitHub Release. Update the workflow to download `publish.zip.sha256` as an asset and verify it when present. The release tag must now point to the same commit recorded in `gxmcp-manifest.json`.

- [x] **Step 4: Verify the manifest against the tag commit.**

Extend `verify-release-manifest.py` with an optional `--source-commit` argument. Pass the resolved tag commit from `.github/workflows/release.yml` and fail if the manifest source differs. Keep the existing rejection of `working-tree`.

- [x] **Step 5: Run the focused release tests.**

Run:

```powershell
pwsh -NoProfile -File scripts/tests/release-manifest.tests.ps1
python -m unittest scripts/tests/test_verify_release_manifest.py -v
pwsh -NoProfile -File scripts/tests/test-release-orchestration.ps1
```

Expected: all tests pass and a generated manifest records the exact committed source identifier.

### Task 2: Consolidate release entrypoints and version metadata

**Files:**
- Modify: `scripts/release.ps1`
- Modify: `docs/RELEASE.md`
- Modify: `docs/release_protocol.md`
- Modify: `package-lock.json`
- Modify: `src/nexus-ide/package-lock.json`
- Modify: `release.ps1`
- Create: `scripts/tests/test-release-entrypoint.ps1`

- [x] **Step 1: Make the legacy script a strict wrapper.**

Replace the old implementation in `scripts/release.ps1` with a wrapper that resolves the repository root and invokes `release.ps1`, translating `patch`, `minor`, `major`, and explicit semver values to `-Version`. Reject `-NoBump` with a message pointing to the canonical command. The wrapper must not contain an independent build, tag, or publish implementation.

- [x] **Step 2: Update all release documentation.**

Make `docs/RELEASE.md` and `docs/release_protocol.md` show `pwsh -NoProfile -File .\release.ps1 -Version X.Y.Z` as the only supported command, and link to the wrapper only for backwards-compatible callers.

- [x] **Step 3: Enforce lockfile version parity.**

Add a release metadata check that reads the root package version, the root lockfile `version` and `packages[""] .version`, and the Nexus package/lockfile versions. Fail with the file and observed value when any differs. Synchronize the current lockfiles to `3.0.0` and add the parity check to the preflight suite.

- [x] **Step 4: Test entrypoint and parity behavior.**

Run:

```powershell
pwsh -NoProfile -File scripts/tests/test-release-entrypoint.ps1
python scripts/validate-tool-contracts.py
```

Expected: the wrapper delegates to the root script, stale lockfile fixtures fail, and synchronized metadata passes.

### Task 3: Make warning baseline updates move-aware and self-consistent

**Files:**
- Modify: `scripts/check-build-warning-baseline.ps1`
- Modify: `docs/build_warning_baseline.md`
- Modify: `docs/build_warning_baseline.json`
- Create: `scripts/tests/test-warning-baseline.ps1`

- [x] **Step 1: Add a move-aware diagnostic fingerprint.**

Keep the exact `(code,file,line)` policy for blocking new warnings, but emit a second comparison that groups removed/new entries by `(code,file)` and labels line-only shifts as `moved`. The failure message must list true new diagnostics separately from moved locations.

- [x] **Step 2: Validate the documentation count from JSON.**

Remove hard-coded totals from the Markdown table or add a checked command that reads `warningCount` and per-project totals from the JSON. Update the documented current count to 218 and make the test fail when the Markdown total disagrees with the machine-readable manifest.

- [x] **Step 3: Test line shifts and true additions.**

Use temporary manifests with one warning moved to a new line and one new warning code. Assert the first is reported as `moved` and the second causes a nonzero gate result.

- [x] **Step 4: Run the baseline checks.**

Run:

```powershell
pwsh -NoProfile -File scripts/tests/test-warning-baseline.ps1
pwsh -NoProfile -File scripts/check-build-warning-baseline.ps1 -ValidateOnly
```

Expected: the checked-in JSON and Markdown agree, and the real SDK-backed check still rejects genuine new warnings and `MSB3277`.

### Task 4: Add a complete local release preflight

**Files:**
- Create: `scripts/release-preflight.ps1`
- Modify: `release.ps1`
- Create: `scripts/tests/test-release-preflight.ps1`
- Modify: `docs/release_protocol.md`
- Modify: `CHANGELOG.md`

- [x] **Step 1: Implement explicit preflight phases.**

Create `release-preflight.ps1` with `-GxPath`, `-SkipLive`, and `-SummaryPath`. Run these phases in order and stop on the first failure: solution build/test, `npm test`, `npm run lint`, Nexus `npm run check`, contract validation, operation inventory check, v3 plan readiness, Python script tests, and warning baseline check. Emit one JSON summary with command, exit code, duration, and status per phase.

- [x] **Step 2: Replace the release script's Gateway-only test.**

Call the preflight from `release.ps1` unless `-SkipTests` is explicitly supplied. Preserve the existing warning gate even when tests are skipped. `-SkipTests` must be visible in the status summary as an intentional skip.

- [x] **Step 3: Add deterministic preflight tests.**

Test command ordering, nonzero propagation, summary JSON shape, and the explicit skip behavior using mocked commands in a temporary directory. Do not start a Gateway or touch a KB in these tests.

- [x] **Step 4: Document the gate.**

Document the exact command and expected summary fields in `docs/release_protocol.md`. Add a user-facing changelog entry under `## Unreleased` describing the complete preflight.

### Task 5: Add an explicit live Build All gate

**Files:**
- Modify: `scripts/test-live.ps1`
- Modify: `docs/live-kb-test-harness.md`
- Modify: `scripts/tests/test-live.test.ps1`
- Create: `scripts/live-build-all.ps1`
- Create: `scripts/tests/test-live-build-all.ps1`
- Modify: `CHANGELOG.md`

- [x] **Step 1: Define the live Build All command contract.**

Create `scripts/live-build-all.ps1` with required `-KbPath`, `-FixtureManifest`, and `-GatewayExe` arguments. Start an isolated Gateway, perform MCP initialize, call `genexus_lifecycle` with `{ action: "build_all", kb: <alias>, wait: 1 }`, poll `genexus_lifecycle action=result` until terminal, and require `buildMode=BuildAll`, `kbOpened=true`, `buildAllDone=true`, `reorgRequired=false`, and a valid `fullLogPath`. Return `live=pass` only when every field is present.

- [x] **Step 2: Report unavailable fixtures without false passes.**

When the KB lacks the required cloud `User`, return `live=unavailable` with the exact environment reason and exit code 2. Never reinterpret `msBuildExitCode=0` as success when completion evidence is false.

- [x] **Step 3: Wire the optional gate into `test-live.ps1`.**

Add `-RequireBuildAll` and `-BuildAllTimeoutSeconds`. Run the dedicated command after the existing Gateway live smoke and before benchmarking. Keep it opt-in for generic fixtures, but make the release preflight require it when `GXMCP_REQUIRE_LIVE_BUILD_ALL=1` is set.

- [x] **Step 4: Test the live gate without a real Gateway.**

Load production parsing functions through the AST and test success, reorganization, missing evidence, timeout, and missing `User` result fixtures. Assert that `msBuildExitCode=0` with `buildAllDone=false` fails.

- [x] **Step 5: Document fixture requirements.**

Update the harness guide with the required cloud `User` setup, the exact command, terminal evidence fields, and the distinction between `live=pass` and `live=unavailable`.

### Task 6: Add release status and clearer dry-run output

**Files:**
- Modify: `release.ps1`
- Create: `scripts/release-status.ps1`
- Create: `scripts/tests/test-release-status.ps1`
- Modify: `docs/release_protocol.md`
- Modify: `CHANGELOG.md`

- [x] **Step 1: Define the status schema.**

Write an atomic status file with `{version, tag, phase, state, pid, updatedAtUtc, releaseUrl, workflowRunId, exitCode, error}`. Update it at each phase transition and on both success and failure. Keep it under `%TEMP%` by default and print its path at startup.

- [x] **Step 2: Add a status reader.**

Implement `scripts/release-status.ps1 -Path <status>` to print the latest phase, state, elapsed time, release URL, workflow ID, and error. With `-WaitSeconds N`, poll at one-second intervals and exit 0 for success, 1 for failure, and 2 for timeout.

- [x] **Step 3: Make dry-run messages explicit.**

Change messages such as `[OK] Release created` to `[DRY-RUN] would create GitHub release` and never emit a release URL as if it existed. Keep the final dry-run summary machine-readable.

- [x] **Step 4: Test status transitions.**

Use a temporary status file and mocked phase calls to assert atomic writes, failure capture, wait exit codes, and dry-run wording.

### Task 7: Full verification and integration

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/v3-integration-evidence-2026-09-06.md`

- [x] **Step 1: Run focused tests after each task.**

Run the task-specific PowerShell/Python tests before continuing to the next task, and keep the working tree clean between commits.

- [x] **Step 2: Run the complete validation matrix.**

Run:

```powershell
dotnet test Genexus18MCP.sln -v:minimal --no-restore
npm test
npm run lint
npm --prefix src/nexus-ide run check
python scripts/validate-tool-contracts.py
python scripts/generate-operation-contract-inventory.py --check
python scripts/validate-v3-plan.py --require-ready
python -m unittest discover -s scripts/tests -v
pwsh -NoProfile -File scripts/release-preflight.ps1 -SummaryPath scratchpad/release-preflight.json
```

Expected: all required phases pass, optional live phases report an explicit reason when the fixture is unavailable, and no secrets or KB credentials appear in artifacts.

- [x] **Step 3: Review the final diff and contracts.**

Check `git diff --check`, the release manifest/tag source commit, lockfile versions, baseline/documentation parity, release entrypoint references, and the status JSON schema. Remove only generated scratch artifacts.

- [x] **Step 4: Commit the hardening changes.**

```powershell
git add release.ps1 scripts .github/workflows/release.yml .gitignore docs CHANGELOG.md package-lock.json src/nexus-ide/package-lock.json
git commit -m "chore: harden release validation and provenance"
```

Expected: the commit is clean, pushed only after validation, and future releases have one documented entrypoint with a verifiable status and source binding.

### Verification record

- Focused release, manifest, live-gate, warning-baseline, entrypoint, orchestration, preflight, status, and metadata guards pass.
- The complete preflight passed with 12 phases: 11 passed and the optional live KB gate explicitly skipped because `GXMCP_TEST_KB`/`GXMCP_TEST_FIXTURE` were not configured.
- A dedicated run against `C:\kbs\KBTeste` reached the native Build All path and returned `live=unavailable` (exit 2) because GeneXus cloud requires `User`; evidence correctly showed `buildMode=BuildAll`, `kbOpened=true`, `buildAllDone=false`, `reorgRequired=false`, and `msBuildExitCode=0`.
- The hardening changes were committed and pushed after explicit authorization; the working tree is clean and `main` tracks `origin/main`.
