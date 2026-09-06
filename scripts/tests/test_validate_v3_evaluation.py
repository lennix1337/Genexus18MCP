import importlib.util
import json
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "validate-v3-evaluation.py"
MANIFEST = ROOT / "plans" / "v3-evaluation-corpus.json"
REPLAY_MANIFEST = ROOT / "tests" / "agent-evals" / "corpus.json"


def load_module():
    spec = importlib.util.spec_from_file_location("validate_v3_evaluation", SCRIPT)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ValidateV3EvaluationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_module()

    def test_repository_corpus_has_all_expected_scenarios(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(self.module.validate(document), [])

    def test_replay_corpus_has_explicit_fixture_and_model_boundaries(self):
        document = json.loads(REPLAY_MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(self.module.validate(document), [])
        self.assertEqual(document["status"], "REPLAY_SPECIFIED")
        self.assertEqual(document["executionContract"]["mode"], "deterministic-replay")
        self.assertFalse(document["executionContract"]["requiresLiveSdk"])
        self.assertEqual(document["executionContract"]["modelEvaluation"], "not_executed")

    def test_duplicate_or_missing_scenario_is_rejected(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        document["scenarios"][-1]["id"] = document["scenarios"][0]["id"]
        errors = self.module.validate(document)
        self.assertTrue(any("duplicate scenario id" in error for error in errors))
        self.assertTrue(any("scenario ids must be E01..E15" in error for error in errors))

    def test_success_and_cold_warm_measurements_are_required(self):
        document = json.loads(MANIFEST.read_text(encoding="utf-8"))
        document["measurementPolicy"]["successIsRequired"] = False
        document["measurementPolicy"]["separateColdWarm"] = False
        errors = self.module.validate(document)
        self.assertIn("measurementPolicy.successIsRequired must be true", errors)
        self.assertIn("measurementPolicy.separateColdWarm must be true", errors)


if __name__ == "__main__":
    unittest.main()
