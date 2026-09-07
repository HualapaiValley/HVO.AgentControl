# Backlog triage and assignment order

Owner direction, 2026-09-07; implementation tracker #78. This supersedes older “next work” lists in checkpoint documents. Repository: `RoySalisbury/HVO.AgentControl`.

## Current operating rule

Finish or review active work before starting another overlapping implementation. Product priority and execution readiness are different: a high-priority item with open prerequisites stays blocked. An active or uncertain task keeps its ownership when priorities change. A worker is general-purpose; an idle worker needs a concrete ready assignment, review, or documented blocker, not an indefinite reservation for an issue number.

Root's initial audit compared every open issue body with merged PRs, current PRs, implementation status, provider/telemetry contracts and live coordinator receipts. Most old open issues are **partially implemented**, not forgotten complete work. #53 is the design parent of #75, not a duplicate. #1 is a beta epic. No issue was closed solely because a similar PR exists.

## Initial audit and dependency order

`docs/backlog-plan.json` is the machine-readable plan. “Depends on” describes the remaining delivery scope; an independently useful preparatory slice may be split into its own explicitly bounded issue instead of pretending the whole blocked feature is ready.

| Issue | Priority / disposition | Delivered evidence and next scope | Hard prerequisites |
|---|---|---|---|
| #75 / #53 | P0 active / design parent | Chat UI/performance owner priority; Astra xhigh owns #75 | none |
| #78 | P0 active | Root owns executable audit and this plan; durable periodic scheduler remains | none |
| #34 | P1 correction/review | Usage parsing PR56; PR72 ledger needs backfill correction and fresh review | none for current PR |
| #69 | P1 review, then foundation | Task models PR57; reasoning PR76; durable routing/risk policy remains | #5 for remaining policy |
| #65 | P2 remaining scope blocked | Child visibility PR60 and vanished replies PR77 merged; primed tools and actual task directories remain | #42 |
| #35 | P2 remaining scope needs triage | Snapshot calculations PR64, history PR73 and periodic Linux sampler PR79 merged; validate deployment, then scope macOS/v1 | none |
| #5 | P1 ready, first foundation | Missing work-item/phase ownership despite existing command IDs; add atomic claim/restart contract | none |
| #4 | P1 ready | Existing stable sessions/commands are not the full enrollment/authority-generation contract | none |
| #7 | P1 ready | PR21/38/41 bound summaries; durable per-consumer cursors/exact retrieval still missing | none |
| #6 | P1 blocked | Shared endpoint reservations and safe release need task ownership | #5 |
| #8 | P1 blocked | Prose review receipts exist; durable exact-head independent verdicts still missing | #5 |
| #42 | P1 blocked | Workers remain directory-bound; fresh task/project sessions are missing | #5, #6 |
| #44 | P1 blocked remaining scope | App delivery/renewal PR46/59 and CI permissions PR74 work; durable publication intents do not | #5, #8 |
| #70 | P1 blocked remaining scope | CI permissions PR74 merged; merge intent/gates/serialization/reconciliation remain | #8, #44 |
| #43 | P2 blocked full lifecycle | Official CLI templates PR45/48 verified; automatic create/drain/retire and cache policy remain | #5, #6, #42 |
| #66 | P2 blocked archive foundation | Operational retention is bounded, not a training archive | #5; usage/telemetry enrichment is a soft dependency |
| #3 | P2 ready follow-up | PR62/71 schedules/outbox shipped; material milestones, active-set baselines and retention remain | none |
| #54 | P2 ready follow-up | ChatGPT website sign-in PR55 shipped; later owner comment adds other methods, durable readiness and managed credentials | none |
| #10 | P2 needs acceptance audit | PR39/51/71 current status shipped; coordinate remaining UI scope with #75 | do not duplicate #75 |
| #13 | P2 needs evidence audit | PR25/36/63/71 and native exercises cover many scenarios; map all acceptance to evidence before closure | none |
| #24 | P2 needs scope audit | PR49/52 bounded recovery shipped; native schema/free-model proof remains, not parser recovery again | verified provider availability for native proof |
| #9 | P3 blocked expansion | Additional harness support is distinct from selecting models inside OpenCode | #4 |
| #67 | P3 blocked expansion | External issue-comment intake must wait for task/capacity/publication lifecycle | #42, #43, #44 |
| #1 | epic | Maintain its checklist as child acceptance is actually satisfied | children |

This order promotes #5 because it directly unlocks several older/newer requirements. It does not block safe PR review behind future prerequisites for the issue's broader scope. PR merge, deployment, and full issue acceptance are separately recorded states.

## Executable read-only audit

Run from an authenticated checkout:

```bash
python3 scripts/prioritize-backlog.py --active 34 35 65 69 75 78 > /tmp/backlog-report.json
python3 tests/test_backlog_priority.py
```

Supply the **current** active/uncertain issue ownership set each time; the example is a point-in-time audit, not permanent assignments. The tool fetches all issues and PR evidence, including closed prerequisites, from the plan's explicit repository using `gh`. It refuses a potentially truncated 1000-issue snapshot. Offline input uses `--issues file.json` with `{ "repository": "RoySalisbury/HVO.AgentControl", "issues": [...], "pulls": [...] }`; a repository mismatch fails.

Only audited candidates with closed prerequisites and no active claim enter `ready`. Unplanned issues or issues changed since the recorded audit timestamp require triage. A referenced PR changing state/head, or missing PR evidence, also invalidates the plan even when the issue timestamp is unchanged. Cycles/missing prerequisites block dispatch. Ready candidates sort by owner-assigned priority, number of direct dependents unlocked, age, then issue number for stable ties. Output preserves source update timestamps, blockers and concise rationale. The script never dispatches, closes, merges or edits anything. Revalidate current revisions, runtime eligibility and ownership immediately before dispatch; issue closure alone is not proof of deployment.

## Periodic coordinator review

Target policy: audit every 60 minutes and after owner steering, new/changed dependencies, merge/completion, or a genuinely idle pool. Debounce bursts and permit one audit at a time. Budget a small model for gathering/classification, stronger review for ambiguous dependency/security decisions. The coordinator delegates source collection to a worker and receives a bounded report; it remains the routing model.

While #78's durable scheduler is not implemented, root runs this audit during monitoring and the coordinator must consult the latest plan before each new assignment. Do not claim a timer exists merely because a prompt says “periodically.” The future service must persist next-due time, plan version, source revisions, candidate decisions and dispatch reason; failures preserve the last valid plan but stale candidates require revalidation. No audit failure may stop worker monitoring or replay work.

For each audit, record newly ready/blocked items, oldest ready item, active claims, review queue, starvation/interventions and proposed duplicate/obsolete dispositions. Reserve capacity for the highest ready foundation instead of repeatedly starting new polish. Owner incidents may preempt *new* assignments, never kill existing tasks.

## Duplicate and closure rules

A model may propose a canonical issue with evidence; semantic similarity is not enough. Distinguish parent/child, implemented foundation/remaining scope, and obsolete wording/new requirement. Link merged PRs, tests and deployment observations. Close only when all acceptance is met, or move explicit remaining scope to a linked canonical issue and record why. Preserve discussion; do not bulk-close old issues. Reopened prerequisites invalidate a ready plan at the next audit and must be checked before dispatch.
