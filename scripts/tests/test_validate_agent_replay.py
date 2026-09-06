import importlib.util
import json
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "validate-agent-replay.py"
MANIFEST = ROOT / "tests" / "agent-evals" / "corpus.json"


def load_module():
    spec = importlib.util.spec_from_file_location("validate_agent_replay", SCRIPT)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def valid_report():
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    revision = manifest["executionContract"]["fixtureRevision"]
    return {
        "schemaVersion": "genexus-v3-agent-replay/1",
        "status": "REPLAY_RESULT",
        "fixtureRevision": revision,
        "scenarios": [
            {
                "id": f"E{index:02d}",
                "attempted": True,
                "skipped": False,
                "gatePassed": True,
                "toolCalls": 2,
                "invalidCalls": 0,
                "unexpectedEffects": 0,
                "unknownRetries": 0,
                "containsSecrets": False,
                "containsSourcePayload": False,
            }
            for index in range(1, 16)
        ],
    }


class ValidateAgentReplayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_module()
        cls.manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))

    def test_valid_report_covers_all_scenarios(self):
        self.assertEqual(self.module.validate(valid_report(), self.manifest), [])

    def test_unknown_retry_and_telemetry_leak_fail_closed(self):
        report = valid_report()
        report["scenarios"][11]["unknownRetries"] = 1
        report["scenarios"][14]["containsSourcePayload"] = True
        errors = self.module.validate(report, self.manifest)
        self.assertTrue(any("unknownRetries must be zero" in error for error in errors))
        self.assertTrue(any("containsSourcePayload must be false" in error for error in errors))

    def test_revision_mismatch_is_rejected(self):
        report = valid_report()
        report["fixtureRevision"] = "different-fixture"
        errors = self.module.validate(report, self.manifest)
        self.assertIn("fixtureRevision does not match the corpus executionContract", errors)

    def test_missing_scenario_is_rejected(self):
        report = valid_report()
        report["scenarios"] = report["scenarios"][:-1]
        errors = self.module.validate(report, self.manifest)
        self.assertIn("scenario ids must cover E01..E15 exactly", errors)


if __name__ == "__main__":
    unittest.main()
