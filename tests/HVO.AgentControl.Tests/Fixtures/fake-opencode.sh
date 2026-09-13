#!/bin/sh
# Canonical, checked-in stand-in for the OpenCode attach client used by
# TmuxRealIntegrationTests.
#
# It records the sanitized environment and argv the launcher handed the pane,
# publishes a single "ready" marker, then stays alive so the pane is live. The
# marker is created only after both records have been written and closed, so the
# test can synchronize on a real filesystem event instead of sleeping and
# reading a half-written file.
#
# This is not a real OpenCode, model, provider or network call. The script is
# copied to the output directory and executed only through a per-test symlink;
# its inode is never rewritten at runtime (issue #232).
set -eu

directory=${0%/*}
/usr/bin/env > "$directory/child.env"
printf '%s\n' "$@" > "$directory/child.args"
: > "$directory/child.ready"

exec /bin/sleep 300
