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

`tmux` and OpenCode are runtime/container dependencies. They are not required
on the host for the standard .NET and browser workflow.

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

CI runs only the hermetic smoke suite. It spawns the locally built app on a
free loopback port with `Control__Enabled=false` and no owner password, so it
needs no Docker, credentials, or model provider.

```bash
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install --with-deps chromium
npm run ci --prefix tests/Browser
```

Test results and screenshots are written to `artifacts/browser-ci/`. See
[`tests/Browser/README.md`](../tests/Browser/README.md) for the suite list.

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
credentials. Container-level OPENAI_API_KEY or similar environment variables
are intentionally not a provider provisioning interface. Default Big Pickle
requires no key; other providers need explicitly authorized OpenCode login in
the private runtime HOME/XDG directories. Managed provider provisioning is
future work.

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

V2 currently has no database; readiness must not require one. This differs from
archived V1, whose `/health/ready` checked control-plane database connectivity.
Do not reintroduce a database probe into the V2 readiness signal.

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

The new endpoints and ProblemDetails behavior have local regression coverage;
publication remains subject to CI and PR review. `/health/ready` is a
fail-closed readiness observation: unknown native status yields 503 during
startup or transient probe failures. Do not use readiness failures as an
automatic restart signal; `/health/live` is the process liveness probe.
No existing runtime data schema (for example `/data/runtime.json`) changes as
part of the API framework work.

## Pull request process guardrails

These bounds are agreed with the repository owner. They keep review and delivery
predictable while the project is small.

- **Phase 1 reviewer selection.** Follow the [two-model protocol](REVIEW-PROTOCOL.md).
  Two distinct randomly selected approved models, excluding the coordinator,
  review independently; record verifiable selection and routing limits. The
  pair counts as one cycle. Unavailable models require owner direction, not a
  hidden substitution.
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
