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
runtime; the browser terminal attaches to it over a same-origin WebSocket.
Controller-private state lives in the `/control-data` volume (controller UID
1001); the agent-owned `/data` volume (UID 1000) holds the OpenCode home,
workspace and conversation history.

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
launches OpenCode. With no password configured this development mode has no
authentication; use loopback only. If a password file is configured, it must
contain at least 24 characters after trimming in either runtime mode.

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
password. Blank/short or unreadable existing files fail closed and are not
replaced automatically.

An existing deployment created before controller/agent isolation has its owner
secret owned by the shared UID 1000. Re-own it once, before starting the new
image — the container refuses to start while the agent identity can still read
the secret:

```bash
# Preserve the exact currently deployed pre-isolation image before replacing
# the Compose project image. Record the printed image ID with the deployment
# evidence; refuse to proceed if this inspection/tag fails.
docker --context home-docker image inspect agentcontrol-v2-control:latest \
  --format '{{.Id}}'
docker --context home-docker image tag agentcontrol-v2-control:latest \
  agentcontrol-v2-control:rollback-pre-isolation

python3 scripts/init-secrets.py --context home-docker --migrate-owner
```

This changes ownership and mode only: the credential is never read, rotated or
rewritten, so it stays valid and the step is reversible. On first start the
entrypoint copies `/data/runtime.json` into the controller-private volume;
organization, session and conversation history are preserved. It also preserves
the byte-exact original as `/control-data/runtime.pre-isolation.json` with a
SHA-256 in a sibling `.meta.json`, written once at adoption and never
overwritten.

While the isolated image runs, `/data/runtime.json` is a **stale** root-owned
`0600` leftover, not a live copy: the controller writes only the private state,
so the legacy path keeps the organization, session and tmux owner token as they
were at adoption. Root-only ownership matters because that stale copy still
carries a token the agent must not read.

### Rolling back to the pre-isolation image

Run these exact steps in order; the script refuses to act while the controller
is running.

```bash
# 1. Stop the isolated controller. The rollback must not run underneath it.
docker --context home-docker compose down

# 2. Republish the CURRENT controller-private runtime state to
#    /data/runtime.json and hand it, /data and the owner secret to UID 1000.
python3 scripts/init-secrets.py --context home-docker --revert-isolation

# 3. Restore the retained image to the exact tag Compose uses and start without
#    building or pulling a replacement.
docker --context home-docker image tag \
  agentcontrol-v2-control:rollback-pre-isolation agentcontrol-v2-control:latest
docker --context home-docker compose up -d --no-build
```

Verify the running container's image ID equals the ID recorded before migration.
Do not use plain `compose up`, `--build`, or a floating registry tag for rollback.
If the retained local image is missing, stop: state ownership has been restored,
but the old application artifact has not, so starting a different image is not
a verified rollback.

Step 2 is **not** a pure ownership change, and it cannot be. Re-owning the stale
legacy file would resume the old image on the organization/session/token from
adoption time and silently discard everything the isolated run did, so the
rollback writes the current private bytes to `/data/runtime.json` atomically
(`O_EXCL` temporary in the same directory, final ownership and mode applied
before it is visible, `fsync`, `renameat`, directory `fsync`) and refuses any
symlinked, hard-linked or unexpectedly owned path. The owner secret and `/data`
remain metadata-only: the credential is never read, rotated or rewritten, and
its inode is preserved.

The three volumes cannot be changed in one transaction, so **the rollback is
replayable rather than atomic**: nothing is deleted, every step is idempotent,
and a failure part-way through is repaired by running the exact same command
again. Do not start either image until it reports success.

**The replay window closes when the pre-isolation image starts.** Replay is safe
only while `/data/runtime.json` is still what the rollback put there. Once the
old image has run it advances that file, and re-running `--revert-isolation`
would republish the older controller-private bytes over the newer legacy state —
the same data loss the roll-forward interlock prevents, reached from the other
side. The command therefore checks the recorded `rollback.active` against the
bytes actually on the legacy path **before writing or chowning anything**, and
refuses with both digests if they have diverged. The deployment is already
reverted at that point, so nothing needs repairing; to go back to the isolated
image, resolve the interlock explicitly with `--resume-isolation --state-source
legacy|private`. Repairing a genuinely interrupted rollback still works, because
an unchanged (or already converged) legacy file is recognised as a replay.

That check needs the record to exist before the old image can start, so
`rollback.active` is written **before** `/data`, the runtime state or the owner
secret are handed back. If the marker cannot be recorded the command aborts while
the controller still owns everything: neither image starts, nothing is lost, and
re-running the identical command converges. A crash after the marker is the
ordinary replayable case.

A deployment with no controller-private state (one that never started) fails
closed; `--accept-missing-runtime-state` opts into handing back ownership only
and letting the old image create a new organization. That path records the marker
too, so a later replay is still bounded.

### Rolling forward again after a rollback

Once the pre-isolation image has run, `/data/runtime.json` and
`/control-data/runtime.json` are two independent histories of the same
organization. There is no automatic merge, so the isolated image **refuses to
start** while the rollback recorded in `/control-data/rollback.active` is
unresolved, rather than silently freezing the newer legacy state or discarding
the isolated run. Choose the survivor explicitly:

```bash
docker --context home-docker compose down

# legacy  = keep what the pre-isolation image wrote while it was running
# private = keep the state the isolated run left behind
python3 scripts/init-secrets.py --context home-docker \
  --resume-isolation --state-source legacy

docker --context home-docker compose up -d
```

The state that is not chosen is preserved next to the private store under a
timestamped name, never deleted. The name carries a one-second timestamp, so two
resumes inside the same second would otherwise collide; archives are created
under the first unused name rather than written over an existing one, and both
survive byte-exact. This command also returns the owner secret to UID 1001 and
clears the interlock.

The same fail-closed refusal applies if the legacy file was advanced outside
isolation without a recorded rollback — an operator who started the old image by
hand, for example. In that case the entrypoint **records the divergence itself**
(with both SHA-256 digests and the detected owner) before failing, so the start
is a decision point rather than a permanent refusal with nothing to resolve; the
same `--resume-isolation --state-source` command then applies. Rolling back out
of that state instead is also supported and keeps the diverged legacy bytes
untouched.

A legacy file still owned by UID 1000 whose bytes are **identical** to the
controller-private copy is not a divergence: it is a first adoption that was
interrupted after the private copy was published but before the snapshot and the
legacy re-own. The start completes those remaining steps instead of demanding a
choice between two copies of the same state.

`--revert-isolation`, `--resume-isolation` and `--migrate-owner` are mutually
exclusive. `--project` derives the `<project>_control-data`,
`<project>_control-private` volumes and the `<project>-control-1` container for
a deployment under a non-default Compose project; the owner-secret volume is
`external` and is set with `--secrets-volume`. Missing volumes fail before any
container runs, because `docker run` would otherwise create an empty one and
report a confident success against nothing. Compose enables the
runtime, installs OpenCode 1.18.30 and uses its
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

`/control-data/runtime.json` stores the organization/session mapping in the
controller-private volume; the agent-owned data volume retains workspace and
native conversation state. A failed session load is
surfaced, not silently replaced. Detaching the browser leaves the TUI/runtime
alive. Container restart restores conversation history, not a running command.
Only an initial new-session readiness prompt is automatic.

A failed bootstrap leaves the runtime `degraded`, not ready. When its ACP
session remains established, terminal/cancel controls remain available for
inspection and recovery (`canControl`); `/health/ready` still returns 503.
Faulted, starting, disabled and stopped runtimes do not expose these controls.

## Development security boundary

The portal uses HTTP Basic authentication and same-origin WebSocket/cancel
checks. The Compose mapping permits direct trusted-LAN access without an SSH
tunnel. **HTTP does not encrypt the password or terminal traffic:** do not expose
this port to untrusted networks or the Internet. TLS-terminating reverse proxy
support still needs trusted forwarded-header configuration and validation;
it is not supported by this slice. Use an SSH tunnel for encrypted remote access. To
restrict it to SSH-only access again, bind `127.0.0.1:5054:8080`.

The controller (UID 1001) and every agent process — OpenCode, tmux, the TUI and
the PTY bridge (UID 1000) — now run as **separate OS identities** in the same
container. The owner secret and `/control-data` are readable only by the
controller; prompt/tool deny rules remain defense-in-depth on top of that
boundary rather than standing in for it. Agent processes are started by a
narrow setuid launcher exposing four fixed operations; the agent identity cannot
execute it. Because that launcher is the elevation mechanism, the container runs
without `no-new-privileges`, and every other setuid binary is stripped from the
image. See [architecture](docs/ARCHITECTURE.md#access-and-isolation). Separate
credentials further before adding untrusted repository execution. No Docker
socket or real repository is mounted in this slice.

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
