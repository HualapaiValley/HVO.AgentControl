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
controller slice: schema-v7 records, approved-host validation, fixed SSH/Docker
command construction and shared bridge protocol code. Durable hire request and
revision-bound rejection are shipped; approval and worker provisioning remain
future work. Live provisioning and task routing are implemented hermetically, as
are the viewer protocol and fixed production worker PTY backend; live remote
operation and two-host evidence remain unavailable and are not authorized.
Distinguish code capability from operationally tested behavior.

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

Phase 1 uses independent reviewers on randomly selected approved policy lanes,
excluding the coordinator model. Reviewer count follows the owner-approved
practice: one independent reviewer for a routine change, two for a high-risk
change, and one focused reviewer for a correction. Named aliases are requested
policy lanes, not serving models; random independence is requested-lane
independence only and fallback overlap must be disclosed. Verify explicit model
selection and record provenance; generic Task agent types do not prove model
identity. See [review protocol](docs/REVIEW-PROTOCOL.md). The reviewer set is one
review cycle, with a maximum of three cycles before owner direction. No silent
model fallback.
The owner-approved pool uses Fable 5.1 instead of Fable 5. Failed review/task
models may be replaced from the applicable approved list under the protocol;
record the failure/replacement and reconcile partial task effects before retry.
Record and attest the same immutable merge-base/head pair for both reviewers.
Mark owner-approved deferrals explicitly in the source thread with a follow-up
issue number, keep the issue open with `status:deferred`, and carry it into the
next development/review cycle. A resolved thread or merged PR is not a fixed
deferred item.

Keep changes focused and one issue per branch/PR. Review is independent of the
implementer and bound to an exact head SHA; PR #208 Round 1 baseline is
`002e826`. Triage every finding, including all owner comments, and answer each
fix in its own thread with the change, the validation that exercised it, and the
exact head it applies to. Defer a finding only with owner approval and a linked
follow-up issue. Before completion of an authorized fourth review cycle,
security, data-loss, acceptance, failing-CI and material-correctness findings
are not deferrable. After cycle four, the owner may grant a per-finding exception
that converts a critical or material finding which does not block build, required
CI, migration safety or repository integrity from a merge blocker into a release
blocker. Record the exception in the original thread, link a dedicated open
issue carrying `status:deferred` and `status:release-blocker`, list it in the PR
and release checklist, and resolve the thread only as **Deferred, not fixed**.
No tag, registry publication, release, deployment, live migration or live
enrollment is authorized while that release blocker remains open and
independently unverified. The exception is never blanket authorization for other
findings or PRs. Allow at most three review rounds, then pause and ask the owner
before a fourth; every later cycle also requires explicit owner authorization.
CI must be green on the exact reviewed head, but green CI is necessary, not
sufficient. Do not amend or force-push a reviewed head. Merge only when the
owner explicitly authorizes it; never auto-merge, and never treat a merge as a
release.

## Secrets and data

Never commit credentials, owner password files, runtime databases, provider
transcripts, local configuration, or generated badge/token URLs. Use disposable
resources for integration tests. Commits, pushes, publishing and production
operations require explicit owner authorization.
