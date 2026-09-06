#!/usr/bin/env python3
"""Run a credential-free dual-transport MCP conformance smoke.

The script exercises the built Gateway over legacy session HTTP, sessionless
2026 HTTP/SSE and stdio. It does not mutate a KB: only discovery and unknown
task handles are used. A real KB path is kept in the generated config so the
same process startup path used by live validation is covered.
"""

from __future__ import annotations

import http.client
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PUBLISH = ROOT / "publish"
KB = Path(os.environ.get("GXMCP_TEST_KB", r"C:\kbs\KBTeste"))


def assert_true(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def wait_port(port: int, process: subprocess.Popen, timeout: float = 30.0) -> None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"gateway exited with code {process.returncode}")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.5):
                return
        except OSError:
            time.sleep(0.15)
    raise TimeoutError(f"gateway did not listen on {port}")


def start_gateway(directory: Path, port: int, stdio: bool) -> tuple[subprocess.Popen, object, object]:
    config = {
        "GeneXus": {
            "InstallationPath": r"C:\Program Files (x86)\GeneXus\GeneXus18",
            "WorkerExecutable": str(PUBLISH / "worker" / "GxMcp.Worker.exe"),
        },
        "Environment": {
            "DefaultKb": "live-fixture",
            "KBs": [{"Alias": "live-fixture", "Path": str(KB)}],
        },
        "Server": {
            "HttpPort": port,
            "McpStdio": stdio,
            "BindAddress": "127.0.0.1",
            "SessionIdleTimeoutMinutes": 10,
            "WorkerIdleTimeoutMinutes": 5,
        },
    }
    config_path = directory / ("stdio-config.json" if stdio else "http-config.json")
    config_path.write_text(json.dumps(config), encoding="utf-8")
    stdout = open(directory / ("stdio.stdout" if stdio else "http.stdout"), "wb")
    stderr = open(directory / ("stdio.stderr" if stdio else "http.stderr"), "wb")
    env = os.environ.copy()
    env.update(
        {
            "GX_CONFIG_PATH": str(config_path),
            "GX_MCP_PORT": str(port),
            "GX_MCP_STDIO": "true" if stdio else "false",
            "GX_PATH": r"C:\Program Files (x86)\GeneXus\GeneXus18",
        }
    )
    process = subprocess.Popen(
        [str(PUBLISH / "GxMcp.Gateway.exe")],
        cwd=str(PUBLISH),
        env=env,
        stdin=subprocess.PIPE if stdio else subprocess.DEVNULL,
        stdout=subprocess.PIPE if stdio else stdout,
        stderr=stderr,
    )
    return process, stdout, stderr


def stop_process(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return
    try:
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=10,
            check=False,
        )
    except Exception:
        process.kill()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()


def post_http(port: int, body: dict, headers: dict[str, str], timeout: float = 20.0) -> tuple[int, dict, dict[str, str]]:
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/mcp",
        data=json.dumps(body).encode("utf-8"),
        headers=headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read().decode("utf-8", errors="replace")
            try:
                payload = json.loads(raw) if raw else {}
            except json.JSONDecodeError:
                payload = {"_raw": raw}
            return response.status, payload, dict(response.headers)
    except urllib.error.HTTPError as error:
        raw = error.read().decode("utf-8", errors="replace")
        try:
            payload = json.loads(raw) if raw else {}
        except json.JSONDecodeError:
            payload = {"_raw": raw}
        return error.code, payload, dict(error.headers)


def legacy_headers(session: str | None = None, origin: str | None = None, host: str | None = None) -> dict[str, str]:
    result = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": "2025-11-25",
    }
    if session:
        result["MCP-Session-Id"] = session
    if origin:
        result["Origin"] = origin
    if host:
        result["Host"] = host
    return result


def modern_headers(
    method: str,
    name: str | None = None,
    client_id: str | None = None,
) -> dict[str, str]:
    result = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": "2026-07-28",
        "Mcp-Method": method,
    }
    if name is not None:
        result["Mcp-Name"] = name
    if client_id is not None:
        result["Mcp-Client-Id"] = client_id
    return result


def modern_meta() -> dict:
    return {
        "io.modelcontextprotocol/protocolVersion": "2026-07-28",
        "io.modelcontextprotocol/clientCapabilities": {},
    }


def exercise_http(port: int) -> dict:
    status, initialize, response_headers = post_http(
        port,
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "wire-conformance", "version": "3.0"}}},
        legacy_headers(origin="http://127.0.0.1"),
    )
    assert_true(status == 200, f"legacy initialize status={status}")
    session = response_headers.get("MCP-Session-Id")
    assert_true(bool(session), "legacy initialize did not establish a session")

    post_http(port, {"jsonrpc": "2.0", "method": "notifications/initialized"}, legacy_headers(session))
    status, listing, _ = post_http(port, {"jsonrpc": "2.0", "id": 2, "method": "tools/list"}, legacy_headers(session))
    assert_true(status == 200 and isinstance(listing.get("result", {}).get("tools"), list), "legacy tools/list failed")

    # Loopback origin is accepted; a hostile Host is rejected before dispatch.
    status, _, _ = post_http(port, {"jsonrpc": "2.0", "id": 3, "method": "tools/list"}, legacy_headers(session, host="evil.test"))
    assert_true(status == 403, f"DNS-rebinding Host was not rejected: {status}")

    modern_list = {
        "jsonrpc": "2.0",
        "id": "modern-list",
        "method": "tools/list",
        "params": {"_meta": modern_meta()},
    }
    status, modern_result, _ = post_http(port, modern_list, modern_headers("tools/list"))
    assert_true(status == 200 and isinstance(modern_result.get("result", {}).get("tools"), list), "modern tools/list failed")

    task_request = {
        "jsonrpc": "2.0",
        "id": 4,
        "method": "tasks/get",
        "params": {
            "taskId": "wire-missing-task",
            "_meta": {
                **modern_meta(),
                "io.modelcontextprotocol/clientCapabilities": {
                    "extensions": {"io.modelcontextprotocol/tasks": {}}
                },
            },
        },
    }
    status, task_result, _ = post_http(port, task_request, modern_headers("tasks/get", "wire-missing-task"))
    assert_true(
        status == 200
        and task_result.get("error", {}).get("code") == -32023
        and task_result.get("error", {}).get("data", {}).get("requiredHeader") == "Mcp-Client-Id",
        "modern tasks/get without client scope did not fail closed",
    )
    status, task_result, _ = post_http(
        port,
        task_request,
        modern_headers("tasks/get", "wire-missing-task", client_id="wire-client"),
    )
    assert_true(
        status == 200 and task_result.get("error", {}).get("code") == -32602,
        "modern tasks/get with client scope did not preserve task validation",
    )

    def open_subscription(subscription_id: str) -> tuple[http.client.HTTPConnection, object]:
        body = {
            "jsonrpc": "2.0",
            "id": subscription_id,
            "method": "subscriptions/listen",
            "params": {"notifications": {"toolsListChanged": True}, "_meta": modern_meta()},
        }
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
        connection.request(
            "POST",
            "/mcp",
            json.dumps(body),
            headers=modern_headers("subscriptions/listen"),
        )
        stream = connection.getresponse()
        assert_true(stream.status == 200, f"subscriptions/listen status={stream.status}")
        lines: list[str] = []
        for _ in range(32):
            line = stream.readline()
            if not line:
                break
            lines.append(line.decode("utf-8", errors="replace"))
            if "notifications/subscriptions/acknowledged" in "".join(lines):
                break
        chunk = "".join(lines)
        assert_true(
            "notifications/subscriptions/acknowledged" in chunk,
            "subscription acknowledgement missing",
        )
        return connection, stream

    # A modern subscription is a real SSE response. Read only the acknowledgement
    # and deliberately leave the stream unconsumed while another request runs;
    # a slow subscriber must not hold the HTTP request loop hostage. Close it and
    # reconnect with a new transport handle to prove cleanup is client-owned.
    conn, _ = open_subscription("subscription-1")
    status, _, _ = post_http(
        port,
        {
            "jsonrpc": "2.0",
            "id": "slow-consumer-probe",
            "method": "tools/list",
            "params": {"_meta": modern_meta()},
        },
        modern_headers("tools/list"),
        timeout=5.0,
    )
    assert_true(status == 200, f"request behind slow subscription failed: {status}")
    conn.close()

    reconnect, _ = open_subscription("subscription-2")
    reconnect.close()

    # Unknown protocol versions are rejected before a session can be created.
    bad_headers = legacy_headers()
    bad_headers["MCP-Protocol-Version"] = "2099-01-01"
    status, _, _ = post_http(port, {"jsonrpc": "2.0", "id": 5, "method": "tools/list"}, bad_headers)
    assert_true(status == 400, f"unknown protocol version accepted: {status}")
    return {
        "legacySession": True,
        "modernDiscovery": True,
        "tasksFailClosed": True,
        "subscriptionAck": True,
        "subscriptionReconnect": True,
        "slowConsumerRequest": True,
        "hostOriginGuard": True,
    }


def read_json_lines(stream, expected_ids: set[object], timeout: float = 15.0) -> dict[str, dict]:
    results: dict[str, dict] = {}
    deadline = time.time() + timeout
    while time.time() < deadline and len(results) < len(expected_ids):
        line = stream.readline()
        if not line:
            break
        try:
            value = json.loads(line.decode("utf-8"))
        except json.JSONDecodeError:
            continue
        if "id" in value and value["id"] in expected_ids:
            results[json.dumps(value["id"], sort_keys=True)] = value
    return results


def exercise_stdio(directory: Path) -> dict:
    process, stdout, stderr = start_gateway(directory, 0, True)
    try:
        assert process.stdin is not None and process.stdout is not None
        process.stdin.write((json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "stdio-wire", "version": "3.0"}}}) + "\n").encode())
        process.stdin.write((json.dumps({"jsonrpc": "2.0", "id": "1", "method": "tools/list"}) + "\n").encode())
        process.stdin.flush()
        responses = read_json_lines(process.stdout, {1, "1"})
        assert_true(responses.get("1", {}).get("result", {}).get("protocolVersion") == "2025-11-25", "stdio numeric initialize did not return")
        assert_true(responses.get('"1"', {}).get("result", {}).get("tools") is not None, "stdio string tools/list did not return")
        assert_true(responses.get('"1"', {}).get("id") == "1", "stdio string request id was not preserved")
        assert_true(responses.get('"1"', {}).get("jsonrpc") == "2.0", "stdio response is not JSON-RPC")
        return {"stdioConcurrentIds": True, "stdioIdType": True}
    finally:
        stop_process(process)
        stdout.close()
        stderr.close()


def main() -> int:
    if not (PUBLISH / "GxMcp.Gateway.exe").is_file():
        print("mcp-wire: publish/GxMcp.Gateway.exe is missing", file=sys.stderr)
        return 2
    with tempfile.TemporaryDirectory(prefix="gxmcp-wire-") as temp:
        directory = Path(temp)
        port = 55000 + (os.getpid() % 1000)
        process, stdout, stderr = start_gateway(directory, port, False)
        try:
            wait_port(port, process)
            result = {"http": exercise_http(port)}
        finally:
            stop_process(process)
            stdout.close()
            stderr.close()
        result["stdio"] = exercise_stdio(directory)
        print(json.dumps({"status": "pass", "checks": result}, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"mcp-wire: fail: {error}", file=sys.stderr)
        raise SystemExit(1)
