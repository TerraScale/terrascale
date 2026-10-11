#!/usr/bin/env bash
# Only TerraScale.Logging at the calendar version PACKAGE_VERSION may be packed into
# artifacts/packages, so nothing else or any other version can reach the registry.
set -euo pipefail

: "${PACKAGE_VERSION:?PACKAGE_VERSION is required}"
shopt -s nullglob
packages=(artifacts/packages/*.nupkg)
if [[ ${#packages[@]} -ne 1 || ${packages[0]} != "artifacts/packages/TerraScale.Logging.$PACKAGE_VERSION.nupkg" ]]; then
  echo "expected only TerraScale.Logging.$PACKAGE_VERSION.nupkg in artifacts/packages, found: ${packages[*]:-none}" >&2
  exit 1
fi
echo "packed ${packages[0]}"
