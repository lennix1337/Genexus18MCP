# Agent model evaluation scoreboard

Rows are written by `scripts/run-agent-model-eval.py --markdown docs/agent-eval-scoreboard.md`
(see [agent-evals.md](agent-evals.md)). One row per (model, profile); a new run replaces its row.
Only first-call tool selection is measured: one request per scenario, no worker, no KB, no
multi-turn recovery. Lenient accuracy also accepts `genexus_whoami` as a first call; strict does not.

_No model run has been recorded yet. This table is intentionally empty until a real run is made._

| model | profile | scenarios | first-call accuracy (lenient) | first-call accuracy (strict) | required-args validity | avg input tokens/request | date (UTC) | harness version |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
