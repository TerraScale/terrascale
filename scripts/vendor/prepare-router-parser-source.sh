#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
output="$root/artifacts/router-parser"
mkdir -p "$output"
archive="$output/turso-5c168be5.tar.gz"
if [[ ! -f "$archive" ]]; then
  curl --fail --location --retry 2 --proto '=https' --tlsv1.2 \
    https://codeload.github.com/tursodatabase/turso/tar.gz/5c168be5ef6a32ac966daf5983502a4f694b37ad -o "$archive"
fi
printf '7ea83d5e28577cf9d8655b77705902379e06b82fb29424f6ea328b5f26f17ecb  %s\n' "$archive" | sha256sum --check
mkdir -p "$output/turso"
tar -xzf "$archive" -C "$output/turso" --strip-components=1
