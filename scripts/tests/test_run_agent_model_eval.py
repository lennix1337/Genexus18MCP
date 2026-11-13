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


if __name__ == "__main__":
    unittest.main()
