#!/usr/bin/env python3
"""Generate and validate the published MCP operation inventory.

The executable policy remains in OperationClassifier.cs.  This command projects
that policy together with tool_definitions.json into a reviewable JSON artifact,
then fails closed when a published action is missing from the classifier.  It is
deliberately source based: no gateway process or KB is required to run it.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Iterable


ROOT = Path(__file__).resolve().parents[1]
CLASSIFIER = ROOT / "src" / "GxMcp.Gateway" / "OperationClassifier.cs"
TOOLS = ROOT / "src" / "GxMcp.Gateway" / "tool_definitions.json"


def _strings(value: str) -> list[str]:
    return re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"', value)


def _set_block(source: str, field: str) -> set[str]:
    match = re.search(
        rf"private static readonly HashSet<string> {field}.*?\{{(.*?)\n        \}};",
        source,
        re.S,
    )
    if not match:
        raise ValueError(f"could not locate {field} in OperationClassifier.cs")
    return set(_strings(match.group(1)))


def read_policy(path: Path) -> tuple[dict[str, dict[str, set[str]]], dict[str, set[str]]]:
    source = path.read_text(encoding="utf-8")
    contracts: dict[str, dict[str, set[str]]] = {}
    marker = "private static readonly Dictionary<string, ActionContract> ActionContracts"
    start = source.index(marker)
    end = source.index("\n\n        // Only actions", start)
    block = source[start:end]
    entries = list(re.finditer(r'\["([^"]+)"\]\s*=\s*Contract\(', block))
    for index, entry in enumerate(entries):
        segment_end = entries[index + 1].start() if index + 1 < len(entries) else len(block)
        segment = block[entry.end() : segment_end]
        read_marker = segment.find("readOnly:")
        mutate_marker = segment.find("mutating:")
        if read_marker < 0 or mutate_marker < 0 or mutate_marker <= read_marker:
            raise ValueError(f"malformed contract for {entry.group(1)}")
        contracts[entry.group(1)] = {
            "readOnly": set(_strings(segment[read_marker + len("readOnly:") : mutate_marker])),
            "mutating": set(_strings(segment[mutate_marker + len("mutating:") :])),
        }

    named = {
        field: _set_block(source, field)
        for field in (
            "PureReadOnlyTools",
            "KnownMutatingTools",
            "ModeDependentTools",
            "NameOnlyMutatingTools",
        )
    }
    return contracts, named


def classify(tool: str, action: str | None, contracts: dict[str, dict[str, set[str]]], named: dict[str, set[str]]) -> str:
    if action and tool in contracts:
        if action in contracts[tool]["readOnly"]:
            return "readOnly"
        if action in contracts[tool]["mutating"]:
            return "mutating"
        return "unknown"
    if tool in named["PureReadOnlyTools"]:
        return "readOnly"
    if tool in named["KnownMutatingTools"] or tool in named["NameOnlyMutatingTools"]:
        return "mutating"
    if tool in named["ModeDependentTools"]:
        return "modeDependent"
    return "unknown"


def router_for(tool: str) -> str:
    if tool == "genexus_lifecycle" or tool.startswith("genexus_kb") or tool in {
        "genexus_whoami", "genexus_doctor", "genexus_connection_recover",
        "genexus_worker_reload", "genexus_navigation", "genexus_build_plan",
        "genexus_validate", "genexus_build", "genexus_test", "genexus_doc",
    }:
        return "SystemRouter"
    if tool in {"genexus_read", "genexus_inspect", "genexus_analyze", "genexus_search_source", "genexus_linter", "genexus_get_navigation", "genexus_get_signature", "genexus_inject_context"}:
        return "AnalyzeRouter"
    return "OperationsRouter"


def effect_for(kind: str, tool: str) -> str:
    if kind == "readOnly":
        return "kb.read"
    if tool in {"genexus_connection_recover", "genexus_worker_reload"}:
        return "process.write"
    if tool in {"genexus_test", "genexus_run_object"}:
        return "external.execute"
    if tool == "genexus_sdk_probe":
        return "file.write"
    if kind == "mutating":
        return "kb.write"
    return "unknown"


def build_inventory(tools_path: Path = TOOLS, classifier_path: Path = CLASSIFIER) -> dict:
    tools = json.loads(tools_path.read_text(encoding="utf-8"))
    contracts, named = read_policy(classifier_path)
    entries: list[dict] = []
    errors: list[str] = []
    for definition in tools:
        tool = definition.get("name")
        if not isinstance(tool, str) or not tool:
            errors.append("tool without a name")
            continue
        props = definition.get("inputSchema", {}).get("properties", {})
        action_schema = props.get("action", {}) if isinstance(props, dict) else {}
        actions = action_schema.get("enum", []) if isinstance(action_schema, dict) else []
        actions = [str(action) for action in actions]
        rows = []
        if actions:
            for action in actions:
                kind = classify(tool, action, contracts, named)
                if kind == "unknown":
                    errors.append(f"{tool}:{action}")
                rows.append({
                    "action": action,
                    "kind": kind,
                    "effects": effect_for(kind, tool),
                    "execution": "worker" if kind in {"readOnly", "mutating"} else "unknown",
                    "retry": "safe" if kind == "readOnly" else ("operation_key" if kind == "mutating" else "never"),
                    "cache": "semantic" if kind == "readOnly" else "never",
                    "invalidation": [] if kind != "mutating" else ["kb", "dependents", "collections"],
                })
        else:
            kind = classify(tool, None, contracts, named)
            if kind == "unknown":
                errors.append(tool)
            rows.append({
                "action": None,
                "kind": kind,
                "effects": effect_for(kind, tool),
                "execution": "worker" if kind in {"readOnly", "mutating"} else "unknown",
                "retry": "safe" if kind == "readOnly" else ("operation_key" if kind == "mutating" else "never"),
                "cache": "semantic" if kind == "readOnly" else "never",
                "invalidation": [] if kind != "mutating" else ["kb", "dependents", "collections"],
            })
        entries.append({
            "tool": tool,
            "router": router_for(tool),
            "actions": rows,
            "inputExamples": len(action_schema.get("examples", [])) if isinstance(action_schema, dict) else 0,
            "outputSchema": "outputSchema" in definition,
        })
    if errors:
        raise ValueError("unclassified published operations: " + ", ".join(sorted(errors)))
    return {
        "schemaVersion": "genexus-operation-inventory/1",
        "source": [str(tools_path.relative_to(ROOT)).replace("\\", "/"), str(classifier_path.relative_to(ROOT)).replace("\\", "/")],
        "toolCount": len(entries),
        "actionCount": sum(len(item["actions"]) for item in entries),
        "tools": entries,
    }


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="write the deterministic inventory JSON")
    parser.add_argument("--check", action="store_true", help="validate only; do not write")
    args = parser.parse_args(list(argv) if argv is not None else None)
    try:
        inventory = build_inventory()
        if args.output and not args.check:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(inventory, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"operation-inventory: valid tools={inventory['toolCount']} actions={inventory['actionCount']}")
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"operation-inventory: invalid: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
