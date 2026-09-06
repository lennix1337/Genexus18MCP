#!/usr/bin/env python3
"""Validate the published MCP tool schemas and their executable examples.

This is intentionally dependency-free so the same guard can run in a clean CI
runner. It checks the JSON Schema subset used by the Gateway (objects, arrays,
primitive types, required fields, enums and additionalProperties) and fails
closed when an example is not a valid invocation for its tool.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "src" / "GxMcp.Gateway" / "tool_definitions.json"


class ContractError(ValueError):
    pass


def _type_matches(value: Any, expected: str) -> bool:
    return {
        "object": isinstance(value, dict),
        "array": isinstance(value, list),
        "string": isinstance(value, str),
        "integer": isinstance(value, int) and not isinstance(value, bool),
        "number": isinstance(value, (int, float)) and not isinstance(value, bool),
        "boolean": isinstance(value, bool),
        "null": value is None,
    }.get(expected, True)


def validate_value(value: Any, schema: dict[str, Any], path: str) -> None:
    if not isinstance(schema, dict):
        raise ContractError(f"{path}: schema must be an object")

    expected = schema.get("type")
    if isinstance(expected, list):
        if not any(_type_matches(value, item) for item in expected):
            raise ContractError(f"{path}: expected one of {expected}, got {type(value).__name__}")
    elif isinstance(expected, str) and not _type_matches(value, expected):
        raise ContractError(f"{path}: expected {expected}, got {type(value).__name__}")

    enum = schema.get("enum")
    if isinstance(enum, list) and value not in enum:
        raise ContractError(f"{path}: value {value!r} is not in enum")

    if isinstance(value, dict):
        required = schema.get("required", [])
        if not isinstance(required, list):
            raise ContractError(f"{path}: required must be an array")
        for name in required:
            if name not in value:
                raise ContractError(f"{path}: missing required property {name!r}")
        properties = schema.get("properties", {})
        if not isinstance(properties, dict):
            raise ContractError(f"{path}: properties must be an object")
        if schema.get("additionalProperties") is False:
            unknown = sorted(set(value) - set(properties))
            if unknown:
                raise ContractError(f"{path}: unknown properties {unknown}")
        for name, child in value.items():
            if name in properties:
                validate_value(child, properties[name], f"{path}.{name}")

    if isinstance(value, list) and isinstance(schema.get("items"), dict):
        for index, item in enumerate(value):
            validate_value(item, schema["items"], f"{path}[{index}]")


def validate_tool(tool: dict[str, Any], index: int) -> int:
    name = tool.get("name")
    if not isinstance(name, str) or not name.strip():
        raise ContractError(f"tools[{index}]: name is required")
    schema = tool.get("inputSchema")
    if not isinstance(schema, dict) or schema.get("type") != "object":
        raise ContractError(f"{name}: inputSchema must be an object schema")
    properties = schema.get("properties", {})
    if not isinstance(properties, dict):
        raise ContractError(f"{name}: inputSchema.properties must be an object")

    action = properties.get("action")
    actions = action.get("enum", []) if isinstance(action, dict) else []
    if len(actions) != len(set(actions)):
        raise ContractError(f"{name}: action enum contains duplicates")
    examples = schema.get("examples", [])
    if not isinstance(examples, list) or not examples:
        raise ContractError(f"{name}: inputSchema.examples must contain at least one example")
    for example_index, example in enumerate(examples):
        validate_value(example, schema, f"{name}.examples[{example_index}]")
    return len(actions)


def validate_document(document: Any) -> dict[str, int]:
    if not isinstance(document, list) or not document:
        raise ContractError("tool definitions must be a non-empty array")
    names: set[str] = set()
    action_count = 0
    for index, tool in enumerate(document):
        if not isinstance(tool, dict):
            raise ContractError(f"tools[{index}]: expected object")
        name = tool.get("name")
        if name in names:
            raise ContractError(f"duplicate tool name {name!r}")
        names.add(name)
        action_count += validate_tool(tool, index)
    return {"tools": len(document), "actions": action_count}


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?", type=Path, default=TOOLS)
    args = parser.parse_args(list(argv) if argv is not None else None)
    try:
        document = json.loads(args.path.read_text(encoding="utf-8"))
        counts = validate_document(document)
        print(f"tool-contracts: valid tools={counts['tools']} actions={counts['actions']}")
        return 0
    except (OSError, json.JSONDecodeError, ContractError) as error:
        print(f"tool-contracts: invalid: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
