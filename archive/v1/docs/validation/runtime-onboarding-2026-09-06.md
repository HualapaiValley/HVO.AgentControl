# Runtime onboarding and startup options validation

Executed 2026-09-06 using SDK 10.0.400, the existing disposable Ubuntu x64 SSH fixtures, OpenCode 1.18.29 and Playwright 1.58.2.

## Checks passed

- Final required restore, Release build with `--warnaserror` (zero warnings/errors), and format verification.
- Final deterministic unit/persistence/SSH regression: **14 passed, 0 failed, 2 native tests explicitly skipped**, 1 minute 1 second.
- Both native tests passed in the earlier combined invocation: real provider work across two SSH targets, and native install/schema/reuse/session recovery with the newly selected startup flags. The process command line contained `--pure --print-logs --log-level WARN`. Reconnecting with changed flags was rejected until the owned process was explicitly stopped.
- Guided browser: automatic host-key discovery, explicit first trust, missing/wrong password, encrypted credential reuse without re-entry, verification invalidation when options change, actual native startup with `--pure --print-logs --log-level INFO`, automatic worker setup and native session creation.
- Existing browser flow: manual mounted-reference profile, runtime connection, task/follow-up, queue, question, reopen, second client and safe transcript rendering.
- Updated Docker image: startup/migration on the existing volume as UID/GID 1000, readiness 200, anonymous API 401, then restart with the database and authentication keys intact.
- Owner demo: refreshed server at port 5054, original login, healthy runtime and unchanged worker native session ID. No remote stop/abort was issued to the owner's client.

The first combined run returned 15 passed / 1 failed: the existing cancellation test checked the other worker before it had started. The test now waits for both workers to be active. The following regression exposed an incorrect revision in the newly added trust-token test setup; it now uses the saved profile revision. These were corrected before the final passing run. An initial browser selector used an exact label match despite help text in the label; the selector was corrected and both browser runs passed. No known failing checks remain.

## Commands and retained evidence

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes

# Final passing regression:
HVO_SSH_FIXTURES=1 dotnet test HVO.AgentControl.slnx --no-build \
  --configuration Release --logger 'trx;LogFileName=runtime-onboarding-final.trx'

# Earlier combined run, including the two passing native tests:
HVO_SSH_FIXTURES=1 HVO_NATIVE_FIXTURE=1 dotnet test HVO.AgentControl.slnx \
  --no-build --configuration Release --logger 'trx;LogFileName=runtime-onboarding.trx'

# Against the separate fixture-backed app on 5056:
HVO_BASE_URL=http://127.0.0.1:5056 \
HVO_PLAYWRIGHT="$PWD/.fixture/browser/node_modules/@playwright/test" \
/tmp/hvo-node/bin/node tests/Browser/onboarding.cjs
HVO_BASE_URL=http://127.0.0.1:5056 \
HVO_PLAYWRIGHT="$PWD/.fixture/browser/node_modules/@playwright/test" \
/tmp/hvo-node/bin/node tests/Browser/smoke.cjs

docker build -t hvo-agentcontrol:local .
```

Logs: `/tmp/hvo-onboarding-final-tests.log`, `/tmp/hvo-onboarding-full-tests.log`, `/tmp/hvo-onboarding-browser.log`, `/tmp/hvo-onboarding-browser-regression.log`, and `/tmp/hvo-onboarding-docker.log`. TRX files are in ignored `tests/HVO.AgentControl.Tests/TestResults/`. The guided form screenshot is `artifacts/browser/onboarding-verified.png`. Normal Node/npm setup and the separate browser-app command are documented in `tests/README.md`.

Docker image ID: `sha256:86505e0b2c2943fc3f9790749ec8bb590f11478d5c2c6269c1aa90c5608fbc10`.

The actual pinned binary's `serve --auto` probe exited 1. Its `serve --help` advertises the three exposed options; TUI/run `--auto` is not silently mapped into permission grants. See the [compatibility record](../OPENCODE_COMPATIBILITY.md) for source references.

## Practical limits

Host-key discovery retrieves a key; explicit first-use trust does not independently establish the host's identity. Existing pinned keys cannot be silently replaced. Entered credentials are encrypted using persistent Data Protection keys; protect and back up both `data/credentials/` and `data/keys/`. Cancelled drafts may leave unreferenced encrypted files. Provider login remains remote/manual when required. Live macOS/arm64 checks remain unavailable. Hosted CI was not run. The owner demo is left running; separate verification fixtures and package-check services are stopped.
