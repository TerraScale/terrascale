#!/usr/bin/env bash
# E-13 production-boundary bulk-ingest family.
#
# Subjects are supplied by the operator (the script never silently starts a
# substitute).  Run one topology at a time; target result files therefore
# cannot mix the single-node and RF=2 families.  The Rust driver retains a
# failed JSON attempt before returning non-zero.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
# The driver records the serving member's launch envelope and refuses any
# run where the cgroup fence cannot be established.
# shellcheck disable=SC1091
source "$SCRIPT_DIR/resource-envelope.sh"
envelope_require
OUT_DIR="${TERRABASE_BENCH_INGEST_OUTPUT_DIR:-$REPO_ROOT/bench/results}"
TOPOLOGY="${TERRABASE_BENCH_INGEST_TOPOLOGY:-single-node}"
TARGET="${TERRABASE_BENCH_INGEST_TARGET:-all}"
DOCS="${TERRABASE_BENCH_INGEST_DOCUMENTS:-100000}"
BATCH="${TERRABASE_BENCH_INGEST_BATCH_SIZE:-1000}"
SAMPLES="${TERRABASE_BENCH_INGEST_SAMPLES:-5}"
WARMUP="${TERRABASE_BENCH_INGEST_WARMUP_BATCHES:-1}"
RUN_GROUP="${TERRABASE_BENCH_INGEST_RUN_GROUP:-e13-$(date +%s)-$$}"
export TERRABASE_BENCH_INGEST_RUN_GROUP="$RUN_GROUP"

case "$TOPOLOGY" in
  single-node|rf2) ;;
  *) echo "ingest: topology must be single-node or rf2 (got $TOPOLOGY)" >&2; exit 2 ;;
esac

# These are hard admission checks, not defaults.  In particular, an old
# A legacy oversized lane must not accidentally publish a production-looking result.
if [[ "${TERRABASE_BENCH_INGEST_MEMBER_OCPU:-}" != "2" ]]; then
  echo "ingest: TERRABASE_BENCH_INGEST_MEMBER_OCPU=2 is required" >&2
  exit 2
fi
case "${TERRABASE_BENCH_INGEST_MEMBER_MEMORY:-}" in
  12G|12GiB|12884901888) ;;
  *) echo "ingest: TERRABASE_BENCH_INGEST_MEMBER_MEMORY must be 12G/12GiB" >&2; exit 2 ;;
esac

if [[ "$TARGET" == postgresql-jsonb || "$TARGET" == pg-jsonb || "$TARGET" == all ]]; then
  [[ "${TERRABASE_BENCH_INGEST_ALLOW_RESET:-}" == 1 ]] || {
    echo "ingest: PostgreSQL JSONB requires TERRABASE_BENCH_INGEST_ALLOW_RESET=1 on a dedicated database" >&2
    exit 2
  }
fi

# Every comparator pin is an artifact digest.  Mutable image tags and empty
# identities are rejected before any service is contacted.  Only the targets
# this invocation actually drives are pinned: a terrabase-only run owes no
# PostgreSQL or RavenDB artifact identity.
targets=(terrabase postgresql-jsonb ravendb)
if [[ "$TARGET" != all ]]; then
  targets=("$TARGET")
fi
pin_names=(TERRABASE_BENCH_INGEST_RUNTIME_DIGEST)
for driven in "${targets[@]}"; do
  case "$driven" in
    terrabase|tb) pin_names+=(TERRABASE_BENCH_INGEST_TERRABASE_ARTIFACT_SHA256) ;;
    postgresql-jsonb|postgres-jsonb|pg-jsonb) pin_names+=(TERRABASE_BENCH_INGEST_POSTGRESQL_ARTIFACT_SHA256) ;;
    ravendb|raven) pin_names+=(TERRABASE_BENCH_INGEST_RAVENDB_ARTIFACT_SHA256) ;;
  esac
done
for pin_name in "${pin_names[@]}"; do
  pin_value="${!pin_name:-}"
  if [[ ! "$pin_value" =~ ^[[:xdigit:]]{64}$ ]]; then
    echo "ingest: $pin_name must be a 64-character immutable SHA-256 digest" >&2
    exit 2
  fi
done
export TERRABASE_BENCH_INGEST_MEMBER_OCPU=2
export TERRABASE_BENCH_INGEST_MEMBER_MEMORY=12G

command -v cargo >/dev/null || { echo "ingest: cargo is required" >&2; exit 2; }
command -v nice >/dev/null || { echo "ingest: nice is required" >&2; exit 2; }

# The benchmark process is placed under the same cgroup contract used for the
# member.  The driver records the resulting cgroup files with every sample.
run_enveloped() {
  envelope_wrap nice -n 19 "$@"
}

for target in "${targets[@]}"; do
  case "$target" in
    terrabase|tb) ;;
    postgresql-jsonb|postgres-jsonb|pg-jsonb) ;;
    ravendb|raven) ;;
    *) echo "ingest: unknown target $target" >&2; exit 2 ;;
  esac
  echo "ingest: target=$target topology=$TOPOLOGY envelope=2ocpu/12GiB"
  run_enveloped cargo run --release --manifest-path "$REPO_ROOT/bench/Cargo.toml" -- \
    --ingest-target "$target" \
    --ingest-topology "$TOPOLOGY" \
    --documents-per-sample "$DOCS" \
    --batch-size "$BATCH" \
    --ingest-samples "$SAMPLES" \
    --warmup-batches "$WARMUP" \
    --out "$OUT_DIR" \
    ${TERRABASE_BENCH_INGEST_ALLOW_RESET:+--allow-reset}
done

echo "ingest: complete family=$TOPOLOGY targets=${#targets[@]} output=$OUT_DIR"
