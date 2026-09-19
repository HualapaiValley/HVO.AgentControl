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

The separate #213 worker artifact now implements the worker-image bridge/employee
identity split and fixed-operation supervisor described below. Controller routing
and provisioning were exercised on the first managed disposable two-host path
accepted on 2026-09-18 (#217); production managed provisioning and per-role UID
allocation for additional internal roles remain future work.

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
  acknowledges only after its own transaction commits the event/cursor, and each
  page's exact ACK must converge before another page or generation is requested.
  An uncertain ACK stops replay and newer-generation processing; reconnect retries
  that exact ACK first, resumes the same generation after the committed controller
  cursor, finishes its suffix, and only then advances. A failed retry keeps dispatch
  held even though the authenticated connection may remain open. Initial status
  `LastSequence` is the lower-bound current-generation target for one finite pass. A
  cursor already above it before replay is divergence. Replay accepts, commits and ACKs
  a whole valid page that crosses that target, completes without a recovery obligation,
  and leaves later live appends for the next synchronization. If replay ends before the
  target, authoritative exact replay gap/loss markers are persisted and preferred; an
  unavailable status or validation/concurrency failure projecting it first attempts a
  marker-less controller protocol obligation and then faults. Prior generations retain
  the 10,000-event cap, while current generation permits only one 256-event crossing
  page, with 10,001 pages
  as the independent cap. Replay rejects null items, empty `hasMore` pages,
  non-increasing/wrong-generation sequences, raw payloads over 64 KiB, mismatched UTF-8
  byte counts and empty/overlong kinds before normalization. A malformed, store-invalid
  or over-bound response records a marker-less controller replay-gap obligation before
  the session is faulted. Duplicates are ignored by generation/sequence. Bound retained
  replay to 64 MiB and 10,000 events initially. An overflow or missing cursor records
  an exact durable replay-gap obligation, holds dispatch and requires
  ID-and-tuple reconciliation; distinct obligations use set semantics and a bounded
  overflow marker prevents unbounded growth. Observation journal infrastructure
  failure holds new dispatch but does not stop ACP or rewrite request, cancellation
  or permission outcomes; exact integrity/schema/probe recovery is controller-reachable.
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
  input/resize frames, streams output as explicit
  `{ "type": "output", "encoding": "base64", "data": "..." }` frames to the
  authenticated same-origin WebSocket, and enforces one viewer per session. The
  local PTY bridge uses the same base64 byte contract; the browser accepts only
  exact `base64` or legacy-explicit `text` encodings and rejects unknown values. Detach asks the supervisor to stop/reap
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

### 10.1 Container profiles (#257; #258 profiles and #259 builds implemented)

Every managed employee is built from a **container profile revision**: an
immutable, content-addressed template that extends the approved worker base
image. The owner accepted this design on 2026-09-18.

- A profile (`prof-<hex>`) is a mutable label (slug, display name, description,
  `active|retired` status, optimistic revision) over an append-only chain of
  revisions (`prev-<hex>`, `revision_number` 1..n). Immutability is enforced at
  the database boundary, not only by the store API: triggers abort any UPDATE
  of a revision's identity/content columns, any DELETE of a revision or
  profile, any INSERT that conflicts with an existing row, and any UPDATE of a
  profile's identity columns or slug that would collide with another profile
  (so `INSERT OR REPLACE`/`UPDATE OR REPLACE`, whose implicit delete bypasses
  delete triggers, cannot replace a revision or profile); both tables are
  `WITHOUT ROWID` so no implicit rowid key exists for a conflict to target;
  only the reserved build-lifecycle columns stay writable for #259,
  and identical content cannot be re-recorded for the same profile
  (`UNIQUE (profile_id, content_hash)`). The triggers are part of the exact
  schema signature.
- `hire_requests` is rebuilt in v8 with a nullable
  `container_profile_revision_id` reference; existing hires migrate with `NULL`
  and #260 makes the reference required at approval time.
- Idempotent create replay is bound to the original payload (revision 1), so a
  replay after later revisions still returns the same profile.
- A revision is a **constrained `devcontainer.json` subset** plus an optional
  Dockerfile fragment. Accepted keys: `name`, `image` (exactly the symbolic
  `agentcontrol-worker-base`) or `build.dockerfile` (`"Dockerfile"` with a
  fragment whose first instruction is `FROM agentcontrol-worker-base`),
  allowlisted `features` with only a `version` option, allowlisted
  `containerEnv`/`remoteEnv` names, `postCreateCommand`/`postStartCommand`
  (string without shell metacharacters or an argument array), and
  `customizations.agentcontrol.{summary,tags}`. Everything else fails closed
  with a key-specific reason: `runArgs`, `mounts`, `workspaceMount`,
  `forwardPorts`/`appPort`, `privileged`, `capAdd`, `securityOpt`, `init`,
  `initializeCommand` and the other lifecycle hooks, `remoteUser`/`containerUser`,
  `overrideCommand`, Compose keys, `hostRequirements`, unknown keys, duplicate
  keys, comments/trailing commas, nesting deeper than 6, or more than 64 KiB.
  Fragments may only use `RUN`, `ENV`, `ARG`, `LABEL` and `WORKDIR` (under
  `/workspace`); `USER`, `ENTRYPOINT`, `CMD`, `VOLUME`, `EXPOSE`, `COPY`, `ADD`,
  `HEALTHCHECK`, `SHELL`, `STOPSIGNAL`, `ONBUILD`, a second `FROM`, parser
  directives, heredocs, `RUN --mount/--network/--security`, the Docker socket
  path and replacing `PATH` are rejected. Checks run on the **joined logical
  instruction** (backslash continuations joined exactly as BuildKit does), so a
  token or keyword split across continued lines is seen as Docker would
  execute it.
- The canonical form (compact JSON, ordinal-sorted keys, LF fragment) is what is
  hashed and stored, so key order and line endings never create a new revision.
- The base is referenced symbolically. The concrete approved digest is pinned per
  host when a revision is built and frozen again at hire approval (#260); the
  revision itself stays immutable and hermetically validatable without
  deployment configuration.
- **Builds are per (revision, host)** (`profile_builds`, schema v9, `pbld-<hex>`;
  identity columns immutable, never deleted or replaced, at most one live and one
  verified build per pair). The controller renders the revision to a
  deterministic build context — one generated `Dockerfile` in a fixed-metadata
  tar whose SHA-256 is the build's `context_hash` — and streams it to
  `docker build -` on the approved host over the same pinned SSH path as
  provisioning: `--pull=false --no-cache`, no build arguments, secrets, cache
  sources or host context, `--network none` unless a feature recipe needs apt,
  labels `agentcontrol.profile`/`context-hash`/`base-digest`, a local
  `agentcontrol-profile:<revision>-<hash12>` tag. BuildKit refuses an image id as
  a `FROM`, so the exact base digest is first pinned under the controller-owned
  `agentcontrol-worker-base:pin-<12hex>` tag on that host and the generated
  Dockerfile names only that tag.
- The generated Dockerfile is `FROM <pin>`, the owner's fragment (its own `FROM`
  dropped), the devcontainer `features` rendered as **fixed apt recipes** per
  allowlisted id and version (nothing is fetched from ghcr.io), `containerEnv`
  as `ENV`, lifecycle commands and `remoteEnv` recorded as labels (the supervisor
  runs them as the employee; the build never executes them), and a fixed trailer
  that re-strips setuid bits, re-owns `/app` and the supervisor to root, resets
  the four private directories to their fixed owners and 0700, asserts the two
  uids and the worker binaries, and restores the supervisor entrypoint. The
  fragment runs before the trailer, so it cannot undo it.
- A build becomes `built`/verified only after two independent checks over what
  the host printed: `docker image inspect` must show the controller's three labels
  for this revision and context, the supervisor entrypoint, no default user, no
  exposed ports or anonymous volumes, the approved platform and a root filesystem
  whose layers start with the approved base's; then a fixed verification program
  (a controller constant, carried base64 so the remote token has no quotes) runs
  inside the image with `--network none --read-only --cap-drop ALL` and must
  report uids 1101/1102, the four directories 0700 with their owners, `/app` and
  the supervisor root-owned, the worker dll, `dotnet`, `opencode`, no setuid files
  and no Docker socket path. A contract failure records `rejected`; a host or
  build failure records `failed`; a transport loss during build or verify records
  `uncertain`, and the next run reconciles by inspecting the tag — found → verify,
  absent → `failed` — never by rebuilding blindly.
- **Approved digests are per host**: the configured base plus every verified build
  on that host. The container-create command builder refuses any other digest;
  the bootstrap always runs the base. `POST /api/workers/enroll/plan` accepts an
  optional `imageDigest` that must be in the target host's approved set, and the
  chosen digest is frozen into the enrollment. The revision-level
  `build_status`/`built_image_digest`/`verified` columns are a fold of the
  per-host rows (`built` if any host verified, else `building`, `rejected`,
  `failed`, `unbuilt`).
- Owner routes: `GET/POST /api/profiles/{id}/revisions/{revisionId}/builds`
  (the POST queues, runs and verifies synchronously on one ready host and returns
  the terminal record); `/profiles/{id}` shows per-host builds and a build action.
  **Building never provisions an employee.**
- `generic-employee` revision 1 is seeded on fresh stores and on the v7→v8
  migration, never overwriting an existing slug.
- Owner routes: `GET/POST /api/profiles`, `GET /api/profiles/{id}` (profile plus
  full revision chain), `GET/POST /api/profiles/{id}/revisions`,
  `POST /api/profiles/{id}/retire`; portal pages `/profiles` and `/profiles/{id}`.
  These record definitions only. **Nothing in the profile or build slices
  approves a hire, creates an employee or rebuilds an existing employee**, and
  profile updates never auto-rebuild employees (#261 adds the explicit
  data-preserving rebuild).

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
  active. Timeout or caller cancellation sends bounded `session/cancel`, abandons
  the capture token, and retains the underlying request correlation and prompt-slot
  fence without a timer until the exact response/error frame arrives or ACP
  transport/process termination is confirmed. Malformed, empty, oversized,
  non-terminal, ACP-error and transport-failed host-started turns persist a
  `live-model` failure outcome bound to the exact started assignment and session.
  If policy replacement made that assignment Stale meanwhile, the failure evidence
  is written to its historical row without changing Stale or mutating the current
  replacement assignment or its holds. Malformed caller-submitted manual evidence
  remains validation-only and does not mutate Delivered state.
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
- **Implemented worker artifact (#213) and accepted disposable enrollment (#217):** worker-image bridge/employee UID separation; private `0700` control/home/workspace/session trees; bootstrap-only stdin key creation with duplicate verification and symlink/hard-link refusal; no Docker socket, host checkout or controller secrets; fixed root PID1 supervision; ACP stdio ending at the unprivileged bridge; worker/process generations; and explicit bridge/container interruption behavior. Disposable enrollment over pinned ED25519 strict SSH to `home-dev-02` was accepted on 2026-09-18 and the disposable worker/key were removed. **Still unresolved:** key rotation interruption, compromise re-enrollment and production remote custody/delivery.
- **Implemented worker channel (#213), hermetic controller foundation and accepted disposable two-host path (#217):** the worker retains the bounded NDJSON, mutual role-labelled HMAC, lease/epoch fencing, durable request/replay/permission and interruption behavior described above. The protocol framing, canonical hashing and HMAC code is a storage-independent library referenced by both worker and controller. Control schema v7 retains the exact stable remote host/enrollment/cursor/task/request/provisioning/resource/recovery/viewer, durable hire-request and bounded pending-permission metadata, chaining exact released-v3/v4/v5/v6 migrations with verified create-once backups and hashes; v5 added the audited `session-reconciliation` recovery kind and v6 adds audited external disposition for `request-uncertain` obligations. Owner APIs enroll only configured approved-host references; strict SSH vectors pin known_hosts and identity paths and forbid arbitrary command tokens. The disabled-by-default controller has injectable authenticated synchronization, replay/ACK, request/cancellation/permission, heartbeat, typed provisioning and ownership-checked cleanup coordinators with intent-first durable transitions. On 2026-09-18 the first managed disposable path was accepted live over pinned ED25519 strict SSH to `home-dev-02`, including ACP session lifecycle, provider prompt/tool, cancellation, disconnect/reconnect reconcile, stale-lease fencing, bounded replay, worker restart and viewer attach; disposable resources and the key were removed. The role-separated viewer protocol, fixed production PTY/TUI attach backend, exact process-session binding and store-only read model are implemented hermetically. **Still unimplemented and not to be inferred:** key rotation/compromise re-enrollment, production managed provisioning, and cancellation-as-rollback (OpenCode reported the cancelled turn completed). Owner keystrokes execute with employee authority and are not a sandbox.
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
  worker path can run without touching existing services. **Resolved for the
  disposable run (2026-09-18):** the isolated labeled worker path ran without
  touching existing services and all labeled resources were removed; production
  provisioning and collateral at scale remain unvalidated.
- Owner review and acceptance of these contracts. This document is a proposal.
  The #217 first managed disposable two-host acceptance is complete; #219
  approval/provisioning still requires discussion and owner approval.

## 15. Non-goals

No full role split, no finance workflows, no container per internal role, no V1
migration, no release, and no change to the archive. #213 provides the independent
worker-owned bridge/runtime artifact; #217 provides the hermetic controller
binding/routing/provisioning state machines and its first managed disposable
two-host path was operationally accepted on 2026-09-18. Viewer/read-model work is
code-complete; key rotation, production provisioning, and #219
approval/provisioning remain pending discussion and owner approval.
