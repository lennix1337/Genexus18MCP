import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "generate-operation-contract-inventory.py"
INVENTORY = ROOT / "docs" / "operation-contract-inventory.json"


class OperationContractInventoryTests(unittest.TestCase):
    def test_committed_inventory_is_generated_and_complete(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--check"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        self.assertEqual(inventory["schemaVersion"], "genexus-operation-inventory/1")
        self.assertEqual(inventory["toolCount"], 54)
        self.assertGreaterEqual(inventory["actionCount"], 247)
        self.assertTrue(all(row["actions"] for row in inventory["tools"]))
        for tool in inventory["tools"]:
            for action in tool["actions"]:
                self.assertIn(action["kind"], {"readOnly", "mutating", "modeDependent"})
                self.assertIn(action["retry"], {"safe", "operation_key", "never", "reconcile_inventory"})
                self.assertIn(action["cache"], {"semantic", "never"})

    def test_generator_detects_new_unclassified_action(self):
        definitions = json.loads((ROOT / "src/GxMcp.Gateway/tool_definitions.json").read_text(encoding="utf-8"))
        for tool in definitions:
            if tool.get("name") == "genexus_lifecycle":
                tool["inputSchema"]["properties"]["action"]["enum"].append("__inventory_gap__")
                break
        with tempfile.TemporaryDirectory() as temp:
            temp_path = Path(temp) / "tools.json"
            temp_path.write_text(json.dumps(definitions), encoding="utf-8")
            # The source-level command accepts the production path by design;
            # this focused check exercises the same fail-closed policy directly.
            from importlib.util import module_from_spec, spec_from_file_location
            spec = spec_from_file_location("inventory", SCRIPT)
            module = module_from_spec(spec)
            assert spec and spec.loader
            spec.loader.exec_module(module)
            with self.assertRaises(ValueError):
                module.build_inventory(temp_path, module.CLASSIFIER)

    def test_mode_dependent_analyze_is_registered_in_inventory(self):
        inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        analyze = next(row for row in inventory["tools"] if row["tool"] == "genexus_analyze")
        self.assertEqual(analyze["actions"][0]["kind"], "modeDependent")

    def test_journal_policy_includes_argument_selected_variants(self):
        from importlib.util import module_from_spec, spec_from_file_location
        spec = spec_from_file_location("inventory", SCRIPT)
        module = module_from_spec(spec)
        assert spec and spec.loader
        spec.loader.exec_module(module)
        inventory = module.build_inventory()
        tools = {row["tool"]: row for row in inventory["tools"]}
        recovery = {row["action"]: row for row in tools["genexus_connection_recover"]["actions"]}

        status = recovery["journal_status"]
        self.assertEqual(status["kind"], "readOnly")
        self.assertEqual(status["effects"], "file.read")
        self.assertEqual(status["execution"], "gateway")
        self.assertEqual(status["retry"], "safe")
        self.assertEqual(status["cache"], "never")
        self.assertEqual(status["invalidation"], [])
        self.assertFalse(status["previewSupported"])

        repair = recovery["journal_repair"]
        self.assertEqual(repair["kind"], "readOnly")
        self.assertEqual(repair["effects"], "file.read")
        self.assertTrue(repair["previewSupported"])
        self.assertEqual([variant["selector"] for variant in repair["variants"]],
                         [{"dryRun": "not false"}, {"dryRun": False}])
        self.assertEqual(repair["variants"][0]["kind"], "readOnly")
        self.assertEqual(repair["variants"][0]["retry"], "safe")
        self.assertEqual(repair["variants"][1]["kind"], "mutating")
        self.assertEqual(repair["variants"][1]["effects"], "file.write")
        self.assertEqual(repair["variants"][1]["invalidation"], ["files"])

        for tool_name in ("genexus_connection_recover", "genexus_worker_reload"):
            action = tools[tool_name]["actions"][0]
            self.assertEqual(action["execution"], "gateway")
            self.assertEqual(action["effects"], "process.write")
            self.assertEqual(action["invalidation"], ["process", "sessions"])

    def test_check_detects_stale_published_inventory(self):
        with tempfile.TemporaryDirectory() as temp:
            stale = Path(temp) / "operation-contract-inventory.json"
            stale.write_text("{}\n", encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--check", "--output", str(stale)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )
        self.assertEqual(result.returncode, 2)
        self.assertIn("published inventory differs", result.stderr)

    def test_module_installations_require_inventory_reconciliation(self):
        inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        module = next(row for row in inventory["tools"] if row["tool"] == "genexus_module")
        actions = {row["action"]: row for row in module["actions"]}
        for name in ("install", "install_builtin"):
            self.assertEqual(actions[name]["retry"], "reconcile_inventory")
            self.assertEqual(actions[name]["kind"], "mutating")
        self.assertEqual(actions["list"]["retry"], "safe")


if __name__ == "__main__":
    unittest.main()
