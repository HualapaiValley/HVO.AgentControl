#!/usr/bin/env bash
set -euo pipefail

demo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
export CONTROL_UID=${CONTROL_UID:-$(id -u)}
export CONTROL_GID=${CONTROL_GID:-$(id -g)}
demo_compose=(docker compose --project-directory "$demo_root" -f "$demo_root/compose.demo.yaml")

start_demo() {
  if systemctl --user is-active --quiet hvo-agentcontrol-demo.service 2>/dev/null; then
    echo 'Stop the legacy host service before starting Docker: systemctl --user stop hvo-agentcontrol-demo.service' >&2
    exit 1
  fi
  if [[ ! -f "$demo_root/.fixture/demo-secrets/owner-password" || ! -f "$demo_root/.fixture/demo-data/agentcontrol.db" ]]; then
    echo 'Existing demo data and secrets are required under .fixture/.' >&2
    exit 1
  fi
  "${demo_compose[@]}" up --build -d
  "${demo_compose[@]}" ps
}

case "${1:-start}" in
  start) start_demo ;;
  stop) "${demo_compose[@]}" stop ;;
  restart) "${demo_compose[@]}" stop; start_demo ;;
  status) "${demo_compose[@]}" ps ;;
  logs) "${demo_compose[@]}" logs --tail 100 ;;
  *) echo "Usage: bash scripts/demo-server.sh {start|stop|restart|status|logs}" >&2; exit 2 ;;
esac
