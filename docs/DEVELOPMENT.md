# Development Guide

This is the contributor-facing guide for HVO.AgentControl V2: the pinned
toolchain, clean-machine commands, coding conventions, and the API framework
standard the application targets. [AGENTS.md](../AGENTS.md) remains the
repository policy for AI coding agents; [CONTRIBUTING.md](../CONTRIBUTING.md)
covers the contribution workflow and the bounded PR process.

This guide records only general HVO engineering practice that applies here.
Product-specific rules from other repositories (CSS governance, gateway
deployment, roadmap coordination) are **not** inherited; see
[Sources](#sources) for what was consulted and why.

## Pinned toolchain

All pins live in repository files, not prose. Use the pinned versions rather
than a machine-wide latest.

| Tool | Pin | Source of truth |
| --- | --- | --- |
| .NET SDK | `10.0.401` (stable only, `rollForward: disable`) | [`global.json`](../global.json) |
| Target framework | `net10.0` | [`Directory.Build.props`](../Directory.Build.props) |
| NuGet package versions | centralized, no per-project `Version` | [`Directory.Packages.props`](../Directory.Packages.props) |
| Node.js | `22` in CI/browser jobs; `engines.node >= 20` | [`.github/workflows/build.yml`](../.github/workflows/build.yml), [`tests/Browser/package.json`](../tests/Browser/package.json) |
| Playwright | `1.63.0` | [`tests/Browser/package.json`](../tests/Browser/package.json) |
| Python | `3.12` for CI/release validation | [`.github/workflows/build.yml`](../.github/workflows/build.yml), [`.github/workflows/release.yml`](../.github/workflows/release.yml) |
| Docker / Compose | Compose v2 (`docker compose`); buildx for release | [`compose.yaml`](../compose.yaml), [`.github/workflows/release.yml`](../.github/workflows/release.yml) |
| Runtime base images | `mcr.microsoft.com/dotnet/sdk:10.0.401`, `mcr.microsoft.com/dotnet/aspnet:10.0`, `node:22-bookworm-slim` | [`Dockerfile`](../Dockerfile) |
| OpenCode | `1.18.30` | [`Dockerfile`](../Dockerfile) (`OPENCODE_VERSION`) |
| Runtime `python3` / `tmux` | provided by the runtime base image (no separate pin); verified `Python 3.12.3` and `tmux 3.4` in the local CI image | [`Dockerfile`](../Dockerfile) |

OpenCode is a runtime/container dependency and is not required on the host for
standard tests. The .NET suite requires `tmux` for isolated real-command tests
(private sockets, no user sessions or inference); CI installs it explicitly.

Phase 1 retains Node 22 across Docker and CI: it is Maintenance LTS through
2027-04-30. The Node 26 major update proposed in Dependabot #209 is not part
of the SDK patch update; Node 26 reaches its scheduled LTS start on 2026-10-28.
A major upgrade needs a coordinated compatibility assessment, not just an
OpenCode install-stage change. Docker Node major proposals are held by a scoped
Dependabot ignore. The floating `22-bookworm-slim` tag receives Node patches
through refreshed image pulls/rebuilds, not patch-version PRs. The ignore also
suppresses proposals requiring a newer Node major, so reassess it before Node 22
support ends or an advisory requires migration. Other dependencies and update
ecosystems remain enabled.

## Clean machine setup

Prerequisites for host-side development:

- .NET SDK `10.0.401` (the `global.json` pin refuses other SDKs)
- Node.js 22 for the browser checks
- Python 3.12 for the PTY-bridge contract test
- tmux (validated with 3.4) for isolated command integration tests
- Docker Engine with Compose v2 for container work
- Git

Verify the SDK before doing anything else:

```bash
dotnet --version   # must print 10.0.401
```

### Build, test, format

Run from the repository root. These are the same gates CI enforces.

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore -c Release --warnaserror
dotnet test HVO.AgentControl.slnx --no-build -c Release
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
```

Warnings are errors (`--warnaserror`); a clean build means zero warnings and
zero errors.

### Run the portal on the host

```bash
dotnet run --project src/HVO.AgentControl --urls http://127.0.0.1:5054
```

The runtime is disabled by default outside Compose. Without
`Control:Enabled=true` the portal and its non-runtime endpoints serve, but no
OpenCode process is launched.

### Browser checks

CI runs four hermetic browser suites plus two terminal unit checks. The
app-backed suites spawn the locally built app on a free loopback port; the
loopback-stub suites serve the real route modules with no app process. All need
no Docker, credentials, or model provider and perform no inference.

- `ci-smoke.mjs` (`npm run ci`) covers the disabled-runtime portal shell, status,
  terminal/model/cancel gates and responsive layout.
- `terminal-wire.mjs` (`npm run terminal-wire`) covers local text/base64 decoding
  and decoder isolation across terminal attachments.
- `state-kind.mjs` (`npm run state-kind`) independently enumerates the reachable
  server state vocabulary, grouped by its owning C# source (control wire,
  session, employee availability, remote connection, process and session
  operation), and asserts each value's presentation kind plus the
  availability-over-control precedence for held/faulted remote states.
- `system-drafts.mjs` (`npm run ci-system-drafts`) serves the real `system.js`
  behind a loopback stub and covers role-select retention, per-role
  draft/conflict/dirty cues, a failed save followed by a successful `ok` save,
  and recovery from a failed authority reload.
- `route-receipts.mjs` (`npm run ci-route-receipts`) serves the real
  `employee-detail.js` and `hiring.js` behind loopback stubs and covers neutral
  load success, visible load-failure page status, and pending/error/ok
  orientation and hire receipts.
- `ci-organization.mjs` (`npm run ci-organization`) runs with
  `Control__Enabled=true`, a disposable owner password and the checked-in fake
  ACP fixture (`tests/HVO.AgentControl.Tests/Fixtures/fake_acp.py`). It asserts
  the enabled runtime reaches ready; every routed page (including the grouped
  organization directory and department detail) ships only its own DOM and one
  active link; the overview and directory render exactly the authoritative
  departments and link them by stable ID; Operations detail shows its single
  authoritative role, one-employee roster with an availability badge and stable
  employee link, scoped availability counts and a department-scoped hiring CTA;
  Development/QA render real empty states with their own CTA; hiring preselects
  only an authoritative `departmentId` and filters roles to it, ignoring a forged
  value; invalid/unknown department ids fail safely in-page; a legacy slug hash
  resolves to the stable department id; and desktop/mobile layouts expose no owner
  password, tmux owner token or horizontal overflow. It is intentionally separate
  from the disabled smoke.

```bash
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install --with-deps chromium
npm run ci-all --prefix tests/Browser
```

Test results and screenshots are written to `artifacts/browser-ci/` and
`artifacts/browser-organization/`. See
[`tests/Browser/README.md`](../tests/Browser/README.md) for the suite list.

### Hermetic remote-worker controller checks

`RemoteWorkerControlTests` exercise schema-v3 to v4 backup/migration, strict
approved-host configuration, enrollment/resource relations, conditional lifecycle
and request transitions, event deduplication/cursor commit, recovery sets,
intent-first cancellation/provisioning, command-injection rejection, pinned SSH
flags, resource-label ownership and bounded Linux capability parsing. The bridge,
connection and provisioning services expose injectable interfaces for deterministic
fakes. These checks do not access SSH credentials or remote hosts.
`HireApprovalStoreTests`, `HireProvisioningCoordinatorTests`,
`RemoteOrientationCoordinatorTests` and `WorkerOrientationTests` cover the
schema-v9→v10 approval freeze and migration, the managed employee/binding
creation, the `Approved → Provisioning → Orienting → Ready` coordinator, the
fixed worker `install-orientation`/`orientation-comprehension` operations and
their bounded evidence. `DockerHelperTests` additionally pin the Compose/image
contract (the helper is the only service with the daemon socket; the control
image has no daemon socket or Docker CLI; the helper socket volume is shared
with control only; the approved base digest and daemon gid are interpolated) and
the shared argv grammar (employee mounts are only the four named volumes; a bind
or Docker-socket mount is refused; an empty approved digest fails closed).
`DockerHelperIntegrationTests` exercises the real helper process against a local
daemon and builds the helper image to prove the Docker CLI is present, no setuid
file survives and the identity is uid 1002; `LocalManagedHiringDockerIntegrationTests`
drives a full managed hire through the real local helper and real OpenCode.
`WorkerControl` and its hosted manager are disabled by
default. The first
managed disposable two-host path was accepted live on 2026-09-18 under explicit
owner authorization and the disposable resources were removed; the flags in
`/api/info` (`WorkerControlImplemented`, `WorkerControlOperationallyValidated`)
report that first path, with `workerControlValidatedScope` carrying the exact
`first-managed-disposable-two-host` bound, while `WorkerControlEnabled` remains
the deployment/configuration gate and is false by default. The #260
approval/provisioning/orientation slice and the #261 explicit data-preserving
rebuild are code capabilities with hermetic coverage, and `home-docker` also has
live acceptance: employee `emp-933d24fc110222a5` reached `Ready` with one
container/four volumes and live-model comprehension, with no duplicate after
restart/recovery. The live rebuild completed r1→r2 as `Applied` (epoch 600→604),
preserving home/workspace/session hashes and one container/four volumes. Key
rotation/compromise re-enrollment and production managed hiring/provisioning at
scale remain unvalidated. Any termination/scheduling policy is out of scope.

The approved-host capability probe is parsed from the real captured output of
`docker system info`, `docker version` and one `df -B1 --output=avail` of the
daemon's own `DockerRootDir` (checked in under `tests/.../Fixtures/`), not from
controller-invented fields. A host that answers but cannot prove a mandatory
capability is recorded `invalid`; only an unreachable or unparsable host is an
error. Storage locality fails closed: it is accepted only for a known local
storage driver with no non-local volume plugin installed.

#### Viewer and PTY scope

The viewer protocol is covered by hermetic fakes plus a local Unix
`SCM_RIGHTS` PTY descriptor contract for the production backend, a real
loopback WebSocket check of the browser-facing close handshake, and a Python
harness against the real supervisor source. Owner viewer input can execute
employee code, and its framing limits are not a sandbox.

These are descriptor and protocol primitives. The real OpenCode `attach` TUI is
exercised as a CLI contract inside the worker image, and the #217 disposable
two-host acceptance (2026-09-18) exercised live viewer framing/auth/resize/
input-output marker and detach/reconnect over pinned strict SSH. Production
viewer operation at scale remains unvalidated.

Viewer teardown is never silently assumed. An unconfirmed `viewer-stop` is
retried on bounded fresh supervisor connections, then resolved against the
supervisor's `viewer-status` for the exact handle; a still-running viewer
raises `WorkerTerminalStopUncertainException` and suppresses viewer
availability until a later attach proves the slot is free. It does not hold
dispatch, because an unconfirmed viewer teardown says nothing about prompt
safety.

### Profile build checks

`ProfileBuildTests` are hermetic: the deterministic build-context renderer
(one fixed-metadata `Dockerfile` tar; identical inputs, identical bytes and
hash), the fixed `docker build -`/`image tag`/`image inspect`/verify command
grammar, the pure contract verifier over captured inspect and verification
shapes, the per-host build state machine (queued → building → verifying →
built/rejected/failed/uncertain, one live and one verified build per
revision/host), the per-host approved-digest set, and the coordinator against
a scripted host (build, contract rejection, missing base, transport loss →
uncertain → reconcile by tag). No SSH and no Docker.

`WorkerImageContractTests.RealProfileBuildProducesAVerifiableChildOfTheWorkerBase`
runs the controller's own argv against the local daemon: it renders the seeded
`generic-employee` revision, pins the freshly built worker image, builds the
child, proves the verifier accepts the real inspect/verify output and that the
toolchain is present as the employee, then builds a hostile fragment that
plants a setuid binary and proves the fixed trailer stripped it. It needs
Docker and runs in the `Docker config and image` job (`AGENTCONTROL_DOCKER_REQUIRED=1`),
not in the Development v1 gate.

The #268 hardening tests additionally prove the production entrypoint is the
absolute isolated Python vector (`-I -S`), image environment is base-identical
(`ENV` is not permitted in fragments), structured `containerEnv` is applied
only to employee children, candidate mountpoints are empty and runtime mounts
use `volume-nocopy`, system/root OpenCode policy and `/etc/dotnet` are absent,
CA trust and full xattr maps on pinned contract trees match the base, and the
standalone dotnet connector/bootstrap commands clear loader and startup-hook
environment variables.

### Worker bridge checks

The worker is a separate build target and optional Compose profile; normal
control startup does not include it.

```bash
docker build --target worker -t hvo-agentcontrol:worker-tests .
AGENTCONTROL_DOCKER_REQUIRED=1 dotnet test HVO.AgentControl.slnx -c Release \
  --no-build --filter 'FullyQualifiedName~WorkerImageContractTests'
docker compose --profile worker config
python3 -m py_compile src/container/worker-supervisor.py
```

`WorkerBridgeTests` are host-local and use disposable keys/databases plus fake
streams plus a separate test-only lock-holder executable that is not copied into
the worker image. They cover persistent advisory-lock reopen/live-holder rejection,
separate bounded ACP/control codecs, truthful EOF/protocol/transport reconciliation,
host/session/epoch-bound permissions with count/byte caps, protected holds,
stale-socket fencing, single-write JSON-RPC cancellation, sanitized journal data,
exact durable replay-gap sets/caps, replay-loss marker reconciliation, journal-FK
pruning across generations, recoverable injected observation-store failures and
exact journal-failure reconciliation, reconnect fencing and terminal container
process-slot behavior. They require no provider credentials or inference. `WorkerImageContractTests`
build and run the real image with alternate UIDs and validate private path/socket
access, empty bridge capabilities/setuid inventory and PID1 signal/reaping. They
also run the controller's own fixed command construction against a real local
daemon: the ephemeral key bootstrap with controller-encoded bytes, then the
long-lived container, asserting the resulting argv, environment, capabilities,
labels, absent port bindings, the fixed default `bridge` network and the four
named-volume mounts. The container contract test then starts that container and
waits a bounded interval for the fixed ACP process (`/usr/local/bin/opencode
acp --port 4096 --hostname 127.0.0.1 --cwd /workspace --pure`). It proves
`/sys/class/net/eth0` exists, that the namespace's IPv4 table
(`/proc/<pid>/net/tcp`) has its only `LISTEN` (:4096) entry on `127.0.0.1`, and
that the IPv6 table (`/proc/<pid>/net/tcp6`) has no `LISTEN` entry on :4096, so
ACP is never on `0.0.0.0`, the container's bridge address, or an IPv6 wildcard.
A controller-provisioned worker is genuinely on Docker's default `bridge`, which
grants unrestricted outbound egress (the public Internet, the Docker host's
bridge gateway/host services and co-attached containers); the test proves no
published ingress and loopback-only ACP, not network isolation or egress
restriction. A per-worker dedicated network or a host firewall/NAT policy that
limits egress to the approved provider is future work.
The suite uses disposable resources — no SSH, no registry and no provider
credentials. The container is attached to Docker's default bridge and can egress,
but this test submits no prompt and its assertions do not depend on Internet or
DNS reachability once the image is built; CI already requires network access for
npm and NuGet. The CI step asserts the exact suite count.

The worker key is written into the control volume by that ephemeral bootstrap
container **before** the long-lived container is created, so no supervisor can
start without an enrolled key. The controller transmits it as
`base64(key) + "\n"` on standard input only; never put a worker key in argv or
environment. A repeated bootstrap with the identical key is a verified no-op,
which is what makes bootstrap reconciliation safe to retry; a different key
fails closed rather than overwriting. Rotation and remote SSH delivery are not
development helpers in #213.

### Container

Local development uses the `home-docker` context in this workspace; substitute
your own context if it differs.

```bash
docker --context home-docker compose build
python3 scripts/init-secrets.py --context home-docker
docker --context home-docker compose up -d
```

`init-secrets.py` creates the owner password volume and preserves an existing
password. Retrieve the password only when needed (never commit it):

```bash
docker --context home-docker compose exec -T control python3 -c \
  'from pathlib import Path; print(Path("/run/agentcontrol-secrets/owner-password").read_text().strip())'
```

### Live checks (optional, operator-only)

The live browser suites talk to a running container, read the owner password at
runtime, can submit prompts, and change session state. They are **not** part of
`npm run ci` and must not run in CI. They require a loopback tunnel and the
`home-docker` context (override with `DOCKER_CONTEXT`); `CHROME_PATH` is an
optional override for the Chromium binary.

Provider/inference checks are separately requested, explicitly authorized
operator checks. Normal development and CI use no provider credentials.
All child processes strip ambient provider keys as well as GitHub/control
credentials. Container-level OPENAI_API_KEY or similar ambient variables are
not a provider provisioning interface. `opencode/big-pickle` requires no key and
is used only when explicitly selected. The implemented CLIProxy portability
seam accepts a fixed endpoint plus absolute secret-file path, generates a direct
OpenAI-compatible provider with only the committed control exposure profile,
carries explicit `reasoningEffort` on every advertised variant, and exports the
validated file value only to the OpenCode child as `CLIPROXY_API_KEY`; it never
copies an inherited workstation key, and tmux/PTY children never receive it.
Missing/invalid CLIProxy configuration faults before process start without Big
Pickle fallback. See [CLIProxy model policy](CLIPROXY-MODEL-POLICY.md). The code
is ready; deployment is pending a reviewed merge and provisioning of the named
AgentControl key through `scripts/init-secrets.py --provision-cliproxy-key`
(stdin only, never printed). Normal gates use only disposable fake
secrets/endpoints. The key is provisioned after merge; do not activate with the
general interactive OpenCode/workstation key.

## C# and .NET guidelines

Adopted from the general HVO conventions in the sources below; scoped to this
repository's architecture.

- Follow [`.editorconfig`](../.editorconfig) and keep `dotnet format` clean.
- Keep the build at **zero warnings, zero errors**. Nullable reference types and
  implicit usings are enabled; do not use a null-forgiving `!` without
  justification.
- Async end to end. Do not block with `.Result`, `.Wait()`, or
  `GetAwaiter().GetResult()`. Pass `CancellationToken` through I/O, HTTP, and
  background work.
- Respect DI lifetimes: no scoped services captured by singletons and no
  captive dependencies. Dispose `IDisposable`/`IAsyncDisposable` correctly.
- Separate concerns: endpoints and Blazor components call services; business
  logic does not live in the endpoint/component layer.
- Guard clauses and validation at trust boundaries. Do not swallow exceptions
  with a broad `catch (Exception)`; log with structured `ILogger<T>` and never
  log credentials, tokens, or private transcripts.
- Prefer explicit types and named records/enums over magic strings. Keep methods
  small and focused.
- Add XML documentation comments to public APIs.
- Add or update tests for changed behavior, including error paths. Tests must be
  deterministic (no `Thread.Sleep`, no wall-clock dependence without
  abstraction).
- Package versions are centralized. Do not add a NuGet package without a
  documented reason in the PR.

## Blazor code-behind and style separation

The owner standard is explicit file separation for components that carry logic
or styles:

- `Foo.razor` — markup only.
- `Foo.razor.cs` — code-behind: lifecycle, parameters, injected services, and
  event handlers. Use a `partial` class matching the component name.
- `Foo.razor.css` — CSS isolation for styles scoped to that component.

A markup-only static component may omit the code-behind when it truly has no
server logic. In the routed owner portal, page markup is static SSR and behavior
lives in external modules under `wwwroot/js`, so the baseline ships no
`.razor.css` bundle: shared styles belong in `wwwroot/css/portal.css` and
route-specific third-party assets are declared per page via `HeadContent`. New
interactive components that carry server logic or component-scoped styles must
follow the three-file triad.

Additional Blazor rules:

- Keep the global design system in `wwwroot/css/portal.css`; `.razor.css` only
  refines component-scoped elements. Use `::deep` sparingly, as already done for
  xterm internals.
- Do not theme with inline `style=""` attributes; use a class or scoped CSS.
- Put JS interop in a sibling `.razor.js` or a module under `wwwroot/js`; do
  not inline large script blocks in markup. Wrap `OnAfterRenderAsync` interop in
  try/catch so a JS failure does not tear down the circuit.
- Request the least interactive render mode needed. The baseline is static
  server rendering; do not add `InteractiveServer` without a concrete need.
- Vendor third-party front-end assets locally with their license (as with
  xterm). Do not introduce a CDN dependency at runtime.

## API framework standard

The application targets the built-in ASP.NET Core patterns below. This section
separates the **standard** from what the baseline currently implements, so docs
do not overclaim.

### ProblemDetails

Use the built-in RFC 9457 ProblemDetails pipeline:

- `builder.Services.AddProblemDetails()`
- `app.UseExceptionHandler()`
- `app.UseStatusCodePages()`

New endpoints return structured problems with `Results.Problem`,
`Results.ValidationProblem`, `TypedResults.Problem`, or
`TypedResults.ValidationProblem`. Do not add new ad-hoc `{ "error": "..." }`
response shapes. A client should be able to rely on the standard fields
(`type`, `title`, `status`, `detail`, `instance`, `traceId`).

### OpenAPI

Use the built-in `Microsoft.AspNetCore.OpenApi` services and serve the generated
JSON document at `/openapi/v1.json` (`AddOpenApi()` / `MapOpenApi()`). The JSON
document is the contract; external tools can consume it. **No Swagger UI /
Swashbuckle dependency is added** for this slice. When
`Control:OwnerPasswordFile` is configured, the document and all other paths
except `/health/live` sit behind owner Basic authentication.

### Health

Control availability is separate from readiness: an established `degraded`
session allows terminal/cancel recovery while retaining its error and 503
readiness. A timeout does not prove an operation never ran; a failed model
readback is uncertain and must not be reported as an unchanged selection.

Health is deliberately split and must not overstate dependency coverage:

- `/health/live` — process liveness only. It answers whether the web process is
  responding, nothing more. It is the one unauthenticated path.
- `/health/ready` — application readiness: the exact owned OpenCode ACP session
  and the attached TUI/tmux readiness. It is **not** a database check and
  **not** a statement that workers or a model provider are ready.

V2 does have an authoritative controller-private SQLite store at the fixed path
`<PrivateDataDirectory>/control.db` (see below), but readiness deliberately does
not depend on it: the store is opened and validated before the runtime starts and
`/health/ready` never probes it. This differs from archived V1, whose
`/health/ready` checked control-plane database connectivity. Do not reintroduce a
database probe into the V2 readiness signal. Employee orientation readiness is a
separate persisted state exposed by the organization/orientation APIs; changing or
failing orientation must not change the control host's health semantics.

The portal readiness contract always requires its TUI. Setting
`Control:EnableTerminal=false` intentionally leaves `/health/ready` at 503,
even with a functioning headless ACP session. An existing tmux session with a
missing/invalid owned-pane marker requires operator recovery; it is not silently
adopted or replaced. Normal recovery preserves other windows and retained dead panes.

### Status codes

Keep the existing contract semantics: `400` validation, `401` authentication,
`403` same-origin rejection, `404` not found, `409` state conflict, `502`
unconfirmed upstream change, `503` not ready or cancellation not accepted.

### Baseline vs target

| Surface | Baseline at `002e826` | Implemented in review corrections |
| --- | --- | --- |
| Error responses | Ad-hoc `{ "error": ... }` and bare status codes | Built-in ProblemDetails via `AddProblemDetails` + `UseExceptionHandler` + `UseStatusCodePages` |
| OpenAPI | None | JSON contract at `/openapi/v1.json`, owner-auth when configured, no Swagger UI |
| `/health/live` | Implemented, returns `{ "status": "healthy" }`, process only, unauthenticated | Unchanged: process liveness only |
| `/health/ready` | Not implemented | Readiness of the exact owned ACP session and attached TUI |
| `/`, `/api/control`, `/api/control/model`, `/api/control/cancel`, `/terminal`, `/api/version` | Implemented | Unchanged |
| `/api/info` | Implemented | Adds `workerControlValidatedScope` and `taskControlValidatedScope` to bound the accepted capability claims |
| `/api/organization` | Not implemented | Owner-protected overview plus same-origin revision-guarded rename and basic-instruction update backed by the authoritative SQLite store; employee rows include orientation readiness/holds |
| Hire approval (`POST /api/hire-requests/{id}/approve`) | Not implemented | Owner-only, same-origin, revision-bound approval that freezes one hire against one verified profile build on the **controller-local Docker target** (`local-docker`; no SSH host input) and atomically creates the managed employee identity and DeveloperContainer binding, then durably queues provisioning and orientation to Ready. Docker operations run through the privileged `docker-helper` over its Unix socket; the control image has neither the daemon socket nor a Docker CLI. Code capability with hermetic coverage plus the live owner-approved hire completed on `home-docker`; WorkerControl is enabled through the ignored Compose override |
| Employee rebuild (`POST /api/employees/{id}/rebuild`, `GET /api/employees/{id}/rebuilds`) | Not implemented | Owner-only, same-origin, employee-revision-bound synchronous rebuild against a newer verified same-profile build. Durable intent precedes effects; workspace/home are preserved unless the exact typed reset phrase is supplied; single-flight, dispatch hold, fresh-epoch fencing and uncertain-effect recovery are enforced. The employee page shows profile status and history and never auto-adopts. Code capability with hermetic coverage plus the live rebuild completed on `home-docker`; `home-docker` is on promoted `main` `cae2ebc` / schema v13 |
| Bounded employee tasks (`POST`/`GET /api/employees/{id}/tasks`, `GET /api/tasks/{id}`, `/sync`, `/cancel`, `/verify`, `PUT /api/employees/{id}/dispatch-hold`) | Not implemented | Owner-only, same-origin, employee-revision-bound bounded task contract (#220, schema v13). Bounded normalized specification (workspace root under `/workspace/`, allowed paths, closed `read`/`edit`/`test` tools, forbidden actions, maximum seconds, closed test recipe), server-resolved binding/worker/session, host-generated prompt, client idempotency key. `Requested → Running → Completed/Failed/Uncertain/Cancelled`; only an independent host verification moves `Completed → Verified` and a model report never does. Sync never resubmits; cancellation is not rollback. Manual hold controls affect only the owner manual hold. Operationally validated 2026-09-21: the first local managed employee ran two bounded tasks end to end and each reached `Verified` on the controller-local Docker target, across a control restart, a cancellation/hold exercise and an orientation revision. The live evidence was collected on promoted `main` `cae2ebc` / schema v13, whose `/api/info` still reports `TaskControlOperationallyValidated=false`; the `docs/220-live-acceptance` branch reports `TaskControlImplemented=true`, `TaskControlOperationallyValidated=true`, `TaskControlValidatedScope="first-local-managed-two-task-restart-verification"` once promoted, and that is the later evidence line, not current deployed behavior. No scheduler, multi-agent routing, production repository/GitHub write or release publication |
| Employee-scoped orientation re-delivery (`POST /api/employees/{id}/orientation/deliver`, `/orientation/comprehension/run`) | Not implemented | Owner-only, same-origin, revision-bound re-delivery and comprehension for an existing managed employee. Reuses the hire coordination machinery and deliberate container replacement, preserves volumes and the authoritative session, and performs **no image build**. Stale orientation still blocks dispatch through existing holds. Backed by the 2026-09-21 #220 acceptance orientation revision evidence |
| `/api/orientation*`, `/api/roles/{id}/instructions`, `/api/permissions/grants*` | Not implemented | Owner-authenticated stale-readable orientation status, host-verified assignment delivery with persisted restart-required generation, authoritative revisioned role-fragment instruction update, assignment-bound owner/live comprehension, manual hold and staged grant/revoke operations; host-started malformed/empty/oversized/non-terminal/ACP-error turns persist live-model failure, timeout/caller cancellation request bounded remote cancellation and fence retries, grants are not ACP-executable in Phase 1, permission callbacks persist synchronous rejection plus every matched restriction ID, and all mutations require same origin and ProblemDetails |

The new endpoints and ProblemDetails behavior have local regression coverage;
publication remains subject to CI and PR review. `/health/ready` is a
fail-closed readiness observation: unknown native status yields 503 during
startup or transient probe failures. Do not use readiness failures as an
automatic restart signal; `/health/live` is the process liveness probe.

The authoritative store path is fixed at
`<PrivateDataDirectory>/control.db`; there is no `Control:DatabasePath`
configuration override. The rollback tooling, the container layout preparation
and the host all resolve that same path (`/control-data/control.db` in Compose),
so no deployment can silently select a different authoritative store. The API
framework change itself does not alter the runtime data schema: `/data/runtime.json`
remains adoption evidence only, and the SQLite store is the separate #215
persistence surface.

## Execution workflow

A **development batch** is the dependency-ordered set of issues being delivered
under an owner request. A **review round** is one review (initial `R0` or
correction `R<n>`) of one PR's exact range under the
[Development v1 review process](REVIEW-PROTOCOL.md); the level sets the round
cap. Deferred findings enter the next development batch through their linked
follow-up issues.

1. Establish the starting point: inspect worktree changes, target/base SHA,
   issue prerequisites, open deferrals, recorded authorization and applicable CI.
   Verify the pinned toolchain before implementation. If a required SDK/tool is
   missing, use an explicitly provisioned isolated toolchain or the pinned CI
   environment; never silently substitute a version. Distinguish local checks
   from CI evidence and state which were not run.
2. Translate the issue into a small acceptance checklist and focused regression
   tests. Keep one implementation issue per PR. Prefer a bounded vertical slice
   over a speculative framework. Contracts specify decisions, interfaces and
   safety/failure boundaries; add implementation detail only where necessary for
   feasibility or safe execution. If scope must split or expand, update linked
   issues/dependencies before doing the extra work and obtain approval where it
   exceeds the owner's request.
3. Implement and validate outcomes, including denial and failure paths. Keep a
   previously green baseline green after the change. Investigate every discovered
   failure, including intermittent or apparently pre-existing failures. Capture
   the error and environment, reproduce or compare baselines where feasible,
   and add a regression for the diagnosed cause. Do not disable coverage, add
   masking sleeps/retries, or rerun until a failure disappears. A bounded rerun
   after an evidenced infrastructure interruption can be diagnostic; retain both
   results and do not use it to waive an unexplained repeatable failure.
4. If a separate defect blocks the PR, track/fix it in a focused issue/PR, then
   update the blocked branch and revalidate/review the changed base/head. A
   pre-existing cause is not a reason to merge red. Do not call a fix complete
   based only on publication, local success or a previous head's green CI.
5. Use the [review process](REVIEW-PROTOCOL.md) and the
   [walkthrough](runbooks/pull-request-walkthrough.md). Keep the PR body current
   after head/base changes and review/validation results: scope, current SHA
   pair, checks, finding dispositions and deferrals. Comments preserve history;
   the body is the current summary, not a stale plan.
6. After an authorized merge, verify the actual merge SHA and issue closure,
   ensure unfinished/deferred issues remain open, update original finding links
   when follow-ups truly finish, and reconcile local/remote branch state without
   overwriting unrelated changes. Inspect the resulting `development/v1` push
   run; investigate a failure before relying on that baseline. A pending run is
   not a pass. Promotion to `main` is the nightly bot PR; deployments are taken
   from `main`. Record the next dependency-ready issue and continue authorized
   work.

Adapt the batch to evidence, not convenience: fix blocking regressions first,
carry approved deferrals first in the next batch, and parallelize only independent
work with clear file/resource ownership. Pause for new credentials/permissions,
unapproved destructive operations, expanded scope, unavailable selected models
without an approved fallback, exhausted review cycles, or unresolved safety/data
integrity decisions. State the blocker and the decision needed, not just that
progress stopped. A routine issue completion does not require another start
request when the owner already authorized continuing the batch.

## Pull request process guardrails

These bounds keep review and delivery predictable while the project is small.
They summarize the [Development v1 review process](REVIEW-PROTOCOL.md); that
document governs where they differ.

- **Independent review, distinct identity.** Review is performed by a session
  other than the implementing one, against an immutable exact range, and posted
  as `hvo-agentcontrol[bot]` through the `AgentControl` workflow so the record
  is attributable to an identity distinct from the implementer.
- **Levels bound rounds.** Mechanical (1), Standard (1 + 2 corrections), Deep
  (1 + 3 corrections). An exceptional focused review is allowed only for a
  CI-discovered code defect, a late security/data-loss defect or a material
  base-sync interaction, with the reason recorded.
- **Triage everything.** Every finding is a thread with a stable ID and a
  severity; every one gets a verified terminal disposition. Nothing is silently
  ignored.
- **Deferrals are explicit.** Only the issue owner defers, only Medium and below,
  only to a linked follow-up issue that records the source evidence and residual
  risk. Critical and High are never deferred.
- **CI green is necessary, not sufficient.** Required checks must pass on the
  exact reviewed head, but unresolved threads, a stale reviewed SHA or an
  unsynchronized base still block. Do not post a converged verdict before the
  checks have run.
- **Squash into `development/v1`; merge commit into `main`.** Do not amend or
  force-push a reviewed head. Merging never triggers a release or a deployment.

## Notes for agents

- V2 is the active repository; `archive/v1/` is read-only and is not part of the
  build, CI, or deployment.
- Distinguish planned, implemented, and validated behavior in code, tests, and
  docs. Do not restate another repository's product rules as if they governed
  this one.
- Commits, pushes, publishing, and production operations require explicit owner
  authorization.

## Sources

General HVO guidance consulted (read-only) while writing this guide. Only the
portable, applicable practices were adopted; repository-specific product rules
were intentionally not copied.

- HVO.WebSite `AGENTS.md` — C#/.NET and Blazor review expectations, zero-warning
  gate, code-behind/triad convention:
  https://github.com/HualapaiValley/HVO.WebSite/blob/main/AGENTS.md
- HVO.WebSite `CONTRIBUTING.md` — branch naming, conventional commits, workflow:
  https://github.com/HualapaiValley/HVO.WebSite/blob/main/CONTRIBUTING.md
- HVO.WebSite `.github/copilot-instructions.md` — Blazor component triad, async
  data access, CSS/theme separation:
  https://github.com/HualapaiValley/HVO.WebSite/blob/main/.github/copilot-instructions.md
- HVO.WebSite `docs/CSS_GOVERNANCE.md` — style separation and theme tokens:
  https://github.com/HualapaiValley/HVO.WebSite/blob/main/docs/CSS_GOVERNANCE.md
- HVO.RoofController `.github/copilot-instructions.md` — thin controllers,
  DI, XML docs on public APIs:
  https://github.com/HualapaiValley/HVO.RoofController/blob/main/.github/copilot-instructions.md
- HVO.RoofController `CONTRIBUTING.md` — prerequisites, conventional commits:
  https://github.com/HualapaiValley/HVO.RoofController/blob/main/CONTRIBUTING.md
- HVO.SDK `CONTRIBUTING.md` — centralized package management, zero-warning
  build, PR checklist:
  https://github.com/RoySalisbury/HVO.SDK/blob/main/CONTRIBUTING.md
- HVO.SkyMonitor `AGENTS.md` — pinned SDK/global.json, code-behind + scoped CSS
  for Blazor, validation ladder:
  https://github.com/HualapaiValley/HVO.SkyMonitor/blob/main/AGENTS.md
- HVO.SkyMonitor `.agents/skills/pr-lifecycle/SKILL.md` — exact-SHA review,
  bounded rereviews, per-finding disposition:
  https://github.com/HualapaiValley/HVO.SkyMonitor/blob/main/.agents/skills/pr-lifecycle/SKILL.md
