#!/usr/bin/env bash
set -euo pipefail
case ${1:?thing} in
  auth) package=ts-auth signer=.github/workflows/ci.yml ;;
  platform) package=ts-platform signer=.github/workflows/ci.yml ;;
  dashboard) package=terrascale-dashboard signer=.github/workflows/images-ghcr.yml ;;
  *) exit 2 ;;
esac
namespace=${OCIR_NAMESPACE:?}
image="sa-saopaulo-1.ocir.io/${namespace}/terrascale/${package}"
attested() {
  local arm64 listing repository
  local -a repositories
  arm64=$(jq -r 'if .manifests then (.manifests[] | select(.platform.os == "linux" and .platform.architecture == "arm64") | .digest) else empty end' "$RUNNER_TEMP/manifest.json") ||
    { echo "Cannot read the manifest of $image:$REVISION"; return 1; }
  if [[ -z "$arm64" ]]; then
    arm64=$(docker buildx imagetools inspect "$image:$REVISION" --format '{{json .Manifest.Digest}}' | jq -er '.') ||
      { echo "Cannot resolve the linux/arm64 digest of $image:$REVISION"; return 1; }
  fi
  [[ "$arm64" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "Invalid linux/arm64 digest for $image:$REVISION: $arm64"; return 1; }
  gh attestation verify "oci://${image}@${arm64}" --owner TerraScale --signer-workflow "TerraScale/terrascale/${signer}" \
    --source-ref refs/heads/main --source-digest "$REVISION" > /dev/null ||
    { echo "No authentic ${signer} build provenance for ${image}@${arm64} at $REVISION"; return 1; }
  gh attestation verify "oci://${image}@${arm64}" --owner TerraScale --predicate-type https://spdx.dev/Document/v2.3 \
    --cert-identity-regex '^https://github\.com/TerraScale/terrascale/\.github/workflows/(ci|attest-images)\.yml@refs/heads/main$' \
    --source-ref refs/heads/main > /dev/null ||
    { echo "No authentic SBOM attestation for ${image}@${arm64}"; return 1; }
  listing=$(bash .github/scripts/ci/ocir-repositories.sh "$package") || { echo "Cannot list the OCIR repositories of $package"; return 1; }
  mapfile -t repositories <<< "$listing"
  for repository in "${repositories[@]}"; do
    oras discover --artifact-type application/vnd.dev.sigstore.bundle.v0.3+json --format json --depth 1 "${repository}@${arm64}" |
      jq -e '(.referrers | length) >= 2' > /dev/null ||
      { echo "Missing provenance and SBOM Sigstore referrers on ${repository}@${arm64}"; return 1; }
  done
}
if docker buildx imagetools inspect "$image:${REVISION:?}" --raw > "$RUNNER_TEMP/manifest.json" 2> "$RUNNER_TEMP/manifest-error"; then
  if attested; then
    cached=true
    echo "Reusing attested $image:$REVISION"
  else
    cached=false
    echo "Rebuilding $image:$REVISION to sign, mirror and attest it"
  fi
else
  if ! grep -Eqi 'manifest[_ ]unknown|not found|name[_ ]unknown' "$RUNNER_TEMP/manifest-error"; then
    cat "$RUNNER_TEMP/manifest-error" >&2
    exit 1
  fi
  cached=false
fi
echo "cached=$cached" >> "$GITHUB_OUTPUT"
echo "IMAGE_CACHED=$cached" >> "$GITHUB_ENV"
