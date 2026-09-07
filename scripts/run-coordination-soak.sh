#!/usr/bin/env bash
# Opt-in bounded soak runner for the coordination stress suite.
#
# CoordinationStressTests (tests/HVO.AgentControl.Tests) is a small,
# deterministic, Docker-free suite. This script repeatedly runs only that
# suite (FullyQualifiedName~CoordinationStressTests) for several bounded
# batches so the scheduling and persistence paths are exercised over many
# iterations. It stops on the first failing outcome and is protected by a
# hard upper duration guard.
#
# CI never invokes this script: the workflow runs the same tests exactly
# once through `dotnet test HVO.AgentControl.slnx`. Running this file is the
# opt-in step.
#
# Hardening guarantees (see docs/COORDINATION_SOAK.md):
#   * Each invocation writes to a UNIQUE timestamped evidence directory;
#     separate invocations never truncate or reuse directories or files, and
#     all artifacts including failure logs are preserved.
#   * Build and each test iteration have their own hard timeouts, the build
#     duration is bounded by min(build timeout, overall deadline), every
#     iteration may complete past the batch target as long as it fits inside
#     the remaining overall deadline, and the whole run has a hard upper
#     duration guard.
#   * The runner FAILS CLOSED on missing/invalid/failed/skipped/timeout
#     evidence: an iteration counts as a clean pass only when dotnet exits 0,
#     the summary is present, Total is nonzero, Passed is nonzero and Failed
#     is zero. Zero-test, all-skipped, missing-summary, timeout and test
#     failure outcomes terminate the run with a nonzero exit, as does a batch
#     that cannot complete its target inside the remaining overall deadline
#     and a failed evidence validation.
#   * Every evidence line is one whole, valid JSON object; the produced
#     evidence file is validated line-by-line at the end, and a validator
#     failure itself fails the run.
#
# Tuning (env): SOAK_BATCHES (4), SOAK_BATCH_SECONDS (120),
#   SOAK_MAX_TOTAL_SECONDS (900), SOAK_BUILD_SECONDS (300),
#   SOAK_CONFIGURATION (Release), SOAK_ARTIFACT_ROOT (artifacts).
set -u

cd "$(dirname "$0")/.." || exit 2
repo_root="$(pwd)"

command -v dotnet >/dev/null 2>&1 || { echo "dotnet is required; refusing to run the soak." >&2; exit 2; }

batch_count="${SOAK_BATCHES:-4}"
batch_seconds="${SOAK_BATCH_SECONDS:-120}"
max_total_seconds="${SOAK_MAX_TOTAL_SECONDS:-900}"
build_seconds="${SOAK_BUILD_SECONDS:-300}"
configuration="${SOAK_CONFIGURATION:-Release}"
project="tests/HVO.AgentControl.Tests/HVO.AgentControl.Tests.csproj"
filter="FullyQualifiedName~CoordinationStressTests"
artifact_root="${SOAK_ARTIFACT_ROOT:-$repo_root/artifacts}"
run_tag="$(date -u +%Y%m%dT%H%M%SZ)-$$"
artifact_dir="$artifact_root/coordination-soak-$run_tag"
evidence_path="$artifact_dir/evidence.jsonl"

ok=0
bad_arg=1
failed_run=2
aborted=3

case "$batch_count" in
  *[!0-9]*|''|0) echo "SOAK_BATCHES must be a positive integer." >&2; exit "$bad_arg";;
esac
case "$batch_seconds" in
  *[!0-9]*|''|0) echo "SOAK_BATCH_SECONDS must be a positive integer." >&2; exit "$bad_arg";;
esac
case "$max_total_seconds" in
  *[!0-9]*|''|0) echo "SOAK_MAX_TOTAL_SECONDS must be a positive integer." >&2; exit "$bad_arg";;
esac
case "$build_seconds" in
  *[!0-9]*|''|0) echo "SOAK_BUILD_SECONDS must be a positive integer." >&2; exit "$bad_arg";;
esac
min_total=$(( batch_count * batch_seconds ))
if (( max_total_seconds <= min_total )); then
  echo "SOAK_MAX_TOTAL_SECONDS ($max_total_seconds) must exceed batch_count*batch_seconds ($min_total) so every batch can complete." >&2
  exit "$bad_arg"
fi

mkdir -p "$artifact_dir"

now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }
ms_since() { echo "$(( ( $(date +%s%N) - $1 ) / 1000000 ))"; }

write_event() {
  local json="$1"
  printf '%s\n' "$json" >> "$evidence_path"
}

# Each event must be ONE whole JSON object on ONE line (valid JSONL).
iteration_event() {
  local batch="$1" iteration="$2" outcome="$3" status="$4" elapsed_ms="$5" passed="$6" failed="$7" total="$8"
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"iteration\",\"batch\":$batch,\"iteration\":$iteration,\"outcome\":\"$outcome\",\"exit\":$status,\"elapsedMs\":$elapsed_ms,\"passed\":$passed,\"failed\":$failed,\"total\":$total}"
}

# Validate every non-empty evidence line is a whole, valid JSON object.
# Returns 0 only when a validator ran AND every line was valid JSON.
verify_evidence_jsonl() {
  local lines=0 valid=0 invalid=0
  if command -v python3 >/dev/null 2>&1; then
    local result python_status
    result=$(python3 -c '
import sys, json
lines = valid = invalid = 0
with open(sys.argv[1], encoding="utf-8") as f:
    for raw in f:
        line = raw.strip()
        if not line:
            continue
        lines += 1
        try:
            json.loads(line)
            valid += 1
        except Exception:
            invalid += 1
print(lines, valid, invalid)
' "$evidence_path" 2>&1)
    python_status=$?
    if (( python_status != 0 )); then
      echo "Evidence validation failed: python3 exited $python_status." >&2
      return 2
    fi
    read -r lines valid invalid <<< "$result"
  elif command -v jq >/dev/null 2>&1; then
    local jq_status
    jq -e '.' "$evidence_path" >/dev/null 2>&1
    jq_status=$?
    if (( jq_status != 0 )); then
      echo "Evidence validation failed: jq exited $jq_status." >&2
      return 2
    fi
    lines=$(grep -c -v '^[[:space:]]*$' "$evidence_path" || true)
    valid=$lines
  else
    echo "No JSON validator (python3 or jq) available; cannot verify evidence." >&2
    return 2
  fi
  echo "$lines $valid $invalid"
}

printf '' > "$evidence_path"
echo "Coordination soak starting: $batch_count batch(es) x $batch_seconds s, hard guard $max_total_seconds s."
echo "Artifacts (unique per run): $artifact_dir"
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_start\",\"runTag\":\"$run_tag\",\"batches\":$batch_count,\"batchSeconds\":$batch_seconds,\"maxTotalSeconds\":$max_total_seconds,\"buildSeconds\":$build_seconds,\"filter\":\"$filter\"}"

run_started=$(date +%s%N)
build_limit_s=$build_seconds
if (( max_total_seconds < build_seconds )); then
  build_limit_s=$max_total_seconds
fi
echo "Building $project ($configuration) once with a ${build_limit_s}s hard timeout (bounded by the overall ${max_total_seconds}s guard)."
build_log="$artifact_dir/build.log"
timeout --kill-after=10 "$build_limit_s" \
  dotnet build "$project" --configuration "$configuration" >"$build_log" 2>&1
build_status=$?
if (( build_status != 0 )); then
  echo "Build failed (exit=$build_status); see $build_log." >&2
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_end\",\"exit\":$failed_run,\"phase\":\"build\",\"buildExit\":$build_status}"
  exit "$failed_run"
fi

total_iterations=0
total_passed=0
total_failed=0
total_zero_tests=0
total_timeouts=0
total_skipped_only=0
total_missing_evidence=0
run_exit=$ok

for batch in $(seq 1 "$batch_count"); do
  batch_started=$(date +%s%N)
  batch_iterations=0
  batch_passed=0
  batch_failed=0
  batch_zero_tests=0
  batch_timeouts=0
  batch_skipped_only=0
  batch_missing_evidence=0
  batch_blocked=0
  echo "[batch $batch/$batch_count] starting (target ${batch_seconds}s of test execution)."
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"batch_start\",\"batch\":$batch}"

  while true; do
    now_ms=$(date +%s%N)
    overall_elapsed=$(( ( now_ms - run_started ) / 1000000 ))
    batch_elapsed=$(( ( now_ms - batch_started ) / 1000000 ))
    if (( overall_elapsed >= max_total_seconds * 1000 )); then
      echo "[batch $batch] ABORTED: hard overall duration guard (${max_total_seconds}s) reached." >&2
      run_exit=$aborted
      break
    fi
    if (( batch_elapsed >= batch_seconds * 1000 )); then
      break
    fi
    remaining_batch_ms=$(( batch_seconds * 1000 - batch_elapsed ))
    remaining_overall_ms=$(( max_total_seconds * 1000 - overall_elapsed ))
    if (( remaining_batch_ms >= remaining_overall_ms )); then
      echo "[batch $batch] CANNOT COMPLETE: remaining batch (${remaining_batch_ms}ms) exceeds the overall deadline remaining (${remaining_overall_ms}ms); failing closed." >&2
      run_exit=$failed_run
      batch_blocked=1
      break
    fi
    # The per-iteration timeout is bounded by the remaining OVERALL deadline,
    # so an iteration already started when the batch target is reached may
    # complete as long as it fits inside the overall guard.
    kill_after_s=$(( remaining_overall_ms / 1000 ))
    if (( kill_after_s < 1 )); then kill_after_s=1; fi

    batch_iterations=$(( batch_iterations + 1 ))
    iteration=$(printf '%03d' "$batch_iterations")
    total_iterations=$(( total_iterations + 1 ))
    iter_started=$(date +%s%N)
    logfile="$artifact_dir/batch${batch}-iter${iteration}.log"
    # Plain timeout returns 124 on timeout; a real test failure keeps dotnet's
    # own (non-124) exit code, so the outcome is unambiguous.
    timeout --kill-after=5 "$kill_after_s" \
      dotnet test "$project" --configuration "$configuration" --no-restore --no-build --filter "$filter" \
      >"$logfile" 2>&1
    status=$?
    iter_elapsed=$(( ( $(date +%s%N) - iter_started ) / 1000000 ))

    summary_line=$(grep -a -E 'Failed:|Passed:' "$logfile" | grep -a -E 'Total:' | tail -n 1)
    failed=0
    passed=0
    total=0
    if [ -n "$summary_line" ]; then
      failed=$(printf '%s' "$summary_line" | sed -nE 's/.*Failed:[[:space:]]*([0-9]+).*/\1/p')
      passed=$(printf '%s' "$summary_line" | sed -nE 's/.*Passed:[[:space:]]*([0-9]+).*/\1/p')
      total=$(printf '%s' "$summary_line" | sed -nE 's/.*Total:[[:space:]]*([0-9]+).*/\1/p')
    fi
    failed=${failed:-0}; passed=${passed:-0}; total=${total:-0}

    if (( status == 124 )); then
      batch_timeouts=$(( batch_timeouts + 1 ))
      total_timeouts=$(( total_timeouts + 1 ))
      iteration_event "$batch" "$batch_iterations" "timeout" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations TIMEOUT (exit=$status) in ${iter_elapsed}ms; failing closed." >&2
      run_exit=$failed_run
      break
    elif [ -z "$summary_line" ]; then
      batch_missing_evidence=$(( batch_missing_evidence + 1 ))
      total_missing_evidence=$(( total_missing_evidence + 1 ))
      iteration_event "$batch" "$batch_iterations" "missing-evidence" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations MISSING-EVIDENCE (exit=$status, no test summary) in ${iter_elapsed}ms; failing closed." >&2
      run_exit=$failed_run
      break
    elif (( total == 0 )); then
      batch_zero_tests=$(( batch_zero_tests + 1 ))
      total_zero_tests=$(( total_zero_tests + 1 ))
      iteration_event "$batch" "$batch_iterations" "zero-tests" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations ZERO-TESTS (exit=$status, total=0) in ${iter_elapsed}ms; failing closed." >&2
      run_exit=$failed_run
      break
    elif (( passed == 0 )); then
      batch_skipped_only=$(( batch_skipped_only + 1 ))
      total_skipped_only=$(( total_skipped_only + 1 ))
      iteration_event "$batch" "$batch_iterations" "skipped-only" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations SKIPPED-ONLY (exit=$status, passed=0, total=$total) in ${iter_elapsed}ms; failing closed." >&2
      run_exit=$failed_run
      break
    elif (( status != 0 || failed > 0 )); then
      batch_failed=$(( batch_failed + failed ))
      total_failed=$(( total_failed + failed ))
      iteration_event "$batch" "$batch_iterations" "failure" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations FAILED (exit=$status, failed=$failed) in ${iter_elapsed}ms; stop requested." >&2
      run_exit=$failed_run
      break
    else
      batch_passed=$(( batch_passed + passed ))
      total_passed=$(( total_passed + passed ))
      iteration_event "$batch" "$batch_iterations" "success" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations ok in ${iter_elapsed}ms (tests=$total, failed=$failed)."
    fi
  done

  batch_elapsed_ms=$(ms_since "$batch_started")
  echo "[batch $batch/$batch_count] done: $batch_iterations iteration(s), ${batch_elapsed_ms}ms elapsed, passed=$batch_passed, failed=$batch_failed, zero-tests=$batch_zero_tests, timeouts=$batch_timeouts, skipped-only=$batch_skipped_only, missing-evidence=$batch_missing_evidence."
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"batch_end\",\"batch\":$batch,\"iterations\":$batch_iterations,\"elapsedMs\":$batch_elapsed_ms,\"passed\":$batch_passed,\"failed\":$batch_failed,\"zeroTests\":$batch_zero_tests,\"timeouts\":$batch_timeouts,\"skippedOnly\":$batch_skipped_only,\"missingEvidence\":$batch_missing_evidence,\"blockedByDeadline\":$batch_blocked}"
  if (( run_exit != ok )); then
    break
  fi
done

total_elapsed_ms=$(ms_since "$run_started")
echo "Coordination soak complete: $total_iterations iteration(s), ${total_elapsed_ms}ms total, passed=$total_passed, failed=$total_failed, zero-tests=$total_zero_tests, timeouts=$total_timeouts, skipped-only=$total_skipped_only, missing-evidence=$total_missing_evidence, exit=$run_exit."

verify_output="$(verify_evidence_jsonl)"
verify_status=$?
read -r ev_lines ev_valid ev_invalid <<< "$verify_output"
ev_valid=${ev_valid:-0}; ev_invalid=${ev_invalid:-0}
if (( verify_status != 0 )); then
  echo "Evidence validation FAILED; failing the run closed." >&2
  ev_valid=0; ev_invalid=1; ev_lines=0
  run_exit=$failed_run
else
  if (( ev_invalid > 0 )); then
    echo "WARNING: evidence validation found $ev_invalid invalid JSON line(s) of $ev_lines." >&2
    run_exit=$failed_run
  else
    echo "Evidence JSON validation passed: $ev_valid/$ev_lines lines are valid JSON objects."
  fi
fi
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"evidence_validated\",\"lines\":$ev_lines,\"valid\":$ev_valid,\"invalid\":$ev_invalid}"
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_end\",\"exit\":$run_exit,\"runTag\":\"$run_tag\",\"iterations\":$total_iterations,\"totalElapsedMs\":$total_elapsed_ms,\"passed\":$total_passed,\"failed\":$total_failed,\"zeroTests\":$total_zero_tests,\"timeouts\":$total_timeouts,\"skippedOnly\":$total_skipped_only,\"missingEvidence\":$total_missing_evidence}"
echo "Evidence: $evidence_path"
exit "$run_exit"