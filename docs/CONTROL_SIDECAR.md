# Host-owned OpenCode control service

Owner decision, 2026-09-08. [Issue #140](https://github.com/RoySalisbury/HVO.AgentControl/issues/140) tracks replacing the coordinator-only SSH runtime with one lightweight OpenCode sidecar per AgentControl installation. The sidecar hosts a host-operations conversation and separate controller/workgroup conversations. They are control sessions, excluded from development worker capacity. The C# service owns timers, scheduling, authorization, durable delivery and recovery; OpenCode supplies model reasoning.

## Host integration

The host connects directly over authenticated HTTP/SSE using the existing durable command pipeline. Register the sidecar on **Control services** (`/control-services`), using its private endpoint, the expected instance UUID from the owned volume, and a mounted password reference. Registration verifies the manifest and pinned native API before saving, then records creation of the host-operations conversation. Add one workgroup conversation per controller/workgroup. Control sessions are excluded from development worker selectors and capacity; host operations is also excluded from development coordination.

`ControlServiceRecord` owns endpoint and native instance identity; `ControlSessionBinding` owns the stable scope, creation receipt and native session identity. Internal runtime/coordinator records are compatibility projections for command delivery and existing conversation UI, not development runtime registrations. There is one control service per host and at most 64 scopes. At most two control model turns dispatch concurrently, independently of development capacity. Different workgroups have separate coordination runs; each receives scheduling and heartbeat opportunities, and shared worker claims remain atomic across runs.

The direct adapter performs no SSH verification, tmux bootstrap, workspace creation or development capability probe. Disposing the host closes HTTP/SSE clients only. The container owner controls native lifecycle. Worker creation, terminal access, SSH profile edits, development environment configuration, GitHub credential delivery and destructive runtime stop are rejected for this service. Provider Go key delivery and ChatGPT device enrollment use the existing encrypted credential and native HTTP services from the control-service page.

## Owner API

All mutations use the existing authenticated owner session and CSRF protection.

| Operation | Endpoint |
| --- | --- |
| Services, health and bindings | `GET /api/v1/control-services` |
| Verify and register | `POST /api/v1/control-services` with `id` (N-format GUID), `name`, `endpoint`, `expectedInstanceId`, `passwordReference` |
| Ensure a scope | `POST /api/v1/control-services/{id}/sessions` with request `id`, `scopeKind`, `scopeId`, `name`, optional `providerId`, `modelId`, `variant` |
| Retry known rejected creation | `POST /api/v1/control-services/{id}/sessions/{sessionId}/retry` with request `id`, `expectedRevision` |
| Migrate paused coordination | `POST /api/v1/coordinations/{id}/control-session` with request `id`, `expectedRevision`, `controlSessionId` |

Valid scopes are `HostOperations` / `host` (created by registration) and `Workgroup` / an owner-defined stable ID. Repeating the same scope/settings discovers the original binding with a separate discovery receipt; it does not create a second native session or change current model settings. Edit a bound session through the page or existing worker settings endpoint using its compatibility `workerId`. Connection refresh/disconnect and provider endpoints likewise use the service ID as the internal runtime ID. API responses retain explicit provisioning and delivery states.

A creation intent and unique native title are committed before the native POST. Lost responses are recovered by bounded discovery of that title and canonical directory. Missing evidence never authorizes another POST. An explicit retry is allowed after known rejection or cancellation before dispatch; acknowledging uncertain delivery does not make it safe to retry. Original creation receipts remain in the journal. Native history can still settle acknowledged uncertainty when the original session is discovered.

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

## Service identity and process incarnation

Before starting OpenCode, the entrypoint takes a nonblocking exclusive `flock` on `/var/lib/opencode/.agentcontrol-service.lock`. Its open descriptor survives `exec` into OpenCode; the real-container smoke verifies that a second sidecar using the same volume fails before changing the manifest. The pinned image already includes BusyBox `flock`; no daemon or SSH transport is involved. Never delete or replace the lock file while a process may own it. All processes sharing this state must use the shipped entrypoint; the lock does not constrain a Docker administrator who bypasses it.

The stable UUID is stored in `/var/lib/opencode/state/agentcontrol-instance-id`. A corrupt identity, or an identity missing while a previous manifest exists, fails startup without generating a replacement. An operator must investigate and restore the original identity from trusted state. The entrypoint publishes `/var/lib/opencode/workspaces/control/.agentcontrol-service.json` through atomic rename before starting each native process:

```json
{
  "schemaVersion": 1,
  "instanceId": "a7e2d753-2521-4e77-b0f9-c49b5eb4917e",
  "incarnationId": "25e78c20-ea4e-462d-aee0-b499acbbd737",
  "startedAt": "2026-09-08T08:00:00Z",
  "directory": "/var/lib/opencode/workspaces/control"
}
```

Read it through authenticated `GET /file/content?path=.agentcontrol-service.json&directory=/var/lib/opencode/workspaces/control`. Native 1.18.29 returns a JSON envelope with `type: "text"` and a string `content` containing the manifest JSON. Unauthenticated access returns 401. Validate the schema, canonical directory and expected instance UUID before using a saved session binding. Reconnecting a web client leaves every field unchanged; recreating the sidecar with the same volume preserves `instanceId` and changes `incarnationId`. The timestamp describes entrypoint startup, not an uptime measurement or proof of a completed request. Compare incarnation IDs to detect native replacement; do not infer successful recovery or replay an uncertain instruction from this manifest. Restoring a backup or cloning a volume requires an explicit reconciliation decision, since copied identity and history do not establish continuity of computation.

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

The secret creation intentionally refuses to overwrite an existing key. Keep it stable across recreation. Provider registration follows through the Control services page using existing AgentControl credential policy; this infrastructure command does not copy credentials, call a model or migrate a session. `CONTROL_OPENCODE_MEMORY_LIMIT` overrides the initial `2g` guardrail.

The direct adapter uses `http://opencode-control:4096` with the same secret reference. For the demo's saved SSH worker IPs, use the optional web overlay and host deployment helper described below to retain `network_mode: bridge` while adding the private connection. A browser continues to reach AgentControl through its existing host IP/port, not OpenCode.

Native upgrades/recreation must be drained or classified as interruption. Back up the state volume while OpenCode is stopped, or use a verified native-consistent backup method; copying a live SQLite file alone is insufficient. Never run `down --volumes` as routine deployment. Retained session history is not proof that an interrupted turn resumed or that a tool had no side effect.

## Transitional web deployment on the legacy Docker bridge

The demo's saved SSH runtime addresses currently belong to Docker's built-in `bridge`. Compose 5.5.0 cannot declaratively attach that bridge alongside a user-defined network: clearing `network_mode` with `!reset` and declaring external network `bridge` fails container creation because Compose adds network aliases that the built-in bridge rejects. Keeping `network_mode: bridge` together with service `networks` is also invalid. This was reproduced with disposable containers; it is not a reason to change working SSH runtime addresses during the control-service cutover. [Compose networking reference](https://docs.docker.com/reference/compose-file/services/#network_mode), [Compose 5.5.0 alias construction](https://github.com/docker/compose/blob/v5.5.0/pkg/compose/create.go#L439).

`compose.control.web.yaml` therefore declares an opt-in `com.hvo.agentcontrol.control-network` label while preserving the demo's network mode, data mounts and published UI port. The host-only `scripts/connect-control-network.py` resolves the web container and control network by exact Compose ownership labels, checks the sidecar's private DNS alias, and attaches the captured web container ID to the captured network ID. The new connection uses gateway priority `-1`, preserving the built-in bridge's gateway preference. Missing opt-in labels, ambiguous resources and mismatched expected identities fail before attachment. An already-correct attachment is a verified no-op; no container receives a Docker socket or Docker CLI.

After starting the independent sidecar, use this deployment sequence, including any usual image override and `DEMO_BIND_ADDRESS` setting:

```bash
docker compose -f compose.demo.yaml -f compose.control.web.yaml up -d --no-deps agentcontrol
python3 scripts/connect-control-network.py
```

The deployment workflow must invoke the helper automatically after **every** web create/recreate and before declaring deployment ready. Include the opt-in overlay in later deployments. A standalone manual `docker network connect` is insufficient because Compose recreation removes that attachment. The helper accepts `--expected-container-id` and `--expected-network-id` for deployment identity checks; these are Docker IDs, not credentials. Custom Compose project names use `--project` and `--control-project`; set `CONTROL_OPENCODE_NETWORK` to the actual control network name when rendering the overlay. It never creates or deletes a network, changes an SSH worker, stops the sidecar, or publishes its port. Membership verification is followed by the application's own authenticated service-identity and readiness checks; network membership alone does not establish native service continuity.

Run `python3 tests/control_web_network_smoke.py` to exercise this transition with isolated disposable resources. It verifies legacy worker IP access and `opencode-control` DNS access from the same web container, the published loopback UI port, idempotence, web recreation followed by reconciliation, unchanged sidecar process identity, and rejection of missing labels/wrong identities without changing unrelated memberships. Tiny HTTP services stand in for the applications; this fixture does not claim real model inference or a live deployment. A later migration of all worker addresses to managed user-defined networks can replace this transitional helper with ordinary Compose network declarations.

## Restart and migration behavior

Host startup reloads existing mappings and verifies the same native instance. A web restart preserves the independently running native process. A changed sidecar incarnation records an explicit restart observation; session history is retained but interrupted computation is not claimed to have resumed. Ordinary uncertain instructions are reconciled without replay. For an exact, still-unapplied coordinator decision, all native tools were disabled: the service retires that proposal's routing authority with a receipt preserving **unknown native delivery**, then schedules a fresh decision against current evidence. This also handles backend startup converting a dispatching proposal to unknown. Owner-paused/stopped runs remain paused/stopped. The old proposal is never applied or resent.

Migration requires an owner-paused run, a configured and freshly observed idle workgroup control session, and drained source/target decision delivery. Worker tasks may continue independently. The atomic migration retains the run ID, instruction, rounds, assigned commands, worker receipts and old coordinator identity/history; it records the replaced coordinator and any superseded unapplied decision. Resume explicitly after checking that receipt. A missing or uncertain decision blocks migration. This transfers routing responsibility, not the old native conversation.

Control models remain decision-only. Future scoped evidence/action tools should call the same C# services with session/run authority; no Docker socket, raw database access or implicit permission approval is exposed. Provider limits and unavailable models continue to use the existing provider policy. Separate operations/adviser agents, native resource probes and further provider policy remain follow-on work.

## Validation and migration gates

Run `python3 tests/control_sidecar_smoke.py` on a Docker host. CI executes it in a separate job. It renders the shipped Compose configuration and exercises its image/settings with uniquely owned disposable Docker resources. The probe checks missing-secret failure, non-root execution, absent development/SSH tools, no published ports, unauthenticated denial, authenticated health/schema/session APIs and SSE connectivity, separate session histories, client replacement without process replacement, and container recreation retaining native IDs/history. Cleanup uses captured IDs and ownership labels.

The probe uses `noReply` message persistence: **no model inference or provider credentials**. This Docker fixture does not call a model. `ControlServiceTests` additionally exercise the production C# direct factory, actual loopback HTTP/SSE, persistent backend reconnect, lost-create discovery, explicit retry, migration, old-schema upgrade and restart recovery; the loopback native service is a protocol fixture, not an inference test. Architecture output records the actual tested platform; building a multi-platform manifest is not ARM64 runtime acceptance. Resource saturation, active inference through web redeploy, provider enrollment and live worker coordination require separate deployment receipts. Direct reads of cgroup files through the pinned native file API returned HTTP 500; the adapter deliberately reports no CPU/memory samples rather than fabricate utilization. Docker health/stats remain operator evidence until a supported native resource probe is added.

Only then migrate the legacy coordinator: stop new decisions, settle known/unknown commands without replay, record existing identities/history/credentials, create or explicitly transfer the canary binding, verify a real coordinator decision and worker receipt, restart the web alone, and prove continuous supervision. Retire only the old owned coordinator process/container after this succeeds. Preserve the rollback record and native history; never imply restoring a registration resurrects a stopped turn.
