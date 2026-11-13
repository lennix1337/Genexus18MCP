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
```

The report holds per-scenario results and totals (first-call accuracy,
required-args validity, input tokens). HTTP and network errors are recorded per
scenario without aborting the run. Cost: one request per scenario, each about
105 KB of request body (the full catalog), so a full run is 8 large requests;
set a budget before running.

Status: the harness has been unit-tested offline against a fake transport only.
It has not been executed against the live API; the request format and the
resulting numbers are unverified.
