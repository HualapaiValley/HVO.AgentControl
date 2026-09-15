# Phase 1 Organization, Runtime Placement and Control Contracts (Proposed)

**Status: proposed design, not implemented.** The new contracts below extend,
rather than describe, the active baseline. They are the working set needed for
Phase 1 (#211 / #212). It is deliberately narrower than an enterprise model:
no final role split, no multi-process runtime framework, and no provisioning in
this slice. Every item is a proposal for owner review; none is owner-accepted
until explicitly recorded on the issue.

The active baseline is one control container, one OpenCode ACP process and one
combined role (`AcpControlHost` + `AgentControlOpenCodeConfig.RoleName`). The
existing runtime identity, session and tmux ownership must be preserved, not
reset.

Since controller/agent isolation (#240) the authoritative runtime state is
`/control-data/runtime.json`, not `/data/runtime.json`; the latter is a stale
copy while the isolated image runs, and the byte-exact pre-isolation original is
preserved separately as `/control-data/runtime.pre-isolation.json` with hash
evidence. Read `runtime.json` below as the authoritative controller-private file.

## 1. Identity model

Stable, generated IDs are the durable keys. Slugs and display names are mutable
labels and are never used as identity.

| Record | Stable id | Mutable labels | Notes |
| --- | --- | --- | --- |
| Organization | `org-<hex>` | `slug`, `displayName` | Mirrors existing `runtime.json` id format. |
| Department | `dept-<hex>` | `slug`, `displayName` | Belongs to exactly one organization. |
| Role | `role-<hex>` | `slug`, `displayName`, profile refs | Belongs to one department; holds instruction + permission profile refs. |
| Employee | `emp-<hex>` | `slug`, `displayName` | Binds org + department + role + one runtime binding. |
| Runtime binding | `rtb-<hex>` | placement, refs, epoch | Separates durable identity from where it runs. |
| Runtime process | `proc-<hex>` | native PID, generation, status | One supervised OpenCode process slot; PID is observation, not identity. |
| ACP session | `acps-<hex>` | native session ID, title, status | Employee-owned conversation; native ID is returned by OpenCode. |

- IDs are generated once and never reused or re-slugged.
- A runtime binding records `placement` (`InternalSharedContainer` or
  `DeveloperContainer`), the container/volume/session/home/workspace
  references, and the last observed bridge ownership epoch (Section 7). The
  controller does not allocate a competing worker fencing counter.
- Current cardinality is 1 employee : 1 runtime binding; the separate record
  exists so placement can be replaced without changing employee identity.
- Explicit mapping: employee -> binding -> process slot and employee-owned ACP
  session. One process may host several sessions only under the isolation gate
  below; a session is attached to at most one live process generation. A process
  restart keeps its slot ID but increments generation and changes PID; loading
  persisted conversation keeps the session IDs. Explicit new conversation creates
  a new session record. Placement replacement creates a new binding/process slot
  and never changes employee identity or silently selects different history.

## 2. Initial organization and employees

- One organization: display name **AgentControl Development**, slug
  **`agentcontrol-development`**, adopting the existing persisted
  `organizationId`. The name is the existing development display name from the
  PR #208 deployment; the slug is a new development default, not a field claimed
  to exist in the old JSON. The owner accepted development naming latitude on
  2026-09-13 and authorized bounded work on #211. Import preserves any actual
  persisted display-name difference and reports it rather than silently renaming.
- Adoption is deterministic: the existing `runtime.json` `organizationId`,
  `sessionId`, `sessionTitle` and `tmuxOwnerToken` are imported unchanged into
  the new records, so the existing session and tmux pane keep working.
- At inception, seed **one combined Operations / IT employee** in Operations,
  bound to the existing control runtime.
- This seed adopts the existing owner-approved PR #208 control runtime, not an
  unapproved new hire. Persist an adoption audit record with source identity,
  owner authorization reference and timestamp; do not forge a historical hire
  transition. For a fresh disposable organization, the authorized test/bootstrap
  request explicitly approves this one internal seed and records that approval
  before binding it. Neither case approves subsequent development hires.
- **Development** and **QA** departments initially exist but are **empty**.
  The first Development employee is provisioned later in Phase 1 through #219,
  not by organization initialization or this documentation slice.
- Finance is not created; it is a future internal department.

## 3. Storage

**Status: implemented for the control host (#215), with the remaining
organization-breadth items still proposed.** The initial store, adoption, seed,
validation and fail-closed startup below are live in the active code; the wider
Phase 1 lifecycle that consumes it is not.

Chosen: **one small SQLite database file** owned by the C# host at
`/control-data/control.db` (controller-private, `0600` in a `0700` directory).

- Single writer: the host process. A cross-process file lock plus
  `busy_timeout` makes a second writer fail closed rather than interleave.
- WAL mode, foreign keys on, `synchronous=FULL` on every writer connection for
  all writes, including the durability-critical hire/task records.
- Rationale: a hire writes employee + binding + orientation assignment
  together; SQLite transactions give atomic multi-record commits and real
  crash semantics. A rewrite of one JSON document is smaller but must reimplement
  atomicity, partial-write detection and journaling. The package addition must
  be justified explicitly in the implementation PR.
- Schema version is recorded in a `schema_version` table. Schema v2 adds only
  non-secret runtime-binding provider metadata: credential-set ID, provider
  config/profile/status/version, catalog version, policy lane, and requested
  provider/model/variant. It stores no endpoint, key, account or key
  fingerprint. Startup validates the exact canonical v1 signature
  before the only supported migration (1 -> 2), creates and verifies a
  create-once SQLite Backup API snapshot plus SHA-256 evidence, migrates in one
  transaction, and validates the canonical v2 signature afterward. Unknown,
  newer and partial shapes fail closed. The backup is recovery evidence, never a
  second authority. Startup also checks `PRAGMA quick_check`/foreign keys. A
  failed check or unexpected table shape **faults the host** — it never creates,
  resets or "repairs" the store.
- Data directory `0700`, database file `0600`.

### 3.1 Safe baseline adoption, no V1 migration

- Create a verified backup before adoption. Only an explicitly new database may
  import `runtime.json`; an initialized store missing its organization is corrupt,
  not an invitation to import or seed again. Commit identity, initial records,
  schema version and adoption marker in one transaction. Re-running after a
  committed adoption validates matching identity without creating new employees.
- `runtime.json` is **left in place** as rollback evidence; it is not deleted
  and not silently reinterpreted. After cutover the database is the single
  authoritative store. Note that "left in place" means the controller-private
  file plus the `runtime.pre-isolation.json` snapshot: the isolation rollback
  (#240) overwrites `/data/runtime.json` with the current state, so that path is
  not durable evidence of any particular point in time.
- The pre-upgrade JSON is a snapshot, not a live backward-compatible store.
  Rollback must restore the matching backup, not run old code against stale
  session identity after new activity. Backup/restore includes WAL state through
  SQLite backup or a quiesced checkpoint, not a live copy of the database alone.
  The implemented first-adoption backup is the byte-exact
  `/control-data/runtime.pre-database.json` plus create-once SHA-256 evidence;
  both are durable inputs/evidence, not a second live authority. Database-era
  operational backups remain quiesced whole-volume/SQLite backups.
  **Implemented decision (#215):** the pre-isolation image cannot read `control.db`,
  so `--revert-isolation` fails closed before changing anything once the database
  exists instead of offering a JSON-only downgrade; recovery is a verified restore
  of the whole controller-private volume. This avoids a dual-write compatibility
  authority.
- Archived V1 state is never read or migrated. V2 starts from its own store.

### 3.2 Crash-uncertain writes

- **Hire:** one transaction commits the requested/approved employee, its binding
  and its orientation assignment. A crash before commit leaves no partial
  employee. Re-running an idempotent transition reconciles to the same records.
- **Task dispatch:** the dispatch intent and its identity are committed
  **before** any bytes are written to the bridge. A crash between commit and
  bridge ACK leaves the task `Uncertain`; it is never blindly retried. Recovery
  reconciles by querying the runtime for the dispatch id and any observable
  effect before deciding.
- An uncertain write is never treated as success and never retried without
  reconciliation (consistent with the existing "receipt is not execution" rule).

## 4. Authority and trusted identity

- The **C# host is the sole authority** for identity, placement, provisioning,
  orientation, dispatch and reconciliation. The Operations employee may
  **request** actions; it never becomes the authorization boundary.
- The owner approves the initial hire. Approval is a host-recorded transition,
  not a model assertion.
- Requests are bound to a **trusted identity** (authenticated owner or an
  employee-scoped request channel issued by the host). Role, department or
  permission claims carried in a JSON prompt payload or model message are
  untrusted data and never grant authority.
- Permissions are computed from persisted records keyed by stable employee id,
  never from a caller-supplied role string.

## 5. Internal role placement

- Future Ops, IT and Finance roles live in the **same AgentControl container**.
  Finance is future-only: there are no Finance department, employee, runtime or
  usage records now.
  Two acceptable tiers, in order:
  1. Separate ACP sessions in one OpenCode process, **only where per-session
     configuration isolation has been proven** by a test, not assumed.
  2. Otherwise separate OpenCode processes in the same container, each with its
     own private home/XDG config directories and native port.
- **No one-container-per-internal-role.** Idle internal roles must not each hold
  a container.
- Separate **developer** employees are self-contained containers with independent
  home, repository and runtime/session storage.
- The role split itself is later work. This phase does not claim per-session
  config isolation is proven, and does not claim a co-located internal process
  survives replacement of its own container.
- Conversation and task history belong to the stable employee and session IDs,
  not a selected UI tab or current working directory. The host must reject a
  session/history lookup through another employee binding. A shared-process
  option requires both configuration and history access-isolation tests, including
  model tools/native HTTP; host routing alone is insufficient. If these fail,
  use distinct per-role process identities and private home/history directories
  in the same container. Retain history on restart; new sessions are explicit.
- The shared-process gate also requires cross-employee credential-isolation tests
  covering private stores, environment, inherited descriptors, provider credentials
  and native HTTP credentials. Failure of any configuration, history or credential
  isolation test requires distinct per-role process identities and private stores
  in the same container, not host routing alone. Section 6 applies to every role.
- The host and managed employees use one global AgentControl inference key; the
  general interactive OpenCode key is separate. Future Finance usage reporting
  is attributed only by correlating proxy usage with AgentControl-persisted
  employee, runtime, session, task and request IDs. A shared-key aggregate alone
  cannot attribute usage to Finance or any other employee.

## 6. Credential and process isolation prerequisite

**Before** any privileged provisioning authority is added, the baseline
same-UID credential access must be fixed. The original baseline ran the
controller, OpenCode and the mounted owner secret under UID 1000; prompt/tool
deny rules are defense-in-depth, not OS isolation, and another executable could
read the secret.

**Control-host status: implemented (#240).** The control container now runs a
controller identity (UID 1001) owning `/control-data` (`0700`) and the owner
secret, and a separate agent identity (UID 1000) owning the OpenCode home,
workspace, tmux, TUI and PTY bridge. Orientation is host-owned and
agent-readable at `/agent-config`. A setuid launcher (root:control, `4750`,
not agent-executable) provides only registered `acp`, `tmux`, `pty` and `signal`
operations, drops privileges irreversibly, sets `no_new_privs`, leaves the child
with empty permitted and effective capability sets, and rebuilds the child
environment from an allow-list. The child's capability *bounding* set is bounded
by the entrypoint (`SETUID | SETGID | KILL`), not by the launcher, which cannot
shrink it once `CAP_SETPCAP` is gone.
`/data` itself is root-owned so the agent cannot substitute a subdirectory for
the root entrypoint to act on, and the entrypoint removes
`CHOWN`/`DAC_OVERRIDE`/`FOWNER` from the capability bounding set before the
controller starts. See
[architecture](ARCHITECTURE.md#controller-and-agent-identities-implemented).

This is **identity confinement, not an execution allow-list.** The launcher's
`tmux` operation forwards caller-supplied pane command vectors, so a compromised
controller can execute a program of its choosing — including a shell — as the
agent UID. What it cannot do is run anything as root, as the controller UID, or
with access to `/control-data`, the owner secret or the launcher binary. Do not
describe the registered-operation surface as preventing code execution.

The **worker-image half below remains future work**, as does per-role UID
allocation for additional internal roles.

Concrete separation inside the same container:

- Distinct OS identities: a controller UID owning the database and owner secret,
  and one separate unprivileged agent/OpenCode UID for the initial combined
  Operations/IT role. *(Implemented for the control host in #240: UID 1001 and
  UID 1000 respectively.)* Future process-per-role placement allocates a distinct UID
  and private `0700` store per internal role in the same container. Shared-process
  placement is allowed only after all Section 5 isolation gates pass.
- The agent private store is owned by the agent UID, mode `0700`. A narrow
  runtime supervisor provides launch, orientation installation, inspection and
  termination for registered bindings; it does not expose arbitrary commands or
  filesystem paths to a model. The unprivileged controller uses this channel
  rather than granting its own identity to the OpenCode child.
- The owner-secret directory and authoritative store are readable only by the
  controller UID. The secret mount exists in the shared container namespace but
  is inaccessible to the agent UID. Protect directory traversal, files, process
  environments, inherited descriptors and supervisor endpoints; deny rules alone
  are insufficient. Test real alternate-executable access attempts.
- Child processes start with explicit modes/`umask`; no ambient credential
  inheritance beyond the current filtering.
- **No broad Docker authorization is exposed to any LLM or agent process.** No
  Docker socket, `DOCKER_HOST`, or docker-group membership enters an
  agent-owned process. Any Docker/provisioning access is a narrowly scoped
  broker used by the controller only.
- Apply the same separation to the worker image: bridge-private key, socket and
  journal directories owned by a dedicated bridge UID with mode `0700` and key
  files `0600`; OpenCode/TUI/tools run as a different unprivileged employee UID.
  A persistent minimal privileged worker supervisor owns employee-UID child
  launch, stop and restart, including ACP, TUI and viewer clients. It accepts only
  registered binding operations from the authenticated bridge UID over a private
  local channel, never arbitrary executable, UID, environment or path parameters.
  It establishes child identities and strips their capabilities and inherited
  privileged descriptors before execution; employee children cannot invoke the
  supervisor. The supervisor retains only lifecycle privileges it needs; the
  bridge has no root/setuid capability. ACP stdio descriptors are handed to the
  bridge, not to the connector. The bridge persists a new process generation
  before requesting an ACP process-slot start; the supervisor binds that
  generation to the owned child and reports its PID/exit as observations only.
  TUI/viewer starts receive separate opaque supervisor lifecycle handles and do
  not advance ACP generation or invalidate pending permissions. Uncertain start
  acknowledgment is reconciled against the still-live supervisor's owned child
  handle, never a bare reusable PID and never blindly relaunched.
  The persistent supervisor is the worker container's PID 1, responsible for
  reaping children. Its failure terminates the container and remaining processes
  in that private PID namespace; no replacement supervisor adopts old PIDs.
  Disable automatic container restart for Phase 1. The host marks work interrupted
  or uncertain, holds dispatch, observes container termination and reconciles
  effects before an explicit restart. A new container process lifetime starts
  with no surviving children; durable bridge state advances generations and
  invalidates old permissions/leases. Worker restart does not resume tool stacks.
  This is deterministic worker infrastructure, not another full controller or
  model agent.
  The connector runs as the bridge UID and connects to the bridge socket, but
  does not own or launch ACP. Validate real UID, descriptor, `/proc` and private
  path access from alternate employee executables before enrollment.

This is a gating prerequisite for Phase 1 provisioning, not an optional
hardening item.

## 7. Private remote transport

Proposed topology for the disposable phase test:

- **Host A: `home-docker`** — existing AgentControl host. Existing
  infrastructure is never destroyed.
- **Host B: `home-dev-02`** — disposable worker host for this phase.

Design constraints:

- No worker ports are published. No full second controller and no LLM relay on
  B. The worker needs only a deterministic worker-owned bridge.
- C# on A reaches the remote Docker daemon over **SSH with pinned host keys**
  (verified `known_hosts`; `StrictHostKeyChecking=no` is forbidden) and pinned,
  agent-restricted key material. Key custody/rotation is an open item.
- The controller launches a **short-lived connector** from C# that attaches to a
  **worker-owned bridge UNIX socket**. The connector does not exec an
  ACP process owned by the controller; the bridge is started and owned by the
  worker and owns the ACP stdio channel provided by its local supervisor.
- The connector opens a bridge-owned UNIX socket inaccessible to the worker
  OpenCode UID. Bridge and connector perform a versioned mutual HMAC challenge
  over independent random nonces, controller/worker IDs and protocol version,
  using a per-worker random key stored only in controller and bridge-private
  files. Role-specific challenge labels prevent reflection; reject reused or
  expired nonces and compare MACs in constant time. The SSH tunnel authenticates
  the remote host and protects bytes. This key is a scoped newly provisioned
  worker's internal control credential, not a provider or infrastructure grant.
  It is never exposed to worker tools or supplied as a command argument.
- During provisioning, C# generates the key and persists its protected copy
  before enrollment. Deliver it over authenticated SSH/Docker exec stdin to a
  fixed bootstrap operation running as the bridge UID, writing atomically into
  the private bridge volume before starting OpenCode. Do not use Docker inspect-
  visible environment variables, image layers, logs or command arguments. A
  duplicate bootstrap verifies the existing key ID rather than overwriting it;
  this rule applies only to initial enrollment.
  Rotation is an owner-approved maintenance operation: hold dispatch, reconcile
  active work, stop bridge access, replace both copies through this out-of-band
  channel, advance ownership epoch and reauthenticate. Rotation uses a distinct
  conditional replace operation carrying expected current and new key IDs under
  the separately authenticated SSH/Docker maintenance channel, not the rotating
  bridge key. Persist the operation ID and protected new key before either write;
  each side atomically records the new key and ID. Repeating with the new ID is a
  verified no-op; an expected-old-ID match permits replacement, and any other ID
  fails closed for owner reconciliation/re-enrollment. Resume the recorded
  operation to finish an interrupted two-copy replacement, then verify both sides
  and obtain the new lease before clearing the maintenance hold. An interrupted rotation
  stays held; reject old keys for bridge authentication, with no automatic
  dual-key fallback. Expected-old-ID replacement is allowed only through the
  separately authenticated maintenance channel. Compromise requires
  revoking access and explicit re-enrollment, not trusting a request signed only
  with the compromised key. Validate key delivery/rotation before claiming them
  implemented; loss of the key cannot be repaired by reading worker model data.
- The bridge durably allocates monotonically increasing ownership epochs in its
  own store, transactionally with a controller ID and connection nonce. Every
  mutation carries that epoch and nonce. Only an authenticated matching controller
  may reconnect; replacing its live connection advances the epoch and fences the
  old one. Different controller identity needs explicit enrollment approval.
  Heartbeats every 5 seconds maintain a 20-second monotonic lease. Lease expiry
  holds new dispatch, not the running tool; a restarted bridge invalidates all
  prior leases and requires reauthentication. Timing values are testable options.
- Counter ownership is explicit: the bridge database owns the authoritative
  lease epoch; the binding stores only its last observed value. The bridge also
  increments a durable worker generation at every bridge start; events use
  `(workerGeneration, sequence)` with sequence increasing within that generation.
  The OpenCode process slot has a separate durable process generation, advanced
  at each ACP process-slot start even if the bridge did not restart, never for
  a TUI/viewer client. A bridge restart may therefore advance both generations.
  Controller restart itself changes no
  worker counter. Its subsequent successful lease acquisition advances the
  bridge-owned ownership epoch, but not worker/process generations; it records
  the returned epoch during reconciliation. New terminal viewers reuse the
  active lease rather than fencing the dispatch connection.
- Before acknowledging a request the bridge durably stores its ID and payload
  hash; the same ID/hash queries the recorded outcome and never repeats an ACP
  prompt, while a changed hash is rejected. Record forwarding intent before ACP
  write; a crash in the write/response gap is `Uncertain`, not retry-safe.
- Events are ordered by persisted worker generation and sequence. The controller
  acknowledges only after its own transaction commits the event/cursor. Replay
  starts after that cursor; duplicates are ignored by generation/sequence. Bound
  retained replay to 64 MiB and 10,000 events initially. An overflow or missing
  cursor returns an explicit replay gap, holds dispatch and requires status/effect
  reconciliation, never truncates silently or drops request/permission state.
- **Future worker-bridge contract:** status includes session/process generations,
  active request, pending permission, lease, replay bounds and hold state. Pending
  permission and decisions bind the employee session, OpenCode process generation,
  originating turn/request and decision ID. A generation change invalidates pending
  permissions; never replay decisions into a replacement child or a vanished turn.
  Conflicting repeats fail closed. Automatic model retry cannot resolve an uncertain
  permission response. The current co-located controller callback does not implement
  this pending lifecycle; it responds synchronously and persists `rejected`.
- **Future worker-bridge contract:** a pending permission request survives
  controller disconnect and is re-delivered only if the original child generation
  and turn are still live. Otherwise report invalidated/interrupted status and
  require explicit recovery.
- A **worker crash does not resume** the in-flight turn; only durable state is
  reloaded. Loading history is not resuming a command.
- The C#-owned SSH credential may use an already-authorized development Docker
  account for this bounded test, with its actual root-equivalent authority
  disclosed. A dedicated host allowlist, typed operation validation, approved
  image/digest/resource limits, no privileged/host namespaces/host bind mounts,
  no arbitrary exec, and identity-checked resource removal constrain the host
  provisioning adapter. This is not daemon-enforced least privilege. Never give
  that credential or raw adapter to a model. The credential may enter the control
  container only after Section 6 isolation is implemented and tested; until then
  keep it outside agent-accessible identities and enable no provisioning authority.
  If existing authorization/access is
  insufficient, stop for a new grant; a daemon-side rootless/restricted service is
  a separately approved hardening option, not an assumed existing service.
- Terminal traffic uses a separate SSH/Docker connector to the exact registered
  worker's private bridge socket. Authenticate it as a `viewer` channel, not
  a controller lease-acquisition/reconnect operation. C# supplies the active
  controller/worker IDs, epoch and connection nonce through protected connector
  stdin; bind these fields and the viewer channel role into the existing mutual
  challenge. The bridge requires an exact match to its live lease, never issues a
  new lease for this channel, and closes viewer channels when that lease expires
  or is replaced. A viewer cannot dispatch ACP prompts or mutate ownership via
  the bridge protocol. Authorized owner keystrokes retain the TUI behavior below.
  After authentication the bridge asks its supervisor to launch only an
  employee-UID attach client for the bound TUI/session. It accepts bounded
  input/resize frames, streams bytes to the authenticated same-origin WebSocket,
  and enforces one viewer per session. Detach asks the supervisor to stop/reap
  only that viewer's lifecycle handle; the bound TUI and ACP remain unaffected.
  The connector never launches an arbitrary command, alternate session or
  fallback shell; this
  is a launch-routing guarantee, not a claim that keystrokes cannot execute code.
  An authenticated owner using an interactive tmux/TUI may execute employee tools
  and tmux commands under the unprivileged employee UID. Do not market frame
  bounds as a sandbox; process/credential isolation remains the security boundary.
  If a future non-owner read-only view is added, it needs separately enforced
  read-only input semantics and cannot inherit this owner-interactive endpoint.
- Before provisioning, probe and persist Docker API/daemon version, Linux OS and
  architecture, selected image platform, usable non-shared volume storage/free
  space, memory/CPU capacity and enforceable limits, SSH host identity, and required
  image tools (OpenCode pin, bridge, tmux, PTY support). Reject incompatible or
  unknown mandatory capabilities before creation. Hardware access is not granted
  merely because a host advertises it.
- Disposable workers default to `opencode/big-pickle` only when that anonymous
  capability is explicitly selected, already measured with no injected provider
  credentials in the standalone POC. A task/role selecting `cliproxy/*` requires
  the committed policy-lane catalog, fixed endpoint and distinct AgentControl
  inference secret file; validation failure makes only that required runtime
  unavailable before process start and never substitutes Big Pickle. CLIProxy
  aliases record requested policy, not actual serving identity. The Phase 1
  shared key has deployment-wide revocation collateral; see
  [CLIProxy model policy](CLIPROXY-MODEL-POLICY.md). Do not copy workstation or
  coordinator CLIProxy credentials into workers. Provider failure is explicit;
  a paid/authenticated provider or new infrastructure credential requires owner
  direction, not silent fallback.
- Docker Desktop Mac hosts run Linux containers; treat them as Linux execution,
  not native macOS.

## 8. Exact TUI mapping

- One TUI per runtime binding, attached to **that binding's exact ACP session**.
- The tmux session name is derived deterministically from the stable runtime
  binding; the owner token remains per runtime. Baseline behavior is preserved:
  the `agentcontrol` tmux session, `AGENTCONTROL_OWNER` token, and the
  `@agentcontrol_attach_pane` pane identity.
- The portal terminal endpoint selects one employee/binding and attaches only to
  that exact session. It never falls back to another employee's tmux or session.
- A missing or invalid owned-pane marker requires operator recovery; it is never
  silently adopted or replaced, and other windows/panes are never killed.
- Multiple internal sessions each have their own tmux session and pane; one
  viewer at a time per endpoint.

## 9. Readiness versus availability

- **Control-host availability** is a control-plane claim: the host process owns
  a live ACP child and established session. The existing `canControl` semantics
  apply — `degraded` is still controllable for inspection/recovery.
- **Control-host `/health/ready`** requires the exact owned control session
  **and** the attached TUI, with unknown native state failing closed to 503.
  It is not a database, worker or
  provider probe, and must never trigger a destructive restart.
- **Employee readiness** is a separate host-computed state based on observed
  binding/lease status and persisted orientation/hold records, not a worker probe
  added to the control-host endpoint. An employee is `Ready` only when its binding
  is ready, its current orientation is acknowledged and comprehended, and dispatch
  is not held. The
  organization can be available while an employee is provisioning/orienting and
  therefore not ready for task dispatch.

## 10. Employee lifecycle

Idempotent, host-owned transitions:

```text
Requested -> Approved -> Provisioning -> Orienting -> Ready
Requested -> Rejected
Provisioning -> ProvisionFailed / Uncertain
Orienting -> OrientationFailed / Uncertain
Ready -> Degraded / Stopped / Orienting
```

- `Approved` is entered only by explicit owner approval.
- Each request uses a durable host-issued request ID and a caller-scoped
  idempotency key. Approval binds the immutable request revision/hash; changed
  parameters require new approval. The host allocates employee identity once.
  Transitions use stable request/employee IDs and expected record revisions,
  never mutable slugs as idempotency keys. Re-running a step reconciles actual
  resources by identity labels before any create operation.
- Failure paths are explicit and never auto-advance to `Ready`.
- A crash leaves the last committed state; the next run reconciles actual
  effects before continuing. No automatic retry of an uncertain create or tool.
- Provisioning has no privileged authority until Section 6 is satisfied.

## 11. Orientation composition and versioning

- Orientation is a deterministic, ordered composition of versioned fragments:
  organization, department, role, employee, and the host permission policy.
- The composed set has a content-addressed `orientationVersion`.
- **Security precedence:** the host permission policy is applied last and can
  only **restrict**, never relax, a restriction from any other layer.
  Precedence is host policy > organization > department > role > employee, and
  restrictions accumulate (deny wins). A lower layer cannot grant what a higher
  layer denied.
- Evaluate scoped exceptions in this order: collect restrictions from all layers;
  reject any matching non-waivable deny; require a separate active exact owner grant
  for every matching waivable restriction; reject unknown/no-match claims. A grant
  names restriction ID, employee, policy revision, exact tool/resource and expiry,
  and never erases a deny from another layer. Missing/mismatched/revoked grants fail
  closed. Natural-language permission requests and lower-layer instructions cannot
  mark their own restrictions waivable. OpenCode 1.18.30's pinned ACP callback does
  not provide a host-derived canonical path/resource, so Phase 1 grants are staged
  and audited but are not executable through inbound ACP requests; the host always
  selects a reject option (or a protocol cancellation response when none exists).
  The current callback persists requests directly as rejected and records the
  decisive restriction plus every matched stable restriction ID; durable pending
  permission recovery remains a future worker-bridge contract, not an implemented
  controller callback feature. Revoke or reapprove grants when their policy
  revision changes.
- Per assignment the host records: `Assigned` (persisted) -> `Delivered`
  (the host verifies the published name/inode, ownership, single-link status,
  exact bytes and semantic hash associated with the session; this is not runtime
  or model confirmation) -> `Acknowledged`
  (employee response names its employee/session identity and assigned version)
  -> `Comprehended` (host-evaluated bounded evidence about duties, restrictions,
  reporting and escalation). A bridge receipt alone is not employee
  acknowledgment. Each record stores the exact
  immutable assignment ID and `orientationVersion`; a version, assignment or
  session-binding change invalidates prior acknowledgment and requires a fresh
  assignment. Rejected, timed-out and failed attempts are terminal history:
  redelivery marks the old row Stale and creates a new attempt even when semantic
  content is unchanged. `Uncertain` is reserved for a future bridge reconciliation
  state and is not produced by this control-host path. Status prefers a non-Stale
  assignment, or returns the latest Stale row with dispatch held when no current
  attempt exists. Evidence records carry explicit `owner-submitted` or `live-model`
  provenance. Owner-submitted evidence is an explicit override record, not a claim
  that a model demonstration occurred.
- Live comprehension captures bounded agent-message chunks synchronously in ACP
  codec-reader order before the general lossy notification channel. The result frame
  is the completion barrier for every prior frame. Since the pinned ACP contract has
  no turn ID, all prompt operations serialize. The startup bootstrap prompt
  occupies that same slot and is reported as a `busy` session state from the first
  ready snapshot; owner comprehension is rejected deterministically while it is
  active. Timeout or caller cancellation sends
  bounded `session/cancel`, abandons the capture token, and fences another prompt
  until the original request completes or the affected process restarts. Malformed,
  empty, oversized, non-terminal and ACP-error host-started turns persist a
  `live-model` failure outcome and failed hold; malformed caller-submitted manual
  evidence remains validation-only and does not mutate Delivered state.
- **No-rebuild config update:** changing organization instructions, authoritative
  role-fragment instructions, or permission fragments produces a new orientation
  version and updates the affected binding's private config in place. The role API
  uses the active role-fragment revision as its optimistic token. No container image
  rebuild is required. Delivery persists a required runtime generation and leaves
  `policy-update`/`orientation-reload-required` holds. Only startup after the
  OpenCode process/session is established may confirm that generation loaded the
  exact assignment and clear the reload hold; comprehension cannot clear it.
- **Safe affected-runtime restart:** hold dispatch, checkpoint or cancel an
  active turn under explicit policy, await observed termination, reconcile
  effects, restart only that runtime, re-deliver
  orientation and require comprehension before returning to `Ready`. Unaffected
  bindings keep running.
- A future shared-process configuration change must hold/reorient every affected
  session or use process-per-role placement. Do not claim sibling sessions are
  unaffected by a process-wide restart. Policy exceptions are explicit,
  versioned, owner-approved scoped grants; they cannot override non-waivable
  denies or be authorized by the requesting employee. Verify ACP semantics in
  the pinned runtime rather than equating TUI `--auto` with policy enforcement.

## 12. Minimal task acceptance

- A task is a durable record with visible status: `Requested`, `Uncertain`
  (dispatched, unconfirmed), `Running`, `Completed`, `Failed`, `Cancelled`,
  `Verified`.
- Completion is not acceptance. Acceptance requires a host-defined verification
  step whose result is **independently verified** (the host runs the check, or a
  separate verifier does) and persisted. A model message is input evidence only,
  never the acceptance result.
- An uncertain dispatch is reconciled before any retry.
- The employee detail UI shows task ID, description, lifecycle/timestamps,
  sanitized errors and independently verified results separately from a model
  completion. The Overview shows company summary, employee counts by department
  and state, pending approvals, and linked failures needing owner attention.

## 13. Fresh disposable teardown/rebuild test

Scope: one disposable organization/employee on the phase topology, not the
existing dev deployment.

- Every provisioned resource carries identity labels, for example
  `hvo.agentcontrol.organization=<orgId>`,
  `hvo.agentcontrol.employee=<employeeId>`, and `hvo.agentcontrol.disposable=true`.
- Deletion plan: enumerate resources **by label**, verify each belongs to the
  disposable organization, stop the binding, remove labeled
  containers/volumes/networks within an isolated test deployment. The test
  organization has its own database and secrets, not rows in the existing
  development database. Never delete by name or prefix guess. Record the
  selected resource IDs and preview before deletion, verify ownership again at
  deletion, and refuse shared networks/volumes or non-disposable attachments.
- A guard refuses any resource lacking the disposable label. Teardown/rebuild
  must not touch the existing `agentcontrol-v2` dev container, its volumes, or
  `/data`. **No silent resetting of existing dev.**

## 14. Genuine unresolved validations

These are open and must not be presented as decided or owner-accepted:

- Whether per-session configuration isolation is ever sufficient, or every
  internal role needs its own OpenCode process in the shared container.
- The concrete UID/ownership model for Section 6, and whether the existing
  `/data` volume can be re-permissioned without disrupting the running dev
  container.
- **Implemented for the one control employee (#216):** deterministic standing
  orientation composition/versioning, atomic host-owned `/agent-config`
  publication on Linux x64, durable attempt-preserving assignment/evidence/hold
  records with source provenance, stale-readable dispatch blocking, bounded
  null-safe host validation and a synchronously rejecting ACP permission evaluator
  with revisioned staged owner grants and complete matched-restriction audit IDs.
  General worker delivery/restart/bridge behavior remains unresolved.
- Worker-image UID separation and bridge-key bootstrap/rotation interruption,
  private-file/descriptor isolation, and compromise re-enrollment tests.
- Supervisor PID1 termination/reaping, channel authentication, child capability
  and descriptor stripping, generation-bound start reconciliation and explicit
  container-restart recovery. Test viewer attach/detach with a pending permission
  to prove no ACP generation/lease mutation, and reject stale viewer challenges.
- Whether the existing authorized SSH/Docker credential can be used without new
  grants; secure key custody/rotation and measured host-adapter restrictions.
- Implementation and adversarial validation of the specified bridge challenge,
  epoch/fencing, replay and pending-permission contracts, including redelivery
  versus invalidation on child/turn changes and uncertain decision writes.
- SQLite is the selected proposed storage scheme, not an unresolved alternative.
  **Implemented for the control host (#215):** WAL, `synchronous=FULL`, foreign
  keys and a busy timeout are configured on every writer connection; the bounded
  cross-process single-writer lock, the controller-only `0600` database/WAL/SHM
  modes and fail-closed validation are covered by local and container tests, and
  `Microsoft.Data.Sqlite` is pinned centrally to the matching `10.0.12` servicing
  band. **Not yet validated:** WAL/`synchronous=FULL` crash durability on the
  actual Docker volume filesystem has not been crash-tested; the evidence is the
  configured pragmas and the local/container test filesystems only.
- **Implemented bounded initial definition (#216):** the host accepts only a
  size-limited structured record bound to the exact immutable assignment, employee,
  native session and orientation version, and deterministically compares identity, department,
  owner reporting, allowed duties, restrictions and escalation against persisted
  expected facts. It stores a SHA-256 plus sanitized outcome summary, never raw
  model reasoning. **Still unresolved:** how well this bounded check predicts
  future behavior across models and roles; one live demonstration remains an
  explicit operator action and was not run for #216.
- Reachability and collateral of `home-dev-02`, and whether the disposable
  worker path can run without touching existing services.
- Owner review and acceptance of these contracts. This document is a proposal.

## 15. Non-goals

No full role split, no finance workflows, no provisioning implementation, no
multi-process runtime framework, no worker bridge implementation, no container
per internal role, no V1 migration, no release, and no change to the archive.
