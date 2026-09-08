# Managed project and worker conventions

Owner-approved design defaults, 2026-09-07. These define the configuration contract for #42/#43/#98; they are not a claim that automatic provisioning or multiple project coordinators are already implemented. Use [project workspaces](PROJECT_WORKSPACES.md), [Dev Container provisioning](DEVCONTAINER_PROVISIONING.md) and [coordination supervision](COORDINATION_SUPERVISION.md) for the lifecycle and control boundaries.

The [2026-09-08 execution environment milestone](EXECUTION_ENVIRONMENT_PLAN.md) refines these defaults: separate host, runtime/environment, worker and task-session identities; one worker and one active task per managed devcontainer; versioned REST for all capabilities; creation/recovery tests before live fleet migration. Its contract takes precedence over earlier examples.

## Identity and grouping

| Record | Initial value or rule | Meaning |
| --- | --- | --- |
| Workgroup | Display name `HVO Development`, slug `hvo-development`, generated immutable UUID | Scheduling/organization group containing projects and eligible execution slots. A name is not a credential or cache trust boundary. |
| Project | Display name `HVO.AgentControl`; provider `github`; repository `RoySalisbury/HVO.AgentControl`; immutable UUID | Repository-qualified task, policy and evidence namespace. Register the owner-selected second repository separately. |
| Project adviser | `HVO.AgentControl planner`, role `Coordinator` | Optional dedicated OpenCode planning conversation. It does not occupy a development slot or implement tasks. |
| Worker | `HVO worker <short-id>`, immutable UUID | Reusable execution slot, eligible for any task its runtime can support. Do not encode permanent reviewer/developer roles in its name. |
| Host | Immutable UUID plus verified machine/provisioner identity | Shared physical capacity and optional Docker access; not a repository or worker slot. |
| Runtime | Immutable UUID, host binding and observed environment/endpoint identity | Existing-machine or managed-devcontainer environment. Initial transport is SSH; one slot per devcontainer by default. Multiple slots require explicit policy and shared capacity accounting. |
| Task | UUID plus project ID and repository-qualified issue/PR reference | One durable assignment through implementation, review and handoff. `#123` alone cannot identify cross-project work. |
| Task session | UUID, task ID, worker ID, native session ID and canonical directory | Fresh for a new task/project; preserved when recovering the same task. Keep previous bindings and history. |
| Provisioning request | UUID, request payload digest and intended worker/runtime IDs | Idempotent lifecycle identity, allocated before remote effects. Identical retries return the existing request. |

Display names and slugs may change without changing ownership, paths or external receipts. Store canonical repository identity separately from display casing and URL aliases. Treat a repository rename as a verified identity update. Do not use labels or model-written names as authorization.

## Paths and configuration precedence

Each provisioner host has an explicit absolute `managedRoot`, verified using the authenticated host connection. For an unprivileged Linux provisioner account, the suggested default is its resolved home plus `.local/share/hvo-agentcontrol`; do not assume `/home/agent`, `/home/vscode` or the controller's own home. Native macOS execution uses an owner-configured writable root such as a directory below the existing development root. Existing personal checkouts are discovery candidates, not implicitly owned disposable workspaces.

Use immutable IDs for owned host paths:

```text
<managedRoot>/projects/<projectId>/sources/<sourceId>/
<managedRoot>/projects/<projectId>/tasks/<taskId>/repo/
<managedRoot>/provisioning/<requestId>/
<managedRoot>/retained/<requestId>/
```

`sources` holds managed source repositories when useful; each task receives a separate clone/worktree and branch. A provisioning request records its actual source workspace path, config path, project/source SHA, and resolved container workspace path. One task's branch/worktree must never become another task's writable workspace. A same-task retry resolves its recorded paths instead of deriving new ones from current display names.

Path precedence is explicit: owner-selected repository config, then that repository's default `.devcontainer/devcontainer.json`, then an explicitly selected central template when the repository has no config. Respect `workspaceFolder`, `workspaceMount`, Features, lifecycle hooks, `containerUser` and `remoteUser` from the resolved configuration. The host bind source must exist on the selected Docker daemon host. Record both the selected values and the observed effective user/path; mismatches block readiness. A central template has its own source revision/digest separate from the project's revision.

For the current explicit lightweight template, the internal path is `/home/agent/workspaces/project` and the user is `agent`. The repository's default development configuration declares `vscode`; it must be built and verified as that configuration, not silently replaced with the lightweight one. The broad Codespaces-style fallback remains planned work. An external runtime's configured parent directory is a workspace base, not proof that a requested repository is present or current.

Canonicalize and validate every managed path against its registered root before remote effects; reject traversal and escaping symlinks. Never interpolate model-provided paths into a shell command. Persist the resolved path and observed filesystem ownership for cleanup decisions.

Until #42 opens a fresh task session in its assigned worktree, assignment guidance uses the verified native session directory as the only task root. It derives per-assignment task and scratch paths beneath that directory and tells workers not to select sibling worktrees, `/tmp`, or other external roots. A genuinely necessary external path remains a one-time, exact-scope owner approval with its task cause; this convention does not automatically grant or remember directory access.

## Container and endpoint identity

Where the selected Dev Container configuration permits naming, use `hvo-ac-worker-<12-character-id>` as a readable container name. A CLI-generated name is also valid and must be recorded rather than renamed behind its configuration. Full immutable ownership labels are authoritative:

```text
hvo.agentcontrol.managed=true
hvo.agentcontrol.workgroup=<workgroupId>
hvo.agentcontrol.project=<projectId>
hvo.agentcontrol.worker=<workerId>
hvo.agentcontrol.provisioning=<requestId>
```

Also record exact container/image IDs, Docker host identity, config digest and CLI version. A short name or prefix match never authorizes deletion. Reconcile uncertain `up` results by full request identity; zero or multiple ambiguous matches must not trigger blind creation/removal.

The Docker host registration defines the controller-reachable publish address and allowed SSH port range. Allocate ports and resources transactionally, verify the published endpoint reaches the recorded container, and pin its SSH host key through the authenticated provisioner. Do not advertise a bridge IP or assume remote `localhost` is reachable. Host address, SSH credentials, port policy and managed root are administrator configuration, not choices an LLM may invent.

## Effective values and readiness

Persist requested, resolved and observed values separately so the UI can explain differences. Required evidence includes:

- Workgroup/project/task/request IDs, selected host, reservations and policy revision.
- Repository remote and source SHA, task branch/worktree, host workspace and native/container directory.
- Dev Container config source/path/digest, CLI version, Features and lifecycle outcomes, image/container IDs, user/UID/GID and architecture.
- Project SDK from `global.json`, git/gh, OpenCode and transport/supervisor-specific prerequisites (SSH/tmux for the initial adapter), plus selected optional tools. Docker capability requires a working declared integration, not merely a Docker executable.
- Scoped GitHub grant and provider readiness references; keep secrets out of prompts, build arguments, images, receipts and source files.
- Native session identity, environment verification time and actual task completion evidence.

An LLM can ask read-only APIs/MCP tools for these observations and propose a worker/model/task. C# validates current policy, provider readiness, project access and shared reservations before atomically dispatching. Recheck environment readiness at task start even after advance preparation. A workgroup/project adviser failure cannot release another task's reservation or stop unrelated projects.

## Cache and retirement policy

Task workspaces and agent session data are not shared dependency caches. Cache scopes use explicit owner/trust/feed policy, host, OS/architecture/toolchain compatibility and a versioned cache policy; a common workgroup name does not imply equal trust. Shared NuGet packages require a shared dedicated NuGet scratch/lock volume. Keep credentials and private-feed access outside shared writable caches.

Default retirement is drain, reconcile, stop/remove the exact owned container, retain checkout/home/SSH artifacts, and release running capacity only after the stop/removal is observed. Active or uncertain commands, admin terminals, unpushed work and unreachable hosts require recorded handling before cleanup. Retained artifacts have their own inventory; explicit purge only removes proven owned artifacts selected by ID. Preserve approved caches and other workers; never use blanket Docker prune for retirement. Revoke/stop managed credential delivery and mark the runtime/worker retired so no new tasks are sent.

## Required live acceptance

Exercise the real API/UI request through official CLI creation, effective-config/user/tool verification, enrollment, a small repository task, drain and owned teardown. Repeat the same request and inject host-service restarts around remote effects to prove stable identities and no duplicate creation. Verify failure cards, unreachable endpoints, a failed lifecycle hook and missing dependencies. Check retained/unpushed work and cache inventory, then create a second fresh worker from cached layers and verify its user/tools/session again. Repeat on another Docker host before claiming multi-host support.

The multi-project acceptance then runs two repositories concurrently, reuses capacity for a fresh task in the other project, provisions when needed, and recovers from a host restart and one unavailable adviser/provider without duplicate work or project access leakage. See issues #43 and #98 for the executable acceptance checklist.
