#!/bin/sh
# AgentControl control-container entrypoint.
#
# Runs as root only long enough to establish the two-identity layout, then
# irreversibly drops to the controller identity and execs the portal. The
# controller never regains root: the only privileged component that survives is
# the fixed-operation launcher (mode 4750 root:control), which the agent
# identity cannot execute.
#
#   UID 1000 / GID 1000  agent    /data/home, /data/workspace (tmux, OpenCode)
#   UID 1001 / GID 1001  control  /control-data (runtime state), owner secret
#
# The filesystem work itself is done by prepare-layout.py rather than chown/chmod
# here, because /data is a persistent agent-owned volume: every entry in it is
# attacker-controlled and must be handled with O_NOFOLLOW descriptors, never a
# path a symlink could redirect. See that file for the ordering guarantees.
#
# Any failure exits non-zero and the service stays down. An isolation boundary
# that could not be established is never approximated.
set -eu

AGENT_UID=1000
AGENT_GID=1000
CONTROL_UID=1001
CONTROL_GID=1001

LAUNCHER=/usr/local/bin/agentcontrol-launch
PREPARE_LAYOUT=/usr/local/bin/agentcontrol-prepare-layout

log() { printf 'control-entrypoint: %s\n' "$1" >&2; }

fail() { log "$1"; exit 1; }

if [ "$(id -u)" -ne 0 ]; then
    # Already unprivileged (for example a `docker run --user` override). The
    # isolation guarantees below cannot be established, so do not pretend.
    fail "must start as root to establish controller/agent isolation"
fi

# ---------------------------------------------------------------------------
# Ownership and permissions for the agent tree, the controller-private store,
# the orientation directory and the legacy runtime state. Symlink-safe and
# non-recursive; existing agent content keeps its UID 1000 ownership.
#
# This also enforces the rollback interlock: after
# `scripts/init-secrets.py --revert-isolation` the pre-isolation image may have
# advanced /data/runtime.json past the controller-private copy, and the two
# cannot be merged, so the start is refused until the operator names the state
# that survives with --resume-isolation --state-source legacy|private.
# ---------------------------------------------------------------------------
"$PREPARE_LAYOUT" || fail "filesystem layout preparation failed; refusing to start"

# ---------------------------------------------------------------------------
# Record this shell's PID as the controller's PID. `exec` below replaces this
# process image without changing its PID, so the recorded value is the
# controller's for the whole life of the container - unlike a guessed "PID 2",
# which depends on how tini and this script happened to fork. The file is
# rewritten on every start and is only meaningful while the container runs; a
# value left behind by a stopped container is stale by definition.
#
# Operators read it rather than pattern-matching the process list:
#
#   docker compose exec -T control sh -c \
#     'grep CapBnd /proc/$(cat /control-data/controller.pid)/status'
#
# `pgrep -f HVO.AgentControl.dll` is not a substitute: the pattern appears in
# the inspecting command's own command line, so pgrep matches that shell and
# reports a PID even when no controller is running.
# ---------------------------------------------------------------------------
"$PREPARE_LAYOUT" --record-controller-pid "$$" \
    || fail "could not record the controller PID; refusing to start"

# ---------------------------------------------------------------------------
# The owner secret must be controller-only. The mount is read-only, so this is
# a verification, not a repair: a secret the controller cannot read, or one the
# agent can read, fails startup instead of running with a false boundary.
# Migrate it with scripts/init-secrets.py --migrate-owner before deploying.
# ---------------------------------------------------------------------------
SECRET_FILE="${Control__OwnerPasswordFile:-}"
if [ -n "$SECRET_FILE" ]; then
    [ -f "$SECRET_FILE" ] || fail "owner password file '$SECRET_FILE' is missing"

    secret_uid=$(stat -c '%u' "$SECRET_FILE")
    secret_mode=$(stat -c '%a' "$SECRET_FILE")
    secret_dir=$(dirname "$SECRET_FILE")
    secret_dir_uid=$(stat -c '%u' "$secret_dir")
    secret_dir_mode=$(stat -c '%a' "$secret_dir")

    if [ "$secret_uid" != "$CONTROL_UID" ]; then
        fail "owner password file is owned by UID $secret_uid, expected the controller UID $CONTROL_UID; run scripts/init-secrets.py --migrate-owner"
    fi
    case "$secret_mode" in
        600|400) ;;
        *) fail "owner password file mode is $secret_mode, expected 600 or 400" ;;
    esac
    # The parent directory governs rename/unlink of the secret, so it must be
    # owned by root or the controller and writable by neither group nor other.
    if [ "$secret_dir_uid" != "0" ] && [ "$secret_dir_uid" != "$CONTROL_UID" ]; then
        fail "owner secret directory is owned by UID $secret_dir_uid; expected root or the controller"
    fi
    # Inspect only the group and other octal digits for the write bit.
    secret_dir_group_other=${secret_dir_mode#"${secret_dir_mode%??}"}
    case "$secret_dir_group_other" in
        *[2367]*) fail "owner secret directory mode is $secret_dir_mode; it must not be group- or world-writable" ;;
    esac

    # Prove the boundary from both sides before serving anything.
    setpriv --reuid "$CONTROL_UID" --regid "$CONTROL_GID" --clear-groups \
        test -r "$SECRET_FILE" \
        || fail "controller identity cannot read the owner password file"
    if setpriv --reuid "$AGENT_UID" --regid "$AGENT_GID" --clear-groups \
        test -r "$SECRET_FILE" 2>/dev/null; then
        fail "agent identity can read the owner password file; refusing to start"
    fi
fi

# ---------------------------------------------------------------------------
# The launcher is the whole privileged surface. Verify it before granting the
# controller a way to call it.
# ---------------------------------------------------------------------------
if [ -e "$LAUNCHER" ]; then
    launcher_meta=$(stat -c '%u %g %a' "$LAUNCHER")
    [ "$launcher_meta" = "0 $CONTROL_GID 4750" ] \
        || fail "launcher permissions are '$launcher_meta', expected '0 $CONTROL_GID 4750'"
    if setpriv --reuid "$AGENT_UID" --regid "$AGENT_GID" --clear-groups \
        test -x "$LAUNCHER" 2>/dev/null; then
        fail "agent identity can execute the privileged launcher; refusing to start"
    fi
fi

# ---------------------------------------------------------------------------
# The startup capabilities are not needed after this point, and they must not
# survive into the controller: the setuid launcher regains every capability in
# the bounding set while it is root, so CHOWN/DAC_OVERRIDE/FOWNER left in the
# set would still be reachable from a compromised controller through the
# launcher's brief root phase. Removing them from the bounding set here means no
# descendant - launcher included - can ever hold them again. SETUID/SETGID/KILL
# stay, because the launcher's identity change and cross-UID signal need them.
# SETPCAP is required to shrink the bounding set and is dropped in the same call.
# ---------------------------------------------------------------------------
# CAP_SETPCAP is bit 8. Without it the bounding set cannot be reduced, and a
# controller that keeps CHOWN/DAC_OVERRIDE/FOWNER reachable is not the boundary
# this image documents - so refuse to start rather than serve a weaker one.
capability_effective=$(awk '/^CapEff:/ { print $2 }' /proc/self/status)
if [ "$(( 0x${capability_effective} & 0x100 ))" -eq 0 ]; then
    fail "CAP_SETPCAP is not available (CapEff=$capability_effective); add SETPCAP to cap_add so the startup capabilities can be dropped before the controller starts"
fi

log "controller/agent isolation established; dropping to UID $CONTROL_UID"

#
# No --no-new-privs: the controller must retain the ability to gain the agent
# identity through the setuid launcher, which is the entire isolation mechanism.
# Every other setuid binary is stripped from the image at build time, and the
# launcher itself sets no_new_privs on the child before exec.
exec setpriv \
    --bounding-set=-chown,-dac_override,-fowner,-fsetid,-setpcap \
    --reuid "$CONTROL_UID" \
    --regid "$CONTROL_GID" \
    --init-groups \
    "$@"
