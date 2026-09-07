# Adopting the SkyMonitor coordination prototype

Reviewed 2026-09-07. This is a design/adoption review, not a claim that the proposed features are implemented. No prototype commands, agent enrollment, GitHub messages, or live assignments were changed.

## Recommendation

Keep the prototype's ownership, receipts, recovery and evidence rules. Move scheduling, dispatch authorization, current state, resource reservations and routine communication into AgentControl's service and database. Keep the coordinator model responsible for interpreting requests, selecting suitable agents and forming concise prompts. It must not become the timer, lock manager, or source of durable truth.

GitHub should remain a task artifact/integration: issues, PRs, reviews and comments requested by a task. It should cease being the heartbeat and inter-agent message bus. Arbitrary prompts and prose responses remain supported; PR lifecycle controls are an optional workflow profile, not a requirement for asking an agent the time.

## Evidence inspected

- [Coordinator training guide](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513#issuecomment-5565244201), posted 2026-09-07 04:56:47 UTC.
- [Permanent enrollment contract](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513#issuecomment-5561026605), [Claude client audit](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513#issuecomment-5565130223), [heartbeat adoption gate](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513#issuecomment-5564873845), and [host/daemon inventory](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513#issuecomment-5561026689).
- The three linked mutable coordinator/OpenCode/Claude status slots, fetched individually; their current contents are snapshots, not immutable policy.
- SkyMonitor main pinned to `76e48bdb4933a9d91b21a9623dfb822cb49dcb09`: [execution protocol](https://github.com/RoySalisbury/HVO.SkyMonitor/blob/76e48bdb4933a9d91b21a9623dfb822cb49dcb09/docs/planning/agent-execution.md), [agent prompts](https://github.com/RoySalisbury/HVO.SkyMonitor/blob/76e48bdb4933a9d91b21a9623dfb822cb49dcb09/docs/planning/agent-prompts.md), [experiments](https://github.com/RoySalisbury/HVO.SkyMonitor/blob/76e48bdb4933a9d91b21a9623dfb822cb49dcb09/docs/planning/coordination-experiments.md), AGENTS.md, command guard/tests, and review dispatcher.
- [Accepted protocol changes #674](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/674), [implementation PR #679](https://github.com/RoySalisbury/HVO.SkyMonitor/pull/679), and [channel evaluation #664](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/664).
- AgentControl handoff sections 12–15, current coordination docs, `CoordinationStore`, `CoordinatorService`, `RuntimeSupervisor`, `ControlDb`, `AssignmentGuidance`, and single-replica lock.

The authenticated GitHub API supplied the source; unauthenticated browser access returned 404. Statements called proven in the prototype's audit remain reported operational evidence here. Independently checked: the pinned guard's local fixture tests pass; its implementation performs pre-write validation; AgentControl's current store enforces transactional routing and revision checks. No live review, merge or heartbeat experiment was replayed.

## What transfers

| Prototype practice | AgentControl adaptation | Current coverage / gap |
| --- | --- | --- |
| Stable participant identity and explicit JOIN | Versioned enrollment record bound to authenticated adapter, host and native session; separate work authorization | Stable worker IDs and pinned SSH exist; external enrollment/authority epochs do not |
| Commands separate from participant-owned status | Durable command/receipt stream, separate versioned current-status projection | Commands and snapshots already separate; extend for external clients |
| Command ID, target and lease echoed in receipts | Adapter envelope with command ID, participant ID, enrollment generation, attempt ID and monotonic receipt sequence | Command IDs and native message correlation exist; explicit external receipts/fencing do not |
| Current slots plus durable ledger | Compact status rows plus retained evidence and append-only protocol events | Current records/events exist, but event retention is bounded; important evidence needs independent durable references |
| Scheduled operator heartbeat plus immediate milestones | Service-owned schedules and notification outbox; model-independent status delivery | Progress preamble and overdue text exist; persistent heartbeat jobs/outbox do not |
| One implementer per issue/worktree | Durable work-item claims through review, CI, merge and cleanup | Workspace claims exist; native turn capacity is not lifecycle capacity |
| Docker and finalization reservations | Typed resource reservations with generation, owner, scope and verified release | Not implemented |
| Immutable review ranges and carried findings | Review record with base/head, reviewer route, finding dispositions and artifact links | Arbitrary review prompts work; typed review evidence/gates do not |
| Bounded retrieval and exact evidence references | Persisted per-consumer cursor, compact digest and paged evidence retrieval | Latest 16 run commands and bounded excerpts exist; durable evidence cursors/retrieval do not |
| Independent harnesses | Shared coordination envelope implemented by OpenCode, Claude Code and Codex adapters | OpenCode transport only; selecting Anthropic/OpenAI models is not native Claude Code/Codex harness support |

## Important qualifications

The guide is a useful recovery aid, not a replacement for its canonical documents. For example, the accepted execution protocol skips an unnecessary merge/base-sync review when the newly fetched target SHA is already the reviewed target. Adopt that conditional rule in a repository workflow instead of copying an unconditional merge ritual. C1.2 support in scripts or a closed issue does not establish that the required live cutover acknowledgement happened.

The 500-byte C1.1 rule is an optimization target, not a reliable property of every observed message. At retrieval, the coordinator, OpenCode and Claude slot bodies measured 976, 759 and 425 UTF-8 bytes respectively. AgentControl should generate compact views automatically, retain full evidence separately and budget model input. Do not require agents to hand-count prose or discard important evidence to meet a comment-format limit.

The guard's body-hash check verifies the supplied local body. It does not atomically bind that check to a later GitHub PATCH. Likewise, reading receipts before an external side effect does not guarantee exactly-once execution. Its tests are useful input-validation cases, not proof of distributed write atomicity. AgentControl should atomically accept/claim commands and receipts in SQLite, while explicitly retaining uncertain delivery when the remote side effect cannot be reconciled. Never blindly resend a potentially accepted task.

A missing heartbeat establishes an observation gap, not failed execution. A lease timeout must not automatically release a workspace or Docker reservation while an isolated agent may still be using it. Fence new authority, reconcile the actual operation, and require a terminal/release record before assigning a conflicting successor. Fencing only works where adapters/resources enforce it; a database cannot revoke an already-running shell command.

## Service and model responsibilities

The coordinator model chooses the next useful action from bounded, attributed evidence. It may propose a review, answer an in-scope question, ask for capability verification, or route a result. The service validates and commits the decision against current state before dispatch.

The service owns identity, timers, idempotency, queue order, deadlines, scope checks, resource reservations, notification delivery and recovery. Worker-owned status updates cannot overwrite coordinator commands. Agent-reported state, native observations and coordinator interpretations retain separate provenance and observation times.

Keep permanent `Worker` / `Coordinator` roles. Implementer, reviewer and observer are assignment roles/capabilities. A reviewer designation or prompt is not itself an enforced read-only execution boundary. Advertise whether the adapter can enforce it. The coordinator's small-model conversation remains routing-only. For a PR workflow, approved finalization operations are validated service actions or targeted executor assignments; the model does not acquire an unrestricted terminal or merge authority merely because the prototype coordinator performed those actions itself.

Repository-specific rules belong in versioned project profiles: priority ordering, implementation capacity, review requirements, correction limits, finalization and cleanup. Do not hard-code CameraAgent-first, two global issues, fixed model names, or MST into the general product. Preserve operator authority and repository instructions when running that repository's tasks. Store times in UTC; offer labeled display timezone preferences, including fixed UTC-7 if selected.

## Enrollment and adapter contract

Use a stable opaque participant ID. Store harness/version, provider/model controls, host identity, native session, capabilities, protocol version and display name separately. Changing a model must not rename the participant or destroy its conversation. A provider-bearing human label may remain useful but cannot authorize dispatch.

A proposed enrollment flow is Requested → Accepted → Joined/Available, with a scoped acknowledgement and explicit adapter confirmation. Enrollment grants eligibility, not a task. For existing owner-managed OpenCode sessions, the trusted service adapter can establish this binding without injecting ceremonial JOIN prompts into active model turns. External clients need authenticated, participant-scoped credentials; a self-reported ID is insufficient.

Negotiate support for asynchronous prompt acceptance, final responses, intermediate progress, questions, tool permissions, cancellation, history recovery, model changes and active-turn steering separately. An unsupported capability is explicit, not simulated by TUI keystrokes. Connection generations and enrollment authority generations are different: a routine reconnect should not invalidate legitimate ongoing work. Session replacement or host migration requires deliberate reconciliation and a new authority binding.

The transport carries the protocol envelope; the worker model still receives ordinary instructions and replies normally. Receipts distinguish accepted delivery, observed execution, reported completion and verified outcome. A result reference may point to a PR comment, file, test run, image or any other artifact.

## Heartbeat and progress design

Separate three clocks:

1. Transport/adapter liveness: when the service last successfully observed the participant.
2. Work progress: when a meaningful agent report or native work transition was observed.
3. Operator update delivery: when the service published a scheduled status update and, where supported, a client acknowledged it.

A live connection is not proof of progress. A queued progress question behind a long model turn cannot guarantee a timely answer. The service should publish known state on schedule, even while the coordinator model or every worker is busy. An unchanged update needs no model call. Invoke the coordinator for material changes, ambiguity or a decision; retain a deterministic fallback if inference is slow or unavailable.

Persist schedule ID, active-set revision, interval, next due time, last emitted event sequence and notification delivery state. Emit an immediate baseline when monitoring starts; emit milestones immediately. Active-set changes produce an updated baseline without indefinitely postponing an existing deadline. Deduplicate scheduled notifications by schedule/due time; acknowledge delivery idempotently. On restart, record a missed interval/gap and emit one catch-up snapshot instead of flooding historical ticks.

Use the existing authenticated browser event path to update an Activity view, with durable notification records and reconnect cursors. Publishing to an open browser, retaining an unread update, and delivering to a closed browser are distinct promises. A disconnected UI cannot receive a live update; retain it for return. External push/email integrations would be separate configured features, not an implicit GitHub fallback.

Suggested compact display: current phase, last meaningful update age, next step, blocker, and resource reservations. Historical review results stay in workflow details; they must not turn an otherwise ready worker into a perpetual NeedsReview card.

## Minimum durable additions

These are proposed concepts, not a mandate for a table per noun:

- **Participant enrollment:** authenticated adapter/session binding, protocol/capabilities, authority generation, join state and revocation.
- **Command receipts:** command/attempt/participant/generation, sequence, receipt type, observation time, evidence reference; uniqueness and stale-write checks.
- **Work item:** objective, project scope, owner, phase, dependencies, acceptance evidence and cleanup responsibility. It may span several native prompts.
- **Resource reservation:** endpoint/resource key, participant/work item, allowed use, generation, grant/expiry, terminal state and release evidence. Include storage backing and workload suitability in inventory. Cloned Docker engine IDs must not collapse distinct endpoints; aliases to one real endpoint need explicit reconciliation too.
- **Progress projection:** bounded finished/current/next/blocker fields, provenance, source event sequence and freshness, independently writable from transport liveness.
- **Scheduled update/outbox:** durable due time and delivery cursor, independent of model turns.
- **Evidence/review:** immutable artifact/range references, observed model/effort, verdict, finding dispositions, costs and validation identity.

Preserve the existing single-replica SQLite boundary while extending it. The in-process gate/file lock is not multi-host dispatch fencing. Do not expand to multiple controllers until that failure model is tested.

## Optional repository workflow

Model a work item separately from a worker turn: claim → implementation → review → corrections → finalization → cleanup → released. Waiting for review/CI can still consume its configured implementation slot even when its worker is native-idle.

A review stores the exact repository/base/head range, purpose, selected and observed provider/model/effort, request/ack deadlines, prior findings, fallback and evidence. Start an external reviewer only after the durable reservation exists. Prefer local read-only repository evidence or a bounded evidence pack; process success or a report file never implies a clean review. A reviewer unable to inspect the range reports incomplete.

For correction reviews, preserve the original reviewed SHA and verify every prior finding across the delta. Make the prototype's three-correction limit configurable. A limit may defer non-blocking findings under project policy; it cannot convert unresolved material defects into approval. Review validity must be reconsidered when the head/base changes.

Serialize finalization per repository, revalidate the actual head/base/CI and authority immediately before a side effect, and reconcile ambiguous remote outcomes. Cleanup targets the host that owns the worktree. Claim release follows verified cleanup or an explicit recorded disposition, not merely a PR merge. Evidence reuse requires matching commit, environment and configuration; old green tests do not apply automatically to a new head.

## Adoption sequence and acceptance

1. **Service-owned updates and bounded evidence.** Add durable schedules, separate liveness/progress projections, outbox and cursors. Test scheduled UI delivery through long tools and coordinator inference, browser disconnect, restart, unchanged state and duplicate wakes. A useful initial trial is at least 20 scheduled intervals; measure lateness and missed updates explicitly.
2. **Enrollment and receipts.** Extend the existing OpenCode adapter first, then add one native Claude Code or Codex adapter. Test two similarly named agents, wrong target, duplicate consumption, old generation, restart after remote acceptance, lost acknowledgement, changed model and resumed native history. Do not migrate all harnesses at once.
3. **Work claims and resource reservations.** Test two implementers claiming one item, a stale claimant returning, one daemon reached through aliases, cloned engine IDs on different hosts, and a disconnected task retaining resource ownership. Count lifecycle capacity independently of active native turns.
4. **Review/finalization profile.** Exercise the owner's PR → independent review → fixes → review result loop with exact range records. Then add guarded finalization and host-specific cleanup. Test changed heads, incomplete reviews, deadline fallback, duplicate dispatch and merge-success/receipt-loss recovery.
5. **Measured cutover.** Import selected prototype identities, evidence references and active claims read-only for comparison. Do not infer enrollment or authority from historical comments. Transfer one idle participant or completed-task boundary explicitly; establish one command source and new authority generation. Reconcile in-flight commands first. Stop that participant's old command consumer/heartbeat writer before enabling central dispatch. GitHub may receive an authorized audit mirror, but cannot remain a second active command authority. Rollback also requires reconciliation, not simply restarting both consumers.

Current code nuance: `CoordinationTick` hashes command progress and question state, not every relevant participant/capability/resource change. It also commonly waits while any prior work is unresolved. Extending event-driven scheduling must add relevant observation revisions and allow safe decisions for eligible idle participants without resending busy work. UI snapshots and native polling alone do not provide the proposed durable operator heartbeat.

Track delivery latency, duplicate execution, stale-authority rejection, prevented collisions, time waiting for resources/review, reopened findings, coordinator/reviewer tokens and failed-route cost. Report actual usage when available and mark estimates. Preserve the prototype's measured-experiment approach; adopt each adapter only after its own recovery and signaling tests pass.
