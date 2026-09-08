# Worker management and coordination

Updated 2026-09-07. AgentControl routes arbitrary prompts and ordinary responses through a persistent OpenCode coordinator conversation. It does not need a task-specific API for checking the time, reporting memory usage, or reviewing a PR. The coordinator selects recipients and writes prompts; the service persists and dispatches them.

Sessions now have a persisted Worker or Coordinator role. Coordinators appear on Coordination, are excluded from worker inventory and task capacity, and cannot receive ordinary worker prompts. Run creation and dispatch enforce the distinction. See [capabilities, guidance and terminal operation](AGENT_EXPERIENCE.md) for the new controls and their limits.

## Edit and retire workers

On **Workers**, choose **Edit worker**. Change the name, project, role/description, default model, agent mode, or advertised reasoning variant. **Refresh workspace models** loads choices from that worker's actual workspace. The description helps the coordinator choose workers; it does not replace native agent instructions. From a conversation, **Edit worker / model** opens the same editor.

Saving keeps the runtime, workspace, native session, and history. Active work continues. Instructions capture model/agent/variant at submission: changing defaults affects future submissions, including prompts queued while the worker is busy, but does not rewrite instructions already accepted. The conversation shows the default and the most recently observed assistant model separately.

**Archive worker** requires a fresh idle observation and no queued/in-flight/uncertain commands or pending questions. It hides the worker from the default inventory. **Show archived workers → Restore worker** returns the original conversation. Archiving retains workspace ownership, native history, and audit records. **Details & management → Delete worker** removes its registration and releases the workspace claim after fresh-idle, queue, interaction and coordination checks. Remote files and native history remain. Restore an archived worker to continue its existing conversation, or delete its registration before creating a replacement. Native history/worktree purge is not implemented.

## Questions and approvals

Overview links to all workers needing input. Questions render single/multiple-choice controls and a custom answer where the native request permits one. Tool approvals show the requested tool and patterns, with full details available, and offer **Allow once**, **Allow remembered scope**, or **Reject**. Remembered permission scope is OpenCode's scope, not a newly invented AgentControl policy.

The coordinator may answer task questions from established instructions. Native permission approvals remain with the owner. Pending replies are identified and reconciled across reconnects; a stale or uncertain reply is not blindly repeated. An arbitrary terminal program waiting for stdin is a different interaction and is not automatically a native question.

## Dedicated demo coordinator

The owner has selected a [host-owned OpenCode sidecar](CONTROL_SIDECAR.md) as the replacement architecture (#140). The host now supports direct private-network HTTP/SSE, persistent host/workgroup bindings, independent scheduling and an explicit drained migration. Use Control services to enroll it. The following SSH setup is retained for installations that have not migrated and for historical recovery; do not recreate it after a successful sidecar cutover.

Legacy owner demos use a dedicated `hvo-agentcontrol-coordinator` Docker container, registered runtime **AgentControl coordinator**, and a persistent coordinator session of the same name. Its OpenCode process is owned by tmux, independently of the web container. OpenCode 1.18.29 is bootstrapped by the existing verified installation path. The original bootstrap default was `opencode/big-pickle`; inspect the registered session for its current model. Advertised availability is not a guarantee of inference access.

Recreate the supporting container/key setup, when needed, from the SSH host:

```bash
bash scripts/init-coordinator.sh
```

This creates only missing credentials, starts the Compose `coordinator` profile, preserves its named home/SSH volumes, and writes `.fixture/coordinator-profile.json`. For a fresh deployment, register the generated host/key/secret-reference profile and create a session with role Coordinator in `/home/agent/workspaces/coordinator`. The application uses the existing demo secret directory. The script does not sign in to a model provider or send prompts.

The demo uses Docker's default bridge, like its existing client. If container recreation changes its IP, recheck the generated profile before reconnecting; never silently redirect a registered runtime to an unrelated host. Server-wide stop/restart is distinct from restarting the web UI. Rebooting the coordinator container interrupts active execution, while the home volume retains conversations/configuration. AgentControl ensures the owned server again when it reconnects.

The home volume includes native OpenCode data and provider configuration. Any provider supported and configured in that OpenCode runtime can be used, including an available free model or an Ollama model. Configure provider access in the coordinator runtime, refresh its workspace models, and select the model through Coordination → Edit coordinator and model. For a host-side Ollama service, `host.docker.internal` resolves to the Docker host through the Compose host-gateway entry; that service must actually listen on an address reachable from the container. No Ollama endpoint/model was supplied or tested here, and no worker provider credentials were copied.

## Run a coordination

1. Open **Coordination** and choose the dedicated coordinator session.
2. Select the agents it may contact.
3. Enter an ordinary instruction, such as “Ask Clock agent what time it is, then tell Observer what Clock reported.”
4. Choose the maximum number of coordinator turns and select **Start coordination**.
5. Watch the coordinator's summary, messages, delivery states, and worker responses. Use **Follow-up to coordinator** to answer a blocker or change the instruction; an older unapplied decision is discarded after its native turn ends. Open any conversation for its native transcript or pending approval.

The coordinator conversation is dedicated to routing: decision prompts disable its native execution tools using OpenCode's permission mechanism, which persists on that session. Role selection prevents a coding-worker conversation from being used for decisions. A dedicated idle legacy conversation can be permanently reserved as a coordinator in its editor. The service applies validated routing actions returned by the model; workers receive ordinary prompts without a mandatory response syntax. A prompt may request a particular completion format, such as a PR comment ID.

A real native check completed the time-query/broadcast workflow: Clock ran `date -u`, reported `Mon Sep 7 00:41:35 UTC 2026`, and Observer received and acknowledged that same timestamp. The run used three coordinator turns and two worker prompts. See [recorded evidence](validation/native-coordination-2026-09-07.json).

## Delivery, recovery, and current limits

- One unfinished coordination run at a time; up to 16 selected workers and 1–100 coordinator turns per run. Limits are explicit in the UI. Manual worker control remains available.
- Decisions are asynchronous prompts to the existing coordinator session. The service wakes for worker delivery changes, pending questions, and rate-limited intermediate progress, not every streamed token. No change after a wait decision means no additional inference.
- Outgoing prompts and question replies are committed atomically with the decision. The service checks target membership, worker revisions, question identity, and queue rules. It rejects duplicate recipients in a single batch. Native uncertain delivery blocks further automatic routing.
- Each worker command retains its originating run, native message ID, submitted model settings, and completed response. Workers can respond in prose. Decision JSON is an internal routing envelope for the coordinator; it does not restrict worker task content.
- **Pause** and **Stop** prevent further decisions from dispatching. Already accepted worker instructions remain independent and continue. Use native cancellation explicitly when wanted. **Resume** obtains a fresh decision after a completed invalid/stale decision; ambiguous deliveries must first be reconciled.
- Invalid model output, unsupported actions, stale decisions, excessive context, or exhausted rounds become visible paused states. Model-reported completion is not independent verification of a PR or test result.
- The context includes the 16 latest run commands as plain evidence records: command ID, assignment, state, bounded progress, and the latest text-bearing worker response. Responses are limited to 6,000 characters (beginning and ending retained when oversized); progress is limited to 2,000 and omitted once the command finishes to avoid duplicating its final response. Explicit flags distinguish earlier omitted narration from truncation of the latest response. Full native tool history and responses remain on durable command records. The coordinator also receives bounded intermediate progress and persisted capability reports. It can ask an idle worker for a concise clarification. This implementation does not yet expose an MCP evidence-fetch tool to the coordinator.
- General schedules/deadlines, multiple simultaneous workflows, structured PR-specific acceptance checks, additional Summarize/Assist modes, immediate active-turn steering, and remote history/worktree purge are not implemented. PR labels/reviews/comments can still be requested as arbitrary prompts and interpreted by the coordinator.

Back up the whole AgentControl data directory and the coordinator's persistent home and SSH volumes. A web-controller restart preserves recorded decisions and worker execution; stopping the OpenCode process cannot preserve its in-memory computation. Pending native permissions/questions may need rediscovery or replacement after such a process restart.

See [prototype adoption review](PROTOTYPE_COORDINATION_REVIEW.md) for the proposed transition from GitHub-based multi-harness coordination to service-owned enrollment, scheduling, resource claims and evidence. These extensions are not yet implemented.
