#!/usr/bin/env python3
"""A stateful fake of the Gateway's Streamable HTTP MCP endpoint.

It implements just enough of genexus_workwith (list_filters, add_filter_attribute,
remove_filter_attribute, list_actions, add_action, remove_action), genexus_kb,
genexus_whoami and genexus_worker_reload to drive scripts/workwith_persistence_smoke.ps1
without a Knowledge Base. It models a live definition, what survives a Worker restart and a
request log, so test-workwith-persistence-smoke-behavior.ps1 can assert what the smoke wrote,
removed and restarted in each scenario. It is a test double: it proves the control flow of the
script, not the behavior of the real tool.

Usage: fake-workwith-mcp-server.py            (binds 127.0.0.1 on a free port, prints PORT=<n>)
Control: POST /_control {"scenario": "...", "existing": "Name"} resets the state; GET /_state
returns {"log": [...], "live": {...}, "persisted": {...}, "reloads": n}.
"""
import copy
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STATE = None


def norm(name):
    text = (name or "").strip()
    return text[1:].strip() if text.startswith("&") else text


def attribute(name):
    return {"name": name, "description": "", "domain": "", "default": "", "variableName": "&" + name}


class State:
    def __init__(self, scenario="success", existing=""):
        self.scenario = scenario
        self.pid = 1000
        self.reloads = 0
        self.log = []
        self.after_second_reload = False
        live = {
            "level": "Level1",
            "attributes": [attribute("CustomerName"), attribute("CustomerCity")],
            "conditions": ["CustomerName like &CustomerName when not &CustomerName.IsEmpty();"],
            "actions": [],
        }
        if existing:
            live["attributes"].append(attribute(existing))
        self.live = live
        self.persisted = copy.deepcopy(live)

    # What a write changes. Some scenarios keep the change out of the persisted copy.
    def write(self, mutate, persists=True):
        mutate(self.live)
        if persists:
            mutate(self.persisted)

    def observed_source(self):
        if self.scenario == "reader_changes" and self.reloads >= 2:
            return "xml_projection"
        return "in_memory_instance_reread"


def ok(code, result=None):
    payload = {"status": "ok", "code": code}
    if result is not None:
        payload["result"] = result
    return payload


def error(code, message):
    return {"status": "error", "error": {"code": code, "message": message}}


def filters_view(definition):
    return {
        "level": definition["level"],
        "attributes": copy.deepcopy(definition["attributes"]),
        "conditions": list(definition["conditions"]),
    }


def handle_workwith(args):
    state = STATE
    action = args.get("action")
    state.log.append({"tool": "genexus_workwith", "action": action, "args": args})
    live = state.live
    if action == "list_filters":
        return ok("WorkWithFilters", {"observedSource": state.observed_source(), "filters": filters_view(live)})
    if action == "add_filter_attribute":
        name = args["attribute"]
        if any(norm(a["name"]).lower() == norm(name).lower() for a in live["attributes"]):
            return error("FilterAttributeAlreadyExists", "exists")
        conditions = list(args.get("conditions") or [])
        persists = state.scenario != "persist_lost"

        def mutate(definition):
            definition["attributes"].append(attribute(norm(name)))
            definition["conditions"].extend(conditions)

        state.write(mutate, persists=persists)
        return ok("WorkWithFilterAdded", {
            "verified": True,
            "verificationScope": "fake",
            "persistenceEvidence": {"independentPersistedReread": False, "provesDurability": False},
        })
    if action == "remove_filter_attribute":
        name = norm(args["attribute"])
        match = [a for a in live["attributes"] if norm(a["name"]).lower() == name.lower()]
        if not match:
            return error("FilterAttributeNotFound", "not on the filter")
        listed = list(args.get("removeConditions") or [])
        for text in listed:
            if text not in live["conditions"]:
                return error("FilterConditionNotFound", text)
        remaining = [c for c in live["conditions"] if c not in listed]
        if state.scenario == "remove_refused" or any(("&" + name) in c for c in remaining):
            return error("FilterAttributeReferencedByConditions", "still referenced")
        persists = state.scenario != "cleanup_not_persisted"

        def mutate(definition):
            definition["attributes"] = [a for a in definition["attributes"] if norm(a["name"]).lower() != name.lower()]
            definition["conditions"] = [c for c in definition["conditions"] if c not in listed]

        state.write(mutate, persists=persists)
        verified = state.scenario != "unverified_receipt"
        code = "WorkWithFilterAttributeRemoved" if verified else "WorkWithFilterAttributeRemovedUnverified"
        return ok(code, {"verified": verified, "persistenceEvidence": {"provesDurability": False}})
    if action == "list_actions":
        return ok("WorkWithActions", {"observedSource": "in_memory_instance_reread",
                                      "tabs": [{"code": "General", "name": "General", "actions": copy.deepcopy(live["actions"])}]})
    if action == "add_action":
        name = args["actionName"]

        def mutate(definition):
            definition["actions"].append({"name": name})

        state.write(mutate)
        return ok("WorkWithActionAdded", {"verified": True, "persistenceEvidence": {"provesDurability": False}})
    if action == "remove_action":
        name = args["actionName"]

        def mutate(definition):
            definition["actions"] = [a for a in definition["actions"] if a["name"] != name]

        state.write(mutate)
        return ok("WorkWithActionRemoved", {"persistenceEvidence": {"provesDurability": False}})
    return error("UnknownAction", str(action))


def handle_tool(name, args):
    state = STATE
    if name == "genexus_kb":
        state.log.append({"tool": name, "action": args.get("action")})
        return ok("KbOpened")
    if name == "genexus_whoami":
        state.log.append({"tool": name})
        return {"status": "ok", "worker": {"pid": state.pid}}
    if name == "genexus_worker_reload":
        state.log.append({"tool": name, "mode": args.get("mode")})
        if state.scenario == "reload_fails" and state.reloads == 0:
            state.reloads += 1
            return error("WorkerReloadFailed", "the replacement Worker did not start")
        state.reloads += 1
        state.pid += 1
        # A restart keeps only what was persisted.
        state.live = copy.deepcopy(state.persisted)
        return {"status": "Reloaded", "swappedAndReady": True}
    if name == "genexus_workwith":
        return handle_workwith(args)
    return error("UnknownTool", name)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def _send(self, body, headers=None, status=200):
        data = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        for key, value in (headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/_state":
            self._send({"log": STATE.log, "live": STATE.live, "persisted": STATE.persisted, "reloads": STATE.reloads})
        else:
            self._send({"ok": True})

    def do_POST(self):
        global STATE
        length = int(self.headers.get("Content-Length") or 0)
        request = json.loads(self.rfile.read(length) or b"{}")
        if self.path == "/_control":
            STATE = State(request.get("scenario", "success"), request.get("existing", ""))
            self._send({"ok": True})
            return
        method = request.get("method")
        reply = {"jsonrpc": "2.0", "id": request.get("id")}
        headers = {}
        if method == "initialize":
            reply["result"] = {"protocolVersion": "2025-11-25", "capabilities": {}}
            headers["MCP-Session-Id"] = "fake-session"
        elif method == "tools/call":
            params = request.get("params") or {}
            payload = handle_tool(params.get("name"), params.get("arguments") or {})
            reply["result"] = {"content": [{"type": "text", "text": json.dumps(payload)}]}
        else:
            reply["error"] = {"code": -32601, "message": "method not found"}
        self._send(reply, headers)


def main():
    global STATE
    STATE = State()
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    print("PORT=%d" % server.server_address[1], flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
