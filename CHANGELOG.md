# Changelog

Notable changes to HVO.AgentControl are recorded here. Entries follow the spirit
of [Keep a Changelog](https://keepachangelog.com/). Release headings must match
the single version source, `<Version>` in `Directory.Build.props`; the current
portal release is `0.1.0` and is **unreleased**.

## Unreleased

Changes after the first portal release are collected here.

### Added

- Controller/agent OS isolation inside the single control container (#240),
  the Section 6 prerequisite for #215. The controller runs as UID 1001 and owns
  `/control-data` (`0700`) and the owner secret; OpenCode, tmux, the TUI and the
  PTY bridge run as UID 1000 and own `/data/home` and `/data/workspace`.
  Orientation moves to the host-owned, agent-readable `/agent-config`.
  Agent-identity children are started by `src/launcher/agentcontrol-launch.c`, a
  setuid launcher (root:control, `4750`) exposing only the fixed `acp`, `tmux`,
  `pty` and `signal` operations, with the executable always one of four
  compiled-in paths and no parameter for a program, UID, capability set or
  environment block. It drops privileges irreversibly, sets `no_new_privs`,
  empties the capability bounding set, rebuilds the environment from an
  allow-list, closes inherited descriptors while still privileged, and re-checks
  the resolved working directory after `chdir` so a symlink inside `/data`
  cannot place the child outside the agent tree. The `tmux` operation forwards
  caller-supplied pane command vectors, so the controller can run a program of
  its choosing — but only ever as the agent UID; the guarantee is confinement,
  not an execution allow-list, and the code, docs and tests now say so. Cross-UID
  termination uses the verified `signal` operation; the controller reaps its own
  direct children and tini reaps orphaned descendants.
  `/data` is root-owned `0755` with agent-owned `home`/`workspace` beneath it, so
  the agent cannot rename a subdirectory and leave a symlink for the next root
  start; `src/container/prepare-layout.py` performs all startup ownership work
  through `O_NOFOLLOW` descriptors and `fchown`/`fchmod`, never a path, and fails
  the start before touching anything when an entry has been substituted.
  The entrypoint drops `CHOWN`/`DAC_OVERRIDE`/`FOWNER`/`FSETID`/`SETPCAP` from
  the capability bounding set as it drops to the controller, so the setuid
  launcher cannot regain them during its root phase; without `SETPCAP` it refuses
  to start. The agent account gets `/bin/bash` because tmux starts its server
  through the account's `default-shell` and `nologin` leaves "no server running";
  the controller keeps `nologin`.
  Real alternate-UID container tests cover launcher execution denial, private
  store/secret/orientation access (including rename and unlink), `/proc`
  environment and descriptor access, environment injection, termination and
  reaping, entrypoint idempotence and fail-closed startup, planted-symlink and
  hard-link escalation attempts against `/data`, `/data/home`, `/data/workspace`
  and the legacy runtime state, the controller/child capability bounding sets,
  the `/bin/sh`-as-agent confinement case, the login-shell requirement, the
  end-to-end rollback with diverged state, a rollback for a deployment that never
  had a legacy file, replayed rollbacks, the fail-closed roll-forward interlock
  and the recorded controller PID.
- `docs/DEVELOPMENT.md`: pinned toolchain (.NET SDK 10.0.400, target
  `net10.0`, Node 22, Playwright 1.63.0, Python 3.12, OpenCode 1.18.30, and the
  runtime image's `python3`/`tmux`), clean-machine build/test/browser/container
  and opt-in live-check commands, C# guidance, Blazor code-behind/scoped-style
  separation, and the API framework standard.
- Bounded PR process guardrails in `AGENTS.md`, `CONTRIBUTING.md` and the PR
  template: independent exact-SHA review (PR #208 Round 1 baseline `002e826`),
  full triage including owner comments, per-thread fix evidence,
  owner-approved linked-issue deferrals, a three-round review cap, CI as a
  necessary-but-insufficient gate, and owner-authorized merge only.

### Changed

- `scripts/init-secrets.py --revert-isolation` (#240) is the explicit rollback to
  the pre-isolation image. It **republishes the current controller-private
  runtime state** to `/data/runtime.json` and then hands it, `/data` and the
  owner secret to UID 1000. A metadata-only revert would have been wrong for the
  runtime state: the isolated controller writes only `/control-data/runtime.json`,
  so the legacy path holds the organization, session and tmux owner token as they
  were at adoption, and re-owning it would resume the old image on a dead session
  while silently discarding the isolated run. The republish is atomic within the
  data volume (`O_EXCL` temporary in the destination directory, `fchown`/`fchmod`
  before it is visible, `fsync`, `renameat` by directory descriptor, directory
  `fsync`) and refuses symlinked, hard-linked or unexpectedly owned paths. The
  owner secret and `/data` stay metadata-only, with the same `O_NOFOLLOW`/
  hard-link/inode guards as the forward migration. Source owners are validated
  before anything changes, including the already-reverted owners, so an
  interrupted run is repaired by re-running the identical command: the three
  volumes admit no cross-volume transaction, so the operation is documented and
  tested as replayable rather than atomic. A deployment with no controller-private
  state fails closed unless `--accept-missing-runtime-state` is given. It still
  refuses to run while the control container is up or its state cannot be
  determined, and is mutually exclusive with `--migrate-owner`.
- The byte-exact pre-isolation runtime state is preserved once, at first
  adoption, as `/control-data/runtime.pre-isolation.json` with SHA-256, size and
  provenance in a sibling `.meta.json` (#240). The rollback overwrites
  `/data/runtime.json`, so that path is no longer the historical record; the
  snapshot is never overwritten and is backfilled for deployments isolated before
  it existed.
- `scripts/init-secrets.py --resume-isolation --state-source legacy|private`
  (#240) is the roll-forward after a rollback. A rollback leaves two independent
  histories of the same organization and there is no automatic merge, so
  `--revert-isolation` records `/control-data/rollback.active` and the isolated
  entrypoint **fails closed** while it is unresolved rather than silently
  freezing the newer legacy state or discarding the isolated run. The resume is
  the only thing that clears the interlock, it cannot run without an explicit
  state choice, it preserves the losing state under a timestamped name instead of
  deleting it, and it returns the owner secret to UID 1001. A legacy file written
  outside isolation without a recorded rollback fails closed the same way.
- `scripts/init-secrets.py` derives the data/private volumes and the control
  container from `--project` (validated against the Compose naming rules) and
  takes `--secrets-volume`/`--image` overrides (#240), so a non-default Compose
  project can be operated without editing the script. Named volumes are verified
  to exist before any container runs, because `docker run` would otherwise create
  an empty one and report a confident success against state it never touched.
  README documents the stop/revert/start and stop/resume/start sequences.
- The entrypoint records the controller's PID in `/control-data/controller.pid`
  before `exec` (#240), and the capability-verification recipe in `compose.yaml`,
  the Dockerfile and the architecture doc now reads it. The previous
  `grep CapBnd /proc/1/status` advice inspected tini, which still holds the
  startup capabilities because the bounding set is reduced in the same `setpriv`
  call that execs the controller. `pgrep -f HVO.AgentControl.dll` is not a
  substitute either: the pattern appears in the inspecting command's own command
  line, so it reports a PID even with no controller running — asserted in the
  container suite rather than assumed.
- CI runs the alternate-UID isolation suite in the `docker` job against the image
  that job already built (`AGENTCONTROL_ISOLATION_IMAGE`) with
  `AGENTCONTROL_DOCKER_REQUIRED=1`, so an unavailable daemon or a missing image
  fails the job instead of silently skipping the suite, and the job asserts the
  executed test count afterwards.
- Runtime state moves from `/data/runtime.json` to the controller-private
  `/control-data/runtime.json` (#240), which is the single authoritative state
  while the isolated image runs. First start copies the legacy file;
  organization, session and conversation history are preserved. The controller
  never writes the legacy path afterwards, so it is a stale leftover rather than
  a live mirror, and it is reduced to root-only `0600` because that stale copy
  still carries the tmux owner token the agent must not read.
  `scripts/init-secrets.py --migrate-owner` re-owns an existing owner
  secret to UID 1001 as a metadata-only, reversible change that never reads,
  rotates or rewrites the credential. The container refuses to start while the
  agent identity can still read the secret. Compose adds the `control-private`
  volume and cannot set `no-new-privileges`, because that would disable the
  setuid launcher and return every agent process to the controller's identity;
  all other setuid binaries are stripped from the image.
- Update centralized ASP.NET Core testing/OpenAPI packages to `10.0.12` and
  Microsoft.NET.Test.Sdk to `18.10.0` (#222, superseding Dependabot #210).
- Update the active SDK and Docker build image together to .NET `10.0.401`
  (#221, superseding Dependabot #209). Retain supported Node 22 across Docker
  and CI; assess Node major upgrades separately.
- API errors now use RFC 9457 ProblemDetails; added protected OpenAPI JSON
  at `/openapi/v1.json` and runtime/TUI readiness at `/health/ready`.
- Every terminal subprocess now uses the shared credential environment filter;
  pane startup clears an existing tmux server's inherited environment too.
  Ambient provider keys are no longer passed through: configure authorized
  providers in the runtime's private OpenCode home, not controller environment.

### Fixed

- Resolve exact tmux session names to session IDs only for `set-option` and
  `show-options`, which reject the same exact-name syntax as `has-session`; all
  other commands keep the `=name` target so a restarted server that reuses a
  numeric id cannot redirect a write to an unrelated session. Add real private-
  socket tmux coverage for ownership, marker persistence and prefix isolation
  after deployment exposed the mocked-test gap (#238, #239).
- Stop writing executable ACP fixture files while tests run. Per-test symlinks
  and scenario sidecars use one build-copied script, avoiding Linux `ETXTBSY`
  from writable descriptors inherited by concurrent process starts (#232).
- Reject cancellation as soon as the control runtime reports an unavailable
  state, even while its ACP child is still being reaped. Degraded sessions keep
  their recovery controls (#228).
- Final full-review corrections: reject blank/short configured owner passwords
  regardless of runtime enablement; atomically initialize secrets and refuse
  damaged existing files without overwriting them.
- Keep terminal and cancellation controls available for established degraded
  sessions while preserving the bootstrap error and failed readiness signal.
  Bootstrap timeouts request bounded cancellation rather than silently orphaning work.
- Report unconfirmed model changes as uncertain, never as proof the selection
  is unchanged. Do not retry automatically.
- Require at least one second of shutdown grace; explicitly exclude archived
  Docker/Actions files from dependency updates.

- Round 3: retain a just-created TUI pane through marker-persistence or selection
  failures and retry those steps with backoff before advertising readiness.
  Retries do not create duplicate attach clients or replace unrelated panes.

- Round 2: readiness requires an attached terminal even when terminal launch is
  disabled. Exceptional ProblemDetails responses retain their media type,
  instance and trace ID when content negotiation declines the default writer.
- Track the exact owned TUI pane rather than any live pane; recover without
  destroying other windows or stealing the user's selection on healthy probes.
  Missing pane identity requires operator recovery rather than creating duplicates.
- Back off failed tmux probes and log terminal warnings only on error transitions.

- Require numeric ACP protocol version 1 before establishing a session.
- Enforce loopback-only native binding and consistent tmux name validation.
- Use the configured OpenCode executable for the attached TUI.
- Monitor TUI readiness and recover missing/dead owned tmux sessions with
  bounded restart backoff, without restarting native ACP or replacing foreign sessions.

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
  (The shared-user limit is addressed after this release; see Unreleased #240.)
