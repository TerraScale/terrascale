#!/usr/bin/env bash
set -euo pipefail
source=${1:?source image digest}
destination=${2:?destination repository and tags}
shift 2
[[ $source =~ @sha256:[0-9a-f]{64}$ ]]
(( $# > 0 ))
digest=${source##*@}
transport=()
copy_transport=()
if [[ $destination == http://* ]]; then
  destination=${destination#http://}
  transport=(--plain-http)
  copy_transport=(--to-plain-http)
fi
repository=${destination%:*}
IFS=, read -ra tags <<< "${destination##*:}"
oras copy "${copy_transport[@]}" "$source" "$repository:${tags[0]}"
actual=$(oras manifest fetch "${transport[@]}" --descriptor "$repository:${tags[0]}" | jq -er .digest)
[[ $actual == "$digest" ]]
target=$repository
if (( ${#transport[@]} )); then target=http://$target; fi
bash "$(dirname "$0")/attach-image-bundles.sh" "$target@$digest" "$@"
for tag in "${tags[@]:1}"; do
  oras tag "${transport[@]}" "$repository@$digest" "$tag"
  [[ $(oras resolve "${transport[@]}" "$repository:$tag") == "$digest" ]]
done
echo "PUBLISHED $repository@$digest: image and Sigstore bundles verified"
