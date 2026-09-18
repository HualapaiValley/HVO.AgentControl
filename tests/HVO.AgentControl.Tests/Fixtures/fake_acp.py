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
PROMPT_COUNT = 0
SESSION_LOADED = False


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


def respond_gated_bootstrap(request_id, late_chunk):
    release = os.path.join(HOME, "bootstrap-release") if HOME else None
    deadline = time.time() + 30
    while release and not os.path.exists(release) and time.time() < deadline:
        time.sleep(0.05)
    if late_chunk:
        send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": SESSION_ID, "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "late bootstrap chunk"}}}})
    send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn"}})


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
        SESSION_LOADED = True
        if SCENARIO == "load_error":
            send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32001, "message": "session not found"}})
        else:
            send({"jsonrpc": "2.0", "id": request_id, "result": {}})
    elif method == "session/set_mode":
        if SCENARIO == "orientation_gated_handshake":
            release = os.path.join(HOME, "handshake-release") if HOME else None
            deadline = time.time() + 30
            while release and not os.path.exists(release) and time.time() < deadline:
                time.sleep(0.05)
        send({"jsonrpc": "2.0", "id": request_id, "result": {}})
    elif method == "session/prompt":
        PROMPT_COUNT += 1
        if SCENARIO == "orientation_hang" and PROMPT_COUNT > 1:
            pass
        elif SCENARIO in ("orientation_error", "orientation_secret_error") and (PROMPT_COUNT > 1 or SESSION_LOADED):
            message_text = "SENTINEL_PROVIDER_SECRET_246" if SCENARIO == "orientation_secret_error" else "orientation prompt exploded"
            send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32603, "message": message_text}})
        elif SCENARIO == "orientation_transport_close" and (PROMPT_COUNT > 1 or SESSION_LOADED):
            sys.stdout.close()
            sys.exit(0)
        elif SCENARIO in ("orientation_gated_bootstrap", "bootstrap_ignores_cancel") and PROMPT_COUNT == 1:
            # Keep reading the transport while the provider-side turn is gated so
            # session/cancel can be received and deliberately ignored.
            threading.Thread(
                target=respond_gated_bootstrap,
                args=(request_id, SCENARIO == "bootstrap_ignores_cancel"),
                daemon=True).start()
        elif SCENARIO in ("orientation_fast", "orientation_malformed", "orientation_fenced", "orientation_empty", "orientation_oversized", "orientation_non_end", "orientation_wrong_session", "orientation_wrong_assignment", "orientation_wrong_employee", "orientation_wrong_version", "orientation_null_fields", "orientation_empty_object", "orientation_bad_facts", "orientation_bad_result_shape", "orientation_post_response", "orientation_unrelated_response", "orientation_gated_bootstrap", "orientation_gated_malformed", "orientation_gated_wrong_employee", "orientation_gated_transport_close") and (PROMPT_COUNT > 1 or SESSION_LOADED):
            response_scenario = {
                "orientation_gated_malformed": "orientation_malformed",
                "orientation_gated_wrong_employee": "orientation_wrong_employee",
                "orientation_gated_transport_close": "orientation_transport_close",
            }.get(SCENARIO, SCENARIO)
            if response_scenario != SCENARIO:
                release = os.path.join(HOME, "orientation-release") if HOME else None
                deadline = time.time() + 30
                while release and not os.path.exists(release) and time.time() < deadline:
                    time.sleep(0.05)
            if response_scenario == "orientation_transport_close":
                sys.stdout.close()
                sys.exit(0)
            params = message.get("params") or {}
            prompt = params.get("prompt") or []
            text = prompt[0].get("text", "") if prompt else ""
            def value(label):
                marker = label + " "
                start = text.find(marker)
                if start < 0:
                    return ""
                start += len(marker)
                end = text.find(",", start)
                return text[start:] if end < 0 else text[start:end]
            evidence = {
                "assignmentId": value("assignmentId"),
                "employeeId": value("employeeId"),
                "sessionId": value("sessionId"),
                "orientationVersion": value("orientationVersion").rstrip("."),
                "identity": "Operations / IT",
                "department": "Operations",
                "reporting": "owner",
                "duties": ["operate and maintain the control host", "inspect runtime health and sanitized diagnostics", "explain organization state", "request owner-authorized changes"],
                "restrictions": ["no secrets or controller-private state", "no unrestricted Docker, GitHub, or host authority", "no autonomous hiring, provisioning, delegation, or dispatch", "no Fleet or V1", "no cross-employee history"],
                "escalation": "escalate uncertainty, failed controls, suspected secret exposure, and irreversible effects before retrying"
            }
            response_text = json.dumps(evidence)
            if response_scenario == "orientation_malformed":
                response_text = "{not-json"
            elif response_scenario == "orientation_fenced":
                response_text = "```json\n" + response_text + "\n```"
            elif response_scenario == "orientation_empty":
                response_text = ""
            elif response_scenario == "orientation_oversized":
                response_text = "x" * (16 * 1024 + 1)
            elif response_scenario == "orientation_wrong_assignment":
                evidence["assignmentId"] = "asn_wrong"
                response_text = json.dumps(evidence)
            elif response_scenario == "orientation_wrong_employee":
                evidence["employeeId"] = "emp_wrong"
                response_text = json.dumps(evidence)
            elif response_scenario == "orientation_wrong_version":
                evidence["orientationVersion"] = "sha256:wrong"
                response_text = json.dumps(evidence)
            elif response_scenario == "orientation_null_fields":
                evidence["identity"] = None
                evidence["duties"] = None
                response_text = json.dumps(evidence)
            elif response_scenario == "orientation_empty_object":
                response_text = "{}"
            elif response_scenario == "orientation_bad_facts":
                evidence["department"] = "Finance"
                response_text = json.dumps(evidence)
            midpoint = len(response_text) // 2
            chunks = [response_text[:midpoint], response_text[midpoint:]]
            if response_scenario == "orientation_unrelated_response":
                send({"jsonrpc": "2.0", "id": 424242, "result": {}})
            chunk_session = "ses_wrong" if response_scenario == "orientation_wrong_session" else SESSION_ID
            for chunk in chunks:
                send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": chunk_session, "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": chunk}}}})
            # Adjacent chunks and result intentionally have no sleep. The host's
            # codec-order frame hook is the completion barrier.
            stop_reason = "max_tokens" if response_scenario == "orientation_non_end" else "end_turn"
            result = [] if response_scenario == "orientation_bad_result_shape" else {"stopReason": stop_reason}
            send({"jsonrpc": "2.0", "id": request_id, "result": result})
            if response_scenario == "orientation_post_response":
                send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": SESSION_ID, "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "post-response-corruption"}}}})
        elif SCENARIO == "prompt_fast":
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
            # Pinned OpenCode 1.18.30 emits generic option IDs for read/edit/bash
            # permission classes. The default scenario keeps the older suffixed
            # shape so both the legacy and the live pinned shapes stay covered.
            if SCENARIO == "permission_generic":
                options = [
                    {"optionId": "once", "name": "Allow once", "kind": "allow_once"},
                    {"optionId": "always", "name": "Allow always", "kind": "allow_always"},
                    {"optionId": "reject", "name": "Reject", "kind": "reject_once"},
                ]
            else:
                options = [
                    {"optionId": "allow_once", "name": "Allow once", "kind": "allow_once"},
                    {"optionId": "reject_once", "name": "Reject once", "kind": "reject_once"},
                ]
            send({"jsonrpc": "2.0", "id": 9001, "method": "session/request_permission", "params": {"sessionId": SESSION_ID, "toolCall": {"toolCallId": "tc-1", "title": "Read secret", "kind": "read", "status": "pending"}, "options": options}})
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
