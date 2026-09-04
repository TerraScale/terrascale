#!/usr/bin/env bash
# E-13 point-ops family harness: read + write vs PostgreSQL/JSONB + RavenDB.
#
# One command for the whole family (D-47: benchmarks are one command
# appending to terrabase/bench/RESULTS.md):
#   bench/scripts/run-point-ops.sh [--live] [--results DIR]
#
# Default mode never needs a live server: it records the resource ceiling,
# captures ambient host metadata, and (re)renders the point-ops section of
# bench/RESULTS.md from the retained JSON under bench/results/. It always
# exits 0 when the render succeeds, so CI can run it without databases.
#
# --live additionally drives the lane binary for the read and write
# workloads of every reachable reference target (pg, raven, pg-fullstack)
# with the lane's RECORD_MINIMUM configuration, then
# renders. Unreachable targets are reported and skipped, never faked: their
# rows stay missing/failed in RESULTS.md.
#
# Scope: read + write only. query belongs to the indexed-query family and
# mixed is a cross-family composite; this script never drives or renders
# them, so sibling families share no files with it.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
RESULTS_DIR="$REPO_ROOT/bench/results"
LIVE=0

for arg in "$@"; do
  case "$arg" in
    --live) LIVE=1 ;;
    --results=*) RESULTS_DIR="${arg#--results=}" ;;
    --results) echo "--results requires =DIR" >&2; exit 2 ;;
    -h|--help)
      sed -n '2,20p' "$0"
      exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# The fixed 2 OCPU / 12 GiB launch-member envelope every measured subject
# runs under. The live path refuses to proceed when cgroup tooling is absent.
# shellcheck disable=SC1091
source "$SCRIPT_DIR/resource-envelope.sh"

echo "point-ops: envelope spec $TERRABASE_BENCH_ENVELOPE_SPEC"
echo -n "point-ops: host "
uname -sr
echo -n "point-ops: logical cpus "
(nproc 2>/dev/null || python3 -c 'import os; print(os.cpu_count() or 0)')
echo -n "point-ops: mem "
(free -g 2>/dev/null | awk '/^Mem:/{print $2"GiB total"}' || echo unknown)
echo -n "point-ops: loadavg "
cat /proc/loadavg 2>/dev/null || echo unknown
if [[ -x "$SCRIPT_DIR/io-pressure.sh" ]]; then
  echo -n "point-ops: "
  IO_PRESSURE_SAMPLE_SECONDS=1 "$SCRIPT_DIR/io-pressure.sh" || true
fi

# Record this invocation's ceiling + ambient facts next to the lane facts
# so a future reader can tell an honest comparison from a noisy one.
RUN_META="$RESULTS_DIR/point-ops-run-$(date +%s)-$$.json"
mkdir -p "$RESULTS_DIR"
{
  echo "{"
  echo "  \"family\": \"point-ops\","
  echo "  \"workloads\": [\"read\", \"write\"],"
  echo "  \"envelope_spec\": \"$TERRABASE_BENCH_ENVELOPE_SPEC\","
  echo '  "envelope_cpus": "'"${TERRABASE_BENCH_ENVELOPE_CPUS:-2}"'",'
  echo '  "envelope_memory": "'"${TERRABASE_BENCH_ENVELOPE_MEMORY:-12G}"'",'
  echo "  \"kernel\": \"$(uname -sr)\","
  echo "  \"loadavg\": \"$(cat /proc/loadavg 2>/dev/null || echo unknown)\","
  echo "  \"live\": $LIVE"
  echo "}"
} > "$RUN_META"
echo "point-ops: run metadata $RUN_META"

LANE_BIN=""
if [[ "$LIVE" == "1" ]]; then
  if [[ -x "$REPO_ROOT/bench/target/release/terrabase-bench-lane" ]]; then
    LANE_BIN="$REPO_ROOT/bench/target/release/terrabase-bench-lane"
  else
    echo "point-ops: building lane driver (release)..."
    (cd "$REPO_ROOT/bench" && cargo build --release --locked)
    LANE_BIN="$REPO_ROOT/bench/target/release/terrabase-bench-lane"
  fi
  for target in pg raven; do
    for workload in read write; do
      echo "point-ops: live $target/$workload ..."
      if ! "$LANE_BIN" --target "$target" --workload "$workload" --out "$RESULTS_DIR"; then
        echo "point-ops: $target/$workload failed or unreachable; retained as-is, continuing" >&2
      fi
    done
  done
  echo "point-ops: live runs done (pg-fullstack/terrabase stay on their own lanes)"
fi

python3 "$SCRIPT_DIR/render-point-ops.py" --results "$RESULTS_DIR" --out "$REPO_ROOT/bench/RESULTS.md"
echo "point-ops: published to bench/RESULTS.md"
