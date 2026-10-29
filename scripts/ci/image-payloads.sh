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
    # Fast proves packaging without runtime precompilation. Reuse its compiled
    # graph while retaining the production ReadyToRun setting for both images.
    dotnet publish src/TerraBase --configuration Release --runtime linux-x64 --self-contained --no-restore --output "$RUNNER_TEMP/image-payloads/amd64"
    make terrabase-publish RUNTIME=linux-arm64 PUBLISH_DIR="$RUNNER_TEMP/image-payloads/arm64"
    ;;
  pack)
    area=${2:?area required}
    case $area in auth) binary=ts-auth ;; terrabase) binary=terrabase ;; *) exit 2 ;; esac
    for arch in amd64 arm64; do
      case $arch in amd64) machine=3e00 ;; arm64) machine=b700 ;; esac
      path="$RUNNER_TEMP/image-payloads/$arch/$binary"
      test -x "$path"
      test "$(od -An -tx1 -j18 -N2 "$path" | tr -d ' \n')" = "$machine"
    done
    tar -C "$RUNNER_TEMP/image-payloads" -cf "$RUNNER_TEMP/image-payloads-$area.tar" amd64 arm64
    ;;
  runtime-dockerfile)
    area=${2:?area required}
    case $area in auth) binary=ts-auth ;; terrabase) binary=terrabase ;; *) exit 2 ;; esac
    # Keep each area's runtime configuration identical to its source Dockerfile.
    sed -n "/ AS $binary\$/,/^FROM /p" Dockerfile | sed '1!{/^FROM /d;}' |
      sed -E "s/^COPY --from=publish-[^ ]+ \/out \/app$/ARG TARGETARCH\nCOPY \$\{TARGETARCH\}\/ \/app/"
    ;;
  *) echo 'Expected auth | terrabase | pack <area> | runtime-dockerfile <area>' >&2; exit 2 ;;
esac
