#!/usr/bin/env bash
# Opt-in bounded soak runner for the coordination stress suite.
#
# CoordinationStressTests (tests/HVO.AgentControl.Tests) is a small,
# deterministic, Docker-free suite. This script repeatedly runs only that
# suite (FullyQualifiedName~CoordinationStressTests) for several bounded
# batches so the scheduling and persistence paths are exercised over many
# iterations. It stops on the first failing iteration and is protected by a
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
#   * Build and each test step have their own hard timeouts, and the whole
#     run has a hard upper duration guard.
#   * A passing count is only a clean pass with a NONZERO expected test
#     count. Zero-test iterations (e.g. a marginal iteration killed by the
#     timeout against remaining batch time) are recorded as zero-tests and
#     never counted green.
#   * Every evidence line is one whole, valid JSON object; the produced
#     evidence file is validated line-by-line at the end.
#
# Tuning (env): SOAK_BATCHES (4), SOAK_BATCH_SECONDS (120),
#   SOAK_MAX_TOTAL_SECONDS (900), SOAK_BUILD_SECONDS (300),
#   SOAK_ITERATION_MIN_SECONDS (10), SOAK_CONFIGURATION (Release),
#   SOAK_ARTIFACT_ROOT (artifacts).
set -u

cd "$(dirname "$0")/.." || exit 2
repo_root="$(pwd)"

command -v dotnet >/dev/null 2>&1 || { echo "dotnet is required; refusing to run the soak." >&2; exit 2; }

batch_count="${SOAK_BATCHES:-4}"
batch_seconds="${SOAK_BATCH_SECONDS:-120}"
max_total_seconds="${SOAK_MAX_TOTAL_SECONDS:-900}"
build_seconds="${SOAK_BUILD_SECONDS:-300}"
iteration_min_seconds="${SOAK_ITERATION_MIN_SECONDS:-10}"
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
case "$iteration_min_seconds" in
  *[!0-9]*|''|0) echo "SOAK_ITERATION_MIN_SECONDS must be a positive integer." >&2; exit "$bad_arg";;
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
verify_evidence_jsonl() {
  local lines=0 valid=0 invalid=0
  local line
  while IFS= read -r line || [ -n "$line" ]; do
    if [ -z "$line" ]; then continue; fi
    lines=$(( lines + 1 ))
    if command -v python3 >/dev/null 2>&1; then
      if printf '%s\n' "$line" | python3 -c 'import sys,json; json.loads(sys.stdin.read())' 2>/dev/null; then
        valid=$(( valid + 1 )); else invalid=$(( invalid + 1 )); fi
    elif command -v jq >/dev/null 2>&1; then
      if printf '%s\n' "$line" | jq -e . >/dev/null 2>&1; then
        valid=$(( valid + 1 )); else invalid=$(( invalid + 1 )); fi
    else
      echo "No JSON validator (python3 or jq) available; cannot verify evidence." >&2
      return 2
    fi
  done < "$evidence_path"
  echo "$lines $valid $invalid"
}

printf '' > "$evidence_path"
echo "Coordination soak starting: $batch_count batch(es) x $batch_seconds s, hard guard $max_total_seconds s."
echo "Artifacts (unique per run): $artifact_dir"
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_start\",\"runTag\":\"$run_tag\",\"batches\":$batch_count,\"batchSeconds\":$batch_seconds,\"maxTotalSeconds\":$max_total_seconds,\"buildSeconds\":$build_seconds,\"filter\":\"$filter\"}"

run_started=$(date +%s%N)
echo "Building $project ($configuration) once with a ${build_seconds}s hard timeout."
build_log="$artifact_dir/build.log"
timeout --kill-after=10 "$build_seconds" \
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
run_exit=$ok

for batch in $(seq 1 "$batch_count"); do
  batch_started=$(date +%s%N)
  batch_iterations=0
  batch_passed=0
  batch_failed=0
  batch_zero_tests=0
  batch_timeouts=0
  echo "[batch $batch/$batch_count] starting (target ${batch_seconds}s of test execution)."
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"batch_start\",\"batch\":$batch}"

  while true; do
    now_ms=$(date +%s%N)
    overall_elapsed=$(( ( now_ms - run_started ) / 1000000 ))
    batch_elapsed=$(( ( now_ms - batch_started ) / 1000000 ))
    if (( overall_elapsed >= max_total_seconds * 1000 )); then
      echo "[batch $batch] ABORTED: hard overall duration guard (${max_total_seconds}s) reached." >&2
      run_exit=$aborted
      break 2
    fi
    if (( batch_elapsed >= batch_seconds * 1000 )); then
      break
    fi
    remaining_batch_ms=$(( batch_seconds * 1000 - batch_elapsed ))
    remaining_overall_ms=$(( max_total_seconds * 1000 - overall_elapsed ))
    if (( remaining_batch_ms > remaining_overall_ms )); then
      break
    fi
    # Do not start an iteration that cannot fit a meaningful run before the
    # batch target; avoids marginal zero-test garbage iterations each batch.
    if (( remaining_batch_ms < iteration_min_seconds * 1000 )); then
      break
    fi
    kill_after_s=$(( remaining_batch_ms / 1000 ))

    batch_iterations=$(( batch_iterations + 1 ))
    iteration=$(printf '%03d' "$batch_iterations")
    iter_started=$(date +%s%N)
    logfile="$artifact_dir/batch${batch}-iter${iteration}.log"
    # Hard per-iteration timeout bounded by the remaining batch duration.
    # Plain timeout returns 124 on timeout; a real test failure keeps dotnet's
    # own (non-124) exit code, so the outcome is unambiguous.
    timeout --kill-after=5 "$kill_after_s" \
      dotnet test "$project" --configuration "$configuration" --no-restore --no-build --filter "$filter" \
      >"$logfile" 2>&1
    status=$?
    iter_elapsed=$(( ( $(date +%s%N) - iter_started ) / 1000000 ))

    summary_line=$(grep -a -E 'Failed:|Passed:' "$logfile" | grep -a -E 'Total:' | tail -n 1)
    failed=$(printf '%s' "$summary_line" | sed -nE 's/.*Failed:[[:space:]]*([0-9]+).*/\1/p')
    passed=$(printf '%s' "$summary_line" | sed -nE 's/.*Passed:[[:space:]]*([0-9]+).*/\1/p')
    total=$(printf '%s' "$summary_line" | sed -nE 's/.*Total:[[:space:]]*([0-9]+).*/\1/p')
    failed=${failed:-0}; passed=${passed:-0}; total=${total:-0}

    if (( status == 124 )); then
      # timeout(1) returns 124 (no --preserve-status semantics here).
      batch_timeouts=$(( batch_timeouts + 1 ))
      total_timeouts=$(( total_timeouts + 1 ))
      iteration_event "$batch" "$batch_iterations" "timeout" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations TIMEOUT (exit=$status) in ${iter_elapsed}ms; not counted green."
    elif (( total == 0 )); then
      batch_zero_tests=$(( batch_zero_tests + 1 ))
      total_zero_tests=$(( total_zero_tests + 1 ))
      iteration_event "$batch" "$batch_iterations" "zero-tests" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations ZERO-TESTS (exit=$status, total=0) in ${iter_elapsed}ms; not counted green."
    elif (( status != 0 || failed > 0 )); then
      run_exit=$failed_run
      batch_failed=$(( batch_failed + failed ))
      total_failed=$(( total_failed + failed ))
      total_iterations=$(( total_iterations + 1 ))
      iteration_event "$batch" "$batch_iterations" "failure" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations FAILED (exit=$status, failed=$failed) in ${iter_elapsed}ms; stop requested." >&2
      break 2
    else
      batch_passed=$(( batch_passed + passed ))
      batch_failed=$(( batch_failed + failed ))
      total_passed=$(( total_passed + passed ))
      total_failed=$(( total_failed + failed ))
      total_iterations=$(( total_iterations + 1 ))
      iteration_event "$batch" "$batch_iterations" "success" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations ok in ${iter_elapsed}ms (tests=$total, failed=$failed)."
    fi
  done

  batch_elapsed_ms=$(ms_since "$batch_started")
  echo "[batch $batch/$batch_count] done: $batch_iterations iteration(s), ${batch_elapsed_ms}ms elapsed, passed=$batch_passed, failed=$batch_failed, zero-tests=$batch_zero_tests, timeouts=$batch_timeouts."
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"batch_end\",\"batch\":$batch,\"iterations\":$batch_iterations,\"elapsedMs\":$batch_elapsed_ms,\"passed\":$batch_passed,\"failed\":$batch_failed,\"zeroTests\":$batch_zero_tests,\"timeouts\":$batch_timeouts}"
  if (( run_exit != ok )); then
    break
  fi
done

total_elapsed_ms=$(ms_since "$run_started")
echo "Coordination soak complete: $total_iterations iteration(s), ${total_elapsed_ms}ms total, passed=$total_passed, failed=$total_failed, zero-tests=$total_zero_tests, timeouts=$total_timeouts, exit=$run_exit."

read -r ev_lines ev_valid ev_invalid <<< "$(verify_evidence_jsonl)"
ev_valid=${ev_valid:-0}; ev_invalid=${ev_invalid:-0}
if (( ev_invalid > 0 )); then
  echo "WARNING: evidence validation found $ev_invalid invalid JSON line(s) of $ev_lines." >&2
else
  echo "Evidence JSON validation passed: $ev_valid/$ev_lines lines are valid JSON objects."
fi
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"evidence_validated\",\"lines\":$ev_lines,\"valid\":$ev_valid,\"invalid\":$ev_invalid}"
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_end\",\"exit\":$run_exit,\"runTag\":\"$run_tag\",\"iterations\":$total_iterations,\"totalElapsedMs\":$total_elapsed_ms,\"passed\":$total_passed,\"failed\":$total_failed,\"zeroTests\":$total_zero_tests,\"timeouts\":$total_timeouts}"
echo "Evidence: $evidence_path"
exit "$run_exit"