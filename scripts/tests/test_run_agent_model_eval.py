import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "run-agent-model-eval.py"
spec = importlib.util.spec_from_file_location("run_agent_model_eval", SCRIPT)
ev = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ev)

TOOLS = {"tools": [
    {"name": "genexus_read", "description": "Read", "inputSchema": {"properties": {"name": {"type": "string"}}, "required": ["name"]}},
    {"name": "genexus_whoami", "description": "Who", "inputSchema": {}},
]}


def tool_use(name, inp, tokens=100):
    return 200, {"content": [{"type": "tool_use", "name": name, "input": inp}], "usage": {"input_tokens": tokens, "output_tokens": 5}}


REPO = Path(__file__).resolve().parents[2]


class EvalTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        d = Path(self.dir.name)
        (d / "tools.json").write_text(json.dumps(TOOLS), encoding="utf-8")
        (d / "init.json").write_text(json.dumps({"instructions": "SYS"}), encoding="utf-8")
        (d / "corpus.json").write_text(json.dumps({"scenarios": [{"id": "E02"}, {"id": "E03"}]}), encoding="utf-8")
        (d / "exp.json").write_text(json.dumps({
            "alwaysAcceptable": ["genexus_whoami"],
            "scenarios": [
                {"id": "E02", "userTask": "t2", "expectedFirstTools": ["genexus_read"]},
                {"id": "E03", "userTask": "t3", "expectedFirstTools": ["genexus_read"]},
            ]}), encoding="utf-8")
        self.d = d

    def run_main(self, transport, env, *extra):
        argv = ["--tools", str(self.d / "tools.json"), "--initialize", str(self.d / "init.json"),
                "--corpus", str(self.d / "corpus.json"), "--expectations", str(self.d / "exp.json"),
                "--out", str(self.d / "out.json"), *extra]
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stderr(err):
            code = ev.main(argv, transport=transport, env=env, out=out)
        return code, out.getvalue(), err.getvalue()

    def report(self):
        return json.loads((self.d / "out.json").read_text(encoding="utf-8"))

    def test_tool_mapping(self):
        mapped = ev.to_api_tool(TOOLS["tools"][1])
        self.assertEqual(mapped, {"name": "genexus_whoami", "description": "Who", "input_schema": {"type": "object"}})
        self.assertEqual(ev.to_api_tool(TOOLS["tools"][0])["input_schema"]["required"], ["name"])

    def test_scoring(self):
        by = {t["name"]: t for t in map(ev.to_api_tool, TOOLS["tools"])}
        exp = {"genexus_read"}
        ok = ev.score_response(tool_use("genexus_read", {"name": "P"})[1], exp, by)
        self.assertTrue(ok["expected"] and ok["requiredArgsValid"])
        self.assertEqual(ok["inputTokens"], 100)
        wrong = ev.score_response(tool_use("genexus_whoami", {})[1], exp, by)
        self.assertFalse(wrong["expected"])
        none = ev.score_response({"content": [{"type": "text", "text": "hi"}], "usage": {}}, exp, by)
        self.assertFalse(none["toolCalled"] or none["expected"])
        missing = ev.score_response(tool_use("genexus_read", {})[1], exp, by)
        self.assertFalse(missing["requiredArgsValid"])
        self.assertEqual(missing["missingRequired"], ["name"])

    def test_totals_and_request_shape(self):
        seen = []

        def transport(url, headers, body):
            seen.append((url, headers, json.loads(body)))
            return tool_use("genexus_read", {"name": "P"}) if len(seen) == 1 else tool_use("genexus_whoami", {})

        code, out, _ = self.run_main(transport, {"ANTHROPIC_API_KEY": "sk-secret"})
        self.assertEqual(code, 0)
        self.assertNotIn("sk-secret", out)
        url, headers, body = seen[0]
        self.assertEqual(url, ev.API_URL)
        self.assertEqual(headers["x-api-key"], "sk-secret")
        self.assertEqual(body["system"], "SYS")
        self.assertEqual(body["model"], "claude-sonnet-5-5")
        self.assertEqual(body["messages"], [{"role": "user", "content": "t2"}])
        totals = self.report()["totals"]
        # genexus_whoami is always acceptable, so both pass
        self.assertEqual((totals["answered"], totals["firstCallAccuracy"], totals["avgInputTokens"]), (2, 1.0, 100.0))
        self.assertNotIn("sk-secret", json.dumps(self.report()))

    def test_unexpected_tool_lowers_accuracy(self):
        tools = {"tools": TOOLS["tools"] + [{"name": "genexus_other", "description": "", "inputSchema": {}}]}
        (self.d / "tools.json").write_text(json.dumps(tools), encoding="utf-8")
        responses = iter([tool_use("genexus_other", {}), tool_use("genexus_read", {"name": "P"})])
        self.run_main(lambda *a: next(responses), {"ANTHROPIC_API_KEY": "k"})
        self.assertEqual(self.report()["totals"]["firstCallAccuracy"], 0.5)

    def test_missing_key_exits(self):
        called = []
        code, _, err = self.run_main(lambda *a: called.append(a), {})
        self.assertNotEqual(code, 0)
        self.assertIn("ANTHROPIC_API_KEY", err)
        self.assertEqual(called, [])

    def test_dry_run_sends_nothing(self):
        called = []
        code, out, _ = self.run_main(lambda *a: called.append(a), {}, "--dry-run")
        self.assertEqual(code, 0)
        self.assertEqual(called, [])
        rep = self.report()
        self.assertTrue(rep["dryRun"])
        self.assertTrue(all(r["requestBytes"] > 0 for r in rep["scenarios"]))

    def test_http_error_does_not_abort(self):
        responses = iter([(429, {"error": {"message": "rate limited"}}), tool_use("genexus_read", {"name": "P"})])
        code, out, _ = self.run_main(lambda *a: next(responses), {"ANTHROPIC_API_KEY": "k"})
        self.assertEqual(code, 0)
        rep = self.report()
        self.assertEqual(rep["scenarios"][0]["status"], "error")
        self.assertIn("429", rep["scenarios"][0]["error"])
        self.assertEqual((rep["totals"]["errors"], rep["totals"]["answered"]), (1, 1))

    def test_network_error_does_not_abort(self):
        def boom(*a):
            raise OSError("down")
        code, _, _ = self.run_main(boom, {"ANTHROPIC_API_KEY": "k"}, "--scenario", "E02")
        self.assertEqual(code, 0)
        self.assertEqual(self.report()["totals"]["errors"], 1)

    def test_scenario_and_limit(self):
        self.run_main(None, {}, "--dry-run", "--scenario", "E03")
        self.assertEqual([r["id"] for r in self.report()["scenarios"]], ["E03"])
        self.run_main(None, {}, "--dry-run", "--limit", "1")
        self.assertEqual(len(self.report()["scenarios"]), 1)

    def write_profile(self, profile, names):
        tools = [t for t in TOOLS["tools"] if t["name"] in names]
        (self.d / f"tools-list.{profile}.response.json").write_text(
            json.dumps({"profile": profile, "tools": tools}), encoding="utf-8")

    def run_profile(self, transport, env, *extra):
        original = ev.DISCOVERY
        ev.DISCOVERY = self.d
        self.addCleanup(setattr, ev, "DISCOVERY", original)
        argv = ["--initialize", str(self.d / "init.json"), "--corpus", str(self.d / "corpus.json"),
                "--expectations", str(self.d / "exp.json"), "--out", str(self.d / "out.json"), *extra]
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stderr(err):
            code = ev.main(argv, transport=transport, env=env, out=out, today=lambda: "2030-01-02")
        return code, out.getvalue(), err.getvalue()

    def test_profile_uses_its_own_tool_list_and_reports_it(self):
        self.write_profile("core", {"genexus_read"})
        seen = []

        def transport(url, headers, body):
            seen.append(json.loads(body))
            return tool_use("genexus_read", {"name": "P"})

        code, out, _ = self.run_profile(transport, {"ANTHROPIC_API_KEY": "k"}, "--profile", "core")
        self.assertEqual(code, 0)
        self.assertEqual([t["name"] for t in seen[0]["tools"]], ["genexus_read"])
        rep = self.report()
        self.assertEqual((rep["profile"], rep["toolCount"], rep["harnessVersion"]), ("core", 1, ev.HARNESS_VERSION))
        self.assertIn("profile=core", out)

    def test_scenario_not_servable_under_profile_is_skipped_not_failed(self):
        self.write_profile("core", {"genexus_whoami"})  # genexus_read (expected) is not served
        called = []
        code, out, _ = self.run_profile(lambda *a: called.append(a), {"ANTHROPIC_API_KEY": "k"}, "--profile", "core")
        self.assertEqual(code, 0)
        self.assertEqual(called, [])
        rep = self.report()
        self.assertEqual({r["status"] for r in rep["scenarios"]}, {"skipped"})
        self.assertEqual((rep["plannedScenarios"], rep["totals"]["scenarios"]), (0, 0))

    def test_tools_and_profile_are_mutually_exclusive(self):
        code, _, err = self.run_main(None, {}, "--dry-run", "--profile", "core")
        self.assertEqual(code, 2)
        self.assertIn("mutually exclusive", err)

    def test_missing_profile_fixture_exits_cleanly(self):
        code, _, err = self.run_profile(None, {}, "--dry-run", "--profile", "ui")
        self.assertEqual(code, 2)
        self.assertIn("tool list not found", err)

    def test_strict_ignores_always_acceptable_call(self):
        responses = iter([tool_use("genexus_whoami", {}), tool_use("genexus_read", {"name": "P"})])
        self.run_main(lambda *a: next(responses), {"ANTHROPIC_API_KEY": "k"})
        lenient = self.report()
        self.assertEqual(lenient["totals"]["firstCallAccuracy"], 1.0)
        self.assertEqual(
            (lenient["totals"]["lenientFirstCallAccuracy"], lenient["totals"]["strictFirstCallAccuracy"]), (1.0, 0.5))

        responses = iter([tool_use("genexus_whoami", {}), tool_use("genexus_read", {"name": "P"})])
        _, out, _ = self.run_main(lambda *a: next(responses), {"ANTHROPIC_API_KEY": "k"}, "--strict")
        strict = self.report()
        self.assertTrue(strict["strict"])
        self.assertEqual(strict["totals"]["firstCallAccuracy"], 0.5)
        self.assertEqual(strict["totals"]["lenientFirstCallAccuracy"], 1.0)  # both numbers are always present
        self.assertIn("FAIL", out)

    def test_markdown_creates_replaces_and_appends_by_model_and_profile(self):
        md = self.d / "board.md"
        for model, tokens in [("m1", 100), ("m1", 300), ("m2", 50)]:
            responses = iter([tool_use("genexus_read", {"name": "P"}, tokens)] * 2)
            code, _, _ = self.run_main(lambda *a: next(responses), {"ANTHROPIC_API_KEY": "k"},
                                       "--model", model, "--markdown", str(md))
            self.assertEqual(code, 0)
        text = md.read_text(encoding="utf-8")
        rows = ev.parse_scoreboard_rows(text)
        self.assertEqual([(r[0], r[1]) for r in rows], [("m1", "all"), ("m2", "all")])
        self.assertEqual(rows[0][6], "300")  # replaced, not duplicated
        self.assertEqual(rows[0][2], "2/2")
        self.assertEqual(rows[0][3], "2/2 (100%)")
        self.assertEqual(rows[0][8], ev.HARNESS_VERSION)
        self.assertNotIn(ev.SCOREBOARD_EMPTY, text)
        self.assertNotIn("\r", text)

    def test_markdown_second_profile_adds_a_row(self):
        md = self.d / "board.md"
        self.write_profile("core", {"genexus_read"})
        self.run_profile(lambda *a: tool_use("genexus_read", {"name": "P"}), {"ANTHROPIC_API_KEY": "k"},
                         "--profile", "core", "--markdown", str(md))
        responses = iter([tool_use("genexus_read", {"name": "P"})] * 2)
        self.run_main(lambda *a: next(responses), {"ANTHROPIC_API_KEY": "k"}, "--markdown", str(md))
        text = md.read_text(encoding="utf-8")
        self.assertEqual(sorted(r[1] for r in ev.parse_scoreboard_rows(text)), ["all", "core"])
        self.assertIn("2030-01-02", text)

    def test_markdown_is_not_written_for_dry_run_or_when_nothing_answered(self):
        md = self.d / "board.md"
        self.run_main(None, {}, "--dry-run", "--markdown", str(md))
        self.assertFalse(md.exists())
        self.run_main(lambda *a: (500, {"error": {"message": "x"}}), {"ANTHROPIC_API_KEY": "k"}, "--markdown", str(md))
        self.assertFalse(md.exists())

    def test_markdown_escapes_pipes_in_model_names(self):
        md = self.d / "board.md"
        responses = iter([tool_use("genexus_read", {"name": "P"})] * 2)
        self.run_main(lambda *a: next(responses), {"ANTHROPIC_API_KEY": "k"}, "--model", "a|b", "--markdown", str(md))
        self.assertEqual(ev.parse_scoreboard_rows(md.read_text(encoding="utf-8"))[0][0], "a/b")


class RepositoryContractTests(unittest.TestCase):
    def test_committed_scoreboard_is_the_honest_empty_state_or_well_formed(self):
        text = (REPO / "docs/agent-eval-scoreboard.md").read_text(encoding="utf-8")
        rows = ev.parse_scoreboard_rows(text)
        self.assertEqual(text, ev.render_scoreboard(rows))
        if not rows:
            self.assertIn("empty", text)

    def test_every_profile_has_a_fixture_with_published_tools(self):
        all_tools = {t["name"] for t in ev.load_json(ev.DEFAULT_TOOLS)["tools"]}
        for profile in ev.PROFILES:
            tools = {t["name"] for t in ev.load_json(ev.tools_path_for_profile(profile))["tools"]}
            self.assertTrue(tools, profile)
            self.assertTrue(tools <= all_tools, profile)
            if profile != "all":
                self.assertLess(len(tools), len(all_tools), profile)

    def test_expectations_cover_or_explain_every_corpus_scenario(self):
        exp = ev.load_json(ev.DEFAULT_EXPECTATIONS)
        corpus = {s["id"] for s in ev.load_json(ev.DEFAULT_CORPUS)["scenarios"]}
        covered, omitted = {s["id"] for s in exp["scenarios"]}, set(exp["omittedScenarios"])
        self.assertEqual(covered | omitted, corpus)
        self.assertFalse(covered & omitted)
        self.assertTrue(all(reason.strip() for reason in exp["omittedScenarios"].values()))
        all_tools = {t["name"] for t in ev.load_json(ev.DEFAULT_TOOLS)["tools"]}
        for s in exp["scenarios"]:
            self.assertTrue(set(s["expectedFirstTools"]) <= all_tools, s["id"])

    def test_workflow_is_manual_only_and_does_not_write_back(self):
        text = (REPO / ".github/workflows/agent-evals.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_dispatch:", text)
        for trigger in ("push:", "pull_request", "schedule:", "workflow_run", "repository_dispatch"):
            self.assertNotIn(trigger, text)
        self.assertIn("secrets.ANTHROPIC_API_KEY", text)
        self.assertIn("permissions:\n  contents: read", text)
        for forbidden in ("git commit", "git push", "contents: write"):
            self.assertNotIn(forbidden, text)
        for line in text.splitlines():
            if "uses:" in line:
                self.assertRegex(line, r"@[0-9a-f]{40}\b")


if __name__ == "__main__":
    unittest.main()
