#!/usr/bin/env bash
set -euo pipefail
area=${2:?area required}
case $area in
  auth) binary=ts-auth project=src/TerraScale.Authentication ;;
  terrabase) binary=terrabase project=src/TerraBase ;;
  platform) binary=ts-platform project=src/TerraScale.Platform ;;
  dashboard) binary=dashboard project=src/TerraScale.Dashboard ;;
  *) exit 2 ;;
esac
case ${1:-} in
  publish)
    output=$RUNNER_TEMP/image-payloads/arm64
    dotnet restore "$project" --locked-mode
    make "$area-publish" RUNTIME=linux-arm64 PUBLISH_DIR="$output"
    test -x "$output/$binary"
    test "$(od -An -tx1 -j18 -N2 "$output/$binary" | tr -d ' \n')" = b700
    ;;
  runtime-dockerfile)
    cat ".github/docker/$binary.Dockerfile"
    ;;
  *) echo 'Expected publish | runtime-dockerfile <auth|terrabase|platform|dashboard>' >&2; exit 2 ;;
esac
