#!/usr/bin/env bash
# Verify the checked-in release pins, including cached/local assets, before consumption.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
output=${1:?usage: verify-turso-native.sh <native-directory>}
pins="$root/src/vendor/Turso.Native/SHA256SUMS"
expected=$(sed '/^#/d; /^$/d' "$pins")
if [[ $(wc -l <<< "$expected") != 2 ]] \
  || ! grep -qxE '[0-9a-f]{64}  terrascale-turso-linux-x64\.so' <<< "$expected" \
  || ! grep -qxE '[0-9a-f]{64}  terrascale-turso-linux-arm64\.so' <<< "$expected"; then
  echo 'Combined Turso release SHA pins are not populated: publication is incomplete.' >&2
  exit 1
fi
case "${2:-all}" in
  all) rids=(linux-x64 linux-arm64) ;;
  linux-x64|linux-arm64) rids=("$2") ;;
  *) echo 'Unsupported native RID' >&2; exit 1 ;;
esac
for rid in "${rids[@]}"; do
  asset="terrascale-turso-$rid.so"
  hash=$(awk -v asset="$asset" '$2 == asset { print $1 }' "$pins")
  file="runtimes/$rid/native/libturso_sdk_kit.so"
  (cd "$output" && printf '%s  %s\n' "$hash" "$file" | sha256sum --check --status)
  case "$rid" in linux-x64) machine=3e00 ;; linux-arm64) machine=b700 ;; esac
  test "$(od -An -tx1 -N6 "$output/$file" | tr -d ' \n')" = 7f454c460201
  test "$(od -An -tx1 -j18 -N2 "$output/$file" | tr -d ' \n')" = "$machine"
done
