#!/usr/bin/env bash
# The replication benchmark against three local regions (gru, sjc, fra) hosted in one process by
# tests/TerraBase.E2E/BenchmarkClusterHost.cs. Run from the repository root through make benchmark-repl-local; extra
# repl-bench flags come from REPL_ARGS (for example REPL_ARGS='--docs 100000'); TERRABASE_BENCH_REGIONS=gru hosts one region.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d) # 0700: the targets file holds the nodes' tokens
targets="$work/cluster.json"
host_log="$work/host.log"
cleanup() {
  rm -f "$targets"
  if [[ -n "${host:-}" ]] && kill -0 "$host" 2>/dev/null; then
    wait "$host" || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

TERRABASE_BENCH_TARGETS="$targets" dotnet run --project "$root/tests/TerraBase.E2E" --configuration Release --no-build -- \
  --treenode-filter '/*/*/BenchmarkClusterHost/*' >"$host_log" 2>&1 &
host=$!
for _ in $(seq 1 180); do
  [[ -f "$targets" ]] && break
  if ! kill -0 "$host" 2>/dev/null; then
    tail -50 "$host_log"
    echo 'the cluster host exited before it was ready' >&2
    exit 1
  fi
  sleep 1
done
[[ -f "$targets" ]] || { tail -50 "$host_log"; echo 'the cluster host was not ready within 180 s' >&2; exit 1; }

cd "$root/benchmark"
status=0
# shellcheck disable=SC2086 # REPL_ARGS is a list of flags
bin/repl-bench --targets "$targets" --out bench ${REPL_ARGS:-} || status=$?
exit "$status"
