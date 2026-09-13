import importlib.util
import json
import shutil
import tempfile
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
        self.assertEqual(54, counts["tools"])
        self.assertEqual(226, counts["actions"])

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

    def test_repository_router_parameter_contract_is_valid(self):
        document = json.loads(
            (ROOT / "src" / "GxMcp.Gateway" / "tool_definitions.json").read_text(
                encoding="utf-8"
            )
        )

        counts = MODULE.validate_router_parameters(document, ROOT)

        self.assertGreaterEqual(counts["tools"], 5)
        self.assertGreater(counts["declared"], 0)
        self.assertGreater(counts["allowlisted"], 0)

    def test_new_router_parameter_fails_closed_without_allowlist_entry(self):
        document = [
            {
                "name": "genexus_query",
                "inputSchema": {
                    "type": "object",
                    "properties": {"query": {"type": "string"}},
                },
            }
        ]
        source = """
using Newtonsoft.Json.Linq;
public class SearchRouter {
    public object? ConvertToolCall(string toolName, JObject? args) {
        switch (toolName) {
            case "genexus_query":
                var hidden = args?["newParameter"];
                return hidden;
            default:
                return null;
        }
    }
}
"""
        with tempfile.TemporaryDirectory() as temp:
            router_dir = Path(temp) / "src" / "GxMcp.Gateway" / "Routers"
            router_dir.mkdir(parents=True)
            for filename in ("AnalyzeRouter.cs", "ObjectRouter.cs"):
                shutil.copy(ROOT / "src" / "GxMcp.Gateway" / "Routers" / filename, router_dir / filename)
            router = router_dir / "SearchRouter.cs"
            router.write_text(source, encoding="utf-8")

            with self.assertRaisesRegex(MODULE.ContractError, "newParameter"):
                MODULE.validate_router_parameters(document, Path(temp))

    def test_known_description_debt_is_allowed_but_new_debt_fails(self):
        document = json.loads(
            (ROOT / "src" / "GxMcp.Gateway" / "tool_definitions.json").read_text(
                encoding="utf-8"
            )
        )
        counts = MODULE.validate_descriptions(document)
        self.assertEqual(75, counts["known_debt"])
        self.assertEqual(0, counts["action_debt"])

        query = next(tool for tool in document if tool["name"] == "genexus_query")
        query["inputSchema"]["properties"]["newParameter"] = {"type": "string"}
        with self.assertRaises(MODULE.ContractError):
            MODULE.validate_descriptions(document)

    def test_action_description_has_no_debt_allowlist(self):
        tool = {
            "name": "example",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "action": {"type": "string", "enum": ["read"]}
                },
            },
        }
        with self.assertRaises(MODULE.ContractError):
            MODULE.validate_descriptions([tool])


if __name__ == "__main__":
    unittest.main()
