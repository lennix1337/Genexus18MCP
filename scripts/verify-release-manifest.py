#!/usr/bin/env python3
"""Fail-closed verification for a staged Genexus MCP release artifact."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys


REQUIRED = {
    "GxMcp.Gateway.exe",
    "worker/GxMcp.Worker.exe",
    "tool_definitions.json",
}
PROTOCOLS = {"2025-11-25", "2026-07-28"}
PROVENANCE = "gxmcp-sbom.json"


def fail(message: str) -> int:
    print(f"release-manifest: invalid: {message}", file=sys.stderr)
    return 2


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    root = args.directory.resolve()
    manifest_path = root / "gxmcp-manifest.json"
    if not root.is_dir():
        return fail(f"staging directory does not exist: {root}")
    if not manifest_path.is_file():
        return fail("gxmcp-manifest.json is missing")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"manifest is not valid JSON: {exc}")

    expected_version = args.version.lstrip("v")
    if manifest.get("schemaVersion") != "gxmcp-release-manifest/1":
        return fail("unsupported schemaVersion")
    if manifest.get("version") != expected_version:
        return fail(f"version mismatch: expected {expected_version}, got {manifest.get('version')}")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", expected_version):
        return fail("requested version is not semver")
    if manifest.get("schema") != "tool_definitions.json":
        return fail("schema must be tool_definitions.json")
    if not PROTOCOLS.issubset(set(manifest.get("protocolVersions") or [])):
        return fail("both supported protocol revisions are required")
    if manifest.get("provenance") != PROVENANCE:
        return fail(f"provenance must be {PROVENANCE}")
    source_commit = manifest.get("sourceCommit")
    if not isinstance(source_commit, str) or not source_commit.strip():
        return fail("sourceCommit is required")
    if source_commit.strip().lower() == "working-tree":
        return fail("sourceCommit must identify a committed source tree")
    runtime = manifest.get("runtime") or {}
    if runtime.get("gateway") != "net10.0-windows" or runtime.get("worker") != "net48-x86":
        return fail("runtime matrix is unsupported")
    if runtime.get("node") != ">=22.0.0":
        return fail("node runtime floor must be >=22.0.0")

    entries = manifest.get("artifacts")
    if not isinstance(entries, list) or not entries:
        return fail("artifacts must be a non-empty list")
    seen: set[str] = set()
    for entry in entries:
        if not isinstance(entry, dict):
            return fail("artifact entry is not an object")
        relative = entry.get("path")
        if not isinstance(relative, str) or not relative or "\\" in relative:
            return fail(f"artifact path is not normalized: {relative!r}")
        path = Path(relative)
        if path.is_absolute() or ".." in path.parts:
            return fail(f"artifact path escapes staging root: {relative}")
        if relative in seen:
            return fail(f"duplicate artifact: {relative}")
        seen.add(relative)
        artifact = root / path
        if not artifact.is_file():
            return fail(f"artifact is missing: {relative}")
        if int(entry.get("size", -1)) != artifact.stat().st_size:
            return fail(f"size mismatch: {relative}")
        actual = sha256(artifact)
        if str(entry.get("sha256", "")).lower() != actual:
            return fail(f"SHA-256 mismatch: {relative}")

    missing = sorted(REQUIRED - seen)
    if missing:
        return fail("required artifacts are not covered: " + ", ".join(missing))
    if PROVENANCE not in seen:
        return fail("provenance artifact is not covered")
    schema_entry = next(entry for entry in entries if entry["path"] == "tool_definitions.json")
    if manifest.get("schemaSha256", "").lower() != schema_entry["sha256"].lower():
        return fail("schemaSha256 does not match tool_definitions.json")

    print(
        f"release-manifest: valid version={expected_version} "
        f"artifacts={len(entries)} commit={manifest.get('sourceCommit', 'unknown')}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
