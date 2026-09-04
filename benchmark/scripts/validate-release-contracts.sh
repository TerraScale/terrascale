#!/usr/bin/env bash
# Fail closed when the checked-in contract bundle drifts from its sources:
# regenerate into a scratch directory and diff. The Forgejo registry publish
# check joins this script with the full E-1 publish.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

test -d contracts || {
  echo "missing contracts/; run 'make contracts'" >&2
  exit 1
}

work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/terrabase-contracts.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT

cargo run --quiet --locked -p terrabase-server --bin terrabase-contracts -- \
  --repository-root "$repo_root" --output "$work/contracts" --version v1
diff -r contracts "$work/contracts"

echo "contract artifacts are up to date"
