#!/usr/bin/env bash
set -euo pipefail
source=${1:?source image digest}
provenance=${2:?provenance bundle}
sbom=${3:?SBOM bundle}
package=${source##*/}
package=${package%@*}
for destination in gru/terrascale fra/terrascale sjc/terrascale; do
  bash "$(dirname "$0")/publish-image.sh" "$source" \
    "http://localhost:5000/$destination/$package:proof" "$provenance" "$sbom"
done
