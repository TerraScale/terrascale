#!/usr/bin/env bash
# Frozen-source HTTPS control; run through ts-heavy-any nice -n 19.
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
previous=$PWD/benchmark/bench/https-merged-0018-quiet-2
out=$PWD/benchmark/bench/https-merged-0018-reserved
mkdir "$out"
bin=$PWD/benchmark/dotnet/VsTurso/bin/https
prefix=$out/control
cp "$previous/cpu-monitor.py" "$out/"
sha256sum -c "$previous/artifacts.sha256" > "$out/artifact-verification.txt"
cmp "$bin/before/TerraScale.DotNet.dll" "$bin/after/TerraScale.DotNet.dll" && exit 1
restore() { :; }
# Builds are complete. Reserve other admitted heavy slots without stopping work.
reserve_fds=()
for slot in 1 2 3 4; do
  exec {reserve_fd}>"/tmp/ts-heavy-$slot.lock"
  if flock -n "$reserve_fd"; then reserve_fds+=("$reserve_fd"); fi
done
if [[ ${#reserve_fds[@]} != 3 ]]; then
  echo 'Quiet reservation unavailable; no timed arm started.' >&2; exit 73
fi
out=$(dirname "$prefix")
sha256sum "$bin/before/TerraBase.E2E.dll" "$bin/after/TerraBase.E2E.dll" "$bin/before/TerraScale.DotNet.dll" "$bin/after/TerraScale.DotNet.dll" > "$out/artifacts.sha256"
python3 -u "$out/cpu-monitor.py" "$$" > "$out/cpu-processes.jsonl" &
observer=$!
trap 'kill "$observer" 2>/dev/null || true; restore' EXIT

for repeat in 1 2 3; do
 arms=(before after)
 if ((repeat%2==0));then arms=(after before);fi
 for arm in "${arms[@]}";do
  label=36d114877a69d18fba33b27ad362bd3e68e9e7ff
  if [[ "$arm" == before ]];then label=fba075bcba060afb560c711b395385626df91161;fi
  { date -u +%FT%TZ;uptime;cat /proc/pressure/cpu; } > "$prefix-$arm-$repeat.host"
  TERRABASE_HTTPS_TRANSPORT_LABEL="$label" TERRABASE_HTTPS_TRANSPORT_FIRST="$repeat" TERRABASE_HTTPS_TRANSPORT_REPEATS=1 TERRABASE_HTTPS_TRANSPORT_OUT="$prefix-$arm-$repeat.md" dotnet exec "$bin/$arm/TerraBase.E2E.dll" --treenode-filter '/*/*/HttpsTransportBenchmark/*' --minimum-expected-tests 1 > "$prefix-$arm-$repeat.log" 2>&1 || { tail -45 "$prefix-$arm-$repeat.log";exit 1; }
  { date -u +%FT%TZ;uptime;cat /proc/pressure/cpu; } >> "$prefix-$arm-$repeat.host"
  echo "$arm reserved HTTPS pair $repeat completed."
 done
done
