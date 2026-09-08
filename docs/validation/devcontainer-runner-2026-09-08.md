# Official Dev Container runner acceptance

Executed on a disposable local Linux Docker-host fixture on 2026-09-08. No current worker, sidecar, provider credentials or owner application data were used.

Official CLI `0.89.0`, npm lock integrity and bundled JavaScript SHA256 `22b5b3a7345608f552db2f1af589ca06c7274d116778d00aa67cbfe1e1cafc65`. The runner invoked `read-configuration`, `up` and `exec` through process argument arrays; Docker independently confirmed labels, container/image/mount identity and cleanup.

| Creation | Container ID | Image ID | Effective user | .NET SDK |
| --- | --- | --- | --- | --- |
| cold | `908830d3346351327f3a76214263cb51d46e2116394957b74aac91068ae6027f` | `sha256:536c2d4895e6e6dc288eeed3fd9887e0f1a7e1b0b73c562063b51edf1f69fa48` | vscode (UID 1000) | 10.0.400 |
| warm | `27e7890b77901abfbaabcbe7c896503488ce32ae435b9714cab0550d25687bfa` | `sha256:8541ac2919f6cd79d97bce722e44b636fd85ee2245f32d8744a25c3c76af6fb1` | vscode (UID 1000) | 10.0.400 |

The cold creation explicitly used `--build-no-cache`. Warm creation used another operation and checkout, and reused all 15 observed filesystem layers. Image configuration IDs differ because CLI metadata identifies the separate workspace; that does not invalidate cache reuse. Both containers ran Git/bash, the source-pinned local Feature, the postCreate marker check and `dotnet run --project Probe.csproj --configuration Release`, which printed `disposable-project-built`.

Reconstructing the runner/admission caller returned the original container without repeating up. Changing the same operation intent was rejected. A third configuration exited 17 in postCreate and returned Failed with its owned container retained. All three containers were then removed by exact ownership; absence and consumed-attempt/no-retry behavior were checked. Checkouts remained after container removal and were deleted only by final disposable-fixture cleanup. Docker images/build cache were retained.

Two additional disposable checkouts exercised rejected inputs before creation. The pinned CLI resolved local Feature `privileged: true` and `capAdd: [SYS_PTRACE]` only in `mergedConfiguration`; the runner returned Unsupported with no up effect. Another checkout had clean Git status while its Feature directory was ignored; complete committed-input validation rejected it without creating a container. Both had zero Docker owners during final reconciliation.

The corrected combined local acceptance run passed 40 tests (39 deterministic regressions plus the actual CLI case) in 53 seconds. Deterministic recovery cases retained and removed exact ownership after missing CLI, changed CLI, changed configuration and deleted workspace; no consumed up attempt replayed. The receipt recorded three exact removals, no cleanup errors, 15 identical cold/warm filesystem layers and no retained fixture root. The separate CI workflow repeats this against the proposed revision and retains `devcontainer-acceptance.json` with requested/resolved/observed values, layer IDs and cleanup outcomes. Full solution checks and exact-head CI are reported in the PR. This validates a host-side runner, not UI/API enrollment, capacity reservation, remote hosts, provider access or live worker replacement.
