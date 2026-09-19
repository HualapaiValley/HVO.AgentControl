# Repository Administration

## Current policy

- Repository `HualapaiValley/HVO.AgentControl` (transferred from
  `RoySalisbury/HVO.AgentControl` on 2026-09-18; GitHub redirects the old name).
  Public repository. `development/v1` is the default (daily integration)
  branch; `main` is the stable line, moved only by the nightly promotion PR
  (see [development-v1.md](development-v1.md) and
  [runbooks/repository-setup.md](runbooks/repository-setup.md)).
- Pull requests required on both branches, including for admins; no force
  pushes or branch deletion; review conversations must be resolved; branch must
  be up to date before merging; no linear-history requirement on `main` so
  promotion can use a merge commit.
- Required checks on `development/v1`: `Development v1 / Preflight` and
  `Development v1 / Build and Unit`. Required checks on `main`: `Build and
  test`, `Docker config and image`, and `Browser smoke (disabled runtime)`.
  These strings are branch-protection contexts, so a workflow job name must not
  be renamed without updating the protection rule first: a renamed job silently
  stops satisfying its required check and blocks the merge. The
  `Browser smoke (disabled runtime)` job intentionally runs both hermetic
  browser suites (the disabled-runtime smoke and the enabled-runtime
  organization check), so the required gate covers both.
- Self-hosted CI: the repository is in the organization runner group
  `hvo-linux-x64`; the three runners there carry the `hvo-agentcontrol` label.
- Review identity: the organization-owned `HVO-AgentControl` GitHub App
  (installed on all organization repositories). This repository is granted the
  org variables `HVO_AGENTCONTROL_APP_ID` / `HVO_AGENTCONTROL_INSTALLATION_ID`
  and the org secret `HVO_AGENTCONTROL_PRIVATE_KEY`, used only by the
  manually-dispatched `agentcontrol.yml` and the scheduled `promote-main.yml`.
- Labels used by the process: `review:{mechanical,standard,deep,requested,changes-required,converged}`,
  `risk:{security,durability,concurrency,ci-control,deployment}`,
  `workflow:{in-progress,blocked}`. Earlier `status:*` labels remain on
  historical issues.
- Zero required approvals on purpose: GitHub refuses self-approval when the
  reviewer and implementer share an account, and the review record lives in the
  bot-posted review and the resolved finding threads, which the
  conversation-resolution rule enforces.
- Automatically delete merged feature branches. No automatic merging enabled.
- Actions defaults remain read-only and cannot approve PRs. Only the manual
  release publishing job requests `contents: write` and `packages: write`; the
  App tokens minted by `agentcontrol.yml`/`promote-main.yml` are narrowed to
  pull-requests write and never carry contents write.
- The HualapaiValley organization requires every `uses:` action to be pinned
  to a full-length commit SHA (a tag such as `@v7` is rejected at job setup).
  Workflows pin `<owner>/<action>@<sha> # <version>`; Dependabot's
  `github-actions` group updates the SHA and the version comment together.
- Secret scanning, push protection, dependency vulnerability alerts and
  Dependabot security updates are enabled (re-applied after the transfer, when
  the public visibility made them available). Weekly grouped Dependabot updates
  cover active NuGet, browser npm, Dockerfile and GitHub Actions dependencies.
  Archived projects are excluded from NuGet discovery and are not update targets.

## Release settings

The `release` environment allows protected branches only. Required environment
reviewers could not be enabled: GitHub rejected that protection rule because
the repository's current billing plan does not support it. Manual workflow
dispatch and the main-ref check are the present gates, **not a second-person
release approval**. Revisit the plan/settings before promising that guarantee.

GHCR publication remains undispatched. Verify package visibility/access on the
first publish; do not make the package public implicitly. See [RELEASING.md](RELEASING.md).

## Remaining owner checks

- The `hvo-agentcontrol` GitHub App is installed on the HualapaiValley
  organization with access to all repositories; verify its permission grants in
  the organization settings. No credentials were altered by the transfer.
- Branch protection was re-applied after the transfer (the private-repo plan had
  dropped it) and extended to `development/v1` on adoption of the development
  model; recheck it whenever the repository's plan or visibility changes.
- The `batismal-web` outside collaborator (read) was not carried across the
  transfer; re-add only if still needed.
- Select an independent reviewer for the first V2 PR, then revisit required
  approval count if they become a collaborator.

These are recorded settings, not portable configuration enforced by a file.
Recheck branch protection/check names whenever CI jobs are renamed.
