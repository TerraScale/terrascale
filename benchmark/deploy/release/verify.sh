#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: deploy/release/verify.sh [--unsigned] <release-output-directory>" >&2
  exit 2
}

mode=signed
if [[ ${1-} == --unsigned ]]; then
  mode=unsigned
  shift
fi
[[ $# -eq 1 ]] || usage
output=$1
[[ -d "$output" && ! -L "$output" ]] || {
  echo "release output is not a directory: $output" >&2
  exit 1
}

for tool in jq openssl sha256sum skopeo stat tar; do
  command -v "$tool" >/dev/null || {
    echo "required verification tool is missing: $tool" >&2
    exit 1
  }
done
[[ -z "$(find "$output" -type l -print -quit)" ]] || {
  echo "release output contains a symbolic link" >&2
  exit 1
}
manifest=$(find "$output" -maxdepth 1 -type f -name '*.release.json' -print)
[[ $(printf '%s\n' "$manifest" | sed '/^$/d' | wc -l) -eq 1 ]] || {
  echo "release output must contain exactly one release manifest" >&2
  exit 1
}

component=$(jq -er '.component' "$manifest")
package=$(jq -er '.package' "$manifest")
version=$(jq -er '.version' "$manifest")
target=$(jq -er '.target' "$manifest")
binary=$(jq -er '.binary.name' "$manifest")
binary_digest=$(jq -er '.binary.sha256' "$manifest")
case "$component" in
  server)
    expected_package=terrabase-server
    expected_title='TerraBase Server'
    ;;
  studio)
    expected_package=terrabase-studio
    expected_title='TerraBase Studio'
    ;;
  *)
    echo "unsupported release component: $component" >&2
    exit 1
    ;;
esac
[[ "$package" == "$expected_package" && "$binary" == "$expected_package" ]] || {
  echo "release component, package, and binary identities disagree" >&2
  exit 1
}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]] || {
  echo "release manifest contains an invalid package version" >&2
  exit 1
}
case "$target" in
  x86_64-unknown-linux-gnu) expected_arch=amd64 ;;
  aarch64-unknown-linux-gnu) expected_arch=arm64 ;;
  *)
    echo "unsupported target in release manifest: $target" >&2
    exit 1
    ;;
esac

artifact_stem=$package-$version-$target
archive_name=$artifact_stem.tar.gz
oci_name=$artifact_stem.oci.tar
sbom_name=$artifact_stem.cdx.json
provenance_name=$artifact_stem.intoto.json
manifest_name=$artifact_stem.release.json
oci_tag=$package-$version-$expected_arch

jq -e \
  --arg archive "$archive_name" \
  --arg oci "$oci_name" \
  --arg sbom "$sbom_name" \
  --arg provenance "$provenance_name" \
  '
    .schemaVersion == 1 and
    .artifacts.archive.name == $archive and
    (.artifacts.archive.sha256 | test("^[0-9a-f]{64}$")) and
    .artifacts.ociArchive.name == $oci and
    (.artifacts.ociArchive.sha256 | test("^[0-9a-f]{64}$")) and
    .evidence.sbom == $sbom and
    .evidence.provenance == $provenance and
    .evidence.checksums == "SHA256SUMS" and
    .signature.file == "SHA256SUMS.sig" and
    .signature.algorithm == "ecdsa-p256-sha256" and
    (.binary.sha256 | test("^[0-9a-f]{64}$")) and
    (.source.revision | test("^[0-9a-f]{40,64}$")) and
    (.source.tree | test("^[0-9a-f]{40,64}$")) and
    (.source.dirty | type == "boolean")
  ' "$manifest" >/dev/null
[[ $(basename -- "$manifest") == "$manifest_name" ]] || {
  echo "release manifest name does not match its package identity" >&2
  exit 1
}
if [[ "$component" == server ]]; then
  jq -e '
    .evidence.productionCapability.statement == "PRODUCTION-CAPABILITY.json" and
    .evidence.productionCapability.signature == "PRODUCTION-CAPABILITY.sig" and
    .evidence.productionCapability.clusterComposition == "cluster-composition.evidence.json" and
    .evidence.productionCapability.encryptionAtRest == "encryption-at-rest.evidence.json"
  ' "$manifest" >/dev/null
else
  jq -e '.evidence | has("productionCapability") | not' "$manifest" >/dev/null
fi

archive_digest=$(jq -er '.artifacts.archive.sha256' "$manifest")
oci_digest=$(jq -er '.artifacts.ociArchive.sha256' "$manifest")
[[ $(sha256sum "$output/$archive_name" | awk '{print $1}') == "$archive_digest" ]]
[[ $(sha256sum "$output/$oci_name" | awk '{print $1}') == "$oci_digest" ]]
[[ $(jq -er '.metadata.component.hashes[] | select(.alg == "SHA-256") | .content' "$output/$sbom_name") == "$oci_digest" ]]
jq -e \
  --arg package "$package" \
  --arg version "$version" \
  --arg target "$target" \
  --arg oci "$oci_name" \
  '
    .bomFormat == "CycloneDX" and
    .specVersion == "1.6" and
    .version == 1 and
    .metadata.component.name == $package and
    .metadata.component.version == $version and
    .metadata.component.type == "application" and
    ([.metadata.component.properties[] | select(.name == "terrabase:rust-target" and .value == $target)] | length) == 1 and
    ([.metadata.component.properties[] | select(.name == "terrabase:artifact" and .value == $oci)] | length) == 1
  ' "$output/$sbom_name" >/dev/null
jq -e \
  --arg archive "$archive_name" \
  --arg archive_digest "$archive_digest" \
  --arg oci "$oci_name" \
  --arg oci_digest "$oci_digest" \
  --arg target "$target" \
  --arg revision "$(jq -er '.source.revision' "$manifest")" \
  --arg tree "$(jq -er '.source.tree' "$manifest")" \
  --argjson dirty "$(jq -er '.source.dirty' "$manifest")" \
  '
    ._type == "https://in-toto.io/Statement/v1" and
    .predicateType == "https://slsa.dev/provenance/v1" and
    ([.subject[] | select(.name == $archive and .digest.sha256 == $archive_digest)] | length) == 1 and
    ([.subject[] | select(.name == $oci and .digest.sha256 == $oci_digest)] | length) == 1 and
    (.subject | length) == 2 and
    .predicate.buildDefinition.externalParameters.target == $target and
    .predicate.buildDefinition.externalParameters.sourceDirty == $dirty and
    ([.predicate.buildDefinition.resolvedDependencies[] |
      select(.digest.gitCommit == $revision and .digest.gitTree == $tree)] | length) == 1
  ' "$output/$provenance_name" >/dev/null

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-verify.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-verify.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

expected_archive_entries=$(printf '%s\n' \
  "$artifact_stem/" \
  "$artifact_stem/LICENSE.md" \
  "$artifact_stem/RELEASE.md" \
  "$artifact_stem/bin/" \
  "$artifact_stem/bin/$binary" | LC_ALL=C sort)
observed_archive_entries=$(tar -tzf "$output/$archive_name" | LC_ALL=C sort)
[[ "$observed_archive_entries" == "$expected_archive_entries" ]] || {
  echo "native archive contains missing or unexpected paths" >&2
  diff -u <(printf '%s\n' "$expected_archive_entries") <(printf '%s\n' "$observed_archive_entries") >&2 || true
  exit 1
}
mkdir "$work/archive"
tar -xzf "$output/$archive_name" -C "$work/archive" --no-same-owner --no-same-permissions
archive_binary=$work/archive/$artifact_stem/bin/$binary
[[ -f "$archive_binary" && ! -L "$archive_binary" ]]
[[ $(stat -c '%a' "$archive_binary") == 755 ]]
[[ $(sha256sum "$archive_binary" | awk '{print $1}') == "$binary_digest" ]]

image=$(skopeo inspect "oci-archive:$output/$oci_name:$oci_tag")
[[ $(jq -er '.Architecture' <<<"$image") == "$expected_arch" ]]
[[ $(jq -er '.Os' <<<"$image") == linux ]]
[[ $(jq -er '.Config.Entrypoint | if length == 1 then .[0] else error("entrypoint must contain one command") end' <<<"$image") == "/usr/local/bin/$binary" ]]
[[ $(jq -er '.Labels["tech.terrascale.terrabase.binary.sha256"]' <<<"$image") == "$binary_digest" ]]
[[ $(jq -er '.Labels["org.opencontainers.image.title"]' <<<"$image") == "$expected_title" ]]
[[ $(jq -er '.Labels["org.opencontainers.image.version"]' <<<"$image") == "$version" ]]
[[ $(jq -er '.Labels["org.opencontainers.image.revision"]' <<<"$image") == "$(jq -er '.source.revision' "$manifest")" ]]

expected_files=$(jq -rn \
  --arg archive "$archive_name" \
  --arg manifest "$manifest_name" \
  --arg oci "$oci_name" \
  --arg provenance "$provenance_name" \
  --arg sbom "$sbom_name" \
  --arg mode "$mode" \
  --arg component "$component" \
  '
    [$archive, $manifest, $oci, $provenance, $sbom, "SHA256SUMS"] +
    (if $mode == "signed" then ["SHA256SUMS.sig"] else [] end) +
    (if $mode == "signed" and $component == "server" then [
      "PRODUCTION-CAPABILITY.json",
      "PRODUCTION-CAPABILITY.sig",
      "cluster-composition.evidence.json",
      "encryption-at-rest.evidence.json"
    ] else [] end) |
    sort | .[]
  ')
observed_files=$(find "$output" -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)
[[ "$observed_files" == "$expected_files" ]] || {
  echo "release output contains missing or unexpected files" >&2
  diff -u <(printf '%s\n' "$expected_files") <(printf '%s\n' "$observed_files") >&2 || true
  exit 1
}

(
  cd "$output"
  sha256sum --check --strict SHA256SUMS
)

if [[ "$mode" == signed ]]; then
  [[ $(jq -er '.source.dirty' "$manifest") == false ]] || {
    echo "a signed release cannot claim a dirty source tree" >&2
    exit 1
  }
  umask 077
  public_key=$work/release-public-key.pem
  if [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_FILE-} ]]; then
    install -m 0600 "$TERRABASE_RELEASE_VERIFY_KEY_FILE" "$public_key"
  elif [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_PEM-} ]]; then
    printf '%s\n' "$TERRABASE_RELEASE_VERIFY_KEY_PEM" >"$public_key"
  else
    echo "independent release verification key is not configured" >&2
    exit 1
  fi
  openssl pkey -pubin -in "$public_key" -text_pub -noout | grep -Fq 'ASN1 OID: prime256v1' || {
    echo "release verification key must be ECDSA P-256" >&2
    exit 1
  }
  openssl dgst -sha256 -verify "$public_key" -signature "$output/SHA256SUMS.sig" "$output/SHA256SUMS" >/dev/null
  if [[ "$component" == server ]]; then
    TERRABASE_PRODUCTION_CAPABILITY_STATEMENT=$output/PRODUCTION-CAPABILITY.json \
    TERRABASE_PRODUCTION_CAPABILITY_SIGNATURE=$output/PRODUCTION-CAPABILITY.sig \
    TERRABASE_PRODUCTION_CAPABILITY_EVIDENCE_DIRECTORY=$output \
      "$(dirname -- "${BASH_SOURCE[0]}")/require-production-capability.sh" "$output"
  fi
fi

echo "verified $mode release output $output"
