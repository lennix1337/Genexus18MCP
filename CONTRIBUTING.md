# Contributing

Thanks for looking at the code. This is a solo project that I maintain in spare time, so a few honest notes upfront so you don't waste yours.

## What this repo is

A two-process MCP server:

- **Gateway** (`src/GxMcp.Gateway`, .NET 10) — speaks MCP over stdio, hot-reloads `config.json`, brokers calls to the worker.
- **Worker** (`src/GxMcp.Worker`, .NET Framework 4.8, x86, STA) — hosts the selected native GeneXus SDK (`Artech.*` DLLs). Has to be .NET 4.8 + STA because the SDK won't run otherwise.
- **CLI** (`cli/`, Node 22+) — the `npx genexus-mcp` entry point: `init`, `doctor`, `axi`, update check.

The Worker references DLLs from the SDK selected by `GX_PATH` (the default is
the primary entry in [`config/gx-versions.json`](config/gx-versions.json)).
**You can't build it without a supported GeneXus SDK installed locally** — see
[`docs/RELEASE.md`](docs/RELEASE.md) for why CI hosted runners can't build the
.NET side.

## Before you open a PR

- **Bug fix or doc tweak** — go ahead, open the PR.
- **New tool, refactor, behavior change, anything user-facing** — open an issue first so we can agree on the shape. I'd rather discuss for 10 minutes than ask you to rewrite a 400-line PR.
- **CLAUDE.md / GEMINI.md / skill changes** — these are agent-facing instructions, not generic docs. If you change them, say which agent/scenario you tested against.
- **Run the complete CI-equivalent checks before every PR and again before each push that changes code or tool schemas.** Do not rely on a narrower project test: the coverage step also runs the Gateway contract-golden tests and fails on stale discovery fixtures.

## Dev loop

```pwsh
# Restore + build everything
.\build.ps1

# Run CLI tests
npm test

# Smoke the MCP end-to-end (requires a real KB)
npx . doctor --mcp-smoke
```

If you only touched `cli/`, `npm test` is enough. If you touched the Gateway or Worker, you need `.\build.ps1` and a real KB to verify — there is no mock.

### Local prerequisites

Prepare the root package with `npm ci`; it installs the locked development
dependencies used by `npm run lint` (`eslint` and `@eslint/js`). The lint wrapper
reports missing local dependencies and never installs them globally. In a
non-interactive terminal or CI, run `npm ci` explicitly before retrying.

The repository routines use these external tools:

| Tool | Used by | Requirement |
|---|---|---|
| Node.js 22+ and npm | CLI, `npm test`, `npm run lint` | Required for the root package |
| .NET SDK 10 | Gateway/Worker builds and tests | Required for C# validation |
| GeneXus SDK and `GX_PATH` | Worker build and live KB smoke | Required locally for Worker/live validation |
| PowerShell 7+ | build, install, coverage and preflight scripts | Required for those scripts |
| Git | source control, PR tooling and release checks | Required for development |
| ripwire | architectural orientation and optional PR analysis | Optional runtime tool; required only when a preflight is run with `-RequireRipwire` |

`ripwire` is not an npm or runtime dependency. When it is unavailable,
`scripts/pr-preflight.ps1` reports `skipped` by default and fails clearly with
`-RequireRipwire`. On Windows, install it through the documented CMake/MSVC
route rather than running a Unix `install.sh` in PowerShell:

```powershell
git clone https://github.com/redhat-et/ripwire.git C:\Dev\Tools\ripwire
cmake -S C:\Dev\Tools\ripwire -B C:\Dev\Tools\ripwire\build
cmake --build C:\Dev\Tools\ripwire\build --config Release
```

Add the directory containing `ripwire.exe` to the user `PATH`. The tool is used
for analysis only; it is never installed automatically by this repository.

### What CI runs beyond the dev loop

CI (`.github/workflows/ci.yml`) also runs steps not in the dev loop above, so a green local run can still hit CI-only failures. To reproduce them locally:

```pwsh
# Coverage collection + component thresholds, exactly as CI runs it
$coverageRoot = Join-Path $env:TEMP 'gx-coverage-pr'
.\scripts\coverage\collect.ps1 -OutputRoot $coverageRoot
.\scripts\coverage\assert-threshold.ps1 -CoverageRoot $coverageRoot -MinLineRatePercent 60 -MinWorkerLineRatePercent 45

# LLM tool-contract smoke
.\scripts\mcp_llm_contract_smoke.ps1

# Nexus IDE lint (VS Code extension)
cd src\nexus-ide; npm run lint
```

On a pull request, CI skips the lanes your diff cannot affect: `scripts/ci/classify-lanes.ps1` maps each changed path through `scripts/ci/lane-paths.json`, and an unmapped path or a change under `.github/` or `scripts/ci/` runs every lane. Documentation is tested too, so a change to `README.md`, `CHANGELOG.md` or another document the Gateway or CLI tests read still runs those lanes. When a test starts reading a new repository file, add the file to the matching rule; `scripts/tests/test-ci-workflow-policy.ps1` fails until the map covers it.

`collect.ps1` resolves the GeneXus SDK from `-GxPath`, then `GX_PATH`, then the default installation directory. For a non-default installation, use either form:

```pwsh
$env:GX_PATH = 'C:\GeneXus\GeneXus18U16'
.\scripts\coverage\collect.ps1 -OutputRoot $coverageRoot

# Or keep the setting scoped to this invocation.
.\scripts\coverage\collect.ps1 -OutputRoot $coverageRoot -GxPath 'C:\GeneXus\GeneXus18U16'
```

An explicitly configured path that does not contain `Artech.Architecture.Common.dll` fails immediately instead of silently skipping Worker coverage. When no path is configured and the default installation is absent, the script drops a `worker.skipped.txt` marker and `assert-threshold.ps1` enforces only the Gateway floor — the same behavior as a GitHub-hosted runner.

With an SDK configured the Gateway suite and the Worker suite run one after the other, and the Worker suite is the longer one. `collect.ps1 -Parallel` runs both at once, each as its own test process with its own build output, environment and report, so the run waits for the slower suite instead of for both; the verdicts and the files `assert-threshold.ps1` reads are the same. Each suite's log is printed when that suite ends, and kept as `gateway.console.log` and `worker.console.log` in the output directory, instead of streaming. The Gateway per-test durations are then measured while the Worker suite is running. Without an SDK the Worker suite is skipped and the switch changes nothing.

Do not add coverage exclusions merely to satisfy a floor. Add tests that execute the affected contract, run the complete command above, and confirm the current component baselines before pushing: at least 60% for Gateway and 45% for Worker when an SDK is configured. Raise each floor as its exercised surface grows; never lower it or skip tests to make a change pass.

### Before every PR push

The source of truth is the test suite at the commit that will be sent. For a
new PR, first update the base and run the local integration gate:

```pwsh
git fetch --no-tags origin main
pwsh -NoProfile -File .\scripts\integration-preflight.ps1 -BaseRef origin/main
```

For an existing PR, use the push helper. It checks for uncommitted changes,
fetches the PR's current base branch, refuses a branch that is behind it, runs
the operation-inventory check, all Python tests, PowerShell tests, CLI tests,
CLI lint, and the Gateway tests. When a GeneXus SDK is installed it also runs
the full solution tests; otherwise the Worker gate is recorded as unavailable
and remains covered by CI's protected SDK lane. Only then does it push the exact
`HEAD`:

```pwsh
pwsh -NoProfile -File .\scripts\pr-push.ps1 -PullRequest <number> -ForceWithLease
```

If the preflight fails, no push is attempted. Fix the reported gate, commit
the fix, and run the helper again. This prevents a stale test baseline or
uncommitted local state from reaching the PR.

If a tool schema or description changed, update and verify the discovery golden before running coverage:

```pwsh
$env:GXMCP_UPDATE_GOLDEN = '1'
dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~McpDiscoveryContractTests"
Remove-Item Env:GXMCP_UPDATE_GOLDEN

# Review this diff; commit only the intended, anonymized contract changes.
git diff -- src\GxMcp.Gateway.Tests\Fixtures\Contract\Discovery\tools-list.response.json

# Prove the checked-in fixture matches without update mode.
dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~McpDiscoveryContractTests"
```

Never leave `GXMCP_UPDATE_GOLDEN=1` set while validating: update mode overwrites the expected fixture and can hide an unintended contract change.

## Code style

- **C# (Gateway + Worker)** — match the surrounding file. No new abstractions unless they're paying for themselves. Don't add error handling for cases that can't happen.
- **JS (CLI)** — Node built-ins only where possible, no TypeScript in `cli/`. Keep dependencies minimal — `package.json` has 0 runtime deps and I'd like to keep it that way.
- **No comments explaining *what*** — naming should do that. Comments are for *why* something non-obvious is the way it is (workarounds, SDK quirks, hidden invariants).
- **PowerShell scripts** use `.ps1`, target `pwsh` 7+. Use `&&` / `||` chaining freely.
- **Don't reformat unrelated code** in a PR. If a file needs cleanup, do it in a separate `chore(...)` commit.

## Commits

Conventional Commits, scope mandatory when it's obvious which subsystem changed. Look at `git log --oneline` for the cadence — short, factual, no marketing.

```
feat(gateway): async build polls worker until terminal
fix(gateway): meta-tools bypass KbResolver
chore(release): add -NoBump flag to release.ps1
docs(readme): add SafeSkill badge, normalize badge sizing
```

Scopes in use: `gateway`, `worker`, `cli`, `readme`, `release`, `plan`, `spec`. Add a new one if your change genuinely needs it.

## Testing GeneXus changes

There's no fixture KB in the repo — KBs are tens of GB and tied to a SQL Server instance. To test SDK-touching changes you need a local supported GeneXus install and a KB built at least once. The `doctor --mcp-smoke` command exercises the common tool paths against whichever KB `config.json` points at.

When you submit a PR that touches the Worker, **say what KB you tested against** (object types touched, KB size, GeneXus build). "Works on my KB" is more useful than it sounds — KBs vary wildly.

## What I will and won't accept

**Will:**
- Bug fixes with a clear repro.
- New MCP tools that wrap a specific SDK capability that's currently awkward to drive from an agent.
- Performance work backed by a measurement (before/after wall-clock, token counts, etc.).
- Docs fixes, typo fixes, troubleshooting additions.

**Probably won't:**
- "Generic AI improvements" / vibes-driven refactors.
- Adding runtime dependencies to the CLI.
- Rewrites of working code to a different style.
- PRs that bundle a small fix with unrelated formatting churn.

## Releases

You can't cut a release — npm Trusted Publishing only accepts publishes from this repo's `release.yml`. See [`docs/RELEASE.md`](docs/RELEASE.md). PRs land on `main`, I cut releases from there.

## Questions

Open an issue, or DM on the npm package's repo discussions. I read everything; replies may take a few days.
