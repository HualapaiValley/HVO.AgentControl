# Changelog

Notable changes to HVO.AgentControl are recorded here. Entries follow the spirit
of [Keep a Changelog](https://keepachangelog.com/). Release headings must match
the single version source, `<Version>` in `Directory.Build.props`; the current
portal release is `0.1.0` and is **unreleased**.

## Unreleased

Changes after the first portal release will be collected here.

## [0.1.0] - unreleased

First portal release in preparation. **Not tagged or published as a release.**
Local development images have been built and tested. Feature status below
reflects active code, not a shipped release artifact.

### Added

- Version metadata and badges, refreshed architecture/roadmap, and a canonical
  external-issue tracker including our OpenCode selector feature request.
- CI build/test/format, container build, hermetic Playwright browser checks,
  and test-result artifacts. Opt-in GHCR release workflow, not yet executed.
- .NET 10 Blazor portal using static server rendering, owner Basic
  authentication from a mounted password file, and an embedded xterm.js
  terminal.
- `AcpControlHost` background service in the same control container: one
  OpenCode ACP process plus its loopback-only native HTTP server.
- Optional attach TUI in tmux on the same runtime; the browser terminal
  attaches over a same-origin authenticated WebSocket, one viewer at a time.
- Durable organization/session identity at `/data/runtime.json` and a private
  persistent `/data` volume for workspace and native conversation state.
- Portal endpoints: `/`, `/api/info`, `/api/control`, `/api/control/model`,
  `/api/control/cancel`, `/terminal`, `/health/live`, `/api/version`.
- Bounded ACP cancellation request and same-origin checks for cancellation and
  model selection.
- Docker packaging (OpenCode 1.18.30, `linux/amd64`) and trusted-LAN publishing
  on `0.0.0.0:5054`; the host runtime is disabled by default outside Compose.
- Sanitized ACP stderr buffering, child-environment hardening, permission
  policy and runtime persistence with automated tests.

### Known limits

- Developer provisioning, task dispatch/routing and the full organization
  lifecycle are **not implemented**.
- No worker bridge or reconnect implementation exists in the active code; this
  was demonstrated only by the standalone POC.
- Model selection is not bidirectional. On OpenCode 1.18.30 the attached TUI
  picker is client-local, so the portal shows the observed server session model
  and disables its selector (`modelSyncSupported = false`).
- No TLS. HTTP Basic credentials and terminal traffic are unencrypted on the
  LAN; use SSH-tunneled access elsewhere. TLS-proxy forwarded-header handling
  is not yet supported.
- The controller and OpenCode share a container OS user; deny rules are not OS
  isolation. No Docker socket or real repository is mounted in this slice.
