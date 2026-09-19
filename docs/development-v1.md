# Development v1

[![Development v1 CI](https://github.com/HualapaiValley/HVO.AgentControl/actions/workflows/development-v1.yml/badge.svg?branch=development%2Fv1)](https://github.com/HualapaiValley/HVO.AgentControl/actions/workflows/development-v1.yml?query=branch%3Adevelopment%2Fv1)

`development/v1` is the daily integration branch and the repository default.
`main` is the stable line; it moves only by gated promotion from
`development/v1`, and deployments to `home-docker` are taken from `main`.

This page is about how changes reach **this repository**. The review model
the AgentControl product implements for managed employees is product design
(`PHASE-1-CONTRACTS.md`), not this process.

## Daily Flow

```text
development/v1
  -> feature/<issue>-<short-name>
  -> draft pull request targeting development/v1
  -> independent review posted as hvo-agentcontrol[bot], one thread per finding
  -> ready pull request
  -> required Development v1 CI
  -> squash merge into development/v1
  -> nightly promotion PR into main (merge commit, operator merges)
```

Create work from the current `development/v1` head. Ordinary issue branches and
pull requests target `development/v1`, not `main`.

## Issue And Pull Request Forms

- [Development v1 issue form](../.github/ISSUE_TEMPLATE/development-v1.yml)
- [Development v1 pull request template](../.github/PULL_REQUEST_TEMPLATE/development-v1.md)
  (also the repository default template)
- [Review process](REVIEW-PROTOCOL.md): the rulebook
- [Pull request walkthrough](runbooks/pull-request-walkthrough.md): the
  step-by-step procedure with commands
- [Repository setup](runbooks/repository-setup.md): the model, how this
  repository was brought into it, and how to bring another one in

Issues merged into `development/v1` are closed explicitly with the pull request
and merge SHA. Completion comments state that the work is not promoted to `main`.

## Required CI

`development/v1` requires:

- **Development v1 / Preflight** on a GitHub-hosted runner: exact-range
  whitespace validation, pinned workflow linting (`actionlint`, which also
  enforces the organization's full-SHA action pins), and a check that no staged
  review body is still on the branch.
- **Development v1 / Build and Unit** on a self-hosted `hvo-linux-x64` runner
  (`hvo-agentcontrol` label): pinned SDK setup, restore, warning-clean Release
  build, `dotnet format --verify-no-changes`, and the hermetic test selection
  (everything except the Docker isolation, worker image contract, real-tmux and
  OpenCode wire suites) under a 540 s workload budget, with stage timing,
  largest-process RSS and TRX evidence uploaded per run.

Draft pull requests run hosted Preflight only. Marking a reviewed pull request
ready starts the self-hosted Build and Unit check. Pushes to `development/v1`
run both.

`main` keeps the full qualification pipeline (`V2 CI` in `build.yml`): build and
test including the real-OpenCode wire test, Docker config and image with the
alternate-UID isolation and worker image contract suites, and both hermetic
browser suites. It runs on every promotion pull request and every push to
`main`.

## Review

Mechanical, Standard and Deep review levels use one parent review with one
resolvable child thread per finding. The parent is committed under
`.agentcontrol/reviews/` on the PR branch and posted by the `AgentControl`
workflow as `hvo-agentcontrol[bot]`; author resolutions and the reviewer's
`VERIFIED_*` verification stay in the finding thread, and the operator resolves
it. A pull request is marked ready only after the current head is reviewed,
every finding has a verified terminal disposition, every thread is resolved,
and the review file is off the branch.

## Promotion To main

`main` moves only by promotion from `development/v1`. Every night at 02:00
America/Phoenix, `.github/workflows/promote-main.yml` compares the two branches
and, when `development/v1` is ahead, opens or refreshes one promotion pull
request as `hvo-agentcontrol[bot]`. It refuses to promote a head whose own
Development v1 push run is not green, and it halts if `main` has commits that
`development/v1` lacks, because that means the branch model was bypassed.

`V2 CI` on `main` is the qualification gate for the promotion pull request.
Merging is the operator's decision, taken with a merge commit so every
`development/v1` commit keeps its SHA on `main`. The workflow never pushes to
`main` and never merges; its token is pull-requests write only. It can be run
on demand with `gh workflow run promote-main.yml`.
