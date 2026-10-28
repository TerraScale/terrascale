#!/usr/bin/env bash
# Runs on one ts-orch serving host; all credentials stay in its private temporary directory.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
out=$(realpath -m "${1:?output directory}")
probe=$(realpath "${2:?xregion-probe executable}")
sha=${3:?full harness SHA}
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || { echo 'expected full harness commit SHA'; exit 1; }
mkdir -p "$(dirname "$out")"
if [[ ${4:-} != cleanup ]]; then mkdir "$out"; fi
umask 077
exec 9>"$out/.lock"
flock -n 9 || { echo 'this output still has an active benchmark'; exit 1; }
# A deterministic private tmpfs directory lets recovery remove SIGKILL leftovers.
work="/dev/shm/terrascale-perf-$UID-$(printf '%s' "$out" | sha256sum | cut -c 1-32)"
if [[ ${4:-} == cleanup ]]; then rm -rf -- "$work"; fi
mkdir "$work"
job=
cleanup() {
  if [[ -n "$job" ]] && kill -0 "$job" 2>/dev/null; then
    kill -TERM "$job" 2>/dev/null || true
    wait "$job" || true
  fi
  rm -rf -- "$work"
}
trap cleanup EXIT
trap 'exit 143' TERM
trap 'exit 130' INT
trap 'exit 129' HUP
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort))
[[ -n "$container" && "$container" != *$'\n'* ]] || { echo 'expected one running TerraBase'; exit 1; }
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
environment=$(sudo -n cat "/proc/$pid/environ" | tr '\0' '\n')
secret_file=$(sed -n 's/^TERRABASE_PEER_SECRET_FILE=//p' <<<"$environment")
peers_file=$(sed -n 's/^TS_PEERS_FILE=//p' <<<"$environment")
region=$(sed -n 's/^TS_REGION=//p' <<<"$environment")
unset environment
[[ -n "$region" && -n "$peers_file" && -n "$secret_file" ]] || { echo 'missing ts-orch identity'; exit 1; }
credential=$(sudo -n cat "/proc/$pid/root$secret_file")
[[ -n "$credential" ]] || { echo 'empty delivered peer credential'; exit 1; }
printf 'X-TerraBase-Peer-Credential: %s\n' "$credential" >"$work/peer"
unset credential
sudo -n cat "/proc/$pid/root$peers_file" >"$work/peers"
if [[ ${4:-} != cleanup ]]; then
python3 - "$out/user" <<'PY'
import pathlib, secrets, sys, time
n = (int(time.time()*1000) << 80) | secrets.randbits(80)
identifier = ''.join('0123456789ABCDEFGHJKMNPQRSTVWXYZ'[(n >> i) & 31] for i in range(125, -1, -5))
pathlib.Path(sys.argv[1]).write_text('X-TerraBase-User-Id: ' + identifier + '\n')
PY
fi
cp "$out/user" "$work/user"
python3 -I "$root/benchmark/scripts/perf-live.py" "$work" "$region" "$sha" "$out/live.jsonl" "$probe" \
  "$root/scripts/terrabase/endpoint-replication-proof.py" "${@:4}" &
job=$!
wait "$job"
job=
