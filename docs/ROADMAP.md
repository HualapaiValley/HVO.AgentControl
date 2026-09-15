# V2 Development Order

Status reflects the active code, not the standalone POC.

1. **Reliable ACP transport — implemented (baseline):** newline framing,
   concurrent RPC correlation, stream limits, permission round trips, process
   supervision and bounded cancellation.
2. **Control-host portal — implemented (0.1.0 slice):** Blazor static SSR
   portal, owner Basic auth, embedded terminal, one owned OpenCode ACP runtime,
   loopback native HTTP, and an authoritative SQLite organization/session store
   at `/control-data/control.db` (with `runtime.json` retained as evidence).
3. **Durable controller — partial:** organization/session identity persists in
   the authoritative store and a failed load faults instead of being replaced.
   A member/task journal, one active writer per session, uncertain-delivery
   reconciliation and explicit resume decisions are not implemented.
4. **Self-contained worker lifecycle — worker artifact implemented (#213), provisioning pending:** the distinct worker image has persistent private control/home/workspace/session volumes, fixed PID1 supervision, ACP start/stop/status, process generations, disconnect survival and explicit restart interruption. Devcontainer creation, host readiness/provisioning and checkpoint policy remain #217+.
5. **Private authenticated communication — worker side implemented (#213), controller/remote side pending (#217):** the worker bridge implements bounded NDJSON, mutual role-bound HMAC, local peer credentials, leases/epoch fencing, durable idempotency/replay/holds and pending permission state. The control host has no connector binding, SSH routing or two-host success path and continues to report `WorkerControlImplemented=false`.
6. **Owner visibility — partial:** organization navigation, department counts,
   host-computed employee availability, exact safe employee diagnostics and an
   employee-ID-bound same-origin terminal are live for the single owned control
   host. Pending approvals are explicitly unsupported/empty; worker lifecycle,
   hiring, tasks, proxied native web and multi-host routing remain future work.
7. **Development workflow — not implemented:** manager delegation, independent
   review, exact-head evidence, GitHub App integration and bounded write
   authorization. Developer provisioning and task routing are absent.
8. **Fault acceptance — not started:** disconnect during tools/permissions,
   controller/worker crashes, long tools, provider failures, duplicate delivery,
   partial effects, and a repeated operational test without human permission
   babysitting.

None of the not-implemented milestones may be claimed complete merely because
the standalone connectivity POC passed. The concrete Phase 1 contracts for
organization identity, internal-role placement, storage, orientation, authority,
transport and the disposable two-host test are proposed or partially implemented as marked in
[Phase 1 contracts](PHASE-1-CONTRACTS.md).
