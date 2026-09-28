#!/usr/bin/env bash
# Run one command with exclusive use of a persistent Cargo target directory.
#
# CI sets TS_AUTH_CARGO_CACHE_DIR to a directory on the runner's shared /cache
# volume so compiled dependencies survive between runs, and the command sees
# it as CARGO_TARGET_DIR. Concurrent jobs take turns on it, and a command
# copies out whatever it runs later before the lock is released, so no other
# job can rebuild those files underneath it. An oversized directory starts
# over rather than growing without bound. Without TS_AUTH_CARGO_CACHE_DIR the
# command runs as is.
#
# Rust compiles go through kache, whose store is shared by every job on the
# runner. Units an earlier run compiled, in any job, are restored instead of
# rebuilt, so a target directory that starts over costs restores rather than
# a full build. The store sits on the same /cache mount as the target
# directories, so restores are reflinks, not copies.
set -euo pipefail

test "$#" -gt 0 || { echo "usage: $0 COMMAND [ARG...]" >&2; exit 2; }
[[ -n ${TS_AUTH_CARGO_CACHE_DIR:-} ]] || exec "$@"
export CARGO_TARGET_DIR=$TS_AUTH_CARGO_CACHE_DIR
export RUSTC_WRAPPER=kache KACHE_CACHE_DIR=/cache/kache
# Sockets, locks, and event logs stay private to this job.
export KACHE_RUNTIME_DIR=${RUNNER_TEMP:?}/kache

repo=$(cd "$(dirname "$0")/../.." && pwd)
sdk=$(cd "$repo/../ts-rust" && pwd)
max_gib=${TS_AUTH_CARGO_CACHE_MAX_GIB:-20}
[[ $max_gib =~ ^[1-9][0-9]*$ ]] || { echo "TS_AUTH_CARGO_CACHE_MAX_GIB must be a positive integer" >&2; exit 2; }

mkdir -p -- "$CARGO_TARGET_DIR"
exec {lock}>"$CARGO_TARGET_DIR.lock"
if ! flock --nonblock "$lock"; then
  echo "waiting for the Cargo target cache at $CARGO_TARGET_DIR" >&2
  flock "$lock"
fi

# The ts-rust SDK compiles from one pinned revision checked out beside this
# repository. A checkout gives its files fresh mtimes, which alone would make
# Cargo rebuild the SDK every run, so the cache belongs to that exact revision
# and pins its mtimes instead.
sdk_revision=$(git -C "$sdk" rev-parse HEAD)
stamp="$CARGO_TARGET_DIR/.ts-rust-revision"
reason=
if [[ ! -f $stamp || $(<"$stamp") != "$sdk_revision" ]]; then
  reason="it was built from another ts-rust revision"
else
  used=$(du --summarize --bytes -- "$CARGO_TARGET_DIR" | cut -f1)
  (( used <= max_gib * 1024 * 1024 * 1024 )) || reason="it holds $used bytes (limit ${max_gib} GiB)"
fi
if [[ -n $reason ]]; then
  echo "starting the Cargo target cache at $CARGO_TARGET_DIR over: $reason" >&2
  find "$CARGO_TARGET_DIR" -mindepth 1 -delete
  printf '%s\n' "$sdk_revision" > "$stamp"
fi
sdk_time=$(git -C "$sdk" log -1 --format=%ct HEAD)
find "$sdk" -path "$sdk/.git" -prune -o -type f \
  -exec touch --no-dereference --date="@$sdk_time" -- {} +

exec "$@"
