import importlib.util
import json
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "validate_tool_contracts", ROOT / "scripts" / "validate-tool-contracts.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class ValidateToolContractsTests(unittest.TestCase):
    def test_repository_contract_is_valid(self):
        document = json.loads(
            (ROOT / "src" / "GxMcp.Gateway" / "tool_definitions.json").read_text(
                encoding="utf-8"
            )
        )
        counts = MODULE.validate_document(document)
        self.assertEqual(50, counts["tools"])
        self.assertEqual(207, counts["actions"])

    def test_invalid_action_example_is_rejected(self):
        tool = {
            "name": "example",
            "inputSchema": {
                "type": "object",
                "required": ["action"],
                "properties": {"action": {"type": "string", "enum": ["read"]}},
                "additionalProperties": False,
                "examples": [{"action": "write"}],
            },
        }
        with self.assertRaises(MODULE.ContractError):
            MODULE.validate_document([tool])

    def test_missing_required_and_extra_property_are_rejected(self):
        schema = {
            "type": "object",
            "required": ["name"],
            "properties": {"name": {"type": "string"}},
            "additionalProperties": False,
            "examples": [{"name": "ok", "extra": True}],
        }
        with self.assertRaises(MODULE.ContractError):
            MODULE.validate_document([{"name": "example", "inputSchema": schema}])


if __name__ == "__main__":
    unittest.main()
