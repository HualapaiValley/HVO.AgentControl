#!/usr/bin/env python3
"""Fixed-operation PID1 supervisor for the worker image."""
import json
import os
import selectors
import signal
import socket
import struct
import subprocess
import sys
import time
import uuid

BRIDGE_UID = 1101
EMPLOYEE_UID = 1102
EMPLOYEE_GID = 1102
CONTROL = "/run/worker-supervisor.sock"
BRIDGE = ["/usr/bin/dotnet", "/app/HVO.AgentControl.Worker.dll", "--worker-bridge"]
ACP = ["/usr/local/bin/opencode", "acp"]
MAX_REQUEST = 4096
IO_TIMEOUT = 5.0
children = {}
pending_acp = {}
stopping = False
lifecycle_handle = None
process_generation = None


def child_setup():
    os.setgroups([])
    os.setgid(EMPLOYEE_GID)
    os.setuid(EMPLOYEE_UID)
    os.umask(0o077)
    os.chdir("/worker/workspace")


def bridge_setup():
    os.setgroups([])
    os.setgid(BRIDGE_UID)
    os.setuid(BRIDGE_UID)
    os.umask(0o077)
    os.chdir("/worker-control")


def close_pending():
    for value in pending_acp.values():
        try:
            value.close()
        except OSError:
            pass
    pending_acp.clear()


def start_bridge():
    bridge_read, acp_write = socket.socketpair()
    acp_read, bridge_write = socket.socketpair()
    try:
        env = {
            "HOME": "/worker-control", "PATH": "/usr/bin:/bin", "LANG": "C.UTF-8",
            "WORKER_CONTROL_DIRECTORY": "/worker-control", "WORKER_ID": os.environ["WORKER_ID"],
            "WORKER_CONTROLLER_ID": os.environ["WORKER_CONTROLLER_ID"],
            "WORKER_ACP_READ_FD": str(bridge_read.fileno()),
            "WORKER_ACP_WRITE_FD": str(bridge_write.fileno())}
        bridge = subprocess.Popen(
            BRIDGE, stdin=subprocess.DEVNULL, stdout=sys.stdout, stderr=sys.stderr,
            env=env, preexec_fn=bridge_setup,
            pass_fds=(bridge_read.fileno(), bridge_write.fileno()), close_fds=True)
        children["bridge"] = bridge
        pending_acp["read"] = acp_read
        pending_acp["write"] = acp_write
    except Exception:
        acp_read.close()
        acp_write.close()
        raise
    finally:
        bridge_read.close()
        bridge_write.close()


def start_acp(requested_generation):
    global lifecycle_handle, process_generation
    bridge = children.get("bridge")
    if bridge is None or bridge.poll() is not None:
        return {"ok": False, "error": "bridge-unavailable"}
    current = children.get("acp")
    if current and current.poll() is None:
        return {"ok": False, "error": "already-running", "lifecycleHandle": lifecycle_handle,
                "processGeneration": process_generation, "pid": current.pid}
    if set(pending_acp) != {"read", "write"}:
        return {"ok": False, "error": "transport-unavailable"}
    acp_read = pending_acp.pop("read")
    acp_write = pending_acp.pop("write")
    try:
        acp = subprocess.Popen(
            ACP, stdin=acp_read.fileno(), stdout=acp_write.fileno(), stderr=sys.stderr,
            env={"HOME": "/worker/home", "PATH": "/usr/local/bin:/usr/bin:/bin", "LANG": "C.UTF-8"},
            preexec_fn=child_setup, close_fds=True)
    finally:
        acp_read.close()
        acp_write.close()
    lifecycle_handle = uuid.uuid4().hex
    process_generation = requested_generation
    children["acp"] = acp
    return {"ok": True, "state": "running", "lifecycleHandle": lifecycle_handle,
            "processGeneration": process_generation, "pid": acp.pid}


def terminate_all(signum=signal.SIGTERM):
    global stopping
    stopping = True
    close_pending()
    for child in children.values():
        if child.poll() is None:
            try:
                child.send_signal(signum)
            except ProcessLookupError:
                pass


def reap():
    for name, child in list(children.items()):
        if child.poll() is not None:
            children.pop(name, None)
            if not stopping:
                terminate_all()


def peer_uid(conn):
    return struct.unpack("3i", conn.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))[1]


def receive_request(conn):
    conn.settimeout(IO_TIMEOUT)
    data = bytearray()
    while len(data) <= MAX_REQUEST:
        chunk = conn.recv(min(1024, MAX_REQUEST + 1 - len(data)))
        if not chunk:
            return None
        data.extend(chunk)
        if b"\n" in chunk:
            break
    if len(data) > MAX_REQUEST or not data.endswith(b"\n") or data.count(b"\n") != 1:
        return None
    try:
        request = json.loads(data)
    except (json.JSONDecodeError, UnicodeDecodeError):
        return None
    return request if isinstance(request, dict) else None


def send(conn, value):
    conn.settimeout(IO_TIMEOUT)
    conn.sendall(json.dumps(value, separators=(",", ":")).encode() + b"\n")


def handle(conn):
    if peer_uid(conn) != BRIDGE_UID:
        return
    request = receive_request(conn)
    if request is None:
        return
    operation = request.get("operation")
    if operation == "start":
        if set(request) != {"operation", "processGeneration"} or not isinstance(request["processGeneration"], int) or request["processGeneration"] < 1:
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        send(conn, start_acp(request["processGeneration"]))
    elif operation == "status" and set(request) == {"operation"}:
        child = children.get("acp")
        running = child is not None and child.poll() is None
        send(conn, {"state": "running" if running else "stopped", "lifecycleHandle": lifecycle_handle,
                    "processGeneration": process_generation, "pid": child.pid if running else None})
    elif operation == "stop" and set(request) == {"operation"}:
        child = children.get("acp")
        if child and child.poll() is None:
            child.terminate()
        send(conn, {"ok": True})
    else:
        send(conn, {"ok": False, "error": "unsupported"})


def prepare_socket():
    try:
        state = os.lstat(CONTROL)
    except FileNotFoundError:
        state = None
    if state is not None:
        if not stat_is_socket(state.st_mode) or state.st_uid != 0 or state.st_nlink != 1:
            raise RuntimeError("unsafe supervisor socket")
        os.unlink(CONTROL)
    server = socket.socket(socket.AF_UNIX)
    server.bind(CONTROL)
    os.chmod(CONTROL, 0o600)
    os.chown(CONTROL, BRIDGE_UID, BRIDGE_UID)
    server.listen(4)
    server.setblocking(False)
    return server


def stat_is_socket(mode):
    return mode & 0o170000 == 0o140000


def bridge_key_present():
    read_fd, write_fd = os.pipe2(os.O_CLOEXEC)
    child = os.fork()
    if child == 0:
        try:
            os.close(read_fd)
            bridge_setup()
            valid = os.path.isfile("/worker-control/bridge.key")
            os.write(write_fd, b"1" if valid else b"0")
        except OSError:
            os.write(write_fd, b"0")
        finally:
            os._exit(0)
    os.close(write_fd)
    result = os.read(read_fd, 1)
    os.close(read_fd)
    os.waitpid(child, 0)
    return result == b"1"


def main():
    os.umask(0o077)
    if not bridge_key_present():
        print("worker key is absent; run the documented stdin bootstrap command before starting the worker profile", file=sys.stderr, flush=True)
        return 78
    server = prepare_socket()
    selector = selectors.DefaultSelector()
    selector.register(server, selectors.EVENT_READ)
    start_bridge()
    signal.signal(signal.SIGTERM, lambda *_: terminate_all())
    signal.signal(signal.SIGINT, lambda *_: terminate_all(signal.SIGINT))
    while children:
        for key, _ in selector.select(0.1):
            conn, _ = key.fileobj.accept()
            with conn:
                try:
                    handle(conn)
                except (OSError, ValueError, KeyError, subprocess.SubprocessError):
                    print("worker supervisor fixed operation failed", file=sys.stderr, flush=True)
        reap()
    terminate_all()
    deadline = time.monotonic() + 5
    while children and time.monotonic() < deadline:
        reap()
        time.sleep(0.02)
    for child in children.values():
        if child.poll() is None:
            child.kill()
    for child in children.values():
        child.wait()
    close_pending()
    server.close()
    try:
        os.unlink(CONTROL)
    except FileNotFoundError:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
