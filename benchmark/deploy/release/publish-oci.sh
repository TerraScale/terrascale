#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 ]] || {
  echo "usage: $0 <signed-package-directory>" >&2
  exit 2
}
output=$1
: "${TERRABASE_RELEASE_REGISTRY:?protected registry repository is required}"
: "${TERRABASE_RELEASE_REGISTRY_USERNAME:?protected registry username is required}"
: "${TERRABASE_RELEASE_REGISTRY_TOKEN:?protected registry token is required}"

registry_host=${TERRABASE_RELEASE_REGISTRY%%/*}
registry_path=${TERRABASE_RELEASE_REGISTRY#*/}
[[ "$TERRABASE_RELEASE_REGISTRY" == */* \
  && "$registry_host" =~ ^[A-Za-z0-9.-]+(:[0-9]+)?$ \
  && "$registry_path" =~ ^[A-Za-z0-9._/-]+$ \
  && "$registry_path" != */ \
  && "$registry_path" != *//* \
  && "$registry_path" != *@* \
  && "$registry_path" != *:* ]] || {
  echo "registry repository must be an immutable repository path without a tag or digest" >&2
  exit 1
}
[[ -d "$output" && ! -L "$output" ]]
command -v jq >/dev/null
command -v openssl >/dev/null
command -v sha256sum >/dev/null
command -v skopeo >/dev/null

shopt -s nullglob
manifests=("$output"/*.release.json)
[[ ${#manifests[@]} -eq 1 ]] || {
  echo "signed package must contain exactly one release manifest" >&2
  exit 1
}
manifest=${manifests[0]}
package=$(jq -er '.package | select(. == "terrabase-server" or . == "terrabase-studio")' "$manifest")
version=$(jq -er '.version | select(test("^[0-9]+[.][0-9]+[.][0-9]+([.-][A-Za-z0-9.-]+)?$"))' "$manifest")
revision=$(jq -er '.source.revision | select(test("^[0-9a-f]{40,64}$"))' "$manifest")
target=$(jq -er '.target' "$manifest")
case "$target" in
  x86_64-unknown-linux-gnu) arch=amd64 ;;
  aarch64-unknown-linux-gnu) arch=arm64 ;;
  *) echo "unsupported release target: $target" >&2; exit 1 ;;
esac
oci_name=$(jq -er '.artifacts.ociArchive.name' "$manifest")
oci_digest=$(jq -er '.artifacts.ociArchive.sha256 | select(test("^[0-9a-f]{64}$"))' "$manifest")
oci_tag=$package-$version-$arch
[[ "$oci_name" == "$(basename -- "$oci_name")" && -f "$output/$oci_name" && ! -L "$output/$oci_name" ]]
[[ -f "$output/SHA256SUMS" && ! -L "$output/SHA256SUMS" \
  && -f "$output/SHA256SUMS.sig" && ! -L "$output/SHA256SUMS.sig" ]] || {
  echo "registry publication requires a signed package" >&2
  exit 1
}
[[ $(sha256sum "$output/$oci_name" | awk '{print $1}') == "$oci_digest" ]]
[[ $(awk -v name="$oci_name" '$2 == name {print $1}' "$output/SHA256SUMS") == "$oci_digest" ]]
public_key=
source_manifest=
destination_manifest=
auth_file=
cleanup() {
  rm -f -- ${source_manifest:+"$source_manifest"} \
    ${destination_manifest:+"$destination_manifest"} \
    ${auth_file:+"$auth_file"} ${public_key:+"$public_key"}
}
trap cleanup EXIT
public_key=$(mktemp "${TMPDIR:-/tmp}/terrabase-registry-public-key.XXXXXXXX")
if [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_RELEASE_VERIFY_KEY_FILE" "$public_key"
elif [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_PEM-} ]]; then
  printf '%s\n' "$TERRABASE_RELEASE_VERIFY_KEY_PEM" >"$public_key"
else
  rm -f -- "$public_key"
  echo "independent release verification key is not configured" >&2
  exit 1
fi
openssl dgst -sha256 -verify "$public_key" -signature "$output/SHA256SUMS.sig" "$output/SHA256SUMS" >/dev/null
destination="$TERRABASE_RELEASE_REGISTRY/$package:$revision-$arch"

source_manifest=$(mktemp "${TMPDIR:-/tmp}/terrabase-source-manifest.XXXXXXXX")
destination_manifest=$(mktemp "${TMPDIR:-/tmp}/terrabase-destination-manifest.XXXXXXXX")
auth_file=$(mktemp "${TMPDIR:-/tmp}/terrabase-registry-auth.XXXXXXXX")
chmod 600 "$auth_file"
printf '%s' "$TERRABASE_RELEASE_REGISTRY_TOKEN" | skopeo login \
  --authfile "$auth_file" --username "$TERRABASE_RELEASE_REGISTRY_USERNAME" \
  --password-stdin "$registry_host" >/dev/null
skopeo inspect --raw "oci-archive:$output/$oci_name:$oci_tag" >"$source_manifest"
source_digest=$(sha256sum "$source_manifest" | awk '{print $1}')
skopeo copy --all --preserve-digests --authfile "$auth_file" \
  "oci-archive:$output/$oci_name:$oci_tag" "docker://$destination"
skopeo inspect --raw --authfile "$auth_file" "docker://$destination" >"$destination_manifest"
destination_digest=$(sha256sum "$destination_manifest" | awk '{print $1}')
[[ "$destination_digest" == "$source_digest" ]] || {
  echo "registry manifest digest differs from the signed candidate" >&2
  exit 1
}
printf 'published %s@sha256:%s\n' "${destination%:*}" "$destination_digest"
