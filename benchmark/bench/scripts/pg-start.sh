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

ROOT=/tmp/terrabase-bench/pg
PORT=55432

nix-shell -p postgresql_18 --run "
set -euo pipefail
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
psql -h $ROOT -p $PORT -U bench -d postgres \
  -c 'SELECT version();' \
  -c 'SHOW synchronous_commit;' \
  -c 'SHOW fsync;' \
  -c 'SHOW full_page_writes;' \
  -c 'SHOW shared_buffers;' \
  -c 'SHOW work_mem;'
"
echo PG_READY
