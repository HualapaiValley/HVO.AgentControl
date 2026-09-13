# HVO Distributed Worker Checkpoint Research Brief

## Purpose

Research how to add **Git-based task/turn checkpoints** to the HVO distributed worker architecture.

The goal is to borrow the useful checkpoint concepts from T3 Code without adopting assumptions that only work when all workers share one host or one filesystem.

This is a **research task**, not an instruction to immediately implement T3's design.

---

## Existing HVO Worker Model

The architecture currently assumes:

- Multiple physical or virtual **Worker Hosts** across the network.
- Each Worker Host can execute one or more transient workers.
- A **Worker** is created for one task.
- **One worker = one task = one devcontainer**.
- The task repository/workspace is local to the Worker Host executing that task.
- The coding agent (initially OpenCode, potentially other providers later) runs inside the worker's devcontainer.
- Workers and containers should be disposable.
- Worker Hosts should not need to share a live filesystem.
- The central HVO orchestrator owns scheduling, task state, lifecycle, events, and recovery coordination.

Conceptually:

```text
                         HVO Orchestrator
                               |
              +----------------+----------------+
              |                |                |
              v                v                v
        Worker Host A    Worker Host B    Worker Host C
              |                |                |
          Worker 1         Worker 2         Worker 3
              |                |                |
        Devcontainer      Devcontainer      Devcontainer
              |                |                |
          OpenCode           OpenCode           OpenCode
```

The design must continue to support workers spread across multiple Docker hosts.

---

## T3 Code Concept To Research

T3 Code uses Git as an internal checkpoint/state mechanism.

Instead of creating visible commits on the user's branch, T3 creates Git objects and keeps them reachable using private refs outside the normal branch namespace.

Conceptually:

```text
refs/heads/main

refs/t3/checkpoints/<...>
```

For HVO, an equivalent namespace might be:

```text
refs/hvo/checkpoints/<task-id>/<turn-id>/before
refs/hvo/checkpoints/<task-id>/<turn-id>/after
```

These refs should not normally appear in the user's branch history or be pushed to the normal upstream repository.

Research T3's current implementation, including:

- `CheckpointStore`
- `CheckpointReactor`
- `CheckpointDiffQuery`
- `VcsCheckpointOps`
- how it creates checkpoint commits without altering normal branch history
- how it avoids disturbing the user's Git index/staging area
- how untracked, modified, deleted, and staged files are represented
- how restore/revert works
- how per-turn diffs are calculated
- how checkpoints interact with provider/session rollback

Useful T3 repository:

```text
https://github.com/pingdotgg/t3code
```

Start from the orchestration/VCS/checkpoint code rather than relying only on user documentation.

---

## Important Difference From T3

Do **not** assume all HVO workers share one repository object database or one filesystem.

A design such as:

```text
Single Host
   |
Shared Git object database
   |
   +-- Worktree A --> Container A
   +-- Worktree B --> Container B
   +-- Worktree C --> Container C
```

may be useful inside one host, but it is not sufficient for HVO.

HVO must support:

```text
Host A                          Host B
  |                               |
local repository                 local repository
  |                               |
Worker A                         Worker B
  |                               |
Container A                      Container B
```

There must be no requirement for NFS, SMB, or another shared live filesystem between Worker Hosts.

---

## Proposed Direction To Evaluate

### Local Git Cache/Mirror Per Worker Host

Each Worker Host may maintain local bare mirrors/cache repositories:

```text
/var/lib/hvo/repos/
    ProjectA.git
    ProjectB.git
```

Each task then gets its own local working directory/worktree:

```text
/var/lib/hvo/workers/<task-id>/
    workspace/
    state/
    logs/
```

A typical lifecycle would be:

```text
Task scheduled
      |
      v
Choose Worker Host
      |
      v
Update local Git mirror/cache
      |
      v
Create task workspace / branch
      |
      v
Start devcontainer
      |
      v
Run agent inside /workspace
```

Research whether Git worktrees, local clones, alternates, reference repositories, or another mechanism is the best fit.

The priority is:

1. isolation
2. reliability
3. cheap workspace creation
4. easy cleanup
5. easy recovery

Avoid premature optimization if simple local clones are safer.

---

## Checkpoint Ownership

The coding agent should **not** own checkpoint creation.

Preferred separation:

```text
                 HVO Orchestrator
                       |
          +------------+------------+
          |            |            |
     Worker Mgmt    Git Mgmt      State Store
          |            |
          v            v
    Devcontainer    Checkpoints
          |
          v
       OpenCode
```

For a turn:

```text
1. HVO receives task/turn.
2. HVO captures BEFORE checkpoint.
3. HVO instructs the agent runtime to execute.
4. Agent modifies files inside its worker workspace.
5. HVO observes turn completion.
6. HVO captures AFTER checkpoint.
7. HVO calculates BEFORE..AFTER diff.
8. HVO persists turn/checkpoint metadata.
```

The agent should not need to know that checkpointing exists.

---

## Distributed Recovery Problem

Local checkpoints alone are insufficient.

Example:

```text
Host B
  |
Task abc123
  |
15 local checkpoints
  |
HOST FAILURE
```

If checkpoint Git objects and refs existed only on Host B, HVO may lose the task's recoverable filesystem state.

Research a replication mechanism.

One possible model:

```text
                   Canonical Git Remote
                    GitHub / Azure DevOps
                            |
                   normal user branches
                            |
          +-----------------+-----------------+
          |                                   |
     Worker Host A                       Worker Host B
          |                                   |
      local task                           local task
      checkpoints                          checkpoints
          |                                   |
          +----------------+------------------+
                           |
                           v
                 HVO Git State Store
```

The HVO Git State Store could be a simple internal bare Git service/repository.

Workers could push only HVO-owned namespaces such as:

```text
refs/hvo/checkpoints/<task-id>/*
refs/hvo/tasks/<task-id>
```

These should remain separate from normal branches pushed to GitHub/Azure DevOps.

---

## Recovery / Migration Goal

The architecture should eventually allow:

```text
Task abc123
    |
Host B fails
    |
Orchestrator selects Host D
    |
Host D obtains canonical repo
    |
Fetch HVO task/checkpoint refs
    |
Restore latest stable checkpoint
    |
Recreate devcontainer
    |
Restart/resume agent execution
```

Filesystem recovery and agent-session recovery are separate problems.

Research them separately.

A provider may or may not support moving its conversation/session state between hosts, but HVO should still be capable of restoring:

- source tree state
- task branch
- checkpoint history
- per-turn diffs
- task metadata
- execution logs/events where available

---

## Worker Host vs Worker

Preserve this distinction.

### Worker Host

A relatively persistent machine capable of executing workers.

Example metadata:

```text
Host: hvo-dev-02
OS: Linux
Architecture: x64
CPU: 16
RAM: 64 GB
Docker: yes

Capabilities:
- dotnet-10
- node
- docker
- gpu
- arm64
- macos
- xcode
```

### Worker

A transient execution boundary for one task.

Example:

```text
Worker: worker-4839
Task: task-872
Host: hvo-dev-02
Workspace: /var/lib/hvo/workers/4839/workspace
Container: hvo-worker-4839
Agent: OpenCode
State: Running
```

When the task is complete, the worker may be destroyed.

---

## Questions To Answer

Produce concrete recommendations for each of these.

### 1. Checkpoint Creation

- How does T3 create hidden Git checkpoint commits?
- Does it use a temporary Git index via `GIT_INDEX_FILE`?
- Which Git plumbing commands/APIs are involved?
- Does it correctly capture staged, unstaged, deleted, and untracked files?
- How are ignored files handled?
- Are large/binary files problematic?
- What happens with Git LFS?

### 2. Checkpoint Restore

- How does T3 restore a checkpoint?
- How does it avoid corrupting the normal Git index?
- What happens to untracked files created after the checkpoint?
- Can restore safely be performed when the agent/container is still running?
- What locking/quiescing is required?

### 3. Checkpoint Namespace

Recommend an HVO ref layout.

Candidate:

```text
refs/hvo/checkpoints/<task-id>/<turn-id>/before
refs/hvo/checkpoints/<task-id>/<turn-id>/after
```

Consider whether refs should instead use checkpoint IDs rather than turn IDs.

### 4. Per-Turn Diff

Determine the most reliable way to calculate:

```text
before-checkpoint..after-checkpoint
```

and expose:

- files changed
- additions/deletions
- binary changes
- renamed files
- complete patch/diff

### 5. Host-Local Repository Strategy

Compare:

- full clone per worker
- local mirror + clone
- local mirror + Git worktree
- Git alternates/reference repository
- partial clone
- other appropriate approaches

Prioritize correctness and isolation over minor disk savings.

### 6. Multi-Host Checkpoint Replication

Determine whether an internal bare Git repository is a good HVO checkpoint store.

Address:

- when checkpoints should replicate
- whether BEFORE and AFTER checkpoints should both replicate
- batching vs every-turn replication
- failure handling
- namespace collisions
- concurrent tasks
- garbage collection
- retention
- authentication
- encryption/security
- repository corruption/recovery

### 7. Canonical Remote Separation

The user's normal remote should remain normal:

```text
origin -> GitHub / Azure DevOps
```

Determine whether HVO should configure a second remote:

```text
origin       -> canonical repository
hvo-state    -> internal HVO checkpoint repository
```

Evaluate this model.

### 8. Task Migration

Document exactly what is necessary to recreate a worker on another host.

Separate:

- repository state
- checkpoint state
- devcontainer configuration
- secrets/environment
- MCP configuration
- agent-provider session
- orchestration history
- logs/artifacts

Identify which of these can be reproduced deterministically versus which require provider-specific persistence.

### 9. Concurrency

Research Git locking/contention when:

- multiple workers exist on one host
- multiple worktrees share one object database
- checkpoint operations happen concurrently
- fetch/push/GC happen concurrently
- tasks use the same repository on different hosts

Recommend what must be serialized at:

- repository level
- worker level
- task level

### 10. Cleanup / Retention

Checkpoint refs will prevent Git objects from being garbage collected.

Recommend lifecycle rules such as:

```text
active task     -> preserve all checkpoints
completed task  -> preserve for N days
merged task     -> preserve reduced checkpoint set
expired task    -> delete refs and allow Git GC
```

Do not choose arbitrary retention periods without explaining the tradeoffs.

---

## Explicit Non-Goals

Do not redesign HVO around:

- one shared Docker host
- a shared NFS/SMB workspace
- one global OpenCode process for every task
- GitHub as the only place where transient HVO checkpoints can exist
- visible checkpoint commits on user branches

Do not assume OpenCode-specific behavior belongs in the orchestration core.

The checkpoint mechanism should be usable regardless of whether the worker runs:

- OpenCode
- Codex
- Claude Code
- another future coding-agent provider

---

## Desired Deliverable

Return a design/research document containing:

1. How T3's checkpoint architecture actually works.
2. Which pieces are worth borrowing.
3. Which T3 assumptions do not apply to HVO.
4. Recommended HVO checkpoint architecture.
5. Proposed Git ref naming scheme.
6. Worker-host local repository strategy.
7. Checkpoint replication strategy.
8. Host-failure recovery flow.
9. Task migration flow.
10. Concurrency and locking requirements.
11. Cleanup/retention strategy.
12. Security concerns.
13. Open questions or risks.
14. A recommended implementation sequence.

Include diagrams where useful.

Where conclusions depend on T3 behavior, cite the exact source files/functions from the current T3 Code repository.

Do not begin implementation until the research/design recommendation is complete.
