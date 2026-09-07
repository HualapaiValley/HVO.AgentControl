# Dev Container provisioning

Owner requirements, 2026-09-07. The worker template below is executable now; automatic UI/coordinator provisioning and host scheduling are planned work. GitHub App credential brokerage is implemented; see [Worker GitHub access](GITHUB_ACCESS.md).

Tracking: [project/task sessions #42](https://github.com/RoySalisbury/HVO.AgentControl/issues/42), [multi-host provisioning #43](https://github.com/RoySalisbury/HVO.AgentControl/issues/43), [worker GitHub publication #44](https://github.com/RoySalisbury/HVO.AgentControl/issues/44).

## Environment contract

Use `devcontainer.json` and the official Dev Container CLI for builds, Features, lifecycle commands, startup and execution. Do not create a competing environment-definition format. Honor the repository's `.devcontainer/devcontainer.json` (or explicitly selected repository configuration), including its Features, lifecycle commands, container/remote user and workspace. Do not silently substitute a worker template because it is smaller. When no repository configuration exists, the planned default is a broad Codespaces-style tool image; the lightweight execution template is an explicit alternative. Record the selected configuration path, source revision, configuration digest, CLI version and resulting image/container identities.

AgentControl owns the durable provisioning request, host selection, capacity reservations, credentials, registration, task sessions and retirement. A successful `devcontainer up` does not prove OpenCode is connected or the task environment is ready. Expose requested/building/starting/connecting/verifying/ready/blocked/failed states and bounded build logs, including after a web restart. On uncertain completion, reconcile by request-specific labels before creating anything again.

The coordinator can request an environment and delegate preparation to an execution worker. It remains a routing model. Each subsequent task starts a fresh project-scoped session and verifies its environment as described in [Project workspaces](PROJECT_WORKSPACES.md). Repository clone/init may be a separate preparation task before invoking its devcontainer configuration.

## Baseline and build caching — owner clarification

The target fallback is the same kind of broad development environment as [Codespaces' universal image](https://github.com/devcontainers/images/blob/main/src/universal/README.md), with commonly used languages, Git/GitHub CLI, shells, build/debug utilities and the required AgentControl transport tools. Repository configuration still takes precedence; do not force `agent` when it declares `vscode` or another user. Keep project-specific SDK requirements such as `global.json` intact. This broad fallback and a managed image-refresh process are subsequent work after the current verification.

Use versioned, validated base images and cache them on each Docker host. A new worker normally reuses the image and build layers; source/Feature/base changes invalidate relevant layers. Cached layers do not update themselves when a newer package is released. Refresh images deliberately, run their checks, and select the new version for subsequent workers. Rebuilding/replacing an active worker remains a drained lifecycle operation, not a tool-update mechanism. See [Docker build cache](https://docs.docker.com/build/cache/).

Provisioning cards must identify the repository/source SHA, selected configuration path/digest, effective user, Features, CLI version and resulting image ID, so a JSON file merely existing in a checkout cannot be mistaken for the configuration that built the container. Editor customizations are separate from installed command-line tools.

The earlier **Devcontainer Test Worker** was created by CLI 0.89.0 with `--config .devcontainer/worker/devcontainer.json`: user `agent`, .NET/Git/gh/tmux/Python/Node/npm, and no Docker CLI. It did not use the repository default config, whose `vscode` user, pinned Git/gh and zsh Features are different. Its worker description now states that selection explicitly. It is intentionally preserved with its working GitHub App grant.

## Docker host inventory

Support multiple provisioner hosts, including but not limited to hvo-dev-03. A provisioner host owns a Docker engine and can create container runtimes; an execution runtime is where tasks run. These are separate registrations and capacity scopes. Record SSH/connection identity, architecture, available CPU/memory/disk, supported container features, image cache and configured limits. Reserve host resources before a build/start and account for build capacity as well as running containers.

Run the CLI on the selected host with the checkout on that host. Docker bind mounts resolve on the daemon host; a local path on hvo-dev-03 is not automatically present on another server. Never reuse a local Docker bridge IP as a cross-host endpoint. Registration needs a reachable host-published SSH endpoint or a supported tunnel/transport, with pinned identity and persisted endpoint mapping. Port forwarding in an editor is not service connectivity.

Select an eligible host based on task requirements and policy, with a visible reason when none qualifies. Linux containers on an ARM Mac do not provide native macOS/Xcode/iOS build capability; those tasks still need a native Mac runtime.

## Test worker template

`.devcontainer/worker/devcontainer.json` builds the `tools` stage of `docker/development/Dockerfile`. It includes the pinned .NET SDK, Git/GitHub CLI, SSH/tmux, Python/venv, Node/npm, C/C++ build tools, diagnostics and shellcheck. The agent has passwordless sudo inside its development container so authorized tasks can install missing packages. Home and SSH identity use per-container named volumes; the workspace is an independent checkout on the selected host. Two CPUs, four GiB and 512 PIDs are enforced.

The lightweight template runs restore on creation and starts SSH on startup. It does not import host credentials, publish SSH, or install OpenCode on its own; bootstrap/enrollment supplies a public login key and AgentControl's existing verified OpenCode installation. It does not mount the Docker socket. The existing `.devcontainer/devcontainer.json` remains the interactive VS Code configuration with host Docker integration; the explicit Docker integration-test alternative below runs its own nested daemon. Docker support is not inferred from having a Docker CLI.

Example on a Docker host (use an independent checkout, not a workspace with active work):

```bash
npm install --prefix .fixture/devcontainer-cli --no-audit --no-fund @devcontainers/cli@0.89.0
git clone --no-hardlinks . .fixture/devcontainer-test/project
.fixture/devcontainer-cli/node_modules/.bin/devcontainer up \
  --workspace-folder .fixture/devcontainer-test/project \
  --config .devcontainer/worker/devcontainer.json \
  --id-label hvo.agentcontrol.test=devcontainer-worker
.fixture/devcontainer-cli/node_modules/.bin/devcontainer exec \
  --workspace-folder .fixture/devcontainer-test/project \
  --config .devcontainer/worker/devcontainer.json \
  --id-label hvo.agentcontrol.test=devcontainer-worker \
  dotnet test HVO.AgentControl.slnx --configuration Release
```

The label above identifies this one test instance; use a unique immutable provisioning ID for every additional instance. Repeat `up` with the same identity to reconnect; do not use `--remove-existing-container` on active work. The checkout must be writable by the container user (UID 1000 in this template); configure ownership/UID mapping on other hosts. A local clone's origin points at the local source, so set the intended remote and supply scoped credentials before assigning GitHub work.

## Explicit Docker integration-test alternative

`.devcontainer/worker-docker/devcontainer.json` adds the official `ghcr.io/devcontainers/features/docker-in-docker:4.1.0` Feature to the lightweight worker tool image. It retains user `agent`, uses a separate nested Docker daemon and per-instance Docker/containerd volumes, and enforces two CPUs, six GiB and 1024 PIDs. It does not mount the host Docker socket. This is an explicit alternative, not the default repository configuration or the future universal tool baseline.

The [official Feature](https://github.com/devcontainers/features/tree/main/src/docker-in-docker) requires privileged mode. A separate daemon separates Docker-managed container/image state; it is not a VM security boundary. The host's provisioning policy must permit this specific capability. The repo's default configuration instead declares Docker-outside-of-Docker, which uses the host daemon.

Build and test in an independent checkout on the selected host:

```bash
devcontainer up --workspace-folder /absolute/path/to/checkout \
  --config .devcontainer/worker-docker/devcontainer.json \
  --id-label hvo.agentcontrol.provisioning=UNIQUE_REQUEST_ID
devcontainer exec --workspace-folder /absolute/path/to/checkout \
  --config .devcontainer/worker-docker/devcontainer.json \
  --id-label hvo.agentcontrol.provisioning=UNIQUE_REQUEST_ID \
  bash -lc './tests/Fixtures/start.sh && HVO_SSH_FIXTURES=1 HVO_GITHUB_CLI_TESTS=1 dotnet test HVO.AgentControl.slnx --configuration Release'
```

The fixtures in that command are created inside the nested daemon. SSH/OpenCode enrollment and scoped repository credentials remain separate provisioning phases. Named volumes and the host checkout survive container stop; no pruning or automatic deletion is implied.

## GitHub and tool access

Workers should read/update issues, create/update PRs, publish reviews and return repository-qualified artifact IDs directly. Prefer GitHub App installation credentials scoped to selected repositories and task permissions; installation tokens expire after one hour and require renewal for long tasks. GitHub CLI accepts `GH_TOKEN`. Provide tokens through a runtime credential mechanism, not prompts, images, source-controlled configuration or build arguments. Repository Git deploy keys alone cannot publish API comments or reviews. The implemented broker delivers renewable App credentials to dedicated runtime GitHub CLI configuration; see [Worker GitHub access](GITHUB_ACCESS.md).

Verify access and distinguish missing permission from transient failure. Publication needs durable intent and artifact reconciliation before retrying an uncertain write, so a restart cannot duplicate comments or PRs. Record the task/session actor even when GitHub sees a shared App identity. Merge/close authority follows project policy. Do not mark end-to-end worker publication verified until an actual issue → PR → separate review → correction exercise succeeds.

Missing tools can be installed by the worker within the environment's permissions, or requested through the coordinator when host-level work is needed. Record installed tools and refresh capability observations. Useful recurring dependencies should become reviewed configuration/image changes; an ad hoc package installation will not survive container replacement merely because the home volume persists. Installing a CLI does not grant its cloud/account access.

## Implementation sequence for automatic provisioning (#43)

Build this as a durable control-plane workflow, with the first real host being hvo-dev-03. Additional Docker hosts use the same transport and lifecycle; they are not separate implementations. The existing CLI-created test worker remains a compatibility fixture until this workflow can produce and enroll its own replacement.

1. **Host inventory and request records.** Add a separate Docker host registration with pinned SSH identity, encrypted login references, an absolute host workspace root, reachable SSH publish address, permitted port range, architecture and configured capacity. Execution runtimes and Docker hosts remain separate identities. Host verification checks Docker daemon access, the pinned Dev Container CLI, disk, architecture, workspace ownership and the controller's endpoint reachability. A request records its UUID, host ID/revision, repository and source SHA, selected configuration path/digest, template version, resource reservation and intended runtime/worker IDs. An identical request returns its existing record; conflicting reuse is rejected. Persist the card before starting any remote work.
2. **Host-side execution and reconciliation.** Run checkout preparation and the official CLI on the selected SSH host, with the bind source actually present on that Docker daemon's filesystem. Persist a unique request label and operation receipt before launching. Keep a bounded log and status artifact in an owned request directory. A backend restart reads the receipt and inspects exact ownership labels before starting another command. Zero matches after uncertain creation is a reconciliation state, not proof that creation never ran; multiple matches block enrollment. Never invoke remove-existing-container to recover an uncertain request.
3. **Bootstrap and verified enrollment.** Reserve a host-published SSH endpoint; validate that it reaches the exact resulting container. Obtain its host-key fingerprint through the authenticated provisioner connection, install a generated public login key, and store the private login key encrypted in AgentControl. Use the existing verified OpenCode bootstrap and runtime enrollment, then create the requested worker. Reuse the recorded runtime/worker IDs after restart. Ready requires dependency checks and an observed healthy native session, not merely a successful CLI exit. GitHub grants are explicitly selected and delivered after enrollment; host credentials must not enter builds or agent prompts.
4. **Owner UI and coordinator requests.** Show host suitability/capacity and a durable request card with stage, latest bounded progress, elapsed time, failure reason and a reconciliation-aware retry. Reserve build and runtime capacity transactionally. Add a validated coordinator action that creates the same request record; the model cannot choose arbitrary Docker flags, privileged mounts or bypass host policy. Projects/templates declare Docker integration as a capability rather than inheriting the host socket automatically.
5. **Drain and retain.** Mark the runtime draining so it accepts no new assignments. Require resolved deliveries, closed admin terminals and a recorded preservation check before stopping the exact owned container. Keep checkout/home/SSH volumes by default, record the retained artifacts and release active capacity only after stop is observed. Inaccessible hosts, unknown deliveries or ambiguous ownership block cleanup. Removing retained data is a separate explicit operation.

For each stage, inject a web restart immediately before and after the remote effect and assert stable request/container/runtime identities. Cover insufficient capacity, unsupported architecture, missing CLI/Docker, an unreachable published endpoint, failed lifecycle commands, competing retries and another request's container/volume labels. The final live acceptance is: owner requests worker → CLI builds/starts it → verified runtime/worker appears → managed GitHub access works → a task executes → drain/stop retains its work. Repeat on a second SSH Docker host before claiming multi-host validation.

## Retirement

Drain assignments and resolve pending/uncertain deliveries before stopping a managed runtime. Preserve task results, unpushed changes and retained workspaces according to policy. Check ownership labels and registration before removing only that provisioned container; retain volumes by default. Purging retained volumes and pruning shared images are separate operations. Browser closure, native idle or a missed heartbeat alone must never trigger deletion. The CLI creates/configures the environment; Docker stop/remove and AgentControl's drain/reconciliation logic complete its lifecycle.

For the current manually enrolled test, first finish its work and disconnect the runtime through AgentControl. Inspect the exact container ID returned by `up` and its `hvo.agentcontrol.test` label, then use `docker stop <container-id>`. A subsequent `up` with the same label can start it again; reconnect the runtime afterward. Do not remove its volumes or checkout to stop it. Automatic drain and retirement controls are not implemented by this template.

## References

- [Dev Container CLI](https://github.com/devcontainers/cli): reference implementation and headless up/exec commands.
- [Remote Docker hosts](https://code.visualstudio.com/remote/advancedcontainers/develop-remote-host): daemon-host workspace placement.
- [GitHub App installation authentication](https://docs.github.com/en/enterprise-cloud%40latest/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation): permissions and expiring tokens.
- [GitHub CLI environment](https://cli.github.com/manual/gh_help_environment): runtime authentication variables.

## Executed verification

See [configuration and cache verification](validation/devcontainer-configurations-2026-09-07.md) for actual default-config `vscode` tools, the nested-Docker full test run, repeat-up identity checks and a fresh cached instance.
