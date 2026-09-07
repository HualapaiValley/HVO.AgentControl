# Page organization and Docker browser validation

Completed 2026-09-06 on Linux x64, resumed after a VS Code connection loss.

## Delivered behavior

- Overview (`/`) contains fleet status, pending-response links, an agent selector and the selected conversation/instruction/reply controls.
- Runtimes (`/runtimes`) contains registration, guided verification, connection/startup settings and server lifecycle/diagnostics.
- Workers (`/workers`) contains worker/worktree setup, workspace model inspection, filters and inventory.
- A shared menu connects the three pages. Runtime setup links preselect a runtime on Workers; successful worker creation opens its conversation on Overview. Conversation query links preserve selection through reload and browser Back. Switching workers clears drafts, reply state and older loaded history. Page disposal releases only its local snapshot subscription.

## Results

| Check | Result |
| --- | --- |
| Pinned SDK | 10.0.400 |
| Required restore, Release build with warnings as errors, format verification | Passed, repeated after the packaging fix; zero build warnings/errors |
| Regression suite with SSH fixtures | 14 passed, 0 failed, 2 optional native tests skipped; 1 minute 2 seconds; `navigation.trx` |
| Expanded fixture browser smoke | Passed: menu/page separation, two-worker conversation and draft isolation, query links/Back/reload, task/follow-up/queue/question, close/reopen, second client, safe transcript rendering |
| Desktop/mobile rendering | Passed; no horizontal overflow on Overview, Runtimes or Workers |
| Guided onboarding browser | Passed: explicit first trust, missing/wrong password, encrypted credential reuse, verification invalidation, actual native startup flags, preselected worker setup and new conversation |
| Published Docker browser regression | Reproduced noninteractive UI on the previous image; passed on the corrected image before and after restart |
| Docker persistence | Existing database present, unchanged authentication key hashes across restart, UID 1000, `/data` mode 700 |
| Owner demo | Browser sign-in, all three pages, runtime setup link, conversation selection and original managed runtime/native worker identities passed after server restart |

The fixture smoke uses simulated model output. The guided onboarding check runs real OpenCode but does not invoke inference. The optional native-provider tests were not repeated for this UI follow-up; earlier executed native results remain in the release/onboarding validation records.

## Packaging failure and fix

The initial Docker image passed build/readiness checks but remained noninteractive in the browser. Docker restores copied project files before copying Razor source. The SDK normally detects Razor files during restore to include ASP.NET browser assets; that detection could not see the source, and the published image omitted the Blazor framework script.

The application project now sets `RequiresAspNetWebAssets` explicitly, preserving Docker's dependency restore cache. `tests/Browser/package-smoke.cjs` failed against the previous image because `.shell` remained `data-interactive="false"`. With the fix it passed sign-in, interactive menu navigation, reload and runtime form interaction, with no browser script errors, before and after container restart.

Previous failing image: `sha256:0989a0bb442cb59802e6e23d1c56f1ca018c699cdbee877fe7a25726234842e2`.

Final image `hvo-agentcontrol:local`: `sha256:094cc75428269dd63aa12358b956ae38ec6141434426dfe877f0cf2d568fe7ed`.

## Commands and evidence

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
HVO_SSH_FIXTURES=1 dotnet test HVO.AgentControl.slnx --no-build \
  --configuration Release --logger 'trx;LogFileName=navigation.trx'

# Against a separate fixture-backed application on port 5056:
HVO_BASE_URL=http://127.0.0.1:5056 \
HVO_PLAYWRIGHT="$PWD/.fixture/browser/node_modules/@playwright/test" \
/tmp/hvo-node/bin/node tests/Browser/smoke.cjs
HVO_BASE_URL=http://127.0.0.1:5056 \
HVO_PLAYWRIGHT="$PWD/.fixture/browser/node_modules/@playwright/test" \
/tmp/hvo-node/bin/node tests/Browser/onboarding.cjs

docker build -t hvo-agentcontrol:local .
# The package check used the current inspected container IP on port 8080,
# HVO_OWNER_PASSWORD_FILE=.fixture/package-owner-password and the same Playwright override.
# Portable local Docker instructions are in tests/README.md.
/tmp/hvo-node/bin/node .fixture/verify-navigation-demo.cjs
```

Saved logs: `/tmp/hvo-navigation-tests.log`, `/tmp/hvo-navigation-browser.log`, `/tmp/hvo-navigation-onboarding.log`, `/tmp/hvo-navigation-docker-fixed.log`, `/tmp/hvo-navigation-package-before.log`, `/tmp/hvo-navigation-package-fixed.log`, and `/tmp/hvo-navigation-demo-resumed.log`. The TRX is under ignored `tests/HVO.AgentControl.Tests/TestResults/`. Screenshots are under ignored `artifacts/browser/`: `dashboard.png`, `mobile.png`, `runtimes-mobile.png`, `workers-mobile.png`, `onboarding-verified.png`, `demo-overview.png`, and `demo-workers.png`.

## Final environment

The owner's server at port 5054 and Docker client `hvo-agentcontrol-demo-client` remain running with existing data and conversations. The server was restarted independently of the old editor process. No prompt, abort or remote stop was issued to the demo during resumption checks. Temporary browser application, SSH fixtures and package-validation container are stopped; their evidence/data are retained. No commits, pushes, production deployment or hosted CI run was performed. No known failing checks or unfinished work remain for this follow-up.
