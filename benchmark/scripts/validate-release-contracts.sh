#!/usr/bin/env bash
# Fail closed when checked-in contract artifacts drift from their sources.
# E-1 stub: OpenAPI freshness (regenerate + diff), proto copies (diff), and
# manifest/version sanity. The Forgejo registry publish check joins this
# script with the full E-1 publish.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

test -f contracts/openapi.json || {
  echo "missing contracts/openapi.json; run 'make contracts'" >&2
  exit 1
}

work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/terrabase-contracts.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT

cargo run --quiet --locked -p terrabase-server --bin terrabase-openapi -- "$work/openapi.json"
diff -u contracts/openapi.json "$work/openapi.json"

for proto in terrabase/v1/engine.proto terrabase/v1/family.proto; do
  diff -u "crates/terrabase-tbep/proto/$proto" "contracts/protos/$proto"
done

jq --exit-status '.contract == "terrabase-reactive-manifest"' contracts/manifest.json >/dev/null
manifest_version="$(jq -r .contract_version contracts/manifest.json)"
file_version="$(tr -d '[:space:]' <contracts/CONTRACT_VERSION)"
test -n "$file_version"
test "$manifest_version" = "$file_version" || {
  echo "manifest contract_version ($manifest_version) != CONTRACT_VERSION ($file_version)" >&2
  exit 1
}

echo "contract artifacts are up to date (CONTRACT_VERSION $file_version)"
