#!/usr/bin/env python3
"""Prepare repository-scoped keys and three persistent .NET development hosts.
Requires Docker, gh authenticated as repository admin, and ssh-keygen.
Secrets stay in ignored .fixture paths and are never printed.
"""
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
REPO = "RoySalisbury/HVO.AgentControl"


def run(*args, **kwargs):
    return subprocess.check_output(args, cwd=ROOT, text=True, **kwargs).strip()


def main():
    os.umask(0o077)
    secrets = ROOT / ".fixture/demo-secrets"
    secrets.mkdir(parents=True, exist_ok=True)
    github = json.loads(run("gh", "api", "meta"))
    existing = json.loads(run("gh", "api", f"repos/{REPO}/keys"))
    for number in range(1, 4):
        name = f"dev{number}"
        auth = ROOT / f".fixture/beta-auth/{name}"
        auth.mkdir(parents=True, exist_ok=True)
        auth.chmod(0o755)
        login = secrets / f"beta-{name}-key"
        repository = auth / "repository-key"
        for key in [login, repository]:
            if not key.exists():
                run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(key))
            key.chmod(0o600)
        (auth / "authorized_keys").write_text(Path(str(login) + ".pub").read_text())
        (auth / "github_known_hosts").write_text("".join("github.com " + key + "\n" for key in github["ssh_keys"]))
        password = secrets / f"beta-{name}-server-password"
        if not password.exists():
            password.write_text(os.urandom(32).hex() + "\n")
        public = Path(str(repository) + ".pub").read_text().split()
        if not any(key["key"].split()[:2] == public[:2] for key in existing):
            run("gh", "repo", "deploy-key", "add", str(repository) + ".pub", "--repo", REPO,
                "--title", f"AgentControl beta {name}", "--allow-write")
    subprocess.run(["docker", "compose", "-f", "compose.beta.yaml", "up", "--build", "-d"], cwd=ROOT, check=True)
    profiles = []
    for number in range(1, 4):
        name = f"dev{number}"
        container = f"hvo-agentcontrol-beta-{name}"
        for _ in range(80):
            ready = subprocess.run(["docker", "exec", container, "test", "-f", "/etc/ssh/ssh_host_ed25519_key.pub"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if ready.returncode == 0:
                break
            time.sleep(.5)
        else:
            raise RuntimeError(f"SSH keys not ready: {container}")
        fingerprint = run("docker", "exec", container, "ssh-keygen", "-lf", "/etc/ssh/ssh_host_ed25519_key.pub", "-E", "sha256").split()[1]
        info = json.loads(run("docker", "inspect", container))[0]
        run("docker", "exec", "-u", "agent", container, "bash", "-lc",
            "git config --global user.name 'AgentControl Beta' && git config --global user.email 'agentcontrol@localhost' && "
            "if ! test -d /home/agent/workspaces/HVO.AgentControl/.git; then "
            f"git clone git@github.com:{REPO}.git /home/agent/workspaces/HVO.AgentControl; fi")
        profiles.append({"id": uuid.uuid5(uuid.NAMESPACE_URL, f"https://github.com/{REPO}/beta/{name}").hex, "name": f"Beta development {number}",
                         "host": info["NetworkSettings"]["Networks"]["bridge"]["IPAddress"], "port": 22,
                         "username": "agent", "authentication": "privateKey", "hostKeyAlgorithm": "ssh-ed25519",
                         "hostKeySha256": fingerprint, "credentialReference": f"beta-{name}-key",
                         "serverPasswordReference": f"beta-{name}-server-password",
                         "stateDirectory": "/home/agent/.local/state/agentcontrol", "allowedRoots": "/home/agent/workspaces",
                         "capacity": 1, "installIfMissing": True})
        print(f"{container}: clone and pinned SSH identity ready", flush=True)
    (ROOT / ".fixture/beta-runtime-profiles.json").write_text(json.dumps(profiles, indent=2) + "\n")
    print("Runtime profiles saved under .fixture; register/enroll through AgentControl before sending tasks.")


if __name__ == "__main__":
    main()
