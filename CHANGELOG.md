# Changelog

Notable changes to HVO.AgentControl are recorded here. Entries follow the spirit
of [Keep a Changelog](https://keepachangelog.com/). Release headings must match
the single version source, `<Version>` in `Directory.Build.props`; the current
portal release is `0.1.0` and is **unreleased**.

## Unreleased

Changes after the first portal release are collected here.

### Fixed

- Completed #217 operational acceptance (2026-09-18) and updated capability truth. The first managed disposable two-host path was validated live on an authorized disposable topology: source `main` `c4a7966`, worker OpenCode `1.18.30`, Docker `29.8.0`, worker host `home-dev-02`, reached over pinned ED25519 strict SSH with an isolated labeled worker and exact latest images. The run exercised ACP initialize/new/load, anonymous provider prompt streaming to completion, a real tool side effect at exact `/workspace/agentcontrol-217-tool.txt` with bytes `TOOL217_OK\n` (SHA-256 `954cf…`), cancellation delivered on the same lease in ~2.5 s with the active request cleared, disconnect after a forwarded receipt, reconnect epoch advance, exact reconcile completion, idempotent resubmit without duplicate, stale-lease rejection after a competing owner epoch, bounded multi-generation replay (hermetic and live), worker restart advancing the process generation with no automatic resume and explicit load of the same native session, and viewer framing/auth/resize/marker plus detach/reconnect. Live permission handling covered `[once, always, reject]` and safe-reject `[reject]` with a same-lease reject pending→decided and prompt completion (compatibility fix PR #255). `/api/info` now reports `WorkerControlImplemented=true` and `WorkerControlOperationallyValidated=true`; `WorkerControlCodeAvailable=true` is unchanged and `WorkerControlEnabled` remains the deployment/configuration gate and is `false` in the deployment/default configuration. These flags cover only the first managed disposable two-host path, not key rotation/compromise re-enrollment or production managed hiring/provisioning. Cancellation limitation: OpenCode reported the cancelled turn completed, so cancellation cleared the active request but was not a rollback. Cleanup was exact — zero labeled containers, volumes or tags remained and the local key was removed. The `home-docker` control portal was deployed with a separate schema-v7 UI and is irrelevant to worker flags except portal inspection. #219 approval/provisioning still requires discussion and owner approval even though its #217 dependency is now satisfied.
- Fixed the live remote-worker submit deadlock discovered operationally in #217. The worker now durably registers a prompt, writes its ACP frame, and returns `forwarded` immediately while a bridge-owned background operation continues to await and journal the terminal response. The prompt lock and active-request binding remain held through completion, duplicate request IDs return the current durable state without another ACP write, and a write failure before the `forwarded` receipt still returns the fixed uncertain category and closes the owner stream, while uncertainty observed after receipt is retained in the worker's durable request state and learned by later reconciliation instead of closing the live connection. Because the authenticated bridge reader is no longer blocked on prompt completion, the same controller connection can read status, forward cancellation, and answer a pending permission while the prompt remains active; that later synchronization reconciles forwarded controller requests to the worker's completed, failed, or uncertain state, clearing the request recovery obligation on an exact terminal completion or failure. Reconciliation is keyed by request ID and process generation rather than the current connection lease epoch, so a request durably stored under epoch 1 can complete after reconnecting under epoch 2 while its remote correlation still retains epoch 1. If the worker can never establish the outcome, control schema v6 adds `request-uncertain` to the hash-only recovery audit and permits an owner to clear only a valid marker-bearing controller obligation with external SHA-256 evidence.
- Corrected the remote-worker network contract after live operation (`network none` left the worker unable to reach the approved anonymous model, faulting the prompt with `-32603`). The typed `ContainerCreate` command now attaches controller-provisioned worker containers to Docker's fixed default `bridge` network (`WorkerControlOptions.ContainerNetworkMode`) so the approved model provider is reachable, while still publishing no ingress. That default bridge grants unrestricted outbound egress: the public Internet through the daemon's NAT, the Docker host's bridge gateway and any host services listening there, and any other container co-attached to the same bridge. The guarantee is only ingress-free and in-container: the fixed command contains no `-p`, `--publish` or `--expose`, `PortBindings` is empty, the ACP/TUI HTTP listener remains on container loopback, and the supervisor socket is a container-private path. This is not network isolation or egress restriction; a per-worker dedicated network or a host firewall/NAT policy that restricts egress to the approved provider remains future work. The checked-in hermetic `worker` Compose profile deliberately keeps `network_mode: none` because it is not a live provider runtime.
- Fixed the live #217 permission compatibility defect discovered operationally with pinned OpenCode 1.18.30. With `permission` set to `ask` for `read`/`edit`/`bash`, the pinned runtime emits generic option IDs `once`, `always`, and `reject`, not the older `allow_once`/`reject_once` suffixed IDs. Full-object selection now permits only exact fixed IDs `reject_once`, `reject`, and `reject_always` with compatible absent/exact reject kinds and priority in that order; allow, unknown, malformed, and contradictory kinds are ineligible, reject kinds can no longer authorize arbitrary vendor IDs, and a repeated option ID is vetoed entirely even when every occurrence was individually eligible so a duplicate allow/reject collision cannot widen the allowlist. The worker stores offered IDs separately from a canonical safe-reject list in a versioned object inside the unchanged pending-permission JSON column, validates bridge decisions only against that safe list, and projects both views to the controller; legacy arrays retain diagnostics but have no safe evidence and fail closed. Unbound callbacks additionally exclude the `reject_always` ID and kind. Bound owner decisions use only the authoritative safe list after validating the exact request/session/lease/turn tuple. If that list is empty, the permission remains held and returns a dedicated sanitized 409 without exposing offered IDs. The prior vendor fallback was intentionally removed and the supported pinned shape is recorded in `docs/ARCHITECTURE.md`.
- Fixed the production remote-worker ACP startup handshake discovered during live operation. The worker now starts its ACP reader, sends a correlated protocol-version-1 `initialize` request with no filesystem or terminal capability claims, validates the exact negotiated version, and leaves the authenticated recovery bridge available with a protocol-failed status if initialization cannot be confirmed. Base controller synchronization always persists status, replay and recovery even when ACP is exited or uninitialized; only provisioning and work operations require an initialized running exact session. Provisioning creates a fresh session through the fixed lease-fenced `new-session` mutation (`cwd=/workspace`, no MCP servers), records the returned native identity transactionally in the authoritative organization store, and safely reconciles a worker-bound session after a controller database failure. Recorded sessions use the equally fixed `load-session` mutation. The worker journals session creation/loading as none, creating/loading, uncertain or bound; a timeout, transport loss, cancellation after write, invalid response, or ownership change marks a `session-create-uncertain` hold and is never blindly retried. Existing exact schema-v7 worker journals are migrated to v9 only after a create-once verified `/control/bridge.schema-v7.db` backup and SHA-256 evidence, preserving requests, events and lease identity; unknown signatures fail closed. Control schema v5 migrates only the exact released v4 signature after a verified create-once `control.schema-v4.db` backup and SHA-256 evidence, preserving recovery obligations and audit rows while adding `session-reconciliation` to both CHECK constraints. Session divergence is retained as a bounded `session-reconciliation` marker requiring audited external evidence, permission rejection is bound back to the exact durable request and active session, and cancellation remains available during permission, replay, recovery and manual dispatch holds while still requiring the exact initialized running session, process generation and ownership epoch. Worker transport and uncertain outcomes map to a sanitized 502; only the fixed remote `worker-request-rejected` code maps to 409, while generic local protocol invariants remain sanitized 500 failures.

### Changed

- Adopted the HualapaiValley development model for changes to this repository (2026-09-18): `development/v1` is the default daily-integration branch under a fast required gate (`Development v1 / Preflight` hosted; `Development v1 / Build and Unit` on the organization's self-hosted `hvo-linux-x64` runners: restore, warning-clean build, format, hermetic test selection); `main` is the stable line, moved only by the nightly promotion PR that `promote-main.yml` opens as `hvo-agentcontrol[bot]` and the operator merges with a merge commit after `V2 CI` passes. Independent reviews, finding threads and `VERIFIED_*` replies are posted as the bot through `agentcontrol.yml` (`post-review`, `post-finding`, `reply-thread`) from dispatch inputs bound to the exact head SHA; review text is never committed to the PR branch. `docs/REVIEW-PROTOCOL.md` is now the v1 review rulebook (replacing the policy-lane protocol used through PR #262), with `docs/development-v1.md`, `docs/runbooks/pull-request-walkthrough.md` and `docs/runbooks/repository-setup.md`; issue and PR templates, `scripts/review:v1` and the `review:*`/`risk:*`/`workflow:*` labels were added. 133 V1-era branches were archived to an operator-held bundle and deleted. This is repository process, not the managed-employee review the product implements.
- Repository transferred to `HualapaiValley/HVO.AgentControl` (2026-09-18) and made public; branch protection, secret scanning/push protection and Dependabot security updates were re-applied after the move. In-repo references (CI badge, GHCR image name `ghcr.io/hualapaivalley/hvo.agentcontrol` in the never-dispatched release workflow, sibling HVO repository links, repository-admin notes) now point at the organization. No image has ever been published under the old name. GitHub Actions are pinned to full-length commit SHAs (with version comments) to satisfy the organization's actions policy; the resolved versions are unchanged (`checkout` v7.0.1, `setup-dotnet` v6.0.0, `setup-node` v7.0.0, `setup-python` v7.0.0, `upload-artifact` v7.0.1, `setup-buildx-action` v4.4.1, `build-push-action` v7.4.0, `login-action` v4.6.0).

### Added

- Per-host profile image builds (#259, second slice of epic #257). Control schema v9 migrates only the exact released v8 signature after a verified create-once `control.schema-v8.db` plus SHA-256 evidence and adds `profile_builds` (`WITHOUT ROWID`, identity columns immutable, never deleted or replaced, one live and one verified build per revision/host). A revision is rendered to a deterministic build context — one generated `Dockerfile` in a fixed-metadata tar whose SHA-256 is the recorded context hash; the owner's fragment first, allowlisted devcontainer features as fixed apt recipes (nothing fetched from ghcr.io), `containerEnv` as `ENV`, lifecycle commands as labels, and a fixed trailer that re-asserts the worker contract and entrypoint — and streamed to `docker build -` on the approved host over the pinned SSH path with `--pull=false --no-cache`, no build arguments/secrets/cache/context, `--network none` unless a recipe needs apt, and controller labels; the exact base digest is first pinned under the controller-owned `agentcontrol-worker-base:pin-<12hex>` tag because BuildKit refuses an image id as a `FROM`. A build is `built`/verified only after `docker image inspect` proves the labels, the supervisor entrypoint, no user/ports/volumes, the platform and the base-layer prefix, and a fixed verification program (base64-carried controller constant) run in the **approved base** with the candidate mounted read-only — never executing anything the candidate supplies — proves the two accounts with fixed homes/shells, the 0700 private directories and their owners, root-owned `/app` and supervisor, contract artifacts identical in content and uid/gid/mode (the `env`/`sh`/`python3` launch chain, supervisor, `/app`, `dotnet`, node/opencode, the Python standard library and the loader/shared-library tree with no base file altered, pinned `ld.so.conf`/`nsswitch.conf`, an `ld.so.cache` whose base SONAMEs keep exactly the base's ordered entries including hwcap variants, directory modes preserved throughout the pinned trees), no `ld.so.preload`, no python shadowing, no setuid files, no file capabilities and no Docker socket; contract failure is `rejected`, host/build failure `failed`, transport loss, cancellation or a controller restart mid-build `uncertain` and reconciled by inspecting the tag rather than rebuilding (a second POST resumes the live row). Network is granted only to controller-owned apt recipes, never to a fragment; the dotnet feature installs the repository's pinned SDK side by side so the worker's runtime stays untouched. Approved digests are now per host — the configured base plus verified builds on that host — enforced by the container-create builder; the key bootstrap always runs the configured base; `POST /api/workers/enroll/plan` accepts an optional `imageDigest` from that set and freezes it into the enrollment, whose resources then carry `agentcontrol.profile`. Owner routes `GET/POST /api/profiles/{id}/revisions/{revisionId}/builds`; `/profiles/{id}` shows per-host builds and a build action. `WorkerControl:ImageBuildTimeoutSeconds` (default 900) bounds a build. Building never provisions an employee. The real-Docker contract test builds the seeded `generic-employee` through the controller's own argv, proves the verifier accepts it and the toolchain is present, and proves a matrix of 28 hostile fragments (setuid, replaced python3/env/supervisor/worker dll/dotnet/opencode/shared library, mode/ownership changes on artifacts and libraries, python shadowing, `ld.so.preload`, loader configuration and `ldconfig` cache redirection, file capabilities, account change; a replaced shell, loader path or unexecutable dotnet fails the build itself; a setuid bit or `/app` chown is repaired by the trailer and accepted) behaves as specified. A build id has one in-flight owner per process, so a concurrent request is refused instead of racing the row.
- Immutable container profiles (#258, first slice of epic #257 completing #219). Control schema v8 migrates only the exact released v7 signature after a verified create-once `control.schema-v7.db` plus SHA-256 evidence and adds `container_profiles` (mutable label: slug, display name, description, `active|retired`, optimistic revision, optional idempotency key) and `container_profile_revisions` (append-only, `UNIQUE (profile_id, revision_number)` and `UNIQUE (profile_id, content_hash)`, `build_status` reserved as `unbuilt|building|built|failed|rejected`, both `WITHOUT ROWID`, with signature-bound triggers that abort content updates, deletes, conflicting inserts and identity/slug-collision updates so no SQLite conflict-resolution form can replace a row), and rebuilds `hire_requests` once with a nullable `container_profile_revision_id` reference (existing hires migrate with `NULL`). A revision is a validated, canonicalized devcontainer subset plus an optional Dockerfile fragment that may only extend the symbolic `agentcontrol-worker-base`; the closed allowlist (`name`, `image`/`build.dockerfile`, allowlisted `features`/`containerEnv`/`remoteEnv`, `postCreateCommand`/`postStartCommand`, `customizations.agentcontrol`) rejects every placement/mount/port/privilege/user/entrypoint key, unknown keys, duplicate keys, non-strict JSON, oversized input and forbidden Dockerfile instructions/flags/heredocs with an actionable reason; fragment checks run on the joined logical instruction so hazards split across backslash continuations are still caught. Fresh stores and the v7→v8 migration seed `generic-employee` revision 1 idempotently. Owner routes `GET/POST /api/profiles`, `GET /api/profiles/{id}`, `GET/POST /api/profiles/{id}/revisions` (revision-bound, identical content refused; create replay stays bound to revision 1) and `POST /api/profiles/{id}/retire`, with same-origin checks and RFC 9457 errors; portal pages `/profiles` and `/profiles/{id}` are routed SSR pages with friendly not-found/invalid-id states and a truthful 405 `Allow: GET`. Recording a profile does not build an image, approve a hire, create an employee or rebuild an existing employee.
- At that revision, the first owner-facing #219 slice split the static SSR portal into real authenticated routes (`/organization`, `/employees`, `/employees/{id}`, `/hiring`, and `/system`) under a persistent responsive layout; route responses no longer shipped every page section and hidden behind hash navigation. The all-employee directory supported search and department/availability filters, stable detail links, and a clear “Request employee” path. Exact employee detail alone loaded orientation actions and terminal/model controls. Control schema v7 migrated only the exact frozen v6 signature after a verified create-once `control.schema-v6.db` plus SHA-256 evidence, adding durable idempotent hire requests and append-only hash-only events. Same-origin owner APIs supported create/list/get and revision-bound rejection with bounded purpose/name/resources and organization/department/role relationship validation. Approval was deliberately absent and the UI truthfully disabled it: at that revision #217 two-host operational acceptance had not yet completed and was the stated prerequisite for approval, provisioning, orientation, or employee creation. The organization section was a real hierarchy: `/organization` rendered cards linking each department by stable ID, `/organization/departments` was the authoritative directory, and `/organization/departments/{id}` showed the department revision, persisted standing instructions, assigned roles, scoped roster with availability badges, availability counts, scoped failures, an empty-state message, and a `Request employee` path that preselects the exact department in `/hiring?departmentId=<id>`. `GET /api/departments/{id}` was owner-authenticated and applied the shared stable-id validation: invalid ids were `400`, unknown ids `404`, and a forged department/role relationship was still rejected by the create endpoint. The shared navigation grouped Overview and Departments under Organization with a server-computed parent active state. This entry is superseded by the 2026-09-18 #217 operational acceptance entry above: the #217 dependency is now satisfied, and the current gate is #219 discussion and owner approval. Approval action and provisioning remain unimplemented.

- Hermetic controller foundation for #217. Control schema v4 migrates only the exact released v3 signature after a verified create-once SQLite backup and SHA-256 evidence, preserving organization and #213/#216 policy data while adding stable execution-host, worker enrollment/cursor/task/request/cancellation, provisioning/resource, recovery-obligation, bounded pending-permission and terminal-viewer metadata without key bytes or raw active nonces. Configured-reference owner host APIs, disabled-by-default `WorkerControlOptions`, strict fixed-vector SSH/Docker command construction, typed operation boundaries, inspect-label ownership checks, bounded host-probe parsing, race-resistant controller-private file opening, exact local known_hosts fingerprinting, transparent no-key worker piping, strict authentication/result parsing and shared worker/controller protocol framing/canonical-HMAC code are covered hermetically. At that revision `/api/info` kept `WorkerControlImplemented=false` and separately reported code availability versus operational validation; this was superseded by the 2026-09-18 acceptance entry above. No SSH/Docker host, live enrollment, provider inference or tool side effect was used in that hermetic change. The remote route/protocol, pending-permission projection and production worker PTY backend were hermetically exercised at that revision; two-host portal terminal evidence was pending authorization at that time and was later covered by the 2026-09-18 acceptance entry above.

- Independent worker-owned ACP bridge/runtime artifact (#213). A separate .NET worker executable and Docker `worker` target use a fixed-operation root PID1 supervisor, bridge UID 1101 and employee UID 1102 with private persistent control/home/workspace/session trees. Bootstrap consumes an exact disposable 32-byte key only on stdin, atomically creates a single-link `0600` key, verifies duplicate enrollment and refuses overwrite/symlink/hard-link paths. The bounded `hvo-worker-acp/1` NDJSON channel performs role-labelled mutual HMAC over independent nonces and exact identities/version/key ID, checks Linux peer credentials, rejects replay/expiry/reflection, and grants a bridge-owned epoch/connection-nonce lease. A separate exact-signature schema-v4 SQLite journal uses WAL/FULL/foreign keys/busy timeout and persists worker/process generations, request hashes and forwarding uncertainty, retained multi-generation replay events with lexicographic generation/sequence ACKs/gaps/holds, process state, generation-bound pending permissions and durable cancellation intents. Every operation is fenced by its authenticated socket lease; mutations also require matching epoch/nonce fields. Pinned permission callbacks are bound only to the host-owned active prompt/turn/process/session context, use host-generated `perm:` IDs and retain only hashes plus offered `optionId` values. Cancellation and permission response writes survive connector disconnect, record forwarded versus uncertain delivery, and are never blindly retried. A validated persistent `0600` lock inode is held by nonblocking kernel `flock`, so only a live bridge blocks startup; ACP EOF/read failure fails pending correlators with host-authored errors, reconciles active forwarding requests to uncertain, releases prompt ownership and holds dispatch until a fresh child start is acknowledged. The optional worker Compose profile has no published port or Docker socket, uses no network (`network_mode: none`) and disables restart. At that revision the control portal remained unintegrated and reported `WorkerControlImplemented=false`; SSH/two-host provisioning, key rotation, viewer/TUI transport, hiring/tasks/UI and live resources remained future work. This was superseded by later #217 slices and the 2026-09-18 operational acceptance entry above.

- At that revision, owner organization navigation and exact employee diagnostics (#218) exposed Overview, Operations, Development, QA and System configuration views backed only by the authoritative store plus the current host status. Deterministic host-availability categories, explicit unsupported/empty pending approvals, actionable orientation/runtime failures, safe employee detail that exposes only the current sanitized runtime error, and unsupported recent logs (no employee-scoped safe log contract) avoided implying worker lifecycle or leaking logs/secrets. `/api/organization/portal` and `/api/employees/{id}` provided the owner read models. Terminal attachment required a bounded stable `employeeId` and failed closed unless employee, runtime binding and native session exactly matched the host-owned status; the browser disconnected on selection changes and never fell back. Hiring, workers, tasks and approval workflow remained unimplemented at that revision; this entry was superseded by the acceptance entry above.
- Versioned orientation and host-owned permission policy (#216). Schema v3 safely chains exact canonical v1 → v2 → v3 migrations with immutable source-bound backups and SHA-256 evidence at each boundary. Durable records hold revisioned organization/department/role/employee fragments and facts, layered restrictions, attempt-preserving assignments/evidence, dispatch holds, staged scoped grants, permission requests and sanitized audit. Composition uses real LF canonical UTF-8 Markdown, preserves the informational no-Fleet/no-workers/no-code role, excludes task text, applies deny accumulation, and atomically publishes a controller-owned single-link `orientation-current.md`. Reverting A → B → A, rotating sessions, or recovering from Rejected/TimedOut/Failed creates a fresh current assignment while retaining stale history; status remains readable and manually holdable when only a Stale row exists. Evidence, timeout and failure transitions bind the immutable assignment ID in addition to employee/session/version/revision. `Delivered` records host verification of publication and persists the runtime generation that must load it; live recomposition leaves `policy-update`/`orientation-reload-required` holds and `restartRequired=true` until startup of the affected OpenCode process confirms that generation loaded the exact artifact. Comprehension cannot clear this barrier. Live comprehension captures ordered agent-message chunks synchronously in ACP codec-reader order before the lossy observation channel, detects bounded overflow, uses the adjacent result as a completion barrier, and serializes all prompt operations because ACP exposes no turn ID. Timeout and caller cancellation request bounded remote cancellation and fence retry until request completion or runtime restart; malformed, empty, oversized, non-terminal and ACP-error host runs persist Failed/TimedOut outcomes with `live-model` provenance, while caller-submitted malformed owner evidence remains non-mutating validation. Owner evidence remains an explicit `owner-submitted` override. Role standing instructions are mutable through revision-guarded `PUT /api/roles/{id}/instructions`, with the role fragment as the authoritative record; updates preserve IDs/session/history and require affected-runtime reload without an image rebuild. OpenCode routes broad bash/read/edit classes through `ask`: this means ask the owner through the host-controlled permission prompt, never infer approval from natural language. The pinned ACP callback lacks a host-derived canonical resource, so inbound permissions always reject/cancel; requests persist directly as rejected, audits retain every matched restriction ID, and owner grants are validated and audited as staged, non-executable records. Linux native artifact stat validation is restricted to x64 and fails closed elsewhere. Publication failures explicitly report that assignment persistence succeeded but delivery did not advance. No live inference, deployment, worker, Finance or task dispatcher was added.
- Portable CLIProxy managed-employee inference seam (#243). A commit-curated,
  sanitized policy-lane catalog (18 lanes) is separated from a deterministic
  control exposure profile (`agentcontrol-control-phase1-v1`) that generates only
  the directly selectable lanes plus step/tool-bounded, read-only task agents; the proxy's
  internal fallback source aliases and `auto` are never exposed, and no implicit
  review agent exists. The generated direct `@ai-sdk/openai-compatible` provider
  references `{env:CLIPROXY_API_KEY}` and carries an explicit `reasoningEffort`
  on every advertised variant. The controller reads an absolute secret file
  immediately before start, exports the value only to the OpenCode child (tmux
  and the PTY bridge never receive it, and the privileged launcher forwards it
  only for the `acp` operation), and faults before process start without Big
  Pickle fallback when configuration is missing or invalid. Schema v2 records
  only non-secret provider metadata (credential set, config/profile/catalog
  version, policy lane, requested provider/model/variant) with a create-once v1
  backup and transactional migration; no key or fingerprint is stored.
  `scripts/init-secrets.py --provision-cliproxy-key` creates or rotates the named
  AgentControl key from stdin without printing it, fails closed on hostile paths
  and while the control container runs, and preserves the inode on unchanged
  input. Compose selects `cliproxy/default` at medium; deployment awaits a
  reviewed merge and operator key provisioning. Evaluated and rejected the
  dashboard config sync, Oh-My plugin set, MCP injection and dynamic discovery;
  the lightweight community provider plugin is recommended only for general
  interactive OpenCode clients, not managed employee authority.
- Authoritative SQLite organization store (#215). `/control-data/control.db`
  (controller UID, `0600` in the `0700` private volume) is the single source of
  truth for organization, department, role, employee, runtime-binding and
  ACP-session identity. A fresh store is seeded in one transaction with exactly
  Operations, Development and QA; one combined Operations/IT role and one adopted
  Operations employee bound to the existing internal shared container; no fake
  Development/QA employees and no Finance. The existing `runtime.json`
  `organizationId`, persisted display-name difference, `sessionId`,
  `sessionTitle` and `tmuxOwnerToken` are adopted unchanged and the difference is
  reported rather than renamed. The adoption audit records the source identity,
  owner authorization reference and timestamp. Stable IDs are generated once and
  never derived from a slug, process or container. Schema version, expected table
  shape, `PRAGMA quick_check`, foreign keys, WAL, `synchronous=FULL`, a busy
  timeout and a bounded cross-process single-writer lock guard the store; a
  malformed, partial, corrupt, unknown or newer database faults the host instead
  of being reseeded or reset. Optimistic revisions protect the owner-facing
  rename. `Microsoft.Data.Sqlite` is centrally pinned to `10.0.12`, matching the
  other 10.0 servicing packages. The owner-protected, same-origin
  `GET /api/organization` overview and `PATCH /api/organization` rename, plus a
  minimal portal overview panel, read only from this store. Unit coverage proves
  restart idempotence, duplicate slug/id and invalid-reference rejection, rename
  stability, optimistic conflicts, failed-write non-mutation, baseline adoption
  without duplicates and malformed, newer, corrupt, partial and organization-less
  stores failing closed; a real alternate-UID container test proves the
  database-era rollback is refused byte- and inode-exact.
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
  leaves the child with empty permitted and effective capability sets, rebuilds
  the environment from an
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
  had a legacy file, replayed rollbacks, the replay refusal once the old image has
  advanced the legacy state (asserting byte, hash and inode preservation), the
  crash boundaries of an interrupted first adoption (identical-bytes completion,
  evidence backfill and corrupted-evidence refusal), the recorded reconciliation
  marker for an unrecorded divergence, the fail-closed roll-forward interlock and
  the recorded controller PID.
- `docs/DEVELOPMENT.md`: pinned toolchain (.NET SDK 10.0.401, target
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

- The controller no longer writes `runtime.json` after cutover (#215): the
  SQLite store is authoritative before OpenCode starts, session changes are
  persisted transactionally before the bootstrap prompt, and a persisted
  organization display name is never overwritten from configuration. The
  existing file and the `runtime.pre-isolation.json` snapshot remain as
  evidence. `scripts/init-secrets.py --revert-isolation` now fails closed before
  any write or ownership hand-back when `control.db` exists, because the
  pre-isolation image cannot read the database and would resume stale JSON
  session identity; `prepare-layout.py` treats a JSON-only divergence as
  evidence when the database is present. No dual-write compatibility authority
  is introduced.
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
- The rollback replay is now **bounded by the recorded marker**, which closes a
  data-loss path (#240). Replay was only ever safe while `/data/runtime.json`
  still held what the rollback published; once the pre-isolation image had run
  and advanced it, re-running the identical command republished the older
  controller-private bytes over the newer legacy state — destroying exactly the
  work the roll-forward interlock exists to protect. `--revert-isolation` now
  validates `rollback.active` (regular file, controller-owned, parsed digests)
  against the bytes on the legacy path **before its first write or chown**: an
  unchanged or already-converged legacy file is a replay and proceeds, and an
  already-converged one re-applies metadata only instead of churning the inode.
  Anything else is refused with both digests, nothing is modified, and the
  operator is routed to `--resume-isolation`. A marker that cannot be parsed
  fails closed. Covered by a real container regression that reverts, mutates the
  legacy state as UID 1000, re-runs the revert, and asserts byte, hash and inode
  preservation before resuming.
- The rollback marker is now durable **before the runtime state is republished**,
  not only before the ownership hand-backs (#240). Ordering the `chown` calls
  after the marker left the data-loss path open: the republish creates a new
  inode and renames it over `/data/runtime.json`, and it gave that inode its
  `1000:1000` ownership at creation time, so **publishing was itself an ownership
  hand-back that ran before the marker existed**. A crash in between left a legacy
  state the pre-isolation image could read and advance with no record of it, and
  the next replay — finding no marker — treated the advanced state as the ordinary
  first rollback and overwrote it. The order is now decide → record the intent
  marker → publish **root-owned `0600`** → hand back explicitly → complete the
  marker, so until the hand-back runs the old image cannot read a state the
  rollback has not finished committing to. A marker that cannot be published now
  aborts with the legacy state keeping its prior owner *and* its prior bytes.
- The rollback marker carries a **schema with three digests** so a replay
  classifies rather than guesses (#240): `prepublicationLegacySha256` (what the
  legacy path held before this rollback), `intendedSha256` (what it will publish)
  and `publishedSha256` (what it holds now, null while `phase` is `"intent"`).
  A legacy file matching the pre-publication digest means the publication never
  ran and is performed; one matching the intended digest means it already landed,
  so the inode is not replaced and only the remaining hand-backs are re-applied;
  anything else is a legacy state another writer advanced and is refused with all
  three digests before the first write or chown. Both markers also carry
  `schema: 2` and a `reason`/`phase` pair. `--accept-missing-runtime-state`
  records the marker too, so that path is bounded as well.
- `--revert-isolation` distinguishes the two marker kinds by **`reason`,
  explicitly** (#240). `prepare-layout.py`'s `unrecorded-legacy-divergence` marker
  records a divergence that was only detected — nothing was written to the legacy
  path — so it now records null `prepublicationLegacySha256`, `intendedSha256` and
  `publishedSha256`, and the rollback branches on the reason before it looks at
  any digest. Inferring a publication from the recorded diverged bytes would
  republish the private state over exactly the bytes the operator is rolling back
  to keep. A diverged legacy file that has itself moved on since detection is
  refused rather than resolved unilaterally.
- The rollback hands back **the published inode, never the stale descriptor**
  (#240). A republish replaces the inode, so the descriptor opened during
  validation refers to a superseded one; chowning it would give UID 1000 an
  unreachable orphan while the file the old image actually opens stayed
  root-owned — a rollback reporting success while leaving the old image unable to
  read its own state. The published name is re-opened by directory descriptor
  under the same no-follow/hard-link/owner rules, its bytes re-verified against
  the digest just published, and only that descriptor is handed back.
  Covered by container regressions that inject a marker-publication failure and
  assert the runtime state's owner, bytes and inode are all unchanged and
  unreadable by UID 1000; that crash at each side of the root-owned publish and
  assert the replay classifies and converges; and that pin the superseded inode
  through an open descriptor to prove it was not the one handed back. Plus
  source-order assertions that need no daemon.
- Resume archives are published **create-never-replace** (#240). The
  `runtime.superseded-*`/`runtime.rolled-back-*` name carries a one-second
  timestamp, so two resumes inside one second — or after a clock step backwards —
  chose the same name and the `os.replace` silently destroyed the first archive
  while reporting that both states were preserved. The bytes are now written to a
  private temporary and hard-linked into the first unused name, so `EEXIST` makes
  the free-name test and the claim on it one atomic step and two concurrent
  resumes keep two archives. Covered by a container regression that pins the
  timestamp, runs two rollback/resume cycles, and asserts both archives exist with
  their exact bytes, ownership, mode and link count.
- Publication temporaries use a random suffix instead of the process id
  (`scripts/init-secrets.py` and `src/container/prepare-layout.py`, #240). A
  container restart reuses low PIDs, so a temporary left behind by an interrupted
  run made the `O_EXCL` create fail on exactly the same name at every later
  attempt — a permanent start or rollback failure repairable only by hand — and
  none of this code may unlink or truncate an entry it did not create.
- An unrecorded divergence now **records a reconciliation marker before failing**
  (#240). `prepare-layout.py` refused to start when the legacy state was owned by
  UID 1000 while controller-private state existed, but recorded nothing — and
  `--resume-isolation` is driven by that marker, so every later start failed
  identically with nothing for the operator to resolve. It now publishes a marker
  carrying `reason: unrecorded-legacy-divergence`, both SHA-256 digests and sizes,
  the detected legacy owner, and an explicit null `publishedSha256` (nothing was
  republished, so a later replay must not validate against a publication that is
  not on disk). Rolling back out of that state is supported and keeps the diverged
  legacy bytes untouched rather than overwriting them.
- **Interrupted first adoption is resumable** (#240). Adoption publishes the
  private copy before it records the snapshot and before it re-owns the legacy
  file, so a crash in between left a UID 1000 legacy file that was byte-identical
  to the private copy — which the previous owner-only check reported as a
  divergence written outside isolation, permanently refusing to boot and demanding
  a choice between two copies of the same state. Identical bytes are now treated
  as an unfinished adoption and the remaining steps are completed; only differing
  bytes are a divergence. A snapshot without evidence has its evidence generated
  from the snapshot's own bytes (the snapshot is never rewritten to make them
  agree), the snapshot is validated as a controller-owned regular file on every
  start, and evidence that disagrees with it on hash or size fails the start
  instead of standing as a false historical record.
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
  outside isolation without a recorded rollback fails closed the same way. The
  resume validates the marker inode it consumes (regular, controller-owned, no
  symlink at the final component) rather than only testing for its existence; the
  recorded body is advisory, because the explicit `--state-source` choice — not a
  recorded digest — decides which state survives.
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
  fails the job instead of silently skipping the suite. The job then asserts the
  **exact** TRX counters — 42 discovered, 42 executed, 42 passed and 0
  `notExecuted` — rather than a lower bound, because a lower bound accepts a
  suite that quietly lost coverage and a skip reports as "0 failed". The 42 are
  40 real alternate-UID container test cases plus 2 structural source/Compose
  wiring tests that need no daemon; only the 40 prove the kernel-enforced
  boundary, and the step records that distinction.
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

- Strict worker replay now treats the current-generation status sequence as a
  lower-bound pass target: a valid crossing page is committed and ACKed without a
  false recovery hold, while a controller cursor already beyond the target before
  replay remains divergence. If replay ends early, authoritative exact worker
  gap/loss markers are preferred; unavailable status or conflicting/invalid status
  projection attempts a durable marker-less protocol obligation before faulting.
- First adoption of `control.db` is now atomic and can never leave an empty or
  header-only authoritative file. A fresh store is seeded in a uniquely named
  controller-private temporary, `wal_checkpoint(TRUNCATE)`-ed, closed, cleared
  from the connection pool, restricted to `0600` and only then renamed to
  `control.db`; a crash before publication leaves no authoritative path, and the
  temporary created by that attempt is the only thing cleaned up. An existing
  zero-length or header-only `control.db` fails closed and is never reseeded.
  Existing-runtime adoption first retains the exact input bytes as controller-only
  `runtime.pre-database.json` with verified create-once SHA-256 evidence; a
  conflicting backup or digest blocks adoption before `control.db` is published.
  Covered by a deterministic publication fault seam proving no authoritative
  path appears before a committed, validated seed.
- The database, its WAL/SHM sidecars and the writer lock are controller-only
  `0600`: the controller narrows its process creation mask (`umask 0077` in the
  entrypoint and `RestrictProcessFileCreation` in the host) and the store applies
  explicit sidecar modes, so a widened parent directory would not expose them.
  Unit tests assert the actual sidecar modes; the real alternate-UID container
  suite asserts the controller mask, the created sidecar modes and agent denial.
- `prepare-layout.py` labels the database-era JSON divergence with truthful
  provenance (`database-era-json-divergence`) instead of claiming it was
  recorded, and the real container test asserts the label appears exactly once:
  a byte-identical legacy/private pair never emits it.
- `RecordSession` demotes the prior active session in the same transaction; the
  schema constrains `acp_sessions.status` and a partial unique index allows only
  one active session per employee. Tests assert the single-active invariant and
  both constraints.
- `OrganizationStore`/identity publication on `AcpControlHost` is synchronized
  under the host gate so a request never observes a half-published store with a
  stale identity. Added enabled-runtime API integration tests (fake ACP,
  temporary database) covering GET 200, successful PATCH, 409, 404,
  ProblemDetails, owner auth, same-origin and the absence of secret fields.
- The seeded organization text, role profiles and employee
  purpose/instructions/rules/restrictions are asserted exactly, as is the
  `owner-approved:issue-211` adoption reference whose source is documented on
  the option; the uniqueness and foreign-key tests now populate every NOT NULL
  column so they assert the intended constraint rather than a missing value.
- Docs now scope the WAL claim precisely: WAL/`synchronous=FULL`, foreign keys,
  the bounded single-writer lock and the `0600` modes are configured and tested
  locally and in the container, while crash durability on the real volume
  filesystem is explicitly **not** claimed as tested.

- Bound the terminal bridge teardown so a failed cross-UID termination can no
  longer strand `/terminal` permanently (#240). `StopBridgeAsync` waited on
  `CancellationToken.None` after issuing the forced termination, and that wait
  runs inside the request's `finally` ahead of the single-viewer semaphore
  release — so a launcher that refused the privileged signal (the controller
  cannot signal its UID 1000 children directly) or that reported success while
  the child survived hung the request and returned 409 to every later viewer
  until the controller restarted. The forced result is now honoured: a refused
  termination logs a category-only warning and returns without waiting for an
  exit nobody requested, an issued one is awaited under a bounded second grace
  and logs on timeout, and the `Process` handle is released on every path.
  Covered by deterministic tests that drive a real SIGTERM-ignoring child with a
  real `AgentProcessLauncher` whose signal helper is scripted to refuse, swallow
  or deliver; they reproduce the unbounded wait when the fix is reverted.
- Reuse the checked-in fake tmux fixture in `AcpControlHostTests`, which still
  wrote and then exec'd a per-test executable and so kept the #232/#233
  `ETXTBSY` race. The scripted stand-in is now the single shared `FakeTmux`
  helper over `Fixtures/fake-tmux.py`, and the `docker` stub in
  `SecretInitializationTests` moves to `Fixtures/fake-docker.sh` for the same
  reason. `TestFixtureHygieneTests` makes the rule structural: no test source
  may set an execute bit on a file it wrote or embed a script body, and every
  canonical fixture must reach the output directory executable.
- Resolve exact tmux session names to session IDs only for `set-option` and
  `show-options`, which reject the same exact-name syntax as `has-session`; all
  other commands keep the `=name` target so a restarted server that reuses a
  numeric id cannot redirect a write to an unrelated session. Add real private-
  socket tmux coverage for ownership, marker persistence and prefix isolation
  after deployment exposed the mocked-test gap (#238, #239).
- Stop writing executable ACP fixture files while tests run. Per-test symlinks
  and scenario sidecars use one build-copied script, avoiding Linux `ETXTBSY`
  from writable descriptors inherited by concurrent process starts (#232).
- Apply that same fix to the fake tmux binary in `TmuxAttachLauncherTests`, which
  still wrote and then exec'd a per-test executable and so kept the #232 race.
  It surfaced as a rare first-`EnsureAsync` failure during a full-suite run and
  did not reproduce when the class ran alone. Measured directly: exec'ing a
  freshly written executable under a parallel `Process.Start` load fails with
  errno 26 in roughly 2% of starts, while exec'ing a symlink to a never-written
  canonical file fails in 0 of 400. The script moves to
  `Fixtures/fake-tmux.py` and each test symlinks to it, deriving its private
  data directory from the invoked path.
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
