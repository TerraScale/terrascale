#!/usr/bin/env bash
# Build matching harnesses with original/candidate SDK sources, then alternate three public TLS control pairs.
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
sdk=$(realpath "${1:?usage: run-https.sh <clean SDK checkout> <before SDK ref> <output prefix>}")
before=${2:?before SDK ref}
prefix=$(realpath -m "${3:?output prefix}")
head=$(git -C "$sdk" rev-parse HEAD)
test -z "$(git -C "$sdk" status --porcelain --untracked-files=all)"
git -C "$sdk" diff --quiet
git -C "$sdk" diff --cached --quiet
files=(src/TerraScale.DotNet/TerraBase/TerraBaseConnection.cs src/TerraScale.DotNet/AuthService/TerraScaleAuthClient.cs)
restore() { git -C "$sdk" restore --source="$head" -- "${files[@]}"; }
trap restore EXIT
bin="$root/benchmark/dotnet/VsTurso/bin/https"
mkdir -p "$bin" "$(dirname "$prefix")"
"$root/scripts/vendor/download-turso-native.sh"
build() {
  dotnet build "$root/tests/TerraBase.E2E" --configuration Release -p:TsDotnetRoot="$sdk"
  mkdir -p "$bin/$1"
  cp -a "$root/tests/TerraBase.E2E/bin/Release/net11.0/." "$bin/$1/"
}
git -C "$sdk" restore --source="$before" -- "${files[@]}"
# Fail if any other compiled SDK source differs: this is an exact source baseline, not an assumed revert.
git -C "$sdk" diff --quiet "$before" -- src
build before
restore
git -C "$sdk" diff --quiet
build after
for repeat in 1 2 3; do
  arms=(before after)
  if ((repeat % 2 == 0)); then arms=(after before); fi
  for arm in "${arms[@]}"; do
    label=$head
    if [[ "$arm" == before ]]; then label=$(git -C "$sdk" rev-parse "$before"); fi
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$prefix-$arm-$repeat.host"
    TERRABASE_HTTPS_TRANSPORT_LABEL="$label" TERRABASE_HTTPS_TRANSPORT_FIRST="$repeat" \
      TERRABASE_HTTPS_TRANSPORT_REPEATS=1 TERRABASE_HTTPS_TRANSPORT_OUT="$prefix-$arm-$repeat.md" \
      dotnet exec "$bin/$arm/TerraBase.E2E.dll" --treenode-filter '/*/*/HttpsTransportBenchmark/*' --minimum-expected-tests 1
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$prefix-$arm-$repeat.host"
  done
done
