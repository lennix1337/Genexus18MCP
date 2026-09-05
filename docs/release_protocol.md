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
Use the one-shot script:

```powershell
.\release.ps1 -Version <X.Y.Z>
```

It bumps versions, builds Gateway and Worker, creates the normalized
`publish.zip` and checksum, commits/tags, and creates the GitHub release with
the zip attached. Do not run `gh release create` manually: the release workflow
requires `publish.zip` on the initial published event. The Worker needs the
local GeneXus 18 SDK, so the release artifact must be built on Windows with
GeneXus installed.

Use `-DryRun` to rehearse the changelog, artifact, warning, and release-note
checks without changing Git state or deleting existing package artifacts.

The release script requires a substantive `## Unreleased` section when the
target version heading is absent, promotes that section, verifies the exact
version heading, and refuses to publish generic release notes.

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

## Live KB and performance gate

The normal CI workflow does not have the proprietary GeneXus SDK or a KB. On a
Windows machine with GeneXus 18 installed, run the live gate against the
provided test KB (or another disposable KB):

```powershell
.\scripts\test-live.ps1 -KbPath 'C:\KBs\KBTeste' -RunBenchmark `
  -BenchmarkOut "$env:TEMP\gxmcp-live-benchmark.json" -Iterations 12
```

The manual `Live KB Smoke` workflow runs the same gate only on a self-hosted
Windows runner. WorkWithPlus-licensed tests remain opt-in through
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
`MSB3277` or any new `(code, file, line)` warning location.

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
