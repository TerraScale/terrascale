#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-publish-fixtures.XXXXXXXX")
cleanup() {
  rm -rf -- "$work"
}
trap cleanup EXIT
mkdir -p "$work/bin" "$work/manifests" "$work/registry"

cat >"$work/bin/skopeo" <<'FAKE_SKOPEO'
#!/usr/bin/env bash
set -euo pipefail

fixture_root=${FAKE_OCI_FIXTURE_ROOT:?}
registry_state=${FAKE_REGISTRY_STATE:?}

reference_tag() {
  printf '%s\n' "${1##*:}"
}

write_oci_reference() {
  local reference=$1 destination=$2 body layout descriptor digest
  body=${reference#oci:}
  layout=${body%:*}
  descriptor=$(jq -cer --arg tag "${body##*:}" \
    '.manifests[] | select(.annotations["org.opencontainers.image.ref.name"] == $tag)' \
    "$layout/index.json")
  digest=$(jq -er '.digest' <<<"$descriptor")
  cp -- "$layout/blobs/sha256/${digest#sha256:}" "$destination"
}

write_source_manifest() {
  local reference=$1 destination=$2 tag arch
  case "$reference" in
    oci-archive:*)
      tag=$(reference_tag "$reference")
      case "$tag" in
        *-amd64) arch=amd64 ;;
        *-arm64) arch=arm64 ;;
        *) exit 2 ;;
      esac
      cp -- "$fixture_root/$arch.json" "$destination"
      ;;
    oci:*) write_oci_reference "$reference" "$destination" ;;
    *) exit 2 ;;
  esac
}

case ${1-} in
  login)
    cat >/dev/null
    ;;
  inspect)
    reference=${!#}
    raw=false
    for argument in "$@"; do
      [[ "$argument" == --raw ]] && raw=true
    done
    case "$reference" in
      oci-archive:*)
        if [[ "$raw" == true ]]; then
          write_source_manifest "$reference" /dev/stdout
        else
          tag=$(reference_tag "$reference")
          case "$tag" in
            *-amd64) arch=amd64 ;;
            *-arm64) arch=arm64 ;;
            *) exit 2 ;;
          esac
          jq -cn --arg arch "$arch" '{Architecture:$arch,Os:"linux"}'
        fi
        ;;
      oci:*)
        temporary=$(mktemp)
        trap 'rm -f -- "$temporary"' EXIT
        write_oci_reference "$reference" "$temporary"
        if [[ ${FAKE_STAGING_REBUILD-} == 1 \
          && ("$reference" == *:amd64 || "$reference" == *:arm64) ]]; then
          jq '.annotations.hostile = "rebuilt"' "$temporary"
        else
          cp -- "$temporary" /dev/stdout
        fi
        ;;
      docker://*)
        tag=$(reference_tag "$reference")
        state_file=$registry_state/$tag.json
        [[ -f "$state_file" ]] || exit 1
        cp -- "$state_file" /dev/stdout
        ;;
      *) exit 2 ;;
    esac
    ;;
  copy)
    source_reference=${@: -2:1}
    destination_reference=${@: -1}
    temporary=$(mktemp)
    trap 'rm -f -- "$temporary" "$temporary.next"' EXIT
    write_source_manifest "$source_reference" "$temporary"
    case "$destination_reference" in
      oci:*)
        body=${destination_reference#oci:}
        layout=${body%:*}
        tag=${body##*:}
        digest=sha256:$(sha256sum "$temporary" | awk '{print $1}')
        size=$(stat -c '%s' "$temporary")
        mkdir -p "$layout/blobs/sha256"
        cp -- "$temporary" "$layout/blobs/sha256/${digest#sha256:}"
        printf '{"imageLayoutVersion":"1.0.0"}\n' >"$layout/oci-layout"
        jq -cnS --arg tag "$tag" --arg digest "$digest" --argjson size "$size" \
          '{schemaVersion:2,manifests:[{
            mediaType:"application/vnd.oci.image.manifest.v1+json",
            digest:$digest,size:$size,
            annotations:{"org.opencontainers.image.ref.name":$tag}
          }]}' >"$layout/index.json"
        ;;
      docker://*)
        tag=$(reference_tag "$destination_reference")
        [[ ${FAKE_FAIL_TAG-} != "$tag" ]] || exit 1
        mutation=${FAKE_REGISTRY_MUTATION-}
        case "$mutation" in
          arch)
            if [[ "$tag" == *-amd64 || "$tag" == *-arm64 ]]; then
              jq '.schemaVersion = 3' "$temporary" >"$temporary.next"
              mv -- "$temporary.next" "$temporary"
            fi
            ;;
          index-missing)
            if jq -e '.mediaType == "application/vnd.oci.image.index.v1+json"' "$temporary" >/dev/null; then
              jq 'del(.manifests[1])' "$temporary" >"$temporary.next"
              mv -- "$temporary.next" "$temporary"
            fi
            ;;
          index-duplicate)
            if jq -e '.mediaType == "application/vnd.oci.image.index.v1+json"' "$temporary" >/dev/null; then
              jq '.manifests[1] = .manifests[0]' "$temporary" >"$temporary.next"
              mv -- "$temporary.next" "$temporary"
            fi
            ;;
          index-child-digest)
            if jq -e '.mediaType == "application/vnd.oci.image.index.v1+json"' "$temporary" >/dev/null; then
              jq '.manifests[0].digest = "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"' \
                "$temporary" >"$temporary.next"
              mv -- "$temporary.next" "$temporary"
            fi
            ;;
          index-child-size)
            if jq -e '.mediaType == "application/vnd.oci.image.index.v1+json"' "$temporary" >/dev/null; then
              jq '.manifests[0].size += 1' "$temporary" >"$temporary.next"
              mv -- "$temporary.next" "$temporary"
            fi
            ;;
          index-child-media-type)
            if jq -e '.mediaType == "application/vnd.oci.image.index.v1+json"' "$temporary" >/dev/null; then
              jq '.manifests[0].mediaType = "application/vnd.docker.distribution.manifest.v2+json"' \
                "$temporary" >"$temporary.next"
              mv -- "$temporary.next" "$temporary"
            fi
            ;;
          evidence-subject)
            if jq -e '.artifactType == "application/vnd.terrascale.oci-promotion-signatures.v1"' \
              "$temporary" >/dev/null; then
              jq '.subject.digest = "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"' \
                "$temporary" >"$temporary.next"
              mv -- "$temporary.next" "$temporary"
            fi
            ;;
          '') ;;
          *) exit 2 ;;
        esac
        mkdir -p "$registry_state"
        cp -- "$temporary" "$registry_state/$tag.json"
        ;;
      *) exit 2 ;;
    esac
    ;;
  *) exit 2 ;;
esac
FAKE_SKOPEO
chmod 755 "$work/bin/skopeo"

jq -cnS '{
  schemaVersion:2,
  mediaType:"application/vnd.oci.image.manifest.v1+json",
  config:{mediaType:"application/vnd.oci.image.config.v1+json",
    digest:"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",size:2},
  layers:[],annotations:{"fixture.architecture":"amd64"}
}' >"$work/manifests/amd64.json"
jq -cnS '{
  schemaVersion:2,
  mediaType:"application/vnd.oci.image.manifest.v1+json",
  config:{mediaType:"application/vnd.oci.image.config.v1+json",
    digest:"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",size:2},
  layers:[],annotations:{"fixture.architecture":"arm64"}
}' >"$work/manifests/arm64.json"

openssl ecparam -name prime256v1 -genkey -noout -out "$work/signing-key.pem"
openssl ec -in "$work/signing-key.pem" -pubout -out "$work/verify-key.pem" >/dev/null 2>&1

readonly revision=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
readonly tree=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb

resign_package() {
  local package_directory=$1 manifest archive
  manifest=$(find "$package_directory" -maxdepth 1 -type f -name '*.release.json' -print)
  archive=$(jq -er '.artifacts.ociArchive.name' "$manifest")
  (
    cd "$package_directory"
    LC_ALL=C sha256sum "$archive" "$(basename -- "$manifest")" >SHA256SUMS
    openssl dgst -sha256 -sign "$work/signing-key.pem" -out SHA256SUMS.sig SHA256SUMS
  )
}

make_package() {
  local arch=$1 target=$2 package_directory manifest_file manifest_digest manifest_size archive_digest
  package_directory=$work/package-$arch
  mkdir -p "$package_directory"
  printf 'fixture OCI archive for %s\n' "$arch" >"$package_directory/terrabase-server-$arch.oci.tar"
  archive_digest=$(sha256sum "$package_directory/terrabase-server-$arch.oci.tar" | awk '{print $1}')
  manifest_file=$work/manifests/$arch.json
  manifest_digest=sha256:$(sha256sum "$manifest_file" | awk '{print $1}')
  manifest_size=$(stat -c '%s' "$manifest_file")
  jq -n \
    --arg target "$target" \
    --arg arch "$arch" \
    --arg revision "$revision" \
    --arg tree "$tree" \
    --arg archive "terrabase-server-$arch.oci.tar" \
    --arg archive_digest "$archive_digest" \
    --arg manifest_digest "$manifest_digest" \
    --argjson manifest_size "$manifest_size" \
    '{
      schemaVersion:1,
      component:"server",
      package:"terrabase-server",
      version:"1.2.3",
      target:$target,
      source:{revision:$revision,tree:$tree,dirty:false},
      artifacts:{ociArchive:{name:$archive,sha256:$archive_digest}},
      ociImage:{
        manifestDigest:$manifest_digest,
        manifestSize:$manifest_size,
        mediaType:"application/vnd.oci.image.manifest.v1+json",
        os:"linux",
        architecture:$arch
      }
    }' >"$package_directory/terrabase-server-$arch.release.json"
  resign_package "$package_directory"
}

reset_fixture() {
  rm -rf -- "$work/package-amd64" "$work/package-arm64" "$work/registry"
  mkdir -p "$work/registry"
  make_package amd64 x86_64-unknown-linux-gnu
  make_package arm64 aarch64-unknown-linux-gnu
}

mutate_manifest() {
  local package_directory=$1 expression=$2 manifest temporary
  manifest=$(find "$package_directory" -maxdepth 1 -type f -name '*.release.json' -print)
  temporary=$manifest.next
  jq "$expression" "$manifest" >"$temporary"
  mv -- "$temporary" "$manifest"
  resign_package "$package_directory"
}

expect_failure() {
  local name=$1
  shift
  local stdout=$work/failure.stdout stderr=$work/failure.stderr
  : >"$stdout"
  : >"$stderr"
  if "$@" >"$stdout" 2>"$stderr"; then
    printf 'hostile publication case unexpectedly succeeded: %s\n' "$name" >&2
    exit 1
  fi
  if grep -Fq 'published ' "$stdout"; then
    printf 'failed publication reported partial success: %s\n' "$name" >&2
    exit 1
  fi
}

export PATH="$work/bin:$PATH"
export FAKE_OCI_FIXTURE_ROOT=$work/manifests
export FAKE_REGISTRY_STATE=$work/registry
export TERRABASE_RELEASE_REGISTRY=registry.example.test/terrascale
export TERRABASE_RELEASE_REGISTRY_USERNAME=publisher
export TERRABASE_RELEASE_REGISTRY_TOKEN=secret-fixture-token
export TERRABASE_RELEASE_VERIFY_KEY_FILE=$work/verify-key.pem

reset_fixture
publication_output=$work/publication.out
"$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64" >"$publication_output"
[[ $(grep -Fc 'published terrabase-server ' "$publication_output") -eq 4 ]]
index_file=$work/registry/$revision.json
[[ -f "$index_file" ]]
amd64_digest=sha256:$(sha256sum "$work/manifests/amd64.json" | awk '{print $1}')
arm64_digest=sha256:$(sha256sum "$work/manifests/arm64.json" | awk '{print $1}')
amd64_size=$(stat -c '%s' "$work/manifests/amd64.json")
arm64_size=$(stat -c '%s' "$work/manifests/arm64.json")
jq -e \
  --arg amd64_digest "$amd64_digest" --argjson amd64_size "$amd64_size" \
  --arg arm64_digest "$arm64_digest" --argjson arm64_size "$arm64_size" '
  .mediaType == "application/vnd.oci.image.index.v1+json" and
  .manifests == [
    {mediaType:"application/vnd.oci.image.manifest.v1+json",digest:$amd64_digest,size:$amd64_size,
      platform:{os:"linux",architecture:"amd64"}},
    {mediaType:"application/vnd.oci.image.manifest.v1+json",digest:$arm64_digest,size:$arm64_size,
      platform:{os:"linux",architecture:"arm64"}}
  ]' "$index_file" >/dev/null
index_digest=sha256:$(sha256sum "$index_file" | awk '{print $1}')
evidence_file=$(find "$work/registry" -maxdepth 1 -type f -name 'sha256-*.release-signatures.json' -print)
[[ -n "$evidence_file" ]]
jq -e --arg index_digest "$index_digest" '
  .subject.digest == $index_digest and
  .subject.mediaType == "application/vnd.oci.image.index.v1+json" and
  .artifactType == "application/vnd.terrascale.oci-promotion-signatures.v1" and
  (.layers | length) == 6 and
  ([.layers[].annotations["org.opencontainers.image.title"]] | unique | length) == 6
' "$evidence_file" >/dev/null

reset_fixture
mutate_manifest "$work/package-amd64" 'del(.ociImage.manifestDigest)'
expect_failure missing-signed-manifest-digest \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
mutate_manifest "$work/package-amd64" '.ociImage.manifestDigest = "sha256:not-a-digest"'
expect_failure malformed-signed-manifest-digest \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
mutate_manifest "$work/package-amd64" \
  '.ociImage.manifestDigest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"'
expect_failure mismatched-signed-manifest-digest \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
mutate_manifest "$work/package-amd64" '.ociImage.architecture = "arm64"'
expect_failure architecture-swap \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
amd64_manifest_digest=sha256:$(sha256sum "$work/manifests/amd64.json" | awk '{print $1}')
amd64_manifest_size=$(stat -c '%s' "$work/manifests/amd64.json")
mutate_manifest "$work/package-arm64" \
  ".target = \"x86_64-unknown-linux-gnu\" |
   .ociImage.architecture = \"amd64\" |
   .ociImage.manifestDigest = \"$amd64_manifest_digest\" |
   .ociImage.manifestSize = $amd64_manifest_size"
expect_failure duplicate-architecture \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
mutate_manifest "$work/package-arm64" '.version = "1.2.4"'
expect_failure version-mismatch \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
mutate_manifest "$work/package-arm64" \
  '.source.tree = "dddddddddddddddddddddddddddddddddddddddd"'
expect_failure tree-mismatch \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
rm -- "$work/package-amd64/SHA256SUMS.sig"
expect_failure unsigned-package \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
printf 'post-signature mutation\n' >>"$work/package-amd64/terrabase-server-amd64.oci.tar"
expect_failure post-signature-mutation \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
expect_failure staging-rebuild \
  env FAKE_STAGING_REBUILD=1 "$script_dir/publish-oci.sh" \
  "$work/package-amd64" "$work/package-arm64"

for registry_mutation in \
  arch index-missing index-duplicate index-child-digest index-child-size \
  index-child-media-type evidence-subject; do
  reset_fixture
  expect_failure "registry-$registry_mutation" \
    env FAKE_REGISTRY_MUTATION="$registry_mutation" "$script_dir/publish-oci.sh" \
    "$work/package-amd64" "$work/package-arm64"
done

reset_fixture
expect_failure partial-publication \
  env FAKE_FAIL_TAG="$revision" "$script_dir/publish-oci.sh" \
  "$work/package-amd64" "$work/package-arm64"

reset_fixture
printf '{"schemaVersion":3}\n' >"$work/registry/$revision-amd64.json"
expect_failure existing-tag-conflict \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"

reset_fixture
expect_failure tagged-registry-destination \
  env TERRABASE_RELEASE_REGISTRY=registry.example.test/terrascale:latest \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"
expect_failure digest-qualified-registry-destination \
  env TERRABASE_RELEASE_REGISTRY=registry.example.test/terrascale@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
  "$script_dir/publish-oci.sh" "$work/package-amd64" "$work/package-arm64"
expect_failure missing-registry-token \
  env TERRABASE_RELEASE_REGISTRY_TOKEN= "$script_dir/publish-oci.sh" \
  "$work/package-amd64" "$work/package-arm64"

echo "immutable multi-architecture registry publication fixtures passed"
