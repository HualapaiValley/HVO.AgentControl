# Worker status, runtime telemetry and model usage

Requested during the advanced concurrent exercise on 2026-09-07. Current UI work: #28. Durable usage ledger/reporting: #34. Runtime sampling: #35.

## Status without reading the transcript

Use authoritative delivery/activity/request states for queued, active, approval required, stale, failed and completed-turn indicators. Add a short worker-reported progress line and its age; retain the full prompt and transcript behind details. Tool events can identify reading files, editing, running a build or waiting for a tool. During this run, native `compaction` parts and assistant `summary=true` metadata identified context compaction; that supports a useful “Compacting conversation” phase without displaying reasoning text. A reasoning event can indicate that generation is active, but raw reasoning is not a suitable status label. Do not claim a prompt-reading percentage, infer success from idle, or invent an ETA.

Optional coordinator summaries should be short, timestamped and explicitly attributed. They complement the event-derived status, which must continue updating while the coordinator is busy or unavailable. Avoid another model request on every token; update summaries at meaningful checkpoints. Keep the coordinator's own activity visible separately from task workers.

## Observed provider data

The pinned `docs/compatibility/opencode-1.18.29.openapi.json` schema defines assistant `modelID`, `providerID`, creation/completion times, `cost`, and tokens for input, output, reasoning and cache read/write. `step-finish` parts repeat usage information. The running Big Pickle sessions supplied these counters, including nonzero cache reads and zero reported cost. Incomplete streamed messages also carried zero placeholders before completion; those are not final usage evidence.

The [OpenCode server documentation](https://opencode.ai/docs/server/) documents session status, message retrieval and event streams. Validate adapters against their installed schema and observed messages, since provider coverage can differ.

Store a compact durable usage row keyed by runtime, native session and native message ID. Upsert revisions; never add each streamed snapshot as new usage and never sum both assistant and step-finish totals. Preserve the message's observed model across worker setting changes. Keep missing, provisional and final counters distinct, and separate provider-reported cost from any explicitly labelled estimate. Backfill only retained history and expose coverage dates/gaps.

Report by worker, provider/model and time window. Separate queue wait, command wall duration, message duration, tool time and time to first observable output; none is automatically pure inference time. Provide totals and export, with coordinator overhead shown separately. Record failures/cancellations as outcomes, not zero-cost assumptions. Test replay/reconnect, repeated and reordered updates, model changes, missing counters and retention.

The advanced exercise exports a deduplicated observational usage artifact from retained command results/transcripts. It proves data availability; it is not the durable ledger or an all-time billing report.

The first durable implementation slice is documented in [MODEL_USAGE_LEDGER.md](MODEL_USAGE_LEDGER.md).

## Runtime measurements

Sample without a model over the existing authenticated transport at a bounded interval. CPU usage requires counter deltas and effective allocation; explicitly label core usage versus percent of quota. Display current memory against the measured runtime limit, with host-visible data kept distinct. Preserve sample time and stale/unavailable states; discard invalid/reset CPU deltas and wait for the next valid sample after reconnect.

Support Linux cgroups and macOS independently. Sampling failures must not interrupt tasks or registration. Bound history and command duration, cancel polling on disconnect, and use no detached OS daemons. Runtime cards need a compact current reading and a detail trend. Static capability enrollment facts remain separate from live utilization.
