# Beta development fleet

The fleet has three independent .NET development hosts and uses the existing lightweight coordinator. See [development plan](BETA_DEVELOPMENT_PLAN.md) and [issue #2](https://github.com/RoySalisbury/HVO.AgentControl/issues/2).

Run from the SSH host with Docker, Python 3, ssh-keygen and authenticated `gh` repository administration:

```bash
python3 scripts/init-beta-fleet.py
```

This creates only missing SSH/API/Git keys under ignored `.fixture/`, registers repository-only write deploy keys, pins GitHub's SSH public keys from its authenticated HTTPS metadata, builds the shared .NET SDK 10.0.400 image, starts three containers and clones HVO.AgentControl into each. It writes runtime profiles to `.fixture/beta-runtime-profiles.json`. Do not print or commit private keys/passwords.

Containers: `hvo-agentcontrol-beta-dev1`, `hvo-agentcontrol-beta-dev2`, `hvo-agentcontrol-beta-dev3`. Each has its own home and SSH configuration/key volumes. Two CPUs, four GiB memory and 512 PIDs are configured per development host. No host Docker socket is mounted. SDK/Git/unit checks run in the workers; Docker-based integration is assigned to CI or the host.

Register each generated profile through AgentControl, connect/ensure OpenCode, and create a Worker-role session at `/home/agent/workspaces/HVO.AgentControl`. Use `opencode/big-pickle` for the first beta, subject to actual runtime availability. The coordinator remains a Coordinator-role session outside the worker participant list. Initial inventory probes do not prove task success.

A repeat setup preserves clones, credentials and SSH host keys. Container bridge IPs may change after recreation; compare the generated profile against the saved runtime and update/reconnect deliberately. Do not replace a saved host-key fingerprint silently. Deleted runtime IDs are retired; a deliberately replaced registration needs a new ID after reconciliation.

Git credentials permit this repository only. `gh` is installed but has no account-wide API token in the development containers. Agents fetch/push via their deploy keys and return findings/evidence through AgentControl; the authenticated host publishes attributed issue/PR comments. Do not mistake host publication for model-generated review work.

Open admin terminals from the runtime cards for maintenance. Stopping the web service leaves remote native processes alive; stopping a development container interrupts its processes. Home volumes preserve repositories/native history but cannot preserve in-memory computation. Stop containers with `docker compose -f compose.beta.yaml stop`; do not use `down -v` unless intentionally deleting their data.
