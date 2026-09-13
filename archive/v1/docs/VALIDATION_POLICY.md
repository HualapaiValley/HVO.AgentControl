# Prerelease validation

The owner selected this policy on 2026-09-09 to reduce duplicate CI while workers already build and test their changes. PRs provide independent review. This policy replaces the previous requirement to run four hosted jobs on every PR and again after every merge.

## Automatic PR check

The **CI** workflow runs one job named `build` for ready-for-review PRs targeting `main`. It restores the pinned SDK solution, builds Release with warnings as errors, verifies formatting, and runs inexpensive Python/Node regression scripts. It does not create Docker fixtures, install browsers or run the full .NET suite. Superseded runs are cancelled. Drafts skip the job; `ready_for_review` starts it. No workflow runs automatically on a push to `main`.

Keep `build` unique: manual .NET validation uses `full-tests`. A missing, pending, failed or skipped `build` is not a successful check. Mark the PR ready and verify success at its current revision before merging. There are no path filters that can silently omit this check for documentation changes.

## Worker and review evidence

Workers run the restore, Release build, full feasible tests and format commands in [AGENTS.md](../AGENTS.md) for code changes. Behavior fixes need meaningful regressions; UI changes need relevant published desktop/mobile validation. Documentation/workflow-only changes need checks appropriate to those files instead of another full test run of unchanged application code.

The PR records the exact tested commit, commands, pass/fail/skip counts, fixture flags, and any timeout or missing capability. A test-class rerun does not erase a previous unexplained full-suite failure. A draft may retain incomplete work and evidence; it is not ready to merge merely because the short check succeeds.

An independent reviewer checks the current source and validation evidence. If a changed boundary needs a fixture unavailable to the worker, the reviewer requests that manual suite. Record the selected suite and required outcome in the review/merge evidence. Do not treat an unselected suite as passed, or ignore a relevant failed manual result. Avoid repeating a completed test run unless source, tests, environment assumptions or an unresolved failure justify it.

## Manual integration and release checks

Use **Actions → Full validation → Run workflow**, choose the intended branch/tag and select one suite:

| Suite | Coverage |
| --- | --- |
| `unit-ssh` | Release build, format, full .NET unit/persistence/recovery tests, disposable SSH fixtures and GitHub CLI fixtures |
| `published-ui` | Published application with disposable data, Chromium desktop/mobile checks and UI diagnostics |
| `control-sidecar` | Private OpenCode sidecar lifecycle and web network continuity using disposable containers |
| `devcontainer` | Official pinned Dev Container CLI, independent cold/warm instances and ownership/cleanup receipts |
| `all` | All of the above |

For example:

```bash
gh workflow run full-validation.yml --repo RoySalisbury/HVO.AgentControl --ref <branch-or-tag> -f suite=published-ui
gh workflow run full-validation.yml --repo RoySalisbury/HVO.AgentControl --ref <release-branch-or-tag> -f suite=all
```

The standalone **Dev Container runner** remains manually dispatchable and is reused by `Full validation`. All existing fixture setup, flags and cleanup remain in the manual jobs. These fixtures do not claim real provider inference or every optional platform test; report remaining skips.

Before dispatch, resolve the intended commit. After dispatch, record the actual run ID, `head_sha`, selected suite and result; a moving branch name alone is insufficient evidence. Validate the release candidate's relevant affected boundaries before deployment. A broad release can run `all` once for its batch of reviewed changes instead of once per PR. Do not deploy a candidate with a relevant failed or incomplete check. Web-only deployment still preserves independent native processes and requires normal readiness/continuity observations.

## Coordinator merge policy

For this repository, the default prerelease required-check list is `["build"]`. Configure it through the owner REST `POST /api/v1/github/merge-policies`, preserving the expected revision. This changes policy, not review authority: current-head independent review, resolved findings, fresh base identity and serialized merging still apply. Existing intents retain their policy revision; reconcile them and create a fresh intent rather than altering recorded history.

The merge service evaluates the trusted policy's exact check names. It does not make every optional manual job a gate automatically. If a reviewer requires a manual check to be machine-enforced, select a policy revision that includes its actual observed check name, and create an intent under that revision. Missing/pending/failed/skipped/ambiguous required checks remain blocked. Do not infer reusable workflow check names from YAML job IDs; record the name GitHub reports.

GitHub protection/rulesets remain separate enforcement. This repository had none at the policy change; if protection is added later, require the same essential name and any deliberately selected additional checks. Do not use administrator bypass or weaken exact-head review to compensate for an obsolete check name.
