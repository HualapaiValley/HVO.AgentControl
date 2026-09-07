#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
mkdir -p .fixture/demo-secrets .fixture/coordinator-auth
if [[ ! -f .fixture/demo-secrets/coordinator-key ]]; then
  ssh-keygen -q -t ed25519 -N '' -f .fixture/demo-secrets/coordinator-key
fi
if [[ ! -f .fixture/demo-secrets/coordinator-server-password ]]; then
  head -c 32 /dev/urandom | base64 > .fixture/demo-secrets/coordinator-server-password
fi
cp .fixture/demo-secrets/coordinator-key.pub .fixture/coordinator-auth/authorized_keys
chmod 755 .fixture/coordinator-auth
chmod 644 .fixture/coordinator-auth/authorized_keys
chmod 600 .fixture/demo-secrets/coordinator-key .fixture/demo-secrets/coordinator-server-password
docker compose -f compose.demo.yaml --profile coordinator up --build -d coordinator
docker exec -u agent hvo-agentcontrol-coordinator sh -c '
  git -C /home/agent/workspaces/coordinator init -q
  if ! git -C /home/agent/workspaces/coordinator rev-parse --verify HEAD >/dev/null 2>&1; then
    git -C /home/agent/workspaces/coordinator -c user.name=AgentControl -c user.email=coordinator@localhost commit -q --allow-empty -m "Initialize coordinator workspace"
  fi'
python3 - <<'PY'
import json,subprocess,time
from pathlib import Path
name='hvo-agentcontrol-coordinator'
info=json.loads(subprocess.check_output(['docker','inspect',name]))[0]
for attempt in range(80):
 if subprocess.run(['docker','exec',name,'test','-f','/etc/ssh/ssh_host_ed25519_key.pub']).returncode == 0: break
 time.sleep(.25)
else: raise RuntimeError('Coordinator SSH host keys did not become ready')
fingerprint=subprocess.check_output(['docker','exec',name,'ssh-keygen','-lf','/etc/ssh/ssh_host_ed25519_key.pub','-E','sha256'],text=True).split()[1]
profile={'name':'AgentControl coordinator','host':info['NetworkSettings']['Networks']['bridge']['IPAddress'],
 'port':22,'username':'agent','hostKeySha256':fingerprint,'hostKeyAlgorithm':'ssh-ed25519',
 'authentication':'privateKey','credentialReference':'coordinator-key','serverPasswordReference':'coordinator-server-password',
 'stateDirectory':'/home/agent/.local/state/agentcontrol-coordinator','allowedRoots':'/home/agent/workspaces',
 'capacity':1,'installIfMissing':True}
Path('.fixture/coordinator-profile.json').write_text(json.dumps(profile,indent=2)+'\n')
print('Coordinator runtime profile saved in .fixture/coordinator-profile.json. Register it in AgentControl and create a session with role Coordinator in /home/agent/workspaces/coordinator.')
PY
