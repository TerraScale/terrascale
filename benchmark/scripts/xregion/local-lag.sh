#!/usr/bin/env bash
# Replication lag on a local three-region cell whose peer links carry the real one-way delays: xregion-probe in all six
# directions, interleaved per run, and every node's /metrics before and after (step durations, remote ack delay, catalog
# cache lookups). Nodes come from tests/TerraBase.E2E/BenchmarkClusterHost.cs. Run from the repository root after
# `make terrabase-build CONFIGURATION=Release` and `make -C benchmark` builds of cmd/xregion-probe:
#   benchmark/scripts/xregion/local-lag.sh <out-dir> [runs] [probes per direction per run] [load writers on the origin]
#     [interval between one writer's writes, e.g. 50ms; 0 = back to back]
# then benchmark/scripts/xregion/local-lag-table.py <out-dir>. TERRABASE_BENCH_LINK_DELAYS overrides the delays (ms).
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
out=${1:?usage: local-lag.sh <out-dir> [runs] [probes]}
runs=${2:-3}
probes=${3:-60}
load=${4:-0}
load_every=${5:-0}
delays=${TERRABASE_BENCH_LINK_DELAYS:-gru-sjc=84.6,gru-fra=94.1,sjc-fra=68.9} # RT1's TCP RTT/2 floors
mkdir -p "$out"
work=$(mktemp -d) # 0700: the targets file and key files hold the nodes' tokens
targets="$work/cluster.json"
cleanup() {
  rm -f "$targets"
  if [[ -n "${host:-}" ]] && kill -0 "$host" 2>/dev/null; then
    wait "$host" || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

TERRABASE_BENCH_LINK_DELAYS="$delays" TERRABASE_BENCH_TARGETS="$targets" \
  dotnet run --project "$root/tests/TerraBase.E2E" --configuration Release --no-build -- \
  --treenode-filter '/*/*/BenchmarkClusterHost/*' >"$work/host.log" 2>&1 &
host=$!
for _ in $(seq 1 180); do
  [[ -f "$targets" ]] && break
  kill -0 "$host" 2>/dev/null || { tail -50 "$work/host.log"; echo 'the cluster host exited' >&2; exit 1; }
  sleep 1
done
[[ -f "$targets" ]] || { tail -50 "$work/host.log"; echo 'the cluster host was not ready within 180 s' >&2; exit 1; }

project=$(jq -r .project "$targets")
database=$(jq -r .database "$targets")
regions=(gru sjc fra)
for r in "${regions[@]}"; do
  jq -r ".regions.$r.token" "$targets" >"$work/$r.key"
done
url() { jq -r ".regions.$1.url" "$targets" | sed 's#/$##'; }
db="/v1/projects/$project/databases/$database"
curl -fsS -X POST -H "Authorization: Bearer $(cat "$work/gru.key")" -H 'Content-Type: application/json' \
  -d '{"name":"probe"}' "$(url gru)$db/collections" >/dev/null
for r in sjc fra; do
  for _ in $(seq 1 120); do
    curl -fsS -o /dev/null -H "Authorization: Bearer $(cat "$work/$r.key")" "$(url "$r")$db/collections/probe" && break
    sleep 1
  done
done

scrape() {
  for r in "${regions[@]}"; do
    curl -fsS "$(jq -r ".regions.$r.metrics" "$targets" | sed 's#/$##')/metrics" >"$out/metrics-$1-$r.txt"
  done
}
echo "$delays" >"$out/delays.txt"
git -C "$root" rev-parse HEAD >"$out/commit.txt"
scrape before
for run in $(seq 1 "$runs"); do
  for a in "${regions[@]}"; do
    for b in "${regions[@]}"; do
      [[ "$a" == "$b" ]] && continue
      "$root/benchmark/bin/xregion-probe" -origin "$(url "$a")" -local "$(url "$b")" -key-file "$work/$a.key" \
        -local-key-file "$work/$b.key" -label "$a->$b" -project "$project" -database "$database" -collection probe \
        -n "$probes" -rtt-samples 3 -load "$load" -load-every "$load_every" >"$out/probe-$run-$a-$b.jsonl"
    done
  done
done
scrape after
