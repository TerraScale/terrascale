#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 ]] || {
  echo "usage: deploy/release/require-production-capability.sh <server-release-directory>" >&2
  exit 2
}
output=$1
[[ -d "$output" && ! -L "$output" ]] || {
  echo "server release output is not a directory: $output" >&2
  exit 1
}

manifest=$(find "$output" -maxdepth 1 -type f -name '*.release.json' -print)
[[ $(printf '%s\n' "$manifest" | sed '/^$/d' | wc -l) -eq 1 ]] || {
  echo "server release output must contain exactly one release manifest" >&2
  exit 1
}
[[ $(jq -er '.component' "$manifest") == server ]] || {
  echo "production capability evidence applies only to the TerraBase server" >&2
  exit 1
}
[[ $(jq -er '.source.dirty' "$manifest") == false ]] || {
  echo "dirty source cannot receive a production capability admission" >&2
  exit 1
}

statement=${TERRABASE_PRODUCTION_CAPABILITY_STATEMENT:-$output/PRODUCTION-CAPABILITY.json}
signature=${TERRABASE_PRODUCTION_CAPABILITY_SIGNATURE:-$output/PRODUCTION-CAPABILITY.sig}
evidence_directory=${TERRABASE_PRODUCTION_CAPABILITY_EVIDENCE_DIRECTORY:-$(dirname -- "$statement")}
cluster_evidence=$evidence_directory/cluster-composition.evidence.json
encryption_evidence=$evidence_directory/encryption-at-rest.evidence.json

for evidence in "$statement" "$signature" "$cluster_evidence" "$encryption_evidence"; do
  [[ -f "$evidence" && ! -L "$evidence" ]] || {
    echo "production capability evidence is missing or unsafe" >&2
    exit 1
  }
done
[[ $(wc -c <"$statement") -le 65536 && $(wc -c <"$signature") -le 4096 ]] || {
  echo "production capability admission files exceed their bounds" >&2
  exit 1
}
[[ $(wc -c <"$cluster_evidence") -le 1048576 \
  && $(wc -c <"$encryption_evidence") -le 1048576 ]] || {
  echo "production capability evidence exceeds its bounds" >&2
  exit 1
}

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-capability.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-capability.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT
umask 077
public_key=$work/public-key.pem
if [[ -n ${TERRABASE_PRODUCTION_CAPABILITY_VERIFY_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_PRODUCTION_CAPABILITY_VERIFY_KEY_FILE" "$public_key"
elif [[ -n ${TERRABASE_PRODUCTION_CAPABILITY_VERIFY_KEY_PEM-} ]]; then
  printf '%s\n' "$TERRABASE_PRODUCTION_CAPABILITY_VERIFY_KEY_PEM" >"$public_key"
else
  echo "the independent production capability verification key is not configured" >&2
  exit 1
fi
openssl pkey -pubin -in "$public_key" -text_pub -noout | grep -Fq 'ASN1 OID: prime256v1' || {
  echo "the production capability verification key must be ECDSA P-256" >&2
  exit 1
}
openssl dgst -sha256 -verify "$public_key" -signature "$signature" "$statement" >/dev/null

revision=$(jq -er '.source.revision' "$manifest")
tree=$(jq -er '.source.tree' "$manifest")
repository='https://git.terrascale.tech/TerraScale/TerraBase'
cluster_name=cluster-composition.evidence.json
encryption_name=encryption-at-rest.evidence.json
jq -e \
  --arg repository "$repository" \
  --arg revision "$revision" \
  --arg tree "$tree" \
  --arg cluster "$cluster_name" \
  --arg encryption "$encryption_name" \
  '
    .schemaVersion == 1 and
    .subject.repository == $repository and
    .subject.revision == $revision and
    .subject.tree == $tree and
    .subject.component == "terrabase-server" and
    .capabilities.clusterComposition.status == "qualified" and
    .capabilities.clusterComposition.evidence.name == $cluster and
    (.capabilities.clusterComposition.evidence.sha256 | test("^[0-9a-f]{64}$")) and
    .capabilities.encryptionAtRest.status == "qualified" and
    .capabilities.encryptionAtRest.evidence.name == $encryption and
    (.capabilities.encryptionAtRest.evidence.sha256 | test("^[0-9a-f]{64}$"))
  ' "$statement" >/dev/null

cluster_digest=$(sha256sum "$cluster_evidence" | awk '{print $1}')
encryption_digest=$(sha256sum "$encryption_evidence" | awk '{print $1}')
[[ $(jq -er '.capabilities.clusterComposition.evidence.sha256' "$statement") == "$cluster_digest" ]]
[[ $(jq -er '.capabilities.encryptionAtRest.evidence.sha256' "$statement") == "$encryption_digest" ]]

jq -e --arg revision "$revision" --arg tree "$tree" '
  .schemaVersion == 1 and
  .capability == "real-cluster-composition" and
  .status == "passed" and
  .source.revision == $revision and
  .source.tree == $tree
' "$cluster_evidence" >/dev/null
jq -e --arg revision "$revision" --arg tree "$tree" '
  .schemaVersion == 1 and
  .capability == "encryption-at-rest" and
  .status == "passed" and
  .source.revision == $revision and
  .source.tree == $tree
' "$encryption_evidence" >/dev/null

echo "verified independent production capability admission for $revision"
