import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "bench", Path(__file__).resolve().parents[1] / "bench-live-http.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class BenchmarkGateTests(unittest.TestCase):
    def run_main(self, measured, extra=None, operation="whoami"):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "result.json"
            response = unittest.mock.MagicMock()
            response.__enter__.return_value.headers.get.return_value = "test-session"
            calls = 0

            def rpc(session, method, params, **kwargs):
                nonlocal calls
                calls += 1
                if calls == 1:
                    return 1, None  # initialized notification
                if calls == 2:
                    return 1, {"status": "ok"}
                if calls == 3:
                    return 1, {"status": "ok", "index": {"status": "Ready"}}
                if calls == 4:
                    return 1, {"status": "ok", "results": [{"name": "Probe"}]}
                return 10, measured

            argv = ["bench", "--ops", operation, "--iterations", "1", "--out", str(output)]
            with patch.object(bench.sys, "argv", argv + (extra or [])), \
                    patch.object(bench, "rpc", rpc), \
                    patch.object(bench.urllib.request, "urlopen", return_value=response), \
                    patch.object(bench.time, "sleep"), contextlib.redirect_stdout(io.StringIO()):
                result = bench.main()
            return result, json.loads(output.read_text()) if output.exists() else None

    def test_errors_cannot_be_successful_latency_samples(self):
        for envelope in (None, {}, {"error": {"code": -32603}},
                         {"isError": True, "status": "ok"}, {"status": "error"}):
            with self.subTest(envelope=envelope):
                code, report = self.run_main(envelope)
                self.assertNotEqual(0, code)
                self.assertEqual(0, report["ops"]["whoami"]["n"])
                self.assertEqual(1, report["ops"]["whoami"]["failed"])

    def test_success_counts_and_samples(self):
        code, report = self.run_main({"connected": True, "kb": {}})
        self.assertEqual(0, code)
        self.assertEqual(1, report["ops"]["whoami"]["succeeded"])
        self.assertEqual([10], report["ops"]["whoami"]["samples"])
        self.assertEqual(0, report["ops"]["whoami"]["responseBytes"]["n"])
        self.assertIn("population", report)

    def test_empty_collection_is_a_valid_result(self):
        code, report = self.run_main({"status": "ok", "results": []}, operation="list_objects")
        self.assertEqual(0, code)
        self.assertEqual(1, report["ops"]["list_objects"]["succeeded"])

    def test_statusless_gateway_shapes_are_valid_for_live_read_operations(self):
        cases = (
            ("whoami", {"connected": True, "kb": {"name": "KBTeste"}}),
            ("list_objects", {"results": []}),
            ("query", {"results": []}),
            ("search_source", {"status": "ok", "result": {"hits": []}}),
            ("inspect", {"name": "Probe", "type": "Procedure"}),
            ("read", {"part": "Source", "source": "parm;"}),
            ("lifecycle_status", {"Status": "Ready", "Phase": "idle"}),
        )
        for operation, envelope in cases:
            with self.subTest(operation=operation):
                self.assertTrue(bench.operation_envelope_is_ok(operation, envelope))

    def test_statusless_success_without_requested_shape_is_rejected(self):
        for operation in ("whoami", "inspect", "read", "lifecycle_status"):
            with self.subTest(operation=operation):
                self.assertFalse(bench.operation_envelope_is_ok(operation, {"message": "unknown"}))

    def test_error_status_with_success_shape_is_rejected(self):
        self.assertFalse(bench.operation_envelope_is_ok(
            "list_objects", {"status": "InvalidArgs", "results": []}))

    def test_read_target_selection_prefers_source_backed_types(self):
        targets = bench.select_read_targets([
            {"name": "Root", "type": "Folder"},
            {"name": "Client", "type": "Module"},
            {"name": "Customer", "type": "Transaction"},
            {"name": "Customer", "type": "Table"},
        ])
        self.assertEqual([{"name": "Customer", "type": "Transaction"}], targets)

    def test_success_without_requested_collection_is_a_failure(self):
        code, report = self.run_main({"status": "ok"}, operation="query")
        self.assertEqual(1, code)
        self.assertEqual(1, report["ops"]["query"]["failed"])

    def test_missing_baseline_fails_gate(self):
        code, _ = self.run_main({"status": "ok"}, ["--compare", "missing-baseline.json", "--fail-on-regression"])
        self.assertNotEqual(0, code)

    def test_missing_operation_invalidates_comparison(self):
        stats = {"n": 1, "p50": 10, "p95": 10}
        with contextlib.redirect_stdout(io.StringIO()):
            result = bench.print_comparison(
                {"ops": {"whoami": stats, "read": stats}},
                {"ops": {"whoami": stats}}, 25)
        self.assertIsNone(result)

    def test_rpc_preserves_outer_error_even_with_successful_text(self):
        response = unittest.mock.MagicMock()
        for outer in (
                {"error": {"code": -32603}},
                {"result": {"isError": True, "content": [{"text": '{"status":"ok"}'}]}}):
            response.__enter__.return_value.read.return_value = json.dumps(outer).encode()
            with patch.object(bench.urllib.request, "urlopen", return_value=response):
                _, envelope = bench.rpc("s", "tools/call", {})
            self.assertFalse(bench.envelope_is_ok(envelope))

    def test_rpc_accepts_structured_content(self):
        response = unittest.mock.MagicMock()
        response.__enter__.return_value.read.return_value = b'{"result":{"structuredContent":{"status":"ok"}}}'
        with patch.object(bench.urllib.request, "urlopen", return_value=response):
            measurement = bench.rpc("s", "tools/call", {})
            _, envelope = measurement
        self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertGreater(measurement.response_bytes, 0)

    def test_skipped_dry_run_fails(self):
        code, report = self.run_main({"status": "error"}, operation="edit_dryrun")
        self.assertEqual(1, code)
        self.assertEqual(1, report["ops"]["edit_dryrun"]["skipped"])

    def test_invalid_baseline_metrics_fail(self):
        for stats in ({}, {"n": 0, "p50": 1, "p95": 1},
                      {"n": 1, "p50": float("nan"), "p95": 1},
                      {"n": 1, "p50": 1, "p95": 1, "failed": 1}):
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertIsNone(bench.print_comparison(
                    {"ops": {"whoami": stats}}, {"ops": {"whoami": stats}}, 25))

    def test_tail_latency_regression_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            baseline = Path(directory) / "baseline.json"
            baseline.write_text(json.dumps({"ops": {"whoami": {"n": 1, "p50": 5, "p95": 5}}}))
            code, _ = self.run_main({"connected": True, "kb": {}},
                                    ["--compare", str(baseline), "--fail-on-regression", "--max-p50-regression", "200"])
        # The baseline intentionally predates the population/byte contract, so
        # fail-on-regression rejects it as an invalid comparison (exit 2).
        self.assertEqual(2, code)

    def test_comparison_requires_matching_population_and_payload_metrics(self):
        population = {
            "fixtureId": "fixture-r1",
            "fixtureRevision": "seed-1",
            "kbAlias": "live",
            "kbPath": "C:/fixture",
            "generator": "net",
            "cacheMode": "warm",
            "concurrency": 1,
            "iterations": 2,
            "ops": ["whoami"],
        }
        valid = {
            "population": population,
            "ops": {
                "whoami": {
                    "n": 2, "p50": 10, "p95": 12,
                    "responseBytes": {"n": 2, "p50": 100, "p95": 110},
                    "failed": 0, "skipped": 0,
                }
            },
        }
        current = {
            "population": dict(population),
            "ops": {
                "whoami": {
                    "n": 2, "p50": 11, "p95": 13,
                    "responseBytes": {"n": 2, "p50": 101, "p95": 112},
                    "failed": 0, "skipped": 0,
                }
            },
        }
        self.assertEqual([], bench.print_comparison(valid, current, 25, 25, 25))
        current["population"]["cacheMode"] = "cold"
        self.assertIsNone(bench.print_comparison(valid, current, 25, 25, 25))


if __name__ == "__main__":
    unittest.main()
