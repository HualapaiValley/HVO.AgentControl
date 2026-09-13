# Beta development assignment guidance

Use these constraints in development coordination instructions. Ordinary questions, broadcasts and OS inquiries do not need a Git workflow. The optional general coordination preamble remains separate.

## Coordinator

- Route work; do not implement or review it yourself. Match each task to available capabilities and explicit permissions.
- Use current durable command records as evidence that a prompt was dispatched. A previous model proposal may have been rejected or superseded. Never wait for a result from an action that was only proposed.
- Return exactly one routing JSON object, starting with `{` and ending with `}`. No prose prefix/suffix, markdown or code fences. Worker responses remain ordinary prose.
- Name the actual recipient, registered workspace, branch, exact known baseline/head, file scope, contract and required validation. Do not assume another worker's independent checkout has the same branch or HEAD.
- Wait for actual pushed SHAs and test evidence. Route unresolved findings to a separate modifying worker and request exact-SHA re-review. Preserve ownership: one modifying worker per branch at a time.
- A `CLEAN` label with an unresolved requested finding is insufficient completion evidence. Treat optional observations separately and explicitly. Missing or truncated evidence requires clarification, not invented success.
- Permission requests, uncertain delivery and unsupported output remain visible blockers. Do not bypass the service's validation or treat a rejected decision as dispatched.
- Completion requires the requested reviewed artifact, validation and workspace handoff. Do not start unrelated work afterward.

## Existing checkout instructions

Use explicit wording such as:

> Use the EXISTING registered persistent checkout at the assigned path. Do not create a new clone or work under `/tmp` unless that is explicitly part of the task. Check `git status` first. If there is uncommitted work, report it without discarding it. Fetch the named branch/baseline before reading files that may not exist in the old checkout. Safely create/switch the requested local branch or detach at the exact review SHA; do not reset, force, or assume the branch already exists. Confirm the actual HEAD. All source changes stay within the assigned file scope.

A review is read-only for tracked source. Temporary executable probes may be stored separately as evidence when needed, but implementations must remain in persistent workspaces. After another worker pushes a correction, fetch the new SHA before claiming the old checkout contains it.

## Worker report

Aim for a final report under 3,500 characters:

- Outcome: completed, changes requested, or incomplete; state unresolved work plainly.
- Exact branch and actual pushed/reviewed SHA; distinguish local edits from a successful remote push.
- Numbered findings/resolutions with expected/actual behavior when relevant.
- Exact validation commands, exit status, and test counts; never hide failures behind shell pipelines.
- Persistent workspace path, actual HEAD and clean/dirty status at handoff.
- Blockers and any permitted work that remains.

At safe checkpoints during longer work, give a short completed/current/blocker/next update. Two minutes is the initial beta cadence; it is best effort, not a scheduled heartbeat service. Do not start background heartbeat jobs or interrupt a useful tool solely to send an update.

Use the pinned SDK and required AGENTS.md checks. The coordination lab is outside the root solution, so run its tests and format verification explicitly. Docker integration runs on the authenticated host or CI, not inside the restricted development containers.

## Current GitHub boundary

Workers have repository-scoped Git deploy keys and can fetch/push this repository. They do not have an account-wide GitHub API token. Return review/results through AgentControl. The authenticated supervising host creates PRs and publishes attributed copies. Do not claim a PR comment was posted by the worker or invent its comment ID.

These rules reflect observed failures in [the beta validation record](validation/beta-development-2026-09-07.md). Explicit decision receipts, schema-constrained output, normalized resource inventory and durable evidence cursors remain tracked improvements rather than guarantees supplied by prompts alone.
