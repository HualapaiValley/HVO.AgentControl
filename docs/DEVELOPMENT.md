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

CI runs two hermetic suites. Both spawn the locally built app on a free loopback
port, need no Docker, credentials, or model provider, and perform no inference.

- `ci-smoke.mjs` (`npm run ci`) runs with `Control__Enabled=false` and no owner
  password and covers the portal shell, disabled-runtime status, terminal/model/
  cancel gates and responsive layout.
- `ci-organization.mjs` (`npm run ci-organization`) runs with
  `Control__Enabled=true`, a disposable owner password and the checked-in fake
  ACP fixture (`tests/HVO.AgentControl.Tests/Fixtures/fake_acp.py`). It asserts
  the enabled runtime reaches ready; all five navigation items render; Overview
  reports exact department/availability counts and unsupported pending approvals;
  Operations selects the one persisted Operations/IT employee by stable ID;
  Development/QA show empty states; safe detail and orientation actions render;
  and desktop/mobile layouts expose no owner password, tmux owner token or
  horizontal overflow. It is intentionally separate from the disabled smoke.

```bash
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install --with-deps chromium
npm run ci --prefix tests/Browser
npm run ci-organization --prefix tests/Browser
```

Test results and screenshots are written to `artifacts/browser-ci/` and
`artifacts/browser-organization/`. See
[`tests/Browser/README.md`](../tests/Browser/README.md) for the suite list.

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
access, empty bridge capabilities/setuid inventory and PID1 signal/reaping. The
local connector mode reads its key from the first stdin line; never put a worker
key in argv or environment. Rotation and remote SSH delivery are not development
helpers in #213.

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
server logic. In the current `0.1.0` baseline, `Home.razor` is static SSR whose
behavior lives in the external `/js/terminal.js` module, so only
`Home.razor.css` exists as a sibling. New interactive components must follow the
three-file triad.

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
| `/`, `/api/info`, `/api/control`, `/api/control/model`, `/api/control/cancel`, `/terminal`, `/api/version` | Implemented | Unchanged |
| `/api/organization` | Not implemented | Owner-protected overview plus same-origin revision-guarded rename and basic-instruction update backed by the authoritative SQLite store; employee rows include orientation readiness/holds |
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
under an owner request. A **review cycle** is one independent reviewer pair on
one PR's exact base/head. An authorized fourth review cycle is not a new batch
or permission to carry deferred work indefinitely. Deferred issues enter the
next development batch and must be assessed in that batch's relevant PR reviews.

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
5. Use the [review protocol](REVIEW-PROTOCOL.md). Keep the PR body current after
   head/base changes and review/validation results: scope, current SHA pair,
   checks, reviewer evidence, finding dispositions, deferrals and authorization.
   Comments preserve history; the body is the current summary, not a stale plan.
6. After an authorized merge, verify the actual merge SHA and issue closure,
   ensure unfinished/deferred issues remain open, update original finding links
   when follow-ups truly finish, and reconcile local/remote branch state without
   overwriting unrelated changes. Inspect the resulting main CI; investigate a
   failure before relying on that baseline or deploying it. A pending run is not
   a pass. Record the next dependency-ready issue and continue authorized work.

Adapt the batch to evidence, not convenience: fix blocking regressions first,
carry approved deferrals first in the next batch, and parallelize only independent
work with clear file/resource ownership. Pause for new credentials/permissions,
unapproved destructive operations, expanded scope, unavailable selected models
without an approved fallback, exhausted review cycles, or unresolved safety/data
integrity decisions. State the blocker and the decision needed, not just that
progress stopped. A routine issue completion does not require another start
request when the owner already authorized continuing the batch.

## Pull request process guardrails

These bounds are agreed with the repository owner. They keep review and delivery
predictable while the project is small.

- **Phase 1 reviewer selection.** Follow the [two-model protocol](REVIEW-PROTOCOL.md).
  Two distinct randomly selected approved models, excluding the coordinator,
  review independently; record verifiable selection and routing limits. The
  pair counts as one cycle. Failed review/task models may be replaced from the
  applicable approved list under the recorded replacement procedure; disclose
  every replacement and preserve independence and partial-effect reconciliation.
- **Independent review.** Review is performed by a party other than the
  implementing agent (a separate agent/harness or a human), against an immutable
  exact head SHA. A review result is valid only for the SHA it reviewed. PR #208
  Round 1 baseline is `002e8261ad0f6f48dd2a55f8d6f82c5bee9f94c0` (`002e826`).
- **Triage everything.** Disposition every review finding, including all owner
  comments. Nothing is silently ignored.
- **Evidence per thread.** Each fix is answered in its own thread with the
  change, the validation that exercised it, and the exact head it applies to.
  Resolve a thread only after the response is posted.
- **Deferrals are explicit.** A finding may be deferred only with owner approval
  and a linked follow-up issue describing reproduction, expected/actual
  behavior, and acceptance criteria. Security, data-loss, acceptance,
  failing-CI, and material-correctness findings are never deferrable.
- **Bounded rounds.** At most three review rounds. After the third, pause and
  ask the owner before starting a fourth; route remaining non-blocking findings
  to one linked follow-up issue rather than looping.
- **CI green is necessary, not sufficient.** Required checks must pass on the
  exact reviewed head, but a green run does not by itself authorize merge;
  unresolved threads, stale review SHAs, or unsynchronized bases still block.
- **Owner-authorized merge only.** Do not auto-merge. Merge only when the owner
  explicitly authorizes it. Merging never triggers a release; releases are a
  separate manual action.
- Do not amend or force-push a reviewed head. Add new commits so the reviewed
  range stays auditable.

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
  https://github.com/RoySalisbury/HVO.WebSite/blob/main/AGENTS.md
- HVO.WebSite `CONTRIBUTING.md` — branch naming, conventional commits, workflow:
  https://github.com/RoySalisbury/HVO.WebSite/blob/main/CONTRIBUTING.md
- HVO.WebSite `.github/copilot-instructions.md` — Blazor component triad, async
  data access, CSS/theme separation:
  https://github.com/RoySalisbury/HVO.WebSite/blob/main/.github/copilot-instructions.md
- HVO.WebSite `docs/CSS_GOVERNANCE.md` — style separation and theme tokens:
  https://github.com/RoySalisbury/HVO.WebSite/blob/main/docs/CSS_GOVERNANCE.md
- HVO.RoofController `.github/copilot-instructions.md` — thin controllers,
  DI, XML docs on public APIs:
  https://github.com/RoySalisbury/HVO.RoofController/blob/main/.github/copilot-instructions.md
- HVO.RoofController `CONTRIBUTING.md` — prerequisites, conventional commits:
  https://github.com/RoySalisbury/HVO.RoofController/blob/main/CONTRIBUTING.md
- HVO.SDK `CONTRIBUTING.md` — centralized package management, zero-warning
  build, PR checklist:
  https://github.com/RoySalisbury/HVO.SDK/blob/main/CONTRIBUTING.md
- HVO.SkyMonitor `AGENTS.md` — pinned SDK/global.json, code-behind + scoped CSS
  for Blazor, validation ladder:
  https://github.com/RoySalisbury/HVO.SkyMonitor/blob/main/AGENTS.md
- HVO.SkyMonitor `.agents/skills/pr-lifecycle/SKILL.md` — exact-SHA review,
  bounded rereviews, per-finding disposition:
  https://github.com/RoySalisbury/HVO.SkyMonitor/blob/main/.agents/skills/pr-lifecycle/SKILL.md
