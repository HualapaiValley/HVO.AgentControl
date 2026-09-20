# V2 Architecture

Generation V2 is a clean architecture line separate from the archived V1. The
semantic release version (currently `0.1.0`, unreleased) is tracked separately
from the `generation = 2` identity.

## Current implementation (0.1.0 portal slice)

- **Control container:** one .NET 10 process hosting both the Blazor static
  server-rendered portal and the `AcpControlHost` background service. The host
  owns a single OpenCode ACP process, its loopback-only native HTTP server, the
  durable organization/session record and a private persistent `/data` volume.
- **Attach TUI:** a TUI client in tmux on the same OpenCode runtime. The browser
  terminal connects through a same-origin authenticated WebSocket only after the
  requested stable employee ID, authoritative runtime binding, persisted native
  session and current host `ControlStatus` all match exactly; one viewer at a
  time and no fallback. The TUI is a human view, not a second engine.
- **Owner routed UI/read model:** a shared static-SSR layout serves distinct
  `/organization`, `/organization/departments`, `/organization/departments/{id}`,
  `/employees`, `/employees/{id}`, `/hiring`, and `/system` components under a
  grouped Organization navigation. Each route renders only its own page sections;
  route-specific ES modules enhance the server response, and terminal/model code
  is loaded only by employee detail. The organization section is the authoritative
  hierarchy: the overview and directory link each department by stable ID, and the
  department detail exposes the stored revision, the active department orientation
  fragment as standing instructions when one exists, the roles assigned to the
  department, the scoped roster with host-computed availability, scoped
  attention items and a department-scoped `Request employee` link.
  `/api/organization/portal`, `/api/employees/{id}` and `/api/departments/{id}`
  join one authoritative store snapshot with the exact host status. Availability wire
  values describe host readiness/orientation conditions, not worker lifecycle.
  For the exact host-owned employee the current sanitized runtime error
  (`ControlStatus.Error`) is exposed. Recent logs are explicitly unsupported
  because no employee-scoped safe log contract exists, so neither bulk logs nor
  another employee's logs are returned. Pending hire requests are real durable
  records; approval is implemented as a code capability (§10.2 of the contracts):
  an owner-only, same-origin, revision-bound action freezes one hire revision
  against one verified profile build on one ready host and atomically creates the
  managed employee identity and DeveloperContainer binding. Approval does not
  provision or orient — that is a separate trigger and the request stays
  `Approved` until it runs. **No live owner-approved hire has been executed on any
  host**, the `home-docker` execution-host enrollment is held by the owner, and
  `WorkerControl` is disabled by default, so this is not operationally validated
  and is never represented as completed employee creation.
- **Persistence:** `/control-data/control.db` is the authoritative SQLite store.
  Schema v10 migrates only the exact released v9 signature after creating and
  verifying immutable `control.schema-v9.db` and SHA-256 evidence; it adds
  `hire_request_approvals` (the immutable owner freeze of one hire revision
  against one verified profile build) and `managed_enrollment_resources` (the
  frozen per-binding limits/digest/host a managed provisioning run consumes),
  rebuilds `hire_requests` with a bounded sanitized nullable `status_detail`, and
  installs triggers that abort any update/delete/replace of a freeze. Schema v9
  migrated only the exact released v8 signature after verified
  `control.schema-v8.db` evidence and added the per-host `profile_builds` table
  (additive only). Schema v8 migrated only the exact released v7 signature after
  verified `control.schema-v7.db` evidence. It added
  immutable container profiles and their append-only revision chain and seeds
  the `generic-employee` profile (see `docs/PHASE-1-CONTRACTS.md` §10.1); the
  seed never overwrites an existing slug. Schema v7 migrated only the exact
  released v6 signature after verified `control.schema-v6.db` evidence. It added
  idempotent hire requests (bounded requested identity/purpose/placement/resources,
  immutable request-version hash, optimistic revision) and append-only state events
  containing hashes rather than raw secrets. The schema reserves later lifecycle
  states; this slice exposes request, reject, approve and the post-approval
  lifecycle transitions. Existing
  organization, policy, worker and recovery records are preserved. Key bytes and
  active connection nonces are never stored there. Semantic release `0.1.0`
  remains unreleased and has not been published or deployed as a release.
  It stores organization, department, role, employee, runtime-binding and ACP-session
  identity, plus versioned orientation, dispatch-hold and permission-policy records,
  in the controller-private volume. `/control-data/runtime.json` is
  retained as adoption evidence only and is no longer written by the controller;
  the agent-owned `/data` volume retains the workspace, the private OpenCode home
  and native conversation state. A failed session load is surfaced, not silently
  replaced.
- **Access:** owner Basic authentication from a mounted password file, plus
  same-origin checks on WebSocket and mutation endpoints. The portal publishes
  on all Docker-host IPv4 interfaces (`0.0.0.0:5054`) for trusted-LAN use and is
  **unencrypted**. Use SSH-tunneled access off the trusted LAN; a TLS-terminating
  proxy needs explicit trusted forwarded-header support, not yet implemented.

The runtime is disabled by default for host development; Compose enables it.
This slice records, rejects and (as code capability) approves owner hire requests,
and can create the managed employee identity and binding from an approval, but it
has no live owner-approved hire, does not route tasks, and does not implement the
full organization lifecycle. `WorkerControl` remains the deployment gate and is
false by default.
Remote-worker reconciliation-integrity failures return a sanitized `502`
ProblemDetails response titled `Remote worker reconciliation is invalid`; the
detail states that correlation failed and dispatch remains held.

## Communication

ACP is newline-delimited JSON-RPC over process stdin/stdout. OpenCode's `--port`
flag exposes its embedded HTTP server, not native ACP-over-TCP. The control host
talks ACP over stdio and uses loopback HTTP only to read native session state
(for example the authoritative model and session status). Native HTTP never
leaves the container. The browser talks only to the portal.

## Worker-owned ACP bridge artifact (#213)

The separate `HVO.AgentControl.Worker` executable and Docker `worker` target now
implement the worker side of Sections 6-7 without starting the web portal. A
minimal root PID1 supervisor starts only the pinned OpenCode ACP operation as
employee UID 1102 and the bridge as UID 1101, reaps fixed children, and accepts
only authenticated-local `start`, `status` and `stop` operations. ACP stdin and
stdout terminate at the bridge. The bridge owns a private Unix socket and a
separate exact-signature schema-v6 SQLite journal in `/control`;
worker/process generations, supervisor lifecycle handles, ownership epochs,
request forwarding state, replay cursors/events, holds and generation-bound
pending permissions are durable and bounded by count and bytes. The journal refuses unknown or changed schema
objects, corruption, foreign-key violations, symlinks, non-regular or multiply
linked files, unexpected owners/modes, and a second live bridge instance. The
persistent `0600` regular lock file is opened without symlink following, validated
by descriptor/path inode identity and held with a nonblocking kernel advisory lock;
a crash releases ownership without requiring deletion of the lock inode.

The bridge protocol is bounded newline-delimited JSON (`hvo-worker-acp/2`, 1 MiB
control frames). ACP uses a separate buffered codec with an 8 MiB frame limit and
128-level depth limit; control framing remains 1 MiB/64 levels. Mutual HMAC-SHA256 binds role-specific labels, independent
nonces, controller/worker/key identities and protocol version. The bridge
checks Linux peer credentials, rejects nonce replay and stale proofs, bounds the
authentication exchange, and advances an ownership epoch on same-controller
reconnect. Every operation is fenced by the exact lease captured when its socket
authenticated; mutations additionally carry epoch plus connection nonce and must
match both that socket lease and the current journal lease. Thus an old socket
cannot issue even `status`, `reconcile`, or `replay`, and stale replay validation
cannot create a replay-gap hold. Lease expiry, manual or protected safety hold, replay gap/loss, and non-running
process state reject submit before request registration or ACP I/O. Independent
normalized hold rows preserve manual, replay-gap, replay-loss, process, permission,
ownership, transport and journal obligations without clobbering one another; status
exposes the ordered reason list and retains the joined legacy reason string. Generic
hold operations change only the manual row. Protected holds clear only through their
own exact reconciliation transition. Submit
accepts only `session/prompt`, requires a validated `sessionId` and host turn ID,
and binds durable deduplication to payload, turn, session and accepting ownership
epoch. Requests use bounded recursive canonical JSON hashing, persist ID/hash and
forwarding ownership before write, and become `uncertain` on write ambiguity. A
controller connection may stop awaiting a forwarded request, but the
bridge-owned operation, active request and prompt serialization remain until ACP
responds or its process transport terminates. Clean EOF and read failures fail all pending correlators with host-authored errors,
mark only still-forwarding/forwarded requests uncertain and release the active
prompt slot. Clean EOF is `process-exited`; malformed/oversized ACP frames are
`acp-protocol-failed`; transport failures are `acp-transport-uncertain`. These
truthful states have protected holds, and a response completed before EOF is not
rewritten. There is no exactly-once tool-effect claim. Events are ordered by the
`(workerGeneration, sequence)` tuple with sequence restarting at 1. Prior-generation
events and the lexicographic generation/sequence ACK survive bridge restart and
remain replayable; status exposes both the current generation and ACK generation.
Pruning applies only behind that ACK cursor across the global 10,000-event/64-MiB
bounds and removes reconciled loss rows before their referenced generation rows.
Replay is paged in sequence order, with at most 256 events and a conservative
256-KiB serialized response budget per page. The controller rejects null pages,
null event items, empty `hasMore` pages, invalid raw UTF-8 byte counts or payloads
over 64 KiB, empty/overlong kinds, and responses exceeding the replay bounds. It
validates the worker-declared metadata before kind/JSON normalization, commits and
ACKs each page's exact generation/last sequence, and records a durable controller
replay-gap obligation if protocol validation or the store validation rejects a page.
An uncertain
page ACK creates an exact durable obligation and stops both that generation and every
newer generation. On reconnect, the exact ACK must converge first; replay then resumes
the same generation from the already-committed controller cursor, finishes its suffix,
and only then advances to newer generations. A failed pending-ACK retry leaves the
connection held and permits no replay, request reconciliation or dispatch. The worker
queries at most one look-ahead event, serializes each candidate once for conservative
O(n) page sizing, and serializes the completed response once for the final bound check,
so it never builds a control response near the 1-MiB framing limit. An individual
event that cannot fit a replay page is treated as replay loss
at append time rather than becoming an unreplayable retained row. The authenticated
controller connection processes multiple operation frames. A well-framed pre-effect
operation rejection returns only the fixed `worker-request-rejected` category and
leaves the stream usable while its lease is still current. If ACP delivery or
completion becomes ambiguous after durable mutation registration, the bridge returns
`worker-operation-uncertain` if possible and then closes the owner connection; the
controller records the request/cancellation/permission uncertain and discards the
session. Malformed framing, authentication failure, transport failure, or a
stale/fenced lease also closes the connection. Each missing generation/cursor creates or reuses an exact durable replay-gap
obligation identified by a stable hash; status exposes up to 128 obligations and
their total count. Distinct gaps cannot overwrite each other, reconciliation names
the exact ID and tuple, and a loss reconciliation clears only gaps linked to that
loss marker. At the 128-row bound, one non-clearable overflow obligation replaces
additional attacker-provoked tuples while dispatch remains held. If an observational
event cannot fit after acknowledged pruning, the journal reserves a compact
`events-dropped` marker/sequence and accumulates bounded dropped-count/byte
metadata, including the rejected event and each retained non-marker event evicted
to reserve the marker. Status and replay expose the loss; ordinary observation
continues without unbounded growth, while dispatch remains held until the controller
explicitly acknowledges the exact loss marker through `reconcile-replay-loss`.
A cursor-before-boundary gap tied to that unreconciled marker clears in the same
exact transition. Unknown-generation, future-cursor and other gaps require
`reconcile-replay-gap` with the exact gap ID, attempted generation/cursor and
reported first-retained/last sequence values. The initial status `LastSequence` is a
lower-bound current-generation target for one replay pass: a controller cursor already
above it is divergence because the controller was ahead before replay began. A valid
page may contain newly appended sequences beyond that snapshot; the controller commits
and ACKs the whole page, then successfully completes the pass once its cursor reaches or
crosses the target, without creating a recovery obligation. An already-satisfied or zero
target requires no replay, leaving later appends unacknowledged for the next
synchronization rather than chasing a live suffix.
Prior generations retain the independent 10,000-event bound; the current generation
allows at most one 256-event crossing page beyond that bound. Replay is also bounded
to 10,001 pages; every non-final page must contain at least one event, so the page cap
cannot be reached by a conforming retained journal. When a replay request is rejected, the controller
immediately reads status on that authenticated session and persists the worker-reported
gap and loss markers; it never fabricates an exact tuple from the rejected request.
A malformed replay page or exceeded controller replay bound closes the session after
persisting a marker-less controller replay-gap obligation. If replay ends before the
snapshot target, the controller reads authoritative status: exact replay gap/loss holds
are preferred and persisted, while an unavailable status or a validation/concurrency
conflict projecting that exact status triggers a best-effort marker-less protocol
obligation before the session faults. If status after a worker rejection is unavailable,
recovery likewise remains held by a marker-less controller obligation and later healthy
status never auto-clears it.
Because no exact worker marker exists, automatic worker reconciliation is impossible:
only the same-origin owner API may acknowledge the obligation after external
reconciliation, using the fixed `acknowledged-after-external-reconciliation`
disposition and a SHA-256 evidence reference. The obligation clear and a hash-only
`worker_recovery_audit` row commit atomically; no free-form notes are retained.
Acknowledgment never synthesizes readiness: when it clears the final obligation,
the cursor becomes `disconnected`, clears its hold summary, and suppresses viewer
availability until a subsequent authenticated worker status is recorded. The
owner-authenticated `/api/workers/status` response exposes the hash-only audit rows.
Marker-less acknowledgment is limited to controller `replay-gap` and
`ownership-changed` obligations. Marker-bearing controller `session-reconciliation`
and `request-uncertain` obligations are also eligible when their bounded marker is
valid; the latter is the explicit escape hatch when repeated request-id reconciliation
cannot establish a worker outcome. For ownership changes with active work, the owner
must first externally confirm the outcome or choose a reconciled stop/restart; the
acknowledgment itself neither stops nor adopts work. A non-capacity observation append
failure does not cancel ACP or alter an already-established request, cancellation
or permission outcome. It persists a sanitized `journal-failed` marker carrying a
random operation ID, worker generation and category, then holds new dispatch.
`reconcile-journal`, fenced by the current lease, requires that exact marker and
internally repeats exact-schema, quick-check and foreign-key validation, performs
an insert/delete transaction probe and checkpoints before clearing the obligation.
An unavailable journal that cannot persist its marker remains fail-closed in memory
for that bridge process. Pinned OpenCode permission
callbacks carry no trusted request identity: the bridge binds the actual `optionId`
shape only to its single host-owned active prompt context (request ID, required
persisted turn ID, process generation, ACP correlation and matching session ID when
known), generates a `perm:` decision ID from host context, and stores only a bounded
payload hash and option IDs. Unbound callbacks are rejected/cancelled; correlation
failure after durable insertion invalidates that row before one rejection. Observation
capacity loss is local and cannot reject an established permission, rewrite a durable
request/cancellation/permission result, or terminate the ACP reader. Permission
response intent is durable and bound to both the row epoch and current lease epoch
before a bridge-lifetime write; reconnect with pending permission work installs an
`ownership-changed-pending-permission` hold. Ambiguity becomes
`permission-decision-uncertain`, and no blind resend or new-epoch adoption occurs;
`stop-process` is the safe fixed recovery that terminates ACP and lets observed EOF
atomically invalidate old permissions, clear ownership/permission holds and retain a
`process-exited` hold. ACP process exit is not itself a bridge transport failure:
status, replay, hold and exact recovery operations remain available on the same
healthy owner connection, while submit/cancel/permission continue to enforce their
own running-process and hold gates.

The worker retains two distinct bounded views of a permission request in the existing
`option_ids_json` column: offered IDs for diagnostics and a versioned
`safeRejectIds` list derived while the full option objects and their `kind` fields
are still available. Only exact fixed IDs `reject_once`, `reject`, and
`reject_always` can enter that safe list, in that priority order. A missing `kind`
permits ID-only compatibility; a present string must be exactly compatible
(`reject_once` or `reject_always` as appropriate). Allow, unknown, malformed, and
contradictory kinds make the option ineligible. A reject kind never authorizes an
arbitrary vendor ID. Names, substrings, and case folding are never authorization
inputs. Options are grouped by exact ordinal `optionId`; a repeated ID is a
protocol violation that vetoes that ID entirely, even when every occurrence was
individually eligible, so a duplicate allow/reject collision can never widen the
reject allowlist. Offered IDs are deduplicated for diagnostics only and are never
authorization evidence. Legacy stored arrays preserve offered IDs but produce an
empty safe list, so they fail closed rather than reconstructing lost kind
evidence.

The owner rejection decision is chosen only from the controller's projected
`SafeRejectOptionIds`, never from offered IDs. Pinned OpenCode 1.18.30 with
`permission` configured to `ask` for broad `read`/`edit`/`bash` classes emits the
generic IDs `once`, `always`, and `reject`; the exact generic `reject` remains
supported when its kind is absent or compatible. The worker also validates the
submitted decision against its own durable safe list before writing ACP. Local ACP
handling uses the same full-object selector. An unbound callback is stricter:
`oneShotOnly` excludes both the `reject_always` ID and `reject_always` kind, then
selects only eligible exact `reject_once` or `reject`; otherwise it cancels. A bound
owner decision may retain exact eligible `reject_always` as the last fallback after
the request/session/lease/turn invariant is validated. If that exact binding is
valid but the safe list is empty, the pending permission remains held and the API
returns the dedicated sanitized 409 compatibility diagnostic; offered IDs are not
disclosed or used to decide. The prior vendor-ID kind fallback was intentionally
removed because it allowed model/vendor-controlled identifiers to cross the reject
authorization boundary.

Submit receipt is deliberately split from prompt
completion: registration and the ACP frame write complete the controller-facing call
with durable `forwarded` state, while a bridge-owned operation continues waiting for
the correlated response, retains the active prompt and prompt lock, and records the
terminal outcome for reconciliation. This keeps the authenticated connection reader
available for status, cancellation, replay and permission decisions during an active
prompt. Cancellation likewise requires an explicit cancellation ID, target request,
epoch/nonce and bounded `session/cancel` envelope. Its canonical intent is persisted
before the bridge-lifetime write; same ID/hash is idempotent, changed reuse rejects,
forwarded receipt is not target completion, and ambiguous writes reconcile as
`uncertain` rather than being retried. The emitted wire is exactly a JSON-RPC 2.0
notification (`jsonrpc`, `method`, `params`, no `id`), and concurrent duplicate
cancellation IDs share one durable single-winner write. Raw ACP event/result/tool
payloads are never journaled: outcomes and replay observations contain only bounded
state/category, correlation, SHA-256 and byte-count metadata.

A bootstrap-only stdin mode atomically creates a single-link `0600` 32-byte key
in the bridge-private `0700` directory and prints only its SHA-256 key ID.
Duplicate bootstrap verifies the exact existing key and never overwrites it.
The legacy local connector still takes the key on stdin and exists only for
hermetic protocol tests and local diagnostics. Controller transport instead uses
`--worker-pipe`, a transparent same-UID Unix-socket byte pipe; no key is placed in
remote argv or environment and authentication remains controller-to-bridge.

The #217 controller core implements disabled-by-default connection, replay,
dispatch, cancellation, permission rejection, heartbeat, provisioning and cleanup
coordinators with injectable bridge/provisioner abstractions and intent-first
SQLite transitions. Schema-v4 also projects bounded pending worker permissions without raw payloads; reject-only decisions reload the authoritative stored tuple/options and require the unchanged cached owner lease. Exact terminal routing has no local/remote fallback, and the viewer role reuses that cached lease without advancing its epoch. The fixed production worker TUI transport/backend is implemented and hermetically validated.

The first managed disposable two-host path was operationally accepted on
2026-09-18 on an authorized disposable topology: source `main` at `c4a7966`,
worker OpenCode `1.18.30`, Docker `29.8.0`, worker host `home-dev-02`. Pinned
ED25519 strict SSH reached an isolated labeled worker with exact latest images.
Redacted evidence covered ACP initialize/new/load; anonymous provider prompt
streaming to completion; a real tool side effect at exact
`/workspace/agentcontrol-217-tool.txt` bytes `TOOL217_OK\n` (SHA-256 `954cf…`);
cancellation delivered on the same lease in ~2.5 s with the active request
cleared; disconnect after a forwarded receipt, reconnect epoch advance, exact
reconcile completion and idempotent resubmit without duplicate; stale-lease
rejection after a competing owner epoch; bounded multi-generation replay both
hermetic and live; worker restart advancing the process generation with no
automatic resume and explicit load of the same native session; and viewer
framing/auth/resize/marker plus detach/reconnect. Live permission handling
covered `[once, always, reject]` and safe-reject `[reject]` with a same-lease
reject pending→decided and prompt completion (compatibility fix PR #255).
Cleanup was exact: zero labeled containers, volumes or tags, and the local key
was removed. Cancellation limitation: OpenCode reported the cancelled turn
completed, so cancellation cleared the active request but was not rollback.
The `home-docker` control portal was deployed with separate schema-v7 UI (schema v8 is not yet deployed) and is
irrelevant to worker flags except portal inspection.

**Three worker classes.** Do not collapse them. (1) The **automatic
controller-local managed employee** is created by owner approval and provisioned
through the privileged `docker-helper`; the control image itself has no Docker
socket and no Docker CLI. (2) A **manually operated remote Docker worker** is
reached by controller-initiated pinned SSH and `docker exec`; it is never created
by hiring. (3) **Future manually enrolled standalone workers** that connect
outbound are **not implemented**: no listener and no enrollment protocol exists.
Class (2) is the first managed disposable two-host path accepted on 2026-09-18;
class (1) is the #272 local managed path.

The #272 slice freezes a managed hire to the controller-local Docker target
(persisted `local-docker` transport kind, schema v11). Approval requires no SSH
host input. Docker operations execute through
`HVO.AgentControl.DockerHelper`, which is the only service mounting
`/var/run/docker.sock`, publishes no ports, runs read-only with `cap_drop: ALL`,
`no-new-privileges` and `init`, runs as uid 1002, and is the only writer of the
helper-socket volume shared with control. The helper accepts only the shared
argv grammar: employee containers receive exactly the four fixed named volumes
and no bind or Docker-socket mount, and the helper fails closed while the
approved base digest is empty. `AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST`
and the daemon `AGENTCONTROL_DOCKER_GID` are deployment inputs.

**Still not implemented or operationally validated:** key rotation/compromise
re-enrollment, production managed hiring/provisioning at scale, and the approval
and managed-provisioning/orientation slice (#260) itself, which is code-complete
and hermetically tested but has never run a live owner-approved hire on a host
(see `docs/PHASE-1-CONTRACTS.md` §10.2/§10.3). The local managed path has been
exercised end to end on one development machine — a generic-employee build, a
real helper-provisioned container, real OpenCode orientation and a hire reaching
`Ready` in about 1m50s — but that is a local dev-machine result only, not a
`home-docker` deployment and not the accepted two-host path. `/api/info` reports
`WorkerControlImplemented=true` and `WorkerControlOperationallyValidated=true`,
covering only the first managed disposable two-host path, and carries
`workerControlValidatedScope="first-managed-disposable-two-host"` as the in-band
bound on exactly that claim; `WorkerControlEnabled`
remains the deployment/configuration gate and is false by default. The optional Compose worker profile is disabled by default, has
no published port or Docker socket, is read-only outside named volumes/tmpfs,
has process/CPU/memory limits and disables automatic restart. That hermetic
Compose profile keeps `network_mode: none`. A controller-provisioned worker
instead attaches to Docker's shared default `bridge`, which grants unrestricted
outbound egress: the public Internet through the daemon's NAT, the Docker host's
bridge gateway and any host services listening there, and any other container
co-attached to the same bridge. The fixed create contract still publishes no
ingress (no `-p`, `--publish` or `--expose`, empty port bindings), the ACP/TUI
listener binds container loopback only, and the supervisor socket is a
container-private path. That is an ingress/exposure guarantee, not isolation or
egress restriction. ACP exit does not
cascade-kill the bridge: the bridge remains available for bounded reconciliation,
but the process slot is terminal for that container because inherited ACP
transports cannot be safely reused. Recovery is explicit container replacement;
there is no claimed in-container child restart path in #213.

## Control contract (design)

Persist task/member/session/request identity before dispatch. Serialize prompts
per session. Treat unconfirmed delivery as uncertain, not safe to retry.

Shutdown/recovery must hold new dispatch, cancel running turns when required,
await observed termination, reconcile effects, persist resumable task state and
only then stop or restart. Loading conversation history is not resuming a
suspended command. A transport ACK is not employee readiness. Phase 1 requires
the current orientation to be both Acknowledged and Comprehended, the exact
owned runtime/TUI to be ready, and no dispatch hold before Ready or task dispatch.

The #213 worker artifact implements durable sequence receipts, ownership leases,
replay bounds and stale-owner fencing on the worker side. #217 controller
integration and routing consume that contract on the first managed disposable
two-host path accepted on 2026-09-18; production managed provisioning remains
pending. An operator stop must
remain authoritative when models fail.

## Access and isolation

Keep private keys out of images, logs and worker mounts. Worker access should use
bounded grants and short-lived scoped credentials. Do not assume an OpenCode
instruction or a default-allow tool setting prevents credential access through
another executable: deny rules are defense-in-depth, and the OS identity split
below is the actual security boundary.

### Controller and agent identities (implemented)

The control container runs two unprivileged identities plus one narrow
privileged launcher. This satisfies the
[Section 6](PHASE-1-CONTRACTS.md#6-credential-and-process-isolation-prerequisite)
prerequisite for the control host. The separate #213 worker image now implements
the worker-image identity split; #217 controller-to-worker integration was
exercised on the accepted disposable two-host path, while production managed
provisioning remains future work.

| Identity | UID:GID | Owns | Login shell |
| --- | --- | --- | --- |
| controller | 1001:1001 | `/control-data` (`0700`), the owner secret, `/agent-config` | `/usr/sbin/nologin` |
| agent | 1000:1000 | `/data/home`, `/data/workspace` (`0700`), tmux, OpenCode, the TUI and PTY bridge | `/bin/bash` |

`/data` itself is **root-owned `0755`**, not agent-owned. An agent-owned parent
would let the agent rename `home`/`workspace` away and leave a symlink for the
next root start to act on; with a root-owned parent the agent cannot create,
rename or unlink anything directly in `/data` while keeping full ownership of
the two subtrees it uses. `src/container/prepare-layout.py` does all of the
startup ownership work through `O_NOFOLLOW` descriptors and `fchown`/`fchmod`,
never through a path, and refuses a symlink, a non-directory or a hard-linked
runtime state file before changing anything. A refusal exits the entrypoint and
the service stays down.

The agent needs a real login shell: tmux resolves `default-shell` from the
account and starts its server through it, so with `nologin` a bare
`tmux new-session` leaves "no server running" (measured). The controller never
starts a shell and keeps `nologin`.

- **Controller-private state.** `/control-data/control.db` is the authoritative
  SQLite store, mode `0600` in a `0700` directory, so the agent cannot read or
  edit the organization/session identity. It records the schema version, uses
  WAL with `synchronous=FULL` and foreign keys, and is single-writer via a
  bounded cross-process lock. The database, its WAL/SHM sidecars and the writer
  lock are all `0600`: the runtime narrows its own creation mask in addition to
  the explicit post-open mode, so a widened parent directory would not expose
  them. Local and container tests assert the configured pragmas, the actual
  sidecar modes and single-writer refusal. **Not validated:** crash durability of
  WAL/`synchronous=FULL` on the real volume filesystem has not been
  crash-tested; only the configured contract and the local/container test
  filesystems are evidenced. A fresh database is never seeded in place: it is
  built in a uniquely named controller-private temporary, checkpointed, closed,
  cleared from the pool and atomically published as `control.db`, so a crash
  before publication leaves no authoritative path and an existing empty or
  header-only file fails closed instead of being reseeded.
  Before the database is built from an existing runtime, the exact adoption
  input is retained create-once as `/control-data/runtime.pre-database.json`
  with its verified digest in `runtime.pre-database.sha256`. Both are `0600`,
  exist before database publication, and conflicting evidence fails closed.
  `/control-data/runtime.json` is **not** written by
  the database-era controller: it is retained as the adoption evidence the store
  was seeded from, along with the byte-exact `/control-data/runtime.pre-isolation.json`
  snapshot and its SHA-256 evidence. `--revert-isolation` refuses once
  `control.db` exists, because the pre-isolation image cannot read the database.
  `/data/runtime.json` is root-owned `0600` and carries only the stale
  pre-isolation copy.
- **Owner secret.** Mounted read-only and owned by the controller. The entrypoint
  verifies from both sides that the controller can read it and the agent cannot,
  and refuses to start otherwise.
- **Orientation.** `/agent-config/orientation-current.md` is host-owned and
  agent-readable (`0644` in a `0755` controller-owned directory): the agent reads
  it but can never rewrite, unlink or swap it. The controller composes canonical
  UTF-8 Markdown from active revisioned organization, department, role and employee
  fragments followed by the host policy; SHA-256 of canonical semantic content
  (excluding fragment record IDs/revision metadata) is the `orientationVersion`.
  Assignment is durable before atomic publication. `Delivered` means the host
  verified the published name, inode ownership/link count, exact bytes and semantic
  hash for the session-associated assignment; it also records the runtime generation
  required to load that artifact, but is not itself runtime or model confirmation.
  Startup after OpenCode process/session establishment confirms the exact assignment,
  version, session and generation loaded, then clears `orientation-reload-required`.
  A live recomposition/delivery deliberately leaves `policy-update` and reload holds;
  comprehension cannot clear them and status exposes `restartRequired`. A version or
  session-binding change, or a terminal failed/rejected/timed-out attempt, marks the
  old assignment Stale and creates a fresh attempt while preserving history. Linux
  publication uses the native stat layout only on x64 and fails closed on unsupported
  Linux architectures; publication errors distinguish persisted assignment from an
  unadvanced delivery. Structured evidence is bounded and host validated against
  persisted facts, including the immutable assignment ID; only sanitized summary/hash
  and truthful `owner-submitted` or `live-model` provenance are retained, not model
  reasoning. The active role fragment is the authoritative mutable role-instruction
  record and carries its own optimistic revision. Live comprehension captures chunks
  in ACP codec-reader order before the lossy observation channel and treats the result
  frame as the barrier. Because ACP exposes no turn ID, prompt operations serialize,
  and the startup bootstrap occupies the same slot and is reported as `busy` before
  readiness is promoted, so owner comprehension is a deterministic conflict while it
  runs; abandoned turns receive bounded `session/cancel`, while their correlator and
  prompt-slot ownership remain indefinitely fenced until an exact terminal frame or
  confirmed transport/process termination. A host-started failure remains bound to
  its exact assignment/session; if policy replacement has made that assignment Stale,
  sanitized live-model failure evidence is added to that historical row without
  changing Stale or the replacement assignment/holds. Recomposition updates the store/artifact without
  rebuilding the image and preserves employee/session/history. OpenCode reads the
  generated file at process start, so affected-runtime restart is the reload mechanism.
  There is still no general task dispatcher.
- **Remote managed orientation (#260, code capability).** For a managed
  DeveloperContainer employee the authoritative store composes and assigns the
  orientation, the controller delivers it over the authenticated bridge with the
  fixed `install-orientation` mutation, and the worker's fixed supervisor writes
  it atomically as the employee owner into
  `/home/worker/.agentcontrol/orientation` (`O_NOFOLLOW` traversal, `0600`,
  fsync, rename); the bridge journal records `installing`/`installed`/`uncertain`
  and the bridge never writes employee-owned state directly. Because **ACP stdio
  descriptors are handed to the bridge only at bridge start**, a running OpenCode
  process cannot load a changed file, so the controller deliberately replaces the
  container — preserving all four named volumes and the authoritative native
  session — and requires the fresh process generation to have loaded the exact
  assignment before comprehension. The bounded, tool-free
  `orientation-comprehension` mutation runs inside that exact worker, captures at
  most 16 KiB, returns only a structurally validated structured-evidence object,
  and the store validates it as `live-model` before `Ready`. See
  `docs/PHASE-1-CONTRACTS.md` §10.2. This path is hermetically tested but has not
  run a live owner-approved hire.
- **Permission policy.** Stable persisted restrictions are attached to host,
  organization, department, role and employee layers. Evaluation collects every
  active matching restriction; any non-waivable match rejects regardless of order,
  and every waivable match requires its own exact active owner grant. OpenCode routes
  broad bash/read/edit classes through `ask`, while explicit secret/controller paths
  and Fleet/network classes remain local denies. The pinned ACP 1.18.30 callback has
  no host-derived canonical path/resource—only an untrusted model-facing title—so
  Phase 1 never selects an allow option. Owner grants are validated, persisted and
  audited as staged records for a future canonical adapter, but are not executable.
  Requests are persisted directly as synchronously rejected; pending/cancellation
  recovery is reserved for the future worker bridge and is not claimed by this
  callback path. Audits retain the decisive restriction plus a canonical JSON list
  of every matched stable restriction ID. Requests and decisions persist only
  fixed/bounded claims, hashes and sanitized summaries; no requested secret content
  is retained.
- **Privileged launcher.** `src/launcher/agentcontrol-launch.c` is the container's
  only setuid binary (root:control, mode `4750`, so the agent cannot execute it).
  It exposes four fixed operations — `acp`, `tmux`, `pty` and `signal` — with the
  executable always one of four compiled-in paths and no parameter for a program,
  UID, capability set or environment block. Every exec drops irreversibly to the
  agent identity, sets `no_new_privs`, leaves the child with empty permitted and
  effective capability sets, rebuilds the environment from an allow-list, closes
  inherited descriptors while still privileged, and re-checks the resolved
  working directory after `chdir` so a symlink inside `/data` cannot place the
  child outside the agent tree. The launcher does **not** empty the child's
  capability *bounding* set: `PR_CAPBSET_DROP` needs `CAP_SETPCAP`, which the
  entrypoint has already removed, so that attempt is a no-op here. The bounding
  set the child inherits is the one the entrypoint installed
  (`SETUID | SETGID | KILL`), measured as such in the container suite.

  **What the launcher does not bound.** The `tmux` operation's arguments are the
  caller's, and `new-session`/`new-window` take a child command vector — so the
  controller *can* run a program of its choosing, including a shell, inside a
  pane. That is the intended contract (OpenCode's TUI is such a child). The
  guarantee is confinement, not an execution allow-list: whatever runs, runs as
  UID 1000 with no capabilities and cannot reach `/control-data`, the owner
  secret, the launcher binary or any root-owned path. Subcommands that execute
  outside a pane or reconfigure the server (`run-shell`, `if-shell`,
  `source-file`, `kill-server`) are not allow-listed.
- **Cross-UID lifecycle.** A UID 1001 parent cannot signal its UID 1000 children,
  so termination uses the launcher's `signal` operation, which refuses anything
  that is not a live agent-identity child of the calling controller. The
  controller reaps its own direct children through the ordinary wait path; tini
  is PID 1 for the descendants that outlive their parent and would otherwise
  accumulate as zombies.
- **Capabilities.** `CHOWN`, `DAC_OVERRIDE` and `FOWNER` exist only for the root
  entrypoint's one-time ownership work on the pre-isolation volume. Because the
  setuid launcher regains everything in the bounding set while it is root, the
  entrypoint uses `SETPCAP` to remove those three (plus `FSETID` and `SETPCAP`
  itself) from the bounding set in the same `setpriv` call that drops to the
  controller. The controller and every descendant therefore hold a bounding set
  of `SETUID | SETGID | KILL` only. Without `SETPCAP` the entrypoint refuses to
  start rather than serve a weaker boundary than this document describes.

  Verify it against the controller, not PID 1. The bounding set is reduced in
  the same `setpriv` call that execs the controller, so PID 1 (tini) still
  reports the startup capabilities. The entrypoint records the controller's own
  PID — it `exec`s in place, so the PID written before the exec stays its own:

  ```bash
  docker compose exec -T control sh -c \
    'grep CapBnd /proc/$(cat /control-data/controller.pid)/status'
  ```

  `pgrep -f HVO.AgentControl.dll` is not a substitute: the pattern occurs in the
  inspecting command's own command line, so pgrep matches that shell and returns
  a PID even when no controller is running (measured). The PID file is
  meaningful only while the container runs.

### Rollback and roll-forward (state, not just ownership)

**Database era:** once `/control-data/control.db` exists, it is the sole
authority and `--revert-isolation` **fails closed before changing anything**.
The pre-isolation image understands only `runtime.json`, so republishing it
would run old JSON-only code against stale session identity while the database
held the real state. No compatible downgrade is provided; restoring a verified
backup of the whole controller-private volume is the owner-run recovery path.
Before the first live deployment of schema v9, stop/quiesce the controller and
snapshot the current private volume (including `control.db` and any SQLite
sidecars) through the deployment's existing backup workflow, then verify that
snapshot before starting the new image. The automatic schema-v7 and schema-v8
backups are pre-migration evidence only; they are not a current schema-v9
recovery point and cannot recover profiles, builds or other writes made after
migration.
`prepare-layout.py` likewise treats a JSON-only divergence as evidence rather
than blocking the database-era start.

The pre-database rollback contract below still applies only while no database
exists. Ownership hand-back alone is not a correct rollback for the runtime
state, and the distinction is a data-loss boundary rather than a detail:

| Target | Rollback treatment |
| --- | --- |
| `/data/runtime.json` | **Republished** from the current controller-private state, then owned `1000:1000` `0600` |
| `/data` | Metadata only: root → `1000:1000`, `0755` |
| owner secret | Metadata only: `1001:1001` → `1000:1000`, `0600`; never read, rotated or rewritten |

Re-owning the stale legacy file instead would resume the pre-isolation image on
the organization, session and tmux owner token as they were at adoption, silently
discarding the isolated run. The republish is atomic within the data volume (an
`O_EXCL` temporary in the destination directory, final ownership and mode applied
before it is visible, `fsync`, `renameat` by directory descriptor, directory
`fsync`) and refuses a symlinked, hard-linked or unexpectedly owned path. Across
the three volumes there is no transaction, so the operation is **replayable
instead of atomic**: every step is idempotent, nothing is deleted, and a failure
is repaired by re-running the identical command.

Replayable is bounded, and the bound is a data-loss boundary in its own right.
A replay is safe only while `/data/runtime.json` still holds what the rollback
published; once the pre-isolation image has run it advances that file, and
republishing the older private bytes over it would destroy that work. The
rollback therefore validates the recorded `rollback.active` against the bytes on
the legacy path **before its first write or chown**.

There is no automatic merge between a state the pre-isolation image advanced and
the state the isolated run left behind. `--revert-isolation` therefore records
`/control-data/rollback.active`, and `prepare-layout.py` fails the start while it
is unresolved instead of silently picking a side. `--resume-isolation
--state-source legacy|private` is the only thing that clears it; the state that
loses is preserved under a timestamped name, never deleted.

#### Marker durability and the two crash windows

**The marker is durable before the runtime state is republished and before every
ownership hand-back.** Ordering only the `chown` calls after the marker was not
sufficient, and that gap was a real data-loss path: the republish creates a new
inode and renames it over `/data/runtime.json`, and it previously gave that inode
its `1000:1000` ownership at creation time. Publishing was therefore itself an
ownership hand-back that ran *before* the marker existed — a crash in between left
a legacy state the pre-isolation image could read and advance with no record of
it, and the next replay, finding no marker, treated the advanced state as the
ordinary first rollback and overwrote it.

The order is now: decide, record the **intent** marker, publish **root-owned
`0600`**, hand back explicitly, complete the marker. The marker carries three
digests so a replay classifies what it finds rather than guessing:

| Field | Meaning |
| --- | --- |
| `prepublicationLegacySha256` | What `/data/runtime.json` held before this rollback touched it; `null` when there was no legacy file. |
| `intendedSha256` | What this rollback will publish; `null` when nothing will be (`--accept-missing-runtime-state`, or a rollback out of an unrecorded divergence that keeps the legacy bytes). |
| `publishedSha256` | What the legacy path holds now; `null` while `phase` is `"intent"`. |

A replay reads the legacy path and matches:

| Observed | Classification | Action |
| --- | --- | --- |
| `legacy == prepublicationLegacySha256` | The publication never ran. | Proceed and publish; nothing is lost. |
| `legacy == intendedSha256` | The publication already landed. | Converged — the inode is **not** replaced, only the remaining hand-backs are re-applied. |
| Neither | The old image or another writer advanced it. | Refuse before the first write or chown; route to `--resume-isolation`. |

Every failure mode is therefore a safe one. A marker that cannot be published
aborts while the legacy state keeps its prior owner **and** its prior bytes, so
the old image cannot start at all. A crash after the marker but before the publish
is classified by `prepublicationLegacySha256` and re-runs cleanly. A crash after
the root-owned publish but before the hand-back is classified by `intendedSha256`,
and until that hand-back runs the published bytes are root-owned, so the old image
cannot read a state this run has not finished committing to.

Because the republish replaces the inode, the descriptor opened during validation
refers to a **superseded** one. Handing that back would give UID 1000 an
unreachable orphan while the file the old image actually opens stayed root-owned —
a rollback that reports success and leaves the old image unable to read its own
state. The published name is therefore re-opened by directory descriptor under the
same no-follow/hard-link/owner rules, its bytes re-verified against the digest just
published, and only that descriptor is handed back.

The two markers share a file name and are distinguished by `reason`, branched on
explicitly and never inferred from which fields are populated. `operator-rollback`
records a publication (or the intent to perform one) that a replay may resume;
`unrecorded-legacy-divergence` records a divergence that was only *detected*, with
all three publication digests `null` and the diverged bytes in `legacySha256`.
Reading the latter as a publication record would republish the current private
state over exactly the bytes the operator is rolling back to keep. Both carry
`schema: 2`.

"Preserved, never deleted" also has to survive a name collision: the archive name
carries a one-second timestamp, so two resumes inside one second (or after a clock
step) would choose the same name. Archives are therefore published
**create-never-replace** — written to a private temporary and `link`ed into the
first unused name, so `EEXIST` makes the free-name test and the claim on it one
atomic step — rather than renamed over whatever is there. Publication temporaries
use a random suffix rather than the PID, because a container restart reuses low
PIDs and a leftover temporary would otherwise make the `O_EXCL` create fail on the
same name at every later attempt.

A legacy file written outside isolation without a recorded rollback fails closed
the same way, but failing alone was not sufficient: the resume path is driven by
the marker, so a refusal that recorded nothing left every later start failing
identically with nothing to resolve. `prepare-layout.py` now writes the
reconciliation marker itself — `reason: unrecorded-legacy-divergence`, both
digests and sizes, the detected legacy owner, and null `publishedSha256`,
`intendedSha256` and `prepublicationLegacySha256` because nothing was written to
the legacy path — and only then fails. Rolling back out of that state is
supported and keeps the diverged legacy bytes rather than overwriting them; if
that legacy file has itself moved on since the divergence was detected, the
rollback refuses rather than choosing a survivor on the operator's behalf.

### Interrupted first adoption

Adoption is three writes, not one: publish the controller-private copy, record
the snapshot plus its evidence, then reduce the legacy file to root-owned `0600`.
A crash between them is resumable, and each boundary has a defined outcome:

| Observed state | Treatment |
| --- | --- |
| Private copy exists, legacy still UID 1000, **bytes equal** | Interrupted adoption. Backfill snapshot/evidence, re-own the legacy file, continue. |
| Private copy exists, legacy still UID 1000, **bytes differ** | Divergence. Record the reconciliation marker, then fail closed. |
| Snapshot exists, evidence missing | Generate evidence *from the snapshot's own bytes*; the snapshot is never rewritten. |
| Snapshot and evidence disagree on hash or size | Corruption. Fail the start rather than treat a damaged historical record as authoritative. |

The snapshot is validated on every start, not only on the start that writes it,
and must be a controller-owned regular file.

Because the setuid launcher is the elevation mechanism, the container cannot run
with `no-new-privileges`. Every other setuid binary is stripped from the image at
build time to keep that from becoming a general escalation surface. A compromised
controller can therefore execute code as the agent identity — which it already
  drives by design — but never as root, as its own UID, or as any other identity.
- **Tmux metadata trust.** The tmux server, owner environment and pane option
  live under the agent UID. The agent can read or forge those values and can
  disrupt its own TUI. They are routing/cleanup evidence for a controller that
  already owns the binding, not authentication or authorization evidence for
  organization records, hiring, permissions or another employee.
- **Worker viewer credential boundary.** Worker PID1 retains a random native
  OpenCode password only in memory and passes it to the employee ACP and attach
  children. Because those children share the employee UID, employee code can
  inspect that environment and disrupt its own TUI; the credential protects only
  the loopback HTTP endpoint from unrelated identities and is not a controller
  secret or sandbox boundary.
- **Worker network egress and lateral reachability.** A controller-provisioned
  worker attaches to Docker's shared default `bridge`, which grants unrestricted
  outbound egress: the public Internet through the daemon's NAT, the Docker
  host's bridge gateway and any host services listening there, and any other
  container co-attached to the same default bridge. The fixed contract guarantees
  only that no ingress is published (no `-p`, `--publish` or `--expose`, empty
  port bindings) and that the ACP/TUI listener and supervisor socket stay
  container-private; it is not a network-isolation or egress-restriction
  boundary. A per-worker dedicated network, or a host firewall/NAT policy that
  restricts egress to the approved provider, is possible future work.
Real alternate-UID tests live in
`tests/HVO.AgentControl.Tests/AgentIsolationContainerTests.cs`, including the
planted-symlink escalation attempts, the capability bounding set, the `/bin/sh`
confinement case, the state-republishing rollback (including the diverged-state
case, a deployment with no legacy file, replayed runs, the replay refusal once
the old image has advanced the legacy state, and substituted paths), both crash
windows around the root-owned republish, the published-versus-superseded inode
hand-back, fail-closed unsupported-marker-schema handling, the
interrupted-adoption crash boundaries, the recorded
reconciliation marker, the fail-closed roll-forward interlock and the recorded
controller PID. CI runs them in the `docker` job with
`AGENTCONTROL_DOCKER_REQUIRED=1`, so an unavailable daemon fails the job instead
of skipping the suite, and asserts the exact TRX counters (44
discovered/executed/passed, 0 skipped — 42 container test cases plus 2
structural wiring tests) rather than a lower bound.

Separate credentials and process identities further before adding untrusted
repository execution.

V1 is reference only. No V1 database migration or runtime compatibility is
promised. V2 starts with new state and explicitly provisioned environments.
The accepted path does not validate key rotation or compromise re-enrollment and
does not authorize production managed hiring/provisioning. The #260 approval and
managed-provisioning/orientation paths, and the #272 controller-local helper
path, exist as tested code but have no live owner-approved hire evidence on any
host.
