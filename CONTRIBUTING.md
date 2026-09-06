# Contributing

Thanks for looking at the code. This is a solo project that I maintain in spare time, so a few honest notes upfront so you don't waste yours.

## What this repo is

A two-process MCP server:

- **Gateway** (`src/GxMcp.Gateway`, .NET 10) — speaks MCP over stdio, hot-reloads `config.json`, brokers calls to the worker.
- **Worker** (`src/GxMcp.Worker`, .NET Framework 4.8, x86, STA) — hosts the native GeneXus 18 SDK (`Artech.*` DLLs). Has to be .NET 4.8 + STA because the SDK won't run otherwise.
- **CLI** (`cli/`, Node 22+) — the `npx genexus-mcp` entry point: `init`, `doctor`, `axi`, update check.

The Worker references DLLs from `C:\Program Files (x86)\GeneXus\GeneXus18`. **You can't build it without GeneXus 18 installed locally** — see [`docs/RELEASE.md`](docs/RELEASE.md) for why CI hosted runners can't build the .NET side.

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

`collect.ps1` resolves the GeneXus SDK from `-GxPath`, then `GX_PATH`, then the default installation directory. For a non-default installation, use either form:

```pwsh
$env:GX_PATH = 'C:\GeneXus\GeneXus18U16'
.\scripts\coverage\collect.ps1 -OutputRoot $coverageRoot

# Or keep the setting scoped to this invocation.
.\scripts\coverage\collect.ps1 -OutputRoot $coverageRoot -GxPath 'C:\GeneXus\GeneXus18U16'
```

An explicitly configured path that does not contain `Artech.Architecture.Common.dll` fails immediately instead of silently skipping Worker coverage. When no path is configured and the default installation is absent, the script drops a `worker.skipped.txt` marker and `assert-threshold.ps1` enforces only the Gateway floor — the same behavior as a GitHub-hosted runner.

Do not add coverage exclusions merely to satisfy a floor. Add tests that execute the affected contract, run the complete command above, and confirm the current component baselines before pushing: at least 60% for Gateway and 45% for Worker when an SDK is configured. Raise each floor as its exercised surface grows; never lower it or skip tests to make a change pass.

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

There's no fixture KB in the repo — KBs are tens of GB and tied to a SQL Server instance. To test SDK-touching changes you need a local GeneXus 18 install and a KB built at least once. The `doctor --mcp-smoke` command exercises the common tool paths against whichever KB `config.json` points at.

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
