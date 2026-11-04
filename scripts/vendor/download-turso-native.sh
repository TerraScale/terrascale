#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
output=${1:-"$root/src/vendor/Turso.Data.Sqlite/native"}
tag=turso-native-v0.8.2-fts-contract1-aegis-wal1-interrupt-synccdc1-localenc1-upsertcdc1-sqlparser1-20261104
pins="$root/src/vendor/Turso.Native/SHA256SUMS"
# Release asset name and installed file name of each pinned library.
libraries=("terrascale-turso:libturso_sdk_kit.so" "terrascale-sql-parser:libterrascale_sql_parser.so")
# Check pins before any network access; partial publication must fail closed.
test "$(grep -cE '^[0-9a-f]{64}  terrascale-(turso|sql-parser)-linux-(x64|arm64)\.so$' "$pins")" = 4 || {
  echo 'Combined Turso SHA pins are pending real artifact qualification.' >&2; exit 1;
}
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
for rid in linux-x64 linux-arm64; do
  for library in "${libraries[@]}"; do
    asset="${library%%:*}-$rid.so"
    curl --fail --location --retry 3 --proto '=https' --tlsv1.2 \
      "https://github.com/TerraScale/terrascale/releases/download/$tag/$asset" -o "$scratch/$asset"
  done
done
(cd "$scratch" && sha256sum --check "$pins")
for rid in linux-x64 linux-arm64; do
  mkdir -p "$scratch/runtimes/$rid/native"
  for library in "${libraries[@]}"; do
    mv "$scratch/${library%%:*}-$rid.so" "$scratch/runtimes/$rid/native/${library#*:}"
  done
done
"$root/scripts/vendor/verify-turso-native.sh" "$scratch"
for rid in linux-x64 linux-arm64; do
  mkdir -p "$output/runtimes/$rid/native"
  for library in "${libraries[@]}"; do
    cp "$scratch/runtimes/$rid/native/${library#*:}" "$output/runtimes/$rid/native/"
  done
done
"$root/scripts/vendor/verify-turso-native.sh" "$output"
