#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d) # 0700: temporary test tokens stay outside git and reports.
targets="$work/cluster.json"
host=''
cleanup() {
  status=$?
  rm -f "$targets"
  if [[ -n "$host" ]]; then
    for _ in {1..60}; do
      kill -0 "$host" 2>/dev/null || break
      sleep 1
    done
    if kill -0 "$host" 2>/dev/null; then
      kill "$host" 2>/dev/null || true
    fi
    wait "$host" || true
  fi
  if [[ "$status" == 0 ]]; then
    rm -rf "$work"
  else
    echo "ANN job failed; private host log retained at $work/host.log" >&2
  fi
}
trap cleanup EXIT

TERRABASE_BENCH_TARGETS="$targets" \
  dotnet run --project "$root/tests/TerraBase.E2E" --configuration Release --no-build -- \
  --treenode-filter '/*/*/LocalBenchmarkHost/*' >"$work/host.log" 2>&1 &
host=$!
for _ in {1..180}; do
  [[ -f "$targets" ]] && break
  if ! kill -0 "$host" 2>/dev/null; then
    echo 'ANN cluster host exited before readiness' >&2
    exit 1
  fi
  sleep 1
done
[[ -f "$targets" ]] || { echo 'ANN cluster host was not ready within 180 seconds' >&2; exit 1; }

cd "$root/benchmark"
go test ./cmd/ann-bench
go run ./cmd/ann-bench --targets "$targets" --out "bench/$(date -u +%F)-ann-local.md" "$@"
