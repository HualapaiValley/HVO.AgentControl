# HVO.AgentControl

[![CI](https://github.com/RoySalisbury/HVO.AgentControl/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/RoySalisbury/HVO.AgentControl/actions/workflows/build.yml)
![Version 0.1.0](https://img.shields.io/badge/version-0.1.0-blue)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
[![License: Proprietary](https://img.shields.io/badge/license-Proprietary-red)](LICENSE)

Generation **V2** of a Docker-native development controller for OpenCode. The
architecture generation (`generation = 2`) is distinct from the semantic release
version: the first portal release is **0.1.0**, and it is **unreleased**: no
`v0.1.0` release tag or registry image has been published. The original application is preserved
read-only in [archive/v1](archive/v1/); it is not part of the active solution,
build, CI, or deployment configuration.

> Badge note: this is a private repository, so the GitHub Actions badge may
> require a signed-in account with access. The badge targets the workflow
> **file** (`build.yml`), not its display name, and no token or credential is
> embedded in any badge URL. There is deliberately no release or coverage badge.

## First portal slice

The .NET 10 Blazor portal (static server rendering) hosts a background
`AcpControlHost` **inside the same control container**. That host owns one
OpenCode ACP process, the loopback-only OpenCode native HTTP server, and the
durable organization/session record. A TUI client runs in tmux on the same
runtime; the browser terminal attaches to it over a same-origin WebSocket. All
runtime state lives under the private persistent `/data` volume.

This is a development slice. **Developer provisioning and task routing are not
implemented**, and the active code has **no worker bridge or reconnect
implementation** — the standalone POC only demonstrated that transport idea.
Do not expose this portal to untrusted networks or the Internet.

### Run locally

Pinned tool versions, clean-machine setup, and the C#/Blazor/API standards are
in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore -c Release --warnaserror
dotnet test HVO.AgentControl.slnx --no-build -c Release
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
dotnet run --project src/HVO.AgentControl --urls http://127.0.0.1:5054
```

The runtime is disabled by default for host-side development. Running without
`Control:Enabled=true` serves the portal and health/info endpoints but never
launches OpenCode.

### Container

```bash
docker --context home-docker compose build
python3 scripts/init-secrets.py --context home-docker
docker --context home-docker compose up -d
```

Open `http://home-docker.home.lan:5054` on the trusted LAN. The username is
`owner`; retrieve the password locally with:

```bash
docker --context home-docker compose exec -T control python3 -c \
  'from pathlib import Path; print(Path("/run/agentcontrol-secrets/owner-password").read_text().strip())'
```

Secrets initialization creates a volume and preserves an existing owner
password. Compose enables the runtime, installs OpenCode 1.18.30 and uses its
default Big Pickle provider. The container publishes only the portal on all
Docker-host IPv4 interfaces (`0.0.0.0:5054`); OpenCode's native HTTP stays on
container loopback. Nothing starts or migrates the archived V1 deployment.

## Portal surface

- `/`: authenticated Blazor terminal portal
- `/api/info`: implementation identity (`generation`, `status`, worker flags)
- `/api/control`: runtime/session/terminal status
- `/api/control/model`: model selection, same-origin only
- `/api/control/cancel`: bounded ACP cancellation request, not completion proof
- `/terminal`: same-origin authenticated terminal WebSocket, one viewer at a time
- `/health/live`: process health, not worker/provider readiness
- `/api/version`: semantic version and architecture direction

HTTP errors use RFC 9457 ProblemDetails. The built-in OpenAPI JSON document
at `/openapi/v1.json` and the `/health/ready` runtime/TUI readiness probe are
protected by owner authentication when configured. Readiness is distinct from
process liveness; a failed readiness probe must not trigger a destructive
restart. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for the API contract.

Model display reads the native session, not the startup default. OpenCode
1.18.30's attached TUI keeps its picker selection local until submission, so the
portal labels the value it can observe as the server session model. The web
selector is disabled (`modelSyncSupported = false`) because actual two-way TUI
synchronization is not available; an acknowledged ACP model-setting RPC alone
does not update the native session or the TUI picker. See
[external issue tracking](docs/EXTERNAL-ISSUES.md).

`/data/runtime.json` stores the organization/session mapping; the named data
volume retains workspace and native conversation state. A failed session load is
surfaced, not silently replaced. Detaching the browser leaves the TUI/runtime
alive. Container restart restores conversation history, not a running command.
Only an initial new-session readiness prompt is automatic.

## Development security boundary

The portal uses HTTP Basic authentication and same-origin WebSocket/cancel
checks. The Compose mapping permits direct trusted-LAN access without an SSH
tunnel. **HTTP does not encrypt the password or terminal traffic:** do not expose
this port to untrusted networks or the Internet. TLS-terminating reverse proxy
support still needs trusted forwarded-header configuration and validation;
it is not supported by this slice. Use an SSH tunnel for encrypted remote access. To
restrict it to SSH-only access again, bind `127.0.0.1:5054:8080`. The controller
and OpenCode currently share a container OS user, so prompt/tool deny rules are
defense-in-depth, **not OS isolation** from the mounted owner secret. Separate
credentials and process identities before adding untrusted repository execution.
No Docker socket or real repository is mounted in this slice.

## Direction

- One central controller with its own OpenCode runtime for management.
- Self-contained developer containers, each with OpenCode, private session
  storage and its own repository checkout. No shared worker repos/worktrees.
- A small worker-owned bridge maintains OpenCode's ACP stdio connection;
  controller disconnection must not terminate the worker's current task.
- No worker ports published. Human web/TUI access is proxied by the controller;
  a TUI attached inside tmux is optional, not the agent runtime.
- The controller owns durable dispatch, identity, authorization and recovery.
  Instructions guide agents; they are not a security boundary.
- OpenCode only. No Claude-specific adapter, Fleet service, or SSH-worker
  enrollment dependency. Remote Docker hosts require explicit connectivity.
- Keep the existing AgentControl-host GitHub App. Credential provisioning and
  worker identity design are future work; never commit App keys or tokens.

## Release and upstream

The release process (manual GHCR publish to
`ghcr.io/roysalisbury/hvo.agentcontrol`, `linux/amd64`, no `latest` tag,
`workflow_dispatch` gate) is owned by the release teammate and documented in
[docs/RELEASING.md](docs/RELEASING.md). Upstream status is tracked canonically in
[docs/EXTERNAL-ISSUES.md](docs/EXTERNAL-ISSUES.md); filing guidance is in
[docs/UPSTREAM-OPENCODE.md](docs/UPSTREAM-OPENCODE.md).

See [development guide](docs/DEVELOPMENT.md),
[architecture](docs/ARCHITECTURE.md), [roadmap](docs/ROADMAP.md), and
[POC findings](docs/POC-FINDINGS.md).
