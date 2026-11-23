#!/usr/bin/env bash
set -euo pipefail
image=${1:?destination image digest}
shift
[[ $image =~ @sha256:[0-9a-f]{64}$ ]]
(( $# > 0 ))
transport=()
if [[ $image == http://* ]]; then
  image=${image#http://}
  transport=(--plain-http)
fi
repository=${image%@*}
digest=${image##*@}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
type=application/vnd.dev.sigstore.bundle.v0.3+json
for bundle in "$@"; do
  jq -e --arg type "$type" '.mediaType == $type and .dsseEnvelope.payloadType == "application/vnd.in-toto+json"' "$bundle" > /dev/null
  jq -r .dsseEnvelope.payload "$bundle" | base64 --decode |
    jq -e --arg digest "${digest#sha256:}" '.subject | any(.digest.sha256 == $digest)' > /dev/null
  cp "$bundle" "$work/bundle.json"
  (
    cd "$work"
    oras attach "${transport[@]}" --artifact-type "$type" --export-manifest manifest.json \
      "$repository@$digest" "bundle.json:$type"
  )
  artifact=sha256:$(sha256sum "$work/manifest.json" | cut -d ' ' -f1)
  oras discover "${transport[@]}" --artifact-type "$type" --format json --depth 1 "$repository@$digest" |
    jq -e --arg artifact "$artifact" '.referrers | any(.digest == $artifact)' > /dev/null
  oras manifest fetch "${transport[@]}" "$repository@$artifact" > "$work/fetched.json"
  jq -e --arg digest "$digest" --arg type "$type" \
    '.subject.digest == $digest and .artifactType == $type and (.layers | length) == 1' "$work/fetched.json" > /dev/null
  layer=$(jq -er '.layers[0].digest' "$work/fetched.json")
  oras blob fetch "${transport[@]}" "$repository@$layer" --output "$work/fetched-bundle.json"
  cmp "$bundle" "$work/fetched-bundle.json"
done
echo "ATTESTED $repository@$digest: $# Sigstore referrers verified"
