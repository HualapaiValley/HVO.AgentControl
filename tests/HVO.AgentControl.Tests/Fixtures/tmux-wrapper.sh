#!/bin/sh
# Canonical, checked-in tmux launch shim for TmuxRealIntegrationTests.
#
# Every test symlinks this script as its tmux client and writes a two-line,
# non-executable sidecar next to the symlink:
#   line 1: absolute path to the real tmux binary
#   line 2: absolute path to the test's private server socket
#
# The sidecar is resolved through the *invoked* path ($0), so the executable
# inode is shared and never rewritten at runtime. Materializing an executable by
# writing to it would reintroduce the ETXTBSY race of issue #232: a concurrent
# fork can inherit the writable descriptor and fail its execve after the writing
# parent closes. This fixture is copied to the output directory and executed
# only through a per-test symlink, exactly like Fixtures/fake_acp.py.
set -eu

directory=${0%/*}
{
    read -r real_tmux
    read -r socket
} < "$directory/tmux-wrapper.side"

# Isolation: never attach to a developer/CI server through an inherited tmux
# environment, and never source the user's or host's tmux configuration.
unset TMUX TMUX_TMPDIR
exec "$real_tmux" -f /dev/null -S "$socket" "$@"
