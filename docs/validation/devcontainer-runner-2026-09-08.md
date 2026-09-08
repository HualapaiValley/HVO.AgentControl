# Official Dev Container runner acceptance

Executed on a disposable local Linux Docker-host fixture on 2026-09-08. No current worker, sidecar, provider credentials or owner application data were used.

Official CLI `0.89.0`, npm lock integrity and bundled JavaScript SHA256 `22b5b3a7345608f552db2f1af589ca06c7274d116778d00aa67cbfe1e1cafc65`. The runner invoked `read-configuration`, `up` and `exec` through process argument arrays; Docker independently confirmed labels, container/image/mount identity and cleanup.

| Creation | Container ID | Image ID | Effective user | .NET SDK |
| --- | --- | --- | --- | --- |
| cold | `933672ea11d5d921aa87ba25c0d83ef67e57483c062269eca02ba456ebe12b2a` | `sha256:92dfa464c6d66b516015799ed3bd4a415ddac8c1cc005531ff717e7a83fd248d` | vscode (UID 1000) | 10.0.400 |
| warm | `e4d017721a1ae4c8a4056f935b9c259af16429862739ad5063d50e5ca67fcf7d` | `sha256:148b98a3d36c8a361e56554520910f58f0345e5bfa2745874d7789158e05ffe2` | vscode (UID 1000) | 10.0.400 |

The cold creation explicitly used `--build-no-cache`. Warm creation used another operation and checkout, and reused all 15 observed filesystem layers. Image configuration IDs differ because CLI metadata identifies the separate workspace; that does not invalidate cache reuse. Both containers ran Git/bash, the source-pinned local Feature, the postCreate marker check and `dotnet run --project Probe.csproj --configuration Release`, which printed `disposable-project-built`.

Reconstructing the runner/admission caller returned the original container without repeating up. Changing the same operation intent was rejected. A third configuration exited 17 in postCreate and returned Failed with its owned container retained. All three containers were then removed by exact ownership; absence and consumed-attempt/no-retry behavior were checked. Checkouts remained after container removal and were deleted only by final disposable-fixture cleanup. Docker images/build cache were retained.

Two additional disposable checkouts exercised rejected inputs before creation. The pinned CLI resolved local Feature `privileged: true` and `capAdd: [SYS_PTRACE]` only in `mergedConfiguration`; the runner returned Unsupported with no up effect. Another checkout had clean Git status while its Feature directory was ignored; complete committed-input validation rejected it without creating a container. Both had zero Docker owners during final reconciliation.

The corrected combined local acceptance run passed 40 tests (39 deterministic regressions plus the actual CLI case) in 51 seconds. Deterministic recovery cases retained and removed exact ownership after missing CLI, changed CLI, changed configuration and deleted workspace; no consumed up attempt replayed. Missing checkout paths were explicitly reported as retention unverified. Disposable project outputs use an open umask so host cleanup also works when the hosted runner UID differs from vscode UID 1000; any fixture-directory deletion failure is recorded before final receipt serialization. The receipt recorded three exact removals, no cleanup errors, 15 identical cold/warm filesystem layers and no retained fixture root. The separate CI workflow repeats this against the proposed revision and retains `devcontainer-acceptance.json` with requested/resolved/observed values, layer IDs and cleanup outcomes. Full solution checks and exact-head CI are reported in the PR. This validates a host-side runner, not UI/API enrollment, capacity reservation, remote hosts, provider access or live worker replacement.
