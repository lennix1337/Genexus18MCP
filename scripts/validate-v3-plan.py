#!/usr/bin/env python3
"""Validate the v3 execution manifest and its dependency graph.

The default mode checks that the plan is internally coherent.  ``--require-ready``
is intended for a release gate and refuses packages that are not
``VERIFIED_INTEGRATED``; it does not turn a plan status into runtime evidence.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any


EXPECTED_SCHEMA = "genexus-v3-plan/1"
SHA1 = re.compile(r"^[0-9a-f]{40}$", re.IGNORECASE)


def validate(document: Any, require_ready: bool = False) -> list[str]:
    errors: list[str] = []
    if not isinstance(document, dict):
        return ["root must be an object"]

    if document.get("schemaVersion") != EXPECTED_SCHEMA:
        errors.append(f"schemaVersion must be {EXPECTED_SCHEMA!r}")
    base_commit = document.get("baseCommit")
    if not isinstance(base_commit, str) or not SHA1.fullmatch(base_commit):
        errors.append("baseCommit must be a 40-character hexadecimal commit")

    states = document.get("states")
    if not isinstance(states, list) or not states or any(not isinstance(item, str) for item in states):
        errors.append("states must be a non-empty string array")
        states = []
    state_set = set(states)

    packages = document.get("packages")
    if not isinstance(packages, list) or not packages:
        return errors + ["packages must be a non-empty array"]

    by_id: dict[int, dict[str, Any]] = {}
    for index, package in enumerate(packages):
        prefix = f"packages[{index}]"
        if not isinstance(package, dict):
            errors.append(f"{prefix} must be an object")
            continue
        package_id = package.get("id")
        if not isinstance(package_id, int) or isinstance(package_id, bool):
            errors.append(f"{prefix}.id must be an integer")
        elif package_id in by_id:
            errors.append(f"duplicate package id: {package_id}")
        else:
            by_id[package_id] = package

        for key in ("file", "title", "priority", "effort", "risk", "completionKind"):
            if not isinstance(package.get(key), str) or not package[key].strip():
                errors.append(f"{prefix}.{key} must be a non-empty string")

        status = package.get("status")
        if status not in state_set:
            errors.append(f"{prefix}.status is not declared in states: {status!r}")
        if require_ready and status != "VERIFIED_INTEGRATED":
            errors.append(f"{prefix}.status must be VERIFIED_INTEGRATED in release mode")

        # A release-ready status must carry a reviewable evidence envelope.  The
        # status alone is deliberately insufficient: this prevents a stale or
        # hand-edited manifest from turning an unreviewed package into a release
        # gate pass.
        if status == "VERIFIED_INTEGRATED":
            evidence = package.get("integrationEvidence")
            if not isinstance(evidence, dict):
                errors.append(f"{prefix}.integrationEvidence must be an object for VERIFIED_INTEGRATED packages")
            else:
                report = evidence.get("report")
                artifacts = evidence.get("artifacts")
                external_gates = evidence.get("externalGates")
                if not isinstance(report, str) or not report.strip():
                    errors.append(f"{prefix}.integrationEvidence.report must be a non-empty string")
                if not isinstance(artifacts, list) or not artifacts or any(not isinstance(item, str) or not item.strip() for item in artifacts):
                    errors.append(f"{prefix}.integrationEvidence.artifacts must be a non-empty string array")
                if not isinstance(external_gates, list) or any(not isinstance(item, str) or not item.strip() for item in external_gates):
                    errors.append(f"{prefix}.integrationEvidence.externalGates must be a string array")

        scope = package.get("scope")
        if not isinstance(scope, list) or not scope or any(not isinstance(item, str) or not item.strip() for item in scope):
            errors.append(f"{prefix}.scope must be a non-empty string array")

        dependencies = package.get("dependsOn", [])
        if not isinstance(dependencies, list) or any(not isinstance(dep, int) or isinstance(dep, bool) for dep in dependencies):
            errors.append(f"{prefix}.dependsOn must contain only integer package ids")

    for package_id, package in by_id.items():
        dependencies = package.get("dependsOn", [])
        if isinstance(dependencies, list):
            for dependency in dependencies:
                if dependency not in by_id:
                    errors.append(f"package {package_id} depends on unknown package {dependency}")

    # Detect cycles independently of the error list so one malformed package
    # cannot make release validation accidentally pass.
    visiting: set[int] = set()
    visited: set[int] = set()

    def visit(package_id: int, trail: list[int]) -> None:
        if package_id in visiting:
            cycle_start = trail.index(package_id) if package_id in trail else 0
            cycle = trail[cycle_start:] + [package_id]
            errors.append("dependency cycle: " + " -> ".join(map(str, cycle)))
            return
        if package_id in visited or package_id not in by_id:
            return
        visiting.add(package_id)
        dependencies = by_id[package_id].get("dependsOn", [])
        if isinstance(dependencies, list):
            for dependency in dependencies:
                if isinstance(dependency, int) and not isinstance(dependency, bool):
                    visit(dependency, trail + [package_id])
        visiting.remove(package_id)
        visited.add(package_id)

    for package_id in by_id:
        visit(package_id, [])
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "manifest",
        nargs="?",
        default="plans/v3-execution.json",
        type=Path,
        help="path to the v3 execution manifest",
    )
    parser.add_argument(
        "--require-ready",
        action="store_true",
        help="require VERIFIED_INTEGRATED for every package (release gate)",
    )
    args = parser.parse_args(argv)
    try:
        document = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"v3-plan: unable to read {args.manifest}: {exc}", file=sys.stderr)
        return 2

    errors = validate(document, require_ready=args.require_ready)
    if errors:
        print("v3-plan: invalid", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 2

    mode = "release-ready" if args.require_ready else "structurally valid"
    print(f"v3-plan: {mode} packages={len(document['packages'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
