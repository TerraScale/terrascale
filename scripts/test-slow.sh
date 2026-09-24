#!/usr/bin/env bash
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
export GOFLAGS="${GOFLAGS:--mod=vendor}"

if [[ ! -f "$root/tests/integration/sdk/package.json" ]]; then
  echo "missing pinned test-only TypeScript SDK snapshot at tests/integration/sdk" >&2
  exit 1
fi

exec npm run e2e
