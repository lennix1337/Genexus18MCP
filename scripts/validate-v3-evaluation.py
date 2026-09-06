#!/usr/bin/env python3
"""Validate the deterministic v3 evaluation corpus contract.

This gate validates the corpus manifest itself. It does not execute a live KB,
invoke a model, or treat a design manifest as evidence that an evaluation ran.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any


EXPECTED_SCHEMA = "genexus-v3-evaluation-design/1"
SCENARIO_ID = re.compile(r"^E\d{2}$")


def validate(document: Any) -> list[str]:
    errors: list[str] = []
    if not isinstance(document, dict):
        return ["root must be an object"]

    if document.get("schemaVersion") != EXPECTED_SCHEMA:
        errors.append(f"schemaVersion must be {EXPECTED_SCHEMA!r}")
    if not isinstance(document.get("fixturePolicy"), str) or not document["fixturePolicy"].strip():
        errors.append("fixturePolicy must be a non-empty string")

    execution = document.get("executionContract")
    if execution is not None:
        if not isinstance(execution, dict):
            errors.append("executionContract must be an object when present")
        else:
            if not isinstance(execution.get("fixtureRevision"), str) or not execution["fixtureRevision"].strip():
                errors.append("executionContract.fixtureRevision must be a non-empty string")
            if execution.get("mode") != "deterministic-replay":
                errors.append("executionContract.mode must be deterministic-replay")
            if execution.get("requiresLiveSdk") is not False:
                errors.append("executionContract.requiresLiveSdk must be false for replay manifests")
            if execution.get("modelEvaluation") != "not_executed":
                errors.append("executionContract.modelEvaluation must remain not_executed")

    measurement = document.get("measurementPolicy")
    if not isinstance(measurement, dict):
        errors.append("measurementPolicy must be an object")
    else:
        if measurement.get("successIsRequired") is not True:
            errors.append("measurementPolicy.successIsRequired must be true")
        if measurement.get("separateColdWarm") is not True:
            errors.append("measurementPolicy.separateColdWarm must be true")
        record = measurement.get("record")
        if not isinstance(record, list) or not record or any(not isinstance(item, str) for item in record):
            errors.append("measurementPolicy.record must be a non-empty string array")

    scenarios = document.get("scenarios")
    if not isinstance(scenarios, list) or not scenarios:
        return errors + ["scenarios must be a non-empty array"]

    ids: set[str] = set()
    for index, scenario in enumerate(scenarios):
        prefix = f"scenarios[{index}]"
        if not isinstance(scenario, dict):
            errors.append(f"{prefix} must be an object")
            continue
        scenario_id = scenario.get("id")
        if not isinstance(scenario_id, str) or not SCENARIO_ID.fullmatch(scenario_id):
            errors.append(f"{prefix}.id must match E##")
        elif scenario_id in ids:
            errors.append(f"duplicate scenario id: {scenario_id}")
        else:
            ids.add(scenario_id)

        for key in ("name", "fixture"):
            if not isinstance(scenario.get(key), str) or not scenario[key].strip():
                errors.append(f"{prefix}.{key} must be a non-empty string")
        for key in ("plans", "flow", "oracle"):
            value = scenario.get(key)
            if not isinstance(value, list) or not value:
                errors.append(f"{prefix}.{key} must be a non-empty array")
        plans = scenario.get("plans")
        if isinstance(plans, list) and any(not isinstance(plan, int) or isinstance(plan, bool) for plan in plans):
            errors.append(f"{prefix}.plans must contain only integer package ids")

    expected_ids = {f"E{number:02d}" for number in range(1, 16)}
    if ids != expected_ids:
        missing = ",".join(sorted(expected_ids - ids)) or "none"
        unexpected = ",".join(sorted(ids - expected_ids)) or "none"
        errors.append(f"scenario ids must be E01..E15 (missing={missing}; unexpected={unexpected})")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "manifest",
        nargs="?",
        default="plans/v3-evaluation-corpus.json",
        type=Path,
        help="path to the v3 evaluation corpus manifest",
    )
    args = parser.parse_args(argv)
    try:
        document = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"v3-evaluation: unable to read {args.manifest}: {exc}", file=sys.stderr)
        return 2

    errors = validate(document)
    if errors:
        print("v3-evaluation: invalid", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 2

    print(f"v3-evaluation: valid scenarios={len(document['scenarios'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
