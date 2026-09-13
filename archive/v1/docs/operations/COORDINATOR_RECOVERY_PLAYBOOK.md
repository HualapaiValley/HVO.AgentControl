# Coordinator recovery: evidence before retry

Owner intent is generic: coordinate the selected workgroup, keep useful work moving, and recover failures. The service supplies current worker IDs, workspace paths, repository scope, provider catalogs, command receipts, pending requests, and retained results. Do not bake deployment names or addresses into a reusable instruction.

## Classify what actually failed

Before asking for a JSON correction, inspect the caller-scoped native result. A completed assistant message with `info.error` is a native failure, even if its text is empty. An HTTP 401 is an authentication/route failure; 429 is a provider limit; neither establishes malformed coordinator output. Never send keys, response headers, cookies, or raw provider error bodies into an operator status summary.

For a provider failure, preserve the failed request and effective provider/model identity. Hold the failed route, choose only an owner-authorized configured alternative, and verify real inference through that alternative. A catalog entry or HTTP acceptance alone does not prove that inference succeeds. Avoid repeating the same unchanged failure indefinitely. Missing permissions or uncertain effects need an explicit incident and recovery step, not another formatting prompt.

## Repair configuration before resuming

A proxy credential belongs to its proxy endpoint. Register it under a distinct provider such as `cliproxy`, with an explicit base URL and an owner-approved catalog. Install the configuration at the OpenCode user's global configuration location before starting the managed server, so every worker workspace sees the same route. A file in the managed server's launch directory is not necessarily loaded for task directories. Inspect effective configuration using the **task directory**, not the launch directory. Changes to cached global configuration may require a controlled process restart after settling activity.

Keep credentials in the native credential store and the host's encrypted vault. Do not put credentials in repository files, prompts, logs, source control, or command-line arguments. A new fleet also needs repository-scoped GitHub access registered and verified in the actual managed process; having `gh` installed is insufficient.

## Retain the coordination ledger

The control database is the primary ledger: run instruction and follow-ups, command IDs and effective routes, delivery attempts, native caller IDs, assignments, results, approvals/questions, provider holds, and applied decisions. OpenCode history supplements that ledger; it cannot replace it.

Before replacing a session, inspect command receipts, native status and tool evidence. Settle never-dispatched queued probes through the queue API. Abort confirmed orphaned turns through the owner API and wait for the receipt. A restart alone does not settle unknown delivery or authorize replay of worker side effects. Retain interrupted evidence and issue a linked fresh decision or explicit recovery instruction. Ask workers for current state if the retained ledger is stale; ask them to inspect existing branches and PRs before opening duplicate work.

Use the current `expectedRevision` from the relevant API response when pausing, resuming, or adding a follow-up. `revision` is not the request field name. A conflict caused by an incorrect request body is an operator-client problem, not proof that the controller rejects recovery.

## Verify recovery end to end

Verify all of these separately:

1. Runtime transport and API are healthy.
2. A real model reply arrives through the configured provider.
3. The coordinator emits a validated decision and the service records outgoing command IDs.
4. Workers emit actual progress/tool evidence and terminal responses.
5. The coordinator consumes those responses and makes its next decision.

`Ready`, a live container, and `AcceptedByRuntime` alone do not establish active work or successful recovery. A monitor must surface lack of progress and repeated native failures as incidents. A timer that repeatedly restarts healthy processes can create more unknown deliveries; preserve deliberate owner pauses and avoid destroying long-running work.

## Incident: 2026-09-09 remote fleet

The new remote fleet's model catalog appeared available, but task-directory configuration did not load the CLIProxy endpoint. The proxy credential reached OpenAI directly and received HTTP 401. An older controller branch deployed during recovery classified the empty errored responses as invalid decision JSON. It also lacked later native-process and provider recovery behavior.

Recovery restored main revision `decc067`, installed the distinct global CLIProxy provider, settled orphaned/never-dispatched attempts through the APIs, restarted OpenCode after the configuration change, refreshed workspace catalogs, verified real inference, and restored scoped GitHub App registration. The coordinator then issued three ordinary worker assignments. No code review or implementation was claimed from the earlier attempts: their native histories contained zero tool parts.

A subsequent, separate format-recovery loop had successful native model responses and syntactically valid JSON. The nested `githubMergeScope.repository` field was an object with `owner` and `name`, while the C# contract requires a single `"owner/repo"` string. The generic format correction did not identify the field. An owner follow-up specifying the exact type immediately restored valid routing: one worker continued its implementation and two others received independent PR reviews. This failure needed schema guidance, not a provider switch or process restart. Repair feedback should identify a safe service-known field and expected type; it must not repeat arbitrary model text or raw exception contents.

The external watchdog was installed from a persistent directory with a private incident ledger, an explicit five-container allowlist, both owned Compose project labels, and systemd user lingering. It monitors every 30 seconds. Confirmed stopped containers qualify for bounded starts; schema/provider incidents are recorded for control-plane recovery and analysis. Continuous service supervision was enabled through a paused checkpoint renewal, preserving the existing assignments and receipts.

Release recovery must identify the actual deployed revision and compare it to the intended baseline before rebuilding. A locally checked-out topic branch can be substantially older than the running controller even when its top commit is recent. Preserve backups before deployment and do not use a downgrade as a generic restart procedure.
