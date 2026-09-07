# Initial release validation record

Executed 2026-09-06 in the repository dev container. SDK: 10.0.400. Docker daemon: 29.7.2. Remote fixtures: Ubuntu 24.04 Linux x64, OpenSSH 9.6p1, tmux 3.4. Native OpenCode: 1.18.29; provider/model: `opencode/big-pickle` using its advertised anonymous access. Production credentials were not used.

## Final repository checks

All commands exited 0 after the final source changes:

```bash
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
bash -n scripts/init-local.sh tests/Fixtures/start.sh tests/Fixtures/native-probe.sh
docker compose config --quiet
```

Build output: `Build succeeded. 0 Warning(s). 0 Error(s).` Restore reported all projects up-to-date. Format verification produced no changes or errors.

## Automated tests

The fixtures were started with `./tests/Fixtures/start.sh`. These runs completed successfully:

```bash
HVO_SSH_FIXTURES=1 HVO_NATIVE_FIXTURE=1 \
dotnet test HVO.AgentControl.slnx --no-build --configuration Release \
  --logger 'trx;LogFileName=initial-release.trx'
# 11 passed, 0 failed, 0 skipped; 2.3988 minutes.

HVO_SSH_FIXTURES=1 \
dotnet test HVO.AgentControl.slnx --no-build --configuration Release \
  --logger 'trx;LogFileName=release-regression.trx'
# After reply-resolution and SSE-watchdog refinements:
# 9 passed, 0 failed, 2 native tests explicitly skipped; 52.7265 seconds.

HVO_SSH_FIXTURES=1 \
dotnet test HVO.AgentControl.slnx --no-build --configuration Release \
  --filter 'FullyQualifiedName~ForwardingBootstrap|FullyQualifiedName~PersistenceTests' \
  --logger 'console;verbosity=normal'
# After the final canonical bootstrap-path check:
# 5 passed, 0 failed; 6.3635 seconds.
```

TRX files remain in ignored `tests/HVO.AgentControl.Tests/TestResults/`. Local console logs are `/tmp/hvo-final-tests.log`, `/tmp/hvo-regression-tests.log`, and `/tmp/hvo-final-targeted.log`. Native tests include installation, actual schema verification, repeated owned-server reuse, deliberate native server restart with session recovery, three concurrent workspaces across two SSH endpoints, SSH loss during a running tool, backend restart, and a same-conversation follow-up. [Retained real-provider evidence](native-live-2026-09-06.json).

## Browser checks

Node 22.22.0, Playwright 1.58.2 and Chromium were installed for validation. The application used ignored `.fixture/browser-data` and `.fixture/secrets`, listening on `127.0.0.1:5054` with the documented local-HTTP setting. Both runs exited 0:

```bash
HVO_PLAYWRIGHT="$PWD/.fixture/browser/node_modules/@playwright/test" \
/tmp/hvo-node/bin/node tests/Browser/smoke.cjs

HVO_PLAYWRIGHT="$PWD/.fixture/browser/node_modules/@playwright/test" \
/tmp/hvo-node/bin/node tests/Browser/native-smoke.cjs
```

The first exercised sign-in, runtime bootstrap, workspace model inspection, task/follow-up queue, question reply, page close/reopen, a second client, safe untrusted transcript rendering, and desktop/mobile screenshots using explicitly simulated responses. The second used the real provider and passed two permission approvals, one question reply, file validation, follow-up and reload. The final file contained `BROWSER_NATIVE_135eeb9668` and `FOLLOWUP` on separate lines. [Retained native browser evidence](native-browser-2026-09-06.json).

Console logs: `/tmp/hvo-browser-final.log`, `/tmp/hvo-native-browser-final.log`. Screenshots remain in ignored `artifacts/browser/`. Normal reproduction uses `npm ci --prefix tests/Browser` and `node` as shown in the README; the temporary tool paths above describe this session.

## Docker package

`docker build -t hvo-agentcontrol:local .` succeeded after the final source changes. Final image ID:

```text
sha256:25b717e8674c1429549d2200295b6d6d24385ffa9511b99e1d2ad1e4219e4ec5
```

The disposable `hvo-agentcontrol-package-check` container ran that exact image with named data and read-only secret volumes. Before and after `docker restart`:

- `GET /health/ready` returned 200 and `{"status":"ready","scope":"control-plane database"}`.
- Anonymous `GET /api/v1/snapshot` returned 401.
- The existing `/data/agentcontrol.db` and `/data/keys/key-*.xml` were present.
- The process ran as UID/GID 1000, with `/data` mode 700.

The initial manual file-presence probe used the wrong filename `control.db` and was corrected to the configured `agentcontrol.db`; this was a probe error, not a startup failure. Final build log: `/tmp/hvo-final-docker.log`.

## Remaining scope

No known failing checks remain. macOS and Linux arm64 live execution were unavailable; their installer branches are implemented with pinned checksums. No owner-supplied production host/provider or public deployment was exercised. Hosted CI was configured but not run. M6 remains deferred. The two SSH fixtures, package-check container, local browser-test application and standalone native contract probe were stopped after validation; their disposable data/evidence remains available locally.
