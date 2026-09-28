#!/usr/bin/env bash
# Build auth-app as a static x86_64 musl executable with the job image's musl
# toolchain and stage it as DIST_DIR/ts-auth, the path the Containerfile
# copies. TS_AUTH_REVISION, reported in the /healthz and /readyz version, is
# the first 12 characters of the source commit. CI runs it under
# scripts/ci/cargo-cache.sh, or with a clean target directory to publish.
set -euo pipefail

dist=${1:-}
[[ -n $dist && $# -eq 1 ]] || { echo "usage: $0 DIST_DIR" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"

command -v readelf >/dev/null || { echo "readelf is required" >&2; exit 1; }
revision=$(git rev-parse HEAD)
[[ -z ${FORGEJO_SHA:-} || $revision == "$FORGEJO_SHA" ]] ||
  { echo "checkout $revision is not the event revision $FORGEJO_SHA" >&2; exit 1; }

target=x86_64-unknown-linux-musl
TS_AUTH_REVISION=${revision:0:12} cargo build --release --locked -p auth-app --target "$target"
binary=${CARGO_TARGET_DIR:-$repo/target}/$target/release/auth-app

metadata=$(readelf -hldW -- "$binary")
grep -Eq '^[[:space:]]*Machine:[[:space:]]+Advanced Micro Devices X86-64$' <<<"$metadata" ||
  { echo "$binary is not an x86-64 executable" >&2; exit 1; }
if grep -Eq 'INTERP|\((NEEDED|RPATH|RUNPATH)\)' <<<"$metadata"; then
  echo "$binary is not a self-contained static executable" >&2
  exit 1
fi

mkdir -p -- "$dist"
install -m 0755 -- "$binary" "$dist/ts-auth"
printf 'staged static %s ts-auth revision %s in %s\n' "$target" "${revision:0:12}" "$dist"
