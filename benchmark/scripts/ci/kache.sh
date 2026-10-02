#!/usr/bin/env bash
# Run one command with Rust compiles routed through the runner's shared kache
# store on /cache, so units any earlier job compiled are restored rather than
# rebuilt. Without that store or kache, run the command as is.
set -euo pipefail

(( $# > 0 )) || { echo "usage: $0 COMMAND [ARG...]" >&2; exit 2; }
if [[ -d /cache && -w /cache ]] && command -v kache >/dev/null; then
  export RUSTC_WRAPPER=kache KACHE_CACHE_DIR=/cache/kache
  export KACHE_RUNTIME_DIR=${RUNNER_TEMP:-/tmp}/kache
  mkdir -p -- "$KACHE_CACHE_DIR" "$KACHE_RUNTIME_DIR"
  echo "compiling through kache at $KACHE_CACHE_DIR" >&2
fi
exec "$@"
