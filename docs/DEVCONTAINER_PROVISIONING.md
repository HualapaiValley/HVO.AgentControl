# Dev Container provisioning

Owner requirements, 2026-09-07. The worker template below is executable now; automatic UI/coordinator provisioning, host scheduling, and credential brokerage are planned work.

Tracking: [project/task sessions #42](https://github.com/RoySalisbury/HVO.AgentControl/issues/42), [multi-host provisioning #43](https://github.com/RoySalisbury/HVO.AgentControl/issues/43), [worker GitHub publication #44](https://github.com/RoySalisbury/HVO.AgentControl/issues/44).

## Environment contract

Use `devcontainer.json` and the official Dev Container CLI for builds, Features, lifecycle commands, startup and execution. Do not create a competing environment-definition format. Projects can supply their own configuration; an explicitly selected AgentControl template supplies a default when needed. Record the selected configuration path, source revision, configuration digest, CLI version and resulting image/container identities.

AgentControl owns the durable provisioning request, host selection, capacity reservations, credentials, registration, task sessions and retirement. A successful `devcontainer up` does not prove OpenCode is connected or the task environment is ready. Expose requested/building/starting/connecting/verifying/ready/blocked/failed states and bounded build logs, including after a web restart. On uncertain completion, reconcile by request-specific labels before creating anything again.

The coordinator can request an environment and delegate preparation to an execution worker. It remains a routing model. Each subsequent task starts a fresh project-scoped session and verifies its environment as described in [Project workspaces](PROJECT_WORKSPACES.md). Repository clone/init may be a separate preparation task before invoking its devcontainer configuration.

## Docker host inventory

Support multiple provisioner hosts, including but not limited to hvo-dev-03. A provisioner host owns a Docker engine and can create container runtimes; an execution runtime is where tasks run. These are separate registrations and capacity scopes. Record SSH/connection identity, architecture, available CPU/memory/disk, supported container features, image cache and configured limits. Reserve host resources before a build/start and account for build capacity as well as running containers.

Run the CLI on the selected host with the checkout on that host. Docker bind mounts resolve on the daemon host; a local path on hvo-dev-03 is not automatically present on another server. Never reuse a local Docker bridge IP as a cross-host endpoint. Registration needs a reachable host-published SSH endpoint or a supported tunnel/transport, with pinned identity and persisted endpoint mapping. Port forwarding in an editor is not service connectivity.

Select an eligible host based on task requirements and policy, with a visible reason when none qualifies. Linux containers on an ARM Mac do not provide native macOS/Xcode/iOS build capability; those tasks still need a native Mac runtime.

## Test worker template

`.devcontainer/worker/devcontainer.json` builds the `tools` stage of `docker/development/Dockerfile`. It includes the pinned .NET SDK, Git/GitHub CLI, SSH/tmux, Python/venv, Node/npm, C/C++ build tools, diagnostics and shellcheck. The agent has passwordless sudo inside its development container so authorized tasks can install missing packages. Home and SSH identity use per-container named volumes; the workspace is an independent checkout on the selected host. Two CPUs, four GiB and 512 PIDs are enforced.

This template runs restore on creation and starts SSH on startup. It does not import host credentials, publish SSH, or install OpenCode on its own; bootstrap/enrollment supplies a public login key and AgentControl's existing verified OpenCode installation. It does not mount the Docker socket. The existing `.devcontainer/devcontainer.json` remains the interactive VS Code configuration with host Docker integration; Docker integration fixtures for execution workers currently run on the host/CI. A fuller isolated Docker test template is a separate capability, not inferred from having a Docker CLI.

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

## GitHub and tool access

Workers should read/update issues, create/update PRs, publish reviews and return repository-qualified artifact IDs directly. Prefer GitHub App installation credentials scoped to selected repositories and task permissions; installation tokens expire after one hour and require renewal for long tasks. GitHub CLI accepts `GH_TOKEN`. Provide tokens through a runtime credential mechanism, not prompts, images, source-controlled configuration or build arguments. Repository Git deploy keys alone cannot publish API comments or reviews. A repository-scoped fine-grained token can bridge beta testing while the broker is built.

Verify access and distinguish missing permission from transient failure. Publication needs durable intent and artifact reconciliation before retrying an uncertain write, so a restart cannot duplicate comments or PRs. Record the task/session actor even when GitHub sees a shared App identity. Merge/close authority follows project policy. Do not mark end-to-end worker publication verified until an actual issue → PR → separate review → correction exercise succeeds.

Missing tools can be installed by the worker within the environment's permissions, or requested through the coordinator when host-level work is needed. Record installed tools and refresh capability observations. Useful recurring dependencies should become reviewed configuration/image changes; an ad hoc package installation will not survive container replacement merely because the home volume persists. Installing a CLI does not grant its cloud/account access.

## Retirement

Drain assignments and resolve pending/uncertain deliveries before stopping a managed runtime. Preserve task results, unpushed changes and retained workspaces according to policy. Check ownership labels and registration before removing only that provisioned container; retain volumes by default. Purging retained volumes and pruning shared images are separate operations. Browser closure, native idle or a missed heartbeat alone must never trigger deletion. The CLI creates/configures the environment; Docker stop/remove and AgentControl's drain/reconciliation logic complete its lifecycle.

For the current manually enrolled test, first finish its work and disconnect the runtime through AgentControl. Inspect the exact container ID returned by `up` and its `hvo.agentcontrol.test` label, then use `docker stop <container-id>`. A subsequent `up` with the same label can start it again; reconnect the runtime afterward. Do not remove its volumes or checkout to stop it. Automatic drain and retirement controls are not implemented by this template.

## References

- [Dev Container CLI](https://github.com/devcontainers/cli): reference implementation and headless up/exec commands.
- [Remote Docker hosts](https://code.visualstudio.com/remote/advancedcontainers/develop-remote-host): daemon-host workspace placement.
- [GitHub App installation authentication](https://docs.github.com/en/enterprise-cloud%40latest/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation): permissions and expiring tokens.
- [GitHub CLI environment](https://cli.github.com/manual/gh_help_environment): runtime authentication variables.
