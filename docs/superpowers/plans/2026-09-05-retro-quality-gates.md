# Release and Quality Gates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the retrospective recommendations into executable merge, live-KB, release, warning-baseline, and performance gates.

**Architecture:** Keep the gates in small PowerShell helpers and reuse the existing Python live benchmark. The normal CI remains SDK-independent; a manual self-hosted Windows workflow runs live tests when GeneXus 18 and a test KB are available. Release preflight validates the changelog and the warning manifest before packaging.

**Tech Stack:** PowerShell 7, GitHub CLI, GitHub Actions, .NET/xUnit, Python standard library, JSON.

---

### Task 1: Add fork-safe PR preflight and push helpers

**Files:**
- Create: `scripts/pr-preflight.ps1`
- Create: `scripts/pr-push.ps1`
- Modify: `docs/release_protocol.md`
- Test: PowerShell parser validation and `-PullRequest 133` safe failure after the PR is merged.

- [x] **Step 1: Create a read-only PR gate**

Implement `scripts/pr-preflight.ps1 -PullRequest <number>` to require an open, non-draft PR with `mergeStateStatus=CLEAN`, `reviewDecision=APPROVED`, all reported checks passing, and an explicit base/head repository and ref. It must exit non-zero with an actionable message for any failed gate.

- [x] **Step 2: Create an explicit fork-safe push command**

Implement `scripts/pr-push.ps1 -PullRequest <number> [-ForceWithLease]` to resolve the PR head repository/ref/OID through `gh pr view`, reject pushes from `main`, and push `HEAD` directly to the resolved head repository. `-ForceWithLease` must pin the exact remote OID returned by GitHub.

- [x] **Step 3: Document the required sequence**

Update `docs/release_protocol.md` with the exact preflight and push commands, including waiting for local review completion before merge and using the helper for fork PRs.

- [x] **Step 4: Validate the helpers**

Run PowerShell parser validation for both scripts and run `pwsh -File scripts/pr-preflight.ps1 -PullRequest 133`; expected result is a clear non-zero “PR must be OPEN” failure without any write.

### Task 2: Make live KB validation repeatable

**Files:**
- Create: `scripts/test-live.ps1`
- Create: `.github/workflows/live-smoke.yml`
- Modify: `docs/release_protocol.md`
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Modify: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
- Modify: `src/GxMcp.Gateway.Tests/McpHandshakeContractTests.cs`
- Test: `scripts/test-live.ps1 -KbPath C:\KBs\KBTeste` with the Gateway live smoke and selected Worker SDK test.

- [x] **Step 1: Add a local live-test runner**

Implement `scripts/test-live.ps1` with `-KbPath`, `-GatewayOnly`, `-RunBenchmark`, `-BenchmarkBaseline`, `-BenchmarkOut`, and `-Iterations`. It must validate the KB directory, set `GXMCP_TEST_KB`, require the published Gateway artifact, run the live Gateway tests, optionally run the SDK type-resolution test, and invoke the existing benchmark with `--fail-on-regression` when a comparison baseline is supplied.

- [x] **Step 2: Add an opt-in self-hosted workflow**

Create `.github/workflows/live-smoke.yml` with `workflow_dispatch`, a `kb_path` input defaulting to `C:\KBs\KBTeste`, and a `[self-hosted, windows]` runner. It must invoke the local runner and store benchmark output as an artifact; it must never run on ordinary hosted CI.

- [x] **Step 3: Document the live gate**

Document the local command and the self-hosted runner requirements. State that WWP-licensed tests remain opt-in through `GXMCP_REQUIRE_WWP=1`.

- [x] **Step 4: Run the live gate against the supplied KB**

Set `GXMCP_TEST_KB` through the runner and execute the Gateway live smoke plus the non-WWP Worker SDK resolution test. Record pass/skip/failure counts without committing machine-specific benchmark output.

### Task 3: Add a machine-readable warning baseline guard

**Files:**
- Create: `scripts/check-build-warning-baseline.ps1`
- Create: `docs/build_warning_baseline.json`
- Modify: `docs/build_warning_baseline.md`
- Modify: `.github/workflows/ci.yml`
- Modify: `release.ps1`
- Test: baseline generation, validation-only mode, and a Release check with the GeneXus SDK.

- [x] **Step 1: Implement baseline parsing and validation**

Implement `scripts/check-build-warning-baseline.ps1` with `-BaselineFile`, `-GxPath`, `-UpdateBaseline`, and `-ValidateOnly`. Normalize warnings to distinct `(code,file,line)` entries relative to the repository, fail on `MSB3277` or newly introduced warning locations, and keep output to a compact summary.

- [x] **Step 2: Generate the checked-in manifest**

Run the guard with `-UpdateBaseline` using the configured GeneXus SDK and preserve the existing 216-location baseline unless the current build proves a real change.

- [x] **Step 3: Wire the guard into CI and release**

CI validates the manifest format without requiring the proprietary SDK. `release.ps1` runs the full warning guard before packaging so a release cannot silently add warnings.

- [x] **Step 4: Update the baseline documentation**

Make the Markdown document describe the JSON manifest as the source of truth and give the exact update command.

- [x] **Step 5: Validate the guard**

Run validation-only mode, the SDK-backed Release check, and the existing CI-compatible contract checks. Expected result: zero new warning locations and zero `MSB3277` diagnostics.

### Task 4: Harden release changelog invariants

**Files:**
- Modify: `release.ps1`
- Modify: `docs/release_protocol.md`
- Modify: `CHANGELOG.md`
- Test: `release.ps1 -DryRun -Version <next-version>` and a temporary missing/empty `Unreleased` fixture through the parser-level helper logic.

- [x] **Step 1: Replace the best-effort warning**

Require a non-empty `## Unreleased` section before promotion when the target version heading is absent. After promotion, verify the exact version heading exists.

- [x] **Step 2: Reject generic release notes**

Fail if release-note extraction produces no substantive changelog body instead of publishing a generic fallback.

- [x] **Step 3: Document the invariant**

Clarify that the script owns promotion from `Unreleased`, but refuses an empty or missing section.

- [x] **Step 4: Validate dry-run behavior**

Run the release script in dry-run mode and confirm it reports the promotion/check without changing Git state.

### Task 5: Add a failing performance regression option

**Files:**
- Modify: `scripts/bench-live-http.py`
- Modify: `docs/release_protocol.md`
- Test: benchmark help, baseline comparison without regression, and an isolated synthetic comparison that exits non-zero over the configured threshold.

- [x] **Step 1: Add threshold arguments**

Add `--max-p50-regression` (default `25.0`) and `--fail-on-regression`. Return a non-zero process status only when comparison mode finds an operation above the threshold and the flag is present.

- [x] **Step 2: Keep benchmark output comparable**

Preserve the existing p50/p95 table and JSON shape, adding only the threshold/exit behavior needed for automation.

- [x] **Step 3: Document the same-KB rule**

Require baseline and current runs to use the same KB, operation set, iteration count, and comparable machine conditions before treating a delta as a regression.

- [x] **Step 4: Validate the command**

Run `python scripts/bench-live-http.py --help` and the existing Python syntax check. Use a temporary JSON pair for pass/fail exit-code validation.

### Task 6: Final integration and review

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/plans/2026-09-05-retro-quality-gates.md`

- [x] **Step 1: Record the environment improvements**

Add one concise `## Unreleased` entry describing the new merge, live-KB, release, warning, and performance gates.

- [x] **Step 2: Run repository validation**

Run `dotnet test Genexus18MCP.sln`, `npm test`, `npm run lint`, `npm audit --json`, JSON parsing, warning validation, live smoke, and `git diff --check`.

- [x] **Step 3: Review scope and status**

Confirm only the planned files changed, no benchmark output or KB data is tracked, and report that commit/push/release still require a fresh explicit request.
