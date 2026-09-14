#!/usr/bin/env python3
"""Establish the controller/agent filesystem layout as root, without ever
following a path the agent identity can control.

The control container starts as root only long enough to run this helper and
then drops to the controller identity for good. Everything here runs with the
agent's previous writes already on the volume: `/data` is persistent and was
owned by UID 1000 before isolation existed, so every entry inside it is
attacker-controlled input, not trusted state.

Three rules follow, and they are why this is not a `chown`/`chmod` shell
sequence:

1. **Lock the attacker-writable parent first.** `/data` becomes root-owned
   `0755` before any entry inside it is inspected. That is what removes the
   original escalation: while the agent owned the `/data` directory entry it
   could rename `home`/`workspace` away and leave a symlink to `/usr/local/bin`
   or `/control-data` for the next root start to follow. Once the parent is
   root-owned the agent cannot create, rename or unlink anything directly in
   `/data`, so the entries examined below cannot be swapped mid-run. It keeps
   full ownership of the two subtrees it actually uses.
2. **Never dereference.** Every entry is opened with `O_NOFOLLOW` (plus
   `O_DIRECTORY` where a directory is required) relative to a directory
   descriptor, and ownership/permissions are applied to that descriptor with
   `fchown`/`fchmod`. A planted symlink can therefore never become a chown of
   its target; it is rejected outright.
3. **Validate everything before changing anything inside the tree.** All
   descriptors are opened and checked first; a single rejected entry aborts
   before the first metadata change to `home`, `workspace` or the legacy state
   file. A failed start leaves the service down, which is the intended outcome:
   the isolation boundary could not be established, so it is not claimed.

Nothing is recursive. Nothing under `/data/home` or `/data/workspace` is
created, moved, re-owned or deleted: the agent's existing content keeps its
UID 1000 ownership, and only the two directory inodes themselves are normalized.

Runtime state
-------------

The controller-private `/control-data/runtime.json` is the single authoritative
runtime state while the isolated image runs. Three files exist around it and
each has one job:

`/control-data/runtime.json`
    Authoritative. Written by the controller during the isolated run.
`/control-data/runtime.pre-isolation.json` (+ `.meta.json` evidence)
    The byte-exact original legacy state captured **once**, at first adoption,
    with its SHA-256, size and provenance. It is never overwritten, so the
    state the deployment had before isolation stays recoverable independently
    of whatever happens to `/data/runtime.json` afterwards.
`/data/runtime.json`
    While isolated this is a **stale** root-owned `0600` leftover, not a
    rollback source: the controller does not update it, so it drifts from the
    private state with every session change. `scripts/init-secrets.py
    --revert-isolation` republishes the *current* private state over it when the
    operator rolls back. Reducing it to root-only matters because the original
    carries the tmux owner token and the agent must not read it.

Rollback interlock
------------------

`--revert-isolation` records `/control-data/rollback.active`. There is no
automatic merge between a state the pre-isolation image advanced in
`/data/runtime.json` and the private state left by the isolated run, so while
that marker exists this helper refuses to start: rolling forward would
otherwise silently discard one of the two. The operator picks the surviving
state explicitly with `scripts/init-secrets.py --resume-isolation
--state-source legacy|private`, which is the only thing that clears the marker.
"""

import errno
import hashlib
import json
import os
import stat
import sys
import time

AGENT_UID = 1000
AGENT_GID = 1000
CONTROL_UID = 1001
CONTROL_GID = 1001

AGENT_DATA = "/data"
AGENT_SUBDIRECTORIES = ("home", "workspace")
CONTROL_DATA = "/control-data"
AGENT_CONFIG = "/agent-config"

LEGACY_STATE_NAME = "runtime.json"
PRIVATE_STATE_NAME = "runtime.json"
SNAPSHOT_NAME = "runtime.pre-isolation.json"
SNAPSHOT_EVIDENCE_NAME = "runtime.pre-isolation.meta.json"
ROLLBACK_MARKER_NAME = "rollback.active"
CONTROLLER_PID_NAME = "controller.pid"

RESUME_INSTRUCTION = (
    "python3 scripts/init-secrets.py --resume-isolation --state-source legacy|private"
)

OPEN_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC
DIRECTORY_FLAGS = OPEN_FLAGS | os.O_DIRECTORY
# O_NONBLOCK so a planted FIFO cannot make the entrypoint hang instead of fail.
FILE_FLAGS = OPEN_FLAGS | os.O_NONBLOCK


class LayoutError(Exception):
    """A condition that must stop the container from starting."""


def log(message):
    print("prepare-layout: %s" % message, file=sys.stderr)


def describe(error, path, parent_fd=None, name=None):
    """Explain a refused open without ever following the path again.

    `O_NOFOLLOW | O_DIRECTORY` reports a symlink as ENOTDIR rather than ELOOP, so
    the distinction is recovered from an lstat on the same directory descriptor.
    The diagnosis matters: "someone planted a symlink here" and "this is a
    regular file" are very different operational situations.
    """
    if error.errno in (errno.ELOOP, errno.ENOTDIR) and parent_fd is not None:
        try:
            info = os.lstat(name, dir_fd=parent_fd)
        except OSError:
            info = None
        if info is not None and stat.S_ISLNK(info.st_mode):
            return "'%s' is a symlink; refusing to act on it" % path
    if error.errno == errno.ELOOP:
        return "'%s' is a symlink; refusing to act on it" % path
    if error.errno == errno.ENOTDIR:
        return "'%s' is not a directory; refusing to act on it" % path
    return "'%s' could not be opened: %s" % (path, error.strerror)


def open_directory(path):
    """Open an existing directory, rejecting a symlink at the final component."""
    try:
        return os.open(path, DIRECTORY_FLAGS)
    except OSError as error:
        raise LayoutError(describe(error, path)) from error


def open_child_directory(parent_fd, name, parent_path):
    """Open `parent/name` as a directory, or return None when it is absent.

    A symlink, regular file or any other non-directory is a hard failure: the
    agent may have replaced the entry, and following or replacing it would be
    exactly the escalation this helper exists to prevent.
    """
    try:
        return os.open(name, DIRECTORY_FLAGS, dir_fd=parent_fd)
    except OSError as error:
        if error.errno == errno.ENOENT:
            return None
        raise LayoutError(
            describe(error, os.path.join(parent_path, name), parent_fd, name)
        ) from error


def open_regular_file(parent_fd, name, parent_path):
    """Open `parent/name` as a regular file, or return None when it is absent."""
    path = os.path.join(parent_path, name)
    try:
        fd = os.open(name, FILE_FLAGS, dir_fd=parent_fd)
    except OSError as error:
        if error.errno == errno.ENOENT:
            return None
        raise LayoutError(describe(error, path, parent_fd, name)) from error

    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode):
            raise LayoutError("'%s' is not a regular file; refusing to act on it" % path)
        if info.st_nlink != 1:
            raise LayoutError("'%s' has extra hard links; refusing to act on it" % path)
    except BaseException:
        os.close(fd)
        raise
    return fd


def apply(fd, uid, gid, mode, path):
    """Apply ownership and mode to an already-validated descriptor."""
    os.fchown(fd, uid, gid)
    os.fchmod(fd, mode)
    verified = os.fstat(fd)
    if verified.st_uid != uid or verified.st_gid != gid:
        raise LayoutError("ownership verification failed for '%s'" % path)
    if stat.S_IMODE(verified.st_mode) != mode:
        raise LayoutError("permission verification failed for '%s'" % path)


def read_all(fd):
    """Read a validated descriptor from the start, without reopening its path."""
    os.lseek(fd, 0, os.SEEK_SET)
    chunks = []
    while True:
        chunk = os.read(fd, 65536)
        if not chunk:
            return b"".join(chunks)
        chunks.append(chunk)


def publish(directory_fd, directory_path, name, payload, uid, gid, mode):
    """Publish bytes into a directory atomically, by descriptor only.

    The temporary is created inside the destination directory with `O_EXCL` and
    `O_NOFOLLOW`, given its final ownership and mode while it is still invisible
    under the published name, flushed to disk, and only then renamed over the
    target with `os.replace` relative to the same directory descriptor. The
    directory itself is fsynced afterwards, so a crash cannot leave the rename
    unrecorded. A reader therefore sees either the previous file or the complete
    new one, never a partial write, and no step ever resolves a path that a
    symlink could redirect.
    """
    temporary = ".%s.%d.tmp" % (name, os.getpid())
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
    fd = os.open(temporary, flags, 0o600, dir_fd=directory_fd)
    published = False
    try:
        offset = 0
        while offset < len(payload):
            offset += os.write(fd, payload[offset:])
        os.fchown(fd, uid, gid)
        os.fchmod(fd, mode)
        os.fsync(fd)
        os.replace(temporary, name, src_dir_fd=directory_fd, dst_dir_fd=directory_fd)
        published = True
    finally:
        os.close(fd)
        if not published:
            try:
                os.unlink(temporary, dir_fd=directory_fd)
            except OSError:
                pass
    os.fsync(directory_fd)

    # Verify what landed rather than what was intended.
    check_fd = open_regular_file(directory_fd, name, directory_path)
    if check_fd is None:
        raise LayoutError("'%s' disappeared immediately after publication" % os.path.join(directory_path, name))
    try:
        written = read_all(check_fd)
        info = os.fstat(check_fd)
    finally:
        os.close(check_fd)
    if written != payload:
        raise LayoutError("'%s' does not match the published bytes" % os.path.join(directory_path, name))
    if info.st_uid != uid or info.st_gid != gid or stat.S_IMODE(info.st_mode) != mode:
        raise LayoutError("'%s' does not have the published ownership or mode" % os.path.join(directory_path, name))
    return hashlib.sha256(payload).hexdigest()


def exists(directory_fd, name):
    """True when `directory/name` exists, without following a final symlink."""
    try:
        os.lstat(name, dir_fd=directory_fd)
    except FileNotFoundError:
        return False
    return True


def record_pre_isolation_snapshot(control_fd, payload, provenance):
    """Preserve the original legacy state once, with hash evidence.

    This is what makes the pre-isolation state independent of `/data/runtime.json`
    afterwards: the rollback republishes the *current* private state over that
    path, so the file itself can no longer serve as the historical record. An
    existing snapshot is never overwritten - only the first one is the original.
    """
    if exists(control_fd, SNAPSHOT_NAME):
        return False

    digest = publish(
        control_fd,
        CONTROL_DATA,
        SNAPSHOT_NAME,
        payload,
        CONTROL_UID,
        CONTROL_GID,
        0o600,
    )
    evidence = json.dumps(
        {
            "sha256": digest,
            "bytes": len(payload),
            "source": os.path.join(AGENT_DATA, LEGACY_STATE_NAME),
            "provenance": provenance,
            "recordedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        },
        indent=2,
        sort_keys=True,
    ).encode("utf-8")
    publish(
        control_fd,
        CONTROL_DATA,
        SNAPSHOT_EVIDENCE_NAME,
        evidence,
        CONTROL_UID,
        CONTROL_GID,
        0o600,
    )
    return True


def refuse_when_rollback_is_recorded(control_fd):
    """Fail closed while a rollback is recorded but no state choice was made.

    After `--revert-isolation` the pre-isolation image owns `/data/runtime.json`
    and may have advanced it. Rolling forward would then either freeze that newer
    legacy state (the private copy wins) or discard the isolated run (the legacy
    copy wins). Neither is safe to pick silently, and the two cannot be merged,
    so the start is refused until the operator chooses.
    """
    if not exists(control_fd, ROLLBACK_MARKER_NAME):
        return

    detail = ""
    marker_fd = open_regular_file(control_fd, ROLLBACK_MARKER_NAME, CONTROL_DATA)
    if marker_fd is not None:
        try:
            body = read_all(marker_fd)
        finally:
            os.close(marker_fd)
        try:
            recorded = json.loads(body.decode("utf-8"))
            detail = " (rollback recorded at %s)" % recorded.get("publishedAt", "an unknown time")
        except (ValueError, UnicodeDecodeError):
            detail = ""

    raise LayoutError(
        "a rollback to the pre-isolation image is recorded in '%s'%s; the legacy '%s' may have been "
        "advanced by the old image and there is no automatic merge, so refusing to start and silently "
        "discard one of the two states. Choose which state survives with: %s"
        % (
            os.path.join(CONTROL_DATA, ROLLBACK_MARKER_NAME),
            detail,
            os.path.join(AGENT_DATA, LEGACY_STATE_NAME),
            RESUME_INSTRUCTION,
        )
    )


def record_controller_pid(pid_text):
    """Record the controller's own PID for operational inspection.

    The entrypoint passes its `$$` before `exec`, so the recorded PID is the one
    the controller process itself keeps - not tini's PID 1 and not a transient
    helper. The file is only meaningful while the container runs; the next start
    overwrites it before the controller is exec'd.
    """
    if not pid_text.isdigit() or int(pid_text) <= 0:
        raise LayoutError("'%s' is not a process id" % pid_text)
    if not os.path.isdir("/proc/%s" % pid_text):
        raise LayoutError("process %s does not exist; refusing to record it" % pid_text)

    control_fd = open_directory(CONTROL_DATA)
    try:
        publish(
            control_fd,
            CONTROL_DATA,
            CONTROLLER_PID_NAME,
            (pid_text + "\n").encode("ascii"),
            CONTROL_UID,
            CONTROL_GID,
            0o644,
        )
    finally:
        os.close(control_fd)


def prepare():
    opened = []

    def track(fd):
        if fd is not None:
            opened.append(fd)
        return fd

    try:
        # ------------------------------------------------------------------
        # Step 0: the rollback interlock. This reads the controller-private
        # volume only, so it happens before any metadata anywhere changes.
        # ------------------------------------------------------------------
        control_fd = track(open_directory(CONTROL_DATA))
        refuse_when_rollback_is_recorded(control_fd)

        # ------------------------------------------------------------------
        # Step 1: take the agent-writable parent away first. `/data` is a fixed
        # mount point, not a caller-supplied path, and O_NOFOLLOW|O_DIRECTORY
        # rejects a substituted symlink. After this the directory entries below
        # cannot be renamed or replaced while we work on them.
        # ------------------------------------------------------------------
        data_fd = track(open_directory(AGENT_DATA))
        apply(data_fd, 0, 0, 0o755, AGENT_DATA)

        # ------------------------------------------------------------------
        # Step 2: validate every entry in the now-frozen tree. Nothing inside
        # `/data` is modified until all of them are accepted.
        # ------------------------------------------------------------------
        agent_directories = [
            (name, track(open_child_directory(data_fd, name, AGENT_DATA)))
            for name in AGENT_SUBDIRECTORIES
        ]
        legacy_fd = track(open_regular_file(data_fd, LEGACY_STATE_NAME, AGENT_DATA))
        private_fd = track(open_regular_file(control_fd, PRIVATE_STATE_NAME, CONTROL_DATA))
        config_fd = track(open_directory(AGENT_CONFIG))

        # ------------------------------------------------------------------
        # Step 3: apply ownership to the validated inodes, by descriptor only.
        # ------------------------------------------------------------------
        for name, fd in agent_directories:
            path = os.path.join(AGENT_DATA, name)
            if fd is None:
                os.mkdir(name, 0o700, dir_fd=data_fd)
                fd = track(open_child_directory(data_fd, name, AGENT_DATA))
                if fd is None:
                    raise LayoutError("'%s' disappeared while being created" % path)
                log("created the missing agent directory '%s'" % path)
            apply(fd, AGENT_UID, AGENT_GID, 0o700, path)

        apply(control_fd, CONTROL_UID, CONTROL_GID, 0o700, CONTROL_DATA)
        apply(config_fd, CONTROL_UID, CONTROL_GID, 0o755, AGENT_CONFIG)

        # ------------------------------------------------------------------
        # Step 4: runtime state. Adopt once, snapshot the original once, and
        # keep the stale legacy path root-only for the rest of the isolated run.
        # ------------------------------------------------------------------
        legacy_path = os.path.join(AGENT_DATA, LEGACY_STATE_NAME)
        if legacy_fd is not None:
            legacy_info = os.fstat(legacy_fd)
            payload = read_all(legacy_fd)

            if private_fd is None:
                publish(
                    control_fd,
                    CONTROL_DATA,
                    PRIVATE_STATE_NAME,
                    payload,
                    CONTROL_UID,
                    CONTROL_GID,
                    0o600,
                )
                log("adopted legacy runtime state into the controller-private store")
                if record_pre_isolation_snapshot(control_fd, payload, "pre-isolation-adoption"):
                    log(
                        "preserved the pre-isolation runtime state as '%s' with hash evidence"
                        % os.path.join(CONTROL_DATA, SNAPSHOT_NAME)
                    )
            elif legacy_info.st_uid != 0:
                # Root ownership is what the isolated run leaves behind. Any other
                # owner means something wrote this path outside isolation - almost
                # certainly the pre-isolation image after an unrecorded rollback -
                # and the two states cannot be merged automatically.
                raise LayoutError(
                    "'%s' is owned by UID %d while controller-private state exists; it was written "
                    "outside isolation and there is no automatic merge. Choose which state survives "
                    "with: %s" % (legacy_path, legacy_info.st_uid, RESUME_INSTRUCTION)
                )
            elif record_pre_isolation_snapshot(control_fd, payload, "pre-isolation-backfill"):
                # A deployment isolated before snapshots existed: the root-owned
                # legacy file is still the untouched original, so record it now.
                log(
                    "preserved the existing pre-isolation runtime state as '%s' with hash evidence"
                    % os.path.join(CONTROL_DATA, SNAPSHOT_NAME)
                )

            # The stale legacy copy carries the tmux owner token from the state it
            # was captured at, and the agent identity must not be able to read it
            # while the isolated image runs. The bytes are left untouched;
            # `--revert-isolation` replaces them with the current private state.
            apply(legacy_fd, 0, 0, 0o600, legacy_path)
    except LayoutError as error:
        log(str(error))
        return 1
    except OSError as error:
        log("layout preparation failed: %s" % error)
        return 1
    finally:
        for fd in opened:
            try:
                os.close(fd)
            except OSError:
                pass

    return 0


def main(argv):
    if argv[:1] == ["--record-controller-pid"]:
        if len(argv) != 2:
            log("--record-controller-pid takes exactly one process id")
            return 2
        try:
            record_controller_pid(argv[1])
        except LayoutError as error:
            log("could not record the controller PID: %s" % error)
            return 1
        except OSError as error:
            log("could not record the controller PID: %s" % error)
            return 1
        return 0

    if argv:
        log("unexpected argument '%s'" % argv[0])
        return 2

    return prepare()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
