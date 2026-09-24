#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly TEST_SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
readonly TEST_SCRIPT_DIR="$(dirname "$TEST_SCRIPT_PATH")"
readonly TEST_FILTER="$TEST_SCRIPT_DIR/strict-rustfs-tiered-40k-cgroup.jq"
readonly TEST_WORK_DIR="$(mktemp -d /tmp/strict-rustfs-tiered-40k-cgroup-test.XXXXXX)"
readonly TEST_MEMORY_MAX=268435456

cleanup() {
    rm -rf -- "$TEST_WORK_DIR"
}
trap cleanup EXIT

make_receipt() {
    local name="$1"
    local memory_max="$2"
    local memory_peak="$3"
    local memory_current="$4"
    local swap_max="$5"
    local swap_current="$6"
    local events="$7"
    jq -n \
        --arg memory_max "$memory_max" \
        --arg memory_peak "$memory_peak" \
        --arg memory_current "$memory_current" \
        --arg swap_max "$swap_max" \
        --arg swap_current "$swap_current" \
        --arg events "$events" \
        '{FinalCgroup:{MemoryMax:$memory_max,MemoryPeak:$memory_peak,
          MemoryCurrent:$memory_current,SwapMax:$swap_max,
          SwapCurrent:$swap_current,MemoryEvents:$events}}' \
        > "$TEST_WORK_DIR/$name.json"
}

run_gate() {
    local name="$1"
    jq -e --argjson configured_max "$TEST_MEMORY_MAX" \
        -f "$TEST_FILTER" "$TEST_WORK_DIR/$name.json" \
        > "$TEST_WORK_DIR/$name-verification.json"
}

events='low 0
high 0
max 197431
oom 0
oom_kill 0
oom_group_kill 0'
make_receipt accepts-peak-overshoot 268435456 274821120 245137408 0 0 "$events"
run_gate accepts-peak-overshoot
jq -e '
    .verification == "passed"
    and .policy.memoryPeakMustBeAtMostMax == false
    and .memory.peakExceedsMax == true
    and .memory.peakOvershootBytes == 6385664
    and .memory.finalCurrentBytes == 245137408
    and .memoryEvents.max == 197431
    and .pressure.memoryEventsMax == 197431
' "$TEST_WORK_DIR/accepts-peak-overshoot-verification.json" >/dev/null

make_receipt rejects-final-current 268435456 274821120 268435457 0 0 "$events"
if run_gate rejects-final-current >/dev/null 2>&1; then
    printf 'expected final memory.current gate to fail\n' >&2
    exit 1
fi

make_receipt rejects-unbounded max 274821120 245137408 0 0 "$events"
if run_gate rejects-unbounded >/dev/null 2>&1; then
    printf 'expected finite memory.max gate to fail\n' >&2
    exit 1
fi

oom_events='low 0
high 0
max 197431
oom 0
oom_kill 1
oom_group_kill 0'
make_receipt rejects-oom 268435456 274821120 245137408 0 0 "$oom_events"
if run_gate rejects-oom >/dev/null 2>&1; then
    printf 'expected OOM counter gate to fail\n' >&2
    exit 1
fi

make_receipt rejects-swap 268435456 274821120 245137408 0 1 "$events"
if run_gate rejects-swap >/dev/null 2>&1; then
    printf 'expected swap current gate to fail\n' >&2
    exit 1
fi

printf 'strict-rustfs-tiered-40k cgroup fixture: ok\n'
