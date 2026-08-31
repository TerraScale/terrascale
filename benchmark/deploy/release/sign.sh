#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 ]] || {
  echo "usage: deploy/release/sign.sh <release-output-directory>" >&2
  exit 2
}
output=$1
[[ -d "$output" && ! -L "$output" ]] || {
  echo "release output is not a directory: $output" >&2
  exit 1
}
[[ -f "$output/SHA256SUMS" && ! -L "$output/SHA256SUMS" ]] || {
  echo "release output has no regular SHA256SUMS" >&2
  exit 1
}
[[ ! -e "$output/SHA256SUMS.sig" && ! -L "$output/SHA256SUMS.sig" ]] || {
  echo "release output is already signed" >&2
  exit 1
}

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
"$script_dir/verify.sh" --unsigned "$output"
manifest=$(find "$output" -maxdepth 1 -type f -name '*.release.json' -print)
[[ $(jq -er '.source.dirty' "$manifest") == false ]] || {
  echo "a dirty source package cannot be signed for release" >&2
  exit 1
}
component=$(jq -er '.component' "$manifest")
if [[ "$component" == server ]]; then
  "$script_dir/require-production-capability.sh" "$output"
  statement=${TERRABASE_PRODUCTION_CAPABILITY_STATEMENT:?production capability statement is required}
  signature=${TERRABASE_PRODUCTION_CAPABILITY_SIGNATURE:?production capability signature is required}
  evidence_directory=${TERRABASE_PRODUCTION_CAPABILITY_EVIDENCE_DIRECTORY:-$(dirname -- "$statement")}
  install -m 0644 "$statement" "$output/PRODUCTION-CAPABILITY.json"
  install -m 0644 "$signature" "$output/PRODUCTION-CAPABILITY.sig"
  install -m 0644 "$evidence_directory/cluster-composition.evidence.json" "$output/cluster-composition.evidence.json"
  install -m 0644 "$evidence_directory/encryption-at-rest.evidence.json" "$output/encryption-at-rest.evidence.json"
fi

archive_name=$(jq -er '.artifacts.archive.name' "$manifest")
oci_name=$(jq -er '.artifacts.ociArchive.name' "$manifest")
sbom_name=$(jq -er '.evidence.sbom' "$manifest")
provenance_name=$(jq -er '.evidence.provenance' "$manifest")
manifest_name=$(basename -- "$manifest")
checksum_files=("$archive_name" "$manifest_name" "$oci_name" "$provenance_name" "$sbom_name")
fips_feature_graph_name=$(jq -er '.evidence.fipsFeatureGraph.name' "$manifest")
checksum_files+=("$fips_feature_graph_name")
if [[ "$component" == server ]]; then
  checksum_files+=(
    PRODUCTION-CAPABILITY.json
    PRODUCTION-CAPABILITY.sig
    cluster-composition.evidence.json
    encryption-at-rest.evidence.json
  )
fi
(
  cd "$output"
  LC_ALL=C sha256sum "${checksum_files[@]}" >SHA256SUMS.next
  mv SHA256SUMS.next SHA256SUMS
  sha256sum --check --strict SHA256SUMS
)

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-sign.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-sign.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT
umask 077

private_key=$work/private-key.pem
public_key=$work/public-key.pem
if [[ -n ${TERRABASE_RELEASE_SIGNING_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_RELEASE_SIGNING_KEY_FILE" "$private_key"
elif [[ -n ${TERRABASE_RELEASE_SIGNING_KEY_PEM-} ]]; then
  printf '%s\n' "$TERRABASE_RELEASE_SIGNING_KEY_PEM" >"$private_key"
else
  echo "protected release signing key is not configured" >&2
  exit 1
fi
if [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_RELEASE_VERIFY_KEY_FILE" "$public_key"
elif [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_PEM-} ]]; then
  printf '%s\n' "$TERRABASE_RELEASE_VERIFY_KEY_PEM" >"$public_key"
else
  echo "independent release verification key is not configured" >&2
  exit 1
fi

openssl pkey -in "$private_key" -text_pub -noout | grep -Fq 'ASN1 OID: prime256v1' || {
  echo "release signing key must be ECDSA P-256" >&2
  exit 1
}
openssl pkey -pubin -in "$public_key" -text_pub -noout | grep -Fq 'ASN1 OID: prime256v1' || {
  echo "release verification key must be ECDSA P-256" >&2
  exit 1
}

(
  cd "$output"
  openssl dgst -sha256 -sign "$private_key" -out SHA256SUMS.sig SHA256SUMS
  openssl dgst -sha256 -verify "$public_key" -signature SHA256SUMS.sig SHA256SUMS
)
chmod 0644 "$output/SHA256SUMS.sig"
"$script_dir/verify.sh" "$output"
echo "signed and verified $output/SHA256SUMS"
