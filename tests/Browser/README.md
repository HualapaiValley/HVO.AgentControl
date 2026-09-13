# Browser checks (`tests/Browser`)

Playwright checks for the AgentControl V2 portal. The package is portability-first:
the browser binary is either the one bundled by the pinned `playwright`
dependency or an explicit `CHROME_PATH`; no machine paths are hardcoded.

## Hermetic CI (`ci-smoke.mjs`)

`ci-smoke.mjs` is the only suite CI runs. It spawns the locally built app
(`src/HVO.AgentControl/bin/Release/net10.0/HVO.AgentControl.dll`) on a free
loopback port with `Control__Enabled=false` and no owner password, then drives
the real Blazor portal. It needs no Docker, model provider, or credentials and
performs no runtime mutations.

```bash
# from the repository root, after the .NET build
dotnet build HVO.AgentControl.slnx --no-restore -c Release   # or the app test step
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install --with-deps chromium
npm run ci --prefix tests/Browser
```

Artifacts (results JSON, `app.log`, desktop/mobile screenshots) are written to
`artifacts/browser-ci/`. The app is always killed and the browser closed in
`finally`, and the process exits non-zero on any failure.

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
| `portal-smoke.mjs` | Full live portal smoke (auth, terminal, prompt). `run-portal-smoke.sh` opens the tunnel. |
| `model-sync-live.mjs` | Live model-sync contract in both directions; restores the original model. |

`CHROME_PATH` is optional everywhere. When unset, the Chromium bundled by the
pinned `playwright` version is used.
