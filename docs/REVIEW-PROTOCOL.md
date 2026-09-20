# Development v1 Review Process

This is the review process for pull requests **to this repository**. It is
not the managed-employee review model the AgentControl product implements;
that is product design and lives under `docs/PHASE-1-CONTRACTS.md`.

This process applies to feature pull requests targeting `development/v1`. Promotion to `main` is described in [development-v1.md](development-v1.md). The command-level procedure for one pull request is [pull-request-walkthrough.md](runbooks/pull-request-walkthrough.md); bringing another repository into the model is [repository-setup.md](runbooks/repository-setup.md).

## Flow

```text
implement and validate locally
  -> commit and push
  -> open draft PR (hosted Preflight runs)
  -> post review request
  -> R0: one parent review (bot) with one child thread per finding (bot)
  -> resolve findings and commit corrections
  -> R<n>: correction review of the exact delta; VERIFIED_* replies (bot)
  -> repeat within the round limit until the verdict on the current head is APPROVE
  -> mark ready (self-hosted Build and Unit runs on that head)
  -> post review convergence once the required checks are green on that head
  -> merge (squash) when converged, green and current
```

Hosted Preflight runs on draft and ready PRs. Self-hosted Build and Unit runs
only on non-draft PRs and pushes to `development/v1`. The ordering is
deliberate: an `APPROVE` verdict on head `H` permits marking ready; the
required checks then run on `H`; **convergence** is asserted only after they
are green. A verdict is a claim about a head, the checks are its evidence, and
nothing in this process moves the head between the two (review text is never
committed to the branch).

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
| `post-review` | metadata read, pull-requests write | Posts the parent review body (a dispatch input beginning `REVIEW PR-<n>-R<k>-<head8>`) as a PR comment |
| `post-finding` | metadata read, pull-requests write | Opens one line-level review thread (`path`, `line`, body beginning `### F<n> - <Severity> - <title>`) |
| `reply-thread` | metadata read, pull-requests write | Replies in an existing finding thread with a `VERIFIED_*` disposition |

Every writing operation is bound to a full head SHA supplied at dispatch and
refuses to run if the PR head has moved, so a stale review cannot be posted
against a newer head. Review text is a dispatch input, never a file on the PR
branch: committing it would move the head past the reviewed range. Every
posted comment carries the dispatcher, the bound head and the run URL.

Resolving a finding thread is not an App operation: GitHub refuses the
`resolveReviewThread` mutation for installation tokens even on a thread the App
authored (proven on HVO.SkyMonitor PR #909). The reviewer's judgement is the `VERIFIED_*`
reply, which is posted as the bot; the operator resolves the thread once that
reply is present. Branch protection's conversation-resolution gate is satisfied
either way.

The implementer's own replies in a finding thread (`CORRECTED`, `DEFERRED`,
`NON_ACTIONABLE`, `SUPERSEDED`) are posted under the operator account: they are
the implementer speaking, and attributing them to the reviewer identity would be
wrong.

Merge authority is not delegated to the App. The workflow runs only on manual
dispatch by a collaborator with write access; it has no push or pull-request
trigger and no `contents: write` token. Every posted comment carries the run URL.

Reviews posted before this repository adopted the model (up to PR #262) used
the earlier policy-lane protocol and were attributed in-text under the shared
operator account; they remain valid records for the heads they reviewed.

## Review Parent And Child Findings

Each round posts one parent review summary. Every finding from that review is one resolvable child review thread. The parent indexes the child IDs and links; it does not duplicate all evidence.

**Keep the parent short.** It is a header plus an index: range, one line per verified claim (what was checked and how, not prose), the findings table (ID, severity, one-line summary, status) and the verdict. Finding bodies, reproductions and suggested fixes live only in the thread. A correction parent is shorter still: one status line per prior finding and the index of any new threads. A parent that restates its threads is a defect in the review, not thoroughness.

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

The implementer posts `CORRECTED`, `DEFERRED`, `NON_ACTIONABLE`, or `SUPERSEDED` in the finding thread under the operator account. Only the independent reviewer posts the verified disposition, as the bot through `reply-thread`; the operator resolves the thread after it.

## Parent Review Format

```markdown
## Review

Review ID: `PR-123-R0-abc1234`
Mode: `Initial | Correction | Base Sync | Exceptional | Convergence`
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

```markdown
## Review (correction)

Review ID: `PR-123-R1-def5678`
Mode: `Correction`
Reviewed range: `abc1234..def5678`
Reviewer: `<identity>`

| ID | Status |
| --- | --- |
| F1 | VERIFIED_CORRECTED |
| F2 | STILL_OPEN |
| F3 | new — OPEN |

Verdict: `APPROVE | CHANGES_REQUIRED | BLOCKED`
```

The `VERIFIED_*` reasoning goes in each finding's thread (via `reply-thread`), not in the parent.

## Convergence

A PR is converged only when:

- current head equals the latest reviewed head;
- every finding has a verified terminal disposition;
- every finding thread is resolved;
- deferred findings link to valid follow-up issues;
- acceptance criteria are verified;
- the latest verdict is `APPROVE`;
- the round limit is respected.

Post one convergence summary after the required checks are green on the reviewed head (the `APPROVE` verdict, not convergence, is what permits marking the PR ready). The summary is **not a review round**: it re-reads nothing, it restates the last verdict against the same head and adds the check evidence, so it does not count against the level's maximum and carries no `R<k>` number. It is posted by the reviewer as the bot through `post-review` with a header of the form `REVIEW PR-<n>-R<k>-<head8>` where `R<k>` is the **last reviewed round** (the header binds it to that round and head; it is the same claim, now evidenced), and `Mode: Convergence` in the body:

```markdown
## Review Converged

Review ID: `PR-123-R2-abc1234` (convergence of round 2; not a new round)
Mode: `Convergence`
Reviewed head: `<sha>`
Review level: `Standard`
Rounds used: `2 of 3 maximum`
Verified corrected: 2
Verified deferred: 1
Open: 0

Current PR head matches the reviewed head.
Required checks: Preflight <t>, Build and Unit <t>, both green on this head.
Next: merge.
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
