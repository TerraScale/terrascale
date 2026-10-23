#!/usr/bin/env bash
# Peer bytes per replicated write on live cells: every second, each host's kernel TCP bytes_sent of the sockets on the
# peer port (sender side, so every byte counts once), around a repl-bench load run from one of the hosts.
#
#   HOSTS='ts-sjc-1;ts-fra-1;-J ts-gru-1 ubuntu@10.251.0.97' RUNNER=ts-sjc-1 TARGETS='~/rt2-laned/cell.json' \
#   BENCH='~/rt2-laned/repl-bench' PEER_PORT=18081 benchmark/scripts/repl-bytes-live.sh <docs> <size-bytes> [idle-seconds]
#
# HOSTS are ssh arguments separated by ';' (the runner included), TARGETS a repl-bench targets file on RUNNER. Prints one
# TSV line like repl-bytes.sh: size, docs, idle peer bytes/s, load seconds, peer bytes during load, bytes per write (idle
# subtracted), bytes per write per receiving region (two receivers), raw bytes per write per receiver. TCP payload only:
# NetBird (WireGuard, UDP, IP) adds per-packet headers on top. UNSAMPLED_IP names a peer without ssh: the sampled hosts
# also count the bytes they receive from it, so its sends still count once.
set -euo pipefail
docs=${1:?docs}
size=${2:?size in bytes}
idle=${3:-130}
: "${HOSTS:?}" "${RUNNER:?}" "${TARGETS:?}" "${BENCH:?}"
port=${PEER_PORT:-18081}
run=rt2-bytes-$(date +%s)-$$
IFS=';' read -ra hosts <<<"$HOSTS"
work=$(mktemp -d)
# Stops this run's samplers and removes its files on every host, also when the run fails.
cleanup() {
  for h in "${hosts[@]}"; do
    # shellcheck disable=SC2086 # ssh arguments
    ssh -o BatchMode=yes $h "pkill -f 'bash /tmp/$run[.]sh'; rm -rf /tmp/$run.sh /tmp/$run.samples /tmp/$run-bench /tmp/$run-bench.log" || true
  done
  rm -rf "$work"
}
trap cleanup EXIT

# Runs on each host: one line per peer socket per second, "time endpoints bytes_sent".
sampler=$(cat <<'SAMPLER'
while :; do
  t=$(date +%s.%N)
  ss -tinH state established "( sport = :$1 or dport = :$1 )" |
    awk -v t="$t" -v x="$2" '/bytes_sent/ { match($0, /bytes_sent:[0-9]+/); print t, key, substr($0, RSTART + 11, RLENGTH - 11)
      if (x != "" && key ~ x) { match($0, /bytes_received:[0-9]+/); print t, key "-rx", substr($0, RSTART + 15, RLENGTH - 15) }; next } { key = $3 "-" $4 }'
  sleep 1
done
SAMPLER
)
for i in "${!hosts[@]}"; do
  # shellcheck disable=SC2086 # ssh arguments
  ssh -o BatchMode=yes ${hosts[$i]} "cat > /tmp/$run.sh && (nohup timeout $((idle + 1800)) bash /tmp/$run.sh $port ${UNSAMPLED_IP:-} > /tmp/$run.samples 2>/dev/null &)" <<<"$sampler"
done

sleep 3
t0=$(date +%s.%N); sleep "$idle"; t1=$(date +%s.%N)
t2=$(date +%s.%N)
# A verify failure after a completed load still measured the load; anything else ends the run.
ssh -o BatchMode=yes "$RUNNER" "cd /tmp && $BENCH --targets $TARGETS --out /tmp/$run-bench --docs $docs --sizes $size:100 --batch 1 \
  --concurrency 4 --delete-pct 0 --conflict-docs 0 --probe-every 0 > /tmp/$run-bench.log 2>&1" ||
  ssh -o BatchMode=yes "$RUNNER" "grep -q 'end markers everywhere' /tmp/$run-bench.log && grep 'repl-bench converge' /tmp/$run-bench.log >&2" ||
  { ssh "$RUNNER" "tail -20 /tmp/$run-bench.log" >&2; exit 1; }
t3=$(date +%s.%N)
sleep 3
for i in "${!hosts[@]}"; do
  # shellcheck disable=SC2086 # ssh arguments
  ssh -o BatchMode=yes ${hosts[$i]} "cat /tmp/$run.samples" | sed "s/^/$i /" >>"$work/samples"
done

# Each socket's bytes at time T: its largest sample at or before T (0 when it did not exist yet).
awk -v t0="$t0" -v t1="$t1" -v t2="$t2" -v t3="$t3" -v size="$size" -v docs="$docs" '
  { k = $1 " " $3; for (i = 0; i < 4; i++) if ($2 <= T[i] && $4 > at[i, k]) at[i, k] = $4; keys[k] }
  BEGIN { T[0] = t0; T[1] = t1; T[2] = t2; T[3] = t3 }
  END {
    for (k in keys) for (i = 0; i < 4; i++) s[i] += at[i, k]
    rate = (s[1] - s[0]) / (t1 - t0); secs = t3 - t2; load = s[3] - s[2]; per = (load - rate * secs) / docs
    printf "%d\t%d\t%.0f\t%.1f\t%d\t%.0f\t%.0f\t%.0f\n", size, docs, rate, secs, load, per, per / 2, load / docs / 2 }' "$work/samples"
