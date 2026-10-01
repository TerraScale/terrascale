#!/usr/bin/env bash
# Transfer the smoke-checked image between jobs through the registry. Job
# outputs bind the manifest digest; a publish retry pulls that same digest.
set -euo pipefail

mode=${1:-}
[[ $# -eq 1 && $mode =~ ^(stage|load)$ ]] || { echo "usage: $0 stage|load" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
image="$(bash "$repo/scripts/ci/image-ref.sh")"
owner=${REPOSITORY_OWNER:?REPOSITORY_OWNER is required}
owner=${owner,,}
[[ $owner =~ ^[a-z0-9][a-z0-9_.-]*$ ]] || { echo "invalid registry owner" >&2; exit 2; }
staging="git.terrascale.tech/$owner/ts-auth-ci"
export REGISTRY_AUTH_FILE="${RUNNER_TEMP:?RUNNER_TEMP is required}/ts-auth-podman-auth.json"

if [[ $mode == load && ! ${IMAGE_MANIFEST_DIGEST:-} =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "IMAGE_MANIFEST_DIGEST must be the manifest digest the release job reported" >&2
  exit 2
fi
printf '%s\n' "${REGISTRY_PUSH_TOKEN:?registry push token is required}" |
  podman login git.terrascale.tech --username "$owner" --password-stdin >/dev/null

case $mode in
  stage)
    : "${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"
    run=${GITHUB_RUN_ID:?GITHUB_RUN_ID is required}
    attempt=${GITHUB_RUN_ATTEMPT:-1}
    [[ $run =~ ^[0-9]+$ && $attempt =~ ^[0-9]+$ ]] || { echo "invalid workflow run identity" >&2; exit 2; }
    source_commit=$(git -C "$repo" rev-parse HEAD)
    candidate="$staging:$source_commit-$run-$attempt"
    digest_file="$RUNNER_TEMP/ts-auth-manifest-digest"
    podman image exists "$image"
    podman tag "$image" "$candidate"
    podman push --digestfile "$digest_file" "$candidate"
    digest=$(cat "$digest_file")
    [[ $digest =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "registry returned an invalid manifest digest" >&2; exit 1; }
    printf 'image-digest=%s\npublish-enabled=true\n' "$digest" >> "$GITHUB_OUTPUT"
    printf 'staged %s at %s@%s\n' "$image" "$staging" "$digest"
    ;;
  load)
    podman pull "$staging@$IMAGE_MANIFEST_DIGEST"
    podman tag "$staging@$IMAGE_MANIFEST_DIGEST" "$image"
    podman image exists "$image"
    printf 'loaded %s from manifest %s\n' "$image" "$IMAGE_MANIFEST_DIGEST"
    ;;
esac
