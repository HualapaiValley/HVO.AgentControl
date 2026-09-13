# V2 Development Order

1. **Reliable ACP transport:** framing, concurrent RPC correlation, stream
   limits, permission round trips, process supervision and cancellation.
2. **Durable controller:** member/session/task journal, one active writer per
   session, uncertain-delivery reconciliation and explicit resume decisions.
3. **Self-contained worker lifecycle:** devcontainer provisioning, independent
   persistent home/repo volumes, readiness, cancel/checkpoint/stop/restart.
4. **Private authenticated communication:** controller/bridge identity,
   connection leases, replay acknowledgments and remote-host routing.
5. **Owner visibility:** worker status, pending decisions, streamed output,
   proxied native web and optional browser terminal attached to tmux.
6. **Development workflow:** manager delegation, independent review, exact-head
   evidence, existing GitHub App integration and bounded write authorization.
7. **Fault acceptance:** disconnect during tools/permissions, controller/worker
   crashes, long tools, provider failures, duplicate delivery, partial effects,
   and a repeated operational test without human permission babysitting.

The root service is currently only a baseline. None of these milestones is
claimed complete merely because the standalone connectivity POC passed.
