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

for tool in buildah cargo date file git gzip jq python3 readelf rustc sha256sum skopeo tar; do
  command -v "$tool" >/dev/null || {
    echo "required release tool is missing: $tool" >&2
    exit 1
  }
done

rust_version=$(rustc --version)
[[ "$rust_version" == 'rustc 1.97.1 '* ]] || {
  echo "release packaging requires rustc 1.97.1, observed: $rust_version" >&2
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

export CARGO_INCREMENTAL=0
export CARGO_PROFILE_RELEASE_DEBUG=0
export CARGO_PROFILE_RELEASE_PANIC=abort
export CARGO_PROFILE_RELEASE_STRIP=symbols
export RUSTFLAGS="-C debuginfo=0 --remap-path-prefix=$repository_root=/usr/src/terrabase"
export SOURCE_DATE_EPOCH=$source_epoch

metadata=$work/cargo-metadata.json
cargo metadata \
  --manifest-path "$manifest" \
  --locked \
  --filter-platform "$target" \
  --format-version 1 >"$metadata"
version=$(jq -er --arg package "$package" '[.packages[] | select(.name == $package) | .version] | if length == 1 then .[0] else error("package identity is not unique") end' "$metadata")

target_directory=$work/cargo-target
cargo build \
  --manifest-path "$manifest" \
  --locked \
  --release \
  --package "$package" \
  --target "$target" \
  --target-dir "$target_directory"
built_binary=$target_directory/$target/release/$binary
[[ -x "$built_binary" ]] || {
  echo "release binary was not produced: $built_binary" >&2
  exit 1
}
readelf -h "$built_binary" | grep -Fq "Machine:                           $elf_machine" || {
  echo "release binary architecture does not match $target" >&2
  exit 1
}

mkdir -p "$(dirname -- "$output")"
mkdir "$output"

artifact_stem=$package-$version-$target
archive_name=$artifact_stem.tar.gz
oci_name=$artifact_stem.oci.tar
sbom_name=$artifact_stem.cdx.json
provenance_name=$artifact_stem.intoto.json
manifest_name=$artifact_stem.release.json

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
binary_digest=$(sha256sum "$built_binary" | awk '{print $1}')
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
  --pull=always \
  --platform "linux/$oci_arch" \
  --timestamp "$source_epoch" \
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

oci_digest=$(sha256sum "$output/$oci_name" | awk '{print $1}')
archive_digest=$(sha256sum "$output/$archive_name" | awk '{print $1}')
lock_digest=$(sha256sum "$(dirname -- "$manifest")/Cargo.lock" | awk '{print $1}')

python3 "$script_dir/generate_sbom.py" \
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
  --arg rust "$rust_version" \
  --arg lock_digest "$lock_digest" \
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
        "internalParameters": {"rustc": $rust, "cargoLockSha256": $lock_digest},
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
  --arg sbom "$sbom_name" \
  --arg provenance "$provenance_name" \
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
    evidence: (
      {sbom: $sbom, provenance: $provenance, checksums: "SHA256SUMS"} +
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
  LC_ALL=C sha256sum \
    "$archive_name" \
    "$manifest_name" \
    "$oci_name" \
    "$provenance_name" \
    "$sbom_name" >SHA256SUMS
  sha256sum --check --strict SHA256SUMS
)

observed_arch=$(skopeo inspect "oci-archive:$output/$oci_name:$oci_tag" | jq -er '.Architecture')
[[ "$observed_arch" == "$oci_arch" ]] || {
  echo "OCI archive architecture mismatch: expected $oci_arch, observed $observed_arch" >&2
  exit 1
}

"$script_dir/verify.sh" --unsigned "$output"

echo "packaged unsigned $package $version for $target at $output"
