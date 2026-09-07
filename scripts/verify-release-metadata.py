#!/usr/bin/env python3
"""Validate that all published package metadata carries one release version."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys


SEMVER = re.compile(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\Z")


def fail(message: str) -> int:
    print(f"release-metadata: invalid: {message}", file=sys.stderr)
    return 2


def read_json(path: Path) -> object:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise ValueError(f"missing file: {path.relative_to(path.parents[2]) if len(path.parents) > 2 else path}")
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read {path}: {exc}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).parents[1])
    parser.add_argument("--version", required=True)
    args = parser.parse_args()

    root = args.root.resolve()
    version = args.version.lstrip("v")
    if not SEMVER.fullmatch(version):
        return fail(f"requested version is not semver: {args.version}")

    paths = {
        "package.json": (root / "package.json", ("version",)),
        "package-lock.json": (root / "package-lock.json", ("version", "packages", "", "version")),
        "src/nexus-ide/package.json": (root / "src" / "nexus-ide" / "package.json", ("version",)),
        "src/nexus-ide/package-lock.json": (
            root / "src" / "nexus-ide" / "package-lock.json",
            ("version", "packages", "", "version"),
        ),
    }
    observed: list[tuple[str, str, object]] = []
    try:
        for label, (path, keys) in paths.items():
            document = read_json(path)
            if not isinstance(document, dict):
                raise ValueError(f"{label} must contain a JSON object")
            if keys == ("version",):
                values = [("version", document.get("version"))]
            else:
                packages = document.get("packages")
                values = [
                    ("version", document.get("version")),
                    ("packages[''].version", packages.get("", {}).get("version") if isinstance(packages, dict) else None),
                ]
            for field, value in values:
                observed.append((label, field, value))
    except ValueError as exc:
        return fail(str(exc))

    mismatches = [
        f"{label} {field}={value!r} (expected {version})"
        for label, field, value in observed
        if value != version
    ]
    if mismatches:
        return fail("; ".join(mismatches))

    print(f"release-metadata: valid version={version} fields={len(observed)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
