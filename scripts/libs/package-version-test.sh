#!/usr/bin/env bash
# Self-check for scripts/package-version.sh; CI runs it before packing.
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/package-version.sh"

utc() { date -u -d "$1" +%s; }

check() {
  local want=$1
  shift
  local got
  got=$(calver "$@")
  if [[ $got != "$want" ]]; then
    echo "calver $* printed '$got', expected '$want'" >&2
    exit 1
  fi
}

check 2026.1011.5 "$(utc '2026-10-11 00:05:00')"
check 2026.1011.2359 "$(utc '2026-10-11 23:59:00')"
check 2026.1231.1200 "$(utc '2026-12-31 12:00:00')"
check 2027.101.0 "$(utc '2027-01-01 00:00:00')"
check 2026.1011.254.42 "$(utc '2026-10-11 02:54:42')" "$(utc '2026-10-11 02:54:03')"
check 2026.1011.254 "$(utc '2026-10-11 02:54:42')" "$(utc '2026-10-11 02:53:59')"
check 2026.1011.254.1 "$(utc '2026-10-11 02:54:01')" "$(utc '2026-10-11 02:54:00')"
# An empty parent is the root-commit fallback of the CLI.
check 2026.1011.254 "$(utc '2026-10-11 02:54:42')" ""
if calver "$(utc '2026-10-11 02:54:00')" "$(utc '2026-10-11 02:54:00')" 2>/dev/null; then
  echo "calver accepted a commit sharing its parent's UTC second" >&2
  exit 1
fi
echo "package-version: all checks passed"
