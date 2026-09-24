#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly TEST_SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
readonly TEST_SCRIPT_DIR="$(dirname "$TEST_SCRIPT_PATH")"
readonly TEST_VERIFIER="$TEST_SCRIPT_DIR/strict-rustfs-tiered-40k.sh"
readonly TEST_FILTER="$TEST_SCRIPT_DIR/strict-rustfs-tiered-40k-durability.jq"
readonly TEST_WORK_DIR="$(mktemp -d /tmp/strict-rustfs-tiered-40k-durability-test.XXXXXX)"

cleanup() {
    rm -rf -- "$TEST_WORK_DIR"
}
trap cleanup EXIT

mkdir -p -- "$TEST_WORK_DIR/pass/engine" "$TEST_WORK_DIR/mismatch/engine"
: > "$TEST_WORK_DIR/pass/engine/wal"
printf 'wal-fixture\n' > "$TEST_WORK_DIR/mismatch/engine/wal"

events='low 0
high 0
max 293503
oom 0
oom_kill 0
oom_group_kill 0'
jq -n --arg events "$events" \
    '{LocalWalBytes:0,
      RemoteAck:{AcknowledgedBatches:644,AcknowledgedRecords:41104},
      RemoteJournalObjects:{ObjectCount:644,TotalBytes:350719536},
      SeedRootLossRecoverySeconds:141.9,MutationRootLossRecoverySeconds:149.8,
      FinalCgroup:{MemoryEvents:$events}}' \
    > "$TEST_WORK_DIR/pass/tiered-rustfs-receipt.json"
jq -n --arg events "$events" \
    '{LocalWalBytes:0,
      RemoteAck:{AcknowledgedBatches:644,AcknowledgedRecords:41104},
      RemoteJournalObjects:{ObjectCount:643,TotalBytes:350719536},
      SeedRootLossRecoverySeconds:141.9,MutationRootLossRecoverySeconds:149.8,
      FinalCgroup:{MemoryEvents:$events}}' \
    > "$TEST_WORK_DIR/mismatch/tiered-rustfs-receipt.json"

source "$TEST_VERIFIER"
verify_wal_evidence "$TEST_WORK_DIR/pass" "$TEST_WORK_DIR/pass/tiered-rustfs-receipt.json"

if (verify_wal_evidence "$TEST_WORK_DIR/mismatch" "$TEST_WORK_DIR/mismatch/tiered-rustfs-receipt.json") >/dev/null 2>&1; then
    printf 'expected WAL size mismatch to fail\n' >&2
    exit 1
fi

jq -e --argjson minimum_ack 41104 \
    -f "$TEST_FILTER" "$TEST_WORK_DIR/pass/tiered-rustfs-receipt.json" \
    > "$TEST_WORK_DIR/pass/durability-verification.json"
jq -e '
    .verification == "passed"
    and .remoteJournal.batchesMatchListedObjects == true
    and .frontierEvidence.validatedByRootLossRebuilds == true
' "$TEST_WORK_DIR/pass/durability-verification.json" >/dev/null

if jq -e --argjson minimum_ack 41104 \
    -f "$TEST_FILTER" "$TEST_WORK_DIR/mismatch/tiered-rustfs-receipt.json" >/dev/null 2>&1; then
    printf 'expected remote journal object mismatch to fail\n' >&2
    exit 1
fi

printf 'strict-rustfs-tiered-40k durability fixture: ok\n'
