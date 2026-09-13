# HVO.AgentControl V2

A fresh start for a Docker-native development team using OpenCode and ACP.
The original application is preserved in [archive/v1](archive/v1/); it is not
part of the active solution, build, CI, or deployment configuration.

## Baseline

This commit provides a minimal .NET 10 control-host service, health and version
endpoints, integration tests, container packaging, and CI. It does **not** yet
provision workers, connect to ACP, persist tasks, or implement authentication.
Do not expose this baseline publicly.

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore -c Release --warnaserror
dotnet test HVO.AgentControl.slnx --no-build -c Release
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
dotnet run --project src/HVO.AgentControl --urls http://127.0.0.1:5054
```

Alternatively, `docker compose up --build -d` exposes only the control host on
`127.0.0.1:5054`. Nothing starts or migrates the archived V1 deployment.

- `/`: baseline identity and implementation status
- `/health/live`: process health, not worker/provider readiness
- `/api/version`: major version and architecture direction

## Direction

- One central controller with its own OpenCode runtime for management.
- Self-contained developer containers, each with OpenCode, private session
  storage and its own repository checkout. No shared worker repos/worktrees.
- A small worker-owned bridge maintains OpenCode's ACP stdio connection;
  controller disconnection must not terminate the worker's current task.
- No worker ports published. Human web/TUI access is proxied by the controller;
  a TUI attached inside tmux is optional, not the agent runtime.
- The controller owns durable dispatch, identity, authorization, and recovery.
  Instructions guide agents; they are not a security boundary.
- OpenCode only. No Claude-specific adapter, Fleet service, or SSH-worker
  enrollment dependency. Remote Docker hosts require explicit connectivity.
- Keep the existing AgentControl-host GitHub App. Credential provisioning and
  worker identity design are future work; never commit App keys or tokens.

See [architecture](docs/ARCHITECTURE.md), [roadmap](docs/ROADMAP.md), and
[POC findings](docs/POC-FINDINGS.md).
