#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
source "$root/benchmark/turso-sync-host/turso-pin.env"
[[ $TURSO_COMMIT =~ ^[0-9a-f]{40}$ ]]
[[ $TURSO_SHA256 =~ ^[0-9a-f]{64}$ ]]
output="$root/artifacts/router-parser"
mkdir -p "$output"
archive="$output/turso-${TURSO_COMMIT:0:8}.tar.gz"
if [[ ! -f "$archive" ]]; then
  curl --fail --location --retry 2 --proto '=https' --tlsv1.2 \
    "https://codeload.github.com/tursodatabase/turso/tar.gz/${TURSO_COMMIT}" -o "$archive"
fi
printf '%s  %s\n' "$TURSO_SHA256" "$archive" | sha256sum --check
mkdir -p "$output/turso"
tar -xzf "$archive" -C "$output/turso" --strip-components=1
mkdir -p "$output/native-turso"
tar -xzf "$archive" -C "$output/native-turso" --strip-components=1
patch --batch --fuzz=0 --forward --directory "$output/native-turso" -p1 < "$root/src/vendor/Turso.Native/sql/parser-limits.patch"
