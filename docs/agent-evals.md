# Agent evaluation corpus

`tests/agent-evals/corpus.json` is the replay manifest for the v3 quality
gates. It mirrors the 15 scenarios in `plans/v3-evaluation-corpus.json` and
adds a revisioned synthetic fixture identifier, deterministic replay mode, and
an explicit `modelEvaluation=not_executed` boundary.

The manifest is a contract for fixtures and oracles, not a claim that a live KB
or a model run has completed. Replay must use an isolated synthetic KB and
datastore, record cold and warm measurements separately, and preserve the
listed success, safety, and no-side-effect oracles. Model-backed evaluations
remain opt-in and require a separately authorized provider and cost budget.

Validate both the design and replay manifests with:

```powershell
python scripts/validate-v3-evaluation.py
python scripts/validate-v3-evaluation.py tests/agent-evals/corpus.json
```

When an authorized runner produces a completed agent replay, validate the
provider-neutral result before publishing any performance or quality claim:

```powershell
python scripts/validate-agent-replay.py path\to\replay-result.json
```

The replay result must use schema `genexus-v3-agent-replay/1`, match the
manifest fixture revision, and attempt every E01–E15 scenario. Each scenario
must have at least one tool call, pass its gate, report zero invalid calls,
unexpected effects, and blind unknown-outcome retries, and keep source and
secret fields false. The validator never invokes a model or opens a KB; a
missing live fixture therefore remains an unexecuted gate.

No KB source, credentials, or model prompt content belongs in the manifest or
telemetry. A missing live fixture keeps the replay gate unexecuted; it must not
be replaced by a production KB or an inferred success.

## First-call tool-selection harness (opt-in)

`scripts/run-agent-model-eval.py` measures one thing: given the tool list exactly
as served (`src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`)
and the `initialize` instructions as the system prompt, does a model pick an
acceptable **first** tool for a scenario task, and does that call carry the
schema's required keys. Scenarios come from
`tests/agent-evals/first-tool-expectations.json` (task text plus
`expectedFirstTools`, derived from the first step of the matching corpus flow;
`genexus_whoami` is always accepted because the server instructions ask for it).
Only E02, E03, E05, E06, E07, E08, E10 and E15 have a single-tool first step.

It does not run a worker or KB, does not execute the returned call, and does not
cover multi-turn recovery, argument correctness beyond required keys, or the
safety oracles. `modelEvaluation` in `corpus.json` stays `not_executed`.

```powershell
python scripts/run-agent-model-eval.py --dry-run                 # request sizes, sends nothing
$env:ANTHROPIC_API_KEY = "<key>"                                  # opt-in; never read by CI
python scripts/run-agent-model-eval.py --out artifacts/first-tool-eval.json
python scripts/run-agent-model-eval.py --scenario E03 --model <model-id> --limit 1
python scripts/run-agent-model-eval.py --profile core --strict --markdown docs/agent-eval-scoreboard.md
```

The report holds per-scenario results and totals (first-call accuracy,
required-args validity, input tokens). HTTP and network errors are recorded per
scenario without aborting the run.

### Profiles

The Gateway serves a different, compacted `tools/list` per tool profile
(`ToolProfileFilter`). `--profile all|core|standard|authoring|devops|ui|db`
offers the model exactly that list, read from
`src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.<profile>.response.json`
(`all` is `tools-list.response.json`). The profile fixtures are written and
verified by `ToolProfileListFixtureTests` (regenerate with
`GXMCP_UPDATE_GOLDEN=1`, like the other discovery fixtures), so no Gateway is
needed to run the harness. They hold only `name`, `description` and `inputSchema`,
which is all the harness sends. A scenario none of whose expected tools is
served under the chosen profile (for example `genexus_refactor` under `core`) is
reported as `skipped`, not scored as a model failure, and sends no request.

### Strict and lenient scoring

By default `genexus_whoami` is accepted as a first call for every scenario
(`alwaysAcceptable`), which is lenient. Every run computes both numbers; they
are reported separately in `totals` (`lenientFirstCallAccuracy`,
`strictFirstCallAccuracy`) and in the scoreboard. `--strict` makes the strict
number the headline (`firstCallAccuracy`, PASS/FAIL column).

Only E02, E03, E05, E06, E07, E08, E10 and E15 have a first-tool expectation.
The other seven are listed under `omittedScenarios` in
`tests/agent-evals/first-tool-expectations.json`, each with its reason: E01 (the
first step is the protocol method `tools/list`), E04 (several equally valid read
tools), E09 (no dedicated translation tool, so the first tool is ambiguous),
E11-E14 (transport, recovery, load and installer scenarios, not tool choices). A
test keeps `scenarios` plus `omittedScenarios` equal to the corpus.

### Scoreboard

`--markdown <path>` adds or replaces the row for the run's (model, profile) in a
compact table: model, profile, scenarios answered/planned, lenient and strict
first-call accuracy, required-args validity, average input tokens per request,
date (UTC) and harness version. The tracked file is
[agent-eval-scoreboard.md](agent-eval-scoreboard.md). It is empty, and says so,
until someone records a real run; rows are never written by dry runs or by runs
in which no scenario was answered. A partial run (`--limit`, `--scenario`)
shows its smaller `answered/planned` count and replaces the previous row for
that model and profile.

### Manual workflow and cost

`.github/workflows/agent-evals.yml` is `workflow_dispatch` only (Actions tab,
"Agent evals (manual)", inputs `model` and `profile`). It reads the key from the
repository secret `ANTHROPIC_API_KEY`, runs the harness, prints the table in the
job summary and uploads the JSON report and markdown as the `agent-eval-report`
artifact. It commits nothing and runs on no other trigger; copy a row into the
scoreboard by hand.

Cost is one request per applicable scenario, each carrying the whole tool list
and the server instructions. Measured with `--dry-run` on the current corpus
(request bodies, in bytes; roughly one character each for this mostly-ASCII
payload):

| profile | tools | requests | bytes per request | bytes total |
| --- | --- | --- | --- | --- |
| all | 60 | 8 | about 104,900 | 838,962 |
| authoring | 35 | 7 | about 74,300 | 520,213 |
| ui | 23 | 6 | about 53,100 | 318,607 |
| devops | 37 | 5 | about 53,400 | 267,043 |
| standard | 20 | 5 | about 46,500 | 232,648 |
| db | 19 | 6 | about 36,500 | 219,104 |
| core | 15 | 5 | about 28,500 | 142,278 |

Re-run `--dry-run --profile <p>` for current numbers; they change with the tool
catalog. Output is capped by `--max-tokens` (default 1024) per request. Set a
spend budget before running.

Status: the harness has been unit-tested offline against a fake transport only.
It has not been executed against the live API, so the request format and any
resulting numbers are unverified, and the scoreboard has no rows.
