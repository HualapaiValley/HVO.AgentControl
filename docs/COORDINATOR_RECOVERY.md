# Coordinator recovery

Malformed coordinator output must not terminate the coordinator or require a new owner prompt to recover. The service rejects the response without dispatching any of its proposed actions. Two immediate format corrections use new native caller IDs and fresh participant observations. Further failures enter a visible `Recovering` state with automatic retries after 30, 60, 120 and 240 seconds, capped at five minutes.

A definitively failed coordinator turn or a rejected action batch also enters delayed recovery. Every batch is still validated before mutation. Re-observation can resolve a stale recipient revision or busy worker; invalid permissions or unlisted targets never become authorized because they are retried. Recovery reasons are included in the next coordinator context. No arbitrary JSON fragment is extracted from surrounding prose.

## Durable fallback

The existing persisted coordination input contains optional `repair` and `recovery` objects. They retain the rejected command ID, correction count, recovery attempt, reason and retry deadline. Old inputs without these fields remain compatible. Journal events link rejected decisions, replacement commands, scheduled recovery and elapsed deadlines. A valid applied decision clears consecutive recovery state.

The hosted monitoring loop remains alive if a tick throws, including when recording recovery also fails because storage is unavailable. Shutdown cancellation is respected. Runtime supervisors continue collecting worker results and pending requests independently of coordinator generation or backoff. A pending decision retains its command identity during scheduling-error recovery; a definitively failed or rejected decision gets a fresh command. Recovery does not replay worker instructions or discard their results.

The configured maximum coordinator turns remains a hard owner-selected budget, including recovery requests. Reaching it pauses with an explicit budget message. Explicit owner pause/stop is never undone by the fallback. An owner follow-up or resume clears consecutive backoff/correction state. Unknown delivery and deliberate cancellation remain intervention states rather than blind resubmission; missing/archived participants or coordinator configuration still require restoration. These are deliberate safety/configuration controls, not unhandled JSON errors.

A provider quota wait is distinct from a parsing failure. OpenCode may retain an accepted turn in native `retry` state until its scheduled retry. The UI labels that state as **Provider retry**. Service recovery cannot supply quota or silently switch to an unconfigured paid model; the existing task and session remain preserved.

## Native structured-output compatibility remains open (#24)

An isolated OpenCode 1.18.29 / Big Pickle probe using the pinned `json_schema` format accepted `prompt_async`, but subsequent message retrieval returned HTTP 400 `Expected OutputFormatJsonSchema` at the stored user message's `format`. This does not establish whether the model supports structured generation. Managed coordination continues using the strict text-JSON contract until native message-read compatibility and actual structured generation are verified. Ordinary worker prompts remain free-form.

## Verification

Regression tests cover correction and recovery persistence across restart, reconnect observation before dispatch, bounded backoff, owner pause, turn limits, failed versus uncertain delivery, malformed envelopes/missing fields, full-batch rejection, fresh recipient revisions and one-time dispatch after recovery. Failure injection verifies that a tick error followed by a recovery-storage error cannot prevent the next monitoring iteration.

A hosted-timer integration test supplies three malformed native-result fixtures, waits through the real 30-second recovery deadline, supplies a valid fourth decision, verifies exactly one worker assignment, then processes its result and observes completion without any owner follow-up or resume. This exercises the actual scheduler and database; it does not claim live provider inference while Big Pickle's free quota is exhausted.
