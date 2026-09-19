# V2 Development Order

Status reflects the active code, not the standalone POC.

1. **Reliable ACP transport — implemented (baseline):** newline framing,
   concurrent RPC correlation, stream limits, permission round trips, process
   supervision and bounded cancellation.
2. **Control-host portal — implemented (0.1.0 slice):** Blazor static SSR
   portal, owner Basic auth, embedded terminal, one owned OpenCode ACP runtime,
   loopback native HTTP, and an authoritative SQLite organization/session store
   at `/control-data/control.db` (with `runtime.json` retained as evidence).
3. **Durable controller — partial:** schema v9 preserves organization/session
   and #213 policy identity and adds remote host/enrollment, cursor, task/request,
   provisioning/resource and recovery-obligation records with exact-signature
   chained migration and verified v3/v4 backups. Durable hire request creation
   and revision-bound rejection are shipped; durable pending requested hires and
   the requested count are supported. Immutable container profiles with the
   constrained devcontainer subset and the seeded `generic-employee` profile
   (#258) and per-host verified profile image builds (#259) are shipped.
   Approval action and provisioning are not implemented; the owner accepted the
   profile-based design on 2026-09-18 and the remaining slices are #260
   (approve/provision/orient) and #261 (rebuild). The schema
   also has a sanitized deduplicated controller event inbox, conditional
   request/cancellation/provisioning transitions, exact recovery markers and
   restart reconciliation. The first managed disposable two-host path was
   operationally accepted 2026-09-18 (#217).
4. **Self-contained worker lifecycle — worker artifact implemented (#213), disposable path accepted (#217), production provisioning pending:** the distinct worker image has persistent private control/home/workspace/session volumes, fixed PID1 supervision, ACP start/stop/status, process generations and disconnect survival. ACP exit or initialization failure leaves the authenticated bridge alive for status, replay and reconciliation but is terminal for that container; recovery is explicit container replacement with automatic restart disabled. Fresh ACP session creation/loading is lease-fenced and durably marks an uncertain non-retryable operation when completion cannot be proved. Exact schema-v7 worker journals migrate to v9 only after a create-once verified `bridge.schema-v7.db` plus SHA-256 backup in `/control`; unknown or altered signatures fail closed. Devcontainer creation, fresh transport/process restart orchestration, host readiness/production provisioning, checkpoint policy and key rotation remain future work.
5. **Private authenticated communication — first managed disposable two-host path accepted (#217):** worker and controller share the storage-independent NDJSON/canonical-hash/HMAC library. The controller has race-resistant private-file opening, exact mutual authentication and result parsing, configured-reference host APIs, local exact known_hosts fingerprinting, deterministic quoted SSH/Docker commands and a transparent no-key worker pipe. The process adapter and hosted manager remain disabled by default. On 2026-09-18 the first managed disposable two-host path was operationally accepted on an authorized topology (pinned ED25519 strict SSH to `home-dev-02`, isolated labeled worker, ACP session lifecycle, provider prompt/tool, cancellation, disconnect/reconnect reconcile, stale-lease fencing, bounded replay, worker restart and viewer attach), and the disposable resources were cleaned up. `/api/info` reports `WorkerControlImplemented=true` and `WorkerControlOperationallyValidated=true`; `WorkerControlEnabled` remains the deployment gate and is false by default. Key rotation/compromise re-enrollment and production managed hiring/provisioning remain unproven.
   The accepted path does not validate key rotation or compromise re-enrollment and does not authorize production managed hiring/provisioning.
6. **Owner visibility — partial:** organization navigation, department counts,
   host-computed employee availability, exact safe employee diagnostics and an
   employee-ID-bound same-origin terminal are live for the single owned control
   host. Durable pending requested hires are visible; approval action and
   provisioning remain not implemented pending #219 owner discussion and
   approval. Worker lifecycle,
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
