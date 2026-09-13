# Backlog triage and assignment order

Owner direction, updated 2026-09-08; implementation tracker #78. This supersedes older “next work” lists in checkpoint documents. Repository: `RoySalisbury/HVO.AgentControl`.

## Current operating rule

Finish or review active work before starting another overlapping implementation. Product priority and execution readiness are different: a high-priority item with open prerequisites stays blocked. An active or uncertain task keeps its ownership when priorities change. A worker is general-purpose; an idle worker needs a concrete ready assignment, review, or documented blocker, not an indefinite reservation for an issue number.

Root's initial audit compared every open issue body with merged PRs, current PRs, implementation status, provider/telemetry contracts and live coordinator receipts. Most old open issues are **partially implemented**, not forgotten complete work. #53 is the design parent of #75, not a duplicate. #1 is a beta epic. No issue was closed solely because a similar PR exists.

## Current owner priority and dependency order

The [execution environment milestone](EXECUTION_ENVIRONMENT_PLAN.md) supersedes earlier chat/provider-first ordering. Finish current PR104 and already commissioned audit108 work without duplicate assignments or model changes. A concrete production-breaking defect may interrupt the sequence; record the reason. New unrelated UI, provider catalog and transport expansion follows this milestone.

`docs/backlog-plan.json` is the machine-readable plan. Dependencies describe the explicit remaining slice, not every acceptance item in partially shipped parent issues. #4/#5/#7 already have enrollment, ownership and evidence foundations; do not assign their original implementation again. Inspect live source and split a bounded prerequisite when broad issue closure would incorrectly block ready work.

| Order / scope | Priority | Next delivery and prerequisite |
| --- | --- | --- |
| #121 configuration foundation | P0 | Separate host, runtime/environment, worker, project, workspace and task-session identities; additive persistence and REST contracts with legacy compatibility. Root prepares the contract, then implementation follows independent review. |
| #6 capacity; remaining #4/#5 integration | P0 / P1 | Reuse shipped authority/ownership, reserve shared host and environment resources, account for builds and conditional multiworker support. New environment integration follows #121. |
| #42 task workspaces/sessions | P0 | Fresh project-qualified sessions, owned preparation and separate writable workspaces; follows #121/#6. |
| #43 provisioning and lifecycle | P0 | Existing-machine/devcontainer selection, official CLI, durable REST/UI operation cards and real creation/restart/drain/retirement tests; follows #121/#6/#42. |
| #98 multi-project acceptance | P0 epic | Run two registered projects against shared capacities and compatible isolated environments; follows the above, not a standalone giant assignment. |
| #43 fleet transition | Acceptance gate | Canary through the new flow, prove recovery/retained work, drain and migrate legacy workers one at a time. No bulk live migration from this planning change. |
| #35 / #120 | P1 supporting reliability | Effective memory/pressure observations and interrupted-native recovery support admission/lifecycle. Confirmed incidents can be repaired independently in a bounded slice. |
| #7 / #8 / #44 / #70 | P1 remaining integration | Existing evidence and GitHub access support bounded planning, exact reviews and durable publication/merge intents; do not rebuild shipped primitives. |
| #75 / #53, #34, #54 / #84, #69, #78 | Retained backlog | Preserve working UI/provider/usage/routing/triage behavior; re-audit remaining scope and implement when milestone dependencies or a concrete incident require it. |
| #108 | Existing audit | Preserve completed A/B artifacts and current C/D work. Synthesize actual coverage and prioritize verified defects without duplicate implementation. |

Default managed development uses one worker/one active task per devcontainer and many capacity-accounted containers per Docker host. Existing physical/VM runtimes can have multiple isolated slots. New tasks use fresh sessions/workspaces; compatible environments may be reused sequentially. Checkpoints add optional recovery and do not replace isolation or block provisioning. Every new capability includes versioned REST, authorization, idempotency, bounded progress and API/integration tests.

The report is a read-only, point-in-time readiness assessment. Active ownership, changed issue/PR evidence and deployment status must be checked immediately before assignment. Do not treat issue closure as proof of deployment or a P0 label as permission to skip prerequisites.

## Executable read-only audit

Run from an authenticated checkout:

```bash
python3 scripts/prioritize-backlog.py --active 82 108 121 > /tmp/backlog-report.json
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

## Idle capacity reassessment (#78, service trigger)

An active Waiting run now requests a fresh coordinator assessment when its last decision is at least five minutes old and a non-stale participant is idle without an outstanding assignment. This also applies while another worker has a long-running task. `Control:CoordinationIdleReassessmentMinutes` accepts 1–60 minutes. The existing persisted `LastDecisionAt` provides the deadline across restart; reconnect observations are still required. Each request records a `CoordinatorIdleReassessmentRequested` event and an explicit context reason.

This closes the unchanged-command-fingerprint stall. It does not fetch GitHub directly or implement the full periodic priority audit: the coordinator must delegate fresh evidence gathering where needed. Active/uncertain work, owner pauses, provider gates and the configured decision-round budget remain authoritative. Owner permissions stay in the operator UI and worker status; only task questions enter the actionable questions array. A pending owner approval excludes its worker from idle eligibility, without blocking another available worker. An expired round budget still pauses; this trigger does not silently extend model usage.

The coordinator is instructed to reuse review evidence at an unchanged revision, explain concrete blockers, and consider independent ready work while a PR waits for merge. Semantic duplicate-review prevention remains guidance until durable task/review identities are enforced. The service never creates arbitrary tasks merely to fill capacity.
