#!/usr/bin/env bash
# Proves a TerraBase node's S3-compatible store (Tigris today, Cloudflare R2 next) with configuration alone: one local
# Production node ships its snapshots, log segments, data keys and catalog anchor to the bucket (PUT), then restarts on
# the same data directory and finds its files (LIST) and reads its anchor back (GET). Run from the repository root after
# `dotnet build src/TerraBase -c Release`:
#
#   scripts/terrabase/object-store-proof.sh <env-file>
#
# The env file (mode 0600, never committed) sets AWS_ENDPOINT_URL_S3, AWS_REGION, BUCKET_NAME, AWS_ACCESS_KEY_ID and
# AWS_SECRET_ACCESS_KEY: the variables ts-orch delivers from OCI Vault. Each run generates its own master key, so name an
# empty test bucket. Prints PASS or FAIL lines only; no value from the env file is printed.
set -euo pipefail
envfile=${1:?env file}
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
data="$work/data"
port=$((20000 + RANDOM % 20000))
cleanup() {
  [[ -n "${node:-}" ]] && kill "$node" 2>/dev/null && wait "$node" 2>/dev/null
  rm -rf "$work"
}
trap cleanup EXIT

start() {
  (
    set -a
    # shellcheck disable=SC1090 # the operator's env file
    . "$envfile"
    set +a
    TERRABASE_DATA_DIRECTORY="$data" TERRABASE_PORT=$port \
      TERRABASE_INTERNAL_PORT=$((port + 1)) TERRABASE_METRICS_PORT=$((port + 2)) TERRABASE_POSTGRES_PORT=$((port + 3)) \
      exec dotnet "$root/src/TerraBase/bin/Release/net11.0/terrabase.dll"
  ) >"$work/$1.log" 2>&1 &
  node=$!
}

# /backupz answers 200 once every file this node ships reached the bucket.
shipping() {
  for _ in $(seq 1 90); do
    [[ $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/backupz") == 200 ]] && return 0
    kill -0 "$node" 2>/dev/null || return 1
    sleep 1
  done
  return 1
}

check() { if "${@:2}"; then echo "PASS $1"; else echo "FAIL $1"; status=1; fi; }
status=0
mkdir -p "$data"
# The master key wraps the data keys, so both runs use one key.
key=$(head -c 32 /dev/urandom | base64)
export TERRABASE_MASTER_KEY=$key
start first
check "first start ships snapshots, log segments, keys and anchor (backupz 200)" shipping
check "first start names shipped files" grep -q 'This member now ships' "$work/first.log"
kill "$node"; wait "$node" 2>/dev/null || true
start second
check "restart finds its files in the bucket (backupz 200)" shipping
check "restart reads its catalog anchor back" bash -c "! grep -q 'No backup bucket holds this machine' '$work/second.log'"
check "restart verifies its catalog" grep -q 'The catalog is verified' "$work/second.log"
exit "$status"
