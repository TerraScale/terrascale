#!/usr/bin/env bash
# Compare one server change with identical harness/SDK sources and verified different server artifacts.
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
sdk=$(realpath "${1:?usage: run-server-overhead.sh <SDK checkout> <before server ref> <output directory> <changed src file...>}")
before=${2:?before server ref}
out=$(realpath -m "${3:?output directory}")
cd "$root"
head=$(git rev-parse HEAD)
files=("${@:4}")
test "${#files[@]}" -gt 0
for file in "${files[@]}"; do
  [[ "$file" == src/TerraBase/* && "$file" != *../* ]]
done
git diff --quiet -- "${files[@]}"
git diff --cached --quiet -- "${files[@]}"
restore() { git restore --source="$head" -- "${files[@]}"; }
trap restore EXIT
mkdir -p "$out"
scripts/vendor/download-turso-native.sh
build() {
  # Explicit propagation: callers capturing build logs must never turn a failed build into a stale-artifact run.
  dotnet build tests/TerraBase.E2E --configuration Release -p:TsDotnetRoot="$sdk" || return $?
  mkdir -p "$out/$1"
  cp -a tests/TerraBase.E2E/bin/Release/net11.0/. "$out/$1/"
  config=$out/$1/TerraBase.E2E.runtimeconfig.json
  jq '.runtimeOptions.configProperties |= (del(."Microsoft.Extensions.DependencyInjection.DisableDynamicEngine")
    + {"System.GC.Server":false,"System.GC.Concurrent":true,"System.Runtime.TieredPGO":true})' "$config" > "$config.tmp"
  mv "$config.tmp" "$config"
}
git restore --source="$before" -- "${files[@]}"
git diff --quiet "$before" -- src
build before > "$out/build-before.log" 2>&1 || { tail -60 "$out/build-before.log"; exit 1; }
restore
build after > "$out/build-after.log" 2>&1 || { tail -60 "$out/build-after.log"; exit 1; }
before_hash=$(sha256sum "$out/before/terrabase.dll")
after_hash=$(sha256sum "$out/after/terrabase.dll")
test "${before_hash%% *}" != "${after_hash%% *}" || { echo 'Server artifacts did not change'; exit 1; }
cmp "$out/before/TerraScale.DotNet.dll" "$out/after/TerraScale.DotNet.dll"
printf '%s\n%s\n' "$before_hash" "$after_hash" > "$out/artifacts.sha256"
for repeat in 1 2 3; do
  arms=(before after)
  if ((repeat % 2 == 0)); then arms=(after before); fi
  for arm in "${arms[@]}"; do
    prefix=$out/$arm-$repeat
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$prefix.host"
    TERRABASE_VS_TURSO_LANES=${TERRABASE_VS_TURSO_LANES:-engine,api} TERRABASE_VS_TURSO_WORKLOADS=read \
      TERRABASE_VS_TURSO_SECONDS=5 TERRABASE_VS_TURSO_REPEATS=1 TERRABASE_VS_TURSO_OUT="$prefix.md" \
      dotnet exec "$out/$arm/TerraBase.E2E.dll" --treenode-filter '/*/*/VsTursoBenchmark/*' \
      --minimum-expected-tests 1 > "$prefix.log" 2>&1 || { tail -60 "$prefix.log"; exit 1; }
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$prefix.host"
    echo "$arm pair $repeat completed."
  done
done
