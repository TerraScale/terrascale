#!/usr/bin/env bash
set -euo pipefail
operation=${1:?operation} image=${2:?image}
shift 2
package=${image##*/}
package=${package%@*}
[[ $package == ts-auth || $package == terrascale-dashboard ]]
[[ $operation == publish || $operation == attest ]]
repositories=$(bash "$(dirname "$0")/ocir-repositories.sh" "$package")
pids=()
if [[ $operation == publish ]]; then
  bash "$(dirname "$0")/prove-image-publication.sh" "$image" "$@" &
  pids+=("$!")
fi
while IFS= read -r repository; do
  if [[ $operation == publish ]]; then
    bash "$(dirname "$0")/publish-image.sh" "$image" "$repository:${REVISION:?},main" "$@" &
  else
    bash "$(dirname "$0")/attach-image-bundles.sh" "$repository@${image##*@}" "$@" &
  fi
  pids+=("$!")
done <<< "$repositories"
result=0
for pid in "${pids[@]}"; do
  wait "$pid" || result=1
done
exit "$result"
