#!/usr/bin/env bash
# TerraBase SE-15 benchmark lane: pinned PostgreSQL reference server.
#
# Pinned identity (docs/benchmarks/se15-postgresql-reference-pin.md):
# PostgreSQL 18, the newest fully-supported stable major series, at the
# artifact's shipped defaults except the documented durability settings.
# Fresh empty data directory per run on the same storage class as the
# TerraBase engine location (/tmp), loopback only, trust authentication
# for the benchmark role only, discarded afterwards.
set -euo pipefail

ROOT=/tmp/terrabase-bench-pg-lane
PORT=55432
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# PostgreSQL is a measured subject. Keep the entire nix-shell/postmaster
# process tree in the same fail-closed launch-member cgroup.
# shellcheck disable=SC1091
source "$SCRIPT_DIR/resource-envelope.sh"
envelope_require
COMPARATOR_DIGEST="${TERRABASE_BENCH_COMPARATOR_DIGEST:-}"
if [[ ! "$COMPARATOR_DIGEST" =~ ^[0-9a-fA-F]{64}$ ]]; then
  echo "pg-start: TERRABASE_BENCH_COMPARATOR_DIGEST must be an immutable 64-hex digest" >&2
  exit 2
fi

envelope_wrap nix-shell -p postgresql_18 --run "
set -euo pipefail
source $SCRIPT_DIR/resource-envelope.sh
# Stop any server a previous run left on this data directory before the
# directory is discarded; a live postmaster whose files are removed under
# it is the failure this prevents.
pg_ctl -D $ROOT/data stop -m immediate >/dev/null 2>&1 || true
rm -rf $ROOT
mkdir -p $ROOT
export PGDATA=$ROOT/data
initdb -D \$PGDATA --no-locale -E UTF8 -U bench >/dev/null
pg_ctl -D \$PGDATA -l $ROOT/server.log \
  -o '-p $PORT -c listen_addresses=127.0.0.1 -c unix_socket_directories=$ROOT -c synchronous_commit=on -c fsync=on -c full_page_writes=on -c logging_collector=off' \
  -w start >/dev/null
cleanup() {
  local pid
  pid=\$(head -n 1 "\$PGDATA/postmaster.pid" 2>/dev/null || true)
  if [[ "\$pid" =~ ^[0-9]+\$ && "\$pid" != 0 ]]; then
    envelope_subject_evidence postgresql "\$pid" "$ROOT/resource-evidence.txt"
    echo "PG_RESOURCE_EVIDENCE: $ROOT/resource-evidence.txt"
  fi
  pg_ctl -D \$PGDATA stop -m immediate >/dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 143' INT TERM
# Hold mode: leave the pinned reference running for the lane runs that the
# documented se15 flow starts AFTER this script exits. The next start always
# discards the data directory first, so nothing leaks.
[[ "\${TERRABASE_BENCH_HOLD:-}" != 1 ]] || trap - EXIT
psql -h $ROOT -p $PORT -U bench -d postgres \
  -c 'SELECT version();' \
  -c 'SHOW synchronous_commit;' \
  -c 'SHOW fsync;' \
  -c 'SHOW full_page_writes;' \
  -c 'SHOW shared_buffers;' \
  -c 'SHOW work_mem;'
echo "PG_TOPOLOGY: single-node"
echo "PG_ENVELOPE: $TERRABASE_BENCH_ENVELOPE_SPEC"
echo "PG_COMPARATOR_DIGEST: sha256:$COMPARATOR_DIGEST"
echo PG_RUNTIME_DIGEST: sha256:\$(sha256sum \$(command -v postgres) | cut -d ' ' -f1)
pid=\$(head -n 1 "\$PGDATA/postmaster.pid" 2>/dev/null || true)
if [[ "\$pid" =~ ^[0-9]+\$ && "\$pid" != 0 ]]; then
  envelope_subject_evidence postgresql "\$pid" "$ROOT/resource-evidence.txt"
  echo "PG_RESOURCE_EVIDENCE: $ROOT/resource-evidence.txt"
fi
"
echo PG_READY
