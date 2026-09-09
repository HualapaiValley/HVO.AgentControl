# Operations and recovery

AgentControl is one owner, one backend replica, one SQLite database. `/health/live` checks the web process. `/health/ready` checks database connectivity; neither claims a remote runtime/provider is healthy. Authenticated snapshots show SSH/API/SSE freshness, reconnect attempts, queued commands, unknown deliveries and pending attention.

Use the main menu's **Runtimes** page for host verification, startup settings and runtime lifecycle/diagnostics; use **Workers** for workspace creation, model inspection and inventory filters. **Overview** holds live status and agent conversations, including permission/question responses, delivery and cancellation. Conversation URLs identify an existing worker; navigating or closing these pages does not control the remote process lifetime.

## Configuration and secrets

Configuration uses ASP.NET Core conventions. `Control__DataDirectory` and `Control__SecretsDirectory` are absolute paths in the deployment examples; relative paths resolve against the application's working directory. `Control__OwnerPasswordFile` defaults to `owner-password`.

| Setting | Default | Behavior |
| --- | --- | --- |
| `GlobalCapacity` | 8 | Maximum occupied worker turns across runtimes |
| Runtime `Capacity` | 2 | Maximum occupied turns on that runtime |
| `QueueLimit` | 32 | Maximum undelivered prompts per worker |
| `MaxPromptCharacters` | 64000 | Maximum prompt size |
| `PollMilliseconds` | 750 | Snapshot/reconciliation cadence; SSE drives observation and browser notifications |
| `HistoryLimit` | 200 | Latest native messages per reconciliation; retain at most twice this many local snapshots per worker |
| `EventRetention` | 10000 | Most recent central journal events; old sequence gaps require snapshots |
| `MaxCommandRecords` | 10000 | Hard audit cap. Requests fail visibly at the cap; old request IDs are never silently forgotten/replayed |
| `MaxRuntimes` / `MaxWorkers` | 32 / 128 | Registration/claim limits |
| `AllowInsecureLocalHttp` | false | Local loopback HTTP exception; normal operation requires HTTPS |
| `TrustedProxies` | empty | Explicit proxy IP list for forwarded scheme/address headers |

The database stores secret **references**, not credentials. Mounted-file references are restricted names under the mounted secrets directory; symlink references are refused. Guided Verify also accepts entered SSH passwords/private keys/passphrases. After successful checks, it encrypts them in `/data/credentials/vault-<uuid>` using ASP.NET Core Data Protection, with a separate protection purpose bound to each file reference. Files are mode 600, their directory mode 700, and plaintext is never returned by verification/snapshot APIs or stored in commands. Password whitespace is preserved. The separate OpenCode server password can be generated and protected automatically. Server and owner passwords require at least 24 characters.

Persist **the entire `/data` directory**, including `credentials/` and `keys/`. Decryption after restart requires the original keys; encryption does not protect against someone who obtains both ciphertext and keys. Protect the volume and backups accordingly. A lost key requires restoring the backup or entering the SSH credential again. Mounted owner login secrets remain supported and separate. Cancelled verification drafts or replaced credentials may leave unreferenced encrypted files; keep these with backups and remove only after confirming no saved runtime references them.

Verify discovers a new SSH fingerprint during key exchange and aborts before authentication. The owner explicitly trusts the displayed fingerprint before credentials are sent. The ten-minute trust token is bound to host, port, user and draft identity; the final verification token is bound to the complete profile. A changed saved key is rejected. Independent verification is still the stronger enrollment method and remains available through the advanced fingerprint field. Verification is bounded to four concurrent connections and sixty seconds per attempt. It may create the default workspace root and protected bootstrap directory; it does not install packages with sudo or launch a server until Save and set up workers.

The OpenCode server password is transferred with SFTP to mode-600 `server.env` in the mode-700 remote state directory. It is never a command-line argument, URL, profile response, or ordinary log entry. Scripts are transferred through atomic SFTP rename to avoid partial concurrent reads. The model provider's own auth/config stays remote. Transcripts/tool output may contain sensitive repository information; grant access to the owner only and protect the database/backups accordingly.

To rotate SSH credentials, disconnect, replace the mounted file (preserving permissions), update its reference if needed, and reconnect. To change the OpenCode server password, stop the owned server while connected, then replace the launch credential and reconnect; a running process retains its launch environment. Changing a mounted owner password changes subsequent sign-ins; existing cookies expire after 12 hours. To revoke all existing owner sessions immediately, stop the backend, archive/remove its data-protection key directory, and restart; existing browser circuits close on that restart. Do not delete the SQLite database for credential rotation.

## HTTPS and reverse proxies

The example Compose endpoint is bound to Docker-host loopback for local use. Do not publish an HTTP owner session directly onto a LAN/Internet interface. For a TLS-terminating proxy, keep its backend network private and configure:

```text
Control__AllowInsecureLocalHttp=false
Control__TrustedProxies__0=<actual proxy IP>
ASPNETCORE_HTTPS_PORT=443
```

The proxy must forward `Host`, `X-Forwarded-Proto: https`, and WebSocket upgrades for `/_blazor` and `/hubs/activity`. Only explicitly trusted proxy addresses are accepted. Use an appropriate application `AllowedHosts` setting. Direct Kestrel HTTPS is also supported using ASP.NET Core certificate configuration.

## SSH identity and bootstrap

Default host-key algorithm is `ssh-ed25519`. ECDSA P-256 and RSA SHA-512 can be explicitly selected when appropriate. The fingerprint must match the selected key; verify it out of band. Unknown/changed keys fail closed. To enroll a legitimate rotation, select Disconnect, confirm its provenance outside AgentControl, edit the algorithm/fingerprint, save, and Connect. No trust-on-first-use or `StrictHostKeyChecking=no` equivalent exists.

Bootstrap state lives outside repositories. Use the physically canonical absolute path reported by `pwd -P`; symbolic-link state directories or ancestors are refused. Allowed roots must exist, and state must remain outside their physically resolved paths before secrets are uploaded. A runtime records an independent managed-server ID, port and tmux namespace (`hvo-<managedServerId>`, session `managed`). It verifies metadata, its tmux owner marker and listening process PID; an unrelated socket/session/listener is not killed or adopted. A port collision is a visible failure; choose another configured port before workers exist. A registration with workers or uncertain/queued operations cannot be redirected to a different SSH identity/state directory/port.

Prerequisites must be installed by the owner/provisioning system. AgentControl will not wait on sudo. Installation selects the pinned official archive and verifies its SHA256 checksum. A manually installed version is discovered through the configured absolute executable, managed bin directory, documented user paths, Homebrew/system paths and noninteractive PATH. Existing different releases fail compatibility without upgrade.

If `BOOTSTRAP_LOCKED` persists after the bounded wait, inspect the target for an active bootstrap first. Only after confirming none is running, remove the empty `bootstrap.lock` directory inside this runtime's state directory. Never remove someone else's state or lock.

If the pane exited, AgentControl only restarts that owned pane/process. The `restarts` file limits consecutive failed launches to three; a verified healthy API resets the budget. To reset after correcting a problem that exhausted the budget, inspect diagnostics and use the actual absolute state path on the target:

```bash
printf '0' > /absolute/owned/state/restarts
```

Startup options are persisted with the runtime and checked against a fingerprint in the remote `startup-options` file. `STARTUP_OPTIONS_CHANGED_STOP_REQUIRED` means the living owned process uses different settings: deliberately stop that owned server, then reconnect. No automatic restart is issued to apply an option change. Pre-options installations without the marker are treated as using defaults. Supported options for 1.18.29 are pure mode, diagnostic logging and log level; the listener remains authenticated and loopback-only. `--auto` is not a supported `serve` option, and no permission bypass is implied by these settings.

A living process without the expected listener is reported unhealthy; it is not automatically killed. Inspect `server.log`, `previous.log` (bounded to the previous 64 KiB), native OpenCode logs, `tmux -L hvo-MANAGED_ID list-panes -a`, and `lsof -nP -iTCP:PORT -sTCP:LISTEN`. Never use a global `pkill opencode` or `tmux kill-server` as a recovery shortcut. Native OpenCode controls its own logs; monitor remote disk usage/rotation separately.

For a connected SSH runtime whose retained owned-process observation has aged out, `POST /api/v1/runtimes/{id}/native-process/reinspect` accepts a UUID, expected runtime revision, managed-server ID, and expected incarnation. It performs only the live owner/tmux/PID/incarnation probe; it does not reconnect, bootstrap, stop, change desired connection state, or alter worker assignments. The request is bound to the retained PID as well as the submitted incarnation. A changed revision, managed-server identity, PID/incarnation, unsupported probe, timeout, or restart records no new stop authority; retrying the same UUID returns the retained receipt.

## Workspace and provider setup

Select an existing absolute remote directory under an allowed root. SSH resolves roots and workspaces physically before creating a native session, then `/path` and native session metadata must agree. Quotes, embedded newlines and shell metacharacters stay arguments; prompts are JSON HTTP bodies. One modifying worker holds each workspace. Durable claims also prevent concurrent creations against the same exact SSH endpoint/user and canonical path across registrations. Aliases or separately routed identities cannot prove physical equivalence; operators should represent one actual environment consistently.

For a new worktree, provide a clean source repository, a new branch, target directory and base ref. The target must not already exist. Dirty source, existing branch/path, outside-root or invalid ref failures leave existing work intact. AgentControl does not clean/reset/remove worktrees or merge/push branches. A failed provisioning attempt can leave an explicitly created worktree; inspect it and select it as an existing workspace rather than forcing a second creation.

Use **Inspect workspace models** before creation if provider configuration is repository-specific. Default runtime discovery uses its first allowed root. The dispatcher independently verifies selected model availability in the actual workspace. No connected models means manual provider setup is needed. As the remote user, run the selected executable's `auth login`, then refresh. Changes cached by the running OpenCode instance may require a deliberate owned-server stop/reconnect after affected workers are idle. Do not download remote provider authentication files into AgentControl.

## What survives

| Interruption | Behavior |
| --- | --- |
| Browser closes or second device connects | Backend monitoring/queue continues; UI rebuilds from durable snapshots; no new worker is created |
| SSE subscription drops | Reopen the stream and reconcile messages/requests/status; SSH and the owned process stay intact; display a gap |
| SSH drops / Disconnect selected | Dispose local forwarding only; tmux/OpenCode continue; pending commands remain queued until reconciliation |
| Backend stops/restarts | SQLite registrations, requests, commands and claims survive; in-flight mutations become `DeliveryUnknown`; reconnect before dispatch |
| OpenCode stops/restarts | Native conversations may survive if its data remains; active tools and legacy pending request maps are not promised to resume |
| Host reboot/container removal | tmux does not survive reboot; rebootstrap only when reachable; native history requires persistent native data |
| Database unavailable | Reject new submissions instead of acknowledging unsaved work; no further remote dispatch until state is usable |
| Provider unavailable | Keep manual monitoring and unrelated runtimes usable; inspect native error/retry evidence and provider setup |

Native OpenCode data uses XDG paths, normally `~/.local/share/opencode/opencode.db`, with config under `~/.config/opencode`, state under `~/.local/state/opencode` and logs beneath its data directory. Preserve the runtime user's data/config/state plus workspaces and the AgentControl bootstrap directory when containerizing a worker. Per-user processes/worktrees are not security sandboxes.

See [retention and evaluation evidence](RETENTION_AND_EVALUATION.md) for archive/restore requirements, training-example coverage, proposed retention defaults and native context limits. The [September 8 audit](validation/retention-audit-2026-09-08.md) records measured storage and verified backup compression. Automatic retention is still pending; context compaction does not remove durable native history.

## Delivery and outcome recovery

Each application request has a UUID and immutable routing/content. Retrying that UUID returns the same command; changing its contents with the same UUID is rejected. A single transactional claim precedes dispatch. Never wrap native mutations in a generic HTTP retry handler.

`AcceptedByRuntime` means receipt. `Running` requires native evidence. `Finished` means an associated native turn ended, not that its objective succeeded. Assignment outcomes remain separate; the owner records evidence for reported/verified completion, failure or blocking.

Coordinator native errors are classified before decision JSON repair. A run can show `Waiting` with **Coordinator held after native …** while its heartbeat and independent workers continue. See [coordinator native failure recovery](COORDINATOR_NATIVE_FAILURES.md) for the durable hold, eligible recovery, and remaining fallback/compaction limitations.

### External fleet watchdog

`scripts/agentcontrol-watchdog.py` supplies a small operational monitor outside the controller process. It authenticates using the owner password file, refreshes an expired login on the next poll, and reads the owner-only `GET /api/v1/watchdog` endpoint with ten-second request timeouts and an 8 MiB response limit. This endpoint projects status and recovery counters in the database, excluding transcripts, prompts, catalogs and credential bodies. It includes all open commands up to an explicit 4,096-command bound, including commands older than the full snapshot's latest 500. Other collections are bounded at 512 records. Overflow returns `complete: false`; it cannot masquerade as a healthy partial observation. Run it against the controller's local interface or an authenticated HTTPS origin. The password stays out of arguments, logs, and the incident file.

The monitor rejects incomplete, malformed or stale responses and never falls back to downloading retained conversations. Its private ledger exposes `observationStatus` (`Healthy`, `Unavailable`, or `Stale`), `lastControllerSuccess`, and `consecutiveObservationFailures`. An outage immediately records `controller_observation_failed`; after the configured stall interval, `controller_observation_stale` is a critical journal event, repeated every five minutes. Previously observed incidents remain unresolved until fresh evidence arrives. Successful observation records recovery and clears the outage. `--once` exits nonzero if observation fails, for external health checks. These events are local journald signals; they do not send email or external notifications. GitHub credential blockage or expiry is a separate incident even during a scheduling pause.

```bash
python3 scripts/agentcontrol-watchdog.py \
  --url http://127.0.0.1:5054 \
  --password-file /srv/agentcontrol/secrets/owner-password \
  --state-file /srv/agentcontrol/watchdog/state.json \
  --once
```

Omit `--once` to poll every 30 seconds. The monitor classifies current coordinator and recent worker provider errors, repeated format correction or decision recovery, unresolved commands, pending approvals/questions, unavailable connected runtimes, and active runs with available idle workers but no assignments. Missing assignments, pending requests, and runtime outages must persist five minutes before the first alert. Pending approvals/questions suppress the missing-assignment alert but remain observable themselves. An intentional pause, completed run, or stopped run does not produce a missing-assignment alert; uncertain commands remain observable. Slow inference produces an incident when appropriate and does not authorize a process restart.

The private, atomically replaced state file retains up to 256 current incidents and 256 journal entries. Repeated incidents are reported at most once per five minutes and record their resolution. Subjects are SHA-256 identifier prefixes (16 hex characters); compare those hashes to the live run/command/container IDs when investigating. Error reports retain only an allowlisted native error name, numeric HTTP status, and fixed category. Raw messages, prompts, model responses, headers, Docker stderr, and passwords are excluded. This file is an operational index; the AgentControl database remains the authoritative assignment and delivery ledger.

For optional stopped-container recovery, add `--restart-exited`, repeat `--container exact-name` for each owned container, and provide `--owner-label key=value`. A Compose project's `com.docker.compose.project` label is suitable if it uniquely identifies this deployment; repeat the label option when controller and workers belong to separate owned Compose projects. At least one supplied ownership label must match, in addition to the exact container name. `--docker-context name` selects the Docker host without changing the default context. Container names are explicit; no container is discovered and enrolled for recovery automatically.

Only a matching name and ownership label with Docker state `exited` qualifies. Three consecutive observations are required; start attempts have a ten-minute cooldown and a maximum of three per hour, retained across watchdog restarts. The watchdog starts the observed container ID, preserving its volumes; it never recreates containers, restarts running containers, or acts on a connection wobble. A recorded intentional fleet pause suppresses container starts, including while the controller is unreachable. Startup without an observation, no active coordination, and blindness exceeding the stall interval also suppress starts. A recent running observation permits bounded recovery of a controller that just exited. It never grants approvals, edits model settings, acknowledges unknown delivery, or replays tasks. Provider routing and session recovery still belong to the control plane and require their recorded evidence.

Run the foreground process under a service manager, with one service per state file. An exclusive lock rejects duplicate instances. Example unit settings (replace paths and add the intended container allowlist):

```ini
[Service]
Type=simple
User=agentcontrol
ExecStart=/usr/bin/python3 /srv/agentcontrol/scripts/agentcontrol-watchdog.py --url http://127.0.0.1:5054 --password-file /srv/agentcontrol/secrets/owner-password --state-file /srv/agentcontrol/watchdog/state.json
Restart=on-failure
RestartSec=30
UMask=0077
StandardOutput=journal
StandardError=journal
LogRateLimitIntervalSec=30s
LogRateLimitBurst=32
```

Use the host's bounded journald retention (for example, a chosen `SystemMaxUse` and `MaxRetentionSec`) instead of redirecting stdout to an unlimited file. Corrupt persisted watchdog state fails startup; inspect and restore it rather than silently clearing restart budgets. Validate this script with `python3 -m unittest discover -s tests -p 'test_agentcontrol_watchdog.py' -v`.

Keep the installed script and state outside temporary directories. For a user systemd service, omit `User=`, enable the unit, and enable lingering for its owner if it must start at boot without an SSH login. See the [coordinator recovery playbook](operations/COORDINATOR_RECOVERY_PLAYBOOK.md) for failure classification and the end-to-end recovery evidence required before declaring the fleet healthy.

For `DeliveryUnknown`, inspect the corresponding native message ID, transcript and files. Native identity can establish acceptance; prompt text similarity cannot. Unknown work blocks further prompts on that worker until evidence resolves it or the owner acknowledges uncertainty. Acknowledgement marks that command withdrawn without retry. Any later instruction is a new explicit request. Uncertain session creation is reconciled only by the exact application creation marker and workspace; otherwise its workspace claim remains held until explicit resolution.

An abort receipt is **cancellation requested**. AgentControl waits for observed idle; a remote child process may have effects beyond that receipt. Other workers remain independent. Permissions are answered only by native request identity. A retried reply never grants a different request/scope; after native restart a formerly pending request may become `NoLongerPending` rather than falsely “answered.”

If OpenCode is idle after an interrupted turn but has no final assistant response, a known accepted command can remain open. Inspect its history, request cancellation to explicitly close that turn, and wait for observed idle before sending a new continuation. Uncertain delivery still requires its separate acknowledgement. Per-worker serialization covers AgentControl commands; avoid driving the same managed session concurrently through another native client that bypasses its queue.

## Backup and restore

Stop the control plane for the simplest consistent backup. A stopped backup must include the entire data directory (database, any `-wal`/`-shm`, authentication keys and migrations metadata) and separately protected mounted secrets. For the default local layout:

```bash
# Stop the local dotnet process, then:
umask 077
tar -czf agentcontrol-data-backup.tgz data/
```

For Compose, stop the service and archive the named data volume with your volume backup tooling. Never copy only a live SQLite main file while ignoring its WAL. A supported SQLite backup operation is an alternative if you need online backups. Restore data and secret mounts with original UID/GID/permissions, start exactly one replica, and inspect reconciliation/unknown deliveries before issuing new work. Restoring an old database cannot prove what happened remotely since that backup; disconnect desired runtimes in an offline maintenance copy or isolate network access for the first recovery inspection.

Commands/assignment audit have a deliberate hard cap instead of expiring idempotency records. At the cap, back up and increase the bounded configured limit deliberately or archive the deployment and start a separately identified control plane. Do not delete old request rows in place and then replay clients that may reuse those IDs. Journal and local message retention are bounded; older native history remains on the target.

To retire a runtime, cancel undelivered work, resolve uncertainties, and delete its worker/coordinator registrations while they are freshly observed idle and outside unfinished coordination. If retiring the remote process too, use **Stop owned server** while connected. Disconnect, close admin terminals, then use **Delete runtime**. The UI rejects references (including archived workers), unresolved commands and claims. Registration deletion retains command audit, credentials, remote files and native history; it does not remove worktrees. Destroy disposable test containers only by their explicit fixture names.
