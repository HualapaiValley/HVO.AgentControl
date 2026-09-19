# Development v1 Review Process

This is the review process for pull requests **to this repository**. It is
not the managed-employee review model the AgentControl product implements;
that is product design and lives under `docs/PHASE-1-CONTRACTS.md`.

This process applies to feature pull requests targeting `development/v1`. Promotion to `main` is described in [development-v1.md](development-v1.md). The command-level procedure for one pull request is [pull-request-walkthrough.md](runbooks/pull-request-walkthrough.md); bringing another repository into the model is [repository-setup.md](runbooks/repository-setup.md).

## Flow

```text
implement and validate locally
  -> commit and push
  -> open draft PR
  -> post review request
  -> post one parent review with one child thread per finding
  -> resolve findings and commit corrections
  -> correction review of the exact delta
  -> repeat within the round limit
  -> post review convergence
  -> mark ready
  -> Development v1 CI
  -> merge when green and current
```

Hosted Preflight runs on draft and ready PRs. Self-hosted Build and Unit runs only on non-draft PRs and pushes to `development/v1`.

## Review Levels And Limits

| Level | Initial reviews | Correction reviews | Ordinary maximum |
| --- | ---: | ---: | ---: |
| Mechanical | 0 or 1 | 0 | 1 |
| Standard | 1 | 2 | 3 |
| Deep | 1 | 3 | 4 |

One independent reviewer is sufficient for Standard work. Deep review uses the strongest available independent reviewer and adds a specialist only when the issue requires it.

Medium findings block by default. The issue owner may explicitly approve deferral only when the behavior is outside current acceptance and a follow-up issue records the source evidence and residual risk. Critical and High findings are never deferred.

An exceptional focused review is allowed for a CI-discovered code defect, a late security/data-loss defect, or a material base-sync interaction. The reason must be recorded.

## Reviewer Identity

The independent reviewer posts as `hvo-agentcontrol[bot]`, the HualapaiValley-owned
GitHub App, so that review records are attributable to an identity distinct from
the implementer even when both are driven from the same operator account. A local
agent cannot act as the App directly; it dispatches the `AgentControl` workflow
(`.github/workflows/agentcontrol.yml`), which holds the App's private key as an
organization secret and performs three operations, each with a token narrowed to
the permission that operation needs:

| Operation | Token permissions | What it does |
| --- | --- | --- |
| `verify-identity` | metadata, contents read | Proves the key mints an installation token that sees exactly this repository |
| `post-review` | + pull-requests write | Posts a review body committed under `.agentcontrol/reviews/` on the PR head as the bot, appending who dispatched it and the reviewed head |

Resolving a finding thread is not an App operation: GitHub refuses the
`resolveReviewThread` mutation for installation tokens even on a thread the App
authored (proven on HVO.SkyMonitor PR #909). The reviewer's judgement is the `VERIFIED_*`
comment, which is posted as the bot; the operator resolves the thread once that
comment is present. Branch protection's conversation-resolution gate is satisfied
either way.

Merge authority is not delegated to the App. The workflow runs only on manual
dispatch by a collaborator with write access; it has no push or pull-request
trigger and no `contents: write` token. Every posted comment carries the run URL.

Reviews posted before this repository adopted the model (up to PR #262) used
the earlier policy-lane protocol and were attributed in-text under the shared
operator account; they remain valid records for the heads they reviewed.

## Review Parent And Child Findings

Each round posts one parent review summary. Every finding from that review is one resolvable child review thread. The parent indexes the child IDs and links; it does not duplicate all evidence.

Finding IDs are stable across the PR: `F1`, `F2`, and so on. New correction-round findings receive the next unused ID. A finding thread contains:

- finding ID and severity;
- short description and changed-line location;
- source evidence or reproducer;
- observable impact;
- required resolution.

Line-specific GitHub review threads are preferred. Attach cross-cutting findings to the closest changed line and cite all interactions. Repository issues are created only for work deferred beyond the PR.

## Finding Lifecycle

```text
OPEN -> CORRECTED -> VERIFIED_CORRECTED -> resolved thread
OPEN -> DEFERRED -> VERIFIED_DEFERRED -> resolved thread + open follow-up issue
OPEN -> NON_ACTIONABLE -> VERIFIED_NON_ACTIONABLE -> resolved thread
OPEN -> SUPERSEDED -> VERIFIED_SUPERSEDED -> resolved thread
```

The implementer posts `CORRECTED`, `DEFERRED`, `NON_ACTIONABLE`, or `SUPERSEDED` in the finding thread. Only the independent reviewer posts the verified disposition; the operator resolves the thread after it.

## Parent Review Format

```markdown
## Review

Review ID: `PR-123-R0-abc1234`
Mode: `Initial | Correction | Base Sync | Exceptional`
Level: `Mechanical | Standard | Deep`
Reviewer: `<identity>`
Provider/model/effort: `<actual values or N/A>`

Reviewed base: `<sha>`
Reviewed head: `<sha>`
Exact range: `<left>..<right>`

Verdict: `APPROVE | CHANGES_REQUIRED | BLOCKED`

| ID | Severity | Summary | Thread | Status |
| --- | --- | --- | --- | --- |
| F1 | High | Short behavior description | link | OPEN |

Acceptance:
- criterion: verified or blocked by finding

Open blockers: `F1` or `none`
```

## Finding Thread Format

```markdown
### F1 - High - Short title

Parent review: `PR-123-R0-abc1234`
Location: `path/file.cs:120`

Description:
Short observable problem.

Source evidence:
Code path, reproducer, test output, trace, or mutation result.

Required resolution:
Observable behavior needed for closure.
```

## Author Resolution Format

```markdown
### F1 - CORRECTED

Correction commit: `<sha>`
Correction range: `<previous-reviewed-head>..<current-head>`

Resolution:
Behavioral correction.

Evidence:
- `focused command`: passed
```

## Reviewer Verification Format

```markdown
### F1 - VERIFIED_CORRECTED

Reviewed range: `<left>..<right>`
Verification evidence and disposition.

Disposition: resolved.
```

## Correction Review

Correction review covers the previous reviewed head to the current head, the interactions introduced by that delta, and every unresolved finding. It must disposition every prior finding. Omitted findings remain open.

The parent correction review indexes existing finding threads instead of recreating them. New findings get new child threads.

## Convergence

A PR is converged only when:

- current head equals the latest reviewed head;
- every finding has a verified terminal disposition;
- every finding thread is resolved;
- deferred findings link to valid follow-up issues;
- acceptance criteria are verified;
- the latest verdict is `APPROVE`;
- the round limit is respected.

Post one convergence summary before marking the PR ready:

```markdown
## Review Converged

Reviewed head: `<sha>`
Review level: `Standard`
Rounds: `2 of 3 maximum`
Verified corrected: 2
Verified deferred: 1
Open: 0

Current PR head matches the reviewed head.
Next: mark ready and run Development v1 CI.
```

## CI And Merge

If Development v1 CI finds a real code defect, return the PR to draft, add a finding ID, correct it, and obtain a focused exceptional review. An infrastructure failure does not create a code finding and may receive one diagnosed rerun.

Merge when review is converged, all conversations are resolved, the current head is reviewed and up to date, and required Development v1 checks pass. No repository-wide finalization lock is used for ordinary v1 PRs.

After merge, explicitly close the issue and comment:

```text
Completed on development/v1 via PR #123.
Merge SHA: <sha>.
Not promoted to main.
```
