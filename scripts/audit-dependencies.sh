#!/usr/bin/env bash
set -euo pipefail

bash scripts/install-govulncheck.sh
scanner="$HOME/.cache/terrascale/govulncheck-v1.9.0-go1.27.3/govulncheck"
mapfile -d '' manifests < <(git ls-files -z -- go.mod '**/go.mod')
if (( ${#manifests[@]} == 0 )); then
  echo 'No committed Go modules found' >&2
  exit 1
fi
for manifest in "${manifests[@]}"; do
  directory=$(dirname "$manifest")
  echo "Audit: $manifest"
  "$scanner" -C "$directory" -scan=module -show=verbose
  "$scanner" -C "$directory" -test -show=verbose ./...
done
