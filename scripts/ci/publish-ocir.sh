#!/usr/bin/env bash
set -euo pipefail
source=${1:?source image digest}
tags=${2:?revision tags}
shift 2
package=${source##*/}
package=${package%@*}
repositories=$(bash "$(dirname "$0")/ocir-repositories.sh" "$package")
while IFS= read -r repository; do
  bash "$(dirname "$0")/publish-image.sh" "$source" "$repository:$tags" "$@"
done <<< "$repositories"
