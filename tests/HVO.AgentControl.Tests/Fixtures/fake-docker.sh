#!/bin/sh
# Canonical, checked-in `docker` stand-in for SecretInitializationTests.
#
# Each test symlinks this script into its own temporary directory and writes
# non-executable sidecars next to the symlink; nothing ever writes to this file.
# Materializing an executable at runtime reintroduces the ETXTBSY race of #232:
# File.WriteAllText holds a writable descriptor, a concurrent Process.Start on
# another thread forks and inherits it, and that child's execve then fails with
# "Text file busy". The same rule governs Fixtures/fake_acp.py,
# Fixtures/fake-tmux.py and Fixtures/tmux-wrapper.sh, and
# TestFixtureHygieneTests keeps it structural.
#
# Sidecars, all resolved through the invoked path ($0) so each test stays
# private:
#   calls.log           appended with one line per invocation
#   response.<kind>     three lines: exit code, stdout text, stderr text
#
# <kind> is the docker operation the argument list selects: `volume` for
# `volume inspect`, `run` for `run --rm`, `inspect` for any other inspect. An
# absent sidecar means "succeed silently", which is what an unstubbed docker
# subcommand should look like to the script under test.
set -u

directory=${0%/*}
printf '%s\n' "$*" >> "$directory/calls.log"

case "$*" in
    *"volume inspect"*) kind=volume ;;
    *"run --rm"*) kind=run ;;
    *inspect*) kind=inspect ;;
    *) exit 0 ;;
esac

response="$directory/response.$kind"
if [ ! -f "$response" ]; then
    exit 0
fi

code=0
out=""
err=""
{
    read -r code || code=0
    read -r out || out=""
    read -r err || err=""
} < "$response"

if [ -n "$out" ]; then
    printf '%s\n' "$out"
fi
if [ -n "$err" ]; then
    printf '%s\n' "$err" >&2
fi
exit "$code"
