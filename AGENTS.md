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

## Secrets and data

Never commit credentials, owner password files, runtime databases, provider
transcripts, local configuration, or generated badge/token URLs. Use disposable
resources for integration tests. Commits, pushes, publishing and production
operations require explicit owner authorization.
