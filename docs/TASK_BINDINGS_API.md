# Worker slot and task binding API

This is the bounded reusable-slot/task binding slice of #121. It is additive and local to the control database. It does not provision a container, inspect a host, create a native session, move a live worker, or prove physical placement.

## Identity and compatibility

- `RuntimeEnvironmentRecord` remains the explicit environment association. A new task binding requires that the runtime has an environment record, including an explicit `LegacySsh` record when the owner intentionally chooses that compatibility mode.
- `WorkerSlotRecord` is a reusable logical slot belonging to one runtime. It is not a `WorkerRecord`, native conversation, capacity reservation, or host placement claim. A managed devcontainer configuration permits one slot or one existing legacy worker registration.
- `ProjectRecord` is the canonical repository identity. A task binding validates that the existing `WorkItem.Repository` canonicalizes to the selected project and that its branch matches the new workspace binding.
- `TaskWorkspaceRecord` is an exclusive task checkout identity. Active bindings cannot share a runtime directory, worker slot, work item, or workspace. A released directory may be used again only through a new workspace identity and new task binding request.
- `TaskSessionBindingRecord` is an explicit native-session association. New bindings begin `Unbound`; this API never creates a native session. A legacy session can be attached only when the caller supplies both the existing worker ID and its exact recorded native session ID, matching worker role, runtime/managed-server identity, directory and branch. Coordinator conversations cannot be attached to development task bindings. No legacy rows are backfilled.
- `TaskBindingRecord` joins the task, project, runtime, reusable slot, workspace, and session binding. `placementVerified` is always `false`; requested host/configuration metadata is not evidence of actual placement or lifecycle authority.

Existing `RuntimeRecord`, `WorkerRecord`, `CommandRecord`, native session IDs, transcript history, credentials and active assignments are not rewritten. Existing WorkItems retain their current owner-worker authority; binding a task to a reusable slot does not transfer that ownership.

## Routes

All routes use `/api/v1`, owner authentication, and the existing CSRF exchange for mutations. JSON request DTOs reject unknown fields.

| Method | Route | Behavior |
| --- | --- | --- |
| `GET` | `/worker-slots?after=0&take=50&includeArchived=false` | Stable sequence pagination of reusable slots. |
| `GET` | `/worker-slots/{id}` | Slot detail. |
| `POST` | `/worker-slots` | Idempotently create a slot for an existing runtime. |
| `POST` | `/worker-slots/{id}/archive` | CAS/idempotently archive or restore a slot; active task bindings must be released before archiving. |
| `GET` | `/task-bindings?after=0&take=50&includeReleased=false` | Stable sequence pagination of task binding views. |
| `GET` | `/task-bindings/{id}` | Binding, workspace, session, project and slot detail. |
| `POST` | `/task-bindings` | Atomically reserve a fresh workspace/session binding for an existing WorkItem. This has no remote side effect. |
| `POST` | `/task-bindings/{id}/release` | CAS/idempotently release the binding, workspace and session association. It does not stop or move native work. |

Mutations use `requestId` and durable inventory receipts. Replaying the same request returns its original committed response after restart; reusing a request ID with a different resource, action or intent returns `idempotency_conflict`. Task creation requires current WorkItem, Project and WorkerSlot revisions. Active uniqueness constraints are enforced transactionally for each task, slot, workspace and runtime directory. The returned `nextAfter` cursor is the last returned sequence, so page boundaries do not skip or duplicate rows. Existing worker/runtime deletion paths reject active new bindings with actionable errors; released binding history is retained without making deletion fail through a database FK.

## Deferred capabilities

Provisioning, Dev Container CLI operations, verified host/container evidence, resource reservations, scheduling, live-session movement, native session creation, UI flows, retirement automation and fleet migration remain #6/#42/#43 work. A successful binding is an ownership/configuration reservation only and is not proof that the requested checkout or native session exists remotely.
