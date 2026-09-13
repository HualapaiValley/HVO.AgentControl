# External Issue Tracker

Canonical, read-only record of upstream issues that affect this project. This is
the **single place** for upstream status — do not copy drifting status or draft
narratives into other docs. No upstream issue, comment, label or PR is created or
edited by updating this document. Only #48759 was filed on our behalf; the
other rows track reports from the wider community. Owner below means our local
follow-up owner, not the upstream issue's author or assignee.

Last checked: **2026-09-13** (UTC) via read-only `gh api`.

## OpenCode (`anomalyco/opencode`)

| Ref | Product | Title / URL | Upstream state | Last checked | Impact | Local workaround | Recheck trigger | Owner |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 48759 | OpenCode | [Session-scoped bidirectional model and agent selection for attached TUI / ACP](https://github.com/anomalyco/opencode/issues/48759) | open, no labels, 0 comments | 2026-09-13 | Blocks two-way model/agent sync between portal and attached TUI | Portal shows the observed server session model and disables the selector (`modelSyncSupported=false`); change model in the TUI picker | OpenCode version change; before enabling the selector; before tagging `0.1.0` | RoySalisbury |
| 47836 | OpenCode | [tui: bindable "set as default model for this agent" action in model dialog](https://github.com/anomalyco/opencode/issues/47836) | open, label `2.0`, 3 comments | 2026-09-13 | No UI action to bind a default model; affects default-selection UX | Select the model in the TUI picker per session; no default automation | OpenCode version change; if default-model UX becomes a requirement | RoySalisbury |
| 43179 | OpenCode | [Primary-agent switches silently keep the previous agent's model in V2](https://github.com/anomalyco/opencode/issues/43179) | open, label `2.0`, 3 comments | 2026-09-13 | Switching agent can leave a stale model in effect | Verify the active model after switching agent; do not assume a switch changed it | OpenCode version change; before automating agent switches | RoySalisbury |
| 38940 | OpenCode | [TUI: session model hydration is lost under OPENCODE_FAST_BOOT](https://github.com/anomalyco/opencode/issues/38940) | open, no labels, 0 comments | 2026-09-13 | Session model may hydrate incorrectly on fast boot | Do not set `OPENCODE_FAST_BOOT`; poll the native session model for the authoritative value | OpenCode version change; if fast-boot support is considered | RoySalisbury |
| 42893 | OpenCode | [Model swap gets clobbered by model-less prompts (plugin reminders)](https://github.com/anomalyco/opencode/issues/42893) | open, no labels, 2 comments | 2026-09-13 | A model-less prompt can revert a model selection | Avoid model-less prompts; confirm the model after any prompt | OpenCode version change; before relying on model persistence across turns | RoySalisbury |
| 46311 | OpenCode | [Per-agent model configuration has no effect when using ACP](https://github.com/anomalyco/opencode/issues/46311) | open, no labels, 1 comment | 2026-09-13 | Per-agent model configuration is unreliable under ACP | Rely on a single session model; do not depend on per-agent model config | OpenCode version change; before implementing per-agent models | RoySalisbury |

## Notes

- Before enabling model selection, align the API and UI availability rules:
  degraded sessions may expose terminal/cancel recovery, but model writes
  currently require full readiness. Keep that distinction in selector tests.

- **48759** is this project's submitted feature request. It is the primary
  bidirectional model/agent selection item and gates enabling the portal model
  selector. It remains open with no maintainer response.
- The related issues above are **related, not confirmed duplicates** of 48759.
   Our model-sync reproduction applies to OpenCode **1.18.30**. The other rows
   summarize upstream reports, not independently reproduced bugs; recheck their
   versions and evidence before treating them as local defects.
- Recheck the rows by reading state, labels and comment counts; re-evaluate the
  local workaround and the selector decision before any release. Filing guidance
  is in [UPSTREAM-OPENCODE.md](UPSTREAM-OPENCODE.md).
