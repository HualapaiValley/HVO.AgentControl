# V2 Development Order

Status reflects the active code, not the standalone POC.

1. **Reliable ACP transport — implemented (baseline):** newline framing,
   concurrent RPC correlation, stream limits, permission round trips, process
   supervision and bounded cancellation.
2. **Control-host portal — implemented (0.1.0 slice):** Blazor static SSR
   portal, owner Basic auth, embedded terminal, one owned OpenCode ACP runtime,
   loopback native HTTP, durable `/data/runtime.json` and private persistent
   data.
3. **Durable controller — partial:** organization/session identity persists and
   a failed load faults instead of being replaced. A member/task journal, one
   active writer per session, uncertain-delivery reconciliation and explicit
   resume decisions are not implemented.
4. **Self-contained worker lifecycle — not implemented:** devcontainer
   provisioning, independent persistent home/repo volumes, readiness,
   cancel/checkpoint/stop/restart.
5. **Private authenticated communication — not implemented:** controller/bridge
   identity, connection leases, replay acknowledgments and remote-host routing.
   The POC only proved the idea; there is no bridge/reconnect code in the active
   repository.
6. **Owner visibility — partial:** runtime/session/terminal status and a
   same-origin terminal are live. Worker status, pending decisions, proxied
   native web and multi-worker views are future work.
7. **Development workflow — not implemented:** manager delegation, independent
   review, exact-head evidence, GitHub App integration and bounded write
   authorization. Developer provisioning and task routing are absent.
8. **Fault acceptance — not started:** disconnect during tools/permissions,
   controller/worker crashes, long tools, provider failures, duplicate delivery,
   partial effects, and a repeated operational test without human permission
   babysitting.

None of the not-implemented milestones may be claimed complete merely because
the standalone connectivity POC passed.
