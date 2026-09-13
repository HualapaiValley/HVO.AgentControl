# Phase 1 Review Protocol

## Selection

For each cycle, randomly draw two distinct eligible models without replacement.
Exclude the coordinator's model, including aliases of that same model. Both
review tasks together count as one cycle. Record the pool, draw, exact provider
and model IDs, coordinator identity, head SHA, session IDs and result references.
Do not silently redraw when a selected model is unavailable; report the failure
and obtain operator direction. Do not substitute generic Task agent types as
proof that different models ran.

The approved names currently map to these local catalog entries:

| Approved Name | Catalog Provider/Model |
| --- | --- |
| Astra | `cliproxy/gpt-6-astra` |
| 5.6 Sol | `cliproxy/gpt-5.6-sol` |
| Fabel 5 | `cliproxy/claude-fable-5` |
| Opus 5 | `cliproxy/claude-opus-5` |
| Deepseek v4.1 Flash | `cliproxy/deepseek-v4.1-flash` |
| Big Pickle | `opencode/big-pickle` |

Catalog presence is not a successful call or verified upstream identity. These
are exact selectable IDs observed on the development machine, not a promise
that every environment supplies them. Exclude both Big Pickle provider aliases
if Big Pickle is coordinating. Never use `auto` or `default` routing as an
independent named model.

## Isolation And Evidence

Reviewers do not implement changes, edit the branch, or see one another's
findings before both finish. Supply the same immutable head and base, issue
acceptance criteria, relevant source, complete diff and validation evidence.
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

For Phase 1 the owner explicitly accepted OpenCode's recorded requested model
IDs as sufficient evidence, with upstream proxy routing uncertainty disclosed.
This does not permit a known fallback or substitution. Sol and Deepseek passed
selection smoke calls on 2026-09-13; their assistant-message metadata recorded
`cliproxy/gpt-5.6-sol` and `cliproxy/deepseek-v4.1-flash`, respectively. Other
catalog entries still require successful explicit selection when drawn.

The development CLIProxy catalog relies on a provider plugin for connection
configuration. A plugin-free reviewer needs an explicit process-local provider
configuration using existing environment references; do not re-enable all
plugins or change the user's global configuration to make a review work.
Never copy credentials into repository files, issue bodies or command arguments.

## Cycles And Findings

Cycle one reviews the full PR. Later cycles examine every correction and its
interactions with the rest of the PR; expand scope when a fix affects an earlier
assumption. Reviews remain bound to the exact new head SHA, not the last head
that happened to pass CI.

Consolidate duplicate findings only after both reviews finish. Post a short
summary and actionable inline findings with severity, location, reproduction or
reasoning, and acceptance impact. Triage owner comments too. Reply to every fix
thread with its change, exercising validation and exact head before resolving
the thread. Do not label deferred work fixed.

Stop when both reviews are clear, findings are resolved and exact-head required
CI is green. At most three cycles are allowed; pause for the owner before cycle
four. A deferral needs explicit owner approval and a linked issue. Security,
data-loss, acceptance, failing-CI and material-correctness findings block.

## Authorization

One implementation issue per branch/PR. Do not amend or force-push a reviewed
head. On 2026-09-13 the owner explicitly authorized bounded Phase 1 commits,
pushes and focused PRs, with merge only after both independent reviews and
required exact-head CI pass. This is standing owner authorization, not GitHub
auto-merge or permission to bypass a failed gate. It includes the development
deployment refresh and scoped disposable two-host, initial-hire and
teardown/rebuild tests. It excludes production changes, release tags/registry
publication, new credential grants, and destruction of existing development
data. Pause for operator direction on those actions or any expanded scope.

Record the reviewed SHA and this standing authorization on each merge. Other
work remains subject to the repository's explicit authorization requirements.
