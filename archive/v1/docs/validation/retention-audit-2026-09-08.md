# Retention and training-evidence audit — 2026-09-08

Operator measurement of deployed main `ac2dbcc371362e32d39b2e96c0d38c80c2f92f39`, OpenCode 1.18.29, on hvo-dev-03. Source was read-only; a consistent SQLite online backup was captured at 11:31 UTC and passed `quick_check`. The private capture is `.fixture/retention-audit-20260908T113109Z/`. Raw conversation/credential-bearing files are not included in the repository or uploaded for training.

## Storage observations

| Surface | Observation | Meaning |
| --- | --- | --- |
| AgentControl SQLite | 341,524,480 bytes (325.7 MiB) | Database allocated size; about 55.6 MiB was reusable free pages at the first measurement |
| AgentControl WAL | About 197 MiB at the first measurement | A changing file/high-water allocation, not another independently retained history archive |
| Commands table | About 221 MiB | Dominant operational table; requested/effective prompts and full results |
| Current Messages table | About 33 MiB | Bounded projection, not complete native history |
| Rollback backup directories | About 3.6 GiB before compression | Seventeen preserved deployment/registration snapshots |
| Entire `.fixture` | About 12 GiB before compression | Includes a 6.3 GiB active worker checkout; this is not all logs and was not deleted |
| Docker output logs | Inspected web, workers and sidecar have 10 MiB × 3 rotation limits | Native SQLite/event journals are outside that rotation |

Four active development native databases were inspected sequentially with bounded read-only queries. Their DB files total 2,847,264,768 bytes (about 2.65 GiB), including database indexes but excluding WAL, snapshots and tool-output files. The individual `event` table totals below exclude its separate indexes.

| Container | DB bytes | Native `event` table bytes | Sessions | Compaction parts observed |
| --- | ---: | ---: | ---: | ---: |
| beta-dev1 / Worker2 | 562651136 | 460906496 | 17 | 12 |
| beta-dev2 / Worker3 | 655081472 | 577265664 | 19 | 13 |
| beta-dev3 / Worker1 | 387719168 | 332664832 | 15 | 10 |
| musing_newton / Worker4 | 1241812992 | 1136472064 | 16 | 11 |

Event tables total about 2.34 GiB, approximately 88% of these DB file bytes. Actual compaction-summary records also exist. This inventory shows substantial durable history remains despite compaction; growth rate and compaction's impact over time were not measured. It does not establish that database size caused a particular OOM or measure UI/model latency. Queries observed live state sequentially, so individual counters can advance between samples. No native compaction, deletion, restart, SQL update or WAL truncation was performed.

## Retained sample and limits

The snapshot contains 1,583 commands, including 1,250 prompt commands: 819 coordinator decisions and 431 worker prompts. It also has 7,866 native usage rows, 1,754 cached transcript messages, 491 assignment records and 103 retained permission records. Current worker/session projections do not cover every historical worker or child.

The journal had 10,007 rows covering roughly 67 minutes at capture, plus exempted native process evidence. Its sequence range contains gaps. This proves why a later whole-database snapshot cannot be described as a complete event recording. Older deployment archives may preserve some earlier windows; merging those windows needs explicit deduplication and coverage accounting.

For each of the 819 coordinator prompts, that command's requested and effective text were identical; the prompts differed across commands. Median effective coordinator prompt length was 62,976 characters, 95th percentile 63,856, maximum 63,975. Worker prompt median was 2,498 characters. These are character counts, not token counts or measured provider context limits. Exact-text blob reuse and smaller current evidence are separate storage/inference improvements worth testing.

Terminal coordinator commands had a median recorded elapsed time of 15.853 seconds; terminal worker commands had a median of 281.35 seconds. Elapsed duration includes queueing, tools, CI and interruptions and cannot rank model inference performance. A conservative first-line classifier identified 15 worker CI/readiness-monitor candidates, not a complete count of low-value work.

Of 491 retained assignments, only 3 were `VerifiedComplete`; 466 were `NeedsReview`, 17 `Cancelled`, 4 `Running`, and 1 `Assigned`. Do not train a success label from `Finished` alone. Earlier coordinator deletion also removed projections after a private export; retained command/native history cannot replace missing owner attestation automatically.

## Useful setup and prompt evidence

The tool scan deduplicated parts by runtime/session/part identity and found 10,298 tool parts in retained command results. Pattern matching produced 33 missing-command candidates across 27 commands, 55 missing-file candidates, 22 permission candidates and provider/GitHub/CI candidates. These are triage leads: tool output frequently quotes test cases, source code or prior reports. Counts are not confirmed incident rates.

Directly inspected small tool records include:

- Command `38d11d62-6f61-47e3-aeb1-b1c67eceb949`: separate `npm` and `node` invocations returned exit 127 with missing-command diagnostics. Native tool status was `completed`. Record exit evidence and verify Node/npm under the actual task/child user and PATH; template file presence alone is insufficient.
- Command `ca572407-e7ee-4b52-bbe6-c92db0597830`: the `file` utility invocation returned exit 127. Consider this diagnostic utility in the baseline profile when recurring tasks require it.
- Docker/Python diagnostic candidates include compound shell commands returning zero despite an earlier missing command. Treat per-tool capability results separately from the last shell exit; Docker availability must match the selected profile rather than granting every worker the host daemon.
- Earlier audited Node discovery/permission churn is documented in [#75](https://github.com/RoySalisbury/HVO.AgentControl/issues/75#issuecomment-5574555119). Prime verified paths and capabilities in child tasks, and replace recurring ad hoc installation with reviewed image changes.
- GitHub App self-approval is an actor/permission limitation, not missing `gh`. Route a credential-distinct reviewer and preserve the independent review receipt; repeatedly assigning another worker with the same App cannot change the identity. Root and native review receipts already record this failure pattern.
- The sidecar's first ambiguous canary copied controller-only instructions into worker text; the second delimited JSON-string canary executed the exact one-command task once. Preserve both examples with the same model/environment and explicit boundary change; see [#140](https://github.com/RoySalisbury/HVO.AgentControl/issues/140).
- The stale merge-hold incident showed current owner policy being overridden by repeated historical reports despite a healthy heartbeat. Replacing the checkpoint restored implementation assignment. Preserve it as a policy-provenance/prompt regression, not a timer failure; see [#78](https://github.com/RoySalisbury/HVO.AgentControl/issues/78#issuecomment-5584259459).
- The provider-cache failure and confirmed idle reload belong in provisioning-readiness examples. Catalog visibility did not establish usable auth; see [#84](https://github.com/RoySalisbury/HVO.AgentControl/issues/84#issuecomment-5583659341).

The derived `command-features.jsonl` contains IDs, times, prompt fingerprints/lengths, model observations, delivery state and candidate categories. `candidate-index.json` links diagnostic candidates to native parts and output hashes. Neither contains prompt text, raw tool input/output or reasoning text. This supports reproducible private review without publishing the raw archive. Provider cost fields remain provider-reported values; subscription costs/quota and comparable task quality were not established.

## Archive action and restore evidence

Seventeen sealed rollback directories were compressed separately as private `.tar.zst` archives under `.fixture/archives/rollback/`, with per-file SHA-256, size/mode and archive hashes. Every file was read back and compared before original removal. The removal pass also extracted each eligible archive and checked restored SQLite consistency. Source inventories were rechecked for changes; active data, worker checkouts and native volumes were excluded.

The 17 original directories contained 3,818,378,744 bytes. Their compressed archives occupy 392,757,607 bytes. One recent known-good rollback, `before-control-service-20260908T102417Z`, remains unpacked (355,400,213 bytes). Removing the other 16 verified redundant copies reduced logical file bytes by a net 3,070,220,924 bytes (about 2.86 GiB), accounting for the new archives. This calculation uses file lengths, not filesystem allocated blocks or a controlled free-space measurement. Every snapshot's contents remain archived, including the former coordinator's cached history/assignment export. Exact totals and restore receipts are in `archive-summary.json` in the private audit directory. This is same-host compression, not an off-host disaster-recovery copy or an automatic expiry service.

The former coordinator export can now be restored from `.fixture/archives/rollback/legacy-coordinator-registration-20260908T112154Z.tar.zst`; its manifest records the original path. Native stopped-container volumes still remain. Never restore a historical DB over the live database merely to inspect training examples.

Follow [the retention policy](../RETENTION_AND_EVALUATION.md) for the implementation sequence. Automatic archive APIs, guarded payload pruning, complete native export, context-budget UI/compaction controls and fresh-task session dispatch remain explicit follow-up work. The audit does not claim those features are deployed.
