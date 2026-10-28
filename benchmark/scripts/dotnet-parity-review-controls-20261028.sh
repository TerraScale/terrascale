#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/../.."
out=$PWD/benchmark/bench/dotnet-parity-review-controls-a
mkdir "$out"
git rev-parse HEAD > "$out/source-sha.txt"
cd benchmark/clients/dotnet
if ! make check > "$out/check.log" 2>&1; then
  tail -80 "$out/check.log"
  exit 1
fi
git -C .sdk rev-parse HEAD > "$out/sdk-sha.txt"
git -C .sdk diff --exit-code > "$out/sdk-diff.txt"
git diff --exit-code -- '*packages.lock.json' > "$out/lock-diff.txt"
sha256sum "$out/check.log"
tail -12 "$out/check.log"
