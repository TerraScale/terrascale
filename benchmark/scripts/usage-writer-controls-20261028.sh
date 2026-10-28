#!/usr/bin/env bash
set -euo pipefail
umask 077
out=$PWD/benchmark/bench/usage-writer-controls-20261028-a
mkdir "$out"
cp "$0" "$out/run.sh"
git rev-parse HEAD > "$out/source.txt"
git -C "$TsDotnetRoot" rev-parse HEAD > "$out/sdk-source.txt"
bash scripts/vendor/download-turso-native.sh > "$out/native.log" 2>&1 || { tail -20 "$out/native.log"; exit 1; }
make terrabase-test-fast TEST_ARGS='--treenode-filter /*/*/UsageWriterTests/* --minimum-expected-tests 5' > "$out/check.log" 2>&1 || { tail -45 "$out/check.log"; exit 1; }
tail -20 "$out/check.log"
