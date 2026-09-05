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
            raw = resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return (time.perf_counter() - t0) * 1000.0, {"__http_error__": e.code}
    elapsed = (time.perf_counter() - t0) * 1000.0
    # JSON-in-JSON: result.content[0].text holds the worker envelope
    try:
        outer = json.loads(raw)
    except Exception:
        return elapsed, None
    try:
        txt = outer["result"]["content"][0]["text"]
        inner = json.loads(txt)
    except Exception:
        inner = None
    return elapsed, inner


def percentile(samples, p):
    s = sorted(samples)
    if not s:
        return 0.0
    k = (p / 100.0) * (len(s) - 1)
    lo = int(k)
    hi = min(lo + 1, len(s) - 1)
    frac = k - lo
    return s[lo] * (1 - frac) + s[hi] * frac


def agg(name, samples, out):
    if not samples:
        print(f"  {name:24s} NO SAMPLES")
        return
    avg = statistics.mean(samples)
    p50 = percentile(samples, 50)
    p95 = percentile(samples, 95)
    p99 = percentile(samples, 99)
    print(f"  {name:24s} n={len(samples):3d} p50={p50:7.1f}ms p95={p95:7.1f}ms p99={p99:7.1f}ms avg={avg:7.1f}ms")
    out[name] = {
        "n": len(samples),
        "p50": round(p50, 2),
        "p95": round(p95, 2),
        "p99": round(p99, 2),
        "avg": round(avg, 2),
        "samples": [round(x, 2) for x in samples],
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


def envelope_is_ok(env):
    """True for a successful worker envelope. Success carries status='ok' — a
    dry-run write returns code='WriteDryRun' WITH status='ok', so a bare `code`
    field is NOT an error signal. Failures carry isError/error (or an error
    status with no ok status)."""
    if not isinstance(env, dict):
        return False
    if env.get("isError") or env.get("error"):
        return False
    st = str(env.get("status") or "").strip().lower()
    return st in ("ok", "success") or env.get("ok") is True


def print_comparison(baseline, current, max_p50_regression):
    print("\n=== COMPARISON (baseline -> current) ===")
    base_ops = baseline.get("ops", {})
    cur_ops = current.get("ops", {})
    keys = [k for k in ALL_OPS if k in base_ops and k in cur_ops]
    if not keys:
        keys = [k for k in base_ops if k in cur_ops]
    if not keys:
        print("  no overlapping ops to compare")
        return None
    print(f"  {'op':<18} {'base p50':>9} {'cur p50':>9} {'delta':>8}   "
          f"{'base p95':>9} {'cur p95':>9} {'delta':>8}")
    total = 0.0
    n = 0
    regressions = []
    for k in keys:
        b, c = base_ops[k], cur_ops[k]
        b50, c50 = b.get("p50", 0.0), c.get("p50", 0.0)
        b95, c95 = b.get("p95", 0.0), c.get("p95", 0.0)
        d50 = ((c50 - b50) / b50 * 100.0) if b50 else 0.0
        d95 = ((c95 - b95) / b95 * 100.0) if b95 else 0.0
        print(f"  {k:<18} {b50:8.2f}ms {c50:8.2f}ms {d50:+7.1f}%   "
              f"{b95:8.2f}ms {c95:8.2f}ms {d95:+7.1f}%")
        total += d50
        n += 1
        if d50 > max_p50_regression:
            regressions.append((k, d50))
    if n:
        print(f"\n  mean p50 delta: {total/n:+.1f}%")
    if regressions:
        for k, d in regressions:
            print(f"  WARNING: {k} p50 regressed {d:+.1f}% — investigate before shipping")
    else:
        print(f"  no p50 regressions > +{max_p50_regression:.1f}%")
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
                    help="Exit 1 when comparison mode finds a p50 regression above the threshold.")
    args = ap.parse_args()

    if args.max_p50_regression < 0:
        print("FATAL: --max-p50-regression must be non-negative")
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
    names = []
    if inner:
        for r in (inner.get("results") or []):
            nm = r.get("name")
            if nm:
                names.append(nm)
    if not names:
        names = ["TrnGroupProbeBase"]
    print(f"discovered {len(names)} object names for read/inspect targets")

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

    def run_op(label, build_args):
        samples = []
        for i in range(n):
            args_dict = build_args(i)
            el, _ = rpc(session_id, "tools/call", {
                "name": args_dict["name"],
                "arguments": args_dict["arguments"],
            }, timeout=120)
            samples.append(el)
        agg(label, samples, results)

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
        run_op("inspect", lambda i: {"name": "genexus_inspect", "arguments": {"kb": args.alias, "name": names[i % len(names)]}})
    if "read" in ops:
        run_op("read", lambda i: {"name": "genexus_read", "arguments": {"kb": args.alias, "name": names[i % len(names)], "part": "Source", "limit": 0}})
    if "edit_dryrun" in ops:
        if edit_args is None:
            print("  edit_dryrun SKIPPED (no edit target prepared)")
        else:
            run_op("edit_dryrun", lambda i: {"name": "genexus_edit", "arguments": dict(edit_args)})
    if "analyze" in ops:
        # mode=summary — mode=impact's caller-walk exceeds the gateway's ~50s
        # sync cap on tracked ops without a progress token, returning 'Gateway
        # timeout' envelopes while the op keeps running in the worker (and
        # serializes everything behind it). summary is the measurable,
        # still-SDK-backed analyze path.
        run_op("analyze", lambda i: {"name": "genexus_analyze", "arguments": {"kb": args.alias, "name": names[i % len(names)], "mode": "summary"}})
    if "lifecycle_status" in ops:
        run_op("lifecycle_status", lambda i: {"name": "genexus_lifecycle", "arguments": {"kb": args.alias, "action": "status"}})

    out = {"timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
           "label": label, "kb": args.kb, "iterations": n, "ops": results,
           "opsOrder": ops}
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(out, f, indent=2)
        print(f"\nWrote {args.out}")
    if args.compare:
        try:
            with open(args.compare, "r", encoding="utf-8") as f:
                baseline = json.load(f)
        except Exception as ex:
            print(f"\nWARN: could not load baseline {args.compare}: {ex}")
        else:
            regressions = print_comparison(baseline, out, args.max_p50_regression)
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
