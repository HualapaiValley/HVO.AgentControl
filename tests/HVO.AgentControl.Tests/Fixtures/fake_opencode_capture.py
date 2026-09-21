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
MAX_PROMPT_BLOCKS = 32
MAX_PROMPT_BYTES = 256 * 1024


def valid_prompt_content(params):
    # Mirror the pinned OpenCode/ACP session/prompt validation: a non-empty,
    # bounded array of text content blocks. Anything else is -32602.
    if not isinstance(params, dict):
        return False
    prompt = params.get("prompt")
    if not isinstance(prompt, list) or not prompt or len(prompt) > MAX_PROMPT_BLOCKS:
        return False
    total = 0
    for block in prompt:
        if not isinstance(block, dict):
            return False
        if block.get("type") != "text":
            return False
        text = block.get("text")
        if not isinstance(text, str) or not text or "\x00" in text:
            return False
        total += len(text.encode("utf-8"))
        if total > MAX_PROMPT_BYTES:
            return False
    return True


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
        if not valid_prompt_content(message.get("params")):
            send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32602, "message": "Invalid params"}})
        else:
            send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn"}})
    elif method is not None:
        send({"jsonrpc": "2.0", "id": request_id, "result": {}})
