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
        self.assertEqual(inventory["toolCount"], 50)
        self.assertGreaterEqual(inventory["actionCount"], 200)
        self.assertTrue(all(row["actions"] for row in inventory["tools"]))
        for tool in inventory["tools"]:
            for action in tool["actions"]:
                self.assertIn(action["kind"], {"readOnly", "mutating", "modeDependent"})
                self.assertIn(action["retry"], {"safe", "operation_key", "never"})
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


if __name__ == "__main__":
    unittest.main()
