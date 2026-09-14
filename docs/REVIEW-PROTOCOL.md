# Phase 1 Review Protocol

## Selection

Terminology: a review cycle is the reviewer set for one exact PR base/head
(one reviewer for routine and focused-correction reviews, two for high-risk);
a development batch is the dependency-ordered issue queue. See the
[execution workflow](DEVELOPMENT.md#execution-workflow). Next-cycle deferrals
mean the next development batch and its relevant reviews, not an unrequested
extra review of a merged PR.

Reviewer count follows the revised owner-approved practice: a routine change
gets **one** independent reviewer, a high-risk change gets **two**, and a
focused correction after a routine review gets **one focused reviewer**. A
reviewer is drawn from the eligible pool below. Exclude the coordinator's model,
including aliases of that same model. Record the pool, draw, exact requested
provider/model IDs, coordinator identity, immutable base/head SHAs, session IDs
and result references. When two reviewers are used, both review tasks together
count as one cycle.
If a selected review or task model fails, the owner authorizes choosing another
from the applicable approved list without asking again. Record the failure,
original model/session, replacement and reason; never substitute silently.
For reviews, randomly draw from remaining eligible models, excluding the
coordinator, the other reviewer and aliases of either, and models that already
failed in this cycle. Stop for owner direction if no eligible model remains.
A failed attempt is not a completed review or a new review cycle. A replacement
gets the same immutable packet in a fresh session, without the other review's
findings. For tasks, reconcile any partial edits/tool effects before handing off;
model replacement does not authorize replay of uncertain writes. Do not use
generic Task agent types as proof of model identity.

The approved names currently map to these committed policy-lane catalog entries.
Routine reviews normally draw Fable, Opus, Sol or DeepSeek; high-risk work
prefers Astra or Opus 1M; a focused correction may use Sonnet. The actual draw
still follows the independence/exclusion rules below, and availability failures
use the recorded replacement procedure rather than silent substitution:

| Approved Name | Catalog Provider/Model |
| --- | --- |
| Astra | `cliproxy/gpt-6-astra` |
| 5.6 Sol | `cliproxy/gpt-5.6-sol` |
| Fable 5.1 | `cliproxy/claude-fable-5.1` |
| Opus 5 | `cliproxy/claude-opus-5` |
| Opus 5 1M (high risk/context) | `cliproxy/claude-opus-5-1m` |
| Sonnet 5 (focused correction) | `cliproxy/claude-sonnet-5` |
| Deepseek v4.1 Flash | `cliproxy/deepseek-v4.1-flash` |
| Big Pickle | `opencode/big-pickle` (alias `cliproxy/big-pickle`) |

Catalog presence is not a successful call or verified upstream identity. These
IDs are policy/capability aliases from catalog
`cliproxy-phase1-2026-09-14-v1`, not promises that every environment supplies
them or that a named source lane served the request. CLIProxy uses weighted
round robin, one-hour session affinity and automatic failover, and force mapping
may report the alias in the response. Exclude both Big Pickle provider aliases
if Big Pickle is coordinating. Never use `auto` or `default` routing as an
independent named model. See [CLIProxy model policy](CLIPROXY-MODEL-POLICY.md).

The owner replaced Fable 5 with `cliproxy/claude-fable-5.1` in the approved pool
after its successful PR #231 review. Do not draw the retired Fable 5 entry.
Other provider spellings are not additional independent reviewer slots.

## Isolation And Evidence

Reviewers do not implement changes, edit the branch, or see one another's
findings before all reviewers finish. Supply the same immutable head and base, issue
acceptance criteria, relevant source, complete diff and validation evidence.
Include the unchanged context needed to evaluate the diff: relevant complete
configuration, callers, tests and contracts. A missing line in a diff is not
evidence that a setting or safeguard does not exist. State packet scope/omissions;
if a finding depends on omitted context, inspect it before accepting the finding
and supply the necessary context for subsequent review. Do not expose either
reviewer's findings to the other while their independent reviews are in flight.
The review base is the recorded merge-base of the PR head and target branch,
not the previous reviewed head used to compute a correction diff. Both reviewers
attest the same base/head pair. If the merge-base changes, record the new pair
and review the new diff and changed interactions; prior evidence does not silently
carry over. Advancing the target without changing the merge-base still requires
the normal up-to-date CI and interaction checks before merge.
Do not attach secrets, private runtime transcripts, owner-password files or
unrelated repositories. Disable external plugins, delegation and writes; a
no-tool review can receive a prepared complete review packet. A large packet
must not be silently truncated: use read-only bounded source access or report
the review incomplete.

Use explicit model selection, not model self-identification. Verify the
assistant session/message provider/model metadata and any available upstream
response/routing metadata. CLI-selected metadata establishes what OpenCode
requested, not what a proxy actually executed. If upstream aliases/fallbacks
cannot be verified, disclose that exact limit and ask the owner whether this
evidence is sufficient before counting the review as satisfying the policy.
Smoke calls only validate selection/connectivity; they are not code reviews.

For Phase 1 the owner explicitly accepted OpenCode's recorded requested policy
lane as sufficient selection evidence, with upstream proxy routing uncertainty
disclosed. It is not evidence of actual serving identity. **Random independence
is requested-lane independence only.** Distinct aliases can share the same
DeepSeek (or other) fallback entry, so actual upstream model independence cannot
be guaranteed from selection metadata alone; disclose that overlap whenever it
applies. Do not describe a review as true model independence. Effort proof
requires all three available layers: the selected lane advertises the variant, the
completed session/message records the requested provider/model/variant, and the
proxy request carries the corresponding reasoning field (for these OpenAI-
compatible lanes, `reasoning_effort`). A label or self-report alone is not
proof. This does not permit undisclosed configured fallback. For every review in
every cycle, verify the actual completed review session's assistant-message
provider/model metadata against that draw. Historical smoke results never waive
this check, even with unchanged provider configuration. An incomplete/timed-out
response does not count as a review; reconcile the original session and retry
the same selected model when safe, recording both attempts, or use the approved
replacement procedure above. Big Pickle aliases are not two
independent review models, and `default`/`auto` can never be the named review.

The managed AgentControl runtime uses the committed direct
`@ai-sdk/openai-compatible` provider generated from the control exposure profile;
it does not use a plugin, dashboard config sync, MCP injection or dynamic
discovery. A repo reviewer running a general interactive OpenCode client may use
the lightweight community CLIProxy provider, but that is not the managed
employee's authority. A plugin-free reviewer needs an explicit process-local
provider configuration using existing environment references; do not re-enable
all plugins or change the user's global configuration to make a review work.
Never copy credentials into repository files, issue bodies or command arguments.

## Cycles And Findings

Cycle one reviews the full PR. Later cycles examine every correction and its
interactions with the rest of the PR; expand scope when a fix affects an earlier
assumption. Reviews remain bound to the exact new head SHA, not the last head
that happened to pass CI.

Consolidate duplicate findings only after all reviews finish. Post a short
summary and actionable inline findings with severity, location, reproduction or
reasoning, and acceptance impact. Triage owner comments too. Reply to every fix
thread with its change, exercising validation and exact head before resolving
the thread. Do not label deferred work fixed.

Reviewers provide evidence, not authority or a majority vote. Verify each finding
against the actual repository, acceptance criteria and applicable safety rules.
Consolidate duplicates without losing distinct concerns. Disposition every item:
**Fixed** with commit/test evidence, **Rejected** with specific contrary evidence
or scope reasoning, or **Deferred** with owner approval and a linked issue.
Stylistic suggestions are not automatically defects or new backlog. Disagreement
between reviewers is resolved by investigation, not by choosing the favorable
review. Do not dismiss an established security/correctness problem as cosmetic
or defer it simply to end a review loop.

Mark each owner-approved deferred finding **Deferred, not fixed** in a comment
on its original review thread, and include the follow-up issue number. The issue
must link back to the finding, carry `status:deferred`, describe the remaining
acceptance criteria, and be scheduled into the next development/review cycle.
Resolving the original thread records that disposition, not a completed fix.
Keep the follow-up open until its own implementation, validation and review are
complete; avoid closing-keyword references from the source PR and verify issue
state after merge so GitHub cannot silently close unfinished work.

At the next cycle, explicitly list carried-forward issues in the review packet
and require each reviewer to assess their fixes and interactions. Do not leave
them as an unscheduled backlog. If a carried item still cannot be completed,
report it for a new owner disposition; the prior deferral is not permission to
defer it indefinitely. Remove `status:deferred` only after verified completion,
or an explicit replacement disposition recorded by the owner.

Stop when all required reviews are clear, findings are resolved and exact-head required
CI is green. At most three cycles are allowed; pause for the owner before cycle
four. A deferral needs explicit owner approval and a linked issue. Security,
data-loss, acceptance, failing-CI and material-correctness findings block.

For PR #223, the owner explicitly authorized cycle four after the three-cycle
pause recorded in #224. After that cycle, remaining non-critical findings may
be deferred to linked follow-up issues with their actual status disclosed, and
the PR may merge with required exact-head CI green. This exception does not
waive blocking security, data-loss, acceptance or material-correctness findings,
nor does it authorize a fifth cycle or change other PRs' three-cycle default.
A blocking finding after an authorized fourth cycle keeps the PR unmerged;
pause for a separate owner decision before further review. Non-critical
deferrals still follow the next-cycle carry-forward rule above.

## Authorization

One implementation issue per branch/PR. Do not amend or force-push a reviewed
head. On 2026-09-13 the owner explicitly authorized bounded Phase 1 commits,
pushes and focused PRs, with merge only after both independent reviews and
required exact-head CI pass. This is standing owner authorization, not GitHub
auto-merge or permission to bypass a failed gate. It includes the development
deployment refresh and scoped disposable two-host, initial-hire and
teardown/rebuild tests. It excludes production changes, release tags/registry
publication, new credential grants, and destruction of existing development
data. Pause for repository-owner direction on those actions or any expanded scope.

Record the reviewed SHA and this standing authorization on each merge. Other
work remains subject to the repository's explicit authorization requirements.

Before merge, reconcile the current PR body with the actual reviewed head,
required checks and all finding dispositions. After merge, follow the execution
workflow's issue/branch/main-CI checks. Thread resolution records a disposition;
issue closure records completed acceptance (or an explicit owner alternative);
merge records integration. None substitutes for the others or for a release.
