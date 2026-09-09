# Outbound Linux host executor

`HVO.AgentControl.HostExecutor` is a distinct outbound Linux process. The web application does not register `LocalDevContainerRunner`, receive a Docker path/socket, or execute host commands. The executor reads an owner-installed authority manifest and bearer file, authenticates as one enrolled host executor, and uses the pinned official Dev Container CLI through `LocalDevContainerRunner`.

The manifest is an absolute, non-symlink path that is not writable by group/other and is not readable by other users. Its `authorityDigest` is the SHA-256 of the serialized `ProvisionerHostAuthority`. The separate credential file has the same protection and contains the raw `<enrollment-id>.<secret>` bearer. Neither file belongs in source control or an owner HTTP request. The manifest explicitly binds the controller URI, enrollment/generation, boot and process incarnation, endpoint/physical-host/engine/builder digests, approved canonical checkout/configuration, pinned tools, Docker socket/engine, operation/workspace, resource reservation, and protected command paths.

The initial canary is two phase because host authority must exist before the owner can acquire and bind the physical permit:

```bash
dotnet HVO.AgentControl.HostExecutor.dll authorize /etc/hvo-agentcontrol/executor-operation.json
# Owner acquires one combined build/runtime reservation and binds it to the operation.
dotnet HVO.AgentControl.HostExecutor.dll run /etc/hvo-agentcontrol/executor-operation.json
```

`authorize` activates the pending enrollment and submits the immutable intent derived by the same runner that will execute it. `run` claims the exact selected enrollment/incarnation, collects fresh Linux/filesystem/Docker evidence, and invokes the official CLI. Immediately before `up`, one controller transaction revalidates and consumes the physical reservation and appends the complete `ProvisionEffect`. A successful response with `authorizedNow: true` is the only execution permit. A lost response remains uncertain; replay returns false and never repeats `up`.

Progress uses deterministic report IDs and monotonic sequences. Exact replay is idempotent; changed IDs/sequences conflict. Results are bound to the claim and immutable intent. `VerifiedEnvironment` requires the committed effect, exact ownership labels, running container/image identities, approved bind root, effective user/workspace/tool evidence, and Docker-inspected runtime CPU/memory limits. It ends at `AwaitingEnrollment`. Later failure/unknown reports, cancellation, or reconciliation cannot downgrade that terminal provisioning success or erase its observed identities.

This slice does not make a worker ready. It does not prepare a fresh checkout, hard-limit the Docker builder, allocate/verify SSH transport, bootstrap OpenCode, establish provider/GitHub readiness, create stable runtime/slot records, execute a task, restart/reconnect, drain, retire, release capacity after observed absence, or replace a beta worker. Those remain required gates in `WORKER_ROLLOUT.md`. Do not use fake SSH endpoints or preseeded inventory to represent them as complete.
