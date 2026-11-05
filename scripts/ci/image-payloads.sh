#!/usr/bin/env bash
# Reuse Fast's checked/published output; the release job only wraps runtime payloads.
set -euo pipefail
case ${1:-} in
  auth)
    mkdir -p "$RUNNER_TEMP/image-payloads/amd64"
    cp -a "$RUNNER_TEMP/auth-publish/." "$RUNNER_TEMP/image-payloads/amd64/"
    make auth-publish RUNTIME=linux-arm64 PUBLISH_DIR="$RUNNER_TEMP/image-payloads/arm64"
    ;;
  terrabase)
    arch=${2:?architecture required}
    case $arch in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 2 ;; esac
    # This required packaging proof produces the exact production payload once.
    # Keep ReadyToRun and all normal compiler/analyzer checks enabled.
    make terrabase-publish RUNTIME="$rid" PUBLISH_DIR="$RUNNER_TEMP/image-payloads/$arch"
    ;;
  pack)
    area=${2:?area required}
    case $area in auth) binary=ts-auth ;; terrabase) binary=terrabase ;; *) exit 2 ;; esac
    architectures=(amd64 arm64)
    suffix=
    if [[ -n ${3:-} ]]; then
      case $3 in amd64|arm64) architectures=("$3"); suffix="-$3" ;; *) exit 2 ;; esac
    fi
    for arch in "${architectures[@]}"; do
      case $arch in amd64) machine=3e00 ;; arm64) machine=b700 ;; esac
      path="$RUNNER_TEMP/image-payloads/$arch/$binary"
      test -x "$path"
      test "$(od -An -tx1 -j18 -N2 "$path" | tr -d ' \n')" = "$machine"
    done
    tar -C "$RUNNER_TEMP/image-payloads" -cf "$RUNNER_TEMP/image-payloads-$area$suffix.tar" "${architectures[@]}"
    ;;
  runtime-dockerfile)
    area=${2:?area required}
    case $area in auth) binary=ts-auth ;; terrabase) binary=terrabase ;; *) exit 2 ;; esac
    # Keep each area's runtime configuration identical to its source Dockerfile.
    sed -n "/ AS $binary\$/,/^FROM /p" Dockerfile | sed '1!{/^FROM /d;}' |
      sed -E "s/^COPY --from=publish-[^ ]+ \/out \/app$/ARG TARGETARCH\nCOPY \$\{TARGETARCH\}\/ \/app/"
    ;;
  *) echo 'Expected auth | terrabase <arch> | pack <area> [arch] | runtime-dockerfile <area>' >&2; exit 2 ;;
esac
