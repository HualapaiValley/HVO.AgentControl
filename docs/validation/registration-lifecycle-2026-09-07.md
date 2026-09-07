# Registration lifecycle and managed executable validation — 2026-09-07

Implemented durable worker setup cards, progress events, failed/cancelled setup editing and dismissal, worker/coordinator registration deletion, unused runtime deletion, optional CLI inventory, installed executable reporting, and PATH initialization for AgentControl admin terminals.

Validation:

- Required solution restore, Release build with warnings as errors, and format verification passed using pinned SDK 10.0.400.
- Full SSH-enabled suite: 31 passed, 2 optional native-provider tests skipped (`registration-full.trx`). New store tests cover setup persistence beyond 500 recent commands and across restart; dismissal constraints; busy/coordination/queue deletion rejection; workspace claim release; terminal/runtime reference protection; idempotent deletion and retired runtime ID rejection.
- Existing full browser smoke passed against disposable SSH fixtures: active worker edits, frozen queued model selection, questions/permissions, archive/restore, conversation identity, navigation and mobile layout.
- `registration-lifecycle.cjs` passed: queued setup survives refresh/navigation; cancelled setup editing and dismissal; failure details persist; unused runtime deletion; in-use runtime rejection; idle worker deletion and refresh. This script requires a disposable app previously initialized by `smoke.cjs`.
- Terminal browser smoke passed against a disposable SSH fixture and the real Home M4: shell input/output, resize, explicit close/reopen, page-close cleanup, anonymous and invalid-CSRF denial. The M4 run set `HVO_EXPECT_OPENCODE_VERSION=1.18.29` and successfully executed `opencode --version` by name.
- Published Docker browser smoke passed on all four primary pages.
- Existing owner data and secrets were backed up with mode 0600 while the web container was stopped; the additive migration then ran successfully. Three existing native conversation identities were preserved. Docker demo, coordinator and Home M4 all returned Connected/Healthy. No owner worker/runtime registrations were deleted for testing.

Home M4 already had OpenCode 1.18.29 at `/Users/roys/.local/state/hvo-agentcontrol/c8574361e17743e2afdf5c2507cb0e36/bin/opencode`. A direct pinned-key SSH probe confirmed its version before deployment. The defect was terminal PATH visibility, not a missing installation. New AgentControl terminals now inherit the actual installed executable directory. Independently opened SSH shells retain their own environment.

Deletion removes AgentControl registrations, not remote files or native history. Worker deletion releases its workspace claim; runtime deletion retains credentials and remote processes. Active/uncertain work must be resolved first. CLI presence is inventory only and does not establish authentication or capabilities of a logged-in account.
