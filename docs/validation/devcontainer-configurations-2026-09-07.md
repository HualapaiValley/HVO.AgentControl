# Dev Container configuration and cache verification — 2026-09-07

All builds used the official Dev Container CLI **0.89.0** installed at `.fixture/devcontainer-cli/node_modules/.bin/devcontainer`. JSON files were not merely copied into pre-existing Docker containers. Independent test checkouts came from local integration candidate `3a7de235c1247c39917eace64036ced51ba0269c`, containing the final coordination stress/status changes. The earlier registered App-enabled test worker remains unchanged.

## Configurations actually selected

| Configuration | User / workspace | Docker | Verified tools and checks |
| --- | --- | --- | --- |
| `.devcontainer/worker/devcontainer.json` | `agent`, `/home/agent/workspaces/project` | No CLI/daemon | Earlier registered worker: .NET, Git, gh, tmux, Python, Node/npm; actual App publication. This is the lightweight alternative, not the repo default. |
| `.devcontainer/worker-docker/devcontainer.json` | `agent`, `/home/agent/workspaces/project` | Dedicated nested daemon via official Docker-in-Docker Feature 4.1.0 | Two CPUs, 6 GiB, 1024 PIDs. Restore/build/format and **100 passed, 2 optional live-provider tests skipped**, including real SSH fixtures created inside the nested daemon. Docker client/server 29.7.2-2. |
| Repository default `.devcontainer/devcontainer.json` (no `--config` override) | **`vscode`**, `/workspaces/project` | Docker-outside-of-Docker Feature, host daemon | .NET 10.0.400; Git 2.55.0; gh 2.98.0; zsh 5.9 and Oh My Zsh; tmux 3.4; Python 3.12.3; Docker client 29.7.2-2 / host server 29.7.2. Restore/build/format and **94 passed, 8 optional SSH/live-provider tests skipped**. |

The default config's tools and user were observed through `devcontainer exec`, not inferred from its JSON. Its post-create hook restored the solution and found the existing valid development certificate. It printed a workload-verification advisory; no workload upgrade was performed, and the required application checks passed. Native mobile workloads were not validated. For the standalone test, Docker CPU/memory/PID limits were applied after startup; the repo's `hostRequirements` fields are suitability hints rather than enforced container limits.

The Docker-in-Docker Feature requests privileged mode. No host Docker socket is mounted in that container, and its Docker daemon ID differs from the host's. Its initial Docker inventory was empty; subsequent SSH fixtures were nested. This is Docker-state separation, not a VM isolation claim. The default repo config intentionally uses the host Docker daemon instead.

## Reuse and fresh-container cache check

Repeated `devcontainer up` with each original immutable test label returned the same container ID and user:

- Docker test: `5096cb913e789d152e39bc35a9f4aa77c8ab8262428a5f7757807688b0d087a6`, `trusting_proskuriakova`.
- Default config: `721dbfc6d2eda76b012fc6b319993f93f031bd584f539f1468ecdc6593c00615`, `clever_fermi`.

A second default-config instance with a different label produced distinct container `29e824caf636e4fe62943e7794dc80f8af2a0a57dcdabdb8e08fa3585485f8cd` in **15.32 seconds**, including its lifecycle restore. All **12 base/Feature build steps reported CACHED**. The image IDs differed, so this specifically verifies cached build-layer reuse, not byte-identical image identity. Creating a new container still runs its lifecycle setup; installed tools inside cached image layers were not rebuilt.

Retained evidence:

- `.fixture/devcontainer-docker-test/{up.log,reuse.log,tests.log,result.json,inspection.json}`.
- `.fixture/devcontainer-standard-test/{up.log,reuse.log,tools.log,tests.log,cached-new-instance.log,cache-evidence.json}`.
- Independent checkouts under those directories. They are detached verification checkouts, not enrolled task sessions or newly authorized GitHub workers.

Both primary test instances and the extra cache-check instance are stopped after verification; images, named volumes and checkouts are retained. The original seven registered runtimes/workers remain running, including the App-enabled lightweight worker. No Docker pruning or removal of retained work was performed.

## Reproduction

From the repository root, use a separate clone for each primary test and an immutable label. To verify the actual default config:

```bash
devcontainer up --workspace-folder /absolute/path/to/clone \
  --id-label hvo.agentcontrol.test=default-config
devcontainer exec --workspace-folder /absolute/path/to/clone \
  --id-label hvo.agentcontrol.test=default-config \
  bash -lc 'id -un; dotnet --version; git --version; gh --version; zsh --version'
```

For the nested-Docker alternative add `--config .devcontainer/worker-docker/devcontainer.json` to both commands. Use the test command in [provisioning](../DEVCONTAINER_PROVISIONING.md#explicit-docker-integration-test-alternative).

Automatic host selection, durable UI provisioning/recovery, verified SSH/OpenCode enrollment and retirement are still #43. The owner-selected broad Codespaces-style fallback and image refresh policy remain next-stage work; this verification does not claim those are implemented.
