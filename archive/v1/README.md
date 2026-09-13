# HVO.AgentControl

For an introduction to the architecture and communication model, read the [project and communications brief](docs/PROJECT_AND_COMMUNICATIONS_BRIEF.md). It is written for another coordinating agent or team and includes the intended workflow, current limitations and questions for feedback.

AgentControl is a single-owner web application for persistent OpenCode coding sessions on SSH-accessible Linux/macOS runtimes. The .NET 10 Blazor UI controls a durable SQLite backend. SSH carries HTTP/SSE to a loopback-only OpenCode server owned by a dedicated tmux session. Remote work continues when the browser, tunnel, or backend disconnects.

The initial M0–M5 release supports runtime registration, verified bootstrap/install, separate workspace sessions, provider/model discovery, streamed transcripts, follow-up queues, native questions/permissions, cancellation, multiple workers, and restart reconciliation. Manual control works without a coordinator. Optional persistent OpenCode coordination now routes arbitrary prompts and responses between workers; see [worker management and coordination](docs/COORDINATION.md). See [implementation status](docs/IMPLEMENTATION_STATUS.md), [compatibility](docs/OPENCODE_COMPATIBILITY.md), and [operations](docs/OPERATIONS.md).

## Local setup

Install the SDK pinned in `global.json` (10.0.400). From the repository root:

```bash
./scripts/init-local.sh
dotnet tool restore
dotnet restore HVO.AgentControl.slnx
dotnet build HVO.AgentControl.slnx --no-restore --configuration Release --warnaserror
dotnet format HVO.AgentControl.slnx --no-restore --verify-no-changes
Control__DataDirectory="$PWD/data" \
Control__SecretsDirectory="$PWD/.secrets" \
Control__AllowInsecureLocalHttp=true \
dotnet run --project src/HVO.AgentControl --configuration Release --no-build \
  --no-launch-profile --urls http://127.0.0.1:5054
```

Open **http://127.0.0.1:5054**. Sign in using the contents of `.secrets/owner-password`. The initialization script generates missing random passwords without printing or replacing them. Read the password locally with `cat .secrets/owner-password`. Database migrations apply at startup. Persist the entire `data/` directory, including authentication keys. Do not run multiple replicas against it.

For HTTPS development, trust a development certificate (`dotnet dev-certs https --trust` where supported), omit `Control__AllowInsecureLocalHttp`, and use `--urls https://localhost:7160`. HTTP relaxation is for loopback development. Production TLS/proxy configuration is in the runbook.

The main menu separates daily work from setup:

- **Overview** (`/`): live fleet status, agents needing a response, and the selected agent's conversation, instructions and replies. Use **Agent conversation** to switch workers. Conversation links (`/?worker=<id>`) preserve the selected worker on reload and browser Back.
- **Runtimes** (`/runtimes`): add/verify/edit hosts, configure startup options, connect/refresh/disconnect, and inspect server diagnostics or stop an owned server.
- **Coordination** (`/coordination`): choose a dedicated coordinator conversation, select participants, send an instruction, and inspect routed prompts/results.
- **Workers** (`/workers`): create workers/worktrees, edit model defaults and descriptions, archive/restore idle workers, inspect workspace models, filter the inventory, and open conversations. A newly created worker opens on Overview automatically.

## Register a runtime

The target must accept SSH directly into the execution environment and have `tmux`, `lsof`, `curl`, a POSIX shell, and the appropriate archive/checksum utilities (`tar`/`sha256sum` on Linux; `unzip`/`shasum` on macOS). Git is required for repository inspection/worktree provisioning. No interactive sudo or package-manager installation is attempted.

1. Open **Runtimes → Add runtime**, then enter the host, SSH port, username and authentication method. Enter the password or paste a private key (and its passphrase if needed). Existing mounted credentials remain available under **Advanced connection settings**.
2. Select **Verify**. For a new host, AgentControl retrieves its fingerprint without sending credentials. Review the displayed host/key and choose **Trust this host and verify**. Compare through another trusted channel when available; for example, `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256` on the target. Future connections pin that exact key and never silently replace it.
3. Verify checks authentication, SFTP, platform, required tools, workspace access, bootstrap path, API port and OpenCode version/options. It requests a credential if missing or rejected. Blank workspace roots create `~/workspaces`; the bootstrap path defaults to a separate canonical `~/.local/state/hvo-agentcontrol/<runtime-id>` directory. You can select existing workspace roots and a different physically canonical bootstrap path. Resolve any failed checks and verify again.
4. Entered passwords/private keys/passphrases are encrypted under `data/credentials/` using the persisted Data Protection keys; runtime records contain opaque references. A separate random OpenCode server password is generated automatically. Select **Save and set up workers** to save/connect and open worker setup after native health checks pass. Editing fields invalidates verification; results expire after ten minutes. Mounted-secret/manual-save workflows remain supported under Advanced settings.
5. If OpenCode is missing and installation is enabled, AgentControl installs **1.18.29** into the bootstrap directory's `bin/`, verifying the release checksum. An existing different version is refused without upgrading it. OpenCode binds `127.0.0.1`; its port is forwarded only to backend loopback.
6. Inspect connectivity, version, provider discovery, and diagnostics. Provider sign-in remains a manual action as the runtime user: `opencode auth login` (use the installed absolute executable when it is outside PATH), then **Refresh**. The release may advertise anonymous free models; advertised availability does not guarantee inference access. If provider configuration must change, reconnect/refresh its directory context as described in the runbook.

SSH must terminate in the same network namespace as OpenCode. SSH to an outer Docker host followed by `docker exec` is not an implemented production route. Direct SSH into a container is supported. Do not expose the remote API publicly to work around routing.

**OpenCode startup options** provides checkboxes for `--pure` (disable external plugins) and `--print-logs`, plus a `--log-level` selector. These apply to the owned server when it starts. OpenCode 1.18.29 does **not** accept `--auto` on `serve`; that flag belongs to its interactive/run modes. Arbitrary shell command lines and listener overrides are not accepted. For an existing running server, stop its owned process before changing startup options, then edit/verify/reconnect; Disconnect alone preserves the old process, and a mismatch is reported instead of silently reusing it.

## First task and follow-up

1. Choose **Workers → New worker**, its runtime, a name/project, an existing remote workspace, and a discovered provider/model. **Set up worker** on a connected runtime also opens this form with that runtime selected. To create a worktree instead, also specify a clean source repository, a new branch, and a base ref. AgentControl never resets, cleans, merges, or pushes a repository.
2. Use a separate clone/worktree for each modifying worker. One worker holds each canonical workspace within a runtime. Configure global and runtime active-turn limits to suit the host.
3. Creation opens the worker's conversation on **Overview**. For an existing worker, use **Open conversation** on its inventory card or the **Agent conversation** selector on Overview. Enter a bounded task and its validation requirements, and select **Send instruction**. The command is saved before dispatch. While work is active, **Queue follow-up** records the next instruction in the same native conversation.
4. Review transcript/tool output and structured question/permission cards. Permissions have explicit **Allow once**, **Allow remembered scope**, and **Reject** controls. The application does not automatically approve them.
5. Inspect command delivery independently from assignment outcome. A finished native turn becomes **NeedsReview**, not verified success. Record an outcome with evidence after review.
6. Close/reopen the page or disconnect/reconnect the runtime to recover a snapshot. **Disconnect** closes control transport; **Stop owned server** deliberately stops the displayed workers. **Request cancellation** targets one worker and distinguishes receipt from observed idle.

An uncertain submission is **DeliveryUnknown** and blocks further prompts on that worker. Native message identity may resolve it. Otherwise inspect history/files, then explicitly acknowledge the uncertainty. That action never resends the old prompt. The UI does not offer unverified active-turn steering; use queueing or cancellation followed by a queued correction.

## Docker

Generate local secrets first, then build and start one container:

```bash
./scripts/init-local.sh
CONTROL_UID="$(id -u)" CONTROL_GID="$(id -g)" docker compose up --build -d
docker compose logs --tail 50 agentcontrol
curl --fail http://127.0.0.1:8080/health/ready
```

Open **http://127.0.0.1:8080** on the Docker host. Compose binds only host loopback, persists `/data` in `agentcontrol-data`, and mounts `.secrets` read-only. Build UID/GID must match ownership of mounted secrets. If the Docker daemon is remote, bind-mount paths and loopback ports are on that host: use a host-side secrets directory or a named secret volume, and an SSH tunnel for browser access. The application's SSH key mounts are independent of Docker contexts.

The provided Docker image runs without root privileges. Persist native OpenCode data independently on each runtime; the control-plane volume is not a backup of remote repositories/conversations.

The original repository's [Dev Container and Docker context instructions](docs/DEVCONTAINER.md) are preserved separately.

## Validation

During prerelease development, ready-for-review PRs run one short `build` check: restore, Release compilation with warnings as errors, formatting and quick script regressions. Drafts skip automatic builds; marking them ready starts the check. Merges do not trigger another run. Workers still validate code changes and obtain independent review. Full .NET/SSH tests, browser checks, sidecar checks and official Dev Container acceptance are available through the manual **Full validation** workflow. Record exact-source evidence for relevant checks before deployment; see [validation policy](docs/VALIDATION_POLICY.md).

```bash
dotnet test HVO.AgentControl.slnx --configuration Release --no-restore
./tests/Fixtures/start.sh
HVO_SSH_FIXTURES=1 dotnet test HVO.AgentControl.slnx --configuration Release --no-restore
# Downloads/runs real OpenCode and invokes the advertised anonymous provider:
HVO_NATIVE_FIXTURE=1 dotnet test HVO.AgentControl.slnx --configuration Release --no-restore
```

The SSH fixture uses a deterministic HTTP/SSE implementation; its simulated output is clearly identified. Native tests use the real binary/provider on disposable SSH containers. Optional test attributes report skipped checks unless the relevant environment variable is enabled. Fixture IPs must be reachable from the test process; when Docker is remote, run tests on its network or provide an explicit verified route.

For browser smoke, install Node 22+, then:

```bash
npm ci --prefix tests/Browser
npx --prefix tests/Browser playwright install --with-deps chromium
# In another terminal, start the fixture-backed application:
Control__DataDirectory="$PWD/.fixture/browser-data" \
Control__SecretsDirectory="$PWD/.fixture/secrets" \
Control__AllowInsecureLocalHttp=true \
dotnet run --project src/HVO.AgentControl --configuration Release --no-build \
  --no-launch-profile --urls http://127.0.0.1:5054
# From the repository root:
node tests/Browser/smoke.cjs
# After native installation test; invokes a real provider and grants only test-workspace permissions:
node tests/Browser/native-smoke.cjs
```

Screenshots go to `artifacts/browser/`. Test credentials/data stay in ignored `.fixture/`. The tests explicitly create disposable repositories and owned tmux sessions. To remove only these disposable SSH targets after inspection:

```bash
docker rm -f hvo-agentcontrol-fixture-a hvo-agentcontrol-fixture-b
```

See [validation evidence](docs/IMPLEMENTATION_REPORT.md) for checks actually run and platform limitations. No commits, pushes, publishing, or production deployment are part of the setup/test workflow.
