#!/usr/bin/env python3
"""Canonical fake OpenCode/ACP executable for the CLIProxy request test.

It captures the environment OpenCode would actually receive into
``$HOME/opencode-capture.json`` (mode 0600) and then speaks just enough ACP to
let the control host reach its bootstrap turn. It never prints the inference key
or the generated config to stdout/stderr and performs no model call.

The capture is a disposable test artifact under the test's private data
directory; it is not a product code path.
"""
import json
import os
import sys
import threading

SESSION_ID = "ses_proxy_capture_0001"
WRITE_LOCK = threading.Lock()


def capture():
    home = os.environ.get("HOME")
    if not home:
        return
    payload = {
        "apiKey": os.environ.get("CLIPROXY_API_KEY", ""),
        "config": os.environ.get("OPENCODE_CONFIG_CONTENT", ""),
        "args": sys.argv[1:],
    }
    try:
        path = os.path.join(home, "opencode-capture.json")
        flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
        descriptor = os.open(path, flags, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(payload, handle)
    except OSError:
        pass


def send(payload):
    with WRITE_LOCK:
        sys.stdout.write(json.dumps(payload) + "\n")
        sys.stdout.flush()


capture()

for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        message = json.loads(line)
    except ValueError:
        continue
    request_id = message.get("id")
    method = message.get("method")
    if method == "initialize":
        send({"jsonrpc": "2.0", "id": request_id, "result": {"protocolVersion": 1, "agentCapabilities": {}}})
    elif method == "session/new":
        send({"jsonrpc": "2.0", "id": request_id, "result": {"sessionId": SESSION_ID, "configOptions": []}})
    elif method == "session/load":
        send({"jsonrpc": "2.0", "id": request_id, "result": {}})
    elif method == "session/prompt":
        send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn"}})
    elif method is not None:
        send({"jsonrpc": "2.0", "id": request_id, "result": {}})
