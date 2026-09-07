#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p .fixture/secrets
chmod 700 .fixture .fixture/secrets
if [[ ! -f .fixture/secrets/fixture-key ]]; then
  ssh-keygen -q -t ed25519 -N '' -f .fixture/secrets/fixture-key
fi
for secret in owner-password server-password; do
  if [[ ! -f .fixture/secrets/$secret ]]; then head -c 32 /dev/urandom | base64 > ".fixture/secrets/$secret"; fi
  chmod 600 ".fixture/secrets/$secret"
done
docker build -t hvo-agentcontrol-ssh-fixture:local tests/Fixtures
for target in a b; do
  name="hvo-agentcontrol-fixture-$target"
  if ! docker inspect "$name" >/dev/null 2>&1; then docker run -d --name "$name" hvo-agentcontrol-ssh-fixture:local >/dev/null; fi
  docker start "$name" >/dev/null
  docker cp .fixture/secrets/fixture-key.pub "$name:/home/agent/.ssh/authorized_keys" >/dev/null
  docker exec "$name" sh -c 'chown agent:agent /home/agent/.ssh/authorized_keys && chmod 600 /home/agent/.ssh/authorized_keys'
  docker exec -u agent "$name" sh -c 'for dir in a b; do git -C /home/agent/workspaces/$dir init -q; git -C /home/agent/workspaces/$dir -c user.name=Fixture -c user.email=fixture@invalid commit -q --allow-empty -m Initial; done'
  ready=false
  for attempt in {1..60}; do
    if docker exec "$name" python3 -c 'from pathlib import Path; import socket; assert Path("/etc/ssh/ssh_host_ed25519_key.pub").is_file(); socket.create_connection(("127.0.0.1",22),1).close()' >/dev/null 2>&1; then
      ready=true
      break
    fi
    sleep .5
  done
  if [[ "$ready" != true ]]; then
    echo "SSH fixture $name did not become ready within 30 seconds." >&2
    exit 1
  fi
  host=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$name")
  fingerprint=$(docker exec "$name" ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256 | awk '{print $2}')
  jq -n --arg host "$host" --arg fingerprint "$fingerprint" --arg target "$target" '{host:$host,fingerprint:$fingerprint,name:("Fixture "+$target)}' > ".fixture/runtime-$target.json"
done
printf 'Fixture profiles: .fixture/runtime-a.json and .fixture/runtime-b.json\n'
