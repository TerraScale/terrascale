#!/usr/bin/env bash
# Final TerraBase-only benchmark orchestration.
#
# This script is deliberately gated on an explicit verification handoff. It
# creates a fresh database for every lane and retains every raw receipt/log.
# It never starts comparator services or invents unavailable CDN/region lanes.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${TERRABASE_BENCH_FINAL_OUTPUT:-$ROOT/bench/results/run-$(date -u +%Y%m%dT%H%M%SZ)-$$}"
DRIVER="${TERRABASE_BENCH_FINAL_DRIVER:-$ROOT/bench/target/release/terrabase-bench-lane}"
TIMEOUT_SECS="${TERRABASE_BENCH_FINAL_TIMEOUT_SECONDS:-1800}"

[[ "${TERRABASE_BENCH_RELEASE_READY:-}" == 1 ]] || {
  echo "final bench: TERRABASE_BENCH_RELEASE_READY=1 is required after verification handoff" >&2
  exit 2
}
[[ -x "$DRIVER" ]] || { echo "final bench: pinned bench driver is not executable: $DRIVER" >&2; exit 2; }
[[ -n "${LOCAL_MEMBER_BIN:-}" && -x "$LOCAL_MEMBER_BIN" ]] || {
  echo "final bench: LOCAL_MEMBER_BIN must name the handed-off release executable" >&2
  exit 2
}
[[ -n "${TERRABASE_BENCH_MEMBER_API:-}" && -n "${TERRABASE_BENCH_MEMBER_AUTH:-}" && \
  -n "${TERRABASE_BENCH_MEMBER_KEY:-}" && -n "${TERRABASE_BENCH_MEMBER_VERSION:-}" ]] || {
  echo "final bench: source the verified member environment first" >&2
  exit 2
}
mkdir -p "$OUT"
MANIFEST="$OUT/manifest.tsv"
printf 'lane\tstatus\tlog\n' >"$MANIFEST"
overall=0

new_database() {
  local lane="$1"
  local database="bench-final-${lane}-$(date +%s%N)"
  curl -fsS --max-time 30 "$TERRABASE_BENCH_MEMBER_API/api/v1/databases" \
    -H "$TERRABASE_BENCH_MEMBER_AUTH" -H 'Content-Type: application/json' \
    -H 'x-terrascale-tenant-id: terrabase-local' -H 'x-terrascale-project-id: local' \
    --data "{\"name\":\"$database\",\"placement\":[\"${LOCAL_MEMBER_REGION:-GRU}\"]}" \
    >"$OUT/${lane}-database.json"
  export TERRABASE_BENCH_TERRABASE_DATABASE="$database"
  # The point and local auto-index drivers consume the bearer value without
  # a header name. Keep this scoped to the fresh database lane and derive it
  # from the verified member credential rather than copying the environment.
  export TERRABASE_BENCH_TERRABASE_API="$TERRABASE_BENCH_MEMBER_API"
  export TERRABASE_BENCH_TERRABASE_AUTH="Bearer $TERRABASE_BENCH_MEMBER_KEY"
}

run_point() {
  local workload="$1"
  new_database "$workload"
  local log="$OUT/${workload}.log"
  set +e
  timeout --signal=TERM --kill-after=30 "$TIMEOUT_SECS" nice -n 19 "$DRIVER" \
    --target terrabase --workload "$workload" \
    --seed 1000 --ops 10000 --samples 5 --concurrency 16 --warmup 1000 \
    --out "$OUT" >"$log" 2>&1
  local status=$?
  set -e
  printf '%s\t%s\t%s\n' "$workload" "$status" "$log" >>"$MANIFEST"
  echo "final bench: $workload status=$status log=$log" >&2
  (( status == 0 )) || overall=1
  return 0
}

source "$ROOT/bench/scripts/resource-envelope.sh"
envelope_require
for workload in read write query mixed; do run_point "$workload"; done

new_database ingest
export TERRABASE_BENCH_INGEST_TERRABASE_SINGLE_NODE_ENDPOINT="$TERRABASE_BENCH_MEMBER_API/api/v1/databases/$TERRABASE_BENCH_TERRABASE_DATABASE/collections/users/documents/batch"
export TERRABASE_BENCH_INGEST_TERRABASE_HEALTH_ENDPOINT="$TERRABASE_BENCH_MEMBER_API/api/v1/health"
export TERRABASE_BENCH_INGEST_TERRABASE_VERSION="$TERRABASE_BENCH_MEMBER_VERSION"
export TERRABASE_BENCH_INGEST_AUTHORIZATION="Bearer $TERRABASE_BENCH_MEMBER_KEY"
export TERRABASE_BENCH_INGEST_TARGET=terrabase
export TERRABASE_BENCH_INGEST_OUTPUT_DIR="$OUT"
export TERRABASE_BENCH_INGEST_DOCUMENTS=100000
export TERRABASE_BENCH_INGEST_BATCH_SIZE=1000
export TERRABASE_BENCH_INGEST_SAMPLES=5
export TERRABASE_BENCH_INGEST_WARMUP_BATCHES=1
export TERRABASE_BENCH_INGEST_TERRABASE_ARTIFACT_SHA256="${TERRABASE_BENCH_MEMBER_ARTIFACT_SHA256:?}"
export TERRABASE_BENCH_INGEST_RUNTIME_DIGEST="$TERRABASE_BENCH_MEMBER_ARTIFACT_SHA256"
export TERRABASE_BENCH_INGEST_MEMBER_PID="${TERRABASE_BENCH_MEMBER_PID:?}"
export TERRABASE_BENCH_INGEST_MEMBER_OCPU=2
export TERRABASE_BENCH_INGEST_MEMBER_MEMORY=12G
set +e
timeout --signal=TERM --kill-after=30 "$TIMEOUT_SECS" nice -n 19 "$DRIVER" \
  --ingest-target terrabase --ingest-topology single-node \
  --documents-per-sample 100000 --batch-size 1000 --ingest-samples 5 --warmup-batches 1 \
  --out "$OUT" >"$OUT/ingest.log" 2>&1
ingest_status=$?
set -e
printf 'ingest\t%s\t%s\n' "$ingest_status" "$OUT/ingest.log" >>"$MANIFEST"
(( ingest_status == 0 )) || overall=1

new_database auto-index
set +e
timeout --signal=TERM --kill-after=30 "$TIMEOUT_SECS" nice -n 19 "$DRIVER" \
  --family auto-index --target terrabase --documents 1000000 --samples 100 --writes 1000 --out "$OUT" \
  >"$OUT/auto-index.log" 2>&1
auto_index_status=$?
set -e
printf 'auto-index\t%s\t%s\n' "$auto_index_status" "$OUT/auto-index.log" >>"$MANIFEST"
(( auto_index_status == 0 )) || overall=1

new_database sync
export TERRABASE_SYNC_DATABASE="$TERRABASE_BENCH_TERRABASE_DATABASE"
export TERRABASE_SYNC_TOKEN="$TERRABASE_BENCH_MEMBER_KEY"
export TERRABASE_SYNC_LOCAL_ENDPOINT="$TERRABASE_BENCH_MEMBER_API"
export TERRABASE_SYNC_RUNTIME_DIGEST="$TERRABASE_BENCH_MEMBER_ARTIFACT_SHA256"
export TERRABASE_SYNC_COMPARATOR_DIGEST="$TERRABASE_BENCH_MEMBER_ARTIFACT_SHA256"
export TERRABASE_SYNC_RESOURCE_EVIDENCE="$TERRABASE_BENCH_MEMBER_RESOURCE_EVIDENCE"
export TERRABASE_SYNC_MEMBER_PID="$TERRABASE_BENCH_MEMBER_PID"
export TERRABASE_SYNC_REPLICATION=single
export TERRABASE_SYNC_SQL='SELECT _id FROM orders ORDER BY _id LIMIT 25'
export TERRABASE_SYNC_OUTPUT="$OUT/sync.json"
set +e
timeout --signal=TERM --kill-after=30 "$TIMEOUT_SECS" nice -n 19 "$ROOT/bench/scripts/sync.sh" >"$OUT/sync.log" 2>&1
sync_status=$?
set -e
printf 'sync\t%s\t%s\n' "$sync_status" "$OUT/sync.log" >>"$MANIFEST"
(( sync_status == 0 )) || overall=1

cp "$TERRABASE_BENCH_MEMBER_RESOURCE_EVIDENCE" "$OUT/member-after.txt"
echo "final bench: receipts retained under $OUT" >&2
echo "final bench: manifest=$MANIFEST overall=$overall" >&2
exit "$overall"
