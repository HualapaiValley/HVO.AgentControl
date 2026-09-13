# Save for setup — 2026-09-07

Problem: successful SSH authentication followed by a failed agent prerequisite check provided no guided way to save entered credentials. Missing `tmux` therefore prevented access to the new admin terminal needed to repair the host.

Change: successful pinned-key SSH authentication now permits a protected, profile-bound setup-save ticket, even if prerequisite checks fail. Save for setup atomically records an idempotent receipt and a disconnected runtime with encrypted credentials and `SetupRequired` diagnostics. It does not enqueue EnsureServer. Full verified-connect still requires its distinct readiness ticket. Changed profiles and setup tickets used for connection are rejected. The UI clears plaintext credential fields once protected references have been issued, exposes Save for setup, and explains how to use the terminal and verify again.

Validation:

- Required solution restore, Release build with warnings as errors, and format verification passed.
- Five onboarding tests passed, including real SSH verification, protected private-key reuse, unmet workspace requirements, setup-only saving, duplicate-request handling, profile tampering, rejection of setup tickets for agent connection, and readiness re-verification.
- `tests/Browser/setup-recovery.cjs` passed against a separately created SSH container with `tmux` temporarily unavailable. The UI showed the failed dependency, allowed secure saving without starting OpenCode, and opened a real interactive SSH terminal. After the fixture restored `tmux`, Edit → Verify reused the saved credential and reached worker setup. That container was removed afterward.
- Docker demo updated without a schema migration. Packaged browser readiness, authentication, navigation/reload and runtime-form checks were run after startup. The first attempt raced the web listener during container restart; readiness subsequently succeeded and the repeated browser check passed. Both owner runtimes were Healthy and their existing native session identities were preserved.

The local test app and shared SSH fixtures were stopped after validation. Owner runtime processes and native conversations remain independent of the web deployment. Dependency installation is an explicit admin action through the terminal; no installation or sudo operation was added to setup-only saving.
