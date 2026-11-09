#!/usr/bin/env bash
# Run the driver from only its bundled executable; a publish-sidecar cannot satisfy native loading.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
case "$(uname -m)" in x86_64) rid=linux-x64 ;; aarch64) rid=linux-arm64 ;; *) exit 1 ;; esac
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
dotnet publish "$root/tests/Turso.Data.Sqlite.Tests" --configuration Release --runtime "$rid" \
  --self-contained true --no-restore --output "$scratch/publish"
test ! -f "$scratch/publish/libturso_sdk_kit.so"
test ! -f "$scratch/publish/libterrascale_sql_parser.so"
mkdir "$scratch/isolated"
cp "$scratch/publish/Turso.Data.Sqlite.Tests" "$scratch/isolated/"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$scratch/extracted"
(cd "$scratch/isolated" && ./Turso.Data.Sqlite.Tests --minimum-expected-tests 3)
for library in terrascale-turso:libturso_sdk_kit.so terrascale-sql-parser:libterrascale_sql_parser.so; do
  extracted=$(find "$scratch/extracted" -type f -name "${library#*:}")
  test -n "$extracted"
  expected=$(awk -v asset="${library%%:*}-$rid.so" '$2 == asset { print $1 }' "$root/src/vendor/Turso.Native/SHA256SUMS")
  printf '%s  %s\n' "$expected" "$extracted" | sha256sum --check
done
