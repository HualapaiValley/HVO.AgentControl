# Contributing

V2 development uses feature branches and pull requests targeting `main`. Keep
the V1 archive unchanged. Describe the change, validation and known limits
using the PR template; attach redacted screenshots for visible UI changes.

## Toolchain and setup

Pinned versions and clean-machine commands live in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). In short: .NET SDK `10.0.400`
(`global.json`), Node 22 with Playwright `1.63.0` for browser checks, Python
3.12 for CI validation, and Docker Compose v2 for containers. Package versions
are centralized in `Directory.Packages.props`.

Run the commands in [AGENTS.md](AGENTS.md) for build/test/format. CI checks
build/tests/format, container packaging, and the browser portal with the agent
runtime disabled. Live provider tests are separate, explicitly authorized
checks.

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

Use `feature/<issue#>-<short-desc>` or `fix/<issue#>-<short-desc>` from `main`.
Keep one issue per branch/PR and prefer [Conventional Commits](https://www.conventionalcommits.org/)
(`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`).

## Pull request process

Phase 1 requires the [two-model review protocol](docs/REVIEW-PROTOCOL.md): two
independent reviewers, randomly selected distinct approved models excluding the
coordinator, with recorded model provenance and no silent fallback. Both reviews
together count as one cycle. Generic agent names do not establish model identity.
Both reviewers attest the same immutable merge-base/head pair. Explicitly mark
deferred findings in their source threads with follow-up issue numbers; keep
those issues open and labeled `status:deferred` until completed, and include
their fixes and interactions in the next development/review cycle.

Review is independent of the implementing agent and bound to an exact head SHA.
PR #208 Round 1 baseline is `002e826`. The process is intentionally bounded:

- Triage every finding, including all owner comments. Nothing is silently
  ignored.
- Answer each fix in its own thread with the change, the validation that
  exercised it, and the exact head it applies to. Resolve a thread only after
  the response is posted.
- Defer a finding only with owner approval and a linked follow-up issue.
  Security, data-loss, acceptance, failing-CI and material-correctness findings
  are never deferrable.
- Allow at most three review rounds, then pause and ask the owner before a
  fourth; move remaining non-blocking findings to one linked issue.
- CI must be green on the exact reviewed head, but green CI is necessary, not
  sufficient. Unresolved threads or a stale reviewed SHA still block.
- Do not amend or force-push a reviewed head.
- Merge only when the owner explicitly authorizes it. Merging is never a
  release; releases are a separate manual action.

Squash merge is a reasonable default; merged branches are automatically
deleted.

Track work with the bug/feature issue templates. Record relevant upstream
dependencies in [docs/EXTERNAL-ISSUES.md](docs/EXTERNAL-ISSUES.md).

Do not put secrets or sensitive transcripts in issues, PRs or screenshots.
Report sensitive security concerns privately to the repository owner.
