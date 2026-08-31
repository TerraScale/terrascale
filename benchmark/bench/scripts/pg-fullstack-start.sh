#!/usr/bin/env bash
# TerraBase benchmark lane: the pinned PostgreSQL full-stack reference.
#
# Pinned identity (GOAL.md section 3, "PostgreSQL-with-JSONB" framing): the
# same SE-15 PostgreSQL 18 pin as bench/scripts/pg-start.sh (shipped
# defaults plus synchronous_commit=on, fsync=on, full_page_writes=on, fresh
# data directory per run, loopback only, trust auth for the bench role),
# served as documents-on-jsonb through pgbouncer (transaction pooling) and
# a minimal Go HTTP/JSON service (terrabase-go/bench/pgrest; its own nested
# Go module, because release contracts allow Go source only under
# terrabase-go). This is the like-for-like served stack the full-stack
# comparator lane drives: every request pays real JSON encoding and a
# pooled database round-trip.
#
# Endpoints after PG_FULLSTACK_READY:
#   pgrest     http://127.0.0.1:55433  (TERRABASE_PG_FULLSTACK_ENDPOINT)
#   pgbouncer  127.0.0.1:55434         (transaction pooling)
#   postgresql 127.0.0.1:55435         (direct, not used by the lane)
#
# Hold mode: TERRABASE_BENCH_HOLD=1 leaves the whole stack running after
# this script exits; the next start always discards the data directory
# first, so nothing leaks.
set -euo pipefail

# Everything below needs postgresql_18 and pgbouncer on PATH; re-exec this
# same script inside a nix-shell once, keeping cwd and environment.
if [[ "${TERRABASE_PG_FULLSTACK_NIXED:-}" != "1" ]]; then
  export TERRABASE_PG_FULLSTACK_NIXED=1
  exec nix-shell -p postgresql_18 -p pgbouncer --run "exec bash '$0'"
fi

ROOT=/tmp/terrabase-bench-pgfullstack-lane
PG_PORT=55435
PGBOUNCER_PORT=55434
PGREST_PORT=55433
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PGREST_BIN="$REPO_ROOT/terrabase-go/bench/pgrest/pgrest"

# The service binary is built from terrabase-go/bench/pgrest; building it
# here keeps a fresh checkout usable. Everything else in the stack is
# pinned from nix.
if [[ ! -x "$PGREST_BIN" ]]; then
  (cd "$REPO_ROOT/terrabase-go/bench/pgrest" && go build -o pgrest .)
fi

# Stop any stack a previous run left on this data directory before the
# directory is discarded; a live postmaster whose files are removed under
# it is the failure this prevents.
pg_ctl -D "$ROOT/data" stop -m immediate >/dev/null 2>&1 || true
if [[ -f "$ROOT/pgbouncer.pid" ]]; then
  kill "$(cat "$ROOT/pgbouncer.pid")" >/dev/null 2>&1 || true
fi
# A held run from before leaves its pgrest serving 127.0.0.1:55433; without
# this the new instance dies on "address already in use" and the old one,
# wired to a dead pooler, keeps answering.
pkill -f "$PGREST_BIN" >/dev/null 2>&1 || true
rm -rf "$ROOT"
mkdir -p "$ROOT"
export PGDATA="$ROOT/data"
initdb -D "$PGDATA" --no-locale -E UTF8 -U bench >/dev/null
pg_ctl -D "$PGDATA" -l "$ROOT/server.log" \
  -o "-p $PG_PORT -c listen_addresses=127.0.0.1 -c unix_socket_directories=$ROOT -c synchronous_commit=on -c fsync=on -c full_page_writes=on -c logging_collector=off" \
  -w start >/dev/null

cat > "$ROOT/pgbouncer.ini" <<EOF
[pgbouncer]
listen_addr = 127.0.0.1
listen_port = $PGBOUNCER_PORT
unix_socket_dir = $ROOT
pidfile = $ROOT/pgbouncer.pid
auth_type = trust
auth_file = $ROOT/userlist.txt
admin_users = bench
pool_mode = transaction
default_pool_size = 32
max_client_conn = 200
# Protocol-level prepared statements must survive transaction pooling's
# connection rerouting; without this a Bind can land on a server connection
# that never saw the Parse, which PostgreSQL answers with SQLSTATE 26000
# under concurrency. (pgrest additionally runs in simple-protocol mode.)
max_prepared_statements = 200

[databases]
postgres = host=127.0.0.1 port=$PG_PORT dbname=postgres
EOF
printf '"bench" ""\n' > "$ROOT/userlist.txt"
pgbouncer -d "$ROOT/pgbouncer.ini"

PGREST_LOG="$ROOT/pgrest.log"
# sslmode=disable: the pinned stack is loopback-only, so no side of this
# lane pays TLS (matching the engine-side lane's plain TCP boundary).
# default_query_exec_mode=simple_protocol: pgx's pgbouncer-compatible mode;
# the extended protocol's prepared statements do not survive transaction
# pooling's rerouting on PostgreSQL 18 (intermittent SQLSTATE 26000).
DATABASE_URL="postgres://bench@127.0.0.1:$PGBOUNCER_PORT/postgres?sslmode=disable&default_query_exec_mode=simple_protocol" \
PGREST_LISTEN="127.0.0.1:$PGREST_PORT" \
PGREST_MAX_CONNS=32 \
  "$PGREST_BIN" >"$PGREST_LOG" 2>&1 &
PGREST_PID=$!

cleanup() {
  kill "$PGREST_PID" >/dev/null 2>&1 || true
  if [[ -f "$ROOT/pgbouncer.pid" ]]; then
    kill "$(cat "$ROOT/pgbouncer.pid")" >/dev/null 2>&1 || true
  fi
  pg_ctl -D "$PGDATA" stop -m immediate >/dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 143' INT TERM

# Readiness is the service's own /healthz: it answers only after a SELECT
# succeeded through pgbouncer, so this loop gates the whole stack.
ready=false
for _ in $(seq 1 120); do
  if curl -sf --max-time 2 "http://127.0.0.1:$PGREST_PORT/healthz" >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  echo "the full-stack reference did not become ready on 127.0.0.1:$PGREST_PORT (pgrest log: $PGREST_LOG, server log: $ROOT/server.log)" >&2
  exit 1
fi

# Fresh schema per start; the reset endpoint is the single source of DDL.
curl -sf -X POST "http://127.0.0.1:$PGREST_PORT/admin/reset" >/dev/null

echo PG_FULLSTACK_IDENTITY:
psql -h 127.0.0.1 -p "$PGBOUNCER_PORT" -U bench -d postgres \
  -c 'SELECT version();' \
  -c 'SHOW synchronous_commit;' \
  -c 'SHOW fsync;' \
  -c 'SHOW full_page_writes;'
echo PG_FULLSTACK_POOL:
psql -h 127.0.0.1 -p "$PGBOUNCER_PORT" -U bench -d pgbouncer -c 'SHOW POOLS;'
echo "endpoints:"
echo "  pgrest     http://127.0.0.1:$PGREST_PORT (TERRABASE_PG_FULLSTACK_ENDPOINT)"
echo "  pgbouncer  127.0.0.1:$PGBOUNCER_PORT (pool_mode=transaction)"
echo "  postgresql 127.0.0.1:$PG_PORT (direct)"
# Hold mode: leave the pinned stack running for the lane runs that the
# documented flow starts AFTER this script exits. The next start always
# discards the data directory first, so nothing leaks.
[[ "${TERRABASE_BENCH_HOLD:-}" != 1 ]] || trap - EXIT
echo PG_FULLSTACK_READY
