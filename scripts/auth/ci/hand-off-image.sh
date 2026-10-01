#!/usr/bin/env bash
# Hand the release image packaged by scripts/ci/package-image.sh from the
# release job to the publish job of the same workflow run.
#
#   save  archive localhost/terrascale/ts-auth:<commit> under the runner's
#         shared /cache volume and write its SHA-256 to GITHUB_OUTPUT as
#         `sha256`, which the workflow passes to publish as a job output.
#   load  copy that archive into this job's private RUNNER_TEMP, check it
#         against IMAGE_ARCHIVE_SHA256, and load it into podman.
#
# Every job on the runner can write /cache, so publish trusts only the digest
# carried by the job output, never the file it finds there, and checks the
# private copy it actually loads.
set -euo pipefail

mode=${1:-}
[[ $# -eq 1 && $mode =~ ^(save|load)$ ]] || { echo "usage: $0 save|load" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
handoffs=/cache/ci-tmp
dir="$handoffs/ts-auth-image-${GITHUB_RUN_ID:?GITHUB_RUN_ID is required}"
archive="$dir/ts-auth.tar"
image="localhost/terrascale/ts-auth:$(git -C "$repo" rev-parse HEAD)"

case $mode in
  save)
    : "${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"
    # Hand-offs a failed gate left unpublished expire after a day.
    mkdir -p -- "$handoffs"
    find "$handoffs" -mindepth 1 -maxdepth 1 -name 'ts-auth-image-*' -mmin +1440 -exec rm -rf -- {} +
    rm -rf -- "$dir"
    mkdir -p -- "$dir"
    podman save --format docker-archive --output "$archive" "$image"
    digest=$(sha256sum -- "$archive" | cut -d' ' -f1)
    echo "sha256=$digest" >> "$GITHUB_OUTPUT"
    printf 'handed off %s as %s (sha256 %s)\n' "$image" "$archive" "$digest"
    ;;
  load)
    [[ ${IMAGE_ARCHIVE_SHA256:-} =~ ^[0-9a-f]{64}$ ]] ||
      { echo "IMAGE_ARCHIVE_SHA256 must be the SHA-256 the release job reported" >&2; exit 2; }
    copy="${RUNNER_TEMP:?}/ts-auth.tar"
    cp -- "$archive" "$copy"
    rm -rf -- "$dir"
    printf '%s  %s\n' "$IMAGE_ARCHIVE_SHA256" "$copy" | sha256sum --check --strict
    podman load --input "$copy"
    rm -f -- "$copy"
    podman image exists "$image" || { echo "the archive does not hold $image" >&2; exit 1; }
    printf 'loaded %s\n' "$image"
    ;;
esac
