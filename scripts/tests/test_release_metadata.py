import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "verify-release-metadata.py"


class ReleaseMetadataTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="gxmcp-release-metadata-"))
        (self.root / "src" / "nexus-ide").mkdir(parents=True)
        self._write("package.json", {"name": "genexus-mcp", "version": "3.0.0"})
        self._write(
            "package-lock.json",
            {"name": "genexus-mcp", "version": "3.0.0", "packages": {"": {"version": "3.0.0"}}},
        )
        self._write("src/nexus-ide/package.json", {"name": "nexus-ide", "version": "3.0.0"})
        self._write(
            "src/nexus-ide/package-lock.json",
            {"name": "nexus-ide", "version": "3.0.0", "packages": {"": {"version": "3.0.0"}}},
        )

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def _write(self, relative, document):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(document), encoding="utf-8")

    def _run(self):
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(self.root), "--version", "3.0.0"],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_all_metadata_matches(self):
        result = self._run()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("fields=6", result.stdout)

    def test_stale_lockfile_is_rejected_with_file_and_field(self):
        lock = json.loads((self.root / "package-lock.json").read_text(encoding="utf-8"))
        lock["packages"][""]["version"] = "2.56.0"
        (self.root / "package-lock.json").write_text(json.dumps(lock), encoding="utf-8")
        result = self._run()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("package-lock.json packages[''].version", result.stderr)


if __name__ == "__main__":
    unittest.main()
