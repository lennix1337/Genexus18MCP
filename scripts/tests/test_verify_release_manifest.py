import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import re
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "verify-release-manifest.py"
SPEC = importlib.util.spec_from_file_location("verify_release_manifest", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class ReleaseManifestTests(unittest.TestCase):
    def _fixture(self):
        root = Path(tempfile.mkdtemp(prefix="gxmcp-manifest-test-"))
        (root / "worker").mkdir()
        files = {
            "GxMcp.Gateway.exe": b"gateway",
            "worker/GxMcp.Worker.exe": b"worker",
            "tool_definitions.json": b"[]",
            "nexus-ide.vsix": b"vsix",
            "gxmcp-sbom.json": b"{}",
        }
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
            "version": "3.0.0-rc.1",
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

    def test_working_tree_provenance_is_rejected(self):
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


if __name__ == "__main__":
    unittest.main()
