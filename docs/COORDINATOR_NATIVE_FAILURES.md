# Coordinator native failure recovery

Issue #160 separates a failed native turn from a malformed model answer. Reconciliation can correctly record a command as delivery `Finished` and its assignment as `Failed`: the associated turn ended, but it did not produce a successful decision. The coordinator checks the retained caller-scoped `ResultJson` for native assistant errors before parsing a decision or superseding it with an owner follow-up. Text beside an error, including valid routing JSON, is never applied. A non-error answer with malformed JSON retains the existing two format corrections and subsequent bounded backoff.

## Durable hold

The run enters `Waiting` with a visible **Coordinator held after native …** explanation. `CoordinatorContext.NativeFailure`, stored in the run's existing `InputJson`, retains the failed command ID, native caller/session/assistant identities, effective provider/model/agent/variant, provider-pool revision, sanitized category/status/retry evidence, and text/tool-presence flags. No database migration is required. `CoordinatorNativeFailureHeld` is an audit event; pruning that event does not release the persisted hold.

The original command, native result, transport delivery, assignment outcome and worker commands remain unchanged. The service heartbeat still updates. An unchanged rejection produces neither additional coordinator prompts nor `CoordinatorCorrectionRequested` events, including after restart or the normal idle-reassessment deadline. An owner text follow-up does not establish recovered model access.

| Evidence | Classification / behavior |
| --- | --- |
| Native API 400 and other otherwise unclassified 4xx responses | `InvalidRequest`; change the incompatible request route/settings. A provider-pool reset alone does not fix it. |
| Native authentication error or API 401/403 | `AuthenticationRequired`; existing provider-access recovery applies. |
| Structured quota-exhaustion code | `Exhausted`; no reset time or remaining allowance is inferred. |
| API 429 without proven exhaustion | `Throttled`; parsed `Retry-After` remains evidence, not permission to replay. |
| API 5xx | `Unavailable`; unchanged transient failure is held rather than JSON-repaired. |
| Native message abort | `Cancelled`; delivery and cancellation evidence remain distinct. |
| Native context overflow / output-length failure | `ContextLimit` / `OutputLimit`; do not try to repair response JSON. |
| Other native assistant error | `NativeError`; preserve evidence and hold. |

Provider response bodies, headers, error prose and credentials are not copied into the checkpoint, status or new audit event. Original authenticated native evidence remains under the existing transcript/database protection policy.

## Eligible fresh decision

For a turn without tool evidence, the hold can clear when the coordinator is freshly observed idle, has no outstanding commands, and its current route is present in discovered model options with an available provider pool. In addition, either:

- Its selected provider/model/agent/variant differs from the failed command's **frozen effective request**. Changing only its name or description is insufficient. Earlier queued commands keep their own effective model.
- A recorded authentication, exhaustion, throttling or transient provider hold has a newer, confirmed available pool revision, through the existing provider-recovery mechanism. Merely reaching a cooldown timestamp is insufficient.

Release records `CoordinatorNativeRecoveryEligible`. The next tick constructs a fresh decision from current evidence with a new command ID. Its frozen context includes the prior failure checkpoint; a subsequent native failure links to the preceding failed command. The original native command is never resent. An explicit owner pause continues to block recovery and dispatch until the owner resumes it.

Tool evidence requires review rather than automatic continuation, even after selecting another model. Inspect effects, stop the held run, and start a reviewed recovery with appropriate instructions. Missing legacy effective-route evidence also prevents automatic release; it is not replaced with guessed current settings.

## Remaining work

This is a bounded emergency slice of #82. Existing provider fallback recommendations are advisory; automatic policy-selected fallback dispatch is still missing. A durable visible hold is not a claim that autonomous fallback is complete. Eligibility does not prove a provider will accept its next request; another rejection creates another retained hold.

This change handles errors already attributed to a terminal decision receipt. Automatic-compaction failures attached to unrecognized synthetic parents can leave a caller `AcceptedByRuntime` while the native session is idle. #157 tracks that lineage/context-limit boundary and session recovery. This slice neither attributes unrelated synthetic messages by proximity nor implements context rotation, aborts, session wipes or worker replay.

Regression tests use the actual runtime reconciler and coordination store with disposable native-shaped observations. They cover native error categories, genuine malformed answers, partial/tool evidence, unchanged holds across event pruning/restart, owner pause, frozen request routing, repeated rejection, explicit provider recovery, and independent worker/heartbeat continuity.
