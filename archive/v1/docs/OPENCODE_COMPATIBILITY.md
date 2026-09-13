# OpenCode compatibility record

## Runtime startup options follow-up (2026-09-06)

The actual pinned binary's `opencode serve --help` advertises global `--pure`, `--print-logs`, and `--log-level` (`DEBUG`, `INFO`, `WARN`, `ERROR`), in addition to network settings. AgentControl exposes those three optional settings as typed controls. It retains ownership of the listener address/port and does not execute an arbitrary shell command line. The launcher supplies each selected value as an argument and verifies an options fingerprint before reusing a living server.

`--auto` is declared by the tag's TUI/run commands, not `serve`; the CLI uses strict argument parsing. Running the pinned binary with `serve --auto` fails instead of starting a server. It must not be exposed as a working server startup checkbox or silently translated into broader permission grants. Sources: [pinned serve command](https://github.com/anomalyco/opencode/blob/16747470f976aca3d362ad730bcd3fe82ecc2c9a/packages/opencode/src/cli/cmd/serve.ts), [pinned global flags/strict parsing](https://github.com/anomalyco/opencode/blob/16747470f976aca3d362ad730bcd3fe82ecc2c9a/packages/opencode/src/index.ts), and [CLI documentation](https://opencode.ai/docs/cli/).

Validated 2026-09-06. Supported binary: **1.18.29**, released 2026-09-04. Git tag commit: **16747470f976aca3d362ad730bcd3fe82ecc2c9a**. GitHub release metadata separately identifies `target_commitish` as `02a167e048d3bd7299225068d79e4fce5c830d67`; the inspected checkout is the resolved tag commit. The release binary reports 1.18.29. `/doc` reports OpenAPI 3.1.0 and an API-info version of 1.0.0; that schema-info version is not the executable version.

[Actual server schema snapshot](compatibility/opencode-1.18.29.openapi.json) includes its release/commit wrapper. It was fetched from the running release, not inferred from a moving SDK. [OpenCode release](https://github.com/anomalyco/opencode/releases/tag/v1.18.29), [versioned source](https://github.com/anomalyco/opencode/tree/16747470f976aca3d362ad730bcd3fe82ecc2c9a), [server reference](https://opencode.ai/docs/server/). The adapter checks both health version and core route presence, refuses another version, and never silently upgrades it.

## Endpoint mapping

Every scoped call URL-encodes the explicit canonical remote directory in `?directory=...` (or `&directory=...`). Native IDs are opaque and escaped as path segments. Newer `/api/...` interfaces also exist in this release; the adapter deliberately uses the verified legacy surface below.

| Operation | Native route | Observed contract |
| --- | --- | --- |
| Health | `GET /global/health` | `{healthy:true,version:"1.18.29"}` |
| Contract | `GET /doc` | JSON schema; core paths checked at connect |
| Events | `GET /global/event` | SSE data contains `{directory?,payload:{id?,type,properties}}` |
| Directory check | `GET /path` | `directory` must equal SSH-canonicalized workspace |
| Create/list sessions | `POST /session`, `GET /session` | Created session returns opaque ID, directory, title and timestamps |
| Session/status | `GET /session/{sessionID}`, `GET /session/status` | Sparse status map; absent session status means idle, not task success |
| History | `GET /session/{sessionID}/message?limit=...` | Array of `{info,parts}`; latest identities replace stored snapshots |
| Async prompt | `POST /session/{sessionID}/prompt_async` | `{messageID,model:{providerID,modelID},parts:[{type:"text",text}]}`; 204 is receipt, not completion |
| Abort | `POST /session/{sessionID}/abort` | Boolean response; then observe status/history |
| Providers | `GET /provider` | `all`, `connected`, `default`; only provider/model IDs and names are projected to UI |
| Pending permissions | `GET /permission` | Requests include native ID, session ID, permission/patterns/remembered scope |
| Permission reply | `POST /permission/{requestID}/reply` | `{reply:"once"|"always"|"reject"}` |
| Pending questions | `GET /question` | Requests include native ID, session ID and a questions array |
| Question reply/reject | `POST /question/{requestID}/reply`, `POST /question/{requestID}/reject` | `{answers:string[][]}` or empty reject body; wrong/resolved native identity is rejected |

The documented older `/session/{id}/permissions/{permissionID}` exists but is not used. Authentication is HTTP Basic with `opencode` and a separately mounted server password, exported through a protected remote launch file. Provider credentials are neither read nor copied by AgentControl.

## Required compatibility investigations

| Investigation | Source/schema finding | Actual release evidence / remaining limit |
| --- | --- | --- |
| 1. Directory context | Request location uses directory query or encoded `x-opencode-directory` fallback. | Actual `/path`, creation and history tested in different Git repositories. All adapter commands/replies/queries supply directory. |
| 2. Several workspaces per server | Instance-scoped services/config/MCP state coexist with process/user-level auth, model metadata and data storage. | Two simultaneous real sessions on target A in different repositories passed. Separate target B passed concurrently. Do not assume all caches/config are shared or all are isolated. |
| 3. Global event envelope | Global handler sends directory plus payload; connection/heartbeat frames may omit directory. | Real SSE collected through SSH during file/tool/question/permission operations. Routing uses runtime + managed server + native session + exact directory. |
| 4. Event vocabulary | `message.updated`, `message.part.updated`, `message.part.delta`, `session.status`, `session.error`, permission/question asked/replied/rejected, plus V2 and future events. Tool state and usage are in message parts. | Real tool output, native requests and history observed. Fixture covers split UTF-8, repeat replacement snapshots, unknown types and SSE loss. Plugin hooks are not assumed to be HTTP events. |
| 5. Supplied IDs/idempotency | Legacy message IDs start `msg_`; native IDs encode a 48-bit time/counter prefix with a random suffix, and affect history order. User message is persisted before the execution loop. | `noReply` probe persisted a supplied ID. Submitting it twice returned 200 twice, retained one message **with two duplicate text parts**. Therefore caller IDs are correlation, not idempotency. Generate IDs at dispatch from the latest native prefix (or native session creation time), not from random UUIDs or the local queue time. |
| 6. Busy-session prompts | `prompt.ts` creates the user message before `state.ensureRunning`; an existing runner consumes the evolving history. | Source establishes context insertion/shared runner semantics, not a safe independent application queue or interrupt guarantee. Active steering remains disabled. Fixture and real tasks prove AgentControl's own one-turn-per-worker queue. Arbitrary native busy injection is not advertised as a supported control. |
| 7. HTTP disconnect | Async handler forks prompt execution into service scope and returns 204. | Real coding action completed; real SSH disconnect while `sleep` ran, backend restart, and same-session follow-up passed. Browser fixture closes its page while queued work continues. |
| 8. Native replies | Dedicated permission/question routes and request IDs verified in `/doc` and source handlers. | Real browser run granted two `once` permissions and answered one question, then validated the resulting file. Fixture adds duplicate/stale reply checks and pending question recovery after backend restart. |
| 9. OpenCode restart | Conversations/messages persist in its database; legacy status, pending question and permission maps are instance memory and finalizers clear them. | Backend restart and reattachment to a still-running native server passed. The native install test also deliberately stops/restarts its server and checks that its session identity survives. This does not resume tools or guarantee restoration of pending request identities. The UI retains request audit but marks requests absent from native reconciliation `NoLongerPending`; unknown delivery is not replayed. Destructive reboot/container recreation is not claimed as tool continuation. |
| 10. SSE replay | The global handler subscribes to an in-memory bus, adds heartbeats, and does not implement a durable cursor/replay contract. Native event IDs are not acknowledged replay offsets. | SSH/SSE interruptions reconcile retained message snapshots; explicit history-gap indicators remain. Partial/transient tool events during outages may be permanently missing. |

Source entry points at the pinned revision: `packages/opencode/src/server/routes/instance/httpapi/handlers/{session,global,question,permission}.ts`, `packages/server/src/location.ts`, `packages/opencode/src/session/{prompt,session,message-v2}.ts`, `packages/opencode/src/id/id.ts`, `packages/opencode/src/{question,permission}/index.ts`, and `packages/core/src/{global,database/database}.ts`.

## Capabilities and readiness

Supported: create sessions, async prompts, history/status, SSE, caller message IDs for evidence, abort, native question and permission replies. Unsupported: verified active-turn steering, automatic provider OAuth, coordinator inference, generic CLI adapters, shared modifying workspaces, and direct SSH to an outer host plus `docker exec` routing.

A fresh installation advertised anonymous `opencode` models, including `big-pickle`, even without provider credentials. On the validation date `opencode/big-pickle` actually ran the recorded tasks. This is not a promise of continued anonymous access, price, capacity or model availability. `ModelsAvailable` means discovery succeeded; provider errors remain distinct task/runtime diagnostics. The controllable fixture also verifies the zero-connected-model response.

## SSH/bootstrap and platform evidence

SSH.NET **2026.0.0**, source commit **7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e**, was tested against Ubuntu 24.04 / x86_64, OpenSSH 9.6p1 and tmux 3.4. Host-key verification, Ed25519 private-key auth, SFTP, two simultaneous forwarding channels, local ephemeral loopback ports, wrong-key rejection, port conflict preservation, idempotent/concurrent bootstrap, and paths with quotes/newlines/metacharacters are covered by real SSH fixtures. `ChangePermissions` takes octal digits written as decimal `600`, not numeric bitmask 384. [SSH.NET 2026.0.0](https://github.com/sshnet/SSH.NET/releases/tag/2026.0.0).

The installer selects official release assets, checks their embedded SHA256 digests, and uses user-scoped extraction. Linux x64 baseline installation passed. Linux arm64 and macOS arm64/x64 asset names and digests are recorded in `BootstrapScript.cs`; **those platforms were not executed here**. Native macOS requires tmux/lsof/curl/unzip/shasum already installed. Linux support assumes glibc; Alpine/musl is not an advertised first-release install target. A live but unhealthy owned process is not killed automatically. Failed process starts have a persisted three-launch budget; inspect/reset that budget explicitly in the runbook.

## Reproduction and evidence

- [Native multi-worker evidence](validation/native-live-2026-09-06.json): real provider, three sessions, two SSH targets, disconnect during a tool, backend restart, final files and follow-up.
- [Native browser evidence](validation/native-browser-2026-09-06.json): real task and follow-up, two native permissions, one native question, final file.
- `tests/Fixtures/native-probe.sh` reproduces caller-ID persistence/repetition and a bounded anonymous-provider task in the disposable target. Repeated-ID probing intentionally demonstrates a mutation and must never be applied to a production conversation.
- Native provider tests are separate from the deterministic contract fixture. No owner-supplied production host or credential was used.

The schema snapshot is redistributed under the accompanying [OpenCode MIT license](compatibility/OpenCode-LICENSE.txt). Other runtime dependencies retain their package licenses.
