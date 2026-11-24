#!/usr/bin/env bash
set -euo pipefail
source=${1:?source image digest}
tags=${2:?revision tags}
shift 2
package=${source##*/}
package=${package%@*}
repositories=$(bash "$(dirname "$0")/ocir-repositories.sh" "$package")
pids=()
while IFS= read -r repository; do
  bash "$(dirname "$0")/publish-image.sh" "$source" "$repository:$tags" "$@" &
  pids+=("$!")
done <<< "$repositories"
failed=0
for pid in "${pids[@]}"; do
  wait "$pid" || failed=1
done
exit "$failed"
