#!/usr/bin/env bash
# Production replication lag in all six directions between gru, sjc and fra (xregion-probe, one client per region).
#
#   scripts/terrabase/xregion-lag.sh <out-dir> [repeats=3] [probes=60] [load-writers=2]
#
# Run from the repository root on a workstation with ssh access to ts-gru-1, ts-sjc-1 and ts-fra-1. The probe runs on
# each destination host as an unprivileged, niced process; it changes nothing on the hosts beyond ~/xregion-lag. As the
# platform operator on the internal listeners (the shared peer credential read from the local TerraBase task into a
# private header file, never printed), it creates a synthetic tenant with an all-regions database, runs idle and loaded probes for every direction and
# deletes the tenant afterwards. Raw runs land in <out-dir>/<origin>-<destination>.<mode>.<run>.jsonl.
set -euo pipefail
out=${1:?usage: xregion-lag.sh <out-dir> [repeats] [probes] [load-writers]}
repeats=${2:-3} probes=${3:-60} load=${4:-2}
here=$(cd "$(dirname "$0")" && pwd)
declare -A host=([gru]=ts-gru-1 [sjc]=ts-sjc-1 [fra]=ts-fra-1)
mkdir -p "$out"
bin=$(mktemp -d)
(cd "$here/../../benchmark" && nice -n 19 env GOOS=linux GOARCH=arm64 CGO_ENABLED=0 go build -trimpath -o "$bin/xregion-probe" ./cmd/xregion-probe)

# The operator header and the peers file, from each host's running TerraBase task.
prepare='set -euo pipefail; umask 077; mkdir -p ~/xregion-lag; cd ~/xregion-lag
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '\''$2 ~ /terrascale\/terrabase@/ {print $1}'\'' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '\''$3 == "RUNNING" {print $1}'\'' | sort) | head -1)
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '\''$1 == c {print $2}'\'')
secret=$(sudo -n cat "/proc/$pid/environ" | tr "\0" "\n" | sed -n "s/^TERRABASE_PEER_SECRET_FILE=//p")
printf "X-TerraBase-Peer-Credential: %s\n" "$(sudo -n cat "/proc/$pid/root$secret")" > peer
sudo -n cat "/proc/$pid/root/run/ts-orch/peers/peers" > peers'
for region in "${!host[@]}"; do
  ssh "${host[$region]}" "$prepare"
  scp -q "$bin/xregion-probe" "$here/xregion-lag.py" "${host[$region]}:xregion-lag/"
done
user=$(python3 -c 'import os,time
a="0123456789ABCDEFGHJKMNPQRSTVWXYZ"; n=(int(time.time()*1000)<<80)|int.from_bytes(os.urandom(10),"big")
print("".join(a[(n>>(5*i))&31] for i in range(25,-1,-1)))')
ssh ts-sjc-1 "echo 'X-TerraBase-User-Id: $user' > ~/xregion-lag/user"
teardown() { ssh ts-sjc-1 'cd ~/xregion-lag && python3 -I xregion-lag.py teardown .' || true; for r in "${!host[@]}"; do ssh "${host[$r]}" 'rm -f ~/xregion-lag/peer' || true; done; rm -rf -- "$bin"; }
trap teardown EXIT
tenant=$(ssh ts-sjc-1 'cd ~/xregion-lag && python3 -I xregion-lag.py provision .' | tail -1)
echo "$tenant" | tee "$out/tenant.json"
project=$(jq -r .project <<<"$tenant") database=$(jq -r .database <<<"$tenant")
declare -A ip
while read -r _ region address; do ip[$region]=$address; done < <(ssh ts-sjc-1 'cat ~/xregion-lag/peers')
ssh ts-sjc-1 "curl -sS --http2-prior-knowledge -H @\$HOME/xregion-lag/peer http://${ip[sjc]}:8081/readyz" > "$out/readyz.json"

for run in $(seq 1 "$repeats"); do
  for mode in idle load; do
    for to in gru sjc fra; do
      for from in gru sjc fra; do
        [ "$from" = "$to" ] && continue
        flags=""; [ "$mode" = load ] && flags="-load $load"
        echo "run $run $mode $from -> $to"
        ssh "${host[$to]}" "cd ~/xregion-lag && nice -n 19 ./xregion-probe -h2c -origin http://${ip[$from]}:8081 \
          -local http://${ip[$to]}:8081 -label $from-\>$to -project $project -database $database -collection probe \
          -header-file peer -n $probes $flags" > "$out/$from-$to.$mode.$run.jsonl"
      done
    done
  done
done
python3 -I "$here/xregion-lag.py" summary "$out" | tee "$out/summary.md"
