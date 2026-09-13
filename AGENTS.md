# AgentControl V2 Development

The repository root is V2. `archive/v1/` is historical reference only; its
instructions do not govern V2. **The archive is read-only:** do not modify
`archive/v1/` or `archive/README.md`, reactivate V1 workflows, or run old
deployment scripts without explicit owner direction.

## Scope

Build a Docker-native controller for self-contained OpenCode workers using ACP.
No Fleet dependency, Claude-specific adapter, shared worker checkout, or
implicit reuse of existing infrastructure. The active baseline implements a
single control-host portal only; developer provisioning, task routing and the
worker bridge/reconnect path are not implemented. Distinguish planned from
tested behavior in code, tests and docs.

## Repository layout

- Active code belongs in `src/`; tests in `tests/`; design notes in `docs/`.
- Architecture generation (`generation = 2`) is distinct from the semantic
  release version.

## Development toolchain and standards

Pinned versions and clean-machine commands are in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). Do not substitute a machine-wide
latest for a pin: .NET SDK `10.0.400` (`global.json`), target `net10.0`,
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
validation, an image build and hermetic browser smoke. The browser CI suite
starts its own disabled runtime and uses no provider credentials or inference.
Run it locally for UI changes with Node 22+ and Chromium:

```bash
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install chromium
npm run ci --prefix tests/Browser
```

Live browser suites are separate, explicitly requested checks against a running
portal; they can submit prompts and change session state. See `tests/Browser/README.md`.

Test outcomes and failure/recovery boundaries. Receipt is not execution; turn
completion is not verified task success; cancellation is not rollback. Do not
retry uncertain writes without reconciling effects.

## Pull request process

Phase 1 uses two independent reviewers on randomly selected, distinct approved
models, excluding the coordinator model. Verify explicit model selection and
record provenance; generic Task agent types do not prove model identity. See
[review protocol](docs/REVIEW-PROTOCOL.md). The pair is one review cycle, with a
maximum of three cycles before owner direction. No silent model fallback.

Keep changes focused and one issue per branch/PR. Review is independent of the
implementer and bound to an exact head SHA; PR #208 Round 1 baseline is
`002e826`. Triage every finding, including all owner comments, and answer each
fix in its own thread with the change, the validation that exercised it, and the
exact head it applies to. Defer a finding only with owner approval and a linked
follow-up issue; security, data-loss, acceptance, failing-CI and
material-correctness findings are never deferrable. Allow at most three review
rounds, then pause and ask the owner before a fourth; route remaining
non-blocking findings to one linked issue. CI must be green on the exact
reviewed head, but green CI is necessary, not sufficient. Do not amend or
force-push a reviewed head. Merge only when the owner explicitly authorizes it;
never auto-merge, and never treat a merge as a release.

## Secrets and data

Never commit credentials, owner password files, runtime databases, provider
transcripts, local configuration, or generated badge/token URLs. Use disposable
resources for integration tests. Commits, pushes, publishing and production
operations require explicit owner authorization.
