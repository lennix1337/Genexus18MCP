# Genexus18MCP Release Protocol

Release-facing instructions moved out of `AGENTS.md` so normal implementation
tasks load a smaller instruction file. These rules remain normative whenever a
release, merge, or changelog edit is requested.

## Explicit release gate

Do not run `release.ps1`, create tags, push release branches, or publish a
GitHub Release because a change looks ready. Shipping requires the maintainer's
explicit request for that change. A prior approval never carries to a later
release. Before a release, `CHANGELOG.md` must contain a substantive
`## Unreleased` section; `release.ps1` promotes it into the exact version entry.

## Standard release execution

This project ships both the GitHub Release and the npm package `genexus-mcp`.
Use the one-shot script (the only implementation entrypoint):

```powershell
./release.ps1 -Version <X.Y.Z>
```

It bumps versions, synchronizes both npm lockfiles, SDK project files, and the
catalog-generated release metadata, commits that source state before building,
creates the normalized `publish.zip`,
embeds `gxmcp-manifest.json` with artifact hashes and protocol revisions, and
creates the GitHub release with the zip, checksum, and Nexus VSIX attached. The
manifest source commit must equal the tag commit. Do not run `gh release create` manually: the release workflow
requires `publish.zip` on the initial published event. The Worker needs the
local primary SDK from `config/gx-versions.json`, so the release artifact must
built on Windows with that supported GeneXus installation.

To close completed issues as part of the same release, pass them explicitly:

```powershell
./release.ps1 -Version <X.Y.Z> -CloseIssues 146,148
```

The script reads each issue, comments the verified release URL, closes it, and
reads the issue back to verify `state=closed`. It never infers issues from
changelog text; omit `-CloseIssues` to leave issue state untouched.

Gateway, tests, and benchmarks build with the .NET 10 SDK; the Worker remains
.NET Framework 4.8/x86 for the GeneXus SDK. The v3 corporate installer stages
and probes an archive before swapping it into place, validates the manifest and
checksum, preserves operator configuration, and retains the previous directory
for rollback. Legacy releases keep an explicitly versioned compatibility path.

Use `-DryRun` to rehearse the changelog, artifact, warning, and release-note
checks without changing Git state or deleting existing package artifacts. Dry
runs label remote actions as `[DRY-RUN]` and never report a release URL as
created.

Before a release, the local matrix can be run independently:

```powershell
pwsh -NoProfile -File .\scripts\release-preflight.ps1 -GxPath $env:GX_PATH `
  -SummaryPath "$env:TEMP\gxmcp-release-preflight.json"
```

The summary uses schema `gxmcp-release-preflight/1` and records each phase's
`name`, `command`, `status`, `exitCode`, `durationSeconds`, timestamps, and an
optional `reason`. Live KB validation is skipped with an explicit reason when
no verified fixture is configured; set `GXMCP_REQUIRE_LIVE_BUILD_ALL=1` to make
the live Build All gate mandatory.

Release progress is written atomically to a status file under `%TEMP%` by
default. Read it with `scripts/release-status.ps1`; terminal states are
`succeeded` and `failed`, while exit code 2 means the run is still in progress
or the requested wait elapsed.

The release script synchronizes `server.json`, `config.sample.json`,
`README.md`, `AGENTS.md`, and `docs/generated/supported-versions.md` from the
version catalog before its dirty-tree gate. It refuses a missing or ambiguous
generated block and the CI/release metadata check fails if those files drift.
The release script requires a substantive `## Unreleased` section when the
target version heading is absent, promotes that section, verifies the exact
version heading, and refuses to publish generic release notes.
If a local build fails after the metadata commit, leave that untagged commit in
place, fix the cause, and rerun the same version; do not manually rewrite the
manifest or tag a different source tree.
If a tag already has a GitHub Release without `publish.zip`, the same command
resumes with an asset upload and preserves the existing release record.

After publishing, verify both channels:

```powershell
gh run list --workflow release.yml
npm view genexus-mcp@latest version
```

Issues are closed only after the released fix is available. Comment on the
issue with the release URL first, then close it.

## Merge discipline

Two PRs that both edit `CHANGELOG.md` can conflict regardless of merge order.
Before merging, probe with `git merge-tree --write-tree` and, when needed, a
read-only `git commit-tree` simulation. For a fork PR, resolve the Unreleased
sections in a temporary worktree, preserve CRLF, commit with the canonical
GitHub merge message, and push the explicit ref. After a manual main update,
rebase the local branch onto `origin/main` and resolve the changelog by
combining sections in project order.

Before merging, run the executable gate and wait for every local/independent
review to finish before acting on its result:

```powershell
.\scripts\pr-preflight.ps1 -PullRequest <number>
```

The gate requires an open, non-draft, cleanly mergeable PR, an approved GitHub
review, and passing reported checks. For a fork PR, update its head only with
the repository/ref resolved by GitHub CLI:

```powershell
.\scripts\pr-push.ps1 -PullRequest <number> -ForceWithLease
```

The helper rejects pushes from `main` and pins the exact remote head OID for a
force-with-lease push, preventing a same-named branch from being updated in the
base repository by accident.

The architectural `ripwire` analysis is an optional local/CI quality gate because
it is not a runtime dependency of this repository. When unavailable, the PR
preflight exits successfully only if the required GitHub gates pass, reports the
analysis as `skipped`, and labels the final message as incomplete. Pass
`-RequireRipwire` when a local policy requires it; absence then fails with exit
code 127. If present, a nonzero `ripwire` exit code always fails the preflight.

## Live KB and performance gate

The normal CI workflow does not have the proprietary GeneXus SDK or a KB. On a
Windows machine with a supported GeneXus SDK installed, run the live gate against the
verified isolated synthetic KB. Provision and attest the fixture as described in
[the live harness guide](live-kb-test-harness.md); a folder name alone is not
evidence of isolation:

```powershell
.\scripts\test-live.ps1 -KbPath $env:GXMCP_TEST_KB `
  -FixtureManifest $env:GXMCP_TEST_FIXTURE -RequireBuildAll -RunBenchmark `
  -BenchmarkOut "$env:TEMP\gxmcp-live-benchmark.json" -Iterations 100
```

To validate every SDK major from the catalog against the same built
Gateway/Worker artifact, use the catalog-driven matrix. It builds the artifact
once with the catalog primary SDK unless `-SkipBuild` is supplied, then runs the
same fixture gate once per selected major:

```powershell
pwsh -NoProfile -File .\scripts\test-live-matrix.ps1 `
  -KbPath $env:GXMCP_TEST_KB `
  -FixtureManifest $env:GXMCP_TEST_FIXTURE `
  -RequireBuildAll -RunBenchmark -Iterations 100 `
  -SummaryPath "$env:TEMP\gxmcp-live-matrix.json"
```

Use `-Majors 17,18` to select a subset and `-GxPathMap
'17=C:\Program Files (x86)\GeneXus\GeneXus17Trial;18=C:\Program Files (x86)\GeneXus\GeneXus18'`
when an installation is not at the catalog default. The matrix writes
`gxmcp-live-matrix/1`; exit code `0` means every selected major passed, `2`
means the environment was unavailable, and `1` means a live check failed. An
unavailable major is never treated as a pass. `release-preflight.ps1` selects
this matrix automatically when `-LiveMajors`, `-LiveGxPathMap`,
`GXMCP_LIVE_MAJORS`, or `GXMCP_LIVE_GX_PATH_MAP` is supplied.

The manual `Live KB Smoke` workflow runs the same gate only on a self-hosted
Windows runner and requires both KB path and fixture manifest inputs. Its
default dispatch now runs the matrix for all catalog majors; missing SDKs or
fixtures fail with `live=unavailable`; they never count as release validation.
WorkWithPlus-licensed tests remain opt-in through
`GXMCP_REQUIRE_WWP=1`.

Compare benchmark runs only when both runs use the same KB, operation set,
iteration count, and comparable machine conditions. Add
`-BenchmarkBaseline <path>` to the runner to make a p50 regression above the
default 25% threshold fail the command; override it with
`--max-p50-regression` in the underlying Python harness when justified.

## Release warning gate

The machine-readable source of truth is `docs/build_warning_baseline.json`.
Validate its shape without the SDK, or regenerate it only after reviewing a
real Release rebuild:

```powershell
.\scripts\check-build-warning-baseline.ps1 -ValidateOnly
.\scripts\check-build-warning-baseline.ps1 -UpdateBaseline -GxPath `
  'C:\Program Files (x86)\GeneXus\GeneXus18'
```

The release script runs the non-update check automatically and fails on
`MSB3277` or any new `(code, file, line)` warning location. Line-only moves are
reported as `moved` and do not hide genuinely new diagnostics.

## npm version verification

The npm registry can show a new version before the npmjs.com rendered page
updates. Treat `npm view` and the registry endpoint as authoritative; do not
re-cut a release because the website CDN still shows an older version.

If a user is actually running an old install, check multiple binaries with
`where.exe genexus-mcp`, clear stale npm metadata only when appropriate, and
confirm the result with `genexus-mcp doctor`.

## Changelog voice

`CHANGELOG.md` is user-facing. Use `### Added`, `### Fixed`, `### Changed`, and
`### Removed` in that order, with `### Internal` last for engineer-only notes.
Each user-facing bullet should lead with the capability or behavior, use plain
English and past tense for fixes, and avoid roadmap codes, session narratives,
agent IDs, commit hashes, KB-specific names, and implementation dumps. Do not
put test counts in user-facing sections. Every merged PR's user-facing work
must include the contributor credit and PR links before release.
