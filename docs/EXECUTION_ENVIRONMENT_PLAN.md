# Project, worker and execution environment milestone

Owner priority, 2026-09-08. This is the implementation contract and delivery order for #121, #6, #42, #43 and #98. It supersedes older configuration and assignment defaults where they conflict. It does not claim that the new records, APIs or automated lifecycle are implemented. Preserve current PR104 and audit108 assignments; production-breaking defects may interrupt this sequence with recorded evidence.

## 1. Separate identities before provisioning

| Concept | Owns | Relationship and lifetime |
| --- | --- | --- |
| Workgroup | Project/worker eligibility and organization policy | Groups projects and slots; never duplicates the physical host's capacity or grants credentials through a display name. |
| Host | Verified machine/VM identity, architecture, available resources and provisioner access | One physical capacity boundary can contain an existing-machine runtime and many Docker runtimes. Docker capability is optional. |
| Runtime / execution environment | Environment kind, host binding, tool/configuration profile, allowed roots, effective limits and lifecycle | `ExistingMachine` or `ManagedDevcontainer`; keep legacy SSH bindings until hosting is verified. Runtime is the product term for the environment, not a repository or task. |
| Worker | Reusable agent slot, harness and default model/profile | Belongs to an environment; selected for any suitable task. It is not permanently an author, reviewer, repository or conversation. |
| Project | Canonical repository identity, access references, base branch and environment/setup policy | Tasks and external issue/PR references always carry project/repository identity. A project can use multiple environments. |
| Workspace | Canonical owned checkout/worktree, branch/ref, dirty state, preparation evidence and retention | Exclusive to its task while writable; separate from dependency caches and arbitrary owner checkouts. |
| Task and phase | Existing WorkItem ownership, objective, dependency and review/handoff evidence | Reuse shipped ownership/phase records; command IDs identify delivery attempts, not the entire durable task. |
| Task session | Worker, project, workspace, native conversation and recovery lineage | A new task gets a fresh native session. Recovery preserves the same task and native session when valid; an unavoidable replacement is an explicitly linked new execution binding. |
| Operation | Requested action, immutable request identity, authority generation, progress and result | Provision, prepare, restart, migrate and retire operations survive controller restarts and uncertain remote responses. |

A dedicated coordinator/adviser is a control-plane role and remains outside development worker capacity. The host's physical budget still includes its actual resource use. A model choice belongs to a task/attempt; it does not define a container image or change an active execution retroactively.

#121 delivers the additive domain/persistence/REST foundation first. Existing WorkItem, enrollment authority and evidence primitives are already present on main; extend their integration. Preserve existing runtime/worker IDs, command/native-message lineage, history and credential references. Current persistent conversations can contain many historical tasks: import an explicit legacy binding rather than inventing a one-task history or changing a live directory. An SSH connection alone does not prove the controller owns the hosting container.

## 2. Default isolation and resource policy

For managed development, use **one devcontainer, one worker slot, one active task**. A Docker host can run several such containers within its shared resource budget. This remains compatible with a Compose-based devcontainer having supporting database or other sidecar containers; account for the entire environment's resources and owned components.

An existing machine may host several workers, each with an isolated task workspace and a resource reservation. Multiple worker slots inside one devcontainer are a later, explicitly enabled advanced policy. They share a kernel resource limit, environment, credential/process exposure and failure domain; extra slots are not free capacity. Agent-spawned subagents and child builds consume their parent's budget and cannot silently acquire another workspace or slot.

Default concurrency is one active task per slot. Compatible idle environments may be kept warm and reused sequentially after prior ownership is settled and environment readiness is rechecked. A fresh task always gets a fresh session and an independently owned workspace. Moving from Repo1 to Repo2 requires checking the new project's config, mounts, tools, credentials and retained data policy. If the environment differs or the trust boundary requires a fresh container/home, provision another environment. Do not silently reuse Repo1's devcontainer configuration or shared HOME for Repo2.

#6 must reserve host and environment resources atomically with task authority: agent baseline plus estimated build/test peak, CPU, memory, disk, ports and explicitly shared build resources. Use effective cgroup limits and host headroom, not host-wide `free` output inside a container or slot counts alone. Reserve provisioning/build capacity as well as steady-state task capacity. Avoid double counting an environment's reserved budget, and account for workspaces/caches retained after its running capacity is released.

The 4 GiB native-process OOMs in #120 demonstrate that a blanket slot allowance is inadequate. Temporary live 6 GiB limits are incident mitigation, not a certified default. Profile cold/warm build, test/browser workload and long conversation memory before establishing profiles. Unknown/stale capacity blocks new admission; it does not release an uncertain owner's reservation. #35 supplies observations; #120 supplies interrupted-process evidence and recovery. Bound automatic restart attempts and escalate recurring pressure to capacity or software investigation.

## 3. Workspaces and optional Git checkpoints

Each simultaneous task uses a different writable checkout and branch. Different projects have separate repository roots. For the same repository, independent task clones are the initial managed-container default. A host-local source/object cache may reduce transfer later, with explicit trust and retention policy. Existing trusted machine runtimes can use managed linked worktrees when their common Git directory is accessible and lifecycle is coordinated. Worktrees have separate working files/indexes but share repository administration and some refs/configuration; they do not provide process, credential or security isolation. [Git worktree documentation](https://git-scm.com/docs/git-worktree)

Do not share task `bin`/`obj`, writable checkouts, private credentials or native session HOME as dependency caches. Follow the scoped NuGet package/lock cache contract in [Dev Container provisioning](DEVCONTAINER_PROVISIONING.md). A worktree mounted in another container also needs correctly mapped Git metadata; independent clones avoid that dependency in the first milestone.

Git checkpoints complement workspace isolation with source-tree recovery and evidence. They are optional follow-up work and do not block the creation/lifecycle milestone. Capture/restore must use existing task ownership, record exclusions and quiescence, preserve unpushed branch heads, and prefer recovery into a new owned workspace. A source checkpoint cannot restore compiler memory, background processes, provider conversation state or external GitHub effects. The research also identified incomplete staging and in-progress merge recovery; never silently claim an exact restore for those cases. See [the checkpoint research brief](research/HVO_Checkpoint_Research_Brief.md); its candidate distributed system is not an implementation dependency.

## 4. Runtime creation and transport

The primary creation flow is **Add runtime → Existing machine (SSH) or Managed devcontainer**. Do not ask the user to enter a fake SSH target before creating a container.

For an existing machine, collect the SSH endpoint/identity, workspace base, permitted use and resource policy; verify platform, PATH, tools and current capabilities. Store a draft with visible validation failures; only verified environments become schedulable. A saved draft is not a ready runtime or permission to execute.

For a managed devcontainer, choose a registered Docker host, project/configuration source or explicit central template, selected `devcontainer.json`, resource profile and credential/provider profiles. Resolve and record requested versus effective user, directory, mounts, Features, lifecycle hooks, image and configuration digest. The project configuration can be selected during preparation; a reusable generic pool template is allowed only with explicit compatibility checks when a task arrives.

Persist the operation card before remote work. Use the official pinned Dev Container CLI on the selected host to resolve/build/start the selected configuration, then inspect its actual container IDs and effective setup. Installing a JSON file in an ordinary Docker image is not provisioning. The CLI exposes configuration, build/up and execution operations; only claim features supported by the installed pinned version. [Official CLI](https://github.com/devcontainers/cli/blob/main/README.md)

Keep three concerns separate: provisioner access to the Docker host; runtime command/file transport; durable native-process supervision. The first implementation uses the existing SSH transport and tmux supervisor inside the approved managed template to reuse verification, terminals and reconnect behavior. Docker lifecycle management remains host-side and must not grant the worker the host Docker socket by default.

Neither SSH nor tmux is fundamental to the domain model. A later Docker exec/attach transport can use Docker access through the registered host, and a container-owned foreground server or another explicitly verified supervisor can replace tmux. `docker exec` alone is not a task recovery or native-process ownership contract. Direct Docker transport must prove startup, environment/user selection, logs/terminal, reconnect after web restart and process-incarnation recovery before replacing the SSH path. Dependency validation is conditional on the selected transport/supervisor; a verified tmux-free adapter must not fail a global tmux prerequisite.

## 5. REST is the public control surface

Every supported administrative and coordination capability must have a documented versioned REST contract. UI, CLI, automation and MCP tools use the same application-service authorization, concurrency and ownership logic. Current `/api/v1` endpoints cover many worker/runtime controls, but the following new contracts are delivery requirements, not existing routes:

| Resource / action | Required API behavior |
| --- | --- |
| Hosts, projects, runtime profiles, workers | Bounded list/detail, create/update with validation and revision checks; separate archive, retirement, deletion and purge semantics. |
| Workspaces and task sessions | Prepare/verify, query ownership and binding, create a new task session, inspect recovery lineage; no silent mutation of a live session's directory. |
| Provisioning and runtime lifecycle | Idempotent requests for provision, drain, restart and retire; return a durable operation ID and current state, normally HTTP 202 for accepted asynchronous work. |
| Operations | Bounded progress/log queries, stage/result/error details, retry/cancellation rules and recoverable unknown outcomes after reconnect. |
| Capacity and environment evidence | Requested/resolved/observed settings, fresh capability/resource observations and reservation state for planning and owner diagnosis. |

Keep current routes compatible while introducing new resources, and publish a route/DTO/implementation-status matrix and OpenAPI documentation or an explicit equivalent until generated coverage exists. Stable structured errors must distinguish validation, conflict, insufficient capacity, unauthorized access and uncertain delivery. Use bounded pagination and event cursors; do not make clients download the entire fleet transcript to inspect status. SignalR/SSE updates supplement durable REST reads.

Browser cookie authentication retains CSRF protection. Headless clients require a designed scoped authentication mechanism and rotation/revocation rather than copied owner cookies; do not claim this already exists. Project/host/operation permissions apply equally to UI and automation. MCP tools are bounded wrappers over the same service contracts, not arbitrary Docker or database access. Add endpoint-level authorization, idempotency, concurrency and restart tests for every capability.

## 6. Automated acceptance before fleet transition

#43 owns the real UI/API lifecycle implementation and disposable end-to-end automation; #13 supplies the broader recovery acceptance matrix. Build tests alongside each slice. A test requiring unavailable fixtures reports a skip/blocker rather than success.

| Scenario | Required evidence |
| --- | --- |
| Existing SSH machine | Save a draft, show failed verification, correct it, enroll and run an isolated project task; platform/PATH and conditional prerequisites are exercised. |
| Cold managed creation | Owner API/UI request creates a durable card; official CLI builds selected config; actual user, SDK/tools, Features/hooks, paths, credentials readiness and limits match recorded evidence. |
| Warm creation and repeat request | Cached build yields a second independent worker; replaying the same request yields its original operation/container, with no duplicate setup. |
| Repository correctness | Concurrent same-repo tasks have different workspaces; sequential Repo1/Repo2 tasks use fresh sessions and verify config/remote/access; incompatible environments require reprovisioning. |
| Capacity and failures | Reject competing claims/insufficient memory, bad config, failed hook, missing dependency, wrong architecture, unreachable host and missing provider access with actionable persistent status. |
| Restart boundaries | Restart web host before/after each Docker/native effect, kill the native child, interrupt a compiler, and restart an owned container. Preserve data/identity and classify partial effects; no blind replay or fabricated completion. |
| Retirement | Drain assignments and terminals; inspect unpushed work; stop/remove only recorded owned resources; retain approved volumes/caches and release capacity after observed cleanup. Purge is separate. |
| Remote Docker host | Repeat a full lifecycle on a second explicitly registered host with real published endpoint and daemon-local paths/caches; no localhost or shared-filesystem assumption. |

Existing fixture/unit/SSH/published desktop/mobile CI remains required. New isolated Docker/Dev Container integration jobs must run on the exact proposed revision with bounded resources and unique ownership labels. Record which live acceptance exercises ran against actual models/hosts. Build caching does not waive lifecycle/user/tool verification.

Only after these gates pass: inventory the legacy fleet; map verified host/environment/project/workspace identities without rewriting active conversations; create one canary worker through the new flow; execute and recover a task; drain and replace one old managed worker; verify retained work/history and rollback; then migrate the rest one at a time. Existing physical machines can remain supported. Restoring the old registration cannot resurrect a killed process, so rollback receipts must state what actually survives.

## Delivery order

1. **#121:** additive identity/configuration/REST contract with legacy compatibility. Root owns the plan; delegate bounded implementation after review.
2. **#6 and remaining #4/#5 integration:** shared capacity and authority using existing foundations. **#42:** owned repository preparation and fresh task sessions with workspace verification.
3. **#43:** type-aware runtime wizard plus REST provisioning, operations, restart/drain/retirement and real automated lifecycle tests. Telemetry/#120 failure classification accompanies this work; multiple slots per container and checkpoint replication are deferred.
4. **#98:** validate multi-project scheduling against these shared capacities and environment contracts. Admit a second owner-selected repository only with explicit project access/configuration.
5. **#43 migration acceptance:** canary, retained-state verification and progressive transition of the existing workers/runtimes.

Review current main and issue scope before each assignment. Do not gate a small ready slice on the full closure of a partially shipped epic; create an explicitly bounded prerequisite when necessary. Current PR/audit owners keep their work and the original commissioned audit model policy. New work uses normal risk/provider/model criteria. Unrelated UI polish, expanded provider catalogs and additional transports follow this milestone unless an actual incident requires immediate repair.
