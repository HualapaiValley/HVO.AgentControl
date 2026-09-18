# Releasing HVO.AgentControl V2

This document describes the automated CI and the manual, opt-in release
process. The release workflow is **prepared but not executed**: no tag,
GitHub release, or container image has been published by this change.

## Continuous integration

`.github/workflows/build.yml` runs on pull requests to `main`, pushes to
`main`, and manual dispatch. It receives no secrets and never publishes
anything. Three jobs run:

| Job | What it validates |
| --- | --- |
| `build-test` | `dotnet restore`, Release build with `--warnaserror`, tests (TRX uploaded as the `test-results` artifact), and `dotnet format --verify-no-changes`. Python 3.12 is installed so the checked-in PTY bridge contract test executes rather than skips. |
| `docker` | `docker compose config --quiet` and a `docker build` without pushing. |
| `browser-smoke` | Builds the app, installs `tests/Browser` dependencies with `npm ci --prefix tests/Browser` and Chromium with `npx --prefix tests/Browser playwright install --with-deps chromium`, then runs the hermetic `tests/Browser/ci-smoke.mjs` via `npm run ci --prefix tests/Browser`. `ci-smoke.mjs` spawns the locally built app on a free loopback port with `Control__Enabled=false` and no owner password, drives the real Blazor portal, and writes results and screenshots to `artifacts/browser-ci/` (uploaded). Normal CI uses no Docker, credentials, or provider/free-model calls. |

Action versions use mutable major tags: `actions/checkout@v7`,
`actions/setup-dotnet@v6`, `actions/setup-python@v7`, `actions/setup-node@v7`,
`actions/upload-artifact@v7`, `docker/setup-buildx-action@v4`,
`docker/login-action@v4`, and `docker/build-push-action@v7` (verified with
`gh api repos/<action>/releases/latest`).

## Version source of truth

The release version is `<Version>` in `Directory.Build.props`. The workflow
dispatch input must match it exactly; the validator refuses anything else.

The initial version is `0.1.0`; V2 is the architecture generation, not a
semantic major version. `/api/version`, the portal footer, and the ACP client
handshake derive their version from assembly metadata generated from that
property. `CHANGELOG.md` is the version history. The validator accepts a semver
prerelease suffix but rejects build metadata (`+...`) in container tags.

`scripts/check-release.py` is the only component that interprets the
operator-supplied version. It receives the value through the environment,
never through shell interpolation, validates it against a strict semver
pattern, requires an exact match with `Directory.Build.props`, and fails if
the git tag `v<version>` already exists.

## Release workflow

`.github/workflows/release.yml` is `workflow_dispatch`-only.

### Inputs

| Input | Required | Default | Meaning |
| --- | --- | --- | --- |
| `version` | yes | – | Exact version, for example `0.1.0`. Must equal `<Version>` in `Directory.Build.props`. |
| `prerelease` | no | `true` | Marks the GitHub release as a prerelease. `0.1.0` is early development, so leaving this enabled is recommended. |

### Jobs

1. **`validate`** – refuses to run unless the ref is `refs/heads/main`, then
   runs `scripts/check-release.py`. It fails on a malformed version, a
   mismatch with `Directory.Build.props`, or an existing `v<version>` tag.
2. **`verify`** – the release gate: restore, Release build with
   `--warnaserror`, tests, format verification, hermetic browser smoke, and
   Compose validation. Test results are uploaded.
3. **`publish`** – runs in the optional `release` environment, with
   `contents: write` and `packages: write` granted **only to this job**:
   - re-checks the git tag and (after GHCR login) the image tag, failing
     rather than overwriting either;
   - builds and pushes a `linux/amd64` image to
     `ghcr.io/hualapaivalley/hvo.agentcontrol` tagged with both
     `<version>` and `sha-<short-sha>`;
   - applies OCI labels `org.opencontainers.image.version`, `...revision`
     (the tested commit SHA), and `...source`;
   - writes `image-digest.txt` (image, version, revision, SHA tag, platform,
     digest, timestamp) and uploads it as a downloadable artifact;
   - creates the GitHub release and the `v<version>` tag at the exact tested
      `GITHUB_SHA` with `gh release create --target`. Every checkout is pinned
      to that SHA. A nonempty `CHANGELOG.md` section for the exact version is
      required before publication; generated notes do not replace it.

No `latest` image tag is published, and the GitHub release is not marked latest.
A version tag and a tested-SHA image tag are the only image references created.

### Triggering

```bash
gh workflow run release.yml --ref main -f version=0.1.0 -f prerelease=true
```

Or use **Actions → V2 Release (manual) → Run workflow** on `main`.

## Required repository setup

See [REPOSITORY-ADMIN.md](REPOSITORY-ADMIN.md) for verified settings and limits.
The `release` environment is restricted to protected branches. Required
environment reviewers were rejected by GitHub for the current private-repo
billing plan, so no approval gate beyond manual dispatch is claimed.

- **Actions token permissions.** Settings → Actions → General → Workflow
  permissions must allow the token to request `contents: write` and
  `packages: write`; the job only requests these for `publish`. No personal
  access token is stored; the ephemeral `GITHUB_TOKEN` is used.
- **`release` environment (optional but recommended).** The `publish` job
  references `environment: release`. Add required reviewers and/or a wait
  timer there for approval. If the environment is not configured, the job
  runs without protection, so configure it before relying on it.
- **GHCR package visibility.** A first publish creates the package under the
  `HualapaiValley` organization with its default visibility; package visibility
  and access must be **manually confirmed** in the organization package
  settings after the first publish. Do not claim the image is public unless
  that is verified.

## Repeat releases

The workflow is idempotency-guarded, not idempotent:

- `validate` fails if `v<version>` already exists.
- `publish` re-checks the git tag and, after login, the GHCR image tag
  (`docker buildx imagetools inspect`) and fails before building/pushing.
- `publish` fails if a GitHub release for the tag already exists.

To release an existing version again, choose a new version; do not delete and
reuse tags or image tags.

Publication across GHCR and GitHub Releases is not atomic. If an image push
succeeds but release creation fails, inspect the saved image digest and tested
SHA before completing the release manually; do not blindly rerun the workflow.
Registry permission/network errors fail closed rather than count as absence.

## Post-release verification (manual)

1. Download the `release-image-digest-<version>` artifact from the run and
   record the digest.
2. Confirm the GitHub release points at the expected commit and is marked
   prerelease as intended.
3. Confirm package visibility in the GHCR package settings.
4. Pull and inspect the image if needed:

   ```bash
   docker pull ghcr.io/hualapaivalley/hvo.agentcontrol:0.1.0
   docker image inspect ghcr.io/hualapaivalley/hvo.agentcontrol:0.1.0 \
     --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}'
   ```

Container publication can be postponed entirely; the CI workflow remains the
required baseline and the release workflow simply stays undispatched.
