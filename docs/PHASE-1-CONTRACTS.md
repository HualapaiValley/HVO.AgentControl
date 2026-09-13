# Phase 1 Organization, Runtime Placement and Control Contracts (Proposed)

**Status: proposed design, not implemented.** The new contracts below extend,
rather than describe, the active baseline. They are the working set needed for
Phase 1 (#211 / #212). It is deliberately narrower than an enterprise model:
no final role split, no multi-process runtime framework, and no provisioning in
this slice. Every item is a proposal for owner review; none is owner-accepted
until explicitly recorded on the issue.

The active baseline is one control container, one OpenCode ACP process and one
combined role (`AcpControlHost` + `AgentControlOpenCodeConfig.RoleName`). The
existing `/data/runtime.json` identity, session and tmux ownership must be
preserved, not reset.

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
  references, and a monotonic `runtimeEpoch` used for ownership fencing.
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
- **Development** and **QA** departments initially exist but are **empty**.
  The first Development employee is provisioned later in Phase 1 through #219,
  not by organization initialization or this documentation slice.
- Finance is not created; it is a future internal department.

## 3. Storage

Chosen: **one small SQLite database file** owned by the C# host, for example
`/data/control.db` (final path decided in the implementation issue).

- Single writer: the host process. A cross-process file lock plus
  `busy_timeout` makes a second writer fail closed rather than interleave.
- WAL mode, foreign keys on, `synchronous=FULL` on every writer connection for
  all writes, including the durability-critical hire/task records.
- Rationale: a hire writes employee + binding + orientation assignment
  together; SQLite transactions give atomic multi-record commits and real
  crash semantics. A rewrite of one JSON document is smaller but must reimplement
  atomicity, partial-write detection and journaling. The package addition must
  be justified explicitly in the implementation PR.
- Schema version is recorded in a `schema_version` table. Startup checks schema
  version and `PRAGMA quick_check`/`integrity_check`. An unknown or newer
  version, a failed integrity check, or an unexpected table shape **faults the
  host** — it never creates, resets or "repairs" the store.
- Data directory `0700`, database file `0600`.

### 3.1 Safe baseline adoption, no V1 migration

- Create a verified backup before adoption. Only an explicitly new database may
  import `runtime.json`; an initialized store missing its organization is corrupt,
  not an invitation to import or seed again. Commit identity, initial records,
  schema version and adoption marker in one transaction. Re-running after a
  committed adoption validates matching identity without creating new employees.
- `runtime.json` is **left in place** as rollback evidence; it is not deleted
  and not silently reinterpreted. After cutover the database is the single
  authoritative store.
- The pre-upgrade JSON is a snapshot, not a live backward-compatible store.
  Rollback must restore the matching backup, not run old code against stale
  session identity after new activity. Backup/restore includes WAL state through
  SQLite backup or a quiesced checkpoint, not a live copy of the database alone.
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

## 6. Credential and process isolation prerequisite

**Before** any privileged provisioning authority is added, the baseline
same-UID credential access must be fixed. Today the controller, OpenCode and the
mounted owner secret share UID 1000; prompt/tool deny rules are defense-in-depth,
not OS isolation, and another executable can read the secret.

Concrete separation inside the same container:

- Distinct OS identities: a controller UID owning the database and owner secret,
  and a separate agent/OpenCode UID with its own private store.
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
  worker and owns OpenCode ACP stdio.
- The connector opens a bridge-owned UNIX socket inaccessible to the worker
  OpenCode UID. Bridge and connector perform a versioned mutual HMAC challenge
  over independent random nonces, controller/worker IDs and protocol version,
  using a per-worker random key stored only in controller and bridge-private
  files. Role-specific challenge labels prevent reflection; reject reused or
  expired nonces and compare MACs in constant time. The SSH tunnel authenticates
  the remote host and protects bytes. This key is a scoped newly provisioned
  worker's internal control credential, not a provider or infrastructure grant.
  It is never exposed to worker tools or supplied as a command argument.
- The bridge durably allocates monotonically increasing ownership epochs in its
  own store, transactionally with a controller ID and connection nonce. Every
  mutation carries that epoch and nonce. Only an authenticated matching controller
  may reconnect; replacing its live connection advances the epoch and fences the
  old one. Different controller identity needs explicit enrollment approval.
  Heartbeats every 5 seconds maintain a 20-second monotonic lease. Lease expiry
  holds new dispatch, not the running tool; a restarted bridge invalidates all
  prior leases and requires reauthentication. Timing values are testable options.
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
- Status includes session/process generations, active request, pending permission,
  lease, replay bounds and hold state. Permission decisions are keyed by original
  request and decision ID; conflicting repeats fail closed. Automatic model retry
  cannot resolve an uncertain permission response.
- A **pending permission request survives controller disconnect** and is
  re-delivered on reconnect.
- A **worker crash does not resume** the in-flight turn; only durable state is
  reloaded. Loading history is not resuming a command.
- The C#-owned SSH credential may use an already-authorized development Docker
  account for this bounded test, with its actual root-equivalent authority
  disclosed. A dedicated host allowlist, typed operation validation, approved
  image/digest/resource limits, no privileged/host namespaces/host bind mounts,
  no arbitrary exec, and identity-checked resource removal constrain the host
  provisioning adapter. This is not daemon-enforced least privilege. Never give
  that credential or raw adapter to a model. If existing authorization/access is
  insufficient, stop for a new grant; a daemon-side rootless/restricted service is
  a separately approved hardening option, not an assumed existing service.
- Terminal traffic uses a separate SSH/Docker connector to the exact registered
  worker's private bridge socket. After authentication the bridge launches only
  an attach client for the bound TUI/session; it accepts bounded input/resize
  frames, streams terminal bytes to the authenticated same-origin portal WebSocket,
  and enforces one viewer per session. Detach reaps the viewer only; no public
  port, alternate session, arbitrary command or fallback shell is permitted.
- Before provisioning, probe and persist Docker API/daemon version, Linux OS and
  architecture, selected image platform, usable non-shared volume storage/free
  space, memory/CPU capacity and enforceable limits, SSH host identity, and required
  image tools (OpenCode pin, bridge, tmux, PTY support). Reject incompatible or
  unknown mandatory capabilities before creation. Hardware access is not granted
  merely because a host advertises it.
- Disposable workers default to `opencode/big-pickle`, already measured with no
  injected provider credentials in the standalone POC. Validate that availability
  again; it is not guaranteed service. Do not copy coordinator CLIProxy credentials
  into workers. Provider failure is explicit; a paid/authenticated provider or new
  infrastructure credential requires owner direction, not silent fallback.
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

- **Availability** is a control-plane claim: the host process is alive and owns a
  live ACP child and established session. The existing `canControl` semantics
  apply — `degraded` is still controllable for inspection/recovery.
- **Readiness** requires the exact owned session **and** the attached TUI, with
  unknown native state failing closed to 503. It is not a database, worker or
  provider probe, and must never trigger a destructive restart.
- Per employee: an employee is `Ready` only when its binding is ready, its current
  orientation is acknowledged and comprehended, and dispatch is not held. The
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
- Per assignment the host records: `Assigned` (persisted) -> `Delivered`
  (runtime confirms exact artifact installation) -> `Acknowledged`
  (employee response names its employee/session identity and assigned version)
  -> `Comprehended` (host-evaluated bounded evidence about duties, restrictions,
  reporting and escalation). A bridge receipt alone is not employee
  acknowledgment. Each record stores the exact
  `orientationVersion`; a version change invalidates prior acknowledgment and
  requires re-delivery.
- **No-rebuild config update:** changing instructions or permission fragments
  produces a new orientation version and updates the affected binding's private
  config in place. No container image rebuild is required.
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
- Whether the existing authorized SSH/Docker credential can be used without new
  grants; secure key custody/rotation and measured host-adapter restrictions.
- Implementation and adversarial validation of the specified bridge challenge,
  epoch/fencing, replay and pending-permission contracts.
- SQLite is the selected proposed storage scheme, not an unresolved alternative.
  Validate WAL behavior on the actual volume filesystem and single-writer locking;
  justify the package version in #215.
- The definition and reliability of "comprehension" evidence.
- Reachability and collateral of `home-dev-02`, and whether the disposable
  worker path can run without touching existing services.
- Owner review and acceptance of these contracts. This document is a proposal.

## 15. Non-goals

No full role split, no finance workflows, no provisioning implementation, no
multi-process runtime framework, no worker bridge implementation, no container
per internal role, no V1 migration, no release, and no change to the archive.
