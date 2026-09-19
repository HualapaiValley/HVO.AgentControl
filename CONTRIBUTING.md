# Contributing

V2 development uses feature branches and pull requests targeting
`development/v1`, the repository default; `main` is the stable line and moves
only by the nightly promotion PR (see [docs/development-v1.md](docs/development-v1.md)).
Keep the V1 archive unchanged. Describe the change, validation and known limits
using the PR template; attach redacted screenshots for visible UI changes.

## Toolchain and setup

Pinned versions and clean-machine commands live in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). In short: .NET SDK `10.0.401`
(`global.json`), Node 22 with Playwright `1.63.0` for browser checks, Python
3.12 for CI validation, and Docker Compose v2 for containers. Package versions
are centralized in `Directory.Packages.props`.

Run the commands in [AGENTS.md](AGENTS.md) for build/test/format. The
Development v1 gate on feature PRs checks whitespace, workflow lint, build,
format and the hermetic test selection; `main`'s pipeline additionally checks
container packaging, the isolation/contract suites and the browser portal, and
runs on every promotion PR. Live provider tests are separate, explicitly
authorized checks.

## Coding standards

Follow [.editorconfig](.editorconfig) and the general C#, Blazor, and API
standards in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). The essentials:

- Build at zero warnings and zero errors; tests must pass.
- Blazor components with logic separate markup (`.razor`), code-behind
  (`.razor.cs`), and scoped styles (`.razor.css`).
- New API endpoints use the built-in ProblemDetails pipeline and the OpenAPI
  JSON document; `/health/live` is process liveness and `/health/ready` is
  exact-session/TUI readiness.
- Do not overclaim: mark planned or unvalidated behavior as such in code, tests
  and docs.

## Branches and commits

Use `feature/<issue#>-<short-desc>` or `fix/<issue#>-<short-desc>` from
`development/v1`.
Keep one issue per branch/PR and prefer [Conventional Commits](https://www.conventionalcommits.org/)
(`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`).

Use the [execution workflow](docs/DEVELOPMENT.md#execution-workflow) for baseline
checks, acceptance-first changes, failure diagnosis and post-merge verification.
Keep design contracts focused on decisions and safety/acceptance boundaries;
implementation mechanics belong in the implementing issue unless needed to
establish feasibility or safety. Do not grow scope just to satisfy review nits.

## Pull request process

The process is the HualapaiValley development model: the rulebook is
[docs/REVIEW-PROTOCOL.md](docs/REVIEW-PROTOCOL.md) and the command-level
procedure is [docs/runbooks/pull-request-walkthrough.md](docs/runbooks/pull-request-walkthrough.md).
In short:

- Claim the issue, pick a review level (`review:mechanical|standard|deep`),
  branch from `development/v1`, open a **draft** PR using the template.
- An independent reviewer (a different session from the implementer) reviews
  the exact range; the review, each finding thread and each `VERIFIED_*`
  reply are posted as `hvo-agentcontrol[bot]` via the `AgentControl` workflow,
  bound to the head SHA; each finding is one resolvable thread with a stable
  `F<n>` ID and severity.
- The implementer answers each finding in its thread (`CORRECTED at <head8>`,
  `DEFERRED to #<issue>`, `NON_ACTIONABLE because …`, `SUPERSEDED by …`), the
  reviewer verifies (`VERIFIED_*`), the operator resolves the thread. Critical
  and High are never deferred.
- Mark ready only when the current head has an `APPROVE` verdict with every
  thread resolved; wait for the required Development v1 checks; the reviewer
  records convergence; squash merge; close the issue with the merge SHA and
  "Not promoted to main".
- Do not amend or force-push a reviewed head. Merging is never a release.

`main` moves only by the nightly promotion PR opened by the bot and merged by
the operator with a merge commit after `V2 CI` passes on it.

Track work with the bug/feature issue templates. Record relevant upstream
dependencies in [docs/EXTERNAL-ISSUES.md](docs/EXTERNAL-ISSUES.md).

Do not put secrets or sensitive transcripts in issues, PRs or screenshots.
Report sensitive security concerns privately to the repository owner.
