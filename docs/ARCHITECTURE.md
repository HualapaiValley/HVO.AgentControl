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
  terminal connects to it through the portal over a same-origin authenticated
  WebSocket; one viewer at a time. The TUI is a human view, not a second engine.
- **Persistence:** `/control-data/control.db` is the authoritative SQLite store. The released/authoritative lineage is schema v2; this unmerged branch uses a build-local schema-3 signature for orientation work. Schema 3 has never been released or deployed, is accepted only on an exact signature match, and has no migration or compatibility promise for earlier branch-local v3 files.
  for organization, department, role, employee, runtime-binding and ACP-session
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
This slice does not provision developers, route tasks, or implement the full
organization lifecycle.

## Communication

ACP is newline-delimited JSON-RPC over process stdin/stdout. OpenCode's `--port`
flag exposes its embedded HTTP server, not native ACP-over-TCP. The control host
talks ACP over stdio and uses loopback HTTP only to read native session state
(for example the authoritative model and session status). Native HTTP never
leaves the container. The browser talks only to the portal.

## Planned components (not implemented)

- **Worker container:** self-contained execution environment with its own
  OpenCode runtime, private home/session storage and private repository storage.
  No shared worker checkout, worktree or writable coordination database.
- **Worker bridge:** process owner for OpenCode ACP stdio that keeps worker
  lifetime independent of a controller connection. It is infrastructure, not
  another model agent.
- **Human views:** optional proxied native web plus tmux-hosted TUI attach.

There is **no worker bridge or reconnection implementation in the active code**.
The standalone POC demonstrated the transport idea only; see
[POC findings](POC-FINDINGS.md). The concrete Phase 1 organization, placement,
storage and transport contracts are proposed in
[Phase 1 contracts](PHASE-1-CONTRACTS.md). Section 11's bounded orientation and
host permission-policy slice is implemented for the single Operations/IT employee;
worker transport and general dispatch remain future.

## Control contract (design)

Persist task/member/session/request identity before dispatch. Serialize prompts
per session. Treat unconfirmed delivery as uncertain, not safe to retry.

Shutdown/recovery must hold new dispatch, cancel running turns when required,
await observed termination, reconcile effects, persist resumable task state and
only then stop or restart. Loading conversation history is not resuming a
suspended command. A transport ACK is not employee readiness. Phase 1 requires
the current orientation to be both Acknowledged and Comprehended, the exact
owned runtime/TUI to be ready, and no dispatch hold before Ready or task dispatch.

Bridge/controller reconnection needs durable sequence receipts, ownership
leases, replay bounds and stale-owner fencing. None of this is implemented in the
baseline. An operator stop must remain authoritative when models fail.

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
prerequisite for the control host; the worker-image half of Section 6 is still
future work.

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
  runs; abandoned turns receive bounded `session/cancel` and fence retries until remote
  completion or process restart. Recomposition updates the store/artifact without
  rebuilding the image and preserves employee/session/history. OpenCode reads the
  generated file at process start, so affected-runtime restart is the reload mechanism.
  There is still no general task dispatcher.
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
