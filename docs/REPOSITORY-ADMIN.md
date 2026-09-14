# Repository Administration

## Current policy

- Private repository, `main` is the default branch.
- Pull requests required on `main`, including for admins; no force pushes or
  branch deletion. Review conversations must be resolved.
- Required CI checks: `Build and test`, `Docker config and image`, and
  `Browser smoke (disabled runtime)`. Branch must be up to date before merging.
  These strings are branch-protection contexts, so a workflow job name must not
  be renamed without updating the protection rule first: a renamed job silently
  stops satisfying its required check and blocks the merge. The
  `Browser smoke (disabled runtime)` job intentionally runs both hermetic
  browser suites (the disabled-runtime smoke and the enabled-runtime
  organization check), so the required gate covers both.
- Zero required approvals while the owner is the only collaborator: an author
  cannot approve their own PR. Independent review remains the workflow; add a
  required approval when a second reviewer is enrolled.
- Automatically delete merged feature branches. No automatic merging enabled.
- Actions defaults remain read-only and cannot approve PRs. Only the manual
  release publishing job requests `contents: write` and `packages: write`.
- Dependency vulnerability alerts enabled. Weekly grouped Dependabot updates
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

- Verify the existing AgentControl GitHub App installation and repository grants
  in GitHub Settings. The current CLI token cannot list App installations; no
  credentials, permissions, installations or collaborators were altered.
- Review secret scanning/push protection availability for this private repo's
  plan. Git ignores and manual scans are not equivalent to GitHub secret scanning.
- Select an independent reviewer for the first V2 PR, then revisit required
  approval count if they become a collaborator.

These are recorded settings, not portable configuration enforced by a file.
Recheck branch protection/check names whenever CI jobs are renamed.
