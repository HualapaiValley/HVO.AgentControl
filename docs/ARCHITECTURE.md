# V2 Architecture Baseline

## Components

- **Control host:** central .NET application, durable database and policy owner.
  Its management OpenCode runtime advises on development work; deterministic
  application code owns dispatch, access decisions and recovery.
- **Worker container:** one self-contained execution environment with its own
  OpenCode runtime, private home/session storage and private repository storage.
  No shared worker checkout, worktree, or writable coordination database.
- **Worker bridge:** process owner for OpenCode ACP stdio. It maintains worker
  lifetime independently of a controller connection. It is infrastructure, not
  another model agent.
- **Human views:** optional native web proxy and tmux-hosted TUI attach client.
  They connect to the same OpenCode runtime/session as ACP. The TUI is not a
  second engine and is not needed for automated execution.

## Communication

ACP is newline-delimited JSON-RPC over process stdin/stdout. OpenCode's port
flags expose its embedded HTTP server, not native ACP-over-TCP. A bridge may
carry ACP frames over an authenticated private connection to the control host.

Only the control host exposes an owner-facing interface. Workers publish no
ports. Private Docker networks still need authentication and authorization;
remote Docker contexts do not join networks on different hosts automatically.
Hosted model inference and GitHub require deliberate outbound connectivity.

## Control Contract

Persist task/member/session/request identity before dispatch. Serialize prompts
per session. Treat unconfirmed delivery as uncertain, not safe to retry.

Shutdown/recovery must hold new dispatch, cancel running turns when required,
await observed termination, reconcile effects, persist resumable task state and
only then stop or restart. Loading conversation history is not resuming a
suspended command. An ACK is explicit readiness, not just any model reply.

Bridge/controller reconnection needs durable sequence receipts, ownership
leases, replay bounds and stale-owner fencing. These are not implemented in
the baseline. An operator stop must remain authoritative when models fail.

## Access

Retain the existing control-host GitHub App; do not create a replacement by
default. Keep private keys out of images, logs and worker mounts. Worker access
should use bounded grants and short-lived scoped credentials. Do not assume
an OpenCode instruction or a default-allow tool setting prevents credential
access through another executable.

V1 is reference only. No V1 database migration or runtime compatibility is
promised. V2 starts with new state and explicitly provisioned environments.
