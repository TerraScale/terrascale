#!/usr/bin/env bash
# E-13 sync family: served TerraBase reactive view attach and delivery.
#
# The Rust driver is compiled as a standalone program so this family does not
# edit bench/src/main.rs or the shared history/results registry.  It requires
# a real served member and (for the CDN lane) a real CDN/cache endpoint.  A
# missing service is reported as `unavailable`; a measured threshold breach is
# reported as `fail`.  There is no synthetic or dry-run success.
#
# Required environment:
#   TERRABASE_SYNC_DATABASE   database hosted by the served member
#   TERRABASE_SYNC_TOKEN      Bearer API key
#   TERRABASE_SYNC_LOCAL_ENDPOINT (or TERRABASE_SYNC_ENDPOINT)
#   TERRABASE_SYNC_CDN_ENDPOINT
#   TERRABASE_SYNC_RUNTIME_DIGEST and TERRABASE_SYNC_COMPARATOR_DIGEST
#   TERRABASE_SYNC_RESOURCE_EVIDENCE (served member cgroup receipt)
# Optional cross-region probes:
#   TERRABASE_SYNC_REGIONS=GRU=http://...,SFO=http://...,FRA=http://...
#   TERRABASE_SYNC_REPLICATION=single|rf2
#   TERRABASE_SYNC_MEMBER_PID=<served member pid> (makes the RSS bound target
#       the member; without it, the driver RSS is reported explicitly)
#
# Examples:
#   bench/scripts/sync.sh --samples 25
#   TERRABASE_SYNC_REPLICATION=rf2 bench/scripts/sync.sh --no-writes
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BUILD_DIR="$(mktemp -d "${TMPDIR:-/tmp}/terrabase-sync.XXXXXXXX")"
SYNC_BIN="$BUILD_DIR/terrabase-sync"
cleanup() { rm -rf "$BUILD_DIR"; }
trap cleanup EXIT
trap 'exit 143' INT TERM

for arg in "$@"; do
  case "$arg" in
    --help|-h)
      sed -n '2,28p' "$0"
      exit 0
      ;;
  esac
done

echo "sync: compiling standalone driver with Rust priority nice -n 19" >&2
nice -n 19 rustc --edition=2024 -O "$REPO_ROOT/bench/src/sync.rs" -o "$SYNC_BIN"

# A sync result is only useful when the launch-member envelope was actually
# available. The driver is the instrument, but placing it in the same cgroup
# also makes the recorded resource evidence independently auditable.
# shellcheck disable=SC1091
source "$SCRIPT_DIR/resource-envelope.sh"
envelope_require
if [[ -z "${TERRABASE_SYNC_RESOURCE_EVIDENCE:-}" || ! -s "$TERRABASE_SYNC_RESOURCE_EVIDENCE" ]]; then
  echo "sync: TERRABASE_SYNC_RESOURCE_EVIDENCE must name the served member's cgroup evidence" >&2
  exit 2
fi
grep -Eiq '2ocpu/12gib|2[[:space:]]*ocpu.*12[[:space:]]*gib' "$TERRABASE_SYNC_RESOURCE_EVIDENCE" || {
  echo "sync: resource evidence does not prove the 2 OCPU / 12 GiB launch-member envelope" >&2
  exit 2
}
if [[ -z "${TERRABASE_SYNC_OUTPUT:-}" ]]; then
  TERRABASE_SYNC_OUTPUT="$REPO_ROOT/bench/results/sync-$(date +%s)-$$.json"
  export TERRABASE_SYNC_OUTPUT
fi

echo "sync: resource evidence 2 OCPU / 12 GiB; member connections bounded at 4; cadence ${TERRABASE_SYNC_CADENCE_MS:-100}ms" >&2
set +e
envelope_wrap nice -n 19 "$SYNC_BIN" "$@"
status=$?
set -e
case "$status" in
  0) echo "sync: verdict pass" >&2 ;;
  3) echo "sync: verdict unavailable (live service or required lane is absent)" >&2 ;;
  4) echo "sync: verdict fail (measured contract regression)" >&2 ;;
  *) echo "sync: driver failed before a verdict (exit $status)" >&2 ;;
esac
exit "$status"
