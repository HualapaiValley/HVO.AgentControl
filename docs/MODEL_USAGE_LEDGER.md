# Model usage ledger

Issue #34's first durable slice stores normalized assistant-message usage independently from the
bounded transcript. Rows are keyed by runtime ID, native session ID, and native message ID.
Replayed retained history updates the same row through `OpenCodeUsageMerger`; final observations
cannot be demoted by later provisional snapshots, and identical snapshots do not advance the
stored observation timestamp.

The supervisor records assistant usage while reconciling native history and performs an
idempotent startup backfill from retained transcript rows and completed command results. Source
flags show which retained evidence contained a message. Usage rows have no registration foreign
key and are not removed with bounded transcript history or worker deletion. Startup backfill retains
the transcript native-created and command-result update chronology rather than assigning startup
time. For equal native revisions, transcript evidence takes precedence over command-result
evidence, so a stale retained command result cannot replace a reconciled transcript row.

When native assistant metadata contains its parent caller ID, the ledger also resolves and retains
the exact command and coordination-run IDs. Control-session usage records immutable binding ID,
generation and automatic-recovery intent where applicable. This keeps predecessor and successor
spend attributable after cutover, archival, transcript pruning or restart. Missing native caller or
binding evidence remains null rather than being inferred from timestamps or the worker's current
settings.

Authenticated endpoints:

- `GET /api/v1/usage?workerId=&providerId=&modelId=&from=&to=` returns stable ledger rows,
  deterministic role/worker/provider/model/currency groups, nullable sums with present/missing
  counts, and retained-evidence coverage bounds.
- `GET /api/v1/usage/export?...` returns the same filtered rows as deterministic UTF-8 CSV.

Time filters use the native assistant message's creation timestamp. Rows without a trustworthy
creation timestamp are retained but excluded when a time boundary is supplied. Coverage is
explicitly `retained-evidence-only`: it does not claim messages older than available native or
command history. Provider-reported cost is kept separate by currency and labelled
`provider-reported`; no price estimate is introduced.

Remaining issue #34 scope includes richer command/turn timing (queue wait, first output, tool
time), deliberate retention policy and durable coverage cursors, UI reports, and validation
against additional provider schemas.
