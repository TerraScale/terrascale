#!/usr/bin/env bash
# Background work of an idle local three-region cell: sender passes, storage I/O calls, reads and writer holds per second
# on every node, with no client traffic, after extra databases exist: some placed in every region, some in gru and sjc only.
# Nodes come from tests/TerraBase.E2E/BenchmarkClusterHost.cs. Run from the repository root after
# `make terrabase-build CONFIGURATION=Release`:
#   benchmark/scripts/xregion/local-idle.sh <out-dir> [placed databases=20] [gru+sjc databases=20] [window seconds=30]
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
out=${1:?usage: local-idle.sh <out-dir> [placed] [single] [window]}
placed=${2:-20} single=${3:-20} window=${4:-30}
mkdir -p "$out"
work=$(mktemp -d)
targets="$work/cluster.json"
cleanup() {
  rm -f "$targets"
  if [[ -n "${host:-}" ]] && kill -0 "$host" 2>/dev/null; then
    wait "$host" || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

TERRABASE_BENCH_TARGETS="$targets" dotnet run --project "$root/tests/TerraBase.E2E" --configuration Release --no-build -- \
  --treenode-filter '/*/*/BenchmarkClusterHost/*' >"$work/host.log" 2>&1 &
host=$!
for _ in $(seq 1 180); do
  [[ -f "$targets" ]] && break
  kill -0 "$host" 2>/dev/null || { tail -50 "$work/host.log"; echo 'the cluster host exited' >&2; exit 1; }
  sleep 1
done
[[ -f "$targets" ]] || { tail -50 "$work/host.log"; echo 'the cluster host was not ready within 180 s' >&2; exit 1; }

regions=(gru sjc fra)
metrics() { jq -r ".regions.$1.metrics" "$targets" | sed 's#/$##'; }
scrape() {
  for r in "${regions[@]}"; do
    curl -fsS "$(metrics "$r")/metrics" >"$out/metrics-$1-$r.txt"
  done
  # The three nodes share one host process: its user and system CPU time, in clock ticks.
  awk '{print $14 + $15}' "/proc/$(jq -r .pid "$targets")/stat" >"$out/cpu-$1.txt"
}
python3 -I "$root/benchmark/scripts/xregion/local-idle.py" provision "$targets" "$placed" "$single" >"$out/provision.log" 2>&1 \
  || { cat "$out/provision.log"; exit 1; }
sleep 20 # let creation settle
scrape a
sleep "$window"
scrape b
python3 -I "$root/benchmark/scripts/xregion/local-idle.py" rates "$out" "$window" | tee "$out/rates.md"
