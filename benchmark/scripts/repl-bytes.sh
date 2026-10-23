#!/usr/bin/env bash
# Peer-path bytes per replicated write on three local regions (tests/TerraBase.E2E/BenchmarkClusterHost.cs), read from the
# kernel's TCP counters (ss bytes_sent) of the host process. Every peer connection crosses one local PeerLink proxy, so each
# peer byte is sent twice inside the process; client sockets (the benchmark's own requests) are left out. Run from the
# repository root after `dotnet build tests/TerraBase.E2E -c Release` and `make -C benchmark repl-bench-build`:
#
#   benchmark/scripts/repl-bytes.sh <docs> <size-bytes> [idle-seconds]
#
# Prints one TSV line: size, docs, idle peer bytes/s, load seconds, peer bytes during load, bytes per write (idle
# subtracted), bytes per write per receiving region. Peer streams are h2c here; production adds TLS record overhead.
set -euo pipefail
docs=${1:?docs}
size=${2:?size in bytes}
idle=${3:-130}
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d) # 0700: the targets file holds the nodes' tokens
targets="$work/cluster.json"
cleanup() {
  [[ -n "${sampler:-}" ]] && kill "$sampler" 2>/dev/null
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
  kill -0 "$host" 2>/dev/null || { tail -50 "$work/host.log" >&2; exit 1; }
  sleep 1
done
[[ -f "$targets" ]] || { echo 'the cluster host was not ready within 180 s' >&2; exit 1; }
pid=$(jq -r .pid "$targets")
clients=$(jq -r '[.regions[].url | capture(":(?<p>[0-9]+)/?$").p] | join(" ")' "$targets")

# Every 0.5 s, each established host socket's bytes_sent (keyed by its two endpoints), except sockets on a client listener
# port; a socket's last sample stays counted after it closes.
sample() {
  while kill -0 "$host" 2>/dev/null; do
    ss -tinpH state established | awk -v pid="pid=$pid," -v clients=" $clients " '
      /users:/ { split($3, local, ":"); mine = index($0, pid) > 0 && index(clients, " " local[length(local)] " ") == 0; key = $3 "-" $4; next }
      mine && match($0, /bytes_sent:[0-9]+/) { print key, substr($0, RSTART + 11, RLENGTH - 11) }' >>"$work/samples"
    echo "--" >>"$work/samples"
    sleep 0.5
  done
}
sample &
sampler=$!
# Bytes sent so far: the sum of every socket's largest sample.
sent() {
  sleep 1
  awk '$1 != "--" { if ($2 > max[$1]) max[$1] = $2 } END { for (k in max) total += max[k]; printf "%d\n", total }' "$work/samples"
}

sleep 20 # startup catalog and refill traffic settles
s0=$(sent); sleep "$idle"; s1=$(sent)
s2=$(sent); started=$(date +%s.%N)
(cd "$root/benchmark" && bin/repl-bench --targets "$targets" --out "$work/bench" --docs "$docs" --sizes "$size:100" --batch 1 \
  --concurrency 4 --delete-pct 0 --conflict-docs 0 --probe-every 0 >"$work/bench.log" 2>&1) || { tail -30 "$work/bench.log" >&2; exit 1; }
s3=$(sent); ended=$(date +%s.%N)
awk -v size="$size" -v docs="$docs" -v idle="$idle" -v s0="$s0" -v s1="$s1" -v s2="$s2" -v s3="$s3" -v t0="$started" -v t1="$ended" 'BEGIN {
  rate = (s1 - s0) / 2 / idle; secs = t1 - t0; load = (s3 - s2) / 2; per = (load - rate * secs) / docs
  printf "%d\t%d\t%.0f\t%.1f\t%d\t%.0f\t%.0f\n", size, docs, rate, secs, load, per, per / 2 }'
