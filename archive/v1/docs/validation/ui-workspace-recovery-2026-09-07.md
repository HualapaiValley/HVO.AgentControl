# UI organization and workspace recovery — 2026-09-07

Diagnosis: Home M4 still had its original worker registration and native conversation. No completed deletion appeared in the recent command audit. A later creation failed because the existing worker owned `/Users/roys/Development/_github/RoySalisbury`. The worker was idle and the runtime healthy before this change. Last tool results and assignment outcomes were being displayed on cards without distinguishing their historical nature.

Changes:

- Workspace conflict checks name the existing owner, including archived workers and equivalent registered SSH endpoints. The draft offers direct conversation/editor links and prevents duplicate submission. Canonical SSH workspace checks remain authoritative for aliases. Idempotent request replay is preserved.
- Overview becomes an explicit fleet landing page; selecting a worker opens its conversation without fleet metrics above it. Worker cards prioritize current status and primary actions. Historical outcomes, tool details, connection metadata and delivery records are collapsed.
- Previous failed/cancelled setup attempts for an already registered workspace are grouped separately; dismissing attempts does not remove worker registrations.
- Shared dark navy/blue theme and navigation adapted from the local HVO.SkyMonitor LogicHost layout/theme at commit `a9e87030`. Runtime editor appears before the inventory. Mobile layouts were checked visually and for horizontal overflow.

Validation:

- Pinned SDK restore, Release build with warnings as errors, and format verification passed.
- Full SSH-enabled test suite: 33 passed, 2 optional native-provider tests skipped (`ui-workspace-recovery.trx`). New tests cover named workspace conflicts, archived ownership, replacement after deletion, idempotent replay and separation of live readiness from previous outcomes.
- Full browser smoke passed with active model edits, frozen queued settings, interactive permission/question replies, archive/restore and preserved conversations. Tests now expand Details & management for secondary actions.
- Registration lifecycle browser test passed: setup refresh/navigation recovery, failure details, cancellation/dismissal, worker deletion and unused/in-use runtime handling.
- New `ui-recovery.cjs` passed against both the disposable test app and published owner demo. It opens a duplicate draft but never submits it; verifies links to the existing worker, disabled creation, collapsed history, compact statuses, explicit overview selection and preserved native identity. All four primary pages and the conversation were checked at 390px width. Desktop/mobile screenshots are in ignored `artifacts/browser/ui-*.png`.
- Published application smoke passed, including readiness, authentication boundaries, scripts and interactive page navigation/reload.
- Before deployment, owner data/secrets were privately backed up while the web container was stopped. All three existing native session IDs were retained; Docker demo, coordinator and Home M4 returned Connected/Healthy with idle, non-stale workers. No owner registration was deleted and no model prompt was submitted for UI validation.
