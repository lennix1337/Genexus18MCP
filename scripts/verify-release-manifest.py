#!/usr/bin/env python3
"""Fail-closed verification for a staged Genexus MCP release artifact."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import zipfile


REQUIRED = {
    "GxMcp.Gateway.exe",
    "worker/GxMcp.Worker.exe",
    "tool_definitions.json",
}
# Engine.Backend defaults to gxengine, so releases after ENGINE_FLOOR must ship
# the Rust binaries. Releases up to and including 4.6.0 predate this check.
ENGINE_REQUIRED = {"gx-engine.exe", "gxwire.exe"}
ENGINE_FLOOR = (4, 6, 0)
PROTOCOLS = {"2025-11-25", "2026-07-28"}
PROVENANCE = "gxmcp-sbom.json"
# Keys build.ps1 writes into the neutral publish/config.json (New-PublishConfigJson
# in scripts/publish-config.ps1). Anything else, such as an Engine section kept
# for a local run or a KB catalog, is machine-specific and must not ship (#682).
NEUTRAL_CONFIG_KEYS = {
    "": {"ConfigSchemaVersion", "GatewayMode", "GeneXus", "Server", "Environment"},
    "GeneXus": {"InstallationPath", "WorkerExecutable"},
    "Environment": {"ResolutionPolicy"},
}


def requires_engine(version: str) -> bool:
    """True when the release version is above the floor (a prerelease of the floor is not)."""
    core = re.match(r"(\d+)\.(\d+)\.(\d+)(-.*)?$", version)
    if not core:
        return False
    numbers = tuple(int(core.group(i)) for i in (1, 2, 3))
    return numbers > ENGINE_FLOOR


def fail(message: str) -> int:
    print(f"release-manifest: invalid: {message}", file=sys.stderr)
    return 2


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def publish_config_problem(path: Path) -> str | None:
    """Return why publish/config.json is not the neutral build output, or None."""
    try:
        config = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        return f"config.json is not valid JSON: {exc}"
    if not isinstance(config, dict):
        return "config.json must be a JSON object"
    for section, allowed in NEUTRAL_CONFIG_KEYS.items():
        node = config.get(section) if section else config
        if node is None:
            continue
        if not isinstance(node, dict):
            return f"config.json {section} must be an object"
        extra = sorted(set(node) - allowed)
        if extra:
            names = ", ".join(f"{section}.{key}" if section else key for key in extra)
            return (
                f"config.json is not the neutral build output; unexpected {names}. "
                "Rebuild publish/ with build.ps1 (without -PreserveEngineConfig) before releasing."
            )
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument(
        "--source-commit",
        help="Require the manifest sourceCommit to match this resolved Git commit.",
    )
    parser.add_argument(
        "--archive",
        type=Path,
        help="Verify that the ZIP contains exactly the staged publish tree and bytes.",
    )
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
    if args.source_commit and source_commit.strip().lower() != args.source_commit.strip().lower():
        return fail(
            "sourceCommit does not match the checked-out release commit: "
            f"expected {args.source_commit}, got {source_commit}"
        )
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

    required = REQUIRED | (ENGINE_REQUIRED if requires_engine(expected_version) else set())
    missing = sorted(required - seen)
    if missing:
        return fail("required artifacts are not covered: " + ", ".join(missing))
    if PROVENANCE not in seen:
        return fail("provenance artifact is not covered")
    schema_entry = next(entry for entry in entries if entry["path"] == "tool_definitions.json")
    if manifest.get("schemaSha256", "").lower() != schema_entry["sha256"].lower():
        return fail("schemaSha256 does not match tool_definitions.json")

    config_path = root / "config.json"
    if config_path.is_file():
        problem = publish_config_problem(config_path)
        if problem:
            return fail(problem)

    if args.archive:
        archive_path = args.archive.resolve()
        if not archive_path.is_file():
            return fail(f"archive does not exist: {archive_path}")
        expected_files = sorted(
            path.relative_to(root).as_posix()
            for path in root.rglob("*")
            if path.is_file()
        )
        try:
            with zipfile.ZipFile(archive_path, "r") as archive:
                infos = [info for info in archive.infolist() if not info.is_dir()]
                names = [info.filename.replace("\\", "/") for info in infos]
                if len(names) != len(set(names)):
                    return fail("archive contains duplicate file entries")
                for name in names:
                    path = Path(name)
                    if path.is_absolute() or ".." in path.parts or "\\" in name:
                        return fail(f"archive path is unsafe: {name}")
                if sorted(names) != expected_files:
                    missing = sorted(set(expected_files) - set(names))
                    extra = sorted(set(names) - set(expected_files))
                    return fail(
                        "archive file set differs from publish/ "
                        f"(missing={missing[:5]}, extra={extra[:5]})"
                    )
                for info in infos:
                    name = info.filename.replace("\\", "/")
                    data = archive.read(info)
                    current = root / Path(name)
                    if len(data) != current.stat().st_size:
                        return fail(f"archive size mismatch: {name}")
                    if hashlib.sha256(data).hexdigest() != sha256(current):
                        return fail(f"archive SHA-256 mismatch: {name}")
        except (OSError, zipfile.BadZipFile, KeyError) as exc:
            return fail(f"archive could not be verified: {exc}")

    print(
        f"release-manifest: valid version={expected_version} "
        f"artifacts={len(entries)} commit={manifest.get('sourceCommit', 'unknown')}"
        + (f" archive={args.archive.name}" if args.archive else "")
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
