#!/usr/bin/env python3
"""Opt-in model eval: does a model pick the right FIRST tool from the served catalog?

For each scenario in tests/agent-evals/first-tool-expectations.json this sends ONE
Anthropic Messages API request (system = initialize instructions, tools = the
served tools/list mapped to the API shape) and scores the first tool_use block.
It measures a single first call only: no worker, no multi-turn recovery.

Never run by CI. Needs ANTHROPIC_API_KEY unless --dry-run (which sends nothing).
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parent.parent
DISCOVERY = ROOT / "src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery"
DEFAULT_TOOLS = DISCOVERY / "tools-list.response.json"
DEFAULT_INIT = DISCOVERY / "initialize.response.json"
DEFAULT_CORPUS = ROOT / "tests/agent-evals/corpus.json"
DEFAULT_EXPECTATIONS = ROOT / "tests/agent-evals/first-tool-expectations.json"
API_URL = "https://api.anthropic.com/v1/messages"
DEFAULT_MODEL = "claude-sonnet-5-5"

# transport(url, headers, body) -> (http_status, parsed_json_body); raises OSError on network failure.
Transport = Callable[[str, "dict[str, str]", bytes], "tuple[int, Any]"]


def urllib_transport(url: str, headers: "dict[str, str]", body: bytes) -> "tuple[int, Any]":
    request = urllib.request.Request(url, data=body, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            return exc.code, json.loads(raw)
        except json.JSONDecodeError:
            return exc.code, {"raw": raw[:500]}


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def to_api_tool(tool: "dict[str, Any]") -> "dict[str, Any]":
    schema = dict(tool.get("inputSchema") or {})
    schema.setdefault("type", "object")
    return {"name": tool["name"], "description": tool.get("description", ""), "input_schema": schema}


def load_instructions(path: Path) -> "str | None":
    if not path.is_file():
        return None
    document = load_json(path)
    document = document.get("result", document)
    instructions = document.get("instructions")
    return instructions if isinstance(instructions, str) and instructions.strip() else None


def build_request(model: str, max_tokens: int, system: "str | None", tools: list, task: str) -> "dict[str, Any]":
    body: "dict[str, Any]" = {
        "model": model,
        "max_tokens": max_tokens,
        "tools": tools,
        "messages": [{"role": "user", "content": task}],
    }
    if system:
        body["system"] = system
    return body


def score_response(response: Any, expected: set, tools_by_name: dict) -> "dict[str, Any]":
    """Score the first tool_use block of an API response body."""
    body = response if isinstance(response, dict) else {}
    usage = body.get("usage") or {}
    first = next(
        (b for b in (body.get("content") or []) if isinstance(b, dict) and b.get("type") == "tool_use"),
        None,
    )
    result: "dict[str, Any]" = {
        "toolCalled": first is not None,
        "tool": None,
        "expected": False,
        "requiredArgsValid": None,
        "missingRequired": [],
        "inputTokens": usage.get("input_tokens"),
        "outputTokens": usage.get("output_tokens"),
    }
    if first is None:
        return result
    name = first.get("name")
    result["tool"] = name
    result["expected"] = name in expected
    schema = (tools_by_name.get(name) or {}).get("input_schema") or {}
    provided = first.get("input") if isinstance(first.get("input"), dict) else {}
    missing = [key for key in schema.get("required") or [] if key not in provided]
    result["missingRequired"] = missing
    result["requiredArgsValid"] = not missing
    return result


def summarize(rows: "list[dict[str, Any]]") -> "dict[str, Any]":
    answered = [r for r in rows if r.get("status") == "ok"]
    called = [r for r in answered if r["toolCalled"]]
    tokens = [r["inputTokens"] for r in answered if isinstance(r.get("inputTokens"), int)]
    correct = sum(1 for r in answered if r["expected"])
    return {
        "scenarios": len(rows),
        "answered": len(answered),
        "errors": len(rows) - len(answered),
        "noToolCall": len(answered) - len(called),
        "firstCallCorrect": correct,
        "firstCallAccuracy": round(correct / len(answered), 4) if answered else None,
        "requiredArgsValidity": round(sum(1 for r in called if r["requiredArgsValid"]) / len(called), 4) if called else None,
        "avgInputTokens": round(sum(tokens) / len(tokens), 1) if tokens else None,
    }


def load_scenarios(expectations_path: Path, corpus_path: Path) -> "list[dict[str, Any]]":
    expectations = load_json(expectations_path)
    corpus_ids = {s["id"] for s in load_json(corpus_path)["scenarios"]}
    always = list(expectations.get("alwaysAcceptable", []))
    scenarios = []
    for entry in expectations["scenarios"]:
        if entry["id"] not in corpus_ids:
            raise ValueError(f"{entry['id']} is not in the corpus")
        scenarios.append({**entry, "accepted": sorted(set(entry["expectedFirstTools"]) | set(always))})
    return scenarios


def run(args: argparse.Namespace, transport: Transport, env: "dict[str, str]", out: Any) -> int:
    api_key = env.get("ANTHROPIC_API_KEY", "")
    if not api_key and not args.dry_run:
        print("ANTHROPIC_API_KEY is not set; refusing to run. Use --dry-run to inspect requests offline.", file=sys.stderr)
        return 2

    tools_doc = load_json(args.tools)
    mcp_tools = tools_doc.get("result", tools_doc)["tools"]
    api_tools = [to_api_tool(t) for t in mcp_tools]
    tools_by_name = {t["name"]: t for t in api_tools}
    system = load_instructions(args.initialize)
    scenarios = load_scenarios(args.expectations, args.corpus)
    if args.scenario:
        unknown = set(args.scenario) - {s["id"] for s in scenarios}
        if unknown:
            print(f"unknown scenario(s): {', '.join(sorted(unknown))}", file=sys.stderr)
            return 2
        scenarios = [s for s in scenarios if s["id"] in args.scenario]
    if args.limit is not None:
        scenarios = scenarios[: args.limit]

    headers = {"x-api-key": api_key, "anthropic-version": "2023-06-01", "content-type": "application/json"}
    rows: "list[dict[str, Any]]" = []
    for scenario in scenarios:
        body = build_request(args.model, args.max_tokens, system, api_tools, scenario["userTask"])
        payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
        row: "dict[str, Any]" = {"id": scenario["id"], "acceptedTools": scenario["accepted"], "requestBytes": len(payload)}
        if args.dry_run:
            row["status"] = "dry-run"
        else:
            try:
                status, response = transport(API_URL, headers, payload)
            except OSError as exc:
                row.update(status="error", error=f"network: {type(exc).__name__}: {exc}")
            else:
                if status == 200:
                    row.update(status="ok", **score_response(response, set(scenario["accepted"]), tools_by_name))
                else:
                    detail = (response.get("error") or {}).get("message") if isinstance(response, dict) else None
                    row.update(status="error", error=f"HTTP {status}: {detail or response}")
        rows.append(row)

    report = {
        "schemaVersion": "genexus-agent-first-tool-eval/1",
        "model": args.model,
        "dryRun": args.dry_run,
        "toolCount": len(api_tools),
        "systemPromptIncluded": system is not None,
        "scenarios": rows,
        "totals": None if args.dry_run else summarize(rows),
    }
    if args.out:
        Path(args.out).write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"{'id':<5} {'status':<8} {'result':<8} {'tool':<28} {'args':<5} {'bytes':>8} {'in_tok':>7}", file=out)
    for r in rows:
        verdict = "-" if r["status"] != "ok" else ("PASS" if r["expected"] else "FAIL")
        args_ok = "-" if r.get("requiredArgsValid") is None else ("ok" if r["requiredArgsValid"] else "miss")
        print(
            f"{r['id']:<5} {r['status']:<8} {verdict:<8} {str(r.get('tool') or '-'):<28} {args_ok:<5} "
            f"{r['requestBytes']:>8} {str(r.get('inputTokens') or '-'):>7}",
            file=out,
        )
        if r["status"] == "error":
            print(f"      {r['error']}", file=out)
    if report["totals"]:
        print(json.dumps(report["totals"]), file=out)
    return 0


def parse_args(argv: "list[str] | None") -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default=DEFAULT_MODEL)
    p.add_argument("--max-tokens", type=int, default=1024)
    p.add_argument("--out", help="write the JSON report to this path")
    p.add_argument("--scenario", action="append", help="scenario id (repeatable)")
    p.add_argument("--limit", type=int)
    p.add_argument("--dry-run", action="store_true", help="print request sizes; send nothing")
    p.add_argument("--tools", type=Path, default=DEFAULT_TOOLS)
    p.add_argument("--initialize", type=Path, default=DEFAULT_INIT)
    p.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
    p.add_argument("--expectations", type=Path, default=DEFAULT_EXPECTATIONS)
    return p.parse_args(argv)


def main(argv: "list[str] | None" = None, transport: "Transport | None" = None,
         env: "dict[str, str] | None" = None, out: Any = None) -> int:
    return run(parse_args(argv), transport or urllib_transport, os.environ if env is None else env, out or sys.stdout)


if __name__ == "__main__":
    raise SystemExit(main())
