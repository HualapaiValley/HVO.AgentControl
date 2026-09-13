# Advanced concurrent coordination exercise

Started 2026-09-07. Tracking: [epic #29](https://github.com/RoySalisbury/HVO.AgentControl/issues/29). Uses the three existing Big Pickle development workers and dedicated routing coordinator; existing owner/M4 work is excluded.

## Workload

| Work item | Branch | Initial assignee | Acceptance |
| --- | --- | --- | --- |
| #19 Effective container resources | advanced/effective-resources | Beta Reviewer | Measured host/effective limits distinguished; quota/cpuset/unlimited/malformed cases; actual 2 CPU/4 GiB evidence |
| #23 Applied decision receipts | advanced/decision-receipts | Beta Fixer | Applied versus proposed/superseded work explicit; bounded context; legacy compatibility and atomicity tests |
| #27 Coordination stress/soak | advanced/coordination-soak | Beta Developer | Meaningful invariants; explicit opt-in eight-minute repeated-test soak with timestamps/counts/failures; publish branch before soaking |
| #28 Current coordination status | advanced/coordination-status | Next eligible free worker | Concise active/queued/blocked participant view using existing durable state; independent review |

The first three assignments must overlap. A/B review each other's actual pushed heads as capacity permits while C remains occupied. D is queued until a worker can take it without overlapping a modifying assignment. PRs are published by the authenticated host as branches arrive; the coordinator routes independent review and correction using exact SHAs. Source work stays in persistent registered checkouts, one modifying worker per branch. Identical paths on different runtimes remain independent checkouts.

## Stress and evidence

- Measure actual overlapping native commands, completed-work-to-next-assignment latency, busy-slot use, progress gaps, permission requests and intervention count.
- Keep the long task active across at least one web-only controller restart. Preserve native session/caller identities and reconcile before any retry.
- Record applied decisions separately from model proposals. Invalid JSON, stale decisions, failed validation or missing evidence must remain visible.
- Run real tests repeatedly with bounded duration/resource limits. Do not substitute a long sleep for workload, launch uncontrolled background workers, or inject load into owner/M4 machines.
- Local/CI integration checks remain deterministic protocol evidence; native model reports remain reported evidence until independently verified.
- Existing architecture allows one active coordination run containing multiple concurrent work items. Multiple independent simultaneous coordination runs are still a separate product feature.

## Gate

All four work items need pushed PR heads, independent native reviews, relevant tests, and supervising verification before merge. The eight-minute soak must have real iteration/timing/exit-code artifacts and no unexplained failures. Record observed product defects and fixes rather than declaring an unattended stress pass after manual rescue.

GitHub publication remains a host bridge; no account-wide token is copied into workers. Prompts follow [the assignment guide](BETA_ASSIGNMENT_GUIDE.md). Container limits remain two CPUs/four GiB each. Leave the fleet running and update the implementation handoff with exact run/PR/evidence references.
