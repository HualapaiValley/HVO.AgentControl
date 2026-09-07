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
# opt-in step; it refuses to operate unless executed directly.
#
# Tuning (env): SOAK_BATCHES (4), SOAK_BATCH_SECONDS (120),
#   SOAK_MAX_TOTAL_SECONDS (900), SOAK_CONFIGURATION (Release),
#   SOAK_ARTIFACT_DIR (artifacts/coordination-soak).
set -u

cd "$(dirname "$0")/.." || exit 2
repo_root="$(pwd)"

command -v dotnet >/dev/null 2>&1 || { echo "dotnet is required; refusing to run the soak." >&2; exit 2; }

batch_count="${SOAK_BATCHES:-4}"
batch_seconds="${SOAK_BATCH_SECONDS:-120}"
max_total_seconds="${SOAK_MAX_TOTAL_SECONDS:-900}"
configuration="${SOAK_CONFIGURATION:-Release}"
project="tests/HVO.AgentControl.Tests/HVO.AgentControl.Tests.csproj"
filter="FullyQualifiedName~CoordinationStressTests"
artifact_dir="${SOAK_ARTIFACT_DIR:-$repo_root/artifacts/coordination-soak}"
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
log_event() {
  local started_ms="$1" event="$2" batch="$3" iteration="$4" status="$5"
  local now_ms; now_ms=$(date +%s%N)
  local elapsed_ms=$(( ( now_ms - started_ms ) / 1000000 ))
  printf '{"ts":"%s","event":"%s","batch":%s,"iteration":%s,"exit":%s,"elapsedMs":%s}\n' \
    "$(now_iso)" "$event" "$batch" "$iteration" "$status" "$elapsed_ms" >> "$evidence_path"
}
iteration_event() {
  local batch="$1" iteration="$2" status="$3" elapsed_ms="$4" passed="$5" failed="$6" total="$7"
  printf '{"ts":"%s","event":"iteration","batch":%s,"iteration":%s,"exit":%s,"elapsedMs":%s,"passed":%s,"failed":%s,"total":%s}\n' \
    "$(now_iso)" "$batch" "$iteration" "$status" "$elapsed_ms" "$passed" "$failed" "$total" >> "$evidence_path"
}

printf '' > "$evidence_path"
echo "Coordination soak starting: $batch_count batch(es) x $batch_seconds s, hard guard $max_total_seconds s."
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_start\",\"batches\":$batch_count,\"batchSeconds\":$batch_seconds,\"maxTotalSeconds\":$max_total_seconds,\"filter\":\"$filter\"}"

run_started=$(date +%s%N)
echo "Building $project ($configuration) once before the soaked loops."
build_log="$artifact_dir/build.log"
dotnet build "$project" --configuration "$configuration" >"$build_log" 2>&1
if (( $? != 0 )); then
  echo "Build failed; see $build_log." >&2
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_end\",\"exit\":$failed_run,\"phase\":\"build\"}"
  exit "$failed_run"
fi

total_iterations=0
total_passed=0
total_failed=0
run_exit=$ok

for batch in $(seq 1 "$batch_count"); do
  batch_started=$(date +%s%N)
  batch_iterations=0
  batch_passed=0
  batch_failed=0
  batch_errors=0
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
    kill_after_s=$(( ( remaining_batch_ms + 999 ) / 1000 ))
    (( kill_after_s >= 1 )) || kill_after_s=1

    batch_iterations=$(( batch_iterations + 1 ))
    iteration=$(printf '%03d' "$batch_iterations")
    iter_started=$(date +%s%N)
    logfile="$artifact_dir/batch${batch}-iter${iteration}.log"
    timeout --preserve-status --kill-after=5 "$kill_after_s" \
      dotnet test "$project" --configuration "$configuration" --no-restore --no-build --filter "$filter" \
      >"$logfile" 2>&1
    status=$?
    iter_elapsed=$(( ( $(date +%s%N) - iter_started ) / 1000000 ))

    summary_line=$(grep -a -E 'Failed:|Passed:' "$logfile" | grep -a -E 'Total:' | tail -n 1)
    failed=$(printf '%s' "$summary_line" | sed -nE 's/.*Failed:[[:space:]]*([0-9]+).*/\1/p')
    passed=$(printf '%s' "$summary_line" | sed -nE 's/.*Passed:[[:space:]]*([0-9]+).*/\1/p')
    total=$(printf '%s' "$summary_line" | sed -nE 's/.*Total:[[:space:]]*([0-9]+).*/\1/p')
    failed=${failed:-0}; passed=${passed:-0}; total=${total:-0}

    if (( status == 0 )); then
      batch_passed=$(( batch_passed + passed ))
      batch_failed=$(( batch_failed + failed ))
      total_passed=$(( total_passed + passed ))
      total_failed=$(( total_failed + failed ))
      total_iterations=$(( total_iterations + 1 ))
      iteration_event "$batch" "$batch_iterations" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations ok in ${iter_elapsed}ms (tests=$total, failed=$failed)."
    else
      run_exit=$failed_run
      batch_failed=$(( batch_failed + failed ))
      total_failed=$(( total_failed + failed ))
      iteration_event "$batch" "$batch_iterations" "$status" "$iter_elapsed" "$passed" "$failed" "$total"
      echo "[batch $batch] iteration $batch_iterations FAILED (exit=$status) in ${iter_elapsed}ms; stop requested." >&2
      log_event "$iter_started" "iteration_failed" "$batch" "$batch_iterations" "$status"
      break 2
    fi
  done

  batch_elapsed_ms=$(ms_since "$batch_started")
  echo "[batch $batch/$batch_count] done: $batch_iterations iteration(s), ${batch_elapsed_ms}ms elapsed, failed=$batch_failed."
  write_event "{\"ts\":\"$(now_iso)\",\"event\":\"batch_end\",\"batch\":$batch,\"iterations\":$batch_iterations,\"elapsedMs\":$batch_elapsed_ms,\"passed\":$batch_passed,\"failed\":$batch_failed}"
  if (( run_exit != ok )); then
    break
  fi
done

total_elapsed_ms=$(ms_since "$run_started")
echo "Coordination soak complete: $total_iterations iteration(s), ${total_elapsed_ms}ms total, passed=$total_passed, failed=$total_failed, exit=$run_exit."
write_event "{\"ts\":\"$(now_iso)\",\"event\":\"run_end\",\"exit\":$run_exit,\"iterations\":$total_iterations,\"totalElapsedMs\":$total_elapsed_ms,\"passed\":$total_passed,\"failed\":$total_failed}"
echo "Evidence: $evidence_path"
exit "$run_exit"