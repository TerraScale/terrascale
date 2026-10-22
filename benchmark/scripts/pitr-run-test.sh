#!/usr/bin/env bash
# Check source routing without starting nodes, reading credentials or contacting storage.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
eval "$(sed -n '/^pitrsrc() {$/,/^}$/p' "$here/pitr-run.sh")"
BASE='/unused/run with spaces'
TARGET=tigris
[[ $(pitrsrc "$BASE/s3/src-root") == '{"kind":"s3"}' ]]
for root in "$BASE/faults/deleted-segment" "$BASE/copies/pathb"; do
	[[ $(pitrsrc "$root" | jq -r '.kind + ":" + .root') == "dir:$root" ]]
done
TARGET=local-s3-emulator
[[ $(pitrsrc "$BASE/s3/src-root" | jq -r '.kind + ":" + .root') == "dir:$BASE/s3/src-root" ]]
echo 'pitr-run-test: source routing passed'
