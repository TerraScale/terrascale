#!/usr/bin/env bash
# Publish a service's linux/arm64 runtime payload and print the runtime-only Dockerfile that wraps it.
set -euo pipefail
area=${2:?area required}
case $area in auth) binary=ts-auth ;; terrabase) binary=terrabase ;; *) exit 2 ;; esac
case ${1:-} in
  publish)
    # Production runs linux/arm64 only. Keep ReadyToRun and all normal compiler/analyzer checks enabled.
    output=$RUNNER_TEMP/image-payloads/arm64
    make "$area-publish" RUNTIME=linux-arm64 PUBLISH_DIR="$output"
    test -x "$output/$binary"
    test "$(od -An -tx1 -j18 -N2 "$output/$binary" | tr -d ' \n')" = b700
    ;;
  runtime-dockerfile)
    # Keep each area's runtime configuration identical to its source Dockerfile.
    sed -n "/ AS $binary\$/,/^FROM /p" Dockerfile | sed '1!{/^FROM /d;}' |
      sed -E "s/^COPY --from=publish-[^ ]+ \/out \/app$/ARG TARGETARCH\nCOPY \$\{TARGETARCH\}\/ \/app/"
    ;;
  *) echo 'Expected publish | runtime-dockerfile <auth|terrabase>' >&2; exit 2 ;;
esac
