# AgentControl V2 Development

The repository root is V2. `archive/v1/` is historical reference only; its
instructions do not govern V2. **The archive is read-only:** do not modify
`archive/v1/` or `archive/README.md`, reactivate V1 workflows, or run old
deployment scripts without explicit owner direction.

## Scope

Build a Docker-native controller for self-contained OpenCode workers using ACP.
No Fleet dependency, Claude-specific adapter, shared worker checkout, or
implicit reuse of existing infrastructure. The active baseline implements a
single control-host portal plus a disabled-by-default hermetic remote-worker
controller slice: schema-v12 records, approved-host validation, fixed SSH/Docker
command construction and shared bridge protocol code. Durable hire request and
revision-bound rejection are shipped, as are immutable container profiles with
a constrained devcontainer subset (#258) and per-host verified builds (#259).
Owner approval (#260) is implemented as tested code capability: a
revision-bound, same-origin owner action freezes one verified profile-revision
build, atomically creates the managed employee identity and DeveloperContainer
binding, and a resumable coordinator drives
`Approved → Provisioning → Orienting → Ready` from the frozen approval through
orientation delivery and comprehension. Approval commits the request to
`Provisioning` and durably queues that work; it runs in the background and is
rebuilt from persisted state after a restart. The #272 work freezes a managed
hire to the **controller-local Docker target** (`local-docker`): approval
requires no SSH host input, and every Docker operation runs through a privileged
`docker-helper` over a Unix socket. The control image has neither the Docker
daemon socket nor a Docker CLI; the helper is the only service that mounts the
socket and the only writer of the shared helper-socket volume. It is **not
operationally validated**: no live
owner-approved hire has been executed on any host, the
`home-docker` execution-host enrollment is still held by the owner, and
`WorkerControl` remains disabled by default. Approval is an explicit owner act
on a verified selection; nothing provisions without it. No request may
auto-create an employee and profile updates never auto-rebuild employees. #261
adds an explicit owner-only, same-origin, employee-revision-bound rebuild against
a newer verified build on the same profile and host. It preserves workspace and
home by default; an exact typed phrase authorizes either reset scope. The durable
single-flight state machine holds dispatch, fences on a fresh ownership epoch and
is synchronously driven by the API after recording intent; uncertain effects
require recovery. There is no termination or scheduling policy. Keep the three worker classes
distinct: (1) automatic controller-local managed employees provisioned through
the helper; (2) manually operated remote Docker workers reached by
controller-initiated pinned SSH + `docker exec`, never created by hiring; and
(3) future manually enrolled standalone workers that connect outbound — **not
implemented**, no listener or enrollment protocol exists. Live provisioning and
task routing are implemented hermetically, as are the viewer protocol and fixed
production worker PTY backend. The first managed disposable two-host path is now
live-accepted on an authorized disposable topology: pinned ED25519/strict SSH to
`home-dev-02`, enrolled isolated labeled worker, ACP session lifecycle, anonymous
provider prompt/tool streaming, real tool side effect, cancellation, forwarded
receipt, disconnect/reconnect epoch advance, exact reconcile completion,
idempotent resubmit, stale-lease rejection, bounded replay, worker restart, and
viewer attach were exercised, and the disposable resources were removed. This
path stays disabled by default; `WorkerControl:Enabled` remains the deployment
gate and is false by default. Key rotation and compromise re-enrollment, and
production managed hires/provisioning, are **not** validated by this evidence.
Separately, the local managed path has since reached `Ready` against the real
`docker-helper` and real OpenCode on one development machine (about 1m50s), and
that is a local dev-machine result only — not a `home-docker` deployment and not
the operationally accepted two-host path. Distinguish code capability from
operationally tested behavior.

## Repository layout

- Active code belongs in `src/`; tests in `tests/`; design notes in `docs/`.
- Architecture generation (`generation = 2`) is distinct from the semantic
  release version.

## Development toolchain and standards

Pinned versions and clean-machine commands are in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). Do not substitute a machine-wide
latest for a pin: .NET SDK `10.0.401` (`global.json`), target `net10.0`,
central package versions (`Directory.Packages.props`), Node 22 and Playwright
`1.63.0` (`tests/Browser`), Python 3.12 for CI validation, and OpenCode
`1.18.30` with the container's `python3`/`tmux` (`Dockerfile`).

`docs/DEVELOPMENT.md` also records the general C#, Blazor, and API standards
adopted from other HVO repositories:

- Blazor components with logic use sibling `.razor` (markup), `.razor.cs`
  (code-behind) and `.razor.css` (scoped styles). Keep the global design system
  in `wwwroot/css/portal.css`; do not theme with inline styles.
- APIs use the built-in ASP.NET ProblemDetails pipeline
  (`AddProblemDetails` + `UseExceptionHandler` + `UseStatusCodePages`) and the
  built-in OpenAPI JSON document at `/openapi/v1.json` behind owner auth when
  configured; no Swagger UI dependency.
- `/health/live` is process liveness only. `/health/ready` is readiness of the
  exact owned ACP session and attached TUI, not a database or worker probe.

Distinguish the target standard from what the baseline actually implements.
`docs/DEVELOPMENT.md` carries a baseline-versus-target table; keep it accurate
and never claim an endpoint is implemented or validated before it is.

## Versioning

- The application version has a **single source**: the `<Version>` property in
  `Directory.Build.props`.
- `/api/version`, the ACP handshake and portal footer derive their version from
  assembly metadata. Keep changelog headings and the README version badge in
  sync when bumping the build property; they describe that version, not another
  runtime version source.
- The first portal release is `0.1.0` and is unreleased (no release tag or
  published registry image). Local development images are not releases.

## Validation

Use the SDK pinned in `global.json`; package versions belong in
`Directory.Packages.props`.

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore -c Release --warnaserror
dotnet test HVO.AgentControl.slnx --no-build -c Release
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
```

CI (`.github/workflows/build.yml`) runs restore/build/test/format, Compose
validation, an image build and hermetic browser checks. The browser CI suites
start their own runtime (one disabled, one enabled against the checked-in fake
ACP fixture) and use no provider credentials or inference. Run them locally with
Node 22+ and Chromium:

```bash
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install chromium
npm run ci --prefix tests/Browser                # disabled-runtime smoke
npm run ci-organization --prefix tests/Browser   # enabled-runtime organization
```

Live browser suites are separate, explicitly requested checks against a running
portal; they can submit prompts and change session state. See `tests/Browser/README.md`.

Test outcomes and failure/recovery boundaries. Receipt is not execution; turn
completion is not verified task success; cancellation is not rollback. Do not
retry uncertain writes without reconciling effects.

## Implementation discipline

Follow the [execution workflow](docs/DEVELOPMENT.md#execution-workflow): record
the baseline and acceptance criteria, make the smallest verifiable change, and
finish green. A failure discovered during the work is ours to investigate even
if it predates the diff; do not waive it as unrelated or rerun until green.
Reviewers provide evidence, not votes. Verify findings against the actual code
and scope before fixing, rejecting with evidence, or owner-approved deferral.
Keep the PR summary current and verify issue/branch/CI state after merge.

Continue dependency-ready work within recorded owner authorization until the
requested batch is complete or a real blocker requires a decision. Routine
merges are progress updates, not automatic stopping points. Never broaden scope,
credentials, destructive actions or review-cycle exceptions to avoid a pause.

## Pull request process

This repository uses the HualapaiValley development model: `development/v1`
is the default daily-integration branch, `main` is the stable line that moves
only by the nightly promotion PR, and every feature PR gets an independent
review posted as `hvo-agentcontrol[bot]` with one resolvable thread per
finding. This is the process for changes **to this repository**; it is not the
managed-employee review the AgentControl product implements.

- Rulebook: [docs/REVIEW-PROTOCOL.md](docs/REVIEW-PROTOCOL.md) (levels
  Mechanical/Standard/Deep and their round caps, finding lifecycle
  `OPEN -> CORRECTED -> VERIFIED_CORRECTED -> resolved`, convergence rules).
- Procedure with commands: [docs/runbooks/pull-request-walkthrough.md](docs/runbooks/pull-request-walkthrough.md).
- Landing page and CI shape: [docs/development-v1.md](docs/development-v1.md).

Non-negotiables an agent must keep:

- Claim the issue before editing (`workflow:in-progress`, a `review:*` level
  label, a CLAIM comment). One issue per branch/PR; branch from
  `development/v1` and target it. Never push to `main`.
- The implementer and the independent reviewer are different sessions. The
  reviewer never edits the branch; the implementer never posts `VERIFIED_*`.
  Review text is never committed to the PR branch: the parent review, each
  finding thread and each `VERIFIED_*` reply are dispatched through
  `agentcontrol.yml` (`post-review`, `post-finding`, `reply-thread`) bound to
  the exact head SHA, and posted as `hvo-agentcontrol[bot]`.
- Every finding is a thread with a stable `F<n>` ID and a severity. Critical
  and High are never deferred; Medium is deferred only by the issue owner to a
  linked follow-up issue. The reviewer posts the `VERIFIED_*` disposition; the
  operator resolves the thread. Nothing is silently ignored.
- A verdict is bound to an exact head; the bot refuses to post against a moved
  PR. `APPROVE` on the head permits marking ready; the required checks then run
  on that head; convergence is asserted only after they are green. Pushing
  after `APPROVE` invalidates it. Do not amend or force-push a reviewed head.
- Merge into `development/v1` only when converged, all threads resolved, the
  head current, and the required Development v1 checks green; squash, and
  close the issue with the merge SHA and "Not promoted to main". Promotion to
  `main` is the nightly bot PR, merged by the operator with a merge commit;
  merging is never a release or a deployment.

## Secrets and data

Never commit credentials, owner password files, runtime databases, provider
transcripts, local configuration, or generated badge/token URLs. Use disposable
resources for integration tests. Commits, pushes, publishing and production
operations require explicit owner authorization.
