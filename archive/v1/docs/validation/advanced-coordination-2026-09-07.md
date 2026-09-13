# Advanced coordination and worker GitHub publication — 2026-09-07

## Result

Coordination `c4f3b2ee-d2fb-4310-8afc-723b0a6a6cb7` completed at round 12 with five worker assignments, a maximum of three concurrently active assignments and one recorded delivery attempt per assignment. All participants used `opencode/big-pickle`; the separate coordinator routed work and never became an execution recipient. This was supervised verification, not an unattended-readiness claim.

The earlier advanced run `3bb176bc-7f67-4ea1-b406-904821980cd9` remains stopped with its history preserved. Its instruction had reached the 16000-character ceiling and its final response was malformed; it was not relabelled completed. The final run used a fresh bounded instruction and the existing native sessions.

## Work and review gates

- **PR36**, head `7d99b41b3f5705f9f6ebbeb80c9fac27eeb7ce0e`: supervising agent reproduced a partial-skipped false success that the earlier worker verification missed, fixed it, and committed the eight Python runner regression probes to CI. Beta Developer independently reviewed the exact head, ran the probes and seven focused .NET cases, and returned CLEAN. Beta Fixer ran the actual foreground soak. Merged as `36d8feb127db627734e6ab0d802b75c6f884d723`.
- **PR39**, final head `d19827a340645aa869557b98fc78077452012b7f`: eight committed status cases cover permission/question precedence, simultaneous prompts, uncertain delivery during active work, unrelated queued work, historical failures and native cancellation. The managed App worker posted a static review with findings; root fixed the actionable findings and ran the tests; the same reviewer checked the new head and posted a CLEAN follow-up with an explicit no-test-execution limitation. Both reviews remain on [PR39](https://github.com/RoySalisbury/HVO.AgentControl/pull/39). Root also kept routing detail visible outside collapsed history.
- **PR47**, head `d793d0c0ad8cbf2b745bf57c386bd7f6c3bb5b3b`: after its review task finished, the coordinator assigned the now-idle Devcontainer Test Worker the two-file installation-help patch. It verified a clean checkout, configured GitHub CLI's HTTPS helper in its dedicated runtime, fetched main, applied the patch, ran restore/build/format, pushed its branch and created [PR47](https://github.com/RoySalisbury/HVO.AgentControl/pull/47) as `hvo-agentcontrol[bot]`. Root independently reviewed the exact diff and merged it after CI.

The App access also passed a prior issue read/write smoke: [issue44 comment 5568557813](https://github.com/RoySalisbury/HVO.AgentControl/issues/44#issuecomment-5568557813). The App private key remains encrypted in the control host. Workers receive expiring repository-scoped installation credentials, not the private key. No host-owner GitHub token was copied to the worker.

## Soak evidence

Root copied and independently parsed the JSONL plus every real iteration log from Beta Fixer:

- 4 batches: 120088, 124241, 121612 and 121436 ms.
- 97 iterations × 7 tests = **679 passing test executions**.
- 0 failed, skipped, zero-test, missing-evidence or timeout outcomes; final exit 0.
- Total including build: 495930 ms.
- Native artifact directory: `/home/agent/workspaces/HVO.AgentControl/artifacts/coordination-soak-20260907T093948Z-40831`.
- Retained supervising copy: `.fixture/final-coordination-soak/`.
- `evidence.jsonl` SHA-256: `3ad16e4494836c7a35401d1fc5e324e2a5e3e593a5e4c59e7065367c66dbd18b`.

The runner exercises deterministic store invariants repeatedly; this is not a provider throughput benchmark. It uses GNU date/timeout on Linux. Its cosmetic `blockedByDeadline` batch flag does not cover every overall-guard exit; the final nonzero exit is authoritative for failure. Signal cleanup of a detached/background runner is not claimed: the accepted soak ran in the foreground.

## Validation and interventions

Required restore, Release build with warnings as errors, and format verification passed. The combined current-main candidate with PR36, PR39 and PR47 passed **100 tests, 2 optional live-provider tests skipped**, including actual SSH fixtures and an installed GitHub CLI credential-format check. Each PR also passed hosted CI. Initial supervising worktree SSH runs failed because fixture profiles/keys were not present there; after copying only disposable fixture credentials into that worktree, the combined run passed. Those setup failures were not hidden as product successes.

Explicit supervising interventions:

1. Fixed the partial-skipped runner defect and the two actionable UI review findings, and independently reran checks.
2. Approved one native external-directory read of the supplied `/tmp/github-installation-guidance.patch`, once. The coordinator did not grant tool permission.
3. Corrected unsupported wording in PR47's generated description; the worker's code diff was the reviewed patch.
4. The coordinator emitted malformed JSON at the final summary in round 11. The application paused and dispatched nothing from it. One owner correction with the completion schema produced valid completion in round 12. Bounded structured-output/repair remains #24; it is not implemented by this intervention.

Root's retained command/receipt export is `.fixture/final-coordination-evidence.json`. It records assignment IDs, timestamps, final worker reports, applied decision receipts and the interventions. No second delivery attempt was recorded for any of the five assignments. The owner Mac and original demo worker were not assigned work.

## Observed model usage

These are completed assistant-message counters retained in command results, deduplicated by runtime/session/message. They exclude supervising Codex usage and any history outside the retained evidence. Cost is the provider's reported value, not an estimated or all-time billing ledger. Cache reads are shown separately.

| Worker | Completed messages | Input tokens | Output tokens | Cache-read tokens | Reported cost |
| --- | ---: | ---: | ---: | ---: | ---: |
| AgentControl coordinator | 13 | 434597 | 15535 | 459264 | 0 |
| Beta Developer | 21 | 132647 | 14432 | 1259520 | 0 |
| Devcontainer Test Worker | 54 | 111415 | 23711 | 2227712 | 0 |
| Beta Fixer | 11 | 183303 | 4308 | 953600 | 0 |

A durable usage ledger/report UI remains #34. See [observability design](../OBSERVABILITY_DESIGN.md) for final/provisional counters, replay-safe updates and telemetry requirements.

## Deployment

The web container was rebuilt from merged main `bb0e7d4` and refreshed at the existing owner URL. All seven native session identities were unchanged, all seven runtimes returned Healthy, and the App grant remained Ready. Read-only desktop/mobile browser checks passed: three current participants, collapsed history, visible run detail, updated GitHub setup instructions and no page errors or horizontal mobile overflow. Existing model sessions and the M4 were not restarted.
