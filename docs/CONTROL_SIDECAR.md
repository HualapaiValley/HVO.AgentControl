# Host-owned OpenCode control service

Owner decision, 2026-09-08. [Issue #140](https://github.com/RoySalisbury/HVO.AgentControl/issues/140) tracks replacing the coordinator-only SSH runtime with one lightweight OpenCode sidecar per AgentControl installation. The sidecar hosts a host-operations conversation and separate controller/workgroup conversations. They are control sessions, excluded from development worker capacity. The C# service owns timers, scheduling, authorization, durable delivery and recovery; OpenCode supplies model reasoning.

## Implemented foundation and remaining integration

`docker/opencode-control` and `compose.control.yaml` provide an independently deployed headless server. This initial slice does **not** connect the running coordinator to it. Existing SSH coordinator enrollment and its saved session remain in use until direct transport, durable control-session bindings and migration acceptance are implemented. Creating the sidecar is not registering it in AgentControl. Do not create a development worker as a substitute for that integration.

The existing `OpenCodeClient` already uses HTTP/SSE and verifies version/schema; the current `IRuntimeTransport` lifecycle is SSH/workspace oriented. Add a distinct control-service connection that reuses the client without invoking SSH verification, workspace creation, tmux bootstrap or development-host capability probes. Disposing this connection closes network clients only. Stopping/replacing OpenCode is a separate explicit lifecycle operation.

## Image and deployment contract

The upstream **1.18.29** multi-platform image is pinned by manifest digest. Its Alpine base uses native musl executables for x64 and ARM64, plus their runtime libraries and ripgrep. Our derivative adds curl for an authenticated health check and an unprivileged UID/GID 1000. It contains no SSH server/client, tmux, Git, .NET SDK, Docker CLI or Docker socket. OpenCode's executable accounts for much of the image size, so a minimal OS does not make the entire image a few megabytes. [Pinned upstream Dockerfile](https://github.com/anomalyco/opencode/blob/v1.18.29/packages/opencode/Dockerfile), [release](https://github.com/anomalyco/opencode/releases/tag/v1.18.29).

`opencode serve --hostname 0.0.0.0 --port 4096` runs in the foreground with Docker's init process. OpenCode supports headless HTTP/SSE and HTTP Basic authentication; our entrypoint requires a server password before starting it. This password is separate from provider credentials. [OpenCode server documentation](https://opencode.ai/docs/server/).

| Concern | Contract |
| --- | --- |
| Networking | Compose user-defined bridge, DNS service `opencode-control`, container port 4096, no published host port. Only trusted services should join this network. Docker host administrators can still reach container addresses. |
| Provider access | Bridge retains outbound access to remote providers. `internal: true` by itself would block that access. Other Docker hosts require a separately designed authenticated transport; do not publish plaintext Basic auth to a LAN. |
| Web lifecycle | Independent Compose deployment/project; web-only rebuild/recreate must leave this service running. Do not use a combined `down` that stops both. |
| Storage | Named volume at `/var/lib/opencode` contains home, data, state, config, cache and control workspace. Native session database and provider registrations persist together. Do not share a writable volume between two OpenCode processes. |
| Secret | Required read-only file `/run/secrets/opencode-server-password`; 32–256 base64/base64url or hexadecimal characters. Entry reads it into process environment without recording its value in Compose or command arguments. Health check passes auth through stdin. Docker administrators can inspect process state. |
| Process | UID/GID 1000, read-only root filesystem, writable state volume and bounded `/tmp`, dropped capabilities, no-new-privileges, Docker restart policy. Docker health status is evidence; `unless-stopped` restarts exited processes, not a merely unhealthy/hung process. |
| Resources | Initial configurable 2 GiB limit, one CPU and 256 PIDs. These are guardrails, **not** certified capacity for several concurrent conversations. Measure concurrent workloads and retained context before choosing production limits. |
| Tools | Initial control config denies native tools and disables sharing/autoupdate. This matches the current decision-only coordinator. A future bounded service API/MCP tool allowlist must be deliberate; no development SDK or shell-based GitHub workflow is needed just to ask a model for a decision. |

Conversation IDs separate history; they do not isolate credentials, process memory, filesystem or global configuration. All sessions in one sidecar share a trust boundary. Different tenants or incompatible provider policies require separate services.

## Start an isolated service

This is an opt-in infrastructure operation; it is not required for the existing demo. Create a new dedicated secret in a private directory. The bind-mounted file must be readable by container UID 1000 (Compose file secrets do not remap host ownership):

```bash
install -d -m 700 .fixture/control-secrets
python3 - <<'PY'
from pathlib import Path
import secrets
p = Path('.fixture/control-secrets/opencode-server-password')
with p.open('x') as f:
    f.write(secrets.token_hex(32) + '\n')
p.chmod(0o600)
PY
# If the current user is not UID 1000, set ownership appropriately before starting.
export CONTROL_OPENCODE_PASSWORD_FILE="$PWD/.fixture/control-secrets/opencode-server-password"
docker compose -f compose.control.yaml up --build -d opencode-control
docker compose -f compose.control.yaml ps
```

The secret creation intentionally refuses to overwrite an existing key. Keep it stable across recreation. Provider registration is a later integration step, using existing AgentControl credential policy; this command does not copy credentials, call a model or migrate a session. `CONTROL_OPENCODE_MEMORY_LIMIT` overrides the initial `2g` guardrail.

For the forthcoming direct adapter, the web service joins this same bridge and uses `http://opencode-control:4096` with the same secret reference. Current `compose.demo.yaml` uses `network_mode: bridge` for saved SSH worker IPs; attaching the web container needs an explicit tested Compose/network change. This slice leaves that deployment intact. A browser continues to reach AgentControl through its existing host IP/port, not OpenCode.

Native upgrades/recreation must be drained or classified as interruption. Back up the state volume while OpenCode is stopped, or use a verified native-consistent backup method; copying a live SQLite file alone is insufficient. Never run `down --volumes` as routine deployment. Retained session history is not proof that an interrupted turn resumed or that a tool had no side effect.

## Session registry and supervision follow-on

Persist installation/service identity, endpoint/secret reference, version and observed incarnation separately from native session IDs. A binding needs a stable purpose (`HostOperations`, `WorkgroupCoordinator`, later an operations/model adviser), owner/controller/workgroup ID, native session ID, canonical sidecar directory, revision and lifecycle state. Provision/rebind should have durable idempotent receipts. Lost create responses require discovery/reconciliation, not blind session duplication.

At host startup, load existing mappings, verify the same service and recover visibility. Before another prompt, reconcile native history with durable command/message IDs. A web restart preserves an independently running native turn. A sidecar restart can preserve history while destroying computation and pending in-memory interactions; record interruption/uncertainty and reconcile effects before any linked continuation. Do not use runtime connection generation or an empty `/session/status` alone as proof of successful recovery (#120).

The current global one-unfinished-coordination-run check must become controller/workgroup scoped before simultaneous workgroups are advertised. Worker reservations remain global and atomic across those runs. Host-operations and adviser sessions cannot bypass worker claims or make competing lifecycle decisions (#98/#121).

When tools are added, expose bounded worker/capability/backlog/evidence queries and authorized actions through the same C# application services and REST contracts. Bind credentials and actions to the calling control session/run; validate revisions, eligible workers, ownership and provider policy, and persist receipts. Model failure, malformed output and unavailable providers must not stop the C# heartbeat. Native provider registration still requires a usable credential and readiness observation (#54/#82).

## Validation and migration gates

Run `python3 tests/control_sidecar_smoke.py` on a Docker host. CI executes it in a separate job. It renders the shipped Compose configuration and exercises its image/settings with uniquely owned disposable Docker resources. The probe checks missing-secret failure, non-root execution, absent development/SSH tools, no published ports, unauthenticated denial, authenticated health/schema/session APIs and SSE connectivity, separate session histories, client replacement without process replacement, and container recreation retaining native IDs/history. Cleanup uses captured IDs and ownership labels.

The probe uses `noReply` message persistence: **no model inference or provider credentials**. A disposable HTTP client stands in for the future adapter; this is not a test of an integrated AgentControl web restart. Architecture output records the actual tested platform; building a multi-platform manifest is not ARM64 runtime acceptance. Resource saturation, active inference through web redeploy, SSE reconnection/event reconciliation, pending tools through native restart, provider rotation, and coordinated multi-workgroup scheduling remain additional acceptance gates.

Only then migrate the legacy coordinator: stop new decisions, settle known/unknown commands without replay, record existing identities/history/credentials, create or explicitly transfer the canary binding, verify a real coordinator decision and worker receipt, restart the web alone, and prove continuous supervision. Retire only the old owned coordinator process/container after this succeeds. Preserve the rollback record and native history; never imply restoring a registration resurrects a stopped turn.
