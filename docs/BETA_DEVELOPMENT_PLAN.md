# AgentControl beta development and proving plan

Status: execution started 2026-09-07. The owner authorized creating RoySalisbury/HVO.AgentControl, issues, a container development fleet and real coordination exercises. Repository visibility defaults to private. Start with three development agents and one dedicated routing coordinator; scale only after observing the first complete handoff.

## Outcome and boundaries

AgentControl will coordinate development of its own repository through durable prompts, responses and artifacts. The first milestone is a demonstrated reviewer → separate fixer → reviewer handoff, followed by a feature assignment, with exact Git SHAs and validation evidence. The exercise uses an isolated test project, never deliberate defects in the running control service.

The present OpenCode adapter is the initial transport. A provider/model choice inside OpenCode is not a native Claude Code or Codex integration. Existing owner/M4 conversations remain separate from the beta participants. Keep the current single-controller SQLite constraint.

## Deployment

- Existing web/control service: authenticated Blazor UI and durable SQLite at the current host URL.
- Lightweight coordinator: dedicated persistent OpenCode conversation in the coordinator container; routing tools remain disabled. It receives the beta objectives, participant IDs, capability summaries and evidence, and writes bounded routing decisions.
- Three development containers: one shared .NET SDK 10.0.400 image with SSH, tmux, Git, curl, jq, ripgrep, archive utilities and GitHub CLI. Separate persistent home/workspace and SSH host-key volumes. Each receives its own repository-scoped deploy key, pinned host keys and Git identity. Initially two CPUs / four GiB per development container; verify actual limits and available resources.
- No host Docker socket in development containers. Host/CI runs Docker integration fixtures serially; worker validation uses restore, Release build, formatting, unit tests and the isolated exercise suite. Browser tooling remains an explicit validation workload.
- GitHub issue/PR publication uses the authenticated host. Containers use repository-specific Git keys; they return review/result evidence through AgentControl. Automated GitHub publication is a subsequent scoped integration, not an unrecorded copy of the host's account-wide credential.
- Persist native sessions/configuration and workspace state. Web restarts must preserve conversation identities. Never stop the coordinator/worker processes merely to deploy UI changes.

## Workstreams and sequence

| Phase | Deliverable | Acceptance / evidence |
| --- | --- | --- |
| B0 Repository | Initial audited commit, private GitHub repository, CI and issue plan | No local credentials/state staged; hosted build/test result recorded |
| B1 Fleet | Reproducible image/Compose/init/enrollment runbook; three ready development workers | Runtime capability/SDK checks, independent clones/keys and persistent session IDs |
| B2 Review exercise | Draft PR with isolated flawed pricing test project | Builds successfully; requirements make seeded boundary/arithmetic defects objectively detectable |
| B3 Coordinated correction | Coordinator dispatches review, routes findings to another worker, obtains rereview | Distinct worker IDs, exact reviewed/fixed SHAs, tests, receipts and decision/result references; no operator-authored substitute reviews |
| B4 Feature exercise | New issue for a bounded feature in the same lab | Coordinator assigns implementation, independent review and validation; separate branch/PR |
| B5 Reliability improvements | Fix observed stalls and implement service-owned updates/evidence | Restart/long-tool/quiet-worker experiments; no duplicate prompts or manufactured progress |
| B6 Broader beta | Enrollment/receipts, work/resource claims, one additional native harness | Failure/recovery tests before enabling broader concurrency |

## Development backlog

Create GitHub issues for these independently reviewable slices and link them to the beta epic:

1. Reproducible development fleet and operator runbook.
2. Durable scheduled operator updates and notification outbox, independent of model turns.
3. Authenticated participant enrollment and explicit command receipts with authority generations.
4. Work-item lifecycle claims that include review/CI/cleanup, separate from native-turn capacity.
5. Endpoint-scoped resource reservations and safe recovery after disconnect.
6. Bounded evidence retrieval and durable coordinator cursors/digests.
7. Exact-range review records, findings, deadlines and guarded finalization profile.
8. Native Claude Code/Codex adapter investigation and one bounded implementation route.
9. Coordination observability: current assignment, last receipt/progress, blocker, next expected event and operator intervention.
10. Pricing lab review/fix/rereview exercise.
11. Pricing lab shipping-feature exercise.
12. Failure injection: restart, lost response, long tool, invalid decision, pending approval and unavailable model.

The prototype adoption review remains the design reference. These issue records distinguish planned work from current functionality; beta exercises can reveal a smaller necessary correction before a larger feature is built.

## First exercise contract

The lab calculates an order total from a decimal unit price and positive integer quantity. Reject negative price and nonpositive quantity. Multiply price by quantity, apply a ten-percent discount at quantity ten or above, then round the final result to two decimals using AwayFromZero. The initial PR intentionally contains arithmetic/boundary/rounding defects with only a narrow passing test. Its description labels it as a coordination exercise and it stays draft until independently reviewed and fixed.

Coordinator receives PR/branch/base/head, requirements, agent roles and permitted actions. Reviewer uses the exact local diff and produces findings with reproducible examples. Fixer uses a separate clone, fetches the PR branch, implements fixes and regression tests, and pushes the exercise branch. Reviewer fetches the new head, verifies each finding and runs the relevant tests. No worker merges, deletes branches or edits the production application during this exercise.

The host publishes genuine agent findings/results as attributed PR comments, with source command and worker IDs. The host may prepare the deliberately flawed seed, synchronize artifacts, approve already-scoped tool requests and diagnose infrastructure, but does not silently do the review or correction in place of an agent. Record every such intervention.

The second exercise requests shipping cost: free for a discounted subtotal of at least 50.00, otherwise 4.95; reject negative subtotals. It gets a separate issue and feature branch, with independent review. Do not mix unresolved seed corrections into the feature branch.

## Safety and authority during the beta

- Limit tasks to this repository and the exercise branch/project unless a product issue explicitly authorizes broader files.
- One modifying agent per branch/workspace at a time. A handoff names the new owner and exact head; independent clones do not by themselves prevent branch collisions.
- Reviewers report incomplete when evidence is inaccessible. Idle/exit zero is not proof of task success.
- Preserve uncertain delivery and reconcile before retrying. Do not release work/resource ownership simply because a heartbeat is late.
- Permission requests remain visible and owner-controlled under existing behavior; do not turn off all permissions globally to make the demo pass.
- PR merge is a separate reviewed step. Deliberately flawed seed code must not reach the running product.

## Observation and improvement loop

For each run, record run/command/native session IDs, assignment recipients, exact prompt, accepted/start/progress/terminal times where available, requested and observed model, Git artifacts/SHAs, review findings, validation, and intervention history. Mark observations separately from model claims. Capture timing gaps and model errors without repeatedly resubmitting prompts.

Use one bounded workflow at a time initially. Record results in `docs/validation/` and relevant GitHub issues/PRs. Promote product fixes through their own changes and focused regression tests. Keep the broader beta epic open until service-owned heartbeat/recovery and at least one extra harness have been exercised.

## Validation and handoff

Every product change runs the repository-mandated restore, Release build with warnings as errors and format verification. Run meaningful unit/integration/browser checks appropriate to the change. The lab suite runs independently in CI whenever its project exists. Evidence must identify whether execution was native model work, deterministic fixture output or operator action.

Handoff includes repo/issue/PR URLs, UI URL, fleet names and worker IDs, models used, active run state, completed evidence, unresolved blockers, and exact next steps. Never include deploy keys, provider tokens or owner passwords in the repository or reports.

## GitHub tracking

Repository: [RoySalisbury/HVO.AgentControl](https://github.com/RoySalisbury/HVO.AgentControl). Beta epic: [#1](https://github.com/RoySalisbury/HVO.AgentControl/issues/1).

- [#2: Provision reproducible .NET beta development containers](https://github.com/RoySalisbury/HVO.AgentControl/issues/2)
- [#3: Persist scheduled operator updates independently of model turns](https://github.com/RoySalisbury/HVO.AgentControl/issues/3)
- [#4: Add participant enrollment and explicit command receipts](https://github.com/RoySalisbury/HVO.AgentControl/issues/4)
- [#5: Track work-item ownership through review and cleanup](https://github.com/RoySalisbury/HVO.AgentControl/issues/5)
- [#6: Add endpoint-scoped resource reservations](https://github.com/RoySalisbury/HVO.AgentControl/issues/6)
- [#7: Persist evidence cursors and bounded coordinator retrieval](https://github.com/RoySalisbury/HVO.AgentControl/issues/7)
- [#8: Record exact-range reviews and bounded correction handoffs](https://github.com/RoySalisbury/HVO.AgentControl/issues/8)
- [#9: Investigate and implement one additional native agent adapter](https://github.com/RoySalisbury/HVO.AgentControl/issues/9)
- [#10: Expose current assignment, expected events and stalls in Coordination](https://github.com/RoySalisbury/HVO.AgentControl/issues/10)
- [#11: Exercise: review, fix and rereview the pricing lab](https://github.com/RoySalisbury/HVO.AgentControl/issues/11)
- [#12: Exercise: add a simple shipping rule to the pricing lab](https://github.com/RoySalisbury/HVO.AgentControl/issues/12)
- [#13: Exercise restart, long tools and uncertain coordination delivery](https://github.com/RoySalisbury/HVO.AgentControl/issues/13)
