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
# Dev-host checkouts can already contain these exact pinned libraries. Verify
# them before reuse; an incomplete or corrupted copy still needs a download.
if "$root/scripts/vendor/verify-turso-native.sh" "$output" >/dev/null 2>&1; then
  echo 'Reusing verified pinned Turso and SQL parser libraries.'
  exit 0
fi
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
for rid in linux-x64 linux-arm64; do
  for library in "${libraries[@]}"; do
    asset="${library%%:*}-$rid.so"
    # GitHub release assets can return a burst of cached 5xx responses. Curl's
    # exponential delays span 63 seconds; both attempts and elapsed time are bounded.
    curl --fail --location --retry 6 --retry-max-time 120 --connect-timeout 10 --max-time 30 --proto '=https' --tlsv1.2 \
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
