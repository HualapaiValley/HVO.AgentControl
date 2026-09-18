# Browser checks (`tests/Browser`)

Playwright checks for the AgentControl V2 portal. The package is portability-first:
the browser binary is either the one bundled by the pinned `playwright`
dependency or an explicit `CHROME_PATH`; no machine paths are hardcoded.

## Hermetic CI suites

CI runs two hermetic browser suites against the locally built app
(`src/HVO.AgentControl/bin/Release/net10.0/HVO.AgentControl.dll`) on a free
loopback port, plus two local terminal unit checks (`terminal-wire.mjs` and
`state-kind.mjs`). None needs Docker, a model provider, or credentials, and none
performs an inference call.

| Script | Runtime | What it proves |
| --- | --- | --- |
| `ci-smoke.mjs` | `Control__Enabled=false`, no owner password | Portal shell, disabled-runtime status, terminal/model/cancel gates, responsive layout |
| `terminal-wire.mjs` | No app process | Local text/base64 output and UTF-8 decoder isolation across detach/reattach |
| `state-kind.mjs` | No app process | Independently enumerated server-state vocabulary (control wire, session, employee availability, remote connection, process, session operation) maps to the expected presentation kind, with availability-over-control precedence for held/faulted remote states |
| `system-drafts.mjs` | No app process (loopback stub) | System role-select retention, per-role draft/conflict/dirty cues across unrelated organization reloads, reset and clean save, vanished-role fallback, failed-then-successful save receipt (`ok`), failed authority reload recovery, load-failure status |
| `route-receipts.mjs` | No app process (loopback stub) | Employee-detail and hiring status contract: neutral load success, visible load-failure page status, and pending/error/ok orientation and hire receipts |
| `ci-organization.mjs` | `Control__Enabled=true`, disposable owner password, checked-in fake ACP fixture | Routed-page DOM isolation and grouped Organization nav, overview/directory department cards by stable ID, directory list styling, Operations department role/roster/availability/CTA, Development/QA empty states and CTA prefill, hiring `departmentId` preselect and role filtering with a forged value ignored, safe invalid/unknown ids, exact employee selection/detail, employee availability/runtime-state datasets, remote model placeholder, orientation mutation receipts, no secret field, desktop/mobile layout |

`ci-organization.mjs` copies `tests/HVO.AgentControl.Tests/Fixtures/fake_acp.py`
into a temporary directory with a `prompt_fast` scenario sidecar and points
`Control__OpenCodeExecutable` at it; it never uses the real OpenCode binary,
provider credentials, or model inference. It is deliberately separate from the
disabled smoke so the disabled baseline keeps its exact coverage.

```bash
# from the repository root, after the .NET build
dotnet build HVO.AgentControl.slnx --no-restore -c Release   # or the app test step
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install --with-deps chromium
npm run ci-all --prefix tests/Browser            # smoke + wire + state-kind + system drafts + receipts + organization
```

Artifacts are written to `artifacts/browser-ci/` and
`artifacts/browser-organization/` (results JSON, `app.log`, screenshots). The app
is always killed and the browser closed in `finally`, and each process exits
non-zero on any failure.

## Live operator suites (intentional, not run in CI)

These talk to a real running container and are **not** hermetic. They require a
loopback tunnel to the deployed instance and the `home-docker` Docker context
(override with `DOCKER_CONTEXT`); they deliberately read the owner password at
runtime and must only be run by an operator against a disposable deployment.
They are not run by `npm run ci` and must not run in CI.

| Script | Purpose |
| --- | --- |
| `model-sync.mjs` | UI-only model dropdown test against a local stub server (hermetic, no container). |
| `portal-layout.mjs` | Read-only layout regression against a live deployment. |
| `portal-smoke.mjs` | Full live exact-employee portal smoke (authenticates the organization API, selects the host-owned employee, verifies detail telemetry/terminal, then submits a prompt). `run-portal-smoke.sh` opens the tunnel. |
| `model-sync-live.mjs` | Live model-sync contract in both directions; restores the original model. |

`CHROME_PATH` is optional everywhere. When unset, the Chromium bundled by the
pinned `playwright` version is used.
