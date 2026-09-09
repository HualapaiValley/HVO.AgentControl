# Runtime environment configuration API

This second #121 slice associates an existing runtime registration with an owner-registered host and an intended environment type. It builds on [host/project inventory](INVENTORY_API.md) and follows [the execution environment plan](EXECUTION_ENVIRONMENT_PLAN.md). Configuration is stored separately from connection/process observations. No SSH call, clone, Docker action, native-session creation or filesystem change occurs when these endpoints are used.

## Managed runtime drafts

`POST /runtimes/drafts` creates a managed runtime identity and its `ManagedDevcontainer` environment intent in one transaction. The request requires a new runtime UUID, active host and configuration-project UUIDs with their current revisions, and a safe repository-relative `devcontainer.json` path. The resulting runtime uses the internal `ManagedDraft` connection kind and contains no endpoint, username, host key, credential, native session or observed placement. The response is `PendingEnrollment`; it is visible through runtime inventory and the runtime page but is unschedulable.

`GET /runtimes/drafts/{id}` returns the same environment view as the normal runtime environment route. The request receipt is available through `GET /inventory/requests/{requestId}`. Replaying the exact request returns the original runtime/environment view after restart; changed intent, an existing runtime identity or a retired runtime identity conflicts. Host, project and configuration path identify a reusable template and may be shared by separate runtime UUIDs. An unused draft may be removed through the guarded runtime deletion route, but retained provisioning operations or attempts block deletion. A future enrollment transition must atomically replace `ManagedDraft` with an enrolled transport identity only after authenticated ownership evidence, verified placement and an enrollment revision are recorded. This slice does not perform that transition or any remote effect.

## Requested configuration and compatibility

- `ExistingMachine`: the runtime uses an existing machine; current shared-worker/task capacity remains available.
- `ManagedDevcontainer`: the owner intends this runtime to represent a managed devcontainer. This slice enforces one registered worker **or coordinator**, including archived registrations, and task capacity one on this runtime registration. Multiple slots require a later explicit policy.
- `LegacySsh`: no explicit association, including after reset. Existing SSH registrations start here without a migration backfill. Their IDs, workers, native conversations, commands, credentials and capacity remain intact.

`requestedHostId` identifies the owner-declared host inventory record. It is not inferred from SSH address, labels, directory or Docker hostname. Every response has `placementVerified: false`: neither an association nor the `ManagedDevcontainer` kind proves container ownership, physical placement, available memory, provisioner credentials or lifecycle authority. Duplicate SSH aliases cannot yet be proven to share a container/physical host. The one-registration limit does not constitute physical capacity admission across aliases. Verified placement, resource reservations and provisioning must precede automatic fleet migration or scheduling from this metadata.

A managed configuration may optionally supply both `configurationProjectId` and `devcontainerPath`. The project identifies the repository containing the intended configuration, independently of the repository of any future task. This does not pin a worker permanently to that repository or imply that a configuration has been downloaded, validated or built. Paths are literal repository-relative paths ending in `devcontainer.json`, or root `.devcontainer.json`, at most 1,024 characters; absolute paths, parent/dot traversal, empty segments, backslashes, colons and control characters are rejected. A resolved commit, configuration digest, actual container ID/user/features/hooks and requested/resolved/observed provisioning evidence remain future operation results. Omitting both fields leaves the configuration source unselected. Existing-machine configurations cannot specify this devcontainer source.

## Routes and representation

All routes have prefix `/api/v1` and share `ControlStore` with UI/coordinator consumers. Owner authentication is required; mutations use the existing `X-CSRF-TOKEN` exchange. Unknown JSON fields are rejected, including caller-supplied verification or observation fields.

| Method and route | Input and response |
| --- | --- |
| `GET /runtimes/{id}/environment` | Current `RuntimeEnvironmentView`, including a legacy view with revision 0 when never configured; 404 for a missing runtime |
| `PUT /runtimes/{id}/environment` | `ConfigureRuntimeEnvironmentInput`; synchronous 200 with original committed view |
| `POST /runtimes/{id}/environment/reset` | `ResetRuntimeEnvironmentInput`; synchronous 200 with original committed legacy view |
| `GET /hosts/{id}/runtimes?take=50&after=...` | Bounded `RuntimeEnvironmentPage` of owner-declared associations, including changed connection profiles; 404 for a missing host |
| `GET /inventory/requests/{requestId}` | Existing durable request-receipt lookup; resource kind `RuntimeEnvironment` and action `Configure` or `Reset` |

The view includes `runtimeId`, `runtimeName`, `runtimeRevision`, `revision`, `requestedHostId`, `requestedKind`, `configurationProjectId`, `devcontainerPath`, `state`, nullable `maxWorkerRegistrations`, `activeTaskCapacity`, `placementVerified`, and nullable `createdAt`/`updatedAt` UTC Unix milliseconds. Null `maxWorkerRegistrations` means this slice adds no per-runtime registration limit; existing global limits still apply. `activeTaskCapacity` reports configured concurrency, not measured physical resource availability.

`state` is `LegacySsh`, `Configured`, `ConnectionProfileChanged` or `PendingEnrollment`. On configuration, the service stores an internal fingerprint of the current SSH/server/startup profile. A later connection-profile edit makes the association visibly stale. Name/labels, health, reconnect generation and model catalogs do not affect that fingerprint. It contains no secret values and is not exposed in the public view. Even a matching fingerprint is only a match to requested profile fields, not remote placement verification. The managed registration/capacity restriction remains in force when the profile changes.

Host runtime pages use a **runtime ID cursor**, unlike the numeric sequence cursor on `/hosts` and `/projects`. Runtime IDs and these cursors must use the exact 32-character N-format key returned by existing runtime inventory, preserving its case. Legacy registration permits uppercase/mixed-case and all-zero GUID keys; these remain distinct stored identities and are not normalized or rewritten by this API. Host/project/request UUIDs still follow inventory normalization. `take` is 1–100; default 50. Continue with the returned non-null `nextAfter` using the same host. Null means no further rows at that read. Pages are live membership reads in database ordinal ID order, not a snapshot: restart enumeration to see newly associated runtimes ordered behind the cursor. Configuration changes are also signaled through existing durable inventory events.

Example configuration after disconnecting the runtime and fetching its current environment view:

```json
{
  "requestId": "39b87662-8352-4a7d-9091-2bf06a8a3ad8",
  "expectedRevision": 0,
  "expectedRuntimeRevision": 5,
  "hostId": "5d380bae-e7a3-4143-ad82-30a21547cfd2",
  "kind": "ManagedDevcontainer",
  "configurationProjectId": "a57e4d9b-495c-46a3-98f4-69227f66df24",
  "devcontainerPath": ".devcontainer/devcontainer.json"
}
```

Reset input contains only a new `requestId`, `expectedRevision` and `expectedRuntimeRevision`. Reset clears the host/configuration source and changes kind to `LegacySsh`. It retains a configuration tombstone with an incremented revision; it never returns to revision 0 or silently raises capacity. Every accepted configure/reset increments both configuration and runtime revisions. Reconfiguring an existing managed registration therefore requires an explicit current revision; a stale legacy edit cannot quietly change its capacity.

## Guards and retries

Both mutations require explicit disconnection (`desiredConnected=false`, transport `Disconnected`), no active admin terminal, no unresolved runtime operations or unowned setup claim, no pending/uncertain worker request, no unreleased/unabandoned work-item ownership, and no unfinished coordination referencing the runtime's workers/coordinator. Disconnect does not terminate remote work or resolve its uncertain delivery. These guards run with the mutation under the same transaction/write gate as existing command admission.

The host and any configuration project must exist and be unarchived. Host archive rejects referenced environment associations; project archive rejects references as a runtime's configuration source. Reset/reassign those references first. Archive never drains or stops anything. Future project/task/workspace references must add their own archive guards when introduced.

Managed configuration rejects more than one existing worker/coordinator, including archived workers. Existing common worker-creation admission reserves the single slot as soon as a creation command is queued. A registered worker, pending/uncertain create, or retained unowned setup claim blocks another request even with a different workspace. An identical request retrieves its existing command before checking the slot; it cannot create a second worker. A failed/cancelled setup only frees the slot when its remaining claim has been released through existing recovery/dismissal controls. Worker deletion still uses existing ownership/uncertainty safeguards. Saving runtime settings through any existing save/setup/connect path cannot increase managed task capacity above one.

Configure/reset use the inventory's durable successful-request receipts and normalized request-intent hashes. The mutation, both revisions, original response, and event commit together. The same request ID/payload returns the original response after later edits, reset, controller restart, or runtime deletion, without applying the change again. Replay may report older revisions; fetch current detail before editing. Reusing a request ID with different intent conflicts. Failed requests roll back without reserving an ID. Host/project/request IDs are non-empty UUIDs normalized to lowercase N format; runtime resource identity retains the legacy key as described above.

Deleting a runtime through the existing guarded registration-deletion API removes its current configuration row through a foreign key. The mutation receipts remain; replay cannot resurrect the runtime or its association. Host/project foreign keys restrict deleting referenced inventory records. The migration adds a configuration table and alternate identity constraints on host/project inventory; it preserves their sequence cursors and history.

Errors use the inventory `{error, code}` shape: `validation` (400), `not_found` (404), and `revision_conflict`, `idempotency_conflict`, `runtime_connected`, `runtime_in_use`, `resource_archived`, `resource_in_use`, `worker_limit` (409). Existing worker/runtime APIs retain their existing actionable `ControlException` responses. Authentication/CSRF and JSON-binding errors retain framework/middleware statuses.

## Remaining milestone work

| Capability | Status |
| --- | --- |
| Declared host/type/configuration-source association, REST reads/mutations, CAS/retry receipts, legacy compatibility, per-managed-registration defaults | Implemented in this slice |
| Verified host/container binding, current placement evidence, lifecycle authority and shared physical capacity | #121/#6 follow-up; configuration alone cannot authorize remote effects |
| Reusable worker slots independent of legacy conversations, project-owned task/workspace/session identities | The additive local binding contract is implemented in [TASK_BINDINGS_API.md](TASK_BINDINGS_API.md); native session creation and verified placement remain follow-up work |
| Configuration resolution and official Dev Container CLI build, isolated repository preparation and fresh task sessions | #42/#43 |
| UI wizard, durable provision/drain/restart/retire operations and complete disposable lifecycle automation | #43; no new UI or lifecycle adapter in this slice |
| Multi-project scheduling and progressive live worker transition | #98 after provisioning/recovery acceptance |

Regression coverage includes request replay across restart/deletion, competing edits/creates, retained setup claims, archived coordinators, connection-profile changes, archive references, endpoint authentication/CSRF/strict JSON, bounded pagination, and migration from existing inventory with retained sequences/receipts and enforced foreign keys. Full exact-head CI remains required. These service/API tests do not claim an actual Docker build, restart or fleet migration.
