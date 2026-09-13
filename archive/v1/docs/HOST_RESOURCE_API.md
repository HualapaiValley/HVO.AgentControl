# Trusted host executor and resource API

This is the independent host-authority and shared-capacity slice of #6. It accepts authenticated, sequenced evidence from an explicitly enrolled Linux host executor and fences resource grants at the external-effect boundary. It does not connect to a host, infer an SSH endpoint, run Docker, create a container, or enroll a real executor during migration or startup.

The controller stores opaque SHA-256 identities for the endpoint, physical host, engine, builder, CLI bundle, protected root, workspaces and filesystems. Raw executable paths, protected paths, Docker socket/context details and host credentials are outside this API. Docker capability is optional; missing Docker evidence blocks build grants without blocking non-Docker runtime grants.

## Authentication and enrollment

Owner routes retain the existing cookie authentication and `X-CSRF-TOKEN` requirement. Host executor routes use only the `HostExecutorBearer` scheme and do not accept the owner cookie. The bearer is:

```text
<enrollment UUID>.<43-character base64url secret>
```

The owner transfers that high-entropy secret to the executor through an external trusted channel and submits only its uppercase SHA-256 hexadecimal digest in `CreateHostExecutorInput`. The raw secret is never accepted by an owner route, persisted, returned, journaled or placed in a mutation receipt. A copied digest is not accepted as a bearer.

Enrollment begins in `Pending`. The pending bearer can call only activation, where the request must match the owner-approved host, authority generation and authority digest and supply digest-only boot/process incarnations. Activation consumes the exchange and makes an exact retry idempotent. Rotation increments authority generation, replaces the credential and authority digest, and returns the enrollment to `Pending`. Suspension and revocation fail subsequent authentication. Observation sequence remains monotonic across rotations so historical evidence cannot collide with a successor incarnation.

## Evidence and admission

An active executor submits immutable observations. The request must match all enrolled identity digests and the current authority generation, boot ID and process incarnation. Sequence advances monotonically per enrollment. The controller assigns a monotonic `physicalRevision` across every enrolled alias of the same physical host; only the latest physical revision and latest applicable workspace sequence can authorize admission or effect start.

Freshness is checked against both the executor's bounded `collectedTo` and the controller's `receivedAt`. `Complete` evidence contains effective and available CPU/memory, workspace filesystem capacity, build slots and explicit Docker availability. Optional process, swap, pressure and inode values support diagnosis but do not turn the temporary 6 GiB incident mitigation into a validated profile. Policy limits and controller reserves remain owner-configured immutable revisions.

Reservations are atomic under the controller's single-replica write gate and account all `Held`, `EffectCommitted` and `Unknown` records by physical-host identity, including aliases/devcontainers. They fence canonical workspaces, ports and explicit shared resources with durable filtered unique indexes. Configured limits and fresh observed headroom are both enforced. Filesystem reservations are charged against every reservation sharing either observed workspace or Docker filesystem identity, so workspace and Docker roles cannot overbook one physical filesystem. Builds additionally require matching Docker filesystem evidence and a fresh observed build lane.

`operationId` and `intentDigest` bind a reservation to the caller's durable operation intent without exposing executable arguments or host authority. The current owner API is the authority that requests admission. The provisioning ledger introduced by #171 must pass its already-approved operation identity and digest into this seam; this independent slice does not yet connect those records or claim that a Docker effect occurred.

## Effect and release fencing

A `Held` grant has a short owner-configured lifetime. Expiry prevents effect start but never silently releases or deletes the reservation. Immediately before an external effect, the authenticated executor calls `begin-effect`. The transaction revalidates exact executor generation/incarnation, host archive state, policy revision, latest observation, freshness, workspace/filesystem identity and current shared headroom.

The first successful transition returns `authorizedNow: true`; only that response authorizes the caller to begin the external effect. An exact retry after a lost response returns the durable reservation with `authorizedNow: false`, so restart/retry cannot execute the effect twice. Any changed request conflicts. A different executor cannot commit the reservation.

Once effect authority is committed, capacity remains held through timeout, restart, suspension and `Unknown`. `Unknown` is allowed only after effect commitment. Release requires a newer authenticated `Absent` observation for the same physical endpoint, workspace/filesystem scope and exact intent digest, with collection beginning after effect commitment. A rotated successor generation can supply that reconciliation evidence. An unconsumed `Held` grant can instead be explicitly revoked as `RevokedBeforeEffect` without fabricated host evidence.

## Routes

All routes use `/api/v1`.

| Authentication | Method and route | Input / response |
| --- | --- | --- |
| Owner cookie + CSRF | `GET /host-executors?hostId=...` | Enrollments without credential digests |
| Owner cookie + CSRF | `POST /host-executors` | `CreateHostExecutorInput`; 201 enrollment |
| Owner cookie + CSRF | `POST /host-executors/{id}/rotate` | `RotateHostExecutorInput` |
| Owner cookie + CSRF | `POST /host-executors/{id}/suspend` | `ChangeHostExecutorStateInput` |
| Owner cookie + CSRF | `POST /host-executors/{id}/revoke` | `ChangeHostExecutorStateInput` |
| Owner cookie + CSRF | `POST /host-resource-policies` | `ConfigureHostResourcePolicyInput` and immutable next revision |
| Owner cookie | `GET /hosts/{id}/capacity` | Executor, latest policy/evidence and physical reservations |
| Owner cookie | `GET /host-resource-reservations?physicalHostId=...` | Bounded reservation history |
| Owner cookie + CSRF | `POST /host-resource-reservations` | `AcquireHostResourceReservationInput`; 201 grant |
| Owner cookie + CSRF | `POST /host-resource-reservations/{id}/unknown` | `MarkHostResourceUnknownInput` |
| Owner cookie + CSRF | `POST /host-resource-reservations/{id}/release` | `ReleaseHostResourceReservationInput` |
| Pending or active executor bearer | `POST /host-executor/activate` | `ActivateHostExecutorInput` |
| Active executor bearer | `POST /host-executor/observations` | `SubmitHostResourceObservationInput` |
| Active executor bearer | `POST /host-executor/reservations/{id}/begin-effect` | `BeginHostResourceEffectInput`; `HostResourceEffectDecision` |

IDs and request IDs are non-empty UUIDs normalized to 32 lowercase hexadecimal characters. Identity and intent values are SHA-256 hexadecimal digests. Mutation request IDs are durable and share one host-resource namespace, including acquisition. An exact replay returns its original result; reuse for a different action, resource or payload conflicts. Failed validation does not reserve the request ID.

## Implementation boundary

Implemented here: enrollment/rotation/revocation, opaque bearer authentication, sequenced observations, physical-host policy revisions, atomic grants, effect fencing, uncertainty/release reconciliation, persistence, migration, owner/machine route separation and restart regressions.

Not implemented here: a deployed executor process, endpoint discovery, SSH exchange, Docker access, resource-profile certification, operation execution, automatic timer release, or live-host enrollment. Provisioning integration must call this boundary from its durable approved operation immediately before its existing external-effect adapter and honor `authorizedNow`; it must not move Docker authority into the web process or worker.
