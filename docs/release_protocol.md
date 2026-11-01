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
For a prepared but unpublished same-version release, keep the substantive notes
in that version's entry and resume it; do not duplicate them or bump again.

## Source and backend acceptance

Before a backend/default release, complete the
[backend compatibility checklist](../CODING_STANDARDS.md#backend-and-default-changes).
Use the [isolated live harness](live-kb-test-harness.md#default-and-backend-compatibility-smoke)
for the same read → edit, query and error-contract tests across the default,
`sdk`, `auto` and `gxengine` policies. A configured backend label is not proof
that the backend served the request; retain routing evidence with the live
summary. Preserve the exact source commit and artifact fingerprint.

Obtain required checks on a candidate branch before advancing main. Required
checks apply to administrators as well; release authorization does not bypass
branch protection. A missing, skipped or unavailable required SDK/live result
blocks publication. Run one canonical preflight after integration, reconcile
failed/interrupted effects, and reuse only the exact-input evidence allowed by
the resume contract below.

When launching the canonical script through a logging shell, preserve its exit
code rather than returning the exit code of the log printer:

```bash
pwsh -NoProfile -File scripts/release.ps1 > "$log" 2>&1
rc=$?
tail -40 "$log"
exit "$rc"
```

A background start confirms dispatch only. Continue through the completion
notification, publication read-back, asset checks and npm visibility before
reporting success.

## Standard release execution

This project ships both the GitHub Release and the npm package `genexus-mcp`.
Use the one-shot script (the only implementation entrypoint):

```powershell
./release.ps1 -Minor                  # auto-bumps minor (e.g. 4.2.0 -> 4.3.0)
./release.ps1 -Patch                  # auto-bumps patch (e.g. 4.2.0 -> 4.2.1)
./release.ps1 -Version <X.Y.Z>        # explicit version
```

For a terminal or agent session that may time out, use
`./release.ps1 -Minor -Detach` (or `-Patch`, or `-Version <X.Y.Z>`) and poll the returned status path.

### Automated candidate CI & overlapped execution

By default, `release.ps1` automates candidate CI verification and overlaps execution to minimize release turnaround:
1. When a new release commit is created, it checks whether required status checks (`test`) are already green on that SHA.
2. If not, it pushes `candidate/vX.Y.Z` to `origin` and ensures an automated candidate PR exists to trigger GitHub Actions CI.
3. While cloud CI runs required checks on GitHub, the local machine concurrently builds `publish/`, compiles `nexus-ide.vsix`, and executes the canonical preflight (Worker, Gateway, live KB tests).
4. Right before tagging (`Step "Verifying pre-publication freshness"`), `release.ps1` waits for GitHub Actions CI to conclude with success.
5. Upon successful publication and verification, the candidate PR and remote candidate branch are cleaned up automatically.
6. To disable automated candidate CI and require manual preparation / pre-existing green CI (legacy mode), pass `-NoAutoCandidate`.

It bumps versions, synchronizes both npm lockfiles, SDK project files, and the
catalog-generated release metadata, commits that source state before building,
creates the normalized `publish.zip`,
embeds `gxmcp-manifest.json` with artifact hashes and protocol revisions, and
creates the GitHub release with the zip, checksum, and Nexus VSIX attached. The
manifest source commit must equal the tag commit. Do not run `gh release create` manually: the release workflow
requires `publish.zip` on the initial published event. The Worker needs the
local primary SDK from `config/gx-versions.json`, so the release artifact must
built on Windows with that supported GeneXus installation.

The `publish/config.json` that ships in `publish.zip` must be the neutral output
of `build.ps1`. `scripts/verify-release-manifest.py` rejects any other with
`release-manifest: invalid: config.json is not the neutral build output;
unexpected <keys>`: it accepts only `ConfigSchemaVersion`, `GatewayMode`,
`GeneXus` (`InstallationPath`, `WorkerExecutable`), `Server` and `Environment`
(`ResolutionPolicy`), so an `Engine` section, which `build.ps1
-PreserveEngineConfig` keeps for a local run, or a KB catalog under
`Environment` is refused. `release.ps1` runs the check on the staged `publish/`
and again on the archive, and the release workflow runs it on the unpacked
package, so it can first show up on a `-SkipBuild` retry, a reused preflight or
a resumed publication. Rebuild `publish/` with `build.ps1` without
`-PreserveEngineConfig` and run the release again; do not edit the file by hand
([#682](https://github.com/lennix1337/Genexus18MCP/issues/682)).

Before changing release metadata, `release.ps1` checks the live `origin/main`
head and requires local main to match it. It also permits retrying the same
version when the only local commit is `release: vX.Y.Z` directly on the current
remote head, and resumes an existing remote tag from that tag's pinned source
commit.

At release time, all open issues labeled `fixed-pending-release` are collected
automatically into `release-issues.txt`, included in the changelog, and closed
after publication. Marking an issue for that release is a separate operation
and never closes it:

```powershell
pwsh -NoProfile -File ./scripts/release-issues.ps1 `
  -Action MarkFixedPendingRelease -Issue 184,185
```

The helper reads the issue back and verifies that it remains open. To add
already-labeled explicit issues as well, pass them directly:

```powershell
./release.ps1 -Version <X.Y.Z> -CloseIssues 146,148
```

For a larger batch, keep one issue number per line (optional `#` and commas are
accepted) and pass the file alongside any inline numbers:

```powershell
./release.ps1 -Version <X.Y.Z> -CloseIssuesFile ./release-issues.txt
```

The release script deduplicates the combined list and adds a `Tracked issues`
section with links to the promoted changelog entry. Every explicit issue must
be open and carry `fixed-pending-release`; the release refuses to close an
unlabeled issue. In `-DryRun` it reports the planned links without editing the
changelog. Issues are still commented and closed only after GitHub confirms
that the release and its assets were created. Use `-SkipLabeledIssues` only
when a release must exclude the automatic label collection.

Before any issue mutation, the release performs a read-only pre-validation of
the complete batch. It writes `release-issues.json` with the issue titles and
URLs as an immutable snapshot for that version, and records discovered,
validated, commented, and closed issue numbers in the release status file. A
rerun for the same version reuses the existing snapshot. Use
`-ReleaseMilestone <number>` to restrict automatic collection to one milestone.

The script never infers issues from changelog text. By default it uses the explicit `fixed-pending-release` label; use `-SkipLabeledIssues` and omit `-CloseIssues`/`-CloseIssuesFile` to leave issue state untouched.

## Changelog and issue-reference ledger

Every issue selected for a release must already have its canonical issue URL in
the current `## Unreleased` section before the release proceeds:

```text
https://github.com/lennix1337/Genexus18MCP/issues/<number>
```

Read each issue with `gh issue view` before writing the note. A grouped fix is
allowed, but the bullet or its sub-bullets must link every issue it fixes.
GitHub PR numbers and `/pull/<number>` URLs do not replace issue references.
The canonical release entrypoint checks this set after collecting the explicit
and `fixed-pending-release` issue batch, including in `-DryRun`, and fails
before writing the release snapshot when a link is missing.

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
optional `reason`. It also records `sourceCommit`, `artifactFingerprint`,
`executionMode`, `processSmokeMode`, and `resumedFrom` when applicable; reused phases remain
observable with `reused=true`. Contract, inventory, and script checks run before
the expensive build/test phases. The warning-baseline documentation parity check
also runs in this cheap phase, so a stale generated table fails before any
build work. After those prerequisites, independent CLI,
PowerShell, Nexus, and non-process solution-test phases run in parallel. Tests
that launch Gateways, sockets, or child processes are tagged `ProcessSmoke` and
run in a dedicated serial lane after that wave. The live-artifact phase runs
after the warning baseline because it also invokes `dotnet test` against the
Gateway test binaries; keeping it sequenced prevents testhost and `bin/obj`
races. The warning-baseline rebuild runs after the solution phase so shared
MSBuild `bin/obj` outputs are never written concurrently. The summary is written
atomically at startup and after each phase starts or completes, retaining
`running` and terminal states if the host is interrupted.

Every phase that actually started a process also records `stdoutPath` and
`stderrPath`, pointing at `<summary>.<phase-slug>.stdout.log` and
`.stderr.log` next to the summary. The contract is that the phase carries the
*path* and the content stays in the file: the console tail is ambiguous in the
parallel wave (every header prints before any tail, with no phase label) and
gone once the terminal closes, so the log is what makes a red phase triageable
after the fact. Both files are written even when the command produced nothing —
an empty log is evidence, a missing file is indistinguishable from a phase that
never ran. Output is redacted with the canonical credential set in
`scripts/release-contract.ps1` — `Protect-GxMcpReleaseText` — and is **not**
truncated to the 1200-character cap that bounds summary messages. That set is the
single implementation for the repository: the `ghp_`/`github_pat_`/`npm_` literal
prefixes plus `token`/`password`/`passwd`/`pwd`/`secret`/`api_key`/
`api_secret`/`auth_token`/`authorization`/`connection string`/`user id`/
`userid`/`client secret`/`private key`/`credential` pairs, all replaced with
`[REDACTED]`. A value is read as a unit — a quoted value is one unit whatever it
contains, and an unquoted value runs to the first structural boundary (`,`, `;`,
`&`, `|`, a newline, or the next `name=value` pair). That boundary is a
deliberate over-masking limit: prose after a credential on the same line with no
following pair is masked too, because the boundary between "more secret" and
"more sentence" is not recoverable from the text. The publication verifier
(600-character cap), the integration preflight, and the diagnostics collector
delegate to the same function instead of keeping local pattern copies. A phase that never ran - skipped, dry-run, reused, or with no
executable on `PATH` - carries no log path at all: the keys are absent rather than
present and `null`. An `unavailable` phase records
its reason instead of leaving it `null`. The `Write-Error` at the end of a
failed preflight cites the log paths, because it previously pointed only at the
summary that held no output.

On a local Windows SDK machine, the preflight automatically selects
`C:/KBs/KBTeste` for GeneXus 18 or `C:/KBs/KBTeste17`
for GeneXus 17 when `GXMCP_TEST_KB` is unset. `GXMCP_TEST_FIXTURE` is optional
for normal live validation and is only needed for attested benchmark
populations. Set `GXMCP_REQUIRE_LIVE_BUILD_ALL=1` to make the live Build All
gate mandatory.

### Fast iteration and safe preflight resume

During development, run the narrowest affected test after each edit. Before a
release, run one canonical `release-preflight.ps1` and use its phase timings;
do not rerun the same complete .NET, CLI, script, lint, and live suites manually
after the canonical gate unless the source, SDK, fixture, or relevant
environment changed. The preflight owns the warning baseline on its normal
path; `release.ps1` invokes that check separately only when `-SkipTests` was
explicitly selected.

If the preflight fails after producing a source commit and `publish/` artifacts,
rerun the same version. `release.ps1` passes the previous summary back to the
preflight and may reuse the existing build only when the version, repository
root, source commit, selected SDK path, live inputs, and artifact fingerprint
all match. A missing or blank fingerprint is also a mismatch and invalidates
reuse; it is never treated as proof that artifacts are unchanged. An explicit
`-SkipBuild` without a matching summary now fails closed. Before a valid
`-SkipBuild` retry, the entrypoint reconciles the root and `publish/` VSIX
copies and requires the complete fingerprint set. This is a retry optimization,
not permission to skip an individual gate. A `-SkipBuild -SkipTests` resume additionally requires a passed certificate with every mandatory phase, a non-empty exact `ProcessSmoke` result set, and matching phase commands; a timeout, failed phase, legacy summary, or missing TRX result is not reusable evidence. The process lane runs the current Release Gateway/Worker binaries and records its selected test count.

The npm workflow uses `--no-audit --no-fund`, bounds each registry probe with no
hidden npm retries, and avoids sleeping after the final attempt. It still waits
synchronously for an exact `package@version` registry read-back. GitHub Step
Summary reports publish acceptance, registry visibility, probe count, and
propagation seconds; registry propagation is external latency and is not
treated as a local development-performance win.

Release progress is written atomically to a status file under `%TEMP%` by
default. Detached runs record that status path plus their stdout/stderr log
paths in the same JSON handoff. Read it with `scripts/release-status.ps1` or
`-Json`; terminal states are `succeeded` and `failed`, while exit code 2 means
the run is still in progress or the requested wait elapsed. The status includes
publication state/evidence, artifact fingerprint, preflight path, and a safe
next action.

The release script synchronizes `server.json`, `config.sample.json`,
`README.md`, `AGENTS.md`, and `docs/generated/supported-versions.md` from the
version catalog before its dirty-tree gate. It refuses a missing or ambiguous
generated block and the CI/release metadata check fails if those files drift.
The release script requires a substantive `## Unreleased` section when the
target version heading is absent, promotes that section, verifies the exact
version heading, and refuses to publish generic release notes. Promotion also
leaves an anchor in the fresh `## Unreleased`: an HTML comment naming the
contract plus empty `### Added` / `### Changed` / `### Fixed` / `### Internal`
headings. Without it, `## Unreleased` is present but has no subsection, and the
natural next edit — appending a bullet to the first visible `### Fixed` — files
new work under the release just published. The anchor is deliberately *not*
substantive: the guard that refuses an empty release counts a line only when it
is not blank, not inside an HTML comment, and not a bare heading, so an
untouched anchored section still fails the release.
If a local build fails after the metadata commit, leave that untagged commit in
place, fix the cause, and rerun the same version; do not manually rewrite the
manifest or tag a different source tree. If the full solution test suite and
warning baseline have already run and passed in the preflight for that exact
commit and version, and only post-build packaging or transient live-environment
gates required remediation, rerun with `-SkipBuild -SkipTests` (the release
script still enforces the Release warning baseline and executable version stamp
checks independently):

```powershell
./release.ps1 -Version <X.Y.Z> -SkipBuild -SkipTests
```

If a tag already has a GitHub Release without the complete asset set, the same
command resumes with an asset upload and preserves the existing release record.
A release with complete assets but a missing or failed workflow is also
resumable through the idempotent verification path. Resume never rebuilds,
rewrites the manifest or zip, retags, or pushes the already pinned source; it
only reconciles missing assets and verifies publication. The normal workflow is
triggered by `release.published`; an existing-release asset repair uses explicit
`workflow_dispatch` rather than a duplicate `edited` run.

After the GitHub Release is created or resumed, `release.ps1` verifies required
assets, the exact local-versus-remote SHA-256/size set, the non-draft release,
the peeled tag commit, the exact tag/event workflow run created after the
current asset update, and the exact npm registry version plus `gitHead` before
it marks the release successful. The archive is checked against the staged
manifest and publish tree before upload. Publication evidence is stored beside
the status file as `<status>.publication.json`; status/doctor output treats
missing, malformed, failed, or tag/commit-mismatched evidence as failure and
redacts nested diagnostics. Legacy versions may use the documented
`publish.zip`-only recovery path; the v3 asset set remains strict. Use
`scripts/release-doctor.ps1 -Remote` for a read-only recovery diagnosis. The
doctor only recommends `-SkipBuild -SkipTests` after a passed preflight whose
root, version, source commit, SDK, live inputs, phase certificate, process TRX
evidence, process-binary binding, and canonical artifact fingerprint all
match; its retry command is root-qualified and includes the validated live
path and flags.

Issues are closed only after the released fix is available and the publication
checks pass. Snapshot reuse rejects malformed records and accepts an already
closed issue only when the exact release URL comment is present. The script
re-reads and revalidates each issue immediately before commenting and again
before closing it, then verifies the closed state.

Repository text follows `.editorconfig` and `.gitattributes`: LF is the default,
while `CHANGELOG.md` intentionally retains CRLF because the release merge
contract preserves that file's historical line endings. Do not normalize the
changelog as a side effect of release tooling.

## Merge discipline

For multi-PR review, use the two-phase workflow in
[`docs/pr-review-playbook.md`](pr-review-playbook.md). It creates one detached
worktree per exact PR head, batches each lane's fixes before the final test
wave, bounds ripwire output, and requires a clean local tree plus a verified
remote head before a lane is considered complete.

Two PRs that both edit `CHANGELOG.md` can conflict regardless of merge order.
Before merging, probe with `git merge-tree --write-tree` and, when needed, a
read-only `git commit-tree` simulation. For a fork PR, resolve the Unreleased
sections in a temporary worktree, preserve CRLF, commit with the canonical
GitHub merge message, and push the explicit ref. After a manual main update,
rebase the local branch onto `origin/main` and resolve the changelog by
combining sections in project order.

When both sides changed `CHANGELOG.md`, the reviewed conflict sides can be
combined deterministically with:

```powershell
.\scripts\merge-unreleased-changelog.ps1 `
  -OursPath .\CHANGELOG.ours.md -TheirsPath .\CHANGELOG.theirs.md `
  -OutputPath .\CHANGELOG.md -Force
```

The helper refuses conflict markers or a missing `## Unreleased`, preserves the
non-Unreleased base from the coordinator's copy, deduplicates exact bullet
blocks, and writes atomically. Inspect its diff before committing.

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
base repository by accident. After a successful push it reads the explicit
remote ref back and fails if it does not equal the local reviewed `HEAD`; a
local commit alone is never completion evidence.

`pr-push.ps1` is also the mandatory local submission gate. Before it invokes
Git, it requires a clean worktree, fetches the PR base into an isolated local
ref, requires the branch to contain that fresh base, and runs
`integration-preflight.ps1`. That preflight validates the operation-contract
inventory and the complete Python script-test suite in addition to the
PowerShell, CLI, lint, and Gateway gates. With a local GeneXus SDK it runs the
full solution tests; without one it records the Worker gate as unavailable and
leaves that validation to the protected CI SDK lane. A failure stops before
any remote write. For a branch without an open PR yet, run the same integration
preflight manually against the freshly fetched `origin/main`.

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
same explicitly selected fixture gate once per selected major:

```powershell
pwsh -NoProfile -File .\scripts\test-live-matrix.ps1 `
  -KbPath $env:GXMCP_TEST_KB `
  -RequireBuildAll -RunBenchmark -Iterations 100 `
  -SummaryPath "$env:TEMP\gxmcp-live-matrix.json"
```

Add `-FixtureManifest $env:GXMCP_TEST_FIXTURE` only when comparing an attested
The matrix requires an explicit `-KbPath`; when invoked through
`release-preflight.ps1`, `GXMCP_TEST_KB` supplies that path. The single-major
release preflight is the path that autodetects `C:/KBs/KBTeste*`.

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
real Release rebuild. `-UpdateBaseline` atomically updates both the JSON source
of truth and the generated Markdown block:

```powershell
.\scripts\check-build-warning-baseline.ps1 -ValidateOnly
.\scripts\check-build-warning-baseline.ps1 -UpdateBaseline -GxPath `
  'C:\Program Files (x86)\GeneXus\GeneXus18'
```

The release script runs the non-update check automatically and fails on
`MSB3277`, any new `(code, file, line)` warning location, or stale generated
Markdown. Line-only moves are reported as `moved` and do not hide genuinely new
diagnostics.

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
