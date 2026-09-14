#!/bin/sh
# Canonical, checked-in stand-in for the privileged agent launcher
# (src/launcher/agentcontrol-launch.c), used to drive AgentProcessLauncher's
# `signal` operation deterministically from tests.
#
# Only the `signal <pid> <TERM|KILL>` operation is modelled, because that is the
# one the terminal teardown depends on. Behaviour comes from a non-executable
# sidecar resolved through the invoked path ($0), so this file is symlinked per
# test and never rewritten at runtime (the ETXTBSY race of #232).
#
#   fake-launcher.mode:
#     refuse   exit non-zero without signalling - the real launcher's
#              caller/parentage/UID check refusing, or the helper failing to
#              start. AgentProcessLauncher.TryTerminate then returns false.
#     swallow  exit 0 without signalling - a launcher that reports success while
#              the target survives, which is what makes an unbounded wait after
#              a "successful" termination hang forever.
#     deliver  actually send the requested signal, then exit 0.
#
# Every invocation is appended to fake-launcher.log so a test can assert whether
# a termination was even attempted.
set -u

directory=${0%/*}
printf '%s\n' "$*" >> "$directory/fake-launcher.log"

mode=deliver
if [ -f "$directory/fake-launcher.mode" ]; then
    read -r mode < "$directory/fake-launcher.mode"
fi

if [ "${1:-}" != "signal" ]; then
    echo "fake launcher: only the signal operation is modelled" >&2
    exit 64
fi

case "$mode" in
    refuse) exit 64 ;;
    swallow) exit 0 ;;
    deliver) kill -"${3:-TERM}" "${2:-0}" 2>/dev/null; exit 0 ;;
    *) echo "fake launcher: unknown mode '$mode'" >&2; exit 64 ;;
esac
