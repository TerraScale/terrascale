#!/usr/bin/env bash
# Verify the checked-in release pins, including cached/local assets, before consumption.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
output=${1:?usage: verify-turso-native.sh <native-directory>}
pins="$root/src/vendor/Turso.Native/SHA256SUMS"
# Release asset name and installed file name of each pinned library.
libraries=("terrascale-turso:libturso_sdk_kit.so" "terrascale-sql-parser:libterrascale_sql_parser.so")
expected=$(sed '/^#/d; /^$/d' "$pins")
complete=$([[ $(wc -l <<< "$expected") == 4 ]] && echo true || echo false)
for rid in linux-x64 linux-arm64; do
  for library in "${libraries[@]}"; do
    grep -qxE "[0-9a-f]{64}  ${library%%:*}-$rid\\.so" <<< "$expected" || complete=false
  done
done
if [[ $complete != true ]]; then
  echo 'Combined Turso release SHA pins are not populated: publication is incomplete.' >&2
  exit 1
fi
case "${2:-all}" in
  all) rids=(linux-x64 linux-arm64) ;;
  linux-x64|linux-arm64) rids=("$2") ;;
  *) echo 'Unsupported native RID' >&2; exit 1 ;;
esac
for rid in "${rids[@]}"; do
  case "$rid" in linux-x64) machine=3e00 ;; linux-arm64) machine=b700 ;; esac
  for library in "${libraries[@]}"; do
    hash=$(awk -v asset="${library%%:*}-$rid.so" '$2 == asset { print $1 }' "$pins")
    file="runtimes/$rid/native/${library#*:}"
    (cd "$output" && printf '%s  %s\n' "$hash" "$file" | sha256sum --check --status)
    test "$(od -An -tx1 -N6 "$output/$file" | tr -d ' \n')" = 7f454c460201
    test "$(od -An -tx1 -j18 -N2 "$output/$file" | tr -d ' \n')" = "$machine"
  done
done
