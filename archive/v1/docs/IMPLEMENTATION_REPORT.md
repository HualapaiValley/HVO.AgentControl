# Initial release implementation report

Prepared 2026-09-06. M0–M5 are implemented and verified on Linux x64, including real provider work through two disposable SSH targets. All available acceptance checks passed. M6 remains optional and unimplemented. The application is usable in Manual mode without a coordinator endpoint.

The subsequent owner-requested runtime onboarding improvement is also complete: guided first-trust verification, encrypted entered credentials, prerequisite checks, automatic worker setup and validated OpenCode server startup controls. Its final regression passed 14 tests, with both optional native tests verified in the preceding combined run; browser and Docker checks also passed. [Follow-up validation evidence](validation/runtime-onboarding-2026-09-06.md).

The page organization follow-up is complete: Overview contains fleet status and agent conversations, while Runtimes and Workers have separate management pages with a shared menu. Two-worker navigation/draft isolation, mobile layouts, guided setup and demo session preservation passed. Docker browser validation uncovered and fixed missing Blazor assets during the project-only restore stage; the new package regression passed on the rebuilt image before and after restart. [Page organization and packaging evidence](validation/page-organization-2026-09-06.md).

## Milestones

| Milestone | Delivered behavior | Evidence |
| --- | --- | --- |
| M0 | Preserved existing .NET/Blazor conventions; pinned SDK/dependencies/OpenCode; inspected actual release schema and source; built isolated SSH fixtures | `global.json`, central package versions, `OPENCODE_COMPATIBILITY.md`, actual `/doc` snapshot and native install tests |
| M1 | Owner sign-in, protected API/SignalR, CSRF/rate limits, mounted secret references, durable keys/database/migrations, request identities and real UI state | Authentication/CSRF/persistence/restart tests; browser sign-in; built/started Docker package |
| M2 | Validated SSH keys, managed bootstrap/install/reuse, protected SFTP launcher, explicit loopback forwarding, ownership/port checks, provider visibility | Real SSH and real release install; wrong-key, port/tmux conflict and concurrent ensure tests |
| M3 | Workspace/native session/model selection, async task and follow-up, transcript/tools/usage, queue, abort and native replies | Deterministic browser smoke plus real-provider browser task/follow-up, two native permission approvals and one question |
| M4 | Transactional record/claim, per-worker serialization, unknown delivery, retained history reconciliation, pending request recovery, browser/backend/SSH/SSE restart behavior, bounded storage | Recovery integration tests; actual lost-response fixture; real SSH loss during a tool and backend restart with unchanged session IDs |
| M5 | Multiple runtimes/workers, canonical workspace claims and clean-source worktrees, runtime/global capacities, consolidated filters/attention, deployment and runbooks | Three real sessions on two actual disposable SSH endpoints; separate files; Docker build/start/restart; README and operations instructions |

The architecture remains browser → authenticated AgentControl → persistent SSH forwarding → OpenCode HTTP/SSE, with tmux owning the remote process. The existing Blazor foundation supersedes the handoff's proposed Angular default. SQLite intentionally supports one replica; the process locks its data directory. There is no coordinator inference, terminal scraping, direct browser SSH, generic CLI transport, automatic provider sign-in or hidden approval bypass.

## Actual real-provider evidence

On Ubuntu 24.04/x86_64 disposable SSH targets, OpenCode **1.18.29**, provider **opencode**, model **big-pickle**:

- Target A ran two separate workspaces/sessions while target B ran a third. Each wrote only its own `evidence.txt` for the requested task. An SSH disconnect occurred while the first worker's sleep/tool was running; the backend then restarted. Original native session identities and one dispatch attempt per prompt were preserved.
- Original session on A accepted a follow-up and appended `FOLLOWUP`; other files retained only their own marker. Exact native IDs, prompts, tool output and file contents are in [native evidence](validation/native-live-2026-09-06.json).
- The final real browser run created `browser-evidence.txt`, answered two native `once` permissions and one native question, sent a follow-up in the same conversation, and reloaded the view. Final contents were `BROWSER_NATIVE_135eeb9668` and `FOLLOWUP` on separate lines. [Browser evidence](validation/native-browser-2026-09-06.json).
- A separate native contract probe resubmitted the same caller message ID: one message contained two duplicate text parts. This proves the ID is not a safe prompt retry token. The service never automatically repeats an uncertain mutation.

These are real OpenCode/provider operations on isolated Docker SSH targets, not owner-supplied production hosts. Anonymous provider access was available during testing and may change. Provider configuration remains an explicit remote prerequisite when it is unavailable.

## Acceptance coverage and limits

| Acceptance area | Result / practical limit |
| --- | --- |
| Missing OpenCode, repeated/concurrent ensure, unrelated listener/tmux, prerequisites | Real installer and SSH fixture exercise these paths; no unrelated process adoption/termination |
| Quotes/newlines/metacharacters | Actual SSH bootstrap state and worktree paths tested; prompts travel only as HTTP JSON |
| Unknown/changed host identity | Exact SHA256 pin rejects mismatches; supported key algorithm is selected explicitly |
| Provider setup | Connected-model discovery and empty-provider fixture covered; real anonymous model ran, without claiming future access |
| Concurrent sessions and retries | Three native sessions plus duplicate application UUID tests; scoped routing and durable per-worker dispatch |
| Stream fragmentation/replacement/errors | Byte-fragment UTF-8/multiline SSE limits plus duplicate replacement fixture; UI renders plain untrusted text |
| Ambiguous acceptance | Accepted-but-response-lost fixture reconciles by native message ID; unresolved delivery remains blocked until explicit acknowledgement |
| SSH/SSE/browser/backend outages | Fixture recovery matrix and real active-tool SSH/backend interruption passed; no automatic replacement worker or prompt replay |
| Requests/cancellation/outcomes | Native browser and fixture replies; cancellation receipt/observed idle separate; idle never proves success |
| Storage failure and retention | Database trigger failure rejects submission; event/transcript retention and bounded audit/registration caps; no silent idempotency eviction |
| Deployment | Local and Docker startup/readiness, anonymous API rejection and container restart tested; no public production deployment performed |
| Platforms | Linux x64 executed. macOS and Linux arm64 installer routes/checksums implemented but live platform checks remain unavailable |
| Native restart | Deliberate owned-server stop/restart recovered the same persisted conversation in the native test; tool execution and legacy pending approvals/questions are memory-scoped and not guaranteed to resume |
| History guarantees | SSE has no verified durable replay. Reconciliation restores bounded retained snapshots; transient events may be missing and gaps remain visible |
| Workspace isolation | Application validates/claims workspaces; same-user processes and worktrees are not security sandboxes. Consistently identify an environment rather than registering aliases to bypass workspace ownership checks |

No commits, pushes, PR publication, production remote changes or deployment were performed. Local source and disposable test artifacts remain reviewable in the workspace.

## Validation commands and final checks

The required restore, Release build with `--warnaserror` (zero warnings/errors), and format verification all passed after the final source changes. The combined suite passed **11/11**, including both native tests; subsequent recovery regression passed **9/9** with the two native tests explicitly skipped in that invocation. Final canonical-path/persistence regression passed **5/5**. Browser fixture and real-provider browser runs both passed. The final non-root Docker image passed startup, readiness, anonymous API rejection and restart against existing persistent data/keys. Shell syntax and Compose validation passed.

Exact commands, elapsed times and image identity are in the [validation record](validation/initial-release-2026-09-06.md); the [status checkpoint](IMPLEMENTATION_STATUS.md) lists remaining operator inputs and exact next steps. Reproduction/setup commands are in the [README](../README.md). No known test failures remain. The optional native/provider tests are kept out of unattended CI; deterministic unit/persistence/SSH integration checks are wired into the existing workflow. Hosted CI itself was not run from this session.
