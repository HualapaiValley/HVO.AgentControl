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
  actual wall-clock test execution before the batch moves on. An iteration
  already started when the batch target is reached is allowed to complete, as
  long as it fits inside the remaining overall deadline.
- Stops on the first failing outcome and its batch.
- Hard upper guard `SOAK_MAX_TOTAL_SECONDS` aborts the whole run regardless
  of batch progress.
- Each invocation writes to a UNIQUE timestamped evidence directory
  `artifacts/coordination-soak-<UTC timestamp>-<pid>`; separate invocations
  never truncate or reuse directories or files, and all artifacts including
  failure logs are preserved. `SOAK_ARTIFACT_ROOT` overrides the base path.
- The build step has its own hard `SOAK_BUILD_SECONDS` timeout bounded by
  `min(build timeout, overall remaining)`, every test iteration is bounded by
  the remaining OVERALL duration through `timeout` (which returns 124 on
  timeout), and the whole run has the overall guard.
- The runner fails closed: an iteration is a clean pass only when dotnet
  exits `0`, a test summary is present, `Total` is nonzero, `Passed` is
  equal to `Total`, and `Failed` is zero. A mixture of passed and skipped tests also fails. Per-iteration outcome is recorded explicitly
  as one of `success`, `failure`, `zero-tests`, `skipped-only`,
  `incomplete-tests`, `missing-evidence`, or `timeout`; every non-success outcome terminates the
  run with a nonzero exit. A batch that cannot complete its target inside the
  remaining overall deadline, and a failed evidence validation (validator
  failure or any invalid line), also terminate the run with a nonzero exit.
- Every evidence line is ONE whole, valid JSON object. The produced
  `evidence.jsonl` is validated line-by-line at the end (via `python3`
  `json.loads`, or `jq -e .` when python3 is unavailable), and the valid /
  total line counts are recorded in an `evidence_validated` event.
- `set -u` only, no `set -e`, so the probing loop can observe each command's
  exit code directly; every command exit status is preserved, never piped
  through a hiding pipeline.

Defaults: 4 batches x 120 s, hard guard 900 s, build timeout 300 s,
`Release` configuration, artifacts under `artifacts/coordination-soak-<run tag>`.

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

Each run produces its own evidence directory, so repeated or concurrent
invocations never clobber one another:

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
## Runner regression checks

Run `python3 tests/test_soak_runner.py scripts/run-coordination-soak.sh`. These isolated probes use a stub `dotnet` to exercise runner deadlines, partial/all-skipped tests, missing/zero-test evidence and validator failures. They do not substitute for the real .NET suite or the long soak. CI runs them separately.
