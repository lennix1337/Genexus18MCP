# Coding Standards

Review-time rules for this repository. The implementer reads
[`AGENTS.md`](AGENTS.md) for navigation and workflow; this file holds the rules a
reviewer **enforces** on a diff. Each rule names the failure it prevents, so a
reviewer can recognise a violation without reconstructing the history that
motivated it.

## Text edits

A tracked text file is edited with the file-editing tool, never by round-tripping
it through a shell. The shell is for reading and for running commands.
Never treat a bounded/truncated tool preview as complete file content when
integrating a change. Verify byte completeness against the source before compiling;
continuation notices belong to the tool response, not the destination file.

Three distinct corruptions came from doing this the other way, all on files that
carry history or contracts:

- Addressing a long file by **fixed line index** truncated a 4,418-line
  `CHANGELOG.md` to 115 lines, deleting every released version.
- Piping a UTF-8 file through the **PowerShell pipeline** re-encoded it through
  the console codepage: em-dashes became `ΓÇö` and accented characters were
  destroyed, while the file still parsed as valid text.
- Mutating a list by **index arithmetic with shifting bounds** deleted closing
  braces, so the file stopped compiling.

**Review signals.** A diff that rewrites a whole file's line endings, or a
`CHANGELOG.md` that lost `## v` sections, is this bug wearing a different hat.
Check `git diff --stat`: when the changed-line count dwarfs the semantic change,
the edit mechanism is suspect, not the intent.

`git checkout --ours` / `--theirs` is the correct way to resolve a conflict
wholesale. Resolving a single hunk means hand-editing the markers out.

## Build state

A build that fails leaves stale outputs behind, and the next test run then
reports results from the **previous** binary. A test failure that contradicts the
code you just wrote is a stale output until proven otherwise.

After any compile failure, force the rebuild before trusting a test result:

```powershell
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -c Release -t:Rebuild
```

**Review signal.** If a change is described as fixing a failure and the test that
caught it still fails, suspect the binary before the code.

## Validation lanes

A change is validated when **every** lane is green, not the lanes you remember.
The PowerShell and Python suites catch contract drift that the .NET, CLI and Nexus
suites are blind to: a live-test class missing its `ProcessSmoke` trait passes
every .NET lane and is rejected by the release preflight guard.

```powershell
dotnet test Genexus18MCP.sln -c Release --filter "Category!=ProcessSmoke"
dotnet test Genexus18MCP.sln -c Release --no-build --filter "Category=ProcessSmoke"
pwsh -NoProfile -File scripts\tests\run-release-script-tests.ps1
python -m unittest discover -s scripts/tests
python scripts/validate-tool-contracts.py
python scripts/generate-operation-contract-inventory.py --check
python scripts/verify-release-metadata.py --root . --version <package.json version>
cargo fmt --manifest-path src/gx-engine/Cargo.toml --check
cargo clippy --manifest-path src/gx-engine/Cargo.toml --all-targets -- -D warnings
cargo test --manifest-path src/gx-engine/Cargo.toml
npm test
npm run lint
Push-Location src/nexus-ide
npm ci
npm run compile
npm run lint
npm test
Pop-Location
pwsh -NoProfile -File scripts/check-build-warning-baseline.ps1 -ValidateOnly
pwsh -NoProfile -File scripts/mcp_llm_contract_smoke.ps1
pwsh -NoProfile -File scripts/coverage/collect.ps1
pwsh -NoProfile -File scripts/coverage/assert-threshold.ps1 -CoverageRoot artifacts/coverage -MinLineRatePercent 60
.\build.ps1                                                     # last; see ordering below
```

**Superset of CI.** This block is the reviewer's full set.
`.github/workflows/ci.yml` enforces a *subset* of it: an entry is here because CI
runs it, because a release depends on it, or because CI cannot run it locally —
not because every line is a CI step. A reviewer who expects CI and this block to
be line-for-line identical will flag a legitimate superset as drift. CI
additionally runs the protected SDK/live lane (`scripts/ci/sdk-validation.ps1`),
which needs the self-hosted `gx-sdk-18` runner and is therefore not listed.

The Nexus-IDE lane is **not** optional. On a 3.x release the `.vsix` it builds is
a required release asset, and `.github/workflows/release.yml` byte-compares it
against the copy inside `publish.zip`.

**Ordering.** `build.ps1` runs **last**. A solution-level `dotnet test -c Release`
rebuilds the Worker outside the `x86` platform the solution maps it to, so
running it after `build.ps1` breaks the publish↔source byte identity and
`Get-GxMcpReleaseProcessSmokeFingerprint` returns empty — which fails
`test-release-preflight.ps1` for a reason unrelated to the change under test.

**Clean-checkout prerequisites.** A lane that invokes tests with `--no-build`
must compile every selected test project before the final package build; do not
rely on a developer's existing `bin/obj`. Regression-test missing outputs and
compilation failure without skipping the live check.

**Artifact identity.** Compare equivalent target/runtime/publish representations.
A ReadyToRun published DLL is not byte-identical to its IL build. Verify the
matching RID module identity, native-header state and manifest hashes instead
of changing production code or replaying a build to satisfy an invalid comparison.

**Review signal.** A commit whose message cites counts for some lanes but not
others is unverified. Ask for the missing lane's output, not for a claim about it.

## Backend and default changes

A backend is substitutable only when the same public operation has compatible
observable behavior. Before changing routing or a default, record:

- The affected operation/argument admission table and SDK fallback boundaries.
- Shared behavioral tests against `sdk`, `auto`, and `gxengine`: read → edit
  with the returned version token, exact/fuzzy search and metadata, unknown-part
  errors, stale-write rejection, and unsupported-operation fallback.
- Live results from the packaged binary on an explicit disposable KB; verify
  which backend served each operation, not merely the configured backend.
- Required CI results for the exact source commit, including Rust and relevant
  SDK/live evidence. A skipped or unavailable lane is not a passing gate.

Argument normalization and projection must match the reference backend for that
specific operation; do not trim inputs or omit response fields merely because a
different operation supports that transformation.

Unit counts, capability labels and byte-identical generated files do not replace
this contract evidence. Keep snapshots unchanged unless the public contract
intentionally changes; test the negative/error paths as well as success.

A native reader takes its scope (KB version, model, environment) from the
session the SDK serves, passed by the host and checked against storage, never
from a hardcoded default. When storage admits more than one scope and none is
named, refuse before reading, with a code the host can fall back on. Parse
scope-bearing metadata structurally; malformed or duplicate values must not
become an absent value/default scope. Validate the full build's supporting facts,
not just object parts, before admitting a versioned scope.

## Guards must be able to fail

A regression test that cannot fail is not a guard. Every new guard gets a
**mutation check**: revert the fix it protects and confirm the test goes red.
This is what distinguishes coverage from the appearance of coverage.

The same rule applies to source-level guards. A regex asserted against a string
built in a double-quoted PowerShell string will silently interpolate `\$var`
into nothing and match for the wrong reason — the guard passes while testing
nothing. Use single-quoted patterns so the regex reaches the matcher intact.

**Review signal.** A new test with no recorded mutation check is unproven. Ask
which mutation turns it red.

## Honest failure modes

A generator must refuse unsupported executable semantics, not replace them with
comments, empty statements or a bare nested body and report successful generation.
Propagate nested emitter failures to the artifact writer; a compilable artifact
is not evidence that database or control-flow behavior was preserved. Regression
tests must exercise parsed source through the checked generation pipeline and
verify that refused output is not counted as generated or compiled.

Source projections preserve lexical boundaries using the language's escape rules;
regress escaped quotes/backslashes and a real expression after the literal.
Keep presentation projections separate from stored source and codegen inputs.
Metadata scalar validation must cover the whole XML node, not just its first text
child; regress comment/processing-instruction boundaries and reject unsupported structure.

Read-only validators must preserve their inputs even when an output path aliases
an input through a symlink, junction or hardlink. Validate output boundaries and
publish reports by atomic replacement rather than truncating a potentially
aliased file; regression-test that input bytes remain unchanged.

Non-interactive subprocesses must validate required credentials before launch;
never let an authentication prompt consume stdin intended for protocol/batch input.
Pass secrets via the child environment, not argv/logs. Automatic backend replay
requires an explicit machine-validated pre-effect refusal, not inferred error text
or a generic nonzero exit; regression-test malformed refusals and started failures.

A gate that cannot run is `unavailable`, never `pass`. This covers a KB that will
not open under the selected SDK, a native assembly absent from every install, and
a live test skipped for want of a fixture.

A label may describe a cause; it may never change an outcome. The preflight
aggregate accepts `unavailable` as an approved terminal status, so downgrading a
failed phase would certify a release that ran nothing.

An SDK failure response carries the root cause. Catches around reflective or
wrapped SDK calls report and log through `ExceptionRoot` (message, type, trace),
never a wrapper's `ex.Message`; a "see the worker log" hint needs a matching log
line. Known SDK preconditions are refused with a typed code before the call.
A failed SDK task also emits its diagnostic through the operation's output sink,
including a false or invalid return without an exception; an internal debug log
alone does not explain the failure to the MCP caller. Test these silent-return
paths and verify that they execute at most once.

Absence of evidence is reported as absence. When a contract is unverifiable on
this host, say so and name what would unblock it, rather than merging on the
strength of the build being green.

A background-command acknowledgement is not operation completion. An observing
job must retain the operation identity and verify a coherent terminal state;
readiness labels alone cannot override active work. Observer cancellation or a
timeout does not prove that the underlying operation stopped. Regression tests
must hold the operation active after acceptance and reject premature success.

## Scope

Smallest scoped change; preserve unrelated working-tree changes. A file the task
never names is not an invitation to reformat it.

For review-enforceable contract rules (tool schema, dispatch path, cache
invalidation, generated artifacts), see
[`AGENTS.md` § Source of truth and tool changes](AGENTS.md).
