#!/usr/bin/env bash
set -euo pipefail
if [[ $# == 0 ]]; then
  echo 'Usage: with-sdk.sh <command> [args...]' >&2
  exit 1
fi
client_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec 9>"$client_root/.sdk.lock"
flock --shared 9
bash "$client_root/prepare-sdk.sh" --verify
export TS_BENCH_SDK_LOCKED=1
# Keep the lock in this shell throughout the command, including MSBuild workers.
"$@"
