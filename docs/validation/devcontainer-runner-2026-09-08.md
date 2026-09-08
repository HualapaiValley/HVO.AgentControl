# Official Dev Container runner acceptance

Executed on a disposable local Linux Docker-host fixture on 2026-09-08. No current worker, sidecar, provider credentials or owner application data were used.

Official CLI `0.89.0`, npm lock integrity and bundled JavaScript SHA256 `22b5b3a7345608f552db2f1af589ca06c7274d116778d00aa67cbfe1e1cafc65`. The runner invoked `read-configuration`, `up` and `exec` through process argument arrays; Docker independently confirmed labels, container/image/mount identity and cleanup.

| Creation | Container ID | Image ID | Effective user | .NET SDK |
| --- | --- | --- | --- | --- |
| cold | `77f9aa6bf889850d7659ca74dbe3ca42531612b71021d2b3588413c63f81d218` | `sha256:eef376412434630d4cf76b725b22b9aee6a0817654129866988aab9b94adbf48` | vscode (UID 1000) | 10.0.400 |
| warm | `4ade92abcc5962541e521d334875d0a50886483914c4be65b20830662cdb86cb` | `sha256:c6b00a84e1a80c85bccc5345af2847e855100e2f39fa4fefe9840aaf14e5491c` | vscode (UID 1000) | 10.0.400 |

The cold creation explicitly used `--build-no-cache`. Warm creation used another operation and checkout, and reused all 15 observed filesystem layers. Image configuration IDs differ because CLI metadata identifies the separate workspace; that does not invalidate cache reuse. Both containers ran Git/bash, the source-pinned local Feature, the postCreate marker check and `dotnet run --project Probe.csproj --configuration Release`, which printed `disposable-project-built`.

Reconstructing the runner/admission caller returned the original container without repeating up. Changing the same operation intent was rejected. A third configuration exited 17 in postCreate and returned Failed with its owned container retained. All three containers were then removed by exact ownership; absence and consumed-attempt/no-retry behavior were checked. Checkouts remained after container removal and were deleted only by final disposable-fixture cleanup. Docker images/build cache were retained.

The combined local acceptance run passed 23 tests (22 deterministic regressions plus the actual CLI case) in 44 seconds. The separate CI workflow repeats this against the proposed revision and retains `devcontainer-acceptance.json` with requested/resolved/observed values, layer IDs and cleanup outcomes. Full solution checks and exact-head CI are reported in the PR. This validates a host-side runner, not UI/API enrollment, capacity reservation, remote hosts, provider access or live worker replacement.
