#!/usr/bin/env bash
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
export GOFLAGS="${GOFLAGS:--mod=vendor}"

exec npm run e2e
