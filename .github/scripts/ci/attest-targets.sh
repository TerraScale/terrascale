#!/usr/bin/env bash
set -euo pipefail
repository=${GITHUB_REPOSITORY:?}
run=${RUN_ID:?}
revision=${REVISION:?}
workflow=${WORKFLOW:?}
namespace=${OCIR_NAMESPACE:?}
[[ $run =~ ^[0-9]+$ ]]
[[ $revision =~ ^[0-9a-f]{40}$ ]]
case $workflow in
  .github/workflows/ci.yml) services='["terrabase", "ts-platform", "ts-auth"]' ;;
  .github/workflows/images-ghcr.yml) services='["ts-dashboard"]' ;;
  *) echo "::error::Unexpected triggering workflow $workflow" >&2; exit 1 ;;
esac
listing=$(gh api --paginate "repos/$repository/actions/runs/$run/artifacts?per_page=100")
artifacts=$(jq -cs '[.[].artifacts[] | select((.name | startswith("attest-targets-")) and (.expired | not))]
  | group_by(.name) | map(max_by(.id) | {id, name})' <<< "$listing")
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
targets='[]'
count=$(jq length <<< "$artifacts")
for ((index = 0; index < count; index++)); do
  id=$(jq -r ".[$index].id" <<< "$artifacts")
  name=$(jq -r ".[$index].name" <<< "$artifacts")
  [[ $id =~ ^[0-9]+$ ]]
  gh api "repos/$repository/actions/artifacts/$id/zip" > "$work/$id.zip"
  target=$(unzip -p "$work/$id.zip" attest-target.json)
  unzip -p "$work/$id.zip" provenance.json | jq -e '.mediaType == "application/vnd.dev.sigstore.bundle.v0.3+json"' > /dev/null ||
    { echo "::error::Artifact $name ($id) lacks a Sigstore build provenance bundle" >&2; exit 1; }
  jq -e --arg name "$name" --arg namespace "$namespace" --arg revision "$revision" --argjson services "$services" '
    {"terrabase": "terrabase", "ts-platform": "ts-platform", "ts-auth": "ts-auth", "ts-dashboard": "terrascale-dashboard"} as $packages
    | type == "object"
    and (.service | type == "string")
    and (.service | IN($services[]))
    and $name == "attest-targets-" + .service
    and .image == "sa-saopaulo-1.ocir.io/" + $namespace + "/terrascale/" + $packages[.service]
    and (.arm64 | type == "string" and test("^sha256:[0-9a-f]{64}$"))
    and .revision == $revision
    and (.tags | type == "array" and length > 0 and .[0] == $revision and all(.[]; IN($revision, "main")))
  ' <<< "$target" > /dev/null || { echo "::error::Artifact $name ($id) holds an invalid attestation target" >&2; exit 1; }
  targets=$(jq -c --argjson target "$target" --argjson artifact "$id" '. + [$target + {artifact: $artifact}]' <<< "$targets")
done
echo "targets=$targets"
