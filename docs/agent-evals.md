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
