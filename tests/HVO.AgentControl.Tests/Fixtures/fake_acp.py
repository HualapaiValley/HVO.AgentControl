#!/usr/bin/env python3
import json
import os
import sys
import threading
import time


def resolve_scenario():
    # Scenario comes from a sidecar next to the *invoked* path, not the resolved
    # symlink target. Callers materialize a unique symlink to this canonical,
    # never-rewritten fixture plus a non-executable "scenario" sidecar. Starting
    # this file directly (the Start helper) falls back to the first argument.
    here = os.path.dirname(os.path.abspath(sys.argv[0]))
    sidecar = os.path.join(here, "scenario")
    if os.path.exists(sidecar):
        with open(sidecar, "r") as handle:
            value = handle.read().strip()
        return value or "happy"
    if len(sys.argv) > 1:
        return sys.argv[1]
    return "happy"


SCENARIO = resolve_scenario()

HOME = os.environ.get("HOME")
CALLS = os.path.join(HOME, "calls.log") if HOME else None
SESSION_ID = "ses_fake_0001"
WRITE_LOCK = threading.Lock()


def log_call(method):
    if not CALLS:
        return
    try:
        with open(CALLS, "a") as handle:
            handle.write(method + "\n")
    except Exception:
        pass


def send(payload):
    with WRITE_LOCK:
        sys.stdout.write(json.dumps(payload) + "\n")
        sys.stdout.flush()


def respond_slow(request_id):
    time.sleep(0.30)
    send({"jsonrpc": "2.0", "id": request_id, "result": {"method": "test/slow"}})


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        message = json.loads(line)
    except Exception:
        continue
    request_id = message.get("id")
    method = message.get("method")
    if method:
        log_call(method)
    if method == "initialize":
        if SCENARIO == "init_error":
            send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32603, "message": "initialize exploded"}})
        elif SCENARIO.startswith("init_version_"):
            raw = SCENARIO[len("init_version_"):]
            result = {} if raw == "missing" else {"protocolVersion": "1" if raw == "string" else json.loads(raw)}
            send({"jsonrpc": "2.0", "id": request_id, "result": result})
        else:
            send({"jsonrpc": "2.0", "id": request_id, "result": {"protocolVersion": 1, "agentCapabilities": {}}})
    elif method == "session/new":
        send({"jsonrpc": "2.0", "id": request_id, "result": {"sessionId": SESSION_ID, "configOptions": []}})
        send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": SESSION_ID, "update": {"sessionUpdate": "current_mode_update", "currentModeId": "agentcontrol"}}})
    elif method == "session/load":
        if SCENARIO == "load_error":
            send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32001, "message": "session not found"}})
        else:
            send({"jsonrpc": "2.0", "id": request_id, "result": {}})
    elif method == "session/set_mode":
        send({"jsonrpc": "2.0", "id": request_id, "result": {}})
    elif method == "session/prompt":
        if SCENARIO == "prompt_fast":
            send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn"}})
        elif SCENARIO == "prompt_error":
            send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32603, "message": "prompt exploded"}})
        elif SCENARIO == "prompt_schema_error":
            send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32602, "message": "invalid prompt"}})
        elif SCENARIO == "prompt_stop":
            send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "max_tokens"}})
        elif SCENARIO == "prompt_hang":
            # Never answers. The host's prompt deadline must bound the
            # wait; the transport stays open so the session remains
            # usable (and cancellable) afterwards.
            pass
        else:
            send({"jsonrpc": "2.0", "id": 9001, "method": "session/request_permission", "params": {"sessionId": SESSION_ID, "toolCall": {"toolCallId": "tc-1", "title": "Read secret", "kind": "read", "status": "pending"}, "options": [{"optionId": "allow_once", "name": "Allow once", "kind": "allow_once"}, {"optionId": "reject_once", "name": "Reject once", "kind": "reject_once"}]}})
            permission_response = None
            while True:
                raw = sys.stdin.readline()
                if not raw:
                    break
                try:
                    candidate = json.loads(raw)
                except Exception:
                    continue
                if candidate.get("id") == 9001:
                    permission_response = candidate
                    break
            send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn", "permissionResponse": permission_response}})
    elif method == "test/slow":
        threading.Thread(target=respond_slow, args=(request_id,), daemon=True).start()
    elif method == "test/fast":
        send({"jsonrpc": "2.0", "id": request_id, "result": {"method": "test/fast"}})
    elif method == "huge":
        sys.stdout.write("x" * 6000 + "\n")
        sys.stdout.flush()
    else:
        send({"jsonrpc": "2.0", "id": request_id, "result": {}})
