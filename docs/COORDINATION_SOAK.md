# Coordination soak

## Purpose

`tests/HVO.AgentControl.Tests/CoordinationStressTests.cs` is a small,
deterministic, Docker-free suite that stresses the coordination and
persistence paths of the `ControlStore` without needing live runtimes,
model providers, or SSH fixtures. It is meant to be run locally and simple
enough that CI can run it exactly once.

`scripts/run-coordination-soak.sh` is an opt-in, duration-bounded runner
that repeatedly executes that focused suite so the same stress scenarios
run many times under one sustained pass. It is never invoked by CI.

## Stress scenarios

| Test | What it verifies |
| --- | --- |
| `BatchedFanOutDispatchesEveryTargetExactlyOnce` | A single coordinator decision fanning out to 8 workers produces exactly one queued prompt per target, with a full batch and a distinct `workerId` set. |
| `DuplicateRequestIdsReturnTheSameRecordAndConflictsAreRejected` | Re-issuing the same request ID is at-most-once idempotent; reusing it for different content or routing is rejected. |
| `BusyWorkerWithQueuedWorkExcludesTargetWithoutPartialDispatch` | A worker with queued work is excluded from a fan-out batch; the whole batch is rejected atomically, with no partial dispatch. |
| `NonIdleOrStaleWorkerExcludesTargetWithoutDispatch` | An active or stale target excludes the batch and no command is dispatched. |
| `DurableStateAndRequestIdempotencySurviveRestartAndRecovery` | Coordination state, dispatched commands, and request-id idempotency survive an app restart; recovery turns in-flight work into unknown state and marks workers stale without losing records. |
| `StaleObservationRejectsWholeBatchWithoutPartialFanOut` | A decision based on a stale worker revision is rejected as one atomic batch with no partial fan-out. |

## Runner contract

- Runs only `FullyQualifiedName~CoordinationStressTests` on
  `HVO.AgentControl.Tests`, building once first.
- `SOAK_BATCHES` batches, each accumulating at least `SOAK_BATCH_SECONDS` of
  actual `dotnet test` execution before the batch moves on.
- Stops on the first failing (non-zero) iteration and its batch.
- Hard upper guard `SOAK_MAX_TOTAL_SECONDS` aborts the whole run regardless
  of batch progress.
- Writes machine-readable evidence to
  `artifacts/coordination-soak/evidence.jsonl`: per-iteration elapsed time,
  exit status, and `Passed/Failed/Total` counts, plus batch and run markers.
- Per-suite timeout is bounded to the remaining duration with a `timeout`
  kill so no single iteration can run away.
- `set -u` only, no `set -e`, so the probing loop can observe each command's
  exit code directly; every command exit status is preserved, never piped
  through a hiding pipeline.

Defaults: 4 batches x 120 s, hard guard 900 s, `Release` configuration,
artifacts in `artifacts/coordination-soak`.

## Usage

```bash
# Focused single run, as CI would (one pass, no eighty-minute loop)
dotnet test tests/HVO.AgentControl.Tests/HVO.AgentControl.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~CoordinationStressTests

# Opt-in soak anywhere on the same machine/container
bash scripts/run-coordination-soak.sh

# Tune the defaults
SOAK_BATCHES=4 SOAK_BATCH_SECONDS=120 \
  bash scripts/run-coordination-soak.sh
```

## CI behavior

The GitHub Actions workflow runs the ordinary solution test step, which
includes the stress suite once. The eight-plus-minute soak loop is opt-in
and never runs in CI.

## Constraints honored

- No Docker required: the suite drives `TestApp`/`ControlStore` over the
  file-backed SQLite replica in a temp directory.
- Bounded resources: the suite creates one `TestApp` web host per test, a
  handful of workers, and a strict command count; no uncontrolled fan-out.
- Bounded time: every iteration is duration-capped and the whole run has a
  hard upper guard.
- The runner and all artifacts stay inside the repository working tree.