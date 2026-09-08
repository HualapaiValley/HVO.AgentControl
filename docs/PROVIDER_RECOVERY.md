# Provider Recovery Authority

Provider access and recovery request ownership are independent durable facts:

- `State`, `RetryAt`, and `ConsecutiveFailures` describe access evidence and policy.
- `RecoveryCommandId` reserves one request, including while it is queued again by
  read-only preflight or its native delivery is unknown.
- `LastCommandId` identifies the latest recorded failure, not the reservation.
- `RecoveryOwnershipUnknown` blocks admission when a legacy reservation cannot
  be attributed safely. Provider access verification does not clear it.

Only the reserved request may pass the ownership gate. It must still pass the
provider-readiness and access gates: a later authentication/quota hold blocks
even that request. Late transient failures also preserve their cooldown evidence;
they cannot replace the reservation. Failure receipts remain deduplicated and
attributed to the command which reported them.

Owner access resume clears an access hold, not request ownership. It cannot
authorize a competing probe while an earlier request remains reserved. It does
not send a provider probe, reset a quota, purchase access, or replay a command.

## Settlement

Known-unsent cancellation/rejection and attributed native terminal evidence can
release the matching reservation. An accepted abort with observed idle settles
accepted/running work; an unknown or administratively retired recovery request
also requires its native caller identity in the snapshot. A lost response or
`resolveUnknown` acknowledgement is not native settlement.

Cancelled routing authority with a retained reservation continues to receive
native observation. Later terminal evidence settles the reservation without
reactivating the command, overwriting its historical outcome, or reapplying a
coordinator decision. Registration deletion cannot remove its observation owner.

Settlement never overwrites later access evidence. Only successful settlement
while access is still `Recovering` establishes `Available`. Unsuccessful settlement
in that state yields `RecoveryRequired`; existing manual holds/cooldowns and
independent explicit access verification are retained.

## Legacy Upgrade

The unmerged `ProviderRecoveryLeaseOwnership` migration backfills legacy
`Recovering/LastCommandId` reservations only when the existing prompt, pinned
pool, worker/runtime relationship, and outstanding delivery state agree.
Queued preflight reservations and unknown deliveries are included. Missing,
terminal, or mismatched records become `RecoveryRequired` with
`RecoveryOwnershipUnknown = true`, not guessed recovered access. No new native
request or provider mutation is performed by migration.

Unverifiable ownership remains fail-closed pending evidence-backed reconciliation;
this change does not provide an automatic or access-only release for it. Do not
clear this flag by treating provider availability as proof of request settlement.
Upgrade tests create an actual prior-schema SQLite database, close it, then start
the application and verify both reservation retention and definitive settlement.

The migration has not shipped separately from this PR; no compatibility with
databases created from intermediate, unmerged PR168 revisions is claimed.
