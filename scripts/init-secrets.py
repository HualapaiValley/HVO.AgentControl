#!/usr/bin/env python3
"""Create, re-own, roll back or resume the owner password and isolation state on
the selected Docker host, without ever printing a credential."""
import argparse
import json
import re
import subprocess
import sys

DEFAULT_PROJECT = "agentcontrol-v2"
DEFAULT_SECRET_VOLUME = "agentcontrol-v2-secrets"
TARGET = "/secrets/owner-password"

# Compose derives volume and container names from the project name. A malformed
# project would otherwise produce names that silently do not exist, and the
# rollback would report success against nothing.
PROJECT_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]*$")

# Identity that owns the controller-private state and the owner secret. It is
# deliberately distinct from the agent identity (UID/GID 1000) that runs
# OpenCode, tmux and the terminal bridge.
CONTROL_UID = 1001
CONTROL_GID = 1001

# The pre-isolation shared identity. Rollback restores it so the old image -
# which runs everything as UID 1000 - can read its own state again.
LEGACY_UID = 1000
LEGACY_GID = 1000

# Runs inside the secrets volume's container. It never prints the credential.
# A generated password is written to a private temporary file, assigned the
# final ownership and permissions, and only then hard-linked to the known path,
# so the known path is never a partially written file. Any existing file is
# preserved: a usable one is accepted, and a blank/short one aborts.
REMOTE_CODE = '''\
import os
import secrets
import sys
import tempfile

path = sys.argv[1]
control_uid = int(sys.argv[2])
control_gid = int(sys.argv[3])
minimum = 24
directory = os.path.dirname(path) or "."

if os.path.lexists(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            existing = handle.read().strip()
    except OSError:
        print("existing owner password file could not be read; refusing to overwrite", file=sys.stderr)
        raise SystemExit(1)
    if len(existing) < minimum:
        print("existing owner password file is blank or shorter than 24 characters; refusing to overwrite", file=sys.stderr)
        raise SystemExit(1)
    print("Existing owner password preserved.")
    raise SystemExit(0)

fd, temp_path = tempfile.mkstemp(prefix=".owner-password.", dir=directory)
try:
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        stream.write(secrets.token_urlsafe(32) + "\\n")
    os.chmod(temp_path, 0o600)
    if os.name == "posix" and os.geteuid() == 0:
        os.chown(temp_path, control_uid, control_gid)
    try:
        os.link(temp_path, path)
    except FileExistsError:
        print("owner password file appeared while creating; refusing to overwrite", file=sys.stderr)
        raise SystemExit(1)
finally:
    try:
        os.unlink(temp_path)
    except OSError:
        pass

print("Created owner password (contents not printed).")
'''

# Re-owns an existing secret from the pre-isolation shared identity (UID 1000)
# to the controller identity. This is a metadata-only change: the file is never
# read, rewritten, rotated or replaced, and the inode is left untouched, so the
# operation is reversible by chowning it back. It refuses to act on a symlink,
# a non-regular file, or a file owned by an unexpected identity, and it verifies
# the resulting ownership and mode before reporting success.
MIGRATE_CODE = '''\
import os
import stat
import sys

path = sys.argv[1]
control_uid = int(sys.argv[2])
control_gid = int(sys.argv[3])

if not os.path.lexists(path):
    print("owner password file does not exist; run without --migrate-owner first", file=sys.stderr)
    raise SystemExit(1)

info = os.lstat(path)
if stat.S_ISLNK(info.st_mode):
    print("owner password path is a symlink; refusing to change ownership", file=sys.stderr)
    raise SystemExit(1)
if not stat.S_ISREG(info.st_mode):
    print("owner password path is not a regular file; refusing to change ownership", file=sys.stderr)
    raise SystemExit(1)
if info.st_nlink != 1:
    print("owner password file has extra hard links; refusing to change ownership", file=sys.stderr)
    raise SystemExit(1)

if info.st_uid == control_uid and info.st_gid == control_gid:
    if stat.S_IMODE(info.st_mode) not in (0o600, 0o400):
        os.chmod(path, 0o600)
    print("Owner password already belongs to the controller identity.")
    raise SystemExit(0)

# 1000 is the pre-isolation shared identity; 0 covers a root-created secret.
if info.st_uid not in (0, 1000):
    print("owner password file has an unexpected owner; refusing to change ownership", file=sys.stderr)
    raise SystemExit(1)

previous_uid = info.st_uid
previous_gid = info.st_gid

# Open by descriptor so the checked inode is the one modified.
fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
try:
    opened = os.fstat(fd)
    if (opened.st_ino, opened.st_dev) != (info.st_ino, info.st_dev):
        print("owner password file changed during migration; refusing to change ownership", file=sys.stderr)
        raise SystemExit(1)
    os.fchown(fd, control_uid, control_gid)
    os.fchmod(fd, 0o600)
    verified = os.fstat(fd)
finally:
    os.close(fd)

if verified.st_uid != control_uid or verified.st_gid != control_gid:
    print("ownership verification failed after migration", file=sys.stderr)
    raise SystemExit(1)
if stat.S_IMODE(verified.st_mode) != 0o600:
    print("permission verification failed after migration", file=sys.stderr)
    raise SystemExit(1)
if verified.st_size != info.st_size or verified.st_ino != info.st_ino:
    print("owner password contents changed during migration", file=sys.stderr)
    raise SystemExit(1)

print(
    "Owner password re-owned from %d:%d to %d:%d (contents unchanged; revert with --revert-isolation)."
    % (previous_uid, previous_gid, control_uid, control_gid)
)
'''

# ---------------------------------------------------------------------------
# Rollback to the pre-isolation image.
#
# Ownership hand-back alone is NOT a correct rollback for the runtime state. The
# isolated controller writes /control-data/runtime.json and never updates
# /data/runtime.json, so by the time an operator rolls back the legacy path holds
# whatever the state was at adoption - an old organization, an old session id and
# an old tmux owner token. Handing that stale file back to UID 1000 would resume
# the old image on a stale session and silently lose everything the isolated run
# did.
#
# So the rollback is split by target:
#
#   /data/runtime.json      REPUBLISHED from the current controller-private state
#                           and then owned 1000:1000, mode 0600. This is a real
#                           write, done atomically inside the (root-owned) data
#                           directory through a directory descriptor.
#   /data                   metadata only: root -> 1000:1000, mode 0755, so the
#                           old single-identity image can create its own entries.
#   /secrets/owner-password metadata only: 1001:1001 -> 1000:1000, mode 0600. The
#                           credential is never read, rotated or replaced.
#
# The original pre-isolation state is NOT the thing being handed back and does
# not need to be: prepare-layout.py preserved it byte-exactly, with a SHA-256, as
# /control-data/runtime.pre-isolation.json at first adoption.
#
# Cross-volume atomicity does not exist. The runtime republish is atomic within
# the data volume, but the three volumes cannot be changed as one transaction, so
# this operation is written to be replayable instead: every step is idempotent,
# nothing is deleted, and a failure part-way through is repaired by running the
# exact same command again.
#
# "Replayable" is bounded, and the bound is the point of the marker check below.
# A replay is only safe while the legacy state is still what this rollback put
# there. Once the pre-isolation image has actually run it advances
# /data/runtime.json, and republishing the (now older) controller-private bytes
# over it would destroy the work the old image did - the precise data loss the
# roll-forward interlock exists to prevent, arrived at from the other direction.
#
# That bound only holds if the marker is durable before ANY of this operation's
# effects are reachable by the pre-isolation identity, and publishing the runtime
# state is one of those effects, not a preparation for them. The publication
# creates a new inode and renames it over /data/runtime.json; giving that inode
# its 1000:1000 ownership at creation time is an ownership hand-back like any
# other, and doing it before the marker existed left exactly the unbounded window
# the marker is supposed to close: a crash in between produced a legacy file the
# old image could read and advance with no record of what had been published, so
# the next replay saw no marker at all, treated the advanced state as the ordinary
# first rollback, and overwrote it.
#
# So the order is: decide, record the INTENT marker, publish root-owned, hand
# back. The marker is written before the first byte is published and before every
# ownership hand-back, and it carries three digests so a replay can classify what
# it finds without guessing:
#
#   prepublicationLegacySha256  what /data/runtime.json held before this rollback
#                               touched it (null when there was no legacy file).
#   intendedSha256              the controller-private bytes this rollback will
#                               publish (null when nothing will be published:
#                               --accept-missing-runtime-state, or a rollback out
#                               of an unrecorded divergence that keeps the legacy
#                               bytes).
#   publishedSha256             what /data/runtime.json actually holds once the
#                               rollback has completed; null while phase=="intent".
#
# A replay then reads the legacy path and matches:
#
#   legacy == prepublicationLegacySha256  the publication never happened. Proceed
#                                         and publish; nothing is lost.
#   legacy == intendedSha256              the publication already landed (a crash
#                                         after the root-owned publish, or after a
#                                         completed rollback). Converged: the inode
#                                         is NOT replaced, only the remaining
#                                         hand-backs are re-applied.
#   neither                               the old image - or some other writer -
#                                         advanced the legacy state. Refuse before
#                                         the first write or chown and route the
#                                         operator to --resume-isolation.
#
# Recording first inverts every failure mode into a safe one: a marker that cannot
# be published aborts while /data/runtime.json still has its prior owner and prior
# bytes, so the old image cannot start at all; a crash after the marker but before
# the publish is classified by prepublicationLegacySha256 and re-runs cleanly; a
# crash after the root-owned publish but before the hand-back is classified by
# intendedSha256 and only finishes the hand-backs.
#
# The reason field distinguishes the two markers that can be on disk. This one is
# "operator-rollback". prepare-layout.py writes "unrecorded-legacy-divergence" for
# a divergence nobody rolled back; that marker records no publication intent at
# all, and reading it as one would republish over the diverged legacy bytes the
# operator is about to choose. It is branched on explicitly, never inferred.
REVERT_CODE = '''\
import hashlib
import json
import os
import secrets
import stat
import sys
import time

secret_path = sys.argv[1]
legacy_uid = int(sys.argv[2])
legacy_gid = int(sys.argv[3])
data_root = sys.argv[4]
private_root = sys.argv[5]
allow_missing_state = sys.argv[6] == "1"

control_uid = 1001
control_gid = 1001
state_name = "runtime.json"
marker_name = "rollback.active"
snapshot_name = "runtime.pre-isolation.json"

legacy_state = os.path.join(data_root, state_name)
private_state = os.path.join(private_root, state_name)

OPEN_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC


def fail(message):
    print(message, file=sys.stderr)
    raise SystemExit(1)


def open_checked(path, directory=False, allowed_owners=None, parent_fd=None, name=None):
    """Open without following a final symlink; None when absent.

    Hard links, non-regular files and unexpected owners are refused: an operator
    rollback must never hand an attacker-substituted inode to UID 1000.
    """
    flags = OPEN_FLAGS | (os.O_DIRECTORY if directory else os.O_NONBLOCK)
    try:
        if parent_fd is None:
            fd = os.open(path, flags)
        else:
            fd = os.open(name, flags, dir_fd=parent_fd)
    except FileNotFoundError:
        return None
    except OSError as error:
        fail("%s could not be opened (%s); refusing to revert" % (path, error.strerror))

    try:
        info = os.fstat(fd)
        if directory:
            if not stat.S_ISDIR(info.st_mode):
                fail("%s is not a directory; refusing to revert" % path)
        else:
            if not stat.S_ISREG(info.st_mode):
                fail("%s is not a regular file; refusing to revert" % path)
            if info.st_nlink != 1:
                fail("%s has extra hard links; refusing to revert" % path)
        if allowed_owners is not None and info.st_uid not in allowed_owners:
            fail(
                "%s is owned by UID %d; expected one of %s. Refusing to revert an "
                "unexpected layout." % (path, info.st_uid, sorted(allowed_owners))
            )
    except BaseException:
        os.close(fd)
        raise
    return fd


def read_all(fd):
    os.lseek(fd, 0, os.SEEK_SET)
    chunks = []
    while True:
        chunk = os.read(fd, 65536)
        if not chunk:
            return b"".join(chunks)
        chunks.append(chunk)


def hand_back_metadata(fd, path, uid, gid, mode):
    """Change ownership and mode only. No read, no write, no replacement."""
    before = os.fstat(fd)
    os.fchown(fd, uid, gid)
    os.fchmod(fd, mode)
    after = os.fstat(fd)
    if after.st_uid != uid or after.st_gid != gid:
        fail("ownership verification failed for %s" % path)
    if stat.S_IMODE(after.st_mode) != mode:
        fail("permission verification failed for %s" % path)
    if after.st_ino != before.st_ino or after.st_size != before.st_size:
        fail("%s changed during the revert" % path)
    print(
        "Reverted %s from %d:%d to %d:%d (contents unchanged)."
        % (path, before.st_uid, before.st_gid, uid, gid)
    )


def create_temporary(directory_fd, name):
    """Create a private temporary in `directory_fd` and return (fd, name).

    The suffix is random rather than the PID: a PID repeats across container
    runs, and a leftover temporary from an interrupted run would then make
    O_EXCL fail permanently on the reused name. Retrying on a collision keeps
    the create-exclusive guarantee without ever reusing, truncating or removing
    an existing entry.
    """
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
    for _ in range(64):
        temporary = ".%s.%s.tmp" % (name, secrets.token_hex(8))
        try:
            return os.open(temporary, flags, 0o600, dir_fd=directory_fd), temporary
        except FileExistsError:
            continue
    fail("could not create a private temporary for %s" % name)


def publish_bytes(directory_fd, directory_path, name, payload, uid, gid, mode):
    """Atomically publish bytes into a directory, by descriptor only.

    The temporary is created inside the destination directory with O_EXCL and
    O_NOFOLLOW, given its final ownership and mode while still invisible under
    the published name, fsynced, and then renamed over the target with renameat
    on the same directory descriptor. The directory is fsynced afterwards so the
    rename cannot be lost. A concurrent reader sees the old file or the complete
    new one; no path is ever resolved, so nothing can be redirected by a symlink.
    """
    fd, temporary = create_temporary(directory_fd, name)
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

    check_fd = open_checked(
        os.path.join(directory_path, name), parent_fd=directory_fd, name=name
    )
    if check_fd is None:
        fail("%s disappeared immediately after publication" % os.path.join(directory_path, name))
    try:
        written = read_all(check_fd)
        info = os.fstat(check_fd)
    finally:
        os.close(check_fd)
    if written != payload:
        fail("%s does not match the bytes that were published" % os.path.join(directory_path, name))
    if info.st_uid != uid or info.st_gid != gid or stat.S_IMODE(info.st_mode) != mode:
        fail("%s does not carry the intended ownership or mode" % os.path.join(directory_path, name))
    return hashlib.sha256(payload).hexdigest()


# ---------------------------------------------------------------------------
# Validate every path first. A rollback that re-owns the secret and then aborts
# on the runtime state leaves a half-reverted deployment that neither image can
# start cleanly, so nothing is changed until all targets are accepted.
# ---------------------------------------------------------------------------
descriptors = []
try:
    # The secret: 1001 while isolated, 1000 when a previous run already handed it
    # back (a replayed rollback must be accepted, not refused), 0 when root
    # created it.
    secret_fd = open_checked(secret_path, allowed_owners=(control_uid, legacy_uid, 0))
    if secret_fd is None:
        fail("owner password file does not exist; nothing to revert")
    descriptors.append(secret_fd)

    # The agent data root: root-owned while isolated, 1000 after a previous run.
    data_fd = open_checked(data_root, directory=True, allowed_owners=(0, legacy_uid))
    if data_fd is None:
        fail("%s does not exist; the agent data volume is not mounted" % data_root)
    descriptors.append(data_fd)

    # The controller-private store holds the authoritative state of the isolated
    # run. It is the source of the republish, never a target of it.
    private_dir_fd = open_checked(
        private_root, directory=True, allowed_owners=(control_uid, 0)
    )
    if private_dir_fd is None:
        fail("%s does not exist; the controller-private volume is not mounted" % private_root)
    descriptors.append(private_dir_fd)

    private_fd = open_checked(
        private_state,
        allowed_owners=(control_uid, 0),
        parent_fd=private_dir_fd,
        name=state_name,
    )
    if private_fd is not None:
        descriptors.append(private_fd)
    elif not allow_missing_state:
        fail(
            "%s does not exist, so there is no current runtime state to hand back. The isolated "
            "controller writes it at startup; a deployment that never started has none. Re-run with "
            "--accept-missing-runtime-state to hand back only the ownership and let the old image "
            "create a new organization." % private_state
        )

    # The stale legacy copy. Its bytes are about to be replaced, so its presence
    # is optional - but a substituted inode still has to be refused.
    legacy_fd = open_checked(
        legacy_state,
        allowed_owners=(0, legacy_uid),
        parent_fd=data_fd,
        name=state_name,
    )
    if legacy_fd is not None:
        descriptors.append(legacy_fd)

    payload = read_all(private_fd) if private_fd is not None else None
    legacy_payload = read_all(legacy_fd) if legacy_fd is not None else None
    legacy_digest = (
        hashlib.sha256(legacy_payload).hexdigest() if legacy_payload is not None else None
    )
    # Recorded in the marker so an operator can see whether the legacy path was
    # still root-owned (isolated, nothing handed back) or already UID 1000 when
    # this rollback started.
    legacy_owner_uid = None if legacy_fd is None else os.fstat(legacy_fd).st_uid

    # ------------------------------------------------------------------
    # A recorded rollback bounds the replay. Validate it before the first
    # write or chown: once the pre-isolation image has run it owns
    # /data/runtime.json and may have advanced it, and republishing the older
    # controller-private bytes over that would destroy the newer state - the
    # same data loss the roll-forward interlock refuses, reached from the
    # rollback side.
    #
    # The classification is three-way and explicit, because "not what we
    # published" and "not yet published" are different situations with opposite
    # correct actions:
    #
    #   legacy == prepublicationLegacySha256  publication did not happen; publish.
    #   legacy == intendedSha256              publication already landed; converged,
    #                                         do not replace the inode.
    #   neither                               someone else advanced it; refuse.
    #
    # intendedSha256 is what makes the crash window between the marker and the
    # ownership hand-back recoverable: the bytes are already on the legacy path,
    # owned by root, and the replay has to recognise them as its own work rather
    # than as a foreign writer.
    # ------------------------------------------------------------------
    keep_legacy_bytes = False
    already_published = False
    marker_fd = open_checked(
        os.path.join(private_root, marker_name),
        allowed_owners=(control_uid, 0),
        parent_fd=private_dir_fd,
        name=marker_name,
    )
    if marker_fd is not None:
        try:
            marker_body = read_all(marker_fd)
        finally:
            os.close(marker_fd)

        try:
            recorded = json.loads(marker_body.decode("utf-8"))
            if not isinstance(recorded, dict):
                raise ValueError("marker is not an object")
            recorded_digest = recorded.get("publishedSha256")
            recorded_intended = recorded.get("intendedSha256")
            recorded_prepublication = recorded.get("prepublicationLegacySha256")
            recorded_legacy_digest = recorded.get("legacySha256")
            recorded_at = recorded.get("publishedAt", "an unknown time")
            recorded_reason = recorded.get("reason", "rollback")
            recorded_phase = recorded.get("phase", "complete")
        except (ValueError, UnicodeDecodeError) as error:
            # Fail closed. A marker that cannot be read cannot bound the replay,
            # and this operation writes over the state the old image may hold.
            fail(
                "%s exists but could not be read as rollback evidence (%s). Refusing to republish "
                "over %s on an unverifiable record. Resolve the interlock explicitly instead: "
                "--resume-isolation --state-source legacy|private."
                % (os.path.join(private_root, marker_name), error, legacy_state)
            )

        if recorded_reason == "unrecorded-legacy-divergence":
            # prepare-layout's marker. It records no publication and no
            # publication intent - nothing was ever written to the legacy path -
            # so it must never be read as a rollback whose publication can be
            # replayed. Rolling back out of this state is legitimate and keeps
            # the diverged legacy bytes: the operator chose them by rolling back
            # at all. The private state is retained either way.
            #
            # legacySha256 is this marker's own record of the diverged bytes and
            # is deliberately a different field from the operator-rollback
            # digests. Reading it as a publication record would republish over
            # exactly the bytes the operator is keeping.
            if legacy_fd is None:
                # The diverged file is gone. There is nothing to keep, so the
                # ordinary publication path applies.
                pass
            elif recorded_legacy_digest is not None and legacy_digest == recorded_legacy_digest:
                keep_legacy_bytes = True
                print(
                    "%s records an unrecorded divergence (%s) and %s still holds exactly the "
                    "diverged bytes it recorded. Rolling back keeps them: the legacy state is "
                    "handed back unmodified and the controller-private state is retained, not "
                    "republished over it."
                    % (os.path.join(private_root, marker_name), recorded_at, legacy_state)
                )
            else:
                fail(
                    "an unrecorded divergence is recorded in %s (%s) but %s no longer holds the "
                    "bytes it recorded (recorded sha256 %s, found sha256 %s). Something advanced "
                    "the legacy state after the divergence was detected, so neither keeping it nor "
                    "republishing over it is a decision this command may take on its own. Nothing "
                    "was changed. Resolve it explicitly with: --resume-isolation --state-source "
                    "legacy|private."
                    % (
                        os.path.join(private_root, marker_name),
                        recorded_at,
                        legacy_state,
                        recorded_legacy_digest if recorded_legacy_digest is not None else "none",
                        legacy_digest,
                    )
                )
        elif legacy_fd is None:
            # Nothing on the legacy path to overwrite, so publishing cannot
            # destroy anything. This is also the replay of an interrupted
            # rollback for a deployment that never had a legacy file.
            pass
        elif recorded_intended is not None and legacy_digest == recorded_intended:
            # The publication already landed: either the crash window between the
            # root-owned publish and the ownership hand-back, or an ordinary
            # replay of a completed rollback. Converged - the inode is not
            # replaced, only the remaining hand-backs are re-applied.
            already_published = True
        elif recorded_digest is not None and legacy_digest == recorded_digest:
            # A completed rollback recorded by this version, or by an older one
            # that wrote only publishedSha256.
            already_published = True
        elif recorded_prepublication is not None and legacy_digest == recorded_prepublication:
            # The marker is durable but the publication never happened. The
            # legacy path still holds exactly what it held before this rollback
            # started, so publishing over it loses nothing.
            print(
                "%s records a rollback at %s whose publication did not complete; %s still holds "
                "the bytes recorded before it (sha256 %s). Resuming the publication."
                % (
                    os.path.join(private_root, marker_name),
                    recorded_at,
                    legacy_state,
                    legacy_digest,
                )
            )
        elif payload is not None and legacy_payload == payload:
            # Converged against the current private state even though the marker
            # predates the intent fields. Still a replay, not a foreign write.
            already_published = True
        elif (
            recorded_intended is None
            and recorded_digest is None
            and recorded_prepublication is None
        ):
            # A marker carrying no digest at all cannot bound anything, and this
            # command is about to write over state the old image may hold. Fail
            # closed rather than guess which side the legacy bytes came from.
            fail(
                "a rollback is recorded in %s (%s, reason '%s') but it records no digest for %s, "
                "so a replay cannot tell an unfinished publication from a legacy state the old "
                "image advanced. Nothing was changed. Resolve it explicitly with: "
                "--resume-isolation --state-source legacy|private."
                % (
                    os.path.join(private_root, marker_name),
                    recorded_at,
                    recorded_reason,
                    legacy_state,
                )
            )
        else:
            fail(
                "a rollback is already recorded in %s (%s, reason '%s', phase '%s') and %s matches "
                "neither the state recorded before the publication (sha256 %s) nor the state the "
                "rollback intended to publish (sha256 %s); it is sha256 %s. The pre-isolation image "
                "- or another writer - has advanced the legacy state, so republishing the "
                "controller-private copy over it would discard that work. Nothing was changed, and "
                "the deployment is already reverted: %s and the owner secret belong to UID %d and "
                "the old image can run. To roll forward to the isolated image instead, choose "
                "which state survives explicitly with: --resume-isolation --state-source "
                "legacy|private."
                % (
                    os.path.join(private_root, marker_name),
                    recorded_at,
                    recorded_reason,
                    recorded_phase,
                    legacy_state,
                    recorded_prepublication if recorded_prepublication is not None else "none",
                    recorded_intended
                    if recorded_intended is not None
                    else (recorded_digest if recorded_digest is not None else "none"),
                    legacy_digest,
                    data_root,
                    legacy_uid,
                )
            )

    # ------------------------------------------------------------------
    # Apply. Order is deliberate and each step is idempotent on a re-run.
    #
    # 1. Decide what the legacy path will hold, WITHOUT writing anything.
    # 2. Record the intent marker - before the first byte is published and
    #    before any ownership is handed back. The marker is the roll-forward
    #    interlock and the evidence every later replay classifies against, so it
    #    has to be durable before any effect of this operation is reachable by
    #    the pre-isolation identity. Publishing the runtime state IS such an
    #    effect: the publication creates the inode already owned 1000:1000, so
    #    doing it before the marker left a crash window in which the old image
    #    could read and advance a state with no record of it, and the next
    #    replay - finding no marker - would treat that advanced state as the
    #    ordinary first rollback and overwrite it.
    # 3. Publish the runtime state root-owned 0600 while both images are
    #    stopped, so the old image can neither observe a half-written file nor
    #    read one this run has not finished committing to.
    # 4. Hand back the runtime state explicitly, then /data, which is what lets
    #    the old image write.
    # 5. Hand back the secret last: it is the step that makes the old image able
    #    to authenticate, so it should not precede a failure in the others.
    # 6. Complete the marker with what actually landed.
    #
    # A failure at step 2 therefore leaves a deployment neither image can start
    # from a half-reverted state: the legacy state keeps its prior owner and its
    # prior bytes, /data and the secret still belong to the controller, and
    # re-running the identical command converges.
    # ------------------------------------------------------------------
    marker_path = os.path.join(private_root, marker_name)

    def write_marker(phase, published_digest, published_bytes, intended_digest, intended_bytes):
        """Publish the rollback marker atomically, controller-owned 0600.

        `phase` is "intent" before the legacy path has been written and
        "complete" once it holds what this rollback decided. A marker is durable
        at both points, so a crash in between is classified rather than guessed
        at: `prepublicationLegacySha256` identifies "the publication did not
        happen", `intendedSha256` identifies "it did".
        """
        body = {
            "publishedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "reason": "operator-rollback",
            "phase": phase,
            "schema": 2,
            # What the legacy path held before this rollback touched it. A replay
            # that finds exactly this knows the publication never happened.
            "prepublicationLegacySha256": legacy_digest,
            "prepublicationLegacyBytes": (
                None if legacy_payload is None else len(legacy_payload)
            ),
            "prepublicationLegacyOwnerUid": legacy_owner_uid,
            # What this rollback will put there. A replay that finds exactly this
            # knows the publication landed and must not replace the inode.
            "intendedSha256": intended_digest,
            "intendedBytes": intended_bytes,
            # What is actually there now; null while phase == "intent".
            "publishedSha256": published_digest,
            "publishedBytes": published_bytes,
            "legacyBytesRetained": keep_legacy_bytes,
            "privateStateRetained": private_fd is not None,
            "legacyPath": legacy_state,
            "privatePath": private_state,
        }
        publish_bytes(
            private_dir_fd,
            private_root,
            marker_name,
            (json.dumps(body, indent=2, sort_keys=True) + "\\n").encode("utf-8"),
            control_uid,
            control_gid,
            0o600,
        )

    # Step 1: decide. Nothing is written in this block.
    if keep_legacy_bytes:
        # A recorded, unrecorded-origin divergence the operator is rolling back
        # into: the legacy bytes are the survivor, so only ownership moves.
        intended_digest = legacy_digest
        intended_bytes = len(legacy_payload)
        will_publish = False
    elif already_published or (payload is not None and legacy_payload == payload):
        # The legacy path already carries the bytes this rollback would write -
        # a replay after the publication landed. Rewriting identical bytes would
        # replace the inode for nothing, so only the hand-backs are re-applied.
        intended_digest = legacy_digest
        intended_bytes = 0 if legacy_payload is None else len(legacy_payload)
        will_publish = False
    elif payload is not None:
        intended_digest = hashlib.sha256(payload).hexdigest()
        intended_bytes = len(payload)
        will_publish = True
    else:
        # --accept-missing-runtime-state: ownership only. There is no intended
        # publication, and the marker says so rather than recording the bytes
        # that happen to be on the legacy path as an intent this run never had.
        intended_digest = None
        intended_bytes = None
        will_publish = False

    # Step 2: the marker, before the first write and before every hand-back.
    write_marker("intent", None, None, intended_digest, intended_bytes)

    # Step 3: publish root-owned. The old image must not be able to read a state
    # this run has not yet handed back; the hand-back below is the explicit,
    # separately verified step that makes it reachable.
    republished = False
    if will_publish:
        published_digest = publish_bytes(
            data_fd, data_root, state_name, payload, 0, 0, 0o600
        )
        republished = True
        print(
            "Published the current controller-private runtime state to %s root-owned "
            "(%d bytes, sha256 %s); it is handed to %d:%d below."
            % (legacy_state, len(payload), published_digest, legacy_uid, legacy_gid)
        )
    elif keep_legacy_bytes:
        published_digest = legacy_digest
    elif intended_digest is not None:
        published_digest = legacy_digest
        print(
            "%s already carries the current controller-private runtime state "
            "(%d bytes, sha256 %s); it was not rewritten."
            % (legacy_state, intended_bytes, published_digest)
        )
    else:
        published_digest = legacy_digest
        print(
            "No controller-private runtime state; %s was not republished and the old image will "
            "create a new organization on start." % legacy_state,
            file=sys.stderr,
        )
    if published_digest is None:
        published_bytes = None
    elif will_publish:
        published_bytes = intended_bytes
    else:
        published_bytes = 0 if legacy_payload is None else len(legacy_payload)

    # Step 4: hand the runtime state back explicitly.
    #
    # A republish replaced the inode, so the descriptor opened during validation
    # refers to a superseded one and must never be chowned - that would hand the
    # OLD inode to UID 1000 while the published one stayed root-owned. Re-open
    # the published name by directory descriptor instead, under the same
    # no-follow/hard-link/owner rules as every other path here.
    if republished:
        published_fd = open_checked(
            legacy_state, allowed_owners=(0,), parent_fd=data_fd, name=state_name
        )
        if published_fd is None:
            fail("%s disappeared between its publication and its hand-back" % legacy_state)
        try:
            if hashlib.sha256(read_all(published_fd)).hexdigest() != published_digest:
                fail(
                    "%s no longer holds the bytes that were just published; refusing to hand it "
                    "back." % legacy_state
                )
            hand_back_metadata(published_fd, legacy_state, legacy_uid, legacy_gid, 0o600)
        finally:
            os.close(published_fd)
    elif legacy_fd is not None:
        hand_back_metadata(legacy_fd, legacy_state, legacy_uid, legacy_gid, 0o600)

    # Step 5.
    hand_back_metadata(data_fd, data_root, legacy_uid, legacy_gid, 0o755)
    hand_back_metadata(secret_fd, secret_path, legacy_uid, legacy_gid, 0o600)

    # Step 6: record what actually landed. A crash before this leaves the intent
    # marker, which the replay classifies by intendedSha256 and finishes.
    write_marker("complete", published_digest, published_bytes, intended_digest, intended_bytes)
finally:
    for descriptor in descriptors:
        os.close(descriptor)

print(
    "Controller-private %s is retained unchanged; it is still the state of the isolated run."
    % private_state
)
if os.path.lexists(os.path.join(private_root, snapshot_name)):
    print(
        "The byte-exact pre-isolation state remains preserved at %s."
        % os.path.join(private_root, snapshot_name)
    )
print(
    "Rollback complete. Start the pre-isolation image now. Rolling forward to the isolated image "
    "again requires an explicit state choice: --resume-isolation --state-source legacy|private."
)
'''

# ---------------------------------------------------------------------------
# Roll forward again after a rollback, with an explicit state choice.
#
# Once the pre-isolation image has run, /data/runtime.json may have advanced past
# the controller-private copy. The two are independent histories of the same
# organization and there is no automatic merge, so the isolated image refuses to
# start while /control-data/rollback.active exists. This is the only operation
# that clears it, and it cannot be performed without naming the state that
# survives. The other state is never deleted: it is preserved next to the private
# store under a timestamped name.
#
# "Never deleted" has to survive a name collision. The archive name carries a
# one-second timestamp, so two resumes inside the same second - or two resumes
# after a clock step - would pick the same name, and an os.replace would then
# silently destroy the first archive while reporting that it had preserved both.
# Archives are therefore published create-never-replace: the bytes are written to
# a private temporary and linked into the first archive name that does not exist
# yet (os.link fails with EEXIST rather than overwriting, so the check and the
# claim are one atomic operation). Two concurrent resumes keep two archives.
RESUME_CODE = '''\
import hashlib
import json
import os
import secrets
import stat
import sys
import time

secret_path = sys.argv[1]
control_uid = int(sys.argv[2])
control_gid = int(sys.argv[3])
data_root = sys.argv[4]
private_root = sys.argv[5]
source = sys.argv[6]

state_name = "runtime.json"
marker_name = "rollback.active"
legacy_state = os.path.join(data_root, state_name)
private_state = os.path.join(private_root, state_name)

OPEN_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC


def fail(message):
    print(message, file=sys.stderr)
    raise SystemExit(1)


def open_checked(path, directory=False, allowed_owners=None, parent_fd=None, name=None):
    flags = OPEN_FLAGS | (os.O_DIRECTORY if directory else os.O_NONBLOCK)
    try:
        if parent_fd is None:
            fd = os.open(path, flags)
        else:
            fd = os.open(name, flags, dir_fd=parent_fd)
    except FileNotFoundError:
        return None
    except OSError as error:
        fail("%s could not be opened (%s); refusing to resume" % (path, error.strerror))

    try:
        info = os.fstat(fd)
        if directory:
            if not stat.S_ISDIR(info.st_mode):
                fail("%s is not a directory; refusing to resume" % path)
        else:
            if not stat.S_ISREG(info.st_mode):
                fail("%s is not a regular file; refusing to resume" % path)
            if info.st_nlink != 1:
                fail("%s has extra hard links; refusing to resume" % path)
        if allowed_owners is not None and info.st_uid not in allowed_owners:
            fail(
                "%s is owned by UID %d; expected one of %s. Refusing to resume an "
                "unexpected layout." % (path, info.st_uid, sorted(allowed_owners))
            )
    except BaseException:
        os.close(fd)
        raise
    return fd


def read_all(fd):
    os.lseek(fd, 0, os.SEEK_SET)
    chunks = []
    while True:
        chunk = os.read(fd, 65536)
        if not chunk:
            return b"".join(chunks)
        chunks.append(chunk)


def write_temporary(directory_fd, name, payload, uid, gid, mode):
    """Write `payload` to a private temporary in `directory_fd`, fully durable.

    The suffix is random rather than the PID, because a PID repeats across
    container runs: a temporary left behind by an interrupted run would make
    O_EXCL fail permanently on the reused name, and nothing here may unlink or
    truncate an entry it did not create. Returns the temporary's name.
    """
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
    fd = None
    temporary = None
    for _ in range(64):
        candidate = ".%s.%s.tmp" % (name, secrets.token_hex(8))
        try:
            fd = os.open(candidate, flags, 0o600, dir_fd=directory_fd)
            temporary = candidate
            break
        except FileExistsError:
            continue
    if fd is None:
        fail("could not create a private temporary for %s" % name)

    try:
        offset = 0
        while offset < len(payload):
            offset += os.write(fd, payload[offset:])
        os.fchown(fd, uid, gid)
        os.fchmod(fd, mode)
        os.fsync(fd)
    except BaseException:
        try:
            os.unlink(temporary, dir_fd=directory_fd)
        except OSError:
            pass
        raise
    finally:
        os.close(fd)
    return temporary


def publish_bytes(directory_fd, directory_path, name, payload, uid, gid, mode):
    """Replace `name` with `payload` atomically, by descriptor only."""
    temporary = write_temporary(directory_fd, name, payload, uid, gid, mode)
    try:
        os.replace(temporary, name, src_dir_fd=directory_fd, dst_dir_fd=directory_fd)
    except BaseException:
        try:
            os.unlink(temporary, dir_fd=directory_fd)
        except OSError:
            pass
        raise
    os.fsync(directory_fd)
    return hashlib.sha256(payload).hexdigest()


def archive_names(stamp, kind):
    """Candidate archive names, most preferred first, and finite.

    The timestamp has one-second resolution, so two resumes in the same second -
    or after a clock step backwards - collide. The collision is resolved by
    taking the next unused name, never by replacing the archive already there:
    an ordinal suffix keeps the ordinary case readable, and a random suffix
    guarantees a free name exists without an unbounded loop.
    """
    yield "runtime.%s-%s.json" % (kind, stamp)
    for ordinal in range(2, 66):
        yield "runtime.%s-%s.%d.json" % (kind, stamp, ordinal)
    for _ in range(64):
        yield "runtime.%s-%s.%s.json" % (kind, stamp, secrets.token_hex(8))


def archive_bytes(directory_fd, directory_path, stamp, kind, payload, uid, gid, mode):
    """Preserve `payload` under a name that did not exist before. Never replaces.

    The bytes are written to a private temporary first and then hard-linked into
    the chosen name: os.link fails with EEXIST instead of overwriting, so the
    "is this name free?" test and the claim on it are one atomic operation and a
    concurrent publisher cannot win the name in between. A crash can therefore
    leave an unreferenced temporary, but never a truncated or replaced archive.
    """
    temporary = write_temporary(directory_fd, "archive", payload, uid, gid, mode)
    published = None
    try:
        for candidate in archive_names(stamp, kind):
            try:
                os.link(
                    temporary,
                    candidate,
                    src_dir_fd=directory_fd,
                    dst_dir_fd=directory_fd,
                    follow_symlinks=False,
                )
            except FileExistsError:
                continue
            published = candidate
            break
    finally:
        # Drop the temporary's own reference either way, so the surviving name is
        # the only link to the inode (a later start's nlink check requires it)
        # and a failed reservation leaves nothing behind.
        try:
            os.unlink(temporary, dir_fd=directory_fd)
        except OSError:
            pass

    if published is None:
        fail("could not reserve an unused archive name under %s" % directory_path)

    os.fsync(directory_fd)

    # Verify what landed, by descriptor, rather than what was intended. The owner
    # is enforced by open_checked's allowed_owners; the group and mode are
    # checked here, and the nlink check inside open_checked proves the temporary
    # reference is gone.
    check_fd = open_checked(
        os.path.join(directory_path, published),
        allowed_owners=(uid,),
        parent_fd=directory_fd,
        name=published,
    )
    if check_fd is None:
        fail("%s disappeared immediately after it was preserved" % os.path.join(directory_path, published))
    try:
        written = read_all(check_fd)
        info = os.fstat(check_fd)
    finally:
        os.close(check_fd)
    if written != payload:
        fail("%s does not match the bytes that were preserved" % os.path.join(directory_path, published))
    if info.st_gid != gid or stat.S_IMODE(info.st_mode) != mode:
        fail("%s does not carry the intended ownership or mode" % os.path.join(directory_path, published))
    return published


private_dir_fd = open_checked(private_root, directory=True, allowed_owners=(control_uid, 0))
if private_dir_fd is None:
    fail("%s does not exist; the controller-private volume is not mounted" % private_root)

marker_path = os.path.join(private_root, marker_name)
if not os.path.lexists(marker_path):
    print("No rollback is recorded; nothing to resume. The isolated image can start as it is.")
    raise SystemExit(0)

# The marker is what this command consumes and unlinks, so it is held to the
# same rules as every other path here: a regular, controller-owned file with no
# symlink at the final component and no extra hard links. Its body is advisory
# (the operator's --state-source decides, not the recorded digests), so a
# damaged body is reported rather than fatal - but a substituted *inode* is not
# something to resolve on.
marker_fd = open_checked(
    marker_path, allowed_owners=(control_uid, 0), parent_fd=private_dir_fd, name=marker_name
)
if marker_fd is None:
    fail("%s disappeared while it was being read; re-run the command" % marker_path)
try:
    marker_body = read_all(marker_fd)
finally:
    os.close(marker_fd)

try:
    recorded_marker = json.loads(marker_body.decode("utf-8"))
    if not isinstance(recorded_marker, dict):
        raise ValueError("marker is not an object")
    print(
        "Resolving the interlock recorded in %s at %s (reason '%s')."
        % (
            marker_path,
            recorded_marker.get("publishedAt", "an unknown time"),
            recorded_marker.get("reason", "rollback"),
        )
    )
except (ValueError, UnicodeDecodeError) as error:
    print(
        "%s could not be parsed as rollback evidence (%s); resolving it from the explicit "
        "--state-source choice, which does not depend on the recorded detail."
        % (marker_path, error),
        file=sys.stderr,
    )

data_fd = open_checked(data_root, directory=True, allowed_owners=(0, 1000))
if data_fd is None:
    fail("%s does not exist; the agent data volume is not mounted" % data_root)

legacy_fd = open_checked(
    legacy_state, allowed_owners=(0, 1000), parent_fd=data_fd, name=state_name
)
private_fd = open_checked(
    private_state, allowed_owners=(control_uid, 0), parent_fd=private_dir_fd, name=state_name
)
secret_fd = open_checked(secret_path, allowed_owners=(control_uid, 1000, 0))
if secret_fd is None:
    fail("owner password file does not exist; cannot resume without it")

stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())

if source == "legacy":
    if legacy_fd is None:
        fail(
            "--state-source legacy was requested but %s does not exist. Use --state-source private "
            "to keep the isolated run's state." % legacy_state
        )
    chosen = read_all(legacy_fd)
    if private_fd is not None:
        superseded = read_all(private_fd)
        if superseded != chosen:
            name = archive_bytes(
                private_dir_fd,
                private_root,
                stamp,
                "superseded",
                superseded,
                control_uid,
                control_gid,
                0o600,
            )
            print(
                "Preserved the superseded controller-private state as %s."
                % os.path.join(private_root, name)
            )
    digest = publish_bytes(
        private_dir_fd, private_root, state_name, chosen, control_uid, control_gid, 0o600
    )
    print(
        "Adopted the pre-isolation image's %s as the controller-private state (%d bytes, sha256 %s)."
        % (legacy_state, len(chosen), digest)
    )
elif source == "private":
    if private_fd is None:
        fail(
            "--state-source private was requested but %s does not exist. Use --state-source legacy "
            "to adopt the state the pre-isolation image left behind." % private_state
        )
    kept = read_all(private_fd)
    if legacy_fd is not None:
        discarded = read_all(legacy_fd)
        if discarded != kept:
            name = archive_bytes(
                private_dir_fd,
                private_root,
                stamp,
                "rolled-back",
                discarded,
                control_uid,
                control_gid,
                0o600,
            )
            print(
                "Preserved the pre-isolation image's state as %s; it is not discarded, only unused."
                % os.path.join(private_root, name)
            )
    print("Keeping the controller-private state of the isolated run.")
else:
    fail("unknown state source '%s'; expected 'legacy' or 'private'" % source)

# The isolated layout expects the legacy path to be the root-owned stale copy.
# Leaving it owned by UID 1000 would trip the entrypoint's own fail-closed check.
if legacy_fd is not None:
    os.fchown(legacy_fd, 0, 0)
    os.fchmod(legacy_fd, 0o600)
    print("Returned %s to the root-owned stale copy the isolated layout expects." % legacy_state)

# The secret goes back to the controller identity, so the isolated entrypoint's
# both-sides check passes. Metadata only, exactly like --migrate-owner.
before = os.fstat(secret_fd)
os.fchown(secret_fd, control_uid, control_gid)
os.fchmod(secret_fd, 0o600)
after = os.fstat(secret_fd)
if after.st_uid != control_uid or after.st_gid != control_gid:
    fail("ownership verification failed for %s" % secret_path)
if after.st_ino != before.st_ino or after.st_size != before.st_size:
    fail("%s changed during the resume" % secret_path)
print(
    "Re-owned %s from %d:%d to %d:%d (contents unchanged)."
    % (secret_path, before.st_uid, before.st_gid, control_uid, control_gid)
)

# Last, so any earlier failure leaves the interlock in place and the command
# replayable.
os.unlink(marker_name, dir_fd=private_dir_fd)
os.fsync(private_dir_fd)
print("Cleared the rollback interlock. The isolated image can start again.")
'''


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--context", default="default")
    parser.add_argument(
        "--project",
        default=DEFAULT_PROJECT,
        help=(
            "Compose project name. The data and private volumes are derived from it "
            f"as <project>_control-data and <project>_control-private (default: {DEFAULT_PROJECT})."
        ),
    )
    parser.add_argument(
        "--secrets-volume",
        default=DEFAULT_SECRET_VOLUME,
        help=(
            "external owner-secret volume; it is declared external in compose.yaml, so it "
            f"is not derived from the project name (default: {DEFAULT_SECRET_VOLUME})"
        ),
    )
    parser.add_argument(
        "--image",
        default=None,
        help="image used to run the container-side helpers (default: <project>-control)",
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--migrate-owner",
        action="store_true",
        help=(
            "re-own an existing owner password from the pre-isolation shared UID 1000 "
            f"to the controller UID {CONTROL_UID}. Metadata only: the credential is "
            "never read, rotated or rewritten."
        ),
    )
    mode.add_argument(
        "--revert-isolation",
        action="store_true",
        help=(
            "roll back to the pre-isolation image: republish the current controller-private "
            f"runtime state to /data/runtime.json and hand it, /data and the owner secret to UID "
            f"{LEGACY_UID}. Requires the control container to be stopped."
        ),
    )
    mode.add_argument(
        "--resume-isolation",
        action="store_true",
        help=(
            "roll forward to the isolated image after a rollback. Requires --state-source, "
            "because the pre-isolation image may have advanced /data/runtime.json past the "
            "controller-private copy and the two cannot be merged automatically."
        ),
    )
    parser.add_argument(
        "--state-source",
        choices=("legacy", "private"),
        help=(
            "with --resume-isolation, which runtime state survives: 'legacy' adopts what the "
            "pre-isolation image left in /data/runtime.json, 'private' keeps the isolated run's "
            "state. The other one is preserved under a timestamped name, never deleted."
        ),
    )
    parser.add_argument(
        "--accept-missing-runtime-state",
        action="store_true",
        help=(
            "with --revert-isolation, proceed when there is no controller-private runtime state "
            "to republish. The old image will then start a new organization."
        ),
    )
    parser.add_argument(
        "--container",
        default=None,
        help=(
            "control container checked for the stopped precondition "
            "(default: <project>-control-1)"
        ),
    )
    args = parser.parse_args(argv)

    if not PROJECT_PATTERN.match(args.project):
        parser.error(
            "--project must start with a lowercase letter or digit and contain only "
            "lowercase letters, digits, '-' or '_'"
        )
    if args.resume_isolation and args.state_source is None:
        parser.error(
            "--resume-isolation requires --state-source legacy|private: the surviving runtime "
            "state must be chosen explicitly, never inferred"
        )
    if args.state_source is not None and not args.resume_isolation:
        parser.error("--state-source is only meaningful with --resume-isolation")
    if args.accept_missing_runtime_state and not args.revert_isolation:
        parser.error("--accept-missing-runtime-state is only meaningful with --revert-isolation")

    args.data_volume = f"{args.project}_control-data"
    args.private_volume = f"{args.project}_control-private"
    args.image = args.image or f"{args.project}-control"
    args.container = args.container or f"{args.project}-control-1"
    return args


def container_is_running(docker, container):
    """True when the named container exists and is running.

    An absent container is not running. An inspect failure that is not a plain
    absence is treated as running, because a rollback must never re-own state
    underneath a live controller on an uncertain answer.
    """
    result = subprocess.run(
        docker + ["inspect", "--format", "{{json .State.Running}}", container],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        combined = (result.stderr or "") + (result.stdout or "")
        if "No such object" in combined or "no such object" in combined:
            return False
        print(
            f"could not determine whether '{container}' is running: {combined.strip()}",
            file=sys.stderr,
        )
        raise SystemExit(1)
    try:
        return bool(json.loads(result.stdout.strip()))
    except (ValueError, TypeError):
        print(f"unexpected docker inspect output for '{container}'", file=sys.stderr)
        raise SystemExit(1)


def require_volumes(docker, volumes):
    """Fail before doing anything when a named volume does not exist.

    `docker run` creates a missing named volume silently, which would produce a
    confident report about an empty volume while the real state sits untouched
    under a different project name.
    """
    missing = []
    for volume in volumes:
        result = subprocess.run(
            docker + ["volume", "inspect", volume],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            missing.append(volume)
    if missing:
        print(
            "these volumes do not exist on this context: "
            + ", ".join(missing)
            + ". Check --context and --project (docker volume ls).",
            file=sys.stderr,
        )
        raise SystemExit(1)


def run_in_volumes(docker, image, code, mounts, arguments):
    command = docker + ["run", "--rm", "--user", "root", "--entrypoint", "python3"]
    for source, target in mounts:
        command += ["--mount", f"type=volume,source={source},target={target}"]
    command += [image, "-c", code] + [str(value) for value in arguments]
    subprocess.run(command, check=True)


def main(argv):
    args = parse_args(argv)
    docker = ["docker", "--context", args.context]

    if args.revert_isolation or args.resume_isolation:
        # The isolated controller holds the private state and would immediately
        # re-apply the isolation layout on its next restart, so acting under a
        # live container is neither complete nor stable.
        if container_is_running(docker, args.container):
            verb = "reverting" if args.revert_isolation else "resuming"
            flag = "--revert-isolation" if args.revert_isolation else "--resume-isolation"
            print(
                f"'{args.container}' is running; stop it before {verb} "
                f"(docker compose down), then re-run {flag}.",
                file=sys.stderr,
            )
            return 1

        require_volumes(
            docker, [args.secrets_volume, args.data_volume, args.private_volume]
        )
        mounts = [
            (args.secrets_volume, "/secrets"),
            (args.data_volume, "/data"),
            (args.private_volume, "/control-data"),
        ]
        if args.revert_isolation:
            arguments = [
                TARGET,
                LEGACY_UID,
                LEGACY_GID,
                "/data",
                "/control-data",
                "1" if args.accept_missing_runtime_state else "0",
            ]
            code = REVERT_CODE
            action = "isolation rollback"
        else:
            arguments = [
                TARGET,
                CONTROL_UID,
                CONTROL_GID,
                "/data",
                "/control-data",
                args.state_source,
            ]
            code = RESUME_CODE
            action = "isolation resume"
    elif args.migrate_owner:
        require_volumes(docker, [args.secrets_volume])
        mounts = [(args.secrets_volume, "/secrets")]
        arguments = [TARGET, CONTROL_UID, CONTROL_GID]
        code = MIGRATE_CODE
        action = "ownership migration"
    else:
        subprocess.run(docker + ["volume", "create", args.secrets_volume], check=True)
        mounts = [(args.secrets_volume, "/secrets")]
        arguments = [TARGET, CONTROL_UID, CONTROL_GID]
        code = REMOTE_CODE
        action = "initialization"

    try:
        run_in_volumes(docker, args.image, code, mounts, arguments)
    except subprocess.CalledProcessError:
        if args.revert_isolation:
            print(
                "isolation rollback failed. Nothing was deleted and every step is idempotent: "
                "fix the reported condition and run the exact same command again. Do not start "
                "either image until it reports success.",
                file=sys.stderr,
            )
        elif args.resume_isolation:
            print(
                "isolation resume failed. The rollback interlock is still in place, so the "
                "isolated image still refuses to start: fix the reported condition and run the "
                "exact same command again.",
                file=sys.stderr,
            )
        else:
            print(
                f"owner password {action} failed; no existing file was overwritten.",
                file=sys.stderr,
            )
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
