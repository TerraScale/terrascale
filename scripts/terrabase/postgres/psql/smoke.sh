#!/usr/bin/env bash
# Smoke test: psql against TerraBase over the Postgres wire. See ../README.md.
# Splits TERRABASE_PG_URL into libpq PG* variables so the key never appears on a command line.
set -uo pipefail
cd "$(dirname "$0")"

url=${TERRABASE_PG_URL:-}
[[ -n $url ]] || { echo "set TERRABASE_PG_URL"; exit 2; }
major=$(psql --version | sed -E 's/[^0-9]*([0-9]+).*/\1/')
(( major >= 18 )) || { echo "FAIL psql $major found; \\parse and \\bind_named need psql 18+"; exit 2; }

decode() { local s=${1//+/ }; printf '%b' "${s//%/\\x}"; }
rest=${url#*://}
userinfo=${rest%@*}
hostpart=${rest##*@}
hostport=${hostpart%%/*}
dbq=${hostpart#*/}
query=; [[ $dbq == *\?* ]] && query=${dbq#*\?}

export PGUSER=$(decode "${userinfo%%:*}")
export PGPASSWORD=$(decode "${userinfo#*:}")
export PGHOST=${hostport%:*}
export PGPORT=5432; [[ $hostport == *:* ]] && PGPORT=${hostport##*:}
export PGDATABASE=$(decode "${dbq%%\?*}")
export PGSSLMODE=require; [[ $query =~ (^|&)sslmode=([^&]*) ]] && PGSSLMODE=${BASH_REMATCH[2]}
export PGCONNECT_TIMEOUT=10
echo "target $PGHOST:$PGPORT db=$PGDATABASE"

t=smoke_psql_$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')
out=$(psql -X -v t="$t" -f smoke.sql 2>&1); rc=$?
echo "$out"
failed=0
if (( rc != 0 )); then
  echo "FAIL psql exited $rc on the step after the last PASS line"
  failed=1
elif grep -q '^FAIL' <<<"$out"; then
  failed=1
fi
if psql -X -q -v ON_ERROR_STOP=1 -c "DROP TABLE IF EXISTS ${t}_items" -c "DROP TABLE IF EXISTS ${t}_owners" >/dev/null; then
  echo "PASS drop tables"
else
  echo "FAIL drop tables"
  failed=1
fi
(( failed )) && { echo FAIL; exit 1; }
echo PASS
