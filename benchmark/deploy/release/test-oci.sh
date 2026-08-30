#!/usr/bin/env bash
set -euo pipefail

# Exercise the OCI admission boundary with archives assembled from ordinary
# tar files. Buildah smoke coverage belongs in the release environment; these
# deterministic negative fixtures run on every developer and CI machine.
script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
verify=$script_dir/verify-oci.sh
test -x "$verify"
for tool in gzip jq sha256sum stat tar; do
  command -v "$tool" >/dev/null || {
    echo "required OCI fixture tool is missing: $tool" >&2
    exit 1
  }
done

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-oci-fixtures.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-oci-fixtures.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

base_reference='gcr.io/distroless/cc-debian12@sha256:9dac0a79194e45a7da0158a9c6da57b217585af0786db3845d1f0ec1a0dd182f'
tag=terrabase-server-1.2.3-amd64
native=$work/native/terrabase-server
mkdir -p "$(dirname -- "$native")"
printf 'the native release executable\n' >"$native"
chmod 755 "$native"
native_digest=$(sha256sum "$native" | awk '{print $1}')

make_layer() {
  local output=$1 kind=$2 root=$work/layer-root-$RANDOM-$RANDOM
  mkdir -p "$root/usr/local/bin"
  case "$kind" in
    correct)
      cp -- "$native" "$root/usr/local/bin/terrabase-server"
      ;;
    wrong)
      printf 'a different executable\n' >"$root/usr/local/bin/terrabase-server"
      chmod 755 "$root/usr/local/bin/terrabase-server"
      ;;
    remove)
      : >"$root/usr/local/bin/.wh.terrabase-server"
      ;;
    *)
      echo "unknown layer fixture: $kind" >&2
      exit 1
      ;;
  esac
  tar -cf "$output" -C "$root" usr/local/bin
}

make_image() {
  local output=$1 diff_mode=$2 digest_mode=$3 size_mode=$4
  shift 4
  local root=$work/image-$RANDOM-$RANDOM
  mkdir -p "$root/blobs/sha256"
  jq -n '{imageLayoutVersion:"1.0.0"}' >"$root/oci-layout"

  local layers='[]' diff_ids='[]' layer_number=0 layer actual_digest descriptor_digest size
  for layer in "$@"; do
    actual_digest=$(sha256sum "$layer" | awk '{print $1}')
    descriptor_digest=$actual_digest
    size=$(stat -c '%s' "$layer")
    if [[ "$layer_number" -eq 0 && "$digest_mode" == wrong ]]; then
      descriptor_digest=0000000000000000000000000000000000000000000000000000000000000000
    fi
    if [[ "$layer_number" -eq 0 && "$size_mode" == wrong ]]; then
      size=$((size + 1))
    fi
    cp -- "$layer" "$root/blobs/sha256/$actual_digest"
    layers=$(jq -cn --argjson old "$layers" --arg digest "sha256:$descriptor_digest" --argjson size "$size" \
      '$old + [{mediaType:"application/vnd.oci.image.layer.v1.tar",size:$size,digest:$digest}]')
    diff_id=$actual_digest
    if [[ "$layer_number" -eq 0 && "$diff_mode" == wrong ]]; then
      diff_id=0000000000000000000000000000000000000000000000000000000000000000
    fi
    diff_ids=$(jq -cn --argjson old "$diff_ids" --arg diff "sha256:$diff_id" '$old + [$diff]')
    layer_number=$((layer_number + 1))
  done

  local config=$root/config.json config_digest manifest=$root/manifest.json manifest_digest
  jq -n --arg base "$base_reference" --argjson diff_ids "$diff_ids" \
    '{architecture:"amd64",os:"linux",config:{Labels:{"tech.terrascale.terrabase.base":$base}},rootfs:{type:"layers",diff_ids:$diff_ids}}' \
    >"$config"
  config_digest=$(sha256sum "$config" | awk '{print $1}')
  cp -- "$config" "$root/blobs/sha256/$config_digest"
  jq -n --arg config "sha256:$config_digest" --argjson config_size "$(stat -c '%s' "$config")" --argjson layers "$layers" \
    '{schemaVersion:2,mediaType:"application/vnd.oci.image.manifest.v1+json",config:{mediaType:"application/vnd.oci.image.config.v1+json",size:$config_size,digest:$config},layers:$layers}' \
    >"$manifest"
  manifest_digest=$(sha256sum "$manifest" | awk '{print $1}')
  cp -- "$manifest" "$root/blobs/sha256/$manifest_digest"
  jq -n --arg tag "$tag" --arg manifest "sha256:$manifest_digest" --argjson size "$(stat -c '%s' "$manifest")" \
    '{schemaVersion:2,manifests:[{mediaType:"application/vnd.oci.image.manifest.v1+json",size:$size,digest:$manifest,annotations:{"org.opencontainers.image.ref.name":$tag}}]}' \
    >"$root/index.json"
  tar -cf "$output" -C "$root" oci-layout index.json blobs/sha256
}

repack() {
  local tree=$1 output=$2
  tar -cf "$output" -C "$tree" oci-layout index.json blobs/sha256
}

assert_rejected() {
  local label=$1 archive=$2
  if "$verify" "$archive" "$native" "$native_digest" "$tag" "$base_reference" >"$work/$label.log" 2>&1; then
    echo "$label unexpectedly succeeded" >&2
    return 1
  fi
}

correct_layer=$work/layer-correct.tar
wrong_layer=$work/layer-wrong.tar
remove_layer=$work/layer-remove.tar
make_layer "$correct_layer" correct
make_layer "$wrong_layer" wrong
make_layer "$remove_layer" remove

valid=$work/valid.oci.tar
make_image "$valid" normal normal normal "$correct_layer"
"$verify" "$valid" "$native" "$native_digest" "$tag" "$base_reference" >/dev/null

# A final whiteout removes the executable and must not be mistaken for a
# successful image. A later layer replacing it is accepted.
removed=$work/whiteout-remove.oci.tar
make_image "$removed" normal normal normal "$correct_layer" "$remove_layer"
assert_rejected whiteout-removal "$removed"
replaced=$work/whiteout-replacement.oci.tar
make_image "$replaced" normal normal normal "$correct_layer" "$remove_layer" "$correct_layer"
"$verify" "$replaced" "$native" "$native_digest" "$tag" "$base_reference" >/dev/null

wrong_executable=$work/wrong-executable.oci.tar
make_image "$wrong_executable" normal normal normal "$wrong_layer"
assert_rejected wrong-executable "$wrong_executable"
wrong_diff=$work/wrong-diff-id.oci.tar
make_image "$wrong_diff" wrong normal normal "$correct_layer"
assert_rejected corrupted-layer-diff-id "$wrong_diff"
wrong_size=$work/wrong-descriptor-size.oci.tar
make_image "$wrong_size" normal normal wrong "$correct_layer"
assert_rejected corrupted-descriptor-size "$wrong_size"
wrong_digest=$work/wrong-descriptor-digest.oci.tar
make_image "$wrong_digest" normal wrong normal "$correct_layer"
assert_rejected corrupted-descriptor-digest "$wrong_digest"

# Repack a valid tree to create outer-tar attacks without changing the valid
# descriptor graph.
valid_tree=$work/valid-tree
mkdir "$valid_tree"
tar -xf "$valid" -C "$valid_tree" --no-same-owner --no-same-permissions
duplicate=$work/duplicate-entry.oci.tar
repack "$valid_tree" "$duplicate"
tar -rf "$duplicate" -C "$valid_tree" index.json
assert_rejected duplicate-outer-entry "$duplicate"

traversal=$work/path-traversal.oci.tar
repack "$valid_tree" "$traversal"
tar -rf "$traversal" -C "$valid_tree" --transform='s#^#../#' oci-layout
assert_rejected outer-path-traversal "$traversal"

symlink=$work/symlink-entry.oci.tar
repack "$valid_tree" "$symlink"
ln -s -- /etc/passwd "$valid_tree/outer-link"
tar -rf "$symlink" -C "$valid_tree" outer-link
assert_rejected outer-symlink "$symlink"

fifo=$work/special-entry.oci.tar
repack "$valid_tree" "$fifo"
mkfifo "$valid_tree/outer-fifo"
tar -rf "$fifo" -C "$valid_tree" outer-fifo
assert_rejected outer-special-file "$fifo"

echo "OCI hostile archive fixtures passed"
