import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import re
import unittest
import zipfile
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "verify-release-manifest.py"
SPEC = importlib.util.spec_from_file_location("verify_release_manifest", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class ReleaseManifestTests(unittest.TestCase):
    def _fixture(self, version="3.0.0-rc.1", engine=False):
        root = Path(tempfile.mkdtemp(prefix="gxmcp-manifest-test-"))
        (root / "worker").mkdir()
        files = {
            "GxMcp.Gateway.exe": b"gateway",
            "worker/GxMcp.Worker.exe": b"worker",
            "tool_definitions.json": b"[]",
            "nexus-ide.vsix": b"vsix",
            "gxmcp-sbom.json": b"{}",
        }
        if engine:
            files["gx-engine.exe"] = b"engine"
            files["gxwire.exe"] = b"wire"
        for relative, payload in files.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
        artifacts = [
            {"path": relative, "size": len(payload), "sha256": MODULE.sha256(root / relative)}
            for relative, payload in files.items()
        ]
        manifest = {
            "schemaVersion": "gxmcp-release-manifest/1",
            "version": version,
            "sourceCommit": "fixture",
            "runtime": {"gateway": "net10.0-windows", "worker": "net48-x86", "node": ">=22.0.0"},
            "protocolVersions": ["2025-11-25", "2026-07-28"],
            "schema": "tool_definitions.json",
            "schemaSha256": MODULE.sha256(root / "tool_definitions.json"),
            "provenance": "gxmcp-sbom.json",
            "artifacts": artifacts,
        }
        (root / "gxmcp-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        return root

    def _verify(self, root, version):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(root), "--version", version],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_release_above_floor_requires_engine_binaries(self):
        for version in ("4.6.1", "4.7.0", "5.0.0-rc.1"):
            root = self._fixture(version=version)
            try:
                result = self._verify(root, version)
                self.assertEqual(2, result.returncode, f"{version}: {result.stdout}")
                self.assertIn("gx-engine.exe", result.stderr)
                self.assertIn("gxwire.exe", result.stderr)
            finally:
                shutil.rmtree(root, ignore_errors=True)

    def test_release_above_floor_with_engine_binaries_is_valid(self):
        root = self._fixture(version="4.6.1", engine=True)
        try:
            result = self._verify(root, "4.6.1")
            self.assertEqual(0, result.returncode, result.stderr)
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_published_releases_up_to_floor_still_verify_without_engine(self):
        for version in ("4.6.0", "4.5.2", "4.6.0-rc.1"):
            root = self._fixture(version=version)
            try:
                result = self._verify(root, version)
                self.assertEqual(0, result.returncode, f"{version}: {result.stderr}")
            finally:
                shutil.rmtree(root, ignore_errors=True)

    def test_valid_manifest(self):
        root = self._fixture()
        try:
            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(root), "--version", "3.0.0-rc.1"],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("artifacts=5", result.stdout)
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_tampered_artifact_is_detected(self):
        root = self._fixture()
        try:
            (root / "tool_definitions.json").write_text("[1]", encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(root), "--version", "3.0.0-rc.1"],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertRegex(result.stderr, r"(size mismatch|SHA-256 mismatch)")
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_archive_must_match_staged_publish_bytes(self):
        root = self._fixture()
        archive_fd, archive_name = tempfile.mkstemp(prefix="gxmcp-manifest-archive-", suffix=".zip")
        os.close(archive_fd)
        archive_path = Path(archive_name)
        try:
            with zipfile.ZipFile(archive_path, "w") as archive:
                for path in sorted(p for p in root.rglob("*") if p.is_file()):
                    archive.write(path, path.relative_to(root).as_posix())
            result = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT),
                    str(root),
                    "--version",
                    "3.0.0-rc.1",
                    "--archive",
                    str(archive_path),
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("archive=", result.stdout)

            (root / "tool_definitions.json").write_text("[1]", encoding="utf-8")
            result = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT),
                    str(root),
                    "--version",
                    "3.0.0-rc.1",
                    "--archive",
                    str(archive_path),
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertRegex(result.stderr, r"(size mismatch|SHA-256 mismatch|file set differs)")
        finally:
            archive_path.unlink(missing_ok=True)
            shutil.rmtree(root, ignore_errors=True)

    def test_working_tree_source_commit_is_rejected(self):
        root = self._fixture()
        try:
            manifest_path = root / "gxmcp-manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["sourceCommit"] = "working-tree"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(root), "--version", "3.0.0-rc.1"],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("committed source tree", result.stderr)
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_source_commit_must_match_when_requested(self):
        root = self._fixture()
        try:
            result = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT),
                    str(root),
                    "--version",
                    "3.0.0-rc.1",
                    "--source-commit",
                    "other-commit",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("does not match", result.stderr)

            result = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT),
                    str(root),
                    "--version",
                    "3.0.0-rc.1",
                    "--source-commit",
                    "fixture",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
        finally:
            shutil.rmtree(root, ignore_errors=True)

    NEUTRAL_CONFIG = {
        "ConfigSchemaVersion": 2,
        "GatewayMode": "stdio-isolated",
        "GeneXus": {"InstallationPath": "C:\\GeneXus18", "WorkerExecutable": "worker\\GxMcp.Worker.exe"},
        "Server": {"HttpPort": 0, "McpStdio": True},
        "Environment": {"ResolutionPolicy": "strict"},
    }

    def _verify_with_config(self, config):
        root = self._fixture()
        try:
            (root / "config.json").write_text(json.dumps(config), encoding="utf-8")
            return subprocess.run(
                [sys.executable, str(SCRIPT), str(root), "--version", "3.0.0-rc.1"],
                capture_output=True,
                text=True,
                check=False,
            )
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_neutral_publish_config_is_accepted(self):
        result = self._verify_with_config(self.NEUTRAL_CONFIG)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_publish_config_with_engine_section_is_rejected(self):
        # Issue #682: a reused publish/ (-SkipBuild, preflight reuse, resume) can
        # still carry Engine.Backend=sdk written for a local run.
        result = self._verify_with_config({**self.NEUTRAL_CONFIG, "Engine": {"Backend": "sdk"}})
        self.assertNotEqual(0, result.returncode)
        self.assertIn("config.json", result.stderr)
        self.assertIn("Engine", result.stderr)

    def test_publish_config_with_kb_catalog_is_rejected(self):
        config = json.loads(json.dumps(self.NEUTRAL_CONFIG))
        config["Environment"]["KBs"] = {"Local": {"Path": "C:\\KBs\\Customer"}}
        result = self._verify_with_config(config)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Environment.KBs", result.stderr)

    def test_publish_config_must_be_json(self):
        root = self._fixture()
        try:
            (root / "config.json").write_text("{not json", encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(root), "--version", "3.0.0-rc.1"],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("config.json", result.stderr)
        finally:
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
