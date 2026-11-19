#!/usr/bin/env bash
set -euo pipefail
package=${1:?package}
namespace=${OCIR_NAMESPACE:?}
printf '%s\n' \
  "us-sanjose-1.ocir.io/$namespace/terrascale/$package" \
  "eu-frankfurt-1.ocir.io/$namespace/terrascale/$package" \
  "sa-saopaulo-1.ocir.io/$namespace/terrascale/$package" \
  "us-sanjose-1.ocir.io/$namespace/terrascale-stg/$package"
