#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: deploy/release/package.sh <server|studio> <x86_64-unknown-linux-gnu|aarch64-unknown-linux-gnu> <output-directory>" >&2
  exit 2
}

[[ $# -eq 3 ]] || usage
component=$1
target=$2
output=$3

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
cd "$repository_root"

case "$component" in
  server)
    package=terrabase-server
    binary=terrabase-server
    manifest=Cargo.toml
    state_directory=terrabase
    containerfile=$script_dir/Containerfile.server
    ;;
  studio)
    package=terrabase-studio
    binary=terrabase-studio
    manifest=crates/terrabase-studio/Cargo.toml
    state_directory=terrabase-studio
    containerfile=$script_dir/Containerfile.studio
    ;;
  *) usage ;;
esac

case "$target" in
  x86_64-unknown-linux-gnu)
    oci_arch=amd64
    elf_machine='Advanced Micro Devices X86-64'
    ;;
  aarch64-unknown-linux-gnu)
    oci_arch=arm64
    elf_machine=AArch64
    ;;
  *) usage ;;
esac

for tool in buildah cargo date file git gzip jq readelf rustc sha256sum skopeo tar; do
  command -v "$tool" >/dev/null || {
    echo "required release tool is missing: $tool" >&2
    exit 1
  }
done

"$script_dir/require-fips-toolchain.sh"

readonly expected_toolchain_revision=c5c4a43b0e8056328ec4529f735cabdb8f1942bb
readonly expected_toolchain_nar_hash='sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei/uiU9vrMqOiwWP1Y='
[[ ${TERRABASE_NATIVE_TOOLCHAIN_REVISION-} == "$expected_toolchain_revision" ]] || {
  echo "release packaging requires the pinned native Nixpkgs toolchain" >&2
  exit 1
}
[[ ${TERRABASE_NATIVE_TOOLCHAIN_NAR_HASH-} == "$expected_toolchain_nar_hash" ]]
[[ ${TERRABASE_NATIVE_TOOLCHAIN_TARGET-} == "$target" ]]

rust_version=$(rustc --version)
[[ "$rust_version" == 'rustc 1.97.1 (8bab26f4f 2026-07-14)' ]] || {
  echo "release packaging requires rustc 1.97.1, observed: $rust_version" >&2
  exit 1
}
cargo_version=$(cargo --version)
[[ "$cargo_version" == 'cargo 1.97.1 (c980f4866 2026-06-30)' ]] || {
  echo "release packaging requires Cargo 1.97.1, observed: $cargo_version" >&2
  exit 1
}
host_target=$(rustc -vV | sed -n 's/^host: //p')
[[ "$host_target" == "$target" ]] || {
  echo "native release runner mismatch: requested $target, observed $host_target" >&2
  exit 1
}

ambient_flags="${RUSTFLAGS-} ${CARGO_ENCODED_RUSTFLAGS-}"
[[ "$ambient_flags" != *target-cpu* ]] || {
  echo "generic release artifacts refuse target-cpu flags; tuned artifacts require a separate qualification" >&2
  exit 1
}
# Cargo gives CARGO_ENCODED_RUSTFLAGS precedence in some invocation paths.
# Refuse ambient encoded flags entirely so the reproducibility contract is
# controlled by this script's explicit release flags below.
unset CARGO_ENCODED_RUSTFLAGS

source_dirty=false
if ! git diff --quiet --ignore-submodules -- \
  || ! git diff --cached --quiet --ignore-submodules -- \
  || [[ -n "$(git ls-files --others --exclude-standard)" ]]; then
  if [[ ${TERRABASE_ALLOW_DIRTY_PACKAGE-0} != 1 ]]; then
    echo "release packaging requires a clean worktree" >&2
    exit 1
  fi
  source_dirty=true
fi

[[ ! -e "$output" && ! -L "$output" ]] || {
  echo "release output already exists: $output" >&2
  exit 1
}

source_revision=$(git rev-parse HEAD)
source_tree=$(git rev-parse 'HEAD^{tree}')
source_epoch=${SOURCE_DATE_EPOCH:-$(git show -s --format=%ct HEAD)}
[[ "$source_epoch" =~ ^[0-9]+$ ]] || {
  echo "SOURCE_DATE_EPOCH must be an integer Unix timestamp" >&2
  exit 1
}
created=$(date -u -d "@$source_epoch" '+%Y-%m-%dT%H:%M:%SZ')

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-release.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-release.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

capture_source_state() {
  git rev-parse --verify HEAD
  git rev-parse --verify 'HEAD^{tree}'
  git diff --binary --full-index --no-ext-diff HEAD --
  while IFS= read -r -d '' untracked; do
    printf 'untracked\0%s\0' "$untracked"
    if [[ -L $untracked ]]; then
      printf 'symlink\0'
      readlink -z -- "$untracked"
    elif [[ -f $untracked ]]; then
      printf 'file\0'
      sha256sum -- "$untracked" | awk '{print $1}'
    else
      printf 'unsupported\0'
    fi
  done < <(git ls-files --others --exclude-standard -z | LC_ALL=C sort -z)
}

source_state_before=$work/source-state.before
source_state_after=$work/source-state.after
capture_source_state >"$source_state_before"

native_tools_json=$work/native-tools.json
"$script_dir/native-toolchain-identity.sh" "$target" >"$native_tools_json"
require_tool_path() {
  local command_name=$1
  local policy_path=$2
  local actual expected
  actual=$(readlink -f "$(command -v "$command_name")")
  expected=$(jq -er "$policy_path" "$native_tools_json")
  [[ "$actual" == "$expected" ]] || {
    echo "release tool identity mismatch for $command_name: $actual != $expected" >&2
    exit 1
  }
}
[[ "$(readlink -f "${CC:?}")" == "$(jq -er '.compiler.cc.path' "$native_tools_json")" ]]
[[ "$(readlink -f "${CXX:?}")" == "$(jq -er '.compiler.cxx.path' "$native_tools_json")" ]]
[[ "$(readlink -f "${CMAKE:?}")" == "$(jq -er '.cmake.path' "$native_tools_json")" ]]
[[ "$(readlink -f "${CMAKE_MAKE_PROGRAM:?}")" == "$(jq -er '.ninja.path' "$native_tools_json")" ]]
[[ "$(readlink -f "${PERL:?}")" == "$(jq -er '.perl.path' "$native_tools_json")" ]]
[[ "$(readlink -f "${GO:?}")" == "$(jq -er '.go.path' "$native_tools_json")" ]]
linker_variable=CARGO_TARGET_$(printf '%s' "$target" | tr '[:lower:]-' '[:upper:]_')_LINKER
[[ "$(readlink -f "${!linker_variable:?}")" == "$(jq -er '.rustToolchain.linker' "$native_tools_json")" ]]
[[ $(sha256sum "$script_dir/sbom/Cargo.lock" | awk '{print $1}') == \
  "$(jq -er '.sbomGenerator.lockSha256' "$native_tools_json")" ]]
require_tool_path buildah '.artifactShaping.buildah.path'
require_tool_path skopeo '.artifactShaping.skopeo.path'
require_tool_path jq '.artifactShaping.jq.path'
require_tool_path readelf '.artifactShaping.readelf.path'
require_tool_path gzip '.artifactShaping.gzip.path'
require_tool_path tar '.artifactShaping.tar.path'
require_tool_path sha256sum '.artifactShaping.sha256sum.path'
require_tool_path file '.artifactShaping.file.path'

export CARGO_INCREMENTAL=0
export CARGO_PROFILE_RELEASE_DEBUG=0
export CARGO_PROFILE_RELEASE_PANIC=abort
export CARGO_PROFILE_RELEASE_STRIP=symbols
export RUSTFLAGS="-C debuginfo=0 --remap-path-prefix=$repository_root=/usr/src/terrabase"
export SOURCE_DATE_EPOCH=$source_epoch

sbom_tool=${TERRABASE_SBOM_TOOL-}
if [[ -z "$sbom_tool" ]]; then
  cargo build \
    --manifest-path "$script_dir/sbom/Cargo.toml" \
    --locked \
    --release \
    --target-dir "$work/sbom-target"
  sbom_tool=$work/sbom-target/release/terrabase-release-sbom
fi
[[ -f "$sbom_tool" && ! -L "$sbom_tool" && -x "$sbom_tool" ]] || {
  echo "release SBOM generator is missing or unsafe: $sbom_tool" >&2
  exit 1
}

metadata=$work/cargo-metadata.json
cargo metadata \
  --manifest-path "$manifest" \
  --locked \
  --no-default-features \
  --features production-fips \
  --filter-platform "$target" \
  --format-version 1 >"$metadata"
version=$(jq -er --arg package "$package" '[.packages[] | select(.name == $package) | .version] | if length == 1 then .[0] else error("package identity is not unique") end' "$metadata")

fips_feature_graph=
{
  feature_tree_arguments=(
    --manifest-path "$manifest"
    --locked
    --package "$package"
    --target "$target"
    --no-default-features
    --features production-fips
    --edges features
    --charset ascii
  )
  feature_tree_raw=$work/production-fips.features.raw
  fips_feature_graph=$work/production-fips.features.txt
  {
    printf 'target: %s\n' "$target"
    printf 'selected features: production-fips\n'
    printf '\n[terrabase]\n'
    cargo tree "${feature_tree_arguments[@]}" --invert terrabase
    printf '\n[aws-lc-rs]\n'
    cargo tree "${feature_tree_arguments[@]}" --invert aws-lc-rs
    printf '\n[aws-lc-fips-sys]\n'
    cargo tree "${feature_tree_arguments[@]}" --invert aws-lc-fips-sys
  } >"$feature_tree_raw"
  sed "s|$repository_root|/usr/src/terrabase|g" "$feature_tree_raw" >"$fips_feature_graph"
  grep -Fq 'terrabase feature "production-fips"' "$fips_feature_graph" || {
    echo "production $component feature graph does not select terrabase/production-fips" >&2
    exit 1
  }
  grep -Fq 'aws-lc-rs feature "fips"' "$fips_feature_graph" || {
    echo "production $component feature graph does not select aws-lc-rs/fips" >&2
    exit 1
  }
  grep -Fq 'aws-lc-fips-sys v' "$fips_feature_graph" || {
    echo "production $component feature graph does not contain aws-lc-fips-sys" >&2
    exit 1
  }
  if grep -Fq 'terrabase feature "local-crypto"' "$fips_feature_graph" \
    || grep -Fq 'aws-lc-rs feature "non-fips"' "$fips_feature_graph"; then
    echo "production $component feature graph contains a local or non-FIPS crypto feature" >&2
    exit 1
  fi
}

target_directory=$work/cargo-target
build_arguments=(
  --manifest-path "$manifest"
  --locked
  --release
  --package "$package"
  --target "$target"
  --target-dir "$target_directory"
)
build_arguments+=(--no-default-features --features production-fips)
cargo build "${build_arguments[@]}"
"$script_dir/verify-fips-cmake-caches.sh" "$target_directory"
built_binary=$target_directory/$target/release/$binary
[[ -x "$built_binary" ]] || {
  echo "release binary was not produced: $built_binary" >&2
  exit 1
}
readelf -h "$built_binary" | grep -Fq "Machine:                           $elf_machine" || {
  echo "release binary architecture does not match $target" >&2
  exit 1
}
binary_digest=$(sha256sum "$built_binary" | awk '{print $1}')
printf 'binary sha256: %s\n' "$binary_digest" >>"$fips_feature_graph"

mkdir -p "$(dirname -- "$output")"
mkdir "$output"

artifact_stem=$package-$version-$target
archive_name=$artifact_stem.tar.gz
oci_name=$artifact_stem.oci.tar
sbom_name=$artifact_stem.cdx.json
provenance_name=$artifact_stem.intoto.json
manifest_name=$artifact_stem.release.json
fips_feature_graph_name=
fips_feature_graph_digest=
fips_feature_graph_name=$artifact_stem.fips-features.txt
install -m 0644 "$fips_feature_graph" "$output/$fips_feature_graph_name"
fips_feature_graph_digest=$(sha256sum "$output/$fips_feature_graph_name" | awk '{print $1}')

archive_root=$work/archive/$artifact_stem
install -D -m 0755 "$built_binary" "$archive_root/bin/$binary"
install -D -m 0644 LICENSE.md "$archive_root/LICENSE.md"
install -D -m 0644 "$script_dir/README.md" "$archive_root/RELEASE.md"
tar \
  --sort=name \
  --mtime="@$source_epoch" \
  --owner=0 \
  --group=0 \
  --numeric-owner \
  --format=posix \
  -C "$work/archive" \
  -cf - "$artifact_stem" | gzip -n -9 >"$output/$archive_name"

image_context=$work/image
install -D -m 0755 "$built_binary" "$image_context/rootfs/usr/local/bin/$binary"
install -d -m 0750 "$image_context/rootfs/var/lib/$state_directory"
image_ref=localhost/$package:$version-$oci_arch
oci_tag=$package-$version-$oci_arch
buildah \
  --root "$work/container-storage" \
  --runroot "$work/container-runroot" \
  --storage-driver vfs \
  bud \
  --format oci \
  --layers=false \
  --no-cache \
  --source-date-epoch "$source_epoch" \
  --rewrite-timestamp \
  --pull=always \
  --platform "linux/$oci_arch" \
  --build-arg "TERRABASE_VERSION=$version" \
  --build-arg "TERRABASE_REVISION=$source_revision" \
  --build-arg "TERRABASE_CREATED=$created" \
  --build-arg "TERRABASE_BINARY_SHA256=$binary_digest" \
  --tag "$image_ref" \
  --file "$containerfile" \
  "$image_context"
buildah \
  --root "$work/container-storage" \
  --runroot "$work/container-runroot" \
  --storage-driver vfs \
  push \
  --format oci \
  "$image_ref" \
  "oci-archive:$output/$oci_name:$oci_tag"

oci_manifest_raw=$work/oci-manifest.json
skopeo inspect --raw "oci-archive:$output/$oci_name:$oci_tag" >"$oci_manifest_raw"
oci_manifest_digest=sha256:$(sha256sum "$oci_manifest_raw" | awk '{print $1}')
oci_manifest_size=$(stat -c '%s' "$oci_manifest_raw")
oci_manifest_media_type=$(jq -er '.mediaType' "$oci_manifest_raw")
[[ "$oci_manifest_media_type" == application/vnd.oci.image.manifest.v1+json ]] || {
  echo "OCI archive contains an unsupported image manifest media type" >&2
  exit 1
}
image_metadata=$(skopeo inspect "oci-archive:$output/$oci_name:$oci_tag")
observed_arch=$(jq -er '.Architecture' <<<"$image_metadata")
observed_os=$(jq -er '.Os' <<<"$image_metadata")
[[ "$observed_arch" == "$oci_arch" && "$observed_os" == linux ]] || {
  echo "OCI archive platform mismatch: expected linux/$oci_arch, observed $observed_os/$observed_arch" >&2
  exit 1
}

oci_digest=$(sha256sum "$output/$oci_name" | awk '{print $1}')
archive_digest=$(sha256sum "$output/$archive_name" | awk '{print $1}')
lock_digest=$(sha256sum "$(dirname -- "$manifest")/Cargo.lock" | awk '{print $1}')

"$sbom_tool" \
  --metadata "$metadata" \
  --root "$package" \
  --artifact "$output/$oci_name" \
  --target "$target" \
  --output "$output/$sbom_name"

jq -n \
  --arg name "$oci_name" \
  --arg digest "$oci_digest" \
  --arg repository 'https://git.terrascale.tech/TerraScale/TerraBase' \
  --arg revision "$source_revision" \
  --arg tree "$source_tree" \
  --arg target "$target" \
  --arg component "$component" \
  --arg rust "$rust_version" \
  --arg lock_digest "$lock_digest" \
  --arg oci_manifest_digest "$oci_manifest_digest" \
  --argjson oci_manifest_size "$oci_manifest_size" \
  --arg oci_manifest_media_type "$oci_manifest_media_type" \
  --arg oci_os "$observed_os" \
  --arg oci_architecture "$observed_arch" \
  --arg fips_feature_graph "$fips_feature_graph_name" \
  --arg fips_feature_graph_digest "$fips_feature_graph_digest" \
  --argjson native_tools "$(<"$native_tools_json")" \
  --arg base 'gcr.io/distroless/cc-debian12@sha256:9dac0a79194e45a7da0158a9c6da57b217585af0786db3845d1f0ec1a0dd182f' \
  --arg archive_name "$archive_name" \
  --arg archive_digest "$archive_digest" \
  --argjson source_dirty "$source_dirty" \
  '{
    "_type": "https://in-toto.io/Statement/v1",
    "subject": [
      {"name": $archive_name, "digest": {"sha256": $archive_digest}},
      {"name": $name, "digest": {"sha256": $digest}}
    ],
    "predicateType": "https://slsa.dev/provenance/v1",
    "predicate": {
      "buildDefinition": {
        "buildType": "https://terrascale.tech/build/terrabase-native-oci/v1",
        "externalParameters": {"target": $target, "profile": "release", "sourceDirty": $source_dirty},
        "internalParameters": {
          "rustc": $rust,
          "cargoLockSha256": $lock_digest,
          "nativeTools": $native_tools,
          "ociImage": {
            "manifestDigest": $oci_manifest_digest,
            "manifestSize": $oci_manifest_size,
            "mediaType": $oci_manifest_media_type,
            "os": $oci_os,
            "architecture": $oci_architecture
          },
          "fipsFeatureGraph": {
            "name": $fips_feature_graph,
            "sha256": $fips_feature_graph_digest
          }
        },
        "resolvedDependencies": [
          {"uri": $repository, "digest": {"gitCommit": $revision, "gitTree": $tree}},
          {"uri": $base}
        ]
      },
      "runDetails": {"builder": {"id": "https://git.terrascale.tech/TerraScale/TerraBase/.forgejo/workflows/release.yml"}}
    }
  }' >"$output/$provenance_name"

jq -n \
  --arg component "$component" \
  --arg package "$package" \
  --arg version "$version" \
  --arg target "$target" \
  --arg revision "$source_revision" \
  --arg tree "$source_tree" \
  --arg created "$created" \
  --arg binary "$binary" \
  --arg binary_digest "$binary_digest" \
  --arg archive "$archive_name" \
  --arg archive_digest "$archive_digest" \
  --arg oci "$oci_name" \
  --arg oci_digest "$oci_digest" \
  --arg oci_manifest_digest "$oci_manifest_digest" \
  --argjson oci_manifest_size "$oci_manifest_size" \
  --arg oci_manifest_media_type "$oci_manifest_media_type" \
  --arg oci_os "$observed_os" \
  --arg oci_architecture "$observed_arch" \
  --arg sbom "$sbom_name" \
  --arg provenance "$provenance_name" \
  --arg fips_feature_graph "$fips_feature_graph_name" \
  --arg fips_feature_graph_digest "$fips_feature_graph_digest" \
  --argjson source_dirty "$source_dirty" \
  '{
    schemaVersion: 1,
    component: $component,
    package: $package,
    version: $version,
    target: $target,
    source: {revision: $revision, tree: $tree, created: $created, dirty: $source_dirty},
    binary: {name: $binary, sha256: $binary_digest},
    artifacts: {
      archive: {name: $archive, sha256: $archive_digest},
      ociArchive: {name: $oci, sha256: $oci_digest}
    },
    ociImage: {
      manifestDigest: $oci_manifest_digest,
      manifestSize: $oci_manifest_size,
      mediaType: $oci_manifest_media_type,
      os: $oci_os,
      architecture: $oci_architecture
    },
    evidence: (
      {
        sbom: $sbom,
        provenance: $provenance,
        checksums: "SHA256SUMS",
        fipsFeatureGraph: {
          name: $fips_feature_graph,
          sha256: $fips_feature_graph_digest
        }
      } +
      if $component == "server" then {
        productionCapability: {
          statement: "PRODUCTION-CAPABILITY.json",
          signature: "PRODUCTION-CAPABILITY.sig",
          clusterComposition: "cluster-composition.evidence.json",
          encryptionAtRest: "encryption-at-rest.evidence.json"
        }
      } else {} end
    ),
    signature: {file: "SHA256SUMS.sig", algorithm: "ecdsa-p256-sha256"}
  }' >"$output/$manifest_name"

(
  cd "$output"
  checksum_files=("$archive_name" "$manifest_name" "$oci_name" "$provenance_name" "$sbom_name")
  checksum_files+=("$fips_feature_graph_name")
  LC_ALL=C sha256sum "${checksum_files[@]}" >SHA256SUMS
  sha256sum --check --strict SHA256SUMS
)

"$script_dir/verify.sh" --unsigned "$output"

capture_source_state >"$source_state_after"
cmp -s "$source_state_before" "$source_state_after" || {
  echo "repository source changed while release artifacts were being built" >&2
  exit 1
}

echo "packaged unsigned $package $version for $target at $output"
