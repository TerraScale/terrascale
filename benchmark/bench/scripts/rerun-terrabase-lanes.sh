#!/usr/bin/env bash
# One-shot terrabase-side re-run for the 2026-09-09 diff: served point ops
# (fresh database per workload) + single-node ingest, matching
# run-terrabase-final.sh lane-for-lane. Receipts land flat in bench/results.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DRIVER="$ROOT/bench/target/release/terrabase-bench-lane"
OUT="$ROOT/bench/results"
TIMEOUT_SECS="${TERRABASE_BENCH_FINAL_TIMEOUT_SECONDS:-1800}"

new_database() {
  local lane="$1"
  local database="bench-diff-${lane}-$(date +%s%N)"
  curl -fsS --max-time 30 "$TERRABASE_BENCH_MEMBER_API/api/v1/databases" \
    -H "$TERRABASE_BENCH_MEMBER_AUTH" -H 'Content-Type: application/json' \
    -H 'x-terrascale-tenant-id: terrabase-local' -H 'x-terrascale-project-id: local' \
    --data "{\"name\":\"$database\",\"placement\":[\"${LOCAL_MEMBER_REGION:-GRU}\"]}" \
    >/dev/null
  export TERRABASE_BENCH_TERRABASE_DATABASE="$database"
  export TERRABASE_BENCH_TERRABASE_API="$TERRABASE_BENCH_MEMBER_API"
  export TERRABASE_BENCH_TERRABASE_AUTH="Bearer $TERRABASE_BENCH_MEMBER_KEY"
}

for workload in read write query mixed; do
  new_database "$workload"
  echo "=== terrabase/$workload (db $TERRABASE_BENCH_TERRABASE_DATABASE) ==="
  timeout --signal=TERM --kill-after=30 "$TIMEOUT_SECS" nice -n 19 "$DRIVER" \
    --target terrabase --workload "$workload" \
    --seed 1000 --ops 10000 --samples 5 --concurrency 16 --warmup 1000 \
    --out "$OUT" || echo "terrabase/$workload FAILED ($?)"
done

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
echo "=== terrabase/ingest ==="
timeout --signal=TERM --kill-after=30 "$TIMEOUT_SECS" nice -n 19 "$DRIVER" \
  --ingest-target terrabase --ingest-topology single-node \
  --documents-per-sample 100000 --batch-size 1000 --ingest-samples 5 --warmup-batches 1 \
  --out "$OUT" || echo "terrabase/ingest FAILED ($?)"
