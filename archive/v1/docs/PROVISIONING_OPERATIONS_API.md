# Provisioning operations API

This is the bounded durable-operation slice of #43. It records owner intent before host work and implements the SQLite-backed `IProvisionAttemptLedger` required by `LocalDevContainerRunner`. It does not register that runner, a host executor, Docker access, capacity service, enrollment workflow, retirement authority or fleet migration.

## Owner routes

All routes are below `/api/v1/provisioning/operations`, require owner cookie authentication, and require the existing `X-CSRF-TOKEN` header for writes.

| Method | Route | Result |
| --- | --- | --- |
| `POST` | `/` | Idempotently creates an operation and returns HTTP 202 with its durable view and `Location`. |
| `GET` | `?after=0&take=50` | Returns a sequence-cursor page; `take` is 1-100. |
| `GET` | `/{operationId}` | Returns one durable operation view. |
| `POST` | `/{operationId}/cancel` | Requires `expectedRevision`; records cancellation without claiming a remote effect stopped. |
| `POST` | `/{operationId}/reconcile` | Requires `expectedRevision`; revokes unconsumed capacity or records owner demand to reconcile a committed effect, without granting host access. |

The create body binds the UUID to the selected host, runtime environment and project revisions, workspace UUID, source SHA, repository-relative `devcontainer.json` path, configuration SHA-256, cold-build choice, and requested build/runtime CPU and memory. The referenced host, runtime, managed-devcontainer environment and project inventory must already exist; this slice does not create that prerequisite inventory. Unknown fields are rejected. In particular, callers cannot submit executable paths, Docker socket/context, mount authority, credentials, probe commands or a claimed result.

API UUIDs are normalized to lowercase `N` form. The host runner continues to receive the same UUID in `D` form because that is its validated request contract; the ledger compares their parsed UUID identity rather than formatting.

## State boundary

New requests are `AwaitingHostAuthority`. Only internal trusted-host code can bind the complete runner intent and authority revision. The operation then remains `AwaitingCapacity` until a fresh reservation from #6 covers all requested build and runtime CPU/memory. Only that internal evidence can produce `AwaitingExecution`.

Committing an external effect start changes the operation to `Unknown` before process execution. Cancellation before any committed effect becomes `Cancelled`; cancellation afterward remains `Unknown` and permits read-only reconciliation. A verified CLI environment can advance only to `AwaitingEnrollment`. This slice has no `Ready` transition because endpoint, host-key, credential, OpenCode, runtime and worker-slot enrollment evidence does not exist here.

The web process registers the database ledger but not `LocalDevContainerRunner` or an executor. Consequently production requests cannot leave `AwaitingHostAuthority` through these owner routes. This is intentional fail-closed behavior until authenticated executor delivery and the #6 reservation service are implemented and tested.

## Persistence and recovery

`ProvisionOperations` stores immutable requested identity, separately approved intent/capacity evidence, state and revision. Full approved intent is immutable: an identical trusted approval is an idempotent replay, while any changed approval conflicts. `ProvisionAttempts` binds the durable admission to that intent, its authority revision, the exact capacity generation and the canonical approved host directory. Effect start atomically revalidates that the admission is live and that its unchanged capacity grant remains current. `ProvisionEffects` commits one exact effect/resource identity before returning permission to the runner. The unique host/canonical-directory admission remains held after disposal and across restart for reconciliation; an uncertain or disconnected result never authorizes a second `up` under another operation or workspace UUID.

Runner reconstruction and web restart read the committed effect before checking vanished CLI/source inputs. An identical operation/intent can observe but cannot repeat `up`; changed intent is rejected. Removal remains rejected until separate drained retirement authority and retained-state disposition are modeled.

The owner response exposes bounded stage markers and exact ownership IDs/effect receipts. Raw CLI output and resolved configuration are deliberately not persisted in the operation result because configuration/output may contain secrets. Detailed native diagnostics remain host-side evidence and must gain a separately designed protected retention contract before exposure.

## Remaining acceptance

Still required before live provisioning: authenticated executor/outbox delivery, verified host identity and protected roots/tools, #6 physical reservations and release reconciliation, disposable real pinned-CLI restart/lost-response tests, enrollment through verified SSH/OpenCode transport, credential grants, owned drain/removal with retained-state handling, canary task execution and progressive fleet migration. A successful HTTP 202, committed Docker effect or observed container is not readiness evidence.
