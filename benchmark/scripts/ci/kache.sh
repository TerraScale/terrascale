#!/usr/bin/env bash
# Run one command with Rust compiles routed through the runner's shared kache
# store on /cache, so units any earlier job compiled are restored rather than
# rebuilt. The cache is optional: when kache, the store, or a private runtime
# directory is unavailable or unwritable, run the command as is. The private
# runtime directory is removed when the command finishes.
set -euo pipefail

(( $# > 0 )) || { echo "usage: $0 COMMAND [ARG...]" >&2; exit 2; }
store=/cache/kache
if ! command -v kache >/dev/null || ! mkdir -p -- "$store" 2>/dev/null ||
   [[ ! -d $store || ! -w $store || ! -x $store ]]; then
  echo 'kache store unavailable; compiling without it' >&2
  exec "$@"
fi
runtime=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/kache.XXXXXX" 2>/dev/null) || {
  echo 'kache runtime directory unavailable; compiling without it' >&2
  exec "$@"
}
trap 'rm -rf -- "$runtime"' EXIT
export RUSTC_WRAPPER=kache KACHE_CACHE_DIR=$store KACHE_RUNTIME_DIR=$runtime
echo "compiling through kache at $store" >&2
"$@"
