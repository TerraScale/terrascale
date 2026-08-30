#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 5 ]] || {
  echo "usage: deploy/release/verify-oci.sh <oci-archive> <native-binary> <binary-sha256> <oci-tag> <base-reference>" >&2
  exit 2
}
archive=$1
native_binary=$2
binary_digest=$3
oci_tag=$4
base_reference=$5

[[ -f "$archive" && ! -L "$archive" && -f "$native_binary" && ! -L "$native_binary" ]] || {
  echo "OCI or native binary input is not a safe regular file" >&2
  exit 1
}
[[ "$binary_digest" =~ ^[0-9a-f]{64}$ ]] || {
  echo "invalid native binary digest" >&2
  exit 1
}
[[ "$oci_tag" =~ ^[A-Za-z0-9_.-]+$ && "$base_reference" =~ ^.+@sha256:[0-9a-f]{64}$ ]] || {
  echo "invalid OCI identity" >&2
  exit 1
}
for tool in buildah find gzip jq sha256sum stat tar; do
  command -v "$tool" >/dev/null || {
    echo "required OCI verification tool is missing: $tool" >&2
    exit 1
  }
done

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-oci-verify.XXXXXXXX")
cleanup() {
  if [[ -n ${container-} ]]; then
    buildah --root "$work/buildah-storage" --runroot "$work/buildah-runroot" \
      --storage-driver vfs umount "$container" >/dev/null 2>&1 || true
    buildah --root "$work/buildah-storage" --runroot "$work/buildah-runroot" \
      --storage-driver vfs rm "$container" >/dev/null 2>&1 || true
  fi
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-oci-verify.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

oci_root=$work/oci
mkdir "$oci_root"

# Validate names and reject links before extracting an untrusted OCI tar.
while IFS= read -r entry; do
  entry=${entry%/}
  case "$entry" in
    oci-layout|index.json|blobs|blobs/sha256|blobs/sha256/[0-9a-f]*)
      [[ "$entry" != blobs/sha256/* || "${#entry}" -eq 77 ]] || {
        echo "OCI archive contains an invalid blob name: $entry" >&2
        exit 1
      }
      ;;
    *)
      echo "OCI archive contains an unexpected or unsafe entry: $entry" >&2
      exit 1
      ;;
  esac
done < <(tar -tf "$archive")
if tar -tvf "$archive" | awk 'substr($1, 1, 1) != "-" && substr($1, 1, 1) != "d" { exit 1 }'; then
  :
else
  echo "OCI archive contains a link or special file" >&2
  exit 1
fi
tar -xf "$archive" -C "$oci_root" --no-same-owner --no-same-permissions --no-overwrite-dir
[[ -z "$(find "$oci_root" -type l -print -quit)" ]] || {
  echo "extracted OCI archive contains a symbolic link" >&2
  exit 1
}

layout=$oci_root/oci-layout
index=$oci_root/index.json
jq -e '.imageLayoutVersion == "1.0.0"' "$layout" >/dev/null
[[ $(jq -er '.manifests | length' "$index") -eq 1 ]] || {
  echo "OCI index must contain exactly one manifest" >&2
  exit 1
}
jq -e --arg tag "$oci_tag" \
  '(.manifests[0].annotations["org.opencontainers.image.ref.name"] // $tag) == $tag' \
  "$index" >/dev/null

blob_path() {
  local digest=$1
  [[ "$digest" =~ ^sha256:([0-9a-f]{64})$ ]] || return 1
  printf '%s/blobs/sha256/%s\n' "$oci_root" "${BASH_REMATCH[1]}"
}
check_blob() {
  local digest=$1 expected_size=$2 path
  path=$(blob_path "$digest") || {
    echo "invalid OCI blob digest: $digest" >&2
    exit 1
  }
  [[ -f "$path" && ! -L "$path" ]] || {
    echo "OCI blob is missing: $digest" >&2
    exit 1
  }
  [[ "$expected_size" =~ ^[0-9]+$ && $(stat -c '%s' "$path") == "$expected_size" ]] || {
    echo "OCI blob size mismatch: $digest" >&2
    exit 1
  }
  [[ $(sha256sum "$path" | awk '{print $1}') == "${digest#sha256:}" ]] || {
    echo "OCI blob digest mismatch: $digest" >&2
    exit 1
  }
}

manifest_digest=$(jq -er '.manifests[0].digest' "$index")
manifest_size=$(jq -er '.manifests[0].size | numbers' "$index")
manifest_media=$(jq -er '.manifests[0].mediaType' "$index")
[[ "$manifest_media" == application/vnd.oci.image.manifest.v1+json || "$manifest_media" == application/vnd.docker.distribution.manifest.v2+json ]] || {
  echo "unsupported OCI manifest media type" >&2
  exit 1
}
check_blob "$manifest_digest" "$manifest_size"
manifest_path=$(blob_path "$manifest_digest")

config_digest=$(jq -er '.config.digest' "$manifest_path")
config_size=$(jq -er '.config.size | numbers' "$manifest_path")
config_media=$(jq -er '.config.mediaType' "$manifest_path")
[[ "$config_media" == application/vnd.oci.image.config.v1+json ]] || {
  echo "unsupported OCI config media type" >&2
  exit 1
}
check_blob "$config_digest" "$config_size"
config_path=$(blob_path "$config_digest")
jq -e --arg base "$base_reference" '.config.Labels["tech.terrascale.terrabase.base"] == $base' "$config_path" >/dev/null || {
  echo "OCI image does not declare the pinned release base" >&2
  exit 1
}

layer_count=$(jq -er '.layers | length' "$manifest_path")
diff_count=$(jq -er '.rootfs.diff_ids | length' "$config_path")
[[ "$layer_count" == "$diff_count" ]] || {
  echo "OCI config does not declare every image layer" >&2
  exit 1
}
for ((index_number = 0; index_number < layer_count; index_number++)); do
  layer_digest=$(jq -er ".layers[$index_number].digest" "$manifest_path")
  layer_size=$(jq -er ".layers[$index_number].size | numbers" "$manifest_path")
  layer_media=$(jq -er ".layers[$index_number].mediaType" "$manifest_path")
  case "$layer_media" in
    application/vnd.oci.image.layer.v1.tar|application/vnd.oci.image.layer.v1.tar+gzip|application/vnd.docker.image.rootfs.diff.tar|application/vnd.docker.image.rootfs.diff.tar.gzip) ;;
    *) echo "unsupported OCI layer media type: $layer_media" >&2; exit 1 ;;
  esac
  check_blob "$layer_digest" "$layer_size"
  layer_path=$(blob_path "$layer_digest")
  if [[ "$layer_media" == *+gzip || "$layer_media" == *.gzip ]]; then
    observed_diff=$(gzip -cd "$layer_path" | sha256sum | awk '{print $1}')
  else
    observed_diff=$(sha256sum "$layer_path" | awk '{print $1}')
  fi
  expected_diff=$(jq -er ".rootfs.diff_ids[$index_number]" "$config_path")
  [[ "$expected_diff" == sha256:$observed_diff ]] || {
    echo "OCI layer $index_number does not match its rootfs diff ID" >&2
    exit 1
  }
done

# Buildah applies the verified layer graph, so this checks the final merged
# filesystem rather than trusting a config label for the shipped executable.
container=$(buildah --root "$work/buildah-storage" --runroot "$work/buildah-runroot" \
  --storage-driver vfs from --name terrabase-oci-verify "oci-archive:$archive:$oci_tag")
mountpoint=$(buildah --root "$work/buildah-storage" --runroot "$work/buildah-runroot" \
  --storage-driver vfs mount "$container")
image_binary="$mountpoint/usr/local/bin/$(basename -- "$native_binary")"
[[ -f "$image_binary" && ! -L "$image_binary" ]] || {
  echo "OCI image executable is missing or unsafe" >&2
  exit 1
}
[[ $(sha256sum "$image_binary" | awk '{print $1}') == "$binary_digest" ]] || {
  echo "OCI image executable digest does not match the native artifact" >&2
  exit 1
}
cmp -s "$native_binary" "$image_binary" || {
  echo "OCI image executable bytes do not match the native artifact" >&2
  exit 1
}

echo "verified OCI layer graph, pinned base declaration, and executable bytes"
