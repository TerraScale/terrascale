#!/usr/bin/env bash
# Keyed-minus-keyless API diagnostic: one build, alternating fresh processes, quiet reservation.
# Run through: ts-heavy-any nice -n 19 benchmark/scripts/api-keyed-keyless-20261028.sh <run label> --repo terrascale --branch <branch>
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
label=${1:?usage: api-keyed-keyless-20261028.sh <fresh run label>}
[[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid run label.' >&2; exit 2; }
sdk_pin=36d114877a69d18fba33b27ad362bd3e68e9e7ff
server_source=93b873792
cpus=2-11
out=$PWD/benchmark/bench/keyed-keyless-$label
mkdir "$out"
exec > "$out/job.log" 2>&1 < /dev/null
git rev-parse HEAD > "$out/harness-sha.txt"
# Only benchmark wiring may differ from the recorded server source.
git diff --quiet "$server_source" HEAD -- src tests Directory.Build.props Directory.Build.targets Directory.Packages.props global.json
git rev-parse "$server_source^{commit}" > "$out/source-sha.txt"
git diff --stat "$server_source" HEAD > "$out/harness-diff.txt"
sdk=$HOME/remote-builds/ts-dotnet/keyed-keyless-$label
mkdir -p "$(dirname "$sdk")"
test ! -e "$sdk"
git clone --quiet --no-checkout https://github.com/TerraScale/ts-dotnet.git "$sdk"
git -C "$sdk" checkout --quiet --detach "$sdk_pin"
git -C "$sdk" rev-parse HEAD > "$out/sdk-sha.txt"
test "$(cat "$out/sdk-sha.txt")" = "$sdk_pin"
scripts/vendor/download-turso-native.sh > "$out/turso-native.log" 2>&1
# No shared compiler or node reuse: no build daemon outlives the build into the timed arms.
dotnet build tests/TerraBase.E2E --configuration Release -p:TsDotnetRoot="$sdk" -p:UseSharedCompilation=false -nodeReuse:false \
  > "$out/build.log" 2>&1 || { tail -60 "$out/build.log"; exit 1; }
bin=$out/bin
mkdir "$bin"
cp -a tests/TerraBase.E2E/bin/Release/net11.0/. "$bin/"
config=$bin/TerraBase.E2E.runtimeconfig.json
jq '.runtimeOptions.configProperties |= (del(."Microsoft.Extensions.DependencyInjection.DisableDynamicEngine")
  + {"System.GC.Server":false,"System.GC.Concurrent":true,"System.Runtime.TieredPGO":true})' "$config" > "$config.tmp"
mv "$config.tmp" "$config"
# Both arms execute these exact files; verify them before every arm.
(cd "$bin" && sha256sum terrabase.dll TerraScale.DotNet.dll TerraBase.E2E.dll TerraBase.E2E.runtimeconfig.json) > "$out/artifacts.sha256"
cp benchmark/scripts/api-cpu-observer.py "$out/"
lscpu -e > "$out/cpu-topology.txt"

# Builds are complete. Reserve the other admitted heavy slots without stopping work.
reserve_fds=()
for slot in 1 2 3 4; do
  exec {reserve_fd}>"/tmp/ts-heavy-$slot.lock"
  if flock -n "$reserve_fd"; then reserve_fds+=("$reserve_fd"); fi
done
if [[ ${#reserve_fds[@]} != 3 ]]; then
  echo 'Quiet reservation unavailable; no timed arm started.'; exit 73
fi
echo "Reserved ${#reserve_fds[@]} additional heavy slots for the timed arms." > "$out/reservation.txt"
python3 -I -u "$out/api-cpu-observer.py" "$$" > "$out/cpu-processes.jsonl" &
observer=$!
vmstat -t 1 > "$out/vmstat.txt" &
vmstat=$!
trap 'kill "$observer" "$vmstat" 2>/dev/null || true' EXIT

for repeat in 1 2 3; do
  arms=(keyed keyless)
  if ((repeat % 2 == 0)); then arms=(keyless keyed); fi
  for arm in "${arms[@]}"; do
    lane=api
    if [[ "$arm" == keyless ]]; then lane=api-keyless; fi
    prefix=$out/$arm-$repeat
    (cd "$bin" && sha256sum --check --quiet "$out/artifacts.sha256")
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$prefix.host"
    TERRABASE_VS_TURSO_LANES=$lane TERRABASE_VS_TURSO_WORKLOADS=read TERRABASE_VS_TURSO_SECONDS=5 TERRABASE_VS_TURSO_REPEATS=1 \
      TERRABASE_VS_TURSO_OUT="$prefix.md" taskset -c "$cpus" dotnet exec "$bin/TerraBase.E2E.dll" \
      --treenode-filter '/*/*/VsTursoBenchmark/*' --minimum-expected-tests 1 > "$prefix.log" 2>&1 || { tail -60 "$prefix.log"; exit 1; }
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$prefix.host"
    echo "$arm pair $repeat completed."
  done
done
echo 'All six arms completed.'
