#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly TEST_SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
readonly TEST_SCRIPT_DIR="$(dirname "$TEST_SCRIPT_PATH")"
readonly TEST_FILTER="$TEST_SCRIPT_DIR/strict-rustfs-tiered-40k-staging.jq"
readonly TEST_WORK_DIR="$(mktemp -d /tmp/strict-rustfs-tiered-40k-staging-test.XXXXXX)"
readonly TEST_QUANTUM=67108864

cleanup() {
    rm -rf -- "$TEST_WORK_DIR"
}
trap cleanup EXIT

make_receipt() {
    local name="$1"
    local journal_bytes="$2"
    local peak_bytes="$3"
    local final_bytes="$4"
    jq -n \
        --argjson journal_bytes "$journal_bytes" \
        --argjson peak_bytes "$peak_bytes" \
        --argjson final_bytes "$final_bytes" \
        '{RemoteJournalObjects:{TotalBytes:$journal_bytes},
          PeakStagingBytes:$peak_bytes,
          LatestResources:{StagingBytes:$final_bytes}}' \
        > "$TEST_WORK_DIR/$name.json"
}

run_gate() {
    local name="$1"
    jq -e --argjson allocation_quantum "$TEST_QUANTUM" \
        -f "$TEST_FILTER" "$TEST_WORK_DIR/$name.json" \
        > "$TEST_WORK_DIR/$name-verification.json"
}

# 350,719,536 bytes require six 64 MiB allocation chunks.
make_receipt accepts-boundary 350719536 402653184 0
run_gate accepts-boundary
jq -e '
    .verification == "passed"
    and .staging.allocatedChunks == 6
    and .staging.derivedBoundBytes == 402653184
    and .staging.peakBytes == 402653184
    and .staging.finalBytes == 0
' "$TEST_WORK_DIR/accepts-boundary-verification.json" >/dev/null

make_receipt rejects-overrun 350719536 402653185 0
if run_gate rejects-overrun >/dev/null 2>&1; then
    printf 'expected staging overrun gate to fail\n' >&2
    exit 1
fi

make_receipt rejects-no-staging 350719536 0 0
if run_gate rejects-no-staging >/dev/null 2>&1; then
    printf 'expected missing staging telemetry gate to fail\n' >&2
    exit 1
fi

make_receipt rejects-final-staging 350719536 402653184 1
if run_gate rejects-final-staging >/dev/null 2>&1; then
    printf 'expected final staging cleanup gate to fail\n' >&2
    exit 1
fi

printf 'strict-rustfs-tiered-40k staging fixture: ok\n'
