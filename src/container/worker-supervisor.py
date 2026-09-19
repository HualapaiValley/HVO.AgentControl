#!/usr/bin/env python3
"""Fixed-operation PID1 supervisor for the worker image."""
import base64
import fcntl
import hashlib
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
# Orientation delivery carries up to 64 KiB of content, base64-encoded into the
# fixed frame, so the control frame bound is raised to the smallest value that
# still holds the exact maximum request plus fixed JSON overhead.
MAX_REQUEST = 192 * 1024
IO_TIMEOUT = 5.0
MAX_DIMENSION = 500
MAX_ORIENTATION_BYTES = 64 * 1024
EMPLOYEE_HOME = "/home/worker"
ORIENTATION_VERSION_PATTERN = re.compile(r"^[^\x00-\x1f\x7f]{1,128}$")
ORIENTATION_NAME_PATTERN = re.compile(r"^[A-Za-z0-9._-]{1,128}$")
ORIENTATION_HASH_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
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


def orientation_root():
    return os.path.join(EMPLOYEE_HOME, ".agentcontrol", "orientation")


def validate_orientation_request(request):
    """Validates the exact fixed orientation-install request.

    Every field is checked here again, independently of the bridge, because this
    is the boundary where untrusted base64 and a caller-chosen file name become a
    root-owned write. The content hash is the only authority for content and is
    verified against the decoded bytes. Returns (filename, content) or None.
    """
    if set(request) != {"operation", "assignmentId", "orientationVersion",
                        "artifactFileName", "contentHash", "content"}:
        return None
    assignment = request["assignmentId"]
    version = request["orientationVersion"]
    filename = request["artifactFileName"]
    content_hash = request["contentHash"]
    encoded = request["content"]
    if not isinstance(assignment, str) or not SESSION_PATTERN.fullmatch(assignment):
        return None
    if not isinstance(version, str) or not ORIENTATION_VERSION_PATTERN.fullmatch(version):
        return None
    if not isinstance(filename, str) or not ORIENTATION_NAME_PATTERN.fullmatch(filename) or filename in (".", ".."):
        return None
    if not isinstance(content_hash, str) or not ORIENTATION_HASH_PATTERN.fullmatch(content_hash):
        return None
    if not isinstance(encoded, str):
        return None
    try:
        content = base64.b64decode(encoded, validate=True)
    except ValueError:
        return None
    if len(content) > MAX_ORIENTATION_BYTES or b"\x00" in content:
        return None
    if hashlib.sha256(content).hexdigest() != content_hash[7:]:
        return None
    return filename, content


def open_orientation_directory():
    """Opens the fixed orientation directory without following any symlink.

    The employee owns the home tree and could replace a path component with a
    symlink, so each level is opened with O_NOFOLLOW|O_DIRECTORY and created
    relative to its parent descriptor.
    """
    home_fd = os.open(EMPLOYEE_HOME, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        try:
            os.mkdir(".agentcontrol", 0o700, dir_fd=home_fd)
        except FileExistsError:
            pass
        agent_fd = os.open(".agentcontrol", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=home_fd)
        try:
            try:
                os.mkdir("orientation", 0o700, dir_fd=agent_fd)
            except FileExistsError:
                pass
            return os.open("orientation", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=agent_fd)
        finally:
            os.close(agent_fd)
    finally:
        os.close(home_fd)


def write_orientation_bytes(root_fd, filename, content):
    """Writes one 0600 file atomically, relative to the validated directory fd."""
    temporary = ".orientation." + uuid.uuid4().hex + ".tmp"
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=root_fd)
    try:
        written = 0
        while written < len(content):
            written += os.write(descriptor, content[written:])
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
    os.rename(temporary, filename, src_dir_fd=root_fd, dst_dir_fd=root_fd)
    os.fsync(root_fd)


def install_orientation(filename, content):
    """Runs the write as the employee uid through a forked child.

    PID 1 holds CAP_CHOWN but not DAC_OVERRIDE, so it cannot write inside the
    0700 employee home. The forked child drops to the employee identity and
    performs the O_NOFOLLOW traversal and atomic rename there. The parent only
    learns success/failure, never mutates the path itself.
    """
    read_fd, write_fd = os.pipe2(0)
    child = os.fork()
    if child == 0:
        try:
            os.close(read_fd)
            try:
                child_setup()
                root_fd = open_orientation_directory()
                try:
                    write_orientation_bytes(root_fd, filename, content)
                finally:
                    os.close(root_fd)
                os.write(write_fd, b"1")
            except BaseException:
                try:
                    os.write(write_fd, b"0")
                except OSError:
                    pass
        finally:
            os._exit(0)
    os.close(write_fd)
    try:
        result = os.read(read_fd, 1)
    finally:
        os.close(read_fd)
        os.waitpid(child, 0)
    return result == b"1"


def read_orientation(filename, expected_hash):
    """Reads one installed artifact as the employee uid through a forked child.

    The 0600 file is unreadable to the bridge uid, so the read must happen under
    the employee identity. The child opens the directory with O_NOFOLLOW and
    reads the exact basename, then hashes the bytes. Returns (content, digest) on
    success or None. The child verifies the expected hash before the parent sees
    any content.
    """
    read_fd, write_fd = os.pipe2(0)
    child = os.fork()
    if child == 0:
        try:
            os.close(read_fd)
            payload = None
            try:
                child_setup()
                root_fd = open_orientation_directory()
                try:
                    descriptor = os.open(filename, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=root_fd)
                    try:
                        data = b""
                        while len(data) <= MAX_ORIENTATION_BYTES:
                            chunk = os.read(descriptor, MAX_ORIENTATION_BYTES + 1 - len(data))
                            if not chunk:
                                break
                            data += chunk
                    finally:
                        os.close(descriptor)
                finally:
                    os.close(root_fd)
                if len(data) <= MAX_ORIENTATION_BYTES and b"\x00" not in data:
                    digest = "sha256:" + hashlib.sha256(data).hexdigest()
                    if digest == expected_hash:
                        payload = json.dumps({"content": base64.b64encode(data).decode("ascii"), "contentHash": digest}).encode("utf-8")
            except BaseException:
                payload = None
            if payload is not None:
                os.write(write_fd, b"1" + payload)
            else:
                os.write(write_fd, b"0")
        finally:
            os._exit(0)
    os.close(write_fd)
    chunks = []
    try:
        while True:
            chunk = os.read(read_fd, 65536)
            if not chunk:
                break
            chunks.append(chunk)
    finally:
        os.close(read_fd)
        os.waitpid(child, 0)
    data = b"".join(chunks)
    if not data.startswith(b"1"):
        return None
    try:
        answer = json.loads(data[1:])
    except ValueError:
        return None
    if not isinstance(answer.get("content"), str) or not isinstance(answer.get("contentHash"), str):
        return None
    return answer


def validate_orientation_read_request(request):
    """Validates the exact fixed orientation-read request.

    Only the exact installed basename and its recorded hash can be read; the
    content hash is the authority and is re-verified against the bytes. Returns
    (filename, content_hash) or None.
    """
    if set(request) != {"operation", "assignmentId", "orientationVersion",
                        "artifactFileName", "contentHash"}:
        return None
    assignment = request["assignmentId"]
    version = request["orientationVersion"]
    filename = request["artifactFileName"]
    content_hash = request["contentHash"]
    if not isinstance(assignment, str) or not SESSION_PATTERN.fullmatch(assignment):
        return None
    if not isinstance(version, str) or not ORIENTATION_VERSION_PATTERN.fullmatch(version):
        return None
    if not isinstance(filename, str) or not ORIENTATION_NAME_PATTERN.fullmatch(filename) or filename in (".", ".."):
        return None
    if not isinstance(content_hash, str) or not ORIENTATION_HASH_PATTERN.fullmatch(content_hash):
        return None
    return filename, content_hash


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
    elif operation == "orientation-install":
        validated = validate_orientation_request(request)
        if validated is None:
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        filename, content = validated
        if not install_orientation(filename, content):
            send(conn, {"ok": False, "error": "orientation-install-failed"})
            return
        send(conn, {"ok": True, "installedPath": os.path.join(orientation_root(), filename),
                    "contentHash": request["contentHash"]})
    elif operation == "orientation-read":
        validated = validate_orientation_read_request(request)
        if validated is None:
            send(conn, {"ok": False, "error": "invalid-request"})
            return
        filename, content_hash = validated
        answer = read_orientation(filename, content_hash)
        if answer is None:
            send(conn, {"ok": False, "error": "orientation-read-failed"})
            return
        send(conn, {"ok": True, "installedPath": os.path.join(orientation_root(), filename),
                    "contentHash": answer["contentHash"], "content": answer["content"]})
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
