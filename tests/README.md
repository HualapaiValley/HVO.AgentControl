# Validation

Run the exact commands in the root README. The test suite has three distinct layers:

- `ProtocolTests` / `PersistenceTests`: fragmented SSE/UTF-8 limits, safe path arguments, owner authentication/CSRF, atomic UUID deduplication/revisions, database failure, reply identity, restart recovery and single-replica locking.
- `SshIntegrationTests` (`HVO_SSH_FIXTURES=1`): actual SSH.NET authentication/SFTP/tmux/forwarding to two Docker targets, with a deterministic HTTP/SSE process. Covers lifecycle ownership, concurrent ensure, missing prerequisite, host-key/port conflicts, safe worktree paths, models, routing/concurrency, queues, response loss, questions/permissions, abort and backend/SSE/SSH recovery.
- `NativeReleaseTests` (`HVO_NATIVE_FIXTURE=1`): actual checksum-pinned OpenCode installation/schema and real `opencode/big-pickle` tasks across two SSH targets. These tests use the provider's currently advertised anonymous access and may fail if it is unavailable. They never use an inferred owner credential.

`Browser/smoke.cjs` checks the browser flow against simulated transcripts: separate Overview/Runtimes/Workers pages, menu navigation, runtime and worker creation, two-worker conversation selection and draft isolation, query links/reload/Back, task/follow-up/question recovery, and desktop/mobile rendering without horizontal overflow. `Browser/native-smoke.cjs` checks real model tasks, native permissions/questions and resulting files. The latter deliberately answers only its disposable test-workspace requests; this is test behavior, not an application approval policy.

`RuntimeOnboardingTests` adds encrypted credential restart/tamper coverage, verification authorization/CSRF and expiring profile-bound proof, atomic save/connect, and SSH password/private-key/passphrase enrollment. Password checks temporarily enable password login on **fixture-a only** and restore its configuration afterwards. Do not run them concurrently with the onboarding browser check, which temporarily configures the same fixture.

`Browser/onboarding.cjs` exercises automatic key discovery/explicit trust on Runtimes, missing and wrong passwords, protected credential reuse, startup-option changes invalidating verification, real OpenCode startup flags, navigation to preselected setup on Workers, and the created native conversation on Overview. Start a separate fixture-backed app on port 5056, then run:

```bash
Control__DataDirectory="$PWD/.fixture/onboarding-browser-data" \
Control__SecretsDirectory="$PWD/.fixture/secrets" \
Control__AllowInsecureLocalHttp=true \
dotnet run --project src/HVO.AgentControl --configuration Release --no-build \
  --no-launch-profile --urls http://127.0.0.1:5056
# In a second terminal, after npm/Playwright setup from the root README:
node tests/Browser/onboarding.cjs
```

This check installs the real pinned binary if missing but does not invoke provider inference. It requires the disposable SSH fixture setup and advertised `opencode/big-pickle` discovery. The native installation test separately checks that changed startup options cannot silently reuse a living process, then verifies actual process arguments and session recovery after an explicit stop/restart.

`Browser/package-smoke.cjs` checks the published application in Docker: readiness, anonymous API rejection, sign-in, interactive navigation and reload on all three pages, the runtime form, and script loading. Run it against the container started using the root README's Docker instructions, then repeat after restarting that container:

```bash
HVO_BASE_URL=http://127.0.0.1:8080 \
HVO_OWNER_PASSWORD_FILE="$PWD/.secrets/owner-password" \
node tests/Browser/package-smoke.cjs
```

Set the URL to an explicitly reachable container address when Docker is remote. This check does not register hosts, submit prompts or require SSH fixtures. It catches the missing Blazor assets regression caused by restoring project files before Razor source is copied; the application declares `RequiresAspNetWebAssets` so that restore includes those assets.

All fixture configuration/credentials live under ignored `.fixture/`; screenshots are under ignored `artifacts/browser/`. The native evidence excerpts in `docs/validation/` identify actual tested sessions/files. Fixture tests do not claim model behavior, and native/provider checks are visibly skipped unless enabled. Tests are serial within the test assembly because lifecycle tests intentionally restart backend instances and control their disposable targets. No production target, branch, push or deployment is part of validation.

## Capabilities, guidance and terminal checks

`HVO_BASE_URL=http://127.0.0.1:5056 node tests/Browser/setup-recovery.cjs` creates its own disposable SSH container, hides `tmux`, verifies that secure setup-only saving and a real terminal still work, restores `tmux`, then re-verifies and reaches worker setup using saved credentials. It removes that container on completion. Run fixture setup first to prepare the image and key.

After the fixture browser smoke has registered a runtime, run `HVO_BASE_URL=http://127.0.0.1:5056 node tests/Browser/capabilities-smoke.cjs` for initial capability onboarding, role exclusion and rendered guidance. It creates disposable fixture conversations and sends deterministic fixture prompts.

`HVO_BASE_URL=http://127.0.0.1:5056 node tests/Browser/terminal-smoke.cjs` checks real SSH terminal input/output, resize, close/reopen/page cleanup, anonymous denial and invalid-CSRF rejection. Set `HVO_OWNER_PASSWORD_FILE` and optionally `HVO_TERMINAL_RUNTIME_ID` for another registered test runtime. It runs only a marker printf and `stty size`; it does not prompt OpenCode.
