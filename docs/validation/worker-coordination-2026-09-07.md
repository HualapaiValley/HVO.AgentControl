# Worker lifecycle and coordinator validation — 2026-09-07

## Implemented

- Worker name/project/description and model/agent/variant editing, independent settings revision, immutable execution settings per accepted prompt, and migration of legacy queued defaults when editing.
- Archive/restore with fresh idle, queue, uncertain-delivery, and pending-input checks. Native conversation and workspace claim retained.
- Structured native question choices/custom answers and readable permission scope with native reply identity checks.
- Durable arbitrary-prompt coordinator runs, single active run, allowlisted recipients, atomic fan-out, correlated native response capture, question answers, bounded decisions, and pause/stop/resume.
- Dedicated Docker coordinator runtime, persistent home/SSH volumes, generated SSH/API credentials, and an existing-session coordinator conversation in the owner demo.
- Updated handoff and runbooks clarify that the coordinator interprets general requests and ordinary responses. Its role is not restricted to a PR workflow, and model strength is independent of worker model choices.

## Executed checks

Required pinned-SDK restore, Release build with warnings as errors, and format verification passed. The first focused persistence/lifecycle/coordination run passed 10 tests. The broader regression run passed 20 tests with two explicitly skipped optional native-release tests; the final full regression passed **21 tests, 0 failures, 2 optional native-release tests skipped** (`worker-coordination-final.trx`). After adding owner follow-ups, the focused persistence/lifecycle/coordination suite passed **12 tests, 0 failures** (`coordination-followup.trx`), including rejection of superseded decisions without interrupting native execution.

The expanded browser smoke passed against an isolated application and disposable SSH fixture: worker edit while native work is active, old/new captured model IDs, reasoning choice, question radio selection, permission reply, archive/restore, unchanged native session ID/history, two-worker draft/navigation isolation, page close/reopen, and desktop/mobile layouts across Overview/Runtimes/Workers/Coordination.

The fake fixture advertises a second deterministic model solely to exercise model selection and capture. That browser check does not establish real inference quality for different models.

A separate live check used actual OpenCode 1.18.29 and `opencode/big-pickle` in three disposable conversations under a separate managed validation server. The coordinator asked Clock to execute `date -u`, forwarded Clock's timestamp to Observer, and interpreted Observer's acknowledgement. It completed in three coordinator turns with exactly two worker prompts. The source timestamp appears unchanged in both the broadcast and acknowledgement. See [native evidence](native-coordination-2026-09-07.json). The owner's original worker was not prompted. The owned validation server was stopped afterward; the dedicated demo coordinator server remains separate.

Persistence tests simulate a web-controller restart between Clock's completion and the coordinator's next decision and verify no duplicate dispatch. Stale batches, unauthorized recipients/permission actions, competing question replies, archive restrictions, and prompt-setting capture are tested separately.

## Failures found and resolved

- The added navigation item overflowed mobile width. Navigation now wraps; the browser check passed on all four pages afterward.
- The coordinator's initial public-key mount was owned by the host UID rather than the container's agent UID; OpenSSH correctly rejected it. Startup now copies the public key to the agent-owned `.ssh` directory, sets its permissions, and explicitly selects that path even with an existing SSH config volume. Initialization waits for generated host keys before recording the fingerprint.
- A browser locator assumed an exact select-label string that included option text; the locator was corrected.
- An initial test invocation used an older build without the new migration; rebuilt tests and the required migration passed. One overlapping MSBuild worker exited early; subsequent sequential builds passed.

## Limits

The implementation supports one unfinished coordinator run at a time. Responses are interpreted by a model; completion is reported, not independent verification of task correctness. Local Ollama, different coordinator model quality, advanced scheduling, multiple simultaneous runs, destructive deletion, MCP evidence-fetch tools, and active-turn steering were not validated or implemented. The complete current behavior is documented in [the coordinator runbook](../COORDINATION.md).

## Demo deployment

The web image was rebuilt and the existing demo data backed up before migration. The updated container passed readiness, anonymous API denial, owner sign-in, and interactive navigation/reload on all four pages. Both existing worker/native session mappings were compared before and after deployment and remained identical. Both Docker demo and AgentControl coordinator runtimes report Healthy; their workspace model catalogs were refreshed. The demo remains at `http://192.168.1.14:5054`, with no coordination automatically started on the owner's behalf.

Disposable SSH fixtures and the isolated browser application were stopped after validation. The owner web controller, demo client, and dedicated coordinator remain running. No repository commits, pushes, PR comments, or production deployments were made.
