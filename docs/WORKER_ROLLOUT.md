# Worker rollout and repository admission

Owner-confirmed delivery order, 2026-09-09: finish managed devcontainer provisioning, replace the beta workers, update the UI to represent them clearly, then introduce `RoySalisbury/HVO.RoofControl` alongside `RoySalisbury/HVO.AgentControl`. This is an acceptance contract, not a declaration that the lifecycle or cross-repository dispatch is implemented.

Tracking: [provisioning and replacement #43](https://github.com/RoySalisbury/HVO.AgentControl/issues/43), [task/workspace/session isolation #42](https://github.com/RoySalisbury/HVO.AgentControl/issues/42), [UI #53](https://github.com/RoySalisbury/HVO.AgentControl/issues/53), and [multiple projects #98](https://github.com/RoySalisbury/HVO.AgentControl/issues/98). Use the identity model in [the execution environment plan](EXECUTION_ENVIRONMENT_PLAN.md) and [provisioning conventions](PROVISIONING_CONVENTIONS.md).

## Delivery gates

| Gate | Required evidence before advancing |
| --- | --- |
| 1. Complete a managed worker lifecycle | A real owner UI/API request persists its card before host work; a registered executor uses the official CLI and verifies the selected configuration, user, tools, hooks and resource limits. Enrollment supplies verified transport, OpenCode, repository access and provider readiness. The worker completes an isolated AgentControl task, reconnects after web/container restart, drains, and retires with exact resource ownership and retained-data receipts. |
| 2. Replace the beta workers | Create a replacement through that flow, settle active/unknown commands and unpushed work on one old worker, preserve history and retained artifacts, then drain and transition it. Confirm the new worker accepts work before proceeding to the next. Record actual rollback limits. All intended beta replacements must have lifecycle evidence; an existing manually enrolled CLI container is not evidence for automatic replacement. |
| 3. Deploy the fleet UI | The deployed website identifies workgroups, reusable workers and their current project/task, with clear status and direct conversation selection. Runtime/host details remain accessible. Desktop and mobile checks prove navigation and prompt/terminal destinations remain correct through refresh, selection changes and background provisioning. |
| 4. Admit RoofControl | Register and verify its own repository identity, access, branch/configuration policy and eligible environments. Pass the repository-isolation matrix below against the deployed application, then enable bounded live intake. Start with one small issue and independent review; run both projects concurrently only after the first task's preparation and GitHub receipts are verified. |

Infrastructure work and disposable isolation tests can progress before their live rollout gate. Active assignments finish or reach an explicit preserved checkpoint; changing priorities does not abort native sessions. New eligible assignments prioritize the unmet provisioning/replacement gate and its concrete prerequisites. A worker with no eligible prerequisite may take independent work, with the reason recorded. Production incidents may interrupt the sequence with evidence.

Passing the CLI runner fixture alone is not gate 1: the public flow must reach an enrolled, usable worker and complete retirement. `VerifiedEnvironment`/`AwaitingEnrollment` is an intermediate result. A merged implementation is not a deployed capability. Keep the tracking issues open for their remaining scope.

## Fleet presentation

Use `HVO Development` as the initial workgroup display name and `HVO worker <short-id>` as the reusable worker default. Immutable identities own assignments and history; display names never grant eligibility or access. Existing names remain linked to their historical records during replacement.

The sidebar should present workgroups with their coordinator/adviser and workers/task conversations. Coordinators remain outside development capacity. Show each worker's current repository, task, state and model concisely, with details available for the underlying runtime, Docker host, effective environment, resource use and lifecycle operation. Avoid making each one-worker container another mandatory sidebar level. Provide a project view/filter over the shared fleet without implying that workers are permanently tied to repositories.

Repository-qualified issue/PR links, current workspace and session details must be inspectable from the conversation. Pending/failed provisioning cards survive refresh. Refresh and project filters must preserve the actual selected worker/task or require an explicit new selection; they must not silently redirect an already composed prompt. Historical conversations remain accessible and visibly historical.

This requires real workgroup membership and task identity in the application. Rendering a label or tree does not implement scheduling policy. Broader theme polish follows these operational navigation requirements.

## Repository admission and isolation

A reusable worker may execute either project when its environment and policy allow it. The failure to prevent is assigning a task through the wrong project, workspace, session or GitHub scope. Pin these relationships in durable C# records and revalidate them at proposal admission, session activation and command dispatch. Prompts should repeat the verified context, but cannot establish it.

Each task and handoff needs a project ID resolving to a canonical repository, a repository-qualified issue/PR reference, base/head branch or revision as appropriate, its worker/runtime reservation, owned workspace, and task/native session binding. Provider-side repository identity and verified rename handling should accompany canonical URL normalization. `#123`, a directory basename, a worker's project label, the last active chat, and model-written repository names are not sufficient authority.

New tasks create fresh native sessions even on the same worker and repository. Recovery of the same task preserves the recorded session and effect history, or explicitly records a successor. Reusing an idle environment for the other repository requires fresh compatibility, workspace and access checks; incompatible project configurations require a suitable new environment. Separate project workspaces and histories must not share credentials or arbitrary writable data through dependency caches.

| Scenario | Expected result and proof |
| --- | --- |
| Two repositories both have issue/PR `#123` | Intake, assignment, review, comments and completion retain the repository-qualified identity. Exercise with disposable external-service fixtures, not by inventing production issues. |
| AgentControl task proposed with RoofControl project, workspace, native session or GitHub scope | C# rejects the mismatched tuple before a task prompt, shell action or GitHub effect is delivered; assert zero downstream calls as well as the structured error. |
| Eligible idle worker receives a task for either project | Each valid assignment succeeds after verification; a blanket second-repository rejection does not count as isolation. |
| One worker finishes AgentControl and next receives RoofControl | The old ownership is settled, a distinct owned workspace and fresh session are used, and the observed remote/configuration/access match RoofControl. Repeat a later AgentControl task with another fresh session. |
| Two projects contend for the same idle worker | One atomic reservation wins. The losing proposal receives updated evidence and cannot reuse the winning session or issue reference. Exercise other eligible slots to prove both projects can progress. |
| Project access, worker eligibility or assignment generation changes after planning | Dispatch rechecks current authority. Delayed proposals, replies, tool callbacks and activation results cannot bind to the new task. Project pause holds new work for that project without cancelling another project's active task. |
| Wrong remote, directory alias, stale preparation or incompatible config | Verification blocks task execution with useful status; no fallback to the worker's last directory, inherited repository configuration or ambient GitHub target. |
| GitHub effect selects the wrong owner/repository or lacks the required grant | The service-mediated effect is refused. Worker CLI commands use explicit repository targeting supported by that command and the task's scoped credential delivery. Verify repository-scoped app grants and any owner-managed wider credentials separately; a display filter or prompt is not a credential boundary. |
| Web/executor restart or an unknown dispatch outcome | Recovery uses the persisted project/task/workspace/session and qualified external receipt. It does not repeat a GitHub effect or resume a task in the last selected project. |
| UI selection changes between composing and sending | The draft remains tied to its worker/task/project generation or requires explicit retargeting. Opening another project does not redirect an in-flight send, permission reply or terminal command. |

Repository admission must include inspection of RoofControl's actual `devcontainer.json`, toolchain, instructions, base branch and GitHub App installation access. Do not assume AgentControl's .NET configuration or app grant applies. Record missing access/configuration as an admission blocker rather than silently broadening existing credentials. A failed or paused RoofControl adviser must not prevent AgentControl's eligible tasks from progressing.

## Evidence and validation

For each gate retain the source/deployed revision, operation/task/project/worker IDs, actual configuration and container identities, test commands and counts, relevant GitHub links, and retained-state disposition. Use isolated data and recorded owned resources for destructive lifecycle tests. Include unknown-outcome/replay and rejection cases alongside a successful path.

Follow [the prerelease validation policy](VALIDATION_POLICY.md): workers validate their code; the short automatic `build` and independent current-revision review remain merge requirements; run relevant manual UI/SSH/Dev Container suites for the milestone's actual dependencies. Skipped or unselected suites are not passes. Record which gates remain incomplete when handing off or deploying a partial slice.
