#!/usr/bin/env bash
set -euo pipefail
source=ghcr.io/oras-project/oras@sha256:68357796f10b82eeb4b62538fb0e07ac1fec6ca8575f2a9108d53ab0bbc74e75
digest=${source##*@}
repository=localhost:5000/transport/terrascale/oras
type=application/vnd.dev.sigstore.bundle.v0.3+json
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
payload=$(jq -cn --arg digest "${digest#sha256:}" \
  '{subject:[{name:"transport-proof",digest:{sha256:$digest}}]}' | base64 -w0)
jq -cn --arg type "$type" --arg payload "$payload" \
  '{mediaType:$type,dsseEnvelope:{payloadType:"application/vnd.in-toto+json",payload:$payload,signatures:[]}}' > "$work/bundle.json"
jq '.mediaType = "invalid-transport-proof"' "$work/bundle.json" > "$work/invalid.json"
if bash "$(dirname "$0")/publish-image.sh" "$source" "http://$repository:proof,release" "$work/invalid.json" > "$work/refused.log" 2>&1; then
  echo 'Publication accepted an invalid bundle' >&2
  exit 1
fi
[[ $(curl -sS -o /dev/null -w '%{http_code}' "http://localhost:5000/v2/transport/terrascale/oras/manifests/release") == 404 ]]
bash "$(dirname "$0")/publish-image.sh" "$source" "http://$repository:proof,release" "$work/bundle.json"
[[ $(oras resolve --plain-http "$repository:release") == "$digest" ]]
bash "$(dirname "$0")/publish-image.sh" "$repository@$digest" "http://$repository:self-proof,self-release" "$work/bundle.json"
[[ $(oras resolve --plain-http "$repository:self-release") == "$digest" ]]
[[ $(curl -sS -o /dev/null -w '%{http_code}' "http://localhost:5000/v2/transport/terrascale/oras/referrers/$digest") == 404 ]]
oras manifest fetch --plain-http "$repository:sha256-${digest#sha256:}" |
  jq -e --arg type "$type" '.mediaType == "application/vnd.oci.image.index.v1+json" and (.manifests | any(.artifactType == $type))' > /dev/null
bash "$(dirname "$0")/publish-image.sh" "$source" "http://localhost:5000/mirror/terrascale/oras:mirror-proof,mirror-release"
[[ $(oras resolve --plain-http "localhost:5000/mirror/terrascale/oras:mirror-release") == "$digest" ]]
[[ $(curl -sS -o /dev/null -w '%{http_code}' "http://localhost:5000/v2/mirror/terrascale/oras/manifests/sha256-${digest#sha256:}") == 404 ]]
bash "$(dirname "$0")/attach-image-bundles.sh" "http://localhost:5000/mirror/terrascale/oras@$digest" "$work/bundle.json"
echo "TRANSPORT_PROOF $digest: identical image, bundle-free mirror, fallback index, bundle roundtrip and release ordering verified"
