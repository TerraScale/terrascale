#!/usr/bin/env bash
set -euo pipefail

while IFS= read -r -d '' path; do
  jq --exit-status . "$path" >/dev/null || {
    echo "invalid JSON asset: $path" >&2
    exit 1
  }
done < <(git ls-files -z -- '*.json')

while IFS= read -r -d '' path; do
  yq eval '.' "$path" >/dev/null || {
    echo "invalid YAML asset: $path" >&2
    exit 1
  }
done < <(git ls-files -z -- '*.yml' '*.yaml')

echo "validated committed JSON and YAML assets"
