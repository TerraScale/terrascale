#!/usr/bin/env bash
# Log chunks (RT3b): bytes over the peer links and through the object store, and object-store requests, per replicated
# change on three local regions (tests/TerraBase.E2E/BenchmarkClusterHost.cs with the real routes' one-way link delays).
# Each mode starts its own host: "bucket" nodes share one in-process S3 bucket, as production nodes share the platform
# bucket; "inline" nodes have none, the baseline. For each mode, scenario and repeat it scrapes every region's /metrics
# before and after one repl-bench load and prints the deltas, one TSV row per region. Run from the repository root through
# make benchmark-repl-chunks; REPEATS (default 3) and DOCS (default 10000) override the run size.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d) # 0700: the targets file holds the nodes' tokens
targets="$work/cluster.json"
cleanup() {
  rm -f "$targets"
  if [[ -n "${host:-}" ]] && kill -0 "$host" 2>/dev/null; then
    wait "$host" || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

start_host() {
  local store=none
  [[ $1 == bucket ]] && store=fake
  TERRABASE_BENCH_TARGETS="$targets" TERRABASE_BENCH_BUCKET="$store" TERRABASE_BENCH_LINK_DELAYS='gru-sjc=85,gru-fra=94,sjc-fra=69' \
    dotnet run --project "$root/tests/TerraBase.E2E" --configuration Release --no-build -- \
    --treenode-filter '/*/*/BenchmarkClusterHost/*' >"$work/host-$1.log" 2>&1 &
  host=$!
  for _ in $(seq 1 180); do
    [[ -f "$targets" ]] && return 0
    if ! kill -0 "$host" 2>/dev/null; then
      tail -50 "$work/host-$1.log"
      echo 'the cluster host exited before it was ready' >&2
      exit 1
    fi
    sleep 1
  done
  tail -50 "$work/host-$1.log"
  echo 'the cluster host was not ready within 180 s' >&2
  exit 1
}

stop_host() {
  rm -f "$targets"
  wait "$host" || true
  host=
}

# One line per region: peer bytes, store bytes, puts, gets, deletes (counters since the node started).
scrape() {
  for region in gru sjc fra; do
    url=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["regions"][sys.argv[2]]["metrics"])' "$targets" "$region")
    curl -fsS "${url%/}/metrics" | awk -v region="$region" '
      /^terrabase_replication_shipped_bytes_total\{/ && /path="peer"/ { peer += $NF }
      /^terrabase_replication_shipped_bytes_total\{/ && /path="store"/ { store += $NF }
      /^terrabase_replication_log_chunk_requests_total\{/ && /operation="put"/ { put += $NF }
      /^terrabase_replication_log_chunk_requests_total\{/ && /operation="get"/ { get += $NF }
      /^terrabase_replication_log_chunk_requests_total\{/ && /operation="delete"/ { del += $NF }
      END { printf "%s %d %d %d %d %d\n", region, peer, store, put, get, del }'
  done
}

docs=${DOCS:-10000}
printf 'mode\tscenario\trepeat\tregion\tdocs\tpeer_bytes\tstore_bytes\tputs\tgets\tdeletes\tseconds\n'
for mode in bucket none; do
  start_host "$mode"
  for scenario in 'small --batch 1 --sizes 1024:100' 'bulk --batch 50 --sizes 3072:100'; do
    read -r name args <<<"$scenario"
    for repeat in $(seq 1 "${REPEATS:-3}"); do
      before=$(scrape)
      started=$(date +%s%N)
      # shellcheck disable=SC2086 # args is a list of flags
      (cd "$root/benchmark" && bin/repl-bench --targets "$targets" --out "$work/reports" --name "chunks-$mode-$name-$repeat" --docs "$docs" \
        --concurrency 16 --conflict-docs 0 --probe-every 0 $args >"$work/bench-$mode-$name-$repeat.log" 2>&1) \
        || { tail -30 "$work/bench-$mode-$name-$repeat.log"; exit 1; }
      seconds=$(awk -v ns=$(($(date +%s%N) - started)) 'BEGIN { printf "%.1f", ns / 1e9 }')
      after=$(scrape)
      paste -d' ' <(echo "$before") <(echo "$after") | while read -r region p0 s0 u0 g0 d0 _ p1 s1 u1 g1 d1; do
        printf '%s\t%s\t%s\t%s\t%s\t%d\t%d\t%d\t%d\t%d\t%s\n' "$mode" "$name" "$repeat" "$region" "$docs" \
          $((p1 - p0)) $((s1 - s0)) $((u1 - u0)) $((g1 - g0)) $((d1 - d0)) "$seconds"
      done
    done
  done
  stop_host
done
