#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly TEST_SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
readonly TEST_SCRIPT_DIR="$(dirname "$TEST_SCRIPT_PATH")"
readonly TEST_BENCH_ROOT="$(cd "$TEST_SCRIPT_DIR/../.." && pwd)"
readonly TEST_FIXTURE_DIR="$TEST_BENCH_ROOT/bench/fixtures/strict-rustfs-ingest-performance"
readonly TEST_INGEST_SCRIPT="$TEST_SCRIPT_DIR/strict-rustfs-ingest.sh"
readonly TEST_WORK_DIR="$(mktemp -d /tmp/strict-rustfs-ingest-performance-test.XXXXXX)"

cleanup() {
    rm -rf -- "$TEST_WORK_DIR"
}
trap cleanup EXIT

make_run() {
    local name="$1"
    local records="$2"
    local duration="$3"
    local progress_fixture="$4"
    local output="$TEST_WORK_DIR/$name"
    mkdir -p -- "$output"
    cp -- "$TEST_FIXTURE_DIR/$progress_fixture" "$output/progress.jsonl"
    cp -- "$TEST_FIXTURE_DIR/monitor-good.tsv" "$output/monitor.tsv"
    jq \
        --arg progress_path "$output/progress.jsonl" \
        --argjson records "$records" \
        --argjson duration "$duration" \
        '.TargetRecords = $records
         | .ProgressPath = $progress_path
         | .PhaseMetrics[0].CompletedRecords = $records
         | .PhaseMetrics[0].DurationSeconds = $duration' \
        "$TEST_FIXTURE_DIR/receipt-template.json" > "$output/tiered-rustfs-receipt.json"
    printf '%s\n' "$output"
}

run_performance_check() {
    local output="$1"
    local records="$2"
    (
        source "$TEST_INGEST_SCRIPT"
        OUTPUT="$output"
        EXPECTED_RECORDS="$records"
        verify_ingest_performance
    )
}

run_resource_check() {
    local output="$1"
    (
        source "$TEST_INGEST_SCRIPT"
        OUTPUT="$output"
        verify_resource_budget
    )
}

pass_run="$(make_run pass 10000000 2000 progress-good.jsonl)"
run_resource_check "$pass_run"
jq -e '
    .status == "passed-with-memory-pressure-events"
    and .memory.peakExceedsMax
    and .memory.peakOvershootBytes == 1011712
    and .memoryEvents.max == 2156
    and .memoryEvents.high == 2156
    and .gates.oom.passed
    and .swap.passed
    and .gates.anonymousMemory.passed
' "$pass_run/resource-verification.json" >/dev/null
run_performance_check "$pass_run" 10000000
jq -e '
    .status == "passed"
    and .gates.overall.passed
    and .gates.sustained.passed
    and (.progress.lastFullIntervals | length) == 3
    and (.progress.lastFullIntervals | all(.records == 524288))
' "$pass_run/performance-verification.json" >/dev/null

slow_run="$(make_run slow 10000000 2000 progress-slow.jsonl)"
if run_performance_check "$slow_run" 10000000 >/dev/null 2>&1; then
    printf 'expected the enforced 10M sustained gate to fail\n' >&2
    exit 1
fi

large_progress="$TEST_WORK_DIR/large-progress.jsonl"
jq -c 'if .phase == "seed" then .total = 100000000 | .completed = (.completed * 10) | .elapsedSeconds = (.elapsedSeconds * 10) else . end' \
    "$TEST_FIXTURE_DIR/progress-good.jsonl" > "$large_progress"
large_run="$TEST_WORK_DIR/large"
mkdir -p -- "$large_run"
cp -- "$large_progress" "$large_run/progress.jsonl"
jq \
    --arg progress_path "$large_run/progress.jsonl" \
    '.TargetRecords = 100000000
     | .ProgressPath = $progress_path
     | .PhaseMetrics[0].CompletedRecords = 100000000
     | .PhaseMetrics[0].DurationSeconds = 20000' \
    "$TEST_FIXTURE_DIR/receipt-template.json" > "$large_run/tiered-rustfs-receipt.json"
run_performance_check "$large_run" 100000000
jq -e '
    .status == "passed"
    and .enforced
    and .gates.overall.passed
    and .gates.sustained.passed
    and (.progress.lastFullIntervals | length) == 3
    and (.progress.lastFullIntervals | all(.records == 5242880))
' \
    "$large_run/performance-verification.json" >/dev/null

large_overall_slow="$TEST_WORK_DIR/large-overall-slow"
mkdir -p -- "$large_overall_slow"
cp -- "$large_progress" "$large_overall_slow/progress.jsonl"
jq \
    --arg progress_path "$large_overall_slow/progress.jsonl" \
    '.TargetRecords = 100000000
     | .ProgressPath = $progress_path
     | .PhaseMetrics[0].CompletedRecords = 100000000
     | .PhaseMetrics[0].DurationSeconds = 30000' \
    "$TEST_FIXTURE_DIR/receipt-template.json" > "$large_overall_slow/tiered-rustfs-receipt.json"
if run_performance_check "$large_overall_slow" 100000000 >/dev/null 2>&1; then
    printf 'expected the enforced 100M overall gate to fail\n' >&2
    exit 1
fi

large_sustained_slow_progress="$TEST_WORK_DIR/large-sustained-slow-progress.jsonl"
jq -c 'if .phase == "seed" then .total = 100000000 | .completed = (.completed * 10) | .elapsedSeconds = (.elapsedSeconds * 10) else . end' \
    "$TEST_FIXTURE_DIR/progress-slow.jsonl" > "$large_sustained_slow_progress"
large_sustained_slow="$TEST_WORK_DIR/large-sustained-slow"
mkdir -p -- "$large_sustained_slow"
cp -- "$large_sustained_slow_progress" "$large_sustained_slow/progress.jsonl"
jq \
    --arg progress_path "$large_sustained_slow/progress.jsonl" \
    '.TargetRecords = 100000000
     | .ProgressPath = $progress_path
     | .PhaseMetrics[0].CompletedRecords = 100000000
     | .PhaseMetrics[0].DurationSeconds = 20000' \
    "$TEST_FIXTURE_DIR/receipt-template.json" > "$large_sustained_slow/tiered-rustfs-receipt.json"
if run_performance_check "$large_sustained_slow" 100000000 >/dev/null 2>&1; then
    printf 'expected the enforced 100M sustained gate to fail\n' >&2
    exit 1
fi

large_bad_run="$TEST_WORK_DIR/large-bad-progress"
mkdir -p -- "$large_bad_run"
sed '$d' "$large_progress" > "$large_bad_run/progress.jsonl"
jq \
    --arg progress_path "$large_bad_run/progress.jsonl" \
    '.TargetRecords = 100000000
     | .ProgressPath = $progress_path
     | .PhaseMetrics[0].CompletedRecords = 100000000
     | .PhaseMetrics[0].DurationSeconds = 30000' \
    "$TEST_FIXTURE_DIR/receipt-template.json" > "$large_bad_run/tiered-rustfs-receipt.json"
if run_performance_check "$large_bad_run" 100000000 >/dev/null 2>&1; then
    printf 'expected incomplete 100M progress to fail verification\n' >&2
    exit 1
fi

resource_fail="$TEST_WORK_DIR/resource-fail"
mkdir -p -- "$resource_fail"
cp -- "$pass_run/monitor.tsv" "$resource_fail/monitor.tsv"
jq '.FinalCgroup.SwapCurrent = "1"' "$pass_run/tiered-rustfs-receipt.json" > "$resource_fail/tiered-rustfs-receipt.json"
if run_resource_check "$resource_fail" >/dev/null 2>&1; then
    printf 'expected the resource swap gate to fail\n' >&2
    exit 1
fi

printf 'strict-rustfs-ingest performance fixture: ok\n'
