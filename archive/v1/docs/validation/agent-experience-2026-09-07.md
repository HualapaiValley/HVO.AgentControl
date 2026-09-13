# Agent experience validation — 2026-09-07

Implemented coordinator-only roles, capability onboarding and observations, optional versioned guidance, intermediate progress delivery, and dedicated admin terminals. See [operating guide](../AGENT_EXPERIENCE.md).

## Automated checks

- Required solution restore, Release build with warnings as errors, and format verification passed.
- Full regression with real SSH fixtures: **27 passed, 0 failed, 2 optional native-provider checks skipped**, `agent-experience-final.trx` (1 minute 2 seconds).
- After refining capability instructions to exclude provider/authentication files and suppress timestamps on empty reports: **13 focused tests passed**, `capability-report-refinement.trx`. Release build and format verification passed again. Counts overlap with the full run and must not be added together.
- Tests cover immutable rendered guidance across restart/retry, capability-inquiry coalescing without aborting work, coordinator role reservation and exclusion (including changed roles before dispatch), inventory in model context, intermediate progress before completion, per-message guidance overrides, and no duplicate assignments.

## Browser and SSH checks

- Existing fixture browser smoke passed: editing during work, captured queued model settings, reasoning choice, questions/permissions, archive/restore, preserved sessions, navigation, multiple workers, and mobile layout.
- `capabilities-smoke.cjs` passed: automatic initial worker inquiry, actual SSH workspace facts and unknown-access distinctions, no coordinator inquiry, coordinator exclusion in inventory and recipient controls, guidance capture, coordinator conversation controls, and mobile layout. Agent responses here came from the deterministic fixture, not live inference.
- `terminal-smoke.cjs` passed against both the fixture app and the published Docker demo: real SSH shell input/output, an OS marker command, `stty size` proving column changes, explicit close, reopening, page-close cleanup, anonymous denial, and invalid-CSRF rejection before SSH creation. The published check used the dedicated coordinator runtime but did not prompt its model or touch its OpenCode tmux session.
- Published Docker readiness, authenticated interactive navigation/reloads on all four main pages, form interaction, browser assets and anonymous API denial passed. A source-only test initially lacked the correct content root/environment; relaunching with the application content root and Development static assets resolved it. Packaged Docker assets passed independently.
- Screenshots inspected: `artifacts/browser/admin-terminal.png` and `artifacts/browser/coordinator-roles-mobile.png`. Checkbox alignment and empty participant descriptions were subsequently improved.

## Demo deployment and native observations

The owner demo remains at `http://192.168.1.14:5054`. A stopped-web backup of its data and secrets was created at `.fixture/before-agent-experience-20260907T015211Z.tar.gz` with owner-only file permissions. Native worker/coordinator session IDs were checked against the pre-update snapshot and preserved. The unused provisioned coordinator was explicitly reserved using `.fixture/coordinator-ready.json`; names were not used to infer its role. Both runtime health checks passed after deployment.

A real OpenCode capability inquiry exposed a permission to read OS identification data and another request to print provider configuration. The OS command was reviewed and allowed once; the configuration dump was declined. That native turn ended without narrative output. The inquiry now explicitly excludes provider/authentication files and environment values, and empty output does not establish a fresh capability-report timestamp. A fresh inquiry uses a new command identity; the original delivery remains recorded. The refined inquiry completed on the real OpenCode demo worker, persisted a 3557-character report, and left no pending permission requests. Both runtime health checks remained Healthy. Compact evidence is in [native capability result](native-capability-2026-09-07.json); the full agent report remains in the owner UI.

The separate local test app and SSH fixture containers were stopped after validation. The owner's web, coordinator, demo client and original development container remain independent. No native session was replaced, no original coding task was aborted, and no PR/push/merge was performed.

## Limits

Capability prose is agent-reported; individual claims are not yet normalized or independently verified. Progress excerpts can contain incomplete streamed text; report cadence is best effort, and general scheduled reminder jobs are not implemented. The coordinator still has a bounded context and one unfinished workflow. Admin shells are ephemeral; deliberately detached OS processes can outlive them. The full optional native-provider regression was not rerun in this change.
