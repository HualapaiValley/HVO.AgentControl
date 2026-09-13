#!/usr/bin/env bash
set -euo pipefail

demo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
demo_state_root=${HVO_DEMO_STATE_ROOT:-"$demo_root/.fixture"}
if [[ "$demo_state_root" != /* ]]; then
  echo 'HVO_DEMO_STATE_ROOT must be an absolute path.' >&2
  exit 1
fi
export CONTROL_UID=${CONTROL_UID:-$(id -u)}
export CONTROL_GID=${CONTROL_GID:-$(id -g)}
export DEMO_DATA_DIRECTORY="$demo_state_root/demo-data"
export DEMO_SECRETS_DIRECTORY="$demo_state_root/demo-secrets"
demo_project=${COMPOSE_PROJECT_NAME:-hvo-agentcontrol-demo}
demo_compose=(docker compose --project-name "$demo_project" --project-directory "$demo_root" -f "$demo_root/compose.demo.yaml")
control_network_name=
control_network_marker="$demo_state_root/control-network-name"

valid_network_name() {
  local LC_ALL=C
  [[ "$1" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}$ ]]
}

configure_control_network() {
  if [[ -e "$control_network_marker" || -L "$control_network_marker" ]]; then
    if [[ ! -f "$control_network_marker" || -L "$control_network_marker" ]]; then
      echo 'The control network marker must be a regular file.' >&2
      exit 1
    fi
    marker_bytes=$(wc -c < "$control_network_marker")
    if [[ "$marker_bytes" -lt 1 || "$marker_bytes" -gt 256 ]]; then
      echo 'The control network marker must contain one Docker network name.' >&2
      exit 1
    fi
    control_network_name=$(<"$control_network_marker")
    if ! valid_network_name "$control_network_name" ||
       [[ "$marker_bytes" -ne "${#control_network_name}" && "$marker_bytes" -ne $((${#control_network_name} + 1)) ]]; then
      echo 'The control network marker must contain one Docker network name.' >&2
      exit 1
    fi
  else
    # Compatibility with an earlier opt-in deployment. Keep the requirement
    # provisional until the post-start helper verifies actual ownership/membership.
    existing_web=$(docker ps -aq --no-trunc --filter "label=com.docker.compose.project=$demo_project" --filter label=com.docker.compose.service=agentcontrol)
    if [[ -n "$existing_web" ]]; then
      mapfile -t existing_web_ids <<< "$existing_web"
      if [[ "${#existing_web_ids[@]}" -ne 1 ]]; then
        echo 'Expected at most one existing demo web container.' >&2
        exit 1
      fi
      control_network_name=$(docker inspect --format '{{if index .Config.Labels "com.hvo.agentcontrol.control-network"}}{{index .Config.Labels "com.hvo.agentcontrol.control-network"}}{{end}}' "${existing_web_ids[0]}")
      if [[ -n "$control_network_name" ]] && ! valid_network_name "$control_network_name"; then
        echo 'The existing web container has an invalid control network requirement.' >&2
        exit 1
      fi
    fi
  fi
  if [[ -n "$control_network_name" ]]; then
    export CONTROL_OPENCODE_NETWORK="$control_network_name"
    demo_compose+=(-f "$demo_root/compose.control.web.yaml")
  fi
}

persist_verified_network() {
  local receipt
  receipt=$(python3 "$demo_root/scripts/connect-control-network.py" --project "$demo_project")
  # Only non-secret Docker identity metadata is passed here. Link a same-directory
  # temporary file atomically so a concurrent owner setting is never overwritten.
  python3 - "$control_network_marker" "$control_network_name" "$receipt" <<'PY'
import json
import os
from pathlib import Path
import sys
import tempfile

marker, expected, receipt = Path(sys.argv[1]), sys.argv[2], json.loads(sys.argv[3])
if receipt.get("networkName") != expected:
    raise SystemExit("The verified control network does not match the configured requirement.")

def check_existing():
    if marker.is_symlink() or not marker.is_file() or marker.read_bytes() not in [expected.encode(), (expected + "\n").encode()]:
        raise SystemExit("The control network marker changed during deployment; it was not overwritten.")

if marker.exists() or marker.is_symlink():
    check_existing()
else:
    with tempfile.NamedTemporaryFile(dir=marker.parent, prefix=".control-network-name-", delete=False) as pending:
        temporary = Path(pending.name)
        pending.write((expected + "\n").encode())
        pending.flush()
        os.fsync(pending.fileno())
    try:
        try:
            os.link(temporary, marker)
        except FileExistsError:
            check_existing()
    finally:
        temporary.unlink()
print(json.dumps(receipt))
PY
}

prepare_demo() {
  if systemctl --user is-active --quiet hvo-agentcontrol-demo.service 2>/dev/null; then
    echo 'Stop the legacy host service before starting Docker: systemctl --user stop hvo-agentcontrol-demo.service' >&2
    exit 1
  fi
  if [[ ! -f "$DEMO_SECRETS_DIRECTORY/owner-password" || ! -f "$DEMO_DATA_DIRECTORY/agentcontrol.db" ]]; then
    echo 'Existing demo data and secrets are required under the demo state directory.' >&2
    exit 1
  fi
  configure_control_network
}

start_prepared_demo() {
  "${demo_compose[@]}" up --build -d --no-deps agentcontrol
  if [[ -n "$control_network_name" ]]; then
    persist_verified_network
  fi
  "${demo_compose[@]}" ps agentcontrol
}

case "${1:-start}" in
  start) prepare_demo; start_prepared_demo ;;
  stop) "${demo_compose[@]}" stop agentcontrol ;;
  restart) prepare_demo; "${demo_compose[@]}" stop agentcontrol; start_prepared_demo ;;
  status) "${demo_compose[@]}" ps agentcontrol ;;
  logs) "${demo_compose[@]}" logs --tail 100 agentcontrol ;;
  *) echo "Usage: bash scripts/demo-server.sh {start|stop|restart|status|logs}" >&2; exit 2 ;;
esac
