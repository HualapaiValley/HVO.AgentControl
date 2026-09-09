# Reliability assessment: preserve the managed environment, prove the agent engine

Status: accepted direction, 2026-09-09. Broad backlog assignment is paused. This is a trial plan, not a completed reliability result.

## What we keep

The devcontainer launcher and lifecycle controller remain core product capabilities. AgentControl owns host selection, workspace/repository isolation, official devcontainer CLI provisioning, resource budgets, GitHub App credential delivery, durable task and communication receipts, approvals, recovery policy, and the administrative UI/API. Preserve current containers, volumes, branches and database while the engine comparison is prepared.

An engine runs a task in an assigned environment. OpenCode and native Claude Code should be interchangeable behind a task/session adapter. A Claude model selected inside OpenCode still uses the OpenCode harness and is not a native Claude comparison. Transport connectivity, a live process and a successful model reply are separate from successful task completion.

The controller service owns deadlines, dispatch, observation and recovery. Models advise on task selection, review and recovery choices. Neither an engine's conversation nor its peer messaging replaces the durable ledger or authorizes replay of uncertain side effects.

## Why expansion is paused

On September 9, the existing full snapshot exceeded the watchdog's 8 MiB response bound. The watchdog process stayed alive while observation failed. Separately, GitHub CLI rewrote managed App credentials with a valid token format that our ownership parser rejected, preventing renewal. These are control-plane integration failures; they do not establish which engine is more reliable.

The owner-approved pause stops new coordination assignments. Already dispatched work remains independent. At the initial pause, no worker commands remained open. Unmerged PRs and local branch work must be reconciled before any retry. Do not bulk-close the backlog or destroy environments as part of this freeze.

## Entry gates

1. Deploy and verify the compact authenticated observation endpoint. Seed evidence larger than 8 MiB in a disposable database; verify bounded status, older unresolved commands, pause state, provider errors, credential expiry, and explicit incomplete/stale monitoring alarms.
2. Repair token-format handling without weakening directory ownership, single-account identity, token equality, or fingerprint checks. Prove renewal with the installed GitHub CLI version and fake credentials, then observe scoped live delivery and an automatic renewal.
3. Record deployed source/image revisions, environment/image/config hashes, tool versions, model routes, permissions and resource limits. Verify actual inference and repository access in each task directory.
4. Provision and remove a disposable worker through the managed devcontainer lifecycle. Verify effective user, tools, repository boundary, owned cleanup and retained shared caches. Existing containers are preserved until replacement passes.
5. Verify native Claude authentication and inbox availability inside its isolated test environment. An authenticated CLI on the administrator's host does not establish container login or cross-machine discovery. Never put credential material in the trial ledger or prompts.

## Comparison design

Use the same small .NET fixture and equivalent isolated workspaces for both engines. Start with three paired repetitions, alternating engine order. Record exact models and routes. Use the same model/provider when both engines support an authorized route; otherwise report the model difference and avoid attributing all differences to the harness. Keep task prompts, tool permissions, CPU/memory limits, repository scope and progress cadence comparable.

Run two experiments separately:

- **Engine adapter:** implementation, independent review, correction, second review and merge, with AgentControl dispatch and receipts throughout. This measures the engine integration under the same controller.
- **Native messaging:** independent Claude sessions exchange an assignment, acknowledgement, progress and completion using their native inboxes. Test isolated containers and cross-machine relay separately. Mirror the resulting receipts into the trial ledger; do not present this as an existing AgentControl feature.

Claude documents `ListAgents`/`SendMessage` for independent sessions. Local discovery uses same-machine inboxes; cross-machine discovery requires Claude login and Remote Control. Incoming messages retain the receiver's permission checks. Container boundaries and the relay therefore need their own test, even when local messaging works. [Claude cross-session messaging documentation](https://code.claude.com/docs/en/cross-session-messaging).

OpenCode documents a persistent HTTP server with session and event APIs. Test the project's pinned release (currently 1.18.29), recording the actual binary version rather than assuming current public documentation matches it. [OpenCode server documentation](https://opencode.ai/docs/server/).

## Workload and fault cases

| Case | Required evidence |
| --- | --- |
| Known faulty implementation → PR → independent review → fix → review → merge | Exact commits, review receipt, passing tests and one merge; the seeded defect is actually detected and corrected. |
| Two independent tasks plus one long-running task | No duplicate assignment, no repository overlap, progress at the configured cadence, other work continues. |
| Controller web process restart during active work | Same task/session identities reconciled; completed side effects are not repeated. |
| Brief transport loss followed by recovery | Unknown delivery held until reconciled; no prompt replay based only on elapsed time. |
| Worker process/container exit | Owned recovery within policy, persisted workspace retained, tool side effects inspected before continuation. |
| Invalid model decision and repeated invalid repair | Bounded repair, specific feedback, alternate recovery or explicit incident; no infinite formatting loop. |
| Provider quota/authentication failure | Correct classification, authorized fallback or actionable hold; no unchanged retry loop. |
| Credential renewal and deliberate unrelated-account fixture | Renewal succeeds for the owned profile; unrelated credentials remain untouched. |
| Large conversation/evidence history and observer failure | Bounded status reads; fresh/stale distinction survives monitor restart; observation recovery is recorded. |
| Second repository with identical issue numbers | Task, workspace, PR and review remain bound to the intended repository. |
| Owner pause during work | New assignments stop; live work and unknown receipts remain visible. |

Inject destructive faults only into disposable trial environments. Restarting the production UI is a deployment check, not a substitute for a repeatable failure fixture. Use scoped trial branches and PRs; never merge the intentionally faulty revision into the production branch.

## Results and pass criteria

Record each event with UTC timestamp, trial/run/task/command identity, repository, workspace, engine/session, model/provider, phase, receipt state and linked commit/PR evidence. Record input/output/cache tokens, elapsed time, billed cost when available, and resource peaks. Mark unavailable usage or subscription cost as unknown, never zero. Keep raw transcripts in restricted retained storage and publish sanitized summaries.

Compare completed tasks/hour, time to first acknowledgement, time to first useful action, handoff delay, recovery time, duplicate/lost work, stalls, approval interventions and cost per completed task. Classify each failure as controller, environment, credential, provider, engine, permission policy or workload before selecting a remedy.

An engine passes only after the paired workload succeeds and a full 24-hour supervised soak needs **zero manual recovery**, loses/duplicates **zero tasks or externally visible side effects**, preserves repository boundaries, and surfaces each injected outage within the declared timeout. Define acknowledgement/progress/recovery deadlines before starting; initially use a 60-second acknowledgement, five-minute progress cadence and five-minute outage escalation. Long tools may exceed five minutes only with recorded tool activity and an explicit deadline. A safety hold can be correct behavior; it does not count as autonomous task completion.

A failed entry gate prevents starting the comparative clock. A short successful recovery is evidence for that case only. Any manual rescue is recorded and fails the unattended criterion; after repair, begin a new named soak rather than erasing the incident.

## Decision

If OpenCode passes, retain it and make native Claude an additional adapter when useful. If native Claude passes and OpenCode does not under comparable conditions, prioritize the native adapter while keeping devcontainer management and the central ledger. If both fail in the same control-plane path, fix that path before judging engines. Resume broad backlog work only after this evidence supports it.
