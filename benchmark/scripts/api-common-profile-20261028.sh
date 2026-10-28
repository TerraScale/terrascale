#!/usr/bin/env bash
# Common API overhead profile: the frozen keyed-keyless-b1 binary, keyed api lane, small reads at c=1 and c=16 profiled in two
# fresh processes under a quiet reservation. No build; benchmark semantics, attribution and the engine denominator are unchanged.
# Run through: TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-common-profile-20261028.sh <label> --repo terrascale --branch <branch>
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
label=${1:?usage: api-common-profile-20261028.sh <fresh run label>}
[[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid run label.' >&2; exit 2; }
frozen=$HOME/remote-builds/terrascale/fix-api-keyed-keyless-diagnostic-1720f17fb2ad64c0a1ff24bf490ba4c12df402f3b30f6b7337e4d8c7bdac56b4/benchmark/bench/keyed-keyless-b1
bin=$frozen/bin
tool=$HOME/.dotnet/tools/dotnet-trace
cpus=2-11
out=$PWD/benchmark/bench/common-profile-$label
mkdir "$out"
exec > "$out/job.log" 2>&1 < /dev/null
git rev-parse HEAD > "$out/harness-sha.txt"
# The frozen build: production source 93b873792, harness f005f0081, ts-dotnet 36d114877a69d18fba33b27ad362bd3e68e9e7ff.
test "$(cat "$frozen/source-sha.txt")" = 93b873792678c32fd14ae1aebc4fa9717eae79c3
test "$(cat "$frozen/sdk-sha.txt")" = 36d114877a69d18fba33b27ad362bd3e68e9e7ff
cp "$frozen/artifacts.sha256" "$frozen/source-sha.txt" "$frozen/sdk-sha.txt" "$out/"
cp "$frozen/harness-sha.txt" "$out/frozen-harness-sha.txt"
test "$(sha256sum < "$bin/terrabase.dll" | cut -d' ' -f1)" = c1175b9eca103e93a8d1e36d83c5c9489c34ae4d3f0c058a71fa72f1e79c7fbf
"$tool" --version > "$out/tool-version.txt"
sha256sum "$(readlink -f "$tool")" > "$out/tool.sha256"
cp benchmark/scripts/api-common-profile.py benchmark/scripts/api-cpu-observer.py "$out/"
grep -q 0x80000000019 "$out/api-common-profile.py" && : > "$out/alloc-sampling"
lscpu -e > "$out/cpu-topology.txt"

# Reserve the other admitted heavy slots without stopping work.
reserve_fds=()
for slot in 1 2 3 4; do
  exec {reserve_fd}>"/tmp/ts-heavy-$slot.lock"
  if flock -n "$reserve_fd"; then reserve_fds+=("$reserve_fd"); fi
done
if [[ ${#reserve_fds[@]} != 3 ]]; then
  echo 'Quiet reservation unavailable; no profile started.'; exit 73
fi
echo "Reserved ${#reserve_fds[@]} additional heavy slots for the profiled processes." > "$out/reservation.txt"
python3 -I -u "$out/api-cpu-observer.py" "$$" > "$out/cpu-processes.jsonl" &
observer=$!
vmstat -t 1 > "$out/vmstat.txt" &
vmstat=$!
bench=''
finish() {
  kill "$observer" "$vmstat" 2>/dev/null || true
  if [[ -n "$bench" ]]; then kill "$bench" 2>/dev/null || true; wait "$bench" 2>/dev/null || true; fi
}
trap finish EXIT

for run in a b; do
  mkdir "$out/$run"
  (cd "$bin" && sha256sum --check --quiet "$frozen/artifacts.sha256")
  { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$out/$run/host.txt"
  TERRABASE_VS_TURSO_LANES=api TERRABASE_VS_TURSO_WORKLOADS=read TERRABASE_VS_TURSO_CONCURRENCY=1,16 TERRABASE_VS_TURSO_SECONDS=45 \
    TERRABASE_VS_TURSO_REPEATS=1 TERRABASE_VS_TURSO_OUT="$out/$run/cells.md" taskset -c "$cpus" dotnet exec "$bin/TerraBase.E2E.dll" \
    --treenode-filter '/*/*/VsTursoBenchmark/*' --minimum-expected-tests 1 > "$out/$run/benchmark.log" 2>&1 &
  bench=$!
  echo "$bench" > "$out/$run/owned.pid"
  python3 -I -u "$out/api-common-profile.py" "$out/$run" "$bench" "$out/$run/cells.md" "$tool" > "$out/$run/profile.log" 2>&1 \
    || { cat "$out/$run/profile.log"; exit 1; }
  wait "$bench" || { tail -60 "$out/$run/benchmark.log"; exit 1; }
  bench=''
  { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$out/$run/host.txt"
  echo "Profiled process $run completed."
done
(cd "$out" && sha256sum ./*/*.nettrace ./*/cells.md ./*/proc.jsonl > traces.sha256)
echo 'Both profiled processes completed.'
