# Pull Request Walkthrough

The exact sequence for taking an issue from claim to merge on `development/v1`
in this repository, with the commands. [REVIEW-PROTOCOL.md](../REVIEW-PROTOCOL.md)
is the rulebook; this is the procedure. The model itself, and how this repository
was brought into it, is [repository-setup.md](repository-setup.md). The worked
example at the end is HVO.SkyMonitor PR #909, the first PR to run the full
sequence; it is replaced by this repository's own first PR once one has run.

This is the process for pull requests **to this repository**. It is not the
managed-employee review the AgentControl product implements.

The shape:

```text
claim -> branch -> implement -> local gates -> draft PR (Preflight)
   -> R0 posted by the bot -> findings as bot threads
   -> corrections -> VERIFIED_* (bot) -> operator resolves -> APPROVE on head
   -> ready (Build and Unit) -> converged -> merge -> close issue -> (nightly) promote
```

Two roles run this, and on a single-operator repository they are two sessions
of the same person or agent: the **implementer** and the **independent
reviewer**. The reviewer never edits the branch; the implementer never posts
`VERIFIED_*`.

## 1. Claim

Before editing anything, look for an existing claim: assignee,
`workflow:in-progress`, a claim comment, a linked open PR. An existing claim
means take a different issue.

```bash
gh issue edit <n> --add-assignee @me --add-label workflow:in-progress --add-label review:standard
gh issue comment <n> --body 'CLAIM
Owner: <harness>:<provider>:<host>:<session>
Target: development/v1
Branch: feature/<n>-<short-name>
Scope: <one line>
Next checkpoint: <what will be true at the next report>'
```

Pick the review level now and label it: `review:mechanical` (docs, links,
renames), `review:standard` (ordinary code), `review:deep` (CI control,
security, durability, concurrency, deployment, or anything cross-boundary). Add
`risk:*` labels for the deep triggers. Re-read the issue after claiming.

## 2. Branch and implement

```bash
git switch development/v1 && git pull --ff-only
git switch -c feature/<n>-<short-name>
```

One issue per branch. Commit messages are sentences describing the change, not
the issue number. Never commit credentials, owner-password files, runtime
databases, provider transcripts or anything under a runtime data directory;
`archive/v1/` is read-only.

## 3. Local gates before the draft

The tier decides how much. Every PR runs at least:

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore -c Release --warnaserror
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
git diff --check
dotnet test HVO.AgentControl.slnx --no-build -c Release
```

Plus the focused tests for what changed. The Development v1 gate runs the
hermetic selection only (everything except the Docker isolation, worker image
contract, real-tmux and OpenCode wire suites); a PR that touches the
`Dockerfile`, `compose.yaml`, the worker supervisor, the terminal bridge or the
portal UI additionally runs locally what `main`'s pipeline will run on the
promotion PR: `docker build` for both targets with the isolation and contract
suites under `AGENTCONTROL_DOCKER_REQUIRED=1`, and the hermetic browser suites
(`npm run ci-all --prefix tests/Browser`). Record what you ran and the numbers
in the PR body; "tests pass" without counts is not evidence.

## 4. Open the draft

```bash
git push -u origin feature/<n>-<short-name>
gh pr create --draft --base development/v1 --title '<sentence>' --body '<see template>'
```

The body follows `.github/PULL_REQUEST_TEMPLATE/development-v1.md`: the issue,
what changed and why, exclusions, local validation with numbers, the review
level and the lens the reviewer should apply. Drafts run hosted Preflight only;
the self-hosted Build and Unit job waits until the PR is ready, so review
happens before the expensive check, not after.

## 5. Review

The reviewer works from the exact range, verifies claims against the
repository and external sources rather than the PR text, and writes the review
body **outside the repository**. It is never committed to the PR branch:
committing it would move the head past the reviewed range. The bot posts it:

```bash
head=$(gh pr view <n> --json headRefOid --jq .headRefOid)
cat > /tmp/PR-<n>-R0.md <<EOF
REVIEW PR-<n>-R0-${head:0:8}
Level: Standard
Mode: Initial
Range: <merge-base>..$head (complete PR diff)
Reviewer: <identity>, posted as hvo-agentcontrol[bot]
Provider/model/effort: <actual values>

Verified against the repository and external sources, not the PR text:
- <one line per verified claim, stating how it was verified>

Findings: <count, or none>
Verdict: APPROVE | CHANGES_REQUIRED | BLOCKED
EOF
gh workflow run agentcontrol.yml --ref development/v1 \
  -f operation=post-review -f pr=<n> -f head="$head" -f body="$(cat /tmp/PR-<n>-R0.md)"
```

The comment appears authored by `hvo-agentcontrol[bot]` with a trailer naming
who dispatched it, the bound head, and the run. The workflow refuses the
dispatch if the first line is not exactly `REVIEW PR-<n>-R<k>-<head8>` for that
PR and head, or if the PR has moved off `head` since. `R0` is the initial
review; `R1`, `R2`, `R3` are correction reviews and the level caps them
(Mechanical 1, Standard 2, Deep 3).

## 6. Findings

Each finding is one resolvable review thread on a line of the diff, with a
stable ID and a severity, opened by the bot:

```bash
gh workflow run agentcontrol.yml --ref development/v1 \
  -f operation=post-finding -f pr=<n> -f head="$head" -f path=<file> -f line=<line> \
  -f body="$(cat /tmp/PR-<n>-F1.md)"
```

where the file begins `### F1 - Low - <short title>` and carries the parent
review ID, the location, the description with how it was observed and why it
matters, and the required resolution (formats in the rulebook).

Severity sets what happens: Critical and High always block and are never
deferred; Medium blocks unless the issue owner records a deferral to a linked
follow-up issue; Low and nits are at the reviewer's discretion but still get a
thread so the disposition is recorded.

The implementer replies in the thread, under the operator account, with
exactly one of `CORRECTED at <head8>`, `DEFERRED to #<issue>`,
`NON_ACTIONABLE because <reason>`, or `SUPERSEDED by <what>`, and pushes the
correction. The reviewer re-checks the delta only (not the whole PR); the bot
posts the verification into the thread:

```bash
# the numeric id of the comment that opened the F1 thread:
cid=$(gh api repos/HualapaiValley/HVO.AgentControl/pulls/<n>/comments --jq '.[]|select(.body|startswith("### F1 "))|.id')
newhead=$(gh pr view <n> --json headRefOid --jq .headRefOid)
gh workflow run agentcontrol.yml --ref development/v1 \
  -f operation=reply-thread -f pr=<n> -f head="$newhead" -f comment_id="$cid" \
  -f body="$(cat /tmp/PR-<n>-F1-verified.md)"
```

where that file begins `VERIFIED_CORRECTED at <newhead8>` (or
`VERIFIED_DEFERRED`, `VERIFIED_NON_ACTIONABLE`, `VERIFIED_SUPERSEDED`). A
correction review body (`R1`) is posted via `post-review` the same way as
`R0`, covering the delta and stating each finding's disposition. Then the
**operator** resolves the thread. This is deliberately not a bot operation:
GitHub refuses `resolveReviewThread` to App installation tokens.

```bash
tid=$(gh api graphql -f query='query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){pullRequest(number:$n){reviewThreads(first:50){nodes{id comments(first:1){nodes{databaseId}}}}}}}' \
  -f o=HualapaiValley -f r=HVO.AgentControl -F n=<n> --jq ".data.repository.pullRequest.reviewThreads.nodes[]|select(.comments.nodes[0].databaseId==$cid)|.id")
gh api graphql -f query='mutation($t:ID!){resolveReviewThread(input:{threadId:$t}){thread{isResolved}}}' -f t="$tid"
```

A thread with no `VERIFIED_*` reply is unresolved, and branch protection will
not let the PR merge with it open.

## 7. Ready, checks, converged, merge

Only after the latest review verdict is `APPROVE` on the current head and every
finding has a verified disposition with its thread resolved:

```bash
gh pr ready <n>
gh pr checks <n> --watch
```

Marking ready starts the self-hosted Build and Unit check on exactly that head.
When both required checks are green, the reviewer posts the convergence summary
(via `post-review` as the next `R<k>` with `Mode: Convergence`), the operator
labels it and merges:

```bash
gh pr edit <n> --add-label review:converged
gh pr merge <n> --squash --delete-branch
```

Squash is right for feature branches into `development/v1`: one commit per
PR, the PR number in the subject. (Promotion into `main` is the opposite: a
merge commit, so those squashed SHAs survive.) Pushing after `APPROVE`
invalidates it; the reviewer must re-verify the new head, and the bot refuses
to post anything bound to the old one.

Do not post a converged verdict before the required checks have run. On
HVO.SkyMonitor this was done once (PR #907) and Preflight then failed twice on
diagnostics the review had dismissed. The verdict is a claim about the head; the
checks are the evidence for it.

## 8. Close the issue

```bash
gh issue comment <n> --body 'COMPLETE
PR: #<pr>
Merged into development/v1 as <sha>
Review: PR-<pr>-R0 (Standard, posted as hvo-agentcontrol[bot], <findings>) -> PR-<pr>-R1 CONVERGED
CI: Preflight <t>, Build and Unit <t>
<what was delivered, with the numbers>
Not promoted to main.'
gh issue edit <n> --remove-label workflow:in-progress
gh issue close <n>
```

"Not promoted to main" is literal: the nightly promotion carries it, and the
promotion PR lists the issue number. If a follow-up was discovered and not
taken, open it and link it here rather than leaving it in the comment.

## 9. What the nightly does with it

At 02:00 America/Phoenix, if `development/v1` is ahead of `main` and its head's
push run is green, `promote-main.yml` opens (or refreshes) one PR from
`development/v1` to `main` as the bot, listing every commit and the issues they
reference. `main`'s full pipeline (`V2 CI`: build/test with the real-OpenCode
wire test, Docker config/image with the isolation and worker-contract suites,
and both hermetic browser suites) runs on it. The operator merges it with a
merge commit. That is the only way `main` moves, and a deployment to
`home-docker` is taken from `main`, never from `development/v1`.

## Worked example: HVO.SkyMonitor PR #909 (until this repository has its own)

| Step | What happened |
| --- | --- |
| Claim | #908 assigned, `workflow:in-progress`, `review:standard` |
| Implement | 9 Dependabot bumps, SDK 10.0.401 in 14 places, 6 further packages; 2 implementation commits (7 on the PR once review-body and correction commits are counted) |
| Local gates | restore clean, both builds warning-clean, format clean, category audit unchanged, Unit 3496/3496, Integration 676/676 across six assemblies |
| Draft | body listed every version, the CA2025 re-probe, and the Redis release-notes summary |
| R0 | verified digests against MCR, action SHAs against tag objects, Redis API surface against the source; no findings on the diff; **posted by the bot** |
| F1 (Low) | the review body was still on the branch (SkyMonitor commits it; this repository does not) |
| R1 | delta only; F1 `VERIFIED_CORRECTED`; **posted by the bot**; operator resolved the thread |
| Ready | Preflight 9s, Build and Unit 7m18s |
| Merge | squash, `3932273d` |
| Promote | carried to `main` by #911 the same night, merge commit `d999c172` |
