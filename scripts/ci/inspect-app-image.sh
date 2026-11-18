#!/usr/bin/env bash
set -euo pipefail
case ${1:?thing} in
  auth) package=ts-auth ;;
  dashboard) package=terrascale-dashboard ;;
  *) exit 2 ;;
esac
image=ghcr.io/terrascale/$package
if docker buildx imagetools inspect "$image:${REVISION:?}" --raw > "$RUNNER_TEMP/manifest.json" 2> "$RUNNER_TEMP/manifest-error"; then
  cached=true
  echo "Reusing $image:$REVISION"
else
  if ! grep -Eq 'manifest unknown|not found' "$RUNNER_TEMP/manifest-error"; then
    cat "$RUNNER_TEMP/manifest-error" >&2
    exit 1
  fi
  cached=false
fi
echo "cached=$cached" >> "$GITHUB_OUTPUT"
echo "IMAGE_CACHED=$cached" >> "$GITHUB_ENV"
