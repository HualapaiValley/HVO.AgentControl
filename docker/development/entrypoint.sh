#!/usr/bin/env bash
set -euo pipefail
install -d -o agent -g agent -m 700 /home/agent/.ssh
install -o agent -g agent -m 600 /run/agentcontrol/authorized_keys /home/agent/.ssh/authorized_keys
install -o agent -g agent -m 600 /run/agentcontrol/repository-key /home/agent/.ssh/repository-key
install -o agent -g agent -m 644 /run/agentcontrol/github_known_hosts /home/agent/.ssh/known_hosts
cat > /home/agent/.ssh/config <<'CONFIG'
Host github.com
  User git
  IdentityFile ~/.ssh/repository-key
  IdentitiesOnly yes
  StrictHostKeyChecking yes
CONFIG
chown agent:agent /home/agent/.ssh/config
chmod 600 /home/agent/.ssh/config
install -d -o agent -g agent /home/agent/workspaces
ssh-keygen -A >/dev/null
exec /usr/sbin/sshd -D -e -o AuthorizedKeysFile=.ssh/authorized_keys
