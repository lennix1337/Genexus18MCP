import contextlib
import http.client
import importlib.util
import io
import json
import os
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
            def http_post(payload, session_id=None, timeout=180):
                return bench.HttpResponse(200, {"MCP-Session-Id": "test-session"}, b"{}")
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
                    patch.object(bench, "http_post", http_post), \
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
        for operation in ("whoami", "inspect", "read", "lifecycle_status",
                          "graph", "design_system", "pattern_diagnose"):
            with self.subTest(operation=operation):
                self.assertFalse(bench.operation_envelope_is_ok(operation, {"message": "unknown"}))

    def test_graph_design_and_pattern_success_shapes_are_validated(self):
        cases = (
            ("graph", {"nodes": [], "edges": []}),
            ("design_system", {"tokens": {}, "classes": []}),
            ("pattern_diagnose", {"findings": [], "pattern": "WorkWithPlus"}),
        )
        for operation, envelope in cases:
            with self.subTest(operation=operation):
                self.assertTrue(bench.operation_envelope_is_ok(operation, envelope))

    def test_bounded_operation_catalog_contains_lifecycle_and_extended_families(self):
        self.assertLessEqual(bench.MAX_ITERATIONS, 20)
        for operation in ("whoami", "kb_list", "list_objects", "query", "search_source",
                          "inspect", "read", "lifecycle_status", "pattern_diagnose"):
            self.assertIn(operation, bench.DEFAULT_OPS)
        for operation in ("kb_list", "kb_select", "graph", "design_system", "pattern_diagnose"):
            self.assertIn(operation, bench.ALL_OPS)

    def test_extended_operations_record_only_validated_successes(self):
        cases = {
            "kb_list": {"items": []},
            "kb_select": {"selected": "live", "selectionState": "valid"},
            "graph": {"nodes": [], "edges": []},
            "design_system": {"tokens": {}, "classes": []},
            "pattern_diagnose": {"findings": [], "pattern": "WorkWithPlus"},
        }
        for operation, envelope in cases.items():
            with self.subTest(operation=operation):
                code, report = self.run_main(envelope, operation=operation)
                self.assertEqual(0, code)
                self.assertEqual(1, report["ops"][operation]["succeeded"])
                self.assertEqual(0, report["ops"][operation]["failed"])

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

    def serving(self, body, status=200):
        """Patch the transport with a canned response body (or HTTP status)."""
        return patch.object(bench, "http_post",
                            lambda payload, session_id=None, timeout=180:
                            bench.HttpResponse(status, {}, body))

    def test_rpc_preserves_outer_error_even_with_successful_text(self):
        for outer in (
                {"error": {"code": -32603}},
                {"result": {"isError": True, "content": [{"text": '{"status":"ok"}'}]}}):
            with self.serving(json.dumps(outer).encode()):
                _, envelope = bench.rpc("s", "tools/call", {})
            self.assertFalse(bench.envelope_is_ok(envelope))

    def test_rpc_accepts_structured_content(self):
        with self.serving(b'{"result":{"structuredContent":{"status":"ok"}}}'):
            measurement = bench.rpc("s", "tools/call", {})
            _, envelope = measurement
        self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertGreater(measurement.response_bytes, 0)

    def test_rpc_parses_text_content_when_structured_content_is_omitted(self):
        body = json.dumps({"result": {"content": [{"type": "text", "text": '{"status":"ok"}'}]}}).encode()
        with self.serving(body):
            measurement = bench.rpc("s", "tools/call", {})
            _, envelope = measurement
        self.assertEqual({"status": "ok"}, envelope)
        self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertEqual(0, measurement.structured_content_bytes)

    def test_rpc_reports_an_http_error_status_as_an_envelope(self):
        with self.serving(b"backend unavailable", status=503):
            measurement = bench.rpc("s", "tools/call", {})
            _, envelope = measurement
        self.assertEqual({"__http_error__": 503}, envelope)
        self.assertFalse(bench.envelope_is_ok(envelope))
        self.assertEqual(503, measurement.status_code)

    class FakeConnection:
        """Stands in for http.client.HTTPConnection so pooling is observable."""

        created = []
        fail_first_request = False

        def __init__(self, host, port, timeout=None):
            type(self).created.append((host, port))
            self.sock = None
            self.timeout = timeout
            self.requests = []

        def request(self, method, path, body=None, headers=None):
            if type(self).fail_first_request and len(type(self).created) == 1:
                raise http.client.RemoteDisconnected("server closed the idle socket")
            self.requests.append((method, path, headers))

        def getresponse(self):
            return type(self).FakeResponse()

        def close(self):
            pass

        class FakeResponse:
            status = 200
            headers = {"MCP-Session-Id": "pooled"}

            @staticmethod
            def read():
                # A full JSON-RPC envelope, exactly like the gateway's HTTP response.
                inner = '{"status":"ok","connected":true,"kb":{}}'
                return json.dumps({"jsonrpc": "2.0", "id": 1,
                                   "result": {"content": [{"type": "text", "text": inner}]}}).encode()

    def use_fake_connections(self, fail_first=False):
        self.FakeConnection.created = []
        self.FakeConnection.fail_first_request = fail_first
        bench._close_connection()
        self.addCleanup(bench._close_connection)
        return patch.object(bench.http.client, "HTTPConnection", self.FakeConnection)

    def test_rpc_reuses_one_persistent_connection(self):
        with self.use_fake_connections():
            for _ in range(3):
                _, envelope = bench.rpc("s", "tools/call", {})
                self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertEqual(1, len(self.FakeConnection.created),
                        "a connection per call re-introduces the select() timer tax")

    def test_rpc_reconnects_once_after_a_dropped_connection(self):
        with self.use_fake_connections(fail_first=True):
            _, envelope = bench.rpc("s", "tools/call", {})
        self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertEqual(2, len(self.FakeConnection.created))

    def test_rpc_surfaces_a_persistent_transport_failure(self):
        class AlwaysFailing(self.FakeConnection):
            def request(self, method, path, body=None, headers=None):
                raise OSError("connection refused")

        AlwaysFailing.created = []
        bench._close_connection()
        self.addCleanup(bench._close_connection)
        with patch.object(bench.http.client, "HTTPConnection", AlwaysFailing):
            with self.assertRaises(OSError):
                bench.rpc("s", "tools/call", {})
        self.assertEqual(2, len(AlwaysFailing.created),
                         "a failed retry must surface the error instead of looping")

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

    def test_matrix_axes_are_part_of_the_compared_population(self):
        # A 1-KB baseline must not be comparable against a 3-KB current run. Without
        # the matrix axes in the population, a comparison would report a confident
        # delta between two workloads that are not the same workload.
        population = {
            "fixtureId": "fixture-r1", "fixtureRevision": "seed-1",
            "generator": "net", "cacheMode": "warm", "concurrency": 1,
            "iterations": 1, "ops": ["whoami"], "kbs": 1, "clients": 1,
        }
        stats = {"n": 1, "p50": 5, "p95": 5, "failed": 0, "skipped": 0,
                 "responseBytes": {"n": 1, "p50": 10, "p95": 10}}
        valid = {"population": population, "ops": {"whoami": stats}}
        current = {"population": dict(population, kbs=3),
                   "ops": {"whoami": stats}}
        self.assertIsNone(bench.print_comparison(valid, current, 25, 25, 25))
        current["population"] = dict(population, clients=2)
        self.assertIsNone(bench.print_comparison(valid, current, 25, 25, 25))
        current["population"] = dict(population)
        self.assertEqual([], bench.print_comparison(valid, current, 25, 25, 25))


class ScaleMatrixTests(unittest.TestCase):
    """Issue #358: the matrix layer, exercised without a gateway or a KB.

    These are pure functions on purpose. The native lane is optional because it
    cannot run everywhere, and behavior that is only observable when a GeneXus
    installation and a live KB happen to be present is behavior that quietly stops
    being checked.
    """

    def test_kb_specs_parse_path_and_alias(self):
        specs = bench.parse_kb_specs("C:/KBs/KBTeste=alpha,C:/KBs/KBTeste16=beta")
        self.assertEqual([{"path": "C:/KBs/KBTeste", "alias": "alpha"},
                          {"path": "C:/KBs/KBTeste16", "alias": "beta"}], specs)

    def test_kb_specs_tolerate_a_windows_drive_colon(self):
        # The reason the separator is '=' rather than ':': every Windows KB path
        # begins with a drive letter, so ':' would make the alias ambiguous.
        specs = bench.parse_kb_specs("C:/KBs/KBTeste=alpha")
        self.assertEqual("C:/KBs/KBTeste", specs[0]["path"])
        self.assertEqual("alpha", specs[0]["alias"])

    def test_kb_specs_default_aliases_when_omitted(self):
        specs = bench.parse_kb_specs("C:/KBs/A,C:/KBs/B")
        self.assertEqual(["kb1", "kb2"], [s["alias"] for s in specs])

    def test_malformed_kb_specs_are_rejected_rather_than_dropped(self):
        # Silently dropping one would turn a declared 3-KB matrix into a 1-KB run
        # that still reports as a pass.
        for spec in ("C:/KBs/A=", "=alpha", "C:/KBs/A=x,C:/KBs/A=y"):
            with self.subTest(spec=spec):
                with self.assertRaises(ValueError):
                    bench.parse_kb_specs(spec)

    def test_alias_collisions_are_rejected_case_insensitively(self):
        with self.assertRaises(ValueError):
            bench.parse_kb_specs("C:/KBs/A=live,C:/KBs/B=LIVE")

    def test_a_missing_kb_path_is_unavailable_not_available(self):
        # Asserted through a path that provably does not exist, so the check cannot
        # degrade into "available" without this failing. A random-looking name is not
        # enough: it has to be absent, not merely unusual.
        with tempfile.TemporaryDirectory() as parent:
            missing = str(Path(parent) / "absent-kb-directory")
            self.assertFalse(os.path.exists(missing))
            state = bench.classify_kb_availability({"path": missing})
        self.assertEqual("unavailable", state["state"])
        self.assertEqual("path_not_found", state["reason"])

    def test_a_spec_without_a_path_is_unavailable(self):
        self.assertEqual("unavailable",
                         bench.classify_kb_availability({})["state"])
        self.assertEqual("unavailable",
                         bench.classify_kb_availability({"path": ""})["state"])
        self.assertEqual("no_path",
                         bench.classify_kb_availability({"path": ""})["reason"])

    def test_an_unavailable_kb_makes_every_cell_that_needs_it_unavailable(self):
        # The property the previous test only checked in isolation: that availability
        # actually reaches the grid. Without this wiring, a missing KB would leave the
        # cell looking runnable, which is the whole failure the matrix guards.
        with tempfile.TemporaryDirectory() as parent:
            present = Path(parent) / "present"
            present.mkdir()
            specs = [{"path": str(present), "alias": "a"},
                     {"path": str(Path(parent) / "absent"), "alias": "b"},
                     {"path": str(Path(parent) / "absent2"), "alias": "c"}]
            cells = bench.apply_kb_availability(bench.matrix_cells(specs), specs)

        states = {c["cellId"]: c for c in cells}
        self.assertEqual("declared", states["kbs1-clients1"]["state"],
                         "the one-KB cell uses only the present KB")
        self.assertEqual("unavailable", states["kbs3-clients1"]["state"])
        self.assertEqual("unavailable", states["kbs3-clients2"]["state"])
        # The reason names the alias and the cause, so a red cell is actionable
        # without re-deriving which of three KBs was at fault.
        self.assertIn("b:path_not_found", states["kbs3-clients1"]["reason"])
        self.assertIn("c:path_not_found", states["kbs3-clients1"]["reason"])

    def test_availability_reasoning_never_marks_a_cell_available(self):
        # Only "declared" or "unavailable" may come out of apply_kb_availability.
        # A cell that answered "available" here would be claiming a result it never
        # measured.
        with tempfile.TemporaryDirectory() as parent:
            present = Path(parent) / "p"
            present.mkdir()
            for specs in ([], [{"path": str(present), "alias": "a"}],
                          [{"path": str(Path(parent) / "x"), "alias": "b"}]):
                for cell in bench.apply_kb_availability(
                        bench.matrix_cells(specs), specs):
                    self.assertIn(cell["state"], ("declared", "unavailable"),
                                  f"unexpected state {cell['state']}")

    def test_a_file_is_not_a_kb(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "kb.txt"
            target.write_text("not a KB")
            state = bench.classify_kb_availability({"path": str(target)})
        self.assertEqual("unavailable", state["state"])
        self.assertEqual("path_not_a_directory", state["reason"])

    def test_an_existing_directory_is_available(self):
        with tempfile.TemporaryDirectory() as directory:
            state = bench.classify_kb_availability({"path": directory})
        self.assertEqual("available", state["state"])

    def test_cells_cover_the_requested_grid(self):
        specs = bench.parse_kb_specs("C:/a=one,C:/b=two,C:/c=three")
        cells = bench.matrix_cells(specs)
        self.assertEqual(
            ["kbs1-clients1", "kbs1-clients2", "kbs3-clients1", "kbs3-clients2"],
            [c["cellId"] for c in cells])
        three = next(c for c in cells if c["cellId"] == "kbs3-clients1")
        self.assertEqual(["one", "two", "three"], three["kbs"])

    def test_a_cell_needing_more_kbs_than_declared_is_unavailable(self):
        # Emitted rather than omitted: a missing cell is indistinguishable from a
        # grid that silently narrowed itself.
        cells = bench.matrix_cells(bench.parse_kb_specs("C:/a=one"))
        wide = next(c for c in cells if c["kbCount"] == 3)
        self.assertEqual("unavailable", wide["state"])
        self.assertIn("1", wide["reason"])

    def test_unavailable_cells_never_summarize_as_a_pass(self):
        # The issue's rule that missing SDK fixtures are not passes.
        cells = [{"state": "pass"}, {"state": "unavailable"}]
        summary = bench.summarize_cells(cells)
        self.assertEqual("unavailable", summary["outcome"])
        self.assertEqual(1, summary["pass"])

    def test_unavailable_outranks_fail(self):
        # A grid whose missing fixtures were reported as failures would train a
        # reader to ignore red.
        summary = bench.summarize_cells([{"state": "fail"}, {"state": "unavailable"}])
        self.assertEqual("unavailable", summary["outcome"])

    def test_declared_cells_are_not_counted_as_results(self):
        summary = bench.summarize_cells([{"state": "pass"}, {"state": "declared"}])
        self.assertEqual("pass", summary["outcome"])
        self.assertEqual(1, summary["pass"])

    def test_a_matrix_with_nothing_executed_is_unavailable(self):
        # An empty pass is the failure mode a three-valued outcome exists to prevent.
        summary = bench.summarize_cells([{"state": "declared"}])
        self.assertEqual("unavailable", summary["outcome"])
        self.assertEqual("no_executed_cells", summary["reason"])

    def test_no_cells_at_all_is_unavailable(self):
        self.assertEqual("unavailable", bench.summarize_cells([])["outcome"])
        self.assertEqual("unavailable", bench.summarize_cells(None)["outcome"])

    def test_a_failing_cell_summarizes_as_fail(self):
        summary = bench.summarize_cells([{"state": "pass"}, {"state": "fail"}])
        self.assertEqual("fail", summary["outcome"])
        self.assertEqual(1, summary["fail"])

    def test_matrix_run_reports_unavailable_when_a_declared_kb_is_missing(self):
        code, report = self.run_matrix_main("C:/definitely/not/here=alpha")
        self.assertEqual(2, code)
        self.assertEqual("unavailable", report["matrix"]["summary"]["outcome"])

    def test_matrix_run_declares_both_axes_even_when_one_cell_runs(self):
        with tempfile.TemporaryDirectory() as directory:
            # Three real directories, so the 3-KB cells are genuinely runnable and the
            # assertion below is about which cell this run executed rather than about
            # the fixtures being absent.
            for name in ("a", "b", "c"):
                (Path(directory) / name).mkdir()
            kbs = ",".join(f"{Path(directory) / name}={name}" for name in ("a", "b", "c"))
            code, report = self.run_matrix_main(kbs)
        self.assertIn(code, (0, 1))

        states = {c["cellId"]: c for c in report["matrix"]["cells"]}
        # Both axes are declared: the grid is 1 and 3 KBs by 1 and 2 clients.
        self.assertIn("kbs1-clients1", states)
        self.assertIn("kbs3-clients2", states)
        # The 1/1 cell is the one this run actually opened and measured.
        self.assertIn(states["kbs1-clients1"]["state"], ("pass", "fail"))
        self.assertEqual("declared", states["kbs3-clients1"]["state"])
        self.assertEqual(3, report["population"]["kbs"])
        self.assertEqual(1, report["population"]["clients"])

    def test_the_matrix_axes_are_recorded_in_the_report_population(self):
        # Without this, a comparison could pair a 1-KB baseline with a 3-KB current
        # run and report a confident delta between different workloads.
        with tempfile.TemporaryDirectory() as directory:
            code, report = self.run_matrix_main(f"{directory}=live")
        self.assertIn(code, (0, 1, 2))
        self.assertIn("kbs", report["population"])
        self.assertIn("clients", report["population"])

    def test_a_matrix_run_that_cannot_establish_a_client_is_not_a_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "matrix.json"

            def http_post(payload, session_id=None, timeout=180):
                # First client initializes; the second is refused, as a gateway that
                # cannot take another session would.
                if session_id is None and bench._INIT_COUNT.get("n", 0) >= 1:
                    return bench.HttpResponse(503, {}, b"")
                bench._INIT_COUNT["n"] = bench._INIT_COUNT.get("n", 0) + 1
                return bench.HttpResponse(200, {"MCP-Session-Id": "s1"}, b"{}")

            bench._INIT_COUNT = {}
            calls = {"n": 0}

            def rpc(session, method, params, **kwargs):
                calls["n"] += 1
                if calls["n"] == 1:
                    return 1, None
                if calls["n"] == 2:
                    return 1, {"status": "ok"}
                if calls["n"] == 3:
                    return 1, {"status": "ok", "index": {"status": "Ready"}}
                if calls["n"] == 4:
                    return 1, {"status": "ok", "results": [{"name": "Probe"}]}
                return 10, {"status": "ok", "results": []}

            argv = ["bench", "--ops", "list_objects", "--iterations", "1",
                    "--matrix", "--kbs", f"{directory}=live",
                    "--clients", "2", "--out", str(output)]
            with patch.object(bench.sys, "argv", argv), \
                    patch.object(bench, "rpc", rpc), \
                    patch.object(bench, "http_post", http_post), \
                    patch.object(bench.time, "sleep"), \
                    contextlib.redirect_stdout(io.StringIO()):
                code = bench.main()

            self.assertEqual(2, code)
            report = json.loads(output.read_text())
            states = {c["cellId"]: c for c in report["matrix"]["cells"]}
            self.assertEqual("unavailable", states["kbs1-clients2"]["state"])
            self.assertEqual("unavailable", report["matrix"]["summary"]["outcome"])

    def run_matrix_main(self, kbs):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "matrix.json"

            def http_post(payload, session_id=None, timeout=180):
                return bench.HttpResponse(200, {"MCP-Session-Id": "s1"}, b"{}")

            calls = {"n": 0}

            def rpc(session, method, params, **kwargs):
                calls["n"] += 1
                if calls["n"] == 1:
                    return 1, None
                if calls["n"] == 2:
                    return 1, {"status": "ok"}
                if calls["n"] == 3:
                    return 1, {"status": "ok", "index": {"status": "Ready"}}
                if calls["n"] == 4:
                    return 1, {"status": "ok", "results": [{"name": "Probe"}]}
                return 10, {"status": "ok", "results": []}

            argv = ["bench", "--ops", "list_objects", "--iterations", "1",
                    "--matrix", "--out", str(output)]
            if kbs:
                argv += ["--kbs", kbs]
            with patch.object(bench.sys, "argv", argv), \
                    patch.object(bench, "rpc", rpc), \
                    patch.object(bench, "http_post", http_post), \
                    patch.object(bench.time, "sleep"), \
                    contextlib.redirect_stdout(io.StringIO()):
                code = bench.main()
            report = json.loads(output.read_text()) if output.exists() else None
        return code, report

    def test_a_malformed_matrix_is_rejected_before_anything_opens(self):
        with contextlib.redirect_stdout(io.StringIO()):
            code = self.run_failing_main(["--kbs", "C:/KBs/A=", "--matrix"])
        self.assertEqual(2, code)

    def test_non_positive_clients_are_rejected(self):
        with contextlib.redirect_stdout(io.StringIO()):
            code = self.run_failing_main(["--clients", "0", "--matrix"])
        self.assertEqual(2, code)

    def run_failing_main(self, extra):
        def http_post(payload, session_id=None, timeout=180):
            raise AssertionError("the harness must not reach the transport")

        argv = ["bench", "--ops", "whoami", "--iterations", "1"] + extra
        with patch.object(bench.sys, "argv", argv), \
                patch.object(bench, "http_post", http_post):
            return bench.main()

    @staticmethod
    def measure_under_load(read_envelope):
        """Drive measure_read_under_load with a stubbed transport.

        measure_read_under_load calls the module-level rpc, so the stub is installed
        for the duration rather than passed in: without it the harness reaches a real
        socket, which is exactly the optional-native-lane dependency these tests exist
        to avoid.
        """
        def rpc(session, method, params, timeout=180):
            if params.get("name") == "genexus_search_source":
                return 5, {"status": "ok", "result": {"hits": []}}
            return 7, read_envelope

        with patch.object(bench, "rpc", rpc), \
                contextlib.redirect_stdout(io.StringIO()):
            return bench.measure_read_under_load(
                "s1", "s2", "live", [{"name": "T", "type": "Transaction"}], {}, 3)

    def test_read_under_load_rejects_an_unusable_envelope(self):
        # A busy/gateway-timeout envelope must not become a latency sample, exactly
        # as for any other measured op.
        result = self.measure_under_load({"status": "error"})
        self.assertEqual(0, result["n"])

    def test_read_under_load_reports_load_and_read_separately(self):
        result = self.measure_under_load({"part": "Source", "source": "parm;"})
        self.assertEqual(3, result["n"])
        self.assertEqual(3, result["loadIssued"])
        self.assertEqual([7, 7, 7], result["samples"])
        self.assertEqual(7, result["p50"])

    def test_read_under_load_with_no_targets_is_unavailable(self):
        result = bench.measure_read_under_load("s1", "s2", "live", [], {}, 3)
        self.assertEqual(0, result["n"])


if __name__ == "__main__":
    unittest.main()
