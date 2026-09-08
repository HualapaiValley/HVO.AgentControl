# Reusable runtimes and project workspaces

Owner clarification, 2026-09-07. This is a proposed extension, not implemented automatic provisioning.

See [managed project and worker conventions](PROVISIONING_CONVENTIONS.md) for the initial `HVO Development` workgroup, immutable project/task identities, managed host paths, container configuration precedence and required multi-project lifecycle verification.

## Current behavior

A runtime is the reusable SSH execution environment and managed OpenCode server. A worker binds one persistent native conversation to one canonical directory. Its `Project` string is a label, not a repository registration or scheduler policy. Coordination selects existing worker IDs; it does not select a bare runtime and provision a project session.

Worker creation accepts an existing directory, or creates a Git worktree from an existing local repository with a new branch and base ref. The `Repository` input is a local source directory, not a clone URL. Provisioning does not automatically clone missing repositories. A model can run shell commands in child directories, but that does not create separate conversations or reliably establish project-specific context.

The Home M4 worker currently uses `/Users/roys/Development/_github/RoySalisbury`. The beta workers use individual HVO.AgentControl checkouts. These are provisioning choices, not a requirement for one machine or container per repository.

## Intended model

- **Runtime:** reusable host/container, capabilities, credentials references, allowed workspace roots and execution capacity.
- **Project:** repository identity/remote, base branch, setup instructions and required capabilities.
- **Workspace:** project checkout or isolated worktree on a runtime, with exclusive ownership while in use.
- **Worker session:** a fresh native conversation for each new task, bound to its project and workspace. Reconnection or recovery resumes that same task session; subsequent tasks get new sessions, including tasks for the same repository.
- **Coordinator:** routing conversation with explicit project/work-item metadata, independent of a development checkout. It can monitor multiple repositories and request preparation by an execution worker ahead of an assignment.

Every task must carry a durable project ID that resolves to its repository identity, plus any issue/PR reference, requested branch/ref and dependencies. Repository identity must accompany external references: issue or PR numbers alone are ambiguous across repositories. Preserve this identity through dispatch, progress, results and handoffs. A cross-repository objective has separate project-scoped tasks with explicit dependencies; do not infer their destination from the preceding task or a worker's last directory. Resolve ambiguous destinations before executing repository operations.

An assignment should select an eligible runtime, reserve capacity and a workspace, and create a fresh task session whose first phase verifies and prepares its environment. The execution worker performs clone/fetch/worktree setup, repository-specific initialization and dependency checks within its configured permissions. AgentControl owns durable task/session identity, workspace reservations and dispatch guards; the coordinator routes preparation instructions and interprets results.

Because the current native session API requires an existing directory, implementation must provide an owned preparation directory or a separate preparation session before opening the task session in its final workspace. Do not silently change a live session's directory. A preparation session is distinct from the fresh task session and must release its claim before ownership transfers. This bootstrap choice remains to be implemented.

At task startup, verify the actual remote/repository identity, workspace ownership and dirty state, requested branch/ref, applicable repository instructions, toolchain/dependencies, required access and current resource availability. Record the observed revision and verification result. Show durable preparing/verifying/ready/running/blocked/failed states throughout. Missing access or dependencies must be visible and block task execution. Retry must reconcile partially created checkouts and sessions instead of duplicating them.

The coordinator may dispatch an advance preparation task to initialize or refresh a repository on an eligible runtime. That work is performed by an execution worker, and its result is scoped to the project, runtime, workspace and observed revision/time. The eventual task session still verifies its environment: preparation can become stale and does not establish permanent readiness. Refresh must preserve active work and local modifications. Fresh sessions can reuse suitable checkouts and dependency caches; they do not require a fresh clone every time.

On M4, the configured parent can remain the allowed root for HVO.SkyMonitor, HVO.RoofControl and HVO.AgentControl workspaces. Existing owner checkouts must not be reset, cleaned or switched underneath other work. Concurrent assignments need separate worktrees and explicit capacity limits. Repository setup instructions execute within configured permissions; project enrollment does not grant additional credentials.

## Delivery and acceptance

1. Add durable project registrations and runtime eligibility; distinguish host capacity from existing conversations in the UI.
2. Add persisted, recoverable worker-executed preparation, fresh sessions per task, and mandatory environment verification before task execution, retaining existing directory-bound workers during migration.
3. Enforce workspace ownership, project/session boundaries and resource reservations across concurrent assignments.
4. Verify consecutive Repo1 and Repo2 tasks on M4 have correct repository identities and fresh conversations; another Repo1 task also gets a fresh conversation while recovery of the original task preserves its session.
5. Verify advance preparation still triggers task-start checks, wrong remotes and stale/dirty workspaces block unsafe execution, cross-repository issue numbers remain unambiguous, and interrupted preparation recovers without duplicate sessions or workspace ownership.

Until this exists, explicitly prepare each repository/worktree and create a worker for that directory on the same runtime, then select those workers for coordination. Selecting the parent-directory worker does not automatically enroll or prepare its child repositories.
