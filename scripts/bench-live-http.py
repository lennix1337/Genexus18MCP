#!/usr/bin/env python3
"""Live HTTP benchmark harness for the Genexus18MCP gateway.

Drives the Streamable-HTTP MCP endpoint (default http://127.0.0.1:5000/mcp),
opens the configured KB, waits for the index to be Ready, then measures
round-trip latency for the discovery/read/write-dryrun ops an LLM calls
repeatedly.

Usage:
  python scripts/bench-live-http.py [--kb C:/KBs/KBTeste] [--alias live]
      [--iterations 12] [--port 5000] [--out bench-live.json] [--name baseline]
      [--ops whoami,query,edit_dryrun]      # subset of the op catalog
      [--compare baseline.json]             # print delta table vs a prior --out
      [--fail-on-regression]                # exit 1 when p50 exceeds the threshold

Ops (all read-only / dry-run — nothing persists on the KB):
  whoami, list_objects, query, search_source, inspect, read,
  edit_dryrun   (genexus_edit mode=full dryRun on a small Transaction source —
                 append-marker write through the read->write->project->diff
                 pipeline, no persist),
  analyze       (genexus_analyze mode=summary — mode=impact's full caller-walk
                 exceeds the gateway's ~50s sync cap for tracked ops without a
                 progress token (SafeLongPollSecondsWithoutProgress) and is not
                 latency-measurable),
  lifecycle_status (genexus_lifecycle action=status — index/build health)

Latency hygiene: measure on a freshly restarted gateway. An op that exceeds the
~50s cap returns a 'Gateway timeout' envelope while STILL RUNNING in the STA
worker, serializing every later call behind it (every op then times out at 50s).
If a run shows uniform ~50s timeouts, restart the gateway and re-run.

Comparison mode: run with --compare <baseline.json> (the --out file of an
earlier run) and the harness prints a per-op p50/p95 delta table plus a
mean-delta and a >+25% p50 regression warning. Typical workflow:
  run 1: python scripts/bench-live-http.py --out .gx-smoke-futures/bench-base.json
  run 2 (after optimizing): python scripts/bench-live-http.py \
      --compare .gx-smoke-futures/bench-base.json --out .gx-smoke-futures/bench-new.json
"""
import argparse
from dataclasses import dataclass
import json
import statistics
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:5000/mcp"

# Op catalog order is the run order. `edit_dryrun` is prepared lazily (needs a
# real identifier from the target's source); the rest are static shapes.
ALL_OPS = [
    "whoami",
    "list_objects",
    "query",
    "search_source",
    "inspect",
    "read",
    "edit_dryrun",
    "analyze",
    "lifecycle_status",
]

# Folders, modules and physical tables are valid list/query results but do not
# reliably expose a Source part through genexus_read. Prefer object families
# with source-backed parts when selecting the shared inspect/read target set.
_SOURCE_BACKED_TYPES = frozenset({
    "procedure", "transaction", "webpanel", "panel", "sdpanel",
    "workpanel", "dataprovider", "businesscomponent", "externalobject",
    "structureddata", "structureddatatype", "structured datatype",
})


@dataclass(frozen=True)
class RpcMeasurement:
    """A wire measurement that remains backward-compatible with ``(elapsed, envelope)``.

    The benchmark historically returned a two-item tuple from ``rpc`` and a few
    local probes/tests unpack that shape.  Keeping ``__iter__`` two-item while
    exposing response_bytes lets the gate measure payload size without making
    those callers silently start ignoring a third value.
    """

    elapsed_ms: float
    envelope: object
    response_bytes: int = 0
    status_code: object = None

    def __iter__(self):
        yield self.elapsed_ms
        yield self.envelope


def rpc(session_id, method, params, timeout=180, is_notification=False):
    req_body = {"jsonrpc": "2.0", "method": method, "params": params}
    if not is_notification:
        req_body["id"] = 1
    body = json.dumps(req_body).encode()
    req = urllib.request.Request(BASE, data=body, method="POST",
                                 headers={
                                     "Accept": "application/json, text/event-stream",
                                     "Content-Type": "application/json",
                                 })
    if session_id:
        req.add_header("MCP-Session-Id", session_id)
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw_bytes = resp.read()
            raw = raw_bytes.decode("utf-8", errors="replace")
            status_code = getattr(resp, "status", None)
    except urllib.error.HTTPError as e:
        try:
            error_bytes = e.read()
        except Exception:
            error_bytes = b""
        return RpcMeasurement(
            (time.perf_counter() - t0) * 1000.0,
            {"__http_error__": e.code},
            len(error_bytes),
            e.code,
        )
    elapsed = (time.perf_counter() - t0) * 1000.0
    response_bytes = len(raw_bytes)
    # JSON-in-JSON: result.content[0].text holds the worker envelope
    try:
        outer = json.loads(raw)
    except Exception:
        return RpcMeasurement(elapsed, None, response_bytes, status_code)
    if not isinstance(outer, dict) or outer.get("error"):
        return RpcMeasurement(elapsed, {"isError": True}, response_bytes, status_code)
    result = outer.get("result")
    if not isinstance(result, dict) or result.get("isError"):
        return RpcMeasurement(elapsed, {"isError": True}, response_bytes, status_code)
    if isinstance(result.get("structuredContent"), dict):
        return RpcMeasurement(elapsed, result["structuredContent"], response_bytes, status_code)
    try:
        txt = outer["result"]["content"][0]["text"]
        inner = json.loads(txt)
    except Exception:
        inner = None
    return RpcMeasurement(elapsed, inner, response_bytes, status_code)


def percentile(samples, p):
    s = sorted(samples)
    if not s:
        return 0.0
    k = (p / 100.0) * (len(s) - 1)
    lo = int(k)
    hi = min(lo + 1, len(s) - 1)
    frac = k - lo
    return s[lo] * (1 - frac) + s[hi] * frac


def agg(name, samples, out, byte_samples=None):
    if not samples:
        print(f"  {name:24s} NO SAMPLES")
        out[name] = {"n": 0, "samples": [], "responseBytes": {"n": 0, "samples": []}}
        return
    avg = statistics.mean(samples)
    p50 = percentile(samples, 50)
    p95 = percentile(samples, 95)
    p99 = percentile(samples, 99)
    byte_samples = [int(x) for x in (byte_samples or []) if isinstance(x, (int, float)) and x >= 0]
    byte_stats = {"n": len(byte_samples), "samples": byte_samples}
    if byte_samples:
        byte_stats.update({
            "p50": round(percentile(byte_samples, 50), 2),
            "p95": round(percentile(byte_samples, 95), 2),
            "avg": round(statistics.mean(byte_samples), 2),
        })
    print(f"  {name:24s} n={len(samples):3d} p50={p50:7.1f}ms p95={p95:7.1f}ms p99={p99:7.1f}ms avg={avg:7.1f}ms"
          + (f" bytes-p50={byte_stats['p50']:.0f} bytes-p95={byte_stats['p95']:.0f}" if byte_samples else ""))
    out[name] = {
        "n": len(samples),
        "p50": round(p50, 2),
        "p95": round(p95, 2),
        "p99": round(p99, 2),
        "avg": round(avg, 2),
        "samples": [round(x, 2) for x in samples],
        "responseBytes": byte_stats,
    }


def read_content_text(env):
    """Extract source text from a genexus_read envelope (content/lines/source/
    text priority). Needed for short Transaction sources the recursive dig
    (>=40-char strings) misses."""
    if not isinstance(env, dict):
        return None
    for key in ("content", "lines", "source", "text"):
        v = env.get(key)
        if isinstance(v, list):
            return "\n".join(str(x) for x in v[:40])
        if isinstance(v, str):
            return v
    return None


_ERROR_STATUS_PREFIXES = ("error", "fail", "invalid", "notfound", "notimplemented")


def _case_insensitive_value(env, key):
    for name, value in env.items():
        if str(name).lower() == key.lower():
            return value
    return None


def select_read_targets(items):
    """Return source-backed ``{name, type}`` targets from list_objects data.

    The first page of a KB commonly contains folders/modules. Measuring
    ``genexus_read`` against those entries would count deterministic
    ``SourcePartNotFound`` errors as latency samples and fail the live gate.
    If a fixture exposes no recognized source family, retain the original
    names as a diagnostic fallback so the gate still fails closed.
    """
    entries = []
    for item in items if isinstance(items, list) else []:
        if not isinstance(item, dict):
            continue
        name = item.get("name")
        if not isinstance(name, str) or not name.strip():
            continue
        object_type = item.get("type")
        entries.append({"name": name, "type": object_type} if object_type else {"name": name})
    preferred = [
        entry for entry in entries
        if _is_source_backed_type(entry.get("type"))
    ]
    return preferred or entries


def _is_source_backed_type(object_type):
    return str(object_type or "").strip().lower() in _SOURCE_BACKED_TYPES


def prepare_read_targets(session_id, alias, candidates, max_targets=30):
    """Probe candidates once and retain only readable Source-backed objects."""
    valid = []
    seen = set()
    for entry in candidates if isinstance(candidates, list) else []:
        key = (entry.get("name"), entry.get("type"))
        if key in seen:
            continue
        seen.add(key)
        try:
            measurement = rpc(session_id, "tools/call", {
                "name": "genexus_read",
                "arguments": {"kb": alias, **entry, "part": "Source", "limit": 0},
            }, timeout=120)
            _, envelope = measurement
        except (OSError, TimeoutError):
            continue
        if operation_envelope_is_ok("read", envelope):
            valid.append(entry)
            if len(valid) >= max_targets:
                break
    return valid


def _has_error_signal(env):
    if env.get("isError") or env.get("error"):
        return True
    status = str(_case_insensitive_value(env, "status") or "").strip().lower()
    if status and status.startswith(_ERROR_STATUS_PREFIXES):
        return True
    code = str(env.get("code") or "").strip().lower()
    return bool(code and code.startswith(_ERROR_STATUS_PREFIXES))


def envelope_is_ok(env):
    """True for an envelope with an explicit successful status.

    Some tools expose a typed status (for example ``search_source``), while
    others expose a statusless success shape (for example ``genexus_read``).
    Callers measuring a concrete operation must use
    :func:`operation_envelope_is_ok`, which validates that operation's shape.
    This helper remains strict for generic callers such as dry-run target
    discovery.
    """
    if not isinstance(env, dict) or not env or _has_error_signal(env):
        return False
    status = str(_case_insensitive_value(env, "status") or "").strip().lower()
    return status in ("ok", "success") or env.get("ok") is True


def operation_envelope_is_ok(operation, env):
    """Validate the minimum shape needed for a latency sample.

    The MCP wire contract deliberately allows successful statusless envelopes.
    A successful response without the requested collection is still a protocol
    failure, not a fast empty result. Empty ``results``/``items`` arrays remain
    valid because they are legitimate answers.
    """
    if not isinstance(env, dict) or not env or _has_error_signal(env):
        return False
    if operation in ("list_objects", "query"):
        return isinstance(env.get("results"), list) or isinstance(env.get("items"), list)
    if operation == "search_source":
        if isinstance(env.get("results"), list) or isinstance(env.get("items"), list):
            return True
        nested = env.get("result")
        return isinstance(nested, dict) and isinstance(nested.get("hits"), list)
    if operation == "whoami":
        return env.get("connected") is True and isinstance(env.get("kb"), dict)
    if operation == "inspect":
        return any(key in env for key in ("name", "identity", "summary", "availableParts", "type"))
    if operation == "read":
        return any(key in env for key in ("source", "content", "parts", "part", "versionToken", "isEmpty"))
    if operation == "lifecycle_status":
        return any(key in env for key in ("status", "Status", "Phase", "TaskId", "summary", "compact"))
    return envelope_is_ok(env)


def _population_matches(baseline, current):
    """Require an explicit, equivalent population for regression gating."""
    base_population = baseline.get("population")
    current_population = current.get("population")
    if not isinstance(base_population, dict) or not isinstance(current_population, dict):
        return False
    required = ("fixtureId", "fixtureRevision", "generator", "cacheMode",
                "concurrency", "iterations", "ops")
    for population in (base_population, current_population):
        if any(key not in population for key in required):
            return False
        if any(isinstance(population[key], str) and not population[key].strip()
               for key in ("fixtureId", "fixtureRevision", "generator", "cacheMode")):
            return False
        if not isinstance(population["concurrency"], int) or population["concurrency"] < 1:
            return False
        if not isinstance(population["iterations"], int) or population["iterations"] < 1:
            return False
        if not isinstance(population["ops"], list) or not population["ops"]:
            return False
    # Timestamp/label are intentionally excluded.  All other fields describe
    # the fixture, SDK/model, cache state, concurrency and requested sample set.
    return base_population == current_population


def print_comparison(baseline, current, max_p50_regression, max_p95_regression=25.0,
                     max_bytes_regression=25.0):
    print("\n=== COMPARISON (baseline -> current) ===")
    if not isinstance(baseline, dict):
        return None
    if not _population_matches(baseline, current):
        print("  benchmark populations differ or are missing explicit metadata")
        return None
    base_ops = baseline.get("ops", {})
    cur_ops = current.get("ops", {})
    if not isinstance(base_ops, dict) or set(base_ops) != set(cur_ops):
        print("  operation sets differ; comparison invalid")
        return None
    for stats in list(base_ops.values()) + list(cur_ops.values()):
        if not isinstance(stats, dict) or stats.get("n", 0) <= 0 or stats.get("failed", 0) or stats.get("skipped", 0):
            return None
        if any(not isinstance(stats.get(k), (int, float)) or not 0 < stats[k] < float("inf") for k in ("p50", "p95")):
            return None
        bytes_stats = stats.get("responseBytes")
        if (not isinstance(bytes_stats, dict) or bytes_stats.get("n", 0) <= 0
                or any(not isinstance(bytes_stats.get(k), (int, float)) or bytes_stats[k] < 0
                       for k in ("p50", "p95"))):
            return None
    keys = [k for k in ALL_OPS if k in base_ops and k in cur_ops]
    if not keys:
        keys = [k for k in base_ops if k in cur_ops]
    if not keys:
        print("  no overlapping ops to compare")
        return None
    print(f"  {'op':<18} {'base p50':>9} {'cur p50':>9} {'delta':>8}   "
          f"{'base p95':>9} {'cur p95':>9} {'delta':>8}   {'bytes delta':>11}")
    total = 0.0
    n = 0
    regressions = []
    for k in keys:
        b, c = base_ops[k], cur_ops[k]
        b50, c50 = b.get("p50", 0.0), c.get("p50", 0.0)
        b95, c95 = b.get("p95", 0.0), c.get("p95", 0.0)
        bb95 = b["responseBytes"].get("p95", 0.0)
        cb95 = c["responseBytes"].get("p95", 0.0)
        d50 = ((c50 - b50) / b50 * 100.0) if b50 else 0.0
        d95 = ((c95 - b95) / b95 * 100.0) if b95 else 0.0
        dbytes = ((cb95 - bb95) / bb95 * 100.0) if bb95 else 0.0
        print(f"  {k:<18} {b50:8.2f}ms {c50:8.2f}ms {d50:+7.1f}%   "
              f"{b95:8.2f}ms {c95:8.2f}ms {d95:+7.1f}%   {dbytes:+7.1f}%")
        total += d50
        n += 1
        if d50 > max_p50_regression:
            regressions.append((k, d50))
        if d95 > max_p95_regression:
            regressions.append((k + " p95", d95))
        if dbytes > max_bytes_regression:
            regressions.append((k + " responseBytes p95", dbytes))
    if n:
        print(f"\n  mean p50 delta: {total/n:+.1f}%")
    if regressions:
        for k, d in regressions:
            print(f"  WARNING: {k} regressed {d:+.1f}% — investigate before shipping")
    else:
        print(f"  no p50/p95/responseBytes regressions above +{max_p50_regression:.1f}%/"
              f"+{max_p95_regression:.1f}%/+{max_bytes_regression:.1f}%")
    return regressions


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--kb", default="C:/KBs/KBTeste")
    ap.add_argument("--alias", default="live")
    ap.add_argument("--iterations", type=int, default=12)
    ap.add_argument("--port", type=int, default=5000)
    ap.add_argument("--out", default=None)
    ap.add_argument("--name", default=None)
    ap.add_argument("--ops", default=None,
                    help="Comma-separated subset of the op catalog. Default: all ops.")
    ap.add_argument("--compare", default=None,
                    help="Path to a prior --out JSON; prints a p50/p95 delta table vs this run.")
    ap.add_argument("--max-p50-regression", type=float, default=25.0,
                    help="p50 regression percentage that is considered a failure (default: 25).")
    ap.add_argument("--fail-on-regression", action="store_true",
                    help="Exit 1 when comparison mode finds a p50 or p95 regression above its threshold.")
    ap.add_argument("--max-p95-regression", type=float, default=25.0,
                    help="Maximum p95 regression percentage (default: 25).")
    ap.add_argument("--max-bytes-regression", type=float, default=25.0,
                    help="Maximum response-byte p95 growth percentage (default: 25).")
    ap.add_argument("--fixture-id", default=None,
                    help="Stable synthetic fixture identity for population matching.")
    ap.add_argument("--fixture-revision", default=None,
                    help="Fixture seed/revision for population matching.")
    ap.add_argument("--generator", default=None,
                    help="GeneXus generator/SDK identity for population matching.")
    ap.add_argument("--cache-mode", choices=("cold", "warm", "mixed"), default="warm",
                    help="Cache state represented by this run (default: warm).")
    ap.add_argument("--concurrency", type=int, default=1,
                    help="Logical client concurrency represented by this run.")
    args = ap.parse_args()

    if args.iterations < 1 or (args.fail_on_regression and not args.compare):
        print("FATAL: positive iterations and a baseline for regression gating are required")
        return 2

    if args.concurrency < 1:
        print("FATAL: --concurrency must be positive")
        return 2

    if not all(0 <= threshold < float("inf") for threshold in (args.max_p50_regression,
                                                                  args.max_p95_regression,
                                                                  args.max_bytes_regression)):
        print("FATAL: regression thresholds must be finite and non-negative")
        return 2

    ops = [o.strip() for o in (args.ops or "").split(",") if o.strip()] if args.ops else list(ALL_OPS)
    unknown = [o for o in ops if o not in ALL_OPS]
    if unknown:
        print(f"FATAL: unknown op(s) {unknown}; catalog: {ALL_OPS}")
        sys.exit(2)

    global BASE
    BASE = f"http://127.0.0.1:{args.port}/mcp"

    # Handshake — single initialize, capture session id from response headers.
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                       "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                                  "clientInfo": {"name": "bench-live-http", "version": "1.0"}}}).encode()
    req = urllib.request.Request(BASE, data=body, method="POST",
                                 headers={"Accept": "application/json, text/event-stream",
                                          "Content-Type": "application/json"})
    t0 = time.perf_counter()
    session_id = None
    with urllib.request.urlopen(req, timeout=30) as resp:
        session_id = resp.headers.get("MCP-Session-Id")
        resp.read()
    el = (time.perf_counter() - t0) * 1000.0
    if not session_id:
        print("FATAL: no MCP-Session-Id in initialize response")
        sys.exit(2)
    print(f"initialize: {el:.0f}ms session: {session_id}")

    rpc(session_id, "notifications/initialized", {}, is_notification=True)
    time.sleep(1)

    # Open KB
    el, inner = rpc(session_id, "tools/call", {
        "name": "genexus_kb",
        "arguments": {"action": "open", "path": args.kb, "alias": args.alias},
    }, timeout=240)
    if isinstance(inner, dict) and "__http_error__" in inner:
        print(f"open KB: HTTP {inner['__http_error__']}")
        sys.exit(2)
    status = (inner or {}).get("status", "?")
    print(f"open KB {args.kb}: {el:.0f}ms status={status}")

    # Wait for index Ready (poll whoami)
    print("waiting for index Ready...", flush=True)
    ready = False
    for _ in range(48):
        el, inner = rpc(session_id, "tools/call", {
            "name": "genexus_whoami",
            "arguments": {"kb": args.alias},
        }, timeout=60)
        if inner:
            idx = inner.get("index") or {}
            st = idx.get("status", "?")
            tot = idx.get("totalObjects", 0)
            print(f"  index status={st} total={tot}", flush=True)
            if st in ("Ready", "LiteReady", "Enriching"):
                ready = True
                break
        time.sleep(4)
    if not ready:
        print("WARN: index not Ready; benchmarking anyway (numbers include cold path)")

    time.sleep(3)

    # Discover real object names for inspect/read targets
    el, inner = rpc(session_id, "tools/call", {
        "name": "genexus_list_objects",
        "arguments": {"kb": args.alias, "limit": 30},
    }, timeout=120)
    listed_items = (inner or {}).get("results") if inner else []
    target_entries = select_read_targets(listed_items)
    if not any(_is_source_backed_type(entry.get("type")) for entry in target_entries):
        # The first page is often entirely folders/modules. Ask the index for
        # source-backed families before falling back to those non-readable rows.
        target_entries = []
        for type_filter in ("Procedure", "Transaction", "WebPanel", "DataProvider", "SDPanel"):
            _, typed_inner = rpc(session_id, "tools/call", {
                "name": "genexus_list_objects",
                "arguments": {"kb": args.alias, "typeFilter": type_filter, "limit": 30},
            }, timeout=120)
            typed = select_read_targets((typed_inner or {}).get("results") if typed_inner else [])
            typed = [entry for entry in typed if _is_source_backed_type(entry.get("type"))]
            if typed:
                target_entries.extend(typed)
                break
    if not target_entries:
        target_entries = [{"name": "TrnGroupProbeBase", "type": "Transaction"}]
    target_entries = prepare_read_targets(session_id, args.alias, target_entries)
    if not target_entries:
        # Keep the failure visible to the gate when a fixture has no readable
        # Source part; never silently convert an empty target population into
        # successful latency samples.
        target_entries = [{"name": "TrnGroupProbeBase", "type": "Transaction"}]
    names = [entry["name"] for entry in target_entries]
    print(f"discovered {len(target_entries)} source-backed object names for read/inspect targets")

    # edit_dryrun needs a real identifier from the target's source so the
    # patch `find` actually matches (a miss would short-circuit to an error
    # path and under-measure the write pipeline). Target a small Transaction:
    # a patch on a big WebForm source exceeds the gateway's ~50s sync cap and
    # leaves a long op running in the STA worker (poisoning every later op).
    # Some Transactions (atomic-created probes) have an EMPTY Source part, so
    # iterate candidates until one yields an identifier.
    edit_args = None
    if "edit_dryrun" in ops:
        # Target small Transactions: a write on a big WebForm source exceeds the
        # gateway's ~50s sync cap and leaves a long op running in the STA worker
        # (poisoning every later op). Some Transactions (atomic-created probes)
        # have an EMPTY Source part, and some objects fail the write-path read
        # even when genexus_read works (the gateway auto-injects type="Table"
        # for a Transaction, resolving to the table object, which exposes no
        # Source). Iterate candidates: read the source, then VERIFY a dryRun
        # write actually succeeds before pinning it.
        edit_candidates = []
        for type_filter in ("Transaction", "Procedure"):
            el, inner = rpc(session_id, "tools/call", {
                "name": "genexus_list_objects",
                "arguments": {"kb": args.alias, "typeFilter": type_filter, "limit": 10},
            }, timeout=120)
            for r in (inner or {}).get("results") or []:
                nm = r.get("name")
                if nm:
                    edit_candidates.append((nm, type_filter))
            if edit_candidates:
                break
        edit_candidates = edit_candidates or [(n, None) for n in names]
        for cand, cand_type in edit_candidates:
            el, inner = rpc(session_id, "tools/call", {
                "name": "genexus_read",
                "arguments": {"kb": args.alias, "name": cand, "part": "Source", "limit": 0},
            }, timeout=120)
            # Never build content from a non-source envelope.
            if not isinstance(inner, dict) or inner.get("isError") or inner.get("error") \
                    or "__http_error__" in inner or "__raw__" in inner:
                continue
            src_text = read_content_text(inner)
            if not src_text or not src_text.strip():
                continue  # empty Source part (atomic-created probe)
            candidate_args = {
                "kb": args.alias,
                "name": cand,
                "part": "Source",
                "mode": "full",
                # Real change (append a marker comment line): exercises the full
                # read -> write -> project -> diff pipeline. mode=full needs no
                # byte-exact context matching (mode=patch NoMatches when the read
                # view differs from the patch view). dryRun: nothing persists.
                "content": src_text.rstrip() + "\n// gxbench-dryrun",
                "dryRun": True,
            }
            if cand_type:
                # Explicit type: without it the gateway auto-injects type="Table"
                # for a Transaction, which resolves to the table object (no Source
                # part) and fails the write read.
                candidate_args["type"] = cand_type
            # Verify the dryRun write succeeds on this object before pinning it —
            # a candidate whose write path fails would measure error envelopes.
            el, inner = rpc(session_id, "tools/call", {
                "name": "genexus_edit",
                "arguments": candidate_args,
            }, timeout=180)
            if envelope_is_ok(inner):
                edit_args = candidate_args
                print(f"edit_dryrun target: {cand} (mode=full +marker, dryRun verified, no persist)")
                break
        if not edit_args:
            print("WARN: no edit target with a verifiable dryRun write; skipping edit_dryrun")

    n = args.iterations
    results = {}
    label = args.name or "baseline"
    population = {
        "fixtureId": args.fixture_id or "",
        "fixtureRevision": args.fixture_revision or "",
        "kbAlias": args.alias,
        "kbPath": args.kb,
        "generator": args.generator or "",
        "cacheMode": args.cache_mode,
        "concurrency": args.concurrency,
        "iterations": n,
        "ops": list(ops),
    }

    def run_op(label, build_args):
        samples = []
        byte_samples = []
        failed = 0
        for i in range(n):
            args_dict = build_args(i)
            try:
                measurement = rpc(session_id, "tools/call", {
                    "name": args_dict["name"],
                    "arguments": args_dict["arguments"],
                }, timeout=120)
            except (OSError, TimeoutError):
                failed += 1
                continue
            el, envelope = measurement
            if operation_envelope_is_ok(label, envelope):
                samples.append(el)
                response_bytes = getattr(measurement, "response_bytes", 0)
                # A patched/local probe that still returns the historical
                # two-item tuple has no wire-size metadata.  Do not turn that
                # absence into a misleading zero-byte sample; successful HTTP
                # responses always have a non-empty JSON body.
                if isinstance(response_bytes, (int, float)) and response_bytes > 0:
                    byte_samples.append(response_bytes)
            else:
                failed += 1
        agg(label, samples, results, byte_samples)
        results[label].update(attempted=n, succeeded=len(samples), failed=failed, skipped=0)

    if "whoami" in ops:
        run_op("whoami", lambda i: {"name": "genexus_whoami", "arguments": {"kb": args.alias}})
    if "list_objects" in ops:
        run_op("list_objects", lambda i: {"name": "genexus_list_objects", "arguments": {"kb": args.alias, "limit": 10}})
    if "query" in ops:
        run_op("query", lambda i: {"name": "genexus_query", "arguments": {"kb": args.alias, "query": "Trn", "limit": 10}})
    if "search_source" in ops:
        # NOTE: genexus_search_source takes `pattern`/`callee` — NOT `query` (that
        # arg yields the MissingCriteria error path, ~15ms flat, and the harness
        # previously measured that instead of the real search).
        run_op("search_source", lambda i: {"name": "genexus_search_source", "arguments": {"kb": args.alias, "pattern": "parm", "maxResults": 10}})
    if "inspect" in ops:
        run_op("inspect", lambda i: {"name": "genexus_inspect", "arguments": {
            "kb": args.alias, **target_entries[i % len(target_entries)]
        }})
    if "read" in ops:
        run_op("read", lambda i: {"name": "genexus_read", "arguments": {
            "kb": args.alias, **target_entries[i % len(target_entries)], "part": "Source", "limit": 0
        }})
    if "edit_dryrun" in ops:
        if edit_args is None:
            print("  edit_dryrun SKIPPED (no edit target prepared)")
            results["edit_dryrun"] = {"n": 0, "samples": [], "attempted": 0,
                                      "succeeded": 0, "failed": 0, "skipped": n,
                                      "responseBytes": {"n": 0, "samples": []}}
        else:
            run_op("edit_dryrun", lambda i: {"name": "genexus_edit", "arguments": dict(edit_args)})
    if "analyze" in ops:
        # mode=summary — mode=impact's caller-walk exceeds the gateway's ~50s
        # sync cap on tracked ops without a progress token, returning 'Gateway
        # timeout' envelopes while the op keeps running in the worker (and
        # serializes everything behind it). summary is the measurable,
        # still-SDK-backed analyze path.
        run_op("analyze", lambda i: {"name": "genexus_analyze", "arguments": {
            "kb": args.alias, **target_entries[i % len(target_entries)], "mode": "summary"
        }})
    if "lifecycle_status" in ops:
        run_op("lifecycle_status", lambda i: {"name": "genexus_lifecycle", "arguments": {"kb": args.alias, "action": "status"}})

    out = {"timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
           "label": label, "kb": args.kb, "iterations": n, "ops": results,
           "opsOrder": ops, "population": population}
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(out, f, indent=2)
        print(f"\nWrote {args.out}")
    if set(results) != set(ops) or any(r["failed"] or r["skipped"] for r in results.values()):
        print("FATAL: requested operations failed or were skipped")
        return 1
    if args.compare:
        try:
            with open(args.compare, "r", encoding="utf-8") as f:
                baseline = json.load(f)
        except Exception as ex:
            print(f"\nWARN: could not load baseline {args.compare}: {ex}")
            if args.fail_on_regression:
                return 2
        else:
            regressions = print_comparison(baseline, out, args.max_p50_regression,
                                           args.max_p95_regression, args.max_bytes_regression)
            if args.fail_on_regression:
                if regressions is None:
                    print("FATAL: performance comparison has no overlapping operations")
                    return 2
                if regressions:
                    return 1
    print("\n=== DONE ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
