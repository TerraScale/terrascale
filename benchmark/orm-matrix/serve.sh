#!/usr/bin/env bash
set -euo pipefail

usage() { echo "usage: serve.sh terrabase|turso-postgres <client command...>" >&2; exit 2; }
[ $# -ge 2 ] || usage
surface=$1
shift
case "$surface" in terrabase|turso-postgres) ;; *) usage ;; esac

repo=$(cd "$(dirname "$0")/../.." && pwd)
turso_commit=5c168be5ef6a32ac966daf5983502a4f694b37ad
cache=${XDG_CACHE_HOME:-$HOME/.cache}/terrascale-orm-matrix
work=$(mktemp -d "${TMPDIR:-/tmp}/orm-matrix-XXXXXX")
server=''
umask 077

cleanup() {
  if [ -n "$server" ]; then
    kill "$server" 2>/dev/null || true
    wait "$server" 2>/dev/null || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

free_port() {
  python3 -I -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'
}

fail() {
  echo "serve.sh: $1" >&2
  [ -f "$work/server.log" ] && tail -n 40 "$work/server.log" >&2
  exit 1
}

wait_tcp() {
  for _ in $(seq 1 600); do
    kill -0 "$server" 2>/dev/null || fail "server exited before port $1 opened"
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && return 0
    sleep 0.2
  done
  fail "port $1 did not open"
}

ulid() {
  python3 -I -c 'import os, time
n = (int(time.time() * 1000) << 80) | int.from_bytes(os.urandom(10), "big")
print("".join("0123456789ABCDEFGHJKMNPQRSTVWXYZ"[(n >> (5 * i)) & 31] for i in range(25, -1, -1)))'
}

turso_postgres() {
  local root=$cache/turso-$turso_commit
  local binary=$root/src/target/release/tursopg
  mkdir -p "$root"
  (
    flock -x 9
    if [ ! -x "$binary" ]; then
      [ -d "$root/src/.git" ] || git init -q "$root/src"
      git -C "$root/src" fetch -q --depth 1 https://github.com/tursodatabase/turso.git "$turso_commit"
      git -C "$root/src" checkout -q -f FETCH_HEAD
      [ "$(git -C "$root/src" rev-parse HEAD)" = "$turso_commit" ] || fail "upstream checkout is not $turso_commit"
      local nixpkgs libclang
      nixpkgs=$(jq -er '.nodes.nixpkgs.locked.rev' "$root/src/flake.lock")
      libclang=$(nix build --no-link --print-out-paths "github:NixOS/nixpkgs/$nixpkgs#llvmPackages.libclang.lib")
      (cd "$root/src" && nix develop "path:$root/src" --command env LIBCLANG_PATH="$libclang/lib" cargo build --release --locked -p tursopg) \
        >"$work/build.log" 2>&1 \
        || { tail -n 60 "$work/build.log" >&2; fail "tursopg build failed"; }
    fi
  ) 9>"$root/build.lock"
  [ "$(git -C "$root/src" rev-parse HEAD)" = "$turso_commit" ] || fail "cached upstream checkout has the wrong commit"
  git -C "$root/src" diff --quiet || fail "cached upstream tracked sources are modified"
  local port
  port=$(free_port)
  "$binary" --quiet "$work/matrix.db" --server "127.0.0.1:$port" >"$work/server.log" 2>&1 &
  server=$!
  wait_tcp "$port"
  database_url="postgresql://turso@127.0.0.1:$port/postgres?sslmode=disable"
}

terrabase() {
  [ -f "$repo/src/vendor/Turso.Data.Sqlite/native/runtimes/linux-x64/native/libturso_sdk_kit.so" ] \
    || "$repo/scripts/vendor/download-turso-native.sh" >"$work/native.log" 2>&1 || { cat "$work/native.log" >&2; fail "native download failed"; }
  dotnet build "$repo/src/TerraBase/TerraBase.csproj" -c Release -nologo -v quiet >"$work/build.log" 2>&1 \
    || { tail -n 60 "$work/build.log" >&2; fail "TerraBase build failed"; }
  local dll=$repo/src/TerraBase/bin/Release/net11.0/terrabase.dll
  [ -f "$dll" ] || fail "missing $dll"
  local port internal metrics postgres peer
  port=$(free_port)
  internal=$(free_port)
  metrics=$(free_port)
  postgres=$(free_port)
  peer=$(python3 -I -c 'import secrets; print(secrets.token_hex(32))')
  printf 'X-TerraBase-Peer-Credential: %s\n' "$peer" >"$work/peer.headers"
  (
    for name in $(compgen -e); do
      case "$name" in TS_*|TERRABASE_*|AWS_*|PRIMARY_REGION|BUCKET_NAME) unset "$name" ;; esac
    done
    TERRABASE_MASTER_KEY=$(python3 -I -c 'import base64, hashlib
print(base64.b64encode(hashlib.sha256(b"TerraBase integration fixtures only; never a production master").digest()).decode())')
    TERRABASE_PEER_SECRET=$peer
    export TERRABASE_MASTER_KEY TERRABASE_PEER_SECRET ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
    exec dotnet "$dll" --data-directory="$work/data" --port="$port" --internal-port="$internal" --metrics-port="$metrics" \
      --postgres-port="$postgres" --wire-port=0 --storage-threads=2
  ) >"$work/server.log" 2>&1 &
  server=$!
  for _ in $(seq 1 600); do
    kill -0 "$server" 2>/dev/null || fail "TerraBase exited during startup"
    curl -fsS -o /dev/null "http://127.0.0.1:$port/healthz" 2>/dev/null && break
    sleep 0.2
  done
  curl -fsS -o /dev/null "http://127.0.0.1:$port/healthz" || fail "TerraBase did not answer /healthz"
  local owner organization project
  local local_management="http://127.0.0.1:$internal/v1/management"
  owner=$(ulid)
  jq -n --arg name "orm-matrix-$(ulid)" '{name: $name}' >"$work/organization.json"
  organization=$(curl -fsS --http2-prior-knowledge -H @"$work/peer.headers" -H "X-TerraBase-User-Id: $owner" \
    -H 'Content-Type: application/json' --data-binary @"$work/organization.json" "$local_management/organizations" | jq -er '.id') \
    || fail "organization creation failed"
  curl -fsS --http2-prior-knowledge -o /dev/null -X PUT -H @"$work/peer.headers" -H "X-TerraBase-User-Id: $owner" \
    -H 'Content-Type: application/json' --data '{"plan":"enterprise"}' "$local_management/organizations/$organization/plan" \
    || fail "plan update failed"
  project=$(curl -fsS --http2-prior-knowledge -H @"$work/peer.headers" -H "X-TerraBase-User-Id: $owner" \
    -H 'Content-Type: application/json' --data '{"name":"orm-matrix"}' "$local_management/organizations/$organization/projects" | jq -er '.id') \
    || fail "project creation failed"
  curl -fsS --http2-prior-knowledge -o /dev/null -H @"$work/peer.headers" -H "X-TerraBase-User-Id: $owner" \
    -H 'Content-Type: application/json' --data '{"name":"matrix"}' "$local_management/organizations/$organization/projects/$project/databases" \
    || fail "database creation failed"
  jq -n --arg organization "$organization" --arg project "$project" \
    '{name: "orm-matrix", scopes: ["documents:read", "documents:write"], organizationId: $organization, projectId: $project, databaseId: null}' \
    >"$work/key.json"
  local key
  key=$(curl -fsS --http2-prior-knowledge -H @"$work/peer.headers" -H 'Content-Type: application/json' \
    --data-binary @"$work/key.json" "http://127.0.0.1:$internal/admin/api-keys" | jq -er '.secret | @uri') || fail "API key issue failed"
  wait_tcp "$postgres"
  database_url="postgresql://wire:$key@127.0.0.1:$postgres/matrix?sslmode=disable"
}

"${surface//-/_}"
status=0
MATRIX_SURFACE=$surface DATABASE_URL=$database_url "$@" || status=$?
kill -0 "$server" 2>/dev/null || fail "server exited while the client ran"
exit "$status"
