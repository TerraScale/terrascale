#!/usr/bin/env bash
# ClientCors.IsDataPath span matcher: Fast tests on both servers, predicate equivalence, then three alternating
# before/after pairs of the keyed api read lane under a quiet reservation. Before is main's ClientCors.cs; nothing else differs.
# Run through: TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cors-span-20261028.sh <run label> --repo terrascale --branch <branch>
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
label=${1:?usage: api-cors-span-20261028.sh <fresh run label>}
[[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid run label.' >&2; exit 2; }
sdk_pin=36d114877a69d18fba33b27ad362bd3e68e9e7ff
baseline=a5999f5eaddc608e908a92f3b35da3857dec0f1e
changed=src/TerraBase/Hosting/ClientCors.cs
cpus=2-11
out=$PWD/benchmark/bench/cors-span-$label
mkdir "$out"
exec > "$out/job.log" 2>&1 < /dev/null
# No build daemon may outlive a build into the timed arms.
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false DOTNET_CLI_USE_MSBUILD_SERVER=0
git rev-parse HEAD > "$out/candidate-sha.txt"
echo "$baseline" > "$out/baseline-sha.txt"
git merge-base --is-ancestor "$baseline" HEAD
# The only production input that differs is ClientCors.cs.
test "$(git diff --name-only "$baseline" HEAD -- src Directory.Build.props Directory.Build.targets Directory.Packages.props global.json)" = "$changed"
git diff --stat "$baseline" HEAD > "$out/diff-stat.txt"
git diff --quiet && git diff --cached --quiet
trap 'git checkout -q HEAD -- "$changed"' EXIT
sdk=$HOME/remote-builds/ts-dotnet/cors-span-$label
mkdir -p "$(dirname "$sdk")"
test ! -e "$sdk"
git clone --quiet --no-checkout https://github.com/TerraScale/ts-dotnet.git "$sdk"
git -C "$sdk" checkout --quiet --detach "$sdk_pin"
git -C "$sdk" rev-parse HEAD > "$out/sdk-sha.txt"
test "$(cat "$out/sdk-sha.txt")" = "$sdk_pin"
export TsDotnetRoot=$sdk
scripts/vendor/download-turso-native.sh > "$out/turso-native.log" 2>&1

# Fast (Debug, the Makefile default): the whole suite on the candidate, then the CORS cases on main's matcher.
make fmt-check > "$out/fmt-check.log" 2>&1 || { tail -40 "$out/fmt-check.log"; exit 1; }
make terrabase-test-fast > "$out/fast-candidate.log" 2>&1 || { tail -80 "$out/fast-candidate.log"; exit 1; }
git checkout -q "$baseline" -- "$changed"
make terrabase-test-fast TEST_ARGS="--treenode-filter /*/*/ClientCorsTests/*" > "$out/fast-baseline-cors.log" 2>&1 \
  || { tail -80 "$out/fast-baseline-cors.log"; exit 1; }
git checkout -q HEAD -- "$changed"

# Seeded equivalence of main's predicate and the span matcher, built under the repository's analyzers.
equivalence=$out/equivalence
mkdir "$equivalence"
cp benchmark/scripts/api-cors-path-equivalence.cs "$equivalence/Program.cs"
printf '%s\n' '<Project Sdk="Microsoft.NET.Sdk">' '  <PropertyGroup>' '    <OutputType>Exe</OutputType>' \
  '    <GenerateDocumentationFile>true</GenerateDocumentationFile>' '  </PropertyGroup>' '</Project>' > "$equivalence/CorsPathEquivalence.csproj"
dotnet build "$equivalence/CorsPathEquivalence.csproj" --configuration Release > "$equivalence/build.log" 2>&1 \
  || { tail -40 "$equivalence/build.log"; exit 1; }
dotnet exec "$equivalence/bin/Release/net11.0/CorsPathEquivalence.dll" > "$equivalence/result.txt"

build() {
  dotnet build tests/TerraBase.E2E --configuration Release > "$out/build-$1.log" 2>&1 || { tail -60 "$out/build-$1.log"; return 1; }
  mkdir "$out/$1"
  cp -a tests/TerraBase.E2E/bin/Release/net11.0/. "$out/$1/"
  config=$out/$1/TerraBase.E2E.runtimeconfig.json
  jq '.runtimeOptions.configProperties |= (del(."Microsoft.Extensions.DependencyInjection.DisableDynamicEngine")
    + {"System.GC.Server":false,"System.GC.Concurrent":true,"System.Runtime.TieredPGO":true})' "$config" > "$config.tmp"
  mv "$config.tmp" "$config"
  (cd "$out/$1" && sha256sum terrabase.dll TerraScale.DotNet.dll TerraBase.E2E.dll TerraBase.E2E.runtimeconfig.json) > "$out/$1.sha256"
}
git checkout -q "$baseline" -- "$changed"
build before
git checkout -q HEAD -- "$changed"
build after
sha256sum "$changed" > "$out/after-source.sha256"
git show "$baseline:$changed" | sha256sum > "$out/before-source.sha256"
# Only the server assembly may differ between the arms.
cmp "$out/before/TerraScale.DotNet.dll" "$out/after/TerraScale.DotNet.dll"
cmp "$out/before/TerraBase.E2E.runtimeconfig.json" "$out/after/TerraBase.E2E.runtimeconfig.json"
if cmp -s "$out/before/terrabase.dll" "$out/after/terrabase.dll"; then echo 'Server artifacts did not change.'; exit 1; fi
cmp "$out/before/TerraBase.E2E.dll" "$out/after/TerraBase.E2E.dll" > "$out/e2e-dll-cmp.txt" 2>&1 || true
dotnet build-server shutdown > "$out/build-server-shutdown.log" 2>&1 || true
cp benchmark/scripts/api-cpu-observer.py "$out/"
lscpu -e > "$out/cpu-topology.txt"

# Builds and tests are complete. Reserve the other admitted heavy slots without stopping work.
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
trap 'kill "$observer" "$vmstat" 2>/dev/null || true; git checkout -q HEAD -- "$changed"' EXIT

for repeat in 1 2 3; do
  arms=(before after)
  if ((repeat % 2 == 0)); then arms=(after before); fi
  for arm in "${arms[@]}"; do
    prefix=$out/$arm-$repeat
    (cd "$out/$arm" && sha256sum --check --quiet "$out/$arm.sha256")
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$prefix.host"
    date -u +%FT%T.%NZ > "$prefix.window"
    TERRABASE_VS_TURSO_LANES=api TERRABASE_VS_TURSO_WORKLOADS=read TERRABASE_VS_TURSO_CONCURRENCY=1,16 TERRABASE_VS_TURSO_SECONDS=5 \
      TERRABASE_VS_TURSO_REPEATS=1 TERRABASE_VS_TURSO_OUT="$prefix.md" taskset -c "$cpus" dotnet exec "$out/$arm/TerraBase.E2E.dll" \
      --treenode-filter '/*/*/VsTursoBenchmark/*' --minimum-expected-tests 1 > "$prefix.log" 2>&1 || { tail -60 "$prefix.log"; exit 1; }
    date -u +%FT%T.%NZ >> "$prefix.window"
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$prefix.host"
    echo "$arm pair $repeat completed."
  done
done
kill "$observer" "$vmstat" 2>/dev/null || true
python3 -I benchmark/scripts/api-cors-span-summary.py "$out" > "$out/summary.md"
echo 'All six arms completed.'
