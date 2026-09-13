# Standalone POC Findings

OpenCode 1.18.30 was exercised independently of Fleet and AgentControl. The
controller ran on home-dev-01; disposable Docker containers ran on home-docker.
The container's default `opencode/big-pickle` performed inference without
copied provider credentials. Free-provider availability is not guaranteed.

## Verified

- ACP prompts, streaming tool events, permissions and final responses over
  Docker-over-SSH stdio.
- Cancellation of a confirmed running sleep, including child termination,
  with about 1.4 seconds including verification overhead.
- Session-scoped cancellation without interrupting a sibling session.
- Worker-owned bridge preserves active work across controller disconnection;
  tested reconnect received the completed response without re-running the tool.
- Saved sessions load across a process/container restart and retain context.
- Same-runtime HTTP history, live TUI output and TUI-origin input visible over
  ACP. The TUI is an attached client running in tmux.
- Browser terminal keyboard, resize propagation, detach/reattach with unchanged
  pane identity, and a proxy serving the native page and exact session API.
  The final browser suite had 18 passing assertions.
- Worker ports were not published. A same-network peer could connect and a
  container on an unrelated Docker bridge could not.

## Not Proven / Lessons

ACP is stdio-only; `--port` is the embedded HTTP server. A bridge is necessary
for the selected persistent private-network transport. Cancellation does not
undo external effects, and session load does not restore an interrupted stack.

Busy-session concurrent prompts did not establish FIFO behavior. The
application must serialize dispatch and coordinate human/automated writers.
Simple in-memory bridge replay is not durable delivery: sequence ACKs, leases,
fencing, half-open detection, overflow recovery and supervision remain work.

The experiment used a private network on one Docker host plus cross-host
Docker/SSH orchestration, not a multi-host overlay network. Native web proxying
was tested for page/session access, not every native UI operation. The test
worker also needed a proper init/supervisor for graceful PID-1 shutdown.

Raw exploratory scripts/results are retained locally in `/home/roys/acp-poc`;
they are not imported as production V2 code or portable acceptance tests.
Earlier exploratory checks had weak assertions; stronger final checks used
computed assistant output rather than matching echoed prompts.
