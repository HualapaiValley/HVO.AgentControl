# Retention, evaluation evidence and native context

Owner direction, 2026-09-08: use logs and conversations to improve worker tools, provisioning and prompts, then archive redundant storage. Preserve failed attempts, approvals and corrections alongside successful examples. Treat OpenCode disk history and model context as separate operational concerns.

This document defines the policy and implementation contract for #66, with #34/#35 usage/resources, #42/#43 task and environment lifecycle, #78 intake, #120 native recovery, and [#157 context budgets and safe compaction](https://github.com/RoySalisbury/HVO.AgentControl/issues/157). The [initial measured audit](validation/retention-audit-2026-09-08.md) records what was actually captured and archived. Automatic application/native retention and context management are **not implemented by this document**.

## Current implementation and gaps

AgentControl's journal keeps about 10,000 events, with current native-process baselines and replacement evidence exempted. Local transcript history is normally capped at twice `HistoryLimit=200` per worker; the insertion-batch over-retention case in #66 remains a regression to fix. Telemetry keeps 200 ordered observations per runtime. Acknowledged operator updates have a pruning method, but this is not a comprehensive scheduled retention policy.

Command records are retained for request idempotency and recovery. `MaxCommandRecords=10000` is an admission cap, not cleanup: reaching it blocks new records. Requested prompts, effective execution prompts and full assistant/tool results can dominate database storage. Worker deletion removes cached transcript, assignments and request projections; command audit and remote native history are not sufficient to reconstruct every owner-only attestation afterward. Export must precede those deletions and journal pruning.

The retained database is not a full traffic recording. Events roll out, only bounded transcript pages are cached, and child transcripts may never have been collected. Preserve and expose coverage gaps rather than relabeling a snapshot as complete training data.

## Policy defaults and activation

These are initial beta defaults for implementation. Existing count-based cleanup remains the deployed behavior. Do not enable automatic expiry merely because this policy exists: durable verified export, retrieval, protected-reference checks, disk-failure handling and restore tests must first pass.

| Data | Hot operational target | Archive and retention rule |
| --- | --- | --- |
| Active/uncertain work, pending permissions, active child sessions, preparation and recovery | Retain all evidence required for delivery, ownership and recovery | No age-based expiry while unresolved; a disconnected/idle observation is not a release |
| Completed prompts, outputs, tools, permission replies and session evidence | 14 days after verified task closure, subject to a bounded configured byte budget | Private compressed raw archive; review at 180 days. Beta raw evidence remains pinned until export coverage and curated examples have been reviewed |
| Curated setup/prompt/regression examples | Small indexed records with source lineage | Keep for the project's lifetime or until explicitly superseded; preserve adverse cases and holdout sets |
| Request identities, authority/review receipts and archive references | Compact searchable metadata | Keep for the lifetime of the deployment identity; archived payloads remain retrievable. Expiry must never make an old request executable again |
| Usage and operational summaries | 90 days of detailed accounting; bounded time-series queries | Daily aggregates for 365 days, with provenance/gaps and raw-archive references. Unknown cost stays unknown; subscription-reported zero is not proof of free usage |
| Routine application/Docker logs | Size-based rotation, currently 10 MiB × 3 on inspected containers | Export incident windows before rotation when pinned; routine 14-day archive target after collection exists |
| Deployment rollback backups | One recent known-good backup available unpacked | Verified compressed backups: target 7 daily, 4 weekly and 3 monthly restore points, plus pinned releases/incidents. Multiple same-day copies may be compressed immediately; expiry requires a coverage check |
| Native OpenCode state | Every active task/session and required recovery lineage | Export closed task generations, verify restore, then retire through the owned native/environment lifecycle. A UI archive flag is not proof of reclaimed disk |

Storage pressure must produce visible archive/backpressure status and restrict new admission before critical evidence is lost. Preserve the last known-good restore point. A byte budget is not permission to silently discard unresolved work or training examples.

## Archive and deletion protocol

1. Create a durable export operation with deployment, host/runtime, project/task/attempt/session IDs, policy revision, source boundaries and exclusions. Freeze the selected closed set; work that becomes active or referenced again is excluded.
2. Use a consistent SQLite backup or a verified logical export. Never copy only a live database file while ignoring its WAL, truncate a live WAL, or edit native event tables. Record native/application versions and independent source cutoffs; exports from separate databases are not automatically one atomic distributed snapshot.
3. Store a manifest containing schema version, byte counts, hashes, archive location, covered sequences/IDs, gaps, permission mode and restore prerequisites. Raw evidence can contain private source, tool output and encrypted credential records. Keep it owner-private locally; off-host storage requires configured encryption/access/retention. Provider secrets and authentication payloads do not belong in evaluation exports.
4. Verify every archived file/blob by hash and perform a restore/read check. Incomplete, corrupted, unavailable or quota-exhausted storage leaves source evidence retained and the operation visibly Failed/Unknown. Retry uses the original operation identity and receipts.
5. Produce derived evaluation metadata and review the candidate examples. Pin incidents, independently verified reviews, failed prompts and important setup changes. Analysis may discover a new hold; classification must not itself erase data.
6. Only a service operation may remove eligible hot payloads. Recheck references, authority and policy revision atomically; preserve immutable idempotency tombstones and archive retrieval. Commands' current `Same`/replay checks depend on original payload semantics, so replacing payloads with empty JSON or deleting rows is not a valid implementation.
7. Report actual reclaimed bytes separately from database free pages, WAL bytes and archive bytes. Reclaiming physical SQLite space is a separate maintenance operation after backup and scheduling; no live `VACUUM` is implied by logical pruning.

Content-addressed storage is a useful next step: for each of the 819 coordinator prompts in the first audit, that command's requested text equaled its effective text. The 819 prompts were not all the same prompt. Reuse exact immutable text blobs while retaining request/execution envelopes, hashes, policy provenance and replay semantics. Repeated full deployment snapshots can be compressed now; incremental/content-addressed exports require their own restore coverage.

## Evaluation records and useful labels

A versioned evaluation record should link the exact requested and effective prompt/template, task/repository/ref, model/provider/variant, native/application versions, worker image/configuration digest, actual user/PATH/tools, environment and permission profile, capability freshness and child lineage. Include tools/exit evidence, approvals and wait time, provider errors, interventions, tests, PR/review references, compaction boundaries, recovery generations, usage provenance and archive coverage.

Separate delivery, task outcome and evaluation judgment. A Finished command only proves the observed model turn ended; `NeedsReview` is not a positive training label. Prefer exact-revision CI/review and owner attestation for verified outcomes. Do not infer that a native tool status of `completed` means its shell exited successfully: the audit contains `completed` calls with exit 127. A multi-command shell can also return zero after an earlier missing executable.

Classify setup causes independently: missing binary, wrong PATH/user, missing workload/browser library, unavailable Docker capability, bad repository/root, missing credential permission, provider readiness, quota, and interrupted environment. Installing more tools does not fix all of these. Promote repeated confirmed missing tools into reviewed devcontainer profiles; advertise verified paths and capabilities in the task preamble rather than prompting broad filesystem discovery.

For prompt comparisons, preserve the task, environment and model context. Test explicit worker-instruction boundaries, compact current policy, repository-qualified artifact IDs, known tool paths, bounded output and progress/report formats. Keep historical instructions as history; only current revisioned owner policy can authorize actions. Compare like tasks across prompt/configuration versions; heterogeneous retrospective model totals do not establish which model is best.

Use a reviewed, redacted derivative for evaluation outside the private archive. Exclude opaque/encrypted reasoning and credentials. Automated text matches are triage leads, not verified defects; source listings and quoted incident reports can contain the same error strings. Maintain an evaluation holdout and report sample size, selection rules, missing data and uncertainty. No automatic external training upload or fine-tuning is part of this policy.

## OpenCode storage versus model context

The installed release is 1.18.29, source `16747470f976aca3d362ad730bcd3fe82ecc2c9a`. Current online V2 documentation is not a drop-in description of this deployed V1 adapter.

In the pinned source, automatic context checks use previous completed-message usage and the selected model's advertised limits. Auto compaction is enabled unless disabled, but a zero/unknown catalog context limit prevents this check. The usable limit reserves output/input headroom; the exact branch differs depending on whether a separate input limit exists. This is not a prediction of an arbitrarily large next prompt/tool output. [Pinned overflow calculation](https://github.com/anomalyco/opencode/blob/16747470f976aca3d362ad730bcd3fe82ecc2c9a/packages/opencode/src/session/overflow.ts).

V1 supports `auto`, opt-in `prune`, `tail_turns`, `preserve_recent_tokens` and `reserved`. Old-tool pruning marks eligible tool parts as compacted; it does not purge native event storage. Compaction records a summary and recent tail for model context. It is lossy and may itself fail. [Pinned configuration](https://github.com/anomalyco/opencode/blob/16747470f976aca3d362ad730bcd3fe82ecc2c9a/packages/core/src/v1/config/config.ts), [compaction implementation](https://github.com/anomalyco/opencode/blob/16747470f976aca3d362ad730bcd3fe82ecc2c9a/packages/opencode/src/session/compaction.ts).

The pinned `stream` implementation loads session pages into an array before `filterCompactedEffect` filters the model-facing history. This is a concrete reason to measure long-session allocation/latency even after compaction; it does not prove the earlier OOMs' cause. Native event journals also persist independently of the current message projection. [Pinned history implementation](https://github.com/anomalyco/opencode/blob/16747470f976aca3d362ad730bcd3fe82ecc2c9a/packages/opencode/src/session/message-v2.ts), [native durable event store](https://github.com/anomalyco/opencode/blob/16747470f976aca3d362ad730bcd3fe82ecc2c9a/packages/core/src/event.ts).

Required next behavior:

- Each new development task gets a fresh native session/workspace binding under #42. Resume the same session for valid recovery of that task; retain an explicit generation link if replacement is unavoidable. Do not reset a busy worker to reclaim history.
- Preserve per-workgroup controller sessions with compact current C# evidence. Replace superseded policy in a durable checkpoint; do not repeatedly append a complete backlog or repeat historical holds. Use evidence retrieval/change detection to reduce current 63,000-character decision prompts.
- Record effective model context/input/output limits with provider/configuration provenance, last completed usage including cache counters, freshness and an estimated next-input budget. Distinguish cumulative usage from current context, unknown limits from zero usage, and active-turn zero counters from a measured empty context. Today's `ModelChoice` does not retain these limits.
- Guard model changes and fallback to smaller-context models. Account for output, instructions, tool schemas and incoming content; invoke supported compaction only through a durable, version-verified operation at a safe boundary. Record checkpoint, model/variant, parent task and usage, and reconcile uncertain responses instead of resending.
- Show context estimate/headroom, compaction activity and stale/unknown status in the conversation UI. Capacity remains physical host/environment admission, not a context-window percentage.
- Measure native DB/WAL/event bytes, session/part counts, compaction count, bounded history-fetch latency and native RSS against short/long session fixtures. Native archive/delete/recreation requires exact ownership, a drained parent/child tree, verified export, preservation of unpushed work and explicit retained-state evidence.

## Delivery and acceptance

Keep the first-host devcontainer milestone moving. The immediate audit/compression is an operator action; implement automatic retention in a separate reviewed slice instead of attaching deletion to a provisioning callback.

The first #66 service slice should expose authenticated bounded inventory/dry-run/export/verify/read APIs with durable operation IDs and progress. Initially leave pruning disabled. Add archive retrieval and protected-reference/idempotency tests before a second prune slice. Include failed export, disk full, restart before/after each effect, deleted-worker owner attestation, late child activity, unreadable archive, race with recovery, repeat requests and old-client replay. Prove restored evidence can reproduce a curated case. UI/coordinator/MCP callers must use those same service boundaries.

Context management should separately prove smaller-model fallback, oversized tool output, no provider limit, failed/uncertain compaction, parent/child activity, current-policy precedence and preservation of external side effects. Cold/warm and long-session measurements must identify the exact runtime/configuration; a smaller archive or a successful short canary is not a performance benchmark.
