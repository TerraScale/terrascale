#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
output=${1:-"$root/src/vendor/Turso.Data.Sqlite/native"}
tag=turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029
pins="$root/src/vendor/Turso.Native/SHA256SUMS"
# Check pins before any network access; partial publication must fail closed.
test "$(grep -cE '^[0-9a-f]{64}  terrascale-turso-linux-(x64|arm64)\.so$' "$pins")" = 2 || {
  echo 'Combined Turso SHA pins are pending real artifact qualification.' >&2; exit 1;
}
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
for rid in linux-x64 linux-arm64; do
  asset="terrascale-turso-$rid.so"
  curl --fail --location --retry 3 --proto '=https' --tlsv1.2 \
    "https://github.com/TerraScale/terrascale/releases/download/$tag/$asset" -o "$scratch/$asset"
done
(cd "$scratch" && sha256sum --check "$pins")
for rid in linux-x64 linux-arm64; do
  mkdir -p "$scratch/runtimes/$rid/native"
  mv "$scratch/terrascale-turso-$rid.so" "$scratch/runtimes/$rid/native/libturso_sdk_kit.so"
done
"$root/scripts/vendor/verify-turso-native.sh" "$scratch"
for rid in linux-x64 linux-arm64; do
  mkdir -p "$output/runtimes/$rid/native"
  cp "$scratch/runtimes/$rid/native/libturso_sdk_kit.so" "$output/runtimes/$rid/native/"
done
"$root/scripts/vendor/verify-turso-native.sh" "$output"
