#!/usr/bin/env bash
set -euo pipefail

bash scripts/install-govulncheck.sh
scanner="$HOME/.cache/terrascale/govulncheck-v1.9.0-go1.27.3/govulncheck"
reports=$(mktemp -d)
trap 'rm -rf "$reports"' EXIT
git ls-files -z -- go.mod '**/go.mod' > "$reports/manifests"
mapfile -d '' manifests < "$reports/manifests"
if (( ${#manifests[@]} == 0 )); then
  echo 'No committed Go modules found' >&2
  exit 1
fi
for manifest in "${manifests[@]}"; do
  directory=$(dirname "$manifest")
  echo "Audit: $manifest"
  # Explicit roots retain test-only modules even when the scanner merges a
  # package's production and internal-test variants under the same package path.
  go -C "$directory" list -deps -test -f '{{if and .Module (not .ForTest)}}{{.Module.Path}} {{.ImportPath}}{{end}}' ./... > "$reports/packages"
  awk 'NF && !seen[$1]++ {print $2}' "$reports/packages" > "$reports/roots"
  mapfile -t roots < "$reports/roots"
  if (( ${#roots[@]} == 0 )); then
    echo "No source packages found in $manifest" >&2
    exit 1
  fi
  # SARIF retains affected module/package findings even without reachable symbols.
  # Only parse after a successful scan; govulncheck emits SARIF on completion.
  "$scanner" -C "$directory" -test -format=sarif ./... "${roots[@]}" > "$reports/source.sarif"
  python3 scripts/check-advisory-report.py "$reports/source.sarif"
done
