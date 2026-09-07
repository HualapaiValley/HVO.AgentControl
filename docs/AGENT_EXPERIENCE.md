# Agent capabilities, progress and admin terminals

Implemented 2026-09-07, following the initial coordination release.

## Coordinator role

Choose **Coordinator** as the session role during creation, or follow **Coordination → Create a coordinator session**. Coordinators have their own list and model/settings links on Coordination. They reuse durable native conversation transport, but cannot be selected as workers, receive ordinary tasks, or occupy task-worker capacity. Backend validation applies even when requests bypass the UI. Send owner instructions and follow-ups through Coordination; the coordinator conversation displays its native transcript and pending requests.

For a dedicated legacy conversation only, **Edit worker → Reserve as a dedicated coordinator** permanently reserves its role. This requires fresh idle state, no unresolved work/questions, and no unfinished coordination. There is no demotion into a task worker: native coordinator decision prompts persist disabled execution-tool permissions in that conversation. Migration preserves coordinator identities already recorded in run/decision history; it never infers roles from display names. A provisioned but unused coordinator is reserved explicitly by saved identity.

## Capability onboarding

On macOS, AgentControl supplements the non-interactive SSH command PATH with existing standard Homebrew directories: `/opt/homebrew/bin` and `/opt/homebrew/sbin` on Apple Silicon, plus `/usr/local/bin` and `/usr/local/sbin` for Intel installations. Existing PATH precedence is retained. Verification shows detected prerequisite paths, and bootstrap, workspace commands, capability probes and the OpenCode launcher use the same environment. Shell dotfiles are not sourced or modified. See Homebrew's [documented installation prefixes](https://docs.brew.sh/Installation). Already-running remote processes retain their original environment until deliberately restarted; this change does not stop them.

Each runtime connection collects lightweight SSH observations: OS/architecture, CPU type and visible cores, visible memory, container quotas/limits where readable, workspace free disk, and selected installed tools. **Runtimes → Machine capabilities** shows their scope and time. Tool presence is distinct from usable access; unavailable probes remain unknown.

New worker setup enables **Request initial capability report** by default. This queues an ordinary model inquiry asking about effective resources, development tools, Docker daemon access, GPU access, image/video generation versus processing, and iOS editing/build/test/signing access separately. It requests read-only checks and excludes authentication/provider configuration files and environment values, installations, benchmarks, media generation, signing and credential disclosure. Native tool permissions remain part of the normal approval flow.

The API defaults `discoverCapabilities` to false for compatibility; set it explicitly for onboarding. Coordinator creation never submits a capability inquiry. Existing workers have **Capabilities → Refresh capability report**. An inquiry waits behind active work, coalesces with an already pending inquiry, and refreshes the workspace machine observations before model submission. Its latest delivery state remains visible. Reconnect refreshes machine facts without repeatedly inserting model prompts into existing conversations.

Machine observations and the agent's narrative report are persisted separately. The narrative is labeled agent-reported and dated; it is not independent validation of access. The coordinator receives those snapshots in its decision context, using runtime facts when workspace probes are unavailable. It can ask follow-up questions before assigning specialized tasks. Individual normalized claims, targeted refresh after configuration changes, and automatic capability-specific validation remain future work.

## Instruction guidance and progress

Overview offers **Include coordination guidance**, off by default. New coordination runs default to guidance with a five-minute progress request. Zero minutes means milestones only. The supported periodic interval is 1–1440 minutes. Routing actions may override `includeGuidance` and `progressMinutes`; `includeGuidance:false` removes guidance and cadence for a simple question or broadcast.

The `coordination-v1` preamble includes assignment identity, scope/permission guidance, handling of missing capabilities and questions, requested progress, and evidence-based completion. Arbitrary task text and requested completion formats remain valid. Original text, resolved options, exact rendered prompt, origin and model settings are captured at submission. Retries use the same command; editing defaults cannot rewrite queued instructions. Overview exposes **Exact submitted instruction** after submission.

Intermediate assistant text is correlated to the originating command and stored as a bounded excerpt. While work is outstanding, progress-triggered coordinator decisions are limited to the run's interval (one minute when unspecified). This lets the coordinator inform another idle worker before the first task finishes. The backend rejects another automatic task assignment to a busy worker; manual follow-ups still queue normally.

Progress may be incomplete streamed prose. Completion still requires native turn completion and the run's existing checks; neither a progress sentence nor native idle proves task success. The UI shows an overdue report after the requested interval without new assistant text. This is a reporting indicator, not a failure verdict. It neither interrupts a blocking tool nor inserts recurring status prompts. General reminder jobs, precise periodic reports, and persistent incremental digest/evidence cursors remain future work. Text excerpts and inventory remain subject to the overall coordinator context limit.

## Admin terminal

If verification authenticates SSH but reports missing dependencies such as `tmux`, choose **Save for setup**. The profile and encrypted credentials are saved with `SetupRequired` status, without connecting or starting OpenCode. Open its admin terminal to finish setup, then return to **Edit → Verify → Save and set up workers**. Authentication failures and untrusted/changed host keys do not grant this save option. A setup-only verification ticket cannot authorize agent startup; the full verified-connect path still requires successful readiness checks. On an already ready host, Save for setup is also available when you want to save without starting OpenCode.

Choose **Open admin terminal** from a runtime card or agent conversation. It opens a separate tab; select **Open terminal** to connect using that runtime's saved SSH login and pinned host key. OpenCode need not be healthy or connected. The shell starts in the SSH user's home directory, supports interactive input, Ctrl-C and resizing, and is independent of OpenCode's tmux session.

**Close terminal**, page close, connection loss, or login expiry closes this dedicated SSH shell. Reopening starts a new shell; it does not reconnect to the previous console. Deliberately detached/background processes follow normal OS behavior and can outlive the shell. An admin may manually use tmux inside this console when persistence is needed. The application does not automatically attach to or stop the agent's tmux window.

The endpoint requires owner authentication, a same-origin WebSocket and an anti-forgery handshake before opening SSH. Four simultaneous terminals are allowed. Browser heartbeats detect abandoned connections, output/input are bounded, and connection lifetime cannot exceed login expiry. Opening and closing are journaled; shell contents and keystrokes are not stored in the application journal or sent to the coordinator model. Commands have the saved SSH user's OS access.

Terminal assets are bundled locally: xterm.js 6.0.0 and fit addon 0.11.0, with MIT licenses in `wwwroot/vendor/xterm`. They follow the upstream [installation guidance](https://xtermjs.org/docs/guides/download/) and [terminal API](https://xtermjs.org/docs/api/terminal/classes/terminal/); no CDN access is required from the browser. A reverse proxy must support WebSockets on `/api/v1/runtimes/{id}/terminal`, in addition to existing Blazor connections.

## Setup progress, deletion and tool discovery

Worker setup appears immediately in the Workers inventory from its durable command record. Queued/creating, failed, cancelled and uncertain requests survive navigation, refresh and backend restart, even beyond the recent-operations window. Progress describes workspace preparation, model discovery and conversation creation. Successful setup becomes the normal worker card. Failed/cancelled cards support editing a new setup draft or dismissal; command audit remains. Uncertain delivery requires reconciliation before dismissal.

**Delete worker** removes the AgentControl registration and cached conversation, releases its workspace claim, and retains remote files, native conversation and command audit. It requires a fresh idle observation, no queued/uncertain work or pending interaction, and no unfinished coordination. Archived workers count as runtime references. Coordinator registration deletion is available from its editor with the same safeguards. **Delete runtime** requires all worker/coordinator registrations removed, no open admin terminals or unresolved work/claims, and a disconnected transport. Deletion retains remote processes and credentials; stop an owned server explicitly before disconnecting if it should also cease running.

Verification inventories optional `gh`, `az`, `aws`, `gcloud`, `terraform` and `kubectl` tools without blocking connection when absent. They also appear in machine capability probes. Install/configure them through the admin terminal as needed; presence does not establish authentication or permissions.

OpenCode verification distinguishes **Planned** installation from an installed executable. Installation happens during connect/ensure, never during Save for setup. A connected runtime displays the actual executable recorded by bootstrap. New admin terminals include its directory in PATH, so managed OpenCode is available by name without modifying remote shell dotfiles. This applies to AgentControl's terminals; independently opened SSH sessions keep their own PATH.

## UI organization and workspace recovery

The layout now follows the local HVO.SkyMonitor LogicHost reference (commit `a9e87030`): a dark navy surface palette, blue accents, compact horizontal navigation, consistent cards and responsive spacing. This adapts the visual conventions without introducing SkyMonitor's application dependencies.

Overview is a fleet landing page. It no longer chooses an arbitrary conversation. Select a worker card or the conversation picker to open a dedicated conversation view; fleet metrics stay on the landing page. Worker cards prioritize live readiness and the Open conversation / Edit worker actions. Details & management contains capabilities, previous assignment outcomes, archive and delete. Idle is presented as Ready; stale state is Reconnecting; permission and question waits have readable labels. Previous tool errors/completions are not shown as current activity.

Conversation messages and pending questions/approvals stay visible. Tool output, reasoning, long submitted instructions, connection metadata and delivery history are collapsed. Current queued/in-flight/uncertain instructions retain visible controls. Runtime connection metadata and installed tool paths are under Connection details & installed tools. Runtime editing opens above the inventory.

Creating a worker in an already registered workspace identifies its existing owner, including archived registrations and equivalent saved SSH endpoints. The form offers Open existing worker and Edit existing worker before submitting; the backend independently checks the conflict and still resolves canonical paths over SSH. Repeated request IDs retain their original result. Dismissing a failed/cancelled setup card does not delete a worker registration. Prior failed/cancelled attempts for a workspace that already has a worker are grouped in Previous setup attempts, with a link to that worker. No native conversation is deleted or restarted to resolve a UI conflict.
