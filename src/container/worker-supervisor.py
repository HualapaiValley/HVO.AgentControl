#!/usr/bin/env python3
"""Fixed-operation PID1 supervisor for the worker image."""
import base64
import fcntl
import json
import os
import re
import selectors
import signal
import socket
import stat
import struct
import subprocess
import sys
import termios
import time
import uuid

BRIDGE_UID = 1101
EMPLOYEE_UID = 1102
EMPLOYEE_GID = 1102
CONTROL = "/run/worker-supervisor.sock"
BRIDGE = ["/usr/bin/dotnet", "/app/HVO.AgentControl.Worker.dll", "--worker-bridge"]
ACP = ["/usr/local/bin/opencode", "acp", "--port", "4096", "--hostname", "127.0.0.1", "--cwd", "/workspace", "--pure"]
ATTACH_PREFIX = ["/usr/local/bin/opencode", "attach", "http://127.0.0.1:4096", "--dir", "/workspace", "--session"]
MAX_REQUEST = 4096
IO_TIMEOUT = 5.0
MAX_DIMENSION = 500
SESSION_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")
children = {}
pending_acp = {}
viewer = None
stopping = False
lifecycle_handle = None
process_generation = None
server_username = "opencode"
server_password = base64.urlsafe_b64encode(os.urandom(32)).decode("ascii")


def employee_environment():
    result = {
        "HOME": "/home/worker",
        "XDG_CONFIG_HOME": "/home/worker/.config",
        "XDG_CACHE_HOME": "/home/worker/.cache",
        "XDG_DATA_HOME": "/home/worker/.local/share",
        "PATH": "/usr/local/bin:/usr/bin:/bin",
        "LANG": "C.UTF-8",
        "TERM": "xterm-256color",
        "OPENCODE_SERVER_USERNAME": server_username,
        "OPENCODE_SERVER_PASSWORD": server_password,
    }
    # The controller supplies only ContainerProfileDefinition's closed
    # containerEnv allowlist. Overlay those values into employee children only;
    # PID 1 and the bridge do not inherit them as interpreter/runtime authority.
    for name in (
        "TZ", "LANG", "LC_ALL", "EDITOR", "VISUAL", "DOTNET_ROOT", "PATH",
        "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL",
        "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
        "NPM_CONFIG_UPDATE_NOTIFIER", "NPM_CONFIG_FUND", "PYTHONDONTWRITEBYTECODE", "PIP_DISABLE_PIP_VERSION_CHECK",
    ):
        if name in os.environ:
            result[name] = os.environ[name]
    return result


def child_setup():
    os.setgroups([])
    os.setgid(EMPLOYEE_GID)
    os.setuid(EMPLOYEE_UID)
    os.umask(0o077)
    os.chdir("/workspace")


def bridge_setup():
    os.setgroups([])
    os.setgid(BRIDGE_UID)
    os.setuid(BRIDGE_UID)
    os.umask(0o077)
    os.chdir("/control")


def viewer_setup():
    child_setup()
    os.setsid()
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)


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
            "HOME": "/control", "PATH": "/usr/bin:/bin", "LANG": "C.UTF-8",
            "WORKER_CONTROL_DIRECTORY": "/control", "WORKER_ID": os.environ["WORKER_ID"],
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
            env=employee_environment(), preexec_fn=child_setup, close_fds=True)
    finally:
        acp_read.close()
        acp_write.close()
    lifecycle_handle = uuid.uuid4().hex
    process_generation = requested_generation
    children["acp"] = acp
    return {"ok": True, "state": "running", "lifecycleHandle": lifecycle_handle,
            "processGeneration": process_generation, "pid": acp.pid}


def set_winsize(fd, rows, columns):
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))


def start_viewer(session_id, rows, columns):
    global viewer
    if viewer is not None and viewer["process"].poll() is None:
        return ({"ok": False, "error": "viewer-already-running"}, None)
    close_viewer()
    acp = children.get("acp")
    if acp is None or acp.poll() is not None:
        return ({"ok": False, "error": "acp-unavailable"}, None)
    master_fd, slave_fd = os.openpty()
    try:
        set_winsize(slave_fd, rows, columns)
        process = subprocess.Popen(
            ATTACH_PREFIX + [session_id], stdin=slave_fd, stdout=slave_fd, stderr=slave_fd,
            env=employee_environment(), preexec_fn=viewer_setup, close_fds=True)
    except Exception:
        os.close(master_fd)
        raise
    finally:
        os.close(slave_fd)
    handle = uuid.uuid4().hex
    viewer = {"handle": handle, "process": process, "master_fd": master_fd, "session_id": session_id}
    return ({"ok": True, "viewerHandle": handle}, master_fd)


def close_viewer():
    global viewer
    if viewer is None:
        return
    child = viewer["process"]
    if child.poll() is not None:
        child.wait()
    master_fd = viewer["master_fd"]
    if master_fd is not None:
        try:
            os.close(master_fd)
        except OSError:
            pass
    viewer = None


def viewer_status(handle):
    """Report whether the exact viewer handle still has a live process.

    The supervisor hands the PTY master to the bridge and keeps no copy, so it
    cannot observe the transferred descriptor. It can still observe its own
    child, which is what a controller needs to decide whether an uncertain stop
    left a viewer running.
    """
    if viewer is None or viewer["handle"] != handle:
        return {"ok": True, "state": "absent"}
    return {"ok": True, "state": "running" if viewer["process"].poll() is None else "exited"}


def stop_viewer(handle):
    if viewer is None or viewer["handle"] != handle:
        return {"ok": False, "error": "viewer-not-found"}
    child = viewer["process"]
    if child.poll() is None:
        child.terminate()
        try:
            child.wait(timeout=2)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait()
    close_viewer()
    return {"ok": True}


def terminate_all(signum=signal.SIGTERM):
    global stopping
    stopping = True
    close_pending()
    if viewer is not None and viewer["process"].poll() is None:
        try:
            viewer["process"].send_signal(signum)
        except ProcessLookupError:
            pass
    for child in children.values():
        if child.poll() is None:
            try:
                child.send_signal(signum)
            except ProcessLookupError:
                pass


def reap():
    global viewer
    if viewer is not None and viewer["process"].poll() is not None:
        viewer["process"].wait()
        close_viewer()
    for name, child in list(children.items()):
        if child.poll() is not None:
            child.wait()
            children.pop(name, None)
            if not stopping and name == "bridge":
                terminate_all()
            # ACP exit is terminal for this container's process slot, but the
            # bridge remains alive for status/replay/reconciliation and an
            # explicit stop. Viewer exit never affects ACP or bridge lifetime.


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


def send(conn, value, fd=None):
    conn.settimeout(IO_TIMEOUT)
    payload = json.dumps(value, separators=(",", ":")).encode() + b"\n"
    if fd is None:
        conn.sendall(payload)
    else:
        sent = conn.sendmsg([payload], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, struct.pack("i", fd))])
        if sent != len(payload):
            raise OSError("partial supervisor descriptor response")


def valid_dimension(value):
    return isinstance(value, int) and not isinstance(value, bool) and 1 <= value <= MAX_DIMENSION


def handle(conn):
    if peer_uid(conn) != BRIDGE_UID:
        return
    request = receive_request(conn)
    if request is None:
        return
    operation = request.get("operation")
    if operation == "start":
        if set(request) != {"operation", "processGeneration"} or not isinstance(request["processGeneration"], int) or isinstance(request["processGeneration"], bool) or request["processGeneration"] < 1:
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        send(conn, start_acp(request["processGeneration"]))
    elif operation == "viewer-start":
        allowed = {"operation", "sessionId", "rows", "columns"}
        if not {"operation", "sessionId"} <= set(request) <= allowed or not isinstance(request["sessionId"], str) or not SESSION_PATTERN.fullmatch(request["sessionId"]):
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        rows = request.get("rows", 24)
        columns = request.get("columns", 80)
        if not valid_dimension(rows) or not valid_dimension(columns):
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        response, fd = start_viewer(request["sessionId"], rows, columns)
        try:
            send(conn, response, fd)
            if response.get("ok"):
                os.close(fd)
                viewer["master_fd"] = None
        except OSError:
            if response.get("ok"):
                stop_viewer(response["viewerHandle"])
            raise
    elif operation == "viewer-stop":
        if set(request) != {"operation", "viewerHandle"} or not isinstance(request["viewerHandle"], str) or not SESSION_PATTERN.fullmatch(request["viewerHandle"]):
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        send(conn, stop_viewer(request["viewerHandle"]))
    elif operation == "viewer-status":
        if set(request) != {"operation", "viewerHandle"} or not isinstance(request["viewerHandle"], str) or not SESSION_PATTERN.fullmatch(request["viewerHandle"]):
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        reap()
        send(conn, viewer_status(request["viewerHandle"]))
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
            valid = os.path.isfile("/control/bridge.key")
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


def prepare_volume_roots():
    """Own and protect only the four named-volume roots.

    `volume-nocopy` deliberately leaves a fresh Docker volume root-owned rather
    than seeding it from the image. PID 1 is the trusted fixed supervisor, so it
    initializes the mount root through an O_NOFOLLOW directory fd before any uid
    drop. It never recurses: employee data already persisted below a mount is
    neither re-owned nor rewritten during a container replacement.
    """
    for path, uid in (("/control", BRIDGE_UID), ("/home/worker", EMPLOYEE_UID),
                      ("/workspace", EMPLOYEE_UID), ("/session", EMPLOYEE_UID)):
        # Root PID 1 deliberately has CAP_CHOWN but not DAC_OVERRIDE/FOWNER. A
        # bootstrapped /control is already 1101:1101/0700, so opening it would be
        # denied. No child exists yet: reject symlinks/non-directories by lstat,
        # use CAP_CHOWN to make root the owner, chmod as owner, then hand it to
        # the fixed uid. Pathname operations touch only the mount root and never
        # recurse into persistent contents.
        value = os.lstat(path)
        if not stat.S_ISDIR(value.st_mode) or stat.S_ISLNK(value.st_mode) or value.st_nlink < 2:
            raise RuntimeError("unsafe worker volume root")
        os.chown(path, 0, 0, follow_symlinks=False)
        os.chmod(path, 0o700, follow_symlinks=False)
        os.chown(path, uid, uid, follow_symlinks=False)


def main():
    os.umask(0o077)
    prepare_volume_roots()
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
    while (children or viewer is not None) and time.monotonic() < deadline:
        reap()
        time.sleep(0.02)
    all_children = list(children.values()) + ([] if viewer is None else [viewer["process"]])
    for child in all_children:
        if child.poll() is None:
            child.kill()
    for child in all_children:
        child.wait()
    close_viewer()
    close_pending()
    server.close()
    try:
        os.unlink(CONTROL)
    except FileNotFoundError:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
