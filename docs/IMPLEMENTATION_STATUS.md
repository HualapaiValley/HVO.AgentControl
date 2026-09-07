# AgentControl implementation status

## Current priority checkpoint — 2026-09-07

The dependency-aware [backlog triage plan](BACKLOG_PRIORITIZATION.md) and `backlog-plan.json` are the current assignment source. Older “next” lists below are historical. Finish active #75 UI/performance and PR review/correction work, then prioritize #5 ownership, #4 enrollment and #7 bounded evidence; #6/#8 unlock #42/#44 and subsequent provisioning/merge automation. #78 tracks periodic triage and evidence-based duplicate/obsolete issue review.

PR73 runtime history and PR74 bounded GitHub CI-read permissions are merged. PR72 usage ledger remains in correction/review; PR76 reasoning, PR77 vanished replies and PR79 periodic sampling are merged. PR81 corrects generated migration history before deployment. These statements describe repository state, not an assertion that the running host has already deployed them.

## Earlier checkpoint — advanced coordination and provisioning verification, 2026-09-07

PR36 (soak), PR39 (current participant status) and worker-published PR47 (installation help) are merged. The final supervised run completed five assignments with three-way overlap and one delivery attempt each; its real foreground soak passed 679 test executions over eight minutes. Root fixed defects found during verification, approved one specific tool prompt, and corrected one malformed final coordinator response. See [advanced validation and exact limitations](validation/advanced-coordination-2026-09-07.md). This supersedes the older beta checkpoint's publication and next-priority statements below.

GitHub App credential brokerage is deployed. The dedicated devcontainer worker can read issues, publish comments/reviews, push a branch and create a PR with its scoped installation credential. Automatic renewal is implemented; the observed live exercise does not establish a multi-hour renewal endurance result. Publication intent/reconciliation is still #44. The App setup guide is [Worker GitHub access](GITHUB_ACCESS.md).

Dev Containers use the official CLI. The earlier registered test worker deliberately used `.devcontainer/worker/devcontainer.json` (`agent`), not the repo default config (`vscode`). Its description now exposes that distinction. The owner clarified that repository configuration should be honored by default, with a broad Codespaces-style fallback and deliberate cached image refreshes; lightweight templates are explicit alternatives. [Provisioning](DEVCONTAINER_PROVISIONING.md) records that requirement and the staged implementation plan. Default-config and nested-Docker builds are verified, including their actual users/tools and a fresh cached instance in 15.32 seconds; see [configuration verification](validation/devcontainer-configurations-2026-09-07.md). Automatic UI/coordinator container provisioning and retirement are **not implemented** yet.

The priority order from this checkpoint is superseded by [backlog triage](BACKLOG_PRIORITIZATION.md). Its linked design documents remain useful; merged foundations must not be assigned again as unimplemented features.

Issue #3 now has a bounded durable foundation: per-coordination operator schedules, immediate
baselines, periodic/catch-up update rows, terminal updates on the next scheduler tick,
deterministic database-state summaries, authenticated bounded retrieval, and idempotent
acknowledgement run independently of model turns. The Activity UI, immediate material milestones,
active-set change baselines, retention, and external delivery remain follow-up scope. See
[durable operator updates](OPERATOR_UPDATES.md).

The live owner fleet remains intentionally running. Historical statements below about no GitHub repository, no production input, or stopped beta resources describe earlier checkpoints only.

## Beta execution checkpoint — 2026-09-07

Both native beta runs are complete: pricing review → separate fixer → exact-SHA re-review (PR #18), and shipping implementation → review → README correction → re-review (PR #22). The lab now has 13 passing tests. Real controller context overflow was fixed in PR #21; a final CI preflight-cancellation race (#26) is fixed in PR #25; web restart during active native work preserved caller identity and one delivery attempt. See [full validation and interventions](validation/beta-development-2026-09-07.md) and [assignment guidance](BETA_ASSIGNMENT_GUIDE.md). This is supervised beta evidence, not unattended readiness. Next priorities are #23/#24 (decision receipts/output), #19 (effective limits), #3/#7 (scheduled updates/evidence), then #10 (workflow visibility).

The private [GitHub repository](https://github.com/RoySalisbury/HVO.AgentControl) is now established, with a [complete development plan](BETA_DEVELOPMENT_PLAN.md), [beta epic #1](https://github.com/RoySalisbury/HVO.AgentControl/issues/1), and independently scoped backlog issues. The reproducible fleet bootstrap and fresh-container SSH readiness fix are merged in [PR #17](https://github.com/RoySalisbury/HVO.AgentControl/pull/17); hosted CI passed.

Three separate .NET 10.0.400 development containers are connected to the existing dedicated coordinator. All four native sessions use `opencode/big-pickle` for the initial exercises. Workers have repository-scoped Git deploy keys; GitHub comments/PR publication is currently performed by the authenticated supervising host. The coordinator remains outside the work participant list. See [fleet operations](BETA_FLEET.md). Existing owner demo/M4 workers remain separate.

The historical initial-release notes below describe their original validation time. Their statements about an untracked baseline, no GitHub mutations, stopped test services, and hosted CI not being run no longer describe the current beta environment. Beta containers are intentionally left running.

Research checkpoint: [SkyMonitor prototype adoption review](PROTOTYPE_COORDINATION_REVIEW.md) completed against training-guide comment 5565244201, related audits/experiments and repository main `76e48bdb4933a9d91b21a9623dfb822cb49dcb09`. The pinned local guard fixtures passed. Proposed next slices are durable operator updates/evidence cursors, explicit enrollment and harness adapters, lifecycle/resource claims, then optional PR review/finalization and controlled migration. This documentation review did not change running coordination or enroll prototype agents.


The UI now uses SkyMonitor-inspired dark styling, an explicit fleet-to-conversation flow, readable current status, and collapsed history/diagnostics. Duplicate workspace setup identifies the existing worker and links to its conversation/editor. See [UI organization](AGENT_EXPERIENCE.md#ui-organization-and-workspace-recovery) and [validation](validation/ui-workspace-recovery-2026-09-07.md).


Worker setup now has durable inventory cards with progress, failure recovery and dismissal. Worker/coordinator registration deletion enforces fresh idle state and queue/coordination checks; unused runtime deletion rejects references, terminals and unresolved work. Optional CLI inventory and managed OpenCode terminal PATH are implemented. See [agent experience](AGENT_EXPERIENCE.md#setup-progress-deletion-and-tool-discovery) and [validation](validation/registration-lifecycle-2026-09-07.md).

Updated: 2026-09-07. This is the authoritative resumption checkpoint.

## macOS non-interactive command discovery

Confirmed on the owner's Home M4 host: SSH exec used `/usr/bin:/bin:/usr/sbin:/sbin`, while installed `tmux 3.6a` was at `/opt/homebrew/bin/tmux`. Added a shared POSIX command environment that appends existing standard macOS Homebrew paths, preserves configured precedence, and is applied to verification, SSH operations, bootstrap and the native launcher. Prerequisite checks now report detected executable paths. No remote shell configuration or packages were changed. See [validation](validation/macos-command-path-2026-09-07.md).

## Save hosts before agent dependencies are ready

SSH authentication and agent readiness now have separate save paths. After pinned-key SSH authentication succeeds, failed prerequisite checks offer **Save for setup**, retain credentials encrypted, and save a disconnected `SetupRequired` runtime with an admin-terminal link. The setup ticket cannot be used for verified-connect. After remediation, Edit → Verify reuses saved credentials and enables worker setup when requirements pass. No dependency installation or OpenCode start occurs during setup-only save. See [validation](validation/save-for-setup-2026-09-07.md).

## Coordinator roles, capabilities, guidance, and admin terminal

Implemented persisted coordinator-only roles with backend recipient checks, separate coordinator settings, optional versioned instruction guidance, initial worker capability inquiries, timestamped machine probes and agent reports, bounded intermediate progress delivery, and cadence/overdue display. Dedicated admin terminals use the saved SSH login in a separate shell and leave native agent sessions independent. See [operating guide and limits](AGENT_EXPERIENCE.md) and [validation](validation/agent-experience-2026-09-07.md).

Next refinements: normalized capability claims and targeted refresh after configuration changes; persistent progress digests/evidence cursors and scheduled reminders; Summarize/Assist modes; concurrent workflows and dependency/deadline handling. The broad [next-work design](COORDINATION_NEXT.md) remains the requirements reference, with its implementation checkpoint updated.

## Worker lifecycle and coordination follow-up

Worker editing preserves native session/workspace identity; model, agent, and reasoning defaults apply to future submissions. Each queued instruction captures its settings. Archive/restore retains history and workspace claims and requires fresh idle/no unresolved work. Native questions now have real choice controls; permission cards summarize tool scope.

Initial autonomous coordination is implemented: a durable run sends ordinary prompts through a dedicated OpenCode coordinator, correlates free-text worker responses, and atomically validates/queues model routing decisions. The Coordination page exposes participants, instructions and owner follow-ups, bounded rounds, pause/stop/resume, decisions, and responses. A follow-up supersedes an older unapplied decision without stopping its native turn. A separate persistent Docker coordinator runtime and native worker are provisioned for the demo. Manual operation remains independent. See [current capabilities and limits](COORDINATION.md); historical completion notes below describe earlier releases.

The native time-query/broadcast check passed with three coordinator turns and exactly two worker dispatches, preserving the reported timestamp. Worker/model/approval/archive browser checks passed, including mobile layouts. Final validation and deployment evidence is in [the follow-up record](validation/worker-coordination-2026-09-07.md).


## Page organization follow-up completed

Implemented separate `/runtimes` and `/workers` pages with a shared Overview/Runtimes/Workers menu. Registration, verification, startup settings and runtime lifecycle/diagnostics are on Runtimes. Worker/worktree creation, workspace model inspection, filters and inventory are on Workers. Overview retains live status, pending-response links and conversations with a compact agent selector. Newly created workers open their conversation; verified runtimes open preselected worker setup. Conversation query links preserve selection through reload and Back. Page disposal only stops its local snapshot subscription.

Validation completed: required restore/Release build/format checks passed; regression suite **14 passed, 0 failed, 2 optional native checks skipped** (`navigation.trx`, 1 minute 2 seconds). Expanded browser checks passed with two-worker conversation/draft isolation, query links/Back/reload, task/follow-up, question, close/reopen, and desktop/mobile rendering without horizontal overflow on all three pages. Guided onboarding also passed through preselected worker setup and native conversation creation.

The published Docker browser check exposed missing Blazor framework assets: Docker restores before copying Razor files, so automatic web-asset discovery did not run during restore. The application now explicitly declares `RequiresAspNetWebAssets`. A new package browser regression reproduced the failure on the old image and passed on the rebuilt image, including all three interactive pages and reloads before/after container restart. Existing database and authentication keys persisted; the container runs as UID 1000. Required restore/Release build/format checks passed again after this fix. See [page organization validation](validation/page-organization-2026-09-06.md).

Resumed after the VS Code disconnect, restarted the local server independently of the old editor process, and verified the owner's demo at port 5054. Its original managed runtime/native worker identities and existing conversation are preserved. The demo server and Docker client remain running; temporary browser/SSH/package validation services are stopped. README/runbooks and completion evidence are updated. No remaining implementation work or blocking input for this follow-up; no commits, pushes or production deployment were made.

## Runtime onboarding follow-up completed

Implemented the owner's guided SSH verification/enrollment, encrypted entered credentials, and optional OpenCode startup arguments. New hosts are discovered before authentication and trusted explicitly; successful checks protect password/key/passphrase values using persisted Data Protection keys. Workspace/bootstrap defaults and generated API passwords remove manual prerequisites from the normal form. Save/connect opens worker setup after native health succeeds. Mounted references/manual profiles remain supported.

Typed `serve` options are `--pure`, `--print-logs`, and `--log-level`; `--auto` is a TUI/run option in 1.18.29 and the actual `serve --auto` probe exits 1. The new migration preserves existing default behavior. Changed options cannot silently reuse a living owned server.

Validation completed: **14 passed, 0 failed, 2 native tests explicitly skipped** in the final regression run (1 minute 1 second); both native tests passed in the earlier combined run. Guided browser flow with password retry/real native arguments and manual-profile browser regression both passed. Coverage includes encrypted password/key/passphrase persistence and reuse, tampering, old trust-token rejection, authentication/CSRF, atomic save/connect and startup-option validation. The combined run exposed an existing cancellation-test race (second worker had not started), fixed by waiting for both workers to be active; a stale-token test's saved revision setup was also corrected before the passing final run. Restore, Release build with warnings as errors and format verification passed. The rebuilt non-root Docker image migrated existing data, passed readiness/anonymous API 401, and restarted with its database/keys intact. See [onboarding validation record](validation/runtime-onboarding-2026-09-06.md) for exact results and commands.

The owner's demo at port 5054 is refreshed. Browser login, new Verify form, healthy runtime and original native session identity were checked after the update. The Docker demo client remains running; the separate fixture/browser-test/package services are stopped. No known failing checks or remaining implementation work for this follow-up. Next owner action: reload the demo, open Add runtime, enter host/user/authentication and use Verify; review startup options before saving and continuing to worker setup. No commits, pushes or production deployments were made.

## Active owner test environment

After release validation, the owner requested one Docker client and the server for manual testing. These are now intentionally left running:

- AgentControl runs in Docker as `hvo-agentcontrol-demo-server` at `http://192.168.1.14:5054`. Its persistent data is `.fixture/demo-data`, secrets are `.fixture/demo-secrets`, and logs are available through `bash scripts/demo-server.sh logs`. No secondary SSH port forward is required.
- Its dedicated coordinator runtime runs in `hvo-agentcontrol-coordinator` with persistent named home/SSH volumes and its own OpenCode session.
- Docker client `hvo-agentcontrol-demo-client` uses image `hvo-agentcontrol-demo-client:local` and named home/SSH volumes. Real OpenCode 1.18.29 was installed through AgentControl's SSH bootstrap and is healthy under its owned tmux session.
- Runtime **Docker demo**, worker **Demo worker**, workspace `/home/agent/workspaces/demo`, model `opencode/big-pickle`. A native session is created and idle; no task was submitted on the owner's behalf.
- Browser sign-in, interactive dashboard, runtime/model discovery and worker creation were verified. Restore, Release build with warnings as errors, and format verification passed again. The older validation containers remain stopped.

See [local demo access and restart instructions](LOCAL_DEMO.md). Do not stop this new environment as leftover test cleanup; the owner is using it for manual testing.

## Completed implementation

M0–M5 and the initial M6 autonomous routing loop are implemented; no coordinator configuration is required for manual use. The existing .NET 10 / Blazor foundation is retained. SSH + owned tmux + OpenCode HTTP/SSE remains the remote architecture.

- M0: read the entire 849-line handoff and all repository instructions/conventions. Pinned OpenCode 1.18.29, inspected tag commit `16747470f976aca3d362ad730bcd3fe82ecc2c9a`, captured actual `/doc`, and documented all ten compatibility investigations. Dependencies are centrally versioned.
- M1: owner authentication, authorized Blazor/API/SignalR, CSRF/rate limits, mounted secret references, protected persistent keys/data, EF SQLite migrations and single-replica lock. Durable commands/events and a working browser dashboard.
- M2: explicit host-key algorithm/fingerprint, SSH.NET/SFTP, atomic script transfer, checksum-pinned user-scoped installation, owned tmux metadata/process/port checks, bounded failed launches, loopback forwarding, provider discovery and reconnect.
- M3: safe workspace/native session creation, workspace-specific model inspection, live plain-text/tool/usage transcript, tasks/follow-ups, queue order/cancel, native question/permission replies, explicit cancellation and reviewed assignment outcomes.
- M4: transactional UUID/revision/claim handling, per-worker serialization, ambiguous-delivery reconciliation without replay, native snapshots, pending-request recovery, SSE/SSH/browser/backend restart recovery, gap/freshness indicators, bounded event/transcript/audit limits.
- M5: multiple runtimes/workers, durable workspace claims (including identical SSH endpoints across registrations), separate clean-source Git worktrees, runtime/global capacity, dashboard filters, Docker/Compose, exact setup instructions, compatibility and recovery/backup runbooks.

## Validation already passed

- Pinned SDK `dotnet --info`: 10.0.400; Docker daemon: 29.7.2.
- Required solution restore and Release build with `--warnaserror`: passed, zero warnings/errors.
- Final `dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes`: passed. Required restore/build/format were rerun after the last source changes and all passed.
- Combined unit/persistence/SSH/native-provider run: **11 passed, 0 failed, 0 skipped** in 2.3988 minutes. TRX: `tests/HVO.AgentControl.Tests/TestResults/initial-release.trx`.
- Regression run after reply-resolution/SSE-watchdog refinements: **9 passed**, **2 optional native tests skipped** in that invocation, 52.7265 seconds. Coverage includes database rejection, authentication/CSRF, duplicates/revisions, SSE fragmentation, owned/concurrent bootstrap, wrong keys, port/tmux conflicts, prerequisites, shell-safe state/worktree paths, three workers, lost responses, pending replies, cancellation and restart.
- Final targeted regression after canonical bootstrap-path hardening: **5 passed** (all persistence tests plus actual SSH bootstrap/workspace integration), 6.3635 seconds. Includes rejecting a state directory reached through a symlink ancestor before uploading secrets.
- Native installation/schema/session-context tests: actual Linux x64 release installed successfully in a disposable SSH target and repeated Ensure reused it.
- Real provider integration: **three real OpenCode sessions on two SSH targets**, `opencode/big-pickle`, separate workspaces and verified files. SSH disconnected during a running tool; backend restarted; a follow-up preserved the first conversation and no prompt was resubmitted. Native evidence is checked in under `docs/validation/`.
- Native browser rerun: task + follow-up, **two native permission approvals, one native question**, final file verification and reload passed. Final file: `BROWSER_NATIVE_135eeb9668` followed by `FOLLOWUP`.
- Browser fixture smoke: sign-in, runtime registration/bootstrap, workspace model inspection, worker creation, prompt/follow-up queue, question, close/reopen, second browser, safe transcript rendering and desktop/mobile screenshots passed.
- Actual caller-ID probe: resubmitting a native caller message ID produced duplicate text parts. The application deliberately never treats it as an idempotency token.
- Final Docker image built, started as UID/GID 1000 using persisted named volumes, passed readiness and anonymous API 401 checks, and restarted successfully. Existing SQLite database and authentication keys remained available; data directory mode is 700. No public deployment or hosted CI run performed.
- Shell syntax checks and `docker compose config --quiet` passed. The [validation record](validation/initial-release-2026-09-06.md) preserves commands, counts and final image identity.

## Completion and exact next steps

M0–M5 implementation and the available acceptance checks are complete. There are no known failing checks or unfinished independent implementation steps. Further M6 modes and scheduling remain subsequent work; the initial autonomous routing loop is available.

1. Follow [README local setup](../README.md#local-setup): run `./scripts/init-local.sh`, restore/build with SDK 10.0.400, and start on `http://127.0.0.1:5054` with the documented data/secrets environment variables. Sign in with `.secrets/owner-password`.
2. Mount an owner-provided SSH key, independently verify the target's host-key fingerprint, prepare the allowed repository roots and physically canonical bootstrap directory, then register/connect the runtime. Discover a provider/model; perform remote provider sign-in if required.
3. Create a worker in a separate workspace, send a bounded task and follow-up, and review the outcome. For Docker, follow the README Compose instructions and the [operations runbook](OPERATIONS.md).
4. When macOS/arm64 hardware or a production target is supplied, repeat the documented live smoke there and record its actual results. Those environment-specific checks were unavailable in this session; do not infer them from Linux x64 tests.

## Remaining live inputs / limits

- No owner-supplied production SSH host, provider credential, or production deployment was provided or used. Real provider tests ran on two explicitly created disposable Docker SSH targets with advertised anonymous provider access.
- Native macOS and Linux arm64 checks remain unavailable here. Their installation routes/checksums are implemented; Linux x64 is the executed platform.
- Host reboot/container recreation cannot guarantee tool continuation; OpenCode pending question/permission maps are in-memory. SSE has no durable replay; transient history gaps are explicit.
- One backend replica only. Workspace checks are application policy, not an OS sandbox. Use consistent runtime identity; aliases cannot prove physical equivalence.
- Active-turn steering remains disabled; coordination is explicitly started from its own page. Retirement keeps audit/claims; no automatic destructive runtime/workspace removal exists.

## Workspace and test resources

Existing repository files were an untracked baseline; they were preserved. No Git commits or remote repository mutations were made. Disposable containers are `hvo-agentcontrol-fixture-a`, `hvo-agentcontrol-fixture-b`, and `hvo-agentcontrol-package-check`. Native/package/test data and credentials are under ignored `.fixture/`, temporary test directories, and named test volumes. Screenshots: `artifacts/browser/`. Task-created services are stopped after validation; data, images and evidence are retained for review. Restart the SSH fixtures with `./tests/Fixtures/start.sh` and the browser test application using the README commands. The dev container and unrelated services are untouched.
