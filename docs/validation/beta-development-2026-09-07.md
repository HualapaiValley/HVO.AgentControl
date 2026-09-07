# Native beta development exercises — 2026-09-07

This is a real native-model exercise, not a deterministic fixture transcript. The host seeded the flawed lab, provisioned infrastructure, published attributed GitHub artifacts, diagnosed controller failures, and independently checked results. Worker implementation/review work was performed through AgentControl by OpenCode using `opencode/big-pickle`. The routing coordinator did not implement the lab.

## Repository and fleet

Private repository: [RoySalisbury/HVO.AgentControl](https://github.com/RoySalisbury/HVO.AgentControl). [Plan](../BETA_DEVELOPMENT_PLAN.md), [epic #1](https://github.com/RoySalisbury/HVO.AgentControl/issues/1), [fleet runbook](../BETA_FLEET.md). UI: `http://192.168.1.14:5054` (owner authentication).

| Role | Container | Worker ID | Native conversation |
| --- | --- | --- | --- |
| Coordinator, routing only | hvo-agentcontrol-coordinator | 7b9be4189590410bb6988bf407380b92 | ses_f86b33ccfffeCHF65p9x5r2ObR |
| Beta Reviewer | hvo-agentcontrol-beta-dev1 | 2279daf05d754cd5be4152317ef266d9 | ses_f85a29501ffe7cIGUYljlDJ3ig |
| Beta Fixer | hvo-agentcontrol-beta-dev2 | c18c83d6f5f04969a4df4ef639157ba1 | ses_f85a294f6ffeC6PJNSJ60j7Kwg |
| Beta Developer | hvo-agentcontrol-beta-dev3 | 6688a2c6aef444cfa3b1341094166d46 | ses_f85a29669ffeVP5C2psA62RBTY |

Development containers use pinned SDK 10.0.400, separate persistent home/SSH volumes and repository-scoped Git deploy keys. Each is limited to two CPUs/four GiB. No host Docker socket or account-wide GitHub API token is installed. OpenCode 1.18.29 was installed by runtime connection. Existing demo and Home M4 sessions were excluded from assignments and preserved.

Bootstrap [PR #17](https://github.com/RoySalisbury/HVO.AgentControl/pull/17) is merged. First hosted CI exposed a fresh-container host-key startup race; the bounded SSH readiness fix resolved it. PR CI run `34087401848` passed.

## Pricing: native review → separate fixer → exact-SHA re-review

Run: `da6ea056-fdad-4cff-9ac4-2af684f7943b`. [Issue #11](https://github.com/RoySalisbury/HVO.AgentControl/issues/11), merged [PR #18](https://github.com/RoySalisbury/HVO.AgentControl/pull/18).

| Assignment | Command | Exact head / result |
| --- | --- | --- |
| Reviewer | 5ee8cb3b-0554-4b8e-9af2-bc1275cf1c18 | 363a016eb2d16640aac0d9cf897141673490cda0; CHANGES_REQUESTED |
| Separate fixer | 1d9b329f-9602-4517-8669-46420aa45b87 | Pushed a242c6b293fd93428309b3276d8efe6c357c946c; 10 lab tests pass |
| Reviewer again | f09ef94a-8590-4daa-a545-7caf9160c6e7 | Same a242c6b head; CLEAN, tests and executable probes |

The one-test seed passed while ignoring quantity and excluding quantity ten from the discount. The reviewer identified both defects and missing regression coverage. Its separately numbered discount-basis finding is another manifestation of the quantity defect. The fixer corrected the calculation and added nine cases, repairing its own initial decimal-attribute compilation error and missing final newline. It noticed the lab is outside the root solution and explicitly validated lab formatting too.

The supervising host independently inspected the actual diff, ran the ten lab tests and lab formatting, and ran six additional executable checks: quantities 9/10/11, zero price, rounding after multiplication (`0.335 × 3 → 1.01`), and rounding after discount (`0.055 × 10 × 0.90 → 0.50`). All passed. Hosted CI `34088205504` passed on the exact corrected head, including SSH/recovery integration. The seed was squash-merged only with these corrections; main baseline became `401f126d11d7909c16d6a3c69a64b2a403a9243b`.

Attributed native reports: [first review](https://github.com/RoySalisbury/HVO.AgentControl/pull/18#issuecomment-5565655187), [fix](https://github.com/RoySalisbury/HVO.AgentControl/pull/18#issuecomment-5565674772), [re-review and independent check](https://github.com/RoySalisbury/HVO.AgentControl/pull/18#issuecomment-5565682382), [completion](https://github.com/RoySalisbury/HVO.AgentControl/pull/18#issuecomment-5565715246).

### Actual controller failure and recovery

The controller paused before recording completion because the accumulated context exceeded 64,000 characters. Native results contained tens of thousands of characters of tools/narration, and prefix truncation could omit the final verdict. [Issue #20](https://github.com/RoySalisbury/HVO.AgentControl/issues/20), merged [PR #21](https://github.com/RoySalisbury/HVO.AgentControl/pull/21), changed coordinator evidence to plain bounded latest reports with omission flags and durable command IDs; full history remains stored. Two regression cases cover verbose tools, large final reports, exact verdict/SHA retention and no duplicate assignment.

Required restore/Release warnaserror build/format passed, as did 31 local tests (six optional integrations skipped). Hosted CI `34088500870` passed, including SSH integration. After web-only deployment, all six native session IDs were unchanged. Resuming the SAME run produced a 48,016-character context and genuine completion decision `936b94ba-d478-4c7b-b3ff-74877b5ae437`. Exactly three worker assignments remained; none replayed. The run completed in eight decision rounds. Larger histories/participant lists can still hit the overall guard; full cursors/retrieval remain #7.

## Shipping feature and active-work restart

Run: `1144c007-bf4d-4a75-8641-6f77e793e041`. [Issue #12](https://github.com/RoySalisbury/HVO.AgentControl/issues/12), [PR #22](https://github.com/RoySalisbury/HVO.AgentControl/pull/22).

Beta Developer implemented and pushed `b8c62a3d38e7ba2c25cd2c6249bcef2edda44d7e` from baseline `401f126d11d7909c16d6a3c69a64b2a403a9243b`, assignment `17abd4ad-4041-4b5a-aa45-a6d5a24719dd`. Shipping rejects negative discounted subtotals, charges 4.95 below 50.00, and is free at/above 50.00, without rounding before the threshold comparison. Thirteen lab tests and both lab/root format checks passed. The supervising host independently ran the 13 tests and lab format; initial-head hosted CI `34088806594` passed.

While that assignment was Running, the host restarted only the web container. All six native session identities, the caller message ID `msg_07a63a0090022aaf0983c9be5b`, and delivery attempt count (one) were unchanged afterward. The native assignment continued to its pushed result. This proves this observed active-turn restart path; it does not prove all uncertain-delivery or machine-restart cases.

### Final shipping result

The run completed in twelve decision rounds with five actual worker assignments: implementation (`17abd4ad-4041-4b5a-aa45-a6d5a24719dd`), persistent checkout handoff (`d4e477e6-ec66-44d9-b39f-124d9ac001e0`), review (`1f0fab15-02f2-43f1-a5a2-52a953f3fdc1`), README correction (`466a0845-e2b2-4da7-a4e2-c0cd3221f424`), and exact-SHA re-review (`aac141c8-d608-4a07-889a-cd95278e54e6`). Final reviewed head: `10aa3c69ade97572263ba10fed44f7f755e6ef9a`, CLEAN with no remaining findings. Thirteen tests and lab format passed independently on the host; hosted CI `34089370352` passed including SSH/recovery integration. PR #22 is merged and issue #12 closed.

Completion decision: `94e05a4c-2ad8-40c1-8ebc-1d7baf553241`. The coordinator's native session compacted before returning idle; the original decision/results remained attributable, and no assignment was replayed. Its wording about the fixer advancing “that same persistent checkout” was imprecise: these are independent container filesystems with identical path strings. Developer holds its implementation head `b8c62a3`; Fixer and Reviewer hold final reviewed head `10aa3c6`. Runtime IDs distinguish them. Routing guidance now states this explicitly.

Native [initial implementation](https://github.com/RoySalisbury/HVO.AgentControl/pull/22#issuecomment-5565766136), [review](https://github.com/RoySalisbury/HVO.AgentControl/pull/22#issuecomment-5565807599), [correction](https://github.com/RoySalisbury/HVO.AgentControl/pull/22#issuecomment-5565826064), [re-review](https://github.com/RoySalisbury/HVO.AgentControl/pull/22#issuecomment-5565847784), and [completion/precision notes](https://github.com/RoySalisbury/HVO.AgentControl/pull/22#issuecomment-5565863327) are mirrored on the PR. The fixer's blanket claim that every exit status was preserved exceeded its tool evidence: some commands piped through `tail`. Independent host/CI verification, not that claim, establishes acceptance.

The [curated native evidence](beta-native-2026-09-07.json) includes command timing/identity/final reports, applied-decision journal events, restart identity checks and the operator intervention log. A command's Finished state means the native turn ended; journal events distinguish applied routing from rejected/superseded proposals. Full tool history remains in the running database.

## Interventions and prompt lessons

- Five specific read-only capability permission requests were approved once after inspecting the requested OS/container probes. No global approval bypass was enabled.
- Capability reports sometimes confused host-visible 8 CPUs/23 GiB with effective container limits. Verified limits were supplied in the shipping instruction; normalization is [#19](https://github.com/RoySalisbury/HVO.AgentControl/issues/19).
- The developer interpreted “your independent clean clone” as an instruction to create an ephemeral `/tmp/opencode/HVO.AgentControl` clone. It used reset only to materialize that new empty clone, then pushed its work. The host supplied no implementation correction. A coordinator-mediated handoff (`d4e477e6-ec66-44d9-b39f-124d9ac001e0`) safely switched the clean registered persistent checkout to the pushed branch, with matching head, clean status, and no reset. Future prompts must explicitly say to use the EXISTING registered persistent checkout and fetch before reading baseline-specific files.
- An owner follow-up correctly superseded a pending review decision, but the model treated its own old proposal as an applied dispatch. The database correctly contained no corresponding review command. A corrective owner prompt clarified actual dispatch evidence; [#23](https://github.com/RoySalisbury/HVO.AgentControl/issues/23) tracks explicit applied/superseded receipts. Routing guidance now warns about rejected/superseded proposals.
- The next response prefixed JSON with prose. The strict parser rejected the entire decision, sending no actions. A corrective prompt requiring exactly one JSON object recovered the run. Coordinator-only schema-constrained output and bounded repair are [#24](https://github.com/RoySalisbury/HVO.AgentControl/issues/24); the pinned native schema advertises support, but actual provider behavior still needs validation.
- Independent supervision noticed the shipping README still called the corrected lab an incomplete draft. The coordinator was asked to include that inconsistency in native review. This is disclosed supervisory input, not an unaided model finding.
- GitHub publication and merge remain authenticated host actions. Reports are explicitly attributed copies of real native worker output; workers do not yet autonomously post PR comments through a scoped application integration.

## Next priorities

Keep the fleet running for the owner to inspect. Prioritize #23/#24 (decision receipts/output), #19 (effective capability limits), #3/#7 (service-owned updates and bounded evidence retrieval), and #10 (clear current assignment/blocker/expected event). Enrollment and lifecycle/resource claims remain #4–#6; additional harness work is #9. Continue failure exercises in #13 before claiming unattended coordination or broad multi-harness support.
