import importlib.util
import json
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "validate-v3-plan.py"
MANIFEST = ROOT / "plans" / "v3-execution.json"


def load_module():
    spec = importlib.util.spec_from_file_location("validate_v3_plan", SCRIPT)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ValidateV3PlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_module()

    def test_repository_plan_is_structurally_valid(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(self.module.validate(document), [])

    def test_unknown_dependency_is_rejected(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        document["packages"][0]["dependsOn"] = [999]
        errors = self.module.validate(document)
        self.assertIn("package 74 depends on unknown package 999", errors)

    def test_dependency_cycle_is_rejected(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        document["packages"][0]["dependsOn"] = [75]
        errors = self.module.validate(document)
        self.assertTrue(any(error.startswith("dependency cycle:") for error in errors))

    def test_release_mode_requires_verified_packages(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        # The repository manifest is intentionally release-ready after the v3
        # integration pass. Exercise the guard by introducing a transient
        # non-ready package in memory rather than relying on a stale fixture.
        document["packages"][0]["status"] = "IN_PROGRESS"
        errors = self.module.validate(document, require_ready=True)
        self.assertTrue(any("must be VERIFIED_INTEGRATED" in error for error in errors))

    def test_release_mode_requires_evidence_for_verified_packages(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        document["packages"][0].pop("integrationEvidence", None)
        errors = self.module.validate(document, require_ready=True)
        self.assertTrue(any("integrationEvidence must be an object" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
