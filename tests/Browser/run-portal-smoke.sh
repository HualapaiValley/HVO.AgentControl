#!/usr/bin/env bash
# Run the AgentControl V2 portal smoke test.
#
# Ensures the loopback-only SSH tunnel (home-dev-01 127.0.0.1:15054 ->
# home-docker 127.0.0.1:5054) exists, then executes the Playwright suite.
# The tunnel is intentionally left running for the operator; its PID is in
# artifacts/tunnel.pid and its log in artifacts/tunnel.log.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ART="$ROOT/artifacts"
mkdir -p "$ART/portal-smoke"

if ! ss -ltn 2>/dev/null | grep -q '127\.0\.0\.1:15054'; then
  echo "Opening tunnel: home-dev-01 127.0.0.1:15054 -> home-docker 127.0.0.1:5054"
  nohup ssh -N \
    -o ExitOnForwardFailure=yes \
    -o ServerAliveInterval=30 \
    -o ServerAliveCountMax=3 \
    -o StrictHostKeyChecking=accept-new \
    -L 127.0.0.1:15054:127.0.0.1:5054 home-docker \
    > "$ART/tunnel.log" 2>&1 &
  echo $! > "$ART/tunnel.pid"
  sleep 2
fi

exec node "$ROOT/tests/Browser/portal-smoke.mjs"
