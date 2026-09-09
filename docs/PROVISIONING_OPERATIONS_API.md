# Provisioning operations API

This is the durable-operation and outbound executor canary slice of #43. It records owner intent before host work, selects an authenticated executor incarnation, binds a real physical reservation, and atomically joins that permit to `ProvisionEffects`. The web process does not register the runner or receive Docker access.

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

The separate Linux executor uses authenticated machine routes for authority, claim, effect, progress and result delivery. Owner capacity binding accepts only a matching held `HostResourceReservation`; arbitrary capacity numbers are not an HTTP authority. The direct database ledger remains a test fixture and is not registered by the web process.

## Persistence and recovery

`ProvisionOperations` stores immutable requested identity, separately approved intent/capacity evidence, state and revision. Full approved intent is immutable: an identical trusted approval is an idempotent replay, while any changed approval conflicts. `ProvisionAttempts` binds the durable admission to that intent, its authority revision, the exact capacity generation and the canonical approved host directory. Effect start atomically revalidates that the admission is live and that its unchanged capacity grant remains current. `ProvisionEffects` commits one exact effect/resource identity before returning permission to the runner. The unique host/canonical-directory admission remains held after disposal and across restart for reconciliation; an uncertain or disconnected result never authorizes a second `up` under another operation or workspace UUID.

Runner reconstruction and web restart read the committed effect before checking vanished CLI/source inputs. An identical operation/intent can observe but cannot repeat `up`; changed intent is rejected. Removal remains rejected until separate drained retirement authority and retained-state disposition are modeled.

The owner response exposes bounded stage markers and exact ownership IDs/effect receipts. Raw CLI output and resolved configuration are deliberately not persisted in the operation result because configuration/output may contain secrets. Detailed native diagnostics remain host-side evidence and must gain a separately designed protected retention contract before exposure.

## Remaining acceptance

Still required before beta replacement: production installation/outbox discovery, fresh checkout preparation, Docker builder hard limits, release reconciliation, a published real pinned-CLI/lost-response canary, verified SSH/OpenCode transport, credential/provider/GitHub grants, stable runtime/slot enrollment, task/restart/reconnect, and owned drain/removal with retained-state handling. A successful HTTP acknowledgement, committed Docker effect, or `AwaitingEnrollment` container is not readiness evidence.
