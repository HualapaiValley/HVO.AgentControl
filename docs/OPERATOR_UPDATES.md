# Durable operator updates

## Implemented first slice

Operator updates use a service-owned clock that is independent of coordinator and worker model
turns. An authenticated owner can create one schedule for a coordination run through
`POST /api/v1/coordinations/{runId}/operator-updates` with a UUID request ID and an interval of
1-1440 minutes. This does not alter the coordinator action schema, enqueue a prompt, or consume a
model turn.

Schedule and update rows are durable SQLite records. Starting monitoring emits an immediate
baseline. Each nominal due time has a deterministic occurrence ID and a database uniqueness
constraint. Concurrent or repeated wakes therefore create one update row. A restart before the
next due time emits nothing early; after multiple missed intervals, the service emits one catch-up
row with the missed count and advances to the next original cadence boundary instead of flooding
historical ticks. Completed or stopped runs receive one terminal update on the next scheduler tick
and disable the schedule.

Each immutable update contains a bounded JSON summary built directly from current database
records. It records run state and participant phase, a bounded plain assignment excerpt when a
recorded run prompt exists, and separate timestamps for transport observation, meaningful
progress, and command receipt. Pending permission, task question, uncertain delivery, explicit
failure/cancellation, queued work, and quiet native activity remain distinct. Absence of progress
does not become failure, completion, or an invented estimate.

Updates are retrieved in bounded sequence order through
`GET /api/v1/operator-updates?after={sequence}&take={1-100}`. The existing authenticated activity
signal remains advisory: clients retrieve committed rows after reconnect and deduplicate by
sequence/occurrence ID. `POST /api/v1/operator-updates/{id}/ack` records an idempotent owner
acknowledgement. Authentication and the existing antiforgery policy protect these endpoints.
Acknowledgement is not inferred from SignalR delivery.

## Remaining scope

- Add the Activity page, unread cursor presentation, and schedule controls to the Blazor UI.
- Emit immediate material milestones and active-set baselines without moving periodic deadlines.
  Coordination participants are immutable in the current run model, so this slice initializes the
  persisted active-set revision but does not yet revise it.
- Define acknowledged-row retention. This slice does not prune operator updates and therefore
  cannot discard unread records.
- Add stable per-device cursors if the product later needs acknowledgement beyond the current
  single-owner identity.
- Add external delivery only as an explicit configured feature. No Slack, email, or GitHub
  notification is sent by this implementation.
- Measure hosted-timer lateness and closed-browser presentation in a browser soak. Store-level
  regression tests cover twenty intervals, duplicate wakes, restart catch-up, terminal state, and
  authenticated retrieval/acknowledgement.

This first slice guarantees one committed database record per logical occurrence in the current
single-replica boundary. It does not claim exactly-once websocket or browser observation.
