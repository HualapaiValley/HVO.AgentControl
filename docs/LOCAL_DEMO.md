# Running local demo

Started 2026-09-06 at the owner's request. The AgentControl server runs in Docker as `hvo-agentcontrol-demo-server`, using `compose.demo.yaml`. The SSH runtime remains in the Docker client container. VS Code and the dev container are no longer needed to run the demo.

The server has been refreshed with separate **Overview**, **Runtimes**, and **Workers** pages. Existing demo login/data and the original worker session are preserved. Reload to see the menu: Overview contains status and agent conversations, Runtimes contains host maintenance and **Add runtime → Verify**, and Workers contains setup and the worker inventory. Manual fingerprints/references remain under **Advanced connection settings** on Runtimes.

Open **http://192.168.1.14:5054** from your remote machine. The ignored repository `.env` sets `DEMO_BIND_ADDRESS=192.168.1.14` to publish the UI on this host’s LAN IP. No SSH port forward is needed. Without that setting, the demo defaults to loopback. This development demo explicitly uses HTTP on the LAN; use the TLS configuration in [operations](OPERATIONS.md) for production.

Retrieve the owner password in the repository terminal:

```bash
cat .fixture/demo-secrets/owner-password
```

After signing in, choose **Demo worker** in **Agent conversation** on Overview (or **Workers → Open conversation**). It is connected to runtime **Docker demo**, running real OpenCode **1.18.29** with the discovered model **opencode/big-pickle**. Its Git repository is `/home/agent/workspaces/demo` inside the client container. The existing owner conversation was preserved through the page update and VS Code connection recovery. An example task to try:

```text
Create hello.txt containing "Hello from AgentControl". Read it back and report the result. Do not commit.
```

Provider inference depends on the model's continued anonymous availability. Sign-in, SSH bootstrap, native API health, model discovery, native session creation and the interactive browser were checked for this environment; the initial-release report separately records real inference tests.

## Processes and retained data

| Resource | Location |
| --- | --- |
| Client container | `hvo-agentcontrol-demo-client` |
| Client image | `hvo-agentcontrol-demo-client:local` |
| Client home/workspace volume | `hvo-agentcontrol-demo-client-home` |
| Client SSH configuration/host-key volume | `hvo-agentcontrol-demo-client-ssh` |
| Server database/authentication keys | `.fixture/demo-data/` |
| Owner password, SSH key and server password | `.fixture/demo-secrets/` |
| Server container | `hvo-agentcontrol-demo-server` |
| Server log | `bash scripts/demo-server.sh logs` (Docker logs) |
| Server container control | `bash scripts/demo-server.sh` with `start`, `stop`, `restart`, `status`, or `logs` |
| Database backup before host transition | `.fixture/host-transition-backup.db` |
| Recorded runtime/session identities | `.fixture/demo-ready.json` |

Credentials, data and the transition backup are intentionally ignored by Git. The demo client uses the repository's SSH fixture image build, but AgentControl installed and runs the actual OpenCode binary; it does not use the simulated HTTP/SSE executable.

If the environment is stopped, restart the existing client and run the server helper from the repository root:

```bash
docker start hvo-agentcontrol-demo-client
bash scripts/demo-server.sh start
```

The helper builds and starts the controller container with the existing data/password bind mounts and your UID/GID. Docker’s `unless-stopped` policy keeps it running independently of the terminal or editor and restarts it after a host reboot unless explicitly stopped. AgentControl reconnects its saved runtime and session. Use `stop` for an intentional shutdown. The legacy systemd service must stay stopped: only one controller may use the database. The controller shares the default Docker bridge with the existing client so its saved SSH address remains reachable.

For development, use the host SDK pinned in `global.json` (10.0.400). Validate changes, then rebuild and restart the controller container:

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
bash scripts/demo-server.sh restart
```

To inspect the disposable repository directly:

```bash
docker exec -it -u agent -w /home/agent/workspaces/demo hvo-agentcontrol-demo-client bash
```

The previous release-validation containers remain stopped. This owner test environment is deliberately left running.

## Coordinator and worker updates

The demo now includes a separate `hvo-agentcontrol-coordinator` container and **AgentControl coordinator** worker using `opencode/big-pickle`. Its persistent home/SSH volumes are managed by the `coordinator` profile in `compose.demo.yaml`. Open **Coordination** to select that coordinator and the agents it should contact. No automatic coordination starts merely by opening the page. See [worker management and coordination](COORDINATION.md) for editing, archive/restore, structured prompts, and runtime setup.

## Agent experience update (2026-09-07)

The coordinator now has a dedicated Coordinator role and appears on Coordination, outside the worker inventory. The existing demo worker has a saved initial capability report; open its Capabilities panel to review the agent-reported inventory and measured facts. Optional coordination guidance is available on instructions. Runtime cards and agent conversations offer **Open admin terminal**, which opens a separate ephemeral SSH shell. See [operating details](AGENT_EXPERIENCE.md).
