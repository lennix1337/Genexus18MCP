#!/usr/bin/env python3
"""Validate a completed deterministic agent replay report.

The gate is independent of any model provider. It checks that a report belongs
to the same fixture revision as the corpus and that every scenario was attempted
with zero invalid calls, unintended effects, blind unknown-outcome retries, or
source/secret telemetry leakage. It does not execute a model or a KB.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any


EXPECTED_SCHEMA = "genexus-v3-agent-replay/1"
EXPECTED_IDS = {f"E{number:02d}" for number in range(1, 16)}


def validate(report: Any, manifest: Any | None = None) -> list[str]:
    errors: list[str] = []
    if not isinstance(report, dict):
        return ["root must be an object"]
    if report.get("schemaVersion") != EXPECTED_SCHEMA:
        errors.append(f"schemaVersion must be {EXPECTED_SCHEMA!r}")
    if report.get("status") != "REPLAY_RESULT":
        errors.append("status must be REPLAY_RESULT")

    expected_revision = None
    if isinstance(manifest, dict):
        contract = manifest.get("executionContract")
        if isinstance(contract, dict):
            expected_revision = contract.get("fixtureRevision")
    if not isinstance(report.get("fixtureRevision"), str) or not report["fixtureRevision"].strip():
        errors.append("fixtureRevision must be a non-empty string")
    elif expected_revision and report["fixtureRevision"] != expected_revision:
        errors.append("fixtureRevision does not match the corpus executionContract")

    scenarios = report.get("scenarios")
    if not isinstance(scenarios, list) or not scenarios:
        return errors + ["scenarios must be a non-empty array"]

    seen: set[str] = set()
    for index, scenario in enumerate(scenarios):
        prefix = f"scenarios[{index}]"
        if not isinstance(scenario, dict):
            errors.append(f"{prefix} must be an object")
            continue
        scenario_id = scenario.get("id")
        if not isinstance(scenario_id, str) or scenario_id not in EXPECTED_IDS:
            errors.append(f"{prefix}.id must be one of E01..E15")
        elif scenario_id in seen:
            errors.append(f"duplicate scenario id: {scenario_id}")
        else:
            seen.add(scenario_id)

        if scenario.get("attempted") is not True:
            errors.append(f"{prefix}.attempted must be true")
        if scenario.get("skipped") is True:
            errors.append(f"{prefix}.skipped must be false")
        if scenario.get("gatePassed") is not True:
            errors.append(f"{prefix}.gatePassed must be true")
        for field in ("toolCalls", "invalidCalls", "unexpectedEffects", "unknownRetries"):
            value = scenario.get(field)
            if not isinstance(value, int) or isinstance(value, bool) or value < 0:
                errors.append(f"{prefix}.{field} must be a non-negative integer")
        if isinstance(scenario.get("toolCalls"), int) and scenario["toolCalls"] < 1:
            errors.append(f"{prefix}.toolCalls must be at least one")
        for field in ("invalidCalls", "unexpectedEffects", "unknownRetries"):
            if isinstance(scenario.get(field), int) and scenario[field] != 0:
                errors.append(f"{prefix}.{field} must be zero")
        for field in ("containsSecrets", "containsSourcePayload"):
            if scenario.get(field) is not False:
                errors.append(f"{prefix}.{field} must be false")

    if seen != EXPECTED_IDS:
        errors.append("scenario ids must cover E01..E15 exactly")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path, help="completed replay report")
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path("tests/agent-evals/corpus.json"),
        help="corpus whose fixture revision must match",
    )
    args = parser.parse_args(argv)
    try:
        report = json.loads(args.report.read_text(encoding="utf-8"))
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"agent-replay: unable to read input: {exc}", file=sys.stderr)
        return 2

    errors = validate(report, manifest)
    if errors:
        print("agent-replay: invalid", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 2
    print(f"agent-replay: valid scenarios={len(report['scenarios'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
