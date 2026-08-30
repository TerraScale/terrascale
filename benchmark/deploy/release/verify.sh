#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 ]] || {
  echo "usage: deploy/release/verify.sh <release-output-directory>" >&2
  exit 2
}
output=$1
[[ -d "$output" && ! -L "$output" ]] || {
  echo "release output is not a directory: $output" >&2
  exit 1
}

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-verify.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-verify.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT
umask 077
public_key=$work/public-key.pem
if [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_RELEASE_VERIFY_KEY_FILE" "$public_key"
elif [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_PEM-} ]]; then
  printf '%s\n' "$TERRABASE_RELEASE_VERIFY_KEY_PEM" >"$public_key"
else
  echo "independent release verification key is not configured" >&2
  exit 1
fi

[[ -z "$(find "$output" -type l -print -quit)" ]] || {
  echo "release output contains a symbolic link" >&2
  exit 1
}
manifest=$(find "$output" -maxdepth 1 -type f -name '*.release.json' -print)
[[ $(printf '%s\n' "$manifest" | sed '/^$/d' | wc -l) -eq 1 ]] || {
  echo "release output must contain exactly one release manifest" >&2
  exit 1
}

(
  cd "$output"
  sha256sum --check --strict SHA256SUMS
  openssl dgst -sha256 -verify "$public_key" -signature SHA256SUMS.sig SHA256SUMS
)

archive_name=$(jq -er '.artifacts.archive.name' "$manifest")
archive_digest=$(jq -er '.artifacts.archive.sha256' "$manifest")
oci_name=$(jq -er '.artifacts.ociArchive.name' "$manifest")
oci_digest=$(jq -er '.artifacts.ociArchive.sha256' "$manifest")
sbom_name=$(jq -er '.evidence.sbom' "$manifest")
provenance_name=$(jq -er '.evidence.provenance' "$manifest")
target=$(jq -er '.target' "$manifest")
binary=$(jq -er '.binary.name' "$manifest")

[[ $(sha256sum "$output/$archive_name" | awk '{print $1}') == "$archive_digest" ]]
[[ $(sha256sum "$output/$oci_name" | awk '{print $1}') == "$oci_digest" ]]
[[ $(jq -er '.metadata.component.hashes[] | select(.alg == "SHA-256") | .content' "$output/$sbom_name") == "$oci_digest" ]]
[[ $(jq -er '.subject | if length == 1 then .[0].digest.sha256 else error("provenance requires one subject") end' "$output/$provenance_name") == "$oci_digest" ]]

case "$target" in
  x86_64-unknown-linux-gnu) expected_arch=amd64 ;;
  aarch64-unknown-linux-gnu) expected_arch=arm64 ;;
  *) echo "unsupported target in release manifest: $target" >&2; exit 1 ;;
esac
image=$(skopeo inspect "oci-archive:$output/$oci_name")
[[ $(jq -er '.Architecture' <<<"$image") == "$expected_arch" ]]
[[ $(jq -er '.Os' <<<"$image") == linux ]]
[[ $(jq -er '.Config.Entrypoint | if length == 1 then .[0] else error("entrypoint must contain one command") end' <<<"$image") == "/usr/local/bin/$binary" ]]

expected_files=$(jq -rn \
  --arg archive "$archive_name" \
  --arg manifest "$(basename -- "$manifest")" \
  --arg oci "$oci_name" \
  --arg provenance "$provenance_name" \
  --arg sbom "$sbom_name" \
  '[$archive, $manifest, $oci, $provenance, $sbom, "SHA256SUMS", "SHA256SUMS.sig"] | sort | .[]')
observed_files=$(find "$output" -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)
[[ "$observed_files" == "$expected_files" ]] || {
  echo "release output contains missing or unexpected files" >&2
  diff -u <(printf '%s\n' "$expected_files") <(printf '%s\n' "$observed_files") >&2 || true
  exit 1
}

echo "verified signed release output $output"
