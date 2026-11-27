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
type=application/vnd.dev.sigstore.bundle.v0.3+json
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
attested() {
  local arm64 listing repository found referrer layer has_prov has_sbom
  local -a repositories referrers
  arm64=$(jq -r 'if .manifests then (.manifests[] | select(.platform.os == "linux" and .platform.architecture == "arm64") | .digest) else empty end' "$RUNNER_TEMP/manifest.json") ||
    { echo "Cannot read the manifest of $image:$REVISION"; return 1; }
  if [[ -z "$arm64" ]]; then
    arm64=$(docker buildx imagetools inspect "$image:$REVISION" --format '{{json .Manifest.Digest}}' | jq -er '.') ||
      { echo "Cannot resolve the linux/arm64 digest of $image:$REVISION"; return 1; }
  fi
  [[ "$arm64" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "Invalid linux/arm64 digest for $image:$REVISION: $arm64"; return 1; }
  listing=$(bash .github/scripts/ci/ocir-repositories.sh "$package") || { echo "Cannot list the OCIR repositories of $package"; return 1; }
  mapfile -t repositories <<< "$listing"
  for repository in "${repositories[@]}"; do
    has_prov=false
    has_sbom=false
    found=$(oras discover --artifact-type "$type" --format json --depth 1 "${repository}@${arm64}" | jq -r '.referrers[].digest') ||
      { echo "Cannot list the Sigstore referrers of ${repository}@${arm64}"; rm -rf "${work:?}"/*; return 1; }
    mapfile -t referrers <<< "$found"
    for referrer in "${referrers[@]}"; do
      [[ "$referrer" =~ ^sha256:[0-9a-f]{64}$ ]] || continue
      rm -f "$work/manifest.json" "$work/bundle.json"
      if ! oras manifest fetch "${repository}@${referrer}" > "$work/manifest.json" ||
        ! jq -e --arg digest "$arm64" --arg type "$type" \
          '.subject.digest == $digest and .artifactType == $type and (.layers | length) == 1' "$work/manifest.json" > /dev/null ||
        ! layer=$(jq -er '.layers[0].digest' "$work/manifest.json") ||
        ! oras blob fetch "${repository}@${layer}" --output "$work/bundle.json"; then
        echo "Skipping malformed Sigstore referrer ${repository}@${referrer}"
        continue
      fi
      if gh attestation verify "oci://${image}@${arm64}" --bundle "$work/bundle.json" --owner TerraScale \
        --signer-workflow "TerraScale/terrascale/${signer}" --source-ref refs/heads/main --source-digest "$REVISION" > /dev/null 2>&1; then
        has_prov=true
      fi
      if gh attestation verify "oci://${image}@${arm64}" --bundle "$work/bundle.json" --owner TerraScale \
        --predicate-type https://spdx.dev/Document/v2.3 \
        --cert-identity-regex '^https://github\.com/TerraScale/terrascale/\.github/workflows/(ci|attest-images)\.yml@refs/heads/main$' \
        --source-ref refs/heads/main > /dev/null 2>&1; then
        has_sbom=true
      fi
    done
    if [[ "$has_prov" != true || "$has_sbom" != true ]]; then
      [[ "$has_prov" == true ]] || echo "Missing authentic ${signer} build provenance bundle at $REVISION on ${repository}@${arm64}"
      [[ "$has_sbom" == true ]] || echo "Missing authentic SBOM bundle on ${repository}@${arm64}"
      rm -rf "${work:?}"/*
      return 1
    fi
  done
  rm -rf "${work:?}"/*
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
