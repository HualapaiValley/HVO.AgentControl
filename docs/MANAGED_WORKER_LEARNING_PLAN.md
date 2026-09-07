# Managed workers, approval analysis and GitHub intake

Owner direction, 2026-09-07: managed Dev Containers are the primary execution pool. External SSH hosts remain supported for existing machines and capabilities such as macOS/iOS builds. The coordinator accepts work, selects a suitable available worker or requests provisioning, and follows the work through independent review. This document is a plan and an investigation record, not a claim that automatic provisioning or GitHub intake is shipped.

Tracking: [approval improvements #65](https://github.com/RoySalisbury/HVO.AgentControl/issues/65), [evaluation evidence #66](https://github.com/RoySalisbury/HVO.AgentControl/issues/66), [GitHub intake #67](https://github.com/RoySalisbury/HVO.AgentControl/issues/67).

## Approval investigation

A read-only inspection of the owner demo database found 31 retained permission requests. All were `external_directory`; this is a retained sample, not a complete lifetime count. Requests were grouped by requested paths and inspected tool metadata:

| Cause | Requests | Example | Improvement |
| --- | ---: | --- | --- |
| Work outside the native session directory | 17 | Sibling `HVO.AgentControl-issue3`, `-issue34`, `-restart` worktrees | Create each task session in its actual assigned worktree; pass that path to child tasks. |
| Environment discovery | 9 | `/etc`, `/proc`, cgroup data, macOS `/Applications` | Supply a verified, timestamped capability manifest and resource limits before task execution. |
| Temporary files | 3 | Files under `/tmp` | Use a task scratch directory within the workspace when practical. One request was our deliberate approval regression probe. |
| User configuration/tools | 2 | OpenCode configuration inspection and user-bin setup | Publish tool paths and supported installation locations; preinstall recurring requirements in the next image revision. |

These are path-policy requests, not evidence that the requested operations were destructive. Permission scopes still need to reflect the actual task. Do not globally allow `/home`, `/etc` or `/tmp` to suppress prompts. A container's Docker socket, credentials and privileged capabilities remain relevant when choosing a policy.

The recorded reply commands included 14 successful `once`, eight successful `always`, one successful rejection, and four failed `always` replies. Each failure says the native request was no longer pending and no reply was sent. The four failures occurred in a burst of overlapping issue3 worktree requests; a remembered scope resolving sibling requests is a plausible explanation, not a proven causal trace. Four request rows had no linked reply command. `NoLongerPending` alone does not mean an owner approved a request.

PR #60 already fixes visibility of verified descendant-session permissions and questions. A live child read request appeared on its parent worker, was approved through the browser, and completed. The next work is to reduce avoidable requests and show an already-resolved reply distinctly from a genuine tool failure. Preserve native request identity and never replay a vanished approval against a different request.

## Evidence for later analysis

Current persistence is operational rather than a complete traffic archive. `HistoryLimit` defaults to 200; reconciliation retains at most twice that many message snapshots per worker. Journal retention defaults to 10,000 events. Message snapshots are updated, SSE has no durable replay guarantee, and deleting a worker removes its messages and requests. Child approval ownership does not imply complete child transcript capture. Missing history must remain explicit in an analysis dataset.

Add an independent, versioned analysis record/export before operational pruning. Record:

- Task, attempt, coordination run, runtime, worker, parent/child session, command, message and tool-call identities; timestamped acceptance, activity, approval waits and completion.
- Exact effective prompt and template/preamble version, instruction source, supplied context, provider/model/variant and native adapter version. Treat model summaries as annotations, not observed facts.
- Repository identity and base/head revisions; resolved Dev Container configuration and image digest, CLI version, actual user/PATH/tool versions, capability snapshot age, resource allocation and permission-profile version.
- Requested permission and scope, triggering tool, first/last observed times, responder, reply scope and native receipt/outcome. Keep absent, expired, rejected and uncertain outcomes distinct.
- Provider-reported tokens, cache usage, cost/currency and timing with provenance; missing values remain unknown. Link implementation to #34 and telemetry to #35.
- Artifacts, PR/review IDs and exact reviewed revisions, test evidence, corrections, owner interventions, and independently verified outcome. A finished assistant turn is not task success.

Keep the archive outside the live SQLite hot path with bounded buffering, explicit retention/storage limits, resumable export and recorded gaps. Exclude credentials, authentication payloads and opaque encrypted reasoning; redact sensitive tool output before export. Keep restricted original evidence separate from shareable evaluation data. Add deletion/export policy and schema/redaction versions. Do not send this data to an external training service automatically.

Start with offline evaluation: compare completion and review acceptance rates, correction rounds, approval count/wait time, environment repair time, latency and cost across prompt/image versions. Separate synthetic probes from real tasks. Control for task difficulty, model and supplied context; do not attribute an improvement to a prompt when the image or model changed too. Preserve failures and human interventions alongside successes. Later fine-tuning is a separate decision.

## Task environment contract

Build on #42 (fresh project sessions), #43 (multi-host provisioning), #44 (GitHub publication) and #54 (provider enrollment). Honor repository `devcontainer.json` using the official CLI, including Features, lifecycle hooks and declared users. Use the documented Codespaces-style fallback when no configuration exists. Cache versioned images per host; record changes and refresh between drained workers.

Before dispatch, give the worker a concise manifest: task objective and acceptance checks, repository/worktree/base revision, branch ownership, available SDKs/tools and paths, actual resource limits, required special capabilities, GitHub scope/credential status, permitted installation locations, scratch path, progress interval, review/publication rules and expected response format. Mark capabilities observed versus self-reported and allow refresh/follow-up questions. Never include credential values. Have the worker verify the manifest against its actual task directory.

Scheduling must reserve capacity durably, select by capabilities rather than worker names, and expose queue/build/connect/verify/ready/failure states. When capacity is unavailable, provision only within configured host/resource/concurrency budgets. Reconcile a timed-out creation by its durable provisioning identity before attempting another. Retain a warm pool if configured, drain idle workers before retirement, and preserve evidence/artifacts before removing a container. External hosts use the same task contract but are not destroyed as disposable workers.

## GitHub entry point

Target flow: an authorized repository operator writes an instruction such as `@hvo-agent-control review this PR`, AgentControl records a durable task, acknowledges it with a task link, obtains a suitable worker, runs/reviews the work, and publishes concise evidence back to the same issue/PR. GitHub is the entry point and results surface; the central DB remains the coordination authority.

The first supported trigger should parse an explicit configured command from an `issue_comment` webhook. GitHub documents that event for issue and PR conversation comments. Do not assume an arbitrary GitHub App can appear in the assignee picker or that the requested handle is available; assignment integration needs separate verification. Inline review comments require their own event support. See [GitHub webhook events](https://docs.github.com/en/webhooks/webhook-events-and-payloads).

Validate the webhook's original bytes using its HMAC signature, verify installation/repository scope and the sender's current authorization, and reject self-generated bot loops. Persist receipt before acknowledging; process asynchronously. Use both delivery identity and semantic comment/action identity to reconcile redelivery without duplicate task creation. Define explicit edit/cancel behavior and bind reviews to a head SHA, so later pushes cannot silently reuse an old approval. Treat repository/comment content as task data, not authority to expand credentials or provisioning limits. See [signature validation](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries) and [delivery guidance](https://docs.github.com/en/webhooks/using-webhooks/best-practices-for-using-webhooks).

The current App setup deliberately disables webhooks. Intake requires a reachable HTTPS receiver or an explicitly configured relay; the private LAN demo address is not sufficient. Do not expose the owner UI as a webhook endpoint. Outbound progress/publication uses durable intents and reconciliation from #44, including duplicate/uncertain result handling.

## Delivery order

1. Reduce approval friction with correct task directories, scoped scratch space and verified capability context; improve resolution/audit presentation.
2. Preserve versioned task evidence and provide an offline evaluation export before older operational records are pruned.
3. Complete managed Dev Container lifecycle and fresh task sessions, then exercise warm reuse and bounded scale-out alongside external hosts.
4. Add GitHub comment intake with authorization, deduplication and durable publication, first against this test repository. Test duplicate webhooks, unauthorized senders, edited instructions, changed PR heads, unavailable capacity, provisioning failure and controller restart.

## Model selection during intake

Owner refinement: choose the model per task and phase, not permanently per worker. Current task-level provider/model overrides already preserve worker defaults and native sessions. All four development runtime catalogs advertise `openai/gpt-6-astra`; catalog visibility is not proof of successful inference or remaining subscription quota. The initial routing policy below is an operational hypothesis to evaluate, not a benchmark result. [Official Astra guidance](https://developers.openai.com/api/docs/models/gpt-6-astra) identifies complex reasoning and coding as intended uses.

| Task characteristics | Initial model policy |
| --- | --- |
| Routine intake, progress summaries, bounded low-risk reviews | Luna |
| Clear, localized implementation and regression tests | Terra |
| Substantial implementation or integration across components | Sol |
| Architecture, credential boundaries, migrations/recovery invariants, distributed provisioning, ambiguous cross-repository work or difficult unresolved defects | Astra |

Review model selection follows the risk of the change, not a blanket cheaper-review rule. Use Astra for critical security, migration and recovery reviews even when another model implemented the change. Keep independent reviewers; a stronger author does not replace review. Provisioning #43, GitHub authorization/intake #67, and evidence lifecycle #66 warrant Astra design or critical-path review; routine UI work within those issues can use Terra/Sol.

The durable intake decision should contain task/phase, repository and exact revision, acceptance criteria, complexity/risk/uncertainty and supporting facts, required tools/capabilities, chosen provider/model, concise rationale, routing-policy version, budget and allowed fallback/escalation path. Validate the model against the selected runtime and provider readiness. Choose a compatible free worker after determining task requirements. Owner model choices take precedence. Show the decision and escalation history in the task UI and export them with #66 evidence.

A lightweight classifier can propose a tier; service-enforced policy sets risk floors, allowed models and resource/usage limits. Low-confidence critical classification gets a bounded stronger-model assessment. Escalate after two substantive failed correction attempts, an unresolved critical review finding or demonstrated reasoning difficulty. Tool approvals, missing dependencies, provider outages and stale connections need operational repair, not automatic model escalation. Pass exact findings, prior attempts and artifact revisions to the next model. Switch only at a safe task boundary after accepted or uncertain work is reconciled; never interrupt/replay an active mutation to change models.

If the selected model is unavailable, record the reason and use only a configured fallback that meets the task's risk floor; otherwise leave the task visibly blocked. Do not silently downgrade critical work. Do not interpret ChatGPT subscription usage as API-priced cost. Track observed provider usage/limits separately and leave unavailable cost unknown. Task-level reasoning-effort selection is future work: current overrides clear the default variant and must not pretend to set unsupported action fields.

Rollout: apply this as explicit coordinator guidance for future dispatches now, then implement a versioned durable intake policy shared by website, coordinator and GitHub entry points. Test owner overrides, risk floors, unavailable providers, invalid classifier output, bounded escalation, independent review, restart persistence, and active/uncertain-work protection. The initial live guidance is prompt-based, not yet a service-enforced routing engine.

## Free models and worker delegation

Owner refinement: use verified free models for bounded mechanical work when their results can be checked cheaply. Examples include extracting named facts from supplied text, classifying files against explicit rules, drafting a short status message from structured facts, and proposing labels. Use deterministic code for exact transformations when it already solves the task. Free is a provider/account availability and quota property, not a permanent property inferred from a model name; verify current access and successful inference before enabling a profile. Do not assume the earlier Big Pickle quota failure has cleared. Keep provider data-use terms in the configured model policy.

Apply the same routing policy to both coordinator assignments and worker subagents. OpenCode v1.18.29 `tool/task.ts` accepts `subagent_type`, resolves its agent profile, and uses the configured agent model or otherwise inherits the parent message model. It does not expose an arbitrary per-call model parameter on that task tool. A skill teaches when and how to delegate; an agent profile supplies the actual provider/model and tool permissions. See [OpenCode agents](https://opencode.ai/docs/agents/), [skills](https://opencode.ai/docs/skills/), and [pinned task implementation](https://github.com/anomalyco/opencode/blob/v1.18.29/packages/opencode/src/tool/task.ts).

Provision versioned, opt-in profiles for mechanical extraction, scoped exploration, implementation and critical review, resolving models from the runtime's verified allowlist. Skills should specify task boundaries, minimal context, expected evidence/output, validation, escalation and failure reporting. Do not hot-reload active native instances just to update a profile. Preserve repository-specific configuration and apply profile revisions to new or safely idle task environments.

The parent remains accountable for validation and synthesis. A child used by an author is not a replacement for the independent review handoff. Bound child concurrency, nesting, tool permissions and aggregate usage; record every child profile/model, effective prompt, ancestry, skill version, result and approval delay in #66. A free-model timeout or quota error may select only an explicitly allowed fallback; distinguish operational failures from reasoning failures. Evaluate candidate skill/profile versions on retained real tasks and synthetic fixtures before broader rollout. This is planned configuration and skill packaging, not a claim that free profiles have been installed or tested.

## Coordinator-managed merges

Owner authorization now includes merges in `RoySalisbury/HVO.AgentControl`; this supersedes earlier root-only merge instructions. Root still monitors, handles complex work and deploys the running host in batches. The coordinator owns merge readiness and delegates execution to an available worker, preserving its lightweight routing role. Implementation is tracked in [#70](https://github.com/RoySalisbury/HVO.AgentControl/issues/70).

Require independent review and successful required CI for the exact current PR head, resolved blocking findings, a ready/non-draft PR, and GitHub branch rules. Missing CI is unknown, never success. Re-read state immediately before merging, serialize target-branch merges, and use an expected-head conditional operation. Record a durable merge intent and actual merge SHA; reconcile timeouts by querying remote state before another write. A shared App identity does not prove worker-independent review and must not be used to bypass native review requirements.

Assign conflicts to a worker in an isolated worktree, preserve both changes, and push a normal correction. The new head needs fresh CI and independent review. Escalate difficult semantic conflicts to Astra. Do not force push, use admin bypass, or merge merely because a model says ready. Base changes that invalidate integration evidence require refreshed validation.

Live check: worker `gh pr checks` cannot access statusCheckRollup with current installation tokens. The broker currently requests only Contents/Issues/Pull requests write and Metadata read. Add explicitly scoped check-reading support and configuration/migration tests; the owner may need to accept expanded App read permissions when that feature is concrete. Classic branch-protection lookup for main returned 404; this does not establish the absence of every possible ruleset. Until direct CI observation works, use independently verified, exact-head CI receipts or keep the merge blocked. This policy authorizes merges but does not claim a durable merge service is implemented.

References: [conditional CLI merge](https://cli.github.com/manual/gh_pr_merge), [GitHub pull-request API](https://docs.github.com/en/rest/pulls/pulls#merge-a-pull-request).
