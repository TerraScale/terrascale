#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 2 ]] || {
  echo "usage: $0 <signed-amd64-package-directory> <signed-arm64-package-directory>" >&2
  exit 2
}
package_directories=("$1" "$2")

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
  echo "registry repository must be a repository path without a tag or digest" >&2
  exit 1
}

for tool in cmp cp find jq openssl sha256sum skopeo sort stat; do
  command -v "$tool" >/dev/null || {
    echo "required registry publication tool is missing: $tool" >&2
    exit 1
  }
done

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-oci-publish.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-oci-publish.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

public_key=$work/release-public-key.pem
if [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_RELEASE_VERIFY_KEY_FILE" "$public_key"
elif [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_PEM-} ]]; then
  umask 077
  printf '%s\n' "$TERRABASE_RELEASE_VERIFY_KEY_PEM" >"$public_key"
else
  echo "independent release verification key is not configured" >&2
  exit 1
fi
openssl pkey -pubin -in "$public_key" -text_pub -noout | grep -Fq 'ASN1 OID: prime256v1' || {
  echo "release verification key must be ECDSA P-256" >&2
  exit 1
}

signed_checksum() {
  local sums=$1 name=$2 expected=$3 count observed
  count=$(awk -v name="$name" '$2 == name { count += 1 } END { print count + 0 }' "$sums")
  observed=$(awk -v name="$name" '$2 == name { print $1 }' "$sums")
  [[ "$count" == 1 && "$observed" == "$expected" ]] || {
    echo "signed checksum does not bind $name to the expected bytes" >&2
    exit 1
  }
}

declare -A directory_by_arch
declare -A manifest_by_arch
declare -A archive_by_arch
declare -A archive_tag_by_arch
declare -A manifest_raw_by_arch
declare -A manifest_digest_by_arch
declare -A manifest_size_by_arch
declare -A manifest_media_type_by_arch
declare -A layout_by_arch

expected_package=
expected_component=
expected_version=
expected_revision=
expected_tree=

for output in "${package_directories[@]}"; do
  [[ -d "$output" && ! -L "$output" ]] || {
    echo "signed package is not a regular directory: $output" >&2
    exit 1
  }
  shopt -s nullglob
  manifests=("$output"/*.release.json)
  shopt -u nullglob
  [[ ${#manifests[@]} -eq 1 ]] || {
    echo "signed package must contain exactly one release manifest" >&2
    exit 1
  }
  manifest=${manifests[0]}
  manifest_name=$(basename -- "$manifest")
  component=$(jq -er '.component | select(. == "server" or . == "studio")' "$manifest")
  package=$(jq -er '.package | select(. == "terrabase-server" or . == "terrabase-studio")' "$manifest")
  case "$component/$package" in
    server/terrabase-server|studio/terrabase-studio) ;;
    *) echo "release component and package identities disagree" >&2; exit 1 ;;
  esac
  version=$(jq -er '.version | select(test("^[0-9]+[.][0-9]+[.][0-9]+([+-][A-Za-z0-9.-]+)?$"))' "$manifest")
  revision=$(jq -er '.source.revision | select(test("^[0-9a-f]{40,64}$"))' "$manifest")
  tree=$(jq -er '.source.tree | select(test("^[0-9a-f]{40,64}$"))' "$manifest")
  jq -e '.source.dirty == false' "$manifest" >/dev/null || {
    echo "registry publication refuses a dirty release identity" >&2
    exit 1
  }
  target=$(jq -er '.target' "$manifest")
  case "$target" in
    x86_64-unknown-linux-gnu) arch=amd64 ;;
    aarch64-unknown-linux-gnu) arch=arm64 ;;
    *) echo "unsupported release target: $target" >&2; exit 1 ;;
  esac
  [[ -z ${directory_by_arch[$arch]+present} ]] || {
    echo "signed package pair contains duplicate architecture: $arch" >&2
    exit 1
  }

  oci_name=$(jq -er '.artifacts.ociArchive.name' "$manifest")
  oci_archive_digest=$(jq -er '.artifacts.ociArchive.sha256 | select(test("^[0-9a-f]{64}$"))' "$manifest")
  oci_manifest_digest=$(jq -er '.ociImage.manifestDigest | select(test("^sha256:[0-9a-f]{64}$"))' "$manifest")
  oci_manifest_size=$(jq -er '.ociImage.manifestSize | select(type == "number" and . > 0 and floor == .)' "$manifest")
  oci_manifest_media_type=$(jq -er '.ociImage.mediaType | select(. == "application/vnd.oci.image.manifest.v1+json")' "$manifest")
  oci_os=$(jq -er '.ociImage.os | select(. == "linux")' "$manifest")
  oci_arch=$(jq -er '.ociImage.architecture | select(. == "amd64" or . == "arm64")' "$manifest")
  [[ "$oci_arch" == "$arch" ]] || {
    echo "signed OCI architecture does not match the release target" >&2
    exit 1
  }
  [[ "$oci_name" == "$(basename -- "$oci_name")" \
    && -f "$output/$oci_name" && ! -L "$output/$oci_name" ]] || {
    echo "signed OCI archive path is missing or unsafe" >&2
    exit 1
  }
  [[ -f "$output/SHA256SUMS" && ! -L "$output/SHA256SUMS" \
    && -f "$output/SHA256SUMS.sig" && ! -L "$output/SHA256SUMS.sig" ]] || {
    echo "registry publication requires a signed package" >&2
    exit 1
  }
  openssl dgst -sha256 -verify "$public_key" \
    -signature "$output/SHA256SUMS.sig" "$output/SHA256SUMS" >/dev/null
  observed_manifest_checksum=$(sha256sum "$manifest" | awk '{print $1}')
  observed_archive_checksum=$(sha256sum "$output/$oci_name" | awk '{print $1}')
  [[ "$observed_archive_checksum" == "$oci_archive_digest" ]] || {
    echo "OCI archive differs from the signed release manifest" >&2
    exit 1
  }
  signed_checksum "$output/SHA256SUMS" "$manifest_name" "$observed_manifest_checksum"
  signed_checksum "$output/SHA256SUMS" "$oci_name" "$oci_archive_digest"

  archive_tag=$package-$version-$arch
  source_manifest=$work/source-$arch.json
  skopeo inspect --raw "oci-archive:$output/$oci_name:$archive_tag" >"$source_manifest"
  observed_manifest_digest=sha256:$(sha256sum "$source_manifest" | awk '{print $1}')
  observed_manifest_size=$(stat -c '%s' "$source_manifest")
  observed_manifest_media_type=$(jq -er '.mediaType' "$source_manifest")
  [[ "$observed_manifest_digest" == "$oci_manifest_digest" \
    && "$observed_manifest_size" == "$oci_manifest_size" \
    && "$observed_manifest_media_type" == "$oci_manifest_media_type" ]] || {
    echo "OCI archive manifest differs from the signed image descriptor" >&2
    exit 1
  }
  image_metadata=$(skopeo inspect "oci-archive:$output/$oci_name:$archive_tag")
  [[ $(jq -er '.Architecture' <<<"$image_metadata") == "$arch" \
    && $(jq -er '.Os' <<<"$image_metadata") == "$oci_os" ]] || {
    echo "OCI archive platform differs from the signed release identity" >&2
    exit 1
  }

  if [[ -z "$expected_package" ]]; then
    expected_package=$package
    expected_component=$component
    expected_version=$version
    expected_revision=$revision
    expected_tree=$tree
  elif [[ "$package" != "$expected_package" \
    || "$component" != "$expected_component" \
    || "$version" != "$expected_version" \
    || "$revision" != "$expected_revision" \
    || "$tree" != "$expected_tree" ]]; then
    echo "signed architecture packages do not share one release identity" >&2
    exit 1
  fi

  architecture_layout=$work/layout-$arch
  skopeo copy --all --preserve-digests \
    "oci-archive:$output/$oci_name:$archive_tag" "oci:$architecture_layout:$arch"
  copied_manifest=$work/copied-$arch.json
  skopeo inspect --raw "oci:$architecture_layout:$arch" >"$copied_manifest"
  cmp -s "$source_manifest" "$copied_manifest" || {
    echo "OCI staging attempted to rebuild the signed $arch manifest" >&2
    exit 1
  }

  directory_by_arch[$arch]=$output
  manifest_by_arch[$arch]=$manifest
  archive_by_arch[$arch]=$output/$oci_name
  archive_tag_by_arch[$arch]=$archive_tag
  manifest_raw_by_arch[$arch]=$source_manifest
  manifest_digest_by_arch[$arch]=$oci_manifest_digest
  manifest_size_by_arch[$arch]=$oci_manifest_size
  manifest_media_type_by_arch[$arch]=$oci_manifest_media_type
  layout_by_arch[$arch]=$architecture_layout
done

[[ -n ${directory_by_arch[amd64]+present} && -n ${directory_by_arch[arm64]+present} ]] || {
  echo "signed package pair must contain exactly amd64 and arm64" >&2
  exit 1
}

combined_layout=$work/multiarch-layout
mkdir -p "$combined_layout/blobs/sha256"
printf '{"imageLayoutVersion":"1.0.0"}\n' >"$combined_layout/oci-layout"

copy_blob() {
  local source=$1 digest destination
  digest=$(sha256sum "$source" | awk '{print $1}')
  destination=$combined_layout/blobs/sha256/$digest
  if [[ -e "$destination" ]]; then
    [[ -f "$destination" && ! -L "$destination" ]] && cmp -s "$source" "$destination" || {
      echo "OCI layouts contain conflicting content-addressed blobs" >&2
      exit 1
    }
  else
    cp -- "$source" "$destination"
  fi
}

for arch in amd64 arm64; do
  while IFS= read -r -d '' blob; do
    blob_name=${blob##*/}
    [[ "$blob_name" =~ ^[0-9a-f]{64}$ \
      && $(sha256sum "$blob" | awk '{print $1}') == "$blob_name" ]] || {
      echo "staged OCI layout contains an invalid content-addressed blob" >&2
      exit 1
    }
    copy_blob "$blob"
  done < <(find "${layout_by_arch[$arch]}/blobs/sha256" -maxdepth 1 -type f -print0)
done

index_manifest=$work/index-manifest.json
jq -cnS \
  --arg amd64_digest "${manifest_digest_by_arch[amd64]}" \
  --argjson amd64_size "${manifest_size_by_arch[amd64]}" \
  --arg amd64_media_type "${manifest_media_type_by_arch[amd64]}" \
  --arg arm64_digest "${manifest_digest_by_arch[arm64]}" \
  --argjson arm64_size "${manifest_size_by_arch[arm64]}" \
  --arg arm64_media_type "${manifest_media_type_by_arch[arm64]}" \
  '{
    schemaVersion: 2,
    mediaType: "application/vnd.oci.image.index.v1+json",
    manifests: [
      {mediaType: $amd64_media_type, digest: $amd64_digest, size: $amd64_size,
       platform: {os: "linux", architecture: "amd64"}},
      {mediaType: $arm64_media_type, digest: $arm64_digest, size: $arm64_size,
       platform: {os: "linux", architecture: "arm64"}}
    ]
  }' >"$index_manifest"
index_digest=sha256:$(sha256sum "$index_manifest" | awk '{print $1}')
index_size=$(stat -c '%s' "$index_manifest")
copy_blob "$index_manifest"

write_layout_reference() {
  local reference=$1 media_type=$2 digest=$3 size=$4
  jq -cnS \
    --arg reference "$reference" \
    --arg media_type "$media_type" \
    --arg digest "$digest" \
    --argjson size "$size" \
    '{schemaVersion: 2, manifests: [{
      mediaType: $media_type,
      digest: $digest,
      size: $size,
      annotations: {"org.opencontainers.image.ref.name": $reference}
    }]}' >"$combined_layout/index.json"
}

auth_file=$work/registry-auth.json
: >"$auth_file"
chmod 600 "$auth_file"
printf '%s' "$TERRABASE_RELEASE_REGISTRY_TOKEN" | skopeo login \
  --authfile "$auth_file" --username "$TERRABASE_RELEASE_REGISTRY_USERNAME" \
  --password-stdin "$registry_host" >/dev/null

repository=$TERRABASE_RELEASE_REGISTRY/$expected_package
verify_or_refuse_existing_tag() {
  local tag=$1 expected_manifest=$2 existing=$work/existing-${tag//[^A-Za-z0-9._-]/_}.json
  if skopeo inspect --raw --authfile "$auth_file" "docker://$repository:$tag" >"$existing" 2>/dev/null; then
    cmp -s "$expected_manifest" "$existing" || {
      echo "registry tag already names different immutable content: $repository:$tag" >&2
      exit 1
    }
  fi
}

push_and_verify() {
  local source=$1 tag=$2 expected_manifest=$3 label=$4 destination_manifest
  verify_or_refuse_existing_tag "$tag" "$expected_manifest"
  skopeo copy --all --preserve-digests --authfile "$auth_file" \
    "$source" "docker://$repository:$tag"
  destination_manifest=$work/registry-${tag//[^A-Za-z0-9._-]/_}.json
  skopeo inspect --raw --authfile "$auth_file" \
    "docker://$repository:$tag" >"$destination_manifest"
  cmp -s "$expected_manifest" "$destination_manifest" || {
    echo "registry $label manifest differs from the signed promotion candidate" >&2
    exit 1
  }
}

for arch in amd64 arm64; do
  arch_tag=$expected_revision-$arch
  push_and_verify \
    "oci-archive:${archive_by_arch[$arch]}:${archive_tag_by_arch[$arch]}" \
    "$arch_tag" "${manifest_raw_by_arch[$arch]}" "$arch"
done

write_layout_reference "$expected_revision" \
  application/vnd.oci.image.index.v1+json "$index_digest" "$index_size"
push_and_verify "oci:$combined_layout:$expected_revision" "$expected_revision" \
  "$index_manifest" 'multi-architecture index'

evidence_layers=$work/evidence-layers.jsonl
: >"$evidence_layers"
for arch in amd64 arm64; do
  for evidence_name in "$(basename -- "${manifest_by_arch[$arch]}")" SHA256SUMS SHA256SUMS.sig; do
    evidence_path=${directory_by_arch[$arch]}/$evidence_name
    [[ -f "$evidence_path" && ! -L "$evidence_path" ]] || {
      echo "signed promotion evidence is missing: $arch/$evidence_name" >&2
      exit 1
    }
    evidence_digest=sha256:$(sha256sum "$evidence_path" | awk '{print $1}')
    evidence_size=$(stat -c '%s' "$evidence_path")
    case "$evidence_name" in
      *.release.json) evidence_media_type=application/vnd.terrascale.release-manifest.v1+json ;;
      SHA256SUMS) evidence_media_type=text/plain ;;
      SHA256SUMS.sig) evidence_media_type=application/vnd.terrascale.ecdsa-p256-signature ;;
    esac
    copy_blob "$evidence_path"
    jq -cnS \
      --arg media_type "$evidence_media_type" \
      --arg digest "$evidence_digest" \
      --argjson size "$evidence_size" \
      --arg title "$arch/$evidence_name" \
      '{mediaType: $media_type, digest: $digest, size: $size,
        annotations: {"org.opencontainers.image.title": $title}}' >>"$evidence_layers"
  done
done

empty_config=$work/empty-config.json
printf '{}' >"$empty_config"
empty_config_digest=sha256:$(sha256sum "$empty_config" | awk '{print $1}')
empty_config_size=$(stat -c '%s' "$empty_config")
copy_blob "$empty_config"
evidence_manifest=$work/evidence-manifest.json
jq -cnS \
  --arg artifact_type application/vnd.terrascale.oci-promotion-signatures.v1 \
  --arg config_digest "$empty_config_digest" \
  --argjson config_size "$empty_config_size" \
  --arg subject_digest "$index_digest" \
  --argjson subject_size "$index_size" \
  --argjson layers "$(jq -s '.' "$evidence_layers")" \
  '{
    schemaVersion: 2,
    mediaType: "application/vnd.oci.image.manifest.v1+json",
    artifactType: $artifact_type,
    config: {mediaType: "application/vnd.oci.empty.v1+json", digest: $config_digest, size: $config_size},
    layers: $layers,
    subject: {mediaType: "application/vnd.oci.image.index.v1+json", digest: $subject_digest, size: $subject_size}
  }' >"$evidence_manifest"
evidence_digest=sha256:$(sha256sum "$evidence_manifest" | awk '{print $1}')
evidence_size=$(stat -c '%s' "$evidence_manifest")
copy_blob "$evidence_manifest"
fallback_tag=sha256-${index_digest#sha256:}.release-signatures
write_layout_reference "$fallback_tag" \
  application/vnd.oci.image.manifest.v1+json "$evidence_digest" "$evidence_size"
push_and_verify "oci:$combined_layout:$fallback_tag" "$fallback_tag" \
  "$evidence_manifest" 'signature evidence'

remote_evidence=$work/registry-${fallback_tag}.json
jq -e \
  --arg digest "$index_digest" \
  --argjson size "$index_size" \
  '.subject == {
    "mediaType": "application/vnd.oci.image.index.v1+json",
    "digest": $digest,
    "size": $size
  } and
  .artifactType == "application/vnd.terrascale.oci-promotion-signatures.v1" and
  (.layers | length) == 6' "$remote_evidence" >/dev/null || {
  echo "published signature evidence is not bound to the promoted index" >&2
  exit 1
}

printf 'published %s linux/amd64 %s@%s\n' \
  "$expected_package" "$repository" "${manifest_digest_by_arch[amd64]}"
printf 'published %s linux/arm64 %s@%s\n' \
  "$expected_package" "$repository" "${manifest_digest_by_arch[arm64]}"
printf 'published %s multi-architecture %s@%s\n' \
  "$expected_package" "$repository" "$index_digest"
printf 'published %s signed promotion evidence %s@%s fallback-tag=%s\n' \
  "$expected_package" "$repository" "$evidence_digest" "$fallback_tag"
