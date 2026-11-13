#!/usr/bin/env python3
"""Opt-in model eval: does a model pick the right FIRST tool from the served catalog?

For each scenario in tests/agent-evals/first-tool-expectations.json this sends ONE
Anthropic Messages API request (system = initialize instructions, tools = the
served tools/list mapped to the API shape) and scores the first tool_use block.
It measures a single first call only: no worker, no multi-turn recovery.

Never run by CI or on pull requests. Needs ANTHROPIC_API_KEY unless --dry-run (which sends nothing).
--profile picks the tool list a Gateway serves under that tool profile (fixtures written by
ToolProfileListFixtureTests); --markdown records a scoreboard row; --strict scores without the
always-acceptable orienting call.
"""

from __future__ import annotations

import argparse
import json
import datetime
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parent.parent
DISCOVERY = ROOT / "src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery"
DEFAULT_TOOLS = DISCOVERY / "tools-list.response.json"
PROFILES = ("all", "core", "standard", "authoring", "devops", "ui", "db")
HARNESS_VERSION = "2"
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


def tools_path_for_profile(profile: str) -> Path:
    return DEFAULT_TOOLS if profile == "all" else DISCOVERY / f"tools-list.{profile}.response.json"


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


def score_response(response: Any, expected: set, tools_by_name: dict,
                   strict_expected: "set | None" = None) -> "dict[str, Any]":
    """Score the first tool_use block of an API response body.

    `expected` is the lenient set (includes the always-acceptable orienting call);
    `strict_expected` is the set without it and defaults to `expected`.
    """
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
        "expectedStrict": False,
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
    result["expectedStrict"] = name in (expected if strict_expected is None else strict_expected)
    schema = (tools_by_name.get(name) or {}).get("input_schema") or {}
    provided = first.get("input") if isinstance(first.get("input"), dict) else {}
    missing = [key for key in schema.get("required") or [] if key not in provided]
    result["missingRequired"] = missing
    result["requiredArgsValid"] = not missing
    return result


def summarize(rows: "list[dict[str, Any]]", strict: bool = False) -> "dict[str, Any]":
    rows = [r for r in rows if r.get("status") != "skipped"]
    answered = [r for r in rows if r.get("status") == "ok"]
    called = [r for r in answered if r["toolCalled"]]
    tokens = [r["inputTokens"] for r in answered if isinstance(r.get("inputTokens"), int)]
    correct = sum(1 for r in answered if r["expected"])
    strict_correct = sum(1 for r in answered if r["expectedStrict"])
    valid = sum(1 for r in called if r["requiredArgsValid"])
    headline = strict_correct if strict else correct
    return {
        "scenarios": len(rows),
        "answered": len(answered),
        "errors": len(rows) - len(answered),
        "noToolCall": len(answered) - len(called),
        "firstCallCorrect": headline,
        "firstCallAccuracy": round(headline / len(answered), 4) if answered else None,
        "lenientFirstCallCorrect": correct,
        "lenientFirstCallAccuracy": round(correct / len(answered), 4) if answered else None,
        "strictFirstCallCorrect": strict_correct,
        "strictFirstCallAccuracy": round(strict_correct / len(answered), 4) if answered else None,
        "requiredArgsValid": valid,
        "requiredArgsValidity": round(valid / len(called), 4) if called else None,
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
        scenarios.append({
            **entry,
            "accepted": sorted(set(entry["expectedFirstTools"]) | set(always)),
            "acceptedStrict": sorted(set(entry["expectedFirstTools"])),
        })
    return scenarios


SCOREBOARD_HEADER = """# Agent model evaluation scoreboard

Rows are written by `scripts/run-agent-model-eval.py --markdown docs/agent-eval-scoreboard.md`
(see [agent-evals.md](agent-evals.md)). One row per (model, profile); a new run replaces its row.
Only first-call tool selection is measured: one request per scenario, no worker, no KB, no
multi-turn recovery. Lenient accuracy also accepts `genexus_whoami` as a first call; strict does not.
"""

SCOREBOARD_COLUMNS = (
    "model", "profile", "scenarios", "first-call accuracy (lenient)", "first-call accuracy (strict)",
    "required-args validity", "avg input tokens/request", "date (UTC)", "harness version",
)
SCOREBOARD_EMPTY = "_No model run has been recorded yet. This table is intentionally empty until a real run is made._"


def _cell(text: Any) -> str:
    return str(text).replace("|", "/").replace("\n", " ").strip()


def _ratio(count: int, total: int) -> str:
    return f"{count}/{total} ({round(100 * count / total)}%)" if total else "n/a"


def scoreboard_row(model: str, profile: str, planned: int, totals: "dict[str, Any]", date: str) -> "list[str]":
    answered = totals["answered"]
    called = answered - totals["noToolCall"]
    tokens = totals["avgInputTokens"]
    return [
        _cell(model), profile, f"{answered}/{planned}",
        _ratio(totals["lenientFirstCallCorrect"], answered),
        _ratio(totals["strictFirstCallCorrect"], answered),
        _ratio(totals["requiredArgsValid"], called),
        "n/a" if tokens is None else f"{tokens:.0f}",
        date, HARNESS_VERSION,
    ]


def parse_scoreboard_rows(text: str) -> "list[list[str]]":
    rows = []
    for line in text.splitlines():
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != len(SCOREBOARD_COLUMNS) or cells[0] == SCOREBOARD_COLUMNS[0] or set(cells[0]) <= {"-", ":"}:
            continue
        rows.append(cells)
    return rows


def render_scoreboard(rows: "list[list[str]]") -> str:
    lines = [SCOREBOARD_HEADER.rstrip("\n"), ""]
    if not rows:
        lines += [SCOREBOARD_EMPTY, ""]
    lines.append("| " + " | ".join(SCOREBOARD_COLUMNS) + " |")
    lines.append("|" + "|".join(" --- " for _ in SCOREBOARD_COLUMNS) + "|")
    for row in sorted(rows, key=lambda r: (r[0], r[1])):
        lines.append("| " + " | ".join(row) + " |")
    return "\n".join(lines) + "\n"


def update_scoreboard(path: Path, row: "list[str]") -> None:
    """Replace the row with the same (model, profile), or append it; rewrite the file."""
    existing = parse_scoreboard_rows(path.read_text(encoding="utf-8")) if path.is_file() else []
    rows = [r for r in existing if (r[0], r[1]) != (row[0], row[1])] + [row]
    path.write_text(render_scoreboard(rows), encoding="utf-8", newline="\n")


def run(args: argparse.Namespace, transport: Transport, env: "dict[str, str]", out: Any,
        today: "Callable[[], str] | None" = None) -> int:
    api_key = env.get("ANTHROPIC_API_KEY", "")
    if not api_key and not args.dry_run:
        print("ANTHROPIC_API_KEY is not set; refusing to run. Use --dry-run to inspect requests offline.", file=sys.stderr)
        return 2
    if args.tools is not None and args.profile != "all":
        print("--tools and --profile are mutually exclusive.", file=sys.stderr)
        return 2
    tools_path = args.tools or tools_path_for_profile(args.profile)
    if not tools_path.is_file():
        print(f"tool list not found: {tools_path}", file=sys.stderr)
        return 2

    tools_doc = load_json(tools_path)
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
    # A scenario none of whose expected tools is served under this profile cannot be answered
    # correctly; it is reported as skipped instead of being scored as a model failure.
    applicable = [s for s in scenarios if set(s["expectedFirstTools"]) & set(tools_by_name)]
    skipped = [s for s in scenarios if s not in applicable]
    planned = len(applicable)
    if args.limit is not None:
        applicable = applicable[: args.limit]

    headers = {"x-api-key": api_key, "anthropic-version": "2023-06-01", "content-type": "application/json"}
    rows: "list[dict[str, Any]]" = []
    for scenario in applicable:
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
                    row.update(status="ok", **score_response(
                        response, set(scenario["accepted"]), tools_by_name, set(scenario["acceptedStrict"])))
                else:
                    detail = (response.get("error") or {}).get("message") if isinstance(response, dict) else None
                    row.update(status="error", error=f"HTTP {status}: {detail or response}")
        rows.append(row)
    for scenario in skipped:
        rows.append({"id": scenario["id"], "status": "skipped", "requestBytes": 0,
                     "error": f"none of {scenario['expectedFirstTools']} is served under profile '{args.profile}'"})

    report = {
        "schemaVersion": "genexus-agent-first-tool-eval/1",
        "harnessVersion": HARNESS_VERSION,
        "model": args.model,
        "profile": args.profile,
        "strict": args.strict,
        "dryRun": args.dry_run,
        "toolCount": len(api_tools),
        "systemPromptIncluded": system is not None,
        "plannedScenarios": planned,
        "scenarios": rows,
        "totals": None if args.dry_run else summarize(rows, args.strict),
    }
    if args.out:
        Path(args.out).write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"profile={args.profile} tools={len(api_tools)} model={args.model} mode={'strict' if args.strict else 'lenient'}", file=out)
    print(f"{'id':<5} {'status':<8} {'result':<8} {'tool':<28} {'args':<5} {'bytes':>8} {'in_tok':>7}", file=out)
    for r in rows:
        key = "expectedStrict" if args.strict else "expected"
        verdict = "-" if r["status"] != "ok" else ("PASS" if r[key] else "FAIL")
        args_ok = "-" if r.get("requiredArgsValid") is None else ("ok" if r["requiredArgsValid"] else "miss")
        print(
            f"{r['id']:<5} {r['status']:<8} {verdict:<8} {str(r.get('tool') or '-'):<28} {args_ok:<5} "
            f"{r['requestBytes']:>8} {str(r.get('inputTokens') or '-'):>7}",
            file=out,
        )
        if r["status"] in ("error", "skipped"):
            print(f"      {r['error']}", file=out)
    if args.dry_run:
        print(f"total request bytes: {sum(r['requestBytes'] for r in rows)} over {len(applicable)} request(s)", file=out)
    if report["totals"]:
        print(json.dumps(report["totals"]), file=out)
    if args.markdown:
        totals = report["totals"]
        if args.dry_run:
            print("--markdown ignored: a dry run has no results to record.", file=sys.stderr)
        elif not totals["answered"]:
            print("--markdown not updated: no scenario was answered.", file=sys.stderr)
        else:
            date = (today or (lambda: datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")))()
            update_scoreboard(Path(args.markdown), scoreboard_row(args.model, args.profile, planned, totals, date))
    return 0


def parse_args(argv: "list[str] | None") -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default=DEFAULT_MODEL)
    p.add_argument("--max-tokens", type=int, default=1024)
    p.add_argument("--out", help="write the JSON report to this path")
    p.add_argument("--scenario", action="append", help="scenario id (repeatable)")
    p.add_argument("--limit", type=int)
    p.add_argument("--dry-run", action="store_true", help="print request sizes; send nothing")
    p.add_argument("--profile", choices=PROFILES, default="all", help="tool profile whose served tools/list is used")
    p.add_argument("--strict", action="store_true", help="score the headline without the always-acceptable orienting call")
    p.add_argument("--markdown", help="add or replace this (model, profile) row in a scoreboard file")
    p.add_argument("--tools", type=Path, default=None, help="explicit tools/list JSON (only with the 'all' profile)")
    p.add_argument("--initialize", type=Path, default=DEFAULT_INIT)
    p.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
    p.add_argument("--expectations", type=Path, default=DEFAULT_EXPECTATIONS)
    return p.parse_args(argv)


def main(argv: "list[str] | None" = None, transport: "Transport | None" = None,
         env: "dict[str, str] | None" = None, out: Any = None,
         today: "Callable[[], str] | None" = None) -> int:
    return run(parse_args(argv), transport or urllib_transport, os.environ if env is None else env,
               out or sys.stdout, today)


if __name__ == "__main__":
    raise SystemExit(main())
