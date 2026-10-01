#!/usr/bin/env bash
# Transfer the smoke-checked image by immutable manifest digest. Staging tags
# carry their creation time and remain available for publish retries for 30 days.
set -euo pipefail

mode=${1:-}
[[ $# -eq 1 && $mode =~ ^(stage|publish|prune)$ ]] || { echo "usage: $0 stage|publish|prune" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
owner=${REPOSITORY_OWNER:?REPOSITORY_OWNER is required}
owner=${owner,,}
[[ $owner =~ ^[a-z0-9]+((\.|_{1,2}|-+)[a-z0-9]+)*$ ]] || { echo "invalid registry owner" >&2; exit 2; }
staging="git.terrascale.tech/$owner/ts-auth-ci"
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"
: "${REGISTRY_PUSH_TOKEN:?registry push token is required}"
if [[ $mode == publish && ! ${IMAGE_MANIFEST_DIGEST:-} =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "IMAGE_MANIFEST_DIGEST must be the manifest digest the release job reported" >&2
  exit 2
fi

REGISTRY_AUTH_FILE=$(mktemp "$RUNNER_TEMP/ts-auth-registry-auth.XXXXXXXX")
export REGISTRY_AUTH_FILE
trap 'rm -f -- "$REGISTRY_AUTH_FILE"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
chmod 600 "$REGISTRY_AUTH_FILE"
# Podman requires an empty JSON object when an auth file already exists.
printf '{}\n' > "$REGISTRY_AUTH_FILE"
printf '%s\n' "$REGISTRY_PUSH_TOKEN" |
  podman login git.terrascale.tech --username "$owner" --password-stdin >/dev/null
unset REGISTRY_PUSH_TOKEN

case $mode in
  stage)
    : "${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"
    run=${GITHUB_RUN_ID:?GITHUB_RUN_ID is required}
    attempt=${GITHUB_RUN_ATTEMPT:-1}
    [[ $run =~ ^[0-9]+$ && $attempt =~ ^[0-9]+$ ]] || { echo "invalid workflow run identity" >&2; exit 2; }
    source_commit=$(git -C "$repo" rev-parse HEAD)
    image=$(bash "$repo/scripts/ci/image-ref.sh")
    candidate="$staging:$source_commit-$run-$attempt-$(date +%s)"
    digest_file=$(mktemp "$RUNNER_TEMP/ts-auth-manifest-digest.XXXXXXXX")
    trap 'rm -f -- "$REGISTRY_AUTH_FILE" "$digest_file"' EXIT
    podman image exists "$image"
    podman tag "$image" "$candidate"
    podman push --digestfile "$digest_file" "$candidate"
    digest=$(cat "$digest_file")
    [[ $digest =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "registry returned an invalid manifest digest" >&2; exit 1; }
    printf 'image-digest=%s\npublish-enabled=true\n' "$digest" >> "$GITHUB_OUTPUT"
    printf 'staged %s at %s@%s\n' "$image" "$staging" "$digest"
    ;;
  publish)
    source_commit=$(git -C "$repo" rev-parse HEAD)
    remote_image="git.terrascale.tech/$owner/ts-auth:$source_commit"
    digest_file=$(mktemp "$RUNNER_TEMP/ts-auth-manifest-digest.XXXXXXXX")
    trap 'rm -f -- "$REGISTRY_AUTH_FILE" "$digest_file"' EXIT
    # Preserve the registry manifest and compressed layers. Podman's local
    # pull/tag/push round trip can change them before a post-push check runs.
    skopeo copy --all --preserve-digests --src-authfile "$REGISTRY_AUTH_FILE" \
      --dest-authfile "$REGISTRY_AUTH_FILE" --digestfile "$digest_file" \
      "docker://$staging@$IMAGE_MANIFEST_DIGEST" "docker://$remote_image"
    [[ $(cat "$digest_file") == "$IMAGE_MANIFEST_DIGEST" ]] || {
      echo "published manifest digest does not match the staged digest" >&2
      exit 1
    }
    printf 'published %s from manifest %s\n' "$remote_image" "$IMAGE_MANIFEST_DIGEST"
    ;;
  prune)
    # All jobs finish within an hour. A 30-day grace also retains failed-gate
    # candidates and supports retries without making storage grow forever.
    tags=$(skopeo list-tags --authfile "$REGISTRY_AUTH_FILE" "docker://$staging")
    cutoff=$(($(date +%s) - 30 * 86400))
    tag_list=$(jq -r 'if (.Tags | type) == "array" then .Tags[] else error("registry tag list must be an array") end' <<< "$tags")
    mapfile -t candidates < <(printf '%s' "$tag_list")
    declare -A protected=() expired=() deleted=()
    for tag in "${candidates[@]}"; do
      digest=$(skopeo inspect --no-tags --authfile "$REGISTRY_AUTH_FILE" --format '{{.Digest}}' "docker://$staging:$tag")
      [[ $digest =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "invalid staging manifest digest" >&2; exit 1; }
      if [[ $tag =~ ^[0-9a-f]{40}-[0-9]+-[0-9]+-([0-9]{10})$ ]] &&
        ((10#${BASH_REMATCH[1]} < cutoff)); then
        expired[$tag]=$digest
      else
        protected[$digest]=1
      fi
    done
    # Registry deletion removes a manifest, including its other tags. Keep
    # every manifest with a recent or unmanaged tag, and delete each only once.
    for tag in "${!expired[@]}"; do
      digest=${expired[$tag]}
      if [[ -z ${protected[$digest]:-} && -z ${deleted[$digest]:-} ]]; then
        skopeo delete --authfile "$REGISTRY_AUTH_FILE" "docker://$staging:$tag"
        deleted[$digest]=1
        printf 'removed expired staging manifest %s\n' "$digest"
      fi
    done
    ;;
esac
