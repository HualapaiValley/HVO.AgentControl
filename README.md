# HVO.AgentControl

[![CI](https://github.com/HualapaiValley/HVO.AgentControl/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/HualapaiValley/HVO.AgentControl/actions/workflows/build.yml) [![Development v1 CI](https://github.com/HualapaiValley/HVO.AgentControl/actions/workflows/development-v1.yml/badge.svg?branch=development%2Fv1)](https://github.com/HualapaiValley/HVO.AgentControl/actions/workflows/development-v1.yml?query=branch%3Adevelopment%2Fv1)
![Version 0.1.0](https://img.shields.io/badge/version-0.1.0-blue)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
[![License: Proprietary](https://img.shields.io/badge/license-Proprietary-red)](LICENSE)

Generation **V2** of a Docker-native development controller for OpenCode. The
architecture generation (`generation = 2`) is distinct from the semantic release
version: the first portal release is **0.1.0**, and it is **unreleased**: no
`v0.1.0` release tag or registry image has been published. The original application is preserved
read-only in [archive/v1](archive/v1/); it is not part of the active solution,
build, CI, or deployment configuration.

> Badge note: the badge targets the workflow **file** (`build.yml`), not its
> display name, and no token or credential is embedded in any badge URL. There
> is deliberately no release or coverage badge.

## First portal slice

The .NET 10 Blazor portal (static server rendering) hosts a background
`AcpControlHost` **inside the same control container**. That host owns one
OpenCode ACP process, the loopback-only OpenCode native HTTP server, and the
durable organization/session record. A TUI client runs in tmux on the same
runtime; the browser terminal attaches to it over a same-origin WebSocket.
Controller-private state lives in the `/control-data` volume (controller UID
1001); the agent-owned `/data` volume (UID 1000) holds the OpenCode home,
workspace and conversation history.

This is a development slice. The owner portal now uses real SSR routes for
`/organization`, `/organization/departments`, `/organization/departments/{id}`,
`/employees`, `/employees/{id}`, `/hiring`, and `/system`; the organization
section is a hierarchy whose cards link departments by stable ID and whose detail
shows roles, roster, scoped availability, standing instructions and a
department-scoped request-employee path. Control schema v7 added durable,
idempotent hire requests with append-only hashed
state events and owner rejection; a request does **not** create an employee.
Control schema v8 adds immutable, content-addressed container profiles
(`/profiles`, `/profiles/{id}`, `/api/profiles`) with a constrained
devcontainer subset and the seeded `generic-employee` profile, and schema v9
adds per-host profile image builds: a revision is rendered to a deterministic
build context, built on one approved host over the pinned SSH path, and
contract-verified inside the image before its digest joins that host's
approved set. Control schema v10 adds owner approval and managed-employee
creation: an owner-only, same-origin, revision-bound `POST
/api/hire-requests/{id}/approve` freezes one hire revision against one verified
profile build, creates exactly one managed employee identity
and DeveloperContainer binding with fixed safe defaults, and records the frozen
per-binding resources a later provisioning run consumes. Control schema v11
gives an execution host a constrained transport kind (`local-docker` or
`ssh-docker`) and seeds one reserved `local-docker` host. The #272 work freezes a
managed hire to the **controller-local** Docker target: approval takes no SSH
host input, and every Docker operation is executed by a privileged
`docker-helper` process over a Unix socket. The control image contains neither
the Docker daemon socket nor a Docker CLI; the helper is the only service that
mounts the daemon socket, publishes no ports, runs read-only with
`cap_drop: ALL`, `no-new-privileges` and `init`, runs as uid 1002, and is the
    only writer of the helper-socket volume shared with control. Control schema
v12 adds durable single-flight managed-employee rebuild records. The owner-only,
same-origin `POST /api/employees/{id}/rebuild` is employee-revision-bound,
selects a newer verified revision of the same profile on the employee's host,
preserves workspace and home by default, and requires the exact typed phrase for
workspace, home, or combined reset. It records intent before effects, holds
dispatch, fences application on a fresh ownership epoch, and returns recovery
conflict for uncertain effects; profile revisions are never auto-adopted.
Recorded in

compose, `AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST` and
`AGENTCONTROL_DOCKER_GID` must be supplied for deployment; the helper fails
closed while the approved digest is empty. Recording a profile,
building it, or approving a hire does **not** provision or orient a worker:
approval leaves the request `Approved` and provisioning is a separate, resumable
trigger that drives `Approved → Provisioning → Orienting → Ready`. The owner
accepted the profile-based employee-creation design (epic #257); the explicit
data-preserving rebuild (#261) is now implemented as code capability. Control
schema v13 adds the durable bounded task domain: a canonical, normalized task
specification, a captured model report, and typed independent host verification.
`POST /api/employees/{id}/tasks` accepts an owner-revision-bound specification
and a caller idempotency key — never a binding, worker or session identity — and
the controller resolves those server-side and renders a host-generated prompt.
The task state machine is explicit (`Requested → Running
→ Completed/Failed/Uncertain/Cancelled`, with `Completed → Verified` the only
host-verified edge); a model report is labeled **Model-reported (unverified)**
and never promotes a task, and cancellation is an observation, never a rollback.
The employee page offers the bounded task form, current/recent task cards,
revision-bound Sync/Cancel/Verify controls, and revision-bound manual
dispatch-hold set/clear controls. The same employee-scoped, revision-bound
routes re-deliver current orientation and run comprehension for an existing
managed employee by reusing the hire coordination machinery and a deliberate
container replacement, with no image build. `/api/info` reports the capability
separately as `TaskControlImplemented=true`,
`TaskControlOperationallyValidated=false` and `TaskControlValidatedScope=null`.
The #217 hermetic controller records are retained in the current schema-v13 store, including
enrollment/cursor/event/request/cancellation/recovery APIs, durable
intent-first dispatch and cancellation, authenticated replay synchronization,
uncertain-write reconciliation, typed provisioning and reverse cleanup, and
minimal same-origin owner control routes.

Keep the three worker classes distinct. (1) An **automatic controller-local
managed employee** is provisioned through the `docker-helper` by an approved
hire. (2) A **manually operated remote Docker worker** is reached by
controller-initiated pinned SSH and `docker exec`; it is never created by
hiring. (3) **Future manually enrolled standalone workers** that connect outbound
are **not implemented** — no listener or enrollment protocol exists. Only class
(1) is the subject of the local managed path described here; class (2) is the
first managed disposable two-host acceptance below, and class (3) is direction,
not capability.

**#217 operational acceptance (2026-09-18).** The first managed disposable
two-host path was accepted on source `main` at `c4a7966` with worker OpenCode
`1.18.30` and Docker `29.8.0`. The worker host was `home-dev-02`, reached over
pinned ED25519 strict SSH and provisioned as an isolated labeled container with
exact latest images. Redacted acceptance evidence: ACP initialize/new/load;
anonymous provider prompt streaming to completion; a real tool side effect at
the exact `/workspace/agentcontrol-217-tool.txt` path with bytes `TOOL217_OK\n`
(SHA-256 `954cf…`); cancellation delivered on the same lease in ~2.5 s with the
active request cleared; disconnect after a forwarded receipt, reconnect with an
epoch advance, exact reconcile completion, and idempotent resubmit with no
duplicate; stale-lease rejection after a competing owner epoch; bounded
multi-generation replay both hermetic and live; worker restart advancing the
process generation with no automatic resume and the same native session loaded
explicitly; and viewer framing/auth/resize/marker plus detach/reconnect. Live
permission handling covered `[once, always, reject]` and safe-reject
`[reject]`, with a same-lease reject moving pending→decided and the prompt
completing; the compatibility fix is PR #255. Cleanup was exact: zero labeled
containers, volumes or tags remained and the local key was removed. The worker
control portal on `home-docker` is deployed from promoted `main` `7d4078b`, with
schema v12. `quick_check` is clean and foreign-key errors are zero. WorkerControl
is enabled through the ignored Compose override; product/Compose remains `true` by
default.

This path remains disabled by default. `/api/info` now reports
`WorkerControlImplemented=true` and `WorkerControlOperationallyValidated=true`,
and it carries `workerControlValidatedScope="first-managed-disposable-two-host"`
so clients can see the exact bound in band; `WorkerControlCodeAvailable=true` is
unchanged, and `WorkerControlEnabled`
still reports the effective configuration gate and is `false` in the
deployment/default configuration. Implemented/validated and the scope cover only
this first managed disposable two-host path — not key rotation or compromise
re-enrollment, and not production managed hires. Cancellation result limitation:
OpenCode reported the cancelled turn completed, so cancellation cleared the
active request but was not a rollback of the partial tool/turn. A
controller-provisioned worker attaches to Docker's default `bridge`, which
grants unrestricted outbound egress as documented below. Key rotation,
production provisioning and fresh re-enrollment remain pending separate work.
The viewer role, fixed production worker PTY backend and store-only remote read
model are hermetically code-complete; viewer input is owner-authorized
interactive execution, not a sandbox boundary. Do not expose this portal to
untrusted networks or the Internet.

The authorization to run the first managed disposable two-host path does not
extend to a production live owner-approved hire: the #260 approval,
managed-provisioning
and orientation slice has live `home-docker` evidence: employee
`emp-933d24fc110222a` reached `Ready` with one worker container and four
persistent volumes, live-model orientation comprehension, and no duplicates after
restart/recovery. The #257 live rebuild completed r1→r2 as `Applied`, advancing
epoch `600 → 604` while preserving home/workspace/session hashes and one
container/four volumes. Any termination/scheduling policy remains out of scope.

The bounded task capability (#220) remains **not deployed and not operationally
validated**. There has been no live bounded task, controller-restart task
reconciliation, cancellation/hold acceptance, independent host task
verification, or second task, so `/api/info` reports
`TaskControlOperationallyValidated=false`
with `TaskControlValidatedScope=null`; the task capability is never folded into
the worker-control flags. There is no task scheduler: a task is a single
owner-triggered bounded action, and an employee's independent host verification —
not the model report, and not turn completion — is the only path to `Verified`.

The live `home-docker` hire and rebuild results do not validate key rotation or
compromise re-enrollment. They do not authorize production managed
hiring/provisioning at scale or the unvalidated #220 task
capability described above.
The accepted path does not validate key rotation or compromise re-enrollment.
The accepted path does not authorize production managed hiring/provisioning.

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

### Disposable worker artifact

The `worker` Docker target is independent of the control image. It uses a root
PID1 fixed-operation supervisor, bridge UID 1101 and employee UID 1102, with
separate persistent `/control`, `/home/worker`, `/workspace` and
`/session` trees. The optional `worker` Compose profile is the hermetic local
development artifact: no published port, no Docker socket, `network_mode: none`
and `restart: "no"`; it is not part of normal `docker compose up`. A
controller-provisioned remote worker instead attaches to Docker's default
`bridge` network, which grants **unrestricted outbound egress**: the public
Internet through the daemon's NAT, the Docker host's bridge gateway and any host
services reachable there, and any other container co-attached to the same
bridge. The fixed contract still publishes no ingress — no `-p`, `--publish` or
`--expose`, and empty port bindings — and the worker's ACP/TUI HTTP listener
stays on container loopback while its supervisor socket is a container-private
path. That is an ingress-free and in-container guarantee, not network isolation
or egress restriction. A per-worker dedicated network, or a host firewall/NAT
policy that limits egress to the approved provider, is possible future work.

Enrollment is bootstrap-only and consumes a disposable 32-byte key from stdin.
For the checked-in Compose profile, bootstrap the named volume **before** the
supervisor starts:

```bash
docker compose --profile worker build worker-local
openssl rand -base64 32 | docker compose --profile worker run --rm --no-deps \
  --user 1101:1101 --entrypoint /usr/bin/dotnet worker-local \
  /app/HVO.AgentControl.Worker.dll --worker-bootstrap-key
docker compose --profile worker up worker-local
```

The bootstrap command inherits the profile's fixed worker/controller IDs and
`/control` named volume, but the key exists only on stdin: it is never an
environment or Compose configuration value. Only the non-secret key ID is
printed. Repeating with the exact same key verifies the existing enrollment; a
different key, symlink, hard link, wrong owner or wrong mode fails closed and is
never overwritten. If the key is absent, the supervisor exits with a fixed
bootstrap-required message instead of starting the bridge or ACP. This is a
disposable local workflow, not live enrollment. Live disposable enrollment was
exercised in the #217 acceptance, but key delivery and rotation in production
remain future work; key rotation and compromise re-enrollment are not covered by
the `/api/info` flags.

### Container

```bash
docker --context home-docker compose build
python3 scripts/init-secrets.py --context home-docker
docker --context home-docker compose up -d
```

#### Local managed-hiring deployment inputs

Controller-local managed hiring runs Docker operations through the privileged
`docker-helper` service, never through the control container. Before
`compose up` an operator supplies two inputs, normally in a `.env` file beside
`compose.yaml` (Compose reads it automatically; both are interpolated with an
empty/default fallback in `compose.yaml`, so an unconfigured deployment starts
but cannot provision):

```bash
# The host daemon socket's group id, so the helper's uid 1002 can reach it.
echo "AGENTCONTROL_DOCKER_GID=$(stat -c '%g' /var/run/docker.sock)" >> .env
# The approved worker base image digest. Build the worker image first, then pin
# its exact id; the helper refuses every container-create while this is empty.
docker --context home-docker build --target worker --tag hvo-agentcontrol:worker .
echo "AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST=$(docker --context home-docker image inspect --format '{{.Id}}' hvo-agentcontrol:worker)" >> .env
```

Then, with the controller running:

1. Build both images (`docker compose build`); `docker-helper` is the only
   service that mounts `/var/run/docker.sock`.
2. Set `AGENTCONTROL_DOCKER_GID` and
   `AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST` in `.env`.
3. `docker compose up -d`.
4. Probe the local Docker target from the UI/API
   (`POST /api/hosts/{id}/probe` for `local-docker`) and confirm it reports
   `ready`/`valid`.
5. Build the `generic-employee` profile on the `local-docker` host and confirm
   the build verifies.
6. Approve a hire against the verified profile-revision build; provisioning
   runs through the helper to `Ready`.

The `docker-helper` service fails closed: with no approved base digest it
rejects every container-create rather than approving an unpinned image, and the
control service has neither the daemon socket nor a Docker CLI.

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
bytes actually on the legacy path **before writing or chowning anything**.

That check needs the record to exist before the old image can observe anything
the rollback did, so `rollback.active` is written **before the runtime state is
republished** and before `/data`, the runtime state or the owner secret are
handed back. Ordering only the ownership changes after the marker was not enough:
the republish creates a new inode and renames it into place, and it used to give
that inode its UID 1000 ownership at creation time, so publishing was itself a
hand-back that ran first.

The sequence is now: decide, record the marker, publish **root-owned `0600`**,
hand back explicitly, then complete the marker. The marker records what the legacy
path held before (`prepublicationLegacySha256`), what the rollback will publish
(`intendedSha256`) and what it holds now (`publishedSha256`), so a replay can tell
the three cases apart:

- the legacy file still matches the pre-publication digest → the publication never
  happened; the replay performs it;
- it matches the intended digest → the publication already landed; the replay
  re-applies only the remaining hand-backs and does not touch the inode;
- it matches neither → the old image (or another writer) advanced it; the command
  refuses with all three digests and nothing is changed.

If the marker cannot be recorded the command aborts while the controller still
owns everything — including the runtime state, which keeps its prior owner *and*
its prior bytes — so neither image starts, nothing is lost, and re-running the
identical command converges. A crash after the marker is the ordinary replayable
case in both windows; until the explicit hand-back runs, the published bytes are
root-owned and the old image cannot read a state the rollback has not finished.

The deployment is already reverted when the refusal happens, so nothing needs
repairing; to go back to the isolated image, resolve the interlock explicitly with
`--resume-isolation --state-source legacy|private`.

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

That marker is `reason: unrecorded-legacy-divergence` and is deliberately not an
operator rollback: nothing was written to the legacy path, so it records no
publication and no intent, and `--revert-isolation` branches on the reason rather
than inferring one from the digests present. Treating the recorded diverged bytes
as a publication record would republish the private state over exactly the bytes
the operator is rolling back to keep. If the legacy file has moved on again since
the divergence was detected, the rollback refuses rather than picking a survivor.

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
report a confident success against nothing. `--provision-cliproxy-key` creates
or rotates the named AgentControl managed-employee key from stdin (never
printed) in that same external volume. Compose enables the runtime, installs
OpenCode 1.18.30, and selects the curated `cliproxy/default` policy lane at
medium through a direct OpenAI-compatible provider (no plugin, dashboard sync,
MCP injection or discovery). The container publishes only the portal on all
Docker-host IPv4 interfaces (`0.0.0.0:5054`); OpenCode's native HTTP stays on
container loopback. Nothing starts or migrates the archived V1 deployment.

## Portal surface

- `/`: authenticated Blazor terminal portal
- `/api/info`: implementation identity (`generation`, `status`, worker flags,
  and `workerControlValidatedScope` bounding exactly what
  `workerControlImplemented`/`workerControlOperationallyValidated` cover)
- `/api/control`: runtime/session/terminal status
- `/api/control/model`: model selection, same-origin only
- `/api/control/cancel`: bounded ACP cancellation request, not completion proof
- `/api/organization`: owner-protected authoritative store overview; `/api/organization/portal` adds host-computed employee availability, durable pending requested-hire counts, and actionable safe diagnostics. Hire approval is implemented as a revision-bound owner action (`POST /api/hire-requests/{id}/approve`) that freezes one verified profile-revision build on a ready host, creates the managed employee identity and binding, and durably queues provisioning and orientation to Ready; the live owner-approved hire completed (see the capability note above). `/api/employees/{id}` returns exact employee detail by stable ID, exposing only the current sanitized runtime error (recent logs are unsupported because no employee-scoped safe log contract exists). Same-origin revision-guarded organization/basic-instruction and role-instruction updates mark orientation stale.
- `/api/orientation`: assigned version, lifecycle timestamps, artifact metadata, evidence provenance and dispatch holds; when configuration changes before recomposition it returns the latest Stale assignment with readiness false rather than becoming unavailable
- `/api/orientation/deliver`, `/api/orientation/comprehension`, `/api/orientation/comprehension/run`, `/api/orientation/manual-hold`: same-origin owner operations for exact delivery, host-validated structured evidence, an explicitly triggered bounded ACP JSON demonstration, and independent manual hold
- `/api/permissions/grants`: same-origin owner-only staged scoped grant creation; `/{id}/revoke` revokes under optimistic revision. Grants are persisted/audited but not executable through Phase 1 ACP callbacks.
- `/api/workers/{workerId}/permissions`: safe hash/option-ID projection of remote worker pending permissions; the reject endpoint accepts only decision identity/revision, reloads the authoritative tuple/options, and never offers allow.
- `/api/workers/{workerId}/recover`: same-origin owner recovery for one exact obligation, identified by obligation ID and revision. It invokes the matching worker-side reconciliation (event acknowledgment, replay loss, replay gap, journal failure) and clears the controller obligation only after the worker accepted it, so clearing a row can never stand in for an unreconciled worker. Obligations whose real outcome cannot be established remotely — an uncertain request, a changed ownership epoch — are refused with 409 and remain open for an operator decision.
- `/api/workers/status`: enrollments, cursors, pending worker permissions, active recovery obligations and bounded event-retention diagnostics (what was pruned after the controller committed its cursor).
- `/terminal?employeeId=<stable-id>`: same-origin authenticated terminal WebSocket with exact, non-fallback routing. InternalSharedContainer attaches only the exact host-owned employee/binding/native-session tuple. DeveloperContainer is routed only when the exact enrolled/authenticated/running, hold-free cached owner lease and persisted worker viewer capability are available. The fixed worker supervisor backend launches the pinned loopback OpenCode attach command under the employee UID and passes the PTY descriptor with `SCM_RIGHTS`; two-host viewer framing/auth/resize/marker and detach/reconnect were exercised in the #217 disposable acceptance.
- `/health/live`: process health, not worker/provider readiness
- `/api/version`: semantic version and architecture direction

HTTP errors use RFC 9457 ProblemDetails. The built-in OpenAPI JSON document
at `/openapi/v1.json` and the `/health/ready` runtime/TUI readiness probe are
protected by owner authentication when configured. Readiness is distinct from
process liveness; a failed readiness probe must not trigger a destructive
restart. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for the API contract.

Selected-employee control telemetry shows the observed native session model,
transport, last synchronization time, and the model selector with its receipt and
synchronization note. Model display reads the native session, not the startup
default. OpenCode 1.18.30's attached TUI keeps its picker selection local until
submission, so the portal labels the value it can observe as the server session
model. The selector remains visible but disabled (`modelSyncSupported = false`)
because actual two-way TUI synchronization is not available; an acknowledged ACP
model-setting RPC alone does not update the native session or the TUI picker. See
[external issue tracking](docs/EXTERNAL-ISSUES.md).

`/control-data/control.db` is the authoritative schema-v10 SQLite store for organization,
department, role, employee, runtime-binding and session identity; versioned
orientation fragments/facts/assignments/evidence; layered permission policy/grants/audit;
dispatch holds; remote-worker controller records; durable hire requests and their
immutable owner approvals and frozen managed-enrollment resources; immutable container profiles; and per-host profile builds in the
controller-private volume; `/control-data/runtime.json` is retained as adoption
evidence only. The database, its WAL/SHM sidecars and the writer lock are
controller-only `0600`, and a fresh database is seed-published atomically so an
interrupted first start leaves no half-written authoritative file. Before an
existing runtime is adopted, its exact input bytes are retained create-once as
`runtime.pre-database.json` with verified SHA-256 evidence in
`runtime.pre-database.sha256`; conflicting evidence fails closed and is never
replaced. WAL,
`synchronous=FULL`, foreign keys and a bounded single-writer lock are configured
and covered by local/container tests; crash durability on the real volume
filesystem has not been crash-tested. The agent-owned data volume retains
workspace and native conversation state. A failed session load is
surfaced, not silently replaced. Detaching the browser leaves the TUI/runtime
alive. Container restart restores conversation history, not a running command.
The composed standing orientation is published without an image rebuild and the
preserved session/history remains authoritative. `Delivered` is a host publication
claim: the controller verified the session-associated file name/inode, ownership,
single-link status, exact bytes and semantic hash; it is not runtime/model
confirmation. Terminal rejected/timed-out/failed/uncertain attempts and session
rotation create a fresh assignment while preserving the old row as Stale. Linux
native inode validation is supported on the shipped `linux/amd64` architecture and
fails closed on unsupported Linux process architectures. Employee readiness is separate
from `/health/ready`: until the exact delivered version passes host validation of
the exact persisted identity, department/reporting, duty, restriction and escalation fact sets,
dispatch is held. Evidence records expose truthful `owner-submitted` or
`live-model` provenance. Owner-submitted evidence is a manual host validation
record, not proof of a live model run. The repository separately provides an
owner-triggered comprehension API that sends a strict, tool-free JSON prompt
through ACP, byte-bounds and parses the unfenced response, and subjects it
to the same host validation; no demonstration runs automatically and no live key
was consumed for this change. The existing initial new-session informational bootstrap remains separate
from employee readiness.

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
`ghcr.io/hualapaivalley/hvo.agentcontrol`, `linux/amd64`, no `latest` tag,
`workflow_dispatch` gate) is owned by the release teammate and documented in
[docs/RELEASING.md](docs/RELEASING.md). Upstream status is tracked canonically in
[docs/EXTERNAL-ISSUES.md](docs/EXTERNAL-ISSUES.md); filing guidance is in
[docs/UPSTREAM-OPENCODE.md](docs/UPSTREAM-OPENCODE.md).

See [development guide](docs/DEVELOPMENT.md),
[architecture](docs/ARCHITECTURE.md), [roadmap](docs/ROADMAP.md), and
[POC findings](docs/POC-FINDINGS.md).
