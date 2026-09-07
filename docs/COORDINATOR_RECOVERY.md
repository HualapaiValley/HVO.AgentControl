# Bounded coordinator format recovery

A finished coordinator command whose response cannot be parsed as a routing decision can request at most two format corrections. Each is a new command with a new native caller identity and a fresh participant observation. The rejected native command and its full response remain in command storage; none of its proposed actions are dispatched or extracted from surrounding prose.

The existing persisted coordination input contains an optional `repair` object with the attempt count and rejected command ID. Old inputs without it remain compatible. `CoordinatorDecisionRejected` and `CoordinatorCorrectionRequested` journal events link the rejected and replacement commands. The budget survives web restart. Recovery waits for an observed, idle coordinator, respects the run's total decision-round limit, and does not bypass uncertain worker delivery or full-batch validation.

A valid decision clears the consecutive format-repair budget. Exhausted recovery pauses visibly for the owner. An explicit owner follow-up or resume resets that budget; it does not raise the total round limit. Invalid action batches, permission attempts, stale recipient revisions, failed/cancelled commands and uncertain delivery still pause for review rather than being automatically retried.

## Native structured-output compatibility remains open (#24)

An isolated OpenCode 1.18.29 / Big Pickle probe using the pinned `json_schema` format accepted `prompt_async`, but subsequent message retrieval returned HTTP 400 `Expected OutputFormatJsonSchema` at the stored user message's `format`. This does not establish whether the model supports structured generation. The managed coordinator continues using the strict text-JSON contract until the native message-read problem and actual structured generation are verified. Ordinary worker prompts remain free-form.

The regression tests cover persisted correction budgets across restart, reconnect observation before dispatch, distinct correction receipts, full budget exhaustion, round limits, uncertain delivery, fresh recipient revisions and one-time dispatch after correction. Existing whole-batch, permission and stale-recipient tests remain in effect. Native recovery acceptance must be recorded separately from these fixture-backed tests.
