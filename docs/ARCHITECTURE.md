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
- **Persistence:** `/data/runtime.json` holds organization/session identity;
  the data volume retains workspace and native conversation state. The private
  OpenCode home also lives under the data directory. A failed session load is
  surfaced, not silently replaced.
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
storage, orientation and transport contracts are proposed (not implemented) in
[Phase 1 contracts](PHASE-1-CONTRACTS.md).

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
another executable. In the current slice the controller and OpenCode share one
container OS user, so deny rules are defense-in-depth, not OS isolation. Separate
credentials and process identities before adding untrusted repository execution.

V1 is reference only. No V1 database migration or runtime compatibility is
promised. V2 starts with new state and explicitly provisioned environments.
